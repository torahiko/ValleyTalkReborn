// Tier2bBlockIds.cs
// VT3-B — Tier 2b 块标识符常量。
// Tier 2b = 即时/一次性块：每代可能变化，需实时渲染，不进 Tier 1 快照缓存。

using System.Collections.Generic;

namespace ValleytalkReborn;

/// <summary>
/// Tier 2b 块标识符（17 项）。这些块反映即时状态，每代独立渲染。
/// </summary>
public static class Tier2bBlockIds
{
    public const string Gossip = "Gossip";
    public const string Interaction = "Interaction";
    public const string Jealousy = "Jealousy";
    public const string Preoccupation = "Preoccupation";
    public const string PendingTopic = "PendingTopic";
    public const string Gift = "Gift";
    public const string Milestone = "Milestone";
    public const string Echo = "Echo";
    public const string Eavesdrop = "Eavesdrop";
    public const string SpouseWaiting = "SpouseWaiting";
    public const string LocalPerception = "LocalPerception";
    public const string Emotion = "Emotion";
    public const string PlayerProfile = "PlayerProfile";
    public const string DateInvite = "DateInvite";
    public const string FollowProto = "FollowProto";
    public const string DateEndProto = "DateEndProto";
    public const string Movement = "Movement";

    /// <summary>Tier 2b 全部块 Id 集合（有序）。</summary>
    public static readonly IReadOnlyList<string> All = new[]
    {
        Gossip, Interaction, Jealousy, Preoccupation, PendingTopic, Gift, Milestone, Echo, Eavesdrop,
        SpouseWaiting, LocalPerception, Emotion, PlayerProfile, DateInvite, FollowProto,
        DateEndProto, Movement,
    };
}
