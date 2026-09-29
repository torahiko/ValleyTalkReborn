using System.Collections.Generic;

namespace ValleytalkReborn;

/// <summary>TIE-001: three-act phase of a town incident.</summary>
internal enum IncidentPhase
{
    Inception,
    Escalation,
    Climax
}

/// <summary>
/// TIE-009A: the four incident archetypes an event slot may be built from.
/// The member name is the persisted <see cref="EventSlotContract.ArchetypeId"/>
/// value and is matched with <see cref="System.StringComparison.Ordinal"/>.
/// </summary>
internal enum TownIncidentArchetype
{
    Contest,
    Friction,
    Mystery,
    Collaboration
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
/// dictionary lookups. TIE-009C: both are installed by
/// <see cref="TownIncidentScriptwriter.CreateFallback(EventSlotContract, bool)"/>
/// from the archetype's own static fallback — the phase scripts cover every
/// <see cref="IncidentArchetypeDefinition.RequiredRoles"/> entry (re-keyed onto
/// the assigned NPCs) and the branch group keys come from that archetype only.
/// </summary>
internal sealed class EventSlotContract
{
    public string IncidentId { get; set; }

    /// <summary>
    /// TIE-009A: exact, case-sensitive archetype identifier resolved by
    /// <see cref="TownIncidentArchetypeCatalog.TryGetDefinition(string, out IncidentArchetypeDefinition)"/>.
    /// </summary>
    public string ArchetypeId { get; set; }

    public int StartGameDay { get; set; }
    public int DurationDays { get; set; }
    public string ClimaxLocation { get; set; }
    public int ClimaxTimeOfDay { get; set; }

    /// <summary>
    /// Role name → NPC name. Runtime shape is unchanged by TIE-009A: the
    /// dictionary stays <see cref="Dictionary{TKey, TValue}"/> of string→string.
    /// </summary>
    public Dictionary<string, string> AssignedRoles { get; set; }

    public string EventName { get; set; }
    public string IncidentTheme { get; set; }
    public Dictionary<IncidentPhase, Dictionary<string, RolePhaseBrief>> PhaseScripts { get; set; }
    public Dictionary<string, Dictionary<string, string>> BranchOutcomes { get; set; }
    public Dictionary<string, bool> RuntimeFlags { get; set; }
}
