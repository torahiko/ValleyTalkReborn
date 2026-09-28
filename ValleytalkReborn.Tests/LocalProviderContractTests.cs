// LocalProviderContractTests.cs
// LOCAL-001-R1 — 本地 Provider 共享分类契约测试。
// 覆盖：UrlHelper.IsPrivateNetworkUrl、ProviderDefaults.IsLocalTarget、
// LlmOpenAiBase.EvaluateThinkingSuppression 本地端点短路与云端矩阵回归。
// 纯内存测试：无游戏实例、无 ModEntry、无网络、无 DNS、无文件 I/O。

using ValleytalkReborn;
using Xunit;

namespace ValleytalkReborn.Tests;

public class LocalProviderContractTests
{
    // ── UrlHelper.IsPrivateNetworkUrl ──

    [Theory]
    // 回环
    [InlineData("http://localhost:11434/v1", true)]
    [InlineData("http://127.0.0.1:1234/v1", true)]
    [InlineData("http://[::1]:8080/v1", true)]
    [InlineData("localhost:11434", true)]
    // RFC1918 私网
    [InlineData("http://10.0.0.5:8080", true)]
    [InlineData("10.255.255.254", true)]
    [InlineData("http://172.16.0.1:1234/v1", true)]
    [InlineData("http://172.31.255.255:1234/v1", true)]
    [InlineData("http://192.168.1.20:1234/v1", true)]
    [InlineData("192.168.1.20:1234/v1", true)]
    // 公网与非私网边界
    [InlineData("https://api.openai.com/v1", false)]
    [InlineData("https://openrouter.ai/api/v1", false)]
    [InlineData("http://8.8.8.8", false)]
    [InlineData("http://11.0.0.1:1234/v1", false)]
    [InlineData("http://172.15.0.1:1234/v1", false)]
    [InlineData("http://172.32.0.1:1234/v1", false)]
    [InlineData("http://192.169.0.1:1234/v1", false)]
    // 解析失败：不得因解析失败判为本地
    [InlineData("http://[::1", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData(null, false)]
    public void IsPrivateNetworkUrl_MatchesContract(string url, bool expected)
    {
        Assert.Equal(expected, UrlHelper.IsPrivateNetworkUrl(url));
    }

    // ── ProviderDefaults.IsLocalTarget ──

    [Theory]
    // 本地 Provider 名（大小写不敏感）
    [InlineData("Ollama", "", true)]
    [InlineData("LMStudio", "", true)]
    [InlineData("LlamaCpp", "", true)]
    [InlineData("oLlAmA", null, true)]
    [InlineData("lmstudio", "https://api.openai.com/v1", true)]
    [InlineData("LLAMACPP", "https://api.openai.com/v1", true)]
    // 非本地 Provider 名 + 本地/私网 URL
    [InlineData("OpenAiCompatible", "http://localhost:1234/v1", true)]
    [InlineData("OpenAiCompatible", "http://127.0.0.1:1234/v1", true)]
    [InlineData("OpenAiCompatible", "http://[::1]:1234/v1", true)]
    [InlineData("OpenAiCompatible", "http://10.1.2.3:8080/v1", true)]
    [InlineData("OpenAiCompatible", "http://172.20.0.9:8080/v1", true)]
    [InlineData("OpenAiCompatible", "http://192.168.1.20:1234/v1", true)]
    [InlineData("OpenAiCompatible", "192.168.1.20:1234/v1", true)]
    // 非本地 Provider 名 + 公网/空/非法 URL
    [InlineData("OpenAI", "https://api.openai.com/v1", false)]
    [InlineData("OpenAiCompatible", "https://openrouter.ai/api/v1", false)]
    [InlineData("OpenAiCompatible", "http://8.8.8.8/v1", false)]
    [InlineData("OpenAiCompatible", "", false)]
    [InlineData("OpenAiCompatible", "http://[::1", false)]
    [InlineData("OpenAI", null, false)]
    public void IsLocalTarget_MatchesContract(string provider, string url, bool expected)
    {
        Assert.Equal(expected, ProviderDefaults.IsLocalTarget(provider, url));
    }

    // ── EvaluateThinkingSuppression：本地端点一律 Empty ──

    [Theory]
    [InlineData("http://localhost:1234/v1")]
    [InlineData("http://127.0.0.1:1234/v1")]
    [InlineData("http://[::1]:1234/v1")]
    [InlineData("http://10.0.0.7:8080/v1")]
    [InlineData("http://172.18.0.7:8080/v1")]
    [InlineData("http://192.168.1.20:1234/v1")]
    [InlineData("192.168.1.20:1234/v1")]
    public void EvaluateThinkingSuppression_LocalEndpoint_ReturnsEmptyPlan(string baseUrl)
    {
        var plan = LlmOpenAiBase.EvaluateThinkingSuppression("deepseek-r1:14b", baseUrl, "BioEditor_Fast");

        Assert.False(plan.AnyApiSuppression);
        Assert.True(string.IsNullOrEmpty(plan.Reason));
    }

    // ── EvaluateThinkingSuppression：云端矩阵不受影响 ──

    [Fact]
    public void EvaluateThinkingSuppression_CloudMatrix_Unchanged()
    {
        var official = LlmOpenAiBase.EvaluateThinkingSuppression("o3", "https://api.openai.com/v1", "BioEditor_Fast");
        Assert.True(official.UseReasoningEffortLow);
        Assert.Equal("OfficialOpenAi", official.Reason);

        var openRouter = LlmOpenAiBase.EvaluateThinkingSuppression("any", "https://openrouter.ai/api/v1", "BioEditor_Fast");
        Assert.True(openRouter.UseOpenRouterReasoning);
        Assert.Equal("OpenRouter", openRouter.Reason);

        Assert.False(LlmOpenAiBase.EvaluateThinkingSuppression("any", "https://api.anthropic.com", "BioEditor_Fast").AnyApiSuppression);
        Assert.False(LlmOpenAiBase.EvaluateThinkingSuppression("any", "https://generativelanguage.googleapis.com/v1beta", "BioEditor_Fast").AnyApiSuppression);

        var unknownCloud = LlmOpenAiBase.EvaluateThinkingSuppression("local-model", "https://api.example.com/v1", "BioEditor_Fast");
        Assert.True(unknownCloud.UseThinkingDisabled);
        Assert.Equal("UniversalBroadcast", unknownCloud.Reason);
    }
}
