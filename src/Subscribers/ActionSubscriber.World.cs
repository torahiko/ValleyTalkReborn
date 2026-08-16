using System;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

namespace ValleytalkReborn;

/// <summary>
/// Subscribes to world object/terrain changes for placement and chopping.
/// Uses event-driven APIs instead of polling — no per-tick overhead.
/// </summary>
internal static class WorldSubscriber
{
    private static bool _initialized = false;

    public static void Initialize()
    {
        if (_initialized || ModEntry.SHelper == null) return;
        try
        {
            ModEntry.SHelper.Events.World.ObjectListChanged += OnObjectListChanged;
            ModEntry.SHelper.Events.World.TerrainFeatureListChanged += OnTerrainFeatureListChanged;
            _initialized = true;
        }
        catch (Exception ex)
        {
            ModEntry.SMonitor?.Log($"[WorldSubscriber] Event subscription failed: {ex.Message}", LogLevel.Warn);
        }
    }

    public static void Cleanup()
    {
        if (!_initialized || ModEntry.SHelper == null) return;
        try
        {
            ModEntry.SHelper.Events.World.ObjectListChanged -= OnObjectListChanged;
            ModEntry.SHelper.Events.World.TerrainFeatureListChanged -= OnTerrainFeatureListChanged;
            _initialized = false;
        }
        catch (Exception ex)
        {
            ModEntry.SMonitor?.Log($"[WorldSubscriber] Event unsubscription failed: {ex.Message}", LogLevel.Warn);
        }
    }

    private static void OnObjectListChanged(object sender, ObjectListChangedEventArgs e)
    {
        if (e.Location == null) return;
        foreach (var pair in e.Added)
            HandleObjectAdded(pair.Value);
    }

    private static void OnTerrainFeatureListChanged(object sender, TerrainFeatureListChangedEventArgs e)
    {
        if (e.Location == null) return;
        foreach (var pair in e.Removed)
            HandleTerrainFeatureRemoved(pair.Value);
    }

    private static void HandleObjectAdded(StardewValley.Object obj)
    {
        if (obj == null) return;
        try
        {
            if (obj.Category == StardewValley.Object.furnitureCategory)
            {
                string name = obj.DisplayName ?? obj.Name ?? "something";
                string template = PerceptionManager.PickVariant(new[]
                {
                    $"You noticed the farmer setting down a {name} nearby.",
                    $"The farmer just placed a {name} not far from where you were standing.",
                    $"You saw the farmer arranging a {name} a moment ago.",
                });
                PerceptionManager.Instance.Record("Place", template, null,
                    ModEntry.Config.PerceptionActionLifetime, isLandmark: false);
            }
        }
        catch (Exception ex)
        {
            ModEntry.SMonitor?.Log($"[WorldSubscriber] Error handling added object: {ex.Message}", LogLevel.Trace);
        }
    }

    private static void HandleTerrainFeatureRemoved(StardewValley.TerrainFeatures.TerrainFeature feature)
    {
        if (feature == null) return;
        try
        {
            if (feature is StardewValley.TerrainFeatures.Tree)
            {
                string template = PerceptionManager.PickVariant(new[]
                {
                    "You heard the crack of an axe — the farmer just felled a tree nearby.",
                    "The farmer was swinging an axe at a tree close by and brought it down.",
                    "You saw the farmer chopping down a tree not far from here.",
                });
                PerceptionManager.Instance.Record("Chop", template, null,
                    ModEntry.Config.PerceptionActionLifetime, isLandmark: false);
            }
        }
        catch (Exception ex)
        {
            ModEntry.SMonitor?.Log($"[WorldSubscriber] Error handling removed terrain feature: {ex.Message}", LogLevel.Trace);
        }
    }
}