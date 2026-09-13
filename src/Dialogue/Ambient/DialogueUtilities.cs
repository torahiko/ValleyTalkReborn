using System;
using System.Collections.Generic;
using System.Linq;
using StardewValley;

namespace ValleytalkReborn;

/// <summary>
/// 对话系统工具方法（从 DynamicBarkManager 中拆出的距离/状态判断等纯函数）。
/// </summary>
internal static class DialogueUtilities
{
    private const int TILE_SIZE = 64;

    /// <summary>
    /// NPC 与 NPC 距离判断。
    /// </summary>
    internal static bool IsInRangeSquared(NPC npcA, NPC npcB, int rangeSquared)
    {
        if (npcA == null || npcB == null) return false;
        if (npcA.currentLocation != npcB.currentLocation) return false;

        long dx = (long)npcA.Position.X - (long)npcB.Position.X;
        long dy = (long)npcA.Position.Y - (long)npcB.Position.Y;
        long distSq = (dx * dx + dy * dy) / (TILE_SIZE * TILE_SIZE);

        return distSq <= rangeSquared;
    }

    /// <summary>
    /// NPC 与玩家距离判断。
    /// </summary>
    internal static bool IsInRangeSquared(NPC npc, Farmer player, int rangeSquared)
    {
        if (npc == null || player == null) return false;
        if (npc.currentLocation != player.currentLocation) return false;

        long dx = (long)npc.Position.X - (long)player.Position.X;
        long dy = (long)npc.Position.Y - (long)player.Position.Y;
        long distSq = (dx * dx + dy * dy) / (TILE_SIZE * TILE_SIZE);

        return distSq <= rangeSquared;
    }

    /// <summary>
    /// 判断 NPC 是否正在睡眠。
    /// </summary>
    internal static bool IsNpcSleeping(NPC npc)
    {
        if (npc == null) return true;

        try
        {
            return npc.isSleeping.Value;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 计算 NPC 到玩家的距离平方（用于排序）。
    /// </summary>
    internal static long DistanceSqToPlayer(NPC npc)
    {
        if (npc == null || Game1.player == null) return long.MaxValue;

        long dx = (long)npc.Position.X - (long)Game1.player.Position.X;
        long dy = (long)npc.Position.Y - (long)Game1.player.Position.Y;

        return (dx * dx + dy * dy) / (TILE_SIZE * TILE_SIZE);
    }

    /// <summary>
    /// 解析对话中的 token（@、%farm、%pet）。
    /// </summary>
    internal static string ResolveDialogueTokens(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;

        text = text.Replace("@", Game1.player?.Name ?? "Farmer");
        text = text.Replace("%farm", Game1.player?.farmName?.Value ?? "Farm");
        text = text.Replace("%pet", Game1.player?.getPetName() ?? "pet");

        while (text.Contains("  "))
            text = text.Replace("  ", " ");

        return text.Trim();
    }

    /// <summary>
    /// 生成 A2A 组合冷却 key。
    /// </summary>
    internal static string MakePairKey(IEnumerable<string> names)
    {
        var sorted = names
            .Select(n => n ?? "")
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return string.Join("_", sorted);
    }

    /// <summary>
    /// 判断 NPC 是否正在跟随玩家。
    /// </summary>
    internal static bool IsFollowingSafe(NPC npc)
    {
        try
        {
            return MovementManager.Instance?.IsFollowing(npc) == true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 判断 NPC 是否正在与玩家约会。
    /// </summary>
    internal static bool IsOnDate(NPC npc)
    {
        try
        {
            return npc != null && DateManager.Instance?.IsOnDate(npc.Name) == true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 获取下一个显示间隔（ticks）。
    /// </summary>
    internal static int NextDisplayInterval(NPC npc, Random rng)
    {
        if (IsFollowingSafe(npc) || IsOnDate(npc))
        {
            // 跟随/约会：约 8 ~ 11 秒
            return 480 + rng.Next(181);
        }

        // 日常独处（同居/农舍/小镇）：约 11 ~ 14 秒一句，整轮 5 句约 60 秒放完
        return 660 + rng.Next(181);
    }

    /// <summary>
    /// 计算 NPC 优先级（用于雷达扫描排序）。
    /// </summary>
    internal static int CalculatePriority(NPC npc)
    {
        if (npc == null) return 0;

        // 跟随或约会拥有最高优先级
        if (IsFollowingSafe(npc) || IsOnDate(npc))
            return 4;

        var player = Game1.player;
        if (player == null) return 0;

        int hearts = 0;
        if (player.friendshipData != null && 
            player.friendshipData.TryGetValue(npc.Name, out var friendship) && 
            friendship != null)
        {
            hearts = Math.Max(0, friendship.Points / 250);
        }

        if (hearts >= 6) return 3;
        if (hearts >= 3) return 2;

        // 基础优先级：新存档、0心、未结识的村民返回 1，保证能正常触发 Bark
        return 1;
    }

    /// <summary>
    /// 冷却字典 Tick。
    /// </summary>
    internal static void TickCooldownDict(Dictionary<string, int> dict)
    {
        if (dict.Count == 0) return;

        List<string> toRemove = null;

        foreach (var key in dict.Keys.ToList())
        {
            dict[key]--;

            if (dict[key] <= 0)
                (toRemove ??= new List<string>()).Add(key);
        }

        if (toRemove != null)
        {
            foreach (var key in toRemove)
                dict.Remove(key);
        }
    }
}
