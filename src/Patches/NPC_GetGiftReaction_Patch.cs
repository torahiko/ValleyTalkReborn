using HarmonyLib;
using StardewValley;

namespace ValleytalkReborn
{
    [HarmonyPatch(typeof(NPC), nameof(NPC.GetGiftReaction))]
    public class NPC_GetGiftReaction_Patch
    {
        public static bool Prefix(ref NPC __instance, ref Dialogue __result, Farmer giver, StardewValley.Object gift, int taste)
        {
            if (__instance == null || gift == null)
            {
                return true;
            }

            var giftName = gift.DisplayName ?? gift.Name ?? "Gift";
            ModEntry.SMonitor.Log($"NPC {__instance.Name} trying to get gift reaction for {giftName}", StardewModdingAPI.LogLevel.Trace);

            if (!DialogueBuilder.Instance.PatchNpc(__instance, ModEntry.Config.GiftFrequency))
            {
                return true;
            }

            if (AsyncBuilder.Instance.AwaitingGeneration && AsyncBuilder.Instance.SpeakingNpc == __instance)
            {
                return true;
            }

            // Check network availability early (Android only)
            if (!NetworkAvailabilityChecker.IsNetworkAvailableWithRetry())
            {
                ModEntry.SMonitor.Log($"Network not available, skipping AI gift reaction for {__instance.Name}", StardewModdingAPI.LogLevel.Trace);
                return true; // Use default behavior
            }

            AsyncBuilder.Instance.RequestNpcGiftResponse(__instance, gift, taste);

            // Record gift-giving action via new history system
            DialogueHistoryManager.Instance.RecordGiftGiven(__instance.Name, giftName, taste);

            // Record gift perception directly here — gift object is guaranteed non-null at this point
            GiftSubscriber.RecordGiftPerceptionFromPatch(__instance.Name, gift.DisplayName ?? gift.Name, gift.ItemId);

            // 【核心修复】：返回一个只有空格的占位对话，千万不能调 exitCurrentDialogue()。
            // 引擎拿到这个返回值后，会自动在屏幕上打开对话框，从而完美唤醒 AsyncBuilder。
            __result = new Dialogue(__instance, "", "   ");

            return false; // Prevent default behavior
        }
    }
}