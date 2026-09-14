using StardewValley;

namespace ValleytalkReborn;

public static class HandoverPromptProtocols
{
    private static bool IsZh =>
        LocalizedContentManager.CurrentLanguageCode == LocalizedContentManager.LanguageCode.zh;

    public static string BuildTopic(HandoverVerdict verdict, NPC npc, StardewValley.Object activeItem)
    {
        bool isZh = IsZh;
        string itemName = activeItem?.DisplayName ?? activeItem?.Name ?? "???";

        return verdict switch
        {
            // ── 1. 任务交付 ──
            HandoverVerdict.Quest_Accept => isZh
                ? $"<quest_delivery_protocol>\n- 事实：农夫（@）找到了你遗失的【{itemName}】，此时正当面递还给你。\n- 背景：这是你之前遗失并挂念的私人物品。\n</quest_delivery_protocol>"
                : $"<quest_delivery_protocol>\n- Fact: The farmer (@) found your lost [{itemName}] and is returning it to you in person.\n- Context: This is your personal belonging that you have been looking for.\n</quest_delivery_protocol>",

            // ── 2. 花束（告白）──
            HandoverVerdict.Bouquet_Accept => isZh
                ? "<confession_protocol verdict=\"accept\">\n- 事实：农夫（@）正当面递给你一束花束，向你表明心意。\n- 你的心意：你也对农夫心动，决定接受花束，正式成为恋人。\n</confession_protocol>"
                : "<confession_protocol verdict=\"accept\">\n- Fact: The farmer (@) is offering you a bouquet to declare their feelings.\n- Your feelings: You have feelings for the farmer too and accept the bouquet, officially becoming a couple.\n</confession_protocol>",

            HandoverVerdict.Bouquet_Reject_HeartsLow => isZh
                ? "<confession_protocol verdict=\"decline_unready\">\n- 事实：农夫（@）正当面递给你一束花束，向你表明心意。\n- 你的心意：你目前只把农夫当成普通朋友，还没有恋爱方面的心思。本次无法接受这份心意。\n</confession_protocol>"
                : "<confession_protocol verdict=\"decline_unready\">\n- Fact: The farmer (@) is offering you a bouquet to declare their feelings.\n- Your feelings: You only see the farmer as a friend right now and are not ready for romance. You decline for now.\n</confession_protocol>",

            HandoverVerdict.Bouquet_Reject_NotDatable => isZh
                ? "<confession_protocol verdict=\"decline_not_datable\">\n- 事实：农夫（@）正递给你一束用于恋人告白的花束。\n- 事实：你与农夫之间并不存在发展恋爱关系的可能。\n</confession_protocol>"
                : "<confession_protocol verdict=\"decline_not_datable\">\n- Fact: The farmer (@) is offering you a bouquet meant for romance.\n- Fact: There is no possibility of a romantic relationship between you and the farmer.\n</confession_protocol>",

            HandoverVerdict.Bouquet_Reject_AlreadyDating => isZh
                ? "<confession_protocol verdict=\"decline_already_dating\">\n- 事实：农夫（@）又拿了一束花束递给你。\n- 事实：你们两人早已经是正式交往的恋人关系了。\n</confession_protocol>"
                : "<confession_protocol verdict=\"decline_already_dating\">\n- Fact: The farmer (@) is offering you another bouquet.\n- Fact: The two of you are already dating.\n</confession_protocol>",

            // ── 3. 美人鱼吊坠（求婚）──
            HandoverVerdict.Pendant_Accept => isZh
                ? "<proposal_protocol verdict=\"accept\">\n- 事实：农夫（@）正当面手捧美人鱼吊坠向你正式求婚。\n- 你的心意：你心仪农夫，愿意答应求婚，与他结为伴侣。\n- 肢体参考：台词末尾可自然附带表情标签，如 [ACTION:EMOTE:HEART] 或 [ACTION:EMOTE:BLUSH]。\n</proposal_protocol>"
                : "<proposal_protocol verdict=\"accept\">\n- Fact: The farmer (@) is proposing to you with a Mermaid's Pendant.\n- Your feelings: You love the farmer and agree to marry them.\n- Action cue: You may naturally end your dialogue with an emote tag like [ACTION:EMOTE:HEART] or [ACTION:EMOTE:BLUSH].\n</proposal_protocol>",

            HandoverVerdict.Pendant_Reject_HeartsLow or HandoverVerdict.Pendant_Reject_NotDating => isZh
                ? "<proposal_protocol verdict=\"decline_unready\">\n- 事实：农夫（@）正当面拿出了美人鱼吊坠向你求婚。\n- 你的心意：你们现在的交情还远远没到谈婚论嫁的程度。本次无法答应求婚。\n</proposal_protocol>"
                : "<proposal_protocol verdict=\"decline_unready\">\n- Fact: The farmer (@) is proposing to you with a Mermaid's Pendant.\n- Your feelings: You are not close enough for marriage yet. You cannot accept this proposal.\n</proposal_protocol>",

            HandoverVerdict.Pendant_Reject_NotDatable => isZh
                ? "<proposal_protocol verdict=\"decline_not_datable\">\n- 事实：农夫（@）正拿着美人鱼吊坠向你求婚。\n- 事实：你与农夫之间是绝不可能结为夫妻的。\n</proposal_protocol>"
                : "<proposal_protocol verdict=\"decline_not_datable\">\n- Fact: The farmer (@) is proposing with a Mermaid's Pendant.\n- Fact: Marriage between you and the farmer is not possible.\n</proposal_protocol>",

            HandoverVerdict.Pendant_Reject_NoDoubleBed => isZh
                ? "<proposal_protocol verdict=\"decline_no_bed\">\n- 事实：农夫（@）正当面手捧美人鱼吊坠向你求婚。\n- 客观处境：你喜欢农夫，但农舍还没有扩建，连一张双人床都放不下，还不具备共同生活的起码条件。本次无法答应求婚。\n</proposal_protocol>"
                : "<proposal_protocol verdict=\"decline_no_bed\">\n- Fact: The farmer (@) is proposing with a Mermaid's Pendant.\n- Circumstance: You care for the farmer, but the farmhouse hasn't been upgraded to fit a double bed. You cannot accept until there is a proper place to live together.\n</proposal_protocol>",

            HandoverVerdict.Pendant_Reject_AlreadyMarried => isZh
                ? "<proposal_protocol verdict=\"decline_married\">\n- 事实：农夫（@）拿着美人鱼吊坠向你求婚。\n- 客观事实：婚姻关系不符合当前条件（已有婚约或已婚状态）。\n</proposal_protocol>"
                : "<proposal_protocol verdict=\"decline_married\">\n- Fact: The farmer (@) is proposing with a Mermaid's Pendant.\n- Fact: Marital conditions are not met (already engaged or married).\n</proposal_protocol>",

            // ── 4. 枯萎花束（分手）──
            HandoverVerdict.WiltedBouquet_Accept => isZh
                ? "<breakup_protocol verdict=\"accept\">\n- 事实：农夫（@）递给了你一束枯萎的花束，向你提出了分手。\n- 现实情况：你们的恋爱交往在此刻结束。\n</breakup_protocol>"
                : "<breakup_protocol verdict=\"accept\">\n- Fact: The farmer (@) gave you a wilted bouquet, breaking things off.\n- Reality: Your romantic relationship ends here.\n</breakup_protocol>",

            HandoverVerdict.WiltedBouquet_Reject_NotDating => isZh
                ? "<breakup_protocol verdict=\"not_dating\">\n- 事实：农夫（@）递给了你一束枯萎的花束。\n- 事实：你们本来就没有在交往，这举动让你感到莫名其妙。\n</breakup_protocol>"
                : "<breakup_protocol verdict=\"not_dating\">\n- Fact: The farmer (@) handed you a wilted bouquet.\n- Fact: You were never dating in the first place, making this gesture confusing.\n</breakup_protocol>",

            // ── 兜底 ──
            _ => isZh
                ? $"<handover_protocol verdict=\"{verdict}\">\n- 事实：农夫（@）正当面向你递出了【{itemName}】。\n</handover_protocol>"
                : $"<handover_protocol verdict=\"{verdict}\">\n- Fact: The farmer (@) is offering you [{itemName}] in person.\n</handover_protocol>"
        };
    }
}
