using System;
using System.Collections.Generic;

namespace ValleytalkReborn.Cutscene.Serendipity
{
    /// <summary>
    /// 偶遇事件冷却存储器：负责记录当日触发次数、NPC 个人冷却及每日零点重置。
    /// 纯内存管理，确保同一 NPC 组合不会高频重复上演。
    /// </summary>
    public sealed class SerendipityCooldownStore
    {
        public static SerendipityCooldownStore Instance { get; } = new();

        /// <summary>
        /// 当日已触发的偶遇剧情次数
        /// </summary>
        public int DailyTriggeredCount { get; private set; } = 0;

        /// <summary>
        /// 上次触发时的游戏内时刻 (timeOfDay)
        /// </summary>
        public int LastTriggerTimeOfDay { get; private set; } = -1;

        /// <summary>
        /// NPC 名字 -> 上次参与偶遇剧情的游戏天数 (Game1.Date.TotalDays 或 dayOfMonth)
        /// </summary>
        private readonly Dictionary<string, int> _npcLastTriggeredDays = new(StringComparer.OrdinalIgnoreCase);

        private SerendipityCooldownStore() { }

        /// <summary>
        /// 检查今日是否仍有触发额度
        /// </summary>
        public bool CanTriggerToday(int maxDailyCount)
        {
            if (maxDailyCount <= 0) return false;
            return DailyTriggeredCount < maxDailyCount;
        }

        /// <summary>
        /// 检查指定 NPC 今天是否已经参演过偶遇剧情
        /// </summary>
        public bool IsNpcAvailable(string npcName, int currentTotalDays, int cooldownDays = 1)
        {
            if (string.IsNullOrWhiteSpace(npcName)) return false;

            if (_npcLastTriggeredDays.TryGetValue(npcName, out int lastDay))
            {
                return (currentTotalDays - lastDay) >= cooldownDays;
            }

            return true;
        }

        /// <summary>
        /// 记录一次成功的剧情触发
        /// </summary>
        public void RecordTrigger(IEnumerable<string> actorNames, int currentTotalDays, int timeOfDay)
        {
            DailyTriggeredCount++;
            LastTriggerTimeOfDay = timeOfDay;

            if (actorNames != null)
            {
                foreach (var name in actorNames)
                {
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        _npcLastTriggeredDays[name] = currentTotalDays;
                    }
                }
            }
        }

        /// <summary>
        /// 换天时重置每日计数
        /// </summary>
        public void ResetDay()
        {
            DailyTriggeredCount = 0;
            LastTriggerTimeOfDay = -1;
        }

        /// <summary>
        /// 回标题时全量清空
        /// </summary>
        public void Clear()
        {
            DailyTriggeredCount = 0;
            LastTriggerTimeOfDay = -1;
            _npcLastTriggeredDays.Clear();
        }
    }
}
