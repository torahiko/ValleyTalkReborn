// LlmDialogueOutputBoundaryTests.cs
// DIALOGUE-001 — LlmDialogueService.SelectFirstDialogueTurn 首轮对话边界测试
// （internal 静态助手，InternalsVisibleTo 已配置）。
// 覆盖：第二轮截断、#$b# 保留、多行第一轮、无选项不截断、纯选项回退、
// 无前缀回退、null 拒绝、空输入、选项后杂行不截断。
// 纯内存测试：无游戏实例、无 Provider、无网络。

using System;
using System.Collections.Generic;
using ValleytalkReborn;
using Xunit;

public class LlmDialogueOutputBoundaryTests
{
    // AC-1 + AC-2：首轮台词 + 首轮选项 + 第二轮 '-' → 仅保留第一轮，truncated=true
    [Fact]
    public void SecondTurnAfterFirstOptions_KeepsOnlyFirstTurn_AndTruncates()
    {
        var lines = new List<string>
        {
            "- First turn dialogue",
            "% Option A",
            "% Option B",
            "- Second turn dialogue",
            "% Option C",
        };

        LlmDialogueService.SelectFirstDialogueTurn(lines, out var dialogue, out var responses, out var truncated);

        Assert.True(truncated);
        Assert.Single(dialogue);
        Assert.Equal("- First turn dialogue", dialogue[0]);
        Assert.Equal(2, responses.Count);
        Assert.Equal("% Option A", responses[0]);
        Assert.Equal("% Option B", responses[1]);
    }

    // AC-3：单行台词内嵌 #$b# 分页标记 → 原样保留，不拆分
    [Fact]
    public void PageBreakInsideDialogueLine_PreservedUnchanged()
    {
        var lines = new List<string> { "- Page one#$b#Page two", "% Continue" };

        LlmDialogueService.SelectFirstDialogueTurn(lines, out var dialogue, out var responses, out var truncated);

        Assert.False(truncated);
        Assert.Single(dialogue);
        Assert.Equal("- Page one#$b#Page two", dialogue[0]);
        Assert.Single(responses);
    }

    // AC-4：首个 '%' 之前的多行 '-' → 全部保留为第一轮（沿用既有 join 行为）
    [Fact]
    public void MultipleDialogueLinesBeforeOptions_AllRetainedAsFirstTurn()
    {
        var lines = new List<string> { "- Line one", "- Line two", "% Option" };

        LlmDialogueService.SelectFirstDialogueTurn(lines, out var dialogue, out var responses, out var truncated);

        Assert.False(truncated);
        Assert.Equal(2, dialogue.Count);
        Assert.Equal("- Line one", dialogue[0]);
        Assert.Equal("- Line two", dialogue[1]);
        Assert.Single(responses);
    }

    // AC-5：无 '%' 选项时，多行 '-' 不触发截断
    [Fact]
    public void NoOptions_MultipleDialogueLines_NotTruncated()
    {
        var lines = new List<string> { "- Line one", "- Line two", "- Line three" };

        LlmDialogueService.SelectFirstDialogueTurn(lines, out var dialogue, out var responses, out var truncated);

        Assert.False(truncated);
        Assert.Equal(3, dialogue.Count);
        Assert.Empty(responses);
    }

    // AC-6：仅 '%' 选项 → 无对话结果（沿用既有回退契约），不截断
    [Fact]
    public void OptionLinesOnly_NoDialogueResult()
    {
        var lines = new List<string> { "% Option A", "% Option B" };

        LlmDialogueService.SelectFirstDialogueTurn(lines, out var dialogue, out var responses, out var truncated);

        Assert.False(truncated);
        Assert.Empty(dialogue);
        Assert.Equal(2, responses.Count);
    }

    // 既有回退契约：无 '-' 前缀时，边界前的非 '%' 文本作为对话候选
    [Fact]
    public void NoDashPrefix_NonOptionTextBeforeBoundary_BecomesDialogueFallback()
    {
        var lines = new List<string> { "Hello there!", "% Hi", "% Bye" };

        LlmDialogueService.SelectFirstDialogueTurn(lines, out var dialogue, out var responses, out var truncated);

        Assert.False(truncated);
        Assert.Single(dialogue);
        Assert.Equal("Hello there!", dialogue[0]);
        Assert.Equal(2, responses.Count);
    }

    // 失败路径 1：null 输入 → ArgumentNullException
    [Fact]
    public void NullInput_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() =>
            LlmDialogueService.SelectFirstDialogueTurn(null, out _, out _, out _));
    }

    // 失败路径 2：空输入 → 空对话与空选项，不截断
    [Fact]
    public void EmptyInput_EmptyResultNotTruncated()
    {
        LlmDialogueService.SelectFirstDialogueTurn(new List<string>(), out var dialogue, out var responses, out var truncated);

        Assert.False(truncated);
        Assert.Empty(dialogue);
        Assert.Empty(responses);
    }

    // AC-8（助手层）：选项区之后的非 '-' 杂行不构成第二轮，不置 truncated
    [Fact]
    public void NonDialogueJunkAfterOptions_DoesNotTruncate()
    {
        var lines = new List<string> { "- Dialogue", "% Option A", "junk line", "% Option B" };

        LlmDialogueService.SelectFirstDialogueTurn(lines, out var dialogue, out var responses, out var truncated);

        Assert.False(truncated);
        Assert.Single(dialogue);
        Assert.Equal(2, responses.Count);
    }
}
