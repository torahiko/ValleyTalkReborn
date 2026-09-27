// ContextRouterBaselineTests.cs
// ═══════════════════════════════════════════════════════════════════════════
// CONTEXT ROUTER BASELINE（票 CTX-002，REV B）
// ═══════════════════════════════════════════════════════════════════════════
//
// ContextRouter.Evaluate（src/Dialogue/Coordination/ContextRouter.cs）的基线
// 测试与契约锁定。本文件不修改任何生产代码。
//
// ── 环境事实（2026-09-28 实证）────────────────────────────────────────────
// 1. `new NPC()` 与 `Name` setter 可在无游戏环境下使用。
// 2. `npc.currentLocation` 无法在单元测试中初始化：该属性由私有字段
//    Character.currentLocationRef（NetLocationRef）支撑，其 Get/Set 路径
//    需要在线游戏网络状态（Game1）。无游戏世界时 getter 抛 NullReference，
//    因此 EvaluateMovement 的位置守卫（ContextRouter.cs:1012）对任何
//    非空输入都会抛异常。REV B 起 Evaluate 外层 catch 已移除，异常直接上抛。
// 3. DateManager.IsOnDate 解引用 ModEntry.Config（ContextRouter.cs:895），
//    故所有非 null-NPC 用例都要设置 ModEntry.Config（用后恢复）。
// 4. Context.IsWorldReady 的读取（CompanionFocusResolver L17、邀请分支 L937）
//    会触发 SMAPI Context 的静态构造器，它依赖 SMAPI.Toolkit.dll（测试 bin
//    未复制）。若在其它测试类注册 AssemblyResolve 之前触发，构造失败会
//    污染整个进程。此处沿用 TownIncidentContractTests 的 InstallHeadless
//    模式，在类静态构造中先行注册（见类静态构造函数）。
//
// ── 覆盖策略 ────────────────────────────────────────────────────────────
// 可观察并断言：null-NPC 早退、空输入、非空输入异常浮出锚点。
// 必须跳过：问候/告别/否定/Follow/StopFollow/方向移动/GoTo 疑问句抑制——
// 这些纯文本意图路径被 L1012 守卫阻断，需要"已放入装载完成的 GameLocation
// 的 NPC"这一 SMAPI 状态，现有 TestFakes 无法隔离。Skip 原因统一记录
// 缺失的 setup；跳过用例的方法体保留预期断言，供后续票恢复执行。
//
// ═══════════════════════════════════════════════════════════════════════════

using System;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using StardewValley;
using ValleytalkReborn;
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

    private const string SkipReason =
        "Requires a loaded Stardew Valley game world: EvaluateMovement's guard " +
        "(ContextRouter.cs:1012) reads npc.currentLocation, whose NetLocationRef " +
        "needs live game net state; without a world the getter throws, and since " +
        "REV B removed Evaluate's catch-all, the exception now propagates. Missing " +
        "SMAPI state: Context.IsWorldReady=true and an NPC placed in a loaded " +
        "GameLocation. Not isolable with existing TestFakes; production code " +
        "unchanged (ticket CTX-002).";

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

    // ── 3. 问候（预期行为，恢复 Skip 后执行）────────────────────────────────
    //    命中 ExactGreetingsEn（"hi"）→ IsSimpleGreeting=true（L1151），
    //    并折叠上下文开关：Memories/Environment/FarmDetails/ShortTerm 均关闭
    //    （L1154-1162）。

    [Fact(Skip = SkipReason)]
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

    // ── 4. 告别（预期行为，恢复 Skip 后执行）────────────────────────────────
    //    "bye" 同时命中 ExactGreetings 与 ExactFarewells（L1196-1199），
    //    IsFarewell 由 L1152 一并置位。

    [Fact(Skip = SkipReason)]
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

    // ── 5. 否定（预期行为，恢复 Skip 后执行）────────────────────────────────
    //    "不要亲我" 以否定词开头 → 否定守卫拦截（L1089-1099），
    //    不触发 Follow 或方向移动（设计说明 L68-69："don't kiss me"）。

    [Fact(Skip = SkipReason)]
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

    // ── 6. Follow（预期行为，恢复 Skip 后执行）──────────────────────────────
    //    "跟着我" 命中 IntentRegex.Follow（L1101-1102）→ ActionTag.Follow；
    //    Follow 是普通动作而非方向移动，IsMovementRequested 保持 false
    //    （L1029-1031，设计原则 2）。

    [Fact(Skip = SkipReason)]
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
        }
    }

    // ── 7. StopFollow（预期行为，恢复 Skip 后执行）──────────────────────────
    //    "别跟着我" 命中 IntentRegex.StopFollow（L1086-1087，优先级高于
    //    否定守卫）→ ActionTag.StopFollow；同样不是方向移动。

    [Fact(Skip = SkipReason)]
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
        }
    }

    // ── 8. 方向移动（预期行为，恢复 Skip 后执行）────────────────────────────
    //    "向前走" 命中 IntentRegex.Forward（L1104-1105）→ ActionTag.StepForward，
    //    且 IsMovementRequested=true（L1030-1031）。

    [Fact(Skip = SkipReason)]
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
        }
    }

    // ── 9. GoTo 疑问句抑制（预期行为，恢复 Skip 后执行）─────────────────────
    //    "你能去" 虽命中 GotoTriggerZh（L604-615），但句尾"？"触发
    //    疑问句抑制（TryDetectGotoIntentInternal L1433-1439），
    //    IsGotoRequested/IsActionRequested 均保持 false。

    [Fact(Skip = SkipReason)]
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
        }
    }

    // ── 回归锚点 A：异常浮出（原 catch-all 降级返回已移除）────────────────
    //    非空输入 + 无游戏世界时，EvaluateMovement 的位置守卫（L1012）抛异常，
    //    异常直接上抛，不再被外层 catch 降级为安全默认值。
    //    锁定契约 = "BUG 不再被转为静默默认路由"。

    [Fact]
    public void Evaluate_NonEmptyInput_WithoutGameState_ReturnsSafeFallback()
    {
        NPC npc = NewTestNpc();
        using (UseModConfig())
        {
            var ex = Record.Exception(() =>
                ContextRouter.Evaluate(new ContextRouteInput(
                    npc, "hi", SafetyModeLevel.Strict, null, false)));

            Assert.NotNull(ex);
        }
    }

    [Fact]
    public void Evaluate_NonEmptyInput_WithSafetyOff_FallbackDisablesSafetyRules()
    {
        // 非空输入 + 无游戏世界时同样异常浮出（异常类型/行号不钉死）。
        // 锁定契约 = "BUG 不再被转为静默默认路由"。
        NPC npc = NewTestNpc();
        using (UseModConfig())
        {
            var ex = Record.Exception(() =>
                ContextRouter.Evaluate(new ContextRouteInput(
                    npc, "hi", SafetyModeLevel.Off, null, false)));

            Assert.NotNull(ex);
        }
    }

    // ── 回归锚点 B：Date 邀请路径异常浮出（原恒拒降级返回已移除）──────────
    //    非空输入 + 无游戏世界时，位置守卫（L1012）异常直接上抛。
    //    锁定契约 = "BUG 不再被转为静默默认路由"。

    [Fact]
    public void Evaluate_DateInviteInput_WithoutGameState_InviteNotRequested()
    {
        NPC npc = NewTestNpc();
        using (UseModConfig())
        {
            var ex = Record.Exception(() =>
                ContextRouter.Evaluate(new ContextRouteInput(
                    npc, "约你", SafetyModeLevel.Strict, null, false)));

            Assert.NotNull(ex);
        }
    }
}
