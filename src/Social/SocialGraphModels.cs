// SocialGraphModels.cs
// VT-SOCIAL-01-Phase1-ShadowGraph — 高维寄生影子层（Shadow Observer）的数据契约。
// 纯只读影子建模：ModData 单一数据源，零 friendshipData 写入、零 Harmony、零静态缓存。

using System;
using System.Collections.Generic;

namespace ValleytalkReborn.Social;

/// <summary>
/// 九宫格离散原型：未婚由 Affection × Trust 交叉判定，婚后由专用指标（疏离/未解摩擦）接管。
/// </summary>
public enum SocialArchetype
{
    Stranger,              // 陌生疏离
    GuardedAcquaintance,   // 礼貌熟络（高好感，低信任，假面亲热）
    ReluctantConfidant,    // 沉重托付（低好感，高信任，嘴硬心软）
    CloseConfidant,        // 莫逆知己（高好感，高信任）
    DomesticHarmonious,    // 婚后和谐
    DomesticColdSpell,     // 婚后单日冷战摩擦
    DomesticRoommate       // 婚后室友化疏离
}

/// <summary>
/// 单个 NPC 对农夫的心理档案（随 farmer.modData 持久化、跨存档同步）。
/// 字段域：Affection/Trust 0~100，Tension -50~+50，DomesticDistance/UnresolvedFriction 0~100。
/// </summary>
public sealed class SocialProfile
{
    public int Affection { get; set; }            // 0 ~ 100
    public int Trust { get; set; }                // 0 ~ 100
    public int Tension { get; set; }              // -50 ~ +50
    public int DomesticDistance { get; set; }     // 0 ~ 100 (婚后专用)
    public int UnresolvedFriction { get; set; }   // 0 ~ 100 (婚后专用)
    public SocialArchetype Archetype { get; set; }
    public List<string> SalientMemories { get; set; } = new();

    /// <summary>
    /// 旧存档冷启动：按原版心级与婚姻状态做一次性确定性映射，杜绝旧档断层。
    /// </summary>
    public static SocialProfile CreateDefault(int vanillaHearts = 0, bool isMarried = false)
    {
        var profile = new SocialProfile();
        if (isMarried)
        {
            profile.Affection = 85;
            profile.Trust = 85;
            profile.Tension = 0;
            profile.DomesticDistance = 0;
            profile.UnresolvedFriction = 0;
            profile.Archetype = SocialArchetype.DomesticHarmonious;
            return profile;
        }
        if (vanillaHearts >= 8)
        {
            profile.Affection = Math.Clamp(vanillaHearts * 10, 0, 100);
            profile.Trust = 70;
            profile.Archetype = SocialArchetype.CloseConfidant;
            return profile;
        }
        if (vanillaHearts >= 4)
        {
            profile.Affection = Math.Clamp(vanillaHearts * 10, 0, 100);
            profile.Trust = 40;
            profile.Archetype = SocialArchetype.GuardedAcquaintance;
            return profile;
        }
        profile.Affection = 10;
        profile.Trust = 10;
        profile.Tension = 0;
        profile.Archetype = SocialArchetype.Stranger;
        return profile;
    }
}
