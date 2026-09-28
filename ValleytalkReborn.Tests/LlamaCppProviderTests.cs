// LlamaCppProviderTests.cs
// LOCAL-002 — llama.cpp Provider 失败收敛契约测试。
// 覆盖：成功文本不变、调用方取消、QueryTimeout、重试延迟可中断、
// 连接/HTTP 5xx/429 重试预算、HTTP 4xx 不重发、非法 JSON、缺少 content、空响应、
// 以及两个入口均不阻塞调用线程。
//
// 纯内存测试：通过反射把 Llm 的共享 HttpClient 换成内存 HttpMessageHandler 桩，
// 不发起真实网络请求；用例结束恢复原值。互斥依赖 assemblies 级
// DisableTestParallelization（TestCollections.cs）保护进程级静态字段。

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ValleytalkReborn;
using Xunit;

namespace ValleytalkReborn.Tests;

[Collection("StaticGlobalStateCollection")]
public class LlamaCppProviderTests : IDisposable
{
    private const string Endpoint = "http://127.0.0.1:8080/completion";
    private const string PromptFormat = "{system}\n{prompt}[/INST]{response_start}";

    private static readonly FieldInfo SharedHttpClientField =
        typeof(Llm).GetField("_sharedHttpClient", BindingFlags.NonPublic | BindingFlags.Static);

    private readonly ModConfig _originalConfig;
    private readonly HttpClient _originalClient;

    public LlamaCppProviderTests()
    {
        _originalConfig = ModEntry.Config;
        _originalClient = (HttpClient)SharedHttpClientField.GetValue(null);
    }

    public void Dispose()
    {
        SharedHttpClientField.SetValue(null, _originalClient);
        ModEntry.Config = _originalConfig;
    }

    // ── 成功路径 ──

    [Fact]
    public async Task RunInference_Success_ReturnsOriginalContentText()
    {
        var (provider, handler) = Install((_, _, _) => Task.FromResult(
            Ok("{\"content\":\" Hello there. \",\"timings\":{\"prompt_n\":3}}")));

        var result = await provider.RunInference("system", "game", "npc", "prompt");

        Assert.True(result.IsSuccess);
        Assert.Equal(" Hello there. ", result.Text);
        Assert.Equal(DialogueModels.LlmRequestEndReason.Success, result.EndReason);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task RunStreamingInference_Success_InvokesOnTokenOnceWithFullText()
    {
        var (provider, handler) = Install((_, _, _) => Task.FromResult(
            Ok("{\"content\":\"Hello there\"}")));

        var received = new List<string>();
        using var cts = new CancellationTokenSource();

        var result = await provider.RunStreamingInference(
            "system", "", "", "prompt", received.Add, cts.Token);

        Assert.True(result.IsSuccess);
        Assert.Equal("Hello there", result.Text);
        Assert.Equal(new[] { "Hello there" }, received);
        Assert.Equal(1, handler.CallCount);
    }

    // ── 取消：不得触发第二次 HTTP 请求 ──

    [Fact]
    public async Task RunStreamingInference_PreCancelledToken_SendsNoRequest()
    {
        var (provider, handler) = Install((_, _, _) => Task.FromResult(Ok("{\"content\":\"x\"}")));

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var invoked = false;
        var result = await provider.RunStreamingInference(
            "system", "", "", "prompt", _ => invoked = true, cts.Token);

        Assert.Equal(DialogueModels.LlmRequestEndReason.Cancelled, result.EndReason);
        Assert.False(result.IsSuccess);
        Assert.Equal(0, handler.CallCount);
        Assert.False(invoked);
    }

    [Fact]
    public async Task RunStreamingInference_CancelledDuringFirstRequest_DoesNotSendSecondRequest()
    {
        using var cts = new CancellationTokenSource();

        var (provider, handler) = Install((_, token, _) =>
        {
            cts.Cancel();
            throw new OperationCanceledException(token);
        });

        var watch = Stopwatch.StartNew();
        var invoked = false;
        var result = await provider.RunStreamingInference(
            "system", "", "", "prompt", _ => invoked = true, cts.Token);
        watch.Stop();

        Assert.Equal(DialogueModels.LlmRequestEndReason.Cancelled, result.EndReason);
        Assert.False(result.IsSuccess);
        Assert.Equal(1, handler.CallCount);
        Assert.False(invoked);
        Assert.True(watch.Elapsed < TimeSpan.FromMilliseconds(900),
            $"取消应在重试等待有效期内返回，实际耗时 {watch.ElapsedMilliseconds}ms");
    }

    [Fact]
    public async Task RetryDelay_IsInterruptedByCancellationToken()
    {
        // 第一次返回可重试的 5xx，第二次尝试前的等待必须被令牌打断。
        var (provider, handler) = Install((_, _, call) =>
            call == 1
                ? Task.FromResult(Status(HttpStatusCode.InternalServerError, "boom"))
                : Task.FromResult(Ok("{\"content\":\"late\"}")));

        using var cts = new CancellationTokenSource();
        var watch = Stopwatch.StartNew();
        Task<LlmResponse> task = provider.RunStreamingInference(
            "system", "", "", "prompt", _ => { }, cts.Token);

        await Task.Delay(100);
        cts.Cancel();
        var result = await task;
        watch.Stop();

        Assert.Equal(DialogueModels.LlmRequestEndReason.Cancelled, result.EndReason);
        Assert.Equal(1, handler.CallCount);
        Assert.True(watch.Elapsed < TimeSpan.FromMilliseconds(900),
            $"重试等待应可被取消中断，实际耗时 {watch.ElapsedMilliseconds}ms");
    }

    // ── 超时：不得进入重试循环 ──

    [Fact]
    public async Task RunStreamingInference_HttpTimeout_ReturnsTimeoutWithoutRetry()
    {
        var (provider, handler) = Install((_, _, _) =>
            throw new TaskCanceledException("simulated HttpClient timeout"));

        using var cts = new CancellationTokenSource();
        var invoked = false;
        var result = await provider.RunStreamingInference(
            "system", "", "", "prompt", _ => invoked = true, cts.Token);

        Assert.Equal(DialogueModels.LlmRequestEndReason.Timeout, result.EndReason);
        Assert.False(result.IsSuccess);
        Assert.Null(result.Text);
        Assert.Equal(1, handler.CallCount);
        Assert.False(invoked);
    }

    // ── HTTP 错误分类 ──

    [Fact]
    public async Task RunInference_Http4xx_DoesNotResendSameRequest()
    {
        var (provider, handler) = Install((_, _, _) => Task.FromResult(
            Status(HttpStatusCode.BadRequest, "{\"error\":\"bad request\"}")));

        var result = await provider.RunInference("system", "", "", "prompt");

        Assert.False(result.IsSuccess);
        Assert.Equal(400, result.ResponseCode);
        Assert.Contains("HTTP 400", result.ErrorMessage);
        Assert.Null(result.Text);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task RunInference_Http500_RetriesWithinBudgetThenSucceeds()
    {
        var (provider, handler) = Install((_, _, call) =>
            call == 1
                ? Task.FromResult(Status(HttpStatusCode.InternalServerError, "boom"))
                : Task.FromResult(Ok("{\"content\":\"recovered\"}")));

        var result = await provider.RunInference("system", "", "", "prompt");

        Assert.True(result.IsSuccess);
        Assert.Equal("recovered", result.Text);
        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task RunInference_Http429Exhausted_ReturnsExplicitFailure()
    {
        var (provider, handler) = Install((_, _, _) => Task.FromResult(
            Status((HttpStatusCode)429, "rate limited")));

        var result = await provider.RunInference("system", "", "", "prompt");

        Assert.False(result.IsSuccess);
        Assert.Equal(429, result.ResponseCode);
        Assert.Contains("HTTP 429", result.ErrorMessage);
        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task RunInference_Http500WithAllowRetryFalse_SendsOnlyOnce()
    {
        var (provider, handler) = Install((_, _, _) => Task.FromResult(
            Status(HttpStatusCode.ServiceUnavailable, "down")));

        var result = await provider.RunInference("system", "", "", "prompt", allowRetry: false);

        Assert.False(result.IsSuccess);
        Assert.Equal(503, result.ResponseCode);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task RunInference_ConnectionFailure_RetriesThenFailsExplicitly()
    {
        var (provider, handler) = Install((_, _, _) =>
            throw new HttpRequestException("connection refused"));

        var result = await provider.RunInference("system", "", "", "prompt");

        Assert.False(result.IsSuccess);
        Assert.Contains("connection failed", result.ErrorMessage);
        Assert.Null(result.Text);
        Assert.Equal(2, handler.CallCount);
    }

    // ── 响应体错误分类 ──

    [Fact]
    public async Task RunInference_InvalidJson_ReturnsDiagnosableFailure()
    {
        var (provider, handler) = Install((_, _, _) => Task.FromResult(Ok("<html>not json</html>")));

        var result = await provider.RunInference("system", "", "", "prompt");

        Assert.False(result.IsSuccess);
        Assert.Equal(502, result.ResponseCode);
        Assert.Contains("malformed JSON", result.ErrorMessage);
        Assert.Null(result.Text);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task RunInference_EmptyBody_ReturnsFailureInsteadOfEmptySuccess()
    {
        var (provider, handler) = Install((_, _, _) => Task.FromResult(Ok(string.Empty)));

        var result = await provider.RunInference("system", "", "", "prompt");

        Assert.False(result.IsSuccess);
        Assert.Equal(502, result.ResponseCode);
        Assert.True(string.IsNullOrEmpty(result.Text));
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task RunInference_MissingContent_ReturnsFailureInsteadOfEmptySuccess()
    {
        var (provider, handler) = Install((_, _, _) => Task.FromResult(
            Ok("{\"timings\":{\"prompt_n\":7,\"predicted_n\":2}}")));

        var result = await provider.RunInference("system", "", "", "prompt", allowRetry: false);

        Assert.False(result.IsSuccess);
        Assert.Equal(502, result.ResponseCode);
        Assert.Contains("no content", result.ErrorMessage);
        Assert.True(string.IsNullOrEmpty(result.Text));
        Assert.Equal(1, handler.CallCount);
    }

    // ── 线程边界：两个入口都不得阻塞调用线程 ──

    [Fact]
    public async Task BothEntryPoints_DoNotBlockCallerThread()
    {
        // 每次调用都返回全新的 HttpResponseMessage（响应会被 Provider 的 using 释放）。
        var gate = new TaskCompletionSource<bool>();
        var (provider, _) = Install(async (_, _, _) =>
        {
            await gate.Task;
            return Ok("{\"content\":\"async\"}");
        });

        using var cts = new CancellationTokenSource();

        Task<LlmResponse> inference = provider.RunInference("system", "", "", "prompt");
        Assert.False(inference.Wait(TimeSpan.FromMilliseconds(150)), "RunInference 不应同步返回已完成任务");

        Task<LlmResponse> streaming = provider.RunStreamingInference(
            "system", "", "", "prompt", _ => { }, cts.Token);
        Assert.False(streaming.Wait(TimeSpan.FromMilliseconds(150)), "RunStreamingInference 不应同步返回已完成任务");

        gate.SetResult(true);

        Assert.Equal("async", (await inference).Text);
        Assert.Equal("async", (await streaming).Text);
    }

    // ── 测试脚手架 ──

    private delegate Task<HttpResponseMessage> StubBehavior(
        HttpRequestMessage request, CancellationToken token, int call);

    private (LlmLlamaCpp Provider, StubHandler Handler) Install(StubBehavior behavior)
    {
        ModEntry.Config = new ModConfig { QueryTimeout = 5 };

        var handler = new StubHandler(behavior);
        SharedHttpClientField.SetValue(null, new HttpClient(handler));

        return (new LlmLlamaCpp(Endpoint, PromptFormat), handler);
    }

    private static HttpResponseMessage Ok(string body) => Status(HttpStatusCode.OK, body);

    private static HttpResponseMessage Status(HttpStatusCode code, string body)
        => new HttpResponseMessage(code)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };

    /// <summary>内存 HTTP 桩：统计请求次数并按用例注入行为。</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly StubBehavior _behavior;
        private int _callCount;

        public StubHandler(StubBehavior behavior) => _behavior = behavior;

        public int CallCount => Volatile.Read(ref _callCount);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return _behavior(request, cancellationToken, Interlocked.Increment(ref _callCount));
        }
    }
}
