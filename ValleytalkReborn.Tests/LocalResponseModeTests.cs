// LocalResponseModeTests.cs
// LOCAL-004（契约更正版）— 本地端点的 SSE 自动协商与降级契约测试。
// 无配置面：本地端点（UrlHelper.IsPrivateNetworkUrl 命中）一律优先流式，
// 仅在未产生任何 token 时最多降级一次非流式；云端端点零变化。
// 覆盖：成功流式、非 SSE 完整 JSON 就地解析（不重发）、非 SSE 且不可解析的显式失败、
// “明确不支持流式”降级恰一次、4xx 无流式信号不降级、部分 token 后断连不重发、
// 取消不触发降级请求、云端端点回归。
//
// 纯内存测试：通过反射把 Llm 的共享 HttpClient 换成内存 HttpMessageHandler 桩，
// 不发起真实网络请求；用例结束还原 Config / HttpClient / SMonitor。互斥依赖 assemblies 级
// DisableTestParallelization（TestCollections.cs）保护进程级静态字段。

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using StardewModdingAPI;
using ValleytalkReborn;
using Xunit;

namespace ValleytalkReborn.Tests;

[Collection("StaticGlobalStateCollection")]
public class LocalResponseModeTests : IDisposable
{
    private const string LocalUrl = "http://127.0.0.1:1234/v1";
    private const string CloudUrl = "https://api.example.com/v1";

    private const string StreamUnsupportedBody =
        "{\"error\":{\"message\":\"streaming is not supported by this server\"}}";

    /// <summary>退化重复正文：单字符重复 80 次，命中 LooksLikeDegenerateRepetition（≥60 且末尾重复 ≥20）。</summary>
    private static readonly string DegenerateBody =
        "{\"choices\":[{\"message\":{\"content\":\"" + new string('a', 80) + "\"}}]}";

    private static readonly FieldInfo SharedHttpClientField =
        typeof(Llm).GetField("_sharedHttpClient", BindingFlags.NonPublic | BindingFlags.Static);

    private readonly ModConfig _originalConfig;
    private readonly HttpClient _originalClient;
    private readonly IMonitor _originalMonitor;

    public LocalResponseModeTests()
    {
        _originalConfig = ModEntry.Config;
        _originalClient = (HttpClient)SharedHttpClientField.GetValue(null);
        _originalMonitor = ModEntry.SMonitor;
    }

    public void Dispose()
    {
        SharedHttpClientField.SetValue(null, _originalClient);
        ModEntry.Config = _originalConfig;
        ModEntry.SMonitor = _originalMonitor;
    }

    // ── 本地端点：成功的 SSE  ──

    [Fact]
    public async Task LocalEndpoint_SseSuccess_StreamsTokensWithoutFallback()
    {
        var (provider, handler) = Install(LocalUrl, (_, _, _) =>
            Task.FromResult(Sse(
                "data: {\"choices\":[{\"delta\":{\"content\":\"Hello\"}}]}\n\n" +
                "data: {\"choices\":[{\"delta\":{\"content\":\" there\"}}]}\n\n" +
                "data: [DONE]\n\n")));

        var received = new List<string>();
        using var cts = new CancellationTokenSource();

        var result = await provider.RunStreamingChatInference("system", Messages(), received.Add, cts.Token);

        Assert.True(result.IsSuccess);
        Assert.Equal("Hello there", result.Text);
        Assert.Equal(new[] { "Hello", " there" }, received);
        Assert.Equal(1, handler.CallCount);
        Assert.Contains("\"stream\":true", handler.Bodies[0]);
    }

    // ── 本地端点：响应不是 SSE → 降级恰一次（就地解析已收到的完整 JSON，不重复发送请求） ──

    [Fact]
    public async Task LocalEndpoint_NonSseJsonBody_ParsesReceivedBodyWithoutSecondRequest()
    {
        var (provider, handler) = Install(LocalUrl, (_, _, _) =>
            Task.FromResult(Json("{\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"Whole reply\"}}]}")));

        var received = new List<string>();
        using var cts = new CancellationTokenSource();

        var result = await provider.RunStreamingChatInference("system", Messages(), received.Add, cts.Token);

        Assert.True(result.IsSuccess);
        Assert.Equal("Whole reply", result.Text);
        Assert.Equal(new[] { "Whole reply" }, received);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task LocalEndpoint_NonSseUnparsableBody_ReturnsExplicitFailure()
    {
        var (provider, handler) = Install(LocalUrl, (_, _, _) =>
            Task.FromResult(Json("<html>not json</html>")));

        var received = new List<string>();
        using var cts = new CancellationTokenSource();

        var result = await provider.RunStreamingChatInference("system", Messages(), received.Add, cts.Token);

        Assert.False(result.IsSuccess);
        Assert.Equal(502, result.ResponseCode);
        Assert.Empty(received);
        Assert.Equal(1, handler.CallCount);
    }

    // ── 本地端点：服务明确拒绝 SSE → 降级恰一次非流式 ──

    [Fact]
    public async Task LocalEndpoint_StreamUnsupported_DegradesExactlyOnceToNonStreaming()
    {
        var (provider, handler) = Install(LocalUrl, (_, _, call) =>
            call == 1
                ? Task.FromResult(Json(StreamUnsupportedBody, HttpStatusCode.BadRequest))
                : Task.FromResult(Json("{\"choices\":[{\"message\":{\"content\":\"Fallback reply\"}}]}")));

        var received = new List<string>();
        using var cts = new CancellationTokenSource();

        var result = await provider.RunStreamingChatInference("system", Messages(), received.Add, cts.Token);

        Assert.True(result.IsSuccess);
        Assert.Equal("Fallback reply", result.Text);
        Assert.Equal(new[] { "Fallback reply" }, received);
        Assert.Equal(2, handler.CallCount);
        Assert.Contains("\"stream\":true", handler.Bodies[0]);
        Assert.Contains("\"stream\":false", handler.Bodies[1]);
    }

    // ── 本地端点：4xx 但没有明确的流式不支持信号 → 不降级，返回原始失败 ──

    [Fact]
    public async Task LocalEndpoint_Http400WithoutStreamSignal_ReturnsOriginalFailure()
    {
        var (provider, handler) = Install(LocalUrl, (_, _, _) =>
            Task.FromResult(Json("{\"error\":{\"message\":\"bad request\"}}", HttpStatusCode.BadRequest)));

        var received = new List<string>();
        using var cts = new CancellationTokenSource();

        var result = await provider.RunStreamingChatInference("system", Messages(), received.Add, cts.Token);

        Assert.False(result.IsSuccess);
        Assert.Equal(400, result.ResponseCode);
        Assert.Empty(received);
        Assert.Equal(1, handler.CallCount);
    }

    // ── 已下发 token 后失败：必须不再生成重复内容 ──

    [Fact]
    public async Task LocalEndpoint_PartialTokensThenBrokenStream_DoesNotResend()
    {
        var (provider, handler) = Install(LocalUrl, (_, _, _) =>
            Task.FromResult(SseInterrupted(
                "data: {\"choices\":[{\"delta\":{\"content\":\"Half\"}}]}\n\n")));

        var received = new List<string>();
        using var cts = new CancellationTokenSource();

        var result = await provider.RunStreamingChatInference("system", Messages(), received.Add, cts.Token);

        Assert.False(result.IsSuccess);
        Assert.Equal(new[] { "Half" }, received);
        Assert.Equal(1, handler.CallCount);
    }

    // ── 取消：不得触发降级请求 ──

    [Fact]
    public async Task LocalEndpoint_CancelledDuringStreamRequest_DoesNotSendFallbackRequest()
    {
        using var cts = new CancellationTokenSource();

        var (provider, handler) = Install(LocalUrl, (_, token, _) =>
        {
            cts.Cancel();
            return Task.FromException<HttpResponseMessage>(new OperationCanceledException(token));
        });

        var received = new List<string>();
        var result = await provider.RunStreamingChatInference("system", Messages(), received.Add, cts.Token);

        Assert.False(result.IsSuccess);
        Assert.Empty(received);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task LocalEndpoint_CancelledBeforeFallback_DoesNotSendNonStreamingRequest()
    {
        // 第一次调用明确拒绝 SSE，同时在返回响应后立刻取消；若实现仍然降级，
        // 第二次调用会返回成功文本，即可暴露契约违规。
        using var cts = new CancellationTokenSource();

        var (provider, handler) = Install(LocalUrl, (_, _, call) =>
        {
            if (call == 1)
            {
                cts.Cancel();
                return Task.FromResult(Json(StreamUnsupportedBody, HttpStatusCode.BadRequest));
            }

            return Task.FromResult(Json("{\"choices\":[{\"message\":{\"content\":\"should never be requested\"}}]}"));
        });

        var received = new List<string>();
        var result = await provider.RunStreamingChatInference("system", Messages(), received.Add, cts.Token);

        Assert.False(result.IsSuccess);
        Assert.Empty(received);
        Assert.Equal(1, handler.CallCount);
    }

    // ── 云端端点：本地自动降级不得影响云端路径 ──

    [Fact]
    public async Task CloudEndpoint_IsUnaffectedByLocalAutoFallback()
    {
        var (provider, handler) = Install(CloudUrl, (_, _, _) =>
            Task.FromResult(Sse(
                "data: {\"choices\":[{\"delta\":{\"content\":\"Cloud\"}}]}\n\n" +
                "data: [DONE]\n\n")));

        var received = new List<string>();
        using var cts = new CancellationTokenSource();

        var result = await provider.RunStreamingChatInference("system", Messages(), received.Add, cts.Token);

        Assert.True(result.IsSuccess);
        Assert.Equal("Cloud", result.Text);
        Assert.Equal(new[] { "Cloud" }, received);
        Assert.Equal(1, handler.CallCount);
        Assert.Contains("\"stream\":true", handler.Bodies[0]);
    }

    [Fact]
    public async Task CloudEndpoint_NonSseBody_KeepsLegacyEmptySuccess()
    {
        // 云端回归：非 SSE 响应体不属于本地协商范围，保持改动前的既有行为。
        var (provider, handler) = Install(CloudUrl, (_, _, _) =>
            Task.FromResult(Json("{\"choices\":[{\"message\":{\"content\":\"Cloud whole\"}}]}")));

        var received = new List<string>();
        using var cts = new CancellationTokenSource();

        var result = await provider.RunStreamingChatInference("system", Messages(), received.Add, cts.Token);

        Assert.Equal(1, handler.CallCount);
        Assert.Empty(received);
        Assert.True(result.IsSuccess);
        Assert.Equal(string.Empty, result.Text);
    }

    // ── LOCAL-004-R2：非流式执行器的取消 / 超时语义（照搬 LOCAL-002 模板） ──

    [Fact]
    public async Task NonStreaming_TransportCancelledWithoutCallerToken_ReturnsTimeout()
    {
        using var cts = new CancellationTokenSource();

        var (provider, handler) = Install(CloudUrl, (_, token, _) =>
            Task.FromException<HttpResponseMessage>(new OperationCanceledException(token)));

        var result = await provider.ExecuteNonStreamingRequestAsync(RawMessages(), 64, "", true, cts.Token);

        Assert.Equal("Timeout", result.EndReason.ToString());
        Assert.False(result.IsSuccess);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task NonStreaming_PreCancelledToken_ReturnsCancelledWithoutHttp()
    {
        // 本地端点：预取消令牌在 ExecuteNonStreamingRequestAsync 内的本地闸门即被拦下，零 HTTP。
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var (provider, handler) = Install(LocalUrl, (_, token, _) =>
            Task.FromException<HttpResponseMessage>(new OperationCanceledException(token)));

        var result = await provider.ExecuteNonStreamingRequestAsync(RawMessages(), 64, "", true, cts.Token);

        Assert.Equal("Cancelled", result.EndReason.ToString());
        Assert.False(result.IsSuccess);
        Assert.Equal(0, handler.CallCount);
    }

    [Fact]
    public async Task NonStreaming_PreCancelledToken_CloudEndpoint_HasNoSecondAttempt()
    {
        // 云端端点：无本地闸门，HttpClient 传输层会进入 handler 一次；执行器立即返回 Cancelled，
        // 不消耗重试预算、无第二次请求（CallCount 恒 1，绝不出现 2）。
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var (provider, handler) = Install(CloudUrl, (_, token, _) =>
            Task.FromException<HttpResponseMessage>(new OperationCanceledException(token)));

        var result = await provider.ExecuteNonStreamingRequestAsync(RawMessages(), 64, "", true, cts.Token);

        Assert.Equal("Cancelled", result.EndReason.ToString());
        Assert.False(result.IsSuccess);
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task NonStreaming_CancelledDuringFirstAttempt_ReturnsCancelled()
    {
        using var cts = new CancellationTokenSource();

        var (provider, handler) = Install(CloudUrl, (_, token, _) =>
        {
            cts.Cancel();
            return Task.FromException<HttpResponseMessage>(new OperationCanceledException(token));
        });

        var result = await provider.ExecuteNonStreamingRequestAsync(RawMessages(), 64, "", true, cts.Token);

        Assert.Equal("Cancelled", result.EndReason.ToString());
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task NonStreaming_CancelledDuringRetryWait_DoesNotSendSecondRequest()
    {
        using var cts = new CancellationTokenSource();

        var (provider, handler) = Install(CloudUrl, (_, _, _) =>
        {
            // 退化重复命中重试预算；在返回响应前取消，重试等待必须被中断。
            cts.Cancel();
            return Task.FromResult(Json(DegenerateBody));
        });

        var result = await provider.ExecuteNonStreamingRequestAsync(RawMessages(), 64, "", true, cts.Token);

        Assert.Equal("Cancelled", result.EndReason.ToString());
        Assert.Equal(1, handler.CallCount);
    }

    [Fact]
    public async Task NonStreaming_InvalidJson_KeepsLegacyRetryBudget()
    {
        using var cts = new CancellationTokenSource();

        var (provider, handler) = Install(CloudUrl, (_, _, _) =>
            Task.FromResult(Json("not-json")));

        await provider.ExecuteNonStreamingRequestAsync(RawMessages(), 64, "", true, cts.Token);

        Assert.Equal(3, handler.CallCount);
    }

    [Fact]
    public async Task NonStreaming_DegenerateRepetition_KeepsLegacyRetryBudget()
    {
        using var cts = new CancellationTokenSource();

        var (provider, handler) = Install(CloudUrl, (_, _, _) =>
            Task.FromResult(Json(DegenerateBody)));

        await provider.ExecuteNonStreamingRequestAsync(RawMessages(), 64, "", true, cts.Token);

        Assert.Equal(3, handler.CallCount);
    }

    // ── LOCAL-004-R2 + LOCAL-005 新路径：本地降级进入非流式重试循环后的取消 / 超时 ──

    [Fact]
    public async Task LocalDegradePath_Timeout_ReturnsTimeoutWithOneNonStreamingAttempt()
    {
        using var cts = new CancellationTokenSource();

        var (provider, handler) = Install(LocalUrl, (_, token, call) =>
            call == 1
                ? Task.FromResult(Json(StreamUnsupportedBody, HttpStatusCode.BadRequest))
                : Task.FromException<HttpResponseMessage>(new OperationCanceledException(token)));

        var received = new List<string>();
        var result = await provider.RunStreamingChatInference("system", Messages(), received.Add, cts.Token);

        Assert.Equal("Timeout", result.EndReason.ToString());
        Assert.Equal(2, handler.CallCount);
        Assert.Contains("\"stream\":false", handler.Bodies[1]);
        Assert.Empty(received);
    }

    [Fact]
    public async Task LocalDegradePath_CallerCancel_ReturnsCancelledWithOneNonStreamingAttempt()
    {
        using var cts = new CancellationTokenSource();

        var (provider, handler) = Install(LocalUrl, (_, token, call) =>
        {
            if (call == 1)
            {
                return Task.FromResult(Json(StreamUnsupportedBody, HttpStatusCode.BadRequest));
            }

            cts.Cancel();
            return Task.FromException<HttpResponseMessage>(new OperationCanceledException(token));
        });

        var received = new List<string>();
        var result = await provider.RunStreamingChatInference("system", Messages(), received.Add, cts.Token);

        Assert.Equal("Cancelled", result.EndReason.ToString());
        Assert.Equal(2, handler.CallCount);
        Assert.Empty(received);
    }

    // ── 测试脚手架 ──

    private delegate Task<HttpResponseMessage> StubBehavior(
        HttpRequestMessage request, CancellationToken token, int call);

    private static IReadOnlyList<LlmChatMessage> Messages() =>
        new[] { new LlmChatMessage("user", "hello") };

    /// <summary>非流式执行器的 messages 形参（List&lt;object&gt;），供 ExecuteNonStreamingRequestAsync 直连。</summary>
    private static List<object> RawMessages() =>
        new List<object> { new { role = "user", content = "hello" } };

    private (LlmOAICompatible Provider, StubHandler Handler) Install(
        string baseUrl,
        StubBehavior behavior)
    {
        // ResolveParameters 需要可写的 Config 与非空 SMonitor（与其他 Provider 契约测试一致）。
        ModEntry.Config = new ModConfig { QueryTimeout = 5 };
        ModEntry.SMonitor = new FakeMonitor();

        var handler = new StubHandler(behavior);
        SharedHttpClientField.SetValue(null, new HttpClient(handler));

        return (new LlmOAICompatible("local-key", baseUrl, "local-model"), handler);
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK)
        => new HttpResponseMessage(code)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };

    private static HttpResponseMessage Sse(string body)
        => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "text/event-stream")
        };

    private static HttpResponseMessage SseInterrupted(string prefix)
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(new InterruptedStream(prefix))
        };
        response.Content.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
        return response;
    }

    /// <summary>内存 HTTP 桩：记录请求体次数与内容，按用例注入行为。</summary>
    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly StubBehavior _behavior;
        private readonly List<string> _bodies = new List<string>();
        private int _callCount;

        public StubHandler(StubBehavior behavior) => _behavior = behavior;

        public int CallCount => Volatile.Read(ref _callCount);
        public IReadOnlyList<string> Bodies => _bodies;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            int call = Interlocked.Increment(ref _callCount);

            string body = request.Content == null
                ? string.Empty
                : await request.Content.ReadAsStringAsync();

            lock (_bodies)
            {
                _bodies.Add(body);
            }

            return await _behavior(request, cancellationToken, call);
        }
    }

    /// <summary>先吐一部分 SSE，再模拟连接中断，用于验证“已下发 token 后不得重发”。</summary>
    private sealed class InterruptedStream : Stream
    {
        private readonly byte[] _bytes;
        private int _position;

        public InterruptedStream(string prefix) => _bytes = Encoding.UTF8.GetBytes(prefix);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_position < _bytes.Length) return ReadCore(buffer, count);
            throw new IOException("simulated SSE connection reset");
        }

        public override Task<int> ReadAsync(
            byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            return _position < _bytes.Length
                ? Task.FromResult(ReadCore(buffer, count))
                : Task.FromException<int>(new IOException("simulated SSE connection reset"));
        }

        private int ReadCore(byte[] buffer, int count)
        {
            int n = Math.Min(count, _bytes.Length - _position);
            Buffer.BlockCopy(_bytes, _position, buffer, 0, n);
            _position += n;
            return n;
        }
    }
}
