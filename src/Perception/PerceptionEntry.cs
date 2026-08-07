using System;

namespace ValleyTalk;

/// <summary>
/// Represents a single perception record that an NPC can "know" about.
/// Only the latest record per Key is kept — new records overwrite old ones.
/// </summary>
internal class PerceptionEntry
{
    /// <summary>Behavior key, e.g. "Eat", "Fish", "Talk", "Harvest".</summary>
    public string Key { get; set; } = "";

    /// <summary>Pre-filled template sentence describing the action.</summary>
    public string Template { get; set; } = "";

    /// <summary>Witness NPC name. Null or empty means global (town-wide broadcast).</summary>
    public string NpcName { get; set; } = "";

    /// <summary>When the perception was recorded.</summary>
    public DateTime Timestamp { get; set; } = DateTime.Now;

    /// <summary>Lifetime in minutes before the perception expires.</summary>
    public int LifetimeMinutes { get; set; } = 5;

    /// <summary>Whether this is a global (town-wide) broadcast perception.</summary>
    public bool IsGlobal { get; set; } = false;

    /// <summary>Stardew Valley 1.6 string item ID associated with this perception (e.g. for "Eat" actions).</summary>
    public string ItemId { get; set; } = null;
}
