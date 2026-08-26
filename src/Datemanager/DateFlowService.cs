using System;
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

            string latenessContext = isZh
                ? lateness switch
                {
                    LatenessLevel.OnTime       => "玩家准时或提前到达，你感到开心期待。",
                    LatenessLevel.SlightlyLate => "玩家迟到了一会儿（约 19:00-21:00 之间才到），你略带幽怨或调侃，但仍高兴他/她来了。",
                    LatenessLevel.VeryLate     => "玩家严重迟到（21:00 后才到），你已经等得很焦虑，开场时带着明显的委屈与责备。",
                    _                          => ""
                }
                : lateness switch
                {
                    LatenessLevel.OnTime       => "The player arrived on time. You are happy and excited.",
                    LatenessLevel.SlightlyLate => "The player is a bit late (arrived between 7pm and 9pm). You are mildly teasing or slightly pouty, but still glad they came.",
                    LatenessLevel.VeryLate     => "The player is very late (arrived after 9pm). You have been waiting anxiously and open with noticeable reproach.",
                    _                          => ""
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
                LatenessLevel.OnTime => isZh
                    ? "你来啦！今晚能和你一起度过，我真的很开心。"
                    : "You're here! I'm so glad we can spend tonight together.",
                LatenessLevel.SlightlyLate => isZh
                    ? "你终于来了……我都开始担心你了，不过没关系，你来了就好。"
                    : "You finally made it... I was starting to worry. But I'm glad you're here.",
                LatenessLevel.VeryLate => isZh
                    ? "……你来了。我已经等了好久了，差点以为你不来了。"
                    : "...You came. I waited so long, I almost thought you forgot about me.",
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
                ? "请向面前的玩家（@）说一句温情依依的告别台词（35字以内，直接输出台词）。"
                : "Say a warm goodbye to player (@) in 1-2 short sentences.";

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
    }
}
