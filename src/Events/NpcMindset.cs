using System;
using System.Collections.Generic;

namespace ValleytalkReborn;

/// <summary>
/// 单个 NPC 的常驻心智底色（长期稳定，非本次对话事件）。
/// 字典键即 NPC 名，本类不存 NpcName 以避免双写漂移。
/// </summary>
internal sealed class NpcMindset
{
    /// <summary>关系定位，≤120 字符。</summary>
    public string RelationshipStance { get; set; } = string.Empty;

    /// <summary>核心印象，最多 3 条，每条 ≤60 字符。</summary>
    public List<string> CoreImpressions { get; set; } = new();

    /// <summary>边界层级，≤120 字符。</summary>
    public string BoundaryLevel { get; set; } = string.Empty;

    /// <summary>最后更新游戏日（Game1.Date.TotalDays）。</summary>
    public int LastUpdateDay { get; set; }
}
