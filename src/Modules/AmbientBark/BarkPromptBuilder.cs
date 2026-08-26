using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using StardewValley;

namespace ValleytalkReborn;

/// <summary>
/// Bark Prompt 构建器：负责将 NPC 游戏状态转换为纯数据 BarkRequest。
/// 只允许主线程调用。返回的 BarkRequest 不包含 NPC、Farmer、GameLocation 引用。
/// </summary>
internal sealed class BarkPromptBuilder
{
    private readonly AmbientBarkStateStore _stateStore;

    internal BarkPromptBuilder(AmbientBarkStateStore stateStore)
    {
        _stateStore = stateStore;
    }

    /// <summary>
    /// 构建 Bark 请求。必须在主线程调用。
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
        string userPrompt = BuildMicroPrompt(npc);

        if (string.IsNullOrWhiteSpace(userPrompt))
            return null;

        string zhExample = JsonConvert.SerializeObject(new[]
        {
            "今天天气真好。",
            "好像快下雨了……",
            "嗯，有点饿了。",
            "这里挺安静的。",
            "农场的作物长得不错。"
        });

        string enExample = JsonConvert.SerializeObject(new[]
        {
            "Nice weather today.",
            "Looks like rain...",
            "I could use a snack.",
            "Pretty quiet here.",
            "The crops are coming along."
        });

        string systemInstruction = isZh
            ? "你正在为游戏NPC生成日常随口念叨台词。" +
              "输出格式：恰好5个字符串组成的JSON数组，每个元素是一句台词文本。" +
              "核心要求：5句台词必须涵盖不同的生活与人际维度，避免多句重复同一话题。" +
              $"示例格式：{zhExample}" +
              "提及玩家时用\"你\"或角色对玩家的自然称呼。"
            : "You are generating ambient barks for an NPC. " +
              "Output format: exactly a JSON array of 5 plain dialogue strings. " +
              "Core Requirement: The 5 lines must span varied topics (personal life, friends, work, surroundings). Avoid repeating the same theme across multiple lines. " +
              $"Example: {enExample} " +
              "CRITICAL: Output MUST start with [ and end with ]. Example: [\"Line 1\", \"Line 2\", \"Line 3\", \"Line 4\", \"Line 5\"] " +
              "DO NOT omit the opening bracket [ or closing bracket ]. " +
              "Refer to the player naturally as 'you' or by nickname.";

        return new DialogueModels.BarkRequest
        {
            NpcName = npc.Name,
            SystemPrompt = systemInstruction,
            UserPrompt = userPrompt,
            IsChinese = isZh
        };
    }

    /// <summary>
    /// 构建 Micro Prompt（完整的用户提示词）。
    /// </summary>
    private string BuildMicroPrompt(NPC npc)
    {
        if (npc == null) return null;

        var character = DialogueBuilder.Instance?.GetCharacter(npc);
        var bio = character?.Bio;

        if (bio == null) return null;

        bool isZh = IsChineseLanguage;
        var sb = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(bio.AmbientBarkPrompt))
        {
            string enrichedPersona = EnrichBarkPromptWithState(npc, bio.AmbientBarkPrompt.Trim(), isZh);

            sb.AppendLine(isZh ? "### [角色性格]" : "### [CHARACTER PERSONA]");

            sb.AppendLine(isZh
                ? $"- 性格：{enrichedPersona}"
                : $"- Persona: {enrichedPersona}");

            string rel = GetRelationshipLabel(npc, isZh);

            if (!string.IsNullOrEmpty(rel))
                sb.AppendLine(rel);

            sb.AppendLine();
        }

        string indoorOutdoor = BuildIndoorOutdoorContext(npc, isZh);

        if (!string.IsNullOrEmpty(indoorOutdoor))
        {
            sb.AppendLine(isZh ? "### [位置环境]" : "### [LOCATION CONTEXT]");
            sb.AppendLine(indoorOutdoor);
            sb.AppendLine();
        }

        var localPerceptions = PerceptionManager.Instance?.GetFilteredBucketFor(npc.Name, 2);

        if (localPerceptions != null && localPerceptions.Count > 0)
        {
            sb.AppendLine(isZh ? "### [即时观察细节]" : "### [IMMEDIATE OBSERVATIONS]");

            foreach (var p in localPerceptions)
            {
                if (!string.IsNullOrWhiteSpace(p.Template))
                    sb.AppendLine($"- {p.Template}");
            }

            sb.AppendLine();
        }

        sb.AppendLine(SceneContextBuilder.BuildSceneBlock(npc, radiusTiles: 5, maxItems: 6));
        sb.AppendLine();

        if (_stateStore.TryGet(npc.Name, out var barkState))
        {
            List<string> recent;

            lock (barkState)
            {
                recent = barkState.RecentBarks.ToList();
            }

            if (recent.Count > 0)
            {
                sb.AppendLine(isZh ? "### [话题新鲜度]" : "### [TOPIC FRESHNESS]");

                sb.AppendLine(isZh
                    ? "为保持对话丰富度，请选择与以下历史感叹【截然不同的新视角、新话题或新感官细节】进行创作："
                    : "Explore FRESH perspectives, new sensory details, or topics completely unrelated to these recent lines:");

                foreach (var recentLine in recent)
                    sb.AppendLine($"- \"{recentLine}\"");

                sb.AppendLine();
            }
        }

        if (isZh)
        {
            sb.AppendLine("### [台词多样性要求]");
            sb.AppendLine("请生成 5 条【完全不同角度】的随口念叨，确保内容丰富多元：");
            sb.AppendLine("1. 个人手头计划 / 兴趣爱好 / 碎碎念");
            sb.AppendLine("2. 对玩家或身边其他村民的留意 / 随口问候");
            sb.AppendLine("3. 对当前具体地点、工作或小镇日常的看法");
            sb.AppendLine("4. 当下心情或生活小烦恼/小期待");
            sb.AppendLine("5. 周围环境感叹（注：提及天气的台词在 5 条中【最多仅允许 1 条】，其余请关注生活细节）。");
            sb.AppendLine("以 5 个字符串组成的 JSON 数组格式输出。");
        }
        else
        {
            sb.AppendLine("### [DIVERSITY REQUIREMENTS]");
            sb.AppendLine("Generate 5 distinct barks covering DIFFERENT topics:");
            sb.AppendLine("1. Personal hobbies, upcoming plans, or casual inner thoughts");
            sb.AppendLine("2. Casual observation or friendly greeting toward the player or nearby villagers");
            sb.AppendLine("3. Thoughts on current location, daily tasks, or town life");
            sb.AppendLine("4. Current mood or relatable everyday moments");
            sb.AppendLine("5. Ambient observation (CRITICAL: Max 1 bark may mention the weather; keep the rest focused on life/character).");
            sb.AppendLine("Output strictly as a JSON array of 5 plain strings.");
        }

        return sb.ToString();
    }

    private static bool IsChineseLanguage =>
        LocalizedContentManager.CurrentLanguageCode == LocalizedContentManager.LanguageCode.zh;

    private static string EnrichBarkPromptWithState(NPC npc, string basePrompt, bool isChinese)
    {
        if (npc == null || string.IsNullOrWhiteSpace(basePrompt))
            return basePrompt;

        if (npc.Name.Equals("Shane", StringComparison.OrdinalIgnoreCase))
        {
            int hearts = Game1.player?.friendshipData.TryGetValue("Shane", out var fs) == true
                ? fs.Points / 250
                : 0;

            bool isSober = CompanionScheduleManager.IsLegalSpouse("Shane") || hearts >= 6;

            if (isChinese)
            {
                string stateConstraint = isSober
                    ? "【戒酒状态：已完全戒酒，在酒吧或日常仅喝苏打水/气泡水，绝不提及或点啤酒/酒精。】"
                    : "【嗜酒状态：处于酒精成瘾沉沦期，经常在酒吧灌啤酒以逃避现实。】";

                return $"{basePrompt} {stateConstraint}";
            }
            else
            {
                string stateConstraint = isSober
                    ? " [SOBRIETY: Completely sober. Strictly drinks soda or sparkling water; never mentions or orders beer/alcohol.]"
                    : " [ALCOHOLIC: Currently struggles with alcoholism and frequently drinks beer at the saloon.]";

                return $"{basePrompt} {stateConstraint}";
            }
        }

        return basePrompt;
    }

    private static string GetRelationshipLabel(NPC npc, bool isChinese)
    {
        if (npc == null) return null;

        string dn = npc.displayName ?? npc.Name;

        if (PolyamorySweetLoveBridge.IsOfficialSpouse(npc))
        {
            return isChinese
                ? $"- 关系：{dn} 是玩家的配偶。"
                : $"- Relationship: {dn} is married to the player, sharing this home. Speak with comfortable ownership, deep warmth, and effortless intimacy.";
        }

        if (PolyamorySweetLoveBridge.IsUnofficialSpouse(npc))
        {
            return isChinese
                ? $"- 关系：{dn} 与玩家是热恋中的情侣，请展现出恋人的欣喜与热络。"
                : $"- Relationship: {dn} is in a sweet romantic relationship with the player, bound by high trust. Express genuine fondness and enthusiastic warmth.";
        }

        var player = Game1.player;

        if (player?.friendshipData == null) return null;

        if (!player.friendshipData.TryGetValue(npc.Name, out var fs) || fs == null)
            return null;

        if (fs.IsMarried())
        {
            return isChinese
                ? $"- 关系：{dn} 是玩家的配偶。这里是你们共同的家。"
                : $"- Relationship: {dn} is married to the player in their shared home. Maintain a cozy, deeply familiar tone.";
        }

        if (fs.IsDating())
        {
            return isChinese
                ? $"- 关系：{dn} 正在与玩家恋爱中，请表现出对恋人的依恋、温柔与喜爱。"
                : $"- Relationship: {dn} is dating the player. Express gentle affection, romantic interest, and warm engagement.";
        }

        int hearts = fs.Points / 250;

        if (hearts >= 6)
        {
            return isChinese
                ? $"- 关系：{dn} 与玩家是亲密挚友（{hearts} 颗心），请使用熟络、轻松且充满善意的语气。"
                : $"- Relationship: {dn} and the player are close friends ({hearts} hearts). Speak with easy familiarity and genuine friendliness.";
        }

        if (hearts >= 4)
        {
            // 原始逻辑：4+ 心时 Bark 不显示关系文本
            return null;
        }

        return null;
    }

    private static string BuildIndoorOutdoorContext(NPC npc, bool isChinese)
    {
        var loc = npc?.currentLocation;

        if (loc == null) return null;

        bool isIndoor = !loc.IsOutdoors
                        || loc is StardewValley.Locations.FarmHouse
                        || loc is StardewValley.Locations.IslandFarmHouse
                        || loc is StardewValley.Locations.DecoratableLocation
                        || loc is StardewValley.Locations.LibraryMuseum
                        || loc is StardewValley.Locations.SeedShop
                        || loc.Name == "Hospital"
                        || loc.Name == "Saloon"
                        || loc.Name == "Blacksmith"
                        || loc.Name == "ManorHouse"
                        || loc.Name == "ScienceHouse"
                        || loc.Name == "JoshHouse"
                        || loc.Name == "HaleyHouse"
                        || loc.Name == "SamHouse"
                        || loc.Name == "ElliottHouse"
                        || loc.Name == "WizardHouse"
                        || loc.Name == "ArchaeologyHouse"
                        || loc.Name == "Tent"
                        || loc.Name == "CommunityCenter_JojaRuins";

        if (isIndoor)
        {
            string locDesc = GetLocationFriendlyName(loc);

            return isChinese
                ? $"- 环境：室内（{locDesc}）。NPC 已经在屋内，请【仅】针对当前的室内家具、氛围或活动发表感想。"
                : $"- Environment: INDOORS ({locDesc}). The NPC is already inside. Focus EXCLUSIVELY on the indoor furniture, atmosphere, or immediate indoor activities.";
        }

        string locName = GetLocationFriendlyName(loc);

        return isChinese
            ? $"- 环境：室外（当前位于 {locName}，天气：{GetWeatherDescriptionChinese()}）。天气仅作为背景氛围，重点关注当前地点、手头事情、心情或对身边事物的留意。"
            : $"- Environment: OUTDOORS (Location: {locName}, Weather: {GetWeatherDescription()}). Weather is only background context; focus primarily on the location, personal thoughts, tasks, or surroundings.";
    }

    private static string GetLocationFriendlyName(GameLocation loc)
    {
        if (loc == null) return "unknown location";

        string display = loc.NameOrUniqueName;

        if (!string.IsNullOrWhiteSpace(display) && display != loc.Name) return display;

        return loc.Name switch
        {
            "FarmHouse" => "the farmhouse",
            "Farm" => "the farm",
            "SeedShop" => "Pierre's shop",
            "Saloon" => "the Saloon",
            "Hospital" => "Harvey's clinic",
            "Blacksmith" => "Clint's blacksmith",
            "ManorHouse" => "the Manor House",
            "ScienceHouse" => "Robin's house",
            "JoshHouse" => "Alex and Evelyn's house",
            "HaleyHouse" => "Emily and Haley's house",
            "SamHouse" => "Sam's house",
            "ElliottHouse" => "Elliott's beach house",
            "WizardHouse" => "the Wizard's tower",
            "ArchaeologyHouse" => "the museum",
            "Tent" => "Linus's tent",
            _ => loc.Name
        };
    }

    private static string GetWeatherDescription()
    {
        if (Game1.isSnowing) return "snowing";
        if (Game1.isRaining) return "raining";
        if (Game1.isLightning) return "stormy";
        if (Game1.isDebrisWeather) return "windy";
        return "clear";
    }

    private static string GetWeatherDescriptionChinese()
    {
        if (Game1.isSnowing) return "下雪";
        if (Game1.isRaining) return "下雨";
        if (Game1.isLightning) return "雷雨";
        if (Game1.isDebrisWeather) return "刮风";
        return "晴天";
    }
}
