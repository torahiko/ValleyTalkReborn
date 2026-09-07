using System;
using System.Collections.Generic;
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

        /// <summary>
        /// 打分：品质是主要维度（哪怕只有一颗，铱星也比一大筐普通货更值得说），
        /// 数量是次要维度，用于同品质下比较"谁的丰收更大"，也用于反映真实的累计规模。
        /// 品质权重给得足够大，保证任何品质差距都能压过数量差距。
        /// </summary>
        public double Score => Quality * 1000.0 + Amount;
    }

    // 如果安装了 Harmony 真实收获补丁，则启用严格模式。
    private static bool _realHarvestMode = false;

    // Crop.harvest() 被调用时记录 tick，形成一个极短的"真实收获窗口"。
    private static int _lastRealHarvestTick = -1000;

    // Forage category，用于排除明显采集物。
    private const int ForageCategory = -81;

    // 大丰收广播门槛：数量达标或品质到铱星才值得全镇皆知；
    // 银星/金星单株不再触发 landmark，避免频繁挤占只有 2 个坑位的全局 gossip 队列。
    private const int MajorHarvestAmountThreshold = 15;
    private const int MajorHarvestQualityThreshold = 4; // 铱星

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

        _todayHarvestTotals.Clear();
        _leaderItemId = null;
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
        _todayHarvestTotals.Clear();
        _leaderItemId = null;
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

        string itemId = obj.ItemId;
        int addAmount = Math.Max(1, addedAmount);

        // 累加当天总量：同一 itemId 多次收获（哪怕分散在好几次 InventoryChanged 事件里）
        // 会被正确地加总，而不是只看"这一批有多大"。品质取当天见过的最高值。
        _todayHarvestTotals.TryGetValue(itemId, out var existing);
        var updated = new HarvestTotal(
            quality: Math.Max(existing.Quality, obj.Quality),
            amount:  existing.Amount + addAmount);
        _todayHarvestTotals[itemId] = updated;

        // 判断这次更新后，这个作物是否（依然/重新）是全天分数最高的那个：
        // - 今天还没有任何 leader → 直接成为 leader；
        // - 这个作物本来就是 leader → 累计量变了，需要刷新显示；
        // - 这个作物不是 leader，但累计分数已经超过当前 leader → 换人。
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

        // 大丰收才做全局传闻；普通收获只做短时间的本地观察。
        // 门槛现在基于"当天累计总量"和"铱星品质"，避免单株银/金星就误判为全镇大事。
        bool isMajorHarvest = totalAmount >= MajorHarvestAmountThreshold
                           || totalQuality >= MajorHarvestQualityThreshold;

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