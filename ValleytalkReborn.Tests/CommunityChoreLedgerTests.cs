// CommunityChoreLedgerTests.cs
// CHORE-003: contract tests and asset verification for the Community Chore Ledger.
// Headless tests only. Manager state is injected via reflection on the new ledger's
// private static fields: _entries, _entriesByNpcAndSchedule, _isSaveLoaded.
// SMAPI Context is faked by InstallHeadlessContext (AssemblyResolve + GameRunner
// injection); multiplayer is toggled by manipulating GameRunner.gameInstances count.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using Newtonsoft.Json;
using StardewModdingAPI;
using StardewValley;
using ValleytalkReborn;
using Xunit;

namespace ValleytalkReborn.Tests;

[Collection("StaticGlobalStateCollection")]
public class CommunityChoreLedgerTests
{
    public CommunityChoreLedgerTests()
    {
        TestEnvironment.InstallHeadlessContext();
    }

    // ── 反射助手：读写账目管理器私有静态字段 ──

    private static void SetField(string name, object value)
    {
        typeof(CommunityChoreLedger).GetField(name,
            BindingFlags.NonPublic | BindingFlags.Static)!.SetValue(null, value);
    }

    private static T GetField<T>(string name)
    {
        return (T)typeof(CommunityChoreLedger).GetField(name,
            BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null);
    }

    // ── 上下文控制 ──

    private static void SetMultiplayer(bool multi)
    {
        var runner = typeof(GameRunner).GetField("instance",
            BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)!.GetValue(null);
        var fi = typeof(GameRunner).GetField("gameInstances",
            BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance)!;
        var list = fi.GetValue(runner);
        list.GetType().GetMethod("Clear")!.Invoke(list, null);
        if (!multi) return;
        var add = list.GetType().GetMethod("Add");
        add.Invoke(list, new[] { FormatterServices.GetUninitializedObject(typeof(Game1)) });
        add.Invoke(list, new[] { FormatterServices.GetUninitializedObject(typeof(Game1)) });
    }

    private static void SetWorldReady(bool ready)
    {
        // Context.IsWorldReady only exposes a private setter; assign via reflection.
        typeof(Context).GetProperty("IsWorldReady")!
            .GetSetMethod(nonPublic: true)!
            .Invoke(null, new object[] { ready });
    }

    private static void SetupContext(bool multiplayer, bool worldReady, bool saveLoaded)
    {
        SetMultiplayer(multiplayer);
        SetWorldReady(worldReady);
        SetField("_isSaveLoaded", saveLoaded);
    }

    // ── 资产加载 ──

    private static string ResolveAssetPath()
    {
        var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
        for (int i = 0; i < 12; i++)
        {
            var src = Path.Combine(dir.FullName, "src", "assets", "ChoreLedgerData.json");
            if (File.Exists(src)) return src;
            var output = Path.Combine(dir.FullName, "assets", "ChoreLedgerData.json");
            if (File.Exists(output)) return output;
            if (dir.Parent == null) break;
            dir = dir.Parent;
        }
        return null;
    }

    private static List<ChoreLedgerEntry> LoadAssetEntries()
    {
        var path = ResolveAssetPath();
        Assert.True(path != null,
            $"ChoreLedgerData.json not found. Searched upward from {AppDomain.CurrentDomain.BaseDirectory}.");
        var json = File.ReadAllText(path);
        List<ChoreLedgerEntry> entries;
        try
        {
            entries = JsonConvert.DeserializeObject<List<ChoreLedgerEntry>>(json);
        }
        catch (Exception ex)
        {
            Assert.True(false, $"ChoreLedgerData.json deserialization failed: {ex}.");
            return null!; // unreachable
        }
        Assert.True(entries != null, "ChoreLedgerData.json deserialized to null.");
        return entries;
    }

    // ── 测试夹具构建 ──

    private static ChoreTopic Topic(string motivation, string opinion) =>
        new ChoreTopic { InnerMotivation = motivation, PublicOpinion = opinion };

    private static ChoreLedgerEntry ValidEntry(
        string choreId = "chore-test-01",
        string source = "Gus",
        string target = "Robin",
        int dayOfWeek = 1,
        List<string>? seasons = null)
    {
        return new ChoreLedgerEntry
        {
            ChoreId = choreId,
            SourceNpc = source,
            TargetNpc = target,
            ApplicableSeasons = seasons ?? new List<string> { "spring", "summer", "fall" },
            DayOfWeek = dayOfWeek,
            SourceTopic = Topic("Source motivation text.", "Source public opinion text."),
            TargetTopic = Topic("Target motivation text.", "Target public opinion text."),
        };
    }

    private static bool InvokeTryValidate(ChoreLedgerEntry entry)
    {
        var method = typeof(CommunityChoreLedger).GetMethod("TryValidateEntry",
            BindingFlags.NonPublic | BindingFlags.Static)!;
        var args = new object[] { entry, null };
        return (bool)method.Invoke(null, args);
    }

    // ════════════════════════════════════════════════════════════════
    // UT01 — 资产 JSON 可反序列化为 ChoreLedgerEntry 列表
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public void UT01_AssetJson_ParsesAsChoreEntryList()
    {
        var entries = LoadAssetEntries();
        Assert.NotNull(entries);
        Assert.IsAssignableFrom<List<ChoreLedgerEntry>>(entries);
        Assert.NotEmpty(entries);
    }

    // ════════════════════════════════════════════════════════════════
    // UT02 — 资产包含至少三个要求的家务场景
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public void UT02_AssetContainsAtLeastThreeRequiredExamples()
    {
        var entries = LoadAssetEntries();
        Assert.True(entries.Count >= 3,
            $"Expected at least 3 chore entries, found {entries.Count}.");

        var ids = entries.Select(e => e.ChoreId).ToHashSet();
        Assert.Contains("chore-01-saloon-dairy-restock", ids);
        Assert.Contains("chore-02-shelf-repair", ids);
        Assert.Contains("chore-03-boat-anchor-rust", ids);
    }

    // ════════════════════════════════════════════════════════════════
    // UT03 — 无效 DayOfWeek 被拒绝（有效范围 1..7）
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public void UT03_InvalidDayOfWeek_IsRejected()
    {
        Assert.False(InvokeTryValidate(ValidEntry(dayOfWeek: 0)),
            "DayOfWeek=0 must be rejected.");
        Assert.False(InvokeTryValidate(ValidEntry(dayOfWeek: 8)),
            "DayOfWeek=8 must be rejected.");
        Assert.False(InvokeTryValidate(ValidEntry(dayOfWeek: -1)),
            "DayOfWeek=-1 must be rejected.");

        for (int dow = 1; dow <= 7; dow++)
        {
            Assert.True(InvokeTryValidate(ValidEntry(dayOfWeek: dow)),
                $"DayOfWeek={dow} must be accepted (valid 1..7 range).");
        }
    }

    // ════════════════════════════════════════════════════════════════
    // UT04 — Source 与 Target 必须不同
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public void UT04_SourceAndTargetMustDiffer()
    {
        Assert.False(InvokeTryValidate(ValidEntry(source: "Gus", target: "Gus")),
            "SourceNpc == TargetNpc must be rejected.");
        Assert.True(InvokeTryValidate(ValidEntry(source: "Gus", target: "Robin")),
            "Distinct SourceNpc/TargetNpc must be accepted.");
    }

    // ════════════════════════════════════════════════════════════════
    // UT05 — 所有资产条目的 Source/Target 话题非空且彼此不同
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public void UT05_SourceAndTargetTopicsAreDistinctAndNonEmpty()
    {
        var entries = LoadAssetEntries();
        foreach (var e in entries)
        {
            Assert.False(string.IsNullOrWhiteSpace(e.ChoreId), "ChoreId must be non-empty.");
            Assert.False(string.IsNullOrWhiteSpace(e.SourceNpc), "SourceNpc must be non-empty.");
            Assert.False(string.IsNullOrWhiteSpace(e.TargetNpc), "TargetNpc must be non-empty.");
            Assert.True(e.SourceNpc != e.TargetNpc,
                $"ChoreId={e.ChoreId}: SourceNpc and TargetNpc must differ.");

            Assert.NotNull(e.SourceTopic);
            Assert.NotNull(e.TargetTopic);
            Assert.False(string.IsNullOrWhiteSpace(e.SourceTopic.InnerMotivation));
            Assert.False(string.IsNullOrWhiteSpace(e.SourceTopic.PublicOpinion));
            Assert.False(string.IsNullOrWhiteSpace(e.TargetTopic.InnerMotivation));
            Assert.False(string.IsNullOrWhiteSpace(e.TargetTopic.PublicOpinion));

            Assert.True(e.SourceTopic.InnerMotivation != e.TargetTopic.InnerMotivation
                || e.SourceTopic.PublicOpinion != e.TargetTopic.PublicOpinion,
                $"ChoreId={e.ChoreId}: Source and Target topics must not be identical.");
        }
    }

    // ════════════════════════════════════════════════════════════════
    // UT06 — 确定性碰撞：序号最小的 ChoreId 胜出，与 JSON 顺序无关
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public void UT06_DeterministicCollisionChoosesOrdinalSmallestChoreId()
    {
        Game1.currentSeason = "spring";
        Game1.dayOfMonth = 1; // → DayOfWeek = 1

        // "z-second" > "a-first" 按序号；先注入大序号条目。
        SetField("_entries", new List<ChoreLedgerEntry>
        {
            ValidEntry(choreId: "z-second", dayOfWeek: 1),
            ValidEntry(choreId: "a-first", dayOfWeek: 1),
        });
        SetupContext(multiplayer: false, worldReady: true, saveLoaded: true);

        CommunityChoreLedger.OnDayStarted();
        var winner1 = GetField<Dictionary<string, ChoreLedgerEntry>>("_entriesByNpcAndSchedule");
        Assert.True(winner1.TryGetValue("Gus", out var entry1), "Gus must be scheduled.");
        Assert.Equal("a-first", entry1.ChoreId);

        // 反转注入顺序，结果必须一致。
        SetField("_entries", new List<ChoreLedgerEntry>
        {
            ValidEntry(choreId: "a-first", dayOfWeek: 1),
            ValidEntry(choreId: "z-second", dayOfWeek: 1),
        });
        CommunityChoreLedger.OnDayStarted();
        var winner2 = GetField<Dictionary<string, ChoreLedgerEntry>>("_entriesByNpcAndSchedule");
        Assert.Equal("a-first", winner2["Gus"].ChoreId);
    }

    // ════════════════════════════════════════════════════════════════
    // UT07 — SaveLoaded 前查询返回 false
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public void UT07_PreSaveLoadedLookupReturnsFalse()
    {
        SetupContext(multiplayer: false, worldReady: true, saveLoaded: false);

        Assert.False(CommunityChoreLedger.TryGetTopic("Gus", out var topic));
        Assert.Null(topic);
    }

    // ════════════════════════════════════════════════════════════════
    // UT08 — 多人模式查询返回 false 且不改变内存调度
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public void UT08_MultiplayerLookupReturnsFalse()
    {
        var entry = ValidEntry();
        SetField("_entriesByNpcAndSchedule", new Dictionary<string, ChoreLedgerEntry>(
            StringComparer.OrdinalIgnoreCase)
        {
            ["Gus"] = entry,
            ["Robin"] = entry,
        });
        SetupContext(multiplayer: true, worldReady: true, saveLoaded: true);

        Assert.False(CommunityChoreLedger.TryGetTopic("Gus", out var topic),
            "Multiplayer lookup must return false.");
        Assert.Null(topic);

        // 内存调度未被修改。
        var after = GetField<Dictionary<string, ChoreLedgerEntry>>("_entriesByNpcAndSchedule");
        Assert.True(after.ContainsKey("Gus"), "Schedule must remain unchanged in multiplayer.");
        Assert.Equal("chore-test-01", after["Gus"].ChoreId);
    }

    // ════════════════════════════════════════════════════════════════
    // UT09 — 未编排的 NPC 查询返回 false
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public void UT09_UnscheduledNpcReturnsFalse()
    {
        var entry = ValidEntry();
        SetField("_entriesByNpcAndSchedule", new Dictionary<string, ChoreLedgerEntry>(
            StringComparer.OrdinalIgnoreCase)
        {
            ["Gus"] = entry,
            ["Robin"] = entry,
        });
        SetupContext(multiplayer: false, worldReady: true, saveLoaded: true);

        Assert.False(CommunityChoreLedger.TryGetTopic("Pierre", out var topic),
            "Unscheduled NPC must return false.");
        Assert.Null(topic);
    }

    // ════════════════════════════════════════════════════════════════
    // UT10 — 编排为 Source 的 NPC 返回 SourceTopic
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public void UT10_ScheduledSourceReturnsSourceTopic()
    {
        var entry = ValidEntry(choreId: "chore-src", source: "Abigail", target: "Sebastian");
        SetField("_entriesByNpcAndSchedule", new Dictionary<string, ChoreLedgerEntry>(
            StringComparer.OrdinalIgnoreCase)
        {
            ["Abigail"] = entry,
            ["Sebastian"] = entry,
        });
        SetupContext(multiplayer: false, worldReady: true, saveLoaded: true);

        Assert.True(CommunityChoreLedger.TryGetTopic("Abigail", out var topic),
            "Scheduled source NPC Abigail must return a topic.");
        Assert.Contains("[Community Chore]", topic);
        Assert.Contains("Source motivation text.", topic);
        Assert.Contains("Source public opinion text.", topic);
        Assert.DoesNotContain("Target motivation text.", topic);
    }

    // ════════════════════════════════════════════════════════════════
    // UT11 — 编排为 Target 的 NPC 返回 TargetTopic
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public void UT11_ScheduledTargetReturnsTargetTopic()
    {
        var entry = ValidEntry(choreId: "chore-tgt", source: "Abigail", target: "Sebastian");
        SetField("_entriesByNpcAndSchedule", new Dictionary<string, ChoreLedgerEntry>(
            StringComparer.OrdinalIgnoreCase)
        {
            ["Abigail"] = entry,
            ["Sebastian"] = entry,
        });
        SetupContext(multiplayer: false, worldReady: true, saveLoaded: true);

        Assert.True(CommunityChoreLedger.TryGetTopic("Sebastian", out var topic),
            "Scheduled target NPC Sebastian must return a topic.");
        Assert.Contains("[Community Chore]", topic);
        Assert.Contains("Target motivation text.", topic);
        Assert.Contains("Target public opinion text.", topic);
        Assert.DoesNotContain("Source motivation text.", topic);
    }

    // ════════════════════════════════════════════════════════════════
    // UT12 — 账目管理器实现不依赖 SaveData / ModData
    // ════════════════════════════════════════════════════════════════

    [Fact]
    public void UT12_NoSaveDataOrModDataDependencyInLedgerImplementation()
    {
        var type = typeof(CommunityChoreLedger);

        // 静态类，无实例状态。
        Assert.True(type.IsAbstract && type.IsSealed,
            "CommunityChoreLedger must be a static class.");

        // 所有静态字段均不涉及 SaveData / ModData。
        foreach (var f in type.GetFields(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static))
        {
            Assert.False(f.FieldType.FullName!.Contains("SaveData")
                || f.FieldType.FullName.Contains("ModData"),
                $"Field '{f.Name}' ({f.FieldType.FullName}) must not reference SaveData/ModData.");
        }

        // 所有方法签名（参数/返回类型）均不涉及 SaveData / ModData。
        foreach (var m in type.GetMethods(BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static))
        {
            Assert.False(m.ReturnType.FullName!.Contains("SaveData")
                || m.ReturnType.FullName.Contains("ModData"),
                $"Method '{m.Name}' return type must not reference SaveData/ModData.");
            foreach (var p in m.GetParameters())
            {
                Assert.False(p.ParameterType.FullName!.Contains("SaveData")
                    || p.ParameterType.FullName.Contains("ModData"),
                    $"Method '{m.Name}' parameter '{p.Name}' ({p.ParameterType.FullName}) must not reference SaveData/ModData.");
            }
        }
    }
}
