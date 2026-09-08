using System;
using System.Collections.Generic;
using System.Linq;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

namespace ValleytalkReborn;

/// <summary>
/// Tracks likely harvests by listening to inventory additions instead of scanning the whole bag.
/// If the optional Harmony Crop.harvest patch is installed, it will only record during a real harvest window.
///
/// 当天"最佳收获"判定：按 itemId 累计当天到目前为止的总收获量与见过的最高品质，
/// 再用累计后的分数在所有作物之间比较，选出全天目前最值得说的那一种，
/// 用同一个 "Harvest" key 重新 Record 顶替旧记录——因此感知桶里同一时刻永远只有
/// 一条 Harvest，且这条反映的是真实累计的丰收状况，而不是"谁先被摘到就赢"。
/// </summary>
internal static class HarvestSubscriber
{
    private static bool _initialized = false;

    /// <summary>当天各作物 itemId 累计到目前为止的（最高品质, 累计数量）。</summary>
    private static readonly Dictionary<string, HarvestTotal> _todayHarvestTotals =
        new Dictionary<string, HarvestTotal>(StringComparer.OrdinalIgnoreCase);

    /// <summary>当天目前为止分数最高（最值得说）的作物 itemId，null 表示今天还没有任何收获。</summary>
    private static string _leaderItemId = null;

    private readonly struct HarvestTotal
    {
        /// <summary>当天目前为止见过的最高品质（0普通/1银星/2金星/4铱星）。</summary>
        public readonly int Quality;

        /// <summary>当天目前为止累计的总数量（跨多次收获动作累加）。</summary>
        public readonly int Amount;

        public HarvestTotal(int quality, int amount)
        {
            Quality = quality;
            Amount  = amount;
        }

        public double Score => Quality * 1000.0 + Amount;
    }

    // 如果安装了 Harmony 真实收获补丁，则启用严格模式。
    private static bool _realHarvestMode = false;

    // Crop.harvest() 被调用时记录 tick，形成一个极短的"真实收获窗口"。
    private static int _lastRealHarvestTick = -1000;

    // Forage category，用于排除明显采集物。
    private const int ForageCategory = -81;

    // 大丰收广播门槛：数量达标或品质到铱星才值得全镇皆知
    private const int MajorHarvestAmountThreshold = 15;
    private const int MajorHarvestQualityThreshold = 4; // 铱星

    private static bool IsZh =>
        LocalizedContentManager.CurrentLanguageCode == LocalizedContentManager.LanguageCode.zh;

    public static void Initialize()
    {
        if (_initialized || ModEntry.SHelper == null) return;

        ModEntry.SHelper.Events.Player.InventoryChanged += OnInventoryChanged;
        ModEntry.SHelper.Events.GameLoop.DayStarted += OnDayStarted;

        _initialized = true;
    }

    public static void Cleanup()
    {
        if (!_initialized || ModEntry.SHelper == null) return;

        ModEntry.SHelper.Events.Player.InventoryChanged -= OnInventoryChanged;
        ModEntry.SHelper.Events.GameLoop.DayStarted -= OnDayStarted;

        _todayHarvestTotals.Clear();
        _leaderItemId = null;
        _realHarvestMode = false;
        _lastRealHarvestTick = -1000;
        _initialized = false;
    }

    public static void EnableRealHarvestMode()
    {
        _realHarvestMode = true;
    }

    public static void MarkRealHarvest()
    {
        _realHarvestMode = true;
        _lastRealHarvestTick = Game1.ticks;
    }

    private static bool InRealHarvestWindow =>
        _realHarvestMode && Game1.ticks <= _lastRealHarvestTick + 3;

    private static void OnDayStarted(object sender, DayStartedEventArgs e)
    {
        _todayHarvestTotals.Clear();
        _leaderItemId = null;
    }

    private static void OnInventoryChanged(object sender, InventoryChangedEventArgs e)
    {
        if (!Context.IsWorldReady || Game1.player == null || !e.IsLocalPlayer) return;

        var player = Game1.player;

        // 【新增：丢弃/存箱实时驱逐】背包一旦有空位，立即解除满包感知
        int filledSlots = player.Items?.Count(item => item != null) ?? 0;
        if (filledSlots < player.MaxItems)
        {
            PerceptionManager.Instance.Evict("PlayerBagFull");
        }

        var loc = player.currentLocation;

        if (_realHarvestMode)
        {
            if (!InRealHarvestWindow) return;
        }
        else
        {
            if (!IsCropGrowingLocation(loc)) return;
        }

        bool isRealHarvest = InRealHarvestWindow;

        // 新增整组物品
        if (e.Added != null)
        {
            foreach (var item in e.Added)
            {
                TryRecordAddedItem(item, item?.Stack ?? 1, isRealHarvest);
            }
        }

        // 已有堆叠数量增加
        if (e.QuantityChanged != null)
        {
            foreach (var change in e.QuantityChanged)
            {
                int delta = change.NewSize - change.OldSize;
                if (delta > 0)
                {
                    TryRecordAddedItem(change.Item, delta, isRealHarvest);
                }
            }
        }
    }

    private static bool IsCropGrowingLocation(GameLocation loc)
    {
        if (loc == null) return false;
        return loc is Farm || loc.Name.Equals("Greenhouse", StringComparison.OrdinalIgnoreCase);
    }

    private static void TryRecordAddedItem(Item item, int addedAmount, bool isRealHarvest)
    {
        if (!(item is StardewValley.Object obj)) return;
        if (obj.Type == "Arch") return;

        try
        {
            if (obj.Category == ForageCategory) return;
            if (obj.HasContextTag("forage_item")) return;
        }
        catch
        {
            // ignore
        }

        // 包含蔬菜与水果类作物
        if (obj.Category != StardewValley.Object.VegetableCategory &&
            obj.Category != StardewValley.Object.FruitsCategory) return;

        string itemId = obj.ItemId;
        int addAmount = Math.Max(1, addedAmount);

        _todayHarvestTotals.TryGetValue(itemId, out var existing);
        var updated = new HarvestTotal(
            quality: Math.Max(existing.Quality, obj.Quality),
            amount:  existing.Amount + addAmount);
        _todayHarvestTotals[itemId] = updated;

        bool isLeader;
        if (_leaderItemId == null || string.Equals(_leaderItemId, itemId, StringComparison.OrdinalIgnoreCase))
        {
            isLeader = true;
        }
        else
        {
            var leaderTotal = _todayHarvestTotals[_leaderItemId];
            isLeader = updated.Score > leaderTotal.Score;
        }

        if (!isLeader) return;

        _leaderItemId = itemId;
        RecordHarvestPerception(obj, updated.Quality, updated.Amount, isRealHarvest);
    }

    private static void RecordHarvestPerception(
        StardewValley.Object harvest,
        int totalQuality,
        int totalAmount,
        bool isRealHarvest)
    {
        string cropName = harvest.DisplayName ?? harvest.Name ?? "crops";
        bool isZh = IsZh;

        string qualityPrefix = isZh
            ? totalQuality switch
            {
                1 => "银星",
                2 => "金星",
                4 => "铱星",
                _ => ""
            }
            : totalQuality switch
            {
                1 => "silver-quality ",
                2 => "gold-quality ",
                4 => "iridium-quality ",
                _ => ""
            };

        string template;

        if (isRealHarvest)
        {
            template = PerceptionManager.PickVariant(isZh ? new[]
            {
                $"有村民说，农夫今天收获了{qualityPrefix}【{cropName}】。",
                $"听说农夫的{qualityPrefix}【{cropName}】今天收成了。",
                $"有人在谈论农夫今天收获了一些{qualityPrefix}【{cropName}】。"
            }
            : new[]
            {
                $"Word is going around that the farmer harvested {qualityPrefix}{cropName} today.",
                $"You heard that the farmer's {qualityPrefix}{cropName} came in today.",
                $"People are saying the farmer brought in a harvest of {qualityPrefix}{cropName} today."
            });
        }
        else
        {
            template = PerceptionManager.PickVariant(isZh ? new[]
            {
                $"有人看到农夫今天弄到了一些{qualityPrefix}【{cropName}】。",
                $"听说农夫今天带回来一些{qualityPrefix}【{cropName}】。",
                $"有人注意到农夫包里装着{qualityPrefix}【{cropName}】。"
            }
            : new[]
            {
                $"Word is going around that the farmer got some {qualityPrefix}{cropName} today.",
                $"You heard that the farmer picked up some {qualityPrefix}{cropName} today.",
                $"People are saying the farmer has {qualityPrefix}{cropName} with them today."
            });
        }

        bool isMajorHarvest = totalAmount >= MajorHarvestAmountThreshold
                           || totalQuality >= MajorHarvestQualityThreshold;

        // 【修复地点阻断】：如果是农场/温室的日常收获，locationName 设为 null，
        // 确保无论农夫之后进农舍(FarmHouse)还是在屋外农田(Farm)，配偶和附近NPC都能感知到。
        string locName = isMajorHarvest ? null : null; 

        PerceptionManager.Instance.Record(
            key: "Harvest",
            template: template,
            npcName: null,
            lifetimeHours: isMajorHarvest ? 6 : 2, // 延长普通收获感知窗口到 2 小时
            isGossip: false,
            isLandmark: isMajorHarvest,
            itemId: harvest.ItemId,
            locationName: locName);
    }
}