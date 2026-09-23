using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using ValleytalkReborn.Services;

namespace ValleytalkReborn;

/// <summary>
/// Tracks cross-day consecutive interaction patterns per NPC:
///   - Consecutive talk days
///   - Consecutive gift days
///   - Consecutive same-item gift days
///
/// Refactored with positive framing and strict EN-Fallback for unsupported locales.
/// </summary>
internal static class ConsecutiveTalkTracker
{
    private class TrackerData
    {
        public Dictionary<string, NpcStreakEntry> Streaks { get; set; }
            = new(StringComparer.OrdinalIgnoreCase);
    }

    private class NpcStreakEntry
    {
        public int    TalkStreak         { get; set; } = 0;
        public int    GiftStreak         { get; set; } = 0;
        public string LastGiftItemId     { get; set; } = "";
        public int    SameItemGiftStreak { get; set; } = 0;
        public string LastUpdatedDate    { get; set; } = "";
    }

    private static readonly Dictionary<string, string> _todayGifts
        = new(StringComparer.OrdinalIgnoreCase);

    private static TrackerData _data = new();
    private static bool _initialized = false;

    /// <summary>
    /// Checks if current game language is Chinese (supports zh-CN, zh-TW, etc.).
    /// Defaults to English fallback for all other languages.
    /// </summary>
    private static bool IsChineseLanguage =>
        LocalizedContentManager.CurrentLanguageCode.ToString().StartsWith("zh", StringComparison.OrdinalIgnoreCase);

    private static string? FilePath =>
        StorageLayout.LocalBaseDir is null || string.IsNullOrEmpty(Constants.SaveFolderName)
            ? null
            : Path.Combine(StorageLayout.LocalBaseDir!, $"ConsecutiveStreak_{Constants.SaveFolderName}.json");

    // ── Lifecycle ─────────────────────────────────────────────────────────

    public static void Initialize()
    {
        if (_initialized || ModEntry.SHelper == null) return;
        ModEntry.SHelper.Events.GameLoop.SaveLoaded += OnSaveLoaded;
        ModEntry.SHelper.Events.GameLoop.Saving     += OnSaving;
        ModEntry.SHelper.Events.GameLoop.DayStarted += OnDayStarted;
        ModEntry.SHelper.Events.GameLoop.DayEnding  += OnDayEnding;
        _initialized = true;
        ModEntry.SMonitor?.Log("[ConsecutiveTalkTracker] Initialized.", LogLevel.Debug);
    }

    public static void Cleanup()
    {
        if (!_initialized || ModEntry.SHelper == null) return;
        ModEntry.SHelper.Events.GameLoop.SaveLoaded -= OnSaveLoaded;
        ModEntry.SHelper.Events.GameLoop.Saving     -= OnSaving;
        ModEntry.SHelper.Events.GameLoop.DayStarted -= OnDayStarted;
        ModEntry.SHelper.Events.GameLoop.DayEnding  -= OnDayEnding;
        _todayGifts.Clear();
        _initialized = false;
    }

    // ── Event handlers ────────────────────────────────────────────────────

    private static void OnSaveLoaded(object sender, SaveLoadedEventArgs e)
    {
        string? path = FilePath;
        if (path == null) return;

        // 一次性单向迁移遗留数据
        string legacyPath = Path.Combine(StorageLayout.ModDirectory, $"data/ConsecutiveStreak_{Constants.SaveFolderName}.json");
        StorageLayout.MigrateLegacyFile(legacyPath, path, "ConsecutiveStreak");

        _data = ModEntry.SHelper.Data.ReadJsonFile<TrackerData>(path) ?? new TrackerData();
        _todayGifts.Clear();
        ModEntry.SMonitor?.Log("[ConsecutiveTalkTracker] Data loaded.", LogLevel.Debug);
    }

    private static void OnSaving(object sender, SavingEventArgs e)
    {
        string? path = FilePath;
        if (path == null)
        {
            ModEntry.SMonitor?.Log("[ConsecutiveTalkTracker] OnSaving: no save loaded, skipping persistence.", LogLevel.Trace);
            return;
        }
        ModEntry.SHelper.Data.WriteJsonFile(path, _data);
        ModEntry.SMonitor?.Log("[ConsecutiveTalkTracker] Data saved.", LogLevel.Debug);
    }

    private static void OnDayStarted(object sender, DayStartedEventArgs e)
    {
        _todayGifts.Clear();
    }

    private static void OnDayEnding(object sender, DayEndingEventArgs e)
    {
        try { FlushAllToday(); }
        catch (Exception ex)
        {
            ModEntry.SMonitor?.Log($"[ConsecutiveTalkTracker] DayEnding flush failed: {ex.Message}", LogLevel.Warn);
        }
    }

    // ConsecutiveTalkTracker.cs 新增方法
    /// <summary>
    /// 实时查询：如果今天已向该 NPC 送礼，当前连续送礼天数是多少（含今天）。
    /// 不修改任何状态，纯读取。
    /// </summary>
    public static (int giftStreak, int sameItemStreak, string lastItemId) PeekGiftStreakToday(string npcName)
    {
        if (string.IsNullOrEmpty(npcName))
            return (0, 0, null);

        // 今天没有送礼记录，不计入
        if (!_todayGifts.TryGetValue(npcName, out string todayItemId))
            return (0, 0, null);

        _data.Streaks.TryGetValue(npcName, out var entry);

        // 昨天的 GiftStreak + 今天这次 = 预计连续天数
        int giftStreak = (entry?.GiftStreak ?? 0) + 1;

        // 同款物品连续天数
        bool sameItem = !string.IsNullOrEmpty(todayItemId)
                        && !string.IsNullOrEmpty(entry?.LastGiftItemId)
                        && string.Equals(todayItemId, entry.LastGiftItemId, StringComparison.OrdinalIgnoreCase);

        int sameItemStreak = sameItem ? (entry?.SameItemGiftStreak ?? 0) + 1 : 1;
        string lastItemId  = todayItemId;

        return (giftStreak, sameItemStreak, lastItemId);
    }

    // ── Public write API ──────────────────────────────────────────────────

    public static void RecordGiftToday(string npcName, string itemId)
    {
        if (string.IsNullOrEmpty(npcName)) return;
        _todayGifts[npcName] = itemId ?? "";
    }

    // ── Core: advance streaks for one NPC (idempotent per day) ────────────

    /// <summary>
    /// Advances all streak counters for the current day. Idempotent: calling
    /// multiple times on the same day only advances once (guarded by LastUpdatedDate).
    /// No perception writes — pure counter state mutation.
    /// </summary>
    private static void AdvanceStreaks(string npcName)
    {
        string todayKey = $"{Game1.year}_{Game1.currentSeason}_{Game1.dayOfMonth}";

        if (!_data.Streaks.TryGetValue(npcName, out var entry))
        {
            entry = new NpcStreakEntry();
            _data.Streaks[npcName] = entry;
        }

        if (entry.LastUpdatedDate == todayKey) return;
        entry.LastUpdatedDate = todayKey;

        // ── 1. Consecutive talk days ──────────────────────────────────────
        bool talkedToday = DialogueHistoryManager.Instance
            .GetHistory(npcName)
            .Any(e => e.SpeakerType       == SpeakerType.Player
                      && e.Timestamp.Year    == Game1.year
                      && e.Timestamp.Season.ToString() == Game1.season.ToString() // ★ 转化为字符串比较，彻底解决 CS0019
                      && e.Timestamp.DayOfMonth == Game1.dayOfMonth);

        entry.TalkStreak = talkedToday ? entry.TalkStreak + 1 : 0;

        // ── 2. Consecutive gift days + same-item streak ───────────────────
        if (_todayGifts.TryGetValue(npcName, out string todayItemId))
        {
            entry.GiftStreak++;

            if (!string.IsNullOrEmpty(todayItemId)
                && string.Equals(todayItemId, entry.LastGiftItemId, StringComparison.OrdinalIgnoreCase))
            {
                entry.SameItemGiftStreak++;
            }
            else
            {
                entry.SameItemGiftStreak = 1;
                entry.LastGiftItemId     = todayItemId;
            }
        }
        else
        {
            entry.GiftStreak         = 0;
            entry.SameItemGiftStreak = 0;
            entry.LastGiftItemId     = "";
        }
    }

    // ── Core: called by NightlyConsolidationHook at DayEnding ─────────────

    public static List<string> FlushAndGetContextLines(string npcName)
    {
        var result = new List<string>();
        if (string.IsNullOrEmpty(npcName)) return result;

        string todayKey = $"{Game1.year}_{Game1.currentSeason}_{Game1.dayOfMonth}";

        if (!_data.Streaks.TryGetValue(npcName, out var entry))
        {
            entry = new NpcStreakEntry();
            _data.Streaks[npcName] = entry;
        }

        if (entry.LastUpdatedDate == todayKey)
        {
            AppendStreakLines(entry, npcName, result);
            return result;
        }

        AdvanceStreaks(npcName);
        AppendStreakLines(entry, npcName, result);
        return result;
    }

    /// <summary>
    /// DayEnding driver: advances streaks for every NPC in the ledger plus
    /// every NPC interacted with today. Failures are logged per-NPC and never
    /// abort the whole roster.
    /// </summary>
    public static void FlushAllToday()
    {
        var roster = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var name in _data.Streaks.Keys)
            roster.Add(name);

        try
        {
            var interacted = PerceptionManager.Instance.GetInteractedNpcNamesToday();
            if (interacted != null)
            {
                foreach (var name in interacted)
                    roster.Add(name);
            }
        }
        catch { /* skip — 仍按台账键推进 */ }

        int count = 0;
        foreach (var name in roster)
        {
            try
            {
                AdvanceStreaks(name);
                count++;
            }
            catch (Exception ex)
            {
                ModEntry.SMonitor?.Log($"[ConsecutiveTalkTracker] FlushAllToday failed for [{name}]: {ex.Message}", LogLevel.Warn);
            }
        }

        ModEntry.SMonitor?.Log($"[ConsecutiveTalkTracker] FlushAllToday advanced {count} tracker(s).", LogLevel.Debug);
    }

    /// <summary>
    /// Builds a read-only streak context block for dialogue prompt injection.
    /// Zero state mutation, zero consumption. Returns "" on any error or no data.
    /// </summary>
    public static string BuildStreakContextBlock(string npcName)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(npcName)) return "";

            if (!_data.Streaks.TryGetValue(npcName, out var entry)) return "";

            var lines = new List<string>();
            AppendStreakLines(entry, npcName, lines);

            // Landmark line: same-item gift streak hits a multiple of 3 (>=3)
            if (entry.SameItemGiftStreak >= 3 && entry.SameItemGiftStreak % 3 == 0
                && !string.IsNullOrEmpty(entry.LastGiftItemId))
            {
                bool isZh = IsChineseLanguage;
                string itemName = GetItemDisplayName(entry.LastGiftItemId);
                lines.Add(isZh
                    ? $"农夫已连续 {entry.SameItemGiftStreak} 天专程送给 {npcName} 同一件礼物：[{itemName}]，展现出非常明确的关注与心思。"
                    : $"The farmer has consistently brought {npcName} the exact same gift [{itemName}] for {entry.SameItemGiftStreak} days in a row, showing deliberate care and focus.");
            }

            return lines.Count > 0 ? string.Join("\n", lines) : "";
        }
        catch (Exception ex)
        {
            ModEntry.SMonitor?.Log($"[ConsecutiveTalkTracker] BuildStreakContextBlock failed: {ex.Message}", LogLevel.Warn);
            return "";
        }
    }

    // ── Private helpers ───────────────────────────────────────────────────

    private static void AppendStreakLines(NpcStreakEntry entry, string npcName, List<string> result)
    {
        bool isZh = IsChineseLanguage;

        if (entry.TalkStreak >= 2)
        {
            result.Add(isZh
                ? $"农夫已连续 {entry.TalkStreak} 天主动寻找 {npcName} 交谈。"
                : $"The farmer has initiated conversation with {npcName} for {entry.TalkStreak} consecutive days.");
        }

        if (entry.GiftStreak >= 2)
        {
            result.Add(isZh
                ? $"农夫已连续 {entry.GiftStreak} 天为 {npcName} 送上礼物。"
                : $"The farmer has presented {npcName} with a gift for {entry.GiftStreak} consecutive days.");
        }

        if (entry.SameItemGiftStreak >= 2 && !string.IsNullOrEmpty(entry.LastGiftItemId))
        {
            string itemName = GetItemDisplayName(entry.LastGiftItemId);
            result.Add(isZh
                ? $"农夫已连续 {entry.SameItemGiftStreak} 天赠送 {npcName} 同一款特定礼物：[{itemName}]。"
                : $"The farmer has given {npcName} the exact same gift [{itemName}] for {entry.SameItemGiftStreak} consecutive days.");
        }
    }

    private static string GetItemDisplayName(string itemId)
    {
        try
        {
            var item = ItemRegistry.Create(itemId);
            return item?.DisplayName ?? item?.Name ?? itemId;
        }
        catch { return itemId; }
    }
}
