using System;
using HarmonyLib;
using StardewValley;

namespace ValleytalkReborn
{
    [HarmonyPatch(typeof(NPC), "_PushTemporaryDialogue")]
    public class NPC_PushTemporaryDialogue_Patch
    {
        public static bool Prefix(ref NPC __instance, string translationKey)
        {
            if (__instance == null || string.IsNullOrEmpty(translationKey))
            {
                return true;
            }

            ModEntry.SMonitor.Log($"NPC {__instance.Name} pushing temporary dialogue with key '{translationKey}'", StardewModdingAPI.LogLevel.Trace);

            if (!DialogueBuilder.Instance.PatchNpc(__instance, ModEntry.Config.GeneralFrequency, true))
            {
                return true;
            }

            // Check network availability early (Android only)
            if (!NetworkAvailabilityChecker.IsNetworkAvailableWithRetry())
            {
                ModEntry.SMonitor.Log($"Network not available, skipping AI temporary dialogue for {__instance.Name}", StardewModdingAPI.LogLevel.Trace);
                return true; // Use default behavior
            }

            try
            {
                if (translationKey.StartsWith("Resort") && translationKey.Length >= 6)
                {
                    string path = $"Resort_Marriage{translationKey.Substring(6)}";
                    if (Game1.content.LoadStringReturnNullIfNotFound(path) != null)
                    {
                        translationKey = path;
                    }
                }

                if (__instance.CurrentDialogue != null && __instance.CurrentDialogue.Count != 0)
                {
                    var peekDialogue = __instance.CurrentDialogue.Peek();
                    if (peekDialogue != null && peekDialogue.temporaryDialogueKey == translationKey)
                    {
                        return true;
                    }
                }

                var originalString = Game1.content.LoadString(translationKey);

                AsyncBuilder.Instance.RequestNpcBasic(__instance, translationKey, originalString);
                __instance.CurrentDialogue?.Push(new Dialogue(__instance, translationKey, "   ")
                {
                    removeOnNextMove = true,
                    temporaryDialogueKey = translationKey
                });

                return false;
            }
            catch (Exception ex)
            {
                ModEntry.SMonitor.Log($"Error in NPC_PushTemporaryDialogue_Patch: {ex.Message}", StardewModdingAPI.LogLevel.Warn);
                return true; // 【Bug 修复】出错时允许原版逻辑执行，防止卡死
            }
        }
    }
}