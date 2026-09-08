using System;
using System.Collections.Generic;
using System.Linq;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

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

    private static string FilePath =>
        $"data/ConsecutiveStreak_{Constants.SaveFolderName}.json";

    // ── Lifecycle ─────────────────────────────────────────────────────────

    public static void Initialize()
    {
        if (_initialized || ModEntry.SHelper == null) return;
        ModEntry.SHelper.Events.GameLoop.SaveLoaded += OnSaveLoaded;
        ModEntry.SHelper.Events.GameLoop.Saving     += OnSaving;
        ModEntry.SHelper.Events.GameLoop.DayStarted += OnDayStarted;
        _initialized = true;
        ModEntry.SMonitor?.Log("[ConsecutiveTalkTracker] Initialized.", LogLevel.Debug);
    }

    public static void Cleanup()
    {
        if (!_initialized || ModEntry.SHelper == null) return;
        ModEntry.SHelper.Events.GameLoop.SaveLoaded -= OnSaveLoaded;
        ModEntry.SHelper.Events.GameLoop.Saving     -= OnSaving;
        ModEntry.SHelper.Events.GameLoop.DayStarted -= OnDayStarted;
        _todayGifts.Clear();
        _initialized = false;
    }
    
    // ── Event handlers ────────────────────────────────────────────────────

    private static void OnSaveLoaded(object sender, SaveLoadedEventArgs e)
    {
        _data = ModEntry.SHelper.Data.ReadJsonFile<TrackerData>(FilePath) ?? new TrackerData();
        _todayGifts.Clear();
        ModEntry.SMonitor?.Log("[ConsecutiveTalkTracker] Data loaded.", LogLevel.Debug);
    }

    private static void OnSaving(object sender, SavingEventArgs e)
    {
        ModEntry.SHelper.Data.WriteJsonFile(FilePath, _data);
        ModEntry.SMonitor?.Log("[ConsecutiveTalkTracker] Data saved.", LogLevel.Debug);
    }

    private static void OnDayStarted(object sender, DayStartedEventArgs e)
    {
        _todayGifts.Clear();
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
                TryInjectSameItemLandmark(npcName, todayItemId, entry.SameItemGiftStreak);
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

        AppendStreakLines(entry, npcName, result);
        return result;
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

    /// <summary>
    /// Fires landmark perception every 3 days for same-item gift streaks.
    /// Uses positive framing to emphasize clear intention.
    /// </summary>
    private static void TryInjectSameItemLandmark(string npcName, string itemId, int streak)
    {
        if (streak < 3 || streak % 3 != 0) return;

        bool isZh = IsChineseLanguage;
        string itemName = GetItemDisplayName(itemId);

        string template = isZh
            ? $"农夫已连续 {streak} 天专程送给 {npcName} 同一件礼物：[{itemName}]，展现出非常明确的关注与心思。"
            : $"The farmer has consistently brought {npcName} the exact same gift [{itemName}] for {streak} days in a row, showing deliberate care and focus.";

        PerceptionManager.Instance.Record(
            key:           "ConsecutiveSameGift",
            template:      template,
            npcName:       npcName,
            lifetimeHours: 20,
            isGossip:      false,
            isLandmark:    true,
            itemId:        itemId,
            locationName:  "");

        ModEntry.SMonitor?.Log(
            $"[ConsecutiveTalkTracker] Landmark injected: {npcName} ← [{itemName}] x{streak}",
            LogLevel.Debug);
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