using System.Collections.Generic;
using System.Text;
using StardewValley;

namespace ValleytalkReborn
{
    /// <summary>
    /// 统一的场景上下文组装器。
    /// 供 DynamicBarkManager（单人 Bark）和 A2A 系统共同调用。
    /// </summary>
    internal static class SceneContextBuilder
    {
        /// <summary>
        /// 组装完整的场景感知块（地点、时间、天气、节日、周围实体）。
        /// 以 centerNpc 为中心扫描，若为 null 则以玩家为中心。
        /// </summary>
        public static string BuildSceneBlock(NPC centerNpc, int radiusTiles = 5, int maxItems = 5,
            IEnumerable<string> excludeNames = null)
        {
            var sb = new StringBuilder();
            sb.AppendLine("### [SCENE AWARENESS]");

            // 地点 / 时间 / 天气
            string locationName = EnvironmentScanner.GetLocationFriendlyName(
                centerNpc?.currentLocation?.Name ?? Game1.currentLocation?.Name ?? "");

            int timeOfDay  = Game1.timeOfDay;
            string timeStr = $"{(timeOfDay / 100) % 24}:{timeOfDay % 100:00}";

            var weatherParts = new List<string>();
            if (Game1.IsRainingHere())   weatherParts.Add("Rainy");
            if (Game1.IsSnowingHere())   weatherParts.Add("Snowy");
            if (Game1.IsLightningHere()) weatherParts.Add("Thunder");
            if (weatherParts.Count == 0) weatherParts.Add("Sunny");

            sb.AppendLine($"- Location: {locationName} ({timeStr}, {Game1.currentSeason}, {string.Join(", ", weatherParts)})");

            // 节日
            string festival = EnvironmentScanner.GetTodayFestivalName();
            if (!string.IsNullOrEmpty(festival))
                sb.AppendLine($"- Event: {festival}");

            // 周围实体（物品 + NPC）
            var nearby = BuildNearbyList(centerNpc, radiusTiles, maxItems, excludeNames);
            if (nearby.Count > 0)
                sb.AppendLine($"- Nearby: {string.Join(", ", nearby)}");

            return sb.ToString().TrimEnd();
        }

        /// <summary>
        /// 组装周围实体列表：物品扫描 + 周围 NPC + 玩家（如在范围内）。
        /// excludeNames 用于排除对话双方自身名字。
        /// </summary>
        public static List<string> BuildNearbyList(NPC centerNpc, int radiusTiles = 5, int maxItems = 5,
            IEnumerable<string> excludeNames = null)
        {
            var exclude = excludeNames != null
                ? new HashSet<string>(excludeNames, System.StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);

            // 物品扫描
            var raw = EnvironmentScanner.ScanNearbyObjects(centerNpc, radiusTiles, maxItems);
            var nearby = new List<string>();
            foreach (var item in raw)
            {
                if (!exclude.Contains(item))
                    nearby.Add(item);
            }

            // 玩家是否在场（优先级最高，插到最前面）
            if (Game1.player != null && centerNpc != null)
            {
                long dx = (long)centerNpc.Position.X - (long)Game1.player.Position.X;
                long dy = (long)centerNpc.Position.Y - (long)Game1.player.Position.Y;
                long distSq = (dx * dx + dy * dy) / (64 * 64);
                if (distSq <= radiusTiles * radiusTiles)
                {
                    string playerName = Game1.player.displayName ?? Game1.player.Name ?? "the farmer";
                    if (!exclude.Contains(playerName))
                        nearby.Insert(0, playerName);
                }
            }

            return nearby;
        }
    }
}