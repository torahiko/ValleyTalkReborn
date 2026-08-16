// NPC_PushTemporaryDialogue_Patch.cs
using System;
using HarmonyLib;
using StardewValley;
using StardewModdingAPI;

namespace ValleytalkReborn
{
    [HarmonyPatch(typeof(NPC), "_PushTemporaryDialogue")]
    public class NPC_PushTemporaryDialogue_Patch
    {
        public static bool Prefix(ref NPC __instance, string translationKey)
        {
            if (__instance == null || string.IsNullOrEmpty(translationKey))
                return true;

            // ★ 日志降级：Trace
            ModEntry.SMonitor.Log($"[PushTempDialogue] {__instance.Name} key='{translationKey}'", LogLevel.Trace);

            if (!DialogueBuilder.Instance.PatchNpc(__instance, ModEntry.Config.GeneralFrequency, true))
                return true;

            if (!NetworkAvailabilityChecker.IsNetworkAvailableWithRetry())
            {
                // ★ 日志降级：Trace
                ModEntry.SMonitor.Log($"[PushTempDialogue] Network not available, skipping for {__instance.Name}", LogLevel.Trace);
                return true;
            }

            try
            {
                if (translationKey.StartsWith("Resort") && translationKey.Length >= 6)
                {
                    string path = $"Resort_Marriage{translationKey.Substring(6)}";
                    if (Game1.content.LoadStringReturnNullIfNotFound(path) != null)
                        translationKey = path;
                }

                if (__instance.CurrentDialogue != null && __instance.CurrentDialogue.Count != 0)
                {
                    var peekDialogue = __instance.CurrentDialogue.Peek();
                    if (peekDialogue != null && peekDialogue.temporaryDialogueKey == translationKey)
                        return true;
                }

                var originalString = Game1.content.LoadString(translationKey);

                // ★ 修复：先尝试注册请求，注册成功后才创建占位框
                bool accepted = AsyncBuilder.Instance.TryRequestNpcBasic(__instance, translationKey, originalString);
                if (!accepted)
                {
                    // 被拒就走原版，不留孤立框
                    return true;
                }

                Game1.activeClickableMenu = new StardewValley.Menus.DialogueBox(
                    new Dialogue(__instance, translationKey, "   "));

                // ★ 日志降级：Trace
                ModEntry.SMonitor.Log($"[PushTempDialogue] Placeholder created, awaiting={AsyncBuilder.Instance.AwaitingGeneration}", LogLevel.Trace);

                return false;
            }
            catch (Exception ex)
            {
                ModEntry.SMonitor.Log($"[PushTempDialogue] Error: {ex.Message}", LogLevel.Warn);
                return true;
            }
        }
    }
}