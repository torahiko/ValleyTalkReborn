using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using StardewModdingAPI;
using StardewValley;

namespace ValleytalkReborn
{
    /// <summary>
    /// DD403：NPC 日记蒸馏账本（D3 声明）。字段权威为 NPC.modData（ModDataKey），
    /// 内存对象是反序列化投影，仅在 TryWrite 成功后才视为已发布。
    /// 正文不入账本，仍由 TimelineSaveDataKey 负责。
    /// </summary>
    internal sealed class DailyDistillationLedger
    {
        public int Version { get; set; } = 1;
        public Dictionary<int, DailyDistillationDayState> Days { get; set; } = new();
    }

    /// <summary>单个目标日的蒸馏进度状态。TargetDay 为一基日历日（DateToDayNumber+1 口径）。</summary>
    internal sealed class DailyDistillationDayState
    {
        public int TargetDay { get; set; }
        public string EntryId { get; set; } = "";
        public string LastContentHash { get; set; } = "";
        public List<string> CoveredRowKeys { get; set; } = new();
        public string LastEvaluatedFingerprint { get; set; } = "";
        public int IntradayAttempts { get; set; }
        public int FinalAttempts { get; set; }
        public bool Suspended { get; set; }
        public string SuspensionReason { get; set; } = "";
        public bool FinalizationClosed { get; set; }
        public string FinalizationOutcome { get; set; } = "";
        public DailyPendingCommit PendingCommit { get; set; }
    }

    /// <summary>prepared 提交凭据：正文提交落盘前先持久化，用于崩溃后的归属核对。</summary>
    internal sealed class DailyPendingCommit
    {
        public string CommitId { get; set; }
        public string EntryId { get; set; }
        public string ExpectedOldHash { get; set; }
        public string NewContentHash { get; set; }
        public string InputFingerprint { get; set; }
        public List<string> CoveredRowKeys { get; set; }
        public bool IsFinal { get; set; }
    }

    /// <summary>
    /// DD403 状态存储：账本读写、所有权核对与崩溃恢复（Reconciliation）。
    /// 全部主线程；不提交正文、不调用 LLM、不触碰 player.modData、不清除未知 schema。
    /// </summary>
    internal static class DailyDistillationStateStore
    {
        internal const string ModDataKey = "ValleytalkReborn/DailyDistillation/v1";

        private const string ReasonDeleted = "Deleted";
        private const string ReasonEdited = "Edited";
        private const string ReasonMultipleEntries = "MultipleEntries";
        private const string ReasonUnownedEntry = "UnownedEntry";
        private const string ReasonRecoveryConflict = "RecoveryConflict";
        private const string ReasonExpired = "Expired";
        private const string OutcomeCommitted = "Committed";

        /// <summary>保留今日与过去 7 日（与调度器候选窗口一致）。</summary>
        private const int RetainedPastDays = 7;

        /// <summary>HashText 产物：小写十六进制 SHA256。</summary>
        private static readonly Regex HashPattern = new Regex("^[0-9a-f]{64}$", RegexOptions.Compiled);

        /// <summary>
        /// 读取 NPC 账本。key 不存在 → 新 Version=1 空 ledger 并返回 true；
        /// schema 不满足（畸形 JSON、未知 Version、负预算、字段矛盾）→ false 且原字符串保留。
        /// </summary>
        internal static bool TryRead(StardewValley.NPC npc, out DailyDistillationLedger ledger)
        {
            ledger = null;
            if (!IsReadable(npc))
            {
                Log.Debug($"[DailyDistill] Ledger read skipped (npcNull={npc == null}, worldReady={Context.IsWorldReady}, mainPlayer={Context.IsMainPlayer}).");
                return false;
            }

            try
            {
                if (!npc.modData.TryGetValue(ModDataKey, out string raw) || string.IsNullOrEmpty(raw))
                {
                    ledger = new DailyDistillationLedger();
                    return true;
                }

                var parsed = JsonConvert.DeserializeObject<DailyDistillationLedger>(raw);
                if (parsed == null || !IsValidSchema(parsed))
                {
                    Log.Error("[DailyDistill] Ledger schema invalid; original modData preserved and NPC distillation stopped for this session.");
                    return false;
                }

                Normalize(parsed);
                ledger = parsed;
                return true;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[DailyDistill] Ledger parse failed; original modData preserved.");
                ledger = null;
                return false;
            }
        }

        /// <summary>
        /// 发布账本：先在给定对象上完成校验与序列化，再单次替换该 NPC 的 D3 key。
        /// 失败返回 false 且原权威保持；不主动触发游戏保存。
        /// </summary>
        internal static bool TryWrite(StardewValley.NPC npc, DailyDistillationLedger ledger)
        {
            if (!IsReadable(npc))
            {
                Log.Debug($"[DailyDistill] Ledger write skipped (npcNull={npc == null}, worldReady={Context.IsWorldReady}, mainPlayer={Context.IsMainPlayer}).");
                return false;
            }
            if (ledger == null || !IsValidSchema(ledger))
            {
                Log.Error("[DailyDistill] Ledger write rejected: schema invalid; original modData preserved.");
                return false;
            }

            try
            {
                string json = JsonConvert.SerializeObject(ledger);
                npc.modData[ModDataKey] = json;
                return true;
            }
            catch (Exception ex)
            {
                Log.Error(ex, "[DailyDistill] Ledger write failed; original modData preserved.");
                return false;
            }
        }

        /// <summary>
        /// 恢复式核对（无回收）：对账本中每个日期执行 pending 核对与所有权检查；
        /// 有变化时回写一次。调度器未提供当前日，过期回收由四参重载承担。
        /// </summary>
        internal static void Reconcile(StardewValley.NPC npc, DailyDistillationLedger ledger, IReadOnlyList<MemoryEntry> dailyEntries)
        {
            Reconcile(npc, ledger, dailyEntries, 0);
        }

        /// <summary>
        /// 恢复式核对全量入口。dailyEntries 为该 NPC 全部 Daily 层条目（按 CreatedDay 分组核对）。
        /// 恢复只操作账本与 ModData：不发网络请求、不修改日记条目、任何恢复分支不增加次数。
        /// </summary>
        /// <param name="currentGameDay">调度器提供的当前一基日历日；0 或负数表示未知，跳过过期回收。</param>
        internal static void Reconcile(StardewValley.NPC npc, DailyDistillationLedger ledger, IReadOnlyList<MemoryEntry> dailyEntries, int currentGameDay)
        {
            if (npc == null || ledger == null || ledger.Days == null)
            {
                Log.Debug($"[DailyDistill] Reconcile skipped (npcNull={npc == null}, ledgerNull={ledger == null}).");
                return;
            }

            var byDay = GroupDailyEntries(dailyEntries);
            bool changed = false;

            foreach (int dayKey in ledger.Days.Keys.OrderBy(k => k).ToList())
            {
                if (!ledger.Days.TryGetValue(dayKey, out var day) || day == null)
                    continue;

                byDay.TryGetValue(dayKey, out var dayEntries);
                int entryCount = dayEntries?.Count ?? 0;

                if (day.PendingCommit != null)
                {
                    changed |= ReconcilePendingCommit(day, dayEntries, entryCount, out bool settled);
                    if (!settled)
                        continue; // RecoveryConflict：保留 pending 作为证据，本轮不再做所有权检查
                }

                changed |= ReconcileOwnership(day, dayEntries, entryCount);
            }

            if (currentGameDay > 0)
                changed |= ReclaimExpiredDays(ledger, currentGameDay);

            if (changed)
                TryWrite(npc, ledger); // 回写恢复前后的变化一次
        }

        // ── pending 核对：返回是否有账本变化；settled=是否已完成核对（A/B 恢复后才允许所有权检查落穿）──

        private static bool ReconcilePendingCommit(DailyDistillationDayState day, List<MemoryEntry> dayEntries, int entryCount, out bool settled)
        {
            var pending = day.PendingCommit;
            MemoryEntry single = entryCount == 1 ? dayEntries[0] : null;
            settled = true;

            // 已写未 ack：提交内容已在卡片上 → 恢复所有权、CoveredRowKeys 及 LastEvaluatedFingerprint
            if (single != null
                && string.Equals(single.Id, pending.EntryId, StringComparison.Ordinal)
                && HashEquals(single.Content, pending.NewContentHash))
            {
                day.EntryId = pending.EntryId;
                day.CoveredRowKeys = pending.CoveredRowKeys != null ? new List<string>(pending.CoveredRowKeys) : new List<string>();
                day.LastEvaluatedFingerprint = pending.InputFingerprint ?? "";
                day.LastContentHash = pending.NewContentHash;
                if (pending.IsFinal)
                {
                    day.FinalizationClosed = true;
                    day.FinalizationOutcome = OutcomeCommitted;
                }
                day.PendingCommit = null;
                Log.Information($"[DailyDistill] Recovered pending commit {pending.CommitId} on day {day.TargetDay} (final={pending.IsFinal}).");
                return true; // Changed + settled
            }

            // prepared 未写：正文仍为旧值 → 清除 pending，进度保持旧值
            if (single != null
                && string.Equals(single.Id, pending.EntryId, StringComparison.Ordinal)
                && !string.IsNullOrEmpty(pending.ExpectedOldHash)
                && HashEquals(single.Content, pending.ExpectedOldHash))
            {
                day.PendingCommit = null;
                Log.Information($"[DailyDistill] Cleared unapplied pending commit {pending.CommitId} on day {day.TargetDay}; progress preserved.");
                return true; // Changed + settled
            }

            // prepared 未写（首次创建）：目标日没有卡片 → 清除 pending，进度保持旧值
            if (entryCount == 0 && string.IsNullOrEmpty(pending.ExpectedOldHash))
            {
                day.PendingCommit = null;
                Log.Information($"[DailyDistill] Cleared unapplied create pending {pending.CommitId} on day {day.TargetDay}.");
                return true; // Changed + settled
            }

            // 其余：无法证明归属 → 暂停，保留 pending 作为证据，不再做所有权检查
            settled = false;
            if (!day.Suspended)
            {
                day.Suspended = true;
                day.SuspensionReason = ReasonRecoveryConflict;
                Log.Warning($"[DailyDistill] Pending commit {pending.CommitId} on day {day.TargetDay} cannot be proven (entries={entryCount}); suspended for supervisor review.");
                return true; // Changed，未 settled
            }
            return false; // 已暂停：状态不变，仅继续跳过所有权检查
        }

        // ── 所有权检查：返回是否有账本变化 ──

        private static bool ReconcileOwnership(DailyDistillationDayState day, List<MemoryEntry> dayEntries, int entryCount)
        {
            if (!string.IsNullOrEmpty(day.EntryId))
            {
                if (day.FinalizationClosed)
                    return false; // 已关闭：条目缺失保留关闭状态；被编辑保留玩家内容；均不变更状态

                var owned = dayEntries?.FirstOrDefault(e =>
                    e != null && string.Equals(e.Id, day.EntryId, StringComparison.Ordinal));
                if (owned == null)
                    return Suspend(day, ReasonDeleted, $"[DailyDistill] Day {day.TargetDay} entry {day.EntryId} deleted; suspended.");
                if (!HashEquals(owned.Content, day.LastContentHash))
                    return Suspend(day, ReasonEdited, $"[DailyDistill] Day {day.TargetDay} entry {day.EntryId} edited; suspended.");
                if (entryCount > 1)
                    return Suspend(day, ReasonMultipleEntries, $"[DailyDistill] Day {day.TargetDay} has {entryCount} daily entries; suspended.");
                return false;
            }

            // 无所有权且同日已有 Daily 条目：不通过 Source 猜测其来源
            if (entryCount > 0)
                return Suspend(day, ReasonUnownedEntry, $"[DailyDistill] Day {day.TargetDay} has unowned daily entries; suspended.");
            return false;
        }

        // ── 过期回收：保留今日与过去 7 日 ──

        private static bool ReclaimExpiredDays(DailyDistillationLedger ledger, int currentGameDay)
        {
            int oldestKept = currentGameDay - RetainedPastDays;
            bool changed = false;
            int removed = 0;

            foreach (int dayKey in ledger.Days.Keys.Where(k => k < oldestKept).OrderBy(k => k).ToList())
            {
                var day = ledger.Days[dayKey];
                if (day != null && day.PendingCommit != null)
                {
                    // 过期但 pending 未完成：记录 Expired 并暂停，保留作为证据，不删除
                    if (!day.Suspended || !string.Equals(day.SuspensionReason, ReasonExpired, StringComparison.Ordinal))
                    {
                        day.Suspended = true;
                        day.SuspensionReason = ReasonExpired;
                        Log.Warning($"[DailyDistill] Expired day {dayKey} keeps unsettled pending commit {day.PendingCommit.CommitId}; suspended for supervisor review.");
                        changed = true;
                    }
                    continue;
                }

                ledger.Days.Remove(dayKey);
                removed++;
                changed = true;
            }

            if (removed > 0)
                Log.Information($"[DailyDistill] Reclaimed {removed} expired ledger day(s) before day {oldestKept}.");
            return changed;
        }

        // ── 辅助 ──

        private static bool IsReadable(StardewValley.NPC npc)
            => npc != null && Context.IsWorldReady && Context.IsMainPlayer;

        private static bool Suspend(DailyDistillationDayState day, string reason, string message)
        {
            if (day.Suspended && string.Equals(day.SuspensionReason, reason, StringComparison.Ordinal))
                return false;
            day.Suspended = true;
            day.SuspensionReason = reason;
            Log.Information(message);
            return true;
        }

        private static bool HashEquals(string content, string expectedHash)
            => string.Equals(DailyDistillationSnapshotBuilder.HashText(content), expectedHash ?? "", StringComparison.Ordinal);

        private static Dictionary<int, List<MemoryEntry>> GroupDailyEntries(IReadOnlyList<MemoryEntry> dailyEntries)
        {
            var byDay = new Dictionary<int, List<MemoryEntry>>();
            if (dailyEntries == null)
                return byDay;
            for (int i = 0; i < dailyEntries.Count; i++)
            {
                var entry = dailyEntries[i];
                if (entry == null || entry.Tier != MemoryTier.Daily || entry.CreatedDay <= 0)
                    continue; // 非 Daily 层与旧未知日期（CreatedDay=0）不参与核对、不迁移
                if (!byDay.TryGetValue(entry.CreatedDay, out var list))
                    byDay[entry.CreatedDay] = list = new List<MemoryEntry>();
                list.Add(entry);
            }
            return byDay;
        }

        private static bool IsValidSchema(DailyDistillationLedger ledger)
        {
            if (ledger.Version != 1 || ledger.Days == null)
                return false;
            foreach (var pair in ledger.Days)
            {
                var day = pair.Value;
                if (day == null || day.TargetDay != pair.Key || pair.Key <= 0)
                    return false;
                if (day.IntradayAttempts < 0 || day.FinalAttempts < 0)
                    return false;
                if (!IsHashOrNull(day.LastContentHash) || !IsHashOrNull(day.LastEvaluatedFingerprint))
                    return false;
                if (!IsValidRowKeys(day.CoveredRowKeys))
                    return false;
                if (day.PendingCommit != null && !IsValidPending(day.PendingCommit))
                    return false;
            }
            return true;
        }

        private static bool IsValidPending(DailyPendingCommit pending)
        {
            if (string.IsNullOrEmpty(pending.CommitId) ||
                string.IsNullOrEmpty(pending.EntryId) ||
                string.IsNullOrEmpty(pending.NewContentHash))
                return false;
            if (!IsHashOrNull(pending.NewContentHash) ||
                !IsHashOrNull(pending.ExpectedOldHash) ||
                !IsHashOrNull(pending.InputFingerprint))
                return false;
            return IsValidRowKeys(pending.CoveredRowKeys);
        }

        private static bool IsValidRowKeys(List<string> keys)
        {
            if (keys == null)
                return true; // 反序列化缺省；读取成功后归一化为空表
            foreach (string key in keys)
            {
                if (string.IsNullOrEmpty(key) || !HashPattern.IsMatch(key))
                    return false;
            }
            return true;
        }

        private static bool IsHashOrNull(string value)
            => string.IsNullOrEmpty(value) || HashPattern.IsMatch(value);

        /// <summary>补齐反序列化缺省：null 字符串归空、null 列表归空表，使核对路径免于空引用。</summary>
        private static void Normalize(DailyDistillationLedger ledger)
        {
            foreach (var day in ledger.Days.Values)
            {
                day.EntryId ??= "";
                day.LastContentHash ??= "";
                day.CoveredRowKeys ??= new List<string>();
                day.LastEvaluatedFingerprint ??= "";
                day.SuspensionReason ??= "";
                day.FinalizationOutcome ??= "";
                if (day.PendingCommit != null)
                {
                    day.PendingCommit.ExpectedOldHash ??= "";
                    day.PendingCommit.InputFingerprint ??= "";
                    day.PendingCommit.CoveredRowKeys ??= new List<string>();
                }
            }
        }
    }
}
