#nullable enable
using StardewModdingAPI;
using ValleytalkReborn.Services.Overlays;

namespace ValleytalkReborn.Services;

/// <summary>兴趣点偏好覆盖层存储服务。FileName = "poi_preferences.json"。</summary>
internal sealed class PoiPreferenceOverlayService : OverlayStorageServiceBase<PoiOverlayFile>
{
    public PoiPreferenceOverlayService(IModHelper helper, IMonitor monitor)
        : base(helper, monitor, "custom_overlays")
    { }

    protected override string FileName => "poi_preferences.json";

    protected override string GetEntrySummary(PoiOverlayFile file)
        => $"NpcPrefs={file.NpcPreferences.Count},CustomPois={file.CustomPois.Count}";
}
