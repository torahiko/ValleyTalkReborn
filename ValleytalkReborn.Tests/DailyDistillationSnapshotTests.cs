using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using StardewValley;
using ValleytalkReborn;
using Xunit;

namespace ValleytalkReborn.Tests
{
    /// <summary>
    /// DD402-SNAPSHOT：日记蒸馏纯函数快照与统一阈值。
    /// 覆盖：Guid 再生成不影响 fingerprint、序列化/重载 fingerprint 稳定、相同文本不同时间不合并、
    /// 礼物/偷听/结束标记统计、未覆盖计数多重集合扣减、300 条滚动后计数、确定性采样与 6000 预算、
    /// 一基日序号与跨季逆变换、失败路径空快照、GetHistoryNpcNames 键副本。
    /// 纯函数测试，不触碰 Game1 静态，可并行。
    /// </summary>
    public class DailyDistillationSnapshotTests
    {
        private static readonly StardewTime Day1 = new(1, Season.Spring, 1, 600);

        static DailyDistillationSnapshotTests()
        {
            // DialogueHistoryEntry 构造器读取 Context.IsWorldReady，其静态构造依赖
            // smapi-internal 的 SMAPI.Toolkit——先幂等安装 headless 程序集解析钩子。
            TestEnvironment.InstallHeadlessContext();
        }

        private static DialogueHistoryEntry Entry(string speaker, string text, SpeakerType type, StardewTime ts,
            string dialogueType = "", long utcMs = 0, string giftName = null, int giftTaste = -1)
        {
            var e = new DialogueHistoryEntry(speaker, text, type, ts, dialogueType);
            e.UtcTimestampMs = utcMs;
            e.GiftName = giftName;
            e.GiftTaste = giftTaste;
            return e;
        }

        private static DailyDistillationSnapshot Create(string npcName, int targetDay, StardewTime targetDate,
            List<DialogueHistoryEntry> entries, IEnumerable<string> covered, bool isChinese = false)
        {
            return DailyDistillationSnapshotBuilder.Create(
                npcName, targetDay, targetDate, entries, covered?.ToList(), isChinese);
        }

        /// <summary>一基日序号：第 1 年春 1 = 1，第 2 年春 1 = 113。</summary>
        private static int DayNumberOf(StardewTime t) => ((t.Year - 1) * 112) + ((int)t.Season * 28) + t.DayOfMonth;

        // ── 纯函数性：Guid 再生成 / 序列化重载不改变 fingerprint ──

        [Fact]
        public void RecreatedGuids_ProduceSameFingerprint()
        {
            var first = new List<DialogueHistoryEntry>
            {
                Entry("Abigail", "hello there", SpeakerType.NPC, new StardewTime(1, Season.Spring, 1, 900), "dialogue", 1000),
                Entry("Player", "hi", SpeakerType.Player, new StardewTime(1, Season.Spring, 1, 910), "dialogue", 1010)
            };
            var second = new List<DialogueHistoryEntry>
            {
                Entry("Abigail", "hello there", SpeakerType.NPC, new StardewTime(1, Season.Spring, 1, 900), "dialogue", 1000),
                Entry("Player", "hi", SpeakerType.Player, new StardewTime(1, Season.Spring, 1, 910), "dialogue", 1010)
            };
            // 两次构造各自 Guid.NewGuid()（get-only Id 不可控制）
            Assert.NotEqual(first[0].Id, second[0].Id);

            var a = Create("Abigail", 1, Day1, first, Array.Empty<string>());
            var b = Create("Abigail", 1, Day1, second, Array.Empty<string>());

            Assert.Equal(a.InputFingerprint, b.InputFingerprint);
        }

        [Fact]
        public void SerializedReload_PreservesFingerprint()
        {
            var entries = new List<DialogueHistoryEntry>
            {
                Entry("Abigail", "gift time", SpeakerType.NPC, new StardewTime(1, Season.Spring, 2, 1300), "gift", 2000, "Leek", 0),
                Entry("Abigail", "thanks for the leek", SpeakerType.NPC, new StardewTime(1, Season.Spring, 2, 1310), "dialogue", 2010)
            };
            var direct = Create("Abigail", 2, new StardewTime(1, Season.Spring, 2, 600), entries, Array.Empty<string>());

            // 生产存档往返：SerializableEntry DTO → JSON → ToEntry() 重建（Id 重新生成）
            string json = JsonConvert.SerializeObject(entries.Select(SerializableEntry.FromEntry).ToList());
            var reloaded = JsonConvert.DeserializeObject<List<SerializableEntry>>(json)
                .Select(se => se.ToEntry())
                .ToList();
            Assert.NotEqual(entries[0].Id, reloaded[0].Id);

            var round = Create("Abigail", 2, new StardewTime(1, Season.Spring, 2, 600), reloaded, Array.Empty<string>());

            Assert.Equal(direct.InputFingerprint, round.InputFingerprint);
            Assert.Equal(direct.QualifyingCount, round.QualifyingCount);
            Assert.Equal(direct.UncoveredQualifyingCount, round.UncoveredQualifyingCount);
            Assert.Equal(direct.Rows.Select(r => r.RowKey), round.Rows.Select(r => r.RowKey));
        }

        // ── 相同文本不同时间不合并 ──

        [Fact]
        public void SameText_DifferentTimes_AreNotMerged()
        {
            var entries = new List<DialogueHistoryEntry>
            {
                Entry("Abigail", "nice weather", SpeakerType.NPC, new StardewTime(1, Season.Spring, 1, 600), "dialogue", 100),
                Entry("Abigail", "nice weather", SpeakerType.NPC, new StardewTime(1, Season.Spring, 1, 1300), "dialogue", 200)
            };

            var snapshot = Create("Abigail", 1, Day1, entries, Array.Empty<string>());

            Assert.Equal(2, snapshot.Rows.Count);
            Assert.Equal(2, snapshot.QualifyingCount);
            Assert.Equal(2, snapshot.UncoveredQualifyingCount);
            Assert.NotEqual(snapshot.Rows[0].RowKey, snapshot.Rows[1].RowKey);
        }

        // ── 礼物 / 偷听 / 结束标记统计 ──

        [Fact]
        public void GiftEavesdropSessionEnd_StatisticsAreCorrect()
        {
            var entries = new List<DialogueHistoryEntry>
            {
                Entry("Abigail", "regular chat", SpeakerType.NPC, new StardewTime(1, Season.Spring, 1, 600), "dialogue", 100),
                Entry("Abigail", "gave a Leek", SpeakerType.NPC, new StardewTime(1, Season.Spring, 1, 700), "gift", 110, "Leek", 6),
                Entry("Player", "hmm?", SpeakerType.Player, new StardewTime(1, Season.Spring, 1, 800), "dialogue", 120),
                Entry("System", "farmer whispered nearby", SpeakerType.System, new StardewTime(1, Season.Spring, 1, 900), "eavesdrop", 130),
                Entry("System", "session ended", SpeakerType.System, new StardewTime(1, Season.Spring, 1, 1000), "session-end", 140)
            };

            var snapshot = Create("Abigail", 1, Day1, entries, Array.Empty<string>());

            // eavesdrop 与 session-end 被过滤，不进入 Rows
            Assert.Equal(3, snapshot.Rows.Count);
            Assert.DoesNotContain(snapshot.Rows, r => r.DialogueType == "eavesdrop");
            Assert.DoesNotContain(snapshot.Rows, r => r.DialogueType == "session-end");

            // 合格 = SpeakerType.NPC 且非 gift
            Assert.Equal(1, snapshot.QualifyingCount);
            Assert.Equal(1, snapshot.UncoveredQualifyingCount);
            var giftRow = Assert.Single(snapshot.Rows, r => r.DialogueType == "gift");
            Assert.False(giftRow.Qualifies);
            Assert.Equal(0, (int)giftRow.SpeakerType); // SpeakerType.NPC 的数值
        }

        // ── 未覆盖计数与多重集合扣减 ──

        [Fact]
        public void CoveredRowKeys_ReduceUncoveredCount_MultisetSemantics()
        {
            // 两条完全相同的材料（同时间、同 utcMs）→ 相同 RowKey，但计为两次交流
            var entries = new List<DialogueHistoryEntry>
            {
                Entry("Abigail", "duplicated line", SpeakerType.NPC, new StardewTime(1, Season.Spring, 1, 600), "dialogue", 100),
                Entry("Abigail", "duplicated line", SpeakerType.NPC, new StardewTime(1, Season.Spring, 1, 600), "dialogue", 100)
            };

            var all = Create("Abigail", 1, Day1, entries, Array.Empty<string>());
            Assert.Equal(2, all.QualifyingCount);
            Assert.Equal(2, all.UncoveredQualifyingCount);

            // covered 只含该 RowKey 一次 → 扣减一次，剩一次未覆盖（不折叠成一个交流）
            var once = Create("Abigail", 1, Day1, entries, new[] { all.Rows[0].RowKey });
            Assert.Equal(1, once.UncoveredQualifyingCount);

            // covered 含该 RowKey 两次 → 全部扣减
            var twice = Create("Abigail", 1, Day1, entries, new[] { all.Rows[0].RowKey, all.Rows[1].RowKey });
            Assert.Equal(0, twice.UncoveredQualifyingCount);
        }

        [Fact]
        public void NewNpcRow_IncreasesUncoveredCount()
        {
            var entries = new List<DialogueHistoryEntry>
            {
                Entry("Abigail", "first", SpeakerType.NPC, new StardewTime(1, Season.Spring, 1, 600), "dialogue", 100),
                Entry("Abigail", "second", SpeakerType.NPC, new StardewTime(1, Season.Spring, 1, 700), "dialogue", 110)
            };

            var before = Create("Abigail", 1, Day1, entries, Array.Empty<string>());
            Assert.Equal(2, before.UncoveredQualifyingCount);

            var covered = before.Rows.Select(r => r.RowKey).ToList();
            var settled = Create("Abigail", 1, Day1, entries, covered);
            Assert.Equal(0, settled.UncoveredQualifyingCount);

            entries.Add(Entry("Abigail", "third", SpeakerType.NPC, new StardewTime(1, Season.Spring, 1, 800), "dialogue", 120));
            var after = Create("Abigail", 1, Day1, entries, covered);
            Assert.Equal(3, after.QualifyingCount);
            Assert.Equal(1, after.UncoveredQualifyingCount);
        }

        // ── 300 条滚动移除早段后新行仍可计数 ──

        [Fact]
        public void RollingRemoval_NewRowsStillCounted()
        {
            var entries = Enumerable.Range(0, 300)
                .Select(i => Entry("Abigail", $"line {i:000}", SpeakerType.NPC,
                    new StardewTime(1, Season.Spring, 1, 600), "dialogue", i))
                .Cast<DialogueHistoryEntry>()
                .ToList();

            var full = Create("Abigail", 1, Day1, entries, Array.Empty<string>());
            Assert.Equal(300, full.Rows.Count); // Rows 保留全部过滤后行，不受采样限制

            // covered 覆盖前 290 条 RowKey（模拟上一轮快照已评估范围）
            var covered = full.Rows.Take(290).Select(r => r.RowKey).ToList();

            // 滚动：移除最早 100 条（等价 RemoveRange(0, 100)），追加 1 条新 NPC 行
            var rolled = entries.Skip(100).ToList();
            rolled.Add(Entry("Abigail", "brand new line", SpeakerType.NPC, new StardewTime(1, Season.Spring, 1, 2200), "dialogue", 999));

            var snapshot = Create("Abigail", 1, Day1, rolled, covered);

            Assert.Equal(201, snapshot.QualifyingCount); // 剩余 200 + 新 1
            // 未覆盖 = 原第 291–300 条（10 条，超出前 290 covered 范围）+ 新行 1 条
            Assert.Equal(11, snapshot.UncoveredQualifyingCount);
            Assert.Contains(snapshot.Rows, r => r.Text == "brand new line");
        }

        // ── 确定性采样 + 6000 预算 ──

        [Fact]
        public void Sampling_IncludesEarlyMiddleLate_AndRespectsBudget()
        {
            var entries = Enumerable.Range(0, 300)
                .Select(i => Entry("Abigail", $"line {i:000} filler", SpeakerType.NPC,
                    new StardewTime(1, Season.Spring, 1, 600), "dialogue", i))
                .Cast<DialogueHistoryEntry>()
                .ToList();

            var snapshot = Create("Abigail", 1, Day1, entries, Array.Empty<string>());

            Assert.True(snapshot.InputTruncated);
            Assert.Equal(300, snapshot.Rows.Count);
            Assert.Equal(48, snapshot.PromptLines.Count);

            // 最早 8：首行 = line 000；最近 16：末行 = line 299；中间均匀采样：j=0 → index 8
            Assert.Contains("line 000", snapshot.PromptLines[0]);
            Assert.Contains("line 299", snapshot.PromptLines[47]);
            Assert.Contains("line 008", snapshot.PromptLines[8]);

            // 严格满足预算
            int totalUnits = snapshot.PromptLines.Sum(l => l.Length);
            Assert.True(totalUnits <= 6000, $"PromptLines total {totalUnits} exceeds 6000");
        }

        [Fact]
        public void BudgetTruncation_KeepsAllLines_NoBrokenSurrogates()
        {
            // 30 行（≤48 不触发采样）× 每条 300 个 emoji（每 element 为 surrogate pair，4 code units）
            // → 触发 96-element 截断与 6000 预算截断，但所有选中行保留
            string emojiText = string.Concat(Enumerable.Repeat("😀", 300));
            var entries = Enumerable.Range(0, 30)
                .Select(i => Entry("Abigail", emojiText + $" #{i}", SpeakerType.NPC,
                    new StardewTime(1, Season.Spring, 1, 600), "dialogue", i))
                .ToList();

            var snapshot = Create("Abigail", 1, Day1, entries, Array.Empty<string>(), isChinese: true);

            Assert.True(snapshot.InputTruncated);
            Assert.Equal(30, snapshot.PromptLines.Count); // 所有选中行保留

            int totalUnits = snapshot.PromptLines.Sum(l => l.Length);
            Assert.True(totalUnits <= 6000, $"PromptLines total {totalUnits} exceeds 6000");

            // 绝不拆 surrogate pair：任何 high surrogate 后必须紧跟 low surrogate
            foreach (var line in snapshot.PromptLines)
            {
                for (int i = 0; i < line.Length; i++)
                {
                    if (char.IsHighSurrogate(line[i]))
                        Assert.True(i + 1 < line.Length && char.IsLowSurrogate(line[i + 1]),
                            "High surrogate must not be split from its low surrogate");
                }
            }

            // 行时刻与角色标签保留
            Assert.All(snapshot.PromptLines, l => Assert.Contains("Abigail", l));
            Assert.Contains("[6:00]", snapshot.PromptLines[0]);
        }

        [Fact]
        public void LongText_ClippedTo96TextElements_WithEllipsis()
        {
            string longText = string.Concat(Enumerable.Repeat("字", 200));
            var entries = new List<DialogueHistoryEntry>
            {
                Entry("Abigail", longText, SpeakerType.NPC, new StardewTime(1, Season.Spring, 1, 600), "dialogue", 100)
            };

            var snapshot = Create("Abigail", 1, Day1, entries, Array.Empty<string>(), isChinese: true);

            var line = Assert.Single(snapshot.PromptLines);
            // 文本部分：95 个"字" + 省略号 = 96 text elements（不含前缀）
            string textPart = line.Substring(line.IndexOf('：') + 1);
            Assert.Equal(96, new System.Globalization.StringInfo(textPart).LengthInTextElements);
            Assert.EndsWith("…", textPart);
            Assert.True(snapshot.InputTruncated);
        }

        [Fact]
        public void SpeakerLabel_ClippedTo32CodeUnits()
        {
            string longName = new('A', 40);
            var entries = new List<DialogueHistoryEntry>
            {
                Entry(longName, "hello", SpeakerType.NPC, new StardewTime(1, Season.Spring, 1, 600), "dialogue", 100)
            };

            var snapshot = Create(longName, 1, Day1, entries, Array.Empty<string>());

            var line = Assert.Single(snapshot.PromptLines);
            // 标签限长 32 code units：截断后不再包含第 33 个 'A'
            Assert.DoesNotContain(new string('A', 33), line);
            Assert.Contains(new string('A', 32), line);
        }

        // ── 一基目标日序号与跨季逆变换 ──

        [Fact]
        public void Calendar_OneBasedDayNumber_AndInverseTransform()
        {
            var spring1 = new StardewTime(1, Season.Spring, 1, 600);
            Assert.Equal(1, DayNumberOf(spring1));

            var nextYearSpring1 = spring1.AddDays(112);
            Assert.Equal(new StardewTime(2, Season.Spring, 1, 600), nextYearSpring1);
            Assert.Equal(113, DayNumberOf(nextYearSpring1));

            // 跨季正变换
            var summer1 = spring1.AddDays(28);
            Assert.Equal(Season.Summer, summer1.Season);
            Assert.Equal(1, summer1.DayOfMonth);
            Assert.Equal(29, DayNumberOf(summer1));

            // 逆变换一致
            Assert.Equal(spring1, summer1.AddDays(-28));
            Assert.Equal(spring1, nextYearSpring1.AddDays(-112));

            // 快照按 targetDate 精确过滤跨季日期
            var entries = new List<DialogueHistoryEntry>
            {
                Entry("Abigail", "spring entry", SpeakerType.NPC, new StardewTime(1, Season.Spring, 28, 600), "dialogue", 100),
                Entry("Abigail", "summer entry", SpeakerType.NPC, new StardewTime(1, Season.Summer, 1, 600), "dialogue", 110)
            };
            var summerSnapshot = Create("Abigail", DayNumberOf(summer1), summer1, entries, Array.Empty<string>());
            var springSnapshot = Create("Abigail", 28, new StardewTime(1, Season.Spring, 28, 600), entries, Array.Empty<string>());

            Assert.Equal(1, summerSnapshot.QualifyingCount);
            Assert.Contains(summerSnapshot.Rows, r => r.Text == "summer entry");
            Assert.Equal(1, springSnapshot.QualifyingCount);
            Assert.Contains(springSnapshot.Rows, r => r.Text == "spring entry");
        }

        // ── 失败路径：BUG → Warn → 明确空快照 ──

        [Theory]
        [InlineData("", 1)]
        [InlineData("   ", 1)]
        [InlineData("Abigail", 0)]
        [InlineData("Abigail", -5)]
        public void InvalidInput_ReturnsExplicitEmptySnapshot(string npcName, int targetDay)
        {
            var entries = new List<DialogueHistoryEntry>
            {
                Entry("Abigail", "ignored", SpeakerType.NPC, Day1, "dialogue", 100)
            };

            var snapshot = Create(npcName, targetDay, Day1, entries, Array.Empty<string>());

            Assert.Empty(snapshot.Rows);
            Assert.Empty(snapshot.PromptLines);
            Assert.Equal(0, snapshot.QualifyingCount);
            Assert.Equal(0, snapshot.UncoveredQualifyingCount);
            Assert.False(snapshot.InputTruncated);
            Assert.False(string.IsNullOrEmpty(snapshot.InputFingerprint));
        }

        [Fact]
        public void NullCollections_ReturnEmptySnapshot()
        {
            var entries = new List<DialogueHistoryEntry>
            {
                Entry("Abigail", "ignored", SpeakerType.NPC, Day1, "dialogue", 100)
            };

            var nullEntries = DailyDistillationSnapshotBuilder.Create("Abigail", 1, Day1, null, new List<string>(), true);
            var nullCovered = DailyDistillationSnapshotBuilder.Create("Abigail", 1, Day1, entries, null, true);

            Assert.Empty(nullEntries.Rows);
            Assert.Empty(nullEntries.PromptLines);
            Assert.Empty(nullCovered.Rows);
            Assert.Empty(nullCovered.PromptLines);
        }

        [Fact]
        public void NullEntries_AreIgnored_RestKept()
        {
            var entries = new List<DialogueHistoryEntry>
            {
                null,
                Entry("Abigail", "kept", SpeakerType.NPC, Day1, "dialogue", 100),
                null
            };

            var snapshot = Create("Abigail", 1, Day1, entries, Array.Empty<string>());

            Assert.Equal(1, snapshot.Rows.Count);
            Assert.Equal("kept", snapshot.Rows[0].Text);
            Assert.Equal(1, snapshot.QualifyingCount);
        }

        [Fact]
        public void NoMaterialForTargetDay_ReturnsEmptySnapshot()
        {
            var entries = new List<DialogueHistoryEntry>
            {
                Entry("Abigail", "other day", SpeakerType.NPC, new StardewTime(1, Season.Spring, 2, 600), "dialogue", 100)
            };

            var snapshot = Create("Abigail", 1, Day1, entries, Array.Empty<string>());

            Assert.Empty(snapshot.Rows);
            Assert.Empty(snapshot.PromptLines);
            Assert.Equal(0, snapshot.QualifyingCount);
            Assert.False(snapshot.InputTruncated);
        }

        // ── HashText ──

        [Fact]
        public void HashText_IsDeterministicLowercaseHex()
        {
            string h1 = DailyDistillationSnapshotBuilder.HashText("abc");
            string h2 = DailyDistillationSnapshotBuilder.HashText("abc");
            string h3 = DailyDistillationSnapshotBuilder.HashText("abd");

            Assert.Equal(h1, h2);
            Assert.NotEqual(h1, h3);
            Assert.Equal(64, h1.Length); // SHA256 → 64 hex chars
            Assert.True(h1.All(c => (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')), "hash must be lowercase hex");
        }

        // ── GetHistoryNpcNames：既有锁中返回字典键副本 ──

        [Fact]
        public void GetHistoryNpcNames_ReturnsKeyCopy()
        {
            // 单例 + private 构造器：向既有 _history 字典追加带前缀测试键，finally 清理还原
            var manager = DialogueHistoryManager.Instance;
            var field = typeof(DialogueHistoryManager).GetField("_history", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(field);
            var dict = Assert.IsAssignableFrom<IDictionary<string, List<DialogueHistoryEntry>>>(field.GetValue(manager));

            dict["__testAbigail"] = new List<DialogueHistoryEntry>();
            dict["__testSebastian"] = new List<DialogueHistoryEntry>();
            try
            {
                var names = manager.GetHistoryNpcNames();
                Assert.Contains("__testAbigail", names);
                Assert.Contains("__testSebastian", names);
                Assert.Equal(dict.Count, names.Count);

                // 返回的是副本：外部修改不影响内部状态
                names.Clear();
                Assert.Equal(dict.Count, manager.GetHistoryNpcNames().Count);
            }
            finally
            {
                dict.Remove("__testAbigail");
                dict.Remove("__testSebastian");
            }
        }
    }
}
