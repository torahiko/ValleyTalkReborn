// Prompts.cs
// ═══════════════════════════════════════════════════════════════════════════
// PROMPT ASSEMBLY ARCHITECTURE
// ═══════════════════════════════════════════════════════════════════════════
//
// This file is responsible for assembling the complete prompt structure sent to
// the LLM for dialogue generation. It follows a carefully designed topology to:
// 1. Balance cognitive attention flow (recent context weighted higher)
// 2. Maximize KV-Cache hit rate (static content first, dynamic content last)
// 3. Prevent attention hijacking (e.g., held items overpowering dialogue intent)
//
// ───────────────────────────────────────────────────────────────────────────
// PROMPT TOPOLOGY (from top to bottom):
// ───────────────────────────────────────────────────────────────────────────
//
// [SystemPrompt] <- Tier 1 & 2 Static Cache (assembled in LlmDialogueService)
//   +- Base system prompt (character persona, translation rules)
//   +- NPC personal memory (updated every few days)
//   +- World memory (updated every few days)
//   +- Gossip (daily rotation, single entry per day)
//
// [GameConstantContext] <- Tier 1 Static Cache
//   +- Game mechanics summary (locations, seasons, crops)
//   +- Farm state summary (barn animals, coop animals, buildings)
//
// [NpcConstantContext] <- Tier 1 Static Cache
//   +- Biography
//   +- Personality traits (filtered by current heart level)
//   +- Progress states (quest status, marriage status)
//   +- Relationships (daily rotation subset)
//
// --- [KV-Cache Boundary] ---
//
// [CorePrompt] <- Dynamic Context (changes every turn)
//   +- Scene context (location, weather, time, nearby NPCs)
//   +- Friendship level & marriage status
//   +- Special events (stood up, date invitation, jealousy)
//   +- Preoccupation (background thought, rarely injected)
//   +- Player profile (appearance, held item, buffs)
//   +- Eavesdrop block (overheard conversations)
//   +- Spouse waiting block (late-night scenario)
//   +- Echo block (recent interaction afterglow)
//   +- CURRENT CONVERSATION (dialogue history, positioned at bottom)
//   +- SESSION CONTINUITY (earlier exchanges today)
//   +- PENDING TOPIC (pre-conversation lingering thought)
//   +- MOVEMENT INSTRUCTION (embodied action, tightly coupled to conversation)
//   +- Evolved traits block (personality facets dynamically weighted)
//   +- Local perception block (gift, eat, nearby actions)
//
// [Instructions] <- Format constraints
//   +- Core perspective rules (only output your own dialogue)
//   +- Mood tag syntax ([MOOD:curious])
//   +- Extra portrait syntax (if applicable)
//
// [Command] <- Action tag syntax & tool calling rules
//   +- Emote bubbles ([ACTION:EMOTE:HAPPY])
//   +- Movement tags ([ACTION:STEP:FORWARD], [ACTION:STEP:BACKWARD], [ACTION:STEP:LEFT], [ACTION:STEP:RIGHT])
//
// [ResponseStart] <- Final delimiter ("[In-Character Dialogue]:")
//
// ───────────────────────────────────────────────────────────────────────────
// KEY DESIGN PRINCIPLES:
// ───────────────────────────────────────────────────────────────────────────
// 1. Recency Bias: Dialogue history positioned right before Instructions,
//    ensuring the player's latest input is fresh in the LLM's "working memory"
//
// 2. Cache Preservation: SystemPrompt never modified after assembly,
//    ensuring multi-turn conversations reuse cached KV states
//
// 3. Embodied Action Coupling: Movement instructions placed immediately
//    after dialogue history, creating tight semantic coupling
//
// 4. Attention Isolation: Special events (stood-up, jealousy) use XML tags
//    to create attention boundaries, preventing bleed into normal conversation
//
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

    // ── 【新增】收集本轮注入的私有思绪（Memory 作用域，不序列化） ──
    private readonly List<string> _injectedPrivateThoughts = new List<string>();
    public IReadOnlyList<string> InjectedPrivateThoughts => _injectedPrivateThoughts;

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
    
    private string _systemPrompt;
    public string SystemPrompt { get => _systemPrompt ??= GetSystemPrompt(); internal set => _systemPrompt = value; }

    private string _gameConstantContext;
    public string GameConstantContext { get => _gameConstantContext ??= GetGameConstantContext(); internal set => _gameConstantContext = value; }

    private string _npcConstantContext;
    public string NpcConstantContext { get => _npcConstantContext ??= GetNpcConstantContext(); internal set => _npcConstantContext = value; }

    private string _corePrompt;
    public string CorePrompt { get => _corePrompt ??= GetCorePrompt(); internal set => _corePrompt = value; }

    // ── 动态注入占位：由 LlmDialogueService 在 CorePrompt 求值前赋值 ──
    public string PendingEvolvedTraitsBlock { get; set; }
    public string PendingLocalPerceptionBlock { get; set; }
  
    // ── 【新增】动态挂载模块字段（偷听、配偶等待、近期互动余韵） ──
    /// <summary>
    /// Eavesdrop context block - recent overheard conversations or ambient social context.
    /// Injected by EavesdropInjector in LlmDialogueService before CorePrompt evaluation.
    /// </summary>
    public string PendingEavesdropBlock { get; set; }
  
    /// <summary>
    /// Spouse waiting event block - late-night waiting scenario for married NPCs.
    /// Injected by SpouseWaitingEvent in LlmDialogueService before CorePrompt evaluation.
    /// </summary>
    public string PendingSpouseWaitingBlock { get; set; }
  
    /// <summary>
    /// Immediate echo block - recent interaction afterglow (shared meals, activities, etc.).
    /// Injected by ImmediateEchoStore in LlmDialogueService before CorePrompt evaluation.
    /// </summary>
    public string PendingEchoBlock { get; set; }

    /// <summary>
    /// 关系里程碑与修罗场冲突块（结婚倒计时、离婚申请日、花舞节修罗场）。
    /// 在 CorePrompt 求值前由 LlmDialogueService 赋值。
    /// </summary>
    public string PendingMilestoneBlock { get; set; }

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

    private void GetMicroEnvironment(StringBuilder prompt)
    {
        prompt.AppendLine(Util.GetString(Character, "sceneHeading"));
        prompt.AppendLine("<scene_context>");

        string friendlyLocation = EnvironmentScanner.GetLocationFriendlyName(Context.Location);
        prompt.AppendLine(Util.GetString(Character, "sceneLocation", new { Location = friendlyLocation }));

        string festival = EnvironmentScanner.GetTodayFestivalName();
        if (!string.IsNullOrEmpty(festival))
        {
            if (EnvironmentScanner.IsFestivalCurrentlyActive())
                prompt.AppendLine(Util.GetString(Character, "sceneFestivalActive", new { Festival = festival }));
            else
                prompt.AppendLine(Util.GetString(Character, "sceneFestivalToday", new { Festival = festival }));
        }

        prompt.AppendLine(Util.GetString(Character, "sceneTimeSeason", new { Time = Context.TimeOfDay, Season = Game1.CurrentSeasonDisplayName, Day = Context.DayOfSeason }));

        if (Context.Weather != null && Context.Weather.Any())
        {
            string sensoryWeather = GetSensoryWeatherDescription(Context.Weather);
            if (!string.IsNullOrWhiteSpace(sensoryWeather))
            {
                prompt.AppendLine(Util.GetString(Character, "sceneWeather", new { Weather = sensoryWeather }));
            }
        }

        var nearbyObjects = EnvironmentScanner.ScanNearbyObjects(Character.StardewNpc, 5, 8);
        if (nearbyObjects.Any())
        {
            prompt.AppendLine(Util.GetString(Character, "sceneObjects", new { Objects = string.Join(", ", nearbyObjects) }));
        }

        string npcLocationName = Character.StardewNpc.currentLocation?.Name ?? "";
        string playerLocationName = Game1.getPlayerOrEventFarmer().currentLocation?.Name ?? "";
        if (string.Equals(npcLocationName, playerLocationName, StringComparison.OrdinalIgnoreCase))
        {
            prompt.AppendLine(Util.GetString(Character, "sceneSpatialCoPresent"));
        }

        if (CurrentFlags.IncludeEnvironment)
        {
            var otherNpcs = Util.GetSceneVillagers(Character.StardewNpc);
            if (otherNpcs.Any())
            {
                prompt.AppendLine(Util.GetString(Character, "sceneNearbyVillagers", new { Villagers = string.Join(", ", otherNpcs.Select(n => n.displayName)) }));
            }
        }

        string poiContext = CompanionScheduleManager.Instance.GetActivePoiContext(Character.Name);
        if (!string.IsNullOrEmpty(poiContext))
            prompt.AppendLine(poiContext);

        prompt.AppendLine("</scene_context>\n");
    }

    private string GetSensoryWeatherDescription(List<string> weatherList)
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
            string desc = key != null ? Util.GetString(Character, key) : raw;
            if (!string.IsNullOrEmpty(desc))
                descriptions.Add(desc);
        }
        return string.Join(" ", descriptions);
    }

    private string GetCorePrompt()
    {
        var prompt = new StringBuilder();
        bool isZh = IsChineseLanguage;
        var flags = CurrentFlags;
        string npcName = Character?.Name ?? "";

        // 🔧 互斥安全清洗：HasStoodUpPending 与 IsOnDate 不应同时为 true。
        // 若发生碰撞，以正在约会为优先，重置爽约标记，防止 stood_up 语境污染约会对话。
        if (flags?.HasStoodUpPending == true && flags?.IsOnDate == true)
        {
            ModEntry.SMonitor?.Log(
                $"[Prompts] Conflict detected for {npcName}: HasStoodUpPending && IsOnDate both true. " +
                "Prioritizing IsOnDate and clearing stood-up flag.",
                StardewModdingAPI.LogLevel.Warn);

            flags.HasStoodUpPending = false;
            flags.StoodUpDate = string.Empty;
        }

        DefaultOrOverride("GameState", GetGameState, prompt);

        if (flags?.IncludeMemories == true)
            DefaultOrOverride("EventHistory", GetEventHistory, prompt);

        // ── stood_up: 纯事实+结构约束，不预设情绪 ──
        if (flags?.HasStoodUpPending == true && ModEntry.Config.EnableDateSystem)
        {
            prompt.AppendLine("<emotional_conflict type=\"stood_up\">");
            prompt.AppendLine(isZh
                ? "- 事实：你昨晚一直等待着玩家赴约，但玩家完全没有出现。\n- 反应要求：依据角色性格与当前好感度做出回应。"
                : "- Fact: You waited for the player last night, but they did not show up.\n- Instruction: Respond according to character personality and current heart level.");
            prompt.AppendLine("</emotional_conflict>\n");
            GetMicroEnvironment(prompt);
            InjectPendingTopic(prompt);

            // ── 🔧 一次性动态模块注入（与完整分支保持同一"背景→即时"顺序） ──
            if (!string.IsNullOrEmpty(PendingEavesdropBlock))
            {
                prompt.AppendLine(PendingEavesdropBlock);
                prompt.AppendLine();
            }
            if (!string.IsNullOrEmpty(PendingSpouseWaitingBlock))
            {
                prompt.AppendLine(PendingSpouseWaitingBlock);
                prompt.AppendLine();
            }
            if (!string.IsNullOrEmpty(PendingEchoBlock))
            {
                prompt.AppendLine(PendingEchoBlock);
                prompt.AppendLine();
            }

            // ── 💍 关系里程碑与冲突（结婚倒计时 / 离婚申请日 / 花舞节伴侣与吃醋） ──
            if (!string.IsNullOrEmpty(PendingMilestoneBlock))
            {
                prompt.AppendLine(PendingMilestoneBlock);
                prompt.AppendLine();
            }

            // ── 🔧 补齐动态注入（与 date context 分支保持一致） ──
            if (!string.IsNullOrEmpty(PendingEvolvedTraitsBlock))
                prompt.AppendLine("\n" + PendingEvolvedTraitsBlock);
            if (!string.IsNullOrEmpty(PendingLocalPerceptionBlock))
                prompt.AppendLine("\n" + PendingLocalPerceptionBlock);

            // ── 🔧 早返回分支同样需要注入约会协议，否则 LLM 无法输出 [UI:DATE_INVITE] ──
            AppendDateInvitationProtocol(prompt);
            AppendFollowInvitationProtocol(prompt);

            string stoodUpPrompt = prompt.ToString();
            LogTopologyVerification(stoodUpPrompt, "STOOD_UP");
            return stoodUpPrompt;
        }

        // ── date context ──
        if (ModEntry.Config.EnableDateSystem && !string.IsNullOrEmpty(npcName) && DateManager.Instance?.IsOnDate(npcName) == true)
        {
            var dateMode = DateManager.Instance.CurrentDateMode;
            if (dateMode == DateManager.DateMode.Follow)
            {
                prompt.AppendLine("<date_context mode=\"walking\">");
                prompt.AppendLine(isZh
                    ? $"- 当前地点：{DateManager.Instance.ActiveDateLocation ?? ""}"
                    : $"- Location: {DateManager.Instance.ActiveDateLocation ?? ""}");
                // 修复：删除"轻松自然的日常氛围"
                prompt.AppendLine(isZh
                    ? "- 你正在陪农夫在附近走走。依据角色性格与当前好感度进行表达。"
                    : "- You are walking around together. Respond according to character personality and current heart level.");
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
                // 修复：删除"倾听与深情""浪漫环境互动"
                prompt.AppendLine(isZh
                    ? "- 你们正在进行约会。依据角色性格与当前好感度进行表达，可将当前环境作为上下文参考。"
                    : "- You are currently on a date. Respond according to character personality and current heart level. Reference the current surroundings as context.");
                prompt.AppendLine("</date_context>\n");
            }

            var lastPlayerLine = Context?.ChatHistory?.LastOrDefault(x => x.IsPlayerLine)?.Text;
            if (!string.IsNullOrWhiteSpace(lastPlayerLine))
                DateManager.Instance.RecordDateDialogue(Game1.player?.Name ?? "Farmer", lastPlayerLine);

            GetMicroEnvironment(prompt);

            // ── 🔧 一次性动态模块注入（背景感知先于对话历史，与完整分支同一顺序） ──
            if (!string.IsNullOrEmpty(PendingEavesdropBlock))
            {
                prompt.AppendLine(PendingEavesdropBlock);
                prompt.AppendLine();
            }
            if (!string.IsNullOrEmpty(PendingSpouseWaitingBlock))
            {
                prompt.AppendLine(PendingSpouseWaitingBlock);
                prompt.AppendLine();
            }
            if (!string.IsNullOrEmpty(PendingEchoBlock))
            {
                prompt.AppendLine(PendingEchoBlock);
                prompt.AppendLine();
            }

            // ── 💍 关系里程碑与冲突（结婚倒计时 / 离婚申请日 / 花舞节伴侣与吃醋） ──
            if (!string.IsNullOrEmpty(PendingMilestoneBlock))
            {
                prompt.AppendLine(PendingMilestoneBlock);
                prompt.AppendLine();
            }

            GetCurrentConversation(prompt);
            InjectSessionContinuity(prompt);
            InjectPendingTopic(prompt);
            if (!string.IsNullOrEmpty(PendingEvolvedTraitsBlock))
                prompt.AppendLine("\n" + PendingEvolvedTraitsBlock);
            if (!string.IsNullOrEmpty(PendingLocalPerceptionBlock))
                prompt.AppendLine("\n" + PendingLocalPerceptionBlock);

            // ── 🔧 早返回分支同样需要注入约会协议，否则 LLM 无法输出 [UI:DATE_INVITE] ──
            AppendDateInvitationProtocol(prompt);
            AppendFollowInvitationProtocol(prompt);
            AppendDateEndingProtocol(prompt);

            string datePrompt = prompt.ToString();
            LogTopologyVerification(datePrompt, "DATE_CONTEXT");
            return datePrompt;
        }

        // ── simple greeting fast pass ──
        if (flags?.IsSimpleGreeting == true && flags?.IsMovementRequested != true && string.IsNullOrEmpty(PendingMilestoneBlock))
        {
            prompt.AppendLine("<greeting_fast_pass>");
            // 修复：删除"随和、自然且简练"
            prompt.AppendLine(isZh
                ? "农夫正向你打招呼。依据角色性格与当前好感度做出简短回应。"
                : "The farmer is greeting you. Respond briefly according to character personality and current heart level.");
            prompt.AppendLine("</greeting_fast_pass>\n");
            GetMicroEnvironment(prompt);
            DefaultOrOverride("CurrentConversation", GetCurrentConversation, prompt);
            InjectSessionContinuity(prompt);
            InjectPendingTopic(prompt);

            // ── 🔧 一次性动态模块注入（在 PlayerProfile 之前） ──
            if (!string.IsNullOrEmpty(PendingEavesdropBlock))
            {
                prompt.AppendLine(PendingEavesdropBlock);
                prompt.AppendLine();
            }
            if (!string.IsNullOrEmpty(PendingSpouseWaitingBlock))
            {
                prompt.AppendLine(PendingSpouseWaitingBlock);
                prompt.AppendLine();
            }
            if (!string.IsNullOrEmpty(PendingEchoBlock))
            {
                prompt.AppendLine(PendingEchoBlock);
                prompt.AppendLine();
            }

            // ── 🔧 补齐动态注入（在 PlayerProfile 之前） ──
            if (!string.IsNullOrEmpty(PendingEvolvedTraitsBlock))
                prompt.AppendLine("\n" + PendingEvolvedTraitsBlock);
            if (!string.IsNullOrEmpty(PendingLocalPerceptionBlock))
                prompt.AppendLine("\n" + PendingLocalPerceptionBlock);

            string simpleProfile = PlayerProfileManager.BuildProfileText(
                Character.StardewNpc,
                playerInput: "",
                flags: flags);
            if (!string.IsNullOrEmpty(simpleProfile))
                prompt.AppendLine(simpleProfile);

            // ── 🔧 早返回分支同样需要注入约会协议，否则 LLM 无法输出 [UI:DATE_INVITE] ──
            AppendDateInvitationProtocol(prompt);

            string greetingPrompt = prompt.ToString();
            LogRoutingDebug(greetingPrompt, "SIMPLE_GREETING_FAST_PASS");
            LogTopologyVerification(greetingPrompt, "SIMPLE_GREETING");
            return greetingPrompt;
        }

        // ── full context build ──
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

        bool hasNoPlayerInput = Context == null || (!Context.IsActiveTurn && Context.Accept == null);
        if (!hasNoPlayerInput && flags?.IncludeShortTermContext == true)
        {
            prompt.AppendLine("<interaction_state>");
            prompt.AppendLine(Util.GetString(Character, "interactionOngoingState"));
            prompt.AppendLine(Util.GetString(Character, "interactionOngoingGoal"));
            prompt.AppendLine("</interaction_state>\n");
        }
        else if (hasNoPlayerInput && flags?.IsSimpleGreeting != true)
        {
            prompt.AppendLine("<interaction_state>");
            prompt.AppendLine(Util.GetString(Character, "interactionApproaching"));
            prompt.AppendLine("</interaction_state>\n");
        }

        // ── date invitation protocol (Consent Tag + Client UI Dispatch) ──
        AppendDateInvitationProtocol(prompt);

        // ── jealousy trigger: 删除"吃醋情绪" ──
        if (ModEntry.Config.EnableDateSystem && flags?.IsJealousy == true && DateManager.Instance != null)
        {
            prompt.AppendLine("<jealousy_trigger>");
            prompt.AppendLine(isZh
                ? $"你注意到农夫今晚已经与 {DateManager.Instance.ActiveDateNpcName} 有约。依据角色性格与当前好感度做出回应。"
                : $"You notice the farmer already has plans with {DateManager.Instance.ActiveDateNpcName} tonight. Respond according to character personality and current heart level.");
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
      
        // ══════════════════════════════════════════════════════════════════════
        // 🔧 [INJECTION SEQUENCE CORRECTION] 动态挂载模块统一注入（对话历史之前）
        // ══════════════════════════════════════════════════════════════════════
        // 注入顺序（从背景到即时）：
        // 1. Eavesdrop（偷听上下文，短期背景）
        // 2. SpouseWaiting（配偶等待事件，情境背景）
        // 3. Echo（近期互动余韵，情感延续）
        // 4. EvolvedTraits + LocalPerception（人格特质 + 现场目击，环境背景）
        // → 然后对话历史（CurrentConversation）稳居底部，直接承接生成入口
        // → 动作指令（movement_instruction）紧咬对话
        // ══════════════════════════════════════════════════════════════════════

        if (!string.IsNullOrEmpty(PendingEavesdropBlock))
        {
            prompt.AppendLine(PendingEavesdropBlock);
            prompt.AppendLine();
        }

        if (!string.IsNullOrEmpty(PendingSpouseWaitingBlock))
        {
            prompt.AppendLine(PendingSpouseWaitingBlock);
            prompt.AppendLine();
        }

        if (!string.IsNullOrEmpty(PendingEchoBlock))
        {
            prompt.AppendLine(PendingEchoBlock);
            prompt.AppendLine();
        }

        // ── 💍 关系里程碑与冲突（结婚倒计时 / 离婚申请日 / 花舞节伴侣与吃醋） ──
        if (!string.IsNullOrEmpty(PendingMilestoneBlock))
        {
            prompt.AppendLine(PendingMilestoneBlock);
            prompt.AppendLine();
        }

        // ── 🎯 特质与现场目击是环境背景，必须先于对话历史注入（防注意力劫持） ──
        if (!string.IsNullOrEmpty(PendingEvolvedTraitsBlock))
            prompt.AppendLine(PendingEvolvedTraitsBlock + "\n");
        if (!string.IsNullOrEmpty(PendingLocalPerceptionBlock))
            prompt.AppendLine(PendingLocalPerceptionBlock + "\n");

        // ── 对话历史置底（绝对贴合生成入口，Recency Bias 最优） ──
        DefaultOrOverride("CurrentConversation", GetCurrentConversation, prompt);
        InjectSessionContinuity(prompt);
        InjectPendingTopic(prompt);
        InjectMovementInstruction(prompt);

        // ══════════════════════════════════════════════════════════════════════
        // ❌ [REMOVAL] 删除重复的 gossipSnapshots 注入（已在 SystemPrompt 中处理）
        // ══════════════════════════════════════════════════════════════════════

        string finalPrompt = prompt.ToString();
        LogRoutingDebug(finalPrompt, "FULL_CONTEXT_BUILD");

        // ── 🔍 拓扑验证日志（Debug 模式） ──
        LogTopologyVerification(finalPrompt, "FULL_CONTEXT_BUILD");

        return finalPrompt;
    }

    /// <summary>
    /// 统一的约会邀请协议注入器。
    /// 在完整构建与所有早返回分支中调用，确保 LLM 无论走哪条路由都能看到
    /// &lt;date_invitation_protocol&gt; 规则，从而正确输出 [UI:DATE_INVITE] 标签。
    /// </summary>
    private void AppendDateInvitationProtocol(StringBuilder sb)
    {
        if (!ModEntry.Config.EnableDateSystem || CurrentFlags?.IsInviteRequested != true || CurrentFlags?.IsOnDate == true)
            return;

        bool isZh = IsChineseLanguage;
        bool isFestivalToday = Utility.isFestivalDay(Game1.dayOfMonth, Game1.season);

        sb.AppendLine("<date_invitation_protocol>");
        sb.AppendLine(isZh ? "农夫正在向你发起今晚的约会邀请。" : "The farmer is inviting you out on a date tonight.");

        if (isFestivalToday)
        {
            sb.AppendLine(isZh
                ? "- 冲突拒绝: 今天是节日，日程有冲突。依据角色性格在对白中委婉说明改天再约，本次仅输出对白文本。"
                : "- DECLINE: Festival conflict today. Explain in dialogue that you'll reschedule; output dialogue text only.");
        }
        else
        {
            sb.AppendLine(isZh
                ? "- 若同意赴约: 依据角色性格与好感度表达欣喜与期待，并在回复台词的最末尾附加内部标签 [UI:DATE_INVITE]。"
                : "- IF ACCEPTING: Express anticipation consistent with your persona, and append [UI:DATE_INVITE] at the absolute end.");
            sb.AppendLine(isZh
                ? "- 若拒绝赴约: 依据角色性格在对白中委婉表达理由，本情况仅输出对白文本。"
                : "- IF DECLINING: Provide an in-character explanation as dialogue text only.");
        }
        sb.AppendLine("</date_invitation_protocol>\n");
    }

    private void AppendFollowInvitationProtocol(StringBuilder sb)
    {
        // 门禁 a: CurrentFlags 为 null 或非 Follow 请求或已在跟随状态 → 不注入
        if (CurrentFlags == null
            || CurrentFlags.RequestedAction != ActionTag.Follow
            || CurrentFlags.IsFollowing)
            return;

        bool isZh = IsChineseLanguage;

        // 门禁 b: 已有其他 NPC 跟随 → decline 块
        if (MovementManager.Instance != null
            && MovementManager.Instance.HasActiveFollow
            && MovementManager.Instance.CurrentFollowingNpc?.Name != Character?.Name)
        {
            sb.AppendLine("<follow_unavailable>");
            sb.AppendLine(isZh
                ? "- 事实: 农夫身边已有其他同伴随行，你现在无法加入同行。"
                : "- Fact: The farmer already has another companion with them. You cannot join right now.");
            sb.AppendLine(isZh
                ? "- 反应要求: 依据角色性格做出回应，仅输出对白文本，不附加任何标签。"
                : "- Instruction: Respond according to character personality. Output dialogue text only, no tags.");
            sb.AppendLine("</follow_unavailable>\n");
            return;
        }

        // 门禁 c: 正常邀请协议（彻底移除末尾无意义的工具警示）
        sb.AppendLine("<follow_invitation_protocol>");
        sb.AppendLine(isZh
            ? "农夫正在邀请你与他同行。"
            : "The farmer is inviting you to come along.");
        sb.AppendLine(isZh
            ? "- 若同意: 依据角色性格与好感度表达回应，并在台词的最末尾附加内部标签 [UI:FOLLOW]。"
            : "- IF ACCEPTING: Respond according to character personality and heart level, and append [UI:FOLLOW] at the absolute end.");
        sb.AppendLine(isZh
            ? "- 若拒绝: 依据角色性格在对白中委婉说明缘由，仅输出对白文本。"
            : "- IF DECLINING: Provide an in-character explanation as dialogue text only.");
        sb.AppendLine("</follow_invitation_protocol>\n");
    }

    private void AppendDateEndingProtocol(StringBuilder sb)
    {
        if (CurrentFlags?.IsOnDate != true) return;
        bool isZh = IsChineseLanguage;
        sb.AppendLine("<date_ending_protocol>");
        sb.AppendLine(isZh
            ? "- 若玩家提出结束今天的约会或向你道别（例如\"今天就到这吧\"\"我先回去\"）：依据角色性格与好感度回应，并在台词的最末尾附加内部标签 [ACTION:END_DATE]。"
            : "- IF THE PLAYER PROPOSES ENDING TODAY'S DATE OR SAYS GOODBYE: Respond in character, and append [ACTION:END_DATE] at the absolute end.");
        sb.AppendLine(isZh
            ? "- 若玩家未提及结束约会：保持正常对话，仅输出纯对白文本。"
            : "- OTHERWISE: Continue regular dialogue as plain text only.");
        sb.AppendLine("</date_ending_protocol>\n");
    }

    /// <summary>
    /// Logs topology verification information in debug mode.
    /// Helps diagnose prompt assembly order, dynamic injection sequence, and cache-friendly structure.
    /// </summary>
    private void LogTopologyVerification(string finalPrompt, string routeType)
    {
        if (!ModEntry.Config?.Debug ?? true)
            return;

        try
        {
            var sb = new StringBuilder();
            sb.AppendLine("╔═══════════════════════════════════════════════════════════");
            sb.AppendLine($"║ [Topology Verification] {Name} | Route: {routeType}");
            sb.AppendLine("╠═══════════════════════════════════════════════════════════");

            // ── 1. 动态注入块检测 ──
            sb.AppendLine("║ [Dynamic Injection Status]");
            sb.AppendLine($"║   PendingEvolvedTraitsBlock: {(!string.IsNullOrEmpty(PendingEvolvedTraitsBlock) ? "✓ Injected" : "✗ Empty")}");
            sb.AppendLine($"║   PendingLocalPerceptionBlock: {(!string.IsNullOrEmpty(PendingLocalPerceptionBlock) ? "✓ Injected" : "✗ Empty")}");
            sb.AppendLine($"║   PendingEavesdropBlock: {(!string.IsNullOrEmpty(PendingEavesdropBlock) ? "✓ Injected" : "✗ Empty")}");
            sb.AppendLine($"║   PendingSpouseWaitingBlock: {(!string.IsNullOrEmpty(PendingSpouseWaitingBlock) ? "✓ Injected" : "✗ Empty")}");
            sb.AppendLine($"║   PendingEchoBlock: {(!string.IsNullOrEmpty(PendingEchoBlock) ? "✓ Injected" : "✗ Empty")}");
            sb.AppendLine($"║   PendingMilestoneBlock: {(!string.IsNullOrEmpty(PendingMilestoneBlock) ? "✓ Injected" : "✗ Empty")}");

            // ── 2. 拓扑结构验证 ──
            sb.AppendLine("║");
            sb.AppendLine("║ [Topology Structure]");

            bool hasCurrentConversation = finalPrompt.Contains("### 当前对话")
                || finalPrompt.Contains("### 对话历史")
                || finalPrompt.Contains("### Conversation History")
                || finalPrompt.Contains("### CURRENT");

            bool hasMovementInstruction = finalPrompt.Contains("<movement_instruction");
            bool isMovementExpected = CurrentFlags?.IsMovementRequested == true || CurrentFlags?.IsFollowing == true;

            string instructionsText = Instructions ?? "";
            bool hasInstructions = instructionsText.Contains("## 输出指令")
                                   || instructionsText.Contains("## 格式与排版要求")
                                   || instructionsText.Contains("## OUTPUT INSTRUCTIONS")
                                   || instructionsText.Contains("## Formatting & Output Requirements")
                                   || !string.IsNullOrWhiteSpace(instructionsText);

            sb.AppendLine($"║   CurrentConversation: {(hasCurrentConversation ? "✓ Present" : "✗ Missing")}");
            sb.AppendLine($"║   MovementInstruction: {(hasMovementInstruction ? "✓ Present" : (isMovementExpected ? "✗ Missing" : "– Not Required"))}");
            sb.AppendLine($"║   InstructionsBlock: {(hasInstructions ? "✓ Present" : "✗ Missing")}");

            // ── 3. 对话历史位置验证（关键：必须在 movement_instruction 之前） ──
            if (hasCurrentConversation && hasMovementInstruction)
            {
                int conversationPos = finalPrompt.LastIndexOf("### 当前对话", StringComparison.Ordinal);
                if (conversationPos < 0)
                    conversationPos = finalPrompt.LastIndexOf("### 对话历史", StringComparison.Ordinal);
                if (conversationPos < 0)
                    conversationPos = finalPrompt.LastIndexOf("### Conversation History", StringComparison.Ordinal);
                if (conversationPos < 0)
                    conversationPos = finalPrompt.LastIndexOf("### CURRENT", StringComparison.Ordinal);

                int movementPos = finalPrompt.LastIndexOf("<movement_instruction", StringComparison.Ordinal);

                bool correctOrder = conversationPos < movementPos;
                sb.AppendLine($"║   Conversation → Movement order: {(correctOrder ? "✓ Correct" : "✗ INVERTED (BUG!)")}");

                if (!correctOrder)
                {
                    sb.AppendLine("║   ⚠ WARNING: Movement instruction appears BEFORE conversation history!");
                    sb.AppendLine("║   ⚠ This breaks the 'dialogue history at bottom' principle.");
                }
            }

            // ── 4. Turn 判定验证 ──
            sb.AppendLine("║");
            sb.AppendLine("║ [Turn Detection]");
            sb.AppendLine($"║   Context.IsActiveTurn: {Context?.IsActiveTurn.ToString() ?? "null"}");
            sb.AppendLine($"║   Context.DialogueSessionId: {Context?.DialogueSessionId ?? "null"}");
            sb.AppendLine($"║   RoutingFlags.IsSimpleGreeting: {CurrentFlags?.IsSimpleGreeting.ToString() ?? "null"}");
            sb.AppendLine($"║   RoutingFlags.IncludeShortTermContext: {CurrentFlags?.IncludeShortTermContext.ToString() ?? "null"}");

            // ── 5. 长度统计 ──
            sb.AppendLine("║");
            sb.AppendLine("║ [Length Statistics]");
            sb.AppendLine($"║   CorePrompt length: {finalPrompt.Length} chars (~{finalPrompt.Length / 4} tokens)");
            sb.AppendLine($"║   Lines: {finalPrompt.Split('\n').Length}");

            sb.AppendLine("╚═══════════════════════════════════════════════════════════");
          
            ModEntry.SMonitor?.Log(sb.ToString(), StardewModdingAPI.LogLevel.Debug);
        }
        catch (Exception ex)
        {
            ModEntry.SMonitor?.Log($"[Prompts] Topology verification failed: {ex.Message}", StardewModdingAPI.LogLevel.Warn);
        }
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

    // ── preoccupation: 删除"微妙浸润语气" ──
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
        _injectedPrivateThoughts.Add(preoccupation);
        prompt.AppendLine(Util.GetString(Character, "preoccupation", new { Name = Name, preoccupation = preoccupation }));
        prompt.AppendLine(isZh
            ? "（这是你尚未说出口的内心想法，农夫无从知晓。若要让农夫知道，需由你自己先在台词中说出来；发言选项的内容范围以你已经说出口的台词为准。）"
            : "(This is your private, unspoken thought — the farmer has no way of knowing it. If you want them to know, voice it yourself in dialogue first; base any farmer response options only on what you have already said aloud.)");
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

    // ── pending topic: 删除"自然渲染语气" ──
    private void InjectPendingTopic(StringBuilder prompt)
    {
        // ConsumePendingTopic 内部已 Remove，天然防重复+跨角色隔离
        string pending = PendingTopicManager.Instance.ConsumePendingTopic(Character.Name);
        if (string.IsNullOrEmpty(pending)) return;

        string playerName = Game1.player?.Name ?? "Farmer";
        string farmName = Game1.player?.farmName?.Value ?? "Farm";
        pending = pending
            .Replace("@", playerName)
            .Replace("%farmer", playerName)
            .Replace("%farm", farmName);

        bool isZh = IsChineseLanguage;
        _injectedPrivateThoughts.Add(pending);
        prompt.AppendLine("<pending_thought>");
        prompt.AppendLine(Util.GetString(Character, "pendingTopicIntro"));
        prompt.AppendLine(pending);
        prompt.AppendLine(Util.GetString(Character, "pendingTopicOutro"));
        prompt.AppendLine("</pending_thought>\n");
    }

    private void InjectGreetingContext(StringBuilder prompt)
    {
        var historyManager = DialogueHistoryManager.Instance;
        if (historyManager == null) return;
        var history = historyManager.GetRecentHistory(Character.Name, 1);
        if (history.Count == 0) return;

        var lastEntry = history[0];
        var lastTime = lastEntry.Timestamp;
        var now = new StardewTime(Game1.year, (Season)Game1.season, Game1.dayOfMonth, Game1.timeOfDay);
        bool isToday = lastTime.Year == now.Year && lastTime.Season == now.Season
                       && lastTime.DayOfMonth == now.DayOfMonth;
        if (isToday) return;

        int dayGap = (int)Math.Round(now.TotalDays - lastTime.TotalDays);
        prompt.AppendLine("<greeting_context>");
        if (dayGap == 1)
        {
            prompt.AppendLine(Util.GetString(Character, "greetingYesterday"));
        }
        else if (dayGap <= 3)
        {
            prompt.AppendLine(Util.GetString(Character, "greetingFewDays", new { DayGap = dayGap }));
        }
        else
        {
            prompt.AppendLine(Util.GetString(Character, "greetingLongTime", new { DayGap = dayGap }));
        }
        prompt.AppendLine("</greeting_context>\n");
    }

    // ── session continuity: 删除"自然延续" ──
    private void InjectSessionContinuity(StringBuilder prompt)
    {
        var session = SessionCache.Instance.GetOrCreate(Character.Name);
        if (session.RecentTurns.Count == 0) return;
        if (StardewModdingAPI.Context.IsWorldReady &&
            (session.LastUpdatedYear != Game1.year ||
             session.LastUpdatedSeason != (Season)Game1.season ||
             session.LastUpdatedDay != Game1.dayOfMonth))
            return;

        string playerName = Game1.player?.Name ?? "";
        string Normalize(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return "";
            string clean = s;
            if (!string.IsNullOrEmpty(playerName))
                clean = clean.Replace(playerName, "");
            return Regex.Replace(clean, @"[\s\$\#\@\{\}\[\]\(\)]", "");
        }

        var currentNorms = Context.ChatHistory
            .Select(x => Normalize(x.Text))
            .Where(x => !string.IsNullOrEmpty(x))
            .ToHashSet();

        var candidateTurns = session.RecentTurns
            .Where(t => !string.IsNullOrWhiteSpace(t.Text) && !currentNorms.Contains(Normalize(t.Text)))
            .ToList();

        if (Context.ChatHistory.Any() && candidateTurns.Count > 0)
        {
            int skipRecentCount = Math.Min(2, candidateTurns.Count);
            candidateTurns = candidateTurns.Take(candidateTurns.Count - skipRecentCount).ToList();
        }
        if (candidateTurns.Count == 0) return;

        var previousTurns = candidateTurns.TakeLast(3).ToList();
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
            // 修复：删除"自然延续"
            prompt.AppendLine(isZh
                ? $"（你之前的情绪基调：{session.EmotionalTone}。保持与前序交流的一致性。）"
                : $"(Your earlier emotional tone: {session.EmotionalTone} — maintain consistency with prior exchanges.)");
        }
        prompt.AppendLine();
    }

    // ── movement instruction: 修复 following/adjacent 语气指令 ──
    private void InjectMovementInstruction(StringBuilder prompt)
    {
        if (CurrentFlags.IsOnDate || CurrentFlags.IsJealousy) return;
        if (!CurrentFlags.IsActionRequested && !CurrentFlags.IsFollowing) return;

        bool isZh = IsChineseLanguage;

        // ── VT-FOLLOW-T2: Follow invitation ──
        if (CurrentFlags.RequestedAction == ActionTag.Follow && !CurrentFlags.IsFollowing)
        {
            AppendFollowInvitationProtocol(prompt);
            return;
        }

        // ── VT-FOLLOW-T2: Stop follow ──
        if (CurrentFlags.RequestedAction == ActionTag.StopFollow)
        {
            prompt.AppendLine("<movement_instruction mode=\"stop_follow\">");
            prompt.AppendLine(isZh
                ? "农夫希望你停止跟随。"
                : "The farmer wants you to stop following.");
            prompt.AppendLine(isZh
                ? "- 若同意停止: 依据角色性格简短回应，并在台词最末尾附加 [ACTION:STOP_FOLLOW]。"
                : "- IF ACCEPTING: Respond briefly in character, and append [ACTION:STOP_FOLLOW] at the absolute end.");
            prompt.AppendLine(isZh
                ? "- 若想继续跟随: 仅输出对白说明缘由，不附加标签。"
                : "- IF DECLINING: Explain in character as dialogue text only, no tags.");
            prompt.AppendLine("</movement_instruction>\n");
            return;
        }

        // ── VT-FOLLOW-T2: Stay home ──
        if (CurrentFlags.RequestedAction == ActionTag.StayHome)
        {
            prompt.AppendLine("<movement_instruction mode=\"stay_home\">");
            prompt.AppendLine(isZh
                ? "农夫希望你今天留在家里，不要出门。"
                : "The farmer wants you to stay home today.");
            prompt.AppendLine(isZh
                ? "- 若同意: 依据角色性格简短回应，并在台词最末尾附加 [ACTION:STAY_HOME]。"
                : "- IF ACCEPTING: Respond briefly in character, and append [ACTION:STAY_HOME] at the absolute end.");
            prompt.AppendLine(isZh
                ? "- 若拒绝: 仅输出对白说明缘由，不附加标签。"
                : "- IF DECLINING: Explain in character as dialogue text only, no tags.");
            prompt.AppendLine("</movement_instruction>\n");
            return;
        }

        // ── VT-FOLLOW-T2: All-day follow ──
        if (CurrentFlags.RequestedAction == ActionTag.AllDayFollow)
        {
            prompt.AppendLine("<movement_instruction mode=\"all_day_follow\">");
            prompt.AppendLine(isZh
                ? "农夫希望你今天一整天都陪着他。"
                : "The farmer wants you to accompany them all day.");
            prompt.AppendLine(isZh
                ? "- 若同意: 依据角色性格与好感度表达回应，并在台词最末尾附加 [ACTION:ALL_DAY_FOLLOW]。"
                : "- IF ACCEPTING: Respond in character, and append [ACTION:ALL_DAY_FOLLOW] at the absolute end.");
            prompt.AppendLine(isZh
                ? "- 若拒绝: 仅输出对白说明缘由，不附加标签。"
                : "- IF DECLINING: Explain in character as dialogue text only, no tags.");
            prompt.AppendLine("</movement_instruction>\n");
            return;
        }

        if (CurrentFlags.IsFollowing && !CurrentFlags.IsMovementRequested)
        {
            prompt.AppendLine("<movement_instruction mode=\"following\">");
            // 修复：删除"轻松随和且专注陪伴"
            prompt.AppendLine(isZh
                ? "你此刻正跟在农夫身边。依据角色性格与当前好感度进行表达。"
                : "You are currently following the farmer. Respond according to character personality and current heart level.");
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
                ? "- 匹配成功: 文字回应，并在台词最末尾附带 [ACTION:GOTO:x,y]（使用列表中精准坐标）。"
                : "- MATCH FOUND: Respond AND append [ACTION:GOTO:x,y] at the end using exact coordinates.");
            prompt.AppendLine(isZh
                ? "- 未能匹配: 说明找不到该物品，结束输出。"
                : "- NO MATCH: Explain that you cannot find it as regular dialogue.");
            prompt.AppendLine("</movement_instruction>\n");
            return;
        }

        if (CurrentFlags.IsPathBlocked)
        {
            prompt.AppendLine("<movement_instruction mode=\"blocked\">");
            prompt.AppendLine(isZh
                ? $"向 {CurrentFlags.BlockDirection.ToString().ToLower()} 移动的路径被障碍物阻挡。指出前面的阻碍并说明无法过去。"
                : $"Path to the {CurrentFlags.BlockDirection.ToString().ToLower()} is BLOCKED. Acknowledge the barrier and explain why you cannot move.");
            prompt.AppendLine("</movement_instruction>\n");
            return;
        }

        if (CurrentFlags.IsAlreadyAdjacent)
        {
            prompt.AppendLine("<movement_instruction mode=\"adjacent\">");
            // 修复：删除"打趣对方重复要求"
            prompt.AppendLine(isZh
                ? "你此刻已经站在农夫正前方。依据角色性格与好感度对重复请求做出回应。"
                : "You are ALREADY standing face-to-face with the farmer. Address the repeated request according to character personality and heart level.");
            prompt.AppendLine("</movement_instruction>\n");
            return;
        }

        prompt.AppendLine("<movement_instruction mode=\"move_request\">");
        prompt.AppendLine(isZh
            ? "玩家要求你移动，前方路径畅通。"
            : "The player asked you to move. The path is CLEAR.");
        prompt.AppendLine(isZh
            ? "- 若同意: 将对应动作标签（如 [ACTION:STEP:FORWARD]、[ACTION:STEP:LEFT]）置于台词最末尾。"
            : "- IF ACCEPTING: Place the action tag (e.g. [ACTION:STEP:FORWARD], [ACTION:STEP:LEFT]) at the absolute end.");
        prompt.AppendLine(isZh
            ? "- 若拒绝: 保持纯口头对白说明缘由，结束输出。"
            : "- IF DECLINING: Provide pure conversational reasoning as regular dialogue.");
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
        if (friendship.IsEngaged() && string.IsNullOrEmpty(PendingMilestoneBlock))
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
        bool isDating = false;
        if (Game1.player?.friendshipData.TryGetValue(Character.Name, out var fs) == true)
        {
            isDating = fs.IsDating();
        }
        string line = GetFriendshipText(hearts, isSingle, isChild, isDating);
        if (!string.IsNullOrWhiteSpace(line))
            prompt.AppendLine(line);
    }

    private string GetFriendshipText(int hearts, bool isSingle, bool isChild, bool isDating)
    {
        string note = Util.GetString(Character, "friendshipNote");
        if (hearts < 0)
            return Util.GetString(Character, "friendshipFirstMeeting", new { Note = note });
        if (hearts < 2)
            return Util.GetString(Character, "friendshipStrangers", new { Hearts = hearts, Note = note });
        if (hearts < 4)
            return Util.GetString(Character, "friendshipNeighbors", new { Hearts = hearts, Note = note });
        if (hearts < 6)
            return Util.GetString(Character, "friendshipFriends", new { Hearts = hearts, Note = note });
        if (hearts < 8)
            return Util.GetString(Character, "friendshipCloseFriends", new { Hearts = hearts, Note = note });
        if (isChild)
            return Util.GetString(Character, "friendshipChild8Plus", new { Hearts = hearts, Note = note });
        if (isSingle)
        {
            if (isDating)
                return Util.GetString(Character, "friendshipDating", new { Hearts = hearts, Note = note });
            return Util.GetString(Character, "friendshipBestFriends", new { Hearts = hearts, Note = note });
        }
        return Util.GetString(Character, "friendshipLongTermBond", new { Hearts = hearts, Note = note });
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
        /*
        // ── 【旧版送礼逻辑注释说明】 ──
        // 1. 玩家送礼即时响应已由统一的 PerceptionInjector 接管（具有严格的单次注入即消费机制，防多轮复读）。
        // 2. 原版 Context.Accept 是静态字段，在玩家不关闭对话框持续对话时不会自动清空，会导致多轮对话持续残留送礼指令。
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
        else
        */
        if (!string.IsNullOrEmpty(giveGift))
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
            (Season.Winter, 1) => Util.GetString(Character, "specialDatesWinter1"),
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

        // 统一且纯粹地映射游戏底层头顶表情资产：只讲格式与对应事实，不给角色预设行为与道德条框
        if (isZh)
        {
            commandPrompt.AppendLine("- 表情气泡标签（对应角色头顶动画）：");
            commandPrompt.AppendLine("  * [ACTION:EMOTE:HAPPY]：开心、微笑");
            commandPrompt.AppendLine("  * [ACTION:EMOTE:HEART]：爱心");
            commandPrompt.AppendLine("  * [ACTION:EMOTE:BLUSH]：害羞");
            commandPrompt.AppendLine("  * [ACTION:EMOTE:SURPRISE]：惊讶");
            commandPrompt.AppendLine("  * [ACTION:EMOTE:SAD]：难过");
            commandPrompt.AppendLine("  * [ACTION:EMOTE:ANGRY]：生气");
        }
        else
        {
            commandPrompt.AppendLine("- Emote bubble tags (head animation triggers):");
            commandPrompt.AppendLine("  * [ACTION:EMOTE:HAPPY]: Happy, smile");
            commandPrompt.AppendLine("  * [ACTION:EMOTE:HEART]: Heart");
            commandPrompt.AppendLine("  * [ACTION:EMOTE:BLUSH]: Blush");
            commandPrompt.AppendLine("  * [ACTION:EMOTE:SURPRISE]: Surprise");
            commandPrompt.AppendLine("  * [ACTION:EMOTE:SAD]: Sad");
            commandPrompt.AppendLine("  * [ACTION:EMOTE:ANGRY]: Angry");
        }

        commandPrompt.AppendLine(isZh ? "- 转向标签: [ACTION:FACE:FARMER] (面向玩家), [ACTION:FACE:UP] / [DOWN] / [LEFT] / [RIGHT]" : "- Turn tags: [ACTION:FACE:FARMER] (look at player), [ACTION:FACE:UP] / [DOWN] / [LEFT] / [RIGHT]");
        commandPrompt.AppendLine(isZh ? "- 位移标签: [ACTION:STEP:FORWARD], [ACTION:STEP:BACKWARD], [ACTION:STEP:LEFT], [ACTION:STEP:RIGHT]" : "- Movement tags: [ACTION:STEP:FORWARD], [ACTION:STEP:BACKWARD], [ACTION:STEP:LEFT], [ACTION:STEP:RIGHT]");
        commandPrompt.AppendLine();
        commandPrompt.AppendLine(isZh ? "格式规则 — 动作标签置于台词的最末尾：" : "OUTPUT FORMAT — Action tags MUST be placed at the absolute end of spoken line:");
        commandPrompt.AppendLine("  [Dialogue Text] [ACTION:STEP:BACKWARD]");
        commandPrompt.AppendLine("  [Dialogue Text] [ACTION:EMOTE:HAPPY]");

        // Skip language instruction when target is English or InvariantCulture (no meaningful constraint)
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

    private string GetInstructions()
    {
        var instructions = new StringBuilder();
        bool isZh = IsChineseLanguage;
        instructions.AppendLine($"## {Util.GetString(Character, "instructionsHeading", new { Language = TargetLanguageName })}");
        instructions.AppendLine(Util.GetString(Character, "instructionsIntro", new { Name = Name }));
        instructions.AppendLine(Util.GetString(Character, "instructionsFarmersName"));
        instructions.AppendLine(Util.GetString(Character, "instructionsBreaks"));
        instructions.AppendLine(Util.GetString(Character, "instructionsSingleLine"));
        instructions.AppendLine(Util.GetString(Character, "instructionsResponses", new { Name = Name }));
        instructions.AppendLine(Util.GetString(Character, "instructionsFallback", new { Name = Name }));
        instructions.AppendLine(isZh
            ? "- 【核心视角】仅输出你自身角色的言语、动作与神态反应。完成当前台词表达后立即停下，将话语权交还给面前的农夫。"
            : "- [CORE PERSPECTIVE] Output only your own character's dialogue, actions, and mannerisms. Conclude your lines cleanly and yield the floor to the farmer.");
        instructions.AppendLine(isZh
            ? "- 若本次对话结束后你的情绪明显转变（如变得好奇/生气/高兴），在台词最末尾附加 [MOOD:curious] / [MOOD:annoyed] / [MOOD:happy] 等标签。"
            : "- If your emotional tone has clearly shifted after this exchange (e.g. curious/annoyed/happy), append [MOOD:curious] / [MOOD:annoyed] / [MOOD:happy] at the absolute end.");
        instructions.AppendLine(isZh
            ? "- 【选项范围】% 发言选项，取材范围仅限于你已经在台词中亲口说出的内容——这是农夫能听到、能借此接话的信息。你的内心思绪（preoccupation / pending_thought）与偷听到的内容，要等你自己说出口之后，才算进入这个范围。"
            : "- [OPTION SCOPE] % options draw only from what you have actually said aloud in dialogue — that's the information the farmer has heard and can respond to. Your inner thoughts (preoccupation / pending_thought) and anything overheard enter that scope only once you've voiced them yourself.");

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