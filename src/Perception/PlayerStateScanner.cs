using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Characters;
using StardewValley.Objects;
using StardewValley.Tools;

namespace ValleytalkReborn;

/// <summary>
/// 玩家即时身体状态与可见属性扫描器。
/// 在每次组装对话/Bark Prompt 前调用，将可观察到的状态同步到 PerceptionManager (Track 2) 中。
/// 状态满足时录入，不满足或状态消失时立即显式驱逐（Evict），杜绝旧上下文跨地图或长效残留。
/// 文本仅记录纯粹客观环境/感官事实，不附带“不要提/可忽略”等抑制性指令，避免诱发模型的粉色大象效应。
/// </summary>
internal static class PlayerStateScanner
{
    // 星露谷物语官方分类常量：建材资源分类（Wood, Stone, Hardwood, Clay, Fiber 等均为 -16）
    private const int BuildingResourcesCategory = -16;

    private static bool IsZh =>
        LocalizedContentManager.CurrentLanguageCode == LocalizedContentManager.LanguageCode.zh;

    /// <summary>
    /// 扫描入口：记录所有当前可被 NPC 观察到的玩家状态。
    /// </summary>
    public static void Scan()
    {
        try
        {
            var player = Game1.player;
            if (player == null || !Context.IsWorldReady) return;

            bool isZh = IsZh;

            // 生理与体力状态
            ScanExhaustion(player, isZh);
            ScanDrunk(player, isZh);
            ScanHealth(player, isZh);
            ScanLateNight(player, isZh);
            ScanFainted(player, isZh);

            // 伴随生物（宠物 / 坐骑）
            ScanPet(player, isZh);
            ScanHorse(player, isZh);

            // 具有强烈外在感知的长效 Buff / 视觉光环
            ScanSpeedBuff(player, isZh);
            ScanGarlicSmell(player, isZh);
            ScanMonsterMusk(player, isZh);
            ScanGlowing(player, isZh);

            // 外部装束
            ScanHat(player, isZh);
            ScanOutfit(player, isZh);

            // 携带物感知流水线（单向抢占：求婚信物 > 表白花束 > 背包满载 > 手边物品 > 深度感官感知保底）
            ScanPlayerCarriedItemsPipeline(player, isZh);
        }
        catch (Exception ex)
        {
            ModEntry.SMonitor?.Log($"[PlayerStateScanner] Scan error: {ex.Message}", LogLevel.Trace);
        }
    }

    // ─────────────────────────────────────────────
    //  子扫描器：携带物链式感知流水线（严格分级互斥）
    // ─────────────────────────────────────────────

    private static void ScanPlayerCarriedItemsPipeline(Farmer player, bool isZh)
    {
        const string BouquetId = "(O)458";
        const string MermaidPendantId = "(O)460";

        bool hasPendant = player.Items.ContainsId(MermaidPendantId);
        bool hasBouquet = player.Items.ContainsId(BouquetId);

        // 1. 最高优先级：求婚信物（美人鱼吊坠）
        if (hasPendant)
        {
            PerceptionManager.Instance.Record(
                key: "PlayerHasPendant",
                template: isZh
                    ? "[随身细节] 玩家随身带着一枚用于求婚的美人鱼吊坠。"
                    : "[Item detail] The player is carrying a Mermaid's Pendant (used for marriage proposals).",
                lifetimeHours: 20,
                isLandmark: false,
                itemId: MermaidPendantId);

            PerceptionManager.Instance.Evict("PlayerHasBouquet");
            PerceptionManager.Instance.Evict("PlayerBagFull");
            PerceptionManager.Instance.Evict("PlayerActiveItem");
            return;
        }
        PerceptionManager.Instance.Evict("PlayerHasPendant");

        // 2. 次高优先级：确立关系道具（表白花束）
        if (hasBouquet)
        {
            PerceptionManager.Instance.Record(
                key: "PlayerHasBouquet",
                template: isZh
                    ? "[随身细节] 玩家随身带着一束用于正式告白的花束。"
                    : "[Item detail] The player is carrying a bouquet (used for confessions).",
                lifetimeHours: 20,
                isLandmark: false,
                itemId: BouquetId);

            PerceptionManager.Instance.Evict("PlayerBagFull");
            PerceptionManager.Instance.Evict("PlayerActiveItem");
            return;
        }
        PerceptionManager.Instance.Evict("PlayerHasBouquet");

        // 3. 背包满载（严格统计非空槽位，兼容未升级背包）
        int filledSlots = player.Items?.Count(item => item != null) ?? 0;
        bool isFull = filledSlots >= player.MaxItems;

        if (isFull)
        {
            PerceptionManager.Instance.Record(
                key: "PlayerBagFull",
                template: isZh
                    ? "[随身细节] 玩家的背包塞得鼓鼓囊囊、满满当当，全身上下都带满了东西。"
                    : "[Ambient detail] The player's backpack is completely stuffed and overflowing with gear.",
                lifetimeHours: 2,
                isLandmark: false);

            PerceptionManager.Instance.Evict("PlayerActiveItem");
            return;
        }
        PerceptionManager.Instance.Evict("PlayerBagFull");

        // 4. 快捷栏红框手边携带物
        int selectedIndex = player.CurrentToolIndex;
        Item handItem = null;

        if (player.Items != null && selectedIndex >= 0 && selectedIndex < player.Items.Count)
        {
            handItem = player.Items[selectedIndex];
        }

        if (handItem == null)
        {
            handItem = player.CurrentItem;
        }

        if (handItem != null)
        {
            string name = handItem.DisplayName;
            if (!string.IsNullOrWhiteSpace(name) &&
                !name.StartsWith("错误物品", StringComparison.Ordinal) &&
                !name.StartsWith("Error", StringComparison.OrdinalIgnoreCase))
            {
                string itemTemplate = BuildHandheldItemTemplate(handItem, name, isZh);

                PerceptionManager.Instance.Record(
                    key: "PlayerActiveItem",
                    template: itemTemplate,
                    lifetimeHours: 1,
                    isLandmark: false,
                    itemId: handItem.QualifiedItemId);
                return;
            }
        }

        // 5. 深度感知：消除透视感，将背包深处特色/高价值物品转变为物理感官线索（红框选空格子且未满包时触发）
        Item interestingItem = FindInterestingPackItem(player);

        if (interestingItem != null)
        {
            string sensoryTemplate = BuildPackSensoryTemplate(interestingItem, isZh);

            PerceptionManager.Instance.Record(
                key: "PlayerActiveItem",
                template: sensoryTemplate,
                lifetimeHours: 1,
                isLandmark: false,
                itemId: interestingItem.QualifiedItemId);
            return;
        }

        // 均未命中，彻底清空
        PerceptionManager.Instance.Evict("PlayerActiveItem");
    }

    // ─────────────────────────────────────────────
    //  手持物语境解析（手持展示状态）
    // ─────────────────────────────────────────────

    private static string BuildHandheldItemTemplate(Item handItem, string name, bool isZh)
    {
        string qId = handItem.QualifiedItemId;

        // 1. 武器类（剑、匕首、锤、弹弓）
        if (handItem is MeleeWeapon || handItem is Slingshot)
        {
            return isZh
                ? $"[随身细节] 玩家腰间正别着一把武器【{name}】。"
                : $"[Item detail] The player is firmly holding a weapon [{name}] in hand.";
        }

        // 2. 农场工具类（水壶、锄头、斧头、十字镐、钓竿）
        if (handItem is Tool tool && !(tool is MeleeWeapon or Slingshot))
        {
            if (tool is FishingRod)
            {
                return isZh
                    ? $"[随身细节] 玩家随手提着一把钓鱼竿【{name}】。"
                    : $"[Item detail] The player is holding a fishing rod [{name}].";
            }
            return isZh
                ? $"[随身细节] 玩家手里正拎着农具【{name}】。"
                : $"[Item detail] The player is carrying a farm tool [{name}].";
        }

        // 3. 恶作剧与禁忌/违和物品（垃圾、Joja可乐、怪异泥偶、海沟废品）
        if (qId == "(O)167") // Joja可乐
        {
            return isZh
                ? $"[随身细节] 玩家手里正拿着一罐【{name}】。"
                : $"[Item detail] The player is holding a can of chilled [{name}] with its unmistakable corporate logo.";
        }
        if (qId is "(O)168" or "(O)169" or "(O)170" or "(O)171" or "(O)172") // 垃圾、浮木、破眼镜等
        {
            return isZh
                ? $"[随身细节] 玩家手里莫名其妙拿着一块刚捡到的废品【{name}】（湿漉漉、脏兮兮的）。"
                : $"[Item detail] The player is strangely holding a piece of trash [{name}] (damp and dirty).";
        }
        if (qId is "(O)126" or "(O)127") // 奇异玩偶
        {
            return isZh
                ? $"[随身细节] 玩家正端详着一个面目诡异古怪的【{name}】。"
                : $"[Item detail] The player is examining an eerie-looking [{name}] .";
        }

        // 4. 基础建材/杂物（开荒劳作侧影）
        bool isMundaneMaterial = handItem.Category == BuildingResourcesCategory
                              || qId is "(O)770" or "(O)771" or "(O)388" or "(O)390" or "(O)92" or "(O)330";

        if (isMundaneMaterial)
        {
            return isZh
                ? $"[随身细节] 玩家随手攥着一些日常杂物【{name}】（开荒与劳作的痕迹）。"
                : $"[Ambient detail] The player is casually holding some mundane materials [{name}] (signs of farm work.).";
        }

        // 5. 常规物品通用保底：弱化动作指向，客观记录随身携带
        return isZh
            ? $"[随身细节] 玩家随身带着一件【{name}】（日常携带物）。"
            : $"[Item detail] The player is carrying [{name}] (everyday carry).";
    }

    // ─────────────────────────────────────────────
    //  背包深度搜索与感官化解析（无透视物理线索）
    // ─────────────────────────────────────────────

    private static Item FindInterestingPackItem(Farmer player)
    {
        if (player.Items == null) return null;

        const string BouquetId = "(O)458";
        const string MermaidPendantId = "(O)460";

        var candidates = player.Items
            .Where(i => i != null &&
                        i.QualifiedItemId != BouquetId &&
                        i.QualifiedItemId != MermaidPendantId &&
                        !string.IsNullOrWhiteSpace(i.DisplayName) &&
                        !i.DisplayName.StartsWith("错误物品", StringComparison.Ordinal) &&
                        !i.DisplayName.StartsWith("Error", StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (!candidates.Any()) return null;

        // 优先级 A：高戏剧性与特殊物品（恐龙蛋、史莱姆、五彩碎片、奇异玩偶、怪兽精华）
        var dramaticItem = candidates.FirstOrDefault(i =>
            i.QualifiedItemId is "(O)107" // 恐龙蛋
                              or "(O)74"  // 五彩碎片
                              or "(O)768" // 太阳精华
                              or "(O)769" // 虚空精华
                              or "(O)305" // 虚空蛋黄酱
                              or "(O)684" // 虫肉
                              or "(O)766" // 史莱姆泥
                              or "(O)126" or "(O)127" // 玩偶
        );
        if (dramaticItem != null) return dramaticItem;

        // 优先级 B：高单价贵重物保底（>= 500g）
        var topValuable = candidates
            .OrderByDescending(i => i.salePrice(true))
            .FirstOrDefault();

        if (topValuable != null && topValuable.salePrice(true) >= 500)
            return topValuable;

        return null;
    }

    private static string BuildPackSensoryTemplate(Item item, bool isZh)
    {
        string name = item.DisplayName;
        int category = item.Category;
        string qId = item.QualifiedItemId;

        // 1. 活物、黏液与蠕动感（史莱姆、虫肉、活动物蛋）
        if (qId is "(O)766" or "(O)684" || qId.StartsWith("(O)413") || qId.StartsWith("(O)437") || qId.StartsWith("(O)439"))
        {
            return isZh
                ? $"[动态细节] 玩家的行囊缝隙里隐约渗出点奇怪的黏液或轻微蠕动声（里面似乎装着【{name}】这类古怪的怪物材料/活物）。"
                : $"[Physical clue] A slight squishing sound and strange residue hint at bizarre monster materials like [{name}] inside the player's pack.";
        }

        // 2. 远古与考古神秘物（恐龙蛋、奇异玩偶、化石）
        if (qId is "(O)107" or "(O)126" or "(O)127" or "(O)579" or "(O)580")
        {
            return isZh
                ? $"[视觉线索] 玩家行囊的搭扣旁露出了【{name}】布满远古裂纹的一角（散发着浓郁的历史陈旧感与泥土味）。"
                : $"[Visual clue] The cracked, weathered edge of a curiosity like [{name}] peeks out from the player's pack flap, smelling of ancient earth.";
        }

        // 3. 气味类：新鲜料理、生鱼、松露、虚空蛋黄酱
        if (category is StardewValley.Object.CookingCategory or StardewValley.Object.FishCategory ||
            qId is "(O)430" or "(O)432" or "(O)305")
        {
            string scentDesc = (qId == "(O)305")
                ? (isZh ? "刺鼻烧焦的硫磺与变质怪味" : "pungent, sulfurous stench")
                : (isZh ? "新鲜食物/食材的特有香气" : "rich, distinct aroma");

            return isZh
                ? $"[嗅觉线索] 走近时，你能从玩家行囊里隐约闻到一股【{name}】散发出的{scentDesc}（仅为飘散出来的气味线索）。"
                : $"[Olfactory cue] Standing close, you catch a whiff of [{name}] ({scentDesc}) escaping from the player's pack.";
        }

        // 4. 光芒与魔法脉动（五彩碎片、太阳/虚空精华、稀有宝石）
        if (category is StardewValley.Object.GemCategory or StardewValley.Object.mineralsCategory ||
            qId is "(O)74" or "(O)768" or "(O)769")
        {
            return isZh
                ? $"[视觉线索] 玩家行囊的搭扣边缘，隐约泛着【{name}】独特的鲜明反光或微弱魔法微光（不经意间瞥见的一角）。"
                : $"[Visual clue] A subtle gleam or faint magical pulse from [{name}] caught your eye through the flap of the player's pack (an incidental glance; do not speak as if possessing X-ray vision).";
        }

        // 5. 沉重金属与负重碰撞（金属锭、重型矿石、建材）
        if (category == StardewValley.Object.metalResources || category == BuildingResourcesCategory)
        {
            return isZh
                ? $"[动态线索] 玩家随身行囊明显被沉甸甸的重物压得下沉，走动间伴有硬物撞击声（里面似乎装着【{name}】这类沉重物资）。"
                : $"[Physical clue] The player's pack is heavily weighed down, clinking slightly with the heft of materials like [{name}].";
        }

        // 6. 通用保底（侧袋露出一角）
        return isZh
            ? $"[随身线索] 玩家行囊的侧袋边缘隐约露出了【{name}】的一角（仅为视线扫过的随身侧影）。"
            : $"[Item glimpse] You caught a brief glimpse of [{name}] peeking out from the side pocket of the player's pack.";
    }

    // ─────────────────────────────────────────────
    //  子扫描器：伴随生物、坐骑与外观
    // ─────────────────────────────────────────────

    private static void ScanHorse(Farmer player, bool isZh)
    {
        if (player.isRidingHorse() && player.mount != null)
        {
            PerceptionManager.Instance.Evict("PlayerHorseNearby");

            string horseName = player.mount.displayName ?? (isZh ? "马儿" : "horse");
            var horseHat = player.mount.hat.Value;
            string hatInfo = horseHat != null
                ? (isZh ? $"，马头上正戴着一顶【{horseHat.DisplayName}】" : $", and the horse is wearing a [{horseHat.DisplayName}]")
                : "";

            string desc = isZh
                ? $"[环境细节] 面前的玩家正高坐在马背上（马名叫【{horseName}】{hatInfo}），居高临下地与你面对面交谈。"
                : $"[Ambient detail] The player is mounted on their horse (named {horseName}{hatInfo}), looking down from the saddle while speaking to you.";

            PerceptionManager.Instance.Record("PlayerRidingHorse", desc, lifetimeHours: 1, isLandmark: false);
            return;
        }

        PerceptionManager.Instance.Evict("PlayerRidingHorse");

        Horse nearbyHorse = null;
        if (player.currentLocation != null)
        {
            var charactersSnapshot = player.currentLocation.characters.ToArray();
            foreach (var character in charactersSnapshot)
            {
                if (character is Horse h && h.rider == null && Vector2.Distance(player.Tile, h.Tile) <= 8f)
                {
                    nearbyHorse = h;
                    break;
                }
            }
        }

        if (nearbyHorse != null)
        {
            string horseName = nearbyHorse.displayName ?? (isZh ? "马儿" : "horse");
            string desc = isZh
                ? $"[环境细节] 玩家的坐骑【{horseName}】正安静地停在不远处的路边等待着主人。"
                : $"[Ambient detail] The player's horse [{horseName}] is standing quietly nearby.";

            PerceptionManager.Instance.Record("PlayerHorseNearby", desc, lifetimeHours: 1, isLandmark: false);
        }
        else
        {
            PerceptionManager.Instance.Evict("PlayerHorseNearby");
        }
    }

    private static void ScanPet(Farmer player, bool isZh)
    {
        var pet = player.getPet();
        if (pet == null)
        {
            PerceptionManager.Instance.Evict("PlayerPet");
            return;
        }

        string petLocation = pet.currentLocation?.Name ?? "";
        string playerLocation = player.currentLocation?.Name ?? "";

        if (string.IsNullOrEmpty(petLocation) ||
            !string.Equals(petLocation, playerLocation, StringComparison.OrdinalIgnoreCase) ||
            Vector2.Distance(player.Tile, pet.Tile) > 12f)
        {
            PerceptionManager.Instance.Evict("PlayerPet");
            return;
        }

        string petName = pet.displayName ?? (isZh ? "宠物" : "pet");

        PerceptionManager.Instance.Record(
            key: "PlayerPet",
            template: isZh
                ? $"[环境细节] 玩家的宠物【{petName}】跟在玩家身旁随行。"
                : $"[Ambient detail] The player's pet [{petName}] is accompanying them nearby.",
            lifetimeHours: 1,
            isLandmark: false);
    }

    private static void ScanHat(Farmer player, bool isZh)
    {
        var hat = player.hat.Value;
        string hatName = hat?.DisplayName;

        if (hat != null && !string.IsNullOrWhiteSpace(hatName))
        {
            PerceptionManager.Instance.Record(
                key: "PlayerHat",
                template: isZh
                    ? $"[装束细节] 玩家头上正戴着一顶醒目的【{hatName}】。"
                    : $"[Visual detail] The player is wearing a notable hat: [{hatName}].",
                lifetimeHours: 20,
                isLandmark: false,
                itemId: hat.QualifiedItemId);
        }
        else
        {
            PerceptionManager.Instance.Evict("PlayerHat");
        }
    }

    private static void ScanOutfit(Farmer player, bool isZh)
    {
        var shirt = player.shirtItem.Value;
        var pants = player.pantsItem.Value;
        var hat = player.hat.Value;

        string shirtId = shirt?.QualifiedItemId ?? "";
        string pantsId = pants?.QualifiedItemId ?? "";
        string hatId = hat?.QualifiedItemId ?? "";

        bool wearingLuckyShorts = pantsId == "(P)15" || pantsId == "(P)71" || 
                                  (pants?.ItemId is "15" or "71") ||
                                  (pants?.Name?.Contains("Lucky", StringComparison.OrdinalIgnoreCase) == true);

        bool wearingWedding = shirtId == "(S)1028" || shirtId == "(S)1029" || hatId == "(H)16";
        bool wearingTrashOutfit = hatId == "(H)61" || shirtId == "(S)1008";
        bool wearingHazmat = shirtId == "(S)Hazmat_Suit" && pantsId == "(P)Hazmat_Pants";

        if (wearingLuckyShorts)
        {
            PerceptionManager.Instance.Record(
                key: "PlayerSpecialOutfit_Shorts",
                template: isZh
                    ? "[装束细节] 玩家下身正大摇大摆地穿着镇长刘易斯那条带着金边的紫色幸运短裤。"
                    : "[Visual detail] The player is brazenly wearing Mayor Lewis's trimmed purple lucky shorts.",
                lifetimeHours: 20,
                isLandmark: false,
                itemId: pantsId);
        }
        else
        {
            PerceptionManager.Instance.Evict("PlayerSpecialOutfit_Shorts");
        }

        if (wearingWedding)
        {
            PerceptionManager.Instance.Record(
                key: "PlayerWeddingOutfit",
                template: isZh
                    ? "[装束细节] 玩家身上正穿着隆重喜庆的婚礼礼服。"
                    : "[Visual detail] The player is dressed in formal wedding attire.",
                lifetimeHours: 20,
                isLandmark: false,
                itemId: shirtId);

            PerceptionManager.Instance.Evict("PlayerHat");
        }
        else
        {
            PerceptionManager.Instance.Evict("PlayerWeddingOutfit");
        }

        if (wearingTrashOutfit)
        {
            PerceptionManager.Instance.Record(
                key: "PlayerSpecialOutfit_Trash",
                template: isZh
                    ? "[装束细节] 玩家头上顶着一个垃圾桶盖，身上穿着垃圾桶风格的外观。"
                    : "[Visual detail] The player is wearing a trash can lid hat and garbage-themed outfit.",
                lifetimeHours: 20,
                isLandmark: false,
                itemId: hatId);
        }
        else
        {
            PerceptionManager.Instance.Evict("PlayerSpecialOutfit_Trash");
        }

        if (wearingHazmat)
        {
            PerceptionManager.Instance.Record(
                key: "PlayerSpecialOutfit_Hazmat",
                template: isZh
                    ? "[装束细节] 玩家全身套在极其厚重严密的防辐射生化服里。"
                    : "[Visual detail] The player is suited up in a heavy full-body hazmat suit.",
                lifetimeHours: 20,
                isLandmark: false,
                itemId: shirtId);
        }
        else
        {
            PerceptionManager.Instance.Evict("PlayerSpecialOutfit_Hazmat");
        }
    }

    // ─────────────────────────────────────────────
    //  子扫描器：Buff、气味与生理状态
    // ─────────────────────────────────────────────

    private static void ScanSpeedBuff(Farmer player, bool isZh)
    {
        if (player.buffs.Speed > 0)
        {
            PerceptionManager.Instance.Record(
                key: "PlayerSpeedBuff",
                template: isZh
                    ? "[生理状态] 玩家步伐极快、步频飞速，神情显得异常亢奋躁动（似乎刚喝了大剂量咖啡）。"
                    : "[Physical state] The player is moving unusually fast, practically buzzing with caffeine energy.",
                lifetimeHours: 1,
                isLandmark: false);
        }
        else
        {
            PerceptionManager.Instance.Evict("PlayerSpeedBuff");
        }
    }

    private static void ScanGarlicSmell(Farmer player, bool isZh)
    {
        if (player.hasBuff("oil_of_garlic"))
        {
            PerceptionManager.Instance.Record(
                key: "PlayerGarlicSmell",
                template: isZh
                    ? "[嗅觉线索] 玩家周身弥漫着一股极其冲鼻浓烈的大蒜油气味，几步之外都能闻到。"
                    : "[Olfactory cue] The player is radiating an overpowering stench of pungent garlic oil.",
                lifetimeHours: 1,
                isLandmark: false);
        }
        else
        {
            PerceptionManager.Instance.Evict("PlayerGarlicSmell");
        }
    }

    private static void ScanMonsterMusk(Farmer player, bool isZh)
    {
        if (player.hasBuff("monster_musk"))
        {
            PerceptionManager.Instance.Record(
                key: "PlayerMonsterMusk",
                template: isZh
                    ? "[嗅觉线索] 玩家身上散发着一股令人作呕的怪兽麝香腥气，仿佛刚从魔物巢穴深处爬出来。"
                    : "[Olfactory cue] The player reeks of dangerous Monster Musk, smelling of dark caves and wild beasts.",
                lifetimeHours: 1,
                isLandmark: false);
        }
        else
        {
            PerceptionManager.Instance.Evict("PlayerMonsterMusk");
        }
    }

    private static void ScanGlowing(Farmer player, bool isZh)
    {
        bool isNightOrDark = Game1.isDarkOut(player.currentLocation) || Game1.timeOfDay >= 1800;
        
        bool hasGlowRing = player.isWearingRing("(TR)517") || player.isWearingRing("517") ||
                           player.isWearingRing("(TR)516") || player.isWearingRing("516") ||
                           player.isWearingRing("(TR)527") || player.isWearingRing("527") ||
                           player.hasBuff("glow") || player.hasBuff("glowing");

        if (isNightOrDark && hasGlowRing)
        {
            PerceptionManager.Instance.Record(
                key: "PlayerGlowing",
                template: isZh
                    ? "[视觉线索] 昏暗的环境中，玩家身上正散发着魔法戒指带来的明亮光晕，照亮了四周。"
                    : "[Visual cue] In the dim light, the player is radiating a noticeable magical glow from their ring.",
                lifetimeHours: 1,
                isLandmark: false);
        }
        else
        {
            PerceptionManager.Instance.Evict("PlayerGlowing");
        }
    }

    private static void ScanExhaustion(Farmer player, bool isZh)
    {
        bool isExhausted = player.exhausted.Value;
        float staminaRatio = player.Stamina / Math.Max(1f, player.MaxStamina);
        bool isTired = !isExhausted && staminaRatio <= 0.15f;

        if (isExhausted)
        {
            PerceptionManager.Instance.Evict("PlayerTired");
            PerceptionManager.Instance.Record(
                key: "PlayerExhausted",
                template: isZh
                    ? "[生理状态] 面前的玩家此刻已精疲力竭，头顶冒出虚弱的汗水，步履十分沉重。"
                    : "[Physical state] The player in front of you looks completely exhausted, visibly sweating and moving heavily.",
                lifetimeHours: 1,
                isLandmark: false);
        }
        else if (isTired)
        {
            PerceptionManager.Instance.Evict("PlayerExhausted");
            PerceptionManager.Instance.Record(
                key: "PlayerTired",
                template: isZh
                    ? "[生理状态] 面前的玩家看起来体力所剩无几，神情有些疲惫。"
                    : "[Physical state] The player in front of you looks tired and noticeably low on energy.",
                lifetimeHours: 1,
                isLandmark: false);
        }
        else
        {
            PerceptionManager.Instance.Evict("PlayerExhausted");
            PerceptionManager.Instance.Evict("PlayerTired");
        }
    }

    private static void ScanDrunk(Farmer player, bool isZh)
    {
        if (player.hasBuff("tipsy"))
        {
            PerceptionManager.Instance.Record(
                key: "PlayerDrunk",
                template: isZh
                    ? "[状态特征] 面前的玩家身上散发着酒气，走路步态有些微晃飘忽。"
                    : "[Physical state] The player smells of alcohol and is walking with a slightly unsteady, tipsy gait.",
                lifetimeHours: 2,
                isLandmark: false);
        }
        else
        {
            PerceptionManager.Instance.Evict("PlayerDrunk");
        }
    }

    private static void ScanHealth(Farmer player, bool isZh)
    {
        float healthRatio = (float)player.health / Math.Max(1, player.maxHealth);
        if (healthRatio <= 0.2f)
        {
            PerceptionManager.Instance.Record(
                key: "PlayerLowHealth",
                template: isZh
                    ? "[生理状态] 面前的玩家面色苍白且身上带着伤痕，像是刚经历过恶战。"
                    : "[Physical state] The player looks visibly pale and wounded, as if having just survived a grueling fight.",
                lifetimeHours: 1,
                isLandmark: false);
        }
        else
        {
            PerceptionManager.Instance.Evict("PlayerLowHealth");
        }
    }

    private static void ScanLateNight(Farmer player, bool isZh)
    {
        if (Game1.timeOfDay >= 2500)
        {
            PerceptionManager.Instance.Record(
                key: "PlayerLateNight",
                template: isZh
                    ? "[时间环境] 当前时间已过凌晨 1 点，夜色极深，玩家依然独自在外游荡。"
                    : "[Environmental context] It is well past 1:00 AM in the dead of night, and the player is still wandering outside.",
                lifetimeHours: 1,
                isLandmark: false);
        }
        else
        {
            PerceptionManager.Instance.Evict("PlayerLateNight");
        }
    }

    private static void ScanFainted(Farmer player, bool isZh)
    {
        if (FaintedToday)
        {
            PerceptionManager.Instance.Record(
                key: "PlayerFainted",
                template: isZh
                    ? "[状态线索] 玩家昨晚精疲力竭晕倒在外并被人送回家中，今天一早神态还未完全复原。"
                    : "[Context cue] The player passed out from exhaustion outside last night and was brought home; they still appear somewhat weary this morning.",
                lifetimeHours: 20,
                isLandmark: false);
        }
        else
        {
            PerceptionManager.Instance.Evict("PlayerFainted");
        }
    }

    // ─────────────────────────────────────────────
    //  缓存与日更生命周期
    // ─────────────────────────────────────────────

    private static bool WasFaintingPending { get; set; } = false;
    private static bool FaintedToday { get; set; } = false;

    public static void OnDayEnding()
    {
        try
        {
            var player = Game1.player;
            WasFaintingPending = player != null && (player.passedOut || Game1.timeOfDay >= 2600);
        }
        catch
        {
            WasFaintingPending = false;
        }
    }

    public static void OnDayStarted()
    {
        try
        {
            FaintedToday = WasFaintingPending;
            WasFaintingPending = false;
        }
        catch
        {
            FaintedToday = false;
        }
    }

    public static void ResetOnSaveExit()
    {
        WasFaintingPending = false;
        FaintedToday = false;
    }
}