// GraphDrivenCastResolverTests.cs
// TIE-CAST-001: focused tests for the graph-driven cast resolver. The resolver
// ladder (TryResolveCastCore) is pure and headless — driven entirely through
// stub providers (neighbors / validity / hearts), so no Game1 / SMAPI context is
// installed. The scanner's raw neighbor merge and hearts gate are production
// behavior exercised through smoke, not unit-tested here (DialogueBuilder is a
// process singleton populated only from a live game).

using System;
using System.Collections.Generic;
using System.Linq;
using ValleytalkReborn;
using ValleytalkReborn.Dialogue.Coordination;
using Xunit;

public class GraphDrivenCastResolverTests
{
    private static IncidentArchetypeDefinition Definition(string archetypeId)
    {
        Assert.True(
            TownIncidentArchetypeCatalog.TryGetDefinition(archetypeId, out var definition),
            $"archetype '{archetypeId}' must resolve");
        return definition;
    }

    private static string[] SortedValues(Dictionary<string, string> roles, string exclude = null)
    {
        return roles.Values
            .Where(v => exclude == null || !string.Equals(v, exclude, StringComparison.Ordinal))
            .OrderBy(v => v, StringComparer.Ordinal)
            .ToArray();
    }

    private static EventSlotContract BuildShellWithFallback(string archetypeId, string[] cast, bool isChinese = false)
    {
        var definition = Definition(archetypeId);
        var roles = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < definition.RequiredRoles.Count; i++)
            roles[definition.RequiredRoles[i]] = cast[i];

        var shell = new EventSlotContract
        {
            IncidentId = $"{archetypeId.ToLowerInvariant()}:1:Spring:4",
            ArchetypeId = archetypeId,
            StartGameDay = 4,
            DurationDays = definition.DefaultDurationDays,
            ClimaxLocation = definition.DefaultClimaxLocation,
            ClimaxTimeOfDay = definition.DefaultClimaxTimeOfDay,
            AssignedRoles = roles,
            EventName = definition.EventName,
            IncidentTheme = definition.IncidentTheme,
        };

        TownIncidentScriptwriter.CreateFallback(shell, isChinese);
        return shell;
    }

    // ── 验收：FNV-1a 种子确定且跨输入可区分 ──

    [Fact]
    public void ComputeCastSeed_IsDeterministic()
    {
        Assert.Equal(
            TownIncidentEngine.ComputeCastSeed("contest:1:Spring:4", 0),
            TownIncidentEngine.ComputeCastSeed("contest:1:Spring:4", 0));
    }

    [Fact]
    public void ComputeCastSeed_IsDistinctAcrossInputs()
    {
        long baseSeed = TownIncidentEngine.ComputeCastSeed("contest:1:Spring:4", 0);
        Assert.NotEqual(baseSeed, TownIncidentEngine.ComputeCastSeed("contest:1:Spring:5", 0));
        Assert.NotEqual(baseSeed, TownIncidentEngine.ComputeCastSeed("contest:1:Spring:4", 1));
    }

    // ── 验收：心数闸门（RequiredHearts 阈值以下排除）──

    [Fact]
    public void IsRelationshipActive_RespectsRequiredHearts()
    {
        Assert.False(NpcPersonaRelationScanner.IsRelationshipActive(4, new BioData.ListEntry { RequiredHearts = 5 }));
        Assert.True(NpcPersonaRelationScanner.IsRelationshipActive(5, new BioData.ListEntry { RequiredHearts = 5 }));
        Assert.True(NpcPersonaRelationScanner.IsRelationshipActive(0, new BioData.ListEntry { RequiredHearts = 0 }));
        Assert.False(NpcPersonaRelationScanner.IsRelationshipActive(3, null));
    }

    // ── 验收：关系描述净化（换行/花括号剥离 + 120 截断）──

    [Fact]
    public void CleanRelationshipLine_StripsAndTruncates()
    {
        Assert.Null(TownIncidentTemplateCatalog.CleanRelationshipLine(null));
        Assert.Null(TownIncidentTemplateCatalog.CleanRelationshipLine("   "));

        string cleaned = TownIncidentTemplateCatalog.CleanRelationshipLine("Line one\n{braced}\rnext");
        Assert.Equal("Line one braced next", cleaned);

        string longText = new string('x', 200);
        Assert.Equal(120, TownIncidentTemplateCatalog.CleanRelationshipLine(longText).Length);
    }

    // ── 验收：有效性谓词过滤（动物/黑名单/未知名）──

    [Fact]
    public void Core_ExcludesInvalidCastNpcs()
    {
        var definition = Definition("Contest");

        Func<string, IReadOnlyList<string>> neighbors = name =>
            name == "Gus" ? new[] { "Cat", "Abigail", "Alex", "Wizard", "Unknown" } : Array.Empty<string>();
        Func<string, bool> valid = name => name != "Cat" && name != "Wizard" && name != "Unknown";
        Func<string, int> hearts = _ => 5;

        bool ok = TownIncidentEngine.TryResolveCastCore(
            definition, "contest:1:Spring:4", neighbors, valid, hearts, out var roles);

        Assert.True(ok);
        Assert.Equal("Gus", roles["Host"]);
        Assert.DoesNotContain("Cat", roles.Values);
        Assert.DoesNotContain("Wizard", roles.Values);
        Assert.DoesNotContain("Unknown", roles.Values);
        Assert.Equal(new[] { "Abigail", "Alex" }, SortedValues(roles, "Gus"));
    }

    // ── 验收：合并邻居列表（单向关系合并后的结果被正确使用）──

    [Fact]
    public void Core_UsesMergedNeighborList()
    {
        var definition = Definition("Contest");

        // The scanner merges out-edges + in-edges; the stub here stands in for that
        // merged result (a one-way relationship surface), which the ladder must honor.
        Func<string, IReadOnlyList<string>> neighbors = name =>
            name == "Gus" ? new[] { "Abigail", "Alex" } : Array.Empty<string>();
        Func<string, bool> valid = _ => true;
        Func<string, int> hearts = _ => 5;

        bool ok = TownIncidentEngine.TryResolveCastCore(
            definition, "contest:1:Spring:4", neighbors, valid, hearts, out var roles);

        Assert.True(ok);
        Assert.Equal(3, roles.Count);
        Assert.Equal("Gus", roles["Host"]);
        Assert.Equal(new[] { "Abigail", "Alex" }, SortedValues(roles, "Gus"));
    }

    // ── 验收：heartsProvider 负值（无友谊数据）排除，无需回退 ──

    [Fact]
    public void Core_HeartsBelowZero_ExcludedWithoutFallback()
    {
        var definition = Definition("Contest");

        Func<string, IReadOnlyList<string>> neighbors = name =>
            name == "Gus" ? new[] { "Abigail", "Alex", "Sebastian" } : Array.Empty<string>();
        Func<string, bool> valid = _ => true;
        Func<string, int> hearts = name => name == "Alex" ? -1 : 5;

        bool ok = TownIncidentEngine.TryResolveCastCore(
            definition, "contest:1:Spring:4", neighbors, valid, hearts, out var roles);

        Assert.True(ok);
        Assert.DoesNotContain("Alex", roles.Values);
        Assert.Equal(new[] { "Abigail", "Sebastian" }, SortedValues(roles, "Gus"));
    }

    // ── 验收：L1 拟合（锚点 + 一跳邻居）──

    [Fact]
    public void Core_L1_FillsCastFromAnchorNeighbors()
    {
        var definition = Definition("Contest");

        Func<string, IReadOnlyList<string>> neighbors = name =>
            name == "Gus" ? new[] { "Abigail", "Alex" } : Array.Empty<string>();
        Func<string, bool> valid = _ => true;
        Func<string, int> hearts = _ => 5;

        bool ok = TownIncidentEngine.TryResolveCastCore(
            definition, "contest:1:Spring:4", neighbors, valid, hearts, out var roles);

        Assert.True(ok);
        Assert.Equal(3, roles.Count);
        Assert.Equal("Gus", roles["Host"]);
        Assert.Equal(new[] { "Abigail", "Alex" }, SortedValues(roles, "Gus"));
    }

    // ── 验收：L2 二跳扩展（一跳不足，二跳补齐）──

    [Fact]
    public void Core_L2_TwoHopFillsRemainingRole()
    {
        var definition = Definition("Contest");

        Func<string, IReadOnlyList<string>> neighbors = name =>
            name == "Gus" ? new[] { "Abigail" } :
            name == "Abigail" ? new[] { "Alex" } :
            Array.Empty<string>();
        Func<string, bool> valid = _ => true;
        Func<string, int> hearts = _ => 5;

        bool ok = TownIncidentEngine.TryResolveCastCore(
            definition, "contest:1:Spring:4", neighbors, valid, hearts, out var roles);

        Assert.True(ok);
        Assert.Equal(3, roles.Count);
        Assert.Equal("Gus", roles["Host"]);
        Assert.Equal("Abigail", roles["Champion"]);
        Assert.Equal("Alex", roles["Skeptic"]);
    }

    // ── 验收：L3 FixedCasts 逐字回退 ──

    [Fact]
    public void Core_L3_FallsBackToFixedCast()
    {
        var definition = Definition("Contest");

        Func<string, IReadOnlyList<string>> neighbors = _ => Array.Empty<string>();
        Func<string, bool> valid = _ => true;
        Func<string, int> hearts = _ => 5;

        bool ok = TownIncidentEngine.TryResolveCastCore(
            definition, "contest:1:Spring:4", neighbors, valid, hearts, out var roles);

        Assert.True(ok);
        Assert.Equal("Gus", roles["Host"]);
        Assert.Equal("Abigail", roles["Champion"]);
        Assert.Equal("Alex", roles["Skeptic"]);
    }

    // ── 验收：相同种子 ⇒ 相同选角 ──

    [Fact]
    public void Core_IdenticalSeed_YieldsIdenticalCast()
    {
        var definition = Definition("Contest");

        Func<string, IReadOnlyList<string>> neighbors = name =>
            name == "Gus" ? new[] { "Abigail", "Alex", "Sebastian", "Leah" } : Array.Empty<string>();
        Func<string, bool> valid = _ => true;
        Func<string, int> hearts = _ => 5;

        TownIncidentEngine.TryResolveCastCore(
            definition, "contest:1:Spring:4", neighbors, valid, hearts, out var first);
        TownIncidentEngine.TryResolveCastCore(
            definition, "contest:1:Spring:4", neighbors, valid, hearts, out var second);

        Assert.Equal(first, second);
    }

    // ── 验收：图驱动选角通过 TryValidateShell 门控 ──

    [Fact]
    public void Core_GraphCast_PassesTryValidateShell()
    {
        var definition = Definition("Contest");

        Func<string, IReadOnlyList<string>> neighbors = name =>
            name == "Gus" ? new[] { "Abigail", "Alex" } : Array.Empty<string>();
        Func<string, bool> valid = _ => true;
        Func<string, int> hearts = _ => 5;

        bool ok = TownIncidentEngine.TryResolveCastCore(
            definition, "contest:1:Spring:4", neighbors, valid, hearts, out var roles);
        Assert.True(ok);

        var shell = new EventSlotContract
        {
            IncidentId = "contest:1:Spring:4",
            ArchetypeId = "Contest",
            StartGameDay = 4,
            DurationDays = definition.DefaultDurationDays,
            ClimaxLocation = definition.DefaultClimaxLocation,
            ClimaxTimeOfDay = definition.DefaultClimaxTimeOfDay,
            AssignedRoles = roles,
            EventName = definition.EventName,
            IncidentTheme = definition.IncidentTheme,
        };

        Assert.True(TownIncidentScriptwriter.TryValidateShell(shell, out string error), error);
    }

    // ── 验收：无激活关系文本时 Contest 提示词逐字节保持 ──

    private const string LegacyContestPromptEn =
        "Incident \"Saloon Cook-Off\" (contest:1:Spring:4), archetype Contest. "
        + "Theme: A friendly cooking contest strains old rivalries in Pelican Town..\n"
        + "Roles (echo exactly): Host=Gus; Champion=Abigail; Skeptic=Alex.\n"
        + "Timeline: 8 days starting game day 4. Phases: Inception = elapsed days 0-2, "
        + "Escalation = 3-5, Climax = 6-7 (finale at the Saloon, 1900).\n"
        + "Branch group keys (use exactly these): backed_champion, backed_skeptic, stayed_neutral.\n"
        + "Write the complete JSON object now.";

    [Fact]
    public void ContestPrompt_ByteIdentical_WhenNoRelationshipText()
    {
        var shell = BuildShellWithFallback("Contest", new[] { "Gus", "Abigail", "Alex" });
        Assert.Equal(LegacyContestPromptEn, TownIncidentTemplateCatalog.BuildUserPrompt(shell, isChinese: false));
    }
}
