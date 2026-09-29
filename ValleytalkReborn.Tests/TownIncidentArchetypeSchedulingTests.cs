// TownIncidentArchetypeSchedulingTests.cs
// TIE-009B: archetype scheduling + role assignment contract tests.
//
// Headless: Game1.Date (StartGameDay source) is unavailable outside a running game,
// so a NetRoot<WorldState> shim is injected for the duration of each test and removed
// afterwards. Schedule resolution, the fixed casts, phase boundaries and the
// LoadFromSave archetype gate are all exercised through the real production code.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewModdingAPI.Framework.Logging;
using StardewValley;
using ValleytalkReborn;
using Xunit;

[Collection("StaticGlobalStateCollection")]
public class TownIncidentArchetypeSchedulingTests : IDisposable
{
    // TIE-009B schedule ruling constants.
    private const int ContestTriggerDayOfMonth = 4;
    private const int ContestDurationDays = 8;
    private const int FirstRotatingSlotStartDay = 12;
    private const int SecondRotatingSlotStartDay = 18;
    private const int RotatingSlotDurationDays = 6;

    private static readonly string[] SeasonNames = { "Spring", "Summer", "Fall", "Winter" };
    private static readonly string[] RotationOrder = { "Friction", "Mystery", "Collaboration" };

    // Vanilla festival start days per season; no rotating slot may start on one.
    private static readonly Dictionary<string, int[]> FestivalStartDays = new(StringComparer.Ordinal)
    {
        ["Spring"] = new[] { 13, 24 },
        ["Summer"] = new[] { 11, 28 },
        ["Fall"] = new[] { 16, 27 },
        ["Winter"] = new[] { 8, 25 },
    };

    // Fixed casts ordered like IncidentArchetypeDefinition.RequiredRoles.
    private static readonly Dictionary<string, string[]> FixedCasts = new(StringComparer.Ordinal)
    {
        ["Contest"] = new[] { "Gus", "Abigail", "Alex" },
        ["Friction"] = new[] { "George", "Marnie", "Alex" },
        ["Mystery"] = new[] { "Pierre", "Sebastian", "Leah" },
        ["Collaboration"] = new[] { "Robin", "Sam", "Haley" },
    };

    private readonly ModConfig _originalConfig;
    private readonly IMonitor _originalMonitor;

    public TownIncidentArchetypeSchedulingTests()
    {
        TestEnvironment.InstallHeadlessContext();
        _originalConfig = ModEntry.Config;
        _originalMonitor = ModEntry.SMonitor;
        LocalizedContentManager.CurrentLanguageCode = LocalizedContentManager.LanguageCode.en;
    }

    public void Dispose()
    {
        ModEntry.Config = _originalConfig;
        ModEntry.SMonitor = _originalMonitor;
        Field("_monitor").SetValue(null, null);
        Field("_data").SetValue(null, null);
        Field("_isSaveLoaded").SetValue(null, false);
        Field("_isDirty").SetValue(null, false);
        Field("_multiplayerDisabledLogged").SetValue(null, false);
        Field("_scriptwriterRequestedIncidentId").SetValue(null, null);
        Field("_scriptwriterCts").SetValue(null, null);
        Field("_scriptwriterTask").SetValue(null, null);
        LocalizedContentManager.CurrentLanguageCode = LocalizedContentManager.LanguageCode.en;
    }

    // ── 反射助手 ──

    private static FieldInfo Field(string name)
        => typeof(TownIncidentEngine).GetField(name, BindingFlags.NonPublic | BindingFlags.Static);

    private static T Constant<T>(string name) => (T)Field(name).GetValue(null);

    private static TownIncidentData EngineData() => (TownIncidentData)Field("_data").GetValue(null);

    private static bool EngineBool(string name) => (bool)Field(name).GetValue(null);

    private static void ResetEngine(TownIncidentData data = null)
    {
        Field("_data").SetValue(null, data ?? new TownIncidentData { SchemaVersion = 1 });
        Field("_isSaveLoaded").SetValue(null, true);
        Field("_isDirty").SetValue(null, false);
        Field("_multiplayerDisabledLogged").SetValue(null, false);
        Field("_scriptwriterRequestedIncidentId").SetValue(null, null);
        Field("_scriptwriterCts").SetValue(null, null);
        Field("_scriptwriterTask").SetValue(null, null);
    }

    private static IncidentArchetypeDefinition Definition(string archetypeId)
    {
        Assert.True(
            TownIncidentArchetypeCatalog.TryGetDefinition(archetypeId, out var definition),
            $"archetype '{archetypeId}' must resolve");
        return definition;
    }

    private static string CurrentSeasonName()
    {
        MemberInfo season = (MemberInfo)typeof(Game1)
                                .GetProperty("season", BindingFlags.Public | BindingFlags.Static)
                            ?? typeof(Game1).GetField("season", BindingFlags.Public | BindingFlags.Static);

        Assert.NotNull(season);
        object value = season is PropertyInfo property
            ? property.GetValue(null)
            : ((FieldInfo)season).GetValue(null);

        return value?.ToString();
    }

    private static void InvokePrivateStatic(string methodName, params object[] args)
    {
        var method = typeof(TownIncidentEngine).GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        method.Invoke(null, args);
    }

    private static string ExpectedKey(string archetypeId, int year, string season, int dayOfMonth)
        => $"{archetypeId.ToLowerInvariant()}:{year}:{season}:{dayOfMonth}";

    private static string ExpectedRotatingArchetype(int year, string season, int slotDay)
    {
        // Mirrors the engine's case-insensitive season lookup (TIE-009B-R1).
        int seasonIndex = (year - 1) * 4 + Array.FindIndex(
            SeasonNames, name => string.Equals(name, season, StringComparison.OrdinalIgnoreCase));
        int offset = slotDay == FirstRotatingSlotStartDay ? 0 : 1;
        return RotationOrder[(seasonIndex + offset) % RotationOrder.Length];
    }

    // ── UT01：轮换表（seasonIndex 扫描 × 槽位断言）──

    [Fact]
    public void UT01_Rotation_SeasonIndexSweep_AssignsBothSlots()
    {
        string[] seasons = SeasonNames;
        int created = 0;

        for (int year = 1; year <= 3; year++)
        {
            for (int seasonNumber = 0; seasonNumber < seasons.Length; seasonNumber++)
            {
                string season = seasons[seasonNumber];
                int seasonIndex = (year - 1) * 4 + seasonNumber;

                foreach (int slotDay in new[] { FirstRotatingSlotStartDay, SecondRotatingSlotStartDay })
                {
                    string expectedArchetypeId = ExpectedRotatingArchetype(year, season, slotDay);
                    string expectedKey = ExpectedKey(expectedArchetypeId, year, season, slotDay);

                    using (var scope = new EngineScope())
                    {
                        ResetEngine();

                        Assert.True(TownIncidentEngine.TryCreateIncidentForSchedule(year, season, slotDay), expectedKey);
                        var incident = EngineData().ActiveIncident;

                        Assert.Equal(expectedKey, incident.IncidentId);
                        Assert.Equal(expectedArchetypeId, incident.ArchetypeId);
                        Assert.Equal(expectedKey, EngineData().LastScheduleKey);
                        Assert.Equal(RotatingSlotDurationDays, incident.DurationDays);
                        Assert.Equal((int)Game1.Date.TotalDays, incident.StartGameDay);

                        var definition = Definition(expectedArchetypeId);
                        Assert.Equal(definition.RequiredRoles.OrderBy(x => x, StringComparer.Ordinal).ToArray(),
                            incident.AssignedRoles.Keys.OrderBy(x => x, StringComparer.Ordinal).ToArray());
                        Assert.Equal(
                            FixedCasts[expectedArchetypeId]
                                .Select((npc, i) => new KeyValuePair<string, string>(definition.RequiredRoles[i], npc))
                                .OrderBy(x => x.Key, StringComparer.Ordinal).ToArray(),
                            incident.AssignedRoles.OrderBy(x => x.Key, StringComparer.Ordinal).ToArray());

                        // Every archetype fires in 2 of every 3 slots of a season; across the
                        // two slots of one season exactly two distinct archetypes appear.
                        Assert.Empty(scope.Monitor.Errors());
                        created++;
                    }
                }
            }
        }

        Assert.Equal(24, created);
    }

    [Fact]
    public void UT02_Rotation_Deterministic_RepeatedCallsResolveSameArchetype()
    {
        for (int repeat = 0; repeat < 3; repeat++)
        {
            using (var scope = new EngineScope())
            {
                ResetEngine();
                Assert.True(TownIncidentEngine.TryCreateIncidentForSchedule(2, "Fall", SecondRotatingSlotStartDay));
                Assert.Equal(
                    ExpectedRotatingArchetype(2, "Fall", SecondRotatingSlotStartDay),
                    EngineData().ActiveIncident.ArchetypeId);
            }
        }

        // Each archetype appears in exactly two of the three seasons of a year.
        var seasonsOfYear = new HashSet<string>(StringComparer.Ordinal);
        for (int seasonNumber = 0; seasonNumber < 4; seasonNumber++)
            seasonsOfYear.Add(ExpectedRotatingArchetype(1, SeasonNames[seasonNumber], FirstRotatingSlotStartDay));

        Assert.Equal(3, seasonsOfYear.Count);
    }

    // ── UT03：日期覆盖测试（不与 Contest 重叠 / 不落在节日开始日）──

    [Fact]
    public void UT03_RotatingSlots_NeverOverlapContest_OrFestivalStartDays()
    {
        Assert.Equal(ContestTriggerDayOfMonth, Constant<int>("ContestTriggerDayOfMonth"));
        Assert.Equal(ContestDurationDays, Constant<int>("ContestDurationDays"));
        Assert.Equal(FirstRotatingSlotStartDay, Constant<int>("FirstRotatingSlotStartDay"));
        Assert.Equal(SecondRotatingSlotStartDay, Constant<int>("SecondRotatingSlotStartDay"));
        Assert.Equal(RotatingSlotDurationDays, Constant<int>("RotatingSlotDurationDays"));

        int contestEnd = ContestTriggerDayOfMonth + ContestDurationDays - 1;
        int slotOneEnd = FirstRotatingSlotStartDay + RotatingSlotDurationDays - 1;
        int slotTwoEnd = SecondRotatingSlotStartDay + RotatingSlotDurationDays - 1;

        Assert.True(FirstRotatingSlotStartDay > contestEnd, "slot one must start after the Contest span");
        Assert.True(SecondRotatingSlotStartDay > slotOneEnd, "slot two must start after slot one ends");
        Assert.Equal(23, slotTwoEnd);
        Assert.True(SecondRotatingSlotStartDay + RotatingSlotDurationDays - 1 <= 28);

        foreach (var season in FestivalStartDays)
        {
            Assert.DoesNotContain(FirstRotatingSlotStartDay, season.Value);
            Assert.DoesNotContain(SecondRotatingSlotStartDay, season.Value);
        }
    }

    [Fact]
    public void UT04_NonSlotDays_ResolveNothing_NoMutation()
    {
        foreach (int day in new[] { 1, 3, 5, 11, 13, 17, 19, 24, 28 })
        {
            using (var scope = new EngineScope())
            {
                ResetEngine();
                Assert.False(TownIncidentEngine.TryCreateIncidentForSchedule(1, "Spring", day), day.ToString());
                Assert.Null(EngineData().ActiveIncident);
                Assert.Null(EngineData().LastScheduleKey);
                Assert.False(EngineBool("_isDirty"));
            }
        }

        // An unknown season name resolves no slot either.
        using (var scope = new EngineScope())
        {
            ResetEngine();
            Assert.False(TownIncidentEngine.TryCreateIncidentForSchedule(1, "Snow", FirstRotatingSlotStartDay));
            Assert.Null(EngineData().ActiveIncident);
        }
    }

    // ── UT05：Contest 回归（Day 4 / 8 天 / Gus-Abigail-Alex / key 格式）──

    [Fact]
    public void UT05_ContestDay4_Unchanged()
    {
        using (var scope = new EngineScope())
        {
            ResetEngine();

            Assert.True(TownIncidentEngine.TryCreateIncidentForSchedule(1, "Spring", ContestTriggerDayOfMonth));
            var incident = EngineData().ActiveIncident;

            Assert.Equal("contest:1:Spring:4", incident.IncidentId);
            Assert.Equal("contest:1:Spring:4", EngineData().LastScheduleKey);
            Assert.Equal("Contest", incident.ArchetypeId);
            Assert.Equal(8, incident.DurationDays);
            Assert.Equal("Saloon", incident.ClimaxLocation);
            Assert.Equal(1900, incident.ClimaxTimeOfDay);
            Assert.Equal("Saloon Cook-Off", incident.EventName);
            Assert.Equal("A friendly cooking contest strains old rivalries in Pelican Town.", incident.IncidentTheme);
            Assert.Equal((int)Game1.Date.TotalDays, incident.StartGameDay);

            Assert.Equal("Gus", incident.AssignedRoles["Host"]);
            Assert.Equal("Abigail", incident.AssignedRoles["Champion"]);
            Assert.Equal("Alex", incident.AssignedRoles["Skeptic"]);
            Assert.Equal(3, incident.AssignedRoles.Count);

            // The incident is usable immediately: fallback installed before persistence.
            Assert.Equal(
                new[] { IncidentPhase.Inception, IncidentPhase.Escalation, IncidentPhase.Climax },
                incident.PhaseScripts.Keys.OrderBy(x => x).ToArray());
            Assert.NotEmpty(incident.BranchOutcomes);
            Assert.NotNull(incident.RuntimeFlags);
            Assert.True(EngineBool("_isDirty"));
            Assert.Empty(scope.Monitor.Errors());
        }
    }

    [Fact]
    public void UT06_ContestShell_SourcedFromCatalog()
    {
        var contest = Definition("Contest");

        using (var scope = new EngineScope())
        {
            ResetEngine();
            Assert.True(TownIncidentEngine.TryCreateIncidentForSchedule(1, "Spring", ContestTriggerDayOfMonth));
            var incident = EngineData().ActiveIncident;

            Assert.Equal(contest.EventName, incident.EventName);
            Assert.Equal(contest.IncidentTheme, incident.IncidentTheme);
            Assert.Equal(contest.DefaultDurationDays, incident.DurationDays);
            Assert.Equal(contest.DefaultClimaxLocation, incident.ClimaxLocation);
            Assert.Equal(contest.DefaultClimaxTimeOfDay, incident.ClimaxTimeOfDay);
            Assert.Equal(contest.ArchetypeId, incident.ArchetypeId);
        }
    }

    // ── UT07：重复处理不产生第二个事件 ──

    [Fact]
    public void UT07_SameScheduleKey_NeverDuplicates()
    {
        using (var scope = new EngineScope())
        {
            ResetEngine();

            Assert.True(TownIncidentEngine.TryCreateIncidentForSchedule(1, "Spring", ContestTriggerDayOfMonth));
            string firstId = EngineData().ActiveIncident.IncidentId;

            // Same DayStarted replayed while the incident is active.
            Assert.False(TownIncidentEngine.TryCreateIncidentForSchedule(1, "Spring", ContestTriggerDayOfMonth));
            Assert.Equal(firstId, EngineData().ActiveIncident.IncidentId);

            // The key stays recorded even after the incident elapsed away.
            EngineData().ActiveIncident = null;
            Assert.False(TownIncidentEngine.TryCreateIncidentForSchedule(1, "Spring", ContestTriggerDayOfMonth));
            Assert.Null(EngineData().ActiveIncident);
            Assert.Equal("contest:1:Spring:4", EngineData().LastScheduleKey);
        }
    }

    [Fact]
    public void UT08_ActiveIncidentBlocksSlot_SkipsWithInfo()
    {
        using (var scope = new EngineScope())
        {
            ResetEngine();

            Assert.True(TownIncidentEngine.TryCreateIncidentForSchedule(1, "Spring", ContestTriggerDayOfMonth));
            var active = EngineData().ActiveIncident;

            Assert.False(TownIncidentEngine.TryCreateIncidentForSchedule(1, "Spring", FirstRotatingSlotStartDay));

            Assert.Same(active, EngineData().ActiveIncident);
            Assert.Equal("contest:1:Spring:4", EngineData().LastScheduleKey);
            Assert.True(scope.Monitor.Messages.Exists(m => m.Contains("[Info]") && m.Contains("still active")));
            Assert.Empty(scope.Monitor.Errors());
        }
    }

    // ── UT09：固定卡司 ≡ 目录 RequiredRoles ──

    [Theory]
    [InlineData("Friction")]
    [InlineData("Mystery")]
    [InlineData("Collaboration")]
    [InlineData("Contest")]
    public void UT09_FixedCast_MatchesCatalogRequiredRoles(string archetypeId)
    {
        var definition = Definition(archetypeId);
        string[] expected = FixedCasts[archetypeId];

        Assert.Equal(definition.RequiredRoles.Count, expected.Length);

        var expectedCast = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < expected.Length; i++)
            expectedCast[definition.RequiredRoles[i]] = expected[i];

        using (var scope = new EngineScope())
        {
            ResetEngine();
            int slotDay = archetypeId == "Contest" ? ContestTriggerDayOfMonth : FirstRotatingSlotStartDay;
            string season = "Spring";

            // Find a year whose slot one carries this archetype (every archetype
            // appears within three years because 4 mod 3 == 1).
            int year = 1;
            while (year <= 3 && ExpectedRotatingArchetype(year, season, slotDay) != archetypeId)
            {
                if (archetypeId == "Contest") break;
                year++;
            }

            Assert.True(TownIncidentEngine.TryCreateIncidentForSchedule(year, season, slotDay));
            var incident = EngineData().ActiveIncident;

            Assert.Equal(archetypeId, incident.ArchetypeId);
            Assert.Equal(
                expectedCast.OrderBy(x => x.Key, StringComparer.Ordinal).ToArray(),
                incident.AssignedRoles.OrderBy(x => x.Key, StringComparer.Ordinal).ToArray());
            Assert.True(TownIncidentScriptwriter.TryValidateShell(incident, out string error), error);
            Assert.All(incident.AssignedRoles.Values, npc => Assert.False(string.IsNullOrWhiteSpace(npc)));
        }
    }

    // ── UT10：阶段边界（8 天回归 + 6 天新原型）──

    [Fact]
    public void UT10_PhaseBoundaries_EightDayContest()
    {
        Assert.Equal(IncidentPhase.Inception, TownIncidentEngine.GetPhaseForElapsedDay(0, 8));
        Assert.Equal(IncidentPhase.Inception, TownIncidentEngine.GetPhaseForElapsedDay(2, 8));
        Assert.Equal(IncidentPhase.Escalation, TownIncidentEngine.GetPhaseForElapsedDay(3, 8));
        Assert.Equal(IncidentPhase.Escalation, TownIncidentEngine.GetPhaseForElapsedDay(5, 8));
        Assert.Equal(IncidentPhase.Climax, TownIncidentEngine.GetPhaseForElapsedDay(6, 8));
        Assert.Equal(IncidentPhase.Climax, TownIncidentEngine.GetPhaseForElapsedDay(7, 8));
    }

    [Fact]
    public void UT11_PhaseBoundaries_SixDayArchetypes()
    {
        Assert.Equal(IncidentPhase.Inception, TownIncidentEngine.GetPhaseForElapsedDay(0, 6));
        Assert.Equal(IncidentPhase.Inception, TownIncidentEngine.GetPhaseForElapsedDay(1, 6));
        Assert.Equal(IncidentPhase.Escalation, TownIncidentEngine.GetPhaseForElapsedDay(2, 6));
        Assert.Equal(IncidentPhase.Escalation, TownIncidentEngine.GetPhaseForElapsedDay(3, 6));
        Assert.Equal(IncidentPhase.Climax, TownIncidentEngine.GetPhaseForElapsedDay(4, 6));
        Assert.Equal(IncidentPhase.Climax, TownIncidentEngine.GetPhaseForElapsedDay(5, 6));
    }

    [Fact]
    public void UT12_PhaseBoundaries_ShareArithmeticWithPromptWindow()
    {
        Match AssertParseable(string text, int durationDays)
        {
            var match = Regex.Match(
                text,
                @"Inception = elapsed days (\d+)-(\d+), Escalation = (\d+)-(\d+), Climax = (\d+)-(\d+)");
            Assert.True(match.Success, text);
            return match;
        }

        IncidentPhase PhaseOfRange(Match m, int day)
        {
            if (day >= int.Parse(m.Groups[1].Value) && day <= int.Parse(m.Groups[2].Value))
                return IncidentPhase.Inception;
            if (day >= int.Parse(m.Groups[3].Value) && day <= int.Parse(m.Groups[4].Value))
                return IncidentPhase.Escalation;
            return IncidentPhase.Climax;
        }

        foreach (int duration in Enumerable.Range(3, 13))
        {
            Match m = AssertParseable(TownIncidentTemplateCatalog.BuildPhaseWindowText(duration, false), duration);
            int climaxLast = int.Parse(m.Groups[6].Value);
            Assert.Equal(duration - 1, climaxLast);

            for (int day = 0; day < duration; day++)
            {
                Assert.Equal(
                    PhaseOfRange(m, day),
                    TownIncidentEngine.GetPhaseForElapsedDay(day, duration));
            }
        }
    }

    // ── UT13：加载边界：未知 ArchetypeId → 空模型 + 单条 Error ──

    [Fact]
    public void UT13_UnknownArchetypeIdOnLoad_DegradesToEmptyModel()
    {
        var incident = BuildPersistedShell("Unknown");
        using (var scope = new EngineScope())
        {
            var helper = new StubModHelper();
            helper.Save.Add(Constant<string>("SaveDataKey"), new TownIncidentData
            {
                SchemaVersion = 1,
                ActiveIncident = incident,
                LastScheduleKey = "unknown:1:Spring:12",
            });

            Field("_helper").SetValue(null, helper);
            try
            {
                InvokePrivateStatic("LoadFromSave");

                var data = EngineData();
                Assert.NotNull(data);
                Assert.Null(data.ActiveIncident);
                Assert.Null(data.LastScheduleKey);
                Assert.Equal(1, data.SchemaVersion);

                Assert.Single(scope.Monitor.Errors());
                Assert.Contains("structurally invalid", scope.Monitor.Errors()[0]);
            }
            finally
            {
                Field("_helper").SetValue(null, null);
            }
        }
    }

    [Fact]
    public void UT14_ValidContestSave_StillLoads()
    {
        var incident = BuildPersistedShell("Contest");
        TownIncidentScriptwriter.CreateFallback(incident, isChinese: false);

        using (var scope = new EngineScope())
        {
            var helper = new StubModHelper();
            helper.Save.Add(Constant<string>("SaveDataKey"), new TownIncidentData
            {
                SchemaVersion = 1,
                ActiveIncident = incident,
                LastScheduleKey = "contest:1:Spring:4",
            });

            Field("_helper").SetValue(null, helper);
            try
            {
                InvokePrivateStatic("LoadFromSave");

                var data = EngineData();
                Assert.Same(incident, data.ActiveIncident);
                Assert.Equal("contest:1:Spring:4", data.LastScheduleKey);
                Assert.Empty(scope.Monitor.Errors());
            }
            finally
            {
                Field("_helper").SetValue(null, null);
            }
        }
    }

    // ── UT15：存档键与 DayStarted 边界不变 ──

    [Fact]
    public void UT15_SaveDataKey_AndPersistedShape_Unchanged()
    {
        Assert.Equal("valleytalk.town-incidents", Constant<string>("SaveDataKey"));
        Assert.Equal(1, Constant<int>("CurrentSchemaVersion"));

        Assert.Equal(
            new[] { "ActiveIncident", "LastScheduleKey", "SchemaVersion" },
            typeof(TownIncidentData).GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(x => x.Name).OrderBy(x => x, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void UT16_DayStarted_ReschedulesOnce_DoesNotDuplicate()
    {
        using (var scope = new EngineScope())
        using (new DayScope(ContestTriggerDayOfMonth))
        {
            ResetEngine();
            string season = CurrentSeasonName();

            InvokePrivateStatic("OnDayStarted", new object[] { null, null });
            Assert.NotNull(EngineData().ActiveIncident);
            Assert.Equal(ExpectedKey("Contest", Game1.year, season, ContestTriggerDayOfMonth),
                EngineData().ActiveIncident.IncidentId);

            InvokePrivateStatic("OnDayStarted", new object[] { null, null });
            Assert.NotNull(EngineData().ActiveIncident);
            Assert.Single(scope.Monitor.Messages.FindAll(m =>
                m.Contains("Created Contest incident") && m.Contains(ExpectedKey("Contest", Game1.year, season, ContestTriggerDayOfMonth))));
        }
    }

    [Fact]
    public void UT17_Multiplayer_CreatesNothing()
    {
        using (new MultiplayerScope())
        using (var scope = new EngineScope())
        using (new DayScope(ContestTriggerDayOfMonth))
        {
            SetMultiplayer(true);
            ResetEngine();

            // The multiplayer boundary lives in OnDayStarted, before any incident
            // is created, mutated or persisted.
            InvokePrivateStatic("OnDayStarted", new object[] { null, null });
            Assert.Null(EngineData().ActiveIncident);
            Assert.Null(EngineData().LastScheduleKey);
            Assert.False(EngineBool("_isDirty"));
            Assert.Empty(scope.Monitor.Errors());
        }
    }

    // ── UT18：票面纪律（无 UpdateTicked / Harmony / 新增重置调用点）──

    [Fact]
    public void UT18_Engine_Guardrails()
    {
        var methods = typeof(TownIncidentEngine).GetMethods(
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly);

        Assert.DoesNotContain(methods, m => m.Name.Contains("UpdateTicked", StringComparison.Ordinal));
        Assert.DoesNotContain(methods, m => m.Name.Contains("Tick", StringComparison.Ordinal));

        foreach (var m in methods)
        {
            Assert.DoesNotContain(m.GetCustomAttributes(false),
                attribute => attribute.GetType().Name.StartsWith("Harmony", StringComparison.Ordinal));
        }

        Assert.Equal(2, EngineSourceCallSiteCount("ResetDailyClaims"));
    }

    // ── TIE-009B-R1：季名大小写归一 ──

    /// <summary>
    /// R1a: UT01 的 seasonIndex 扫描用小写季名（游戏季键的 casing）重跑一遍，
    /// 逐槽断言原型与 key 与大写跑法完全一致。
    /// </summary>
    [Fact]
    public void UT19_SeasonIndexSweep_LowercaseCasing_ResolvesIdentically()
    {
        int created = 0;

        for (int year = 1; year <= 3; year++)
        {
            for (int seasonNumber = 0; seasonNumber < SeasonNames.Length; seasonNumber++)
            {
                string capitalized = SeasonNames[seasonNumber];
                string lowercase = capitalized.ToLowerInvariant();

                foreach (int slotDay in new[] { FirstRotatingSlotStartDay, SecondRotatingSlotStartDay })
                {
                    string expectedArchetypeId = ExpectedRotatingArchetype(year, capitalized, slotDay);
                    Assert.Equal(expectedArchetypeId, ExpectedRotatingArchetype(year, lowercase, slotDay));

                    using (var scope = new EngineScope())
                    {
                        ResetEngine();

                        Assert.True(TownIncidentEngine.TryCreateIncidentForSchedule(year, lowercase, slotDay));
                        var incident = EngineData().ActiveIncident;

                        Assert.Equal(expectedArchetypeId, incident.ArchetypeId);
                        Assert.Equal(ExpectedKey(expectedArchetypeId, year, lowercase, slotDay), incident.IncidentId);
                        Assert.Equal(
                            ExpectedKey(expectedArchetypeId, year, lowercase, slotDay),
                            EngineData().LastScheduleKey);
                        Assert.Equal(RotatingSlotDurationDays, incident.DurationDays);
                        Assert.Empty(scope.Monitor.Errors());
                        created++;
                    }
                }
            }
        }

        Assert.Equal(24, created);
    }

    /// <summary>
    /// R1b: 同一槽位在 "SPRING"/"Spring"/"spring" 三种 casing 下解析出同一个原型。
    /// </summary>
    [Fact]
    public void UT20_SeasonCasing_ResolvesTheSameArchetype()
    {
        string expected = ExpectedRotatingArchetype(2, "Fall", SecondRotatingSlotStartDay);
        var keys = new List<string>();

        foreach (string season in new[] { "FALL", "Fall", "fall" })
        {
            using (var scope = new EngineScope())
            {
                ResetEngine();
                Assert.True(TownIncidentEngine.TryCreateIncidentForSchedule(2, season, SecondRotatingSlotStartDay));

                var incident = EngineData().ActiveIncident;
                Assert.Equal(expected, incident.ArchetypeId);
                Assert.Equal(ExpectedKey(expected, 2, season, SecondRotatingSlotStartDay), incident.IncidentId);
                keys.Add(incident.IncidentId);
            }
        }

        // Only the season part differs; the archetype id and the day do not.
        Assert.Equal(3, keys.Distinct(StringComparer.Ordinal).Count());
        Assert.Single(keys.Select(k => k.ToLowerInvariant()).Distinct(StringComparer.Ordinal));
    }

    /// <summary>
    /// R1c: key casing 钉死 —— key 逐字保留传入的季名，不做大小写改写
    /// （老存档去重依赖这一点）。生产 casing 由 Game1.season 决定，此处按运行时
    /// 读取值断言，不再硬编码某种假设。
    /// </summary>
    [Fact]
    public void UT21_ScheduleKey_PreservesSeasonCasing()
    {
        using (var scope = new EngineScope())
        {
            ResetEngine();
            Assert.True(TownIncidentEngine.TryCreateIncidentForSchedule(1, "spring", FirstRotatingSlotStartDay));
            Assert.Equal(
                "friction:1:spring:12",
                EngineData().ActiveIncident.IncidentId);

            EngineData().ActiveIncident = null;
            EngineData().LastScheduleKey = null;

            Assert.True(TownIncidentEngine.TryCreateIncidentForSchedule(1, "spring", ContestTriggerDayOfMonth));
            Assert.Equal("contest:1:spring:4", EngineData().ActiveIncident.IncidentId);

            // Capitalized input keeps the capitalized season part verbatim.
            EngineData().ActiveIncident = null;
            EngineData().LastScheduleKey = null;

            Assert.True(TownIncidentEngine.TryCreateIncidentForSchedule(1, "Spring", ContestTriggerDayOfMonth));
            Assert.Equal("contest:1:Spring:4", EngineData().ActiveIncident.IncidentId);
        }
    }

    [Fact]
    public void UT22_ProductionCasing_KeyUsesGame1SeasonVerbatim()
    {
        using (var scope = new EngineScope())
        using (new DayScope(ContestTriggerDayOfMonth))
        {
            ResetEngine();
            string season = CurrentSeasonName();
            Assert.False(string.IsNullOrEmpty(season));

            InvokePrivateStatic("OnDayStarted", new object[] { null, null });
            Assert.Equal(
                $"contest:{Game1.year}:{season}:{ContestTriggerDayOfMonth}",
                EngineData().ActiveIncident.IncidentId);
        }

        using (var scope = new EngineScope())
        using (new DayScope(FirstRotatingSlotStartDay))
        {
            ResetEngine();
            string season = CurrentSeasonName();

            InvokePrivateStatic("OnDayStarted", new object[] { null, null });
            string incidentId = EngineData().ActiveIncident.IncidentId;
            Assert.Equal(
                $"{incidentId.Split(':')[0]}:{Game1.year}:{season}:{FirstRotatingSlotStartDay}",
                incidentId);
        }
    }

    /// <summary>
    /// R1d: 哨兵 —— FixedCasts 的键集合与目录原型 id 集合双向等价，且每个卡司的
    /// 长度等于该原型的 RequiredRoles 数量。
    /// </summary>
    [Fact]
    public void UT23_FixedCasts_CoverEveryCatalogArchetype()
    {
        var casts = (Dictionary<string, string[]>)Field("FixedCasts").GetValue(null);
        string[] catalogIds = TownIncidentArchetypeCatalog.Definitions
            .Select(x => x.ArchetypeId).OrderBy(x => x, StringComparer.Ordinal).ToArray();

        Assert.Equal(
            catalogIds,
            casts.Keys.OrderBy(x => x, StringComparer.Ordinal).ToArray());

        foreach (var definition in TownIncidentArchetypeCatalog.Definitions)
        {
            Assert.True(casts.TryGetValue(definition.ArchetypeId, out var cast), definition.ArchetypeId);
            Assert.Equal(definition.RequiredRoles.Count, cast.Length);
            Assert.Equal(definition.RequiredRoles.Count, cast.Distinct(StringComparer.Ordinal).Count());
            Assert.All(cast, npc => Assert.False(string.IsNullOrWhiteSpace(npc)));
        }

        Assert.Equal(4, catalogIds.Length);
    }

    private static int EngineSourceCallSiteCount(string memberName)
    {
        string sourcePath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..",
            "src", "Events", "TownIncident", "TownIncidentEngine.cs");

        Assert.True(File.Exists(sourcePath), sourcePath);
        string source = File.ReadAllText(sourcePath);

        return Regex.Matches(source, Regex.Escape(memberName) + @"\s*\(\s*\)").Count;
    }

    // ── 辅助模型 ──

    private static EventSlotContract BuildPersistedShell(string archetypeId)
    {
        Assert.True(TownIncidentArchetypeCatalog.TryGetDefinition(
            archetypeId, out var definition) || archetypeId == "Unknown");

        var roles = new Dictionary<string, string>(StringComparer.Ordinal);
        if (definition != null)
        {
            string[] cast = FixedCasts[archetypeId];
            for (int i = 0; i < definition.RequiredRoles.Count; i++)
                roles[definition.RequiredRoles[i]] = cast[i];
        }
        else
        {
            roles["Host"] = "Gus";
            roles["Champion"] = "Abigail";
            roles["Skeptic"] = "Alex";
        }

        return new EventSlotContract
        {
            IncidentId = ExpectedKey(archetypeId, 1, "Spring", definition == null ? 12 : 4),
            ArchetypeId = archetypeId,
            StartGameDay = 4,
            DurationDays = definition?.DefaultDurationDays ?? 6,
            ClimaxLocation = definition?.DefaultClimaxLocation ?? "Saloon",
            ClimaxTimeOfDay = definition?.DefaultClimaxTimeOfDay ?? 1900,
            AssignedRoles = roles,
            EventName = definition?.EventName ?? "Unknown Incident",
            IncidentTheme = definition?.IncidentTheme ?? "An archetype the catalog does not know.",
            PhaseScripts = new Dictionary<IncidentPhase, Dictionary<string, RolePhaseBrief>>(),
            BranchOutcomes = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal),
            RuntimeFlags = new Dictionary<string, bool>(StringComparer.Ordinal),
        };
    }

    private static void SetMultiplayer(bool multi)
    {
        var runner = typeof(GameRunner).GetField("instance",
            BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static).GetValue(null);
        var instancesField = typeof(GameRunner).GetField("gameInstances",
            BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
        var list = instancesField.GetValue(runner);
        list.GetType().GetMethod("Clear").Invoke(list, null);
        if (!multi) return;

        var add = list.GetType().GetMethod("Add");
        add.Invoke(list, new[] { System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(Game1)) });
        add.Invoke(list, new[] { System.Runtime.Serialization.FormatterServices.GetUninitializedObject(typeof(Game1)) });
    }

    private sealed class CapturingMonitor : IMonitor
    {
        public readonly List<string> Messages = new();
        public bool IsVerbose => false;
        public void Log(string message, LogLevel level) => Messages.Add($"[{level}] {message}");
        public void LogOnce(string message, LogLevel level) => Messages.Add($"[{level}] {message}");
        public void VerboseLog(string message) { }
        public void VerboseLog(ref VerboseLogStringHandler handler) { }

        public List<string> Errors() => Messages.FindAll(m => m.StartsWith("[Error]", StringComparison.Ordinal));
    }

    /// <summary>
    /// Installs everything the scheduling path needs outside a running game: a
    /// readable Game1.Date (NetRoot&lt;WorldState&gt; shim), a Config for the
    /// scriptwriter gateway, and a capturing monitor on both ModEntry.SMonitor
    /// and the engine's own logger. Everything is restored on dispose.
    /// </summary>
    private sealed class EngineScope : IDisposable
    {
        internal readonly CapturingMonitor Monitor = new();
        private readonly GameDateScope _date;
        private readonly ModConfig _config;
        private readonly IMonitor _monitorSnapshot;

        public EngineScope()
        {
            TestEnvironment.InstallHeadlessContext();
            _date = new GameDateScope();
            _config = ModEntry.Config;
            _monitorSnapshot = ModEntry.SMonitor;
            ModEntry.Config = new ModConfig();
            ModEntry.SMonitor = Monitor;
            Field("_monitor").SetValue(null, Monitor);
        }

        public void Dispose()
        {
            ModEntry.Config = _config;
            ModEntry.SMonitor = _monitorSnapshot;
            Field("_monitor").SetValue(null, null);
            _date.Dispose();
        }
    }

    /// <summary>
    /// Game1.Date reads Game1.netWorldState.Value.Date, which is null outside a
    /// running game; a fresh NetRoot/WorldState pair makes it readable again so
    /// StartGameDay can be resolved exactly like the game does.
    /// </summary>
    private sealed class GameDateScope : IDisposable
    {
        private static readonly FieldInfo WorldStateRoot = typeof(Game1).GetField(
            "netWorldState", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static);

        private readonly object _previousRoot;

        public GameDateScope()
        {
            Assert.NotNull(WorldStateRoot);
            _previousRoot = WorldStateRoot.GetValue(null);

            object root = Activator.CreateInstance(WorldStateRoot.FieldType);
            var valueProperty = WorldStateRoot.FieldType.GetProperty("Value");
            valueProperty.SetValue(root, Activator.CreateInstance(valueProperty.PropertyType));
            WorldStateRoot.SetValue(null, root);
        }

        public void Dispose() => WorldStateRoot.SetValue(null, _previousRoot);
    }

    private sealed class DayScope : IDisposable
    {
        private readonly int _previousDay;

        public DayScope(int dayOfMonth)
        {
            _previousDay = Game1.dayOfMonth;
            Game1.dayOfMonth = dayOfMonth;
        }

        public void Dispose() => Game1.dayOfMonth = _previousDay;
    }

    private sealed class MultiplayerScope : IDisposable
    {
        private readonly object[] _snapshot;

        public MultiplayerScope()
        {
            var list = GameInstances();
            _snapshot = new object[list.Count];
            list.CopyTo(_snapshot, 0);
        }

        public void Dispose()
        {
            var list = GameInstances();
            list.Clear();
            foreach (var instance in _snapshot)
                list.Add(instance);
        }

        private static System.Collections.IList GameInstances()
            => (System.Collections.IList)typeof(GameRunner).GetField("gameInstances",
                BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)
                .GetValue(GameRunner.instance);
    }

    private sealed class StubModHelper : IModHelper
    {
        public readonly StubDataHelper Save = new();
        public string DirectoryPath => ".";
        public IModEvents Events => throw new NotImplementedException();
        public ICommandHelper ConsoleCommands => throw new NotImplementedException();
        public IGameContentHelper GameContent => throw new NotImplementedException();
        public IModContentHelper ModContent => throw new NotImplementedException();
        public IContentPackHelper ContentPacks => throw new NotImplementedException();
        public IDataHelper Data => Save;
        public IInputHelper Input => throw new NotImplementedException();
        public IReflectionHelper Reflection => throw new NotImplementedException();
        public IModRegistry ModRegistry => throw new NotImplementedException();
        public IMultiplayerHelper Multiplayer => throw new NotImplementedException();
        public ITranslationHelper Translation => throw new NotImplementedException();
        public TConfig ReadConfig<TConfig>() where TConfig : class, new() => throw new NotImplementedException();
        public void WriteConfig<TConfig>(TConfig config) where TConfig : class, new() => throw new NotImplementedException();
    }

    private sealed class StubDataHelper : IDataHelper
    {
        private readonly Dictionary<string, object> _saveData = new(StringComparer.Ordinal);
        private readonly Dictionary<string, object> _globalData = new(StringComparer.Ordinal);

        public string DirectoryPath => ".";

        public void Add<TModel>(string key, TModel data) where TModel : class => _saveData[key] = data;

        public TModel ReadSaveData<TModel>(string key) where TModel : class
            => _saveData.TryGetValue(key, out var value) ? (TModel)value : null;

        public void WriteSaveData<TModel>(string key, TModel data) where TModel : class => _saveData[key] = data;

        public TModel ReadGlobalData<TModel>(string key) where TModel : class
            => _globalData.TryGetValue(key, out var value) ? (TModel)value : null;

        public void WriteGlobalData<TModel>(string key, TModel data) where TModel : class => _globalData[key] = data;

        public TModel ReadJsonFile<TModel>(string path) where TModel : class
            => throw new NotImplementedException();

        public void WriteJsonFile<TModel>(string path, TModel data) where TModel : class
            => throw new NotImplementedException();

        public void ClearCache() { }
    }
}
