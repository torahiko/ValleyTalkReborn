#nullable enable
using StardewModdingAPI;
using ValleytalkReborn.Services.Overlays;

namespace ValleytalkReborn.Services;

/// <summary>世界概要覆盖层存储服务。FileName = "world_summaries.json"。</summary>
internal sealed class WorldSummaryOverlayService : OverlayStorageServiceBase<WorldSummaryOverlayFile>
{
    public WorldSummaryOverlayService(IModHelper helper, IMonitor monitor)
        : base(helper, monitor, "custom_overlays")
    { }

    protected override string FileName => "world_summaries.json";

    protected override string GetEntrySummary(WorldSummaryOverlayFile file)
        => $"LocDesc={file.LocationDescriptions.Count},Festivals={file.Festivals.Count}";
}
