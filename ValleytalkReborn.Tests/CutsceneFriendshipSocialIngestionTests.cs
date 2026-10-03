// CutsceneFriendshipSocialIngestionTests.cs
// VT-SOCIAL-04-Phase3.2 — 剧情好感结算 → 高维 SocialProfile 即时刺激分流契约测试。
// 验证：自然偶遇（isSerendipity=true）正向 delta 提升 Affection/Trust 并消减 Friction；
// 负向 delta 降低 Affection 并累加 Friction；F9 沙盒模式（isSerendipity=false）高维与
// 原版每日计数双零副作用；小额 delta 走 Max(1, rawDelta/5) 下限分支。
//
// Headless 夹具纪律（沿用 DailySalienceTests / SocialRelationBaseAssemblyTests）：
// - TestEnvironment.InstallHeadlessContext + WithWorldReady(true) 供给 Context 门禁。
// - FakePlayer.Install 提供带标量 Net 字段的 Farmer；modData 系 NetStringDictionary
//   不承 NetFieldBase，需反射回填（同 SocialGraphStoreTests）。
// - game1 垫片携带空 _locations：SpouseQueryService.IsMarried 回退路径中未包裹的
//   Game1.getCharacterFromName 经 ForEachLocation 空转后返回 null 而非 NRE。
// - FriendshipSettlementService.Instance.Reset() 隔离每日计数，防跨用例污染。

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Serialization;
using StardewValley;
using StardewValley.Mods;
using ValleytalkReborn;
using ValleytalkReborn.Cutscene.Services;
using ValleytalkReborn.Social;
using ValleytalkReborn.Tests;
using Xunit;
// StardewValley.Character 与 ValleytalkReborn.Character 二义性消解（本文件用 Farmer，无需别名，
// 但 GameLocation 等类型与 ValleytalkReborn 无冲突）。
using Farmer = StardewValley.Farmer;

[Collection("StaticGlobalStateCollection")]
public class CutsceneFriendshipSocialIngestionTests : IDisposable
{
    private static readonly FieldInfo Game1InstanceField =
        typeof(Game1).GetField("game1", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
    private static readonly FieldInfo ModDataBackingField = FindModDataBackingField();

    private readonly object _originalGame1;
    private readonly ModConfig _originalConfig;
    private readonly IDisposable _playerScope;

    public CutsceneFriendshipSocialIngestionTests()
    {
        _originalGame1 = Game1InstanceField?.GetValue(null);
        _originalConfig = ModEntry.Config;

        TestEnvironment.InstallHeadlessContext();

        // game1 垫片携带空 _locations（同 DailySalienceTests 纪律）。
        var shim = _originalGame1;
        if (shim == null)
        {
            shim = FormatterServices.GetUninitializedObject(typeof(Game1));
            Game1InstanceField?.SetValue(null, shim);
        }
        typeof(Game1).GetField("_locations", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?.SetValue(shim, new List<GameLocation>());

        _playerScope = FakePlayer.Install("虎彦");
        BackfillModData(Game1.player);

        ModEntry.Config = new ModConfig(); // EnableCutsceneFriendshipChange 默认 true
        FriendshipSettlementService.Instance.Reset();
    }

    public void Dispose()
    {
        FriendshipSettlementService.Instance.Reset();
        _playerScope?.Dispose();
        ModEntry.Config = _originalConfig;
        Game1InstanceField?.SetValue(null, _originalGame1);
    }

    private static FieldInfo FindModDataBackingField()
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

    private static void BackfillModData(Farmer farmer)
    {
        if (farmer != null)
            ModDataBackingField?.SetValue(farmer, new ModDataDictionary());
    }

    private static SocialProfile SeedProfile(int affection, int trust, int friction)
    {
        var profile = new SocialProfile { Affection = affection, Trust = trust, UnresolvedFriction = friction };
        SocialGraphService.Instance.SaveProfile(Game1.player, "Sam", profile, isMarried: false);
        return profile;
    }

    // ── T1 正向大额 delta（>=20）：Affection/Trust 上升，Friction 显著解冻，原版硬顶保持 ──

    [Fact]
    public void T1_PositiveLargeDelta_RaisesAffectionTrustAndMeltsFriction()
    {
        SeedProfile(affection: 40, trust: 40, friction: 45);

        TestEnvironment.WithWorldReady(true, () =>
        {
            FriendshipSettlementService.Instance.ApplyFriendshipDelta("Sam", 25, isSerendipity: true);
        });

        var profile = SocialGraphService.Instance.GetProfile(Game1.player, "Sam", isMarried: false);
        Assert.Equal(45, profile.Affection);          // 40 + max(1, 25/5=5)
        Assert.Equal(42, profile.Trust);              // 40 + 2（rawDelta >= 20）
        Assert.Equal(30, profile.UnresolvedFriction); // 45 - 15（送礼/好话解冻冷战）
        Assert.Equal(SocialArchetype.GuardedAcquaintance, profile.Archetype); // 45/42 → GA 自愈
        // 原版每日 ±20 硬顶不受影响：raw 25 → allowed 20。
        Assert.Equal(20, FriendshipSettlementService.Instance.GetDailyChange("Sam"));
    }

    // ── T2 负向 delta：Affection 下降（rawDelta/4），Friction 累加（|rawDelta|/2），Trust 不动 ──

    [Fact]
    public void T2_NegativeDelta_LowersAffectionAndAccumulatesFriction()
    {
        SeedProfile(affection: 60, trust: 40, friction: 10);

        TestEnvironment.WithWorldReady(true, () =>
        {
            FriendshipSettlementService.Instance.ApplyFriendshipDelta("Sam", -12, isSerendipity: true);
        });

        var profile = SocialGraphService.Instance.GetProfile(Game1.player, "Sam", isMarried: false);
        Assert.Equal(57, profile.Affection);          // 60 + (-12/4 = -3)
        Assert.Equal(40, profile.Trust);              // 负向不动 Trust
        Assert.Equal(16, profile.UnresolvedFriction); // 10 + |-12|/2
        Assert.Equal(-12, FriendshipSettlementService.Instance.GetDailyChange("Sam"));
    }

    // ── T3 F9 沙盒模式：高维档案与原版每日计数双零副作用 ──

    [Fact]
    public void T3_SandboxMode_ZeroSideEffects()
    {
        SeedProfile(affection: 40, trust: 40, friction: 45);

        TestEnvironment.WithWorldReady(true, () =>
        {
            FriendshipSettlementService.Instance.ApplyFriendshipDelta("Sam", 25, isSerendipity: false);
        });

        var profile = SocialGraphService.Instance.GetProfile(Game1.player, "Sam", isMarried: false);
        Assert.Equal(40, profile.Affection);
        Assert.Equal(40, profile.Trust);
        Assert.Equal(45, profile.UnresolvedFriction);
        Assert.Equal(0, FriendshipSettlementService.Instance.GetDailyChange("Sam"));
    }

    // ── T4 小额正向 delta（<20）：Max(1, rawDelta/5) 下限与轻量解冻分支 ──

    [Fact]
    public void T4_SmallPositiveDelta_UsesFloorAndLightThaw()
    {
        SeedProfile(affection: 40, trust: 40, friction: 45);

        TestEnvironment.WithWorldReady(true, () =>
        {
            FriendshipSettlementService.Instance.ApplyFriendshipDelta("Sam", 4, isSerendipity: true);
        });

        var profile = SocialGraphService.Instance.GetProfile(Game1.player, "Sam", isMarried: false);
        Assert.Equal(41, profile.Affection);          // 40 + max(1, 4/5=0) = +1 下限
        Assert.Equal(41, profile.Trust);              // 40 + 1（rawDelta < 20）
        Assert.Equal(40, profile.UnresolvedFriction); // 45 - 5
        Assert.Equal(4, FriendshipSettlementService.Instance.GetDailyChange("Sam"));
    }
}
