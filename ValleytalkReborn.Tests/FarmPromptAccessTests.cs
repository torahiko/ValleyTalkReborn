// FarmPromptAccessTests.cs
// VT-FARM-ACCESS-01/03 — farm_state injection is gated to household members and
// carries no greenhouse interior facts.
// Verifies: ShouldIncludeFarmSummary is a pure AND of (includeFarmDetails,
// isHouseholdMember); non-household NPCs (mine Abigail, beach Willy,
// non-cohabiting Krobus) get no farm summary and never reach the scanner in
// either the role-based payload (non-streaming) or the legacy conversation
// payload (streaming); a spouse and a cohabiting roommate (Krobus) keep the
// outdoor-only summary (greenhouse interior withheld) while the routing flag
// allows it and lose it when routing is off; the without-greenhouse cache is
// selected per includeGreenhouseInterior and never falls back to the full
// summary when null; a household check reached while the world is not ready is
// a BUG (Error + InvalidOperationException); world-ready-with-no-player is a
// BOUNDARY gap (Warn + InvalidOperationException). No HTTP requests, no active
// LLM Provider required.
//
// 无头事实（沿用 FarmStateScannerFruitTreeTests / GiftPipelineHistoryTests 头注惯例）：
//   1) BuildRuntimeChatMessages / BuildRuntimeConversationPrompt 不设
//      GameConstantContext 时会懒加载触发 GetGameConstantContext —— 农场门禁的
//      真实装配入口；NpcConstantContext 显式置空以隔离。
//   2) GameSummaryBuilder 的 FromGameState 在 dayOfMonth < 28 时会走
//      IsNextDayFestival → isFestivalDay → DataLoader（无头 NRE）；
//      dayOfMonth = 28 直接短路（tomorrow > 28），今日节日分支自带 try/catch。
//   3) FarmStateScanner 是静态缓存：用公开 InvalidateCache 复位后，_cachedYear
//      保持 -1 即证明扫描器从未被调用；注入正例以种子哨兵摘要验证装配接线
//      （真实扫描在无头环境会因 Game1.getFarm 抛 KeyNotFoundException）。
//   4) SpouseQueryService.IsMarried 的原版降级路径需要 Game1.game1._locations
//      非空（getCharacterFromName → ForEachLocation 遍历）与 farmer.friendshipData
//      （NetStringDictionary，FakePlayer 的 NetFieldBase 补齐不覆盖）。

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text;
using Netcode;
using StardewModdingAPI;
using StardewValley;
using ValleytalkReborn;
using ValleytalkReborn.Dialogue.Coordination;
using ValleytalkReborn.Tests;
using Xunit;

[Collection("StaticGlobalStateCollection")]
public class FarmPromptAccessTests : IDisposable
{
    private const string FarmSummarySentinel = "FARM-STATE-SENTINEL";
    private const string OutdoorFarmSummarySentinel = "OUTDOOR-FARM-SENTINEL";
    private const string PlayerName = "FarmAccessFarmer";

    private readonly ModConfig _originalConfig;
    private readonly IModHelper _originalSHelper;
    private readonly IMonitor _originalSMonitor;
    private readonly object _originalGame1;
    private readonly int _originalDayOfMonth;
    private readonly FieldInfo _game1InstanceField;
    private readonly bool _needRestore;

    public FarmPromptAccessTests()
    {
        TestEnvironment.InstallHeadlessContext();
        var sHelperField = typeof(ModEntry).GetField("<SHelper>k__BackingField",
            BindingFlags.Static | BindingFlags.NonPublic);
        _originalSHelper = (IModHelper)sHelperField?.GetValue(null);
        _originalSMonitor = ModEntry.SMonitor;
        _originalConfig = ModEntry.Config;
        _needRestore = _originalSHelper == null;
        if (_needRestore)
        {
            ModEntry.SMonitor = new FakeMonitor();
            ModEntry.Config = new ModConfig();
            sHelperField?.SetValue(null, new FakeModHelper(string.Empty));
        }

        // Game1.getCharacterFromName（SpouseQueryService 原版降级路径）与
        // Game1.content（GameSummaryBuilder 的 catch 分支）都经由 game1 实例。
        // _locations 垫空列表保证遍历安全；instanceGameLocation 保持 null。
        _game1InstanceField = typeof(Game1).GetField("game1",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        _originalGame1 = _game1InstanceField?.GetValue(null);
        var game1 = (Game1)FormatterServices.GetUninitializedObject(typeof(Game1));
        typeof(Game1).GetField("_locations", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?.SetValue(game1, new List<GameLocation>());
        _game1InstanceField?.SetValue(null, game1);

        // 见头注 2)：短路 IsNextDayFestival 的无头内容加载路径。
        _originalDayOfMonth = Game1.dayOfMonth;
        Game1.dayOfMonth = 28;
    }

    public void Dispose()
    {
        Game1.dayOfMonth = _originalDayOfMonth;
        _game1InstanceField?.SetValue(null, _originalGame1);
        ModEntry.Config = _originalConfig;
        if (_needRestore)
        {
            var sHelperField = typeof(ModEntry).GetField("<SHelper>k__BackingField",
                BindingFlags.Static | BindingFlags.NonPublic);
            sHelperField?.SetValue(null, null);
            ModEntry.SMonitor = _originalSMonitor;
        }
    }

    // ── 1. 门禁真值表：四种布尔组合仅 true/true 允许注入 ──

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, false)]
    public void ShouldIncludeFarmSummary_OnlyTrueTrueAllowsInjection(
        bool includeFarmDetails, bool isHouseholdMember, bool expected)
    {
        Assert.Equal(expected, Prompts.ShouldIncludeFarmSummary(includeFarmDetails, isHouseholdMember));
    }

    // ── 2. 非同住人（矿洞阿比盖尔 / 海滩威利 / 未同住科罗布斯）：
    //        流式与非流式最终载荷均无 farm_state，且扫描器从未被调用 ──

    [Theory]
    [InlineData("Abigail")]
    [InlineData("Willy")]
    [InlineData("Krobus")]
    public void NonHouseholdNpc_FinalPayloadsOmitFarmSummary_AndScannerIsNeverCalled(string npcName)
    {
        var prompts = MakePrompts(npcName, includeFarmDetails: true);

        using (InstallPlayerWithoutFriendship(PlayerName))
        using (FarmStateScannerCacheProbe.ResetForInvocationCountProbe())
        {
            TestEnvironment.WithWorldReady(() =>
            {
                // 非流式（role-based）最终载荷
                var messages = prompts.BuildRuntimeChatMessages();
                Assert.All(messages, m => Assert.DoesNotContain(FarmSummarySentinel, m.Content, StringComparison.Ordinal));

                // 流式（legacy conversation）最终载荷
                string legacyPayload = LlmDialogueService.BuildRuntimeConversationPrompt(prompts);
                Assert.DoesNotContain(FarmSummarySentinel, legacyPayload, StringComparison.Ordinal);

                // 非同住路径不调用扫描器：缓存保持复位态（_cachedYear == -1）
                Assert.Equal(-1, FarmStateScannerCacheProbe.CachedYear);
            });
        }
    }

    // ── 3. 配偶在路由允许时保留室外摘要（不含温室；流式 + 非流式） ──

    [Fact]
    public void SpouseNpc_KeepsFarmSummaryWhenRoutingAllows()
    {
        var prompts = MakePrompts("Abigail", includeFarmDetails: true);

        using (InstallMarriedPlayer(PlayerName, "Abigail", roommate: false))
        using (FarmStateScannerCacheProbe.SeedSentinelSummaries(FarmSummarySentinel, OutdoorFarmSummarySentinel))
        {
            TestEnvironment.WithWorldReady(() =>
            {
                var messages = prompts.BuildRuntimeChatMessages();
                Assert.Contains(messages, m => m.Content.Contains(OutdoorFarmSummarySentinel, StringComparison.Ordinal));
                Assert.All(messages, m => Assert.DoesNotContain(FarmSummarySentinel, m.Content, StringComparison.Ordinal));

                string legacyPayload = LlmDialogueService.BuildRuntimeConversationPrompt(prompts);
                Assert.Contains(OutdoorFarmSummarySentinel, legacyPayload, StringComparison.Ordinal);
                Assert.DoesNotContain(FarmSummarySentinel, legacyPayload, StringComparison.Ordinal);
            });
        }
    }

    // ── 4. 同住室友（科罗布斯）在路由允许时保留室外摘要（不含温室） ──

    [Fact]
    public void CohabitingRoommateKrobus_KeepsFarmSummaryWhenRoutingAllows()
    {
        var prompts = MakePrompts("Krobus", includeFarmDetails: true);

        using (InstallMarriedPlayer(PlayerName, "Krobus", roommate: true))
        using (FarmStateScannerCacheProbe.SeedSentinelSummaries(FarmSummarySentinel, OutdoorFarmSummarySentinel))
        {
            TestEnvironment.WithWorldReady(() =>
            {
                var messages = prompts.BuildRuntimeChatMessages();
                Assert.Contains(messages, m => m.Content.Contains(OutdoorFarmSummarySentinel, StringComparison.Ordinal));
                Assert.All(messages, m => Assert.DoesNotContain(FarmSummarySentinel, m.Content, StringComparison.Ordinal));

                string legacyPayload = LlmDialogueService.BuildRuntimeConversationPrompt(prompts);
                Assert.Contains(OutdoorFarmSummarySentinel, legacyPayload, StringComparison.Ordinal);
                Assert.DoesNotContain(FarmSummarySentinel, legacyPayload, StringComparison.Ordinal);
            });
        }
    }

    // ── 5. 路由关闭：同住人也不注入，关系查询与扫描器均被跳过 ──

    [Fact]
    public void RoutingOff_HouseholdMemberOmitted_AndScannerNeverCalled()
    {
        var prompts = MakePrompts("Abigail", includeFarmDetails: false);

        using (InstallMarriedPlayer(PlayerName, "Abigail", roommate: false))
        using (FarmStateScannerCacheProbe.ResetForInvocationCountProbe())
        {
            TestEnvironment.WithWorldReady(() =>
            {
                var messages = prompts.BuildRuntimeChatMessages();
                Assert.All(messages, m => Assert.DoesNotContain(FarmSummarySentinel, m.Content, StringComparison.Ordinal));

                string legacyPayload = LlmDialogueService.BuildRuntimeConversationPrompt(prompts);
                Assert.DoesNotContain(FarmSummarySentinel, legacyPayload, StringComparison.Ordinal);

                Assert.Equal(-1, FarmStateScannerCacheProbe.CachedYear);
            });
        }
    }

    // ── 6. VT-FARM-ACCESS-03: 同住人最终载荷含室外哨兵、不含温室哨兵，中英文均验证 ──

    [Theory]
    [InlineData("zh")]
    [InlineData("en")]
    public void HouseholdNpc_PayloadContainsOutdoorSentinel_NotGreenhouseSentinel(string languageOverride)
    {
        var prompts = MakePrompts("Abigail", includeFarmDetails: true);
        string previousLanguage = ModEntry.Config.LanguageOverride;
        ModEntry.Config.LanguageOverride = languageOverride;

        using (new RestoreScope(() => ModEntry.Config.LanguageOverride = previousLanguage))
        using (InstallMarriedPlayer(PlayerName, "Abigail", roommate: false))
        using (FarmStateScannerCacheProbe.SeedSentinelSummaries(FarmSummarySentinel, OutdoorFarmSummarySentinel))
        {
            TestEnvironment.WithWorldReady(() =>
            {
                string expectedSentinel = languageOverride == "zh"
                    ? OutdoorFarmSummarySentinel + "-ZH"
                    : OutdoorFarmSummarySentinel + "-EN";

                // 非流式（role-based）最终载荷
                var messages = prompts.BuildRuntimeChatMessages();
                Assert.Contains(messages, m => m.Content.Contains(expectedSentinel, StringComparison.Ordinal));
                Assert.All(messages, m => Assert.DoesNotContain(FarmSummarySentinel, m.Content, StringComparison.Ordinal));

                // 流式（legacy conversation）最终载荷
                string legacyPayload = LlmDialogueService.BuildRuntimeConversationPrompt(prompts);
                Assert.Contains(expectedSentinel, legacyPayload, StringComparison.Ordinal);
                Assert.DoesNotContain(FarmSummarySentinel, legacyPayload, StringComparison.Ordinal);
            });
        }
    }

    // ── 7. VT-FARM-ACCESS-03: 无温室缓存为 null 时不回退完整摘要、不注入空标签，Debug 省略 ──

    [Fact]
    public void HouseholdNpc_NullOutdoorSummary_OmitsFarmState_NoFullSummaryFallback()
    {
        var prompts = MakePrompts("Abigail", includeFarmDetails: true);
        var capture = new StringBuilder();
        var originalMonitor = ModEntry.SMonitor;
        ModEntry.SMonitor = new CaptureMonitor(capture);
        try
        {
            using (InstallMarriedPlayer(PlayerName, "Abigail", roommate: false))
            using (FarmStateScannerCacheProbe.SeedNullOutdoorSummaries(FarmSummarySentinel))
            {
                TestEnvironment.WithWorldReady(() =>
                {
                    var messages = prompts.BuildRuntimeChatMessages();
                    Assert.All(messages, m =>
                    {
                        Assert.DoesNotContain(FarmSummarySentinel, m.Content, StringComparison.Ordinal);
                        Assert.DoesNotContain("<farm_state>", m.Content, StringComparison.Ordinal);
                    });

                    string legacyPayload = LlmDialogueService.BuildRuntimeConversationPrompt(prompts);
                    Assert.DoesNotContain(FarmSummarySentinel, legacyPayload, StringComparison.Ordinal);
                    Assert.DoesNotContain("<farm_state>", legacyPayload, StringComparison.Ordinal);
                });
            }

            Assert.Contains("[Debug]", capture.ToString(), StringComparison.Ordinal);
            Assert.Contains("no injectable outdoor farm background", capture.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            ModEntry.SMonitor = originalMonitor;
        }
    }

    // ── 8. VT-FARM-ACCESS-03: 同一新鲜缓存交替请求 true/false/true 不串权限；
    //         旧单参数入口仍返回完整摘要；探针退出恢复全部缓存字段 ──

    [Fact]
    public void CacheSelection_AlternatingViews_NoCrossContamination_AndProbeRestoresFields()
    {
        var fieldsBefore = new Dictionary<string, object>();
        foreach (var f in FarmStateScannerCacheProbe.CacheFieldNames)
            fieldsBefore[f] = FarmStateScannerCacheProbe.ReadField(f);

        using (FarmStateScannerCacheProbe.SeedSentinelSummaries(FarmSummarySentinel, OutdoorFarmSummarySentinel))
        {
            TestEnvironment.WithWorldReady(() =>
            {
                Assert.Equal(FarmSummarySentinel + "-ZH",
                    FarmStateScanner.BuildFarmSummary(isZh: true, includeGreenhouseInterior: true));
                Assert.Equal(OutdoorFarmSummarySentinel + "-ZH",
                    FarmStateScanner.BuildFarmSummary(isZh: true, includeGreenhouseInterior: false));
                Assert.Equal(FarmSummarySentinel + "-ZH",
                    FarmStateScanner.BuildFarmSummary(isZh: true, includeGreenhouseInterior: true));

                Assert.Equal(FarmSummarySentinel + "-EN",
                    FarmStateScanner.BuildFarmSummary(isZh: false, includeGreenhouseInterior: true));
                Assert.Equal(OutdoorFarmSummarySentinel + "-EN",
                    FarmStateScanner.BuildFarmSummary(isZh: false, includeGreenhouseInterior: false));
                Assert.Equal(FarmSummarySentinel + "-EN",
                    FarmStateScanner.BuildFarmSummary(isZh: false, includeGreenhouseInterior: true));

                // 旧单参数入口仍返回完整摘要
                Assert.Equal(FarmSummarySentinel + "-ZH", FarmStateScanner.BuildFarmSummary(isZh: true));
                Assert.Equal(FarmSummarySentinel + "-EN", FarmStateScanner.BuildFarmSummary(isZh: false));
            });
        }

        foreach (var f in FarmStateScannerCacheProbe.CacheFieldNames)
            Assert.Equal(fieldsBefore[f], FarmStateScannerCacheProbe.ReadField(f));
    }

    // ── 9. 世界未就绪却到达关系查询：BUG → Error → InvalidOperationException ──

    [Fact]
    public void HouseholdCheckWhileWorldNotReady_LogsErrorAndThrows()
    {
        var prompts = MakePrompts("Abigail", includeFarmDetails: true);
        var capture = new StringBuilder();
        var originalMonitor = ModEntry.SMonitor;
        ModEntry.SMonitor = new CaptureMonitor(capture);
        try
        {
            using (InstallMarriedPlayer(PlayerName, "Abigail", roommate: false))
            {
                TestEnvironment.WithoutWorldReady(() =>
                {
                    var ex = Assert.Throws<InvalidOperationException>(() => prompts.BuildRuntimeChatMessages());
                    Assert.Contains("IsWorldReady", ex.Message);
                });
            }
            Assert.Contains("[Error]", capture.ToString(), StringComparison.Ordinal);
            Assert.Contains("Abigail", capture.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            ModEntry.SMonitor = originalMonitor;
        }
    }

    // ── 10. 世界就绪但无当前玩家：BOUNDARY → Warn → 契约缺口上报并停止 ──

    [Fact]
    public void HouseholdCheckWithNoCurrentPlayer_LogsWarnAndThrows()
    {
        var prompts = MakePrompts("Abigail", includeFarmDetails: true);
        var capture = new StringBuilder();
        var originalMonitor = ModEntry.SMonitor;
        ModEntry.SMonitor = new CaptureMonitor(capture);
        try
        {
            using (InstallNoPlayer())
            {
                TestEnvironment.WithWorldReady(() =>
                {
                    // 直接触发 GetGameConstantContext：绕开装配前置的玩家身份解析，
                    // 精确隔离同住判定本身的契约缺口行为。
                    var method = typeof(Prompts).GetMethod("GetGameConstantContext",
                        BindingFlags.Instance | BindingFlags.NonPublic);
                    Assert.NotNull(method);
                    var ex = Assert.Throws<TargetInvocationException>(
                        () => method.Invoke(prompts, Array.Empty<object>()));
                    var inner = Assert.IsType<InvalidOperationException>(ex.InnerException);
                    Assert.Contains("cannot reliably determine household identity", inner.Message);
                });
            }
            Assert.Contains("[Warn]", capture.ToString(), StringComparison.Ordinal);
            Assert.Contains("Abigail", capture.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            ModEntry.SMonitor = originalMonitor;
        }
    }

    // ── 测试辅助 ──

    private static Prompts MakePrompts(string npcName, bool includeFarmDetails)
    {
        var prompts = (Prompts)FormatterServices.GetUninitializedObject(typeof(Prompts));
        prompts.SystemPrompt = "SYS-CONTEXT";
        // GameConstantContext 留空：DynamicContext 懒加载触发 GetGameConstantContext 门禁。
        prompts.NpcConstantContext = string.Empty;
        prompts.CurrentFlags = new ContextFlags
        {
            IncludeSafetyRules = true,
            IncludeShortTermContext = true,
            IncludeMemories = true,
            IncludeEnvironment = true,
            IncludeFarmDetails = includeFarmDetails,
            IsSimpleGreeting = false,
        };
        SetPrivateField(prompts, "_corePlan", new InjectionPlan
        {
            Tier1Snapshot = new Tier1SnapshotContext(new Dictionary<string, string>()),
            ActiveImpulses = new Dictionary<string, string>(),
        });

        var ctx = new DialogueContext();
        ctx.ChatHistory = new List<ConversationElement>();
        ctx.RoutingFlags = prompts.CurrentFlags;
        SetPrivateField(prompts, "<Context>k__BackingField", ctx);

        var character = (ValleytalkReborn.Character)FormatterServices.GetUninitializedObject(typeof(ValleytalkReborn.Character));
        SetPrivateField(character, "<Name>k__BackingField", npcName);
        SetPrivateField(character, "_bioData", new BioData { Biography = "测试用人物设定", Missing = true });
        SetPrivateField(prompts, "<Character>k__BackingField", character);
        return prompts;
    }

    private static void SetPrivateField(object target, string fieldName, object value)
    {
        var field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field.SetValue(target, value);
    }

    /// <summary>安装当前玩家并注入与 npcName 的婚姻（可选室友标记）。</summary>
    private static IDisposable InstallMarriedPlayer(string playerName, string npcName, bool roommate)
    {
        IDisposable scope = FakePlayer.Install(playerName);
        try
        {
            InstallFriendship(Game1.player, npcName, roommate);
            return scope;
        }
        catch
        {
            scope.Dispose();
            throw;
        }
    }

    /// <summary>安装当前玩家且无任何婚姻条目（非同住人场景）。</summary>
    private static IDisposable InstallPlayerWithoutFriendship(string playerName)
    {
        IDisposable scope = FakePlayer.Install(playerName);
        try
        {
            InstallFriendshipData(Game1.player);
            return scope;
        }
        catch
        {
            scope.Dispose();
            throw;
        }
    }

    /// <summary>卸下当前玩家（Game1.player == null 的 BOUNDARY 场景）。</summary>
    private static IDisposable InstallNoPlayer()
    {
        var playerField = typeof(Game1).GetField("_player",
            BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        object previous = playerField?.GetValue(null);
        playerField?.SetValue(null, null);
        return new RestoreScope(() => playerField?.SetValue(null, previous));
    }

    private static void InstallFriendship(Farmer farmer, string npcName, bool roommate)
    {
        InstallFriendshipData(farmer);
        var friendship = new Friendship { Status = FriendshipStatus.Married };
        if (roommate)
        {
            typeof(Friendship).GetField("roommateMarriage",
                    BindingFlags.Instance | BindingFlags.NonPublic)
                ?.SetValue(friendship, new NetBool(true));
        }
        farmer.friendshipData[npcName] = friendship;
    }

    /// <summary>FriendshipData 不承 NetFieldBase（FakePlayer 通用补齐不覆盖），显式补齐。</summary>
    private static void InstallFriendshipData(Farmer farmer)
    {
        var field = typeof(Farmer).GetField("friendshipData",
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        Assert.NotNull(field);
        if (field.GetValue(farmer) != null) return;
        field.SetValue(farmer, Activator.CreateInstance(field.FieldType));
    }

    /// <summary>
    /// FarmStateScanner 静态缓存探针：复位后 _cachedYear == -1 即"扫描器未被调用"；
    /// 注入正例以种子哨兵摘要绕开无头环境下 Game1.getFarm 的 KeyNotFoundException。
    /// VT-FARM-ACCESS-03：完整与无温室四份文本缓存均纳入快照/种入/恢复范围，
    /// 完整与无温室缓存使用不同哨兵。
    /// </summary>
    private static class FarmStateScannerCacheProbe
    {
        /// <summary>扫描器全部缓存字段（四份文本缓存 + 三个日期有效性标记）。</summary>
        internal static readonly string[] CacheFieldNames =
        {
            "_cachedSummaryZh", "_cachedSummaryEn",
            "_cachedSummaryWithoutGreenhouseZh", "_cachedSummaryWithoutGreenhouseEn",
            "_cachedYear", "_cachedSeason", "_cachedDay",
        };

        internal static int CachedYear =>
            (int)GetField("_cachedYear").GetValue(null);

        internal static object ReadField(string name) =>
            GetField(name).GetValue(null);

        /// <summary>复位缓存至陈旧态；退出时复位再复位态（保证后续断言不受污染）。</summary>
        internal static IDisposable ResetForInvocationCountProbe()
        {
            FarmStateScanner.InvalidateCache();
            return new RestoreScope(() => FarmStateScanner.InvalidateCache());
        }

        /// <summary>
        /// 种入完整与无温室哨兵摘要（不同哨兵）并将缓存标记为新鲜
        /// （year/season/day 与当前游戏状态一致）。
        /// </summary>
        internal static IDisposable SeedSentinelSummaries(string fullSentinel, string outdoorSentinel)
        {
            var previous = SnapshotFields();

            GetField("_cachedSummaryZh").SetValue(null, fullSentinel + "-ZH");
            GetField("_cachedSummaryEn").SetValue(null, fullSentinel + "-EN");
            GetField("_cachedSummaryWithoutGreenhouseZh").SetValue(null, outdoorSentinel + "-ZH");
            GetField("_cachedSummaryWithoutGreenhouseEn").SetValue(null, outdoorSentinel + "-EN");
            MarkCacheFresh();
            return new RestoreScope(RestoreFields(previous));
        }

        /// <summary>
        /// 种入完整哨兵摘要，同时把无温室缓存置为 null（无室外事实场景），
        /// 并将缓存标记为新鲜；验证注入路径不得回退完整摘要。
        /// </summary>
        internal static IDisposable SeedNullOutdoorSummaries(string fullSentinel)
        {
            var previous = SnapshotFields();

            GetField("_cachedSummaryZh").SetValue(null, fullSentinel + "-ZH");
            GetField("_cachedSummaryEn").SetValue(null, fullSentinel + "-EN");
            GetField("_cachedSummaryWithoutGreenhouseZh").SetValue(null, null);
            GetField("_cachedSummaryWithoutGreenhouseEn").SetValue(null, null);
            MarkCacheFresh();
            return new RestoreScope(RestoreFields(previous));
        }

        private static Dictionary<string, object> SnapshotFields()
        {
            var snapshot = new Dictionary<string, object>();
            foreach (var f in CacheFieldNames)
                snapshot[f] = GetField(f).GetValue(null);
            return snapshot;
        }

        private static Action RestoreFields(Dictionary<string, object> previous) => () =>
        {
            foreach (var kv in previous)
                GetField(kv.Key).SetValue(null, kv.Value);
        };

        private static void MarkCacheFresh()
        {
            GetField("_cachedYear").SetValue(null, Game1.year);
            GetField("_cachedSeason").SetValue(null, Game1.currentSeason);
            GetField("_cachedDay").SetValue(null, Game1.dayOfMonth);
        }

        private static FieldInfo GetField(string name) =>
            typeof(FarmStateScanner).GetField(name, BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new Xunit.Sdk.XunitException($"FarmStateScanner.{name} not found.");
    }

    private sealed class CaptureMonitor : IMonitor
    {
        private readonly StringBuilder _captured;
        public CaptureMonitor(StringBuilder captured) => _captured = captured;
        public bool IsVerbose => false;
        public void Log(string message, LogLevel level) => _captured.AppendLine($"[{level}] {message}");
        public void LogOnce(string message, LogLevel level) => _captured.AppendLine($"[{level}] {message}");
        public void VerboseLog(string message) { }
        public void VerboseLog(ref StardewModdingAPI.Framework.Logging.VerboseLogStringHandler handler) { }
    }

    private sealed class RestoreScope : IDisposable
    {
        private readonly Action _restore;
        public RestoreScope(Action restore) => _restore = restore;
        public void Dispose() => _restore();
    }
}
