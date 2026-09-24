using System;
using System.Collections.Generic;
using StardewModdingAPI;

namespace ValleytalkReborn;

internal sealed class SensoryEvaluation
{
    public bool Hit { get; init; }
    public PerceptionEntry Entry { get; init; }
    public string PerceptionKey { get; init; }
    public SensoryType Type { get; init; }
    public SensoryCategory Category { get; init; }
}

internal static class SensoryClassifier
{
    // 测试缝线：默认实现读取感知桶；测试可注入。禁止在生产代码路径外的写入。
    internal static Func<string, List<PerceptionEntry>> BucketProvider { get; set; }
        = npcName => PerceptionManager.Instance?.GetFilteredBucketFor(npcName, max: 6) ?? new List<PerceptionEntry>();

    // key → (Type, Category) 固定映射（来源：PlayerStateScanner.cs 已核实的 8 个 key，逐字匹配、OrdinalIgnoreCase）
    private static readonly Dictionary<string, (SensoryType Type, SensoryCategory Category)> _keyMap = new(StringComparer.OrdinalIgnoreCase)
    {
        { "PlayerSpecialOutfit_Shorts",  (SensoryType.LewisShorts,     SensoryCategory.Continuous) },
        { "PlayerSpecialOutfit_Trash",   (SensoryType.TrashOutfit,     SensoryCategory.Continuous) },
        { "PlayerSpecialOutfit_Hazmat",  (SensoryType.HazmatSuit,      SensoryCategory.Continuous) },
        { "PlayerWeddingOutfit",         (SensoryType.WeddingDress,    SensoryCategory.Continuous) },
        { "PlayerFainted",               (SensoryType.FaintedYesterday, SensoryCategory.Continuous) },
        { "PlayerGarlicSmell",           (SensoryType.GarlicStench,    SensoryCategory.Transient) },
        { "PlayerMonsterMusk",           (SensoryType.MonsterMusk,     SensoryCategory.Transient) },
        { "PlayerExhausted",             (SensoryType.Exhaustion,      SensoryCategory.Transient) },
    };

    internal static SensoryEvaluation Evaluate(string npcName)
    {
        if (string.IsNullOrWhiteSpace(npcName))
            return new SensoryEvaluation { Hit = false };

        try
        {
            var bucket = BucketProvider(npcName);
            if (bucket == null || bucket.Count == 0)
                return new SensoryEvaluation { Hit = false };

            // 多命中裁定：按 GetFilteredBucketFor 返回顺序（即感知层 salience 降序）取第一个映射命中项
            foreach (var entry in bucket)
            {
                if (_keyMap.TryGetValue(entry.Key, out var mapped))
                {
                    return new SensoryEvaluation
                    {
                        Hit = true,
                        Entry = entry,
                        PerceptionKey = entry.Key,
                        Type = mapped.Type,
                        Category = mapped.Category,
                    };
                }
            }

            return new SensoryEvaluation { Hit = false };
        }
        catch (Exception ex)
        {
            ModEntry.SMonitor?.Log($"[SensoryClassifier] Evaluate error: {ex.Message}", LogLevel.Trace);
            return new SensoryEvaluation { Hit = false };
        }
    }
}
