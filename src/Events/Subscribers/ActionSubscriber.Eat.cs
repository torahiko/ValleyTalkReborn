using System;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

namespace ValleytalkReborn;

/// <summary>
/// 监听玩家进食事件并记录即时感知。
/// 具备完整的重置机制与超短生命周期（阅后即焚）。
/// </summary>
internal static class EatSubscriber
{
    private static string _lastItemId = null;
    private static bool _wasEating = false;
    private static bool _initialized = false;

    public static void Initialize()
    {
        if (_initialized || ModEntry.SHelper == null) return;
        ModEntry.SHelper.Events.GameLoop.UpdateTicked += OnUpdateTicked;
        _initialized = true;
    }

    public static void Cleanup()
    {
        if (!_initialized || ModEntry.SHelper == null) return;
        ModEntry.SHelper.Events.GameLoop.UpdateTicked -= OnUpdateTicked;
        _lastItemId = null;
        _wasEating = false;
        _initialized = false;
    }

    private static void OnUpdateTicked(object sender, UpdateTickedEventArgs e)
    {
        if (!e.IsMultipleOf(15)) return;
        var player = Game1.player;
        if (player == null) return;

        // 1. 开始进食瞬间触发
        if (player.isEating && !_wasEating)
        {
            RecordEatPerception(player.itemToEat);
        }
        // 2. 进食动作彻底结束时，重置防重缓存，允许下一次吃同一种物品继续触发
        else if (!player.isEating && _wasEating)
        {
            _lastItemId = null;
        }

        _wasEating = player.isEating;
    }

    private static void RecordEatPerception(Item itemToEat)
    {
        if (itemToEat == null) return;
        string itemId = itemToEat.ItemId;
        if (string.IsNullOrEmpty(itemId) || itemId == _lastItemId) return;

        string itemName = itemToEat.DisplayName ?? itemToEat.Name ?? "something";
        bool isZh = LocalizedContentManager.CurrentLanguageCode.ToString()
            .StartsWith("zh", StringComparison.OrdinalIgnoreCase);

        string template = isZh
            ? PerceptionManager.PickVariant(new[]
            {
                $"你注意到农夫刚刚就在你面前吃了一个【{itemName}】。",
                $"农夫刚才拿出了一个【{itemName}】，在旁边吃了起来。",
                $"你刚刚看到农夫在旁边享用【{itemName}】。"
            })
            : PerceptionManager.PickVariant(new[]
            {
                $"You noticed the farmer eating a [{itemName}] right in front of you.",
                $"The farmer just pulled out a [{itemName}] and started eating it nearby.",
                $"You watched the farmer snack on a [{itemName}] a moment ago."
            });

        // 阅后即焚生命周期：
        // 1. 物理保质期只给 1 个游戏小时（超出即过期自动清理）
        // 2. 严禁设置为 Landmark（非全镇长效广播）
        const int BurnAfterReadingLifetime = 1;

        PerceptionManager.Instance.Record(
            key: "Eat",
            template: template,
            npcName: null,
            lifetimeHours: BurnAfterReadingLifetime,
            isLandmark: false,
            itemId: itemId
        );

        _lastItemId = itemId;
    }
}