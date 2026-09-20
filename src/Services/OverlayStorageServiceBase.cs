#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using StardewModdingAPI;
using ValleytalkReborn.Services.Overlays;

namespace ValleytalkReborn.Services;

/// <summary>
/// 覆盖层存储抽象基类。覆盖层为磁盘上的独立 JSON 文件（全局、不进存档、联机各客户端独立）；
/// 基类仅负责原子读写与损坏回退，不含任何基线捕获/资产合并逻辑（那是 T2 各服务 Edit 回调的职责）。
/// </summary>
internal abstract class OverlayStorageServiceBase<TFile> where TFile : class, new()
{
    private readonly IMonitor monitor;
    private readonly string directoryName;

    protected OverlayStorageServiceBase(IModHelper helper, IMonitor monitor, string directoryName)
    {
        this.monitor = monitor;
        this.directoryName = directoryName;
    }

    /// <summary>各服务返回固定常量文件名。</summary>
    protected abstract string FileName { get; }

    /// <summary>虚属性，供测试子类覆写以指向临时目录。</summary>
    protected virtual string RootDirectory => ModEntry.SHelper.DirectoryPath;

    /// <summary>= RootDirectory/saves/custom_overlays/{directoryName}</summary>
    protected string OverlayDirectory =>
        Path.Combine(RootDirectory, "saves", "custom_overlays", directoryName);

    /// <summary>= OverlayDirectory/{FileName}</summary>
    protected string OverlayFilePath =>
        Path.Combine(OverlayDirectory, FileName);

    /// <summary>覆盖层文件是否存在。</summary>
    public bool HasOverlay => File.Exists(OverlayFilePath);

    /// <summary>
    /// 加载覆盖层：不存在→null（Debug 日志）；反序列化异常→Warn 日志+返回 null（不删除文件）。
    /// 对 PoiOverlayFile 额外做 CustomPois 消毒（MapName 空白或 X/Y 负值→剔除该条）。
    /// </summary>
    public TFile? LoadOrNull()
    {
        if (!File.Exists(OverlayFilePath))
        {
            this.monitor.Log($"[OverlayStorage] 覆盖层不存在({FileName})，回退基线", LogLevel.Debug);
            return null;
        }

        try
        {
            string json = File.ReadAllText(OverlayFilePath);
            TFile? result = JsonConvert.DeserializeObject<TFile>(json);
            if (result == null)
            {
                this.monitor.Log($"[OverlayStorage] 覆盖层损坏({FileName})，回退基线", LogLevel.Warn);
                return null;
            }

            // PoiOverlayFile 消毒：MapName 空白或 X/Y 负值 → 剔除该条并 Warn。
            if (result is PoiOverlayFile poi)
            {
                SanitizePoiCustomPois(poi);
            }

            return result;
        }
        catch (Exception ex)
        {
            this.monitor.Log($"[OverlayStorage] 覆盖层损坏({FileName})，回退基线: {ex.Message}", LogLevel.Warn);
            return null;
        }
    }

    /// <summary>
    /// 保存覆盖层：创建目录 → 序列化 → 反序列化校验 → 原子写（.tmp + Move）→ Info 日志。
    /// 任意异常不部分写入；失败时 best-effort 删除残留 .tmp，return false 且 errorMessage 明确。
    /// </summary>
    public bool Save(TFile file, out string errorMessage)
    {
        errorMessage = string.Empty;
        string tmp = OverlayFilePath + ".tmp";
        try
        {
            if (!Directory.Exists(OverlayDirectory))
                Directory.CreateDirectory(OverlayDirectory);

            string json = JsonConvert.SerializeObject(file, Formatting.Indented);

            // 验签：反序列化不能为空。
            TFile? verify = JsonConvert.DeserializeObject<TFile>(json);
            if (verify == null)
            {
                errorMessage = "序列化校验为空";
                this.monitor.Log($"[OverlayStorage] {errorMessage}: {FileName}", LogLevel.Error);
                return false;
            }

            // 原子写：写 .tmp 后覆盖移动，避免中断产生半写文件。
            File.WriteAllText(tmp, json);
            File.Move(tmp, OverlayFilePath, overwrite: true);

            this.monitor.Log($"[OverlayStorage] 已保存覆盖层: {FileName} {GetEntrySummary(file)}", LogLevel.Info);
            return true;
        }
        catch (Exception ex)
        {
            // 失败时 best-effort 删除残留 .tmp，源文件保持原状（Move 未发生则源文件原封不动）。
            try { if (File.Exists(tmp)) File.Delete(tmp); }
            catch { }

            errorMessage = ex.Message;
            this.monitor.Log($"[OverlayStorage] 覆盖层保存失败({FileName}): {ex.Message}", LogLevel.Error);
            return false;
        }
    }

    /// <summary>
    /// 删除覆盖层：存在则删除；删除抛异常 → errorMessage + Log Error + return false（不谎报成功）；
    /// 不存在也 return true（幂等）。
    /// </summary>
    public bool DeleteOverlay(out string errorMessage)
    {
        errorMessage = string.Empty;
        try
        {
            if (File.Exists(OverlayFilePath))
                File.Delete(OverlayFilePath);

            this.monitor.Log($"[OverlayStorage] 已删除覆盖层: {FileName}", LogLevel.Info);
            return true;
        }
        catch (Exception ex)
        {
            errorMessage = ex.Message;
            this.monitor.Log($"[OverlayStorage] 覆盖层删除失败({FileName}): {ex.Message}", LogLevel.Error);
            return false;
        }
    }

    /// <summary>供子类覆写，在 Info 日志中报告条目计数。默认空。</summary>
    protected virtual string GetEntrySummary(TFile file) => string.Empty;

    /// <summary>文件名消毒（照抄 BioStorageService 实现），供未来扩展。</summary>
    protected static string SanitizeFileName(string name)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        string sanitized = name;
        foreach (char c in invalid)
            sanitized = sanitized.Replace(c, '_');
        return sanitized;
    }

    private void SanitizePoiCustomPois(PoiOverlayFile poi)
    {
        if (poi.CustomPois == null)
            return;

        List<string> toRemove = new();
        foreach (var kv in poi.CustomPois)
        {
            PoiAsset? asset = kv.Value;
            if (asset == null
                || string.IsNullOrWhiteSpace(asset.MapName)
                || asset.TargetTile == null
                || asset.TargetTile.X < 0
                || asset.TargetTile.Y < 0)
            {
                toRemove.Add(kv.Key);
            }
        }

        if (toRemove.Count == 0)
            return;

        foreach (string key in toRemove)
            poi.CustomPois.Remove(key);

        this.monitor.Log($"[OverlayStorage] 覆盖层消毒({FileName})：剔除 {toRemove.Count} 条非法自定义兴趣点", LogLevel.Warn);
    }
}
