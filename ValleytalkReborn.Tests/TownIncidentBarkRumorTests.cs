// TownIncidentBarkRumorTests.cs
// TIE-007: ambient-bark / main-dialogue incident rumor mutual exclusion.
// Headless tests: the relay touches only engine memory state (reflection-injected),
// Context.IsMultiplayer (false outside a game), Game1.dayOfMonth (plain static) and
// LocalizedContentManager.CurrentLanguageCode (plain static, restored in a finally).
//
// Scope note: BarkFocusRouter's candidate pools require a live NPC/GameLocation, so
// the router-side wiring is verified through the relay preview/claim contract and the
// internal bark-context formatter; the router's existing pools are not modified by
// TIE-007 beyond adding the incident candidate when the preview succeeds.

using System;
using System.Collections.Generic;
using System.Reflection;
using StardewModdingAPI;
using StardewValley;
using ValleytalkReborn;
using Xunit;

[Collection("StaticGlobalStateCollection")]
public class TownIncidentBarkRumorTests
{
    public TownIncidentBarkRumorTests()
    {
        TestEnvironment.InstallHeadlessContext();
    }

    // ── 反射/上下文助手 ──

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
        TownIncidentRumorRelay.ResetDailyClaims();
        SetEngineData(new TownIncidentData { SchemaVersion = 1, ActiveIncident = BuildIncident() });
    }

    private static void ResetWithoutIncident()
    {
        LocalizedContentManager.CurrentLanguageCode = LocalizedContentManager.LanguageCode.en;
        TownIncidentRumorRelay.ResetDailyClaims();
        SetEngineData(new TownIncidentData { SchemaVersion = 1 });
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

    private static void InvokePrivateStatic(string methodName, params object[] args)
    {
        var method = typeof(TownIncidentEngine).GetMethod(methodName,
            BindingFlags.NonPublic | BindingFlags.Static);
        Assert.NotNull(method);
        method.Invoke(null, args);
    }

    // ── 验收：Bark 传闻来自 relay，而非 GetGossipSnapshots ──

    [Fact]
    public void UT01_BarkRumor_ComesFromRelay_WithEmptyGossipSnapshots()
    {
        ResetWithActiveIncident();

        int snapshotsBefore = PerceptionManager.Instance.GetGossipSnapshots().Count;

        Assert.True(TownIncidentRumorRelay.TryGetIncidentRumorPreview("Pierre", out var preview));
        Assert.Contains("Saloon Cook-Off", preview);
        Assert.Contains("Pierre", preview);

        Assert.True(TownIncidentRumorRelay.TryClaimIncidentRumor(
            "Pierre", TownIncidentRumorConsumer.AmbientBark, out var claimed));
        Assert.Equal(preview, claimed);

        // 认领不写全局 Gossip 队列（relay 与 PerceptionManager 解耦）。
        Assert.Equal(snapshotsBefore, PerceptionManager.Instance.GetGossipSnapshots().Count);
    }

    // ── 验收：同一 NPC 同日重复认领被拒（大小写不敏感） ──

    [Fact]
    public void UT02_SameNpc_SameDay_DuplicateClaim_Rejected()
    {
        ResetWithActiveIncident();

        Assert.True(TownIncidentRumorRelay.TryClaimIncidentRumor(
            "Pierre", TownIncidentRumorConsumer.AmbientBark, out _));
        Assert.True(TownIncidentRumorRelay.HasClaimedToday("Pierre"));
        Assert.True(TownIncidentRumorRelay.HasClaimedToday("pierre"));

        Assert.False(TownIncidentRumorRelay.TryClaimIncidentRumor(
            "Pierre", TownIncidentRumorConsumer.AmbientBark, out var denied));
        Assert.Null(denied);
        Assert.False(TownIncidentRumorRelay.TryClaimIncidentRumor(
            "pierre", TownIncidentRumorConsumer.MainDialogue, out _));
    }

    // ── 验收：MainDialogue 认领抑制同一 NPC 的 Bark（方向 A） ──

    [Fact]
    public void UT03_MainDialogueClaim_SuppressesBark()
    {
        ResetWithActiveIncident();

        Assert.True(TownIncidentRumorRelay.TryClaimIncidentRumor(
            "Pierre", TownIncidentRumorConsumer.MainDialogue, out _));

        Assert.False(TownIncidentRumorRelay.TryGetIncidentRumorPreview("Pierre", out var preview));
        Assert.Null(preview);
        Assert.False(TownIncidentRumorRelay.TryClaimIncidentRumor(
            "Pierre", TownIncidentRumorConsumer.AmbientBark, out _));
    }

    // ── 验收：Bark 认领抑制同一 NPC 的 MainDialogue（方向 B），且不消耗 MainDialogue 配额 ──

    [Fact]
    public void UT04_BarkClaim_SuppressesMainDialogue_AndLeavesMainQuota()
    {
        ResetWithActiveIncident();

        Assert.True(TownIncidentRumorRelay.TryClaimIncidentRumor(
            "Marnie", TownIncidentRumorConsumer.AmbientBark, out _));

        Assert.False(TownIncidentRumorRelay.TryClaimIncidentRumor(
            "Marnie", TownIncidentRumorConsumer.MainDialogue, out _));

        // Bark 认领不占用 MainDialogue 的 2/日配额。
        Assert.True(TownIncidentRumorRelay.TryClaimIncidentRumor(
            "Pierre", TownIncidentRumorConsumer.MainDialogue, out _));
        Assert.True(TownIncidentRumorRelay.TryClaimIncidentRumor(
            "Penny", TownIncidentRumorConsumer.MainDialogue, out _));
        Assert.False(TownIncidentRumorRelay.TryClaimIncidentRumor(
            "Sam", TownIncidentRumorConsumer.MainDialogue, out _));
    }

    // ── 验收：候选构建（预览）零消耗，后续 MainDialogue 仍可用满 2 次 ──

    [Fact]
    public void UT05_PreviewBuild_ConsumesNothing()
    {
        ResetWithActiveIncident();

        Assert.True(TownIncidentRumorRelay.TryGetIncidentRumorPreview("Pierre", out _));
        Assert.True(TownIncidentRumorRelay.TryGetIncidentRumorPreview("Pierre", out _));
        Assert.False(TownIncidentRumorRelay.HasClaimedToday("Pierre"));

        Assert.True(TownIncidentRumorRelay.TryClaimIncidentRumor(
            "Pierre", TownIncidentRumorConsumer.MainDialogue, out _));
        Assert.True(TownIncidentRumorRelay.TryClaimIncidentRumor(
            "Marnie", TownIncidentRumorConsumer.MainDialogue, out _));
        Assert.False(TownIncidentRumorRelay.TryClaimIncidentRumor(
            "Penny", TownIncidentRumorConsumer.MainDialogue, out _));
    }

    // ── 验收：MainDialogue 上限仍为 2/日（经 Provider 兼容入口） ──

    [Fact]
    public void UT06_MainDialogue_Cap_StillTwoPerDay_ViaProvider()
    {
        ResetWithActiveIncident();

        Assert.True(TownIncidentRumorProvider.TryClaimMainDialogueRumor("Pierre", out _));
        Assert.True(TownIncidentRumorProvider.TryClaimMainDialogueRumor("Marnie", out _));
        Assert.False(TownIncidentRumorProvider.TryClaimMainDialogueRumor("Penny", out var denied));
        Assert.Null(denied);
    }

    // ── 验收：无活跃事件 ⇒ 无候选、无认领（现有候选池不受影响） ──

    [Fact]
    public void UT07_NoIncident_NoPreview_NoClaim()
    {
        ResetWithoutIncident();

        Assert.False(TownIncidentRumorRelay.TryGetIncidentRumorPreview("Pierre", out var preview));
        Assert.Null(preview);
        Assert.False(TownIncidentRumorRelay.TryClaimIncidentRumor(
            "Pierre", TownIncidentRumorConsumer.AmbientBark, out _));
        Assert.False(TownIncidentRumorRelay.HasClaimedToday("Pierre"));

        // 现有候选池与 FreeDrift 回退未被 TIE-007 改动：relay 返回 false 时
        // BarkFocusRouter 只是不追加事件候选，其余池的构建路径保持不变。
        Assert.Null(BarkFocusRouter.FormatIncidentRumorForBark(null, isZh: false));
    }

    // ── 验收：案外人黑名单与参与者在预览/认领两侧均被排除 ──

    [Fact]
    public void UT08_Blacklist_And_Participants_Excluded()
    {
        ResetWithActiveIncident();

        foreach (string excluded in new[] { "Wizard", "Krobus", "Leo", "Dwarf", "Linus", "Gus", "Abigail", "Alex" })
        {
            Assert.False(TownIncidentRumorRelay.TryGetIncidentRumorPreview(excluded, out _), excluded);
            Assert.False(TownIncidentRumorRelay.TryClaimIncidentRumor(
                excluded, TownIncidentRumorConsumer.AmbientBark, out _), excluded);
            Assert.False(TownIncidentRumorRelay.HasClaimedToday(excluded), excluded);
        }
    }

    // ── 验收：重置清空认领集合与配额 ──

    [Fact]
    public void UT09_ResetDailyClaims_RestoresClaimsAndQuota()
    {
        ResetWithActiveIncident();

        Assert.True(TownIncidentRumorRelay.TryClaimIncidentRumor(
            "Pierre", TownIncidentRumorConsumer.MainDialogue, out _));
        Assert.True(TownIncidentRumorRelay.TryClaimIncidentRumor(
            "Sam", TownIncidentRumorConsumer.MainDialogue, out _));
        Assert.True(TownIncidentRumorRelay.TryClaimIncidentRumor(
            "Marnie", TownIncidentRumorConsumer.AmbientBark, out _));
        Assert.False(TownIncidentRumorRelay.TryClaimIncidentRumor(
            "Penny", TownIncidentRumorConsumer.MainDialogue, out _));

        TownIncidentRumorRelay.ResetDailyClaims();

        Assert.False(TownIncidentRumorRelay.HasClaimedToday("Pierre"));
        Assert.False(TownIncidentRumorRelay.HasClaimedToday("Marnie"));
        Assert.True(TownIncidentRumorRelay.TryClaimIncidentRumor(
            "Penny", TownIncidentRumorConsumer.MainDialogue, out _));
        Assert.True(TownIncidentRumorRelay.TryClaimIncidentRumor(
            "Sam", TownIncidentRumorConsumer.MainDialogue, out _));
    }

    // ── 验收：DayStarted 处理链重置 relay（沿用现有订阅，无新增订阅） ──

    [Fact]
    public void UT10_EngineDayStarted_ResetsRelay()
    {
        ResetWithActiveIncident();

        // OnDayStarted 先重置 relay，再做多人早退；用无活跃事件 + 非触发日
        // 的数据避免 Game1.Date 依赖。
        TownIncidentRumorRelay.TryClaimIncidentRumor(
            "Pierre", TownIncidentRumorConsumer.AmbientBark, out _);
        Assert.True(TownIncidentRumorRelay.HasClaimedToday("Pierre"));

        SetEngineData(new TownIncidentData { SchemaVersion = 1 });
        int originalDayOfMonth = Game1.dayOfMonth;
        try
        {
            Game1.dayOfMonth = 1; // != ContestTriggerDayOfMonth(4)
            InvokePrivateStatic("OnDayStarted", new object[] { null, null });
        }
        finally
        {
            Game1.dayOfMonth = originalDayOfMonth;
        }

        Assert.False(TownIncidentRumorRelay.HasClaimedToday("Pierre"));
    }

    // ── 验收：SaveLoaded / ReturnedToTitle 共用的 ResetMemoryState 重置 relay ──

    [Fact]
    public void UT11_EngineResetMemoryState_ResetsRelay()
    {
        ResetWithActiveIncident();

        TownIncidentRumorRelay.TryClaimIncidentRumor(
            "Marnie", TownIncidentRumorConsumer.MainDialogue, out _);
        Assert.True(TownIncidentRumorRelay.HasClaimedToday("Marnie"));

        InvokePrivateStatic("ResetMemoryState");

        Assert.False(TownIncidentRumorRelay.HasClaimedToday("Marnie"));
    }

    // ── 验收：每个 GameLoop 事件只有一个处理器方法（无重复订阅导致的二次重置） ──

    [Fact]
    public void UT12_Engine_SingleLifecycleHandlerPerEvent()
    {
        var methods = typeof(TownIncidentEngine).GetMethods(
            BindingFlags.NonPublic | BindingFlags.Static);

        foreach (string handler in new[] { "OnSaveLoaded", "OnDayStarted", "OnSaving", "OnReturnedToTitle" })
        {
            int count = 0;
            foreach (var m in methods)
                if (string.Equals(m.Name, handler, StringComparison.Ordinal)) count++;

            Assert.Equal(1, count);
        }

        // 重置幂等：即便出现重复订阅，二次重置也不会产生额外状态变化。
        ResetWithActiveIncident();
        TownIncidentRumorRelay.TryClaimIncidentRumor("Pierre", TownIncidentRumorConsumer.AmbientBark, out _);
        TownIncidentRumorRelay.ResetDailyClaims();
        TownIncidentRumorRelay.ResetDailyClaims();
        Assert.False(TownIncidentRumorRelay.HasClaimedToday("Pierre"));
        Assert.True(TownIncidentRumorRelay.TryClaimIncidentRumor("Pierre", TownIncidentRumorConsumer.AmbientBark, out _));
    }

    // ── 验收：Bark 语境改写剔除主对话面向农夫的措辞 ──

    [Fact]
    public void UT13_FormatIncidentRumorForBark_StripsFarmerFacingWording()
    {
        ResetWithActiveIncident();
        Assert.True(TownIncidentRumorRelay.TryGetIncidentRumorPreview("Pierre", out var preview));

        string barkLine = BarkFocusRouter.FormatIncidentRumorForBark(preview, isZh: false);

        Assert.Contains("Saloon Cook-Off", barkLine);
        Assert.DoesNotContain("has heard the talk of the town", barkLine);
        Assert.DoesNotContain("Pierre", barkLine);
    }

    [Fact]
    public void UT13b_FormatIncidentRumorForBark_Zh()
    {
        var original = LocalizedContentManager.CurrentLanguageCode;
        try
        {
            LocalizedContentManager.CurrentLanguageCode = LocalizedContentManager.LanguageCode.zh;
            TownIncidentRumorRelay.ResetDailyClaims();
            SetEngineData(new TownIncidentData { SchemaVersion = 1, ActiveIncident = BuildIncident() });

            Assert.True(TownIncidentRumorRelay.TryGetIncidentRumorPreview("Pierre", out var preview));
            string barkLine = BarkFocusRouter.FormatIncidentRumorForBark(preview, isZh: true);

            Assert.Contains("Gus", barkLine);
            Assert.DoesNotContain("听说了镇上最近的热议", barkLine);
        }
        finally
        {
            LocalizedContentManager.CurrentLanguageCode = original;
        }
    }

    // ── 验收：多人在线 ⇒ 预览与认领均返回 false 且零变异 ──

    [Fact]
    public void UT14_Multiplayer_PreviewAndClaim_NoMutation()
    {
        ResetWithActiveIncident();

        var scope = new MultiplayerScope();
        try
        {
            SetMultiplayer(true);

            Assert.False(TownIncidentRumorRelay.TryGetIncidentRumorPreview("Pierre", out var preview));
            Assert.Null(preview);
            Assert.False(TownIncidentRumorRelay.TryClaimIncidentRumor(
                "Pierre", TownIncidentRumorConsumer.AmbientBark, out _));
            Assert.False(TownIncidentRumorRelay.HasClaimedToday("Pierre"));
        }
        finally
        {
            scope.Dispose();
        }
    }

    // ── 验收（回归）：PerceptionInjector 调用点经 Provider→relay 行为不变 ──

    [Fact]
    public void UT15_BuildGossipBlock_Regression_ViaRelay()
    {
        ResetWithActiveIncident();

        string result = PerceptionInjector.BuildGossipBlock("Pierre");

        Assert.False(string.IsNullOrEmpty(result));
        Assert.Contains("Saloon Cook-Off", result);
        Assert.Contains("Pierre", result);
        Assert.True(TownIncidentRumorRelay.HasClaimedToday("Pierre"));
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
}
