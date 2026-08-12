namespace ValleytalkReborn
{
    internal class DialogueHistoryAdapter : IHistory
    {
        private readonly DialogueHistoryEntry _entry;

        public DialogueHistoryAdapter(DialogueHistoryEntry entry)
        {
            _entry = entry;
        }

        public DialogueHistoryEntry Entry => _entry;

        public string Format(string npcName)
        {
            string speakerLabel = _entry.SpeakerType switch
            {
                SpeakerType.Player => Util.GetString("generalFarmerLabel") ?? "农夫",
                SpeakerType.System => "***",
                _ => npcName
            };

            string text = _entry.Text;
            if (_entry.SpeakerType == SpeakerType.System && !string.IsNullOrEmpty(_entry.GiftName))
                text = $"[Gift: {_entry.GiftName}] {_entry.Text}";

            return $"- {speakerLabel}: {text}";
        }

        public static string GetFuzzyTime(StardewTime entryTime, StardewTime now)
        {
            int dayDiff = TotalDays(now) - TotalDays(entryTime);
            if (dayDiff < 0) dayDiff = 0;

            if (dayDiff == 0)
            {
                int minuteDiff = ToTotalMinutes(now.TimeOfDay) - ToTotalMinutes(entryTime.TimeOfDay);
                return minuteDiff <= 120 ? "In the past hour" : "Earlier today";
            }

            if (dayDiff == 1) return "Yesterday";
            if (dayDiff <= 7) return "A few days ago";
            if (dayDiff <= 28) return "Earlier this season";
            return "A long time ago";
        }

        private static int ToTotalMinutes(int timeOfDay)
        {
            return (timeOfDay / 100) * 60 + (timeOfDay % 100);
        }

        private static int TotalDays(StardewTime t)
        {
            int seasonIndex = t.Season switch
            {
                Season.Spring => 0,
                Season.Summer => 1,
                Season.Fall   => 2,
                Season.Winter => 3,
                _             => 0
            };
            return (t.Year - 1) * 112 + seasonIndex * 28 + (t.DayOfMonth - 1);
        }
    }
}