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
        LocalizedContentManager.CurrentLanguageCode == LocalizedContentManager.LanguageCode.zh;

    private static HashSet<string> _mentionedGossipKeys =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 清空跨天/跨存档的 gossip 提及去重记录。
    /// 由 PerceptionManager 在 DayStarted 时调用。
    /// </summary>
    public static void ResetMentionedGossipKeys()
    {
        _mentionedGossipKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

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

        prompts.SystemPrompt += "\n\n" + text;
    }

    public static string BuildGossipBlock(string npcName)
    {
        var snapshots = PerceptionManager.Instance.GetGossipSnapshots();
        if (snapshots == null || snapshots.Count == 0) return string.Empty;

        bool isZh = IsChineseLanguage;

        var lines = new List<string>
        {
            isZh 
                ? "[小镇传闻]（小镇近期的日常谈资与背景印象）"
                : "[Town Rumors] (Passive background information circulating around town)"
        };

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

        // 按优先级排队候选：优先 LifeEvent，其余按原顺序补上，
        // 直到找到一条今天还没对该 NPC 提过的八卦为止。
        var orderedCandidates = snapshots
            .Where(p => p != null && !string.IsNullOrWhiteSpace(p.Template))
            .OrderByDescending(p => p.Key == "LifeEvent")
            .ToList();

        PerceptionEntry targetSnapshot = null;
        string targetDedupeKey = null;

        foreach (var candidate in orderedCandidates)
        {
            string dedupeKey = $"{dayKey}:{npcName}:{candidate.Key ?? ""}:{candidate.Template}";
            if (_mentionedGossipKeys.Contains(dedupeKey))
                continue; // 今天已经跟这个 NPC 提过这条八卦了，跳过

            targetSnapshot   = candidate;
            targetDedupeKey  = dedupeKey;
            break;
        }

        if (targetSnapshot != null)
        {
            if (_mentionedGossipKeys.Count > 10000)
                _mentionedGossipKeys.Clear();

            _mentionedGossipKeys.Add(targetDedupeKey);
            lines.Add($"- {targetSnapshot.Template}");
        }

        return lines.Count > 1 ? string.Join("\n", lines) : string.Empty;
    }

    public static string BuildLocalBlock(string npcName)
    {
        var perceptions = PerceptionManager.Instance.GetFilteredBucketFor(npcName, 3);
        if (perceptions == null || perceptions.Count == 0) return string.Empty;

        bool isZh = IsChineseLanguage;

        // 1. 本人收到的礼物：必须给出强反应，进入强指令块
        var giftPerceptions = perceptions
            .Where(p => p?.Key == "Gift"
                && !string.IsNullOrEmpty(p.NpcName)
                && p.NpcName.Equals(npcName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        // 2. 弱感知：非礼物事件 + 旁观别人收到礼物的目击事件
        var otherPerceptions = perceptions
            .Where(p => p?.Key != "Gift" || 
                       (p.Key == "Gift" && !string.Equals(p.NpcName, npcName, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        var lines = new List<string>();

        // ── 礼物感知：以强指令形式出现 ──
        if (giftPerceptions.Any())
        {
            lines.Add(isZh
                ? "[即时事件] 你刚刚收到了玩家递来的礼物，请对此作出符合人设的回应："
                : "[Immediate Event] You just received a gift from the player. Respond naturally according to your character:");

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

            PerceptionManager.Instance.ConsumePerceptions(npcName, giftPerceptions);
        }

        // ── 其他近距离观察：弱感知形式 ──
        if (otherPerceptions.Any())
        {
            lines.Add(isZh
                ? "[目击到的近况与现场细节]"
                : "[Observed Context]");

            foreach (var p in otherPerceptions)
            {
                string template = ResolveTemplate(p, npcName, isZh);
                string line = $"- {template}";
                if (p.Key == "Eat" && !string.IsNullOrEmpty(p.ItemId))
                    line += BuildGiftTasteAnnotation(npcName, p.ItemId, isZh);
                lines.Add(line);

                // ★ 关键防复读：随身物品一旦真正进入当前 NPC 的对话 Prompt，标记为当日已阅
                if (p.Key == "PlayerActiveItem" && !string.IsNullOrEmpty(p.ItemId))
                {
                    PerceptionManager.Instance.MarkItemNoticedToday(npcName, p.ItemId);
                }
            }

            // 核心消费：仅消费真正注入了当前 Prompt 的瞬态动作事件（Eat, Fish, Chop 等）
            var transientActions = otherPerceptions
                .Where(p => IsTransientAction(p.Key))
                .ToList();

            if (transientActions.Any())
            {
                PerceptionManager.Instance.ConsumePerceptions(npcName, transientActions);
            }
        }

        return lines.Count > 1 ? string.Join("\n", lines) : string.Empty;
    }

    /// <summary>
    /// 判断事件是否为单次瞬态动作（注入后立即对该 NPC 消费，避免同一次对话连续复读）。
    /// </summary>
    private static bool IsTransientAction(string key)
    {
        if (string.IsNullOrEmpty(key)) return false;

        // 玩家自身持续身体/装备/精神状态不属于瞬态动作，保持自然存活
        if (key.StartsWith("Player", StringComparison.OrdinalIgnoreCase))
            return false;

        return key switch
        {
            "Gift"          => true, // 旁观他人收礼
            "Eat"           => true, // 吃东西
            "Fish"          => true, // 钓鱼
            "LegendaryFish" => true, // 钓上传说鱼的现场目击
            "Chop"          => true, // 砍树
            "Place"         => true, // 放置物品
            "Harvest"       => true, // 收获作物
            "Talk"          => true, // 与他人交谈
            _               => false
        };
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
                NPC.gift_taste_love    => isZh ? "（最爱的礼物）" : " (Loved gift)",
                NPC.gift_taste_like    => isZh ? "（喜欢的礼物）" : " (Liked gift)",
                NPC.gift_taste_dislike => isZh ? "（不喜欢的礼物）" : " (Disliked gift)",
                NPC.gift_taste_hate    => isZh ? "（讨厌的礼物）" : " (Hated gift)",
                _                      => isZh ? "（普通礼物）" : " (Neutral gift)",
            };
        }
        catch
        {
            return string.Empty;
        }
    }
}