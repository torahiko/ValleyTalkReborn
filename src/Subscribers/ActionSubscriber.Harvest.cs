using System;
using System.Collections.Generic;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

namespace ValleytalkReborn;

/// <summary>
/// Tracks likely harvests by listening to inventory additions instead of scanning the whole bag.
/// If the optional Harmony Crop.harvest patch is installed, it will only record during a real harvest window.
/// </summary>
internal static class HarvestSubscriber
{
    private static bool _initialized = false;

    private static readonly HashSet<string> _recordedCropIds =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    // 如果安装了 Harmony 真实收获补丁，则启用严格模式。
    private static bool _realHarvestMode = false;

    // Crop.harvest() 被调用时记录 tick，形成一个极短的"真实收获窗口"。
    private static int _lastRealHarvestTick = -1000;

    // Forage category，用于排除明显采集物。
    private const int ForageCategory = -81;

    private static bool IsZh =>
        LocalizedContentManager.CurrentLanguageCode == LocalizedContentManager.LanguageCode.zh;

    public static void Initialize()
    {
        if (_initialized || ModEntry.SHelper == null) return;

        // 不再使用 UpdateTicked 扫描整个背包。
        ModEntry.SHelper.Events.Player.InventoryChanged += OnInventoryChanged;
        ModEntry.SHelper.Events.GameLoop.DayStarted += OnDayStarted;

        _initialized = true;
    }

    public static void Cleanup()
    {
        if (!_initialized || ModEntry.SHelper == null) return;

        ModEntry.SHelper.Events.Player.InventoryChanged -= OnInventoryChanged;
        ModEntry.SHelper.Events.GameLoop.DayStarted -= OnDayStarted;

        _recordedCropIds.Clear();
        _realHarvestMode = false;
        _lastRealHarvestTick = -1000;
        _initialized = false;
    }

    /// <summary>
    /// 如果你安装了 Harmony 补丁，请在 ModEntry 里调用这个。
    /// 调用后，HarvestSubscriber 只会在 Crop.harvest() 触发后的极短窗口内记录收获。
    /// </summary>
    public static void EnableRealHarvestMode()
    {
        _realHarvestMode = true;
    }

    /// <summary>
    /// 由可选 Harmony Patch 调用。
    /// </summary>
    public static void MarkRealHarvest()
    {
        _realHarvestMode = true;
        _lastRealHarvestTick = Game1.ticks;
    }

    private static bool InRealHarvestWindow =>
        _realHarvestMode && Game1.ticks <= _lastRealHarvestTick + 3;

    private static void OnDayStarted(object sender, DayStartedEventArgs e)
    {
        _recordedCropIds.Clear();
    }

    private static void OnInventoryChanged(object sender, InventoryChangedEventArgs e)
    {
        if (!Context.IsWorldReady || Game1.player == null) return;

        var loc = Game1.player.currentLocation;

        if (_realHarvestMode)
        {
            // Harmony 模式：只相信 Crop.harvest() 产生的短窗口。
            if (!InRealHarvestWindow) return;
        }
        else
        {
            // 非 Harmony 模式：至少限制在农场，避免野外捡到触发。
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
                // 使用 NewSize - OldSize 计算增加量，兼容不同 SMAPI 版本
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

        // 保守方案：只检测 Farm。
        // 注意：Farm 类在 StardewValley 命名空间下，不在 StardewValley.Locations 下。
        return loc is Farm;

        // 如果不装 Harmony，但又想检测姜岛，可以改成：
        //
        // return loc is Farm
        //     || loc is StardewValley.Locations.IslandWest;
        //
        // 但注意：这会把姜岛捡到、购买、箱子取出等也更宽泛地纳入判断。
    }

    private static void TryRecordAddedItem(Item item, int addedAmount, bool isRealHarvest)
    {
        if (!(item is StardewValley.Object obj)) return;
        if (obj.Type == "Arch") return;

        // 排除明显采集物。
        // 如果没有 HasContextTag，可以删除 try 里面第二行。
        try
        {
            if (obj.Category == ForageCategory) return;
            if (obj.HasContextTag("forage_item")) return;
        }
        catch
        {
            // ignore
        }

        // 和你原逻辑一致：先只处理蔬菜类作物。
        // 如果以后要支持果树/花卉，再扩展 Category。
        if (obj.Category != StardewValley.Object.VegetableCategory) return;

        if (_recordedCropIds.Add(obj.ItemId))
        {
            RecordHarvestPerception(obj, Math.Max(1, addedAmount), isRealHarvest);
        }
    }

    private static void RecordHarvestPerception(
        StardewValley.Object harvest,
        int addedAmount,
        bool isRealHarvest)
    {
        string cropName = harvest.DisplayName ?? harvest.Name ?? "crops";
        bool isZh = IsZh;

        string qualityPrefix = isZh
            ? harvest.Quality switch
            {
                1 => "银星",
                2 => "金星",
                4 => "铱星",
                _ => ""
            }
            : harvest.Quality switch
            {
                1 => "silver-quality ",
                2 => "gold-quality ",
                4 => "iridium-quality ",
                _ => ""
            };

        string template;

        if (isRealHarvest)
        {
            // 真实收获模式：可以明确说"收获"。
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
            // 非真实收获模式：来源不完全确定，不要说死"收获"。
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

        // 大收获才做全局传闻；普通收获只做短时间的本地观察。
        // 你可以按喜好调整阈值。
        bool isMajorHarvest = addedAmount >= 10 || harvest.Quality >= 2;

        PerceptionManager.Instance.Record(
            key: "Harvest",
            template: template,
            npcName: null,
            lifetimeHours: isMajorHarvest ? 6 : 1,
            isGossip: false,
            isLandmark: isMajorHarvest,
            itemId: harvest.ItemId,
            locationName: Game1.player?.currentLocation?.Name);
    }
}