// TownIncidentA2ARumorTests.cs
// TIE-008: A2A 侧接入 TIE-007 共享事件传闻认领表。
//
// 环境事实：relay 只触碰 TownIncidentEngine 的内存态（反射注入）、
// Context.IsMultiplayer（无游戏环境下为 false）与
// LocalizedContentManager.CurrentLanguageCode（普通静态量，Dispose 中还原）。
//
// 边界说明：A2APromptBuilder.Build 无法在无头环境端到端执行
// （DialogueModels.A2ASession.ResolveParticipants 经 Game1.getCharacterFromName
// 解析 NPC，无地图时恒定返回空列表），因此提示握手层通过 builder 的私有
// 决选方法与「源码 + 输出 schema」级契约断言验证，运行时行为在 relay 层覆盖。

#nullable disable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using StardewValley;
using ValleytalkReborn;
using Xunit;

[Collection("StaticGlobalStateCollection")]
public class TownIncidentA2ARumorTests : IDisposable
{
    private readonly LocalizedContentManager.LanguageCode _originalLanguageCode;

    // 无头环境下 Game1.characterData 为空，NPC.displayName 的 getter
    // （NPC.translateName → TryGetData）会抛 NRE。注入空字典使 displayName
    // 回落到内部名，与真实运行环境保持同一取值路径。
    private static readonly FieldInfo CharacterDataField = typeof(Game1).GetField(
        "characterData", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);

    private static readonly FieldInfo Game1Field = typeof(Game1).GetField(
        "game1", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);

    private readonly object _originalCharacterData;

    // getCharacterFromName → Utility.ForEachLocation → Game1.game1._locations：
    // 无实例时 NpcNameLocalizer.GetZhName 的字典回退不可用（NRE）。注入空地图列表，
    // 让查询安全返回 null，走字典回退分支。
    private readonly object _originalGame1;

    public TownIncidentA2ARumorTests()
    {
        TestEnvironment.InstallHeadlessContext();
        _originalLanguageCode = LocalizedContentManager.CurrentLanguageCode;
        _originalCharacterData = CharacterDataField.GetValue(null);

        var dictionaryType = typeof(Dictionary<,>).MakeGenericType(
            CharacterDataField.FieldType.GetGenericArguments());
        CharacterDataField.SetValue(null, Activator.CreateInstance(dictionaryType));

        var locationsField = typeof(Game1).GetField(
            "_locations", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        var game1 = FormatterServices.GetUninitializedObject(typeof(Game1));
        locationsField.SetValue(game1, new List<GameLocation>());

        _originalGame1 = Game1Field.GetValue(null);
        Game1Field.SetValue(null, game1);
    }

    public void Dispose()
    {
        Game1Field.SetValue(null, _originalGame1);
        CharacterDataField.SetValue(null, _originalCharacterData);
        LocalizedContentManager.CurrentLanguageCode = _originalLanguageCode;
        A2APromptBuilder.ResetGossipCache();
        TownIncidentRumorRelay.ResetDailyClaims();
        SetEngineData(new TownIncidentData { SchemaVersion = 1 });
    }

    // ── 助手 ──

    private static void SetEngineData(TownIncidentData data)
    {
        typeof(TownIncidentEngine).GetField("_data", BindingFlags.NonPublic | BindingFlags.Static)
            .SetValue(null, data);
    }

    private static EventSlotContract BuildIncident(string eventName = "Saloon Cook-Off")
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
            EventName = eventName,
            IncidentTheme = "A friendly cooking contest strains old rivalries in Pelican Town.",
        };
        TownIncidentScriptwriter.CreateFallback(incident, isChinese: false);
        return incident;
    }

    private static void ResetWithActiveIncident(string eventName = "Saloon Cook-Off")
    {
        LocalizedContentManager.CurrentLanguageCode = LocalizedContentManager.LanguageCode.en;
        TownIncidentRumorRelay.ResetDailyClaims();
        A2APromptBuilder.ResetGossipCache();
        SetEngineData(new TownIncidentData { SchemaVersion = 1, ActiveIncident = BuildIncident(eventName) });
    }

    private static void ResetWithoutIncident()
    {
        LocalizedContentManager.CurrentLanguageCode = LocalizedContentManager.LanguageCode.en;
        TownIncidentRumorRelay.ResetDailyClaims();
        A2APromptBuilder.ResetGossipCache();
        SetEngineData(new TownIncidentData { SchemaVersion = 1 });
    }

    private static List<NPC> Npcs(params string[] names)
    {
        var list = new List<NPC>();
        foreach (string name in names)
        {
            var npc = new NPC();
            npc.Name = name;
            list.Add(npc);
        }
        return list;
    }

    /// <summary>
    /// 调用 A2APromptBuilder 的私有决选方法（Build 唯一的传闻入口）。
    /// </summary>
    private static bool SelectContext(List<NPC> participants, bool isZh, out string line)
    {
        var method = typeof(A2APromptBuilder).GetMethod(
            "TrySelectA2ARumorContext", BindingFlags.NonPublic | BindingFlags.Static);
        Assert.True(method != null, "TrySelectA2ARumorContext 未找到（签名可能已变更）");

        object[] args = { participants, isZh, null };
        bool result = (bool)method.Invoke(null, args);
        line = (string)args[2];
        return result;
    }

    private static IDisposable UseModConfig()
    {
        return new ModConfigScope();
    }

    private sealed class ModConfigScope : IDisposable
    {
        private readonly ModConfig _original;

        public ModConfigScope()
        {
            _original = ModEntry.Config;
            ModEntry.Config = new ModConfig();
        }

        public void Dispose() => ModEntry.Config = _original;
    }

    /// <summary>
    /// 直接向 PerceptionManager 的八卦队列注入条目：RecordGossip 会经
    /// Game1.currentLocation 取地点名（无头环境下 NRE），故绕过录入语。
    /// </summary>
    private sealed class GossipScope : IDisposable
    {
        private readonly string _key;

        public GossipScope(string key, string template)
        {
            _key = key;

            var queue = (Queue<PerceptionEntry>)typeof(PerceptionManager)
                .GetField("_globalGossip", BindingFlags.NonPublic | BindingFlags.Instance)
                .GetValue(PerceptionManager.Instance);

            queue.Enqueue(new PerceptionEntry
            {
                Key = key,
                Template = template,
                RecordedTimeOfDay = 600,
                LifetimeHours = 20,
                IsGossip = true,
            });
        }

        public void Dispose() => PerceptionManager.Instance.Evict(_key, fromGossip: true);
    }

    private static string ReadSourceOrFail(params string[] relativeParts)
    {
        var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
        for (int i = 0; i < 12; i++)
        {
            string candidate = Path.Combine(dir.FullName, Path.Combine(relativeParts));
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            if (dir.Parent == null) break;
            dir = dir.Parent;
        }

        Assert.True(false, $"{Path.Combine(relativeParts)} not found. Searched upward from {AppDomain.CurrentDomain.BaseDirectory}.");
        return null;
    }

    // ── 验收：八卦快照为空时事件传闻仍可产出，且文案不点名任何在场参与者 ──

    [Fact]
    public void UT01_Preview_Eligible_NoClaim_WithEmptyGossipSnapshots()
    {
        ResetWithActiveIncident();

        Assert.Empty(PerceptionManager.Instance.GetGossipSnapshots());

        var participants = Npcs("Pierre", "Marnie");

        Assert.True(TownIncidentRumorRelay.TryGetIncidentRumorPreviewForA2A(participants, out var rumor));
        Assert.False(string.IsNullOrWhiteSpace(rumor));
        Assert.Contains("Saloon Cook-Off", rumor);
        Assert.Contains("Gus", rumor);

        // A2A 语境行必须保持「不可点名在场者」的形态
        Assert.DoesNotContain("Pierre", rumor);
        Assert.DoesNotContain("Marnie", rumor);
        Assert.DoesNotContain("has heard the talk of the town", rumor);

        // 预览零消耗
        Assert.False(TownIncidentRumorRelay.HasClaimedToday("Pierre"));
        Assert.False(TownIncidentRumorRelay.HasClaimedToday("Marnie"));
    }

    // ── 验收：认领把所有过滤后参与者写入共享表 ──

    [Fact]
    public void UT02_Claim_RegistersEveryFilteredParticipant()
    {
        ResetWithActiveIncident();

        Assert.True(TownIncidentRumorRelay.TryGetIncidentRumorPreviewForA2A(
            Npcs("Pierre", "Marnie", "Penny"), out var preview));

        Assert.True(TownIncidentRumorRelay.TryClaimA2AIncidentRumor(
            Npcs("Pierre", "Marnie", "Penny"), out var claimed));
        Assert.Equal(preview, claimed);

        Assert.True(TownIncidentRumorRelay.HasClaimedToday("Pierre"));
        Assert.True(TownIncidentRumorRelay.HasClaimedToday("Marnie"));
        Assert.True(TownIncidentRumorRelay.HasClaimedToday("Penny"));
    }

    // ── 验收：A2A 认领与 MainDialogue / Bark 共用同一张表（跨消费者互斥）──

    [Fact]
    public void UT03_A2AClaim_SharedWithMainDialogueAndBark()
    {
        ResetWithActiveIncident();

        Assert.True(TownIncidentRumorRelay.TryClaimA2AIncidentRumor(
            Npcs("Pierre", "Marnie"), out _));

        Assert.False(TownIncidentRumorRelay.TryClaimIncidentRumor(
            "Pierre", TownIncidentRumorConsumer.MainDialogue, out _));
        Assert.False(TownIncidentRumorRelay.TryClaimIncidentRumor(
            "Marnie", TownIncidentRumorConsumer.AmbientBark, out _));
        Assert.False(TownIncidentRumorRelay.TryGetIncidentRumorPreviewForA2A(
            Npcs("Pierre", "Marnie"), out _));

        // A2A 认领不占用 MainDialogue 的 2/日配额
        Assert.True(TownIncidentRumorRelay.TryClaimIncidentRumor(
            "Penny", TownIncidentRumorConsumer.MainDialogue, out _));
        Assert.True(TownIncidentRumorRelay.TryClaimIncidentRumor(
            "Sam", TownIncidentRumorConsumer.MainDialogue, out _));
        Assert.False(TownIncidentRumorRelay.TryClaimIncidentRumor(
            "Lewis", TownIncidentRumorConsumer.MainDialogue, out _));
    }

    // ── 失败路径：过滤后参与者 < 2 ⇒ 零认领 ──

    [Fact]
    public void UT04_FilteredBelowTwo_Rejected_ZeroClaims()
    {
        ResetWithActiveIncident();

        var cases = new List<List<NPC>>
        {
            null,
            new List<NPC>(),
            Npcs("Pierre"),
            Npcs("Pierre", "Wizard"),          // RFC 案外人黑名单
            Npcs("Pierre", "Gus"),             // 事件参与者
            Npcs("Pierre", "pierre"),          // 同一 NPC 重复出现
            new List<NPC> { null, null },
        };

        foreach (var participants in cases)
        {
            Assert.False(TownIncidentRumorRelay.TryGetIncidentRumorPreviewForA2A(participants, out var preview));
            Assert.Null(preview);
            Assert.False(TownIncidentRumorRelay.TryClaimA2AIncidentRumor(participants, out _));
        }

        Assert.False(TownIncidentRumorRelay.HasClaimedToday("Pierre"));
        Assert.False(TownIncidentRumorRelay.HasClaimedToday("Wizard"));
    }

    // ── 失败路径：传闻文案涉及在场参与者 ⇒ 拒绝且不消耗任何认领 ──

    [Fact]
    public void UT05_RumorInvolvingParticipant_Rejected_ZeroClaims()
    {
        ResetWithActiveIncident("Penny Cook-Off");

        var participants = Npcs("Pierre", "Penny");

        Assert.False(TownIncidentRumorRelay.TryGetIncidentRumorPreviewForA2A(participants, out var preview));
        Assert.Null(preview);
        Assert.False(TownIncidentRumorRelay.TryClaimA2AIncidentRumor(participants, out _));

        Assert.False(TownIncidentRumorRelay.HasClaimedToday("Pierre"));
        Assert.False(TownIncidentRumorRelay.HasClaimedToday("Penny"));

        // 换一对未被点名的参与者即可正常产出 ⇒ 证明上一步是被文案内容挡下，而非资格链误判
        Assert.True(TownIncidentRumorRelay.TryGetIncidentRumorPreviewForA2A(
            Npcs("Pierre", "Marnie"), out _));
    }

    // ── 失败路径：无活跃事件 ⇒ 无传闻、无认领 ──

    [Fact]
    public void UT06_NoIncident_NoRumor_NoClaim()
    {
        ResetWithoutIncident();

        var participants = Npcs("Pierre", "Marnie");

        Assert.False(TownIncidentRumorRelay.TryGetIncidentRumorPreviewForA2A(participants, out var preview));
        Assert.Null(preview);
        Assert.False(TownIncidentRumorRelay.TryClaimA2AIncidentRumor(participants, out _));
        Assert.False(TownIncidentRumorRelay.HasClaimedToday("Pierre"));
    }

    // ── 失败路径：relay 渲染为空（BUG）⇒ 记 Error 且零认领 ──

    [Fact]
    public void UT07_EmptyRender_BugPath_NoClaim()
    {
        ResetWithActiveIncident();

        var broken = BuildIncident();
        broken.AssignedRoles.Remove("Champion");
        SetEngineData(new TownIncidentData { SchemaVersion = 1, ActiveIncident = broken });

        var participants = Npcs("Pierre", "Marnie");

        Assert.False(TownIncidentRumorRelay.TryGetIncidentRumorPreviewForA2A(participants, out var preview));
        Assert.Null(preview);
        Assert.False(TownIncidentRumorRelay.TryClaimA2AIncidentRumor(participants, out _));
        Assert.False(TownIncidentRumorRelay.HasClaimedToday("Pierre"));

        // 修复事件壳后同一批参与者立即可用 ⇒ BUG 路径没有留下任何残留状态
        SetEngineData(new TownIncidentData { SchemaVersion = 1, ActiveIncident = BuildIncident() });
        Assert.True(TownIncidentRumorRelay.TryClaimA2AIncidentRumor(participants, out _));
    }

    // ── 验收：builder 决选命中事件传闻，但此刻尚未消耗认领（preview-only）──

    [Fact]
    public void UT08_BuilderSelection_IncidentRumor_PreviewOnly()
    {
        ResetWithActiveIncident();
        Assert.Empty(PerceptionManager.Instance.GetGossipSnapshots());

        var participants = Npcs("Pierre", "Marnie");

        Assert.True(SelectContext(participants, isZh: false, out string first));
        Assert.Contains("Saloon Cook-Off", first);

        // 预览零消耗 ⇒ Build 走到 claim-after-accept 之前不会污染共享表
        Assert.False(TownIncidentRumorRelay.HasClaimedToday("Pierre"));
        Assert.False(TownIncidentRumorRelay.HasClaimedToday("Marnie"));

        // 可重复预览（无副作用的表征）
        Assert.True(SelectContext(participants, isZh: false, out string second));
        Assert.Equal(first, second);
        Assert.False(TownIncidentRumorRelay.HasClaimedToday("Pierre"));
    }

    // ── 回归：无事件时逐字节回落到普通八卦路径（含 key 去重）──

    [Fact]
    public void UT09_BuilderSelection_FallsBackToOrdinaryGossip()
    {
        ResetWithoutIncident();

        var participants = Npcs("Pierre", "Marnie");

        using (new GossipScope("Tie008Fallback", "Lewis misplaced his purple shorts again."))
        {
            Assert.False(SelectContext(participants, isZh: false, out string first));
            Assert.Equal("Lewis misplaced his purple shorts again.", first);

            // _usedA2AGossipKeys 去重语义未变：同一天内同一条八卦不再二次出镜
            Assert.False(SelectContext(participants, isZh: false, out string second));
            Assert.Null(second);

            A2APromptBuilder.ResetGossipCache();
            Assert.False(SelectContext(participants, isZh: false, out string third));
            Assert.Equal("Lewis misplaced his purple shorts again.", third);
        }
    }

    // ── 回归：事件传闻优先，但不吃掉普通八卦的当日去重额度 ──

    [Fact]
    public void UT10_IncidentRumor_Wins_WithoutConsumingGossipKey()
    {
        ResetWithActiveIncident();

        var participants = Npcs("Pierre", "Marnie");

        using (new GossipScope("Tie008Precedence", "Willy swears the lake has been acting strange."))
        {
            Assert.True(SelectContext(participants, isZh: false, out string rumor));
            Assert.Contains("Saloon Cook-Off", rumor);

            SetEngineData(new TownIncidentData { SchemaVersion = 1 });
            Assert.False(SelectContext(participants, isZh: false, out string gossip));
            Assert.Equal("Willy swears the lake has been acting strange.", gossip);
        }
    }

    // ── 验收：A2A 输出 schema 与 DialogueModels 未被改动 ──

    [Fact]
    public void UT11_A2ARequest_Schema_Unchanged()
    {
        var props = typeof(DialogueModels.A2ARequest)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(p => p.Name)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            new[] { "IsChinese", "NamesLog", "SystemPrompt", "UserPrompt" },
            props);

        string models = ReadSourceOrFail("src", "Dialogue", "Ambient", "DialogueModels.cs");
        Assert.DoesNotContain("IncidentRumor", models);
        Assert.DoesNotContain("TownIncidentRumorRelay", models);
    }

    // ── 验收：事件传闻只经 gossip 语境槽入镜，不参与发言人台词构建 ──

    [Fact]
    public void UT12_RumorEntersContextSlotOnly()
    {
        string builder = ReadSourceOrFail("src", "Dialogue", "Ambient", "A2A", "A2APromptBuilder.cs");

        // 预览与认领各自只有一个不动点
        Assert.Equal(1, CountOccurrences(builder, "TryGetIncidentRumorPreviewForA2A"));
        Assert.Equal(1, CountOccurrences(builder, "TryClaimA2AIncidentRumor"));

        // 传闻的唯一落点仍是既有 gossip 语境行/话题钩子
        Assert.Contains("最近的小镇传闻：{gossip}", builder);
        Assert.DoesNotContain("personaLines.Add(gossip", builder);

        // 发言人台词只由 LLM 回传的 line 提供，relay 文本不进入发言人池
        string sessionSource = ReadSourceOrFail("src", "Dialogue", "Ambient", "A2A", "A2ASessionManager.cs");
        Assert.DoesNotContain("TownIncidentRumorRelay.TryGetIncidentRumorPreviewForA2A", sessionSource);
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0;
        int index = haystack.IndexOf(needle, StringComparison.Ordinal);
        while (index >= 0)
        {
            count++;
            index = haystack.IndexOf(needle, index + needle.Length, StringComparison.Ordinal);
        }
        return count;
    }

    // ── 验收：换天 / 回标题沿用既有订阅撤离共享认领，且无重复订阅 ──

    [Fact]
    public void UT13_LifecycleResets_SharedeRegistry_NoDuplicateHandlers()
    {
        var methods = typeof(A2ASessionManager).GetMethods(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        foreach (string handler in new[] { "OnDayStarted", "OnReturnedToTitle" })
        {
            int count = methods.Count(m => string.Equals(m.Name, handler, StringComparison.Ordinal));
            Assert.Equal(1, count);
        }

        var manager = new A2ASessionManager(null, null, null, null);

        ResetWithActiveIncident();
        Assert.True(TownIncidentRumorRelay.TryClaimA2AIncidentRumor(Npcs("Pierre", "Marnie"), out _));
        manager.OnDayStarted();
        Assert.False(TownIncidentRumorRelay.HasClaimedToday("Pierre"));
        Assert.False(TownIncidentRumorRelay.HasClaimedToday("Marnie"));

        ResetWithActiveIncident();
        Assert.True(TownIncidentRumorRelay.TryClaimA2AIncidentRumor(Npcs("Penny", "Sam"), out _));
        manager.OnReturnedToTitle();
        Assert.False(TownIncidentRumorRelay.HasClaimedToday("Penny"));
        Assert.False(TownIncidentRumorRelay.HasClaimedToday("Sam"));
    }

    // ── 验收：中文渲染同样剥除面向单个 NPC 的主语前缀 ──

    [Fact]
    public void UT14_ZhPreview_StripsSubjectPrefix()
    {
        var original = LocalizedContentManager.CurrentLanguageCode;
        try
        {
            LocalizedContentManager.CurrentLanguageCode = LocalizedContentManager.LanguageCode.zh;
            TownIncidentRumorRelay.ResetDailyClaims();
            SetEngineData(new TownIncidentData { SchemaVersion = 1, ActiveIncident = BuildIncident() });

            Assert.True(TownIncidentRumorRelay.TryGetIncidentRumorPreviewForA2A(
                Npcs("Pierre", "Marnie"), out var rumor));

            Assert.DoesNotContain("听说了镇上最近的热议", rumor);
            Assert.Contains("Gus", rumor);
            Assert.DoesNotContain("Pierre", rumor);
            Assert.DoesNotContain("皮埃尔", rumor);
        }
        finally
        {
            LocalizedContentManager.CurrentLanguageCode = original;
        }
    }
}
