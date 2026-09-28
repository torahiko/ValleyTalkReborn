// DateInvitationContractTests.cs
// ═══════════════════════════════════════════════════════════════════════════
// CTX-005 — 约会邀请契约与路由边界
// ═══════════════════════════════════════════════════════════════════════════
//
// 锁定的契约（对应 src/Dialogue/Coordination/ContextRouter.cs 邀请边界）：
//
// 1. DateRules.CanScheduleDate 拒绝空地点——纯策略边界的白名单校验不得被削弱。
// 2. 邀请检测与可调度性校验是两个独立决策：
//    ContextRouter.TryDetectDateInvitation 只做「邀请语言检测 + 显式地点解析」，
//    绝不把空地点当作有效地点交给 DateRules.CanScheduleDate。
// 3. 无显式受支持地点 ⇒ 不产生已验证的邀请标志（IsInviteRequested 保持 false），
//    并保持纯对白（<date_invitation_protocol> 不注入）。
// 4. 世界未就绪 ⇒ 不求值、不调度邀请（不抛异常）。
// 5. 约会系统关闭 ⇒ 不残留任何日期衍生标志。
//
// 环境事实：本文件沿用 TestEnvironment.InstallHeadlessContext() 无头 SMAPI
// 前置（CTX-008），并复用 DateLocationRegistry 自带的默认地点数据
// （Saloon/Beach/Forest/Mountain/Town），不新增任何地点或别名。
//
// ═══════════════════════════════════════════════════════════════════════════

using System;
using System.Collections.Generic;
using StardewValley;
using ValleytalkReborn;
using ValleytalkReborn.Dialogue.Coordination;
using Xunit;

namespace ValleytalkReborn.Tests;

public class DateInvitationContractTests
{
    static DateInvitationContractTests()
    {
        TestEnvironment.InstallHeadlessContext();

        // 注册表自带默认地点（Initialize 只做 FallbackToDefault，不读游戏内容）。
        DateLocationRegistry.Initialize(null);
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

    // ── 2. 邀请检测：语言检测与地点解析分离，无显式地点时输出空串 ──────────────

    [Theory]
    [InlineData("约你")]
    [InlineData("今晚一起去吧")]
    [InlineData("meet me tonight")]
    public void TryDetectDateInvitation_InvitationWithoutLocation_ReturnsEmptyLocation(string input)
    {
        bool detected = ContextRouter.TryDetectDateInvitation(input, out string requestedLocationId);

        Assert.True(detected);
        Assert.Equal(string.Empty, requestedLocationId);
    }

    [Theory]
    [InlineData("let's go to the saloon tonight", "Saloon")]
    [InlineData("一起去星之果实酒吧", "Saloon")]
    [InlineData("陪我去海滩码头", "Beach")]
    [InlineData("let's go to the woods", "Forest")]
    public void TryDetectDateInvitation_InvitationWithExplicitLocation_ReturnsLocationId(
        string input, string expectedLocationId)
    {
        bool detected = ContextRouter.TryDetectDateInvitation(input, out string requestedLocationId);

        Assert.True(detected);
        Assert.Equal(expectedLocationId, requestedLocationId);
    }

    [Theory]
    [InlineData("今天天气不错")]
    [InlineData("saloon")]
    public void TryDetectDateInvitation_NoInvitationLanguage_ReturnsFalse(string input)
    {
        bool detected = ContextRouter.TryDetectDateInvitation(input, out string requestedLocationId);

        Assert.False(detected);
        Assert.Equal(string.Empty, requestedLocationId);
    }

    // ── 3. 路由边界：无显式地点 ⇒ 不设已验证邀请标志（纯对白）────────────────

    [Fact]
    public void Evaluate_InvitationWithoutLocation_DoesNotSetInviteRequested()
    {
        NPC npc = NewTestNpc();

        using (UseModConfig(enableDateSystem: true))
        {
            ContextFlags flags = ContextRouter.Evaluate(new ContextRouteInput(
                npc, "约你", SafetyModeLevel.Strict, null, false));

            Assert.False(flags.IsInviteRequested);
        }
    }

    // ── 4. 路由边界：世界未就绪 ⇒ 不求值、不调度（不抛异常）───────────────────

    [Fact]
    public void Evaluate_InvitationWithExplicitLocation_WithoutWorld_PreservesPlainDialogue()
    {
        NPC npc = NewTestNpc();

        using (UseModConfig(enableDateSystem: true))
        {
            ContextFlags flags = ContextRouter.Evaluate(new ContextRouteInput(
                npc, "let's go to the saloon tonight", SafetyModeLevel.Strict, null, false));

            Assert.False(flags.IsInviteRequested);
        }
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
