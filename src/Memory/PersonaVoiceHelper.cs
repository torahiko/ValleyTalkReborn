using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace ValleytalkReborn;

/// <summary>
/// persona 语音特征切片（FEAT-MEM-300-T7）：从 BehavioralRules.Description 正则抽取四段（VOICE/SPEECH PATTERNS/BIOGRAPHY）+ 当前阶段文本，
/// 组装定调短片段。纯文本处理，无 Game1/StardewValley 依赖、无状态。
/// </summary>
internal static class PersonaVoiceHelper
{
    /// <summary>从 BehavioralRules.Description 提取四段并组装定调切片；无任何匹配 → ""。</summary>
    public static string ExtractVoiceSnippet(string behavioralRulesDesc, string currentStageText)
    {
        if (string.IsNullOrWhiteSpace(behavioralRulesDesc)) return "";

        var sb = new StringBuilder();

        string voice = ExtractSection(behavioralRulesDesc, "VOICE").Trim();
        if (!string.IsNullOrEmpty(voice))
        {
            voice = MemoryManager.SmartTruncate(voice, 60);
            if (!string.IsNullOrEmpty(voice))
                sb.Append("[VOICE] ").AppendLine(voice);
        }

        string speech = ExtractSection(behavioralRulesDesc, "SPEECH PATTERNS").Trim();
        if (!string.IsNullOrEmpty(speech))
        {
            speech = MemoryManager.SmartTruncate(speech, 60);
            if (!string.IsNullOrEmpty(speech))
            {
                if (sb.Length > 0) sb.AppendLine();
                sb.Append("[SPEECH] ").Append(speech);
            }
        }

        string bio = ExtractSection(behavioralRulesDesc, "BIOGRAPHY");
        if (!string.IsNullOrWhiteSpace(bio))
        {
            var firstLine = bio
                .Split('\n')
                .Select(l => l.Trim())
                .FirstOrDefault(l => !string.IsNullOrEmpty(l));
            if (firstLine != null)
            {
                string bioSnippet = MemoryManager.SmartTruncate(firstLine, 60);
                if (!string.IsNullOrEmpty(bioSnippet))
                {
                    if (sb.Length > 0) sb.AppendLine();
                    sb.Append("[WHO] ").Append(bioSnippet);
                }
            }
        }

        string stage = (currentStageText ?? "").Trim();
        if (!string.IsNullOrEmpty(stage))
        {
            stage = MemoryManager.SmartTruncate(stage, 40);
            if (!string.IsNullOrEmpty(stage))
            {
                if (sb.Length > 0) sb.AppendLine();
                sb.Append("[STAGE] ").Append(stage);
            }
        }

        return sb.ToString();
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
