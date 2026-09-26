using System;
using System.Linq;
using System.Threading.Tasks;
using StardewModdingAPI;
using StardewValley;

namespace ValleytalkReborn
{
    /// <summary>
    /// 负责约会流程中的 LLM 交互：Prompt 构建、LLM 调用、默认文本。
    /// DateManager 只管状态机，DateFlowService 只管"怎么跟 LLM 说话"。
    /// </summary>
    public static class DateFlowService
    {
        private static bool IsChineseLanguage =>
            LocalizedContentManager.CurrentLanguageCode == LocalizedContentManager.LanguageCode.zh;

        // ═══════════════════════════════════════════════════════════
        //  统一 LLM 调用（带超时）
        // ═══════════════════════════════════════════════════════════
        /// <summary>
        /// 统一的 LLM 调用封装：超时控制 + 异常捕获 + 文本清理。
        /// 返回 null 表示失败或超时。
        /// </summary>
        public static async Task<string> FetchLlmResponse(string sys, string user, int timeoutMs)
        {
            try
            {
                var inferTask   = Llm.Instance.RunInference(sys, "", "", user, "");
                var timeoutTask = Task.Delay(TimeSpan.FromMilliseconds(timeoutMs));

                if (await Task.WhenAny(inferTask, timeoutTask) != timeoutTask)
                {
                    var response = await inferTask;
                    if (response.IsSuccess && !string.IsNullOrWhiteSpace(response.Text))
                        return response.Text.Trim().Trim('\"', '\u201c', '\u201d');
                }
                else
                {
                    ModEntry.SMonitor?.Log("[DateFlow] LLM timeout.", LogLevel.Debug);
                }
            }
            catch (Exception ex)
            {
                ModEntry.SMonitor?.Log($"[DateFlow] LLM error: {ex.Message}", LogLevel.Debug);
            }

            return null;
        }

        // ═══════════════════════════════════════════════════════════
        //  问候语
        // ═══════════════════════════════════════════════════════════
        /// <summary>构建问候语的 System + User Prompt。</summary>
        public static (string sys, string user) BuildGreetingPrompt(
            string npcName, string locationId, LatenessLevel lateness)
        {
            bool isZh = IsChineseLanguage;

            string locPrompt = locationId;
            if (DateLocationRegistry.Locations.TryGetValue(locationId, out var info))
            {
                locPrompt = isZh
                    ? $"{info.DisplayNameZh}（环境特征：{info.ContextDescriptionZh}）"
                    : $"{info.DisplayNameEn} (Atmosphere: {info.ContextDescriptionEn})";
            }

            // 纯事实陈述：只说明玩家到达时间状态，不预设 NPC 情绪反应
            string latenessContext = isZh
                ? lateness switch
                {
                    LatenessLevel.TooEarly      => "玩家提前到达（早于 18:00）。",
                    LatenessLevel.OnTime         => "玩家准时或提前到达。",
                    LatenessLevel.SlightlyLate   => "玩家迟到约 19:00-21:00 之间才到。",
                    LatenessLevel.VeryLate       => "玩家严重迟到，21:00 后才到。",
                    LatenessLevel.MissedWindow   => "玩家很晚才到（22:00 后）。",
                    _                            => ""
                }
                : lateness switch
                {
                    LatenessLevel.TooEarly      => "The player arrived early (before 6pm).",
                    LatenessLevel.OnTime         => "The player arrived on time or early.",
                    LatenessLevel.SlightlyLate   => "The player arrived slightly late (between 7pm and 9pm).",
                    LatenessLevel.VeryLate       => "The player arrived very late (after 9pm).",
                    LatenessLevel.MissedWindow   => "The player arrived very late (after 10pm).",
                    _                            => ""
                };

            string sys = isZh
                ? $"你是《星露谷物语》的 {npcName}。你和玩家（@）在 {locPrompt} 开启了浪漫约会。{latenessContext}"
                : $"You are {npcName} from Stardew Valley. You are starting a romantic date with player (@) at {locPrompt}. {latenessContext}";

            string user = isZh
                ? "请对刚到场的玩家说一句简短开场白（30字以内，纯台词）。"
                : "Say a short opening line to the player to start the date (under 20 words).";

            return (sys, user);
        }

        /// <summary>默认问候语（LLM 失败时的兜底）。</summary>
        public static string BuildDefaultGreeting(LatenessLevel lateness)
        {
            bool isZh = IsChineseLanguage;
            return lateness switch
            {
                LatenessLevel.TooEarly => isZh
                    ? "你来这么早呀！离天黑还有一会儿呢，不过我已经开始期待了。"
                    : "You're here so early! There's still time before dark — but I'm already looking forward to it.",
                LatenessLevel.OnTime => isZh
                    ? "你来啦！今晚能和你一起度过，我真的很开心。"
                    : "You're here! I'm so glad we can spend tonight together.",
                LatenessLevel.SlightlyLate => isZh
                    ? "你终于来了……我都开始担心你了，不过没关系，你来了就好。"
                    : "You finally made it... I was starting to worry. But I'm glad you're here.",
                LatenessLevel.VeryLate => isZh
                    ? "……你来了。我已经等了好久了，差点以为你不来了。"
                    : "...You came. I waited so long, I almost thought you forgot about me.",
                LatenessLevel.MissedWindow => isZh
                    ? "……这么晚了，我以为今晚的约会已经取消了。"
                    : "...It's so late — I thought tonight's call was cancelled.",
                _ => isZh ? "你来啦！" : "You're here!"
            };
        }

        // ═══════════════════════════════════════════════════════════
        //  告别语
        // ═══════════════════════════════════════════════════════════
        /// <summary>构建告别语的 System + User Prompt。</summary>
        public static (string sys, string user) BuildFarewellPrompt(
            string npcName, string locationId)
        {
            bool isZh = IsChineseLanguage;

            string locName = DateLocationRegistry.Locations.TryGetValue(locationId, out var info)
                ? (isZh ? info.DisplayNameZh : info.DisplayNameEn)
                : locationId;

            string sys = isZh
                ? $"你是《星露谷物语》中的 {npcName}。你刚和面前的玩家（@）在 {locName} 结束了一场浪漫约会。现在已经是晚上，你需要回家了。"
                : $"You are {npcName} from Stardew Valley. You and player (@) just finished a romantic date at {locName}. It is late.";

           
            string user = isZh
                ? "请向面前的玩家（@）说一句告别台词（35字以内，直接输出台词）。"
                : "Say a goodbye line to player (@) in 1-2 short sentences.";

            return (sys, user);
        }

        /// <summary>默认告别语（LLM 失败时的兜底）。</summary>
        public static string BuildDefaultFarewell()
        {
            bool isZh = IsChineseLanguage;
            return isZh
                ? "时间不早了……今晚我和你在一起很开心。晚安！"
                : "It's getting late... I had a wonderful time tonight. Good night!";
        }

        // ═══════════════════════════════════════════════════════════
        //  手账摘要生成（Chronicle）
        // ═══════════════════════════════════════════════════════════

        /// <summary>
        /// 安全获取地点显示名称（兼容 LocationId 与 TargetMap 两种传入值）。
        /// 优先通过 LocationId 查询，失败时遍历 TargetMap 匹配，兜底返回原键。
        /// </summary>
        /// <param name="locationKey">地点 ID 或地图名（如 "Forest" 或 "Woods"）</param>
        /// <returns>本地化显示名称（如"秘密森林"或"The Secret Forest"）</returns>
        public static string ResolveLocationDisplayName(string locationKey)
        {
            bool isZh = IsChineseLanguage;

            // BOUNDARY: 注册表未初始化
            if (DateLocationRegistry.Locations == null || DateLocationRegistry.Locations.Count == 0)
                return locationKey;

            // 优先通过 LocationId 查询
            if (DateLocationRegistry.Locations.TryGetValue(locationKey, out var info))
                return isZh ? info.DisplayNameZh : info.DisplayNameEn;

            // 降级：遍历 TargetMap 匹配（处理 "Woods" → "Forest" 映射）
            var matched = DateLocationRegistry.Locations.Values
                .FirstOrDefault(l => string.Equals(l.TargetMap, locationKey, StringComparison.OrdinalIgnoreCase));

            if (matched != null)
                return isZh ? matched.DisplayNameZh : matched.DisplayNameEn;

            // 兜底：返回原键
            return locationKey;
        }

        /// <summary>
        /// 构建约会手账生成的 System + User Prompt。
        /// </summary>
        /// <param name="npcName">NPC 姓名</param>
        /// <param name="locationKey">约会地点 ID 或地图名</param>
        /// <param name="startTime">开始时间（游戏时间，如 1800）</param>
        /// <param name="endTime">结束时间（游戏时间，如 2130）</param>
        /// <param name="modeDesc">约会模式描述（"定点小聚" / "深情定点与漫步送归" / "随性散步"）</param>
        /// <param name="giftSummary">送礼摘要（如"玩家送了我最爱的紫水晶" 或空字符串）</param>
        /// <param name="dialogueHighlights">关键对话片段（可选，最多 2-3 句）</param>
        /// <returns>(system prompt, user prompt)</returns>
        public static (string sys, string user) BuildChroniclePrompt(
            string npcName,
            string locationKey,
            int startTime,
            int endTime,
            string modeDesc,
            string giftSummary,
            string dialogueHighlights)
        {
            bool isZh = IsChineseLanguage;
            string locDisplay = ResolveLocationDisplayName(locationKey);

            string sys = isZh
                ? $"你是《星露谷物语》中的 {npcName}。你刚和面前的农夫（@）结束了一场浪漫约会。"
                : $"You are {npcName} from Stardew Valley. You just finished a romantic date with the farmer (@).";

            string user = isZh
                ? $"请根据以下约会事实，写下一篇属于你的第一人称约会手账心流随笔（50-80字，纯文本台词，不要用引号包裹）：\n" +
                  $"- 约会地点：{locDisplay}\n" +
                  $"- 约会形式：{modeDesc}\n" +
                  (string.IsNullOrWhiteSpace(giftSummary) ? "" : $"- 礼物互动：{giftSummary}\n") +
                  (string.IsNullOrWhiteSpace(dialogueHighlights) ? "" : $"- 彼此聊过的话题或片刻：{dialogueHighlights}\n") +
                  $"\n要求：以第一人称（\"我\"）记录当下的心动或温情触动，强调情感体验而非报流水账，语言细腻。"
                : $"Write a first-person personal date diary entry (50-80 words, plain text, no quotes) based on:\n" +
                  $"- Location: {locDisplay}\n" +
                  $"- Date Type: {modeDesc}\n" +
                  (string.IsNullOrWhiteSpace(giftSummary) ? "" : $"- Gift: {giftSummary}\n") +
                  (string.IsNullOrWhiteSpace(dialogueHighlights) ? "" : $"- Moments: {dialogueHighlights}\n") +
                  $"\nFocus on emotional resonance and warmth rather than just a dry checklist.";

            return (sys, user);
        }

        /// <summary>
        /// 默认手账摘要（LLM 失败或超时时的模板化降级）。
        /// </summary>
        /// <param name="npcName">NPC 姓名</param>
        /// <param name="locationKey">约会地点 ID 或地图名</param>
        /// <param name="modeDesc">约会模式描述</param>
        /// <param name="hasGift">是否送礼</param>
        /// <returns>模板化手账文本</returns>
        public static string BuildFallbackChronicle(
            string npcName,
            string locationKey,
            string modeDesc,
            bool hasGift)
        {
            bool isZh = IsChineseLanguage;
            string locDisplay = ResolveLocationDisplayName(locationKey);

            string giftPart = hasGift
                ? (isZh ? "收到了特意为我准备的礼物，" : "received a thoughtful gift, ")
                : "";

            return isZh
                ? $"今晚在{locDisplay}度过了很开心的时光（{modeDesc}）。{giftPart}夜风很温柔，和农夫在一起的时间总是过得飞快。"
                : $"Spent a wonderful evening at {locDisplay} ({modeDesc}). {giftPart}Time always seems to fly by when we are together.";
        }
    }
}