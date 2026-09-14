using System.Collections.Generic;

namespace ValleytalkReborn;

internal sealed class NightlyWorkItem
{
    public string NpcName { get; set; } = string.Empty;

    public List<string> Events { get; set; } = new();

    /// <summary>对话回合切片（替换 DialogueExcerpts）。每行 ≤200 字符。</summary>
    public List<string> DialogueTurns { get; set; } = new();

    /// <summary>角色视角旁白（MEM-04 填充；本票恒为空串）。</summary>
    public string CharacterLens { get; set; } = string.Empty;

    public List<string> RelationshipContext { get; set; } = new();
}
