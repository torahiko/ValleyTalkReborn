using System;
using System.Collections.Generic;
using System.Globalization;
using StardewModdingAPI;
using StardewValley;

namespace ValleytalkReborn;

/// <summary>
/// 原生游戏数据挖掘器：从当前生效的 NPC 数据资产（婚配标志 / 送礼口味 / 对话切片）抽取可引用的「锚点」。
/// 全部方法仅主线程调用（Game1.content / ItemRegistry 访问），仅在存档加载后由向导按钮触发一次，非热路径。
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
    /// 口味资产字段语义：fields[3] = 专属最爱，fields[1] = 普世最爱；fields[3] 缺失时回退 fields[1] 并强制过滤通货。
    /// 负数 ID 为物品类别（v1 不做类别翻译，直接跳过），正数 ID 通过 ItemRegistry.GetData 解析 DisplayName，未命中跳过。
    /// </summary>
    public static List<string> GetSpecificLoves(string npcName, int maxCount = 3)
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

        // 口味资产条目以 '/' 分段：0=loveUniversal? 语义视版本，取 fields[3] 为专属最爱
        var fields = entry.Split('/');
        string specificField = fields.Length > 3 ? fields[3].Trim() : string.Empty;

        // 专属最爱缺失时回退普世最爱（fields[1]），强制过滤通货以保留性格意象
        if (string.IsNullOrEmpty(specificField))
        {
            if (fields.Length > 1)
                specificField = fields[1].Trim();
        }

        if (string.IsNullOrEmpty(specificField))
            return result;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var token in specificField.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (result.Count >= maxCount)
                break;

            if (!int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out int id))
                continue;

            // 负数 ID = 物品类别（Category）；v1 不做类别翻译，跳过（注释占位：后续版本可扩展）
            if (id < 0)
                continue;

            // 通货黑名单无条件生效：无论专属最爱还是普世回退，均不得混入通货（契约原文语义）
            if (UniversalBlacklist.Contains(id))
                continue;

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

    /// <summary>原版对白切片 —— 直接复用 DialogueScraper 已有的[1,5]钳制 / 吞错不抛契约。</summary>
    public static List<string> GetSampleDialogues(string npcName, int count = 3)
    {
        return DialogueScraper.FetchCleanDialogueExamples(npcName, count);
    }

    /// <summary>
    /// 组装「原生游戏数据锚点」结构化纯文本。
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

        var lines = new List<string>();

        lines.Add("【原生游戏数据锚点（当前生效数据，供参考与隐喻取材）】");

        // 可婚状态
        lines.Add($"- 可婚状态：{(IsDatable(npcName) ? "可婚" : "不可婚")}");

        // 专属最爱物品（性格意象参考）
        var loves = GetSpecificLoves(npcName, 3);
        if (loves.Count > 0)
        {
            lines.Add($"- 专属最爱物品（性格意象参考）：{string.Join("、", loves)}");
        }

        // 原版对白切片（语气参考），每条截断至 120 字符
        var dialogues = GetSampleDialogues(npcName, 3);
        if (dialogues.Count > 0)
        {
            lines.Add("- 原版对白切片（语气参考）：");
            for (int i = 0; i < dialogues.Count; i++)
            {
                string d = dialogues[i];
                if (d.Length > 120)
                    d = d.Substring(0, 120) + "…";
                lines.Add($"  {i + 1}. \"{d}\"");
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
        ModEntry.SMonitor?.Log($"[GameDataScraper] BuildContextSummary({npcName}) 输出：\n{summary}", LogLevel.Debug);

        return summary;
    }
}
