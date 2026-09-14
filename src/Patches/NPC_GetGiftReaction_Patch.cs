using HarmonyLib;
using StardewValley;

namespace ValleytalkReborn
{
    [HarmonyPatch(typeof(NPC), nameof(NPC.GetGiftReaction))]
    public class NPC_GetGiftReaction_Patch
    {
        public static bool Prefix(
            ref NPC __instance,
            ref Dialogue __result,
            Farmer giver,
            StardewValley.Object gift,
            int taste)
        {
            if (__instance == null || gift == null)
                return true;

            if (gift.QualifiedItemId is "(O)458" or "(O)460" or "(O)277" || gift.questItem.Value)
            {
                ModEntry.SMonitor.Log(
                    $"[GetGiftReaction] ritual/quest item reached legacy path, handing back to vanilla: {__instance.Name} {gift.QualifiedItemId}",
                    StardewModdingAPI.LogLevel.Trace);
                return true;
            }

            var giftName = gift.DisplayName ?? gift.Name ?? "Gift";
            ModEntry.SMonitor.Log(
                $"NPC {__instance.Name} trying to get gift reaction for {giftName}",
                StardewModdingAPI.LogLevel.Trace);

            if (!DialogueBuilder.Instance.PatchNpc(__instance, ModEntry.Config.GiftFrequency))
                return true;

            // 仅保留判断更精准的那个完整判断即可
            if (AsyncBuilder.Instance.AwaitingGeneration
                && AsyncBuilder.Instance.SpeakingNpc == __instance
                && AsyncBuilder.Instance.AwaitedType == GenerationType.Gift)
                return true;

            if (!NetworkAvailabilityChecker.IsNetworkAvailableWithRetry())
            {
                ModEntry.SMonitor.Log(
                    $"Network not available, skipping AI gift reaction for {__instance.Name}",
                    StardewModdingAPI.LogLevel.Trace);
                return true;
            }

            // 记录礼物历史
            DialogueHistoryManager.Instance.RecordGiftGiven(__instance.Name, giftName, taste);

            // 记录感知条目（PerceptionInjector 会在 SystemPrompt 尾部读到它）
            GiftSubscriber.RecordGiftPerceptionFromPatch(
                __instance.Name,
                gift.DisplayName ?? gift.Name,
                gift.ItemId);

            // ★ 核心新增：向 PendingTopicManager 注入礼物强语境
            InjectGiftPendingTopic(__instance, gift, taste);

            // ★ 清除配偶的 marriageDialogue，防止它与 Gift DialogueBox 竞争
            // 当天第一次和配偶交互时，原版 checkAction 会预填 marriageDialogue，
            // 导致 AsyncBuilder 等来的 DialogueBox 不是它的占位符而被丢弃。
            if (__instance.currentMarriageDialogue?.Count > 0)
            {
                __instance.currentMarriageDialogue.Clear();
                __instance.shouldSayMarriageDialogue.Value = false;
            }

            AsyncBuilder.Instance.RequestNpcGiftResponse(__instance, gift, taste);

            var result = new Dialogue(__instance, null, SldConstants.DialogueSkipTag);
            result.exitCurrentDialogue();
            __result = result;

            return false;
        }

        private static void InjectGiftPendingTopic(NPC npc, StardewValley.Object gift, int taste)
        {
            bool isZh = LocalizedContentManager.CurrentLanguageCode
                .ToString().StartsWith("zh", System.StringComparison.OrdinalIgnoreCase);

            string itemName = gift.DisplayName ?? gift.Name ?? "???";

            string tasteDesc = taste switch
            {
                NPC.gift_taste_love    => isZh ? "你非常喜爱这件礼物" : "you love this gift",
                NPC.gift_taste_like    => isZh ? "你喜欢这件礼物"   : "you like this gift",
                NPC.gift_taste_dislike => isZh ? "你不太喜欢这件礼物" : "you dislike this gift",
                NPC.gift_taste_hate    => isZh ? "你很讨厌这件礼物"  : "you hate this gift",
                _                      => isZh ? "你对这件礼物感觉一般" : "you feel neutral about this gift",
            };

            bool isBirthday = Game1.dayOfMonth == npc.GetData()?.BirthDay
                && Game1.season.ToString().Equals(
                    npc.GetData()?.BirthSeason.ToString(),
                    System.StringComparison.OrdinalIgnoreCase);

            string birthdayNote = isBirthday
                ? (isZh ? "而且今天正好是你的生日！" : " and today is your birthday!")
                : string.Empty;

            string topic = isZh
                ? $"农夫（@）刚刚送给了你一件礼物：[{itemName}]。事实：{tasteDesc}{birthdayNote}。"
                : $"The farmer (@) just handed you a gift: [{itemName}]. Fact: {tasteDesc}{birthdayNote}.";

            PendingTopicManager.Instance.SetPendingTopic(npc.Name, topic);
        }
    }
}
