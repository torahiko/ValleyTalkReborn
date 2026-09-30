// AsyncBuilderNarrationOverlayTests.cs
// VT-Emotion-Narration-Remove-PostGeneration-Overlay — AsyncBuilder.PerformGeneration
// 生成后 narration 前置移除的源码契约测试。
// PerformGeneration 为 private 且深度耦合游戏态（Game1/NPC/DialogueBox），
// 无头环境无法直接驱动，故对源文件做契约断言：
// 覆盖：display 路径不再引用 NarrationLine / 前置改写首页文本 / 叙事门副作用；
//       VT-STREAM-03 起流式对白框取代原版占位框与 DrawDialogue 顶替；
//       rawText 捕获与第 ④⑤ 步业务管道逐字保留。
// 纯内存测试：无游戏实例、无 Provider、无网络。

using System;
using System.IO;
using Xunit;

namespace ValleytalkReborn.Tests;

public class AsyncBuilderNarrationOverlayTests
{
    // ── 定位仓库内 AsyncBuilder.cs 源文件（沿用 CommunityChoreLedgerTests 向上查找模式）──

    private static string ReadAsyncBuilderSource()
    {
        var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
        for (int i = 0; i < 12; i++)
        {
            var src = Path.Combine(dir.FullName, "src", "LLM", "Prompts", "Generation", "AsyncBuilder.cs");
            if (File.Exists(src)) return File.ReadAllText(src);
            if (dir.Parent == null) break;
            dir = dir.Parent;
        }
        return null;
    }

    private static string SourceOrFail()
    {
        var source = ReadAsyncBuilderSource();
        Assert.True(source != null,
            $"AsyncBuilder.cs not found. Searched upward from {AppDomain.CurrentDomain.BaseDirectory}.");
        return source;
    }

    // AC：display 路径不再编译/引用 resolver narration，不再前置改写首页文本，
    // 也不再写叙事门副作用标记（EmotionNarrationDoneToday 由 DialogueBuilder 每日重置）。
    [Fact]
    public void PerformGeneration_DoesNotPrependNarrationLineToDisplayedDialogue()
    {
        var source = SourceOrFail();
        Assert.DoesNotContain("NarrationLine", source);
        Assert.DoesNotContain("dialogues[0].Text", source);
        Assert.DoesNotContain("EmotionNarrationDoneToday", source);
    }

    // AC：VT-STREAM-03 流式架构不变量 —— 流式对白框取代原版占位框，
    // 成功路径不得再用 Game1.DrawDialogue 顶替流式界面。
    [Fact]
    public void PerformGeneration_UsesStreamingBoxWithoutDrawDialogueOverride()
    {
        var source = SourceOrFail();

        Assert.Contains("AiStreamingDialogueBox", source);
        Assert.DoesNotContain("Game1.DrawDialogue(newDialogue);", source);
        Assert.DoesNotContain("new DialogueBox(\"   \")", source);
    }

    // AC：展示层换壳不得牵连业务管道 —— rawText 捕获与第 ④ 步（历史/偷听）、
    // 第 ⑤ 步（情绪反馈结算与冷落衰减提交）必须与前序票逐字一致。
    [Fact]
    public void PerformGeneration_PreservesRawTextCaptureAndHistoryPipeline()
    {
        var source = SourceOrFail();

        // ① rawText 捕获不变，保证入库文本与移除 narration 之前一致。
        Assert.Contains("string rawText = string.Join(\" \", newDialogue.dialogues.Select(d => d.Text));", source);

        // ④ 历史记录与偷听广播
        Assert.Contains(
            "RecentConversationTracker.RecordResponse(npc.Name, cleanResponseText, lastPlayerChoice);",
            source);
        Assert.Contains(
            "DialogueHistoryManager.Instance.RecordGiftReaction(npc.Name, cleanResponseText);",
            source);
        Assert.Contains(
            "DialogueHistoryManager.Instance.RecordNpcDialogue(npc.Name, cleanResponseText, \"conversation\");",
            source);
        Assert.Contains("DialogueHistoryManager.Instance.RecordSystemEvent(", source);

        // ⑤ 情绪反馈结算与冷落衰减提交
        Assert.Contains(
            "DialogueFeedbackService.EvaluateDialogueFeedback(character, character.PendingEmotion.Value, portraitCode);",
            source);
        Assert.Contains("EmotionalStateResolver.NotifyDialogueCommitted(character);", source);
    }
}
