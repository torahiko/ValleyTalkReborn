// FarmGreenhouseObservationTests.cs
// VT-FARM-OBSERVE-05 — greenhouse on-site observation.
// Verifies: CanObserveGreenhouse is a pure permission check (routing allowed +
// non-null NPC location + ReferenceEquals with the player location + native
// GameLocation.IsGreenhouse — never name-based), so the same-named but distinct
// greenhouse instance and any shared non-greenhouse room grant nothing;
// BuildObservedGreenhouseSummary scans only the passed location once, wraps the
// CURRENT_OBSERVATION source marker in the existing farm_state tags, states the
// empty room verbatim, reuses BuildGreenhouseSection formatting when planting
// facts exist, omits unresolvable varieties while keeping counts, and never
// touches the seven full-farm cache fields. Same date + same room + empty →
// dead crop added → crop removed must flip the output accordingly.
//
// 本文件是无头单测锚点，不是游戏内视野或模型台词验收。
//
// 无头事实（全部经反编译源核实）：
//   1) GameLocation.terrainFeatures 是 NetVector2Dictionary<TerrainFeature,
//      NetRef<TerrainFeature>>（非 OverlaidDictionary）；其 Pairs 枚举 FieldDict
//      并按 NetRef<TerrainFeature>.Value 取值，因此直接向 FieldDict 写
//      NetRef 条目即可被 ScanCropsInLocation 枚举。
//   2) HoeDirt.crop 是属性，后备私有字段 netCrop（NetRef<Crop>）；未初始化
//      HoeDirt 只需补 netCrop，避免触发构造器的贴图加载。
//   3) Crop.isWildSeedCrop() 读 overrideTexturePath.Value 与 rowInSpriteSheet.Value；
//      未初始化 Crop 必须补齐这两个 Net 字段，否则枚举即 NRE。
//      indexOfHarvest 给非空 ID 以跳过 Crop.GetData() 的内容加载路径；
//      ID 未注册时 SafeGetDisplayName 返回 null，品种按契约省略。
//   4) Character.currentLocation 由基类 protected readonly currentLocationRef
//      （NetLocationRef）承载，NPC 与 Farmer 均无 override。NetLocationRef.Set
//      会读 location.isStructure 与 NameOrUniqueName（未初始化地点为 null），
//      因此测试直接写 ref 的 _gameLocation 并复位 _dirty，绕过名称解析。
//   5) GameLocation.IsGreenhouse 是 NetBool isGreenhouse 的属性包装，
//      未初始化地点直接注入 NetBool(true/false)，不依赖固定地图名。

using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Serialization;
using Netcode;
using StardewValley;
using StardewValley.Network;
using StardewValley.TerrainFeatures;
using ValleytalkReborn;
using Xunit;

[Collection("StaticGlobalStateCollection")]
public class FarmGreenhouseObservationTests
{
    // ── 1. 权限真值表：纯判定，原生 IsGreenhouse，实例同一性按 ReferenceEquals ──

    [Fact]
    public void SameGreenhouseInstance_GrantsPermission()
    {
        var greenhouse = GreenhouseObservationTestWorld.MakeGameLocation(isGreenhouse: true);
        Assert.True(Prompts.CanObserveGreenhouse(true, greenhouse, greenhouse));
    }

    [Fact]
    public void DifferentGreenhouseInstances_DenyPermission()
    {
        var npcRoom = GreenhouseObservationTestWorld.MakeGameLocation(isGreenhouse: true);
        var playerRoom = GreenhouseObservationTestWorld.MakeGameLocation(isGreenhouse: true);
        Assert.False(Prompts.CanObserveGreenhouse(true, npcRoom, playerRoom));
    }

    [Fact]
    public void SameNameDifferentInstances_DenyPermission()
    {
        var npcRoom = GreenhouseObservationTestWorld.MakeGameLocation(isGreenhouse: true, name: "Greenhouse");
        var playerRoom = GreenhouseObservationTestWorld.MakeGameLocation(isGreenhouse: true, name: "Greenhouse");
        Assert.NotSame(npcRoom, playerRoom);
        Assert.False(Prompts.CanObserveGreenhouse(true, npcRoom, playerRoom));
    }

    [Fact]
    public void SameNonGreenhouseRoom_DenyPermission()
    {
        var room = GreenhouseObservationTestWorld.MakeGameLocation(isGreenhouse: false, name: "FarmHouse");
        Assert.False(Prompts.CanObserveGreenhouse(true, room, room));
    }

    [Fact]
    public void NullNpcLocation_DenyPermission()
    {
        var greenhouse = GreenhouseObservationTestWorld.MakeGameLocation(isGreenhouse: true);
        Assert.False(Prompts.CanObserveGreenhouse(true, null, greenhouse));
    }

    [Fact]
    public void NullPlayerLocation_DenyPermission()
    {
        var greenhouse = GreenhouseObservationTestWorld.MakeGameLocation(isGreenhouse: true);
        Assert.False(Prompts.CanObserveGreenhouse(true, greenhouse, null));
    }

    [Fact]
    public void RoutingOff_DenyPermission_EvenInsideSameGreenhouse()
    {
        var greenhouse = GreenhouseObservationTestWorld.MakeGameLocation(isGreenhouse: true);
        Assert.False(Prompts.CanObserveGreenhouse(false, greenhouse, greenhouse));
    }

    // ── 2. 现场观察新鲜度：同日期同地点 空 → 枯萎作物 → 移除；输出随扫描变化，
    //        七个全农场缓存字段始终保持原值 ──

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ObservedSummary_EmptyThenDeadCropThenRemoved_TracksRoom_AndNeverWritesFarmCache(bool isZh)
    {
        Dictionary<string, object> before = GreenhouseObservationTestWorld.SnapshotFarmCacheFields();
        var greenhouse = GreenhouseObservationTestWorld.MakeGameLocation(isGreenhouse: true);

        string empty = FarmStateScanner.BuildObservedGreenhouseSummary(isZh, greenhouse);
        Assert.Contains(isZh ? "温室内目前没有作物或果树。" : "The greenhouse currently contains no crops or fruit trees.",
            empty, StringComparison.Ordinal);
        Assert.Contains("<farm_state>", empty, StringComparison.Ordinal);
        Assert.Contains("</farm_state>", empty, StringComparison.Ordinal);
        Assert.Contains("CURRENT_OBSERVATION", empty, StringComparison.Ordinal);
        // 不调用完整农场摘要：既无其标题，也无枯萎/成熟行
        Assert.DoesNotContain(isZh ? "农场经营状态" : "FARM OPERATION STATUS", empty, StringComparison.Ordinal);
        Assert.DoesNotContain(isZh ? "枯萎" : "withered", empty, StringComparison.Ordinal);

        var deadDirt = GreenhouseObservationTestWorld.MakeHoeDirtWithDeadCrop();
        object tile = GreenhouseObservationTestWorld.MakeVector2(2f, 3f);
        GreenhouseObservationTestWorld.AddTerrainFeature(greenhouse, tile, deadDirt);

        string withDead = FarmStateScanner.BuildObservedGreenhouseSummary(isZh, greenhouse);
        Assert.Contains(isZh ? "温室作物异常" : "Greenhouse Crop Warning", withDead, StringComparison.Ordinal);
        Assert.Contains(isZh ? "枯萎死去的作物" : "withered crops spotted", withDead, StringComparison.Ordinal);
        Assert.DoesNotContain(isZh ? "温室内目前没有作物或果树" : "The greenhouse currently contains no crops or fruit trees.",
            withDead, StringComparison.Ordinal);

        GreenhouseObservationTestWorld.RemoveTerrainFeature(greenhouse, tile);

        string removed = FarmStateScanner.BuildObservedGreenhouseSummary(isZh, greenhouse);
        Assert.Contains(isZh ? "温室内目前没有作物或果树。" : "The greenhouse currently contains no crops or fruit trees.",
            removed, StringComparison.Ordinal);
        Assert.DoesNotContain(isZh ? "温室作物异常" : "Greenhouse Crop Warning", removed, StringComparison.Ordinal);

        Dictionary<string, object> after = GreenhouseObservationTestWorld.SnapshotFarmCacheFields();
        Assert.Equal(before, after);
    }

    // ── 3. 品种无法解析但计数有效：保留状态行，省略品种样例 ──

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ObservedSummary_UnresolvableVariety_KeepsCountLine_OmitsSample(bool isZh)
    {
        var greenhouse = GreenhouseObservationTestWorld.MakeGameLocation(isGreenhouse: true);
        GreenhouseObservationTestWorld.AddTerrainFeature(
            greenhouse, GreenhouseObservationTestWorld.MakeVector2(5f, 5f),
            GreenhouseObservationTestWorld.MakeHoeDirtWithDeadCrop());

        string observed = FarmStateScanner.BuildObservedGreenhouseSummary(isZh, greenhouse);

        Assert.Contains(isZh ? "温室作物异常" : "Greenhouse Crop Warning", observed, StringComparison.Ordinal);
        Assert.DoesNotContain(isZh ? "包含" : "including", observed, StringComparison.Ordinal);
        Assert.DoesNotContain(isZh ? "温室内目前没有作物或果树" : "currently contains no crops",
            observed, StringComparison.Ordinal);
    }

    // ── 4. 存在种植事实：复用温室分区种植格式，不出现修复状态行 ──

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ObservedSummary_WithDeadCrop_UsesGreenhouseSectionFormat_NoRepairLine(bool isZh)
    {
        var greenhouse = GreenhouseObservationTestWorld.MakeGameLocation(isGreenhouse: true);
        GreenhouseObservationTestWorld.AddTerrainFeature(
            greenhouse, GreenhouseObservationTestWorld.MakeVector2(1f, 1f),
            GreenhouseObservationTestWorld.MakeHoeDirtWithDeadCrop());

        string observed = FarmStateScanner.BuildObservedGreenhouseSummary(isZh, greenhouse);

        // BuildGreenhouseSection 的种植格式（异常行措辞）直接出现在观察文本中
        Assert.Contains(isZh ? "枯萎死去的作物" : "withered crops spotted", observed, StringComparison.Ordinal);
        Assert.Contains(isZh ? "温室作物异常" : "Greenhouse Crop Warning", observed, StringComparison.Ordinal);
        // 未解锁/地点不可用的两行修复状态话术不得出现
        Assert.DoesNotContain(isZh ? "破损废弃" : "Dilapidated and abandoned", observed, StringComparison.Ordinal);
        Assert.DoesNotContain(isZh ? "无法确认内部种植情况" : "currently unavailable", observed, StringComparison.Ordinal);
    }
}

/// <summary>
/// VT-FARM-OBSERVE-05 无头装配助手：未初始化 GameLocation/NPC/Crop/HoeDirt 与
/// currentLocationRef 注入。全部为测试桩，生产代码不做任何兜底。
/// </summary>
internal static class GreenhouseObservationTestWorld
{
    private static readonly string[] FarmCacheFieldNames =
    {
        "_cachedSummaryZh", "_cachedSummaryEn",
        "_cachedSummaryWithoutGreenhouseZh", "_cachedSummaryWithoutGreenhouseEn",
        "_cachedYear", "_cachedSeason", "_cachedDay",
    };

    // 测试工程编译期不引用 MonoGame（既有约束）；Vector2 以基类
    // NetFieldDictionary 的 TKey 泛型实参在运行期解析。
    private static readonly Type Vector2Type =
        typeof(NetVector2Dictionary<TerrainFeature, NetRef<TerrainFeature>>)
            .BaseType.GetGenericArguments()[0];

    /// <summary>装箱 Vector2 键（float x, float y），值相等语义由 Vector2.Equals 保证。</summary>
    internal static object MakeVector2(float x, float y) =>
        Activator.CreateInstance(Vector2Type, x, y);

    /// <summary>未初始化 GameLocation：仅补 isGreenhouse / name / terrainFeatures 三个被读成员。</summary>
    internal static GameLocation MakeGameLocation(bool isGreenhouse, string name = null)
    {
        var location = (GameLocation)FormatterServices.GetUninitializedObject(typeof(GameLocation));
        SetField(location, "isGreenhouse", new NetBool(isGreenhouse));
        if (name != null)
        {
            SetField(location, "name", new NetString(name));
        }
        SetField(location, "terrainFeatures",
            new NetVector2Dictionary<TerrainFeature, NetRef<TerrainFeature>>());
        return location;
    }

    internal static void AddTerrainFeature(GameLocation location, object tile, TerrainFeature feature)
    {
        // FieldDict 的编译期签名携带 MonoGame 的 Vector2（测试工程不引用 MonoGame），
        // 因此 terrainFeatures 与其 FieldDict 均按运行期反射读取，再经非泛型
        // IDictionary 写入装箱键；枚举侧 Pairs 按 Vector2.Equals 命中。
        ((IDictionary)ReadTerrainFeaturesFieldDict(location))[tile] = new NetRef<TerrainFeature>(feature);
    }

    internal static void RemoveTerrainFeature(GameLocation location, object tile)
    {
        IDictionary fieldDict = (IDictionary)ReadTerrainFeaturesFieldDict(location);
        Assert.True(fieldDict.Contains(tile));
        fieldDict.Remove(tile);
    }

    private static object ReadTerrainFeaturesFieldDict(GameLocation location)
    {
        object terrainFeatures = typeof(GameLocation)
            .GetField("terrainFeatures", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?.GetValue(location);
        Assert.NotNull(terrainFeatures);
        return terrainFeatures.GetType()
            .GetProperty("FieldDict", BindingFlags.Instance | BindingFlags.Public)
            ?.GetValue(terrainFeatures);
    }

    /// <summary>未初始化 HoeDirt：补 netCrop（NetRef&lt;Crop&gt;），绕开构造器贴图加载。</summary>
    internal static HoeDirt MakeHoeDirtWithDeadCrop()
    {
        var dirt = (HoeDirt)FormatterServices.GetUninitializedObject(typeof(HoeDirt));
        SetField(dirt, "netCrop", new NetRef<Crop>(MakeDeadCrop()));
        return dirt;
    }

    /// <summary>未初始化 Crop：补齐扫描路径读取的 Net 字段（见文件头注 3）。</summary>
    private static Crop MakeDeadCrop()
    {
        var crop = (Crop)FormatterServices.GetUninitializedObject(typeof(Crop));
        SetField(crop, "dead", new NetBool(true));
        SetField(crop, "currentPhase", new NetInt(0));
        SetField(crop, "phaseDays", new NetIntList());
        SetField(crop, "indexOfHarvest", new NetString("VT_OBS_UNREGISTERED_ID"));
        SetField(crop, "rowInSpriteSheet", new NetInt(0));
        SetField(crop, "overrideTexturePath", new NetString());
        return crop;
    }

    /// <summary>未初始化 NPC，带一个非温室默认地点引用。</summary>
    internal static NPC MakeHeadlessNpc()
    {
        var npc = (NPC)FormatterServices.GetUninitializedObject(typeof(NPC));
        InstallLocationRef(npc, MakeGameLocation(isGreenhouse: false, name: "FarmHouse"));
        return npc;
    }

    /// <summary>
    /// 把 Character（含 Farmer 派生）的 currentLocationRef 指向传入地点实例。
    /// 直接写 ref 的 _gameLocation 并复位 _dirty：NetLocationRef.Set 会读
    /// location.isStructure 与 NameOrUniqueName，未初始化地点两者皆空（见头注 4）。
    /// </summary>
    internal static void InstallLocationRef(StardewValley.Character character, GameLocation location)
    {
        var locationRef = new NetLocationRef();
        SetField(locationRef, "_gameLocation", location);
        SetField(locationRef, "_dirty", false);
        SetField(character, "currentLocationRef", locationRef);
    }

    internal static Dictionary<string, object> SnapshotFarmCacheFields()
    {
        var snapshot = new Dictionary<string, object>();
        foreach (var name in FarmCacheFieldNames)
        {
            var field = typeof(FarmStateScanner).GetField(name, BindingFlags.Static | BindingFlags.NonPublic)
                ?? throw new Xunit.Sdk.XunitException($"FarmStateScanner.{name} not found.");
            snapshot[name] = field.GetValue(null);
        }
        return snapshot;
    }

    private static void SetField(object target, string fieldName, object value)
    {
        var field = target.GetType().GetField(fieldName,
            BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field.SetValue(target, value);
    }
}
