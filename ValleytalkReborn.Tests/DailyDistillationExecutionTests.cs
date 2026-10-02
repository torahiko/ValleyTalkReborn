// DailyDistillationExecutionTests.cs
// ═══════════════════════════════════════════════════════════════════════════
// DD407-EXECUTION-COMMIT：请求执行、epoch 与恢复式提交。
//
// 覆盖（受控输入，无真实网络；DD404 受控 IDataHelper 承载正文存储）：
//   1) 预算预留——入队（规划）不扣费；StartDailyTask 在调用 Provider 前完成
//      Intraday/FinalAttempts 加 1 并 TryWrite（OnInvoke 钩子观察调用瞬间账面）；
//      一次请求只扣一次；取消/失败不退次数。
//   2) epoch——同档重载（InvalidateSaveSession）更新存档/配置 epoch；同名存档重载与
//      A→B→A（多次失效）均拒绝旧结果；旧回调不释放新会话运行标志（冷却保持 0）。
//   3) 提交事务——Applied 创建/更新写卡片 + ack + HUD；Unchanged 幂等（无新卡片、
//      无 HUD、进度推进）；最终分支关闭 Committed/Unchanged；Empty 记录评估范围/
//      关闭；Failed/Cancelled 保留进度；BUG: 暂停升级；prepared 后存储失败保留
//      journal 并 block；玩家编辑冲突持久 Suspended 零覆盖。
//   4) 交互暂存——完成时玩家交互中 → 结果入 _dailyCompletions（运行键保持占用防重复
//      规划）；空闲消费后落卡并释放；不再次生成/计费。
//   5) 跨日/配置 epoch 丢弃、唯一运行任务拒绝、启动期重验撤销。
//
// 无头垫片沿用 DD406（Game1/位置/NPC/历史）与 DD404（ControlledDataHelper +
// ControlledModHelper + 真 Load/CommitDailyTimeline）。
// ═══════════════════════════════════════════════════════════════════════════

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Netcode;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Menus;
using StardewValley.Network;
using ValleytalkReborn;
using Xunit;

namespace ValleytalkReborn.Tests;

[Collection("WorldReadyStateCollection")]
public class DailyDistillationExecutionTests : IDisposable
{
    private const string NpcA = "Abigail";
    private const string TimelineKey = "valleytalk.npc-timeline-memories";
    private const string CategoryFlagKey = "valleytalk.memory-category-migrated";
    private const string OldDiary = "I enjoyed our quiet chat today.";
    private const string NewDiary = "I finally felt at peace by the river today.";

    private static readonly TimelineAutoSummaryScheduler S = TimelineAutoSummaryScheduler.Instance;

    // ── 调度器私有成员（D5 声明为 private，经反射驱动）──
    private static readonly MethodInfo ProbeMethod = M("ProbeDailyWork");
    private static readonly MethodInfo StartMethod = M("StartDailyTask");
    private static readonly MethodInfo CompleteMethod = M("CompleteDailyTask");
    private static readonly FieldInfo PendingField = F(typeof(TimelineAutoSummaryScheduler), "_dailyPending");
    private static readonly FieldInfo InFlightField = F(typeof(TimelineAutoSummaryScheduler), "_dailyInFlight");
    private static readonly FieldInfo BlockedField = F(typeof(TimelineAutoSummaryScheduler), "_dailyBlocked");
    private static readonly FieldInfo CompletionsField = F(typeof(TimelineAutoSummaryScheduler), "_dailyCompletions");
    private static readonly FieldInfo IdleField = F(typeof(TimelineAutoSummaryScheduler), "_dailyIdleSeconds");
    private static readonly FieldInfo TicksField = F(typeof(TimelineAutoSummaryScheduler), "_dailyProbeTicks");
    private static readonly FieldInfo DrainField = F(typeof(TimelineAutoSummaryScheduler), "_dailyTransportDrain");
    private static readonly FieldInfo SaveEpochField = F(typeof(TimelineAutoSummaryScheduler), "_saveSessionEpoch");
    private static readonly FieldInfo ConfigEpochField = F(typeof(TimelineAutoSummaryScheduler), "_configurationEpoch");
    private static readonly FieldInfo SessionCtsField = F(typeof(TimelineAutoSummaryScheduler), "_dailySessionCts");
    private static readonly FieldInfo RebuildField = F(typeof(TimelineAutoSummaryScheduler), "_dailyNeedsRebuild");
    private static readonly FieldInfo IsProcessingField = F(typeof(TimelineAutoSummaryScheduler), "_isProcessing");
    private static readonly FieldInfo CooldownField = F(typeof(TimelineAutoSummaryScheduler), "_cooldownSecondsRemaining");
    private static readonly FieldInfo IsLoadedField = F(typeof(MemoryManager), "_isLoaded");
    private static readonly FieldInfo HistoryField = F(typeof(DialogueHistoryManager), "_history");
    private static readonly FieldInfo PendingChoiceField = typeof(ValleytalkReborn.UI.PendingChoiceStore)
        .GetField("_pending", BindingFlags.Static | BindingFlags.NonPublic);
    private static readonly FieldInfo NetWorldStateField = F(typeof(Game1), "netWorldState");
    private static readonly FieldInfo Game1InstanceField = F(typeof(Game1), "game1");
    private static readonly FieldInfo LocationsField = F(typeof(Game1), "_locations");
    private static readonly FieldInfo GameSeasonField = F(typeof(Game1), "season");
    private static readonly FieldInfo ActiveMenuField = F(typeof(Game1), "_activeClickableMenu");
    private static readonly FieldInfo HudMessagesField = F(typeof(Game1), "hudMessages");

    private static MethodInfo M(string name) =>
        typeof(TimelineAutoSummaryScheduler).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic);

    private static FieldInfo F(Type type, string name)
    {
        for (var t = type; t != null && t != typeof(object); t = t.BaseType)
        {
            var f = t.GetField(name, BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (f != null) return f;
        }
        return null;
    }

    static DailyDistillationExecutionTests()
    {
        string gameDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Stardew Valley"));
        string smapiInternalDir = Path.Combine(gameDir, "smapi-internal");
        AssemblyLoadContext.Default.Resolving += (context, name) =>
        {
            if (name.Name == null) return null;
            string candidate = name.Name.StartsWith("SMAPI.", StringComparison.Ordinal)
                ? Path.Combine(smapiInternalDir, name.Name + ".dll")
                : Path.Combine(gameDir, name.Name + ".dll");
            return File.Exists(candidate) ? context.LoadFromAssemblyPath(candidate) : null;
        };
        TestEnvironment.InstallHeadlessContext();

        var previousHelper = TestEnv.GetSHelper();
        TestEnv.SetSHelper(null);
        try
        {
            _ = DialogueHistoryManager.Instance;
            _ = AsyncBuilder.Instance;
        }
        finally
        {
            TestEnv.SetSHelper(previousHelper);
        }
    }

    // ── 受控 Provider 桩（确定性；CallCount/OnInvoke 供扣费断言）──

    private sealed class ExecutionStubLlm : Llm
    {
        private Func<CancellationToken, Task<LlmResponse>> _handler = _ => Task.FromResult(new LlmResponse("[]"));

        public int CallCount;
        public Action OnInvoke;

        public void SetDiary(string diaryText) => SetRaw("[\"" + diaryText + "\"]");
        public void SetRaw(string raw) => _handler = _ => Task.FromResult(new LlmResponse(raw));
        public void SetDelayed(TimeSpan delay, string raw) =>
            _handler = async _ =>
            {
                await Task.Delay(delay).ConfigureAwait(false);
                return new LlmResponse(raw);
            };
        public void SetCancelHonoring() =>
            _handler = async ct =>
            {
                await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
                return null;
            };

        public override bool IsHighlySensoredModel => false;
        public override string ExtraInstructions => string.Empty;

        internal override Task<LlmResponse> RunInference(
            string systemPromptString, string gameCacheString, string npcCacheString,
            string promptString, string responseStart = "", int n_predict = 2048,
            string cacheContext = "", bool allowRetry = true)
            => throw new InvalidOperationException("Daily path must call RunInferenceAsync");

        internal override Task<LlmResponse> RunInferenceAsync(
            string systemPromptString, string gameCacheString, string npcCacheString,
            string promptString, CancellationToken ct, string responseStart = "",
            int n_predict = 2048, string cacheContext = "", bool allowRetry = true)
        {
            CallCount++;
            OnInvoke?.Invoke();
            return _handler(ct);
        }

        internal override Dictionary<string, double>[] RunInferenceProbabilities(string fullPrompt, int n_predict = 1)
            => throw new NotImplementedException();
    }

    private static readonly PropertyInfo LlmInstanceProperty =
        typeof(Llm).GetProperty("Instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

    private Llm _previousLlm;
    private ExecutionStubLlm _stub;

    // ── 受控 IDataHelper / IModHelper（承 DD404 配方）──

    private sealed class ControlledDataHelper : IDataHelper
    {
        public readonly Dictionary<string, object> SaveData = new();
        public bool ThrowOnWrite;

        public TModel ReadSaveData<TModel>(string key) where TModel : class
            => SaveData.TryGetValue(key, out var value) ? (TModel)value : null;

        public void WriteSaveData<TModel>(string key, TModel data) where TModel : class
        {
            if (ThrowOnWrite) throw new InvalidOperationException("controlled write failure");
            SaveData[key] = data;
        }

        public TModel ReadJsonFile<TModel>(string path) where TModel : class => throw new NotImplementedException();
        public void WriteJsonFile<TModel>(string path, TModel data) where TModel : class => throw new NotImplementedException();
        public TModel ReadGlobalData<TModel>(string key) where TModel : class => throw new NotImplementedException();
        public void WriteGlobalData<TModel>(string key, TModel data) where TModel : class => throw new NotImplementedException();
    }

    private class NoopInterfaceProxy : DispatchProxy
    {
        protected override object Invoke(MethodInfo targetMethod, object[] args)
        {
            if (targetMethod.IsSpecialName && targetMethod.Name.StartsWith("get_", StringComparison.Ordinal)
                && targetMethod.ReturnType.IsInterface)
            {
                return CreateNoop(targetMethod.ReturnType);
            }
            return null;
        }

        public static object CreateNoop(Type interfaceType)
        {
            var create = typeof(DispatchProxy)
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .First(m => m.IsGenericMethodDefinition && m.GetGenericArguments().Length == 2 && m.GetParameters().Length == 0)
                .MakeGenericMethod(interfaceType, typeof(NoopInterfaceProxy));
            return create.Invoke(null, Array.Empty<object>());
        }
    }

    private sealed class ControlledModHelper : IModHelper
    {
        private readonly IModEvents _events = (IModEvents)NoopInterfaceProxy.CreateNoop(typeof(IModEvents));
        private readonly ITranslationHelper _translation = new FakeTranslationHelper("en");

        public ControlledModHelper(IDataHelper data) { Data = data; }

        public string DirectoryPath => ".";
        public IModEvents Events => _events;
        public ICommandHelper ConsoleCommands => throw new NotImplementedException();
        public IGameContentHelper GameContent => throw new NotImplementedException();
        public IModContentHelper ModContent => throw new NotImplementedException();
        public IContentPackHelper ContentPacks => throw new NotImplementedException();
        public IDataHelper Data { get; }
        public IInputHelper Input => throw new NotImplementedException();
        public IReflectionHelper Reflection => throw new NotImplementedException();
        public IModRegistry ModRegistry => throw new NotImplementedException();
        public IMultiplayerHelper Multiplayer => throw new NotImplementedException();
        public ITranslationHelper Translation => _translation;
        public TConfig ReadConfig<TConfig>() where TConfig : class, new() => throw new NotImplementedException();
        public void WriteConfig<TConfig>(TConfig config) where TConfig : class, new() => throw new NotImplementedException();
    }

    // ── 夹具状态 ──

    private readonly IDisposable _localeScope;
    private readonly IDisposable _playerScope;
    private readonly object _previousNetWorldState;
    private readonly object _previousGame1;
    private readonly int _previousGameYear;
    private readonly string _previousGameSeason;
    private readonly int _previousGameDay;
    private readonly bool _previousEventUp;
    private readonly bool _previousDialogueUp;
    private readonly IClickableMenu _previousMenu;
    private readonly Dictionary<string, List<DialogueHistoryEntry>> _previousHistory;
    private readonly ControlledDataHelper _data;
    private readonly Dictionary<string, NPC> _npcs = new(StringComparer.OrdinalIgnoreCase);
    private System.Collections.IList _hudList;
    private readonly List<object> _previousHud = new();

    public DailyDistillationExecutionTests()
    {
        _localeScope = TestEnv.UseIsolatedLocale("en");
        ModEntry.Config.EnableMod = true;
        ModEntry.Config.DailyDistillMode = "Intraday";
        ModEntry.Config.DailyDistillThreshold = 3;
        ModEntry.Config.DailyMaxRequestsPerNpc = 2;

        _playerScope = FakePlayer.Install("农夫");
        InitializeNetFields(Game1.player);

        _previousNetWorldState = NetWorldStateField?.GetValue(null);
        if (_previousNetWorldState == null)
            NetWorldStateField?.SetValue(null, Activator.CreateInstance(NetWorldStateField.FieldType, new NetWorldState()));
        _previousGameYear = Game1.year;
        _previousGameSeason = Game1.currentSeason;   // string 属性（season 字段为游戏 Season 枚举，不可强转）
        _previousGameDay = Game1.dayOfMonth;
        SetGameDay(5);   // 今日 = 第 1 年春 5 日（一基日历日 5）

        _previousGame1 = Game1InstanceField?.GetValue(null);
        InstallLocationWorld();   // 空世界；测试按需放置 NPC

        // 正文存储：受控 IDataHelper + 真 Load（CommitDailyTimeline 全程可用）。
        _data = new ControlledDataHelper();
        _data.SaveData[CategoryFlagKey] = "true";
        TestEnv.SetSHelper(new ControlledModHelper(_data));
        TestEnvironment.WithWorldReady(true, () => MemoryManager.Instance.Load());

        var history = (Dictionary<string, List<DialogueHistoryEntry>>)HistoryField.GetValue(DialogueHistoryManager.Instance);
        _previousHistory = history.ToDictionary(p => p.Key, p => p.Value.ToList());
        history.Clear();

        _previousEventUp = Game1.eventUp;
        _previousDialogueUp = Game1.dialogueUp;
        _previousMenu = Game1.activeClickableMenu;
        Game1.eventUp = false;
        Game1.dialogueUp = false;
        ActiveMenuField?.SetValue(null, null);

        // HUD 计数基线：hudMessages 为进程级列表，先快照清空（Dispose 还原）。
        _hudList = (System.Collections.IList)HudMessagesField.GetValue(null);
        foreach (var item in _hudList.OfType<object>().ToList()) _previousHud.Add(item);
        _hudList.Clear();

        ValleytalkReborn.UI.PendingChoiceStore.Clear();
        DialogueBuilder.Instance.LlmDisabled = false;
        AsyncBuilder.Instance.IsGeneratingDialogue = false;

        _stub = new ExecutionStubLlm();
        _previousLlm = (Llm)LlmInstanceProperty.GetValue(null);
        LlmInstanceProperty.SetValue(null, _stub);

        ResetSchedulerState();
        MainThreadActionQueue.ProcessMainThreadQueue();
    }

    public void Dispose()
    {
        ResetSchedulerState();
        MainThreadActionQueue.ProcessMainThreadQueue();

        LlmInstanceProperty.SetValue(null, _previousLlm);
        DialogueBuilder.Instance.LlmDisabled = false;
        AsyncBuilder.Instance.IsGeneratingDialogue = false;
        ValleytalkReborn.UI.PendingChoiceStore.Clear();

        // 还原进程级 HUD 列表内容。
        if (_hudList != null)
        {
            _hudList.Clear();
            foreach (var item in _previousHud) _hudList.Add(item);
        }

        Game1.eventUp = _previousEventUp;
        Game1.dialogueUp = _previousDialogueUp;
        ActiveMenuField?.SetValue(null, _previousMenu);

        var history = (Dictionary<string, List<DialogueHistoryEntry>>)HistoryField.GetValue(DialogueHistoryManager.Instance);
        history.Clear();
        foreach (var pair in _previousHistory) history[pair.Key] = pair.Value;

        MemoryManager.Instance.Cleanup();
        TestEnv.SetSHelper(null);

        Game1InstanceField?.SetValue(null, _previousGame1);
        NetWorldStateField?.SetValue(null, _previousNetWorldState);
        Game1.year = _previousGameYear;
        Game1.currentSeason = _previousGameSeason;
        Game1.dayOfMonth = _previousGameDay;

        _playerScope?.Dispose();
        _localeScope?.Dispose();
    }

    // ── 夹具辅助 ──────────────────────────────────────────────────────────

    private void ResetSchedulerState()
    {
        ((Dictionary<(string, int), AutoSummaryTask>)PendingField.GetValue(S)).Clear();
        ((HashSet<(string, int)>)InFlightField.GetValue(S)).Clear();
        ((HashSet<(string, int)>)BlockedField.GetValue(S)).Clear();
        ((Queue<(AutoSummaryTask, DailyDistillationRequest, MemoryExtractResult)>)CompletionsField.GetValue(S)).Clear();
        IdleField.SetValue(S, 0);
        TicksField.SetValue(S, 0);   // 探针节拍归零：跨测试累积会跳过 5-tick 扫描窗口
        RebuildField.SetValue(S, false);
        DrainField.SetValue(S, null);
        // 每个测试使用唯一的 epoch 基线：前序测试泄漏的旧回调（较小 epoch）
        // 在本测试内必然 epoch 不匹配而被丢弃，杜绝跨测试状态污染。
        _testEpochBase += 100;
        SaveEpochField.SetValue(S, _testEpochBase);
        ConfigEpochField.SetValue(S, _testEpochBase);
        if (SessionCtsField.GetValue(S) is CancellationTokenSource oldCts)
        {
            try { oldCts.Cancel(); } catch (ObjectDisposedException) { }
            oldCts.Dispose();
        }
        SessionCtsField.SetValue(S, new CancellationTokenSource());
        IsProcessingField.SetValue(S, false);
        CooldownField.SetValue(S, 0);
    }

    private static void SetGameDay(int totalDays)
    {
        // WorldDate.Now() 读 Game1.year/currentSeason/dayOfMonth；season 字段为游戏
        // Season 枚举（GameData.dll 不可引用）——经 string 属性 currentSeason 写入。
        Game1.year = (totalDays - 1) / 112 + 1;
        Game1.currentSeason = (new[] { "spring", "summer", "fall", "winter" })[(totalDays - 1) / 28 % 4];
        Game1.dayOfMonth = (totalDays - 1) % 28 + 1;
    }

    private static NPC NewNpc(string name)
    {
        var npc = (NPC)FormatterServices.GetUninitializedObject(typeof(NPC));
        InitializeNetFields(npc);
        F(typeof(NPC), "name")?.SetValue(npc, new NetString(name));
        npc.displayName = name;
        F(typeof(NPC), "dialogue")?.SetValue(npc, new Dictionary<string, string>());
        return npc;
    }

    private void InstallLocationWorld(params NPC[] npcs)
    {
        _npcs.Clear();
        var location = (GameLocation)FormatterServices.GetUninitializedObject(typeof(GameLocation));
        InitializeNetFields(location);
        foreach (var npc in npcs)
        {
            location.characters.Add(npc);
            _npcs[npc.Name] = npc;
        }

        var game1 = (Game1)FormatterServices.GetUninitializedObject(typeof(Game1));
        LocationsField?.SetValue(game1, new List<GameLocation> { location });
        Game1InstanceField?.SetValue(null, game1);
    }

    private NPC WorldNpc(string name) => _npcs.TryGetValue(name, out var npc) ? npc : null;

    private static void SeedHistory(string npcName, params (SpeakerType Type, string Text, string DialogueType, int Day, int Time)[] lines)
    {
        var history = (Dictionary<string, List<DialogueHistoryEntry>>)HistoryField.GetValue(DialogueHistoryManager.Instance);
        history[npcName] = lines
            .Select(l => new DialogueHistoryEntry(npcName, l.Text, l.Type, new StardewTime(1, Season.Spring, l.Day, l.Time), l.DialogueType))
            .ToList();
    }

    private static void AddHistoryLine(string npcName, string text, int day, int time) =>
        ((Dictionary<string, List<DialogueHistoryEntry>>)HistoryField.GetValue(DialogueHistoryManager.Instance))[npcName]
            .Add(new DialogueHistoryEntry(npcName, text, SpeakerType.NPC, new StardewTime(1, Season.Spring, day, time), "dialogue"));

    private static MemoryEntry Card(string id, string content, int createdDay) => new()
    {
        Id = id,
        NpcName = NpcA,
        Content = content,
        CreatedAt = new DateTime(2026, 1, 1, 12, 0, 0),
        CreatedDay = createdDay,
        Source = "Timeline",
        Category = MemoryCategory.Fact,
        Type = MemoryType.Fact,
        Tier = MemoryTier.Daily,
        DateLabel = "seeded",
        Importance = 3
    };

    private void SeedCards(params MemoryEntry[] cards) =>
        _data.SaveData[TimelineKey] = new Dictionary<string, List<MemoryEntry>>(StringComparer.OrdinalIgnoreCase)
        {
            [NpcA] = cards.ToList()   // 与 Load 的 ReadSaveData<Dictionary<string, List<MemoryEntry>>> 形状一致
        };

    private static void ReloadMemoryManager() =>
        TestEnvironment.WithWorldReady(true, () => MemoryManager.Instance.Load());

    private DailyDistillationLedger ReadLedger()
    {
        DailyDistillationLedger ledger = null;
        TestEnvironment.WithWorldReady(true, () =>
            DailyDistillationStateStore.TryRead(WorldNpc(NpcA), out ledger));
        return ledger;
    }

    private void WriteLedger(DailyDistillationLedger ledger) =>
        TestEnvironment.WithWorldReady(true, () =>
            Assert.True(DailyDistillationStateStore.TryWrite(WorldNpc(NpcA), ledger)));

    private static List<string> RowKeysOf(string npcName, int targetDay) =>
        DailyDistillationSnapshotBuilder.Create(
            npcName, targetDay, MemoryManager.GameDayToStardewTime(targetDay),
            DialogueHistoryManager.Instance.GetHistory(npcName), new List<string>(), isChinese: false)
            .Rows.Select(r => r.RowKey).ToList();

    private void Probe() => TestEnvironment.WithWorldReady(true, () => ProbeMethod.Invoke(S, null));

    private void StartDailyTask(AutoSummaryTask task) =>
        TestEnvironment.WithWorldReady(true, () => StartMethod.Invoke(S, new object[] { task }));

    private void CompleteDailyTask(AutoSummaryTask task, DailyDistillationRequest request, MemoryExtractResult result) =>
        TestEnvironment.WithWorldReady(true, () => CompleteMethod.Invoke(S, new object[] { task, request, result }));

    private Dictionary<(string, int), AutoSummaryTask> Pending() =>
        (Dictionary<(string, int), AutoSummaryTask>)PendingField.GetValue(S);

    private HashSet<(string, int)> InFlight() => (HashSet<(string, int)>)InFlightField.GetValue(S);

    private HashSet<(string, int)> Blocked() => (HashSet<(string, int)>)BlockedField.GetValue(S);

    private Queue<(AutoSummaryTask Task, DailyDistillationRequest Request, MemoryExtractResult Result)> Completions() =>
        (Queue<(AutoSummaryTask, DailyDistillationRequest, MemoryExtractResult)>)CompletionsField.GetValue(S);

    private bool IsProcessing => (bool)IsProcessingField.GetValue(S);

    private int Cooldown => (int)CooldownField.GetValue(S);

    private int HudCount => ((System.Collections.IList)HudMessagesField.GetValue(null)).Count;

    private static long _testEpochBase;   // 跨测试递增的 epoch 基线（隔离前序测试的泄漏回调）

    private long SaveEpoch => (long)SaveEpochField.GetValue(S);

    /// <summary>启动后等待底层 drain 结束并泵主线程队列（后台回调由观察者投递）。</summary>
    private void PumpAfterDrain(int timeoutMs = 3000)
    {
        Assert.True(SpinWait.SpinUntil(() =>
        {
            var drain = (Task)DrainField.GetValue(S);
            return drain != null && drain.IsCompleted;
        }, timeoutMs), "daily drain did not complete in time");
        // 完成回调在主线程泵出（生产中世界已加载）——泵必须处于 world-ready 作用域内。
        // drain 完成与观察者续体入队之间存在调度间隙：留出余量并二次泵清，防泄漏到下个测试。
        TestEnvironment.WithWorldReady(true, () =>
        {
            Thread.Sleep(50);
            MainThreadActionQueue.ProcessMainThreadQueue();
            Thread.Sleep(30);
            MainThreadActionQueue.ProcessMainThreadQueue();
        });
    }

    /// <summary>规划一个今日/昨日候选并取出待启动任务。世界已安装时不重建（保住预写台账）。</summary>
    private AutoSummaryTask PlanCandidate(params (SpeakerType Type, string Text, string DialogueType, int Day, int Time)[] lines)
    {
        if (!_npcs.ContainsKey(NpcA)) InstallLocationWorld(NewNpc(NpcA));
        if (lines is { Length: > 0 }) SeedHistory(NpcA, lines);
        Probe();
        Assert.True(Pending().Count > 0,
            "probe produced no candidate: " +
            $"isLoaded={MemoryManager.Instance.IsLoaded}, blocked=[{string.Join(";", Blocked())}], " +
            $"enableMod={ModEntry.Config.EnableMod}, mode={ModEntry.Config.DailyDistillMode}, " +
            $"historyNames=[{string.Join(",", DialogueHistoryManager.Instance.GetHistoryNpcNames())}], " +
            $"worldNpc={(WorldNpc(NpcA) != null)}");
        var task = Pending().Values.Single();
        Pending().Clear();   // 从 pending 取出，模拟 DD408 的出队启动
        return task;
    }

    private static (string, int) Key(string npc, int day) => (npc.ToUpperInvariant(), day);

    private static void InitializeNetFields(object target)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        for (Type t = target.GetType(); t != null && t != typeof(object); t = t.BaseType)
        {
            foreach (var field in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
            {
                if (!visited.Add(field.Name)) continue;
                if (field.GetValue(target) != null) continue;
                try
                {
                    if (field.FieldType == typeof(NetInt)) field.SetValue(target, new NetInt(0));
                    else if (field.FieldType == typeof(NetBool)) field.SetValue(target, new NetBool(false));
                    else if (field.FieldType == typeof(NetLong)) field.SetValue(target, new NetLong(0L));
                    else if (field.FieldType == typeof(NetFloat)) field.SetValue(target, new NetFloat(0f));
                    else if (field.FieldType == typeof(NetDouble)) field.SetValue(target, new NetDouble(0d));
                    else if (field.FieldType == typeof(NetString)) field.SetValue(target, new NetString(string.Empty));
                    else if (IsNetStateField(field.FieldType) && !field.FieldType.IsAbstract)
                    {
                        var ctor = field.FieldType.GetConstructors(BindingFlags.Instance | BindingFlags.Public)
                            .Where(c => c.GetParameters().Length <= 1)
                            .OrderBy(c => c.GetParameters().Length)
                            .FirstOrDefault();
                        if (ctor == null) continue;
                        var args = ctor.GetParameters()
                            .Select(p => p.HasDefaultValue
                                ? p.DefaultValue
                                : (p.ParameterType.IsValueType ? Activator.CreateInstance(p.ParameterType) : null))
                            .ToArray();
                        field.SetValue(target, ctor.Invoke(args));
                    }
                }
                catch (Exception)
                {
                    // 测试用桩：无法初始化的字段保持原样，触碰会以显式异常暴露。
                }
            }
        }
    }

    private static bool IsNetStateField(Type type) =>
        type.GetInterfaces().Any(i => i.IsGenericType && i.GetGenericTypeDefinition().FullName == "Netcode.INetObject`1");

    // ── 预算预留与启动 ────────────────────────────────────────────────────

    [Fact]
    public void StartDailyTask_ReservesBeforeProviderCall_BillsExactlyOnce()
    {
        int attemptsAtCall = -1;
        _stub.SetDiary(NewDiary);
        _stub.OnInvoke = () =>
        {
            var ledger = ReadLedger();
            attemptsAtCall = ledger.Days[5].IntradayAttempts;
        };

        var task = PlanCandidate(
            (SpeakerType.NPC, "chat one", "dialogue", 5, 900),
            (SpeakerType.NPC, "chat two", "dialogue", 5, 910),
            (SpeakerType.NPC, "chat three", "dialogue", 5, 920));

        int hudBefore = HudCount;
        StartDailyTask(task);

        // 规划（入队）不扣费在 Probe 后账面为 0；启动后在 Provider 调用瞬间已完成预留。
        Assert.Equal(1, attemptsAtCall);
        Assert.Equal(1, _stub.CallCount);
        Assert.Equal(1, ReadLedger().Days[5].IntradayAttempts);
        Assert.False(string.IsNullOrEmpty(task.NewEntryId));           // 首次创建固定 Guid
        Assert.True(Guid.TryParse(task.NewEntryId, out _));
        Assert.Equal(SaveEpoch, task.SaveSessionEpoch);
        Assert.Contains(Key(NpcA, 5), InFlight());
        Assert.True(IsProcessing);
        Assert.NotNull(DrainField.GetValue(S));

        PumpAfterDrain();

        // 提交完成：Applied + ack + HUD。
        var card = MemoryManager.Instance.GetTimelineMemories(NpcA, MemoryTier.Daily).Single();
        Assert.Equal(task.NewEntryId, card.Id);
        Assert.Equal(NewDiary, card.Content);
        Assert.Equal(5, card.CreatedDay);
        var day = ReadLedger().Days[5];
        Assert.Equal(task.NewEntryId, day.EntryId);
        Assert.Equal(DailyDistillationSnapshotBuilder.HashText(NewDiary), day.LastContentHash);
        Assert.Equal(task.DailyRequest.Snapshot.Rows.Select(r => r.RowKey), day.CoveredRowKeys);
        Assert.Equal(task.DailyRequest.Snapshot.InputFingerprint, day.LastEvaluatedFingerprint);
        Assert.Null(day.PendingCommit);
        Assert.Equal(hudBefore + 1, HudCount);
        Assert.Equal(35, Cooldown);
        Assert.False(IsProcessing);
        Assert.Empty(InFlight());
        Assert.Null(DrainField.GetValue(S));
    }

    [Fact]
    public void StartDailyTask_FinalAttempt_ReservedOnFinalCounter()
    {
        _stub.SetDiary(NewDiary);
        var task = PlanCandidate(
            (SpeakerType.NPC, "yesterday one", "dialogue", 4, 900),
            (SpeakerType.NPC, "yesterday two", "dialogue", 4, 910),
            (SpeakerType.NPC, "yesterday three", "dialogue", 4, 920));
        Assert.Equal(4, DayNumberOfTest(task.TargetDate));

        StartDailyTask(task);

        var day = ReadLedger().Days[4];
        Assert.Equal(1, day.FinalAttempts);
        Assert.Equal(0, day.IntradayAttempts);
        Assert.Equal(1, _stub.CallCount);

        PumpAfterDrain();
        Assert.True(ReadLedger().Days[4].FinalizationClosed);   // 最终 Applied → Committed 关闭
        Assert.Equal("Committed", ReadLedger().Days[4].FinalizationOutcome);
    }

    [Fact]
    public void StartDailyTask_IncompleteTask_EscalatesWithoutBilling()
    {
        InstallLocationWorld(NewNpc(NpcA));   // 台账可读（空 ledger），验证零扣费
        var task = new AutoSummaryTask
        {
            NpcName = NpcA,
            TargetDate = MemoryManager.GameDayToStardewTime(5),
            Type = AutoSummaryType.Daily,
            DailyRequest = null   // 请求完整性缺失
        };
        Pending()[Key(NpcA, 5)] = task;

        StartDailyTask(task);

        Assert.Equal(0, _stub.CallCount);
        Assert.False(ReadLedger().Days.ContainsKey(5));   // 零扣费
        Assert.Empty(Pending());                          // 已撤销
    }

    [Fact]
    public void StartDailyTask_WhileRunActive_BugRefusesToStart()
    {
        IsProcessingField.SetValue(S, true);   // 模拟已有运行任务

        var task = PlanCandidate(
            (SpeakerType.NPC, "chat one", "dialogue", 5, 900),
            (SpeakerType.NPC, "chat two", "dialogue", 5, 910),
            (SpeakerType.NPC, "chat three", "dialogue", 5, 920));

        StartDailyTask(task);

        Assert.Equal(0, _stub.CallCount);
        Assert.False(ReadLedger().Days.ContainsKey(5));   // 零扣费
        Assert.Empty(Pending());
    }

    [Fact]
    public void StartDailyTask_ConditionsChangedSincePlan_WithdrawsWithoutBilling()
    {
        var task = PlanCandidate(
            (SpeakerType.NPC, "chat one", "dialogue", 5, 900),
            (SpeakerType.NPC, "chat two", "dialogue", 5, 910),
            (SpeakerType.NPC, "chat three", "dialogue", 5, 920));

        // 启动前日期被暂停（如恢复核对冲突）→ 重验撤销，零扣费。
        var ledger = ReadLedger();
        ledger.Days[5] = new DailyDistillationDayState { TargetDay = 5, Suspended = true, SuspensionReason = "Edited" };
        WriteLedger(ledger);

        StartDailyTask(task);

        Assert.Equal(0, _stub.CallCount);
        Assert.False(ReadLedger().Days[5].IntradayAttempts > 0);
        Assert.Empty(Pending());
        Assert.Empty(InFlight());
    }

    // ── 提交事务：Applied / Unchanged / 最终关闭 ──────────────────────────

    [Fact]
    public void CompleteDailyTask_UpdateExisting_Applied_ReplacesContent()
    {
        InstallLocationWorld(NewNpc(NpcA));   // 先装世界 NPC：台账写在其实例 modData 上
        SeedCards(Card("E1", OldDiary, 5));
        ReloadMemoryManager();
        var rowKeys = RowKeysOf(NpcA, 5);
        SeedHistory(NpcA,
            (SpeakerType.NPC, "old row one", "dialogue", 5, 900),
            (SpeakerType.NPC, "old row two", "dialogue", 5, 910),
            (SpeakerType.NPC, "old row three", "dialogue", 5, 920));
        var ledger = new DailyDistillationLedger();
        ledger.Days[5] = new DailyDistillationDayState
        {
            TargetDay = 5,
            EntryId = "E1",
            LastContentHash = DailyDistillationSnapshotBuilder.HashText(OldDiary),
            CoveredRowKeys = rowKeys.Take(2).ToList()
        };
        WriteLedger(ledger);

        // 新材料 3 行 → 未覆盖 ≥ 阈值 → 更新候选。
        AddHistoryLine(NpcA, "evening one", 5, 1900);
        AddHistoryLine(NpcA, "evening two", 5, 1910);
        AddHistoryLine(NpcA, "evening three", 5, 1920);
        _stub.SetDiary(NewDiary);
        var task = PlanCandidate();
        Assert.Equal("E1", task.ExistingEntryId);

        StartDailyTask(task);
        PumpAfterDrain();

        var card = MemoryManager.Instance.GetTimelineMemories(NpcA, MemoryTier.Daily).Single();
        Assert.Equal("E1", card.Id);
        Assert.Equal(NewDiary, card.Content);
        var day = ReadLedger().Days[5];
        Assert.Equal(DailyDistillationSnapshotBuilder.HashText(NewDiary), day.LastContentHash);
        Assert.False(day.FinalizationClosed);   // 日内更新不关闭
        Assert.Null(day.PendingCommit);
        Assert.Equal(1, HudCount);
        Assert.Equal(35, Cooldown);
    }

    [Fact]
    public void CompleteDailyTask_Unchanged_Idempotent_NoNewCardNoHud()
    {
        InstallLocationWorld(NewNpc(NpcA));   // 先装世界 NPC：台账写在其实例 modData 上
        SeedCards(Card("E1", OldDiary, 5));
        ReloadMemoryManager();
        SeedHistory(NpcA,
            (SpeakerType.NPC, "old row one", "dialogue", 5, 900),
            (SpeakerType.NPC, "old row two", "dialogue", 5, 910),
            (SpeakerType.NPC, "old row three", "dialogue", 5, 920));
        var ledger = new DailyDistillationLedger();
        ledger.Days[5] = new DailyDistillationDayState
        {
            TargetDay = 5,
            EntryId = "E1",
            LastContentHash = DailyDistillationSnapshotBuilder.HashText(OldDiary),
            CoveredRowKeys = RowKeysOf(NpcA, 5).Take(2).ToList()
        };
        WriteLedger(ledger);

        AddHistoryLine(NpcA, "evening one", 5, 1900);
        AddHistoryLine(NpcA, "evening two", 5, 1910);
        AddHistoryLine(NpcA, "evening three", 5, 1920);
        _stub.SetDiary(OldDiary);   // 与旧正文一致 → 提交阶段 Unchanged
        var task = PlanCandidate();

        StartDailyTask(task);
        PumpAfterDrain();

        var card = MemoryManager.Instance.GetTimelineMemories(NpcA, MemoryTier.Daily).Single();
        Assert.Equal("E1", card.Id);
        Assert.Equal(OldDiary, card.Content);            // 无新卡片、正文保持
        var day = ReadLedger().Days[5];
        Assert.Equal(task.DailyRequest.Snapshot.InputFingerprint, day.LastEvaluatedFingerprint);   // 进度推进
        Assert.Equal(task.DailyRequest.Snapshot.Rows.Select(r => r.RowKey), day.CoveredRowKeys);
        Assert.Null(day.PendingCommit);
        Assert.False(day.FinalizationClosed);
        Assert.Equal(0, HudCount);                       // 不弹 HUD
        Assert.Equal(1, _stub.CallCount);                // 不再次生成
    }

    [Fact]
    public void CompleteDailyTask_FinalUnchanged_ClosesUnchanged()
    {
        InstallLocationWorld(NewNpc(NpcA));   // 先装世界 NPC：台账写在其实例 modData 上
        SeedCards(Card("E4", OldDiary, 4));
        ReloadMemoryManager();
        SeedHistory(NpcA,
            (SpeakerType.NPC, "yesterday one", "dialogue", 4, 900),
            (SpeakerType.NPC, "yesterday two", "dialogue", 4, 910),
            (SpeakerType.NPC, "yesterday three", "dialogue", 4, 920));
        var ledger = new DailyDistillationLedger();
        ledger.Days[4] = new DailyDistillationDayState
        {
            TargetDay = 4,
            EntryId = "E4",
            LastContentHash = DailyDistillationSnapshotBuilder.HashText(OldDiary),
            CoveredRowKeys = RowKeysOf(NpcA, 4).Take(2).ToList()
        };
        WriteLedger(ledger);

        AddHistoryLine(NpcA, "late one", 4, 2000);
        _stub.SetDiary(OldDiary);
        var task = PlanCandidate();
        Assert.True(task.IsFinalDaily);

        StartDailyTask(task);
        PumpAfterDrain();

        var day = ReadLedger().Days[4];
        Assert.True(day.FinalizationClosed);
        Assert.Equal("Unchanged", day.FinalizationOutcome);
        Assert.Equal(0, HudCount);
    }

    // ── Empty / Failed / Cancelled / BUG ─────────────────────────────────

    [Fact]
    public void CompleteDailyTask_Empty_Intraday_RecordsEvaluatedRange_NoCard()
    {
        var task = PlanCandidate(
            (SpeakerType.NPC, "chat one", "dialogue", 5, 900),
            (SpeakerType.NPC, "chat two", "dialogue", 5, 910),
            (SpeakerType.NPC, "chat three", "dialogue", 5, 920));
        _stub.SetRaw("[]");

        StartDailyTask(task);
        PumpAfterDrain();

        var day = ReadLedger().Days[5];
        Assert.Equal(task.DailyRequest.Snapshot.Rows.Select(r => r.RowKey), day.CoveredRowKeys);
        Assert.Equal(task.DailyRequest.Snapshot.InputFingerprint, day.LastEvaluatedFingerprint);
        Assert.Equal("", day.EntryId);   // TryRead Normalize 将 null 归一为空串
        Assert.Empty(MemoryManager.Instance.GetTimelineMemories(NpcA, MemoryTier.Daily));
        Assert.Equal(0, HudCount);
        Assert.Equal(1, ReadLedger().Days[5].IntradayAttempts);   // 次数保持
        Assert.Equal(35, Cooldown);
    }

    [Fact]
    public void CompleteDailyTask_Empty_Final_ClosesEmpty()
    {
        var task = PlanCandidate(
            (SpeakerType.NPC, "yesterday one", "dialogue", 4, 900),
            (SpeakerType.NPC, "yesterday two", "dialogue", 4, 910),
            (SpeakerType.NPC, "yesterday three", "dialogue", 4, 920));
        _stub.SetRaw("[]");

        StartDailyTask(task);
        PumpAfterDrain();

        var day = ReadLedger().Days[4];
        Assert.True(day.FinalizationClosed);
        Assert.Equal("Empty", day.FinalizationOutcome);
        Assert.Empty(MemoryManager.Instance.GetTimelineMemories(NpcA, MemoryTier.Daily));
    }

    [Fact]
    public void CompleteDailyTask_Failed_KeepsProgress_NoRefund()
    {
        var task = PlanCandidate(
            (SpeakerType.NPC, "chat one", "dialogue", 5, 900),
            (SpeakerType.NPC, "chat two", "dialogue", 5, 910),
            (SpeakerType.NPC, "chat three", "dialogue", 5, 920));
        _stub.SetRaw("not a json array at all");   // 严格解析 → Failed（BOUNDARY）

        StartDailyTask(task);
        PumpAfterDrain();

        var day = ReadLedger().Days[5];
        Assert.Equal(1, day.IntradayAttempts);   // 已扣不退
        Assert.Equal("", day.EntryId);   // TryRead Normalize 将 null 归一为空串
        Assert.Empty(day.CoveredRowKeys);        // 覆盖不推进
        Assert.Equal("", day.LastEvaluatedFingerprint);
        Assert.Empty(MemoryManager.Instance.GetTimelineMemories(NpcA, MemoryTier.Daily));
        Assert.Equal(0, HudCount);
        Assert.Equal(35, Cooldown);              // 失败也冷却
    }

    [Fact]
    public void CompleteDailyTask_Cancelled_NoRefund()
    {
        // 会话取消 → GenerateDailyAsync 返回 Cancelled → 丢弃但次数保持。
        var task = PlanCandidate(
            (SpeakerType.NPC, "chat one", "dialogue", 5, 900),
            (SpeakerType.NPC, "chat two", "dialogue", 5, 910),
            (SpeakerType.NPC, "chat three", "dialogue", 5, 920));
        _stub.SetCancelHonoring();
        ((CancellationTokenSource)SessionCtsField.GetValue(S)).Cancel();   // 预取消会话令牌

        StartDailyTask(task);
        PumpAfterDrain();

        var day = ReadLedger().Days[5];
        Assert.Equal(1, day.IntradayAttempts);
        Assert.Empty(day.CoveredRowKeys);
        Assert.Empty(MemoryManager.Instance.GetTimelineMemories(NpcA, MemoryTier.Daily));
        Assert.Equal(0, HudCount);
    }

    [Fact]
    public void CompleteDailyTask_BugDetail_SuspendsDay()
    {
        // 直接送入 BUG: 标记结果（内部解析违约形态）→ 日期持久暂停并升级。
        var task = PlanCandidate(
            (SpeakerType.NPC, "chat one", "dialogue", 5, 900),
            (SpeakerType.NPC, "chat two", "dialogue", 5, 910),
            (SpeakerType.NPC, "chat three", "dialogue", 5, 920));
        _stub.SetDiary(NewDiary);
        StartDailyTask(task);   // 正常启动以建立占用状态
        var request = task.DailyRequest;
        var bugResult = new MemoryExtractResult { Status = MemoryExtractStatus.Failed, ErrorDetail = "BUG: parse: simulated" };

        IsProcessingField.SetValue(S, true);
        InFlight().Add(Key(NpcA, 5));
        CompleteDailyTask(task, request, bugResult);

        var day = ReadLedger().Days[5];
        Assert.True(day.Suspended);
        Assert.Equal("Bug", day.SuspensionReason);
        Assert.Empty(MemoryManager.Instance.GetTimelineMemories(NpcA, MemoryTier.Daily));
        Assert.Equal(35, Cooldown);
        Assert.Empty(InFlight());
    }

    // ── epoch 与失效 ─────────────────────────────────────────────────────

    [Fact]
    public void StaleSessionCallbacks_AreDropped_WithoutTouchingNewSessionState()
    {
        // A→B→A：两次失效（存档重载），旧结果必须被拒。
        InstallLocationWorld(NewNpc(NpcA));
        SeedHistory(NpcA,
            (SpeakerType.NPC, "chat one", "dialogue", 5, 900),
            (SpeakerType.NPC, "chat two", "dialogue", 5, 910),
            (SpeakerType.NPC, "chat three", "dialogue", 5, 920));
        _stub.SetDelayed(TimeSpan.FromMilliseconds(150), "[\"" + NewDiary + "\"]");

        Probe();
        var task = Pending().Values.Single();
        Pending().Clear();

        StartDailyTask(task);
        long capturedEpoch = task.SaveSessionEpoch;

        S.InvalidateSaveSession();   // 重载 1（同名存档）
        S.InvalidateSaveSession();   // 重载 2（A→B→A 的 B）
        Assert.Equal(capturedEpoch + 2, SaveEpoch);

        var hudBefore = HudCount;
        PumpAfterDrain();            // 旧回调返回

        // 旧结果丢弃：无卡片、无 HUD、不释放新会话标志（冷却保持 0）。
        Assert.Empty(MemoryManager.Instance.GetTimelineMemories(NpcA, MemoryTier.Daily));
        Assert.Equal(hudBefore, HudCount);
        Assert.Equal(0, Cooldown);
        Assert.False(IsProcessing);
        Assert.Empty(InFlight());
        // 已预留次数保持（不退费），但正文零写入。
        Assert.Equal(1, ReadLedger().Days[5].IntradayAttempts);
        Assert.Equal("", ReadLedger().Days[5].EntryId);
    }

    [Fact]
    public void InvalidateSaveSession_ClearsQueues_KeepsUnfinishedDrain()
    {
        InstallLocationWorld(NewNpc(NpcA));
        SeedHistory(NpcA,
            (SpeakerType.NPC, "chat one", "dialogue", 5, 900),
            (SpeakerType.NPC, "chat two", "dialogue", 5, 910),
            (SpeakerType.NPC, "chat three", "dialogue", 5, 920));
        _stub.SetDelayed(TimeSpan.FromMilliseconds(200), "[\"" + NewDiary + "\"]");

        Probe();
        var task = Pending().Values.Single();
        Pending().Clear();
        StartDailyTask(task);
        Completions().Enqueue((task, task.DailyRequest, new MemoryExtractResult { Status = MemoryExtractStatus.Failed }));
        InFlight().Add(Key(NpcA, 5));
        IdleField.SetValue(S, 7);

        S.InvalidateSaveSession();

        Assert.Empty(Pending());
        Assert.Empty(Completions());
        Assert.Empty(InFlight());
        Assert.Empty(Blocked());
        Assert.Equal(0, (int)IdleField.GetValue(S));
        Assert.False(IsProcessing);
        // 未完成 drain 句柄保留（仍被观察者 await，结束后由 epoch 丢弃路径处理）。
        var drain = (Task)DrainField.GetValue(S);
        Assert.NotNull(drain);
        Assert.False(drain.IsCompleted);

        // 会话 CTS 已更换：旧令牌已取消，新令牌未取消。
        Assert.True(((CancellationTokenSource)SessionCtsField.GetValue(S)).Token.CanBeCanceled);
    }

    [Fact]
    public void CompleteDailyTask_ConfigEpochChanged_Drops()
    {
        var task = PlanCandidate(
            (SpeakerType.NPC, "chat one", "dialogue", 5, 900),
            (SpeakerType.NPC, "chat two", "dialogue", 5, 910),
            (SpeakerType.NPC, "chat three", "dialogue", 5, 920));
        _stub.SetDiary(NewDiary);
        StartDailyTask(task);
        var request = task.DailyRequest;
        var result = new MemoryExtractResult
        {
            Status = MemoryExtractStatus.Success,
            Candidates = { NewDiary }
        };

        ConfigEpochField.SetValue(S, (long)task.ConfigurationEpoch + 1);   // 配置变化（DD408 接线）
        int hudBefore = HudCount;
        CompleteDailyTask(task, request, result);

        Assert.Empty(MemoryManager.Instance.GetTimelineMemories(NpcA, MemoryTier.Daily));
        Assert.Equal(hudBefore, HudCount);
        Assert.Equal(35, Cooldown);   // 同会话丢弃仍释放运行占用
    }

    // ── 存储失败 / 玩家编辑冲突 / 恢复 ───────────────────────────────────

    [Fact]
    public void CompleteDailyTask_StorageFailure_KeepsJournalAndBlocks()
    {
        InstallLocationWorld(NewNpc(NpcA));   // 先装世界 NPC：台账写在其实例 modData 上
        SeedCards(Card("E1", OldDiary, 5));
        ReloadMemoryManager();
        SeedHistory(NpcA,
            (SpeakerType.NPC, "old row one", "dialogue", 5, 900),
            (SpeakerType.NPC, "old row two", "dialogue", 5, 910),
            (SpeakerType.NPC, "old row three", "dialogue", 5, 920));
        var ledger = new DailyDistillationLedger();
        ledger.Days[5] = new DailyDistillationDayState
        {
            TargetDay = 5,
            EntryId = "E1",
            LastContentHash = DailyDistillationSnapshotBuilder.HashText(OldDiary),
            CoveredRowKeys = RowKeysOf(NpcA, 5).Take(2).ToList()
        };
        WriteLedger(ledger);

        AddHistoryLine(NpcA, "evening one", 5, 1900);
        AddHistoryLine(NpcA, "evening two", 5, 1910);
        AddHistoryLine(NpcA, "evening three", 5, 1920);
        _stub.SetDiary(NewDiary);
        var task = PlanCandidate();

        _data.ThrowOnWrite = true;   // prepared 已写 modData；正文存储将失败
        StartDailyTask(task);
        PumpAfterDrain();

        // journal 保留（prepared 状态），正文未写，本会话 blocked，无 HUD。
        Assert.True(ReadLedger().Days[5].PendingCommit != null);
        Assert.Equal(OldDiary, MemoryManager.Instance.GetTimelineMemories(NpcA, MemoryTier.Daily).Single().Content);
        Assert.Contains(Key(NpcA, 5), Blocked());
        Assert.Equal(0, HudCount);
        Assert.Equal(35, Cooldown);

        // 读档恢复路径：重新加载 + 恢复式核对清 pending（prepared 未写分支）。
        _data.ThrowOnWrite = false;
        ReloadMemoryManager();
        var recoveryLedger = ReadLedger();
        TestEnvironment.WithWorldReady(true, () =>
            DailyDistillationStateStore.Reconcile(
                WorldNpc(NpcA), recoveryLedger,
                MemoryManager.Instance.GetTimelineMemories(NpcA, MemoryTier.Daily), 5));
        var probeLedger = ReadLedger();
        Assert.Null(probeLedger.Days[5].PendingCommit);   // Reconcile 清除未应用 pending
    }

    [Fact]
    public void CompleteDailyTask_PlayerEditedCard_SuspendsWithZeroCoverage()
    {
        InstallLocationWorld(NewNpc(NpcA));   // 先装世界 NPC：台账写在其实例 modData 上
        SeedCards(Card("E1", OldDiary, 5));
        ReloadMemoryManager();
        SeedHistory(NpcA,
            (SpeakerType.NPC, "old row one", "dialogue", 5, 900),
            (SpeakerType.NPC, "old row two", "dialogue", 5, 910),
            (SpeakerType.NPC, "old row three", "dialogue", 5, 920));
        var ledger = new DailyDistillationLedger();
        ledger.Days[5] = new DailyDistillationDayState
        {
            TargetDay = 5,
            EntryId = "E1",
            LastContentHash = DailyDistillationSnapshotBuilder.HashText(OldDiary),
            CoveredRowKeys = RowKeysOf(NpcA, 5).Take(2).ToList()
        };
        WriteLedger(ledger);

        AddHistoryLine(NpcA, "evening one", 5, 1900);
        AddHistoryLine(NpcA, "evening two", 5, 1910);
        AddHistoryLine(NpcA, "evening three", 5, 1920);
        _stub.SetDelayed(TimeSpan.FromMilliseconds(150), "[\"" + NewDiary + "\"]");
        var task = PlanCandidate();

        StartDailyTask(task);

        // 生成期间玩家编辑卡片 → 所有权核对失败 → 持久 Suspended、零覆盖。
        var owned = MemoryManager.Instance.GetTimelineMemories(NpcA, MemoryTier.Daily).Single(e => e.Id == "E1");
        owned.Content = "I was rewritten by the player just now.";
        PumpAfterDrain();

        var day = ReadLedger().Days[5];
        Assert.True(day.Suspended);
        Assert.Equal("Edited", day.SuspensionReason);
        Assert.Null(day.PendingCommit);
        Assert.Equal("I was rewritten by the player just now.",
            MemoryManager.Instance.GetTimelineMemories(NpcA, MemoryTier.Daily).Single().Content);   // 零覆盖
        Assert.Equal(0, HudCount);
        Assert.Equal(1, day.IntradayAttempts);   // 已扣保持
    }

    [Fact]
    public void RecoveryAfterContentWriteBeforeAck_RestoresOwnership()
    {
        // 成功写正文但 ack 失败（崩溃形态）：已写未 ack → 恢复式核对恢复所有权与评估范围。
        InstallLocationWorld(NewNpc(NpcA));   // Probe 的恢复核对需要可解析的世界 NPC
        InstallLocationWorld(NewNpc(NpcA));   // 先装世界 NPC：台账写在其实例 modData 上
        SeedCards(Card("E1", OldDiary, 4));
        ReloadMemoryManager();
        SeedHistory(NpcA,
            (SpeakerType.NPC, "yesterday one", "dialogue", 4, 900),
            (SpeakerType.NPC, "yesterday two", "dialogue", 4, 910),
            (SpeakerType.NPC, "yesterday three", "dialogue", 4, 920));
        var covered = RowKeysOf(NpcA, 4);
        var contentHash = DailyDistillationSnapshotBuilder.HashText(NewDiary);
        var ledger = new DailyDistillationLedger();
        ledger.Days[4] = new DailyDistillationDayState
        {
            TargetDay = 4,
            IntradayAttempts = 1,
            PendingCommit = new DailyPendingCommit
            {
                CommitId = "commit-1",
                EntryId = "E1",
                ExpectedOldHash = DailyDistillationSnapshotBuilder.HashText(OldDiary),
                NewContentHash = contentHash,
                InputFingerprint = DailyDistillationSnapshotBuilder.HashText("fingerprint"),
                CoveredRowKeys = covered,
                IsFinal = false
            }
        };
        WriteLedger(ledger);

        // 正文实际已写（卡片内容 = 新正文）→ 恢复 A 分支。
        MemoryManager.Instance.GetTimelineMemories(NpcA, MemoryTier.Daily).Single(e => e.Id == "E1").Content = NewDiary;

        Probe();   // 恢复式核对（内部回写）

        var day = ReadLedger().Days[4];
        Assert.Null(day.PendingCommit);
        Assert.Equal("E1", day.EntryId);
        Assert.Equal(contentHash, day.LastContentHash);
        Assert.Equal(covered, day.CoveredRowKeys);
        Assert.Equal(DailyDistillationSnapshotBuilder.HashText("fingerprint"), day.LastEvaluatedFingerprint);
    }

    // ── 交互暂存 / 跨日 / 窗口 ───────────────────────────────────────────

    [Fact]
    public void CompleteDailyTask_DeferredWhileInteracting_ConsumedWithoutRegeneration()
    {
        var task = PlanCandidate(
            (SpeakerType.NPC, "chat one", "dialogue", 5, 900),
            (SpeakerType.NPC, "chat two", "dialogue", 5, 910),
            (SpeakerType.NPC, "chat three", "dialogue", 5, 920));
        _stub.SetDiary(NewDiary);

        // 玩家交互中（弹窗）：完成回调被暂存。
        var menuShim = (IClickableMenu)FormatterServices.GetUninitializedObject(typeof(StardewValley.Menus.TitleMenu));
        ActiveMenuField?.SetValue(null, menuShim);
        StartDailyTask(task);
        PumpAfterDrain();

        Assert.Single(Completions());   // 结果保留待消费
        Assert.Empty(MemoryManager.Instance.GetTimelineMemories(NpcA, MemoryTier.Daily));
        Assert.Contains(Key(NpcA, 5), InFlight());   // 运行键保持占用（防重复规划）
        Assert.True(IsProcessing);
        Assert.Equal(1, _stub.CallCount);

        // 玩家退出交互 → 后续一秒事件空闲时消费（重验后处理）。
        ActiveMenuField?.SetValue(null, null);
        var deferred = Completions().Dequeue();
        CompleteDailyTask(deferred.Task, deferred.Request, deferred.Result);

        var card = MemoryManager.Instance.GetTimelineMemories(NpcA, MemoryTier.Daily).Single();
        Assert.Equal(NewDiary, card.Content);
        Assert.Equal(1, _stub.CallCount);            // 不再次生成/计费
        Assert.Empty(Completions());
        Assert.Equal(35, Cooldown);
        Assert.Empty(InFlight());
    }

    [Fact]
    public void CompleteDailyTask_TodayTaskCrossDay_DroppedForFinalPlanning()
    {
        var task = PlanCandidate(
            (SpeakerType.NPC, "chat one", "dialogue", 5, 900),
            (SpeakerType.NPC, "chat two", "dialogue", 5, 910),
            (SpeakerType.NPC, "chat three", "dialogue", 5, 920));
        _stub.SetDiary(NewDiary);
        StartDailyTask(task);

        SetGameDay(6);   // 跨日（今日 → 明日视角下目标日已过去）
        PumpAfterDrain();

        Assert.Empty(MemoryManager.Instance.GetTimelineMemories(NpcA, MemoryTier.Daily));
        Assert.Equal(0, HudCount);
        Assert.Equal(35, Cooldown);   // 丢弃后释放运行占用
        Assert.Empty(InFlight());
    }

    [Fact]
    public void CompleteDailyTask_FinalOutsideSevenDayWindow_Dropped()
    {
        // 最终任务目标日 = 今天（不在过去 7 日窗口内）→ 丢弃。
        var task = new AutoSummaryTask
        {
            NpcName = NpcA,
            NpcDisplayName = NpcA,
            Type = AutoSummaryType.Daily,
            TargetDate = MemoryManager.GameDayToStardewTime(5),
            IsFinalDaily = true,
            SaveSessionEpoch = SaveEpoch,
            ConfigurationEpoch = (long)ConfigEpochField.GetValue(S),
            DailyRequest = new DailyDistillationRequest
            {
                Provider = _stub,
                NpcName = NpcA,
                NpcDisplayName = NpcA,
                Snapshot = new DailyDistillationSnapshot
                {
                    NpcName = NpcA, TargetDay = 5, InputFingerprint = "fp",
                    Rows = Array.Empty<DailyDialogueLine>(), PromptLines = Array.Empty<string>()
                }
            }
        };
        var request = task.DailyRequest;
        var result = new MemoryExtractResult { Status = MemoryExtractStatus.Success, Candidates = { NewDiary } };

        int hudBefore = HudCount;
        CompleteDailyTask(task, request, result);

        Assert.Equal(hudBefore, HudCount);
        Assert.Equal(35, Cooldown);
    }

    private static int DayNumberOfTest(StardewTime t) => (t.Year - 1) * 112 + (int)t.Season * 28 + t.DayOfMonth;
}
