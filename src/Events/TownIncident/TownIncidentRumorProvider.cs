using System;
using System.Collections.Generic;
using StardewModdingAPI;
using StardewValley;

namespace ValleytalkReborn;

/// <summary>
/// TIE-004: main-dialogue incident rumor quota. Claims at most
/// MaxDailyRumors non-participant town NPCs per game day for the active
/// town incident and produces one deterministic static Contest rumor line.
/// All state is memory-only and reset at DayStarted, SaveLoaded and
/// ReturnedToTitle via TownIncidentEngine. Never calls
/// PerceptionManager.RecordGossip and never competes with
/// DailyHeadlinedGenerator's global Gossip queue.
/// </summary>
internal static class TownIncidentRumorProvider
{
    internal const int MaxDailyRumors = 2;

    private const string HostRole = "Host";
    private const string ChampionRole = "Champion";
    private const string SkepticRole = "Skeptic";

    // RFC outsider blacklist: these NPCs do not circulate town rumors.
    private static readonly HashSet<string> OutsiderBlacklist =
        new(StringComparer.OrdinalIgnoreCase) { "Wizard", "Krobus", "Leo", "Dwarf", "Linus" };

    private static int _dailyRumorCount;
    private static HashSet<string> _claimedNpcs = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Attempts to claim the daily incident rumor for one main-dialogue
    /// Gossip build. Returns true only for an eligible non-participant NPC
    /// while quota remains; every rejection path returns false without
    /// mutating the quota.
    /// </summary>
    internal static bool TryClaimMainDialogueRumor(string npcName, out string rumorText)
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

        if (_dailyRumorCount >= MaxDailyRumors)
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
        _dailyRumorCount++;
        _claimedNpcs.Add(npcName);
        return true;
    }

    internal static void ResetDailyRumorQuota()
    {
        _dailyRumorCount = 0;
        _claimedNpcs.Clear();
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
