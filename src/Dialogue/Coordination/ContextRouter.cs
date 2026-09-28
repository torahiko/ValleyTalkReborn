// ContextRouter.cs
// ═══════════════════════════════════════════════════════════════════════════
// CONTEXT ROUTING ARCHITECTURE
// ═══════════════════════════════════════════════════════════════════════════
//
// This file is responsible for analyzing player input and game state to determine:
// 1. What type of dialogue generation is needed (greeting, continuation, action)
// 2. Which context components should be included (memories, environment, farm details)
// 3. What actions the player is requesting (follow, move, go to, date invitation)
//
// ───────────────────────────────────────────────────────────────────────────
// ROUTING PHASES (executed in sequence):
// ───────────────────────────────────────────────────────────────────────────
//
// Phase 1: Date State Evaluation
//   +- Check for pending stood-up scenario (player missed date last night)
//   +- Detect if NPC is currently on a date with player
//   +- Detect date invitation intent in player input
//   +- Check for jealousy trigger (player invited someone else)
//
// Phase 2: Follow State Evaluation
//   +- Read actual NPC following status from MovementManager
//
// Phase 3: Movement & Action Detection
//   +- Detect directional movement (forward, backward, left, right)
//   +- Detect follow/stop-follow commands
//   +- Detect special commands (stay home, all-day follow)
//   +- Detect goto intent (navigate to object/location)
//   +- Check path blockage (collision detection)
//
// Phase 4: Simple Greeting Fast Path
//   +- Exact match against greeting keywords ("hi", "hello")
//   +- First conversation today (TalkedToToday == false)
//   +- Skip if: spouse/roommate, stood-up pending, on date, jealousy, action requested, following
//   +- If matched: minimal context (no memories, no farm details)
//
// Phase 5: Context Switches
//   +- IncludeSafetyRules: always true unless SafetyMode == Off
//   +- IncludeMemories: true if memory count > 0
//   +- IncludeEnvironment: true by default
//   +- IncludeFarmDetails: true by default
//   +- IncludeShortTermContext: based on IsActiveTurn flag (Turn 0 vs Turn 1+)
//
// ───────────────────────────────────────────────────────────────────────────
// TURN DETECTION LOGIC:
// ───────────────────────────────────────────────────────────────────────────
// Turn 0 (New Opening):
//   - Player just right-clicked NPC (DialogueBuilder.Generate)
//   - NPC approached player proactively
//   - IsActiveTurn = false
//   - Full context including environment, player profile, held items
//
// Turn 1+ (Continuous Dialogue):
//   - Player selected a response option in active dialogue box (DialogueBuilder.GenerateResponse)
//   - IsActiveTurn = true
//   - Channel-level silence: ordinary held items suppressed (unless special items)
//   - Short-term context always included
//
// ───────────────────────────────────────────────────────────────────────────
// KEY DESIGN PRINCIPLES:
// ───────────────────────────────────────────────────────────────────────────
// 1. Explicit Turn Tracking: Uses DialogueContext.IsActiveTurn flag instead
//    of inferring from ChatHistory (which persists across dialogue sessions)
//
// 2. Action Hierarchy: Directional movement and follow actions are separate
//    concepts. IsMovementRequested only true for directional/goto, not follow.
//
// 3. Negation Protection: Commands starting with "don't" are ignored
//    to prevent false positives ("don't kiss me" should not trigger action)
//
// 4. Boundary-Aware Matching: English keywords use word boundary detection
//    to avoid false matches (e.g., "following" in a sentence about a TV show)
//
// 5. Special Item Penetration: Legendary fish, mayor's shorts, etc. bypass
//    channel-level silence to ensure important items are always noticed
//
// ═══════════════════════════════════════════════════════════════════════════

#nullable disable

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;

namespace ValleytalkReborn.Dialogue.Coordination;

// ─────────────────────────────────────────────────────────
// Enums
// ─────────────────────────────────────────────────────────

public enum BlockDirection
{
    None,
    Forward,
    Backward,
    Left,
    Right,
    Up,
    Down
}

public enum ActionTag
{
    None,

    StepForward,
    StepBackward,
    StepLeft,
    StepRight,
    StepUp,
    StepDown,

    Follow,
    StopFollow,

    StayHome,
    AllDayFollow
}

public static class ActionTagExtensions
{
    public static string ToTagString(this ActionTag tag)
    {
        switch (tag)
        {
            case ActionTag.StepForward:
                return "STEP:FORWARD";

            case ActionTag.StepBackward:
                return "STEP:BACKWARD";

            case ActionTag.StepLeft:
                return "STEP:LEFT";

            case ActionTag.StepRight:
                return "STEP:RIGHT";

            case ActionTag.StepUp:
                return "STEP:UP";

            case ActionTag.StepDown:
                return "STEP:DOWN";

            case ActionTag.Follow:
                return "FOLLOW";

            case ActionTag.StopFollow:
                return "STOP_FOLLOW";


            case ActionTag.StayHome:
                return "STAY_HOME";

            case ActionTag.AllDayFollow:
                return "ALL_DAY_FOLLOW";

            default:
                return string.Empty;
        }
    }

    public static ActionTag FromTagString(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return ActionTag.None;

        string text = value.Trim();

        text = text.Trim(
            '[', ']',
            ' ', '\t', '\r', '\n'
        );

        if (text.StartsWith("ACTION:", StringComparison.OrdinalIgnoreCase))
        {
            text = text.Substring("ACTION:".Length).Trim();
        }

        text = text
            .Replace('-', '_')
            .ToUpperInvariant();

        switch (text)
        {
            case "STEP:FORWARD":
            case "STEP_FORWARD":
                return ActionTag.StepForward;

            case "STEP:BACKWARD":
            case "STEP_BACKWARD":
                return ActionTag.StepBackward;

            case "STEP:LEFT":
            case "STEP_LEFT":
                return ActionTag.StepLeft;

            case "STEP:RIGHT":
            case "STEP_RIGHT":
                return ActionTag.StepRight;

            case "STEP:UP":
            case "STEP_UP":
                return ActionTag.StepUp;

            case "STEP:DOWN":
            case "STEP_DOWN":
                return ActionTag.StepDown;

            case "FOLLOW":
                return ActionTag.Follow;

            case "STOP_FOLLOW":
            case "STOPFOLLOW":
                return ActionTag.StopFollow;


            case "STAY_HOME":
                return ActionTag.StayHome;

            case "ALL_DAY_FOLLOW":
                return ActionTag.AllDayFollow;

            default:
                return ActionTag.None;
        }
    }

    public static bool IsDirectionalMovement(this ActionTag tag)
    {
        return tag == ActionTag.StepForward
            || tag == ActionTag.StepBackward
            || tag == ActionTag.StepLeft
            || tag == ActionTag.StepRight
            || tag == ActionTag.StepUp
            || tag == ActionTag.StepDown;
    }

    public static bool IsFollowAction(this ActionTag tag)
    {
        return tag == ActionTag.Follow
            || tag == ActionTag.StopFollow
            || tag == ActionTag.AllDayFollow;
    }
}

public enum CompanionFocusMode
{
    None,
    RegularFollow,
    DateWalking,
    DateSettled
}

// ─────────────────────────────────────────────────────────
// Dependency interfaces
// ─────────────────────────────────────────────────────────

public interface IMemoryProvider
{
    int GetMemoryCount(string npcName);
}

public interface IStoodUpProvider
{
    string GetPendingStoodUp(string npcName);
}

public interface IDateStateProvider
{
    string ActiveDateNpcName { get; }
    string ActiveDateLocation { get; }

    bool IsOnDate(string npcName);
    void ConsumeDate(string npcName);
}

// ─────────────────────────────────────────────────────────
// Movement world evaluation
// ─────────────────────────────────────────────────────────

// Memory-only：只在一次对话回合内有效，不写入任何存档 / ModData / Config。
// 由世界状态求值产生，是 IsPathBlocked / BlockDirection / IsAlreadyAdjacent 的唯一来源。
internal sealed record MovementEvaluation(
    bool IsEvaluated,
    bool IsBlocked,
    BlockDirection BlockDirection,
    bool IsAlreadyAdjacent);

// ─────────────────────────────────────────────────────────
// ContextFlags
// ─────────────────────────────────────────────────────────

public sealed class ContextFlags
{
    // Context switches
    public bool IncludeSafetyRules = true;
    public bool IncludeShortTermContext = false;
    public bool IncludeMemories = false;
    public bool IncludeEnvironment = true;
    public bool IncludeFarmDetails = true;

    // Dialogue intent
    public bool IsSimpleGreeting = false;
    public bool IsFarewell = false;

    // General action state
    public bool IsActionRequested = false;
    public ActionTag RequestedAction = ActionTag.None;

    // Movement / collision
    // 注意：此字段现在只表示方向移动或 GoTo，不再表示 Follow 等普通动作。
    public bool IsMovementRequested = false;

    public bool IsPathBlocked = false;
    public BlockDirection BlockDirection = BlockDirection.None;
    public bool IsAlreadyAdjacent = false;

    // Follow state
    public bool IsFollowing = false;

    // GoTo intent
    public bool IsGotoRequested = false;
    public string GotoIntentText = string.Empty;

    // Date system
    public bool IsOnDate = false;
    public CompanionFocusMode CompanionFocus = CompanionFocusMode.None;
    public string DateLocationId = string.Empty;
    public bool IsInviteRequested = false;
    public bool HasStoodUpPending = false;
    public string StoodUpDate = string.Empty;
    public bool IsJealousy = false;

    public ContextFlags Clone()
    {
        return new ContextFlags
        {
            IncludeSafetyRules = IncludeSafetyRules,
            IncludeShortTermContext = IncludeShortTermContext,
            IncludeMemories = IncludeMemories,
            IncludeEnvironment = IncludeEnvironment,
            IncludeFarmDetails = IncludeFarmDetails,

            IsSimpleGreeting = IsSimpleGreeting,
            IsFarewell = IsFarewell,

            IsActionRequested = IsActionRequested,
            RequestedAction = RequestedAction,
            IsMovementRequested = IsMovementRequested,

            IsPathBlocked = IsPathBlocked,
            BlockDirection = BlockDirection,
            IsAlreadyAdjacent = IsAlreadyAdjacent,

            IsFollowing = IsFollowing,

            IsGotoRequested = IsGotoRequested,
            GotoIntentText = GotoIntentText,

            IsOnDate = IsOnDate,
            CompanionFocus = CompanionFocus,
            DateLocationId = DateLocationId,
            IsInviteRequested = IsInviteRequested,
            HasStoodUpPending = HasStoodUpPending,
            StoodUpDate = StoodUpDate,
            IsJealousy = IsJealousy
        };
    }
}

// ─────────────────────────────────────────────────────────
// Route input carrier
// ─────────────────────────────────────────────────────────

internal sealed record ContextRouteInput(
    NPC Npc,
    string PlayerInput,
    SafetyModeLevel SafetyMode,
    List<ConversationElement> ChatHistory,
    bool IsActiveTurn);

// ─────────────────────────────────────────────────────────
// Keyword library
// ─────────────────────────────────────────────────────────

internal static class Keywords
{
    internal static readonly string[] MemoryZh =
    {
        "还记得",
        "上次你说过",
        "你之前说",
        "还记得吗",
        "回忆"
    };

    internal static readonly string[] MemoryEn =
    {
        "remember",
        "you said",
        "recall",
        "you told me",
        "last time"
    };

    internal static readonly string[] TopicResetZh =
    {
        "换个话题",
        "换一个话题",
        "我们聊点别的",
        "不说这个了"
    };

    internal static readonly string[] TopicResetEn =
    {
        "change the subject",
        "change topic",
        "on another topic",
        "let's talk about something else"
    };

    internal static readonly string[] Intimacy =
    {
        "抱抱",
        "抱我",
        "让我抱",
        "摸摸",
        "让我摸",
        "吹吹",
        "贴贴",
        "hug me"
    };

    internal static readonly string[] InviteZh =
    {
        "约你",
        "一起去",
        "陪我去",
        "带你去",
        "要不要去",
        "去约会"
    };

    internal static readonly string[] InviteEn =
    {
        "go on a date",
        "meet me tonight",
        "let's go to",
        "take you to"
    };

    internal static readonly string[] EnvQueryZh =
    {
        "旁边",
        "周围",
        "附近",
        "身边",
        "那里有",
        "这里有",
        "看到什么",
        "有什么"
    };

    internal static readonly string[] EnvQueryEn =
    {
        "nearby",
        "around",
        "next to",
        "what's here",
        "what do you see",
        "surroundings"
    };
}

// ─────────────────────────────────────────────────────────
// Unicode helpers
// ─────────────────────────────────────────────────────────

internal static class UnicodeHelper
{
    internal static bool IsCjkIdeograph(char c)
    {
        return (c >= '\u4E00' && c <= '\u9FFF')
            || (c >= '\u3400' && c <= '\u4DBF')
            || (c >= '\uF900' && c <= '\uFAFF');
    }

    internal static bool ContainsCjk(string s)
    {
        if (string.IsNullOrEmpty(s))
            return false;

        foreach (char c in s)
        {
            if (IsCjkIdeograph(c))
                return true;
        }

        return false;
    }
}

// ─────────────────────────────────────────────────────────
// ContextRouter
// ─────────────────────────────────────────────────────────

public static class ContextRouter
{
    public static IMemoryProvider MemoryProvider { get; set; }
    public static IDateStateProvider DateStateProvider { get; set; }
    public static IStoodUpProvider StoodUpProvider { get; set; }

    private static IMemoryProvider Memory
    {
        get
        {
            if (MemoryProvider != null)
                return MemoryProvider;

            return MemoryManager.Instance as IMemoryProvider;
        }
    }

    private static IDateStateProvider DateState
    {
        get
        {
            if (DateStateProvider != null)
                return DateStateProvider;

            return DateManager.Instance as IDateStateProvider;
        }
    }

    private static IStoodUpProvider StoodUp
    {
        get
        {
            if (StoodUpProvider != null)
                return StoodUpProvider;

            return StoodUpTracker.Instance as IStoodUpProvider;
        }
    }

    private static readonly char[] WordSeparators =
    {
        ' ', ',', '.', '!', '?', ';', ':',
        '\t', '\n', '\r',
        '，', '。', '！', '？', '；', '：', '、',
        '(', ')', '（', '）'
    };

    // ─────────────────────────────────────────────────────
    // Route entry point
    // ─────────────────────────────────────────────────────

    internal static ContextFlags Evaluate(ContextRouteInput input)
    {
        if (input == null)
            throw new ArgumentNullException(nameof(input));

        bool debugEnabled = ModEntry.Config?.Debug ?? false;

        return EvaluateInternal(input, debugEnabled);
    }

    private static ContextFlags EvaluateInternal(
        ContextRouteInput input,
        bool debugEnabled)
    {
        var flags = new ContextFlags();

        if (input.Npc == null)
        {
            DebugLog(debugEnabled, "Evaluate skipped: npc is null.");
            return flags;
        }

        string originalInput = input.PlayerInput?.Trim() ?? string.Empty;
        string cleanInput = originalInput.ToLowerInvariant();
        bool hasInput = cleanInput.Length > 0;

        // 输入边界归一化：ChatHistory 为 null 时视作空列表，下游不再重复判空。
        var chatHistory = input.ChatHistory ?? new List<ConversationElement>();
        bool isActiveTurn = input.IsActiveTurn;

        DebugLog(
            debugEnabled,
            $"Input npc={input.Npc.Name}, raw=\"{originalInput}\", clean=\"{cleanInput}\"");

        // Phase 1: 日期、邀请、爽约和嫉妒状态
        EvaluateDateState(
            input.Npc,
            originalInput,
            hasInput,
            flags,
            debugEnabled);

        // Phase 2: 读取真实跟随状态
        EvaluateFollowState(
            input.Npc,
            flags,
            debugEnabled);

        flags.CompanionFocus = CompanionFocusResolver.Resolve(input.Npc);

        // Phase 3: 动作和导航意图
        EvaluateMovement(
            input.Npc,
            originalInput,
            cleanInput,
            hasInput,
            flags,
            debugEnabled);

        bool talkedToday = HasTalkedToToday(input.Npc);

        // Phase 4: 首次问候快速路径
        if (!ShouldSuppressSimpleGreeting(input.Npc, flags))
        {
            if (TryEvaluateAsSimpleGreeting(
                cleanInput,
                hasInput,
                talkedToday,
                input.SafetyMode,
                flags))
            {
                LogFlags(input.Npc.Name, flags, debugEnabled);
                return flags;
            }
        }

        // Phase 5: 上下文开关
        EvaluateContextSwitches(
            input.Npc,
            cleanInput,
            hasInput,
            input.SafetyMode,
            flags,
            talkedToday,
            chatHistory,
            isActiveTurn,
            debugEnabled);

        LogFlags(input.Npc.Name, flags, debugEnabled);
        return flags;
    }

    // ─────────────────────────────────────────────────────
    // Date state
    // ─────────────────────────────────────────────────────

    private static void EvaluateDateState(
        NPC npc,
        string originalInput,
        bool hasInput,
        ContextFlags flags,
        bool debugEnabled)
    {
        string npcName = npc.Name;

        var stoodUp = StoodUp;
        if (stoodUp != null)
        {
            string stoodUpDate = stoodUp.GetPendingStoodUp(npcName);

            if (!string.IsNullOrEmpty(stoodUpDate))
            {
                flags.HasStoodUpPending = true;
                flags.StoodUpDate = stoodUpDate;

                DebugLog(
                    debugEnabled,
                    $"Date state: pending stood-up date={stoodUpDate}");
            }
        }

        var dateState = DateState;

        if (dateState != null)
        {
            if (dateState.IsOnDate(npcName))
            {
                flags.IsOnDate = true;
                flags.DateLocationId =
                    dateState.ActiveDateLocation ?? string.Empty;

                DebugLog(
                    debugEnabled,
                    $"Date state: on date, location={flags.DateLocationId}");
            }

            string activeNpc = dateState.ActiveDateNpcName;

            if (!string.IsNullOrEmpty(activeNpc)
                && !string.Equals(
                    activeNpc,
                    npcName,
                    StringComparison.OrdinalIgnoreCase))
            {
                if (CompanionScheduleManager.IsLegalSpouse(npcName))
                {
                    flags.IsJealousy = true;

                    DebugLog(
                        debugEnabled,
                        $"Date state: jealousy triggered, activeNpc={activeNpc}");
                }
            }
        }

        // 不因为正在约会或存在爽约记录而提前 return。
        // 这样可以同时保留当前输入中的邀请意图。
        EvaluateDateInvitation(npcName, originalInput, hasInput, flags, debugEnabled);

        // ── 约会系统关闭时，强制清除所有约会衍生状态，防止残留语境泄漏到 LLM ──
        if (!ModEntry.Config.EnableDateSystem)
        {
            flags.IsInviteRequested = false;
            flags.IsOnDate = false;
            flags.IsJealousy = false;
            flags.HasStoodUpPending = false;
            flags.StoodUpDate = string.Empty;
        }
    }

    // ─────────────────────────────────────────────────────
    // Date invitation boundary（CTX-005）
    // ─────────────────────────────────────────────────────

    /// <summary>
    /// 邀请边界：检测语言 → 解析显式地点 → 交给 DateRules 校验，三段各自独立。
    /// 只有输入显式给出受支持地点时才把该地点交给 CanScheduleDate 并置位 IsInviteRequested；
    /// 无地点 / 世界未就绪 / 校验失败都不置位，保持纯对白。
    /// 绝不向 CanScheduleDate 传入空地点，也绝不推断或发明地点。
    /// </summary>
    private static void EvaluateDateInvitation(
        string npcName,
        string originalInput,
        bool hasInput,
        ContextFlags flags,
        bool debugEnabled)
    {
        if (!hasInput)
            return;

        if (!TryDetectDateInvitation(originalInput, out string requestedLocationId))
            return;

        if (string.IsNullOrEmpty(requestedLocationId))
        {
            // BOUNDARY：检测到邀请语言但无显式受支持地点，且不存在经核实的默认地点。
            DebugLog(
                debugEnabled,
                "Date invitation detected but no explicit supported location; plain dialogue preserved.");
            return;
        }

        // BOUNDARY：世界未就绪 ⇒ 不构建快照、不求值、不调度，保持纯对白。
        if (!StardewModdingAPI.Context.IsWorldReady)
        {
            DebugLog(
                debugEnabled,
                "Date invitation not evaluated: world not ready.");
            return;
        }

        var world = new DateWorldSnapshot(
            TimeOfDay: Game1.timeOfDay,
            PlayerLocationName: Game1.player?.currentLocation?.Name ?? "",
            IsFestivalDay: Utility.isFestivalDay(Game1.dayOfMonth, Game1.season),
            IsWorldReady: true
        );

        // 前置路径校验：节日 / 时间晚于 18:00 / 当前有其他约会 → 从源头抑制邀约按钮。
        if (!DateRules.CanScheduleDate(
                world,
                requestedLocationId,
                DateState?.ActiveDateNpcName ?? "",
                npcName,
                DateManager.WhitelistedLocations,
                1800))
        {
            // BOUNDARY：地点非法或未白名单 / 节日 / 时间过晚 / 已有约会 ⇒ 不置位已验证邀请标志。
            DebugLog(
                debugEnabled,
                $"Date invitation rejected: loc={requestedLocationId}, time={world.TimeOfDay}, "
                + $"festival={world.IsFestivalDay}, busy={DateState?.ActiveDateNpcName}.");
            return;
        }

        flags.IsInviteRequested = true;

        DebugLog(
            debugEnabled,
            $"Date invitation validated: loc={requestedLocationId}.");
    }

    /// <summary>
    /// 邀请检测（与可调度性校验分离）：判断输入是否包含邀请语言，并解析其中显式出现的受支持地点。
    /// 仅检测语言、不判断可调度性；无显式受支持地点时 requestedLocationId 为空串。
    /// </summary>
    internal static bool TryDetectDateInvitation(string input, out string requestedLocationId)
    {
        requestedLocationId = string.Empty;

        if (string.IsNullOrWhiteSpace(input))
            return false;

        if (!MatchesWithBoundary(
                input.ToLowerInvariant(),
                Keywords.InviteZh,
                Keywords.InviteEn))
        {
            return false;
        }

        requestedLocationId = ResolveExplicitDateLocation(input) ?? string.Empty;
        return true;
    }

    /// <summary>
    /// 从输入文本解析显式出现的受支持约会地点 ID。
    /// 只认可 DateLocationRegistry 已注册的 LocationId / TargetMap 与中英文显示名，
    /// 不引入别名表、默认地点或任何推断。未命中返回 null。
    /// </summary>
    internal static string ResolveExplicitDateLocation(string input)
    {
        var locations = DateLocationRegistry.Locations;

        if (string.IsNullOrWhiteSpace(input) || locations == null)
            return null;

        foreach (var pair in locations)
        {
            var info = pair.Value;

            if (info == null)
                continue;

            // ASCII 标识（地点 ID / 目标地图 / 英文名）按词边界匹配，避免 "town" 误命中 "downtown"。
            if (MatchesWordBoundaryAny(input, pair.Key, info.TargetMap, info.DisplayNameEn))
                return pair.Key;

            // CJK 无词边界，按中文显示名子串匹配。
            if (!string.IsNullOrWhiteSpace(info.DisplayNameZh)
                && input.IndexOf(info.DisplayNameZh, StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return pair.Key;
            }
        }

        return null;
    }

    // ─────────────────────────────────────────────────────
    // Follow state
    // ─────────────────────────────────────────────────────

    private static void EvaluateFollowState(
        NPC npc,
        ContextFlags flags,
        bool debugEnabled)
    {
        var movement = MovementManager.Instance;

        if (movement != null)
        {
            flags.IsFollowing = movement.IsFollowing(npc);
        }

        DebugLog(
            debugEnabled,
            $"Follow state: isFollowing={flags.IsFollowing}");
    }

    // ─────────────────────────────────────────────────────
    // Movement / action detection
    // ─────────────────────────────────────────────────────

    private static void EvaluateMovement(
        NPC npc,
        string originalInput,
        string cleanInput,
        bool hasInput,
        ContextFlags flags,
        bool debugEnabled)
    {
        if (!hasInput)
        {
            DebugLog(debugEnabled, "Action detection skipped: empty input.");
            return;
        }

        // CTX-004：意图识别不得读取世界状态。原来的 npc.currentLocation 守卫已移除
        // （它在无世界时会 NRE，且把纯文本意图与地图状态耦合在一起）。
        ActionTag detectedTag = ActionIntentClassifier.Detect(cleanInput);

        if (detectedTag != ActionTag.None)
        {
            flags.IsActionRequested = true;
            flags.RequestedAction = detectedTag;

            // 只有方向移动才设置 IsMovementRequested。
            flags.IsMovementRequested =
                detectedTag.IsDirectionalMovement();

            DebugLog(
                debugEnabled,
                $"Action detected: tag={detectedTag}, "
                + $"isAction=true, "
                + $"isMovement={flags.IsMovementRequested}");

            // 方向移动进入世界求值；Follow / StopFollow / StayHome / AllDayFollow
            // 不是方向移动，不产生任何阻挡或邻接标志。
            if (flags.IsMovementRequested)
            {
                MovementEvaluation evaluation =
                    EvaluateDirectionalMovement(npc, detectedTag, debugEnabled);

                ApplyMovementEvaluation(flags, evaluation);
            }

            return;
        }

        string gotoText;
        if (ActionIntentClassifier.TryDetectGoto(cleanInput, out gotoText))
        {
            flags.IsGotoRequested = true;
            flags.IsActionRequested = true;
            flags.IsMovementRequested = true;
            flags.GotoIntentText =
                string.IsNullOrWhiteSpace(originalInput)
                    ? gotoText
                    : originalInput;

            DebugLog(
                debugEnabled,
                $"GoTo detected: text=\"{flags.GotoIntentText}\"");

            return;
        }

        DebugLog(
            debugEnabled,
            "Action detection result: no explicit action.");
    }

    // ─────────────────────────────────────────────────────
    // Movement world evaluation（主线程序求值边界）
    // ─────────────────────────────────────────────────────

    /// <summary>
    /// 纯决策核心：不读取 Game1 / NPC / 地图，只按给定的探测结果判定。
    /// 邻接优先于阻挡；阻挡判定复刻 MovementCoordinator 执行真相
    /// （pos1 不可走 ⇒ 执行端 faceGeneralDirection 仅转向）。
    /// CTX-009：pos2 只决定执行端滑一格还是两格，不参与阻挡判定，故不作为入参。
    /// </summary>
    internal static MovementEvaluation ResolveDirectionalEvaluation(
        bool isAdjacentTarget,
        bool pos1Walkable,
        BlockDirection requestedDirection)
    {
        if (isAdjacentTarget)
        {
            return new MovementEvaluation(
                IsEvaluated: true,
                IsBlocked: false,
                BlockDirection: BlockDirection.None,
                IsAlreadyAdjacent: true);
        }

        bool isBlocked = !pos1Walkable;

        return new MovementEvaluation(
            IsEvaluated: true,
            IsBlocked: isBlocked,
            BlockDirection: isBlocked ? requestedDirection : BlockDirection.None,
            IsAlreadyAdjacent: false);
    }

    private static MovementType ToStepMovementType(ActionTag tag)
    {
        switch (tag)
        {
            case ActionTag.StepForward:  return MovementType.Forward;
            case ActionTag.StepBackward: return MovementType.Backward;
            case ActionTag.StepLeft:     return MovementType.Left;
            case ActionTag.StepRight:    return MovementType.Right;
            case ActionTag.StepUp:       return MovementType.Up;
            case ActionTag.StepDown:     return MovementType.Down;
            default:                     return MovementType.None;
        }
    }

    private static BlockDirection ToBlockDirection(ActionTag tag)
    {
        switch (tag)
        {
            case ActionTag.StepForward:  return BlockDirection.Forward;
            case ActionTag.StepBackward: return BlockDirection.Backward;
            case ActionTag.StepLeft:     return BlockDirection.Left;
            case ActionTag.StepRight:    return BlockDirection.Right;
            case ActionTag.StepUp:       return BlockDirection.Up;
            case ActionTag.StepDown:     return BlockDirection.Down;
            default:                     return BlockDirection.None;
        }
    }

    /// <summary>
    /// 主线程、对话期一次性调用，禁止进入 UpdateTicked / 每帧路径。
    /// 未通过边界守卫时返回 IsEvaluated=false，调用方据此保持 flags 默认。
    /// </summary>
    private static MovementEvaluation EvaluateDirectionalMovement(
        NPC npc,
        ActionTag tag,
        bool debugEnabled)
    {
        // 守卫顺序（CTX-001 实证）：IsWorldReady / Game1.player 必须早于 npc.currentLocation，
        // 无世界时 currentLocation 的 getter 会 NRE。
        if (!StardewModdingAPI.Context.IsWorldReady || Game1.player == null)
        {
            DebugLog(
                debugEnabled,
                "Movement evaluation not performed: world or player unavailable.");
            return new MovementEvaluation(false, false, BlockDirection.None, false);
        }

        GameLocation loc = npc.currentLocation;
        if (loc == null)
        {
            DebugLog(
                debugEnabled,
                "Movement evaluation not performed: npc has no current location.");
            return new MovementEvaluation(false, false, BlockDirection.None, false);
        }

        MovementType step = ToStepMovementType(tag);
        var (dx, dy) = MovementPathfinding.ResolveStepDelta(step, npc.FacingDirection);

        Vector2 npcTile = npc.Tile;
        Vector2 pos1 = new Vector2(npcTile.X + dx, npcTile.Y + dy);

        bool pos1Walkable;

        try
        {
            pos1Walkable = MovementPathfinding.IsTileWalkable(loc, pos1, npc);
        }
        catch (Exception ex)
        {
            ModEntry.SMonitor?.Log(
                $"[ContextRouter] Movement evaluation failed for {npc.Name}: {ex.Message}",
                LogLevel.Error);
            throw;
        }

        // 邻接沿用既有等值语义：请求方向的下一格正是玩家所在格 ⇒ 已面对面。
        bool isAdjacentTarget = pos1 == Game1.player.Tile;

        return ResolveDirectionalEvaluation(
            isAdjacentTarget,
            pos1Walkable,
            ToBlockDirection(tag));
    }

    /// <summary>
    /// MovementEvaluation 是这三个 flags 的唯一赋值来源；未求值时保持默认。
    /// </summary>
    private static void ApplyMovementEvaluation(
        ContextFlags flags,
        MovementEvaluation evaluation)
    {
        if (!evaluation.IsEvaluated)
            return;

        flags.IsPathBlocked = evaluation.IsBlocked;
        flags.BlockDirection = evaluation.BlockDirection;
        flags.IsAlreadyAdjacent = evaluation.IsAlreadyAdjacent;
    }

    // ─────────────────────────────────────────────────────
    // Greeting
    // ─────────────────────────────────────────────────────

    private static bool TryEvaluateAsSimpleGreeting(
        string cleanInput,
        bool hasInput,
        bool talkedToday,
        SafetyModeLevel safetyMode,
        ContextFlags flags)
    {
        if (talkedToday || !hasInput)
            return false;

        if (!DialogueIntentClassifier.IsSimpleGreeting(cleanInput))
            return false;

        flags.IsSimpleGreeting = true;
        flags.IsFarewell = DialogueIntentClassifier.IsFarewell(cleanInput);

        flags.IncludeSafetyRules =
            safetyMode != SafetyModeLevel.Off;

        flags.IncludeShortTermContext = false;
        flags.IncludeMemories = false;
        flags.IncludeEnvironment = false;
        flags.IncludeFarmDetails = false;

        return true;
    }

    private static bool ShouldSuppressSimpleGreeting(NPC npc, ContextFlags flags)
    {
        // 伴侣与室友同住同作息，始终需要完整的家庭与生活上下文
        bool isSpouseOrRoommate = false;
        if (npc != null && Game1.player?.friendshipData != null)
        {
            if (Game1.player.friendshipData.TryGetValue(npc.Name, out var fs))
            {
                isSpouseOrRoommate = fs.IsMarried() || fs.IsRoommate();
            }
        }

        return isSpouseOrRoommate
            || flags.HasStoodUpPending
            || flags.IsOnDate
            || flags.IsJealousy
            || flags.IsInviteRequested
            || flags.IsActionRequested
            || flags.IsFollowing;
    }

    // ─────────────────────────────────────────────────────
    // Context switches
    // ─────────────────────────────────────────────────────

    private static void EvaluateContextSwitches(
        NPC npc,
        string cleanInput,
        bool hasInput,
        SafetyModeLevel safetyMode,
        ContextFlags flags,
        bool talkedToday,
        List<ConversationElement> chatHistory,
        bool isActiveTurn,
        bool debugEnabled)
    {
        flags.IncludeSafetyRules =
            safetyMode != SafetyModeLevel.Off;

        var memory = Memory;

        if (memory is MemoryManager memoryManager)
        {
            memoryManager.EnsureLoaded();
        }

        int memoryCount = 0;

        if (memory != null)
        {
            memoryCount = Math.Max(
                0,
                memory.GetMemoryCount(npc.Name));
        }

        // 没有任何记忆时不注入 Memory Prompt。
        flags.IncludeMemories = memoryCount > 0;

        flags.IncludeEnvironment = true;
        flags.IncludeFarmDetails = true;

        flags.IncludeShortTermContext =
            ShouldIncludeShortTermContext(
                cleanInput,
                hasInput,
                talkedToday,
                chatHistory,
                isActiveTurn);

        // 聚焦态（约会/跟随）下抑制农场细节，让 LLM 聚焦伴侣互动。
        if (flags.CompanionFocus != CompanionFocusMode.None)
            flags.IncludeFarmDetails = false;

        DebugLog(
            debugEnabled,
            $"Context switches: memoryCount={memoryCount}, "
            + $"includeMemories={flags.IncludeMemories}, "
            + $"includeShortTerm={flags.IncludeShortTermContext}");
    }

    private static bool ShouldIncludeShortTermContext(
        string cleanInput,
        bool hasInput,
        bool talkedToday,
        List<ConversationElement> chatHistory,
        bool isActiveTurn)
    {
        // 1. 玩家明确要求重置/换话题
        if (hasInput && ContainsAny(
                cleanInput,
                Keywords.TopicResetZh,
                Keywords.TopicResetEn))
        {
            return false;
        }

        // 2. ★★★ 核心修复：使用显式的 IsActiveTurn 标志判定 ★★★
        //    IsActiveTurn = true  → 当前对话框内的连续交互（Turn 1+），必须保留短期上下文
        //    IsActiveTurn = false → 新开场（Turn 0），根据今天是否交谈过决定
        if (isActiveTurn)
        {
            return true;
        }

        // 3. Turn 0 但今天已经交谈过，保留历史延续感
        if (talkedToday && chatHistory.Any(x => x != null))
        {
            return true;
        }

        return false;
    }

    // ─────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────

    private static bool HasTalkedToToday(NPC npc)
    {
        var player = Game1.player;

        if (player == null || npc == null)
            return false;

        var friendships = player.friendshipData;

        if (friendships == null)
            return false;

        Friendship friendship;

        return friendships.TryGetValue(
            npc.Name,
            out friendship)
            && friendship != null
            && friendship.TalkedToToday;
    }

    private static bool MatchesWordBoundaryAny(
        string input,
        params string[] keywords)
    {
        if (string.IsNullOrWhiteSpace(input))
            return false;

        string[] tokens = input.Split(
            WordSeparators,
            StringSplitOptions.RemoveEmptyEntries);

        foreach (string keyword in keywords)
        {
            if (string.IsNullOrWhiteSpace(keyword))
                continue;

            if (keyword.IndexOf(' ') >= 0)
            {
                if (input.IndexOf(
                    keyword,
                    StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return true;
                }

                continue;
            }

            foreach (string token in tokens)
            {
                if (string.Equals(
                    token,
                    keyword,
                    StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool MatchesWithBoundary(
        string cleanInput,
        string[] zhKeywords,
        string[] enKeywords)
    {
        if (ContainsAny(cleanInput, zhKeywords))
            return true;

        return MatchesWordBoundaryAny(
            cleanInput,
            enKeywords);
    }

    private static bool ContainsAny(
        string input,
        params string[][] keywordArrays)
    {
        if (string.IsNullOrEmpty(input))
            return false;

        foreach (string[] array in keywordArrays)
        {
            if (ContainsAny(input, array))
                return true;
        }

        return false;
    }

    private static bool ContainsAny(
        string input,
        string[] keywords)
    {
        if (string.IsNullOrEmpty(input)
            || keywords == null)
        {
            return false;
        }

        foreach (string keyword in keywords)
        {
            if (!string.IsNullOrEmpty(keyword)
                && input.IndexOf(
                    keyword,
                    StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return true;
            }
        }

        return false;
    }

    // internal 委托桥：保留以兼容外部对旧入口的引用（见票 CTX-003 REV A）。
    // 编译器证实无外部引用后可删除。
    internal static bool TryDetectGotoIntentInternal(
        string cleanInput,
        out string intentText)
    {
        return ActionIntentClassifier.TryDetectGoto(cleanInput, out intentText);
    }

    // ─────────────────────────────────────────────────────
    // Debug logging
    // ─────────────────────────────────────────────────────

    private static void DebugLog(
        bool debugEnabled,
        string message)
    {
        if (!debugEnabled || ModEntry.SMonitor == null)
            return;

        ModEntry.SMonitor.Log(
            "[ContextRouter] " + message,
            LogLevel.Debug);
    }

    private static void LogFlags(
        string npcName,
        ContextFlags flags,
        bool debugEnabled)
    {
        if (!debugEnabled || ModEntry.SMonitor == null)
            return;

        ModEntry.SMonitor.Log(
            $"[ContextRouter] Result npc={npcName} | "
            + $"Greeting={flags.IsSimpleGreeting} "
            + $"Farewell={flags.IsFarewell} | "
            + $"Action={flags.IsActionRequested}"
            + $"({flags.RequestedAction}) "
            + $"Movement={flags.IsMovementRequested} "
            + $"Goto={flags.IsGotoRequested} | "
            + $"Blocked={flags.IsPathBlocked}"
            + $"({flags.BlockDirection}) "
            + $"Adjacent={flags.IsAlreadyAdjacent} | "
            + $"Following={flags.IsFollowing} | "
            + $"OnDate={flags.IsOnDate} "
            + $"Invite={flags.IsInviteRequested} "
            + $"StoodUp={flags.HasStoodUpPending} "
            + $"Jealousy={flags.IsJealousy} | "
            + $"Mem={flags.IncludeMemories} "
            + $"Env={flags.IncludeEnvironment} "
            + $"ShortCtx={flags.IncludeShortTermContext} "
            + $"Farm={flags.IncludeFarmDetails} "
            + $"Focus={flags.CompanionFocus}",
            LogLevel.Debug);
    }
}