using Microsoft.Xna.Framework;
using System;
using StardewValley;
using StardewValley.Pathfinding;

namespace ValleytalkReborn
{
    /// <summary>
    /// 寻路与地图通行性相关的静态工具方法。
    /// MovementManager 和 NpcInteractionService 共用，避免重复实现。
    /// </summary>
    internal static class MovementPathfinding
    {
        // ═══════════════════════════════════════════════
        //  通行性判断（增强：地形检查 + 边界检查）
        // ═══════════════════════════════════════════════

        internal static bool IsTileWalkable(
            GameLocation loc,
            Vector2 tile,
            StardewValley.Character character = null)
        {
            if (loc == null)
                return false;

            int tx = (int)tile.X;
            int ty = (int)tile.Y;

            // Boundary check: reject out-of-bounds tiles immediately.
            if (tx < 0 || ty < 0 || tx >= loc.map.Layers[0].LayerWidth || ty >= loc.map.Layers[0].LayerHeight)
                return false;

            // Terrain passability check (water, cliffs, etc.).
            if (!loc.isTilePassable(new xTile.Dimensions.Location(tx, ty), Game1.viewport))
                return false;

            // Physics collision check (buildings, furniture, NPCs, etc.).
            var box = new Rectangle(
                tx * 64 + 2,
                ty * 64 + 2,
                60,
                60);
            if (loc.isCollidingPosition(box, Game1.viewport, false, 0, false, character))
                return false;

            return true;
        }

        // ═══════════════════════════════════════════════
        //  Warp 落点（修复：优先使用地图 warp 入口，兜底返回 null）
        // ═══════════════════════════════════════════════

        /// <summary>
        /// 在目标地图中找一个安全的 warp 落点。
        /// 优先使用地图自身的 warp 入口坐标，其次在 nearTile 附近搜索。
        /// 如果全部失败，返回 null（调用方应放弃 warp 而非硬塞）。
        /// </summary>
        internal static Vector2? FindSafeWarpTile(
            GameLocation loc,
            Vector2 nearTile,
            StardewValley.Character character = null)
        {
            if (loc == null)
                return null;

            // First priority: use the map's own warp entry points (closest to nearTile).
            if (loc.warps != null && loc.warps.Count > 0)
            {
                Warp bestWarp = null;
                float bestDist = float.MaxValue;

                foreach (var warp in loc.warps)
                {
                    if (warp == null) continue;
                    var warpTile = new Vector2(warp.X, warp.Y);
                    float d = Vector2.Distance(warpTile, nearTile);
                    if (d < bestDist)
                    {
                        bestDist = d;
                        bestWarp = warp;
                    }
                }

                if (bestWarp != null)
                {
                    var result = FindWalkableTileNear(loc, new Vector2(bestWarp.X, bestWarp.Y), character, maxRadius: 3);
                    if (result.HasValue)
                        return result.Value;
                }
            }

            // Second priority: search near the player's tile.
            var fallback = FindWalkableTileNear(loc, nearTile, character, maxRadius: 3);
            if (fallback.HasValue)
                return fallback.Value;

            // All attempts failed; return null so the caller can abort gracefully.
            return null;
        }

        /// <summary>
        /// 在 center 附近按螺旋顺序搜索可通行格。找不到返回 null。
        /// </summary>
        internal static Vector2? FindWalkableTileNear(
            GameLocation loc,
            Vector2 center,
            StardewValley.Character character = null,
            int maxRadius = 3)
        {
            if (IsTileWalkable(loc, center, character))
                return center;

            for (int r = 1; r <= maxRadius; r++)
            {
                for (int dy = -r; dy <= r; dy++)
                for (int dx = -r; dx <= r; dx++)
                {
                    // Only check the current ring, not inner rings.
                    if (Math.Abs(dx) != r && Math.Abs(dy) != r)
                        continue;

                    var candidate = new Vector2(center.X + dx, center.Y + dy);
                    if (IsTileWalkable(loc, candidate, character))
                        return candidate;
                }
            }

            return null;
        }

        /// <summary>
        /// 在 warpTile 附近找可通行格子（供 BeginSmoothDeparture 使用）。
        /// </summary>
        internal static Vector2 FindWalkableTileNearWarp(GameLocation loc, Vector2 warpTile, NPC npc)
        {
            var result = FindWalkableTileNear(loc, warpTile, npc, maxRadius: 3);
            return result ?? warpTile; // Extreme fallback.
        }

        // ═══════════════════════════════════════════════
        //  PathFindController 工具
        // ═══════════════════════════════════════════════

        internal static bool IsPathDead(PathFindController ctrl)
            => ctrl == null || ctrl.pathToEndPoint == null;

        internal static bool IsPathDone(PathFindController ctrl)
            => ctrl == null || ctrl.pathToEndPoint == null || ctrl.pathToEndPoint.Count == 0;

        /// <summary>
        /// 尝试为 npc 创建到 target 的路径，失败时自动尝试邻近偏移格。
        /// </summary>
        internal static bool TryCreatePath(
            NPC npc,
            GameLocation loc,
            Vector2 target,
            out PathFindController controller,
            out Vector2 finalTarget)
        {
            controller  = null;
            finalTarget = target;

            if (npc == null || loc == null)
                return false;

            // If the target itself is not walkable, try nearby offsets first.
            if (!IsTileWalkable(loc, target, npc))
            {
                int[] offsets = { 1, -1, 2, -2 };
                foreach (int dy in offsets)
                foreach (int dx in offsets)
                {
                    if (dx == 0 && dy == 0) continue;
                    var alt = new Vector2(target.X + dx, target.Y + dy);
                    if (!IsTileWalkable(loc, alt, npc))
                        continue;

                    controller = new PathFindController(
                        npc, loc,
                        new Microsoft.Xna.Framework.Point((int)alt.X, (int)alt.Y), -1);

                    if (!IsPathDead(controller))
                    {
                        finalTarget = alt;
                        return true;
                    }
                }

                return false;
            }

            controller = new PathFindController(
                npc, loc,
                new Microsoft.Xna.Framework.Point((int)target.X, (int)target.Y), -1);

            if (!IsPathDead(controller))
                return true;

            // Target is walkable but path is unreachable; try nearby offsets.
            {
                int[] offsets = { 1, -1, 2, -2 };
                foreach (int dy in offsets)
                foreach (int dx in offsets)
                {
                    if (dx == 0 && dy == 0) continue;
                    var alt = new Vector2(target.X + dx, target.Y + dy);
                    if (!IsTileWalkable(loc, alt, npc))
                        continue;

                    controller = new PathFindController(
                        npc, loc,
                        new Microsoft.Xna.Framework.Point((int)alt.X, (int)alt.Y), -1);

                    if (!IsPathDead(controller))
                    {
                        finalTarget = alt;
                        return true;
                    }
                }
            }

            controller = null;
            return false;
        }

        /// <summary>
        /// 若 targetTile 与玩家重叠，找附近不与玩家重叠的可通行格。
        /// </summary>
        internal static Vector2 ResolveTargetAvoidingPlayer(
            Vector2 targetTile,
            Vector2 playerTile,
            Func<Vector2, bool> walkablePredicate)
        {
            if (walkablePredicate == null)
                return targetTile;

            if (Vector2.Distance(targetTile, playerTile) > 1.5f)
                return targetTile;

            var candidates = new[]
            {
                new Vector2(targetTile.X,     targetTile.Y + 1),
                new Vector2(targetTile.X,     targetTile.Y - 1),
                new Vector2(targetTile.X + 1, targetTile.Y),
                new Vector2(targetTile.X - 1, targetTile.Y),
                new Vector2(targetTile.X + 1, targetTile.Y + 1),
                new Vector2(targetTile.X - 1, targetTile.Y - 1),
                new Vector2(targetTile.X + 1, targetTile.Y - 1),
                new Vector2(targetTile.X - 1, targetTile.Y + 1),
            };

            foreach (var c in candidates)
            {
                if (Vector2.Distance(c, playerTile) < 1f) continue;
                if (!walkablePredicate(c))                continue;

                return c;
            }

            return targetTile;
        }

        /// <summary>
        /// 根据位移分量计算朝向（0=上 1=右 2=下 3=左）。
        /// </summary>
        internal static int FacingFromDelta(int dx, int dy)
        {
            if (dy < 0) return 0;
            if (dx > 0) return 1;
            if (dy > 0) return 2;
            return 3;
        }

        /// <summary>
        /// ★ 安全时间加法，钳制到 600~2600，防止产生非法游戏时间。
        /// </summary>
        internal static int SafeAddGameTime(int currentTime, int addMinutes)
        {
            int hours   = currentTime / 100;
            int minutes = currentTime % 100 + addMinutes;

            hours   += minutes / 60;
            minutes %= 60;

            return Math.Clamp(hours * 100 + minutes, 600, 2600);
        }
    }
}