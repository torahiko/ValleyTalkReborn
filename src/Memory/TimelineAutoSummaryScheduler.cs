using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using ValleytalkReborn.UI;

namespace ValleytalkReborn;

/// <summary>自动总结类型。</summary>
internal enum AutoSummaryType { Daily, Weekly, Season, Yearly }

/// <summary>一条自动总结任务（轻量描述，源条目在执行期解析，不做快照）。</summary>
internal sealed class AutoSummaryTask
{
    public string NpcName { get; set; } = "";
    public string NpcDisplayName { get; set; } = "";
    public AutoSummaryType Type { get; set; }
    public StardewTime TargetDate { get; set; }   // 各层统一为"昨天"

    // ── D5 扩充字段（均为 Memory）──
    public bool IsFinalDaily { get; set; }
    public long SaveSessionEpoch { get; set; }
    public long ConfigurationEpoch { get; set; }
    public DailyDistillationRequest DailyRequest { get; set; }
    public string ExistingEntryId { get; set; } = "";
    public string ExpectedContentHash { get; set; } = "";
    public string NewEntryId { get; set; } = "";
    public List<string> SourceEntryIds { get; set; } = new();
    public List<string> SourceContentHashes { get; set; } = new();
}

/// <summary>
/// 时间线自动总结调度器：每日/每周/每季/每年扫描一次，将符合条件的 NPC 入队，
/// 由 OneSecondUpdateTicked 的节流器逐条触发生成。
/// 队列/处理标志/冷却为进程内 Memory；日哨兵写入 player.modData（存档持久）。
/// 仅主机（Context.IsMainPlayer）启用。
/// DD406 起为 partial：日记蒸馏候选规划与请求准入辅助共存于本类（DD408 才接入事件循环）。
/// </summary>
internal sealed partial class TimelineAutoSummaryScheduler
{
    public static TimelineAutoSummaryScheduler Instance { get; } = new();

    private const int ThrottleSeconds = 35;
    private const string LastScanDayKey = "valleytalk.autosummary-lastscan";

    private readonly Queue<AutoSummaryTask> _queue = new();
    private bool _isProcessing;
    private int _cooldownSecondsRemaining;

    private TimelineAutoSummaryScheduler() { }

    public void Initialize(IModHelper helper)
    {
        helper.Events.GameLoop.DayStarted -= OnDayStarted;
        helper.Events.GameLoop.SaveLoaded -= OnSaveLoaded;
        helper.Events.GameLoop.OneSecondUpdateTicked -= OnOneSecondUpdateTicked;
        helper.Events.GameLoop.DayStarted += OnDayStarted;
        helper.Events.GameLoop.SaveLoaded += OnSaveLoaded;
        helper.Events.GameLoop.OneSecondUpdateTicked += OnOneSecondUpdateTicked;
    }

    public void Cleanup()
    {
        _queue.Clear();
        _isProcessing = false;
        _cooldownSecondsRemaining = 0;
    }

    private void OnDayStarted(object sender, DayStartedEventArgs e) => TryScanAndEnqueue();
    private void OnSaveLoaded(object sender, SaveLoadedEventArgs e) => TryScanAndEnqueue();

    private void OnOneSecondUpdateTicked(object sender, OneSecondUpdateTickedEventArgs e)
    {
        if (!Context.IsWorldReady || !ModEntry.Config.EnableMod) return;

        if (_cooldownSecondsRemaining > 0)
        {
            _cooldownSecondsRemaining--;
            return;
        }

        if (!_isProcessing && _queue.Count > 0)
        {
            _isProcessing = true;
            _ = ProcessNextTaskAsync();
        }
    }

    private void TryScanAndEnqueue()
    {
        if (!Context.IsWorldReady || !Context.IsMainPlayer || !ModEntry.Config.EnableMod) return;

        var now = new StardewTime(Game1.Date, Game1.timeOfDay);
        int today = MemoryManager.StardewTimeToGameDay(now);
        string sentinel = Game1.player.modData.TryGetValue(LastScanDayKey, out var s) ? s : "";
        if (sentinel == today.ToString())
        {
            ModEntry.SMonitor?.Log($"[TimelineAutoSummary] Scan already ran for game day {today}; skipping.", LogLevel.Trace);
            return;
        }

        var candidates = new List<(NPC npc, string name, string displayName, bool married, int hearts)>();
        foreach (var name in Game1.player.friendshipData.Keys)
        {
            var npc = Game1.getCharacterFromName(name);
            if (npc == null || !npc.IsVillager) continue;
            candidates.Add((npc, name, npc.displayName ?? name,
                SpouseQueryService.Instance.IsMarried(name),
                Game1.player.getFriendshipHeartLevelForNPC(name)));
        }
        candidates = candidates
            .OrderByDescending(c => c.married)
            .ThenByDescending(c => c.hearts)
            .ToList();

        var yesterday = now.AddDays(-1);
        int enqueued = 0;

        foreach (var (npc, name, displayName, married, hearts) in candidates)
        {
            // a. 季报（Day 1 of any season → 覆盖上一整个季节的周报）
            if (ModEntry.Config.AutoSummarizeSeason && now.DayOfMonth == 1)
            {
                _queue.Enqueue(new AutoSummaryTask
                {
                    NpcName = name,
                    NpcDisplayName = displayName,
                    Type = AutoSummaryType.Season,
                    TargetDate = yesterday
                });
                enqueued++;
            }

            // b. 年报（Spring 1, year >= 2 → 覆盖上一整年的季报）
            if (ModEntry.Config.AutoSummarizeYearly && now.Year >= 2 && now.Season == Season.Spring && now.DayOfMonth == 1)
            {
                _queue.Enqueue(new AutoSummaryTask
                {
                    NpcName = name,
                    NpcDisplayName = displayName,
                    Type = AutoSummaryType.Yearly,
                    TargetDate = yesterday
                });
                enqueued++;
            }

            // c. 周报（每周一）
            if (ModEntry.Config.AutoSummarizeWeekly && now.DayOfMonth % 7 == 1)
            {
                _queue.Enqueue(new AutoSummaryTask
                {
                    NpcName = name,
                    NpcDisplayName = displayName,
                    Type = AutoSummaryType.Weekly,
                    TargetDate = yesterday
                });
                enqueued++;
            }

            // d. 日报（昨日与农夫互动 >= 3 次，且昨日无日记）
            if (ModEntry.Config.AutoSummarizeDaily)
            {
                bool hasDailyForYesterday = MemoryManager.Instance.GetTimelineMemories(name, MemoryTier.Daily)
                    .Any(e =>
                    {
                        if (e.CreatedDay <= 0) return false;
                        var t = MemoryManager.GameDayToStardewTime(e.CreatedDay);
                        return t.Year == yesterday.Year && t.Season == yesterday.Season && t.DayOfMonth == yesterday.DayOfMonth;
                    });

                if (!hasDailyForYesterday)
                {
                    int playerLines = DialogueHistoryManager.Instance.GetRecentHistory(name, 30)
                        .Count(e => e.SpeakerType == SpeakerType.Player
                            && e.Timestamp.Year == yesterday.Year
                            && e.Timestamp.Season == yesterday.Season
                            && e.Timestamp.DayOfMonth == yesterday.DayOfMonth);
                    if (playerLines >= 3)
                    {
                        _queue.Enqueue(new AutoSummaryTask
                        {
                            NpcName = name,
                            NpcDisplayName = displayName,
                            Type = AutoSummaryType.Daily,
                            TargetDate = yesterday
                        });
                        enqueued++;
                    }
                }
            }
        }

        Game1.player.modData[LastScanDayKey] = today.ToString();
        if (enqueued > 0)
        {
            ModEntry.SMonitor?.Log($"[TimelineAutoSummary] Enqueued {enqueued} automatic summary task(s) (game day {today}).", LogLevel.Info);
        }
    }

    private async Task ProcessNextTaskAsync()
    {
        AutoSummaryTask task = null;
        try
        {
            if (_queue.Count == 0) return;
            task = _queue.Dequeue();

            // 守卫链：任一命中即丢弃任务（已出队），finally 复位节流。
            if (!Context.IsWorldReady || !Context.IsMainPlayer)
            {
                ModEntry.SMonitor?.Log($"[TimelineAutoSummary] Task [{task.Type}] for [{task.NpcName}] dropped: world not ready or not main player.", LogLevel.Debug);
                return;
            }
            if (DialogueBuilder.Instance?.LlmDisabled == true)
            {
                ModEntry.SMonitor?.Log($"[TimelineAutoSummary] Task [{task.Type}] for [{task.NpcName}] dropped: LLM disabled.", LogLevel.Debug);
                return;
            }
            if (PeriodAlreadyCovered(task))
            {
                ModEntry.SMonitor?.Log($"[TimelineAutoSummary] Task [{task.Type}] for [{task.NpcName}] dropped: period already covered.", LogLevel.Debug);
                return;
            }

            int targetDay = MemoryManager.StardewTimeToGameDay(task.TargetDate);
            MemoryExtractResult result;

            if (task.Type == AutoSummaryType.Daily)
            {
                var existingContents = MemoryManager.Instance.GetTimelineMemories(task.NpcName, MemoryTier.Daily)
                    .Select(e => e.Content).Take(10).ToList();
                result = await MemoryExtractService.ExtractAsync(
                    task.NpcName, task.NpcDisplayName, existingContents, task.TargetDate, CancellationToken.None);
            }
            else
            {
                var sourceEntities = ResolveSourceEntities(task);
                var sourceContents = sourceEntities.Select(e => e.Content).ToList();
                if (sourceContents.Count < 2)
                {
                    ModEntry.SMonitor?.Log($"[TimelineAutoSummary] Task [{task.Type}] for [{task.NpcName}] dropped: only {sourceContents.Count} source(s) (need >= 2).", LogLevel.Debug);
                    return;
                }
                result = await MemoryExtractService.CondenseAsync(
                    task.NpcName, task.NpcDisplayName, sourceContents, CondenseTier(task.Type), CancellationToken.None);
            }

            if (result == null || result.Status != MemoryExtractStatus.Success
                || result.Candidates == null || result.Candidates.Count == 0)
            {
                ModEntry.SMonitor?.Log($"[TimelineAutoSummary] Task [{task.Type}] for [{task.NpcName}] produced no result (status={result?.Status}, error={result?.ErrorDetail}).", LogLevel.Debug);
                return;
            }

            var capturedTask = task;
            var capturedResult = result;
            MainThreadActionQueue.EnqueueMainThread(() => CompleteTask(capturedTask, capturedResult, targetDay));
        }
        catch (Exception ex)
        {
            ModEntry.SMonitor?.Log($"[TimelineAutoSummary] ProcessNextTaskAsync error: {ex}", LogLevel.Warn);
        }
        finally
        {
            _isProcessing = false;
            _cooldownSecondsRemaining = ThrottleSeconds;
        }
    }

    /// <summary>主线程完成区：复查 → 写入 → 归档+删除源 → Toast。</summary>
    private void CompleteTask(AutoSummaryTask task, MemoryExtractResult result, int targetDay)
    {
        try
        {
            if (!Context.IsWorldReady)
            {
                ModEntry.SMonitor?.Log($"[TimelineAutoSummary] CompleteTask [{task.Type}] for [{task.NpcName}] skipped: world not ready.", LogLevel.Debug);
                return;
            }

            var targetTier = TargetTier(task.Type);
            string bestText = result.Candidates.FirstOrDefault();
            if (string.IsNullOrWhiteSpace(bestText)) return;

            var addResult = MemoryManager.Instance.AddTimelineMemory(task.NpcName, bestText, targetTier, null, targetDay);

            if (addResult == MemoryOperationResult.Success)
            {
                var sourceEntities = ResolveSourceEntities(task);
                if (sourceEntities.Count > 0)
                {
                    MemoryManager.Instance.ArchiveTimelineMemories(task.NpcName, sourceEntities, "Distilled");
                    MemoryManager.Instance.RemoveTimelineMemories(task.NpcName, sourceEntities.Select(e => e.Id).ToList());
                }

                string layerName = (task.Type, I18n.IsChinese) switch
                {
                    (AutoSummaryType.Daily, true) => "日记",
                    (AutoSummaryType.Daily, false) => "daily",
                    (AutoSummaryType.Weekly, true) => "周报",
                    (AutoSummaryType.Weekly, false) => "weekly",
                    (AutoSummaryType.Season, true) => "季报",
                    (AutoSummaryType.Season, false) => "season",
                    (AutoSummaryType.Yearly, true) => "年报",
                    (AutoSummaryType.Yearly, false) => "yearly",
                    _ => task.Type.ToString()
                };
                string msg = I18n.IsChinese
                    ? $"【{task.NpcDisplayName}】为你写下了一篇{layerName}回忆……"
                    : $"{task.NpcDisplayName} wrote a new {layerName} memory for you...";
                Game1.addHUDMessage(new HUDMessage(msg, 1));

                ModEntry.SMonitor?.Log($"[TimelineAutoSummary] Completed [{task.Type}] for [{task.NpcName}]: \"{TrimForLog(bestText)}\".", LogLevel.Info);
            }
            else
            {
                ModEntry.SMonitor?.Log($"[TimelineAutoSummary] AddTimelineMemory [{task.Type}] for [{task.NpcName}] returned {addResult}; source entries retained.", LogLevel.Info);
            }
        }
        catch (Exception ex)
        {
            ModEntry.SMonitor?.Log($"[TimelineAutoSummary] CompleteTask error for [{task.NpcName}]: {ex}", LogLevel.Warn);
        }
    }

    /// <summary>按任务类型解析源条目实体（主线程调用）。</summary>
    private List<MemoryEntry> ResolveSourceEntities(AutoSummaryTask task)
    {
        if (task.Type == AutoSummaryType.Daily) return new List<MemoryEntry>();

        if (task.Type == AutoSummaryType.Weekly)
        {
            int targetDayNum = DateToDayNumber(task.TargetDate);
            int windowStart = targetDayNum - 6;
            return MemoryManager.Instance.GetTimelineMemories(task.NpcName, MemoryTier.Daily)
                .Where(e =>
                {
                    if (e.CreatedDay <= 0) return false;
                    int d = DateToDayNumber(MemoryManager.GameDayToStardewTime(e.CreatedDay));
                    return d >= windowStart && d <= targetDayNum;
                })
                .OrderBy(e => e.CreatedDay)
                .ToList();
        }

        if (task.Type == AutoSummaryType.Season)
        {
            return MemoryManager.Instance.GetTimelineMemories(task.NpcName, MemoryTier.Weekly)
                .Where(e =>
                {
                    if (e.CreatedDay <= 0) return false;
                    var t = MemoryManager.GameDayToStardewTime(e.CreatedDay);
                    return t.Year == task.TargetDate.Year && t.Season == task.TargetDate.Season;
                })
                .OrderBy(e => e.CreatedDay)
                .ToList();
        }

        // Yearly
        return MemoryManager.Instance.GetTimelineMemories(task.NpcName, MemoryTier.Chronicle)
            .Where(e =>
            {
                if (e.CreatedDay <= 0) return false;
                return MemoryManager.GameDayToStardewTime(e.CreatedDay).Year == task.TargetDate.Year;
            })
            .OrderBy(e => e.CreatedDay)
            .ToList();
    }

    private bool PeriodAlreadyCovered(AutoSummaryTask task)
    {
        var entries = MemoryManager.Instance.GetTimelineMemories(task.NpcName, TargetTier(task.Type));
        foreach (var e in entries)
        {
            if (e.CreatedDay <= 0) continue;
            var t = MemoryManager.GameDayToStardewTime(e.CreatedDay);
            bool match = task.Type switch
            {
                AutoSummaryType.Daily => t.Year == task.TargetDate.Year && t.Season == task.TargetDate.Season && t.DayOfMonth == task.TargetDate.DayOfMonth,
                AutoSummaryType.Weekly => t.Year == task.TargetDate.Year && t.Season == task.TargetDate.Season && (t.DayOfMonth - 1) / 7 == (task.TargetDate.DayOfMonth - 1) / 7,
                AutoSummaryType.Season => t.Year == task.TargetDate.Year && t.Season == task.TargetDate.Season,
                AutoSummaryType.Yearly => t.Year == task.TargetDate.Year,
                _ => false
            };
            if (match) return true;
        }
        return false;
    }

    private static MemoryTier CondenseTier(AutoSummaryType type) => type switch
    {
        AutoSummaryType.Weekly => MemoryTier.Weekly,
        AutoSummaryType.Season => MemoryTier.Chronicle,
        AutoSummaryType.Yearly => MemoryTier.Yearly,
        _ => MemoryTier.Daily
    };

    private static MemoryTier TargetTier(AutoSummaryType type) => type switch
    {
        AutoSummaryType.Daily => MemoryTier.Daily,
        AutoSummaryType.Weekly => MemoryTier.Weekly,
        AutoSummaryType.Season => MemoryTier.Season,
        AutoSummaryType.Yearly => MemoryTier.Yearly,
        _ => MemoryTier.Daily
    };

    private static int DateToDayNumber(StardewTime t) =>
        (t.Year - 1) * 112 + (int)t.Season * 28 + (t.DayOfMonth - 1);

    private static string TrimForLog(string s) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= 40 ? s : s.Substring(0, 40) + "…");

    // ════════════════════════════════════════════════════════════
    // DD406：日记蒸馏候选规划与请求准入（主线程辅助，DD408 才接入一秒事件循环）。
    // 本票不启用新事件路径、不发送网络、不推进 CoveredRowKeys、不执行扣费；
    // 统一键一律为 (NPC代码名.ToUpperInvariant(), TargetDay)，不使用展示名作身份。
    // ════════════════════════════════════════════════════════════

    /// <summary>规划扫描间隔：每 5 个一秒事件执行一次全量候选扫描。</summary>
    private const int DailyProbeIntervalSeconds = 5;

    /// <summary>准入所需连续空闲的一秒事件数。</summary>
    private const int DailyAdmissionIdleSeconds = 10;

    /// <summary>单个目标日最终整理的尝试上限。</summary>
    private const int DailyFinalAttemptsCap = 2;

    // ── D5 状态与集合字段（均为 Memory）──

    private int _dailyIdleSeconds;
    private int _dailyProbeTicks;
    private bool _dailyNeedsRebuild;
    private Task _dailyTransportDrain = null;   // DD407 执行期赋真实 drain 任务；null/已完成视为无底层 drain
    private long _saveSessionEpoch;
    private long _configurationEpoch;
    private CancellationTokenSource _dailySessionCts;

    /// <summary>统一键元组：(string NpcNameInUpper, int TargetDay)。</summary>
    private readonly Dictionary<(string NpcName, int TargetDay), AutoSummaryTask> _dailyPending = new();
    private readonly HashSet<(string NpcName, int TargetDay)> _dailyInFlight = new();
    private readonly HashSet<(string NpcName, int TargetDay)> _dailyBlocked = new();
    private readonly HashSet<(string NpcName, int TargetDay)> _settledDailyDependencies = new();
    private readonly HashSet<(string NpcName, AutoSummaryType Type, int TargetDay)> _higherPendingKeys = new();
    private readonly Queue<(AutoSummaryTask Task, DailyDistillationRequest Request, MemoryExtractResult Result)> _dailyCompletions = new();

    /// <summary>一基日历日（(Year-1)*112 + Season*28 + DayOfMonth；Game1.Date.TotalDays 为 0 基，需 +1）。</summary>
    private static int DayNumberOf(StardewTime t) => (t.Year - 1) * 112 + (int)t.Season * 28 + t.DayOfMonth;

    /// <summary>
    /// DD406 候选规划（主线程）：每 5 个一秒事件扫描一次——汇总历史 NPC 名与主机好友表的
    /// 去重并集，逐 NPC 解析对象并做恢复式 Reconcile（内部有变化时自行回写一次），
    /// 再按 DailyDistillMode 规划今日日内评估（Intraday）与过去 7 日最终整理候选。
    /// 只入队/撤销/替换候选，绝不推进 CoveredRowKeys、不扣费、不发网络请求。
    /// </summary>
    private void ProbeDailyWork()
    {
        _dailyProbeTicks++;
        // 完成回执（DD407 置位）触发下一 tick 立即重规划；否则按 5 一秒事件的间隔扫描。
        bool rebuildRequested = _dailyNeedsRebuild;
        _dailyNeedsRebuild = false;
        if (!rebuildRequested && (_dailyProbeTicks - 1) % DailyProbeIntervalSeconds != 0) return;

        // 守卫：世界就绪 + 单人/主机 + 模组开启 + Memory 已加载（RECOVERABLE → 延后，零请求零扣费）。
        if (!Context.IsWorldReady || !Context.IsMainPlayer || !ModEntry.Config.EnableMod || !MemoryManager.Instance.IsLoaded)
            return;

        int currentDay = (int)Game1.Date.TotalDays + 1;   // TotalDays 为 0 基（GetDaysPlayed 含 dayOfMonth-1），一基日历日需 +1
        int windowLowerBound = Math.Max(1, currentDay - 7);
        string mode = ModEntry.Config.DailyDistillMode ?? "";

        // ── 流程 1：统一 NPC 统计（GetHistoryNpcNames ∪ 主机 friendshipData.Keys）──
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var historyName in DialogueHistoryManager.Instance.GetHistoryNpcNames())
        {
            if (!string.IsNullOrWhiteSpace(historyName)) names.Add(historyName.Trim());
        }
        foreach (var friendName in Game1.player.friendshipData.Keys)
        {
            if (!string.IsNullOrWhiteSpace(friendName)) names.Add(friendName.Trim());
        }

        foreach (var name in names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
        {
            string upper = name.ToUpperInvariant();

            // 解析对象：不存在 → RECOVERABLE → Trace → 暂跳过（不阻断其他 NPC）。
            var npc = Game1.getCharacterFromName(name);
            if (npc == null)
            {
                ModEntry.SMonitor?.Log($"[TimelineAutoSummary] Daily probe: NPC [{name}] not found; deferring.", LogLevel.Trace);
                continue;
            }

            // 台账不可读 → BUG → Error → 本会话 blocked（零网络）。
            if (!DailyDistillationStateStore.TryRead(npc, out var ledger))
            {
                ModEntry.SMonitor?.Log($"[TimelineAutoSummary] Daily probe: ledger unreadable for [{name}]; NPC blocked for this session.", LogLevel.Error);
                _dailyBlocked.Add((upper, currentDay));
                _dailyBlocked.Add((upper, currentDay - 1));
                continue;
            }

            var dailyEntries = MemoryManager.Instance.GetTimelineMemories(name, MemoryTier.Daily);

            // 恢复核对（有变化时由 Store 内部回写一次，即"保存恢复变化"）。
            DailyDistillationStateStore.Reconcile(npc, ledger, dailyEntries, currentDay);

            // ── 流程 2：三档模式决议 ──
            if (mode.Equals("Disabled", StringComparison.OrdinalIgnoreCase))
                continue;   // Disabled → 不规划 Daily（恢复核对已完成）

            bool isZh = I18n.IsChinese;
            string displayName = npc.displayName ?? name;

            if (mode.Equals("Intraday", StringComparison.OrdinalIgnoreCase))
                EvaluateTodayCandidate(name, upper, displayName, ledger, dailyEntries, currentDay, isZh);
            // Overnight → 只规划过去日；Intraday → 今日日内评估 + 过去日最终整理。

            EvaluatePastDays(name, upper, displayName, npc, ledger, dailyEntries, currentDay, windowLowerBound, isZh);
        }
    }

    /// <summary>
    /// 流程 3：今日日内评估候选。日期未暂停、InputFingerprint 与 LastEvaluatedFingerprint
    /// 不同、有未覆盖合格 NPC 行；无 EntryId 时 QualifyingCount ≥ 阈值，有 EntryId 时
    /// UncoveredQualifyingCount ≥ 阈值；共享预算（首发/更新同一预算）未耗尽。否则不入队。
    /// </summary>
    private void EvaluateTodayCandidate(
        string name,
        string upper,
        string displayName,
        DailyDistillationLedger ledger,
        List<MemoryEntry> dailyEntries,
        int currentDay,
        bool isZh)
    {
        var key = (upper, currentDay);
        if (_dailyInFlight.Contains(key)) return;

        ledger.Days.TryGetValue(currentDay, out var day);
        if (day != null && day.Suspended) return;   // 日期未暂停

        var snapshot = BuildDailySnapshot(name, currentDay, day, isZh);

        if (day != null && string.Equals(day.LastEvaluatedFingerprint, snapshot.InputFingerprint, StringComparison.Ordinal))
            return;   // 本指纹已评估（初次 Empty 已覆盖的行不反复计入阈值）

        if (snapshot.UncoveredQualifyingCount <= 0) return;   // 无未覆盖合格 NPC 行

        int threshold = ModEntry.Config.DailyDistillThreshold;
        bool hasEntry = day != null && !string.IsNullOrEmpty(day.EntryId);
        if (hasEntry ? snapshot.UncoveredQualifyingCount < threshold
                     : snapshot.QualifyingCount < threshold)
            return;   // 未达阈值 → RECOVERABLE → 等待新材料

        // 共享预算：日内 + 最终尝试总和低于当前上限（首发/更新共享同一预算）。
        if ((day?.IntradayAttempts ?? 0) + (day?.FinalAttempts ?? 0) >= ModEntry.Config.DailyMaxRequestsPerNpc)
        {
            if (_settledDailyDependencies.Add(key))
                ModEntry.SMonitor?.Log($"[TimelineAutoSummary] Daily budget exhausted for [{name}] day {currentDay}; waiting for final pass.", LogLevel.Info);
            return;   // 今日等待最终整理
        }

        UpsertDailyCandidate(name, upper, displayName, ledger, day, dailyEntries, currentDay, snapshot, isFinal: false, isZh);
    }

    /// <summary>
    /// 流程 4/5：过去 7 日最终整理候选与终局关闭。昨天历史即使没有台账也检查；
    /// 另检查台账中窗口内未关闭日期。无卡片首次要求 QualifyingCount ≥ 阈值，
    /// 已有自动卡片只要求 UncoveredQualifyingCount &gt; 0；FinalAttempts &lt; 2 且
    /// 未暂停/未关闭时入队。其余按规则关闭（先 TryWrite，成功才算 settled）。
    /// </summary>
    private void EvaluatePastDays(
        string name,
        string upper,
        string displayName,
        NPC npc,
        DailyDistillationLedger ledger,
        List<MemoryEntry> dailyEntries,
        int currentDay,
        int windowLowerBound,
        bool isZh)
    {
        // 候选日集合：昨天（无台账也检查）∪ 台账中窗口内日期（日期升序处理）。
        var candidateDays = new SortedSet<int>();
        if (currentDay - 1 >= windowLowerBound && currentDay - 1 >= 1)
            candidateDays.Add(currentDay - 1);
        foreach (var dayKey in ledger.Days.Keys)
        {
            if (dayKey >= windowLowerBound && dayKey < currentDay)
                candidateDays.Add(dayKey);
        }

        int threshold = ModEntry.Config.DailyDistillThreshold;

        foreach (int targetDay in candidateDays)
        {
            var key = (upper, targetDay);
            if (_dailyBlocked.Contains(key)) continue;

            ledger.Days.TryGetValue(targetDay, out var day);
            if (day != null && day.FinalizationClosed) continue;   // 已关闭

            // 暂停 → 关闭 SkippedManual（不再规划）。
            if (day != null && day.Suspended)
            {
                ClosePastDay(name, upper, npc, ledger, day, targetDay, "SkippedManual");
                continue;
            }

            // 首个游戏日前的日期不调用快照构建（窗口下界已保证 ≥ 1）。
            var snapshot = BuildDailySnapshot(name, targetDay, day, isZh);
            bool hasAutoCard = dailyEntries.Any(e => e != null && e.CreatedDay == targetDay
                    && string.Equals(e.Source, "Timeline", StringComparison.Ordinal))
                || (day != null && !string.IsNullOrEmpty(day.EntryId));

            // 预算：最终尝试上限 + 首发/更新共享预算；耗尽 → 过去日期 FailedBudget 关闭。
            bool budgetExhausted = (day?.FinalAttempts ?? 0) >= DailyFinalAttemptsCap
                || (day?.IntradayAttempts ?? 0) + (day?.FinalAttempts ?? 0) >= ModEntry.Config.DailyMaxRequestsPerNpc;
            if (budgetExhausted)
            {
                if (_settledDailyDependencies.Add(key))
                    ModEntry.SMonitor?.Log($"[TimelineAutoSummary] Daily final budget exhausted for [{name}] day {targetDay}; closing FailedBudget.", LogLevel.Info);
                ClosePastDay(name, upper, npc, ledger, day, targetDay, "FailedBudget");
                continue;
            }

            if (hasAutoCard)
            {
                // 已有自动卡片：只要求未覆盖 &gt; 0（最后不足阈值尾段仍最终更新）。
                if (snapshot.UncoveredQualifyingCount > 0)
                {
                    UpsertDailyCandidate(name, upper, displayName, ledger, day, dailyEntries, targetDay, snapshot, isFinal: true, isZh);
                    continue;
                }

                // 未入队：条件已变化 → 撤销既有候选（零扣费），关闭 Unchanged。
                _dailyPending.Remove(key);
                ClosePastDay(name, upper, npc, ledger, day, targetDay, "Unchanged");
                continue;
            }

            // 无卡片（首次）：当前 fingerprint 已 Empty 评估且无新合格行 → 关闭 Empty；
            // 达阈值 → 最终候选；不足阈值 → 关闭 BelowThreshold。
            bool fingerprintEvaluated = day != null
                && string.Equals(day.LastEvaluatedFingerprint, snapshot.InputFingerprint, StringComparison.Ordinal);
            if (fingerprintEvaluated && snapshot.UncoveredQualifyingCount == 0)
            {
                _dailyPending.Remove(key);
                ClosePastDay(name, upper, npc, ledger, day, targetDay, "Empty");
                continue;
            }

            if (snapshot.QualifyingCount >= threshold)
            {
                UpsertDailyCandidate(name, upper, displayName, ledger, day, dailyEntries, targetDay, snapshot, isFinal: true, isZh);
                continue;
            }

            // 未入队：条件已变化 → 撤销既有候选（零扣费），关闭 BelowThreshold。
            _dailyPending.Remove(key);
            ClosePastDay(name, upper, npc, ledger, day, targetDay, "BelowThreshold");
        }
    }

    /// <summary>目标日快照构建（主线程）：完整保留历史 + 既有 CoveredRowKeys。</summary>
    private static DailyDistillationSnapshot BuildDailySnapshot(string name, int targetDay, DailyDistillationDayState day, bool isZh)
    {
        return DailyDistillationSnapshotBuilder.Create(
            name,
            targetDay,
            MemoryManager.GameDayToStardewTime(targetDay),
            DialogueHistoryManager.Instance.GetHistory(name),
            day?.CoveredRowKeys ?? new List<string>(),
            isZh);
    }

    /// <summary>
    /// 流程 6：按唯一键合并候选——重复探测不累积任务；键已挂起（运行中）不再入第二个
    /// 运行任务；键已存在则替换候选数据（条件已变化 → 替换，零扣费）。
    /// </summary>
    private void UpsertDailyCandidate(
        string name,
        string upper,
        string displayName,
        DailyDistillationLedger ledger,
        DailyDistillationDayState day,
        List<MemoryEntry> dailyEntries,
        int targetDay,
        DailyDistillationSnapshot snapshot,
        bool isFinal,
        bool isZh)
    {
        var key = (upper, targetDay);
        if (_dailyInFlight.Contains(key)) return;   // 运行中的键不再入第二个运行任务

        var request = new DailyDistillationRequest
        {
            Provider = Llm.Instance,                                       // 主线程捕获既有服务引用
            NpcName = name,
            NpcDisplayName = displayName,
            PersonaSlice = MemoryExtractService.BuildPersonaSlice(name),   // 主线程采集
            ExistingContent = ResolveExistingContent(dailyEntries, day, targetDay),
            TargetDateLabel = FormatDailyTargetLabel(targetDay),
            Snapshot = snapshot,
            IsChinese = isZh,
            IsFinal = isFinal,
            TimeoutSeconds = Math.Clamp(ModEntry.Config.LlmTimeoutSeconds, 15, 120)
        };

        if (_dailyPending.TryGetValue(key, out var existing))
        {
            // 唯一键合并：条件已变化 → 替换候选（新资料留待本轮刷新，不累积第二个任务）。
            existing.NpcDisplayName = displayName;
            existing.TargetDate = MemoryManager.GameDayToStardewTime(targetDay);
            existing.IsFinalDaily = isFinal;
            existing.DailyRequest = request;
            existing.ExistingEntryId = day?.EntryId ?? "";
            existing.ExpectedContentHash = day?.LastContentHash ?? "";
            return;
        }

        _dailyPending[key] = new AutoSummaryTask
        {
            NpcName = name,
            NpcDisplayName = displayName,
            Type = AutoSummaryType.Daily,
            TargetDate = MemoryManager.GameDayToStardewTime(targetDay),
            IsFinalDaily = isFinal,
            DailyRequest = request,
            ExistingEntryId = day?.EntryId ?? "",
            ExpectedContentHash = day?.LastContentHash ?? ""
        };
    }

    /// <summary>旧日记正文：优先台账所有权条目，其次该日自动卡片（Source=Timeline）。</summary>
    private static string ResolveExistingContent(List<MemoryEntry> dailyEntries, DailyDistillationDayState day, int targetDay)
    {
        if (day != null && !string.IsNullOrEmpty(day.EntryId))
        {
            var owned = dailyEntries.FirstOrDefault(e => e != null && string.Equals(e.Id, day.EntryId, StringComparison.Ordinal));
            if (owned != null) return owned.Content ?? "";
        }

        var autoCard = dailyEntries.FirstOrDefault(e => e != null && e.CreatedDay == targetDay
            && string.Equals(e.Source, "Timeline", StringComparison.Ordinal));
        return autoCard?.Content ?? "";
    }

    /// <summary>目标日人读标签：今日/昨日用口语标签，更早日期用日历戳。</summary>
    private static string FormatDailyTargetLabel(int targetDay)
    {
        int currentDay = Context.IsWorldReady ? (int)Game1.Date.TotalDays + 1 : 0;
        bool isZh = I18n.IsChinese;
        if (targetDay == currentDay) return isZh ? "今日" : "today";
        if (targetDay == currentDay - 1) return isZh ? "昨日" : "yesterday";
        return MemoryManager.FormatGameDateLabel(MemoryManager.GameDayToStardewTime(targetDay));
    }

    /// <summary>
    /// 流程 5：过去日期终局关闭——写入 FinalizationClosed/Outcome 并先 TryWrite；
    /// 失败不作为已 settled（BUG → Error → 本会话 blocked）。
    /// </summary>
    private void ClosePastDay(
        string name,
        string upper,
        NPC npc,
        DailyDistillationLedger ledger,
        DailyDistillationDayState day,
        int targetDay,
        string outcome)
    {
        if (day == null)
        {
            day = new DailyDistillationDayState { TargetDay = targetDay };
            ledger.Days[targetDay] = day;
        }
        day.FinalizationClosed = true;
        day.FinalizationOutcome = outcome;
        _dailyPending.Remove((upper, targetDay));

        if (!DailyDistillationStateStore.TryWrite(npc, ledger))
        {
            ModEntry.SMonitor?.Log($"[TimelineAutoSummary] Daily close [{outcome}] for [{name}] day {targetDay} failed to persist; NPC blocked for this session.", LogLevel.Error);
            _dailyBlocked.Add((upper, targetDay));
            return;
        }

        _settledDailyDependencies.Add((upper, targetDay));
        ModEntry.SMonitor?.Log($"[TimelineAutoSummary] Daily day {targetDay} for [{name}] closed: {outcome}.", LogLevel.Debug);
    }

    /// <summary>
    /// 流程 6 出队顺序：过去日期最终整理在前（日期升序），今日在后；
    /// 同日按既有配偶优先/心数降序，最后 NPC 代码名排序。主线程调用。
    /// </summary>
    internal List<AutoSummaryTask> OrderedDailyPending()
    {
        int currentDay = Context.IsWorldReady ? (int)Game1.Date.TotalDays + 1 : 0;
        return _dailyPending.Values
            .Select(t => (Task: t, Day: DayNumberOf(t.TargetDate)))
            .OrderBy(pair => pair.Day >= currentDay ? 1 : 0)
            .ThenBy(pair => pair.Day)
            .ThenByDescending(pair => SpouseQueryService.Instance.IsMarried(pair.Task.NpcName))
            .ThenByDescending(pair => Game1.player.getFriendshipHeartLevelForNPC(pair.Task.NpcName))
            .ThenBy(pair => pair.Task.NpcName, StringComparer.Ordinal)
            .Select(pair => pair.Task)
            .ToList();
    }

    /// <summary>
    /// 流程 7：准入闸门（主线程）。任一交互条件不满足 → 闲置计数立即归零并延后；
    /// 连续 DailyAdmissionIdleSeconds 个一秒事件全部空闲才准入一次（准入后归零重计）。
    /// 本方法不发网络、不改游戏状态、不推进任何进度。
    /// </summary>
    private bool CanAdmitDailyWork()
    {
        if (!IsDailyInteractionFree())
        {
            if (_dailyIdleSeconds > 0)
                ModEntry.SMonitor?.Log("[TimelineAutoSummary] Daily admission deferred: interaction active; idle streak reset.", LogLevel.Trace);
            _dailyIdleSeconds = 0;
            return false;
        }

        _dailyIdleSeconds++;
        if (_dailyIdleSeconds < DailyAdmissionIdleSeconds) return false;
        _dailyIdleSeconds = 0;
        return true;
    }

    /// <summary>准入交互条件全量清单（flow 7）。</summary>
    private bool IsDailyInteractionFree()
    {
        if (!Context.IsWorldReady || !Context.IsMainPlayer) return false;                     // 主机世界就绪
        if (!ModEntry.Config.EnableMod) return false;                                         // 模组开启
        if (string.Equals(ModEntry.Config.DailyDistillMode, "Disabled", StringComparison.OrdinalIgnoreCase)) return false;   // Daily 模式允许
        if (DialogueBuilder.Instance?.LlmDisabled == true) return false;                      // LlmDisabled 不为 true
        if (Game1.activeClickableMenu != null) return false;                                  // 无弹窗菜单
        if (Game1.dialogueUp) return false;                                                   // 无对话
        if (Game1.eventUp) return false;                                                      // 无过场事件
        var builder = AsyncBuilder.Instance;
        if (builder != null && (builder.AwaitingGeneration || builder.IsGeneratingDialogue)) return false;   // 无生成中
        if (PendingChoiceStore.TryPeek(out _)) return false;                                  // 无待挂载自定义选择框
        if (_isProcessing) return false;                                                      // 自动调度器无运行任务
        if (_dailyTransportDrain != null && !_dailyTransportDrain.IsCompleted) return false;  // 无底层 drain
        if (_cooldownSecondsRemaining > 0) return false;                                      // 冷却为 0
        return true;
    }

    // ════════════════════════════════════════════════════════════
    // DD407：请求执行、epoch 与恢复式提交。
    // 事务流：主线程重验与捕获 → 预留（TryWrite 成功才发起）→ 后台 GenerateDailyAsync
    //（drain 真实 await 到底，后台侧不触碰任何调度集合/游戏状态）→ EnqueueMainThread
    // → 主线程验证（epoch/跨日/交互暂存）→ prepared journal → CommitDailyTimeline → ack。
    // prepared→commit→ack 全程同步于同一主线程回调内，不被探针打断。
    // ════════════════════════════════════════════════════════════

    /// <summary>
    /// 存档会话失效：递增存档/配置 epoch、取消会话 CTS、清理存档队列/运行键/闲置计数；
    /// 保留未完成 drain 句柄（其回调将因 epoch 不匹配被丢弃）。世界加载后由新 CTS 承载新任务。
    /// </summary>
    public void InvalidateSaveSession()
    {
        _saveSessionEpoch++;
        _configurationEpoch++;   // 会话边界同时刷新配置快照；会话内 GMCM 变化的接线在 DD408
        try
        {
            _dailySessionCts?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 并发取消竞态：句柄即将被替换，忽略。
        }
        _dailySessionCts?.Dispose();
        _dailySessionCts = new CancellationTokenSource();

        _dailyPending.Clear();          // 存档队列
        _dailyCompletions.Clear();      // 旧存档未消费结果不投递给新存档
        _dailyInFlight.Clear();         // 运行键
        _dailyBlocked.Clear();          // 本会话 blocked 随存档切换重置
        _settledDailyDependencies.Clear();
        _dailyIdleSeconds = 0;          // 闲置计数
        _dailyNeedsRebuild = false;
        _isProcessing = false;

        // 只清除已经结束的 drain（句柄身份即本字段自身）；未完成句柄继续由观察者 await。
        if (_dailyTransportDrain != null && _dailyTransportDrain.IsCompleted)
            _dailyTransportDrain = null;
    }

    /// <summary>
    /// 启动一个 Daily 生成任务（主线程）：按 DD406 再次验证（重读台账与快照，重新核验
    /// 所有权/阈值/预算），零扣费完成同步捕获与请求完整性校验，然后复制 ledger 并在
    /// 实际调用 Provider 前 IntradayAttempts 或 FinalAttempts 加 1，TryWrite 成功后才发起。
    /// 一次调用只预留一次；调用后取消或失败不退次数。
    /// </summary>
    private void StartDailyTask(AutoSummaryTask task)
    {
        string upper = task?.NpcName?.ToUpperInvariant() ?? "";
        if (task == null || task.DailyRequest == null || task.DailyRequest.Snapshot == null)
        {
            ModEntry.SMonitor?.Log("[TimelineAutoSummary] Daily start bug: null task or incomplete request capture.", LogLevel.Error);
            if (task != null) _dailyPending.Remove((upper, DayNumberOf(task.TargetDate)));
            return;
        }
        int targetDay = DayNumberOf(task.TargetDate);
        var key = (upper, targetDay);

        // 唯一运行任务约束：同键在飞或已有运行任务 → 内部状态违约 → BUG 升级。
        if (_isProcessing || _dailyInFlight.Contains(key))
        {
            ModEntry.SMonitor?.Log($"[TimelineAutoSummary] Daily start bug: a run is already active (key={key}); refusing to start.", LogLevel.Error);
            _dailyPending.Remove(key);
            return;
        }

        // ── DD406 再次验证：重读台账/快照，重新核验资格；任一失格 → 撤销候选（零扣费）──
        if (!ModEntry.Config.EnableMod
            || !MemoryManager.Instance.IsLoaded
            || string.Equals(ModEntry.Config.DailyDistillMode, "Disabled", StringComparison.OrdinalIgnoreCase))
        {
            ModEntry.SMonitor?.Log($"[TimelineAutoSummary] Daily start withdrawn for [{task.NpcName}] day {targetDay}: mode/config not eligible.", LogLevel.Debug);
            _dailyPending.Remove(key);
            return;
        }

        var npc = Context.IsWorldReady ? Game1.getCharacterFromName(task.NpcName) : null;
        if (npc == null)
        {
            ModEntry.SMonitor?.Log($"[TimelineAutoSummary] Daily start withdrawn for [{task.NpcName}] day {targetDay}: NPC not found.", LogLevel.Debug);
            _dailyPending.Remove(key);
            return;
        }
        if (!DailyDistillationStateStore.TryRead(npc, out var ledger))
        {
            ModEntry.SMonitor?.Log($"[TimelineAutoSummary] Daily start bug: ledger unreadable for [{task.NpcName}]; NPC blocked.", LogLevel.Error);
            _dailyPending.Remove(key);
            _dailyBlocked.Add(key);
            _dailyBlocked.Add((upper, targetDay - 1));
            return;
        }
        ledger.Days.TryGetValue(targetDay, out var day);
        if (day != null && (day.Suspended || day.FinalizationClosed))
        {
            ModEntry.SMonitor?.Log($"[TimelineAutoSummary] Daily start withdrawn for [{task.NpcName}] day {targetDay}: day suspended/closed.", LogLevel.Debug);
            _dailyPending.Remove(key);
            return;
        }

        var dailyEntries = MemoryManager.Instance.GetTimelineMemories(task.NpcName, MemoryTier.Daily);
        bool hasExistingEntry = day != null && !string.IsNullOrEmpty(day.EntryId);
        var snapshot = BuildDailySnapshot(task.NpcName, targetDay, day, I18n.IsChinese);

        if (snapshot.UncoveredQualifyingCount <= 0)
        {
            ModEntry.SMonitor?.Log($"[TimelineAutoSummary] Daily start withdrawn for [{task.NpcName}] day {targetDay}: no uncovered rows.", LogLevel.Debug);
            _dailyPending.Remove(key);
            return;
        }
        int threshold = ModEntry.Config.DailyDistillThreshold;
        if (task.IsFinalDaily)
        {
            // 最终任务（DD406 流程 4）：已有自动卡片只要求未覆盖 > 0；无卡片首次按阈值。
            bool hasAutoCard = hasExistingEntry
                || dailyEntries.Any(e => e != null && e.CreatedDay == targetDay
                    && string.Equals(e.Source, "Timeline", StringComparison.Ordinal));
            if (hasAutoCard ? snapshot.UncoveredQualifyingCount <= 0
                            : snapshot.QualifyingCount < threshold)
            {
                ModEntry.SMonitor?.Log($"[TimelineAutoSummary] Daily start withdrawn for [{task.NpcName}] day {targetDay}: final eligibility unmet.", LogLevel.Debug);
                _dailyPending.Remove(key);
                return;
            }
        }
        else if (hasExistingEntry ? snapshot.UncoveredQualifyingCount < threshold
                                  : snapshot.QualifyingCount < threshold)
        {
            // 日内任务（DD406 流程 3）：无 EntryId 看 QualifyingCount，有 EntryId 看未覆盖阈值。
            ModEntry.SMonitor?.Log($"[TimelineAutoSummary] Daily start withdrawn for [{task.NpcName}] day {targetDay}: below threshold.", LogLevel.Debug);
            _dailyPending.Remove(key);
            return;
        }
        if ((day?.IntradayAttempts ?? 0) + (day?.FinalAttempts ?? 0) >= ModEntry.Config.DailyMaxRequestsPerNpc
            || (task.IsFinalDaily && (day?.FinalAttempts ?? 0) >= DailyFinalAttemptsCap))
        {
            ModEntry.SMonitor?.Log($"[TimelineAutoSummary] Daily start withdrawn for [{task.NpcName}] day {targetDay}: budget exhausted.", LogLevel.Debug);
            _dailyPending.Remove(key);
            return;
        }
        if (hasExistingEntry)
        {
            var owned = dailyEntries.FirstOrDefault(e => e != null && string.Equals(e.Id, day.EntryId, StringComparison.Ordinal));
            if (owned == null || !string.Equals(DailyDistillationSnapshotBuilder.HashText(owned.Content), day.LastContentHash, StringComparison.Ordinal))
            {
                ModEntry.SMonitor?.Log($"[TimelineAutoSummary] Daily start withdrawn for [{task.NpcName}] day {targetDay}: ownership changed.", LogLevel.Debug);
                _dailyPending.Remove(key);
                return;
            }
        }

        // ── 步骤 2：同步捕获（预留之前）——语言/persona/Provider/超时/完整快照/正文 hash/epoch ──
        var request = new DailyDistillationRequest
        {
            Provider = Llm.Instance,                                     // 主线程捕获既有服务引用
            NpcName = task.NpcName,
            NpcDisplayName = npc.displayName ?? task.NpcName,
            PersonaSlice = MemoryExtractService.BuildPersonaSlice(task.NpcName),
            ExistingContent = ResolveExistingContent(dailyEntries, day, targetDay),
            TargetDateLabel = FormatDailyTargetLabel(targetDay),
            Snapshot = snapshot,
            IsChinese = I18n.IsChinese,
            IsFinal = task.IsFinalDaily,
            TimeoutSeconds = Math.Clamp(ModEntry.Config.LlmTimeoutSeconds, 15, 120)
        };
        task.DailyRequest = request;
        task.SaveSessionEpoch = _saveSessionEpoch;
        task.ConfigurationEpoch = _configurationEpoch;
        task.ExistingEntryId = day?.EntryId ?? "";
        task.ExpectedContentHash = day?.LastContentHash ?? "";
        if (string.IsNullOrEmpty(task.ExistingEntryId) && string.IsNullOrEmpty(task.NewEntryId))
            task.NewEntryId = Guid.NewGuid().ToString();   // 首次创建：固定 Guid 作为 NewEntryId

        // ── 步骤 1：预留——复制 ledger，实际调用 Provider 前加次数，TryWrite 成功才发起 ──
        if (day == null)
        {
            day = new DailyDistillationDayState { TargetDay = targetDay };
            ledger.Days[targetDay] = day;
        }
        if (task.IsFinalDaily) day.FinalAttempts++;
        else day.IntradayAttempts++;

        if (!DailyDistillationStateStore.TryWrite(npc, ledger))
        {
            ModEntry.SMonitor?.Log($"[TimelineAutoSummary] Daily start bug: reserve write failed for [{task.NpcName}] day {targetDay}; NPC blocked (zero billing).", LogLevel.Error);
            _dailyPending.Remove(key);
            _dailyBlocked.Add(key);
            return;
        }

        // ── 预留成功：设置运行占用并立即异步执行 ──
        _dailyInFlight.Add(key);
        _dailyPending.Remove(key);
        _isProcessing = true;

        _dailySessionCts ??= new CancellationTokenSource();
        Task<MemoryExtractResult> generationTask = MemoryExtractService.GenerateDailyAsync(request, _dailySessionCts.Token);
        _dailyTransportDrain = generationTask;
        _ = ObserveDailyGenerationAsync(task, request, generationTask);
    }

    /// <summary>
    /// 后台观察者：完整 await GenerateDailyAsync 的真实 Task 到底（不弃等），
    /// 观察并记录底层异常；无论成败都以捕获 task 身份投递主线程完成回调。
    /// 本方法不修改任何调度集合、游戏状态或冷却——后台 finally 不触碰运行标志。
    /// </summary>
    private async Task ObserveDailyGenerationAsync(
        AutoSummaryTask task,
        DailyDistillationRequest request,
        Task<MemoryExtractResult> generationTask)
    {
        MemoryExtractResult result;
        try
        {
            result = await generationTask.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // 观察底层异常并记录；即使切档仍观察（回调将被 epoch 验证丢弃）。
            ModEntry.SMonitor?.Log($"[TimelineAutoSummary] Daily generation fault for [{task.NpcName}]: {ex}", LogLevel.Warn);
            result = new MemoryExtractResult { Status = MemoryExtractStatus.Failed, ErrorDetail = "generation fault: " + ex.Message };
        }

        var capturedTask = task;
        var capturedRequest = request;
        var capturedResult = result;
        MainThreadActionQueue.EnqueueMainThread(() => CompleteDailyTask(capturedTask, capturedRequest, capturedResult));
    }

    /// <summary>
    /// 主线程完成区：验证 epoch/主机/世界/模式/配置/跨日 → 交互活跃则暂存 _dailyCompletions
    /// 待后续空闲消费（重验 epoch，不递归入队、不重生成）→ 非Success/Success 分支处理
    /// → 释放运行键/_isProcessing 并设置 35 个一秒事件冷却（即使格式失败也冷却）。
    /// </summary>
    private void CompleteDailyTask(AutoSummaryTask task, DailyDistillationRequest request, MemoryExtractResult result)
    {
        if (task == null || request == null || result == null)
        {
            ModEntry.SMonitor?.Log("[TimelineAutoSummary] Daily complete bug: null capture payload.", LogLevel.Error);
            return;
        }

        // 旧存档回调：直接丢弃业务结果，不释放新存档运行状态（失效清理已复位）。
        if (task.SaveSessionEpoch != _saveSessionEpoch)
        {
            ModEntry.SMonitor?.Log($"[TimelineAutoSummary] Daily completion for [{task.NpcName}] dropped: stale save session.", LogLevel.Debug);
            return;
        }

        string upper = task.NpcName?.ToUpperInvariant() ?? "";
        int targetDay = DayNumberOf(task.TargetDate);
        var key = (upper, targetDay);

        // 配置 epoch：模式/阈值/预算/超时/语言/Provider 变化 → 丢弃（已预留次数保持，不退）。
        if (task.ConfigurationEpoch != _configurationEpoch)
        {
            ModEntry.SMonitor?.Log($"[TimelineAutoSummary] Daily completion for [{task.NpcName}] day {targetDay} dropped: configuration epoch changed.", LogLevel.Debug);
            ReleaseDailyRun(task);
            return;
        }

        // 当前主机/世界/EnableMod。
        if (!Context.IsWorldReady || !Context.IsMainPlayer || !ModEntry.Config.EnableMod)
        {
            ModEntry.SMonitor?.Log($"[TimelineAutoSummary] Daily completion for [{task.NpcName}] day {targetDay} dropped: world/host/mod not ready.", LogLevel.Debug);
            ReleaseDailyRun(task);
            return;
        }

        // 模式资格。
        if (string.Equals(ModEntry.Config.DailyDistillMode, "Disabled", StringComparison.OrdinalIgnoreCase))
        {
            ModEntry.SMonitor?.Log($"[TimelineAutoSummary] Daily completion for [{task.NpcName}] day {targetDay} dropped: daily mode disabled.", LogLevel.Debug);
            ReleaseDailyRun(task);
            return;
        }

        // 跨日：今日任务目标日必须等于当前日（丢弃待最终规划）；最终任务只允许当前日前 7 日。
        int currentDay = (int)Game1.Date.TotalDays + 1;
        bool outOfWindow = task.IsFinalDaily
            ? targetDay < currentDay - 7 || targetDay >= currentDay
            : targetDay != currentDay;
        if (outOfWindow)
        {
            ModEntry.SMonitor?.Log($"[TimelineAutoSummary] Daily completion for [{task.NpcName}] day {targetDay} dropped: target day out of window (current {currentDay}).", LogLevel.Debug);
            ReleaseDailyRun(task);
            return;
        }

        // 玩家重新进入交互：结果保留 _dailyCompletions，后续一秒事件空闲时消费
        //（不递归入队、不再次生成/计费；消费时重验 epoch）。运行键保持占用以防重复规划。
        if (IsPlayerInteracting())
        {
            _dailyCompletions.Enqueue((task, request, result));
            ModEntry.SMonitor?.Log($"[TimelineAutoSummary] Daily completion for [{task.NpcName}] day {targetDay} deferred: interaction active.", LogLevel.Debug);
            return;
        }

        if (result.Status == MemoryExtractStatus.Success)
            ProcessDailySuccess(task, request, result, targetDay, key);
        else
            ProcessDailyNonSuccess(task, request, result, targetDay, key);

        // 步骤 10：任务有效且真实底层已结束 → 释放运行键/_isProcessing + 35 个一秒事件冷却。
        ReleaseDailyRun(task);
    }

    /// <summary>玩家交互条件子集（完成暂存判定）：菜单/对话/事件/生成中/待挂载选择框。</summary>
    private static bool IsPlayerInteracting()
    {
        if (Game1.activeClickableMenu != null) return true;
        if (Game1.dialogueUp || Game1.eventUp) return true;
        var builder = AsyncBuilder.Instance;
        if (builder != null && (builder.AwaitingGeneration || builder.IsGeneratingDialogue)) return true;
        if (PendingChoiceStore.TryPeek(out _)) return true;
        return false;
    }

    /// <summary>释放运行键/_isProcessing 并设置 35 个一秒事件冷却；清除已结束的本任务 drain 句柄。</summary>
    private void ReleaseDailyRun(AutoSummaryTask task)
    {
        if (task?.NpcName != null)
            _dailyInFlight.Remove((task.NpcName.ToUpperInvariant(), DayNumberOf(task.TargetDate)));
        _isProcessing = false;
        _cooldownSecondsRemaining = ThrottleSeconds;
        if (_dailyTransportDrain != null && _dailyTransportDrain.IsCompleted)
            _dailyTransportDrain = null;   // 真实底层已结束且句柄身份同（唯一运行任务）→ 清除
    }

    /// <summary>步骤 6：非 Success 处理——BUG 暂停升级；Empty 记录评估范围/关闭；Failed/Cancelled 保留进度按剩余预算重规划。</summary>
    private void ProcessDailyNonSuccess(
        AutoSummaryTask task,
        DailyDistillationRequest request,
        MemoryExtractResult result,
        int targetDay,
        (string Npc, int Day) key)
    {
        string detail = result.ErrorDetail ?? "";

        // BUG: 标记 → 日期暂停并升级（调度器不重试）。
        if (detail.StartsWith("BUG:", StringComparison.Ordinal))
        {
            ModEntry.SMonitor?.Log($"[TimelineAutoSummary] Daily bug for [{task.NpcName}] day {targetDay}: {detail}; day suspended.", LogLevel.Error);
            SuspendDailyDay(task.NpcName, targetDay, key, "Bug");
            return;
        }

        switch (result.Status)
        {
            case MemoryExtractStatus.Empty:
                RecordDailyEmptyEvaluation(task, request, targetDay, key);
                break;

            case MemoryExtractStatus.NoHistory:
                // 找不到资料：过去日 NoHistory 关闭（保留原卡片，不编造日记）；今日仅记录，等待最终整理。
                if (task.IsFinalDaily)
                {
                    CloseDayWithOutcome(task.NpcName, targetDay, key, "NoHistory");
                }
                else
                {
                    ModEntry.SMonitor?.Log($"[TimelineAutoSummary] Daily NoHistory for [{task.NpcName}] day {targetDay}; waiting for final pass.", LogLevel.Warn);
                }
                break;

            default:
                // Failed / Cancelled：保留 CoveredRowKeys 及原正文，次数已扣不退；剩余预算内由下一轮规划重试。
                ModEntry.SMonitor?.Log($"[TimelineAutoSummary] Daily generation for [{task.NpcName}] day {targetDay} ended {result.Status}: {detail}; progress kept.", LogLevel.Debug);
                break;
        }
    }

    /// <summary>初次 Empty：一次 TryWrite 记录全部快照 Rows 为已评估范围与 LastEvaluatedFingerprint；最终分支关闭 Empty。不写卡片/HUD。</summary>
    private void RecordDailyEmptyEvaluation(
        AutoSummaryTask task,
        DailyDistillationRequest request,
        int targetDay,
        (string Npc, int Day) key)
    {
        var npc = Context.IsWorldReady ? Game1.getCharacterFromName(task.NpcName) : null;
        if (npc == null || !DailyDistillationStateStore.TryRead(npc, out var ledger))
        {
            ModEntry.SMonitor?.Log($"[TimelineAutoSummary] Daily empty-record bug for [{task.NpcName}] day {targetDay}: ledger unreadable; NPC blocked.", LogLevel.Error);
            _dailyBlocked.Add(key);
            return;
        }

        if (!ledger.Days.TryGetValue(targetDay, out var day))
        {
            day = new DailyDistillationDayState { TargetDay = targetDay };
            ledger.Days[targetDay] = day;
        }

        if (task.IsFinalDaily)
        {
            // 最终分支：关闭 Empty。
            day.FinalizationClosed = true;
            day.FinalizationOutcome = "Empty";
        }
        else
        {
            // 日内分支：全部快照 Rows 记为已评估范围 + 当前指纹（已覆盖行不反复计入阈值）。
            day.CoveredRowKeys = request.Snapshot.Rows.Select(r => r.RowKey).ToList();
            day.LastEvaluatedFingerprint = request.Snapshot.InputFingerprint;
        }

        if (!DailyDistillationStateStore.TryWrite(npc, ledger))
        {
            ModEntry.SMonitor?.Log($"[TimelineAutoSummary] Daily empty-record write failed for [{task.NpcName}] day {targetDay}; NPC blocked.", LogLevel.Error);
            _dailyBlocked.Add(key);
            return;
        }

        ModEntry.SMonitor?.Log($"[TimelineAutoSummary] Daily empty evaluation recorded for [{task.NpcName}] day {targetDay} (final={task.IsFinalDaily}).", LogLevel.Debug);
    }

    /// <summary>
    /// 步骤 7-9：Success——核对卡片所有权 → 持久化 prepared journal → CommitDailyTimeline →
    /// ack（Applied/Unchanged 推进进度并清 pending）→ HUD/日志。任何 prepared/ack 写失败保留
    /// journal 并 block，无成功 HUD。
    /// </summary>
    private void ProcessDailySuccess(
        AutoSummaryTask task,
        DailyDistillationRequest request,
        MemoryExtractResult result,
        int targetDay,
        (string Npc, int Day) key)
    {
        string content = result.Candidates.FirstOrDefault();
        if (string.IsNullOrWhiteSpace(content))
        {
            // Success 却无候选：内部状态违约 → BUG 暂停升级（不使用 catch-all 成功回落）。
            ModEntry.SMonitor?.Log($"[TimelineAutoSummary] Daily bug for [{task.NpcName}] day {targetDay}: success payload without candidate; day suspended.", LogLevel.Error);
            SuspendDailyDay(task.NpcName, targetDay, key, "Bug");
            return;
        }
        string contentHash = DailyDistillationSnapshotBuilder.HashText(content);

        var npc = Context.IsWorldReady ? Game1.getCharacterFromName(task.NpcName) : null;
        if (npc == null || !DailyDistillationStateStore.TryRead(npc, out var ledger))
        {
            ModEntry.SMonitor?.Log($"[TimelineAutoSummary] Daily commit bug for [{task.NpcName}] day {targetDay}: ledger unreadable; NPC blocked.", LogLevel.Error);
            _dailyBlocked.Add(key);
            return;
        }
        ledger.Days.TryGetValue(targetDay, out var day);
        if (day != null && day.Suspended)
        {
            // 生成期间被恢复核对暂停（如玩家编辑）→ RECOVERABLE → 丢弃，零覆盖。
            ModEntry.SMonitor?.Log($"[TimelineAutoSummary] Daily completion for [{task.NpcName}] day {targetDay} dropped: day suspended during generation.", LogLevel.Debug);
            return;
        }

        // ── 步骤 7：卡片所有权核对 ──
        var dailyEntries = MemoryManager.Instance.GetTimelineMemories(task.NpcName, MemoryTier.Daily);
        bool hasExisting = !string.IsNullOrEmpty(task.ExistingEntryId);
        if (hasExisting)
        {
            var owned = dailyEntries.FirstOrDefault(e => e != null && string.Equals(e.Id, task.ExistingEntryId, StringComparison.Ordinal));
            bool ownedIntact = owned != null
                && !string.IsNullOrEmpty(task.ExpectedContentHash)
                && string.Equals(DailyDistillationSnapshotBuilder.HashText(owned.Content ?? ""), task.ExpectedContentHash, StringComparison.Ordinal);
            if (owned == null || !ownedIntact)
            {
                // 玩家内容或 ID 变更 → Info → 持久 Suspended；零覆盖、零正文写入。
                ModEntry.SMonitor?.Log($"[TimelineAutoSummary] Daily card for [{task.NpcName}] day {targetDay} was changed by player; suspending (zero coverage).", LogLevel.Info);
                SuspendDailyDay(task.NpcName, targetDay, key, "Edited");
                return;
            }
        }
        else
        {
            bool foreignCard = dailyEntries.Any(e => e != null && e.CreatedDay == targetDay);
            if (foreignCard)
            {
                ModEntry.SMonitor?.Log($"[TimelineAutoSummary] Daily card for [{task.NpcName}] day {targetDay} appeared during generation; suspending (zero coverage).", LogLevel.Info);
                SuspendDailyDay(task.NpcName, targetDay, key, "UnownedEntry");
                return;
            }
        }

        // ── 持久化 prepared journal（固定 EntryId、old/new hash、指纹、CoveredRowKeys、IsFinal）──
        string entryId = hasExisting ? task.ExistingEntryId : task.NewEntryId;
        var pending = new DailyPendingCommit
        {
            CommitId = Guid.NewGuid().ToString("N"),
            EntryId = entryId,
            ExpectedOldHash = hasExisting ? task.ExpectedContentHash : "",
            NewContentHash = contentHash,
            InputFingerprint = request.Snapshot.InputFingerprint,
            CoveredRowKeys = request.Snapshot.Rows.Select(r => r.RowKey).ToList(),
            IsFinal = task.IsFinalDaily
        };
        if (day == null)
        {
            day = new DailyDistillationDayState { TargetDay = targetDay };
            ledger.Days[targetDay] = day;
        }
        day.PendingCommit = pending;

        if (!DailyDistillationStateStore.TryWrite(npc, ledger))
        {
            // prepared 写入失败 → BUG → block，保留旧进度供恢复；不调用正文提交，无 HUD。
            ModEntry.SMonitor?.Log($"[TimelineAutoSummary] Daily prepared write failed for [{task.NpcName}] day {targetDay}; NPC blocked, no content commit.", LogLevel.Error);
            _dailyBlocked.Add(key);
            return;
        }

        // ── 步骤 8：正文提交 ──
        var commitResult = MemoryManager.Instance.CommitDailyTimeline(new DailyTimelineCommitRequest
        {
            NpcName = task.NpcName,
            TargetDay = targetDay,
            EntryId = entryId,
            IsCreate = !hasExisting,
            ExpectedContentHash = hasExisting ? task.ExpectedContentHash : null,
            NewContent = content
        });

        if (commitResult == null)
        {
            ModEntry.SMonitor?.Log($"[TimelineAutoSummary] Daily commit bug for [{task.NpcName}] day {targetDay}: null commit result; day suspended.", LogLevel.Error);
            SuspendDailyDay(task.NpcName, targetDay, key, "Bug");
            return;
        }

        switch (commitResult.Status)
        {
            case DailyTimelineCommitStatus.Applied:
            case DailyTimelineCommitStatus.Unchanged:
                AckDailyCommit(npc, ledger, day, task, request, targetDay, key, contentHash, commitResult.Status, entryId, hasExisting, content);
                break;

            case DailyTimelineCommitStatus.Conflict:
                // 玩家编辑冲突：按卡片形态区分原因并持久暂停。
                SuspendDailyDay(task.NpcName, targetDay, key, ResolveConflictReason(dailyEntries, task, targetDay));
                ModEntry.SMonitor?.Log($"[TimelineAutoSummary] Daily commit conflict for [{task.NpcName}] day {targetDay}; day suspended, content kept.", LogLevel.Info);
                break;

            case DailyTimelineCommitStatus.CapacityFull:
            case DailyTimelineCommitStatus.Duplicate:
                // 标记本会话 blocked；最终分支关闭；正文保持。
                _dailyBlocked.Add(key);
                if (task.IsFinalDaily)
                    CloseDayWithOutcome(task.NpcName, targetDay, key, commitResult.Status.ToString());
                ModEntry.SMonitor?.Log($"[TimelineAutoSummary] Daily commit {commitResult.Status} for [{task.NpcName}] day {targetDay}; blocked for session, content kept.", LogLevel.Info);
                break;

            case DailyTimelineCommitStatus.StorageFailed:
                // 正文存储失败：journal 已持久化 → 保留；暂停本会话，等待 Supervisor 或读档恢复。
                ModEntry.SMonitor?.Log($"[TimelineAutoSummary] Daily storage failure for [{task.NpcName}] day {targetDay}: {commitResult.ErrorDetail}; journal kept, session blocked.", LogLevel.Error);
                _dailyBlocked.Add(key);
                break;

            case DailyTimelineCommitStatus.Unavailable:
                // 不 ack，待恢复（journal 保留，读档后由恢复式核对处理）。
                ModEntry.SMonitor?.Log($"[TimelineAutoSummary] Daily commit unavailable for [{task.NpcName}] day {targetDay}: {commitResult.ErrorDetail}; journal kept for recovery.", LogLevel.Warn);
                break;

            case DailyTimelineCommitStatus.Invalid:
            default:
                ModEntry.SMonitor?.Log($"[TimelineAutoSummary] Daily commit bug for [{task.NpcName}] day {targetDay}: {commitResult.Status}/{commitResult.ErrorDetail}; day suspended.", LogLevel.Error);
                SuspendDailyDay(task.NpcName, targetDay, key, "Bug");
                break;
        }
    }

    /// <summary>Conflict 原因区分：同日多卡片 → MultipleEntries，否则按玩家编辑 → Edited。</summary>
    private static string ResolveConflictReason(List<MemoryEntry> dailyEntries, AutoSummaryTask task, int targetDay)
    {
        int sameDayCount = dailyEntries.Count(e => e != null && e.CreatedDay == targetDay);
        return sameDayCount > 1 ? "MultipleEntries" : "Edited";
    }

    /// <summary>
    /// ack：Applied/Unchanged → 副本 ledger 设置 EntryId/hash、全部快照 CoveredRowKeys、
    /// LastEvaluatedFingerprint，最终分支关闭 Committed/Unchanged，清 pending 后 TryWrite。
    /// ack 失败保留准备 journal 并 block 直到恢复，无成功 HUD。Applied 才弹 HUD 与成功日志；
    /// Unchanged 只记录日志且推进已评估进度。
    /// </summary>
    private void AckDailyCommit(
        NPC npc,
        DailyDistillationLedger ledger,
        DailyDistillationDayState day,
        AutoSummaryTask task,
        DailyDistillationRequest request,
        int targetDay,
        (string Npc, int Day) key,
        string contentHash,
        DailyTimelineCommitStatus status,
        string entryId,
        bool hasExisting,
        string content)
    {
        day.EntryId = entryId;
        day.LastContentHash = contentHash;   // Unchanged：内容一致 → 与原 hash 相同
        day.CoveredRowKeys = request.Snapshot.Rows.Select(r => r.RowKey).ToList();
        day.LastEvaluatedFingerprint = request.Snapshot.InputFingerprint;
        if (task.IsFinalDaily)
        {
            day.FinalizationClosed = true;
            day.FinalizationOutcome = status == DailyTimelineCommitStatus.Applied ? "Committed" : "Unchanged";
        }
        day.PendingCommit = null;   // 清 pending

        if (!DailyDistillationStateStore.TryWrite(npc, ledger))
        {
            // ack 失败：modData 仍为 prepared 状态 → block 直到恢复；无成功 HUD。
            ModEntry.SMonitor?.Log($"[TimelineAutoSummary] Daily ack write failed for [{task.NpcName}] day {targetDay}; journal kept, NPC blocked until recovery.", LogLevel.Error);
            _dailyBlocked.Add(key);
            return;
        }

        if (status == DailyTimelineCommitStatus.Applied)
        {
            bool isZh = request.IsChinese;   // 按捕获语言
            string message = isZh
                ? $"【{task.NpcDisplayName}】为你写下了一篇日记回忆……"
                : $"{task.NpcDisplayName} wrote a new daily memory for you...";
            Game1.addHUDMessage(new HUDMessage(message, 1));

            string verb = task.IsFinalDaily ? "Finalized" : (hasExisting ? "Updated" : "Committed");
            ModEntry.SMonitor?.Log($"[DailyDistill] {verb} for [{task.NpcName}] day {targetDay}: \"{TrimForLog(content)}\".", LogLevel.Info);
        }
        else
        {
            // Unchanged：内容一致幂等——进度已推进，不新增卡片、不弹 HUD，预算不再增加。
            ModEntry.SMonitor?.Log($"[DailyDistill] Unchanged for [{task.NpcName}] day {targetDay}; identical content, progress advanced without a new card.", LogLevel.Info);
        }
    }

    /// <summary>持久暂停一个目标日（玩家内容/ID 变更、BUG 升级等）；写失败时调用方按约定 block。</summary>
    private void SuspendDailyDay(string npcName, int targetDay, (string Npc, int Day) key, string reason)
    {
        var npc = Context.IsWorldReady ? Game1.getCharacterFromName(npcName) : null;
        if (npc == null || !DailyDistillationStateStore.TryRead(npc, out var ledger))
        {
            ModEntry.SMonitor?.Log($"[TimelineAutoSummary] Daily suspend bug for [{npcName}] day {targetDay}: ledger unreadable; NPC blocked.", LogLevel.Error);
            _dailyBlocked.Add(key);
            return;
        }

        if (!ledger.Days.TryGetValue(targetDay, out var day))
        {
            day = new DailyDistillationDayState { TargetDay = targetDay };
            ledger.Days[targetDay] = day;
        }
        day.Suspended = true;
        day.SuspensionReason = reason;

        if (!DailyDistillationStateStore.TryWrite(npc, ledger))
        {
            ModEntry.SMonitor?.Log($"[TimelineAutoSummary] Daily suspend write failed for [{npcName}] day {targetDay}; NPC blocked.", LogLevel.Error);
            _dailyBlocked.Add(key);
            return;
        }
    }

    /// <summary>过去日终局关闭（NoHistory/CapacityFull/Duplicate 等 outcomes）；写失败时 block。</summary>
    private void CloseDayWithOutcome(string npcName, int targetDay, (string Npc, int Day) key, string outcome)
    {
        var npc = Context.IsWorldReady ? Game1.getCharacterFromName(npcName) : null;
        if (npc == null || !DailyDistillationStateStore.TryRead(npc, out var ledger))
        {
            ModEntry.SMonitor?.Log($"[TimelineAutoSummary] Daily close bug for [{npcName}] day {targetDay}: ledger unreadable; NPC blocked.", LogLevel.Error);
            _dailyBlocked.Add(key);
            return;
        }

        if (!ledger.Days.TryGetValue(targetDay, out var day))
        {
            day = new DailyDistillationDayState { TargetDay = targetDay };
            ledger.Days[targetDay] = day;
        }
        day.FinalizationClosed = true;
        day.FinalizationOutcome = outcome;

        if (!DailyDistillationStateStore.TryWrite(npc, ledger))
        {
            ModEntry.SMonitor?.Log($"[TimelineAutoSummary] Daily close write failed for [{npcName}] day {targetDay}; NPC blocked.", LogLevel.Error);
            _dailyBlocked.Add(key);
            return;
        }
    }
}
