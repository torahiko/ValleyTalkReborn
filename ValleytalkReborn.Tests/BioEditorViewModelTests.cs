using System.Runtime.Serialization;
using ValleytalkReborn;
using Xunit;

// 直接引用主工程 internal 成员（InternalsVisibleTo 已配置）。
// 仅覆盖静态纯函数 + 极少量需 VM 实例的纯方法；实例方法依赖 Game1/BioStorageService 的不测。
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
        typeof(BioEditorViewModel).GetField("_npcName", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .SetValue(vm, npc);
        return vm;
    }
}
