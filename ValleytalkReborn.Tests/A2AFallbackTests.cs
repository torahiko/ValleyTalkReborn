// A2AFallbackTests.cs
// VT-AMB-02-A2A-Natural-Conversation-Prompt — A2A fallback 与数量契约测试。
// 覆盖：
//   1) BuildFallbackScript 在 2/3/4 人下的数量契约不变、speaker 全部命中参与者
//      集合、台词非空（含 <2 人的空数组行为）；
//   2) fallback 路径不再引用 AmbientBarkModule.GetRandomFallbackPublic，
//      且 AmbientBarkModule 内该公开包装已删除；
//   3) A2AScriptValidator 数量判定改为 validCount != expectedCount 即失败
//      （不足与超出同判失败，MaxLineLength 与 speaker 白名单未放宽）；
//   4) A2A Prompt 的目标条数由 targetLineCount 注入，"4~6" 字面量不复存在，
//      固定结构要求已移除。
// 纯内存测试：无网络、无游戏实例（A2ASessionManager 的四个协作者传 null —— 
// BuildFallbackScript 不触碰它们）。

#nullable disable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using StardewValley;
using ValleytalkReborn;
using Xunit;

namespace ValleytalkReborn.Tests;

[Collection("StaticGlobalStateCollection")]
public class A2AFallbackTests : IDisposable
{
    // A2ASessionManager 常量镜像：值变化即视为契约变更，测试会失败提示同步。
    private const int RoundsTwoPerson = 6;    // A2A_ROUNDS_TWO_PERSON
    private const int RoundsPerSpeaker = 2;   // A2A_ROUNDS_PER_SPEAKER

    private readonly LocalizedContentManager.LanguageCode _originalLanguageCode;

    public A2AFallbackTests()
    {
        _originalLanguageCode = LocalizedContentManager.CurrentLanguageCode;
        LocalizedContentManager.CurrentLanguageCode = LocalizedContentManager.LanguageCode.zh;
    }

    public void Dispose()
    {
        LocalizedContentManager.CurrentLanguageCode = _originalLanguageCode;
    }

    // ── 验收 10：fallback 数量契约（2/3/4 人）──

    [Fact]
    public void UT01_BuildFallbackScript_TwoParticipants_ReturnsSixLines()
    {
        var lines = NewManager().BuildFallbackScript(new List<string> { "Alex", "Haley" }, true);

        Assert.Equal(RoundsTwoPerson, lines.Length);
    }

    [Fact]
    public void UT02_BuildFallbackScript_ThreeParticipants_ReturnsSixLines()
    {
        var lines = NewManager().BuildFallbackScript(new List<string> { "Alex", "Haley", "Sam" }, true);

        Assert.Equal(3 * RoundsPerSpeaker, lines.Length);
    }

    [Fact]
    public void UT03_BuildFallbackScript_FourParticipants_ReturnsEightLines()
    {
        var lines = NewManager().BuildFallbackScript(new List<string> { "Alex", "Haley", "Sam", "Elliott" }, false);

        Assert.Equal(4 * RoundsPerSpeaker, lines.Length);
    }

    // ── 验收 10：speaker 命中参与者集合、台词非空 ──

    [Theory]
    [InlineData(2, true)]
    [InlineData(3, true)]
    [InlineData(4, false)]
    public void UT04_BuildFallbackScript_SpeakersAreParticipantsAndLinesNonEmpty(int count, bool isChinese)
    {
        var names = new List<string> { "Alex", "Haley", "Sam", "Elliott" }.Take(count).ToList();
        var lines = NewManager().BuildFallbackScript(names, isChinese);

        Assert.NotEmpty(lines);
        foreach (var line in lines)
        {
            Assert.Contains(line.SpeakerName, names);
            Assert.False(string.IsNullOrWhiteSpace(line.Line));
        }
    }

    // ── 失败路径 7：参与者少于 2 人 → 空数组，不伪造单人会话 ──

    [Fact]
    public void UT05_BuildFallbackScript_FewerThanTwoNames_ReturnsEmpty()
    {
        Assert.Empty(NewManager().BuildFallbackScript(new List<string> { "Alex" }, true));
        Assert.Empty(NewManager().BuildFallbackScript(new List<string>(), true));
        Assert.Empty(NewManager().BuildFallbackScript(null, true));
    }

    // ── 验收 6：fallback 不再引用 AmbientBarkModule.GetRandomFallbackPublic ──

    [Fact]
    public void UT06_FallbackPath_DoesNotReferenceAmbientBarkFallbackPool()
    {
        string sessionSource = ReadSourceOrFail("src", "Dialogue", "Ambient", "A2A", "A2ASessionManager.cs");
        string barkSource = ReadSourceOrFail("src", "Dialogue", "Ambient", "Bark", "AmbientBarkModule.cs");

        Assert.DoesNotContain("GetRandomFallbackPublic", sessionSource);
        Assert.DoesNotContain("AmbientBarkModule.GetRandomFallback", sessionSource);

        // GetRandomFallbackPublic 已删除；GetRandomFallback（PushFallbackLocked 依赖）保留
        Assert.DoesNotContain("GetRandomFallbackPublic", barkSource);
    }

    // ── 验收 5：A2AScriptValidator 数量契约 ──

    [Fact]
    public void UT07_TryValidate_ExactCount_ReturnsTrue()
    {
        var names = new List<string> { "Alex", "Haley" };
        bool ok = A2AScriptValidator.TryValidate(
            BuildLines(6, names), names, 6, out var validLines);

        Assert.True(ok);
        Assert.Equal(6, validLines.Length);
    }

    [Fact]
    public void UT08_TryValidate_FewerThanExpected_ReturnsFalse()
    {
        var names = new List<string> { "Alex", "Haley" };
        bool ok = A2AScriptValidator.TryValidate(
            BuildLines(5, names), names, 6, out var validLines);

        Assert.False(ok);
        Assert.Null(validLines);
    }

    [Fact]
    public void UT09_TryValidate_MoreThanExpected_ReturnsFalse()
    {
        var names = new List<string> { "Alex", "Haley" };
        bool ok = A2AScriptValidator.TryValidate(
            BuildLines(8, names), names, 6, out var validLines);

        Assert.False(ok);
        Assert.Null(validLines);
    }

    [Fact]
    public void UT10_TryValidate_LineExceedsMaxLength_ReturnsFalse()
    {
        var names = new List<string> { "Alex", "Haley" };
        var lines = BuildLines(6, names);
        lines[2] = new DialogueModels.A2ALine { SpeakerName = "Alex", Line = new string('啊', 201) };

        bool ok = A2AScriptValidator.TryValidate(lines, names, 6, out _);

        Assert.False(ok);
    }

    [Fact]
    public void UT11_TryValidate_UnknownSpeaker_ReturnsFalse()
    {
        var names = new List<string> { "Alex", "Haley" };
        var lines = BuildLines(6, names);
        lines[1] = new DialogueModels.A2ALine { SpeakerName = "Sam", Line = "一句台词" };

        bool ok = A2AScriptValidator.TryValidate(lines, names, 6, out _);

        Assert.False(ok);
    }

    // ── 验收 3/4：Prompt 目标条数与 targetLineCount 同源，无 "4~6" 字面量 ──

    [Fact]
    public void UT12_ConversationGuidance_Zh_UsesTargetCountAndNoFixedStructure()
    {
        string text = InvokePrivateStatic("BuildA2AConversationGuidance", "氛围", "单句口语（10~25字）", true, 6);

        Assert.Contains("编写 6 条", text);
        Assert.Contains("包含 6 个对象", text);
        Assert.Contains("不设固定结构", text);
        Assert.DoesNotContain("4~6", text);
        Assert.DoesNotContain("4–6", text);
    }

    [Fact]
    public void UT13_ConversationGuidance_En_UsesTargetCountAndNoFixedStructure()
    {
        string text = InvokePrivateStatic("BuildA2AConversationGuidance", "atmosphere", "snappy spoken lines", false, 8);

        Assert.Contains("write 8 flowing", text);
        Assert.Contains("array of 8 objects", text);
        Assert.Contains("No fixed shape", text);
        Assert.DoesNotContain("4–6", text);
        Assert.DoesNotContain("4~6", text);
    }

    [Fact]
    public void UT14_FinalRules_UsesTargetCount()
    {
        string zh = InvokePrivateStatic("BuildA2AFinalRules", "单句口语（10~25字）", true, 6);
        string en = InvokePrivateStatic("BuildA2AFinalRules", "snappy spoken lines", false, 6);

        Assert.Contains("总条数 6 条", zh);
        Assert.DoesNotContain("4~6", zh);
        Assert.Contains("Total 6 lines", en);
        Assert.DoesNotContain("4–6", en);
    }

    // ── 验收 1/2：固定冬日示例块已删除（方法不存在 + 源码无残留）──

    [Fact]
    public void UT15_ExampleBlock_Removed()
    {
        var method = typeof(A2APromptBuilder).GetMethod(
            "BuildA2AExampleBlock",
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.Null(method);

        string source = ReadSourceOrFail("src", "Dialogue", "Ambient", "A2A", "A2APromptBuilder.cs");

        Assert.DoesNotContain("BuildA2AExampleBlock", source);
        Assert.DoesNotContain("窗台", source);
        Assert.DoesNotContain("Gloves are in the basket", source);
    }

    // ── 助手 ──

    private static A2ASessionManager NewManager() => new A2ASessionManager(null, null, null, null);

    private static DialogueModels.A2ALine[] BuildLines(int count, List<string> names)
    {
        var lines = new DialogueModels.A2ALine[count];
        for (int i = 0; i < count; i++)
        {
            lines[i] = new DialogueModels.A2ALine
            {
                SpeakerName = names[i % names.Count],
                Line = $"第 {i + 1} 句台词"
            };
        }
        return lines;
    }

    private static string InvokePrivateStatic(string methodName, params object[] args)
    {
        var method = typeof(A2APromptBuilder).GetMethod(
            methodName,
            BindingFlags.NonPublic | BindingFlags.Static);

        Assert.True(method != null, $"{methodName} 未找到（签名可能已变更）");
        return (string)method.Invoke(null, args);
    }

    /// <summary>
    /// 沿用 CommunityChoreLedgerTests / AsyncBuilderNarrationOverlayTests 的向上查找
    /// 模式定位仓库内源文件，用于源码级契约断言。
    /// </summary>
    private static string ReadSourceOrFail(params string[] relativeParts)
    {
        var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
        for (int i = 0; i < 12; i++)
        {
            var candidate = Path.Combine(dir.FullName, Path.Combine(relativeParts));
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            if (dir.Parent == null) break;
            dir = dir.Parent;
        }

        Assert.True(false, $"{Path.Combine(relativeParts)} not found. Searched upward from {AppDomain.CurrentDomain.BaseDirectory}.");
        return null;
    }
}
