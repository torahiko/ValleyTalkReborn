// PerceptionInjector.cs
using System;
using System.Collections.Generic;
using System.Linq;
using StardewValley;

namespace ValleytalkReborn;

/// <summary>
/// Builds and injects perception text into the NPC system prompt.
/// </summary>
internal static class PerceptionInjector
{
    private static bool IsChineseLanguage => 
        LocalizedContentManager.CurrentLanguageCode.ToString().StartsWith("zh", StringComparison.OrdinalIgnoreCase);

    private static readonly HashSet<string> _mentionedGossipKeys =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public static string BuildPerceptionText(string npcName)
    {
        if (string.IsNullOrEmpty(npcName)) return string.Empty;

        string gossipBlock = BuildGossipBlock(npcName);
        string localBlock  = BuildLocalBlock(npcName);

        if (string.IsNullOrEmpty(gossipBlock) && string.IsNullOrEmpty(localBlock))
            return string.Empty;

        var parts = new List<string>();
        if (!string.IsNullOrEmpty(gossipBlock)) parts.Add(gossipBlock);
        if (!string.IsNullOrEmpty(localBlock))  parts.Add(localBlock);
        return string.Join("\n\n", parts);
    }

    public static void Inject(string npcName, Prompts prompts)
    {
        if (prompts == null || string.IsNullOrEmpty(npcName)) return;

        string text = BuildPerceptionText(npcName);
        if (string.IsNullOrEmpty(text)) return;

        // 兼容保留：将 gossip 与 local 合并为一段追加到 SystemPrompt。
        // 注意：local perceptions（gift/eat）每轮都变，理论上应进 CorePrompt；
        //       新代码请改用 BuildGossipBlock / BuildLocalBlock 分开调用。
        prompts.SystemPrompt += "\n\n" + text;
    }

    public static string BuildGossipBlock(string npcName)
    {
        var snapshots = PerceptionManager.Instance.GetGossipSnapshots();
        if (snapshots == null || snapshots.Count == 0) return string.Empty;

        bool isZh = IsChineseLanguage;

        // 🌟 弱化提示，仅保留至多 1 条传闻，避免每轮无脑复读
        var lines = new List<string>
        {
            isZh 
                ? "[小镇背景传闻]（背景认知：仅在与农夫当前对话主题高度契合时顺带提及，优先响应农夫的发言。）"
                : "[Town Gossip] (Background context: Only mention if directly relevant to the ongoing conversation.)"
    };

        // 优先展现 S-tier 人生大事（结婚/生子等），避免被日常世界新闻淹没
        var targetSnapshot = snapshots.FirstOrDefault(p => p.Key == "LifeEvent")
                            ?? snapshots.FirstOrDefault();

        foreach (var p in new[] { targetSnapshot }.Where(x => x != null))
        {
            if (p == null || string.IsNullOrWhiteSpace(p.Template)) continue;

            string dayKey;
            try
            {
                dayKey = Game1.Date != null
                    ? Game1.Date.TotalDays.ToString()
                    : $"{Game1.year}-{Game1.season}-{Game1.dayOfMonth}";
            }
            catch
            {
                dayKey = "unknown-day";
            }

            string dedupeKey =
                $"{dayKey}:{npcName}:{p.Key ?? ""}:{p.Template}";

            // 防止 HashSet 无限增长
            if (_mentionedGossipKeys.Count > 10000)
                _mentionedGossipKeys.Clear();

            // 同一条 gossip，对同一个 NPC，每天最多注入一次
            if (!_mentionedGossipKeys.Add(dedupeKey))
                continue;

            lines.Add($"- {p.Template}");
        }

        return lines.Count > 1 ? string.Join("\n", lines) : string.Empty;
    }

   public static string BuildLocalBlock(string npcName)
   {
        var perceptions = PerceptionManager.Instance.GetFilteredBucketFor(npcName, 3);
        if (perceptions == null || perceptions.Count == 0) return string.Empty;

        bool isZh = IsChineseLanguage;

    // 礼物类感知单独提取，注入为强上下文而非弱背景
    var giftPerceptions = perceptions
        .Where(p => p?.Key == "Gift"
            && !string.IsNullOrEmpty(p.NpcName)
            && p.NpcName.Equals(npcName, StringComparison.OrdinalIgnoreCase))
        .ToList();

    var otherPerceptions = perceptions
        .Where(p => p?.Key != "Gift")
        .ToList();

    var lines = new List<string>();

    // ── 礼物感知：以强指令形式出现 ──
    if (giftPerceptions.Any())
    {
        lines.Add(isZh
            ? "[礼物上下文]（强制要求：你刚刚收到了以下礼物，必须在本次回应中对此作出真实反应。）"
            : "[Gift Context] (REQUIRED: You just received the following gift. You MUST react to it in your response.)");

        foreach (var p in giftPerceptions)
        {
            string itemName = !string.IsNullOrEmpty(p.ItemId)
                ? GetItemDisplayName(p.ItemId)
                : (!string.IsNullOrEmpty(p.Template) ? p.Template : "???");
            string annotation = !string.IsNullOrEmpty(p.ItemId)
                ? BuildGiftTasteAnnotation(npcName, p.ItemId, isZh)
                : string.Empty;

            string entry = isZh
                ? $"- 礼物：{itemName}{annotation}"
                : $"- Gift: {itemName}{annotation}";

            lines.Add(entry);
        }
    }

    // ── 其他近距离观察：保持弱感知形式 ──
    if (otherPerceptions.Any())
    {
        lines.Add(isZh
            ? "[近期近距离观察]（潜意识印象：若与当前话题无关请忽略，切勿主动生硬开启该话题。）"
            : "[NPC's Recent Observations] (Subconscious context: Ignore if irrelevant to the farmer's current topic.)");

        foreach (var p in otherPerceptions)
        {
            string template = ResolveTemplate(p, npcName, isZh);
            string line = $"- {template}";
            if (p.Key == "Eat" && !string.IsNullOrEmpty(p.ItemId))
                line += BuildGiftTasteAnnotation(npcName, p.ItemId, isZh);
            lines.Add(line);
        }
    }

    return lines.Count > 1 ? string.Join("\n", lines) : string.Empty;
}

    private static string ResolveTemplate(PerceptionEntry entry, string npcName, bool isZh)
    {
        if (entry.Key != "Gift") return entry.Template;

        bool isRecipient = !string.IsNullOrEmpty(entry.NpcName)
            && entry.NpcName.Equals(npcName, StringComparison.OrdinalIgnoreCase);

        if (!isRecipient) return entry.Template;

        string itemName = !string.IsNullOrEmpty(entry.ItemId) ? GetItemDisplayName(entry.ItemId) : "";

        if (isZh)
        {
            return !string.IsNullOrEmpty(itemName)
                ? $"面前的玩家（@）递给你了一份礼物：[{itemName}]。"
                : "面前的玩家（@）递给你了一份礼物。";
        }
        else
        {
            return !string.IsNullOrEmpty(itemName)
                ? $"The player (@) in front of you gave you a gift: [{itemName}]."
                : "The player (@) in front of you gave you a gift.";
        }
    }

    private static string GetItemDisplayName(string itemId)
    {
        try
        {
            var item = ItemRegistry.Create(itemId);
            return item?.DisplayName ?? item?.Name ?? itemId;
        }
        catch { return itemId; }
    }

    private static string BuildGiftTasteAnnotation(string npcName, string itemId, bool isZh)
    {
        try
        {
            var npc = Game1.getCharacterFromName(npcName);
            if (npc == null) return string.Empty;

            var item = ItemRegistry.Create(itemId);
            if (item == null) return string.Empty;

            return npc.getGiftTasteForThisItem(item) switch
            {
                NPC.gift_taste_love    => isZh ? "（最爱物品，请表现出明显的惊喜与喜悦。）"  : " (Loved item — react with clear delight and gratitude.)",
                NPC.gift_taste_like    => isZh ? "（喜欢的物品，语气温暖积极。）"           : " (Liked item — warm and appreciative tone.)",
                NPC.gift_taste_dislike => isZh ? "（不喜欢的物品，可礼貌委婉地表达遗憾。）"   : " (Disliked item — politely hint at disappointment.)",
                NPC.gift_taste_hate    => isZh ? "（讨厌的物品，可表现出明显的不适或困惑。）"  : " (Hated item — react with clear discomfort or confusion.)",
                _                      => isZh ? "（普通物品，平淡接受即可。）"              : " (Neutral item — accept graciously without strong reaction.)",
            };
        }
        catch
        {
            return string.Empty;
        }
    }
}