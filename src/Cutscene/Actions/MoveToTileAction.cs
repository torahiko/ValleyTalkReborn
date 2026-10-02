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
        private readonly Vector2 _targetTile;
        private readonly float _timeoutSeconds;

        private PathFindController _controller;
        private Vector2 _lastTile;
        private int _stuckTicks;
        private float _elapsedSeconds;
        private bool _hasRetried;
        private bool _isCompleted;

        public MoveToTileAction(NPC npc, Vector2 targetTile, float timeoutSeconds = 5f)
        {
            _npc = npc ?? throw new ArgumentNullException(nameof(npc));
            _targetTile = targetTile;
            _timeoutSeconds = timeoutSeconds;
        }

        public void Enter()
        {
            _lastTile = _npc.Tile;
            _stuckTicks = 0;
            _elapsedSeconds = 0f;
            _hasRetried = false;
            _isCompleted = false;

            // 彻底打断日程动作与待机状态，解除 freezeMotion 封锁，注入正常寻路移速
            CutsceneActorHelper.WakeupActor(_npc);
            _npc.addedSpeed = 2;

            if (!MovementPathfinding.TryCreatePath(_npc, _npc.currentLocation, _targetTile, out _controller, out var finalTarget))
            {
                ModEntry.SMonitor?.Log(
                    $"[MoveToTileAction] Initial pathfinding failed for {_npc.Name} to ({_targetTile.X},{_targetTile.Y})",
                    LogLevel.Debug);
                
                // 寻路失败：直接瞬移到位并标记完成，避免发呆超时
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
            if (_elapsedSeconds >= _timeoutSeconds)
            {
                ModEntry.SMonitor?.Log(
                    $"[MoveToTileAction] Timeout watchdog triggered for {_npc.Name}, warping to target.",
                    LogLevel.Debug);
                WarpToTargetSafely();
                return true;
            }

            // 已到达目标
            if (Vector2.Distance(_npc.Tile, _targetTile) < 0.5f)
            {
                return true;
            }

            // 路径失效：可能已经到达
            if (_controller == null || MovementPathfinding.IsPathDone(_controller))
            {
                return Vector2.Distance(_npc.Tile, _targetTile) < 1.5f;
            }

            // 静止检测：卡住超过 60 帧（1 秒）
            if (Vector2.Distance(_npc.Tile, _lastTile) < 0.1f)
            {
                _stuckTicks++;
                
                if (_stuckTicks > 45)
                {
                    if (!_hasRetried)
                    {
                        ModEntry.SMonitor?.Log(
                            $"[MoveToTileAction] {_npc.Name} stuck, attempting recovery and repath.",
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
                                _lastTile = _npc.Tile;
                                return false;
                            }
                        }
                        
                        // 重寻路失败：瞬移到位并标记完成
                        ModEntry.SMonitor?.Log(
                            $"[MoveToTileAction] Recovery failed, warping {_npc.Name} to target.",
                            LogLevel.Debug);
                        WarpToTargetSafely();
                        _isCompleted = true;
                        return true;
                    }
                    else
                    {
                        // 已经重试过依然受阻（如玩家站在必经之路上）：在触发原版 3 秒流汗穿透前，立即熔断安全瞬移到位
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
                _lastTile = _npc.Tile;
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
