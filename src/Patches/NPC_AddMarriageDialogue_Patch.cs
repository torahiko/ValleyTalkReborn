using System;
using System.Collections.Generic;
using HarmonyLib;
using StardewValley;

namespace ValleytalkReborn
{
    [HarmonyPatch(typeof(NPC), nameof(NPC.addMarriageDialogue))]
    public class NPC_AddMarriageDialogue_Patch
    {
        // 【优化】升级为 HashSet<string>，享用 O(1) 超高速查询
        private static readonly HashSet<string> SkipGeneratedDialogue = new(StringComparer.OrdinalIgnoreCase)
        {
            "NPC.cs.4463",
            "NPC.cs.4462",
            "NPC.cs.4470",
            "NPC.cs.4474",
            "NPC.cs.4481",
            "MultiplePetBowls_watered",
        };

        public static bool Prefix(ref NPC __instance, string dialogue_file, string dialogue_key, bool gendered, string[] substitutions)
        {
            if (__instance == null || string.IsNullOrEmpty(dialogue_key))
            {
                return true;
            }

            var dialogueRef = new MarriageDialogueReference(dialogue_file, dialogue_key, gendered, substitutions);
            if (!SkipGeneratedDialogue.Contains(dialogue_key))
            {
                __instance.shouldSayMarriageDialogue.Value = true;
                __instance.currentMarriageDialogue.Add(dialogueRef);
            }
            else
            {
                try
                {
                    string text = $"{dialogueRef.DialogueFile}:{dialogueRef.DialogueKey}";
                    string text2 = dialogueRef.IsGendered 
                        ? Game1.LoadStringByGender(__instance.Gender, text, dialogueRef.Substitutions) 
                        : Game1.content.LoadString(text, dialogueRef.Substitutions);

                    if (!string.IsNullOrWhiteSpace(text2))
                    {
                        lock (MarriageDialogueReference_GetDialogue_Patch.LockObj)
                        {
                            MarriageDialogueReference_GetDialogue_Patch.AddToNextDialogue.Add(text2);
                        }
                    }
                }
                catch (Exception)
                {
                    // If we can't find the canon line, just skip it
                }
            }

            return false; // Skip original method
        }
    }        
}