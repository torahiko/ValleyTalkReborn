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
    /// 统一语言解析，支持用户配置覆盖回退。
    /// </summary>
    internal static bool ResolveIsChinese()
    {
        if (!string.IsNullOrEmpty(ModEntry.Language))
        {
            return ModEntry.Language.StartsWith("zh", StringComparison.OrdinalIgnoreCase)
                || ModEntry.Language.IndexOf("chinese", StringComparison.OrdinalIgnoreCase) >= 0;
        }
        return LocalizedContentManager.CurrentLanguageCode.ToString().StartsWith("zh", StringComparison.OrdinalIgnoreCase);
    }

    private bool IsChineseLanguage => ResolveIsChinese();

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

    // VT3-D Δ2: 可变访问器——director 经此将 BuildPreoccupation/BuildPendingTopic 的 side-effect 写回
    // 同一 List 实例，使 ProcessLines 反泄漏过滤（LlmDialogueService.cs:580-589）仍可达。禁止防御性拷贝。
    internal List<string> InjectedPrivateThoughtsMutable => _injectedPrivateThoughts;

    private string _command;
    public string Command { get => _command ??= GetCommand(); internal set => _command = value; }

    private string _responseStart;
    public string ResponseStart { get => _responseStart ??= GetResponseStart(); internal set => _responseStart = value; }

    private string _instructions;
    public string Instructions { get => _instructions ??= GetInstructions(InstructionsBranch.Normal); internal set => _instructions = value; }

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
            if (IsChineseLanguage)
            {
                bio = NpcNameLocalizer.LocalizeNamesInText(bio);
            }
            npcConstantPrompt.AppendLine(bio);

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

            if (Character.Bio.Relationships?.Any() ?? false)
            {
                int currentHearts = Context.Hearts ?? 0;
                var dailyPicks = RelationshipRotationResolver.SelectDailyRelationships(
                    Character.Name, Character.Bio.Relationships, currentHearts);
                if (dailyPicks.Count > 0)
                {
                    npcConstantPrompt.AppendLine($"## {Util.GetString("biographyRelationships")}:");
                    foreach (var relationship in dailyPicks)
                    {
                        string heading = relationship.Heading;
                        string desc = relationship.Description;
                        if (IsChineseLanguage)
                        {
                            heading = NpcNameLocalizer.GetZhName(heading);
                            desc = NpcNameLocalizer.LocalizeNamesInText(desc);
                        }
                        npcConstantPrompt.AppendLine($"* **{heading}**: {desc}");
                    }
                }
            }
            npcConstantPrompt.AppendLine(Character.Bio.BiographyEnd);
        }
        return npcConstantPrompt.ToString();
    }

    /// <summary>Tier 1 拼装序列（11 项，顺序固定）。</summary>
    private static readonly IReadOnlyList<string> Tier1BlockSequence = new[]
    {
        Tier1BlockIds.GameState, Tier1BlockIds.EventHistory, Tier1BlockIds.BranchTheme,
        Tier1BlockIds.Scene, Tier1BlockIds.CompanionFocus, Tier1BlockIds.GreetingContext,
        Tier1BlockIds.RelationBase, Tier1BlockIds.RecentEvents, Tier1BlockIds.SpecialDates,
        Tier1BlockIds.SpouseAction, Tier1BlockIds.EvolvedTraits,
    };

    /// <summary>Tier 2b 拼装序列（17 项，顺序固定）。</summary>
    private static readonly IReadOnlyList<string> Tier2bBlockSequence = new[]
    {
        Tier2bBlockIds.Gossip, Tier2bBlockIds.Interaction, Tier2bBlockIds.Jealousy, Tier2bBlockIds.Preoccupation,
        Tier2bBlockIds.PendingTopic, Tier2bBlockIds.Gift, Tier2bBlockIds.Milestone,
        Tier2bBlockIds.Echo, Tier2bBlockIds.Eavesdrop, Tier2bBlockIds.SpouseWaiting,
        Tier2bBlockIds.LocalPerception, Tier2bBlockIds.Emotion, Tier2bBlockIds.PlayerProfile,
        Tier2bBlockIds.DateInvite, Tier2bBlockIds.FollowProto, Tier2bBlockIds.DateEndProto,
        Tier2bBlockIds.Movement,
    };

    public void AssembleCore(InjectionPlan plan, DialogueContext context, Character character)
    {
        if (plan == null)
            throw new ArgumentNullException(nameof(plan));

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
        prompt.Append(PromptsBlocks.BuildSessionContinuity(character, context, IsChineseLanguage));
        prompt.Append(PromptsBlocks.BuildCurrentConversation(character, context, CurrentFlags, _emittedBlockKeys, Name));
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
            commandPrompt.AppendLine("- 所有控制标签必须统一置于台词对白的最末尾，禁止穿插在句子中间。");
            commandPrompt.AppendLine("- 多标签排布统一流水线：[台词正文] [肖像标记(如$h)] [ACTION标签] [UI标签] [MOOD标签]");
            commandPrompt.AppendLine("  示例：\"好的，我们走吧。\" $h [ACTION:FACE:FARMER] [UI:FOLLOW] [MOOD:happy]");
            commandPrompt.AppendLine("[NEGATIVE_CONSTRAINT] 严禁在对白台词中输出、提及或解释上述任何系统指令标签。");
        }
        else
        {
            commandPrompt.AppendLine("- Emote bubbles (head animation): [ACTION:EMOTE:HAPPY], [ACTION:EMOTE:HEART], [ACTION:EMOTE:BLUSH], [ACTION:EMOTE:SURPRISE], [ACTION:EMOTE:SAD], [ACTION:EMOTE:ANGRY]");
            commandPrompt.AppendLine("- Facing direction: [ACTION:FACE:FARMER] (look at player), [ACTION:FACE:UP], [ACTION:FACE:DOWN], [ACTION:FACE:LEFT], [ACTION:FACE:RIGHT]");
            commandPrompt.AppendLine("- Movement steps: [ACTION:STEP:FORWARD], [ACTION:STEP:BACKWARD], [ACTION:STEP:LEFT], [ACTION:STEP:RIGHT]");
            commandPrompt.AppendLine();
            commandPrompt.AppendLine("[OUTPUT_FORMAT_RULE]");
            commandPrompt.AppendLine("- All control tags MUST be placed at the absolute end of the dialogue line. NEVER inline tags inside sentences.");
            commandPrompt.AppendLine("- Multi-tag sequence pipeline: [Dialogue] [Portrait token(e.g. $h)] [ACTION tag] [UI tag] [MOOD tag]");
            commandPrompt.AppendLine("  Example: \"Sure, let's head out.\" $h [ACTION:FACE:FARMER] [UI:FOLLOW] [MOOD:happy]");
            commandPrompt.AppendLine("[NEGATIVE_CONSTRAINT] Strictly forbid outputting, mentioning, or explaining any action/system tags in character dialogue.");
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
        instructions.AppendLine(Util.GetString(Character, "instructionsBreaks"));
        instructions.AppendLine(Util.GetString(Character, "instructionsSingleLine"));
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
            ? "- [CORE_PERSPECTIVE] 仅输出你自身角色的言语、动作与神态反应。完成当前台词表达后立即停下，交出对话回合，绝不代替玩家说话或行动。"
            : "- [CORE_PERSPECTIVE] Output only your own character's speech, actions, and reactions. Yield the dialogue turn immediately after your line; never generate actions or speech for the farmer.");
        instructions.AppendLine(isZh
            ? "- [IMMEDIATE_RESPONSE_RULE] 农夫刚说的话具有最高注意力优先级。第一句话必须直接承接、回复农夫的输入；完成直接反馈后，方可顺承个人日常思绪或生活事项。"
            : "- [IMMEDIATE_RESPONSE_RULE] The farmer's latest words have top attention priority. Your very first sentence MUST directly address or answer their input before transitioning to personal thoughts or ambient chores.");
        instructions.AppendLine(isZh
            ? "- [FAMILIARITY_BOUNDARIES] 人际边界与抗性随熟悉度动态分流。面对突兀、无用或不合心意之事展现市井本能：对生人保持明确边界感与干脆拒收；对密友与配偶展现防备卸除的熟稔调侃、日常嗔怪或叹气。禁止机械客套与 AI 客服腔。"
            : "- [FAMILIARITY_BOUNDARIES] Scale interpersonal distance and emotional friction to relationship depth. Reject unwanted things with raw everyday instinct: maintain crisp boundaries and blunt refusals with acquaintances; display effortless banter, comfortable teasing, or casual exasperation with spouses and close friends. Avoid robotic politeness.");
        instructions.AppendLine(isZh
            ? "- [MOOD_SHIFT_RULE] 若本次交流导致你的情绪基调发生明显转换（如转为好奇/烦躁/欣喜），在台词末尾附带 [MOOD:curious] / [MOOD:annoyed] / [MOOD:happy] 等标签。"
            : "- [MOOD_SHIFT_RULE] If this turn causes a distinct emotional shift (e.g., to curious, annoyed, happy), append [MOOD:curious] / [MOOD:annoyed] / [MOOD:happy] at the end of the line.");

        if (enableResponses)
        {
            instructions.AppendLine(isZh
                ? "- [SUGGESTION_SCOPE_RULE] 以 '%' 开头的发言选项，取材范围仅严格限于你已经在本次台词中亲口说出的信息。未说出口的潜意识、私密挂念与偷听内容严禁作为选项线索。"
                : "- [SUGGESTION_SCOPE_RULE] Any suggested response options prefixed with '%' MUST draw solely from what you have explicitly voiced aloud. Unspoken inner thoughts and overheard gossip are strictly excluded until you voice them.");
        }
        else
        {
            instructions.AppendLine(isZh
                ? "- [NEGATIVE_CONSTRAINT] 绝对禁止输出任何以 '%' 开头的玩家选项、分支回答或格式解释。"
                : "- [NEGATIVE_CONSTRAINT] Strictly forbid outputting any player options, branches, or formatting notes prefixed with '%'.");
        }

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
                _ => raw
            };
        }

        internal static bool MovementInstructionApplicable(ContextFlags flags)
        {
            return !(flags?.IsOnDate == true)
                && !(flags?.IsJealousy == true)
                && ((flags?.IsActionRequested ?? false) || (flags?.IsFollowing ?? false));
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
                        prompt.AppendLine(isZh
                            ? $"- 当前地点：{DateManager.Instance.ActiveDateLocation ?? ""}"
                            : $"- Location: {DateManager.Instance.ActiveDateLocation ?? ""}");
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
            string npcLocationName = character.StardewNpc.currentLocation?.Name ?? "";
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
            prompt.AppendLine(isZh
                ? "- [ATTENTION_FOCUS] 这是你们俩的共处时光。本轮对话默认从你们的同行相处取材：沿途的景物、彼此的近况、随口的闲聊。你依然保有自己的生活与心事，但\"此刻\"发生在与农夫同行的路上。"
                : "- [ATTENTION_FOCUS] This is your shared time together. This turn's dialogue draws from the walk itself: the scenery along the way, each other's recent lives, casual small talk. You still have your own life and private thoughts, but \"right now\" is happening on the road beside the farmer.");
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
            var spouses = Game1.getPlayerOrEventFarmer().friendshipData.FieldDict
                .Where(x => x.Value.Value.IsMarried() && !x.Value.Value.IsRoommate())
                .Select(x => x.Key);
            bool talkingToSpouse = spouses.Any(x => x == name);
            spouses = spouses.Where(x => x != name);
            if (spouses.Any())
            {
                bool multipleOthers = spouses.Count() > 1;
                var spouseList = string.Join(", ", spouses);
                var nSpouses = spouses.Count();
                if (talkingToSpouse)
                {
                    var otherSpousesList = multipleOthers ? $"{Util.GetString(character, "spousesNOtherPeople", new { nSpouses = nSpouses })} {spouseList}" : spouses.First();
                    var otherSpousesReference = multipleOthers ? Util.GetString(character, "spousesAllTheOthers") : spouses.First();
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
                .Select(x => x.Key);
            bool talkingToRoommate = roommates.Any(x => x == name);
            roommates = roommates.Where(x => x != name);
            if (roommates.Any())
            {
                bool multipleOthers = roommates.Count() > 1;
                var roommateList = multipleOthers ? $"{Util.GetString(character, "spousesNOtherPeople", new { nSpouses = roommates.Count() })} {string.Join(", ", roommates)}" : roommates.First();
                var roommateReference = multipleOthers ? Util.GetString(character, "spouseRoommatesAllTheOthers") : roommates.First();
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
                .Where(x => x.Value.Value.IsEngaged());
            if (engaged.Any(x => x.Key != name))
            {
                var engagedFirst = engaged.First(x => x.Key != name);
                var engagedTo = Game1.characterData[engagedFirst.Key].DisplayName;
                var weddingDays = engagedFirst.Value.Value.CountdownToWedding;
                prompt.AppendLine(Util.GetString(character, "spouseEngaged", new { engagedTo = engagedTo, weddingDays = weddingDays }));
            }
            var total = spouses.Count() + engaged.Count();
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
                ? "[NEGATIVE_CONSTRAINT] 这是未公开的潜意识思绪，农夫毫不知情。绝对严禁在开场白中假定农夫已知；严禁直接展开讨论，除非你自己先在对白中主动坦白。"
                : "[NEGATIVE_CONSTRAINT] CONFIDENTIAL UNSPOKEN THOUGHT. The farmer has zero knowledge of this. Strictly forbid assuming the farmer knows; do NOT elaborate on it unless you explicitly voice it first in dialogue.");
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
                prompt.AppendLine("<jealousy_trigger>");
                prompt.AppendLine(isZh
                    ? $"你注意到农夫今晚已经与 {DateManager.Instance.ActiveDateNpcName} 有约。"
                    : $"You notice the farmer already has plans with {DateManager.Instance.ActiveDateNpcName} tonight.");
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
                    prompt.AppendLine(elem.IsPlayerLine
                        ? $"- {timePrefix}{Util.GetString(character, "generalFarmerLabel")}: {elem.Text}"
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

        internal static string BuildSessionContinuity(Character character, DialogueContext context, bool isZh)
        {
            var prompt = new StringBuilder();
            var session = SessionCache.Instance.GetOrCreate(character.Name);
            if (session.RecentTurns.Count == 0) return prompt.ToString();
            if (StardewModdingAPI.Context.IsWorldReady &&
                (session.LastUpdatedYear != Game1.year ||
                 session.LastUpdatedSeason != (Season)Game1.season ||
                 session.LastUpdatedDay != Game1.dayOfMonth))
                return prompt.ToString();
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
            if (candidateTurns.Count == 0) return prompt.ToString();
            var previousTurns = candidateTurns.TakeLast(3).ToList();

            prompt.AppendLine("<continuity_context>");
            prompt.AppendLine(isZh ? "### [早先会话衔接参考]" : "### [EARLIER SESSION CONTINUITY]");
            prompt.AppendLine(isZh
                ? "[REFERENCE_ONLY] 当日早先对话记录，仅用于保持人物记忆与逻辑连贯："
                : "[REFERENCE_ONLY] Earlier conversation turns today, provided solely for conversational consistency:");
            foreach (var turn in previousTurns)
            {
                string label = turn.IsPlayerLine ? (isZh ? "农夫" : "Farmer") : character.Name;
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