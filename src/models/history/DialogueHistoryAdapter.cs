namespace ValleytalkReborn
{
    using StardewValley;

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

            // Convert raw system gift log into natural memory from NPC's perspective
            if (_entry.SpeakerType == SpeakerType.System && !string.IsNullOrEmpty(_entry.GiftName))
            {
                string farmer = Util.GetString("generalFarmerLabel") ?? "农夫";
                string tasteDesc = _entry.GiftTaste switch
                {
                    0 => "你非常喜欢，甚至爱不释手",
                    2 => "你挺喜欢的，心里很温暖",
                    4 => "你不太喜欢，但还是收下了",
                    6 => "你非常讨厌这个物品",
                    _ => "你平淡地收下了"
                };

                return $"- {farmer}送了你一份礼物：[{_entry.GiftName}]（{tasteDesc}）。";
            }

            return $"- {speakerLabel}: {text}";
        }

        public static string GetFuzzyTime(StardewTime entryTime, StardewTime now)
        {
            bool isZh = LocalizedContentManager.CurrentLanguageCode
                .ToString()
                .StartsWith("zh", System.StringComparison.OrdinalIgnoreCase);

            int dayDiff = TotalDays(now) - TotalDays(entryTime);
            if (dayDiff < 0) dayDiff = 0;

            if (dayDiff == 0)
            {
                int minuteDiff = ToTotalMinutes(now.TimeOfDay) - ToTotalMinutes(entryTime.TimeOfDay);
                if (minuteDiff < 0) minuteDiff = 0;

                if (minuteDiff <= 10)
                    return isZh ? "刚刚" : "Just now";
                if (minuteDiff <= 60)
                    return isZh ? "不到一小时前" : "Within the past hour";
                if (minuteDiff <= 300)
                    return isZh ? "几个小时前" : "A few hours ago";
                return isZh ? "今天早些时候" : "Earlier today";
            }

            if (dayDiff == 1)
                return isZh ? "昨天" : "Yesterday";
            if (dayDiff <= 3)
                return isZh ? "两三天前" : "A couple of days ago";
            if (dayDiff <= 7)
                return isZh ? "前几天" : "A few days ago";
            if (dayDiff <= 28)
                return isZh ? "本季度早些时候" : "Earlier this season";
            return isZh ? "很久以前" : "A long time ago";
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