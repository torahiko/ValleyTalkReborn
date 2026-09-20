#nullable enable
using StardewModdingAPI;
using ValleytalkReborn.Services.Overlays;

namespace ValleytalkReborn.Services;

/// <summary>约会地点覆盖层存储服务。FileName = "date_locations.json"。</summary>
internal sealed class DateLocationOverlayService : OverlayStorageServiceBase<DateLocationOverlayFile>
{
    public DateLocationOverlayService(IModHelper helper, IMonitor monitor)
        : base(helper, monitor, "custom_overlays")
    { }

    protected override string FileName => "date_locations.json";

    protected override string GetEntrySummary(DateLocationOverlayFile file)
        => $"Entries={file.Entries.Count},Removed={file.RemovedLocationIds.Count}";
}
