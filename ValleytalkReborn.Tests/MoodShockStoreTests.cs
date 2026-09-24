using StardewValley;
using ValleytalkReborn;
using Xunit;

// 直接引用主工程 internal 成员（InternalsVisibleTo 已配置）。
// MoodShockStore 纯静态容器 + NowProvider 虚拟时钟注入，全路径可在无游戏实例下覆盖。
public class MoodShockStoreTests
{
    private static void ResetStore(int startClock)
    {
        MoodShockStore.ClearAll();
        MoodShockStore.NowProvider = () => startClock;
    }

    // UT-01 时间轴连续性：日内严格递增；跨日时钟由外部（游戏分钟轴）保证。
    [Fact]
    public void UT01_TimeAxis_IncrementsWithinDay()
    {
        int clock = 1000;
        MoodShockStore.NowProvider = () => clock;
        MoodShockStore.ClearAll();

        MoodShockStore.AddShock("Abigail", "s1", 1f, 0f, 0f, 240);
        float v1000 = MoodShockStore.GetAggregatedDeltas("Abigail").v;
        Assert.Equal(1f, v1000); // ratio=1 → 满值

        clock = 1060; // +60min
        float v1060 = MoodShockStore.GetAggregatedDeltas("Abigail").v;
        Assert.True(v1060 < v1000); // 时间推进 → 衰减
        Assert.Equal(0.75f, v1060); // 1 - 60/240
    }

    // UT-02 倒流防护：Start > now 时 GetCurrentRatio 返回 1.0f 且不抛异常。
    [Fact]
    public void UT02_ReverseClock_ClampsToZeroElapsed()
    {
        var shock = new MoodShock { StartGameMinutes = 1000, DurationMinutes = 240 };
        float ratio = shock.GetCurrentRatio(800); // now < Start
        Assert.Equal(1.0f, ratio);
    }

    // UT-03 惰性过期：elapsed >= Duration 与 DurationMinutes<=0 均清除、聚合为 0、无 NaN。
    [Fact]
    public void UT03_LazyExpiration_RemovesAndZeroesDelta()
    {
        // 分支 A：elapsed >= Duration
        int clock = 1000;
        MoodShockStore.NowProvider = () => clock;
        MoodShockStore.ClearAll();
        MoodShockStore.AddShock("Test", "sA", 1f, 2f, 3f, 100);
        clock = 1200; // elapsed=200 >= 100
        var expired = MoodShockStore.GetAggregatedDeltas("Test");
        Assert.Equal(0f, expired.v);
        Assert.Equal(0f, expired.a);
        Assert.Equal(0f, expired.o);
        Assert.Equal(0, MoodShockStore.CountShocks("Test"));
        Assert.False(float.IsNaN(expired.v));

        // 分支 B：DurationMinutes <= 0 立即过期
        MoodShockStore.AddShock("Test", "sB", 1f, 2f, 3f, 0);
        var zeroDur = MoodShockStore.GetAggregatedDeltas("Test");
        Assert.Equal(0f, zeroDur.v);
        Assert.Equal(0, MoodShockStore.CountShocks("Test"));
    }

    // UT-04 同源覆盖与异槽并存。
    [Fact]
    public void UT04_SameSourceOverwrite_DifferentSlotCoexist()
    {
        int clock = 1000;
        MoodShockStore.NowProvider = () => clock;
        MoodShockStore.ClearAll();

        // 同 SourceId 两次 → CountShocks == 1
        MoodShockStore.AddShock("NPC", "dup", 0.50f, 0f, 0f, 240);
        MoodShockStore.AddShock("NPC", "dup", -0.50f, 0f, 0f, 240);
        Assert.Equal(1, MoodShockStore.CountShocks("NPC"));

        // Loved + Hated 并存 → Count==2 且聚合 V == 0.0f
        MoodShockStore.ClearAll();
        MoodShockStore.OnGiftDelivered("NPC", NPC.gift_taste_love);
        MoodShockStore.OnGiftDelivered("NPC", NPC.gift_taste_hate);
        Assert.Equal(2, MoodShockStore.CountShocks("NPC"));
        float aggregatedV = MoodShockStore.GetAggregatedDeltas("NPC").v;
        Assert.Equal(0.0f, aggregatedV);
    }

    // UT-05 Dampen：压缩为 remainingMinutes 且 PersistAcrossDays 变为 false。
    [Fact]
    public void UT05_Dampen_ReducesDurationAndClearsPersist()
    {
        int clock = 1000;
        MoodShockStore.NowProvider = () => clock;
        MoodShockStore.ClearAll();

        MoodShockStore.AddShock("NPC", "s1", 1f, 0f, 0f, 480, persistAcrossDays: true);

        // Dampen 前：持久条目，OnDayStarted 不应清除
        MoodShockStore.OnDayStarted();
        Assert.Equal(1, MoodShockStore.CountShocks("NPC"));

        // Dampen → 压缩到 120 分钟，PersistAcrossDays=false
        clock = 1100;
        MoodShockStore.DampenShock("NPC", "s1", 120);

        // Dampen 后：非持久条目，OnDayStarted 应当清除
        MoodShockStore.OnDayStarted();
        Assert.Equal(0, MoodShockStore.CountShocks("NPC"));
    }

    // UT-06 OnDayStarted 仅清除非持久条目。
    [Fact]
    public void UT06_OnDayStarted_KeepsPersistentEntries()
    {
        int clock = 1000;
        MoodShockStore.NowProvider = () => clock;
        MoodShockStore.ClearAll();

        MoodShockStore.AddShock("NPC", "temp", 1f, 0f, 0f, 480, persistAcrossDays: false);
        MoodShockStore.AddShock("NPC", "keep", 1f, 0f, 0f, 480, persistAcrossDays: true);

        MoodShockStore.OnDayStarted();

        Assert.Equal(1, MoodShockStore.CountShocks("NPC"));
        Assert.Equal(1f, MoodShockStore.GetAggregatedDeltas("NPC").v);
    }

    // UT-07 ClearNpc/ClearAll/CountShocks 语义 + 空白 npcName 防护。
    [Fact]
    public void UT07_ClearAndCount_Semantics()
    {
        int clock = 1000;
        MoodShockStore.NowProvider = () => clock;
        MoodShockStore.ClearAll();

        MoodShockStore.AddShock("A", "sA", 1f, 0f, 0f, 240);
        MoodShockStore.AddShock("B", "sB1", 1f, 0f, 0f, 240);
        MoodShockStore.AddShock("B", "sB2", 1f, 0f, 0f, 240);

        Assert.Equal(1, MoodShockStore.CountShocks("A"));
        Assert.Equal(2, MoodShockStore.CountShocks("B"));
        Assert.Equal(0, MoodShockStore.CountShocks("NonExistent"));

        // 空白 npcName → AddShock return、GetAggregatedDeltas 返回 (0,0,0)
        MoodShockStore.AddShock("", "x", 1f, 0f, 0f, 240);
        var blank = MoodShockStore.GetAggregatedDeltas("");
        Assert.Equal(0f, blank.v);
        Assert.Equal(0f, blank.a);
        Assert.Equal(0f, blank.o);

        MoodShockStore.ClearNpc("A");
        Assert.Equal(0, MoodShockStore.CountShocks("A"));
        Assert.Equal(2, MoodShockStore.CountShocks("B"));

        MoodShockStore.ClearAll();
        Assert.Equal(0, MoodShockStore.CountShocks("B"));
    }

    // 额外：OnGiftDelivered neutral → 不加 Shock。
    [Fact]
    public void OnGiftDelivered_NeutralCategory_NoShock()
    {
        int clock = 1000;
        MoodShockStore.NowProvider = () => clock;
        MoodShockStore.ClearAll();

        MoodShockStore.OnGiftDelivered("NPC", NPC.gift_taste_neutral);
        Assert.Equal(0, MoodShockStore.CountShocks("NPC"));
    }
}
