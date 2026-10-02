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
    /// DD404-DAILY-COMMIT：Daily 专用比较提交（CAS）。
    /// 覆盖：参数非法 Invalid、前置未就绪/加载失败 Unavailable、重放 Unchanged（优先于容量/重复/旧hash）、
    /// 创建 Applied（沿用现有 Daily 建立规则）、更新 Applied（保留 Id/CreatedDay/CreatedAt/DateLabel）、
    /// 旧 hash 不匹配 Conflict、同日多卡片 Conflict、ID 已占用 Conflict、满 30 条 CapacityFull、
    /// 正文重复 Duplicate、底层写入异常 StorageFailed 时内存与 SaveData 均保持旧值、副本与发布列表不共享引用。
    /// 受控 IDataHelper 直接记录/抛出，不扩展生产公共 API；SMAPI 真实写失败由 Supervisor 实测。
    /// </summary>
    [Collection("StaticGlobalStateCollection")]
    public class DailyTimelineCommitTests
    {
        private const string TimelineKey = "valleytalk.npc-timeline-memories";
        private const string CategoryFlagKey = "valleytalk.memory-category-migrated";
        private const string Npc = "Abigail";

        static DailyTimelineCommitTests()
        {
            TestEnvironment.InstallHeadlessContext();
        }

        // ── 受控 IDataHelper：内存记录 + 按需抛出 ──

        private sealed class ControlledDataHelper : IDataHelper
        {
            public readonly Dictionary<string, object> SaveData = new();
            public bool ThrowOnRead;
            public bool ThrowOnWrite;
            public int WriteCallCount;

            public TModel ReadSaveData<TModel>(string key) where TModel : class
            {
                if (ThrowOnRead) throw new InvalidOperationException("controlled read failure");
                return SaveData.TryGetValue(key, out var value) ? (TModel)value : null;
            }

            public void WriteSaveData<TModel>(string key, TModel data) where TModel : class
            {
                WriteCallCount++;
                if (ThrowOnWrite) throw new InvalidOperationException("controlled write failure");
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

        // ── 状态装配与回收 ──

        private static MemoryEntry Daily(string id, string content, int createdDay, string dateLabel = "seeded label")
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
                Tier = MemoryTier.Daily,
                DateLabel = dateLabel,
                Importance = 3
            };

        private static DailyTimelineCommitRequest Create(string entryId, string content, int targetDay = 10)
            => new DailyTimelineCommitRequest
            {
                NpcName = Npc,
                TargetDay = targetDay,
                EntryId = entryId,
                IsCreate = true,
                ExpectedContentHash = null,
                NewContent = content
            };

        private static DailyTimelineCommitRequest Update(string entryId, string content, string expectedHash, int targetDay = 10)
            => new DailyTimelineCommitRequest
            {
                NpcName = Npc,
                TargetDay = targetDay,
                EntryId = entryId,
                IsCreate = false,
                ExpectedContentHash = expectedHash,
                NewContent = content
            };

        private static string H(string text) => DailyDistillationSnapshotBuilder.HashText(text);

        private ControlledDataHelper SetupLoaded(Dictionary<string, List<MemoryEntry>> timelineSeed = null, bool failLoad = false)
        {
            var data = new ControlledDataHelper { ThrowOnRead = failLoad };
            if (timelineSeed != null)
                data.SaveData[TimelineKey] = timelineSeed;
            data.SaveData[CategoryFlagKey] = "true"; // 跳过一次性类别迁移写回
            TestEnv.SetSHelper(new ControlledModHelper(data));
            TestEnvironment.WithWorldReady(true, () => MemoryManager.Instance.Load());
            return data;
        }

        private void Teardown()
        {
            MemoryManager.Instance.Cleanup();
            TestEnv.SetSHelper(null);
        }

        private static DailyTimelineCommitResult Commit(DailyTimelineCommitRequest request)
        {
            DailyTimelineCommitResult result = null;
            TestEnvironment.WithWorldReady(true, () =>
                result = MemoryManager.Instance.CommitDailyTimeline(request));
            return result;
        }

        private static List<MemoryEntry> TimelineOf(string npc = Npc)
            => MemoryManager.Instance.GetTimelineMemories(npc, MemoryTier.Daily);

        // ── 参数完整性（Invalid）──

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void Commit_InvalidNpcName_ReturnsInvalid(string npcName)
        {
            try
            {
                SetupLoaded();
                var request = new DailyTimelineCommitRequest { NpcName = npcName, TargetDay = 10, EntryId = "E1", IsCreate = true, NewContent = "content" };
                var result = Commit(request);
                Assert.Equal(DailyTimelineCommitStatus.Invalid, result.Status);
                Assert.False(string.IsNullOrEmpty(result.ErrorDetail));
            }
            finally { Teardown(); }
        }

        [Fact]
        public void Commit_InvalidEntryIdDayOrContent_ReturnsInvalid()
        {
            try
            {
                SetupLoaded();
                Assert.Equal(DailyTimelineCommitStatus.Invalid, Commit(Create(null, "content")).Status);
                Assert.Equal(DailyTimelineCommitStatus.Invalid, Commit(Create("E1", null)).Status);
                Assert.Equal(DailyTimelineCommitStatus.Invalid, Commit(Create("E1", "   ")).Status);
                Assert.Equal(DailyTimelineCommitStatus.Invalid,
                    Commit(new DailyTimelineCommitRequest { NpcName = Npc, TargetDay = 0, EntryId = "E1", IsCreate = true, NewContent = "content" }).Status);
                Assert.Equal(DailyTimelineCommitStatus.Invalid,
                    Commit(new DailyTimelineCommitRequest { NpcName = Npc, TargetDay = -1, EntryId = "E1", IsCreate = true, NewContent = "content" }).Status);

                string oversize = new string('x', MemoryManager.MaxMemoryLength + 1);
                Assert.Equal(DailyTimelineCommitStatus.Invalid, Commit(Create("E1", oversize)).Status);
            }
            finally { Teardown(); }
        }

        // ── 前置未就绪（Unavailable，零写入）──

        [Fact]
        public void Commit_NotLoaded_ReturnsUnavailableAndWritesNothing()
        {
            try
            {
                // 设置 Helper 但不执行 Load：IsLoaded=false
                var data = new ControlledDataHelper();
                TestEnv.SetSHelper(new ControlledModHelper(data));

                DailyTimelineCommitResult result = null;
                TestEnvironment.WithWorldReady(true, () =>
                    result = MemoryManager.Instance.CommitDailyTimeline(Create("E1", "content")));

                Assert.Equal(DailyTimelineCommitStatus.Unavailable, result.Status);
                Assert.Equal(0, data.WriteCallCount);
            }
            finally { Teardown(); }
        }

        [Fact]
        public void Commit_WorldNotReady_ReturnsUnavailableAndWritesNothing()
        {
            try
            {
                var data = SetupLoaded();
                int writesBefore = data.WriteCallCount;

                DailyTimelineCommitResult result = null;
                TestEnvironment.WithoutWorldReady(() =>
                    result = MemoryManager.Instance.CommitDailyTimeline(Create("E1", "content")));

                Assert.Equal(DailyTimelineCommitStatus.Unavailable, result.Status);
                Assert.Equal(writesBefore, data.WriteCallCount);
            }
            finally { Teardown(); }
        }

        [Fact]
        public void Commit_LoadFailed_ReturnsUnavailableAndWritesNothing()
        {
            try
            {
                var data = SetupLoaded(failLoad: true); // Load 抛异常 → _loadFailed

                var result = Commit(Create("E1", "content"));

                Assert.Equal(DailyTimelineCommitStatus.Unavailable, result.Status);
                Assert.Contains("load", result.ErrorDetail, StringComparison.OrdinalIgnoreCase);
                Assert.Equal(0, data.WriteCallCount);
            }
            finally { Teardown(); }
        }

        // ── 创建分支（Applied / Unchanged / Conflict / CapacityFull / Duplicate）──

        [Fact]
        public void Commit_CreateWithoutCard_AppliesAndPublishesCopy()
        {
            try
            {
                var seed = new Dictionary<string, List<MemoryEntry>>
                {
                    [Npc] = new List<MemoryEntry> { Daily("OTHER", "older day card", 5) }
                };
                var data = SetupLoaded(seed);
                var before = TimelineOf(Npc).Single(m => m.Id == "OTHER");

                var result = Commit(Create("NEW1", "a fresh day summary"));

                Assert.Equal(DailyTimelineCommitStatus.Applied, result.Status);
                Assert.Equal("NEW1", result.EntryId);
                Assert.Equal(H("a fresh day summary"), result.ContentHash);
                Assert.Null(result.ErrorDetail);

                // 成功后才发布：内存出现新条目，SaveData 收到副本
                var published = TimelineOf(Npc);
                Assert.Equal(2, published.Count);
                var created = published.Single(m => m.Id == "NEW1");
                Assert.Equal("a fresh day summary", created.Content);
                Assert.Equal(10, created.CreatedDay);
                Assert.Equal(MemoryTier.Daily, created.Tier);
                Assert.Equal("Timeline", created.Source);
                Assert.Equal(MemoryCategory.Fact, created.Category);
                Assert.Equal(MemoryType.Fact, created.Type);
                Assert.Equal(3, created.Importance);
                Assert.Equal(Npc, created.NpcName);
                Assert.False(string.IsNullOrWhiteSpace(created.DateLabel));
                Assert.True(created.CreatedAt > DateTime.MinValue);

                var written = (Dictionary<string, List<MemoryEntry>>)data.SaveData[TimelineKey];
                Assert.Contains(written[Npc], m => m.Id == "NEW1"); // 写入 SaveData 的即发布的副本
                Assert.True(written.ContainsKey(Npc));

                // 既有条目未被改动且无共享可变引用
                Assert.Equal("older day card", before.Content);
                Assert.Same(before, TimelineOf(Npc).Single(m => m.Id == "OTHER"));
            }
            finally { Teardown(); }
        }

        [Fact]
        public void Commit_DuplicateCreateReplay_IsUnchangedWithoutReinsert()
        {
            try
            {
                SetupLoaded();
                var first = Commit(Create("NEW1", "a fresh day summary"));
                Assert.Equal(DailyTimelineCommitStatus.Applied, first.Status);
                int countAfterApply = TimelineOf(Npc).Count;

                // 同一 create 请求重复到达：同日唯一 ID 与正文 hash 一致 → Unchanged，不再次插入
                var replay = Commit(Create("NEW1", "a fresh day summary"));

                Assert.Equal(DailyTimelineCommitStatus.Unchanged, replay.Status);
                Assert.Equal("NEW1", replay.EntryId);
                Assert.Equal(H("a fresh day summary"), replay.ContentHash);
                Assert.Equal(countAfterApply, TimelineOf(Npc).Count);
            }
            finally { Teardown(); }
        }

        [Fact]
        public void Commit_CreateWhenDayHasOtherCard_ReturnsConflict()
        {
            try
            {
                SetupLoaded(new Dictionary<string, List<MemoryEntry>>
                {
                    [Npc] = new List<MemoryEntry> { Daily("E1", "existing card", 10) }
                });

                var result = Commit(Create("NEW1", "another summary"));

                Assert.Equal(DailyTimelineCommitStatus.Conflict, result.Status);
                Assert.Empty(TimelineOf(Npc).Where(m => m.Id == "NEW1"));
            }
            finally { Teardown(); }
        }

        [Fact]
        public void Commit_CreateWhenIdUsedOnOtherEntry_ReturnsConflict()
        {
            try
            {
                // ID 已被该 NPC 的其他时间线条目（跨日 Daily）占用
                SetupLoaded(new Dictionary<string, List<MemoryEntry>>
                {
                    [Npc] = new List<MemoryEntry> { Daily("NEW1", "old day card", 5) }
                });

                var result = Commit(Create("NEW1", "a fresh day summary"));

                Assert.Equal(DailyTimelineCommitStatus.Conflict, result.Status);
                Assert.Contains("already used", result.ErrorDetail);
            }
            finally { Teardown(); }
        }

        [Fact]
        public void Commit_CreateWhenDailyFull_ReturnsCapacityFull()
        {
            try
            {
                var list = Enumerable.Range(0, MemoryManager.GetTierCapacity(MemoryTier.Daily))
                    .Select(i => Daily($"D{i}", $"day card {i}", 100 - i))
                    .ToList();
                SetupLoaded(new Dictionary<string, List<MemoryEntry>> { [Npc] = list });

                var result = Commit(Create("NEW1", "a fresh day summary"));

                Assert.Equal(DailyTimelineCommitStatus.CapacityFull, result.Status);
                Assert.Equal(MemoryManager.GetTierCapacity(MemoryTier.Daily), TimelineOf(Npc).Count);
            }
            finally { Teardown(); }
        }

        [Fact]
        public void Commit_CreateWhenContentDuplicate_ReturnsDuplicate()
        {
            try
            {
                // 与另一天的既有 Daily 正文忽略大小写重复
                SetupLoaded(new Dictionary<string, List<MemoryEntry>>
                {
                    [Npc] = new List<MemoryEntry> { Daily("OLD", "A FRESH DAY SUMMARY", 5) }
                });

                var result = Commit(Create("NEW1", "a fresh day summary"));

                Assert.Equal(DailyTimelineCommitStatus.Duplicate, result.Status);
            }
            finally { Teardown(); }
        }

        // ── 更新分支（Applied / Unchanged / Conflict / Duplicate）──

        [Fact]
        public void Commit_UpdateMatchingCard_AppliesAndKeepsIdentity()
        {
            try
            {
                var seeded = Daily("E1", "old content", 10);
                seeded.CreatedAt = new DateTime(2026, 1, 1, 9, 30, 0);
                SetupLoaded(new Dictionary<string, List<MemoryEntry>>
                {
                    [Npc] = new List<MemoryEntry> { seeded }
                });

                var result = Commit(Update("E1", "new content", H("old content")));

                Assert.Equal(DailyTimelineCommitStatus.Applied, result.Status);
                Assert.Equal("E1", result.EntryId);
                Assert.Equal(H("new content"), result.ContentHash);

                var updated = TimelineOf(Npc).Single(m => m.Id == "E1");
                Assert.Equal("new content", updated.Content);          // 仅改 Content
                Assert.Equal(10, updated.CreatedDay);                  // 保留 CreatedDay
                Assert.Equal(new DateTime(2026, 1, 1, 9, 30, 0), updated.CreatedAt); // 保留 CreatedAt
                Assert.Equal("seeded label", updated.DateLabel);       // 保留 DateLabel
                Assert.Equal(MemoryTier.Daily, updated.Tier);
                Assert.Equal(3, updated.Importance);
                Assert.Equal("Timeline", updated.Source);
                Assert.NotSame(seeded, updated);                       // 属性副本，不共享引用
                Assert.Equal("old content", seeded.Content);           // 原条目未被改动
            }
            finally { Teardown(); }
        }

        [Fact]
        public void Commit_UpdateReplay_SameIdSameContent_IsUnchanged()
        {
            try
            {
                SetupLoaded(new Dictionary<string, List<MemoryEntry>>
                {
                    [Npc] = new List<MemoryEntry> { Daily("E1", "current content", 10) }
                });

                // 重放识别优先于旧 hash 检查：即使 ExpectedContentHash 不是当前正文也命中 Unchanged
                var result = Commit(Update("E1", "current content", H("stale expected hash")));

                Assert.Equal(DailyTimelineCommitStatus.Unchanged, result.Status);
                Assert.Equal(H("current content"), result.ContentHash);
            }
            finally { Teardown(); }
        }

        [Fact]
        public void Commit_UpdateHashMismatch_ReturnsConflictAndKeepsContent()
        {
            try
            {
                SetupLoaded(new Dictionary<string, List<MemoryEntry>>
                {
                    [Npc] = new List<MemoryEntry> { Daily("E1", "old content", 10) }
                });

                var result = Commit(Update("E1", "new content", H("different old content")));

                Assert.Equal(DailyTimelineCommitStatus.Conflict, result.Status);
                Assert.Contains("hash mismatch", result.ErrorDetail);
                Assert.Equal("old content", TimelineOf(Npc).Single(m => m.Id == "E1").Content);
            }
            finally { Teardown(); }
        }

        [Fact]
        public void Commit_UpdateWithMultipleCardsSameDay_ReturnsConflict()
        {
            try
            {
                SetupLoaded(new Dictionary<string, List<MemoryEntry>>
                {
                    [Npc] = new List<MemoryEntry>
                    {
                        Daily("E1", "first card", 10),
                        Daily("E2", "second card", 10)
                    }
                });

                var result = Commit(Update("E1", "new content", H("first card")));

                Assert.Equal(DailyTimelineCommitStatus.Conflict, result.Status);
                Assert.Contains("exactly 1", result.ErrorDetail);
            }
            finally { Teardown(); }
        }

        [Fact]
        public void Commit_UpdateWithNoCardOnDay_ReturnsConflict()
        {
            try
            {
                SetupLoaded(new Dictionary<string, List<MemoryEntry>>
                {
                    [Npc] = new List<MemoryEntry> { Daily("E1", "other day card", 5) }
                });

                var result = Commit(Update("E1", "new content", H("other day card"), targetDay: 10));

                Assert.Equal(DailyTimelineCommitStatus.Conflict, result.Status);
            }
            finally { Teardown(); }
        }

        [Fact]
        public void Commit_UpdateIdMismatchOnOwnedDay_ReturnsConflict()
        {
            try
            {
                SetupLoaded(new Dictionary<string, List<MemoryEntry>>
                {
                    [Npc] = new List<MemoryEntry> { Daily("E1", "old content", 10) }
                });

                var result = Commit(Update("OTHER", "new content", H("old content")));

                Assert.Equal(DailyTimelineCommitStatus.Conflict, result.Status);
                Assert.Contains("id mismatch", result.ErrorDetail);
            }
            finally { Teardown(); }
        }

        [Fact]
        public void Commit_UpdateWhenOtherDailyHasSameContent_ReturnsDuplicate()
        {
            try
            {
                SetupLoaded(new Dictionary<string, List<MemoryEntry>>
                {
                    [Npc] = new List<MemoryEntry>
                    {
                        Daily("E1", "old content", 10),
                        Daily("E2", "NEW CONTENT", 5) // 忽略大小写与请求正文重复
                    }
                });

                var result = Commit(Update("E1", "new content", H("old content")));

                Assert.Equal(DailyTimelineCommitStatus.Duplicate, result.Status);
                Assert.Equal("old content", TimelineOf(Npc).Single(m => m.Id == "E1").Content);
            }
            finally { Teardown(); }
        }

        // ── 写入失败（StorageFailed，内存原值保持）──

        [Fact]
        public void Commit_WriteFailure_Update_KeepsOldMemoryAndSaveData()
        {
            try
            {
                var seeded = Daily("E1", "old content", 10);
                var data = SetupLoaded(new Dictionary<string, List<MemoryEntry>>
                {
                    [Npc] = new List<MemoryEntry> { seeded }
                });
                data.ThrowOnWrite = true;

                var result = Commit(Update("E1", "new content", H("old content")));

                Assert.Equal(DailyTimelineCommitStatus.StorageFailed, result.Status);
                Assert.Contains("controlled write failure", result.ErrorDetail);
                Assert.Equal("old content", TimelineOf(Npc).Single(m => m.Id == "E1").Content); // 内存原值保持

                // SaveData 仍是装载时的种子，未被覆写
                var persisted = (Dictionary<string, List<MemoryEntry>>)data.SaveData[TimelineKey];
                Assert.Equal("old content", persisted[Npc].Single(m => m.Id == "E1").Content);
                Assert.Equal("old content", seeded.Content);           // 原条目对象未被改动
            }
            finally { Teardown(); }
        }

        [Fact]
        public void Commit_WriteFailure_Create_KeepsMemoryEmpty()
        {
            try
            {
                var data = SetupLoaded();
                data.ThrowOnWrite = true;

                var result = Commit(Create("NEW1", "a fresh day summary"));

                Assert.Equal(DailyTimelineCommitStatus.StorageFailed, result.Status);
                Assert.Empty(TimelineOf(Npc)); // 未发布
                Assert.False(data.SaveData.ContainsKey(TimelineKey));
            }
            finally { Teardown(); }
        }

        // ── NPC 键大小写（OrdinalIgnoreCase 既有字典规则）──

        [Fact]
        public void Commit_Update_ResolvesNpcKeyCaseInsensitively()
        {
            try
            {
                SetupLoaded(new Dictionary<string, List<MemoryEntry>>
                {
                    ["abigail"] = new List<MemoryEntry> { Daily("E1", "old content", 10) }
                });

                var request = new DailyTimelineCommitRequest
                {
                    NpcName = "ABIGAIL",
                    TargetDay = 10,
                    EntryId = "E1",
                    IsCreate = false,
                    ExpectedContentHash = H("old content"),
                    NewContent = "new content"
                };
                var result = Commit(request);

                Assert.Equal(DailyTimelineCommitStatus.Applied, result.Status);
                Assert.Equal("new content", TimelineOf("ABIGAIL").Single(m => m.Id == "E1").Content);
            }
            finally { Teardown(); }
        }
    }
}
