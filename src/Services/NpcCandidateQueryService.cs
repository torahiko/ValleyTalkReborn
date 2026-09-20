#nullable enable
using StardewValley;
using System;
using System.Collections.Generic;

namespace ValleytalkReborn.Services;

/// <summary>
/// 共享的 NPC 候选集查询服务。
/// 为 Hub NPC 宫格（RefreshNpcCards）与 BioEditorMenu 关系列表（InitNpcList）
/// 提供统一的候选谓词与三段采集逻辑。
/// </summary>
internal static class NpcCandidateQueryService
{
    private static readonly HashSet<string> ExcludedNpcNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Grandpa", "Governor", "Gil", "Bouncer", "Birdie", "Henchman", "MarlonFudge"
    };

    /// <summary>
    /// 判定一个 NPC 内部名是否应被排除（非法名 / 事件演员 / 非社交 / 黑名单 / 被编辑 NPC 自身）。
    /// </summary>
    public static bool IsInvalidOrEventNpc(string internalName, NPC npc, string? excludeName = null)
    {
        if (string.IsNullOrWhiteSpace(internalName)) return true;
        if (excludeName != null && string.Equals(internalName, excludeName, StringComparison.OrdinalIgnoreCase)) return true;
        if (ExcludedNpcNames.Contains(internalName)) return true;

        if (internalName.Contains("_") ||
            internalName.Contains("Event", StringComparison.OrdinalIgnoreCase) ||
            internalName.Contains("Fake", StringComparison.OrdinalIgnoreCase) ||
            internalName.Contains("Dummy", StringComparison.OrdinalIgnoreCase))
            return true;

        bool isTrueMarlon = internalName.Equals("Marlon", StringComparison.OrdinalIgnoreCase);
        if (internalName.StartsWith("Marlon", StringComparison.OrdinalIgnoreCase) && !isTrueMarlon)
            return true;

        var friendshipData = Game1.player?.friendshipData;
        bool inFriendship = friendshipData != null && friendshipData.ContainsKey(internalName);

        if (npc != null)
        {
            if (Game1.CurrentEvent != null && Game1.CurrentEvent.actors != null && Game1.CurrentEvent.actors.Contains(npc))
                return true;

            if (!isTrueMarlon && !npc.CanSocialize && !inFriendship)
                return true;
        }
        else
        {
            if (!isTrueMarlon && !inFriendship)
                return true;
        }

        return false;
    }

    /// <summary>
    /// 按 friendshipData → characterData → getAllCharacters 的顺序采集候选内部名。
    /// 返回 HashSet(OrdinalIgnoreCase)，插入顺序与现两处实现完全一致。
    /// </summary>
    public static HashSet<string> CollectRawCandidates(string? excludeName = null)
    {
        var rawCandidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var friendshipData = Game1.player?.friendshipData;

        if (friendshipData != null)
        {
            foreach (var k in friendshipData.Keys)
            {
                if (!IsInvalidOrEventNpc(k, Game1.getCharacterFromName(k), excludeName))
                    rawCandidates.Add(k);
            }
        }

        if (Game1.characterData != null)
        {
            foreach (var kvp in Game1.characterData)
            {
                string name = kvp.Key;
                if (!IsInvalidOrEventNpc(name, Game1.getCharacterFromName(name), excludeName))
                    rawCandidates.Add(name);
            }
        }

        foreach (var npc in Utility.getAllCharacters())
        {
            if (npc != null && (npc.IsVillager || npc.Name.Equals("Marlon", StringComparison.OrdinalIgnoreCase)) && !IsInvalidOrEventNpc(npc.Name, npc, excludeName))
            {
                rawCandidates.Add(npc.Name);
            }
        }

        return rawCandidates;
    }
}
