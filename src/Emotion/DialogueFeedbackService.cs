using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using StardewModdingAPI;

namespace ValleytalkReborn;

public static class DialogueFeedbackService
{
    // 词边界防 $h 误匹配噪声（如 $house）。
    // 不用 \b：.NET \w 含 CJK 字符，"$h你好" 会被 \b 误杀；改用 ASCII 字母负向前瞻。
    private static readonly Regex PortraitCodeRegex = new(@"\$([hsaul0])(?![a-zA-Z])", RegexOptions.Compiled);

    /// <summary>
    /// 提取 rawText 中最后一个合法肖像码（pure）。
    /// $0 非原版肖像：白名单缺失或不含 "$0" 时降级 null（default）。
    /// </summary>
    public static string ExtractPortraitCode(string rawText, IReadOnlyList<string> validPortraits)
    {
        if (string.IsNullOrEmpty(rawText)) return null;

        Match last = null;
        foreach (Match m in PortraitCodeRegex.Matches(rawText))
        {
            last = m;
        }
        if (last == null) return null;

        string code = "$" + last.Groups[1].Value;

        if (code == "$0")
        {
            if (validPortraits == null) return null;
            if (!validPortraits.Contains("$0")) return null;
        }

        return code;
    }

    /// <summary>
    /// 肖像反馈判定（pure）。kind 值域 {"Comforted","Neutralized","Saddened","Agitated"}，
    /// 完整 SourceId 由调用方以 EmotionShockIds.Dialogue(npc, kind) 拼装。
    /// </summary>
    public static (string kind, float dv, float da, float dOpen, int minutes, bool consumeLock)? DecideFeedback(
        EmotionSnapshot snapshot, string portraitCode)
    {
        if (snapshot.Valence >= -0.25f) return null;

        switch (portraitCode)
        {
            case "$h":
                return ("Comforted", 0.35f, -0.10f, 0.30f, 240, true);
            case "$0":
                return ("Neutralized", 0.15f, 0f, 0.10f, 120, true);
            case "$s":
                return ("Saddened", -0.15f, 0f, -0.15f, 120, true);
            case "$a":
                if (snapshot.Valence < -0.40f) return null; // 极端负面归因豁免，不消耗当日锁
                return ("Agitated", -0.20f, 0.25f, -0.20f, 120, true);
            default:
                return null;
        }
    }

    /// <summary>
    /// 肖像反馈评估与落库（主线程续体）。天级锁 FeedbackTriggeredToday 守卫。
    /// </summary>
    public static void EvaluateDialogueFeedback(Character character, EmotionSnapshot snapshot, string portraitCode)
    {
        if (character == null || character.FeedbackTriggeredToday) return;

        var decision = DecideFeedback(snapshot, portraitCode);
        if (decision == null)
        {
            ModEntry.SMonitor?.Log(
                $"[EmotionFeedback] {character.Name} 未触发肖像反馈 (code: {portraitCode ?? "null"})。",
                LogLevel.Trace);
            return;
        }

        MoodShockStore.AddShock(
            character.Name,
            EmotionShockIds.Dialogue(character.Name, decision.Value.kind),
            decision.Value.dv, decision.Value.da, decision.Value.dOpen,
            decision.Value.minutes,
            persistAcrossDays: false);

        if (decision.Value.consumeLock)
        {
            character.FeedbackTriggeredToday = true;
            ModEntry.SMonitor?.Log(
                $"[EmotionFeedback] {character.Name} 触发肖像反馈: {portraitCode} | 原始V: {snapshot.Valence:F2} | 天级锁已置位",
                LogLevel.Debug);
        }
    }
}
