// SocialGraphStoreTests.cs
// VT-SOCIAL-01-Phase1-ShadowGraph — 影子层存储与契约单测（全路径 headless 可跑）。
//
// Headless 约定（repo-wide，见 TestFakes.cs 头注）：
// - Farmer 用 FormatterServices.GetUninitializedObject 构造，仅回填 Character.modData
//   的私有 backing field（get-only 属性），服务路径不触碰其它 Net 字段。
// - 日志断言通过 Log.Initialize(capture) 换入捕获 monitor，try/finally 内还原
//   （程序集已 DisableTestParallelization，进程级静态无并发竞争）。

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Mods;
using ValleytalkReborn;
using ValleytalkReborn.Social;
using Xunit;

public class SocialGraphStoreTests
{
    private readonly IMonitor _originalLogMonitor;

    public SocialGraphStoreTests()
    {
        _originalLogMonitor = Log.Logger.Monitor;
    }

    // ── Headless Farmer 桩：仅需 modData 可读写 ──

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

    private static bool HasKey(Farmer farmer, string npcName)
    {
        return farmer.modData.TryGetValue(SocialGraphService.ModDataPrefix + npcName, out _);
    }

    private sealed class RecordingMonitor : IMonitor
    {
        public readonly List<(LogLevel Level, string Message)> Entries = new();

        public bool IsVerbose => false;

        public void Log(string message, LogLevel level) => Entries.Add((level, message ?? string.Empty));

        public void LogOnce(string message, LogLevel level) => Log(message, level);

        public void VerboseLog(string message) { }

        public void VerboseLog(ref StardewModdingAPI.Framework.Logging.VerboseLogStringHandler handler) { }
    }

    private RecordingMonitor InstallCapture()
    {
        var capture = new RecordingMonitor();
        Log.Initialize(capture);
        return capture;
    }

    // ── UT-01 CreateDefault：原版心级 / 婚姻 → 确定性映射 ──

    [Fact]
    public void UT01_CreateDefault_HeartsAndMarriageMapping()
    {
        var stranger = SocialProfile.CreateDefault(3);
        Assert.Equal(SocialArchetype.Stranger, stranger.Archetype);
        Assert.Equal(10, stranger.Affection);
        Assert.Equal(10, stranger.Trust);
        Assert.Equal(0, stranger.Tension);
        Assert.Empty(stranger.SalientMemories);

        var guarded4 = SocialProfile.CreateDefault(4);
        Assert.Equal(SocialArchetype.GuardedAcquaintance, guarded4.Archetype);
        Assert.Equal(40, guarded4.Affection);
        Assert.Equal(40, guarded4.Trust);

        var guarded7 = SocialProfile.CreateDefault(7);
        Assert.Equal(SocialArchetype.GuardedAcquaintance, guarded7.Archetype);
        Assert.Equal(70, guarded7.Affection);
        Assert.Equal(40, guarded7.Trust);

        var close8 = SocialProfile.CreateDefault(8);
        Assert.Equal(SocialArchetype.CloseConfidant, close8.Archetype);
        Assert.Equal(80, close8.Affection);
        Assert.Equal(70, close8.Trust);

        var clamped = SocialProfile.CreateDefault(20);
        Assert.Equal(100, clamped.Affection);
        Assert.Equal(SocialArchetype.CloseConfidant, clamped.Archetype);

        var married = SocialProfile.CreateDefault(isMarried: true);
        Assert.Equal(SocialArchetype.DomesticHarmonious, married.Archetype);
        Assert.Equal(85, married.Affection);
        Assert.Equal(85, married.Trust);
        Assert.Equal(0, married.DomesticDistance);
        Assert.Equal(0, married.UnresolvedFriction);
    }

    // ── UT-02 初始加载回退：键缺失 → 播种默认并写回 ModData ──

    [Fact]
    public void UT02_GetProfile_MissingKeySeedsDefaultAndWritesBack()
    {
        var farmer = NewFarmer();
        Assert.False(HasKey(farmer, "Alex"));

        // VT-SOCIAL-03 自愈后种子原型服从九宫格：8 心（80/70）→ CloseConfidant。
        var profile = SocialGraphService.Instance.GetProfile(farmer, "Alex", 8, isMarried: false);

        Assert.Equal(SocialArchetype.CloseConfidant, profile.Archetype);
        Assert.Equal(80, profile.Affection);
        Assert.Equal(70, profile.Trust);
        Assert.True(HasKey(farmer, "Alex"));

        // 二次读取从 ModData 反序列化，数值一致（单一数据源证明）。
        var reloaded = SocialGraphService.Instance.GetProfile(farmer, "Alex", 8, isMarried: false);
        Assert.Equal(profile.Affection, reloaded.Affection);
        Assert.Equal(profile.Trust, reloaded.Trust);
        Assert.Equal(profile.Archetype, reloaded.Archetype);
    }

    // ── UT-03 序列化/反序列化正确性：全字段往返 ──

    [Fact]
    public void UT03_SaveThenGet_RoundTripsAllFields()
    {
        var farmer = NewFarmer();
        var saved = new SocialProfile
        {
            Affection = 72,
            Trust = 31,
            Tension = -20,
            DomesticDistance = 55,
            UnresolvedFriction = 10,
            Archetype = SocialArchetype.DomesticRoommate,
            SalientMemories = new List<string> { "雨天修屋顶", "生日惊喜" }
        };

        // VT-SOCIAL-03：显式 isMarried 让自愈判定与语义一致（摩擦 10 < 40、疏离 55 >= 50 → Roommate 保持）。
        SocialGraphService.Instance.SaveProfile(farmer, "Hakan", saved, isMarried: true);
        var loaded = SocialGraphService.Instance.GetProfile(farmer, "Hakan", 0, isMarried: true);

        Assert.Equal(72, loaded.Affection);
        Assert.Equal(31, loaded.Trust);
        Assert.Equal(-20, loaded.Tension);
        Assert.Equal(55, loaded.DomesticDistance);
        Assert.Equal(10, loaded.UnresolvedFriction);
        Assert.Equal(SocialArchetype.DomesticRoommate, loaded.Archetype);
        Assert.Equal(new List<string> { "雨天修屋顶", "生日惊喜" }, loaded.SalientMemories);
    }

    // ── UT-04 紧凑 JSON + 字符串枚举（高容错格式） ──

    [Fact]
    public void UT04_SaveProfile_WritesCompactStringEnumJson()
    {
        var farmer = NewFarmer();
        var profile = new SocialProfile { Affection = 80, Trust = 70, Archetype = SocialArchetype.CloseConfidant };

        SocialGraphService.Instance.SaveProfile(farmer, "Alex", profile, isMarried: false);

        farmer.modData.TryGetValue(SocialGraphService.ModDataPrefix + "Alex", out string json);
        Assert.Contains("\"Archetype\":\"CloseConfidant\"", json);
        Assert.Contains("\"Affection\":80", json);
        Assert.DoesNotContain("\n", json); // 紧凑（无缩进/换行）
    }

    // ── UT-05 数据损坏：Warn → 回退默认 → 写回修复 ──

    [Fact]
    public void UT05_GetProfile_CorruptJsonRepairsAndWarns()
    {
        var farmer = NewFarmer();
        farmer.modData[SocialGraphService.ModDataPrefix + "Alex"] = "{broken json";
        var capture = InstallCapture();
        try
        {
            // VT-SOCIAL-03：9 心修复种子（90/70）经自愈后仍为 CloseConfidant（九宫格自洽）。
            var profile = SocialGraphService.Instance.GetProfile(farmer, "Alex", 9, isMarried: false);

            Assert.Equal(SocialArchetype.CloseConfidant, profile.Archetype);
            Assert.Equal(90, profile.Affection);
            Assert.Equal(70, profile.Trust);
            Assert.Contains(capture.Entries, e => e.Level == LogLevel.Warn && e.Message.Contains("Alex"));

            // 修复已写回：再次读取不再告警且数值一致。
            var again = SocialGraphService.Instance.GetProfile(farmer, "Alex", 9, isMarried: false);
            Assert.Equal(profile.Affection, again.Affection);
            Assert.Equal(1, capture.Entries.Count(e => e.Level == LogLevel.Warn));
        }
        finally
        {
            Log.Initialize(_originalLogMonitor);
        }
    }

    // ── UT-06 "null" 载荷同样按损坏处理 ──

    [Fact]
    public void UT06_GetProfile_NullPayloadRepairs()
    {
        var farmer = NewFarmer();
        farmer.modData[SocialGraphService.ModDataPrefix + "Alex"] = "null";
        var capture = InstallCapture();
        try
        {
            var profile = SocialGraphService.Instance.GetProfile(farmer, "Alex", 0, isMarried: false);

            Assert.Equal(SocialArchetype.Stranger, profile.Archetype);
            Assert.Equal(10, profile.Affection);
            Assert.Contains(capture.Entries, e => e.Level == LogLevel.Warn);
            Assert.True(HasKey(farmer, "Alex"));
        }
        finally
        {
            Log.Initialize(_originalLogMonitor);
        }
    }

    // ── UT-07 npcName 空白：Trace 级日志（Debug 为项目日志最低档）→ 未绑定默认，不写 ModData ──

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void UT07_GetProfile_EmptyNpcNameReturnsUnboundDefaultWithoutWrite(string npcName)
    {
        var farmer = NewFarmer();
        var capture = InstallCapture();
        try
        {
            var profile = SocialGraphService.Instance.GetProfile(farmer, npcName);

            Assert.Equal(SocialArchetype.Stranger, profile.Archetype);
            Assert.Equal(10, profile.Affection);
            Assert.Contains(capture.Entries, e => e.Level == LogLevel.Debug && e.Message.Contains("npcName"));
            Assert.Equal(0, farmer.modData.Length);
        }
        finally
        {
            Log.Initialize(_originalLogMonitor);
        }
    }

    // ── UT-08 farmer 为 null（SaveLoaded 前调用）：Error 日志 → 安全回退，禁止崩溃 ──

    [Fact]
    public void UT08_GetProfile_NullFarmerReturnsSafeDefaultAndLogsError()
    {
        var capture = InstallCapture();
        try
        {
            var profile = SocialGraphService.Instance.GetProfile(null, "Alex", 9);

            Assert.Equal(SocialArchetype.CloseConfidant, profile.Archetype);
            Assert.Equal(90, profile.Affection);
            Assert.Equal(70, profile.Trust);
            Assert.Contains(capture.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("null farmer"));
        }
        finally
        {
            Log.Initialize(_originalLogMonitor);
        }
    }

    // ── UT-09 九宫格未婚判定契约（阈值边界） ──

    [Theory]
    [InlineData(60, 60, SocialArchetype.CloseConfidant)]
    [InlineData(59, 59, SocialArchetype.GuardedAcquaintance)]   // A3 拓宽：Aff>=40 && Trust<60
    [InlineData(50, 40, SocialArchetype.GuardedAcquaintance)]   // 4~7 心种子 (50/40) 正确落位
    [InlineData(50, 34, SocialArchetype.GuardedAcquaintance)]
    [InlineData(40, 0, SocialArchetype.GuardedAcquaintance)]    // Aff 下界恰好 40
    [InlineData(40, 60, SocialArchetype.Stranger)]              // Trust<60 在 60 处闭合
    [InlineData(39, 55, SocialArchetype.ReluctantConfidant)]
    [InlineData(39, 50, SocialArchetype.ReluctantConfidant)]    // RC 下界恰好 50
    [InlineData(39, 49, SocialArchetype.Stranger)]
    [InlineData(10, 10, SocialArchetype.Stranger)]
    public void UT09_EvaluateArchetype_UnmarriedNineGridBoundaries(int affection, int trust, SocialArchetype expected)
    {
        var profile = new SocialProfile { Affection = affection, Trust = trust };
        Assert.Equal(expected, SocialGraphService.Instance.EvaluateArchetype(profile, isMarried: false));
    }

    // ── UT-10 九宫格婚后判定契约（摩擦 > 疏离 > 和谐） ──

    [Theory]
    [InlineData(40, 0, SocialArchetype.DomesticColdSpell)]
    [InlineData(39, 50, SocialArchetype.DomesticRoommate)]
    [InlineData(39, 49, SocialArchetype.DomesticHarmonious)]
    [InlineData(40, 90, SocialArchetype.DomesticColdSpell)]
    public void UT10_EvaluateArchetype_MarriedPriorityContract(int friction, int distance, SocialArchetype expected)
    {
        var profile = new SocialProfile { UnresolvedFriction = friction, DomesticDistance = distance };
        Assert.Equal(expected, SocialGraphService.Instance.EvaluateArchetype(profile, isMarried: true));
    }

    // ── UT-11 Prompt 契约生成：zh 未婚 GuardedAcquaintance ──

    [Fact]
    public void UT11_CompileAttitudeLens_ZhGuardedAcquaintanceContract()
    {
        var profile = new SocialProfile { Affection = 55, Trust = 30, Archetype = SocialArchetype.Stranger };

        string lens = SocialGraphService.Instance.CompileAttitudeLens("艾利欧特", profile, isZh: true, isMarried: false);

        Assert.StartsWith("<relationship_lens>", lens);
        Assert.EndsWith("</relationship_lens>", lens);
        Assert.Contains("艾利欧特", lens);
        Assert.Contains("好感 55/100", lens);
        Assert.Contains("视角锚点", lens);
        Assert.Contains("信息域", lens);
        Assert.Contains("语气温度", lens);
        Assert.Contains("行为契约", lens);
        Assert.Contains("自然转移话题", lens); // GuardedAcquaintance 防御门禁
        Assert.DoesNotContain("莫逆知己", lens);
    }

    // ── UT-12 Prompt 契约生成：en 镜像 ──

    [Fact]
    public void UT12_CompileAttitudeLens_EnMirror()
    {
        var profile = new SocialProfile { Affection = 55, Trust = 30 };

        string lens = SocialGraphService.Instance.CompileAttitudeLens("Elliott", profile, isZh: false, isMarried: false);

        Assert.StartsWith("<relationship_lens>", lens);
        Assert.EndsWith("</relationship_lens>", lens);
        Assert.Contains("Elliott", lens);
        Assert.Contains("Perspective anchor", lens);
        Assert.Contains("Information domain", lens);
        Assert.Contains("Register", lens);
        Assert.Contains("Behavior contract", lens);
        Assert.Contains("steer the topic away", lens);
    }

    // ── UT-13 婚后三态契约（冷淡/室友/和谐） ──

    [Fact]
    public void UT13_CompileAttitudeLens_MarriedStates()
    {
        var service = SocialGraphService.Instance;
        var cold = new SocialProfile { UnresolvedFriction = 45, DomesticDistance = 10 };
        string coldLens = service.CompileAttitudeLens("Hakan", cold, isZh: true, isMarried: true);
        Assert.Contains("婚后指标", coldLens);
        Assert.Contains("心寒委屈", coldLens);
        Assert.Contains("真诚道歉", coldLens);

        var roommate = new SocialProfile { UnresolvedFriction = 10, DomesticDistance = 60 };
        string roommateLens = service.CompileAttitudeLens("Hakan", roommate, isZh: true, isMarried: true);
        Assert.Contains("室友化", roommateLens);

        var harmonious = new SocialProfile { UnresolvedFriction = 5, DomesticDistance = 5 };
        string warmLens = service.CompileAttitudeLens("Hakan", harmonious, isZh: true, isMarried: true);
        Assert.Contains("深层默契", warmLens);
    }

    // ── UT-14 严格按九宫格判定输出：以 EvaluateArchetype 结果为准，不信任存量 Archetype 字段 ──

    [Fact]
    public void UT14_CompileAttitudeLens_ReevaluatesArchetypeNotStoredField()
    {
        var service = SocialGraphService.Instance;

        var mislabeled = new SocialProfile { Affection = 80, Trust = 80, Archetype = SocialArchetype.Stranger };
        Assert.Contains("莫逆知己", service.CompileAttitudeLens("Alex", mislabeled, isZh: true, isMarried: false));

        var reluctant = new SocialProfile { Affection = 30, Trust = 60 };
        Assert.Contains("主动作出真实诉求", service.CompileAttitudeLens("Alex", reluctant, isZh: true, isMarried: false));

        var stranger = new SocialProfile { Affection = 10, Trust = 10 };
        Assert.Contains("表面寒暄", service.CompileAttitudeLens("Alex", stranger, isZh: true, isMarried: false));
    }

    // ── UT-15 禁止静态可变缓存：不同 Farmer 互不污染，ModData 单一数据源 ──

    [Fact]
    public void UT15_NoStaticCacheLeak_BetweenFarmers()
    {
        var farmerA = NewFarmer();
        var farmerB = NewFarmer();
        var service = SocialGraphService.Instance;

        var aFirst = service.GetProfile(farmerA, "Alex", 8, isMarried: false);
        Assert.Equal(SocialArchetype.CloseConfidant, aFirst.Archetype);

        var bFirst = service.GetProfile(farmerB, "Alex", 0, isMarried: false);
        Assert.Equal(SocialArchetype.Stranger, bFirst.Archetype);

        var aAgain = service.GetProfile(farmerA, "Alex", 8, isMarried: false);
        Assert.Equal(80, aAgain.Affection);
        Assert.Equal(70, aAgain.Trust);
    }

    // ── UT-16 SaveProfile 校验契约 ──

    [Fact]
    public void UT16_SaveProfile_ArgumentValidation()
    {
        var service = SocialGraphService.Instance;
        Assert.Throws<ArgumentNullException>(() => service.SaveProfile(null, "Alex", new SocialProfile()));
        Assert.Throws<ArgumentException>(() => service.SaveProfile(NewFarmer(), "  ", new SocialProfile()));
        Assert.Throws<ArgumentNullException>(() => service.SaveProfile(NewFarmer(), "Alex", null));
    }
}
