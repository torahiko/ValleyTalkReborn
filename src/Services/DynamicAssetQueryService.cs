#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Locations;

namespace ValleytalkReborn.Services;

/// <summary>
/// 动态资产查询服务：地图采集（约会地点候选）与 NPC 候选委托。
/// 同程序集机制，internal static。
/// </summary>
internal static class DynamicAssetQueryService
{
    internal sealed class MapInfo
    {
        public string Id = "";
        public string DisplayName = "";
        public bool IsIndoors;
    }

    /// <summary>
    /// 采集当前世界可用地图列表（去重、排序）。
    /// 过滤流：null / 空白 Name / 临时图 / 矿坑与火山地下城 → 跳过；
    /// IsIndoors = !loc.IsOutdoors；DisplayName 优先取 loc.DisplayName。
    /// </summary>
    public static List<MapInfo> GetAvailableMaps()
    {
        if (!Context.IsWorldReady)
        {
            ModEntry.SMonitor?.Log("[DynamicAssetQuery] 世界未就绪，返回空地图表", LogLevel.Warn);
            return new List<MapInfo>();
        }

        var result = new Dictionary<string, MapInfo>(StringComparer.OrdinalIgnoreCase);
        // 即时快照后再过滤，防遍历中集合变更。
        foreach (GameLocation loc in Game1.locations.ToList())
        {
            if (loc == null)
                continue;
            if (string.IsNullOrEmpty(loc.Name))
                continue;
            if (loc.IsTemporary)
                continue;
            if (loc is MineShaft || loc is VolcanoDungeon)
                continue;

            string id = loc.Name;
            string displayName = !string.IsNullOrEmpty(loc.DisplayName) ? loc.DisplayName : loc.Name;
            result[id] = new MapInfo
            {
                Id = id,
                DisplayName = displayName,
                IsIndoors = !loc.IsOutdoors
            };
        }

        return result.Values
            .OrderBy(m => m.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>NPC 候选委托至 NpcCandidateQueryService（本服务不写任何过滤逻辑）。</summary>
    public static List<(string Id, string DisplayName)> GetAvailableNpcs()
        => NpcCandidateQueryService.GetCleanedCandidates();
}
