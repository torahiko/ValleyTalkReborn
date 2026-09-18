using System;
using Newtonsoft.Json;
using StardewModdingAPI;
using StardewValley;
#nullable disable

namespace ValleytalkReborn
{
    /// <summary>
    /// Represents who spoke a line of dialogue
    /// </summary>
    internal enum SpeakerType
    {
        NPC,
        Player,
        System  // For gift actions, event notifications, etc.
    }

    /// <summary>
    /// Represents a single entry in the dialogue history.
    /// </summary>
    internal class DialogueHistoryEntry
    {
        [JsonConstructor]
        public DialogueHistoryEntry()
        {
            Id = Guid.NewGuid();
        }

        public DialogueHistoryEntry(string speakerName, string text, SpeakerType speakerType, string dialogueType = "")
            : this()
        {
            SpeakerName = speakerName;
            Text = text;
            SpeakerType = speakerType;
            DialogueType = dialogueType;

            // 仅在世界就绪时安全获取当前游戏时间
            if (Context.IsWorldReady)
            {
                Timestamp = new StardewTime(Game1.year, (Season)Game1.season, Game1.dayOfMonth, Game1.timeOfDay);
            }
        }

        public DialogueHistoryEntry(string speakerName, string text, SpeakerType speakerType, StardewTime timestamp, string dialogueType = "")
            : this(speakerName, text, speakerType, dialogueType)
        {
            Timestamp = timestamp;
        }

        /// <summary>
        /// Unique identifier for deduplication
        /// </summary>
        public Guid Id { get; }

        /// <summary>
        /// Who spoke this line (NPC name, "Player", or "System")
        /// </summary>
        public string SpeakerName { get; set; } = "";

        /// <summary>
        /// The dialogue text
        /// </summary>
        public string Text { get; set; } = "";

        /// <summary>
        /// Whether this was spoken by the NPC, Player, or System
        /// </summary>
        public SpeakerType SpeakerType { get; set; }

        /// <summary>
        /// Category: "dialogue", "conversation", "gift", "event", "marriage", "eavesdrop"
        /// </summary>
        public string DialogueType { get; set; } = "";

        /// <summary>
        /// When this was recorded (game time)
        /// </summary>
        public StardewTime Timestamp { get; set; }

        /// <summary>
        /// Physical write-order timestamp (UTC ms). Stable tiebreaker when multiple
        /// NPCs share the same in-game timeOfDay, eliminating HashSet iteration drift.
        /// </summary>
        public long UtcTimestampMs { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        /// <summary>
        /// For gift entries: the gift object name
        /// </summary>
        public string GiftName { get; set; }

        /// <summary>
        /// For gift entries: the reaction taste (0-7)
        /// </summary>
        public int GiftTaste { get; set; } = -1;

        /// <summary>
        /// Soft-delete flag. Consumed entries are excluded from future context queries.
        /// </summary>
        [JsonIgnore]
        public bool IsConsumed { get; set; } = false;

        /// <summary>
        /// Hash for deduplication: speaker + text + approximate time
        /// </summary>
        [JsonIgnore]
        public string DedupKey => $"{SpeakerName}|{Text}|{Timestamp}";

        /// <summary>
        /// Formats this entry for display in the history window
        /// </summary>
        public string Format(string npcName)
        {
            string speakerLabel = SpeakerType switch
            {
                SpeakerType.Player => Util.GetString("generalFarmerLabel") ?? "Farmer",
                SpeakerType.System => "***",
                _ => npcName
            };

            string prefix = SpeakerType switch
            {
                SpeakerType.Player => "▸ ",
                SpeakerType.System => "★ ",
                _ => "  "
            };

            string timestamp = $"{Timestamp.Season} {Timestamp.DayOfMonth}";

            return SpeakerType == SpeakerType.System
                ? $"{prefix}[{timestamp}] {Text}"
                : $"{prefix}[{timestamp}] {speakerLabel}: {Text}";
        }

        /// <summary>
        /// Checks if this entry is a duplicate of another based on speaker, text and time
        /// </summary>
        public bool IsDuplicateOf(DialogueHistoryEntry other)
        {
            if (other == null) return false;

            bool timeMatches = Timestamp.Year == other.Timestamp.Year &&
                               Timestamp.Season == other.Timestamp.Season &&
                               Timestamp.DayOfMonth == other.Timestamp.DayOfMonth &&
                               Timestamp.TimeOfDay == other.Timestamp.TimeOfDay;

            return SpeakerType == other.SpeakerType
                && string.Equals(Text, other.Text, StringComparison.Ordinal)
                && timeMatches;
        }
    }
}