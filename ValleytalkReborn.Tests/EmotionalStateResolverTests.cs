using System.Collections.Generic;
using ValleytalkReborn;
using Xunit;

// 全部驱动 pure 核心（ComposeBaseline/CompileCore/DecideFeedback/ExtractPortraitCode），
// 直接注入 EmotionSnapshot，不触碰 Game1/MoodShockStore。
public class EmotionalStateResolverTests
{
    private static void AssertFloat(float expected, float actual, string context)
    {
        Assert.True(System.Math.Abs(expected - actual) < 0.001f,
            $"{context}: expected {expected}, got {actual}");
    }

    private static EmotionSnapshot Snap(float v, float a, float o, float bo) => new(v, a, o, bo);

    private static int CountOccurrences(string text, string token)
    {
        int count = 0, idx = 0;
        while ((idx = text.IndexOf(token, idx, System.StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += token.Length;
        }
        return count;
    }

    // UT-12 原生映射表逐值断言（Optimism/SocialAnxiety 全枚举 + 兜底）。
    [Fact]
    public void UT12_ComposeBaseline_NativeMappingTable()
    {
        // Optimism 全枚举（SocialAnxiety 固定 1，无 ProgressStates 修饰）
        AssertFloat(0.35f, EmotionalStateResolver.ComposeBaseline(null, 0, 1, false, 0).v, "optimism=0 V");
        AssertFloat(0.05f, EmotionalStateResolver.ComposeBaseline(null, 1, 1, false, 0).v, "optimism=1 V");
        AssertFloat(-0.25f, EmotionalStateResolver.ComposeBaseline(null, 2, 1, false, 0).v, "optimism=2 V");
        AssertFloat(0.05f, EmotionalStateResolver.ComposeBaseline(null, 7, 1, false, 0).v, "optimism=7 兜底 V");

        // SocialAnxiety 全枚举（Optimism 固定 1）
        AssertFloat(0.65f, EmotionalStateResolver.ComposeBaseline(null, 1, 0, false, 0).a, "sa=0 A");
        AssertFloat(0.75f, EmotionalStateResolver.ComposeBaseline(null, 1, 0, false, 0).o, "sa=0 O");
        AssertFloat(0.50f, EmotionalStateResolver.ComposeBaseline(null, 1, 1, false, 0).a, "sa=1 A");
        AssertFloat(0.50f, EmotionalStateResolver.ComposeBaseline(null, 1, 1, false, 0).o, "sa=1 O");
        AssertFloat(0.35f, EmotionalStateResolver.ComposeBaseline(null, 1, 2, false, 0).a, "sa=2 A");
        AssertFloat(0.25f, EmotionalStateResolver.ComposeBaseline(null, 1, 2, false, 0).o, "sa=2 O");
        AssertFloat(0.50f, EmotionalStateResolver.ComposeBaseline(null, 1, 9, false, 0).a, "sa=9 兜底 A");
        AssertFloat(0.50f, EmotionalStateResolver.ComposeBaseline(null, 1, 9, false, 0).o, "sa=9 兜底 O");

        // authored 非 null → 直接采用
        var authored = new EmotionalBaseline { Valence = 0.10f, Arousal = 0.40f, Openness = 0.60f };
        var (av, aa, ao) = EmotionalStateResolver.ComposeBaseline(authored, 2, 2, false, 0);
        AssertFloat(0.10f, av, "authored V");
        AssertFloat(0.40f, aa, "authored A");
        AssertFloat(0.60f, ao, "authored O");
    }

    // UT-13 ProgressStates：已婚 +0.15V/+0.20O；未婚 8 心 +0.10O；已婚不叠加 8 心分支。
    [Fact]
    public void UT13_ComposeBaseline_ProgressStates()
    {
        var (mv, ma, mo) = EmotionalStateResolver.ComposeBaseline(null, 1, 1, true, 0);
        AssertFloat(0.20f, mv, "已婚 V (+0.15)");
        AssertFloat(0.70f, mo, "已婚 O (+0.20)");
        AssertFloat(0.50f, ma, "已婚 A 不变");

        var (hv, ha, ho) = EmotionalStateResolver.ComposeBaseline(null, 1, 1, false, 8);
        AssertFloat(0.05f, hv, "未婚 8 心 V 不变");
        AssertFloat(0.60f, ho, "未婚 8 心 O (+0.10)");
        AssertFloat(0.50f, ha, "未婚 8 心 A 不变");

        var (sv, _, so) = EmotionalStateResolver.ComposeBaseline(null, 1, 1, true, 8);
        AssertFloat(0.20f, sv, "已婚 8 心 V");
        AssertFloat(0.70f, so, "已婚 8 心 O（不叠加 8 心分支）");

        AssertFloat(0.50f, EmotionalStateResolver.ComposeBaseline(null, 1, 1, false, 7).o, "未婚 7 心 O 不变");
    }

    // UT-14 反问双轨四象限。
    [Fact]
    public void UT14_CompileCore_DualTrackQuadrants()
    {
        // ① baseline 0.25 自然常态 → 软约束出现、硬禁令不出现
        var normal = EmotionalStateResolver.CompileCore(Snap(0.05f, 0.35f, 0.25f, 0.25f), 0, null, "测试");
        Assert.Contains("言语克制内敛", normal.PromptBlock);
        Assert.DoesNotContain("严禁向农夫提问", normal.PromptBlock);

        // ②a 同角色 O 压至 <0.20 → 硬禁令出现（基线差 0.25-0.05=0.20 捕获）
        var pressed = EmotionalStateResolver.CompileCore(Snap(0.05f, 0.35f, 0.18f, 0.25f), 0, null, "测试");
        Assert.Contains("严禁向农夫提问", pressed.PromptBlock);

        // ②b V<-0.25 → 硬禁令出现
        var negative = EmotionalStateResolver.CompileCore(Snap(-0.30f, 0.35f, 0.25f, 0.25f), 0, null, "测试");
        Assert.Contains("严禁向农夫提问", negative.PromptBlock);

        // ③ baseline 0.45（已婚内向）中性带 → 两类指令均不出现
        var neutral = EmotionalStateResolver.CompileCore(Snap(0.20f, 0.50f, 0.45f, 0.45f), 0, null, "测试");
        Assert.DoesNotContain("严禁向农夫提问", neutral.PromptBlock);
        Assert.DoesNotContain("言语克制内敛", neutral.PromptBlock);

        // ④ baseline 0.75 外向受击 O=0.30 → 硬禁令出现（基线差捕获）
        var extrovert = EmotionalStateResolver.CompileCore(Snap(0.05f, 0.50f, 0.30f, 0.75f), 0, null, "测试");
        Assert.Contains("严禁向农夫提问", extrovert.PromptBlock);
    }

    // UT-15 Narration 优先级：V=0.40/A=0.30/O=0.30 → 第 4 档（轻松）而非第 5 档（迟缓）。
    [Fact]
    public void UT15_CompileCore_NarrationPriority()
    {
        var (_, narration) = EmotionalStateResolver.CompileCore(
            Snap(0.40f, 0.30f, 0.30f, 0.50f), 0, null, "阿比盖尔");
        Assert.Equal("（阿比盖尔 神色轻松，嘴角带着一丝笑意）", narration);
        Assert.DoesNotContain("心不在焉", narration);
    }

    // UT-16 转折文案：V=-0.5 时恰好出现一次（A 两子分支均验证），非负 V 不出现。
    [Fact]
    public void UT16_CompileCore_TransitionLineOncePerNegativeValence()
    {
        const string transition =
            "- 若农夫的言行让你原本的情绪出现真实的松动，不必刻意维持冷硬；真实的情绪转折比强装的负面更可信。此时允许转用 $h 肖像。";

        var (high, _) = EmotionalStateResolver.CompileCore(Snap(-0.50f, 0.70f, 0.30f, 0.50f), 0, null, "谢恩");
        Assert.Equal(1, CountOccurrences(high, transition));

        var (low, _) = EmotionalStateResolver.CompileCore(Snap(-0.50f, 0.50f, 0.30f, 0.50f), 0, null, "谢恩");
        Assert.Equal(1, CountOccurrences(low, transition));

        var (pos, _) = EmotionalStateResolver.CompileCore(Snap(0.40f, 0.70f, 0.80f, 0.50f), 0, null, "谢恩");
        Assert.Equal(0, CountOccurrences(pos, transition));
    }

    // UT-17 DecideFeedback 全分支表（kind 值域断言）。
    [Fact]
    public void UT17_DecideFeedback_AllBranches()
    {
        var negative = Snap(-0.30f, 0.50f, 0.30f, 0.50f);

        var comforted = DialogueFeedbackService.DecideFeedback(negative, "$h");
        Assert.NotNull(comforted);
        Assert.Equal("Comforted", comforted.Value.kind);
        AssertFloat(0.35f, comforted.Value.dv, "Comforted dv");
        AssertFloat(-0.10f, comforted.Value.da, "Comforted da");
        AssertFloat(0.30f, comforted.Value.dOpen, "Comforted dOpen");
        Assert.Equal(240, comforted.Value.minutes);

        var neutralized = DialogueFeedbackService.DecideFeedback(negative, "$0");
        Assert.NotNull(neutralized);
        Assert.Equal("Neutralized", neutralized.Value.kind);
        AssertFloat(0.15f, neutralized.Value.dv, "Neutralized dv");
        AssertFloat(0f, neutralized.Value.da, "Neutralized da");
        AssertFloat(0.10f, neutralized.Value.dOpen, "Neutralized dOpen");
        Assert.Equal(120, neutralized.Value.minutes);

        var saddened = DialogueFeedbackService.DecideFeedback(negative, "$s");
        Assert.NotNull(saddened);
        Assert.Equal("Saddened", saddened.Value.kind);
        AssertFloat(-0.15f, saddened.Value.dv, "Saddened dv");
        AssertFloat(0f, saddened.Value.da, "Saddened da");
        AssertFloat(-0.15f, saddened.Value.dOpen, "Saddened dOpen");
        Assert.Equal(120, saddened.Value.minutes);

        var agitated = DialogueFeedbackService.DecideFeedback(negative, "$a");
        Assert.NotNull(agitated);
        Assert.Equal("Agitated", agitated.Value.kind);
        AssertFloat(-0.20f, agitated.Value.dv, "Agitated dv");
        AssertFloat(0.25f, agitated.Value.da, "Agitated da");
        AssertFloat(-0.20f, agitated.Value.dOpen, "Agitated dOpen");
        Assert.Equal(120, agitated.Value.minutes);

        // $a 且 V=-0.45 → null（极端负面归因豁免）
        Assert.Null(DialogueFeedbackService.DecideFeedback(Snap(-0.45f, 0.50f, 0.30f, 0.50f), "$a"));

        // $l / 未知码 / null → null
        Assert.Null(DialogueFeedbackService.DecideFeedback(negative, "$l"));
        Assert.Null(DialogueFeedbackService.DecideFeedback(negative, "$x"));
        Assert.Null(DialogueFeedbackService.DecideFeedback(negative, null));

        // V=-0.20（>= -0.25）→ null
        Assert.Null(DialogueFeedbackService.DecideFeedback(Snap(-0.20f, 0.50f, 0.30f, 0.50f), "$h"));
    }

    // UT-18 ExtractPortraitCode：多码取末尾、$0 门禁降级、null 白名单降级、词边界。
    [Fact]
    public void UT18_ExtractPortraitCode_Boundaries()
    {
        // 多码取末尾
        Assert.Equal("$s", DialogueFeedbackService.ExtractPortraitCode("你好$h 最近$s怎么样", null));
        Assert.Equal("$h", DialogueFeedbackService.ExtractPortraitCode("开头$h", null));

        // $0 门禁：白名单不含 "$0" → 降级 null
        Assert.Null(DialogueFeedbackService.ExtractPortraitCode("情绪$0稳定", new List<string> { "h", "s" }));

        // 白名单 null → 仅接受原版五码（$0 降级，$u/$l 放行）
        Assert.Null(DialogueFeedbackService.ExtractPortraitCode("情绪$0稳定", null));
        Assert.Equal("$u", DialogueFeedbackService.ExtractPortraitCode("看$u这里", null));
        Assert.Equal("$l", DialogueFeedbackService.ExtractPortraitCode("看$l这里", null));

        // 白名单显式含 "$0" → 放行
        Assert.Equal("$0", DialogueFeedbackService.ExtractPortraitCode("情绪$0稳定", new List<string> { "$0" }));

        // 词边界：$house 不误匹配
        Assert.Null(DialogueFeedbackService.ExtractPortraitCode("去$house看看", null));
        Assert.Equal("$s", DialogueFeedbackService.ExtractPortraitCode("去$house看看，然后$s坐下", null));

        // 中文紧邻：$h你好 应命中（ASCII 前瞻而非 \b，\b 会因 CJK 属 \w 而误杀）
        Assert.Equal("$h", DialogueFeedbackService.ExtractPortraitCode("$h你好呀", null));

        // 无匹配 → null
        Assert.Null(DialogueFeedbackService.ExtractPortraitCode("没有任何代码", null));
        Assert.Null(DialogueFeedbackService.ExtractPortraitCode("", null));
    }
}
