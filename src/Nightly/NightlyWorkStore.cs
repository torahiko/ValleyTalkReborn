using System;
using System.Collections.Generic;
using System.Linq;
using StardewModdingAPI;

namespace ValleytalkReborn;

internal static class NightlyWorkStore
{
    private static readonly object LockObject = new();

    private static string FilePath =>
        $"data/NightlyPending_{Constants.SaveFolderName}.json";

    private static string BackupPath =>
        $"data/NightlyPending_{Constants.SaveFolderName}.bak.json";

    public static void Save(List<NightlyWorkItem> items)
    {
        if (items == null || items.Count == 0)
            return;

        lock (LockObject)
        {
            try
            {
                // ── 与既有文件合并（新 items 覆盖同名 NPC，其余保留）──
                List<NightlyWorkItem> existing = ReadExistingForMerge();
                var merged = MergeWithExisting(items, existing);

                var cleanItems = new List<NightlyWorkItem>();

                foreach (var item in merged)
                {
                    if (item == null ||
                        string.IsNullOrWhiteSpace(item.NpcName))
                    {
                        continue;
                    }

                    cleanItems.Add(new NightlyWorkItem
                    {
                        NpcName = item.NpcName.Trim(),
                        Events = LimitList(item.Events, 20, 1000),
                        DialogueTurns = LimitList(item.DialogueTurns, 12, 200),
                        CharacterLens = (item.CharacterLens ?? "").Trim(),
                        RelationshipContext = LimitList(
                            item.RelationshipContext,
                            20,
                            300)
                    });
                }

                if (cleanItems.Count == 0)
                    return;

                // 先写备份（备份"合并前"的既有文件），降低写入过程中进程退出导致数据损坏的风险。
                try
                {
                    if (existing != null && existing.Count > 0)
                    {
                        ModEntry.SHelper.Data.WriteJsonFile(
                            BackupPath,
                            existing);
                    }
                }
                catch (Exception ex)
                {
                    ModEntry.SMonitor?.Log(
                        $"[NightlyWorkStore] Failed to create backup: {ex.Message}",
                        LogLevel.Debug);
                }

                ModEntry.SHelper.Data.WriteJsonFile(
                    FilePath,
                    cleanItems);

                ModEntry.SMonitor?.Log(
                    $"[NightlyWorkStore] Saved {cleanItems.Count} work item(s).",
                    LogLevel.Debug);

                if (existing != null && existing.Count > 0)
                {
                    ModEntry.SMonitor?.Log(
                        $"[NightlyWorkStore] Merged {existing.Count} existing + {items.Count} new = {cleanItems.Count} work item(s).",
                        LogLevel.Debug);
                }
            }
            catch (Exception ex)
            {
                ModEntry.SMonitor?.Log(
                    $"[NightlyWorkStore] Save failed: {ex}",
                    LogLevel.Error);

                throw;
            }
        }
    }

    private static List<NightlyWorkItem> ReadExistingForMerge()
    {
        try
        {
            return ModEntry.SHelper.Data
                .ReadJsonFile<List<NightlyWorkItem>>(FilePath);
        }
        catch (Exception ex)
        {
            ModEntry.SMonitor?.Log(
                $"[NightlyWorkStore] Merge read failed, treating as empty: {ex.Message}",
                LogLevel.Warn);
            return null;
        }
    }

    private static List<NightlyWorkItem> MergeWithExisting(
        List<NightlyWorkItem> fresh,
        List<NightlyWorkItem> existing)
    {
        var dict = new Dictionary<string, NightlyWorkItem>(StringComparer.OrdinalIgnoreCase);

        // 先加载既有条目
        if (existing != null)
        {
            foreach (var item in existing)
            {
                if (item == null || string.IsNullOrWhiteSpace(item.NpcName)) continue;
                dict[item.NpcName.Trim()] = item;
            }
        }

        // 新 items 覆盖同名 NPC
        foreach (var item in fresh)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.NpcName)) continue;
            dict[item.NpcName.Trim()] = item;
        }

        return dict.Values.OrderBy(k => k.NpcName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static List<NightlyWorkItem> Load()
    {
        lock (LockObject)
        {
            try
            {
                var items = ModEntry.SHelper.Data
                    .ReadJsonFile<List<NightlyWorkItem>>(FilePath);

                if (items != null && items.Count > 0)
                {
                    // 验证并返回有效数据。
                    return NormalizeWorkItems(items);
                }

                // 主文件为空，尝试从备份恢复。
                var backupItems = ModEntry.SHelper.Data
                    .ReadJsonFile<List<NightlyWorkItem>>(BackupPath);

                if (backupItems != null && backupItems.Count > 0)
                {
                    ModEntry.SMonitor?.Log(
                        $"[NightlyWorkStore] Primary file empty. Loaded {backupItems.Count} item(s) from backup.",
                        LogLevel.Warn);

                    return NormalizeWorkItems(backupItems);
                }

                return new List<NightlyWorkItem>();
            }
            catch (Exception ex)
            {
                ModEntry.SMonitor?.Log(
                    $"[NightlyWorkStore] Load failed: {ex}. Trying backup.",
                    LogLevel.Warn);

                // Guard: 主文件损坏时尝试备份。
                try
                {
                    var backupItems = ModEntry.SHelper.Data
                        .ReadJsonFile<List<NightlyWorkItem>>(BackupPath);

                    if (backupItems != null && backupItems.Count > 0)
                    {
                        ModEntry.SMonitor?.Log(
                            $"[NightlyWorkStore] Loaded {backupItems.Count} item(s) from backup after primary failure.",
                            LogLevel.Info);

                        return NormalizeWorkItems(backupItems);
                    }
                }
                catch (Exception backupEx)
                {
                    ModEntry.SMonitor?.Log(
                        $"[NightlyWorkStore] Backup also failed: {backupEx.Message}",
                        LogLevel.Error);
                }

                return new List<NightlyWorkItem>();
            }
        }
    }

    private static List<NightlyWorkItem> NormalizeWorkItems(List<NightlyWorkItem> items)
    {
        if (items == null)
            return new List<NightlyWorkItem>();

        try
        {
            var validItems = new List<NightlyWorkItem>();

            foreach (var item in items)
            {
                if (item == null || string.IsNullOrWhiteSpace(item.NpcName))
                    continue;

                item.NpcName = item.NpcName.Trim();
                item.Events ??= new List<string>();
                item.DialogueTurns ??= new List<string>();
                item.CharacterLens ??= string.Empty;
                item.RelationshipContext ??= new List<string>();

                validItems.Add(item);
            }

            if (validItems.Count > 0)
            {
                ModEntry.SMonitor?.Log(
                    $"[NightlyWorkStore] Loaded {validItems.Count} pending work item(s).",
                    LogLevel.Debug);
            }

            return validItems;
        }
        catch
        {
            return new List<NightlyWorkItem>();
        }
    }

    public static void Clear()
    {
        lock (LockObject)
        {
            try
            {
                ModEntry.SHelper.Data.WriteJsonFile(
                    FilePath,
                    new List<NightlyWorkItem>());

                ModEntry.SMonitor?.Log(
                    "[NightlyWorkStore] Pending work cleared after successful processing.",
                    LogLevel.Debug);
            }
            catch (Exception ex)
            {
                ModEntry.SMonitor?.Log(
                    $"[NightlyWorkStore] Clear failed. Data will be retried next day: {ex}",
                    LogLevel.Error);
            }
        }
    }

    private static List<string> LimitList(
        List<string> source,
        int maxItems,
        int maxLength)
    {
        var result = new List<string>();

        foreach (string value in source ?? new List<string>())
        {
            if (string.IsNullOrWhiteSpace(value))
                continue;

            string text = value.Trim();

            if (text.Length > maxLength)
                text = text[..maxLength];

            result.Add(text);

            if (result.Count >= maxItems)
                break;
        }

        return result;
    }
}