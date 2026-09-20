#nullable enable
using Microsoft.Xna.Framework.Graphics;
using StardewValley;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ValleytalkReborn.Services;

/// <summary>
/// 共享的 NPC 候选集查询服务。
/// 为 Hub NPC 记忆下拉框、NPC 宫格与 BioEditorMenu 等提供统一的候选判定、资源清洗与消歧逻辑。
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
    public static bool IsInvalidOrEventNpc(string internalName, NPC? npc, string? excludeName = null)
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
    /// 按 friendshipData → characterData → getAllCharacters 采集基础候选名单。
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

    /// <summary>
    /// 核心清洗入口：完成肖像检查、按显示名消歧去重（过滤重复马龙/派生实体）并按拼音字母排序。
    /// </summary>
    public static List<(string Id, string DisplayName)> GetCleanedCandidates(string? excludeName = null)
    {
        var rawCandidates = CollectRawCandidates(excludeName);
        rawCandidates.Add("Marlon"); // 确保真马龙存在

        var resolved = new Dictionary<string, (string Id, string DisplayName)>(StringComparer.OrdinalIgnoreCase);

        foreach (var name in rawCandidates)
        {
            if (string.IsNullOrWhiteSpace(name)) continue;

            // 1. 肖像与资源有效性检查（过滤掉不支持编辑、没有有效立绘的底层角色）
            var character = Game1.getCharacterFromName(name);
            Texture2D? portrait = character?.Portrait;
            if (portrait == null || portrait.IsDisposed)
            {
                try
                {
                    portrait = Game1.content.Load<Texture2D>("Portraits\\" + name);
                }
                catch { }
            }

            if (portrait == null || portrait.IsDisposed || portrait.Width < 64 || portrait.Height < 64)
                continue;

            string dispName = character?.displayName ?? string.Empty;
            if (string.IsNullOrWhiteSpace(dispName))
                dispName = name;

            bool hasFriendship = Game1.player?.friendshipData?.ContainsKey(name) == true;

            // 2. 显示名冲突消歧判定（根据本地化显示名去重，保留正统角色）
            if (resolved.TryGetValue(dispName, out var existing))
            {
                bool isCurrentTrue = name.Equals("Marlon", StringComparison.OrdinalIgnoreCase);
                bool isExistingTrue = existing.Id.Equals("Marlon", StringComparison.OrdinalIgnoreCase);

                if (isCurrentTrue && !isExistingTrue)
                {
                    resolved[dispName] = (name, dispName);
                }
                else if (!isCurrentTrue && isExistingTrue)
                {
                    continue;
                }
                else
                {
                    bool existingHasFriendship = Game1.player?.friendshipData?.ContainsKey(existing.Id) == true;
                    if ((hasFriendship && !existingHasFriendship) ||
                        (hasFriendship == existingHasFriendship && name.Length < existing.Id.Length))
                    {
                        resolved[dispName] = (name, dispName);
                    }
                }
            }
            else
            {
                resolved[dispName] = (name, dispName);
            }
        }

        // 3. 按照本地化名称统一排序
        return resolved.Values
            .OrderBy(tuple => tuple.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }
}