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
//   +- Skip if: stood-up pending, on date, jealousy, action requested, following
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
using System.Text.RegularExpressions;
using StardewModdingAPI;
using StardewValley;

namespace ValleytalkReborn;

// ─────────────────────────────────────────────────────────
// Enums
// ─────────────────────────────────────────────────────────

public enum BlockDirection
{
    None,
    Forward,
    Backward,
    Left,
    Right
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
            DateLocationId = DateLocationId,
            IsInviteRequested = IsInviteRequested,
            HasStoodUpPending = HasStoodUpPending,
            StoodUpDate = StoodUpDate,
            IsJealousy = IsJealousy
        };
    }
}

// ─────────────────────────────────────────────────────────
// Intent Regex
// ─────────────────────────────────────────────────────────

internal static class IntentRegex
{
    public static readonly Regex Forward = new Regex(
        @"(往|向|朝)前[走挪跨靠]?(一?小?大?步)?|过来|靠近|走近|近一点|朝我走|"
        + @"\bcome\s+(here|closer)\b|\bstep\s+forward\b|\bmove\s+closer\b",
        RegexOptions.Compiled
        | RegexOptions.IgnoreCase
        | RegexOptions.CultureInvariant);

    public static readonly Regex Backward = new Regex(
        @"(往|向|朝)后[退走挪靠]?(一?小?大?步)?|退后|后退|退一点|离远点|"
        + @"\bback\s+up\b|\bstep\s+back\b|\bmove\s+back\b|\bgo\s+backward\b",
        RegexOptions.Compiled
        | RegexOptions.IgnoreCase
        | RegexOptions.CultureInvariant);

    public static readonly Regex Left = new Regex(
        @"(往|向|朝)左[走挪靠]?(一?小?大?步)?|"
        + @"\bgo\s+left\b|\bmove\s+left\b|\bstep\s+left\b|\bto\s+the\s+left\b",
        RegexOptions.Compiled
        | RegexOptions.IgnoreCase
        | RegexOptions.CultureInvariant);

    public static readonly Regex Right = new Regex(
        @"(往|向|朝)右[走挪靠]?(一?小?大?步)?|"
        + @"\bgo\s+right\b|\bmove\s+right\b|\bstep\s+right\b|\bto\s+the\s+right\b",
        RegexOptions.Compiled
        | RegexOptions.IgnoreCase
        | RegexOptions.CultureInvariant);

    public static readonly Regex Up = new Regex(
        @"(往|向|朝)上[走挪靠]?(一?小?大?步)?|"
        + @"\bgo\s+up\b|\bmove\s+up\b|\bstep\s+up\b",
        RegexOptions.Compiled
        | RegexOptions.IgnoreCase
        | RegexOptions.CultureInvariant);

    public static readonly Regex Down = new Regex(
        @"(往|向|朝)下[走挪靠]?(一?小?大?步)?|"
        + @"\bgo\s+down\b|\bmove\s+down\b|\bstep\s+down\b",
        RegexOptions.Compiled
        | RegexOptions.IgnoreCase
        | RegexOptions.CultureInvariant);

    public static readonly Regex Follow = new Regex(
        @"^(跟着我?|跟我走?|跟上|一起走|跟我来|"
        + @"follow me|come with me|stay with me)$",
        RegexOptions.Compiled
        | RegexOptions.IgnoreCase
        | RegexOptions.CultureInvariant);

    public static readonly Regex StopFollow = new Regex(
        @"^(别跟了|不要跟了|停止跟随|取消跟随|不用跟了|别跟着我|"
        + @"回去吧|你走吧|不用跟着|别跟着|"
        + @"stop following|stop following me|don't follow|"
        + @"go back|you can go now|stay here)$",
        RegexOptions.Compiled
        | RegexOptions.IgnoreCase
        | RegexOptions.CultureInvariant);

        public static readonly Regex StayHome = new Regex(
        @"(哪里也别去|今天别出门|今天留家|待在家里|不要出门|"
        + @"stay home|don't go out|stay inside)",
        RegexOptions.Compiled
        | RegexOptions.IgnoreCase
        | RegexOptions.CultureInvariant);

    public static readonly Regex AllDayFollow = new Regex(
        @"(陪我一整天|陪着我一整天|陪我一天|陪着我一天|陪我全天|"
        + @"今天一直陪我|今天全程陪我|跟我一整天|跟着我一整天|"
        + @"accompany me all day|accompany me today|"
        + @"stay with me all day|stay with me today)",
        RegexOptions.Compiled
        | RegexOptions.IgnoreCase
        | RegexOptions.CultureInvariant);

    public static bool IsAnyAction(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return false;

        return Forward.IsMatch(text)
            || Backward.IsMatch(text)
            || Left.IsMatch(text)
            || Right.IsMatch(text)
            || Up.IsMatch(text)
            || Down.IsMatch(text)
            || StopFollow.IsMatch(text)
            || Follow.IsMatch(text)
            || StayHome.IsMatch(text)
            || AllDayFollow.IsMatch(text);
    }

    // 兼容旧代码
    public static bool IsAnyMovement(string text)
    {
        return IsAnyAction(text);
    }
}

// ─────────────────────────────────────────────────────────
// Keyword library
// ─────────────────────────────────────────────────────────

internal static class Keywords
{
    internal static readonly HashSet<string> ExactGreetingsZh =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "早",
            "早啊",
            "早安",
            "早上好",
            "中午好",
            "下午好",
            "晚上好",
            "晚安",
            "你好",
            "你好啊",
            "您好",
            "哈喽",
            "嗨"
        };

    internal static readonly HashSet<string> ExactGreetingsEn =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "hi",
            "hello",
            "good morning",
            "good evening",
            "good afternoon",
            "hey",
            "howdy"
        };

    internal static readonly HashSet<string> ExactFarewellsZh =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "再见",
            "拜拜",
            "明天见",
            "晚安"
        };

    internal static readonly HashSet<string> ExactFarewellsEn =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "bye",
            "goodbye",
            "see you",
            "good night"
        };

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

    // 这里保留相对明确的 GoTo 触发词。
    // “你去”“过去”“去一下”“帮我拿”等模糊词已移除，避免误判。
    internal static readonly string[] GotoTriggerZh =
    {
        "你可以去",
        "你能去",
        "走到",
        "移动到",
        "前往",
        "去那个",
        "去那棵",
        "去那里",
        "走过去"
    };

    internal static readonly string[] GotoTriggerEn =
    {
        "can you go to",
        "go to the",
        "walk to",
        "move to",
        "head to",
        "go over to"
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

    private static readonly char[] PunctuationTrimChars =
    {
        ' ', '\t', '\n', '\r',
        ',', '.', '!', '?', ';', ':',
        '，', '。', '！', '？', '；', '：', '、',
        '~', '～',
        '“', '”', '"',
        '‘', '’', '\'',
        '(', ')', '（', '）'
    };

    private static readonly char[] WordSeparators =
    {
        ' ', ',', '.', '!', '?', ';', ':',
        '\t', '\n', '\r',
        '，', '。', '！', '？', '；', '：', '、',
        '(', ')', '（', '）'
    };

    private static readonly Regex NegationRegex = new Regex(
        @"^(不要|别|不想|不必|不用|无需|没必要|"
        + @"don't\b|dont\b|do not\b|not\b|never\b)",
        RegexOptions.Compiled
        | RegexOptions.IgnoreCase
        | RegexOptions.CultureInvariant);

    // ─────────────────────────────────────────────────────
    // Public entry point
    // ─────────────────────────────────────────────────────

    public static ContextFlags Evaluate(
        NPC npc,
        string playerInput,
        SafetyModeLevel safetyMode,
        List<ConversationElement> chatHistory = null)
    {
        bool debugEnabled = ModEntry.Config?.Debug ?? false;

        try
        {
            return EvaluateInternal(
                npc,
                playerInput,
                safetyMode,
                chatHistory,
                debugEnabled);
        }
        catch (Exception ex)
        {
            ModEntry.SMonitor?.Log(
                "[ContextRouter] Evaluate failed: " + ex,
                LogLevel.Error);

            // 路由失败时返回安全默认值，避免阻断整个对话流程。
            return new ContextFlags
            {
                IncludeSafetyRules = safetyMode != SafetyModeLevel.Off,
                IncludeMemories = false,
                IncludeEnvironment = true,
                IncludeFarmDetails = true
            };
        }
    }

    private static ContextFlags EvaluateInternal(
        NPC npc,
        string playerInput,
        SafetyModeLevel safetyMode,
        List<ConversationElement> chatHistory,
        bool debugEnabled)
    {
        var flags = new ContextFlags();

        if (npc == null)
        {
            DebugLog(debugEnabled, "Evaluate skipped: npc is null.");
            return flags;
        }

        string originalInput = playerInput?.Trim() ?? string.Empty;
        string cleanInput = originalInput.ToLowerInvariant();
        bool hasInput = cleanInput.Length > 0;

        DebugLog(
            debugEnabled,
            $"Input npc={npc.Name}, raw=\"{originalInput}\", clean=\"{cleanInput}\"");

        // ── 🔑 构建或获取 DialogueContext（用于访问 IsActiveTurn 标志） ──
        DialogueContext context = null;
        var builder = DialogueBuilder.Instance;
        if (builder != null)
        {
            context = builder.GetContext(npc.Name);
        }

        // 如果没有现有上下文（如首次交互），创建临时上下文用于路由判定
        if (context == null)
        {
            context = new DialogueContext
            {
                ChatHistory = chatHistory ?? new List<ConversationElement>(),
                IsActiveTurn = false  // 默认为新开场
            };
        }

        // Phase 1: 日期、邀请、爽约和嫉妒状态
        EvaluateDateState(
            npc,
            cleanInput,
            hasInput,
            flags,
            debugEnabled);

        // Phase 2: 读取真实跟随状态
        EvaluateFollowState(
            npc,
            flags,
            debugEnabled);

        // Phase 3: 动作和导航意图
        EvaluateMovement(
            npc,
            originalInput,
            cleanInput,
            hasInput,
            flags,
            debugEnabled);

        bool talkedToday = HasTalkedToToday(npc);

        // Phase 4: 首次问候快速路径
        if (!ShouldSuppressSimpleGreeting(flags))
        {
            if (TryEvaluateAsSimpleGreeting(
                cleanInput,
                hasInput,
                talkedToday,
                safetyMode,
                flags))
            {
                LogFlags(npc.Name, flags, debugEnabled);
                return flags;
            }
        }

        // Phase 5: 上下文开关
        EvaluateContextSwitches(
            npc,
            cleanInput,
            hasInput,
            safetyMode,
            flags,
            talkedToday,
            chatHistory,
            context,
            debugEnabled);

        LogFlags(npc.Name, flags, debugEnabled);
        return flags;
    }

    // ─────────────────────────────────────────────────────
    // Date state
    // ─────────────────────────────────────────────────────

    private static void EvaluateDateState(
        NPC npc,
        string cleanInput,
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
        if (hasInput
            && MatchesWithBoundary(
                cleanInput,
                Keywords.InviteZh,
                Keywords.InviteEn))
        {
            flags.IsInviteRequested = true;

            DebugLog(
                debugEnabled,
                "Date intent: invitation detected.");
        }

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

        if (npc.currentLocation == null)
        {
            DebugLog(
                debugEnabled,
                "Action detection skipped: npc has no current location.");
            return;
        }

        ActionTag detectedTag = DetectActionTag(
            cleanInput,
            debugEnabled);

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

            return;
        }

        string gotoText;
        if (TryDetectGotoIntentInternal(cleanInput, out gotoText))
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

    private static ActionTag DetectActionTag(
        string cleanInput,
        bool debugEnabled)
    {
        if (string.IsNullOrWhiteSpace(cleanInput))
            return ActionTag.None;

        // 只有明显以否定词开头时才拦截。
        // 例如“不要亲我”“别往前走”不会触发动作。
        // 不对“我不想亲你，但请你往前走”做复杂复合句解析。
        if (IsNegatedCommand(cleanInput))
        {
            DebugLog(
                debugEnabled,
                $"Action ignored because input is negated: \"{cleanInput}\"");

            return ActionTag.None;
        }

        // 语义优先级：
        // StayHome / AllDayFollow
        // → StopFollow
        // → Follow
        // → 方向移动
        if (IntentRegex.StayHome.IsMatch(cleanInput))
            return ActionTag.StayHome;

        if (IntentRegex.AllDayFollow.IsMatch(cleanInput))
            return ActionTag.AllDayFollow;

        if (IntentRegex.StopFollow.IsMatch(cleanInput))
            return ActionTag.StopFollow;

        if (IntentRegex.Follow.IsMatch(cleanInput))
            return ActionTag.Follow;


        if (IntentRegex.Forward.IsMatch(cleanInput))
            return ActionTag.StepForward;

        if (IntentRegex.Backward.IsMatch(cleanInput))
            return ActionTag.StepBackward;

        if (IntentRegex.Left.IsMatch(cleanInput))
            return ActionTag.StepLeft;

        if (IntentRegex.Right.IsMatch(cleanInput))
            return ActionTag.StepRight;

        if (IntentRegex.Up.IsMatch(cleanInput))
            return ActionTag.StepUp;

        if (IntentRegex.Down.IsMatch(cleanInput))
            return ActionTag.StepDown;

        return ActionTag.None;
    }

    private static bool IsNegatedCommand(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return false;

        string text = input.Trim();
        return NegationRegex.IsMatch(text);
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

        if (!IsSimpleGreeting(cleanInput))
            return false;

        flags.IsSimpleGreeting = true;
        flags.IsFarewell = IsFarewell(cleanInput);

        flags.IncludeSafetyRules =
            safetyMode != SafetyModeLevel.Off;

        flags.IncludeShortTermContext = false;
        flags.IncludeMemories = false;
        flags.IncludeEnvironment = false;
        flags.IncludeFarmDetails = false;

        return true;
    }

    private static bool ShouldSuppressSimpleGreeting(ContextFlags flags)
    {
        return flags.HasStoodUpPending
            || flags.IsOnDate
            || flags.IsJealousy
            || flags.IsInviteRequested
            || flags.IsActionRequested
            || flags.IsFollowing;
    }

    private static bool IsSimpleGreeting(string cleanInput)
    {
        if (string.IsNullOrWhiteSpace(cleanInput))
            return false;

        string stripped = cleanInput.Trim(PunctuationTrimChars);

        if (stripped.Length == 0)
            return false;

        return Keywords.ExactGreetingsZh.Contains(stripped)
            || Keywords.ExactGreetingsEn.Contains(stripped)
            || Keywords.ExactFarewellsZh.Contains(stripped)
            || Keywords.ExactFarewellsEn.Contains(stripped);
    }

    private static bool IsFarewell(string cleanInput)
    {
        string stripped = cleanInput.Trim(PunctuationTrimChars);

        return Keywords.ExactFarewellsZh.Contains(stripped)
            || Keywords.ExactFarewellsEn.Contains(stripped);
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
        DialogueContext context,
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
                context);

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
        DialogueContext context)
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
        if (context?.IsActiveTurn == true)
        {
            return true;
        }

        // 3. Turn 0 但今天已经交谈过，保留历史延续感
        if (talkedToday && chatHistory != null && chatHistory.Any(x => x != null))
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

    internal static bool TryDetectGotoIntentInternal(
        string cleanInput,
        out string intentText)
    {
        intentText = string.Empty;

        if (string.IsNullOrWhiteSpace(cleanInput))
            return false;

        // 避免明显疑问句或叙述句触发 GoTo。
        if (cleanInput.EndsWith("吗", StringComparison.Ordinal)
            || cleanInput.EndsWith("呢", StringComparison.Ordinal)
            || cleanInput.EndsWith("？", StringComparison.Ordinal)
            || cleanInput.EndsWith("?", StringComparison.Ordinal))
        {
            return false;
        }

        if (ContainsAny(
                cleanInput,
                Keywords.GotoTriggerZh)
            || MatchesWordBoundaryAny(
                cleanInput,
                Keywords.GotoTriggerEn))
        {
            intentText = cleanInput;
            return true;
        }

        return false;
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
            + $"Farm={flags.IncludeFarmDetails}",
            LogLevel.Debug);
    }
}