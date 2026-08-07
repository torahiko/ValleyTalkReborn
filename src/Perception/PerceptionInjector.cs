using System.Collections.Generic;
using System.Linq;
using StardewValley;

namespace ValleyTalk;

/// <summary>
/// Handles injecting perception data into NPC dialogue prompts.
/// Separated from PerceptionManager to keep concerns clean.
/// </summary>
internal static class PerceptionInjector
{
    /// <summary>
    /// Builds the perception injection text for the system prompt.
    /// Returns empty string if no valid perceptions exist.
    /// </summary>
    public static string BuildPerceptionText(string npcName)
    {
        var perceptions = PerceptionManager.Instance.GetPerceptionsFor(npcName, 3);
        if (!perceptions.Any())
            return string.Empty;

        // English instruction header — universally understood by LLMs regardless of player language
        var lines = new List<string> { "[NPC's Recent Observations] (Instruction: Choose at most ONE interesting event from the list below to mention naturally in your dialogue ONLY IF it fits the current context and NPC's personality. Do NOT list them mechanically.)" };

        foreach (var p in perceptions)
        {
            string line = $"- {p.Template}";

            // Dynamically evaluate NPC gift taste for "Eat" perceptions with a valid item ID.
            if (p.Key == "Eat" && !string.IsNullOrEmpty(p.ItemId))
            {
                try
                {
                    var npc = Game1.getCharacterFromName(npcName);
                    if (npc != null)
                    {
                        var dummyItem = ItemRegistry.Create(p.ItemId);
                        if (dummyItem != null)
                        {
                            int taste = npc.getGiftTasteForThisItem(dummyItem);
                            if (taste == NPC.gift_taste_love)
                                line += " (CRITICAL INSTRUCTION: You ABSOLUTELY LOVE this food! You MUST react excitedly and explicitly comment on the farmer eating it!)";
                            else if (taste == NPC.gift_taste_hate)
                                line += " (CRITICAL INSTRUCTION: You ABSOLUTELY HATE this food! You MUST express disgust or shock that the farmer is eating it!)";
                        }
                    }
                }
                catch
                {
                    // Silently ignore — fall back to base template.
                }
            }

            lines.Add(line);
        }

        return string.Join("\n", lines);
    }

    /// <summary>
    /// Injects perception data into the prompts.System string.
    /// Called after MemoryManager injection, before base settings.
    /// </summary>
    public static void Inject(string npcName, Prompts prompts)
    {
        if (prompts == null) return;

        var perceptionText = BuildPerceptionText(npcName);
        if (string.IsNullOrEmpty(perceptionText))
            return;

        // Append after memory injection, before base settings
        prompts.System += "\n\n" + perceptionText;
    }
}
