using System;
using System.Linq;
using HarmonyLib;
using StardewValley;
using StardewValley.Menus;
using ValleytalkReborn.UI;

namespace ValleytalkReborn
{
    [HarmonyPatch(typeof(Game1), nameof(Game1.DrawDialogue), new Type[] { typeof(StardewValley.Dialogue) })]
    public class Game1_DrawDialogue_Patch
    {
        internal static bool DrawingDialogue = false;

        public static bool Prefix(StardewValley.Dialogue dialogue)
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
            // VT-UI-005 Stage 2：把刚上屏的原版对话框绑定为待消费选择载荷的身份凭据，
            // 只有这个 Box 关闭时才允许弹出浮动选择框（事件打断等错位关闭自动作废）。
            if (ModEntry.Config.ChoiceBoxStyle == ChoiceBoxStyle.Custom
                && PendingChoiceStore.TryPeek(out var pending)
                && Game1.activeClickableMenu is DialogueBox db)
            {
                pending.BoxRef = db;
            }

            DrawingDialogue = false;
        }
    }
}
