using HarmonyLib;
using StardewValley;

namespace ValleytalkReborn
{
    [HarmonyPatch(typeof(StardewValley.Dialogue), nameof(StardewValley.Dialogue.TryGetDialogue))]
    public class Dialogue_TryGetDialogue_Patch
    {
        public static bool Prefix(ref StardewValley.Dialogue __instance, ref StardewValley.Dialogue __result, NPC speaker, string translationKey)
        {
            if (speaker == null || string.IsNullOrEmpty(translationKey))
            {
                return true;
            }

            ModEntry.SMonitor.Log($"Dialogue.TryGetDialogue called for {speaker.Name} with key {translationKey}", StardewModdingAPI.LogLevel.Trace);
            if (!DialogueBuilder.Instance.PatchNpc(speaker, ModEntry.Config.GeneralFrequency, true))
            {
                return true;
            }
            return true;
        }
    }
}