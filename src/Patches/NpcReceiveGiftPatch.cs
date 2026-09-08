using System;
using HarmonyLib;
using StardewValley;

namespace ValleytalkReborn
{
    /// <summary>
    /// 薄适配层：只负责拦截原版 receiveGift 调用，业务逻辑全部在 DateGiftService 中。
    /// </summary>
    [HarmonyPatch(typeof(NPC), nameof(NPC.receiveGift))]
    public static class NpcReceiveGiftPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(
            NPC __instance,
            [HarmonyArgument("giver")] Farmer who,
            [HarmonyArgument("o")] Item giftItem,
            ref Tuple<int, int> __state)
        {
            // ★ 原版送礼优先：玩家送礼时，取消该 NPC 参与的所有 A2A 会话
            if (__instance != null)
                DynamicBarkManager.CancelA2AForNpc(__instance.Name, "Player gift");

            var decision = DateGiftService.BeforeReceiveGift(__instance, who);
            __state = decision.OriginalCounters;
            if (decision.BlockOriginal)
            {
                Game1.drawObjectDialogue(
                    Game1.content.LoadString(
                        "Strings\\StringsFromCSFiles:NPC.cs.3981",
                        __instance.displayName));
                return false;
            }

            return true;
        }

        [HarmonyPostfix]
        public static void Postfix(
            NPC __instance,
            [HarmonyArgument("giver")] Farmer who,
            [HarmonyArgument("o")] Item giftItem,
            Tuple<int, int> __state)
        {
            DateGiftService.AfterReceiveGift(__instance, who, giftItem, __state);
        }
    }
}