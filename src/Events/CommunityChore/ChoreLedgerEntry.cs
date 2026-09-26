using System.Collections.Generic;

namespace ValleytalkReborn
{
    /// <summary>
    /// 单条社区家务账目条目：来源 NPC、目标 NPC、适用季节、星期编号及双方话题。
    /// 纯内存态，不进存档。
    /// </summary>
    internal sealed class ChoreLedgerEntry
    {
        public string ChoreId { get; set; }
        public string SourceNpc { get; set; }
        public string TargetNpc { get; set; }
        public List<string> ApplicableSeasons { get; set; }
        public int DayOfWeek { get; set; }
        public ChoreTopic SourceTopic { get; set; }
        public ChoreTopic TargetTopic { get; set; }
    }
}
