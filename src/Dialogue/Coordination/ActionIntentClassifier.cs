// ActionIntentClassifier.cs
// ═══════════════════════════════════════════════════════════════════════════
// PURE ACTION-INTENT CLASSIFIER（票 CTX-003）
// ═══════════════════════════════════════════════════════════════════════════
//
// 从 ContextRouter 抽取的纯动作/导航意图匹配逻辑。无状态、Memory-only：
// 不读取 Game1 / NPC / ModEntry.Config / 任何 Manager，线程安全。
//
// 语义优先级（与迁移前 DetectActionTag 完全一致）：
//   StayHome → AllDayFollow → StopFollow → 否定护栏 → Follow
//   → Forward → Backward → Left → Right → Up → Down
// 否定护栏由 DialogueIntentClassifier.IsNegatedCommand 提供。
//
// GoTo 疑问句抑制：吗 / 呢 / ？ / ? 结尾时不触发（原 ContextRouter
// TryDetectGotoIntentInternal 语义）。
// ═══════════════════════════════════════════════════════════════════════════

#nullable disable

using System;
using System.Text.RegularExpressions;
using ValleytalkReborn.Dialogue.Coordination;

namespace ValleytalkReborn;

// ─────────────────────────────────────────────────────────
// Intent Regex
// ─────────────────────────────────────────────────────────

internal static class IntentRegex
{
    public static readonly Regex Forward = new Regex(
        @"(往|向|朝)前[走挪跨靠]?(一?小?大?步)?|过来|靠近|走近|近一点|朝我走|"
        + @"\bcome\s+(here|closer)\b|\bstep\s+forward\b|\bmove\s+closer\b",
        RegexOptions.Compiled
        | RegexOptions.IgnoreCase
        | RegexOptions.CultureInvariant);

    public static readonly Regex Backward = new Regex(
        @"(往|向|朝)后[退走挪靠]?(一?小?大?步)?|退后|后退|退一点|离远点|"
        + @"\bback\s+up\b|\bstep\s+back\b|\bmove\s+back\b|\bgo\s+backward\b",
        RegexOptions.Compiled
        | RegexOptions.IgnoreCase
        | RegexOptions.CultureInvariant);

    public static readonly Regex Left = new Regex(
        @"(往|向|朝)左[走挪靠]?(一?小?大?步)?|"
        + @"\bgo\s+left\b|\bmove\s+left\b|\bstep\s+left\b|\bto\s+the\s+left\b",
        RegexOptions.Compiled
        | RegexOptions.IgnoreCase
        | RegexOptions.CultureInvariant);

    public static readonly Regex Right = new Regex(
        @"(往|向|朝)右[走挪靠]?(一?小?大?步)?|"
        + @"\bgo\s+right\b|\bmove\s+right\b|\bstep\s+right\b|\bto\s+the\s+right\b",
        RegexOptions.Compiled
        | RegexOptions.IgnoreCase
        | RegexOptions.CultureInvariant);

    public static readonly Regex Up = new Regex(
        @"(往|向|朝)上[走挪靠]?(一?小?大?步)?|"
        + @"\bgo\s+up\b|\bmove\s+up\b|\bstep\s+up\b",
        RegexOptions.Compiled
        | RegexOptions.IgnoreCase
        | RegexOptions.CultureInvariant);

    public static readonly Regex Down = new Regex(
        @"(往|向|朝)下[走挪靠]?(一?小?大?步)?|"
        + @"\bgo\s+down\b|\bmove\s+down\b|\bstep\s+down\b",
        RegexOptions.Compiled
        | RegexOptions.IgnoreCase
        | RegexOptions.CultureInvariant);

    public static readonly Regex Follow = new Regex(
        @"(跟着我|跟我走|跟上我|一起走|跟我来|"
        + @"\bfollow me\b|\bcome with me\b|\bstay with me\b)",
        RegexOptions.Compiled
        | RegexOptions.IgnoreCase
        | RegexOptions.CultureInvariant);

    public static readonly Regex StopFollow = new Regex(
        @"(别跟了|不要跟了|不要跟着我|不要跟着|停止跟随|取消跟随|不用跟了|别跟着我|别跟着|不用跟着|回去吧|你走吧|"
        + @"\bstop following( me)?\b|\bdon'?t follow( me)?\b|\bgo back\b|\byou can go now\b|\bstay here\b)",
        RegexOptions.Compiled
        | RegexOptions.IgnoreCase
        | RegexOptions.CultureInvariant);

    public static readonly Regex StayHome = new Regex(
        @"(哪里也别去|今天别出门|今天留家|待在家里|不要出门|"
        + @"stay home|don't go out|stay inside)",
        RegexOptions.Compiled
        | RegexOptions.IgnoreCase
        | RegexOptions.CultureInvariant);

    public static readonly Regex AllDayFollow = new Regex(
        @"(陪我一整天|陪着我一整天|陪我一天|陪着我一天|陪我全天|"
        + @"今天一直陪我|今天全程陪我|跟我一整天|跟着我一整天|"
        + @"accompany me all day|accompany me today|"
        + @"stay with me all day|stay with me today)",
        RegexOptions.Compiled
        | RegexOptions.IgnoreCase
        | RegexOptions.CultureInvariant);

    public static bool IsAnyAction(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        return Forward.IsMatch(text)
            || Backward.IsMatch(text)
            || Left.IsMatch(text)
            || Right.IsMatch(text)
            || Up.IsMatch(text)
            || Down.IsMatch(text)
            || StopFollow.IsMatch(text)
            || Follow.IsMatch(text)
            || StayHome.IsMatch(text)
            || AllDayFollow.IsMatch(text);
    }

    // 兼容旧代码
    public static bool IsAnyMovement(string text)
    {
        return IsAnyAction(text);
    }
}

// ─────────────────────────────────────────────────────────
// ActionIntentClassifier
// ─────────────────────────────────────────────────────────

internal static class ActionIntentClassifier
{
    private static readonly char[] PunctuationTrimChars =
    {
        ' ', '\t', '\n', '\r',
        ',', '.', '!', '?', ';', ':',
        '，', '。', '！', '？', '；', '：', '、',
        '~', '～',
        '“', '”', '"',
        '‘', '’', '\'',
        '(', ')', '（', '）'
    };

    private static readonly char[] WordSeparators =
    {
        ' ', ',', '.', '!', '?', ';', ':',
        '\t', '\n', '\r',
        '，', '。', '！', '？', '；', '：', '、',
        '(', ')', '（', '）'
    };

    // 这里保留相对明确的 GoTo 触发词。
    // “你去”“过去”“去一下”“帮我拿”等模糊词已移除，避免误判。
    private static readonly string[] GotoTriggerZh =
    {
        "你可以去",
        "你能去",
        "走到",
        "移动到",
        "前往",
        "去那个",
        "去那棵",
        "去那里",
        "走过去"
    };

    private static readonly string[] GotoTriggerEn =
    {
        "can you go to",
        "go to the",
        "walk to",
        "move to",
        "head to",
        "go over to"
    };

    internal static ActionTag Detect(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return ActionTag.None;

        string stripped = input.Trim(PunctuationTrimChars);

        // 语义优先级：
        // StayHome / AllDayFollow
        // → StopFollow（在否定词检查之前，确保"别跟着我"等能正常命中）
        // → 否定词拦截（仅影响 Follow 及方向移动）
        // → Follow
        // → 方向移动
        if (IntentRegex.StayHome.IsMatch(stripped))
            return ActionTag.StayHome;

        if (IntentRegex.AllDayFollow.IsMatch(stripped))
            return ActionTag.AllDayFollow;

        if (IntentRegex.StopFollow.IsMatch(stripped))
            return ActionTag.StopFollow;

        // 只有明显以否定词开头时才拦截后续动作。
        // 例如"不要亲我""别往前走"不会触发动作。
        // StopFollow 已在上方处理，不受此守卫影响。
        if (DialogueIntentClassifier.IsNegatedCommand(input))
            return ActionTag.None;

        if (IntentRegex.Follow.IsMatch(stripped))
            return ActionTag.Follow;

        if (IntentRegex.Forward.IsMatch(stripped))
            return ActionTag.StepForward;

        if (IntentRegex.Backward.IsMatch(stripped))
            return ActionTag.StepBackward;

        if (IntentRegex.Left.IsMatch(stripped))
            return ActionTag.StepLeft;

        if (IntentRegex.Right.IsMatch(stripped))
            return ActionTag.StepRight;

        if (IntentRegex.Up.IsMatch(stripped))
            return ActionTag.StepUp;

        if (IntentRegex.Down.IsMatch(stripped))
            return ActionTag.StepDown;

        return ActionTag.None;
    }

    internal static bool TryDetectGoto(string input, out string intentText)
    {
        intentText = string.Empty;

        if (string.IsNullOrWhiteSpace(input))
            return false;

        // 避免明显疑问句或叙述句触发 GoTo。
        if (input.EndsWith("吗", StringComparison.Ordinal)
            || input.EndsWith("呢", StringComparison.Ordinal)
            || input.EndsWith("？", StringComparison.Ordinal)
            || input.EndsWith("?", StringComparison.Ordinal))
        {
            return false;
        }

        if (ContainsAny(input, GotoTriggerZh)
            || MatchesWordBoundaryAny(input, GotoTriggerEn))
        {
            intentText = input;
            return true;
        }

        return false;
    }

    private static bool ContainsAny(string input, string[] keywords)
    {
        if (string.IsNullOrEmpty(input)
            || keywords == null)
        {
            return false;
        }

        foreach (string keyword in keywords)
        {
            if (!string.IsNullOrEmpty(keyword)
                && input.IndexOf(
                    keyword,
                    StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    private static bool MatchesWordBoundaryAny(
        string input,
        params string[] keywords)
    {
        if (string.IsNullOrWhiteSpace(input))
            return false;

        string[] tokens = input.Split(
            WordSeparators,
            StringSplitOptions.RemoveEmptyEntries);

        foreach (string keyword in keywords)
        {
            if (string.IsNullOrWhiteSpace(keyword))
                continue;

            if (keyword.IndexOf(' ') >= 0)
            {
                if (input.IndexOf(
                    keyword,
                    StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }

                continue;
            }

            foreach (string token in tokens)
            {
                if (string.Equals(
                    token,
                    keyword,
                    StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
