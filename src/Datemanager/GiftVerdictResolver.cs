using System;
using System.Linq;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Quests;

namespace ValleytalkReborn
{
    /// <summary>礼物交接判定结果。</summary>
    public enum HandoverVerdict
    {
        Passthrough_Vanilla,
        Block_NotGiftable,
        Quest_Accept,
        Bouquet_Accept, Bouquet_Reject_HeartsLow, Bouquet_Reject_NotDatable, Bouquet_Reject_AlreadyDating,
        Pendant_Accept, Pendant_Reject_HeartsLow, Pendant_Reject_NotDating,
        Pendant_Reject_NotDatable, Pendant_Reject_NoDoubleBed, Pendant_Reject_AlreadyMarried,
        WiltedBouquet_Accept, WiltedBouquet_Reject_NotDating,
        NormalGift_Accept
    }

    /// <summary>礼物交接判定决策（纯数据，无副作用）。</summary>
    public sealed class HandoverDecision
    {
        public HandoverVerdict Verdict { get; }

        /// <summary>纯日志用，禁止进 Prompt。</summary>
        public string DetailReason { get; }

        /// <summary>仅 Quest_Accept 非空。</summary>
        public ItemDeliveryQuest MatchedQuest { get; }

        public HandoverDecision(HandoverVerdict verdict, string detailReason, ItemDeliveryQuest matchedQuest = null)
        {
            Verdict = verdict;
            DetailReason = detailReason;
            MatchedQuest = matchedQuest;
        }
    }

    /// <summary>礼物交接纯判定器。无状态写入、无副作用、无随机数。</summary>
    public static class GiftVerdictResolver
    {
        // 阈值 = 原版原值（P0 反编译），禁止改动数值
        internal const int BouquetVeryLowPoints = 1000;
        internal const int BouquetMinPoints     = 2000;
        internal const int PendantFloorPoints   = 1500;
        internal const int PendantMinPoints     = 2500;
        internal const int BreakupPointsCap     = 1250;

        public static HandoverDecision Resolve(NPC npc, Farmer who, StardewValley.Object activeItem)
        {
            // A1
            if (npc == null || who == null || activeItem == null)
                return new HandoverDecision(HandoverVerdict.Passthrough_Vanilla, "null_arg");

            // A2
            if (!Context.IsWorldReady || !who.IsMainPlayer)
                return new HandoverDecision(HandoverVerdict.Passthrough_Vanilla, "not_main_player");

            // A3
            if (npc.SimpleNonVillagerNPC || !npc.IsVillager)
                return new HandoverDecision(HandoverVerdict.Passthrough_Vanilla, "not_villager");

            // A4 — 拦截白名单 = { questItem 交付, "(O)458", "(O)460", "(O)277" }
            string itemId = activeItem.QualifiedItemId;
            if (itemId != "(O)458" && itemId != "(O)460" && itemId != "(O)277" && activeItem.questItem.Value != true)
                return new HandoverDecision(HandoverVerdict.Passthrough_Vanilla, "not_intercepted");

            // A5 — questItem 交付
            if (activeItem.questItem.Value == true)
            {
                var q = who.questLog?.FirstOrDefault(q =>
                    q is ItemDeliveryQuest idq
                    && !idq.completed.Value
                    && idq.OnItemOfferedToNpc(npc, activeItem, probe: true));
                if (q is ItemDeliveryQuest matched)
                    return new HandoverDecision(HandoverVerdict.Quest_Accept, "quest_match", matched);
                return new HandoverDecision(HandoverVerdict.Block_NotGiftable, "quest_no_match");
            }

            // A6 — "(O)458" 花束
            if (itemId == "(O)458")
            {
                if (!npc.CanReceiveGifts())
                    return new HandoverDecision(HandoverVerdict.Passthrough_Vanilla, "npc_cannot_receive_gifts");
                if (npc.TryGetDialogue("RejectItem_(O)458") != null)
                    return new HandoverDecision(HandoverVerdict.Passthrough_Vanilla, "vanilla_reject_dialogue");
                if (!npc.datable.Value || (!string.Equals(who.spouse, npc.Name, StringComparison.OrdinalIgnoreCase) && npc.isMarriedOrEngaged()))
                    return new HandoverDecision(HandoverVerdict.Bouquet_Reject_NotDatable, "not_datable");
                if (who.friendshipData == null || !who.friendshipData.TryGetValue(npc.Name, out var f6))
                    return new HandoverDecision(HandoverVerdict.Bouquet_Reject_HeartsLow, "no_friendship");
                if (f6.IsDating())
                    return new HandoverDecision(HandoverVerdict.Bouquet_Reject_AlreadyDating, "already_dating");
                if (f6.IsDivorced())
                    return new HandoverDecision(HandoverVerdict.Bouquet_Reject_NotDatable, "divorced");
                if (f6.Points < BouquetVeryLowPoints)
                    return new HandoverDecision(HandoverVerdict.Bouquet_Reject_HeartsLow, "very_low_hearts");
                if (f6.Points < BouquetMinPoints)
                    return new HandoverDecision(HandoverVerdict.Bouquet_Reject_HeartsLow, "low_hearts");
                return new HandoverDecision(HandoverVerdict.Bouquet_Accept, "accepted");
            }

            // A7 — "(O)460" 吊坠
            if (itemId == "(O)460")
            {
                if (!npc.CanReceiveGifts())
                    return new HandoverDecision(HandoverVerdict.Passthrough_Vanilla, "npc_cannot_receive_gifts");
                if (npc.TryGetDialogue("RejectItem_(O)460") != null)
                    return new HandoverDecision(HandoverVerdict.Passthrough_Vanilla, "vanilla_reject_dialogue");
                bool polyActive = SpouseQueryService.Instance.IsPolyamoryEnvironmentActive;
                if (string.Equals(who.spouse, npc.Name, StringComparison.OrdinalIgnoreCase))
                    return new HandoverDecision(HandoverVerdict.Pendant_Reject_AlreadyMarried, "already_accepted");
                if (who.isEngaged())
                    return new HandoverDecision(HandoverVerdict.Pendant_Reject_AlreadyMarried, "player_engaged");
                if (!polyActive && who.isMarriedOrRoommates())
                    return new HandoverDecision(HandoverVerdict.Pendant_Reject_AlreadyMarried, "player_married");
                if (!npc.datable.Value)
                    return new HandoverDecision(HandoverVerdict.Pendant_Reject_NotDatable, "not_datable");
                if (SpouseQueryService.Instance.IsMarried(npc.Name, who))
                    return new HandoverDecision(HandoverVerdict.Pendant_Reject_AlreadyMarried, "npc_is_your_spouse");
                if (!polyActive && npc.isMarriedOrEngaged())
                    return new HandoverDecision(HandoverVerdict.Pendant_Reject_AlreadyMarried, "npc_bound");
                if (who.friendshipData == null || !who.friendshipData.TryGetValue(npc.Name, out var f7))
                    return new HandoverDecision(HandoverVerdict.Pendant_Reject_NotDating, "no_friendship");
                if (f7.IsDivorced())
                    return new HandoverDecision(HandoverVerdict.Pendant_Reject_NotDatable, "divorced");
                if (f7.Points < PendantFloorPoints)
                    return new HandoverDecision(HandoverVerdict.Pendant_Reject_NotDating, "under_6_hearts");
                // 模组策略：原版无此闸门
                if (!f7.IsDating())
                    return new HandoverDecision(HandoverVerdict.Pendant_Reject_NotDating, "not_dating");
                if (f7.Points < PendantMinPoints)
                    return new HandoverDecision(HandoverVerdict.Pendant_Reject_HeartsLow, "under_10_hearts");
                if (who.HouseUpgradeLevel < 1)
                    return new HandoverDecision(HandoverVerdict.Pendant_Reject_NoDoubleBed, "no_double_bed");
                return new HandoverDecision(HandoverVerdict.Pendant_Accept, "accepted");
            }

            // A8 — "(O)277" 枯萎花束
            if (itemId == "(O)277")
            {
                if (!npc.CanReceiveGifts())
                    return new HandoverDecision(HandoverVerdict.Passthrough_Vanilla, "npc_cannot_receive_gifts");
                // ★ 预声明，绕开 ?. + out var 的确定赋值错误
                Friendship f = null;
                who.friendshipData?.TryGetValue(npc.Name, out f);
                if (f != null && f.IsMarried())
                    return new HandoverDecision(HandoverVerdict.Passthrough_Vanilla, "vanilla_meaningless_married");
                if (!npc.datable.Value)
                    return new HandoverDecision(HandoverVerdict.Passthrough_Vanilla, "vanilla_meaningless_not_datable");
                // f == null 必须落 Reject_NotDating（AI 演绎、物品保留），不得落 Passthrough
                if (f == null || !f.IsDating())
                    return new HandoverDecision(HandoverVerdict.WiltedBouquet_Reject_NotDating, "not_dating");
                return new HandoverDecision(HandoverVerdict.WiltedBouquet_Accept, "accepted");
            }

            // A9
            if (!activeItem.canBeGivenAsGift() || ItemContextTagManager.HasBaseTag(activeItem.QualifiedItemId, "not_giftable"))
                return new HandoverDecision(HandoverVerdict.Block_NotGiftable, "not_giftable");

            // A10
            return new HandoverDecision(HandoverVerdict.NormalGift_Accept, "accepted");
        }
    }
}
