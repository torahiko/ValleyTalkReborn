using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;

namespace ValleytalkReborn
{
    /// <summary>
    /// 目标日单条历史行的去重键与统计标记（DD402 纯快照，Memory 层，全部为标量深复制）。
    /// </summary>
    internal sealed class DailyDialogueLine
    {
        public string RowKey { get; init; }
        public SpeakerType SpeakerType { get; init; }
        public string Text { get; init; }
        public string DialogueType { get; init; }
        public int TimeOfDay { get; init; }
        public bool Qualifies { get; init; }
    }

    /// <summary>
    /// 日记蒸馏的不可变输入快照。全部字段属于 Memory：主线程采集后仅被后台只读消费，
    /// 不持有 NPC、Game1、菜单或可变历史条目引用。
    /// </summary>
    internal sealed class DailyDistillationSnapshot
    {
        public string NpcName { get; init; }
        public int TargetDay { get; init; }
        public string InputFingerprint { get; init; }
        public int QualifyingCount { get; init; }
        public int UncoveredQualifyingCount { get; init; }
        public IReadOnlyList<DailyDialogueLine> Rows { get; init; }
        public IReadOnlyList<string> PromptLines { get; init; }
        public bool InputTruncated { get; init; }
    }

    /// <summary>
    /// DD402 稳定快照构建器：纯函数——不访问 Game1、不调用 LLM、不写 ModData、不改历史。
    /// 相同输入重复执行得到完全相同的结果（确定性采样与预算截断）。
    /// </summary>
    internal static class DailyDistillationSnapshotBuilder
    {
        private const int MaxSampledRows = 48;
        private const int KeepFirstRows = 8;
        private const int KeepLastRows = 16;
        private const int MiddleSampleSlots = 24;
        private const int MaxTextElementsPerLine = 96;
        private const int MaxLabelCodeUnits = 32;
        private const int TotalBudgetCodeUnits = 6000;
        private const string Ellipsis = "…";

        /// <summary>
        /// 构建目标日的蒸馏快照。
        /// </summary>
        /// <param name="npcName">NPC 名（内部做 Trim 规范化）。</param>
        /// <param name="targetDay">一基目标日序号：第 1 年春 1 = 1，第 2 年春 1 = 113（(Year-1)*112 + Season*28 + DayOfMonth）。</param>
        /// <param name="targetDate">目标日（年/季/日用于过滤，TimeOfDay 不参与过滤）。</param>
        /// <param name="entries">调用方提供的完整保留历史列表（按既有写入顺序），不使用 GetRecentHistory 截断窗口。</param>
        /// <param name="coveredRowKeys">此前快照已评估过的 RowKey 集合，按多重集合扣减。</param>
        /// <param name="isChinese">PromptLine 游戏时刻格式（中文 24 小时制 / 英文 12 小时制）。</param>
        internal static DailyDistillationSnapshot Create(
            string npcName,
            int targetDay,
            StardewTime targetDate,
            IReadOnlyList<DialogueHistoryEntry> entries,
            IReadOnlyList<string> coveredRowKeys,
            bool isChinese)
        {
            if (string.IsNullOrWhiteSpace(npcName) || targetDay <= 0 || entries == null || coveredRowKeys == null)
            {
                Log.Warning($"[DailyDistillationSnapshot] Invalid snapshot input (npcNameEmpty={string.IsNullOrWhiteSpace(npcName)}, targetDay={targetDay}, entriesNull={entries == null}, coveredNull={coveredRowKeys == null}); returning empty snapshot.");
                return EmptySnapshot(npcName, targetDay);
            }

            string canonicalName = npcName.Trim();

            // ── 1. 按既有写入顺序过滤：目标年/季/日 + 非空文本 + 排除 eavesdrop / session-end ──
            var filteredEntries = new List<DialogueHistoryEntry>();
            var fullRows = new List<DailyDialogueLine>();
            bool sawNullEntry = false;
            foreach (var entry in entries)
            {
                if (entry == null)
                {
                    sawNullEntry = true;
                    continue;
                }

                var ts = entry.Timestamp;
                if (ts.Year != targetDate.Year || ts.Season != targetDate.Season || ts.DayOfMonth != targetDate.DayOfMonth)
                    continue;
                if (string.IsNullOrWhiteSpace(entry.Text))
                    continue;

                string dialogueType = entry.DialogueType ?? string.Empty;
                if (dialogueType.Equals("eavesdrop", StringComparison.OrdinalIgnoreCase) ||
                    dialogueType.Equals("session-end", StringComparison.OrdinalIgnoreCase))
                    continue;

                filteredEntries.Add(entry);
                fullRows.Add(new DailyDialogueLine
                {
                    RowKey = BuildRowKey(entry, dialogueType),
                    SpeakerType = entry.SpeakerType,
                    Text = entry.Text,
                    DialogueType = dialogueType,
                    TimeOfDay = ts.TimeOfDay,
                    Qualifies = entry.SpeakerType == SpeakerType.NPC
                                && !dialogueType.Equals("gift", StringComparison.OrdinalIgnoreCase)
                });
            }

            if (sawNullEntry)
                Log.Warning("[DailyDistillationSnapshot] Null history entry ignored while building daily snapshot.");

            // ── 2. 输入指纹（目标日 + 规范 NPC 名 + 全部 RowKey 有序数组）与合格/未覆盖统计 ──
            string fingerprint = HashText(JsonConvert.SerializeObject(new object[]
            {
                targetDate.Year,
                (int)targetDate.Season,
                targetDate.DayOfMonth,
                canonicalName,
                fullRows.Select(r => r.RowKey).ToArray()
            }));

            int qualifyingCount = 0;
            int uncoveredCount = 0;
            var coveredCounts = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (var key in coveredRowKeys)
                coveredCounts[key] = coveredCounts.TryGetValue(key, out int count) ? count + 1 : 1;

            foreach (var row in fullRows)
            {
                if (!row.Qualifies)
                    continue;
                qualifyingCount++;
                if (coveredCounts.TryGetValue(row.RowKey, out int remaining) && remaining > 0)
                    coveredCounts[row.RowKey] = remaining - 1;
                else
                    uncoveredCount++;
            }

            // ── 3. 确定性采样（仅影响 PromptLines；Rows 保留全部过滤后行） ──
            List<int> selectedIndices;
            bool inputTruncated = false;
            int n = fullRows.Count;
            if (n <= MaxSampledRows)
            {
                selectedIndices = Enumerable.Range(0, n).ToList();
            }
            else
            {
                inputTruncated = true;
                var chosen = new SortedSet<int>();
                for (int i = 0; i < KeepFirstRows; i++)
                    chosen.Add(i);
                for (int i = n - KeepLastRows; i < n; i++)
                    chosen.Add(i);
                int middleCount = n - KeepFirstRows - KeepLastRows;
                for (int j = 0; j < MiddleSampleSlots; j++)
                    chosen.Add(KeepFirstRows + (int)((long)j * (middleCount - 1) / (MiddleSampleSlots - 1)));
                selectedIndices = chosen.ToList();
            }

            // ── 4. PromptLines：游戏时刻 + 角色标签(≤32 code units) + 文本(≤96 text elements)，总预算 ≤6000 code units ──
            var prefixes = new List<string>(selectedIndices.Count);
            var clipped96 = new List<string>(selectedIndices.Count);
            foreach (int idx in selectedIndices)
            {
                var row = fullRows[idx];
                string timeText = FormatGameTime(row.TimeOfDay, isChinese);
                string label = ClipLabel(filteredEntries[idx].SpeakerName);
                prefixes.Add(isChinese ? $"[{timeText}] {label}：" : $"[{timeText}] {label}: ");

                string clipped = ClipTextElements(row.Text, MaxTextElementsPerLine, addEllipsis: true);
                if (!ReferenceEquals(clipped, row.Text))
                    inputTruncated = true;
                clipped96.Add(clipped);
            }

            int totalUnits = prefixes.Sum(p => p.Length) + clipped96.Sum(t => t.Length);
            var finalTexts = clipped96;
            if (totalUnits > TotalBudgetCodeUnits)
            {
                inputTruncated = true;
                int remaining = TotalBudgetCodeUnits - prefixes.Sum(p => p.Length);
                int perLine = selectedIndices.Count > 0 ? remaining / selectedIndices.Count : 0;
                finalTexts = new List<string>(selectedIndices.Count);
                for (int i = 0; i < selectedIndices.Count; i++)
                {
                    if (perLine <= 0)
                    {
                        finalTexts.Add(string.Empty);
                    }
                    else if (clipped96[i].Length <= perLine)
                    {
                        finalTexts.Add(clipped96[i]);
                    }
                    else
                    {
                        finalTexts.Add(ClipTextToCodeUnits(filteredEntries[selectedIndices[i]].Text, perLine));
                    }
                }
            }

            var promptLines = new List<string>(selectedIndices.Count);
            for (int i = 0; i < selectedIndices.Count; i++)
                promptLines.Add(prefixes[i] + finalTexts[i]);

            return new DailyDistillationSnapshot
            {
                NpcName = canonicalName,
                TargetDay = targetDay,
                InputFingerprint = fingerprint,
                QualifyingCount = qualifyingCount,
                UncoveredQualifyingCount = uncoveredCount,
                Rows = fullRows.ToArray(),
                PromptLines = promptLines.ToArray(),
                InputTruncated = inputTruncated
            };
        }

        /// <summary>SHA256 / UTF-8 / 小写十六进制（RowKey 与 InputFingerprint 共用）。</summary>
        internal static string HashText(string text)
        {
            using var sha256 = SHA256.Create();
            byte[] bytes = sha256.ComputeHash(Encoding.UTF8.GetBytes(text ?? string.Empty));
            var sb = new StringBuilder(bytes.Length * 2);
            foreach (byte b in bytes)
                sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
            return sb.ToString();
        }

        /// <summary>RowKey：规范 JSON 数组的 SHA256。空串统一为空串，数值由 Newtonsoft 以不变式表示，无缩进。</summary>
        private static string BuildRowKey(DialogueHistoryEntry entry, string dialogueType)
        {
            var ts = entry.Timestamp;
            string canonicalJson = JsonConvert.SerializeObject(new object[]
            {
                ts.Year,
                (int)ts.Season,
                ts.DayOfMonth,
                ts.TimeOfDay,
                (int)entry.SpeakerType,
                dialogueType,
                entry.SpeakerName ?? string.Empty,
                entry.Text ?? string.Empty,
                entry.UtcTimestampMs,
                entry.GiftName ?? string.Empty,
                entry.GiftTaste
            });
            return HashText(canonicalJson);
        }

        private static DailyDistillationSnapshot EmptySnapshot(string npcName, int targetDay)
        {
            return new DailyDistillationSnapshot
            {
                NpcName = npcName?.Trim() ?? string.Empty,
                TargetDay = targetDay,
                InputFingerprint = HashText("[]"),
                QualifyingCount = 0,
                UncoveredQualifyingCount = 0,
                Rows = Array.Empty<DailyDialogueLine>(),
                PromptLines = Array.Empty<string>(),
                InputTruncated = false
            };
        }

        /// <summary>游戏时刻格式化（不依赖 Game1）：中文 24 小时制，英文 12 小时制 AM/PM。</summary>
        private static string FormatGameTime(int timeOfDay, bool isChinese)
        {
            int hours = timeOfDay / 100;
            int minutes = timeOfDay % 100;
            if (isChinese)
                return $"{hours}:{minutes:00}";
            int hoursInDay = ((hours % 24) + 24) % 24;
            bool isAm = hoursInDay < 12;
            int h12 = hoursInDay % 12;
            if (h12 == 0)
                h12 = 12;
            return $"{h12}:{minutes:00} {(isAm ? "AM" : "PM")}";
        }

        /// <summary>角色标签限长（32 个 UTF-16 code unit），不拆代理对。</summary>
        private static string ClipLabel(string label)
        {
            string text = label ?? string.Empty;
            if (text.Length <= MaxLabelCodeUnits)
                return text;
            int cut = MaxLabelCodeUnits;
            if (char.IsHighSurrogate(text[cut - 1]) && char.IsLowSurrogate(text[cut]))
                cut--;
            return text.Substring(0, cut);
        }

        /// <summary>按 Unicode text element（含组合字符/surrogate pair 整体）截断；截断附省略号并计入上限。</summary>
        private static string ClipTextElements(string text, int maxElements, bool addEllipsis)
        {
            string source = text ?? string.Empty;
            var info = new StringInfo(source);
            if (info.LengthInTextElements <= maxElements)
                return source;
            int keep = addEllipsis ? maxElements - 1 : maxElements;
            if (keep <= 0)
                return addEllipsis ? Ellipsis : string.Empty;
            return info.SubstringByTextElements(0, keep) + (addEllipsis ? Ellipsis : string.Empty);
        }

        /// <summary>按 code unit 预算截断（text element 边界对齐，省略号计入预算），绝不拆代理对或组合字符。</summary>
        private static string ClipTextToCodeUnits(string text, int maxUnits)
        {
            if (maxUnits <= 0)
                return string.Empty;
            string source = text ?? string.Empty;
            if (source.Length <= maxUnits)
                return source;
            int bodyBudget = maxUnits - Ellipsis.Length;
            var sb = new StringBuilder(source.Length);
            var enumerator = StringInfo.GetTextElementEnumerator(source);
            while (enumerator.MoveNext())
            {
                string element = (string)enumerator.Current;
                if (sb.Length + element.Length > bodyBudget)
                    return sb.ToString() + Ellipsis;
                sb.Append(element);
            }
            return source;
        }
    }
}
