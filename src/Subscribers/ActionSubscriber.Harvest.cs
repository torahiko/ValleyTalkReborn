using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

namespace ValleyTalk;

/// <summary>
/// Tracks harvests and records town-wide broadcasts when player ships items.
/// </summary>
internal static class HarvestSubscriber
{
    private static int _lastShippedId = -1;

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
        if (!e.IsMultipleOf(60)) return;

        var player = Game1.player;
        if (player == null) return;

        // Track crops in inventory (harvested items)
        foreach (var item in player.Items)
        {
            if (item is StardewValley.Object obj && obj.Type == "Arch")
            {
                continue; // Skip artifacts
            }

            if (item is StardewValley.Object crop && crop.Category == StardewValley.Object.VegetableCategory)
            {
                int currentId = crop.ParentSheetIndex;
                if (currentId != _lastShippedId)
                {
                    _lastShippedId = currentId;
                    RecordHarvestPerception(crop);
                }
            }
        }
    }

    private static void RecordHarvestPerception(StardewValley.Object harvest)
    {
        string cropName = harvest.DisplayName ?? harvest.Name ?? "crops";
        string qualityPrefix = harvest.Quality.ToString() switch
        {
            "Gold" => "gold-quality ",
            "Silver" => "silver-quality ",
            "Iridium" => "iridium-quality ",
            _ => ""
        };

        string template = $"The farm harvested {qualityPrefix}{cropName} today.";
        int lifetime = ModEntry.Config.PerceptionHarvestLifetime;

        PerceptionManager.Instance.Record("Harvest", template, null, lifetime, true);
    }
}