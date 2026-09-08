using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

namespace ValleytalkReborn;

internal static class NightlyConsolidationHook
{
    private static bool _processing;

    private static string _processingSaveFolder;

    public static void Register(IModHelper helper)
    {
        MainThreadDispatcher.Register(helper);

        helper.Events.GameLoop.SaveLoaded += OnSaveLoaded;
        helper.Events.GameLoop.Saving += OnSaving;
        helper.Events.GameLoop.DayEnding += OnDayEnding;
        helper.Events.GameLoop.DayStarted += OnDayStarted;
    }

    private static void OnSaveLoaded(
        object sender,
        SaveLoadedEventArgs e)
    {
        _processing = false;
        _processingSaveFolder = null;
        EvolvedTraitManager.OnSaveLoaded();

        ModEntry.SMonitor?.Log(
            "[NightlyConsolidation] Save loaded.",
            LogLevel.Debug);
    }

    private static void OnSaving(
        object sender,
        SavingEventArgs e)
    {
        EvolvedTraitManager.OnSaving();
    }

    private static void OnDayEnding(
        object sender,
        DayEndingEventArgs e)
    {
        if (!ModEntry.Config.EnableNightlyConsolidation)
        {
            ModEntry.SMonitor?.Log(
                "[NightlyConsolidation] Disabled by config.",
                LogLevel.Trace);

            return;
        }

        // 不阻塞游戏主线程等待 LLM。
        if (LlmDialogueService.Instance?.IsRequestInProgress == true)
        {
            ModEntry.SMonitor?.Log(
                "[NightlyConsolidation] A dialogue LLM request is still running. " +
                "Nightly consolidation will be skipped this day.",
                LogLevel.Warn);

            return;
        }

        // Guard: 如果昨天的后台任务还在运行，跳过今天的写入，
        // 避免覆盖尚未处理的 pending 数据。
        if (_processing)
        {
            ModEntry.SMonitor?.Log(
                "[NightlyConsolidation] Previous nightly task is still running. " +
                "Today's work items will be skipped to avoid overwriting pending data.",
                LogLevel.Warn);

            return;
        }

        var npcNames = PerceptionManager.Instance
            .GetInteractedNpcNamesToday()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var workItems = new List<NightlyWorkItem>();

        foreach (string npcName in npcNames)
        {
            try
            {
                // FlushAndGetContextLines 可能会注入 landmark，
                // 所以必须在重新读取 perception bucket 之前执行。
                var streakLines =
                    ConsecutiveTalkTracker.FlushAndGetContextLines(npcName);

                var perceptions = PerceptionManager.Instance
                    .GetFilteredBucketFor(npcName, 20)
                    .ToList();

                if (perceptions.Count < 2 &&
                    !perceptions.Any(p => p.IsLandmark))
                {
                    ModEntry.SMonitor?.Log(
                        $"[NightlyConsolidation] Skipped [{npcName}]: " +
                        $"not enough perceptions.",
                        LogLevel.Debug);

                    continue;
                }

                workItems.Add(new NightlyWorkItem
                {
                    NpcName = npcName,
                    Events = perceptions
                        .Select(p => p.Template)
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .Take(20)
                        .ToList(),

                    DialogueExcerpts = BuildDialogueExcerpts(npcName),

                    RelationshipContext =
                        BuildRelationshipContext(
                            npcName,
                            streakLines)
                });
            }
            catch (Exception ex)
        {
            ModEntry.SMonitor?.Log(
                $"[NightlyConsolidation] Failed to build work item for [{npcName}]: {ex}",
                LogLevel.Error);
        }
        }

        if (workItems.Count == 0)
        {
            ModEntry.SMonitor?.Log(
                "[NightlyConsolidation] No work items created.",
                LogLevel.Debug);

            return;
        }

        try
        {
            NightlyWorkStore.Save(workItems);

            foreach (var item in workItems)
            {
                PerceptionManager.Instance.MarkAsConsolidated(
                    item.NpcName);
            }

            ModEntry.SMonitor?.Log(
                $"[NightlyConsolidation] Queued {workItems.Count} NPC(s) " +
                "for tomorrow.",
                LogLevel.Info);
        }
        catch (Exception ex)
        {
            ModEntry.SMonitor?.Log($"[NightlyConsolidation] Failed to save work items. Perceptions were not cleared: {ex}", LogLevel.Error);
        }
    }

    private static void OnDayStarted(
        object sender,
        DayStartedEventArgs e)
    {
        if (!ModEntry.Config.EnableNightlyConsolidation)
            return;

        if (_processing)
        {
            ModEntry.SMonitor?.Log(
                "[NightlyConsolidation] Processing is already running.",
                LogLevel.Debug);

            return;
        }

        var pending = NightlyWorkStore.Load();

        if (pending.Count == 0)
            return;

        _processing = true;
        _processingSaveFolder = Constants.SaveFolderName;

        ModEntry.SMonitor?.Log(
            $"[NightlyConsolidation] Processing {pending.Count} pending NPC(s).",
            LogLevel.Info);

        _ = Task.Run(async () =>
        {
            try
            {
                bool success =
                    await NightlyConsolidator.RunAsync(pending);

                if (success)
                {
                    // Guard: 验证存档未切换，防止跨存档污染。
                    if (Constants.SaveFolderName != _processingSaveFolder)
                    {
                        ModEntry.SMonitor?.Log(
                            "[NightlyConsolidation] Save file changed during processing. " +
                            "Results discarded to prevent cross-save data loss.",
                            LogLevel.Warn);

                        return;
                    }

                    // 只有整个流程成功后才清理 pending 文件。
                    await MainThreadDispatcher.RunOnMainThreadAsync(
                        NightlyWorkStore.Clear);

                    ModEntry.SMonitor?.Log(
                        "[NightlyConsolidation] Processing completed successfully.",
                        LogLevel.Info);
                }
                else
                {
                    ModEntry.SMonitor?.Log(
                        "[NightlyConsolidation] Processing failed. " +
                        "Pending data will be retried next day.",
                        LogLevel.Warn);
                }
            }
            catch (Exception ex)
            {
                ModEntry.SMonitor?.Log(
                    $"[NightlyConsolidation] Background task failed: {ex}",
                    LogLevel.Error);
            }
            finally
            {
                _processing = false;
                _processingSaveFolder = null;
            }
        });
    }

    private static List<string> BuildDialogueExcerpts(string npcName)
    {
        var today = Game1.Date;

        var allToday = DialogueHistoryManager.Instance
            .GetHistory(npcName)
            .Where(e =>
                e.Timestamp.Year == today.Year &&
                e.Timestamp.Season == (Season)today.Season &&
                e.Timestamp.DayOfMonth == today.DayOfMonth)
            .ToList();

        var excerpts = new List<string>();

        foreach (var entry in allToday
                     .Where(e => e.SpeakerType == SpeakerType.Player)
                     .TakeLast(12))
        {
            if (!string.IsNullOrWhiteSpace(entry.Text))
            {
                string text = entry.Text.Trim();

                if (text.Length > 500)
                    text = text[..500];

                excerpts.Add($"[Farmer]: {text}");
            }
        }

        var latestGift = allToday.LastOrDefault(e =>
            e.DialogueType == "gift" ||
            (!string.IsNullOrWhiteSpace(e.Text) &&
             (
                 e.Text.Contains(
                     "礼物",
                     StringComparison.OrdinalIgnoreCase) ||
                 e.Text.Contains(
                     "gift",
                     StringComparison.OrdinalIgnoreCase) ||
                 e.Text.Contains(
                     "present",
                     StringComparison.OrdinalIgnoreCase)
             )));

        if (latestGift != null &&
            !string.IsNullOrWhiteSpace(latestGift.Text))
        {
            string text = latestGift.Text.Trim();

            if (text.Length > 500)
                text = text[..500];

            excerpts.Add($"[Gift]: {text}");
        }

        ModEntry.SMonitor?.Log(
            $"[NightlyConsolidation] Built {excerpts.Count} dialogue excerpt(s) for [{npcName}].",
            LogLevel.Debug);

        return excerpts;
    }

    private static List<string> BuildRelationshipContext(
        string npcName,
        List<string> streakLines)
    {
        bool isZh =
            LocalizedContentManager.CurrentLanguageCode ==
            LocalizedContentManager.LanguageCode.zh;

        var lines = new List<string>();

        var player = Game1.getPlayerOrEventFarmer();

        if (player?.friendshipData == null)
            return lines;

        if (!player.friendshipData.TryGetValue(
                npcName,
                out var friendship))
        {
            return lines;
        }

        int hearts = friendship.Points / 250;

        lines.Add(isZh
            ? $"当前好感度：{hearts} 心"
            : $"Current friendship: {hearts} hearts");

        if (friendship.IsMarried())
        {
            lines.Add(isZh
                ? "关系状态：已婚"
                : "Relationship: Married");
        }
        else if (friendship.IsEngaged())
        {
            lines.Add(isZh
                ? "关系状态：已订婚"
                : "Relationship: Engaged");
        }
        else if (friendship.IsDating())
        {
            lines.Add(isZh
                ? "关系状态：正在约会"
                : "Relationship: Dating");
        }
        else if (friendship.IsDivorced())
        {
            lines.Add(isZh
                ? "关系状态：已离婚"
                : "Relationship: Divorced");
        }

        if (streakLines != null)
            lines.AddRange(streakLines);

        return lines;
    }
}