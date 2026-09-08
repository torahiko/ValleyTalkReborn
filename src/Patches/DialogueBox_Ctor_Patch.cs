using System.Linq;
using HarmonyLib;
using StardewValley;
using StardewValley.Menus;

namespace ValleytalkReborn
{
    /// <summary>
    /// Patch DialogueBox(Dialogue) constructor to capture vanilla NPC dialogue into history.
    /// Game1.DrawDialogue is not always called for vanilla dialogue; the engine may construct
    /// a DialogueBox directly, so this is the reliable interception point.
    /// </summary>
    [HarmonyPatch(typeof(DialogueBox), MethodType.Constructor, new[] { typeof(Dialogue) })]
    public class DialogueBox_Ctor_Patch
    {
        public static void Postfix(Dialogue dialogue)
        {
            if (dialogue?.speaker == null) return;
            if (dialogue.dialogues == null || dialogue.dialogues.Count == 0) return;

            var npc = dialogue.speaker;

            // 过滤占位符、生成标签、空白对话
            var firstText = dialogue.dialogues.FirstOrDefault()?.Text;
            if (string.IsNullOrWhiteSpace(firstText)) return;
            if (firstText == SldConstants.DialogueGenerationTag) return;
            if (firstText.StartsWith(SldConstants.DialogueSkipTag)) return;

            // AI 生成的对话由 AsyncBuilder 自行写入历史，此处跳过
            if (AsyncBuilder.ConsumeAiDialogueFlag(npc.Name)) return;

            var combinedText = string.Join(" ", dialogue.dialogues
                .Where(x => x != null && !string.IsNullOrWhiteSpace(x.Text))
                .Select(x => x.Text));

            if (string.IsNullOrWhiteSpace(combinedText)) return;

            var currentEvent = Game1.currentLocation?.currentEvent;
            if (currentEvent != null)
            {
                DialogueHistoryManager.Instance.RecordNpcDialogue(npc.Name, combinedText, "event");
            }
            else
            {
                DialogueHistoryManager.Instance.RecordNpcDialogue(npc.Name, combinedText, "vanilla");
                RecentConversationTracker.RecordResponse(npc.Name, combinedText, null);
            }
        }
    }
}
