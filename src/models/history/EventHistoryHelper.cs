using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using StardewValley;

namespace ValleytalkReborn
{
    /// <summary>
    /// Helper class for building the event history section of the prompt.
    /// Separated from Prompts.cs to keep the compression logic in one place.
    /// </summary>
    internal static class EventHistoryHelper
    {
        /// <summary>
        /// Builds the event history section for the prompt, using compression for older entries.
        /// </summary>
        internal static void BuildEventHistory(StringBuilder prompt, Character character, DialogueContext context)
        {
            var timeNow = new StardewTime(Game1.Date, Game1.timeOfDay);
            var historySample = character.EventHistorySample().ToList();

            if (!historySample.Any()) return;

            prompt.AppendLine($"##{Util.GetString(character, "eventHistoryHeading")}");
            prompt.AppendLine(Util.GetString(character, "eventHistoryIntro", new { Name = character.Name }));
            prompt.AppendLine(Util.GetString(character, "eventHistorySubheading"));

            // Build a set of texts already present in the active ChatHistory so we don't duplicate them
            var chatHistoryTexts = new System.Collections.Generic.HashSet<string>(
                context?.ChatHistory?.Select(c => c.Text?.Trim() ?? "")
                ?? System.Linq.Enumerable.Empty<string>(),
                System.StringComparer.Ordinal);

            foreach (var entry in historySample)
            {
                if (entry.Item2 is DialogueHistoryAdapter adapter)
                {
                    bool isSystem = adapter.Entry.SpeakerType == SpeakerType.System;
                    if (!isSystem)
                    {
                        // Skip normal dialogue lines already visible in the active chat context
                        var entryText = adapter.Entry.Text?.Trim() ?? "";
                        if (!string.IsNullOrEmpty(entryText) && chatHistoryTexts.Contains(entryText)) continue;
                    }

                    string fuzzyTime = DialogueHistoryAdapter.GetFuzzyTime(entry.Item1, timeNow);
                    prompt.AppendLine($"- {fuzzyTime}: {adapter.Format(character.Name)}");
                }
                else
                {
                    string fuzzyTime = DialogueHistoryAdapter.GetFuzzyTime(entry.Item1, timeNow);
                    prompt.AppendLine($"- {fuzzyTime}: {entry.Item2.Format(character.Name)}");
                }
            }
        }
    }
}
