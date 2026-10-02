using System;
using Microsoft.Xna.Framework;
using StardewValley;

namespace ValleytalkReborn.Cutscene.Actions
{
    /// <summary>
    /// 相机对焦动作：平滑移动相机到目标 NPC 或指定瓦片坐标（如农夫坐标）
    /// </summary>
    public sealed class CameraAction : IDirectorAction
    {
        public bool WaitForCompletion { get; set; } = true;

        private readonly NPC _npc;
        private readonly Vector2? _tilePosition;
        private bool _completed;

        public CameraAction(NPC npc)
        {
            _npc = npc ?? throw new ArgumentNullException(nameof(npc));
        }

        public CameraAction(Vector2 tilePosition)
        {
            _tilePosition = tilePosition;
        }

        public void Enter()
        {
            _completed = false;
            if (_npc != null)
            {
                VirtualDirector.Instance?.SetCameraTarget(_npc);
            }
            else if (_tilePosition.HasValue)
            {
                VirtualDirector.Instance?.SetCameraTarget(_tilePosition.Value);
            }
        }

        public bool Update(GameTime time)
        {
            // 相机平滑插值由 VirtualDirector 每帧处理
            // 这里简单延迟一帧确保相机插值目标生效
            if (!_completed)
            {
                _completed = true;
                return false;
            }
            return true;
        }

        public void Exit()
        {
            // 无需清理
        }
    }
}
