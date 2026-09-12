using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Pathfinding;

namespace ValleytalkReborn.Movement
{
    internal enum FollowState { Halted, Pathing, Wandering }

    internal struct SuspendedFollowSnapshot
    {
        public NPC Npc;
        public int EndTime;
    }

    internal sealed class FollowMovementTracker
    {
        private readonly GotoMovementTracker _gotoTracker;
        private readonly Action<NPC> _clearNpcMovement;
        private readonly Random _rng;
        private readonly Action<NPC> _onFollowStopped;

        // ─── Follow core state ───
        private NPC _followingNpc;
        private int _followEndTime;
        private bool _isDateFollow;

        // ─── Follow state machine ───
        private FollowState _followState = FollowState.Halted;

        // ─── Path target stabilization ───
        private Vector2 _committedTarget;
        private int _retargetCooldown;
        private const int RETARGET_COOLDOWN = 12;

        // ─── Two-gear speed thresholds ───
        private const float SPRINT_DIST  = 10f;
        private const float NORMAL_DIST  = 4f;
        private const int   SPEED_SPRINT = 3;
        private const int   SPEED_NORMAL = 2;
        private bool _isSprinting = false;

        // ─── State transition distance thresholds ───
        private const float DIST_START_PATH              = 4.5f;
        private const float DIST_STOP_PATH               = 2.5f;
        private const float DIST_ABORT_WANDER            = 6.5f;
        private const float DIST_IDLE_APPROACH           = 2.0f;
        private const float DIST_PLAYER_STOPPED_APPROACH = 3.5f;

        // ─── Startup delay ───
        private int _startDelayTimer = 0;
        private const int START_DELAY_FRAMES = 8;

        // ─── Brake inertia ───
        private bool _isBraking = false;
        private int  _brakeTimer = 0;
        private const int BRAKE_FRAMES = 4;
        private const int BRAKE_SPEED  = 1;

        // ─── Pathfinding failure backoff ───
        private int _followPathFailCount;
        private int _followPathFailCooldown;

        // ─── Idle gaze ───
        private int _idleGazeTimer = 0;
        private const int IDLE_GAZE_INTERVAL = 60;

        // ─── Wander fan bias ───
        private const float WANDER_HALF_SPREAD = 1.047f;

        // ─── Player idle timer ───
        private Vector2 _lastPlayerTile;
        private int _playerIdleTimer;
        private const int IDLE_THRESHOLD = 360;

        // ─── Wander parameters ───
        private int _wanderPathCooldown;
        private const int WANDER_PAUSE      = 180;
        private const int WANDER_RADIUS_MIN = 2;
        private const int WANDER_RADIUS_MAX = 5;

        // ─── Pathing stage displacement stall detection ───
        private Vector2 _lastNpcTileInPathing;
        private int _npcStuckTicks;
        private const int STUCK_TICKS_THRESHOLD = 40;

        // ─── Callbacks ───
        internal Action<NPC> OnFollowStartedCallback { get; set; }
        internal Action<string> OnFollowEndedCallback { get; set; }

        public FollowMovementTracker(
            GotoMovementTracker gotoTracker,
            Action<NPC> clearNpcMovement,
            Random sharedRng,
            Action<NPC> onFollowStopped)
        {
            _gotoTracker       = gotoTracker;
            _clearNpcMovement  = clearNpcMovement;
            _rng               = sharedRng;
            _onFollowStopped   = onFollowStopped;
        }

        // ─── Public API ───

        public bool HasActiveFollow => _followingNpc != null;
        public bool HasActiveDateFollow => _isDateFollow && _followingNpc != null;
        public NPC CurrentFollowingNpc => _followingNpc;
        public bool IsFollowing(NPC npc) => _followingNpc == npc && !_isDateFollow;
        public bool IsDateFollowing(NPC npc) => _followingNpc == npc && _isDateFollow;

        public void StartRegularFollow(NPC npc, int endTime)
        {
            if (npc == null) return;

            if (_followingNpc != null)
                Unbind(silent: true);

            _followingNpc  = npc;
            _followEndTime = endTime;
            _isDateFollow  = false;

            TransitionTo(FollowState.Halted);

            ModEntry.SMonitor?.Log(
                $"[FollowMovementTracker] Regular follow started: {npc.Name}, endTime={endTime}.",
                LogLevel.Debug);
        }

        public void StartDateFollow(NPC npc, int endTime)
        {
            if (npc == null) return;

            Unbind(silent: false);

            _followingNpc  = npc;
            _followEndTime = endTime;
            _isDateFollow  = true;

            TransitionTo(FollowState.Halted);

            OnFollowStartedCallback?.Invoke(npc);

            ModEntry.SMonitor?.Log(
                $"[FollowMovementTracker] Date follow started: {npc.Name}, endTime={endTime}.",
                LogLevel.Debug);
        }

        public void Unbind(bool silent = false, bool suppressScheduleRestore = false)
        {
            if (_followingNpc != null)
            {
                _clearNpcMovement(_followingNpc);

                if (!silent)
                {
                    if (!_isDateFollow && !suppressScheduleRestore)
                    {
                        // Capture npc before nulling _followingNpc; delay 30 frames
                        // to match original StopFollowInternal semantics (MMR-05b Gate 0 fix)
                        var departingNpc = _followingNpc;
                        try
                        {
                            StardewValley.DelayedAction.functionAfterDelay(
                                () => _onFollowStopped?.Invoke(departingNpc), 30);
                        }
                        catch (Exception ex)
                        {
                            ModEntry.SMonitor?.Log(
                                $"[FollowMovementTracker] onFollowStopped callback failed: {ex.Message}",
                                LogLevel.Error);
                        }
                    }

                    OnFollowEndedCallback?.Invoke(_followingNpc.Name);
                }
            }

            _followingNpc       = null;
            _isDateFollow       = false;
            _followEndTime      = 0;
            _followState        = FollowState.Halted;
            _retargetCooldown   = 0;
            _wanderPathCooldown = 0;
            _isSprinting        = false;
            _isBraking          = false;
            _brakeTimer         = 0;
            _startDelayTimer    = 0;
            _playerIdleTimer    = 0;
            _idleGazeTimer      = 0;
            _committedTarget    = Vector2.Zero;
        }

        public void Tick(UpdateTickedEventArgs e)
        {
            if (_followingNpc == null || Game1.player == null)
                return;

            if (_followingNpc != null && _gotoTracker.IsMoving(_followingNpc.Name))
                return;

            if (Game1.activeClickableMenu != null || Game1.dialogueUp)
            {
                if (_followState != FollowState.Halted)
                    TransitionTo(FollowState.Halted);

                _followingNpc.faceGeneralDirection(Game1.player.getStandingPosition(), 0, false, false);
                _playerIdleTimer = 0;
                return;
            }

            if (!_isDateFollow && Game1.timeOfDay >= _followEndTime)
            {
                Unbind();
                return;
            }

            if (Game1.timeOfDay >= 2200)
            {
                Unbind();

                ModEntry.SMonitor?.Log(
                    _isDateFollow
                        ? "[FollowMovementTracker] Night end: date physical follow stopped, farewell flow managed by DateManager."
                        : $"[FollowMovementTracker] Night end: {_followingNpc?.Name} normal follow force-ended at 22:00.",
                    LogLevel.Debug);

                return;
            }

            if (_followingNpc.currentLocation == null) { Unbind(); return; }
            if (Game1.player.currentLocation == null)   return;

            if (_followingNpc.currentLocation != Game1.player.currentLocation){
                var targetLocation = Game1.player.currentLocation;

                var safeTile = MovementPathfinding.FindSafeFollowTile(
                    targetLocation,
                    Game1.player.Tile,
                    _followingNpc);

                if (safeTile.HasValue)
                {
                    Game1.warpCharacter(_followingNpc, targetLocation, safeTile.Value);

                    _clearNpcMovement(_followingNpc);
                    TransitionTo(FollowState.Halted);

                    _lastPlayerTile  = Game1.player.Tile;
                    _playerIdleTimer = 0;

                    ModEntry.SMonitor?.Log(
                        $"[FollowMovementTracker] {_followingNpc.Name} cross-map follow, " +
                        $"landing=({safeTile.Value.X},{safeTile.Value.Y}), " +
                        $"player=({Game1.player.Tile.X},{Game1.player.Tile.Y})",
                        LogLevel.Debug);
                }
                else
                {
                    ModEntry.SMonitor?.Log(
                        $"[FollowMovementTracker] {_followingNpc.Name} cross-map failed: no safe landing near player.",
                        LogLevel.Warn);
                }

                return;
            }

            Vector2 currentPlayerTile = Game1.player.Tile;
            if (currentPlayerTile != _lastPlayerTile)
            {
                _playerIdleTimer = 0;
                _lastPlayerTile  = currentPlayerTile;
            }
            else if (_playerIdleTimer < IDLE_THRESHOLD + 60)
            {
                _playerIdleTimer++;
            }

            float dist = Vector2.Distance(_followingNpc.Tile, Game1.player.Tile);

            if (_followState == FollowState.Pathing)
                UpdateFollowSpeed(dist);

            switch (_followState)
            {
                case FollowState.Halted:    TickHalted(dist);    break;
                case FollowState.Pathing:   TickPathing(dist);   break;
                case FollowState.Wandering: TickWandering(dist); break;
            }

            // Wall-phasing safety: validate NPC position every frame
            if (_followingNpc != null && _followingNpc.currentLocation != null)
            {
                if (!MovementPathfinding.IsTileWalkable(_followingNpc.currentLocation, _followingNpc.Tile, _followingNpc))
                {
                    MovementPathfinding.TryRecoverStartingTile(_followingNpc, _followingNpc.currentLocation);
                    ModEntry.SMonitor?.Log(
                        $"[FollowMovementTracker] {_followingNpc.Name} detected out-of-bounds/wall-phasing, forced recovery.",
                        LogLevel.Warn);
                }
            }
        }

        public SuspendedFollowSnapshot SuspendForGoto()
        {
            if (!_isDateFollow || _followingNpc == null)
                return default;

            var snapshot = new SuspendedFollowSnapshot
            {
                Npc     = _followingNpc,
                EndTime = _followEndTime,
            };
            _clearNpcMovement(_followingNpc);

            return snapshot;
        }

        public void RestoreSuspendedFollow(SuspendedFollowSnapshot? snapshot)
        {
            if (snapshot == null) return;

            var s = snapshot.Value;

            _followingNpc  = s.Npc;
            _followEndTime = s.EndTime;
            _isDateFollow  = true;

            TransitionTo(FollowState.Halted);

            ModEntry.SMonitor?.Log(
                $"[FollowMovementTracker] Date-follow restored for {s.Npc.Name} (until {s.EndTime}).",
                LogLevel.Debug);
        }

        public void ResetAll()
        {
            ModEntry.SMonitor?.Log(
                "[FollowMovementTracker] ResetAll: clearing all follow state.",
                LogLevel.Debug);

            _clearNpcMovement(_followingNpc);

            _followingNpc       = null;
            _isDateFollow       = false;
            _followEndTime      = 0;
            _followState        = FollowState.Halted;
            _retargetCooldown   = 0;
            _wanderPathCooldown = 0;
            _isSprinting        = false;
            _isBraking          = false;
            _brakeTimer         = 0;
            _startDelayTimer    = 0;
            _playerIdleTimer    = 0;
            _idleGazeTimer      = 0;
            _committedTarget    = Vector2.Zero;
            _lastPlayerTile     = Game1.player?.Tile ?? Vector2.Zero;
        }

        // ─── Private helpers ───

        private void UpdateFollowSpeed(float dist)
        {
            if (_followingNpc == null) return;

            if (!_isSprinting && dist > SPRINT_DIST)
            {
                _isSprinting             = true;
                _followingNpc.addedSpeed = SPEED_SPRINT;
            }
            else if (_isSprinting && dist < NORMAL_DIST)
            {
                _isSprinting             = false;
                _followingNpc.addedSpeed = SPEED_NORMAL;
            }
        }

        private void TickHalted(float dist)
        {
            if (_followingNpc == null || Game1.player == null) return;

            if (dist > DIST_START_PATH)
            {
                if (_startDelayTimer < START_DELAY_FRAMES)
                {
                    _startDelayTimer++;
                    _followingNpc.faceGeneralDirection(Game1.player.getStandingPosition(), 0, false, false);
                    return;
                }

                _startDelayTimer = 0;
                TransitionTo(FollowState.Pathing);
                return;
            }

            _startDelayTimer = 0;

            if (_isDateFollow)
            {
                if (_playerIdleTimer >= IDLE_THRESHOLD / 2 && dist > DIST_IDLE_APPROACH)
                {
                    TransitionTo(FollowState.Pathing);
                    return;
                }

                _followingNpc.faceGeneralDirection(Game1.player.getStandingPosition(), 0, false, false);
                return;
            }

            if (_playerIdleTimer >= IDLE_THRESHOLD / 3 && dist > DIST_PLAYER_STOPPED_APPROACH)
            {
                TransitionTo(FollowState.Pathing);
                return;
            }

            if (_playerIdleTimer >= IDLE_THRESHOLD)
            {
                if (dist > DIST_IDLE_APPROACH)
                {
                    TransitionTo(FollowState.Pathing);
                    return;
                }

                TransitionTo(FollowState.Wandering);
                return;
            }

            if (_playerIdleTimer >= IDLE_THRESHOLD / 2)
            {
                _idleGazeTimer++;

                if (_idleGazeTimer >= IDLE_GAZE_INTERVAL)
                {
                    _idleGazeTimer = 0;
                    _followingNpc.faceDirection(_rng.Next(4));
                }

                return;
            }

            _idleGazeTimer = 0;
            _followingNpc.faceGeneralDirection(Game1.player.getStandingPosition(), 0, false, false);
        }

        private void TickPathing(float dist)
        {
            if (_followingNpc == null || Game1.player == null) return;

            if (_followPathFailCooldown > 0)
            {
                _followPathFailCooldown--;
                return;
            }

            if (_isBraking)
            {
                if (dist > DIST_START_PATH)
                {
                    _isBraking  = false;
                    _brakeTimer = 0;
                    return;
                }

                _brakeTimer--;
                _followingNpc.addedSpeed = BRAKE_SPEED;

                if (_brakeTimer <= 0)
                {
                    _isBraking = false;
                    TransitionTo(FollowState.Halted);
                }

                return;
            }

            float stopDist = (_playerIdleTimer >= IDLE_THRESHOLD) ? 2.0f : DIST_STOP_PATH;
            if (dist <= stopDist)
            {
                _isBraking               = true;
                _brakeTimer              = BRAKE_FRAMES;
                _followingNpc.addedSpeed = BRAKE_SPEED;
                return;
            }

            // Stall detection
            if (Vector2.Distance(_followingNpc.Tile, _lastNpcTileInPathing) < 0.1f)
                _npcStuckTicks++;
            else
            {
                _npcStuckTicks        = 0;
                _lastNpcTileInPathing = _followingNpc.Tile;
            }

            bool forceRetargetDueToStuck = _npcStuckTicks >= STUCK_TICKS_THRESHOLD;

            if (_retargetCooldown > 0 && !forceRetargetDueToStuck) { _retargetCooldown--; return; }

            bool needRetarget =
                forceRetargetDueToStuck ||
                _followingNpc.controller == null ||
                MovementPathfinding.IsPathDead(_followingNpc.controller) ||
                MovementPathfinding.IsPathDone(_followingNpc.controller) ||
                (Vector2.Distance(_committedTarget, Game1.player.Tile) > 1.5f && dist > DIST_START_PATH);

            if (needRetarget)
            {
                if (forceRetargetDueToStuck)
                {
                    MovementPathfinding.TryRecoverStartingTile(_followingNpc, _followingNpc.currentLocation);
                    _npcStuckTicks = 0;
                    ModEntry.SMonitor?.Log(
                        $"[FollowMovementTracker] {_followingNpc.Name} stuck detected ({STUCK_TICKS_THRESHOLD} ticks no displacement), forced re-path.",
                        LogLevel.Warn);
                }

                var target        = GetSmartFollowTarget();
                _committedTarget = target;

                bool pathCreated = SetFollowPath(target);
                _retargetCooldown = pathCreated
                    ? RETARGET_COOLDOWN
                    : 60;

                ModEntry.SMonitor?.Log(
                    $"[FollowMovementTracker] {_followingNpc.Name} retarget → ({target.X},{target.Y}), dist={dist:F1}",
                    LogLevel.Debug);
            }
        }

        private void TickWandering(float dist)
        {
            if (_followingNpc == null || Game1.player == null) return;

            if (dist > DIST_ABORT_WANDER || _playerIdleTimer < IDLE_THRESHOLD / 3)
            {
                TransitionTo(FollowState.Pathing);
                return;
            }

            if (_followingNpc.controller != null && !MovementPathfinding.IsPathDone(_followingNpc.controller))
                return;

            if (_wanderPathCooldown > 0) { _wanderPathCooldown--; return; }

            TrySetWanderPath();
        }

        private void TransitionTo(FollowState next)
        {
            if (_followState == next) return;

            if (_followingNpc == null) { _followState = next; return; }

            ModEntry.SMonitor?.Log(
                $"[FollowMovementTracker] {_followingNpc.Name} {_followState} → {next}",
                LogLevel.Debug);

            switch (_followState)
            {
                case FollowState.Pathing:
                case FollowState.Wandering:
                    _clearNpcMovement(_followingNpc);
                    break;
            }

            _followState = next;

            switch (next)
            {
                case FollowState.Halted:
                    _isSprinting             = false;
                    _followingNpc.addedSpeed = 0;
                    _retargetCooldown        = 0;
                    _isBraking               = false;
                    _brakeTimer              = 0;
                    _startDelayTimer         = 0;

                    if (Game1.player != null)
                        _followingNpc.faceGeneralDirection(Game1.player.getStandingPosition(), 0, false, false);
                    break;

                case FollowState.Pathing:
                    _isSprinting             = false;
                    _followingNpc.addedSpeed = SPEED_NORMAL;
                    _lastNpcTileInPathing    = _followingNpc.Tile;
                    _npcStuckTicks           = 0;
                    _retargetCooldown        = 0;
                    _wanderPathCooldown      = 0;
                    _idleGazeTimer           = 0;
                    _startDelayTimer         = 0;
                    _isBraking               = false;
                    break;

                case FollowState.Wandering:
                    _isSprinting             = false;
                    _followingNpc.addedSpeed = 0;
                    _retargetCooldown        = 0;
                    _wanderPathCooldown      = 0;
                    _idleGazeTimer           = 0;
                    _startDelayTimer         = 0;

                    ModEntry.SMonitor?.Log(
                        $"[FollowMovementTracker] {_followingNpc.Name} started wandering",
                        LogLevel.Debug);
                    break;
            }
        }

        private Vector2 GetSmartFollowTarget()
        {
            var player = Game1.player;
            if (player == null || _followingNpc == null) return Vector2.Zero;

            var loc = player.currentLocation;
            if (loc == null) return player.Tile;

            int behindDx = 0, behindDy = 0;
            switch (player.FacingDirection)
            {
                case 0: behindDy =  1; break;
                case 1: behindDx = -1; break;
                case 2: behindDy = -1; break;
                case 3: behindDx =  1; break;
            }

            var candidates = new List<(Vector2 tile, float cost)>();

            void AddCandidate(Vector2 t, float bonus = 0f)
            {
                if (MovementPathfinding.IsTileWalkable(loc, t, _followingNpc))
                    candidates.Add((t, Vector2.Distance(_followingNpc.Tile, t) - bonus));
            }

            AddCandidate(new Vector2(player.Tile.X + behindDx,     player.Tile.Y + behindDy),     1.5f);
            AddCandidate(new Vector2(player.Tile.X + behindDx * 2, player.Tile.Y + behindDy * 2), 1.0f);
            AddCandidate(new Vector2(player.Tile.X + 1,  player.Tile.Y));
            AddCandidate(new Vector2(player.Tile.X - 1,  player.Tile.Y));
            AddCandidate(new Vector2(player.Tile.X,      player.Tile.Y + 1));
            AddCandidate(new Vector2(player.Tile.X,      player.Tile.Y - 1));
            AddCandidate(new Vector2(player.Tile.X + 1,  player.Tile.Y + 1));
            AddCandidate(new Vector2(player.Tile.X - 1,  player.Tile.Y - 1));
            AddCandidate(new Vector2(player.Tile.X + 1,  player.Tile.Y - 1));
            AddCandidate(new Vector2(player.Tile.X - 1,  player.Tile.Y + 1));

            if (candidates.Count == 0)
            {
                for (int dy = -2; dy <= 2; dy++)
                for (int dx = -2; dx <= 2; dx++)
                {
                    if (dx == 0 && dy == 0) continue;
                    var fallback = new Vector2(player.Tile.X + dx, player.Tile.Y + dy);
                    if (MovementPathfinding.IsTileWalkable(loc, fallback, _followingNpc))
                        return fallback;
                }

                return new Vector2(player.Tile.X + 1, player.Tile.Y + 1);
            }

            candidates.Sort((a, b) => a.cost.CompareTo(b.cost));
            return candidates[0].tile;
        }

        private bool SetFollowPath(Vector2 target)
        {
            if (_followingNpc == null) return false;

            var loc = _followingNpc.currentLocation ?? Game1.player?.currentLocation;
            if (loc == null) return false;

            // Recover starting tile if it is blocked before creating a new path.
            MovementPathfinding.TryRecoverStartingTile(_followingNpc, loc);

            if (MovementPathfinding.TryCreatePath(_followingNpc, loc, target, out var controller, out _))
            {
                _followingNpc.controller = controller;
                _followPathFailCount = 0;
                return true;
            }

            _followPathFailCount++;
            _followPathFailCooldown = Math.Min(120, 30 * _followPathFailCount);

            _clearNpcMovement(_followingNpc);

            ModEntry.SMonitor?.Log(
                $"[FollowMovementTracker] {_followingNpc.Name} pathfinding failed, " +
                $"target=({target.X},{target.Y}), " +
                $"attempt={_followPathFailCount}, " +
                $"cooldown={_followPathFailCooldown}",
                LogLevel.Debug);

            return false;
        }

        private void TrySetWanderPath()
        {
            if (_followingNpc == null || Game1.player == null) return;

            var loc = _followingNpc.currentLocation ?? Game1.player.currentLocation;
            if (loc == null) return;

            Vector2 center = Game1.player.Tile;

            float behindAngle = Game1.player.FacingDirection switch
            {
                0 =>  (float)(Math.PI / 2),
                1 =>  (float)Math.PI,
                2 => -(float)(Math.PI / 2),
                3 =>  0f,
                _ =>  0f
            };

            for (int attempt = 0; attempt < 20; attempt++)
            {
                float angle;

                if (_rng.NextDouble() < 0.70)
                {
                    float offset = (float)((_rng.NextDouble() * 2 - 1) * WANDER_HALF_SPREAD);
                    angle = behindAngle + offset;
                }
                else
                {
                    angle = (float)(_rng.NextDouble() * Math.PI * 2);
                }

                float radius = WANDER_RADIUS_MIN + (float)_rng.NextDouble() * (WANDER_RADIUS_MAX - WANDER_RADIUS_MIN);

                var target = new Vector2(
                    center.X + (int)Math.Round(Math.Cos(angle) * radius),
                    center.Y + (int)Math.Round(Math.Sin(angle) * radius));

                if (!MovementPathfinding.IsTileWalkable(loc, target, _followingNpc))
                    continue;

                if (MovementPathfinding.TryCreatePath(_followingNpc, loc, target, out var controller, out _))
                {
                    _followingNpc.controller = controller;
                    _wanderPathCooldown      = WANDER_PAUSE;

                    ModEntry.SMonitor?.Log(
                        $"[FollowMovementTracker] wander target ({target.X},{target.Y})",
                        LogLevel.Debug);

                    return;
                }
            }

            _followingNpc.faceDirection(_rng.Next(4));
            _wanderPathCooldown = 60;
        }
    }
}
