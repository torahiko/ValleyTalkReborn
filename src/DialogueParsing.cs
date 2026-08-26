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
    /// 从 LLM 原始输出中提取 JSON 数组字符串。
    /// </summary>
    internal static string ExtractJsonArrayString(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;

        string cleaned = raw.Replace("```json", "").Replace("```", "").Trim();

        int start = cleaned.IndexOf('[');
        int end = cleaned.LastIndexOf(']');

        // 兼容：如果找不到 [ 但找到了 ]，认为开头就是数组内容，补全 [
        if (start < 0 && end >= 0)
        {
            cleaned = "[" + cleaned;
            start = 0;
            // 重新定位 ]
            end = cleaned.LastIndexOf(']');
        }

        return (start >= 0 && end > start) ? cleaned.Substring(start, end - start + 1) : null;
    }

    /// <summary>
    /// 解析 A2A 脚本 JSON。
    /// </summary>
    internal static DialogueModels.A2ALine[] ParseA2AScript(string json, List<NPC> participants)
    {
        try
        {
            var raw = JsonConvert.DeserializeObject<List<Dictionary<string, string>>>(json);
            if (raw == null || raw.Count == 0) return null;

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
    /// 解析 Bark JSON（四级降级解析）。
    /// </summary>
    internal static string[] ParseBarkJson(string cleanJson)
    {
        // 第一级：标准 string[] 反序列化
        try
        {
            var result = JsonConvert.DeserializeObject<string[]>(cleanJson);

            if (result != null && result.Length > 0)
                return result;
        }
        catch { }

        // 第二级：对象数组降级（兼容 {line/text/首个值} 格式）
        try
        {
            var objs = JsonConvert.DeserializeObject<List<Dictionary<string, string>>>(cleanJson);

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
                        $"[DynamicBarkManager] ParseBarkJson 降级解析成功，共 {lines.Length} 条",
                        LogLevel.Debug);
                    return lines;
                }
            }
        }
        catch { }

        // 第三级：尝试补全缺失的 [ 或 ] 后再解析
        try
        {
            string repaired = cleanJson.Trim();
            if (!repaired.StartsWith("[")) repaired = "[" + repaired;
            if (!repaired.EndsWith("]")) repaired = repaired + "]";

            var repairResult = JsonConvert.DeserializeObject<string[]>(repaired);
            if (repairResult != null && repairResult.Length > 0)
            {
                ModEntry.SMonitor?.Log(
                    $"[DynamicBarkManager] ParseBarkJson 补全修复成功，共 {repairResult.Length} 条",
                    LogLevel.Debug);
                return repairResult;
            }
        }
        catch { }

        // 第四级：正则暴力提取所有双引号字符串，过滤掉已知的 key 名
        try
        {
            var matches = System.Text.RegularExpressions.Regex.Matches(
                cleanJson,
                @"""((?:[^""\\]|\\.)*)""");

            // 已知的 JSON key 名，不是台词内容
            var knownKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                { "line", "text", "bark", "speaker", "name", "dialogue", "content" };

            var extracted = matches
                .Cast<System.Text.RegularExpressions.Match>()
                .Select(m => m.Groups[1].Value.Trim())
                .Where(s => !string.IsNullOrWhiteSpace(s) && !knownKeys.Contains(s))
                .Distinct()
                .ToArray();

            if (extracted.Length > 0)
            {
                ModEntry.SMonitor?.Log(
                    $"[DynamicBarkManager] ParseBarkJson 正则兜底提取成功，共 {extracted.Length} 条",
                    LogLevel.Debug);
                return extracted;
            }
        }
        catch (Exception ex)
        {
            ModEntry.SMonitor?.Log(
                $"[DynamicBarkManager] JSON parse failed (fallback also failed): {ex.Message}\nRawJson:\n{cleanJson}",
                LogLevel.Warn);
        }

        return null;
    }
}
