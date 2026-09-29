// TownIncidentArchetypeContractTests.cs
// TIE-009A: pure contract tests for the town incident archetype catalog.
// No Game1 / SMAPI / Harmony / LLM dependency is installed by these tests — the
// catalog is exercised without TestEnvironment.InstallHeadlessContext, which is
// itself the proof that lookups need no game context.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Newtonsoft.Json;
using ValleytalkReborn;
using Xunit;

public class TownIncidentArchetypeContractTests
{
    private static IncidentArchetypeDefinition Definition(string archetypeId)
    {
        Assert.True(
            TownIncidentArchetypeCatalog.TryGetDefinition(archetypeId, out var definition),
            $"archetype '{archetypeId}' must resolve");
        return definition;
    }

    // ── 验收：四个原型定义齐全且值精确 ──

    [Fact]
    public void UT01_Catalog_HoldsExactlyFourArchetypes()
    {
        Assert.Equal(
            new[] { "Contest", "Friction", "Mystery", "Collaboration" },
            TownIncidentArchetypeCatalog.Definitions.Select(x => x.ArchetypeId).ToArray());

        Assert.Equal(
            Enum.GetNames(typeof(TownIncidentArchetype)),
            TownIncidentArchetypeCatalog.Definitions.Select(x => x.ArchetypeId).ToArray());
    }

    [Fact]
    public void UT02_ContestDefinition_MatchesPersistedShell()
    {
        var contest = Definition("Contest");

        Assert.Equal(new[] { "Host", "Champion", "Skeptic" }, contest.RequiredRoles);
        Assert.Equal(8, contest.DefaultDurationDays);
        Assert.Equal("Saloon", contest.DefaultClimaxLocation);
        Assert.Equal(1900, contest.DefaultClimaxTimeOfDay);
        Assert.Equal("Saloon Cook-Off", contest.EventName);
        Assert.Equal(
            "A friendly cooking contest strains old rivalries in Pelican Town.",
            contest.IncidentTheme);
    }

    [Fact]
    public void UT03_NewArchetypeDefinitions_AreExact()
    {
        var friction = Definition("Friction");
        Assert.Equal(new[] { "Victim", "Culprit", "Witness" }, friction.RequiredRoles);
        Assert.Equal(6, friction.DefaultDurationDays);
        Assert.Equal("Pierre's General Store", friction.DefaultClimaxLocation);
        Assert.Equal(1200, friction.DefaultClimaxTimeOfDay);
        Assert.Equal("Store Ledger Dispute", friction.EventName);
        Assert.Equal(
            "A disputed store ledger sets neighbors against each other in Pelican Town.",
            friction.IncidentTheme);

        var mystery = Definition("Mystery");
        Assert.Equal(new[] { "Loser", "Suspect", "Investigator" }, mystery.RequiredRoles);
        Assert.Equal(6, mystery.DefaultDurationDays);
        Assert.Equal("Saloon", mystery.DefaultClimaxLocation);
        Assert.Equal(2000, mystery.DefaultClimaxTimeOfDay);
        Assert.Equal("Vanished Heirloom", mystery.EventName);
        Assert.Equal(
            "A keepsake vanishes from the Saloon and suspicion falls on the regulars.",
            mystery.IncidentTheme);

        var collaboration = Definition("Collaboration");
        Assert.Equal(new[] { "Organizer", "Worker", "Slacker" }, collaboration.RequiredRoles);
        Assert.Equal(6, collaboration.DefaultDurationDays);
        Assert.Equal("Community Center", collaboration.DefaultClimaxLocation);
        Assert.Equal(1000, collaboration.DefaultClimaxTimeOfDay);
        Assert.Equal("Community Center Restoration", collaboration.EventName);
        Assert.Equal(
            "A restoration effort divides the workload and tests old friendships.",
            collaboration.IncidentTheme);
    }

    // ── 验收：空角色集 = BUG，无回退定义 ──

    [Fact]
    public void UT04_EveryDefinition_HasNonEmptyRoleSet()
    {
        foreach (var definition in TownIncidentArchetypeCatalog.Definitions)
        {
            Assert.NotNull(definition.RequiredRoles);
            Assert.NotEmpty(definition.RequiredRoles);
            Assert.All(definition.RequiredRoles, role => Assert.False(string.IsNullOrWhiteSpace(role)));
            Assert.Equal(definition.RequiredRoles.Count, definition.RequiredRoles.Distinct(StringComparer.Ordinal).Count());
            Assert.False(string.IsNullOrWhiteSpace(definition.DefaultClimaxLocation));
            Assert.False(string.IsNullOrWhiteSpace(definition.EventName));
            Assert.False(string.IsNullOrWhiteSpace(definition.IncidentTheme));
            Assert.True(definition.DefaultDurationDays > 0);
        }
    }

    [Fact]
    public void UT05_Definitions_AreInitOnly()
    {
        foreach (var property in typeof(IncidentArchetypeDefinition).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var setter = property.SetMethod;
            Assert.NotNull(setter);
            Assert.Contains(
                typeof(IsExternalInit),
                setter.ReturnParameter.GetRequiredCustomModifiers());
        }
    }

    // ── 验收：未知原型不转换为 Contest ──

    [Fact]
    public void UT06_UnknownArchetype_NotConvertedToContest()
    {
        foreach (string unknown in new[] { "contest", "CONTEST", "Contest ", "Friction2", "", "Unknown" })
        {
            Assert.False(TownIncidentArchetypeCatalog.TryGetDefinition(unknown, out var definition), unknown);
            Assert.Null(definition);
        }

        Assert.False(TownIncidentArchetypeCatalog.TryGetDefinition(null, out var nullDefinition));
        Assert.Null(nullDefinition);
    }

    [Fact]
    public void UT07_KnownArchetype_MatchesExactlyOrdinal()
    {
        Assert.True(TownIncidentArchetypeCatalog.TryGetDefinition("Contest", out var contest));
        Assert.Same(TownIncidentArchetypeCatalog.Definitions[0], contest);

        Assert.True(TownIncidentArchetypeCatalog.TryGetDefinition("Collaboration", out var collaboration));
        Assert.Equal("Community Center", collaboration.DefaultClimaxLocation);
    }

    // ── 验收：既有 Contest 存档形状原样往返 ──

    private static TownIncidentData BuildPersistedContest()
    {
        var incident = new EventSlotContract
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
        TownIncidentScriptwriter.CreateFallback(incident, isChinese: false);
        return new TownIncidentData
        {
            SchemaVersion = 1,
            ActiveIncident = incident,
            LastScheduleKey = "contest:1:Spring:4",
        };
    }

    [Fact]
    public void UT08_ContestSaveData_RoundTripsUnchanged()
    {
        var data = BuildPersistedContest();
        string json = JsonConvert.SerializeObject(data);
        var loaded = JsonConvert.DeserializeObject<TownIncidentData>(json);

        Assert.Equal(1, loaded.SchemaVersion);
        Assert.Equal("contest:1:Spring:4", loaded.LastScheduleKey);

        var incident = loaded.ActiveIncident;
        Assert.Equal("contest:1:Spring:4", incident.IncidentId);
        Assert.Equal("Contest", incident.ArchetypeId);
        Assert.Equal(8, incident.DurationDays);
        Assert.Equal("Saloon", incident.ClimaxLocation);
        Assert.Equal(1900, incident.ClimaxTimeOfDay);
        Assert.Equal("Saloon Cook-Off", incident.EventName);
        Assert.Equal(
            "A friendly cooking contest strains old rivalries in Pelican Town.",
            incident.IncidentTheme);

        Assert.True(TownIncidentArchetypeCatalog.TryGetDefinition(incident.ArchetypeId, out var roleDefinition));
        Assert.Equal(
            roleDefinition.RequiredRoles.OrderBy(x => x, StringComparer.Ordinal).ToArray(),
            incident.AssignedRoles.Keys.OrderBy(x => x, StringComparer.Ordinal).ToArray());
        Assert.Equal("Gus", incident.AssignedRoles["Host"]);
        Assert.Equal("Abigail", incident.AssignedRoles["Champion"]);
        Assert.Equal("Alex", incident.AssignedRoles["Skeptic"]);

        Assert.Equal(
            new[] { IncidentPhase.Inception, IncidentPhase.Escalation, IncidentPhase.Climax },
            incident.PhaseScripts.Keys.OrderBy(x => x).ToArray());
        Assert.NotEmpty(incident.BranchOutcomes);
        Assert.NotNull(incident.RuntimeFlags);

        // Persisted contract stays readable by the catalog: no migration needed.
        Assert.True(TownIncidentArchetypeCatalog.TryGetDefinition(incident.ArchetypeId, out var definition));
        Assert.Equal(incident.DurationDays, definition.DefaultDurationDays);
        Assert.Equal(incident.ClimaxLocation, definition.DefaultClimaxLocation);
        Assert.Equal(incident.ClimaxTimeOfDay, definition.DefaultClimaxTimeOfDay);
        Assert.Equal(incident.EventName, definition.EventName);
        Assert.Equal(incident.IncidentTheme, definition.IncidentTheme);
        Assert.Equal(
            incident.AssignedRoles.Keys.OrderBy(x => x, StringComparer.Ordinal).ToArray(),
            definition.RequiredRoles.OrderBy(x => x, StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public void UT09_EventSlotContract_KeepsPersistedPropertyNames()
    {
        string json = JsonConvert.SerializeObject(BuildPersistedContest().ActiveIncident);

        foreach (string name in new[]
        {
            "IncidentId", "ArchetypeId", "StartGameDay", "DurationDays", "ClimaxLocation",
            "ClimaxTimeOfDay", "AssignedRoles", "EventName", "IncidentTheme",
            "PhaseScripts", "BranchOutcomes", "RuntimeFlags",
        })
        {
            Assert.Contains($"\"{name}\"", json, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void UT10_TownIncidentData_GainsNoNewMembers()
    {
        Assert.Equal(
            new[] { "ActiveIncident", "LastScheduleKey", "SchemaVersion" },
            typeof(TownIncidentData)
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(x => x.Name)
                .OrderBy(x => x, StringComparer.Ordinal)
                .ToArray());
    }
}
