using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using ValleytalkReborn;
using Xunit;

namespace ValleytalkReborn.Tests
{
    /// <summary>
    /// DD404B-CONDENSATION-COMMIT：自动周/季/年浓缩的两阶段保源提交。
    /// 覆盖：归档阶段写失败零活跃写入、活跃阶段写失败保留全部源（archivePrepared）且重试不重复归档、
    /// 成功 payload 同步新增聚合并剔除捕获源、生成期间新增源保留、hash/缺失/tier/日期冲突零写入、
    /// 同周期（PeriodAlreadyCovered 周期定义）与 ID 冲突、容量、重复正文、归档副本不污染原条目、
    /// 归档去重与 30 条滚动、SmartTruncate 正式待写值。
    /// 受控 IDataHelper 双 key 注入；SMAPI 真实写失败由 Supervisor 实测。
    /// </summary>
    [Collection("StaticGlobalStateCollection")]
    public class TimelineCondensationCommitTests
    {
        private const string ActiveKey = "valleytalk.npc-timeline-memories";
        private const string ArchiveKey = "valleytalk.npc-timeline-archived-memories";
        private const string CategoryFlagKey = "valleytalk.memory-category-migrated";
        private const string Npc = "Abigail";

        private const int WeeklyTargetDay = 14;     // Y1 春 14（季内第 2 周）
        private const int ChronicleTargetDay = 20;  // Y1 春 20
        private const int YearlyTargetDay = 100;    // Y1 冬 16

        static TimelineCondensationCommitTests()
        {
            TestEnvironment.InstallHeadlessContext();
        }

        // ── 受控 IDataHelper：内存记录 + 按 key 注入写失败 ──

        private sealed class ControlledDataHelper : IDataHelper
        {
            public readonly Dictionary<string, object> SaveData = new();
            public bool ThrowOnRead;
            public string ThrowOnWriteKey;
            public int WriteCallCount;

            public TModel ReadSaveData<TModel>(string key) where TModel : class
            {
                if (ThrowOnRead) throw new InvalidOperationException("controlled read failure");
                return SaveData.TryGetValue(key, out var value) ? (TModel)value : null;
            }

            public void WriteSaveData<TModel>(string key, TModel data) where TModel : class
            {
                WriteCallCount++;
                if (ThrowOnWriteKey == key) throw new InvalidOperationException($"controlled write failure on {key}");
                SaveData[key] = data;
            }

            public TModel ReadJsonFile<TModel>(string path) where TModel : class => throw new NotImplementedException();
            public void WriteJsonFile<TModel>(string path, TModel data) where TModel : class => throw new NotImplementedException();
            public TModel ReadGlobalData<TModel>(string key) where TModel : class => throw new NotImplementedException();
            public void WriteGlobalData<TModel>(string key, TModel data) where TModel : class => throw new NotImplementedException();
        }

        /// <summary>接口事件/属性的静默桩：事件订阅（add/remove）为无操作，接口属性返回嵌套静默桩。</summary>
        private class NoopInterfaceProxy : DispatchProxy
        {
            protected override object Invoke(MethodInfo targetMethod, object[] args)
            {
                if (targetMethod.IsSpecialName && targetMethod.Name.StartsWith("get_", StringComparison.Ordinal)
                    && targetMethod.ReturnType.IsInterface)
                {
                    return CreateNoop(targetMethod.ReturnType);
                }

                return null;
            }

            public static object CreateNoop(Type interfaceType)
            {
                var create = typeof(DispatchProxy)
                    .GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .First(m => m.IsGenericMethodDefinition
                                && m.GetGenericArguments().Length == 2
                                && m.GetParameters().Length == 0)
                    .MakeGenericMethod(interfaceType, typeof(NoopInterfaceProxy));
                return create.Invoke(null, Array.Empty<object>());
            }
        }

        private sealed class ControlledModHelper : IModHelper
        {
            private readonly IModEvents _events = (IModEvents)NoopInterfaceProxy.CreateNoop(typeof(IModEvents));
            private readonly ITranslationHelper _translation = new FakeTranslationHelper("en");

            public ControlledModHelper(IDataHelper data) { Data = data; }

            public string DirectoryPath => ".";
            public IModEvents Events => _events;
            public ICommandHelper ConsoleCommands => throw new NotImplementedException();
            public IGameContentHelper GameContent => throw new NotImplementedException();
            public IModContentHelper ModContent => throw new NotImplementedException();
            public IContentPackHelper ContentPacks => throw new NotImplementedException();
            public IDataHelper Data { get; }
            public IInputHelper Input => throw new NotImplementedException();
            public IReflectionHelper Reflection => throw new NotImplementedException();
            public IModRegistry ModRegistry => throw new NotImplementedException();
            public IMultiplayerHelper Multiplayer => throw new NotImplementedException();
            public ITranslationHelper Translation => _translation;
            public TConfig ReadConfig<TConfig>() where TConfig : class, new() => throw new NotImplementedException();
            public void WriteConfig<TConfig>(TConfig config) where TConfig : class, new() => throw new NotImplementedException();
        }

        // ── 受控对象构造 ──

        private static string H(string text) => DailyDistillationSnapshotBuilder.HashText(text);

        private static MemoryEntry Entry(string id, string content, MemoryTier tier, int createdDay)
            => new MemoryEntry
            {
                Id = id,
                NpcName = Npc,
                Content = content,
                CreatedAt = new DateTime(2026, 1, 1, 12, 0, 0),
                CreatedDay = createdDay,
                Source = "Timeline",
                Category = MemoryCategory.Fact,
                Type = MemoryType.Fact,
                Tier = tier,
                DateLabel = "seeded label",
                Importance = tier == MemoryTier.Daily ? 3 : 4
            };

        private static TimelineCondensationCommitRequest WeeklyRequest(
            string entryId = "W1",
            string content = "weekly summary",
            int[] sourceDays = null,
            string[] sourceIds = null,
            string[] sourceHashes = null,
            int? targetDay = null)
        {
            int[] days = sourceDays ?? new[] { 8, 13 };
            string[] ids = sourceIds ?? days.Select((_, i) => $"S{i + 1}").ToArray();
            string[] hashes = sourceHashes ?? ids.Select((_, i) => H($"source {i + 1} content")).ToArray();
            return new TimelineCondensationCommitRequest
            {
                NpcName = Npc,
                TargetDay = targetDay ?? WeeklyTargetDay,
                TargetTier = MemoryTier.Weekly,
                EntryId = entryId,
                NewContent = content,
                SourceEntryIds = ids,
                SourceContentHashes = hashes
            };
        }

        /// <summary>构造与 WeeklyRequest 默认源匹配的活跃列表（S1/S2 在窗口 [8,14] 内）。</summary>
        private static List<MemoryEntry> DefaultWeeklySources()
            => new List<MemoryEntry>
            {
                Entry("S1", "source 1 content", MemoryTier.Daily, 8),
                Entry("S2", "source 2 content", MemoryTier.Daily, 13)
            };

        private ControlledDataHelper SetupLoaded(
            Dictionary<string, List<MemoryEntry>> activeSeed = null,
            Dictionary<string, List<MemoryEntry>> archiveSeed = null,
            bool failLoad = false)
        {
            var data = new ControlledDataHelper { ThrowOnRead = failLoad };
            if (activeSeed != null) data.SaveData[ActiveKey] = activeSeed;
            if (archiveSeed != null) data.SaveData[ArchiveKey] = archiveSeed;
            data.SaveData[CategoryFlagKey] = "true";
            TestEnv.SetSHelper(new ControlledModHelper(data));
            TestEnvironment.WithWorldReady(true, () => MemoryManager.Instance.Load());
            return data;
        }

        private void Teardown()
        {
            MemoryManager.Instance.Cleanup();
            TestEnv.SetSHelper(null);
        }

        private static TimelineCondensationCommitResult Commit(TimelineCondensationCommitRequest request)
        {
            TimelineCondensationCommitResult result = null;
            TestEnvironment.WithWorldReady(true, () =>
                result = MemoryManager.Instance.CommitTimelineCondensation(request));
            return result;
        }

        private static List<MemoryEntry> ActiveOf(string npc = Npc)
            => MemoryManager.Instance.GetTimelineMemories(npc, MemoryTier.Daily)
                .Concat(MemoryManager.Instance.GetTimelineMemories(npc, MemoryTier.Weekly))
                .Concat(MemoryManager.Instance.GetTimelineMemories(npc, MemoryTier.Chronicle))
                .Concat(MemoryManager.Instance.GetTimelineMemories(npc, MemoryTier.Yearly))
                .ToList();

        // ── Invalid：参数与结构完整性 ──

        [Fact]
        public void Commit_InvalidRequest_ReturnsInvalidWithZeroWrites()
        {
            try
            {
                var data = SetupLoaded();

                Assert.Equal(TimelineCondensationCommitStatus.Invalid, Commit(null).Status);
                Assert.Equal(TimelineCondensationCommitStatus.Invalid, Commit(WeeklyRequest(content: null)).Status);
                Assert.Equal(TimelineCondensationCommitStatus.Invalid, Commit(WeeklyRequest(entryId: null)).Status);
                Assert.Equal(TimelineCondensationCommitStatus.Invalid, Commit(WeeklyRequest(targetDay: 0)).Status);

                // Daily 不是升档目标层
                var dailyTarget = WeeklyRequest();
                dailyTarget = new TimelineCondensationCommitRequest
                {
                    NpcName = Npc, TargetDay = WeeklyTargetDay, TargetTier = MemoryTier.Daily,
                    EntryId = "W1", NewContent = "weekly summary",
                    SourceEntryIds = new[] { "S1", "S2" }, SourceContentHashes = new[] { H("a"), H("b") }
                };
                Assert.Equal(TimelineCondensationCommitStatus.Invalid, Commit(dailyTarget).Status);

                // 源列表：不足 2 条 / 等长破坏 / ID 重复
                var single = WeeklyRequest();
                single = new TimelineCondensationCommitRequest
                {
                    NpcName = Npc, TargetDay = WeeklyTargetDay, TargetTier = MemoryTier.Weekly,
                    EntryId = "W1", NewContent = "weekly summary",
                    SourceEntryIds = new[] { "S1" }, SourceContentHashes = new[] { H("a") }
                };
                Assert.Equal(TimelineCondensationCommitStatus.Invalid, Commit(single).Status);

                var mismatched = WeeklyRequest();
                mismatched = new TimelineCondensationCommitRequest
                {
                    NpcName = Npc, TargetDay = WeeklyTargetDay, TargetTier = MemoryTier.Weekly,
                    EntryId = "W1", NewContent = "weekly summary",
                    SourceEntryIds = new[] { "S1", "S2" }, SourceContentHashes = new[] { H("a") }
                };
                Assert.Equal(TimelineCondensationCommitStatus.Invalid, Commit(mismatched).Status);

                var duplicated = WeeklyRequest();
                duplicated = new TimelineCondensationCommitRequest
                {
                    NpcName = Npc, TargetDay = WeeklyTargetDay, TargetTier = MemoryTier.Weekly,
                    EntryId = "W1", NewContent = "weekly summary",
                    SourceEntryIds = new[] { "S1", "S1" }, SourceContentHashes = new[] { H("a"), H("b") }
                };
                Assert.Equal(TimelineCondensationCommitStatus.Invalid, Commit(duplicated).Status);

                Assert.Equal(0, data.WriteCallCount); // 全部零写入
            }
            finally { Teardown(); }
        }

        // ── Unavailable ──

        [Fact]
        public void Commit_NotLoadedOrWorldNotReadyOrLoadFailed_ReturnsUnavailable()
        {
            try
            {
                // 未加载
                var data = new ControlledDataHelper();
                TestEnv.SetSHelper(new ControlledModHelper(data));
                Assert.Equal(TimelineCondensationCommitStatus.Unavailable, Commit(WeeklyRequest()).Status);

                // 世界未就绪
                var loaded = SetupLoaded();
                int writesBefore = loaded.WriteCallCount;
                TimelineCondensationCommitResult result = null;
                TestEnvironment.WithoutWorldReady(() =>
                    result = MemoryManager.Instance.CommitTimelineCondensation(WeeklyRequest()));
                Assert.Equal(TimelineCondensationCommitStatus.Unavailable, result.Status);
                Assert.Equal(writesBefore, loaded.WriteCallCount);

                // 加载失败（Error 路径）
                SetupLoaded(failLoad: true);
                var failed = Commit(WeeklyRequest());
                Assert.Equal(TimelineCondensationCommitStatus.Unavailable, failed.Status);
                Assert.Contains("load", failed.ErrorDetail, StringComparison.OrdinalIgnoreCase);
            }
            finally { Teardown(); }
        }

        // ── Applied：成功两阶段提交 ──

        [Fact]
        public void Commit_WeeklySuccess_AppliesAggregateRemovesSourcesKeepsOthersAndArchives()
        {
            try
            {
                var active = DefaultWeeklySources();
                var bystander = Entry("KEEP", "unrelated daily card", MemoryTier.Daily, 9);
                active.Add(bystander);
                var source1 = active[0];
                var data = SetupLoaded(activeSeed: new Dictionary<string, List<MemoryEntry>> { [Npc] = active });

                var result = Commit(WeeklyRequest(entryId: "W1", content: "weekly summary"));

                Assert.Equal(TimelineCondensationCommitStatus.Applied, result.Status);
                Assert.Equal("W1", result.EntryId);
                Assert.Equal(2, data.WriteCallCount); // 归档一次 + 活跃一次

                // 活跃库：新增聚合、剔除捕获源、保留未捕获源
                var all = ActiveOf();
                var aggregate = all.Single(m => m.Id == "W1");
                Assert.Equal(MemoryTier.Weekly, aggregate.Tier);
                Assert.Equal(WeeklyTargetDay, aggregate.CreatedDay);
                Assert.Equal("weekly summary", aggregate.Content);
                Assert.Equal("Timeline", aggregate.Source);
                Assert.Equal(MemoryCategory.Fact, aggregate.Category);
                Assert.Equal(MemoryType.Fact, aggregate.Type);
                Assert.Equal(4, aggregate.Importance); // Weekly → 4
                Assert.False(string.IsNullOrWhiteSpace(aggregate.DateLabel));
                Assert.Null(all.FirstOrDefault(m => m.Id == "S1" || m.Id == "S2")); // 捕获源被移除
                Assert.NotNull(all.FirstOrDefault(m => m.Id == "KEEP"));            // 新增/无关源保留

                // 活跃 payload 同步：新增聚合并移除源（同一 SaveData payload）
                var activePayload = (Dictionary<string, List<MemoryEntry>>)data.SaveData[ActiveKey];
                Assert.Contains(activePayload[Npc], m => m.Id == "W1");
                Assert.DoesNotContain(activePayload[Npc], m => m.Id == "S1" || m.Id == "S2");

                // 归档库：完整属性副本 + ArchivedAt/ArchiveReason；原条目未被就地修改
                var archived = MemoryManager.Instance.GetArchivedTimelineMemories(Npc);
                Assert.Equal(2, archived.Count);
                var archivedCopy = archived.Single(m => m.Id == "S1");
                Assert.Equal("source 1 content", archivedCopy.Content);
                Assert.Equal(MemoryTier.Daily, archivedCopy.Tier);
                Assert.Equal("Distilled", archivedCopy.ArchiveReason);
                Assert.NotEqual(default, archivedCopy.ArchivedAt);
                Assert.Equal(archived.Single(m => m.Id == "S2").ArchivedAt, archivedCopy.ArchivedAt); // 同一个 ArchivedAt
                Assert.Equal(default, source1.ArchivedAt);   // 原对象未被污染
                Assert.Equal("", source1.ArchiveReason);
                Assert.Equal("source 1 content", source1.Content);
                Assert.Equal(default, bystander.ArchivedAt); // 未捕获条目不进归档

                // 归档 payload 同步写入
                var archivePayload = (Dictionary<string, List<MemoryEntry>>)data.SaveData[ArchiveKey];
                Assert.Equal(2, archivePayload[Npc].Count);
            }
            finally { Teardown(); }
        }

        [Fact]
        public void Commit_ReplayAfterSuccess_IsUnchangedWithoutNewWrites()
        {
            try
            {
                SetupLoaded(activeSeed: new Dictionary<string, List<MemoryEntry>> { [Npc] = DefaultWeeklySources() });
                Assert.Equal(TimelineCondensationCommitStatus.Applied, Commit(WeeklyRequest()).Status);
                var data = TestEnv.GetSHelper() as ControlledModHelper;
                // 写入计数通过重新读取 SaveData 判定：记录当前 payload 引用
                var archivePayloadBefore = (Dictionary<string, List<MemoryEntry>>)((ControlledDataHelper)data.Data).SaveData[ArchiveKey];

                var replay = Commit(WeeklyRequest());

                Assert.Equal(TimelineCondensationCommitStatus.Unchanged, replay.Status);
                var archivePayloadAfter = (Dictionary<string, List<MemoryEntry>>)((ControlledDataHelper)data.Data).SaveData[ArchiveKey];
                Assert.Same(archivePayloadBefore, archivePayloadAfter); // 重放零写入

                // 无重复聚合/归档
                Assert.Equal(1, ActiveOf().Count(m => m.Id == "W1"));
                Assert.Equal(2, MemoryManager.Instance.GetArchivedTimelineMemories(Npc).Count);
            }
            finally { Teardown(); }
        }

        // ── StorageFailed：两阶段失败语义 ──

        [Fact]
        public void Commit_ArchivePhaseFailure_ZeroActiveWrites()
        {
            try
            {
                var active = DefaultWeeklySources();
                var seed = new Dictionary<string, List<MemoryEntry>> { [Npc] = active };
                var data = SetupLoaded(activeSeed: seed);
                int writesBefore = data.WriteCallCount;
                data.ThrowOnWriteKey = ArchiveKey;

                var result = Commit(WeeklyRequest());

                Assert.Equal(TimelineCondensationCommitStatus.StorageFailed, result.Status);
                Assert.Contains("archive", result.ErrorDetail, StringComparison.OrdinalIgnoreCase);
                Assert.Equal(writesBefore + 1, data.WriteCallCount);          // 仅阶段一一次尝试
                Assert.False(data.SaveData.ContainsKey(ArchiveKey));          // 归档库未写入
                Assert.Same(seed, data.SaveData[ActiveKey]);                  // 活跃库仍是装载种子，零活跃写入
                Assert.Equal(2, ActiveOf().Count(m => m.Tier == MemoryTier.Daily)); // 源完整保留
                Assert.Empty(ActiveOf().Where(m => m.Id == "W1"));            // 无聚合
                Assert.Empty(MemoryManager.Instance.GetArchivedTimelineMemories(Npc));
            }
            finally { Teardown(); }
        }

        [Fact]
        public void Commit_ActivePhaseFailure_PreservesSourcesAndPreparesArchive_RetrySucceedsWithoutDuplication()
        {
            try
            {
                var active = DefaultWeeklySources();
                var seed = new Dictionary<string, List<MemoryEntry>> { [Npc] = active };
                var data = SetupLoaded(activeSeed: seed);
                int writesBefore = data.WriteCallCount;
                data.ThrowOnWriteKey = ActiveKey;

                var failed = Commit(WeeklyRequest());

                Assert.Equal(TimelineCondensationCommitStatus.StorageFailed, failed.Status);
                Assert.Contains("archivePrepared=true", failed.ErrorDetail);
                Assert.Equal(writesBefore + 2, data.WriteCallCount);          // 两阶段各尝试一次
                Assert.Same(seed, data.SaveData[ActiveKey]);                  // 活跃库 payload 未被替换
                // 归档副本已作为安全备份存在（阶段一成功并发布）
                Assert.Equal(2, MemoryManager.Instance.GetArchivedTimelineMemories(Npc).Count);
                // 活跃源完整保留，未丢源
                Assert.NotNull(ActiveOf().FirstOrDefault(m => m.Id == "S1"));
                Assert.NotNull(ActiveOf().FirstOrDefault(m => m.Id == "S2"));
                Assert.Empty(ActiveOf().Where(m => m.Id == "W1"));

                // 重试（同一请求）：不重复归档，活跃提交成功
                data.ThrowOnWriteKey = null;
                var retry = Commit(WeeklyRequest());
                Assert.Equal(TimelineCondensationCommitStatus.Applied, retry.Status);
                Assert.Equal(2, MemoryManager.Instance.GetArchivedTimelineMemories(Npc).Count); // 仍各一份
                var all = ActiveOf();
                Assert.NotNull(all.FirstOrDefault(m => m.Id == "W1"));
                Assert.Null(all.FirstOrDefault(m => m.Id == "S1" || m.Id == "S2"));
            }
            finally { Teardown(); }
        }

        // ── Conflict：源核对与周期/ID 冲突（全部零写入）──

        [Fact]
        public void Commit_SourceHashMismatch_ReturnsConflictWithZeroWrites()
        {
            try
            {
                var data = SetupLoaded(activeSeed: new Dictionary<string, List<MemoryEntry>> { [Npc] = DefaultWeeklySources() });
                int writesBefore = data.WriteCallCount;

                var request = WeeklyRequest(sourceHashes: new[] { H("source 1 content"), H("wrong hash") });
                var result = Commit(request);

                Assert.Equal(TimelineCondensationCommitStatus.Conflict, result.Status);
                Assert.Contains("hash mismatch", result.ErrorDetail);
                Assert.Equal(writesBefore, data.WriteCallCount);
            }
            finally { Teardown(); }
        }

        [Fact]
        public void Commit_SourceMissing_ReturnsConflictWithZeroWrites()
        {
            try
            {
                var partial = new List<MemoryEntry> { Entry("S1", "source 1 content", MemoryTier.Daily, 8) };
                var data = SetupLoaded(activeSeed: new Dictionary<string, List<MemoryEntry>> { [Npc] = partial });
                int writesBefore = data.WriteCallCount;

                var result = Commit(WeeklyRequest());

                Assert.Equal(TimelineCondensationCommitStatus.Conflict, result.Status);
                Assert.Contains("missing", result.ErrorDetail);
                Assert.Equal(writesBefore, data.WriteCallCount);
            }
            finally { Teardown(); }
        }

        [Fact]
        public void Commit_SourceTierMismatch_ReturnsConflict()
        {
            try
            {
                // 目标 Weekly 期望 Daily 源；S1 被降级/污染为 Weekly tier
                var wrongTier = new List<MemoryEntry>
                {
                    Entry("S1", "source 1 content", MemoryTier.Weekly, 8),
                    Entry("S2", "source 2 content", MemoryTier.Daily, 13)
                };
                SetupLoaded(activeSeed: new Dictionary<string, List<MemoryEntry>> { [Npc] = wrongTier });

                var result = Commit(WeeklyRequest());

                Assert.Equal(TimelineCondensationCommitStatus.Conflict, result.Status);
                Assert.Contains("tier", result.ErrorDetail);
            }
            finally { Teardown(); }
        }

        [Fact]
        public void Commit_SourceOutsideTargetPeriod_ReturnsConflict()
        {
            try
            {
                // Weekly：源早于 TargetDay-6（第 1 周第 7 天）
                var early = new List<MemoryEntry>
                {
                    Entry("S1", "source 1 content", MemoryTier.Daily, 7),
                    Entry("S2", "source 2 content", MemoryTier.Daily, 13)
                };
                SetupLoaded(activeSeed: new Dictionary<string, List<MemoryEntry>> { [Npc] = early });
                var weekly = Commit(WeeklyRequest());
                Assert.Equal(TimelineCondensationCommitStatus.Conflict, weekly.Status);
                Assert.Contains("period", weekly.ErrorDetail);

                // Chronicle：源在下一季（Y1 夏 1 = 29）
                var summerSources = new List<MemoryEntry>
                {
                    Entry("S1", "source 1 content", MemoryTier.Weekly, 15),
                    Entry("S2", "source 2 content", MemoryTier.Weekly, 29)
                };
                SetupLoaded(activeSeed: new Dictionary<string, List<MemoryEntry>> { [Npc] = summerSources });
                var chronicleRequest = new TimelineCondensationCommitRequest
                {
                    NpcName = Npc, TargetDay = ChronicleTargetDay, TargetTier = MemoryTier.Chronicle,
                    EntryId = "C1", NewContent = "season summary",
                    SourceEntryIds = new[] { "S1", "S2" },
                    SourceContentHashes = new[] { H("source 1 content"), H("source 2 content") }
                };
                var chronicle = Commit(chronicleRequest);
                Assert.Equal(TimelineCondensationCommitStatus.Conflict, chronicle.Status);

                // Yearly：源在次年（Y2 春 1 = 113）
                var nextYearSources = new List<MemoryEntry>
                {
                    Entry("S1", "source 1 content", MemoryTier.Chronicle, 50),
                    Entry("S2", "source 2 content", MemoryTier.Chronicle, 113)
                };
                SetupLoaded(activeSeed: new Dictionary<string, List<MemoryEntry>> { [Npc] = nextYearSources });
                var yearlyRequest = new TimelineCondensationCommitRequest
                {
                    NpcName = Npc, TargetDay = YearlyTargetDay, TargetTier = MemoryTier.Yearly,
                    EntryId = "Y1", NewContent = "yearly summary",
                    SourceEntryIds = new[] { "S1", "S2" },
                    SourceContentHashes = new[] { H("source 1 content"), H("source 2 content") }
                };
                var yearly = Commit(yearlyRequest);
                Assert.Equal(TimelineCondensationCommitStatus.Conflict, yearly.Status);
            }
            finally { Teardown(); }
        }

        [Fact]
        public void Commit_PeriodAlreadyCovered_ReturnsConflict()
        {
            try
            {
                // 既有 Weekly 聚合落在同一季内周桶（(DayOfMonth-1)/7 相同）：day 10 与 day 14 同属第 2 周
                var covered = DefaultWeeklySources();
                covered.Add(Entry("OLD-W", "earlier weekly summary", MemoryTier.Weekly, 10));
                SetupLoaded(activeSeed: new Dictionary<string, List<MemoryEntry>> { [Npc] = covered });

                var result = Commit(WeeklyRequest());

                Assert.Equal(TimelineCondensationCommitStatus.Conflict, result.Status);
                Assert.Contains("period already covered", result.ErrorDetail);
            }
            finally { Teardown(); }
        }

        [Fact]
        public void Commit_EntryIdReused_ReturnsConflict()
        {
            try
            {
                // EntryId 被不同周期的既有条目占用（不构成重放）
                var seeded = DefaultWeeklySources();
                seeded.Add(Entry("W1", "unrelated other-week summary", MemoryTier.Weekly, 100));
                SetupLoaded(activeSeed: new Dictionary<string, List<MemoryEntry>> { [Npc] = seeded });

                var result = Commit(WeeklyRequest());

                Assert.Equal(TimelineCondensationCommitStatus.Conflict, result.Status);
                Assert.Contains("already used", result.ErrorDetail);
            }
            finally { Teardown(); }
        }

        // ── CapacityFull / Duplicate ──

        [Fact]
        public void Commit_WeeklyCapacityFull_ReturnsCapacityFull()
        {
            try
            {
                // 既有 10 条 Weekly（不同周桶/年份，避开同周期检查）
                var seeded = DefaultWeeklySources();
                for (int i = 0; i < MemoryManager.GetTierCapacity(MemoryTier.Weekly); i++)
                    seeded.Add(Entry($"OLDW{i}", $"old weekly {i}", MemoryTier.Weekly, 100 + i));
                SetupLoaded(activeSeed: new Dictionary<string, List<MemoryEntry>> { [Npc] = seeded });

                var result = Commit(WeeklyRequest());

                Assert.Equal(TimelineCondensationCommitStatus.CapacityFull, result.Status);
            }
            finally { Teardown(); }
        }

        [Fact]
        public void Commit_YearlyCapacityFull_ReturnsCapacityFull()
        {
            try
            {
                var seeded = new List<MemoryEntry>
                {
                    Entry("S1", "source 1 content", MemoryTier.Chronicle, 50),
                    Entry("S2", "source 2 content", MemoryTier.Chronicle, 60)
                };
                // 既有 Yearly 落在次年（Y2），避开与目标年（Y1）的同周期检查，仅触发容量
                for (int i = 0; i < MemoryManager.GetTierCapacity(MemoryTier.Yearly); i++)
                    seeded.Add(Entry($"OLDY{i}", $"old yearly {i}", MemoryTier.Yearly, 113 + i));
                SetupLoaded(activeSeed: new Dictionary<string, List<MemoryEntry>> { [Npc] = seeded });

                var request = new TimelineCondensationCommitRequest
                {
                    NpcName = Npc, TargetDay = YearlyTargetDay, TargetTier = MemoryTier.Yearly,
                    EntryId = "Y1", NewContent = "yearly summary",
                    SourceEntryIds = new[] { "S1", "S2" },
                    SourceContentHashes = new[] { H("source 1 content"), H("source 2 content") }
                };

                var result = Commit(request);

                Assert.Equal(TimelineCondensationCommitStatus.CapacityFull, result.Status);
            }
            finally { Teardown(); }
        }

        [Fact]
        public void Commit_DuplicateWeeklyContent_ReturnsDuplicate()
        {
            try
            {
                var seeded = DefaultWeeklySources();
                seeded.Add(Entry("OLDW", "WEEKLY SUMMARY", MemoryTier.Weekly, 100)); // 忽略大小写重复
                SetupLoaded(activeSeed: new Dictionary<string, List<MemoryEntry>> { [Npc] = seeded });

                var result = Commit(WeeklyRequest(content: "weekly summary"));

                Assert.Equal(TimelineCondensationCommitStatus.Duplicate, result.Status);
            }
            finally { Teardown(); }
        }

        // ── 归档去重 / 冲突 / 30 条滚动 ──

        [Fact]
        public void Commit_PreparedArchiveSameIdSameContent_KeepsSingleCopy()
        {
            try
            {
                // 模拟 archivePrepared 重放：归档箱已有 S1 副本（同 ID 同正文）
                var prepared = Entry("S1", "source 1 content", MemoryTier.Daily, 8);
                prepared.ArchivedAt = new DateTime(2026, 1, 2, 0, 0, 0);
                prepared.ArchiveReason = "Distilled";
                SetupLoaded(
                    activeSeed: new Dictionary<string, List<MemoryEntry>> { [Npc] = DefaultWeeklySources() },
                    archiveSeed: new Dictionary<string, List<MemoryEntry>> { [Npc] = new List<MemoryEntry> { prepared } });

                var result = Commit(WeeklyRequest());

                Assert.Equal(TimelineCondensationCommitStatus.Applied, result.Status);
                var archived = MemoryManager.Instance.GetArchivedTimelineMemories(Npc);
                Assert.Equal(1, archived.Count(m => m.Id == "S1")); // 只保留一份
                Assert.Equal(2, archived.Count);                     // S2 正常补入
                Assert.Equal(new DateTime(2026, 1, 2, 0, 0, 0), archived.Single(m => m.Id == "S1").ArchivedAt); // 原副本时间戳未重置
            }
            finally { Teardown(); }
        }

        [Fact]
        public void Commit_ArchiveSameIdDifferentContent_ReturnsConflictWithZeroWrites()
        {
            try
            {
                var conflicting = Entry("S1", "archived with different content", MemoryTier.Daily, 8);
                var data = SetupLoaded(
                    activeSeed: new Dictionary<string, List<MemoryEntry>> { [Npc] = DefaultWeeklySources() },
                    archiveSeed: new Dictionary<string, List<MemoryEntry>> { [Npc] = new List<MemoryEntry> { conflicting } });
                int writesBefore = data.WriteCallCount;

                var result = Commit(WeeklyRequest());

                Assert.Equal(TimelineCondensationCommitStatus.Conflict, result.Status);
                Assert.Contains("different content", result.ErrorDetail);
                Assert.Equal(writesBefore, data.WriteCallCount); // 零写入
            }
            finally { Teardown(); }
        }

        [Fact]
        public void Commit_ArchiveRollingLimit_TrimsOldestToThirty()
        {
            try
            {
                var archived = Enumerable.Range(0, MemoryManager.MaxArchivedTimelineMemoriesPerNpc)
                    .Select(i => Entry($"OLD{i}", $"old archived {i}", MemoryTier.Daily, 5))
                    .ToList();
                SetupLoaded(
                    activeSeed: new Dictionary<string, List<MemoryEntry>> { [Npc] = DefaultWeeklySources() },
                    archiveSeed: new Dictionary<string, List<MemoryEntry>> { [Npc] = archived });

                var result = Commit(WeeklyRequest());

                Assert.Equal(TimelineCondensationCommitStatus.Applied, result.Status);
                var after = MemoryManager.Instance.GetArchivedTimelineMemories(Npc);
                Assert.Equal(MemoryManager.MaxArchivedTimelineMemoriesPerNpc, after.Count); // 30 条滚动上限
                Assert.NotNull(after.FirstOrDefault(m => m.Id == "S1"));                    // 新副本在列
                Assert.Null(after.FirstOrDefault(m => m.Id == "OLD29"));                    // 列表尾端最旧项被截去
                Assert.Null(after.FirstOrDefault(m => m.Id == "OLD28"));
            }
            finally { Teardown(); }
        }

        // ── SmartTruncate 正式待写值 ──

        [Fact]
        public void Commit_OverlengthContent_IsTruncatedBySmartTruncate()
        {
            try
            {
                SetupLoaded(activeSeed: new Dictionary<string, List<MemoryEntry>> { [Npc] = DefaultWeeklySources() });
                string longContent = new string('x', 60) + "。" + new string('y', 80);

                var result = Commit(WeeklyRequest(content: longContent));

                Assert.Equal(TimelineCondensationCommitStatus.Applied, result.Status);
                var aggregate = ActiveOf().Single(m => m.Id == "W1");
                string expected = MemoryManager.SmartTruncate(longContent.Trim(), MemoryManager.MaxMemoryLength);
                Assert.Equal(expected, aggregate.Content);
                Assert.True(aggregate.Content.Length <= MemoryManager.MaxMemoryLength);
            }
            finally { Teardown(); }
        }
    }
}
