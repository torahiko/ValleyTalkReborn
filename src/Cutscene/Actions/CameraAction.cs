using System;
using Microsoft.Xna.Framework;
using StardewValley;

namespace ValleytalkReborn.Cutscene.Actions
{
    /// <summary>
    /// 相机对焦动作：平滑移动相机到目标 NPC
    /// </summary>
    public sealed class CameraAction : IDirectorAction
    {
        private readonly NPC _npc;
        private bool _completed;

        public CameraAction(NPC npc)
        {
            _npc = npc ?? throw new ArgumentNullException(nameof(npc));
        }

        public void Enter()
        {
            _completed = false;
            // 通知导演设置相机目标
            VirtualDirector.Instance?.SetCameraTarget(_npc);
        }

        public bool Update(GameTime time)
        {
            // 相机平滑插值由 VirtualDirector 每帧处理
            // 这里简单延迟几帧让相机完成插值
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
