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
            if (response.responseKey == null || !response.responseKey.StartsWith(SldConstants.DialogueKeyPrefix))
            {
                return true;
            }

            // 🌟 意图许可标签（Consent Tag）触发的确定性操作分发
            // 若响应 Key 对应约会地点选择或确认跟随，交由 DialogueBuilder 处理并拦截默认流程
            if (DialogueBuilder.HandleSpecialActionResponse(response.responseKey, __instance.speaker))
            {
                __result = true;
                return false;
            }

            // 🌟【精准修复】：记录为明确的行为动作，避免被 DialogueBuilder 当作异常符号过滤，
            // 同时赋予大模型真实的“冷场/沉默”剧情感知
            if (response.responseKey == $"{SldConstants.DialogueKeyPrefix}Silent")
            {
                bool isZh = LocalizedContentManager.CurrentLanguageCode.ToString().StartsWith("zh", StringComparison.OrdinalIgnoreCase);
                string silentText = isZh ? "*保持沉默，什么也没说*" : "remains silent";
                DialogueHistoryManager.Instance.RecordPlayerDialogue(__instance.speaker.Name, silentText);

                // 🌟【精准修复 1】：显式清理对话菜单并恢复玩家行动，防止悬挂导致用户再次点击空栈崩溃
                Game1.dialogueUp = false;
                Game1.activeClickableMenu = null;
                Game1.player?.forceCanMove();
                __result = true;
                return false;
            }

            // 【优化】动态获取翻译，仅用于诊断日志归档
            string respondString = Util.GetString("outputRespond");

            // 🌟【结构性移除】：进入本补丁的皆为 SLD_* 响应；$q 问题页的唯一发射点是
            // DialogueBuilder.FormatLine，且 $q 与 $r 总是成对出现在字符串末尾、$r 不产生解析页，
            // 因此 $q 问题页必然存在且必为 __instance.dialogues 的最后一页——无需文本比对即可安全移除。
            var dialogueStrings = __instance.dialogues;
            if (dialogueStrings != null && dialogueStrings.Count > 0)
            {
                var lastLine = dialogueStrings.LastOrDefault();
                if (lastLine != null && lastLine.Text != respondString)
                {
                    ModEntry.SMonitor?.Log($"[ChooseResponse] Question page text mismatch (expected '{respondString}', got '{lastLine.Text}'). Removing tail structurally.", StardewModdingAPI.LogLevel.Trace);
                }

                // 🌟【精准修复 2】：Count == 1 时改设为占位符，绝不删空列表触发 DialogueBox.Pop() 空栈崩溃
                if (dialogueStrings.Count == 1)
                {
                    dialogueStrings[0].Text = "...";
                }
                else
                {
                    dialogueStrings.RemoveAt(dialogueStrings.Count - 1);
                }
            }

            // 【Bug 修复】防护 LastContext 为 null 的致命空指针隐患
            var context = DialogueBuilder.Instance.GetContext(__instance.speaker.Name);

            // 🌟【精准修复】：拷贝 context.ChatHistory，避免对话框交互对共享会话历史原地突变
            // （后续 AddRange / Contains 过滤全部作用在副本上，切断写入方 #1）
            List<ConversationElement> previous =
                context?.ChatHistory != null
                    ? new List<ConversationElement>(context.ChatHistory)
                    : new List<ConversationElement>();

            if (dialogueStrings != null)
            {
                var newLines = dialogueStrings
                    .Where(x => x != null 
                                && x.Text != "skip" 
                                && !previous.Any(y => y.Text != null 
                                                      && y.Text.Length >= x.Text.Length 
                                                      && y.Text.Contains(x.Text)))  // ← 注意这里：三个右括号 )))
                    .Select(x => new ConversationElement(x.Text, false));
    
                previous.AddRange(newLines);
            }

            string farmerResponse = response.responseText ?? string.Empty;

            // 【修复】确保记录前清理玩家选项中的 @ 符号
            if (farmerResponse.Contains('@') && Game1.player != null)
            {
                farmerResponse = farmerResponse.Replace("@", Game1.player.Name);
            }

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

            // 用户主动选择选项，清零冷却防止请求被静默丢弃
            AsyncBuilder.Instance.ClearCooldown();

            // TIE-005：观察性钩子——玩家选项文本交由镇事件引擎按关键词组记录运行时标志。
            // 引擎内部自行处理参与者归属、空文本与多人排除；无论 AsyncBuilder 是否接受
            // 后续请求，玩家的选择本身已经发生，故先于请求调用。
            if (!string.IsNullOrWhiteSpace(farmerResponse))
                TownIncidentEngine.RecordChoice(__instance.speaker.Name, farmerResponse);

            var updatedHistory = new List<ConversationElement>(previous)
            {
                new ConversationElement(farmerResponse, true)
            };

            bool accepted = AsyncBuilder.Instance.RequestNpcResponse(
                __instance.speaker, 
                updatedHistory.ToArray()
            );

            if (accepted)
            {
                DialogueHistoryManager.Instance.RecordPlayerDialogue(__instance.speaker.Name, farmerResponse);
            }
            else
            {
                ModEntry.SMonitor?.Log($"[ChooseResponse] Request rejected by AsyncBuilder for {__instance.speaker.Name}.", StardewModdingAPI.LogLevel.Warn);
            }

            __result = true;
            return false;
        }
    }
}