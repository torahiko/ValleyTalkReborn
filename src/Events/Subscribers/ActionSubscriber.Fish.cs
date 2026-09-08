using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Tools;

namespace ValleytalkReborn;

/// <summary>
/// 监听钓鱼捕获事件：完美适配快速钓鱼模组、多鱼捕获（挑战鱼饵等）以及钓鱼宝箱感知。
/// 传说鱼提升为全镇传闻 (Landmark)，普通鱼为目击者现场感知。
/// </summary>
internal static class FishSubscriber
{
    private static bool _initialized = false;

    // 缓存反射字段，防止高频 GC 与反射性能损耗
    private const BindingFlags RodBindingFlags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
    private static readonly FieldInfo CountField = typeof(FishingRod).GetField("numberOfFishCaught", RodBindingFlags);
    private static readonly FieldInfo TreasureField = typeof(FishingRod).GetField("treasureCaught", RodBindingFlags)
                                                   ?? typeof(FishingRod).GetField("caughtTreasure", RodBindingFlags);
    private static readonly FieldInfo WhichFishField = typeof(FishingRod).GetField("whichFish", RodBindingFlags);
    private static readonly FieldInfo LastCatchField = typeof(FishingRod).GetField("lastCatch", RodBindingFlags);

    private static readonly HashSet<string> LegendaryFishIds =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "159", "160", "163", "164", "682",
            "775", "876", "877", "878", "879",
            "(O)159", "(O)160", "(O)163", "(O)164", "(O)682",
            "(O)775", "(O)876", "(O)877", "(O)878", "(O)879"
        };

    public static void Initialize()
    {
        if (_initialized || ModEntry.SHelper == null) return;
        // 彻底废弃不可靠的 UpdateTicked 轮询，改用官方精准的背包变动事件
        ModEntry.SHelper.Events.Player.InventoryChanged += OnInventoryChanged;
        _initialized = true;
    }

    public static void Cleanup()
    {
        if (!_initialized || ModEntry.SHelper == null) return;
        ModEntry.SHelper.Events.Player.InventoryChanged -= OnInventoryChanged;
        _initialized = false;
    }

    private static void OnInventoryChanged(object sender, InventoryChangedEventArgs e)
    {
        if (!Context.IsWorldReady || !e.IsLocalPlayer) return;
        var player = Game1.player;
        if (player == null) return;

        // 核心判定 1：必须手持/使用鱼竿
        if (player.CurrentTool is not FishingRod rod) return;

        // 核心判定 2：抓取当次变动的鱼类或水产
        Item caughtFish = null;
        int caughtDelta = 1;

        if (e.Added != null)
        {
            foreach (var item in e.Added)
            {
                if (IsFishOrCatch(item))
                {
                    caughtFish = item;
                    caughtDelta = item.Stack;
                    break;
                }
            }
        }

        if (caughtFish == null && e.QuantityChanged != null)
        {
            foreach (var change in e.QuantityChanged)
            {
                int delta = change.NewSize - change.OldSize;
                if (delta > 0 && IsFishOrCatch(change.Item))
                {
                    caughtFish = change.Item;
                    caughtDelta = delta;
                    break;
                }
            }
        }

        // 成功捕获鱼类，立即调用完整的感知注入流水线
        if (caughtFish != null)
        {
            RecordFishPerception(rod, caughtFish, caughtDelta);
        }
    }

    private static bool IsFishOrCatch(Item item)
    {
        if (item is not StardewValley.Object obj) return false;

        // 传说鱼 / 鱼类 (Category -4) / 垃圾 (Category -20)
        return obj.Category == StardewValley.Object.FishCategory ||
               obj.Category == StardewValley.Object.junkCategory ||
               LegendaryFishIds.Contains(obj.ItemId) ||
               LegendaryFishIds.Contains(obj.QualifiedItemId);
    }

    private static void RecordFishPerception(FishingRod rod, Item directCatch, int addedStack)
    {
        int lifetime = ModEntry.Config?.PerceptionActionLifetime ?? 2;

        // 优先尝试从鱼竿解析，解析不到则以刚刚确认到账的鱼为准（100% 绝不为 null）
        Item caught = TryGetCaughtFish(rod) ?? directCatch;
        if (caught == null) return;

        bool isZh = LocalizedContentManager.CurrentLanguageCode.ToString()
            .StartsWith("zh", StringComparison.OrdinalIgnoreCase);

        string fishName = caught.DisplayName ?? caught.Name ?? (isZh ? "鱼" : "fish");
        string fishItemId = caught.QualifiedItemId ?? caught.ItemId ?? string.Empty;
        bool isLegendary = !string.IsNullOrEmpty(fishItemId) && LegendaryFishIds.Contains(fishItemId);

        // 提取鱼数量与宝箱信息（完整保留挑战鱼饵多鱼与水底宝箱判定）
        int fishCount = Math.Max(addedStack, GetCaughtFishCount(rod, caught));
        bool hasTreasure = HasCaughtTreasure(rod);

        string template;
        if (isLegendary)
        {
            // 完整保留极限活动追踪器联动
            ExtremeActivityTracker.NotifyLegendaryFishCaught();

            if (fishCount > 1 && hasTreasure)
            {
                template = isZh
                    ? $"不可思议！农夫刚才竟然一杆同时拉起了 {fishCount} 条传说神鱼【{fishName}】，而且还附带捞起了一个神秘宝箱！全镇都要炸开锅了。"
                    : $"Unbelievable! The farmer just hauled in {fishCount} legendary {fishName} at once, along with a sunken treasure chest! The whole town is buzzing.";
            }
            else if (fishCount > 1)
            {
                template = isZh
                    ? $"太惊人了！农夫竟然同时钓起了 {fishCount} 条传说中的【{fishName}】！这等奇事迅速在全镇传开了。"
                    : $"Astounding! The farmer managed to catch {fishCount} legendary {fishName} in a single cast! Word spread like wildfire.";
            }
            else if (hasTreasure)
            {
                template = isZh
                    ? $"大丰收！农夫钓起了传说神鱼【{fishName}】，甚至还拉上了一个水底宝箱！消息已经在整个小镇传开了。"
                    : $"An epic catch! The farmer landed the legendary {fishName} and reeled in a sunken treasure chest! Everyone is talking about it.";
            }
            else
            {
                template = isZh
                    ? PerceptionManager.PickVariant(new[]
                    {
                        $"农夫刚刚钓起了一条【{fishName}】——那可是传说中的神鱼！消息已经在整个小镇传开了。",
                        $"你听到了外面的动静：农夫竟然把传说中的【{fishName}】钓了上来，大家都在热烈议论。",
                        $"现在小镇里几乎人尽皆知了——农夫捕获了极为罕见的传说鱼【{fishName}】。"
                    })
                    : PerceptionManager.PickVariant(new[]
                    {
                        $"The farmer just caught a {fishName} — a legendary fish! Word spread through the whole valley.",
                        $"You heard the commotion: the farmer pulled a {fishName} out of the water. People are already talking.",
                        $"Everyone seems to know by now — the farmer caught a {fishName}, one of the rarest fish in the valley."
                    });
            }
        }
        else
        {
            // 普通捕获分支（多条 / 宝箱全部完整保留）
            if (fishCount > 1 && hasTreasure)
            {
                template = isZh
                    ? PerceptionManager.PickVariant(new[]
                    {
                        $"你亲眼看到农夫刚才一杆拉起了 {fishCount} 条【{fishName}】，居然还有额外的宝藏宝箱！运气简直逆天了。",
                        $"农夫刚才运气爆棚，一次性钓起了 {fishCount} 条【{fishName}】，顺带还从水底捞上了一个大宝箱！"
                    })
                    : PerceptionManager.PickVariant(new[]
                    {
                        $"You saw the farmer pull up {fishCount} {fishName} all at once, along with an extra treasure chest! Incredible luck.",
                        $"The farmer had a massive haul, landing {fishCount} {fishName} in one go plus a sunken treasure chest!"
                    });
            }
            else if (fishCount > 1)
            {
                template = isZh
                    ? PerceptionManager.PickVariant(new[]
                    {
                        $"你看到农夫刚才在水边身手不凡，一次性钓起了 {fishCount} 条【{fishName}】！",
                        $"农夫刚才一杆拉起了 {fishCount} 条【{fishName}】，收获颇丰。"
                    })
                    : PerceptionManager.PickVariant(new[]
                    {
                        $"You saw the farmer land {fishCount} {fishName} in a single cast!",
                        $"The farmer just reeled in {fishCount} {fishName} at once by the water."
                    });
            }
            else if (hasTreasure)
            {
                template = isZh
                    ? PerceptionManager.PickVariant(new[]
                    {
                        $"你看到农夫刚才钓起了一条【{fishName}】，而且还从水下拉上来了一个宝藏宝箱！",
                        $"农夫刚才不仅钓到了【{fishName}】，居然还顺手捞到了一个宝箱，收获满满。"
                    })
                    : PerceptionManager.PickVariant(new[]
                    {
                        $"You saw the farmer catch a {fishName}, along with a sunken treasure chest!",
                        $"The farmer hooked a {fishName} and managed to pull up a treasure chest too."
                    });
            }
            else
            {
                template = isZh
                    ? PerceptionManager.PickVariant(new[]
                    {
                        $"你看到农夫刚才在附近的水边钓上来了一条【{fishName}】。",
                        $"农夫刚才就在不远处钓鱼，顺利拉上来了一条【{fishName}】。",
                        $"你瞥见农夫正举着刚刚钓到的【{fishName}】。"
                    })
                    : PerceptionManager.PickVariant(new[]
                    {
                        $"You saw the farmer pull a {fishName} out of the water nearby.",
                        $"The farmer was fishing close by and just landed a {fishName}.",
                        $"You caught a glimpse of the farmer holding up a {fishName} they just caught."
                    });
            }
        }

        string eventKey = isLegendary ? "LegendaryFish" : "Fish";
        PerceptionManager.Instance.Evict(eventKey, fromGossip: isLegendary);

        // 修复地点：普通鱼传入玩家所在水域地图名，传说鱼设为 null 进行全镇广播
        string currentLocationName = Game1.player?.currentLocation?.Name;

        PerceptionManager.Instance.Record(
            key:           eventKey,
            template:      template,
            npcName:       null,
            lifetimeHours: isLegendary ? 20 : lifetime,
            isLandmark:    isLegendary,
            itemId:        fishItemId,
            locationName:  isLegendary ? null : currentLocationName
        );
    }

    /// <summary>
    /// 获取钓鱼数量（兼容 1.6 挑战鱼饵多鱼机制）
    /// </summary>
    private static int GetCaughtFishCount(FishingRod rod, Item caughtItem)
    {
        if (rod != null)
        {
            // 优先尝试 1.6 官方 NetInt
            try
            {
                if (rod.numberOfFishCaught > 0)
                    return rod.numberOfFishCaught;
            }
            catch { }

            if (CountField != null)
            {
                try
                {
                    object raw = CountField.GetValue(rod);
                    if (raw is int c && c > 0) return c;
                    if (raw != null)
                    {
                        var prop = raw.GetType().GetProperty("Value", RodBindingFlags);
                        if (prop?.GetValue(raw) is int netVal && netVal > 0)
                            return netVal;
                    }
                }
                catch { }
            }
        }

        if (caughtItem != null && caughtItem.Stack > 1)
            return caughtItem.Stack;

        return 1;
    }

    /// <summary>
    /// 判断是否拉起了水下宝箱
    /// </summary>
    private static bool HasCaughtTreasure(FishingRod rod)
    {
        if (rod == null) return false;

        // 优先尝试 1.6 官方 NetBool
        try
        {
            if (rod.treasureCaught) return true;
        }
        catch { }

        if (TreasureField != null)
        {
            try
            {
                object raw = TreasureField.GetValue(rod);
                if (raw is bool b && b) return true;
                if (raw != null)
                {
                    var prop = raw.GetType().GetProperty("Value", RodBindingFlags);
                    if (prop?.GetValue(raw) is bool netBool && netBool)
                        return true;
                }
            }
            catch { }
        }

        return false;
    }

    /// <summary>
    /// 从鱼竿内部解析捕获数据
    /// </summary>
    private static Item TryGetCaughtFish(FishingRod rod)
    {
        if (rod != null)
        {
            if (LastCatchField != null)
            {
                try
                {
                    object rawCatch = LastCatchField.GetValue(rod);
                    if (rawCatch is Item itemCatch) return itemCatch;
                    if (rawCatch != null)
                    {
                        var valProp = rawCatch.GetType().GetProperty("Value", RodBindingFlags);
                        if (valProp?.GetValue(rawCatch) is Item netCatchItem) return netCatchItem;
                    }
                }
                catch { }
            }

            if (WhichFishField != null)
            {
                try
                {
                    object raw = WhichFishField.GetValue(rod);
                    string fishId = null;

                    if (raw is string s) fishId = s;
                    else if (raw != null)
                    {
                        var valProp = raw.GetType().GetProperty("Value", RodBindingFlags);
                        fishId = valProp?.GetValue(raw) as string;
                    }

                    if (!string.IsNullOrEmpty(fishId) && fishId != "-1")
                    {
                        string qualifiedId = fishId.StartsWith("(") ? fishId : $"(O){fishId}";
                        var item = ItemRegistry.Create(qualifiedId, allowNull: true);
                        if (item != null) return item;
                    }
                }
                catch { }
            }
        }

        var player = Game1.player;
        if (player?.ActiveObject != null)
        {
            var obj = player.ActiveObject;
            if (obj.Category == StardewValley.Object.FishCategory ||
                obj.Category == StardewValley.Object.junkCategory ||
                LegendaryFishIds.Contains(obj.ItemId) ||
                LegendaryFishIds.Contains(obj.QualifiedItemId))
            {
                return obj;
            }
        }

        return null;
    }
}