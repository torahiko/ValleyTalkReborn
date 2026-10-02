// SocialBackboneBuilder.cs
// REL-002 — 已知关系骨架（Known Relationship Backbone）。
// 从 BioData.Relationships 的显式公开身份标签确定性拼装一个独立提示词段。
// 纯内存装配：输入字典与显示名委托，输出单条字符串；不读取心数、Description、
// Heading、id，不做持久化、事件订阅、Harmony 或反射。排序与语言选择规则唯一
// 决定输出，保证同一角色卡在多次请求间逐字符相同。

using System;
using System.Collections.Generic;
using System.Text;

namespace ValleytalkReborn;

internal static class SocialBackboneBuilder
{
    /// <summary>非空公开身份标签允许的最大长度（UTF-16 字符）。</summary>
    private const int MaxLabelLength = 48;

    internal static string Build(
        string speakerName,
        Dictionary<string, BioData.ListEntry> relationships,
        bool isZh,
        Func<string, string> resolveDisplayName)
    {
        if (string.IsNullOrWhiteSpace(speakerName))
        {
            ModEntry.SMonitor?.Log(
                "[SocialBackbone] Build rejected: speakerName is blank.",
                StardewModdingAPI.LogLevel.Error);
            throw new ArgumentException("SocialBackbone requires a non-blank speaker name.", nameof(speakerName));
        }
        if (relationships == null)
        {
            ModEntry.SMonitor?.Log(
                $"[SocialBackbone] Build rejected for speaker '{speakerName}': relationships dictionary is null.",
                StardewModdingAPI.LogLevel.Error);
            throw new ArgumentNullException(nameof(relationships),
                "SocialBackbone requires a non-null relationships dictionary.");
        }
        if (resolveDisplayName == null)
        {
            ModEntry.SMonitor?.Log(
                $"[SocialBackbone] Build rejected for speaker '{speakerName}': display-name resolver is null.",
                StardewModdingAPI.LogLevel.Error);
            throw new ArgumentNullException(nameof(resolveDisplayName),
                "SocialBackbone requires a non-null display-name resolver.");
        }

        // 先对所有条目校验：键非空白、值非 null、非空标签单行且不超过上限。
        foreach (var pair in relationships)
        {
            if (string.IsNullOrWhiteSpace(pair.Key))
            {
                ModEntry.SMonitor?.Log(
                    $"[SocialBackbone] Invalid relationship entry for speaker '{speakerName}': entry key is blank or null.",
                    StardewModdingAPI.LogLevel.Error);
                throw new InvalidOperationException(
                    $"SocialBackbone found an invalid relationship entry for speaker '{speakerName}': the entry key is blank.");
            }
            if (pair.Value == null)
            {
                ModEntry.SMonitor?.Log(
                    $"[SocialBackbone] Invalid relationship entry for speaker '{speakerName}' target '{pair.Key}': entry value is null.",
                    StardewModdingAPI.LogLevel.Error);
                throw new InvalidOperationException(
                    $"SocialBackbone found an invalid relationship entry for speaker '{speakerName}' target '{pair.Key}': the entry value is null.");
            }
            ValidateLabel(speakerName, pair.Key, pair.Value.PublicIdentityEn, nameof(BioData.ListEntry.PublicIdentityEn));
            ValidateLabel(speakerName, pair.Key, pair.Value.PublicIdentityZh, nameof(BioData.ListEntry.PublicIdentityZh));
        }

        // 公开候选：任一公开身份标签非空，或 RequiredHearts 等于 0。
        // 排除与说话者同名的目标；不读取心数、Description、Heading、id。
        var candidates = new List<KeyValuePair<string, BioData.ListEntry>>();
        foreach (var pair in relationships)
        {
            if (string.Equals(pair.Key, speakerName, StringComparison.OrdinalIgnoreCase))
                continue;
            var entry = pair.Value;
            bool hasLabel = NonEmpty(entry.PublicIdentityEn) || NonEmpty(entry.PublicIdentityZh);
            if (!hasLabel && entry.RequiredHearts != 0)
                continue;
            candidates.Add(pair);
        }

        if (candidates.Count == 0)
        {
            ModEntry.SMonitor?.Log(
                $"[SocialBackbone] No public relationship candidates for speaker '{speakerName}'; emitting an empty backbone.",
                StardewModdingAPI.LogLevel.Trace);
            return string.Empty;
        }

        // 候选按字典键 Ordinal 固定排序，保证输出与字典插入顺序无关。
        candidates.Sort(static (a, b) => string.CompareOrdinal(a.Key, b.Key));

        var parts = new List<string>(candidates.Count);
        foreach (var pair in candidates)
        {
            string key = pair.Key;
            var entry = pair.Value;
            string zhLabel = entry.PublicIdentityZh?.Trim() ?? string.Empty;
            string enLabel = entry.PublicIdentityEn?.Trim() ?? string.Empty;

            string label;
            if (isZh)
            {
                if (zhLabel.Length > 0)
                {
                    label = zhLabel;
                }
                else if (enLabel.Length > 0)
                {
                    ModEntry.SMonitor?.Log(
                        $"[SocialBackbone] Cross-language label fallback for speaker '{speakerName}' target '{key}': Chinese label missing, using the declared English label.",
                        StardewModdingAPI.LogLevel.Trace);
                    label = enLabel;
                }
                else
                {
                    label = null;
                }
            }
            else
            {
                if (enLabel.Length > 0)
                {
                    label = enLabel;
                }
                else if (zhLabel.Length > 0)
                {
                    ModEntry.SMonitor?.Log(
                        $"[SocialBackbone] Cross-language label fallback for speaker '{speakerName}' target '{key}': English label missing, using the declared Chinese label.",
                        StardewModdingAPI.LogLevel.Trace);
                    label = zhLabel;
                }
                else
                {
                    label = null;
                }
            }

            string displayName;
            try
            {
                displayName = resolveDisplayName(key);
            }
            catch (Exception ex)
            {
                // BOUNDARY：显示名解析异常不吞不改，记录后按原异常继续传播。
                ModEntry.SMonitor?.Log(
                    $"[SocialBackbone] Display-name resolution threw for speaker '{speakerName}' target '{key}': {ex}",
                    StardewModdingAPI.LogLevel.Error);
                throw;
            }
            if (string.IsNullOrWhiteSpace(displayName))
            {
                ModEntry.SMonitor?.Log(
                    $"[SocialBackbone] Display-name resolver returned a blank name for speaker '{speakerName}' target '{key}'.",
                    StardewModdingAPI.LogLevel.Error);
                throw new InvalidOperationException(
                    $"SocialBackbone display-name resolver returned a blank name for speaker '{speakerName}' target '{key}'.");
            }
            displayName = displayName.Trim();

            if (label == null)
            {
                parts.Add(displayName);
            }
            else if (isZh)
            {
                parts.Add($"{displayName}（{label}）");
            }
            else
            {
                parts.Add($"{displayName} ({label})");
            }
        }

        string title = isZh ? "## 已知关系骨架" : "## Known Relationship Backbone";
        string focus = isZh
            ? "以下人物及身份是你已明确掌握的关系背景。回应涉及他们的话题时，以这份背景建立人物关系；近期情况由本次场景与事件资料提供。"
            : "These people and identities are established relationship background you know. Ground responses about them in this background; current circumstances come from the scene and event context for this request.";
        string joinedEntries = isZh ? string.Join("；", parts) : string.Join("; ", parts);

        var section = new StringBuilder();
        section.AppendLine(title);
        section.AppendLine(focus);
        section.Append(joinedEntries);
        return section.ToString();
    }

    private static bool NonEmpty(string label) => !string.IsNullOrWhiteSpace(label);

    private static void ValidateLabel(string speakerName, string targetKey, string label, string fieldName)
    {
        string trimmed = label?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
            return;
        if (trimmed.IndexOf('\n') >= 0 || trimmed.IndexOf('\r') >= 0)
        {
            ModEntry.SMonitor?.Log(
                $"[SocialBackbone] Invalid public identity label for speaker '{speakerName}' target '{targetKey}' on {fieldName}: label must be single-line.",
                StardewModdingAPI.LogLevel.Error);
            throw new InvalidOperationException(
                $"SocialBackbone label validation failed for speaker '{speakerName}' target '{targetKey}' on {fieldName}: the label must be single-line.");
        }
        if (trimmed.Length > MaxLabelLength)
        {
            ModEntry.SMonitor?.Log(
                $"[SocialBackbone] Invalid public identity label for speaker '{speakerName}' target '{targetKey}' on {fieldName}: label exceeds {MaxLabelLength} UTF-16 characters.",
                StardewModdingAPI.LogLevel.Error);
            throw new InvalidOperationException(
                $"SocialBackbone label validation failed for speaker '{speakerName}' target '{targetKey}' on {fieldName}: the label exceeds {MaxLabelLength} UTF-16 characters.");
        }
    }
}
