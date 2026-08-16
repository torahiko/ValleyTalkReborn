using System.Collections.Generic;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

namespace ValleytalkReborn;

/// <summary>
/// Tracks harvests and records town-wide broadcasts when new crops appear in inventory.
/// </summary>
internal static class HarvestSubscriber
{
    private static bool _initialized = false;
    private static readonly HashSet<string> _recordedCropIds = new();

    public static void Initialize()
    {
        if (_initialized || ModEntry.SHelper == null) return;
        ModEntry.SHelper.Events.GameLoop.UpdateTicked += OnUpdateTicked;
        ModEntry.SHelper.Events.GameLoop.DayStarted   += OnDayStarted;
        _initialized = true;
    }

    public static void Cleanup()
    {
        if (!_initialized || ModEntry.SHelper == null) return;
        ModEntry.SHelper.Events.GameLoop.UpdateTicked -= OnUpdateTicked;
        ModEntry.SHelper.Events.GameLoop.DayStarted   -= OnDayStarted;
        _recordedCropIds.Clear();
        _initialized = false;
    }

    private static void OnDayStarted(object sender, DayStartedEventArgs e)
    {
        _recordedCropIds.Clear();
    }

    private static void OnUpdateTicked(object sender, UpdateTickedEventArgs e)
    {
        if (!e.IsMultipleOf(60)) return;
        var player = Game1.player;
        if (player == null) return;

        foreach (var item in player.Items)
        {
            if (item is StardewValley.Object obj && obj.Type == "Arch")
                continue;

            if (item is StardewValley.Object crop
                && crop.Category == StardewValley.Object.VegetableCategory)
            {
                if (_recordedCropIds.Add(crop.ItemId))
                    RecordHarvestPerception(crop);
            }
        }
    }

    private static void RecordHarvestPerception(StardewValley.Object harvest)
    {
        string cropName = harvest.DisplayName ?? harvest.Name ?? "crops";
        string qualityPrefix = harvest.Quality.ToString() switch
        {
            "Gold"    => "gold-quality ",
            "Silver"  => "silver-quality ",
            "Iridium" => "iridium-quality ",
            _         => ""
        };

        string template = PerceptionManager.PickVariant(new[]
        {
            $"Word is going around that the farmer harvested {qualityPrefix}{cropName} today.",
            $"You heard that the farmer's {qualityPrefix}{cropName} came in today.",
            $"People are saying the farmer brought in a harvest of {qualityPrefix}{cropName} this morning.",
        });

        // lifetimeHours: 20 = 全天有效，与 Track 1 语义一致
        PerceptionManager.Instance.Record("Harvest", template, null, 20, isLandmark: true);
    }
}