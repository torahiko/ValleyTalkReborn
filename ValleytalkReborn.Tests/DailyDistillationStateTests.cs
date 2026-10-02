using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using StardewModdingAPI;
using StardewValley;
using ValleytalkReborn;
using Xunit;

namespace ValleytalkReborn.Tests
{
    /// <summary>
    /// DD403-LEDGER：NPC 日记蒸馏账本状态、所有权核对与崩溃恢复。
    /// 覆盖：JSON 序列化/反序列化往返、未找到 key 时的空 ledger、未知 schema 拒绝（原字符串保留）、
    /// world-ready 守卫零写入、Reconcile 各分支（prepared 未写、已写未 ack、内容不匹配、手工编辑、
    /// 条目删除、旧多卡片、无所有权、幂等性）、恢复不增加次数、过期回收与保留窗口。
    /// 仅使用无游戏实例的 DTO/JSON 与受控 NPC 对象，不访问世界。
    /// </summary>
    [Collection("WorldReadyStateCollection")]
    public class DailyDistillationStateTests
    {
        private const int AnyDay = 10;

        static DailyDistillationStateTests()
        {
            TestEnvironment.InstallHeadlessContext();
        }

        // ── 受控对象构造 ──

        private static NPC NewNpc() => new NPC();

        private static string H(string text) => DailyDistillationSnapshotBuilder.HashText(text);

        private static MemoryEntry Card(string id, string content, int createdDay, string source = "Manual")
            => new MemoryEntry
            {
                Id = id,
                NpcName = "Abigail",
                Content = content,
                Tier = MemoryTier.Daily,
                CreatedDay = createdDay,
                Source = source
            };

        private static DailyDistillationDayState Day(int targetDay, Action<DailyDistillationDayState> setup = null)
        {
            var day = new DailyDistillationDayState { TargetDay = targetDay };
            setup?.Invoke(day);
            return day;
        }

        private static DailyPendingCommit Pending(
            string commitId = "commit-1",
            string entryId = "E1",
            string expectedOldHash = null,
            string newContentHash = null,
            string inputFingerprint = null,
            string[] coveredRowKeys = null,
            bool isFinal = false)
            => new DailyPendingCommit
            {
                CommitId = commitId,
                EntryId = entryId,
                ExpectedOldHash = expectedOldHash ?? "",
                NewContentHash = newContentHash ?? H("new content"),
                InputFingerprint = inputFingerprint ?? H("fingerprint"),
                CoveredRowKeys = coveredRowKeys?.ToList(),
                IsFinal = isFinal
            };

        private static bool WriteLedger(NPC npc, DailyDistillationLedger ledger)
        {
            bool written = false;
            TestEnvironment.WithWorldReady(true, () => written = DailyDistillationStateStore.TryWrite(npc, ledger));
            return written;
        }

        private static DailyDistillationLedger ReadLedger(NPC npc)
        {
            DailyDistillationLedger ledger = null;
            bool read = false;
            TestEnvironment.WithWorldReady(true, () => read = DailyDistillationStateStore.TryRead(npc, out ledger));
            Assert.True(read, "ledger setup read should succeed");
            return ledger;
        }

        private static string Raw(NPC npc)
        {
            string raw = null;
            TestEnvironment.WithWorldReady(true, () => npc.modData.TryGetValue(DailyDistillationStateStore.ModDataKey, out raw));
            return raw;
        }

        private static void Reconcile(NPC npc, DailyDistillationLedger ledger, IReadOnlyList<MemoryEntry> entries, int currentGameDay = 0)
        {
            TestEnvironment.WithWorldReady(true, () =>
                DailyDistillationStateStore.Reconcile(npc, ledger, entries, currentGameDay));
        }

        // ── TryRead / TryWrite：空 ledger、schema 拒绝、往返 ──

        [Fact]
        public void TryRead_WithoutKey_ReturnsFreshLedger_AndWritesNothing()
        {
            var npc = NewNpc();
            DailyDistillationLedger ledger = null;
            bool read = false;

            TestEnvironment.WithWorldReady(true, () => read = DailyDistillationStateStore.TryRead(npc, out ledger));

            Assert.True(read);
            Assert.NotNull(ledger);
            Assert.Equal(1, ledger.Version);
            Assert.Empty(ledger.Days);
            Assert.Null(Raw(npc)); // 未找到 key：不落任何写入
        }

        [Fact]
        public void TryRead_MalformedJson_RejectedAndOriginalPreserved()
        {
            var npc = NewNpc();
            string raw = "{\"Version\":1,\"Days\":";
            WriteRaw(npc, raw);

            DailyDistillationLedger ledger = null;
            TestEnvironment.WithWorldReady(true, () => DailyDistillationStateStore.TryRead(npc, out ledger));

            Assert.Null(ledger);
            Assert.Equal(raw, Raw(npc)); // 原畸形字符串未覆盖
        }

        [Theory]
        [InlineData("{\"Version\":2,\"Days\":{}}")]                                    // 未知 Version
        [InlineData("{\"Version\":1,\"Days\":null}")]                                  // Days 为 null
        [InlineData("{\"Version\":1,\"Days\":{\"5\":{\"TargetDay\":5,\"IntradayAttempts\":-1}}}")]   // 负预算
        [InlineData("{\"Version\":1,\"Days\":{\"5\":{\"TargetDay\":5,\"FinalAttempts\":-1}}}")]      // 负预算
        [InlineData("{\"Version\":1,\"Days\":{\"5\":{\"TargetDay\":6}}}")]             // 键 ≠ TargetDay
        [InlineData("{\"Version\":1,\"Days\":{\"5\":{\"TargetDay\":5,\"LastContentHash\":\"xyz\"}}}")] // hash 格式
        [InlineData("{\"Version\":1,\"Days\":{\"5\":{\"TargetDay\":5,\"CoveredRowKeys\":[\"abc\"]}}}")] // 行 hash 格式
        [InlineData("{\"Version\":1,\"Days\":{\"5\":{\"TargetDay\":5,\"PendingCommit\":{\"EntryId\":\"E1\",\"NewContentHash\":\"aa\"}}}}")] // pending 缺 CommitId
        [InlineData("{\"Version\":1,\"Days\":{\"5\":{\"TargetDay\":5,\"PendingCommit\":{\"CommitId\":\"C\",\"EntryId\":\"E1\"}}}}")]        // pending 缺 NewContentHash
        public void TryRead_SchemaViolations_RejectedAndOriginalPreserved(string raw)
        {
            var npc = NewNpc();
            WriteRaw(npc, raw);

            DailyDistillationLedger ledger = null;
            TestEnvironment.WithWorldReady(true, () => DailyDistillationStateStore.TryRead(npc, out ledger));

            Assert.Null(ledger);
            Assert.Equal(raw, Raw(npc));
        }

        [Fact]
        public void TryWrite_InvalidLedger_RejectedAndNothingStored()
        {
            var npc = NewNpc();
            var ledger = new DailyDistillationLedger();
            ledger.Days[AnyDay] = Day(AnyDay, d => d.FinalAttempts = -1);

            bool written = false;
            TestEnvironment.WithWorldReady(true, () => written = DailyDistillationStateStore.TryWrite(npc, ledger));

            Assert.False(written);
            Assert.Null(Raw(npc));
        }

        [Fact]
        public void RoundTrip_PreservesAttemptsSuspensionAndDuplicateRowKeys()
        {
            var npc = NewNpc();
            var pending = Pending(
                commitId: "c1", entryId: "E1",
                expectedOldHash: H("old"), newContentHash: H("new"),
                inputFingerprint: H("fp"),
                coveredRowKeys: new[] { H("row-a"), H("row-a"), H("row-b") },
                isFinal: false);
            var original = new DailyDistillationLedger();
            original.Days[AnyDay] = Day(AnyDay, d =>
            {
                d.EntryId = "E0";
                d.LastContentHash = H("previous");
                d.CoveredRowKeys = new List<string> { H("row-a"), H("row-a"), H("row-b") }; // 覆盖行保留重复次数
                d.LastEvaluatedFingerprint = H("fp-old");
                d.IntradayAttempts = 2;
                d.FinalAttempts = 1;
                d.Suspended = true;
                d.SuspensionReason = "Edited";
                d.FinalizationClosed = true;
                d.FinalizationOutcome = "Unchanged";
                d.PendingCommit = pending;
            });
            original.Days[AnyDay + 1] = Day(AnyDay + 1);

            Assert.True(WriteLedger(npc, original));
            var reloaded = ReadLedger(npc);

            Assert.Equal(2, reloaded.Days.Count);
            var day = reloaded.Days[AnyDay];
            Assert.Equal("E0", day.EntryId);
            Assert.Equal(H("previous"), day.LastContentHash);
            Assert.Equal(new[] { H("row-a"), H("row-a"), H("row-b") }, day.CoveredRowKeys);
            Assert.Equal(H("fp-old"), day.LastEvaluatedFingerprint);
            Assert.Equal(2, day.IntradayAttempts);  // 重载相同 JSON 得到相同次数
            Assert.Equal(1, day.FinalAttempts);
            Assert.True(day.Suspended);
            Assert.Equal("Edited", day.SuspensionReason); // 相同暂停状态
            Assert.True(day.FinalizationClosed);
            Assert.Equal("Unchanged", day.FinalizationOutcome);
            Assert.NotNull(day.PendingCommit);
            Assert.Equal("c1", day.PendingCommit.CommitId);
            Assert.Equal(H("old"), day.PendingCommit.ExpectedOldHash);
            Assert.Equal(H("new"), day.PendingCommit.NewContentHash);
            Assert.Equal(H("fp"), day.PendingCommit.InputFingerprint);
            Assert.Equal(new[] { H("row-a"), H("row-a"), H("row-b") }, day.PendingCommit.CoveredRowKeys);
            Assert.False(day.PendingCommit.IsFinal);
            Assert.NotNull(reloaded.Days[AnyDay + 1]);
        }

        [Fact]
        public void Guards_NotWorldReadyOrMissingNpc_RejectAndWriteNothing()
        {
            var npc = NewNpc();
            string raw = "{\"Version\":1,\"Days\":{}}";
            WriteRaw(npc, raw);

            // 未加载：TryRead/TryWrite=false
            DailyDistillationLedger ledger = null;
            bool read = true;
            bool written = true;
            TestEnvironment.WithoutWorldReady(() =>
            {
                read = DailyDistillationStateStore.TryRead(npc, out ledger);
                written = DailyDistillationStateStore.TryWrite(npc, new DailyDistillationLedger());
            });
            Assert.False(read);
            Assert.Null(ledger);
            Assert.False(written);

            // 未加载：Reconcile 零写入（内存核对不落盘）
            var changeworthy = new DailyDistillationLedger();
            changeworthy.Days[AnyDay] = Day(AnyDay, d =>
            {
                d.EntryId = "E1";
                d.LastContentHash = H("old");
            });
            TestEnvironment.WithoutWorldReady(() =>
                DailyDistillationStateStore.Reconcile(npc, changeworthy, new List<MemoryEntry>(), 10));
            Assert.Equal(raw, Raw(npc));

            // NPC 为空：读写均拒绝
            TestEnvironment.WithWorldReady(true, () =>
            {
                Assert.False(DailyDistillationStateStore.TryRead(null, out var nullLedger));
                Assert.Null(nullLedger);
                Assert.False(DailyDistillationStateStore.TryWrite(null, new DailyDistillationLedger()));
            });
        }

        // ── Reconcile：prepared 未写（清除 pending，进度保持旧值）──

        [Fact]
        public void Reconcile_PreparedCreateWithoutCard_ClearsPendingAndKeepsProgress()
        {
            var npc = NewNpc();
            var ledger = new DailyDistillationLedger();
            ledger.Days[AnyDay] = Day(AnyDay, d =>
            {
                d.PendingCommit = Pending(entryId: "P1", expectedOldHash: "", newContentHash: H("new"));
            });
            Assert.True(WriteLedger(npc, ledger));

            Reconcile(npc, ledger, new List<MemoryEntry>());

            var day = ledger.Days[AnyDay];
            Assert.Null(day.PendingCommit);          // 清除 pending
            Assert.Equal("", day.EntryId);           // 进度保持旧值：无所有权
            Assert.False(day.Suspended);
            Assert.Equal(0, day.IntradayAttempts);   // 恢复不增加次数
            Assert.Equal(0, day.FinalAttempts);

            var republished = ReadLedger(npc);
            Assert.Null(republished.Days[AnyDay].PendingCommit); // 回写已发布
        }

        [Fact]
        public void Reconcile_PreparedUpdateWithOldContent_ClearsPendingAndKeepsProgress()
        {
            var npc = NewNpc();
            var ledger = new DailyDistillationLedger();
            ledger.Days[AnyDay] = Day(AnyDay, d =>
            {
                d.EntryId = "E1";
                d.LastContentHash = H("old");
                d.CoveredRowKeys = new List<string> { H("row-a"), H("row-a"), H("row-b") };
                d.LastEvaluatedFingerprint = H("fp-old");
                d.IntradayAttempts = 1;
                d.FinalAttempts = 0;
                d.PendingCommit = Pending(
                    entryId: "E1",
                    expectedOldHash: H("old"),
                    newContentHash: H("new"),
                    inputFingerprint: H("fp-new"),
                    coveredRowKeys: new[] { H("row-c") });
            });
            var entries = new List<MemoryEntry> { Card("E1", "old", AnyDay) };
            Assert.True(WriteLedger(npc, ledger));

            Reconcile(npc, ledger, entries);

            var day = ledger.Days[AnyDay];
            Assert.Null(day.PendingCommit);
            Assert.Equal("E1", day.EntryId);                       // 进度保持旧值
            Assert.Equal(H("old"), day.LastContentHash);
            Assert.Equal(new[] { H("row-a"), H("row-a"), H("row-b") }, day.CoveredRowKeys);
            Assert.Equal(H("fp-old"), day.LastEvaluatedFingerprint);
            Assert.Equal(1, day.IntradayAttempts);                 // 次数不增加
            Assert.Equal(0, day.FinalAttempts);
            Assert.False(day.Suspended);
        }

        // ── Reconcile：已写未 ack（恢复所有权与已评估进度）──

        [Fact]
        public void Reconcile_CommittedButNotAcked_RestoresOwnershipAndCoveredRows()
        {
            var npc = NewNpc();
            var ledger = new DailyDistillationLedger();
            ledger.Days[AnyDay] = Day(AnyDay, d =>
            {
                d.EntryId = "E1";
                d.LastContentHash = H("old");
                d.LastEvaluatedFingerprint = H("fp-old");
                d.IntradayAttempts = 1;
                d.PendingCommit = Pending(
                    entryId: "E1",
                    expectedOldHash: H("old"),
                    newContentHash: H("new"),
                    inputFingerprint: H("fp-new"),
                    coveredRowKeys: new[] { H(H("row-1")), H(H("row-1")), H("row-2") });
            });
            var card = Card("E1", "new", AnyDay);
            Assert.True(WriteLedger(npc, ledger));

            Reconcile(npc, ledger, new List<MemoryEntry> { card });

            var day = ledger.Days[AnyDay];
            Assert.Null(day.PendingCommit);
            Assert.Equal("E1", day.EntryId);                       // 恢复所有权
            Assert.Equal(H("new"), day.LastContentHash);
            Assert.Equal(H("fp-new"), day.LastEvaluatedFingerprint); // 恢复 LastEvaluatedFingerprint
            Assert.Equal(new[] { H(H("row-1")), H(H("row-1")), H("row-2") }, day.CoveredRowKeys); // 覆盖行与重复次数
            Assert.False(day.Suspended);
            Assert.False(day.FinalizationClosed);
            Assert.Equal(1, day.IntradayAttempts);                 // 恢复不再计费
            Assert.Equal("new", card.Content);                     // 恢复不修改日记
        }

        [Fact]
        public void Reconcile_CommittedCreateButNotAcked_RestoresOwnership()
        {
            var npc = NewNpc();
            var ledger = new DailyDistillationLedger();
            ledger.Days[AnyDay] = Day(AnyDay, d =>
            {
                d.PendingCommit = Pending(
                    entryId: "P1",
                    expectedOldHash: "",
                    newContentHash: H("new"),
                    inputFingerprint: H("fp-new"),
                    coveredRowKeys: new[] { H("row-1") });
            });
            var card = Card("P1", "new", AnyDay);
            Assert.True(WriteLedger(npc, ledger));

            Reconcile(npc, ledger, new List<MemoryEntry> { card });

            var day = ledger.Days[AnyDay];
            Assert.Null(day.PendingCommit);
            Assert.Equal("P1", day.EntryId);
            Assert.Equal(H("new"), day.LastContentHash);
            Assert.Equal(new[] { H("row-1") }, day.CoveredRowKeys);
            Assert.Equal(H("fp-new"), day.LastEvaluatedFingerprint);
            Assert.False(day.Suspended);
        }

        [Fact]
        public void Reconcile_CommittedFinalButNotAcked_ClosesFinalizationAsCommitted()
        {
            var npc = NewNpc();
            var ledger = new DailyDistillationLedger();
            ledger.Days[AnyDay] = Day(AnyDay, d =>
            {
                d.EntryId = "E1";
                d.LastContentHash = H("old");
                d.PendingCommit = Pending(
                    entryId: "E1",
                    expectedOldHash: H("old"),
                    newContentHash: H("new"),
                    coveredRowKeys: new[] { H("row-1") },
                    isFinal: true);
            });
            Assert.True(WriteLedger(npc, ledger));

            Reconcile(npc, ledger, new List<MemoryEntry> { Card("E1", "new", AnyDay) });

            var day = ledger.Days[AnyDay];
            Assert.Null(day.PendingCommit);
            Assert.True(day.FinalizationClosed);
            Assert.Equal("Committed", day.FinalizationOutcome);
            Assert.Equal(H("new"), day.LastContentHash);
        }

        // ── Reconcile：内容不匹配 → RecoveryConflict，保留 pending，不增加次数 ──

        [Fact]
        public void Reconcile_ContentMatchesNeitherHash_SuspendsRecoveryConflictAndKeepsPending()
        {
            var npc = NewNpc();
            var ledger = new DailyDistillationLedger();
            ledger.Days[AnyDay] = Day(AnyDay, d =>
            {
                d.EntryId = "E1";
                d.LastContentHash = H("old");
                d.IntradayAttempts = 1;
                d.FinalAttempts = 1;
                d.PendingCommit = Pending(
                    entryId: "E1",
                    expectedOldHash: H("old"),
                    newContentHash: H("new"));
            });
            Assert.True(WriteLedger(npc, ledger));

            Reconcile(npc, ledger, new List<MemoryEntry> { Card("E1", "player-touched", AnyDay) });

            var day = ledger.Days[AnyDay];
            Assert.True(day.Suspended);
            Assert.Equal("RecoveryConflict", day.SuspensionReason);
            Assert.NotNull(day.PendingCommit);      // 保留 pending 作为证据
            Assert.Equal("commit-1", day.PendingCommit.CommitId);
            Assert.Equal(1, day.IntradayAttempts);  // 任何恢复分支不增加次数
            Assert.Equal(1, day.FinalAttempts);
        }

        [Fact]
        public void Reconcile_PendingWithMultipleEntries_SuspendsRecoveryConflict()
        {
            var npc = NewNpc();
            var ledger = new DailyDistillationLedger();
            ledger.Days[AnyDay] = Day(AnyDay, d =>
            {
                d.EntryId = "E1";
                d.LastContentHash = H("old");
                d.PendingCommit = Pending(
                    entryId: "E1",
                    expectedOldHash: H("old"),
                    newContentHash: H("new"));
            });
            var entries = new List<MemoryEntry>
            {
                Card("E1", "old", AnyDay),
                Card("E2", "second card", AnyDay)
            };
            Assert.True(WriteLedger(npc, ledger));

            Reconcile(npc, ledger, entries);

            var day = ledger.Days[AnyDay];
            Assert.True(day.Suspended);
            Assert.Equal("RecoveryConflict", day.SuspensionReason);
            Assert.NotNull(day.PendingCommit);
        }

        // ── Reconcile：无 pending 的所有权核对 ──

        [Fact]
        public void Reconcile_OwnedEntryDeleted_SuspendsDeleted()
        {
            var npc = NewNpc();
            var ledger = new DailyDistillationLedger();
            ledger.Days[AnyDay] = Day(AnyDay, d =>
            {
                d.EntryId = "E1";
                d.LastContentHash = H("old");
            });
            Assert.True(WriteLedger(npc, ledger));

            Reconcile(npc, ledger, new List<MemoryEntry>()); // 条目缺失

            var day = ledger.Days[AnyDay];
            Assert.True(day.Suspended);
            Assert.Equal("Deleted", day.SuspensionReason);
        }

        [Fact]
        public void Reconcile_OwnedEntryEdited_SuspendsEdited()
        {
            var npc = NewNpc();
            var ledger = new DailyDistillationLedger();
            ledger.Days[AnyDay] = Day(AnyDay, d =>
            {
                d.EntryId = "E1";
                d.LastContentHash = H("old");
            });
            Assert.True(WriteLedger(npc, ledger));

            Reconcile(npc, ledger, new List<MemoryEntry> { Card("E1", "edited by player", AnyDay) });

            var day = ledger.Days[AnyDay];
            Assert.True(day.Suspended);
            Assert.Equal("Edited", day.SuspensionReason);
        }

        [Fact]
        public void Reconcile_MultipleEntriesSameDay_SuspendsMultipleEntries()
        {
            var npc = NewNpc();
            var ledger = new DailyDistillationLedger();
            ledger.Days[AnyDay] = Day(AnyDay, d =>
            {
                d.EntryId = "E1";
                d.LastContentHash = H("old");
            });
            var entries = new List<MemoryEntry>
            {
                Card("E1", "old", AnyDay),
                Card("E2", "another", AnyDay)
            };
            Assert.True(WriteLedger(npc, ledger));

            Reconcile(npc, ledger, entries);

            var day = ledger.Days[AnyDay];
            Assert.True(day.Suspended);
            Assert.Equal("MultipleEntries", day.SuspensionReason);
        }

        [Fact]
        public void Reconcile_UnownedEntries_SuspendsUnownedEntry_RegardlessOfSource()
        {
            var npc = NewNpc();
            var ledger = new DailyDistillationLedger();
            ledger.Days[AnyDay] = Day(AnyDay); // 无所有权、无 pending
            Assert.True(WriteLedger(npc, ledger));

            // 不通过 Source 猜测其是否自动产生：Manual 与 Auto 同样处理
            Reconcile(npc, ledger, new List<MemoryEntry> { Card("E9", "foreign card", AnyDay, source: "Auto") });

            var day = ledger.Days[AnyDay];
            Assert.True(day.Suspended);
            Assert.Equal("UnownedEntry", day.SuspensionReason);
        }

        [Fact]
        public void Reconcile_NoOwnershipAndNoEntries_LeavesDayUntouched()
        {
            var npc = NewNpc();
            var ledger = new DailyDistillationLedger();
            ledger.Days[AnyDay] = Day(AnyDay, d => d.LastEvaluatedFingerprint = H("fp"));
            Assert.True(WriteLedger(npc, ledger));
            string before = Raw(npc);

            Reconcile(npc, ledger, new List<MemoryEntry>());

            Assert.False(ledger.Days[AnyDay].Suspended);
            Assert.Equal(before, Raw(npc)); // 无变化不回写
        }

        // ── 已最终关闭：条目缺失保留关闭状态；被编辑保留玩家内容 ──

        [Fact]
        public void Reconcile_ClosedDayEntryMissing_KeepsClosedStateForPromotion()
        {
            var npc = NewNpc();
            var ledger = new DailyDistillationLedger();
            ledger.Days[AnyDay] = Day(AnyDay, d =>
            {
                d.EntryId = "E1";
                d.LastContentHash = H("old");
                d.FinalizationClosed = true;
                d.FinalizationOutcome = "Committed";
            });
            Assert.True(WriteLedger(npc, ledger));

            Reconcile(npc, ledger, new List<MemoryEntry>()); // 条目缺失

            var day = ledger.Days[AnyDay];
            Assert.False(day.Suspended);            // 保留关闭状态，允许正常升档
            Assert.True(day.FinalizationClosed);
            Assert.Equal("Committed", day.FinalizationOutcome);
        }

        [Fact]
        public void Reconcile_ClosedDayEntryEdited_KeepsPlayerContentAndState()
        {
            var npc = NewNpc();
            var ledger = new DailyDistillationLedger();
            ledger.Days[AnyDay] = Day(AnyDay, d =>
            {
                d.EntryId = "E1";
                d.LastContentHash = H("old");
                d.FinalizationClosed = true;
                d.FinalizationOutcome = "Committed";
            });
            var card = Card("E1", "player version", AnyDay);
            Assert.True(WriteLedger(npc, ledger));

            Reconcile(npc, ledger, new List<MemoryEntry> { card });

            var day = ledger.Days[AnyDay];
            Assert.False(day.Suspended);            // 保留玩家内容，不改写状态
            Assert.True(day.FinalizationClosed);
            Assert.Equal(H("old"), day.LastContentHash);
            Assert.Equal("player version", card.Content); // 恢复不修改日记
        }

        // ── 幂等性：反复恢复同一状态得到相同结果 ──

        [Fact]
        public void Reconcile_RepeatedRuns_AreIdempotent()
        {
            var npc = NewNpc();
            var ledger = new DailyDistillationLedger();
            ledger.Days[AnyDay] = Day(AnyDay, d =>
            {
                d.EntryId = "E1";
                d.LastContentHash = H("old");
                d.PendingCommit = Pending(
                    entryId: "E1",
                    expectedOldHash: H("old"),
                    newContentHash: H("new"),
                    coveredRowKeys: new[] { H("row-1"), H("row-1") });
            });
            Assert.True(WriteLedger(npc, ledger));

            Reconcile(npc, ledger, new List<MemoryEntry> { Card("E1", "new", AnyDay) });
            string afterFirst = Raw(npc);

            // 第二轮：重新读取同一权威，重跑同一核对
            var reloaded = ReadLedger(npc);
            Reconcile(npc, reloaded, new List<MemoryEntry> { Card("E1", "new", AnyDay) });

            Assert.Equal(afterFirst, Raw(npc)); // 结果与已发布内容完全一致，且未重写

            // 冲突场景同样幂等
            var conflictLedger = ReadLedger(npc);
            conflictLedger.Days[AnyDay].PendingCommit = Pending(entryId: "E1", expectedOldHash: H("old"), newContentHash: H("new"));
            Assert.True(WriteLedger(npc, conflictLedger));
            Reconcile(npc, conflictLedger, new List<MemoryEntry> { Card("E1", "still-new", AnyDay) });
            string afterConflict = Raw(npc);
            var conflictReloaded = ReadLedger(npc);
            Reconcile(npc, conflictReloaded, new List<MemoryEntry> { Card("E1", "still-new", AnyDay) });

            Assert.Equal(afterConflict, Raw(npc));
            Assert.Equal("RecoveryConflict", conflictReloaded.Days[AnyDay].SuspensionReason);
            Assert.NotNull(conflictReloaded.Days[AnyDay].PendingCommit);
        }

        // ── 过期回收：保留今日和过去 7 日 ──

        [Fact]
        public void Reconcile_WithCurrentDay_ReclaimsCleanExpiredDaysKeepsWindow()
        {
            var npc = NewNpc();
            var ledger = new DailyDistillationLedger();
            ledger.Days[1] = Day(1);  // 过期（早于 10-7=3）
            ledger.Days[2] = Day(2);  // 过期
            ledger.Days[3] = Day(3);  // 窗口下界：保留
            ledger.Days[9] = Day(9);
            ledger.Days[10] = Day(10); // 今日
            Assert.True(WriteLedger(npc, ledger));

            Reconcile(npc, ledger, new List<MemoryEntry>(), currentGameDay: 10);

            Assert.Equal(new[] { 3, 9, 10 }, ledger.Days.Keys.OrderBy(k => k).ToArray());
        }

        [Fact]
        public void Reconcile_ExpiredDayWithSettledPending_IsReclaimed()
        {
            var npc = NewNpc();
            var ledger = new DailyDistillationLedger();
            ledger.Days[1] = Day(1, d =>
            {
                // create-pending 且目标日无卡片：主循环先完成核对（清除），回收随后删除
                d.PendingCommit = Pending(entryId: "P1", expectedOldHash: "", newContentHash: H("new"));
            });
            Assert.True(WriteLedger(npc, ledger));

            Reconcile(npc, ledger, new List<MemoryEntry>(), currentGameDay: 10);

            Assert.Empty(ledger.Days);
        }

        [Fact]
        public void Reconcile_ExpiredDayWithUnsettledPending_IsExpiredSuspendedAndKept()
        {
            var npc = NewNpc();
            var ledger = new DailyDistillationLedger();
            ledger.Days[1] = Day(1, d =>
            {
                // 无法证明归属：非 create pending 且目标日没有卡片
                d.PendingCommit = Pending(entryId: "E1", expectedOldHash: H("old"), newContentHash: H("new"));
            });
            Assert.True(WriteLedger(npc, ledger));

            Reconcile(npc, ledger, new List<MemoryEntry>(), currentGameDay: 10);

            Assert.True(ledger.Days.ContainsKey(1)); // 保留作为证据，不补造旧日记
            var day = ledger.Days[1];
            Assert.True(day.Suspended);
            Assert.Equal("Expired", day.SuspensionReason);
            Assert.NotNull(day.PendingCommit);
        }

        [Fact]
        public void Reconcile_ExpiredDaySuspendedThenRecovered_ConservesExpiredState()
        {
            var npc = NewNpc();
            var ledger = new DailyDistillationLedger();
            ledger.Days[1] = Day(1, d =>
            {
                d.Suspended = true;
                d.SuspensionReason = "Expired";
                d.PendingCommit = Pending(entryId: "E1", expectedOldHash: H("old"), newContentHash: H("new"));
            });
            Assert.True(WriteLedger(npc, ledger));
            string before = Raw(npc);

            Reconcile(npc, ledger, new List<MemoryEntry>(), currentGameDay: 10);

            Assert.Equal("Expired", ledger.Days[1].SuspensionReason);
            Assert.Equal(before, Raw(npc)); // 幂等：已记录 Expired 的状态不再改写
        }

        [Fact]
        public void Reconcile_ThreeParamOverload_NeverReclaims()
        {
            var npc = NewNpc();
            var ledger = new DailyDistillationLedger();
            ledger.Days[1] = Day(1);
            ledger.Days[2] = Day(2);
            Assert.True(WriteLedger(npc, ledger));

            TestEnvironment.WithWorldReady(true, () =>
                DailyDistillationStateStore.Reconcile(npc, ledger, new List<MemoryEntry>()));

            Assert.Equal(new[] { 1, 2 }, ledger.Days.Keys.OrderBy(k => k).ToArray()); // 未提供当前日：不回收
        }

        [Fact]
        public void Reconcile_NonDailyOrUnknownDayEntries_AreIgnored()
        {
            var npc = NewNpc();
            var ledger = new DailyDistillationLedger();
            ledger.Days[AnyDay] = Day(AnyDay); // 无所有权
            var entries = new List<MemoryEntry>
            {
                Card("W1", "weekly card", AnyDay),
                null
            };
            entries[0].Tier = MemoryTier.Weekly;      // 非 Daily 层
            entries.Add(Card("L1", "legacy", 0));     // 旧未知日期（CreatedDay=0）
            Assert.True(WriteLedger(npc, ledger));

            Reconcile(npc, ledger, entries);

            Assert.False(ledger.Days[AnyDay].Suspended); // 不参与核对，不触发 UnownedEntry
        }

        // ── 辅助 ──

        private static void WriteRaw(NPC npc, string raw)
        {
            TestEnvironment.WithWorldReady(true, () => { npc.modData[DailyDistillationStateStore.ModDataKey] = raw; });
        }
    }
}
