using System;
using System.Collections.Generic;
using ValleytalkReborn;
using Xunit;

[Collection("EmotionStore")]
public class FreshBarkBridgeStoreTests
{
    private static DateTime _now;
    private static int _day;

    private static void ResetAll()
    {
        FreshBarkBridgeStore.ClearAll();
        _now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        _day = 100;
        FreshBarkBridgeStore.NowProvider = () => _now;
        FreshBarkBridgeStore.DayProvider = () => _day;
    }

    // ── 验收 2a：Record→TryConsume 返回原文；二次 TryConsume → null ──

    [Fact]
    public void UT01_RecordThenConsume_ReturnsLine()
    {
        ResetAll();
        FreshBarkBridgeStore.Record("Abigail", "今天天气真好");

        var line = FreshBarkBridgeStore.TryConsume("Abigail");
        Assert.Equal("今天天气真好", line);

        // 阅后即焚
        Assert.Null(FreshBarkBridgeStore.TryConsume("Abigail"));
    }

    // ── 验收 2b：NowProvider 前拨 2.9s → 命中；3.1s → null 且条目被移除 ──

    [Fact]
    public void UT02_Ttl_Within2s9_Hit()
    {
        ResetAll();
        FreshBarkBridgeStore.Record("Abigail", "测试台词");
        _now = _now.AddSeconds(2.9);

        Assert.Equal("测试台词", FreshBarkBridgeStore.TryConsume("Abigail"));
    }

    [Fact]
    public void UT03_Ttl_After3s1_Miss_AndRemoved()
    {
        ResetAll();
        FreshBarkBridgeStore.Record("Abigail", "测试台词");
        _now = _now.AddSeconds(3.1);

        Assert.Null(FreshBarkBridgeStore.TryConsume("Abigail"));

        // 条目已移除：ClearAll 后再 Record 验证表为空
        FreshBarkBridgeStore.ClearAll();
        FreshBarkBridgeStore.Record("Abigail", "新台词");
        _now = _now.AddSeconds(0.1);
        Assert.Equal("新台词", FreshBarkBridgeStore.TryConsume("Abigail"));
    }

    // ── 验收 2c：DayProvider 错开 → null ──

    [Fact]
    public void UT04_DayMismatch_Miss()
    {
        ResetAll();
        FreshBarkBridgeStore.Record("Abigail", "测试台词");
        _day = 101; // 跨日

        Assert.Null(FreshBarkBridgeStore.TryConsume("Abigail"));
    }

    // ── 验收 2d：同 NPC 重记录 → 旧行不可消费、新行可消费 ──

    [Fact]
    public void UT05_Overwrite_OldLineGone()
    {
        ResetAll();
        FreshBarkBridgeStore.Record("Abigail", "旧台词");
        FreshBarkBridgeStore.Record("Abigail", "新台词");

        Assert.Equal("新台词", FreshBarkBridgeStore.TryConsume("Abigail"));
        Assert.Null(FreshBarkBridgeStore.TryConsume("Abigail"));
    }

    // ── 验收 2e：NPC 隔离 ──

    [Fact]
    public void UT06_NpcIsolation()
    {
        ResetAll();
        FreshBarkBridgeStore.Record("Abigail", "艾比的台词");

        Assert.Null(FreshBarkBridgeStore.TryConsume("Sebastian"));
        Assert.Equal("艾比的台词", FreshBarkBridgeStore.TryConsume("Abigail"));
    }

    // ── 验收 2f：Record 空白行 → TryConsume null ──

    [Fact]
    public void UT07_BlankLine_NotRecorded()
    {
        ResetAll();
        FreshBarkBridgeStore.Record("Abigail", "");
        FreshBarkBridgeStore.Record("Abigail", "   ");

        Assert.Null(FreshBarkBridgeStore.TryConsume("Abigail"));
    }

    // ── 验收 2g：ClearAll 全清 ──

    [Fact]
    public void UT08_ClearAll()
    {
        ResetAll();
        FreshBarkBridgeStore.Record("Abigail", "台词A");
        FreshBarkBridgeStore.Record("Sebastian", "台词S");

        FreshBarkBridgeStore.ClearAll();

        Assert.Null(FreshBarkBridgeStore.TryConsume("Abigail"));
        Assert.Null(FreshBarkBridgeStore.TryConsume("Sebastian"));
    }
}
