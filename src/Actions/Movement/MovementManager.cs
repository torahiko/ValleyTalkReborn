using System;
using System.Collections.Generic;
using System.Reflection;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Menus;
using StardewValley.Pathfinding;

namespace ValleytalkReborn
{
    public enum MovementType
    {
        None,
        Forward,
        Backward,
        Left,
        Right,
        Up,
        Down,
        Follow,
        StopFollow
    }

    public class MovementManager
    {
        // ═══════════════════════════════════════════════
        //  GoTo 上下文（含挂起的 DateFollow 快照）
        // ═══════════════════════════════════════════════

        private sealed class GotoContext
        {
            public NPC Npc;
            public Action OnSuccess;
            public Action OnFail;
            public Vector2 Target;
            public GameLocation ExpectedLocation;
            public Vector2 LastTile;
            public int StuckTicks;
            public int RepathAttempts;
            public readonly GotoTimeoutCounter Timeout = new GotoTimeoutCounter(GOTO_TIMEOUT_TICKS);
            public SuspendedFollowSnapshot? SuspendedFollow;
        }

        private struct SuspendedFollowSnapshot
        {
            public NPC Npc;
            public int EndTime;
        }

        // ═══════════════════════════════════════════════
        //  Step 上下文（单步平滑插值位移）
        // ═══════════════════════════════════════════════

        private sealed class StepContext
        {
            public NPC Npc;
            public Vector2 TargetPixel;
            public int Timeout;
        }

        // ═══════════════════════════════════════════════
        //  Pending 上下文（对话框等待 + 动作队列）
        // ═══════════════════════════════════════════════

        private enum PendingPhase
        {
            /// <summary>等待对话框出现（刚入队时）</summary>
            WaitingDialogueOpen,
            /// <summary>对话框已出现，正在倒计时等待关闭</summary>
            WaitingDialogueClose,
            /// <summary>倒计时归零，正在轮询确认菜单已关闭</summary>
            WaitingMenuGone,
        }

        private sealed class PendingContext
        {
            public NPC Npc;
            public MovementType Movement;

            // 表情可以和移动动作共存
            public NPC EmoteNpc;
            public int EmoteId = -1;

            public PendingPhase Phase;
            public int CountdownTimer;       // WaitingDialogueClose 阶段的倒计时
            public int DialogueOpenWaitTicks; // WaitingDialogueOpen 阶段的等待计数
            public int MenuCloseRetry;        // WaitingMenuGone 阶段的重试计数
        }

        private const int DIALOGUE_OPEN_WAIT_MAX = 120;
        private const int MENU_CLOSE_RETRY_MAX   = 60;
        private const int PENDING_COUNTDOWN      = 150; // 原 _autoCloseTimer 初始值

        // ───────────────────────────────────────────────

        public static readonly MovementManager Instance = new();

    /// <summary>
    /// 跟随开始时的外部回调（由 DialogueCoordinator 绑定到 AmbientBarkModule.ResetForFollow）。
    /// </summary>
    internal Action<NPC> OnFollowStartedCallback { get; set; }

    /// <summary>
    /// 跟随结束时的外部回调（由 DialogueCoordinator 绑定到 AmbientBarkModule.ResetForNpc）。
    /// </summary>
    internal Action<string> OnFollowEndedCallback { get; set; }

        private static readonly Random _rng = new Random();

        /// <summary>是否有 NPC 正在约会跟随。</summary>
        public bool HasActiveDateFollow => _isDateFollow && _followingNpc != null;

        /// <summary>是否有任何 NPC 正在跟随，包括普通跟随和约会跟随。</summary>
        public bool HasActiveFollow => _followingNpc != null;

        /// <summary>当前正在跟随的 NPC，可能为 null。</summary>
        public NPC CurrentFollowingNpc => _followingNpc;

        /// <summary>当前正在 GoTo 的 NPC，可能为 null。</summary>
        public NPC CurrentGotoNpc => _goto?.Npc;

        /// <summary>是否有 NPC 正在执行单步滑行。</summary>
        public bool IsStepActive => _step != null;

        public bool IsFollowing(NPC npc) => _followingNpc == npc && !_isDateFollow;

        // ─── Pending 上下文 ───
        private PendingContext _pending;

        // ─── 单步平滑插值位移状态 ───
        private StepContext _step;

        // ─── 跟随核心状态 ───
        private NPC _followingNpc;
        private int _followEndTime;
        private bool _isDateFollow;

        // ─── 跟随状态机 ───
        private enum FollowState { Halted, Pathing, Wandering }
        private FollowState _followState = FollowState.Halted;

        // ─── 路径目标稳定化 ───
        private Vector2 _committedTarget;
        private int _retargetCooldown;
        private const int RETARGET_COOLDOWN = 12;

        // ─── 两挡变速阈值 ───
        private const float SPRINT_DIST  = 10f;
        private const float NORMAL_DIST  = 4f;
        private const int   SPEED_SPRINT = 3;
        private const int   SPEED_NORMAL = 2;
        private bool _isSprinting = false;

        // ─── 状态转换距离阈值 ───
        private const float DIST_START_PATH              = 4.5f;
        private const float DIST_STOP_PATH               = 2.5f;
        private const float DIST_ABORT_WANDER            = 6.5f;
        private const float DIST_IDLE_APPROACH           = 2.0f;
        private const float DIST_PLAYER_STOPPED_APPROACH = 3.5f;

        // ─── 启动延迟 ───
        private int _startDelayTimer = 0;
        private const int START_DELAY_FRAMES = 8;

        // ─── 刹车惯性 ───
        private bool _isBraking = false;
        private int  _brakeTimer = 0;
        private const int BRAKE_FRAMES = 4;
        private const int BRAKE_SPEED  = 1;

        // ─── 寻路失败退避 ───
        private int _followPathFailCount;
        private int _followPathFailCooldown;

        // ─── 空闲视觉游走 ───
        private int _idleGazeTimer = 0;
        private const int IDLE_GAZE_INTERVAL = 60;

        // ─── 闲逛扇形偏向 ───
        private const float WANDER_HALF_SPREAD = 1.047f;

        // ─── 玩家静止计时 ───
        private Vector2 _lastPlayerTile;
        private int _playerIdleTimer;
        private const int IDLE_THRESHOLD = 360;

        // ─── 闲逛参数 ───
        private int _wanderPathCooldown;
        private const int WANDER_PAUSE      = 180;
        private const int WANDER_RADIUS_MIN = 2;
        private const int WANDER_RADIUS_MAX = 5;

        // ─── Pathing 阶段位移停滞检测 ───
        private Vector2 _lastNpcTileInPathing;
        private int _npcStuckTicks;
        private const int STUCK_TICKS_THRESHOLD = 40;

        // ─── GoTo pathfinding state ───
        private const int GOTO_TIMEOUT_TICKS = 45 * 60; 
        private GotoContext _goto;

        /// <summary>GoTo 是否活跃。</summary>
        public bool IsMoving => _goto != null;

        // ─── Schedule reflection cache ───
        private static bool      _scheduleReflectionCached;
        private static MethodInfo _getScheduleMethod;
        private static FieldInfo  _scheduleField;
        private MovementManager()
        {
            _lastPlayerTile = Game1.player?.Tile ?? Vector2.Zero;
        }


        /// <summary>由 ModEntry.Entry() 显式调用，注册所有事件。</summary>
        public void Initialize(IModHelper helper)
        {
            helper.Events.GameLoop.UpdateTicked += OnUpdateTicked;
            helper.Events.GameLoop.DayStarted   += OnDayStarted;
            helper.Events.GameLoop.DayEnding    += OnDayEnding;
            helper.Events.GameLoop.SaveLoaded   += OnSaveLoaded;
        }

        /// <summary>由 ModEntry 在退出时调用，取消所有事件订阅。</summary>
        public void Cleanup(IModHelper helper)
        {
            helper.Events.GameLoop.UpdateTicked -= OnUpdateTicked;
            helper.Events.GameLoop.DayStarted   -= OnDayStarted;
            helper.Events.GameLoop.DayEnding    -= OnDayEnding;
            helper.Events.GameLoop.SaveLoaded   -= OnSaveLoaded;
        }

        //  公开 API
        // ═══════════════════════════════════════════════
        public bool IsNpcMoving(NPC npc) => (_goto?.Npc == npc) || _step?.Npc == npc;

        public void QueueMovement(NPC npc, string actionType, bool skipDialogueWait = false)
        {
            if (npc == null || string.IsNullOrWhiteSpace(actionType))
                return;

            // Use the structured parser to strip brackets, ACTION: prefix, etc.
            ActionTag tag = ActionTagExtensions.FromTagString(actionType);

            if (tag == ActionTag.None)
            {
                ModEntry.SMonitor?.Log(
                    $"[MovementManager] Unknown action type '{actionType}' for {npc.Name}, ignoring.",
                    LogLevel.Warn);
                return;
            }

            // Delegate to the ActionTag-based overload
            QueueMovement(npc, tag, skipDialogueWait);
        }

        public void QueueMovement(NPC npc, ActionTag tag, bool skipDialogueWait = false)
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

        public void QueueEmote(NPC npc, int emoteId)
        {
            if (npc == null || emoteId == -1)
                return;

            if (npc.IsInvisible || npc.currentLocation == null)
                return;


            if (_pending == null)
            {
                // 没有移动动作在等待，单独为 Emote 创建一个 pending
                _pending = new PendingContext
                {
                    Npc      = npc,
                    Movement = MovementType.None,
                    EmoteNpc = npc,
                    EmoteId  = emoteId,
                    Phase    = PendingPhase.WaitingDialogueOpen,
                    CountdownTimer = PENDING_COUNTDOWN,
                };
            }
            else
            {
                // 有移动动作正在等待，附加 Emote 到现有 pending
                _pending.EmoteNpc = npc;
                _pending.EmoteId  = emoteId;
            }
        }

        public void StartDateFollow(NPC npc, int endTime)
        {
            if (npc == null)
                return;


            if (_goto?.Npc == npc)
                EndGoto(success: false, invokeCallbacks: false, facePlayer: false);

            ClearPendingAction();
            FinishStep();
            StopFollowInternal(silent: false);

            _followingNpc  = npc;
            _followEndTime = endTime;
            _isDateFollow  = true;

            TransitionTo(FollowState.Halted);

            OnFollowStartedCallback?.Invoke(npc);

            ModEntry.SMonitor?.Log(
                $"[MovementManager] 约会跟随开始：{npc.Name}，结束时间 {endTime}",
                LogLevel.Debug);
        }

        public void StopDateFollow(NPC npc)
        {
            if (npc == null || _followingNpc != npc)
                return;

            ClearPendingAction();
            FinishStep();
            StopFollowInternal(silent: false);

            ModEntry.SMonitor?.Log(
                $"[MovementManager] 约会跟随停止：{npc.Name}",
                LogLevel.Debug);
        }

        /// <summary>
        /// 玩家主动取消普通跟随（非约会跟随）。
        /// 若当前是约会跟随，调用 StopDateFollow。
        /// </summary>
        public void StopFollow(NPC npc)
        {
            if (npc == null || _followingNpc != npc)
                return;

            if (_isDateFollow)
            {
                StopDateFollow(npc);
                DateManager.Instance.EndDateGracefully(npc.Name, "Player_Stopped_Follow");
                return;
            }

            ClearPendingAction();
            FinishStep();
            StopFollowInternal(silent: false);

            ModEntry.SMonitor?.Log(
                $"[MovementManager] 玩家主动取消跟随：{npc.Name}",
                LogLevel.Debug);
        }

        /// <summary>兼容旧调用：失败时也会调用 onComplete。</summary>
        public void MoveToTile(NPC npc, Vector2 targetTile, Action onComplete = null)
            => MoveToTileInternal(npc, targetTile, onComplete, onComplete);

        /// <summary>成功/失败分开回调。</summary>
        public void MoveToTile(NPC npc, Vector2 targetTile, Action onComplete, Action onFail)
            => MoveToTileInternal(npc, targetTile, onComplete, onFail);

        /// <summary>取消指定 NPC 当前的 MoveToTile / step / pending 动作。</summary>
        public void CancelMoveToTile(NPC npc, bool invokeFailCallback = false)
        {
            if (npc == null)
                return;

            if (_goto?.Npc == npc)
                EndGoto(success: false, invokeCallbacks: invokeFailCallback, facePlayer: false);

            if (_step?.Npc == npc)
                CancelStep();

            if (_pending?.Npc == npc)
                ClearPendingAction();
        }

        /// <summary>
        /// 由 CSM 直接调用，启动普通跟随（非约会跟随），截止指定时间。
        /// </summary>
        public void StartRegularFollow(NPC npc, int endTime)
        {
            if (npc == null) return;

            if (_goto?.Npc == npc)
                EndGoto(success: false, invokeCallbacks: false, facePlayer: false);

            ClearPendingAction();
            FinishStep();

            if (_followingNpc != null)
                StopFollowInternal(silent: true);

            _followingNpc  = npc;
            _followEndTime = endTime;
            _isDateFollow  = false;

            TransitionTo(FollowState.Halted);

            ModEntry.SMonitor?.Log(
                $"[MovementManager] Regular follow started: {npc.Name}, endTime={endTime}.",
                LogLevel.Debug);
        }

        private void CancelStep()
        {
            if (_step == null) return;

            var npc = _step.Npc;
            _step   = null;
            ClearNpcMovement(npc);
        }

        // ═══════════════════════════════════════════════
        //  Update 主循环
        // ═══════════════════════════════════════════════
        private void OnUpdateTicked(object sender, UpdateTickedEventArgs e)
        {
            if (!Context.IsWorldReady || Game1.player == null)
                return;

            HandlePendingMovement();
            HandleStepInterpolation();
            HandleFollow(e);
            HandleGotoCompletion();
        }

        // ═══════════════════════════════════════════════
        //  Pending 状态机
        // ═══════════════════════════════════════════════
        private void HandlePendingMovement()
        {
            if (_pending == null)
                return;

            switch (_pending.Phase)
            {
                case PendingPhase.WaitingDialogueOpen:
                    TickWaitingDialogueOpen();
                    break;

                case PendingPhase.WaitingDialogueClose:
                    TickWaitingDialogueClose();
                    break;

                case PendingPhase.WaitingMenuGone:
                    TickWaitingMenuGone();
                    break;
            }
        }

        private void TickWaitingDialogueOpen()
        {
            if (Game1.activeClickableMenu != null || Game1.dialogueUp)
            {
                // 对话框已出现，进入倒计时阶段
                _pending.Phase = PendingPhase.WaitingDialogueClose;
                return;
            }

            _pending.DialogueOpenWaitTicks++;

            if (_pending.DialogueOpenWaitTicks >= DIALOGUE_OPEN_WAIT_MAX)
            {
                ModEntry.SMonitor?.Log(
                    "[MovementManager] Dialogue open wait timed out, proceeding without dialogue.",
                    LogLevel.Debug);

                // 超时：跳过等待，直接进入执行阶段
                _pending.Phase         = PendingPhase.WaitingMenuGone;
                _pending.CountdownTimer = 0;
            }
        }

        private void TickWaitingDialogueClose()
        {
            if (_pending.CountdownTimer > 0)
            {
                _pending.CountdownTimer--;
                return;
            }

            // 倒计时归零，进入轮询阶段
            _pending.Phase = PendingPhase.WaitingMenuGone;
        }

        private void TickWaitingMenuGone()
        {
            if (Game1.activeClickableMenu != null || Game1.dialogueUp)
            {
                _pending.MenuCloseRetry++;

                if (_pending.MenuCloseRetry > MENU_CLOSE_RETRY_MAX)
                {
                    ModEntry.SMonitor?.Log(
                        "[MovementManager] Pending action aborted: menu/dialogue could not be closed.",
                        LogLevel.Warn);

                    ClearPendingAction();
                    return;
                }

                // 只关闭对话框，避免误关其他 UI
                if (Game1.activeClickableMenu is DialogueBox)
                {
                    try
                    {
                        Game1.activeClickableMenu.exitThisMenu(false);
                        Game1.dialogueUp = false;
                        Game1.player?.forceCanMove();
                    }
                    catch (Exception ex)
                    {
                        ModEntry.SMonitor?.Log(
                            $"[MovementManager] exitThisMenu error: {ex.Message}",
                            LogLevel.Warn);
                    }
                }else if (Game1.dialogueUp && Game1.activeClickableMenu == null)
                {
                    // dialogueUp 残留但没有实际菜单时，温和清理
                    Game1.dialogueUp = false;
                    Game1.player?.forceCanMove();
                }

                return;
            }

            // 菜单已关闭，执行动作
            _pending.MenuCloseRetry = 0;
            FirePendingAction();
        }

        private void FirePendingAction()
        {
            // 先取出，再清空，避免执行期间触发递归
            var ctx = _pending;
            _pending = null;

            // Emote 先触发
            if (ctx.EmoteNpc != null && ctx.EmoteId != -1)
            {
                var emoteNpc = ctx.EmoteNpc;
                int emoteId  = ctx.EmoteId;

                StardewValley.DelayedAction.functionAfterDelay(() =>
                {
                    try   { emoteNpc?.doEmote(emoteId); }
                    catch (Exception ex)
                    {
                        ModEntry.SMonitor?.Log(
                            $"[MovementManager] doEmote error: {ex.Message}",
                            LogLevel.Warn);
                    }
                }, 1);
            }

            // 移动动作后触发
            if (ctx.Movement != MovementType.None && ctx.Npc != null)
                ExecuteMovement(ctx.Npc, ctx.Movement);
        }

        // ═══════════════════════════════════════════════
        //  Step 插值
        // ═══════════════════════════════════════════════
        private void HandleStepInterpolation()
        {
            if (_step == null)
                return;

            if (Game1.player == null || _step.Npc.currentLocation != Game1.player.currentLocation){
                FinishStep();
                return;
            }

            Vector2 diff       = _step.TargetPixel - _step.Npc.Position;
            float   distToStep = diff.Length();

            _step.Timeout--;

            if (distToStep <= 2f || _step.Timeout <= 0)
            {
                var npc    = _step.Npc;
                var target = _step.TargetPixel;
                _step      = null;
                npc.Position = target;

                ClearNpcMovement(npc);
            }
            else
            {
                float speed = Math.Clamp(distToStep * 0.25f, 1f, 4f);

                diff.Normalize();
                _step.Npc.Position += diff * speed;

                if (Math.Abs(diff.X) > Math.Abs(diff.Y))_step.Npc.FacingDirection = diff.X > 0 ? 1 : 3;
                else
                    _step.Npc.FacingDirection = diff.Y > 0 ? 2 : 0;

                if (Game1.currentGameTime != null)
                    _step.Npc.animateInFacingDirection(Game1.currentGameTime);
            }
        }

        // ═══════════════════════════════════════════════
        //  跟随状态机核心
        // ═══════════════════════════════════════════════
        private void HandleFollow(UpdateTickedEventArgs e)
        {
            if (_followingNpc == null || Game1.player == null)
                return;

            if (IsMoving && _goto?.Npc == _followingNpc)
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
                StopFollowInternal();
                return;
            }

            if (Game1.timeOfDay >= 2200)
            {
                StopFollowInternal();

                ModEntry.SMonitor?.Log(
                    _isDateFollow
                        ? "[MovementManager] 夜消：date physical follow stopped, farewell flow managed by DateManager."
                        : $"[MovementManager] 夜消：{_followingNpc?.Name} normal follow force-ended at 22:00.",
                    LogLevel.Debug);

                return;
            }

            if (_followingNpc.currentLocation == null) { StopFollowInternal(); return; }
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

                    ClearNpcMovement(_followingNpc);
                    TransitionTo(FollowState.Halted);

                    _lastPlayerTile  = Game1.player.Tile;
                    _playerIdleTimer = 0;

                    ModEntry.SMonitor?.Log(
                        $"[MovementManager] {_followingNpc.Name} 跨地图跟随玩家，" +
                        $"落点=({safeTile.Value.X},{safeTile.Value.Y})，" +
                        $"玩家=({Game1.player.Tile.X},{Game1.player.Tile.Y})",
                        LogLevel.Debug);
                }
                else
                {
                    ModEntry.SMonitor?.Log(
                        $"[MovementManager] {_followingNpc.Name} 跨地图失败：玩家附近无安全落点。",
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

            // 穿墙保险：每帧校验 NPC 当前位置是否仍在合法区域内
            if (_followingNpc != null && _followingNpc.currentLocation != null)
            {
                if (!MovementPathfinding.IsTileWalkable(_followingNpc.currentLocation, _followingNpc.Tile, _followingNpc))
                {
                    MovementPathfinding.TryRecoverStartingTile(_followingNpc, _followingNpc.currentLocation);
                    ModEntry.SMonitor?.Log(
                        $"[MovementManager] {_followingNpc.Name} 检测到越界/穿墙，已强制恢复。",
                        LogLevel.Warn);
                }
            }
        }

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
            }}

        private void TickHalted(float dist)
        {
            if (_followingNpc == null || Game1.player == null) return;

            if (dist > DIST_START_PATH)
            {
                if (_startDelayTimer < START_DELAY_FRAMES)
                {
                    _startDelayTimer++;
                    _followingNpc.faceGeneralDirection(Game1.player.getStandingPosition(), 0, false, false);return;
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

            // ★ 位移停滞检测
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
                        $"[MovementManager] {_followingNpc.Name} 检测到卡死（{STUCK_TICKS_THRESHOLD}帧无位移），强制重新寻路。",
                        LogLevel.Warn);
                }

                var target        = GetSmartFollowTarget();
                _committedTarget = target;

                bool pathCreated = SetFollowPath(target);
                _retargetCooldown = pathCreated
                    ? RETARGET_COOLDOWN
                    : 60;

                ModEntry.SMonitor?.Log(
                    $"[MovementManager] {_followingNpc.Name} retarget → ({target.X},{target.Y}), dist={dist:F1}",
                    LogLevel.Debug);
            }}

        private void TickWandering(float dist)
        {
            if (_followingNpc == null || Game1.player == null) return;

            if (dist > DIST_ABORT_WANDER || _playerIdleTimer < IDLE_THRESHOLD / 3)
            {
                TransitionTo(FollowState.Pathing);
                return;
            }

            if (_followingNpc.controller != null && !MovementPathfinding.IsPathDone(_followingNpc.controller))return;

            if (_wanderPathCooldown > 0) { _wanderPathCooldown--; return; }

            TrySetWanderPath();
        }

        private void TransitionTo(FollowState next)
        {
            if (_followState == next) return;

            if (_followingNpc == null) { _followState = next; return; }

            ModEntry.SMonitor?.Log(
                $"[MovementManager] {_followingNpc.Name} {_followState} → {next}",
                LogLevel.Debug);

            switch (_followState)
            {
                case FollowState.Pathing:
                case FollowState.Wandering:
                    ClearNpcMovement(_followingNpc);
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
                    _lastNpcTileInPathing    = _followingNpc.Tile;
                    _npcStuckTicks           = 0;
                    break;

                case FollowState.Wandering:
                    _isSprinting             = false;
                    _followingNpc.addedSpeed = 0;
                    _retargetCooldown        = 0;
                    _wanderPathCooldown      = 0;
                    _idleGazeTimer           = 0;
                    _startDelayTimer         = 0;

                    ModEntry.SMonitor?.Log(
                        $"[MovementManager] {_followingNpc.Name} 开始闲逛",
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

            ClearNpcMovement(_followingNpc);

            ModEntry.SMonitor?.Log(
                $"[MovementManager] {_followingNpc.Name} 寻路失败，" +
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
                        $"[MovementManager] 闲逛目标 ({target.X},{target.Y})",
                        LogLevel.Debug);

                    return;
                }
            }

            _followingNpc.faceDirection(_rng.Next(4));_wanderPathCooldown = 60;
        }

        // ═══════════════════════════════════════════════
        //  ExecuteMovement
        // ═══════════════════════════════════════════════
        private void ExecuteMovement(NPC npc, MovementType type)
        {
            if (npc == null) return;

            FinishStep();


            if (type == MovementType.Follow)
            {
                if (Game1.player == null) return;

                if (_goto?.Npc == npc)
                    EndGoto(success: false, invokeCallbacks: false, facePlayer: false);

                StopFollowInternal(suppressScheduleRestore: true);

                _followingNpc  = npc;
                _followEndTime = MovementPathfinding.SafeAddGameTime(Game1.timeOfDay, 60);
                _isDateFollow  = false;

                TransitionTo(FollowState.Halted);

                CompanionScheduleManager.Instance.ClearScheduleForOverride("FollowStarted", npc.Name);
                OnFollowStartedCallback?.Invoke(npc);return;
            }

            if (type == MovementType.StopFollow)
            {
                StopFollow(npc);
                return;
            }

            if (_followingNpc == npc && !_isDateFollow)
                StopFollowInternal();

            if (_goto?.Npc == npc)
                EndGoto(success: false, invokeCallbacks: false, facePlayer: false);

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
            else if (pos1Walkable)target = pos1;
            else
            {
                npc.faceDirection(MovementPathfinding.FacingFromDelta(dx, dy));

                ModEntry.SMonitor?.Log(
                    "[MovementManager] 单步移动被阻挡，仅转向",
                    LogLevel.Debug);

                return;
            }

            _step = new StepContext
            {
                Npc         = npc,
                TargetPixel = target * 64f,
                Timeout     = 60,
            };

            ModEntry.SMonitor?.Log(
                $"[MovementManager] {npc.Name} 滑行至 ({target.X},{target.Y})",
                LogLevel.Debug);
        }

        // ═══════════════════════════════════════════════
        //  GoTo
        // ═══════════════════════════════════════════════
        private void MoveToTileInternal(NPC npc, Vector2 targetTile, Action onSuccess, Action onFail)
        {
            if (npc == null) return;


            if (Game1.player == null)
            {
                onFail?.Invoke();
                return;
            }

            if (_goto != null)
                EndGoto(success: false, invokeCallbacks: false, facePlayer: false);

            var ctx = new GotoContext
            {
                Npc       = npc,
                OnSuccess = onSuccess,
                OnFail    = onFail,};

            if (_isDateFollow && _followingNpc == npc)
            {
                ctx.SuspendedFollow = new SuspendedFollowSnapshot
                {
                    Npc     = npc,
                    EndTime = _followEndTime,
                };ClearNpcMovement(npc);
            }
            else if (_followingNpc == npc && !_isDateFollow)
            {
                StopFollowInternal();
            }

            var loc = npc.currentLocation ?? Game1.player.currentLocation;

            if (loc == null)
            {
                ClearNpcMovement(npc);
                RestoreSuspendedFollow(ctx.SuspendedFollow);
                onFail?.Invoke();
                return;
            }

            // 发起自主寻路前，尝试修正异常起点
            if (!MovementPathfinding.TryRecoverStartingTile(npc, loc))
            {
                ModEntry.SMonitor?.Log($"[MovementManager] {npc.Name} start tile blocked and unrecoverable.", LogLevel.Warn);
            }

            targetTile = MovementPathfinding.ResolveTargetAvoidingPlayer(
                targetTile,
                Game1.player.Tile,
                candidate => MovementPathfinding.IsTileWalkable(loc, candidate, npc));

            if (!MovementPathfinding.TryCreatePath(npc, loc, targetTile, out var controller, out var finalTarget))
            {
                ClearNpcMovement(npc);
                RestoreSuspendedFollow(ctx.SuspendedFollow);
                onFail?.Invoke();

                if (ctx.SuspendedFollow == null && _followingNpc != npc && !IsNpcMoving(npc) && _pending?.Npc != npc)
                    TryRestoreSchedule(npc);

                ModEntry.SMonitor?.Log(
                    $"[MovementManager] MoveToTile: ({targetTile.X},{targetTile.Y}) unreachable.",
                    LogLevel.Warn);

                return;
            }

            ctx.Target = finalTarget;
            ctx.ExpectedLocation = loc;
            ctx.LastTile = npc.Tile;
            ctx.StuckTicks = 0;
            ctx.RepathAttempts = 0;
            ctx.Timeout.Reset();

            _goto          = ctx;
            npc.addedSpeed = 2;
            npc.controller = controller;

            ModEntry.SMonitor?.Log(
                $"[MovementManager] {npc.Name} pathfinding to ({finalTarget.X},{finalTarget.Y})",
                LogLevel.Debug);
        }

        private void HandleGotoCompletion()
        {
            if (_goto == null) return;

            var npc = _goto.Npc;

            if (Game1.player == null)
            {
                EndGoto(success: false, invokeCallbacks: true, facePlayer: false);
                return;
            }

            if (npc.currentLocation != _goto.ExpectedLocation)
            {
                ModEntry.SMonitor?.Log(
                    $"[MovementManager] {npc.Name} GoTo aborted: NPC warped away from expected location '{_goto.ExpectedLocation?.Name}'.",
                    LogLevel.Debug);

                EndGoto(success: false, invokeCallbacks: true, facePlayer: false);
                return;
            }

            bool pathEnded =
                npc.controller == null ||
                MovementPathfinding.IsPathDead(npc.controller) ||
                MovementPathfinding.IsPathDone(npc.controller);

            bool nearTarget = Vector2.Distance(npc.Tile, _goto.Target) <= 1.75f;

            // 无进展检测 (Stuck Detection)
            if (!nearTarget && !pathEnded)
            {
                if (npc.Tile == _goto.LastTile)
                {
                    _goto.StuckTicks++;
                    if (_goto.StuckTicks >= 90) // 约 1.5 秒未移动
                    {
                        _goto.StuckTicks = 0;
                        _goto.RepathAttempts++;
                
                        if (_goto.RepathAttempts > 2)
                        {
                            ModEntry.SMonitor?.Log($"[MovementManager] {npc.Name} stuck too many times, failing GoTo.", LogLevel.Warn);
                            EndGoto(success: false, invokeCallbacks: true, facePlayer: false);
                            return;
                        }
                
                        MovementPathfinding.TryRecoverStartingTile(npc, _goto.ExpectedLocation);
                        if (MovementPathfinding.TryCreatePath(npc, _goto.ExpectedLocation, _goto.Target, out var rep, out var ft))
                        {
                            _goto.Target = ft;
                            npc.controller = rep;
                            npc.addedSpeed = 2;
                            ModEntry.SMonitor?.Log($"[MovementManager] {npc.Name} repath attempt #{_goto.RepathAttempts} success.", LogLevel.Debug);
                        }
                    }
                }
                else
                {
                    _goto.LastTile = npc.Tile;
                    _goto.StuckTicks = 0;
                }
            }
            else
            {
                _goto.StuckTicks = 0;
            }

            if (pathEnded)
            {
                EndGoto(success: nearTarget, invokeCallbacks: true, facePlayer: true);
                return;
            }

            if (_goto.Timeout.Tick())
            {
                ModEntry.SMonitor?.Log(
                    $"[MovementManager] {npc.Name} GoTo timed out.",
                    LogLevel.Warn);

                EndGoto(success: false, invokeCallbacks: true, facePlayer: true);
            }
        }

        private void EndGoto(bool success, bool invokeCallbacks, bool facePlayer)
        {
            if (_goto == null) return;

            var npc       = _goto.Npc;
            var successCb = _goto.OnSuccess;
            var failCb    = _goto.OnFail;
            var suspended = _goto.SuspendedFollow;

            ClearNpcMovement(npc);
            _goto = null;   // 先清空，避免回调中递归触发

            if (facePlayer && Game1.player != null && npc.currentLocation == Game1.player.currentLocation)npc.faceGeneralDirection(Game1.player.getStandingPosition(), 0, false, false);

            RestoreSuspendedFollow(suspended);

            if (invokeCallbacks)
            {
                if (success) successCb?.Invoke();
                else         failCb?.Invoke();
            }

            if (suspended == null && _followingNpc != npc && !IsNpcMoving(npc) && _pending?.Npc != npc)
                TryRestoreSchedule(npc);
        }

        // ═══════════════════════════════════════════════
        //  事件处理
        // ═══════════════════════════════════════════════
        private void OnPlayerWarped(object sender, WarpedEventArgs e)
        {
            // 玩家换图是玩家的事，不应打断 NPC 的独立 GoTo 寻路。
            // 原有的 _goto 强制失败逻辑已删除。
            // 如果 NPC 正在跟随玩家（_followingNpc），HandleFollow 里的跨地图 Warp 逻辑会自行处理，不需要在这里干预。
        }

        private void OnDayStarted(object sender, DayStartedEventArgs e) => ResetAllState(silent: true);
        private void OnSaveLoaded(object sender, SaveLoadedEventArgs e)  => ResetAllState(silent: true);

        /// <summary>
        /// 玩家昏睡倒地（凌晨 2:00 送回家）时的兜底清理：强制解绑仍在跟随的 NPC，
        /// 将其送回默认地图/坐标并恢复日程，避免次日醒来 NPC 卡在农舍床边。
        /// </summary>
        private void OnDayEnding(object sender, DayEndingEventArgs e)
        {
            if (!Context.IsWorldReady) return;

            if (HasActiveFollow && _followingNpc != null)
            {
                var followingNpc = _followingNpc;

                StopFollowInternal(silent: true);

                try
                {
                    if (!string.IsNullOrEmpty(followingNpc.DefaultMap))
                    {
                        Game1.warpCharacter(
                            followingNpc,
                            followingNpc.DefaultMap,
                            new Vector2(followingNpc.DefaultPosition.X / 64f, followingNpc.DefaultPosition.Y / 64f));
                    }

                    TryRestoreSchedule(followingNpc);

                    ModEntry.SMonitor?.Log(
                        $"[MovementManager] OnDayEnding: force-unbound {followingNpc.Name}, warped home + schedule restored.",
                        LogLevel.Info);
                }
                catch (Exception ex)
                {
                    ModEntry.SMonitor?.Log(
                        $"[MovementManager] OnDayEnding cleanup failed for {followingNpc.Name}: {ex.Message}",
                        LogLevel.Warn);
                }
            }

            ResetAllState(silent: true);
        }

        // ═══════════════════════════════════════════════
        //  内部辅助
        // ═══════════════════════════════════════════════
        private void ResetAllState(bool silent)
        {
            ClearPendingAction();
            FinishStep();
            EndGoto(success: false, invokeCallbacks: false, facePlayer: false);
            StopFollowInternal(silent);

            _playerIdleTimer = 0;
            _idleGazeTimer   = 0;
            _lastPlayerTile  = Game1.player?.Tile ?? Vector2.Zero;
        }

        private void ClearPendingAction() => _pending = null;

        private void FinishStep()
        {
            if (_step == null) return;

            var npc    = _step.Npc;
            var target = _step.TargetPixel;
            _step      = null;
            npc.Position = target;

            ClearNpcMovement(npc);
        }

        private bool CanQueueMovement(NPC npc, MovementType type)
        {
            if (Game1.player == null || npc.IsInvisible || npc.currentLocation == null)
            {
                ModEntry.SMonitor?.Log(
                    $"[MovementManager] Action {type} rejected for {npc.Name}: player/NPC state invalid.",
                    LogLevel.Debug);
                return false;
            }

            if (_isDateFollow && _followingNpc == npc)
            {
                ModEntry.SMonitor?.Log(
                    $"[MovementManager] Action {type} rejected for {npc.Name}: date follow is active.",
                    LogLevel.Debug);
                return false;
            }

            return true;
        }

        private void QueueMovementInternal(NPC npc, MovementType type, bool skipDialogueWait)
        {
            if (npc == null || type == MovementType.None) return;


            if (!CanQueueMovement(npc, type)) return;


            if (type == MovementType.Follow)
            {
                if (_isDateFollow)
                {
                    ModEntry.SMonitor?.Log(
                        "[MovementManager] Regular FOLLOW blocked: date follow is active.",
                        LogLevel.Debug);
                    return;
                }

                if (IsMoving && _goto?.Npc == npc)
                {
                    ModEntry.SMonitor?.Log(
                        $"[MovementManager] GoTo interrupted by Follow for {npc.Name}.",
                        LogLevel.Debug);
                    EndGoto(success: false, invokeCallbacks: false, facePlayer: false);
                }}

            if (type == MovementType.StopFollow)
            {
                // 停止跟随不需要等待对话框关闭，直接执行
                StopFollow(npc);
                return;
            }

            // 幂等检查：同一 NPC 同一动作已在队列中
            if (_pending?.Npc == npc && _pending.Movement == type)
                return;

            _pending = new PendingContext
            {
                Npc      = npc,
                Movement = type,
            };

            if (skipDialogueWait)
            {
                // 跳过等待，直接进入轮询阶段
                _pending.Phase          = PendingPhase.WaitingMenuGone;
                _pending.CountdownTimer = 0;
            }
            else
            {
                _pending.Phase          = PendingPhase.WaitingDialogueOpen;
                _pending.CountdownTimer = PENDING_COUNTDOWN;
            }
        }

        private void StopFollowInternal(bool silent = false, bool suppressScheduleRestore = false)
        {
            if (_followingNpc != null)
            {
                ClearNpcMovement(_followingNpc);

                if (!silent)
                {
                    if (!_isDateFollow && !suppressScheduleRestore)
                    {
                        var departingNpc = _followingNpc;
                        StardewValley.DelayedAction.functionAfterDelay(
                            () => BeginSmoothDeparture(departingNpc), 30);
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

        private void BeginSmoothDeparture(NPC npc)
        {
            if (npc == null) return;

            var loc = npc.currentLocation;
            if (loc == null || Game1.locations == null)
            {
                TryRestoreSchedule(npc);
                return;
            }Warp nearestWarp  = null;
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
                $"[MovementManager] {npc.Name} smooth departure: walking to warp at ({warpTile.X},{warpTile.Y}) on '{loc.Name}'.",
                LogLevel.Debug);

            MoveToTileInternal(
                npc,
                walkTarget,
                onSuccess: () =>
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
                            $"[MovementManager] SmoothDeparture home warp failed: {ex.Message}",
                            LogLevel.Warn);
                    }

                    CompanionScheduleManager.Instance.ResumeScheduleAfterFollow(npc);
                },
                onFail: () => CompanionScheduleManager.Instance.ResumeScheduleAfterFollow (npc));
        }

        private void RestoreSuspendedFollow(SuspendedFollowSnapshot? snapshot)
        {
            if (snapshot == null) return;

            var s = snapshot.Value;

            _followingNpc  = s.Npc;
            _followEndTime = s.EndTime;
            _isDateFollow  = true;

            TransitionTo(FollowState.Halted);

            ModEntry.SMonitor?.Log(
                $"[MovementManager] Date-follow restored for {s.Npc.Name} (until {s.EndTime}).",
                LogLevel.Debug);
        }

        private static void ClearNpcMovement(NPC npc)
        {
            if (npc == null) return;

            npc.controller = null;
            npc.addedSpeed = 0;
            npc.Halt();
        }


        /// <summary>
        /// ★ 仅在 CompanionScheduleManager 今天没有为该 NPC 排程时才恢复原版日程。
        /// </summary>
        public void TryRestoreSchedule(NPC npc)
        {
            if (npc == null) return;
            if (_isDateFollow && _followingNpc == npc)
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
                    $"[MovementManager] TryRestoreSchedule spouse-check failed: {ex.Message}",
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
                    $"[MovementManager] Schedule restored for {npc.Name}.",
                    LogLevel.Debug);
            }
            catch (Exception ex)
            {
                ModEntry.SMonitor?.Log(
                    $"[MovementManager] Schedule restore failed: {ex.Message}",
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
                ??
                typeof(NPC).GetField(
                    "<Schedule>k__BackingField",
                    BindingFlags.Instance | BindingFlags.NonPublic);

            _scheduleReflectionCached = true;
        }

    }
}
