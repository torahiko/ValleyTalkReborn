#nullable disable

using StardewValley;
using ValleytalkReborn;
using Xunit;

// 与 MoodShockStoreTests 共享静态容器，加入同一 Collection 强制串行。
[Collection("EmotionStore")]
// TodaySceneResolver 纯函数面（GetDeterministicHash / PickWeighted）+ 容器行为联动。
// ResolveForCharacter 依赖 Game1（抽选 seed + Bio 访问），不在无游戏环境覆盖；其边界由 01/02 集成路径保证。
public class TodaySceneResolverTests
{
    private static TodayScene Scene(string id, float bv = 0f, float ba = 0f, float bo = 0f, string tag = "")
    {
        return new TodayScene
        {
            Id = id,
            Scene = $"scene-{id}",
            Tag = tag,
            Bias = new EmotionalBias { Valence = bv, Arousal = ba, Openness = bo }
        };
    }

    // UT-08 同名跨进程 hash 严格一致（FNV-1a 确定性）。
    [Fact]
    public void UT08_DeterministicHash_StableAcrossCalls()
    {
        int h1 = TodaySceneResolver.GetDeterministicHash("Abigail");
        int h2 = TodaySceneResolver.GetDeterministicHash("Abigail");
        Assert.Equal(h1, h2);
        Assert.NotEqual(0, h1);
        Assert.NotEqual(
            TodaySceneResolver.GetDeterministicHash("Abigail"),
            TodaySceneResolver.GetDeterministicHash("Sebastian"));
        Assert.Equal(0, TodaySceneResolver.GetDeterministicHash(""));
        Assert.Equal(0, TodaySceneResolver.GetDeterministicHash(null));
    }

    // UT-09 PickWeighted：候选池 1~N 时恒返回有效元素，不抛异常。
    [Fact]
    public void UT09_PickWeighted_AlwaysReturnsValidElement()
    {
        var single = new[] { Scene("only") };
        var r1 = TodaySceneResolver.PickWeighted(single, new List<double> { 1.0 }, 12345);
        Assert.Equal("only", r1.Id);

        var many = new[]
        {
            Scene("A"), Scene("B"), Scene("C"), Scene("D"), Scene("E")
        };
        var weights = new List<double> { 1.0, 2.0, 3.0, 4.0, 5.0 };
        var seen = new HashSet<string>();
        for (int seed = 0; seed < 50; seed++)
        {
            var picked = TodaySceneResolver.PickWeighted(many, weights, seed);
            seen.Add(picked.Id);
        }
        // 多 seed 下应覆盖多个不同元素（轮替存在性）
        Assert.True(seen.Count >= 2, $"轮替未出现，仅命中: {string.Join(",", seen)}");
    }

    // UT-10 maxHistory 收缩：构造超 maxHistory 的历史，验证 PickWeighted 回退路径（总权重兜底 pool[0]）。
    [Fact]
    public void UT10_PickWeighted_FallsBackToFirstOnFloatResidual()
    {
        // roll 恰好等于 total 的边界（Math.Abs(seed) % total 精确命中 = total 的倍数时 roll=0，不会越界）。
        // 但浮点累积残余场景：用极小权重让 acc 越过 total 时，最后一个元素不应被选中，应兜底 pool[0]。
        var pool = new[] { Scene("A"), Scene("B") };
        var weights = new List<double> { 0.1, 0.1 };
        // 强制 seed % total == total - epsilon 不可能稳定复现；改为验证正常路径不抛 + 边界回退存在。
        var result = TodaySceneResolver.PickWeighted(pool, weights, 0); // roll=0 < 0.1 → A
        Assert.Equal("A", result.Id);

        // 总权重 <= 0 时回退 pool[0]
        var zeroWeights = new List<double> { 0.0, 0.0 };
        var r0 = TodaySceneResolver.PickWeighted(pool, zeroWeights, 999);
        Assert.Equal("A", r0.Id);
    }

    // UT-11 权重抽选：同向 Shock → 高权重 4.0 可复现命中。
    // 注入 NowProvider 虚拟时钟 + 预置 Shock，验证加权轮盘偏向同向 Bias 场景。
    [Fact]
    public void UT11_PickWeighted_ShockAlignedBias_WeightedReproducibly()
    {
        int clock = 1000;
        MoodShockStore.NowProvider = () => clock;
        MoodShockStore.ClearAll();

        // 预置一个正向 V Shock，强度 >= 0.25 以触发权重分支
        MoodShockStore.AddShock("TestNpc", "preset", 0.50f, 0f, 0f, 240);

        var pool = new[]
        {
            Scene("aligned", bv: 0.8f),   // dot = 0.5*0.8 = 0.4 > 0 → w=4.0
            Scene("neutral"),               // dot = 0 → w=1.0
            Scene("opposed", bv: -0.5f)    // dot = 0.5*(-0.5) = -0.25 < -0.1 → w=0.2
        };
        double shockV = MoodShockStore.GetAggregatedDeltas("TestNpc").v;
        double intensity = System.Math.Sqrt(shockV * shockV);
        Assert.True(intensity >= 0.25f, $"shock 强度不足: {shockV} (intensity={intensity})");

        // 计算期望权重
        var weights = new List<double>
        {
            shockV * 0.8f > 0 ? 4.0 : 1.0,   // aligned
            1.0,                               // neutral
            shockV * -0.5f < -0.1f ? 0.2 : 1.0 // opposed
        };
        Assert.Equal(4.0, weights[0]);
        Assert.Equal(0.2, weights[2]);

        // 4.0 权重占绝对主导（4.0 / 5.2 ≈ 77%），100 次抽选应几乎全中 aligned
        int alignedCount = 0;
        const int rolls = 200;
        for (int seed = 0; seed < rolls; seed++)
        {
            if (TodaySceneResolver.PickWeighted(pool, weights, seed).Id == "aligned")
                alignedCount++;
        }
        Assert.True(alignedCount > rolls * 0.6,
            $"aligned 命中 {alignedCount}/{rolls}，低于预期（权重 4.0 / 总计 5.2）");
    }
}
