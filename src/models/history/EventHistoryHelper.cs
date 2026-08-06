using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using StardewValley;

namespace ValleyTalk
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

            int remainingLength = 4000;
            List<string> historyLines = new List<string>();

            // Separate world events (ActivityHistory) from dialogue entries (DialogueHistoryAdapter)
            var worldEvents = historySample.Where(e => e.Item2 is ActivityHistory).ToList();
            var dialogueEntries = historySample.Where(e => e.Item2 is DialogueHistoryAdapter).ToList();

            int recentCount = ModEntry.Config.MemoryRecentCount;

            // Compress old dialogue entries if we have more than the recent window
            if (ModEntry.Config.EnableMemoryCompression && dialogueEntries.Count > recentCount)
            {
                string cachedSummary = DialogueMemoryCompressor.GetCachedSummary(character.Name, dialogueEntries.Count, recentCount);

                if (!string.IsNullOrEmpty(cachedSummary))
                {
                    string summaryLine = $"[Summary of earlier conversations: {cachedSummary}]";
                    historyLines.Add(summaryLine);
                    remainingLength -= summaryLine.Length + 1;
                }

                // Show recent entries in detail
                var recentEntries = dialogueEntries.Skip(dialogueEntries.Count - recentCount);
                foreach (var entry in recentEntries.AsEnumerable().Reverse())
                {
                    // Exclude the current conversation (entries from "just now")
                    if (entry.Item1.IsJustNow()) continue;

                    var line = $"- {entry.Item1.SinceDescription(timeNow)}: {entry.Item2.Format(character.Name)}";
                    historyLines.Add(line);
                    remainingLength -= line.Length + 1;
                    if (remainingLength < 0) break;
                }
            }
            else
            {
                // Not enough entries to compress - show all verbatim
                foreach (var entry in dialogueEntries.AsEnumerable().Reverse())
                {
                    // Exclude the current conversation (entries from "just now")
                    if (entry.Item1.IsJustNow()) continue;

                    var line = $"- {entry.Item1.SinceDescription(timeNow)}: {entry.Item2.Format(character.Name)}";
                    historyLines.Add(line);
                    remainingLength -= line.Length + 1;
                    if (remainingLength < 0) break;
                }
            }

            // World events are always shown (they're brief and informational)
            foreach (var worldEvent in worldEvents.AsEnumerable().Reverse())
            {
                var line = $"- {worldEvent.Item1.SinceDescription(timeNow)}: {worldEvent.Item2.Format(character.Name)}";
                historyLines.Add(line);
                remainingLength -= line.Length + 1;
                if (remainingLength < 0) break;
            }

            // Reverse to show oldest first
            foreach (var line in historyLines.Reverse<string>())
            {
                prompt.AppendLine(line);
            }
        }
    }
}
