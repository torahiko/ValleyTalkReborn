using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using StardewValley;

namespace ValleytalkReborn;

/// <summary>
/// 近期互动余韵（Immediate Echo）暂存区：
/// 在 A2A 会话正常收尾 / Bark 自然收尾（FinalizeThreadLocked）时，
/// 把最后一小段现场台词暂存下来，供玩家紧接着点开该 NPC 主对话时，
/// 作为一次性的 CorePrompt 注入，还原 "刚聊完/刚嘀咕完" 的现场感。
///
/// 存储模型：以 NPC 名字为单一 Key 的扁平字典。
/// A2A 场景下，同一份台词会为每个参与者各自克隆一份 Entry、各自
/// 独立注册——这样玩家先点开参与者 A 消费一次，转身再点开参与者 B
/// 时，B 的 Entry 完全不受 A 消费与否影响（不同 Key，互不干扰）。
///
/// 消费即移除：BuildEchoBlock 一旦成功提取内容，立刻将该 NPC 的
/// 条目移出字典，天然保证 "每个 NPC 最多注入一次"。
///
/// 只在正常收尾时记录，被打断/取消不记录——理由见调用方注释：
/// 玩家插话打断 A2A、或 A2A 锁定打断 Bark，体验本质都是 "注意力被
/// 现场事件强行拉走"，此时注入 "脑海中的余温" 反而制造记忆倒错。
///
/// 过期判定采用懒清理（lazy clean）：不开定时器，只在 BuildEchoBlock
/// 被调用时顺手过一遍全表。NPC 总数不超过数十个，这个开销可忽略。
/// </summary>
internal static class ImmediateEchoStore
{
    /// <summary>
    /// 物理时间 TTL：超过这个时长（秒），无论游戏内时间是否推进，
    /// 都视为 "注意力早已转移"，作废丢弃。
    /// </summary>
    private const int TTL_SECONDS = 90;

    internal sealed class EchoEntry
    {
        /// <summary>"a2a" 或 "bark"。</summary>
        public string EchoType { get; set; }
        /// <summary>记录时刻的真实时钟时间（UTC），用于 TTL 判定。</summary>
        public DateTime Timestamp { get; set; }
        /// <summary>记录时刻的游戏内时间（HHmm 格式）。</summary>
        public int GameTimeOfDay { get; set; }
        /// <summary>记录时刻的存档天数，用于跨天失效判定。</summary>
        public int SaveDayNumber { get; set; }
        /// <summary>记录时刻 NPC 所在地图内部名，用于同地图校验。</summary>
        public string LocationName { get; set; }
        /// <summary>
        /// 台词列表。A2A 场景 Speaker 为发言者的内部（英文）名字，
        /// 渲染时按当前语言本地化；Bark 场景 Speaker 恒为 null。
        /// </summary>
        public List<(string Speaker, string Line)> Lines { get; set; }
        public bool Consumed { get; set; }
    }

    private static readonly ConcurrentDictionary<string, EchoEntry> _store =
        new ConcurrentDictionary<string, EchoEntry>(StringComparer.OrdinalIgnoreCase);

    private static bool IsChineseLanguage =>
        LocalizedContentManager.CurrentLanguageCode == LocalizedContentManager.LanguageCode.zh;

    /// <summary>
    /// 记录一次 A2A 会话正常收尾时的最后台词，为每个参与者各自
    /// 独立注册一份。
    /// </summary>
    internal static void RecordA2A(
        IEnumerable<string> participantNames,
        IEnumerable<(string Speaker, string Line)> lines,
        string locationName)
    {
        if (participantNames == null) return;
        var lineList = lines?
            .Where(l => !string.IsNullOrWhiteSpace(l.Line))
            .ToList();
        if (lineList == null || lineList.Count == 0) return;

        int day = SafeCurrentDay();
        int timeOfDay = Game1.timeOfDay;
        var now = DateTime.UtcNow;

        foreach (var name in participantNames)
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            _store[name] = new EchoEntry
            {
                EchoType = "a2a",
                Timestamp = now,
                GameTimeOfDay = timeOfDay,
                SaveDayNumber = day,
                LocationName = locationName,
                Lines = new List<(string, string)>(lineList),
                Consumed = false
            };
        }
    }

    /// <summary>
    /// 记录一次 Bark 自然收尾时的最后台词（无 speaker，单人自语）。
    /// </summary>
    internal static void RecordBark(string npcName, IEnumerable<string> tailLines, string locationName)
    {
        if (string.IsNullOrWhiteSpace(npcName)) return;
        var lineList = tailLines?
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => ((string)null, l))
            .ToList();
        if (lineList == null || lineList.Count == 0) return;

        _store[npcName] = new EchoEntry
        {
            EchoType = "bark",
            Timestamp = DateTime.UtcNow,
            GameTimeOfDay = Game1.timeOfDay,
            SaveDayNumber = SafeCurrentDay(),
            LocationName = locationName,
            Lines = lineList,
            Consumed = false
        };
    }

    /// <summary>
    /// 供 LlmDialogueService 在组装 CorePrompt 时调用。
    /// 命中且有效则渲染为 XML 块并立即从字典移除（一次性消费）；
    /// 否则返回 null（调用方不应追加任何内容）。
    /// </summary>
    internal static string BuildEchoBlock(string npcName, string currentLocationName)
    {
        if (string.IsNullOrWhiteSpace(npcName)) return null;
        CleanupExpired();

        if (!_store.TryGetValue(npcName, out var entry))
            return null;

        // 无论有效与否，命中后都从字典移除——过期/错位的条目也不该
        // 继续占位等待下一次访问。
        _store.TryRemove(npcName, out _);

        if (entry.Consumed || IsExpired(entry) || !LocationMatches(entry, currentLocationName))
            return null;

        return RenderEcho(entry);
    }

    /// <summary>
    /// 换天 / 返回标题时整表清空，避免跨存档残留。
    /// </summary>
    internal static void Clear()
    {
        _store.Clear();
    }

    private static void CleanupExpired()
    {
        foreach (var kv in _store)
        {
            if (IsExpired(kv.Value))
                _store.TryRemove(kv.Key, out _);
        }
    }

    private static bool IsExpired(EchoEntry entry)
    {
        if ((DateTime.UtcNow - entry.Timestamp).TotalSeconds > TTL_SECONDS)
            return true;
        if (entry.SaveDayNumber != SafeCurrentDay())
            return true;
        return false;
    }

    private static bool LocationMatches(EchoEntry entry, string currentLocationName)
    {
        // 优先精确匹配：NPC 和玩家在同一地图
        if (!string.IsNullOrEmpty(entry.LocationName) && !string.IsNullOrEmpty(currentLocationName))
        {
            if (string.Equals(entry.LocationName, currentLocationName, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        // 回退：Location 缺失时允许跨地图消费（NPC 可能在请求发出后移动了地图）
        // 仅在位置信息不完整时生效，避免"精确已知不在同一地图"时的误注入
        if (string.IsNullOrEmpty(entry.LocationName) || string.IsNullOrEmpty(currentLocationName))
            return true;

        return false;
    }

    private static int SafeCurrentDay()
    {
        try
        {
            return Game1.Date?.TotalDays ?? 0;
        }
        catch
        {
            return 0;
        }
    }

    private static string RenderEcho(EchoEntry entry)
    {
        bool isZh = IsChineseLanguage;
        var sb = new StringBuilder();

        if (entry.EchoType == "a2a")
        {
            sb.AppendLine("<recent_interaction_echo type=\"a2a\">");
            sb.AppendLine(isZh ? "[刚才的交谈动静]" : "[Moments Ago: A Conversation]");
            sb.AppendLine(isZh
                ? "你方才正与身旁的伙伴闲聊，对话刚好停在："
                : "You were just chatting with someone nearby. The conversation had just paused here:");
            foreach (var (speaker, line) in entry.Lines)
            {
                string dn = ResolveSpeakerDisplayName(speaker, isZh);
                sb.AppendLine(isZh ? $"- {dn}：\u201c{line}\u201d" : $"- {dn}: \"{line}\"");
            }
            sb.AppendLine(isZh ? "[语境引导]" : "[Context Guidance]");
            sb.AppendLine(isZh
                ? "这是刚才留在你脑海中的现场余温。你可以顺着这个状态自然抬起头回应农夫；若农夫主动提起相关话题，可自然顺接，否则专注于农夫当下的来意。"
                : "This is the lingering warmth of that moment, still in your mind. You may naturally look up and respond to the farmer from this state; if the farmer brings up something related, follow it naturally — otherwise, focus on what the farmer actually wants right now.");
            sb.AppendLine("</recent_interaction_echo>");
        }
        else
        {
            sb.AppendLine("<recent_interaction_echo type=\"bark\">");
            sb.AppendLine(isZh ? "[刚才的自言自语]" : "[Moments Ago: A Private Thought]");
            sb.AppendLine(isZh
                ? "你方才正一个人在手头动静中嘀咕："
                : "You were just muttering to yourself, caught up in whatever you were doing:");
            foreach (var (_, line) in entry.Lines)
                sb.AppendLine(isZh ? $"- \u201c{line}\u201d" : $"- \"{line}\"");
            sb.AppendLine(isZh ? "[语境引导]" : "[Context Guidance]");
            sb.AppendLine(isZh
                ? "这只是你方才自顾自冒出的思绪碎屑。视线此刻已完全转向面前的农夫，仅将此作为当下的情绪底色，优先回应农夫的来意。"
                : "This is only a stray fragment of thought that crossed your mind a moment ago. Your attention is now fully turned toward the farmer; let this subtly tint your mood while prioritizing the farmer's presence.");
            sb.AppendLine("</recent_interaction_echo>");
        }

        return sb.ToString().TrimEnd();
    }

    private static string ResolveSpeakerDisplayName(string speakerInternalName, bool isZh)
    {
        if (string.IsNullOrWhiteSpace(speakerInternalName))
            return isZh ? "对方" : "the other person";
        if (isZh)
            return NpcNameLocalizer.GetZhName(speakerInternalName);
        var npc = Game1.getCharacterFromName(speakerInternalName);
        return npc?.displayName ?? speakerInternalName;
    }
}