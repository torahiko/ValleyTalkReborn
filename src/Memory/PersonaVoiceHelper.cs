using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace ValleytalkReborn;

/// <summary>
/// persona 语音特征切片（FEAT-MEM-300-T13）：从 BehavioralRules.Description 正则抽取 [VOICE]/[SPEECH] 两段，
/// 从 bio.Biography 独立字段抽取 [WHO] 首块，叠加当前阶段文本 [STAGE]，组装定调短片段。
/// 纯文本处理，无 Game1/StardewValley 依赖、无状态、零外部依赖。
/// </summary>
internal static class PersonaVoiceHelper
{
    /// <summary>
    /// 从 BehavioralRules.Description 与 bio.Biography 独立字段组装定调切片。
    /// [VOICE] ← BehavioralRules VOICE 段（maxLines 3 / maxChars 240）；
    /// [SPEECH] ← BehavioralRules SPEECH PATTERNS 段（maxLines 5 / maxChars 160）；
    /// [WHO] ← bio.Biography 首块（maxLines 8 / maxChars 400）；
    /// [STAGE] ← currentStageText（maxLines 2 / maxChars 240）。
    /// 各段独立跳过；全空 → ""。
    /// </summary>
    public static string ExtractVoiceSnippet(string behavioralRulesDesc, string biography, string currentStageText)
    {
        var sections = new List<string>();

        string voice = JoinLines(ExtractSection(behavioralRulesDesc, "VOICE"), maxLines: 3, maxChars: 240);
        if (!string.IsNullOrEmpty(voice))
            sections.Add("[VOICE]\n" + voice);

        string speech = JoinLines(ExtractSection(behavioralRulesDesc, "SPEECH PATTERNS"), maxLines: 5, maxChars: 160);
        if (!string.IsNullOrEmpty(speech))
            sections.Add("[SPEECH]\n" + speech);

        string who = JoinLines(ExtractFirstBlock(biography), maxLines: 8, maxChars: 400);
        if (!string.IsNullOrEmpty(who))
            sections.Add("[WHO]\n" + who);

        string stage = JoinLines((currentStageText ?? "").Trim(), maxLines: 2, maxChars: 240);
        if (!string.IsNullOrEmpty(stage))
            sections.Add("[STAGE]\n" + stage);

        return string.Join("\n\n", sections);
    }

    /// <summary>
    /// 行级完整累加：按 '\n' 拆行 → 每行 Trim() → 依次累加非空行（禁止半行截断），
    /// 行数达 maxLines 或累计字符(含 '\n' 分隔)将超 maxChars 时停止（后续行整行丢弃）；
    /// 无任何非空行 → ""；行间以 '\n' 连接。首行始终保留（首行组完整保留）。
    /// </summary>
    private static string JoinLines(string section, int maxLines, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(section)) return "";

        var lines = section.Split('\n')
            .Select(l => l.Trim())
            .Where(l => !string.IsNullOrEmpty(l))
            .ToList();

        if (lines.Count == 0) return "";

        var sb = new StringBuilder();
        for (int i = 0; i < lines.Count && i < maxLines; i++)
        {
            int projected = sb.Length + lines[i].Length + (sb.Length > 0 ? 1 : 0);
            if (projected > maxChars && sb.Length > 0) break;

            if (sb.Length > 0) sb.Append('\n');
            sb.Append(lines[i]);
        }

        return sb.ToString();
    }

    /// <summary>
    /// 抽取首块：按 '\n' 拆行 → 从第一个非空行开始累加非空行，遇首个空行或文末止 →
    /// 返回块原文（各 Trim 后 '\n' 连接）；全空 → ""。
    /// </summary>
    private static string ExtractFirstBlock(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";

        var lines = text.Split('\n')
            .Select(l => l.Trim())
            .ToList();

        var block = new List<string>();
        bool inBlock = false;
        foreach (var line in lines)
        {
            if (string.IsNullOrEmpty(line))
            {
                if (inBlock) break;
                continue;
            }
            inBlock = true;
            block.Add(line);
        }

        return string.Join("\n", block);
    }

    /// <summary>按 [TAG] 起始、到下一个行首 [ 或绝对文末为止抽取段体；标签必须独立成行，无匹配 → ""。</summary>
    private static string ExtractSection(string text, string tag)
    {
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(tag)) return "";

        // T12：标签必须独立成行（行首 [TAG] 后仅允许行尾空白，随后换行）；段体吃至下一个行首 [ 或绝对文末（\z）
        var match = Regex.Match(
            text,
            $"^\\s*\\[{Regex.Escape(tag)}\\][ \\t]*\\r?\\n(.*?)(?=^\\s*\\[|\\z)",
            RegexOptions.IgnoreCase | RegexOptions.Multiline | RegexOptions.Singleline);

        return match.Success ? match.Groups[1].Value : "";
    }
}
