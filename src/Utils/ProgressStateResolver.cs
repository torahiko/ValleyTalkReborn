using System.Collections.Generic;
using System.Linq;
using StardewValley;

namespace ValleytalkReborn;

/// <summary>
/// 通用的"阶段性状态"解析器：根据当前婚姻状态与好感度，
/// 从角色卡的 BioData.ProgressStates 中选出唯一生效的一档文本。
/// 供 BarkPromptBuilder / A2APromptBuilder / 主对话 Prompt 构建统一调用，
/// 避免任何针对具体角色名的硬编码判断散落在各处。
/// </summary>
internal static class ProgressStateResolver
{
    /// <summary>
    /// 解析当前应注入的阶段性状态文本。未配置或未命中任何档位时返回 null。
    /// </summary>
    /// <param name="npc">目标 NPC。</param>
    /// <param name="states">该角色卡配置的档位列表（BioData.ProgressStates）。</param>
    internal static string ResolveActiveState(NPC npc, List<BioData.ProgressStateEntry> states)
    {
        if (npc == null || states == null || states.Count == 0)
            return null;

        bool isMarried = IsMarriedToPlayer(npc);
        int hearts = GetHearts(npc);

        // 1. 优先匹配"已婚"档位。按心数从高到低取第一个满足条件的档位，
        // 既允许 0 心结婚直接命中基础婚后档，也能兼容 14 心婚后深层剧情档。
        if (isMarried)
        {
            var marriedEntry = states
                .Where(s => s.RequireMarried && !string.IsNullOrWhiteSpace(s.Text))
                .OrderByDescending(s => s.RequiredHearts)
                .FirstOrDefault(s => hearts >= s.RequiredHearts);

            if (marriedEntry != null)
                return marriedEntry.Text;
        }

        // 2. 未命中已婚档位（未婚或婚后未满足心数），按心数门槛从高到低取第一个满足条件的普通档位。
        var normalEntry = states
            .Where(s => !s.RequireMarried && !string.IsNullOrWhiteSpace(s.Text))
            .OrderByDescending(s => s.RequiredHearts)
            .FirstOrDefault(s => hearts >= s.RequiredHearts);

        return normalEntry?.Text;
    }

    /// <summary>
    /// 判断该 NPC 是否与玩家已婚——综合法定配偶、原版婚姻数据、
    /// 以及多角恋 mod 的正式配偶信号，任一为真即视为已婚。
    /// （注意：非正式恋人 UnofficialSpouse 不计入已婚，避免跳过未婚恋爱期）
    /// </summary>
    private static bool IsMarriedToPlayer(NPC npc)
    {
        if (npc == null) return false;

        // 法定配偶与 Poly 官方正式配偶
        if (CompanionScheduleManager.IsLegalSpouse(npc.Name))
            return true;

        if (PolyamorySweetLoveBridge.IsOfficialSpouse(npc))
            return true;

        // 原版存档关系检查
        var player = Game1.player;
        if (player?.friendshipData != null
            && player.friendshipData.TryGetValue(npc.Name, out var fs)
            && fs != null
            && fs.IsMarried())
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// 获取玩家对该 NPC 的好感度心数。无数据时返回 0。
    /// </summary>
    private static int GetHearts(NPC npc)
    {
        if (npc == null) return 0;

        var player = Game1.player;
        if (player?.friendshipData == null) return 0;

        if (!player.friendshipData.TryGetValue(npc.Name, out var fs) || fs == null)
            return 0;

        return fs.Points / 250;
    }
}