using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;

namespace ValleytalkReborn.Cutscene.Actions
{
    /// <summary>
    /// NPC 头顶表情气泡动作
    /// </summary>
    public sealed class EmoteAction : IDirectorAction
    {
        private readonly NPC _npc;
        private readonly int _emoteId;
        private float _elapsedMs;
        private const float DefaultDurationMs = 800f;

        // 表情映射表（复用自 EmbodiedActionParser）
        private static readonly Dictionary<string, int> EmoteMap = new(StringComparer.OrdinalIgnoreCase)
        {
            { "SURPRISE",   8 },  // 惊讶/叹号
            { "QUESTION",  56 },  // 问号
            { "HAPPY",     32 },  // 开心/音符
            { "SAD",       28 },  // 悲伤
            { "HEART",     20 },  // 爱心
            { "ANGRY",     12 },  // 愤怒
            { "SLEEP",     24 },  // ZZZ
            { "SICK",      28 },  // 生病
            { "IDEA",       8 },  // 灵感/灯泡（使用惊讶）
        };

        /// <summary>
        /// 通过表情名称创建
        /// </summary>
        public EmoteAction(NPC npc, string emoteName)
        {
            _npc = npc ?? throw new ArgumentNullException(nameof(npc));
            
            if (!EmoteMap.TryGetValue(emoteName, out _emoteId))
            {
                ModEntry.SMonitor?.Log(
                    $"[EmoteAction] Unknown emote name '{emoteName}', defaulting to SURPRISE.",
                    LogLevel.Warn);
                _emoteId = 8;
            }
        }

        /// <summary>
        /// 直接通过表情 ID 创建
        /// </summary>
        public EmoteAction(NPC npc, int emoteId)
        {
            _npc = npc ?? throw new ArgumentNullException(nameof(npc));
            _emoteId = emoteId;
        }

        public void Enter()
        {
            _elapsedMs = 0f;
            _npc?.doEmote(_emoteId);
        }

        public bool Update(GameTime time)
        {
            _elapsedMs += (float)time.ElapsedGameTime.TotalMilliseconds;
            return _elapsedMs >= DefaultDurationMs;
        }

        public void Exit()
        {
            // 无需清理
        }
    }
}
