using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using ValleytalkReborn;
using StardewValley;
using StardewValley.GameData.Characters;
using StardewValley.Objects.Trinkets;

namespace ValleytalkReborn;

public class Prompts
{
    public Prompts(DialogueContext context, Character character)
    {
        InitializeInstanceFields(context, character);
    }

    private string BuildStardewSummary()
    {
        var builder = new GameSummaryBuilder();
        var regionMap = builder.GetLocationRegions(); // 从已加载的 GameSummary 里取
        var ctx = BuildContext.FromGameState(CurrentFlags, Context.Location, regionMap);
        return builder.Build(ctx);
    }
    
    private bool _pendingTopicConsumed = false;

    // 🌟【修改 1】：重命名属性为 SystemPrompt，根治与 System 命名空间的冲突问题
    private string _systemPrompt;
    public string SystemPrompt { get => _systemPrompt ??= GetSystemPrompt(); internal set => _systemPrompt = value; }
    
    private string _gameConstantContext;
    public string GameConstantContext { get => _gameConstantContext ??= GetGameConstantContext(); internal set => _gameConstantContext = value; }
    private string _npcConstantContext;
    public string NpcConstantContext { get => _npcConstantContext ??= GetNpcConstantContext(); internal set => _npcConstantContext = value; }
    private string _corePrompt;
    public string CorePrompt { get => _corePrompt ??= GetCorePrompt(); internal set => _corePrompt = value; }
    private string _command;
    public string Command { get => _command ??= GetCommand(); internal set => _command = value; }
    private string _responseStart;
    public string ResponseStart { get => _responseStart ??= GetResponseStart(); internal set => _responseStart = value; }
    private string _instructions;
    public string Instructions { get => _instructions ??= GetInstructions(); internal set => _instructions = value; }

    public string Name { get; internal set; }
    
    public string Gender { get; internal set; }

    // 用于接收从 DialogueContext 传递过来的路由标志位
    public ContextFlags CurrentFlags { get; internal set; } = new ContextFlags 
    { 
        IncludeSafetyRules = true,
        IncludeShortTermContext = true,
        IncludeMemories = true,
        IncludeEnvironment = true,
        IncludeFarmDetails = true,
        IsSimpleGreeting = false
    };

    public Character Character { get; internal set; }

    internal DialogueContext Context { private get; set; }

    CharacterData npcData;
    bool npcIsMale;

    IEnumerable<DialogueValue> dialogueSample;

    IDialogueValue exactLine;
    private string giveGift;
    private SerializableDictionary<string, int> allPreviousActivities;

    private void InitializeInstanceFields(DialogueContext context, Character character)
    {
        npcData = character.StardewNpc.GetData();
        npcIsMale = npcData.Gender == StardewValley.Gender.Male;
        Context = context;
        Character = character;

        dialogueSample = character.SelectDialogueSample(context);
        exactLine = SelectExactDialogue();
        giveGift = context.CanGiveGift ? SelectGiftGiven() : string.Empty;
        allPreviousActivities = Game1.getPlayerOrEventFarmer().previousActiveDialogueEvents.First();

        Name = character.StardewNpc.displayName;
        Gender = character.Bio.Gender;

        PromptOverrides = ModInteropManager.Instance.GetPromptOverrides(character);

        // 绑定总线传递进来的路由标志位
        CurrentFlags = context.RoutingFlags;
    }

    public string GiveGift => giveGift;

    Dictionary<string, IEnumerable<string>> PromptOverrides = new Dictionary<string, IEnumerable<string>>();

    private void DefaultOrOverride(string promptElement, Action<StringBuilder> defaultPromptFunction, StringBuilder prompt)
    {
        if (!PromptOverrides.TryGetValue(promptElement, out var overrideTexts))
        {
            defaultPromptFunction(prompt);
        }
        else
        {
            foreach (var overrideText in overrideTexts)
            {
                if (!string.IsNullOrWhiteSpace(overrideText))
                {
                    prompt.AppendLine(overrideText);
                }
            }
        }
    }

    private string SelectGiftGiven()
    {
        if (!Game1.getPlayerOrEventFarmer().friendshipData.TryGetValue(Character.Name, out Friendship friendship) || !friendship.IsMarried())
        {
            return null;
        }
        if (Game1.random.NextDouble() < 0.8)
        {
            return null;
        }
        var options = Character.DialogueData.AllEntries.SelectMany(x => x.Value.AllValues).SelectMany(x => x.Elements).SelectMany(x => x.GiftOptions).ToList();
        if (options.Count == 0)
        {
            return null;
        }
        return options[Game1.random.Next(options.Count)];
    }

    private string GetSystemPrompt()
    {
        var systemPrompt = new StringBuilder();
        systemPrompt.AppendLine(Util.GetString(Character,"systemPrompt"));
        if (ModEntry.Config.ApplyTranslation)
        {
            systemPrompt.AppendLine(Util.GetString(Character,"systemPromptTranslation", new { Language = ModEntry.Language }));
        }
        return systemPrompt.ToString();
    }

    private string GetGameConstantContext()
    {
        var gameConstantPrompt = new StringBuilder();
        gameConstantPrompt.AppendLine(Util.GetString(Character, "gameContext"));
        // gameConstantPrompt.AppendLine($"##{Util.GetString(Character, "gameSummaryHeading")}");
        gameConstantPrompt.AppendLine(BuildStardewSummary());
        return gameConstantPrompt.ToString();
    }

    private string GetNpcConstantContext()
    {
        var npcConstantPrompt = new StringBuilder();

        var intro = Util.GetString(Character,"npcContextIntro", new { Name = Name });
        npcConstantPrompt.AppendLine(intro);
        if ((Character.Bio?.Biography ?? string.Empty).Length > 10)
        {
            npcConstantPrompt.AppendLine($"##{Util.GetString(Character,"npcContextBiographyHeading", new { Name = Name })}");
            var bio = Character.Bio.Biography;
            while (bio.Contains("\n\n"))
            {
                bio = bio.Replace("\n\n", "\n");
            }
            npcConstantPrompt.AppendLine(bio);
             // 新：IsKnownNpc=true 时整块跳过；否则按 RequiredHearts 过滤
            if (Character.Bio.Relationships.Any() && !Character.Bio.IsKnownNpc)
            {
                int currentHearts = Context.Hearts ?? 0;
                var visible = Character.Bio.Relationships.Values
                    .Where(r => currentHearts >= r.RequiredHearts)
                    .ToList();
                if (visible.Any())
                {
                    npcConstantPrompt.AppendLine($"## {Util.GetString("biographyRelationships")}:");
                    foreach (var relationship in visible)
                        npcConstantPrompt.AppendLine($"* **{relationship.Heading}**: {relationship.Description}");
                }
            }
             // 新：Traits 无论原版/第三方，始终按 RequiredHearts 过滤
            if (Character.Bio.Traits.Any())
            {
                int currentHearts = Context.Hearts ?? 0;
                var visible = Character.Bio.Traits.Values
                    .Where(t => currentHearts >= t.RequiredHearts)
                    .ToList();
                if (visible.Any())
                {
                    npcConstantPrompt.AppendLine($"## {Util.GetString("biographyPersonality")}:");
                    foreach (var trait in visible)
                        npcConstantPrompt.AppendLine($"* **{trait.Heading}**: {trait.Description}");
                }
            }
            npcConstantPrompt.AppendLine(Character.Bio.BiographyEnd);
        }
        return npcConstantPrompt.ToString();
    }

    /// <summary>
    /// Replaces bloated farm/map scans. Provides precise location, time, weather,
    /// festival status, and nearby objects within a 5-tile radius.
    /// </summary>
    private void GetMicroEnvironment(StringBuilder prompt)
    {
        prompt.AppendLine("\n### [SCENE AWARENESS: CURRENT ENVIRONMENT]");

        // 1. Precise Location
        string friendlyLocation = EnvironmentScanner.GetLocationFriendlyName(Context.Location);
        prompt.AppendLine($"- Current Location: {friendlyLocation}");

        // 2. Festival Awareness
        string festival = EnvironmentScanner.GetTodayFestivalName();
        if (!string.IsNullOrEmpty(festival))
        {
            if (EnvironmentScanner.IsFestivalCurrentlyActive())
                prompt.AppendLine($"- Special Event: Currently attending the {festival}! People are celebrating.");
            else
                prompt.AppendLine($"- Special Event: Today is the {festival}. The town is getting ready for it.");
        }

        // 3. Time & Weather (防止今天第二次及以上对话被时间刺激出重复打招呼)
        bool talkedToToday = Game1.player.friendshipData.TryGetValue(Character.Name, out var tempFriendshipData) && tempFriendshipData.TalkedToToday;
        string timeNote = talkedToToday ? " (Environmental reference ONLY; DO NOT greet based on this time)" : "";

        prompt.AppendLine($"- Time: {Context.TimeOfDay}{timeNote}, Season: {Game1.CurrentSeasonDisplayName} (Day {Context.DayOfSeason})");
        if (Context.Weather != null && Context.Weather.Any())
        {
            prompt.AppendLine($"- Weather: {string.Join(", ", Context.Weather)}");
        }

        // 4. 物品视界无条件执行！以 NPC 坐标为中心辐射 5 格扫描
        var nearbyObjects = EnvironmentScanner.ScanNearbyObjects(Character.StardewNpc, 5, 8);
        if (nearbyObjects.Any())
        {
            prompt.AppendLine($"- Objects near you right now: {string.Join(", ", nearbyObjects)}");
        }
        else
        {
            // 明确告知 LLM 周围没有特别的东西，防止凭空胡编乱造
            prompt.AppendLine("- Nothing notable nearby.");
        }

        // 5. Co-location awareness: clarify that NPC is already here with the player,
        // preventing the NPC from describing itself as arriving or entering.
        string npcLocationName    = Character.StardewNpc.currentLocation?.Name ?? "";
        string playerLocationName = Game1.player.currentLocation?.Name ?? "";
        bool coLocated = string.Equals(npcLocationName, playerLocationName,
            StringComparison.OrdinalIgnoreCase);
        if (coLocated)
        {
            prompt.AppendLine("- You are ALREADY here with the farmer. You did not just arrive.");
            prompt.AppendLine("  Do NOT describe yourself entering or arriving. React to the farmer's presence.");
        }

        // 6. 其他 NPC 列表（仍受 IncludeEnvironment 开关控制，节省不必要的 Token）
        if (CurrentFlags.IncludeEnvironment)
        {
            var otherNpcs = Util.GetNearbyNpcs(Character.StardewNpc);
            if (otherNpcs.Any())
            {
                prompt.AppendLine($"- Other people nearby: {string.Join(", ", otherNpcs.Select(n => n.displayName))}");
            }
        }

        string poiContext = CompanionScheduleManager.Instance.GetActivePoiContext();
        if (!string.IsNullOrEmpty(poiContext))
            prompt.AppendLine(poiContext);

        prompt.AppendLine();
    }

    /// <summary>
    /// 核心拦截与包装 Debug 的 GetCorePrompt 方法
    /// </summary>
    private string GetCorePrompt()
    {
        var prompt = new StringBuilder();

        // ── 静态背景层（远离输出端）──────────────────────────────
        DefaultOrOverride("GameState",      GetGameState,      prompt);
        //DefaultOrOverride("SampleDialogue", GetSampleDialogue, prompt);

        if (CurrentFlags.IncludeMemories)
            DefaultOrOverride("EventHistory", GetEventHistory, prompt);

        // ==========================================
        // [Fast Pass]: Simple Greetings
        // ==========================================
        if (CurrentFlags.IsSimpleGreeting && !CurrentFlags.IsMovementRequested)
        {
            prompt.AppendLine($"## {Util.GetString(Character, "coreInstructionHeading")}");
            prompt.AppendLine("- The farmer is just saying a simple greeting or brief statement.");
            prompt.AppendLine("- Respond naturally and concisely without overthinking.");

            GetMicroEnvironment(prompt);
            DefaultOrOverride("CurrentConversation", GetCurrentConversation, prompt);
            InjectPendingTopic(prompt);

            // C1: Player profile（简单问候路径下不需要安全边界，只需身份）
            string simpleProfile = PlayerProfileManager.BuildProfileText(
                Character.StardewNpc,
                playerInput: "",
                flags: CurrentFlags);
            if (!string.IsNullOrEmpty(simpleProfile))
            {
                prompt.AppendLine();
                prompt.AppendLine(simpleProfile);
            }

            string greetingPrompt = prompt.ToString();
            LogRoutingDebug(greetingPrompt, "SIMPLE_GREETING_FAST_PASS");
            return greetingPrompt;
        }

        // ==========================================
        // [Full Context Path]
        // ==========================================

        prompt.AppendLine($"## {Util.GetString(Character, "coreInstructionHeading")}");
        prompt.AppendLine($"### {Util.GetString(Character, "coreContextHeading")}");

        // ── 场景层（固定事实）────────────────────────────────────
        GetMicroEnvironment(prompt);

        // ── 关系与状态层 ─────────────────────────────────────────
        Game1.getPlayerOrEventFarmer().friendshipData.TryGetValue(Character.Name, out Friendship friendship);
        if (friendship.IsMarried() || friendship.IsRoommate())
        {
            if (friendship.IsRoommate())
            {
                DefaultOrOverride("coreRoommates",
                    p => p.AppendLine(Util.GetString(Character, "coreRoommates", new { Name = Name })), prompt);
            }
            else
            {
                prompt.AppendLine(Util.GetString(Character, "coreMarried",
                    new { Name = Name, Pronoun = npcIsMale ? "his" : "her" }));
                var dateNow    = new StardewTime(Game1.Date, 600);
                var whenMarried = dateNow.AddDays(-friendship.DaysMarried);
                // prompt.AppendLine(Util.GetString(Character, "coreMarriedSince",
                //     new { Name = Name, RelativeDate = whenMarried.SinceDescription(dateNow) }));
                DefaultOrOverride("Children", p => GetChildren(p, friendship), prompt);
            }DefaultOrOverride("Spouse", GetSpouse, prompt);
            if (CurrentFlags.IncludeFarmDetails)
                DefaultOrOverride("Trinkets", GetTrinkets, prompt);
            DefaultOrOverride("MarriageFeelings", GetMarriageFeelings, prompt);
        }
        else
        {
            DefaultOrOverride("NonSpouseFriendshipLevel", GetNonSpouseFriendshipLevel, prompt);
            DefaultOrOverride("Spouse",GetSpouse,                   prompt);
            DefaultOrOverride("SpecialRelationshipStatus",
                p => GetSpecialRelationshipStatus(p, friendship), prompt);
        }

        DefaultOrOverride("RecentEvents",GetRecentEvents,          prompt);
        DefaultOrOverride("SpecialDatesAndBirthday", GetSpecialDatesAndBirthday, prompt);
        DefaultOrOverride("Gift",                  GetGift,                  prompt);
        DefaultOrOverride("SpouseAction",          GetSpouseAction,          prompt);

        // prompt.AppendLine(Util.GetString(Character, "coreFarmerGender"));
        // DefaultOrOverride("coreGenderReferences",
        //     p => p.AppendLine(Util.GetString(Character, "coreGenderReferences")), prompt);

        // ── 状态连续性 ───────────────────────────────────────────
        bool talkedToToday = Game1.player.friendshipData
            .TryGetValue(Character.Name, out var tempFriendshipData) && tempFriendshipData.TalkedToToday;
        if (CurrentFlags.IncludeShortTermContext || talkedToToday)
        {
            prompt.AppendLine();
            prompt.AppendLine("[STATE CONTINUITY: MID-CONVERSATION / HANGING OUT]");
            prompt.AppendLine(
                "- Status: You have ALREADY greeted the player today. You are currently in an ongoing interaction.");
            prompt.AppendLine(
                "- Rule 1 (STRICT): Jump STRAIGHT into responding to the player's topic or presence. ABSOLUTELY NO greetings ('Good morning', 'Hello', 'Hey there') or welcome-backs.");
            prompt.AppendLine(
                "- Rule 2 (No Relocation): Do NOT act like you just arrived, woke up, or haven't seen them today.");
            prompt.AppendLine(
                "- Rule 3 (Environmental Inspiration): Use the ambient context (Time, Weather, Nearby Objects) naturally as background atmosphere or subtle actions, combined with your character personality, to keep the chat grounded.");
            prompt.AppendLine();
        }

        // ── 玩家接近信号 ─────────────────────────────────────────
        bool hasNoPlayerInput = !Context.ChatHistory.Any(x => x.IsPlayerLine);
        if (hasNoPlayerInput && !CurrentFlags.IsSimpleGreeting)
        {
            prompt.AppendLine();
            prompt.AppendLine("[SITUATION: PLAYER APPROACHED YOU]");
            prompt.AppendLine("The farmer has just walked up to you and started a conversation.");
            prompt.AppendLine("Respond naturally to their presence — acknowledge them, continue");
            prompt.AppendLine("what you were doing, or start a topic that feels natural for this");
            prompt.AppendLine("moment. Do NOT monologue. React to the farmer being here.");
            prompt.AppendLine();
        }

        // ── 高优先级行为指令（互斥，靠近输出端）─────────────────
        if (CurrentFlags.HasStoodUpPending)
        {
            prompt.AppendLine();
            prompt.AppendLine("[CRITICAL SYSTEM NOTE: STOOD UP — DEMAND AN EXPLANATION]");
            prompt.AppendLine($"The farmer promised to meet you on a date on {CurrentFlags.StoodUpDate}, but they NEVER showed up!");
            prompt.AppendLine("You have been waiting alone all evening and you are HURT and ANGRY. Confront them immediately.");
            prompt.AppendLine();
        }

        if (CurrentFlags.IsInviteRequested && !CurrentFlags.IsOnDate)
        {
            prompt.AppendLine();
            prompt.AppendLine("[SYSTEM NOTE: DATE INVITATION PROTOCOL]");
            prompt.AppendLine("The farmer is asking you out on a date or suggesting to hang out tonight.");

            bool isFestivalToday = Utility.isFestivalDay(Game1.dayOfMonth, Game1.season);
            if (isFestivalToday)
            {
                prompt.AppendLine("[CRITICAL INSTRUCTION — FESTIVAL CONFLICT]");
                prompt.AppendLine("- Today is a FESTIVAL DAY in town! You have festival duties and preparations tonight.");
                prompt.AppendLine("- You CANNOT accept any date invitation for tonight.");
                prompt.AppendLine("- You MUST politely decline in character, explain the festival commitment, and naturally suggest doing it another day.");
                prompt.AppendLine("- DO NOT call the `schedule_date` tool or output any invite tags.");
            }
            else
            {
                var locationList = string.Join("\n", DateManager.LocationDisplayNames
                    .Select(kv => $"  - {kv.Value} -> Output ID: {kv.Key}"));
                prompt.AppendLine("You may agree to meet tonight at 20:00 at one of the following valid locations:");
                prompt.AppendLine(locationList);
                prompt.AppendLine();
                if (ModEntry.Config.UseNativeToolCalling)
                {
                    prompt.AppendLine("[RULES — NATIVE TOOL MODE]");
                    prompt.AppendLine("- IF YOU ACCEPT: Call the `schedule_date` tool with the appropriate location_id.");
                    prompt.AppendLine("- IF YOU DECLINE: Refuse naturally and DO NOT call any tool.");
                }
                else
                {
                    prompt.AppendLine("[RULES]");
                    prompt.AppendLine("- IF YOU ACCEPT: Append [ACTION:INVITE:LocationID] at the absolute end of your response.");
                    prompt.AppendLine("- IF YOU DECLINE: Refuse naturally and DO NOT output any invite tag.");
                }
            }
            prompt.AppendLine();
        }

        if (CurrentFlags.IsOnDate)
        {
            DateManager.LocationDisplayNames.TryGetValue(CurrentFlags.DateLocationId, out string displayName);
            displayName ??= CurrentFlags.DateLocationId;
            prompt.AppendLine();
            prompt.AppendLine("[💕 ROMANTIC DATE MODE ACTIVE]");
            prompt.AppendLine($"You and the farmer are currently on a date at {displayName}. Maintain an affectionate, highly engaged tone.");
            if (ModEntry.Config.UseNativeToolCalling)
                prompt.AppendLine("[SPECIAL INSTRUCTION]: If the farmer says goodbye or suggests ending the date, respond sweetly AND YOU MUST CALL the `end_current_date` tool.");
            else
                prompt.AppendLine("When saying goodbye or ending the date, append [ACTION:END_DATE] at the absolute end of your response.");
            prompt.AppendLine();
        }

        if (CurrentFlags.IsJealousy)
        {
            prompt.AppendLine();
            prompt.AppendLine("[SYSTEM NOTE: JEALOUSY TRIGGERED]");
            prompt.AppendLine($"You somehow sense that the farmer already has plans with {DateManager.Instance.ActiveDateNpcName} tonight. React accordingly.");
            prompt.AppendLine();
        }

        // ── 心事（软提示，可忽略）───────────────────────────────
        DefaultOrOverride("Preoccupation", GetPreoccupation, prompt);

        // ── 动态层（最靠近输出端，权重最高）─────────────────────
        // C1: 农夫身份
        string latestInput = Context.ChatHistory
            .LastOrDefault(x => x.IsPlayerLine)?.Text ?? "";
        string playerProfile = PlayerProfileManager.BuildProfileText(
            Character.StardewNpc,
            playerInput: latestInput,
            flags: CurrentFlags);
        if (!string.IsNullOrEmpty(playerProfile))
        {
            prompt.AppendLine();
            prompt.AppendLine(playerProfile);
            prompt.AppendLine();
        }

        // C3: 对话历史
        DefaultOrOverride("CurrentConversation", GetCurrentConversation, prompt);

        // C2: 未完成情绪（最后读，最新鲜）
        InjectPendingTopic(prompt);

        // ★ 移动/跟随指令：必须紧跟对话历史，权重最高
        InjectMovementInstruction(prompt);

        string finalPrompt = prompt.ToString();
        LogRoutingDebug(finalPrompt, "FULL_CONTEXT_BUILD");
        return finalPrompt;
    }

    private void LogRoutingDebug(string promptText, string routeType)
    {
        try
        {
            if (ModEntry.Config == null || !ModEntry.Config.Debug)
                return;

            int promptLength = promptText.Length;
            int estimatedTokens = promptText.Count(c => c > 127) * 3 / 2 + promptText.Count(c => c <= 127) / 4;

            string debugMsg = $"[ContextRouter] Target: {Name} | Mode: {routeType} | Length: {promptLength} chars (~{estimatedTokens} Tokens) " +
                             $"| [Flags -> Greeting: {CurrentFlags.IsSimpleGreeting}, Farm: {CurrentFlags.IncludeFarmDetails}, " +
                             $"Env: {CurrentFlags.IncludeEnvironment}, Mem: {CurrentFlags.IncludeMemories}]";

            ModEntry.SMonitor?.Log(debugMsg, StardewModdingAPI.LogLevel.Info);
        }
        catch
        {
            // 静默忽略日志异常
        }
    }

    private void GetPreoccupation(StringBuilder prompt)
    {
        bool playerHasSpoken = Context.ChatHistory.Any(x => x.IsPlayerLine);
        if (playerHasSpoken) return;
        if (Game1.random.NextDouble() < 0.5) return;

        var nPreoccupations = Character.PossiblePreoccupations?.Count ?? 0;
        if (nPreoccupations == 0) return;

        string preoccupation;
        if (Game1.Date == Character.PreoccupationDate
            && !string.IsNullOrEmpty(Character.Preoccupation))
        {
            preoccupation = Character.Preoccupation;
        }
        else
        {
            preoccupation = Character.PossiblePreoccupations[Game1.random.Next(nPreoccupations)];
            Character.Preoccupation = preoccupation;
            Character.PreoccupationDate = Game1.Date;
        }

        prompt.AppendLine(Util.GetString(Character, "preoccupation", new { Name = Name, preoccupation = preoccupation }));
        prompt.AppendLine("(This is a background thought — only mention it if it flows");
        prompt.AppendLine("naturally. Prioritize responding to the farmer's presence first.)");
    }

    private void GetCurrentConversation(StringBuilder prompt)
    {
        if (Context.ChatHistory.Any())
        {
            prompt.AppendLine($"###{Util.GetString(Character, "currentConversationHeading")}");
            prompt.AppendLine(Util.GetString(Character, "currentConversationIntro", new { Name = Name }));
            for (int i = 0; i < Context.ChatHistory.Count; i++)
            {
                prompt.AppendLine(Context.ChatHistory[i].IsPlayerLine ? $"- {Util.GetString(Character, "generalFarmerLabel")}: {Context.ChatHistory[i].Text}" : $"- {Name}: {Context.ChatHistory[i].Text}");
            }
        }
        else if (Character.SpokeJustNow())
        {
            prompt.AppendLine($"###{Util.GetString(Character, "currentConversationHeading")}");
            prompt.AppendLine(Util.GetString(Character, "currentConversationJustSpoke", new { Name = Name }));
        }
    }

    private void InjectPendingTopic(StringBuilder prompt)
    {
        if (_pendingTopicConsumed) return;
        _pendingTopicConsumed = true;

        string pending = PendingTopicManager.Instance.ConsumePendingTopic(Character.Name);
        if (string.IsNullOrEmpty(pending)) return;

        prompt.AppendLine();
        prompt.AppendLine("[Unfinished Topic / Mood]");
        prompt.AppendLine("Before this conversation window opened, something was still on your mind:");
        prompt.AppendLine(pending);
        prompt.AppendLine("Let this naturally color your mood or surface in the conversation only");
        prompt.AppendLine("if it fits — do NOT force it.");
        prompt.AppendLine();
    }

    /// <summary>
    /// 移动/跟随指令注入 — 必须是 CorePrompt 最后写入的内容，紧跟对话历史后，权重最高。
    /// </summary>
    private void InjectMovementInstruction(StringBuilder prompt)
    {
        if (!CurrentFlags.IsMovementRequested && !CurrentFlags.IsFollowing) return;
        if (CurrentFlags.IsOnDate) return;

        // 只有跟随状态、无新移动请求时，用轻量提示
        if (CurrentFlags.IsFollowing && !CurrentFlags.IsMovementRequested)
        {
            prompt.AppendLine();
            prompt.AppendLine("[FOLLOWING MODE ACTIVE]");
            prompt.AppendLine("The farmer asked you to come along, so you are currently following them as they move around.");
            prompt.AppendLine("Keep your responses conversational, relaxed, and attentive to what the farmer is doing.");
            prompt.AppendLine();
            return;
        }

        // GoTo 寻路
        if (CurrentFlags.IsGotoRequested)
        {
            string assetList = EnvironmentScanner.FormatAssetsForGotoPrompt(
                Character.StardewNpc, radiusTiles: 15);

            prompt.AppendLine();
            prompt.AppendLine("[SYSTEM NOTE: NAVIGATION REQUEST]");
            prompt.AppendLine("The player is asking you to walk to a specific place or fetch something.");
            prompt.AppendLine();
            prompt.AppendLine(assetList);
            prompt.AppendLine();
            prompt.AppendLine($"Player's request: \"{CurrentFlags.GotoIntentText}\"");
            prompt.AppendLine();
            prompt.AppendLine("Instructions:");
            prompt.AppendLine("- Pick the object from the list above that best matches the player's request.");
            prompt.AppendLine("- If a match exists: respond naturally AND append [ACTION:GOTO:x,y] at the very end of your response using the exact coordinates from the list.");
            prompt.AppendLine("  Example: \"Sure, I'll head over there! [ACTION:GOTO:12,8]\"");
            prompt.AppendLine("- If no match exists: respond naturally explaining you cannot find what they mean. Do NOT output any ACTION tag.");
            prompt.AppendLine("- Abstract requests (e.g. 'get me water') are fine — use your best judgment to match the nearest plausible object.");
            prompt.AppendLine();
            return;
        }

        // 路径被阻挡
        if (CurrentFlags.IsPathBlocked)
        {
            prompt.AppendLine();
            prompt.AppendLine("[CRITICAL SYSTEM NOTE: PHYSICAL BARRIER DETECTED]");
            prompt.AppendLine($"The player just asked you to move {CurrentFlags.BlockDirection.ToString().ToLower()}, but you are BLOCKED by a physical barrier.");
            prompt.AppendLine("You CANNOT move that way. You MUST naturally refuse and point out the obstacle.");
            prompt.AppendLine();
            return;
        }

        // 已贴近
        if (CurrentFlags.IsAlreadyAdjacent)
        {
            prompt.AppendLine();
            prompt.AppendLine("[CRITICAL SYSTEM NOTE: ALREADY ADJACENT]");
            prompt.AppendLine("The player just asked you to move forward or come closer.");
            prompt.AppendLine("HOWEVER, you are ALREADY standing right in front of them face-to-face!");
            prompt.AppendLine("You MUST naturally tease or complain about this in your dialogue.");
            prompt.AppendLine();
            return;
        }

        // 普通移动 / 跟随请求 — 强制要求工具调用
        prompt.AppendLine();
        prompt.AppendLine("[CRITICAL SYSTEM NOTE: MOVEMENT REQUESTED — ACTION REQUIRED]");
        prompt.AppendLine("The player just asked you to move or follow them. The physical path is CLEAR.");
        prompt.AppendLine("YOU MUST call the `trigger_physical_action` tool. This is NOT optional.");
        prompt.AppendLine("- Use action_type `FOLLOW` when the player says 'follow me', 'come with me', 'come along', '跟我', '跟着我', '跟来', or similar.");
        prompt.AppendLine("- Use `STEP:FORWARD` / `STEP:LEFT` / `STEP:RIGHT` / `STEP:BACKWARD` / `STEP:UP` / `STEP:DOWN` for directional commands.");
        prompt.AppendLine("- Speak naturally in your text reply AND simultaneously make the tool call.");
        prompt.AppendLine("- A text-only response with NO tool call is INVALID and will be ignored by the game engine.");
        prompt.AppendLine();
    }

    private void GetSpecialRelationshipStatus(StringBuilder prompt, Friendship friendship)
    {
        if (friendship == null) return;
        
        if (friendship.IsDating())
        {
            var relationshipPublic = Context.Inlaw == null ? Util.GetString(Character,"specialRelationshipDatingPublic") : Util.GetString(Character,"specialRelationshipDatingDiscrete");
            var relationshipWord = RelationshipWord(Context.MaleFarmer, npcIsMale);
            prompt.AppendLine(Util.GetString(Character,"specialRelationshipDating", new { Name= Name, relationshipPublic= relationshipPublic, relationshipWord= relationshipWord }));
        }
        if (friendship.IsEngaged())
        {
            var daysToWedding = friendship.CountdownToWedding;
            prompt.AppendLine(Util.GetString(Character,"specialRelationshipEngaged", new { Name= Name, daysToWedding= daysToWedding }));
        }
        if (friendship.IsDivorced())
        {
            prompt.AppendLine(Util.GetString(Character,"specialRelationshipDivorced", new { Name= Name }));
        }
        if (friendship.ProposalRejected)
        {
            prompt.AppendLine(Util.GetString(Character,"specialRelationshipProposalRejected", new { Name= Name }));
        }
    }

    private void GetSpouse(StringBuilder prompt)
    {
        var spouses = Game1
                    .getPlayerOrEventFarmer()
                    .friendshipData
                    .FieldDict
                    .Where(x => x.Value.Value.IsMarried() && !x.Value.Value.IsRoommate())
                    .Select(x => x.Key);
        bool talkingToSpouse = spouses.Any(x => x == Name);
        spouses = spouses.Where(x => x != Name);
                    
        if (spouses.Any())
        {
            bool multipleOthers = spouses.Count() > 1;
            var spouseList = string.Join(", ", spouses);
            var nSpouses = spouses.Count();
            if (talkingToSpouse)
            {
                var otherSpousesList = multipleOthers ? $"{Util.GetString(Character,"spousesNOtherPeople", new { nSpouses= nSpouses })} {spouseList}":spouses.First();
                var otherSpousesReference = multipleOthers ? Util.GetString(Character,"spousesAllTheOthers") : spouses.First();
                prompt.AppendLine(Util.GetString(Character,"spousesMarriedToOthers", new { Name= Name, otherSpousesList= otherSpousesList, otherSpousesReference= otherSpousesReference }));
            }
            else
            {
                if (multipleOthers)
                {
                    prompt.AppendLine(Util.GetString(Character,"spousesMarriedToMany", new { nSpouses= nSpouses, spouseList= spouseList, Name= Name }));
                }
                else
                {
                    prompt.AppendLine(Util.GetString(Character,"spousesMarriedToOne", new { spouseList= spouseList, Name= Name }));
                }
            }
        }
        var roommates = Game1
                    .getPlayerOrEventFarmer()
                    .friendshipData
                    .FieldDict
                    .Where(x => x.Value.Value.IsMarried() && x.Value.Value.IsRoommate())
                    .Select(x => x.Key);
        bool talkingToRoommate = roommates.Any(x => x == Name);
        roommates = roommates.Where(x => x != Name);
        
        if (roommates.Any())
        {
            bool multipleOthers = roommates.Count() > 1;
            var roommateList = multipleOthers ? $"{Util.GetString(Character,"spousesNOtherPeople", new { nSpouses= roommates.Count() })} {string.Join(", ", roommates)}":roommates.First();
            var roommateReference = multipleOthers ? Util.GetString(Character,"spouseRoommatesAllTheOthers") : roommates.First();
            if (talkingToRoommate)
            {
                prompt.AppendLine(Util.GetString(Character,"spouseRoommatesWithOthers", new { Name= Name, roommateList= roommateList, roommateReference= roommateReference }));
            }
            else
            {
                if (multipleOthers)
                {
                    prompt.AppendLine(Util.GetString(Character,"spouseRoommateWithMany", new { roommateList= roommateList }));
                }
                else
                {
                    prompt.AppendLine(Util.GetString(Character,"spouseRoommateWithOne", new { roommateList= roommateList }));
                }
            }
        }

        var engaged = Game1
                    .getPlayerOrEventFarmer()
                    .friendshipData
                    .FieldDict
                    .Where(x => x.Value.Value.IsEngaged());
        if (engaged.Any(x => x.Key != Name))
        {
            var engagedFirst = engaged.First();
            var engagedTo = Game1.characterData[engagedFirst.Key].DisplayName;
            var weddingDays = engagedFirst.Value.Value.CountdownToWedding;
            prompt.AppendLine(Util.GetString(Character,"spouseEngaged", new { engagedTo= engagedTo, weddingDays= weddingDays }));
        }
        var total = spouses.Count() + engaged.Count();
        if (total > 1 && !talkingToSpouse && !talkingToRoommate)
        {
            prompt.AppendLine(Util.GetString(Character,"spousePoly", new { Name= Name }));
        } else if (total >= 1 && (talkingToSpouse || talkingToRoommate))
        {
            prompt.AppendLine(Util.GetString(Character,"spousePolyView", new { Name= Name }));
        }
    }

    private void GetNonSpouseFriendshipLevel(StringBuilder prompt)
    {
        var isASingle = npcData.CanBeRomanced;
        var isChild = npcData.Age == NpcAge.Child;

        if (isASingle || Context.Hearts <= 6 || Context.Hearts == null)
        {
            prompt.AppendLine((Context.Hearts ?? 0) switch
            {
                -1 => Util.GetString(Character,"nonSpouseFriendshipFirstConversation", new { Name= Name }),
                < 2 => Util.GetString(Character,"nonSpouseFreindshipStrangers", new { Name= Name }),
                < 4 => Util.GetString(Character,"nonSpouseFriendshipAcquaintances", new { Name= Name }),
                < 6 => Util.GetString(Character,"nonSpouseFriendshipFriends", new { Name= Name }),
                < 8 => Util.GetString(Character,"nonSpouseFriendshipCloseFriends", new { Name= Name }),
                <= 10 => Util.GetString(Character,"nonSpouseFriendshipWantToDate", new { Name= Name }),
                <= 14 => Util.GetString(Character,"nonSpouseFriendshipIntimate", new { Name= Name }),
                _ => throw new InvalidDataException("Invalid heart level.")
            });
        }
        else
        {
            if (Context.Hearts <= 8 && !isChild)
            {
                prompt.AppendLine(Util.GetString(Character,"nonSpouseFriendshipNonSingleAdult8", new { Name= Name }));
            }
            else if (isChild)
            {
                prompt.AppendLine(Util.GetString(Character,"nonSpouseFriendshipChild8Plus", new { Name= Name }));
            }
            else
            {
                prompt.AppendLine(Util.GetString(Character,"nonSpouseFriendshipNonSingleAdult10", new { Name= Name }));
            }
        }
    }

    private void GetSpouseAction(StringBuilder prompt)
    {
        if (Context.SpouseAct == null) return;

        prompt.AppendLine(Context.SpouseAct switch
        {
            SpouseAction.funLeave => Util.GetString(Character,"spouseActionFunLeave", new { Name= Name }),
            SpouseAction.jobLeave => Util.GetString(Character,"spouseActionJobLeave", new { Name= Name }),
            SpouseAction.patio => Util.GetString(Character,"spouseActionPatio", new { Name= Name }),
            SpouseAction.funReturn => Util.GetString(Character,"spouseActionFunReturn", new { Name= Name }),
            SpouseAction.jobReturn => Util.GetString(Character,"spouseActionJobReturn", new { Name= Name }),
            SpouseAction.spouseRoom => Util.GetString(Character,"spouseActionSpouseRoom", new { Name= Name }),
            _ => $""
        });
    }

    private void GetGift(StringBuilder prompt)
    {
        if (Context.Accept != null)
        {
            var giftName = Context.Accept.DisplayName;
            prompt.AppendLine(Util.GetString(Character,"giftIntro", new { Name= Name, giftName= giftName }));
            switch (Context.GiftTaste)
            {
                case 0:
                    prompt.AppendLine(Util.GetString(Character,"giftLoved", new { Name= Name }));
                    break;
                case 2:
                    prompt.AppendLine(Util.GetString(Character,"giftLiked", new { Name= Name }));
                    break;
                case 4:
                    prompt.AppendLine(Util.GetString(Character,"giftDislike", new { Name= Name }));
                    break;
                case 6:
                    prompt.AppendLine(Util.GetString(Character,"giftHate", new { Name= Name }));
                    break;
                default:
                    prompt.AppendLine(Util.GetString(Character,"giftNeutral", new { Name= Name }));
                    break;
            }
            prompt.AppendLine(Util.GetString(Character,"giftMustIncludeReaction", new { Name= Name }));
            if (Context.Birthday)
            {
                prompt.AppendLine(Util.GetString(Character,"giftBirthday", new { Name= Name }));
            }
            prompt.AppendLine(Util.GetString(Character,"giftOutro"));
        }
        else if (!string.IsNullOrEmpty(giveGift))
        {
            string giftName = giveGift;
            if (Game1.objectData.ContainsKey(giveGift))
            {
                giftName = Game1.objectData[giveGift].DisplayName;
                giftName = LoadLocalised(giftName);
            }
            prompt.AppendLine(Util.GetString(Character,"giftGiving", new { Name= Name, GiftName= giftName }));
        }
    }

    private void GetSpecialDatesAndBirthday(StringBuilder prompt)
    {
        if (Context.DayOfSeason == null) return;

        prompt.AppendLine((Context.Season, Context.DayOfSeason) switch
        {
            (Season.Spring, 1) => Util.GetString(Character,"specialDatesSpring1"),
            (Season.Spring, 12) => Util.GetString(Character,"specialDatesSpring12"),
            (Season.Spring, 23) => Util.GetString(Character,"specialDatesSpring23"),
            (Season.Summer, 1) => Util.GetString(Character,"specialDatesSummer1"),
            (Season.Summer, 10) => Util.GetString(Character,"specialDatesSummer10"),
            (Season.Summer, 27) => Util.GetString(Character,"specialDatesSummer27"),
            (Season.Summer, 28) => Util.GetString(Character,"specialDatesSummer28"),
            (Season.Fall, 1) => Util.GetString(Character,"specialDatesFall1"),
            (Season.Fall, 15) => Util.GetString(Character,"specialDatesFall15"),
            (Season.Fall, 26) => Util.GetString(Character,"specialDatesFall26"),
            (Season.Winter, 1) => Util.GetString(Character,"specialDatesWInter1"),
            (Season.Winter, 7) => Util.GetString(Character,"specialDatesWinter7"),
            (Season.Winter, 24) => Util.GetString(Character,"specialDatesWinter24"),
            (Season.Winter, 28) => Util.GetString(Character,"specialDatesWinter28"),
            _ => $""
        });
        var stardewBioData = Character.StardewNpc.GetData();
        if (
            string.Equals(
                Context.Season.Value.ToString(),
                stardewBioData.BirthSeason.ToString(),
                StringComparison.InvariantCultureIgnoreCase
            ) && Context.DayOfSeason == stardewBioData.BirthDay)
        {
            prompt.AppendLine(Util.GetString(Character,"specialDatesBirthday", new { Name= Name }));
        }
    }

    private void GetRecentEvents(StringBuilder prompt)
    {
        var eventSection = new StringBuilder();
        foreach (var activity in allPreviousActivities.Where(x => x.Value < 7))
        {
            var theLine = activity.Key switch
            {
                "cc_Boulder" => /*Util.GetString(Character,"recentEventsBoulder")*/ null,
                "cc_Bridge" => /*Util.GetString(Character,"recentEventsQuarryBridge")*/ null,
                "cc_Bus" => /*Util.GetString(Character,"recentEventsBus")*/ null,
                "cc_Greenhouse" => /*Util.GetString(Character,"recentEventsGreenhouse")*/ null,
                "cc_Minecart" => /*Util.GetString(Character,"recentEventsMinecarts")*/ null,
                "cc_Complete" => /*Util.GetString(Character,"recentEventsCommunityCenter")*/ null,
                "movieTheater" => /*Util.GetString(Character,"recentEventsMovieTheatre")*/ null,
                "pamHouseUpgrade" => /*Util.GetString(Character,"recentEventsPamHouse")*/ null,
                "pamHouseUpgradeAnonymous" => /*Util.GetString(Character,"recentEventsPamHouseAnonymous")*/ null,
                "jojaMartStruckByLightning" => /*Util.GetString(Character,"recentEventsJojaLightning")*/ null,
                "babyBoy" => Util.GetString(Character,"recentEventsBabyBoy"),
                "babyGirl" => Util.GetString(Character,"recentEventsBabyGirl"),
                "wedding" => Util.GetString(Character,"recentEventsMarried"),
                "luauBest" => /*Util.GetString(Character,"recentEventsLuauBest")*/ null,
                "luauShorts" => /*Util.GetString(Character,"recentEventsLuauShorts")*/ null,
                "luauPoisoned" => /*Util.GetString(Character,"recentEventsLuauPoisoned")*/ null,
                "Characters_MovieInvite_Invited" => Util.GetString(Character,"recentEventsMovieInvited", new { Name= Name }),
                "DumpsterDiveComment" => Util.GetString(Character,"recentEventsDumpsterDive", new { Name= Name }),
                "GreenRainFinished" => /*Util.GetString(Character,"recentEventsGreenRain")*/ null,
                _ => $""
            };
            if (!string.IsNullOrWhiteSpace(theLine))
            {
                eventSection.AppendLine(theLine);
            }
        }
        if (eventSection.Length > 0)
        {
            prompt.AppendLine($"## {Util.GetString(Character,"recentEventsHeading")}");
            prompt.AppendLine(Util.GetString(Character,"recentEventsIntro"));
            prompt.AppendLine(eventSection.ToString());
        }
    }

    private void GetMarriageFeelings(StringBuilder prompt)
    {
        var IsRoommate = Game1.getPlayerOrEventFarmer().friendshipData[Character.Name].IsRoommate();
        var marriageOrRoommate = IsRoommate ? Util.GetString(Character,"generalBeingRoommates") : Util.GetString(Character,"generalTheMarriage");
        switch (Context.Hearts)
        {
            case > 12:
                prompt.AppendLine(Util.GetString(Character,"marriageSentimentGood", new { Name= Name, marriageOrRoommate= marriageOrRoommate }));
                break;
            case < 10:
                prompt.AppendLine(Util.GetString(Character,"marriageSentimentBad", new { Name= Name, marriageOrRoommate= marriageOrRoommate }));
                break;
            default:
                prompt.AppendLine(Util.GetString(Character,"marriageSentimentNeutral", new { Name= Name, marriageOrRoommate= marriageOrRoommate }));
                break;
        }
    }

    private void GetTrinkets(StringBuilder prompt)
    {
        var trinkets = Game1.getPlayerOrEventFarmer().trinketItems;
        if (trinkets == null) return;
        var nonNullTrinkets = trinkets.Where(x => x != null).ToList();
        if (!nonNullTrinkets.Any()) return;
        
        if (nonNullTrinkets.Any(x => x.GetEffect() is FairyBoxTrinketEffect))
        {
            prompt.AppendLine(Util.GetString(Character, "trinketsFairyBox", new { Name }));
        }
        else
        {
            var firstTrinketWithEffect = nonNullTrinkets.FirstOrDefault(x => x.GetEffect() is not null);
            if (firstTrinketWithEffect != null)
            {
                TrinketEffect companionEffect = firstTrinketWithEffect.GetEffect() as TrinketEffect;
                if (companionEffect.Companion is StardewValley.Companions.HungryFrogCompanion)
                {
                    prompt.AppendLine(Util.GetString(Character, "trinketsCompanionFrog", new { Name }));
                }
                else if (companionEffect.Companion is StardewValley.Companions.FlyingCompanion)
                {
                    prompt.AppendLine(Util.GetString(Character, "trinketsCompanionParrot", new { Name }));
                }
            }
        }
    }

    private void GetChildren(StringBuilder prompt, Friendship friendship)
    {
        if (Context.Children.Count == 0)
        {
            prompt.AppendLine(Util.GetString(Character, "childrenNone", new { Name = Name }));
        }
        else
        {
            if (Context.Children.Count > 1)
            {
                var count = Context.Children.Count;
                prompt.AppendLine(Util.GetString(Character, "childrenMultiple", new { Name = Name, count = count }));
            }
            else
            {
                prompt.AppendLine(Util.GetString(Character, "childrenSingle", new { Name = Name }));
            }
            prompt.AppendLine();
            foreach (var child in Context.Children)
            {
                prompt.AppendLine($"- {Util.GetString(Character, child.IsMale ? "childrenDescriptionBoy" : "childDescriptionGirl", new { Name = child.Name, Age = child.Age })}");
            }
        }
        if (friendship.DaysUntilBirthing > 0)
        {
            var daysUntilBirth = friendship.DaysUntilBirthing;
            prompt.AppendLine(Util.GetString(Character, "childrenPregnant", new { Name = Name, daysUntilBirth = daysUntilBirth }));
        }
    }

    private void GetEventHistory(StringBuilder prompt)
    {
        EventHistoryHelper.BuildEventHistory(prompt, Character, Context);
    }

    private void GetSampleDialogue(StringBuilder prompt)
    {
        return;
    }

    private void GetGameState(StringBuilder prompt)
    {
        // prompt.AppendLine($"## {Util.GetString(Character,"gameStateHeading")}");
        // if (allPreviousActivities.ContainsKey("cc_Complete"))
        //     prompt.AppendLine(Util.GetString(Character,"gameStateCommunityCenterYes"));
        // else
        //     prompt.AppendLine(Util.GetString(Character,"gameStateCommunityCenterNo"));
        // if (allPreviousActivities.ContainsKey("cc_Bus"))
        //     prompt.AppendLine(Util.GetString(Character,"gameStateBusYes"));
        // else
        //     prompt.AppendLine(Util.GetString(Character,"gameStateBusNo"));
        // if (allPreviousActivities.ContainsKey("cc_Bridge"))
        //     prompt.AppendLine(Util.GetString(Character,"gameStateQuarryBridgeYes"));
        // else
        //     prompt.AppendLine(Util.GetString(Character,"gameStateQuarryBridgeNo"));
        // if (allPreviousActivities.ContainsKey("cc_Minecart"))
        //     prompt.AppendLine(Util.GetString(Character,"gameStateMinecartYes"));
        // else
        //     prompt.AppendLine(Util.GetString(Character,"gameStateMinecartNo"));
        // if (allPreviousActivities.ContainsKey("cc_Boulder"))
        //     prompt.AppendLine(Util.GetString(Character,"gameStateBoulderYes"));
        // else
        //     prompt.AppendLine(Util.GetString(Character,"gameStateBoulderNo"));
        if (Game1.year == 1)
        {
            prompt.AppendLine(Util.GetString(Character,"gameStateKentNo"));
        }
        else
        {
            prompt.AppendLine(Util.GetString(Character,"gameStateKentYes"));
        }
    }

    private string GetCommand()
    {
        var commandPrompt = new StringBuilder();
        commandPrompt.AppendLine($"##{Util.GetString(Character, "commandHeading")}");
        commandPrompt.AppendLine(Util.GetString(Character, "commandIntro", new { Name = Name }));
        DefaultOrOverride("ReplaceSchedule", p => {
            if (!string.IsNullOrWhiteSpace(Context.ScheduleLine) && !Context.ChatHistory.Any() && !Character.SpokeJustNow())
            {
                p.AppendLine();
                p.AppendLine(Util.GetString(Character, "commandReplaceSchedule", new { ScheduleLine = Context.ScheduleLine }));
            }
        }, commandPrompt);

        commandPrompt.AppendLine();
        commandPrompt.AppendLine("### [CRITICAL SYSTEM TRIGGER: PHYSICAL ACTIONS]");
        commandPrompt.AppendLine("- If the player's text implies a physical action, respond appropriately.");

        if (ModEntry.Config.UseNativeToolCalling)
        {
            commandPrompt.AppendLine("- Emote bubbles: Use text tags like [ACTION:EMOTE:ANGRY], [ACTION:EMOTE:HEART], [ACTION:EMOTE:BLUSH] if needed.");
            commandPrompt.AppendLine("- Physical Movements & Following: CALL the `trigger_physical_action` tool with appropriate parameters.");
            commandPrompt.AppendLine("- Do NOT type movement action tags directly into your speech unless tool calling fails.");
        }
        else
        {
            commandPrompt.AppendLine("- Emote bubbles: [ACTION:EMOTE:ANGRY], [ACTION:EMOTE:SAD], [ACTION:EMOTE:HEART], [ACTION:EMOTE:HAPPY], [ACTION:EMOTE:BLUSH], [ACTION:EMOTE:SURPRISE]");
            commandPrompt.AppendLine("- Turn tags: [ACTION:FACE:FARMER] (look at player), [ACTION:FACE:UP] / [DOWN] / [LEFT] / [RIGHT] (turn away)");
            commandPrompt.AppendLine("- Movement tags (ONLY use if path is clear): [ACTION:STEP:FORWARD], [ACTION:STEP:BACKWARD], [ACTION:STEP:LEFT], [ACTION:STEP:RIGHT], [ACTION:FOLLOW]");
            commandPrompt.AppendLine();
            commandPrompt.AppendLine("OUTPUT FORMAT — action tags go AFTER your spoken words, at the very end of the line:");
            commandPrompt.AppendLine("  EXAMPLE (follow):  'Fine, I'll come with you! $h [ACTION:FOLLOW]'");
            commandPrompt.AppendLine("  EXAMPLE (move):    'Okay, stepping back. [ACTION:STEP:BACKWARD]'");
            commandPrompt.AppendLine("  EXAMPLE (emote):   'Hehe, of course! [ACTION:EMOTE:HAPPY]'");
        }

        commandPrompt.AppendLine();

        if (ModEntry.Config.UseNativeToolCalling)
        {
            commandPrompt.AppendLine();
            commandPrompt.AppendLine("### [CRITICAL DUAL-OUTPUT RULE]");
            commandPrompt.AppendLine("When calling any tool (such as `trigger_physical_action` or `schedule_date`), you MUST ALWAYS simultaneously output natural spoken dialogue in your text response. NEVER execute a tool call silently without speaking.");
        }

        if (ModEntry.Config.ApplyTranslation)
        {
            commandPrompt.AppendLine(Util.GetString(Character, "instructionsTranslate", new { Language = ModEntry.Language }));
        }
        return commandPrompt.ToString();
    }

    private string GetInstructions()
    {
        var instructions = new StringBuilder();
        instructions.AppendLine($"##{Util.GetString(Character, "instructionsHeading")}");
        instructions.AppendLine(Util.GetString(Character, "instructionsIntro", new { Name = Name }));
        if (dialogueSample != null && dialogueSample.Any())
        {
            instructions.AppendLine(Util.GetString(Character, "instructionsSampleDialogue", new { Name = Name }));
        }

        instructions.AppendLine(Util.GetString(Character, "instructionsFarmersName"));
        instructions.AppendLine(Util.GetString(Character, "instructionsBreaks"));
        instructions.AppendLine(Util.GetString(Character, "instructionsSingleLine"));
        instructions.AppendLine(Util.GetString(Character, "instructionsResponses", new { Name = Name }));
        if (!Character.Bio.ExtraPortraits.ContainsKey("!"))
        {
            var extraPortraits = new StringBuilder();
            foreach (var portrait in Character.Bio.ExtraPortraits)
            {
                extraPortraits.Append(Util.GetString(Character, "instructionsExtraPortraitLine",
                    new { Key = portrait.Key, Value = portrait.Value }));
            }

            instructions.AppendLine(Util.GetString(Character, "instructionsEmotion",
                new { extraPortraits = extraPortraits }));
        }

        // 1. 如果有 ExtraInstructions，追加进去
        if (!string.IsNullOrWhiteSpace(Llm.Instance.ExtraInstructions))
        {
            instructions.AppendLine(Llm.Instance.ExtraInstructions);
        }

        // 2. 在最外侧统一 return，保证 100% 的代码路径都有返回值！
        return instructions.ToString();
    }

    private string GetResponseStart()
    {
        return Util.GetString(Character,"responseStart", new { Name= Name });
    }

    private string RelationshipWord(bool maleFarmer, bool npcIsMale)
    {
        return maleFarmer ? (npcIsMale ? Util.GetString(Character,"generalGayMale") : Util.GetString(Character,"generalHeterosexual")) : (npcIsMale ? Util.GetString(Character,"generalHeterosexual") : Util.GetString(Character,"generalLesbian"));
    }

    private static string LoadLocalised(string thisName)
    {
        if (string.IsNullOrWhiteSpace(thisName)) return string.Empty;
        if (thisName.StartsWith("[LocalizedText ", StringComparison.InvariantCultureIgnoreCase))
        {
            thisName = thisName.Substring(15, thisName.Length - 16);
            var translation = Game1.content.LoadString(thisName);

            if (translation != null)
            {
                thisName = translation;
            }
        }
        return thisName;
    }

    private IDialogueValue SelectExactDialogue()
    {
        return (Character.DialogueData
                    ?.AllEntries
                    .FirstOrDefault(x => x.Key == Context))?.Value;
    }
}