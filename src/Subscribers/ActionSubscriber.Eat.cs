using System;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

namespace ValleytalkReborn;

/// <summary>
/// Subscribes to eat events and records perceptions when the player eats items.
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

        if (player.isEating && !_wasEating)
            RecordEatPerception(player.itemToEat);

        _wasEating = player.isEating;
    }

    private static void RecordEatPerception(Item itemToEat)
    {
        if (itemToEat == null) return;
        string itemId = itemToEat.ItemId;
        if (itemId == _lastItemId || string.IsNullOrEmpty(itemId)) return;

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

        int lifetime = ModEntry.Config.PerceptionActionLifetime;
        PerceptionManager.Instance.Record("Eat", template, null, lifetime,
            isLandmark: false, itemId: itemId);
        _lastItemId = itemId;
    }
}