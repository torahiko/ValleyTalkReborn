using System;
using System.Collections.Generic;
using StardewModdingAPI;
using StardewValley;
using ValleytalkReborn.Dialogue.Coordination;

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
/// deterministic and phase-neutral — one archetype-aware line produced by
/// <see cref="TownIncidentRumorProvider.TryRenderIncidentRumor(EventSlotContract, string, out string)"/>,
/// the single renderer shared with the preview path.
/// </summary>
internal static class TownIncidentRumorRelay
{
    internal const int MaxDailyRumors = 2;

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

    /// <summary>
    /// TIE-008：非变异版 A2A 预览。过滤参与者名单后渲染一条面向提示语境的
    /// 事件传闻；不写共享认领表、不动 MainDialogue 配额（含 BUG 路径）。
    /// </summary>
    internal static bool TryGetIncidentRumorPreviewForA2A(
        IReadOnlyList<NPC> participants, out string rumorText)
    {
        return TryResolveA2ARumor(participants, out _, out rumorText);
    }

    /// <summary>
    /// TIE-008：变异版。A2A 无独立配额，所有通过过滤的参与者一并写入 TIE-007
    /// 的消费者无关认领表，自动抑制这些 NPC 当日被 MainDialogue / Bark / A2A
    /// 再次认领。任何拒绝路径零变异。
    /// </summary>
    internal static bool TryClaimA2AIncidentRumor(
        IReadOnlyList<NPC> participants, out string rumorText)
    {
        rumorText = null;

        if (!TryResolveA2ARumor(participants, out var eligible, out string rumor))
            return false;

        rumorText = rumor;
        foreach (var npc in eligible)
            _claimedNpcs.Add(npc.Name);

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

        // TIE-009D: single renderer for every archetype — the claim path and
        // both preview paths share it, so no surface can drift.
        if (!TownIncidentRumorProvider.TryRenderIncidentRumor(incident, npcName, out string rumor))
        {
            ModEntry.SMonitor?.Log(
                $"[TownIncidentRumor] No incident rumor for incident '{incident.IncidentId}' (archetype '{incident.ArchetypeId}', NPC '{npcName}'); no quota consumed.",
                LogLevel.Error);
            return false;
        }

        rumorText = rumor;
        return true;
    }

    /// <summary>
    /// TIE-008 共用资格链：先按共享认领表过滤参与者名单（剔除事件参与者、
    /// RFC 案外人黑名单与当日已认领者），要求保留人数 ≥ 2；再以首位合格者为
    /// 锚渲染一条事件事实句；最后拒绝点名在场参与者的文本。只读：共享认领表
    /// 只由 TryClaimA2AIncidentRumor 显式写入，此处零副作用。
    /// </summary>
    private static bool TryResolveA2ARumor(
        IReadOnlyList<NPC> participants, out List<NPC> eligible, out string rumorText)
    {
        eligible = new List<NPC>();
        rumorText = null;

        if (participants == null)
            return false;

        var incident = TownIncidentEngine.CurrentIncident;
        if (incident == null)
            return false;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var npc in participants)
        {
            if (npc == null || string.IsNullOrWhiteSpace(npc.Name))
                continue;

            if (!seen.Add(npc.Name))
                continue;

            if (IsParticipant(incident, npc.Name) || OutsiderBlacklist.Contains(npc.Name))
                continue;

            if (_claimedNpcs.Contains(npc.Name))
                continue;

            eligible.Add(npc);
        }

        if (eligible.Count < 2)
            return false;

        if (!TryResolveEligibleRumor(eligible[0].Name, out string preview))
            return false;

        string rumor = StripSubjectPrefix(preview);
        if (string.IsNullOrWhiteSpace(rumor))
        {
            ModEntry.SMonitor?.Log(
                $"[TownIncidentRumor] A2A 事件传闻渲染为空（锚点 NPC '{eligible[0].Name}'，事件 '{incident.IncidentId}'）；未消耗任何认领。",
                LogLevel.Error);
            return false;
        }

        if (InvolvesAnyParticipant(rumor, participants))
            return false;

        rumorText = rumor;
        return true;
    }

    /// <summary>
    /// 剥除主对话渲染中的 "{npcName} has heard …: " 主语前缀，只留事件事实句
    /// ——A2A 语境行不得点名任何在场参与者。前缀缺失或剥离后为空 ⇒ null（BUG 路径）。
    /// </summary>
    private static string StripSubjectPrefix(string preview)
    {
        string trimmed = preview.Trim();

        bool isZh = LocalizedContentManager.CurrentLanguageCode
                    == LocalizedContentManager.LanguageCode.zh;
        int separator = isZh ? trimmed.IndexOf('：') : trimmed.IndexOf(':');
        if (separator < 0 || separator >= trimmed.Length - 1)
            return null;

        string fact = trimmed.Substring(separator + 1).Trim();
        return string.IsNullOrWhiteSpace(fact) ? null : fact;
    }

    /// <summary>
    /// 与 A2APromptBuilder 现有 InvolvesParticipant 同一子串语义
    /// （OrdinalIgnoreCase），覆盖 Name / displayName / 中文名三种写法。
    /// </summary>
    private static bool InvolvesAnyParticipant(string rumor, IReadOnlyList<NPC> participants)
    {
        foreach (var npc in participants)
        {
            if (npc == null) continue;

            if (!string.IsNullOrEmpty(npc.Name)
                && rumor.Contains(npc.Name, StringComparison.OrdinalIgnoreCase))
                return true;

            if (!string.IsNullOrEmpty(npc.displayName)
                && rumor.Contains(npc.displayName, StringComparison.OrdinalIgnoreCase))
                return true;

            string zh = NpcNameLocalizer.GetZhName(npc.Name);
            if (!string.IsNullOrEmpty(zh)
                && rumor.Contains(zh, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
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

}
