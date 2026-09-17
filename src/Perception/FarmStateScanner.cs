using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using StardewValley;
using StardewValley.Buildings;
using StardewValley.TerrainFeatures;

namespace ValleytalkReborn;

internal static class FarmStateScanner
{
    private static string _cachedSummaryZh;
    private static string _cachedSummaryEn;
    private static int _cachedYear = -1;
    private static string _cachedSeason;
    private static int _cachedDay = -1;

    public static string BuildFarmSummary(bool isZh)
    {
        EnsureCacheFreshness();
        return isZh ? _cachedSummaryZh : _cachedSummaryEn;
    }

    private static void EnsureCacheFreshness()
    {
        bool isStale = _cachedYear != Game1.year
                     || _cachedSeason != Game1.currentSeason
                     || _cachedDay != Game1.dayOfMonth;

        if (!isStale) return;

        _cachedSummaryZh = BuildFarmSummaryInternal(isZh: true);
        _cachedSummaryEn = BuildFarmSummaryInternal(isZh: false);

        _cachedYear = Game1.year;
        _cachedSeason = Game1.currentSeason;
        _cachedDay = Game1.dayOfMonth;
    }

    public static void InvalidateCache()
    {
        _cachedYear = -1;
        _cachedDay = -1;
    }

    private static string BuildFarmSummaryInternal(bool isZh)
    {
        Farm farm = Game1.getFarm();
        if (farm == null) return null;

        var sb = new StringBuilder();

        // ── 1. 扫描农场室外作物 ──
        var (readyCrops, growingCrops, deadCrops, topReadyCropNames, topGrowingCropNames, topDeadCropNames) = ScanCropsInLocation(farm);

        // ── 2. 扫描果树（室外） ──
        var (fruitTreeProducing, fruitTreeGrowing, fruitTreeResting, topProducingFruits, topGrowingFruits, topRestingFruits) = ScanFruitTreesInLocation(farm);

        // ── 3. 扫描温室 ──
        var ghLocation = Game1.getLocationFromName("Greenhouse");
        bool isGreenhouseUnlocked = Game1.MasterPlayer.mailReceived.Contains("ccPantry") ||
                                    Game1.MasterPlayer.mailReceived.Contains("jojaGreenhouse");

        int ghReadyCrops = 0;
        int ghGrowingCrops = 0;
        List<string> ghTopReady = new();
        List<string> ghTopGrowing = new();

        if (isGreenhouseUnlocked && ghLocation != null)
        {
            (ghReadyCrops, ghGrowingCrops, _, ghTopReady, ghTopGrowing, _) = ScanCropsInLocation(ghLocation);
        }

        // ── 4. 扫描动物 ──
        int totalAnimals = 0;
        var animalCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var animal in farm.getAllFarmAnimals())
        {
            totalAnimals++;

            string typeName = animal.displayType;
            if (!string.IsNullOrWhiteSpace(typeName))
            {
                animalCounts[typeName] = animalCounts.GetValueOrDefault(typeName, 0) + 1;
            }
        }

        var topAnimals = animalCounts
            .OrderByDescending(kv => kv.Value)
            .Take(3)
            .Select(kv => kv.Key)
            .ToList();

        // ── 5. 扫描鱼塘 ──
        var fishPondInfos = new List<string>();
        foreach (var building in farm.buildings)
        {
            if (building is FishPond pond && pond.currentOccupants.Value > 0 && !string.IsNullOrWhiteSpace(pond.fishType.Value))
            {
                string fishName = SafeGetDisplayName(pond.fishType.Value) ?? (isZh ? "鱼" : "Fish");
                string pondFuzzy = GetFishPondOccupantsFuzzy(pond.currentOccupants.Value, pond.maxOccupants.Value, isZh);
                fishPondInfos.Add(isZh ? $"{fishName}（{pondFuzzy}）" : $"{fishName} ({pondFuzzy})");
            }
        }

        // ── 6. 格式化输出 ──
        if (isZh)
        {
            sb.AppendLine("### [农场经营状态]");
            sb.AppendLine("<farm_state>");

            if (readyCrops > 0)
            {
                string sample = topReadyCropNames.Count > 0 ? $"（包含: {string.Join("、", topReadyCropNames)} 等）" : "";
                sb.AppendLine($"- 农田作物: 田里有{GetCropQuantityFuzzy(readyCrops, isZh: true)}作物已成熟待收割{sample}。");
            }
            else if (growingCrops > 0)
            {
                string sample = topGrowingCropNames.Count > 0 ? $"（包含: {string.Join("、", topGrowingCropNames)} 等）" : "";
                sb.AppendLine($"- 农田作物: 田里有{GetCropQuantityFuzzy(growingCrops, isZh: true)}作物正在生长中{sample}。");
            }

            if (deadCrops > 0)
            {
                string sample = topDeadCropNames.Count > 0 ? $"（包含枯萎的: {string.Join("、", topDeadCropNames)} 等）" : "";
                sb.AppendLine($"- 农田异常: 发现了{GetDeadCropFuzzy(deadCrops, isZh: true)}枯萎死去的作物{sample}。");
            }

            // 果树展示
            if (fruitTreeProducing > 0)
            {
                string producingTrees = GetFruitTreeQuantityFuzzy(fruitTreeProducing, isZh: true);
                if (topProducingFruits.Count > 3)
                {
                    string sample = $"（主要包括: {string.Join("、", topProducingFruits)} 等）";
                    sb.AppendLine($"- 果园状态: 果树品种丰富，有{producingTrees}果树挂果待摘，涵盖多种不同品种{sample}。");
                }
                else
                {
                    string sample = topProducingFruits.Count > 0 ? $"（包含: {string.Join("、", topProducingFruits)}）" : "";
                    sb.AppendLine($"- 果园状态: 有{producingTrees}果树果实累累{sample}。");
                }
            }
            else if (fruitTreeGrowing > 0)
            {
                string growingTrees = GetFruitTreeQuantityFuzzy(fruitTreeGrowing, isZh: true);
                string sample = topGrowingFruits.Count > 0 ? $"（包含: {string.Join("、", topGrowingFruits)} 等）" : "";
                sb.AppendLine($"- 果园状态: 有{growingTrees}幼年果树正在生长中{sample}。");
            }
            else if (fruitTreeResting > 0)
            {
                string restingTrees = GetFruitTreeQuantityFuzzy(fruitTreeResting, isZh: true);
                string sample = topRestingFruits.Count > 0 ? $"（包含: {string.Join("、", topRestingFruits)} 等）" : "";
                sb.AppendLine($"- 果园状态: 种植了{restingTrees}成年果树{sample}，目前非挂果期。");
            }

            // 温室展示
            if (isGreenhouseUnlocked)
            {
                if (ghReadyCrops > 0)
                {
                    string sample = ghTopReady.Count > 0 ? $"（包含: {string.Join("、", ghTopReady)} 等）" : "";
                    sb.AppendLine($"- 室内温室: 温室内有{GetCropQuantityFuzzy(ghReadyCrops, isZh: true)}作物已成熟待收割{sample}。");
                }
                else if (ghGrowingCrops > 0)
                {
                    string sample = ghTopGrowing.Count > 0 ? $"（包含: {string.Join("、", ghTopGrowing)} 等）" : "";
                    sb.AppendLine($"- 室内温室: 温室内有{GetCropQuantityFuzzy(ghGrowingCrops, isZh: true)}作物正在生长中{sample}。");
                }
                else
                {
                    sb.AppendLine("- 室内温室: 温室已修复但目前空置，里面什么也没种。");
                }
            }
            else
            {
                sb.AppendLine("- 室内温室: 处于破损废弃状态（尚未修复）。");
            }

            if (totalAnimals > 0)
            {
                string animalTypesStr = topAnimals.Count > 0 ? $"（主要养殖: {string.Join("、", topAnimals)}）" : "";
                string animalQty = GetAnimalQuantityFuzzy(totalAnimals, isZh: true);
                sb.AppendLine($"- 农场牲畜: 养了{animalQty}牲畜{animalTypesStr}。");
            }

            if (fishPondInfos.Count > 0)
            {
                string ponds = string.Join("、", fishPondInfos.Take(3));
                sb.AppendLine($"- 鱼塘养殖: 养殖着 {ponds}。");
            }

            sb.AppendLine("</farm_state>");
        }
        else
        {
            sb.AppendLine("### [FARM OPERATION STATUS]");
            sb.AppendLine("<farm_state>");

            if (readyCrops > 0)
            {
                string sample = topReadyCropNames.Count > 0 ? $" (including: {string.Join(", ", topReadyCropNames)})" : "";
                sb.AppendLine($"- Outdoor Crops: {GetCropQuantityFuzzy(readyCrops, isZh: false)} crops are ripe and ready to harvest{sample}.");
            }
            else if (growingCrops > 0)
            {
                string sample = topGrowingCropNames.Count > 0 ? $" (including: {string.Join(", ", topGrowingCropNames)})" : "";
                sb.AppendLine($"- Outdoor Crops: {GetCropQuantityFuzzy(growingCrops, isZh: false)} crops growing{sample}.");
            }

            if (deadCrops > 0)
            {
                string sample = topDeadCropNames.Count > 0 ? $" (including withered: {string.Join(", ", topDeadCropNames)})" : "";
                sb.AppendLine($"- Field Warning: {GetDeadCropFuzzy(deadCrops, isZh: false)} withered crops spotted{sample}.");
            }

            if (fruitTreeProducing > 0)
            {
                string producingTrees = GetFruitTreeQuantityFuzzy(fruitTreeProducing, isZh: false);
                if (topProducingFruits.Count > 3)
                {
                    string sample = $" (mainly: {string.Join(", ", topProducingFruits)}, etc.)";
                    sb.AppendLine($"- Orchard: Diverse orchard with {producingTrees} trees bearing ripe fruit across multiple varieties{sample}.");
                }
                else
                {
                    string sample = topProducingFruits.Count > 0 ? $" (including: {string.Join(", ", topProducingFruits)})" : "";
                    sb.AppendLine($"- Orchard: {producingTrees} fruit trees are bearing ripe fruit{sample}.");
                }
            }
            else if (fruitTreeGrowing > 0)
            {
                string growingTrees = GetFruitTreeQuantityFuzzy(fruitTreeGrowing, isZh: false);
                string sample = topGrowingFruits.Count > 0 ? $" (including: {string.Join(", ", topGrowingFruits)})" : "";
                sb.AppendLine($"- Orchard: {growingTrees} young fruit trees are growing{sample}.");
            }
            else if (fruitTreeResting > 0)
            {
                string restingTrees = GetFruitTreeQuantityFuzzy(fruitTreeResting, isZh: false);
                string sample = topRestingFruits.Count > 0 ? $" (including: {string.Join(", ", topRestingFruits)})" : "";
                sb.AppendLine($"- Orchard: {restingTrees} mature fruit trees planted{sample}, currently out of season.");
            }

            if (isGreenhouseUnlocked)
            {
                if (ghReadyCrops > 0)
                {
                    string sample = ghTopReady.Count > 0 ? $" (including: {string.Join(", ", ghTopReady)})" : "";
                    sb.AppendLine($"- Greenhouse: {GetCropQuantityFuzzy(ghReadyCrops, isZh: false)} crops ripe and ready to harvest{sample}.");
                }
                else if (ghGrowingCrops > 0)
                {
                    string sample = ghTopGrowing.Count > 0 ? $" (including: {string.Join(", ", ghTopGrowing)})" : "";
                    sb.AppendLine($"- Greenhouse: {GetCropQuantityFuzzy(ghGrowingCrops, isZh: false)} crops growing{sample}.");
                }
                else
                {
                    sb.AppendLine("- Greenhouse: Repaired but currently empty with nothing planted.");
                }
            }
            else
            {
                sb.AppendLine("- Greenhouse: Dilapidated and abandoned (not yet repaired).");
            }

            if (totalAnimals > 0)
            {
                string animalTypesStr = topAnimals.Count > 0 ? $" (Mainly: {string.Join(", ", topAnimals)})" : "";
                string animalQty = GetAnimalQuantityFuzzy(totalAnimals, isZh: false);
                sb.AppendLine($"- Livestock: Raising {animalQty} animals{animalTypesStr}.");
            }

            if (fishPondInfos.Count > 0)
            {
                string ponds = string.Join(", ", fishPondInfos.Take(3));
                sb.AppendLine($"- Fish Ponds: Raising {ponds}.");
            }

            sb.AppendLine("</farm_state>");
        }

        return sb.Length > 30 ? sb.ToString() : null;
    }

    private static string GetCropQuantityFuzzy(int count, bool isZh)
    {
        if (isZh)
        {
            if (count < 10) return "零星几株";
            if (count < 30) return "一小片";
            if (count < 80) return "成片";
            if (count < 200) return "一大片";
            return "漫野成片";
        }
        else
        {
            if (count < 10) return "a few";
            if (count < 30) return "a small patch of";
            if (count < 80) return "a sizable patch of";
            if (count < 200) return "a large field of";
            return "sprawling fields of";
        }
    }

    private static string GetDeadCropFuzzy(int count, bool isZh)
    {
        if (isZh)
        {
            if (count < 5) return "零星几株";
            if (count < 20) return "一小片";
            return "成片枯萎";
        }
        else
        {
            if (count < 5) return "a few";
            if (count < 20) return "a small patch of";
            return "swaths of";
        }
    }

    private static string GetFruitTreeQuantityFuzzy(int count, bool isZh)
    {
        if (isZh)
        {
            if (count <= 2) return "一两棵";
            if (count <= 6) return "几棵";
            if (count <= 15) return "一小片果林";
            return "一大片果园";
        }
        else
        {
            if (count <= 2) return "a couple of";
            if (count <= 6) return "a few";
            if (count <= 15) return "a small grove of";
            return "a sprawling orchard of";
        }
    }

    private static string GetAnimalQuantityFuzzy(int count, bool isZh)
    {
        if (isZh)
        {
            if (count <= 2) return "一两只";
            if (count <= 6) return "几只";
            if (count <= 15) return "一小群";
            if (count <= 30) return "一大群";
            return "成群结队";
        }
        else
        {
            if (count <= 2) return "a couple of";
            if (count <= 6) return "a few";
            if (count <= 15) return "a small herd of";
            if (count <= 30) return "a large herd of";
            return "a massive herd of";
        }
    }

    private static string GetFishPondOccupantsFuzzy(int currentOccupants, int maxOccupants, bool isZh)
    {
        if (isZh)
        {
            if (currentOccupants >= 8 || currentOccupants >= maxOccupants) return "满满一塘";
            if (currentOccupants <= 2) return "零星几尾";
            return "数尾";
        }
        else
        {
            if (currentOccupants >= 8 || currentOccupants >= maxOccupants) return "a pond teeming with";
            if (currentOccupants <= 2) return "a couple of";
            return "a small school of";
        }
    }

    private static (int readyCount, int growingCount, int deadCount, List<string> topReady, List<string> topGrowing, List<string> topDead)
        ScanCropsInLocation(GameLocation location)
    {
        if (location == null) return (0, 0, 0, new List<string>(), new List<string>(), new List<string>());

        int readyCount = 0;
        int growingCount = 0;
        int deadCount = 0;

        var readyMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var growingMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var deadMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var pair in location.terrainFeatures.Pairs)
        {
            if (pair.Value is HoeDirt dirt && dirt.crop != null)
            {
                // 解析作物产物 ID（兼容野生种子与空值回退）
                string harvestId = dirt.crop.indexOfHarvest.Value;
                if (string.IsNullOrWhiteSpace(harvestId))
                {
                    harvestId = dirt.crop.GetData()?.HarvestItemId;
                }
                if (dirt.crop.isWildSeedCrop() && !string.IsNullOrWhiteSpace(dirt.crop.whichForageCrop.Value))
                {
                    harvestId = dirt.crop.whichForageCrop.Value;
                }

                string cropName = SafeGetDisplayName(harvestId);

                if (dirt.crop.dead.Value)
                {
                    deadCount++;
                    if (!string.IsNullOrWhiteSpace(cropName))
                    {
                        deadMap[cropName] = deadMap.GetValueOrDefault(cropName, 0) + 1;
                    }
                    continue;
                }

                bool isReady = dirt.crop.currentPhase.Value >= dirt.crop.phaseDays.Count - 1;

                if (isReady)
                {
                    readyCount++;
                    if (!string.IsNullOrWhiteSpace(cropName))
                    {
                        readyMap[cropName] = readyMap.GetValueOrDefault(cropName, 0) + 1;
                    }
                }
                else
                {
                    growingCount++;
                    if (!string.IsNullOrWhiteSpace(cropName))
                    {
                        growingMap[cropName] = growingMap.GetValueOrDefault(cropName, 0) + 1;
                    }
                }
            }
        }

        var topReady = readyMap.OrderByDescending(kv => kv.Value).Take(3).Select(kv => kv.Key).ToList();
        var topGrowing = growingMap.OrderByDescending(kv => kv.Value).Take(3).Select(kv => kv.Key).ToList();
        var topDead = deadMap.OrderByDescending(kv => kv.Value).Take(3).Select(kv => kv.Key).ToList();

        return (readyCount, growingCount, deadCount, topReady, topGrowing, topDead);
    }

    private static (int producingCount, int growingCount, int restingCount, List<string> topProducing, List<string> topGrowing, List<string> topResting)
        ScanFruitTreesInLocation(GameLocation location)
    {
        if (location == null) return (0, 0, 0, new List<string>(), new List<string>(), new List<string>());

        int producingCount = 0;
        int growingCount = 0;
        int restingCount = 0;

        var producingMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var growingMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var restingMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var pair in location.terrainFeatures.Pairs)
        {
            if (pair.Value is FruitTree tree)
            {
                // 1. 优先从当前挂果实体获取名称
                string fruitName = null;
                if (tree.fruit != null && tree.fruit.Count > 0 && tree.fruit[0] != null)
                {
                    fruitName = SafeGetDisplayName(tree.fruit[0].QualifiedItemId);
                }

                // 2. 若未挂果，通过 1.6 的 FruitTreeData 获取该树种的果实名称
                if (string.IsNullOrWhiteSpace(fruitName))
                {
                    var data = tree.GetData();
                    var fruitData = data?.Fruit?.FirstOrDefault();
                    if (fruitData != null && !string.IsNullOrWhiteSpace(fruitData.ItemId))
                    {
                        fruitName = SafeGetDisplayName(fruitData.ItemId);
                    }
                }

                // 1.6 挂果判定：直接依据 tree.fruit 列表
                bool hasFruit = tree.fruit != null && tree.fruit.Count > 0;
                bool isGrowing = tree.growthStage.Value < FruitTree.treeStage;

                if (hasFruit)
                {
                    producingCount++;
                    if (!string.IsNullOrWhiteSpace(fruitName))
                    {
                        producingMap[fruitName] = producingMap.GetValueOrDefault(fruitName, 0) + 1;
                    }
                }
                else if (isGrowing)
                {
                    growingCount++;
                    if (!string.IsNullOrWhiteSpace(fruitName))
                    {
                        growingMap[fruitName] = growingMap.GetValueOrDefault(fruitName, 0) + 1;
                    }
                }
                else
                {
                    // 成年果树但当前非产果期/已被采摘
                    restingCount++;
                    if (!string.IsNullOrWhiteSpace(fruitName))
                    {
                        restingMap[fruitName] = restingMap.GetValueOrDefault(fruitName, 0) + 1;
                    }
                }
            }
        }

        var topProducing = producingMap.OrderByDescending(kv => kv.Value).Take(3).Select(kv => kv.Key).ToList();
        var topGrowing = growingMap.OrderByDescending(kv => kv.Value).Take(3).Select(kv => kv.Key).ToList();
        var topResting = restingMap.OrderByDescending(kv => kv.Value).Take(3).Select(kv => kv.Key).ToList();

        return (producingCount, growingCount, restingCount, topProducing, topGrowing, topResting);
    }

    /// <summary>
    /// 星露谷 1.6 原生只读元数据获取，免实体实例化且原生拦截 Error 物品
    /// </summary>
    private static string SafeGetDisplayName(string itemId)
    {
        if (string.IsNullOrWhiteSpace(itemId)) return null;

        try
        {
            var parsedData = ItemRegistry.GetData(itemId);
            if (parsedData == null || parsedData.IsErrorItem)
            {
                return null;
            }

            if (!string.IsNullOrEmpty(parsedData.QualifiedItemId) &&
                parsedData.QualifiedItemId.Contains("Error", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            string displayName = parsedData.DisplayName;
            if (string.IsNullOrWhiteSpace(displayName) ||
                displayName.Contains("Error", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return displayName;
        }
        catch
        {
            return null;
        }
    }
}