// SocialCrystallizationTests.cs
// VT-SOCIAL-03-Phase3 — 日结结晶与 Archetype 自愈契约测试（全路径 headless 可跑）。
//
// Headless 夹具：Farmer 桩仅回填 modData backing field（同 SocialGraphStoreTests 纪律）。
// SettleSpouseDailyMetrics 为纯静态逻辑，直接断言；SaveProfile/GetProfile 一致性测试
// 一律显式传 isMarried，规避 SpouseQueryService.IsMarried 内 getCharacterFromName 的
// headless 陷阱（该回退路径留待 in-game 验收）。

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Serialization;
using StardewValley;
using StardewValley.Mods;
using ValleytalkReborn;
using ValleytalkReborn.Social;
using Xunit;

public class SocialCrystallizationTests
{
    private static readonly FieldInfo ModDataField = FindModDataField();

    private static FieldInfo FindModDataField()
    {
        for (var t = typeof(Farmer); t != null && t != typeof(object); t = t.BaseType)
        {
            foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (f.FieldType == typeof(ModDataDictionary)) return f;
            }
        }
        return null;
    }

    private static Farmer NewFarmer()
    {
        var farmer = (Farmer)FormatterServices.GetUninitializedObject(typeof(Farmer));
        ModDataField.SetValue(farmer, new ModDataDictionary());
        return farmer;
    }

    // ── 测试 1：冷落/未交流 → Friction +12 / Distance +6，跨阈值自愈跃迁 ColdSpell ──

    [Fact]
    public void T1_NeglectAccumulatesFrictionAndSelfHealsToColdSpell()
    {
        var profile = new SocialProfile { UnresolvedFriction = 32, DomesticDistance = 20 };

        // 未对话（TalkedToToday == false）即触发累积。
        SocialCrystallizationManager.SettleSpouseDailyMetrics(profile, talkedToday: false, hasNeglectShock: false);
        Assert.Equal(44, profile.UnresolvedFriction);   // 32 + 12 = 44 >= 40
        Assert.Equal(26, profile.DomesticDistance);     // 20 + 6
        Assert.Equal(SocialArchetype.DomesticColdSpell, profile.Archetype); // 自愈跃迁

        // Neglect 冲击与未对话等效（今日有交流但携带冷落冲击，同样累积）。
        var shocked = new SocialProfile { UnresolvedFriction = 0, DomesticDistance = 0 };
        SocialCrystallizationManager.SettleSpouseDailyMetrics(shocked, talkedToday: true, hasNeglectShock: true);
        Assert.Equal(12, shocked.UnresolvedFriction);
        Assert.Equal(6, shocked.DomesticDistance);
        Assert.Equal(SocialArchetype.DomesticHarmonious, shocked.Archetype); // 12 < 40 仍和谐

        // 0~100 上限钳位。
        var saturated = new SocialProfile { UnresolvedFriction = 95, DomesticDistance = 99 };
        SocialCrystallizationManager.SettleSpouseDailyMetrics(saturated, talkedToday: false, hasNeglectShock: false);
        Assert.Equal(100, saturated.UnresolvedFriction);
        Assert.Equal(100, saturated.DomesticDistance);
        Assert.Equal(SocialArchetype.DomesticColdSpell, saturated.Archetype);
    }

    // ── 测试 2：陪伴交流 → Friction -15 / Distance -8，低于阈值自愈退回 Harmonious ──

    [Fact]
    public void T2_CompanionshipDecaysFrictionAndSelfHealsToHarmonious()
    {
        var profile = new SocialProfile { UnresolvedFriction = 45, DomesticDistance = 55 };

        SocialCrystallizationManager.SettleSpouseDailyMetrics(profile, talkedToday: true, hasNeglectShock: false);
        Assert.Equal(30, profile.UnresolvedFriction);   // 45 - 15 = 30 < 40
        Assert.Equal(47, profile.DomesticDistance);     // 55 - 8 = 47 < 50
        Assert.Equal(SocialArchetype.DomesticHarmonious, profile.Archetype); // 冰释前嫌

        // 0 下限钳位。
        var floor = new SocialProfile { UnresolvedFriction = 10, DomesticDistance = 5 };
        SocialCrystallizationManager.SettleSpouseDailyMetrics(floor, talkedToday: true, hasNeglectShock: false);
        Assert.Equal(0, floor.UnresolvedFriction);
        Assert.Equal(0, floor.DomesticDistance);
        Assert.Equal(SocialArchetype.DomesticHarmonious, floor.Archetype);
    }

    // ── 测试 3：SaveProfile / GetProfile 对 Archetype 字段的自动校准一致性 ──

    [Fact]
    public void T3_SaveGetProfile_AutoCalibratesArchetype()
    {
        var farmer = NewFarmer();

        // 写入自愈：存量 Archetype=Stranger（陈旧）+ 婚后摩擦 45 → 写入前被校准为 ColdSpell。
        SocialGraphService.Instance.SaveProfile(farmer, "Hakan",
            new SocialProfile { UnresolvedFriction = 45, Archetype = SocialArchetype.Stranger }, isMarried: true);
        var reloaded = SocialGraphService.Instance.GetProfile(farmer, "Hakan", 0, isMarried: true);
        Assert.Equal(SocialArchetype.DomesticColdSpell, reloaded.Archetype);

        // 读取自愈：未婚 80/80 但存量 Archetype=Stranger → 反序列化后被校准为 CloseConfidant。
        SocialGraphService.Instance.SaveProfile(farmer, "Hakan",
            new SocialProfile { Affection = 80, Trust = 80, Archetype = SocialArchetype.Stranger }, isMarried: false);
        var close = SocialGraphService.Instance.GetProfile(farmer, "Hakan", 0, isMarried: false);
        Assert.Equal(SocialArchetype.CloseConfidant, close.Archetype);

        // 冷启动播种自愈：6 心种子（Affection 60/Trust 40）服从九宫格重判 → Stranger
        // （CreateDefault 的 4~7 心 GuardedAcquaintance 标记与九宫格阈值本就不自洽，
        //   自愈后以九宫格为单一真理源）。
        var fresh = NewFarmer();
        var seeded = SocialGraphService.Instance.GetProfile(fresh, "Alex", 6);
        Assert.Equal(SocialArchetype.Stranger, seeded.Archetype);
        Assert.Equal(60, seeded.Affection);
        Assert.Equal(40, seeded.Trust);
    }
}
