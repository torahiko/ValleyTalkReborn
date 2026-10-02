using System;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;

namespace ValleytalkReborn.Cutscene.Actions
{
    /// <summary>
    /// 原生音效播放动作：触发游戏内置音效（如 "dwop", "coin", "purchase" 等）
    /// </summary>
    public sealed class SoundAction : IDirectorAction
    {
        public bool WaitForCompletion { get; set; } = true;

        private readonly string _soundName;

        public SoundAction(string soundName)
        {
            _soundName = soundName?.Trim() ?? string.Empty;
        }

        public void Enter()
        {
            if (string.IsNullOrEmpty(_soundName)) return;

            try
            {
                Game1.playSound(_soundName);
            }
            catch (Exception ex)
            {
                ModEntry.SMonitor?.Log($"[SoundAction] Failed to play sound '{_soundName}': {ex.Message}", LogLevel.Warn);
            }
        }

        public bool Update(GameTime time)
        {
            return true; // 单帧触发完成
        }

        public void Exit()
        {
            // 无需清理
        }
    }
}
