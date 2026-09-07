using HarmonyLib;
using StardewValley;

namespace ValleytalkReborn
{
    [HarmonyPatch(typeof(NPC), nameof(NPC.checkForNewCurrentDialogue))]
    public class NPC_CheckForNewCurrentDialogue_Patch
    {
        private static string _lastCheckedNpc = null;
        private static int _lastHeartLevel = -1;

        internal static void ClearDedupState()
        {
            _lastCheckedNpc = null;
            _lastHeartLevel = -1;
        }

        public static bool Prefix(ref NPC __instance, ref bool __result, int heartLevel, bool noPreface)
        {
            if (__instance == null)
            {
                return true;
            }

            // Deduplication: skip logging if same NPC and heart level checked consecutively
            if (_lastCheckedNpc == __instance.Name && _lastHeartLevel == heartLevel)
            {
                // Still execute the core logic, just skip the log
            }
            else
            {
                _lastCheckedNpc = __instance.Name;
                _lastHeartLevel = heartLevel;

                if (ModEntry.Config.Debug)
                {
                    ModEntry.SMonitor.Log($"NPC {__instance.Name} checking for new dialogue at heart level {heartLevel}", StardewModdingAPI.LogLevel.Trace);
                }
            }

            if (!DialogueBuilder.Instance.PatchNpc(__instance, ModEntry.Config.GeneralFrequency, true))
            {
                return true;
            }

            // Check network availability early (Android only)
            if (!NetworkAvailabilityChecker.IsNetworkAvailableWithRetry())
            {
                if (ModEntry.Config.Debug)
                {
                    ModEntry.SMonitor.Log($"Network not available, skipping AI new dialogue check for {__instance.Name}", StardewModdingAPI.LogLevel.Trace);
                }
                return true; // Use default behavior
            }

            // 🌟 核心修复：移除原先向 CurrentDialogue.Push(DialogueGenerationTag) 强塞 $$$%%% 假对象的逻辑。
            // 避免污染 NPC 堆栈并在农舍等场景弹出 $$$%%%。
            // AI 对话全权由 NPC_CheckAction_Patch 统一调度。
            return true;
        }
    }
}