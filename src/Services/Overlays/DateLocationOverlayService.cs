#nullable enable
using System;
using System.Collections.Generic;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using ValleytalkReborn.Services.Overlays;

namespace ValleytalkReborn.Services;

/// <summary>约会地点覆盖层存储服务（FileName = "date_locations.json"）。资产挂接：晚于 CP 合成基线注入 upsert/墓碑。</summary>
internal sealed class DateLocationOverlayService : OverlayStorageServiceBase<DateLocationOverlayFile>
{
    private readonly IModHelper _helper;
    private readonly IMonitor _monitor;
    private bool _subscribed;
    private DateLocationOverlayFile? _cachedOverlay;
    private bool _dirty = true;
    private Dictionary<string, DateLocationInfo>? _baseline;

    public DateLocationOverlayService(IModHelper helper, IMonitor monitor)
        : base(helper, monitor, "custom_overlays")
    {
        _helper = helper;
        _monitor = monitor;
    }

    protected override string FileName => "date_locations.json";

    protected override string GetEntrySummary(DateLocationOverlayFile file)
        => $"Entries={file.Entries.Count},Removed={file.RemovedLocationIds.Count}";

    // ── 资产挂接（幂等订阅，与 BioStorageService 对齐） ──────────────
    public void RegisterAssetProviders()
    {
        if (_subscribed)
            return;
        _helper.Events.Content.AssetRequested += OnAssetRequested;
        _subscribed = true;
    }

    public void InvalidateCache()
    {
        try
        {
            _helper.GameContent.InvalidateCache(DateLocationRegistry.ASSET_KEY);
        }
        catch (Exception ex)
        {
            _monitor.Log($"[DateOverlay] 失效资产缓存失败: {ex.Message}", LogLevel.Warn);
        }
    }

    private DateLocationOverlayFile? GetOverlay()
    {
        if (_dirty || (_cachedOverlay == null && HasOverlay))
        {
            _cachedOverlay = LoadOrNull();
            _dirty = false;
        }
        return _cachedOverlay;
    }

    public override bool Save(DateLocationOverlayFile file, out string errorMessage)
    {
        bool ok = base.Save(file, out errorMessage);
        _dirty = true;
        if (ok)
            InvalidateCache();
        return ok;
    }

    public override bool DeleteOverlay(out string errorMessage)
    {
        bool ok = base.DeleteOverlay(out errorMessage);
        _dirty = true;
        if (ok)
            InvalidateCache();
        return ok;
    }

    private void OnAssetRequested(object? sender, AssetRequestedEventArgs e)
    {
        if (!e.NameWithoutLocale.IsEquivalentTo(DateLocationRegistry.ASSET_KEY))
            return;

        try
        {
            e.Edit(editor =>
            {
                if (editor is IAssetData<Dictionary<string, DateLocationInfo>> d)
                {
                    _baseline = new Dictionary<string, DateLocationInfo>(
                        d.Data, StringComparer.OrdinalIgnoreCase);
                    var ov = GetOverlay();
                    if (ov == null)
                        return;

                    foreach (var (id, info) in ov.Entries)
                    {
                        info.LocationId = id;   // 回填 ID
                        d.Data[id] = info;       // upsert
                    }
                    foreach (var id in ov.RemovedLocationIds)
                        d.Data.Remove(id);       // 墓碑

                    _monitor.Log($"[DateOverlay] applied {ov.Entries.Count} entries", LogLevel.Trace);
                }
            }, AssetEditPriority.Default, null);
        }
        catch (Exception ex)
        {
            _monitor.Log($"[DateOverlay] 覆盖层注入失败: {ex.Message}，本次回退内容包基线", LogLevel.Error);
        }
    }
}
