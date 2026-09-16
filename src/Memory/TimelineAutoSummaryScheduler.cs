using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

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
}

/// <summary>
/// 时间线自动总结调度器：每日/每周/每季/每年扫描一次，将符合条件的 NPC 入队，
/// 由 OneSecondUpdateTicked 的节流器逐条触发生成。
/// 队列/处理标志/冷却为进程内 Memory；日哨兵写入 player.modData（存档持久）。
/// 仅主机（Context.IsMainPlayer）启用。
/// </summary>
internal sealed class TimelineAutoSummaryScheduler
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
            AgentToolDispatcher.EnqueueMainThread(() => CompleteTask(capturedTask, capturedResult, targetDay));
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
}
