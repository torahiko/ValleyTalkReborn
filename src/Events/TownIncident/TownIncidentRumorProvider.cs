namespace ValleytalkReborn;

/// <summary>
/// TIE-004/TIE-007: main-dialogue incident rumor facade. All eligibility,
/// quota, outsider-blacklist and rendering state now lives in
/// <see cref="TownIncidentRumorRelay"/>; this type only preserves the
/// historical MainDialogue entry points (signature, quota semantics and log
/// texts unchanged) for the PerceptionInjector call site.
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
}
