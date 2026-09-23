#nullable enable
using System;
using System.IO;
using StardewModdingAPI;

namespace ValleytalkReborn.Services;

/// <summary>
/// 统一持久化布局权威。覆盖层 JSON 全局磁盘布局对齐 <see cref="BioStorageService"/>：
/// Global = %AppData%\StardewValley\Saves\_ValleyTalkReborn_Global\{directoryName}。
/// LocalBaseDir 为 T2/T3/T4 预留，本层仅作常量。
/// </summary>
internal static class StorageLayout
{
    /// <summary>全局根：对齐 BioStorageService.GlobalBaseDir 的字面量 "_ValleyTalkReborn_Global"。</summary>
    public static string GlobalBaseDir => Path.Combine(Constants.SavesPath, "_ValleyTalkReborn_Global");

    /// <summary>每存档根：未载档返回 null，调用方必须显式处理（禁止回退模组目录）。</summary>
    public static string? LocalBaseDir =>
        string.IsNullOrEmpty(Constants.CurrentSavePath) ? null : Path.Combine(Constants.CurrentSavePath!, "ValleyTalkReborn_Local");

    /// <summary>模组目录（仅作遗留迁移源）。</summary>
    public static string ModDirectory => ModEntry.SHelper?.DirectoryPath ?? string.Empty;

    /// <summary>
    /// 一次性单向迁移：源不存在→无操作；目标已存在→跳过(Debug)；异常→Warn 吞掉；永不删除源。
    /// </summary>
    public static void MigrateLegacyFile(string legacySourceAbsolutePath, string targetAbsolutePath, string logTag)
    {
        try
        {
            if (!File.Exists(legacySourceAbsolutePath))
                return;

            if (File.Exists(targetAbsolutePath))
            {
                ModEntry.SMonitor?.Log($"[{logTag}] 迁移跳过（目标已存在）: {Path.GetFileName(targetAbsolutePath)}", LogLevel.Debug);
                return;
            }

            string? targetDir = Path.GetDirectoryName(targetAbsolutePath);
            if (!string.IsNullOrEmpty(targetDir) && !Directory.Exists(targetDir))
                Directory.CreateDirectory(targetDir);

            File.Copy(legacySourceAbsolutePath, targetAbsolutePath, overwrite: false);
            ModEntry.SMonitor?.Log($"[{logTag}] 已迁移覆盖层: {Path.GetFileName(targetAbsolutePath)}", LogLevel.Info);
        }
        catch (Exception ex)
        {
            ModEntry.SMonitor?.Log($"[{logTag}] 迁移单文件失败({Path.GetFileName(legacySourceAbsolutePath)}): {ex.Message}", LogLevel.Warn);
        }
    }
}
