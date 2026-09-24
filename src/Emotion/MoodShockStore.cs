using System;
using System.Collections.Generic;
using System.Linq;
using StardewValley;

namespace ValleytalkReborn;

public sealed class MoodShock
{
    public string SourceId { get; set; } = "";
    public float DValence { get; set; }
    public float DArousal { get; set; }
    public float DOpenness { get; set; }
    public int StartGameMinutes { get; set; }
    public int DurationMinutes { get; set; }
    public bool PersistAcrossDays { get; set; }

    public float GetCurrentRatio(int nowMinutes)
    {
        int elapsed = nowMinutes - StartGameMinutes;
        if (elapsed < 0) elapsed = 0; // 读档倒流防护
        if (elapsed >= DurationMinutes) return 0f;
        return 1f - (float)elapsed / DurationMinutes;
    }
}

public static class MoodShockStore
{
    private static readonly Dictionary<string, List<MoodShock>> _shocks = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object _lock = new();

    internal static Func<int> NowProvider { get; set; } = DefaultNowProvider;

    private static int DefaultNowProvider()
    {
        // 游戏线性分钟轴：TotalDays * 1200 + 当日分钟（6:00 起点，负值钳为 0）
        int dayMinutes = Math.Max(0, (Game1.timeOfDay / 100 - 6) * 60 + Game1.timeOfDay % 100);
        return Game1.Date.TotalDays * 1200 + dayMinutes;
    }

    public static void AddShock(string npcName, string sourceId, float dv, float da, float dOpen, int durationMinutes, bool persistAcrossDays = false)
    {
        if (string.IsNullOrWhiteSpace(npcName)) return;
        if (string.IsNullOrWhiteSpace(sourceId)) return;

        int now = NowProvider();
        var shock = new MoodShock
        {
            SourceId = sourceId,
            DValence = dv,
            DArousal = da,
            DOpenness = dOpen,
            StartGameMinutes = now,
            DurationMinutes = durationMinutes,
            PersistAcrossDays = persistAcrossDays
        };

        lock (_lock)
        {
            if (!_shocks.TryGetValue(npcName, out var list))
            {
                list = new List<MoodShock>();
                _shocks[npcName] = list;
            }
            // 同 SourceId 先移除后添加 = 同槽位覆盖（OrdinalIgnoreCase）
            list.RemoveAll(s => string.Equals(s.SourceId, sourceId, StringComparison.OrdinalIgnoreCase));
            list.Add(shock);
        }
    }

    public static (float v, float a, float o) GetAggregatedDeltas(string npcName)
    {
        if (string.IsNullOrWhiteSpace(npcName)) return (0f, 0f, 0f);

        int now = NowProvider();

        lock (_lock)
        {
            if (!_shocks.TryGetValue(npcName, out var list) || list.Count == 0)
                return (0f, 0f, 0f);

            float v = 0f, a = 0f, o = 0f;

            list.RemoveAll(s =>
            {
                // 惰性清除：DurationMinutes <= 0 或已过期
                if (s.DurationMinutes <= 0) return true;
                if (now - s.StartGameMinutes >= s.DurationMinutes) return true;
                float ratio = s.GetCurrentRatio(now);
                v += s.DValence * ratio;
                a += s.DArousal * ratio;
                o += s.DOpenness * ratio;
                return false;
            });

            if (list.Count == 0)
                _shocks.Remove(npcName);

            return (v, a, o);
        }
    }

    public static void DampenShock(string npcName, string sourceId, int remainingMinutes)
    {
        if (string.IsNullOrWhiteSpace(npcName)) return;
        if (string.IsNullOrWhiteSpace(sourceId)) return;

        int now = NowProvider();

        lock (_lock)
        {
            if (!_shocks.TryGetValue(npcName, out var list)) return;
            var shock = list.FirstOrDefault(s =>
                string.Equals(s.SourceId, sourceId, StringComparison.OrdinalIgnoreCase));
            if (shock == null) return;

            shock.StartGameMinutes = now;
            shock.DurationMinutes = remainingMinutes;
            shock.PersistAcrossDays = false;
        }
    }

    public static void OnDayStarted()
    {
        lock (_lock)
        {
            var emptyKeys = new List<string>();
            foreach (var kvp in _shocks)
            {
                kvp.Value.RemoveAll(s => !s.PersistAcrossDays);
                if (kvp.Value.Count == 0)
                    emptyKeys.Add(kvp.Key);
            }
            foreach (var key in emptyKeys)
                _shocks.Remove(key);
        }
    }

    public static void ClearAll()
    {
        lock (_lock)
        {
            _shocks.Clear();
        }
    }

    public static void ClearNpc(string npcName)
    {
        if (string.IsNullOrWhiteSpace(npcName)) return;
        lock (_lock)
        {
            _shocks.Remove(npcName);
        }
    }

    public static int CountShocks(string npcName)
    {
        if (string.IsNullOrWhiteSpace(npcName)) return 0;
        lock (_lock)
        {
            if (!_shocks.TryGetValue(npcName, out var list)) return 0;
            return list.Count;
        }
    }

    public static void OnGiftDelivered(string npcName, int giftTasteCategory)
    {
        if (string.IsNullOrWhiteSpace(npcName)) return;

        string tasteKey;
        float dv, da, dOpen;

        switch (giftTasteCategory)
        {
            case NPC.gift_taste_love:
                tasteKey = "Loved";
                dv = 0.50f; da = 0f; dOpen = 0.20f;
                break;
            case NPC.gift_taste_like:
                tasteKey = "Liked";
                dv = 0.20f; da = 0f; dOpen = 0.10f;
                break;
            case NPC.gift_taste_dislike:
                tasteKey = "Disliked";
                dv = -0.30f; da = 0f; dOpen = -0.15f;
                break;
            case NPC.gift_taste_hate:
                tasteKey = "Hated";
                dv = -0.50f; da = 0f; dOpen = -0.30f;
                break;
            default:
                // neutral/未知 → return 不加 Shock
                return;
        }

        string sourceId = EmotionShockIds.Gift(npcName, tasteKey);
        AddShock(npcName, sourceId, dv, da, dOpen, durationMinutes: 240, persistAcrossDays: false);
    }
}
