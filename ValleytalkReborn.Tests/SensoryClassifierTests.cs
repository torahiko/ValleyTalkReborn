#nullable disable

using System;
using System.Collections.Generic;
using ValleytalkReborn;
using Xunit;

public class SensoryClassifierTests
{
    // ── 辅助 ──────────────────────────────────────────────────────────────

    /// <summary>构造最小 PerceptionEntry（仅 Key 参与映射判定）</summary>
    private static PerceptionEntry Entry(string key) => new() { Key = key };

    /// <summary>注入指定 bucket 的 BucketProvider，返回调用次数</summary>
    private static int InjectBucket(List<PerceptionEntry> bucket, out Func<string, int> callCount)
    {
        int count = 0;
        SensoryClassifier.BucketProvider = _ =>
        {
            count++;
            return bucket;
        };
        callCount = _ => count;
        return count;
    }

    private static void InjectCalls(out Action<int> setCount)
    {
        int count = 0;
        SensoryClassifier.BucketProvider = _ => { count++; return new List<PerceptionEntry>(); };
        setCount = _ => count = _;
    }

    [Fact]
    public void UT01_KeyMapping_LewisShorts_Continuous()
    {
        var bucket = new List<PerceptionEntry> { Entry("PlayerSpecialOutfit_Shorts") };
        InjectBucket(bucket, out _);

        var result = SensoryClassifier.Evaluate("Abigail");

        Assert.True(result.Hit);
        Assert.Equal(SensoryType.LewisShorts, result.Type);
        Assert.Equal(SensoryCategory.Continuous, result.Category);
        Assert.Equal("PlayerSpecialOutfit_Shorts", result.PerceptionKey);
        Assert.NotNull(result.Entry);
    }

    [Fact]
    public void UT02_KeyMapping_TrashOutfit_Continuous()
    {
        var bucket = new List<PerceptionEntry> { Entry("PlayerSpecialOutfit_Trash") };
        InjectBucket(bucket, out _);

        var result = SensoryClassifier.Evaluate("Abigail");

        Assert.True(result.Hit);
        Assert.Equal(SensoryType.TrashOutfit, result.Type);
        Assert.Equal(SensoryCategory.Continuous, result.Category);
    }

    [Fact]
    public void UT03_KeyMapping_HazmatSuit_Continuous()
    {
        var bucket = new List<PerceptionEntry> { Entry("PlayerSpecialOutfit_Hazmat") };
        InjectBucket(bucket, out _);

        var result = SensoryClassifier.Evaluate("Abigail");

        Assert.True(result.Hit);
        Assert.Equal(SensoryType.HazmatSuit, result.Type);
        Assert.Equal(SensoryCategory.Continuous, result.Category);
    }

    [Fact]
    public void UT04_KeyMapping_WeddingDress_Continuous()
    {
        var bucket = new List<PerceptionEntry> { Entry("PlayerWeddingOutfit") };
        InjectBucket(bucket, out _);

        var result = SensoryClassifier.Evaluate("Abigail");

        Assert.True(result.Hit);
        Assert.Equal(SensoryType.WeddingDress, result.Type);
        Assert.Equal(SensoryCategory.Continuous, result.Category);
    }

    [Fact]
    public void UT05_KeyMapping_FaintedYesterday_Continuous()
    {
        var bucket = new List<PerceptionEntry> { Entry("PlayerFainted") };
        InjectBucket(bucket, out _);

        var result = SensoryClassifier.Evaluate("Abigail");

        Assert.True(result.Hit);
        Assert.Equal(SensoryType.FaintedYesterday, result.Type);
        Assert.Equal(SensoryCategory.Continuous, result.Category);
    }

    [Fact]
    public void UT06_KeyMapping_GarlicStench_Transient()
    {
        var bucket = new List<PerceptionEntry> { Entry("PlayerGarlicSmell") };
        InjectBucket(bucket, out _);

        var result = SensoryClassifier.Evaluate("Abigail");

        Assert.True(result.Hit);
        Assert.Equal(SensoryType.GarlicStench, result.Type);
        Assert.Equal(SensoryCategory.Transient, result.Category);
    }

    [Fact]
    public void UT07_KeyMapping_MonsterMusk_Transient()
    {
        var bucket = new List<PerceptionEntry> { Entry("PlayerMonsterMusk") };
        InjectBucket(bucket, out _);

        var result = SensoryClassifier.Evaluate("Abigail");

        Assert.True(result.Hit);
        Assert.Equal(SensoryType.MonsterMusk, result.Type);
        Assert.Equal(SensoryCategory.Transient, result.Category);
    }

    [Fact]
    public void UT08_KeyMapping_Exhaustion_Transient()
    {
        var bucket = new List<PerceptionEntry> { Entry("PlayerExhausted") };
        InjectBucket(bucket, out _);

        var result = SensoryClassifier.Evaluate("Abigail");

        Assert.True(result.Hit);
        Assert.Equal(SensoryType.Exhaustion, result.Type);
        Assert.Equal(SensoryCategory.Transient, result.Category);
    }

    [Fact]
    public void UT09_IgnoresUnknownKeys()
    {
        var bucket = new List<PerceptionEntry> { Entry("PlayerHat"), Entry("PlayerActiveItem"), Entry("") };
        InjectBucket(bucket, out _);

        var result = SensoryClassifier.Evaluate("Abigail");

        Assert.False(result.Hit);
    }

    [Fact]
    public void UT10_MultiHit_TakesFirstBySalienceOrder()
    {
        // 注入顺序 [Exhausted, Shorts] → 命中 Exhaustion（按序取首个）
        var bucket = new List<PerceptionEntry> { Entry("PlayerExhausted"), Entry("PlayerSpecialOutfit_Shorts") };
        InjectBucket(bucket, out _);

        var result = SensoryClassifier.Evaluate("Abigail");

        Assert.True(result.Hit);
        Assert.Equal(SensoryType.Exhaustion, result.Type);
        Assert.Equal(SensoryCategory.Transient, result.Category);
    }

    [Fact]
    public void UT11_EmptyBucket_ReturnsNoHit()
    {
        var bucket = new List<PerceptionEntry>();
        InjectBucket(bucket, out _);

        var result = SensoryClassifier.Evaluate("Abigail");

        Assert.False(result.Hit);
    }

    [Fact]
    public void UT12_NullNpcName_ReturnsNoHit_WithoutCallingBucketProvider()
    {
        bool called = false;
        SensoryClassifier.BucketProvider = _ => { called = true; return new List<PerceptionEntry>(); };

        var result = SensoryClassifier.Evaluate(null);

        Assert.False(result.Hit);
        Assert.False(called);
    }

    [Fact]
    public void UT13_EmptyNpcName_ReturnsNoHit_WithoutCallingBucketProvider()
    {
        bool called = false;
        SensoryClassifier.BucketProvider = _ => { called = true; return new List<PerceptionEntry>(); };

        var result = SensoryClassifier.Evaluate("");

        Assert.False(result.Hit);
        Assert.False(called);
    }

    [Fact]
    public void UT14_NullBucket_ReturnsNoHit()
    {
        SensoryClassifier.BucketProvider = _ => null;

        var result = SensoryClassifier.Evaluate("Abigail");

        Assert.False(result.Hit);
    }

    [Fact]
    public void UT15_BucketProviderThrows_ReturnsNoHit_DoesNotPropagate()
    {
        SensoryClassifier.BucketProvider = _ => throw new InvalidOperationException("test");

        var result = SensoryClassifier.Evaluate("Abigail");

        Assert.False(result.Hit);
    }

    // ── 验收 2d：空桶 → Hit=false；npcName 空 → Hit=false 且 BucketProvider 未被调用（可用计数桩验证）──

    [Fact]
    public void UT16_EmptyBucket_HitFalse_ProviderCalledOnce()
    {
        int callCount = 0;
        SensoryClassifier.BucketProvider = _ => { callCount++; return new List<PerceptionEntry>(); };

        var result = SensoryClassifier.Evaluate("Abigail");

        Assert.False(result.Hit);
        Assert.Equal(1, callCount);
    }

    [Fact]
    public void UT17_NullNpcName_HitFalse_ProviderNotCalled()
    {
        int callCount = 0;
        SensoryClassifier.BucketProvider = _ => { callCount++; return new List<PerceptionEntry>(); };

        var result = SensoryClassifier.Evaluate(null);

        Assert.False(result.Hit);
        Assert.Equal(0, callCount);
    }
}
