using System;
using Microsoft.Xna.Framework;
using StardewValley;

namespace ValleytalkReborn.Cutscene.Actions
{
    /// <summary>
    /// 精准调整 NPC 朝向动作
    /// </summary>
    public sealed class FaceAction : IDirectorAction
    {
        public bool WaitForCompletion { get; set; } = true;

        private readonly NPC _npc;
        private readonly int _direction;

        /// <summary>
        /// 创建朝向动作
        /// </summary>
        /// <param name="npc">目标 NPC</param>
        /// <param name="direction">朝向（0=Up, 1=Right, 2=Down, 3=Left）</param>
        public FaceAction(NPC npc, int direction)
        {
            _npc = npc ?? throw new ArgumentNullException(nameof(npc));
            _direction = direction;
        }

        public void Enter()
        {
            _npc?.faceDirection(_direction);
        }

        public bool Update(GameTime time)
        {
            return true; // 单帧完成
        }

        public void Exit()
        {
            // 无需清理
        }
    }

    /// <summary>
    /// 看向目标对象或坐标动作
    /// </summary>
    public sealed class LookAtAction : IDirectorAction
    {
        public bool WaitForCompletion { get; set; } = true;

        private readonly NPC _npc;
        private readonly StardewValley.Character _target;
        private readonly Vector2? _targetPos;

        /// <summary>
        /// 看向另一个角色
        /// </summary>
        public LookAtAction(NPC npc, StardewValley.Character target)
        {
            _npc = npc ?? throw new ArgumentNullException(nameof(npc));
            _target = target ?? throw new ArgumentNullException(nameof(target));
        }

        /// <summary>
        /// 看向指定坐标
        /// </summary>
        public LookAtAction(NPC npc, Vector2 targetPos)
        {
            _npc = npc ?? throw new ArgumentNullException(nameof(npc));
            _targetPos = targetPos;
        }

        public void Enter()
        {
            if (_target != null)
            {
                _npc?.faceGeneralDirection(_target.Position, opposite: false);
            }
            else if (_targetPos.HasValue)
            {
                _npc?.faceGeneralDirection(_targetPos.Value * 64f, opposite: false);
            }
        }

        public bool Update(GameTime time)
        {
            return true; // 单帧完成
        }

        public void Exit()
        {
            // 无需清理
        }
    }
}
