// TownIncidentContractTests.cs
// TIE-006: deterministic contract tests for the Town Incident system
// (TIE-001..TIE-005). Pure-model tests only: the engine's elapsed-day phase
// calculation reads Game1.Date (→ Game1.netWorldState.Value.Date), which is
// unavailable outside a running game, so the 8-day boundary contract is
// verified through the production boundary constant, the scriptwriter's
// documented phase-window prompt contract, and the fallback phase set.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Newtonsoft.Json;
using ValleytalkReborn;
using Xunit;

[Collection("StaticGlobalStateCollection")]
public class TownIncidentContractTests
{
    public TownIncidentContractTests()
    {
        InstallHeadlessMultiplayerContext();
    }

    private static bool _multiplayerContextInstalled;

    /// <summary>
    /// 无游戏进程环境下驱动 SMAPI Context / 游戏多人检查的两项前置：
    /// 1) StardewModdingAPI.Context 的静态构造器依赖 SMAPI.Toolkit.dll（测试 bin 未复制），
    ///    注册 AssemblyResolve 从游戏 smapi-internal 目录补载；
    /// 2) Context.IsMultiplayer → LocalMultiplayer.IsLocalMultiplayer 读取
    ///    GameRunner.instance.gameInstances.Count（instance 为 null 时 NRE），
    ///    注入中空 GameRunner（空实例列表）使该检查确定性求值为“单人”。
    /// </summary>
    private static void InstallHeadlessMultiplayerContext()
    {
        if (_multiplayerContextInstalled) return;
        _multiplayerContextInstalled = true;

        AppDomain.CurrentDomain.AssemblyResolve += (sender, args) =>
        {
            string assemblyName = new System.Reflection.AssemblyName(args.Name).Name;
            string smapiInternal = System.IO.Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..",
                "Stardew Valley", "smapi-internal", assemblyName + ".dll");
            return System.IO.File.Exists(smapiInternal) ? System.Reflection.Assembly.LoadFrom(smapiInternal) : null;
        };

        StardewValley.Game1.hasLocalClientsOnly = false;
        var runner = System.Runtime.Serialization.FormatterServices
            .GetUninitializedObject(typeof(StardewValley.GameRunner));
        var instancesField = typeof(StardewValley.GameRunner)
            .GetField("gameInstances", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
        instancesField?.SetValue(runner, Activator.CreateInstance(instancesField.FieldType));
        typeof(StardewValley.GameRunner)
            .GetField("instance", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)
            ?.SetValue(null, runner);
    }

    // ── 反射助手：注入引擎内存态（_data / _isSaveLoaded / _isDirty）──

    private static void SetEngineData(TownIncidentData data)
    {
        typeof(TownIncidentEngine).GetField("_data", BindingFlags.NonPublic | BindingFlags.Static)
            .SetValue(null, data);
    }

    private static void SetEngineBool(string fieldName, bool value)
    {
        typeof(TownIncidentEngine).GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Static)
            .SetValue(null, value);
    }

    private static bool GetEngineBool(string fieldName)
    {
        return (bool)typeof(TownIncidentEngine)
            .GetField(fieldName, BindingFlags.NonPublic | BindingFlags.Static)
            .GetValue(null);
    }

    private static bool? RuntimeFlag(string key)
    {
        var data = (TownIncidentData)typeof(TownIncidentEngine)
            .GetField("_data", BindingFlags.NonPublic | BindingFlags.Static)
            .GetValue(null);
        return data?.ActiveIncident?.RuntimeFlags != null
            && data.ActiveIncident.RuntimeFlags.TryGetValue(key, out var value)
            ? value
            : (bool?)null;
    }

    private static EventSlotContract BuildShell()
    {
        var shell = new EventSlotContract
        {
            IncidentId = "contest:1:Spring:4",
            ArchetypeId = "Contest",
            StartGameDay = 4,
            DurationDays = 8,
            ClimaxLocation = "Saloon",
            ClimaxTimeOfDay = 1900,
            AssignedRoles = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["Host"] = "Gus",
                ["Champion"] = "Abigail",
                ["Skeptic"] = "Alex",
            },
            EventName = "Saloon Cook-Off",
            IncidentTheme = "A friendly cooking contest strains old rivalries in Pelican Town.",
        };
        TownIncidentScriptwriter.CreateFallback(shell, isChinese: false);
        return shell;
    }

    private static TownIncidentData DataWithActiveIncident()
    {
        return new TownIncidentData { SchemaVersion = 1, ActiveIncident = BuildShell() };
    }

    private static string MakeJson(
        string incidentId = "contest:1:Spring:4",
        string hostNpc = "Gus",
        bool includeClimax = true,
        string motivation = "Short.")
    {
        var briefs = new Dictionary<string, object>();
        foreach (string npc in new[] { "Gus", "Abigail", "Alex" })
            briefs[npc] = new { Motivation = motivation, PublicOpinion = "Short." };

        var phases = new Dictionary<string, object>
        {
            ["Inception"] = briefs,
            ["Escalation"] = briefs,
        };
        if (includeClimax)
            phases["Climax"] = briefs;

        var root = new
        {
            IncidentId = incidentId,
            ArchetypeId = "Contest",
            AssignedRoles = new Dictionary<string, string>
            {
                ["Host"] = hostNpc,
                ["Champion"] = "Abigail",
                ["Skeptic"] = "Alex",
            },
            EventName = "Saloon Cook-Off",
            IncidentTheme = "A friendly contest.",
            PhaseScripts = phases,
            BranchOutcomes = new Dictionary<string, object>
            {
                ["backed_champion"] = new Dictionary<string, string> { ["cheer"] = "Outcome." },
                ["backed_skeptic"] = new Dictionary<string, string> { ["rigged"] = "Outcome." },
                ["stayed_neutral"] = new Dictionary<string, string> { ["fair judge"] = "Outcome." },
            },
        };

        return JsonConvert.SerializeObject(root);
    }

    // ── 验收：8 天相位边界（DurationDays=8）──

    [Fact]
    public void UT01_PhaseBoundary_PromptContract_IsExact()
    {
        var shell = BuildShell();

        string en = TownIncidentTemplateCatalog.BuildUserPrompt(shell, isChinese: false);
        Assert.Contains("Inception = elapsed days 0-2, Escalation = 3-5, Climax = 6-7", en);

        string zh = TownIncidentTemplateCatalog.BuildUserPrompt(shell, isChinese: true);
        Assert.Contains("Inception = 第 0-2 天，Escalation = 第 3-5 天，Climax = 第 6-7 天", zh);
    }

    [Fact]
    public void UT02_PhaseBoundary_DurationConstant_IsEightDays()
    {
        int duration = (int)typeof(TownIncidentEngine)
            .GetField("ContestDurationDays", BindingFlags.NonPublic | BindingFlags.Static)
            .GetValue(null);
        Assert.Equal(8, duration);
    }

    // ── 验收：静态回退契约完整性 ──

    [Fact]
    public void UT03_FallbackContract_Completeness_English()
    {
        var shell = BuildShell();
        TownIncidentScriptwriter.CreateFallback(shell, isChinese: false);

        Assert.Equal(
            new[] { IncidentPhase.Inception, IncidentPhase.Escalation, IncidentPhase.Climax },
            shell.PhaseScripts.Keys.OrderBy(x => x));

        foreach (var phase in shell.PhaseScripts.Values)
        {
            Assert.Equal(new[] { "Abigail", "Alex", "Gus" }, phase.Keys.OrderBy(x => x, StringComparer.Ordinal));
            foreach (var brief in phase.Values)
            {
                Assert.False(string.IsNullOrWhiteSpace(brief.Motivation));
                Assert.False(string.IsNullOrWhiteSpace(brief.PublicOpinion));
            }
        }

        Assert.True(TownIncidentScriptwriter.TryValidate(shell, shell, out var error), error);
    }

    [Fact]
    public void UT04_FallbackContract_Completeness_Chinese()
    {
        var shell = BuildShell();
        TownIncidentScriptwriter.CreateFallback(shell, isChinese: true);

        Assert.Equal(
            new[] { IncidentPhase.Inception, IncidentPhase.Escalation, IncidentPhase.Climax },
            shell.PhaseScripts.Keys.OrderBy(x => x));
        Assert.True(TownIncidentScriptwriter.TryValidate(shell, shell, out var error), error);
    }

    // ── 验收：畸形剧本拒绝 ──

    [Fact]
    public void UT05_MalformedScript_NeverProducesCandidate()
    {
        var shell = BuildShell();

        Assert.Null(TownIncidentScriptwriter.TryParseScript("The contest will be great!", shell));
        Assert.Null(TownIncidentScriptwriter.TryParseScript("   ", shell));

        string truncated = MakeJson().TrimEnd('}');
        var candidate = TownIncidentScriptwriter.TryParseScript(truncated, shell);
        Assert.True(candidate == null || !TownIncidentScriptwriter.TryValidate(candidate, shell, out _));
    }

    [Fact]
    public void UT06_ValidFencedScript_ParsesAndValidates()
    {
        var shell = BuildShell();
        string raw = "Here is your script:\n```json\n" + MakeJson() + "\n```\nEnjoy!";

        var candidate = TownIncidentScriptwriter.TryParseScript(raw, shell);
        Assert.NotNull(candidate);
        Assert.True(TownIncidentScriptwriter.TryValidate(candidate, shell, out var error), error);
        Assert.Equal("Short.", candidate.PhaseScripts[IncidentPhase.Inception]["Gus"].Motivation);
    }

    [Fact]
    public void UT07_ContractViolations_RejectedWithReason()
    {
        var shell = BuildShell();

        var wrongId = TownIncidentScriptwriter.TryParseScript(MakeJson(incidentId: "contest:9:Fall:4"), shell);
        Assert.False(TownIncidentScriptwriter.TryValidate(wrongId, shell, out var idError));
        Assert.Contains("IncidentId", idError);

        var noClimax = TownIncidentScriptwriter.TryParseScript(MakeJson(includeClimax: false), shell);
        Assert.False(TownIncidentScriptwriter.TryValidate(noClimax, shell, out var phaseError));
        Assert.Contains("Climax", phaseError);

        var changedRoles = TownIncidentScriptwriter.TryParseScript(MakeJson(hostNpc: "Alex"), shell);
        Assert.False(TownIncidentScriptwriter.TryValidate(changedRoles, shell, out var roleError));
        Assert.Contains("role", roleError);

        var overLength = TownIncidentScriptwriter.TryParseScript(MakeJson(motivation: new string('x', 250)), shell);
        Assert.False(TownIncidentScriptwriter.TryValidate(overLength, shell, out var lengthError));
        Assert.Contains("exceeds", lengthError);
    }

    // ── 验收：选项关键词路由（真实 RecordChoice，反射注入引擎内存态）──

    [Fact]
    public void UT08_KeywordRouting_ParticipantsSetFlags()
    {
        SetEngineData(DataWithActiveIncident());
        SetEngineBool("_isDirty", false);

        TownIncidentEngine.RecordChoice("Gus", "You can do it Abigail, I'll cheer for you!");
        Assert.True(RuntimeFlag("backed_champion") == true);

        SetEngineData(DataWithActiveIncident());
        TownIncidentEngine.RecordChoice("Alex", "The whole contest is rigged if you ask me.");
        Assert.True(RuntimeFlag("backed_skeptic") == true);

        SetEngineData(DataWithActiveIncident());
        TownIncidentEngine.RecordChoice("Abigail", "Let's wait and see what the fair judge says.");
        Assert.True(RuntimeFlag("stayed_neutral") == true);
    }

    [Fact]
    public void UT09_NonParticipantChoice_Ignored_NoMutation()
    {
        SetEngineData(DataWithActiveIncident());
        SetEngineBool("_isDirty", false);

        TownIncidentEngine.RecordChoice("Pierre", "I'll cheer for Abigail!");

        Assert.Null(RuntimeFlag("backed_champion"));
        Assert.False(GetEngineBool("_isDirty"));
    }

    [Fact]
    public void UT10_EmptyChoiceText_NoMutation()
    {
        SetEngineData(DataWithActiveIncident());
        SetEngineBool("_isDirty", false);

        TownIncidentEngine.RecordChoice("Gus", "");
        TownIncidentEngine.RecordChoice("Gus", "   ");

        Assert.Null(RuntimeFlag("backed_champion"));
        Assert.False(GetEngineBool("_isDirty"));
    }

    // ── 验收：空文本 / 无活跃事件行为 ──

    [Fact]
    public void UT11_NoActiveIncident_SafeNoOp()
    {
        SetEngineData(new TownIncidentData { SchemaVersion = 1 });
        SetEngineBool("_isDirty", false);

        TownIncidentEngine.RecordChoice("Gus", "I'll cheer for Abigail!");
        Assert.False(GetEngineBool("_isDirty"));
    }

    [Fact]
    public void UT12_TryGetActorBrief_PreSaveLoaded_ReturnsFalse()
    {
        SetEngineBool("_isSaveLoaded", false);
        SetEngineData(null);

        Assert.False(TownIncidentEngine.TryGetActorBrief("Gus", out var brief));
        Assert.Null(brief);
    }

    [Fact]
    public void UT13_TryGetActorBrief_NonParticipant_ReturnsFalse()
    {
        SetEngineBool("_isSaveLoaded", true);
        SetEngineData(DataWithActiveIncident());

        // 非参与者在相位计算（Game1 依赖）之前即被拒绝。
        Assert.False(TownIncidentEngine.TryGetActorBrief("Pierre", out var brief));
        Assert.Null(brief);
    }
}
