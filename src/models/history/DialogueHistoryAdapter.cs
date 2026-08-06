using System;

namespace ValleyTalk
{
    /// <summary>
    /// Adapter to make DialogueHistoryEntry compatible with the IHistory interface.
    /// This allows Prompts.cs to format new history entries using the same code path.
    /// </summary>
    internal class DialogueHistoryAdapter : IHistory
    {
        private readonly DialogueHistoryEntry _entry;

        public DialogueHistoryAdapter(DialogueHistoryEntry entry)
        {
            _entry = entry;
        }

        public string Format(string npcName)
        {
            string speakerLabel = _entry.SpeakerType switch
            {
                SpeakerType.Player => Util.GetString("generalFarmerLabel"),
                SpeakerType.System => "***",
                _ => npcName
            };

            string text = _entry.Text;
            if (_entry.SpeakerType == SpeakerType.System && !string.IsNullOrEmpty(_entry.GiftName))
            {
                text = $"[Gift: {_entry.GiftName}] {_entry.Text}";
            }

            return $"- {speakerLabel}: {text}";
        }
    }
}
