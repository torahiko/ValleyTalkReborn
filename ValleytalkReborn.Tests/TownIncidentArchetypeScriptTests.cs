// TownIncidentArchetypeScriptTests.cs
// TIE-009C: pure contract tests for the per-archetype fallback scripts, the
// creation-time shell gate (TryValidateShell) and the parameterized prompt
// timeline. No Game1 / SMAPI / Harmony / LLM dependency: every path exercised
// here is template + validation code, so no world or save state is installed.

using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using StardewModdingAPI;
using ValleytalkReborn;
using Xunit;

[Collection("StaticGlobalStateCollection")]
public class TownIncidentArchetypeScriptTests : IDisposable
{
    private static readonly string[] ArchetypeIds =
    {
        "Contest", "Friction", "Mystery", "Collaboration",
    };

    // One cast per archetype — deliberately not the same three NPCs, so the
    // tests prove the role-keyed fallback is projected onto whatever cast the
    // shell actually carries.
    private static readonly Dictionary<string, string[]> Casts = new(StringComparer.Ordinal)
    {
        ["Contest"] = new[] { "Gus", "Abigail", "Alex" },
        ["Friction"] = new[] { "Marnie", "Pierre", "Caroline" },
        ["Mystery"] = new[] { "Jodi", "Leah", "Elliott" },
        ["Collaboration"] = new[] { "Robin", "Demetrius", "Sam" },
    };

    // Pre-TIE-009C output of BuildUserPrompt for the 8-day Contest shell,
    // captured verbatim from the production template.
    private const string LegacyContestPromptEn =
        "Incident \"Saloon Cook-Off\" (contest:1:Spring:4), archetype Contest. "
        + "Theme: A friendly cooking contest strains old rivalries in Pelican Town..\n"
        + "Roles (echo exactly): Host=Gus; Champion=Abigail; Skeptic=Alex.\n"
        + "Timeline: 8 days starting game day 4. Phases: Inception = elapsed days 0-2, "
        + "Escalation = 3-5, Climax = 6-7 (finale at the Saloon, 1900).\n"
        + "Branch group keys (use exactly these): backed_champion, backed_skeptic, stayed_neutral.\n"
        + "Write the complete JSON object now.";

    private const string LegacyContestPromptZh =
        "事件「Saloon Cook-Off」（contest:1:Spring:4），原型 Contest。"
        + "主题：A friendly cooking contest strains old rivalries in Pelican Town.。\n"
        + "角色（原样回显）：Host=Gus；Champion=Abigail；Skeptic=Alex。\n"
        + "时间线：自游戏第 4 天起共 8 天。阶段：Inception = 第 0-2 天，Escalation = 第 3-5 天，"
        + "Climax = 第 6-7 天（决赛地点 Saloon，时间 1900）。\n"
        + "分支组键（必须原样使用）：backed_champion, backed_skeptic, stayed_neutral。\n"
        + "现在输出完整 JSON 对象。";

    private readonly IMonitor _originalMonitor;

    public TownIncidentArchetypeScriptTests()
    {
        _originalMonitor = ModEntry.SMonitor;
    }

    public void Dispose()
    {
        ModEntry.SMonitor = _originalMonitor;
    }

    private sealed class CapturingMonitor : IMonitor
    {
        public readonly List<string> Messages = new();
        public bool IsVerbose => false;
        public void Log(string message, LogLevel level) => Messages.Add($"[{level}] {message}");
        public void LogOnce(string message, LogLevel level) => Messages.Add($"[{level}] {message}");
        public void VerboseLog(string message) { }
        public void VerboseLog(ref StardewModdingAPI.Framework.Logging.VerboseLogStringHandler handler) { }
    }

    // ── 辅助：按原型构造 shell 与候选剧本 ──

    private static IncidentArchetypeDefinition Definition(string archetypeId)
    {
        Assert.True(
            TownIncidentArchetypeCatalog.TryGetDefinition(archetypeId, out var definition),
            $"archetype '{archetypeId}' must resolve");
        return definition;
    }

    private static EventSlotContract BuildShell(string archetypeId, bool isChinese = false)
    {
        var definition = Definition(archetypeId);
        string[] cast = Casts[archetypeId];

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

    private static string MakeRawJson(
        EventSlotContract shell,
        bool includeClimax = true,
        string dropNpc = null,
        string roleOverrideNpc = null,
        bool dropFirstBranch = false,
        string branchKeyOverride = null,
        string archetypeOverride = null)
    {
        var briefs = new Dictionary<string, object>();
        foreach (string npc in shell.AssignedRoles.Values)
        {
            if (dropNpc != null && string.Equals(npc, dropNpc, StringComparison.Ordinal))
                continue;
            briefs[npc] = new { Motivation = "Short.", PublicOpinion = "Short." };
        }

        var phases = new Dictionary<string, object>
        {
            ["Inception"] = briefs,
            ["Escalation"] = briefs,
        };
        if (includeClimax)
            phases["Climax"] = briefs;

        var roles = new Dictionary<string, string>(shell.AssignedRoles, StringComparer.Ordinal);
        if (roleOverrideNpc != null)
            roles[shell.AssignedRoles.Keys.First()] = roleOverrideNpc;

        var branches = new Dictionary<string, object>();
        bool first = true;
        foreach (var group in shell.BranchOutcomes)
        {
            if (dropFirstBranch && first)
            {
                first = false;
                continue;
            }

            string key = branchKeyOverride != null && first ? branchKeyOverride : group.Key;
            branches[key] = new Dictionary<string, string> { ["keyword"] = "Outcome." };
            first = false;
        }

        var root = new
        {
            IncidentId = shell.IncidentId,
            ArchetypeId = archetypeOverride ?? shell.ArchetypeId,
            AssignedRoles = roles,
            EventName = shell.EventName,
            IncidentTheme = shell.IncidentTheme,
            PhaseScripts = phases,
            BranchOutcomes = branches,
        };

        return JsonConvert.SerializeObject(root);
    }

    private static EventSlotContract Parse(EventSlotContract shell, string rawJson)
    {
        var candidate = TownIncidentScriptwriter.TryParseScript(rawJson, shell);
        Assert.NotNull(candidate);
        return candidate;
    }

    // ── 验收：8 天 Contest 提示词逐字节保持 ──

    [Fact]
    public void UT01_ContestPrompt_ByteIdentical_English()
    {
        var shell = BuildShell("Contest");
        Assert.Equal(8, shell.DurationDays);
        Assert.Equal(LegacyContestPromptEn, TownIncidentTemplateCatalog.BuildUserPrompt(shell, isChinese: false));
    }

    [Fact]
    public void UT02_ContestPrompt_ByteIdentical_Chinese()
    {
        var shell = BuildShell("Contest", isChinese: true);
        Assert.Equal(LegacyContestPromptZh, TownIncidentTemplateCatalog.BuildUserPrompt(shell, isChinese: true));
    }

    // ── 验收：时间线按 DurationDays 参数化（ceil(D/3) 边界）──

    [Theory]
    [InlineData("Contest")]
    [InlineData("Friction")]
    [InlineData("Mystery")]
    [InlineData("Collaboration")]
    public void UT03_PhaseWindow_FollowsDuration(string archetypeId)
    {
        var shell = BuildShell(archetypeId);
        int duration = shell.DurationDays;

        string en = TownIncidentTemplateCatalog.BuildUserPrompt(shell, isChinese: false);
        string zh = TownIncidentTemplateCatalog.BuildUserPrompt(shell, isChinese: true);

        int boundary = (duration + 2) / 3;
        Assert.Contains($"Timeline: {duration} days starting game day {shell.StartGameDay}.", en);
        Assert.Contains(
            $"Inception = elapsed days 0-{boundary - 1}, "
            + $"Escalation = {boundary}-{2 * boundary - 1}, "
            + $"Climax = {2 * boundary}-{duration - 1}",
            en);
        Assert.Contains(
            $"Inception = 第 0-{boundary - 1} 天，"
            + $"Escalation = 第 {boundary}-{2 * boundary - 1} 天，"
            + $"Climax = 第 {2 * boundary}-{duration - 1} 天",
            zh);
    }

    [Fact]
    public void UT04_PhaseWindowText_MatchesBoundaryFormula()
    {
        Assert.Equal(
            "Inception = elapsed days 0-2, Escalation = 3-5, Climax = 6-7",
            TownIncidentTemplateCatalog.BuildPhaseWindowText(8, isChinese: false));
        Assert.Equal(
            "Inception = 第 0-2 天，Escalation = 第 3-5 天，Climax = 第 6-7 天",
            TownIncidentTemplateCatalog.BuildPhaseWindowText(8, isChinese: true));

        // Six-day archetypes: boundary = ceil(6/3) = 2.
        Assert.Equal(
            "Inception = elapsed days 0-1, Escalation = 2-3, Climax = 4-5",
            TownIncidentTemplateCatalog.BuildPhaseWindowText(6, isChinese: false));
        Assert.Equal(
            "Inception = 第 0-1 天，Escalation = 第 2-3 天，Climax = 第 4-5 天",
            TownIncidentTemplateCatalog.BuildPhaseWindowText(6, isChinese: true));
    }

    // ── 验收：四个原型都有完整的双语回退 ──

    [Theory]
    [InlineData("Contest")]
    [InlineData("Friction")]
    [InlineData("Mystery")]
    [InlineData("Collaboration")]
    public void UT05_Fallback_CoversAllPhasesAndRoles(string archetypeId)
    {
        foreach (bool isChinese in new[] { false, true })
        {
            var shell = BuildShell(archetypeId, isChinese);
            var definition = Definition(archetypeId);

            Assert.Equal(
                new[] { IncidentPhase.Inception, IncidentPhase.Escalation, IncidentPhase.Climax },
                shell.PhaseScripts.Keys.OrderBy(x => x).ToArray());

            foreach (var phaseKv in shell.PhaseScripts)
            {
                Assert.Equal(
                    definition.RequiredRoles.Select(role => shell.AssignedRoles[role])
                        .OrderBy(x => x, StringComparer.Ordinal).ToArray(),
                    phaseKv.Value.Keys.OrderBy(x => x, StringComparer.Ordinal).ToArray());

                foreach (var brief in phaseKv.Value.Values)
                {
                    Assert.False(string.IsNullOrWhiteSpace(brief.Motivation));
                    Assert.False(string.IsNullOrWhiteSpace(brief.PublicOpinion));
                    Assert.True(brief.Motivation.Length <= TownIncidentScriptwriter.MaxBriefTextLength);
                    Assert.True(brief.PublicOpinion.Length <= TownIncidentScriptwriter.MaxBriefTextLength);
                }
            }

            Assert.NotEmpty(shell.BranchOutcomes);
            Assert.All(shell.BranchOutcomes.Values, group => Assert.NotEmpty(group));
            Assert.NotNull(shell.RuntimeFlags);

            // The installed fallback is itself a valid candidate for the shell.
            Assert.True(TownIncidentScriptwriter.TryValidate(shell, shell, out var error), error);
        }
    }

    [Theory]
    [InlineData("Contest")]
    [InlineData("Friction")]
    [InlineData("Mystery")]
    [InlineData("Collaboration")]
    public void UT06_Fallback_IsArchetypeSpecific(string archetypeId)
    {
        var shell = BuildShell(archetypeId);
        string[] expectedGroups = archetypeId switch
        {
            "Contest" => new[] { "backed_champion", "backed_skeptic", "stayed_neutral" },
            "Friction" => new[] { "backed_victim", "backed_culprit", "stayed_neutral" },
            "Mystery" => new[] { "backed_loser", "backed_suspect", "stayed_neutral" },
            "Collaboration" => new[] { "backed_organizer", "backed_worker", "stayed_neutral" },
            _ => throw new ArgumentOutOfRangeException(nameof(archetypeId)),
        };

        Assert.Equal(
            expectedGroups.OrderBy(x => x, StringComparer.Ordinal).ToArray(),
            shell.BranchOutcomes.Keys.OrderBy(x => x, StringComparer.Ordinal).ToArray());

        // No archetype borrows the Contest content.
        if (archetypeId != "Contest")
            Assert.DoesNotContain("backed_champion", shell.BranchOutcomes.Keys);
    }

    [Fact]
    public void UT07_Fallbacks_DifferAcrossArchetypes()
    {
        var texts = new HashSet<string>(StringComparer.Ordinal);
        foreach (string archetypeId in ArchetypeIds)
        {
            var shell = BuildShell(archetypeId);
            foreach (var phaseKv in shell.PhaseScripts)
                foreach (var brief in phaseKv.Value.Values)
                    texts.Add(brief.Motivation);
        }

        // 4 archetypes x 3 roles x 3 phases = 36 distinct motivations.
        Assert.Equal(36, texts.Count);
    }

    // ── 验收：TryValidateShell 门控 ──

    [Theory]
    [InlineData("Contest")]
    [InlineData("Friction")]
    [InlineData("Mystery")]
    [InlineData("Collaboration")]
    public void UT08_TryValidateShell_AcceptsMatchingRoleKeys(string archetypeId)
    {
        var shell = BuildShell(archetypeId);
        Assert.True(TownIncidentScriptwriter.TryValidateShell(shell, out string error), error);
        Assert.Null(error);
    }

    [Fact]
    public void UT09_TryValidateShell_RejectsUnknownArchetype()
    {
        foreach (string unknown in new[] { "contest", "CONTEST", "Contest ", "Unknown", "", null })
        {
            var shell = BuildShell("Contest");
            shell.ArchetypeId = unknown;

            Assert.False(TownIncidentScriptwriter.TryValidateShell(shell, out string error), unknown ?? "<null>");
            Assert.Contains("unknown archetype id", error);
        }
    }

    [Fact]
    public void UT10_TryValidateShell_RejectsWrongRoleKeySet()
    {
        // Missing required role.
        var missing = BuildShell("Friction");
        missing.AssignedRoles.Remove("Witness");
        Assert.False(TownIncidentScriptwriter.TryValidateShell(missing, out string missingError));
        Assert.Contains("does not match required role count", missingError);

        // Extra role beyond RequiredRoles.
        var extra = BuildShell("Mystery");
        extra.AssignedRoles["Bystander"] = "Lewis";
        Assert.False(TownIncidentScriptwriter.TryValidateShell(extra, out string extraError));
        Assert.Contains("does not match required role count", extraError);

        // Renamed role: same count, wrong key.
        var renamed = BuildShell("Collaboration");
        string organizer = renamed.AssignedRoles["Organizer"];
        renamed.AssignedRoles.Remove("Organizer");
        renamed.AssignedRoles["Lead"] = organizer;
        Assert.False(TownIncidentScriptwriter.TryValidateShell(renamed, out string renamedError));
        Assert.Contains("required role 'Organizer' is not assigned", renamedError);
    }

    // ── 验收：未知原型不安装 Contest 回退 ──

    [Fact]
    public void UT11_UnknownArchetype_NoContestFallback()
    {
        var capture = new CapturingMonitor();
        ModEntry.SMonitor = capture;

        var shell = new EventSlotContract
        {
            IncidentId = "unknown:1:Spring:4",
            ArchetypeId = "Unknown",
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
            EventName = "Unknown Incident",
            IncidentTheme = "An archetype the catalog does not know.",
        };

        var returned = TownIncidentScriptwriter.CreateFallback(shell, isChinese: false);

        Assert.Same(shell, returned);
        Assert.Null(shell.PhaseScripts);
        Assert.Null(shell.BranchOutcomes);
        Assert.Null(shell.RuntimeFlags);
        Assert.Contains(capture.Messages, m => m.Contains("[Error]") && m.Contains("no static fallback"));
    }

    // ── 验收：各原型的畸形 / 违规剧本一律拒绝 ──

    [Theory]
    [InlineData("Contest")]
    [InlineData("Friction")]
    [InlineData("Mystery")]
    [InlineData("Collaboration")]
    public void UT12_MalformedOutput_NeverProducesCandidate(string archetypeId)
    {
        var shell = BuildShell(archetypeId);

        Assert.Null(TownIncidentScriptwriter.TryParseScript($"The {archetypeId} incident will be great!", shell));
        Assert.Null(TownIncidentScriptwriter.TryParseScript("   ", shell));

        string truncated = MakeRawJson(shell).TrimEnd('}');
        var candidate = TownIncidentScriptwriter.TryParseScript(truncated, shell);
        Assert.True(candidate == null || !TownIncidentScriptwriter.TryValidate(candidate, shell, out _));
    }

    [Theory]
    [InlineData("Contest")]
    [InlineData("Friction")]
    [InlineData("Mystery")]
    [InlineData("Collaboration")]
    public void UT13_MissingPhase_Rejected(string archetypeId)
    {
        var shell = BuildShell(archetypeId);
        var candidate = Parse(shell, MakeRawJson(shell, includeClimax: false));

        Assert.False(TownIncidentScriptwriter.TryValidate(candidate, shell, out string error));
        Assert.Contains("Climax", error);
    }

    [Theory]
    [InlineData("Contest")]
    [InlineData("Friction")]
    [InlineData("Mystery")]
    [InlineData("Collaboration")]
    public void UT14_MissingAssignedNpcBrief_Rejected(string archetypeId)
    {
        var shell = BuildShell(archetypeId);
        string dropped = shell.AssignedRoles.Values.First();
        var candidate = Parse(shell, MakeRawJson(shell, dropNpc: dropped));

        Assert.False(TownIncidentScriptwriter.TryValidate(candidate, shell, out string error));
        Assert.Contains($"has no brief for assigned NPC '{dropped}'", error);
    }

    [Theory]
    [InlineData("Contest")]
    [InlineData("Friction")]
    [InlineData("Mystery")]
    [InlineData("Collaboration")]
    public void UT15_ChangedRoleAssignmentAndArchetype_Rejected(string archetypeId)
    {
        var shell = BuildShell(archetypeId);

        var changedRoles = Parse(shell, MakeRawJson(shell, roleOverrideNpc: "Lewis"));
        Assert.False(TownIncidentScriptwriter.TryValidate(changedRoles, shell, out string roleError));
        Assert.Contains("role", roleError);

        var changedArchetype = Parse(shell, MakeRawJson(shell, archetypeOverride: "Contest"));
        bool ok = TownIncidentScriptwriter.TryValidate(changedArchetype, shell, out string archetypeError);
        if (archetypeId != "Contest")
        {
            Assert.False(ok);
            Assert.Contains("ArchetypeId", archetypeError);
        }
        else
        {
            Assert.True(ok, archetypeError);
        }
    }

    [Theory]
    [InlineData("Contest")]
    [InlineData("Friction")]
    [InlineData("Mystery")]
    [InlineData("Collaboration")]
    public void UT16_BranchKeyViolations_Rejected(string archetypeId)
    {
        var shell = BuildShell(archetypeId);

        var missingBranch = Parse(shell, MakeRawJson(shell, dropFirstBranch: true));
        Assert.False(TownIncidentScriptwriter.TryValidate(missingBranch, shell, out string missingError));
        Assert.Contains("branch group key set changed", missingError);

        var unknownBranch = Parse(shell, MakeRawJson(shell, branchKeyOverride: "unexpected_group"));
        Assert.False(TownIncidentScriptwriter.TryValidate(unknownBranch, shell, out string unknownError));
        Assert.Contains("branch group", unknownError);
    }

    [Theory]
    [InlineData("Contest")]
    [InlineData("Friction")]
    [InlineData("Mystery")]
    [InlineData("Collaboration")]
    public void UT17_ValidCandidate_EchoesShellExactly(string archetypeId)
    {
        var shell = BuildShell(archetypeId);
        var candidate = Parse(shell, "Here is your script:\n```json\n" + MakeRawJson(shell) + "\n```\nDone!");

        Assert.True(TownIncidentScriptwriter.TryValidate(candidate, shell, out string error), error);
        Assert.Equal(shell.ArchetypeId, candidate.ArchetypeId);
        Assert.Equal(
            shell.AssignedRoles.OrderBy(x => x.Key, StringComparer.Ordinal).ToArray(),
            candidate.AssignedRoles.OrderBy(x => x.Key, StringComparer.Ordinal).ToArray());
        Assert.Equal(
            shell.BranchOutcomes.Keys.OrderBy(x => x, StringComparer.Ordinal).ToArray(),
            candidate.BranchOutcomes.Keys.OrderBy(x => x, StringComparer.Ordinal).ToArray());
    }
}
