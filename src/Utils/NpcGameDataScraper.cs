using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using StardewModdingAPI;
using StardewValley;

namespace ValleytalkReborn;

/// <summary>
/// 原生游戏数据挖掘器：从当前生效的 NPC 数据资产（身份档案 / 性格三维 / 亲属网络 / 送礼口味 / 对话切片）抽取可引用的「锚点」。
/// 全部方法仅主线程调用（Game1.content / ItemRegistry / Game1.getCharacterFromName 访问），仅在存档加载后由向导按钮触发一次，非热路径。
/// 零写入、吞错不抛 —— 缺失子项在 BuildContextSummary 中自动省略，全空返回空串让 Prompt 走「自由创作」分支。
/// </summary>
internal static class NpcGameDataScraper
{
    /// <summary>通货黑名单：五彩碎片 / 珍珠 / 兔子脚 / 魔法糖冰棍 / 黄金南瓜 —— 属普世最爱，不具性格意象。</summary>
    private static readonly HashSet<int> UniversalBlacklist = new()
    {
        74,   // Prismatic Shard / 五彩碎片
        797,  // Pearl / 珍珠
        446,  // Rabbit's Foot / 兔子脚
        373,  // Magic Rock Candy / 魔法糖冰棍
        279,  // Golden Pumpkin / 黄金南瓜
    };

    /// <summary>判定 NPC 是否可婚（Game1.getCharacterFromName?.datable.Value ?? false）。</summary>
    public static bool IsDatable(string npcName)
    {
        if (string.IsNullOrEmpty(npcName))
            return false;

        return Game1.getCharacterFromName(npcName)?.datable.Value ?? false;
    }

    /// <summary>
    /// 抽取该 NPC 的专属最爱物品 DisplayName 列表（最多 maxCount 个）。
    /// 口味资产字段语义：fields[1] = 专属最爱（Loves），fields[0] = 普世最爱（UniversalLoves）。
    /// 负数 ID 为物品类别（v1 不做类别翻译，直接跳过），正数 ID 通过 ItemRegistry.GetData 解析 DisplayName，未命中跳过。
    /// 缺失时回退普世最爱（fields[0]）并强制过滤通货。
    /// </summary>
    public static List<string> GetSpecificLoves(string npcName, int maxCount = 5)
    {
        return ExtractGiftTastes(npcName, fieldIndex: 1, maxCount, fallbackIndex: 0, forceBlacklist: false);
    }

    /// <summary>
    /// 抽取该 NPC 的极度讨厌物品 DisplayName 列表（最多 maxCount 个），用作性格防御机制锚点。
    /// 口味资产字段语义：fields[7] = 极度厌恶（Hates）。
    /// </summary>
    public static List<string> GetSpecificHates(string npcName, int maxCount = 3)
    {
        return ExtractGiftTastes(npcName, fieldIndex: 7, maxCount, fallbackIndex: -1, forceBlacklist: false);
    }

    /// <summary>原版对白切片 —— 直接复用 DialogueScraper 已有的四维情境分层抽取与吞错不抛契约。</summary>
    public static List<string> GetSampleDialogues(string npcName, int count = 4)
    {
        return DialogueScraper.FetchContextualDialogueExamples(npcName, count);
    }

    /// <summary>
    /// 组装「原生游戏数据锚点」结构化纯文本（身份档案 / 性格三维 / 亲属网络 / 送礼口味 / 对白切片）。
    /// 容错：任一子项缺失则该行省略；全空返回空串（调用方 Prompt 对空锚点走「自由创作」分支）。
    /// 组装完成后以 Debug 日志输出全文（验收硬证据）。
    /// </summary>
    public static string BuildContextSummary(string npcName)
    {
        if (string.IsNullOrEmpty(npcName))
        {
            ModEntry.SMonitor?.Log("[GameDataScraper] BuildContextSummary: npcName 为空，返回空串", LogLevel.Debug);
            return string.Empty;
        }

        var npc = Game1.getCharacterFromName(npcName);
        var lines = new List<string>();

        lines.Add("【原生游戏数据锚点（当前生效数据，供角色背景与性格取材）】");

        // 1. 基本身份档案
        string dispName = npc?.displayName ?? npcName;
        string gender = npc?.Gender switch
        {
            Gender.Male => "男性",
            Gender.Female => "女性",
            _ => "未指定"
        };
        string age = npc?.Age switch
        {
            0 => "成年",
            1 => "青年/青少年",
            2 => "儿童",
            _ => "未知"
        };
        string datable = (npc?.datable?.Value ?? false) ? "可婚单身" : "不可婚/已定角色";
        lines.Add($"- 角色基础：{dispName} ({npcName}) | {gender} | {age} | {datable}");

        // 2. 生日与住址
        if (npc != null)
        {
            string birthday = !string.IsNullOrEmpty(npc.Birthday_Season)
                ? $"{TranslateSeason(npc.Birthday_Season)}季第 {npc.Birthday_Day} 日"
                : "未知";
            string home = !string.IsNullOrEmpty(npc.DefaultMap) ? npc.DefaultMap : "星露谷镇上";
            lines.Add($"- 生日与住址：{birthday} | 常驻：{home}");
        }

        // 3. 原生性格三维
        if (npc != null)
        {
            string social = npc.SocialAnxiety switch
            {
                0 => "外向活跃 (主动社交/不怯场)",
                2 => "内向慢热 (回避社交/心防深)",
                _ => "随和适度 (常规社交)"
            };
            string optimism = npc.Optimism switch
            {
                0 => "积极乐观 (自信/充满朝气)",
                2 => "消极低落 (容易悲观/心事重)",
                _ => "务实平和"
            };
            string manners = npc.Manners switch
            {
                1 => "文雅守礼 (注重修养/措辞委婉)",
                2 => "直率不羁 (粗线条/言辞犀利)",
                _ => "随性自然"
            };
            lines.Add($"- 原生性格维度：{social} | {optimism} | {manners}");
        }

        // 4. 社交网络与亲属（兼容 1.6 CharacterData；缺失或异常则整段省略）
        if (npc != null)
        {
            try
            {
                var charData = npc.GetData();
                if (charData?.FriendsAndFamily != null && charData.FriendsAndFamily.Count > 0)
                {
                    var relationStrs = new List<string>();
                    foreach (var (targetName, relKey) in charData.FriendsAndFamily)
                    {
                        string targetDisp = Game1.getCharacterFromName(targetName)?.displayName ?? targetName;
                        string relDesc = TranslateRelation(relKey);
                        relationStrs.Add($"{targetDisp} ({relDesc})");
                    }
                    if (relationStrs.Count > 0)
                        lines.Add($"- 亲属与社交羁绊：{string.Join("、", relationStrs)}");
                }
            }
            catch (Exception ex)
            {
                ModEntry.SMonitor?.Log($"[GameDataScraper] FriendsAndFamily 读取异常({npcName}): {ex.Message}", LogLevel.Debug);
            }
        }

        // 5. 性格意象物品（最爱与讨厌）
        var loves = GetSpecificLoves(npcName, 5);
        var hates = GetSpecificHates(npcName, 3);
        if (loves.Count > 0 || hates.Count > 0)
        {
            lines.Add("- 性格意象物品（送礼偏好反映生活态度）：");
            if (loves.Count > 0)
                lines.Add($"  * 极其珍视（追求与执念）：{string.Join("、", loves)}");
            if (hates.Count > 0)
                lines.Add($"  * 极度反感（心理防御与雷区）：{string.Join("、", hates)}");
        }

        // 6. 原版对白流变切片（带场景标签）
        var dialogues = GetSampleDialogues(npcName, 4);
        if (dialogues.Count > 0)
        {
            lines.Add("- 原版对白流变切片（语气与心理流变参考）：");
            foreach (var d in dialogues)
            {
                lines.Add($"  * {d}");
            }
        }

        // 全空保护：除标题外无任何子项则返回空串
        if (lines.Count <= 1)
        {
            ModEntry.SMonitor?.Log($"[GameDataScraper] BuildContextSummary({npcName}): 所有子项均为空，返回空串", LogLevel.Debug);
            return string.Empty;
        }

        string summary = string.Join(Environment.NewLine, lines);

        // 硬证据：Debug 日志输出全文
        ModEntry.SMonitor?.Log($"[GameDataScraper] BuildContextSummary({npcName}) 深度抽取输出：\n{summary}", LogLevel.Debug);

        return summary;
    }

    /// <summary>
    /// 从 Data/NPCGiftTastes 抽取指定字段索引的物品 DisplayName。
    /// fieldIndex 缺失且 fallbackIndex >= 0 时回退；回退时可选强制过滤通货。
    /// </summary>
    private static List<string> ExtractGiftTastes(string npcName, int fieldIndex, int maxCount, int fallbackIndex, bool forceBlacklist)
    {
        var result = new List<string>();

        if (string.IsNullOrEmpty(npcName) || maxCount <= 0)
            return result;

        Dictionary<string, string> tastes;
        try
        {
            tastes = Game1.content.Load<Dictionary<string, string>>("Data\\NPCGiftTastes");
        }
        catch (Exception ex)
        {
            // 口味资产加载异常（极少见）：吞掉，对齐 DialogueScraper 吞错纪律，Warn 日志
            ModEntry.SMonitor?.Log($"[GameDataScraper] 口味资产加载失败({npcName}): {ex.Message}", LogLevel.Warn);
            return result;
        }

        if (tastes == null)
            return result;

        if (!tastes.TryGetValue(npcName, out var entry) || string.IsNullOrEmpty(entry))
            return result;

        var fields = entry.Split('/');
        string specificField = fields.Length > fieldIndex ? fields[fieldIndex].Trim() : string.Empty;

        // 专属字段缺失时回退普世字段（仅对 >= 0 的 fallbackIndex 生效）
        if (string.IsNullOrEmpty(specificField) && fallbackIndex >= 0 && fields.Length > fallbackIndex)
            specificField = fields[fallbackIndex].Trim();

        if (string.IsNullOrEmpty(specificField))
            return result;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var token in specificField.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (result.Count >= maxCount)
                break;

            // 负数 ID = 物品类别（Category）；v1 不做类别翻译，跳过
            if (int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out int id))
            {
                if (id < 0)
                    continue;

                // 通货黑名单：专属最爱回退普世时强制生效；直接命中专属时亦生效（避免 Mod 数据污染）
                if (UniversalBlacklist.Contains(id))
                    continue;
            }

            // ItemRegistry.GetData 在本 codebase 接受 QualifiedItemId（string），此处传原始 ID token 保持格式
            var data = ItemRegistry.GetData(token);
            // 未命中（ErrorItem 或 null）跳过 —— 对齐「未命中不抛异常」纪律
            if (data == null || data.IsErrorItem)
                continue;

            string displayName = data.DisplayName;
            if (string.IsNullOrWhiteSpace(displayName))
                continue;

            if (!seen.Add(displayName))
                continue;

            result.Add(displayName);
        }

        return result;
    }

    private static string TranslateSeason(string season) => season?.ToLowerInvariant() switch
    {
        "spring" => "春",
        "summer" => "夏",
        "fall" => "秋",
        "winter" => "冬",
        _ => season ?? ""
    };

    private static string TranslateRelation(string rel) => rel?.ToLowerInvariant() switch
    {
        "grandfather" => "祖父",
        "grandmother" => "祖母",
        "father" => "父亲",
        "mother" => "母亲",
        "sister" => "姐妹",
        "brother" => "兄弟",
        "friend" => "朋友",
        "rival" => "对手/情敌",
        "uncle" => "叔伯",
        "aunt" => "姑婶",
        _ => rel ?? "熟人"
    };
}
