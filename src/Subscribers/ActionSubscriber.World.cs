using System;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

namespace ValleytalkReborn;

/// <summary>
/// Subscribes to world object, furniture, and terrain changes for placement and chopping.
/// Uses event-driven APIs instead of polling — no per-tick overhead.
/// </summary>
internal static class WorldSubscriber
{
    private static bool _initialized = false;

    private static bool IsZh =>
        LocalizedContentManager.CurrentLanguageCode == LocalizedContentManager.LanguageCode.zh;

    public static void Initialize()
    {
        if (_initialized || ModEntry.SHelper == null) return;
        try
        {
            ModEntry.SHelper.Events.World.ObjectListChanged += OnObjectListChanged;
            ModEntry.SHelper.Events.World.FurnitureListChanged += OnFurnitureListChanged;
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
            ModEntry.SHelper.Events.World.FurnitureListChanged -= OnFurnitureListChanged;
            ModEntry.SHelper.Events.World.TerrainFeatureListChanged -= OnTerrainFeatureListChanged;
            _initialized = false;
        }
        catch (Exception ex)
        {
            ModEntry.SMonitor?.Log($"[WorldSubscriber] Event unsubscription failed: {ex.Message}", LogLevel.Warn);
        }
    }

    private static void OnFurnitureListChanged(object sender, FurnitureListChangedEventArgs e)
    {
        if (e.Location == null) return;
        foreach (var furniture in e.Added)
        {
            if (furniture == null) continue;
            string name = furniture.DisplayName ?? furniture.Name ?? (IsZh ? "家具" : "furniture");
            RecordPlacement(name);
        }
    }

    private static void OnObjectListChanged(object sender, ObjectListChangedEventArgs e)
    {
        if (e.Location == null) return;
        foreach (var pair in e.Added)
        {
            var obj = pair.Value;
            if (obj == null) continue;

            // 监听大型工艺装置（箱子、熔炉、稻草人、小桶等）
            if (obj.bigCraftable.Value || obj.Category == StardewValley.Object.BigCraftableCategory)
            {
                string name = obj.DisplayName ?? obj.Name ?? (IsZh ? "装置" : "equipment");
                RecordPlacement(name);
            }
        }
    }

    private static void OnTerrainFeatureListChanged(object sender, TerrainFeatureListChangedEventArgs e)
    {
        if (e.Location == null) return;
        foreach (var pair in e.Removed)
            HandleTerrainFeatureRemoved(pair.Value);
    }

    private static void RecordPlacement(string itemName)
    {
        string template = IsZh
            ? PerceptionManager.PickVariant(new[]
            {
                $"你注意到农夫刚刚在附近摆放了一件【{itemName}】。",
                $"农夫刚才在离你不远的地方安置了一个【{itemName}】。",
                $"你看到农夫正在旁边整理摆放着的【{itemName}】。"
            })
            : PerceptionManager.PickVariant(new[]
            {
                $"You noticed the farmer setting down a {itemName} nearby.",
                $"The farmer just placed a {itemName} not far from where you were standing.",
                $"You saw the farmer arranging a {itemName} a moment ago.",
            });

        PerceptionManager.Instance.Record("Place", template, null,
            ModEntry.Config.PerceptionActionLifetime, isLandmark: false);
    }

    private static void HandleTerrainFeatureRemoved(StardewValley.TerrainFeatures.TerrainFeature feature)
    {
        if (feature == null) return;
        try
        {
            // 只有成年大树（growthStage >= 5）被砍倒才记录，彻底过滤镰刀清理小树苗/杂草
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