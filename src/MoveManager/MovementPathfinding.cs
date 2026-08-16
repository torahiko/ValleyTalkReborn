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
        //  通行性判断
        // ═══════════════════════════════════════════════

        internal static bool IsTileWalkable(
            GameLocation loc,
            Vector2 tile,
            StardewValley.Character character = null)
        {
            if (loc == null)
                return false;

            var box = new Microsoft.Xna.Framework.Rectangle(
                (int)(tile.X * 64) + 2,
                (int)(tile.Y * 64) + 2,
                60,
                60);

            return !loc.isCollidingPosition(box, Game1.viewport, false, 0, false, character);
        }

        /// <summary>
        /// 在 nearTile 附近找一个可通行的格子，用于 Warp 落点。
        /// </summary>
        internal static Vector2 FindSafeWarpTile(
            GameLocation loc,
            Vector2 nearTile,
            StardewValley.Character character = null)
        {
            int[] offsets = { 1, -1, 2, -2, 3, -3 };

            foreach (int dy in offsets)
            foreach (int dx in offsets)
            {
                if (dx == 0 && dy == 0) continue;

                var candidate = new Vector2(nearTile.X + dx, nearTile.Y + dy);

                if (IsTileWalkable(loc, candidate, character))
                    return candidate;
            }

            return new Vector2(nearTile.X + 1, nearTile.Y + 1);
        }

        /// <summary>
        /// 在 warpTile 附近找可通行格子，warp tile 本身可能被门/墙占用。
        /// </summary>
        internal static Vector2 FindWalkableTileNearWarp(GameLocation loc, Vector2 warpTile, NPC npc)
        {
            if (IsTileWalkable(loc, warpTile, npc))
                return warpTile;

            int[] offsets = { 1, 2, 3 };
            var directions = new[]
            {
                new Vector2( 0,  1), new Vector2( 0, -1),
                new Vector2( 1,  0), new Vector2(-1,  0)
            };

            foreach (int offset in offsets)
            foreach (var dir in directions)
            {
                var candidate = new Vector2(
                    warpTile.X + dir.X * offset,
                    warpTile.Y + dir.Y * offset);

                if (IsTileWalkable(loc, candidate, npc))
                    return candidate;
            }

            return warpTile;
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

            controller = new PathFindController(
                npc, loc, new Microsoft.Xna.Framework.Point((int)target.X, (int)target.Y), -1);

            if (!IsPathDead(controller))
                return true;

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
                    new Microsoft.Xna.Framework.Point((int)alt.X, (int)alt.Y),
                    -1);

                if (!IsPathDead(controller))
                {
                    finalTarget = alt;
                    return true;
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
