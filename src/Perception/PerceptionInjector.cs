// PerceptionInjector.cs
// ═══════════════════════════════════════════════════════════════════════════
// PERCEPTION INJECTION ARCHITECTURE
// ═══════════════════════════════════════════════════════════════════════════
//
// This file is responsible for building perception text blocks that inform the
// NPC about recent events, ambient context, and player actions. It implements
// a two-tier perception system:
//
// 1. Gossip Perceptions (Background Rumors)
//    - Town-wide events and ambient social context
//    - Injected into SystemPrompt (static cache layer)
//    - Daily rotation with strict deduplication
//    - Examples: marriages, new babies, festivals, mayor scandals
//
// 2. Local Perceptions (Immediate Context)
//    - Events happening in NPC's immediate vicinity
//    - Injected into CorePrompt (dynamic layer)
//    - Split into strong directives and weak observations
//
// ───────────────────────────────────────────────────────────────────────────
// LOCAL PERCEPTION TIERS:
// ───────────────────────────────────────────────────────────────────────────
//
// TIER 1: Strong Directives (Immediate Events)
//   +- Gift reception: "You just received a gift from the player"
//      - Always triggers strong response
//      - Never suppressed by routing flags
//      - Consumed immediately after injection
//      - Includes taste annotation (loved/liked/disliked/hated)
//
// TIER 2: Weak Observations (Contextual Background)
//   +- Player eating food nearby
//   +- Player fishing
//   +- Witnessing someone else receive a gift
//   +- Player appearance (hat, outfit, wedding dress)
//   +- Player held item (ordinary items, suppressed in Turn 1+)
//   +- Player buffs (drunk, exhausted, injured)
//   +- Ambient details (pet, horse, bag full)
//
// ───────────────────────────────────────────────────────────────────────────
// GIFT PROTECTION MECHANISM:
// ───────────────────────────────────────────────────────────────────────────
// Gift reactions are a core game mechanic and MUST NEVER be suppressed.
//
// Protection layers:
// 1. Dedicated code path (giftPerceptions list, processed separately)
// 2. Strong directive format ("[Immediate Event] You just received...")
// 3. Independent return branch (if only gift, return immediately)
// 4. Explicit comment blocks warning against future modification
//
// ═══════════════════════════════════════════════════════════════════════════

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
    /// 由 PerceptionManager 在 DayStarted 与 Cleanup 时调用。
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
                continue;

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

            // 🔑 礼物感知注入后立即消费，防止多轮复读
            PerceptionManager.Instance.ConsumePerceptions(npcName, giftPerceptions);

            // ════════════════════════════════════════════════════════════════════════════════
            // 🔒 CRITICAL: 礼物感知穿透保护 - 以下逻辑确保礼物感知永不被路由屏蔽
            //    即使 IncludeEnvironment/IncludeShortTermContext 为 false，
            //    礼物感知也必须独立于路由控制，始终注入到 Prompt 中。
            //    修改此逻辑前请确认：NPC 收到礼物后必须做出反应，这是核心游戏机制。
            // ════════════════════════════════════════════════════════════════════════════════

            // 🔒 如果只有礼物感知且无其他感知，直接返回（避免空的"目击到的近况"块）
            if (!otherPerceptions.Any())
            {
                return string.Join("\n", lines);
            }
        }

        // ── 其他近距离观察：弱感知形式 ──
        if (otherPerceptions.Any())
        {
            // 正向聚焦指引：专注当下言行意图，背景侧影仅作自然参照，杜绝否定词
            lines.Add(isZh
                ? "[目击到的近况与现场细节]\n" +
                  "（此类信息为当下的物理环境与伴随侧影。交谈时始终专注于对方当下的言行与交流意图，仅在话题自然切中时将其作为背景参照）"
                : "[Observed Context]\n" +
                  "(Physical environment and incidental context. Focus primarily on the current conversation and dialogue intent, using these details as background reference only when organically relevant.)");

            foreach (var p in otherPerceptions)
            {
                string template = ResolveTemplate(p, npcName, isZh);
                string line = $"- {template}";
                if (p.Key == "Eat" && !string.IsNullOrEmpty(p.ItemId))
                    line += BuildGiftTasteAnnotation(npcName, p.ItemId, isZh);
                lines.Add(line);

                // 随身普通物品进入 Prompt 后标记单人单日审美疲劳
                if (p.Key == "PlayerActiveItem" && !string.IsNullOrEmpty(p.ItemId))
                {
                    PerceptionManager.Instance.MarkItemNoticedToday(npcName, p.ItemId);
                }
            }

            // 核心消费：消费真正注入了当前 Prompt 的瞬态动作与需避免同日/同场对话复读的长效装束/状态
            var consumablePerceptions = otherPerceptions
                .Where(p => ShouldConsumeAfterInjection(p.Key))
                .ToList();

            if (consumablePerceptions.Any())
            {
                PerceptionManager.Instance.ConsumePerceptions(npcName, consumablePerceptions);
            }
        }

        return lines.Count > 1 ? string.Join("\n", lines) : string.Empty;
    }

    /// <summary>
    /// 判断感知条目是否在注入当前 NPC 的 Prompt 后即完成消费。
    /// 包含：
    /// 1. 瞬态交互动作（钓鱼、吃东西、送礼等，目击一次即消费）；
    /// 2. 静态长效装束与环境背景（帽子、特殊服饰、昨夜晕倒、宠物坐骑等），
    ///    注入后对当前 NPC 标记已阅，避免在同场对话或后续轮次中重复注入。
    /// </summary>
    private static bool ShouldConsumeAfterInjection(string key)
    {
        if (string.IsNullOrEmpty(key)) return false;

        return key switch
        {
            // 瞬态动作事件（目击一次即消费）
            "Gift"          => true, // 旁观他人收礼
            "Eat"           => true, // 吃东西
            "Fish"          => true, // 钓鱼
            "LegendaryFish" => true, // 钓上传说鱼的现场目击
            "Chop"          => true, // 砍树
            "Place"         => true, // 放置物品
            "Harvest"       => true, // 收获作物
            "Talk"          => true, // 与他人交谈

            // 外观装束与历史/伴随细节（注入后对当前 NPC 消费，避免同场对话每句复读）
            "PlayerHat"                  => true, // 帽子
            "PlayerWeddingOutfit"        => true, // 婚礼礼服
            "PlayerSpecialOutfit_Shorts" => true, // 镇长幸运短裤
            "PlayerSpecialOutfit_Trash"  => true, // 垃圾桶外观
            "PlayerSpecialOutfit_Hazmat" => true, // 防化生化服
            "PlayerFainted"              => true, // 昨夜晕倒经历
            "PlayerPet"                  => true, // 随行宠物
            "PlayerHorseNearby"          => true, // 附近坐骑
            "PlayerRidingHorse"          => true, // 骑乘状态
            "PlayerBagFull"              => true, // 背包满载
            "PlayerActiveItem"           => true, // 手持携带物

            // 生理/Buff/信物（重伤、力竭、醉酒、花束、求婚吊坠等）保持自然存活或由状态解除时显式 Evict
            _ => false
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