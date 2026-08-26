using System;
using System.Threading;
using System.Threading.Tasks;

namespace ValleytalkReborn;

/// <summary>
/// LLM 请求网关：统一并发限制、超时和日志。
/// 第一版只统一并发限制、超时和日志，不实现优先级队列、请求抢占或自动 fallback。
/// Gateway 不得访问 Game1、NPC 或其他游戏对象。
/// </summary>
internal sealed class LlmRequestGateway
{
    private readonly SemaphoreSlim _concurrency =
        new SemaphoreSlim(1, 1);

    private readonly int _timeoutSeconds;

    /// <summary>
    /// 创建 LLM 请求网关。
    /// </summary>
    /// <param name="timeoutSeconds">超时秒数。传入 0 或负数则使用默认 30 秒。</param>
    internal LlmRequestGateway(int timeoutSeconds = 30)
    {
        _timeoutSeconds = timeoutSeconds > 0 ? timeoutSeconds : 30;
    }

    /// <summary>
     /// 执行 LLM 推理请求。
     /// </summary>
    internal async Task<LlmResponse> ExecuteAsync(
        string source,
        string systemPrompt,
        string userPrompt,
        CancellationToken cancellationToken)
    {
        await _concurrency.WaitAsync(cancellationToken);

        try
        {
            var llm = Llm.Instance;

            if (llm == null)
                return null;

            using var timeoutCts =
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken);

            timeoutCts.CancelAfter(TimeSpan.FromSeconds(_timeoutSeconds));

            var response = await llm.RunInference(
                systemPrompt,
                "",
                "",
                userPrompt,
                "[")
                .WaitAsync(timeoutCts.Token);

            if (response == null)
                return null;

            ModEntry.SMonitor?.Log(
                $"[{source}] LLM output:\n{response.Text}",
                StardewModdingAPI.LogLevel.Debug);

            return response;
        }
        finally
        {
            _concurrency.Release();
        }
    }
}
