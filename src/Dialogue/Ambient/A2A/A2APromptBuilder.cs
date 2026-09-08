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
    // A2A gossip 去重：同一天内不重复使用同一条 gossip
    private static string _lastA2AGossipKey = string.Empty;
    private static int _lastA2AGossipDay = -1;

    /// <summary>
    /// 重置跨存档/跨天的静态八卦去重缓存。在换天、退标题、读档时调用，
    /// 防止旧存档的 "当日已聊过" 标记污染新存档。
    /// </summary>
    internal static void ResetGossipCache()
    {
        _lastA2AGossipKey = string.Empty;
        _lastA2AGossipDay = -1;
    }

    internal A2APromptBuilder()
    {
    }

    /// <summary>
    /// 构建 A2A 请求。必须在主线程调用。
    /// </summary>
    internal DialogueModels.A2ARequest Build(DialogueModels.A2ASession session)
    {
        if (session == null) return null;

        var participants = session.ResolveParticipants();

        if (participants == null || participants.Count < 2)
            return null;

        if (Game1.player == null)
            return null;

        PlayerStateScanner.Scan();

        bool isZh = IsChineseLanguage;

        // 角色名替换：中文模式下使用 NpcNameLocalizer 获得精确的本地化显示名字
        var displayNames = participants
            .Select(n => isZh ? NpcNameLocalizer.GetZhName(n.Name) : (n.displayName ?? n.Name))
            .ToList();

        // 收集 Name、displayName 以及中文名，确保 SceneContextBuilder 排除自身
        var excludeNames = participants
            .SelectMany(n => isZh 
                ? new[] { n.Name, n.displayName, NpcNameLocalizer.GetZhName(n.Name) } 
                : new[] { n.Name, n.displayName })
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
            maxItems: 5,
            excludeNames: excludeNames.ToArray());

        string locationName =
            Game1.player?.currentLocation?.Name ?? (isZh ? "山谷" : "the valley");

        string gossip = TryGetRecentGossip();

        if (string.IsNullOrWhiteSpace(gossip))
        {
            gossip = isZh
                ? $"玩家最近一直在探索{locationName}。"
                : $"The player has been exploring {locationName} lately.";
        }
        else if (isZh)
        {
            gossip = NpcNameLocalizer.LocalizeNamesInText(gossip);
        }

        string lengthDesc = isZh ? "单句口语（10~25字）" : "snappy spoken lines (10-25 words)";

        string lateNightBlock = "";
        if (Game1.timeOfDay >= 2200)
        {
            lateNightBlock = isZh
                ? "- 当前时间：深夜/酒吧打烊前，语气带点疲惫或微醺。"
                : "- Current Time: Late night / near closing time. The tone carries slight fatigue, coziness, or a mellow buzz.";
        }

        string allNames = string.Join(isZh ? "、" : ", ", displayNames);

        // ── 1. System Prompt 构建 ──
        var sysSb = new StringBuilder();
        sysSb.AppendLine(BuildA2ASystemPrompt(isZh, allNames));

        foreach (var pl in personaLines)
            sysSb.AppendLine(pl);

        var relToPlayer = participants
            .Select(n => GetRelationshipLabel(n, isZh, forA2A: true))
            .Where(r => !string.IsNullOrEmpty(r))
            .ToList();

        if (relToPlayer.Count > 0)
        {
            sysSb.AppendLine(isZh ? "## [与玩家的关系]" : "## [Relationship with Player]");

            foreach (var r in relToPlayer)
                sysSb.AppendLine(r);

            sysSb.AppendLine();
        }

        string interRel = GetInterNpcRelationships(participants, isZh);

        if (!string.IsNullOrEmpty(interRel))
        {
            if (isZh)
            {
                interRel = NpcNameLocalizer.LocalizeNamesInText(interRel);
            }

            sysSb.AppendLine(isZh ? "## [参与者之间的关系]" : "## [Relationships Among Participants]");
            sysSb.AppendLine(interRel);
            sysSb.AppendLine();
        }

        // ── 2. User Prompt 构建 ──
        string topicHook = GenerateConversationTopic(participants, isZh);

        var userSb = new StringBuilder();
        userSb.AppendLine(sceneBlock);
        if (!string.IsNullOrEmpty(lateNightBlock))
            userSb.AppendLine(lateNightBlock);
        userSb.AppendLine();
        userSb.AppendLine($"Recent town gossip: {gossip}");
        userSb.AppendLine();

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

    private static string BuildA2ASystemPrompt(bool isZh, string allNames)
    {
        var sb = new StringBuilder();

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
            sb.AppendLine("5. 语言转译：以下人设描述中出现的英文口癖或词组仅为语气机制参考。输出时全篇采用地道自然的目标语言口吻予以呈现。");
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

            if (LocalizedContentManager.CurrentLanguageCode != LocalizedContentManager.LanguageCode.en)
            {
                sb.AppendLine($"Target Output Language: Output all dialogue strictly in {LocalizedContentManager.CurrentLanguageCode}. Fallback to English if not supported.");
                sb.AppendLine("5. Language Adaptation: Any quoted phrases or fillers in the persona descriptions are mechanical illustrations. Render the dialogue entirely in native, fluid phrasing appropriate to the target language.");
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
                new { speaker = displayNames[0], line = "Man... still kind of brisk in here this morning." },
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

        sb.AppendLine(isZh ? "规则：" : "Rules:");
        sb.AppendLine(isZh
            ? "- 纯净输出：首字符必为 [，末字符必为 ]，其间仅包含标准 JSON 数组内容。"
            : "- Strict Format: Output exclusively the raw JSON array starting with [ and ending with ].");
        sb.AppendLine(isZh
            ? $"- 语言风格：自然生活口语，{lengthDesc}。"
            : $"- Style: Natural spoken dialogue, {lengthDesc}.");
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
   private static string GenerateConversationTopic(List<NPC> participants, bool isChinese)
{
    int time = Game1.timeOfDay;
    var loc = Game1.player?.currentLocation;
    string locName = loc?.Name ?? "";
    bool isOutdoors = loc?.IsOutdoors ?? false;
    string season = Game1.currentSeason?.ToLowerInvariant() ?? "spring";

    // 1. 季节与天气感官底色（感官层，完全避免具体道具与粉色大象）
    string seasonMoodZh = season switch
    {
        "spring" => Game1.isRaining ? "春雨连绵的潮湿泥泞" : "初春微凉而透着新绿的空气",
        "summer" => Game1.isRaining ? "沉闷潮湿的夏日暴雨" : "夏日耀眼的阳光与燥热微风",
        "fall"   => Game1.isRaining ? "秋雨浸透落叶的萧瑟微寒" : "秋高气爽的丰收时节与微凉秋风",
        "winter" => "冬日凛冽的寒风与屋外积雪",
        _ => "当季特有的时节气息"
    };

    string seasonMoodEn = season switch
    {
        "spring" => Game1.isRaining ? "damp spring drizzle and wet soil" : "crisp, fresh air of early spring",
        "summer" => Game1.isRaining ? "muggy, humid summer downpours" : "blazing sunlight and heavy summer warmth",
        "fall"   => Game1.isRaining ? "chilly autumn drizzle soaking the leaves" : "brisk autumn breeze and harvest season energy",
        "winter" => "freezing wind and quiet snow outside",
        _ => "the current seasonal atmosphere"
    };

    bool isHome = locName.Contains("Farm", StringComparison.OrdinalIgnoreCase) || 
                  locName.Contains("Cabin", StringComparison.OrdinalIgnoreCase);

    if (isChinese)
    {
        // ── 农舍内部 ──
        if (isHome)
        {
            if (time < 1100)
                return $"清晨的农舍，伴随着{seasonMoodZh}。围绕刚睡醒的困倦、屋里的温度、或是今天各自的打算随口闲聊。";
            if (time >= 2100)
                return "深夜的农舍，白天的活计告一段落。聊聊身体的疲惫感、对炉火或夜宵的渴望，享受安歇前的松弛时光。";
            return "在农舍里的随性日常。吐槽一下屋里的琐碎小事、分享此刻偷闲的心情，或是顺着手头的动静搭两句话。";
        }

        // ── 聚会与休闲场所 ──
        if (locName.Contains("Saloon", StringComparison.OrdinalIgnoreCase))
            return "星之果实酒吧的聚会时光。就着手中的饮品、周遭喧闹的谈笑声或一天的疲惫，放松地调侃几句。";

        if (locName.Contains("Club", StringComparison.OrdinalIgnoreCase) || locName.Contains("CommunityCenter", StringComparison.OrdinalIgnoreCase))
            return "社区中心宽敞整洁的室内。聊聊这里的开阔安静、走动时的脚步声，或是享受这一刻的闲适。";

        // ── 商业与工作场所 ──
        if (locName.Contains("SeedShop", StringComparison.OrdinalIgnoreCase) || locName.Contains("GeneralStore", StringComparison.OrdinalIgnoreCase))
            return "皮埃尔杂货店内。顺着货架间的过道、柜台的动静，或是各自打算采买的东西随意搭话。";

        if (locName.Contains("Hospital", StringComparison.OrdinalIgnoreCase) || locName.Contains("Clinic", StringComparison.OrdinalIgnoreCase))
            return "哈维诊所安静的候诊室。压低声音聊聊近来的身体状态、屋里的药草消毒水味，或是单纯打发等待的时间。";

        if (locName.Contains("ArchaeologyHouse", StringComparison.OrdinalIgnoreCase) || locName.Contains("Library", StringComparison.OrdinalIgnoreCase))
            return "博物馆兼图书馆内。在整齐的书架与展柜间放低嗓音，聊聊这里的清静、翻书声或某个引起好奇的陈设。";

        if (locName.Contains("ScienceHouse", StringComparison.OrdinalIgnoreCase) || locName.Contains("Carpenter", StringComparison.OrdinalIgnoreCase))
            return "罗宾的木匠工坊内。伴随着周围木材的气味或敲打加工的动静，随口聊聊家里需要修整的地方或闲聊近况。";

        if (locName.Contains("AnimalShop", StringComparison.OrdinalIgnoreCase) || locName.Contains("Ranch", StringComparison.OrdinalIgnoreCase))
            return "玛妮的牧场小屋。感受着屋里的暖意与牲畜饲料的气味，聊聊牲畜的动静或打理农庄的琐碎杂事。";

        if (locName.Contains("Blacksmith", StringComparison.OrdinalIgnoreCase))
            return "克林特的铁匠铺。伴随着铁砧与炉火的阵阵热浪，就着刺耳的金属敲击声或工具修整随口闲聊两句。";

        // ── 户外区域 ──
        if (isOutdoors)
        {
            if (locName.Contains("Beach", StringComparison.OrdinalIgnoreCase))
                return $"海风拂面的沙滩边。伴随着{seasonMoodZh}，踩着潮湿沙子随口聊聊浪花声或眼前的开阔景色。";

            if (locName.Contains("Forest", StringComparison.OrdinalIgnoreCase))
                return $"树影斑驳的煤矿森林。伴随着{seasonMoodZh}，踩着落叶与泥土小径，聊聊林间的清幽、鸟鸣动静或散步的惬意。";

            if (locName.Contains("Mountain", StringComparison.OrdinalIgnoreCase))
                return $"山道湖畔的开阔处。伴随着{seasonMoodZh}与山风，随口聊聊清澈的湖水、微凉的山路或当下的脚力。";

            if (locName.Contains("Mine", StringComparison.OrdinalIgnoreCase))
                return "昏暗清凉的矿洞入口。感受着岩壁传来的阵阵凉气与回音，随口提醒注意脚下或聊聊里面的幽深。";

            if (locName.Contains("Railroad", StringComparison.OrdinalIgnoreCase))
                return $"空旷孤寂的铁轨尽头。伴随着{seasonMoodZh}与穿堂风，就着延展向远方的铁轨与高处的微凉。";

            // 鹈鹕镇主街道兜底
            return $"在镇上街道不期而遇。伴随着{seasonMoodZh}，以熟络自然的口吻开启一段短促的街头交谈。";
        }

        // ── 通用室内兜底 ──
        return "在室内偶遇。就着当前的场所氛围与各自手头正在留意的事情，随性搭几句话。";
    }
    else
    {
        // ── Farmhouse ──
        if (isHome)
        {
            if (time < 1100)
                return $"Morning in the farmhouse amid {seasonMoodEn}. Casual talk driven by grogginess, room temperature, or thoughts about the day ahead.";
            if (time >= 2100)
                return "Late night winding down at the farmhouse. Relaxed banter about sore muscles, craving a late bite, or savoring the quiet before bed.";
            return "Casual domestic moments inside. Relaxed banter about minor household clutter, an idle afternoon mood, or whatever is close at hand.";
        } 

        // ── Hangouts & Social Spots ──
        if (locName.Contains("Saloon", StringComparison.OrdinalIgnoreCase))
            return "Hanging out at the Stardrop Saloon. Chatting over drinks, background chatter, or unwinding from the day.";

        if (locName.Contains("Club", StringComparison.OrdinalIgnoreCase) || locName.Contains("CommunityCenter", StringComparison.OrdinalIgnoreCase))
            return "Inside the airy Community Center. Casual conversation sparked by the stillness, echoing footsteps, or taking a relaxed breather.";

        // ── Town Shops & Workplaces ──
        if (locName.Contains("SeedShop", StringComparison.OrdinalIgnoreCase) || locName.Contains("GeneralStore", StringComparison.OrdinalIgnoreCase))
            return "Inside Pierre's General Store. Offhand comments browsing the aisles, listening to register chatter, or errands yet to run.";

        if (locName.Contains("Hospital", StringComparison.OrdinalIgnoreCase) || locName.Contains("Clinic", StringComparison.OrdinalIgnoreCase))
            return "Harvey's quiet waiting room. Lowering voices to chat about everyday health, the clean herbal smell, or passing the time.";

        if (locName.Contains("ArchaeologyHouse", StringComparison.OrdinalIgnoreCase) || locName.Contains("Library", StringComparison.OrdinalIgnoreCase))
            return "Inside the library and museum. Keeping tones hushed amidst the shelves, idle curiosity about displays, or enjoying the calm.";

        if (locName.Contains("ScienceHouse", StringComparison.OrdinalIgnoreCase) || locName.Contains("Carpenter", StringComparison.OrdinalIgnoreCase))
            return "Robin's carpentry shop. Banter surrounded by sawdust scents and workbench sounds, touching on house upkeep or casual gossip.";

        if (locName.Contains("AnimalShop", StringComparison.OrdinalIgnoreCase) || locName.Contains("Ranch", StringComparison.OrdinalIgnoreCase))
            return "Marnie's ranch parlor. Grounded by indoor warmth and livestock sounds outside, trading remarks on barn chores or farm life.";

        if (locName.Contains("Blacksmith", StringComparison.OrdinalIgnoreCase))
            return "Clint's blacksmith shop. Shouting over glowing forge heat and striking metal, talking about stubborn tools or work fatigue.";

        // ── Outdoor Locations ──
        if (isOutdoors)
        {
            if (locName.Contains("Beach", StringComparison.OrdinalIgnoreCase))
                return $"Along the shore with sea air and {seasonMoodEn}. Casual talk inspired by breaking waves, damp sand, or the open horizon.";

            if (locName.Contains("Forest", StringComparison.OrdinalIgnoreCase))
                return $"Under the leafy canopy of Cindersap Forest. Grounded in {seasonMoodEn}, rambling dirt paths, bird calls, or peaceful quiet.";

            if (locName.Contains("Mountain", StringComparison.OrdinalIgnoreCase))
                return $"By the mountain lakeside. Spurred by mountain gusts, {seasonMoodEn}, clear lake waters, or taking a stroll along the slope.";

            if (locName.Contains("Mine", StringComparison.OrdinalIgnoreCase))
                return "The cool, dim mouth of the Mines. Banter stirred by the chilly draft from the depths, echoing rocks, or caution on the steps.";

            if (locName.Contains("Railroad", StringComparison.OrdinalIgnoreCase))
                return $"The empty, breezy railroad stretch. Light remarks colored by {seasonMoodEn}, distant treelines, and open skies.";

            // Town Center fallback
            return $"Crossing paths outdoors in town amidst {seasonMoodEn}. A brief, natural chat fitting the shared weather and familiarity.";
        }

        // ── Generic Indoor Fallback ──
        return "Crossing paths indoors. A relaxed, offhand exchange grounded in the room's atmosphere and whatever caught their eye.";
    }
}

    private static bool IsChineseLanguage =>
        LocalizedContentManager.CurrentLanguageCode == LocalizedContentManager.LanguageCode.zh;

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
                : null; // Bark 侧 4-7 心原本就返回 null，保持不变
        }

        return null;
    }

    private static string GetInterNpcRelationships(List<NPC> participants, bool isChinese)
        => NpcRelationRegistry.Instance?.GetRelationships(participants, isChinese);

    private static string TryGetRecentGossip()
    {
        try
        {
            var snapshots = PerceptionManager.Instance?.GetGossipSnapshots();

            if (snapshots == null || snapshots.Count == 0) return null;

            var last = snapshots[snapshots.Count - 1];

            if (last == null || string.IsNullOrWhiteSpace(last.Template)) return null;

            int day;

            try
            {
                day = Game1.Date?.TotalDays ?? -1;
            }
            catch
            {
                day = -1;
            }

            string key = $"{last.Key ?? ""}:{last.Template}";

            if (_lastA2AGossipDay == day && _lastA2AGossipKey == key)
                return null;

            _lastA2AGossipDay = day;
            _lastA2AGossipKey = key;

            return last.Template;
        }
        catch
        {
            return null;
        }
    }
}