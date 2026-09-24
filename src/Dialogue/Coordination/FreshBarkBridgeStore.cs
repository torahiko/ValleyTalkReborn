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

    /// <summary>
    /// 纯渲染（可单测）：逐字采用双语模板，{line} 为桥行原文。
    /// </summary>
    internal static string RenderBridgeBlock(string line, bool isZh)
    {
        if (isZh)
        {
            return $"<fresh_bark_bridge>\n" +
                   $"[刚才你说的那句话]\n" +
                   $"你几秒前刚抬起头，对面前的农夫说了：\n" +
                   $"-\"{line}\"\n" +
                   $"[语境引导]\n" +
                   $"农夫现在就站在你面前，很可能正是听到这句话才停下脚步的。自然承接你刚才的话头，\n" +
                   $"或直接回应农夫的来意；不要逐字重复上面那句话，也不要解释你为什么这么说。\n" +
                   $"</fresh_bark_bridge>";
        }

        return $"<fresh_bark_bridge>\n" +
               $"[WHAT YOU JUST SAID]\n" +
               $"Moments ago you looked up and said to the farmer:\n" +
               $"-\"{line}\"\n" +
               $"[CONTEXT GUIDANCE]\n" +
               $"The farmer is standing right in front of you — quite possibly because they heard it.\n" +
               $"Pick up where you left off naturally, or respond to what the farmer wants;\n" +
               $"do not repeat the line verbatim and do not explain yourself.\n" +
               $"</fresh_bark_bridge>";
    }

    /// <summary>
    /// 组合入口（唯一生产调用方 = LlmDialogueService）：消费 + 渲染；任何失败 → null（D9）。
    /// </summary>
    internal static string BuildBridgeBlock(string npcName)
    {
        string line = TryConsume(npcName);
        if (string.IsNullOrWhiteSpace(line)) return null;

        try
        {
            bool isZh = LocalizedContentManager.CurrentLanguageCode == LocalizedContentManager.LanguageCode.zh;
            return RenderBridgeBlock(line, isZh);
        }
        catch { return null; }
    }
}
