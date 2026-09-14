using System;
using HarmonyLib;
using StardewModdingAPI;
using StardewValley;

namespace ValleytalkReborn;

[HarmonyPatch(typeof(NPC), nameof(NPC.tryToReceiveActiveObject))]
public static class NpcTryToReceiveActiveObject_Patch
{
    [HarmonyPrefix]
    public static bool Prefix(NPC __instance, Farmer who, bool probe, ref bool __result)
    {
        if (__instance == null || who == null || who.ActiveObject == null || !Context.IsWorldReady)
            return true;

        if (probe) return true;

        bool committed = false;
        try
        {
            var decision = GiftVerdictResolver.Resolve(__instance, who, who.ActiveObject);
            ModEntry.SMonitor?.Log(
                $"[GiftVerdictResolver] NPC={__instance.Name} item={who.ActiveObject.QualifiedItemId} probe=false → {decision.Verdict} ({decision.DetailReason})",
                LogLevel.Trace);

            if (decision.Verdict == HandoverVerdict.Passthrough_Vanilla
                || decision.Verdict == HandoverVerdict.Block_NotGiftable
                || decision.Verdict == HandoverVerdict.NormalGift_Accept)
                return true;

            if (!DialogueBuilder.Instance.PatchNpc(__instance)
                || !NetworkAvailabilityChecker.IsNetworkAvailableWithRetry())
            {
                ModEntry.SMonitor?.Log(
                    $"[HandoverGate] AI unavailable, vanilla fallback: {decision.Verdict}",
                    LogLevel.Trace);
                return true;
            }

            who.Halt();
            who.faceGeneralDirection(__instance.getStandingPosition(), 0, false, false);
            DynamicBarkManager.CancelA2AForNpc(__instance.Name, "Handover");

            if (!HandoverSettlement.TryApply(decision.Verdict, __instance, who,
                    who.ActiveObject, decision, out var failReason))
            {
                ModEntry.SMonitor?.Log(
                    $"[HandoverGate] settlement failed ({failReason}), vanilla fallback: {decision.Verdict}",
                    LogLevel.Warn);
                return true;
            }

            committed = true;

            try
            {
                PendingTopicManager.Instance.SetPendingTopic(__instance.Name,
                    HandoverPromptProtocols.BuildTopic(decision.Verdict, __instance, who.ActiveObject),
                    3, 10);
            }
            catch (Exception tex)
            {
                ModEntry.SMonitor?.Log(
                    $"[HandoverGate] topic inject failed: {tex.Message}",
                    LogLevel.Error);
            }

            AsyncBuilder.Instance.RequestNpcHandover(__instance, decision.Verdict, who.ActiveObject);
            if (!AsyncBuilder.Instance.AwaitingGeneration)
            {
                ModEntry.SMonitor?.Log(
                    "[HandoverGate] dispatch dropped post-settlement (unexpected).",
                    LogLevel.Warn);
                bool isZh = LocalizedContentManager.CurrentLanguageCode
                    .ToString().StartsWith("zh", StringComparison.OrdinalIgnoreCase);
                Game1.addHUDMessage(new HUDMessage(
                    isZh ? "这一刻已经发生，但对话暂时无法生成。"
                         : "The moment happened, but dialogue can't be generated right now.",
                    3));
            }
        }
        catch (Exception ex)
        {
            if (!committed)
            {
                ModEntry.SMonitor?.Log(
                    $"[HandoverGate] pre-settlement failure, vanilla fallback: {ex.Message}",
                    LogLevel.Error);
                return true;
            }
            ModEntry.SMonitor?.Log(
                $"[HandoverGate] post-settlement failure (no vanilla fallback): {ex.Message}",
                LogLevel.Error);
        }

        __result = true;
        return false;
    }
}
