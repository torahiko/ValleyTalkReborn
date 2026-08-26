using System;
using StardewModdingAPI;
using StardewValley;

namespace ValleytalkReborn
{
    /// <summary>
    /// Prefix/Postfix 的决策结果。
    /// </summary>
    public class DateGiftDecision
    {
        /// <summary>true = 拦截原版 receiveGift，不执行。</summary>
        public bool BlockOriginal { get; set; }

        /// <summary>需要保存的原始计数器（用于 Postfix 恢复）。null = 不需要保存。</summary>
        public Tuple<int, int> OriginalCounters { get; set; }
    }

    /// <summary>
    /// 约会送礼的业务规则。
    /// NpcReceiveGiftPatch 只负责拦截原版调用，具体逻辑全部在这里。
    /// </summary>
    public static class DateGiftService
    {
        /// <summary>
        /// Prefix 逻辑：判断是否需要清零计数器或拦截送礼。
        /// </summary>
        public static DateGiftDecision BeforeReceiveGift(NPC npc, Farmer who)
        {
            var decision = new DateGiftDecision();

            if (who?.friendshipData == null) return decision;
            if (!who.friendshipData.TryGetValue(npc.Name, out var friendship)) return decision;

            bool onDate       = DateManager.Instance.IsOnDate(npc.Name);
            bool usedDateGift = DateManager.Instance.HasGivenDateGiftThisSession;

            // 1. 约会中且尚未用过约会专属礼物：清零计数器，走原版送礼流程
            if (onDate && !usedDateGift)
            {
                decision.OriginalCounters = new Tuple<int, int>(
                    friendship.GiftsToday, friendship.GiftsThisWeek);
                friendship.GiftsToday    = 0;
                friendship.GiftsThisWeek = 0;
                return decision;
            }

            // 2. 约会中但约会礼物已用过：按原版规则拦截
            if (onDate && usedDateGift)
            {
                bool isSpouse = friendship.IsMarried() ||
                    (who.getSpouse() != null && string.Equals(
                        who.getSpouse().Name, npc.Name, StringComparison.OrdinalIgnoreCase));

                if (friendship.GiftsToday >= 1 || (friendship.GiftsThisWeek >= 2 && !isSpouse))
                {
                    decision.BlockOriginal = true;
                }
            }

            return decision;
        }

        /// <summary>
        /// Postfix 逻辑：恢复计数器 + 记录约会礼物。
        /// </summary>
        public static void AfterReceiveGift(NPC npc, Farmer who, Item giftItem, Tuple<int, int> originalCounters)
        {
            if (originalCounters == null) return;

            // 恢复计数器
            if (who.friendshipData.TryGetValue(npc.Name, out var friendship))
            {
                friendship.GiftsToday    = originalCounters.Item1;
                friendship.GiftsThisWeek = originalCounters.Item2;
            }

            // 记录约会礼物
            if (DateManager.Instance.IsOnDate(npc.Name) &&
                !DateManager.Instance.HasGivenDateGiftThisSession)
            {
                DateManager.Instance.HasGivenDateGiftThisSession = true;
                int taste = giftItem != null ? npc.getGiftTasteForThisItem(giftItem) : 0;
                DateManager.Instance.RecordDateGift(giftItem?.DisplayName ?? "礼物", taste);

                ModEntry.SMonitor?.Log(
                    $"[DateGiftService] 约会专属送礼完成: {giftItem?.DisplayName}" +
                    $"（日常送礼计数已恢复: today={originalCounters.Item1}, week={originalCounters.Item2}）",
                    LogLevel.Info);
            }
            else if (!DateManager.Instance.IsOnDate(npc.Name))
            {
                ModEntry.SMonitor?.Log(
                    "[DateGiftService] 送礼 Postfix 执行时约会已结束，计数器已安全恢复。",
                    LogLevel.Debug);
            }
        }
    }
}
