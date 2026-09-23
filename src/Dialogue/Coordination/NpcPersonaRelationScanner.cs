using System;
using System.Collections.Generic;

namespace ValleytalkReborn;

/// <summary>
/// 无状态扫描器：从 NPC 人设卡（Bio）的 Relationships 字典中读取双向关系描述。
/// 仅供 A2A 运行时路径在主线程调用，不引入缓存或锁。
/// </summary>
internal static class NpcPersonaRelationScanner
{
    /// <summary>
    /// 返回 owner 人设卡中 target 条目的非空 Description；无条目或空白 → null。
    /// </summary>
    internal static string GetDescription(string ownerName, string targetName)
    {
        if (string.IsNullOrWhiteSpace(ownerName) || string.IsNullOrWhiteSpace(targetName))
            return null;

        var bio = DialogueBuilder.Instance?.GetCharacterByName(ownerName)?.Bio;
        if (bio?.Relationships == null)
            return null;

        foreach (var kv in bio.Relationships)
        {
            if (kv.Value == null) continue;
            if (string.Equals(kv.Key, targetName, StringComparison.OrdinalIgnoreCase))
            {
                string desc = kv.Value.Description;
                return string.IsNullOrWhiteSpace(desc) ? null : desc;
            }
        }
        return null;
    }

    /// <summary>
    /// A→B 或 B→A 任一方向 GetDescription 非 null 即 true；任一名字空白 → false。
    /// </summary>
    internal static bool HasRelationInAnyDirection(string nameA, string nameB)
    {
        if (string.IsNullOrWhiteSpace(nameA) || string.IsNullOrWhiteSpace(nameB))
            return false;
        return GetDescription(nameA, nameB) != null || GetDescription(nameB, nameA) != null;
    }
}
