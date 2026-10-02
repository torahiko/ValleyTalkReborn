using System;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Pathfinding;

namespace ValleytalkReborn.Cutscene.Actions
{
    /// <summary>
    /// 移动到指定格子动作，带物理看门狗熔断机制
    /// </summary>
    public sealed class MoveToTileAction : IDirectorAction
    {
        public bool WaitForCompletion { get; set; } = true;

        private readonly NPC _npc;
        public NPC Actor => _npc;
        private readonly Vector2 _targetTile;
        private readonly float _timeoutSeconds;

        private PathFindController _controller;
        private Vector2 _lastPosition;
        private int _stuckTicks;
        private float _elapsedSeconds;
        private bool _hasRetried;
        private bool _isCompleted;
        private float _effectiveTimeoutSeconds;

        public MoveToTileAction(NPC npc, Vector2 targetTile, float timeoutSeconds = 5f)
        {
            _npc = npc ?? throw new ArgumentNullException(nameof(npc));
            _targetTile = targetTile;
            _timeoutSeconds = timeoutSeconds;
        }

        public void Enter()
        {
            _lastPosition = _npc.Position;
            _stuckTicks = 0;
            _elapsedSeconds = 0f;
            _hasRetried = false;
            _isCompleted = false;

            // 依据距离动态换算充裕的超时阈值（防止长距离正常行走向导看门狗误杀瞬移）
            float distanceTiles = Vector2.Distance(_npc.Tile, _targetTile);
            float distanceTimeout = distanceTiles * 0.8f + 4.0f;
            _effectiveTimeoutSeconds = Math.Max(_timeoutSeconds, distanceTimeout);

            // 彻底打断日程动作与待机状态，解除 freezeMotion 封锁，注入正常寻路移速
            CutsceneActorHelper.WakeupActor(_npc);
            _npc.addedSpeed = 2;

            // 若已经处于目标点，直接标记完成，无需折腾寻路
            if (Vector2.Distance(_npc.Tile, _targetTile) < 0.5f)
            {
                _isCompleted = true;
                return;
            }

            // 1. 起点弹性微吸附：若 NPC 起步时正处于椅子、桌椅边缘等阻挡判定格，先自动微移至就近连通格
            MovementPathfinding.TryRecoverStartingTile(_npc, _npc.currentLocation, radius: 3);

            // 2. 尝试标准寻路
            if (!MovementPathfinding.TryCreatePath(_npc, _npc.currentLocation, _targetTile, out _controller, out var finalTarget))
            {
                // 3. 多阶回退：若目标格微受阻，以更宽半径（5格）向外搜寻连通可达格
                var expandedTarget = MovementPathfinding.FindNearestWalkableTile(_npc.currentLocation, _targetTile, _npc, radius: 5);
                if (expandedTarget != _targetTile)
                {
                    MovementPathfinding.TryCreatePath(_npc, _npc.currentLocation, expandedTarget, out _controller, out finalTarget);
                }
            }

            if (_controller == null || MovementPathfinding.IsPathDead(_controller))
            {
                ModEntry.SMonitor?.Log(
                    $"[MoveToTileAction] All path attempts failed for {_npc.Name} to ({_targetTile.X},{_targetTile.Y}), warping safely.",
                    LogLevel.Debug);
                
                // 实在无可通行路径：熔断瞬移到位
                WarpToTargetSafely();
                _controller = null;
                _isCompleted = true;
            }
            else
            {
                _npc.controller = _controller;
            }
        }

        public bool Update(GameTime time)
        {
            if (_isCompleted) return true;

            _elapsedSeconds += (float)time.ElapsedGameTime.TotalSeconds;

            // 超时熔断：强制瞬移到位
            if (_elapsedSeconds >= _effectiveTimeoutSeconds)
            {
                ModEntry.SMonitor?.Log(
                    $"[MoveToTileAction] Timeout watchdog triggered for {_npc.Name} (elapsed {_elapsedSeconds:F1}s >= limit {_effectiveTimeoutSeconds:F1}s), warping to target.",
                    LogLevel.Debug);
                WarpToTargetSafely();
                return true;
            }

            // 已到达目标或处于有效交互站位范围内（智能宽松到达容差，避免误杀瞬移）
            if (Vector2.Distance(_npc.Tile, _targetTile) <= 1.0f)
            {
                return true;
            }

            // 路径失效：可能已经到达
            if (_controller == null || MovementPathfinding.IsPathDone(_controller))
            {
                return Vector2.Distance(_npc.Tile, _targetTile) <= 1.5f;
            }

            // 像素级静止检测：真正卡住超过 120 帧（2 秒）才进入脱困流程
            if (Vector2.Distance(_npc.Position, _lastPosition) < 2.0f)
            {
                _stuckTicks++;
                
                if (_stuckTicks > 120)
                {
                    // 终点宽松容差保护：若已经走到目标 1.5 格以内，直接优雅就位，绝不瞬移
                    if (Vector2.Distance(_npc.Tile, _targetTile) <= 1.5f)
                    {
                        _isCompleted = true;
                        return true;
                    }

                    if (!_hasRetried)
                    {
                        ModEntry.SMonitor?.Log(
                            $"[MoveToTileAction] {_npc.Name} stuck (0 displacement for 2s), attempting recovery and repath.",
                            LogLevel.Debug);
                        
                        _hasRetried = true;
                        
                        // 尝试脱困与重寻路
                        if (MovementPathfinding.TryRecoverStartingTile(_npc, _npc.currentLocation, radius: 4))
                        {
                            CutsceneActorHelper.WakeupActor(_npc);
                            if (MovementPathfinding.TryCreatePath(_npc, _npc.currentLocation, _targetTile, out _controller, out _))
                            {
                                _npc.addedSpeed = 2;
                                _npc.controller = _controller;
                                _stuckTicks = 0;
                                _lastPosition = _npc.Position;
                                return false;
                            }
                        }
                        
                        // 若重寻路失败但已在 1.8 格附近，直接就位免瞬移
                        if (Vector2.Distance(_npc.Tile, _targetTile) <= 1.8f)
                        {
                            _isCompleted = true;
                            return true;
                        }

                        // 确实远离目标且重寻路失败：瞬移到位并标记完成
                        ModEntry.SMonitor?.Log(
                            $"[MoveToTileAction] Recovery failed, warping {_npc.Name} to target.",
                            LogLevel.Debug);
                        WarpToTargetSafely();
                        _isCompleted = true;
                        return true;
                    }
                    else
                    {
                        // 已经重试过依然受阻：若已近身则平稳收工，否则在触发原版 3 秒流汗穿透前熔断瞬移
                        if (Vector2.Distance(_npc.Tile, _targetTile) <= 1.8f)
                        {
                            _isCompleted = true;
                            return true;
                        }

                        ModEntry.SMonitor?.Log(
                            $"[MoveToTileAction] {_npc.Name} blocked repeatedly (player or dynamic obstacle), warping safely before vanilla charge-through.",
                            LogLevel.Debug);
                        WarpToTargetSafely();
                        _isCompleted = true;
                        return true;
                    }
                }
            }
            else
            {
                _stuckTicks = 0;
                _lastPosition = _npc.Position;
            }

            return false;
        }

        /// <summary>
        /// 熔断瞬移：目标格先经可通行性格子换算（红线 #2），
        /// 绝不把 NPC 塞进家具/吧台等不可站立格子。
        /// </summary>
        private void WarpToTargetSafely()
        {
            var loc = _npc.currentLocation;
            if (loc == null) return;

            var safeTile = MovementPathfinding.FindNearestWalkableTile(loc, _targetTile, _npc, radius: 3);
            Game1.warpCharacter(_npc, loc.NameOrUniqueName, safeTile);
            if (_npc != null)
            {
                _npc.isCharging = false;
            }
        }

        public void Exit()
        {
            if (_npc != null)
            {
                _npc.Halt();
                _npc.controller = null;
                _npc.addedSpeed = 0;
                _npc.isCharging = false;
            }
        }
    }
}
