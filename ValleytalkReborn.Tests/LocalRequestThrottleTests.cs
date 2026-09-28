// LocalRequestThrottleTests.cs
// LOCAL-005 — 本地请求并发闸门与遥测回归测试。
//
// 纯内存：HttpClient 由反射注入 Fake Handler（无真实网络 / 无 DNS / 无文件 I/O），
// 无存档、无 ModData、无多人同步、无 Harmony。
//
// 覆盖：本地并发上限、排队取消不进入 HTTP、释放后闸门可继续复用、云端不受限、
// 遥测字段可区分 queue wait / TTFT / total / retry / status、遥测不含敏感信息。

using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using StardewModdingAPI;
using StardewModdingAPI.Framework.Logging;
using ValleytalkReborn;
using Xunit;

[Collection("StaticGlobalStateCollection")]
public class LocalRequestThrottleTests : IDisposable
{
    private const string SecretKey = "sk-LOCAL-SECRET-DO-NOT-LOG";
    private const string SecretPrompt = "PROMPT-CANARY-DO-NOT-LOG";
    private const string LocalBody = "{\"choices\":[{\"message\":{\"content\":\"hello from local\"}}]}";

    private readonly CapturingMonitor _monitor = new CapturingMonitor();
    private readonly ModConfig _originalConfig;
    private readonly IMonitor _originalSMonitor;
    private readonly HttpClient _originalHttpClient;

    public LocalRequestThrottleTests()
    {
        TestEnvironment.InstallHeadlessContext();

        _originalConfig = ModEntry.Config;
        _originalSMonitor = ModEntry.SMonitor;
        _originalHttpClient = (HttpClient)SharedHttpClientField.GetValue(null);

        ModEntry.SMonitor = _monitor;
        ModEntry.Config = new ModConfig
        {
            LocalMaxConcurrentRequests = 1,
            QueryTimeout = 30,
            Debug = false
        };
        Log.Initialize(_monitor);
    }

    public void Dispose()
    {
        SharedHttpClientField.SetValue(null, _originalHttpClient);
        ModEntry.Config = _originalConfig;
        ModEntry.SMonitor = _originalSMonitor;
        Log.Cleanup();
    }

    private static FieldInfo SharedHttpClientField =>
        typeof(Llm).GetField("_sharedHttpClient", BindingFlags.Static | BindingFlags.NonPublic);

    private static LlmOAICompatible LocalProvider() =>
        new LlmOAICompatible(SecretKey, "http://127.0.0.1:1234/v1", "local-model");

    private static LlmOpenAi CloudProvider() => new LlmOpenAi(SecretKey, "gpt-4o");

    private static List<object> Messages() =>
        new List<object> { new { role = "user", content = SecretPrompt } };

    private static Task<LlmResponse> LocalRequest(LlmOAICompatible provider, CancellationToken ct) =>
        provider.ExecuteNonStreamingRequestAsync(Messages(), 64, LlmContextTypes.Bark, true, ct);

    private static CountingHandler InstallFakeHttp(
        TimeSpan delay,
        HttpStatusCode status = HttpStatusCode.OK,
        string body = LocalBody)
    {
        var handler = new CountingHandler(delay, status, body);
        SharedHttpClientField.SetValue(null, new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(30)
        });
        return handler;
    }

    private static async Task<T> WithTimeout<T>(Task<T> task, int milliseconds)
    {
        Task completed = await Task.WhenAny(task, Task.Delay(milliseconds));

        if (completed != task)
        {
            throw new TimeoutException(
                $"Operation did not complete within {milliseconds}ms — the local request gate is likely leaking permits.");
        }

        return await task;
    }

    // ── 验收 1：LocalMaxConcurrentRequests=1 时同一时间最多一个本地 HTTP 请求 ──

    [Fact]
    public async Task LocalRequests_ConcurrencyOne_NeverOverlapInHttp()
    {
        CountingHandler handler = InstallFakeHttp(TimeSpan.FromMilliseconds(150));
        LlmOAICompatible provider = LocalProvider();

        var tasks = new List<Task<LlmResponse>>();
        for (int i = 0; i < 3; i++)
        {
            tasks.Add(LocalRequest(provider, CancellationToken.None));
        }

        LlmResponse[] results = await WithTimeout(Task.WhenAll(tasks), 15000);

        Assert.Equal(3, handler.Total);
        Assert.Equal(1, handler.MaxObserved);
        Assert.All(results, r => Assert.True(r.IsSuccess));
    }

    // ── 验收 1（续）：配置改为 2 后闸门容量随之重建 ──

    [Fact]
    public async Task LocalRequests_ConcurrencyTwo_AllowsTwoInFlight()
    {
        ModEntry.Config.LocalMaxConcurrentRequests = 2;

        CountingHandler handler = InstallFakeHttp(TimeSpan.FromMilliseconds(200));
        LlmOAICompatible provider = LocalProvider();

        var tasks = new List<Task<LlmResponse>>();
        for (int i = 0; i < 4; i++)
        {
            tasks.Add(LocalRequest(provider, CancellationToken.None));
        }

        await WithTimeout(Task.WhenAll(tasks), 15000);

        Assert.Equal(4, handler.Total);
        Assert.Equal(2, handler.MaxObserved);
    }

    // ── 验收 2：排队请求被取消后不进入 HTTP 层 ──

    [Fact]
    public async Task QueuedLocalRequest_Cancelled_DoesNotReachHttp()
    {
        CountingHandler handler = InstallFakeHttp(TimeSpan.FromMilliseconds(400));
        LlmOAICompatible provider = LocalProvider();

        Task<LlmResponse> holder = LocalRequest(provider, CancellationToken.None);

        using var cts = new CancellationTokenSource();
        Task<LlmResponse> queued = LocalRequest(provider, cts.Token);

        // holder 持有唯一许可，queued 此刻必然在排队；排队中取消。
        await Task.Delay(120);
        cts.Cancel();

        LlmResponse cancelled = await WithTimeout(queued, 10000);
        await WithTimeout(holder, 10000);

        Assert.Equal(1, handler.Total);
        Assert.False(cancelled.IsSuccess);
        Assert.Equal("Cancelled", cancelled.EndReason.ToString());
    }

    // ── 验收 3：成功 / 失败 / 取消之后闸门都能继续使用 ──

    [Fact]
    public async Task GateStaysUsable_AfterSuccessFailureAndCancellation()
    {
        LlmOAICompatible provider = LocalProvider();

        InstallFakeHttp(TimeSpan.FromMilliseconds(20));
        LlmResponse success = await WithTimeout(LocalRequest(provider, CancellationToken.None), 10000);
        Assert.True(success.IsSuccess);

        InstallFakeHttp(TimeSpan.FromMilliseconds(20), HttpStatusCode.InternalServerError, "{\"error\":\"boom\"}");
        LlmResponse failure = await WithTimeout(LocalRequest(provider, CancellationToken.None), 10000);
        Assert.False(failure.IsSuccess);

        InstallFakeHttp(TimeSpan.FromMilliseconds(300));
        Task<LlmResponse> holder = LocalRequest(provider, CancellationToken.None);
        using var cts = new CancellationTokenSource();
        Task<LlmResponse> queued = LocalRequest(provider, cts.Token);
        await Task.Delay(80);
        cts.Cancel();

        LlmResponse cancelled = await WithTimeout(queued, 10000);
        await WithTimeout(holder, 10000);
        Assert.Equal("Cancelled", cancelled.EndReason.ToString());

        InstallFakeHttp(TimeSpan.FromMilliseconds(20));
        LlmResponse afterAll = await WithTimeout(LocalRequest(provider, CancellationToken.None), 10000);
        Assert.True(afterAll.IsSuccess);
    }

    // ── 验收 4：云端 Provider 不受本地信号量影响 ──

    [Fact]
    public async Task CloudRequests_AreNotThrottled()
    {
        CountingHandler handler = InstallFakeHttp(TimeSpan.FromMilliseconds(200));
        LlmOpenAi provider = CloudProvider();

        var tasks = new List<Task<LlmResponse>>();
        for (int i = 0; i < 3; i++)
        {
            tasks.Add(provider.ExecuteNonStreamingRequestAsync(
                Messages(), 64, LlmContextTypes.Bark, true, CancellationToken.None));
        }

        await WithTimeout(Task.WhenAll(tasks), 15000);

        Assert.Equal(3, handler.Total);
        Assert.Equal(3, handler.MaxObserved);
    }

    // ── 验收 5 / 6：遥测字段可区分，且不含 Key / Authorization / Prompt / 响应正文 ──

    [Fact]
    public async Task RequestTelemetry_ExposesTimingsAndRedactsSecrets()
    {
        InstallFakeHttp(TimeSpan.FromMilliseconds(30));
        LlmOAICompatible provider = LocalProvider();

        LlmResponse result = await WithTimeout(LocalRequest(provider, CancellationToken.None), 10000);
        Assert.True(result.IsSuccess);

        string telemetry = Assert.Single(_monitor.Messages, m => m.Contains("[LlmTraffic] REQUEST"));

        Assert.Contains("provider=LlmOAICompatible", telemetry);
        Assert.Contains("host=127.0.0.1", telemetry);
        Assert.Contains("model=local-model", telemetry);
        Assert.Contains("context=Bark", telemetry);
        Assert.Contains("attempt=", telemetry);
        Assert.Contains("status=200", telemetry);
        Assert.Contains("queueWaitMs=", telemetry);
        Assert.Contains("ttftMs=", telemetry);
        Assert.Contains("totalMs=", telemetry);
        Assert.Contains("chars=", telemetry);

        Assert.DoesNotContain(SecretKey, telemetry);
        Assert.DoesNotContain("Authorization", telemetry);
        Assert.DoesNotContain(SecretPrompt, telemetry);
        Assert.DoesNotContain("hello from local", telemetry);
    }

    // ── 失败路径：非法并发值 → Warn + 限制到 1~4，且不产生零容量闸门 ──

    [Fact]
    public void ValidateDialogueConfig_ClampsLocalMaxConcurrentRequests()
    {
        var config = new ModConfig { LocalMaxConcurrentRequests = 0 };
        config.ValidateDialogueConfig(_monitor);
        Assert.Equal(1, config.LocalMaxConcurrentRequests);

        config.LocalMaxConcurrentRequests = 9;
        config.ValidateDialogueConfig(_monitor);
        Assert.Equal(4, config.LocalMaxConcurrentRequests);
    }

    [Fact]
    public async Task InvalidConcurrency_ClampedWithoutZeroCapacityGate()
    {
        ModEntry.Config.LocalMaxConcurrentRequests = 0;

        LocalRequestLease lease = await WithTimeout(
            LocalRequestThrottle.AcquireAsync("http://127.0.0.1:1234/v1", CancellationToken.None), 5000);

        lease.Dispose();

        Assert.Contains(_monitor.Messages,
            m => m.Contains("[LocalRequestThrottle] LocalMaxConcurrentRequests=0"));
    }

    // ── 验收 7：Bark 网关发出的并发本地请求同样受闸门限制，不会无限堆积 ──

    [Fact]
    public async Task BarkGateway_ConcurrentLocalRequests_BoundedByGate()
    {
        CountingHandler handler = InstallFakeHttp(TimeSpan.FromMilliseconds(150));

        FieldInfo instanceField = typeof(Llm).GetField(
            "<Instance>k__BackingField", BindingFlags.Static | BindingFlags.NonPublic);
        object originalInstance = instanceField?.GetValue(null);
        instanceField?.SetValue(null, LocalProvider());

        try
        {
            var gateway = new LlmRequestGateway(30);

            var tasks = new List<Task<LlmResponse>>();
            for (int i = 0; i < 4; i++)
            {
                tasks.Add(gateway.ExecuteAsync(
                    LlmContextTypes.Bark, "SYS-CONTEXT", SecretPrompt, CancellationToken.None));
            }

            LlmResponse[] results = await WithTimeout(Task.WhenAll(tasks), 20000);

            Assert.Equal(4, handler.Total);
            Assert.Equal(1, handler.MaxObserved);
            Assert.All(results, r => Assert.True(r.IsSuccess));
        }
        finally
        {
            instanceField?.SetValue(null, originalInstance);
        }
    }

    /// <summary>
    /// 统计并发与总次数的 Fake HTTP Handler：只观察，不发起任何真实网络请求。
    /// </summary>
    private sealed class CountingHandler : HttpMessageHandler
    {
        private readonly TimeSpan _delay;
        private readonly HttpStatusCode _status;
        private readonly string _body;
        private readonly object _sync = new object();
        private int _inFlight;

        internal int MaxObserved { get; private set; }
        internal int Total { get; private set; }

        internal CountingHandler(TimeSpan delay, HttpStatusCode status, string body)
        {
            _delay = delay;
            _status = status;
            _body = body;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            int current = Interlocked.Increment(ref _inFlight);

            lock (_sync)
            {
                Total++;
                if (current > MaxObserved) MaxObserved = current;
            }

            try
            {
                await Task.Delay(_delay, cancellationToken);

                return new HttpResponseMessage(_status)
                {
                    Content = new StringContent(_body, Encoding.UTF8, "application/json")
                };
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }
    }

    /// <summary>收集所有日志行的 Monitor，用于遥测字段与脱敏断言。</summary>
    private sealed class CapturingMonitor : IMonitor
    {
        private readonly object _sync = new object();
        private readonly List<string> _messages = new List<string>();

        internal IReadOnlyList<string> Messages
        {
            get { lock (_sync) { return _messages.ToArray(); } }
        }

        public bool IsVerbose => false;

        public void Log(string message, LogLevel level)
        {
            lock (_sync) { _messages.Add(message ?? string.Empty); }
        }

        public void LogOnce(string message, LogLevel level) => Log(message, level);

        public void VerboseLog(string message) { }

        public void VerboseLog(ref VerboseLogStringHandler handler) { }
    }
}
