// DateInvitationContractTests.cs
// ═══════════════════════════════════════════════════════════════════════════
// CTX-005 — 约会邀请契约与路由边界
// ═══════════════════════════════════════════════════════════════════════════
//
// 锁定的契约（对应 src/Dialogue/Coordination/ContextRouter.cs 邀请边界）：
//
// 1. DateRules.CanScheduleDate 拒绝空地点——纯策略边界的白名单校验不得被削弱。
//    （CTX-010 起 Router 不再调用它；该 API 保留为已测试的纯策略入口，去留另议。）
// 2. ContextRouter.TryDetectDateInvitation(input) 只做「邀约意向词判定」，
//    无 out 地点参数，不做地点短语命中。
// 3. CTX-010：含邀约意向 + 世界就绪 ⇒ 置位 IsInviteRequested；地点由玩家在
//    DateLocationPickerMenu 自选，调度与校验下沉到 Picker / TryScheduleDate。
// 4. 世界未就绪 ⇒ 不置位、不抛（BOUNDARY），保持纯对白。
// 5. 约会系统关闭 ⇒ 不残留任何日期衍生标志。
//
// 环境事实：本文件沿用 TestEnvironment.InstallHeadlessContext() 无头 SMAPI
// 前置（CTX-008）。CTX-011 起该 fixture 提供 WithWorldReady(Action) 作用域注入；
// 本类因此纳入 WorldReadyStateCollection（非并行），与断言"无世界"状态的
// 用例集合互斥（TestCollections.cs 另有程序集级 DisableTestParallelization）。
//
// ═══════════════════════════════════════════════════════════════════════════

using System;
using System.Collections.Generic;
using StardewValley;
using ValleytalkReborn;
using ValleytalkReborn.Dialogue.Coordination;
using Xunit;

namespace ValleytalkReborn.Tests;

[Collection("WorldReadyStateCollection")]
public class DateInvitationContractTests
{
    static DateInvitationContractTests()
    {
        TestEnvironment.InstallHeadlessContext();
    }

    private static NPC NewTestNpc()
    {
        var npc = new NPC();
        npc.Name = "BaselineNpc";
        return npc;
    }

    private static DateWorldSnapshot ReadyWorld(int timeOfDay = 1200, bool isFestivalDay = false)
        => new(timeOfDay, "Farm", isFestivalDay, true);

    private static IDisposable UseModConfig(bool enableDateSystem)
        => new ModConfigScope(enableDateSystem);

    private sealed class ModConfigScope : IDisposable
    {
        private readonly ModConfig _original;

        public ModConfigScope(bool enableDateSystem)
        {
            _original = ModEntry.Config;
            ModEntry.Config = new ModConfig { EnableDateSystem = enableDateSystem };
        }

        public void Dispose() => ModEntry.Config = _original;
    }

    private sealed class FakeDateStateProvider : IDateStateProvider
    {
        public bool OnDate { get; set; }
        public string NpcName { get; set; } = "";

        public string ActiveDateNpcName => NpcName;
        public string ActiveDateLocation => "Saloon";

        public bool IsOnDate(string npcName) => OnDate;
        public void ConsumeDate(string npcName) { }
    }

    private sealed class FakeStoodUpProvider : IStoodUpProvider
    {
        public string PendingDate { get; set; } = "";

        public string GetPendingStoodUp(string npcName) => PendingDate;
    }

    // ── 1. 空地点必须被 DateRules 拒绝（纯策略边界）──────────────────────────

    [Fact]
    public void CanScheduleDate_EmptyLocation_IsRejected()
    {
        var whitelist = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Saloon", "Beach"
        };

        bool allowed = DateRules.CanScheduleDate(
            ReadyWorld(), "", "", "Abigail", whitelist, 1800);

        Assert.False(allowed);
    }

    [Fact]
    public void CanScheduleDate_WhitelistedLocation_IsAccepted()
    {
        // 对照用例：证明空地点被拒是白名单校验的结果，而不是该方法恒返回 false。
        var whitelist = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Saloon", "Beach"
        };

        bool allowed = DateRules.CanScheduleDate(
            ReadyWorld(), "Saloon", "", "Abigail", whitelist, 1800);

        Assert.True(allowed);
    }

    // ── 2. 邀请检测：仅意向词判定（CTX-010），地点短语不影响结果 ──────────────

    [Theory]
    [InlineData("约你")]
    [InlineData("今晚一起去吧")]
    [InlineData("meet me tonight")]
    public void TryDetectDateInvitation_InvitationLanguage_ReturnsTrue(string input)
    {
        Assert.True(ContextRouter.TryDetectDateInvitation(input));
    }

    // 对照：含地点短语与否不再是判定条件（Router 地点解析已随本票删除）。
    [Theory]
    [InlineData("let's go to the saloon tonight")]
    [InlineData("一起去星之果实酒吧")]
    [InlineData("陪我去海滩码头")]
    public void TryDetectDateInvitation_InvitationWithLocationPhrase_ReturnsTrue(string input)
    {
        Assert.True(ContextRouter.TryDetectDateInvitation(input));
    }

    [Theory]
    [InlineData("今天天气不错")]
    [InlineData("saloon")]
    public void TryDetectDateInvitation_NoInvitationLanguage_ReturnsFalse(string input)
    {
        Assert.False(ContextRouter.TryDetectDateInvitation(input));
    }

    // ── 3. 路由边界：世界未就绪 ⇒ 不置位、不抛 ───────────────────────────────
    //     CTX-010 起 Router 不做地点解析，故「无地点」与「有地点」两条输入
    //     在无世界时走向同一条 BOUNDARY 早退，均保持纯对白。
    //     CTX-011：用 WithoutWorldReady 显式钉定 false——IsWorldReady 是进程级静态，
    //     任何先行 fixture 都可能留下置位值，ambient 值不可依赖
    //     （见 TestEnvironment 注释；CTX-011.5/CTX-013 起各写入方均改为作用域注入）。

    [Fact]
    public void Evaluate_InvitationIntentWithoutLocation_WithoutWorld_DoesNotSetInviteRequested()
    {
        NPC npc = NewTestNpc();

        using (UseModConfig(enableDateSystem: true))
        {
            ContextFlags flags = null;

            TestEnvironment.WithoutWorldReady(() =>
                flags = ContextRouter.Evaluate(new ContextRouteInput(
                    npc, "约你", SafetyModeLevel.Strict, null, false)));

            Assert.False(flags.IsInviteRequested);
        }
    }

    [Fact]
    public void Evaluate_InvitationIntentWithLocation_WithoutWorld_PreservesPlainDialogue()
    {
        NPC npc = NewTestNpc();

        using (UseModConfig(enableDateSystem: true))
        {
            ContextFlags flags = null;

            TestEnvironment.WithoutWorldReady(() =>
                flags = ContextRouter.Evaluate(new ContextRouteInput(
                    npc, "let's go to the saloon tonight", SafetyModeLevel.Strict, null, false)));

            Assert.False(flags.IsInviteRequested);
        }
    }

    // ── 3b. 正例：世界就绪 + 意向 ⇒ 置位（CTX-010 核心契约，CTX-011 启用）───
    //     TestEnvironment.WithWorldReady 在作用域内置位并在 finally 还原；
    //     本类在 WorldReadyStateCollection 中非并行运行。

    [Fact]
    public void Evaluate_InvitationIntent_WithWorldReady_SetsInviteRequested()
    {
        NPC npc = NewTestNpc();

        using (UseModConfig(enableDateSystem: true))
        {
            ContextFlags flags = null;

            TestEnvironment.WithWorldReady(() =>
                flags = ContextRouter.Evaluate(new ContextRouteInput(
                    npc, "约你", SafetyModeLevel.Strict, null, false)));

            Assert.True(flags.IsInviteRequested);
        }
    }

    [Fact]
    public void Evaluate_NonInvitationInput_WithWorldReady_DoesNotSetInviteRequested()
    {
        NPC npc = NewTestNpc();

        using (UseModConfig(enableDateSystem: true))
        {
            ContextFlags flags = null;

            TestEnvironment.WithWorldReady(() =>
                flags = ContextRouter.Evaluate(new ContextRouteInput(
                    npc, "今天天气不错", SafetyModeLevel.Strict, null, false)));

            Assert.False(flags.IsInviteRequested);
        }
    }

    // ── 3c. 注入助手自身的置位/还原契约（CTX-011 acceptance 1）──────────────

    [Fact]
    public void WithWorldReady_IsTrueInsideScope_AndRestoresOriginalValue()
    {
        bool before = StardewModdingAPI.Context.IsWorldReady;
        bool inside = false;

        TestEnvironment.WithWorldReady(() => inside = StardewModdingAPI.Context.IsWorldReady);

        Assert.True(inside);
        Assert.Equal(before, StardewModdingAPI.Context.IsWorldReady);
    }

    [Fact]
    public void WithWorldReady_ActionThrows_StillRestoresOriginalValue()
    {
        bool before = StardewModdingAPI.Context.IsWorldReady;

        Assert.Throws<InvalidOperationException>(() =>
            TestEnvironment.WithWorldReady(() => throw new InvalidOperationException("probe")));

        Assert.Equal(before, StardewModdingAPI.Context.IsWorldReady);
    }

    // ── 5. 约会系统关闭 ⇒ 无残留日期标志 ─────────────────────────────────────

    [Fact]
    public void Evaluate_DateSystemDisabled_ClearsDateDerivedFlags()
    {
        NPC npc = NewTestNpc();
        var dateState = new FakeDateStateProvider { OnDate = true, NpcName = npc.Name };
        var stoodUp = new FakeStoodUpProvider { PendingDate = "Spring 1" };

        IDateStateProvider originalDateState = ContextRouter.DateStateProvider;
        IStoodUpProvider originalStoodUp = ContextRouter.StoodUpProvider;

        try
        {
            ContextRouter.DateStateProvider = dateState;
            ContextRouter.StoodUpProvider = stoodUp;

            using (UseModConfig(enableDateSystem: false))
            {
                ContextFlags flags = ContextRouter.Evaluate(new ContextRouteInput(
                    npc, "", SafetyModeLevel.Strict, null, false));

                Assert.False(flags.IsInviteRequested);
                Assert.False(flags.IsOnDate);
                Assert.False(flags.IsJealousy);
                Assert.False(flags.HasStoodUpPending);
                Assert.Equal(string.Empty, flags.StoodUpDate);
            }
        }
        finally
        {
            ContextRouter.DateStateProvider = originalDateState;
            ContextRouter.StoodUpProvider = originalStoodUp;
        }
    }

    // ── 6. 提示词协议：未验证的邀请状态不得注入 date_invitation_protocol ────────

    [Fact]
    public void BuildDateInvitationProtocol_WithoutValidatedInvite_ReturnsEmpty()
    {
        using (UseModConfig(enableDateSystem: true))
        {
            string block = Prompts.PromptsBlocks.BuildDateInvitationProtocol(new ContextFlags
            {
                IsInviteRequested = false,
                IsOnDate = false
            });

            Assert.Equal(string.Empty, block);
        }
    }
}
