using System;
using System.Reflection;
using StardewModdingAPI;
using StardewValley;
using StardewValley.GameData;
using StardewValley.Quests;

namespace ValleytalkReborn
{
    /// <summary>礼物交接结算器。副作用唯一归属地。</summary>
    public static class HandoverSettlement
    {
        private static void SendGlobalChatInfoMessage(string messageKey, params string[] args)
        {
            try
            {
                var multiplayerField = typeof(Game1).GetField("multiplayer", BindingFlags.NonPublic | BindingFlags.Static);
                var multiplayer = multiplayerField?.GetValue(null);
                if (multiplayer == null) return;
                var method = multiplayer.GetType().GetMethod("globalChatInfoMessage", BindingFlags.Public | BindingFlags.Instance);
                method?.Invoke(multiplayer, new object[] { messageKey, args });
            }
            catch { }
        }

        public static bool TryApply(HandoverVerdict verdict, NPC npc, Farmer who,
                                    StardewValley.Object activeItem,
                                    HandoverDecision decision, out string failReason)
        {
            failReason = null;

            try
            {
                switch (verdict)
                {
                    // ═══ C1 Bouquet_Accept ═══
                    case HandoverVerdict.Bouquet_Accept:
                    {
                        var f = who.friendshipData[npc.Name];
                        f.Status = FriendshipStatus.Dating;                              // P0 §1.6 (O)458 accept
                        if (Game1.IsMultiplayer)
                            SendGlobalChatInfoMessage("Dating", Game1.player.Name, npc.GetTokenizedDisplayName());
                        who.autoGenerateActiveDialogueEvent("dating_" + npc.Name, 4);     // P0 §1.6 (O)458 accept
                        who.autoGenerateActiveDialogueEvent("dating", 4);                // P0 §1.6 (O)458 accept
                        who.changeFriendship(25, npc);                                  // P0 §1.6 (O)458 accept
                        who.reduceActiveItemByOne();                                    // ★ 不可逆点
                        try { who.completelyStopAnimatingOrDoingAction(); } catch { }   // P0 §1.6 (O)458 accept
                        try { npc.doEmote(20, true); } catch { }                         // P0 §1.6 (O)458 accept
                        break;
                    }

                    // ═══ C2 WiltedBouquet_Accept ═══
                    case HandoverVerdict.WiltedBouquet_Accept:
                    {
                        var f = who.friendshipData[npc.Name];
                        Game1.showGlobalMessage(Game1.content.LoadString(
                            "Strings\\StringsFromCSFiles:Wilted_Bouquet_Effect", npc.displayName));  // P0 §1.6 (O)277 accept
                        if (Game1.IsMultiplayer)
                            SendGlobalChatInfoMessage("BreakUp", Game1.player.Name, npc.GetTokenizedDisplayName());
                        who.removeDatingActiveDialogueEvents(npc.Name);                 // P0 §1.6 (O)277 accept
                        f.Status = FriendshipStatus.Friendly;                           // ★ 原版是 Friendly
                        if (who.spouse == npc.Name) who.spouse = null;                  // 覆盖订婚期分手
                        f.WeddingDate = null;                                           // P0 §1.6 (O)277 accept
                        f.Points = Math.Min(f.Points, 1250);                            // P0 §1.6 (O)277 accept
                        who.reduceActiveItemByOne();                                    // ★ 不可逆点（状态变更之后）
                        try { who.completelyStopAnimatingOrDoingAction(); } catch { }   // P0 §1.6 (O)277 accept
                        try { npc.doEmote((npc.Name == "Maru" || npc.Name == "Haley") ? 12 : 28, true); } catch { }  // P0 §1.6 (O)277 accept
                        break;
                    }

                    // ═══ C3 Pendant_Accept ═══
                    case HandoverVerdict.Pendant_Accept:
                    {
                        var f = who.friendshipData[npc.Name];
                        Game1.changeMusicTrack("silence", false, MusicContext.Default);   // P0 §1.7 engagementResponse
                        who.spouse = npc.Name;                                          // P0 §1.7 engagementResponse
                        if (Game1.IsMultiplayer)
                            SendGlobalChatInfoMessage("Engaged", Game1.player.Name, npc.GetTokenizedDisplayName());
                        f.Status = FriendshipStatus.Engaged;                            // P0 §1.7 engagementResponse
                        f.RoommateMarriage = false;                                     // P0 §1.7 engagementResponse
                        WorldDate weddingDate = new WorldDate(Game1.Date);               // P0 §1.7 engagementResponse
                        weddingDate.TotalDays += 3;                                     // P0 §1.7 engagementResponse
                        who.removeDatingActiveDialogueEvents(who.spouse);               // P0 §1.7 engagementResponse
                        while (!Game1.canHaveWeddingOnDay(weddingDate.DayOfMonth, weddingDate.Season))  // P0 §1.7 engagementResponse
                            weddingDate.TotalDays++;
                        f.WeddingDate = weddingDate;                                    // P0 §1.7 engagementResponse
                        who.changeFriendship(1, npc);                                   // P0 §1.7 engagementResponse
                        who.reduceActiveItemByOne();                                    // ★ 不可逆点
                        try { who.completelyStopAnimatingOrDoingAction(); } catch { }   // P0 §1.7 engagementResponse
                        ModEntry.SMonitor?.Log($"[HandoverSettlement] WeddingDate={weddingDate.TotalDays}", LogLevel.Info);
                        break;
                    }

                    // ═══ C4 Pendant_Reject_HeartsLow（仅 under_10_hearts 惩罚） ═══
                    case HandoverVerdict.Pendant_Reject_HeartsLow:
                    {
                        if (decision?.DetailReason != "under_10_hearts")
                        {
                            failReason = "hearts_low_not_under_10";
                            return false;
                        }
                        var f = who.friendshipData[npc.Name];
                        if (!f.ProposalRejected) { who.changeFriendship(-20, npc); f.ProposalRejected = true; }  // P0 §1.7 reject
                        else { who.changeFriendship(-50, npc); }                        // P0 §1.7 reject
                        // 不扣物、不改 Status
                        break;
                    }

                    // ═══ C5 Quest_Accept ═══
                    case HandoverVerdict.Quest_Accept:
                    {
                        var q = decision?.MatchedQuest;
                        if (q == null)
                        {
                            failReason = "matched_quest_null";
                            ModEntry.SMonitor?.Log("[HandoverSettlement] Quest_Accept but MatchedQuest is null", LogLevel.Error);
                            return false;
                        }
                        Game1.player.Items.Reduce(activeItem, q.number.Value, false);   // ★ 不可逆点
                        Game1.player.changeFriendship(q.dailyQuest.Value ? 150 : 255, npc);  // P0 §5.3 ItemDeliveryQuest
                        q.questComplete();                                              // P0 §5.2 Quest.questComplete
                        try { who.completelyStopAnimatingOrDoingAction(); } catch { }   // P0 §5.3 ItemDeliveryQuest
                        try { if (Game1.random.NextDouble() < 0.3 && npc.Name != "Wizard")
                                  npc.doEmote(32, true); } catch { }                     // P0 §5.3 ItemDeliveryQuest
                        break;
                    }

                    // ═══ Reject 档（除 C4）：零状态写入 ═══
                    case HandoverVerdict.Passthrough_Vanilla:
                    case HandoverVerdict.Block_NotGiftable:
                    case HandoverVerdict.Bouquet_Reject_HeartsLow:
                    case HandoverVerdict.Bouquet_Reject_NotDatable:
                    case HandoverVerdict.Bouquet_Reject_AlreadyDating:
                    case HandoverVerdict.Pendant_Reject_NotDating:
                    case HandoverVerdict.Pendant_Reject_NotDatable:
                    case HandoverVerdict.Pendant_Reject_NoDoubleBed:
                    case HandoverVerdict.Pendant_Reject_AlreadyMarried:
                    case HandoverVerdict.WiltedBouquet_Reject_NotDating:
                    case HandoverVerdict.NormalGift_Accept:
                        // 零状态写入，直接成功
                        break;

                    default:
                        failReason = "unsupported_verdict";
                        return false;
                }

                ModEntry.SMonitor?.Log($"[HandoverSettlement] applied {verdict} for {npc.Name}", LogLevel.Info);
                return true;
            }
            catch (Exception ex)
            {
                failReason = ex.Message;
                ModEntry.SMonitor?.Log($"[HandoverSettlement] failed {verdict} for {npc.Name}: {ex.Message}", LogLevel.Warn);
                return false;
            }
        }
    }
}
