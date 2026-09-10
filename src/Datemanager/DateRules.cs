using System;
using System.Collections.Generic;
using StardewValley;

namespace ValleytalkReborn
{
    /// <summary>
    /// 游戏世界的不可变状态快照。
    /// 由 DateManager 从 Game1 中捕获，传给 DateRules 做纯逻辑判断。
    /// </summary>
    public sealed record DateWorldSnapshot(
        int TimeOfDay,
        string PlayerLocationName,
        bool IsFestivalDay,
        bool IsWorldReady
    );

    /// <summary>
    /// 约会业务规则的纯函数集合。
    /// 不依赖 Game1、不依赖 SMAPI、不依赖任何全局状态。
    /// 所有方法都是 static，输入 → 输出，无副作用。
    /// </summary>
    public static class DateRules
    {
        // ═══════════════════════════════════════════════════════════
        //  预约规则
        // ═══════════════════════════════════════════════════════════

        /// <summary>判断是否可以预约一场 Scheduled 约会。</summary>
        public static bool CanScheduleDate(
            DateWorldSnapshot world,
            string locationId,
            string activeNpcName,
            string requestedNpcName,
            HashSet<string> whitelistedLocations,
            int cutoffTime)
        {
            if (!world.IsWorldReady) return false;
            if (world.IsFestivalDay) return false;
            if (world.TimeOfDay >= cutoffTime) return false;
            if (!whitelistedLocations.Contains(locationId)) return false;
            if (!string.IsNullOrEmpty(activeNpcName) &&
                !string.Equals(activeNpcName, requestedNpcName, StringComparison.OrdinalIgnoreCase))
                return false;
            return true;
        }

        /// <summary>综合判断当前是否可立即安排一场约会（统一入口）。</summary>
        public static bool CanScheduleNow(NPC npc, string locationId, DateWorldSnapshot world, out string reason)
        {
            reason = string.Empty;
            bool isZh = LocalizedContentManager.CurrentLanguageCode == LocalizedContentManager.LanguageCode.zh;

            if (!ModEntry.Config.EnableDateSystem)
            {
                reason = isZh ? "约会系统未启用" : "Date system disabled";
                return false;
            }
            if (world.IsFestivalDay)
            {
                reason = isZh ? "今天是节日，无法安排约会" : "Cannot schedule date during festivals";
                return false;
            }
            if (world.TimeOfDay >= 2130)
            {
                reason = isZh ? "太晚了，无法安排今晚的约会" : "Too late to schedule a date tonight";
                return false;
            }
            if (!DateLocationRegistry.Locations.TryGetValue(locationId, out var info))
            {
                reason = isZh ? "地点不存在或不可用" : "Location not found";
                return false;
            }
            int hearts = Game1.player.getFriendshipHeartLevelForNPC(npc.Name);
            if (hearts < info.RequiredHearts)
            {
                reason = isZh ? $"需要好感度达到 {info.RequiredHearts} 心" : $"Requires {info.RequiredHearts} hearts";
                return false;
            }
            if (!info.AllowRainyDays && Game1.isRaining)
            {
                reason = isZh ? "雨天不宜前往该地点" : "Unavailable on rainy days";
                return false;
            }
            return true;
        }

        /// <summary>判断是否可以启动一场 Follow 约会。</summary>
        public static bool CanStartFollow(
            DateWorldSnapshot world,
            string activeNpcName,
            string requestedNpcName,
            int hardEndTime)
        {
            if (!world.IsWorldReady) return false;
            if (world.IsFestivalDay) return false;
            if (world.TimeOfDay >= hardEndTime) return false;
            if (!string.IsNullOrEmpty(activeNpcName) &&
                !string.Equals(activeNpcName, requestedNpcName, StringComparison.OrdinalIgnoreCase))
                return false;
            return true;
        }

        // ═══════════════════════════════════════════════════════════
        //  触发规则
        // ═══════════════════════════════════════════════════════════

        /// <summary>判断当前是否满足触发约会的条件（Pending → Active）。</summary>
        public static bool ShouldTriggerDate(
            DateWorldSnapshot world,
            string activeDateLocation,
            int earliestTime,
            int cutoffTime)
        {
            if (!world.IsWorldReady) return false;
            if (world.TimeOfDay < earliestTime) return false;
            if (world.TimeOfDay >= cutoffTime) return false;
            if (!string.Equals(world.PlayerLocationName, activeDateLocation, StringComparison.OrdinalIgnoreCase))
                return false;
            return true;
        }

        // ═══════════════════════════════════════════════════════════
        //  迟到与时长规则
        // ═══════════════════════════════════════════════════════════

        /// <summary>根据到达时间计算迟到等级（5 档）。</summary>
        public static LatenessLevel GetLatenessLevel(
            int timeOfDay, int earliestTime, int onTimeCutoff, int slightlyLateCutoff, int hardEndTime)
        {
            if (timeOfDay < earliestTime) return LatenessLevel.TooEarly;
            if (timeOfDay < onTimeCutoff) return LatenessLevel.OnTime;
            if (timeOfDay < slightlyLateCutoff) return LatenessLevel.SlightlyLate;
            if (timeOfDay < hardEndTime) return LatenessLevel.VeryLate;
            return LatenessLevel.MissedWindow;
        }

        /// <summary>根据到达时间计算约会时长（分钟）。</summary>
        public static int GetDateDurationMinutes(
            int timeOfDay, int onTimeCutoff, int slightlyLateCutoff, int defaultDuration)
        {
            if (timeOfDay >= slightlyLateCutoff) return 60;
            if (timeOfDay >= onTimeCutoff) return 120;
            return defaultDuration;
        }

        // ═══════════════════════════════════════════════════════════
        //  地点合法性
        // ═══════════════════════════════════════════════════════════

        /// <summary>判断是否为非法约会地点（危险区域）。</summary>
        public static bool IsIllegalDateLocation(string locationName)
        {
            if (string.IsNullOrEmpty(locationName)) return false;
            return locationName.StartsWith("UndergroundMine", StringComparison.OrdinalIgnoreCase)
                   || locationName.StartsWith("Island", StringComparison.OrdinalIgnoreCase)
                   || locationName.StartsWith("VolcanoDungeon", StringComparison.OrdinalIgnoreCase)
                   || locationName.Equals("Desert", StringComparison.OrdinalIgnoreCase)
                   || locationName.Equals("SkullCave", StringComparison.OrdinalIgnoreCase);
        }
    }
}