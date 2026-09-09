using System;
using System.Collections.Generic;
using System.Linq;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

namespace ValleytalkReborn;

internal static class GiftSubscriber
{
    private static bool _initialized = false;
    // 记录各 NPC 记录过的最新送礼天数索引（TotalDays），彻底解决生日、配偶不计入 GiftsThisWeek 的问题
    private static readonly Dictionary<string, int> _lastGiftDays = new(StringComparer.OrdinalIgnoreCase);

    public static void Initialize()
    {
        if (_initialized || ModEntry.SHelper == null) return;
        ModEntry.SHelper.Events.GameLoop.UpdateTicked += OnUpdateTicked;
        ModEntry.SHelper.Events.GameLoop.DayStarted   += OnDayStarted;
        _initialized = true;
        ModEntry.SMonitor?.Log("[GiftSubscriber] Initialized.", LogLevel.Debug);
    }

    public static void Cleanup()
    {
        if (!_initialized || ModEntry.SHelper == null) return;
        ModEntry.SHelper.Events.GameLoop.UpdateTicked -= OnUpdateTicked;
        ModEntry.SHelper.Events.GameLoop.DayStarted   -= OnDayStarted;
        _lastGiftDays.Clear();
        _initialized = false;
    }

    /// <summary>
    /// 当由 NPC_GetGiftReaction_Patch 显式截获时调用。
    /// 直接将当天的 TotalDays 填入，锁定防重标记，轮询就不会再次误判。
    /// </summary>
    public static void RecordGiftPerceptionFromPatch(string npcName, string itemName, string itemId)
    {
        int todayDays = Game1.Date?.TotalDays ?? 0;
        _lastGiftDays[npcName] = todayDays;

        MassGiftTracker.RecordGift(npcName, itemId);
        ConsecutiveTalkTracker.RecordGiftToday(npcName, itemId);
        RecordGiftPerception(npcName, itemName, itemId);
    }

    private static void OnDayStarted(object sender, DayStartedEventArgs e)
    {
        // 每日开始时拉取一次现状，填充昨天的送礼快照
        SyncExistingGiftStatus();
    }

    private static void OnUpdateTicked(object sender, UpdateTickedEventArgs e)
    {
        if (!e.IsMultipleOf(15)) return;
        if (!ModEntry.Config.EnablePerceptionSystem) return;
        if (!ModEntry.Config.EnablePerceptionGift) return;

        var player = Game1.player;
        if (player?.friendshipData == null) return;

        int currentDay = Game1.Date?.TotalDays ?? 0;

        // 使用 ToArray 快照遍历，防止 NetStringDictionary 遍历时并发修改崩溃
        foreach (var pair in player.friendshipData.Pairs.ToArray())
        {
            string npcName = pair.Key;
            Friendship friendship = pair.Value;
            if (friendship?.LastGiftDate == null) continue;

            // 获取该 NPC 最后一次收到礼物的总天数
            int lastGivenDay = friendship.LastGiftDate.TotalDays;

            if (!_lastGiftDays.TryGetValue(npcName, out int recordedDay))
            {
                // 初次发现该 NPC，先记录现状以对齐基准
                _lastGiftDays[npcName] = lastGivenDay;
                continue;
            }

            // 如果最新送礼时间就是今天，且之前并未记录过今天给该 NPC 送过礼
            // 说明这是一个绕过 Patch 的礼物（如通过特殊脚本、第三方 Mod 途径赠送）
            if (lastGivenDay == currentDay && recordedDay < currentDay)
            {
                _lastGiftDays[npcName] = currentDay;
                RecordGiftPerception(npcName, null, null);
            }
        }
    }

    private static void SyncExistingGiftStatus()
    {
        var player = Game1.player;
        if (player?.friendshipData == null) return;

        foreach (var pair in player.friendshipData.Pairs.ToArray())
        {
            if (pair.Value?.LastGiftDate != null)
            {
                _lastGiftDays[pair.Key] = pair.Value.LastGiftDate.TotalDays;
            }
        }
    }

    private static void RecordGiftPerception(string npcName, string itemName, string itemId)
    {
        var npc = Game1.getCharacterFromName(npcName);
        string displayName = npc?.displayName ?? npcName;
        string location = Game1.currentLocation?.Name ?? string.Empty;
        bool isZh = LocalizedContentManager.CurrentLanguageCode == LocalizedContentManager.LanguageCode.zh;

        string template;
        if (isZh)
        {
            template = !string.IsNullOrEmpty(itemName)
                ? $"农夫递给了【{displayName}】一件礼物：【{itemName}】。"
                : $"农夫给【{displayName}】赠送了一件礼物。";
        }
        else
        {
            template = !string.IsNullOrEmpty(itemName)
                ? $"The farmer gave {displayName} a gift: [{itemName}]."
                : $"The farmer gave {displayName} a gift.";
        }

        PerceptionManager.Instance.Record(
            key: "Gift",
            template: template,
            npcName: npcName,
            lifetimeHours: 4,
            isGossip: false,
            isLandmark: false,
            itemId: itemId,
            locationName: location);

        // 送礼后物品栏变空，驱逐手持物感知并短时屏蔽深度感知回填
        PerceptionManager.Instance.Evict("PlayerActiveItem");
        PerceptionManager.Instance.SuppressKeyTemporarily("PlayerActiveItem", durationMinutes: 10);

        if (ModEntry.Config.Debug)
            ModEntry.SMonitor?.Log(
                $"[GiftSubscriber] [{itemName ?? "unknown item"}] → {npcName} @ {location}",
                LogLevel.Debug);
    }
}