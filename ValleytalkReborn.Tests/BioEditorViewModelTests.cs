#nullable disable

using System.Reflection;
using System.Runtime.Serialization;
using Newtonsoft.Json;
using StardewValley;
using StardewValley.Locations;
using ValleytalkReborn;
using ValleytalkReborn.Services;
using Xunit;

// 直接引用主工程 internal 成员（InternalsVisibleTo 已配置）。
// 仅覆盖静态纯函数 + 极少量需 VM 实例的纯方法；实例方法依赖 Game1/BioStorageService 的不测。
[Collection("EngineStaticStateCollection")]
public class BioEditorViewModelTests
{
    // ── A. BuildBiographyScaffold ─────────────────────────────────────
    // 需 VM 实例，但该方法为纯逻辑（仅用 _npcName + 常量）。
    // 用 FormatterServices.GetUninitializedObject 绕过构造函数，避免依赖 BioStorageService/Game1。
    [Fact]
    public void A1_Scaffold_ReplacesNpcToken()
    {
        var vm = Vm("Robin");
        string out_ = vm.BuildBiographyScaffold();
        Assert.Contains("Robin", out_);
        Assert.DoesNotContain("{NPC}", out_);
    }

    [Fact]
    public void A2_Scaffold_ContainsSectionHeaders()
    {
        var vm = Vm("Robin");
        string out_ = vm.BuildBiographyScaffold();
        Assert.Contains("[IDENTITY]", out_);
        Assert.Contains("[PSYCHOLOGICAL CONFLICTS]", out_);
    }

    // ── B. ApplyDialogueBreakInsert ───────────────────────────────────
    [Theory]
    [InlineData(null, "#$b#")]
    [InlineData("", "#$b#")]
    [InlineData("abc", "abc#$b#")]
    public void B_BreakInsert(string input, string expected) =>
        Assert.Equal(expected, BioEditorViewModel.ApplyDialogueBreakInsert(input));

    // ── C. ApplyDialogueChoiceInsert ──────────────────────────────────
    [Theory]
    [InlineData(null, "\n% 选项文本内容")]
    [InlineData("abc  ", "abc\n% 选项文本内容")]
    [InlineData("abc\n", "abc\n% 选项文本内容")]
    public void C_ChoiceInsert(string input, string expected) =>
        Assert.Equal(expected, BioEditorViewModel.ApplyDialogueChoiceInsert(input));

    // ── D. AppendScrapedLines ─────────────────────────────────────────
    [Fact]
    public void D1_SkipsBlankLines()
    {
        string result = BioEditorViewModel.AppendScrapedLines(
            "", new List<string> { "", "   ", "X" }, out int a);
        Assert.Equal(1, a);
        Assert.Equal("- X", result);
    }

    [Fact]
    public void D2_ExactDuplicateWithPrefixNormalization_Skipped()
    {
        string result = BioEditorViewModel.AppendScrapedLines(
            "- Hello", new List<string> { "hello" }, out int a); // 大小写不敏感 + 前缀归一化
        Assert.Equal(0, a);
    }

    [Fact]
    public void D3_Substring_NotFalselyDeduped()
    {
        // 既有 "Hello there" 时候选 "hell"（子串）必须被追加（固化误杀修复）
        string result = BioEditorViewModel.AppendScrapedLines(
            "- Hello there", new List<string> { "hell" }, out int a);
        Assert.Equal(1, a);
        Assert.Contains("- hell", result);
    }

    [Fact]
    public void D4_TrimsLineContent()
    {
        string result = BioEditorViewModel.AppendScrapedLines(
            "", new List<string> { "  spaced  " }, out int a);
        Assert.Equal(1, a);
        Assert.Equal("- spaced", result);
    }

    [Fact]
    public void D5_Over4000Limit_NotAppended()
    {
        string big = new string('x', 4001);
        string result = BioEditorViewModel.AppendScrapedLines(
            big, new List<string> { "new" }, out int a);
        Assert.Equal(0, a);
        Assert.DoesNotContain("new", result);
    }

    [Fact]
    public void D6_TrimStart_Effective()
    {
        string result = BioEditorViewModel.AppendScrapedLines(
            "\n\n-first", new List<string> { "X" }, out int a);
        Assert.StartsWith("-first", result);
    }

    [Fact]
    public void D7_Idempotent_SecondCallAddsNothing()
    {
        var lines = new List<string> { "aa", "bb" };
        string first = BioEditorViewModel.AppendScrapedLines(null, lines, out int a1);
        Assert.Equal(2, a1);
        string second = BioEditorViewModel.AppendScrapedLines(first, lines, out int a2);
        Assert.Equal(0, a2);
        Assert.Equal(first, second);
    }

    [Fact]
    public void D8_BatchInternalDedup()
    {
        string result = BioEditorViewModel.AppendScrapedLines(
            null, new List<string> { "dup", "dup", "DUP" }, out int a);
        Assert.Equal(1, a);
        Assert.Single(result.Split('\n'), l => l.Contains("dup"));
    }

    [Fact]
    public void D9_CrLfExisting_ParsedCorrectly()
    {
        string result = BioEditorViewModel.AppendScrapedLines(
            "- line1\r\n- line2", new List<string> { "LINE1", "newline" }, out int a);
        Assert.Equal(1, a); // LINE1 经 Trim 吸收 \r 后被去重
        Assert.Contains("- newline", result);
    }

    // ── E. 门禁循环（OQ-1 对称化后的内部静态重载） ────────────────────
    static BioData.ProgressStateEntry S(
        bool? closed = null, bool? member = null, bool married = false, int hearts = 0) =>
        new BioData.ProgressStateEntry
        {
            RequiredHearts = hearts,
            RequireMarried = married,
            RequireJojaMartClosed = closed,
            RequireJojaMember = member
        };

    [Fact]
    public void E1_Closed_Transitions_AndClearsMember()
    {
        var s = S(member: true);
        BioEditorViewModel.CycleJojaMartClosed(s);
        Assert.True(s.RequireJojaMartClosed == true);
        Assert.Null(s.RequireJojaMember); // Member 被清

        BioEditorViewModel.CycleJojaMartClosed(s); // true -> false
        Assert.False(s.RequireJojaMartClosed.HasValue && s.RequireJojaMartClosed.Value);

        var s2 = S(closed: false);
        BioEditorViewModel.CycleJojaMartClosed(s2); // false -> null
        Assert.Null(s2.RequireJojaMartClosed);
    }

    [Fact]
    public void E2_Member_ClosedConflict_ClearsClosed() // OQ-1 裁决预期
    {
        var s = S(closed: true);
        BioEditorViewModel.CycleJojaMember(s); // null -> true, 与 Closed 冲突 -> 清 Closed
        Assert.True(s.RequireJojaMember == true);
        Assert.Null(s.RequireJojaMartClosed);

        var s2 = S(closed: null);
        BioEditorViewModel.CycleJojaMember(s2); // null -> true, 无冲突
        Assert.True(s2.RequireJojaMember == true);
        Assert.Null(s2.RequireJojaMartClosed);
    }

    [Fact]
    public void E3_Member_Transitions()
    {
        var s = S(member: true);
        BioEditorViewModel.CycleJojaMember(s); // true -> false
        Assert.False(s.RequireJojaMember.HasValue && s.RequireJojaMember.Value);

        var s2 = S(member: false);
        BioEditorViewModel.CycleJojaMember(s2); // false -> null
        Assert.Null(s2.RequireJojaMember);
    }

    [Fact]
    public void E4_RequireMarried_Toggles()
    {
        var s = S();
        BioEditorViewModel.CycleRequireMarried(s);
        Assert.True(s.RequireMarried);
        BioEditorViewModel.CycleRequireMarried(s);
        Assert.False(s.RequireMarried);
    }

    [Fact]
    public void E5_Symmetry_BothSidesClearOpposite()
    {
        var a_ = S(member: true);
        BioEditorViewModel.CycleJojaMartClosed(a_);
        Assert.Null(a_.RequireJojaMember); // Closed 置 true 清 Member

        var b_ = S(closed: true);
        BioEditorViewModel.CycleJojaMember(b_);
        Assert.Null(b_.RequireJojaMartClosed); // Member 置 true 清 Closed
    }

    // ── F. ResolveDisplayNameConflicts ────────────────────────────────
    [Fact]
    public void F1_RealMarlonWinsOverAlias()
    {
        string D(string n) => n == "MarlonFudge2" ? "Marlon" : n;
        var r = BioEditorViewModel.ResolveDisplayNameConflicts(
            new[] { "MarlonFudge2", "Marlon" }, D, null);
        Assert.Equal("Marlon", r["Marlon"]);
    }

    [Fact]
    public void F2_FriendshipBreaksTie()
    {
        string D(string n) => "SameName";
        var r = BioEditorViewModel.ResolveDisplayNameConflicts(
            new[] { "A_nofriend", "B_friend" }, D, new[] { "B_friend" });
        Assert.Equal("B_friend", r["SameName"]);
    }

    [Fact]
    public void F3_ShorterInternalNameWins_EqualKeepsFirst()
    {
        string D(string n) => "Dup";
        var r = BioEditorViewModel.ResolveDisplayNameConflicts(
            new[] { "LongName", "Srt" }, D, new[] { "LongName", "Srt" });
        Assert.Equal("Srt", r["Dup"]); // 等 friendship -> 短者胜

        var r2 = BioEditorViewModel.ResolveDisplayNameConflicts(
            new[] { "only", "only" }, D, null);
        Assert.Single(r2);
        Assert.Equal("only", r2["Dup"]); // 等长先到者胜
    }

    [Fact]
    public void F4_FallbackToInternalName_AndNullFriendship()
    {
        string D(string n) => n == "Ghost" ? "" : n; // 空 displayName -> 回退内部名
        var r = BioEditorViewModel.ResolveDisplayNameConflicts(
            new[] { "Ghost" }, D, null);
        Assert.Equal("Ghost", r["Ghost"]);

        string D2(string n) => "X";
        var r2 = BioEditorViewModel.ResolveDisplayNameConflicts(
            new[] { "P", "Q" }, D2, null); // friendshipNames = null
        Assert.Single(r2); // 先到者胜
        Assert.Equal("P", r2["X"]);
    }

    // ── helper: 构造 VM 实例绕过构造函数（纯方法测试用） ───────────────
    static BioEditorViewModel Vm(string npc)
    {
        var vm = (BioEditorViewModel)FormatterServices.GetUninitializedObject(typeof(BioEditorViewModel));
        typeof(BioEditorViewModel).GetField("_npcName", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(vm, npc);
        return vm;
    }

    // ── G. ResetTabToBaseline(3) 社交关系恢复回归（REL-002C） ──────────
    // 实际恢复入口测试：BioStorageService(null, null) + 预填 _baselineCache，
    // GetBaselineBio 缓存命中走 DeepClone（Newtonsoft 序列化往返）。
    // BioData.Name setter 经 Game1.getCharacterFromName → Utility.ForEachCharacter
    // 遍历三处静态集合；G 段用 EngineStaticShim 以空内存集合垫片隔离，
    // 不加载游戏资产、不触 Content/玩家/选项/世界就绪标志。
    // 使用普通 BioData 走真实序列化与恢复路径（升级方案 A）。

    /// <summary>
    /// 测试侧作用域垫片（升级方案 A，Memory 状态）：保存 Game1.game1、
    /// MineShaft.activeMines、VolcanoDungeon.activeLevels 三个原引用；
    /// 构造未初始化 Game1 实例（_locations = 空表，instanceGameLocation = null）
    /// 指入 Game1.game1，两个生成区域集合替换为新空集合。
    /// 原集合对象不清空、不修改；Dispose 原样恢复全部引用。
    /// </summary>
    private sealed class EngineStaticShim : IDisposable
    {
        private static readonly FieldInfo Game1InstanceField =
            typeof(Game1).GetField("game1", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly FieldInfo Game1LocationsField =
            typeof(Game1).GetField("_locations", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly FieldInfo Game1CurrentLocationField =
            typeof(Game1).GetField("instanceGameLocation", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly FieldInfo ActiveMinesField =
            typeof(MineShaft).GetField("activeMines", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
        private static readonly FieldInfo ActiveLevelsField =
            typeof(VolcanoDungeon).GetField("activeLevels", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

        private readonly object _previousGame1;
        private readonly object _previousActiveMines;
        private readonly object _previousActiveLevels;
        private readonly bool _installed;

        public object OriginalGame1 => _previousGame1;
        public object OriginalActiveMines => _previousActiveMines;
        public object OriginalActiveLevels => _previousActiveLevels;

        public EngineStaticShim()
        {
            Assert.NotNull(Game1InstanceField); // 反射成员缺失：显式失败（BUG），不静默跳过
            _previousGame1 = Game1InstanceField.GetValue(null);
            _previousActiveMines = ActiveMinesField.GetValue(null);
            _previousActiveLevels = ActiveLevelsField.GetValue(null);
            try
            {
                var shimGame1 = (Game1)FormatterServices.GetUninitializedObject(typeof(Game1));
                Game1LocationsField.SetValue(shimGame1, new List<GameLocation>());
                Game1CurrentLocationField.SetValue(shimGame1, null);
                Game1InstanceField.SetValue(null, shimGame1);
                ActiveMinesField.SetValue(null, new List<MineShaft>());
                ActiveLevelsField.SetValue(null, new List<VolcanoDungeon>());
                _installed = true;
            }
            catch
            {
                Restore();
                throw; // 垫片安装失败：显式失败并升级，不吞异常
            }
        }

        public void Dispose()
        {
            if (_installed)
                Restore();
        }

        private void Restore()
        {
            Game1InstanceField.SetValue(null, _previousGame1);
            ActiveMinesField.SetValue(null, _previousActiveMines);
            ActiveLevelsField.SetValue(null, _previousActiveLevels);
        }
    }

    /// <summary>G 段专用：在垫片作用域内执行测试体，结束后验证三个静态引用均恢复原值。</summary>
    private static void WithEngineShim(Action test)
    {
        object previousGame1;
        object previousActiveMines;
        object previousActiveLevels;
        using (var shim = new EngineStaticShim())
        {
            previousGame1 = shim.OriginalGame1;
            previousActiveMines = shim.OriginalActiveMines;
            previousActiveLevels = shim.OriginalActiveLevels;
            test();
        }
        // 到达此处 = 测试体正常完成（断言失败会经 Dispose 恢复后向外传播，不执行本验证）
        Assert.Same(previousGame1, typeof(Game1).GetField("game1", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(null));
        Assert.Same(previousActiveMines, typeof(MineShaft).GetField("activeMines", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(null));
        Assert.Same(previousActiveLevels, typeof(VolcanoDungeon).GetField("activeLevels", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!.GetValue(null));
    }

    static BioEditorViewModel Vm(string npc, BioStorageService storage, BioData bio)
    {
        var vm = (BioEditorViewModel)FormatterServices.GetUninitializedObject(typeof(BioEditorViewModel));
        var type = typeof(BioEditorViewModel);
        type.GetField("_npcName", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(vm, npc);
        type.GetField("_storage", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(vm, storage);
        type.GetField("_bio", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(vm, bio);
        return vm;
    }

    static BioStorageService StorageWithBaseline(string npc, BioData baseline)
    {
        var storage = new BioStorageService(null, null);
        typeof(BioStorageService)
            .GetField("_baselineCache", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(storage, new Dictionary<string, BioData>(System.StringComparer.OrdinalIgnoreCase)
            {
                [npc] = baseline,
            });
        return storage;
    }

    [Fact]
    public void G0_PlainBioData_SerializationRoundTripKeepsNameAndRelationshipFields()
    {
        WithEngineShim(() =>
        {
            var baseline = new BioData();
            baseline.Relationships["George"] = new BioData.ListEntry
            {
                id = "George",
                Heading = "George",
                Description = "desc",
                RequiredHearts = 2,
                PublicIdentity = "grandfather",
            };

            string json = JsonConvert.SerializeObject(baseline);
            Assert.Contains("\"Name\"", json); // 真实序列化路径：Name 字段保留
            var restored = JsonConvert.DeserializeObject<BioData>(json); // Name setter 在空内存环境中正常求值

            Assert.Equal("grandfather", restored.Relationships["George"].PublicIdentity);
            Assert.Equal("George", restored.Relationships["George"].id);
            Assert.Equal("George", restored.Relationships["George"].Heading);
            Assert.Equal("desc", restored.Relationships["George"].Description);
            Assert.Equal(2, restored.Relationships["George"].RequiredHearts);
        });
    }

    [Fact]
    public void G1_ResetRelationshipsTab_RestoresFiveFieldsAndKeySet()
    {
        WithEngineShim(() =>
        {
            var baseline = new BioData();
            // 覆盖：英文、非英文、null（心数>0）、空串、带首尾空白 五类身份值
            baseline.Relationships["George"] = new BioData.ListEntry { id = "George", Heading = "George", Description = "grandpa", RequiredHearts = 0, PublicIdentity = "grandfather" };
            baseline.Relationships["Evelyn"] = new BioData.ListEntry { id = "Evelyn", Heading = "Evelyn", Description = "grandma", RequiredHearts = 0, PublicIdentity = "祖母" };
            baseline.Relationships["Haley"] = new BioData.ListEntry { id = "Haley", Heading = "Haley", Description = "peer", RequiredHearts = 6, PublicIdentity = null };
            baseline.Relationships["Dusty"] = new BioData.ListEntry { id = "Dusty", Heading = "Dusty", Description = "dog", RequiredHearts = 0, PublicIdentity = "" };
            baseline.Relationships["Sam"] = new BioData.ListEntry { id = "Sam", Heading = "Sam", Description = "friend", RequiredHearts = 0, PublicIdentity = "  padded  " };

            var bio = new BioData();
            bio.Relationships["Injected"] = new BioData.ListEntry { Description = "stale" };
            var vm = Vm("GTestNpc", StorageWithBaseline("GTestNpc", baseline), bio);

            vm.ResetTabToBaseline(3);

            var restored = vm.Bio.Relationships;
            Assert.Equal(5, restored.Count);
            Assert.Equal(
                new[] { "Dusty", "Evelyn", "George", "Haley", "Sam" },
                restored.Keys.OrderBy(k => k, System.StringComparer.Ordinal).ToArray());

            void AssertEntry(string key, string id, string heading, string desc, int hearts, string identity)
            {
                var e = restored[key];
                Assert.Equal(id, e.id);
                Assert.Equal(heading, e.Heading);
                Assert.Equal(desc, e.Description);
                Assert.Equal(hearts, e.RequiredHearts);
                Assert.Equal(identity, e.PublicIdentity);
            }

            AssertEntry("George", "George", "George", "grandpa", 0, "grandfather");
            AssertEntry("Evelyn", "Evelyn", "Evelyn", "grandma", 0, "祖母");
            AssertEntry("Haley", "Haley", "Haley", "peer", 6, null);        // null 原样保留
            AssertEntry("Dusty", "Dusty", "Dusty", "dog", 0, "");           // 空串原样保留
            AssertEntry("Sam", "Sam", "Sam", "friend", 0, "  padded  ");    // 不 Trim
            Assert.DoesNotContain("Injected", restored.Keys);
            Assert.True(vm.IsDirty); // MarkDirty 行为保持
        });
    }

    [Fact]
    public void G2_Restore_DoesNotPolluteBaseline_AndIsInstanceIndependent()
    {
        WithEngineShim(() =>
        {
            var baseline = new BioData();
            baseline.Relationships["George"] = new BioData.ListEntry { id = "George", Heading = "George", Description = "grandpa", RequiredHearts = 0, PublicIdentity = "grandfather" };

            var bio = new BioData();
            var vm = Vm("GTestNpc", StorageWithBaseline("GTestNpc", baseline), bio);

            vm.ResetTabToBaseline(3);
            vm.Bio.Relationships["George"].PublicIdentity = "MUTATED";
            vm.Bio.Relationships["George"].Description = "MUTATED";

            // 基线缓存未被污染
            Assert.Equal("grandfather", baseline.Relationships["George"].PublicIdentity);
            Assert.Equal("grandpa", baseline.Relationships["George"].Description);
            // 恢复结果与基线条目实例独立（GetBaselineBio DeepClone 保证）
            Assert.NotSame(baseline.Relationships["George"], vm.Bio.Relationships["George"]);
        });
    }

    [Fact]
    public void G3_DoubleRestore_Identical_AndMutationRecoveredBySecondRestore()
    {
        WithEngineShim(() =>
        {
            var baseline = new BioData();
            baseline.Relationships["George"] = new BioData.ListEntry { id = "George", Heading = "George", Description = "grandpa", RequiredHearts = 0, PublicIdentity = "grandfather" };
            baseline.Relationships["Evelyn"] = new BioData.ListEntry { id = "Evelyn", Heading = "Evelyn", Description = "grandma", RequiredHearts = 0, PublicIdentity = "祖母" };

            var bio = new BioData();
            var vm = Vm("GTestNpc", StorageWithBaseline("GTestNpc", baseline), bio);

            vm.ResetTabToBaseline(3);
            // 快照第一次恢复结果
            var first = vm.Bio.Relationships.ToDictionary(
                kvp => kvp.Key,
                kvp => new BioData.ListEntry
                {
                    id = kvp.Value.id,
                    Heading = kvp.Value.Heading,
                    Description = kvp.Value.Description,
                    RequiredHearts = kvp.Value.RequiredHearts,
                    PublicIdentity = kvp.Value.PublicIdentity,
                });

            // 篡改第一次恢复得到的条目后再次恢复
            vm.Bio.Relationships["George"].PublicIdentity = "MUTATED";
            vm.ResetTabToBaseline(3);

            var second = vm.Bio.Relationships;
            foreach (var kvp in first)
            {
                var s = second[kvp.Key];
                Assert.Equal(kvp.Value.id, s.id);
                Assert.Equal(kvp.Value.Heading, s.Heading);
                Assert.Equal(kvp.Value.Description, s.Description);
                Assert.Equal(kvp.Value.RequiredHearts, s.RequiredHearts);
                Assert.Equal(kvp.Value.PublicIdentity, s.PublicIdentity);
            }
            Assert.Equal("grandfather", second["George"].PublicIdentity); // 篡改被基线值覆盖
        });
    }

    // ── H. 公开身份校验与关系三项同步（REL-002D） ─────────────────────
    // TryValidatePublicIdentity 为纯静态函数（无 Game1/存储依赖）；
    // TrySyncRelationshipEditor/TrySave 用 Vm(npc, storage, bio) 垫片构造：
    // storage 仅占位（校验失败路径先于存储调用返回），不触盘、不触 Game1。

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\n")] // Trim 后为空 → 先判空再判行，合法（与 SocialBackboneBuilder.ValidateLabel 一致）
    [InlineData("grandfather")]
    [InlineData("  grandfather  ")]
    [InlineData("祖母")]
    public void H1_Validate_AcceptsOptionalAndPlainValues(string value)
    {
        Assert.True(BioEditorViewModel.TryValidatePublicIdentity(value, out string errorKey));
        Assert.Null(errorKey);
    }

    [Fact]
    public void H1b_Validate_Accepts48Utf16Units_AsciiAndSurrogatePairs()
    {
        Assert.True(BioEditorViewModel.TryValidatePublicIdentity(new string('x', 48), out _));

        // 24 个代理对字符 = 48 个 UTF-16 单元
        string pairs = string.Concat(Enumerable.Repeat("\uD83D\uDC77", 24));
        Assert.Equal(48, pairs.Length);
        Assert.True(BioEditorViewModel.TryValidatePublicIdentity(pairs, out _));

        // 混合：46 ASCII + 1 代理对 = 48 单元
        Assert.True(BioEditorViewModel.TryValidatePublicIdentity(new string('a', 46) + "\uD83D\uDC77", out _));
    }

    [Theory]
    [InlineData("a\nb")]
    [InlineData("a\rb")]
    [InlineData(" x\r\ny ")]
    [InlineData("第一行\n第二行")]
    public void H2_Validate_RejectsMultiline_AfterTrim(string value)
    {
        Assert.False(BioEditorViewModel.TryValidatePublicIdentity(value, out string errorKey));
        Assert.Equal(BioEditorViewModel.PublicIdentitySingleLineKey, errorKey);
    }

    [Fact]
    public void H3_Validate_Rejects49Utf16Units_AsciiAndSurrogatePairs()
    {
        Assert.False(BioEditorViewModel.TryValidatePublicIdentity(new string('x', 49), out string keyA));
        Assert.Equal(BioEditorViewModel.PublicIdentityTooLongKey, keyA);

        // 25 个代理对字符 = 50 单元
        string pairs = string.Concat(Enumerable.Repeat("\uD83D\uDC77", 25));
        Assert.False(BioEditorViewModel.TryValidatePublicIdentity(pairs, out string keyB));
        Assert.Equal(BioEditorViewModel.PublicIdentityTooLongKey, keyB);

        // 边界：47 ASCII + 1 代理对 = 49 单元
        Assert.False(BioEditorViewModel.TryValidatePublicIdentity(new string('a', 47) + "\uD83D\uDC77", out string keyC));
        Assert.Equal(BioEditorViewModel.PublicIdentityTooLongKey, keyC);
    }

    [Theory]
    [InlineData("  grandfather  ")]
    [InlineData("祖母")]
    public void H4_Sync_PreservesRawValueUntrimmed(string raw)
    {
        var bio = new BioData();
        bio.Relationships["George"] = new BioData.ListEntry { id = "George", Heading = "George", Description = "d", PublicIdentity = "old" };
        var vm = Vm("RelNpc", new BioStorageService(null, null), bio);

        Assert.True(vm.TrySyncRelationshipEditor("George", "H", "D", raw, out string errorKey));
        Assert.Null(errorKey);
        Assert.Equal(raw, vm.Bio.Relationships["George"].PublicIdentity); // 不 Trim、不翻译、不截断
    }

    [Fact]
    public void H10_Getter_ReturnsRawOrNull()
    {
        var bio = new BioData();
        bio.Relationships["George"] = new BioData.ListEntry { id = "George", Heading = "H", Description = "D", PublicIdentity = "  padded  " };
        var vm = Vm("RelNpc", new BioStorageService(null, null), bio);

        Assert.Equal("  padded  ", vm.GetRelationshipPublicIdentityOrNull("George"));
        Assert.Null(vm.GetRelationshipPublicIdentityOrNull("Missing"));
    }

    [Fact]
    public void H5_InvalidSync_LeavesFieldsKeysAndDirtyUntouched()
    {
        var bio = new BioData();
        bio.Relationships["George"] = new BioData.ListEntry { id = "George", Heading = "H0", Description = "D0", RequiredHearts = 2, PublicIdentity = "grandfather" };
        var vm = Vm("RelNpc", new BioStorageService(null, null), bio);

        Assert.False(vm.TrySyncRelationshipEditor("George", "H1", "D1", "bad\nlabel", out string key1));
        Assert.Equal(BioEditorViewModel.PublicIdentitySingleLineKey, key1);

        Assert.False(vm.TrySyncRelationshipEditor("George", "H1", "D1", new string('x', 49), out string key2));
        Assert.Equal(BioEditorViewModel.PublicIdentityTooLongKey, key2);

        var rel = vm.Bio.Relationships["George"];
        Assert.Equal("H0", rel.Heading);
        Assert.Equal("D0", rel.Description);
        Assert.Equal("grandfather", rel.PublicIdentity);
        Assert.Single(vm.Bio.Relationships); // 键集合不变
        Assert.False(vm.IsDirty);            // dirty 不变
    }

    [Fact]
    public void H6_IdentityOnly_CreatesEntry_AllEmptyDoesNot()
    {
        var bio = new BioData();
        var vm = Vm("RelNpc", new BioStorageService(null, null), bio);

        // 无条目且仅身份非空：创建条目
        Assert.True(vm.TrySyncRelationshipEditor("Dusty", "", "", "doctor", out _));
        var entry = vm.Bio.Relationships["Dusty"];
        Assert.Equal("Dusty", entry.id);
        Assert.Equal("", entry.Heading);
        Assert.Equal("", entry.Description);
        Assert.Equal("doctor", entry.PublicIdentity);
        Assert.True(vm.IsDirty);

        // 无条目且三项全空：不创建条目
        Assert.True(vm.TrySyncRelationshipEditor("Nobody", "", "", "", out _));
        Assert.DoesNotContain("Nobody", vm.Bio.Relationships.Keys);
    }

    [Fact]
    public void H7_ClearIdentity_RepeatSyncNoExtraDirty()
    {
        var bio = new BioData();
        bio.Relationships["George"] = new BioData.ListEntry { id = "George", Heading = "H", Description = "D", PublicIdentity = "doctor" };
        var vm = Vm("RelNpc", new BioStorageService(null, null), bio);
        var entry = vm.Bio.Relationships["George"];

        // 同值重复同步：重置 dirty 后再次同步仍为 false → 无额外状态变化
        Assert.True(vm.TrySyncRelationshipEditor("George", "H", "D", "doctor", out _));
        typeof(BioEditorViewModel).GetField("_dirty", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(vm, false);
        Assert.True(vm.TrySyncRelationshipEditor("George", "H", "D", "doctor", out _));
        Assert.False(vm.IsDirty);

        // 清空身份：条目保留，身份写为空串
        Assert.True(vm.TrySyncRelationshipEditor("George", "H2", "D2", "", out _));
        Assert.Equal("", entry.PublicIdentity);
        Assert.Equal("H2", entry.Heading);
        Assert.Equal("D2", entry.Description);
        Assert.True(vm.IsDirty);
    }

    [Fact]
    public void H8_EmptyNpc_IsNoOpReturnsTrue()
    {
        var bio = new BioData();
        bio.Relationships["George"] = new BioData.ListEntry { id = "George", Heading = "H", Description = "D", PublicIdentity = "p" };
        var vm = Vm("RelNpc", new BioStorageService(null, null), bio);

        Assert.True(vm.TrySyncRelationshipEditor("", "X", "Y", "bad\nlabel", out _));
        Assert.Equal("H", vm.Bio.Relationships["George"].Heading);
        Assert.Single(vm.Bio.Relationships);
        Assert.False(vm.IsDirty);
    }

    [Fact]
    public void H9_TrySave_BlockedBeforeStorage_ForInvalidExistingIdentity()
    {
        var bio = new BioData();
        bio.Relationships["George"] = new BioData.ListEntry { id = "George", Heading = "H", Description = "D", PublicIdentity = "bad\nlabel" };
        var vm = Vm("RelNpc", new BioStorageService(null, null), bio);
        // 直接置脏（有效同步会覆盖待测的无效身份，故用反射置位）
        typeof(BioEditorViewModel).GetField("_dirty", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(vm, true);

        Assert.False(vm.TrySave(out string error));
        Assert.Contains("George", error);                                     // error 含目标键
        Assert.Contains(I18n.Get(BioEditorViewModel.PublicIdentitySingleLineKey), error); // 含本地化失败原因
        Assert.True(vm.IsDirty);                                              // 不清除 dirty
        // 存储调用前返回：若 SaveOverlay 被执行，错误信息不会呈现「目标键 + 校验原因」形态
    }

    [Fact]
    public void H9b_TrySave_Blocked_ForTooLongExistingIdentity()
    {
        var bio = new BioData();
        bio.Relationships["George"] = new BioData.ListEntry { id = "George", Heading = "H", Description = "D", PublicIdentity = new string('x', 49) };
        var vm = Vm("RelNpc", new BioStorageService(null, null), bio);

        Assert.False(vm.TrySave(out string error));
        Assert.Contains("George", error);
        Assert.Contains(I18n.Get(BioEditorViewModel.PublicIdentityTooLongKey), error);
    }
}
