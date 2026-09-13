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
    // ── 按天缓存：确保同一天内注入 GameConstantContext 的农场文本字符级不变 ──
    // 原因：这段文本最终会被塞进 Prompts.GameConstantContext，对应 Llm.RunInference 的
    // gameCacheString 参数。在 LlmClaude.cs 中该参数会被标记为 Anthropic 的
    // cache_control: { type: "ephemeral" } 缓存分段（严格按前缀字符匹配）。
    // 若文本随游戏 tick 抖动（尤其是动物饥饿值/抚摸状态这类实时字段），
    // 哪怕只差一个字符也会导致该分段及其后所有内容全部 cache miss，
    // 多付约 25% 的写入成本，且损失命中时 90% 的价格折扣。
    private static string _cachedSummaryZh;
    private static string _cachedSummaryEn;
    private static int _cachedYear = -1;
    private static string _cachedSeason;
    private static int _cachedDay = -1;

    /// <summary>
    /// 扫描全农场生态，构建指标化经营状态概况。
    /// 同一游戏日内多次调用返回完全相同的字符串（按天缓存），跨天自动失效重扫。
    /// isZh 为 false 时（包含所有小语种）自动回落为英文输出。
    /// </summary>
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

    /// <summary>
    /// 供外部（如天亮/收割等事件钩子）在需要即时刷新时强制失效缓存。
    /// 目前无调用方，按天粒度已足够；如后续要在关键事件后立即反映变化可调用此方法。
    /// </summary>
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
        var (readyCrops, growingCrops, deadCrops, topReadyCropNames, topGrowingCropNames) = ScanCropsInLocation(farm);

        // ── 2. 扫描果树（室外） ──
        var (fruitTreeProducing, fruitTreeTypes, topFruits, fruitTreeGrowing) = ScanFruitTreesInLocation(farm);

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
            (ghReadyCrops, ghGrowingCrops, _, ghTopReady, ghTopGrowing) = ScanCropsInLocation(ghLocation);
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

        // ── 6. 格式化输出（全部采用模糊感知描述，杜绝精准报数） ──
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
                sb.AppendLine($"- 农田异常: 发现了{GetDeadCropFuzzy(deadCrops, isZh: true)}枯萎死去的作物。");
            }

            // 果树展示
            if (fruitTreeProducing > 0)
            {
                string producingTrees = GetFruitTreeQuantityFuzzy(fruitTreeProducing, isZh: true);
                if (fruitTreeTypes > 3)
                {
                    string sample = topFruits.Count > 0 ? $"（主要包括: {string.Join("、", topFruits)} 等）" : "";
                    sb.AppendLine($"- 果园状态: 果树品种丰富，有{producingTrees}果树挂果待摘，涵盖多种不同品种{sample}。");
                }
                else
                {
                    string sample = topFruits.Count > 0 ? $"（包含: {string.Join("、", topFruits)}）" : "";
                    sb.AppendLine($"- 果园状态: 有{producingTrees}果树果实累累{sample}。");
                }
            }
            else if (fruitTreeGrowing > 0)
            {
                string growingTrees = GetFruitTreeQuantityFuzzy(fruitTreeGrowing, isZh: true);
                sb.AppendLine($"- 果园状态: 有{growingTrees}幼年果树正在生长中。");
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
            // 英文模板（其他非中文语言自动回落至此）
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
                sb.AppendLine($"- Field Warning: {GetDeadCropFuzzy(deadCrops, isZh: false)} withered crops spotted.");
            }

            // Fruit Trees
            if (fruitTreeProducing > 0)
            {
                string producingTrees = GetFruitTreeQuantityFuzzy(fruitTreeProducing, isZh: false);
                if (fruitTreeTypes > 3)
                {
                    string sample = topFruits.Count > 0 ? $" (mainly: {string.Join(", ", topFruits)}, etc.)" : "";
                    sb.AppendLine($"- Orchard: Diverse orchard with {producingTrees} trees bearing ripe fruit across multiple varieties{sample}.");
                }
                else
                {
                    string sample = topFruits.Count > 0 ? $" (including: {string.Join(", ", topFruits)})" : "";
                    sb.AppendLine($"- Orchard: {producingTrees} fruit trees are bearing ripe fruit{sample}.");
                }
            }
            else if (fruitTreeGrowing > 0)
            {
                string growingTrees = GetFruitTreeQuantityFuzzy(fruitTreeGrowing, isZh: false);
                sb.AppendLine($"- Orchard: {growingTrees} young fruit trees are growing.");
            }

            // Greenhouse
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

    // ── 模糊化感知映射辅助方法（全数量去面板化） ──

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

    private static (int readyCount, int growingCount, int deadCount, List<string> topReady, List<string> topGrowing) ScanCropsInLocation(GameLocation location)
    {
        if (location == null) return (0, 0, 0, new List<string>(), new List<string>());

        int readyCount = 0;
        int growingCount = 0;
        int deadCount = 0;

        var readyMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var growingMap = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var pair in location.terrainFeatures.Pairs)
        {
            if (pair.Value is HoeDirt dirt && dirt.crop != null)
            {
                if (dirt.crop.dead.Value)
                {
                    deadCount++;
                    continue;
                }

                bool isReady = dirt.crop.currentPhase.Value >= dirt.crop.phaseDays.Count - 1;
                string cropName = SafeGetDisplayName(dirt.crop.indexOfHarvest.Value);

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

        return (readyCount, growingCount, deadCount, topReady, topGrowing);
    }

    private static (int producingTreeCount, int fruitTypeCount, List<string> topFruits, int growingCount) ScanFruitTreesInLocation(GameLocation location)
    {
        if (location == null) return (0, 0, new List<string>(), 0);

        int producingTreeCount = 0;
        int growingCount = 0;
        var fruitCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var pair in location.terrainFeatures.Pairs)
        {
            if (pair.Value is FruitTree tree)
            {
                if (tree.fruit != null && tree.fruit.Count > 0)
                {
                    producingTreeCount++;

                    Item fruit = tree.fruit[0];
                    if (IsValidItem(fruit))
                    {
                        string fruitName = fruit.DisplayName;
                        if (!string.IsNullOrWhiteSpace(fruitName))
                        {
                            fruitCounts[fruitName] = fruitCounts.GetValueOrDefault(fruitName, 0) + 1;
                        }
                    }
                }
                else if (tree.growthStage.Value < FruitTree.treeStage)
                {
                    growingCount++;
                }
            }
        }

        int fruitTypeCount = fruitCounts.Count;

        var topFruits = fruitCounts
            .OrderByDescending(kv => kv.Value)
            .Take(3)
            .Select(kv => kv.Key)
            .ToList();

        return (producingTreeCount, fruitTypeCount, topFruits, growingCount);
    }

    private static bool IsValidItem(Item item)
    {
        if (item == null) return false;

        if (item.GetType().Name.Contains("Error", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrEmpty(item.QualifiedItemId) && item.QualifiedItemId.Contains("Error", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var parsedData = ItemRegistry.GetData(item.QualifiedItemId);
        if (parsedData == null)
        {
            return false;
        }

        return true;
    }

    private static string SafeGetDisplayName(string itemId)
    {
        if (string.IsNullOrWhiteSpace(itemId)) return null;

        try
        {
            Item item = ItemRegistry.Create(itemId);
            if (!IsValidItem(item))
            {
                return null;
            }

            return item.DisplayName;
        }
        catch
        {
            return null;
        }
    }
}