using System;
using System.Collections.Generic;
using System.Runtime.Serialization;
using StardewModdingAPI;
using StardewValley;
using ValleytalkReborn;
using ValleytalkReborn.Dialogue.Coordination;
using Xunit;

namespace ValleytalkReborn.Tests;

[Collection("StaticGlobalStateCollection")]
public class ColdStartPromptConsistencyTests : IDisposable
{
    private readonly ModConfig _originalConfig;
    private readonly LocalizedContentManager.LanguageCode _originalLang;
    private readonly IDisposable _playerScope;
    private readonly IModHelper _originalSHelper;
    private readonly IMonitor _originalSMonitor;
    private readonly bool _needRestore;
    private readonly object _originalGame1;

    public ColdStartPromptConsistencyTests()
    {
        TestEnvironment.InstallHeadlessContext();
        var game1Field = typeof(Game1).GetField("game1", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
        _originalGame1 = game1Field?.GetValue(null);
        if (_originalGame1 == null)
        {
            var fakeGame1 = (Game1)FormatterServices.GetUninitializedObject(typeof(Game1));
            game1Field?.SetValue(null, fakeGame1);
        }

        var sHelperField = typeof(ModEntry).GetField("<SHelper>k__BackingField",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        _originalSHelper = (IModHelper)sHelperField?.GetValue(null);
        _originalSMonitor = ModEntry.SMonitor;
        _originalConfig = ModEntry.Config;
        _originalLang = LocalizedContentManager.CurrentLanguageCode;
        _needRestore = _originalSHelper == null;
        if (_needRestore)
        {
            ModEntry.SMonitor = new FakeMonitor();
            ModEntry.Config = new ModConfig { EnablePerceptionSystem = true };
            sHelperField?.SetValue(null, new FakeModHelper(string.Empty));
        }
        else
        {
            ModEntry.Config = new ModConfig { EnablePerceptionSystem = true };
        }
        _playerScope = FakePlayer.Install("虎彦");
    }

    public void Dispose()
    {
        _playerScope?.Dispose();
        ModEntry.Config = _originalConfig;
        LocalizedContentManager.CurrentLanguageCode = _originalLang;
        if (_needRestore)
        {
            var sHelperField = typeof(ModEntry).GetField("<SHelper>k__BackingField",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
            sHelperField?.SetValue(null, _originalSHelper);
            ModEntry.SMonitor = _originalSMonitor;
        }

        if (_originalGame1 == null)
        {
            var game1Field = typeof(Game1).GetField("game1", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
            game1Field?.SetValue(null, null);
        }
    }

    private static Prompts CreatePrompts()
    {
        return (Prompts)FormatterServices.GetUninitializedObject(typeof(Prompts));
    }

    private static Character CreateCharacter(string name, NPC npc)
    {
        var character = (Character)FormatterServices.GetUninitializedObject(typeof(Character));
        typeof(Character).GetField("<Name>k__BackingField", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?.SetValue(character, name);
        typeof(Character).GetProperty("StardewNpc")?.SetValue(character, npc);
        return character;
    }

    [Fact]
    public void UT01_BuildResponseTriggerSuffix_Zh_ContainsColdOpeningPositiveFocus()
    {
        LocalizedContentManager.CurrentLanguageCode = LocalizedContentManager.LanguageCode.zh;
        ModEntry.Config.LanguageOverride = "zh";

        var prompts = CreatePrompts();
        string suffix = prompts.BuildResponseTriggerSuffix("虎彦");

        Assert.Contains("<response_trigger>", suffix);
        Assert.Contains("不凭空编造复杂协同计划", suffix);
        Assert.Contains("小镇传闻仅作为可选背景闲聊", suffix);
        Assert.Contains("顺着已承接的话题向前推进", suffix);
    }

    [Fact]
    public void UT02_BuildResponseTriggerSuffix_En_ContainsColdOpeningPositiveFocus()
    {
        LocalizedContentManager.CurrentLanguageCode = LocalizedContentManager.LanguageCode.en;
        ModEntry.Config.LanguageOverride = "en";

        var prompts = CreatePrompts();
        string suffix = prompts.BuildResponseTriggerSuffix("Farmer");

        Assert.Contains("<response_trigger>", suffix);
        Assert.Contains("rather than fabricating elaborate cooperative missions", suffix);
        Assert.Contains("Town rumors serve as optional background chatter only", suffix);
        Assert.Contains("carry forward the accepted topic", suffix);
    }

    [Fact]
    public void UT03_ColdStartToTurn1_BranchUnchanged_SessionReused()
    {
        // 验证 KV-Cache 前缀一致性约束（Contract Delta G）：
        // 冷启动（Turn 0）与后续选分支/回复（Turn 1）均保持 InstructionsBranch.Normal，
        // 使得 Tier1SnapshotStore 能够正确续用会话与前缀快照。
        var npc = new NPC { Name = "Hakan" };
        var character = CreateCharacter("Hakan", npc);

        // Turn 0: 冷启动开场
        var turn0Input = new ContextRouteInput(
            Npc: npc,
            PlayerInput: "",
            SafetyMode: SafetyModeLevel.Strict,
            ChatHistory: new List<ConversationElement>(),
            IsActiveTurn: false
        );
        var turn0Flags = ContextRouter.Evaluate(turn0Input);
        Assert.True(turn0Flags.IsColdOpening);

        // 模拟建立活动会话
        string testSessionId = Guid.NewGuid().ToString("N");
        var snapshot = new Tier1SnapshotContext(new Dictionary<string, string>());
        Tier1SnapshotStore.RegisterActiveSession(testSessionId, "Hakan", "", InstructionsBranch.Normal, snapshot);

        // Turn 1: 玩家选了选项回答（Turn 1+）
        var turn1Input = new ContextRouteInput(
            Npc: npc,
            PlayerInput: "好啊，正好一起去挑点贺礼。",
            SafetyMode: SafetyModeLevel.Strict,
            ChatHistory: new List<ConversationElement>(),
            IsActiveTurn: true
        );
        var turn1Flags = ContextRouter.Evaluate(turn1Input);
        Assert.False(turn1Flags.IsColdOpening);

        // 验证：分支依然为 Normal，SessionId 一致复用
        bool reused = Tier1SnapshotStore.TryReuseSession(character, InstructionsBranch.Normal, out string reusedSessionId);
        Assert.True(reused, "Session must be reused between Turn 0 and Turn 1 under InstructionsBranch.Normal");
        Assert.Equal(testSessionId, reusedSessionId);

        // 验证：快照能够被安全取出
        bool snapshotRetrieved = Tier1SnapshotStore.TryGetSnapshot(reusedSessionId, out var retrievedSnapshot);
        Assert.True(snapshotRetrieved);
        Assert.Same(snapshot, retrievedSnapshot);
    }
}
