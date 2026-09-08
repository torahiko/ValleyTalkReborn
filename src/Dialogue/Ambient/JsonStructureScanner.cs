using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;

namespace ValleytalkReborn;

/// <summary>
/// 真正的 JSON 结构扫描器：在任意杂乱文本中定位一段"语法闭合 且 能
/// 提取出目标台词数组"的 JSON 片段，取代 IndexOf('[') / LastIndexOf(']')
/// 这种"找孤立字符"的脆弱做法。
///
/// 设计要点：
/// 1. 同时以 [ 和 { 作为候选起点扫描（模型可能把台词包在数组里，
///    也可能包在带 key 的对象里，比如 {"barks": [...]}），不再只认
///    顶层 string[]，否则遇到对象包装就会直接放弃、退化到脆弱的
///    正则/按行降级。
/// 2. 括号深度匹配时正确跳过字符串字面量内部的引号、括号和转义符，
///    避免台词内容里出现的方括号/花括号被误判为结构边界。
/// 3. 找到"语法闭合"的候选片段后，用 JToken 递归下钻寻找可用的
///    字符串数组：优先匹配语义暗示"这是台词"的字段名
///    （bark/barks/line/lines/json/text/content/dialogue），
///    避免对象里恰好还有别的字符串数组（心情标签、分类枚举等）
///    先一步被命中而静默返回错误内容。全对象都没有语义匹配字段时，
///    才退化为"任意一个全字符串数组"。
///    候选片段验证失败（既不是目标数组，也不含可提取的子数组）就
///    不采信，从下一个候选起点继续扫描，而不是拿第一个"能闭合"的
///    片段就返回——"括号能配对"和"内容是我要的数据"是两件事。
///
/// 这样无论模型把 JSON 包在什么语言的寒暄语、伪代码、markdown、
/// 还是嵌套对象里，只要 JSON 本体没坏，都能被正确捞出来。
/// </summary>
internal static class JsonStructureScanner
{
    /// <summary>
    /// 语义上暗示"这是台词/对话内容"的字段名，按优先级从高到低。
    /// 递归下钻时优先按这个顺序匹配，避免命中无关的字符串数组。
    /// </summary>
    private static readonly string[] PreferredArrayKeys =
    {
        "bark", "barks", "line", "lines", "json", "text", "content", "dialogue"
    };

    private static readonly string[] PreferredObjectFieldKeys =
    {
        "bark", "line", "text", "content"
    };

    /// <summary>
    /// 在 raw 中查找第一段可提取出目标字符串数组（至少 minCount 条）的 JSON 片段。
    /// 找不到则返回 null，交给调用方走后续降级（正则提取、按行提取等）。
    /// </summary>
    internal static string[] ExtractFirstValidStringArray(string raw, int minCount = 1)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        int i = 0;
        while (i < raw.Length)
        {
            int bracketIdx = raw.IndexOf('[', i);
            int braceIdx = raw.IndexOf('{', i);

            int openIdx;
            char openChar, closeChar;

            if (bracketIdx < 0 && braceIdx < 0) break;

            if (bracketIdx >= 0 && (braceIdx < 0 || bracketIdx < braceIdx))
            {
                openIdx = bracketIdx;
                openChar = '[';
                closeChar = ']';
            }
            else
            {
                openIdx = braceIdx;
                openChar = '{';
                closeChar = '}';
            }

            int closeIdx = FindMatchingClose(raw, openIdx, openChar, closeChar);
            if (closeIdx > openIdx)
            {
                string candidate = raw.Substring(openIdx, closeIdx - openIdx + 1);

                try
                {
                    var token = JToken.Parse(candidate);
                    var harvested = HarvestPreferredStringArray(token);
                    if (harvested != null && harvested.Count >= minCount)
                        return harvested.ToArray();
                }
                catch
                {
                    // 候选片段闭合但不是合法 JSON，或解析出的结构里
                    // 提取不出满足条数要求的字符串数组：不采信，
                    // 从这个起点之后继续找下一个候选
                }
            }

            i = openIdx + 1;
        }

        return null;
    }

    /// <summary>
    /// 递归下钻寻找字符串数组，按字段名语义优先级选择：
    /// 1. 若 token 本身是数组：全字符串则直接用；元素是对象则按
    ///    PreferredObjectFieldKeys 优先取值，取不到再退化为第一个属性值。
    /// 2. 若 token 是对象：按 PreferredArrayKeys 顺序优先查找匹配字段名
    ///    对应的值再递归；找不到任何语义匹配字段时，才退化为遍历全部
    ///    属性、返回第一个能收获到数组的分支（原始顺序，不再语义排序）。
    /// 支持 ["a","b"]、{"barks":["a","b"]}、{"json":["a","b"]}、
    /// [{"line":"a"}] 等常见变体。
    /// </summary>
    private static List<string> HarvestPreferredStringArray(JToken token)
    {
        if (token == null) return null;

        if (token is JArray arr)
            return HarvestFromArray(arr);

        if (token is JObject obj)
        {
            // 优先按语义字段名匹配
            foreach (var key in PreferredArrayKeys)
            {
                var prop = obj.Property(key, StringComparison.OrdinalIgnoreCase);
                if (prop == null) continue;

                var found = HarvestPreferredStringArray(prop.Value);
                if (found != null && found.Count > 0)
                    return found;
            }

            // 没有任何语义匹配字段：退化为按原始顺序遍历所有属性
            foreach (var prop in obj.Properties())
            {
                var found = HarvestPreferredStringArray(prop.Value);
                if (found != null && found.Count > 0)
                    return found;
            }
        }

        return null;
    }

    private static List<string> HarvestFromArray(JArray arr)
    {
        if (arr.Count == 0) return null;

        if (arr.All(x => x.Type == JTokenType.String))
        {
            return arr.Select(x => x.ToString().Trim())
                      .Where(s => !string.IsNullOrWhiteSpace(s))
                      .ToList();
        }

        // 对象数组：按字段名优先级取值
        var objList = new List<string>();
        foreach (var item in arr)
        {
            if (item is JObject obj)
            {
                JToken val = null;
                foreach (var key in PreferredObjectFieldKeys)
                {
                    val = obj.Property(key, StringComparison.OrdinalIgnoreCase)?.Value;
                    if (val != null) break;
                }
                val ??= obj.Properties().FirstOrDefault()?.Value;

                if (val != null && val.Type == JTokenType.String)
                {
                    string s = val.ToString().Trim();
                    if (!string.IsNullOrWhiteSpace(s))
                        objList.Add(s);
                }
            }
        }

        return objList.Count > 0 ? objList : null;
    }

    /// <summary>
    /// 只做结构定位、不做语义校验的版本，供调用方需要拿到原始候选片段
    /// （而非已经提取好的字符串数组）时复用同一套安全括号扫描逻辑。
    /// </summary>
    internal static string ExtractFirstClosedBracketSpan(string raw, char openChar, char closeChar)
    {
        if (string.IsNullOrEmpty(raw)) return null;

        int i = 0;
        while (i < raw.Length)
        {
            int openIdx = raw.IndexOf(openChar, i);
            if (openIdx < 0) return null;

            int closeIdx = FindMatchingClose(raw, openIdx, openChar, closeChar);
            if (closeIdx > openIdx)
                return raw.Substring(openIdx, closeIdx - openIdx + 1);

            i = openIdx + 1;
        }

        return null;
    }

    /// <summary>
    /// 从 startIndex（必须是 openChar）开始做括号深度匹配，返回配对的
    /// closeChar 位置；找不到配对则返回 -1。
    /// 正确处理双引号字符串字面量：遇到反斜杠直接跳过下一个字符
    /// （无论那是被转义的引号、反斜杠本身还是任意字符），比维护一个
    /// 跨迭代传递的 escape 状态变量更不容易出错，尤其是在连续反斜杠
    /// （如 Windows 路径 "C:\\Users\\x"）等边界场景下更直观可靠。
    /// </summary>
    private static int FindMatchingClose(string s, int startIndex, char openChar, char closeChar)
    {
        int depth = 0;
        bool inString = false;
        int i = startIndex;

        while (i < s.Length)
        {
            char c = s[i];

            if (inString)
            {
                if (c == '\\')
                {
                    i += 2; // 跳过转义符和被转义的那一个字符
                    continue;
                }
                if (c == '"')
                {
                    inString = false;
                }
                i++;
                continue;
            }

            if (c == '"')
            {
                inString = true;
                i++;
                continue;
            }

            if (c == openChar) depth++;
            else if (c == closeChar)
            {
                depth--;
                if (depth == 0) return i;
            }

            i++;
        }

        return -1; // 未闭合
    }
}
