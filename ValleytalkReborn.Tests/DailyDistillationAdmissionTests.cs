// DailyDistillationAdmissionTests.cs
// ═══════════════════════════════════════════════════════════════════════════
// DD406-ADMISSION：日记蒸馏主线程候选规划与请求准入。
//
// 覆盖（受控普通输入，不修改生产公共 API；UI 条件的 SMAPI 矩阵留给最终验收）：
//   1) 三档模式——Disabled 不规划、Overnight 只规划过去日、Intraday 今日+最终整理。
//   2) 统一 NPC 统计——历史名 ∪ 主机好友表去重并集，键统一为
//      (NPC代码名.ToUpperInvariant(), TargetDay)，展示名不作身份。
//   3) 阈值——今日无 EntryId 看 QualifyingCount、有 EntryId 看 UncoveredQualifyingCount；
//      只有玩家发言不触发（NPC 行才 Qualifies）。
//   4) 首发/更新共享预算——日内+最终尝试总和达上限即不再规划；
//      过去日期预算耗尽关闭 FailedBudget 且保留 CoveredRowKeys 原值。
//   5) 最后不足阈值尾段仍最终更新——已有自动卡片只要求未覆盖 &gt; 0，不受阈值限制。
//   6) 终局关闭——Unchanged/BelowThreshold/Empty/SkippedManual/FailedBudget，
//      先 TryWrite 后 settled；昨日无台账也检查、BelowThreshold 落账本。
//   7) 重复探测唯一任务——唯一键合并且替换候选数据（新资料刷新快照），不累积。
//   8) 恢复核对接入——Reconcile 在规划前执行并回写恢复变化。
//   9) 准入闸门——连续 10 个一秒事件空闲才准入；菜单/对话/事件/生成中/自定义选项/
//      调度器运行/drain/冷却/Provider 禁用/模组关闭任一命中立即归零闲置计数。
//
// 无头垫片：netWorldState（Game1.Date）、game1+_locations（getCharacterFromName）、
// FakePlayer+INetObject 补齐（friendshipData）、MemoryManager._isLoaded 直置、
// 历史字典内容注入（前缀键+进程级保存恢复）、台账经 modData 真实读写。
// ═══════════════════════════════════════════════════════════════════════════

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Runtime.Serialization;
using Netcode;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;
using StardewValley.Network;
using ValleytalkReborn;
using Xunit;

namespace ValleytalkReborn.Tests;

[Collection("WorldReadyStateCollection")]
public class DailyDistillationAdmissionTests : IDisposable
{
    private const string NpcA = "Abigail";
    private const string NpcB = "Alex";

    private static readonly TimelineAutoSummaryScheduler S = TimelineAutoSummaryScheduler.Instance;

    // ── 调度器私有成员（IVT + 反射，D5 声明为 private）──
    private static readonly MethodInfo ProbeMethod =
        typeof(TimelineAutoSummaryScheduler).GetMethod("ProbeDailyWork", BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly MethodInfo CanAdmitMethod =
        typeof(TimelineAutoSummaryScheduler).GetMethod("CanAdmitDailyWork", BindingFlags.Instance | BindingFlags.NonPublic);

    private static readonly FieldInfo PendingField = F(typeof(TimelineAutoSummaryScheduler), "_dailyPending");
    private static readonly FieldInfo InFlightField = F(typeof(TimelineAutoSummaryScheduler), "_dailyInFlight");
    private static readonly FieldInfo BlockedField = F(typeof(TimelineAutoSummaryScheduler), "_dailyBlocked");
    private static readonly FieldInfo SettledField = F(typeof(TimelineAutoSummaryScheduler), "_settledDailyDependencies");
    private static readonly FieldInfo IdleField = F(typeof(TimelineAutoSummaryScheduler), "_dailyIdleSeconds");
    private static readonly FieldInfo TicksField = F(typeof(TimelineAutoSummaryScheduler), "_dailyProbeTicks");
    private static readonly FieldInfo RebuildField = F(typeof(TimelineAutoSummaryScheduler), "_dailyNeedsRebuild");
    private static readonly FieldInfo DrainField = F(typeof(TimelineAutoSummaryScheduler), "_dailyTransportDrain");
    private static readonly FieldInfo IsProcessingField = F(typeof(TimelineAutoSummaryScheduler), "_isProcessing");
    private static readonly FieldInfo CooldownField = F(typeof(TimelineAutoSummaryScheduler), "_cooldownSecondsRemaining");

    private static readonly FieldInfo IsLoadedField = F(typeof(MemoryManager), "_isLoaded");
    private static readonly FieldInfo TimelineField = F(typeof(MemoryManager), "_timelineMemories");
    private static readonly FieldInfo HistoryField = F(typeof(DialogueHistoryManager), "_history");
    private static readonly FieldInfo PendingChoiceField = typeof(ValleytalkReborn.UI.PendingChoiceStore)
        .GetField("_pending", BindingFlags.Static | BindingFlags.NonPublic);
    private static readonly FieldInfo AwaitingGenerationField = F(typeof(AsyncBuilder), "_awaitingGeneration");

    private static readonly FieldInfo NetWorldStateField = F(typeof(Game1), "netWorldState");
    private static readonly FieldInfo Game1InstanceField = F(typeof(Game1), "game1");
    private static readonly FieldInfo LocationsField = F(typeof(Game1), "_locations");

    private static FieldInfo F(Type type, string name)
    {
        for (var t = type; t != null && t != typeof(object); t = t.BaseType)
        {
            var f = t.GetField(name, BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
            if (f != null) return f;
        }
        return null;
    }

    static DailyDistillationAdmissionTests()
    {
        // Context / LocalizedContentManager 静态构造依赖 smapi-internal 与游戏目录程序集——
        // 挂 Resolving 钩子按名回退加载（幂等）。
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

        // DialogueHistoryManager / AsyncBuilder 的类型初始化器会订阅 SMAPI 事件；
        // FakeModHelper 的事件树会抛 NotImplementedException——在 SHelper 置空时
        // 先触发一次类型初始化（进程内幂等），使其以"无订阅"形态完成构造。
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

    private readonly IDisposable _localeScope;
    private readonly IDisposable _playerScope;
    private readonly object _previousNetWorldState;
    private readonly object _previousGame1;
    private readonly bool _previousIsLoaded;
    private readonly object _previousTimelines;
    private readonly Dictionary<string, List<DialogueHistoryEntry>> _previousHistory;
    private readonly bool _previousEventUp;
    private readonly bool _previousDialogueUp;
    private readonly IClickableMenu _previousMenu;
    private readonly bool _previousAwaitingGeneration;
    private readonly int _previousGameYear;
    private readonly string _previousGameSeason;
    private readonly int _previousGameDay;
    private readonly Dictionary<string, NPC> _npcs = new(StringComparer.OrdinalIgnoreCase);

    public DailyDistillationAdmissionTests()
    {
        _localeScope = TestEnv.UseIsolatedLocale("en");   // SMonitor/Config/SHelper/locale 隔离
        ModEntry.Config.EnableMod = true;
        ModEntry.Config.DailyDistillMode = "Intraday";
        ModEntry.Config.DailyDistillThreshold = 3;
        ModEntry.Config.DailyMaxRequestsPerNpc = 2;

        _playerScope = FakePlayer.Install("农夫");
        InitializeNetFields(Game1.player);   // friendshipData 等 INetObject 字段

        _previousNetWorldState = NetWorldStateField?.GetValue(null);
        if (_previousNetWorldState == null)
            NetWorldStateField?.SetValue(null, Activator.CreateInstance(NetWorldStateField.FieldType, new NetWorldState()));
        _previousGameYear = Game1.year;
        _previousGameSeason = Game1.currentSeason;
        _previousGameDay = Game1.dayOfMonth;
        SetGameDay(5);   // 今日 = 第 1 年春 5 日（一基日历日 5）

        _previousGame1 = Game1InstanceField?.GetValue(null);
        InstallLocationWorld();             // game1 + _locations = [空位置]（测试按需放置 NPC）

        _previousIsLoaded = (bool)IsLoadedField.GetValue(MemoryManager.Instance);
        IsLoadedField.SetValue(MemoryManager.Instance, true);
        _previousTimelines = TimelineField.GetValue(MemoryManager.Instance);
        TimelineField.SetValue(MemoryManager.Instance, new Dictionary<string, List<MemoryEntry>>(StringComparer.OrdinalIgnoreCase));

        var history = (Dictionary<string, List<DialogueHistoryEntry>>)HistoryField.GetValue(DialogueHistoryManager.Instance);
        _previousHistory = history.ToDictionary(p => p.Key, p => p.Value.ToList());
        history.Clear();

        _previousEventUp = Game1.eventUp;
        _previousDialogueUp = Game1.dialogueUp;
        _previousMenu = Game1.activeClickableMenu;
        Game1.eventUp = false;
        Game1.dialogueUp = false;
        Game1.activeClickableMenu = null;

        _previousAwaitingGeneration = (bool)AwaitingGenerationField.GetValue(AsyncBuilder.Instance);
        AwaitingGenerationField.SetValue(AsyncBuilder.Instance, false);
        AsyncBuilder.Instance.IsGeneratingDialogue = false;
        ValleytalkReborn.UI.PendingChoiceStore.Clear();
        DialogueBuilder.Instance.LlmDisabled = false;

        ResetDailyState();
    }

    public void Dispose()
    {
        ResetDailyState();

        DialogueBuilder.Instance.LlmDisabled = false;
        AsyncBuilder.Instance.IsGeneratingDialogue = false;
        AwaitingGenerationField.SetValue(AsyncBuilder.Instance, _previousAwaitingGeneration);
        ValleytalkReborn.UI.PendingChoiceStore.Clear();

        Game1.eventUp = _previousEventUp;
        Game1.dialogueUp = _previousDialogueUp;
        Game1.activeClickableMenu = _previousMenu;

        var history = (Dictionary<string, List<DialogueHistoryEntry>>)HistoryField.GetValue(DialogueHistoryManager.Instance);
        history.Clear();
        foreach (var pair in _previousHistory) history[pair.Key] = pair.Value;

        TimelineField.SetValue(MemoryManager.Instance, _previousTimelines);
        IsLoadedField.SetValue(MemoryManager.Instance, _previousIsLoaded);

        Game1InstanceField?.SetValue(null, _previousGame1);
        NetWorldStateField?.SetValue(null, _previousNetWorldState);
        Game1.year = _previousGameYear;
        Game1.currentSeason = _previousGameSeason;
        Game1.dayOfMonth = _previousGameDay;

        _playerScope?.Dispose();
        _localeScope?.Dispose();
    }

    // ── 夹具辅助 ──────────────────────────────────────────────────────────

    private void ResetDailyState()
    {
        ((Dictionary<(string, int), AutoSummaryTask>)PendingField.GetValue(S)).Clear();
        ((HashSet<(string, int)>)InFlightField.GetValue(S)).Clear();
        ((HashSet<(string, int)>)BlockedField.GetValue(S)).Clear();
        ((HashSet<(string, int)>)SettledField.GetValue(S)).Clear();
        IdleField.SetValue(S, 0);
        TicksField.SetValue(S, 0);
        RebuildField.SetValue(S, false);
        DrainField.SetValue(S, null);
        IsProcessingField.SetValue(S, false);
        CooldownField.SetValue(S, 0);
    }

    private Dictionary<(string, int), AutoSummaryTask> Pending() =>
        (Dictionary<(string, int), AutoSummaryTask>)PendingField.GetValue(S);

    private HashSet<(string, int)> Blocked() => (HashSet<(string, int)>)BlockedField.GetValue(S);

    private int Idle => (int)IdleField.GetValue(S);

    /// <summary>下一 tick 立即触发扫描（消费重建提示的正式语义）。</summary>
    private void RequestRebuild() => RebuildField.SetValue(S, true);

    private void Probe() => TestEnvironment.WithWorldReady(true, () => ProbeMethod.Invoke(S, null));

    private bool CanAdmit()
    {
        bool result = false;
        TestEnvironment.WithWorldReady(true, () => result = (bool)CanAdmitMethod.Invoke(S, null));
        return result;
    }

    private static void SetGameDay(int totalDays)
    {
        // WorldDate.Now() 读取 Game1 静态传统字段（year/season 字符串/dayOfMonth）——
        // 不触碰 StardewValley.GameData 的 Season 枚举（测试工程不可引用该程序集）。
        Game1.year = (totalDays - 1) / 112 + 1;
        Game1.currentSeason = (new[] { "spring", "summer", "fall", "winter" })[(totalDays - 1) / 28 % 4];
        Game1.dayOfMonth = (totalDays - 1) % 28 + 1;
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

    private static NPC NewNpc(string name)
    {
        var npc = (NPC)FormatterServices.GetUninitializedObject(typeof(NPC));
        InitializeNetFields(npc);
        F(typeof(NPC), "name")?.SetValue(npc, new NetString(name));
        npc.displayName = name;
        F(typeof(NPC), "dialogue")?.SetValue(npc, new Dictionary<string, string>());
        return npc;
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

    private static MemoryEntry Card(string id, string content, int createdDay, string npcName = NpcA) => new()
    {
        Id = id,
        NpcName = npcName,
        Content = content,
        Tier = MemoryTier.Daily,
        CreatedDay = createdDay,
        Source = "Timeline"
    };

    private static void SeedCards(string npcName, params MemoryEntry[] cards) =>
        ((Dictionary<string, List<MemoryEntry>>)TimelineField.GetValue(MemoryManager.Instance))[npcName] = cards.ToList();

    private DailyDistillationLedger ReadLedger(string npcName)
    {
        DailyDistillationLedger ledger = null;
        TestEnvironment.WithWorldReady(true, () =>
            DailyDistillationStateStore.TryRead(WorldNpc(npcName), out ledger));
        return ledger;
    }

    private void WriteLedger(string npcName, DailyDistillationLedger ledger) =>
        TestEnvironment.WithWorldReady(true, () =>
            Assert.True(DailyDistillationStateStore.TryWrite(WorldNpc(npcName), ledger)));

    /// <summary>当前历史配置下某 NPC 目标日的快照指纹（测试侧独立构建，与生产同源纯函数）。</summary>
    private static string FingerprintOf(string npcName, int targetDay, IReadOnlyList<string> covered)
    {
        return DailyDistillationSnapshotBuilder.Create(
            npcName, targetDay, MemoryManager.GameDayToStardewTime(targetDay),
            DialogueHistoryManager.Instance.GetHistory(npcName), covered.ToList(), isChinese: false)
            .InputFingerprint;
    }

    private static List<string> RowKeysOf(string npcName, int targetDay)
    {
        return DailyDistillationSnapshotBuilder.Create(
            npcName, targetDay, MemoryManager.GameDayToStardewTime(targetDay),
            DialogueHistoryManager.Instance.GetHistory(npcName), new List<string>(), isChinese: false)
            .Rows.Select(r => r.RowKey).ToList();
    }

    /// <summary>GiftPipelineHistoryTests 的通用 Net 字段补齐（INetObject 判定，NetCollection/NetDictionary 覆盖）。</summary>
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

    private static (string Npc, int Day) Key(string npc, int day) => (npc.ToUpperInvariant(), day);

    // ── 三档模式 ──────────────────────────────────────────────────────────

    [Fact]
    public void Probe_Disabled_DoesNotPlanDaily()
    {
        InstallLocationWorld(NewNpc(NpcA));
        SeedHistory(NpcA, (SpeakerType.NPC, "morning chat one", "dialogue", 5, 900),
                           (SpeakerType.NPC, "morning chat two", "dialogue", 5, 910),
                           (SpeakerType.NPC, "morning chat three", "dialogue", 5, 920));
        ModEntry.Config.DailyDistillMode = "Disabled";

        Probe();

        Assert.Empty(Pending());
    }

    [Fact]
    public void Probe_Overnight_PlansOnlyPastDays()
    {
        InstallLocationWorld(NewNpc(NpcA));
        SeedHistory(NpcA, (SpeakerType.NPC, "yesterday chat one", "dialogue", 4, 900),
                           (SpeakerType.NPC, "yesterday chat two", "dialogue", 4, 910),
                           (SpeakerType.NPC, "yesterday chat three", "dialogue", 4, 920),
                           (SpeakerType.NPC, "today chat one", "dialogue", 5, 900),
                           (SpeakerType.NPC, "today chat two", "dialogue", 5, 910),
                           (SpeakerType.NPC, "today chat three", "dialogue", 5, 920));
        ModEntry.Config.DailyDistillMode = "Overnight";

        Probe();

        var pending = Pending();
        Assert.True(pending.ContainsKey(Key(NpcA, 4)));
        Assert.False(pending.ContainsKey(Key(NpcA, 5)));
        Assert.True(pending[Key(NpcA, 4)].IsFinalDaily);
    }

    [Fact]
    public void Probe_Intraday_PlansTodayIntradayAndPastFinal()
    {
        InstallLocationWorld(NewNpc(NpcA));
        SeedHistory(NpcA, (SpeakerType.NPC, "yesterday chat one", "dialogue", 4, 900),
                           (SpeakerType.NPC, "yesterday chat two", "dialogue", 4, 910),
                           (SpeakerType.NPC, "yesterday chat three", "dialogue", 4, 920),
                           (SpeakerType.NPC, "today chat one", "dialogue", 5, 900),
                           (SpeakerType.NPC, "today chat two", "dialogue", 5, 910),
                           (SpeakerType.NPC, "today chat three", "dialogue", 5, 920));

        Probe();

        var pending = Pending();
        Assert.Equal(2, pending.Count);
        Assert.False(pending[Key(NpcA, 5)].IsFinalDaily);   // 今日日内评估
        Assert.True(pending[Key(NpcA, 4)].IsFinalDaily);    // 昨日最终整理

        // 准入阶段零扣费：台账不产生日期状态、尝试计数与 CoveredRowKeys 均未推进。
        Assert.Empty(ReadLedger(NpcA).Days);
    }

    // ── 统一 NPC 统计与键约定 ─────────────────────────────────────────────

    [Fact]
    public void Probe_UnionOfHistoryAndFriendship_UsesUpperCodeNameKeys()
    {
        // Abigail 只在历史中（不在好友表），Alex 只在好友表且无行——
        // 并集必须覆盖历史侧；键为 NPC 代码名大写。
        InstallLocationWorld(NewNpc(NpcA), NewNpc(NpcB));
        AddFriendship(NpcB, 1000);
        SeedHistory(NpcA, (SpeakerType.NPC, "chat one", "dialogue", 5, 900),
                           (SpeakerType.NPC, "chat two", "dialogue", 5, 910),
                           (SpeakerType.NPC, "chat three", "dialogue", 5, 920));

        Probe();

        var pending = Pending();
        Assert.Single(pending);
        var key = pending.Keys.Single();
        Assert.Equal("ABIGAIL", key.Item1);   // 统一大写代码名，不使用展示名
        Assert.Equal(5, key.Item2);
        Assert.Equal(NpcA, pending[key].NpcName);
    }

    [Fact]
    public void Probe_MissingNpc_IsDeferredWithoutBlocking()
    {
        // 历史中的名字无法解析为世界 NPC → Trace 延后，不写入 blocked、不规划。
        InstallLocationWorld();   // 空世界
        SeedHistory(NpcA, (SpeakerType.NPC, "chat one", "dialogue", 5, 900),
                           (SpeakerType.NPC, "chat two", "dialogue", 5, 910),
                           (SpeakerType.NPC, "chat three", "dialogue", 5, 920));

        Probe();

        Assert.Empty(Pending());
        Assert.Empty(Blocked());
    }

    // ── 阈值与合格行 ──────────────────────────────────────────────────────

    [Fact]
    public void Probe_Today_BelowThreshold_DoesNotEnqueue()
    {
        InstallLocationWorld(NewNpc(NpcA));
        SeedHistory(NpcA, (SpeakerType.NPC, "chat one", "dialogue", 5, 900),
                           (SpeakerType.NPC, "chat two", "dialogue", 5, 910));

        Probe();

        Assert.Empty(Pending());
    }

    [Fact]
    public void Probe_Today_OnlyPlayerLines_DoesNotEnqueue()
    {
        // 只有玩家发言不触发：Qualifies = NPC 说话且非礼物。
        InstallLocationWorld(NewNpc(NpcA));
        SeedHistory(NpcA, (SpeakerType.Player, "hello one", "conversation", 5, 900),
                           (SpeakerType.Player, "hello two", "conversation", 5, 910),
                           (SpeakerType.Player, "hello three", "conversation", 5, 920));

        Probe();

        Assert.Empty(Pending());
    }

    [Fact]
    public void Probe_Today_ReachesThreshold_EnqueuesIntradayWithFreshRequest()
    {
        InstallLocationWorld(NewNpc(NpcA));
        AddFriendship(NpcA, 750);
        SeedHistory(NpcA, (SpeakerType.NPC, "chat one", "dialogue", 5, 900),
                           (SpeakerType.NPC, "chat two", "dialogue", 5, 910),
                           (SpeakerType.NPC, "chat three", "dialogue", 5, 920));

        Probe();

        var task = Pending()[Key(NpcA, 5)];
        Assert.False(task.IsFinalDaily);
        Assert.Equal(NpcA, task.NpcDisplayName);
        Assert.Equal(AutoSummaryType.Daily, task.Type);
        Assert.NotNull(task.DailyRequest);
        Assert.Same(task.DailyRequest.Snapshot, task.DailyRequest.Snapshot);
        Assert.Equal(3, task.DailyRequest.Snapshot.QualifyingCount);
        Assert.Equal("today", task.DailyRequest.TargetDateLabel);
        Assert.False(task.DailyRequest.IsFinal);
    }

    [Fact]
    public void Probe_Today_SameFingerprint_SkipsUntilNewMaterialReachesThreshold()
    {
        InstallLocationWorld(NewNpc(NpcA));
        SeedHistory(NpcA, (SpeakerType.NPC, "chat one", "dialogue", 5, 900),
                           (SpeakerType.NPC, "chat two", "dialogue", 5, 910),
                           (SpeakerType.NPC, "chat three", "dialogue", 5, 920));

        // 预置：当前指纹已评估（初次 Empty 已记录），未覆盖行为 0。
        var ledger = new DailyDistillationLedger();
        var day = new DailyDistillationDayState
        {
            TargetDay = 5,
            LastEvaluatedFingerprint = FingerprintOf(NpcA, 5, new List<string>()),
            CoveredRowKeys = RowKeysOf(NpcA, 5)
        };
        ledger.Days[5] = day;
        WriteLedger(NpcA, ledger);

        Probe();
        Assert.Empty(Pending());   // 同指纹 + 无未覆盖行 → 不重复规划

        // 新材料 +3 行（未覆盖 ≥ 阈值）→ 新指纹 → 重新规划。
        var history = (Dictionary<string, List<DialogueHistoryEntry>>)HistoryField.GetValue(DialogueHistoryManager.Instance);
        history[NpcA].Add(new DialogueHistoryEntry(NpcA, "afternoon one", SpeakerType.NPC, new StardewTime(1, Season.Spring, 5, 1400), "dialogue"));
        history[NpcA].Add(new DialogueHistoryEntry(NpcA, "afternoon two", SpeakerType.NPC, new StardewTime(1, Season.Spring, 5, 1410), "dialogue"));
        history[NpcA].Add(new DialogueHistoryEntry(NpcA, "afternoon three", SpeakerType.NPC, new StardewTime(1, Season.Spring, 5, 1420), "dialogue"));

        RequestRebuild();
        Probe();

        Assert.True(Pending().ContainsKey(Key(NpcA, 5)));
    }

    [Fact]
    public void Probe_Today_WithEntryId_GatesOnUncoveredThreshold_AndCarriesExistingContent()
    {
        InstallLocationWorld(NewNpc(NpcA));
        const string cardContent = "I enjoyed our chat today.";
        var rowKeys = new List<string>();
        SeedHistory(NpcA, (SpeakerType.NPC, "chat one", "dialogue", 5, 900),
                           (SpeakerType.NPC, "chat two", "dialogue", 5, 910),
                           (SpeakerType.NPC, "chat three", "dialogue", 5, 920));
        rowKeys.AddRange(RowKeysOf(NpcA, 5));
        SeedCards(NpcA, Card("E1", cardContent, 5));

        var ledger = new DailyDistillationLedger();
        ledger.Days[5] = new DailyDistillationDayState
        {
            TargetDay = 5,
            EntryId = "E1",
            LastContentHash = DailyDistillationSnapshotBuilder.HashText(cardContent),
            CoveredRowKeys = rowKeys,
            LastEvaluatedFingerprint = FingerprintOf(NpcA, 5, rowKeys)
        };
        WriteLedger(NpcA, ledger);

        Probe();
        Assert.Empty(Pending());   // 有 EntryId：未覆盖 0 → 不规划

        // 新增 3 行 → 未覆盖 3 ≥ 阈值 → 更新候选，携带既有卡片正文。
        var history = (Dictionary<string, List<DialogueHistoryEntry>>)HistoryField.GetValue(DialogueHistoryManager.Instance);
        history[NpcA].Add(new DialogueHistoryEntry(NpcA, "evening one", SpeakerType.NPC, new StardewTime(1, Season.Spring, 5, 1900), "dialogue"));
        history[NpcA].Add(new DialogueHistoryEntry(NpcA, "evening two", SpeakerType.NPC, new StardewTime(1, Season.Spring, 5, 1910), "dialogue"));
        history[NpcA].Add(new DialogueHistoryEntry(NpcA, "evening three", SpeakerType.NPC, new StardewTime(1, Season.Spring, 5, 1920), "dialogue"));

        RequestRebuild();
        Probe();

        var task = Pending()[Key(NpcA, 5)];
        Assert.Equal("E1", task.ExistingEntryId);
        Assert.Equal(DailyDistillationSnapshotBuilder.HashText(cardContent), task.ExpectedContentHash);
        Assert.Equal(cardContent, task.DailyRequest.ExistingContent);
        Assert.Equal(3, task.DailyRequest.Snapshot.UncoveredQualifyingCount);
    }

    // ── 最终整理：昨日无台账也检查；过去 7 日窗口 ─────────────────────────

    [Fact]
    public void Probe_Yesterday_NoLedger_StillCheckedWithZeroCost()
    {
        InstallLocationWorld(NewNpc(NpcA));
        SeedHistory(NpcA, (SpeakerType.NPC, "yesterday one", "dialogue", 4, 900),
                           (SpeakerType.NPC, "yesterday two", "dialogue", 4, 910),
                           (SpeakerType.NPC, "yesterday three", "dialogue", 4, 920));

        Probe();

        var pending = Pending();
        Assert.True(pending.ContainsKey(Key(NpcA, 4)));
        Assert.True(pending[Key(NpcA, 4)].IsFinalDaily);
        Assert.Equal("yesterday", pending[Key(NpcA, 4)].DailyRequest.TargetDateLabel);
        Assert.Empty(ReadLedger(NpcA).Days);   // 规划零写入
    }

    [Fact]
    public void Probe_WindowLowerBound_KeepsDayOne_IncludesLedgerDaysInWindow()
    {
        InstallLocationWorld(NewNpc(NpcA));
        // 今日 = 5 → 窗口下界 = max(1, 5-7) = 1；最终候选扫描范围 = 昨日 ∪ 台账窗口内日期。
        // 第 1 日仅在台账有日期状态时参与扫描（无台账的非昨日日期不扫描）。
        SeedHistory(NpcA, (SpeakerType.NPC, "old one", "dialogue", 1, 900),
                           (SpeakerType.NPC, "old two", "dialogue", 1, 910),
                           (SpeakerType.NPC, "old three", "dialogue", 1, 920));
        var ledger = new DailyDistillationLedger();
        ledger.Days[1] = new DailyDistillationDayState { TargetDay = 1 };
        WriteLedger(NpcA, ledger);

        Probe();

        // 第 1 日在窗口内（≥ 下界 1）→ 作为过去日最终候选。
        Assert.True(Pending().ContainsKey(Key(NpcA, 1)));
    }

    // ── 终局关闭（先 TryWrite，后 settled）────────────────────────────────

    [Fact]
    public void Probe_PastDay_WithCard_NoUncovered_ClosesUnchanged()
    {
        InstallLocationWorld(NewNpc(NpcA));
        const string cardContent = "Yesterday was quiet but warm.";
        SeedHistory(NpcA, (SpeakerType.NPC, "yesterday one", "dialogue", 4, 900),
                           (SpeakerType.NPC, "yesterday two", "dialogue", 4, 910));
        var rowKeys = RowKeysOf(NpcA, 4);
        SeedCards(NpcA, Card("E4", cardContent, 4));

        var ledger = new DailyDistillationLedger();
        ledger.Days[4] = new DailyDistillationDayState
        {
            TargetDay = 4,
            EntryId = "E4",
            LastContentHash = DailyDistillationSnapshotBuilder.HashText(cardContent),
            CoveredRowKeys = rowKeys
        };
        WriteLedger(NpcA, ledger);

        Probe();

        var day = ReadLedger(NpcA).Days[4];
        Assert.True(day.FinalizationClosed);
        Assert.Equal("Unchanged", day.FinalizationOutcome);
        Assert.False(Pending().ContainsKey(Key(NpcA, 4)));
    }

    [Fact]
    public void Probe_PastDay_NoCard_BelowThreshold_ClosesBelowThreshold()
    {
        InstallLocationWorld(NewNpc(NpcA));
        SeedHistory(NpcA, (SpeakerType.NPC, "quiet one", "dialogue", 4, 900),
                           (SpeakerType.NPC, "quiet two", "dialogue", 4, 910));

        Probe();

        var ledger = ReadLedger(NpcA);
        Assert.True(ledger.Days.ContainsKey(4));
        Assert.True(ledger.Days[4].FinalizationClosed);
        Assert.Equal("BelowThreshold", ledger.Days[4].FinalizationOutcome);
        Assert.Empty(Pending());
    }

    [Fact]
    public void Probe_PastDay_EmptyEvaluated_NoNewRows_ClosesEmpty()
    {
        InstallLocationWorld(NewNpc(NpcA));
        SeedHistory(NpcA, (SpeakerType.NPC, "chat one", "dialogue", 4, 900),
                           (SpeakerType.NPC, "chat two", "dialogue", 4, 910),
                           (SpeakerType.NPC, "chat three", "dialogue", 4, 920));
        var rowKeys = RowKeysOf(NpcA, 4);

        var ledger = new DailyDistillationLedger();
        ledger.Days[4] = new DailyDistillationDayState
        {
            TargetDay = 4,
            CoveredRowKeys = rowKeys,
            LastEvaluatedFingerprint = FingerprintOf(NpcA, 4, rowKeys)
        };
        WriteLedger(NpcA, ledger);

        Probe();

        var day = ReadLedger(NpcA).Days[4];
        Assert.True(day.FinalizationClosed);
        Assert.Equal("Empty", day.FinalizationOutcome);
    }

    [Fact]
    public void Probe_PastDay_Suspended_ClosesSkippedManual()
    {
        InstallLocationWorld(NewNpc(NpcA));
        SeedHistory(NpcA, (SpeakerType.NPC, "chat one", "dialogue", 4, 900),
                           (SpeakerType.NPC, "chat two", "dialogue", 4, 910),
                           (SpeakerType.NPC, "chat three", "dialogue", 4, 920));

        var ledger = new DailyDistillationLedger();
        ledger.Days[4] = new DailyDistillationDayState { TargetDay = 4, Suspended = true, SuspensionReason = "Edited" };
        WriteLedger(NpcA, ledger);

        Probe();

        var day = ReadLedger(NpcA).Days[4];
        Assert.True(day.FinalizationClosed);
        Assert.Equal("SkippedManual", day.FinalizationOutcome);
        Assert.Empty(Pending());
    }

    [Fact]
    public void Probe_PastDay_FinalBudgetExhausted_ClosesFailedBudget_KeepsCoveredRowKeys()
    {
        InstallLocationWorld(NewNpc(NpcA));
        SeedHistory(NpcA, (SpeakerType.NPC, "chat one", "dialogue", 4, 900),
                           (SpeakerType.NPC, "chat two", "dialogue", 4, 910),
                           (SpeakerType.NPC, "chat three", "dialogue", 4, 920));
        var preservedKeys = new List<string> { DailyDistillationSnapshotBuilder.HashText("row-a") };

        var ledger = new DailyDistillationLedger();
        ledger.Days[4] = new DailyDistillationDayState
        {
            TargetDay = 4,
            FinalAttempts = 2,
            CoveredRowKeys = preservedKeys
        };
        WriteLedger(NpcA, ledger);

        Probe();

        var day = ReadLedger(NpcA).Days[4];
        Assert.True(day.FinalizationClosed);
        Assert.Equal("FailedBudget", day.FinalizationOutcome);
        Assert.Equal(preservedKeys, day.CoveredRowKeys);   // 保留 CoveredRowKeys 原值
        Assert.Empty(Pending());
    }

    [Fact]
    public void Probe_PastDay_WithCard_UncoveredBelowThreshold_StillFinalUpdate()
    {
        // 最后不足阈值尾段仍最终更新：已有自动卡片只要求未覆盖 > 0。
        InstallLocationWorld(NewNpc(NpcA));
        const string cardContent = "A first impression from yesterday.";
        SeedHistory(NpcA, (SpeakerType.NPC, "old row one", "dialogue", 4, 900),
                           (SpeakerType.NPC, "old row two", "dialogue", 4, 910));
        var covered = RowKeysOf(NpcA, 4).Take(1).ToList();
        SeedCards(NpcA, Card("E4", cardContent, 4));

        var ledger = new DailyDistillationLedger();
        ledger.Days[4] = new DailyDistillationDayState
        {
            TargetDay = 4,
            EntryId = "E4",
            LastContentHash = DailyDistillationSnapshotBuilder.HashText(cardContent),
            CoveredRowKeys = covered
        };
        WriteLedger(NpcA, ledger);

        // 此时 QualifyingCount = 2 < 阈值 3，但未覆盖 = 1 > 0 且已有卡片 → 仍最终更新。
        Probe();

        var task = Pending()[Key(NpcA, 4)];
        Assert.True(task.IsFinalDaily);
        Assert.Equal("E4", task.ExistingEntryId);
        Assert.Equal(cardContent, task.DailyRequest.ExistingContent);
        Assert.Equal(1, task.DailyRequest.Snapshot.UncoveredQualifyingCount);
    }

    // ── 共享预算（首发/更新同一预算）────────────────────────────────────

    [Fact]
    public void Probe_Today_SharedBudgetExhausted_WaitsForFinalWithoutEnqueue()
    {
        InstallLocationWorld(NewNpc(NpcA));
        SeedHistory(NpcA, (SpeakerType.NPC, "chat one", "dialogue", 5, 900),
                           (SpeakerType.NPC, "chat two", "dialogue", 5, 910),
                           (SpeakerType.NPC, "chat three", "dialogue", 5, 920));

        var ledger = new DailyDistillationLedger();
        ledger.Days[5] = new DailyDistillationDayState { TargetDay = 5, IntradayAttempts = 2 };
        WriteLedger(NpcA, ledger);

        Probe();

        Assert.False(Pending().ContainsKey(Key(NpcA, 5)));   // 今日等待最终整理
        Assert.Empty(Blocked());

        // 最终整理仍受 FinalAttempts < 2 与共享预算共同约束：预算耗尽 → 过去日期 FailedBudget。
        var ledger4 = new DailyDistillationLedger();
        ledger4.Days[4] = new DailyDistillationDayState { TargetDay = 4, IntradayAttempts = 2 };
        WriteLedger(NpcA, ledger4);
        SeedHistory(NpcA, (SpeakerType.NPC, "yesterday one", "dialogue", 4, 900),
                           (SpeakerType.NPC, "yesterday two", "dialogue", 4, 910),
                           (SpeakerType.NPC, "yesterday three", "dialogue", 4, 920));

        RequestRebuild();
        Probe();

        Assert.True(ReadLedger(NpcA).Days[4].FinalizationClosed);
        Assert.Equal("FailedBudget", ReadLedger(NpcA).Days[4].FinalizationOutcome);
    }

    // ── 重复探测唯一任务与候选替换 ────────────────────────────────────────

    [Fact]
    public void Probe_RepeatedScans_MergeSingleTaskPerKey_AndReplaceStaleData()
    {
        InstallLocationWorld(NewNpc(NpcA));
        SeedHistory(NpcA, (SpeakerType.NPC, "chat one", "dialogue", 5, 900),
                           (SpeakerType.NPC, "chat two", "dialogue", 5, 910),
                           (SpeakerType.NPC, "chat three", "dialogue", 5, 920));

        Probe();
        var first = Pending()[Key(NpcA, 5)];
        Assert.Equal(3, first.DailyRequest.Snapshot.QualifyingCount);

        // 重复扫描：任务不累积。
        RequestRebuild();
        Probe();
        Assert.Equal(1, Pending().Count);

        // 新材料到达后的下一轮规划：同一键替换候选数据，不产生第二个任务。
        var history = (Dictionary<string, List<DialogueHistoryEntry>>)HistoryField.GetValue(DialogueHistoryManager.Instance);
        history[NpcA].Add(new DialogueHistoryEntry(NpcA, "extra one", SpeakerType.NPC, new StardewTime(1, Season.Spring, 5, 1500), "dialogue"));
        history[NpcA].Add(new DialogueHistoryEntry(NpcA, "extra two", SpeakerType.NPC, new StardewTime(1, Season.Spring, 5, 1510), "dialogue"));
        history[NpcA].Add(new DialogueHistoryEntry(NpcA, "extra three", SpeakerType.NPC, new StardewTime(1, Season.Spring, 5, 1520), "dialogue"));

        RequestRebuild();
        Probe();

        Assert.Equal(1, Pending().Count);
        var merged = Pending()[Key(NpcA, 5)];
        Assert.Same(merged, first);   // 同一任务对象（合并），数据被替换
        Assert.Equal(6, merged.DailyRequest.Snapshot.QualifyingCount);
    }

    [Fact]
    public void Probe_ConditionsChangedForPendingKey_WithdrawsWithoutCost()
    {
        InstallLocationWorld(NewNpc(NpcA));
        SeedHistory(NpcA, (SpeakerType.NPC, "chat one", "dialogue", 4, 900),
                           (SpeakerType.NPC, "chat two", "dialogue", 4, 910),
                           (SpeakerType.NPC, "chat three", "dialogue", 4, 920));

        Probe();
        Assert.True(Pending().ContainsKey(Key(NpcA, 4)));

        // 台账手工置为暂停 → 条件已变化 → 撤销候选并关闭 SkippedManual。
        var ledger = ReadLedger(NpcA);
        ledger.Days[4] = new DailyDistillationDayState { TargetDay = 4, Suspended = true, SuspensionReason = "Edited" };
        WriteLedger(NpcA, ledger);

        RequestRebuild();
        Probe();

        Assert.False(Pending().ContainsKey(Key(NpcA, 4)));
        Assert.Equal("SkippedManual", ReadLedger(NpcA).Days[4].FinalizationOutcome);
    }

    // ── 恢复核对接入（保存恢复变化）──────────────────────────────────────

    [Fact]
    public void Probe_ReconcilesPendingCommit_AndPersistsRecovery()
    {
        InstallLocationWorld(NewNpc(NpcA));
        const string cardContent = "Recovered diary content.";
        SeedHistory(NpcA, (SpeakerType.NPC, "chat one", "dialogue", 4, 900),
                           (SpeakerType.NPC, "chat two", "dialogue", 4, 910),
                           (SpeakerType.NPC, "chat three", "dialogue", 4, 920));
        var rowKeys = RowKeysOf(NpcA, 4);
        SeedCards(NpcA, Card("E4", cardContent, 4));

        var ledger = new DailyDistillationLedger();
        ledger.Days[4] = new DailyDistillationDayState
        {
            TargetDay = 4,
            PendingCommit = new DailyPendingCommit
            {
                CommitId = "commit-1",
                EntryId = "E4",
                ExpectedOldHash = "",
                NewContentHash = DailyDistillationSnapshotBuilder.HashText(cardContent),
                InputFingerprint = FingerprintOf(NpcA, 4, rowKeys),
                CoveredRowKeys = rowKeys,
                IsFinal = true
            }
        };
        WriteLedger(NpcA, ledger);

        Probe();

        var day = ReadLedger(NpcA).Days[4];
        Assert.Null(day.PendingCommit);          // 已写未 ack → 恢复所有权并清除 pending
        Assert.Equal("E4", day.EntryId);
        Assert.True(day.FinalizationClosed);     // pending.IsFinal → 恢复为已关闭
        Assert.Equal("Committed", day.FinalizationOutcome);
        Assert.Empty(Pending());                 // 已关闭：不再规划
    }

    [Fact]
    public void Probe_UnreadableLedger_BlocksNpcForSession()
    {
        InstallLocationWorld(NewNpc(NpcA));
        SeedHistory(NpcA, (SpeakerType.NPC, "chat one", "dialogue", 5, 900),
                           (SpeakerType.NPC, "chat two", "dialogue", 5, 910),
                           (SpeakerType.NPC, "chat three", "dialogue", 5, 920));
        var npc = WorldNpc(NpcA);
        TestEnvironment.WithWorldReady(true, () => npc.modData[DailyDistillationStateStore.ModDataKey] = "{invalid json");

        Probe();

        Assert.Empty(Pending());
        Assert.Contains(Key(NpcA, 5), Blocked());
        Assert.Contains(Key(NpcA, 4), Blocked());
    }

    // ── 出队顺序（流程 6）────────────────────────────────────────────────

    [Fact]
    public void OrderedDailyPending_PastDaysAscendingTodayLast_HeartsThenName()
    {
        InstallLocationWorld(NewNpc(NpcA), NewNpc(NpcB));
        AddFriendship(NpcA, 1000);   // Abigail 4 心
        AddFriendship(NpcB, 2000);   // Alex 8 心

        SeedHistory(NpcA, (SpeakerType.NPC, "a yesterday one", "dialogue", 4, 900),
                           (SpeakerType.NPC, "a yesterday two", "dialogue", 4, 910),
                           (SpeakerType.NPC, "a yesterday three", "dialogue", 4, 920),
                           (SpeakerType.NPC, "a today one", "dialogue", 5, 900),
                           (SpeakerType.NPC, "a today two", "dialogue", 5, 910),
                           (SpeakerType.NPC, "a today three", "dialogue", 5, 920));
        SeedHistory(NpcB, (SpeakerType.NPC, "b yesterday one", "dialogue", 4, 900),
                           (SpeakerType.NPC, "b yesterday two", "dialogue", 4, 910),
                           (SpeakerType.NPC, "b yesterday three", "dialogue", 4, 920));

        Probe();

        var ordered = S.OrderedDailyPending();
        Assert.Equal(3, ordered.Count);
        // 过去日（第 4 日）在前且日期升序：同日按心数降序 Alex(8) → Abigail(4)；今日最后。
        Assert.Equal(NpcB, ordered[0].NpcName);
        Assert.Equal(4, ordered[0].TargetDate.DayOfMonth);
        Assert.Equal(NpcA, ordered[1].NpcName);
        Assert.Equal(4, ordered[1].TargetDate.DayOfMonth);
        Assert.Equal(NpcA, ordered[2].NpcName);
        Assert.Equal(5, ordered[2].TargetDate.DayOfMonth);
        Assert.False(ordered[2].IsFinalDaily);
    }

    // ── 准入闸门（流程 7）────────────────────────────────────────────────

    [Fact]
    public void CanAdmit_RequiresTenConsecutiveIdleSeconds()
    {
        for (int i = 0; i < 9; i++)
        {
            Assert.False(CanAdmit());
            Assert.Equal(i + 1, Idle);
        }

        Assert.True(CanAdmit());   // 第 10 个连续空闲事件 → 准入
        Assert.Equal(0, Idle);     // 准入后归零重计

        Assert.False(CanAdmit());  // 下一轮重新累计
        Assert.Equal(1, Idle);
    }

    [Fact]
    public void CanAdmit_InteractionActive_ResetsIdleCounterToZero()
    {
        for (int i = 0; i < 5; i++) Assert.False(CanAdmit());
        Assert.Equal(5, Idle);

        Game1.eventUp = true;      // 任一交互活跃 → 立即归零
        Assert.False(CanAdmit());
        Game1.eventUp = false;

        Assert.Equal(0, Idle);
        for (int i = 0; i < 9; i++) Assert.False(CanAdmit());
        Assert.True(CanAdmit());   // 归零后必须重新连续 10 次
    }

    [Fact]
    public void CanAdmit_EachInteractionConditionBlocks_AndResetsIdle()
    {
        var menuShim = (IClickableMenu)FormatterServices.GetUninitializedObject(typeof(StardewValley.Menus.TitleMenu));   // 具体菜单类型（IClickableMenu 为抽象类）
        var tcs = new TaskCompletionSource<object>(TaskCreationOptions.RunContinuationsAsynchronously);

        // 预热闲置计数，验证每个条件都会阻断并归零。
        void AssertBlocked(Action activate, Action deactivate)
        {
            for (int i = 0; i < 3; i++) Assert.False(CanAdmit());
            activate();
            Assert.False(CanAdmit());
            Assert.Equal(0, Idle);
            deactivate();
        }

        // activeClickableMenu 的 setter 会调 player.Halt()（无头 NRE）——直写后备字段。
        AssertBlocked(
            () => typeof(Game1).GetField("_activeClickableMenu", BindingFlags.Static | BindingFlags.NonPublic)?.SetValue(null, menuShim),
            () => typeof(Game1).GetField("_activeClickableMenu", BindingFlags.Static | BindingFlags.NonPublic)?.SetValue(null, null));
        AssertBlocked(() => Game1.dialogueUp = true, () => Game1.dialogueUp = false);
        AssertBlocked(() => Game1.eventUp = true, () => Game1.eventUp = false);
        AssertBlocked(() => AsyncBuilder.Instance.IsGeneratingDialogue = true, () => AsyncBuilder.Instance.IsGeneratingDialogue = false);
        AssertBlocked(() => AwaitingGenerationField.SetValue(AsyncBuilder.Instance, true),
                      () => AwaitingGenerationField.SetValue(AsyncBuilder.Instance, false));
        AssertBlocked(
            () => ValleytalkReborn.UI.PendingChoiceStore.Set(new ValleytalkReborn.UI.PendingChoiceContext()),
            () => ValleytalkReborn.UI.PendingChoiceStore.Clear());
        AssertBlocked(() => IsProcessingField.SetValue(S, true), () => IsProcessingField.SetValue(S, false));
        AssertBlocked(() => CooldownField.SetValue(S, 7), () => CooldownField.SetValue(S, 0));
        AssertBlocked(() => DrainField.SetValue(S, tcs.Task), () => DrainField.SetValue(S, null));
        AssertBlocked(() => DialogueBuilder.Instance.LlmDisabled = true, () => DialogueBuilder.Instance.LlmDisabled = false);
        AssertBlocked(() => ModEntry.Config.EnableMod = false, () => ModEntry.Config.EnableMod = true);
        AssertBlocked(() => ModEntry.Config.DailyDistillMode = "Disabled", () => ModEntry.Config.DailyDistillMode = "Intraday");

        // 世界未就绪同样阻断（在 WithWorldReady(false) 内直接调用）。
        bool result = true;
        TestEnvironment.WithWorldReady(false, () => result = (bool)CanAdmitMethod.Invoke(S, null));
        Assert.False(result);
        Assert.Equal(0, Idle);

        // 已完成的 drain 不阻断。
        DrainField.SetValue(S, Task.CompletedTask);
        for (int i = 0; i < 9; i++) Assert.False(CanAdmit());
        Assert.True(CanAdmit());
        DrainField.SetValue(S, null);
        Assert.Equal(0, Idle);
    }
}
