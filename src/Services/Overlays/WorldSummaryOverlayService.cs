#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using ValleytalkReborn.Services.Overlays;

namespace ValleytalkReborn.Services;

/// <summary>世界概要覆盖层存储服务（FileName = "world_summaries.json"）。资产挂接 GameSummary：WP-A 注入地点/原版节日描述，WP-B 读时合成自创纪念日。</summary>
internal sealed class WorldSummaryOverlayService : OverlayStorageServiceBase<WorldSummaryOverlayFile>
{
    private static readonly string SummaryAssetKey = VtConstants.GameSummaryPath;

    private readonly IModHelper _helper;
    private readonly IMonitor _monitor;
    private bool _subscribed;
    private WorldSummaryOverlayFile? _cachedOverlay;
    private bool _dirty = true;
    private Dictionary<string, string>? _baselineLocationDescriptions;
    private Dictionary<string, CustomFestivalEntry>? _baselineFestivals;

    public WorldSummaryOverlayService(IModHelper helper, IMonitor monitor)
        : base(helper, monitor, "custom_overlays")
    {
        _helper = helper;
        _monitor = monitor;
    }

    protected override string FileName => "world_summaries.json";

    protected override string GetEntrySummary(WorldSummaryOverlayFile file)
        => $"LocDesc={file.LocationDescriptions.Count},Festivals={file.Festivals.Count}";

    // ── WP-A 资产挂接 ──────────────────────────────────────────────
    public void RegisterAssetProviders()
    {
        if (_subscribed) return;
        _helper.Events.Content.AssetRequested += OnAssetRequested;
        _subscribed = true;

        MigrateLegacyOverlay();
    }

    public void InvalidateCache()
    {
        try { _helper.GameContent.InvalidateCache(SummaryAssetKey); }
        catch (Exception ex) { _monitor.Log($"[WorldSummaryOverlay] 失效资产缓存失败: {ex.Message}", LogLevel.Warn); }
    }

    private WorldSummaryOverlayFile? GetOverlay()
    {
        if (_dirty || (_cachedOverlay == null && HasOverlay))
        {
            _cachedOverlay = LoadOrNull();
            _dirty = false;
        }
        return _cachedOverlay;
    }

    // 供 WP-B 读取原始覆盖层（含 IsCustomDate==true 条目，资产侧不写入这些条目）。
    internal WorldSummaryOverlayFile? GetCachedOverlay() => GetOverlay();

    public override bool Save(WorldSummaryOverlayFile file, out string errorMessage)
    {
        bool ok = base.Save(file, out errorMessage);
        _dirty = true;
        if (ok) InvalidateCache();
        return ok;
    }

    public override bool DeleteOverlay(out string errorMessage)
    {
        bool ok = base.DeleteOverlay(out errorMessage);
        _dirty = true;
        if (ok) InvalidateCache();
        return ok;
    }

    private void OnAssetRequested(object? sender, AssetRequestedEventArgs e)
    {
        if (!e.NameWithoutLocale.IsEquivalentTo(SummaryAssetKey)) return;
        try
        {
            e.Edit(editor =>
            {
                if (editor.DataType != typeof(GameSummary))
                {
                    _monitor.Log($"[WorldSummaryOverlay] 注入跳过: 资产数据类型不匹配，期望 {typeof(GameSummary).FullName}，实际 {editor.DataType.FullName}", LogLevel.Error);
                    return;
                }
                var data = (GameSummary)editor.Data;
                _monitor.Log("[WorldSummaryOverlay] 注入回调生效", LogLevel.Debug);
                // 1. 基线捕获（任何 upsert 之前）
                _baselineLocationDescriptions = data.Locations.Entries?.ToDictionary(
                    kv => kv.Key, kv => kv.Value.Description ?? "", StringComparer.OrdinalIgnoreCase);
                _baselineFestivals = data.Festivals?.Entries?.ToDictionary(
                    kv => kv.Key, kv => ToBaselineFestival(kv.Value), StringComparer.OrdinalIgnoreCase);

                var ov = GetOverlay();
                if (ov == null) return;

                // 2-3. 地点描述 upsert（未知键拒绝；墓碑键跳过 = 保持内容包原值）
                Dictionary<string, LocationObject>? locations = data.Locations.Entries;
                if (locations != null)
                {
                    foreach (var kv in ov.LocationDescriptions)
                    {
                        if (ov.RemovedLocationDescriptionIds.Exists(r => string.Equals(r, kv.Key, StringComparison.OrdinalIgnoreCase)))
                            continue;
                        if (!locations.TryGetValue(kv.Key, out var loc))
                        {
                            _monitor.Log($"[WorldSummaryOverlay] 未知地点键 {kv.Key}，已拒绝", LogLevel.Warn);
                            continue;
                        }
                        loc.Description = kv.Value;
                    }
                }

                // 4-5. 原版节日描述 upsert（IsCustomDate==false）；IsCustomDate==true 条目留给 WP-B 读时合成
                Dictionary<string, GeneralObject>? festivals = data.Festivals?.Entries;
                if (festivals != null)
                {
                    foreach (var kv in ov.Festivals)
                    {
                        if (kv.Value == null || kv.Value.IsCustomDate) continue;
                        if (!festivals.TryGetValue(kv.Key, out var fest))
                        {
                            _monitor.Log($"[WorldSummaryOverlay] 未知节日键 {kv.Key}，已拒绝", LogLevel.Warn);
                            continue;
                        }
                        fest.Name = ResolveLocalized(kv.Value.Names);
                        fest.Description = ResolveLocalized(kv.Value.Descriptions);
                    }
                }

                _monitor.Log($"[WorldSummaryOverlay] applied locDesc={ov.LocationDescriptions.Count}, fest={ov.Festivals.Count}", LogLevel.Trace);
            }, AssetEditPriority.Default, null);
        }
        catch (Exception ex)
        {
            _monitor.Log($"[WorldSummaryOverlay] 覆盖层注入失败: {ex.Message}，本次回退内容包基线", LogLevel.Error);
        }
    }

    private static CustomFestivalEntry ToBaselineFestival(GeneralObject go)
    {
        string lang = LocalizedContentManager.CurrentLanguageCode.ToString().ToLowerInvariant();
        return new CustomFestivalEntry
        {
            IsCustomDate = false,
            Names = new Dictionary<string, string> { [lang] = go.Name ?? "" },
            Descriptions = new Dictionary<string, string> { [lang] = go.Description ?? "" }
        };
    }

    private static string ResolveLocalized(Dictionary<string, string> dict)
    {
        if (dict == null || dict.Count == 0) return "";
        string lang = LocalizedContentManager.CurrentLanguageCode.ToString().ToLowerInvariant();
        if (dict.TryGetValue(lang, out var val) && val != null) return val;
        if (dict.TryGetValue("en", out val) && val != null) return val;
        foreach (var v in dict.Values) if (v != null) return v;
        return "";
    }

    // ── WP-B 自创纪念日查询（供 GameSummaryBuilder 与 BuildContext.FromGameState 门控共用） ──
    internal static bool CustomFestivalRelevant()
    {
        string season = Game1.season.ToString();
        int day = Game1.dayOfMonth;
        return FindCustomFestival($"{season}{day}") != null || FindCustomFestival(BuildEveKey(season, day)) != null;
    }

    internal static CustomFestivalEntry? FindCustomFestival(string seasonDayKey)
    {
        WorldSummaryOverlayFile? ov = ModEntry.WorldSummaryOverlay?.GetCachedOverlay();
        if (ov?.Festivals == null) return null;
        if (!ov.Festivals.TryGetValue(seasonDayKey, out var entry) || entry == null) return null;
        return entry.IsCustomDate ? entry : null;
    }

    internal static string BuildEveKey(string season, int day)
    {
        if (day < 28) return $"{season}{day + 1}";
        Season nextSeason = (Season)(((int)Game1.season + 1) % 4);
        return $"{nextSeason}1";
    }

    internal static string ResolveFestivalLanguage(Dictionary<string, string> dict)
    {
        if (dict == null || dict.Count == 0) return "";
        string lang = LocalizedContentManager.CurrentLanguageCode.ToString().ToLowerInvariant();
        if (dict.TryGetValue(lang, out var val) && !string.IsNullOrEmpty(val)) return val;
        if (dict.TryGetValue("en", out val) && !string.IsNullOrEmpty(val)) return val;
        foreach (var v in dict.Values) if (!string.IsNullOrEmpty(v)) return v;
        return "";
    }
}
