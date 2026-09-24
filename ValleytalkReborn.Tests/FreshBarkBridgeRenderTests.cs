using System;
using System.Collections.Generic;
using ValleytalkReborn;
using Xunit;

[Collection("EmotionStore")]
public class FreshBarkBridgeRenderTests
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

    // ── 验收 2a：RenderBridgeBlock zh/en ──

    [Fact]
    public void UT01_RenderBridgeBlock_Zh()
    {
        var block = FreshBarkBridgeStore.RenderBridgeBlock("今天天气真好", isZh: true);

        Assert.Contains("<fresh_bark_bridge>", block);
        Assert.Contains("</fresh_bark_bridge>", block);
        Assert.Contains("\"今天天气真好\"", block);
        Assert.Contains("不要逐字重复", block);
    }

    [Fact]
    public void UT02_RenderBridgeBlock_En()
    {
        var block = FreshBarkBridgeStore.RenderBridgeBlock("Nice weather today", isZh: false);

        Assert.Contains("<fresh_bark_bridge>", block);
        Assert.Contains("</fresh_bark_bridge>", block);
        Assert.Contains("\"Nice weather today\"", block);
        Assert.Contains("do not repeat the line", block);
    }

    // ── 验收 2b：BuildBridgeBlock 消费语义 ──

    [Fact]
    public void UT03_BuildBridgeBlock_RecordThenConsume()
    {
        ResetAll();
        FreshBarkBridgeStore.Record("Abigail", "今天天气真好");

        var block = FreshBarkBridgeStore.BuildBridgeBlock("Abigail");
        Assert.NotNull(block);
        Assert.Contains("今天天气真好", block);
        Assert.Contains("<fresh_bark_bridge>", block);

        // 阅后即焚
        Assert.Null(FreshBarkBridgeStore.BuildBridgeBlock("Abigail"));
    }

    // ── 验收 2c：过期 / 跨日 → null ──

    [Fact]
    public void UT04_BuildBridgeBlock_Expired()
    {
        ResetAll();
        FreshBarkBridgeStore.Record("Abigail", "测试台词");
        _now = _now.AddSeconds(3.1);

        Assert.Null(FreshBarkBridgeStore.BuildBridgeBlock("Abigail"));
    }

    [Fact]
    public void UT05_BuildBridgeBlock_CrossDay()
    {
        ResetAll();
        FreshBarkBridgeStore.Record("Abigail", "测试台词");
        _day = 101;

        Assert.Null(FreshBarkBridgeStore.BuildBridgeBlock("Abigail"));
    }

    // ── 验收 2d：未记录 NPC → null ──

    [Fact]
    public void UT06_BuildBridgeBlock_NoRecord()
    {
        ResetAll();

        Assert.Null(FreshBarkBridgeStore.BuildBridgeBlock("Abigail"));
    }
}
