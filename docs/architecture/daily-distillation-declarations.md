# 当日日记提炼：新增声明清单

日期：2026-10-02。状态：Architect 设计声明，尚未实现。

本文件只声明新增接口、状态格式和命名，不是对现有代码的描述。
现有接口依据本会话 Rider MCP 读取；执行工单另见 daily-distillation-tickets.md。
新增声明先登记在这里，再通过 MCP 读取确认，避免把设计接口冒充现有接口。
命名空间均为 ValleytalkReborn；已知源类型沿用现有可见性。

## D1 配置声明

修改 src/Config/ModConfig.cs：

- public string DailyDistillMode { get; set; }，默认 "Intraday"；规范值 "Disabled"、"Overnight"、"Intraday"。
- public int DailyDistillThreshold { get; set; } = 3;
- public int DailyMaxRequestsPerNpc { get; set; } = 2;
- public bool AutoSummarizeDaily { get; set; }，保留 CLR 接口，改为兼容代理，标记 JsonIgnore。
- private bool LegacyAutoSummarizeDaily { set; }，JsonProperty("AutoSummarizeDaily")，只读入旧 JSON。
- private void OnDailyConfigDeserialized(System.Runtime.Serialization.StreamingContext context)，OnDeserialized 回调。
- internal void NormalizeDailyDistillationConfig(StardewModdingAPI.IMonitor monitor);
- private string _dailyDistillMode = "Intraday";
- private bool _dailyModeWasProvided;
- private bool? _legacyDailyEnabled;

以上均为 Config；反序列化存在标志属于 Memory，不写 JSON。
GMCM 新 UI key：configDailyDistillMode、configDailyDistillModeTooltip、configDailyDistillModeDisabled、configDailyDistillModeOvernight、configDailyDistillModeIntraday、configDailyDistillThreshold、configDailyDistillThresholdTooltip、configDailyMaxRequestsPerNpc、configDailyMaxRequestsPerNpcTooltip。

## D2 快照与纯函数声明

新建 src/Memory/DailyDistillationSnapshot.cs：

- internal sealed class DailyDialogueLine
  - public string RowKey { get; init; }
  - public SpeakerType SpeakerType { get; init; }
  - public string Text { get; init; }
  - public string DialogueType { get; init; }
  - public int TimeOfDay { get; init; }
  - public bool Qualifies { get; init; }
- internal sealed class DailyDistillationSnapshot
  - public string NpcName { get; init; }
  - public int TargetDay { get; init; }
  - public string InputFingerprint { get; init; }
  - public int QualifyingCount { get; init; }
  - public int UncoveredQualifyingCount { get; init; }
  - public IReadOnlyList<DailyDialogueLine> Rows { get; init; }
  - public IReadOnlyList<string> PromptLines { get; init; }
  - public bool InputTruncated { get; init; }
- internal static class DailyDistillationSnapshotBuilder
  - internal static DailyDistillationSnapshot Create(string npcName, int targetDay, StardewTime targetDate, IReadOnlyList<DialogueHistoryEntry> entries, IReadOnlyList<string> coveredRowKeys, bool isChinese);
  - internal static string HashText(string text);

修改 src/Models/history/DialogueHistoryManager.cs：
- public List<string> GetHistoryNpcNames();

以上全部为 Memory。对象不持有 NPC、Game1、菜单或可变历史条目引用。

## D3 状态与提交声明

新建 src/Memory/DailyDistillationState.cs：

- internal sealed class DailyDistillationLedger
  - public int Version { get; set; } = 1;
  - public Dictionary<int, DailyDistillationDayState> Days { get; set; } = new();
- internal sealed class DailyDistillationDayState
  - public int TargetDay { get; set; }
  - public string EntryId { get; set; } = "";
  - public string LastContentHash { get; set; } = "";
  - public List<string> CoveredRowKeys { get; set; } = new();
  - public string LastEvaluatedFingerprint { get; set; } = "";
  - public int IntradayAttempts { get; set; }
  - public int FinalAttempts { get; set; }
  - public bool Suspended { get; set; }
  - public string SuspensionReason { get; set; } = "";
  - public bool FinalizationClosed { get; set; }
  - public string FinalizationOutcome { get; set; } = "";
  - public DailyPendingCommit PendingCommit { get; set; }
- internal sealed class DailyPendingCommit
  - public string CommitId { get; set; }
  - public string EntryId { get; set; }
  - public string ExpectedOldHash { get; set; }
  - public string NewContentHash { get; set; }
  - public string InputFingerprint { get; set; }
  - public List<string> CoveredRowKeys { get; set; }
  - public bool IsFinal { get; set; }
- internal static class DailyDistillationStateStore
  - internal const string ModDataKey = "ValleytalkReborn/DailyDistillation/v1";
  - internal static bool TryRead(StardewValley.NPC npc, out DailyDistillationLedger ledger);
  - internal static bool TryWrite(StardewValley.NPC npc, DailyDistillationLedger ledger);
  - internal static void Reconcile(StardewValley.NPC npc, DailyDistillationLedger ledger, IReadOnlyList<MemoryEntry> dailyEntries);

Ledger / DayState / PendingCommit 的字段权威均为 NPC.modData（ModData）；反序列化对象是 Memory 投影，写入成功后才发布。
保存正文沿用现有 TimelineSaveDataKey，不迁移存储；既有 MemoryEntry 字段与持久化范围保留。

新建 src/Memory/DailyTimelineCommit.cs：

- internal enum DailyTimelineCommitStatus { Applied, Unchanged, Conflict, CapacityFull, Duplicate, StorageFailed, Unavailable, Invalid }
- internal sealed class DailyTimelineCommitRequest
  - public string NpcName { get; init; }
  - public int TargetDay { get; init; }
  - public string EntryId { get; init; }
  - public bool IsCreate { get; init; }
  - public string ExpectedContentHash { get; init; }
  - public string NewContent { get; init; }
- internal sealed class DailyTimelineCommitResult
  - public DailyTimelineCommitStatus Status { get; init; }
  - public string EntryId { get; init; }
  - public string ContentHash { get; init; }
  - public string ErrorDetail { get; init; }

修改 src/Memory/MemoryManager.cs：
- internal DailyTimelineCommitResult CommitDailyTimeline(DailyTimelineCommitRequest request);

请求/返回值都是 Memory；此入口写正文的权威仍是现有 SaveData，NPC ModData 的进度由 StateStore 发布。
新提交入口为 Daily 专用；原 AddTimelineMemory / EditTimelineMemory 签名与其他层级调用保留。

## D4 生成声明

新建 src/Memory/DailyDistillationRequest.cs：

- internal sealed class DailyDistillationRequest
  - public Llm Provider { get; init; }
  - public string NpcName { get; init; }
  - public string NpcDisplayName { get; init; }
  - public string PersonaSlice { get; init; }
  - public string ExistingContent { get; init; }
  - public string TargetDateLabel { get; init; }
  - public DailyDistillationSnapshot Snapshot { get; init; }
  - public bool IsChinese { get; init; }
  - public bool IsFinal { get; init; }
  - public int TimeoutSeconds { get; init; }

修改 src/Memory/MemoryExtractService.cs：
- internal static Task<MemoryExtractResult> GenerateDailyAsync(DailyDistillationRequest request, System.Threading.CancellationToken ct);
- internal static MemoryExtractResult ParseDailyResult(string raw, bool isChinese, string npcName, string npcDisplayName);
- internal static (string SystemPrompt, string UserPrompt) BuildDailyPrompts(DailyDistillationRequest request);

以上为 Memory；后台使用捕获的 Provider.RunInferenceAsync；旧 ExtractAsync / CondenseAsync / ExecuteInferenceAsync 不改签名。

## D5 调度声明

修改 src/Memory/TimelineAutoSummaryScheduler.cs：

- public void InvalidateSaveSession();
- private void ProbeDailyWork();
- private bool CanAdmitDailyWork();
- private void StartDailyTask(AutoSummaryTask task);
- private void CompleteDailyTask(AutoSummaryTask task, DailyDistillationRequest request, MemoryExtractResult result);
- private bool HasUnsettledDailyDependency(AutoSummaryTask task);
- private long _saveSessionEpoch;
- private long _configurationEpoch;
- private System.Threading.CancellationTokenSource _dailySessionCts;
- private readonly Dictionary<(string NpcName, int TargetDay), AutoSummaryTask> _dailyPending;
- private readonly HashSet<(string NpcName, int TargetDay)> _dailyInFlight;
- private readonly HashSet<(string NpcName, int TargetDay)> _dailyBlocked;
- private readonly HashSet<(string NpcName, int TargetDay)> _settledDailyDependencies;
- private System.Threading.Tasks.Task _dailyTransportDrain;
- private int _dailyIdleSeconds;
- private int _dailyProbeTicks;
- private bool _dailyNeedsRebuild;
- private readonly HashSet<(string NpcName, AutoSummaryType Type, int TargetDay)> _higherPendingKeys;
- private readonly Queue<(AutoSummaryTask Task, DailyDistillationRequest Request, MemoryExtractResult Result)> _dailyCompletions;

AutoSummaryTask 新增：
- public bool IsFinalDaily { get; set; }
- public long SaveSessionEpoch { get; set; }
- public long ConfigurationEpoch { get; set; }
- public DailyDistillationRequest DailyRequest { get; set; }
- public string ExistingEntryId { get; set; } = "";
- public string ExpectedContentHash { get; set; } = "";
- public string NewEntryId { get; set; } = "";
- public List<string> SourceEntryIds { get; set; } = new();
- public List<string> SourceContentHashes { get; set; } = new();

以上全部为 Memory。每个字典/集合使用规范 NPC 名的大写不变式作为 tuple 键；NPC 展示名独立保留。
_dailyTransportDrain 是进程内请求占用观察句柄，切档不清除未完成句柄；它不授予任何存档写权限。

## D6 升档存储声明

新建 src/Memory/TimelineCondensationCommit.cs：

- internal enum TimelineCondensationCommitStatus { Applied, Unchanged, Conflict, CapacityFull, Duplicate, StorageFailed, Unavailable, Invalid }
- internal sealed class TimelineCondensationCommitRequest
  - public string NpcName { get; init; }
  - public int TargetDay { get; init; }
  - public MemoryTier TargetTier { get; init; }
  - public string EntryId { get; init; }
  - public string NewContent { get; init; }
  - public IReadOnlyList<string> SourceEntryIds { get; init; }
  - public IReadOnlyList<string> SourceContentHashes { get; init; }
- internal sealed class TimelineCondensationCommitResult
  - public TimelineCondensationCommitStatus Status { get; init; }
  - public string EntryId { get; init; }
  - public string ErrorDetail { get; init; }

修改 src/Memory/MemoryManager.cs：
- internal TimelineCondensationCommitResult CommitTimelineCondensation(TimelineCondensationCommitRequest request);

请求、返回与暂存副本为 Memory。既有正文/归档字典的 SaveData 权威保留。
新入口仅供自动周/季/年浓缩使用，原有手动调用签名保持。

## 常量与日志声明

固定策略：连续空闲 10 秒；探测间隔 5 个 OneSecondUpdateTicked；请求结束后冷却 35 秒；每 NPC/目标日次日最终整理至多 2 次请求；最多处理最近 7 个过去游戏日和今日。
统一日期键为既有DateToDayNumber(date)+1，正向一基序号与GameDayToStardewTime一致；当前日、目标日、ledger与新正文共用。基于本会话读取的日期函数数学声明，旧未知日期不迁移。
日间预算包含首发、更新、超时、失败与取消；只在真正调用 Provider 前预留一次。
全局新 Daily 日志前缀：[DailyDistill]。
事件名与主线程派发沿用已有 SMAPI 订阅和 MainThreadActionQueue.EnqueueMainThread(Action action)。
