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

    private static bool IsZh =>
        LocalizedContentManager.CurrentLanguageCode.ToString().StartsWith("zh", StringComparison.OrdinalIgnoreCase);

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
                string name = obj.DisplayName ?? obj.Name ?? (IsZh ? "家具" : "something");
                string template = IsZh
                    ? PerceptionManager.PickVariant(new[]
                    {
                        $"你注意到农夫刚刚在附近摆放了一件【{name}】。",
                        $"农夫刚才在离你不远的地方安置了一个【{name}】。",
                        $"你看到农夫正在旁边整理摆放着的【{name}】。"
                    })
                    : PerceptionManager.PickVariant(new[]
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
            // 核心修复：只有 growthStage >= 5 的成年大树被砍倒才记录
            // 彻底过滤掉镰刀打碎小树芽/树苗的情况
            if (feature is StardewValley.TerrainFeatures.Tree tree &&
                tree.growthStage.Value >= StardewValley.TerrainFeatures.Tree.treeStage)
            {
                string template = IsZh
                    ? PerceptionManager.PickVariant(new[]
                    {
                        "你听到了斧头劈下的闷响——农夫刚才就在附近砍倒了一棵大树。",
                        "农夫刚才挥着斧头在不远处伐木，一棵大树应声倒地。",
                        "你注意到农夫刚刚在离这儿不远的地方砍翻了一棵树。"
                    })
                    : PerceptionManager.PickVariant(new[]
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