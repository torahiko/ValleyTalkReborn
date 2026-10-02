using System;
using Microsoft.Xna.Framework;
using StardewValley;

namespace ValleytalkReborn.Cutscene.Actions
{
    /// <summary>
    /// 相机对焦动作：平滑移动相机到目标 NPC、玩家农夫或指定瓦片坐标
    /// 支持动态角色跟随与平滑运镜
    /// </summary>
    public sealed class CameraAction : IDirectorAction
    {
        public bool WaitForCompletion { get; set; } = true;

        private readonly NPC _npc;
        private readonly bool _targetPlayer;
        private readonly Vector2? _tilePosition;
        private float _elapsedSeconds;
        private const float MaxCameraWaitSeconds = 2.5f;

        public CameraAction(NPC npc)
        {
            _npc = npc ?? throw new ArgumentNullException(nameof(npc));
        }

        public CameraAction(bool targetPlayer)
        {
            _targetPlayer = targetPlayer;
        }

        public CameraAction(Vector2 tilePosition)
        {
            _tilePosition = tilePosition;
        }

        public void Enter()
        {
            _elapsedSeconds = 0f;
            if (_npc != null)
            {
                VirtualDirector.Instance?.SetCameraTarget(_npc);
            }
            else if (_targetPlayer)
            {
                VirtualDirector.Instance?.SetCameraTargetPlayer();
            }
            else if (_tilePosition.HasValue)
            {
                VirtualDirector.Instance?.SetCameraTarget(_tilePosition.Value);
            }
        }

        public bool Update(GameTime time)
        {
            // 若为并发模式（WaitForCompletion == false），立即交由 VirtualDirector 持续平滑跟随，不阻塞后续动作
            if (!WaitForCompletion)
            {
                return true;
            }

            _elapsedSeconds += (float)time.ElapsedGameTime.TotalSeconds;
            if (_elapsedSeconds >= MaxCameraWaitSeconds)
            {
                return true;
            }

            // 若为串行阻塞运镜（如镜头先切到远处角色，等对焦稳定再进行下一步），等待视口中心平滑接近目标
            if (VirtualDirector.Instance?.HasCameraArrivedAtTarget(thresholdPixels: 48f) == true)
            {
                return true;
            }

            return false;
        }

        public void Exit()
        {
            // 无需清理，相机跟随目标持续保留至下一动作或过场结束
        }
    }
}
