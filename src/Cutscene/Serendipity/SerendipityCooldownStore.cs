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

        /// <summary>
        /// 浮标自毁退避表：key = "{situationTitle}|{locationName}"（OrdinalIgnoreCase），
        /// value = 自毁时的 (游戏天数, timeOfDay)。Memory 瞬态，ResetDay/Clear 清空。
        /// </summary>
        private readonly Dictionary<string, (int Day, int TimeOfDay)> _beaconDismissals
            = new(StringComparer.OrdinalIgnoreCase);

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
        /// 记录一次浮标自毁（超时/离散/演员离场/玩家走远等自毁类 Dismiss；交互与门禁类不记录）。
        /// situationTitle 或 locationName 为空时跳过（RECOVERABLE：退避不生效一秒，无碍）。
        /// </summary>
        public void RecordBeaconDismissal(string situationTitle, string locationName, int currentTotalDays, int timeOfDay)
        {
            if (string.IsNullOrWhiteSpace(situationTitle) || string.IsNullOrWhiteSpace(locationName))
                return;

            _beaconDismissals[$"{situationTitle}|{locationName}"] = (currentTotalDays, timeOfDay);
        }

        /// <summary>
        /// 判定浮标是否仍在退避窗口内：同日且 timeOfDay 差值 &lt; suppressMinutes 游戏分钟。
        /// timeOfDay 为 HHMM 格式（600-2600），按游戏时钟换算分钟差，正确处理跨小时进位；
        /// 跨天由 Day 键校验自然失效。
        /// </summary>
        public bool IsBeaconSuppressed(string situationTitle, string locationName, int currentTotalDays, int timeOfDay, int suppressMinutes = 30)
        {
            if (string.IsNullOrWhiteSpace(situationTitle) || string.IsNullOrWhiteSpace(locationName))
                return false;

            if (!_beaconDismissals.TryGetValue($"{situationTitle}|{locationName}", out var dismissedAt))
                return false;

            if (dismissedAt.Day != currentTotalDays)
                return false;

            return ToGameMinutes(timeOfDay) - ToGameMinutes(dismissedAt.TimeOfDay) < suppressMinutes;
        }

        /// <summary>
        /// HHMM 游戏时刻换算为当日分钟数（游戏时钟无 1260：1250 → 1300 是 10 分钟而非 50）。
        /// </summary>
        private static int ToGameMinutes(int timeOfDay)
            => (timeOfDay / 100) * 60 + (timeOfDay % 100);

        /// <summary>
        /// 换天时重置每日计数
        /// </summary>
        public void ResetDay()
        {
            DailyTriggeredCount = 0;
            LastTriggerTimeOfDay = -1;
            _beaconDismissals.Clear();
        }

        /// <summary>
        /// 回标题时全量清空
        /// </summary>
        public void Clear()
        {
            DailyTriggeredCount = 0;
            LastTriggerTimeOfDay = -1;
            _npcLastTriggeredDays.Clear();
            _beaconDismissals.Clear();
        }
    }
}
