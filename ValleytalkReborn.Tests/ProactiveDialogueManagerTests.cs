using System;
using System.Collections.Generic;
using ValleytalkReborn;
using Xunit;

// 与 MoodShockStoreTests / SensoryCooldownStoreTests 共享 Collection——静态容器互不影响，仅限制并行度
[Collection("EmotionStore")]
public class ProactiveDialogueManagerTests
{
    private static PerceptionEntry Entry(string key) => new() { Key = key };

    private static void ResetAll(int day = 100, int time = 600)
    {
        ProactiveDialogueManager.ClearAll();
        ProactiveDialogueManager.DayProvider = () => day;
        ProactiveDialogueManager.NowGameTimeProvider = () => time;
        ProactiveDialogueManager.PlayerMovingProvider = () => false;
        ProactiveDialogueManager.HeartsProvider = _ => 0;
        ProactiveDialogueManager.MidChanceRollProvider = () => 0.5;
        ProactiveDialogueManager.EnabledProvider = () => true;
        ProactiveDialogueManager.MidChanceProvider = () => 0.35f;
        SensoryClassifier.BucketProvider = _ => new List<PerceptionEntry>();
        SensoryCooldownStore.ClearAll();
    }

    // ── 验收 2a：闸门 0 ──

    [Fact]
    public void UT01_Gate0_Blocked_Then_Pass()
    {
        // 步骤 1：day=100, time=900 登记
        ResetAll(day: 100, time: 900);
        ProactiveDialogueManager.OnMainDialogueStarted("Abigail");

        // 步骤 2：time=1020 查询 → diff=80 ≤ 120 → 拦截
        ProactiveDialogueManager.NowGameTimeProvider = () => 1020;
        var d1 = ProactiveDialogueManager.Resolve("Abigail");
        Assert.Equal(BarkOutputMode.Soliloquy, d1.Mode);
        Assert.Equal("gate0-recent-main-dialogue", d1.DenyReason);

        // 步骤 3：time=1101 查询 → diff=121 > 120 → 放行（闸门 0 不拦）
        ProactiveDialogueManager.NowGameTimeProvider = () => 1101;
        // 无感官、不移动、hearts=0 → 闸门 3 low-hearts（但闸门 0 已放行）
        var d2 = ProactiveDialogueManager.Resolve("Abigail");
        Assert.Equal(BarkOutputMode.Soliloquy, d2.Mode);
        Assert.Equal("gate3-low-hearts", d2.DenyReason);
    }

    [Fact]
    public void UT02_Gate0_CrossNoon_Blocked()
    {
        // 1250 记录 → 1310 查询：ToMinutes diff=20 ≤ 120 → 拦截
        ResetAll(day: 100, time: 1250);
        ProactiveDialogueManager.OnMainDialogueStarted("Abigail");

        ProactiveDialogueManager.NowGameTimeProvider = () => 1310;
        var d = ProactiveDialogueManager.Resolve("Abigail");
        Assert.Equal(BarkOutputMode.Soliloquy, d.Mode);
        Assert.Equal("gate0-recent-main-dialogue", d.DenyReason);
    }

    [Fact]
    public void UT03_Gate0_CrossDay_Pass()
    {
        // day=100 记录 → day=101 查询：跨日 → 闸门 0 不拦
        ResetAll(day: 100, time: 900);
        ProactiveDialogueManager.OnMainDialogueStarted("Abigail");

        ProactiveDialogueManager.DayProvider = () => 101;
        ProactiveDialogueManager.NowGameTimeProvider = () => 905;
        var d = ProactiveDialogueManager.Resolve("Abigail");
        // 闸门 0 不拦；无感官、不移动、hearts=0 → 闸门 3 low-hearts
        Assert.Equal(BarkOutputMode.Soliloquy, d.Mode);
        Assert.Equal("gate3-low-hearts", d.DenyReason);
    }

    // ── 验收 2b：感官路径 ──

    [Fact]
    public void UT04_SensoryPath_MicroSocial_WithSensory()
    {
        ResetAll();
        SensoryClassifier.BucketProvider = _ => new List<PerceptionEntry> { Entry("PlayerSpecialOutfit_Shorts") };

        var d = ProactiveDialogueManager.Resolve("Abigail");
        Assert.Equal(BarkOutputMode.MicroSocial, d.Mode);
        Assert.True(d.SensoryTriggered);
        Assert.NotNull(d.Sensory);
        Assert.Equal(SensoryType.LewisShorts, d.Sensory.Type);
    }

    [Fact]
    public void UT05_AfterCommit_SensoryLocked_RelationPath()
    {
        ResetAll();
        ProactiveDialogueManager.HeartsProvider = _ => 8;
        SensoryClassifier.BucketProvider = _ => new List<PerceptionEntry> { Entry("PlayerSpecialOutfit_Shorts") };

        var d1 = ProactiveDialogueManager.Resolve("Abigail");
        Assert.True(d1.SensoryTriggered);

        bool committed = ProactiveDialogueManager.Commit("Abigail", d1);
        Assert.True(committed);
        Assert.True(SensoryCooldownStore.IsLocked("Abigail", SensoryType.LewisShorts));

        // 再次 Resolve：感官锁定 → 关系路径（hearts=8 ≥ 7）→ 但 cap 已触发 → daily-cap
        var d2 = ProactiveDialogueManager.Resolve("Abigail");
        Assert.Equal(BarkOutputMode.Soliloquy, d2.Mode);
        Assert.Equal("daily-cap", d2.DenyReason);
    }

    // ── 验收 2c：姿态闸门 ──

    [Fact]
    public void UT06_Gate2_PlayerMoving_Blocked()
    {
        ResetAll();
        ProactiveDialogueManager.PlayerMovingProvider = () => true;
        ProactiveDialogueManager.HeartsProvider = _ => 8;

        var d = ProactiveDialogueManager.Resolve("Abigail");
        Assert.Equal(BarkOutputMode.Soliloquy, d.Mode);
        Assert.Equal("gate2-player-moving", d.DenyReason);
    }

    // ── 验收 2d：心数阶梯 ──

    [Fact]
    public void UT07_Hearts7_AlwaysMicroSocial()
    {
        ResetAll();
        ProactiveDialogueManager.HeartsProvider = _ => 7;

        var d = ProactiveDialogueManager.Resolve("Abigail");
        Assert.Equal(BarkOutputMode.MicroSocial, d.Mode);
        Assert.False(d.SensoryTriggered);
    }

    [Fact]
    public void UT08_Hearts3_RollSuccess_MicroSocial()
    {
        ResetAll();
        ProactiveDialogueManager.HeartsProvider = _ => 3;
        ProactiveDialogueManager.MidChanceRollProvider = () => 0.10; // < 0.35

        var d = ProactiveDialogueManager.Resolve("Abigail");
        Assert.Equal(BarkOutputMode.MicroSocial, d.Mode);
        Assert.False(d.SensoryTriggered);
    }

    [Fact]
    public void UT09_Hearts3_RollFail_Blocked()
    {
        ResetAll();
        ProactiveDialogueManager.HeartsProvider = _ => 3;
        ProactiveDialogueManager.MidChanceRollProvider = () => 0.90; // ≥ 0.35

        var d = ProactiveDialogueManager.Resolve("Abigail");
        Assert.Equal(BarkOutputMode.Soliloquy, d.Mode);
        Assert.Equal("gate3-roll-fail", d.DenyReason);
    }

    [Fact]
    public void UT10_Hearts2_Blocked()
    {
        ResetAll();
        ProactiveDialogueManager.HeartsProvider = _ => 2;

        var d = ProactiveDialogueManager.Resolve("Abigail");
        Assert.Equal(BarkOutputMode.Soliloquy, d.Mode);
        Assert.Equal("gate3-low-hearts", d.DenyReason);
    }

    // ── 验收 2e：每日 cap ──

    [Fact]
    public void UT11_DailyCap_BlockedAfterFirstCommit()
    {
        ResetAll();
        ProactiveDialogueManager.HeartsProvider = _ => 8;

        var d1 = ProactiveDialogueManager.Resolve("Abigail");
        Assert.Equal(BarkOutputMode.MicroSocial, d1.Mode);
        Assert.True(ProactiveDialogueManager.Commit("Abigail", d1));

        // 再次 Resolve：hearts=8 → 关系路径 → cap → daily-cap
        var d2 = ProactiveDialogueManager.Resolve("Abigail");
        Assert.Equal(BarkOutputMode.Soliloquy, d2.Mode);
        Assert.Equal("daily-cap", d2.DenyReason);
    }

    [Fact]
    public void UT12_DailyCap_SensoryPath_AlsoBlocked()
    {
        ResetAll();
        ProactiveDialogueManager.HeartsProvider = _ => 8;
        SensoryClassifier.BucketProvider = _ => new List<PerceptionEntry> { Entry("PlayerSpecialOutfit_Shorts") };

        var d1 = ProactiveDialogueManager.Resolve("Abigail");
        Assert.True(ProactiveDialogueManager.Commit("Abigail", d1));

        // 再次 Resolve：感官未锁定（Shorts 是 Continuous，Commit 时 TryClaim 成功）→ 但 cap → daily-cap
        var d2 = ProactiveDialogueManager.Resolve("Abigail");
        Assert.Equal(BarkOutputMode.Soliloquy, d2.Mode);
        Assert.Equal("daily-cap", d2.DenyReason);
    }

    // ── 验收 2f：开关 ──

    [Fact]
    public void UT13_Disabled_Blocked_ProviderNotCalled()
    {
        ResetAll();
        ProactiveDialogueManager.EnabledProvider = () => false;

        bool bucketCalled = false;
        SensoryClassifier.BucketProvider = _ => { bucketCalled = true; return new List<PerceptionEntry> { Entry("PlayerSpecialOutfit_Shorts") }; };

        var d = ProactiveDialogueManager.Resolve("Abigail");
        Assert.Equal(BarkOutputMode.Soliloquy, d.Mode);
        Assert.Equal("disabled", d.DenyReason);
        Assert.False(bucketCalled);
    }

    // ── 验收 2g：Commit 语义 ──

    [Fact]
    public void UT14_Commit_SoliloquyDecision_ReturnsFalse()
    {
        ResetAll();
        var soliloquy = new ProactiveDialogueDecision { Mode = BarkOutputMode.Soliloquy, DenyReason = "test" };

        Assert.False(ProactiveDialogueManager.Commit("Abigail", soliloquy));
    }

    [Fact]
    public void UT15_Commit_Duplicate_ReturnsFalse()
    {
        ResetAll();
        ProactiveDialogueManager.HeartsProvider = _ => 8;

        var d1 = ProactiveDialogueManager.Resolve("Abigail");
        Assert.True(ProactiveDialogueManager.Commit("Abigail", d1));
        Assert.False(ProactiveDialogueManager.Commit("Abigail", d1));
    }

    [Fact]
    public void UT16_Commit_TryClaimFails_CapNotRegistered()
    {
        ResetAll();
        ProactiveDialogueManager.HeartsProvider = _ => 8;
        SensoryClassifier.BucketProvider = _ => new List<PerceptionEntry> { Entry("PlayerSpecialOutfit_Shorts") };

        // 1. Resolve → MicroSocial{Sensory}
        var d1 = ProactiveDialogueManager.Resolve("Abigail");
        Assert.True(d1.SensoryTriggered);

        // 2. 手工锁定同一 Type（模拟并发/异常）
        Assert.True(SensoryCooldownStore.TryClaim("Abigail", SensoryType.LewisShorts, SensoryCategory.Continuous));

        // 3. Commit → TryClaim 失败 → false，cap 未登记
        Assert.False(ProactiveDialogueManager.Commit("Abigail", d1));

        // 4. 验证 cap 未登记：再次 Resolve → 感官锁定 → 关系路径（hearts=8）→ cap 未触发 → MicroSocial
        var d2 = ProactiveDialogueManager.Resolve("Abigail");
        Assert.Equal(BarkOutputMode.MicroSocial, d2.Mode);
        Assert.False(d2.SensoryTriggered);

        // 5. Commit 成功 → 证明 cap 确实未登记
        Assert.True(ProactiveDialogueManager.Commit("Abigail", d2));
    }

    // ── 验收 2h：异常注入 ──

    [Fact]
    public void UT17_HeartsProviderThrows_ReturnsLowHearts()
    {
        ResetAll();
        ProactiveDialogueManager.HeartsProvider = _ => throw new InvalidOperationException("test");

        var d = ProactiveDialogueManager.Resolve("Abigail");
        Assert.Equal(BarkOutputMode.Soliloquy, d.Mode);
        Assert.Equal("gate3-low-hearts", d.DenyReason);
    }
}
