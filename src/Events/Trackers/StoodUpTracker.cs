using System;
using System.Collections.Generic;
using System.IO;
using StardewModdingAPI;
using StardewValley;
using ValleytalkReborn.Services;

namespace ValleytalkReborn;

/// <summary>
/// 持久化"放鸽子"记仇数据，独立于 MemoryManager，生命周期绑定存档。
/// </summary>
public class StoodUpTracker : IStoodUpProvider
{
    public static readonly StoodUpTracker Instance = new StoodUpTracker();

    // Key: NpcName, Value: 被放鸽子的游戏日期描述（用于 Prompt 注入）
    private Dictionary<string, string> _stoodUpData = new();

    /// <summary>
    /// 存档本地绝对路径：StorageLayout.LocalBaseDir/stoodup.json。
    /// 未载档或 SaveFolderName 为空时返回 null（此时读写均跳过）。
    /// </summary>
    private static string? FilePath =>
        StorageLayout.LocalBaseDir is null || string.IsNullOrEmpty(Constants.SaveFolderName)
            ? null
            : Path.Combine(StorageLayout.LocalBaseDir!, "stoodup.json");

    private StoodUpTracker() { }

    public void Load()
    {
        _stoodUpData.Clear();

        string? path = FilePath;
        if (path == null) return;

        // 一次性单向迁移遗留数据（data/{SaveFolderName}/stoodup.json → LocalBaseDir/stoodup.json）
        string legacyPath = Path.Combine(StorageLayout.ModDirectory, $"data/{Constants.SaveFolderName}/stoodup.json");
        StorageLayout.MigrateLegacyFile(legacyPath, path, "StoodUp");

        try
        {
            var loaded = ModEntry.SHelper.Data.ReadJsonFile<Dictionary<string, string>>(path);
            if (loaded != null)
                _stoodUpData = loaded;
        }
        catch (Exception ex)
        {
            ModEntry.SMonitor?.Log($"[StoodUpTracker] Load failed: {ex.Message}", LogLevel.Warn);
        }
    }

    /// <summary>
    /// [OPT-1][OPT-2] 统一使用 Data API，空数据写空字典而非删除文件
    /// </summary>
    private void Save()
    {
        string? path = FilePath;
        if (path == null)
        {
            ModEntry.SMonitor?.Log("[StoodUpTracker] Save: no save loaded, skipping persistence.", LogLevel.Trace);
            return;
        }

        try
        {
            // [OPT-2] 统一使用 SMAPI Data API，不再混用 File.Delete
            // 空字典写入比删除文件更安全：避免下次 Load 时因文件不存在产生额外 IO 或日志噪音
            ModEntry.SHelper.Data.WriteJsonFile(path, _stoodUpData);
        }
        catch (Exception ex)
        {
            ModEntry.SMonitor?.Log($"[StoodUpTracker] Save failed: {ex.Message}", LogLevel.Warn);
        }
    }

    /// <summary>
    /// 记录被放鸽子事件。
    /// [OPT-3] 日期描述包含年份，支持跨年区分
    /// </summary>
    public void RecordStoodUp(string npcName)
    {
        if (string.IsNullOrWhiteSpace(npcName)) return;

        // [OPT-3] 加入 Game1.year，避免第二年同季同日覆盖第一年的记录
        string dateDesc = $"Year {Game1.year} {Game1.currentSeason} {Game1.dayOfMonth}";
        _stoodUpData[npcName] = dateDesc;
        Save();
    }

    /// <summary>
    /// 查询某 NPC 是否有未清算的放鸽子记录（只读，不消费）。
    /// 返回日期描述，否则 null。
    /// </summary>
    public string GetPendingStoodUp(string npcName)
    {
        return _stoodUpData.TryGetValue(npcName, out var date) ? date : null;
    }

    /// <summary>
    /// [OPT-4] 读取并自动清除放鸽子记录（一次性消费语义）。
    /// 调用方无需再手动 ClearStoodUp，读取即消费，防止遗忘导致 NPC 反复提起。
    /// </summary>
    public bool TryConsumeStoodUp(string npcName, out string dateDesc)
    {
        if (_stoodUpData.TryGetValue(npcName, out dateDesc))
        {
            _stoodUpData.Remove(npcName);
            Save();
            ModEntry.SMonitor?.Log(
                $"[StoodUpTracker] Consumed stood-up record for {npcName}: {dateDesc}",
                LogLevel.Debug);
            return true;
        }

        dateDesc = null;
        return false;
    }

    /// <summary>
    /// 手动清除记录（保留向后兼容，内部委托给 TryConsumeStoodUp）。
    /// </summary>
    public void ClearStoodUp(string npcName)
    {
        TryConsumeStoodUp(npcName, out _);
    }
}