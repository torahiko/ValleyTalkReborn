using System;
using System.Collections.Generic;
using System.Linq;
using StardewValley;

namespace ValleytalkReborn;

/// <summary>
/// 关系网"按天轮换"解析器：从角色卡的 BioData.Relationships 全量条目中，
/// 按"当前游戏天数 + 角色名"生成确定性种子，选出当天固定展示的 1~2 条关系。
///
/// 设计动机：
/// - 原 IsKnownNpc 开关的问题是"原版角色完全不注入关系网"，导致模型虽然在语料里
///   认识这些人物，但从不会主动在对话中流露相关情绪，缺乏活人感。
/// - 直接全量注入所有关系条目会让 NpcConstantContext 过长，且部分关系年年不变、
///   条条罗列反而稀释重点。
/// - 每次对话都随机挑选会导致 NpcConstantContext 逐轮变化，彻底击穿 LLM 的
///   prompt cache 前缀命中（该字符串每天仅在 Prompts 实例创建时求值一次，
///   但对话轮次内 Prompts 会重新构造，参见 LlmDialogueService.GenerateDialogueAsync）。
/// - 折中方案：以"游戏内天数"为轮换粒度，同一天内所有对话请求得到完全相同的
///   关系子集，因此当天的 cache 前缀保持稳定；只有跨天时子集才会变化，
///   缓存失效粒度从"每轮"降低为"每天"。
///
/// 与 ProgressStateResolver 的关系：两者都读取 BioData 里的角色卡数据、
/// 都是无状态的纯函数解析器，但语义不同——ProgressStates 回答"此刻角色处于
/// 哪个心理阶段"，本类回答"今天该显性提及哪些人际关系"，因此单独成类而非
/// 合并进 ProgressStateResolver。
/// </summary>
internal static class RelationshipRotationResolver
{
    /// <summary>
    /// 每天注入的关系条目数量上限。保持在 1~2 条，避免单日内容过重，
    /// 也避免和 Traits/ProgressStates 的信息量失衡。
    /// </summary>
    private const int DailyPickCount = 2;

    /// <summary>
    /// 从全量关系表中选出"今天"应该注入的子集。
    /// 同一天内、同一角色，多次调用返回完全一致的结果（纯函数，无缓存字段），
    /// 天然对 prompt cache 友好；跨天后种子变化，子集随之轮换。
    /// </summary>
    /// <param name="npcName">角色内部名，作为随机种子的一部分，确保不同角色不会撞到相同的选取顺序。</param>
    /// <param name="relationships">该角色卡配置的全量关系条目（BioData.Relationships）。</param>
    /// <param name="currentHearts">玩家对该角色当前的好感度心数，用于沿用既有的 RequiredHearts 门槛过滤。</param>
    internal static List<BioData.ListEntry> SelectDailyRelationships(
        string npcName,
        Dictionary<string, BioData.ListEntry> relationships,
        int currentHearts)
    {
        if (string.IsNullOrEmpty(npcName) || relationships == null || relationships.Count == 0)
            return new List<BioData.ListEntry>();

        // 先按既有的心数门槛过滤出当前可见的候选池，逻辑与 Traits 的门槛过滤保持一致。
        var eligible = relationships.Values
            .Where(r => currentHearts >= r.RequiredHearts)
            .OrderBy(r => r.id, StringComparer.Ordinal) // 固定基础顺序，确保同一天内种子一致时结果可复现
            .ToList();

        if (eligible.Count == 0)
            return new List<BioData.ListEntry>();

        if (eligible.Count <= DailyPickCount)
            return eligible;

        int day = GetCurrentDaySafe();
        int seed = HashCode.Combine(npcName, day);
        var rng = new Random(seed);

        // Fisher-Yates 部分洗牌，只需要前 DailyPickCount 个结果
        var pool = eligible.ToList();
        int pickCount = Math.Min(DailyPickCount, pool.Count);
        for (int i = 0; i < pickCount; i++)
        {
            int j = rng.Next(i, pool.Count);
            (pool[i], pool[j]) = (pool[j], pool[i]);
        }

        return pool.Take(pickCount).ToList();
    }

    /// <summary>
    /// 安全获取当前游戏天数。读档前/异常情况下返回 0，
    /// 保证种子计算不会因为 Game1.Date 未就绪而抛出异常。
    /// </summary>
    private static int GetCurrentDaySafe()
    {
        try
        {
            return Game1.Date?.TotalDays ?? 0;
        }
        catch
        {
            return 0;
        }
    }
}
