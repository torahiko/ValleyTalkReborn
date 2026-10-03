// Prompts.cs
// ═══════════════════════════════════════════════════════════════════════════
// PROMPT ASSEMBLY ARCHITECTURE
// ═══════════════════════════════════════════════════════════════════════════

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Newtonsoft.Json;
using ValleytalkReborn.Dialogue.Coordination;
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

    private readonly List<string> _injectedPrivateThoughts = new List<string>();
    public IReadOnlyList<string> InjectedPrivateThoughts => _injectedPrivateThoughts;

    private readonly HashSet<string> _emittedBlockKeys = new HashSet<string>(StringComparer.Ordinal);

    /// <summary>
    /// 统一语言解析：Config.LanguageOverride 优先（"zh" 前缀判定，忽略大小写），
    /// 留空/空白回退游戏语言代码 LocalizedContentManager.CurrentLanguageCode。
    /// </summary>
    internal static bool ResolveIsChinese()
    {
        string languageOverride = ModEntry.Config?.LanguageOverride?.Trim();
        if (!string.IsNullOrEmpty(languageOverride))
            return languageOverride.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
        return LocalizedContentManager.CurrentLanguageCode == LocalizedContentManager.LanguageCode.zh;
    }

    private bool IsChineseLanguage => ResolveIsChinese();

    /// <summary>
    /// VT-CONTEXT-01：运行时提示词中的玩家身份名。实例入口，供本类的装配方法复用。
    /// 每次提示词装配只解析一次（hot path 约束），不在历史循环内重复访问 Game1.player。
    /// </summary>
    private string GetPromptPlayerName() => ResolvePromptPlayerName(Character);

    /// <summary>
    /// 玩家身份名解析：优先 Game1.player.Name；为 null/空/空白时回退
    /// Util.GetString(character, "generalFarmerLabel") 的本地化标签。
    /// PromptsBlocks 的静态装配路径（Tier 2a）复用同一实现，保证 role-based 与
    /// 非 role-based 路径的玩家说话者标签一致。
    /// 玩家名与回退标签同时为空白属 BUG：记录 Error 并抛出，绝不静默产出空标签。
    /// </summary>
    internal static string ResolvePromptPlayerName(Character character)
    {
        string playerName = Game1.player?.Name;
        if (!string.IsNullOrWhiteSpace(playerName))
            return playerName.Trim();

        string fallbackLabel = Util.GetString(character, "generalFarmerLabel");
        if (string.IsNullOrWhiteSpace(fallbackLabel))
        {
            ModEntry.SMonitor?.Log(
                "[Prompts] Player identity unavailable: Game1.player.Name is blank and the generalFarmerLabel fallback resolved to blank; refusing to emit an empty speaker label.",
                StardewModdingAPI.LogLevel.Error);
            throw new InvalidOperationException(
                "Prompt player identity unavailable: Game1.player.Name is blank and the generalFarmerLabel fallback resolved to blank.");
        }
        return fallbackLabel;
    }

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
        var builder = GameSummaryBuilder.Instance;
        var regionMap = builder.GetLocationRegions();
        var ctx = BuildContext.FromGameState(CurrentFlags, Context.Location, regionMap);
        return builder.Build(ctx);
    }
    
    private string _systemPrompt;
    public string SystemPrompt { get => _systemPrompt ??= GetSystemPrompt(); internal set => _systemPrompt = value; }

    private string _gameConstantContext;
    public string GameConstantContext { get => _gameConstantContext ??= GetGameConstantContext(); internal set => _gameConstantContext = value; }

    private string _npcConstantContext;
    public string NpcConstantContext { get => _npcConstantContext ??= GetNpcConstantContext(); internal set => _npcConstantContext = value; }

    private string _corePrompt;
    public string CorePrompt { get => _corePrompt; internal set => _corePrompt = value; }

    private InjectionPlan _corePlan;
    private string _sessionContinuitySegment;
    private string _currentConversationSegment;

    // VT3-D Δ2: 可变访问器——director 经此将 BuildPreoccupation/BuildPendingTopic 的 side-effect 写回
    // 同一 List 实例，使 ProcessLines 反泄漏过滤（LlmDialogueService.cs:580-589）仍可达。禁止防御性拷贝。
    internal List<string> InjectedPrivateThoughtsMutable => _injectedPrivateThoughts;

    private string _command;
    public string Command { get => _command ??= GetCommand(); internal set => _command = value; }

    private string _responseStart;
    public string ResponseStart { get => _responseStart ??= GetResponseStart(); internal set => _responseStart = value; }

    private string _instructions;
    public string Instructions { get => _instructions ??= GetInstructions(InstructionsBranch.Normal); internal set => _instructions = value; }

    // ── 命名边界视图（PROMPT-ARCH-01）：内存态只读组合，按请求重算；不改变 Provider 载荷 ──

    /// <summary>静态指令边界：Instructions + Command。不含 CorePrompt 与对话流。</summary>
    public string StaticInstructionContext => JoinPromptSegments(Instructions, Command);

    /// <summary>
    /// 动态上下文边界：GameConstantContext + NpcConstantContext + Tier 1 + Tier 2b（现有装配顺序）。
    /// 需先经 AssembleCore 装配 InjectionPlan；缺失时按契约报错而非返回空上下文。
    /// </summary>
    public string DynamicContext
    {
        get
        {
            if (_corePlan == null)
            {
                ModEntry.SMonitor?.Log(
                    "[Prompts] DynamicContext accessed before AssembleCore; InjectionPlan unavailable.",
                    StardewModdingAPI.LogLevel.Error);
                throw new InvalidOperationException(
                    "DynamicContext requires AssembleCore to have assembled an InjectionPlan for this Prompts instance.");
            }
            return JoinPromptSegments(
                GameConstantContext,
                NpcConstantContext,
                AssembleTier1Segment(_corePlan),
                AssembleTier2bSegment(_corePlan));
        }
    }

    /// <summary>对话流边界：SessionContinuity + CurrentConversation + 触发后缀。不含指令、常量上下文与 Tier 1/2b。</summary>
    public string ConversationStream
    {
        get
        {
            return JoinPromptSegments(_sessionContinuitySegment, _currentConversationSegment, BuildResponseTriggerSuffix(GetPromptPlayerName()));
        }
    }

    /// <summary>触发后缀单一来源（VT-RCA-02-R1）。两处载荷（role-based 最终 user 消息 / legacy ConversationStream 末段）共用，禁止再内联副本。</summary>
    internal string BuildResponseTriggerSuffix(string promptPlayerName)
    {
        return IsChineseLanguage
            ? $"<response_trigger>\n[IDENTITY] 玩家姓名为 {promptPlayerName}；上下文中标为该姓名的玩家发言，以及称作农夫/Farmer 的玩家身份，都是同一个人。\n[RESPONSE_TRIGGER] 若农夫刚刚说了话：第一句直接回应他的最新发言，顺着已承接的话题向前推进。若农夫没有说话（现场触发或沉默）：以你当下的动作、现场体感与实际时段自然开场问候；下一步行动以双方已有共识为依据，不凭空编造复杂协同计划。小镇传闻仅作为可选背景闲聊。两种情形都只输出一轮，完成选项区后立即交回对话回合。\n[SCENE_CONSISTENCY] 若历史对白中的场景假设与当前场景事实冲突，以当前事实为接话起点。\n</response_trigger>"
            : $"<response_trigger>\n[IDENTITY] The player's name is {promptPlayerName}; player lines labelled with that name and the player identity called 农夫/Farmer refer to the same person.\n[RESPONSE_TRIGGER] If the farmer just spoke: your first line MUST directly reply to their latest words and carry forward the accepted topic. If the farmer said nothing (ambient trigger or silence): open naturally from your current activity, scene sensations, time of day, and familiar greetings; next actions must be grounded in existing mutual consensus rather than fabricating elaborate cooperative missions. Town rumors serve as optional background chatter only. In both cases output exactly one turn and hand the turn back right after the option block.\n[SCENE_CONSISTENCY] If scene assumptions in the dialogue history conflict with the current scene facts, pick up the conversation from the current facts.\n</response_trigger>";
    }

    /// <summary>
    /// 组装 OpenAI 兼容的 role-based 消息序列（PROMPT-ARCH-03）。
    /// 顺序：动态上下文（user）→ 会话衔接（标题说明 + 各轮次 user/assistant）→
    /// 最近对话历史窗口（user/assistant）→ 最终触发后缀（user）。
    /// DynamicContext 经 PromptDeduplicator 去重（复用 PROMPT-ARCH-02A 策略），
    /// 衔接与历史窗口复用现有筛选/窗口语义。
    /// </summary>
    internal IReadOnlyList<LlmChatMessage> BuildRuntimeChatMessages()
    {
        var messages = new List<LlmChatMessage>();

        // VT-CONTEXT-01：本次装配的玩家身份名（只解析一次，后续循环复用）
        string promptPlayerName = GetPromptPlayerName();

        // 1. 去重后的动态上下文作为 user 消息
        string dynamicContext = PromptDeduplicator.DeduplicateDynamicSegment(this.SystemPrompt, this.DynamicContext);
        if (!string.IsNullOrWhiteSpace(dynamicContext))
            messages.Add(new LlmChatMessage("user", dynamicContext));

        // 2. 会话衔接（标题说明 + 各轮次）
        var continuityTurns = PromptsBlocks.GetContinuityTurns(this.Character, this.Context);
        if (continuityTurns.Count > 0)
        {
            bool isZh = IsChineseLanguage;
            var session = SessionCache.Instance.GetOrCreate(this.Character.Name);
            var ctxSb = new StringBuilder();
            ctxSb.AppendLine(isZh ? "### [早先会话衔接参考]" : "### [EARLIER SESSION CONTINUITY]");
            ctxSb.AppendLine(isZh
                ? "[REFERENCE_ONLY] 当日早先对话记录，仅用于保持人物记忆与逻辑连贯："
                : "[REFERENCE_ONLY] Earlier conversation turns today, provided solely for conversational consistency:");
            if (!string.IsNullOrEmpty(session.EmotionalTone))
            {
                ctxSb.AppendLine(isZh
                    ? $"[MOOD_CONSISTENCY] 前置情绪基调: {session.EmotionalTone}。确保语气演变符合心理过渡规律。"
                    : $"[MOOD_CONSISTENCY] Prior emotional tone: {session.EmotionalTone}. Ensure tonal transition remains psychologically coherent.");
            }
            messages.Add(new LlmChatMessage("user", ctxSb.ToString().TrimEnd()));

            foreach (var turn in continuityTurns)
            {
                // 保留 FuzzyTime 前缀（与 BuildSessionContinuity 一致的格式语义）。
                // VT-CONTEXT-01：玩家轮次显式标注玩家名，NPC（assistant）轮次内容不变。
                string timePrefix = string.IsNullOrEmpty(turn.FuzzyTime) ? "" : $"[{turn.FuzzyTime}] ";
                string content = turn.IsPlayerLine
                    ? $"{timePrefix}{promptPlayerName}: {turn.Text}"
                    : timePrefix + turn.Text;
                messages.Add(new LlmChatMessage(turn.IsPlayerLine ? "user" : "assistant", content));
            }
        }

        // 3. 最近对话历史窗口（复用现有窗口规则）
        bool shortCtxAllowed = CurrentFlags?.IncludeShortTermContext != false;
        int configured = Math.Clamp(ModEntry.Config?.PromptHistoryWindow ?? 6, 1, 20);
        int window = Math.Clamp(shortCtxAllowed ? configured : 1, 1, Math.Max(1, this.Context.ChatHistory.Count));
        var visible = this.Context.ChatHistory.Count > window
            ? this.Context.ChatHistory.GetRange(this.Context.ChatHistory.Count - window, window)
            : this.Context.ChatHistory;
        foreach (var elem in visible)
        {
            // VT-CONTEXT-01：玩家轮次以“{玩家名}: ”标注说话者；NPC（assistant）轮次内容不变。
            string content = elem.IsPlayerLine ? $"{promptPlayerName}: {elem.Text}" : elem.Text;
            messages.Add(new LlmChatMessage(elem.IsPlayerLine ? "user" : "assistant", content));
        }

        // 4. 最终触发后缀作为独立的 user 消息
        // VT-CONTEXT-01：附带玩家身份说明——玩家名与“农夫/Farmer”是同一人。
        string triggerSuffix = BuildResponseTriggerSuffix(promptPlayerName);
        messages.Add(new LlmChatMessage("user", triggerSuffix));

        return messages;
    }

    /// <summary>拼接非空 Prompt 段：忽略 null/空段，非空段间恰好两个换行符，全空返回 string.Empty。</summary>
    internal static string JoinPromptSegments(params string[] segments)
    {
        if (segments == null || segments.Length == 0)
            return string.Empty;
        var nonEmpty = new List<string>(segments.Length);
        foreach (var segment in segments)
        {
            if (!string.IsNullOrEmpty(segment))
                nonEmpty.Add(segment);
        }
        return nonEmpty.Count == 0 ? string.Empty : string.Join("\n\n", nonEmpty);
    }

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
    IDialogueValue exactLine;
    private string giveGift;
    private SerializableDictionary<string, int> allPreviousActivities;

    private void InitializeInstanceFields(DialogueContext context, Character character)
    {
        npcData = character.StardewNpc.GetData();
        npcIsMale = npcData.Gender == StardewValley.Gender.Male;
        Context = context;
        Character = character;
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
        return PromptsBlocks.SelectGiftGiven(Character);
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

        bool includeFarmDetails = CurrentFlags.IncludeFarmDetails;
        bool isHouseholdMember = includeFarmDetails && ResolveCurrentNpcHouseholdMembership();

        // VT-FARM-OBSERVE-05: 路由开启且已通过世界就绪/玩家身份校验后，各读取一次
        // NPC 与当前玩家的现场位置；路由关闭时短路，不读取位置、不执行扫描。
        GameLocation npcLocation = null;
        GameLocation playerLocation = null;
        if (includeFarmDetails)
        {
            npcLocation = Character.StardewNpc.currentLocation;
            playerLocation = Game1.player.currentLocation;
        }

        if (CanObserveGreenhouse(includeFarmDetails, npcLocation, playerLocation))
        {
            // 现场权限成立：只注入当前温室观察，不调用 BuildFarmSummary；
            // 无需同住身份成立，也不使用室外缓存。
            gameConstantPrompt.AppendLine(
                FarmStateScanner.BuildObservedGreenhouseSummary(IsChineseLanguage, npcLocation));
        }
        else if (ShouldIncludeFarmSummary(includeFarmDetails, isHouseholdMember))
        {
            string farmSummary = FarmStateScanner.BuildFarmSummary(IsChineseLanguage, includeGreenhouseInterior: false);
            if (!string.IsNullOrEmpty(farmSummary))
                gameConstantPrompt.AppendLine(farmSummary);
            else
                ModEntry.SMonitor?.Log(
                    $"[Prompts] Farm summary omitted for '{Character.Name}': no injectable outdoor farm background.",
                    StardewModdingAPI.LogLevel.Debug);
        }
        else
        {
            ModEntry.SMonitor?.Log(
                $"[Prompts] Farm summary omitted for '{Character.Name}': " +
                (includeFarmDetails ? "not a spouse/roommate of the current player." : "farm details routing is off."),
                StardewModdingAPI.LogLevel.Debug);
        }
        return gameConstantPrompt.ToString();
    }

    /// <summary>
    /// VT-FARM-OBSERVE-05: 温室现场观察权限。仅当路由允许、NPC 地点非 null、
    /// 与当前玩家同处同一地点实例且该地点为温室时成立；房间标记使用原生
    /// GameLocation.IsGreenhouse，不按地点名称推断。null 地点视为无法确认现场，
    /// 不授予权限。纯判定，不携带状态。
    /// </summary>
    internal static bool CanObserveGreenhouse(
        bool includeFarmDetails,
        GameLocation npcLocation,
        GameLocation playerLocation)
    {
        return includeFarmDetails
            && npcLocation != null
            && ReferenceEquals(npcLocation, playerLocation)
            && npcLocation.IsGreenhouse;
    }

    /// <summary>
    /// VT-FARM-ACCESS-01：农场摘要注入门禁。路由开关与同住人身份同时为 true 才允许注入，
    /// 其余组合一律省略整个农场摘要；纯逻辑与判定，不携带字段、集合或持久化状态。
    /// </summary>
    internal static bool ShouldIncludeFarmSummary(bool includeFarmDetails, bool isHouseholdMember)
    {
        return includeFarmDetails && isHouseholdMember;
    }

    /// <summary>
    /// 判定当前对话 NPC 是否为当前玩家的同住人（配偶或室友，含多婚 Mod 感知）。
    /// 仅在提示词装配入口（MonoGame 主线程、存档已加载）执行；关系判定委托
    /// SpouseQueryService.IsMarried，查询主体为当前对话玩家 Game1.player。
    /// </summary>
    private bool ResolveCurrentNpcHouseholdMembership()
    {
        if (!StardewModdingAPI.Context.IsWorldReady)
        {
            ModEntry.SMonitor?.Log(
                $"[Prompts] Farm summary household check reached for '{Character.Name}' while the world is not ready; refusing to query relationships outside a loaded save.",
                StardewModdingAPI.LogLevel.Error);
            throw new InvalidOperationException(
                "Farm summary household check requires Context.IsWorldReady; the relationship query was reached outside a loaded save.");
        }

        var player = Game1.player;
        if (player == null)
        {
            ModEntry.SMonitor?.Log(
                $"[Prompts] Household membership of '{Character.Name}' cannot be reliably determined: no current player although the world reports ready.",
                StardewModdingAPI.LogLevel.Warn);
            throw new InvalidOperationException(
                "Farm summary household check cannot reliably determine household identity: Context.IsWorldReady is true but Game1.player is null.");
        }

        return SpouseQueryService.Instance.IsMarried(Character.Name, player);
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
            if (IsChineseLanguage)
            {
                bio = NpcNameLocalizer.LocalizeNamesInText(bio);
            }
            npcConstantPrompt.AppendLine(bio);

            // REL-002：已知关系骨架。世界就绪校验置于新增接入代码最前面，
            // 未就绪时不读取角色卡或游戏实体，直接拒绝构建。
            if (!StardewModdingAPI.Context.IsWorldReady)
            {
                ModEntry.SMonitor?.Log(
                    $"[SocialBackbone] Known-relationship backbone reached for '{Character.Name}' while the world is not ready; refusing to read character card data outside a loaded save.",
                    StardewModdingAPI.LogLevel.Error);
                throw new InvalidOperationException(
                    "Known-relationship backbone requires Context.IsWorldReady; the constant context was built outside a loaded save.");
            }
            string relationshipBackbone = SocialBackboneBuilder.Build(
                Character.Name,
                Character.Bio.Relationships,
                IsChineseLanguage,
                NpcNameLocalizer.GetLocalizedName);
            if (!string.IsNullOrWhiteSpace(relationshipBackbone))
            {
                npcConstantPrompt.AppendLine(relationshipBackbone);
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
                    {
                        string heading = trait.Heading;
                        string desc = trait.Description;
                        if (IsChineseLanguage)
                        {
                            heading = NpcNameLocalizer.GetZhName(heading);
                            desc = NpcNameLocalizer.LocalizeNamesInText(desc);
                        }
                        npcConstantPrompt.AppendLine($"* **{heading}**: {desc}");
                    }
                }
            }

            var npcInstance = Game1.getCharacterFromName(Character.Name);
            string progressState = ProgressStateResolver.ResolveActiveState(npcInstance, Character.Bio.ProgressStates);
            if (!string.IsNullOrWhiteSpace(progressState))
            {
                if (IsChineseLanguage)
                {
                    progressState = NpcNameLocalizer.LocalizeNamesInText(progressState);
                }
                npcConstantPrompt.AppendLine($"## {(IsChineseLanguage ? "当前状态" : "Current State")}");
                npcConstantPrompt.AppendLine(progressState);
            }

            npcConstantPrompt.AppendLine(Character.Bio.BiographyEnd);
        }
        return npcConstantPrompt.ToString();
    }

    /// <summary>Tier 1 拼装序列（12 项，顺序固定；DailySalience 追加在尾部，既有 11 项前置顺序不变）。</summary>
    private static readonly IReadOnlyList<string> Tier1BlockSequence = new[]
    {
        Tier1BlockIds.GameState, Tier1BlockIds.EventHistory, Tier1BlockIds.BranchTheme,
        Tier1BlockIds.Scene, Tier1BlockIds.CompanionFocus, Tier1BlockIds.GreetingContext,
        Tier1BlockIds.RelationBase, Tier1BlockIds.RecentEvents, Tier1BlockIds.SpecialDates,
        Tier1BlockIds.SpouseAction, Tier1BlockIds.EvolvedTraits, Tier1BlockIds.DailySalience,
    };

    /// <summary>Tier 2b 拼装序列（18 项，顺序固定）。</summary>
    private static readonly IReadOnlyList<string> Tier2bBlockSequence = new[]
    {
        Tier2bBlockIds.Gossip, Tier2bBlockIds.Interaction, Tier2bBlockIds.Jealousy, Tier2bBlockIds.Preoccupation,
        Tier2bBlockIds.PendingTopic, Tier2bBlockIds.Gift, Tier2bBlockIds.Milestone,
        Tier2bBlockIds.Echo, Tier2bBlockIds.Eavesdrop, Tier2bBlockIds.SpouseWaiting,
        Tier2bBlockIds.LocalPerception, Tier2bBlockIds.Emotion, Tier2bBlockIds.PlayerProfile,
        Tier2bBlockIds.DateInvite, Tier2bBlockIds.FollowProto, Tier2bBlockIds.DateEndProto,
        Tier2bBlockIds.Movement, Tier2bBlockIds.SocialLens,
    };

    public void AssembleCore(InjectionPlan plan, DialogueContext context, Character character)
    {
        if (plan == null)
            throw new ArgumentNullException(nameof(plan));

        _corePlan = plan;
        var prompt = new StringBuilder();
        prompt.Append(AssembleTier1(plan));
        prompt.Append(AssembleTier2a(context, character));
        prompt.Append(AssembleTier2b(plan));
        CorePrompt = prompt.ToString();
    }

    private static string AssembleTier1(InjectionPlan plan)
    {
        var prompt = new StringBuilder();
        if (plan.Tier1Snapshot == null)
            throw new InvalidOperationException("InjectionPlan.Tier1Snapshot must be non-null; BuildPlan builds and registers the snapshot for new sessions before assembling the plan.");
        foreach (var blockId in Tier1BlockSequence)
        {
            string text = plan.Tier1Snapshot.Get(blockId);
            if (!string.IsNullOrEmpty(text))
            {
                prompt.AppendLine(text);
                prompt.AppendLine();
            }
        }
        return prompt.ToString();
    }

    private string AssembleTier2a(DialogueContext context, Character character)
    {
        var prompt = new StringBuilder();
        _sessionContinuitySegment = PromptsBlocks.BuildSessionContinuity(character, context, IsChineseLanguage);
        prompt.Append(_sessionContinuitySegment);
        _currentConversationSegment = PromptsBlocks.BuildCurrentConversation(character, context, CurrentFlags, _emittedBlockKeys, Name);
        prompt.Append(_currentConversationSegment);
        return prompt.ToString();
    }

    private static string AssembleTier2b(InjectionPlan plan)
    {
        var prompt = new StringBuilder();
        foreach (var blockId in Tier2bBlockSequence)
        {
            if (plan.ActiveImpulses.TryGetValue(blockId, out string text) && !string.IsNullOrEmpty(text))
            {
                prompt.AppendLine(text);
                prompt.AppendLine();
            }
        }
        return prompt.ToString();
    }

    internal static string AssembleTier1Segment(InjectionPlan plan) => AssembleTier1(plan);
    internal string AssembleTier2aSegment(DialogueContext context, Character character) => AssembleTier2a(context, character);
    internal static string AssembleTier2bSegment(InjectionPlan plan) => AssembleTier2b(plan);

    private void LogRoutingDebug(string promptText, string routeType)
    {
        try
        {
            if (ModEntry.Config == null || !ModEntry.Config.Debug)
                return;
            int promptLength = promptText.Length;
            int estimatedTokens = promptText.Count(c => c > 127) * 3 / 2
                                  + promptText.Count(c => c <= 127) / 4;
            string debugMsg =
                $"[ContextRouter] Target: {Name} | Mode: {routeType} | " +
                $"Length: {promptLength} chars (~{estimatedTokens} Tokens) " +
                $"| [Flags -> Greeting: {CurrentFlags.IsSimpleGreeting}, " +
                $"Farm: {CurrentFlags.IncludeFarmDetails}, " +
                $"Env: {CurrentFlags.IncludeEnvironment}, " +
                $"Mem: {CurrentFlags.IncludeMemories}]";
            ModEntry.SMonitor?.Log(debugMsg, StardewModdingAPI.LogLevel.Info);
        }
        catch { }
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

        // 结构化系统动作协议容器：规定多标签排布优先级流水线
        commandPrompt.AppendLine("<system_action_reference>");
        commandPrompt.AppendLine(isZh ? "### [系统控制协议：动作与行为标签]" : "### [SYSTEM SPECIFICATION: ACTION & EMOTE TAGS]");

        if (isZh)
        {
            commandPrompt.AppendLine("- 表情气泡 (角色头顶动画): [ACTION:EMOTE:HAPPY], [ACTION:EMOTE:HEART], [ACTION:EMOTE:BLUSH], [ACTION:EMOTE:SURPRISE], [ACTION:EMOTE:SAD], [ACTION:EMOTE:ANGRY]");
            commandPrompt.AppendLine("- 身体转向: [ACTION:FACE:FARMER] (面向玩家), [ACTION:FACE:UP], [ACTION:FACE:DOWN], [ACTION:FACE:LEFT], [ACTION:FACE:RIGHT]");
            commandPrompt.AppendLine("- 物理位移: [ACTION:STEP:FORWARD], [ACTION:STEP:BACKWARD], [ACTION:STEP:LEFT], [ACTION:STEP:RIGHT]");
            commandPrompt.AppendLine();
            commandPrompt.AppendLine("[OUTPUT_FORMAT_RULE]");
            commandPrompt.AppendLine("- [ACTION:...] 与 [UI:...] 等实体行为与界面指令标签必须统一置于回复的最末尾，禁止穿插在台词中。");
            commandPrompt.AppendLine("- 肖像表情代码（如 $h, $0, $u, $s, $a 等）允许且鼓励随语境在台词中实时插入（支持单句及跨屏多次插入），打字机将在打印到对应字符时即时切换立绘。");
            commandPrompt.AppendLine("- 多标签排布流水线：[带表情码的台词正文] [ACTION标签] [UI标签] [MOOD标签]");
            commandPrompt.AppendLine("  示例：\"(挠头) $0这事儿... $h哈哈，交给我吧！\" [ACTION:FACE:FARMER]");
            commandPrompt.AppendLine("[OUTPUT_DOMAIN] 对白台词区域承载角色言语与神态描写；上方系统指令标签位于回复末尾的独立区域，与台词正文分离。");
        }
        else
        {
            commandPrompt.AppendLine("- Emote bubbles (head animation): [ACTION:EMOTE:HAPPY], [ACTION:EMOTE:HEART], [ACTION:EMOTE:BLUSH], [ACTION:EMOTE:SURPRISE], [ACTION:EMOTE:SAD], [ACTION:EMOTE:ANGRY]");
            commandPrompt.AppendLine("- Facing direction: [ACTION:FACE:FARMER] (look at player), [ACTION:FACE:UP], [ACTION:FACE:DOWN], [ACTION:FACE:LEFT], [ACTION:FACE:RIGHT]");
            commandPrompt.AppendLine("- Movement steps: [ACTION:STEP:FORWARD], [ACTION:STEP:BACKWARD], [ACTION:STEP:LEFT], [ACTION:STEP:RIGHT]");
            commandPrompt.AppendLine();
            commandPrompt.AppendLine("[OUTPUT_FORMAT_RULE]");
            commandPrompt.AppendLine("- Entity behavior and UI tags such as [ACTION:...] and [UI:...] MUST be placed together at the absolute end of the reply. Never interleave them inside dialogue.");
            commandPrompt.AppendLine("- Portrait emotion codes (e.g. $h, $0, $u, $s, $a) are allowed and encouraged to be inserted inline wherever the mood shifts (multiple times within a sentence and across screen boxes); the typewriter switches the portrait the instant it prints that character.");
            commandPrompt.AppendLine("- Multi-tag sequence pipeline: [Dialogue body with inline portrait codes] [ACTION tag] [UI tag] [MOOD tag]");
            commandPrompt.AppendLine("  Example: \"(scratches head) $0Well... $haha, leave it to me!\" [ACTION:FACE:FARMER]");
            commandPrompt.AppendLine("[OUTPUT_DOMAIN] The dialogue area carries the character's spoken lines and manner descriptions; the system action tags above live in a separate region at the end of the reply, apart from the dialogue body.");
        }
        commandPrompt.AppendLine("</system_action_reference>");

        string targetLang = TargetLanguageName;
        if (ModEntry.Config.ApplyTranslation
            && !string.Equals(targetLang, "Invariant Language (Invariant Country)", StringComparison.Ordinal)
            && !string.Equals(targetLang, "English", StringComparison.OrdinalIgnoreCase)
            && LocalizedContentManager.CurrentLanguageCode != LocalizedContentManager.LanguageCode.en)
        {
            commandPrompt.AppendLine(Util.GetString(Character, "instructionsTranslate", new { Language = targetLang }));
        }
        return commandPrompt.ToString();
    }

    internal string GetInstructions(InstructionsBranch branch)
    {
        var instructions = new StringBuilder();
        bool isZh = IsChineseLanguage;
        bool enableResponses = ModEntry.Config?.EnableSuggestedResponses ?? true;
        instructions.AppendLine($"## {Util.GetString(Character, "instructionsHeading", new { Language = TargetLanguageName })}");
        instructions.AppendLine(Util.GetString(Character, "instructionsIntro", new { Name = Name }));
        instructions.AppendLine(Util.GetString(Character, "instructionsFarmersName"));
        string breaks = Util.GetString(Character, "instructionsBreaks");
        if (!string.IsNullOrWhiteSpace(breaks))
            instructions.AppendLine(breaks);
        string singleLine = Util.GetString(Character, "instructionsSingleLine");
        if (!string.IsNullOrWhiteSpace(singleLine))
            instructions.AppendLine(singleLine);
        if (enableResponses)
        {
            instructions.AppendLine(Util.GetString(Character, "instructionsResponses", new { Name = Name }));
        }
        else
        {
            instructions.AppendLine(Util.GetString(Character, "instructionsNoResponses", new { Name = Name }));
        }
        instructions.AppendLine(Util.GetString(Character, "instructionsFallback", new { Name = Name }));

        // LLM 注意力引导词规范化
        instructions.AppendLine(isZh
            ? "- [CORE_PERSPECTIVE] 你的输出域是你自身角色的言语、动作与神态反应。完成当前台词表达后，对话回合交回玩家。"
            : "- [CORE_PERSPECTIVE] Your output domain is your own character's speech, actions, and reactions. Yield the dialogue turn back to the player after your line.");
        instructions.AppendLine(isZh
            ? "- [IMMEDIATE_RESPONSE_RULE] 农夫刚说的话具有最高注意力优先级。第一句话必须直接承接、回复农夫的输入；完成直接反馈后，方可顺承个人日常思绪或生活事项。"
            : "- [IMMEDIATE_RESPONSE_RULE] The farmer's latest words have top attention priority. Your very first sentence MUST directly address or answer their input before transitioning to personal thoughts or ambient chores.");
        instructions.AppendLine(isZh
            ? "- [FAMILIARITY_BOUNDARIES] 人际边界与抗性随熟悉度动态分流。面对突兀、无用或不合心意之事展现市井本能：对生人保持明确边界感与干脆拒收；对密友与配偶展现防备卸除的熟稔调侃、日常嗔怪或叹气。两种语域都保持角色自己的说话习惯。"
            : "- [FAMILIARITY_BOUNDARIES] Scale interpersonal distance and emotional friction to relationship depth. Reject unwanted things with raw everyday instinct: maintain crisp boundaries and blunt refusals with acquaintances; display effortless banter, comfortable teasing, or casual exasperation with spouses and close friends. Both registers carry the character's own speaking habits.");
        instructions.AppendLine(isZh
            ? "- [MOOD_SHIFT_RULE] 若本次交流导致你的情绪基调发生明显转换（如转为好奇/烦躁/欣喜），在台词末尾附带 [MOOD:curious] / [MOOD:annoyed] / [MOOD:happy] 等标签。"
            : "- [MOOD_SHIFT_RULE] If this turn causes a distinct emotional shift (e.g., to curious, annoyed, happy), append [MOOD:curious] / [MOOD:annoyed] / [MOOD:happy] at the end of the line.");
        instructions.AppendLine(isZh
            ? "- [SCENE_FACT_AUTHORITY] 当前场景（所在地点与现场条件）和当前与农夫的关系状态是唯一事实权威；角色卡、示例对白与历史记录若与此冲突，一律以当前事实为准。你可以做出与现场吻合的细小动作，这些动作取材于你直接感知到的信息。"
            : "- [SCENE_FACT_AUTHORITY] The current scene (your location and on-site conditions) and your current relationship with the farmer are the sole factual authority; your character card, sample lines, and past dialogue never override them. You may take small actions that fit the scene; those actions draw from what you directly perceive right now.");
        instructions.AppendLine(isZh
            ? "- [PROGRESSION_MOMENTUM] 推动事态向前演进：日常交谈具有连贯的生活推进力。当上一轮的话题、提议或询问已被对方承接后，角色应自信推进到下一步具体行动、细节决策或现场分工（例如直接动身起手、商量偏好分工、交代当下收尾），让交互像真实生活一样往前走。"
            : "- [PROGRESSION_MOMENTUM] Keep the moment moving forward: everyday exchanges carry a coherent sense of life progression. Once the previous topic, proposal, or question has been picked up, confidently advance to the next concrete step, detail decision, or on-the-spot division of labor (e.g., get moving right away, settle who does what, hand off the wrap-up), letting the interaction walk forward like real life.");

        if (enableResponses)
        {
            instructions.AppendLine(isZh
                ? "- [OPTION_DIVERGENCE] 选项态度三棱镜：若本轮提出 '%' 回复选项，确保每个选项代表鲜明的态度倾向与行动差异（例如：一条积极推进细节、一条生活幽默打趣、一条表达独立节奏或分工协助），让每个选项都能引出不同的互动走向；若当前交流已自然达成默契或行动已定，直接以干脆自信的生活话语收尾。"
                : "- [OPTION_DIVERGENCE] Attitudinal prism for options: if you offer '%' reply options this turn, make each option carry a distinct attitude and action difference (e.g., one pushing the details forward, one daily-life quip, one voicing an independent pace or lending a hand), so every option opens a different direction; when the exchange has naturally settled into agreement or the plan is set, close with crisp, confident everyday words.");
            instructions.AppendLine(isZh
                ? "- [SUGGESTION_SCOPE_RULE] 以 '%' 开头的发言选项，取材自你本次台词中已经亲口说出的信息。"
                : "- [SUGGESTION_SCOPE_RULE] Any suggested response options prefixed with '%' draw from what you have explicitly voiced aloud in this turn.");
        }
        else
        {
            instructions.AppendLine(isZh
                ? "- [OUTPUT_DOMAIN] 你的输出为角色台词与神态描写；'%' 前缀的玩家选项在本轮不启用。"
                : "- [OUTPUT_DOMAIN] Your output is character dialogue and manner descriptions; player options prefixed with '%' are not enabled this turn.");
        }

        if (!Character.Bio.ExtraPortraits.ContainsKey("!"))
        {
            var extraPortraits = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(Character.Bio.Unique))
            {
                extraPortraits.Append(Util.GetString(Character, "instructionsExtraPortraitLine",
                    new { Key = "u", Value = Character.Bio.Unique }));
            }
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

        switch (branch)
        {
            case InstructionsBranch.StoodUp:
                instructions.AppendLine(Util.GetString(Character, "branchRuleStoodUp"));
                break;
            case InstructionsBranch.Date:
                instructions.AppendLine(Util.GetString(Character, "branchRuleDate"));
                break;
            case InstructionsBranch.Greeting:
                instructions.AppendLine(Util.GetString(Character, "branchRuleGreeting"));
                break;
        }
        return instructions.ToString();
    }

    private string GetResponseStart()
    {
        string start = I18n.ResponseStart();
        return !string.IsNullOrWhiteSpace(start) ? start : "[In-Character Dialogue]:";
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

    // ── VT3-C1: 静态块生产者 ──
    internal static class PromptsBlocks
    {
        internal static bool IsZh() => Prompts.ResolveIsChinese();

        private static string LocalizeDirection(object direction, bool isZh)
        {
            if (direction == null) return string.Empty;
            string raw = direction.ToString();
            if (!isZh) return raw.ToLowerInvariant();
            return raw.ToLowerInvariant() switch
            {
                "up" => "上方",
                "down" => "下方",
                "left" => "左侧",
                "right" => "右侧",
                // CTX-009：BlockDirection 首次可达 Forward/Backward，补齐 ZH 渲染
                // （风格对齐既有 上方/下方/左侧/右侧）；EN 分支走 raw.ToLowerInvariant()，
                // 六方向本就全覆盖，无需改动。
                "forward" => "前方",
                "backward" => "后方",
                _ => raw
            };
        }

        internal static bool MovementInstructionApplicable(ContextFlags flags)
        {
            return !(flags?.IsOnDate == true)
                && !(flags?.IsJealousy == true)
                && ((flags?.IsActionRequested ?? false) || (flags?.IsFollowing ?? false));
        }

        /// <summary>
        /// REL-005：今日社交关注（DailySalience，Tier 1）。由 BuildTier1Snapshot 在
        /// 对话会话建立时调用一次，从 RelationshipRotationResolver 的当日轮换子集
        /// （1~2 位）拼装；无符合条目返回 string.Empty（SetTier1 跳过，不进快照）。
        /// 不进入 NpcConstantContext（Tier 0），保证常量上下文前缀的跨日稳定。
        /// </summary>
        internal static string BuildDailySalience(Character character, DialogueContext context)
        {
            var relationships = character?.Bio?.Relationships;
            if (relationships == null) return string.Empty;

            var dailyPicks = RelationshipRotationResolver.SelectDailyRelationships(
                character.Name, relationships, context?.Hearts ?? 0);
            if (dailyPicks == null || dailyPicks.Count == 0) return string.Empty;

            bool isZh = IsZh();
            var salience = new StringBuilder();
            if (isZh)
            {
                salience.AppendLine("## 今日社交关注");
                salience.AppendLine("今天在日常闲聊中，你更容易顺带联想到或提及以下人物的相关琐事：");
            }
            else
            {
                salience.AppendLine("## Today's Social Salience");
                salience.AppendLine("In casual conversation today, you are more inclined to naturally bring up or think about these people:");
            }

            foreach (var relationship in dailyPicks)
            {
                string heading = relationship.Heading;
                string desc = relationship.Description;
                if (isZh)
                {
                    heading = NpcNameLocalizer.GetLocalizedName(heading);
                    desc = NpcNameLocalizer.LocalizeNamesInText(desc);
                }
                salience.AppendLine($"* **{heading}**: {desc}");
            }
            return salience.ToString();
        }

        internal static string SelectGiftGiven(Character character)
        {
            if (character == null) return null;
            if (!Game1.player.friendshipData.TryGetValue(character.Name, out Friendship friendship) || !friendship.IsMarried())
                return null;
            if (Game1.random.NextDouble() < 0.8) return null;
            var options = character.DialogueData.AllEntries.SelectMany(x => x.Value.AllValues)
                .SelectMany(x => x.Elements).SelectMany(x => x.GiftOptions).ToList();
            if (options.Count == 0) return null;
            return options[Game1.random.Next(options.Count)];
        }

        private static bool NpcIsMale(Character character) =>
            character.StardewNpc.GetData().Gender == StardewValley.Gender.Male;

        internal static string BuildConversationHeading(Character character) =>
            "### " + Util.GetString(character, "currentConversationHeading");

        internal static string BuildRelationshipWord(Character character, bool? maleFarmer)
        {
            bool isMale = NpcIsMale(character);
            return maleFarmer == true
                ? (isMale ? Util.GetString(character, "generalGayMale") : Util.GetString(character, "generalHeterosexual"))
                : (isMale ? Util.GetString(character, "generalHeterosexual") : Util.GetString(character, "generalLesbian"));
        }

        internal static string FormatDateElapsed(int startGameTime, bool isZh)
        {
            int elapsed = Math.Max(0, Game1.timeOfDay - startGameTime);
            if (elapsed < 100)
                return isZh ? "不到一小时" : "less than an hour";
            return isZh ? $"约 {elapsed / 100} 小时" : $"about {elapsed / 100} hour(s)";
        }

        internal static string BuildDateGiftLine(DateSessionDigest? digest, bool isZh)
        {
            if (digest == null || string.IsNullOrWhiteSpace(digest.GivenGiftName))
                return string.Empty;
            string tasteDesc = digest.GivenGiftTaste switch
            {
                NPC.gift_taste_love => isZh ? "你最爱的东西" : "something you love",
                NPC.gift_taste_like => isZh ? "你喜欢的东西" : "something you like",
                NPC.gift_taste_dislike => isZh ? "你不喜欢的" : "something you dislike",
                NPC.gift_taste_hate => isZh ? "你讨厌的" : "something you hate",
                _ => isZh ? "普通的" : "an ordinary gift"
            };
            return isZh
                ? $"- 你已经收到了农夫送的{digest.GivenGiftName}（{tasteDesc}）。"
                : $"- You already received the {digest.GivenGiftName} from the farmer ({tasteDesc}).";
        }

        // ── Tier 1：分支主题前缀 ──
        internal static string BuildBranchTheme(Character character, DialogueContext context, InstructionsBranch branch)
        {
            bool isZh = IsZh();
            var prompt = new StringBuilder();
            switch (branch)
            {
                case InstructionsBranch.StoodUp:
                    prompt.AppendLine("<emotional_conflict type=\"stood_up\">");
                    prompt.AppendLine(isZh
                        ? "- 事实：你昨晚一直等着玩家赴约，但玩家没有出现。"
                        : "- Fact: You waited for the player last night, and they never showed.");
                    prompt.AppendLine("</emotional_conflict>\n");
                    break;

                case InstructionsBranch.Date:
                {
                    if (DateManager.Instance == null)
                        return prompt.ToString();

                    string npcName = character?.Name ?? "";
                    var digest = DateManager.Instance.BuildSessionDigest(npcName);
                    var dateMode = DateManager.Instance.CurrentDateMode;
                    if (dateMode == DateManager.DateMode.Follow)
                    {
                        prompt.AppendLine("<date_context mode=\"walking\">");
                        // VT-FOCUS-03：优先取 NPC 实时所在地点的友好名；NPC 无位置时回落会话地点原值（组装热路径，静默回落）。
                        var npcWalkingLoc = character?.StardewNpc?.currentLocation;
                        string walkingLocation = npcWalkingLoc != null
                            ? EnvironmentScanner.GetLocationFriendlyName(npcWalkingLoc.Name)
                            : (DateManager.Instance.ActiveDateLocation ?? "");
                        prompt.AppendLine(isZh
                            ? $"- 当前地点：{walkingLocation}"
                            : $"- Location: {walkingLocation}");
                        if (digest is { IsValid: true })
                            prompt.AppendLine(isZh
                                ? $"- 约会进行中：你们已经一起走了{FormatDateElapsed(digest.StartGameTime, isZh)}。"
                                : $"- Date in progress: you've been walking together for {FormatDateElapsed(digest.StartGameTime, isZh)}.");
                        string walkingGiftLine = BuildDateGiftLine(digest, isZh);
                        if (!string.IsNullOrEmpty(walkingGiftLine))
                            prompt.AppendLine(walkingGiftLine);
                        prompt.AppendLine(isZh
                            ? "- [ATTENTION_FOCUS] 你正和农夫单独相处、边走边聊。本轮对话默认从约会本身取材：眼前的景色与行人、彼此的近况与感受、一路上的见闻。农场经营、家务杂事等日常话题只在玩家主动提起时才接。"
                            : "- [ATTENTION_FOCUS] You're alone with the farmer, walking and talking. This turn's dialogue draws from the date itself: the scenery and passersby around you, each other's recent lives and feelings, what you notice along the way. Farm work, chores and other everyday topics come up only if the player raises them.");
                        prompt.AppendLine("</date_context>\n");
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
                        prompt.AppendLine("<date_context mode=\"romantic_active\">");
                        prompt.AppendLine(isZh ? $"- 当前地点：{locName}" : $"- Location: {locName}");
                        if (!string.IsNullOrWhiteSpace(locDetail))
                            prompt.AppendLine(isZh ? $"- 周围环境：{locDetail}" : $"- Atmosphere: {locDetail}");
                        prompt.AppendLine(isZh
                            ? "- 你们正在进行约会。周围的环境就在眼前。"
                            : "- You're on a date. The surroundings are right there.");
                        if (digest is { IsValid: true })
                            prompt.AppendLine(isZh
                                ? $"- 约会进行中：你们已经相处了{FormatDateElapsed(digest.StartGameTime, isZh)}。"
                                : $"- Date in progress: you've been together for {FormatDateElapsed(digest.StartGameTime, isZh)}.");
                        string settledGiftLine = BuildDateGiftLine(digest, isZh);
                        if (!string.IsNullOrEmpty(settledGiftLine))
                            prompt.AppendLine(settledGiftLine);
                        prompt.AppendLine(isZh
                            ? "- [ATTENTION_FOCUS] 你们正在进行约会。本轮对话默认从约会本身取材：眼前的场景氛围、彼此的感受与互动。农场经营、家务杂事等日常话题只在玩家主动提起时才接。"
                            : "- [ATTENTION_FOCUS] You're on a date right now. This turn's dialogue draws from the date itself: the scene and atmosphere around you, each other's feelings and interactions. Farm work, chores and other everyday topics come up only if the player raises them.");
                        prompt.AppendLine("</date_context>\n");
                    }
                    break;
                }

                case InstructionsBranch.Greeting:
                    prompt.AppendLine("<greeting_fast_pass>");
                    prompt.AppendLine(isZh
                        ? "农夫正向你打招呼。"
                        : "The farmer just greeted you.");
                    prompt.AppendLine("</greeting_fast_pass>\n");
                    break;

                case InstructionsBranch.Normal:
                default:
                    prompt.AppendLine($"## {Util.GetString(character, "coreInstructionHeading")}");
                    break;
            }
            return prompt.ToString();
        }

        // ── Tier 1：纯文本生产者 ──
        internal static string BuildGameState(Character character)
        {
            var prompt = new StringBuilder();
            if (Game1.year == 1)
                prompt.AppendLine(Util.GetString(character, "gameStateKentNo"));
            else
                prompt.AppendLine(Util.GetString(character, "gameStateKentYes"));
            return prompt.ToString();
        }

        internal static string BuildEventHistory(Character character, DialogueContext context)
        {
            var prompt = new StringBuilder();
            EventHistoryHelper.BuildEventHistory(prompt, character, context);
            return prompt.ToString();
        }

        internal static string BuildMicroEnvironment(Character character, DialogueContext context, ContextFlags flags)
        {
            var prompt = new StringBuilder();
            prompt.AppendLine(Util.GetString(character, "sceneHeading"));
            prompt.AppendLine("<scene_context>");
            string friendlyLocation = EnvironmentScanner.GetLocationFriendlyName(context.Location);
            prompt.AppendLine(Util.GetString(character, "sceneLocation", new { Location = friendlyLocation }));

            // VT-SCENE-01：空间类型权威行。地点缺失属 BUG——不猜室内/室外，直接升级。
            var npcLocation = character.StardewNpc.currentLocation;
            if (npcLocation == null)
            {
                ModEntry.SMonitor?.Log(
                    $"[Prompts] BuildMicroEnvironment: NPC '{character.Name}' has no currentLocation during valid dialogue assembly; refusing to guess indoors/outdoors.",
                    StardewModdingAPI.LogLevel.Error);
                throw new InvalidOperationException(
                    $"BuildMicroEnvironment requires a non-null NPC currentLocation during valid dialogue assembly (NPC: {character.Name}).");
            }
            bool isIndoors = !npcLocation.IsOutdoors
                             || npcLocation is StardewValley.Locations.FarmHouse
                             or StardewValley.Locations.IslandFarmHouse;
            if (isIndoors)
                prompt.AppendLine(IsZh()
                    ? "- 空间类型: 室内。你此刻的感知域是室内温度、光线、声音与近处的人与物。天气进入对话时，只作为窗外所见或他人提及出现。"
                    : "- Space Type: Indoors. Your perception right now covers indoor temperature, light, sound, and the people or objects near you. Weather enters dialogue only as something seen through a window or mentioned by someone else.");
            else
                prompt.AppendLine(IsZh() ? "- 空间类型: 室外。" : "- Space Type: Outdoors.");

            string festival = EnvironmentScanner.GetTodayFestivalName();
            if (!string.IsNullOrEmpty(festival))
            {
                if (EnvironmentScanner.IsFestivalCurrentlyActive())
                    prompt.AppendLine(Util.GetString(character, "sceneFestivalActive", new { Festival = festival }));
                else
                    prompt.AppendLine(Util.GetString(character, "sceneFestivalToday", new { Festival = festival }));
            }
            prompt.AppendLine(Util.GetString(character, "sceneTimeSeason", new { Time = context.TimeOfDay, Season = Game1.CurrentSeasonDisplayName, Day = context.DayOfSeason }));
            if (context.Weather != null && context.Weather.Any())
            {
                string sensoryWeather = BuildSensoryWeatherDescription(character, context.Weather);
                if (!string.IsNullOrWhiteSpace(sensoryWeather))
                    prompt.AppendLine(Util.GetString(character, "sceneWeather", new { Weather = sensoryWeather }));
            }
            var nearbyObjects = EnvironmentScanner.ScanNearbyObjects(character.StardewNpc, 5, 8);
            if (nearbyObjects.Any())
                prompt.AppendLine(Util.GetString(character, "sceneObjects", new { Objects = string.Join(", ", nearbyObjects) }));
            string npcLocationName = npcLocation.Name;
            string playerLocationName = Game1.getPlayerOrEventFarmer().currentLocation?.Name ?? "";
            if (string.Equals(npcLocationName, playerLocationName, StringComparison.OrdinalIgnoreCase))
                prompt.AppendLine(Util.GetString(character, "sceneSpatialCoPresent"));
            if (flags?.IncludeEnvironment == true)
            {
                var otherNpcs = Util.GetSceneVillagers(character.StardewNpc);
                if (otherNpcs.Any())
                    prompt.AppendLine(Util.GetString(character, "sceneNearbyVillagers", new { Villagers = string.Join(", ", otherNpcs.Select(n => n.displayName)) }));
            }
            string poiContext = CompanionScheduleManager.Instance.GetActivePoiContext(character.Name);
            if (!string.IsNullOrEmpty(poiContext))
                prompt.AppendLine(poiContext);
            prompt.AppendLine("</scene_context>\n");
            return prompt.ToString();
        }

        internal static string BuildSensoryWeatherDescription(Character character, List<string> weatherList)
        {
            if (weatherList == null || !weatherList.Any())
                return string.Empty;
            var descriptions = new List<string>();
            foreach (var raw in weatherList)
            {
                string key = raw.ToLowerInvariant() switch
                {
                    "sun" or "clear" => "weatherDescSun",
                    "rain" => "weatherDescRain",
                    "storm" or "lightning" => "weatherDescStorm",
                    "snow" => "weatherDescSnow",
                    "wind" or "debris" => "weatherDescWind",
                    "greenrain" => "weatherDescGreenRain",
                    _ => null
                };
                string desc = key != null ? Util.GetString(character, key) : raw;
                if (!string.IsNullOrEmpty(desc))
                    descriptions.Add(desc);
            }
            return string.Join(" ", descriptions);
        }

        internal static string BuildCompanionWalkingContext(Character character, DialogueContext context, bool isZh)
        {
            var prompt = new StringBuilder();
            string locationName = EnvironmentScanner.GetLocationFriendlyName(context.Location);
            prompt.AppendLine("<companion_context mode=\"walking_together\">");
            prompt.AppendLine(isZh
                ? $"- 当前状态：你正与农夫在{locationName}一同散步同行。"
                : $"- Current state: You're out walking together with the farmer at {locationName}.");
            // 无可靠起点信息（会话不存在/名字不匹配）时跳过进度行，不虚构时长。
            var digest = DateManager.Instance.BuildSessionDigest(character.Name);
            if (digest is { IsValid: true })
                prompt.AppendLine(isZh
                    ? $"- 你们已经同行了{FormatDateElapsed(digest.StartGameTime, isZh)}。"
                    : $"- You've been walking together for {FormatDateElapsed(digest.StartGameTime, isZh)}.");
            prompt.AppendLine(isZh
                ? "- [ATTENTION_FOCUS] 这是你们俩的共处时光。本轮对话默认从你们的同行相处取材：沿途的景物与天气、路过的行人与声响、彼此的近况和感受。眼前的一切都是你们共同的经历，可以随手拿来说。"
                : "- [ATTENTION_FOCUS] This is your shared time together. This turn's dialogue draws from the walk itself: the scenery and weather along the way, the passersby and sounds around you, each other's recent lives and feelings. Everything in front of you is an experience you're sharing — something you can bring up freely at any moment.");
            prompt.AppendLine("</companion_context>\n");
            return prompt.ToString();
        }

        internal static string BuildGreetingContext(Character character, DialogueContext context)
        {
            var prompt = new StringBuilder();
            var historyManager = DialogueHistoryManager.Instance;
            if (historyManager == null) return prompt.ToString();
            if (Game1.getPlayerOrEventFarmer()?.friendshipData?.TryGetValue(character.Name, out var fs) == true
                && (fs.IsMarried() || fs.IsRoommate()))
                return prompt.ToString();
            var history = historyManager.GetRecentHistory(character.Name, 1);
            if (history.Count == 0) return prompt.ToString();
            var lastEntry = history[0];
            var lastTime = lastEntry.Timestamp;
            var now = new StardewTime(Game1.year, (Season)Game1.season, Game1.dayOfMonth, Game1.timeOfDay);
            bool isToday = lastTime.Year == now.Year && lastTime.Season == now.Season
                           && lastTime.DayOfMonth == now.DayOfMonth;
            if (isToday) return prompt.ToString();
            int dayGap = (int)Math.Round(now.TotalDays - lastTime.TotalDays);
            prompt.AppendLine("<greeting_context>");
            if (dayGap == 1)
                prompt.AppendLine(Util.GetString(character, "greetingYesterday"));
            else if (dayGap <= 3)
                prompt.AppendLine(Util.GetString(character, "greetingFewDays", new { DayGap = dayGap }));
            else
                prompt.AppendLine(Util.GetString(character, "greetingLongTime", new { DayGap = dayGap }));
            prompt.AppendLine("</greeting_context>\n");
            return prompt.ToString();
        }

        internal static string BuildMarriageFeelings(Character character, DialogueContext context, string name)
        {
            var prompt = new StringBuilder();
            if (!Game1.getPlayerOrEventFarmer().friendshipData.TryGetValue(character.Name, out var marriageFriendship))
                return prompt.ToString();
            var IsRoommate = marriageFriendship.IsRoommate();
            var marriageOrRoommate = IsRoommate ? Util.GetString(character, "generalBeingRoommates") : Util.GetString(character, "generalTheMarriage");
            switch (context.Hearts)
            {
                case > 12:
                    prompt.AppendLine(Util.GetString(character, "marriageSentimentGood", new { Name = name, marriageOrRoommate = marriageOrRoommate }));
                    break;
                case < 10:
                    prompt.AppendLine(Util.GetString(character, "marriageSentimentBad", new { Name = name, marriageOrRoommate = marriageOrRoommate }));
                    break;
                default:
                    prompt.AppendLine(Util.GetString(character, "marriageSentimentNeutral", new { Name = name, marriageOrRoommate = marriageOrRoommate }));
                    break;
            }
            return prompt.ToString();
        }

        internal static string BuildChildren(Character character, DialogueContext context, Friendship friendship, string name)
        {
            var prompt = new StringBuilder();
            if (context.Children.Count == 0)
            {
                prompt.AppendLine(Util.GetString(character, "childrenNone", new { Name = name }));
            }
            else
            {
                if (context.Children.Count > 1)
                {
                    var count = context.Children.Count;
                    prompt.AppendLine(Util.GetString(character, "childrenMultiple", new { Name = name, count = count }));
                }
                else
                {
                    prompt.AppendLine(Util.GetString(character, "childrenSingle", new { Name = name }));
                }
                prompt.AppendLine();
                foreach (var child in context.Children)
                {
                    prompt.AppendLine($"- {Util.GetString(character, child.IsMale ? "childrenDescriptionBoy" : "childDescriptionGirl", new { Name = child.Name, Age = child.Age })}");
                }
            }
            if (friendship != null && friendship.DaysUntilBirthing > 0)
            {
                var daysUntilBirth = friendship.DaysUntilBirthing;
                prompt.AppendLine(Util.GetString(character, "childrenPregnant", new { Name = name, daysUntilBirth = daysUntilBirth }));
            }
            return prompt.ToString();
        }

        internal static string BuildTrinkets(Character character, string name)
        {
            var prompt = new StringBuilder();
            var trinkets = Game1.getPlayerOrEventFarmer().trinketItems;
            if (trinkets == null) return prompt.ToString();
            var nonNullTrinkets = trinkets.Where(x => x != null).ToList();
            if (!nonNullTrinkets.Any()) return prompt.ToString();
            if (nonNullTrinkets.Any(x => x.GetEffect() is FairyBoxTrinketEffect))
            {
                prompt.AppendLine(Util.GetString(character, "trinketsFairyBox", new { Name = name }));
            }
            else
            {
                var firstTrinketWithEffect = nonNullTrinkets.FirstOrDefault(x => x.GetEffect() is not null);
                if (firstTrinketWithEffect != null)
                {
                    TrinketEffect companionEffect = firstTrinketWithEffect.GetEffect() as TrinketEffect;
                    if (companionEffect?.Companion is StardewValley.Companions.HungryFrogCompanion)
                        prompt.AppendLine(Util.GetString(character, "trinketsCompanionFrog", new { Name = name }));
                    else if (companionEffect?.Companion is StardewValley.Companions.FlyingCompanion)
                        prompt.AppendLine(Util.GetString(character, "trinketsCompanionParrot", new { Name = name }));
                }
            }
            return prompt.ToString();
        }

        internal static string BuildSpouse(Character character, string name)
        {
            var prompt = new StringBuilder();
            bool isZh = ResolveIsChinese();
            // 比较必须用内部名（friendshipData 键恒为英文内部名），渲染必须本地化显示名
            string currentInternalName = character.Name;
            var spouses = Game1.getPlayerOrEventFarmer().friendshipData.FieldDict
                .Where(x => x.Value.Value.IsMarried() && !x.Value.Value.IsRoommate())
                .Select(x => x.Key)
                .ToList();
            bool talkingToSpouse = spouses.Any(x => string.Equals(x, currentInternalName, StringComparison.OrdinalIgnoreCase));
            var otherSpouseKeys = spouses.Where(x => !string.Equals(x, currentInternalName, StringComparison.OrdinalIgnoreCase)).ToList();
            var targetSpouseKeys = talkingToSpouse ? otherSpouseKeys : spouses;
            if (targetSpouseKeys.Any())
            {
                bool multipleOthers = targetSpouseKeys.Count > 1;
                var localizedSpouses = targetSpouseKeys.Select(NpcNameLocalizer.GetLocalizedName).ToList();
                string separator = isZh ? "、" : ", ";
                var spouseList = string.Join(separator, localizedSpouses);
                int nSpouses = targetSpouseKeys.Count;
                if (talkingToSpouse)
                {
                    var otherSpousesList = multipleOthers
                        ? $"{Util.GetString(character, "spousesNOtherPeople", new { nSpouses = nSpouses })} {spouseList}"
                        : localizedSpouses.FirstOrDefault() ?? "";
                    var otherSpousesReference = multipleOthers
                        ? Util.GetString(character, "spousesAllTheOthers")
                        : localizedSpouses.FirstOrDefault() ?? "";
                    prompt.AppendLine(Util.GetString(character, "spousesMarriedToOthers", new { Name = name, otherSpousesList = otherSpousesList, otherSpousesReference = otherSpousesReference }));
                }
                else
                {
                    if (multipleOthers)
                        prompt.AppendLine(Util.GetString(character, "spousesMarriedToMany", new { nSpouses = nSpouses, spouseList = spouseList, Name = name }));
                    else
                        prompt.AppendLine(Util.GetString(character, "spousesMarriedToOne", new { spouseList = spouseList, Name = name }));
                }
            }
            var roommates = Game1.getPlayerOrEventFarmer().friendshipData.FieldDict
                .Where(x => x.Value.Value.IsMarried() && x.Value.Value.IsRoommate())
                .Select(x => x.Key)
                .ToList();
            bool talkingToRoommate = roommates.Any(x => string.Equals(x, currentInternalName, StringComparison.OrdinalIgnoreCase));
            var otherRoommateKeys = roommates.Where(x => !string.Equals(x, currentInternalName, StringComparison.OrdinalIgnoreCase)).ToList();
            var targetRoommateKeys = talkingToRoommate ? otherRoommateKeys : roommates;
            if (targetRoommateKeys.Any())
            {
                bool multipleOthers = targetRoommateKeys.Count > 1;
                var localizedRoommates = targetRoommateKeys.Select(NpcNameLocalizer.GetLocalizedName).ToList();
                string separator = isZh ? "、" : ", ";
                var roommateList = multipleOthers
                    ? $"{Util.GetString(character, "spousesNOtherPeople", new { nSpouses = targetRoommateKeys.Count })} {string.Join(separator, localizedRoommates)}"
                    : localizedRoommates.FirstOrDefault() ?? "";
                var roommateReference = multipleOthers
                    ? Util.GetString(character, "spouseRoommatesAllTheOthers")
                    : localizedRoommates.FirstOrDefault() ?? "";
                if (talkingToRoommate)
                    prompt.AppendLine(Util.GetString(character, "spouseRoommatesWithOthers", new { Name = name, roommateList = roommateList, roommateReference = roommateReference }));
                else
                {
                    if (multipleOthers)
                        prompt.AppendLine(Util.GetString(character, "spouseRoommateWithMany", new { roommateList = roommateList }));
                    else
                        prompt.AppendLine(Util.GetString(character, "spouseRoommateWithOne", new { roommateList = roommateList }));
                }
            }
            var engaged = Game1.getPlayerOrEventFarmer().friendshipData.FieldDict
                .Where(x => x.Value.Value.IsEngaged())
                .ToList();
            var otherEngaged = engaged.Where(x => !string.Equals(x.Key, currentInternalName, StringComparison.OrdinalIgnoreCase)).ToList();
            if (otherEngaged.Any())
            {
                var engagedFirst = otherEngaged.First();
                var engagedTo = NpcNameLocalizer.GetLocalizedName(engagedFirst.Key);
                var weddingDays = engagedFirst.Value.Value.CountdownToWedding;
                prompt.AppendLine(Util.GetString(character, "spouseEngaged", new { engagedTo = engagedTo, weddingDays = weddingDays }));
            }
            var total = targetSpouseKeys.Count + engaged.Count;
            if (total > 1 && !talkingToSpouse && !talkingToRoommate)
                prompt.AppendLine(Util.GetString(character, "spousePoly", new { Name = name }));
            else if (total >= 1 && (talkingToSpouse || talkingToRoommate))
                prompt.AppendLine(Util.GetString(character, "spousePolyView", new { Name = name }));
            return prompt.ToString();
        }

        internal static string BuildNonSpouseFriendshipLevel(Character character, DialogueContext context, CharacterData npcData)
        {
            var prompt = new StringBuilder();
            int hearts = context.Hearts ?? 0;
            bool isSingle = npcData.CanBeRomanced;
            bool isChild = npcData.Age == NpcAge.Child;
            bool isDating = false;
            if (Game1.player?.friendshipData.TryGetValue(character.Name, out var fs) == true)
                isDating = fs.IsDating();
            string line = BuildFriendshipText(character, hearts, isSingle, isChild, isDating);
            if (!string.IsNullOrWhiteSpace(line))
                prompt.AppendLine(line);
            return prompt.ToString();
        }

        internal static string BuildFriendshipText(Character character, int hearts, bool isSingle, bool isChild, bool isDating)
        {
            string note = Util.GetString(character, "friendshipNote");
            if (hearts < 0)
                return Util.GetString(character, "friendshipFirstMeeting", new { Note = note });
            if (hearts < 2)
                return Util.GetString(character, "friendshipStrangers", new { Hearts = hearts, Note = note });
            if (hearts < 4)
                return Util.GetString(character, "friendshipNeighbors", new { Hearts = hearts, Note = note });
            if (hearts < 6)
                return Util.GetString(character, "friendshipFriends", new { Hearts = hearts, Note = note });
            if (hearts < 8)
                return Util.GetString(character, "friendshipCloseFriends", new { Hearts = hearts, Note = note });
            if (isChild)
                return Util.GetString(character, "friendshipChild8Plus", new { Hearts = hearts, Note = note });
            if (isSingle)
            {
                if (isDating)
                    return Util.GetString(character, "friendshipDating", new { Hearts = hearts, Note = note });
                return Util.GetString(character, "friendshipBestFriends", new { Hearts = hearts, Note = note });
            }
            return Util.GetString(character, "friendshipLongTermBond", new { Hearts = hearts, Note = note });
        }

        internal static string BuildRelationBase(
            Character character,
            DialogueContext context,
            CharacterData npcData,
            string name,
            string milestoneBlock,
            ContextFlags flags)
        {
            var prompt = new StringBuilder();
            bool npcIsMale = npcData.Gender == StardewValley.Gender.Male;

            Friendship friendship = null;
            Game1.getPlayerOrEventFarmer()?.friendshipData?.TryGetValue(character.Name, out friendship);
            bool isMarriedOrRoommate = friendship != null && (friendship.IsMarried() || friendship.IsRoommate());

            if (isMarriedOrRoommate)
            {
                if (friendship.IsRoommate())
                {
                    prompt.AppendLine(Util.GetString(character, "coreRoommates", new { Name = name }));
                }
                else
                {
                    prompt.AppendLine(Util.GetString(character, "coreMarried",
                        new { Name = name, Pronoun = npcIsMale ? "his" : "her" }));
                    prompt.Append(BuildChildren(character, context, friendship, name));
                }
                prompt.Append(BuildSpouse(character, name));
                if (flags?.IncludeFarmDetails == true)
                    prompt.Append(BuildTrinkets(character, name));
                prompt.Append(BuildMarriageFeelings(character, context, name));
            }
            else
            {
                prompt.Append(BuildNonSpouseFriendshipLevel(character, context, npcData));
                prompt.Append(BuildSpouse(character, name));
                prompt.Append(BuildSpecialRelationshipStatus(character, context, friendship, milestoneBlock, name));
            }

            return prompt.ToString();
        }

        internal static string BuildSpecialRelationshipStatus(Character character, DialogueContext context, Friendship friendship, string pendingMilestoneBlock, string name)
        {
            var prompt = new StringBuilder();
            if (friendship == null) return prompt.ToString();
            if (friendship.IsDating())
            {
                var relationshipPublic = context.Inlaw == null ? Util.GetString(character, "specialRelationshipDatingPublic") : Util.GetString(character, "specialRelationshipDatingDiscrete");
                var relationshipWord = BuildRelationshipWord(character, context.MaleFarmer);
                prompt.AppendLine(Util.GetString(character, "specialRelationshipDating", new { Name = name, relationshipPublic = relationshipPublic, relationshipWord = relationshipWord }));
            }
            if (friendship.IsEngaged() && string.IsNullOrEmpty(pendingMilestoneBlock))
            {
                var daysToWedding = friendship.CountdownToWedding;
                prompt.AppendLine(Util.GetString(character, "specialRelationshipEngaged", new { Name = name, daysToWedding = daysToWedding }));
            }
            if (friendship.IsDivorced())
                prompt.AppendLine(Util.GetString(character, "specialRelationshipDivorced", new { Name = name }));
            if (friendship.ProposalRejected)
                prompt.AppendLine(Util.GetString(character, "specialRelationshipProposalRejected", new { Name = name }));
            return prompt.ToString();
        }

        internal static string BuildRecentEvents(Character character, SerializableDictionary<string, int> allPreviousActivities)
        {
            var prompt = new StringBuilder();
            if (allPreviousActivities == null) return prompt.ToString();
            var eventSection = new StringBuilder();
            foreach (var activity in allPreviousActivities.Where(x => x.Value < 7))
            {
                var theLine = activity.Key switch
                {
                    "babyBoy" => Util.GetString(character, "recentEventsBabyBoy"),
                    "babyGirl" => Util.GetString(character, "recentEventsBabyGirl"),
                    "wedding" => Util.GetString(character, "recentEventsMarried"),
                    "Characters_MovieInvite_Invited" => Util.GetString(character, "recentEventsMovieInvited", new { Name = character.Name }),
                    "DumpsterDiveComment" => Util.GetString(character, "recentEventsDumpsterDive", new { Name = character.Name }),
                    _ => $""
                };
                if (!string.IsNullOrWhiteSpace(theLine))
                    eventSection.AppendLine(theLine);
            }
            if (eventSection.Length > 0)
            {
                prompt.AppendLine($"## {Util.GetString(character, "recentEventsHeading")}");
                prompt.AppendLine(Util.GetString(character, "recentEventsIntro"));
                prompt.AppendLine(eventSection.ToString());
            }
            return prompt.ToString();
        }

        internal static string BuildSpecialDatesAndBirthday(Character character, DialogueContext context, string name)
        {
            var prompt = new StringBuilder();
            if (context.DayOfSeason == null || context.Season == null) return prompt.ToString();
            prompt.AppendLine((context.Season, context.DayOfSeason) switch
            {
                (Season.Spring, 1) => Util.GetString(character, "specialDatesSpring1"),
                (Season.Spring, 12) => Util.GetString(character, "specialDatesSpring12"),
                (Season.Spring, 23) => Util.GetString(character, "specialDatesSpring23"),
                (Season.Summer, 1) => Util.GetString(character, "specialDatesSummer1"),
                (Season.Summer, 10) => Util.GetString(character, "specialDatesSummer10"),
                (Season.Summer, 27) => Util.GetString(character, "specialDatesSummer27"),
                (Season.Summer, 28) => Util.GetString(character, "specialDatesSummer28"),
                (Season.Fall, 1) => Util.GetString(character, "specialDatesFall1"),
                (Season.Fall, 15) => Util.GetString(character, "specialDatesFall15"),
                (Season.Fall, 26) => Util.GetString(character, "specialDatesFall26"),
                (Season.Winter, 1) => Util.GetString(character, "specialDatesWinter1"),
                (Season.Winter, 7) => Util.GetString(character, "specialDatesWinter7"),
                (Season.Winter, 24) => Util.GetString(character, "specialDatesWinter24"),
                (Season.Winter, 28) => Util.GetString(character, "specialDatesWinter28"),
                _ => $""
            });
            var stardewBioData = character.StardewNpc.GetData();
            if (string.Equals(context.Season.Value.ToString(), stardewBioData.BirthSeason.ToString(), StringComparison.InvariantCultureIgnoreCase)
                && context.DayOfSeason == stardewBioData.BirthDay)
                prompt.AppendLine(Util.GetString(character, "specialDatesBirthday", new { Name = name }));
            return prompt.ToString();
        }

        internal static string BuildSpouseAction(Character character, DialogueContext context, string name)
        {
            var prompt = new StringBuilder();
            if (context.Accept != null) return prompt.ToString();
            if (context.SpouseAct == null) return prompt.ToString();
            prompt.AppendLine(context.SpouseAct switch
            {
                SpouseAction.funLeave => Util.GetString(character, "spouseActionFunLeave", new { Name = name }),
                SpouseAction.jobLeave => Util.GetString(character, "spouseActionJobLeave", new { Name = name }),
                SpouseAction.patio => Util.GetString(character, "spouseActionPatio", new { Name = name }),
                SpouseAction.funReturn => Util.GetString(character, "spouseActionFunReturn", new { Name = name }),
                SpouseAction.jobReturn => Util.GetString(character, "spouseActionJobReturn", new { Name = name }),
                SpouseAction.spouseRoom => Util.GetString(character, "spouseActionSpouseRoom", new { Name = name }),
                _ => $""
            });
            return prompt.ToString();
        }

        // ── Tier 2b ──
        internal static string BuildPreoccupation(Character character, DialogueContext context, List<string> injectedPrivateThoughts, string name)
        {
            var prompt = new StringBuilder();
            if (ModEntry.Config.EnableEmotionSystem && character?.CurrentTodayScene != null) return prompt.ToString();
            bool playerHasSpoken = context.ChatHistory.Any(x => x.IsPlayerLine);
            if (playerHasSpoken) return prompt.ToString();
            if (Game1.random.NextDouble() < 0.5) return prompt.ToString();
            var npc = character?.StardewNpc;
            var entry = ProgressStateResolver.ResolveActiveEntry(npc, character.Bio?.ProgressStates);
            bool useStage = entry?.Preoccupations != null && entry.Preoccupations.Count > 0;
            List<string> pool;
            string stageKey;
            if (useStage)
            {
                pool = entry.Preoccupations;
                stageKey = string.Join("|", entry.Preoccupations);
            }
            else
            {
                pool = character.PossiblePreoccupations;
                stageKey = "GLOBAL";
            }
            if (pool == null || pool.Count == 0) return prompt.ToString();
            bool isZh = IsZh();
            string preoccupation;
            if (Game1.Date == character.PreoccupationDate
                && !string.IsNullOrEmpty(character.Preoccupation)
                && string.Equals(character.PreoccupationStageKey, stageKey, StringComparison.Ordinal))
            {
                preoccupation = LoadLocalised(character.Preoccupation);
            }
            else
            {
                string pick = pool[Game1.random.Next(pool.Count)];
                if (string.IsNullOrWhiteSpace(pick)) return prompt.ToString();
                preoccupation = LoadLocalised(pick);
                if (useStage && isZh)
                    preoccupation = NpcNameLocalizer.LocalizeNamesInText(preoccupation);
                character.Preoccupation = preoccupation;
                character.PreoccupationDate = Game1.Date;
                character.PreoccupationStageKey = stageKey;
            }
            injectedPrivateThoughts.Add(preoccupation);

            // 结构化隐秘容器：彻底防止潜意识外泄与幻觉抢答
            prompt.AppendLine("<inner_preoccupation status=\"confidential\">");
            prompt.AppendLine(Util.GetString(character, "preoccupation", new { Name = name, preoccupation = preoccupation }));
            prompt.AppendLine(isZh
                ? "[VISIBILITY] 这是你内心未说出口的思绪，农夫不知晓。当你在对白中主动说出后，它才进入农夫已知的范围。"
                : "[VISIBILITY] This is your unspoken inner thought; the farmer is unaware of it. It enters the farmer's knowledge only after you voice it in dialogue.");
            prompt.AppendLine("</inner_preoccupation>\n");
            return prompt.ToString();
        }

        internal static string BuildGift(Character character, string giveGift, string name)
        {
            var prompt = new StringBuilder();
            if (!string.IsNullOrEmpty(giveGift))
            {
                string giftName = giveGift;
                if (Game1.objectData.ContainsKey(giveGift))
                {
                    giftName = Game1.objectData[giveGift].DisplayName;
                    giftName = LoadLocalised(giftName);
                }
                prompt.AppendLine(Util.GetString(character, "giftGiving", new { Name = name, GiftName = giftName }));
            }
            return prompt.ToString();
        }

        internal static string BuildInteractionState(Character character, DialogueContext context, ContextFlags flags, string name, bool isMarriedOrRoommate)
        {
            var prompt = new StringBuilder();
            bool hasNoPlayerInput = context == null || (!context.IsActiveTurn && context.Accept == null);
            if (!hasNoPlayerInput && flags?.IncludeShortTermContext == true)
            {
                prompt.AppendLine("<interaction_state>");
                prompt.AppendLine(Util.GetString(character, "interactionOngoingState"));
                prompt.AppendLine(Util.GetString(character, "interactionOngoingGoal"));
                prompt.AppendLine("</interaction_state>\n");
            }
            else if (hasNoPlayerInput && flags?.IsSimpleGreeting != true)
            {
                prompt.AppendLine("<interaction_state>");
                string approachingKey = isMarriedOrRoommate ? "interactionApproachingSpouse" : "interactionApproaching";
                prompt.AppendLine(Util.GetString(character, approachingKey));
                prompt.AppendLine("</interaction_state>\n");
            }
            return prompt.ToString();
        }

        internal static string BuildJealousyTrigger(Character character, ContextFlags flags, bool isZh)
        {
            var prompt = new StringBuilder();
            if (ModEntry.Config.EnableDateSystem && flags?.IsJealousy == true && DateManager.Instance != null)
            {
                var dateNpcDisp = NpcNameLocalizer.GetLocalizedName(DateManager.Instance.ActiveDateNpcName);
                prompt.AppendLine("<jealousy_trigger>");
                prompt.AppendLine(isZh
                    ? $"你注意到农夫今晚已经与 {dateNpcDisp} 有约。"
                    : $"You notice the farmer already has plans with {dateNpcDisp} tonight.");
                prompt.AppendLine("</jealousy_trigger>\n");
            }
            return prompt.ToString();
        }

        internal static string BuildDateInvitationProtocol(ContextFlags flags)
        {
            var sb = new StringBuilder();
            if (!ModEntry.Config.EnableDateSystem || flags?.IsInviteRequested != true || flags?.IsOnDate == true)
                return sb.ToString();
            bool isZh = IsZh();
            bool isFestivalToday = Utility.isFestivalDay(Game1.dayOfMonth, Game1.season);
            sb.AppendLine("<date_invitation_protocol>");
            sb.AppendLine(isZh ? "农夫正在向你发起今晚的约会邀请。" : "The farmer is inviting you out on a date tonight.");
            if (isFestivalToday)
            {
                sb.AppendLine(isZh
                    ? "- 冲突拒绝: 今天是节日，日程有冲突。在台词中委婉说明改天再约，并在最末尾附加 [UI:DATE_INVITE_REJECT]。"
                    : "- CONFLICT DECLINE: Festival today. Explain in dialogue that you'll reschedule, AND append [UI:DATE_INVITE_REJECT] at the absolute end.");
            }
            else
            {
                sb.AppendLine(isZh
                    ? "- 若同意赴约: 在台词最末尾附加内部标签 [UI:DATE_INVITE]。"
                    : "- IF ACCEPTING: Append [UI:DATE_INVITE] at the absolute end.");
                sb.AppendLine(isZh
                    ? "- 若拒绝赴约: 只输出对白文本，不加标签。"
                    : "- IF DECLINING: Dialogue text only, no tags.");
            }
            sb.AppendLine("</date_invitation_protocol>\n");
            return sb.ToString();
        }

        internal static string BuildFollowInvitationProtocol(Character character, ContextFlags flags)
        {
            var sb = new StringBuilder();
            if (flags == null || flags.RequestedAction != ActionTag.Follow || flags.IsFollowing)
                return sb.ToString();
            bool isZh = IsZh();
            if (MovementManager.Instance != null
                && MovementManager.Instance.HasActiveFollow
                && MovementManager.Instance.CurrentFollowingNpc?.Name != character?.Name)
            {
                sb.AppendLine("<follow_unavailable>");
                sb.AppendLine(isZh
                    ? "- 事实: 农夫身边已有其他同伴随行，你现在无法加入同行。"
                    : "- Fact: The farmer already has another companion with them. You cannot join right now.");
                sb.AppendLine(isZh ? "- 只输出对白文本，不加标签。" : "- Dialogue text only, no tags.");
                sb.AppendLine("</follow_unavailable>\n");
                return sb.ToString();
            }
            sb.AppendLine("<follow_invitation_protocol>");
            sb.AppendLine(isZh ? "农夫正在邀请你与他同行。" : "The farmer is inviting you to come along.");
            sb.AppendLine(isZh ? "- 若同意: 在台词最末尾附加内部标签 [UI:FOLLOW]。" : "- IF ACCEPTING: Append [UI:FOLLOW] at the absolute end.");
            sb.AppendLine(isZh ? "- 若拒绝: 只输出对白文本，不加标签。" : "- IF DECLINING: Dialogue text only, no tags.");
            sb.AppendLine("</follow_invitation_protocol>\n");
            return sb.ToString();
        }

        internal static string BuildDateEndingProtocol(ContextFlags flags)
        {
            var sb = new StringBuilder();
            if (flags?.IsOnDate != true) return sb.ToString();
            bool isZh = IsZh();
            sb.AppendLine("<date_ending_protocol>");
            sb.AppendLine(isZh
                ? "- 若玩家提出结束今天的约会或向你道别（例如\"今天就到这吧\"\"我先回去\"）：在台词最末尾附加内部标签 [ACTION:END_DATE]。"
                : "- IF THE PLAYER PROPOSES ENDING TODAY'S DATE OR SAYS GOODBYE: Append [ACTION:END_DATE] at the absolute end.");
            sb.AppendLine(isZh
                ? "- 若玩家未提及结束约会：保持正常对话，仅输出纯对白文本。"
                : "- OTHERWISE: Continue regular dialogue as plain text only.");
            sb.AppendLine("</date_ending_protocol>\n");
            return sb.ToString();
        }

        internal static string BuildMovementInstruction(Character character, ContextFlags flags, bool moveApplicable)
        {
            var prompt = new StringBuilder();
            if (!moveApplicable) return prompt.ToString();
            bool isZh = IsZh();
            if (flags.RequestedAction == ActionTag.Follow && !flags.IsFollowing)
            {
                prompt.Append(BuildFollowInvitationProtocol(character, flags));
                return prompt.ToString();
            }
            if (flags.RequestedAction == ActionTag.StopFollow)
            {
                prompt.AppendLine("<movement_instruction mode=\"stop_follow\">");
                prompt.AppendLine(isZh ? "农夫希望你停止跟随。" : "The farmer wants you to stop following.");
                prompt.AppendLine(isZh ? "- 若同意停止: 在台词最末尾附加 [ACTION:STOP_FOLLOW]。" : "- IF ACCEPTING: Append [ACTION:STOP_FOLLOW] at the absolute end.");
                prompt.AppendLine(isZh ? "- 若想继续跟随: 只输出对白文本，不加标签。" : "- IF DECLINING: Dialogue text only, no tags.");
                prompt.AppendLine("</movement_instruction>\n");
                return prompt.ToString();
            }
            if (flags.RequestedAction == ActionTag.StayHome)
            {
                prompt.AppendLine("<movement_instruction mode=\"stay_home\">");
                prompt.AppendLine(isZh ? "农夫希望你今天留在家里，不要出门。" : "The farmer wants you to stay home today.");
                prompt.AppendLine(isZh ? "- 若同意: 在台词最末尾附加 [ACTION:STAY_HOME]。" : "- IF ACCEPTING: Append [ACTION:STAY_HOME] at the absolute end.");
                prompt.AppendLine(isZh ? "- 若拒绝: 只输出对白文本，不加标签。" : "- IF DECLINING: Dialogue text only, no tags.");
                prompt.AppendLine("</movement_instruction>\n");
                return prompt.ToString();
            }
            if (flags.RequestedAction == ActionTag.AllDayFollow)
            {
                prompt.AppendLine("<movement_instruction mode=\"all_day_follow\">");
                prompt.AppendLine(isZh ? "农夫希望你今天一整天都陪着他。" : "The farmer wants you to accompany them all day.");
                prompt.AppendLine(isZh ? "- 若同意: 在台词最末尾附加 [ACTION:ALL_DAY_FOLLOW]。" : "- IF ACCEPTING: Append [ACTION:ALL_DAY_FOLLOW] at the absolute end.");
                prompt.AppendLine(isZh ? "- 若拒绝: 只输出对白文本，不加标签。" : "- IF DECLINING: Dialogue text only, no tags.");
                prompt.AppendLine("</movement_instruction>\n");
                return prompt.ToString();
            }
            if (flags.IsFollowing && !flags.IsMovementRequested)
            {
                prompt.AppendLine("<movement_instruction mode=\"following\">");
                prompt.AppendLine(isZh ? "你此刻正跟在农夫身边。" : "You're currently walking along with the farmer.");
                prompt.AppendLine("</movement_instruction>\n");
                return prompt.ToString();
            }
            if (flags.IsGotoRequested)
            {
                string assetList = EnvironmentScanner.FormatAssetsForGotoPrompt(character.StardewNpc, radiusTiles: 15);
                prompt.AppendLine("<movement_instruction mode=\"navigation\">");
                prompt.AppendLine(isZh ? "玩家正要求你走去特定地点或拿取物品。" : "The player is asking you to walk to a specific location or interact with an object.");
                prompt.AppendLine(assetList);
                prompt.AppendLine(isZh ? $"玩家请求: \"{flags.GotoIntentText}\"" : $"Player Request: \"{flags.GotoIntentText}\"");
                prompt.AppendLine(isZh
                    ? "- 匹配成功: 文字回应，并在台词最末尾附带 [ACTION:GOTO:x,y]（使用列表中精准坐标）。"
                    : "- MATCH FOUND: Respond AND append [ACTION:GOTO:x,y] at the end using exact coordinates.");
                prompt.AppendLine(isZh
                    ? "- 未能匹配: 说明找不到该物品，结束输出。"
                    : "- NO MATCH: Explain that you cannot find it as regular dialogue.");
                prompt.AppendLine("</movement_instruction>\n");
                return prompt.ToString();
            }
            if (flags.IsPathBlocked)
            {
                prompt.AppendLine("<movement_instruction mode=\"blocked\">");
                string dirStr = LocalizeDirection(flags.BlockDirection, isZh);
                prompt.AppendLine(isZh
                    ? $"向{dirStr}移动的路径被障碍物阻挡。在对白中指出前方阻碍并明确说明无法过去。"
                    : $"Path to the {dirStr} is BLOCKED. Acknowledge the barrier and state clearly that you cannot proceed.");
                prompt.AppendLine("</movement_instruction>\n");
                return prompt.ToString();
            }
            if (flags.IsAlreadyAdjacent)
            {
                prompt.AppendLine("<movement_instruction mode=\"adjacent\">");
                prompt.AppendLine(isZh ? "你此刻已经站在农夫正前方。" : "You're already standing face-to-face with the farmer.");
                prompt.AppendLine("</movement_instruction>\n");
                return prompt.ToString();
            }
            prompt.AppendLine("<movement_instruction mode=\"move_request\">");
            prompt.AppendLine(isZh ? "玩家要求你移动，前方路径畅通。" : "The player asked you to move. The path is CLEAR.");
            prompt.AppendLine(isZh
                ? "- 若同意: 将对应动作标签（如 [ACTION:STEP:FORWARD]、[ACTION:STEP:LEFT]）置于台词最末尾。"
                : "- IF ACCEPTING: Place the action tag (e.g. [ACTION:STEP:FORWARD], [ACTION:STEP:LEFT]) at the absolute end.");
            prompt.AppendLine(isZh
                ? "- 若拒绝: 保持纯口头对白说明缘由，结束输出。"
                : "- IF DECLINING: Provide pure conversational reasoning as regular dialogue.");
            prompt.AppendLine("</movement_instruction>\n");
            return prompt.ToString();
        }

        // ── Tier 2a ──
        internal static string BuildCurrentConversation(Character character, DialogueContext context, ContextFlags flags, HashSet<string> emittedBlockKeys, string name)
        {
            var prompt = new StringBuilder();
            // VT-CONTEXT-01：本次装配的玩家身份名（只解析一次，循环内复用）
            string promptPlayerName = Prompts.ResolvePromptPlayerName(character);
            if (context.ChatHistory.Any())
            {
                prompt.Append(PromptsBlocks.BuildConversationHeading(character));
                emittedBlockKeys.Add("CurrentConversation");
                prompt.AppendLine(Util.GetString(character, "currentConversationIntro", new { Name = name }));
                bool shortCtxAllowed = flags?.IncludeShortTermContext != false;
                int configured = Math.Clamp(ModEntry.Config?.PromptHistoryWindow ?? 6, 1, 20);
                int window = Math.Clamp(shortCtxAllowed ? configured : 1, 1, Math.Max(1, context.ChatHistory.Count));
                var visible = context.ChatHistory.Count > window
                    ? context.ChatHistory.GetRange(context.ChatHistory.Count - window, window)
                    : context.ChatHistory;
                foreach (var elem in visible)
                {
                    string timePrefix = string.IsNullOrEmpty(elem.FuzzyTime) ? "" : $"[{elem.FuzzyTime}] ";
                    // VT-CONTEXT-01：玩家历史行以当前玩家名标注；NPC 标签与格式不变。
                    // 会话边界标记独占一行，保留时间前缀、不带说话人标签。
                    prompt.AppendLine(elem.IsSessionMarker
                        ? $"- {timePrefix}{elem.Text}"
                        : elem.IsPlayerLine
                            ? $"- {timePrefix}{promptPlayerName}: {elem.Text}"
                            : $"- {timePrefix}{name}: {elem.Text}");
                }
            }
            else if (character.SpokeJustNow())
            {
                prompt.Append(PromptsBlocks.BuildConversationHeading(character));
                emittedBlockKeys.Add("CurrentConversation");
                prompt.AppendLine(Util.GetString(character, "currentConversationJustSpoke", new { Name = name }));
            }
            return prompt.ToString();
        }

        /// <summary>
        /// 选择会话衔接的历史轮次（与 BuildSessionContinuity 共享同一筛选语义）。
        /// 返回最多 3 个最早的历史 ConversationElement（已做同日校验、去重、跳过最近 2 轮）。
        /// </summary>
        internal static List<ConversationElement> GetContinuityTurns(Character character, DialogueContext context)
        {
            var session = SessionCache.Instance.GetOrCreate(character.Name);
            if (session.RecentTurns.Count == 0) return new List<ConversationElement>();
            if (StardewModdingAPI.Context.IsWorldReady &&
                (session.LastUpdatedYear != Game1.year ||
                 session.LastUpdatedSeason != (Season)Game1.season ||
                 session.LastUpdatedDay != Game1.dayOfMonth))
                return new List<ConversationElement>();
            string playerName = Game1.player?.Name ?? "";
            string Normalize(string s)
            {
                if (string.IsNullOrWhiteSpace(s)) return "";
                string clean = s;
                if (!string.IsNullOrEmpty(playerName))
                    clean = clean.Replace(playerName, "");
                return Regex.Replace(clean, @"[\s\$\#\@\{\}\[\]\(\)]", "");
            }
            var currentNorms = context.ChatHistory
                .Select(x => Normalize(x.Text))
                .Where(x => !string.IsNullOrEmpty(x))
                .ToHashSet();
            var candidateTurns = session.RecentTurns
                .Where(t => !string.IsNullOrWhiteSpace(t.Text) && !currentNorms.Contains(Normalize(t.Text)))
                .ToList();
            if (context.ChatHistory.Any() && candidateTurns.Count > 0)
            {
                int skipRecentCount = Math.Min(2, candidateTurns.Count);
                candidateTurns = candidateTurns.Take(candidateTurns.Count - skipRecentCount).ToList();
            }
            if (candidateTurns.Count == 0) return new List<ConversationElement>();
            return candidateTurns.TakeLast(3).ToList();
        }

        internal static string BuildSessionContinuity(Character character, DialogueContext context, bool isZh)
        {
            var previousTurns = GetContinuityTurns(character, context);
            if (previousTurns.Count == 0) return string.Empty;

            // VT-CONTEXT-01：本次装配的玩家身份名（只解析一次，循环内复用）
            string promptPlayerName = Prompts.ResolvePromptPlayerName(character);
            var session = SessionCache.Instance.GetOrCreate(character.Name);
            var prompt = new StringBuilder();
            prompt.AppendLine("<continuity_context>");
            prompt.AppendLine(isZh ? "### [早先会话衔接参考]" : "### [EARLIER SESSION CONTINUITY]");
            prompt.AppendLine(isZh
                ? "[REFERENCE_ONLY] 当日早先对话记录，仅用于保持人物记忆与逻辑连贯："
                : "[REFERENCE_ONLY] Earlier conversation turns today, provided solely for conversational consistency:");
            foreach (var turn in previousTurns)
            {
                // VT-CONTEXT-01：玩家历史行使用当前玩家名；NPC 标签与当前会话保持同一本地化显示名。
                string npcLabel = character.StardewNpc?.displayName ?? NpcNameLocalizer.GetLocalizedName(character.Name);
                string label = turn.IsPlayerLine ? promptPlayerName : npcLabel;
                string timePrefix = string.IsNullOrEmpty(turn.FuzzyTime) ? "" : $"[{turn.FuzzyTime}] ";
                prompt.AppendLine($"- {timePrefix}{label}: {turn.Text}");
            }
            if (!string.IsNullOrEmpty(session.EmotionalTone))
            {
                prompt.AppendLine(isZh
                    ? $"[MOOD_CONSISTENCY] 前置情绪基调: {session.EmotionalTone}。确保语气演变符合心理过渡规律。"
                    : $"[MOOD_CONSISTENCY] Prior emotional tone: {session.EmotionalTone}. Ensure tonal transition remains psychologically coherent.");
            }
            prompt.AppendLine("</continuity_context>\n");
            return prompt.ToString();
        }

        internal static string BuildPendingTopic(Character character, string name, List<string> injectedPrivateThoughts)
        {
            var prompt = new StringBuilder();
            string pending = PendingTopicManager.Instance.ConsumePendingTopic(character.Name);
            if (string.IsNullOrEmpty(pending)) return prompt.ToString();
            string playerName = Game1.player?.Name ?? "Farmer";
            string farmName = Game1.player?.farmName?.Value ?? "Farm";
            pending = pending.Replace("@", playerName).Replace("%farmer", playerName).Replace("%farm", farmName);
            injectedPrivateThoughts.Add(pending);
            prompt.AppendLine("<pending_thought>");
            prompt.AppendLine(Util.GetString(character, "pendingTopicIntro"));
            prompt.AppendLine(pending);
            prompt.AppendLine(Util.GetString(character, "pendingTopicOutro"));
            prompt.AppendLine("</pending_thought>\n");
            return prompt.ToString();
        }
    }
}