using System;
using System.Collections.Generic;
using System.Reflection;
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
        // ─── Schedule reflection cache ───
        private static bool      _scheduleReflectionCached;
        private static MethodInfo _getScheduleMethod;
        private static FieldInfo  _scheduleField;

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

            try
            {
                CacheScheduleReflection();

                if (_getScheduleMethod == null || _scheduleField == null)
                    return;

                var schedule = _getScheduleMethod.Invoke(npc, new object[] { Game1.dayOfMonth })
                    as Dictionary<int, SchedulePathDescription>;

                if (schedule == null || schedule.Count == 0)
                    return;

                _scheduleField.SetValue(npc, schedule);

                npc.followSchedule = true;
                npc.checkSchedule(Game1.timeOfDay);

                ModEntry.SMonitor?.Log(
                    $"[ScheduleRestorer] Schedule restored for {npc.Name}.",
                    LogLevel.Debug);
            }
            catch (Exception ex)
            {
                ModEntry.SMonitor?.Log(
                    $"[ScheduleRestorer] Schedule restore failed: {ex.Message}",
                    LogLevel.Warn);
            }
        }

        private static void CacheScheduleReflection()
        {
            if (_scheduleReflectionCached) return;

            _getScheduleMethod = typeof(NPC).GetMethod(
                "getSchedule",
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            _scheduleField =
                typeof(NPC).GetField(
                    "_schedule",
                    BindingFlags.Instance | BindingFlags.NonPublic)
                ?? typeof(NPC).GetField(
                    "<Schedule>k__BackingField",
                    BindingFlags.Instance | BindingFlags.NonPublic);

            _scheduleReflectionCached = true;
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
