using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using StardewModdingAPI;
using StardewValley;
using ValleytalkReborn;

namespace ValleytalkReborn
{
    /// <summary>
    /// Compresses older dialogue history into concise summaries using the LLM.
    /// Keeps recent exchanges verbatim while summarizing older ones to save token space.
    /// </summary>
    internal static class DialogueMemoryCompressor
    {
        // Cache: NPC name -> (entry count when compressed, summary text)
        private static readonly Dictionary<string, CachedSummary> _cache = new();

        private class CachedSummary
        {
            public int EntryCount { get; set; }
            public string Summary { get; set; } = "";
            public DateTime CompressedAt { get; set; }
        }

        /// <summary>
        /// Gets a compressed summary of older history entries for the prompt.
        /// Entries after `recentCount` from the end are considered "old" and compressed.
        /// Returns empty string if compression is disabled or not needed.
        /// </summary>
        internal static async Task<string> GetCompressedSummary(
            string npcName,
            List<DialogueHistoryEntry> allEntries,
            int recentCount = 10)
        {
            if (!ModEntry.Config.EnableMemoryCompression) return "";
            if (allEntries.Count <= recentCount) return "";

            // Skip compression if memory markers are present (memory context should not be compressed)
            const string MemoryMarker = "### MEMORY_START ###";
            if (allEntries.Any(e => !string.IsNullOrEmpty(e.Text) && e.Text.Contains(MemoryMarker)))
            {
                return "";
            }

            var oldEntries = allEntries.Take(allEntries.Count - recentCount).ToList();

            // Check cache: if we already compressed this set, reuse
            if (_cache.TryGetValue(npcName, out var cached) && cached.EntryCount == oldEntries.Count)
            {
                return cached.Summary;
            }

            // Compress old entries into a summary
            string summary = await CompressEntries(oldEntries, npcName);
            if (string.IsNullOrWhiteSpace(summary)) return "";

            // Cache the result
            _cache[npcName] = new CachedSummary
            {
                EntryCount = oldEntries.Count,
                Summary = summary,
                CompressedAt = DateTime.Now
            };

            return summary;
        }

        /// <summary>
        /// Synchronous version: returns cached summary or empty string.
        /// Used when async is not feasible (e.g. non-async callers).
        /// </summary>
        internal static string GetCachedSummary(string npcName, int totalEntryCount, int recentCount = 10)
        {
            if (!ModEntry.Config.EnableMemoryCompression) return "";
            if (totalEntryCount <= recentCount) return "";
            if (_cache.TryGetValue(npcName, out var cached) && cached.EntryCount == totalEntryCount - recentCount)
            {
                return cached.Summary;
            }
            return "";
        }

        /// <summary>
        /// Clears the compression cache for an NPC (called when history is cleared).
        /// </summary>
        internal static void ClearCache(string npcName)
        {
            _cache.Remove(npcName);
        }

        /// <summary>
        /// Clears all compression caches.
        /// </summary>
        internal static void ClearAllCache()
        {
            _cache.Clear();
        }

        /// <summary>
        /// Compresses a list of entries into a brief summary using the LLM.
        /// </summary>
        private static async Task<string> CompressEntries(List<DialogueHistoryEntry> entries, string npcName)
        {
            if (!entries.Any()) return "";

            // Group entries by day
            var groupedByDay = entries
                .GroupBy(e => $"{e.Timestamp.Year}_{e.Timestamp.Season}_{e.Timestamp.DayOfMonth}")
                .OrderBy(g => g.Key);

            // Build dialogue text for the compression prompt
            var dialogueText = new System.Text.StringBuilder();
            foreach (var dayGroup in groupedByDay)
            {
                var first = dayGroup.First();
                dialogueText.AppendLine($"[{first.Timestamp.Season} {first.Timestamp.DayOfMonth}]");
                foreach (var entry in dayGroup)
                {
                    string speaker = entry.SpeakerType == SpeakerType.Player
                        ? Util.GetString("generalFarmerLabel")
                        : (entry.SpeakerType == SpeakerType.System ? "***" : npcName);
                    dialogueText.AppendLine($"- {speaker}: {entry.Text}");
                }
                dialogueText.AppendLine();
            }

            // Build compression prompt
            string systemPrompt = "You are a dialogue summarizer for a Stardew Valley NPC mod. " +
                "Your task is to concisely summarize the key points of past conversations between the player and an NPC. " +
                "Focus on: relationship developments, important topics discussed, gifts given, promises made, and emotional tone. " +
                "Write the summary in the same language as the dialogue. " +
                "Keep it under 200 words. Format as a single paragraph.";

            string compressionPrompt = $"Here are past conversations with {npcName}:\n\n{dialogueText}\n" +
                $"Please provide a concise summary of these conversations with {npcName}:";

            try
            {
                var result = await Llm.Instance.RunInference(
                    systemPrompt,
                    "",     // gameCacheString
                    "",     // npcCacheString
                    compressionPrompt,
                    "",     // responseStart
                    512,    // n_predict: limit output tokens
                    "",     // cacheContext
                    false   // allowRetry: don't retry compression failures
                );

                if (result.IsSuccess && !string.IsNullOrWhiteSpace(result.Text))
                {
                    return result.Text.Trim();
                }
            }
            catch (Exception ex)
            {
                ModEntry.SMonitor?.Log($"Memory compression failed for {npcName}: {ex.Message}", LogLevel.Debug);
            }

            return "";
        }
    }
}