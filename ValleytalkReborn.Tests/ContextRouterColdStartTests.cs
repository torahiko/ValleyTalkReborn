using System;
using System.Collections.Generic;
using System.Linq;
using StardewValley;
using ValleytalkReborn;
using ValleytalkReborn.Dialogue.Coordination;
using Xunit;

namespace ValleytalkReborn.Tests;

[Collection("StaticGlobalStateCollection")]
public class ContextRouterColdStartTests : IDisposable
{
    private readonly ModConfig _originalConfig;

    public ContextRouterColdStartTests()
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
    public void UT01_ContextFlags_Clone_PreservesIsColdOpening()
    {
        var flags = new ContextFlags
        {
            IsColdOpening = true,
            IncludeFarmDetails = false
        };

        var clone = flags.Clone();

        Assert.True(clone.IsColdOpening);
        Assert.False(clone.IncludeFarmDetails);

        flags.IsColdOpening = false;
        var clone2 = flags.Clone();
        Assert.False(clone2.IsColdOpening);
    }

    [Fact]
    public void UT02_Evaluate_EmptyInput_NotActiveTurn_SetsColdOpeningAndDisablesFarmDetails()
    {
        var npc = new NPC { Name = "Hakan" };
        var input = new ContextRouteInput(
            Npc: npc,
            PlayerInput: "",
            SafetyMode: SafetyModeLevel.Strict,
            ChatHistory: new List<ConversationElement>(),
            IsActiveTurn: false
        );

        var flags = ContextRouter.Evaluate(input);

        Assert.True(flags.IsColdOpening);
        Assert.False(flags.IncludeFarmDetails);
    }

    [Fact]
    public void UT03_Evaluate_WhitespaceInput_NotActiveTurn_SetsColdOpening()
    {
        var npc = new NPC { Name = "Hakan" };
        var input = new ContextRouteInput(
            Npc: npc,
            PlayerInput: "   \t\n  ",
            SafetyMode: SafetyModeLevel.Strict,
            ChatHistory: new List<ConversationElement>(),
            IsActiveTurn: false
        );

        var flags = ContextRouter.Evaluate(input);

        Assert.True(flags.IsColdOpening);
        Assert.False(flags.IncludeFarmDetails);
    }

    [Fact]
    public void UT04_Evaluate_ActiveTurn_DoesNotSetColdOpening()
    {
        var npc = new NPC { Name = "Hakan" };
        var input = new ContextRouteInput(
            Npc: npc,
            PlayerInput: "",
            SafetyMode: SafetyModeLevel.Strict,
            ChatHistory: new List<ConversationElement>(),
            IsActiveTurn: true
        );

        var flags = ContextRouter.Evaluate(input);

        Assert.False(flags.IsColdOpening);
        Assert.True(flags.IncludeFarmDetails);
    }

    [Fact]
    public void UT05_BuildLocalBlock_ColdOpening_SuppressesHeldItem_KeepsUnconsumedAndUnmarked()
    {
        string npcName = "Hakan";
        string itemId = "(T)IridiumRod";

        PerceptionManager.Instance.Record(
            key: "PlayerActiveItem",
            template: "玩家行囊边缘露出了【高级铱金鱼竿】",
            npcName: null,
            lifetimeHours: 2,
            isGossip: false,
            isLandmark: false,
            locationName: "FarmHouse",
            itemId: itemId
        );

        // 冷启动开场：构建本地块
        string localBlock = PerceptionInjector.BuildLocalBlock(npcName, isColdOpening: true);

        // 1. 文本中被筛除，不含鱼竿内容（返回空串）
        Assert.True(string.IsNullOrEmpty(localBlock) || !localBlock.Contains("高级铱金鱼竿"));

        // 2. 审美疲劳未被标记（保持未标记状态）
        Assert.False(PerceptionManager.Instance.HasNoticedItemToday(npcName, itemId));

        // 3. 感知条目未被消费（保持未消费状态）
        var remainingPerceptions = PerceptionManager.Instance.GetFilteredBucketFor(npcName, 3);
        Assert.Contains(remainingPerceptions, p => p.Key == "PlayerActiveItem" && !p.IsConsumedBy(npcName));
    }

    [Fact]
    public void UT06_BuildLocalBlock_ColdOpening_RetainsGiftAndDangerStates()
    {
        string npcName = "Hakan";

        // 注入礼物（强事件）与残血低生命（生理危险状态）
        PerceptionManager.Instance.Record(
            key: "Gift",
            template: "农夫送了你一个礼物",
            npcName: npcName,
            lifetimeHours: 2,
            isGossip: false,
            isLandmark: false,
            locationName: "FarmHouse",
            itemId: "388"
        );

        PerceptionManager.Instance.Record(
            key: "PlayerLowHealth",
            template: "农夫看起来面色苍白，身上带伤",
            npcName: null,
            lifetimeHours: 2,
            isGossip: false,
            isLandmark: false,
            locationName: "FarmHouse",
            itemId: null
        );

        string localBlock = PerceptionInjector.BuildLocalBlock(npcName, isColdOpening: true);

        // 礼物与残血危险状态必须穿透保留
        Assert.False(string.IsNullOrEmpty(localBlock));
        Assert.True(localBlock.Contains("Immediate Event") || localBlock.Contains("即时事件"));
        Assert.Contains("农夫看起来面色苍白，身上带伤", localBlock);
    }
}
