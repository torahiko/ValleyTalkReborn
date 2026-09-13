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
    /// 节日语境下的 NPC 与玩家距离判断。
    /// 节日内全部 Event.actors 与玩家必然同处活动地图，npc.currentLocation 在临时 Actor 上可能为 null，
    /// 故不做 npc.currentLocation 相等性检查，改为校验玩家确实处于当前活动地图（Game1.currentLocation）。
    /// 距离计算本体与 <see cref="IsInRangeSquared(NPC, Farmer, int)"/> 完全一致（long 防溢出、TILE_SIZE² 归一）。
    /// </summary>
    internal static bool IsInRangeSquaredDuringFestival(NPC npc, Farmer player, int rangeSquared)
    {
        if (npc == null || player == null) return false;
        if (Game1.currentLocation == null || player.currentLocation != Game1.currentLocation) return false;

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

    /// <summary>
    /// 判定两名 NPC 是否具备对话的空间姿态。
    /// 仅拦截四组明确背对背构型，其余（正对、斜对、并排同向、垂直相邻）一律视为可感知。
    /// 坐标系约定：y 轴向下增长；FacingDirection 0:上 1:右 2:下 3:左。
    /// 几何示例（"×"=不可感知，"✓"=可感知）：
    ///   ×  A↑ B↓   (dy>0, A 朝上, B 朝下：A 在上背对下方的 B)
    ///   ×  A↓ B↑   (dy<0, A 朝下, B 朝上：A 在下背对上方的 B)
    ///   ×  A← B→   (dx>0, A 朝左, B 朝右：A 在左背对右侧的 B)
    ///   ×  A→ B←   (dx<0, A 朝右, B 朝左：A 在右背对左侧的 B)
    ///   ✓  并排同向（同 dir，如吧台并排）、正对、垂直相邻、重叠 → 一律放行
    /// 调用时机约束：仅在雷达建簇时判定一次。POI 与节日 NPC 朝向静态，禁止逐 tick 重复判定。
    /// </summary>
    internal static bool AreNpcsMutuallyAware(NPC a, NPC b)
    {
        if (a == null || b == null) return false;

        long dx = (long)b.Position.X - (long)a.Position.X;
        long dy = (long)b.Position.Y - (long)a.Position.Y;
        int dirA = a.FacingDirection;
        int dirB = b.FacingDirection;

        // A 在上朝上、B 在下朝下 → 背对
        if (dy > 0 && dirA == 0 && dirB == 2) return false;
        // A 在下朝下、B 在上朝上 → 背对
        if (dy < 0 && dirA == 2 && dirB == 0) return false;
        // A 在左朝左、B 在右朝右 → 背对
        if (dx > 0 && dirA == 3 && dirB == 1) return false;
        // A 在右朝右、B 在左朝左 → 背对
        if (dx < 0 && dirA == 1 && dirB == 3) return false;

        return true;
    }
}
