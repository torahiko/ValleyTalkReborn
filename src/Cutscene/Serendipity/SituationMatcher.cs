using System;
using System.Collections.Generic;
using System.Linq;

namespace ValleytalkReborn.Cutscene.Serendipity
{
    /// <summary>
    /// 情境匹配器：根据当前地点、时间、天气与附近 NPC 集合，匹配最契合的偶遇剧本模板
    /// </summary>
    public static class SituationMatcher
    {
        private static readonly List<SituationDefinition> PresetSituations = new()
        {
            new SituationDefinition(
                "SaloonFridayNight",
                "周五晚星果沙龙",
                "周五夜晚热闹的星果沙龙吧台边，几位村民点着饮品闲聊最近的琐事、工作压力与心底的小秘密")
            {
                LocationName = "Saloon",
                MinTimeOfDay = 1700,
                MaxTimeOfDay = 2500,
                RequiredDayOfWeek = "Friday",
                PreferredActors = new List<string> { "Shane", "Clint", "Emily", "Pam", "Gus", "Sam", "Sebastian", "Abigail" }
            },

            new SituationDefinition(
                "SaloonEvening",
                "沙龙炉火微醺",
                "夜晚温暖的星果沙龙内，村民们放松下来，在酒香与炉火旁交流近况与生活趣闻")
            {
                LocationName = "Saloon",
                MinTimeOfDay = 1700,
                MaxTimeOfDay = 2500,
                PreferredActors = new List<string> { "Shane", "Clint", "Emily", "Pam", "Gus", "Willy", "Leah" }
            },

            new SituationDefinition(
                "MountainRainyLake",
                "雨天湖畔偶遇",
                "蒙蒙细雨中山间湖畔泛起涟漪，村民在雨中驻足偶遇，分享对雨天静谧氛围、音乐或各自心事的体悟")
            {
                LocationName = "Mountain",
                RequireRaining = true,
                PreferredActors = new List<string> { "Abigail", "Sebastian", "Linus", "Maru", "Demetrius" }
            },

            new SituationDefinition(
                "TownSquareSunny",
                "广场明媚闲谈",
                "阳光明媚的鹈鹕镇中心广场边，村民惬意地停下脚步，讨论最近镇上的新鲜事、天气与日常规划")
            {
                LocationName = "Town",
                RequireRaining = false,
                MinTimeOfDay = 900,
                MaxTimeOfDay = 1700,
                PreferredActors = new List<string> { "Haley", "Alex", "Penny", "Sam", "Emily", "Jodi", "Caroline" }
            },

            new SituationDefinition(
                "BeachSunset",
                "黄昏海风私语",
                "黄昏海风拂面的沙滩边，村民们看着落日与潮水，交流着关于海洋、艺术灵感与生活感悟")
            {
                LocationName = "Beach",
                RequireRaining = false,
                MinTimeOfDay = 1500,
                MaxTimeOfDay = 2000,
                PreferredActors = new List<string> { "Elliott", "Leah", "Willy", "Sam", "Alex", "Haley" }
            },

            new SituationDefinition(
                "MuseumLibrary",
                "博物馆书香静语",
                "安静的博物馆与图书馆角落，村民们端详着展品与旧书，轻声探讨知识、历史或各自的好奇心")
            {
                LocationName = "ArchaeologyHouse",
                MinTimeOfDay = 900,
                MaxTimeOfDay = 1800,
                PreferredActors = new List<string> { "Penny", "Maru", "Harvey", "Vincent", "Jas" }
            }
        };

        /// <summary>
        /// 匹配当前环境最合适的情境定义
        /// </summary>
        public static SituationDefinition MatchSituation(
            string locationName,
            int timeOfDay,
            bool isRaining,
            string dayOfWeek,
            List<string> candidateNpcNames)
        {
            if (string.IsNullOrWhiteSpace(locationName))
                return CreateGenericFallback(locationName, candidateNpcNames);

            // 1. 优先在内置预设中寻找强匹配项（按优先级遍历）
            foreach (var preset in PresetSituations)
            {
                // 地点过滤
                if (!string.IsNullOrEmpty(preset.LocationName) &&
                    !string.Equals(preset.LocationName, locationName, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // 时间过滤
                if (timeOfDay < preset.MinTimeOfDay || timeOfDay > preset.MaxTimeOfDay)
                {
                    continue;
                }

                // 天气过滤
                if (preset.RequireRaining.HasValue && preset.RequireRaining.Value != isRaining)
                {
                    continue;
                }

                // 星期过滤
                if (!string.IsNullOrEmpty(preset.RequiredDayOfWeek) &&
                    !string.Equals(preset.RequiredDayOfWeek, dayOfWeek, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // 偏好角色匹配度检查（如果预设有偏好名单，候选者中至少包含一人则大幅加权）
                if (preset.PreferredActors.Count > 0 && candidateNpcNames != null && candidateNpcNames.Count > 0)
                {
                    if (candidateNpcNames.Any(name => preset.PreferredActors.Contains(name, StringComparer.OrdinalIgnoreCase)))
                    {
                        return preset;
                    }
                }
                else
                {
                    return preset;
                }
            }

            // 2. 兜底通用偶遇模板
            return CreateGenericFallback(locationName, candidateNpcNames);
        }

        private static SituationDefinition CreateGenericFallback(string locationName, List<string> candidateNpcNames)
        {
            string actorsText = candidateNpcNames != null && candidateNpcNames.Count > 0
                ? string.Join("与", candidateNpcNames)
                : "村民们";

            return new SituationDefinition(
                "GenericEncounter",
                "街头偶遇闲谈",
                $"在{locationName ?? "星露谷"}偶遇的{actorsText}，停下手中的事务，就近借景生情，开展一段生动真实、富有生活气息的微剧场互动。");
        }
    }
}
