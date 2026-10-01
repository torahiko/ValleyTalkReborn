// SpouseQueryServiceTests.cs
// VT-CORE-02 — SpouseQueryService 跨 Mod 边界（BOUNDARY）查询的三级降级契约。
// 验证：SMAPI API 解析故障 / GetSpouses 抛错 / 反射字段读取故障 / SweetRooms 抛错
// 均以 Warn 落日志并就地熔断（置空 API 引用或关闭反射），后续查询确定性走下一级
// 降级，不再重复跨边界；API 正常时 official (all:false) 与 unofficial (all:true)
// 语义隔离；空参数走 RECOVERABLE 直接返回。无 HTTP 请求，无活动 LLM Provider。
//
// 无头事实（沿用 FarmPromptAccessTests 头注惯例）：
//   1) Game1.getCharacterFromName / getLocationFromName 需 Game1.game1 实例且
//      _locations 非空 —— 垫空 List<GameLocation> 保证遍历安全（getCharacterFromName
//      → ForEachCharacter → ForEachLocation；getLocationFromName → _locationLookup 空
//      + locations 空列表 → 返回 null）。
//   2) FakePlayer.Install 提供仅含 Name/标量 Net 字段的 Farmer（Game1.player 直读
//      _player 静态字段，null 安全）；friendshipData 不在补齐范围内，保持 null，
//      生产侧以 ?. 兜底；spouse 经 netSpouse 后备字段直接注入。
//   3) 反射桩继承 FieldInfo 抽象类，以可计数的 GetValue 模拟第三方字段
//      （成功返回字典 / 抛 InvalidOperationException），便于断言熔断后不再跨边界。
//   4) 程序集级 DisableTestParallelization 已开启（TestCollections.cs），
//      单例 ResetForTesting 无并发风险。
//   5) 测试工程无 MonoGame 编译期引用（headless 约束）：ISweetRoomsAPI 桩经
//      Reflection.Emit 动态生成（签名取自接口本身），Point/Vector2 一律以
//      Activator + 反射字段（X/Y/Item1/Item2）读写，不出现编译期 Xna 类型。

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.Serialization;
using Netcode;
using StardewModdingAPI;
using StardewModdingAPI.Framework.Logging;
using StardewValley;
using ValleytalkReborn;
using Xunit;

namespace ValleytalkReborn.Tests;

[Collection("StaticGlobalStateCollection")]
public class SpouseQueryServiceTests : IDisposable
{
    private const string PlayerName = "SpouseQueryFarmer";
    private const string OfficialSpouse = "Abigail";
    private const string UnofficialSpouse = "Sandy";

    private readonly RecordingMonitor _monitor = new();
    private readonly IMonitor _originalMonitor;
    private readonly object _originalGame1;
    private readonly FieldInfo _game1Field;
    private readonly IDisposable _playerScope;
    private readonly Farmer _player;

    public SpouseQueryServiceTests()
    {
        _originalMonitor = ModEntry.SMonitor;
        ModEntry.SMonitor = _monitor;
        SpouseQueryService.Instance.ResetForTesting();

        // Game1.game1 垫实例 + 空 _locations：getCharacterFromName 与
        // getLocationFromName 的无头降级均为确定性空遍历（见头注 1)）。
        _game1Field = typeof(Game1).GetField("game1",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        _originalGame1 = _game1Field?.GetValue(null);
        var game1 = (Game1)FormatterServices.GetUninitializedObject(typeof(Game1));
        typeof(Game1).GetField("_locations", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?.SetValue(game1, new List<GameLocation>());
        _game1Field?.SetValue(null, game1);

        _playerScope = FakePlayer.Install(PlayerName);
        _player = Game1.player;
    }

    public void Dispose()
    {
        SpouseQueryService.Instance.ResetForTesting();
        _playerScope?.Dispose();
        _game1Field?.SetValue(null, _originalGame1);
        ModEntry.SMonitor = _originalMonitor;
    }

    // ── 1. API 解析故障：ResolveApis 记录 Warn 并继续，不抛出 ──

    [Fact]
    public void ResolveApis_RegistryFault_LogsWarnForBothApis_AndStaysInactive()
    {
        // FakeModHelper.ModRegistry 直接抛 NotImplementedException ——
        // 每个候选 ModID 的 BOUNDARY 均被捕获为 Warn，循环不中断。
        SpouseQueryService.Instance.Initialize(new FakeModHelper("en"));

        var ex = Record.Exception(() => SpouseQueryService.Instance.ResolveApis());

        Assert.Null(ex);
        Assert.Contains(_monitor.Entries, e =>
            e.Level == LogLevel.Warn &&
            e.Message.Contains("Failed to resolve IPolyamorySweetApi from 'ApryllForever.PolyamorySweetLove'"));
        Assert.Contains(_monitor.Entries, e =>
            e.Level == LogLevel.Warn &&
            e.Message.Contains("Failed to resolve ISweetRoomsAPI"));
        Assert.False(SpouseQueryService.Instance.IsPolyamoryEnvironmentActive);
    }

    [Fact]
    public void ResolveApis_IsIdempotent_SecondCallDoesNotRetry()
    {
        SpouseQueryService.Instance.Initialize(new FakeModHelper("en"));
        SpouseQueryService.Instance.ResolveApis();
        var countAfterFirst = _monitor.Entries.Count;

        SpouseQueryService.Instance.ResolveApis();

        Assert.Equal(countAfterFirst, _monitor.Entries.Count);
    }

    // ── 2. GetSpouses 抛错：BOUNDARY 捕获 + 熔断，后续不再跨边界 ──

    [Fact]
    public void TryGetSpousesFromApi_Throws_LogsWarn_CircuitBreaks()
    {
        var api = new ThrowingPolyamoryApi();
        SpouseQueryService.Instance.InjectApisForTesting(api);

        Assert.False(SpouseQueryService.Instance.TryGetSpousesFromApi(_player, all: true, out var spouses));
        Assert.Null(spouses);
        Assert.Contains(_monitor.Entries, e =>
            e.Level == LogLevel.Warn &&
            e.Message.Contains("PolyamorySweet API GetSpouses(all=True) threw InvalidOperationException") &&
            e.Message.Contains("Disabling API integration"));

        // 熔断后：第二次探测直接短路，不再触碰 API。
        Assert.False(SpouseQueryService.Instance.TryGetSpousesFromApi(_player, all: false, out _));
        Assert.Equal(1, api.GetSpousesCallCount);
        Assert.False(SpouseQueryService.Instance.IsPolyamoryEnvironmentActive);
    }

    [Fact]
    public void IsMarried_ApiThrows_FallsThroughToVanilla_WithoutCrash()
    {
        SpouseQueryService.Instance.InjectApisForTesting(new ThrowingPolyamoryApi());
        SetSpouse(OfficialSpouse);

        Assert.True(SpouseQueryService.Instance.IsMarried(OfficialSpouse));
        Assert.False(SpouseQueryService.Instance.IsMarried("Penny"));
        Assert.Contains(_monitor.Entries, e =>
            e.Level == LogLevel.Warn && e.Message.Contains("Disabling API integration"));
    }

    // ── 3. API 正常：official (all:false) 与 unofficial (all:true) 语义隔离 ──

    [Fact]
    public void IsMarried_ApiResultShortCircuits_BeforeVanillaLookup()
    {
        var (official, unofficial) = MakeSpouseNpcs();
        SpouseQueryService.Instance.InjectApisForTesting(new StubPolyamoryApi(official, unofficial));

        Assert.True(SpouseQueryService.Instance.IsMarried(OfficialSpouse));
        Assert.True(SpouseQueryService.Instance.IsMarried(UnofficialSpouse));
        Assert.False(SpouseQueryService.Instance.IsMarried("Penny"));
    }

    [Fact]
    public void OfficialAndUnofficial_AreMutuallyExclusiveUnderApi()
    {
        var (official, unofficial) = MakeSpouseNpcs();
        SpouseQueryService.Instance.InjectApisForTesting(new StubPolyamoryApi(official, unofficial));

        Assert.True(SpouseQueryService.Instance.IsOfficialSpouse(official));
        Assert.False(SpouseQueryService.Instance.IsOfficialSpouse(unofficial));
        Assert.True(SpouseQueryService.Instance.IsUnofficialSpouse(unofficial));
        Assert.False(SpouseQueryService.Instance.IsUnofficialSpouse(official));
    }

    [Fact]
    public void GetAllMarriedNpcs_ApiPath_ReturnsAllSpouses()
    {
        var (official, unofficial) = MakeSpouseNpcs();
        SpouseQueryService.Instance.InjectApisForTesting(new StubPolyamoryApi(official, unofficial));

        var married = SpouseQueryService.Instance.GetAllMarriedNpcs(_player);

        Assert.Equal(new[] { OfficialSpouse, UnofficialSpouse },
            married.Select(n => n.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());
        // API 未抛错：不应出现熔断日志。
        Assert.DoesNotContain(_monitor.Entries, e => e.Message.Contains("Disabling API integration"));
    }

    // ── 4. 反射降级：成功路径（含非官方字段）与故障熔断 ──

    [Fact]
    public void ReflectionFallback_Success_OfficialAndUnofficialResolved()
    {
        var (official, unofficial) = MakeSpouseNpcs();
        var current = new StubFieldInfo("currentSpouses", () => new Dictionary<long, Dictionary<string, NPC>>
        {
            [_player.UniqueMultiplayerID] = new Dictionary<string, NPC> { [official.Name] = official }
        });
        var unofficialField = new StubFieldInfo("currentUnofficialSpouses", () => new Dictionary<long, Dictionary<string, NPC>>
        {
            [_player.UniqueMultiplayerID] = new Dictionary<string, NPC> { [unofficial.Name] = unofficial }
        });
        SpouseQueryService.Instance.InjectReflectionForTesting(current, unofficialField);

        Assert.True(SpouseQueryService.Instance.IsPolyamoryEnvironmentActive);
        Assert.True(SpouseQueryService.Instance.IsOfficialSpouse(official));
        Assert.True(SpouseQueryService.Instance.IsUnofficialSpouse(unofficial));

        var married = SpouseQueryService.Instance.GetAllMarriedNpcs(_player);
        Assert.Equal(new[] { OfficialSpouse, UnofficialSpouse },
            married.Select(n => n.Name).OrderBy(n => n, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void ReflectionFault_CircuitBreaks_AfterFirstGetValue()
    {
        var (official, _) = MakeSpouseNpcs();
        var faulted = new StubFieldInfo("currentSpouses",
            () => throw new InvalidOperationException("reflection fault"));
        SpouseQueryService.Instance.InjectReflectionForTesting(faulted, null);

        Assert.False(SpouseQueryService.Instance.IsOfficialSpouse(official));
        Assert.Equal(1, faulted.GetValueCount);
        Assert.Contains(_monitor.Entries, e =>
            e.Level == LogLevel.Warn &&
            e.Message.Contains("Failed to read reflection field 'currentSpouses'") &&
            e.Message.Contains("Disabling reflection fallback"));

        // 熔断后：第二次查询不再触碰字段，走原版降级（spouse 不匹配 → false）。
        Assert.False(SpouseQueryService.Instance.IsOfficialSpouse(official));
        Assert.Equal(1, faulted.GetValueCount);
        Assert.False(SpouseQueryService.Instance.IsPolyamoryEnvironmentActive);
    }

    [Fact]
    public void ReflectionFault_OnUnofficialField_CircuitBreaks_AndReturnsFalse()
    {
        var (official, _) = MakeSpouseNpcs();
        var faulted = new StubFieldInfo("currentUnofficialSpouses",
            () => throw new InvalidOperationException("unofficial fault"));
        SpouseQueryService.Instance.InjectReflectionForTesting(null, faulted);

        Assert.False(SpouseQueryService.Instance.IsUnofficialSpouse(official));
        Assert.Equal(1, faulted.GetValueCount);
        Assert.Contains(_monitor.Entries, e =>
            e.Level == LogLevel.Warn &&
            e.Message.Contains("Failed to read reflection field 'currentUnofficialSpouses'"));
        Assert.False(SpouseQueryService.Instance.IsPolyamoryEnvironmentActive);
    }

    // ── 5. 原版兜底：friendshipData（无头为 null，?. 兜底）与 spouse 字段 ──

    [Fact]
    public void IsMarried_VanillaFallback_MatchesSpouseField()
    {
        SetSpouse(OfficialSpouse);

        Assert.True(SpouseQueryService.Instance.IsMarried(OfficialSpouse));
        Assert.False(SpouseQueryService.Instance.IsMarried(UnofficialSpouse));
    }

    [Fact]
    public void GetAllMarriedNpcs_VanillaFallback_EmptyWhenCharacterUnresolvable()
    {
        SetSpouse(OfficialSpouse);

        var married = SpouseQueryService.Instance.GetAllMarriedNpcs(_player);

        Assert.Empty(married);
    }

    [Fact]
    public void GetAllMarriedNpcs_NoPlayer_ReturnsEmptyList()
    {
        Assert.Empty(SpouseQueryService.Instance.GetAllMarriedNpcs(player: null));
    }

    // ── 6. SweetRooms 边界：成功直通 / 抛错熔断并降级到 FarmHouse 保底 ──

    [Fact]
    public void GetHomeDestination_SweetRoomsSuccess_ReturnsCornerTile()
    {
        var sweetRooms = SweetRoomsStub.Emit(cornerTile: () => Activator.CreateInstance(PointType, 5, 6));
        SpouseQueryService.Instance.InjectApisForTesting(null, sweetRooms.Proxy);

        var tuple = InvokeGetHomeDestination(new NPC());

        Assert.Equal("FarmHouse", GetTupleItem<string>(tuple, "Item1"));
        Assert.Equal(5f, GetVectorComponent(tuple, "X"));
        Assert.Equal(6f, GetVectorComponent(tuple, "Y"));
    }

    [Fact]
    public void GetHomeDestination_SweetRoomsThrows_CircuitBreaks_AndFallsBackToFarmHouseEntry()
    {
        var sweetRooms = SweetRoomsStub.Emit(cornerTile: () =>
            throw new InvalidOperationException("sweet rooms boundary fault"));
        SpouseQueryService.Instance.InjectApisForTesting(null, sweetRooms.Proxy);
        var npc = new NPC();

        var tuple = InvokeGetHomeDestination(npc);

        // 空游戏环境下 FarmHouse 不存在 → 确定性 origin tile 保底。
        Assert.Equal("FarmHouse", GetTupleItem<string>(tuple, "Item1"));
        Assert.Equal(0f, GetVectorComponent(tuple, "X"));
        Assert.Equal(0f, GetVectorComponent(tuple, "Y"));
        Assert.Contains(_monitor.Entries, e =>
            e.Level == LogLevel.Warn &&
            e.Message.Contains("SweetRooms API threw InvalidOperationException") &&
            e.Message.Contains("Disabling SweetRooms API"));

        // 熔断后：第二次查询不再触碰 SweetRooms。
        InvokeGetHomeDestination(npc);
        Assert.Equal(1, sweetRooms.CornerCallCount);
    }

    // ── 7. RECOVERABLE：空参数直接返回，不触碰任何边界 ──

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void IsMarried_NullOrWhitespace_ReturnsFalse(string npcName)
    {
        Assert.False(SpouseQueryService.Instance.IsMarried(npcName));
    }

    [Fact]
    public void SpouseChecks_NullNpc_ReturnFalse()
    {
        Assert.False(SpouseQueryService.Instance.IsOfficialSpouse(null));
        Assert.False(SpouseQueryService.Instance.IsUnofficialSpouse(null));
    }

    // ── 桩与工具 ──

    private (NPC Official, NPC Unofficial) MakeSpouseNpcs()
    {
        var official = new NPC { Name = OfficialSpouse };
        var unofficial = new NPC { Name = UnofficialSpouse };
        return (official, unofficial);
    }

    private void SetSpouse(string spouseName)
    {
        typeof(Farmer).GetField("netSpouse", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?.SetValue(_player, new NetString(spouseName));
    }

    /// <summary>Point 的运行时类型（测试工程无 MonoGame 编译期引用，见头注 5)）。</summary>
    private static Type PointType =>
        typeof(ISweetRoomsAPI).GetMethod(nameof(ISweetRoomsAPI.GetSpouseRoomCornerTile)).ReturnType;

    /// <summary>
    /// 经反射调用 GetHomeDestination，规避返回元组 Item2（Vector2）的编译期
    /// MonoGame 引用；以 object 形式持有元组。
    /// </summary>
    private static object InvokeGetHomeDestination(NPC npc) =>
        typeof(SpouseQueryService)
            .GetMethod(nameof(SpouseQueryService.GetHomeDestination))
            .Invoke(SpouseQueryService.Instance, new object[] { npc });

    private static T GetTupleItem<T>(object tuple, string fieldName) =>
        (T)tuple.GetType().GetField(fieldName).GetValue(tuple);

    /// <summary>读取元组 Item2（Vector2/Point 桩值）的 X / Y float 字段。</summary>
    private static float GetVectorComponent(object tuple, string fieldName) =>
        (float)tuple.GetType().GetField("Item2").FieldType
            .GetField(fieldName).GetValue(GetTupleItem<object>(tuple, "Item2"));

    /// <summary>
    /// Reflection.Emit 生成的 ISweetRoomsAPI 桩：GetSpouseRoomCornerTile 委托给
    /// cornerTile（返回装箱 Point 或抛错）并计数，其余成员抛 NotImplementedException。
    /// 编译期零 MonoGame 引用（见头注 5)）。
    /// </summary>
    private sealed class SweetRoomsStub
    {
        public int CornerCallCount;

        /// <summary>Reflection.Emit 生成的 ISweetRoomsAPI 代理实例。</summary>
        public ISweetRoomsAPI Proxy;

        public static SweetRoomsStub Emit(Func<object> cornerTile)
        {
            var stub = new SweetRoomsStub();
            Func<object> counted = () =>
            {
                stub.CornerCallCount++;
                return cornerTile();
            };

            var asm = AssemblyBuilder.DefineDynamicAssembly(
                new AssemblyName($"SweetRoomsStub_{Guid.NewGuid():N}"), AssemblyBuilderAccess.Run);
            var tb = asm.DefineDynamicModule("Stub").DefineType(
                "SweetRoomsStubType",
                TypeAttributes.Public | TypeAttributes.Sealed | TypeAttributes.Class);
            tb.AddInterfaceImplementation(typeof(ISweetRoomsAPI));

            var cornerField = tb.DefineField("_corner", typeof(Func<object>),
                FieldAttributes.Public | FieldAttributes.Static);

            foreach (var mi in typeof(ISweetRoomsAPI).GetMethods())
            {
                var mb = tb.DefineMethod(mi.Name,
                    MethodAttributes.Public | MethodAttributes.Virtual | MethodAttributes.Final | MethodAttributes.NewSlot,
                    mi.ReturnType, mi.GetParameters().Select(p => p.ParameterType).ToArray());
                var il = mb.GetILGenerator();

                if (mi.Name == nameof(ISweetRoomsAPI.GetSpouseRoomCornerTile))
                {
                    il.Emit(OpCodes.Ldsfld, cornerField);
                    il.EmitCall(OpCodes.Callvirt, typeof(Func<object>).GetMethod("Invoke"), null);
                    il.Emit(OpCodes.Unbox_Any, mi.ReturnType);
                    il.Emit(OpCodes.Ret);
                }
                else
                {
                    var ctor = typeof(NotImplementedException).GetConstructor(Type.EmptyTypes);
                    il.Emit(OpCodes.Newobj, ctor);
                    il.Emit(OpCodes.Throw);
                }
                tb.DefineMethodOverride(mb, mi);
            }

        var type = tb.CreateType();
        type.GetField("_corner").SetValue(null, counted);
        stub.Proxy = (ISweetRoomsAPI)Activator.CreateInstance(type);
        return stub;
    }
    }

    private sealed class StubPolyamoryApi : IPolyamorySweetApi
    {
        private readonly Dictionary<string, NPC> _official;
        private readonly Dictionary<string, NPC> _unofficial;

        public StubPolyamoryApi(NPC official, NPC unofficial)
        {
            _official = new Dictionary<string, NPC> { [official.Name] = official };
            _unofficial = new Dictionary<string, NPC> { [unofficial.Name] = unofficial };
        }

        public Dictionary<string, NPC> GetSpouses(Farmer farmer, bool all = false)
        {
            if (!all) return new Dictionary<string, NPC>(_official);

            var merged = new Dictionary<string, NPC>(_official);
            foreach (var kv in _unofficial)
                if (!merged.ContainsKey(kv.Key)) merged[kv.Key] = kv.Value;
            return merged;
        }
    }

    private sealed class ThrowingPolyamoryApi : IPolyamorySweetApi
    {
        public int GetSpousesCallCount;

        public Dictionary<string, NPC> GetSpouses(Farmer farmer, bool all = false)
        {
            GetSpousesCallCount++;
            throw new InvalidOperationException("api boundary fault");
        }
    }

    /// <summary>可计数的反射字段桩：getter 返回值或抛错由测试注入。</summary>
    private sealed class StubFieldInfo : FieldInfo
    {
        private readonly Func<object> _getter;

        public StubFieldInfo(string name, Func<object> getter)
        {
            Name = name;
            _getter = getter;
        }

        public int GetValueCount;

        public override string Name { get; }
        public override MemberTypes MemberType => MemberTypes.Field;
        public override Type DeclaringType => typeof(SpouseQueryServiceTests);
        public override Type ReflectedType => typeof(SpouseQueryServiceTests);
        public override FieldAttributes Attributes => FieldAttributes.Public | FieldAttributes.Static;
        public override RuntimeFieldHandle FieldHandle => throw new NotSupportedException();
        public override Type FieldType => typeof(Dictionary<long, Dictionary<string, NPC>>);

        public override object GetValue(object obj)
        {
            GetValueCount++;
            return _getter();
        }

        public override void SetValue(object obj, object value, BindingFlags invokeAttr, Binder binder, CultureInfo culture) { }
        public override object GetRawConstantValue() => throw new NotSupportedException();
        public override object[] GetCustomAttributes(bool inherit) => Array.Empty<object>();
        public override object[] GetCustomAttributes(Type attributeType, bool inherit) => Array.Empty<object>();
        public override bool IsDefined(Type attributeType, bool inherit) => false;
    }

    private sealed class RecordingMonitor : IMonitor
    {
        public List<(string Message, LogLevel Level)> Entries { get; } = new();

        public bool IsVerbose => false;

        public void Log(string message, LogLevel level) => Entries.Add((message, level));

        public void LogOnce(string message, LogLevel level) => Entries.Add((message, level));

        public void VerboseLog(string message) { }

        public void VerboseLog(ref VerboseLogStringHandler handler) { }
    }
}
