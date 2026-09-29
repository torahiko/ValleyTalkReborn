// TownIncidentRumorArchetypeTests.cs
// TIE-009D: archetype-aware incident rumor rendering.
// Drives the single renderer (TownIncidentRumorProvider.TryRenderIncidentRumor)
// directly and through the relay claim/preview paths. Engine memory state is
// injected by reflection; Context.IsMultiplayer stays false via the headless
// shim; LocalizedContentManager.CurrentLanguageCode is a plain settable static.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using StardewModdingAPI;
using StardewModdingAPI.Framework.Logging;
using StardewValley;
using ValleytalkReborn;
using Xunit;

[Collection("StaticGlobalStateCollection")]
public class TownIncidentRumorArchetypeTests
{
    private const string RumorNpc = "Lewis";
    private static readonly string[] RoleNpcs = { "Gus", "Abigail", "Alex" };

    // Pre-TIE-009D Contest output, captured verbatim from the retired
    // TownIncidentRumorRelay.BuildContestRumor renderer.
    private const string LegacyContestEn =
        "Lewis has heard the talk of the town: Gus is hosting the Saloon Cook-Off, Abigail is out to defend the title, and Alex keeps telling anyone who will listen that the judging favors the regulars.";

    private const string LegacyContestZh =
        "Lewis 听说了镇上最近的热议：Gus 要在酒吧办一场烹饪大赛，Abigail 准备卫冕冠军，而 Alex 见人就嘀咕评审偏袒熟面孔。";

    private readonly IMonitor _originalMonitor;

    public TownIncidentRumorArchetypeTests()
    {
        TestEnvironment.InstallHeadlessContext();
        LocalizedContentManager.CurrentLanguageCode = LocalizedContentManager.LanguageCode.en;
        _originalMonitor = ModEntry.SMonitor;
    }

    private sealed class CapturingMonitor : IMonitor
    {
        public readonly List<string> Messages = new List<string>();
        public bool IsVerbose => false;
        public void Log(string message, LogLevel level) => Messages.Add($"[{level}] {message}");
        public void LogOnce(string message, LogLevel level) => Messages.Add($"[{level}] {message}");
        public void VerboseLog(string message) { }
        public void VerboseLog(ref VerboseLogStringHandler handler) { }
    }

    private static CapturingMonitor InstallCapturingMonitor()
    {
        var capture = new CapturingMonitor();
        ModEntry.SMonitor = capture;
        return capture;
    }

    private static void SetEngineData(TownIncidentData data)
    {
        typeof(TownIncidentEngine).GetField("_data", BindingFlags.NonPublic | BindingFlags.Static)
            .SetValue(null, data);
    }

    private static EventSlotContract BuildShell(string archetypeId)
    {
        Assert.True(
            TownIncidentArchetypeCatalog.TryGetDefinition(archetypeId, out var definition),
            $"archetype '{archetypeId}' must exist");

        var roles = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < definition.RequiredRoles.Count; i++)
            roles[definition.RequiredRoles[i]] = RoleNpcs[i];

        var incident = new EventSlotContract
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
        TownIncidentScriptwriter.CreateFallback(incident, isChinese: false);
        return incident;
    }

    private static void ResetWithIncident(EventSlotContract incident)
    {
        LocalizedContentManager.CurrentLanguageCode = LocalizedContentManager.LanguageCode.en;
        TownIncidentRumorProvider.ResetDailyRumorQuota();
        SetEngineData(new TownIncidentData { SchemaVersion = 1, ActiveIncident = incident });
    }

    private static string Render(EventSlotContract incident, string npcName, bool isChinese)
    {
        LocalizedContentManager.CurrentLanguageCode = isChinese
            ? LocalizedContentManager.LanguageCode.zh
            : LocalizedContentManager.LanguageCode.en;

        Assert.True(
            TownIncidentRumorProvider.TryRenderIncidentRumor(incident, npcName, out string rumor),
            $"render must succeed for archetype '{incident.ArchetypeId}'");
        return rumor;
    }

    // ── 验收：Contest 输出逐字节保持 ──

    [Fact]
    public void UT01_ContestRumor_ByteIdentical_English()
    {
        var contest = BuildShell("Contest");
        Assert.Equal(LegacyContestEn, Render(contest, RumorNpc, isChinese: false));
    }

    [Fact]
    public void UT02_ContestRumor_ByteIdentical_Chinese()
    {
        var contest = BuildShell("Contest");
        Assert.Equal(LegacyContestZh, Render(contest, RumorNpc, isChinese: true));
    }

    [Fact]
    public void UT03_ContestRumor_ByteIdentical_ThroughClaimPath()
    {
        ResetWithIncident(BuildShell("Contest"));

        Assert.True(TownIncidentRumorProvider.TryClaimMainDialogueRumor(RumorNpc, out var en));
        Assert.Equal(LegacyContestEn, en);

        TownIncidentRumorProvider.ResetDailyRumorQuota();
        LocalizedContentManager.CurrentLanguageCode = LocalizedContentManager.LanguageCode.zh;

        Assert.True(TownIncidentRumorProvider.TryClaimMainDialogueRumor(RumorNpc, out var zh));
        Assert.Equal(LegacyContestZh, zh);
    }

    // ── 验收：四个原型双语渲染 ──

    [Fact]
    public void UT04_AllArchetypes_RenderBilingualLines()
    {
        foreach (string archetypeId in new[] { "Contest", "Friction", "Mystery", "Collaboration" })
        {
            var incident = BuildShell(archetypeId);

            string en = Render(incident, RumorNpc, isChinese: false);
            string zh = Render(incident, RumorNpc, isChinese: true);

            Assert.False(string.IsNullOrWhiteSpace(en), archetypeId);
            Assert.False(string.IsNullOrWhiteSpace(zh), archetypeId);
            Assert.NotEqual(en, zh);

            foreach (string text in new[] { en, zh })
            {
                Assert.Contains(RumorNpc, text);
                foreach (string npc in incident.AssignedRoles.Values)
                    Assert.Contains(npc, text);

                // Contest keeps its frozen legacy wording (the Chinese line
                // names the cook-off in prose); the newer archetypes embed
                // EventName in both locales.
                if (!string.Equals(archetypeId, "Contest", StringComparison.Ordinal))
                    Assert.Contains(incident.EventName, text);
            }

            Assert.StartsWith($"{RumorNpc} has heard the talk of the town: ", en);
            Assert.StartsWith($"{RumorNpc} 听说了镇上最近的热议：", zh);
        }
    }

    [Fact]
    public void UT05_Render_IsDeterministic()
    {
        foreach (string archetypeId in new[] { "Contest", "Friction", "Mystery", "Collaboration" })
        {
            var incident = BuildShell(archetypeId);
            Assert.Equal(Render(incident, RumorNpc, false), Render(incident, RumorNpc, false));
            Assert.Equal(Render(incident, RumorNpc, true), Render(incident, RumorNpc, true));
        }
    }

    [Fact]
    public void UT06_ArchetypeSwap_ChangesRenderedLine()
    {
        var lines = new HashSet<string>(StringComparer.Ordinal);
        foreach (string archetypeId in new[] { "Contest", "Friction", "Mystery", "Collaboration" })
            lines.Add(Render(BuildShell(archetypeId), RumorNpc, false));

        Assert.Equal(4, lines.Count);
    }

    // ── 验收：缺角色 ⇒ 失败 + Error 日志 + 零配额 ──

    [Fact]
    public void UT07_MissingRole_Fails_WithErrorLog_AndNoQuota()
    {
        foreach (string archetypeId in new[] { "Contest", "Friction", "Mystery", "Collaboration" })
        {
            var definition = TownIncidentArchetypeCatalog.Definitions
                .Single(x => string.Equals(x.ArchetypeId, archetypeId, StringComparison.Ordinal));

            foreach (string missingRole in definition.RequiredRoles)
            {
                var broken = BuildShell(archetypeId);
                broken.AssignedRoles.Remove(missingRole);
                ResetWithIncident(broken);

                var capture = InstallCapturingMonitor();
                try
                {
                    Assert.False(
                        TownIncidentRumorProvider.TryClaimMainDialogueRumor(RumorNpc, out var text),
                        $"{archetypeId}/{missingRole}");
                    Assert.Null(text);

                    // 零配额消耗：合法事件下同一 NPC 仍可认领两次配额。
                    Assert.False(TownIncidentRumorRelay.HasClaimedToday(RumorNpc));
                    ResetWithIncident(BuildShell(archetypeId));
                    Assert.True(TownIncidentRumorProvider.TryClaimMainDialogueRumor(RumorNpc, out _));
                    Assert.True(TownIncidentRumorProvider.TryClaimMainDialogueRumor("Marnie", out _));

                    Assert.Contains(
                        capture.Messages,
                        m => m.StartsWith("[Error]", StringComparison.Ordinal)
                             && m.Contains(broken.IncidentId)
                             && m.Contains(missingRole));
                }
                finally
                {
                    ModEntry.SMonitor = _originalMonitor;
                }
            }
        }
    }

    [Fact]
    public void UT08_BlankRoleValue_Fails_LikeMissingRole()
    {
        var blank = BuildShell("Friction");
        blank.AssignedRoles["Witness"] = "   ";

        Assert.False(TownIncidentRumorProvider.TryRenderIncidentRumor(blank, RumorNpc, out var text));
        Assert.Null(text);
    }

    // ── 验收：未知原型不回退到 Contest ──

    [Fact]
    public void UT09_UnknownArchetype_NoContestFallback()
    {
        var unknown = BuildShell("Contest");
        unknown.ArchetypeId = "UnknownArchetype";
        ResetWithIncident(unknown);

        Assert.False(TownIncidentRumorProvider.TryRenderIncidentRumor(unknown, RumorNpc, out var text));
        Assert.Null(text);

        var capture = InstallCapturingMonitor();
        try
        {
            Assert.False(TownIncidentRumorRelay.TryGetIncidentRumorPreview(RumorNpc, out var preview));
            Assert.Null(preview);
            Assert.False(TownIncidentRumorRelay.HasClaimedToday(RumorNpc));

            Assert.Contains(
                capture.Messages,
                m => m.StartsWith("[Error]", StringComparison.Ordinal) && m.Contains("UnknownArchetype"));
        }
        finally
        {
            ModEntry.SMonitor = null;
        }
    }

    // ── 验收：认领路径与预览路径共用同一渲染器 ──

    [Fact]
    public void UT10_PreviewAndClaim_ShareSingleRenderer()
    {
        foreach (string archetypeId in new[] { "Contest", "Friction", "Mystery", "Collaboration" })
        {
            var incident = BuildShell(archetypeId);
            ResetWithIncident(incident);

            string rendered = Render(incident, RumorNpc, isChinese: false);

            Assert.True(TownIncidentRumorRelay.TryGetIncidentRumorPreview(RumorNpc, out var preview));
            Assert.Equal(rendered, preview);

            Assert.True(TownIncidentRumorProvider.TryClaimMainDialogueRumor(RumorNpc, out var claimed));
            Assert.Equal(rendered, claimed);
        }
    }

    // ── 验收：单一渲染实现（无第二个按原型构建的传闻器）──

    [Fact]
    public void UT11_RelayHasNoSecondRumorBuilder()
    {
        var strayBuilders = typeof(TownIncidentRumorRelay)
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            .Where(m => m.Name.Contains("Rumor") && m.Name.StartsWith("Build"))
            .ToArray();
        Assert.Empty(strayBuilders);

        Assert.NotNull(typeof(TownIncidentRumorProvider).GetMethod(
            "TryRenderIncidentRumor", BindingFlags.NonPublic | BindingFlags.Static));

        var catalogBuilders = typeof(TownIncidentTemplateCatalog)
            .GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
            .Where(m => m.Name.Contains("Rumor"))
            .ToArray();
        Assert.Single(catalogBuilders);
    }

    // ── 验收（TIE-009D-R1）：剥离器 / Bark 改写对所有原型往返成立 ──

    private static string StripSubjectPrefix(string renderedLine)
    {
        var method = typeof(TownIncidentRumorRelay).GetMethod(
            "StripSubjectPrefix", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        return (string)method.Invoke(null, new object[] { renderedLine });
    }

    [Fact]
    public void UT12_StripSubjectPrefix_RoundTrip_AllArchetypes()
    {
        foreach (string archetypeId in new[] { "Contest", "Friction", "Mystery", "Collaboration" })
        {
            var incident = BuildShell(archetypeId);

            string en = Render(incident, RumorNpc, isChinese: false);
            string strippedEn = StripSubjectPrefix(en);
            Assert.False(string.IsNullOrWhiteSpace(strippedEn), archetypeId);
            Assert.DoesNotContain("has heard the talk of the town", strippedEn);

            string zh = Render(incident, RumorNpc, isChinese: true);
            string strippedZh = StripSubjectPrefix(zh);
            Assert.False(string.IsNullOrWhiteSpace(strippedZh), archetypeId);
            Assert.DoesNotContain("听说了镇上最近的热议", strippedZh);
        }
    }

    [Fact]
    public void UT13_BarkRewrite_RoundTrip_AllArchetypes()
    {
        foreach (string archetypeId in new[] { "Contest", "Friction", "Mystery", "Collaboration" })
        {
            var incident = BuildShell(archetypeId);

            string en = Render(incident, RumorNpc, isChinese: false);
            string barkEn = BarkFocusRouter.FormatIncidentRumorForBark(en, isZh: false);
            Assert.False(string.IsNullOrWhiteSpace(barkEn), archetypeId);
            Assert.DoesNotContain("has heard the talk of the town", barkEn);
            Assert.DoesNotContain("听说了镇上最近的热议", barkEn);
            Assert.StartsWith("You've caught the talk going around town — ", barkEn);

            string zh = Render(incident, RumorNpc, isChinese: true);
            string barkZh = BarkFocusRouter.FormatIncidentRumorForBark(zh, isZh: true);
            Assert.False(string.IsNullOrWhiteSpace(barkZh), archetypeId);
            Assert.DoesNotContain("听说了镇上最近的热议", barkZh);
            Assert.DoesNotContain("has heard the talk of the town", barkZh);
            Assert.StartsWith("你听过镇上最近在传的说法——", barkZh);
        }
    }

    [Fact]
    public void UT14_StripSubjectPrefix_MissingSeparator_BugPath()
    {
        LocalizedContentManager.CurrentLanguageCode = LocalizedContentManager.LanguageCode.en;
        Assert.Null(StripSubjectPrefix("no separator here"));

        LocalizedContentManager.CurrentLanguageCode = LocalizedContentManager.LanguageCode.zh;
        Assert.Null(StripSubjectPrefix("no separator here"));
    }
}
