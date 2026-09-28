// OutputQueueValidationTests.cs
// VT-AMB-03: 输出队列投递结果（Displayed/Rejected/Failed/Cleared）与状态提交对齐验证。
//
// 纯内存测试：通过 ScopedPlayer/FakePlayer 控制 Game1.player，通过反射驱动
// AmbientBarkModule.OnBarkOutputCompleted / A2ASessionManager.OnA2AOutputCompleted
// 两个主线程回调。Bark 的 Displayed 分支需要真实 NPC 才能算出下一句间隔
// （Game1.getCharacterFromName 在无存档环境下不可用），故该分支不在单测范围内，
// 由 A2A Displayed 与队列层"恰一次"用例共同覆盖提交语义。

using System;
using System.Collections.Generic;
using System.Reflection;
using ValleytalkReborn;
using ValleytalkReborn.Dialogue.Coordination;
using ValleytalkReborn.Tests;
using StardewValley;
using Xunit;

[Collection("StaticGlobalStateCollection")]
public class OutputQueueValidationTests
{
    // ─────────────────────────── 工具 ───────────────────────────

    private sealed class StubValidator : IA2AOutputValidator
    {
        private readonly bool _valid;
        public StubValidator(bool valid) { _valid = valid; }
        public bool Validate(string sessionId, int generation, string npcName) => _valid;
    }

    private sealed class RecordingCallback
    {
        public int Calls;
        public OutputDeliveryResult? Last;
        public void OnCompleted(OutputDeliveryResult result)
        {
            Calls++;
            Last = result;
        }
    }

    private sealed class Scope : IDisposable
    {
        private readonly Action _restore;
        private bool _disposed;
        public Scope(Action restore) { _restore = restore; }
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _restore();
        }
    }

    private static readonly FieldInfo PlayerField =
        typeof(Game1).GetField("_player", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

    private static readonly MethodInfo BarkCallbackMethod =
        typeof(AmbientBarkModule).GetMethod("OnBarkOutputCompleted", BindingFlags.Instance | BindingFlags.NonPublic);

    private static readonly MethodInfo A2ACallbackMethod =
        typeof(A2ASessionManager).GetMethod("OnA2AOutputCompleted", BindingFlags.Instance | BindingFlags.NonPublic);

    private static readonly FieldInfo A2ASessionsField =
        typeof(A2ASessionManager).GetField("_activeA2ASessions", BindingFlags.Instance | BindingFlags.NonPublic);

    private static MainThreadOutputQueue NewQueue(bool validatorValid = false)
        => new MainThreadOutputQueue(new StubValidator(validatorValid));

    private static IDisposable ScopedPlayer(object value)
    {
        object previous = PlayerField?.GetValue(null);
        PlayerField?.SetValue(null, value);
        return new Scope(() => PlayerField?.SetValue(null, previous));
    }

    /// <summary>固定 FreshBarkBridgeStore 的时钟/日期，避免触碰 Game1.Date。</summary>
    private static IDisposable ScopedBridgeClock()
    {
        var now = FreshBarkBridgeStore.NowProvider;
        var day = FreshBarkBridgeStore.DayProvider;
        var fixedNow = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        FreshBarkBridgeStore.NowProvider = () => fixedNow;
        FreshBarkBridgeStore.DayProvider = () => 100;
        return new Scope(() =>
        {
            FreshBarkBridgeStore.NowProvider = now;
            FreshBarkBridgeStore.DayProvider = day;
            FreshBarkBridgeStore.ClearAll();
        });
    }

    private static void InvokeBarkCallback(
        AmbientBarkModule module, string npcName, string text, bool isMicroSocial, OutputDeliveryResult result)
    {
        BarkCallbackMethod.Invoke(module, new object[] { npcName, text, isMicroSocial, result });
    }

    private static void InvokeA2ACallback(
        A2ASessionManager manager, string sessionId, int generation, string npcName, string line, OutputDeliveryResult result)
    {
        A2ACallbackMethod.Invoke(manager, new object[] { sessionId, generation, npcName, line, result });
    }

    private static void RegisterSession(A2ASessionManager manager, DialogueModels.A2ASession session)
    {
        var list = (List<DialogueModels.A2ASession>)A2ASessionsField.GetValue(manager);
        list.Add(session);
    }

    private static AmbientBarkModule NewBarkModule(AmbientBarkStateStore store, MainThreadOutputQueue queue)
        => new AmbientBarkModule(store, null, null, queue, null, new ModConfig());

    // ─────────────────── 队列层：结算结果与"恰一次" ───────────────────

    // Player 未就绪 → Failed，且回调只执行一次；队列排空后不再触发
    [Fact]
    public void UT01_PlayerNull_InvokesFailedExactlyOnce()
    {
        using var playerScope = ScopedPlayer(null);
        var queue = NewQueue();
        var cb = new RecordingCallback();

        queue.Enqueue("Abigail", "你好", 3500, "Bark", false, cb.OnCompleted);
        queue.Process(10);

        Assert.Equal(1, cb.Calls);
        Assert.Equal(OutputDeliveryResult.Failed, cb.Last);

        queue.Process(10);
        Assert.Equal(1, cb.Calls);
    }

    // 无回调条目（OnCompleted == null）保持原有行为，不抛异常
    [Fact]
    public void UT02_Process_WithoutCallback_DoesNotThrow()
    {
        using var playerScope = ScopedPlayer(null);
        var queue = NewQueue();

        queue.Enqueue("Abigail", "你好", 3500, "Bark");
        queue.Process(10);
    }

    // Clear → 每个条目恰好一次 Cleared
    [Fact]
    public void UT03_Clear_ResolvesEachItemAsClearedOnce()
    {
        using var playerScope = ScopedPlayer(null);
        var queue = NewQueue();
        var barkCb = new RecordingCallback();
        var a2aCb = new RecordingCallback();

        queue.Enqueue("Abigail", "bark", 3500, "Bark", false, barkCb.OnCompleted);
        queue.EnqueueA2A("sess", 1, "Sebastian", "a2a", 3500, a2aCb.OnCompleted);

        queue.Clear();

        Assert.Equal(1, barkCb.Calls);
        Assert.Equal(1, a2aCb.Calls);
        Assert.Equal(OutputDeliveryResult.Cleared, barkCb.Last);
        Assert.Equal(OutputDeliveryResult.Cleared, a2aCb.Last);

        queue.Process(10);
        Assert.Equal(1, barkCb.Calls);
        Assert.Equal(1, a2aCb.Calls);
    }

    // ClearType 只结算目标类型；其余类型保持原顺序并仍可在 Process 中结算
    [Fact]
    public void UT04_ClearType_ResolvesOnlyTargetTypeAsCleared()
    {
        using var playerScope = ScopedPlayer(null);
        var queue = NewQueue();
        var barkCb = new RecordingCallback();
        var a2aCb = new RecordingCallback();

        queue.Enqueue("Abigail", "bark", 3500, "Bark", false, barkCb.OnCompleted);
        queue.EnqueueA2A("sess", 1, "Sebastian", "a2a", 3500, a2aCb.OnCompleted);

        queue.ClearType("Bark");

        Assert.Equal(1, barkCb.Calls);
        Assert.Equal(OutputDeliveryResult.Cleared, barkCb.Last);
        Assert.Equal(0, a2aCb.Calls);

        queue.Process(10);
        Assert.Equal(1, a2aCb.Calls);
        Assert.Equal(OutputDeliveryResult.Failed, a2aCb.Last); // Player 仍为 null
    }

    // 缺参条目不入队，也不产生任何回调
    [Fact]
    public void UT05_MissingInput_NotEnqueued_NoCallback()
    {
        using var playerScope = ScopedPlayer(null);
        var queue = NewQueue();
        var cb = new RecordingCallback();

        queue.Enqueue("", "text", 3500, "Bark", false, cb.OnCompleted);
        queue.Enqueue("Abigail", "   ", 3500, "Bark", false, cb.OnCompleted);
        queue.EnqueueA2A("", 1, "Abigail", "text", 3500, cb.OnCompleted);
        queue.EnqueueA2A("sess", 1, "Abigail", "", 3500, cb.OnCompleted);

        queue.Process(10);

        Assert.Equal(0, cb.Calls);
    }

    // ─────────────────── Bark 回调：状态提交对齐 ───────────────────

    // Rejected（距离/原版交互/清理拒绝）→ 队首保留、Pending 清空、不写历史
    [Fact]
    public void UT06_BarkRejected_KeepsQueueHead_AndClearsPending()
    {
        var store = new AmbientBarkStateStore();
        var module = NewBarkModule(store, NewQueue());
        var state = store.GetOrCreate("Abigail");
        state.BarkQueue.Enqueue("留在队首的台词");
        state.PendingBarkText = "留在队首的台词";

        InvokeBarkCallback(module, "Abigail", "留在队首的台词", false, OutputDeliveryResult.Rejected);

        Assert.Null(state.PendingBarkText);
        Assert.False(state.PendingBarkIsMicroSocial);
        Assert.Single(state.BarkQueue);
        Assert.Equal("留在队首的台词", state.BarkQueue.Peek());
        Assert.Empty(state.RecentBarks);
        Assert.False(state.HasPlayedFirst);
    }

    // Failed → 丢弃当前线程并施加冷却，防止逐 Tick 无限重试；Pending 清空
    [Fact]
    public void UT07_BarkFailed_ClearsThreadAndAppliesCooldown()
    {
        var store = new AmbientBarkStateStore();
        var module = NewBarkModule(store, NewQueue());
        var state = store.GetOrCreate("Abigail");
        state.BarkQueue.Enqueue("失败台词");
        state.BarkQueue.Enqueue("后续台词");
        state.PendingBarkText = "失败台词";

        InvokeBarkCallback(module, "Abigail", "失败台词", false, OutputDeliveryResult.Failed);

        Assert.Null(state.PendingBarkText);
        Assert.Empty(state.BarkQueue);
        Assert.True(state.CooldownTicksRemaining.HasValue && state.CooldownTicksRemaining.Value > 0);
    }

    // Pending 与回调原文不一致 → 报错且不出队非匹配队首、不写历史
    [Fact]
    public void UT08_BarkMismatch_DoesNotDequeueOrWriteHistory()
    {
        var store = new AmbientBarkStateStore();
        var module = NewBarkModule(store, NewQueue());
        var state = store.GetOrCreate("Abigail");
        state.BarkQueue.Enqueue("队首台词");
        state.PendingBarkText = "不再是队首的台词";

        InvokeBarkCallback(module, "Abigail", "回调台词", false, OutputDeliveryResult.Displayed);

        Assert.Null(state.PendingBarkText);
        Assert.Single(state.BarkQueue);
        Assert.Equal("队首台词", state.BarkQueue.Peek());
        Assert.Empty(state.RecentBarks);
        Assert.False(state.HasPlayedFirst);
    }

    // ─────────── MicroSocial 首句标记（先拒后播的桥身份）───────────

    // Rejected → 保留队首且保留首句标记，重派时仍可识别（不变量：标记非空 ⇒ Peek() == 标记）
    [Fact]
    public void UT15_BarkRejected_KeepsMicroFirstLineMarker()
    {
        var store = new AmbientBarkStateStore();
        var module = NewBarkModule(store, NewQueue());
        var state = store.GetOrCreate("Abigail");
        state.BarkQueue.Enqueue("微社交首句");
        state.BarkQueue.Enqueue("后续台词");
        state.PendingBarkText = "微社交首句";
        state.PendingBarkIsMicroSocial = true;
        state.MicroFirstLineText = "微社交首句";

        InvokeBarkCallback(module, "Abigail", "微社交首句", true, OutputDeliveryResult.Rejected);

        Assert.Equal("微社交首句", state.MicroFirstLineText);
        Assert.Collection(
            state.BarkQueue,
            line => Assert.Equal("微社交首句", line),
            line => Assert.Equal("后续台词", line));
        Assert.Equal(state.BarkQueue.Peek(), state.MicroFirstLineText);
        Assert.Empty(state.RecentBarks);
        Assert.Null(state.PendingBarkText);
    }

    // Cleared → 标记清空（宁漏录，不误录）
    [Fact]
    public void UT16_BarkCleared_ClearsMicroFirstLineMarker()
    {
        var store = new AmbientBarkStateStore();
        var module = NewBarkModule(store, NewQueue());
        var state = store.GetOrCreate("Abigail");
        state.BarkQueue.Enqueue("微社交首句");
        state.PendingBarkText = "微社交首句";
        state.MicroFirstLineText = "微社交首句";

        InvokeBarkCallback(module, "Abigail", "微社交首句", true, OutputDeliveryResult.Cleared);

        Assert.Null(state.MicroFirstLineText);
        Assert.Equal("微社交首句", state.BarkQueue.Peek());
    }

    // Failed → 线程整体丢弃，标记随之清空
    [Fact]
    public void UT17_BarkFailed_ClearsMicroFirstLineMarker()
    {
        var store = new AmbientBarkStateStore();
        var module = NewBarkModule(store, NewQueue());
        var state = store.GetOrCreate("Abigail");
        state.BarkQueue.Enqueue("微社交首句");
        state.PendingBarkText = "微社交首句";
        state.MicroFirstLineText = "微社交首句";

        InvokeBarkCallback(module, "Abigail", "微社交首句", true, OutputDeliveryResult.Failed);

        Assert.Null(state.MicroFirstLineText);
        Assert.Empty(state.BarkQueue);
    }

    // ─────────────────── 清理路径：Pending 无残留 ───────────────────

    // 运行态重置清空 Pending 字段与 MicroSocial 首句标记
    [Fact]
    public void UT09_StateStore_ResetClearsPending()
    {
        var store = new AmbientBarkStateStore();
        var state = store.GetOrCreate("Abigail");
        state.PendingBarkText = "台词";
        state.PendingBarkIsMicroSocial = true;
        state.MicroFirstLineText = "台词";

        state.ClearRuntimeState();

        Assert.Null(state.PendingBarkText);
        Assert.False(state.PendingBarkIsMicroSocial);
        Assert.Null(state.MicroFirstLineText);
    }

    // CleanupAll（关闭 AmbientBarks / 换天 / 回标题）→ 队列条目以 Cleared 结算且 State 无残留
    [Fact]
    public void UT10_CleanupAll_ResolvesQueuedBarkAsCleared_AndClearsPending()
    {
        var store = new AmbientBarkStateStore();
        var queue = NewQueue();
        var module = NewBarkModule(store, queue);
        var cb = new RecordingCallback();

        var state = store.GetOrCreate("Abigail");
        state.BarkQueue.Enqueue("未播放台词");
        state.PendingBarkText = "未播放台词";
        state.MicroFirstLineText = "未播放台词";
        queue.Enqueue("Abigail", "未播放台词", 3500, "Bark", false, cb.OnCompleted);

        module.CleanupAll();

        Assert.Equal(1, cb.Calls);
        Assert.Equal(OutputDeliveryResult.Cleared, cb.Last);
        Assert.Empty(store.Snapshot());
        Assert.Null(state.MicroFirstLineText);   // reset 路径不得残留首句标记
    }

    // ─────────────────── A2A 回调：历史只在 Displayed 提交 ───────────────────

    // Displayed → RecentSpokenLines 恰提交一次，PendingOutputCount 释放，节奏复位 240 ticks
    [Fact]
    public void UT11_A2A_Displayed_CommitsRecentSpokenLinesOnce()
    {
        var manager = new A2ASessionManager(null, NewQueue(true), null, null);
        var session = new DialogueModels.A2ASession { SessionId = "s1", Generation = 1 };
        session.PendingOutputCount = 1;
        RegisterSession(manager, session);

        InvokeA2ACallback(manager, "s1", 1, "Abigail", "你好", OutputDeliveryResult.Displayed);

        Assert.Single(session.RecentSpokenLines);
        Assert.Equal(("Abigail", "你好"), session.RecentSpokenLines.Peek());
        Assert.Equal(0, session.PendingOutputCount);
        Assert.Equal(240, session.ReadCooldownTicks);
    }

    // Rejected → 不写 RecentSpokenLines，但仍释放 PendingOutputCount
    [Fact]
    public void UT12_A2A_Rejected_DoesNotWriteRecentSpokenLines()
    {
        var manager = new A2ASessionManager(null, NewQueue(true), null, null);
        var session = new DialogueModels.A2ASession { SessionId = "s1", Generation = 1 };
        session.PendingOutputCount = 1;
        RegisterSession(manager, session);

        InvokeA2ACallback(manager, "s1", 1, "Abigail", "台词", OutputDeliveryResult.Rejected);

        Assert.Empty(session.RecentSpokenLines);
        Assert.Equal(0, session.PendingOutputCount);
    }

    // 会话已取消/结束（从活跃表移除）→ 回调停止状态写入，不重建会话
    [Fact]
    public void UT13_A2A_CallbackAfterSessionGone_StopsWriting()
    {
        var manager = new A2ASessionManager(null, NewQueue(true), null, null);
        var session = new DialogueModels.A2ASession { SessionId = "s1", Generation = 1 };
        // 不注册：模拟会话已取消/结束

        InvokeA2ACallback(manager, "s1", 1, "Abigail", "台词", OutputDeliveryResult.Displayed);

        Assert.Empty(session.RecentSpokenLines);
        Assert.Equal(0, session.PendingOutputCount);
    }

    // 无效 A2A 输出经队列 → Rejected → 生产回调不写 RecentSpokenLines 与气泡桥
    [Fact]
    public void UT14_InvalidA2AOutput_Rejected_NoHistory()
    {
        using var bridgeScope = ScopedBridgeClock();
        using var playerScope = FakePlayer.Install("Farmer");

        var queue = NewQueue(validatorValid: false);
        var manager = new A2ASessionManager(null, queue, null, null);

        var session = new DialogueModels.A2ASession { SessionId = "s1", Generation = 7 };
        session.PendingOutputCount = 1;
        RegisterSession(manager, session);

        // 用生产回调（经反射取得同款私有方法）作为队列回调
        Action<OutputDeliveryResult> productionCallback = result =>
            A2ACallbackMethod.Invoke(manager, new object[] { "s1", 7, "Abigail", "无效台词", result });

        queue.EnqueueA2A("s1", 7, "Abigail", "无效台词", 3500, productionCallback);
        queue.Process(10);

        Assert.Empty(session.RecentSpokenLines);
        Assert.Equal(0, session.PendingOutputCount);
        Assert.Null(FreshBarkBridgeStore.TryConsume("Abigail"));
    }
}
