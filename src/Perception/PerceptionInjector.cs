using System.Collections.Generic;
using System.Linq;
using StardewValley;

namespace ValleytalkReborn;

/// <summary>
/// Builds and injects perception text into the NPC system prompt.
///
/// Two independent sections:
///   Section 1 — [Town Gossip] from Track 1 (_globalGossip, max 2)
///   Section 2 — [NPC's Recent Observations] from Track 2 (_farmerBucket, eyewitness-filtered, max 3)
///
/// Each section is only emitted when it has content. Neither is required.
/// </summary>
internal static class PerceptionInjector
{
    public static string BuildPerceptionText(string npcName)
    {
        if (string.IsNullOrEmpty(npcName)) return string.Empty;

        string gossipBlock = BuildGossipBlock();
        string localBlock  = BuildLocalBlock(npcName);

        if (string.IsNullOrEmpty(gossipBlock) && string.IsNullOrEmpty(localBlock))
            return string.Empty;

        var parts = new List<string>();
        if (!string.IsNullOrEmpty(gossipBlock)) parts.Add(gossipBlock);
        if (!string.IsNullOrEmpty(localBlock))  parts.Add(localBlock);
        return string.Join("\n\n", parts);
    }

    public static void Inject(string npcName, Prompts prompts)
    {
        if (prompts == null || string.IsNullOrEmpty(npcName)) return;

        string text = BuildPerceptionText(npcName);
        if (string.IsNullOrEmpty(text)) return;

        prompts.CorePrompt += "\n\n" + text;
    }

    // ─────────────────────────────────────────────
    //  Section 1: Town Gossip
    // ─────────────────────────────────────────────

    private static string BuildGossipBlock()
    {
        var snapshots = PerceptionManager.Instance.GetGossipSnapshots();
        if (snapshots == null || snapshots.Count == 0) return string.Empty;

        var lines = new List<string>
        {
            "[Town Gossip] (Recent town-wide events you have heard about. " +
            "Mention them naturally only if they fit the conversation — do NOT list them mechanically.)"
        };

        foreach (var p in snapshots)
        {
            if (p == null || string.IsNullOrWhiteSpace(p.Template)) continue;
            lines.Add($"- {p.Template}");
        }

        return lines.Count > 1 ? string.Join("\n", lines) : string.Empty;
    }

    // ─────────────────────────────────────────────
    //  Section 2: Personal eyewitness observations
    // ─────────────────────────────────────────────

    private static string BuildLocalBlock(string npcName)
    {
        var perceptions = PerceptionManager.Instance.GetFilteredBucketFor(npcName, 3);
        if (perceptions == null || perceptions.Count == 0) return string.Empty;

        var lines = new List<string>
        {
            "[NPC's Recent Observations] (Instruction: Choose at most ONE interesting event " +
            "from the list below to mention naturally ONLY IF it fits the current context " +
            "and your personality. Do NOT list them mechanically.)"
        };

        foreach (var p in perceptions)
        {
            if (p == null) continue;

            string line = $"- {p.Template}";

            if ((p.Key == "Eat" || p.Key == "Gift") && !string.IsNullOrEmpty(p.ItemId))
                line += BuildGiftTasteAnnotation(npcName, p.ItemId);

            lines.Add(line);
        }

        return lines.Count > 1 ? string.Join("\n", lines) : string.Empty;
    }

    private static string BuildGiftTasteAnnotation(string npcName, string itemId)
    {
        try
        {
            var npc = Game1.getCharacterFromName(npcName);
            if (npc == null) return string.Empty;

            var item = ItemRegistry.Create(itemId);
            if (item == null) return string.Empty;

            return npc.getGiftTasteForThisItem(item) switch
            {
                NPC.gift_taste_love =>
                    // Neutral fact — attitude is determined by the NPC's long-term traits, not forced here.
                    " (Note: This is one of your favorite items. " +
                    "How you react depends on your current feelings toward the farmer.)",
                NPC.gift_taste_hate =>
                    // Neutral fact — attitude is determined by the NPC's long-term traits, not forced here.
                    " (Note: You normally dislike this item. " +
                    "How you react depends on your current feelings toward the farmer.)",
                _ => string.Empty
            };
        }
        catch
        {
            return string.Empty;
        }
    }
}
