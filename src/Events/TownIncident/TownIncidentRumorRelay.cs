using System;
using System.Collections.Generic;
using StardewModdingAPI;
using StardewValley;

namespace ValleytalkReborn;

/// <summary>TIE-007: the three surfaces that may circulate an incident rumor.</summary>
internal enum TownIncidentRumorConsumer { MainDialogue, AmbientBark, A2A }

/// <summary>
/// TIE-007: consumer-agnostic registry for the active town-incident rumor.
/// Owns the per-day claimed-NPC set (keyed by NPC name only, first consumer
/// wins), the MainDialogue-only daily cap, and the RFC outsider blacklist.
/// All state is memory-only: never persisted, never synced, reset through
/// <see cref="ResetDailyClaims"/> at the two existing TownIncidentEngine
/// lifecycle call sites (OnDayStarted, ResetMemoryState). Rendering stays
/// deterministic and phase-neutral (one static Contest line).
/// </summary>
internal static class TownIncidentRumorRelay
{
    internal const int MaxDailyRumors = 2;

    private const string HostRole = "Host";
    private const string ChampionRole = "Champion";
    private const string SkepticRole = "Skeptic";

    // RFC outsider blacklist: these NPCs do not circulate town rumors.
    // Single source of truth — moved here from TownIncidentRumorProvider.
    private static readonly HashSet<string> OutsiderBlacklist =
        new(StringComparer.OrdinalIgnoreCase) { "Wizard", "Krobus", "Leo", "Dwarf", "Linus" };

    // Consumer-agnostic: an NPC claimed by any consumer blocks every other
    // consumer for the rest of the day.
    private static HashSet<string> _claimedNpcs = new(StringComparer.OrdinalIgnoreCase);

    // Applies ONLY to TownIncidentRumorConsumer.MainDialogue.
    private static int _mainDialogueClaimCount;

    /// <summary>
    /// Non-mutating: full eligibility chain plus rendering. Used only for
    /// candidate building; never consumes a claim or a MainDialogue slot.
    /// </summary>
    internal static bool TryGetIncidentRumorPreview(string npcName, out string rumorText)
    {
        return TryResolveEligibleRumor(npcName, out rumorText);
    }

    /// <summary>
    /// Mutating: registers the NPC as claimed (consumer-agnostic) and consumes
    /// one MainDialogue slot only when <paramref name="consumer"/> is
    /// <see cref="TownIncidentRumorConsumer.MainDialogue"/>. Every rejection
    /// path returns false without mutating any state.
    /// </summary>
    internal static bool TryClaimIncidentRumor(
        string npcName, TownIncidentRumorConsumer consumer, out string rumorText)
    {
        rumorText = null;

        if (consumer == TownIncidentRumorConsumer.MainDialogue
            && _mainDialogueClaimCount >= MaxDailyRumors)
            return false;

        if (!TryResolveEligibleRumor(npcName, out string rumor))
            return false;

        rumorText = rumor;
        _claimedNpcs.Add(npcName);
        if (consumer == TownIncidentRumorConsumer.MainDialogue)
            _mainDialogueClaimCount++;

        return true;
    }

    /// <summary>True when the NPC was claimed today by any consumer.</summary>
    internal static bool HasClaimedToday(string npcName)
    {
        return !string.IsNullOrWhiteSpace(npcName) && _claimedNpcs.Contains(npcName);
    }

    internal static void ResetDailyClaims()
    {
        _mainDialogueClaimCount = 0;
        _claimedNpcs.Clear();
    }

    /// <summary>
    /// Shared eligibility chain: name, multiplayer boundary, active incident,
    /// participant/outsider exclusion, same-day duplicate claim, then render.
    /// Read-only with respect to claim/quota state.
    /// </summary>
    private static bool TryResolveEligibleRumor(string npcName, out string rumorText)
    {
        rumorText = null;

        if (string.IsNullOrWhiteSpace(npcName))
            return false;

        if (Context.IsMultiplayer)
            return false;

        var incident = TownIncidentEngine.CurrentIncident;
        if (incident == null)
            return false;

        if (IsParticipant(incident, npcName) || OutsiderBlacklist.Contains(npcName))
            return false;

        if (_claimedNpcs.Contains(npcName))
            return false;

        string rumor = BuildContestRumor(incident, npcName);
        if (string.IsNullOrWhiteSpace(rumor))
        {
            ModEntry.SMonitor?.Log(
                $"[TownIncidentRumor] Failed to build a Contest rumor line for incident '{incident.IncidentId}' (NPC '{npcName}'); no quota consumed.",
                LogLevel.Error);
            return false;
        }

        rumorText = rumor;
        return true;
    }

    private static bool IsParticipant(EventSlotContract incident, string npcName)
    {
        foreach (var assigned in incident.AssignedRoles.Values)
        {
            if (string.Equals(assigned, npcName, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    /// <summary>
    /// One deterministic phase-neutral Contest rumor line built from the
    /// incident shell (roles, event name) and the claiming NPC's name.
    /// Returns null when the shell is missing a pilot role (BUG path).
    /// </summary>
    private static string BuildContestRumor(EventSlotContract incident, string npcName)
    {
        if (!TryGetRoleNpc(incident, HostRole, out var host)
            || !TryGetRoleNpc(incident, ChampionRole, out var champion)
            || !TryGetRoleNpc(incident, SkepticRole, out var skeptic))
            return null;

        bool isZh = LocalizedContentManager.CurrentLanguageCode == LocalizedContentManager.LanguageCode.zh;
        return isZh
            ? $"{npcName} 听说了镇上最近的热议：{host} 要在酒吧办一场烹饪大赛，{champion} 准备卫冕冠军，而 {skeptic} 见人就嘀咕评审偏袒熟面孔。"
            : $"{npcName} has heard the talk of the town: {host} is hosting the {incident.EventName}, {champion} is out to defend the title, and {skeptic} keeps telling anyone who will listen that the judging favors the regulars.";
    }

    private static bool TryGetRoleNpc(EventSlotContract incident, string role, out string npcName)
    {
        return incident.AssignedRoles.TryGetValue(role, out npcName)
            && !string.IsNullOrWhiteSpace(npcName);
    }
}
