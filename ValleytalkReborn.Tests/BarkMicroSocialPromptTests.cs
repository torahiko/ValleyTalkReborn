#nullable disable

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

    // ── 验收 2a：BuildMicroSocialTaskBlock 含固定标题、不再含固定三段式角色 ──

    [Fact]
    public void UT01_TaskBlock_Zh_Sensory_ContainsTitleAndNoFixedRoles()
    {
        var block = BarkPromptBuilder.BuildMicroSocialTaskBlock(isZh: true, sensoryTriggered: true);

        Assert.Contains("### [当前任务：擦肩而过的微社交]", block);
        Assert.Contains("{sensoryLine}", block);
        Assert.Contains("输出纯 JSON 字符串数组", block);
        Assert.Contains("1 至 2 条", block);
        // 固定三段式角色语义已移除
        Assert.DoesNotContain("[0]", block);
        Assert.DoesNotContain("[1]", block);
        Assert.DoesNotContain("[2]", block);
        Assert.DoesNotContain("恰好 3 条", block);
        Assert.DoesNotContain("EXACTLY 3", block);
    }

    [Fact]
    public void UT02_TaskBlock_En_Sensory_ContainsTitleAndNoFixedRoles()
    {
        var block = BarkPromptBuilder.BuildMicroSocialTaskBlock(isZh: false, sensoryTriggered: true);

        Assert.Contains("### [CURRENT TASK: A PASSING GLANCE]", block);
        Assert.Contains("{sensoryLine}", block);
        Assert.Contains("Output a plain JSON array", block);
        Assert.Contains("1 to 2 lines", block);
        Assert.DoesNotContain("[0]", block);
        Assert.DoesNotContain("[1]", block);
        Assert.DoesNotContain("[2]", block);
        Assert.DoesNotContain("EXACTLY 3", block);
        Assert.DoesNotContain("恰好 3 条", block);
    }

    // ── 验收 3a：关系变体（zh/en）──

    [Fact]
    public void UT03_TaskBlock_Zh_Relation_NoSensoryLine()
    {
        var block = BarkPromptBuilder.BuildMicroSocialTaskBlock(isZh: true, sensoryTriggered: false);

        Assert.Contains("### [当前任务：擦肩而过的微社交]", block);
        Assert.Contains("熟人", block);
        Assert.Contains("输出纯 JSON 字符串数组", block);
        Assert.Contains("1 至 2 条", block);
        Assert.DoesNotContain("[0]", block);
        Assert.DoesNotContain("[1]", block);
        Assert.DoesNotContain("[2]", block);
        Assert.DoesNotContain("恰好 3 条", block);
        Assert.DoesNotContain("{sensoryLine}", block);
        Assert.DoesNotContain("异样", block);
    }

    [Fact]
    public void UT04_TaskBlock_En_Relation_NoSensoryLine()
    {
        var block = BarkPromptBuilder.BuildMicroSocialTaskBlock(isZh: false, sensoryTriggered: false);

        Assert.Contains("### [CURRENT TASK: A PASSING GLANCE]", block);
        Assert.Contains("familiar", block);
        Assert.Contains("Output a plain JSON array", block);
        Assert.Contains("1 to 2 lines", block);
        Assert.DoesNotContain("[0]", block);
        Assert.DoesNotContain("[1]", block);
        Assert.DoesNotContain("[2]", block);
        Assert.DoesNotContain("EXACTLY 3", block);
        Assert.DoesNotContain("{sensoryLine}", block);
        Assert.DoesNotContain("What caught your eye", block);
    }

    // ── 验收 1/2：四分支统一不得残留三段式约束 ──

    [Fact]
    public void UT04b_TaskBlock_AllBranches_NoThreeBeatContract()
    {
        foreach (var isZh in new[] { true, false })
        {
            foreach (var sensory in new[] { true, false })
            {
                var block = BarkPromptBuilder.BuildMicroSocialTaskBlock(isZh, sensory);

                Assert.DoesNotContain("第 [0] 条", block);
                Assert.DoesNotContain("Line [0]", block);
                Assert.DoesNotContain("恰好 3 条", block);
                Assert.DoesNotContain("EXACTLY 3", block);
                // 1~2 条的可选延续语义
                Assert.Contains(isZh ? "1 至 2 条" : "1 to 2 lines", block);
            }
        }
    }

    // ── 验收 3b：BuildMicroSocialUserPrompt(sensoryTriggered=false) ──

    [Fact]
    public void UT05_UserPrompt_Relation_SectionOrder()
    {
        var prompt = BarkPromptBuilder.BuildMicroSocialUserPrompt(
            rawPersona: "我是阿比盖尔",
            farmerNote: "农夫的朋友",
            ambientScene: "阳光明媚",
            sensoryLine: null,
            sensoryTriggered: false,
            isZh: true);

        int idxPersona = prompt.IndexOf("### [你是谁]");
        int idxFarmer = prompt.IndexOf("### [已知人物底色]");
        int idxScene = prompt.IndexOf("### [此刻]");
        int idxTask = prompt.IndexOf("### [当前任务：擦肩而过的微社交]");

        Assert.True(idxPersona >= 0);
        Assert.True(idxFarmer > idxPersona);
        Assert.True(idxScene > idxFarmer);
        Assert.True(idxTask > idxScene);
        Assert.DoesNotContain("{sensoryLine}", prompt);
        Assert.Contains("熟人", prompt);
    }

    [Fact]
    public void UT06_UserPrompt_Relation_En()
    {
        var prompt = BarkPromptBuilder.BuildMicroSocialUserPrompt(
            rawPersona: "I am Abigail",
            farmerNote: "A friend of the farmer",
            ambientScene: "Sunny day",
            sensoryLine: null,
            sensoryTriggered: false,
            isZh: false);

        Assert.Contains("### [WHO YOU ARE]", prompt);
        Assert.Contains("### [KNOWN CHARACTER]", prompt);
        Assert.Contains("### [RIGHT NOW]", prompt);
        Assert.Contains("### [CURRENT TASK: A PASSING GLANCE]", prompt);
        Assert.Contains("familiar", prompt);
        Assert.DoesNotContain("{sensoryLine}", prompt);
    }

    // ── 验收 3c：感官路径回归（占位符与首句长度带保留）──

    [Fact]
    public void UT07_SensoryPath_TaskBlock_SensoryAnchorAndLengthBand()
    {
        var block = BarkPromptBuilder.BuildMicroSocialTaskBlock(isZh: true, sensoryTriggered: true);

        Assert.Contains("你注意到的异样：{sensoryLine}", block);
        Assert.Contains("6~15 个汉字", block);
        Assert.Contains("可选", block);
        Assert.DoesNotContain("第 [0] 条", block);
    }

    [Fact]
    public void UT07b_SensoryPath_En_TaskBlock_LengthBandAndOptionalSecond()
    {
        var block = BarkPromptBuilder.BuildMicroSocialTaskBlock(isZh: false, sensoryTriggered: true);

        Assert.Contains("What caught your eye: {sensoryLine}", block);
        Assert.Contains("3-10 words", block);
        Assert.Contains("(optional)", block);
        Assert.DoesNotContain("Line [0]", block);
    }

    [Fact]
    public void UT08_SensoryPath_UserPrompt_SensoryLineReplaced()
    {
        var prompt = BarkPromptBuilder.BuildMicroSocialUserPrompt(
            rawPersona: "我是阿比盖尔",
            farmerNote: "农夫的朋友",
            ambientScene: "阳光明媚",
            sensoryLine: "PlayerSpecialOutfit_Shorts",
            sensoryTriggered: true,
            isZh: true);

        Assert.Contains("PlayerSpecialOutfit_Shorts", prompt);
        Assert.DoesNotContain("{sensoryLine}", prompt);
        Assert.Contains("### [当前任务：擦肩而过的微社交]", prompt);
        Assert.DoesNotContain("恰好 3 条", prompt);
    }

    // ── 验收 2b：BuildMicroSocialUserPrompt 段落顺序与空段处理（感官路径）──

    [Fact]
    public void UT09_UserPrompt_Zh_SectionOrder()
    {
        var prompt = BarkPromptBuilder.BuildMicroSocialUserPrompt(
            rawPersona: "我是阿比盖尔",
            farmerNote: "农夫的朋友",
            ambientScene: "阳光明媚",
            sensoryLine: "LewisShorts",
            sensoryTriggered: true,
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
    public void UT10_UserPrompt_NullFarmerNote_NoEmptySection()
    {
        var prompt = BarkPromptBuilder.BuildMicroSocialUserPrompt(
            rawPersona: "我是阿比盖尔",
            farmerNote: null,
            ambientScene: "阳光明媚",
            sensoryLine: "LewisShorts",
            sensoryTriggered: true,
            isZh: true);

        Assert.DoesNotContain("### [已知人物底色]", prompt);
        Assert.Contains("### [你是谁]", prompt);
        Assert.Contains("### [此刻]", prompt);
    }

    [Fact]
    public void UT11_UserPrompt_NullAmbientScene_NoEmptySection()
    {
        var prompt = BarkPromptBuilder.BuildMicroSocialUserPrompt(
            rawPersona: "我是阿比盖尔",
            farmerNote: "农夫的朋友",
            ambientScene: null,
            sensoryLine: "LewisShorts",
            sensoryTriggered: true,
            isZh: true);

        Assert.DoesNotContain("### [此刻]", prompt);
        Assert.Contains("### [你是谁]", prompt);
        Assert.Contains("### [已知人物底色]", prompt);
    }

    [Fact]
    public void UT12_UserPrompt_SensoryLineInTaskBlock()
    {
        var prompt = BarkPromptBuilder.BuildMicroSocialUserPrompt(
            rawPersona: "我是阿比盖尔",
            farmerNote: "农夫的朋友",
            ambientScene: "阳光明媚",
            sensoryLine: "PlayerSpecialOutfit_Shorts",
            sensoryTriggered: true,
            isZh: true);

        Assert.Contains("PlayerSpecialOutfit_Shorts", prompt);
        // {sensoryLine} 占位符应被替换，不应残留
        Assert.DoesNotContain("{sensoryLine}", prompt);
    }

    // ── 验收 2c：zh/en 输出语言分支正确 ──

    [Fact]
    public void UT13_UserPrompt_En_SectionTitles()
    {
        var prompt = BarkPromptBuilder.BuildMicroSocialUserPrompt(
            rawPersona: "I am Abigail",
            farmerNote: "A friend of the farmer",
            ambientScene: "Sunny day",
            sensoryLine: "LewisShorts",
            sensoryTriggered: true,
            isZh: false);

        Assert.Contains("### [WHO YOU ARE]", prompt);
        Assert.Contains("### [KNOWN CHARACTER]", prompt);
        Assert.Contains("### [RIGHT NOW]", prompt);
        Assert.Contains("### [CURRENT TASK: A PASSING GLANCE]", prompt);
    }

    [Fact]
    public void UT14_UserPrompt_En_NoZhTitles()
    {
        var prompt = BarkPromptBuilder.BuildMicroSocialUserPrompt(
            rawPersona: "I am Abigail",
            farmerNote: "A friend of the farmer",
            ambientScene: "Sunny day",
            sensoryLine: "LewisShorts",
            sensoryTriggered: true,
            isZh: false);

        Assert.DoesNotContain("### [你是谁]", prompt);
        Assert.DoesNotContain("### [已知人物底色]", prompt);
        Assert.DoesNotContain("### [此刻]", prompt);
    }
}
