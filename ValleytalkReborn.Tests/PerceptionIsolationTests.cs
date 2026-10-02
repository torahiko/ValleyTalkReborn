using System;
using System.Collections.Generic;
using System.Linq;
using StardewValley;
using ValleytalkReborn;
using Xunit;

namespace ValleytalkReborn.Tests;

[Collection("StaticGlobalStateCollection")]
public class PerceptionIsolationTests : IDisposable
{
    private readonly ModConfig _originalConfig;

    public PerceptionIsolationTests()
    {
        TestEnvironment.InstallHeadlessContext();
        _originalConfig = ModEntry.Config;
        ModEntry.Config = new ModConfig
        {
            EnablePerceptionSystem = true
        };
        PerceptionManager.Instance.Cleanup();
    }

    public void Dispose()
    {
        PerceptionManager.Instance.Cleanup();
        ModEntry.Config = _originalConfig;
    }

    [Fact]
    public void UT01_RecordGossip_UnspecifiedLocation_ResolvesToEmptyString()
    {
        // 即使当前玩家在特定地图，全局八卦未指定地点时必须为空字符串，不可回退至当前玩家地图
        PerceptionManager.Instance.RecordGossip("WorldNews", "Headline news without location", lifetimeHours: 24);

        var snapshots = PerceptionManager.Instance.GetGossipSnapshots();
        var entry = snapshots.FirstOrDefault(e => e.Key == "WorldNews");

        Assert.NotNull(entry);
        Assert.Equal(string.Empty, entry.LocationName);
    }

    [Fact]
    public void UT02_RecordLandmark_UnspecifiedLocation_ResolvesToEmptyString()
    {
        // Landmark 属于全镇级别广播，未指定地点时也必须解析为空字符串
        PerceptionManager.Instance.Record(
            key: "LegendaryFish",
            template: "Caught a legendary fish!",
            npcName: null,
            lifetimeHours: 20,
            isGossip: false,
            isLandmark: true,
            locationName: null
        );

        var snapshots = PerceptionManager.Instance.GetGossipSnapshots();
        var entry = snapshots.FirstOrDefault(e => e.Key == "LegendaryFish");

        Assert.NotNull(entry);
        Assert.Equal(string.Empty, entry.LocationName);
    }

    [Fact]
    public void UT03_RecordGossipAndLandmark_ExplicitLocation_Preserved()
    {
        // 显式传入地点时原值保留
        PerceptionManager.Instance.Record(
            key: "BeachNews",
            template: "Something happened on the beach",
            npcName: null,
            lifetimeHours: 24,
            isGossip: true,
            isLandmark: false,
            locationName: "Beach"
        );

        var snapshots = PerceptionManager.Instance.GetGossipSnapshots();
        var entry = snapshots.FirstOrDefault(e => e.Key == "BeachNews");

        Assert.NotNull(entry);
        Assert.Equal("Beach", entry.LocationName);
    }

    [Fact]
    public void UT04_GetFilteredBucketFor_DoesNotIncludeGlobalGossip()
    {
        // 注入全镇八卦（Track 1）与 1 条玩家状态（Track 2）
        PerceptionManager.Instance.RecordGossip("WorldNews", "Gossip news item", lifetimeHours: 24);
        PerceptionManager.Instance.Record(
            key: "PlayerActiveItem",
            template: "Player is holding a fishing rod",
            npcName: null,
            lifetimeHours: 2,
            isGossip: false,
            isLandmark: false,
            locationName: "FarmHouse",
            itemId: "(T)IridiumRod"
        );

        // 调用 GetFilteredBucketFor，断言结果中严格没有 IsGossip 条目
        var localPerceptions = PerceptionManager.Instance.GetFilteredBucketFor("Hakan", max: 3);

        Assert.NotEmpty(localPerceptions);
        Assert.All(localPerceptions, p => Assert.False(p.IsGossip, "Track 1 global gossip leaked into local perception bucket!"));
        Assert.DoesNotContain(localPerceptions, p => p.Key == "WorldNews");
        Assert.Contains(localPerceptions, p => p.Key == "PlayerActiveItem");
    }

    [Fact]
    public void UT05_MarkAsConsolidated_DoesNotMutateGlobalGossip()
    {
        // 注入全镇八卦，并调用 MarkAsConsolidated
        PerceptionManager.Instance.RecordGossip("WorldNews", "Global gossip news", lifetimeHours: 24);

        PerceptionManager.Instance.MarkAsConsolidated("Hakan");

        var snapshots = PerceptionManager.Instance.GetGossipSnapshots();
        var entry = snapshots.FirstOrDefault(e => e.Key == "WorldNews");

        Assert.NotNull(entry);
        Assert.False(entry.IsConsolidated, "Global gossip entry should not be marked consolidated by MarkAsConsolidated!");
    }

    [Fact]
    public void UT06_GetGossipSnapshots_A2AConsumer_RemainsFunctional()
    {
        // 验证 GetGossipSnapshots 保持对非主对话独立消费者（如 A2A）的正常读取功能
        PerceptionManager.Instance.RecordGossip("LifeEvent", "Farmer married Alex", lifetimeHours: 40);

        var snapshots = PerceptionManager.Instance.GetGossipSnapshots();

        Assert.Single(snapshots);
        Assert.Equal("LifeEvent", snapshots[0].Key);
        Assert.True(snapshots[0].IsGossip);
    }
}
