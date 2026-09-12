using System;
using System.Collections.Generic;
using StardewValley;

namespace ValleytalkReborn;

/// <summary>
/// A2A 话题钩子的催化剂来源。
/// </summary>
internal enum A2ACatalystType
{
    /// <summary>镇上八卦。</summary>
    Gossip,
    /// <summary>地点基线氛围（GenerateConversationTopic 输出）。</summary>
    LocationBaseline,
}

/// <summary>
/// 路由器决选结果：本次请求唯一的话题钩子来源及其注入文案。
/// </summary>
internal sealed class A2ATopicDecision
{
    public A2ACatalystType Type { get; init; }
    public string InjectedLine { get; init; }
}

/// <summary>
/// A2A 话题路由器：在"八卦催化剂"与"地点基线"之间做加权单槽决选，
/// 决定本次请求的 背景氛围 钩子文案。无状态，仅主线程 Build 路径消费。
/// 权重为硬编码常量，不引入持久化状态或配置项。
/// </summary>
internal static class A2ATopicRouter
{
    private static readonly Random _rng = new Random();

    private const float GossipWeight = 2.2f;
    private const float LocationBaselineWeight = 1.8f;

    /// <summary>
    /// 决选唯一话题钩子。Gossip 候选仅在 filteredGossip 非空时入池；
    /// LocationBaseline 恒有（GenerateConversationTopic 现有实现恒非空）。
    /// </summary>
    internal static A2ATopicDecision Decide(List<NPC> participants, string filteredGossip, bool isZh)
    {
        float gossipWeight = !string.IsNullOrWhiteSpace(filteredGossip) ? GossipWeight : 0f;

        string baselineLine = A2APromptBuilder.GenerateConversationTopic(participants, isZh);
        float baselineWeight = string.IsNullOrWhiteSpace(baselineLine) ? 0f : LocationBaselineWeight;

        float total = gossipWeight + baselineWeight;

        // 兜底（理论上 Baseline 恒有；防御性）
        if (total <= 0f)
        {
            return new A2ATopicDecision
            {
                Type = A2ACatalystType.LocationBaseline,
                InjectedLine = baselineLine
                    ?? (isZh ? "两人随口闲聊。" : "The two chat casually."),
            };
        }

        float roll = (float)(_rng.NextDouble() * total);

        if (gossipWeight > 0f && roll < gossipWeight)
        {
            string line = isZh
                ? $"镇上最近流传一件事：{filteredGossip}。两人的闲聊可以围绕或顺势带出这件事。"
                : $"A rumor is going around town: {filteredGossip}. The chat may circle around it or brush against it.";
            return new A2ATopicDecision
            {
                Type = A2ACatalystType.Gossip,
                InjectedLine = line,
            };
        }

        return new A2ATopicDecision
        {
            Type = A2ACatalystType.LocationBaseline,
            InjectedLine = baselineLine,
        };
    }
}
