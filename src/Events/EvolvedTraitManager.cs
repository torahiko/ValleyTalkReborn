using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using StardewModdingAPI;
using StardewValley;

namespace ValleytalkReborn;

internal static class EvolvedTraitManager
{
    private static Dictionary<string, List<string>> _cache;
    private static bool _dirty;
    private static readonly object LockObject = new();

    private static readonly HashSet<string> PendingFoldSet =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly ConcurrentQueue<string> FoldingQueue = new();

    public const int FoldThreshold = 10;

    public const int MaxTraitsPerNpc = 100;
    public const int MaxFoldRetries = 3;

    private static readonly Dictionary<string, int> FoldRetryCount =
        new(StringComparer.OrdinalIgnoreCase);

    private static bool IsChineseLanguage =>
        LocalizedContentManager.CurrentLanguageCode
            .ToString()
            .StartsWith("zh", StringComparison.OrdinalIgnoreCase);

    private static string FilePath =>
        $"data/EvolvedTraits_{Constants.SaveFolderName}.json";

    public static void OnSaveLoaded()
    {
        lock (LockObject)
        {
            ClearPendingFoldState();

            try
            {
                _cache = ModEntry.SHelper.Data
                    .ReadJsonFile<Dictionary<string, List<string>>>(FilePath)
                    ?? CreateCache();

                NormalizeCache();

                _dirty = false;

                RequeueFoldingTargets();

                ModEntry.SMonitor?.Log(
                    $"[EvolvedTraitManager] Cache loaded. NPCs: {_cache.Count}.",
                    LogLevel.Debug);
            }
            catch (Exception ex)
            {
                ModEntry.SMonitor?.Log(
                    $"[EvolvedTraitManager] Failed to load cache. A new cache will be used. Error: {ex}",
                    LogLevel.Error);

                _cache = CreateCache();
                _dirty = false;
            }
        }
    }

    public static void OnSaving()
    {
        lock (LockObject)
        {
            if (!_dirty)
            {
                ModEntry.SMonitor?.Log(
                    "[EvolvedTraitManager] Saving skipped because cache is clean.",
                    LogLevel.Trace);

                return;
            }

            try
            {
                ModEntry.SHelper.Data.WriteJsonFile(FilePath, _cache);
                _dirty = false;

                ModEntry.SMonitor?.Log(
                    $"[EvolvedTraitManager] Cache flushed to disk. NPCs: {_cache.Count}.",
                    LogLevel.Debug);
            }
            catch (Exception ex)
            {
                ModEntry.SMonitor?.Log(
                    $"[EvolvedTraitManager] Save failed; will retry next save: {ex}",
                    LogLevel.Warn);
            }
        }
    }

    public static void AddTrait(string npcName, string trait)
    {
        npcName = NormalizeString(npcName);
        trait = NormalizeString(trait);

        if (string.IsNullOrWhiteSpace(npcName) ||
            string.IsNullOrWhiteSpace(trait))
        {
            ModEntry.SMonitor?.Log(
                "[EvolvedTraitManager] AddTrait ignored because NPC name or trait was empty.",
                LogLevel.Trace);

            return;
        }

        if (trait.Length > 200)
        {
            trait = trait[..200];

            ModEntry.SMonitor?.Log(
                $"[EvolvedTraitManager] Trait truncated for [{npcName}].",
                LogLevel.Debug);
        }

        lock (LockObject)
        {
            EnsureLoaded();

            if (!_cache.TryGetValue(npcName, out var traits) ||
                traits == null)
            {
                traits = new List<string>();
                _cache[npcName] = traits;
            }

            // Guard: 硬上限保护，防止 traits 无限增长。
            // 当夜间整合被禁用或折叠持续失败时，traits 可能累积。
            if (traits.Count >= MaxTraitsPerNpc)
            {
                traits.RemoveAt(0);

                ModEntry.SMonitor?.Log(
                    $"[EvolvedTraitManager] Hard limit reached for [{npcName}]. Oldest trait removed.",
                    LogLevel.Warn);
            }

            if (traits.Any(t =>
                    string.Equals(t, trait, StringComparison.OrdinalIgnoreCase)))
            {
                ModEntry.SMonitor?.Log(
                    $"[EvolvedTraitManager] Duplicate trait ignored for [{npcName}]: {trait}",
                    LogLevel.Debug);

                return;
            }

            traits.Add(trait);
            _dirty = true;

            ModEntry.SMonitor?.Log(
                $"[EvolvedTraitManager] Trait added for [{npcName}]. Count: {traits.Count}. Text: \"{trait}\"",
                LogLevel.Info);

            QueueFoldIfNeeded(npcName);
        }
    }

    public static void ReplaceFoldedTraits(
        string npcName,
        List<string> folded,
        List<string> originalSnapshot)
    {
        npcName = NormalizeString(npcName);

        if (string.IsNullOrWhiteSpace(npcName) ||
            folded == null ||
            folded.Count == 0)
        {
            ModEntry.SMonitor?.Log(
                $"[EvolvedTraitManager] ReplaceFoldedTraits ignored for [{npcName}]. Invalid result.",
                LogLevel.Warn);

            RequeueFoldTarget(npcName);
            return;
        }

        lock (LockObject)
        {
            EnsureLoaded();

            if (!_cache.TryGetValue(npcName, out var current) ||
                current == null)
            {
                current = new List<string>();
            }

            var originalSet = new HashSet<string>(
                originalSnapshot ?? new List<string>(),
                StringComparer.OrdinalIgnoreCase);

            var merged = new List<string>();
            var seen = new HashSet<string>(
                StringComparer.OrdinalIgnoreCase);

            foreach (string item in folded)
            {
                string value = NormalizeString(item);

                if (!string.IsNullOrWhiteSpace(value) &&
                    value.Length <= 200 &&
                    seen.Add(value))
                {
                    merged.Add(value);
                }
            }

            int newAdditionCount = 0;

            foreach (string item in current)
            {
                string value = NormalizeString(item);

                if (string.IsNullOrWhiteSpace(value))
                    continue;

                if (!originalSet.Contains(value))
                {
                    newAdditionCount++;
                }

                if (seen.Add(value))
                {
                    merged.Add(value);
                }
            }

            _cache[npcName] = merged;
            _dirty = true;

            // Guard: 折叠重试次数上限，防止 LLM 持续失败时无限重试。
            FoldRetryCount.TryGetValue(npcName, out int count);
            count++;

            if (count > MaxFoldRetries)
            {
                PendingFoldSet.Remove(npcName);
                FoldRetryCount.Remove(npcName);

                ModEntry.SMonitor?.Log(
                    $"[EvolvedTraitManager] Fold retry limit reached for [{npcName}]. " +
                    "Traits will not be folded this session.",
                    LogLevel.Warn);

                return;
            }

            FoldRetryCount[npcName] = count;
            PendingFoldSet.Remove(npcName);

            ModEntry.SMonitor?.Log(
                $"[EvolvedTraitManager] Traits folded for [{npcName}]. " +
                $"Original: {current.Count}, Folded: {folded.Count}, " +
                $"New additions: {newAdditionCount}, Final: {merged.Count}.",
                LogLevel.Info);

            // 折叠期间新增数据后，合并结果仍然达到阈值，需要再次排队。
            QueueFoldIfNeeded(npcName);
        }
    }

    public static List<string> GetTraits(string npcName)
    {
        npcName = NormalizeString(npcName);

        lock (LockObject)
        {
            EnsureLoaded();

            if (!_cache.TryGetValue(npcName, out var traits) ||
                traits == null)
            {
                return new List<string>();
            }

            return new List<string>(traits);
        }
    }

    public static string GetPromptBlock(string npcName)
    {
        var traits = GetTraits(npcName);

        if (traits.Count == 0)
            return null;

        bool isZh = IsChineseLanguage;
        var sb = new StringBuilder();

        sb.AppendLine(isZh
            ? "### 对农夫的长期印象（并非本次对话中发生的事，是你长期形成的背景认知，供当前对话参考。）"
            : "### IMPRESSIONS OF THE FARMER (background impressions formed over time, not something that just happened)");

        sb.AppendLine("<farmer_impressions>");

        foreach (string trait in traits)
        {
            if (!string.IsNullOrWhiteSpace(trait))
                sb.AppendLine($"- {trait}");
        }

        sb.AppendLine("</farmer_impressions>");
        return sb.ToString();
    }

    public static string GetPromptBlock(
        string npcName,
        DialogueContext context)
    {
        var traits = GetTraits(npcName);

        if (traits.Count == 0)
            return null;

        bool isZh = IsChineseLanguage;
        var contextKeywords = BuildContextKeywords(context, npcName)
            .Where(k => !string.IsNullOrWhiteSpace(k))
            .Select(k => k.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var sorted = traits
            .Select((trait, index) => new
            {
                Trait = trait,
                Index = index,
                Score = contextKeywords.Count(keyword =>
                    trait.IndexOf(
                        keyword,
                        StringComparison.OrdinalIgnoreCase) >= 0)
            })
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Index)
            .Take(5)
            .Select(x => x.Trait)
            .ToList();

        var sb = new StringBuilder();

        sb.AppendLine(isZh
            ? "### 对农夫的长期印象（并非本次对话中发生的事，是你长期形成的背景认知，供当前对话参考。）"
            : "### IMPRESSIONS OF THE FARMER (background impressions formed over time, not something that just happened)");

        sb.AppendLine("<farmer_impressions>");

        foreach (string trait in sorted)
            sb.AppendLine($"- {trait}");

        sb.AppendLine("</farmer_impressions>");
        return sb.ToString();
    }

    public static bool TryDequeueFoldTarget(out string npcName)
    {
        bool result = FoldingQueue.TryDequeue(out npcName);

        if (result)
        {
            ModEntry.SMonitor?.Log(
                $"[EvolvedTraitManager] Fold target dequeued: {npcName}. Remaining: {FoldingQueue.Count}.",
                LogLevel.Debug);
        }

        return result;
    }

    public static void RequeueFoldTarget(string npcName)
    {
        npcName = NormalizeString(npcName);

        if (string.IsNullOrWhiteSpace(npcName))
            return;

        lock (LockObject)
        {
            EnsureLoaded();

            // Guard: 折叠重试次数上限，防止 LLM 持续失败时无限重试。
            FoldRetryCount.TryGetValue(npcName, out int count);
            count++;

            if (count > MaxFoldRetries)
            {
                PendingFoldSet.Remove(npcName);
                FoldRetryCount.Remove(npcName);

                ModEntry.SMonitor?.Log(
                    $"[EvolvedTraitManager] Fold retry limit reached for [{npcName}]. " +
                    "Traits will not be folded this session.",
                    LogLevel.Warn);

                return;
            }

            FoldRetryCount[npcName] = count;
            PendingFoldSet.Remove(npcName);

            ModEntry.SMonitor?.Log(
                $"[EvolvedTraitManager] Preparing fold retry for [{npcName}] (attempt {count}/{MaxFoldRetries}).",
                LogLevel.Debug);

            QueueFoldIfNeeded(npcName);
        }
    }

    private static void QueueFoldIfNeeded(string npcName)
    {
        if (!_cache.TryGetValue(npcName, out var traits) ||
            traits == null ||
            traits.Count < FoldThreshold)
        {
            return;
        }

        if (PendingFoldSet.Add(npcName))
        {
            FoldingQueue.Enqueue(npcName);

            ModEntry.SMonitor?.Log(
                $"[EvolvedTraitManager] Fold queued for [{npcName}]. Trait count: {traits.Count}.",
                LogLevel.Debug);
        }
    }

    private static void RequeueFoldingTargets()
    {
        int count = 0;

        foreach (var pair in _cache)
        {
            if (pair.Value == null ||
                pair.Value.Count < FoldThreshold)
            {
                continue;
            }

            if (PendingFoldSet.Add(pair.Key))
            {
                FoldingQueue.Enqueue(pair.Key);
                count++;
            }
        }

        if (count > 0)
        {
            ModEntry.SMonitor?.Log(
                $"[EvolvedTraitManager] Restored {count} pending folding task(s).",
                LogLevel.Debug);
        }
    }

    private static void ClearPendingFoldState()
    {
        while (FoldingQueue.TryDequeue(out _))
        {
        }

        PendingFoldSet.Clear();

        FoldRetryCount.Clear();

        ModEntry.SMonitor?.Log(
            "[EvolvedTraitManager] Cleared old folding and retry state.",
            LogLevel.Debug);
    }

    private static void EnsureLoaded()
    {
        if (_cache != null)
            return;

        try
        {
            _cache = ModEntry.SHelper.Data
                .ReadJsonFile<Dictionary<string, List<string>>>(FilePath)
                ?? CreateCache();

            NormalizeCache();
        }
        catch (Exception ex)
        {
            ModEntry.SMonitor?.Log(
                $"[EvolvedTraitManager] Lazy load failed: {ex}",
                LogLevel.Error);

            _cache = CreateCache();
        }
    }

    private static Dictionary<string, List<string>> CreateCache()
    {
        return new Dictionary<string, List<string>>(
            StringComparer.OrdinalIgnoreCase);
    }

    private static void NormalizeCache()
    {
        if (_cache == null)
        {
            _cache = CreateCache();
            return;
        }

        var normalized = CreateCache();

        foreach (var pair in _cache)
        {
            string npcName = NormalizeString(pair.Key);

            if (string.IsNullOrWhiteSpace(npcName))
                continue;

            if (!normalized.TryGetValue(npcName, out var traits))
            {
                traits = new List<string>();
                normalized[npcName] = traits;
            }

            foreach (string item in pair.Value ?? new List<string>())
            {
                string trait = NormalizeString(item);

                if (!string.IsNullOrWhiteSpace(trait) &&
                    trait.Length <= 200 &&
                    !traits.Any(t =>
                        string.Equals(
                            t,
                            trait,
                            StringComparison.OrdinalIgnoreCase)))
                {
                    traits.Add(trait);
                }
            }
        }

        _cache = normalized;
    }

    private static string NormalizeString(string value)
    {
        return value?.Trim();
    }

    private static List<string> BuildContextKeywords(DialogueContext context, string npcName)
    {
        var keywords = new List<string>();

        if (context == null)
            return keywords;

        if (context.Accept != null)
        {
            keywords.AddRange(new[]
            {
                "gift", "礼物", "送", "present", "giving"
            });
        }

        // 仅在对话目标确实是配偶时才加入婚姻关键词，
        // 避免非配偶 NPC（克林特、马尔隆等）在多人婚姻存档中被强行拉取暧昧印象。
        if (context.Married && Game1.player?.spouse == npcName)
        {
            keywords.AddRange(new[]
            {
                "marry", "married", "spouse", "wedding",
                "婚", "爱", "love", "dear"
            });
        }

        if (context.RoutingFlags?.IsOnDate == true)
        {
            keywords.AddRange(new[]
            {
                "date", "romance", "love",
                "约会", "浪漫", "心动"
            });
        }

        if (context.RoutingFlags?.IsJealousy == true)
        {
            keywords.AddRange(new[]
            {
                "jealous", "other",
                "吃醋", "嫉妒", "其他人"
            });
        }

        string lastPlayerLine = context.ChatHistory?
            .LastOrDefault(x => x.IsPlayerLine)?
            .Text ?? string.Empty;

        if (!string.IsNullOrWhiteSpace(lastPlayerLine))
        {
            var words = lastPlayerLine
                .Split(
                    new[]
                    {
                        ' ', '，', ',', '。', '.', '！', '!',
                        '？', '?', '\n', '\r', '、', '；', ';'
                    },
                    StringSplitOptions.RemoveEmptyEntries)
                .Where(word => word.Length > 2)
                .Take(8);

            keywords.AddRange(words);
        }

        return keywords;
    }
}