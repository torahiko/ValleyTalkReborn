using HarmonyLib;
using StardewValley;

namespace ValleytalkReborn
{
    [HarmonyPatch(typeof(GameLocation), nameof(GameLocation.GetLocationOverrideDialogue))]
    public class GameLocation_GetLocationOverrideDialogue_Patch
    {
        public static bool Prefix(ref GameLocation __instance, ref string __result, NPC character)
        {
            if (character == null)
            {
                return true;
            }

            ModEntry.SMonitor.Log($"GameLocation.GetLocationOverrideDialogue called for {character.Name} in {__instance?.Name ?? "Unknown"}", StardewModdingAPI.LogLevel.Trace);

            if (!DialogueBuilder.Instance.PatchNpc(character, ModEntry.Config.GeneralFrequency, true))
            {
                return true;
            }

            if (ModEntry.Config.EnableVanillaFirst)
                return true;

            __result = SldConstants.DialogueGenerationTag;
            return false;
        }
    }
}