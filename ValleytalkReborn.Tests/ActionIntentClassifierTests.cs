// ActionIntentClassifierTests.cs
// ═══════════════════════════════════════════════════════════════════════════
// 纯意图分类器单元测试（票 CTX-003）
// ═══════════════════════════════════════════════════════════════════════════
//
// 直接测试抽取后的纯函数（internal，经 InternalsVisibleTo("ValleytalkReborn.Tests")
// 可访问），不依赖 Game1 / NPC / 任何 Manager，无需游戏世界。
//
// 覆盖：
//   1. ActionIntentClassifier.Detect 的动作优先级与否定护栏；
//   2. ActionIntentClassifier.TryDetectGoto 的触发与疑问句抑制；
//   3. DialogueIntentClassifier 的问候 / 告别 / 否定词判定。
// ═══════════════════════════════════════════════════════════════════════════

using ValleytalkReborn;
using Xunit;

namespace ValleytalkReborn.Tests;

public class ActionIntentClassifierTests
{
    // ─────────────────────────────────────────────────────
    // Detect：动作优先级
    // ─────────────────────────────────────────────────────

    [Fact]
    public void Detect_StayHome_BeatsNegationGuard()
    {
        // "不要出门" 同时命中 StayHome 与否定词；StayHome 优先级更高。
        Assert.Equal(ActionTag.StayHome, ActionIntentClassifier.Detect("不要出门"));
    }

    [Fact]
    public void Detect_AllDayFollow_ReturnsAllDayFollow()
    {
        Assert.Equal(ActionTag.AllDayFollow, ActionIntentClassifier.Detect("陪我一整天"));
    }

    [Fact]
    public void Detect_StopFollow_BeatsNegationGuard()
    {
        // "别跟着我" 命中 StopFollow（在否定护栏之前），不应被否定拦截。
        Assert.Equal(ActionTag.StopFollow, ActionIntentClassifier.Detect("别跟着我"));
    }

    [Fact]
    public void Detect_NegatedCommand_SuppressesFollow()
    {
        Assert.Equal(ActionTag.None, ActionIntentClassifier.Detect("不要亲我"));
    }

    [Fact]
    public void Detect_NegatedDirectional_SuppressesForward()
    {
        Assert.Equal(ActionTag.None, ActionIntentClassifier.Detect("别往前走"));
    }

    [Fact]
    public void Detect_Follow_ReturnsFollow()
    {
        Assert.Equal(ActionTag.Follow, ActionIntentClassifier.Detect("跟着我"));
    }

    [Fact]
    public void Detect_EnglishFollow_CaseInsensitive()
    {
        Assert.Equal(ActionTag.Follow, ActionIntentClassifier.Detect("Follow me"));
    }

    [Fact]
    public void Detect_StepForward()
    {
        Assert.Equal(ActionTag.StepForward, ActionIntentClassifier.Detect("向前走"));
    }

    [Fact]
    public void Detect_StepBackward()
    {
        Assert.Equal(ActionTag.StepBackward, ActionIntentClassifier.Detect("向后退"));
    }

    [Fact]
    public void Detect_StepLeft()
    {
        Assert.Equal(ActionTag.StepLeft, ActionIntentClassifier.Detect("向左走"));
    }

    [Fact]
    public void Detect_StepRight()
    {
        Assert.Equal(ActionTag.StepRight, ActionIntentClassifier.Detect("向右走"));
    }

    [Fact]
    public void Detect_StepUp()
    {
        Assert.Equal(ActionTag.StepUp, ActionIntentClassifier.Detect("向上走"));
    }

    [Fact]
    public void Detect_StepDown()
    {
        Assert.Equal(ActionTag.StepDown, ActionIntentClassifier.Detect("向下走"));
    }

    [Fact]
    public void Detect_NoAction_ReturnsNone()
    {
        Assert.Equal(ActionTag.None, ActionIntentClassifier.Detect("今天天气不错"));
    }

    [Fact]
    public void Detect_NullOrWhitespace_ReturnsNone()
    {
        Assert.Equal(ActionTag.None, ActionIntentClassifier.Detect(null));
        Assert.Equal(ActionTag.None, ActionIntentClassifier.Detect(""));
        Assert.Equal(ActionTag.None, ActionIntentClassifier.Detect("   "));
    }

    // ─────────────────────────────────────────────────────
    // TryDetectGoto：触发与疑问句抑制
    // ─────────────────────────────────────────────────────

    [Fact]
    public void TryDetectGoto_Command_Detected()
    {
        bool detected = ActionIntentClassifier.TryDetectGoto(
            "你能去那里", out string intentText);

        Assert.True(detected);
        Assert.Equal("你能去那里", intentText);
    }

    [Fact]
    public void TryDetectGoto_EnglishCommand_Detected()
    {
        bool detected = ActionIntentClassifier.TryDetectGoto(
            "can you go to the store", out string intentText);

        Assert.True(detected);
        Assert.Equal("can you go to the store", intentText);
    }

    [Fact]
    public void TryDetectGoto_ChineseQuestion_Suppressed()
    {
        Assert.False(ActionIntentClassifier.TryDetectGoto("你能去那里吗？", out _));
        Assert.False(ActionIntentClassifier.TryDetectGoto("你能去那里吗", out _));
        Assert.False(ActionIntentClassifier.TryDetectGoto("你能去那里呢", out _));
    }

    [Fact]
    public void TryDetectGoto_EnglishQuestion_Suppressed()
    {
        Assert.False(ActionIntentClassifier.TryDetectGoto(
            "can you go to the store?", out _));
    }

    [Fact]
    public void TryDetectGoto_NoTrigger_ReturnsFalse()
    {
        Assert.False(ActionIntentClassifier.TryDetectGoto("你好", out _));
    }

    [Fact]
    public void TryDetectGoto_NullOrWhitespace_ReturnsFalseAndEmpty()
    {
        Assert.False(ActionIntentClassifier.TryDetectGoto(null, out string text1));
        Assert.Equal(string.Empty, text1);

        Assert.False(ActionIntentClassifier.TryDetectGoto("", out string text2));
        Assert.Equal(string.Empty, text2);

        Assert.False(ActionIntentClassifier.TryDetectGoto("   ", out string text3));
        Assert.Equal(string.Empty, text3);
    }

    // ─────────────────────────────────────────────────────
    // DialogueIntentClassifier：问候 / 告别 / 否定词
    // ─────────────────────────────────────────────────────

    [Fact]
    public void IsSimpleGreeting_English_True()
    {
        Assert.True(DialogueIntentClassifier.IsSimpleGreeting("hi"));
    }

    [Fact]
    public void IsSimpleGreeting_Chinese_True()
    {
        Assert.True(DialogueIntentClassifier.IsSimpleGreeting("你好"));
    }

    [Fact]
    public void IsSimpleGreeting_NotGreeting_False()
    {
        Assert.False(DialogueIntentClassifier.IsSimpleGreeting("今天天气不错"));
    }

    [Fact]
    public void IsFarewell_English_True()
    {
        Assert.True(DialogueIntentClassifier.IsFarewell("bye"));
    }

    [Fact]
    public void IsFarewell_NotFarewell_False()
    {
        Assert.False(DialogueIntentClassifier.IsFarewell("hi"));
    }

    [Fact]
    public void IsNegatedCommand_Chinese_True()
    {
        Assert.True(DialogueIntentClassifier.IsNegatedCommand("不要亲我"));
        Assert.True(DialogueIntentClassifier.IsNegatedCommand("别往前走"));
    }

    [Fact]
    public void IsNegatedCommand_English_True()
    {
        Assert.True(DialogueIntentClassifier.IsNegatedCommand("don't kiss me"));
    }

    [Fact]
    public void IsNegatedCommand_Normal_False()
    {
        Assert.False(DialogueIntentClassifier.IsNegatedCommand("亲我"));
        Assert.False(DialogueIntentClassifier.IsNegatedCommand("跟着我"));
    }
}
