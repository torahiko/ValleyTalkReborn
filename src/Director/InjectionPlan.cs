// InjectionPlan.cs
// VT3-B — 单次 LLM 请求的完整注入计划。
// 由 VT3-D 的 ConversationDirector.BuildPlan 组装，供 Prompt 装配层消费。
// 本文件仅承载数据结构，不含构建逻辑（构建逻辑属 VT3-D）。

using System.Collections.Generic;

namespace ValleytalkReborn;

/// <summary>
/// 一次 LLM 描述请求的完整注入计划：会话续用状态、分支、Tier 1 快照、
/// 历史窗口大小、已渲染的活跃脉冲块集合，以及建议回复开关。
/// </summary>
public sealed class InjectionPlan
{
    /// <summary>本次请求的会话标识。与 Tier1SnapshotStore 的 SessionRecord.SessionId 对齐。</summary>
    public string SessionId { get; init; } = string.Empty;

    /// <summary>本次 Tier 1 快照是否由历史会话续用（active 续用或 closed 软继承）。</summary>
    public bool IsTier1Reused { get; init; }

    /// <summary>本次请求所处的对话分支。</summary>
    public InstructionsBranch Branch { get; init; }

    /// <summary>始终非空：新会话由 BuildPlan 构建并注册快照后携带（VT3-D 契约）。</summary>
    public Tier1SnapshotContext Tier1Snapshot { get; init; }

    /// <summary>历史窗口大小（已 Clamp 到 1..20）。短上下文门控在装配层。</summary>
    public int HistoryWindowSize { get; init; }

    /// <summary>BlockId → 已渲染文本，仅含非空项。由装配层在构造时过滤。</summary>
    public Dictionary<string, string> ActiveImpulses { get; init; }
        = new Dictionary<string, string>();

    /// <summary>本次 Echo 是否来自微社交 3 秒桥（bridge 优先于即时 echo）。</summary>
    public bool EchoFromBridge { get; init; }

    /// <summary>是否启用建议回复（来自 ModEntry.Config.EnableSuggestedResponses）。</summary>
    public bool EnableSuggestedResponses { get; init; }
}
