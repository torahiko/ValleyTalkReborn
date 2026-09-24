using System;
using System.Collections.Concurrent;
using StardewValley;

namespace ValleytalkReborn;

/// <summary>
/// 微社交气泡桥存储（Memory 域）。
/// MicroSocial 直出成功后记录气泡文本，3 秒现实时间窗内供主对话组装点消费（STEP5）。
/// 同 NPC 覆盖旧值；阅后即焚；跨天/过期自动清除。
/// </summary>
internal static class FreshBarkBridgeStore
{
    internal static Func<DateTime> NowProvider { get; set; } = () => DateTime.UtcNow;
    internal static Func<int> DayProvider { get; set; } = () => Game1.Date.TotalDays;

    internal const double TtlSeconds = 3.0;

    private sealed class BridgeEntry
    {
        public string Line;
        public DateTime Timestamp;
        public int SaveDayNumber;
    }

    private static readonly ConcurrentDictionary<string, BridgeEntry> _store =
        new ConcurrentDictionary<string, BridgeEntry>(StringComparer.OrdinalIgnoreCase);

    /// <summary>记录：空白行忽略；同 NPC 覆盖旧值。</summary>
    internal static void Record(string npcName, string line)
    {
        if (string.IsNullOrWhiteSpace(npcName) || string.IsNullOrWhiteSpace(line)) return;

        _store[npcName] = new BridgeEntry
        {
            Line = line,
            Timestamp = NowProvider(),
            SaveDayNumber = DayProvider(),
        };
    }

    /// <summary>消费（阅后即焚）：命中且未过期且跨日未变 → 返回 Line；否则 null。过期/跨日条目移除。</summary>
    internal static string TryConsume(string npcName)
    {
        if (string.IsNullOrWhiteSpace(npcName)) return null;

        if (!_store.TryGetValue(npcName, out var entry)) return null;

        // 过期或跨日 → 移除并返回 null
        if ((NowProvider() - entry.Timestamp).TotalSeconds > TtlSeconds
            || entry.SaveDayNumber != DayProvider()
            || string.IsNullOrWhiteSpace(entry.Line))
        {
            _store.TryRemove(npcName, out _);
            return null;
        }

        // 命中 → 移除（阅后即焚）
        _store.TryRemove(npcName, out _);
        return entry.Line;
    }

    internal static void ClearAll()
    {
        _store.Clear();
    }
}
