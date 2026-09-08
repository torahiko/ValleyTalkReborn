using System;
using System.Collections.Generic;

namespace ValleytalkReborn;

internal class PerceptionEntry
{
    private readonly object _entryLock = new object();

    public string Key { get; set; } = string.Empty;
    public string Template { get; set; } = string.Empty;

    public string NpcName { get; set; } = string.Empty;

    /// <summary>
    /// In-game time when this was recorded (e.g., 1430 for 2:30 PM).
    /// Used for sorting and lifetime validation. Replaces DateTime.Now to stay on the game clock.
    /// </summary>
    public int RecordedTimeOfDay { get; set; } = 600;

    /// <summary>Lifetime in in-game hours. Values >= 20 mean all-day.</summary>
    public int LifetimeHours { get; set; } = 2;

    /// <summary>
    /// True  → enters the global gossip queue (Track 1, max 2, town-wide snapshots).
    /// False → enters the farmer's personal bucket (Track 2, max 3, eyewitness-filtered).
    /// </summary>
    public bool IsGossip { get; set; } = false;

    /// <summary>
    /// Condition C: landmark/rare event that broadcasts town-wide regardless of NPC presence.
    /// E.g. catching a Legend fish. When true, all NPCs receive this entry regardless of location.
    /// </summary>
    public bool IsLandmark { get; set; } = false;

    /// <summary>
    /// The game location name where this event occurred (e.g. "Saloon", "Beach").
    /// Used by the eyewitness filter: NPCs present at this location can "see" this event.
    /// </summary>
    public string LocationName { get; set; } = string.Empty;

    /// <summary>Item ID for dynamic gift-taste evaluation (used by Eat and Gift perceptions).</summary>
    public string ItemId { get; set; } = null;

    /// <summary>
    /// True → already processed by nightly consolidation; skip during prompt injection the next day.
    /// </summary>
    public bool IsConsolidated { get; set; } = false;

    /// <summary>
    /// 记录已消费此事件的 NPC 名称集合（避免单例事件在多 NPC 旁观时相互踩踏）。
    /// </summary>
    public HashSet<string> ConsumedByNpcs { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 判断该条目对指定 NPC 是否已被消费。
    /// 若已被全局合并 (IsConsolidated) 或已被该 NPC 消费，则返回 true。
    /// </summary>
    public bool IsConsumedBy(string npcName)
    {
        if (IsConsolidated) return true;
        if (string.IsNullOrEmpty(npcName)) return false;

        lock (_entryLock)
        {
            return ConsumedByNpcs.Contains(npcName);
        }
    }

    /// <summary>
    /// 为指定 NPC 标记消费此条目。
    /// </summary>
    public void ConsumeFor(string npcName)
    {
        if (!string.IsNullOrEmpty(npcName))
        {
            lock (_entryLock)
            {
                ConsumedByNpcs.Add(npcName);
            }
        }
    }
}