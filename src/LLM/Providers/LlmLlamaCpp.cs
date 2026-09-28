using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using Newtonsoft.Json; 
using Newtonsoft.Json.Linq; 
using System.Threading;
using System.Threading.Tasks;
using ValleytalkReborn.Platform;

namespace ValleytalkReborn;

internal class LlmLlamaCpp : Llm
{
    /// <summary>重试前的退避等待，受取消令牌中断。</summary>
    private const int RetryDelayMs = 1000;

    public LlmLlamaCpp(string url, string promptFormat)
    {
        this.url = UrlHelper.EnsureScheme(url);
        PromptFormat = promptFormat;
    }

    public string PromptFormat { get; }
    public override string ExtraInstructions => "Include only the new line and any responses in the output, no descriptions or explanations.";

    public override bool IsHighlySensoredModel => false;

    internal string BuildPrompt(string systemPromptString, string promptString, string responseStart = "")
    {
        return PromptFormat
            .Replace("{system}", systemPromptString)
            .Replace("{prompt}", promptString)
            .Replace("{response_start}", responseStart);
    }

    /// <summary>
    /// 非流式入口：保持既有调用链无取消令牌的语义，仅由 QueryTimeout 兜底。
    /// </summary>
    internal override async Task<LlmResponse> RunInference(
        string systemPromptString, string gameCacheString, string npcCacheString, 
        string promptString, string responseStart = "", int n_predict = 2048, 
        string cacheContext = "", bool allowRetry = true)
    {
        // LOCAL-005：本地端点（回环 / 私网）请求进入进程内并发闸门；云端地址拿到空租约。
        LocalRequestLease lease = await LocalRequestThrottle.AcquireAsync(url, CancellationToken.None);
        var telemetry = new LlmTrafficLogger.LlmRequestTelemetry(nameof(LlmLlamaCpp), url, null, string.Empty);

        using (lease)
        {
            telemetry.QueueWaitMs = lease.QueueWaitMs;
            var totalWatch = System.Diagnostics.Stopwatch.StartNew();

            LlmResponse result = await RunInferenceCoreAsync(
                systemPromptString, gameCacheString, npcCacheString,
                promptString, responseStart, n_predict, CancellationToken.None, allowRetry, telemetry);

            totalWatch.Stop();
            telemetry.TotalMs = totalWatch.ElapsedMilliseconds;
            telemetry.OutputChars = result?.Text?.Length ?? 0;
            telemetry.Log();

            return result;
        }
    }

    /// <summary>
    /// 可取消的非流式入口（B2）：令牌贯穿闸门排队与核心（HTTP / 重试等待），
    /// 取消真正中止请求并即时释放闸门租约，不再"弃等"。遥测语义与 RunInference 一致。
    /// </summary>
    internal override async Task<LlmResponse> RunInferenceAsync(
        string systemPromptString, string gameCacheString, string npcCacheString,
        string promptString, CancellationToken ct,
        string responseStart = "", int n_predict = 2048,
        string cacheContext = "", bool allowRetry = true)
    {
        // LOCAL-005：本地端点（回环 / 私网）请求进入进程内并发闸门；云端地址拿到空租约。
        LocalRequestLease lease;

        try
        {
            lease = await LocalRequestThrottle.AcquireAsync(url, ct);
        }
        catch (OperationCanceledException)
        {
            // BOUNDARY：排队阶段被取消 —— 不进入 HTTP，不记为服务故障。
            Log.Debug($"[LlmLlamaCpp] Local request cancelled while queued; no HTTP sent. endpoint={url}");
            return LlmResponse.Cancelled();
        }

        var telemetry = new LlmTrafficLogger.LlmRequestTelemetry(nameof(LlmLlamaCpp), url, null, string.Empty);

        using (lease)
        {
            telemetry.QueueWaitMs = lease.QueueWaitMs;
            var totalWatch = System.Diagnostics.Stopwatch.StartNew();

            LlmResponse result = await RunInferenceCoreAsync(
                systemPromptString, gameCacheString, npcCacheString,
                promptString, responseStart, n_predict, ct, allowRetry, telemetry);

            totalWatch.Stop();
            telemetry.TotalMs = totalWatch.ElapsedMilliseconds;
            telemetry.OutputChars = result?.Text?.Length ?? 0;
            telemetry.Log();

            return result;
        }
    }

    /// <summary>
    /// 流式入口：llama.cpp 不做 SSE 增量解析，复用非流式核心，成功时把完整文本一次性交给回调。
    /// 调用方令牌贯穿 HTTP 与重试等待。
    /// </summary>
    internal override async Task<LlmResponse> RunStreamingInference(
        string systemPromptString,
        string gameCacheString,
        string npcCacheString,
        string promptString,
        Action<string> onToken,
        CancellationToken ct,
        string responseStart = "",
        int n_predict = 2048,
        string cacheContext = "")
    {
        // LOCAL-005：本地端点（回环 / 私网）请求进入进程内并发闸门；云端地址拿到空租约。
        LocalRequestLease lease;

        try
        {
            lease = await LocalRequestThrottle.AcquireAsync(url, ct);
        }
        catch (OperationCanceledException)
        {
            // BOUNDARY：排队阶段被取消 —— 不进入 HTTP，不记为服务故障。
            Log.Debug($"[LlmLlamaCpp] Local request cancelled while queued; no HTTP sent. endpoint={url}");
            return LlmResponse.Cancelled();
        }

        var telemetry = new LlmTrafficLogger.LlmRequestTelemetry(nameof(LlmLlamaCpp), url, null, string.Empty);

        using (lease)
        {
            telemetry.QueueWaitMs = lease.QueueWaitMs;
            var totalWatch = System.Diagnostics.Stopwatch.StartNew();

            var result = await RunInferenceCoreAsync(
                systemPromptString, gameCacheString, npcCacheString,
                promptString, responseStart, n_predict, ct, allowRetry: true, telemetry);

            totalWatch.Stop();
            telemetry.TotalMs = totalWatch.ElapsedMilliseconds;
            telemetry.OutputChars = result?.Text?.Length ?? 0;
            telemetry.Log();

            if (result.IsSuccess && !string.IsNullOrWhiteSpace(result.Text))
            {
                onToken(result.Text);
            }

            return result;
        }
    }

    /// <summary>
    /// 连接失败判定：桌面 HttpClient 抛 HttpRequestException；
    /// Android 路径由 NetworkHelper 把 HTTP 异常包裹成 InvalidOperationException。
    /// </summary>
    private static bool IsConnectionFailure(Exception ex)
    {
        return ex is HttpRequestException
            || (AndroidHelper.IsAndroid && ex is InvalidOperationException);
    }

    /// <summary>
    /// 可取消的推理核心：
    /// 1) 链接调用方令牌与 QueryTimeout，贯穿 HTTP 与重试等待；
    /// 2) 取消 / 超时直接返回，不进入重试；
    /// 3) 仅连接错误、HTTP 429 与 5xx 消耗重试预算；其余错误立即显式失败。
    /// </summary>
    private async Task<LlmResponse> RunInferenceCoreAsync(
        string systemPromptString,
        string gameCacheString,
        string npcCacheString,
        string promptString,
        string responseStart,
        int n_predict,
        CancellationToken ct,
        bool allowRetry,
        LlmTrafficLogger.LlmRequestTelemetry telemetry)
    {
        promptString = gameCacheString + npcCacheString + promptString;
        var fullPrompt = BuildPrompt(systemPromptString, promptString, responseStart);

        if (AndroidHelper.IsAndroid && !NetworkHelper.IsNetworkAvailable())
        {
            throw new InvalidOperationException("Network not available");
        }

        int attemptsRemaining = allowRetry ? 2 : 1;
        int attempt = 0;
        string lastError = "llama.cpp request failed and the retry budget was exhausted.";
        int lastStatusCode = 500;

        while (attemptsRemaining > 0)
        {
            attemptsRemaining--;
            bool exhausted = attemptsRemaining == 0;
            bool isRetry = attempt++ > 0;
            int statusCode = 0;
            telemetry.Attempt = attempt;

            // 调用方令牌与 QueryTimeout 链接后进入 HTTP 与重试等待，二者都能被中断。
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linkedCts.CancelAfter(TimeSpan.FromSeconds(ModEntry.Config.QueryTimeout));
            var linkedToken = linkedCts.Token;

            try
            {
                ct.ThrowIfCancellationRequested();

                if (isRetry)
                {
                    await Task.Delay(RetryDelayMs, linkedToken);
                }

                var jsonData = JsonConvert.SerializeObject(new
                {
                    prompt = fullPrompt,
                    n_predict = n_predict,
                    stream = false,
                    temperature = n_predict == 1 ? 0 : 0.9,
                    top_p = 0.9,
                    min_p = 0.05,
                    repeat_penalty = 1.05,
                });

                string responseString;

                if (AndroidHelper.IsAndroid)
                {
                    responseString = await NetworkHelper.MakeRequestAsync(url, jsonData, linkedToken);

                    if (string.IsNullOrWhiteSpace(responseString))
                    {
                        Log.Error($"[LlmLlamaCpp] Empty response stage=network-helper endpoint={url}");
                        return new LlmResponse("llama.cpp returned an empty response.", 502);
                    }
                }
                else
                {
                    using var jsonContent = new StringContent(jsonData, Encoding.UTF8, "application/json");
                    using var response = await SharedHttpClient.PostAsync(url, jsonContent, linkedToken);

                    statusCode = (int)response.StatusCode;
                    telemetry.StatusCode = statusCode;
                    responseString = await response.Content.ReadAsStringAsync(linkedToken);

                    if (!response.IsSuccessStatusCode)
                    {
                        lastStatusCode = statusCode;
                        lastError = $"llama.cpp request failed with HTTP {statusCode}.";

                        bool retryable = statusCode == 429 || statusCode >= 500;

                        if (retryable && !exhausted)
                        {
                            Log.Warning($"[LlmLlamaCpp] Retryable HTTP status stage=http endpoint={url} status={statusCode}");
                            continue;
                        }

                        Log.Warning($"[LlmLlamaCpp] Request rejected stage=http endpoint={url} status={statusCode} retryable={retryable} exhausted={exhausted}");
                        return new LlmResponse(lastError, statusCode);
                    }
                }

                var responseJson = JObject.Parse(responseString);
                AddToStats(responseJson["timings"] as JObject);

                var contentToken = responseJson["content"];
                if (string.IsNullOrWhiteSpace(contentToken?.ToString()))
                {
                    Log.Error($"[LlmLlamaCpp] Missing content stage=parse endpoint={url}");
                    return new LlmResponse("llama.cpp response contained no content.", 502);
                }

                string content = contentToken.ToString();
                telemetry.OutputChars = content.Length;
                return new LlmResponse(content);
            }
            catch (JsonException ex)
            {
                Log.Error($"[LlmLlamaCpp] Malformed JSON stage=parse endpoint={url} exception={ex.GetType().Name}: {ex.Message}");
                return new LlmResponse("llama.cpp returned malformed JSON.", 502);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                Log.Debug($"[LlmLlamaCpp] Cancelled by caller stage=inference endpoint={url}");
                return LlmResponse.Cancelled();
            }
            catch (OperationCanceledException)
            {
                Log.Warning($"[LlmLlamaCpp] Query timeout stage=inference endpoint={url} timeout={ModEntry.Config.QueryTimeout}s");
                return LlmResponse.Timeout();
            }
            catch (TimeoutException)
            {
                Log.Warning($"[LlmLlamaCpp] Query timeout stage=network-helper endpoint={url} timeout={ModEntry.Config.QueryTimeout}s");
                return LlmResponse.Timeout();
            }
            catch (Exception ex) when (IsConnectionFailure(ex))
            {
                lastError = "llama.cpp connection failed: " + ex.Message;
                lastStatusCode = 500;
                Log.Warning($"[LlmLlamaCpp] Connection failure stage=inference endpoint={url} exception={ex.GetType().Name}: {ex.Message}");
                continue;
            }
            catch (Exception ex)
            {
                Log.Error($"[LlmLlamaCpp] Unexpected failure stage=inference endpoint={url} exception={ex.GetType().Name}: {ex.Message}");
                return new LlmResponse("llama.cpp inference failed: " + ex.Message, 500);
            }
        }

        Log.Error($"[LlmLlamaCpp] Retry budget exhausted stage=inference endpoint={url} status={lastStatusCode}");
        return new LlmResponse(lastError, lastStatusCode);
    }
    
    internal override Dictionary<string, double>[] RunInferenceProbabilities(string fullPrompt, int n_predict = 1)
    {
        try
        {
            var jsonContent = new StringContent(
                JsonConvert.SerializeObject(new 
                {
                    prompt = fullPrompt,
                    n_predict = n_predict,
                    stream = false,
                    temperature = 0.8,
                    top_p = 0.88,
                    min_p = 0.05,
                    cache_prompt = true,
                    n_probs = 10
                }),
                Encoding.UTF8,
                "application/json"
            );

            // 🌟 修复：直接使用 HttpClient 的异步 API，彻底移除外层 Task.Run(...) 嵌套，防止同步/异步混用卡死
            var response = SharedHttpClient.PostAsync(url, jsonContent).GetAwaiter().GetResult();
            var responseString = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            var responseJson = JObject.Parse(responseString);
            
            var token_stats = responseJson["timings"] as JObject;
            AddToStats(token_stats);

            var result = new List<Dictionary<string, double>>();
            var probsToken = responseJson["completion_probabilities"]; 
            if (probsToken is JArray probsArray) 
            {
                foreach (var prob in probsArray)
                {
                    var probDict = new Dictionary<string, double>();
                    var innerProbsToken = prob["probs"];
                    if (innerProbsToken is JArray innerProbsArray) 
                    {
                        foreach (var prop in innerProbsArray)
                        {
                            var token = prop["tok_str"]?.ToString(); 
                            var probability = prop["prob"]?.Value<double>(); 
                            if (token != null && probability.HasValue)
                            {
                                probDict[token] = probability.Value;
                            }
                        }
                    }
                    result.Add(probDict);
                }
            }
            return result.ToArray();
        }
        catch (Exception ex)
        {
            Log.Error(ex.Message);
            return Array.Empty<Dictionary<string, double>>();
        }
    }
}