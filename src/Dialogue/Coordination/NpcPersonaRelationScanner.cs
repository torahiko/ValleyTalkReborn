using System;
using System.Collections.Generic;
using StardewValley;

namespace ValleytalkReborn.Dialogue.Coordination;

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

    /// <summary>
    /// TIE-CAST-001: 纯心数闸门。关系条目在其 Bio 属主的好感度心数达到或超过
    /// 该条目的 RequiredHearts 时才"激活"，供选角拟合与提示词注入共用。
    /// </summary>
    internal static bool IsRelationshipActive(int ownerHearts, BioData.ListEntry entry)
    {
        if (entry == null)
            return false;
        return ownerHearts >= entry.RequiredHearts;
    }

    /// <summary>
    /// TIE-CAST-001: 玩家对某 NPC 的友谊心数（Game1.player.friendshipData，
    /// 与 BarkFocusRouter 同模式；缺失条目 = 0）。
    /// </summary>
    internal static int GetFriendshipHearts(string npcName)
    {
        if (string.IsNullOrWhiteSpace(npcName))
            return 0;

        var player = Game1.player;
        if (player?.friendshipData == null)
            return 0;

        if (!player.friendshipData.TryGetValue(npcName, out var friendship) || friendship == null)
            return 0;

        return friendship.Points / 250;
    }

    /// <summary>
    /// TIE-CAST-001: 心数门控的关系描述读取。仅当 owner→target 条目存在且
    /// 激活（hearts(owner) ≥ RequiredHearts）时返回其非空 Description；否则 null。
    /// </summary>
    internal static string GetActiveDescription(string ownerName, string targetName)
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
                if (!IsRelationshipActive(GetFriendshipHearts(ownerName), kv.Value))
                    return null;

                string desc = kv.Value.Description;
                return string.IsNullOrWhiteSpace(desc) ? null : desc;
            }
        }

        return null;
    }

    /// <summary>
    /// TIE-CAST-001: A→B 或 B→A 任一方向的心数激活关系非空即 true。
    /// </summary>
    internal static bool HasActiveRelationInAnyDirection(string nameA, string nameB)
    {
        if (string.IsNullOrWhiteSpace(nameA) || string.IsNullOrWhiteSpace(nameB))
            return false;
        return GetActiveDescription(nameA, nameB) != null || GetActiveDescription(nameB, nameA) != null;
    }

    /// <summary>
    /// TIE-CAST-001: 返回某 NPC 的原始合并邻居集合——出边（其自身
    /// Relationships 键）+ 入边（一次有界遍历 DialogueBuilder 已加载角色中
    /// Relationships 含该 NPC 者），按 OrdinalIgnoreCase 去重并排除自身。
    /// 不做占用/可用性过滤，也不做心数/有效性过滤；调用方负责施加这些闸门。
    /// </summary>
    internal static bool TryGetNeighbors(string npcName, out IReadOnlyList<string> neighborNames)
    {
        neighborNames = Array.Empty<string>();

        if (string.IsNullOrWhiteSpace(npcName))
            return false;

        var builder = DialogueBuilder.Instance;
        var merged = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Out-edges: the NPC's own Relationships keys.
        var selfBio = builder.GetCharacterByName(npcName)?.Bio;
        if (selfBio?.Relationships != null)
        {
            foreach (var kv in selfBio.Relationships)
            {
                if (kv.Value == null) continue;
                if (!string.IsNullOrWhiteSpace(kv.Key)
                    && !string.Equals(kv.Key, npcName, StringComparison.OrdinalIgnoreCase))
                    merged.Add(kv.Key);
            }
        }

        // In-edges: one bounded pass over loaded characters.
        foreach (var character in builder.GetAllLoadedCharacters())
        {
            if (character == null || string.IsNullOrWhiteSpace(character.Name)) continue;
            if (string.Equals(character.Name, npcName, StringComparison.OrdinalIgnoreCase)) continue;
            if (merged.Contains(character.Name)) continue;

            if (character.Bio?.Relationships == null) continue;
            foreach (var kv in character.Bio.Relationships)
            {
                if (string.Equals(kv.Key, npcName, StringComparison.OrdinalIgnoreCase))
                {
                    merged.Add(character.Name);
                    break;
                }
            }
        }

        neighborNames = new List<string>(merged);
        return true;
    }
}
