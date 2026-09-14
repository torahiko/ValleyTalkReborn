namespace ValleytalkReborn.Scanning.Detectors;

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewValley;
using StardewValley.Locations;
using StardewValley.Monsters;
using StardewValley.Objects;
using StardewValley.TerrainFeatures;

/// <summary>
/// Detects entities (NPCs, animals), terrain features, furniture, and bushes
/// within a bounded window. Stateless; all state is passed in via <see cref="Scan"/>.
/// </summary>
internal sealed class EntityFeatureDetector : IEnvironmentDetector
{
    private static bool IsChineseLanguage =>
        LocalizedContentManager.CurrentLanguageCode == LocalizedContentManager.LanguageCode.zh;

    // ── 三梯队优先级白名单 + 黑名单 ──
    private static readonly HashSet<string> Tier1_Creatures = new(StringComparer.OrdinalIgnoreCase)
    {
        "Chicken", "Cow", "Sheep", "Pig", "Goat", "Duck", "Rabbit", "Dinosaur",
        "Horse", "Cat", "Dog", "Pet",
    };

    private static readonly HashSet<string> Tier2_ActiveDevices = new(StringComparer.OrdinalIgnoreCase)
    {
        "Keg", "Preserves Jar", "Cheese Press", "Mayonnaise Machine", "Oil Maker",
        "Loom", "Crystalarium", "Bee House", "Mushroom Box", "Vinegar Jug",
        "Furnace", "Campfire", "Torch", "Brazier", "Fire Pit", "Heater",
        "Crab Pot", "Fish Pond",
        "Junimo Hut", "Slime Hutch", "Incubator", "Worm Bin",
    };

    private static readonly HashSet<string> Tier3_Landmarks = new(StringComparer.OrdinalIgnoreCase)
    {
        "Statue Of Endless Fortune", "Statue Of Perfection", "Statue Of True Perfection",
        "Gold Clock", "Obelisk", "Totem", "Shipping Bin", "Silo",
        "Jack-O-Lantern", "Rarecrow", "Scarecrow", "Seasonal Decor",
        "Grandfather Clock", "Bookcase", "Chandelier", "Fireplace", "Piano",
    };

    private static readonly HashSet<string> Blacklist = new(StringComparer.OrdinalIgnoreCase)
    {
        // 地面、地板与道路铺设
        "Stone Floor", "Wood Floor", "Straw Floor", "Weathered Floor", "Crystal Floor",
        "Brick Floor", "Rustic Plank Floor", "Stone Walkway Floor",
        "Stepping Stone Path", "Gravel Path", "Wood Path", "Cobblestone Path",

        // 容器与储物
        "Chest", "Stone Chest", "Junimo Chest", "Big Chest", "Big Stone Chest",

        // 地面挖掘点与生成点
        "Artifact Spot", "590", "Seed Spot", "SeedSpot", "Supply Crate", "SupplyCrate",
        "远古斑点", "发掘点", "挖掘点", "発掘ポイント", "种子斑点",

        // 地面自然杂物
        "Weeds", "Stone", "Twig", "Fiber",
        "杂草", "杂草丛", "石头", "树枝", "纤维",

        // 废弃物与垃圾
        "Trash", "Driftwood", "Soggy Newspaper", "Broken CD", "Broken Glasses", "Rotten Plant",
        "Joja Cola", "Trash Can",

        // 基础农场围栏与灌溉工具
        "Wood Fence", "Stone Fence", "Iron Fence", "Hardwood Fence", "Gate",
        "Sprinkler", "Quality Sprinkler", "Iridium Sprinkler",
        "Workbench", "Recycling Machine", "Mini-Obelisk",
    };

    private static readonly string[] FurnitureNameBlacklist = new[]
    {
        "垂叶", "台灯", "地毯", "窗户", "植物", "小茶几", "灯", "盆栽", "盆景",
        "地板", "墙纸", "挂件", "桌布", "饰品", "窗帘", "百叶", "纱帘", "花瓶", "花盆",
        "壁篮", "挂饰", "标志", "旗帜", "海报", "挂画", "火把", "烛台",
        "Rug", "Lamp", "Plant", "Foliage", "Window", "Curtain", "Wallpaper", "Flooring",
        "Vase", "Pot", "Basket", "Banner", "Sign", "Poster", "Painting", "Sconce", "Garland"
    };

    /// <inheritdoc/>
    public void Scan(GameLocation location, Vector2 centerTile, int radiusTiles, NPC focalNpc, List<ScanItem> buffer)
    {
        if (location == null) return;

        // ── 1. NPC 扫描（跳过焦点 NPC 与敌对 Monster）──
        foreach (var character in location.characters)
        {
            if (character == null || character == focalNpc) continue;
            if (character is Monster) continue;

            var charTile = character.Tile;
            if (Math.Abs(charTile.X - centerTile.X) > radiusTiles ||
                Math.Abs(charTile.Y - centerTile.Y) > radiusTiles) continue;

            string name = character.displayName ?? character.Name;
            if (!string.IsNullOrWhiteSpace(name))
                buffer.Add(new ScanItem(name, charTile, 1, ScanItem.TypeNpc));
        }

        // ── 2. 窗口段：地形特征 + 放置物品 ──
        int minX = (int)centerTile.X - radiusTiles;
        int maxX = (int)centerTile.X + radiusTiles;
        int minY = (int)centerTile.Y - radiusTiles;
        int maxY = (int)centerTile.Y + radiusTiles;

        for (int x = minX; x <= maxX; x++)
        {
            for (int y = minY; y <= maxY; y++)
            {
                var tile = new Vector2(x, y);

                // 2.1 地形特征
                if (location.terrainFeatures.TryGetValue(tile, out var feature) && feature != null)
                {
                    string desc = DescribeTerrainFeature(feature, location);
                    if (!string.IsNullOrEmpty(desc))
                    {
                        bool isTier1 = desc.Contains("crop", StringComparison.OrdinalIgnoreCase)
                                    || desc.Contains("fruit", StringComparison.OrdinalIgnoreCase)
                                    || desc.Contains("树")
                                    || desc.Contains("作物");

                        buffer.Add(new ScanItem(desc, tile, isTier1 ? 1 : 3, ScanItem.TypeTerrain));
                    }
                }

                // 2.2 放置物品
                if (location.Objects.TryGetValue(tile, out var obj) && obj != null)
                {
                    if (IsBlacklistedObject(obj)) continue;

                    string desc = DescribeObject(obj);
                    if (!string.IsNullOrEmpty(desc))
                    {
                        int priority = 3;
                        string internalObjName = obj.Name ?? string.Empty;
                        string name = obj.DisplayName ?? string.Empty;

                        if (Tier1_Creatures.Contains(internalObjName) || Tier1_Creatures.Contains(name))
                            priority = 1;
                        else if (Tier2_ActiveDevices.Contains(internalObjName) || Tier2_ActiveDevices.Contains(name))
                            priority = 2;
                        else if (Tier3_Landmarks.Contains(internalObjName) || Tier3_Landmarks.Contains(name))
                            priority = 3;

                        buffer.Add(new ScanItem(desc, tile, priority, ScanItem.TypeObject));
                    }
                }
            }
        }

        // ── 3. 家具段 ──
        if (location is DecoratableLocation decoratable)
        {
            foreach (var furniture in decoratable.furniture)
            {
                if (furniture == null) continue;
                var fTile = furniture.TileLocation;
                if (Math.Abs(fTile.X - centerTile.X) > radiusTiles ||
                    Math.Abs(fTile.Y - centerTile.Y) > radiusTiles) continue;

                int fType = furniture.furniture_type.Value;
                if (fType == Furniture.painting ||
                    fType == Furniture.rug ||
                    fType == Furniture.window)
                {
                    continue;
                }

                string name = SanitizeObjectName(furniture.DisplayName);
                if (string.IsNullOrWhiteSpace(name)) continue;

                string internalName = furniture.Name ?? string.Empty;
                string fItemId = furniture.ItemId ?? string.Empty;

                bool isNoise = FurnitureNameBlacklist.Any(black =>
                    name.Contains(black, StringComparison.OrdinalIgnoreCase) ||
                    internalName.Contains(black, StringComparison.OrdinalIgnoreCase) ||
                    fItemId.Contains(black, StringComparison.OrdinalIgnoreCase));

                if (isNoise) continue;

                int priority = (Tier2_ActiveDevices.Contains(name) || Tier2_ActiveDevices.Contains(internalName)) ? 2 : 3;

                buffer.Add(new ScanItem(name, fTile, priority, ScanItem.TypeFurniture));
            }
        }

        // ── 4. 灌木段 ──
        foreach (var feature in location.largeTerrainFeatures)
        {
            if (feature == null) continue;
            var fTile = feature.Tile;
            if (Math.Abs(fTile.X - centerTile.X) > radiusTiles ||
                Math.Abs(fTile.Y - centerTile.Y) > radiusTiles) continue;

            if (feature is Bush bush)
            {
                string desc = DescribeBush(bush);
                if (!string.IsNullOrEmpty(desc))
                    buffer.Add(new ScanItem(desc, fTile, 3, ScanItem.TypeTerrain));
            }
        }

        // ── 5. 农场动物段 ──
        if (location is Farm farm)
        {
            foreach (var animal in farm.getAllFarmAnimals())
            {
                if (animal == null) continue;

                Vector2? pos = animal.Position;
                if (!pos.HasValue) continue;

                var tile = new Vector2((int)(pos.Value.X / 64f), (int)(pos.Value.Y / 64f));

                if (Math.Abs(tile.X - centerTile.X) > radiusTiles ||
                    Math.Abs(tile.Y - centerTile.Y) > radiusTiles) continue;

                string typeLabel = animal.displayType;
                if (string.IsNullOrEmpty(typeLabel))
                    typeLabel = animal.type?.ToString();
                if (string.IsNullOrEmpty(typeLabel)) continue;

                string name = animal.Name;
                string desc = IsChineseLanguage
                    ? $"{typeLabel}（{name}）"
                    : $"{typeLabel} ({name})";

                buffer.Add(new ScanItem(desc, tile, 1, ScanItem.TypeAnimal));
            }
        }
    }

    // ─────────────────────────────────────────────
    //  黑名单安全判定（多语言与底层元数据双重兜底）
    // ─────────────────────────────────────────────
    private static bool IsBlacklistedObject(StardewValley.Object obj)
    {
        if (obj == null) return true;

        string name = obj.DisplayName ?? string.Empty;
        string internalName = obj.Name ?? string.Empty;
        string itemId = obj.ItemId ?? string.Empty;

        if (string.IsNullOrWhiteSpace(name)) return true;

        if (Blacklist.Contains(name) ||
            Blacklist.Contains(internalName) ||
            Blacklist.Contains(itemId))
        {
            return true;
        }

        if (obj.ParentSheetIndex == 590 ||
            itemId == "590" ||
            itemId.Equals("SeedSpot", StringComparison.OrdinalIgnoreCase) ||
            internalName.IndexOf("Artifact Spot", StringComparison.OrdinalIgnoreCase) >= 0 ||
            internalName.IndexOf("Seed Spot", StringComparison.OrdinalIgnoreCase) >= 0 ||
            name.Contains("远古斑点") ||
            name.Contains("発掘ポイント") ||
            name.Contains("挖掘点"))
        {
            return true;
        }

        return false;
    }

    // ─────────────────────────────────────────────
    //  物品描述（双语）
    // ─────────────────────────────────────────────
    private static string DescribeObject(StardewValley.Object obj)
    {
        if (obj == null) return null;
        if (IsBlacklistedObject(obj)) return null;

        string name = SanitizeObjectName(obj.DisplayName);
        if (string.IsNullOrWhiteSpace(name)) return null;

        if (obj.bigCraftable.Value)
            return name;

        if (IsChineseLanguage)
        {
            return obj.Category switch
            {
                StardewValley.Object.SeedsCategory     => $"种子（{name}）",
                StardewValley.Object.flowersCategory   => $"花卉（{name}）",
                StardewValley.Object.FruitsCategory    => $"水果（{name}）",
                StardewValley.Object.VegetableCategory => $"蔬菜（{name}）",
                StardewValley.Object.GemCategory       => $"宝石（{name}）",
                StardewValley.Object.mineralsCategory  => $"矿物（{name}）",
                _ => name
            };
        }
        else
        {
            return obj.Category switch
            {
                StardewValley.Object.SeedsCategory     => $"seeds ({name})",
                StardewValley.Object.flowersCategory   => $"flowers ({name})",
                StardewValley.Object.FruitsCategory    => $"fruit ({name})",
                StardewValley.Object.VegetableCategory => $"vegetables ({name})",
                StardewValley.Object.GemCategory       => $"gem ({name})",
                StardewValley.Object.mineralsCategory  => $"mineral ({name})",
                _ => name
            };
        }
    }

    // ─────────────────────────────────────────────
    //  显示名清洗
    // ─────────────────────────────────────────────
    private static string SanitizeObjectName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;

        if (name.StartsWith("错误物品", StringComparison.Ordinal)
            || name.StartsWith("Error Item", StringComparison.OrdinalIgnoreCase))
            return null;

        if (name.Contains("(RF.", StringComparison.Ordinal)) return null;

        return name;
    }

    // ─────────────────────────────────────────────
    //  地形特征描述（双语） ★F1：FruitTree 置于 Tree 之前
    // ─────────────────────────────────────────────
    private static string DescribeTerrainFeature(TerrainFeature feature, GameLocation location)
    {
        if (IsChineseLanguage)
        {
            return feature switch
            {
                FruitTree fruitTree when fruitTree.fruit != null && fruitTree.fruit.Count > 0
                    => "挂满果实的果树",
                FruitTree => "果树",
                Tree tree when tree.growthStage.Value >= Tree.treeStage => "大树",
                Grass => (Game1.currentSeason == "winter" || location?.Name == "Desert")
                    ? "干枯的草丛" : null,
                HoeDirt dirt when dirt.crop != null && dirt.readyForHarvest() => "成熟可收割的作物",
                HoeDirt dirt when dirt.crop != null => "生长的作物",
                Flooring => null,
                _ => null
            };
        }
        else
        {
            return feature switch
            {
                FruitTree fruitTree when fruitTree.fruit != null && fruitTree.fruit.Count > 0
                    => "a fruit tree with ripe fruit",
                FruitTree => "a fruit tree",
                Tree tree when tree.growthStage.Value >= Tree.treeStage => "a large tree",
                Grass => (Game1.currentSeason == "winter" || location?.Name == "Desert")
                    ? "patch of dry grass" : null,
                HoeDirt dirt when dirt.crop != null && dirt.readyForHarvest() => "a ready-to-harvest crop",
                HoeDirt dirt when dirt.crop != null => "growing crops",
                Flooring => null,
                _ => null
            };
        }
    }

    // ─────────────────────────────────────────────
    //  灌木描述（双语）
    // ─────────────────────────────────────────────
    private static string DescribeBush(Bush bush)
    {
        if (bush == null) return null;

        switch (bush.size.Value)
        {
            case 0:
                return IsChineseLanguage ? "矮灌木" : "a small bush";
            case 1:
                return IsChineseLanguage ? "灌木丛" : "a bush";
            case 2:
                return IsChineseLanguage ? "大型灌木" : "a large bush";
            default:
                return IsChineseLanguage ? "灌木丛" : "a bush";
        }
    }
}
