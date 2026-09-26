// TownIncidentRumorTests.cs
// TIE-006: deterministic quota tests for TownIncidentRumorProvider (TIE-004).
// Pure-model tests: the provider touches only engine memory state
// (reflection-injected), Context.IsMultiplayer (false outside a game) and
// LocalizedContentManager.CurrentLanguageCode (plain static, settable).

using System;
using System.Collections.Generic;
using System.Reflection;
using StardewValley;
using ValleytalkReborn;
using Xunit;

[Collection("StaticGlobalStateCollection")]
public class TownIncidentRumorTests
{
    public TownIncidentRumorTests()
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

        Game1.hasLocalClientsOnly = false;
        var runner = System.Runtime.Serialization.FormatterServices
            .GetUninitializedObject(typeof(GameRunner));
        var instancesField = typeof(GameRunner)
            .GetField("gameInstances", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
        instancesField?.SetValue(runner, Activator.CreateInstance(instancesField.FieldType));
        typeof(GameRunner)
            .GetField("instance", BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)
            ?.SetValue(null, runner);
    }

    private static void SetEngineData(TownIncidentData data)
    {
        typeof(TownIncidentEngine).GetField("_data", BindingFlags.NonPublic | BindingFlags.Static)
            .SetValue(null, data);
    }

    private static EventSlotContract BuildIncident()
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
        return incident;
    }

    private static void ResetWithActiveIncident()
    {
        LocalizedContentManager.CurrentLanguageCode = LocalizedContentManager.LanguageCode.en;
        TownIncidentRumorProvider.ResetDailyRumorQuota();
        SetEngineData(new TownIncidentData { SchemaVersion = 1, ActiveIncident = BuildIncident() });
    }

    // ── 验收：每日配额上限为 2 ──

    [Fact]
    public void UT01_QuotaMaximum_TwoPerDay()
    {
        ResetWithActiveIncident();

        Assert.True(TownIncidentRumorProvider.TryClaimMainDialogueRumor("Pierre", out _));
        Assert.True(TownIncidentRumorProvider.TryClaimMainDialogueRumor("Marnie", out _));
        Assert.False(TownIncidentRumorProvider.TryClaimMainDialogueRumor("Penny", out var denied));
        Assert.Null(denied);
    }

    // ── 验收：同一 NPC 当日去重（大小写不敏感）──

    [Fact]
    public void UT02_SameNpc_Deduplicated()
    {
        ResetWithActiveIncident();

        Assert.True(TownIncidentRumorProvider.TryClaimMainDialogueRumor("Pierre", out _));
        Assert.False(TownIncidentRumorProvider.TryClaimMainDialogueRumor("Pierre", out _));
        Assert.False(TownIncidentRumorProvider.TryClaimMainDialogueRumor("pierre", out _));
    }

    // ── 验收：RFC 案外人黑名单排除 ──

    [Fact]
    public void UT03_Blacklist_Excluded()
    {
        ResetWithActiveIncident();

        foreach (string outsider in new[] { "Wizard", "Krobus", "Leo", "Dwarf", "Linus" })
            Assert.False(TownIncidentRumorProvider.TryClaimMainDialogueRumor(outsider, out _), outsider);
    }

    // ── 验收：事件参与者排除 ──

    [Fact]
    public void UT04_Participants_Excluded()
    {
        ResetWithActiveIncident();

        foreach (string participant in new[] { "Gus", "Abigail", "Alex" })
            Assert.False(TownIncidentRumorProvider.TryClaimMainDialogueRumor(participant, out _), participant);
    }

    // ── 验收：仅产出有效传闻后才消耗配额 ──

    [Fact]
    public void UT05_QuotaConsumed_OnlyAfterValidRumor()
    {
        ResetWithActiveIncident();

        var broken = BuildIncident();
        broken.AssignedRoles.Remove("Champion");
        SetEngineData(new TownIncidentData { SchemaVersion = 1, ActiveIncident = broken });

        Assert.False(TownIncidentRumorProvider.TryClaimMainDialogueRumor("Lewis", out _));

        // BUG 路径未消耗配额：恢复完整事件后同一 NPC 仍可认领。
        SetEngineData(new TownIncidentData { SchemaVersion = 1, ActiveIncident = BuildIncident() });
        Assert.True(TownIncidentRumorProvider.TryClaimMainDialogueRumor("Lewis", out _));
    }

    // ── 验收：空名 / 无活跃事件行为 ──

    [Fact]
    public void UT06_EmptyName_And_NoIncident_Rejected()
    {
        ResetWithActiveIncident();
        Assert.False(TownIncidentRumorProvider.TryClaimMainDialogueRumor("", out _));
        Assert.False(TownIncidentRumorProvider.TryClaimMainDialogueRumor(null, out _));

        SetEngineData(new TownIncidentData { SchemaVersion = 1 });
        Assert.False(TownIncidentRumorProvider.TryClaimMainDialogueRumor("Pierre", out _));
    }

    // ── 验收：重置恢复配额 ──

    [Fact]
    public void UT07_Reset_RestoresQuota()
    {
        ResetWithActiveIncident();

        Assert.True(TownIncidentRumorProvider.TryClaimMainDialogueRumor("Pierre", out _));
        Assert.True(TownIncidentRumorProvider.TryClaimMainDialogueRumor("Marnie", out _));
        Assert.False(TownIncidentRumorProvider.TryClaimMainDialogueRumor("Penny", out _));

        TownIncidentRumorProvider.ResetDailyRumorQuota();
        Assert.True(TownIncidentRumorProvider.TryClaimMainDialogueRumor("Penny", out _));
    }

    // ── 验收（TIE-FIX-001 回归）：事件传闻在 PerceptionManager 快照为空时仍可认领 ──

    [Fact]
    public void UT09_IncidentRumor_Claimed_WhenGossipSnapshotsEmpty()
    {
        ResetWithActiveIncident();

        // PerceptionManager.Instance 在测试环境下未录入任何 Gossip，
        // GetGossipSnapshots() 返回空列表；BuildGossipBlock 不得因此提前返回。
        string result = PerceptionInjector.BuildGossipBlock("Pierre");

        Assert.False(string.IsNullOrEmpty(result), "BuildGossipBlock returned empty with empty Gossip snapshots.");
        Assert.Contains("Saloon Cook-Off", result);
        Assert.Contains("Pierre", result);
    }

    // ── 验收（TIE-FIX-001 回归）：无活跃事件且快照为空时，BuildGossipBlock 仍返回空 ──

    [Fact]
    public void UT10_NoIncident_EmptySnapshots_ReturnsEmpty()
    {
        LocalizedContentManager.CurrentLanguageCode = LocalizedContentManager.LanguageCode.en;
        TownIncidentRumorProvider.ResetDailyRumorQuota();
        SetEngineData(new TownIncidentData { SchemaVersion = 1 });

        string result = PerceptionInjector.BuildGossipBlock("Pierre");
        Assert.True(string.IsNullOrEmpty(result), "BuildGossipBlock should be empty with no active incident and no Gossip snapshots.");
    }

    // ── 验收（TIE-FIX-001 回归）：事件认领成功时不调用 RecordGossip ──

    [Fact]
    public void UT11_IncidentRumor_DoesNot_RecordGossip()
    {
        ResetWithActiveIncident();

        int snapshotsBefore = PerceptionManager.Instance.GetGossipSnapshots().Count;
        PerceptionInjector.BuildGossipBlock("Pierre");
        int snapshotsAfter = PerceptionManager.Instance.GetGossipSnapshots().Count;

        Assert.Equal(snapshotsBefore, snapshotsAfter);
    }

    // ── 验收（TIE-FIX-001 回归）：配额用尽后，ResetDailyRumorQuota 恢复 2 次新认领 ──

    [Fact]
    public void UT12_ResetDailyQuota_Restores_TwoNewClaims()
    {
        ResetWithActiveIncident();

        Assert.True(TownIncidentRumorProvider.TryClaimMainDialogueRumor("Pierre", out _));
        Assert.True(TownIncidentRumorProvider.TryClaimMainDialogueRumor("Marnie", out _));
        Assert.False(TownIncidentRumorProvider.TryClaimMainDialogueRumor("Penny", out _));

        TownIncidentRumorProvider.ResetDailyRumorQuota();

        Assert.True(TownIncidentRumorProvider.TryClaimMainDialogueRumor("Penny", out _));
        Assert.True(TownIncidentRumorProvider.TryClaimMainDialogueRumor("Sam", out _));
        Assert.False(TownIncidentRumorProvider.TryClaimMainDialogueRumor("Evelyn", out _));
    }

    // ── 验收：传闻行由事件壳与认领 NPC 确定 ──

    [Fact]
    public void UT08_RumorLine_BuiltFromShellAndNpc()
    {
        ResetWithActiveIncident();

        Assert.True(TownIncidentRumorProvider.TryClaimMainDialogueRumor("Pierre", out var rumor));
        Assert.Contains("Pierre", rumor);
        Assert.Contains("Gus", rumor);
        Assert.Contains("Abigail", rumor);
        Assert.Contains("Alex", rumor);
        Assert.Contains("Saloon Cook-Off", rumor);
    }
}
