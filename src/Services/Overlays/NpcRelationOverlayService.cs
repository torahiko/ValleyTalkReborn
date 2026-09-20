#nullable enable
using StardewModdingAPI;
using ValleytalkReborn.Services.Overlays;

namespace ValleytalkReborn.Services;

/// <summary>NPC 关系覆盖层存储服务。FileName = "npc_relations.json"。</summary>
internal sealed class NpcRelationOverlayService : OverlayStorageServiceBase<NpcRelationOverlayFile>
{
    public NpcRelationOverlayService(IModHelper helper, IMonitor monitor)
        : base(helper, monitor, "custom_overlays")
    { }

    protected override string FileName => "npc_relations.json";

    protected override string GetEntrySummary(NpcRelationOverlayFile file)
        => $"Relations={file.Relations.Count}";
}
