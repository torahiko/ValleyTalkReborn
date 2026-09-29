using System;
using System.Collections.Generic;

namespace ValleytalkReborn;

/// <summary>
/// TIE-009A: fixed shell content and role requirements for one town incident
/// archetype. Every member is init-only, so a definition is immutable once
/// built and safe to share from the static catalog.
/// </summary>
internal sealed class IncidentArchetypeDefinition
{
    /// <summary>Exact, case-sensitive persisted archetype identifier.</summary>
    public string ArchetypeId { get; init; }

    public IReadOnlyList<string> RequiredRoles { get; init; }

    public int DefaultDurationDays { get; init; }

    public string DefaultClimaxLocation { get; init; }

    public int DefaultClimaxTimeOfDay { get; init; }

    public string EventName { get; init; }

    public string IncidentTheme { get; init; }
}

/// <summary>
/// TIE-009A: the four archetypes a town incident slot may be built from. Pure
/// model code — no Game1, SMAPI, Harmony or LLM dependency, and no runtime
/// discovery pass. Definitions are static and immutable; lookup is an exact
/// <see cref="StringComparison.Ordinal"/> match on <see cref="IncidentArchetypeDefinition.ArchetypeId"/>,
/// so an unknown id is reported as "not found" and is never converted to Contest.
/// </summary>
internal static class TownIncidentArchetypeCatalog
{
    internal static IReadOnlyList<IncidentArchetypeDefinition> Definitions { get; } = new[]
    {
        new IncidentArchetypeDefinition
        {
            ArchetypeId = nameof(TownIncidentArchetype.Contest),
            RequiredRoles = new[] { "Host", "Champion", "Skeptic" },
            DefaultDurationDays = 8,
            DefaultClimaxLocation = "Saloon",
            DefaultClimaxTimeOfDay = 1900,
            EventName = "Saloon Cook-Off",
            IncidentTheme = "A friendly cooking contest strains old rivalries in Pelican Town.",
        },
        new IncidentArchetypeDefinition
        {
            ArchetypeId = nameof(TownIncidentArchetype.Friction),
            RequiredRoles = new[] { "Victim", "Culprit", "Witness" },
            DefaultDurationDays = 6,
            DefaultClimaxLocation = "Pierre's General Store",
            DefaultClimaxTimeOfDay = 1200,
            EventName = "Store Ledger Dispute",
            IncidentTheme = "A disputed store ledger sets neighbors against each other in Pelican Town.",
        },
        new IncidentArchetypeDefinition
        {
            ArchetypeId = nameof(TownIncidentArchetype.Mystery),
            RequiredRoles = new[] { "Loser", "Suspect", "Investigator" },
            DefaultDurationDays = 6,
            DefaultClimaxLocation = "Saloon",
            DefaultClimaxTimeOfDay = 2000,
            EventName = "Vanished Heirloom",
            IncidentTheme = "A keepsake vanishes from the Saloon and suspicion falls on the regulars.",
        },
        new IncidentArchetypeDefinition
        {
            ArchetypeId = nameof(TownIncidentArchetype.Collaboration),
            RequiredRoles = new[] { "Organizer", "Worker", "Slacker" },
            DefaultDurationDays = 6,
            DefaultClimaxLocation = "Community Center",
            DefaultClimaxTimeOfDay = 1000,
            EventName = "Community Center Restoration",
            IncidentTheme = "A restoration effort divides the workload and tests old friendships.",
        },
    };

    /// <summary>
    /// Resolves <paramref name="archetypeId"/> with an exact case-sensitive
    /// (Ordinal) match. Returns false with a null definition for any unknown
    /// id — including null, empty and differently-cased spellings — instead of
    /// falling back to Contest. Logging belongs to the engine boundary (TIE-009B).
    /// </summary>
    internal static bool TryGetDefinition(string archetypeId, out IncidentArchetypeDefinition definition)
    {
        foreach (var candidate in Definitions)
        {
            if (string.Equals(candidate.ArchetypeId, archetypeId, StringComparison.Ordinal))
            {
                definition = candidate;
                return true;
            }
        }

        definition = null;
        return false;
    }
}
