using System;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Pathfinding;

namespace ValleytalkReborn.Movement
{
    /// <summary>
    /// Handles schedule restoration after follow/goto ends, and smooth NPC departure
    /// (walk to nearest warp → warp home → resume schedule).
    /// Extracted from MovementManager in MMR-05a.
    /// </summary>
    internal sealed class ScheduleRestorer
    {
        private readonly Action<NPC, Vector2, Action, Action> _moveToTile;
        private readonly Func<NPC, bool> _isCurrentlyFollowingDate;

        /// <summary>
        /// Creates a new ScheduleRestorer.
        /// </summary>
        /// <param name="moveToTile">
        /// Delegates to MovementManager.MoveToTileInternal.
        /// Signature: (npc, targetTile, onSuccess, onFail).
        /// </param>
        /// <param name="isCurrentlyFollowingDate">
        /// Returns true if the NPC is currently the active date follow target.
        /// Used to guard TryRestoreSchedule from restoring schedule during active date.
        /// </param>
        public ScheduleRestorer(
            Action<NPC, Vector2, Action, Action> moveToTile,
            Func<NPC, bool> isCurrentlyFollowingDate)
        {
            _moveToTile = moveToTile ?? throw new ArgumentNullException(nameof(moveToTile));
            _isCurrentlyFollowingDate = isCurrentlyFollowingDate ?? throw new ArgumentNullException(nameof(isCurrentlyFollowingDate));
        }

        /// <summary>
        /// Restores the NPC's original schedule after follow/goto ends.
        /// Only restores if CompanionScheduleManager has NOT scheduled a custom follow for today.
        /// Drives NPC departure by filling queuedSchedulePaths; the game's own per-frame
        /// checkSchedule then consumes the queue (no reflection, no checkSchedule call needed).
        /// </summary>
        public void TryRestoreSchedule(NPC npc)
        {
            if (npc == null) return;

            // Guard: don't restore schedule while date follow is active
            if (_isCurrentlyFollowingDate(npc))
                return;

            try
            {
                if (CompanionScheduleManager.IsLegalSpouse(npc.Name) &&
                    CompanionScheduleManager.Instance.HasCustomScheduleToday(npc.Name))
                {
                    return;
                }
            }
            catch (Exception ex)
            {
                ModEntry.SMonitor?.Log(
                    $"[ScheduleRestorer] TryRestoreSchedule spouse-check failed: {ex.Message}",
                    LogLevel.Trace);
            }

            MovementCoordinator.ClearNpcMovement(npc, suppressSchedule: false);
            npc.ignoreScheduleToday = false;

            if (TryCollectPendingStops(npc, out int nextStopTime, out _, out _, applyToNpc: true))
            {
                ModEntry.SMonitor?.Log(
                    $"[ScheduleRestorer] Schedule restored for {npc.Name}: next stop @{nextStopTime}.",
                    LogLevel.Info);
            }
            else
            {
                ModEntry.SMonitor?.Log(
                    $"[ScheduleRestorer] {npc.Name} no pending schedule stop at {Game1.timeOfDay}.",
                    LogLevel.Debug);
            }
        }

        /// <summary>
        /// Collects all schedule stops at or after the current time-of-day, sorted ascending.
        /// When applyToNpc is true, loads them into npc.queuedSchedulePaths so the game drives the NPC
        /// along its remaining daily route. When applyToNpc is false, performs a read-only query without
        /// mutating npc.queuedSchedulePaths or npc.followSchedule.
        /// Returns false (nextStopTime = -1) when no stops remain.
        /// </summary>
        private static bool TryCollectPendingStops(
            NPC npc,
            out int nextStopTime,
            out string nextStopMap,
            out Point nextStopTile,
            bool applyToNpc = true)
        {
            nextStopTime = -1;
            nextStopMap  = null;
            nextStopTile = Point.Zero;

            try
            {
                if (npc.Schedule == null || npc.Schedule.Count == 0)
                    npc.TryLoadSchedule();
            }
            catch (Exception ex)
            {
                ModEntry.SMonitor?.Log(
                    $"[ScheduleRestorer] {npc.Name} schedule load failed: {ex.Message}",
                    LogLevel.Warn);
            }

            if (npc.Schedule == null || npc.Schedule.Count == 0)
                return false;

            int now = Game1.timeOfDay;

            // Collect stops at/after now, ascending by time.
            var pending = npc.Schedule
                .Where(kv => kv.Key >= now)
                .OrderBy(kv => kv.Key)
                .ToList();

            if (pending.Count == 0)
                return false;

            nextStopTime = pending[0].Key;
            nextStopMap  = pending[0].Value?.targetLocationName;
            nextStopTile = pending[0].Value?.targetTile ?? Point.Zero;

            if (applyToNpc)
            {
                npc.queuedSchedulePaths.Clear();
                foreach (var kv in pending)
                {
                    // Defensive re-filter: skip any stale entry that slipped past the query.
                    if (kv.Key < now)
                    {
                        ModEntry.SMonitor?.Log(
                            $"[ScheduleRestorer] {npc.Name} skipping stale schedule stop @{kv.Key} (now={now}).",
                            LogLevel.Trace);
                        continue;
                    }
                    npc.queuedSchedulePaths.Add(kv.Value);
                }

                if (npc.queuedSchedulePaths.Count == 0)
                    return false;

                npc.followSchedule = true;
            }

            return true;
        }

        /// <summary>
        /// 尝试获取当前时间应当所处的原版日程停靠点（即最新一条 time &lt;= now 的记录）。
        /// 若不存在（如当天首个日程时刻之前），返回 false。
        /// </summary>
        private static bool TryGetCurrentScheduleStop(
            NPC npc,
            out int stopTime,
            out string stopMap,
            out Point stopTile,
            out int facingDir)
        {
            stopTime  = -1;
            stopMap   = null;
            stopTile  = Point.Zero;
            facingDir = 2;

            if (npc.Schedule == null || npc.Schedule.Count == 0)
                return false;

            int now = Game1.timeOfDay;
            var pastOrCurrent = npc.Schedule
                .Where(kv => kv.Key <= now)
                .OrderByDescending(kv => kv.Key)
                .FirstOrDefault();

            if (pastOrCurrent.Value == null)
                return false;

            stopTime  = pastOrCurrent.Key;
            stopMap   = pastOrCurrent.Value.targetLocationName;
            stopTile  = pastOrCurrent.Value.targetTile;
            facingDir = pastOrCurrent.Value.facingDirection;
            return true;
        }

        /// <summary>离场四级优先决策的路由类型。</summary>
        private enum DepartureRouteType
        {
            /// <summary>Level 1：CSM 配偶外向日程（恢复 POI 队列）。</summary>
            CsmSchedule,
            /// <summary>Level 2：CSM 配偶农场游荡（走回/传回锚点后居家）。</summary>
            SpouseStayHome,
            /// <summary>Level 3：原版村民日程（queuedSchedulePaths 接管）。</summary>
            VanillaSchedule,
            /// <summary>Level 4：无日程兜底（走回/传回锚点）。</summary>
            AnchorFallback
        }

        /// <summary>
        /// 跟随结束后的离场四级优先决策：
        /// 1) CSM 配偶外向日程 → 2) CSM 配偶农场游荡（PreFollowStayHome，回到锚点）→
        /// 3) 原版村民日程 → 4) 无日程兜底（回到锚点）。
        /// 同图平滑走回目标格；跨图走向最近出口 Warp 后传送/恢复。
        /// </summary>
        public void BeginSmoothDeparture(NPC npc, FollowAnchorSnapshot anchor)
        {
            if (npc == null || npc.currentLocation == null)
            {
                ModEntry.SMonitor?.Log(
                    $"[ScheduleRestorer] BeginSmoothDeparture boundary: npc={npc?.Name ?? "null"}, " +
                    $"currentLocation={(npc?.currentLocation == null ? "null" : "ok")}.",
                    LogLevel.Warn);
                return;
            }

            if (string.IsNullOrEmpty(anchor.MapName) || Game1.getLocationFromName(anchor.MapName) == null)
            {
                ModEntry.SMonitor?.Log(
                    $"[ScheduleRestorer] {npc.Name} anchor map '{anchor.MapName}' invalid, determining fallback destination.",
                    LogLevel.Warn);

                string fallbackMap;
                Vector2 fallbackTile;

                if (CompanionScheduleManager.IsLegalSpouse(npc.Name))
                {
                    (fallbackMap, fallbackTile) = CompanionScheduleManager.GetHomeDestinationPublic(npc);
                }
                else if (!string.IsNullOrWhiteSpace(npc.DefaultMap) && Game1.getLocationFromName(npc.DefaultMap) != null)
                {
                    fallbackMap  = npc.DefaultMap;
                    fallbackTile = new Vector2(npc.DefaultPosition.X / 64f, npc.DefaultPosition.Y / 64f);
                }
                else
                {
                    (fallbackMap, fallbackTile) = CompanionScheduleManager.GetHomeDestinationPublic(npc);
                }

                anchor = new FollowAnchorSnapshot
                {
                    MapName         = fallbackMap,
                    Tile            = fallbackTile,
                    FacingDirection = npc.FacingDirection
                };
            }

            // ─── Step A：四级优先决策 ───
            DepartureRouteType route;
            int                nextStopTime = -1;
            string             nextStopMap  = null;
            Point              nextStopTile = Point.Zero;

            if (CompanionScheduleManager.IsLegalSpouse(npc.Name))
            {
                if (CompanionScheduleManager.Instance.IsPreFollowStayHome(npc.Name))
                {
                    route = DepartureRouteType.SpouseStayHome;
                }
                else if (CompanionScheduleManager.Instance.HasCustomScheduleToday(npc.Name))
                {
                    route = DepartureRouteType.CsmSchedule;
                }
                else
                {
                    // MMR-06：配偶常态归属锚点（农舍/木屋），无 CSM 日程时确定性收敛于此。
                    // 原版早晨不会为合法配偶排发日程，但多婚环境下的非首位配偶（isMarried()==false）
                    // 仍保有单身日程文件——绝不可将其派发为 VanillaSchedule 跨图外勤。
                    route = DepartureRouteType.AnchorFallback;
                }
            }
            else if (TryCollectPendingStops(npc, out nextStopTime, out nextStopMap, out nextStopTile, applyToNpc: false))
            {
                route = DepartureRouteType.VanillaSchedule;
            }
            else
            {
                route = DepartureRouteType.AnchorFallback;
            }

            ModEntry.SMonitor?.Log(
                $"[ScheduleRestorer] {npc.Name} departure route={route}, " +
                $"nextStop={nextStopTime}@{nextStopMap ?? "-"}, " +
                $"anchor='{anchor.MapName}'({anchor.Tile.X},{anchor.Tile.Y}).",
                LogLevel.Debug);

            // ─── Step B：同图 / 跨图分流执行 ───
            switch (route)
            {
                case DepartureRouteType.VanillaSchedule:
                {
                    string  targetMap;
                    Vector2 targetTile;
                    int     targetFacing;

                    if (TryGetCurrentScheduleStop(npc, out _, out string currMap, out Point currTile, out int currFacing)
                        && !string.IsNullOrWhiteSpace(currMap)
                        && Game1.getLocationFromName(currMap) != null)
                    {
                        targetMap    = currMap;
                        targetTile   = new Vector2(currTile.X, currTile.Y);
                        targetFacing = currFacing;
                    }
                    else if (!string.IsNullOrWhiteSpace(nextStopMap) && Game1.getLocationFromName(nextStopMap) != null)
                    {
                        targetMap    = nextStopMap;
                        targetTile   = new Vector2(nextStopTile.X, nextStopTile.Y);
                        targetFacing = 2;
                    }
                    else
                    {
                        targetMap    = anchor.MapName;
                        targetTile   = anchor.Tile;
                        targetFacing = anchor.FacingDirection;
                    }

                    // MMR-06 同图招募保护（配偶已不再进入本分支，此处均为非配偶）：
                    // 同图招募并同图解散时，NPC 的空间归属即当前地图锚点；
                    // 即使日程目标在别的地图，也走回锚点留守，绝不触发平滑离场跨图传送。
                    // ignoreScheduleToday 由原版 resetForNewDay 次日清晨自动复位。
                    if (string.Equals(npc.currentLocation.Name, anchor.MapName, StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(npc.currentLocation.Name, targetMap, StringComparison.OrdinalIgnoreCase))
                    {
                        npc.ignoreScheduleToday = true;
                        npc.followSchedule = false;
                        npc.queuedSchedulePaths?.Clear();

                        ModEntry.SMonitor?.Log(
                            $"[ScheduleRestorer] {npc.Name} recruited and dismissed on anchor map '{anchor.MapName}' " +
                            $"while schedule targets '{targetMap}' — walking back to anchor instead of warping.",
                            LogLevel.Info);

                        _moveToTile(
                            npc,
                            anchor.Tile,
                            () => npc.faceDirection(anchor.FacingDirection),
                            () => npc.faceDirection(anchor.FacingDirection));
                        break;
                    }

                    if (string.Equals(npc.currentLocation.Name, targetMap, StringComparison.OrdinalIgnoreCase))
                    {
                        // 同图：已在目标地图，现在安全装载 queuedSchedulePaths，交还游戏 checkSchedule 接管。
                        npc.ignoreScheduleToday = false;
                        TryCollectPendingStops(npc, out _, out _, out _, applyToNpc: true);
                        npc.followSchedule = true;
                        ModEntry.SMonitor?.Log(
                            $"[ScheduleRestorer] Schedule restored for {npc.Name} in-place on '{targetMap}': next stop @{nextStopTime}.",
                            LogLevel.Info);
                    }
                    else
                    {
                        // 跨图：保持日程压制，走向最近出口，到达后传送到目标地图并恢复日程。
                        npc.ignoreScheduleToday = true;
                        npc.followSchedule = false;
                        npc.queuedSchedulePaths?.Clear();

                        DepartViaNearestWarp(npc, () =>
                        {
                            var targetLoc = !string.IsNullOrWhiteSpace(targetMap) ? Game1.getLocationFromName(targetMap) : null;
                            if (targetLoc != null)
                            {
                                var landingTile = MovementPathfinding.FindWalkableTileNear(targetLoc, targetTile, npc) ?? targetTile;
                                MultiMapNavigator.WarpDirectTo(npc, targetMap, landingTile);
                                npc.faceDirection(targetFacing);
                            }
                            else
                            {
                                MultiMapNavigator.WarpDirectTo(npc, anchor.MapName, anchor.Tile);
                                npc.faceDirection(anchor.FacingDirection);
                            }
                            TryRestoreSchedule(npc);
                        });
                    }
                    break;
                }

                case DepartureRouteType.CsmSchedule:
                {
                    bool onFarm = string.Equals(npc.currentLocation.Name, "Farm",      StringComparison.OrdinalIgnoreCase)
                               || string.Equals(npc.currentLocation.Name, "FarmHouse", StringComparison.OrdinalIgnoreCase);
                    if (onFarm)
                    {
                        CompanionScheduleManager.Instance.ResumeScheduleAfterFollow(npc, restoreStayHome: false);
                    }
                    else
                    {
                        DepartViaNearestWarp(npc, () =>
                        {
                            var (homeMap, homeTile) = CompanionScheduleManager.GetHomeDestinationPublic(npc);
                            if (!string.Equals(npc.currentLocation?.Name, homeMap, StringComparison.OrdinalIgnoreCase))
                            {
                                var homeLoc = Game1.getLocationFromName(homeMap);
                                var landingTile = homeLoc != null
                                    ? (MovementPathfinding.FindWalkableTileNear(homeLoc, homeTile, npc) ?? homeTile)
                                    : homeTile;
                                MultiMapNavigator.WarpDirectTo(npc, homeMap, landingTile);
                            }

                            CompanionScheduleManager.Instance.ResumeScheduleAfterFollow(npc, restoreStayHome: false);
                        });
                    }
                    break;
                }

                case DepartureRouteType.SpouseStayHome:
                {
                    bool isSameMap = string.Equals(npc.currentLocation?.Name, anchor.MapName, StringComparison.OrdinalIgnoreCase);
                    if (isSameMap)
                    {
                        _moveToTile(
                            npc,
                            anchor.Tile,
                            () =>
                            {
                                npc.faceDirection(anchor.FacingDirection);
                                CompanionScheduleManager.Instance.ResumeScheduleAfterFollow(npc, restoreStayHome: true);
                            },
                            () => CompanionScheduleManager.Instance.ResumeScheduleAfterFollow(npc, restoreStayHome: true));
                    }
                    else
                    {
                        DepartViaNearestWarp(npc, () =>
                        {
                            var targetLoc = Game1.getLocationFromName(anchor.MapName);
                            var landingTile = targetLoc != null
                                ? (MovementPathfinding.FindWalkableTileNear(targetLoc, anchor.Tile, npc) ?? anchor.Tile)
                                : anchor.Tile;
                            MultiMapNavigator.WarpDirectTo(npc, anchor.MapName, landingTile);
                            CompanionScheduleManager.Instance.ResumeScheduleAfterFollow(npc, restoreStayHome: true);
                        });
                    }
                    break;
                }

                case DepartureRouteType.AnchorFallback:
                {
                    bool isSameMap = string.Equals(npc.currentLocation?.Name, anchor.MapName, StringComparison.OrdinalIgnoreCase);
                    if (isSameMap)
                    {
                        _moveToTile(
                            npc,
                            anchor.Tile,
                            () => npc.faceDirection(anchor.FacingDirection),
                            () => npc.faceDirection(anchor.FacingDirection));
                    }
                    else
                    {
                        DepartViaNearestWarp(npc, () =>
                        {
                            var targetLoc = Game1.getLocationFromName(anchor.MapName);
                            var landingTile = targetLoc != null
                                ? (MovementPathfinding.FindWalkableTileNear(targetLoc, anchor.Tile, npc) ?? anchor.Tile)
                                : anchor.Tile;
                            MultiMapNavigator.WarpDirectTo(npc, anchor.MapName, landingTile);
                            npc.faceDirection(anchor.FacingDirection);
                        });
                    }
                    break;
                }
            }
        }

        /// <summary>
        /// 走向当前地图最近出口 Warp，到达或寻路失败时执行续接回调；
        /// 地图 50 格内没有可用 Warp 时立即执行续接回调。
        /// </summary>
        private void DepartViaNearestWarp(NPC npc, Action onArrivedOrFailed)
        {
            var loc = npc.currentLocation;

            Warp nearestWarp  = null;
            float nearestDist = float.MaxValue;

            if (loc.warps != null)
            {
                foreach (var warp in loc.warps)
                {
                    if (warp == null) continue;
                    float d = Vector2.Distance(npc.Tile, new Vector2(warp.X, warp.Y));
                    if (d < nearestDist) { nearestDist = d; nearestWarp = warp; }
                }
            }

            if (nearestWarp == null || nearestDist > 50f)
            {
                ModEntry.SMonitor?.Log(
                    $"[ScheduleRestorer] {npc.Name} no warp within 50 tiles on '{loc.Name}', continuing in place.",
                    LogLevel.Debug);
                onArrivedOrFailed();
                return;
            }

            var warpTile   = new Vector2(nearestWarp.X, nearestWarp.Y);
            var walkTarget = MovementPathfinding.FindWalkableTileNearWarp(loc, warpTile, npc);

            ModEntry.SMonitor?.Log(
                $"[ScheduleRestorer] {npc.Name} smooth departure: walking to warp at ({warpTile.X},{warpTile.Y}) on '{loc.Name}'.",
                LogLevel.Debug);

            _moveToTile(npc, walkTarget, onArrivedOrFailed, onArrivedOrFailed);
        }
    }
}
