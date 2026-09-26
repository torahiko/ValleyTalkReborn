// RelationshipAttitudeLensBuilder.cs
// VT-SOCIAL-LENS-01 — Player-triggered NPC relationship attitude lens (Tier 2b).
// Memory-only, per-request dynamic evaluation. Inspects only the latest player line;
// emits one compact, explicitly attributed prompt block when it unambiguously
// matches exactly one eligible relationship target. Zero Bio mutations, zero
// persistence, zero Harmony.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace ValleytalkReborn;

internal static class RelationshipAttitudeLensBuilder
{
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

        var matchedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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

            // Blank description → ineligible.
            if (string.IsNullOrWhiteSpace(entry.Description)) continue;

            // 4. Alias set.
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

            // 5. Match against player line.
            if (aliases.Any(a => MatchesAlias(playerLine, a)))
                matchedKeys.Add(key);
        }

        // 6. Strict disambiguation: exactly one distinct relationship key.
        if (matchedKeys.Count != 1) return string.Empty;

        string matchedKey = matchedKeys.First();
        var matchedEntry = bio.Relationships[matchedKey];
        if (matchedEntry == null || string.IsNullOrWhiteSpace(matchedEntry.Description))
            return string.Empty;

        string targetDisplayName = isZh
            ? NpcNameLocalizer.GetZhName(matchedKey)
            : matchedKey;

        string description = isZh
            ? NpcNameLocalizer.LocalizeNamesInText(matchedEntry.Description)
            : matchedEntry.Description;

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
        else
        {
            return "### [REACTIVE SOCIAL LENS: Player mentioned '" + targetDisplayName + "']\n"
                + "<social_lens target=\"" + targetDisplayName + "\">\n"
                + "- Your subjective stance toward them: " + description + "\n"
                + "- Instruction: The player explicitly brought up this person in their latest line. "
                + "Reflect this subjective attitude naturally in your response to project your own mood, "
                + "values, or concerns. Do NOT recite this as an objective biographical entry "
                + "or encyclopedic fact.\n"
                + "</social_lens>";
        }
    }

    private static void AddIfNonEmpty(List<string> list, string value)
    {
        if (!string.IsNullOrWhiteSpace(value)) list.Add(value);
    }

    private static bool MatchesAlias(string text, string alias)
    {
        if (IsAscii(alias))
        {
            // Word-boundary regex prevents substring false positives (Sam/same, Gus/gust, Leo/leopard).
            string pattern = @"\b" + Regex.Escape(alias) + @"\b";
            return Regex.IsMatch(text, pattern, RegexOptions.IgnoreCase);
        }

        // CJK aliases: case-insensitive substring match.
        return text.IndexOf(alias, StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool IsAscii(string value)
    {
        foreach (char c in value)
            if (c > 127) return false;
        return true;
    }
}
