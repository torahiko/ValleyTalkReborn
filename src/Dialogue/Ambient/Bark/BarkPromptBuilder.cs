using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using StardewValley;

namespace ValleytalkReborn;

/// <summary>
/// Bark Prompt 构建器 - 纯粹的模板生成器，彻底剥离启发式判断。
/// 职责：将游戏状态转换为"此刻正在发生的事 + 角色如何感知"的认知场景。
/// </summary>
internal sealed class BarkPromptBuilder
{
    private readonly AmbientBarkStateStore _stateStore;
    private static readonly Random _rng = new Random();

    internal BarkPromptBuilder(AmbientBarkStateStore stateStore)
    {
        _stateStore = stateStore;
    }

    /// <summary>
    /// 构建 Bark 请求（必须在主线程调用）
    /// </summary>
    internal DialogueModels.BarkRequest Build(NPC npc)
    {
        if (npc == null || Game1.player == null)
            return null;

        var character = DialogueBuilder.Instance?.GetCharacter(npc);
        var bio = character?.Bio;
        if (bio == null || !bio.EnableAmbientBarks || string.IsNullOrWhiteSpace(bio.AmbientBarkPrompt))
            return null;

        PlayerStateScanner.Scan();

        bool isZh = IsChineseLanguage;

        // 焦点决策：在 PlayerStateScanner 扫描完成后立即执行，确保感知数据已入桶
        if (!_stateStore.TryGet(npc.Name, out var barkState))
            barkState = _stateStore.GetOrCreate(npc.Name);

        var focusDecision = BarkFocusRouter.Decide(npc, barkState, bio, isZh);

        // ★ 核心消费闭环：若本轮选中了感知条目，立即标记已阅与审美疲劳
        if (focusDecision.MatchedPerception != null)
        {
            var p = focusDecision.MatchedPerception;
            if (p.Key == "PlayerActiveItem" && !string.IsNullOrEmpty(p.ItemId))
            {
                PerceptionManager.Instance?.MarkItemNoticedToday(npc.Name, p.ItemId);
            }

            if (PerceptionInjector.ShouldConsumeAfterInjection(p.Key))
            {
                PerceptionManager.Instance?.ConsumePerceptions(npc.Name, new[] { p });
            }
        }

        string systemPrompt = BuildSystemPrompt(isZh);
        string userPrompt = BuildUserPrompt(npc, bio, isZh, focusDecision);

        if (string.IsNullOrWhiteSpace(userPrompt))
            return null;

        // 回写本轮焦点决策到 State，供下一轮疲劳阻尼使用（主线程，无需额外锁）
        lock (barkState)
        {
            barkState.LastFocusType  = focusDecision.FocusType;
            barkState.LastSensoryKey = focusDecision.SensoryItemKey;
        }

        return new DialogueModels.BarkRequest
        {
            NpcName = npc.Name,
            SystemPrompt = systemPrompt,
            UserPrompt = userPrompt,
            IsChinese = isZh
        };
    }

    /// <summary>
    /// 构建 System Prompt - 认知基石：模拟"注意力自然流动"而非"生成台词"
    /// </summary>
    private static string BuildSystemPrompt(bool isZh)
    {
        var currentLang = LocalizedContentManager.CurrentLanguageCode;
        bool isStandardEn = !isZh && currentLang == LocalizedContentManager.LanguageCode.en;

        string lengthNotice = isZh
            ? "每条 15~25 个汉字，口语碎片的自然长度。"
            : isStandardEn
                ? "8-15 words per line, natural spoken fragment length."
                : $"In {currentLang}, 8-15 words per line.";

        // ★ 核心改动：Few-shot 示例彻底去除任何"逻辑链"痕迹，
        // 只保留"碎片 + 跳跃 + 搁置"的非线性质感
        string zhExample = JsonConvert.SerializeObject(new[]
        {
            "唔，手有点凉。",
            "刚才那个是什么声音来着...",
            "算了，应该没事。",
            "待会儿要不要出去转转？",
            "...还是算了吧，懒得动。"
        });

        string enExample = JsonConvert.SerializeObject(new[]
        {
            "Hands are kinda cold.",
            "What was that noise just now...",
            "Eh, probably nothing.",
            "Should I go out later?",
            "...Nah, can't be bothered."
        });

        if (isZh)
        {
            return $@"你要模拟一个真实的人在当前场景里，注意力自然流动时脑子里冒出来的念头。

【你要做的事】
想象你就是这个角色，此刻站在/坐在/走在这个地方。你的注意力会落在什么地方？会因为什么卡住或者飘走？会突然想起什么又突然觉得算了？

这不是「生成台词」，是「你在想什么」——那些半成品的、没说完的、突然断掉的念头。

【念头如何流动】
- 有时候一个念头自然接着上一个（因为相关、因为顺手、因为还没想完）
- 有时候突然跳到别的地方（被什么勾起来的、想起另一件事、单纯走神）
- 有时候想到一半就不想了（懒得深究、觉得没意思、被别的打断）
- 有时候就是身体感觉、周围声音、无聊的等待

不用每个念头都能解释清楚为什么这么想。真实的人脑子里很多念头就是「诶我怎么突然想到这个」。

【语言怎么出来】
念头在心里转的时候，只有一部分会真的「说出半句」——那种自言自语式的碎片。
不是完整的句子，是那种「唔...」「算了」「待会儿...」「诶」这样的半截话。

看角色人设里的 [SPOKEN HABITS] / [VOICE & ATTITUDE]，他们平时说话什么风格，这些碎碎念就是什么风格的内心版本。
如果人设里写了英文口癖词（Man / Dude / Heh），别直接抄，想想那个词在他们嘴里是什么「感觉」，用地道中文找到同样感觉的说法。

【输出格式】
纯 JSON 数组：[""念头1"", ""念头2"", ...]
- 4~6 条
- {lengthNotice}
- 首字符 [，末字符 ]，不要包 Markdown 代码块

下面是格式示例（内容随便写的，和实际场景无关，别学话题）：
{zhExample}";
        }
        else
        {
            return $@"You simulate a real person's wandering attention in the current scene — the half-formed thoughts that surface when their mind drifts.

[WHAT YOU'RE DOING]
Imagine you ARE this character, standing/sitting/moving in this place right now. Where does your attention land? What makes it stick or drift away? What do you suddenly remember, then suddenly drop?

This is not ""generating dialogue."" This is ""what are you thinking"" — the unfinished, half-said, suddenly-abandoned thoughts.

[HOW THOUGHTS MOVE]
- Sometimes one thought naturally follows the last (related, convenient, still processing)
- Sometimes it jumps somewhere else (triggered by something, remembered another thing, just spaced out)
- Sometimes you drop it mid-thought (too lazy to pursue, feels pointless, got interrupted)
- Sometimes it's just bodily sensations, ambient sounds, idle waiting

Not every thought needs a clear reason for being there. Real minds have lots of ""huh, why did I just think that"" moments.

[HOW LANGUAGE EMERGES]
When thoughts turn in your head, only some reach the lips as muttered fragments.
Not full sentences — the ""hmm..."" ""forget it"" ""maybe later..."" ""huh"" kind of half-spoken bits.

Check [SPOKEN HABITS] / [VOICE & ATTITUDE] in the persona. However they normally talk, these mutterings are the internal version of that style.
If the persona lists English catchphrases (Man / Dude / Heh), don't copy them literally — grasp their FEEL and express it naturally in the target language.

[OUTPUT FORMAT]
Plain JSON array: [""thought1"", ""thought2"", ...]
- 4-6 lines
- {lengthNotice}
- Must start with [ and end with ], no Markdown wrapping

Format example below (random content, unrelated to actual scene, don't copy topics):
{enExample}";
        }
    }

    /// <summary>
    /// 构建 User Prompt - 消费 Router 决策输出，仅组装，不自行判断
    /// </summary>
    private string BuildUserPrompt(NPC npc, BioData bio, bool isZh, BarkFocusDecision decision)
    {
        var sb = new StringBuilder();

        // ── 1. 角色人设 ──
        string rawPrompt = bio.AmbientBarkPrompt.Trim();
        if (isZh)
            rawPrompt = NpcNameLocalizer.LocalizeNamesInText(rawPrompt);

        rawPrompt = EnrichWithDynamicState(npc, rawPrompt, isZh);

        // 关系标签降级为人设末尾静态注脚，不再独立占段，彻底去除强锚点宣告
        string relNote = GetRelationshipNote(npc, isZh);
        if (!string.IsNullOrEmpty(relNote))
            rawPrompt = rawPrompt + "\n" + relNote;

        sb.AppendLine(isZh ? "### [你是谁]" : "### [WHO YOU ARE]");
        sb.AppendLine(rawPrompt);
        sb.AppendLine();

        // ── 2. 环境底色（最弱的地点/时段/持续状态描述）──
        string ambientScene = BuildAmbientScene(npc, isZh);
        if (!string.IsNullOrEmpty(ambientScene))
        {
            sb.AppendLine(isZh ? "### [此刻]" : "### [RIGHT NOW]");
            sb.AppendLine(ambientScene);
            sb.AppendLine();
        }

        // ── 3. 单焦点注入（Router 决选的唯一触点，FreeDrift 时为空）──
        if (!string.IsNullOrEmpty(decision.InjectedContextLine))
        {
            sb.AppendLine(isZh ? "### [注意力落在]" : "### [ATTENTION LANDS ON]");
            sb.AppendLine(decision.InjectedContextLine);
            sb.AppendLine();
        }

        // ── 4. 生成指令 ──
        sb.AppendLine(BuildThinkingPrompt(bio, isZh, decision.AllowThinkingLens));

        return sb.ToString();
    }

    /// <summary>
    /// 构建最弱的环境底色：仅包含地点、时段、跟随/约会持续状态、POI 动作现状（降级为一行）。
    /// NearbyNPCs、Perceptions、SceneContext 全部移除，由 BarkFocusRouter 单点控制。
    /// </summary>
    private static string BuildAmbientScene(NPC npc, bool isZh)
    {
        var loc = npc?.currentLocation;
        if (loc == null) return null;

        bool isIndoor = !loc.IsOutdoors    || loc is StardewValley.Locations.FarmHouse
                        || loc is StardewValley.Locations.IslandFarmHouse;

        string locName  = EnvironmentScanner.GetLocationFriendlyName(loc.Name);
        string timeDesc = GetTimeOfDayDescription(isZh);
        string weather  = (!isIndoor) ? GetWeatherDescription(isZh) : null;

        var sb = new StringBuilder();

        if (isZh)
        {
            sb.Append($"你在 {locName}");
            if (isIndoor)  sb.Append("（室内）");
            else           sb.Append($"（室外，{weather}）");
            sb.Append($"，{timeDesc}。");

            // 跟随/约会：降级为一句无戏剧性陈述（底色）
            if (DialogueUtilities.IsOnDate(npc))
            {
                sb.Append(" 和玩家出来走走。");
            }
            else if (DialogueUtilities.IsFollowingSafe(npc))
            {
                sb.Append(" 陪着玩家走着。");
            }
            else if (CompanionScheduleManager.Instance?.IsStayHomeActive(npc.Name) == true)
            {
                // POI context 降级为最平淡的一句陈述
                string poiCtx = CompanionScheduleManager.Instance?.GetActivePoiContext(npc.Name);
                if (!string.IsNullOrEmpty(poiCtx))
                {
                    // 只取第一句，避免 POI 描述过长成为新的强锚点
                    string firstSentence = poiCtx.Split(new[]{'。','.'}, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
                    if (!string.IsNullOrEmpty(firstSentence))
                        sb.Append($" {NpcNameLocalizer.LocalizeNamesInText(firstSentence)}。");
                }
            }
        }
        else
        {
            sb.Append($"You're in {locName}");
            if (isIndoor)  sb.Append(" (indoors)");
            else           sb.Append($" (outdoors, {weather})");
            sb.Append($", {timeDesc}.");

            if (DialogueUtilities.IsOnDate(npc))
            {
                sb.Append(" Out with the player.");
            }
            else if (DialogueUtilities.IsFollowingSafe(npc))
            {
                sb.Append(" Walking with the player.");
            }
            else if (CompanionScheduleManager.Instance?.IsStayHomeActive(npc.Name) == true)
            {
                string poiCtx = CompanionScheduleManager.Instance?.GetActivePoiContext(npc.Name);
                if (!string.IsNullOrEmpty(poiCtx))
                {
                    string firstSentence = poiCtx.Split(new[]{'.',';'}, StringSplitOptions.RemoveEmptyEntries)
                                                 .FirstOrDefault()?.Trim();
                    if (!string.IsNullOrEmpty(firstSentence))
                        sb.Append($" {firstSentence}.");
                }
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// 构建"思考提示" - 不再是"生成任务"，而是"开始想吧"
    /// </summary>
    private static string BuildThinkingPrompt(BioData bio, bool isZh, bool allowLens)
    {
        var sb = new StringBuilder();
        sb.AppendLine(isZh ? "### [开始想]" : "### [START THINKING]");

        // 仅当焦点决策允许时注入 Preoccupation lens（Introspective 心事焦点时开启）
        if (allowLens)
        {
            var lenses = GetRandomThinkingLens(bio, 1, isZh);
            if (lenses.Count > 0)
            {
                sb.AppendLine(isZh
                    ? $"你的注意力可能先落在：{lenses[0]}（也可以从别的地方开始）"
                    : $"Your attention might land on: {lenses[0]} (or start elsewhere)");
                sb.AppendLine();
            }
        }

        // FreeDrift / Sensory / Interactive 时提供正向落脚点
        if (!allowLens)
        {
            sb.AppendLine(isZh
                ? "此时脑子里没有任何特定心事。注意力随处落脚——周围的细微动静、当下的身体感觉、脚下的路、或是单纯走神放空，把冒出的念头说出半句："
                : "No particular thoughts on your mind right now. Attention lands anywhere — ambient sounds, a physical sensation, the ground underfoot, or just drifting — mutter whatever surfaces:");
            sb.AppendLine();
        }

        if (isZh)
        {
            sb.AppendLine("现在，让你的注意力自然流动，把那些冒出来的念头说出半句：");
            sb.AppendLine();
            sb.AppendLine("- 4~6 条，每条 15~25 个汉字");
            sb.AppendLine("- 可以连着想，可以突然跳开，可以想到一半就算了");
            sb.AppendLine("- 用你自己的说话方式（看上面 [你是谁] 里的语言习惯）");
            sb.AppendLine("- 输出纯 JSON 数组：[\"...\", \"...\", ...]");
        }
        else
        {
            sb.AppendLine("Now, let your attention flow naturally and mutter those surfacing thoughts:");
            sb.AppendLine();
            sb.AppendLine("- 4-6 lines, 8-15 words each");
            sb.AppendLine("- Can flow together, jump around, or drop mid-thought");
            sb.AppendLine("- Use your own speaking style (see [WHO YOU ARE] above)");
            sb.AppendLine("- Output plain JSON array: [\"...\", \"...\", ...]");
        }

        return sb.ToString();
    }

    /// <summary>
    /// 获取随机的"注意力入口"（不是话题，是感知角度）
    /// </summary>
    private static List<string> GetRandomThinkingLens(BioData bio, int count, bool isZh)
    {
        if (bio?.Preoccupations == null || bio.Preoccupations.Count == 0)
            return new List<string>();

        return bio.Preoccupations
            .OrderBy(_ => _rng.Next())
            .Take(Math.Min(count, bio.Preoccupations.Count))
            .ToList();
    }

    // ══════════════════════════════════════════════════════════════
    // 辅助方法（保持原有逻辑，仅做小幅调整）
    // ══════════════════════════════════════════════════════════════

    private static string EnrichWithDynamicState(NPC npc, string basePrompt, bool isChinese)
    {
        if (npc == null || string.IsNullOrWhiteSpace(basePrompt))
            return basePrompt;

        var bio = DialogueBuilder.Instance?.GetCharacter(npc)?.Bio;
        string stateText = ProgressStateResolver.ResolveActiveState(npc, bio?.ProgressStates);

        if (string.IsNullOrWhiteSpace(stateText))
            return basePrompt;

        return $"{basePrompt}\n\n[CURRENT STATE: {stateText}]";
    }

    /// <summary>
    /// 将关系信息降级为人设末尾的静态注脚（一行克制文本），而非独立段落。
    /// 仅在有真实关系（恋爱/婚姻/≥6 心好友）时注入，普通认识不注入。
    /// </summary>
    private static string GetRelationshipNote(NPC npc, bool isZh)
    {
        if (npc == null) return null;

        if (PolyamorySweetLoveBridge.IsOfficialSpouse(npc) || PolyamorySweetLoveBridge.IsUnofficialSpouse(npc))
        {
            return isZh
                ? "[Identity Note: 你和玩家是伴侣关系。]"
                : "[Identity Note: You are in a romantic relationship with the player.]";
        }

        var player = Game1.player;
        if (player?.friendshipData == null) return null;
        if (!player.friendshipData.TryGetValue(npc.Name, out var fs) || fs == null) return null;

        if (fs.IsMarried() || fs.IsDating())
        {
            return isZh
                ? "[Identity Note: 你和玩家是伴侣关系。]"
                : "[Identity Note: You are in a romantic relationship with the player.]";
        }

        // ≥6 心好友：注入但措辞更淡
        int hearts = fs.Points / 250;
        if (hearts >= 6)
        {
            return isZh
                ? $"[Identity Note: 你和玩家是好朋友（{hearts} 心）。]"
                : $"[Identity Note: You and the player are close friends ({hearts} hearts).]";
        }

        return null; // <6 心不注入，陌生/普通关系不构成有效 Identity Note
    }

    private static string GetWeatherDescription(bool isZh)
    {
        if (Game1.isSnowing) return isZh ? "下雪" : "snowing";
        if (Game1.isRaining) return isZh ? "下雨" : "raining";
        if (Game1.isLightning) return isZh ? "雷雨" : "stormy";
        if (Game1.isDebrisWeather) return isZh ? "刮风" : "windy";
        return isZh ? "晴天" : "clear";
    }

    private static string GetTimeOfDayDescription(bool isZh)
    {
        int time = Game1.timeOfDay;
        if (time < 600) return isZh ? "凌晨" : "very early morning";
        if (time < 1200) return isZh ? "上午" : "morning";
        if (time < 1400) return isZh ? "中午" : "midday";
        if (time < 1800) return isZh ? "下午" : "afternoon";
        if (time < 2000) return isZh ? "傍晚" : "evening";
        if (time < 2200) return isZh ? "晚上" : "night";
        return isZh ? "深夜" : "late night";
    }

    private static bool IsChineseLanguage =>
        LocalizedContentManager.CurrentLanguageCode == LocalizedContentManager.LanguageCode.zh;
}