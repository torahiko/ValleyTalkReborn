using System;
using Newtonsoft.Json;
using StardewValley;
#nullable disable

namespace ValleyTalk
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
    /// This replaces the scattered recording across DialogueHistory, ConversationHistory, etc.
    /// </summary>
    internal class DialogueHistoryEntry
    {
        [JsonConstructor]
        public DialogueHistoryEntry()
        {
            Id = Guid.NewGuid();
            Timestamp = new StardewTime(Game1.year, Game1.season, Game1.dayOfMonth, Game1.timeOfDay);
        }

        public DialogueHistoryEntry(string speakerName, string text, SpeakerType speakerType, string dialogueType = "")
            : this()
        {
            SpeakerName = speakerName;
            Text = text;
            SpeakerType = speakerType;
            DialogueType = dialogueType;
        }

        /// <summary>
        /// Unique identifier for deduplication
        /// </summary>
        public Guid Id { get; }

        /// <summary>
        /// Who spoke this line (NPC name, "Farmer", or "System")
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
        /// Category: "dialogue", "conversation", "gift", "event", "marriage"
        /// </summary>
        public string DialogueType { get; set; } = "";

        /// <summary>
        /// When this was recorded (game time)
        /// </summary>
        public StardewTime Timestamp { get; set; }

        /// <summary>
        /// For gift entries: the gift object name
        /// </summary>
        public string GiftName { get; set; }

        /// <summary>
        /// For gift entries: the reaction taste (0-7)
        /// </summary>
        public int GiftTaste { get; set; } = -1;

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
                SpeakerType.Player => Util.GetString("generalFarmerLabel"),
                SpeakerType.System => "***",
                _ => npcName
            };

            string prefix = SpeakerType switch
            {
                SpeakerType.Player => "▸ ",
                SpeakerType.System => "★ ",
                _ => "  "
            };

            string timestamp = $"{Timestamp.season} {Timestamp.dayOfMonth}";

            return SpeakerType == SpeakerType.System
                ? $"{prefix}[{timestamp}] {Text}"
                : $"{prefix}[{timestamp}] {speakerLabel}: {Text}";
        }

        /// <summary>
        /// Checks if this entry is a duplicate of another based on speaker and text
        /// </summary>
        public bool IsDuplicateOf(DialogueHistoryEntry other)
        {
            return SpeakerName == other.SpeakerName
                && Text == other.Text
                && SpeakerType == other.SpeakerType;
        }
    }
}
