using System.Linq;
using HarmonyLib;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;

namespace ValleytalkReborn
{
    /// <summary>
    /// 事件剧情对话捕获补丁（FEAT-HIST-EVENT-01R2）。
    /// 两个纯观察者补丁：绝不跳原方法、不修改 __result、不在失败路径抛出异常。
    /// </summary>
    public static class Event_AnswerDialogue_Patch
    {
        // ★ Memory 作用域状态：进程内会话级，不持久化；ModEntry 在 SaveLoaded / DayStarted 清空。
        public static string LastEventSpeakerNpc { get; set; }

        [HarmonyPatch(typeof(Event), nameof(Event.answerDialogue),
                         new[] { typeof(string), typeof(int) })]
        public static void Prefix(Event __instance, string questionKey, int answerChoice)
        {
            try
            {
                if (!ModEntry.Config.RecordEventDialogue) return;
                if (!Game1.eventUp || __instance == null) return;

                var db = Game1.activeClickableMenu as DialogueBox;
                if (db == null)
                {
                    ModEntry.SMonitor?.Log(
                        "[Event.answerDialogue] activeClickableMenu is not DialogueBox — skipped.",
                        LogLevel.Trace);
                    return;
                }

                var resp = (answerChoice >= 0 && db.responses != null && answerChoice < db.responses.Length)
                    ? db.responses[answerChoice]
                    : null;

                if (resp == null || string.IsNullOrWhiteSpace(resp.responseText))
                {
                    ModEntry.SMonitor?.Log(
                        $"[Event.answerDialogue] answerChoice={answerChoice} out of range or empty response — skipped.",
                        LogLevel.Trace);
                    return;
                }

                string responseText = resp.responseText;
                if (responseText.Contains('@') && Game1.player != null)
                {
                    responseText = responseText.Replace("@", Game1.player.Name);
                }

                // ★ 归属目标 NPC：严格按序，首个命中即用。
                string targetNpcName = null;

                if (!string.IsNullOrEmpty(LastEventSpeakerNpc)
                    && __instance.getActorByName(LastEventSpeakerNpc) != null)
                {
                    targetNpcName = LastEventSpeakerNpc;
                }
                else if (__instance.actors != null)
                {
                    var villager = __instance.actors
                        .FirstOrDefault(a => a != null && a.IsVillager);
                    if (villager != null)
                    {
                        targetNpcName = villager.Name;
                    }
                }

                if (string.IsNullOrEmpty(targetNpcName))
                {
                    var fallback = ModEntry.LastSpokenNPC?.Name;
                    if (!string.IsNullOrEmpty(fallback))
                    {
                        targetNpcName = fallback;
                    }
                    else
                    {
                        ModEntry.SMonitor?.Log(
                            "[Event.answerDialogue] no villager actor and no NPC fallback — skipped event record.",
                            LogLevel.Trace);
                        return;
                    }
                }

                DialogueHistoryManager.Instance.RecordPlayerDialogue(targetNpcName, responseText, "event");
            }
            catch (System.Exception ex)
            {
                ModEntry.SMonitor?.Log($"[Event.answerDialogue] Patch error: {ex.Message}", LogLevel.Trace);
            }
        }
    }

    /// <summary>
    /// 事件/剧情中以 NPC 名义记录的 chooseResponse 路径（$y 带头像提问事件等）。
    /// 纯观察 Postfix：绝不 return false、不改 __result、不改原流程。
    /// </summary>
    [HarmonyPatch(typeof(Dialogue), nameof(Dialogue.chooseResponse))]
    public class Dialogue_ChooseResponse_EventCapture_Patch
    {
        public static void Postfix(Dialogue __instance, Response response)
        {
            try
            {
                if (!ModEntry.Config.RecordEventDialogue) return;
                if (!Game1.eventUp || __instance?.speaker == null || response == null) return;

                // ★ 防与 AI 链路重叠：SLD_* 响应走既有 ChooseResponse 流程。
                if (response.responseKey != null
                    && response.responseKey.StartsWith(SldConstants.DialogueKeyPrefix))
                {
                    return;
                }

                string text = response.responseText;
                if (string.IsNullOrWhiteSpace(text)) return;

                if (text.Contains('@') && Game1.player != null)
                {
                    text = text.Replace("@", Game1.player.Name);
                }

                // speaker 直接归属，非启发式。
                DialogueHistoryManager.Instance.RecordPlayerDialogue(__instance.speaker.Name, text, "event");
                Event_AnswerDialogue_Patch.LastEventSpeakerNpc = __instance.speaker.Name;
            }
            catch (System.Exception ex)
            {
                ModEntry.SMonitor?.Log($"[Dialogue.chooseResponse:EventCapture] Patch error: {ex.Message}", LogLevel.Trace);
            }
        }
    }
}
