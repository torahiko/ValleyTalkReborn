using System;
using Microsoft.Xna.Framework;
using StardewValley;

namespace ValleytalkReborn.Cutscene.Actions
{
    /// <summary>
    /// 分镜节奏停顿动作：等待指定秒数，用于控制演出节奏。
    /// </summary>
    public sealed class WaitAction : IDirectorAction
    {
        public bool WaitForCompletion { get; set; } = true;

        private readonly float _durationMs;
        private float _elapsedMs;

        public WaitAction(float durationSeconds)
        {
            _durationMs = Math.Max(0f, durationSeconds * 1000f);
        }

        public void Enter()
        {
            _elapsedMs = 0f;
        }

        public bool Update(GameTime time)
        {
            _elapsedMs += (float)time.ElapsedGameTime.TotalMilliseconds;
            return _elapsedMs >= _durationMs;
        }

        public void Exit()
        {
            // 无需清理
        }
    }
}
