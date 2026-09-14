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
    private static volatile bool _processing;

    private static string _processingSaveFolder;

    private const int MinValidBioLength = 10;   // 镜像 Character.MinValidBioLength（该常量为 private，不得跨类引用）

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

                    DialogueTurns = BuildDialogueTurns(npcName),
                    CharacterLens = BuildCharacterLens(npcName),

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

    // ──────────────────────────────────────────────────────────────
    // 🌟 CharacterLens：角色认知棱镜（MEM-04 新增）
    // 组装静态角色卡分段 → 夜间 batch prompt 的 <persona_lens> 素材。
    // 全部不可用时返回 string.Empty（MEM-05 对空 Lens 省略整块）。
    // ──────────────────────────────────────────────────────────────

    private static BioData TryLoadBio(string npcName)
    {
        string path = $"{VtConstants.BiosPath}/{DialogueCleaner.RemoveDotSuffixes(npcName)}";
        var bio = Game1.content.LoadLocalized<BioData>(path);
        if (bio == null) return null;
        if (string.IsNullOrWhiteSpace(bio.Biography) || bio.Biography.Trim().Length <= MinValidBioLength)
            return null;
        return bio;
    }

    private static string ExtractBioSection(string source, string header)
    {
        if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(header)) return null;

        string normalizedHeader = header.Replace(" ", "").ToLowerInvariant();
        var lines = source.Split('\n');
        var collected = new List<string>();
        bool inSection = false;

        foreach (var rawLine in lines)
        {
            string trimmed = rawLine.Trim();

            // 检测分段头：形如 "[HEADER]"，空白移除后 OrdinalIgnoreCase 比较
            if (!inSection)
            {
                if (trimmed.StartsWith("[") && trimmed.EndsWith("]"))
                {
                    string inner = trimmed[1..^1].Replace(" ", "");
                    if (string.Equals(inner, normalizedHeader, StringComparison.OrdinalIgnoreCase))
                    {
                        inSection = true;
                        continue;
                    }
                }
                continue;
            }

            // 已在段内：遇到下一个分段头则结束
            if (trimmed.StartsWith("[") && trimmed.EndsWith("]"))
                break;

            collected.Add(rawLine);
        }

        if (collected.Count == 0) return null;
        return string.Join("\n", collected).Trim();
    }

    private static string Clip(string value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        string t = value.Trim();
        return t.Length <= maxLength ? t : t[..maxLength];
    }

    private static string BuildCharacterLens(string npcName)
    {
        try
        {
            var bio = TryLoadBio(npcName);
            if (bio == null)
            {
                ModEntry.SMonitor?.Log(
                    $"[NightlyConsolidation] Bio unavailable for [{npcName}], lens skipped.",
                    LogLevel.Trace);
                return string.Empty;
            }

            string identity = ExtractBioSection(bio.Biography, "IDENTITY");
            string passions = ExtractBioSection(bio.Biography, "DAILY PASSIONS");
            string lenses = ExtractBioSection(bio.AmbientBarkPrompt ?? string.Empty, "OBSERVATION LENSES");
            string stage = ProgressStateResolver.ResolveActiveState(
                Game1.getCharacterFromName(npcName),
                bio.ProgressStates);

            // Fallback：identity 与 passions 均缺失 → 截取整段 Biography
            if (identity == null && passions == null)
            {
                identity = Clip(bio.Biography, 400);
                ModEntry.SMonitor?.Log(
                    $"[NightlyConsolidation] Lens fallback to raw biography for [{npcName}].",
                    LogLevel.Debug);
            }

            // 按序拼装（跳过 null/空白段）
            var sections = new List<string>();

            if (!string.IsNullOrWhiteSpace(identity))
                sections.Add($"[IDENTITY]\n{Clip(identity, 300)}");

            if (!string.IsNullOrWhiteSpace(passions))
                sections.Add($"[DAILY PASSIONS]\n{Clip(passions, 300)}");

            if (!string.IsNullOrWhiteSpace(stage))
                sections.Add($"[CURRENT STAGE]\n{Clip(stage, 200)}");

            if (!string.IsNullOrWhiteSpace(lenses))
                sections.Add($"[OBSERVATION LENSES]\n{Clip(lenses, 300)}");

            if (sections.Count == 0) return string.Empty;

            string result = string.Join("\n\n", sections);
            return Clip(result, 900);
        }
        catch (Exception ex)
        {
            ModEntry.SMonitor?.Log(
                $"[NightlyConsolidation] BuildCharacterLens failed for [{npcName}]: {ex.Message}",
                LogLevel.Warn);
            return string.Empty;
        }
    }

    /// <summary>
    /// 构建今日对话回合切片。
    /// 过滤：排除 eavesdrop；保留 Player/NPC 行 + gift 类型行。
    /// 回合组装：遇到 Player 行且当前回合已含 [Farmer] 时封存并开启新回合；
    ///           System/gift 并入当前回合（无回合则开启仅含 [Gift] 的新回合）。
    /// 结果扁平列表 TakeLast(12)；每行 ≤200 字符。
    /// </summary>
    private static List<string> BuildDialogueTurns(string npcName)
    {
        var today = Game1.Date;

        var allToday = DialogueHistoryManager.Instance
            .GetHistory(npcName)
            .Where(e =>
                e.Timestamp.Year == today.Year &&
                e.Timestamp.Season == (Season)today.Season &&
                e.Timestamp.DayOfMonth == today.DayOfMonth)
            .ToList();

        // 候选：非 eavesdrop，且为 Player/NPC 行 或 gift 类型
        var candidates = allToday
            .Where(e => e.DialogueType != "eavesdrop")
            .Where(e =>
                e.SpeakerType == SpeakerType.Player ||
                e.SpeakerType == SpeakerType.NPC ||
                e.DialogueType == "gift")
            .ToList();

        // 回合组装
        var rounds = new List<List<string>>();
        List<string> currentRound = null;

        foreach (var entry in candidates)
        {
            if (entry.SpeakerType == SpeakerType.Player)
            {
                // 当前回合已含 [Farmer] 行 → 封存并开启新回合
                if (currentRound != null && currentRound.Count > 0 && currentRound.Any(l => l.StartsWith("[Farmer]")))
                {
                    rounds.Add(currentRound);
                    currentRound = null;
                }
                currentRound ??= new List<string>();

                string text = FormatLine(entry.Text, 200);
                if (text != null)
                    currentRound.Add($"[Farmer] {text}");
            }
            else if (entry.SpeakerType == SpeakerType.NPC)
            {
                currentRound ??= new List<string>();
                string text = FormatLine(entry.Text, 200);
                if (text != null)
                    currentRound.Add($"[{npcName}] {text}");
            }
            else if (entry.DialogueType == "gift")
            {
                currentRound ??= new List<string>();
                string text = FormatLine(entry.Text, 200);
                if (text != null)
                    currentRound.Add($"[Gift] {text}");
            }
            // System 非 gift 类：忽略（不独立成行，避免噪声）
        }

        // 封存最后一个回合
        if (currentRound != null && currentRound.Count > 0)
        {
            rounds.Add(currentRound);
        }

        // 扁平化并 TakeLast(12)
        var allLines = rounds.SelectMany(r => r).ToList();
        var result = allLines.Count > 12 ? allLines.Skip(allLines.Count - 12).ToList() : allLines;

        ModEntry.SMonitor?.Log(
            $"[NightlyConsolidation] Built {result.Count} dialogue turn line(s) for [{npcName}].",
            LogLevel.Debug);

        return result;
    }

    private static string FormatLine(string text, int maxLen)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        string t = text.Trim();
        if (t.Length > maxLen) t = t[..maxLen];
        return t;
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