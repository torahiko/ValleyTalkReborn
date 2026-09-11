using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using StardewModdingAPI;
using StardewValley;

namespace ValleytalkReborn;

/// <summary>
/// Bark Prompt 构建器 - 纯粹的模板生成器，彻底剥离启发式判断[cite: 4]。
/// 职责：将游戏状态转换为"此刻正在发生的事 + 角色如何感知"的认知场景[cite: 4]。
/// </summary>
internal sealed class BarkPromptBuilder
{
    private readonly AmbientBarkStateStore _stateStore;

    internal BarkPromptBuilder(AmbientBarkStateStore stateStore)
    {
        _stateStore = stateStore;
    }

    /// <summary>
    /// 构建 Bark 请求（必须在主线程调用）[cite: 4]
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

        // 焦点决策：在 PlayerStateScanner 扫描完成后立即执行，确保感知数据已入桶[cite: 4]
        if (!_stateStore.TryGet(npc.Name, out var barkState))
            barkState = _stateStore.GetOrCreate(npc.Name);

        var focusDecision = BarkFocusRouter.Decide(npc, barkState, bio, isZh);

        // ★ 核心消费闭环：若本轮选中了感知条目，立即标记已阅与审美疲劳[cite: 4]
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

        // 回写本轮焦点决策到 State，供下一轮疲劳阻尼使用（主线程，无需额外锁）[cite: 4]
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
    /// 构建 System Prompt - 认知基石：模拟"注意力自然流动"而非"生成台词"[cite: 4]
    /// </summary>
    private static string BuildSystemPrompt(bool isZh)
    {
        var currentLang = LocalizedContentManager.CurrentLanguageCode;
        bool isStandardEn = !isZh && currentLang == LocalizedContentManager.LanguageCode.en;
        string targetLangName = GetTargetLanguageName(currentLang);

        string lengthNotice = GetLengthNotice(currentLang, targetLangName, isZh);

        // Few-shot 示例保留"碎片 + 跳跃 + 搁置"的非线性质感[cite: 4]
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
            string langRequirementSection = isStandardEn ? "" : $@"
[LANGUAGE REQUIREMENT]
All thoughts and uttered fragments MUST be strictly written in {targetLangName}.
Do not output in English or any other language unless quoting an untranslated name.
";

            string catchphraseGuidance = isStandardEn
                ? "If the persona lists English catchphrases (Man / Dude / Heh), don't copy them literally — grasp their FEEL and express it naturally."
                : $"If the persona lists English catchphrases (Man / Dude / Heh), don't copy them literally — grasp their FEEL and express it naturally in {targetLangName}.";

            string langOutputConstraint = isStandardEn ? "" : $"\n- MUST be written strictly in {targetLangName}";

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
Not full sentences — the ""hmm..."" ""forget it"" ""maybe later..."" ""huh"" kind of half-spoken bits.

Check [SPOKEN HABITS] / [VOICE & ATTITUDE] in the persona. However they normally talk, these mutterings are the internal version of that style.
{catchphraseGuidance}

[OUTPUT FORMAT]
Plain JSON array: [""thought1"", ""thought2"", ...]
- 4-6 lines
- {lengthNotice}{langOutputConstraint}
- Must start with [ and end with ], no Markdown wrapping

Format example below (random content, unrelated to actual scene, don't copy topics):
{enExample}";
        }
    }

    /// <summary>
    /// 构建 User Prompt - 消费 Router 决策输出，仅组装，彻底杜绝二次摇号[cite: 4]
    /// </summary>
    private string BuildUserPrompt(NPC npc, BioData bio, bool isZh, BarkFocusDecision decision)
    {
        var sb = new StringBuilder();

        // ── 1. 角色人设 ──
        string rawPrompt = bio.AmbientBarkPrompt.Trim();
        if (isZh)
            rawPrompt = NpcNameLocalizer.LocalizeNamesInText(rawPrompt);

        rawPrompt = EnrichWithDynamicState(npc, rawPrompt, isZh);

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
        bool isFreeDrift = string.IsNullOrWhiteSpace(decision.InjectedContextLine);
        if (!isFreeDrift)
        {
            sb.AppendLine(isZh ? "### [注意力落在]" : "### [ATTENTION LANDS ON]");
            sb.AppendLine(decision.InjectedContextLine);
            sb.AppendLine();
        }

        // ── 4. 生成指令（无缝对齐是否有单一焦点）──
        sb.AppendLine(BuildThinkingPrompt(isZh, isFreeDrift));

        return sb.ToString();
    }

    /// <summary>
    /// 构建最弱的环境底色：仅包含地点、时段、跟随/约会持续状态、POI 动作现状（降级为一行）[cite: 4]。
    /// NearbyNPCs、Perceptions、SceneContext 全部移除，由 BarkFocusRouter 单点控制[cite: 4]。
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

            // 跟随/约会：降级为一句无戏剧性陈述（底色）[cite: 4]
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

        // 室内底色：常驻天气感知 + 周围物件（不参与 Router 竞争，仅作[此刻]背景）
        if (isIndoor)
        {
            sb.Append(BuildIndoorWeatherSentence(isZh));
            string objs = BuildNearbyObjectsSentence(npc, isZh);
            if (objs != null) sb.Append(objs);
        }

        return sb.ToString();
    }

    /// <summary>
    /// 构建室内常驻天气句（优先级：雪 > 雷 > 雨 > 风 > 晴）。
    /// 永远非 null 非空，以句号结尾；仅由 BuildAmbientScene 在室内时消费。
    /// </summary>
    private static string BuildIndoorWeatherSentence(bool isZh)
    {
        if (Game1.IsSnowingHere())      return isZh ? "屋外正下着雪。"   : "It's snowing outside.";
        if (Game1.IsLightningHere())    return isZh ? "屋外雷雨大作。"   : "A thunderstorm is raging outside.";
        if (Game1.IsRainingHere())      return isZh ? "屋外正下着雨。"   : "It's raining outside.";
        if (Game1.isDebrisWeather)      return isZh ? "屋外正刮着风。"   : "It's windy outside.";
        return isZh ? "屋外是个大晴天。" : "Clear skies outside.";
    }

    /// <summary>
    /// 构建室内周围物件句。无实体时返回 null；否则一句，以句号结尾。
    /// 玩家名通过 excludeNames 排除，绝不输出。
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
    /// 用于 zh 客户端守卫：POI 上下文若不含中文语料则跳过注入，避免英文括注污染中文 Bark。
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
        sb.AppendLine(isZh ? "### [开始想]" : "### [START THINKING]");

        if (isFreeDrift)
        {
            // 真正放空时，给模型正向漫游指引[cite: 4]
            sb.AppendLine(isZh
                ? "此时脑子里没有任何特定心事。注意力随处落脚——周围的细微动静、当下的身体感觉、脚下的路、或是单纯走神放空，把冒出的念头说出半句："
                : "No particular thoughts on your mind right now. Attention lands anywhere — ambient sounds, a physical sensation, the ground underfoot, or just drifting — mutter whatever surfaces:");
        }
        else
        {
            // 已有确定落脚点（心事/记忆/感官/邻里/群体氛围），顺承流动
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
            sb.AppendLine("- 输出纯 JSON 数组：[\"...\", \"...\", ...]");
        }
        else
        {
            var currentLang = LocalizedContentManager.CurrentLanguageCode;
            bool isStandardEn = currentLang == LocalizedContentManager.LanguageCode.en;
            string targetLangName = GetTargetLanguageName(currentLang);
            string lengthBullet = GetLengthBulletDescription(currentLang, targetLangName);

            sb.AppendLine($"- 4-6 lines, {lengthBullet}");
            sb.AppendLine("- Can flow together, jump around, or drop mid-thought");
            sb.AppendLine("- Use your own speaking style (see [WHO YOU ARE] above)");
            if (!isStandardEn)
            {
                sb.AppendLine($"- MUST be written in {targetLangName}");
            }
            sb.AppendLine("- Output plain JSON array: [\"...\", \"...\", ...]");
        }

        return sb.ToString();
    }

    // ══════════════════════════════════════════════════════════════
    // 辅助方法与多语言适配[cite: 4]
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
    /// 将关系信息降级为人设末尾的静态注脚（一行克制文本），而非独立段落[cite: 4]。
    /// 仅在有真实关系（恋爱/婚姻/≥6 心好友）时注入，普通认识不注入[cite: 4]。
    /// </summary>
    private static string GetRelationshipNote(NPC npc, bool isZh)
    {
        if (npc == null) return null;

        var parts = new List<string>();

        // 1. 婚姻/恋爱关系[cite: 4]
        bool isSpouse = PolyamorySweetLoveBridge.IsOfficialSpouse(npc)
                     || PolyamorySweetLoveBridge.IsUnofficialSpouse(npc);

        if (!isSpouse)
        {
            var player = Game1.player;
            if (player?.friendshipData != null
                && player.friendshipData.TryGetValue(npc.Name, out var fs) && fs != null)
            {
                if (fs.IsMarried() || fs.IsDating())
                {
                    isSpouse = true;
                }
                else
                {
                    int hearts = fs.Points / 250;
                    if (hearts >= 6)
                        parts.Add(isZh
                            ? $"你和玩家是好朋友（{hearts} 心）"
                            : $"Close friends with the player ({hearts} hearts)");
                }
            }
        }

        if (isSpouse)
            parts.Add(isZh ? "你和玩家是伴侣关系" : "In a romantic relationship with the player");

        // 2. 专属称谓注入（单点直取，不读记忆列表）[cite: 4]
        string callsign = MemoryManager.Instance.GetCustomCallsign(npc.Name);
        if (!string.IsNullOrWhiteSpace(callsign))
            parts.Add(isZh
                ? $"提及玩家时的称谓习惯：\"{callsign}\""
                : $"Callsign habit for the player: \"{callsign}\"");

        if (parts.Count == 0) return null;

        return isZh
            ? $"[身份与指代注脚：{string.Join("，", parts)}]"
            : $"[Identity & Callsign Note: {string.Join(". ", parts)}.]";
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

    /// <summary>
    /// 解析当前游戏环境对应的目标自然语言英文全称（供 LLM 认知）[cite: 4]
    /// </summary>
    private static string GetTargetLanguageName(LocalizedContentManager.LanguageCode code)
    {
        switch (code)
        {
            case LocalizedContentManager.LanguageCode.zh:
                return "Chinese";
            case LocalizedContentManager.LanguageCode.ja:
                return "Japanese";
            case LocalizedContentManager.LanguageCode.ru:
                return "Russian";
            case LocalizedContentManager.LanguageCode.pt:
                return "Portuguese";
            case LocalizedContentManager.LanguageCode.es:
                return "Spanish";
            case LocalizedContentManager.LanguageCode.de:
                return "German";
            case LocalizedContentManager.LanguageCode.th:
                return "Thai";
            case LocalizedContentManager.LanguageCode.fr:
                return "French";
            case LocalizedContentManager.LanguageCode.ko:
                return "Korean";
            case LocalizedContentManager.LanguageCode.it:
                return "Italian";
            case LocalizedContentManager.LanguageCode.tr:
                return "Turkish";
            case LocalizedContentManager.LanguageCode.hu:
                return "Hungarian";
            default:
                try
                {
                    if (code == LocalizedContentManager.LanguageCode.mod && LocalizedContentManager.CurrentModLanguage != null)
                    {
                        return LocalizedContentManager.CurrentModLanguage.LanguageCode
                               ?? LocalizedContentManager.CurrentModLanguage.Id
                               ?? "English";
                    }
                }
                catch
                {
                    // 降级容错[cite: 4]
                }
                return "English";
        }
    }

    /// <summary>
    /// 根据语言特性（表意字符 vs 拼音分词）获取 System Prompt 里的长度说明[cite: 4]
    /// </summary>
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

    /// <summary>
    /// 获取 Thinking Prompt 列表清单里的长度约束行[cite: 4]
    /// </summary>
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