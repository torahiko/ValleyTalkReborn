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

            if (TryCollectPendingStops(npc, out int nextStopTime))
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
        private static bool TryCollectPendingStops(NPC npc, out int nextStopTime)
        {
            nextStopTime = -1;

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
            return true;
        }

        /// <summary>
        /// Smooth departure: walk to nearest warp → warp home → resume schedule.
        /// Falls back to direct schedule restore if no suitable warp found.
        /// </summary>
        public void BeginSmoothDeparture(NPC npc)
        {
            if (npc == null) return;

            var loc = npc.currentLocation;
            if (loc == null || Game1.locations == null)
            {
                TryRestoreSchedule(npc);
                return;
            }

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
                    $"[ScheduleRestorer] {npc.Name} no warp within 30 tiles, direct schedule restore.",
                    LogLevel.Debug);

                TryRestoreSchedule(npc);
                return;
            }

            var warpTile   = new Vector2(nearestWarp.X, nearestWarp.Y);
            var walkTarget = MovementPathfinding.FindWalkableTileNearWarp(loc, warpTile, npc);

            ModEntry.SMonitor?.Log(
                $"[ScheduleRestorer] {npc.Name} smooth departure: walking to warp at ({warpTile.X},{warpTile.Y}) on '{loc.Name}'.",
                LogLevel.Debug);

            _moveToTile(
                npc,
                walkTarget,
                () =>
                {
                    try
                    {
                        var (homeMap, homeTile) = CompanionScheduleManager.GetHomeDestinationPublic(npc);

                        if (!string.Equals(npc.currentLocation?.Name, homeMap, StringComparison.OrdinalIgnoreCase))
                            Game1.warpCharacter(npc, homeMap, new Point((int)homeTile.X, (int)homeTile.Y));
                    }
                    catch (Exception ex)
                    {
                        ModEntry.SMonitor?.Log(
                            $"[ScheduleRestorer] SmoothDeparture home warp failed: {ex.Message}",
                            LogLevel.Warn);
                    }

                    CompanionScheduleManager.Instance.ResumeScheduleAfterFollow(npc);
                },
                () => CompanionScheduleManager.Instance.ResumeScheduleAfterFollow(npc));
        }
    }
}
