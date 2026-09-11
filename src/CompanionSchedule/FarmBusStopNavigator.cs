using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;

namespace ValleytalkReborn
{
    /// <summary>
    /// ★ 专用于 CompanionScheduleManager 的"农舍 ⇄ 农场 ⇄ 巴士站 ⇄ 目的地"三图导航器。
    ///
    /// 设计约定（与 MultiMapJourney 的通用 BFS 寻路系统相互独立，互不调用）：
    ///   - "农场同侧" = FARM_INTERNAL_MAPS 集合内的地图（Farm/FarmHouse/Greenhouse/Cellar/FarmCave）。
    ///     去这些地方，NPC 在农场内部直接寻路过去，不经巴士站。
    ///   - "巴士站对侧" = 不在上述集合内的任何地图（Town/Mountain/Beach/SeedShop 等）。
    ///     去这些地方，NPC 先在农场内走到通往 BusStop 的 warp，瞬移到 BusStop 落地点（左侧），
    ///     再在 BusStop 内部寻路走到 TargetName=="Town" 的 warp（右侧），瞬移到最终目的地。
    ///   - 所有中间段 warp 坐标均从对应地图的 warps 动态查找（不硬编码），以兼容地图 Mod。
    ///   - 回家是否经巴士站，取决于"出发时是否走的对侧路线"，该状态记录在
    ///     SpouseScheduleState.WentViaBusStop 上，不依赖队列里的具体 entry。
    /// </summary>
    internal static class FarmBusStopNavigator
    {
        /// <summary>视为"农场内部/同侧"的地图集合。POI 目的地在此集合内则不经巴士站。</summary>
        public static readonly HashSet<string> FarmInternalMaps = new(StringComparer.OrdinalIgnoreCase)
        {
            "Farm", "FarmHouse", "Greenhouse", "Cellar", "FarmCave"
        };

        public static bool IsFarmInternal(string mapName)
            => !string.IsNullOrWhiteSpace(mapName) && FarmInternalMaps.Contains(mapName);

        /// <summary>
        /// 带自动重试的 MoveToTile：失败时先尝试恢复起始点（TryRecoverStartingTile），
        /// 延迟 30 tick 后重试，最多 retries 次仍失败才调用 onFinalFail。
        /// 用于农舍门口/巴士站两侧这类关键节点，减少偶发碰撞卡死导致的兜底瞬移。
        /// </summary>
        private static void MoveWithRetry(NPC npc, Vector2 target, int retries, Action onSuccess, Action onFinalFail)
        {
            void Attempt(int remaining)
            {
                MovementManager.Instance.MoveToTile(
                    npc, target,
                    onComplete: onSuccess,
                    onFail: () =>
                    {
                        if (remaining <= 0) { onFinalFail?.Invoke(); return; }
                        var loc = npc.currentLocation;
                        if (loc != null) MovementPathfinding.TryRecoverStartingTile(npc, loc);
                        DelayedAction.functionAfterDelay(() => Attempt(remaining - 1), 30);
                    });
            }
            Attempt(retries);
        }

        // ────────────────────────────────────────────────
        //  Warp 查找辅助（全部动态查找，不硬编码坐标）
        // ────────────────────────────────────────────────

        /// <summary>在指定地图的 warps 里查找 TargetName 等于 targetMapName 的第一个 warp。</summary>
        private static Warp FindWarpTo(GameLocation loc, string targetMapName)
        {
            return loc?.warps?.FirstOrDefault(w =>
                w != null && string.Equals(w.TargetName, targetMapName, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>在 Farm 上找通往 BusStop 的 warp（农场出门去镇上方向的那个门）。</summary>
        private static Warp FindFarmToBusStopWarp()
        {
            var farm = Game1.getFarm();
            return FindWarpTo(farm, "BusStop");
        }

        /// <summary>在 BusStop 上找回农场的 warp（即 BusStop 左侧）。</summary>
        private static Warp FindBusStopToFarmWarp(GameLocation busStop)
            => FindWarpTo(busStop, "Farm");

        /// <summary>在 BusStop 上找去 Town 的 warp（即 BusStop 右侧）。</summary>
        private static Warp FindBusStopToTownWarp(GameLocation busStop)
            => FindWarpTo(busStop, "Town");

        // ────────────────────────────────────────────────
        //  出发：农舍 → 农场 → (可能经巴士站) → 目的地
        // ────────────────────────────────────────────────

        /// <summary>
        /// 从农舍出发前往目的地。会先走到农舍出口 warp，再瞬移到农场，然后按同侧/对侧规则继续。
        /// </summary>
        public static void DepartFromFarmHouse(
            NPC npc,
            string destMapName,
            Vector2 destTile,
            Action<bool /*wentViaBusStop*/> onArrived,
            Action onFail,
            Action onPathStarted = null)
        {
            var farmHouse = npc.currentLocation;
            var exitWarp  = farmHouse != null ? FindWarpTo(farmHouse, "Farm") : null;

            if (exitWarp == null)
            {
                ModEntry.SMonitor?.Log(
                    $"[FarmBusStopNav] {npc.Name}: FarmHouse has no warp to Farm — warping directly.",
                    LogLevel.Warn);
                var farmEntry = Game1.getFarm().GetMainFarmHouseEntry();
                Game1.warpCharacter(npc, "Farm", new Point(farmEntry.X, farmEntry.Y + 1));
                ContinueDepartFromFarm(npc, destMapName, destTile, onArrived, onFail, onPathStarted);
                return;
            }

            var exitTile = new Vector2(exitWarp.X, exitWarp.Y);
            MovementManager.Instance.MoveToTile(npc, exitTile,
                onComplete: () =>
                {
                    Game1.warpCharacter(npc, exitWarp.TargetName, new Point(exitWarp.TargetX, exitWarp.TargetY));
                    ContinueDepartFromFarm(npc, destMapName, destTile, onArrived, onFail, onPathStarted);
                },
                onFail: () =>
                {
                    ModEntry.SMonitor?.Log(
                        $"[FarmBusStopNav] {npc.Name} could not reach FarmHouse exit — warping directly to Farm.",
                        LogLevel.Warn);
                    var farmEntry = Game1.getFarm().GetMainFarmHouseEntry();
                    Game1.warpCharacter(npc, "Farm", new Point(farmEntry.X, farmEntry.Y + 1));
                    ContinueDepartFromFarm(npc, destMapName, destTile, onArrived, onFail, onPathStarted);
                });
        }

        /// <summary>
        /// 假设 NPC 已经在 Farm 上，按同侧/对侧规则继续走向目的地。
        ///
        /// onPathStarted：仅当目的地就是"当前所在地图内的某个坐标"（比如目的地本身就是 Farm）时才会触发——
        /// 此时 NPC 是"发起寻路但尚未走到"，调用方必须借此机会挂一个到达监听（如 StartFarmPoiWatch），
        /// 不能把 onPathStarted 之后的时刻当成已到达。其余情况（瞬移完成、经巴士站等）都是走完 onArrived。
        /// </summary>
        public static void ContinueDepartFromFarm(
            NPC npc,
            string destMapName,
            Vector2 destTile,
            Action<bool /*wentViaBusStop*/> onArrived,
            Action onFail,
            Action onPathStarted = null)
        {
            if (IsFarmInternal(destMapName))
            {
                // 同侧：农场内部（或子地图）直接寻路走过去，不经巴士站。
                DepartWithinFarmInternal(npc, destMapName, destTile, onArrived, onFail, onPathStarted);
                return;
            }

            // 对侧：农场 → BusStop 左侧 → BusStop 内部走到右侧 → 目的地
            var farm = npc.currentLocation;
            var busStopWarp = FindFarmToBusStopWarp();

            if (busStopWarp == null)
            {
                ModEntry.SMonitor?.Log(
                    $"[FarmBusStopNav] {npc.Name}: Farm has no warp to BusStop — warping directly to destination (skipping bus stop).",
                    LogLevel.Warn);
                Game1.warpCharacter(npc, destMapName, new Point((int)destTile.X, (int)destTile.Y));
                onArrived?.Invoke(false);
                return;
            }

            var busStopExitTile = new Vector2(busStopWarp.X, busStopWarp.Y);
            MovementManager.Instance.MoveToTile(npc, busStopExitTile,
                onComplete: () =>
                {
                    Game1.warpCharacter(npc, "BusStop", new Point(busStopWarp.TargetX, busStopWarp.TargetY));
                    WalkThroughBusStopToTown(npc, destMapName, destTile, onArrived, onFail);
                },
                onFail: () =>
                {
                    ModEntry.SMonitor?.Log(
                        $"[FarmBusStopNav] {npc.Name} could not reach Farm→BusStop warp — warping directly to destination.",
                        LogLevel.Warn);
                    Game1.warpCharacter(npc, destMapName, new Point((int)destTile.X, (int)destTile.Y));
                    onArrived?.Invoke(false);
                });
        }

        /// <summary>
        /// 目的地在 FarmInternalMaps 集合内时，农场内部/子地图直接寻路。
        ///
        /// 注意：当目的地与当前所在地图相同时（例如已经在 Farm 上，要走到 Farm 内的某个坐标），
        /// 这里只负责"发起寻路"，不会调用 onArrived —— 真正走到才算到达，调用方需要用
        /// onPathStarted 回调把 controller 交给自己的 tick 循环去监听 IsPathDone
        /// （与 CompanionScheduleManager.StartFarmPoiWatch 是同一套模式，避免"发起寻路即视为到达"的假到达问题）。
        /// 只有"已经站在目标点上"或"寻路到子地图后瞬移完成"这两种情况才会立即调用 onArrived。
        ///
        /// 调用链说明：CompanionScheduleManager.ExecutePoiEntry 里，MapName=="Farm"/"FarmHouse"
        /// 的情况在更早的 targetIsOnFarm 分支就被单独处理了，不会走到这里；因此实际能进入这个方法的
        /// destMapName 只会是 Greenhouse/Cellar/FarmCave，而 NPC 出发时必然身处 Farm/FarmHouse，
        /// 两者不会相等——也就是说 sameMapAsCurrent 分支（及 onPathStarted）在当前调用路径下不会触发，
        /// 属于防御性设计，是为了让这个方法本身保持通用、可被其他调用方复用，而不是死代码。
        /// </summary>
        private static void DepartWithinFarmInternal(
            NPC npc,
            string destMapName,
            Vector2 destTile,
            Action<bool> onArrived,
            Action onFail,
            Action onPathStarted = null)
        {
            bool sameMapAsCurrent = string.Equals(npc.currentLocation?.Name, destMapName, StringComparison.OrdinalIgnoreCase);

            if (sameMapAsCurrent)
            {
                // 已经在目标地图上（比如目的地就是 Farm 本身），直接原地寻路到坐标。
                var loc = npc.currentLocation;
                var safeTarget = MovementPathfinding.FindNearestWalkableTile(loc, destTile, npc, 3);

                if (Vector2.Distance(npc.Tile, safeTarget) < 1f)
                {
                    onArrived?.Invoke(false);
                    return;
                }

                if (MovementPathfinding.TryCreatePath(npc, loc, safeTarget, out var controller, out _))
                {
                    npc.controller = controller;
                    npc.addedSpeed = 2;
                    // 关键：不在这里调用 onArrived —— NPC 还在路上。调用方通过 onPathStarted
                    // 挂一个到达监听（沿用 StartFarmPoiWatch 的 IsPathDone 轮询模式）。
                    onPathStarted?.Invoke();
                }
                else
                {
                    ModEntry.SMonitor?.Log(
                        $"[FarmBusStopNav] {npc.Name} cannot path to internal target on '{destMapName}'.",
                        LogLevel.Info);
                    onFail?.Invoke();
                }
                return;
            }

            // 目标是农场的子地图（Greenhouse/Cellar/FarmCave），从 Farm 上找对应 warp 走过去再瞬移。
            // 走到 warp 是"真实到达 warp"（MoveToTile 的 onComplete），瞬移之后 NPC 就站在子地图里了，
            // 这一步瞬移完成后才算真正"抵达"，此时调用 onArrived 是合理的（不是假到达）。
            var farm = npc.currentLocation;
            var subMapWarp = FindWarpTo(farm, destMapName);

            if (subMapWarp == null)
            {
                ModEntry.SMonitor?.Log(
                    $"[FarmBusStopNav] {npc.Name}: Farm has no warp to '{destMapName}' — warping directly.",
                    LogLevel.Warn);
                Game1.warpCharacter(npc, destMapName, new Point((int)destTile.X, (int)destTile.Y));
                onArrived?.Invoke(false);
                return;
            }

            var exitTile = new Vector2(subMapWarp.X, subMapWarp.Y);
            MovementManager.Instance.MoveToTile(npc, exitTile,
                onComplete: () =>
                {
                    Game1.warpCharacter(npc, subMapWarp.TargetName, new Point(subMapWarp.TargetX, subMapWarp.TargetY));
                    onArrived?.Invoke(false);
                },
                onFail: () =>
                {
                    Game1.warpCharacter(npc, destMapName, new Point((int)destTile.X, (int)destTile.Y));
                    onArrived?.Invoke(false);
                });
        }

        /// <summary>假设 NPC 刚瞬移到 BusStop 落地点，走到右侧（Town 方向）warp 再瞬移到最终目的地。</summary>
        private static void WalkThroughBusStopToTown(
            NPC npc,
            string destMapName,
            Vector2 destTile,
            Action<bool> onArrived,
            Action onFail)
        {
            var busStop = npc.currentLocation;
            var townWarp = busStop != null ? FindBusStopToTownWarp(busStop) : null;

            if (townWarp == null)
            {
                ModEntry.SMonitor?.Log(
                    $"[FarmBusStopNav] {npc.Name}: BusStop has no warp to Town — warping directly to destination.",
                    LogLevel.Warn);
                Game1.warpCharacter(npc, destMapName, new Point((int)destTile.X, (int)destTile.Y));
                onArrived?.Invoke(true);
                return;
            }

            var exitTile = new Vector2(townWarp.X, townWarp.Y);
            MovementManager.Instance.MoveToTile(npc, exitTile,
                onComplete: () =>
                {
                    // 如果目的地正好就是 Town，落到 warp 的 landing tile 附近即可；
                    // 否则说明目的地是 Town 之外更远的图（Mountain/Beach 等），
                    // 简化处理：先落到 Town，再直接瞬移到最终目的地（不做 Town 内部二次寻路，
                    // 与三图体系的范围保持一致——只在 Farm/FarmHouse/BusStop 三图内寻路）。
                    if (string.Equals(destMapName, "Town", StringComparison.OrdinalIgnoreCase))
                    {
                        Game1.warpCharacter(npc, "Town", new Point(townWarp.TargetX, townWarp.TargetY));
                    }
                    else
                    {
                        Game1.warpCharacter(npc, destMapName, new Point((int)destTile.X, (int)destTile.Y));
                    }
                    onArrived?.Invoke(true);
                },
                onFail: () =>
                {
                    ModEntry.SMonitor?.Log(
                        $"[FarmBusStopNav] {npc.Name} could not walk across BusStop — warping directly to destination.",
                        LogLevel.Warn);
                    Game1.warpCharacter(npc, destMapName, new Point((int)destTile.X, (int)destTile.Y));
                    onArrived?.Invoke(true);
                });
        }

        // ────────────────────────────────────────────────
        //  回家：目的地 → (可能经巴士站) → 农场 → 农舍
        // ────────────────────────────────────────────────

        /// <summary>
        /// 从任意地图返回农舍。wentViaBusStop 应该取自出发时记录的 SpouseScheduleState.WentViaBusStop——
        /// 只有出发时走的是"对侧/经巴士站"路线，回家才会再次经停巴士站；
        /// 否则视为"非农舍出发"或"同侧"两种情形，直接走 当前图→Farm→FarmHouse。
        /// </summary>
        public static void ReturnHome(
            NPC npc,
            bool wentViaBusStop,
            Action onArrivedHome,
            Action onFail)
        {
            string currentMap = npc.currentLocation?.Name ?? "";

            if (string.Equals(currentMap, "Farm", StringComparison.OrdinalIgnoreCase))
            {
                WalkFarmToFarmHouse(npc, onArrivedHome, onFail);
                return;
            }

            if (string.Equals(currentMap, "FarmHouse", StringComparison.OrdinalIgnoreCase))
            {
                onArrivedHome?.Invoke();
                return;
            }

            if (wentViaBusStop)
            {
                // 对称路线：当前地图 → 瞬移到 BusStop 右侧 → 走到左侧 → 瞬移回 Farm → 走回 FarmHouse。
                ReturnViaBusStop(npc, onArrivedHome, onFail);
                return;
            }

            // 非农舍出发 / 同侧出发：直接找当前地图出口 warp 走出去，然后落到 Farm，再走回 FarmHouse。
            ReturnDirectlyToFarm(npc, onArrivedHome, onFail);
        }

        private static void ReturnViaBusStop(NPC npc, Action onArrivedHome, Action onFail)
        {
            string currentMap = npc.currentLocation?.Name ?? "";

            if (string.Equals(currentMap, "BusStop", StringComparison.OrdinalIgnoreCase))
            {
                WalkBusStopToFarm(npc, onArrivedHome, onFail);
                return;
            }

            // 先从当前地图（通常是 Town）走到通往 BusStop 的 warp。
            var loc = npc.currentLocation;
            var busStopWarp = loc != null ? FindWarpTo(loc, "BusStop") : null;

            if (busStopWarp != null)
            {
                var exitTile = new Vector2(busStopWarp.X, busStopWarp.Y);
                MovementManager.Instance.MoveToTile(npc, exitTile,
                    onComplete: () =>
                    {
                        Game1.warpCharacter(npc, "BusStop", new Point(busStopWarp.TargetX, busStopWarp.TargetY));
                        WalkBusStopToFarm(npc, onArrivedHome, onFail);
                    },
                    onFail: () =>
                    {
                        ModEntry.SMonitor?.Log(
                            $"[FarmBusStopNav] {npc.Name} could not reach BusStop warp from '{currentMap}' — warping directly to BusStop.",
                            LogLevel.Warn);
                        WarpToBusStopRightSide(npc);
                        WalkBusStopToFarm(npc, onArrivedHome, onFail);
                    });
                return;
            }

            // 找不到直达 BusStop 的 warp（比如站在更远的地图），直接瞬移到 BusStop 右侧兜底。
            ModEntry.SMonitor?.Log(
                $"[FarmBusStopNav] {npc.Name}: no warp to BusStop from '{currentMap}' — warping directly to BusStop right side.",
                LogLevel.Warn);
            WarpToBusStopRightSide(npc);
            WalkBusStopToFarm(npc, onArrivedHome, onFail);
        }

        /// <summary>兜底：直接把 NPC 放到 BusStop 内、Town warp 的落脚点附近（即"右侧"）。</summary>
        private static void WarpToBusStopRightSide(NPC npc)
        {
            var busStop = Game1.getLocationFromName("BusStop");
            var townWarp = busStop != null ? FindWarpTo(busStop, "Town") : null;

            if (townWarp != null)
            {
                var landing = MovementPathfinding.FindWalkableTileNearWarp(
                    busStop, new Vector2(townWarp.TargetX, townWarp.TargetY), npc);
                Game1.warpCharacter(npc, "BusStop", new Point((int)landing.X, (int)landing.Y));
            }
            else
            {
                var farmEntry = Game1.getFarm().GetMainFarmHouseEntry();
                Game1.warpCharacter(npc, "Farm", new Point(farmEntry.X, farmEntry.Y + 1));
            }
        }

        /// <summary>假设 NPC 已在 BusStop，走到左侧（Farm 方向）warp，瞬移回 Farm。</summary>
        private static void WalkBusStopToFarm(NPC npc, Action onArrivedHome, Action onFail)
        {
            var busStop = npc.currentLocation;
            var farmWarp = busStop != null ? FindBusStopToFarmWarp(busStop) : null;

            if (farmWarp == null)
            {
                ModEntry.SMonitor?.Log(
                    $"[FarmBusStopNav] {npc.Name}: BusStop has no warp to Farm — warping directly to Farm.",
                    LogLevel.Warn);
                var farmEntry = Game1.getFarm().GetMainFarmHouseEntry();
                Game1.warpCharacter(npc, "Farm", new Point(farmEntry.X, farmEntry.Y + 1));
                WalkFarmToFarmHouse(npc, onArrivedHome, onFail);
                return;
            }

            var exitTile = new Vector2(farmWarp.X, farmWarp.Y);
            MovementManager.Instance.MoveToTile(npc, exitTile,
                onComplete: () =>
                {
                    Game1.warpCharacter(npc, "Farm", new Point(farmWarp.TargetX, farmWarp.TargetY));
                    WalkFarmToFarmHouse(npc, onArrivedHome, onFail);
                },
                onFail: () =>
                {
                    ModEntry.SMonitor?.Log(
                        $"[FarmBusStopNav] {npc.Name} could not walk across BusStop to Farm side — warping directly.",
                        LogLevel.Warn);
                    var farmEntry = Game1.getFarm().GetMainFarmHouseEntry();
                    Game1.warpCharacter(npc, "Farm", new Point(farmEntry.X, farmEntry.Y + 1));
                    WalkFarmToFarmHouse(npc, onArrivedHome, onFail);
                });
        }

        /// <summary>非经巴士站的返程：从任意非农场地图直接找出口 warp 走出去，落到 Farm。</summary>
        private static void ReturnDirectlyToFarm(NPC npc, Action onArrivedHome, Action onFail)
        {
            var loc = npc.currentLocation;
            if (loc == null)
            {
                var farmEntry = Game1.getFarm().GetMainFarmHouseEntry();
                Game1.warpCharacter(npc, "Farm", new Point(farmEntry.X, farmEntry.Y + 1));
                WalkFarmToFarmHouse(npc, onArrivedHome, onFail);
                return;
            }

            // 优先找直接通向 Farm 的 warp（矿洞/Greenhouse/Cellar 等农场子地图通常都有）。
            var farmWarp = FindWarpTo(loc, "Farm");

            if (farmWarp != null)
            {
                var exitTile = new Vector2(farmWarp.X, farmWarp.Y);
                MovementManager.Instance.MoveToTile(npc, exitTile,
                    onComplete: () =>
                    {
                        Game1.warpCharacter(npc, "Farm", new Point(farmWarp.TargetX, farmWarp.TargetY));
                        WalkFarmToFarmHouse(npc, onArrivedHome, onFail);
                    },
                    onFail: () =>
                    {
                        var farmEntry = Game1.getFarm().GetMainFarmHouseEntry();
                        Game1.warpCharacter(npc, "Farm", new Point(farmEntry.X, farmEntry.Y + 1));
                        WalkFarmToFarmHouse(npc, onArrivedHome, onFail);
                    });
                return;
            }

            // 找不到直达 Farm 的 warp（说明当前在一个不属于三图体系、也没有直连 Farm 的地图，
            // 比如非农舍出发流程里去了 Town）：室内找任意出口 warp 走出去，室外则直接瞬移兜底。
            bool isOutdoors = loc.IsOutdoors;
            Warp anyExit = isOutdoors ? null : loc.warps?.FirstOrDefault(w => w != null && !string.IsNullOrWhiteSpace(w.TargetName));

            if (anyExit != null)
            {
                var exitTile = new Vector2(anyExit.X, anyExit.Y);
                MovementManager.Instance.MoveToTile(npc, exitTile,
                    onComplete: () =>
                    {
                        Game1.warpCharacter(npc, anyExit.TargetName, new Point(anyExit.TargetX, anyExit.TargetY));
                        // 走到了中间地图，不在三图体系内继续寻路，直接兜底瞬移回 Farm。
                        var farmEntry = Game1.getFarm().GetMainFarmHouseEntry();
                        Game1.warpCharacter(npc, "Farm", new Point(farmEntry.X, farmEntry.Y + 1));
                        WalkFarmToFarmHouse(npc, onArrivedHome, onFail);
                    },
                    onFail: () =>
                    {
                        var farmEntry = Game1.getFarm().GetMainFarmHouseEntry();
                        Game1.warpCharacter(npc, "Farm", new Point(farmEntry.X, farmEntry.Y + 1));
                        WalkFarmToFarmHouse(npc, onArrivedHome, onFail);
                    });
                return;
            }

            ModEntry.SMonitor?.Log(
                $"[FarmBusStopNav] {npc.Name}: no usable exit from '{loc.Name}' — warping directly to Farm.",
                LogLevel.Warn);
            var fallbackEntry = Game1.getFarm().GetMainFarmHouseEntry();
            Game1.warpCharacter(npc, "Farm", new Point(fallbackEntry.X, fallbackEntry.Y + 1));
            WalkFarmToFarmHouse(npc, onArrivedHome, onFail);
        }

        /// <summary>
        /// 解析 FarmHouse 室内落脚点（warp 进 FarmHouse 后 NPC 出现的格）。
        /// 候选顺序（确定性）：
        ///   a. FarmHouse 的 exitWarp（TargetName=="Farm"）正上方一格 (X, Y-1)，若可行走即采用；
        ///   b. 否则以 FindSafeWarpTile 在 exitWarp 坐标（缺省 (9,11)）附近搜索；
        ///   c. 全部失败返回 false（landing = Point.Zero）。
        /// </summary>
        private static bool TryResolveFarmHouseInteriorLanding(NPC npc, out Point landing)
        {
            landing = Point.Zero;

            var farmHouse = Game1.getLocationFromName("FarmHouse");
            if (farmHouse == null)
                return false;

            var exitWarp = FindWarpTo(farmHouse, "Farm");

            // 候选 a：exitWarp 正上方一格 (X, Y-1)。
            if (exitWarp != null)
            {
                var candidate = new Vector2(exitWarp.X, exitWarp.Y - 1);
                if (MovementPathfinding.IsTileWalkable(farmHouse, candidate, npc))
                {
                    landing = new Point((int)candidate.X, (int)candidate.Y);
                    return true;
                }
            }

            // 候选 b：FindSafeWarpTile 在 exitWarp 坐标附近搜索（无 exitWarp 时以 (9,11) 为中心）。
            var near = exitWarp != null
                ? new Vector2(exitWarp.X, exitWarp.Y)
                : new Vector2(9f, 11f);
            var safe = MovementPathfinding.FindSafeWarpTile(farmHouse, near, npc);
            if (safe.HasValue)
            {
                landing = new Point((int)safe.Value.X, (int)safe.Value.Y);
                return true;
            }

            // 候选 c：全部失败。
            return false;
        }

        /// <summary>
        /// 解析 Farm 图上农舍门口前方格（NPC 寻路终点）。
        /// 主路径：FarmHouse exitWarp 的 TargetX/TargetY（即"出农舍后在 Farm 上的落点格"，随房子搬动由游戏维护）。
        /// 兜底：无 exitWarp → GetMainFarmHouseEntry() + (0,+1)，并置 usedEntryFallback=true。
        /// </summary>
        private static Vector2 ResolveFarmHouseDoorFrontOnFarm(out bool usedEntryFallback)
        {
            var farmHouse = Game1.getLocationFromName("FarmHouse");
            var exitWarp = farmHouse != null ? FindWarpTo(farmHouse, "Farm") : null;

            if (exitWarp != null)
            {
                usedEntryFallback = false;
                return new Vector2(exitWarp.TargetX, exitWarp.TargetY);
            }

            usedEntryFallback = true;
            var entry = Game1.getFarm().GetMainFarmHouseEntry();
            return new Vector2(entry.X, entry.Y + 1);
        }

        /// <summary>
        /// 假设 NPC 已在 Farm，寻路走到农舍门口，瞬移进 FarmHouse。
        /// 室内落脚点在发起移动前解析；寻路重试耗尽同样强传进室内（回调 onArrivedHome，不是 onFail）；
        /// 仅 Farm 不可用或室内落脚点无法解析时走 onFail（NPC 保持原位、未 warp）。
        /// </summary>
        private static void WalkFarmToFarmHouse(NPC npc, Action onArrivedHome, Action onFail)
        {
            // 1. farm 必须可用。
            var farm = Game1.getFarm();
            if (farm == null)
            {
                ModEntry.SMonitor?.Log(
                    $"[FarmBusStopNav] Farm unavailable — cannot return {npc.Name} home.",
                    LogLevel.Error);
                onFail?.Invoke();
                return;
            }

            // 2. 室内落脚点提前解析（发起移动前）。
            if (!TryResolveFarmHouseInteriorLanding(npc, out var landing))
            {
                ModEntry.SMonitor?.Log(
                    $"[FarmBusStopNav] {npc.Name}: cannot resolve FarmHouse interior landing — aborting return.",
                    LogLevel.Error);
                onFail?.Invoke();
                return;
            }

            // 3. 农舍门口前方格（寻路终点）。
            var doorFront = ResolveFarmHouseDoorFrontOnFarm(out bool usedEntryFallback);
            if (usedEntryFallback)
            {
                ModEntry.SMonitor?.Log(
                    "[FarmBusStopNav] FarmHouse exit warp missing — using GetMainFarmHouseEntry fallback for door front.",
                    LogLevel.Warn);
            }
            else
            {
                ModEntry.SMonitor?.Log(
                    $"[FarmBusStopNav] {npc.Name} FarmHouse door front resolved to ({(int)doorFront.X},{(int)doorFront.Y}) via FarmHouse exit warp.",
                    LogLevel.Debug);
            }

            // 4. 门口附近最近可行走格。
            var targetTile = MovementPathfinding.FindNearestWalkableTile(farm, doorFront, npc, radius: 3);

            // 5. 带重试寻路；重试耗尽同样强传进室内（onArrivedHome，不是 onFail）。
            MoveWithRetry(npc, targetTile, retries: 2,
                onSuccess: () =>
                {
                    Game1.warpCharacter(npc, "FarmHouse", landing);
                    onArrivedHome?.Invoke();
                },
                onFinalFail: () =>
                {
                    ModEntry.SMonitor?.Log(
                        $"[FarmBusStopNav] {npc.Name} could not path to FarmHouse door after retries — warping directly inside.",
                        LogLevel.Warn);
                    Game1.warpCharacter(npc, "FarmHouse", landing);
                    onArrivedHome?.Invoke();
                });
        }
    }
}
