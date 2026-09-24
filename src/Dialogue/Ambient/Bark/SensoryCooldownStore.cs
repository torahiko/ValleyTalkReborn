using System;
using System.Collections.Generic;
using System.Linq;
using StardewValley;

namespace ValleytalkReborn;

internal static class SensoryCooldownStore
{
    // 测试缝线：NowProvider 返回线性游戏分钟轴（实现同 MoodShockStore.DefaultNowProvider 公式）
    internal static Func<int> NowProvider { get; set; } = DefaultNow;

    private static readonly HashSet<(string NpcName, SensoryType Type)> _dailyLocks = new();
    private static readonly Dictionary<string, int> _transientExpiry = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object _lock = new object();

    private static int DefaultNow()
    {
        // 游戏线性分钟轴：TotalDays * 1200 + 当日分钟（6:00 起点，负值钳为 0）
        int dayMinutes = Math.Max(0, (Game1.timeOfDay / 100 - 6) * 60 + Game1.timeOfDay % 100);
        return Game1.Date.TotalDays * 1200 + dayMinutes;
    }

    /// <summary>只读闸门查询（U3 裁定：决策网先用它判冷却，命中后由决策网调 TryClaim）</summary>
    internal static bool IsLocked(string npcName, SensoryType type)
    {
        if (string.IsNullOrWhiteSpace(npcName)) return true;

        try
        {
            lock (_lock)
            {
                if (type is SensoryType.LewisShorts or SensoryType.TrashOutfit or SensoryType.HazmatSuit
                    or SensoryType.WeddingDress or SensoryType.FaintedYesterday)
                {
                    return _dailyLocks.Contains((npcName, type));
                }

                // Transient
                var key = npcName + "|" + type;
                if (_transientExpiry.TryGetValue(key, out int expiry))
                {
                    if (NowProvider() < expiry) return true;
                    // 已过期，顺手移除
                    _transientExpiry.Remove(key);
                    return false;
                }

                return false;
            }
        }
        catch
        {
            return true; // 保守方向：不触发互动
        }
    }

    /// <summary>触发时上锁：Continuous → 当日锁；Transient → NowProvider() + 120 游戏分钟</summary>
    internal static bool TryClaim(string npcName, SensoryType type, SensoryCategory category)
    {
        if (string.IsNullOrWhiteSpace(npcName)) return false;

        try
        {
            lock (_lock)
            {
                if (IsLocked(npcName, type)) return false;

                if (category == SensoryCategory.Continuous)
                {
                    _dailyLocks.Add((npcName, type));
                }
                else
                {
                    var key = npcName + "|" + type;
                    _transientExpiry[key] = NowProvider() + 120;
                }

                return true;
            }
        }
        catch
        {
            return false;
        }
    }

    /// <summary>仅清 Continuous 当日锁与过期 Transient</summary>
    internal static void ResetDaily()
    {
        try
        {
            lock (_lock)
            {
                _dailyLocks.Clear();
                int now = NowProvider();
                foreach (var key in _transientExpiry.Where(kv => kv.Value <= now).Select(kv => kv.Key).ToList())
                {
                    _transientExpiry.Remove(key);
                }
            }
        }
        catch { /* 清理失败不抛出 */ }
    }

    /// <summary>清空全部</summary>
    internal static void ClearAll()
    {
        try
        {
            lock (_lock)
            {
                _dailyLocks.Clear();
                _transientExpiry.Clear();
            }
        }
        catch { /* 清理失败不抛出 */ }
    }
}
