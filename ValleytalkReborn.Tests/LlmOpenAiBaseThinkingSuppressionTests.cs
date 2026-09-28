// LlmOpenAiBaseThinkingSuppressionTests.cs
// BIOAI-FAST-001 — LlmOpenAiBase 思考抑制判定与 BioEditor 快速模式门控测试
// （internal 静态纯函数，InternalsVisibleTo 已配置）。
// 覆盖：T1 EvaluateThinkingSuppression 矩阵、T2 ApplyThinkingSuppression 键集合、
// T3 IsBioEditorFastContext、T4 FastModePlanCannotGuaranteeSuppression、
// T5 ContainsPairedThinkTags。
// 纯内存测试：无游戏实例、无 Provider、无网络、不触达 ModEntry/ResolveParameters
// （不调用 BuildRequestBody / StripThinkingParameters / ApplyDowngradeStage(stage 2)）。

using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using ValleytalkReborn;
using Xunit;

namespace ValleytalkReborn.Tests;

public class LlmOpenAiBaseThinkingSuppressionTests
{
    private const string FastContext = "BioEditor_Fast";
    private const string ThinkContext = "BioEditor_Think";

    private const string LocalUrl = "http://localhost:1234/v1";
    private const string CloudUrl = "https://api.example.com/v1";
    private const string OpenAiUrl = "https://api.openai.com/v1";
    private const string OpenRouterUrl = "https://openrouter.ai/api/v1";
    private const string AnthropicUrl = "https://api.anthropic.com";

    private static ThinkingSuppressionPlan ReasoningEffortLow(string reason) =>
        new ThinkingSuppressionPlan(false, false, false, false, true, false, reason);

    private static ThinkingSuppressionPlan OpenRouter() =>
        new ThinkingSuppressionPlan(false, false, false, false, false, true, "OpenRouter");

    private static ThinkingSuppressionPlan UniversalBroadcast() =>
        new ThinkingSuppressionPlan(true, true, true, true, false, false, "UniversalBroadcast");

    private static string[] SortedKeys(Dictionary<string, object> body) =>
        body.Keys.OrderBy(k => k, StringComparer.Ordinal).ToArray();

    // ── T1 EvaluateThinkingSuppression 矩阵 ──

    [Fact]
    public void T1_OfficialOpenAiReasoningModel_Fast_UsesReasoningEffortLow()
    {
        var plan = LlmOpenAiBase.EvaluateThinkingSuppression("o3", OpenAiUrl, FastContext);

        Assert.True(plan.UseReasoningEffortLow);
        Assert.False(plan.UseOpenRouterReasoning);
        Assert.False(plan.UseThinkingDisabled);
        Assert.Equal("OfficialOpenAi", plan.Reason);
    }

    [Fact]
    public void T1_ThinkContext_ReturnsEmptyPlan()
    {
        var plan = LlmOpenAiBase.EvaluateThinkingSuppression("o3", LocalUrl, ThinkContext);

        Assert.False(plan.AnyApiSuppression);
        Assert.True(string.IsNullOrEmpty(plan.Reason));
    }

    // LOCAL-001-R1：本地端点（回环 / 私网）一律不广播思考抑制参数，避免本地后端 400/422。
    // 原断言（ReasoningEffortLow / UniversalBroadcast）已被该票据取代，云端矩阵不受影响。
    [Fact]
    public void T1_LocalReasoningFamily_Fast_ReturnsEmptyPlan_LocalEndpoint()
    {
        var plan = LlmOpenAiBase.EvaluateThinkingSuppression("deepseek-r1:14b", LocalUrl, FastContext);

        Assert.False(plan.AnyApiSuppression);
        Assert.True(string.IsNullOrEmpty(plan.Reason));
    }

    [Fact]
    public void T1_LocalGenericModel_Fast_ReturnsEmptyPlan_LocalEndpoint()
    {
        var plan = LlmOpenAiBase.EvaluateThinkingSuppression("local-model", LocalUrl, FastContext);

        Assert.False(plan.AnyApiSuppression);
        Assert.True(string.IsNullOrEmpty(plan.Reason));
    }

    [Fact]
    public void T1_CloudGenericModel_Fast_UsesUniversalBroadcast()
    {
        var plan = LlmOpenAiBase.EvaluateThinkingSuppression("local-model", CloudUrl, FastContext);

        Assert.True(plan.UseThinkingDisabled);
        Assert.True(plan.UseEnableThinking);
        Assert.True(plan.UseChatTemplateKwargs);
        Assert.True(plan.UseIncludeReasoning);
        Assert.False(plan.UseReasoningEffortLow);
        Assert.False(plan.UseOpenRouterReasoning);
        Assert.Equal("UniversalBroadcast", plan.Reason);
    }

    // T1 用例 4b（契约修正存档）：原票据对 "llama3" 期望 UniversalBroadcast，实为 CONTRACT DEFECT。
    // IsPlainTextModel（LlmOpenAiBase.cs L168-183，"llama" 子串）在 L219-220 提前返回 Empty，
    // 属有意行为：基座 llama 模型无 reasoning 通道，不发送抑制参数是正确行为。
    // llama 系在 Fast 上下文仍受输出侧守卫（reasoningEmitted → 502、成对 think 标签 → 502，
    // 均以 IsBioEditorFastContext 为前提、与 plan 无关）覆盖。
    [Fact]
    public void T1_LlamaFamily_Fast_ReturnsEmptyPlan_PlainTextShortCircuit()
    {
        var plan = LlmOpenAiBase.EvaluateThinkingSuppression("llama3", LocalUrl, FastContext);

        Assert.False(plan.AnyApiSuppression);
        Assert.True(string.IsNullOrEmpty(plan.Reason));
    }

    [Fact]
    public void T1_OpenAiPlainTextModel_Fast_ReturnsEmptyPlan()
    {
        var plan = LlmOpenAiBase.EvaluateThinkingSuppression("gpt-4o", OpenAiUrl, FastContext);

        Assert.False(plan.AnyApiSuppression);
        Assert.True(string.IsNullOrEmpty(plan.Reason));
    }

    [Fact]
    public void T1_OpenRouter_Fast_UsesOpenRouterReasoning()
    {
        var plan = LlmOpenAiBase.EvaluateThinkingSuppression("any", OpenRouterUrl, FastContext);

        Assert.True(plan.UseOpenRouterReasoning);
        Assert.False(plan.UseReasoningEffortLow);
        Assert.Equal("OpenRouter", plan.Reason);
    }

    [Fact]
    public void T1_Anthropic_Fast_ReturnsEmptyPlan()
    {
        var plan = LlmOpenAiBase.EvaluateThinkingSuppression("any", AnthropicUrl, FastContext);

        Assert.False(plan.AnyApiSuppression);
        Assert.True(string.IsNullOrEmpty(plan.Reason));
    }

    // ── T2 ApplyThinkingSuppression 键集合 ──

    [Fact]
    public void T2_EmptyPlan_WritesNoKeys()
    {
        var body = new Dictionary<string, object> { { "model", "gpt-4o" } };

        LlmOpenAiBase.ApplyThinkingSuppression(body, ThinkingSuppressionPlan.Empty);

        Assert.Equal(new[] { "model" }, SortedKeys(body));
    }

    [Fact]
    public void T2_ReasoningEffortLowPlan_WritesOnlyReasoningEffort()
    {
        var body = new Dictionary<string, object>();

        LlmOpenAiBase.ApplyThinkingSuppression(body, ReasoningEffortLow("OfficialOpenAi"));

        Assert.Equal(new[] { "reasoning_effort" }, SortedKeys(body));
        Assert.Equal("low", body["reasoning_effort"]);
    }

    [Fact]
    public void T2_OpenRouterPlan_WritesOnlyReasoningNone()
    {
        var body = new Dictionary<string, object>();

        LlmOpenAiBase.ApplyThinkingSuppression(body, OpenRouter());

        Assert.Equal(new[] { "reasoning" }, SortedKeys(body));
        Assert.Equal("{\"effort\":\"none\"}", JsonConvert.SerializeObject(body["reasoning"]));
    }

    [Fact]
    public void T2_UniversalBroadcastPlan_WritesExactlyFourKeys()
    {
        var body = new Dictionary<string, object>();

        LlmOpenAiBase.ApplyThinkingSuppression(body, UniversalBroadcast());

        Assert.Equal(
            new[] { "chat_template_kwargs", "enable_thinking", "include_reasoning", "thinking" },
            SortedKeys(body));
        Assert.Equal("{\"type\":\"disabled\"}", JsonConvert.SerializeObject(body["thinking"]));
        Assert.Equal(false, body["enable_thinking"]);
        Assert.Equal("{\"enable_thinking\":false}", JsonConvert.SerializeObject(body["chat_template_kwargs"]));
        Assert.Equal(false, body["include_reasoning"]);
    }

    // ── T3 IsBioEditorFastContext ──

    [Theory]
    [InlineData("BioEditor_Fast", true)]
    [InlineData("bioeditor_fast", true)]
    [InlineData("BioEditor_Think", false)]
    [InlineData("Bark", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void T3_IsBioEditorFastContext(string cacheContext, bool expected)
    {
        Assert.Equal(expected, LlmOpenAiBase.IsBioEditorFastContext(cacheContext));
    }

    // ── T4 FastModePlanCannotGuaranteeSuppression ──

    [Fact]
    public void T4_ReasoningEffortLowPlans_CannotGuaranteeSuppression()
    {
        Assert.True(LlmOpenAiBase.FastModePlanCannotGuaranteeSuppression(ReasoningEffortLow("OfficialOpenAi")));
        Assert.True(LlmOpenAiBase.FastModePlanCannotGuaranteeSuppression(ReasoningEffortLow("OpenAiReasoningFamily")));
    }

    [Fact]
    public void T4_OtherPlans_CanBeSentAndVerifiedOnOutputSide()
    {
        Assert.False(LlmOpenAiBase.FastModePlanCannotGuaranteeSuppression(OpenRouter()));
        Assert.False(LlmOpenAiBase.FastModePlanCannotGuaranteeSuppression(UniversalBroadcast()));
        Assert.False(LlmOpenAiBase.FastModePlanCannotGuaranteeSuppression(ThinkingSuppressionPlan.Empty));
    }

    // ── T5 ContainsPairedThinkTags ──

    [Fact]
    public void T5_PairedTagsOnSingleLine_ReturnsTrue()
    {
        Assert.True(LlmOpenAiBase.ContainsPairedThinkTags("hello <think>reasoning</think> world"));
    }

    [Fact]
    public void T5_PairedTagsAcrossLines_ReturnsTrue()
    {
        Assert.True(LlmOpenAiBase.ContainsPairedThinkTags("intro\n<think>\nline one\nline two\n</think>\noutro"));
    }

    [Fact]
    public void T5_OpeningTagWithoutClosing_ReturnsFalse()
    {
        Assert.False(LlmOpenAiBase.ContainsPairedThinkTags("<think>never closed"));
    }

    [Fact]
    public void T5_IsolatedClosingTag_ReturnsFalse()
    {
        Assert.False(LlmOpenAiBase.ContainsPairedThinkTags("text </think> more text"));
    }

    [Fact]
    public void T5_NoTags_ReturnsFalse()
    {
        Assert.False(LlmOpenAiBase.ContainsPairedThinkTags("plain narrative text"));
    }

    [Fact]
    public void T5_NullAndEmptyAndWhitespace_ReturnFalse()
    {
        Assert.False(LlmOpenAiBase.ContainsPairedThinkTags(null));
        Assert.False(LlmOpenAiBase.ContainsPairedThinkTags(string.Empty));
        Assert.False(LlmOpenAiBase.ContainsPairedThinkTags("   \r\n\t "));
    }

    [Fact]
    public void T5_PairedTagsInsideCodeFence_ReturnsTrue()
    {
        Assert.True(LlmOpenAiBase.ContainsPairedThinkTags("```json\n{\"a\":1}\n```\n<think>inside</think>"));
    }
}
