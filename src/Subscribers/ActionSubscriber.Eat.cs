using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

namespace ValleyTalk;

/// <summary>
/// Subscribes to eat events and records perceptions when the player eats items.
/// </summary>
internal static class EatSubscriber
{
    private static string _lastItemId = null;
    private static bool _wasEating = false;

    public static void Initialize()
    {
        if (ModEntry.SHelper != null)
        {
            ModEntry.SHelper.Events.GameLoop.UpdateTicked += OnUpdateTicked;
        }
    }

    public static void Cleanup()
    {
        if (ModEntry.SHelper != null)
        {
            ModEntry.SHelper.Events.GameLoop.UpdateTicked -= OnUpdateTicked;
        }
    }

    private static void OnUpdateTicked(object sender, UpdateTickedEventArgs e)
    {
        if (!e.IsMultipleOf(15)) return;

        var player = Game1.player;
        if (player == null) return;

        // Detect eating via isEating flag
        if (player.isEating && !_wasEating)
        {
            RecordEatPerception(player.itemToEat);
        }

        _wasEating = player.isEating;
    }

    private static void RecordEatPerception(Item itemToEat)
    {
        if (itemToEat == null) return;

        string itemId = itemToEat.ItemId;
        if (itemId == _lastItemId || string.IsNullOrEmpty(itemId)) return;

        string itemName = itemToEat.DisplayName ?? itemToEat.Name ?? "something";
        string template = $"The farmer just ate {itemName}.";
        int lifetime = ModEntry.Config.PerceptionActionLifetime;

        PerceptionManager.Instance.Record("Eat", template, null, lifetime, false, itemId);
        _lastItemId = itemId;
    }
}