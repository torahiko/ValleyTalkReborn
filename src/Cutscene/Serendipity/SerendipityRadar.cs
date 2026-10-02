using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewValley;

namespace ValleytalkReborn.Cutscene.Serendipity
{
    /// <summary>
    /// 偶遇事件聚类雷达：负责扫描当前场景中距离相近的 NPC 候选对，并决选出最佳偶遇组合
    /// </summary>
    public static class SerendipityRadar
    {
        public const float MaxClusterDistanceTiles = 6.0f; // NPC 彼此聚集的最大距离（6格以内视为结伴/偶遇）
        public const float MaxPlayerDistanceTiles = 18.0f; // 与玩家的最大距离（18格以内，视野范围内）

        /// <summary>
        /// 扫描当前地图，尝试搜寻符合偶遇条件的演员组合与情境
        /// </summary>
        public static bool TryFindCandidate(
            GameLocation location,
            Vector2 playerTile,
            int currentTotalDays,
            out List<NPC> matchedActors,
            out SituationDefinition matchedSituation)
        {
            matchedActors = null;
            matchedSituation = null;

            if (location == null || location.characters == null || location.characters.Count == 0)
                return false;

            // 1. 过滤合法村民角色
            var candidates = new List<NPC>();
            foreach (var character in location.characters)
            {
                if (character is NPC npc && npc.IsVillager)
                {
                    // 排除动物、怪兽、以及处于不可见/休眠状态的 NPC
                    if (npc.IsMonster || npc.IsInvisible) continue;

                    // 距离玩家不能太远（必须在探索体验感知范围内）
                    if (Vector2.Distance(playerTile, npc.Tile) > MaxPlayerDistanceTiles) continue;

                    // 冷却检查：该 NPC 今天尚未参演过偶遇微电影
                    if (!SerendipityCooldownStore.Instance.IsNpcAvailable(npc.Name, currentTotalDays)) continue;

                    candidates.Add(npc);
                }
            }

            if (candidates.Count < 2)
            {
                // 偶遇小剧场核心体验为双人或多人互动
                return false;
            }

            // 2. 空间聚类：寻找彼此距离 ≤ MaxClusterDistanceTiles 的 NPC 组合
            List<NPC> bestCluster = null;
            float bestDistanceToPlayer = float.MaxValue;

            for (int i = 0; i < candidates.Count; i++)
            {
                var npcA = candidates[i];
                var cluster = new List<NPC> { npcA };

                for (int j = 0; j < candidates.Count; j++)
                {
                    if (i == j) continue;
                    var npcB = candidates[j];

                    if (Vector2.Distance(npcA.Tile, npcB.Tile) <= MaxClusterDistanceTiles)
                    {
                        cluster.Add(npcB);
                        if (cluster.Count >= 3) break; // 最多取 3 人小团体，保证分镜紧凑精致
                    }
                }

                if (cluster.Count >= 2)
                {
                    float distToPlayer = Vector2.Distance(playerTile, npcA.Tile);
                    if (distToPlayer < bestDistanceToPlayer)
                    {
                        bestDistanceToPlayer = distToPlayer;
                        bestCluster = cluster;
                    }
                }
            }

            if (bestCluster == null || bestCluster.Count < 2)
            {
                return false;
            }

            matchedActors = bestCluster;

            // 3. 匹配最合适的情境模板
            string dayOfWeek = Game1.shortDayDisplayNameFromDayOfSeason(Game1.dayOfMonth);
            int timeOfDay = Game1.timeOfDay;
            bool isRaining = Game1.isRaining;
            var actorNames = matchedActors.Select(a => a.Name).ToList();

            matchedSituation = SituationMatcher.MatchSituation(
                location.NameOrUniqueName,
                timeOfDay,
                isRaining,
                dayOfWeek,
                actorNames);

            return true;
        }
    }
}
