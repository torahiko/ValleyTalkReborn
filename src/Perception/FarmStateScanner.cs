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
        // 检查实际收到的邮件状态：ccPantry（献祭路线）或 jojaGreenhouse（Joja 路线）
        // 这样既解决了地图实例默认存在导致的误判，又避免了 hasOrWillReceiveMail 在献祭当天的提前判定问题
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

        // ── 4. 扫描动物（仅统计数量与种类：天级粒度，纳入缓存） ──
        // 注意：饥饿度(fullness)、抚摸状态(wasPet) 属于分钟级实时字段，故意不放进本摘要，
        // 避免污染按天缓存的 GameConstantContext。如需体现实时状态，
        // 请改在 Prompts.CorePrompt 中单独注入（该层本来就每轮重建，不受缓存分段影响）。
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
                fishPondInfos.Add(isZh ? $"{fishName}({pond.currentOccupants.Value}条)" : $"{fishName} ({pond.currentOccupants.Value})");
            }
        }

        // ── 6. 格式化输出 ──
        if (isZh)
        {
            sb.AppendLine("### [农场经营状态]");
            sb.AppendLine("<farm_state>");

            if (readyCrops > 0)
            {
                sb.AppendLine($"- 农田作物: 有 {readyCrops} 块作物已成熟待收割（包含: {string.Join("、", topReadyCropNames)} 等）。");
            }
            else if (growingCrops > 0)
            {
                sb.AppendLine($"- 农田作物: 有 {growingCrops} 块作物正在生长中（包含: {string.Join("、", topGrowingCropNames)} 等）。");
            }

            if (deadCrops > 0)
            {
                sb.AppendLine($"- 农田异常: 发现了 {deadCrops} 株枯萎死去的作物。");
            }

            // 果树展示
            if (fruitTreeProducing > 0)
            {
                if (fruitTreeTypes > 3)
                {
                    sb.AppendLine($"- 果园状态: 果树品种丰富，共 {fruitTreeProducing} 棵树挂果待摘，涵盖 {fruitTreeTypes} 个品种（主要包括: {string.Join("、", topFruits)} 等）。");
                }
                else
                {
                    sb.AppendLine($"- 果园状态: 有 {fruitTreeProducing} 棵果树果实累累（包含: {string.Join("、", topFruits)}）。");
                }
            }
            else if (fruitTreeGrowing > 0)
            {
                sb.AppendLine($"- 果园状态: 有 {fruitTreeGrowing} 棵幼年果树正在生长中。");
            }

            // 温室展示（修复后/破损废弃）
            if (isGreenhouseUnlocked)
            {
                if (ghReadyCrops > 0)
                {
                    sb.AppendLine($"- 室内温室: 有 {ghReadyCrops} 块作物已成熟待收割（包含: {string.Join("、", ghTopReady)} 等）。");
                }
                else if (ghGrowingCrops > 0)
                {
                    sb.AppendLine($"- 室内温室: 有 {ghGrowingCrops} 块作物正在生长中（包含: {string.Join("、", ghTopGrowing)} 等）。");
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
                sb.AppendLine($"- 农场牲畜: 共养了 {totalAnimals} 只动物{animalTypesStr}。");
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
                sb.AppendLine($"- Outdoor Crops: {readyCrops} crops are ripe and ready to harvest (including: {string.Join(", ", topReadyCropNames)}).");
            }
            else if (growingCrops > 0)
            {
                sb.AppendLine($"- Outdoor Crops: {growingCrops} crops growing (including: {string.Join(", ", topGrowingCropNames)}).");
            }

            if (deadCrops > 0)
            {
                sb.AppendLine($"- Field Warning: {deadCrops} withered crops spotted.");
            }

            // Fruit Trees
            if (fruitTreeProducing > 0)
            {
                if (fruitTreeTypes > 3)
                {
                    sb.AppendLine($"- Orchard: Diverse orchard with {fruitTreeProducing} trees bearing ripe fruit across {fruitTreeTypes} varieties (mainly: {string.Join(", ", topFruits)}, etc.).");
                }
                else
                {
                    sb.AppendLine($"- Orchard: {fruitTreeProducing} fruit trees are bearing ripe fruit (including: {string.Join(", ", topFruits)}).");
                }
            }
            else if (fruitTreeGrowing > 0)
            {
                sb.AppendLine($"- Orchard: {fruitTreeGrowing} young fruit trees are growing.");
            }

            // Greenhouse
            if (isGreenhouseUnlocked)
            {
                if (ghReadyCrops > 0)
                {
                    sb.AppendLine($"- Greenhouse: {ghReadyCrops} crops ripe and ready to harvest (including: {string.Join(", ", ghTopReady)}).");
                }
                else if (ghGrowingCrops > 0)
                {
                    sb.AppendLine($"- Greenhouse: {ghGrowingCrops} crops growing (including: {string.Join(", ", ghTopGrowing)}).");
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
                sb.AppendLine($"- Livestock: {totalAnimals} animals{animalTypesStr}.");
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
                // 若无法解析作物名（例如 mod 数据异常），兜底为"未知作物"而不是丢弃计数，
                // 保证 readyCount/growingCount 与展示的 top 名单数量语义一致。
                string cropName = SafeGetDisplayName(dirt.crop.indexOfHarvest.Value) ?? "未知作物";

                if (isReady)
                {
                    readyCount++;
                    readyMap[cropName] = readyMap.GetValueOrDefault(cropName, 0) + 1;
                }
                else
                {
                    growingCount++;
                    growingMap[cropName] = growingMap.GetValueOrDefault(cropName, 0) + 1;
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
                // 1.6+ 写法：tree.fruit 是果实列表，Count > 0 表示挂果
                if (tree.fruit != null && tree.fruit.Count > 0)
                {
                    producingTreeCount++;

                    // 直接从树上挂着的果实 Item 实体安全获取显示名
                    string fruitName = tree.fruit[0]?.DisplayName;
                    if (string.IsNullOrWhiteSpace(fruitName))
                    {
                        fruitName = "水果";
                    }

                    fruitCounts[fruitName] = fruitCounts.GetValueOrDefault(fruitName, 0) + 1;
                }
                // 处于成长阶段（未达到最终成熟树形态）
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

    /// <summary>
    /// 安全获取物品显示名，避免因无效/异常 ItemId（例如部分 mod 数据错误）导致整个扫描抛异常。
    /// </summary>
    private static string SafeGetDisplayName(string itemId)
    {
        if (string.IsNullOrWhiteSpace(itemId)) return null;

        try
        {
            return ItemRegistry.Create(itemId)?.DisplayName;
        }
        catch
        {
            return null;
        }
    }
}