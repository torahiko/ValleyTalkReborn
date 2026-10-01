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

            if (TryCollectPendingStops(npc, out int nextStopTime, out _))
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
        /// Collects all schedule stops at or after the current time-of-day, sorted ascending,
        /// and loads them into npc.queuedSchedulePaths so the game drives the NPC along its
        /// remaining daily route. Returns false (nextStopTime = -1) when no stops remain.
        /// </summary>
        private static bool TryCollectPendingStops(NPC npc, out int nextStopTime, out string nextStopMap)
        {
            nextStopTime = -1;
            nextStopMap  = null;

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
            nextStopTime = pending[0].Key;
            nextStopMap  = pending[0].Value?.targetLocationName;
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
                    $"[ScheduleRestorer] {npc.Name} anchor map '{anchor.MapName}' invalid, falling back to home destination.",
                    LogLevel.Warn);

                var (homeMap, homeTile) = CompanionScheduleManager.GetHomeDestinationPublic(npc);
                anchor = new FollowAnchorSnapshot
                {
                    MapName         = homeMap,
                    Tile            = homeTile,
                    FacingDirection = npc.FacingDirection
                };
            }

            // ─── Step A：四级优先决策 ───
            DepartureRouteType route;
            int                nextStopTime = -1;
            string             nextStopMap  = null;

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
                else if (TryCollectPendingStops(npc, out nextStopTime, out nextStopMap))
                {
                    route = DepartureRouteType.VanillaSchedule;
                }
                else
                {
                    route = DepartureRouteType.AnchorFallback;
                }
            }
            else if (TryCollectPendingStops(npc, out nextStopTime, out nextStopMap))
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
                    if (string.Equals(npc.currentLocation.Name, nextStopMap, StringComparison.OrdinalIgnoreCase))
                    {
                        // 同图：queuedSchedulePaths 已由决策阶段装载，交还游戏 checkSchedule 接管。
                        npc.followSchedule = true;
                        ModEntry.SMonitor?.Log(
                            $"[ScheduleRestorer] Schedule restored for {npc.Name}: next stop @{nextStopTime}.",
                            LogLevel.Info);
                    }
                    else
                    {
                        // 跨图：走向最近出口，离场后再恢复原版日程。
                        DepartViaNearestWarp(npc, () => TryRestoreSchedule(npc));
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
                                Game1.warpCharacter(npc, homeMap, new Point((int)homeTile.X, (int)homeTile.Y));

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
                            Game1.warpCharacter(npc, anchor.MapName, new Point((int)anchor.Tile.X, (int)anchor.Tile.Y));
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
                            Game1.warpCharacter(npc, anchor.MapName, new Point((int)anchor.Tile.X, (int)anchor.Tile.Y));
                            npc.faceDirection(anchor.FacingDirection);
                        });
                    }
                    break;
                }
            }
        }

        /// <summary>
        /// 走向当前地图最近出口 Warp，到达或寻路失败时执行续接回调；
        /// 地图 30 格内没有可用 Warp 时立即执行续接回调。
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

            if (nearestWarp == null || nearestDist > 30f)
            {
                ModEntry.SMonitor?.Log(
                    $"[ScheduleRestorer] {npc.Name} no warp within 30 tiles on '{loc.Name}', continuing in place.",
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
