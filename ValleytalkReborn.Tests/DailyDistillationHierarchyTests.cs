// DailyDistillationHierarchyTests.cs
// ═══════════════════════════════════════════════════════════════════════════
// DD408-LIFECYCLE-HIERARCHY：生命周期启用、次日依赖与高层保源提交。
//
// 覆盖（受控输入，无真实网络；DD404 受控 IDataHelper 承载活跃/归档两库存储）：
//   1) 生命周期——SaveLoaded 失效会话并延后重建（零扫描零写入）；首个世界就绪且
//      Memory 已加载的一秒事件重建（重发现+重规划）；OnDayStarted 跨天取消旧日期
//      日间运行任务（旧日 ledger/attempts 保留、完成经跨日复查失效）、把过期日内
//      候选重规划为最终整理；Cleanup 幂等失效且保留未完成 drain。
//   2) 扫描恢复——日哨兵不再拦截：同档重载后同日重扫描恢复丢失的高层队列；
//      规范化键 (NPC, Type, TargetDay) 去重；本会话终态周期不再入队。
//   3) 依赖闭环——Weekly 等待 7 日窗口 Daily 最终整理闭合（待排队/执行中/暂存/
//      prepared/未关闭任一即延后，Trace 变化时一次）；FailedBudget 等终局关闭视为
//      settled 并允许升档沿用保留源；Disabled 模式依赖视为 SkippedDisabled；
//      Season 等待相关 Weekly 候选、Yearly 等待相关 Season 候选。
//   4) 高层执行——主线程捕获源 ID/hash + 固定 NewEntryId + 绝对 TargetDay；
//      完成区复查 epoch/目标周期/捕获源；DD404B.CommitTimelineCondensation 保源提交
//      （Applied→HUD；Conflict/Unavailable→撤销待重发现；CapacityFull/Duplicate→
//      保留源并结束本会话尝试；StorageFailed/Invalid→Error 升级并 block 本会话）。
//   5) 优先级——Daily 最终整理 → 高层 → 今日 Daily（同一准入窗口内）。
//
// 无头垫片沿用 DD406/407（Game1/位置/NPC/历史 + ControlledDataHelper + 真 Load）。
// ═══════════════════════════════════════════════════════════════════════════

using System;
using System.Collections.Generic;
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
public class DailyDistillationHierarchyTests : IDisposable
{
    private const string NpcA = "Abigail";
    private const string TimelineKey = "valleytalk.npc-timeline-memories";
    private const string CategoryFlagKey = "valleytalk.memory-category-migrated";
    private const string WeeklyContent = "a quiet week by the river with the farmer";

    private static readonly TimelineAutoSummaryScheduler S = TimelineAutoSummaryScheduler.Instance;

    // ── 调度器私有成员（D5 声明为 private，经反射驱动）──
    private static readonly MethodInfo ProbeMethod = M("ProbeDailyWork");
    private static readonly MethodInfo StartMethod = M("StartDailyTask");
    private static readonly MethodInfo CompleteDailyMethod = M("CompleteDailyTask");
    private static readonly MethodInfo CompleteHighTierMethod = M("CompleteTask");
    private static readonly MethodInfo ProcessMethod = M("ProcessNextTaskAsync");
    private static readonly MethodInfo ScanMethod = M("TryScanAndEnqueue");
    private static readonly MethodInfo SaveLoadedMethod = M("OnSaveLoaded");
    private static readonly MethodInfo DayStartedMethod = M("OnDayStarted");
    private static readonly MethodInfo TickMethod = M("OnOneSecondUpdateTicked");
    private static readonly MethodInfo EnqueueHighTierMethod = M("EnqueueHighTier");
    private static readonly MethodInfo DailyDepMethod = M("HasUnsettledDailyDependency");
    private static readonly MethodInfo HigherDepMethod = M("HasUnsettledHigherDependency");
    private static readonly FieldInfo QueueField = F(typeof(TimelineAutoSummaryScheduler), "_queue");
    private static readonly FieldInfo PendingField = F(typeof(TimelineAutoSummaryScheduler), "_dailyPending");
    private static readonly FieldInfo InFlightField = F(typeof(TimelineAutoSummaryScheduler), "_dailyInFlight");
    private static readonly FieldInfo BlockedField = F(typeof(TimelineAutoSummaryScheduler), "_dailyBlocked");
    private static readonly FieldInfo CompletionsField = F(typeof(TimelineAutoSummaryScheduler), "_dailyCompletions");
    private static readonly FieldInfo HigherPendingField = F(typeof(TimelineAutoSummaryScheduler), "_higherPendingKeys");
    private static readonly FieldInfo HigherBlockedField = F(typeof(TimelineAutoSummaryScheduler), "_higherSessionBlocked");
    private static readonly FieldInfo HigherTracedField = F(typeof(TimelineAutoSummaryScheduler), "_higherDeferralTraced");
    private static readonly FieldInfo SettledField = F(typeof(TimelineAutoSummaryScheduler), "_settledDailyDependencies");
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
    private static readonly FieldInfo TimelineField = F(typeof(MemoryManager), "_timelineMemories");
    private static readonly FieldInfo HistoryField = F(typeof(DialogueHistoryManager), "_history");
    private static readonly FieldInfo NetWorldStateField = F(typeof(Game1), "netWorldState");
    private static readonly FieldInfo Game1InstanceField = F(typeof(Game1), "game1");
    private static readonly FieldInfo LocationsField = F(typeof(Game1), "_locations");
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

    static DailyDistillationHierarchyTests()
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

    // ── 受控 Provider 桩：RunInference 承载高层 CondenseAsync，RunInferenceAsync 承载 Daily ──

    private sealed class HierarchyStubLlm : Llm
    {
        private Func<CancellationToken, Task<LlmResponse>> _condenseHandler = _ => Task.FromResult(new LlmResponse("[]"));
        private Func<CancellationToken, Task<LlmResponse>> _dailyHandler = _ => Task.FromResult(new LlmResponse("[]"));

        public int CondenseCallCount;
        public int DailyCallCount;

        public void SetCondense(string raw) => _condenseHandler = _ => Task.FromResult(new LlmResponse(raw));
        public void SetDelayed(TimeSpan delay, string raw) =>
            _dailyHandler = async _ =>
            {
                await Task.Delay(delay).ConfigureAwait(false);
                return new LlmResponse(raw);
            };

        public override bool IsHighlySensoredModel => false;
        public override string ExtraInstructions => string.Empty;

        internal override Task<LlmResponse> RunInference(
            string systemPromptString, string gameCacheString, string npcCacheString,
            string promptString, string responseStart = "", int n_predict = 2048,
            string cacheContext = "", bool allowRetry = true)
        {
            CondenseCallCount++;
            return _condenseHandler(CancellationToken.None);
        }

        internal override Task<LlmResponse> RunInferenceAsync(
            string systemPromptString, string gameCacheString, string npcCacheString,
            string promptString, CancellationToken ct, string responseStart = "",
            int n_predict = 2048, string cacheContext = "", bool allowRetry = true)
        {
            DailyCallCount++;
            return _dailyHandler(ct);
        }

        internal override Dictionary<string, double>[] RunInferenceProbabilities(string fullPrompt, int n_predict = 1)
            => throw new NotImplementedException();
    }

    private static readonly PropertyInfo LlmInstanceProperty =
        typeof(Llm).GetProperty("Instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

    private Llm _previousLlm;
    private HierarchyStubLlm _stub;

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
    private readonly Dictionary<string, List<DialogueHistoryEntry>> _previousHistory;
    private readonly ControlledDataHelper _data;
    private readonly Dictionary<string, NPC> _npcs = new(StringComparer.OrdinalIgnoreCase);
    private System.Collections.IList _hudList;
    private readonly List<object> _previousHud = new();

    public DailyDistillationHierarchyTests()
    {
        _localeScope = TestEnv.UseIsolatedLocale("en");
        ModEntry.Config.EnableMod = true;
        ModEntry.Config.DailyDistillMode = "Intraday";   // 注意：勿写 AutoSummarizeDaily=false（代理属性会把模式改写为 Disabled）
        ModEntry.Config.DailyDistillThreshold = 3;
        ModEntry.Config.DailyMaxRequestsPerNpc = 2;
        ModEntry.Config.AutoSummarizeWeekly = false;
        ModEntry.Config.AutoSummarizeSeason = false;
        ModEntry.Config.AutoSummarizeYearly = false;

        _playerScope = FakePlayer.Install("农夫");
        InitializeNetFields(Game1.player);

        _previousNetWorldState = NetWorldStateField?.GetValue(null);
        if (_previousNetWorldState == null)
            NetWorldStateField?.SetValue(null, Activator.CreateInstance(NetWorldStateField.FieldType, new NetWorldState()));
        _previousGameYear = Game1.year;
        _previousGameSeason = Game1.currentSeason;
        _previousGameDay = Game1.dayOfMonth;
        SetGameDay(8);   // 默认今日 = 第 1 年春 8 日（周一：8 % 7 == 1）

        _previousGame1 = Game1InstanceField?.GetValue(null);
        InstallLocationWorld();   // 空世界；测试按需放置 NPC

        _data = new ControlledDataHelper();
        _data.SaveData[CategoryFlagKey] = "true";
        TestEnv.SetSHelper(new ControlledModHelper(_data));
        TestEnvironment.WithWorldReady(true, () => MemoryManager.Instance.Load());

        var history = (Dictionary<string, List<DialogueHistoryEntry>>)HistoryField.GetValue(DialogueHistoryManager.Instance);
        _previousHistory = history.ToDictionary(p => p.Key, p => p.Value.ToList());
        history.Clear();

        _previousEventUp = Game1.eventUp;
        _previousDialogueUp = Game1.dialogueUp;
        Game1.eventUp = false;
        Game1.dialogueUp = false;
        ActiveMenuField?.SetValue(null, null);

        _hudList = (System.Collections.IList)HudMessagesField.GetValue(null);
        foreach (var item in _hudList.OfType<object>().ToList()) _previousHud.Add(item);
        _hudList.Clear();

        ValleytalkReborn.UI.PendingChoiceStore.Clear();
        DialogueBuilder.Instance.LlmDisabled = false;
        AsyncBuilder.Instance.IsGeneratingDialogue = false;

        _stub = new HierarchyStubLlm();
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

        if (_hudList != null)
        {
            _hudList.Clear();
            foreach (var item in _previousHud) _hudList.Add(item);
        }

        Game1.eventUp = _previousEventUp;
        Game1.dialogueUp = _previousDialogueUp;
        ActiveMenuField?.SetValue(null, null);

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
        ((Queue<AutoSummaryTask>)QueueField.GetValue(S)).Clear();
        ((Dictionary<(string, int), AutoSummaryTask>)PendingField.GetValue(S)).Clear();
        ((HashSet<(string, int)>)InFlightField.GetValue(S)).Clear();
        ((HashSet<(string, int)>)BlockedField.GetValue(S)).Clear();
        ((Queue<(AutoSummaryTask, DailyDistillationRequest, MemoryExtractResult)>)CompletionsField.GetValue(S)).Clear();
        ((HashSet<(string, AutoSummaryType, int)>)HigherPendingField.GetValue(S)).Clear();
        ((HashSet<(string, AutoSummaryType, int)>)HigherBlockedField.GetValue(S)).Clear();
        ((HashSet<(string, AutoSummaryType, int)>)HigherTracedField.GetValue(S)).Clear();
        ((HashSet<(string, int)>)SettledField.GetValue(S)).Clear();
        IdleField.SetValue(S, 0);
        TicksField.SetValue(S, 0);
        RebuildField.SetValue(S, false);
        DrainField.SetValue(S, null);
        // 每个测试使用唯一 epoch 基线：前序测试泄漏的旧回调必然被 epoch 丢弃。
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

    private static void AddFriendship(string name, int points) =>
        Game1.player.friendshipData.Add(name, new Friendship { Points = points });

    private static void SeedHistory(string npcName, params (SpeakerType Type, string Text, string DialogueType, int Day, int Time)[] lines)
    {
        var history = (Dictionary<string, List<DialogueHistoryEntry>>)HistoryField.GetValue(DialogueHistoryManager.Instance);
        history[npcName] = lines
            .Select(l => new DialogueHistoryEntry(npcName, l.Text, l.Type, new StardewTime(1, Season.Spring, l.Day, l.Time), l.DialogueType))
            .ToList();
    }

    private static MemoryEntry Card(string id, string content, int createdDay, MemoryTier tier = MemoryTier.Daily) => new()
    {
        Id = id,
        NpcName = NpcA,
        Content = content,
        CreatedAt = new DateTime(2026, 1, 1, 12, 0, 0),
        CreatedDay = createdDay,
        Source = "Timeline",
        Category = MemoryCategory.Fact,
        Type = MemoryType.Fact,
        Tier = tier,
        DateLabel = "seeded",
        Importance = 3
    };

    private void SeedCards(params MemoryEntry[] cards) =>
        ((Dictionary<string, List<MemoryEntry>>)TimelineField.GetValue(MemoryManager.Instance))[NpcA] = cards.ToList();

    private List<MemoryEntry> ActiveCards(MemoryTier tier) =>
        MemoryManager.Instance.GetTimelineMemories(NpcA, tier);

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

    private void Probe() => TestEnvironment.WithWorldReady(true, () => ProbeMethod.Invoke(S, null));

    private void Scan() => TestEnvironment.WithWorldReady(true, () => ScanMethod.Invoke(S, null));

    private void Tick() => TestEnvironment.WithWorldReady(true, () => TickMethod.Invoke(S, new object[] { null, null }));

    private void StartDailyTask(AutoSummaryTask task) =>
        TestEnvironment.WithWorldReady(true, () => StartMethod.Invoke(S, new object[] { task }));

    private void CompleteHighTier(AutoSummaryTask task, MemoryExtractResult result, int targetDay) =>
        TestEnvironment.WithWorldReady(true, () => CompleteHighTierMethod.Invoke(S, new object[] { task, result, targetDay }));

    /// <summary>执行高层路径并泵出主线程完成回调（含调度间隙余量与二次泵清）。</summary>
    private void RunProcessNext()
    {
        Task task = null;
        // ProcessNextTaskAsync 的同步前缀（出队/守卫/捕获）运行在一秒事件主线程上，
        // 必须处于 world-ready 作用域内。
        TestEnvironment.WithWorldReady(true, () => task = (Task)ProcessMethod.Invoke(S, null));
        Assert.True(SpinWait.SpinUntil(() => task.IsCompleted, 5000), "high-tier path did not complete in time");
        TestEnvironment.WithWorldReady(true, () =>
        {
            Thread.Sleep(50);
            MainThreadActionQueue.ProcessMainThreadQueue();
            Thread.Sleep(30);
            MainThreadActionQueue.ProcessMainThreadQueue();
        });
    }

    /// <summary>启动 Daily 后等待底层 drain 结束并泵主线程队列（后台回调由观察者投递）。</summary>
    private void PumpAfterDrain(int timeoutMs = 3000)
    {
        Assert.True(SpinWait.SpinUntil(() =>
        {
            var drain = (Task)DrainField.GetValue(S);
            return drain != null && drain.IsCompleted;
        }, timeoutMs), "daily drain did not complete in time");
        TestEnvironment.WithWorldReady(true, () =>
        {
            Thread.Sleep(50);
            MainThreadActionQueue.ProcessMainThreadQueue();
            Thread.Sleep(30);
            MainThreadActionQueue.ProcessMainThreadQueue();
        });
    }

    private Queue<AutoSummaryTask> Queue() => (Queue<AutoSummaryTask>)QueueField.GetValue(S);

    private Dictionary<(string, int), AutoSummaryTask> Pending() =>
        (Dictionary<(string, int), AutoSummaryTask>)PendingField.GetValue(S);

    private HashSet<(string, int)> InFlight() => (HashSet<(string, int)>)InFlightField.GetValue(S);

    private HashSet<(string, AutoSummaryType, int)> HigherPending() =>
        (HashSet<(string, AutoSummaryType, int)>)HigherPendingField.GetValue(S);

    private HashSet<(string, AutoSummaryType, int)> HigherBlocked() =>
        (HashSet<(string, AutoSummaryType, int)>)HigherBlockedField.GetValue(S);

    private bool IsProcessing => (bool)IsProcessingField.GetValue(S);

    private int Cooldown => (int)CooldownField.GetValue(S);

    private long SaveEpoch => (long)SaveEpochField.GetValue(S);

    private int HudCount => ((System.Collections.IList)HudMessagesField.GetValue(null)).Count;

    private static (string, AutoSummaryType, int) Key(string npc, AutoSummaryType type, int day) =>
        (npc.ToUpperInvariant(), type, day);

    private static (string, int) DailyKey(string npc, int day) => (npc.ToUpperInvariant(), day);

    private AutoSummaryTask WeeklyTask(int targetDay) => new()
    {
        NpcName = NpcA,
        NpcDisplayName = NpcA,
        Type = AutoSummaryType.Weekly,
        TargetDate = MemoryManager.GameDayToStardewTime(targetDay)
    };

    private static bool DailyDependencyUnsettled(AutoSummaryTask task)
    {
        bool result = false;
        TestEnvironment.WithWorldReady(true, () => result = (bool)DailyDepMethod.Invoke(S, new object[] { task }));
        return result;
    }

    private static bool HigherDependencyUnsettled(AutoSummaryTask task)
    {
        bool result = false;
        TestEnvironment.WithWorldReady(true, () => result = (bool)HigherDepMethod.Invoke(S, new object[] { task }));
        return result;
    }

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

    private static long _testEpochBase;


    // ── 1. 生命周期：SaveLoaded 延后重建 ──────────────────────────────────

    [Fact]
    public void OnSaveLoaded_InvalidatesSession_NoImmediateScanOrLedgerWrite()
    {
        InstallLocationWorld(NewNpc(NpcA));
        SeedHistory(NpcA,
            (SpeakerType.NPC, "yesterday one", "dialogue", 7, 900),
            (SpeakerType.NPC, "yesterday two", "dialogue", 7, 910),
            (SpeakerType.NPC, "yesterday three", "dialogue", 7, 920));
        Pending()[DailyKey(NpcA, 7)] = WeeklyTask(7);   // 预置 Memory 候选，验证会话失效清空

        TestEnvironment.WithWorldReady(true, () => SaveLoadedMethod.Invoke(S, new object[] { null, null }));

        // 失效会话：Memory 候选清空、重建请求置位；零扫描零 ledger 写入。
        Assert.True((bool)RebuildField.GetValue(S));
        Assert.Empty(Pending());
        Assert.Empty(Queue());
        Assert.Empty(HigherPending());
        Assert.False(ReadLedger().Days.ContainsKey(7));   // 未写台账
        Assert.Equal(0, _stub.CondenseCallCount);
    }

    [Fact]
    public void Rebuild_RunsOnFirstSafeTick_WithMemoryLoaded()
    {
        InstallLocationWorld(NewNpc(NpcA));
        SeedHistory(NpcA,
            (SpeakerType.NPC, "yesterday one", "dialogue", 7, 900),
            (SpeakerType.NPC, "yesterday two", "dialogue", 7, 910),
            (SpeakerType.NPC, "yesterday three", "dialogue", 7, 920));
        RebuildField.SetValue(S, true);   // 模拟 SaveLoaded 已请求重建

        Tick();   // 首个安全一秒事件：世界就绪 + Memory 已加载

        // 重建立即全量扫描：昨日（第 7 日）进入最终整理候选。
        Assert.False((bool)RebuildField.GetValue(S));
        Assert.True(Pending().ContainsKey(DailyKey(NpcA, 7)));
        var candidate = Pending()[DailyKey(NpcA, 7)];
        Assert.True(candidate.IsFinalDaily);
        Assert.Equal(0, _stub.CondenseCallCount);   // 规划零网络
    }

    // ── 2. 扫描恢复：哨兵不拦截、规范化键去重 ────────────────────────────

    [Fact]
    public void RescanAfterReload_RestoresLostQueue_SentinelDoesNotBlock()
    {
        InstallLocationWorld(NewNpc(NpcA));
        AddFriendship(NpcA, 2500);
        ModEntry.Config.AutoSummarizeWeekly = true;

        Scan();                                  // 首次扫描：入队第 7 日周报
        Assert.Single(Queue());
        Assert.Contains(Key(NpcA, AutoSummaryType.Weekly, 7), HigherPending());

        S.InvalidateSaveSession();               // 模拟同档重载：Memory 队列清空
        Assert.Empty(Queue());
        Assert.Empty(HigherPending());

        Scan();                                  // 同日重扫描：哨兵相同也不拦截 → 恢复队列
        Assert.Single(Queue());
        Assert.Contains(Key(NpcA, AutoSummaryType.Weekly, 7), HigherPending());

        Scan();                                  // 再次扫描（未失效）：规范化键去重，不重复入队
        Assert.Single(Queue());
    }

    [Fact]
    public void EnqueueHighTier_SkipsSessionBlockedCycle()
    {
        var task = WeeklyTask(7);
        ((HashSet<(string, AutoSummaryType, int)>)HigherBlockedField.GetValue(S)).Add(Key(NpcA, AutoSummaryType.Weekly, 7));

        TestEnvironment.WithWorldReady(true, () => EnqueueHighTierMethod.Invoke(S, new object[] { task }));

        Assert.Empty(Queue());   // 本会话终态周期不再入队
        Assert.Empty(HigherPending());
    }

    // ── 3. 依赖闭环 ──────────────────────────────────────────────────────

    [Fact]
    public void Weekly_IsDeferredUntilDailyWindowClosed_ThenCommitsWithFixedIdAndAbsoluteDay()
    {
        InstallLocationWorld(NewNpc(NpcA));
        AddFriendship(NpcA, 2500);
        ModEntry.Config.AutoSummarizeWeekly = true;
        SeedCards(
            Card("D1", "Monday we planted together.", 6),
            Card("D2", "Tuesday we fished at dawn.", 7));
        _stub.SetCondense("[\"" + WeeklyContent + "\"]");

        Scan();   // 第 8 日（周一）→ 第 7 日周报入队
        Assert.Single(Queue());

        // 台账第 7 日未关闭（探针可规划窗口内）→ 依赖未闭合 → 延后，零生成。
        RunProcessNext();
        Assert.Single(Queue());            // 延后：重新入队
        Assert.Equal(0, _stub.CondenseCallCount);
        Assert.Contains(Key(NpcA, AutoSummaryType.Weekly, 7), HigherPending());

        // 探针关闭第 7 日（无台账无卡片 → BelowThreshold 终局）→ 依赖闭合。
        Probe();
        Assert.True(ReadLedger().Days[7].FinalizationClosed);

        RunProcessNext();   // 捕获 → 生成 → 主线程完成区提交

        // 保源提交：固定 NewEntryId、绝对目标日、源移除、HUD 与成功日志。
        var weekly = ActiveCards(MemoryTier.Weekly).Single();
        Assert.Equal(WeeklyContent, weekly.Content);
        Assert.Equal(7, weekly.CreatedDay);
        Assert.Equal(MemoryTier.Weekly, weekly.Tier);
        Assert.False(string.IsNullOrEmpty(weekly.Id));

        // 捕获源已归档并移除，仅捕获源被移除。
        Assert.Empty(ActiveCards(MemoryTier.Daily));
        Assert.Equal(1, _stub.CondenseCallCount);
        Assert.Equal(1, HudCount);
        Assert.Equal(35, Cooldown);
        Assert.False(IsProcessing);
        Assert.Empty(HigherPending());
    }

    [Fact]
    public void HasUnsettledDailyDependency_VariousDayStates()
    {
        InstallLocationWorld(NewNpc(NpcA));
        var task = WeeklyTask(7);   // 窗口 = 第 1..7 日（今日第 8 日）

        // A. 窗口内未落账（探针可规划）→ 未闭合。
        Assert.True(DailyDependencyUnsettled(task));

        // B. 待排队候选 → 未闭合。
        Pending()[DailyKey(NpcA, 7)] = WeeklyTask(7);
        Assert.True(DailyDependencyUnsettled(task));
        Pending().Clear();

        // C. 执行中 → 未闭合。
        InFlight().Add(DailyKey(NpcA, 7));
        Assert.True(DailyDependencyUnsettled(task));
        InFlight().Clear();

        // D. 第 7 日（昨日）FailedBudget 终局关闭 → 闭合（允许升档沿用保留正文）；
        //    窗口内其余无台账日期按缺省闭合（探针不再发现）。
        var ledger = new DailyDistillationLedger();
        ledger.Days[7] = new DailyDistillationDayState
        {
            TargetDay = 7,
            FinalizationClosed = true,
            FinalizationOutcome = "FailedBudget"
        };
        WriteLedger(ledger);
        Assert.False(DailyDependencyUnsettled(task));

        // E. prepared journal 未清（第 7 日重新打开且挂 prepared）→ 未闭合。
        var pending = ReadLedger();
        pending.Days[7] = new DailyDistillationDayState
        {
            TargetDay = 7,
            PendingCommit = new DailyPendingCommit
            {
                CommitId = "c1",
                EntryId = "E1",
                NewContentHash = DailyDistillationSnapshotBuilder.HashText("pending content")
            }
        };
        WriteLedger(pending);
        Assert.True(DailyDependencyUnsettled(task));

        // F. Disabled 模式 → 依赖视为 SkippedDisabled（闭合，零持久写）。
        ModEntry.Config.DailyDistillMode = "Disabled";
        Assert.False(DailyDependencyUnsettled(task));
        ModEntry.Config.DailyDistillMode = "Intraday";

        // G. 台账内未关闭日期：探针窗口（currentDay-7..currentDay-1）内的第 13 日 → 未闭合；
        //    窗口外的第 8 日（过老）→ 按缺省闭合。关闭 13/14 后整体闭合。
        SetGameDay(20);
        var stale = WeeklyTask(14);   // 窗口 = 第 8..14 日
        var staleLedger = new DailyDistillationLedger();
        staleLedger.Days[8] = new DailyDistillationDayState { TargetDay = 8 };    // 过老：探针不再处理
        staleLedger.Days[13] = new DailyDistillationDayState { TargetDay = 13 };  // 窗口内未关闭
        WriteLedger(staleLedger);
        Assert.True(DailyDependencyUnsettled(stale));

        staleLedger = new DailyDistillationLedger();
        staleLedger.Days[8] = new DailyDistillationDayState { TargetDay = 8 };    // 过老 → 缺省闭合
        for (int day = 13; day <= 14; day++)
            staleLedger.Days[day] = new DailyDistillationDayState
            {
                TargetDay = day,
                FinalizationClosed = true,
                FinalizationOutcome = "BelowThreshold"
            };
        WriteLedger(staleLedger);
        Assert.False(DailyDependencyUnsettled(stale));
        SetGameDay(8);
    }

    [Fact]
    public void Season_WaitsForRelatedWeekly_YearlyWaitsForRelatedSeason()
    {
        var seasonTask = new AutoSummaryTask
        {
            NpcName = NpcA,
            NpcDisplayName = NpcA,
            Type = AutoSummaryType.Season,
            TargetDate = MemoryManager.GameDayToStardewTime(28)   // 春 28（季末）
        };
        var yearlyTask = new AutoSummaryTask
        {
            NpcName = NpcA,
            NpcDisplayName = NpcA,
            Type = AutoSummaryType.Yearly,
            TargetDate = MemoryManager.GameDayToStardewTime(112)  // 冬 28（年末）
        };

        // 无相关候选 → 不等待。
        Assert.False(HigherDependencyUnsettled(seasonTask));
        Assert.False(HigherDependencyUnsettled(yearlyTask));

        // 同 NPC 相关 Weekly（目标日 28 属春季）→ Season 等待。
        var higherPending = HigherPending();
        higherPending.Add(Key(NpcA, AutoSummaryType.Weekly, 28));
        Assert.True(HigherDependencyUnsettled(seasonTask));

        // 其他季节的 Weekly 不阻塞 Season。
        higherPending.Remove(Key(NpcA, AutoSummaryType.Weekly, 28));
        higherPending.Add(Key(NpcA, AutoSummaryType.Weekly, 30));   // 夏 2
        Assert.False(HigherDependencyUnsettled(seasonTask));

        // 同年相关 Season → Yearly 等待；他年 Season 不阻塞。
        higherPending.Remove(Key(NpcA, AutoSummaryType.Weekly, 30));
        higherPending.Add(Key(NpcA, AutoSummaryType.Season, 28));
        Assert.True(HigherDependencyUnsettled(yearlyTask));
        higherPending.Remove(Key(NpcA, AutoSummaryType.Season, 28));
        higherPending.Add(Key(NpcA, AutoSummaryType.Season, 140));   // 次年春 28
        Assert.False(HigherDependencyUnsettled(yearlyTask));
    }

    // ── 4. 完成区复查与保源提交分支 ──────────────────────────────────────

    private AutoSummaryTask CapturedWeeklyTask(params MemoryEntry[] sources)
    {
        var task = WeeklyTask(7);
        task.NewEntryId = Guid.NewGuid().ToString();
        task.SourceEntryIds = sources.Select(e => e.Id).ToList();
        task.SourceContentHashes = sources
            .Select(e => DailyDistillationSnapshotBuilder.HashText(e.Content ?? "")).ToList();
        task.SaveSessionEpoch = SaveEpoch;
        task.ConfigurationEpoch = (long)ConfigEpochField.GetValue(S);
        return task;
    }

    private static MemoryExtractResult SuccessResult(string content) => new()
    {
        Status = MemoryExtractStatus.Success,
        Candidates = { content }
    };

    [Fact]
    public void CompleteTask_SourceChanged_RevokesAndKeepsAllSources()
    {
        InstallLocationWorld(NewNpc(NpcA));
        var d1 = Card("D1", "Monday we planted together.", 6);
        var d2 = Card("D2", "Tuesday we fished at dawn.", 7);
        SeedCards(d1, d2);
        var task = CapturedWeeklyTask(d1, d2);
        HigherPending().Add(Key(NpcA, AutoSummaryType.Weekly, 7));

        // 生成期间玩家修改捕获源正文 → Info → 撤销生成、保留全部源、不写错误聚合。
        d1.Content = "I was rewritten by the player.";
        int hudBefore = HudCount;
        CompleteHighTier(task, SuccessResult(WeeklyContent), 7);

        Assert.Empty(ActiveCards(MemoryTier.Weekly));                       // 零写入
        Assert.Equal(2, ActiveCards(MemoryTier.Daily).Count);               // 全部源保留
        Assert.Equal("I was rewritten by the player.", ActiveCards(MemoryTier.Daily).First(e => e.Id == "D1").Content);
        Assert.Equal(hudBefore, HudCount);
        Assert.Empty(HigherPending());                                      // 候选撤销，等待重新发现
        Assert.Equal(35, Cooldown);
        Assert.False(IsProcessing);
    }

    [Fact]
    public void CompleteTask_PeriodAlreadyCovered_RevokesWithoutCommit()
    {
        InstallLocationWorld(NewNpc(NpcA));
        var d1 = Card("D1", "Monday we planted together.", 6);
        var d2 = Card("D2", "Tuesday we fished at dawn.", 7);
        SeedCards(d1, d2, Card("W0", "existing weekly reflection", 5, MemoryTier.Weekly));   // 同周期已有周报
        var task = CapturedWeeklyTask(d1, d2);
        HigherPending().Add(Key(NpcA, AutoSummaryType.Weekly, 7));

        int hudBefore = HudCount;
        CompleteHighTier(task, SuccessResult(WeeklyContent), 7);

        Assert.Single(ActiveCards(MemoryTier.Weekly));              // 无第二张周报
        Assert.Equal(2, ActiveCards(MemoryTier.Daily).Count);               // 源保留
        Assert.Equal(hudBefore, HudCount);
        Assert.Empty(HigherPending());
        Assert.Equal(35, Cooldown);
    }

    [Fact]
    public void CompleteTask_CapacityFull_KeepsSourcesAndEndsCycleForSession()
    {
        InstallLocationWorld(NewNpc(NpcA));
        var d1 = Card("D1", "Monday we planted together.", 6);
        var d2 = Card("D2", "Tuesday we fished at dawn.", 7);
        // 10 张周报打满容量（目标周期桶 0 留空，确保先通过周期复查再命中容量守卫）。
        var weeklyCards = Enumerable.Range(8, 10)
            .Select(i => Card($"W{i}", $"weekly reflection {i}", i, MemoryTier.Weekly))
            .ToList();
        SeedCards(new[] { d1, d2 }.Concat(weeklyCards).ToArray());
        var task = CapturedWeeklyTask(d1, d2);
        HigherPending().Add(Key(NpcA, AutoSummaryType.Weekly, 7));

        CompleteHighTier(task, SuccessResult(WeeklyContent), 7);

        Assert.Equal(10, ActiveCards(MemoryTier.Weekly).Count);             // 无新卡片
        Assert.Equal(2, ActiveCards(MemoryTier.Daily).Count);               // 源保留
        Assert.Contains(Key(NpcA, AutoSummaryType.Weekly, 7), HigherBlocked());   // 本会话终态
        Assert.Empty(HigherPending());                                      // 依赖释放

        // 本会话重发现不再入队。
        TestEnvironment.WithWorldReady(true, () => EnqueueHighTierMethod.Invoke(S, new object[] { WeeklyTask(7) }));
        Assert.Empty(Queue());
    }

    [Fact]
    public void CompleteTask_StorageFailed_Escalates_KeepsSourcesAndBlocks()
    {
        InstallLocationWorld(NewNpc(NpcA));
        var d1 = Card("D1", "Monday we planted together.", 6);
        var d2 = Card("D2", "Tuesday we fished at dawn.", 7);
        SeedCards(d1, d2);
        var task = CapturedWeeklyTask(d1, d2);
        HigherPending().Add(Key(NpcA, AutoSummaryType.Weekly, 7));

        _data.ThrowOnWrite = true;   // 归档阶段写入失败 → StorageFailed
        try
        {
            int hudBefore = HudCount;
            CompleteHighTier(task, SuccessResult(WeeklyContent), 7);

            Assert.Empty(ActiveCards(MemoryTier.Weekly));                   // 零活跃写入
            Assert.Equal(2, ActiveCards(MemoryTier.Daily).Count);           // 停止删源
            Assert.Contains(Key(NpcA, AutoSummaryType.Weekly, 7), HigherBlocked());
            Assert.Equal(hudBefore, HudCount);
        }
        finally
        {
            _data.ThrowOnWrite = false;
        }
    }

    [Fact]
    public void CompleteTask_ConfigEpochChanged_DropsAndReleases()
    {
        InstallLocationWorld(NewNpc(NpcA));
        var d1 = Card("D1", "Monday we planted together.", 6);
        var d2 = Card("D2", "Tuesday we fished at dawn.", 7);
        SeedCards(d1, d2);
        var task = CapturedWeeklyTask(d1, d2);
        HigherPending().Add(Key(NpcA, AutoSummaryType.Weekly, 7));

        // GMCM 配置保存接线：配置 epoch 递增 → 在途完成重验后丢弃。
        S.InvalidateConfiguration();
        int hudBefore = HudCount;
        CompleteHighTier(task, SuccessResult(WeeklyContent), 7);

        Assert.Empty(ActiveCards(MemoryTier.Weekly));
        Assert.Equal(2, ActiveCards(MemoryTier.Daily).Count);
        Assert.Equal(hudBefore, HudCount);
        Assert.Empty(HigherPending());   // 候选撤销等待重新发现
        Assert.Equal(35, Cooldown);      // 同会话丢弃仍释放运行占用
        Assert.False(IsProcessing);
    }

    [Fact]
    public void CompleteTask_StaleSaveSession_DropsWithoutTouchingNewSessionFlags()
    {
        InstallLocationWorld(NewNpc(NpcA));
        var d1 = Card("D1", "Monday we planted together.", 6);
        var d2 = Card("D2", "Tuesday we fished at dawn.", 7);
        SeedCards(d1, d2);
        var task = CapturedWeeklyTask(d1, d2);
        SaveEpochField.SetValue(S, task.SaveSessionEpoch + 1);   // 新会话（旧回调）

        int hudBefore = HudCount;
        CompleteHighTier(task, SuccessResult(WeeklyContent), 7);

        Assert.Empty(ActiveCards(MemoryTier.Weekly));
        Assert.Equal(2, ActiveCards(MemoryTier.Daily).Count);
        Assert.Equal(hudBefore, HudCount);
        Assert.Equal(0, Cooldown);       // 不释放新会话标志
        Assert.False(IsProcessing);
    }

    [Fact]
    public void CompleteTask_ResolvedSourceWithoutCapture_RemainsAfterCommit()
    {
        // 流程8/9：完成时新解析出的非捕获源条目不得被删除（仅移除捕获源）。
        // 在完成区运行之前预置窗口内的新增源 D3（模拟生成期间玩家互动新增）。
        InstallLocationWorld(NewNpc(NpcA));
        var d1 = Card("D1", "Monday we planted together.", 6);
        var d2 = Card("D2", "Tuesday we fished at dawn.", 7);
        SeedCards(d1, d2);
        var task = CapturedWeeklyTask(d1, d2);
        HigherPending().Add(Key(NpcA, AutoSummaryType.Weekly, 7));

        SeedCards(d1, d2, Card("D3", "Wednesday we danced in the rain.", 7));   // 生成期间新增

        CompleteHighTier(task, SuccessResult(WeeklyContent), 7);

        var weekly = ActiveCards(MemoryTier.Weekly).Single();
        Assert.Equal(7, weekly.CreatedDay);
        var daily = ActiveCards(MemoryTier.Daily).ToList();
        Assert.Single(daily);                       // 仅捕获源被移除
        Assert.Equal("D3", daily[0].Id);            // 非捕获源保留
        Assert.Equal(1, HudCount);                  // Applied → HUD
        Assert.Equal(35, Cooldown);
        Assert.Empty(HigherPending());
    }

    // ── 交互暂存结果的空闲消费（DD407 → DD408 一秒事件接线）──────────────

    [Fact]
    public void DeferredCompletion_ConsumedOnIdleTick_WithoutRegeneration()
    {
        InstallLocationWorld(NewNpc(NpcA));
        SetGameDay(5);
        _stub.SetDelayed(TimeSpan.FromMilliseconds(150), "[\"I spent a quiet day by the river.\"]");
        SeedHistory(NpcA,
            (SpeakerType.NPC, "chat one", "dialogue", 5, 900),
            (SpeakerType.NPC, "chat two", "dialogue", 5, 910),
            (SpeakerType.NPC, "chat three", "dialogue", 5, 920));
        Probe();
        var task = Pending().Values.Single();
        Pending().Clear();

        // 玩家交互中（弹窗）：完成回调被暂存，运行键保持占用。
        var menuShim = (IClickableMenu)FormatterServices.GetUninitializedObject(typeof(StardewValley.Menus.TitleMenu));
        ActiveMenuField?.SetValue(null, menuShim);
        StartDailyTask(task);
        PumpAfterDrain();

        var completions = (Queue<(AutoSummaryTask, DailyDistillationRequest, MemoryExtractResult)>)CompletionsField.GetValue(S);
        Assert.Single(completions);
        Assert.Empty(MemoryManager.Instance.GetTimelineMemories(NpcA, MemoryTier.Daily));
        Assert.True(IsProcessing);

        // 玩家退出交互 → 后续一秒事件空闲时消费（重验后处理，不再次生成/计费）。
        ActiveMenuField?.SetValue(null, null);
        Tick();

        var card = MemoryManager.Instance.GetTimelineMemories(NpcA, MemoryTier.Daily).Single();
        Assert.Equal("I spent a quiet day by the river.", card.Content);
        Assert.Empty(completions);
        Assert.Equal(1, _stub.DailyCallCount);
        Assert.Equal(35, Cooldown);
        Assert.Empty(InFlight());
        Assert.False(IsProcessing);
    }

    // ── 5. 跨天处理 ──────────────────────────────────────────────────────

    [Fact]
    public void DayRollover_CancelsIntradayRun_AttemptsKept_CompletionInvalidated()
    {
        InstallLocationWorld(NewNpc(NpcA));
        SetGameDay(5);
        _stub.SetDelayed(TimeSpan.FromMilliseconds(150), "[\"a day by the river\"]");
        SeedHistory(NpcA,
            (SpeakerType.NPC, "chat one", "dialogue", 5, 900),
            (SpeakerType.NPC, "chat two", "dialogue", 5, 910),
            (SpeakerType.NPC, "chat three", "dialogue", 5, 920));
        Probe();
        var task = Pending().Values.Single();
        Pending().Clear();
        StartDailyTask(task);
        Assert.Contains(DailyKey(NpcA, 5), InFlight());

        SetGameDay(6);
        TestEnvironment.WithWorldReady(true, () => DayStartedMethod.Invoke(S, new object[] { null, null }));

        // 跨天取消：会话 CTS 轮换（旧日间任务取消），运行键释放由完成回调处理；
        // 旧日 ledger/attempts 保留，重建请求置位。
        Assert.True((bool)RebuildField.GetValue(S));
        Assert.Equal(1, ReadLedger().Days[5].IntradayAttempts);   // 次数保留

        PumpAfterDrain();   // 完成回调经跨日复查失效

        Assert.Empty(MemoryManager.Instance.GetTimelineMemories(NpcA, MemoryTier.Daily));   // 零覆盖
        Assert.Equal(0, HudCount);
        Assert.Equal(35, Cooldown);      // 释放运行占用
        Assert.Empty(InFlight());
        Assert.False(IsProcessing);
        Assert.Equal(1, ReadLedger().Days[5].IntradayAttempts);   // 不退费
    }

    [Fact]
    public void DayRollover_ReplacesStaleIntradayCandidateWithFinalPlan()
    {
        InstallLocationWorld(NewNpc(NpcA));
        SetGameDay(5);
        SeedHistory(NpcA,
            (SpeakerType.NPC, "chat one", "dialogue", 5, 900),
            (SpeakerType.NPC, "chat two", "dialogue", 5, 910),
            (SpeakerType.NPC, "chat three", "dialogue", 5, 920));
        Probe();
        Assert.True(Pending().ContainsKey(DailyKey(NpcA, 5)));
        Assert.False(Pending()[DailyKey(NpcA, 5)].IsFinalDaily);

        SetGameDay(6);
        TestEnvironment.WithWorldReady(true, () => DayStartedMethod.Invoke(S, new object[] { null, null }));
        Tick();   // 重建：刚结束的一天重新规划为最终整理

        Assert.True(Pending().ContainsKey(DailyKey(NpcA, 5)));
        Assert.True(Pending()[DailyKey(NpcA, 5)].IsFinalDaily);   // 日内候选已被替换为最终整理
    }

    [Fact]
    public void DayRollover_FinalRunInFlight_NotCancelled()
    {
        InstallLocationWorld(NewNpc(NpcA));
        SetGameDay(5);
        _stub.SetDelayed(TimeSpan.FromMilliseconds(150), "[\"I closed the week in quiet peace.\"]");
        SeedHistory(NpcA,
            (SpeakerType.NPC, "yesterday one", "dialogue", 4, 900),
            (SpeakerType.NPC, "yesterday two", "dialogue", 4, 910),
            (SpeakerType.NPC, "yesterday three", "dialogue", 4, 920));
        Probe();
        var task = Pending().Values.Single();
        Assert.True(task.IsFinalDaily);
        Pending().Clear();
        StartDailyTask(task);
        var ctsBefore = (CancellationTokenSource)SessionCtsField.GetValue(S);

        SetGameDay(6);
        TestEnvironment.WithWorldReady(true, () => DayStartedMethod.Invoke(S, new object[] { null, null }));

        // 最终整理型任务不跨天取消（同一 CTS 保留）。
        Assert.True(ReferenceEquals(ctsBefore, SessionCtsField.GetValue(S)));
        Assert.Equal(1, ReadLedger().Days[4].FinalAttempts);

        PumpAfterDrain();   // 新一天窗口内正常处理

        var day = ReadLedger().Days[4];
        Assert.True(day.FinalizationClosed);
        Assert.Equal("Committed", day.FinalizationOutcome);
        Assert.Single(MemoryManager.Instance.GetTimelineMemories(NpcA, MemoryTier.Daily));
        Assert.Equal(1, HudCount);
    }

    // ── 6. 会话清理幂等 ──────────────────────────────────────────────────

    [Fact]
    public void Cleanup_InvalidatesIdempotently_KeepsUnfinishedDrain()
    {
        InstallLocationWorld(NewNpc(NpcA));
        Queue().Enqueue(WeeklyTask(7));
        HigherPending().Add(Key(NpcA, AutoSummaryType.Weekly, 7));
        HigherBlocked().Add(Key(NpcA, AutoSummaryType.Season, 28));
        Pending()[DailyKey(NpcA, 5)] = WeeklyTask(5);
        InFlight().Add(DailyKey(NpcA, 5));
        IdleField.SetValue(S, 7);
        var unfinishedDrain = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously).Task;
        DrainField.SetValue(S, unfinishedDrain);
        long epochBefore = SaveEpoch;

        S.Cleanup();
        S.Cleanup();   // 重复调用（不同 handler）幂等安全

        Assert.Empty(Queue());
        Assert.Empty(Pending());
        Assert.Empty(InFlight());
        Assert.Empty(HigherPending());
        Assert.Empty(HigherBlocked());
        Assert.Equal(0, (int)IdleField.GetValue(S));
        Assert.False(IsProcessing);
        Assert.Equal(0, Cooldown);
        Assert.Equal(epochBefore + 2, SaveEpoch);       // 两次失效各递增一次
        // 未完成 drain 句柄保留（观察者仍 await；回调由 epoch 丢弃路径处理）。
        Assert.True(ReferenceEquals(unfinishedDrain, DrainField.GetValue(S)));
    }

    // ── 7. 准入优先级 ────────────────────────────────────────────────────

    [Fact]
    public void TickPriority_FinalDailyBeforeHighTier_BeforeIntraday()
    {
        InstallLocationWorld(NewNpc(NpcA));
        SetGameDay(5);
        // 同时播种今日（第 5 日）与昨日（第 4 日）历史后一次性探测——探针会把窗口内
        // 无台账的过去日期直接关闭，分两次播种会令第一次探针提前关闭第 4 日。
        SeedHistory(NpcA,
            (SpeakerType.NPC, "chat one", "dialogue", 5, 900),
            (SpeakerType.NPC, "chat two", "dialogue", 5, 910),
            (SpeakerType.NPC, "chat three", "dialogue", 5, 920),
            (SpeakerType.NPC, "yesterday one", "dialogue", 4, 900),
            (SpeakerType.NPC, "yesterday two", "dialogue", 4, 910),
            (SpeakerType.NPC, "yesterday three", "dialogue", 4, 920));
        Probe();
        Assert.Equal(2, Pending().Count);
        // 高层队列已有任务。
        HigherPending().Add(Key(NpcA, AutoSummaryType.Weekly, 7));
        Queue().Enqueue(WeeklyTask(7));
        _stub.SetDelayed(TimeSpan.FromMilliseconds(80), "[\"I tended the crops before dawn.\"]");

        // 闲置计数 9 → 本次一秒事件达到 10 → 准入一次。
        IdleField.SetValue(S, 9);
        Tick();

        // 最终整理优先：第 4 日任务启动（FinalAttempts 预留），日内候选与高层队列保持。
        Assert.Equal(1, ReadLedger().Days[4].FinalAttempts);
        Assert.False(Pending().ContainsKey(DailyKey(NpcA, 4)));
        Assert.True(Pending().ContainsKey(DailyKey(NpcA, 5)));
        Assert.Single(Queue());
        Assert.True(IsProcessing);
        Assert.Equal(1, _stub.DailyCallCount);

        PumpAfterDrain();
        Assert.Equal(0, _stub.CondenseCallCount);   // 高层未在本轮启动
    }
}
