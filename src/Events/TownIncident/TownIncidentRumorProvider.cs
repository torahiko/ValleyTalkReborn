using System.Collections.Generic;
using StardewModdingAPI;
using StardewValley;

namespace ValleytalkReborn;

/// <summary>
/// TIE-004/TIE-007/TIE-009D: main-dialogue incident rumor facade. Eligibility,
/// quota, outsider-blacklist and claim state live in
/// <see cref="TownIncidentRumorRelay"/>; this type preserves the historical
/// MainDialogue entry points and owns the single archetype-aware rumor renderer
/// that both the claim path and the relay preview path route through.
/// </summary>
internal static class TownIncidentRumorProvider
{
    /// <summary>
    /// Attempts to claim the daily incident rumor for one main-dialogue
    /// Gossip build. Returns true only for an eligible non-participant NPC
    /// while quota remains; every rejection path returns false without
    /// mutating the quota.
    /// </summary>
    internal static bool TryClaimMainDialogueRumor(string npcName, out string rumorText)
    {
        return TownIncidentRumorRelay.TryClaimIncidentRumor(
            npcName, TownIncidentRumorConsumer.MainDialogue, out rumorText);
    }

    internal static void ResetDailyRumorQuota()
    {
        TownIncidentRumorRelay.ResetDailyClaims();
    }

    /// <summary>
    /// TIE-009D: the single incident-rumor renderer. Dispatches on
    /// <see cref="EventSlotContract.ArchetypeId"/> through
    /// <see cref="TownIncidentArchetypeCatalog"/> and builds one deterministic
    /// line from the shell (EventName plus the archetype's assigned role NPCs)
    /// and the spreading NPC's name.
    /// <para>
    /// Failure paths: an archetype id that is not in the catalog is a boundary
    /// case — returns false with no log here, the claim site logs once at Error.
    /// A required role that does not resolve to a non-empty assigned NPC, or a
    /// rendered line that ends up empty, is a BUG — logged at Error with the
    /// incident id and the offending role, then false.
    /// </para>
    /// </summary>
    internal static bool TryRenderIncidentRumor(
        EventSlotContract incident, string npcName, out string rumorText)
    {
        rumorText = null;

        if (!TownIncidentArchetypeCatalog.TryGetDefinition(incident.ArchetypeId, out var definition))
            return false;

        var roleNpcs = new List<string>(definition.RequiredRoles.Count);
        foreach (string role in definition.RequiredRoles)
        {
            if (!incident.AssignedRoles.TryGetValue(role, out string assigned)
                || string.IsNullOrWhiteSpace(assigned))
            {
                ModEntry.SMonitor?.Log(
                    $"[TownIncidentRumor] Incident '{incident.IncidentId}' (archetype '{incident.ArchetypeId}') has no assigned NPC for required role '{role}'; no rumor rendered and no quota consumed.",
                    LogLevel.Error);
                return false;
            }

            roleNpcs.Add(assigned);
        }

        bool isChinese = LocalizedContentManager.CurrentLanguageCode
                         == LocalizedContentManager.LanguageCode.zh;

        string rumor = TownIncidentTemplateCatalog.BuildIncidentRumor(
            incident.ArchetypeId, isChinese, npcName, roleNpcs, incident.EventName);

        if (string.IsNullOrWhiteSpace(rumor))
        {
            ModEntry.SMonitor?.Log(
                $"[TownIncidentRumor] Incident rumor rendering produced empty text for incident '{incident.IncidentId}' (archetype '{incident.ArchetypeId}', NPC '{npcName}', roles {string.Join(", ", definition.RequiredRoles)}); no rumor rendered and no quota consumed.",
                LogLevel.Error);
            return false;
        }

        rumorText = rumor;
        return true;
    }
}
