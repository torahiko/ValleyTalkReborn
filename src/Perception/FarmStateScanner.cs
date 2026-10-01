using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Buildings;
using StardewValley.TerrainFeatures;
using StardewValley.TokenizableStrings;

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
        int ghDeadCrops = 0;
        List<string> ghTopReady = new();
        List<string> ghTopGrowing = new();
        List<string> ghTopDead = new();

        int ghFruitProducing = 0;
        int ghFruitGrowing = 0;
        int ghFruitResting = 0;
        List<string> ghTopProducingFruits = new();
        List<string> ghTopGrowingFruits = new();
        List<string> ghTopRestingFruits = new();

        if (isGreenhouseUnlocked && ghLocation != null)
        {
            (ghReadyCrops, ghGrowingCrops, ghDeadCrops, ghTopReady, ghTopGrowing, ghTopDead) = ScanCropsInLocation(ghLocation);
            (ghFruitProducing, ghFruitGrowing, ghFruitResting, ghTopProducingFruits, ghTopGrowingFruits, ghTopRestingFruits) = ScanFruitTreesInLocation(ghLocation);
        }
        else if (isGreenhouseUnlocked && isZh)
        {
            // BOUNDARY: 已解锁但温室地点不可用；中英两路缓存重建只记录一次
            ModEntry.SMonitor?.Log("[FarmStateScanner] Greenhouse is unlocked but its location is unavailable; planting state omitted", LogLevel.Warn);
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

            // 果树展示（室外果园：不含温室输入）
            if (fruitTreeProducing > 0)
            {
                string producingTrees = GetFruitTreeQuantityFuzzy(fruitTreeProducing, isZh: true);
                string sample = topProducingFruits.Count > 0 ? $"（包含: {string.Join("、", topProducingFruits)}）" : "";
                sb.AppendLine($"- 室外果园: 有{producingTrees}果树果实累累{sample}。");
            }
            else if (fruitTreeGrowing > 0)
            {
                string growingTrees = GetFruitTreeQuantityFuzzy(fruitTreeGrowing, isZh: true);
                string sample = topGrowingFruits.Count > 0 ? $"（包含: {string.Join("、", topGrowingFruits)} 等）" : "";
                sb.AppendLine($"- 室外果园: 有{growingTrees}幼年果树正在生长中{sample}。");
            }
            else if (fruitTreeResting > 0)
            {
                string restingTrees = GetFruitTreeQuantityFuzzy(fruitTreeResting, isZh: true);
                string sample = topRestingFruits.Count > 0 ? $"（包含: {string.Join("、", topRestingFruits)} 等）" : "";
                sb.AppendLine($"- 室外果园: 种植了{restingTrees}成年果树{sample}，目前非挂果期。");
            }

            // 温室展示
            sb.Append(BuildGreenhouseSection(isZh: true, isGreenhouseUnlocked, ghLocation != null,
                (ghReadyCrops, ghGrowingCrops, ghDeadCrops, ghTopReady, ghTopGrowing, ghTopDead),
                (ghFruitProducing, ghFruitGrowing, ghFruitResting, ghTopProducingFruits, ghTopGrowingFruits, ghTopRestingFruits)));

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
                string sample = topProducingFruits.Count > 0 ? $" (including: {string.Join(", ", topProducingFruits)})" : "";
                sb.AppendLine($"- Outdoor Orchard: {producingTrees} fruit trees are bearing ripe fruit{sample}.");
            }
            else if (fruitTreeGrowing > 0)
            {
                string growingTrees = GetFruitTreeQuantityFuzzy(fruitTreeGrowing, isZh: false);
                string sample = topGrowingFruits.Count > 0 ? $" (including: {string.Join(", ", topGrowingFruits)})" : "";
                sb.AppendLine($"- Outdoor Orchard: {growingTrees} young fruit trees are growing{sample}.");
            }
            else if (fruitTreeResting > 0)
            {
                string restingTrees = GetFruitTreeQuantityFuzzy(fruitTreeResting, isZh: false);
                string sample = topRestingFruits.Count > 0 ? $" (including: {string.Join(", ", topRestingFruits)})" : "";
                sb.AppendLine($"- Outdoor Orchard: {restingTrees} mature fruit trees planted{sample}, currently out of season.");
            }

            sb.Append(BuildGreenhouseSection(isZh: false, isGreenhouseUnlocked, ghLocation != null,
                (ghReadyCrops, ghGrowingCrops, ghDeadCrops, ghTopReady, ghTopGrowing, ghTopDead),
                (ghFruitProducing, ghFruitGrowing, ghFruitResting, ghTopProducingFruits, ghTopGrowingFruits, ghTopRestingFruits)));

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

    /// <summary>
    /// 温室展示块纯格式化：仅整理传入的作物与果树事实，不读取 Game1、不写状态。
    /// </summary>
    internal static string BuildGreenhouseSection(
        bool isZh,
        bool isUnlocked,
        bool isLocationAvailable,
        (int readyCount, int growingCount, int deadCount,
         List<string> topReady, List<string> topGrowing,
         List<string> topDead) crops,
        (int producingCount, int growingCount, int restingCount,
         List<string> topProducing, List<string> topGrowing,
         List<string> topResting) fruitTrees)
    {
        var sb = new StringBuilder();

        if (!isUnlocked)
        {
            sb.AppendLine(isZh ? "- 室内温室: 处于破损废弃状态（尚未修复）。"
                               : "- Greenhouse: Dilapidated and abandoned (not yet repaired).");
            return sb.ToString();
        }

        if (!isLocationAvailable)
        {
            sb.AppendLine(isZh ? "- 室内温室: 温室已修复，当前无法确认内部种植情况。"
                               : "- Greenhouse: Repaired; its planting state is currently unavailable.");
            return sb.ToString();
        }

        // 作物：成熟优先于生长
        if (crops.readyCount > 0)
        {
            string sample = crops.topReady.Count > 0
                ? (isZh ? $"（包含: {string.Join("、", crops.topReady)} 等）" : $" (including: {string.Join(", ", crops.topReady)})")
                : "";
            sb.AppendLine(isZh
                ? $"- 室内温室: 温室内有{GetCropQuantityFuzzy(crops.readyCount, isZh: true)}作物已成熟待收割{sample}。"
                : $"- Greenhouse: {GetCropQuantityFuzzy(crops.readyCount, isZh: false)} crops ripe and ready to harvest{sample}.");
        }
        else if (crops.growingCount > 0)
        {
            string sample = crops.topGrowing.Count > 0
                ? (isZh ? $"（包含: {string.Join("、", crops.topGrowing)} 等）" : $" (including: {string.Join(", ", crops.topGrowing)})")
                : "";
            sb.AppendLine(isZh
                ? $"- 室内温室: 温室内有{GetCropQuantityFuzzy(crops.growingCount, isZh: true)}作物正在生长中{sample}。"
                : $"- Greenhouse: {GetCropQuantityFuzzy(crops.growingCount, isZh: false)} crops growing{sample}.");
        }

        if (crops.deadCount > 0)
        {
            string sample = crops.topDead.Count > 0
                ? (isZh ? $"（包含枯萎的: {string.Join("、", crops.topDead)} 等）" : $" (including withered: {string.Join(", ", crops.topDead)})")
                : "";
            sb.AppendLine(isZh
                ? $"- 温室作物异常: 发现了{GetDeadCropFuzzy(crops.deadCount, isZh: true)}枯萎死去的作物{sample}。"
                : $"- Greenhouse Crop Warning: {GetDeadCropFuzzy(crops.deadCount, isZh: false)} withered crops spotted{sample}.");
        }

        // 果树：挂果 → 幼树 → 成年树当前没有挂果（只描述事实，不推断非产果季）
        if (fruitTrees.producingCount > 0)
        {
            string sample = fruitTrees.topProducing.Count > 0
                ? (isZh ? $"（包含: {string.Join("、", fruitTrees.topProducing)}）" : $" (including: {string.Join(", ", fruitTrees.topProducing)})")
                : "";
            sb.AppendLine(isZh
                ? $"- 温室果树: 有{GetFruitTreeQuantityFuzzy(fruitTrees.producingCount, isZh: true)}果树果实累累{sample}。"
                : $"- Greenhouse Fruit Trees: {GetFruitTreeQuantityFuzzy(fruitTrees.producingCount, isZh: false)} fruit trees are bearing ripe fruit{sample}.");
        }
        else if (fruitTrees.growingCount > 0)
        {
            string sample = fruitTrees.topGrowing.Count > 0
                ? (isZh ? $"（包含: {string.Join("、", fruitTrees.topGrowing)} 等）" : $" (including: {string.Join(", ", fruitTrees.topGrowing)})")
                : "";
            sb.AppendLine(isZh
                ? $"- 温室果树: 有{GetFruitTreeQuantityFuzzy(fruitTrees.growingCount, isZh: true)}幼年果树正在生长中{sample}。"
                : $"- Greenhouse Fruit Trees: {GetFruitTreeQuantityFuzzy(fruitTrees.growingCount, isZh: false)} young fruit trees are growing{sample}.");
        }
        else if (fruitTrees.restingCount > 0)
        {
            string sample = fruitTrees.topResting.Count > 0
                ? (isZh ? $"（包含: {string.Join("、", fruitTrees.topResting)} 等）" : $" (including: {string.Join(", ", fruitTrees.topResting)})")
                : "";
            sb.AppendLine(isZh
                ? $"- 温室果树: 种植了{GetFruitTreeQuantityFuzzy(fruitTrees.restingCount, isZh: true)}成年果树{sample}，目前没有挂果。"
                : $"- Greenhouse Fruit Trees: {GetFruitTreeQuantityFuzzy(fruitTrees.restingCount, isZh: false)} mature fruit trees planted{sample}, currently bearing no fruit.");
        }

        bool hasNoCrops = crops.readyCount == 0 && crops.growingCount == 0 && crops.deadCount == 0;
        bool hasNoFruitTrees = fruitTrees.producingCount == 0 && fruitTrees.growingCount == 0 && fruitTrees.restingCount == 0;
        if (hasNoCrops && hasNoFruitTrees)
        {
            sb.AppendLine(isZh ? "- 室内温室: 温室已修复，目前没有作物或果树。"
                               : "- Greenhouse: Repaired; currently contains no crops or fruit trees.");
        }

        return sb.ToString();
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
                string fruitName = ResolveFruitTreeName(tree);

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
    /// 四级容错果实名称解析：活跃挂果实体 → 1.6 FruitTreeData 元数据 → 树苗 ID 原版常数 → 空值。
    /// </summary>
    private static string ResolveFruitTreeName(FruitTree tree)
    {
        // Step 1 [活跃实体挂果]: 当前挂果列表是果实名称的第一权威来源
        if (tree.fruit != null && tree.fruit.Count > 0)
        {
            foreach (var item in tree.fruit)
            {
                if (item == null) continue;

                if (!string.IsNullOrWhiteSpace(item.DisplayName))
                {
                    return item.DisplayName;
                }

                if (!string.IsNullOrWhiteSpace(item.QualifiedItemId))
                {
                    string disp = SafeGetDisplayName(item.QualifiedItemId);
                    if (!string.IsNullOrWhiteSpace(disp)) return disp;
                }
            }
        }

        // Step 2 [1.6 FruitTreeData 元数据]: 未挂果时按树种元数据推导果实名
        try
        {
            var data = tree.GetData();
            if (data != null)
            {
                if (data.Fruit != null)
                {
                    foreach (var fruitDrop in data.Fruit)
                    {
                        string targetId = fruitDrop.ItemId ?? fruitDrop.Id;
                        if (string.IsNullOrWhiteSpace(targetId) && fruitDrop.RandomItemId?.Count > 0)
                        {
                            targetId = fruitDrop.RandomItemId[0];
                        }
                        if (!string.IsNullOrWhiteSpace(targetId))
                        {
                            string fruitDisp = SafeGetDisplayName(targetId);
                            if (!string.IsNullOrWhiteSpace(fruitDisp)) return fruitDisp;
                        }
                    }
                }

                if (!string.IsNullOrWhiteSpace(data.DisplayName))
                {
                    string treeDisp = TokenParser.ParseText(data.DisplayName);
                    string clean = ExtractFruitNameFromTreeName(treeDisp);
                    if (!string.IsNullOrWhiteSpace(clean)) return clean;
                }
            }
        }
        catch
        {
            // BOUNDARY: 离线或 FruitTreeData 数据异常，降级到 treeId 层解析
        }

        // Step 3 [TreeId / 树苗解析]: 树苗展示名剥离后缀，再回退原版常数映射
        string treeId = tree.treeId?.Value;
        if (!string.IsNullOrWhiteSpace(treeId))
        {
            string saplingDisp = SafeGetDisplayName(treeId);
            if (!string.IsNullOrWhiteSpace(saplingDisp))
            {
                string clean = ExtractFruitNameFromTreeName(saplingDisp);
                if (!string.IsNullOrWhiteSpace(clean)) return clean;
            }

            if (TryGetVanillaFruitName(treeId, out string fallbackFruit))
            {
                return fallbackFruit;
            }
        }

        // Step 4 [返回空]: 各层均无法解析时交由上层按空品种省略括号
        return null;
    }

    /// <summary>按当前语言剥离树苗/果树名称中的品种后缀，返回主体果实名。</summary>
    private static string ExtractFruitNameFromTreeName(string treeOrSaplingName)
    {
        if (string.IsNullOrWhiteSpace(treeOrSaplingName)) return null;

        bool isZh = LocalizedContentManager.CurrentLanguageCode == LocalizedContentManager.LanguageCode.zh;
        string name = treeOrSaplingName.Trim();

        if (isZh)
        {
            if (name.EndsWith("树苗"))
            {
                name = name[..^"树苗".Length];
            }
            else
            {
                // "苹果树" 同时以 "果树" 与 "树" 结尾；中文果实名主体至少两字，
                // 剥去 "果树" 后不足两字时按 "树" 后缀处理（"苹果树" -> "苹果"）
                if (name.EndsWith("果树") && name.Length - "果树".Length >= 2)
                {
                    name = name[..^"果树".Length];
                }
                else if (name.EndsWith("树"))
                {
                    name = name[..^"树".Length];
                }
            }
            return name.Trim();
        }

        if (name.EndsWith("Sapling", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^"Sapling".Length];
        }
        if (name.EndsWith("Tree", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^"Tree".Length];
        }
        return name.Trim();
    }

    /// <summary>原版果树常数权威映射：覆盖 1.6 全部树苗与果实 ID 的双语果实名。</summary>
    private static bool TryGetVanillaFruitName(string id, out string name)
    {
        name = null;
        if (string.IsNullOrWhiteSpace(id)) return false;

        bool isZh = LocalizedContentManager.CurrentLanguageCode == LocalizedContentManager.LanguageCode.zh;

        switch (id)
        {
            // 树苗 ID（Data/FruitTrees 键位）
            case "628": name = isZh ? "樱桃" : "Cherry"; return true;
            case "629": name = isZh ? "杏子" : "Apricot"; return true;
            case "630": name = isZh ? "橙子" : "Orange"; return true;
            case "631": name = isZh ? "桃子" : "Peach"; return true;
            case "632": name = isZh ? "石榴" : "Pomegranate"; return true;
            case "633": name = isZh ? "苹果" : "Apple"; return true;
            case "69": name = isZh ? "香蕉" : "Banana"; return true;
            case "835": name = isZh ? "芒果" : "Mango"; return true;
            // 果实 ID（物品 ID，与树苗一一对应）
            case "613": name = isZh ? "樱桃" : "Cherry"; return true;
            case "634": name = isZh ? "杏子" : "Apricot"; return true;
            case "635": name = isZh ? "橙子" : "Orange"; return true;
            case "636": name = isZh ? "桃子" : "Peach"; return true;
            case "637": name = isZh ? "石榴" : "Pomegranate"; return true;
            case "638": name = isZh ? "苹果" : "Apple"; return true;
            case "91": name = isZh ? "香蕉" : "Banana"; return true;
            case "834": name = isZh ? "芒果" : "Mango"; return true;
            default: return false;
        }
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

            // 1.6 展示名可能仍是 [LocalizedText ...] 令牌串，返回前解析为自然语言
            if (displayName.Contains('['))
            {
                displayName = TokenParser.ParseText(displayName);
            }

            return displayName;
        }
        catch
        {
            return null;
        }
    }
}