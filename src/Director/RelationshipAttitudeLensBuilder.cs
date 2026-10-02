// RelationshipAttitudeLensBuilder.cs
// VT-SOCIAL-LENS-01 — Player-triggered NPC relationship attitude lens (Tier 2b).
// REL-003 — Reactive enhancement: up to MaxInjectedLenses explicit targets per
// player line, matched by base name aliases OR possessive kinship aliases
// derived from the unified PublicIdentity label (e.g. "grandfather" -> "你爷爷").
// Memory-only, per-request dynamic evaluation. Zero Bio mutations, zero
// persistence, zero Harmony.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using ValleytalkReborn.Dialogue.Coordination;

namespace ValleytalkReborn;

internal static class RelationshipAttitudeLensBuilder
{
    // 预算上限：单轮最多支持 2 个明确目标的即时透镜。
    internal const int MaxInjectedLenses = 2;

    internal static string Build(Character character, DialogueContext context)
    {
        if (character == null) return string.Empty;
        var bio = character.Bio;
        if (bio?.Relationships == null) return string.Empty;
        if (context?.ChatHistory == null) return string.Empty;

        // 1. Only the most recent player line.
        string playerLine = context.ChatHistory.LastOrDefault(e => e.IsPlayerLine == true)?.Text;
        if (string.IsNullOrWhiteSpace(playerLine)) return string.Empty;

        bool isZh = Prompts.PromptsBlocks.IsZh();

        // 2. Speaker self-filter names.
        var speakerNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(character.Name)) speakerNames.Add(character.Name);
        if (!string.IsNullOrEmpty(character.StardewNpc?.displayName))
            speakerNames.Add(character.StardewNpc.displayName);

        int hearts = context.Hearts ?? 0;

        // 命中记录：每个合法目标一条，IndexInText = 该目标全部别名在发言中的首次出现位置。
        var matches = new List<MatchRecord>();

        foreach (var kvp in bio.Relationships)
        {
            string key = kvp.Key;
            var entry = kvp.Value;
            if (entry == null) continue;

            // 2. Speaker self-filter: exclude entries whose key or id is the speaking NPC.
            if (speakerNames.Contains(key)) continue;
            if (!string.IsNullOrEmpty(entry.id) && speakerNames.Contains(entry.id)) continue;

            // 3. Visibility gate (null hearts treated as 0).
            if (hearts < entry.RequiredHearts) continue;

            // Blank description → ineligible (skip this target, others unaffected).
            if (string.IsNullOrWhiteSpace(entry.Description)) continue;

            // 4. Alias set: base names + possessive kinship aliases.
            var aliases = new List<string>();
            AddIfNonEmpty(aliases, key);
            AddIfNonEmpty(aliases, entry.id);
            AddIfNonEmpty(aliases, entry.Heading);

            if (isZh)
            {
                AddIfNonEmpty(aliases, NpcNameLocalizer.GetZhName(key));
                if (!string.IsNullOrEmpty(entry.id))
                    AddIfNonEmpty(aliases, NpcNameLocalizer.GetZhName(entry.id));
            }

            aliases.AddRange(GetPossessiveKinshipAliases(entry.PublicIdentity, isZh));

            // 5. First-occurrence index across all aliases.
            int indexInText = -1;
            foreach (string alias in aliases)
            {
                int aliasIndex = FindAliasIndex(playerLine, alias);
                if (aliasIndex >= 0 && (indexInText < 0 || aliasIndex < indexInText))
                    indexInText = aliasIndex;
            }

            if (indexInText >= 0)
                matches.Add(new MatchRecord(key, entry, indexInText));
        }

        // 6. Budget: order by first occurrence in the utterance (deterministic
        //    ordinal tie-break), dedupe is inherent (one record per relationship
        //    key), then cap at MaxInjectedLenses.
        if (matches.Count == 0) return string.Empty;

        var selected = matches
            .OrderBy(m => m.IndexInText)
            .ThenBy(m => m.Key, StringComparer.Ordinal)
            .Take(MaxInjectedLenses)
            .ToList();

        return string.Join("\n\n", selected.Select(m => BuildLensBlock(m.Key, m.Entry, isZh)));
    }

    // ───────────────────────────────────────────────────────────────────────
    // 所属亲属别名（REL-003）：把统一的 PublicIdentity 英文小写标签映射为
    // "带第二人称所有格" 的召回别名（"你爷爷" / "your grandfather"）。
    // 严格排除第一人称（"我爷爷"）与无所有格泛指（"爷爷/老爷子"）——
    // 泛指多义词绝不绑定特定 NPC，第一人称亲属绝不命中说话者的亲族。
    // 无匹配或空白标签返回空序列。
    // ───────────────────────────────────────────────────────────────────────
    internal static IEnumerable<string> GetPossessiveKinshipAliases(string publicIdentity, bool isZh)
    {
        switch (publicIdentity?.Trim().ToLowerInvariant())
        {
            case "grandfather":
                return isZh
                    ? new[] { "你外公", "你爷爷", "你的外公", "你的爷爷" }
                    : new[] { "your grandfather", "your grandpa" };
            case "grandmother":
                return isZh
                    ? new[] { "你外婆", "你奶奶", "你的外婆", "你的奶奶" }
                    : new[] { "your grandmother", "your grandma" };
            case "father":
                return isZh
                    ? new[] { "你爸", "你父亲", "你的父亲" }
                    : new[] { "your father", "your dad" };
            case "mother":
                return isZh
                    ? new[] { "你妈", "你母亲", "你的母亲" }
                    : new[] { "your mother", "your mom" };
            case "wife":
                return isZh
                    ? new[] { "你妻子", "你老婆", "你媳妇" }
                    : new[] { "your wife" };
            case "husband":
                return isZh
                    ? new[] { "你丈夫", "你老公" }
                    : new[] { "your husband" };
            case "dog":
            case "family dog":
                return isZh
                    ? new[] { "你的狗", "你家的狗" }
                    : new[] { "your dog" };
            default:
                return Array.Empty<string>();
        }
    }

    private static string BuildLensBlock(string matchedKey, BioData.ListEntry entry, bool isZh)
    {
        string targetDisplayName = NpcNameLocalizer.GetLocalizedName(matchedKey);

        string description = isZh
            ? NpcNameLocalizer.LocalizeNamesInText(entry.Description)
            : entry.Description;

        if (isZh)
        {
            return "### [即时社交态度透镜: 农夫提到了【" + targetDisplayName + "】]\n"
                + "<social_lens target=\"" + targetDisplayName + "\">\n"
                + "- 你对此人的固有主观立场与印象：" + description + "\n"
                + "- 回应指引：农夫在刚才的话中明确提到了此人。这是你对该人的主观态度基线，"
                + "请在回应农夫时自然流露这种情感倾向（借此表达你自己的心情、烦恼或观点），"
                + "切勿机械背诵人设资料，严禁当作客观事实宣讲。\n"
                + "</social_lens>";
        }

        return "### [REACTIVE SOCIAL LENS: Player mentioned '" + targetDisplayName + "']\n"
            + "<social_lens target=\"" + targetDisplayName + "\">\n"
            + "- Your subjective stance toward them: " + description + "\n"
            + "- Instruction: The player explicitly brought up this person in their latest line. "
            + "Reflect this subjective attitude naturally in your response to project your own mood, "
            + "values, or concerns. Do NOT recite this as an objective biographical entry "
            + "or encyclopedic fact.\n"
            + "</social_lens>";
    }

    private sealed class MatchRecord
    {
        internal MatchRecord(string key, BioData.ListEntry entry, int indexInText)
        {
            Key = key;
            Entry = entry;
            IndexInText = indexInText;
        }

        internal string Key { get; }
        internal BioData.ListEntry Entry { get; }
        internal int IndexInText { get; }
    }

    private static void AddIfNonEmpty(List<string> list, string value)
    {
        if (!string.IsNullOrWhiteSpace(value)) list.Add(value);
    }

    private static int FindAliasIndex(string text, string alias)
    {
        if (IsAscii(alias))
        {
            // Word-boundary regex prevents substring false positives (Sam/same, Gus/gust, Leo/leopard).
            string pattern = @"\b" + Regex.Escape(alias) + @"\b";
            Match match = Regex.Match(text, pattern, RegexOptions.IgnoreCase);
            return match.Success ? match.Index : -1;
        }

        // CJK aliases: case-insensitive substring match.
        return text.IndexOf(alias, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsAscii(string value)
    {
        foreach (char c in value)
            if (c > 127) return false;
        return true;
    }
}
