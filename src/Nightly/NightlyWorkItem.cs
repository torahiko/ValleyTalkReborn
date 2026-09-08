using System.Collections.Generic;

namespace ValleytalkReborn;

internal sealed class NightlyWorkItem
{
    public string NpcName { get; set; } = string.Empty;

    public List<string> Events { get; set; } = new();

    public List<string> DialogueExcerpts { get; set; } = new();

    public List<string> RelationshipContext { get; set; } = new();
}