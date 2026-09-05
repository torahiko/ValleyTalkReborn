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

        // ── 2. 扫描温室 ──
        // 直接以地图是否存在作为"已解锁"判据，比 hasOrWillReceiveMail 更可靠：
        // 后者在 CC 剧情刚完成、邮件尚未真正投递的边界日会提前返回 true，
        // 但此时 Greenhouse 地图可能还没有生成，会导致"已解锁但空置"的误导性文本。
        var ghLocation = Game1.getLocationFromName("Greenhouse");
        bool isGreenhouseUnlocked = ghLocation != null;

        int ghReadyCrops = 0;
        int ghGrowingCrops = 0;
        List<string> ghTopReady = new();
        List<string> ghTopGrowing = new();

        if (isGreenhouseUnlocked)
        {
            (ghReadyCrops, ghGrowingCrops, _, ghTopReady, ghTopGrowing) = ScanCropsInLocation(ghLocation);
        }

        // ── 3. 扫描动物（仅统计数量与种类：天级粒度，纳入缓存） ──
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

        // ── 4. 扫描鱼塘 ──
        var fishPondInfos = new List<string>();
        foreach (var building in farm.buildings)
        {
            if (building is FishPond pond && pond.currentOccupants.Value > 0 && !string.IsNullOrWhiteSpace(pond.fishType.Value))
            {
                string fishName = SafeGetDisplayName(pond.fishType.Value) ?? (isZh ? "鱼" : "Fish");
                fishPondInfos.Add(isZh ? $"{fishName}({pond.currentOccupants.Value}条)" : $"{fishName} ({pond.currentOccupants.Value})");
            }
        }

        // ── 5. 格式化输出 ──
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
                    sb.AppendLine("- 室内温室: 温室目前空置着，里面什么也没种。");
                }
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
                    sb.AppendLine("- Greenhouse: The greenhouse is currently completely empty with nothing planted.");
                }
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