using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using StardewModdingAPI;
using StardewValley;

namespace ValleytalkReborn;

/// <summary>
/// 原版对白抓取器：从当前生效的「Characters/Dialogue/{npc}」资产中抽取干净的对话范例。
/// 「原版」取「当前生效值」语义 —— Game1.content.Load 返回的是打过 CP 补丁的对话数据。
/// 零依赖、无实例状态；仅在存档加载后由编辑器 Tab5 按钮调用。
/// </summary>
internal static class DialogueScraper
{
    /// <summary>匹配形如 $q / $y / %fork 等原版应答/分支指令符。</summary>
    private static readonly Regex CommandTagRegex = new(@"\$[a-zA-Z][a-zA-Z0-9_]*|%[a-zA-Z0-9_]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>抓取指定 NPC 的干净对话范例，返回 1~count 条纯文本（失败/无结果返回空表，不抛异常）。</summary>
    /// <param name="npcName">NPC 内部名。</param>
    /// <param name="count">需要的范例数，自动钳制到 [1,5]。</param>
    public static List<string> FetchCleanDialogueExamples(string npcName, int count = 3)
    {
        var result = new List<string>();

        count = Math.Clamp(count, 1, 5);

        var npc = Game1.getCharacterFromName(npcName);
        if (npc == null)
            return result;

        Dictionary<string, string> dialogueData;
        try
        {
            dialogueData = Game1.content.Load<Dictionary<string, string>>($"Characters/Dialogue/{npcName}");
        }
        catch (Exception ex)
        {
            // NPC 无对话资产时引擎抛 ContentLoadException，在此主动吞掉，禁止冒泡到菜单点击处理
            ModEntry.SMonitor?.Log($"[Scraper] 对话资产加载失败({npcName}): {ex.Message}", LogLevel.Warn);
            return result;
        }

        if (dialogueData == null)
            return result;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var raw in dialogueData.Values)
        {
            if (result.Count >= count)
                break;

            if (string.IsNullOrWhiteSpace(raw))
                continue;

            // 跳过问题/快问块
            if (raw.Contains("$q ") || raw.Contains("$y "))
                continue;

            // 剥离应答/分支指令符
            string cleaned = CommandTagRegex.Replace(raw, " ");

            // 性别分支：取 '^' 前的男性分支文本
            cleaned = cleaned.Split('^')[0];

            // 换行符转空格
            cleaned = cleaned.Replace('#', ' ');

            // 折叠空白
            cleaned = Regex.Replace(cleaned, @"\s+", " ").Trim();

            // 长度过滤
            if (cleaned.Length < 10 || cleaned.Length > 300)
                continue;

            // 去重
            if (!seen.Add(cleaned))
                continue;

            result.Add(cleaned);
        }

        return result;
    }
}
