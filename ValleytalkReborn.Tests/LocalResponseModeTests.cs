// LocalResponseModeTests.cs
// LOCAL-004 — 本地响应模式（Auto / Streaming / NonStreaming）与 SSE 降级契约测试。
// 覆盖：Auto 成功流式、Auto 非 SSE 完整 JSON 就地解析（不重发）、
// Auto 遇到“明确不支持流式”最多降级一次、SSE 已下发 token 后断连不重发、
// Streaming 模式失败不降级、4xx 无流式信号不降级、NonStreaming 只发一次非流式请求、
// 取消不触发降级请求、云端端点无视 LocalResponseMode、非法配置值归一化。
//
// 纯内存测试：通过反射把 Llm 的共享 HttpClient 换成内存 HttpMessageHandler 桩，
// 不发起真实网络请求；用例结束还原 Config 与 HttpClient。互斥依赖 assemblies 级
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

    // ── 默认配置 ──

    [Fact]
    public void DefaultConfig_UsesAutoMode()
    {
        Assert.Equal(LocalResponseMode.Auto, new ModConfig().LocalResponseMode);
    }

    [Fact]
    public void ValidateDialogueConfig_NormalizesInvalidModeToAuto()
    {
        var config = new ModConfig { LocalResponseMode = (LocalResponseMode)99 };

        config.ValidateDialogueConfig(null);

        Assert.Equal(LocalResponseMode.Auto, config.LocalResponseMode);
    }

    // ── Auto：成功的 SSE  ──

    [Fact]
    public async Task Auto_SseSuccess_DeliversTokensWithoutFallback()
    {
        var (provider, handler) = Install(LocalResponseMode.Auto, LocalUrl, (_, _, _) =>
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

    // ── Auto：响应不是 SSE → 解析已收到的完整 JSON，不重复发送请求 ──

    [Fact]
    public async Task Auto_NonSseJsonBody_ParsesReceivedBodyWithoutSecondRequest()
    {
        var (provider, handler) = Install(LocalResponseMode.Auto, LocalUrl, (_, _, _) =>
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
    public async Task Auto_NonSseUnparsableBody_ReturnsExplicitFailure()
    {
        var (provider, handler) = Install(LocalResponseMode.Auto, LocalUrl, (_, _, _) =>
            Task.FromResult(Json("<html>not json</html>")));

        var received = new List<string>();
        using var cts = new CancellationTokenSource();

        var result = await provider.RunStreamingChatInference("system", Messages(), received.Add, cts.Token);

        Assert.False(result.IsSuccess);
        Assert.Equal(502, result.ResponseCode);
        Assert.Empty(received);
        Assert.Equal(1, handler.CallCount);
    }

    // ── Auto：服务明确拒绝 SSE → 恰好降级一次非流式 ──

    [Fact]
    public async Task Auto_StreamUnsupported_DegradesExactlyOnceToNonStreaming()
    {
        var (provider, handler) = Install(LocalResponseMode.Auto, LocalUrl, (_, _, call) =>
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

    // ── Auto：4xx 但没有明确的流式不支持信号 → 不降级，返回原始失败 ──

    [Fact]
    public async Task Auto_Http400WithoutStreamSignal_ReturnsOriginalFailure()
    {
        var (provider, handler) = Install(LocalResponseMode.Auto, LocalUrl, (_, _, _) =>
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
    public async Task Auto_PartialTokensThenBrokenStream_DoesNotResend()
    {
        var (provider, handler) = Install(LocalResponseMode.Auto, LocalUrl, (_, _, _) =>
            Task.FromResult(SseInterrupted(
                "data: {\"choices\":[{\"delta\":{\"content\":\"Half\"}}]}\n\n")));

        var received = new List<string>();
        using var cts = new CancellationTokenSource();

        var result = await provider.RunStreamingChatInference("system", Messages(), received.Add, cts.Token);

        Assert.False(result.IsSuccess);
        Assert.Equal(new[] { "Half" }, received);
        Assert.Equal(1, handler.CallCount);
    }

    // ── Streaming：失败即失败，不做非流式重试 ──

    [Fact]
    public async Task Streaming_StreamUnsupported_ReturnsFailureWithoutFallback()
    {
        var (provider, handler) = Install(LocalResponseMode.Streaming, LocalUrl, (_, _, _) =>
            Task.FromResult(Json(StreamUnsupportedBody, HttpStatusCode.BadRequest)));

        var received = new List<string>();
        using var cts = new CancellationTokenSource();

        var result = await provider.RunStreamingChatInference("system", Messages(), received.Add, cts.Token);

        Assert.False(result.IsSuccess);
        Assert.Equal(400, result.ResponseCode);
        Assert.Empty(received);
        Assert.Equal(1, handler.CallCount);
    }

    // ── NonStreaming：不发 SSE，成功后一次性下发完整文本 ──

    [Fact]
    public async Task NonStreaming_NeverRequestsSse_AndDeliversFullTextOnce()
    {
        var (provider, handler) = Install(LocalResponseMode.NonStreaming, LocalUrl, (_, _, _) =>
            Task.FromResult(Json("{\"choices\":[{\"message\":{\"content\":\"Complete answer\"}}]}")));

        var received = new List<string>();
        using var cts = new CancellationTokenSource();

        var result = await provider.RunStreamingChatInference("system", Messages(), received.Add, cts.Token);

        Assert.True(result.IsSuccess);
        Assert.Equal("Complete answer", result.Text);
        Assert.Equal(new[] { "Complete answer" }, received);
        Assert.Equal(1, handler.CallCount);
        Assert.Contains("\"stream\":false", handler.Bodies[0]);
    }

    // ── 取消：不得触发降级请求 ──

    [Fact]
    public async Task Auto_CancelledDuringStreamRequest_DoesNotSendFallbackRequest()
    {
        using var cts = new CancellationTokenSource();

        var (provider, handler) = Install(LocalResponseMode.Auto, LocalUrl, (_, token, _) =>
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
    public async Task Auto_CancelledBeforeFallback_DoesNotSendNonStreamingRequest()
    {
        // 第一次调用明确拒绝 SSE，同时在返回响应后立刻取消；若实现仍然降级，
        // 第二次调用会返回成功文本，即可暴露契约违规。
        using var cts = new CancellationTokenSource();

        var (provider, handler) = Install(LocalResponseMode.Auto, LocalUrl, (_, _, call) =>
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

    // ── 云端端点：LocalResponseMode 不得改变既有行为 ──

    [Fact]
    public async Task CloudEndpoint_IgnoresLocalResponseMode()
    {
        var (provider, handler) = Install(LocalResponseMode.NonStreaming, CloudUrl, (_, _, _) =>
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

    // ── 测试脚手架 ──

    private delegate Task<HttpResponseMessage> StubBehavior(
        HttpRequestMessage request, CancellationToken token, int call);

    private static IReadOnlyList<LlmChatMessage> Messages() =>
        new[] { new LlmChatMessage("user", "hello") };

    private (LlmOAICompatible Provider, StubHandler Handler) Install(
        LocalResponseMode mode,
        string baseUrl,
        StubBehavior behavior)
    {
        // ResolveParameters 需要可写的 Config 与非空 SMonitor（与其他 Provider 契约测试一致）。
        ModEntry.Config = new ModConfig { QueryTimeout = 5, LocalResponseMode = mode };
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
