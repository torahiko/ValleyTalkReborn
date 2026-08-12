using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using StardewValley;

namespace ValleytalkReborn
{
    [HarmonyPatch(typeof(Dialogue), nameof(Dialogue.chooseResponse))]
    public class Dialogue_ChooseResponse_Patch
    {
        // 【优化】使用 Harmony 高性能 FieldRef 替代传统 FieldInfo.SetValue，提升反射运行速度
        private static readonly AccessTools.FieldRef<Dialogue, bool> FinishedLastDialogueRef =
            AccessTools.FieldRefAccess<Dialogue, bool>("finishedLastDialogue");

        public static bool Prefix(ref Dialogue __instance, ref bool __result, Response response)
        {
            if (__instance?.speaker == null || response == null)
            {
                return true;
            }

            ModEntry.SMonitor.Log($"Dialogue.chooseResponse called with response key: {response.responseKey}", StardewModdingAPI.LogLevel.Trace);

            if (!DialogueBuilder.Instance.PatchNpc(__instance.speaker))
            {
                return true;
            }

            var responseOptions = __instance.getResponseOptions();
            if (responseOptions == null || responseOptions.Any(r => r?.responseKey == null || !r.responseKey.StartsWith(SldConstants.DialogueKeyPrefix)))
            {
                return true;
            }

            // 处理静默选项
            if (response.responseKey == $"{SldConstants.DialogueKeyPrefix}Silent")
            {
                DialogueHistoryManager.Instance.RecordPlayerDialogue(__instance.speaker.Name, "...");
                __result = true;
                return false;
            }

            // 【优化】动态获取翻译，避免多语言切换或未初始化时失效
            string respondString = Util.GetString("outputRespond");
            
            var dialogueStrings = __instance.dialogues;
            if (dialogueStrings != null && dialogueStrings.Count > 0)
            {
                // 【Bug 修复】使用 LastOrDefault 防止列表为空时抛出 InvalidOperationException
                var lastLine = dialogueStrings.LastOrDefault();
                if (lastLine != null && lastLine.Text == respondString)
                {
                    dialogueStrings.RemoveAt(dialogueStrings.Count - 1);
                }
            }

            // 【Bug 修复】防护 LastContext 为 null 的致命空指针隐患
            var context = DialogueBuilder.Instance.GetContext(__instance.speaker.Name);
            var previous = context?.ChatHistory ?? new List<ConversationElement>();

            if (dialogueStrings != null)
            {
                var newLines = dialogueStrings
                    .Where(x => x != null && x.Text != "skip" && !previous.Any(y => y.Text != null && y.Text.Contains(x.Text)))
                    .Select(x => new ConversationElement(x.Text, false));
                
                previous.AddRange(newLines);
            }

            string farmerResponse = response.responseText ?? string.Empty;

// 【修复】确保记录前清理玩家选项中的 @ 符号
            if (farmerResponse.Contains('@') && Game1.player != null)
            {
                farmerResponse = farmerResponse.Replace("@", Game1.player.Name);
            }

            DialogueHistoryManager.Instance.RecordPlayerDialogue(__instance.speaker.Name, farmerResponse);

            if (response.responseKey == $"{SldConstants.DialogueKeyPrefix}TypedResponse")
            {
                TextInputManager.RequestTextInput(
                    Util.GetString("uiYourResponse"), 
                    __instance.speaker, 
                    __instance.speaker.LoadedDialogueKey ?? "default",
                    previous);
                
                __result = true;
                return false;
            }

            // 【优化】使用高性能委托赋值
            FinishedLastDialogueRef(__instance) = false;

            var updatedHistory = new List<ConversationElement>(previous)
            {
                new ConversationElement(farmerResponse, true)
            };

            AsyncBuilder.Instance.RequestNpcResponse(
                __instance.speaker, 
                updatedHistory.ToArray()
            );

            __result = true;
            return false;
        }
    }
}