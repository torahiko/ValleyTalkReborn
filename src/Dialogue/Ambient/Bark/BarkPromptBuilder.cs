using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using StardewModdingAPI;
using StardewValley;

namespace ValleytalkReborn;

/// <summary>
/// Bark Prompt 构建器 - 纯粹的模板生成器，彻底剥离启发式判断。
/// 职责：将游戏状态转换为"此刻正在发生的事 + 角色如何感知"的认知场景。
/// </summary>
internal sealed class BarkPromptBuilder
{
    private readonly AmbientBarkStateStore _stateStore;

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

        // 核心消费闭环：若本轮选中了感知条目，立即标记已阅与审美疲劳
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

        // 回写本轮焦点决策到 State，供下一轮疲劳阻尼使用
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
        bool needLangConstraint = ShouldInjectLanguageConstraint(out string targetLangZh, out string targetLangEn);

        string targetLangName = targetLangEn;
        string lengthNotice = GetLengthNotice(currentLang, targetLangName, isZh);

        // Few-shot 示例全量注入：3组正交的日常碎碎念（无修辞口语、真实碎渣感）
        string zhExample1 = JsonConvert.SerializeObject(new[]
        {
            "手好凉，早知道揣兜里了。",
            "刚才树丛里是啥动静？",
            "算了，八成是鸟。",
            "待会儿要不要去海滩溜达一圈？",
            "……懒得走了，脚底板有点酸。"
        });

        string zhExample2 = JsonConvert.SerializeObject(new[]
        {
            "今天这风吹得脑门有点疼。",
            "那边的栅栏怎么又歪了一块……",
            "改天得找个锤子敲一下。",
            "衣服上什么时候蹭了块泥？",
            "……啊，好困，昨晚没睡够。"
        });

        string zhExample3 = JsonConvert.SerializeObject(new[]
        {
            "刚才出门锁门没有来着？",
            "锁了锁了，别疑神疑鬼的。",
            "好香……谁家在弄吃的？",
            "肚子好像开始叫了。",
            "待会儿去酒馆看眼今天有什么吃的。"
        });

        // 真实英语内心碎碎念：掉主语、口头感叹、断句、大白话
        string enExample1 = JsonConvert.SerializeObject(new[]
        {
            "Ugh, fingers are completely numb. Should've grabbed my gloves.",
            "What the heck was that rustling just now?",
            "Squirrel, probably. Or whatever, don't care.",
            "Thought about heading to the beach, but honestly?",
            "...Yeah no, feet hurt way too much. Not moving."
        });

        string enExample2 = JsonConvert.SerializeObject(new[]
        {
            "Wind's blowing straight into my eyes, damn it.",
            "Wait, that fence post is crooked again. Swear I fixed that.",
            "Gotta find the hammer sometime this week. Maybe Robin has one.",
            "How'd I even get mud all over my sleeve?",
            "...God I'm beat. Should not have stayed up so late."
        });

        string enExample3 = JsonConvert.SerializeObject(new[]
        {
            "Locked the front door, right? Pretty sure I did.",
            "Yeah, turned the latch twice. Stop being paranoid.",
            "Wait, what smells so good? Someone baking bread?",
            "Stomach's literally growling now. Great.",
            "Gotta hit the saloon later and see what Gus has cooking."
        });

        if (isZh)
        {
            string langConstraintZh = needLangConstraint ? $"\n- 所有输出请严格使用“{targetLangZh}”" : "";

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

看角色人设里的 [SPOKEN HABITS] / [VOICE & ATTITUDE]，这些碎碎念就是其性格风格的内心版本。
人设中体现的口语习惯与感叹词，请完全转化为地道中文里具有同等语感的情绪虚词与日常口吻；提及的人名、地名严格采用上下文中已给出的中文名称。

【输出格式】
纯 JSON 数组：[""念头1"", ""念头2"", ...]
- 4~6 条
- {lengthNotice}{langConstraintZh}
- 首字符 [，末字符 ]，不要包 Markdown 代码块
- 注意：收尾方式是自然流动的（停在生理感官、纯粹发呆、随口打算等均可），切勿机械式地每次都用“算了”收尾

格式示例（注意不同示例里念头流动和收尾的多样性，内容与实际场景无关，别学话题）：
示例1（犯懒搁置）：
{zhExample1}

示例2（走神与困意）：
{zhExample2}

示例3（琐事与食欲）：
{zhExample3}";
        }
        else
        {
            string langRequirementSection = needLangConstraint ? $@"
[LANGUAGE REQUIREMENT]
All thoughts and uttered fragments MUST be strictly written in {targetLangEn}.
Do not output in English or any other language unless quoting an untranslated name.
" : "";

            string catchphraseGuidance = needLangConstraint
                ? $"Read [SPOKEN HABITS] / [VOICE & ATTITUDE] in the persona as cues for emotional cadence. Naturally transmute any catchphrases, fillers, or speech mannerisms into authentic, native spoken equivalents in {targetLangEn}. Faithfully use the localized character and place names provided in the context."
                : "Read [SPOKEN HABITS] / [VOICE & ATTITUDE] in the persona as cues for emotional cadence. Express their natural spoken feel casually and authentically.";

            string langOutputConstraint = needLangConstraint ? $"\n- MUST be written strictly in {targetLangEn}" : "";

            return $@"You simulate a real person's wandering attention in the current scene — the half-formed thoughts that surface when their mind drifts.
{langRequirementSection}
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
Not full textbook sentences — the ""ugh..."" ""whatever"" ""wait..."" ""damn it"" kind of half-spoken bits with dropped pronouns and authentic rhythm.

Check [SPOKEN HABITS] / [VOICE & ATTITUDE] in the persona. However they normally talk, these mutterings are the internal version of that style.
{catchphraseGuidance}

[OUTPUT FORMAT]
Plain JSON array: [""thought1"", ""thought2"", ...]
- 4-6 lines
- {lengthNotice}{langOutputConstraint}
- Must start with [ and end with ], no Markdown wrapping
- NOTE: Endings must vary naturally (physical sensations, wandering off, small plans). DO NOT mechanically end every generation with ""forget it"" or giving up.

Format examples below (note the variety of flow and endings, unrelated to actual scene, don't copy topics):
Example 1 (Idle & Drop):
{enExample1}

Example 2 (Spacing Out & Fatigue):
{enExample2}

Example 3 (Paranoia & Appetite):
{enExample3}";
        }
    }

    /// <summary>
    /// 构建 User Prompt - 消费 Router 决策输出，仅组装，彻底杜绝二次摇号
    /// </summary>
    private string BuildUserPrompt(NPC npc, BioData bio, bool isZh, BarkFocusDecision decision)
    {
        var sb = new StringBuilder();

        // ── 1. 角色人设 ──
        string rawPrompt = bio.AmbientBarkPrompt.Trim();
        if (isZh)
            rawPrompt = NpcNameLocalizer.LocalizeNamesInText(rawPrompt);

        rawPrompt = EnrichWithDynamicState(npc, rawPrompt, isZh);

        sb.AppendLine(isZh ? "### [你是谁]" : "### [WHO YOU ARE]");
        sb.AppendLine(rawPrompt);
        sb.AppendLine();

        // ── 2. 常驻农夫人物底色（不参与竞争，无门槛持续展示，强约束性别与代词）──
        string farmerNote = BuildFarmerIdentityNote(npc, isZh);
        if (!string.IsNullOrEmpty(farmerNote))
        {
            sb.AppendLine(isZh ? "### [已知人物底色]" : "### [KNOWN CHARACTER]");
            sb.AppendLine(farmerNote);
            sb.AppendLine();
        }

        // ── 3. 环境底色（最弱的地点/时段/持续状态描述）──
        string ambientScene = BuildAmbientScene(npc, isZh);
        if (!string.IsNullOrEmpty(ambientScene))
        {
            sb.AppendLine(isZh ? "### [此刻]" : "### [RIGHT NOW]");
            sb.AppendLine(ambientScene);
            sb.AppendLine();
        }

        // ── 4. 单焦点注入（Router 决选的唯一触点，FreeDrift 时为空）──
        bool isFreeDrift = string.IsNullOrWhiteSpace(decision.InjectedContextLine);
        if (!isFreeDrift)
        {
            sb.AppendLine(isZh ? "### [注意力落在]" : "### [ATTENTION LANDS ON]");
            sb.AppendLine(decision.InjectedContextLine);
            sb.AppendLine();
        }

        // ── 5. 生成指令（无缝对齐是否有单一焦点）──
        sb.AppendLine(BuildThinkingPrompt(isZh, isFreeDrift));

        return sb.ToString();
    }

    /// <summary>
    /// 构建最弱的环境底色：仅包含地点、时段、跟随/约会持续状态、POI 动作现状（降级为一行）。
    /// </summary>
    private static string BuildAmbientScene(NPC npc, bool isZh)
    {
        var currentEvent = Game1.CurrentEvent;
        if (currentEvent != null && currentEvent.isFestival)
        {
            string festivalName = currentEvent.FestivalName;
            if (string.IsNullOrWhiteSpace(festivalName) || (isZh && !ContainsCjkCharacter(festivalName)))
            {
                festivalName = isZh ? "节日集会" : "the Festival";
            }

            if (isZh)
            {
                return $"今天是【{festivalName}】。你正身处热闹的节日现场，镇民们都聚集在周围，暂时放下了日常劳作。";
            }
            else
            {
                return $"Today is the [{festivalName}]. You are gathered at the festival grounds with the other villagers, away from daily routines.";
            }
        }

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

            if (DialogueUtilities.IsOnDate(npc))
            {
                sb.Append(" 和玩家出来走走。");
            }
            else if (DialogueUtilities.IsFollowingSafe(npc))
            {
                sb.Append(" 陪着玩家走着。");
            }
            else
            {
                string poiCtx = CompanionScheduleManager.Instance?.GetActivePoiContext(npc.Name);
                if (!string.IsNullOrEmpty(poiCtx))
                {
                    string firstSentence = poiCtx.Split(new[]{'。','.'}, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
                    if (!string.IsNullOrEmpty(firstSentence))
                    {
                        if (!ContainsCjkCharacter(firstSentence))
                        {
                            ModEntry.SMonitor?.Log($"[BarkPromptBuilder] POI context 为非中文文本，zh 客户端跳过注入: {firstSentence}", LogLevel.Trace);
                        }
                        else
                        {
                            sb.Append($" {NpcNameLocalizer.LocalizeNamesInText(firstSentence)}。");
                        }
                    }
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
            else
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

        if (isIndoor)
        {
            sb.Append(BuildIndoorWeatherSentence(isZh));
            string objs = BuildNearbyObjectsSentence(npc, isZh);
            if (objs != null) sb.Append(objs);
        }

        return sb.ToString();
    }

    /// <summary>
    /// 构建室内常驻天气句（优先级：绿雨 > 雪 > 雷 > 雨 > 风 > 晴）。
    /// </summary>
    private static string BuildIndoorWeatherSentence(bool isZh)
    {
        if (Game1.isGreenRain)          return isZh ? "屋外正下着诡异的绿雨。" : "Strange green rain is falling outside.";
        if (Game1.IsSnowingHere())      return isZh ? "屋外正下着雪。"   : "It's snowing outside.";
        if (Game1.IsLightningHere())    return isZh ? "屋外雷雨大作。"   : "A thunderstorm is raging outside.";
        if (Game1.IsRainingHere())      return isZh ? "屋外正下着雨。"   : "It's raining outside.";
        if (Game1.isDebrisWeather)      return isZh ? "屋外正刮着风。"   : "It's windy outside.";
        return isZh ? "屋外是个大晴天。" : "Clear skies outside.";
    }

    /// <summary>
    /// 构建室内周围物件句。
    /// </summary>
    private static string BuildNearbyObjectsSentence(NPC npc, bool isZh)
    {
        if (npc?.currentLocation == null) return null;

        var exclude = new List<string>();
        if (Game1.player != null)
        {
            if (!string.IsNullOrEmpty(Game1.player.displayName))
                exclude.Add(Game1.player.displayName);
            if (!string.IsNullOrEmpty(Game1.player.Name))
                exclude.Add(Game1.player.Name);
        }

        List<string> nearby;
        try
        {
            nearby = SceneContextBuilder.BuildNearbyList(npc, radiusTiles: 4, maxItems: 3, excludeNames: exclude);
        }
        catch (Exception ex)
        {
            ModEntry.SMonitor?.Log($"[BarkPromptBuilder] NearbyList 读取失败，跳过周围物件行: {ex.Message}", LogLevel.Trace);
            return null;
        }

        if (nearby == null) return null;

        var names = nearby
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l => l.Trim())
            .Take(3)
            .ToList();

        if (names.Count == 0) return null;

        string joined = string.Join(isZh ? "、" : ", ", names);
        return isZh ? $"你周围有：{joined}。" : $"Around you: {joined}.";
    }

    /// <summary>
    /// 判定文本是否包含 CJK 统一表意文字（主平面 一-鿿）。
    /// </summary>
    private static bool ContainsCjkCharacter(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        foreach (char c in text)
        {
            if (c >= '一' && c <= '鿿') return true;
        }
        return false;
    }

    /// <summary>
    /// 构建"思考提示" - 彻底剥离二次摇号，根据单焦点状态提供无冲突的生成指令
    /// </summary>
    private static string BuildThinkingPrompt(bool isZh, bool isFreeDrift)
    {
        var sb = new StringBuilder();
        bool needLangConstraint = ShouldInjectLanguageConstraint(out string targetLangZh, out string targetLangEn);
        bool isFestivalNow = Game1.CurrentEvent?.isFestival == true;

        sb.AppendLine(isZh ? "### [开始想]" : "### [START THINKING]");

        if (isFreeDrift)
        {
            sb.AppendLine(isZh
                ? "此时脑子里没有任何特定心事。注意力随处落脚——周围的细微动静、当下的身体感觉、脚下的路、或是单纯走神放空，把冒出的念头说出半句："
                : "No particular thoughts on your mind right now. Attention lands anywhere — ambient sounds, a physical sensation, the ground underfoot, or just drifting — mutter whatever surfaces:");
        }
        else
        {
            sb.AppendLine(isZh
                ? "顺着你当下的注意力自然流动，把脑子里冒出来的念头说出半句："
                : "Let your attention flow naturally from what you notice, muttering whatever surfaces:");
        }
        sb.AppendLine();

        if (isZh)
        {
            sb.AppendLine("- 4~6 条，每条 15~25 个汉字");
            sb.AppendLine("- 可以连着想，可以突然跳开，可以想到一半就算了");
            sb.AppendLine("- 用你自己的说话方式（看上面 [你是谁] 里的语言习惯）");
            if (isFestivalNow)
            {
                sb.AppendLine("- 此时正身处节日集会，念头可围绕集会活动、食物、周围人潮、或是想早点回家休息等现场心境");
            }
            if (needLangConstraint)
            {
                sb.AppendLine($"- 所有输出请严格使用“{targetLangZh}”");
            }
            sb.AppendLine("- 输出纯 JSON 数组：[\"...\", \"...\", ...]");
        }
        else
        {
            var currentLang = LocalizedContentManager.CurrentLanguageCode;
            string targetLangName = targetLangEn;
            string lengthBullet = GetLengthBulletDescription(currentLang, targetLangName);

            sb.AppendLine($"- 4-6 lines, {lengthBullet}");
            sb.AppendLine("- Can flow together, jump around, or drop mid-thought");
            sb.AppendLine("- Use your own speaking style (see [WHO YOU ARE] above)");
            if (isFestivalNow)
            {
                sb.AppendLine("- You are at a festival; thoughts naturally drift to the events, food, crowds, or wanting to head home");
            }
            if (needLangConstraint)
            {
                sb.AppendLine($"- All output must strictly use {targetLangEn}");
            }
            sb.AppendLine("- Output plain JSON array: [\"...\", \"...\", ...]");
        }

        return sb.ToString();
    }

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
    /// 构建常驻的农夫身份与代词锚点（不参与路由竞争，始终提供给 LLM 作为常识底色）
    /// </summary>
    private static string BuildFarmerIdentityNote(NPC npc, bool isZh)
    {
        var player = Game1.player;
        if (player == null) return null;

        string pName = !string.IsNullOrWhiteSpace(player.displayName)
            ? player.displayName
            : (!string.IsNullOrWhiteSpace(player.Name) ? player.Name : (isZh ? "农夫" : "Farmer"));

        // 1. 严格锁定生理性别与代词约束，杜绝异性恋先验导致的性别幻觉
        bool isMale = player.IsMale;
        string genderStr = isZh ? (isMale ? "男性" : "女性") : (isMale ? "Male" : "Female");
        string pronoun = isZh ? (isMale ? "他" : "她") : (isMale ? "he/him" : "she/her");
        string forbiddenPronoun = isZh ? (isMale ? "她" : "他") : (isMale ? "she/her" : "he/him");

        var parts = new List<string>();

        if (isZh)
        {
            parts.Add($"玩家姓名：{pName}");
            parts.Add($"性别性别：{genderStr}（自言自语中若提及玩家，第三人称代词使用“{pronoun}”）");
        }
        else
        {
            parts.Add($"Player Name: {pName}");
            parts.Add($"Gender: {genderStr} (When referring to the player, 3rd-person pronoun MUST strictly be \"{pronoun}\"");
        }

        // 2. 关系状态解析（伴侣 > 恋爱中 > 挚友 > 熟人 > 镇民）
        bool isSpouse = PolyamorySweetLoveBridge.IsOfficialSpouse(npc)
                     || PolyamorySweetLoveBridge.IsUnofficialSpouse(npc);

        int hearts = 0;
        bool isDating = false;

        if (player.friendshipData != null && player.friendshipData.TryGetValue(npc.Name, out var fs) && fs != null)
        {
            isSpouse = isSpouse || fs.IsMarried();
            isDating = fs.IsDating();
            hearts = fs.Points / 250;
        }

        if (isSpouse)
        {
            parts.Add(isZh ? "关系状态：你和玩家是伴侣/配偶关系" : "Relationship: Married / Spouses");
        }
        else if (isDating)
        {
            parts.Add(isZh ? "关系状态：你正与玩家交往恋爱中" : "Relationship: Dating the player");
        }
        else if (hearts >= 6)
        {
            parts.Add(isZh ? $"关系状态：与玩家是非常要好的朋友（{hearts}心）" : $"Relationship: Close friends ({hearts} hearts)");
        }
        else if (hearts >= 2)
        {
            parts.Add(isZh ? $"关系状态：普通熟人朋友（{hearts}心）" : $"Relationship: Acquaintances ({hearts} hearts)");
        }
        else
        {
            parts.Add(isZh ? "关系状态：点头之交的镇民与农夫" : "Relationship: Casual acquaintances");
        }

        // 3. 称谓习惯（若有）
        string callsign = MemoryManager.Instance?.GetCustomCallsign(npc.Name);
        if (!string.IsNullOrWhiteSpace(callsign))
        {
            parts.Add(isZh
                ? $"称呼习惯：提及玩家时的习惯称呼为\"{callsign}\""
                : $"Callsign habit: Habitually referred to as \"{callsign}\"");
        }

        return isZh
            ? $"- {string.Join("\n- ", parts)}"
            : $"- {string.Join("\n- ", parts)}";
    }

    private static string GetWeatherDescription(bool isZh)
    {
        if (Game1.isGreenRain)  return isZh ? "绿雨" : "green rain";
        if (Game1.isSnowing)    return isZh ? "下雪" : "snowing";
        if (Game1.isLightning)  return isZh ? "雷雨" : "stormy";
        if (Game1.isRaining)    return isZh ? "下雨" : "raining";
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

    /// <summary>
    /// 获取目标语言规范名称（包含原版所有内置语言与 LanguageCode.mod 扩展）。
    /// </summary>
    private static string GetTargetLanguageDisplayName(LocalizedContentManager.LanguageCode code, bool inChinese)
    {
        switch (code)
        {
            case LocalizedContentManager.LanguageCode.zh:
                return inChinese ? "中文" : "Chinese";
            case LocalizedContentManager.LanguageCode.ja:
                return inChinese ? "日语" : "Japanese";
            case LocalizedContentManager.LanguageCode.ru:
                return inChinese ? "俄语" : "Russian";
            case LocalizedContentManager.LanguageCode.pt:
                return inChinese ? "葡萄牙语" : "Portuguese";
            case LocalizedContentManager.LanguageCode.es:
                return inChinese ? "西班牙语" : "Spanish";
            case LocalizedContentManager.LanguageCode.de:
                return inChinese ? "德语" : "German";
            case LocalizedContentManager.LanguageCode.th:
                return inChinese ? "泰语" : "Thai";
            case LocalizedContentManager.LanguageCode.fr:
                return inChinese ? "法语" : "French";
            case LocalizedContentManager.LanguageCode.ko:
                return inChinese ? "韩语" : "Korean";
            case LocalizedContentManager.LanguageCode.it:
                return inChinese ? "意大利语" : "Italian";
            case LocalizedContentManager.LanguageCode.tr:
                return inChinese ? "土耳其语" : "Turkish";
            case LocalizedContentManager.LanguageCode.hu:
                return inChinese ? "匈牙利语" : "Hungarian";
            case LocalizedContentManager.LanguageCode.mod:
                try
                {
                    if (LocalizedContentManager.CurrentModLanguage != null)
                    {
                        string modLang = LocalizedContentManager.CurrentModLanguage.LanguageCode
                                         ?? LocalizedContentManager.CurrentModLanguage.Id;
                        if (!string.IsNullOrWhiteSpace(modLang))
                            return modLang;
                    }
                }
                catch
                {
                    // 降级容错
                }
                return "English";
            default:
                return "English";
        }
    }

    /// <summary>
    /// 判断当前是否需要注入语言约束：当目标语言为英语时返回 false（不注入）。
    /// </summary>
    private static bool ShouldInjectLanguageConstraint(out string targetLangZh, out string targetLangEn)
    {
        var code = LocalizedContentManager.CurrentLanguageCode;
        targetLangZh = GetTargetLanguageDisplayName(code, inChinese: true);
        targetLangEn = GetTargetLanguageDisplayName(code, inChinese: false);

        if (code == LocalizedContentManager.LanguageCode.en || string.Equals(targetLangEn, "English", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    private static string GetLengthNotice(LocalizedContentManager.LanguageCode currentLang, string targetLangName, bool isZh)
    {
        if (isZh)
            return "每条 15~25 个汉字，口语碎片的自然长度。";

        if (currentLang == LocalizedContentManager.LanguageCode.ja)
            return "15-30 Japanese characters per line, natural spoken fragment length.";

        if (currentLang == LocalizedContentManager.LanguageCode.ko)
            return "15-30 Korean characters/syllables per line, natural spoken fragment length.";

        if (currentLang == LocalizedContentManager.LanguageCode.en)
            return "8-15 words per line, natural spoken fragment length.";

        return $"8-15 words per line in {targetLangName}, natural spoken fragment length.";
    }

    private static string GetLengthBulletDescription(LocalizedContentManager.LanguageCode currentLang, string targetLangName)
    {
        if (currentLang == LocalizedContentManager.LanguageCode.ja)
            return "15-30 Japanese characters each";

        if (currentLang == LocalizedContentManager.LanguageCode.ko)
            return "15-30 Korean syllables each";

        if (currentLang == LocalizedContentManager.LanguageCode.en)
            return "8-15 words each";

        return $"8-15 words each in {targetLangName}";
    }
}