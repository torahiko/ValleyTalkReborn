using System;
using System.Collections.Generic;

namespace ValleytalkReborn.Cutscene.Serendipity
{
    /// <summary>
    /// 偶遇情境定义：代表一个特定的时空场景剧本模板（如周五酒吧聚会、雨天湖畔漫步、广场日常等）
    /// </summary>
    public sealed class SituationDefinition
    {
        public string Id { get; init; }
        public string Title { get; init; }
        public string LocationName { get; init; } // 为空代表任意地点
        public int MinTimeOfDay { get; init; } = 600;
        public int MaxTimeOfDay { get; init; } = 2600;
        public bool? RequireRaining { get; init; } = null; // null = 任意天气
        public string RequiredDayOfWeek { get; init; } = null; // null = 任意星期
        public List<string> PreferredActors { get; init; } = new();
        public string IntentPrompt { get; init; }

        public SituationDefinition(string id, string title, string intentPrompt)
        {
            Id = id ?? throw new ArgumentNullException(nameof(id));
            Title = title ?? throw new ArgumentNullException(nameof(title));
            IntentPrompt = intentPrompt ?? throw new ArgumentNullException(nameof(intentPrompt));
        }
    }
}
