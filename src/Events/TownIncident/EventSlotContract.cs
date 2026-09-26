using System.Collections.Generic;

namespace ValleytalkReborn;

/// <summary>TIE-001: three-act phase of a town incident.</summary>
internal enum IncidentPhase
{
    Inception,
    Escalation,
    Climax
}

/// <summary>Per-role acting brief for one incident phase.</summary>
internal sealed class RolePhaseBrief
{
    public string Motivation { get; set; }
    public string PublicOpinion { get; set; }
}

/// <summary>
/// Persisted contract for one town incident event slot. PhaseScripts is keyed
/// by phase then NPC name; BranchOutcomes is keyed by RuntimeFlags flag name
/// then keyword, so both TryGetActorBrief and RecordChoice stay bounded
/// dictionary lookups.
/// </summary>
internal sealed class EventSlotContract
{
    public string IncidentId { get; set; }
    public string ArchetypeId { get; set; }
    public int StartGameDay { get; set; }
    public int DurationDays { get; set; }
    public string ClimaxLocation { get; set; }
    public int ClimaxTimeOfDay { get; set; }
    public Dictionary<string, string> AssignedRoles { get; set; }
    public string EventName { get; set; }
    public string IncidentTheme { get; set; }
    public Dictionary<IncidentPhase, Dictionary<string, RolePhaseBrief>> PhaseScripts { get; set; }
    public Dictionary<string, Dictionary<string, string>> BranchOutcomes { get; set; }
    public Dictionary<string, bool> RuntimeFlags { get; set; }
}
