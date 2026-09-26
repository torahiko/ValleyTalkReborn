#nullable disable

using ValleytalkReborn;
using Xunit;

public class BioPromptBuilderRefinementTests
{
    // ── 验收 9a：User 段包含逐字草稿与追问，System 包含 base + 追问规则块 ──

    [Fact]
    public void BuildRefinementPrompt_ContainsDraftAndDemand()
    {
        string baseSystem = "你是一名专业的《星露谷物语》NPC 人设编辑助手，正在润色角色模块。";
        string draft = "角色说话时总是低头搓手指，声音很轻。";
        string demand = "让语气更口语化一些，加入方言感。";

        var (system, user) = BioPromptBuilder.BuildRefinementPrompt(baseSystem, draft, demand);

        Assert.Contains(baseSystem, system);
        // 测试环境为 en（LocalizedContentManager 默认），断言 en 段头
        Assert.Contains("[REFINEMENT RULES]", system);
        Assert.Contains("second-pass edit", system);
        Assert.Contains("DO NOT output greetings", system);

        Assert.Contains(draft, user);
        Assert.Contains(demand, user);
        Assert.Contains("[PREVIOUS DRAFT]", user);
        Assert.Contains("[PLAYER REQUEST]", user);
        Assert.Contains("complete revised", user);
    }

    // ── 验收 9b：含换行的多行草稿逐字保留 ──

    [Fact]
    public void BuildRefinementPrompt_PreservesMultilineDraft()
    {
        string baseSystem = "BASE";
        string draft = "第一行：角色外貌描写。\n第二行：角色的语气与节奏。\n  [MANNERISMS] 标志性肢体微动作列表。";
        string demand = "精简第二行";

        var (system, user) = BioPromptBuilder.BuildRefinementPrompt(baseSystem, draft, demand);

        Assert.Contains("第一行：角色外貌描写。", user);
        Assert.Contains("第二行：角色的语气与节奏。", user);
        Assert.Contains("[MANNERISMS] 标志性肢体微动作列表。", user);
        Assert.Contains(draft, user);
    }

    // ── 验收 9c：followUpDemand 为空串时不抛异常且结构完整 ──

    [Fact]
    public void BuildRefinementPrompt_EmptyDemand_IsDeterministic()
    {
        string baseSystem = "BASE";
        string draft = "任意草稿内容";
        string demand = string.Empty;

        var (system, user) = BioPromptBuilder.BuildRefinementPrompt(baseSystem, draft, demand);

        Assert.Contains(baseSystem, system);
        // 测试环境为 en（LocalizedContentManager 默认），断言 en 段头
        Assert.Contains("[REFINEMENT RULES]", system);
        Assert.Contains("second-pass edit", system);
        Assert.Contains(draft, user);
        Assert.Contains("[PLAYER REQUEST]", user);
        Assert.Contains("[PREVIOUS DRAFT]", user);
        Assert.Contains("complete revised", user);
    }
}
