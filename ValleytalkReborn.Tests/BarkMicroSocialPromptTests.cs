using System;
using System.Collections.Generic;
using ValleytalkReborn;
using Xunit;

// 与 MoodShockStoreTests / SensoryCooldownStoreTests / ProactiveDialogueManagerTests 共享 Collection
[Collection("EmotionStore")]
public class BarkMicroSocialPromptTests
{
    private static void ResetAll()
    {
        ProactiveDialogueManager.ClearAll();
        ProactiveDialogueManager.DayProvider = () => 100;
        ProactiveDialogueManager.NowGameTimeProvider = () => 600;
        ProactiveDialogueManager.PlayerMovingProvider = () => false;
        ProactiveDialogueManager.HeartsProvider = _ => 0;
        ProactiveDialogueManager.MidChanceRollProvider = () => 0.5;
        ProactiveDialogueManager.EnabledProvider = () => true;
        ProactiveDialogueManager.MidChanceProvider = () => 0.35f;
        SensoryClassifier.BucketProvider = _ => new List<PerceptionEntry>();
        SensoryCooldownStore.ClearAll();
    }

    // ── 验收 2a：BuildMicroSocialTaskBlock 含固定标题与 3 条行说明 ──

    [Fact]
    public void UT01_TaskBlock_Zh_ContainsTitleAndLines()
    {
        var block = BarkPromptBuilder.BuildMicroSocialTaskBlock(isZh: true);

        Assert.Contains("### [当前任务：擦肩而过的微社交]", block);
        Assert.Contains("[0]", block);
        Assert.Contains("[1]", block);
        Assert.Contains("[2]", block);
        Assert.Contains("{sensoryLine}", block);
        Assert.Contains("输出纯 JSON 数组", block);
    }

    [Fact]
    public void UT02_TaskBlock_En_ContainsTitleAndLines()
    {
        var block = BarkPromptBuilder.BuildMicroSocialTaskBlock(isZh: false);

        Assert.Contains("### [CURRENT TASK: A PASSING GLANCE]", block);
        Assert.Contains("[0]", block);
        Assert.Contains("[1]", block);
        Assert.Contains("[2]", block);
        Assert.Contains("{sensoryLine}", block);
        Assert.Contains("Output a plain JSON array", block);
    }

    // ── 验收 2b：BuildMicroSocialUserPrompt 段落顺序与空段处理 ──

    [Fact]
    public void UT03_UserPrompt_Zh_SectionOrder()
    {
        var prompt = BarkPromptBuilder.BuildMicroSocialUserPrompt(
            rawPersona: "我是阿比盖尔",
            farmerNote: "农夫的朋友",
            ambientScene: "阳光明媚",
            sensoryLine: "LewisShorts",
            isZh: true);

        int idxPersona = prompt.IndexOf("### [你是谁]");
        int idxFarmer = prompt.IndexOf("### [已知人物底色]");
        int idxScene = prompt.IndexOf("### [此刻]");
        int idxTask = prompt.IndexOf("### [当前任务：擦肩而过的微社交]");

        Assert.True(idxPersona >= 0);
        Assert.True(idxFarmer > idxPersona);
        Assert.True(idxScene > idxFarmer);
        Assert.True(idxTask > idxScene);
    }

    [Fact]
    public void UT04_UserPrompt_NullFarmerNote_NoEmptySection()
    {
        var prompt = BarkPromptBuilder.BuildMicroSocialUserPrompt(
            rawPersona: "我是阿比盖尔",
            farmerNote: null,
            ambientScene: "阳光明媚",
            sensoryLine: "LewisShorts",
            isZh: true);

        Assert.DoesNotContain("### [已知人物底色]", prompt);
        Assert.Contains("### [你是谁]", prompt);
        Assert.Contains("### [此刻]", prompt);
    }

    [Fact]
    public void UT05_UserPrompt_NullAmbientScene_NoEmptySection()
    {
        var prompt = BarkPromptBuilder.BuildMicroSocialUserPrompt(
            rawPersona: "我是阿比盖尔",
            farmerNote: "农夫的朋友",
            ambientScene: null,
            sensoryLine: "LewisShorts",
            isZh: true);

        Assert.DoesNotContain("### [此刻]", prompt);
        Assert.Contains("### [你是谁]", prompt);
        Assert.Contains("### [已知人物底色]", prompt);
    }

    [Fact]
    public void UT06_UserPrompt_SensoryLineInTaskBlock()
    {
        var prompt = BarkPromptBuilder.BuildMicroSocialUserPrompt(
            rawPersona: "我是阿比盖尔",
            farmerNote: "农夫的朋友",
            ambientScene: "阳光明媚",
            sensoryLine: "PlayerSpecialOutfit_Shorts",
            isZh: true);

        Assert.Contains("PlayerSpecialOutfit_Shorts", prompt);
        // {sensoryLine} 占位符应被替换，不应残留
        Assert.DoesNotContain("{sensoryLine}", prompt);
    }

    // ── 验收 2c：zh/en 输出语言分支正确 ──

    [Fact]
    public void UT07_UserPrompt_En_SectionTitles()
    {
        var prompt = BarkPromptBuilder.BuildMicroSocialUserPrompt(
            rawPersona: "I am Abigail",
            farmerNote: "A friend of the farmer",
            ambientScene: "Sunny day",
            sensoryLine: "LewisShorts",
            isZh: false);

        Assert.Contains("### [WHO YOU ARE]", prompt);
        Assert.Contains("### [KNOWN CHARACTER]", prompt);
        Assert.Contains("### [RIGHT NOW]", prompt);
        Assert.Contains("### [CURRENT TASK: A PASSING GLANCE]", prompt);
    }

    [Fact]
    public void UT08_UserPrompt_En_NoZhTitles()
    {
        var prompt = BarkPromptBuilder.BuildMicroSocialUserPrompt(
            rawPersona: "I am Abigail",
            farmerNote: "A friend of the farmer",
            ambientScene: "Sunny day",
            sensoryLine: "LewisShorts",
            isZh: false);

        Assert.DoesNotContain("### [你是谁]", prompt);
        Assert.DoesNotContain("### [已知人物底色]", prompt);
        Assert.DoesNotContain("### [此刻]", prompt);
    }
}
