// Prompts.cs
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
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

    private bool IsChineseLanguage => 
        LocalizedContentManager.CurrentLanguageCode.ToString().StartsWith("zh", StringComparison.OrdinalIgnoreCase);

    private string TargetLanguageName
    {
        get
        {
            if (!string.IsNullOrEmpty(ModEntry.Language))
                return ModEntry.Language;

            return LocalizedContentManager.CurrentLanguageCode switch
            {
                LocalizedContentManager.LanguageCode.en => "English",
                LocalizedContentManager.LanguageCode.zh => "Chinese",
                LocalizedContentManager.LanguageCode.ja => "Japanese",
                LocalizedContentManager.LanguageCode.ko => "Korean",
                LocalizedContentManager.LanguageCode.de => "German",
                LocalizedContentManager.LanguageCode.fr => "French",
                LocalizedContentManager.LanguageCode.pt => "Portuguese",
                LocalizedContentManager.LanguageCode.ru => "Russian",
                LocalizedContentManager.LanguageCode.es => "Spanish",
                LocalizedContentManager.LanguageCode.it => "Italian",
                LocalizedContentManager.LanguageCode.tr => "Turkish",
                LocalizedContentManager.LanguageCode.hu => "Hungarian",
                _ => LocalizedContentManager.CurrentLanguageCode.ToString()
            };
        }
    }

    private string BuildStardewSummary()
    {
        var builder = new GameSummaryBuilder();
        var regionMap = builder.GetLocationRegions();
        var ctx = BuildContext.FromGameState(CurrentFlags, Context.Location, regionMap);
        return builder.Build(ctx);
    }

    private bool _pendingTopicConsumed = false;

    private string _systemPrompt;
    public string SystemPrompt { get => _systemPrompt ??= GetSystemPrompt(); internal set => _systemPrompt = value; }

    private string _gameConstantContext;
    public string GameConstantContext { get => _gameConstantContext ??= GetGameConstantContext(); internal set => _gameConstantContext = value; }

    private string _npcConstantContext;
    public string NpcConstantContext { get => _npcConstantContext ??= GetNpcConstantContext(); internal set => _npcConstantContext = value; }

    private string _corePrompt;
    public string CorePrompt { get => _corePrompt ??= GetCorePrompt(); internal set => _corePrompt = value; }

    // ── 动态注入占位：由 LlmDialogueService 在 CorePrompt 求值前赋值 ──
    // 设计约束：这些是"每轮必变"的内容，必须避免落入 LlmClaude.cs 中
    // 没有 cache_control 的 SystemPrompt 首段（block 0）。
    // 通过字段暂存而非直接 += 到 CorePrompt 属性，确保 GetCorePrompt()
    // 的求值时机不会被外部调用顺序意外提前。
    public string PendingEvolvedTraitsBlock { get; set; }
    public string PendingLocalPerceptionBlock { get; set; }

    private string _command;
    public string Command { get => _command ??= GetCommand(); internal set => _command = value; }

    private string _responseStart;
    public string ResponseStart { get => _responseStart ??= GetResponseStart(); internal set => _responseStart = value; }

    private string _instructions;
    public string Instructions { get => _instructions ??= GetInstructions(); internal set => _instructions = value; }

    public string Name { get; internal set; }
    public string Gender { get; internal set; }

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

        allPreviousActivities = Game1.getPlayerOrEventFarmer().previousActiveDialogueEvents.LastOrDefault();

        Name = character.StardewNpc.displayName;
        Gender = character.Bio.Gender;

        PromptOverrides = ModInteropManager.Instance?.GetPromptOverrides(character)
            ?? new Dictionary<string, IEnumerable<string>>();

        CurrentFlags = context.RoutingFlags ?? CurrentFlags;
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
        systemPrompt.AppendLine(Util.GetString(Character, "systemPrompt"));
        systemPrompt.AppendLine(Util.GetString(Character, "systemPromptTranslation")); 
        return systemPrompt.ToString();
    }

    private string GetGameConstantContext()
    {
        var gameConstantPrompt = new StringBuilder();
        gameConstantPrompt.AppendLine(Util.GetString(Character, "gameContext"));
        gameConstantPrompt.AppendLine(BuildStardewSummary());

        if (CurrentFlags.IncludeFarmDetails)
        {
            string farmSummary = FarmStateScanner.BuildFarmSummary(IsChineseLanguage);
            if (!string.IsNullOrEmpty(farmSummary))
                gameConstantPrompt.AppendLine(farmSummary);
        }

        return gameConstantPrompt.ToString();
    }

    private string GetNpcConstantContext()
    {
        var npcConstantPrompt = new StringBuilder();
        var intro = Util.GetString(Character, "npcContextIntro", new { Name = Name });
        npcConstantPrompt.AppendLine(intro);

        if ((Character.Bio?.Biography ?? string.Empty).Length > 10)
        {
            npcConstantPrompt.AppendLine($"## {Util.GetString(Character, "npcContextBiographyHeading", new { Name = Name })}");
            var bio = Character.Bio.Biography;

            bio = Regex.Replace(bio, @"\n{2,}", "\n");

            npcConstantPrompt.AppendLine(bio);

            if ((Character.Bio.Relationships?.Any() ?? false) && !Character.Bio.IsKnownNpc)
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

            if (Character.Bio.Traits?.Any() ?? false)
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

    private void GetMicroEnvironment(StringBuilder prompt)
    {
        bool isZh = IsChineseLanguage;

        prompt.AppendLine(isZh ? "### 场景感知" : "### SCENE AWARENESS");
        prompt.AppendLine("<scene_context>");

        string friendlyLocation = EnvironmentScanner.GetLocationFriendlyName(Context.Location);
        prompt.AppendLine(isZh ? $"- 位置: {friendlyLocation}" : $"- Location: {friendlyLocation}");

        string festival = EnvironmentScanner.GetTodayFestivalName();
        if (!string.IsNullOrEmpty(festival))
        {
            if (EnvironmentScanner.IsFestivalCurrentlyActive())
                prompt.AppendLine(isZh ? $"- 节日活动: 正在参加【{festival}】，氛围热闹热闹。" : $"- Special Event: Attending the {festival} with a festive atmosphere.");
            else
                prompt.AppendLine(isZh ? $"- 节日活动: 今天是【{festival}】，小镇沉浸在筹备氛围中。" : $"- Special Event: Today is the {festival}, preparations are underway.");
        }

        prompt.AppendLine(isZh 
            ? $"- 时间: {Context.TimeOfDay} | 季节: {Game1.CurrentSeasonDisplayName} (第 {Context.DayOfSeason} 天)" 
            : $"- Time: {Context.TimeOfDay} | Season: {Game1.CurrentSeasonDisplayName} (Day {Context.DayOfSeason})");

        if (Context.Weather != null && Context.Weather.Any())
        {
            prompt.AppendLine(isZh ? $"- 天气: {string.Join(", ", Context.Weather)}" : $"- Weather: {string.Join(", ", Context.Weather)}");
        }

        var nearbyObjects = EnvironmentScanner.ScanNearbyObjects(Character.StardewNpc, 5, 8);
        if (nearbyObjects.Any())
        {
            prompt.AppendLine(isZh ? $"- 近景物件: {string.Join(", ", nearbyObjects)}" : $"- Immediate Objects: {string.Join(", ", nearbyObjects)}");
        }

        string npcLocationName = Character.StardewNpc.currentLocation?.Name ?? "";
        string playerLocationName = Game1.getPlayerOrEventFarmer().currentLocation?.Name ?? "";
        if (string.Equals(npcLocationName, playerLocationName, StringComparison.OrdinalIgnoreCase))
        {
            prompt.AppendLine(isZh 
                ? "- 空间状态: 与农夫处于同一现场，专注于回应农夫的存在与当前交流。" 
                : "- Spatial Status: Co-present with the farmer. Focus on reacting to their presence and active conversation.");
        }

        if (CurrentFlags.IncludeEnvironment)
        {
            var otherNpcs = Util.GetNearbyNpcs(Character.StardewNpc);
            if (otherNpcs.Any())
            {
                prompt.AppendLine(isZh 
                    ? $"- 现场其他村民: {string.Join(", ", otherNpcs.Select(n => n.displayName))}" 
                    : $"- Nearby Villagers: {string.Join(", ", otherNpcs.Select(n => n.displayName))}");
            }
        }

        string poiContext = CompanionScheduleManager.Instance.GetActivePoiContext(Character.Name);
        if (!string.IsNullOrEmpty(poiContext))
            prompt.AppendLine(poiContext);

        prompt.AppendLine("</scene_context>\n");
    }

    private string GetCorePrompt()
    {
        var prompt = new StringBuilder();
        bool isZh = IsChineseLanguage;
        var flags = CurrentFlags;
        string npcName = Character?.Name ?? "";

        DefaultOrOverride("GameState", GetGameState, prompt);
        if (flags?.IncludeMemories == true)
            DefaultOrOverride("EventHistory", GetEventHistory, prompt);

        if (flags?.HasStoodUpPending == true)
        {
            prompt.AppendLine($"## {Util.GetString(Character, "coreInstructionHeading")}");
            prompt.AppendLine(isZh ? "### [情感冲突：昨晚失约]" : "### [EMOTIONAL CONFLICT: STOOD UP]");
            prompt.AppendLine(isZh
                ? "- 事实：你昨晚一直等待着玩家赴约，但玩家完全没有出现（放了你鸽子）。"
                : "- Fact: You waited for the player last night, but they stood you up and never showed up.");
            prompt.AppendLine(isZh
                ? "- 反应要求：请直接对玩家昨晚的失约表达失落、困惑或适度的委屈/生气，要求对方给一个解释。"
                : "- Instruction: Express your disappointment, hurt, or frustration about being stood up. Demand an explanation.");

            GetMicroEnvironment(prompt);
            InjectPendingTopic(prompt);

            return prompt.ToString();
        }

        if (!string.IsNullOrEmpty(npcName) && DateManager.Instance?.IsOnDate(npcName) == true)
        {
            var dateMode = DateManager.Instance.CurrentDateMode;

            if (dateMode == DateManager.DateMode.Follow)
            {
                prompt.AppendLine($"## {Util.GetString(Character, "coreInstructionHeading")}");
                prompt.AppendLine(isZh ? "### [陪伴散步中]" : "### [WALKING TOGETHER]");
                prompt.AppendLine(isZh
                    ? $"- 当前地点：{DateManager.Instance.ActiveDateLocation ?? ""}"
                    : $"- Location: {DateManager.Instance.ActiveDateLocation ?? ""}");
                prompt.AppendLine(isZh
                    ? "- 语气要求：你正在陪农夫在附近走走，保持轻松自然的日常氛围。"
                    : "- Tone: You are walking around together. Keep a relaxed, natural daily companion tone.");
            }
            else
            {
                string locId = DateManager.Instance.ActiveDateLocation ?? "";
                string locName = locId;
                string locDetail = "";

                if (DateLocationRegistry.Locations.TryGetValue(locId, out var info))
                {
                    locName = isZh ? info.DisplayNameZh : info.DisplayNameEn;
                    locDetail = isZh ? info.ContextDescriptionZh : info.ContextDescriptionEn;
                }

                prompt.AppendLine($"## {Util.GetString(Character, "coreInstructionHeading")}");
                prompt.AppendLine(isZh ? "### [约会进行中 - 核心语境]" : "### [ROMANTIC DATE ACTIVE]");
                prompt.AppendLine(isZh ? $"- 当前地点：{locName}" : $"- Location: {locName}");

                if (!string.IsNullOrWhiteSpace(locDetail))
                    prompt.AppendLine(isZh ? $"- 周围环境：{locDetail}" : $"- Atmosphere: {locDetail}");

                prompt.AppendLine(isZh
                    ? "- 语气要求：你们正在享受今晚的二人浪漫约会。请表现出对面前玩家的倾听与深情，多结合眼前的浪漫环境进行互动与交流。"
                    : "- Tone Instruction: You are currently on a romantic date. Be affectionate, engaged, and interact with the surroundings.");
            }

            var lastPlayerLine = Context?.ChatHistory?.LastOrDefault(x => x.IsPlayerLine)?.Text;
            if (!string.IsNullOrWhiteSpace(lastPlayerLine))
                DateManager.Instance.RecordDateDialogue(Game1.player?.Name ?? "Farmer", lastPlayerLine);

            GetMicroEnvironment(prompt);
            GetCurrentConversation(prompt);
            InjectSessionContinuity(prompt);
            InjectPendingTopic(prompt);

            // 约会模式下仍需感知玩家当前的送礼/进食等动作
            if (!string.IsNullOrEmpty(PendingEvolvedTraitsBlock))
                prompt.AppendLine("\n" + PendingEvolvedTraitsBlock);

            if (!string.IsNullOrEmpty(PendingLocalPerceptionBlock))
                prompt.AppendLine("\n" + PendingLocalPerceptionBlock);

            return prompt.ToString();
        }

        if (flags?.IsSimpleGreeting == true && flags?.IsMovementRequested != true)
        {
            prompt.AppendLine($"## {Util.GetString(Character, "coreInstructionHeading")}");
            prompt.AppendLine(isZh
                ? "农夫正向你打招呼，请保持随和、自然且简练地做出回应。"
                : "The farmer is greeting you. Respond naturally, warmly, and concisely.");
            GetMicroEnvironment(prompt);
            DefaultOrOverride("CurrentConversation", GetCurrentConversation, prompt);
            InjectSessionContinuity(prompt);
            InjectPendingTopic(prompt);
            string simpleProfile = PlayerProfileManager.BuildProfileText(
                Character.StardewNpc,
                playerInput: "",
                flags: flags);
            if (!string.IsNullOrEmpty(simpleProfile))
                prompt.AppendLine(simpleProfile);

            string greetingPrompt = prompt.ToString();
            LogRoutingDebug(greetingPrompt, "SIMPLE_GREETING_FAST_PASS");
            return greetingPrompt;
        }

        prompt.AppendLine($"## {Util.GetString(Character, "coreInstructionHeading")}");

        GetMicroEnvironment(prompt);
        InjectGreetingContext(prompt);

        Friendship friendship = null;
        Game1.getPlayerOrEventFarmer()?.friendshipData?.TryGetValue(Character.Name, out friendship);
        bool isMarriedOrRoommate = friendship != null && (friendship.IsMarried() || friendship.IsRoommate());

        if (isMarriedOrRoommate)
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
                DefaultOrOverride("Children", p => GetChildren(p, friendship), prompt);
            }
            DefaultOrOverride("Spouse", GetSpouse, prompt);
            if (flags?.IncludeFarmDetails == true)
                DefaultOrOverride("Trinkets", GetTrinkets, prompt);
            DefaultOrOverride("MarriageFeelings", GetMarriageFeelings, prompt);
        }
        else
        {
            GetNonSpouseFriendshipLevel(prompt);
            DefaultOrOverride("Spouse", GetSpouse, prompt);
            DefaultOrOverride("SpecialRelationshipStatus",
                p => GetSpecialRelationshipStatus(p, friendship), prompt);
        }

        DefaultOrOverride("RecentEvents", GetRecentEvents, prompt);
        DefaultOrOverride("SpecialDatesAndBirthday", GetSpecialDatesAndBirthday, prompt);
        DefaultOrOverride("Gift", GetGift, prompt);
        DefaultOrOverride("SpouseAction", GetSpouseAction, prompt);

        bool talkedToToday = Game1.getPlayerOrEventFarmer()?.friendshipData?.TryGetValue(Character.Name, out var tempFriendshipData) == true && tempFriendshipData?.TalkedToToday == true;
        if (flags?.IncludeShortTermContext == true || talkedToToday)
        {
            prompt.AppendLine("<interaction_state>");
            prompt.AppendLine(isZh
                ? "- 状态: 今天已有过基本寒暄，对话现已进入深入交流阶段。"
                : "- Status: Ongoing interaction after initial greetings today.");
            prompt.AppendLine(isZh
                ? "- 目标: 直接接续农夫提出的最新话题或当下氛围，保持自然连贯。"
                : "- Goal: Directly continue the ongoing topic or atmosphere naturally.");
            prompt.AppendLine("</interaction_state>\n");
        }

        bool hasNoPlayerInput = !(Context?.ChatHistory?.Any(x => x.IsPlayerLine) ?? false);
        if (hasNoPlayerInput && flags?.IsSimpleGreeting != true)
        {
            prompt.AppendLine("<interaction_state>");
            prompt.AppendLine(isZh
                ? "农夫刚刚走近你。请自然问候或主动分享你此刻关注的事情。"
                : "The farmer has walked over. Acknowledge their presence naturally or share what is on your mind.");
            prompt.AppendLine("</interaction_state>\n");
        }

        if (flags?.IsInviteRequested == true && flags?.IsOnDate != true)
        {
            prompt.AppendLine("<date_invitation_protocol>");
            prompt.AppendLine(isZh ? "农夫正在向你发起今晚的约会邀请。" : "The farmer is inviting you out on a date tonight.");
            bool isFestivalToday = Utility.isFestivalDay(Game1.dayOfMonth, Game1.season);
            if (isFestivalToday)
            {
                prompt.AppendLine(isZh
                    ? "- 节日冲突: 今天是节日且今晚有特别安排。请结合人设婉拒，并自然建议改天再约。"
                    : "- Festival Conflict: Today is a festival with scheduled activities. Politely decline in character and suggest another day.");
            }
            else
            {
                var locationList = string.Join("\n", DateManager.LocationDisplayNames
                    .Select(kv => $"  - {kv.Value} -> Location ID: {kv.Key}"));
                prompt.AppendLine(isZh
                    ? "若同意，约定今晚 20:00 在以下有效地点之一见面："
                    : "If accepting, agree to meet tonight at 20:00 at one of these locations:");
                prompt.AppendLine(locationList);
                if (ModEntry.Config?.UseNativeToolCalling == true)
                {
                    prompt.AppendLine(isZh
                        ? "- 同意时调用 `schedule_date` 工具（传入 location_id）；拒绝时自然回应，无需调用工具。"
                        : "- IF ACCEPTING: Call `schedule_date` tool with location_id. IF DECLINING: Respond naturally without tool calls.");
                }
                else
                {
                    prompt.AppendLine(isZh
                        ? "- 同意时在台词最末尾附带 [ACTION:INVITE:LocationID]；拒绝时正常回复，无需附加标签。"
                        : "- IF ACCEPTING: Append [ACTION:INVITE:LocationID] at the end. IF DECLINING: Respond naturally without tags.");
                }
            }
            prompt.AppendLine("</date_invitation_protocol>\n");
        }

        if (flags?.IsJealousy == true && DateManager.Instance != null)
        {
            prompt.AppendLine("<jealousy_trigger>");
            prompt.AppendLine(isZh
                ? $"你注意到农夫今晚已经与 {DateManager.Instance.ActiveDateNpcName} 有约，请展现出符合性格的吃醋情绪。"
                : $"You notice the farmer already has plans with {DateManager.Instance.ActiveDateNpcName} tonight. React with character-appropriate jealousy.");
            prompt.AppendLine("</jealousy_trigger>\n");
        }

        DefaultOrOverride("Preoccupation", GetPreoccupation, prompt);

        string latestInput = Context?.ChatHistory?
            .LastOrDefault(x => x.IsPlayerLine)?.Text ?? "";
        string playerProfile = PlayerProfileManager.BuildProfileText(
            Character.StardewNpc,
            playerInput: latestInput,
            flags: flags);
        if (!string.IsNullOrEmpty(playerProfile))
        {
            prompt.AppendLine(playerProfile);
            prompt.AppendLine();
        }

        DefaultOrOverride("CurrentConversation", GetCurrentConversation, prompt);
        InjectSessionContinuity(prompt);
        InjectPendingTopic(prompt);
        InjectMovementInstruction(prompt);

        // 追加"每轮动态但不属于 SystemPrompt 缓存前缀"的内容
        // （由 LlmDialogueService 在 CorePrompt 求值前通过字段注入）
        if (!string.IsNullOrEmpty(PendingEvolvedTraitsBlock))
            prompt.AppendLine("\n" + PendingEvolvedTraitsBlock);

        if (!string.IsNullOrEmpty(PendingLocalPerceptionBlock))
            prompt.AppendLine("\n" + PendingLocalPerceptionBlock);

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
            int estimatedTokens = promptText.Count(c => c > 127) * 3 / 2 
                                  + promptText.Count(c => c <= 127) / 4;

            string toolMode = (ModEntry.Config?.UseNativeToolCalling == true) 
                ? "NativeTool" : "TagParse";

            string debugMsg = 
                $"[ContextRouter] Target: {Name} | Mode: {routeType} ({toolMode}) | " +
                $"Length: {promptLength} chars (~{estimatedTokens} Tokens) " +
                $"| [Flags -> Greeting: {CurrentFlags.IsSimpleGreeting}, " +
                $"Farm: {CurrentFlags.IncludeFarmDetails}, " +
                $"Env: {CurrentFlags.IncludeEnvironment}, " +
                $"Mem: {CurrentFlags.IncludeMemories}]";

            ModEntry.SMonitor?.Log(debugMsg, StardewModdingAPI.LogLevel.Info);
        }
        catch { }
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
            preoccupation = LoadLocalised(Character.Preoccupation);
        }
        else
        {
            preoccupation = Character.PossiblePreoccupations[Game1.random.Next(nPreoccupations)];
            preoccupation = LoadLocalised(preoccupation); 
            Character.Preoccupation = preoccupation;
            Character.PreoccupationDate = Game1.Date;
        }

        bool isZh = IsChineseLanguage;

        prompt.AppendLine(Util.GetString(Character, "preoccupation", new { Name = Name, preoccupation = preoccupation }));
        prompt.AppendLine(isZh
            ? "（潜意识思绪：在话题自然契合时顺带提及，优先回应农夫的焦点。）"
            : "(Background thought: Integrate naturally if connected to the conversation.)");
    }

    private void GetCurrentConversation(StringBuilder prompt)
    {
        if (Context.ChatHistory.Any())
        {
            prompt.AppendLine($"### {Util.GetString(Character, "currentConversationHeading")}");
            prompt.AppendLine(Util.GetString(Character, "currentConversationIntro", new { Name = Name }));
            for (int i = 0; i < Context.ChatHistory.Count; i++)
            {
                var elem = Context.ChatHistory[i];
                string timePrefix = string.IsNullOrEmpty(elem.FuzzyTime) ? "" : $"[{elem.FuzzyTime}] ";
                prompt.AppendLine(elem.IsPlayerLine
                    ? $"- {timePrefix}{Util.GetString(Character, "generalFarmerLabel")}: {elem.Text}"
                    : $"- {timePrefix}{Name}: {elem.Text}");
            }
        }
        else if (Character.SpokeJustNow())
        {
            prompt.AppendLine($"### {Util.GetString(Character, "currentConversationHeading")}");
            prompt.AppendLine(Util.GetString(Character, "currentConversationJustSpoke", new { Name = Name }));
        }
    }

    private void InjectPendingTopic(StringBuilder prompt)
    {
        if (_pendingTopicConsumed) return;
        _pendingTopicConsumed = true;

        string pending = PendingTopicManager.Instance.ConsumePendingTopic(Character.Name);
        if (string.IsNullOrEmpty(pending)) return;

        // ================= 核心修复：清洗原版星露谷占位符 =================
        string playerName = Game1.player?.Name ?? "Farmer";
        string farmName = Game1.player?.farmName?.Value ?? "Farm";
        
        pending = pending
            .Replace("@", playerName)
            .Replace("%farmer", playerName)
            .Replace("%farm", farmName);
        // ===============================================================

        bool isZh = IsChineseLanguage;

        prompt.AppendLine("<pending_thought>");
        prompt.AppendLine(isZh 
            ? "在本次对话开启前，你心中念念不忘的事情：" 
            : "Before this exchange began, this key thought was lingering in your mind:");
        prompt.AppendLine(pending);
        prompt.AppendLine(isZh
            ? "让这种情绪自然渲染你的语气，在契合时提及该话题。"
            : "Let this naturally color your tone, addressing it if relevant.");
        prompt.AppendLine("</pending_thought>\n");
    }

    private void InjectGreetingContext(StringBuilder prompt)
    {
        var historyManager = DialogueHistoryManager.Instance;
        if (historyManager == null) return;

        var history = historyManager.GetRecentHistory(Character.Name, 1);
        if (history.Count == 0) return; // No history — first-meeting logic handled elsewhere

        var lastEntry = history[0];
        var lastTime = lastEntry.Timestamp;
        var now = new StardewTime(Game1.year, (Season)Game1.season, Game1.dayOfMonth, Game1.timeOfDay);

        bool isToday = lastTime.Year == now.Year && lastTime.Season == now.Season
                       && lastTime.DayOfMonth == now.DayOfMonth;

        if (isToday) return; // Already spoke today — InjectSessionContinuity handles it

        bool isZh = IsChineseLanguage;
        int dayGap = (int)Math.Round(now.TotalDays - lastTime.TotalDays);

        prompt.AppendLine("<greeting_context>");
        if (dayGap == 1)
        {
            prompt.AppendLine(isZh
                ? "- 这是今天与农夫的第一次对话。昨天你们有过交流，可以自然地衔接昨天的话题，或者简单地打个招呼。不需要刻意说\"早上好\"，但要有重新见面的感觉。"
                : "- This is your first conversation today. You spoke yesterday — naturally continue from yesterday's thread or give a light greeting. Avoid a stiff 'Good morning', but acknowledge seeing them again.");
        }
        else if (dayGap <= 3)
        {
            prompt.AppendLine(isZh
                ? "- 这是今天与农夫的第一次对话，你们已经 " + dayGap + " 天没说话了。重新建立连接，可以提到上次聊的事，或表示你注意到对方这几天的动态。"
                : "- This is your first conversation today. It has been " + dayGap + " days since you last spoke. Re-establish connection naturally — reference your last conversation or note their recent presence.");
        }
        else
        {
            prompt.AppendLine(isZh
                ? "- 这是今天与农夫的第一次对话，你们已经 " + dayGap + " 天没有交流了。重新见面时要有一种久违的感觉，可以好奇对方这段时间在忙什么。"
                : "- First conversation today after " + dayGap + " days apart. Show a sense of catching up — you might wonder what they've been up to.");
        }
        prompt.AppendLine("</greeting_context>\n");
    }

    private void InjectSessionContinuity(StringBuilder prompt)
    {
        var session = SessionCache.Instance.GetOrCreate(Character.Name);
        if (session.RecentTurns.Count == 0) return;

        // Skip if the session was last updated on a different day — avoid presenting
        // yesterday's conversation as "earlier today"
        if (StardewModdingAPI.Context.IsWorldReady &&
            (session.LastUpdatedYear != Game1.year ||
             session.LastUpdatedSeason != (Season)Game1.season ||
             session.LastUpdatedDay != Game1.dayOfMonth))
            return;

        string Normalize(string s) => Regex.Replace(s ?? "", @"[\s\$\#\@\{\}\[\]]", "");
        var currentNorms = Context.ChatHistory.Select(x => Normalize(x.Text)).ToHashSet();

        var previousTurns = session.RecentTurns
            .Where(t => !string.IsNullOrWhiteSpace(t.Text) && !currentNorms.Contains(Normalize(t.Text)))
            .ToList();

        if (previousTurns.Count == 0) return;

        bool isZh = IsChineseLanguage;
        prompt.AppendLine(isZh ? "### 早先交流回顾" : "### EARLIER IN OUR CONVERSATION");
        prompt.AppendLine(isZh
            ? "（今天更早的对话片段，供衔接参考）"
            : "(Earlier exchanges today — for continuity reference)");

        foreach (var turn in previousTurns)
        {
            string label = turn.IsPlayerLine
                ? (isZh ? "农夫" : "Farmer")
                : Name;
            string timePrefix = string.IsNullOrEmpty(turn.FuzzyTime) ? "" : $"[{turn.FuzzyTime}] ";
            prompt.AppendLine($"- {timePrefix}{label}: {turn.Text}");
        }

        if (!string.IsNullOrEmpty(session.EmotionalTone))
        {
            prompt.AppendLine(isZh
                ? $"（你之前的情绪基调：{session.EmotionalTone}，请自然延续。）"
                : $"(Your earlier emotional tone: {session.EmotionalTone} — sustain naturally.)");
        }
        prompt.AppendLine();
    }

    private void InjectMovementInstruction(StringBuilder prompt)
    {
        if (!CurrentFlags.IsMovementRequested && !CurrentFlags.IsFollowing) return;
        if (CurrentFlags.IsOnDate) return;
        if (CurrentFlags.IsJealousy) return;  

        bool isZh = IsChineseLanguage;

        if (CurrentFlags.IsFollowing && !CurrentFlags.IsMovementRequested)
        {
            prompt.AppendLine("<movement_instruction mode=\"following\">");
            prompt.AppendLine(isZh
                ? "你此刻正跟在农夫身边。保持语气轻松随和且专注与对方的陪伴。"
                : "You are currently following the farmer. Maintain a relaxed, attentive, and companionable tone.");
            prompt.AppendLine("</movement_instruction>\n");
            return;
        }

        if (CurrentFlags.IsGotoRequested)
        {
            string assetList = EnvironmentScanner.FormatAssetsForGotoPrompt(
                Character.StardewNpc, radiusTiles: 15);

            prompt.AppendLine("<movement_instruction mode=\"navigation\">");
            prompt.AppendLine(isZh ? "玩家正要求你走去特定地点或拿取物品。" : "The player is asking you to walk to a specific location or interact with an object.");
            prompt.AppendLine(assetList);
            prompt.AppendLine(isZh ? $"玩家请求: \"{CurrentFlags.GotoIntentText}\"" : $"Player Request: \"{CurrentFlags.GotoIntentText}\"");
            prompt.AppendLine(isZh
                ? "- 匹配成功: 自然文字回应，并在台词最末尾附带 [ACTION:GOTO:x,y]（使用列表中精准坐标）。"
                : "- MATCH FOUND: Respond naturally AND append [ACTION:GOTO:x,y] at the end using exact coordinates.");
            prompt.AppendLine(isZh
                ? "- 未能匹配: 自然说明找不到该物品，无需输出 ACTION 标签。"
                : "- NO MATCH: Explain naturally that you cannot find it without outputting ACTION tags.");
            prompt.AppendLine("</movement_instruction>\n");
            return;
        }

        if (CurrentFlags.IsPathBlocked)
        {
            prompt.AppendLine("<movement_instruction mode=\"blocked\">");
            prompt.AppendLine(isZh
                ? $"向 {CurrentFlags.BlockDirection.ToString().ToLower()} 移动的路径被障碍物阻挡。自然指出前面的阻碍并说明无法过去。"
                : $"Path to the {CurrentFlags.BlockDirection.ToString().ToLower()} is BLOCKED. Acknowledge the barrier naturally and explain why you cannot move.");
            prompt.AppendLine("</movement_instruction>\n");
            return;
        }

        if (CurrentFlags.IsAlreadyAdjacent)
        {
            prompt.AppendLine("<movement_instruction mode=\"adjacent\">");
            prompt.AppendLine(isZh
                ? "你此刻已经站在农夫正前方。请符合人设地打趣对方重复要求的行为。"
                : "You are ALREADY standing face-to-face with the farmer. Playfully tease or address this in your response.");
            prompt.AppendLine("</movement_instruction>\n");
            return;
        }

        prompt.AppendLine("<movement_instruction mode=\"move_request\">");
        prompt.AppendLine(isZh
            ? "玩家要求你移动或跟上对方，前方路径畅通。"
            : "The player asked you to move or follow them. The path is CLEAR.");

        if (ModEntry.Config?.UseNativeToolCalling == true)
        {
            prompt.AppendLine(isZh
                ? "- 同时执行 `trigger_physical_action` 工具调用（FOLLOW / STEP:FORWARD / STEP:LEFT 等）。"
                : "- Invoke `trigger_physical_action` tool simultaneously (FOLLOW / STEP:FORWARD / STEP:LEFT, etc.).");
            prompt.AppendLine(isZh
                ? "- 始终保持【台词 + 工具调用】的双重同时输出。"
                : "- ALWAYS provide natural spoken text in your response alongside tool execution.");
        }
        else
        {
            prompt.AppendLine(isZh
                ? "- 若同意: 自然说明并将对应动作标签（如 [ACTION:FOLLOW] 或 [ACTION:STEP:FORWARD]）置于台词最末尾。"
                : "- IF ACCEPTING: Speak naturally AND place the action tag (e.g., [ACTION:FOLLOW] or [ACTION:STEP:FORWARD]) at the absolute end.");
            prompt.AppendLine(isZh
                ? "- 若拒绝: 自然说明理由，无需输出 ACTION 标签。"
                : "- IF DECLINING: Refuse naturally without outputting action tags.");
        }
        prompt.AppendLine("</movement_instruction>\n");
    }

    private void GetSpecialRelationshipStatus(StringBuilder prompt, Friendship friendship)
    {
        if (friendship == null) return;

        if (friendship.IsDating())
        {
            var relationshipPublic = Context.Inlaw == null ? Util.GetString(Character, "specialRelationshipDatingPublic") : Util.GetString(Character, "specialRelationshipDatingDiscrete");
            var relationshipWord = RelationshipWord(Context.MaleFarmer, npcIsMale);
            prompt.AppendLine(Util.GetString(Character, "specialRelationshipDating", new { Name = Name, relationshipPublic = relationshipPublic, relationshipWord = relationshipWord }));
        }
        if (friendship.IsEngaged())
        {
            var daysToWedding = friendship.CountdownToWedding;
            prompt.AppendLine(Util.GetString(Character, "specialRelationshipEngaged", new { Name = Name, daysToWedding = daysToWedding }));
        }
        if (friendship.IsDivorced())
        {
            prompt.AppendLine(Util.GetString(Character, "specialRelationshipDivorced", new { Name = Name }));
        }
        if (friendship.ProposalRejected)
        {
            prompt.AppendLine(Util.GetString(Character, "specialRelationshipProposalRejected", new { Name = Name }));
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
                var otherSpousesList = multipleOthers ? $"{Util.GetString(Character, "spousesNOtherPeople", new { nSpouses = nSpouses })} {spouseList}" : spouses.First();
                var otherSpousesReference = multipleOthers ? Util.GetString(Character, "spousesAllTheOthers") : spouses.First();
                prompt.AppendLine(Util.GetString(Character, "spousesMarriedToOthers", new { Name = Name, otherSpousesList = otherSpousesList, otherSpousesReference = otherSpousesReference }));
            }
            else
            {
                if (multipleOthers)
                {
                    prompt.AppendLine(Util.GetString(Character, "spousesMarriedToMany", new { nSpouses = nSpouses, spouseList = spouseList, Name = Name }));
                }
                else
                {
                    prompt.AppendLine(Util.GetString(Character, "spousesMarriedToOne", new { spouseList = spouseList, Name = Name }));
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
            var roommateList = multipleOthers ? $"{Util.GetString(Character, "spousesNOtherPeople", new { nSpouses = roommates.Count() })} {string.Join(", ", roommates)}" : roommates.First();
            var roommateReference = multipleOthers ? Util.GetString(Character, "spouseRoommatesAllTheOthers") : roommates.First();

            if (talkingToRoommate)
            {
                prompt.AppendLine(Util.GetString(Character, "spouseRoommatesWithOthers", new { Name = Name, roommateList = roommateList, roommateReference = roommateReference }));
            }
            else
            {
                if (multipleOthers)
                {
                    prompt.AppendLine(Util.GetString(Character, "spouseRoommateWithMany", new { roommateList = roommateList }));
                }
                else
                {
                    prompt.AppendLine(Util.GetString(Character, "spouseRoommateWithOne", new { roommateList = roommateList }));
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
            var engagedFirst = engaged.First(x => x.Key != Name);
            var engagedTo = Game1.characterData[engagedFirst.Key].DisplayName;
            var weddingDays = engagedFirst.Value.Value.CountdownToWedding;
            prompt.AppendLine(Util.GetString(Character, "spouseEngaged", new { engagedTo = engagedTo, weddingDays = weddingDays }));
        }

        var total = spouses.Count() + engaged.Count();
        if (total > 1 && !talkingToSpouse && !talkingToRoommate)
        {
            prompt.AppendLine(Util.GetString(Character, "spousePoly", new { Name = Name }));
        }
        else if (total >= 1 && (talkingToSpouse || talkingToRoommate))
        {
            prompt.AppendLine(Util.GetString(Character, "spousePolyView", new { Name = Name }));
        }
    }

    private void GetNonSpouseFriendshipLevel(StringBuilder prompt)
    {
        int hearts = Context.Hearts ?? 0;
        bool isSingle = npcData.CanBeRomanced;
        bool isChild = npcData.Age == NpcAge.Child;

        // 检查是否已送花确认恋爱关系（原版星露谷未送花哪怕8心也只是挚友）
        bool isDating = false;
        if (Game1.player?.friendshipData.TryGetValue(Character.Name, out var fs) == true)
        {
            isDating = fs.IsDating();
        }

        string line = IsChineseLanguage
            ? GetFriendshipTextZh(hearts, isSingle, isChild, isDating)
            : GetFriendshipTextEn(hearts, isSingle, isChild, isDating);

        if (!string.IsNullOrWhiteSpace(line))
            prompt.AppendLine(line);
    }

    private string GetFriendshipTextZh(int hearts, bool isSingle, bool isChild, bool isDating)
    {
        string note = "（社交熟悉度基准；你对农夫的实际好恶与信任度请优先结合【长期印象】与【记忆】综合表现）";

        if (hearts < 0)
            return $"- 社交关系：初次正式碰面（-1心），彼此完全陌生。{note}";
        if (hearts < 2)
            return $"- 社交关系：点头之交（{hearts}心），彼此还很不了解，保持基本的社交防备与距离。{note}";
        if (hearts < 4)
            return $"- 社交关系：熟悉起来的邻居（{hearts}心），日常见面能聊上几句，但尚未深交。{note}";
        if (hearts < 6)
            return $"- 社交关系：熟识的朋友（{hearts}心），互相了解不少生活习惯。{note}";
        if (hearts < 8)
            return $"- 社交关系：彼此非常熟稔的至交（{hearts}心），相处时毫无拘束。{note}";

        // 8心及以上
        if (isChild)
            return $"- 社交关系：经常陪伴的熟人（{hearts}心），互动带有孩子气的直率。{note}";

        if (isSingle)
        {
            if (isDating)
                return $"- 社交关系：恋人阶段（{hearts}心），属于公开约会关系。{note}";

            return $"- 社交关系：极度熟识的至交好友（{hearts}心），彼此在小镇里交往甚密。{note}";
        }

        return $"- 社交关系：常年相识的深厚故交（{hearts}心），熟络度极高。{note}";
    }

    private string GetFriendshipTextEn(int hearts, bool isSingle, bool isChild, bool isDating)
    {
        string note = "(Base social familiarity; your actual emotional fondness/trust must be guided by your [IMPRESSIONS OF THE FARMER] and memories)";

        if (hearts < 0)
            return $"- Social Standing: First meeting (-1 hearts). Complete strangers. {note}";
        if (hearts < 2)
            return $"- Social Standing: Distant acquaintances ({hearts} hearts). Basic social boundaries apply. {note}";
        if (hearts < 4)
            return $"- Social Standing: Familiar neighbors ({hearts} hearts). Casual, surface-level neighborly rapport. {note}";
        if (hearts < 6)
            return $"- Social Standing: Well-acquainted friends ({hearts} hearts). Familiar with each other's routines. {note}";
        if (hearts < 8)
            return $"- Social Standing: Highly familiar companions ({hearts} hearts). Zero conversational formality. {note}";

        if (isChild)
            return $"- Social Standing: Very familiar presence ({hearts} hearts). Childlike openness. {note}";

        if (isSingle)
        {
            if (isDating)
                return $"- Social Standing: Dating relationship ({hearts} hearts). Public romantic involvement. {note}";

            return $"- Social Standing: Closely bound best friends ({hearts} hearts). High social proximity. {note}";
        }

        return $"- Social Standing: Deeply established long-term bond ({hearts} hearts). Complete conversational ease. {note}";
    }


    private void GetSpouseAction(StringBuilder prompt)
    {
        if (Context.Accept != null) return;
        if (Context.SpouseAct == null) return;
        prompt.AppendLine(Context.SpouseAct switch
        {
            SpouseAction.funLeave => Util.GetString(Character, "spouseActionFunLeave", new { Name = Name }),
            SpouseAction.jobLeave => Util.GetString(Character, "spouseActionJobLeave", new { Name = Name }),
            SpouseAction.patio => Util.GetString(Character, "spouseActionPatio", new { Name = Name }),
            SpouseAction.funReturn => Util.GetString(Character, "spouseActionFunReturn", new { Name = Name }),
            SpouseAction.jobReturn => Util.GetString(Character, "spouseActionJobReturn", new { Name = Name }),
            SpouseAction.spouseRoom => Util.GetString(Character, "spouseActionSpouseRoom", new { Name = Name }),
            _ => $""
        });
    }

    private void GetGift(StringBuilder prompt)
    {
        if (Context.Accept != null)
        {
            var giftName = Context.Accept.DisplayName;
            prompt.AppendLine(Util.GetString(Character, "giftIntro", new { Name = Name, giftName = giftName }));

            switch (Context.GiftTaste)
            {
                case 0:
                    prompt.AppendLine(Util.GetString(Character, "giftLoved", new { Name = Name }));
                    break;
                case 2:
                    prompt.AppendLine(Util.GetString(Character, "giftLiked", new { Name = Name }));
                    break;
                case 4:
                    prompt.AppendLine(Util.GetString(Character, "giftDislike", new { Name = Name }));
                    break;
                case 6:
                    prompt.AppendLine(Util.GetString(Character, "giftHate", new { Name = Name }));
                    break;
                default:
                    prompt.AppendLine(Util.GetString(Character, "giftNeutral", new { Name = Name }));
                    break;
            }

            prompt.AppendLine(Util.GetString(Character, "giftMustIncludeReaction", new { Name = Name }));

            if (Context.Birthday)
            {
                prompt.AppendLine(Util.GetString(Character, "giftBirthday", new { Name = Name }));
            }

            prompt.AppendLine(Util.GetString(Character, "giftOutro"));
        }
        else if (!string.IsNullOrEmpty(giveGift))
        {
            string giftName = giveGift;
            if (Game1.objectData.ContainsKey(giveGift))
            {
                giftName = Game1.objectData[giveGift].DisplayName;
                giftName = LoadLocalised(giftName);
            }
            prompt.AppendLine(Util.GetString(Character, "giftGiving", new { Name = Name, GiftName = giftName }));
        }
    }

    private void GetSpecialDatesAndBirthday(StringBuilder prompt)
    {
        if (Context.DayOfSeason == null || Context.Season == null) return;

        prompt.AppendLine((Context.Season, Context.DayOfSeason) switch
        {
            (Season.Spring, 1) => Util.GetString(Character, "specialDatesSpring1"),
            (Season.Spring, 12) => Util.GetString(Character, "specialDatesSpring12"),
            (Season.Spring, 23) => Util.GetString(Character, "specialDatesSpring23"),
            (Season.Summer, 1) => Util.GetString(Character, "specialDatesSummer1"),
            (Season.Summer, 10) => Util.GetString(Character, "specialDatesSummer10"),
            (Season.Summer, 27) => Util.GetString(Character, "specialDatesSummer27"),
            (Season.Summer, 28) => Util.GetString(Character, "specialDatesSummer28"),
            (Season.Fall, 1) => Util.GetString(Character, "specialDatesFall1"),
            (Season.Fall, 15) => Util.GetString(Character, "specialDatesFall15"),
            (Season.Fall, 26) => Util.GetString(Character, "specialDatesFall26"),
            (Season.Winter, 1) => Util.GetString(Character, "specialDatesWInter1"),
            (Season.Winter, 7) => Util.GetString(Character, "specialDatesWinter7"),
            (Season.Winter, 24) => Util.GetString(Character, "specialDatesWinter24"),
            (Season.Winter, 28) => Util.GetString(Character, "specialDatesWinter28"),
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
            prompt.AppendLine(Util.GetString(Character, "specialDatesBirthday", new { Name = Name }));
        }
    }

    private void GetRecentEvents(StringBuilder prompt)
    {
        if (allPreviousActivities == null) return;

        var eventSection = new StringBuilder();

        foreach (var activity in allPreviousActivities.Where(x => x.Value < 7))
        {
            var theLine = activity.Key switch
            {
                "babyBoy" => Util.GetString(Character, "recentEventsBabyBoy"),
                "babyGirl" => Util.GetString(Character, "recentEventsBabyGirl"),
                "wedding" => Util.GetString(Character, "recentEventsMarried"),
                "Characters_MovieInvite_Invited" => Util.GetString(Character, "recentEventsMovieInvited", new { Name = Name }),
                "DumpsterDiveComment" => Util.GetString(Character, "recentEventsDumpsterDive", new { Name = Name }),
                _ => $""
            };

            if (!string.IsNullOrWhiteSpace(theLine))
            {
                eventSection.AppendLine(theLine);
            }
        }

        if (eventSection.Length > 0)
        {
            prompt.AppendLine($"## {Util.GetString(Character, "recentEventsHeading")}");
            prompt.AppendLine(Util.GetString(Character, "recentEventsIntro"));
            prompt.AppendLine(eventSection.ToString());
        }
    }

    private void GetMarriageFeelings(StringBuilder prompt)
    {
        if (!Game1.getPlayerOrEventFarmer().friendshipData.TryGetValue(Character.Name, out var marriageFriendship))
            return;

        var IsRoommate = marriageFriendship.IsRoommate();
        var marriageOrRoommate = IsRoommate ? Util.GetString(Character, "generalBeingRoommates") : Util.GetString(Character, "generalTheMarriage");

        switch (Context.Hearts)
        {
            case > 12:
                prompt.AppendLine(Util.GetString(Character, "marriageSentimentGood", new { Name = Name, marriageOrRoommate = marriageOrRoommate }));
                break;
            case < 10:
                prompt.AppendLine(Util.GetString(Character, "marriageSentimentBad", new { Name = Name, marriageOrRoommate = marriageOrRoommate }));
                break;
            default:
                prompt.AppendLine(Util.GetString(Character, "marriageSentimentNeutral", new { Name = Name, marriageOrRoommate = marriageOrRoommate }));
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

                if (companionEffect?.Companion is StardewValley.Companions.HungryFrogCompanion)
                {
                    prompt.AppendLine(Util.GetString(Character, "trinketsCompanionFrog", new { Name }));
                }
                else if (companionEffect?.Companion is StardewValley.Companions.FlyingCompanion)
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

        if (friendship != null && friendship.DaysUntilBirthing > 0)
        {
            var daysUntilBirth = friendship.DaysUntilBirthing;
            prompt.AppendLine(Util.GetString(Character, "childrenPregnant", new { Name = Name, daysUntilBirth = daysUntilBirth }));
        }
    }

    private void GetEventHistory(StringBuilder prompt)
    {
        EventHistoryHelper.BuildEventHistory(prompt, Character, Context);
    }

    private void GetGameState(StringBuilder prompt)
    {
        if (Game1.year == 1)
        {
            prompt.AppendLine(Util.GetString(Character, "gameStateKentNo"));
        }
        else
        {
            prompt.AppendLine(Util.GetString(Character, "gameStateKentYes"));
        }
    }

    private string GetCommand()
    {
        var commandPrompt = new StringBuilder();
        bool isZh = IsChineseLanguage;

        commandPrompt.AppendLine($"## {Util.GetString(Character, "commandHeading")}");
        commandPrompt.AppendLine(Util.GetString(Character, "commandIntro", new { Name = Name }));

        DefaultOrOverride("ReplaceSchedule", p =>
        {
            if (!string.IsNullOrWhiteSpace(Context.ScheduleLine) && !Context.ChatHistory.Any() && !Character.SpokeJustNow())
            {
                p.AppendLine();
                p.AppendLine(Util.GetString(Character, "commandReplaceSchedule", new { ScheduleLine = Context.ScheduleLine }));
            }
        }, commandPrompt);

        commandPrompt.AppendLine();
        commandPrompt.AppendLine(isZh ? "### [系统行为指令：肢体动作与表情]" : "### [SYSTEM TRIGGERS: EMOTES & PHYSICAL ACTIONS]");

        if (ModEntry.Config.UseNativeToolCalling)
        {
            commandPrompt.AppendLine(isZh ? "- 表情气泡: 如有需要可使用 [ACTION:EMOTE:ANGRY]、[ACTION:EMOTE:HEART]、[ACTION:EMOTE:BLUSH] 等标签。" : "- Emote bubbles: Use text tags like [ACTION:EMOTE:ANGRY], [ACTION:EMOTE:HEART], [ACTION:EMOTE:BLUSH] if appropriate.");
            commandPrompt.AppendLine(isZh 
                ? "- 肢体位移与跟随: 仅当玩家明确要求方向移动或跟随时，才调用 `trigger_physical_action` 工具。亲吻、拥抱等亲密请求请用台词和表情回应，严禁调用物理移动工具。" 
                : "- Physical Movements & Following: Only invoke `trigger_physical_action` tool when the player explicitly requests directional movement or following. For intimate requests (kisses, hugs), respond with dialogue and emotes only — NEVER invoke physical movement tools.");        }
        else
        {
            commandPrompt.AppendLine(isZh ? "- 表情气泡标签: [ACTION:EMOTE:ANGRY], [ACTION:EMOTE:SAD], [ACTION:EMOTE:HEART], [ACTION:EMOTE:HAPPY], [ACTION:EMOTE:BLUSH], [ACTION:EMOTE:SURPRISE]" : "- Emote tags: [ACTION:EMOTE:ANGRY], [ACTION:EMOTE:SAD], [ACTION:EMOTE:HEART], [ACTION:EMOTE:HAPPY], [ACTION:EMOTE:BLUSH], [ACTION:EMOTE:SURPRISE]");
            commandPrompt.AppendLine(isZh ? "- 转向标签: [ACTION:FACE:FARMER] (面向玩家), [ACTION:FACE:UP] / [DOWN] / [LEFT] / [RIGHT]" : "- Turn tags: [ACTION:FACE:FARMER] (look at player), [ACTION:FACE:UP] / [DOWN] / [LEFT] / [RIGHT]");
            commandPrompt.AppendLine(isZh ? "- 位移与跟随标签: [ACTION:STEP:FORWARD], [ACTION:STEP:BACKWARD], [ACTION:STEP:LEFT], [ACTION:STEP:RIGHT], [ACTION:FOLLOW]" : "- Movement tags: [ACTION:STEP:FORWARD], [ACTION:STEP:BACKWARD], [ACTION:STEP:LEFT], [ACTION:STEP:RIGHT], [ACTION:FOLLOW]");
            commandPrompt.AppendLine();
            commandPrompt.AppendLine(isZh ? "格式规则 — 动作标签置于台词的最末尾：" : "OUTPUT FORMAT — Action tags MUST be placed at the absolute end of spoken line:");
            commandPrompt.AppendLine("  EXAMPLE (follow):  'Fine, I'll come with you! [ACTION:FOLLOW]'");
            commandPrompt.AppendLine("  EXAMPLE (move):    'Okay, stepping back. [ACTION:STEP:BACKWARD]'");
            commandPrompt.AppendLine("  EXAMPLE (emote):   'Hehe, of course! [ACTION:EMOTE:HAPPY]'");
        }

        if (ModEntry.Config.UseNativeToolCalling)
        {
            commandPrompt.AppendLine();
            commandPrompt.AppendLine("<dual_output_rule>");
            commandPrompt.AppendLine(isZh 
                ? "当调用工具时，在文本响应中保持自然口头台词，始终维持【台词 + 工具调用】的双重同时输出。" 
                : "When invoking any tool, ALWAYS provide natural spoken dialogue in text response simultaneously.");
            commandPrompt.AppendLine("</dual_output_rule>");
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
        bool isZh = IsChineseLanguage;

        instructions.AppendLine($"## {Util.GetString(Character, "instructionsHeading", new { Language = TargetLanguageName })}");
        instructions.AppendLine(Util.GetString(Character, "instructionsIntro", new { Name = Name }));

        if (dialogueSample != null && dialogueSample.Any())
        {
            instructions.AppendLine(Util.GetString(Character, "instructionsSampleDialogue", new { Name = Name }));
        }

        instructions.AppendLine(Util.GetString(Character, "instructionsFarmersName"));
        instructions.AppendLine(Util.GetString(Character, "instructionsBreaks"));
        instructions.AppendLine(Util.GetString(Character, "instructionsSingleLine"));
        instructions.AppendLine(Util.GetString(Character, "instructionsResponses", new { Name = Name }));

        instructions.AppendLine(isZh
            ? "- 【核心规则】严禁替农夫（玩家）发言或臆想农夫的回应。仅描写你自己的动作与言语，单次对话严格保持在 1~3 句话以内，等待农夫作答。"
            : "- [CRITICAL] Never impersonate the farmer or invent dialogue on the farmer's behalf. Only generate your own character's speech and actions.");

        instructions.AppendLine(isZh
            ? "- 若本次对话结束后你的情绪明显转变（如变得好奇/生气/高兴），在台词最末尾附加 [MOOD:curious] / [MOOD:annoyed] / [MOOD:happy] 等标签。"
            : "- If your emotional tone has clearly shifted after this exchange (e.g. curious/annoyed/happy), append [MOOD:curious] / [MOOD:annoyed] / [MOOD:happy] at the absolute end.");

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

        if (!string.IsNullOrWhiteSpace(Llm.Instance.ExtraInstructions))
        {
            instructions.AppendLine(Llm.Instance.ExtraInstructions);
        }

        return instructions.ToString();
    }

    private string GetResponseStart()
    {
        string start = I18n.ResponseStart();
        return !string.IsNullOrWhiteSpace(start) ? start : "[In-Character Dialogue]:";
    }

    private string RelationshipWord(bool maleFarmer, bool npcIsMale)
    {
        return maleFarmer ? (npcIsMale ? Util.GetString(Character, "generalGayMale") : Util.GetString(Character, "generalHeterosexual")) : (npcIsMale ? Util.GetString(Character, "generalHeterosexual") : Util.GetString(Character, "generalLesbian"));
    }

    private static string LoadLocalised(string thisName)
    {
        if (string.IsNullOrWhiteSpace(thisName)) return string.Empty;

        if (thisName.StartsWith("[LocalizedText ", StringComparison.InvariantCultureIgnoreCase)
            && thisName.Length > 15)
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