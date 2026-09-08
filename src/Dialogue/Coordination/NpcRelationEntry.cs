using System.Collections.Generic;

namespace ValleytalkReborn;

internal sealed class NpcRelationEntry
{
    public string NpcA     { get; set; }
    public string NpcB     { get; set; }
    public bool   Disabled { get; set; } = false;

    public Dictionary<string, string> Descriptions { get; set; } = new();
}

internal sealed class NpcRelationsFile
{
    public List<NpcRelationEntry> Relations { get; set; } = new();
}
