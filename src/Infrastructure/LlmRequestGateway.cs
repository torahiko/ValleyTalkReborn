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
    /// <summary>
    /// Result of an LLM gateway request, including the response and end reason.
    /// </summary>
    internal sealed class GatewayResult
    {
        internal LlmResponse Response { get; init; }
        internal DialogueModels.LlmRequestEndReason EndReason { get; init; }
        internal Exception Error { get; init; }
    }

    private readonly SemaphoreSlim _concurrency =
        new SemaphoreSlim(1, 1);

    private readonly int _timeoutSeconds;
    private Task<LlmResponse> _underlyingTask = Task.FromResult<LlmResponse>(null);

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

        bool released = false;
        try
        {
            var llm = Llm.Instance;

            if (llm == null)
                return null;

            using var timeoutCts =
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken);

            timeoutCts.CancelAfter(TimeSpan.FromSeconds(_timeoutSeconds));

            // Start the underlying inference task
            var inferenceTask = llm.RunInference(
                systemPrompt,
                "",
                "",
                userPrompt,
                "[");

            // Store the underlying task so we can track it
            _underlyingTask = inferenceTask;

            LlmResponse response;
            try
            {
                response = await inferenceTask.WaitAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
                // Timeout or cancellation: the underlying task may still be running.
                // We need to wait for it to complete in the background before
                // releasing the semaphore to prevent concurrent underlying requests.
                _ = Task.Run(async () =>
                {
                    try { await inferenceTask; }
                    catch { /* ignored */ }
                    finally
                    {
                        _concurrency.Release();
                        released = true;
                    }
                });

                return null;
            }

            if (response == null)
                return null;

            ModEntry.SMonitor?.Log(
                $"[{source}] LLM output:\n{response.Text}",
                StardewModdingAPI.LogLevel.Debug);

            return response;
        }
        finally
        {
            if (!released)
                _concurrency.Release();
        }
    }
}
