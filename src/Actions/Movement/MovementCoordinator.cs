using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Pathfinding;

namespace ValleytalkReborn.Movement
{
    /// <summary>
    /// Central coordinator owning all movement trackers, schedule restorer, and tick orchestration.
    /// Extracted from MovementManager in MMR-05b. MovementManager becomes a thin forwarding shell.
    /// </summary>
    internal sealed class MovementCoordinator
    {
        private static readonly Random Rng = new();

        // ─── Trackers ───
        internal readonly GotoMovementTracker   GotoTracker   = new();
        internal readonly StepMovementTracker   StepTracker   = new StepMovementTracker(ClearNpcMovement);
        internal readonly PendingActionQueue    PendingQueue;
        internal readonly FollowMovementTracker FollowTracker;
        internal readonly ScheduleRestorer      ScheduleRestorer;

        internal MovementCoordinator()
        {
            PendingQueue = new PendingActionQueue(ExecuteMovement);

            // ScheduleRestorer before FollowTracker (FollowTracker needs ScheduleRestorer.BeginSmoothDeparture)
            ScheduleRestorer = new ScheduleRestorer(
                MoveToTileInternal,
                npc => FollowTracker != null && FollowTracker.HasActiveDateFollow && FollowTracker.CurrentFollowingNpc == npc);

            FollowTracker = new FollowMovementTracker(
                GotoTracker, ClearNpcMovement, Rng,
                npc => ScheduleRestorer.BeginSmoothDeparture(npc));

            WireGotoTrackerCallbacks();
        }

        // ─── Callback wiring ───

        private void WireGotoTrackerCallbacks()
        {
            GotoTracker.OnSuspendedFollowToRestore = snapshot =>
            {
                if (snapshot != null)
                    FollowTracker.RestoreSuspendedFollow(snapshot.Value);
            };

            GotoTracker.OnGotoEnded = (npc, success) =>
            {
                if (npc == null) return;
                bool stillMoving = GotoTracker.IsMoving(npc.Name)
                                || StepTracker.IsActive(npc.Name)
                                || PendingQueue.HasPending(npc.Name);
                if (!stillMoving && !FollowTracker.IsFollowing(npc))
                    ScheduleRestorer.TryRestoreSchedule(npc);
            };
        }

        // ─── Tick order: pending → step → follow → goto ───

        internal void OnUpdateTicked(UpdateTickedEventArgs e)
        {
            if (!Context.IsWorldReady || Game1.player == null)
                return;

            PendingQueue.TickAll();
            StepTracker.TickAll();
            FollowTracker.Tick(e);
            GotoTracker.TickAll();
        }

        // ─── Lifecycle ───

        internal void OnDayStarted()  => ResetAllState(silent: true);
        internal void OnSaveLoaded()  => ResetAllState(silent: true);

        internal void OnDayEnding()
        {
            if (!Context.IsWorldReady) return;

            if (FollowTracker.HasActiveFollow && FollowTracker.CurrentFollowingNpc != null)
            {
                var followingNpc = FollowTracker.CurrentFollowingNpc;

                FollowTracker.Unbind(silent: true);

                try
                {
                    if (!string.IsNullOrEmpty(followingNpc.DefaultMap))
                    {
                        Game1.warpCharacter(
                            followingNpc,
                            followingNpc.DefaultMap,
                            new Vector2(followingNpc.DefaultPosition.X / 64f, followingNpc.DefaultPosition.Y / 64f));
                    }

                    ScheduleRestorer.TryRestoreSchedule(followingNpc);

                    ModEntry.SMonitor?.Log(
                        $"[MovementCoordinator] OnDayEnding: force-unbound {followingNpc.Name}, warped home + schedule restored.",
                        LogLevel.Info);
                }
                catch (Exception ex)
                {
                    ModEntry.SMonitor?.Log(
                        $"[MovementCoordinator] OnDayEnding cleanup failed for {followingNpc.Name}: {ex.Message}",
                        LogLevel.Warn);
                }
            }

            ResetAllState(silent: true);
        }

        private void ResetAllState(bool silent)
        {
            PendingQueue.ResetAll();
            StepTracker.ResetAll();
            GotoTracker.Clear();
            FollowTracker.ResetAll();
        }

        // ─── Query ───

        internal bool IsNpcMoving(NPC npc)
        {
            if (npc == null) return false;
            return GotoTracker.IsMoving(npc.Name) || StepTracker.IsActive(npc.Name);
        }

        internal bool IsTileTargetedByOtherNpc(Vector2 tile, GameLocation loc, NPC exclude)
            => GotoTracker.IsTileTargetedByOther(tile, loc, exclude?.Name);

        // ─── Queue ───

        internal void QueueMovement(NPC npc, string actionType, bool skipDialogueWait = false)
        {
            if (npc == null || string.IsNullOrWhiteSpace(actionType))
                return;

            ActionTag tag = ActionTagExtensions.FromTagString(actionType);

            if (tag == ActionTag.None)
            {
                ModEntry.SMonitor?.Log(
                    $"[MovementCoordinator] Unknown action type '{actionType}' for {npc.Name}, ignoring.",
                    LogLevel.Warn);
                return;
            }

            QueueMovement(npc, tag, skipDialogueWait);
        }

        internal void QueueMovement(NPC npc, ActionTag tag, bool skipDialogueWait = false)
        {
            if (npc == null || tag == ActionTag.None)
                return;

            var type = tag switch
            {
                ActionTag.StepForward  => MovementType.Forward,
                ActionTag.StepBackward => MovementType.Backward,
                ActionTag.StepLeft     => MovementType.Left,
                ActionTag.StepRight    => MovementType.Right,
                ActionTag.StepUp       => MovementType.Up,
                ActionTag.StepDown     => MovementType.Down,
                ActionTag.Follow       => MovementType.Follow,
                ActionTag.StopFollow   => MovementType.StopFollow,
                _                      => MovementType.None
            };

            if (type == MovementType.None)
                return;

            QueueMovementInternal(npc, type, skipDialogueWait);
        }

        internal void QueueEmote(NPC npc, int emoteId)
            => PendingQueue.EnqueueEmote(npc, emoteId);

        // ─── Follow ───

        internal void StartDateFollow(NPC npc, int endTime)
        {
            if (npc == null) return;

            GotoTracker.Cancel(npc.Name, invokeFailCallback: false);
            PendingQueue.Cancel(npc.Name);
            StepTracker.Cancel(npc.Name);

            FollowTracker.StartDateFollow(npc, endTime);
        }

        internal void StopDateFollow(NPC npc)
        {
            if (npc == null || FollowTracker.CurrentFollowingNpc != npc)
                return;

            PendingQueue.Cancel(npc.Name);
            StepTracker.Cancel(npc.Name);

            FollowTracker.Unbind(silent: false);

            ModEntry.SMonitor?.Log(
                $"[MovementCoordinator] Date follow stopped: {npc.Name}",
                LogLevel.Debug);
        }

        /// <summary>
        /// Player-initiated stop follow. Returns true if caller should invoke DateManager.EndDateGracefully.
        /// </summary>
        internal bool StopFollow(NPC npc)
        {
            if (npc == null || FollowTracker.CurrentFollowingNpc != npc)
                return false;

            if (FollowTracker.HasActiveDateFollow)
            {
                StopDateFollow(npc);
                return true; // caller handles EndDateGracefully
            }

            PendingQueue.Cancel(npc.Name);
            StepTracker.Cancel(npc.Name);

            FollowTracker.Unbind(silent: false);

            ModEntry.SMonitor?.Log(
                $"[MovementCoordinator] Player stopped follow: {npc.Name}",
                LogLevel.Debug);

            return false;
        }

        internal void StartRegularFollow(NPC npc, int endTime)
        {
            if (npc == null) return;

            GotoTracker.Cancel(npc.Name, invokeFailCallback: false);
            PendingQueue.Cancel(npc.Name);
            StepTracker.Cancel(npc.Name);

            FollowTracker.StartRegularFollow(npc, endTime);
        }

        // ─── Move ───

        internal void MoveToTile(NPC npc, Vector2 targetTile, Action onComplete = null)
            => MoveToTileInternal(npc, targetTile, onComplete, onComplete);

        internal void MoveToTile(NPC npc, Vector2 targetTile, Action onComplete, Action onFail)
            => MoveToTileInternal(npc, targetTile, onComplete, onFail);

        internal void CancelMoveToTile(NPC npc, bool invokeFailCallback = false)
        {
            if (npc == null) return;

            GotoTracker.Cancel(npc.Name, invokeFailCallback);
            StepTracker.Cancel(npc.Name);
            PendingQueue.Cancel(npc.Name);
        }

        // ─── Internal ───

        private void MoveToTileInternal(NPC npc, Vector2 targetTile, Action onSuccess, Action onFail)
        {
            if (npc == null) return;

            if (npc.IsInvisible)
            {
                ModEntry.SMonitor?.Log(
                    $"[MovementCoordinator] {npc.Name} GOTO rejected: invisible (event actor).",
                    LogLevel.Debug);
                onFail?.Invoke();
                return;
            }

            if (Game1.player == null)
            {
                onFail?.Invoke();
                return;
            }

            // Build suspended follow snapshot before canceling old goto
            SuspendedFollowSnapshot? suspended = null;
            if (FollowTracker.HasActiveDateFollow && FollowTracker.CurrentFollowingNpc == npc)
            {
                suspended = FollowTracker.SuspendForGoto();
            }
            else if (FollowTracker.IsFollowing(npc))
            {
                FollowTracker.Unbind(silent: true);
            }

            var loc = npc.currentLocation ?? Game1.player.currentLocation;
            if (loc == null)
            {
                ClearNpcMovement(npc);
                if (suspended != null) FollowTracker.RestoreSuspendedFollow(suspended);
                onFail?.Invoke();
                return;
            }

            GotoTracker.Start(npc, targetTile, onSuccess, onFail, suspended);

            ModEntry.SMonitor?.Log(
                $"[MovementCoordinator] {npc.Name} MoveToTile ({targetTile.X},{targetTile.Y}) delegated to GotoMovementTracker.",
                LogLevel.Debug);
        }

        private void ExecuteMovement(NPC npc, MovementType type)
        {
            if (npc == null) return;

            StepTracker.Cancel(npc.Name);

            if (type == MovementType.Follow)
            {
                if (Game1.player == null) return;

                GotoTracker.Cancel(npc.Name, invokeFailCallback: false);

                FollowTracker.Unbind(silent: true);

                FollowTracker.StartRegularFollow(npc, MovementPathfinding.SafeAddGameTime(Game1.timeOfDay, 60));

                CompanionScheduleManager.Instance.ClearScheduleForOverride("FollowStarted", npc.Name);
                return;
            }

            if (type == MovementType.StopFollow)
            {
                // Delegate to MovementManager.StopFollow via callback (cross-system: DateManager)
                // Since we can't call back up, this path is handled at MovementManager level
                return;
            }

            if (FollowTracker.CurrentFollowingNpc == npc && !FollowTracker.HasActiveDateFollow)
                FollowTracker.Unbind(silent: true);

            GotoTracker.Cancel(npc.Name, invokeFailCallback: false);

            ClearNpcMovement(npc);

            int dx = 0, dy = 0;

            if      (type == MovementType.Left)  dx = -1;
            else if (type == MovementType.Right)  dx =  1;
            else if (type == MovementType.Up)     dy = -1;
            else if (type == MovementType.Down)   dy =  1;
            else
            {
                switch (npc.FacingDirection)
                {
                    case 0: dy = -1; break;
                    case 1: dx =  1; break;
                    case 2: dy =  1; break;
                    case 3: dx = -1; break;
                }

                if (type == MovementType.Backward) { dx = -dx; dy = -dy; }
            }

            var loc = npc.currentLocation;
            if (loc == null || Game1.player == null) return;

            Vector2 pos1 = new Vector2(npc.Tile.X + dx,     npc.Tile.Y + dy);
            Vector2 pos2 = new Vector2(npc.Tile.X + dx * 2, npc.Tile.Y + dy * 2);

            bool pos1Walkable = MovementPathfinding.IsTileWalkable(loc, pos1, npc);
            bool pos2Walkable = MovementPathfinding.IsTileWalkable(loc, pos2, npc);

            Vector2 target;

            if      (pos1Walkable && pos2Walkable) target = pos2;
            else if (pos1Walkable)                 target = pos1;
            else
            {
                npc.faceGeneralDirection(pos1, 0, false, false);
                ModEntry.SMonitor?.Log(
                    "[MovementCoordinator] 单步移动被阻挡，仅转向",
                    LogLevel.Debug);
                return;
            }

            StepTracker.Start(npc, target * 64f, 60);

            ModEntry.SMonitor?.Log(
                $"[MovementCoordinator] {npc.Name} 滑行至 ({target.X},{target.Y})",
                LogLevel.Debug);
        }

        private void QueueMovementInternal(NPC npc, MovementType type, bool skipDialogueWait)
        {
            if (npc == null || type == MovementType.None) return;

            if (!CanQueueMovement(npc, type)) return;

            if (type == MovementType.Follow)
            {
                if (FollowTracker.HasActiveDateFollow)
                {
                    ModEntry.SMonitor?.Log(
                        "[MovementCoordinator] Regular FOLLOW blocked: date follow is active.",
                        LogLevel.Debug);
                    return;
                }

                if (GotoTracker.IsMoving(npc.Name))
                {
                    ModEntry.SMonitor?.Log(
                        $"[MovementCoordinator] GoTo interrupted by Follow for {npc.Name}.",
                        LogLevel.Debug);
                    GotoTracker.Cancel(npc.Name, invokeFailCallback: false);
                }
            }

            if (type == MovementType.StopFollow)
            {
                // Handled at MovementManager level (cross-system: DateManager)
                return;
            }

            PendingQueue.Enqueue(npc, type, skipDialogueWait);
        }

        private bool CanQueueMovement(NPC npc, MovementType type)
        {
            if (Game1.player == null || npc.IsInvisible || npc.currentLocation == null)
            {
                ModEntry.SMonitor?.Log(
                    $"[MovementCoordinator] Action {type} rejected for {npc.Name}: player/NPC state invalid.",
                    LogLevel.Debug);
                return false;
            }

            if (FollowTracker.HasActiveDateFollow && FollowTracker.CurrentFollowingNpc == npc)
            {
                ModEntry.SMonitor?.Log(
                    $"[MovementCoordinator] Action {type} rejected for {npc.Name}: date follow is active.",
                    LogLevel.Debug);
                return false;
            }

            return true;
        }

        private static void ClearNpcMovement(NPC npc)
        {
            if (npc == null) return;

            npc.controller = null;
            npc.addedSpeed = 0;
            npc.Halt();
        }
    }
}
