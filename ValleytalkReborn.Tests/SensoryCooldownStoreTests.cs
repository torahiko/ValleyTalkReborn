using ValleytalkReborn;
using Xunit;

// 与 MoodShockStoreTests 共享 Collection——两个静态容器互不影响，仅限制并行度
[Collection("EmotionStore")]
public class SensoryCooldownStoreTests
{
    private static void ResetStore(int startClock)
    {
        SensoryCooldownStore.ClearAll();
        SensoryCooldownStore.NowProvider = () => startClock;
    }

    // ── 验收 3a：TryClaim(Continuous) true → IsLocked true → 再次 TryClaim false ──

    [Fact]
    public void UT01_Continuous_LockAndReclaim()
    {
        ResetStore(1000);

        Assert.True(SensoryCooldownStore.TryClaim("Abigail", SensoryType.LewisShorts, SensoryCategory.Continuous));
        Assert.True(SensoryCooldownStore.IsLocked("Abigail", SensoryType.LewisShorts));
        Assert.False(SensoryCooldownStore.TryClaim("Abigail", SensoryType.LewisShorts, SensoryCategory.Continuous));
    }

    // ── 验收 3b：TryClaim(Transient) → NowProvider 前拨 119 分钟 IsLocked=true、121 分钟 IsLocked=false ──

    [Fact]
    public void UT02_Transient_ExpiryAfter120Minutes()
    {
        int clock = 1000;
        SensoryCooldownStore.ClearAll();
        SensoryCooldownStore.NowProvider = () => clock;

        Assert.True(SensoryCooldownStore.TryClaim("Abigail", SensoryType.GarlicStench, SensoryCategory.Transient));

        clock = 1000 + 119; // 119 分钟后
        Assert.True(SensoryCooldownStore.IsLocked("Abigail", SensoryType.GarlicStench));

        clock = 1000 + 121; // 121 分钟后
        Assert.False(SensoryCooldownStore.IsLocked("Abigail", SensoryType.GarlicStench));
    }

    // ── 验收 3c：NPC A 上锁不影响 NPC B 同 Type（按 npc 隔离）──

    [Fact]
    public void UT03_IsolatedByNpc()
    {
        ResetStore(1000);

        Assert.True(SensoryCooldownStore.TryClaim("Abigail", SensoryType.LewisShorts, SensoryCategory.Continuous));
        Assert.True(SensoryCooldownStore.IsLocked("Abigail", SensoryType.LewisShorts));
        Assert.False(SensoryCooldownStore.IsLocked("Sebastian", SensoryType.LewisShorts));
    }

    // ── 验收 3d：ResetDaily 清 Continuous 但未过期 Transient 保留；ClearAll 双容器全清 ──

    [Fact]
    public void UT04_ResetDaily_ClearsContinuous_KeepsUnexpiredTransient()
    {
        int clock = 1000;
        SensoryCooldownStore.ClearAll();
        SensoryCooldownStore.NowProvider = () => clock;

        Assert.True(SensoryCooldownStore.TryClaim("Abigail", SensoryType.LewisShorts, SensoryCategory.Continuous));
        Assert.True(SensoryCooldownStore.TryClaim("Abigail", SensoryType.GarlicStench, SensoryCategory.Transient));

        clock = 1000 + 60; // 60 分钟后，Transient 未过期
        SensoryCooldownStore.ResetDaily();

        Assert.False(SensoryCooldownStore.IsLocked("Abigail", SensoryType.LewisShorts));
        Assert.True(SensoryCooldownStore.IsLocked("Abigail", SensoryType.GarlicStench));
    }

    [Fact]
    public void UT05_ClearAll_ClearsBothContainers()
    {
        ResetStore(1000);

        Assert.True(SensoryCooldownStore.TryClaim("Abigail", SensoryType.LewisShorts, SensoryCategory.Continuous));
        Assert.True(SensoryCooldownStore.TryClaim("Abigail", SensoryType.GarlicStench, SensoryCategory.Transient));

        SensoryCooldownStore.ClearAll();

        Assert.False(SensoryCooldownStore.IsLocked("Abigail", SensoryType.LewisShorts));
        Assert.False(SensoryCooldownStore.IsLocked("Abigail", SensoryType.GarlicStench));
    }

    // ── 补充：空 npcName → TryClaim false ──

    [Fact]
    public void UT06_EmptyNpcName_TryClaimReturnsFalse()
    {
        ResetStore(1000);

        Assert.False(SensoryCooldownStore.TryClaim("", SensoryType.LewisShorts, SensoryCategory.Continuous));
    }

    // ── 补充：Transient 过期条目在 IsLocked 中被移除 ──

    [Fact]
    public void UT07_TransientExpiredEntry_RemovedOnIsLocked()
    {
        int clock = 1000;
        SensoryCooldownStore.ClearAll();
        SensoryCooldownStore.NowProvider = () => clock;

        Assert.True(SensoryCooldownStore.TryClaim("Abigail", SensoryType.GarlicStench, SensoryCategory.Transient));

        clock = 1000 + 200; // 超过 120 分钟
        Assert.False(SensoryCooldownStore.IsLocked("Abigail", SensoryType.GarlicStench));

        // 过期后应可重新 claim
        Assert.True(SensoryCooldownStore.TryClaim("Abigail", SensoryType.GarlicStench, SensoryCategory.Transient));
    }
}
