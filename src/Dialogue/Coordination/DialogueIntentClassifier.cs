// DialogueIntentClassifier.cs
// ═══════════════════════════════════════════════════════════════════════════
// PURE DIALOGUE-INTENT CLASSIFIER（票 CTX-003）
// ═══════════════════════════════════════════════════════════════════════════
//
// 从 ContextRouter 抽取的纯对话意图匹配逻辑（问候 / 告别 / 否定词）。
// 无状态、Memory-only：不读取 Game1 / NPC / ModEntry.Config / 任何 Manager，
// 线程安全。
//
// 保持迁移前语义：中英文大小写不敏感匹配（中文集合 Ordinal，英文集合
// OrdinalIgnoreCase；否定正则 IgnoreCase）。
// ═══════════════════════════════════════════════════════════════════════════

#nullable disable

using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace ValleytalkReborn.Dialogue.Coordination;

internal static class DialogueIntentClassifier
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

    private static readonly HashSet<string> ExactGreetingsZh =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "早",
            "早啊",
            "早安",
            "早上好",
            "中午好",
            "下午好",
            "晚上好",
            "晚安",
            "你好",
            "你好啊",
            "您好",
            "哈喽",
            "嗨"
        };

    private static readonly HashSet<string> ExactGreetingsEn =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "hi",
            "hello",
            "good morning",
            "good evening",
            "good afternoon",
            "hey",
            "howdy"
        };

    private static readonly HashSet<string> ExactFarewellsZh =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "再见",
            "拜拜",
            "明天见",
            "晚安"
        };

    private static readonly HashSet<string> ExactFarewellsEn =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "bye",
            "goodbye",
            "see you",
            "good night"
        };

    private static readonly Regex NegationRegex = new Regex(
        @"^(不要|别|不想|不必|不用|无需|没必要|"
        + @"don't\b|dont\b|do not\b|not\b|never\b)",
        RegexOptions.Compiled
        | RegexOptions.IgnoreCase
        | RegexOptions.CultureInvariant);

    internal static bool IsSimpleGreeting(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return false;

        string stripped = input.Trim(PunctuationTrimChars);

        if (stripped.Length == 0)
            return false;

        return ExactGreetingsZh.Contains(stripped)
            || ExactGreetingsEn.Contains(stripped)
            || ExactFarewellsZh.Contains(stripped)
            || ExactFarewellsEn.Contains(stripped);
    }

    internal static bool IsFarewell(string input)
    {
        string stripped = input.Trim(PunctuationTrimChars);

        return ExactFarewellsZh.Contains(stripped)
            || ExactFarewellsEn.Contains(stripped);
    }

    internal static bool IsNegatedCommand(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return false;

        string text = input.Trim();
        return NegationRegex.IsMatch(text);
    }
}
