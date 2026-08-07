using System;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

namespace ValleyTalk;

/// <summary>
/// Subscribes to world object/terrain changes for placement and chopping.
/// Uses event-driven APIs instead of polling — no per-tick overhead.
/// </summary>
internal static class WorldSubscriber
{
    public static void Initialize()
    {
        if (ModEntry.SHelper != null)
        {
            try
            {
                ModEntry.SHelper.Events.World.ObjectListChanged += OnObjectListChanged;
                ModEntry.SHelper.Events.World.TerrainFeatureListChanged += OnTerrainFeatureListChanged;
            }
            catch (Exception ex)
            {
                ModEntry.SMonitor?.Log($"[WorldSubscriber] Event subscription failed: {ex.Message}", LogLevel.Warn);
            }
        }
    }

    public static void Cleanup()
    {
        if (ModEntry.SHelper != null)
        {
            try
            {
                ModEntry.SHelper.Events.World.ObjectListChanged -= OnObjectListChanged;
                ModEntry.SHelper.Events.World.TerrainFeatureListChanged -= OnTerrainFeatureListChanged;
            }
            catch (Exception ex)
            {
                ModEntry.SMonitor?.Log($"[WorldSubscriber] Event unsubscription failed: {ex.Message}", LogLevel.Warn);
            }
        }
    }

    private static void OnObjectListChanged(object sender, ObjectListChangedEventArgs e)
    {
        if (e.Location == null) return;

        foreach (var pair in e.Added)
        {
            HandleObjectAdded(pair.Value);
        }
    }

    private static void OnTerrainFeatureListChanged(object sender, TerrainFeatureListChangedEventArgs e)
    {
        if (e.Location == null) return;

        foreach (var pair in e.Removed)
        {
            HandleTerrainFeatureRemoved(pair.Value);
        }
    }

    private static void HandleObjectAdded(StardewValley.Object obj)
    {
        if (obj == null) return;

        try
        {
            // Place detection: furniture category
            if (obj.Category == StardewValley.Object.furnitureCategory)
            {
                string name = obj.DisplayName ?? obj.Name ?? "item";
                string template = $"The farmer just placed a {name}.";
                PerceptionManager.Instance.Record("Place", template, null, ModEntry.Config.PerceptionActionLifetime, false);
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
            // Chop detection: tree terrain feature removed
            if (feature is StardewValley.TerrainFeatures.Tree)
            {
                string template = $"The farmer just chopped down a tree.";
                PerceptionManager.Instance.Record("Chop", template, null, ModEntry.Config.PerceptionActionLifetime, false);
            }
        }
        catch (Exception ex)
        {
            ModEntry.SMonitor?.Log($"[WorldSubscriber] Error handling removed terrain feature: {ex.Message}", LogLevel.Trace);
        }
    }
}