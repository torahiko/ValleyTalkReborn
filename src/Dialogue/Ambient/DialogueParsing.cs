using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using StardewModdingAPI;
using StardewValley;

namespace ValleytalkReborn;

/// <summary>
/// 对话系统解析逻辑（从 DynamicBarkManager 中拆出的 JSON 解析相关方法）。
/// </summary>
internal static class DialogueParsing
{
    /// <summary>
    /// 从 LLM 原始输出中提取一段可能是 JSON 数组的候选片段（保留对外兼容
    /// 入口，供 A2ASessionManager 等仍按"先提取候选片段，再自行反序列化"
    /// 方式调用的场景使用，例如 A2A 的 speaker/line 对象数组）。
    ///
    /// 内部改为调用 JsonStructureScanner 的安全括号扫描
    /// （ExtractFirstClosedBracketSpan），用真正的括号深度匹配
    /// （正确处理字符串字面量、转义符、嵌套）定位候选片段，
    /// 不再是原来 IndexOf('[') / LastIndexOf(']') 那种"找孤立字符"
    /// 的脆弱做法——旧实现在候选片段前面出现无关方括号
    /// （比如编号 "[1]"、提示语 "[如下]"）时会定位到错误边界。
    ///
    /// 注意：这里只做"结构定位"，不做语义校验（不像 ParseBarkJson
    /// 第一级那样验证内容是不是目标数组），因为调用方接下来会自己
    /// 用 JsonConvert 反序列化成特定结构（如 A2A 的对象数组），
    /// 校验交给调用方的反序列化步骤即可。
    /// 找不到候选片段时返回 null，调用方应回退到用原始文本直接解析。
    /// </summary>
    internal static string ExtractJsonArrayString(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        // 先清理常见的 Markdown 代码块标记，减少干扰
        string cleaned = raw.Replace("```json", "").Replace("```", "").Trim();

        return JsonStructureScanner.ExtractFirstClosedBracketSpan(cleaned, '[', ']');
    }

    /// <summary>
    /// 解析 A2A 脚本 JSON。
    ///
    /// json 参数通常是调用方（A2ASessionManager）已经用
    /// ExtractJsonArrayString 截取过一次的候选片段；这里再用同一套
    /// 安全括号扫描器对该片段（或原始文本，当截取失败时）做一次
    /// 更严格的定位 + 反序列化尝试，理由：
    /// 上层截取时只做"结构定位"没做语义校验，如果候选片段本身闭合
    /// 但不是目标对象数组（比如误截取到无关的方括号文本），直接对
    /// 整段 candidate 反序列化会失败并触发 fallback；这里改为在
    /// candidate 内部再扫描一次"能成功反序列化为对象数组"的片段，
    /// 兜住"候选片段外层包了别的东西"的情况，减少不必要的 fallback。
    /// </summary>
    internal static DialogueModels.A2ALine[] ParseA2AScript(string json, List<NPC> participants)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;

        var raw = TryDeserializeSpeakerLineArray(json);

        if (raw == null)
        {
            ModEntry.SMonitor?.Log(
                $"[A2A] ParseA2AScript 反序列化失败，原始输入：\n{json}",
                LogLevel.Warn);
            return null;
        }

        try
        {
            var lookup = new Dictionary<string, NPC>(StringComparer.OrdinalIgnoreCase);
            foreach (var npc in participants)
            {
                if (npc == null) continue;
                if (!string.IsNullOrEmpty(npc.Name))
                    lookup[npc.Name] = npc;
                if (!string.IsNullOrEmpty(npc.displayName) && npc.displayName != npc.Name)
                    lookup[npc.displayName] = npc;
            }

            var result = new List<DialogueModels.A2ALine>();
            foreach (var entry in raw)
            {
                if (!entry.TryGetValue("speaker", out var speakerName) ||
                    !entry.TryGetValue("line", out var lineText))
                    continue;
                if (string.IsNullOrWhiteSpace(lineText)) continue;

                if (!lookup.TryGetValue(speakerName?.Trim() ?? "", out var speaker))
                {
                    ModEntry.SMonitor?.Log(
                        $"[A2A] 未知 speaker \"{speakerName}\"，跳过",
                        LogLevel.Trace);
                    continue;
                }

                result.Add(new DialogueModels.A2ALine
                {
                    SpeakerName = speaker.Name,
                    Line = lineText.Trim()
                });
            }

            return result.Count > 0 ? result.ToArray() : null;
        }
        catch (Exception ex)
        {
            ModEntry.SMonitor?.Log(
                $"[A2A] ParseA2AScript 异常：{ex.Message}\nRaw JSON:\n{json}",
                LogLevel.Warn);
            return null;
        }
    }

    /// <summary>
    /// 在文本中扫描能成功反序列化为 List&lt;Dictionary&lt;string,string&gt;&gt; 的候选片段。
    /// 复用 JsonStructureScanner 的安全括号定位（正确处理字符串字面量与
    /// 转义），但校验目标类型换成 A2A 需要的"对象数组"而不是 Bark 需要
    /// 的"字符串数组"——两者都遵循同一个原则：闭合但反序列化失败就不
    /// 采信，继续从下一个候选起点扫描，而不是拿第一个闭合片段就返回。
    /// </summary>
    private static List<Dictionary<string, string>> TryDeserializeSpeakerLineArray(string text)
    {
        // 先直接尝试整体反序列化（多数情况下，调用方传入的已经是
        // ExtractJsonArrayString 截取过的干净候选片段）
        var direct = TryDeserialize(text);
        if (direct != null) return direct;

        // 整体反序列化失败：说明候选片段外层可能还包着别的内容，
        // 在其中逐个扫描 [ 起点，找到真正能反序列化成功的那一段
        int i = 0;
        while (i < text.Length)
        {
            int openIdx = text.IndexOf('[', i);
            if (openIdx < 0) return null;

            string span = JsonStructureScanner.ExtractFirstClosedBracketSpan(
                text.Substring(openIdx), '[', ']');

            if (string.IsNullOrEmpty(span))
            {
                i = openIdx + 1;
                continue;
            }

            var parsed = TryDeserialize(span);
            if (parsed != null) return parsed;

            i = openIdx + 1;
        }

        return null;
    }

    private static List<Dictionary<string, string>> TryDeserialize(string candidate)
    {
        try
        {
            var result = JsonConvert.DeserializeObject<List<Dictionary<string, string>>>(candidate);
            return (result != null && result.Count > 0) ? result : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 解析 Bark 输出（多级降级解析）。
    /// 请直接传入 LLM 的原始输出 (rawText)，内部会自动处理提取与降级。
    ///
    /// 第一级是主力：用真正的括号深度扫描（JsonStructureScanner）在整段
    /// 原始文本里定位"语法闭合 且 能提取出台词数组"的片段。同时穿透
    /// [ 和 { 两种包装（纯数组 ["..."] 或带 key 的对象 {"barks":[...]}} ），
    /// 并按字段名语义优先级递归下钻，避免误命中对象里其他无关的字符串数组。
    /// 不管模型把 JSON 包在什么语言的寒暄语、伪代码 import 语句、markdown
    /// 代码块、还是嵌套对象里，只要 JSON 本体没坏，这一级都应该能捞出来。
    ///
    /// 后面几级处理的是"结构扫描器也找不到任何可用 JSON"的更差场景
    /// （输出被截断、JSON 完全损坏等），是第一级失败后的兜底，不是重复劳动。
    /// </summary>
    internal static string[] ParseBarkJson(string rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText)) return null;

        // 第一级：真正的结构扫描 + 语义化提取
        // （对"闭合但非目标结构"的候选会自动跳过，继续找下一个，
        //  而不是像旧版 IndexOf/LastIndexOf 那样一次性定错边界；
        //  同时能穿透对象包装，不再只认顶层 string[]）
        var scanned = JsonStructureScanner.ExtractFirstValidStringArray(rawText, minCount: 1);
        if (scanned != null && scanned.Length > 0)
        {
            ModEntry.SMonitor?.Log(
                $"[DialogueParsing] 第一级结构扫描成功，共 {scanned.Length} 条",
                LogLevel.Debug);
            return scanned;
        }

        // 第二级：对象数组降级（兼容 {line/text/首个值} 格式）
        // 用同一套安全括号扫描定位候选片段，而不是全文本硬解析
        // （模型输出对象数组包裹在杂乱前后缀里时也能兼容）
        try
        {
            string objCandidate =
                JsonStructureScanner.ExtractFirstClosedBracketSpan(rawText, '[', ']')
                ?? rawText;

            var objs = JsonConvert.DeserializeObject<List<Dictionary<string, string>>>(objCandidate);
            if (objs != null && objs.Count > 0)
            {
                var lines = objs
                    .Select(o => o.TryGetValue("line", out var l) ? l :
                        o.TryGetValue("text", out var t) ? t :
                        o.Values.FirstOrDefault())
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .ToArray();
                if (lines.Length > 0)
                {
                    ModEntry.SMonitor?.Log(
                        $"[DialogueParsing] 第二级对象数组降级成功，共 {lines.Length} 条",
                        LogLevel.Debug);
                    return lines;
                }
            }
        }
        catch { }

        // 第三级：结构扫描器都找不到闭合片段时（比如输出被截断，缺了结尾的 ]），
        // 尝试补全后再解析——这属于"JSON 本体语法轻微损坏"的场景，
        // 和第一级"从杂乱文本中定位正确边界"是不同的失败模式
        try
        {
            string repaired = rawText.Trim();
            int bracketStart = repaired.IndexOf('[');
            if (bracketStart > 0) repaired = repaired.Substring(bracketStart);
            if (!repaired.StartsWith("[")) repaired = "[" + repaired;
            if (!repaired.EndsWith("]")) repaired = repaired + "]";

            var repairResult = JsonConvert.DeserializeObject<string[]>(repaired);
            if (repairResult != null && repairResult.Length > 0)
            {
                ModEntry.SMonitor?.Log(
                    $"[DialogueParsing] 第三级补全修复成功，共 {repairResult.Length} 条",
                    LogLevel.Debug);
                return repairResult;
            }
        }
        catch { }

        // 第四级：正则暴力提取所有双引号字符串
        // 这是"JSON 结构完全不可用"时的保底手段，不区分语言，
        // 只要文本里散布着双引号包裹的台词就能捞出来
        try
        {
            var matches = System.Text.RegularExpressions.Regex.Matches(
                rawText, @"""((?:[^""\\]|\\.)*)""");
            var knownKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "line", "text", "bark", "speaker", "name", "dialogue", "content" };
            var extracted = matches
                .Cast<System.Text.RegularExpressions.Match>()
                .Select(m => m.Groups[1].Value.Trim())
                .Where(s => !string.IsNullOrWhiteSpace(s) && !knownKeys.Contains(s))
                .Distinct()
                .ToArray();

            if (extracted.Length >= 3)
            {
                ModEntry.SMonitor?.Log(
                    $"[DialogueParsing] 第四级正则提取成功，共 {extracted.Length} 条",
                    LogLevel.Debug);
                return extracted;
            }
            else if (extracted.Length > 0)
            {
                ModEntry.SMonitor?.Log(
                    $"[DialogueParsing] 第四级正则仅提取 {extracted.Length} 条，不足 3 条，继续降级",
                    LogLevel.Trace);
            }
        }
        catch (Exception ex)
        {
            ModEntry.SMonitor?.Log(
                $"[DialogueParsing] 第四级正则提取异常：{ex.Message}",
                LogLevel.Warn);
        }

        // 第五级：列表行提取（处理各种编号/列表格式）
        try
        {
            var extractedLines = new List<string>();

            var knownHeaders = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "角色台词", "台词", "对话", "响应", "回复", "输出", "结果",
                "bark", "barks", "dialogue", "lines", "output", "result", "response", "reply"
            };

            foreach (var rawLine in rawText.Split('\n'))
            {
                string line = rawLine.Trim();
                if (string.IsNullOrWhiteSpace(line)) continue;

                if (line.StartsWith("#"))
                {
                    string headerText = System.Text.RegularExpressions.Regex.Replace(
                        line, @"^#+\s*", "").Trim();
                    if (knownHeaders.Contains(headerText))
                    {
                        ModEntry.SMonitor?.Log(
                            $"[DialogueParsing] 第五级过滤标题行：\"{line}\"",
                            LogLevel.Trace);
                        continue;
                    }
                }

                if (line.StartsWith("//") || line.StartsWith("/*") || line.EndsWith("*/") || line == "*/]")
                {
                    ModEntry.SMonitor?.Log(
                        $"[DialogueParsing] 第五级过滤注释行：\"{line}\"",
                        LogLevel.Trace);
                    continue;
                }

                line = System.Text.RegularExpressions.Regex.Replace(line, @"^[\-\*•·]\s+", "");

                line = System.Text.RegularExpressions.Regex.Replace(
                    line, @"^[\(\[<【]?\s*\d+\s*[\)\]\>】\.\、．]\s*", "");

                line = System.Text.RegularExpressions.Regex.Replace(
                    line, @"^[①②③④⑤⑥⑦⑧⑨⑩]\s*", "");

                line = line.Trim();
                line = line.Trim('[', ']').Trim();

                if (string.IsNullOrWhiteSpace(line)) continue;
                if (line.Length < 2) continue;

                if (System.Text.RegularExpressions.Regex.IsMatch(line, @"^[\*\/\[\]\{\}\(\)\-=_]+$"))
                {
                    ModEntry.SMonitor?.Log(
                        $"[DialogueParsing] 第五级过滤纯符号行：\"{line}\"",
                        LogLevel.Trace);
                    continue;
                }

                if (System.Text.RegularExpressions.Regex.IsMatch(
                    line,
                    @"^\s*(import|export|function|const|let|var|class|interface|type|enum|namespace|return|await|async|use[A-Z])\b",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                {
                    ModEntry.SMonitor?.Log(
                        $"[DialogueParsing] 第五级过滤代码关键词行：\"{line}\"",
                        LogLevel.Trace);
                    continue;
                }

                if (line.Contains("useState", StringComparison.Ordinal) ||
                    line.Contains("useEffect", StringComparison.Ordinal) ||
                    line.Contains("useMemo", StringComparison.Ordinal) ||
                    line.Contains("useCallback", StringComparison.Ordinal) ||
                    line.Contains("from '", StringComparison.Ordinal) ||
                    line.Contains("from \"", StringComparison.Ordinal) ||
                    line.Contains("require(", StringComparison.Ordinal) ||
                    line.Contains("<script", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("<html", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("cloudflare", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("challenge-platform", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("__cf_chl", StringComparison.OrdinalIgnoreCase))
                {
                    ModEntry.SMonitor?.Log(
                        $"[DialogueParsing] 第五级过滤代码/网络拦截特征行：\"{line}\"",
                        LogLevel.Trace);
                    continue;
                }

                extractedLines.Add(line);
            }

            var distinct = extractedLines.Distinct().ToArray();

            if (distinct.Length >= 3)
            {
                ModEntry.SMonitor?.Log(
                    $"[DialogueParsing] 第五级列表行提取成功，共 {distinct.Length} 条",
                    LogLevel.Debug);
                return distinct;
            }
            else if (distinct.Length > 0)
            {
                ModEntry.SMonitor?.Log(
                    $"[DialogueParsing] 第五级仅提取 {distinct.Length} 条，不足 3 条，判定为无效输出",
                    LogLevel.Warn);
            }
        }
        catch { }

        return null;
    }
}
