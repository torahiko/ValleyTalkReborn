using System;
using System.Linq;
using HarmonyLib;
using StardewValley;

namespace ValleytalkReborn
{
    [HarmonyPatch(typeof(Game1), nameof(Game1.DrawDialogue), new Type[] { typeof(Dialogue) })]
    public class Game1_DrawDialogue_Patch
    {
        internal static bool DrawingDialogue = false;

        public static bool Prefix(Dialogue dialogue)
        {
            if (dialogue?.dialogues == null || dialogue.dialogues.Count == 0)
            {
                return true;
            }

            var firstText = dialogue.dialogues.First()?.Text;
            if (firstText != null && firstText.StartsWith(SldConstants.DialogueSkipTag))
            {
                return false;
            }

            DrawingDialogue = true;
            return true;
        }

        public static void Postfix()
        {
            DrawingDialogue = false;
        }
    }
}