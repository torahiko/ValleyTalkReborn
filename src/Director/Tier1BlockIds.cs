// Tier1BlockIds.cs
// VT3-B — Tier 1 块标识符常量。
// Tier 1 = 稳定前缀块：整轮对话内不随每代重排，可跨请求缓存复用。
// 按修正 M3：PlayerProfile 不在 Tier 1（归入 Tier 2b）。

using System.Collections.Generic;

namespace ValleytalkReborn;

/// <summary>
/// Tier 1 块标识符（11 项）。这些块内容在单次对话会话内稳定，
/// 构成 Tier1SnapshotContext 的缓存键空间。
/// </summary>
public static class Tier1BlockIds
{
    public const string GameState = "GameState";
    public const string EventHistory = "EventHistory";
    public const string BranchTheme = "BranchTheme";
    public const string Scene = "Scene";
    public const string CompanionFocus = "CompanionFocus";
    public const string GreetingContext = "GreetingContext";
    public const string RelationBase = "RelationBase";
    public const string RecentEvents = "RecentEvents";
    public const string SpecialDates = "SpecialDates";
    public const string SpouseAction = "SpouseAction";
    public const string EvolvedTraits = "EvolvedTraits";

    /// <summary>Tier 1 全部块 Id 集合（有序）。</summary>
    public static readonly IReadOnlyList<string> All = new[]
    {
        GameState, EventHistory, BranchTheme, Scene, CompanionFocus, GreetingContext,
        RelationBase, RecentEvents, SpecialDates, SpouseAction, EvolvedTraits,
    };
}
