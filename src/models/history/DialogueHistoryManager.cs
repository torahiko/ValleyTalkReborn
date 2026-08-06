using System;
using System.Collections.Generic;
using System.Linq;
using StardewValley;
using StardewModdingAPI.Events;
using StardewModdingAPI;
#nullable disable

namespace ValleyTalk
{
    /// <summary>
    /// Centralized manager for recording and retrieving NPC dialogue history.
    /// Replaces the scattered recording logic across patches.
    /// </summary>
    internal class DialogueHistoryManager
    {
        public static DialogueHistoryManager Instance { get; } = new DialogueHistoryManager();

        // Per-NPC history, keyed by NPC name
        private readonly Dictionary<string, List<DialogueHistoryEntry>> _history = new();

        // Tracks the last recorded entry per NPC for deduplication
        private readonly Dictionary<string, DialogueHistoryEntry> _lastEntry = new();

        // Tracks pending gift recordings (when gift is given but NPC response hasn't been generated yet)
        private readonly Dictionary<string, DialogueHistoryEntry> _pendingGifts = new();

        // Maximum entries per NPC to prevent unbounded growth
        private const int MaxEntriesPerNpc = 500;

        private DialogueHistoryManager()
        {
            ModEntry.SHelper.Events.GameLoop.Saving += OnSaving;
        }

        /// <summary>
        /// Records a line of NPC dialogue
        /// </summary>
        public void RecordNpcDialogue(string npcName, string text, string dialogueType = "dialogue")
        {
            if (string.IsNullOrWhiteSpace(text)) return;

            var entry = new DialogueHistoryEntry(npcName, text, SpeakerType.NPC, dialogueType);
            AddEntry(npcName, entry);
        }

        /// <summary>
        /// Records a player response/line
        /// </summary>
        public void RecordPlayerDialogue(string npcName, string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return;

            var entry = new DialogueHistoryEntry(
                Util.GetString("generalFarmerLabel"),
                text,
                SpeakerType.Player,
                "conversation"
            );
            AddEntry(npcName, entry);
        }

        /// <summary>
        /// Records the action of giving a gift. The NPC response will be recorded separately when generated.
        /// </summary>
        public void RecordGiftGiven(string npcName, string giftName, int taste)
        {
            string tasteLabel = taste switch
            {
                0 => "Love",
                2 => "Like",
                4 => "Dislike",
                6 => "Hate",
                _ => "Neutral"
            };

            string text = $"Given gift: {giftName} (Reaction: {tasteLabel})";
            var entry = new DialogueHistoryEntry("System", text, SpeakerType.System, "gift")
            {
                GiftName = giftName,
                GiftTaste = taste
            };

            // Track pending gift so we can link the NPC response later
            _pendingGifts[npcName] = entry;
            AddEntry(npcName, entry);
        }

        /// <summary>
        /// Records the NPC's reaction to a gift. Links to the pending gift entry if available.
        /// </summary>
        public void RecordGiftReaction(string npcName, string reactionText)
        {
            if (string.IsNullOrWhiteSpace(reactionText)) return;

            // Record the NPC's reaction as a normal NPC line but tagged as gift type
            var entry = new DialogueHistoryEntry(npcName, reactionText, SpeakerType.NPC, "gift");
            AddEntry(npcName, entry);

            // Clear pending gift
            _pendingGifts.Remove(npcName);
        }

        /// <summary>
        /// Records a full conversation exchange (player typed input + NPC response)
        /// </summary>
        public void RecordConversationExchange(string npcName, string playerLine, string npcResponse)
        {
            if (!string.IsNullOrWhiteSpace(playerLine))
            {
                RecordPlayerDialogue(npcName, playerLine);
            }
            if (!string.IsNullOrWhiteSpace(npcResponse))
            {
                RecordNpcDialogue(npcName, npcResponse, "conversation");
            }
        }

        /// <summary>
        /// Gets all history for a specific NPC, ordered chronologically
        /// </summary>
        public List<DialogueHistoryEntry> GetHistory(string npcName)
        {
            if (_history.TryGetValue(npcName, out var entries))
            {
                return entries.ToList();
            }
            return new List<DialogueHistoryEntry>();
        }

        /// <summary>
        /// Gets formatted history lines for display in the UI
        /// </summary>
        public List<string> GetFormattedHistory(string npcName)
        {
            var entries = GetHistory(npcName);
            return entries.Select(e => e.Format(npcName)).ToList();
        }

        /// <summary>
        /// Clears all history for a specific NPC
        /// </summary>
        public void ClearHistory(string npcName)
        {
            _history.Remove(npcName);
            _lastEntry.Remove(npcName);
            _pendingGifts.Remove(npcName);
            DialogueMemoryCompressor.ClearCache(npcName);
        }

        /// <summary>
        /// Clears history for all NPCs
        /// </summary>
        public void ClearAllHistory()
        {
            _history.Clear();
            _lastEntry.Clear();
            _pendingGifts.Clear();
        }

        /// <summary>
        /// Gets the most recent NPC name that the player interacted with (for UI title)
        /// </summary>
        public string GetMostRecentNpc()
        {
            return _lastEntry.OrderByDescending(x => x.Value.Timestamp).FirstOrDefault().Key ?? "";
        }

        // Helper to calculate total days for deduplication time window comparison
        private static double TotalDays(StardewTime t) => t.year * 112 + (int)t.season * 28 + t.dayOfMonth;

        private void AddEntry(string npcName, DialogueHistoryEntry entry)
        {
            if (!_history.ContainsKey(npcName))
            {
                _history[npcName] = new List<DialogueHistoryEntry>();
            }

            // Deduplication: skip if this is identical to the last entry
            if (_lastEntry.TryGetValue(npcName, out var last) && last.IsDuplicateOf(entry))
            {
                return;
            }

            // Deduplication: skip if the same NPC said the same thing within a short time window
            // (prevents the same line being recorded by multiple patches)
            var recentEntries = _history[npcName].Where(e =>
                e.SpeakerType == SpeakerType.NPC &&
                e.Text == entry.Text &&
                Math.Abs((TotalDays(e.Timestamp) - TotalDays(entry.Timestamp))) < 1
            );
            if (recentEntries.Any())
            {
                return;
            }

            _history[npcName].Add(entry);
            _lastEntry[npcName] = entry;

            // Trim if exceeding max
            if (_history[npcName].Count > MaxEntriesPerNpc)
            {
                _history[npcName].RemoveRange(0, _history[npcName].Count - MaxEntriesPerNpc);
            }

            // Proactively compress older entries in the background
            TryTriggerCompression(npcName);
        }

        private void OnSaving(object sender, SavingEventArgs e)
        {
            // Save to SMAPI save data
            var data = new Dictionary<string, List<SerializableEntry>>();
            foreach (var kvp in _history)
            {
                data[kvp.Key] = kvp.Value.Select(SerializableEntry.FromEntry).ToList();
            }
            ModEntry.SHelper.Data.WriteSaveData("ValleyTalk.DialogueHistory", data);
        }

        /// <summary>
        /// Loads history from SMPI save data. Call this on game load.
        /// </summary>
        public void Load()
        {
            var data = ModEntry.SHelper.Data.ReadSaveData<Dictionary<string, List<SerializableEntry>>>("ValleyTalk.DialogueHistory");
            if (data == null) return;

            foreach (var kvp in data)
            {
                _history[kvp.Key] = kvp.Value.Select(se => se.ToEntry()).ToList();
                if (_history[kvp.Key].Any())
                {
                    _lastEntry[kvp.Key] = _history[kvp.Key].Last();
                }
            }
        }

        /// <summary>
        /// Proactively compresses older entries in the background when history grows large.
        /// The result is cached by DialogueMemoryCompressor for use when building prompts.
        /// </summary>
        private void TryTriggerCompression(string npcName)
        {
            if (!ModEntry.Config.EnableMemoryCompression) return;
            var entries = GetHistory(npcName);
            int recentCount = ModEntry.Config.MemoryRecentCount;
            // Only compress if we have significantly more than the "recent" window
            if (entries.Count <= recentCount * 2) return;

            // Fire-and-forget: compress in background, cache the result
            var cached = DialogueMemoryCompressor.GetCachedSummary(npcName, entries.Count, recentCount);
            if (cached == "")
            {
                _ = System.Threading.Tasks.Task.Run(() =>
                    DialogueMemoryCompressor.GetCompressedSummary(npcName, entries, recentCount));
            }
        }
    }

    /// <summary>
    /// Serializable wrapper for DialogueHistoryEntry (StardewTime isn't directly serializable in all cases)
    /// </summary>
    internal class SerializableEntry
    {
        public Guid Id { get; set; }
        public string SpeakerName { get; set; } = "";
        public string Text { get; set; } = "";
        public SpeakerType SpeakerType { get; set; }
        public string DialogueType { get; set; } = "";
        public int Year { get; set; }
        public StardewValley.Season Season { get; set; }
        public int Day { get; set; }
        public int TimeOfDay { get; set; }
        public string GiftName { get; set; }
        public int GiftTaste { get; set; } = -1;

        public static SerializableEntry FromEntry(DialogueHistoryEntry entry)
        {
            return new SerializableEntry
            {
                Id = entry.Id,
                SpeakerName = entry.SpeakerName,
                Text = entry.Text,
                SpeakerType = entry.SpeakerType,
                DialogueType = entry.DialogueType,
                Year = entry.Timestamp.year,
                Season = entry.Timestamp.season,
                Day = entry.Timestamp.dayOfMonth,
                TimeOfDay = entry.Timestamp.timeOfDay,
                GiftName = entry.GiftName,
                GiftTaste = entry.GiftTaste
            };
        }

        public DialogueHistoryEntry ToEntry()
        {
            var entry = new DialogueHistoryEntry(SpeakerName, Text, SpeakerType, DialogueType)
            {
                Timestamp = new StardewTime(Year, Season, Day, TimeOfDay),
                GiftName = GiftName,
                GiftTaste = GiftTaste
            };
            return entry;
        }
    }
}
