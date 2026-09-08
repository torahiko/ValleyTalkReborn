using System.Text.RegularExpressions;

namespace ValleytalkReborn
{
    internal static class EavesdropTextCleaner
    {
        // 🌟 修复 #1: 原 [ACTION:[^]]] 是字符类，匹配单个字母
        // 正确：匹配 [ACTION:EMOTE:HAPPY]、[ACTION:STEP:FORWARD] 等完整标签
        private static readonly Regex ActionTag =
            new Regex(@"\[ACTION:[^\]]*\]", RegexOptions.Compiled);

        // 🌟 修复 #2: 原 ([^)]) 匹配任何非 ) 的单个字符，会删除几乎所有文本
        // 正确：匹配 (脸红)、(叹气)、(小声) 等括号舞台指示
        private static readonly Regex Parenthetical =
            new Regex(@"\([^)]*\)", RegexOptions.Compiled);

        // 🌟 修复 #3: 原 $[a-zA-Z]\d* 中 $ 是行尾锚点，永远不匹配
        // 正确：匹配 $h、$s、$l、$a、$h2 等星露谷肖像标签
        private static readonly Regex PortraitTag =
            new Regex(@"\$[a-zA-Z]\d*", RegexOptions.Compiled);

        // 🌟 修复 #4: 原 [\d+] 是字符类，匹配单个数字或 + 号
        // 正确：匹配 [123]、[206] 等星露谷分支对话 ID
        private static readonly Regex ItemIdBracket =
            new Regex(@"\[\d+\]", RegexOptions.Compiled);

        // 🌟 新增：匹配 #$b#、#$e#、#$q#、#$r# 等星露谷页面/流程控制符
        private static readonly Regex PageControlTag =
            new Regex(@"#\$\w+#", RegexOptions.Compiled);

        // 🌟 新增：匹配 skip# 前缀标记
        private static readonly Regex SkipPrefix =
            new Regex(@"^skip#", RegexOptions.Compiled);

        /// <summary>
        /// 将 NPC 对话文本清理为纯语义文本，用于窃听广播注入其他 NPC 的历史记录。
        /// 移除所有游戏控制符、动作标签、肖像标签和分支 ID，只保留可读的自然语言。
        /// </summary>
        public static string Clean(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return text;
                       
            // 1. 移除 skip# 前缀
            text = SkipPrefix.Replace(text, "");

            // 2. 移除 [ACTION:...] 动作标签
            text = ActionTag.Replace(text, "");

            // 3. 移除 (括号舞台指示)
            text = Parenthetical.Replace(text, "");

            // 4. 移除 $h、$s 等肖像标签
            text = PortraitTag.Replace(text, "");

            // 5. 移除 [123] 等分支对话 ID
            text = ItemIdBracket.Replace(text, "");

            // 6. 移除 #$b#、#$e#、#$q# 等页面控制符，替换为空格保持语句连贯
            text = PageControlTag.Replace(text, " ");

            // 7. 清理残留的单独 # 号（如 #$q 12345# 被部分移除后残留的数字）
            text = Regex.Replace(text, @"#\d+#", " ");

            // 8. 压缩多余空格
            text = Regex.Replace(text, @" {2,}", " ");

            return text.Trim();
        }
    }
}