using Microsoft.Xna.Framework;
using System;
using System.Collections.Generic;
using System.Linq;
using StardewValley;
using StardewModdingAPI;
using StardewValley.Pathfinding;

namespace ValleytalkReborn
{
    /// <summary>
    /// 寻路与地图通行性相关的静态工具方法。
    /// MovementManager 共用，避免重复实现。
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

            // 玩家规则对齐：开阔水面不可走。1.6 的 isTilePassable 与 isCollidingPosition 均
            // 不把 Back 层 "Water" 属性当作移动碰撞（仅 rafting/弹道特例），NPC 寻路与物理
            // 因此可穿行河面。水面上有 Buildings 层 tile（桥面、码头）时不受此规则影响，
            // 交由下方 isTilePassable / isCollidingPosition 的 Buildings 层判定正常放行。
            if (IsOpenWater(loc.isWaterTile(tx, ty), loc.hasTileAt(tx, ty, "Buildings")))
                return false;

            // Terrain passability check (cliffs, Back/Buildings 层属性等).
            if (!loc.isTilePassable(new xTile.Dimensions.Location(tx, ty), Game1.viewport))
                return false;

            // Physics collision check (buildings, furniture, NPCs, etc.).
            // pathfinding:true exempts vs-player and vs-other-NPC collisions,
            // matching PathFindController.findPath semantics exactly.
            var box = new Rectangle(
                tx * 64 + 2,
                ty * 64 + 2,
                60,
                60);
            if (loc.isCollidingPosition(box, Game1.viewport, false, 0, false, character, pathfinding: true))
                return false;

            return true;
        }

        /// <summary>
        /// 开阔水面判定（纯函数，无头可测）：Back 层带 "Water" 属性且其上方没有
        /// Buildings 层 tile（桥面、码头板）时视为不可走；有覆盖 tile 时交由
        /// isTilePassable / isCollidingPosition 的 Buildings 层判定决定（如 Passable 桥板可走）。
        /// </summary>
        internal static bool IsOpenWater(bool isWaterBackTile, bool hasBuildingsLayerTile)
            => isWaterBackTile && !hasBuildingsLayerTile;

        // ═══════════════════════════════════════════════
        //  步进位移表（单一实现）
        // ═══════════════════════════════════════════════

        /// <summary>
        /// 由 MovementCoordinator 步进执行体等价迁移而来，是该 delta 表的唯一实现。
        /// 绝对方向直接给位移；Forward/Backward 由 facingDirection（0=上 1=右 2=下 3=左）取位，
        /// Backward 取反。
        /// </summary>
        internal static (int Dx, int Dy) ResolveStepDelta(
            MovementType type,
            int facingDirection)
        {
            if      (type == MovementType.Left)  return (-1,  0);
            else if (type == MovementType.Right) return ( 1,  0);
            else if (type == MovementType.Up)    return ( 0, -1);
            else if (type == MovementType.Down)  return ( 0,  1);

            int dx = 0;
            int dy = 0;

            switch (facingDirection)
            {
                case 0: dy = -1; break;
                case 1: dx =  1; break;
                case 2: dy =  1; break;
                case 3: dx = -1; break;
            }

            if (type == MovementType.Backward) { dx = -dx; dy = -dy; }

            return (dx, dy);
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
        /// 在玩家附近找一个安全的跟随落点（身后 → 四周，不使用地图 warp）。
        /// 用于跨地图跟随传送，避免 NPC 被传到大入口。
        /// </summary>
        internal static Vector2? FindSafeFollowTile(
            GameLocation loc,
            Vector2 playerTile,
            StardewValley.Character character = null)
        {
            if (loc == null)
                return null;

            var candidates = new List<Vector2>();

            // Priority 1: tiles behind the player (based on facing direction).
            if (Game1.player != null)
            {
                int dx = 0;
                int dy = 0;

                switch (Game1.player.FacingDirection)
                {
                    case 0: dy = 1; break;   // Facing up → place below
                    case 1: dx = -1; break;  // Facing right → place left
                    case 2: dy = -1; break;  // Facing down → place above
                    case 3: dx = 1; break;   // Facing left → place right
                }

                candidates.Add(new Vector2(playerTile.X + dx, playerTile.Y + dy));
                candidates.Add(new Vector2(playerTile.X + dx * 2, playerTile.Y + dy * 2));
            }

            // Priority 2: tiles around the player (radius 1 → 4).
            for (int radius = 1; radius <= 4; radius++)
            {
                for (int dy = -radius; dy <= radius; dy++)
                {
                    for (int dx = -radius; dx <= radius; dx++)
                    {
                        if (dx == 0 && dy == 0)
                            continue;

                        candidates.Add(new Vector2(playerTile.X + dx, playerTile.Y + dy));
                    }
                }
            }

            foreach (var candidate in candidates)
            {
                // Skip tiles too close to the player.
                if (Vector2.Distance(candidate, playerTile) < 1f)
                    continue;

                if (IsTileWalkable(loc, candidate, character))
                    return candidate;
            }

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
        /// 尝试将 NPC 从卡住的不可走 tile 恢复到附近可走的 tile。
        /// </summary>
        public static bool TryRecoverStartingTile(NPC npc, GameLocation location, int radius = 4)
        {
            if (npc == null || location == null) return false;
            Vector2 current = npc.Tile;

            if (IsTileWalkable(location, current, npc))
                return true; // 起点正常，不需要恢复

            for (int distance = 1; distance <= radius; distance++)
            {
                for (int dy = -distance; dy <= distance; dy++)
                {
                    for (int dx = -distance; dx <= distance; dx++)
                    {
                        if (Math.Abs(dx) != distance && Math.Abs(dy) != distance) continue;
                        Vector2 candidate = current + new Vector2(dx, dy);

                        if (IsTileWalkable(location, candidate, npc))
                        {
                            npc.setTilePosition(new Point((int)candidate.X, (int)candidate.Y));
                            npc.Halt();
                            npc.controller = null;
                            npc.addedSpeed = 0;
                            ModEntry.SMonitor?.Log(
                                $"[Pathfinding] Recovered {npc.Name} from blocked tile to ({candidate.X},{candidate.Y}).",
                                LogLevel.Debug);
                            return true;
                        }
                    }
                }
            }
            return false;
        }

        /// <summary>
        /// 寻找目标点附近最近的可走 tile（用于 POI 目标验证）。
        /// </summary>
        public static Vector2 FindNearestWalkableTile(GameLocation loc, Vector2 target, NPC npc, int radius = 3)
        {
            if (IsTileWalkable(loc, target, npc)) return target;
    
            for (int distance = 1; distance <= radius; distance++)
            {
                for (int dy = -distance; dy <= distance; dy++)
                {
                    for (int dx = -distance; dx <= distance; dx++)
                    {
                        if (Math.Abs(dx) != distance && Math.Abs(dy) != distance) continue;
                        Vector2 candidate = target + new Vector2(dx, dy);
                        if (IsTileWalkable(loc, candidate, npc)) return candidate;
                    }
                }
            }
            return target; // 实在找不到就返回原目标，交由后续寻路逻辑处理失败
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
        /// 路线内点校验（纯函数，无头可测）：vanilla A* 与其移动物理都不把水面当碰撞，
        /// 生成的路径可能直接过河。此校验按玩家通行规则（tileWalkable，含水面）检查内点。
        /// 起点与终点沿用 vanilla 语义豁免：终点无条件可达（findPath 邻接判定先例），
        /// 起点即 NPC 当前所在格。入参顺序 = Stack 枚举序 = Pop 序（栈顶=起点）。
        /// </summary>
        internal static bool RouteRespectsPlayerPassability(
            IReadOnlyList<Point> route,
            Func<Vector2, bool> tileWalkable)
        {
            if (route == null || route.Count == 0)
                return false;

            for (int i = 0; i < route.Count; i++)
            {
                bool isStartOrEnd = i == 0 || i == route.Count - 1;
                if (!isStartOrEnd && !tileWalkable(new Vector2(route[i].X, route[i].Y)))
                    return false;
            }

            return true;
        }

        /// <summary>
        /// 尝试为 npc 创建到 target 的路径，失败时自动尝试邻近偏移格。
        /// ① 目标格可走：先走 vanilla A*（保留其终格无条件可达语义与朝向目标的 tie-break），
        ///    但 vanilla 寻路不识别水面，路线可能直接过河 —— 以
        ///    RouteRespectsPlayerPassability 校验内点，违例即弃用；
        /// ② vanilla 无解（如迭代上限下的跨河大绕路）或路线违例 → 自算 4 向 BFS
        ///    （TryCreatePathAvoiding，全程 IsTileWalkable 把关，迭代上限为地图首层面积）；
        /// ③ 目标格本身不可走（或上述全败）→ 邻近偏移兜底（保留原语义）。
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

            if (IsTileWalkable(loc, target, npc))
            {
                var vanilla = new PathFindController(
                    npc, loc,
                    new Microsoft.Xna.Framework.Point((int)target.X, (int)target.Y), -1);

                if (!IsPathDead(vanilla)
                    && RouteRespectsPlayerPassability(
                        vanilla.pathToEndPoint?.ToList(),
                        tile => IsTileWalkable(loc, tile, npc)))
                {
                    controller = vanilla;
                    return true;
                }

                // vanilla 无解或路线内点含水/违例：改走自算 BFS（走玩家规则）。
                if (TryCreatePathAvoiding(npc, loc, target, null, out controller, out finalTarget))
                    return true;
            }

            // Target is not walkable (or the above failed): try nearby offsets.
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

            controller = null;
            return false;
        }

        /// <summary>
        /// 尝试为 npc 创建绕开指定阻挡格（如定身玩家）的路径：4 向 BFS + 阻挡格排除，
        /// 命中后经公开构造器注入自算路径栈（栈序与 vanilla reconstructPath 同构：底=终点，顶=起点）。
        /// 仅过场动作（MoveToTileAction）调用；MovementManager 系调用点与 vanilla 寻路不受影响。
        /// 终点格沿用 vanilla 语义无条件可达（findPath 邻接判定先例）；
        /// 无绕行通路或达 BFS 迭代上限（地图首层面积）时返回 false，调用方回退原 TryCreatePath 与熔断链。
        /// </summary>
        internal static bool TryCreatePathAvoiding(
            NPC npc,
            GameLocation loc,
            Vector2 target,
            IReadOnlyCollection<Vector2> blockedTiles,
            out PathFindController controller,
            out Vector2 finalTarget)
        {
            controller = null;
            finalTarget = target;

            if (npc == null || loc == null)
                return false;

            Point start = npc.TilePoint;
            Point goal = new Point((int)target.X, (int)target.Y);
            if (start == goal)
                return false; // 已在目标格：无可注入路径，交由调用方原逻辑收尾

            // 阻挡格取整比对集合
            var blocked = new HashSet<Point>();
            if (blockedTiles != null)
            {
                foreach (var t in blockedTiles)
                    blocked.Add(new Point((int)t.X, (int)t.Y));
            }

            // BFS 迭代上限：地图首层面积
            int maxIterations = loc.map.Layers[0].LayerWidth * loc.map.Layers[0].LayerHeight;

            var cameFrom = new Dictionary<Point, Point>();
            var visited = new HashSet<Point> { start };
            var frontier = new Queue<Point>();
            frontier.Enqueue(start);

            int[] dxs = { 0, 0, -1, 1 };
            int[] dys = { -1, 1, 0, 0 };
            bool found = false;
            int iterations = 0;

            while (frontier.Count > 0 && iterations < maxIterations && !found)
            {
                iterations++;
                Point current = frontier.Dequeue();

                for (int i = 0; i < 4; i++)
                {
                    var next = new Point(current.X + dxs[i], current.Y + dys[i]);
                    if (visited.Contains(next))
                        continue;

                    // 终点格无条件可达（vanilla findPath 邻接判定先例）；其余格须可走且不在阻挡集
                    bool isGoal = next == goal;
                    if (!isGoal)
                    {
                        if (blocked.Contains(next))
                            continue;
                        if (!IsTileWalkable(loc, new Vector2(next.X, next.Y), npc))
                            continue;
                    }

                    visited.Add(next);
                    cameFrom[next] = current;

                    if (isGoal)
                    {
                        found = true;
                        break;
                    }
                    frontier.Enqueue(next);
                }
            }

            if (!found)
            {
                ModEntry.SMonitor?.Log(
                    $"[Pathfinding] Avoidance path unavailable for {npc.Name} to ({goal.X},{goal.Y}), falling back.",
                    LogLevel.Debug);
                return false;
            }

            // 回溯 route：起点→终点序列
            var route = new List<Point>();
            for (Point p = goal; ; p = cameFrom[p])
            {
                route.Add(p);
                if (p == start)
                    break;
            }

            // 栈序与 vanilla reconstructPath 同构：自终点向起点倒序 Push（栈顶=起点）
            var stack = new Stack<Point>();
            for (int i = route.Count - 1; i >= 0; i--)
                stack.Push(route[i]);

            controller = new PathFindController(stack, npc, loc)
            {
                // finalFacingDirection 字段默认 0（反编译 :566 无初始化）：显式还原 TryCreatePath 的到点不转向语义
                endPoint = goal,
                finalFacingDirection = -1
            };
            return true;
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