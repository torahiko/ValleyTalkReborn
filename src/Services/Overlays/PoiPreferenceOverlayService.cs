#nullable enable
using System;
using System.Collections.Generic;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using ValleytalkReborn.Services.Overlays;

namespace ValleytalkReborn.Services;

/// <summary>兴趣点偏好覆盖层存储服务（FileName = "poi_preferences.json"）。资产挂接：订阅 NPC 喜好 + 全局 POI 两个资产键。</summary>
internal sealed class PoiPreferenceOverlayService : OverlayStorageServiceBase<PoiOverlayFile>
{
    private readonly IModHelper _helper;
    private readonly IMonitor _monitor;
    private bool _subscribed;
    private PoiOverlayFile? _cachedOverlay;
    private bool _dirty = true;
    private Dictionary<string, NpcPreference>? _prefBaseline;
    private Dictionary<string, PoiAsset>? _poiBaseline;

    public PoiPreferenceOverlayService(IModHelper helper, IMonitor monitor)
        : base(helper, monitor, "custom_overlays")
    {
        _helper = helper;
        _monitor = monitor;
    }

    protected override string FileName => "poi_preferences.json";

    protected override string GetEntrySummary(PoiOverlayFile file)
        => $"NpcPrefs={file.NpcPreferences.Count},CustomPois={file.CustomPois.Count}";

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
            _helper.GameContent.InvalidateCache(SchedulePlanner.PREF_ASSET_KEY);
            _helper.GameContent.InvalidateCache(PoiRepository.POI_ASSET_KEY);
        }
        catch (Exception ex)
        {
            _monitor.Log($"[PoiOverlay] 失效资产缓存失败: {ex.Message}", LogLevel.Warn);
        }
    }

    private PoiOverlayFile? GetOverlay()
    {
        if (_dirty || (_cachedOverlay == null && HasOverlay))
        {
            _cachedOverlay = LoadOrNull();
            _dirty = false;
        }
        return _cachedOverlay;
    }

    public override bool Save(PoiOverlayFile file, out string errorMessage)
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
        try
        {
            if (e.NameWithoutLocale.IsEquivalentTo(SchedulePlanner.PREF_ASSET_KEY))
            {
                e.Edit(editor =>
                {
                    if (editor is IAssetData<Dictionary<string, NpcPreference>> d)
                    {
                        _prefBaseline = new Dictionary<string, NpcPreference>(
                            d.Data, StringComparer.OrdinalIgnoreCase);
                        ApplyNpcPreferences(d.Data);
                    }
                }, AssetEditPriority.Default, null);
            }
            else if (e.NameWithoutLocale.IsEquivalentTo(PoiRepository.POI_ASSET_KEY))
            {
                e.Edit(editor =>
                {
                    if (editor is IAssetData<Dictionary<string, PoiAsset>> d)
                    {
                        _poiBaseline = new Dictionary<string, PoiAsset>(
                            d.Data, StringComparer.OrdinalIgnoreCase);
                        ApplyPoiAssets(d.Data);
                    }
                }, AssetEditPriority.Default, null);
            }
        }
        catch (Exception ex)
        {
            _monitor.Log($"[PoiOverlay] 覆盖层注入失败: {ex.Message}，本次回退内容包基线", LogLevel.Error);
        }
    }

    private void ApplyNpcPreferences(Dictionary<string, NpcPreference> data)
    {
        var ov = GetOverlay();
        if (ov == null)
            return;

        foreach (var (name, pref) in ov.NpcPreferences)
            data[name] = pref;           // upsert
        foreach (var name in ov.RemovedNpcNames)
            data.Remove(name);           // 墓碑

        _monitor.Log($"[PoiOverlay] applied {ov.NpcPreferences.Count} npc prefs", LogLevel.Trace);
    }

    private void ApplyPoiAssets(Dictionary<string, PoiAsset> data)
    {
        var ov = GetOverlay();
        if (ov == null)
            return;

        foreach (var (id, poi) in ov.CustomPois)
        {
            if (poi == null
                || string.IsNullOrWhiteSpace(poi.MapName)
                || poi.TargetTile == null
                || poi.TargetTile.X < 0
                || poi.TargetTile.Y < 0)
            {
                _monitor.Log($"[PoiOverlay] 跳过非法自定义兴趣点: {id}", LogLevel.Warn);
                continue;
            }
            data[id] = poi;              // upsert
        }
        foreach (var id in ov.RemovedCustomPoiIds)
            data.Remove(id);             // 墓碑

        _monitor.Log($"[PoiOverlay] applied {ov.CustomPois.Count} custom pois", LogLevel.Trace);
    }
}
