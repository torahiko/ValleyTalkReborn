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
        string systemPrompt = BuildSystemPrompt(isZh);
        string userPrompt = BuildUserPrompt(npc, bio, isZh);

        if (string.IsNullOrWhiteSpace(userPrompt))
            return null;

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
    /// 构建 User Prompt - 把"场景数据"变成"此刻正在发生的事"
    /// </summary>
    private string BuildUserPrompt(NPC npc, BioData bio, bool isZh)
    {
        var sb = new StringBuilder();

        // ── 1. 角色人设（保持不变）──
        string rawPrompt = bio.AmbientBarkPrompt.Trim();
        if (isZh)
            rawPrompt = NpcNameLocalizer.LocalizeNamesInText(rawPrompt);

        rawPrompt = EnrichWithDynamicState(npc, rawPrompt, isZh);

        sb.AppendLine(isZh ? "### [你是谁]" : "### [WHO YOU ARE]");
        sb.AppendLine(rawPrompt);

        string rel = GetRelationshipLabel(npc, isZh);
        if (!string.IsNullOrEmpty(rel))
        {
            sb.AppendLine();
            sb.AppendLine(rel);
        }
        sb.AppendLine();

        // ── 2. 此刻正在发生的事（重写：去掉"清单感"）──
        string situation = BuildCurrentSituation(npc, isZh);
        if (!string.IsNullOrEmpty(situation))
        {
            sb.AppendLine(isZh ? "### [此刻的状况]" : "### [RIGHT NOW]");
            sb.AppendLine(situation);
            sb.AppendLine();
        }

        // ── 3. 记忆路由（三档时间判断）──
        string memoryContext = BuildMemoryContext(npc, isZh);
        if (!string.IsNullOrEmpty(memoryContext))
        {
            sb.AppendLine(memoryContext);
            sb.AppendLine();
        }

        // ── 4. 生成指令（彻底重写：去掉所有"怎么生成"的元层指导）──
        sb.AppendLine(BuildThinkingPrompt(bio, isZh));

        return sb.ToString();
    }

    /// <summary>
    /// 构建"此刻的状况" - 把环境/观察/场景/人物融合成"你正在经历什么"
    /// </summary>
    private static string BuildCurrentSituation(NPC npc, bool isZh)
    {
        var sb = new StringBuilder();
        var loc = npc?.currentLocation;
        if (loc == null) return null;

        bool isIndoor = !loc.IsOutdoors
                        || loc is StardewValley.Locations.FarmHouse
                        || loc is StardewValley.Locations.IslandFarmHouse;

        string locName = EnvironmentScanner.GetLocationFriendlyName(loc.Name);
        string weather = GetWeatherDescription(isZh);
        string timeDesc = GetTimeOfDayDescription(isZh);

        // ★ 核心改动：不再分段列举，而是描述"你正在做什么/处于什么状态"
        if (isZh)
        {
            sb.Append($"你在 {locName}");

            if (isIndoor)
                sb.Append("（室内）");
            else
                sb.Append($"（室外，{weather}）");

            sb.Append($"，{timeDesc}。");

            // 特殊状态优先（约会/跟随优先级最高）
            if (DialogueUtilities.IsOnDate(npc))
            {
                var dateLocation = DateManager.Instance?.ActiveDateLocation ?? "";
                string displayName = dateLocation;
                if (DateManager.LocationDisplayNames != null
                    && DateManager.LocationDisplayNames.TryGetValue(dateLocation, out var dn))
                    displayName = dn;

                sb.Append($" 你和玩家在【{displayName}】约会。");
            }
            else if (DialogueUtilities.IsFollowingSafe(npc))
            {
                sb.Append(" 你陪着玩家到处走。");
            }
            else if (CompanionScheduleManager.Instance?.IsStayHomeActive(npc.Name) == true)
            {
                string poiCtx = CompanionScheduleManager.Instance?.GetActivePoiContext(npc.Name);
                if (!string.IsNullOrEmpty(poiCtx))
                    sb.Append($" {NpcNameLocalizer.LocalizeNamesInText(poiCtx)}");
                else
                    sb.Append(" 今天待在农场。");
            }
        }
        else
        {
            sb.Append($"You're in {locName}");

            if (isIndoor)
                sb.Append(" (indoors)");
            else
                sb.Append($" (outdoors, {weather})");

            sb.Append($", {timeDesc}.");

            if (DialogueUtilities.IsOnDate(npc))
            {
                var dateLocation = DateManager.Instance?.ActiveDateLocation ?? "";
                string displayName = dateLocation;
                if (DateManager.LocationDisplayNames != null
                    && DateManager.LocationDisplayNames.TryGetValue(dateLocation, out var dn))
                    displayName = dn;

                sb.Append($" You're on a date with the player at {displayName}.");
            }
            else if (DialogueUtilities.IsFollowingSafe(npc))
            {
                sb.Append(" You're walking around with the player.");
            }
            else if (CompanionScheduleManager.Instance?.IsStayHomeActive(npc.Name) == true)
            {
                string poiCtx = CompanionScheduleManager.Instance?.GetActivePoiContext(npc.Name);
                if (!string.IsNullOrEmpty(poiCtx))
                    sb.Append($" {poiCtx}");
                else
                    sb.Append(" Spending the day on the farm.");
            }
        }

        sb.AppendLine();
        sb.AppendLine();

        // 在场人物（改成"谁在附近"而不是"人物列表"）
        var nearbyNpcs = GetNearbyNpcNames(npc);
        if (nearbyNpcs.Count > 0)
        {
            sb.Append(isZh ? "附近有：" : "Nearby: ");
            var nearbyLabels = new List<string>();
            foreach (var nearbyName in nearbyNpcs)
            {
                string label = GetNearbyRelationshipLabel(nearbyName, isZh);
                nearbyLabels.Add(label);
            }
            sb.AppendLine(string.Join(isZh ? "、" : ", ", nearbyLabels));
            sb.AppendLine();
        }

        // 即时观察 + 场景细节（融合：只保留"可能进入注意力的东西"）
        var perceptions = PerceptionManager.Instance?.GetFilteredBucketFor(npc.Name, 2);
        bool hasPerceptions = perceptions != null && perceptions.Count > 0;

        if (hasPerceptions || isIndoor)
        {
            sb.AppendLine(isZh ? "周围有些东西可能会进入你的注意力：" : "Things around that might catch your attention:");

            if (hasPerceptions)
            {
                var consumablePerceptions = new List<PerceptionEntry>();
                foreach (var p in perceptions)
                {
                    if (!string.IsNullOrWhiteSpace(p.Template))
                    {
                        string template = isZh ? NpcNameLocalizer.LocalizeNamesInText(p.Template) : p.Template;
                        sb.AppendLine($"- {template}");

                        // 随身普通物品进入 Prompt 后标记单人单日审美疲劳
                        if (p.Key == "PlayerActiveItem" && !string.IsNullOrEmpty(p.ItemId))
                        {
                            PerceptionManager.Instance?.MarkItemNoticedToday(npc.Name, p.ItemId);
                        }

                        // 收集需要阅后即焚的条目（动作、装束、信物、生理/Buff 状态）
                        if (PerceptionInjector.ShouldConsumeAfterInjection(p.Key))
                        {
                            consumablePerceptions.Add(p);
                        }
                    }
                }

                // 核心消费：注入 Bark Prompt 后立即消费，杜绝后续 Bark 循环复读同一状态
                if (consumablePerceptions.Count > 0)
                {
                    PerceptionManager.Instance?.ConsumePerceptions(npc.Name, consumablePerceptions);
                }
            }

            // 只在室内/特定场景补充环境细节（室外大场景不列物品）
            if (isIndoor)
            {
                string sceneBlock = SceneContextBuilder.BuildSceneBlock(npc, radiusTiles: 4, maxItems: 3);
                if (!string.IsNullOrWhiteSpace(sceneBlock))
                {
                    var lines = sceneBlock.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                    foreach (var line in lines.Skip(1)) // 跳过标题行
                    {
                        if (!string.IsNullOrWhiteSpace(line))
                            sb.AppendLine(line);
                    }
                }
            }
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// 构建"思考提示" - 不再是"生成任务"，而是"开始想吧"
    /// </summary>
    private static string BuildThinkingPrompt(BioData bio, bool isZh)
    {
        var sb = new StringBuilder();
        sb.AppendLine(isZh ? "### [开始想]" : "### [START THINKING]");

        // 提供一个可选的"注意力入口"（不是话题，是感知角度）
        var lenses = GetRandomThinkingLens(bio, 1, isZh);
        if (lenses.Count > 0)
        {
            sb.AppendLine(isZh
                ? $"你的注意力可能先落在：{lenses[0]}（也可以从别的地方开始）"
                : $"Your attention might land on: {lenses[0]} (or start elsewhere)");
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

    /// <summary>
    /// 构建记忆上下文（三档时间路由）- 保持原逻辑，但改写提示语言
    /// </summary>
    private string BuildMemoryContext(NPC npc, bool isZh)
    {
        if (!_stateStore.TryGet(npc.Name, out var state))
            return null;

        List<string> lastTail;
        List<string> recent;
        DateTime? lastEndedAt;
        int lastGameTime;
        int lastSaveDayNumber;

        lock (state)
        {
            if (state.LastThreadTail.Count == 0 || !state.LastThreadEndedAt.HasValue)
                return null;

            lastTail = state.LastThreadTail.ToList();
            recent = state.RecentBarks.ToList();
            lastEndedAt = state.LastThreadEndedAt;
            lastGameTime = state.LastThreadGameTimeOfDay;
            lastSaveDayNumber = state.LastThreadSaveDayNumber;
        }

        int currentDay = Game1.Date.TotalDays;

        // 跨天直接走长间隔（避免读档/时间突变的误判）
        if (lastSaveDayNumber != currentDay)
        {
            return BuildLongIntervalContext(lastTail, isZh);
        }

        double minutesSince = (DateTime.UtcNow - lastEndedAt.Value).TotalMinutes;
        bool sameTimeWindow = Game1.timeOfDay == lastGameTime
                              || (lastGameTime > 0 && Math.Abs(Game1.timeOfDay - lastGameTime) <= 20);

        string tailBlock = isZh
            ? string.Join("，", lastTail.Select(l => $"「{l}」"))
            : string.Join(" ", lastTail.Select(l => $"\"{l}\""));

        // ── 短间隔：≤3 分钟且同时段 → 延续上一段思绪 ──
        if (minutesSince <= 3 && sameTimeWindow)
        {
            var sb = new StringBuilder();
            sb.AppendLine(isZh ? "### [刚才想到哪儿了]" : "### [WHERE YOU LEFT OFF]");
            sb.AppendLine(isZh
                ? $"你刚才最后想的是：{tailBlock}。才过了一会儿，接着往下想——可以继续那个，可以补一句，可以被新东西带跑，也可以回过神来想别的。"
                : $"You were just thinking: {tailBlock}. Only a moment ago. Pick up from there — continue it, add one more beat, get pulled onto something new, or snap back to something else.");
            return sb.ToString();
        }

        // ── 中间隔：≤3 小时且同时段 → 仅作新鲜度参考 ──
        if (minutesSince <= 180 && sameTimeWindow)
        {
            var sb = new StringBuilder();
            sb.AppendLine(isZh ? "### [之前说过的]" : "### [SAID EARLIER]");
            sb.AppendLine(isZh
                ? "过去这段时间你说过这些（别机械重复，这次想新的）："
                : "You said these earlier (avoid mechanical repetition, think fresh this time):");
            foreach (var line in recent.TakeLast(3))
                sb.AppendLine($"- 「{line}」");
            return sb.ToString();
        }

        // ── 长间隔：> 3 小时或不同时段 → 小概率闪回 ──
        return BuildLongIntervalContext(lastTail, isZh);
    }

    /// <summary>
    /// 构建长间隔记忆上下文（15% 概率轻触历史）
    /// </summary>
    private static string BuildLongIntervalContext(List<string> lastTail, bool isZh)
    {
        if (_rng.NextDouble() >= 0.15)
            return null; // 85% 完全不提历史

        string tailBlock = isZh
            ? string.Join("，", lastTail.Select(l => $"「{l}」"))
            : string.Join(" ", lastTail.Select(l => $"\"{l}\""));

        var sb = new StringBuilder();
        sb.AppendLine(isZh ? "### [更早之前]" : "### [EARLIER MEMORY]");
        sb.AppendLine(isZh
            ? $"这是新的一段时间，主要想当下的事。但如果自然的话，可以有一条轻轻带到更早想过的：{tailBlock}——像突然想起后续怎么样了，一句带过就行，也完全可以不提。"
            : $"This is a fresh stretch of time; mostly think about right now. But if it fits naturally, one line could lightly touch on something thought about earlier: {tailBlock} — like suddenly remembering how it turned out, brief touch, totally fine to skip.");
        return sb.ToString();
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

    private static string GetRelationshipLabel(NPC npc, bool isZh)
    {
        if (npc == null) return null;

        string dn = isZh ? NpcNameLocalizer.GetZhName(npc.Name) : (npc.displayName ?? npc.Name);

        // 优先检查 Poly 关系
        if (PolyamorySweetLoveBridge.IsOfficialSpouse(npc))
        {
            var allSpouses = GetAllOfficialSpouses();
            bool isPolyMarriage = allSpouses.Count > 1;

            return isPolyMarriage
                ? (isZh
                    ? $"你和玩家是伴侣关系（TA 有 {allSpouses.Count} 位伴侣）。这是你们共同的家。"
                    : $"You're one of the player's partners ({allSpouses.Count} total). This is your shared home.")
                : (isZh
                    ? "你和玩家结婚了，这是你们的家。"
                    : "You're married to the player. This is your shared home.");
        }

        if (PolyamorySweetLoveBridge.IsUnofficialSpouse(npc))
        {
            return isZh
                ? "你和玩家是恋人关系。"
                : "You're romantically dating the player.";
        }

        var player = Game1.player;
        if (player?.friendshipData == null) return null;
        if (!player.friendshipData.TryGetValue(npc.Name, out var fs) || fs == null)
            return null;

        if (fs.IsMarried())
        {
            var allSpouses = GetAllOfficialSpouses();
            bool isPolyMarriage = allSpouses.Count > 1;

            return isPolyMarriage
                ? (isZh ? "你和玩家是伴侣关系。" : "You're one of the player's partners.")
                : (isZh ? "你和玩家结婚了。" : "You're married to the player.");
        }

        if (fs.IsDating())
        {
            return isZh
                ? "你和玩家在恋爱。"
                : "You're dating the player.";
        }

        int hearts = fs.Points / 250;
        if (hearts >= 6)
        {
            return isZh
                ? $"你和玩家是好朋友（{hearts} 心）。"
                : $"You and the player are close friends ({hearts} hearts).";
        }

        if (hearts >= 2)
        {
            return isZh
                ? $"你和玩家算是认识（{hearts} 心）。"
                : $"You're casually acquainted with the player ({hearts} hearts).";
        }

        return isZh
            ? $"你对玩家还不太熟（{hearts} 心）。"
            : $"You barely know the player ({hearts} hearts).";
    }

    private static string GetNearbyRelationshipLabel(string nearbyNpcName, bool isZh)
    {
        var player = Game1.player;

        if (player != null && string.Equals(nearbyNpcName, player.Name, StringComparison.OrdinalIgnoreCase))
        {
            string playerDisplayName = player.displayName ?? player.Name ?? "Player";
            return isZh ? $"{playerDisplayName}（玩家）" : $"{playerDisplayName} (player)";
        }

        var nearbyNpc = Game1.getCharacterFromName(nearbyNpcName);
        string dn = isZh
            ? NpcNameLocalizer.GetZhName(nearbyNpc?.Name ?? nearbyNpcName)
            : (nearbyNpc?.displayName ?? nearbyNpcName);

        if (nearbyNpc == null)
        {
            return dn;
        }

        // 特殊关系标注
        if (PolyamorySweetLoveBridge.IsOfficialSpouse(nearbyNpc))
        {
            return isZh ? $"{dn}（玩家的伴侣）" : $"{dn} (player's partner)";
        }

        if (PolyamorySweetLoveBridge.IsUnofficialSpouse(nearbyNpc))
        {
            return isZh ? $"{dn}（玩家的恋人）" : $"{dn} (player's partner)";
        }

        if (DialogueUtilities.IsFollowingSafe(nearbyNpc))
        {
            return isZh ? $"{dn}（正跟着玩家）" : $"{dn} (following player)";
        }

        return dn;
    }

    private static List<string> GetNearbyNpcNames(NPC centerNpc)
    {
        var result = new List<string>();
        if (centerNpc?.currentLocation == null) return result;

        bool isOutdoors = centerNpc.currentLocation?.IsOutdoors ?? true;
        int rangeSquared = isOutdoors ? 100 : 49;

        var player = Game1.player;
        if (player != null && player.currentLocation == centerNpc.currentLocation)
        {
            if (DialogueUtilities.IsInRangeSquared(centerNpc, player, rangeSquared))
                result.Add(player.Name);
        }

        foreach (var other in centerNpc.currentLocation.characters)
        {
            if (other == null || other == centerNpc || !other.IsVillager)
                continue;

            if (DialogueUtilities.IsInRangeSquared(centerNpc, other, rangeSquared))
                result.Add(other.Name);
        }

        return result;
    }

    private static List<string> GetAllOfficialSpouses()
    {
        var result = new List<string>();
        var player = Game1.player;
        if (player?.friendshipData == null) return result;

        foreach (var pair in player.friendshipData.Pairs)
        {
            if (pair.Value?.IsMarried() == true)
                result.Add(pair.Key);
        }

        return result;
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