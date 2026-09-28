// ContextRouterBaselineTests.cs
// ═══════════════════════════════════════════════════════════════════════════
// CONTEXT ROUTER BASELINE（票 CTX-002，REV B）
// ═══════════════════════════════════════════════════════════════════════════
//
// ContextRouter.Evaluate（src/Dialogue/Coordination/ContextRouter.cs）的基线
// 测试与契约锁定。本文件不修改任何生产代码。
//
// ── 环境事实（2026-09-28 实证；CTX-004 后已刷新）──────────────────────────
// 1. `new NPC()` 与 `Name` setter 可在无游戏环境下使用。
// 2. CTX-004 起，意图识别阶段不再读取世界状态：原先位于动作检测之前的
//    `npc.currentLocation` 守卫已移除。因此纯文本意图路径（问候/告别/否定/
//    Follow/StopFollow/方向移动/GoTo 疑问句抑制）在无游戏世界时可正常走通。
//    世界状态读取被收敛到 ContextRouter.EvaluateDirectionalMovement：
//    它以 Context.IsWorldReady / Game1.player / npc.currentLocation 为前置守卫，
//    未通过时返回 MovementEvaluation(IsEvaluated=false)，三 flags 保持默认，
//    不做任何碰撞读取。
// 3. `npc.currentLocation` 仍无法在单元测试中初始化：该属性由私有字段
//    Character.currentLocationRef（NetLocationRef）支撑，Get/Set 需要在线
//    游戏网络状态。因此世界求值本身（IsTileWalkable）只能在已装载地图下验证，
//    属本票上报的 map-test limitation。
// 4. DateManager.IsOnDate 解引用 ModEntry.Config，
//    故所有非 null-NPC 用例都要设置 ModEntry.Config（用后恢复）。
// 5. Context.IsWorldReady 的读取会触发 SMAPI Context 的静态构造器，它依赖
//    SMAPI.Toolkit.dll（测试 bin 未复制）。若不在其它测试类注册 AssemblyResolve
//    之前触发，构造失败会污染整个进程。此处沿用 TownIncidentContractTests 的
//    InstallHeadless 模式，在类静态构造中先行注册（见类静态构造函数）。
//
// ── 覆盖策略 ────────────────────────────────────────────────────────────
// 可观察并断言：null-NPC 早退、空输入、全部纯文本意图路径（含 greet/否定/
// Follow/StopFollow/方向移动/GoTo 疑问句抑制），以及"无世界 ⇒ 不求值"
// 的三个 flags 默认值契约。
// CTX-005 起，邀请边界（"约你"）不再浮出异常：守卫先于任何世界快照求值，
// 锚点 B 已按新契约重钉。CTX-010 起该守卫收窄为「意向命中 → 世界就绪」两段
// （地点解析与可调度性预检已从 Router 移除）。
// 日期邀请的完整契约见 DateInvitationContractTests。
//
// ═══════════════════════════════════════════════════════════════════════════

using System;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using StardewValley;
using ValleytalkReborn;
using ValleytalkReborn.Dialogue.Coordination;
using Xunit;

namespace ValleytalkReborn.Tests;

public class ContextRouterBaselineTests
{
    // ── 无游戏进程环境下驱动 SMAPI Context 的前置 ────────────────────────────
    // 必须在任何用例触达 Context.IsWorldReady 之前注册（静态构造保证早于
    // 本类的全部用例执行），否则 Context 静态构造失败会污染同进程其它测试。
    // 模式与 TownIncidentContractTests.InstallHeadlessMultiplayerContext 一致。

    static ContextRouterBaselineTests()
    {
        AppDomain.CurrentDomain.AssemblyResolve += (sender, args) =>
        {
            string assemblyName = new AssemblyName(args.Name).Name;
            string smapiInternal = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..",
                "Stardew Valley", "smapi-internal", assemblyName + ".dll");
            return File.Exists(smapiInternal) ? Assembly.LoadFrom(smapiInternal) : null;
        };

        Game1.hasLocalClientsOnly = false;
        var runner = FormatterServices.GetUninitializedObject(typeof(GameRunner));
        var instancesField = typeof(GameRunner).GetField("gameInstances",
            BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
        instancesField?.SetValue(runner, Activator.CreateInstance(instancesField.FieldType));
        typeof(GameRunner).GetField("instance",
            BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)
            ?.SetValue(null, runner);
    }

    // CTX-004：三 flags 只能由 MovementEvaluation 赋值；未求值（IsEvaluated=false）
    // 时必须保持默认，调用方不得把它们当成"路径畅通"。
    private static void AssertMovementFlagsDefault(ContextFlags flags)
    {
        Assert.False(flags.IsPathBlocked);
        Assert.Equal(BlockDirection.None, flags.BlockDirection);
        Assert.False(flags.IsAlreadyAdjacent);
    }

    private static NPC NewTestNpc()
    {
        // new NPC() + Name 可在无游戏环境下使用；currentLocation 刻意不设置
        // （无法在无游戏世界时初始化——见文件头事实 2）。
        var npc = new NPC();
        npc.Name = "BaselineNpc";
        return npc;
    }

    private static IDisposable UseModConfig()
    {
        return new ModConfigScope();
    }

    private sealed class ModConfigScope : IDisposable
    {
        private readonly ModConfig _original;

        public ModConfigScope()
        {
            _original = ModEntry.Config;
            ModEntry.Config = new ModConfig();
        }

        public void Dispose() => ModEntry.Config = _original;
    }

    // ── 1. null-NPC：ContextRouter.cs:772-776 早退，返回全默认 ContextFlags ──
    //    该路径不触达 Game1/MovementManager/DateStateProvider，无需任何 setup。
    //    注意 IncludeSafetyRules=true 是 ContextFlags 字段默认值（L285），
    //    与 safetyMode 无关。

    [Fact]
    public void Evaluate_NullNpc_ReturnsDefaultContextFlags()
    {
        ContextFlags flags = ContextRouter.Evaluate(new ContextRouteInput(
            null, "hi", SafetyModeLevel.Strict, null, false));

        Assert.False(flags.IsSimpleGreeting);
        Assert.False(flags.IsFarewell);
        Assert.False(flags.IsActionRequested);
        Assert.Equal(ActionTag.None, flags.RequestedAction);
        Assert.False(flags.IsMovementRequested);
        Assert.False(flags.IsPathBlocked);
        Assert.Equal(BlockDirection.None, flags.BlockDirection);
        Assert.False(flags.IsAlreadyAdjacent);
        Assert.False(flags.IsFollowing);
        Assert.False(flags.IsGotoRequested);
        Assert.Equal(string.Empty, flags.GotoIntentText);
        Assert.False(flags.IsOnDate);
        Assert.Equal(CompanionFocusMode.None, flags.CompanionFocus);
        Assert.Equal(string.Empty, flags.DateLocationId);
        Assert.False(flags.IsInviteRequested);
        Assert.False(flags.HasStoodUpPending);
        Assert.Equal(string.Empty, flags.StoodUpDate);
        Assert.False(flags.IsJealousy);
        Assert.True(flags.IncludeSafetyRules);
        Assert.False(flags.IncludeShortTermContext);
        Assert.False(flags.IncludeMemories);
        Assert.True(flags.IncludeEnvironment);
        Assert.True(flags.IncludeFarmDetails);
    }

    // ── 2. 空输入：ContextRouter.cs:1006-1010 在位置守卫之前早退 ──
    //    IsActionRequested 保持 false（L1006-1010）。

    [Fact]
    public void Evaluate_EmptyInput_ReturnsNoActionRequested()
    {
        NPC npc = NewTestNpc();
        using (UseModConfig())
        {
            ContextFlags flags = ContextRouter.Evaluate(new ContextRouteInput(
                npc, "", SafetyModeLevel.Strict, null, false));

            Assert.False(flags.IsActionRequested);
            Assert.Equal(ActionTag.None, flags.RequestedAction);
            Assert.False(flags.IsMovementRequested);
            Assert.False(flags.IsGotoRequested);
            Assert.False(flags.IsSimpleGreeting);
            Assert.False(flags.IncludeMemories);
            Assert.True(flags.IncludeEnvironment);
            Assert.True(flags.IncludeFarmDetails);
            Assert.True(flags.IncludeSafetyRules);
        }
    }

    [Fact]
    public void Evaluate_WhitespaceInput_TreatedAsEmpty()
    {
        // L778-780：Trim 后 hasInput=false，走与空输入相同的早退路径。
        NPC npc = NewTestNpc();
        using (UseModConfig())
        {
            ContextFlags flags = ContextRouter.Evaluate(new ContextRouteInput(
                npc, "   ", SafetyModeLevel.Strict, null, false));

            Assert.False(flags.IsActionRequested);
            Assert.False(flags.IsSimpleGreeting);
        }
    }

    // ── 3. 问候（CTX-004 后纯文本路径不再触达世界状态，可直接执行）────────────────────────────────
    //    命中 ExactGreetingsEn（"hi"）→ IsSimpleGreeting=true（L1151），
    //    并折叠上下文开关：Memories/Environment/FarmDetails/ShortTerm 均关闭
    //    （L1154-1162）。

    [Fact]
    public void Evaluate_GreetingInput_SetsSimpleGreeting()
    {
        NPC npc = NewTestNpc();
        using (UseModConfig())
        {
            ContextFlags flags = ContextRouter.Evaluate(new ContextRouteInput(
                npc, "hi", SafetyModeLevel.Strict, null, false));

            Assert.True(flags.IsSimpleGreeting);
            Assert.False(flags.IsFarewell);
            Assert.False(flags.IncludeMemories);
            Assert.False(flags.IncludeEnvironment);
            Assert.False(flags.IncludeFarmDetails);
            Assert.False(flags.IncludeShortTermContext);
            Assert.True(flags.IncludeSafetyRules);
        }
    }

    // ── 4. 告别（CTX-004 后纯文本路径不再触达世界状态，可直接执行）────────────────────────────────
    //    "bye" 同时命中 ExactGreetings 与 ExactFarewells（L1196-1199），
    //    IsFarewell 由 L1152 一并置位。

    [Fact]
    public void Evaluate_FarewellInput_SetsFarewell()
    {
        NPC npc = NewTestNpc();
        using (UseModConfig())
        {
            ContextFlags flags = ContextRouter.Evaluate(new ContextRouteInput(
                npc, "bye", SafetyModeLevel.Strict, null, false));

            Assert.True(flags.IsFarewell);
            Assert.True(flags.IsSimpleGreeting);
        }
    }

    // ── 5. 否定（CTX-004 后纯文本路径不再触达世界状态，可直接执行）────────────────────────────────
    //    "不要亲我" 以否定词开头 → 否定守卫拦截（L1089-1099），
    //    不触发 Follow 或方向移动（设计说明 L68-69："don't kiss me"）。

    [Fact]
    public void Evaluate_NegatedCommand_SuppressesAction()
    {
        NPC npc = NewTestNpc();
        using (UseModConfig())
        {
            ContextFlags flags = ContextRouter.Evaluate(new ContextRouteInput(
                npc, "不要亲我", SafetyModeLevel.Strict, null, false));

            Assert.False(flags.IsActionRequested);
            Assert.Equal(ActionTag.None, flags.RequestedAction);
            Assert.False(flags.IsMovementRequested);
        }
    }

    // ── 6. Follow（CTX-004 后纯文本路径不再触达世界状态，可直接执行）──────────────────────────────
    //    "跟着我" 命中 IntentRegex.Follow（L1101-1102）→ ActionTag.Follow；
    //    Follow 是普通动作而非方向移动，IsMovementRequested 保持 false
    //    （L1029-1031，设计原则 2）。

    [Fact]
    public void Evaluate_FollowInput_RequestsFollow()
    {
        NPC npc = NewTestNpc();
        using (UseModConfig())
        {
            ContextFlags flags = ContextRouter.Evaluate(new ContextRouteInput(
                npc, "跟着我", SafetyModeLevel.Strict, null, false));

            Assert.True(flags.IsActionRequested);
            Assert.Equal(ActionTag.Follow, flags.RequestedAction);
            Assert.False(flags.IsMovementRequested);

            // CTX-004：Follow 不是方向移动，不进入世界求值 ⇒ 无阻挡/邻接标志。
            AssertMovementFlagsDefault(flags);
        }
    }

    // ── 7. StopFollow（CTX-004 后纯文本路径不再触达世界状态，可直接执行）──────────────────────────
    //    "别跟着我" 命中 IntentRegex.StopFollow（L1086-1087，优先级高于
    //    否定守卫）→ ActionTag.StopFollow；同样不是方向移动。

    [Fact]
    public void Evaluate_StopFollowInput_RequestsStopFollow()
    {
        NPC npc = NewTestNpc();
        using (UseModConfig())
        {
            ContextFlags flags = ContextRouter.Evaluate(new ContextRouteInput(
                npc, "别跟着我", SafetyModeLevel.Strict, null, false));

            Assert.True(flags.IsActionRequested);
            Assert.Equal(ActionTag.StopFollow, flags.RequestedAction);
            Assert.False(flags.IsMovementRequested);

            // CTX-004：StopFollow 不是方向移动，不进入世界求值。
            AssertMovementFlagsDefault(flags);
        }
    }

    // ── 8. 方向移动（CTX-004 后纯文本路径不再触达世界状态，可直接执行）────────────────────────────
    //    "向前走" 命中 IntentRegex.Forward（L1104-1105）→ ActionTag.StepForward，
    //    且 IsMovementRequested=true（L1030-1031）。

    [Fact]
    public void Evaluate_DirectionalInput_RequestsStepForward()
    {
        NPC npc = NewTestNpc();
        using (UseModConfig())
        {
            ContextFlags flags = ContextRouter.Evaluate(new ContextRouteInput(
                npc, "向前走", SafetyModeLevel.Strict, null, false));

            Assert.True(flags.IsActionRequested);
            Assert.Equal(ActionTag.StepForward, flags.RequestedAction);
            Assert.True(flags.IsMovementRequested);

            // ★ CTX-004 重钉（探针实证）：无世界时求值边界返回 IsEvaluated=false，
            //   三 flags 保持默认。这与"路径畅通"不同：前者是未求值，后者必须来自求值结果。
            AssertMovementFlagsDefault(flags);
        }
    }

    // ── 9. GoTo 疑问句抑制（CTX-004 后纯文本路径不再触达世界状态，可直接执行）─────────────────────
    //    "你能去" 虽命中 GotoTriggerZh（L604-615），但句尾"？"触发
    //    疑问句抑制（TryDetectGotoIntentInternal L1433-1439），
    //    IsGotoRequested/IsActionRequested 均保持 false。

    [Fact]
    public void Evaluate_GotoQuestionInput_SuppressesGoto()
    {
        NPC npc = NewTestNpc();
        using (UseModConfig())
        {
            ContextFlags flags = ContextRouter.Evaluate(new ContextRouteInput(
                npc, "你能去那里吗？", SafetyModeLevel.Strict, null, false));

            Assert.False(flags.IsGotoRequested);
            Assert.False(flags.IsActionRequested);
            Assert.Equal(string.Empty, flags.GotoIntentText);

            // CTX-004：GoTo 目标解析在 out_of_scope，不走单步求值 ⇒ 无阻挡/邻接标志。
            AssertMovementFlagsDefault(flags);
        }
    }

    // ── 回归锚点 A（CTX-004 重钉）────────────────────────────────────────
    //     CTX-002 契约：非空输入 + 无世界 ⇒ 位置守卫抛异常并上抛，
    //     "BUG 不再被转为静默默认路由"。
    //     CTX-004 实证（探针 2026-09-28）：位置守卫已从意图阶段移除，
    //     "hi" 在无头环境**不再抛异常**，直接走通问候快路 ⇒ IsSimpleGreeting=true。
    //     锁定契约随之更新为 = "纯文本意图不依赖世界状态即可路由"。
    //     注意：这里断言的是意图结果，不钉死任何异常类型/行号。

    [Fact]
    public void Evaluate_NonEmptyInput_WithoutWorld_ReachesIntentRouting()
    {
        NPC npc = NewTestNpc();
        using (UseModConfig())
        {
            ContextFlags flags = ContextRouter.Evaluate(new ContextRouteInput(
                npc, "hi", SafetyModeLevel.Strict, null, false));

            Assert.True(flags.IsSimpleGreeting);
            Assert.False(flags.IsActionRequested);
            AssertMovementFlagsDefault(flags);
        }
    }

    [Fact]
    public void Evaluate_NonEmptyInput_WithSafetyOff_IntentRoutingDisablesSafetyRules()
    {
        NPC npc = NewTestNpc();
        using (UseModConfig())
        {
            ContextFlags flags = ContextRouter.Evaluate(new ContextRouteInput(
                npc, "hi", SafetyModeLevel.Off, null, false));

            Assert.True(flags.IsSimpleGreeting);
            Assert.False(flags.IncludeSafetyRules);
        }
    }

    // ── 回归锚点 B（CTX-005 重钉）────────────────────────────────────────
    //     CTX-004 实证（探针 2026-09-28）：移除位置守卫后，"约你" 仍在
    //     EvaluateDateState 的邀请快照处上抛
    //       System.NullReferenceException
    //       at Game1.get_temporaryContent() ← Utility.isFestivalDay(day, season)
    //       ← ContextRouter.EvaluateDateState（CTX-004 后 ContextRouter.cs:724）
    //     属日期域 BOUNDARY：`Utility.isFestivalDay` 需要已初始化的临时内容管理器。
    //     CTX-005 裁定：邀请边界必须先做「检测 → 显式地点解析 → 世界就绪」守卫；
    //     无显式受支持地点或世界未就绪时不得构建世界快照、不得求值、不得调度。
    //     CTX-010 收窄：Router 只做意向判定（地点解析与 CanScheduleDate 预检已移除），
    //     守卫简化为「意向命中 → 世界就绪」两段；世界未就绪仍不置位、不抛。
    //     故本锚点保持：无世界时 "约你" **不抛异常**、不设 IsInviteRequested、纯对白。
    //     完整契约（意向即置位 / 系统关闭清标志）见 DateInvitationContractTests。

    [Fact]
    public void Evaluate_DateInviteInput_WithoutWorld_PreservesPlainDialogue()
    {
        NPC npc = NewTestNpc();
        using (UseModConfig())
        {
            ContextFlags flags = null;
            var ex = Record.Exception(() =>
                flags = ContextRouter.Evaluate(new ContextRouteInput(
                    npc, "约你", SafetyModeLevel.Strict, null, false)));

            Assert.Null(ex);
            Assert.False(flags.IsInviteRequested);
        }
    }
}
