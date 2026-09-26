namespace ValleytalkReborn;

/// <summary>
/// Persisted root model of the Town Incident Engine (SaveData key
/// "valleytalk.town-incidents"). Daily rumor counters, mentioned-NPC sets and
/// in-flight scriptwriter state are memory-only and never stored here.
/// </summary>
internal sealed class TownIncidentData
{
    public int SchemaVersion { get; set; }
    public EventSlotContract ActiveIncident { get; set; }
    public string LastScheduleKey { get; set; }
}
