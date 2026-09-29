namespace ValleytalkReborn;

/// <summary>
/// Persisted root model of the Town Incident Engine (SaveData key
/// "valleytalk.town-incidents"). Daily rumor counters, mentioned-NPC sets and
/// in-flight scriptwriter state are memory-only and never stored here.
/// <para>
/// TIE-009A adds no member and no schema version: the persisted archetype is
/// carried by <see cref="EventSlotContract.ArchetypeId"/>, an exact
/// case-sensitive identifier resolved by
/// <see cref="TownIncidentArchetypeCatalog.TryGetDefinition(string, out IncidentArchetypeDefinition)"/>.
/// Existing "Contest" slots therefore keep round-tripping without migration.
/// </para>
/// </summary>
internal sealed class TownIncidentData
{
    public int SchemaVersion { get; set; }
    public EventSlotContract ActiveIncident { get; set; }
    public string LastScheduleKey { get; set; }
}
