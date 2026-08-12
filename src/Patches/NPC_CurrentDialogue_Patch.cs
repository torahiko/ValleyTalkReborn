using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using HarmonyLib;
using StardewModdingAPI;
using StardewValley;

namespace ValleytalkReborn
{
    [HarmonyPatch(typeof(NPC), nameof(NPC.CurrentDialogue), MethodType.Getter)]
    public class NPC_CurrentDialogue_Patch
    {
        private static readonly Dictionary<string, string> _lastRecordedDialogue = new Dictionary<string, string>();
        private static readonly Dictionary<string, long> _lastRecordedTime = new Dictionary<string, long>();
        private const long DedupWindowMs = 100;

        internal static void ClearDedupState()
        {
            _lastRecordedDialogue.Clear();
            _lastRecordedTime.Clear();
        }

        public static void Postfix(ref NPC __instance, ref Stack<Dialogue> __result)
        {
            if (__instance == null || __result == null) return;
            if (__result.Count == 0) return;

            // 仅在 drawDialogue 调用链上才做历史记录
            if (!Game1_DrawDialogue_Patch.DrawingDialogue) return;

            var currentDialogue = __result.Peek();
            if (currentDialogue?.dialogues == null || currentDialogue.dialogues.Count == 0) return;

            var nextLine = currentDialogue.dialogues.FirstOrDefault();
            if (nextLine == null) return;

            // 生成占位符或空白行不记录
            if (nextLine.Text == SldConstants.DialogueGenerationTag) return;
            if (string.IsNullOrWhiteSpace(nextLine.Text)) return;
            if (nextLine.Text == SldConstants.DialogueSkipTag) return;

            var combinedText = string.Join(" ", currentDialogue.dialogues
                .Where(x => x != null)
                .Select(x => x.Text));

            // 去重：100ms 窗口内同一内容不重复记录
            var now = Stopwatch.GetTimestamp();
            long nowMs = now * 1000 / Stopwatch.Frequency;
            if (_lastRecordedDialogue.TryGetValue(__instance.Name, out var lastText) &&
                _lastRecordedTime.TryGetValue(__instance.Name, out var lastTime) &&
                lastText == combinedText &&
                (nowMs - lastTime) < DedupWindowMs)
            {
                return;
            }

            _lastRecordedDialogue[__instance.Name] = combinedText;
            _lastRecordedTime[__instance.Name] = nowMs;

            // 感知系统：窃听广播（无论 AI 还是原版对话都执行）
            // 注意：vanilla/AI 对话的历史记录由 DialogueBox_Ctor_Patch 统一处理，此处不再重复记录。
            var nearbyNpcs = Util.GetNearbyNpcs(__instance);
            if (nearbyNpcs != null)
            {
                var cleanedForEavesdrop = EavesdropTextCleaner.Clean(combinedText);
                var farmerLabel = Util.GetString("generalFarmerLabel") ?? "农夫";
                var lastPlayerEntry = DialogueHistoryManager.Instance.GetHistory(__instance.Name)
                    .LastOrDefault(e => e.SpeakerType == SpeakerType.Player && e.DialogueType != "eavesdrop");

                string farmerSaid = lastPlayerEntry != null && !string.IsNullOrWhiteSpace(lastPlayerEntry.Text)
                    ? $"{farmerLabel}对{__instance.displayName}说：\"{lastPlayerEntry.Text}\"，{__instance.displayName}回应：\"{cleanedForEavesdrop}\""
                    : $"{farmerLabel}对{__instance.displayName}说话，{__instance.displayName}回应：\"{cleanedForEavesdrop}\"";

                var eavesdropText = $"[Eavesdrop] {farmerSaid}";
                foreach (var npc in nearbyNpcs)
                {
                    if (npc != null)
                        DialogueHistoryManager.Instance.RecordSystemEvent(npc.Name, eavesdropText, "eavesdrop");
                }
            }
        }
    }
}