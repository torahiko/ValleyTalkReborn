using System;
using System.Collections.Generic;
using HarmonyLib;
using StardewValley;

namespace ValleytalkReborn
{
    [HarmonyPatch(typeof(MarriageDialogueReference), nameof(MarriageDialogueReference.GetDialogue))]
    public class MarriageDialogueReference_GetDialogue_Patch
    {
        public static readonly List<string> AddToNextDialogue = new List<string>();
        public static readonly object LockObj = new object();

        public static bool Prefix(ref MarriageDialogueReference __instance, ref Dialogue __result, NPC n)
        {
            if (n == null || __instance == null)
            {
                return true;
            }

            ModEntry.SMonitor.Log($"MarriageDialogueReference.GetDialogue called for {n.Name} with key {__instance.DialogueKey}", StardewModdingAPI.LogLevel.Trace);

            if (!DialogueBuilder.Instance.PatchNpc(n, ModEntry.Config.MarriageFrequency))
            {
                return true;
            }

            if (AsyncBuilder.Instance.AwaitingGeneration && AsyncBuilder.Instance.SpeakingNpc == n)
            {
                return true;
            }

            if (ModEntry.Config.EnableVanillaFirst)
                return true;

            string nextDialogue = null;
            lock (LockObj)
            {
                if (AddToNextDialogue.Count > 0)
                {
                    try
                    {
                        string text = $"{__instance.DialogueFile}:{__instance.DialogueKey}";
                        string text2 = __instance.IsGendered 
                            ? Game1.LoadStringByGender(n.Gender, text, __instance.Substitutions) 
                            : Game1.content.LoadString(text, __instance.Substitutions);

                        if (!string.IsNullOrWhiteSpace(text2))
                        {
                            AddToNextDialogue.Add(text2);
                        }
                    }
                    catch (Exception)
                    {
                        // If we can't find the current canon line, just skip it
                    }

                    nextDialogue = string.Join(" ", AddToNextDialogue);
                    AddToNextDialogue.Clear();
                }
            }

            var placeholder = new Dialogue(n, __instance.DialogueKey, "   ");
            AsyncBuilder.Instance.RequestNpcBasic(n, __instance.DialogueKey, nextDialogue ?? "");

            __result = placeholder;
            return false;
        }
    }
}