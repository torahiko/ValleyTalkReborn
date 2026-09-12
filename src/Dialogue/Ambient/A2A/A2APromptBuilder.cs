using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using StardewValley;

namespace ValleytalkReborn;

/// <summary>
/// A2A Prompt 构建器：负责将 A2A 会话转换为纯数据 A2ARequest。
/// 只允许主线程调用。返回的 A2ARequest 不包含 NPC 对象引用。
/// </summary>
internal sealed class A2APromptBuilder
{
    // A2A gossip 去重：同一天内不重复使用同一条八卦
    private static readonly HashSet<string> _usedA2AGossipKeys = new(StringComparer.Ordinal);
    private static int _lastA2AGossipDay = -1;

    /// <summary>
    /// 玩家进入参与者 3 格（含）范围内时，A2A 提示里注入一行"农夫就在旁边"的注脚，
    /// 引导模型让对话自然留意/打趣玩家，而非假装玩家不存在。
    /// </summary>
    private const int A2APlayerCloseRangeSq = 9;

    /// <summary>
    /// 重置跨存档/跨天的静态八卦去重缓存。在换天、退标题、读档时调用，
    /// 防止旧存档的 "当日已聊过" 标记污染新存档。
    /// </summary>
    internal static void ResetGossipCache()
    {
        _usedA2AGossipKeys.Clear();
        _lastA2AGossipDay = -1;
    }

    internal A2APromptBuilder()
    {
    }

    /// <summary>
    /// 构建 A2A 请求。必须在主线程调用。
    /// </summary>
    /// <param name="previousTopicLine">上轮尾句，用于跨轮防复读注入。</param>
    /// <param name="interruptedTail">被打断的话尾；非空时注入续接行，并跳过防复读块。</param>
    internal DialogueModels.A2ARequest Build(
        DialogueModels.A2ASession session,
        string previousTopicLine = null,
        string interruptedTail = null)
    {
        if (session == null) return null;

        var participants = session.ResolveParticipants();

        if (participants == null || participants.Count < 2)
            return null;

        if (Game1.player == null)
            return null;

        bool isZh = IsChineseLanguage;

        // 角色名替换：中文模式下使用 NpcNameLocalizer 获得精确的本地化显示名字
        var displayNames = participants
            .Select(n => isZh ? NpcNameLocalizer.GetZhName(n.Name) : (n.displayName ?? n.Name))
            .ToList();

        // 收集 Name、displayName 以及中文名，确保 SceneContextBuilder 排除自身
        // 同时排除参与者自身与玩家的所有名字/称谓，防止玩家泄漏进场景物体列表
        var playerNames = new List<string>();
        if (Game1.player != null)
        {
            playerNames.Add(Game1.player.Name);
            playerNames.Add(Game1.player.displayName);
            playerNames.Add("Farmer");
            if (isZh) { playerNames.Add("农夫"); }
        }

        var excludeNames = participants
            .SelectMany(n => isZh
                ? new[] { n.Name, n.displayName, NpcNameLocalizer.GetZhName(n.Name) }
                : new[] { n.Name, n.displayName })
            .Concat(playerNames)
            .Where(s => !string.IsNullOrEmpty(s))
            .Distinct()
            .ToList();

        var personaLines = new List<string>();

        foreach (var npc in participants)
        {
            var ch = DialogueBuilder.Instance?.GetCharacter(npc);
            string rawPersona = ch?.Bio?.AmbientBarkPrompt?.Trim();

            if (string.IsNullOrEmpty(rawPersona)) continue;

            // 过滤掉单人内心独白的观察视角，只保留语气口吻，避免机械代入观察项
            string cleanedVoice = ExtractVoiceOnly(rawPersona);

            // 角色名替换：将口吻设定中包含的 NPC 英文名批量替换为本地化中文
            if (isZh)
            {
                cleanedVoice = NpcNameLocalizer.LocalizeNamesInText(cleanedVoice);
            }

            string persona = EnrichBarkPromptWithState(npc, cleanedVoice, isZh);
            string dn = isZh ? NpcNameLocalizer.GetZhName(npc.Name) : (npc.displayName ?? npc.Name);

            personaLines.Add(isZh
                ? $"{dn} 的说话习惯与口吻：{persona}"
                : $"{dn}'s speaking style: {persona}");
        }

        string sceneBlock = SceneContextBuilder.BuildSceneBlock(
            participants[0],
            radiusTiles: 5,
            maxItems: 2,
            excludeNames: excludeNames.ToArray());

        string gossip = TryGetRecentGossip(participants);
        if (!string.IsNullOrWhiteSpace(gossip) && isZh)
            gossip = NpcNameLocalizer.LocalizeNamesInText(gossip);
        // gossip 为空时整行不输出，避免空标签污染模型注意力

        string lengthDesc = isZh ? "单句口语（10~25字）" : "snappy spoken lines (10-25 words)";

        string lateNightBlock = "";
        if (Game1.timeOfDay >= 2200)
        {
            lateNightBlock = isZh
                ? "- 当前时间：深夜/酒吧打烊前。"
                : "- Current Time: Late night / near closing time.";
        }

        string allNames = string.Join(isZh ? "、" : ", ", displayNames);

        // ── 1. System Prompt 构建 ──
        var sysSb = new StringBuilder();
        sysSb.AppendLine(BuildA2ASystemPrompt(isZh, allNames));

        foreach (var pl in personaLines)
            sysSb.AppendLine(pl);

        string interRel = GetInterNpcRelationships(participants, isZh);
        if (isZh && !string.IsNullOrEmpty(interRel))
            interRel = NpcNameLocalizer.LocalizeNamesInText(interRel);

        // 多配偶同住事实：仅当全员均为配偶时注入，与 interRel 是否存在完全解耦
        string cohabitLine = null;
        if (participants.All(IsSpouse))
        {
            bool inFarmhouse = participants[0]?.currentLocation is StardewValley.Locations.FarmHouse
                            || participants[0]?.currentLocation is StardewValley.Locations.IslandFarmHouse
                            || (participants[0]?.currentLocation?.Name ?? "").Contains("Cabin", StringComparison.OrdinalIgnoreCase);

            cohabitLine = isZh
                ? (inFarmhouse
                    ? "- 双方均已与农夫结婚，共同居住在此处农舍。"
                    : "- 双方均已与农夫结婚，共同生活在农场。")
                : (inFarmhouse
                    ? "- Both are married to the farmer and share this farmhouse."
                    : "- Both are married to the farmer and share life on the farm.");
        }

        // 只要 interRel 或 cohabitLine 任一有内容，就输出该段落
        if (!string.IsNullOrEmpty(interRel) || cohabitLine != null)
        {
            sysSb.AppendLine(isZh ? "## [参与者之间的关系]" : "## [Relationships Among Participants]");
            if (!string.IsNullOrEmpty(interRel))
                sysSb.AppendLine(interRel);
            if (cohabitLine != null)
                sysSb.AppendLine(cohabitLine);
            sysSb.AppendLine();
        }

        // ── 2. User Prompt 构建 ──
        // 话题钩子决选：八卦催化剂 vs 地点基线，加权单槽。
        // Gossip 命中时其文案已含 gossip 文本，故压制独立 gossip 行防重复。
        var topicDecision = A2ATopicRouter.Decide(participants, gossip, isZh);
        string topicHook = topicDecision.InjectedLine;

        var userSb = new StringBuilder();
        userSb.AppendLine(sceneBlock);
        if (!string.IsNullOrEmpty(lateNightBlock))
            userSb.AppendLine(lateNightBlock);

        string actionBody = BuildEmbodiedActionLines(participants, isZh);
        if (actionBody != null)
            userSb.AppendLine(isZh ? $"【当下举动】{actionBody}。" : $"[CURRENT ACTIONS] {actionBody}.");

        userSb.AppendLine();
        // 仅当话题钩子未携带 gossip 文本时，才输出独立的 gossip 行（消除硬编码英文）
        if (!string.IsNullOrWhiteSpace(gossip) && topicDecision.Type != A2ACatalystType.Gossip)
            userSb.AppendLine(isZh ? $"最近的小镇传闻：{gossip}" : $"Recent town gossip: {gossip}");
        userSb.AppendLine();

        // 玩家近身注脚：农夫站在参与者身旁时，提示模型自然留意/打趣玩家
        string proximityNote = BuildPlayerProximityNote(participants, isZh);
        if (proximityNote != null)
        {
            userSb.AppendLine(proximityNote);
            userSb.AppendLine();
        }

        // 被打断续接 优先于 跨轮防复读：两块绝不并存。
        // interruptedTail 非空时注入续接行并跳过防复读；否则走既有防复读路径。
        if (!string.IsNullOrWhiteSpace(interruptedTail))
        {
            string quotedTail = isZh ? "“" + interruptedTail + "”" : "\"" + interruptedTail + "\"";
            userSb.AppendLine(isZh
                ? $"（你们刚才聊到{quotedTail}就被打断了，现在接着刚才的话头自然续上，或随口吐槽刚才被打断这件事。）"
                : $"(Your conversation was cut off right after: {quotedTail}. Pick it back up naturally, or bring up having been interrupted.)");
            userSb.AppendLine();
        }
        else if (!string.IsNullOrWhiteSpace(previousTopicLine))
        {
            string quoted = isZh
                ? "“" + previousTopicLine + "”"
                : "\"" + previousTopicLine + "\"";
            userSb.AppendLine(isZh
                ? $"（你们不久前刚聊过，刚才最后提到的是：{quoted}。本次交谈请自然开启新话题，避免重复上述内容。）"
                : $"(You two recently had a conversation; the last thing mentioned was: {quoted}. Start a fresh topic this time and avoid repeating it.)");
            userSb.AppendLine();
        }

        userSb.Append(BuildA2AConversationGuidance(topicHook, lengthDesc, isZh));
        userSb.AppendLine();

        userSb.Append(BuildA2AExampleBlock(displayNames, isZh));
        userSb.AppendLine();

        userSb.Append(BuildA2AFinalRules(lengthDesc, isZh));

        return new DialogueModels.A2ARequest
        {
            SystemPrompt = sysSb.ToString().TrimEnd(),
            UserPrompt = userSb.ToString(),
            IsChinese = isZh,
            NamesLog = string.Join(" & ", session.ParticipantNames)
        };
    }

    /// <summary>
    /// 构建"当下举动"提示正文：将每位参与者配偶日程的即时 POI 动作收拢为一句列表。
    /// </summary>
    private static string BuildEmbodiedActionLines(List<NPC> participants, bool isZh)
    {
        if (participants == null || participants.Count == 0)
            return null;

        var fragments = new List<string>();

        foreach (var npc in participants)
        {
            if (npc == null)
                continue;

            string poi = CompanionScheduleManager.Instance?.GetActivePoiContext(npc.Name);
            if (string.IsNullOrWhiteSpace(poi))
                continue;

            string first = poi.Split(new[] { '。', '.' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => s.Trim())
                .FirstOrDefault(s => s.Length > 0);

            if (first == null)
                continue;

            // 剥壳：剥除 bracket 与语言前缀
            if (first.StartsWith("[") && first.EndsWith("]"))
                first = first.Substring(1, first.Length - 2).Trim();
            if (first.StartsWith("你现在："))
                first = first.Substring("你现在：".Length).Trim();
            else if (first.StartsWith("Right now you are:"))
                first = first.Substring("Right now you are:".Length).Trim();

            if (isZh)
            {
                if (!ContainsCjkCharacter(first))
                {
                    ModEntry.SMonitor?.Log(
                        $"[A2A] POI 动作为非中文文本，跳过注入: {first}",
                        StardewModdingAPI.LogLevel.Trace);
                    continue;
                }
                first = NpcNameLocalizer.LocalizeNamesInText(first);
            }

            string dn = isZh ? NpcNameLocalizer.GetZhName(npc.Name) : (npc.displayName ?? npc.Name);

            fragments.Add(isZh
                ? $"{dn} 此刻：{first}"
                : $"{dn} right now: {first}");
        }

        if (fragments.Count == 0)
            return null;

        return string.Join(isZh ? "；" : "; ", fragments);
    }

    /// <summary>
    /// 当玩家站在参与者 3 格（含）范围内时，返回一行"农夫就在旁边"的注脚。
    /// </summary>
    private static string BuildPlayerProximityNote(List<NPC> participants, bool isZh)
    {
        if (Game1.player == null)
            return null;

        if (participants == null || participants.Count == 0)
            return null;

        if (!DialogueUtilities.IsInRangeSquared(participants[0], (Farmer)Game1.player, A2APlayerCloseRangeSq))
            return null;

        bool spouseAny = participants.Any(IsSpouse);

        if (isZh)
        {
            return spouseAny
                ? "（注：农夫就站在你们身边。你们中有人与农夫关系亲密，可自然对农夫打趣一句或有所反应。）"
                : "（注：农夫此刻就站在你们旁边看着你们交谈，可适度自然留意或打趣，无需刻意回避。）";
        }
        else
        {
            return spouseAny
                ? "(Note: The farmer is right beside you. Someone close to the farmer may naturally tease or react to them.)"
                : "(Note: The farmer is standing right beside you, watching. Feel free to acknowledge them naturally.)";
        }
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

    private static string BuildA2ASystemPrompt(bool isZh, string allNames)
    {
        var sb = new StringBuilder();
        bool needLangConstraint = ShouldInjectLanguageConstraint(out string targetLangZh, out string targetLangEn);

        if (isZh)
        {
            sb.AppendLine("你是《星露谷物语》的顶尖编剧。");
            sb.AppendLine($"请为 {allNames} 编写一段生动、自然、充满生活烟火气的日常交流对话。");
            sb.AppendLine();
            sb.AppendLine("【核心要求】");
            sb.AppendLine("1. 自然呼吸：对话围绕一个大致的话题或氛围展开，允许岔开、中断或松散承接。在真实的闲聊中，有人说了句话，另一个人可以直接回应、顺着联想到另一件事、短暂沉默后接一句看似无关的话，或由某一方主动转开话头。营造几个人在当前空间共处的生活感，松散的流动比刻板的排队作答更显真实。");
            sb.AppendLine("2. 纯口语台词：输出完全由角色脱口而出的自然话语构成，依靠标点和语气传达情绪。展现真实的口语节奏——半句收尾、结论在前、语序倒装以及自然的口语语气词。");
            sb.AppendLine("3. 鲜活口吻：依据各自的人设，依据各自人设的口吻与节奏进行表达。");
            sb.AppendLine("4. 数组起始：首个字符必须是 [，末尾字符必须是 ]。");
            sb.AppendLine("5. 语气与专名本地化：以下人设描述中的口语习惯与感叹词仅作为角色性格参考。输出时完全转化为地道目标语言的日常口语虚词与生动句式；提及的角色名字、地点与建筑严格采用上下文中已给出的本地化名称。");

            if (needLangConstraint)
            {
                sb.AppendLine($"6. 语言一致性：所有输出请严格使用“{targetLangZh}”。");
            }
        }
        else
        {
            sb.AppendLine("You are an expert dialogue writer for Stardew Valley.");
            sb.AppendLine($"Write a lively, natural, slice-of-life conversation between {allNames}.");
            sb.AppendLine();
            sb.AppendLine("[CORE INSTRUCTIONS]");
            sb.AppendLine("1. Natural Breathing: The conversation orbits a loose topic or shared atmosphere, allowing it to drift, pause, or connect loosely. Participants may respond directly, branch into a related thought, pause briefly, or steer the focus elsewhere. Ground the exchange in the feeling of people sharing physical space rather than rigid tennis rallies.");
            sb.AppendLine("2. Pure Spoken Dialogue: Output consists entirely of natural spoken lines, relying purely on cadence and punctuation. Capture organic speech patterns: sentence fragments, inverted structures, and casual spoken endings.");
            sb.AppendLine("3. Authentic Hangout Vibe: Ground the interaction in relaxed familiarity, letting unique personalities shape cadence and flow.");
            sb.AppendLine("4. Bracket Framing: Your output starts precisely with [ and concludes precisely with ].");

            if (needLangConstraint)
            {
                sb.AppendLine($"5. Language Requirement: All output must strictly use {targetLangEn}.");
                sb.AppendLine($"6. Cultural & Name Localization: Any catchphrases, fillers, or speech mannerisms in the persona descriptions are character cues. Naturally transmute them into authentic, native spoken phrasing in {targetLangEn}. Faithfully use the localized character and place names provided in the context.");
            }
        }

        return sb.ToString();
    }

    private static string BuildA2AConversationGuidance(string topicHook, string lengthDesc, bool isZh)
    {
        var sb = new StringBuilder();

        if (isZh)
        {
            sb.AppendLine("【对话推进】");
            sb.AppendLine($"背景氛围：{topicHook}");
            sb.AppendLine();
            sb.AppendLine("执行指引：");
            sb.AppendLine("- 对话始于此刻真正进入注意力的现实细节——身体感受、天气、刚想到的事、眼前的景致、或对身旁之人的随口一瞥。");
            sb.AppendLine("- 后续各句顺着注意力自然流动：可以展开深入、转向新念头、或是被动静打断。交谈节奏保持松散自由，单人可连说两句，也可作短促的反应性应答。");
            sb.AppendLine("- 重点在于展现同处一室的生活质感。台词可以是随口念叨、吐槽、或半途而废的念头，在自然停顿处收尾即可。");
            sb.AppendLine("- 场景物体与环境仅作为视线自然扫过或肢体实际接触时的背景衬托。");
            sb.AppendLine();
            sb.AppendLine("任务：参与者均自然开口，编写 4~6 条连贯流动的日常对白。");
            sb.AppendLine();
            sb.AppendLine("输出格式：包含 4~6 个对象的 JSON 数组。首字符为 [，末字符为 ]。");
            sb.AppendLine($"每个对象：{{\"speaker\": \"名字\", \"line\": \"台词文本（{lengthDesc}）\"}}");
        }
        else
        {
            sb.AppendLine("[DIALOGUE FLOW]");
            sb.AppendLine($"Atmosphere: {topicHook}");
            sb.AppendLine();
            sb.AppendLine("Execution:");
            sb.AppendLine("- Begin with an immediate sensory focus: physical sensations, weather, an idle thought, an object in view, or a glance at the person nearby.");
            sb.AppendLine("- Subsequent lines follow the natural drift of attention: deepening, branching, or pausing. Maintain loose pacing where characters may speak consecutively or offer brief reactive mutters.");
            sb.AppendLine("- Emphasize everyday coexistence. Lines can be idle observations, small complaints, or half-finished remarks that conclude at a believable pause.");
            sb.AppendLine("- Surroundings act strictly as sensory backdrop when naturally noticed or physically engaged.");
            sb.AppendLine();
            sb.AppendLine("Task: Ensure all participants contribute naturally, generating 4–6 organic conversational lines.");
            sb.AppendLine();
            sb.AppendLine("Output format: JSON array of 4–6 objects. First character MUST be [, last character MUST be ].");
            sb.AppendLine($"Each object: {{\"speaker\": \"name\", \"line\": \"dialogue text ({lengthDesc})\"}}");
        }

        return sb.ToString();
    }

    private static string BuildA2AExampleBlock(List<string> displayNames, bool isZh)
    {
        var sb = new StringBuilder();

        if (isZh)
        {
            sb.AppendLine("互动参考示例（注重自然呼吸与松散承接）：");

            var example = JsonConvert.SerializeObject(new[]
            {
                new { speaker = displayNames[0], line = "嘶……这一大早的，屋里还是有点冷。" },
                new { speaker = displayNames[1], line = "刚才开门通风来着，炉子火刚生起来。" },
                new { speaker = displayNames[0], line = "窗台那边的霜结得挺厚啊。" },
                new { speaker = displayNames[0], line = "今天出门少不了得裹厚点。" },
                new { speaker = displayNames[1], line = "手套放在门后篮子里了，走的时候自己拿。" }
            });
            sb.AppendLine(example);
        }
        else
        {
            sb.AppendLine("Interaction Examples (focus on natural breathing and loose connections):");

            var example = JsonConvert.SerializeObject(new[]
            {
                new { speaker = displayNames[0], line = "Brr... still kind of brisk in here this morning." },
                new { speaker = displayNames[1], line = "Aired the place out earlier. Fire's just catching now." },
                new { speaker = displayNames[0], line = "Frost is pretty thick on the windowsill." },
                new { speaker = displayNames[0], line = "Definitely bundling up today." },
                new { speaker = displayNames[1], line = "Gloves are in the basket by the door, grab them before you head out." }
            });
            sb.AppendLine(example);
        }

        return sb.ToString();
    }

    private static string BuildA2AFinalRules(string lengthDesc, bool isZh)
    {
        var sb = new StringBuilder();
        bool needLangConstraint = ShouldInjectLanguageConstraint(out string targetLangZh, out string targetLangEn);

        sb.AppendLine(isZh ? "规则：" : "Rules:");
        sb.AppendLine(isZh
            ? "- 纯净输出：首字符必为 [，末字符必为 ]，其间仅包含标准 JSON 数组内容。"
            : "- Strict Format: Output exclusively the raw JSON array starting with [ and ending with ].");
        sb.AppendLine(isZh
            ? $"- 语言风格：自然生活口语，{lengthDesc}。"
            : $"- Style: Natural spoken dialogue, {lengthDesc}.");

        // 临近 Token 生成点的尾部门禁
        if (needLangConstraint)
        {
            sb.AppendLine(isZh
                ? $"- 语言规范：所有输出请严格使用“{targetLangZh}”，确保语气词、人名与地名自然地道统一。"
                : $"- Language: All output must strictly use {targetLangEn}, with all fillers, character names, and place names natively adapted.");
        }

        sb.AppendLine(isZh
            ? "- 发言节奏：参与者均有发言，按当下互动自然轮换或连说，总条数 4~6 条。"
            : "- Pacing: All participants must speak; flow organically without rigid turn-taking. Total 4–6 lines.");
        sb.AppendLine(isZh
            ? "- 交流推进：各人以自身的生活经验、反应或新视角接续话头，让思绪自然前行，保持真实流动的语言交替。"
            : "- Progression: Advance each line with the speaker's own perspective, emotional reaction, or lived experience, sustaining an organic exchange.");
        sb.AppendLine(isZh
            ? "- 真实相处：台词可以半句收住、中途改口、或没有明确结论，展现真实的日常生活瞬间。"
            : "- Coexistence: Lines may trail off, shift midway, or remain open-ended, reflecting genuine everyday life.");

        return sb.ToString();
    }

    /// <summary>
    /// 截取 AmbientBarkPrompt 中的语气特征，剔除容易导致自说自话的观察项
    /// </summary>
    private static string ExtractVoiceOnly(string rawPrompt)
    {
        if (string.IsNullOrWhiteSpace(rawPrompt)) 
            return string.Empty;

        int obsIndex = rawPrompt.IndexOf("[OBSERVATION LENSES]", StringComparison.OrdinalIgnoreCase);
        if (obsIndex > 0)
        {
            return rawPrompt.Substring(0, obsIndex)
                .Replace("[VOICE & ATTITUDE]", "")
                .Trim();
        }

        string customPrompt = rawPrompt.Trim();

        const int MaxSafeChars = 1000;
        if (customPrompt.Length > MaxSafeChars)
        {
            return customPrompt.Substring(0, MaxSafeChars) + "...";
        }

        return customPrompt;
    }

    /// <summary>
    /// 根据时间、地点与季节，提供氛围层面的引导，避免提供具体事件导致模型产生锚定效应。
    /// </summary>
    internal static string GenerateConversationTopic(List<NPC> participants, bool isChinese)
    {
        int time = Game1.timeOfDay;
        var loc = participants[0]?.currentLocation ?? Game1.player?.currentLocation;
        string locName = loc?.Name ?? "";
        bool isOutdoors = loc?.IsOutdoors ?? false;
        string season = Game1.currentSeason?.ToLowerInvariant() ?? "spring";

        string seasonMoodZh = season switch
        {
            "spring" => Game1.isRaining ? "春雨连绵的潮湿泥泞" : "初春微凉而透着新绿的空气",
            "summer" => Game1.isGreenRain 
                ? "笼罩小镇的诡异绿雨与疯长苔藓" 
                : (Game1.isRaining ? "沉闷潮湿的夏日暴雨" : "夏日耀眼的阳光与燥热微风"),
            "fall"   => Game1.isRaining ? "秋雨浸透落叶的萧瑟微寒" : "秋高气爽的丰收时节与微凉秋风",
            "winter" => "冬日凛冽的寒风与屋外积雪",
            _ => "当季特有的时节气息"
        };

        string seasonMoodEn = season switch
        {
            "spring" => Game1.isRaining ? "damp spring drizzle and wet soil" : "crisp, fresh air of early spring",
            "summer" => Game1.isGreenRain 
                ? "the eerie green rain and wild moss overgrowth" 
                : (Game1.isRaining ? "muggy, humid summer downpours" : "blazing sunlight and heavy summer warmth"),
            "fall"   => Game1.isRaining ? "chilly autumn drizzle soaking the leaves" : "brisk autumn breeze and harvest season energy",
            "winter" => "freezing wind and quiet snow outside",
            _ => "the current seasonal atmosphere"
        };

        bool isHome = loc is StardewValley.Locations.FarmHouse
                   || loc is StardewValley.Locations.IslandFarmHouse
                   || locName.Contains("Cabin", StringComparison.OrdinalIgnoreCase);

        if (isChinese)
        {
            if (isHome)
            {
                if (time < 1100)
                    return $"清晨的农舍室内，{seasonMoodZh}，炉火刚生起来。围绕屋里的温度、今天各自的日程或早晨的动静随口搭话。";
                if (time >= 2100)
                    return $"深夜的农舍，屋外{seasonMoodZh}。围绕今天的收尾、身体状态或明天打算随口搭话。";
                return $"农舍室内，{seasonMoodZh}。围绕当前的炉火温度、屋里的动静或手头各自的事情随口搭话。";
            }

            if (locName.Contains("Saloon", StringComparison.OrdinalIgnoreCase))
                return $"星之果实酒吧内，{seasonMoodZh}，酒吧背景杂音连绵。围绕今天的行程、手里的饮品或镇上的近况随口搭话。";

            if (locName.Contains("Club", StringComparison.OrdinalIgnoreCase) || locName.Contains("CommunityCenter", StringComparison.OrdinalIgnoreCase))
                return "社区中心宽敞的室内，脚步声回响。围绕这里的宽敞安静或各自的来意随口搭话。";

            if (locName.Contains("SeedShop", StringComparison.OrdinalIgnoreCase) || locName.Contains("GeneralStore", StringComparison.OrdinalIgnoreCase))
                return "皮埃尔杂货店内，货架与柜台之间。围绕各自要采买的东西或店里的陈设随口搭话。";

            if (locName.Contains("Hospital", StringComparison.OrdinalIgnoreCase) || locName.Contains("Clinic", StringComparison.OrdinalIgnoreCase))
                return "哈维诊所安静的候诊室，带着淡淡药草气味。围绕近来的身体状态或等候时间随口搭话。";

            if (locName.Contains("ArchaeologyHouse", StringComparison.OrdinalIgnoreCase) || locName.Contains("Library", StringComparison.OrdinalIgnoreCase))
                return "博物馆兼图书馆内，书架与展柜安静陈列。围绕某个展陈或各自手边的事情轻声搭话。";

            if (locName.Contains("ScienceHouse", StringComparison.OrdinalIgnoreCase) || locName.Contains("Carpenter", StringComparison.OrdinalIgnoreCase))
                return "罗宾的木匠工坊内，木材气味与加工动静交织。围绕房屋修整事宜或各自近况随口搭话。";

            if (locName.Contains("AnimalShop", StringComparison.OrdinalIgnoreCase) || locName.Contains("Ranch", StringComparison.OrdinalIgnoreCase))
                return "玛妮的牧场小屋内，传来牲畜动静与饲料气味。围绕牲畜状态或农场打理随口搭话。";

            if (locName.Contains("Blacksmith", StringComparison.OrdinalIgnoreCase))
                return "克林特的铁匠铺内，炉火正旺，铁砧敲击声阵阵。围绕工具或手头活计随口搭话。";

            if (isOutdoors)
            {
                if (locName.Contains("Beach", StringComparison.OrdinalIgnoreCase))
                    return $"海边沙滩，{seasonMoodZh}，浪声连绵。围绕眼前景色或各自的来意随口搭话。";

                if (locName.Contains("Forest", StringComparison.OrdinalIgnoreCase))
                    return $"煤矿森林树影间，{seasonMoodZh}，脚下是落叶与泥土小径。围绕林间动静或行程随口搭话。";

                if (locName.Contains("Mountain", StringComparison.OrdinalIgnoreCase))
                    return $"山道湖畔，{seasonMoodZh}，山风阵阵。围绕湖景、山路或各自的目的地随口搭话。";

                if (locName.Contains("Mine", StringComparison.OrdinalIgnoreCase))
                    return "矿洞入口，岩壁传来阵阵凉气与回响。围绕入洞准备或脚下安全随口搭话。";

                if (locName.Contains("Railroad", StringComparison.OrdinalIgnoreCase))
                    return $"铁轨延伸处，{seasonMoodZh}，穿堂风阵阵。围绕远处景色或各自的来意随口搭话。";

                return $"镇上街道，{seasonMoodZh}，路过偶遇。围绕今天的行程或路上所见随口搭话。";
            }

            return "室内偶遇，当前场所的氛围静默。围绕眼前动静或各自手头的事情随口搭话。";
        }
        else
        {
            if (isHome)
            {
                if (time < 1100)
                    return $"Morning in the farmhouse, {seasonMoodEn}, fire just getting started. Offhand remarks about the room temperature, the day's schedule, or the morning's stirrings.";
                if (time >= 2100)
                    return $"Late night in the farmhouse, {seasonMoodEn} outside. Offhand remarks about winding down, how the body feels, or tomorrow's plans.";
                return $"Inside the farmhouse, {seasonMoodEn}. Offhand remarks about the fire's warmth, household goings-on, or whatever each is occupied with.";
            }

            if (locName.Contains("Saloon", StringComparison.OrdinalIgnoreCase))
                return $"Inside the Stardrop Saloon, {seasonMoodEn}, background noise of the bar. Offhand remarks about the day's errands, drinks in hand, or recent town happenings.";

            if (locName.Contains("Club", StringComparison.OrdinalIgnoreCase) || locName.Contains("CommunityCenter", StringComparison.OrdinalIgnoreCase))
                return "Inside the spacious Community Center, footsteps echoing. Offhand remarks about the quiet openness or why they stopped by.";

            if (locName.Contains("SeedShop", StringComparison.OrdinalIgnoreCase) || locName.Contains("GeneralStore", StringComparison.OrdinalIgnoreCase))
                return "Inside Pierre's General Store, between shelves and counter. Offhand remarks about what each is shopping for or the shop's displays.";

            if (locName.Contains("Hospital", StringComparison.OrdinalIgnoreCase) || locName.Contains("Clinic", StringComparison.OrdinalIgnoreCase))
                return "Harvey's quiet waiting room with a faint herbal scent. Offhand remarks about recent health or the wait.";

            if (locName.Contains("ArchaeologyHouse", StringComparison.OrdinalIgnoreCase) || locName.Contains("Library", StringComparison.OrdinalIgnoreCase))
                return "Inside the library and museum, shelves and display cases quietly arranged. Soft remarks about an exhibit or whatever each has at hand.";

            if (locName.Contains("ScienceHouse", StringComparison.OrdinalIgnoreCase) || locName.Contains("Carpenter", StringComparison.OrdinalIgnoreCase))
                return "Robin's carpentry shop, wood scent and work sounds filling the space. Offhand remarks about house repairs or catching up.";

            if (locName.Contains("AnimalShop", StringComparison.OrdinalIgnoreCase) || locName.Contains("Ranch", StringComparison.OrdinalIgnoreCase))
                return "Marnie's ranch parlor, livestock sounds and feed smells drifting in. Offhand remarks about the animals or farm upkeep.";

            if (locName.Contains("Blacksmith", StringComparison.OrdinalIgnoreCase))
                return "Clint's blacksmith shop, forge blazing, hammer on anvil ringing. Offhand remarks about tools or current work.";

            if (isOutdoors)
            {
                if (locName.Contains("Beach", StringComparison.OrdinalIgnoreCase))
                    return $"Along the beach, {seasonMoodEn}, waves murmuring. Offhand remarks about the scenery or why each came down here.";

                if (locName.Contains("Forest", StringComparison.OrdinalIgnoreCase))
                    return $"Under the tree canopy of Cindersap Forest, {seasonMoodEn}, dirt paths underfoot. Offhand remarks about the woods or the day's errands.";

                if (locName.Contains("Mountain", StringComparison.OrdinalIgnoreCase))
                    return $"By the mountain lakeside, {seasonMoodEn}, mountain breezes. Offhand remarks about the lake view, trail, or where each is headed.";

                if (locName.Contains("Mine", StringComparison.OrdinalIgnoreCase))
                    return "At the mine entrance, cool drafts and echoes from the rock walls. Offhand remarks about gearing up or footing safety.";

                if (locName.Contains("Railroad", StringComparison.OrdinalIgnoreCase))
                    return $"Along the railroad stretch, {seasonMoodEn}, crosswinds blowing. Offhand remarks about the distant scenery or why each came this way.";

                return $"Crossing paths on a town street, {seasonMoodEn}. Offhand remarks about the day's plans or what each noticed along the way.";
            }

            return "Crossing paths indoors, the room quiet. Offhand remarks about what's going on around them or whatever each has at hand.";
        }
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

    private static bool IsSpouse(NPC npc)
    {
        if (npc == null) return false;
        if (PolyamorySweetLoveBridge.IsOfficialSpouse(npc)) return true;
        if (PolyamorySweetLoveBridge.IsUnofficialSpouse(npc)) return true;
        return Game1.player?.friendshipData?.TryGetValue(npc.Name, out var fs) == true
               && fs != null && fs.IsMarried();
    }

    private static string EnrichBarkPromptWithState(NPC npc, string basePrompt, bool isChinese)
    {
        if (npc == null || string.IsNullOrWhiteSpace(basePrompt))
            return basePrompt;

        var bio = DialogueBuilder.Instance?.GetCharacter(npc)?.Bio;
        string stateText = ProgressStateResolver.ResolveActiveState(npc, bio?.ProgressStates);

        if (string.IsNullOrWhiteSpace(stateText))
            return basePrompt;

        return $"{basePrompt} [CURRENT STATE: {stateText}]";
    }

    private static string GetRelationshipLabel(NPC npc, bool isChinese, bool forA2A)
    {
        if (npc == null) return null;

        string dn = isChinese ? NpcNameLocalizer.GetZhName(npc.Name) : (npc.displayName ?? npc.Name);

        if (PolyamorySweetLoveBridge.IsOfficialSpouse(npc))
        {
            return forA2A
                ? (isChinese
                    ? $"- {dn} 是玩家的配偶之一。"
                    : $"- {dn} is one of the player's spouses.")
                : (isChinese
                    ? $"- 关系：{dn} 是玩家的配偶。"
                    : $"- Relationship: {dn} is married to the player, sharing this home.");
        }

        if (PolyamorySweetLoveBridge.IsUnofficialSpouse(npc))
        {
            return forA2A
                ? (isChinese
                    ? $"- {dn} 与玩家是热恋情侣之一。"
                    : $"- {dn} is one of the player's romantic partners.")
                : (isChinese
                    ? $"- 关系：{dn} 与玩家是热恋中的情侣。"
                    : $"- Relationship: {dn} is in a sweet romantic relationship with the player.");
        }

        var player = Game1.player;

        if (player?.friendshipData == null) return null;

        if (!player.friendshipData.TryGetValue(npc.Name, out var fs) || fs == null)
            return null;

        if (fs.IsMarried())
        {
            return forA2A
                ? (isChinese
                    ? $"- {dn} 是玩家的配偶，共同生活在农场。"
                    : $"- {dn} is married to the player and shares the farm. Speak with warmth and domestic familiarity.")
                : (isChinese
                    ? $"- 关系：{dn} 是玩家的配偶。这里是你们共同的家。"
                    : $"- Relationship: {dn} is married to the player in their shared home. Maintain a cozy, deeply familiar tone.");
        }

        if (fs.IsDating())
        {
            return forA2A
                ? (isChinese
                    ? $"- {dn} 正在与玩家恋爱。"
                    : $"- {dn} is dating the player.")
                : (isChinese
                    ? $"- 关系：{dn} 正在与玩家恋爱中。"
                    : $"- Relationship: {dn} is dating the player.");
        }

        int hearts = fs.Points / 250;

        if (hearts >= 8)
        {
            return forA2A
                ? (isChinese
                    ? $"- {dn} 是玩家的挚友（{hearts} 颗心）。"
                    : $"- {dn} is a very close friend of the player ({hearts} hearts).")
                : (isChinese
                    ? $"- 关系：{dn} 与玩家是亲密挚友（{hearts} 颗心）。"
                    : $"- Relationship: {dn} and the player are close friends ({hearts} hearts).");
        }

        if (hearts >= 4)
        {
            return forA2A
                ? (isChinese
                    ? $"- {dn} 与玩家是朋友（{hearts} 颗心）。"
                    : $"- {dn} is a friend of the player ({hearts} hearts).")
                : null;
        }

        return null;
    }

    private static string GetInterNpcRelationships(List<NPC> participants, bool isChinese)
        => NpcRelationRegistry.Instance?.GetRelationships(participants, isChinese);

    private static string TryGetRecentGossip(List<NPC> participants)
    {
        try
        {
            int day;

            try
            {
                day = Game1.Date?.TotalDays ?? -1;
            }
            catch
            {
                day = -1;
            }

            if (day != _lastA2AGossipDay)
            {
                _usedA2AGossipKeys.Clear();
                _lastA2AGossipDay = day;
            }

            var participantNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (participants != null)
            {
                foreach (var n in participants)
                {
                    if (n == null) continue;
                    if (!string.IsNullOrEmpty(n.Name)) participantNames.Add(n.Name);
                    if (!string.IsNullOrEmpty(n.displayName)) participantNames.Add(n.displayName);
                    string zh = NpcNameLocalizer.GetZhName(n.Name);
                    if (!string.IsNullOrEmpty(zh)) participantNames.Add(zh);
                }
            }

            var snapshots = PerceptionManager.Instance?.GetGossipSnapshots();

            if (snapshots == null || snapshots.Count == 0) return null;

            for (int i = snapshots.Count - 1; i >= 0; i--)
            {
                var entry = snapshots[i];

                if (entry == null || string.IsNullOrWhiteSpace(entry.Template))
                    continue;

                string key = $"{entry.Key ?? ""}:{entry.Template}";

                if (_usedA2AGossipKeys.Contains(key))
                    continue;

                if (InvolvesParticipant(entry.Template, participantNames))
                {
                    ModEntry.SMonitor?.Log(
                        $"[A2A] 八卦涉及在场参与者，跳过：{key}",
                        StardewModdingAPI.LogLevel.Trace);
                    continue;
                }

                _usedA2AGossipKeys.Add(key);
                return entry.Template;
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    private static bool InvolvesParticipant(string template, HashSet<string> participantNames)
    {
        if (participantNames.Count == 0) return false;

        foreach (var name in participantNames)
        {
            if (template.Contains(name, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}