using System;
using System.Collections.Generic;
using System.Linq;
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
    /// <summary>匹配形如 $h / $0 / $q / $s / $1 / %fork 等原版应答/分支/情绪/性别宏。</summary>
    private static readonly Regex CommandTagRegex = new(@"\$[a-zA-Z0-9_]+|%[a-zA-Z0-9_]+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// 抓取指定 NPC 的干净对话范例（四维情境分层抽取，带情境标签前缀）。
    /// 失败/无结果返回空表，不抛异常。
    /// </summary>
    /// <param name="npcName">NPC 内部名。</param>
    /// <param name="maxCount">需要的范例数，自动钳制到 [1,6]。</param>
    public static List<string> FetchContextualDialogueExamples(string npcName, int maxCount = 4)
    {
        var result = new List<string>();

        maxCount = Math.Clamp(maxCount, 1, 6);

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

        if (dialogueData == null || dialogueData.Count == 0)
            return result;

        string playerName = !string.IsNullOrWhiteSpace(Game1.player?.Name) ? Game1.player.Name : "农夫";
        string farmName = (Game1.player?.farmName?.Value ?? "星露谷") + "农场";

        string introLine = string.Empty;
        string dailyLine = string.Empty;
        string deepLine = string.Empty;
        string specialLine = string.Empty;

        var usedTexts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (key, rawText) in dialogueData)
        {
            string cleaned = CleanDialogue(rawText, playerName, farmName);
            if (string.IsNullOrWhiteSpace(cleaned))
                continue;

            string lowerKey = key.ToLowerInvariant();

            // 1. 初见与第一印象
            if (string.IsNullOrEmpty(introLine) && (lowerKey.Contains("intro") || lowerKey.Contains("first")))
            {
                introLine = $"[初见与初识印象] \"{cleaned}\"";
                usedTexts.Add(cleaned);
                continue;
            }

            // 2. 深层心声 / 高好感态度（高好感度阶梯 / 已婚 / 承诺）
            if (string.IsNullOrEmpty(deepLine) && (lowerKey.Contains("10") || lowerKey.Contains("8")
                || lowerKey.Contains("fourhearts") || lowerKey.Contains("twohearts")
                || lowerKey.Contains("marriage") || lowerKey.Contains("love")))
            {
                deepLine = $"[深层心声与好感流露] \"{cleaned}\"";
                usedTexts.Add(cleaned);
                continue;
            }

            // 3. 特殊情境与节日（节日 / 特殊天气 / 送礼反馈 / 舞蹈）
            if (string.IsNullOrEmpty(specialLine) && (lowerKey.Contains("rain") || lowerKey.Contains("event")
                || lowerKey.Contains("gift") || lowerKey.Contains("festival") || lowerKey.Contains("dance")))
            {
                specialLine = $"[特定情境与性情反馈] \"{cleaned}\"";
                usedTexts.Add(cleaned);
                continue;
            }

            // 4. 常规日常生活（以星期名为 key 前缀）
            if (string.IsNullOrEmpty(dailyLine) && (lowerKey.StartsWith("mon") || lowerKey.StartsWith("tue")
                || lowerKey.StartsWith("wed") || lowerKey.StartsWith("thu") || lowerKey.StartsWith("fri")
                || lowerKey.StartsWith("sat") || lowerKey.StartsWith("sun")))
            {
                dailyLine = $"[日常闲谈与生活节奏] \"{cleaned}\"";
                usedTexts.Add(cleaned);
                continue;
            }
        }

        if (!string.IsNullOrEmpty(introLine)) result.Add(introLine);
        if (!string.IsNullOrEmpty(dailyLine)) result.Add(dailyLine);
        if (!string.IsNullOrEmpty(deepLine)) result.Add(deepLine);
        if (!string.IsNullOrEmpty(specialLine)) result.Add(specialLine);

        // 兜底：情境分类未凑满时，从剩余对白中补充（用纯文本去重，避免与已抽台词雷同）
        if (result.Count < maxCount)
        {
            foreach (var (key, rawText) in dialogueData)
            {
                if (result.Count >= maxCount)
                    break;

                string cleaned = CleanDialogue(rawText, playerName, farmName);
                if (string.IsNullOrWhiteSpace(cleaned))
                    continue;

                // 按纯文本去重：四维分层已抽中的台词不再重复进入
                if (!usedTexts.Add(cleaned))
                    continue;

                string formatted = $"[原版日常对白] \"{cleaned}\"";
                result.Add(formatted);
            }
        }

        return result;
    }

    /// <summary>向后兼容保留：原 FetchCleanDialogueExamples 依然可用（返回剥离标签的纯文本，最多 count 条）。</summary>
    public static List<string> FetchCleanDialogueExamples(string npcName, int count = 3)
    {
        var contextual = FetchContextualDialogueExamples(npcName, count);
        var cleanList = new List<string>();
        foreach (var c in contextual)
        {
            int quoteIdx = c.IndexOf('"');
            if (quoteIdx >= 0 && c.EndsWith('"'))
                cleanList.Add(c.Substring(quoteIdx + 1, c.Length - quoteIdx - 2));
            else
                cleanList.Add(c);
        }
        return cleanList;
    }

    /// <summary>
    /// 清洗单条原版对白：剥离宏 / 性别分支 / 清洗占位符 / 折叠空白 / 长度过滤。
    /// 返回空串表示该条应被丢弃（含 $q/$y 问题块 / 过长过短 / 清洗后为空）。
    /// </summary>
    private static string CleanDialogue(string raw, string playerName, string farmName)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return string.Empty;

        // 跳过问题/快问块（$q=快答 / $y=二选一）
        if (raw.Contains("$q ") || raw.Contains("$y "))
            return string.Empty;

        // 占位符替换：@ = 玩家名，%farm = 农场名
        string text = raw.Replace("@", playerName).Replace("%farm", farmName);

        // 剥离 $h / $0 / $q / %fork 等引擎指令与情绪宏
        text = CommandTagRegex.Replace(text, " ");

        // 性别分支：取 '^' 前的男性分支文本
        text = text.Split('^')[0];

        // 换行符转空格
        text = text.Replace('#', ' ');

        // 折叠空白
        text = Regex.Replace(text, @"\s+", " ").Trim();

        // 长度过滤（过短无信息量 / 过长含大段叙述）
        if (text.Length < 6 || text.Length > 150)
            return string.Empty;

        return text;
    }
}
