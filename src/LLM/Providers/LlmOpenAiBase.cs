using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using StardewValley;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Threading;
using System.Threading.Tasks;
using ValleytalkReborn.Platform;

namespace ValleytalkReborn
{
    /// <summary>
    /// 思考模式禁用策略枚举
    /// </summary>
    internal readonly struct ThinkingSuppressionPlan
    {
        public readonly bool UseThinkingDisabled;
        public readonly bool UseEnableThinking;
        public readonly bool UseChatTemplateKwargs;
        public readonly bool UseIncludeReasoning;
        public readonly bool UseReasoningEffortLow;
        public readonly bool UseOpenRouterReasoning;
        public readonly string Reason;

        public bool AnyApiSuppression =>
            UseThinkingDisabled || UseEnableThinking || UseChatTemplateKwargs ||
            UseIncludeReasoning || UseReasoningEffortLow || UseOpenRouterReasoning;

        public static readonly ThinkingSuppressionPlan Empty = default;

        public ThinkingSuppressionPlan(
            bool useThinkingDisabled, bool useEnableThinking, bool useChatTemplateKwargs,
            bool useIncludeReasoning, bool useReasoningEffortLow, bool useOpenRouterReasoning,
            string reason)
        {
            UseThinkingDisabled = useThinkingDisabled;
            UseEnableThinking = useEnableThinking;
            UseChatTemplateKwargs = useChatTemplateKwargs;
            UseIncludeReasoning = useIncludeReasoning;
            UseReasoningEffortLow = useReasoningEffortLow;
            UseOpenRouterReasoning = useOpenRouterReasoning;
            Reason = reason ?? string.Empty;
        }
    }

    /// <summary>
    /// LOCAL-005：本地（回环 / 私网）LLM 请求的进程内并发闸门。
    /// ① 仅对 UrlHelper.IsPrivateNetworkUrl 命中的端点生效；云端端点拿到的租约是空租约，零影响。
    /// ② 容量取自 ModEntry.Config.LocalMaxConcurrentRequests（1~4），变更时重建信号量；
    ///    旧信号量不 Dispose —— 在途请求仍持有它的 Release，Dispose 会让 Release 抛异常（与 Llm.RecreateHttpClient 同策略）。
    /// ③ 一律 WaitAsync + 调用方 CancellationToken，禁止 Wait / Result 同步阻塞。
    /// </summary>
    internal static class LocalRequestThrottle
    {
        internal const int MinConcurrency = 1;
        internal const int MaxConcurrency = 4;

        private static SemaphoreSlim _gate = new SemaphoreSlim(MinConcurrency, MaxConcurrency);
        private static int _capacity = MinConcurrency;
        private static int _lastWarnedRaw = int.MinValue;

        /// <summary>
        /// 取当前闸门：容量随配置变化重建。云端点返回空租约，不占用信号量。
        /// </summary>
        internal static async Task<LocalRequestLease> AcquireAsync(string url, CancellationToken ct)
        {
            if (!UrlHelper.IsPrivateNetworkUrl(url))
                return LocalRequestLease.None;

            SemaphoreSlim gate = ResolveGate();
            var waitWatch = System.Diagnostics.Stopwatch.StartNew();
            await gate.WaitAsync(ct);
            waitWatch.Stop();

            return new LocalRequestLease(gate, waitWatch.ElapsedMilliseconds);
        }

        private static SemaphoreSlim ResolveGate()
        {
            int configured = ResolveCapacity();

            if (configured == Volatile.Read(ref _capacity))
                return Volatile.Read(ref _gate);

            var created = new SemaphoreSlim(configured, MaxConcurrency);
            Interlocked.Exchange(ref _gate, created);
            Volatile.Write(ref _capacity, configured);

            return Volatile.Read(ref _gate);
        }

        private static int ResolveCapacity()
        {
            int configured = ModEntry.Config?.LocalMaxConcurrentRequests ?? MinConcurrency;

            if (configured < MinConcurrency || configured > MaxConcurrency)
            {
                int raw = configured;
                configured = Math.Clamp(configured, MinConcurrency, MaxConcurrency);

                if (Interlocked.Exchange(ref _lastWarnedRaw, raw) != raw)
                {
                    ModEntry.SMonitor?.Log(
                        $"[LocalRequestThrottle] LocalMaxConcurrentRequests={raw} is out of range; clamped to {configured}.",
                        StardewModdingAPI.LogLevel.Warn);
                }
            }

            return configured;
        }
    }

    /// <summary>
    /// 一次本地请求的信号量租约。Dispose 精确释放一次；空租约（云端）不做任何事。
    /// </summary>
    internal sealed class LocalRequestLease : IDisposable
    {
        internal static readonly LocalRequestLease None = new LocalRequestLease(null, 0);

        private readonly SemaphoreSlim _gate;
        private int _released;

        /// <summary>进入闸门前的排队耗时（毫秒）。</summary>
        internal long QueueWaitMs { get; }

        internal LocalRequestLease(SemaphoreSlim gate, long queueWaitMs)
        {
            _gate = gate;
            QueueWaitMs = queueWaitMs;
        }

        public void Dispose()
        {
            if (_gate == null) return;

            if (Interlocked.Exchange(ref _released, 1) != 0)
            {
                ModEntry.SMonitor?.Log(
                    "[LocalRequestThrottle] Semaphore release count mismatch: lease disposed more than once.",
                    StardewModdingAPI.LogLevel.Error);
                return;
            }

            _gate.Release();
        }
    }

    internal abstract class LlmOpenAiBase : Llm, IModelDiscoveryDiagnostics
    {
        protected string apiKey;
        protected string modelName;

        /// <summary>
        /// 本地无 Key 端点（Ollama / LMStudio / 回环）使用占位 Bearer，避免 HttpClient 拒绝空授权头；
        /// 云端保留真实 Key。纯函数，无副作用。
        /// </summary>
        protected static string EffectiveBearer(string apiKey) =>
            string.IsNullOrWhiteSpace(apiKey) ? "Bearer local" : "Bearer " + apiKey;

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> StrictHostStage
            = new System.Collections.Concurrent.ConcurrentDictionary<string, byte>();

        #region 模型列表

        /// <summary>
        /// 兼容旧契约：仅取模型名，失败返回空数组（8 个 IGetModelNames 实现者行为不变）。
        /// </summary>
        protected async Task<string[]> CoreGetModelNamesAsync()
        {
            ModelDiscoveryResult result = await CoreGetModelNamesDiagnosticsAsync();
            return result.ModelNames;
        }

        public async Task<ModelDiscoveryResult> GetModelNamesWithDiagnosticsAsync()
        {
            return await CoreGetModelNamesDiagnosticsAsync();
        }

        protected async Task<ModelDiscoveryResult> CoreGetModelNamesDiagnosticsAsync()
        {
            // 本地无 Key 端点（回环 / 私网）放行模型列表拉取；云端空 Key 仍拦截，避免无效请求。
            if (string.IsNullOrWhiteSpace(apiKey) && !UrlHelper.IsPrivateNetworkUrl(url))
            {
                return LogFailure(ModelDiscovery.Failed(
                    ModelDiscoveryFailure.MissingApiKey,
                    "API key is empty for a non-local endpoint."));
            }

            if (string.IsNullOrWhiteSpace(apiKey))
            {
                Log.Debug("[LlmOpenAiBase] Local endpoint, fetching models without API key.");
            }

            try
            {
                string modelsUrl = BuildEndpoint("models");

                // URL 非法时不得回退云端默认地址，也不得发起请求。
                if (!Uri.TryCreate(modelsUrl, UriKind.Absolute, out _))
                {
                    return LogFailure(ModelDiscovery.Failed(ModelDiscoveryFailure.InvalidUrl, modelsUrl));
                }

                // Android NetworkHelper 路径无法获得状态码：异常统一归 Transport。
                if (AndroidHelper.IsAndroid && NetworkHelper.IsNetworkAvailable())
                {
                    var headers = new Dictionary<string, string>
                    {
                        { "Authorization", EffectiveBearer(apiKey) }
                    };

                    string responseString =
                        await NetworkHelper.MakeRequestWithCustomHeadersAsync(
                            modelsUrl,
                            null,
                            headers);

                    return LogFailure(ModelDiscovery.ParseModelList(responseString));
                }

                using (var request = new HttpRequestMessage(HttpMethod.Get, modelsUrl))
                {
                    request.Headers.Add("Authorization", EffectiveBearer(apiKey));

                    using (var response = await SharedHttpClient.SendAsync(request))
                    {
                        string responseString = await response.Content.ReadAsStringAsync();

                        if (!response.IsSuccessStatusCode)
                        {
                            return LogFailure(ModelDiscovery.Failed(
                                ModelDiscoveryFailure.Http,
                                responseString,
                                (int)response.StatusCode));
                        }

                        return LogFailure(ModelDiscovery.ParseModelList(responseString));
                    }
                }
            }
            catch (Exception ex)
            {
                return LogFailure(ModelDiscovery.Failed(ModelDiscoveryFailure.Transport, ex.Message));
            }
        }

        private static ModelDiscoveryResult LogFailure(ModelDiscoveryResult result)
        {
            if (result.Failure != ModelDiscoveryFailure.None)
            {
                Log.Warning($"[LlmOpenAiBase] Model discovery failed: failure={result.Failure}, status={result.StatusCode}, detail={result.Detail}");
            }

            return result;
        }

        #endregion

        #region URL 和模型判断

        private string BuildEndpoint(string path)
        {
            string baseUrl = (url ?? string.Empty).TrimEnd('/');

            if (baseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            {
                return baseUrl + "/" + path;
            }

            return baseUrl + "/v1/" + path;
        }

        private static bool IsPlainTextModel(string model)
        {
            if (string.IsNullOrWhiteSpace(model)) return false;

            string m = model.ToLowerInvariant();

            return m.Contains("gemma") ||
                   m.Contains("llama") ||
                   m.Contains("mistral") ||
                   m.Contains("mixtral") ||
                   m.Contains("gpt-4o") ||
                   m.Contains("gpt-4.1") ||
                   m.Contains("gpt-4-turbo") ||
                   m.Contains("gpt-3.5") ||
                   m.Contains("chatgpt");
        }

        private static bool IsLikelyReasoningModel(string model)
        {
            if (string.IsNullOrWhiteSpace(model)) return false;

            string m = model.ToLowerInvariant();

            return m.StartsWith("o1") ||
                   m.StartsWith("o3") ||
                   m.StartsWith("o4") ||
                   m.StartsWith("gpt-5") ||
                   m.Contains("deepseek-r1") ||
                   m.Contains("qwq") ||
                   m.Contains("qwen3-thinking") ||
                   m.Contains("reasoning");
        }

        #endregion

        #region 思考模式处理

        /// <summary>
        /// 按端点类型判定思考抑制方案（纯静态，无副作用）
        /// </summary>
        internal static ThinkingSuppressionPlan EvaluateThinkingSuppression(string model, string baseUrl, string cacheContext = "")
        {
            if (!string.IsNullOrEmpty(cacheContext) &&
                cacheContext.Contains("_Think", StringComparison.OrdinalIgnoreCase))
            {
                return ThinkingSuppressionPlan.Empty;
            }

            string m = model ?? string.Empty;
            string b = baseUrl ?? string.Empty;

            if (IsPlainTextModel(m))
                return ThinkingSuppressionPlan.Empty;

            // 本地端点（回环 / 私网）：不广播任何思考抑制参数，避免 400/422。
            if (UrlHelper.IsPrivateNetworkUrl(b))
                return ThinkingSuppressionPlan.Empty;

            if (b.Contains("api.openai.com"))
            {
                if (IsLikelyReasoningModel(m))
                    return new ThinkingSuppressionPlan(false, false, false, false, true, false, "OfficialOpenAi");
                return ThinkingSuppressionPlan.Empty;
            }

            if (b.Contains("api.anthropic.com") || b.Contains("generativelanguage.googleapis.com"))
                return ThinkingSuppressionPlan.Empty;

            if (b.Contains("openrouter.ai"))
                return new ThinkingSuppressionPlan(false, false, false, false, false, true, "OpenRouter");

            if (IsLikelyReasoningModel(m))
                return new ThinkingSuppressionPlan(false, false, false, false, true, false, "OpenAiReasoningFamily");

            return new ThinkingSuppressionPlan(true, true, true, true, false, false, "UniversalBroadcast");
        }

        /// <summary>
        /// 将抑制方案写入请求体
        /// </summary>
        internal static void ApplyThinkingSuppression(
            Dictionary<string, object> requestBody,
            ThinkingSuppressionPlan plan)
        {
            if (plan.UseThinkingDisabled) requestBody["thinking"] = new { type = "disabled" };
            if (plan.UseEnableThinking) requestBody["enable_thinking"] = false;
            if (plan.UseChatTemplateKwargs) requestBody["chat_template_kwargs"] = new { enable_thinking = false };
            if (plan.UseIncludeReasoning) requestBody["include_reasoning"] = false;
            if (plan.UseReasoningEffortLow) requestBody["reasoning_effort"] = "low";
            if (plan.UseOpenRouterReasoning) requestBody["reasoning"] = new { effort = "none" };
        }

        /// <summary>
        /// 判定上下文是否为 BioEditor 快速（无思考）模式。纯函数，null 安全。
        /// </summary>
        internal static bool IsBioEditorFastContext(string cacheContext)
        {
            return string.Equals(cacheContext, LlmContextTypes.Editor + "_Fast", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 快速模式下该抑制方案是否无法保证关闭思考：推理族（o1/o3/o4/gpt-5/deepseek-r1/qwq）
        /// 协议上只能降低不能关闭，故快速模式不可用；OpenRouter 与 UniversalBroadcast 保留
        /// 为"可发送并在输出侧校验"。
        /// </summary>
        internal static bool FastModePlanCannotGuaranteeSuppression(ThinkingSuppressionPlan plan)
        {
            return plan.UseReasoningEffortLow;
        }

        private static readonly System.Text.RegularExpressions.Regex PairedThinkTagRegex =
            new System.Text.RegularExpressions.Regex(
                @"<think\b[^>]*>[\s\S]*?</think\s*>",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);

        /// <summary>
        /// 判定文本是否含成对 think 标签（开标签之后存在闭标签）。仅识别成对标签，不做任何关键词删除。
        /// </summary>
        internal static bool ContainsPairedThinkTags(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;

            return PairedThinkTagRegex.IsMatch(text);
        }

        /// <summary>
        /// 降级梯子：stage 1 移除部分键，stage 2 完整回退
        /// </summary>
        private void ApplyDowngradeStage(
            Dictionary<string, object> requestBody,
            int stage, int nPredict, string cacheContext)
        {
            if (stage == 1)
            {
                requestBody.Remove("thinking");
                requestBody.Remove("chat_template_kwargs");
                requestBody.Remove("reasoning");
            }
            else if (stage == 2)
            {
                StripThinkingParameters(requestBody, nPredict, cacheContext);
            }
        }

        /// <summary>
        /// 序列化后记录最终 payload 中的抑制键（仅白名单键，禁止 messages）
        /// </summary>
        private static void LogFinalPayloadSuppression(string serializedJson, string endpointUrl)
        {
            if (ModEntry.Config?.Debug != true) return;
            try
            {
                var obj = JObject.Parse(serializedJson);
                var whitelist = new[] { "model", "stream", "thinking", "enable_thinking",
                    "chat_template_kwargs", "include_reasoning", "reasoning", "reasoning_effort",
                    "max_tokens", "max_completion_tokens", "temperature", "top_p" };
                var sb = new StringBuilder("[LlmOpenAiBase] FINAL PAYLOAD -> ").Append(endpointUrl);
                foreach (var key in whitelist)
                {
                    var token = obj[key];
                    if (token != null)
                        sb.Append(" | ").Append(key).Append("=").Append(token.ToString(Formatting.None));
                }
                ModEntry.SMonitor?.Log(sb.ToString(), StardewModdingAPI.LogLevel.Debug);
            }
            catch
            {
                ModEntry.SMonitor?.Log("[LlmOpenAiBase] Final payload log parse skip", StardewModdingAPI.LogLevel.Trace);
            }
        }

        private void StripThinkingParameters(
            Dictionary<string, object> requestBody,
            int nPredict,
            string cacheContext = "")
        {
            var genParams = ResolveParameters(cacheContext);
            requestBody.Remove("thinking");
            requestBody.Remove("thinking_config");
            requestBody.Remove("thinking_budget");
            requestBody.Remove("disable_thinking");
            requestBody.Remove("enable_thinking");
            requestBody.Remove("reasoning");
            requestBody.Remove("reasoning_effort");
            requestBody.Remove("include_reasoning");
            requestBody.Remove("max_completion_tokens");
            requestBody.Remove("chat_template_kwargs");

            requestBody["max_tokens"] = genParams.MaxTokens;
            requestBody["temperature"] = genParams.Temperature;
            requestBody["top_p"] = genParams.TopP;
        }

        /// <summary>
        /// 安全地将请求体序列化为 JSON，支持 Custom Body JSON 深合并（仅对允许的上下文及 LlmOAICompatible 生效）。
        /// 若 CustomBodyJson 格式畸形或合并失败，静默降级为标准序列化，不影响游戏运行。
        /// </summary>
        private static string SerializePayloadWithCustomBody(
            Dictionary<string, object> requestBody,
            bool allowCustomBody)
        {
            if (!allowCustomBody || ModEntry.Config?.Provider != "LlmOAICompatible")
            {
                return JsonConvert.SerializeObject(requestBody);
            }

            string customJson = ModEntry.Config?.CustomBodyJson;
            if (string.IsNullOrWhiteSpace(customJson))
            {
                return JsonConvert.SerializeObject(requestBody);
            }

            try
            {
                var baseObj = JObject.FromObject(requestBody);
                var customObj = JObject.Parse(customJson);
                baseObj.Merge(customObj, new JsonMergeSettings
                {
                    MergeArrayHandling = MergeArrayHandling.Union,
                    MergeNullValueHandling = MergeNullValueHandling.Ignore
                });
                ModEntry.SMonitor?.Log("[LlmOpenAiBase] Custom Body JSON merged successfully.", StardewModdingAPI.LogLevel.Debug);
                return baseObj.ToString(Formatting.None);
            }
            catch (Newtonsoft.Json.JsonReaderException)
            {
                ModEntry.SMonitor?.Log("[LlmOpenAiBase] Failed to parse CustomBodyJson, using standard payload.", StardewModdingAPI.LogLevel.Warn);
                return JsonConvert.SerializeObject(requestBody);
            }
            catch (Exception ex)
            {
                ModEntry.SMonitor?.Log($"[LlmOpenAiBase] Custom Body JSON merge failed: {ex.Message}, using standard payload.", StardewModdingAPI.LogLevel.Warn);
                return JsonConvert.SerializeObject(requestBody);
            }
        }

        private bool LooksLikeUnknownParameterError(string response)
        {
            if (string.IsNullOrWhiteSpace(response)) return false;

            string text = response.ToLowerInvariant();

            return text.Contains("unknown parameter") ||
                   text.Contains("unrecognized parameter") ||
                   text.Contains("unsupported parameter") ||
                   text.Contains("extra inputs are not permitted") ||
                   text.Contains("unexpected keyword") ||
                   text.Contains("invalid field") ||
                   text.Contains("unknown field");
        }

        private string CleanRawResponse(string rawContent)
        {
            if (string.IsNullOrWhiteSpace(rawContent)) return rawContent;

            string text = rawContent.Trim();

            // 检测并移除代码块标记（```json、```javascript 等）
            if (text.Contains("```"))
            {
                text = System.Text.RegularExpressions.Regex.Replace(
                    text,
                    @"```[\w]*\s*",
                    "",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                text = text.Replace("```", "").Trim();
            }

            // 检测并移除 import/export 语句（整行删除）
            var rawLines = text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
            var cleanedLines = new List<string>();
            bool foundJsonStart = false;

            foreach (var line in rawLines)
            {
                string trimmed = line.Trim();

                if (trimmed.StartsWith("import ") || trimmed.StartsWith("export "))
                    continue;

                if (trimmed.StartsWith("//") || trimmed.StartsWith("/*") || trimmed == "*/")
                    continue;

                if (!foundJsonStart && string.IsNullOrWhiteSpace(trimmed))
                    continue;

                if (trimmed.StartsWith("["))
                {
                    foundJsonStart = true;
                }

                if (foundJsonStart)
                {
                    cleanedLines.Add(line);
                }
            }

            if (cleanedLines.Count > 0)
            {
                text = string.Join("\n", cleanedLines).Trim();
            }

            if (text.Contains("\n- (") || text.StartsWith("──") || text.StartsWith("---"))
            {
                string[] lines = text.Split(new[] { "\r\n", "\r", "\n" }, StringSplitOptions.None);
                int lastValidStart = -1;

                for (int i = lines.Length - 1; i >= 0; i--)
                {
                    string line = lines[i].TrimStart();
                    if (line.StartsWith("- (") || (line.StartsWith("- ") && !line.StartsWith("- -")))
                    {
                        lastValidStart = i;
                        break;
                    }
                }

                if (lastValidStart >= 0)
                {
                    var cleanLines = new List<string>();
                    for (int i = lastValidStart; i < lines.Length; i++)
                    {
                        cleanLines.Add(lines[i]);
                    }
                    text = string.Join("\n", cleanLines).Trim();
                }
            }

            while (text.StartsWith("---") || text.StartsWith("──") || text.StartsWith("***"))
            {
                int firstNewline = text.IndexOf('\n');
                if (firstNewline >= 0)
                {
                    text = text.Substring(firstNewline + 1).Trim();
                }
                else
                {
                    break;
                }
            }

            return text;
        }

        #endregion

        #region 请求构造

        private Dictionary<string, object> BuildRequestBody(
            List<object> messages,
            int nPredict,
            bool stream = false,
            string cacheContext = "")
        {
            var genParams = ResolveParameters(cacheContext);

            var requestBody = new Dictionary<string, object>
            {
                { "model", modelName },
                { "messages", messages },
                { "stream", stream }
            };

            bool reasoningModel = IsLikelyReasoningModel(modelName);

            if (reasoningModel)
            {
                requestBody["max_completion_tokens"] = genParams.MaxTokens;
            }
            else
            {
                requestBody["max_tokens"] = genParams.MaxTokens;
                requestBody["temperature"] = genParams.Temperature;
                requestBody["top_p"] = genParams.TopP;
            }

            return requestBody;
        }

        #endregion

        #region 非流式推理请求

        internal override async Task<LlmResponse> RunInference(
            string systemPromptString,
            string gameCacheString,
            string npcCacheString,
            string promptString,
            string responseStart = "",
            int n_predict = 2048,
            string cacheContext = "",
            bool allowRetry = true)
        {
            if (!AndroidHelper.IsAndroid)
            {
                return await RunStreamingInference(
                    systemPromptString, 
                    gameCacheString, 
                    npcCacheString, 
                    promptString, 
                    onToken: null,
                    CancellationToken.None, 
                    responseStart, 
                    n_predict,
                    cacheContext);
            }

            promptString =
                (gameCacheString ?? string.Empty) +
                (npcCacheString ?? string.Empty) +
                (promptString ?? string.Empty);

            var messages = new List<object>();

            if (!string.IsNullOrWhiteSpace(systemPromptString))
            {
                messages.Add(new { role = "system", content = systemPromptString });
            }
            
            if (!string.IsNullOrWhiteSpace(responseStart) &&
                !responseStart.Trim().Equals("responseStart", StringComparison.OrdinalIgnoreCase))
            {
                promptString += "\n\n" + responseStart;
            }

            messages.Add(new { role = "user", content = promptString });

            return await ExecuteNonStreamingRequestAsync(messages, n_predict, cacheContext, allowRetry, CancellationToken.None);
        }

        /// <summary>
        /// 共享的非流式 chat/completions HTTP 执行。
        /// 由 Android 路径与 role-based 路径共用，统一处理思考抑制与重试。
        /// </summary>
        internal async Task<LlmResponse> ExecuteNonStreamingRequestAsync(
            List<object> messages,
            int n_predict,
            string cacheContext,
            bool allowRetry,
            CancellationToken callerToken)
        {
            LocalRequestLease lease;

            try
            {
                lease = await LocalRequestThrottle.AcquireAsync(url, callerToken);
            }
            catch (OperationCanceledException)
            {
                // BOUNDARY：排队阶段被取消 —— 不进入 HTTP，不记为服务故障。
                Log.Debug($"[LlmOpenAiBase] Local request cancelled while queued; no HTTP sent. endpoint={url}");
                return LlmResponse.Cancelled();
            }

            var telemetry = new LlmTrafficLogger.LlmRequestTelemetry(GetType().Name, url, modelName, cacheContext);

            using (lease)
            {
                telemetry.QueueWaitMs = lease.QueueWaitMs;
                if (lease.QueueWaitMs > ModEntry.Config.QueryTimeout * 1000)
                {
                    ModEntry.SMonitor?.Log(
                        $"[LlmOpenAiBase] Local request waited {lease.QueueWaitMs}ms in queue, exceeding QueryTimeout={ModEntry.Config.QueryTimeout}s.",
                        StardewModdingAPI.LogLevel.Warn);
                }
                var totalWatch = System.Diagnostics.Stopwatch.StartNew();

                LlmResponse result = await ExecuteNonStreamingCoreAsync(
                    messages, n_predict, cacheContext, allowRetry, callerToken, telemetry);

                totalWatch.Stop();
                telemetry.TotalMs = totalWatch.ElapsedMilliseconds;
                telemetry.OutputChars = result?.Text?.Length ?? 0;
                telemetry.Log();

                return result;
            }
        }

        private async Task<LlmResponse> ExecuteNonStreamingCoreAsync(
            List<object> messages,
            int n_predict,
            string cacheContext,
            bool allowRetry,
            CancellationToken callerToken,
            LlmTrafficLogger.LlmRequestTelemetry telemetry)
        {
            Dictionary<string, object> requestBody = BuildRequestBody(messages, n_predict, stream: false, cacheContext);
            ThinkingSuppressionPlan plan = EvaluateThinkingSuppression(modelName, url, cacheContext);
            ApplyThinkingSuppression(requestBody, plan);

            // Fast 上下文：推理族协议上只能降低不能关闭思考，无法保证抑制 → 拒绝发起请求
            if (IsBioEditorFastContext(cacheContext) && FastModePlanCannotGuaranteeSuppression(plan))
            {
                ModEntry.SMonitor?.Log(
                    $"[LlmOpenAiBase] Fast context rejected before request: model={modelName}, context={cacheContext}, plan={plan.Reason} cannot disable thinking.",
                    StardewModdingAPI.LogLevel.Warn);
                return new LlmResponse("Fast thinking mode is unsupported for this reasoning model.", 422);
            }

            string endpointUrl = BuildEndpoint("chat/completions");

            int retryCount = allowRetry ? 3 : 1;
            string responseString = string.Empty;
            int statusCode = 500;
            bool strippedThinkingParameters = false;

            int maxAttempts = retryCount;

            while (retryCount > 0)
            {
                telemetry.Attempt = maxAttempts - retryCount + 1;

                try
                {
                    // B1：尝试前置取消检查（对齐 LlmLlamaCpp 模式）——云端预取消也零 HTTP。
                    callerToken.ThrowIfCancellationRequested();

                    var genParams = ResolveParameters(cacheContext);
                    string jsonData = SerializePayloadWithCustomBody(requestBody, genParams.AllowCustomBody);
                    LogFinalPayloadSuppression(jsonData, endpointUrl);

                    // 链接调用方取消令牌与配置的 HTTP 超时。
                    using (var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(callerToken))
                    {
                        linkedCts.CancelAfter(TimeSpan.FromSeconds(ModEntry.Config.QueryTimeout));
                        var linkedToken = linkedCts.Token;

                        if (AndroidHelper.IsAndroid && NetworkHelper.IsNetworkAvailable())
                        {
                            var headers = new Dictionary<string, string>
                            {
                                { "Authorization", EffectiveBearer(apiKey) }
                            };

                            responseString = await NetworkHelper.MakeRequestWithCustomHeadersAsync(
                                endpointUrl,
                                jsonData,
                                headers,
                                linkedToken);

                            statusCode = LooksLikeErrorResponse(responseString) ? 400 : 200;
                        }
                        else
                        {
                            using (var request = new HttpRequestMessage(HttpMethod.Post, endpointUrl))
                            {
                                request.Headers.Add("Authorization", EffectiveBearer(apiKey));
                                request.Content = new StringContent(jsonData, Encoding.UTF8, "application/json");

                                using (var response = await SharedHttpClient.SendAsync(request, linkedToken))
                                {
                                    statusCode = (int)response.StatusCode;
                                    responseString = await response.Content.ReadAsStringAsync();
                                }
                            }
                        }
                    }

                    telemetry.StatusCode = statusCode;

                    bool isClientError = statusCode == 400 || statusCode == 422;

                    if (isClientError && !strippedThinkingParameters &&
                        (plan.AnyApiSuppression || LooksLikeUnknownParameterError(responseString)))
                    {
                        // Fast 上下文：不得剥离抑制参数后继续请求，直接失败
                        if (IsBioEditorFastContext(cacheContext))
                        {
                            ModEntry.SMonitor?.Log($"[LlmOpenAiBase] Fast context: server rejected thinking suppression ({statusCode}); failing instead of stripping.", StardewModdingAPI.LogLevel.Warn);
                            return new LlmResponse(responseString, statusCode);
                        }

                        Log.Debug("[LlmOpenAiBase] Server rejected thinking parameters. Retrying with pure standard payload.");

                        StripThinkingParameters(requestBody, n_predict, cacheContext);
                        strippedThinkingParameters = true;

                        retryCount--;
                        if (retryCount > 0)
                        {
                            continue;
                        }
                    }

                    if (statusCode < 200 || statusCode >= 300)
                    {
                        Log.Debug($"[LlmOpenAiBase] HTTP request failed. Status: {statusCode}, Response: {responseString}");
                        retryCount--;
                        if (retryCount > 0)
                        {
                            await Task.Delay(250, callerToken);
                        }
                        continue;
                    }

                    JObject responseJson = JObject.Parse(responseString);
                    JArray choices = responseJson["choices"] as JArray;

                    if (choices == null || choices.Count == 0)
                    {
                        Log.Debug("[LlmOpenAiBase] Response contains no choices.");
                        retryCount--;
                        continue;
                    }

                    JToken firstChoice = choices[0];
                    JToken messageToken = firstChoice["message"];

                    if (messageToken == null)
                    {
                        Log.Debug("[LlmOpenAiBase] Response contains no message.");
                        retryCount--;
                        continue;
                    }

                    string messageReasoning = messageToken.Value<string>("reasoning_content")
                                              ?? messageToken.Value<string>("reasoning");

                    if (!string.IsNullOrEmpty(messageReasoning) && IsBioEditorFastContext(cacheContext))
                    {
                        ModEntry.SMonitor?.Log(
                            $"[LlmOpenAiBase] Fast context: model emitted reasoning in message despite suppression. model={modelName}, context={cacheContext}.",
                            StardewModdingAPI.LogLevel.Warn);
                        return new LlmResponse("Model emitted reasoning tokens despite suppression (BioEditor fast mode).", 502);
                    }

                    string contentString = messageToken["content"]?.ToString();

                    if (!string.IsNullOrWhiteSpace(contentString) &&
                        IsBioEditorFastContext(cacheContext) &&
                        ContainsPairedThinkTags(contentString))
                    {
                        ModEntry.SMonitor?.Log(
                            $"[LlmOpenAiBase] Fast context: model emitted think tags in content despite suppression. model={modelName}, context={cacheContext}.",
                            StardewModdingAPI.LogLevel.Warn);
                        return new LlmResponse("Model emitted think tags in content despite suppression (BioEditor fast mode).", 502);
                    }

                    if (!string.IsNullOrWhiteSpace(contentString))
                    {
                        contentString = CleanRawResponse(contentString);
                    }

                    if (LooksLikeDegenerateRepetition(contentString))
                    {
                        Log.Debug("[LlmOpenAiBase] Discarded degenerate/repetitive response (non-streaming).");
                        retryCount--;
                        if (retryCount > 0)
                        {
                            await Task.Delay(250, callerToken);
                        }
                        continue;
                    }

                    if (!string.IsNullOrWhiteSpace(contentString))
                    {
                        return new LlmResponse(contentString);
                    }

                    Log.Debug("[LlmOpenAiBase] Response message content is empty.");
                    retryCount--;
                }
                catch (JsonException ex)
                {
                    Log.Debug("[LlmOpenAiBase] Invalid JSON response: " + ex.Message);
                    retryCount--;
                }
                catch (OperationCanceledException) when (callerToken.IsCancellationRequested)
                {
                    // BOUNDARY：调用方取消 —— 立即返回，不消耗重试预算、不记为服务故障。
                    Log.Debug($"[LlmOpenAiBase] Request cancelled by caller. endpoint={endpointUrl}");
                    return LlmResponse.Cancelled();
                }
                catch (OperationCanceledException)
                {
                    // BOUNDARY：QueryTimeout 触发 —— 立即返回，不重试。
                    Log.Warning($"[LlmOpenAiBase] Request timed out after {ModEntry.Config.QueryTimeout}s. endpoint={endpointUrl}");
                    return LlmResponse.Timeout();
                }
                catch (Exception ex)
                {
                    Log.Debug("[LlmOpenAiBase] Request error: " + ex.Message);
                    retryCount--;
                }

                if (retryCount > 0)
                {
                    await Task.Delay(250, callerToken);
                }
            }

            return new LlmResponse(responseString, statusCode);
        }

        /// <summary>
        /// 共享的流式 chat/completions HTTP 执行（真实 SSE）。
        /// 由调用方装配好消息列表后调用，统一处理思考抑制、黑名单降级与重试。
        /// </summary>
        private async Task<LlmResponse> ExecuteStreamingRequestAsync(
            List<object> messages,
            Action<string> onToken,
            CancellationToken ct,
            int n_predict,
            string cacheContext)
        {
            LocalRequestLease lease;

            try
            {
                lease = await LocalRequestThrottle.AcquireAsync(url, ct);
            }
            catch (OperationCanceledException)
            {
                // BOUNDARY：排队阶段被取消 —— 不进入 HTTP，不记为服务故障。
                Log.Debug($"[LlmOpenAiBase] Local streaming request cancelled while queued; no HTTP sent. endpoint={url}");
                return LlmResponse.Cancelled();
            }

            var telemetry = new LlmTrafficLogger.LlmRequestTelemetry(GetType().Name, url, modelName, cacheContext);

            using (lease)
            {
                telemetry.QueueWaitMs = lease.QueueWaitMs;
                if (lease.QueueWaitMs > ModEntry.Config.QueryTimeout * 1000)
                {
                    ModEntry.SMonitor?.Log(
                        $"[LlmOpenAiBase] Local streaming request waited {lease.QueueWaitMs}ms in queue, exceeding QueryTimeout={ModEntry.Config.QueryTimeout}s.",
                        StardewModdingAPI.LogLevel.Warn);
                }
                var totalWatch = System.Diagnostics.Stopwatch.StartNew();

                LlmResponse result = await ExecuteStreamingCoreAsync(
                    messages, onToken, ct, n_predict, cacheContext, telemetry);

                totalWatch.Stop();
                telemetry.TotalMs = totalWatch.ElapsedMilliseconds;
                telemetry.OutputChars = result?.Text?.Length ?? 0;
                telemetry.Log();

                return result;
            }
        }

        private async Task<LlmResponse> ExecuteStreamingCoreAsync(
            List<object> messages,
            Action<string> onToken,
            CancellationToken ct,
            int n_predict,
            string cacheContext,
            LlmTrafficLogger.LlmRequestTelemetry telemetry)
        {
            Dictionary<string, object> requestBody = BuildRequestBody(messages, n_predict, stream: true, cacheContext);
            ThinkingSuppressionPlan plan = EvaluateThinkingSuppression(modelName, url, cacheContext);
            ApplyThinkingSuppression(requestBody, plan);

            // Fast 上下文：推理族协议上只能降低不能关闭思考，无法保证抑制 → 拒绝发起请求
            if (IsBioEditorFastContext(cacheContext) && FastModePlanCannotGuaranteeSuppression(plan))
            {
                ModEntry.SMonitor?.Log(
                    $"[LlmOpenAiBase] Fast context rejected before request: model={modelName}, context={cacheContext}, plan={plan.Reason} cannot disable thinking.",
                    StardewModdingAPI.LogLevel.Warn);
                return new LlmResponse("Fast thinking mode is unsupported for this reasoning model.", 422);
            }

            string endpointUrl = BuildEndpoint("chat/completions");

            // ── LOCAL-004：本地端点的 SSE 自动协商，收敛于此唯一流式入口 ──
            // 尚未发出任何请求，此刻尚未向 UI 下发任何 token。
            // 本地端点默认优先流式，仅在未产生任何 token 时才可能降级一次非流式；云端端点零变化。
            bool localEndpoint = UrlHelper.IsPrivateNetworkUrl(url);
            bool tokenEmitted = false;
            Action<string> emitToken = text =>
            {
                tokenEmitted = true;
                onToken?.Invoke(text);
            };

            var genParams = ResolveParameters(cacheContext);
            string jsonData = SerializePayloadWithCustomBody(requestBody, genParams.AllowCustomBody);
            LogFinalPayloadSuppression(jsonData, endpointUrl);

            // TTFT observation
            var ttftWatch = new System.Diagnostics.Stopwatch();
            bool ttftLogged = false;
            bool truncatedByLength = false;

            // StrictHostStage key
            string hostModelKey = null;
            try
            {
                var uri = new Uri(endpointUrl);
                hostModelKey = uri.Host.ToLowerInvariant() + "|" + (modelName ?? "").ToLowerInvariant();
            }
            catch { /* Uri parse failure → skip blacklist */ }

            // Check blacklist before first attempt
            byte rememberedStage = 0;
            if (!IsBioEditorFastContext(cacheContext) &&
                hostModelKey != null && StrictHostStage.TryGetValue(hostModelKey, out rememberedStage) && rememberedStage > 0)
            {
                ApplyDowngradeStage(requestBody, rememberedStage, n_predict, cacheContext);
            }

            // Re-serialize after blacklist check
            jsonData = SerializePayloadWithCustomBody(requestBody, genParams.AllowCustomBody);
            LogFinalPayloadSuppression(jsonData, endpointUrl);

            // Retry loop: degradation (stage 0→1→2) and 429/5xx retries are independent
            int stage = rememberedStage;
            int retryAttempt = 0;
            const int maxRetries = 3;
            while (retryAttempt < maxRetries)
            {
                telemetry.Attempt = retryAttempt + 1;
                ttftWatch.Restart();

                using (var request = new HttpRequestMessage(HttpMethod.Post, endpointUrl))
                {
                    request.Headers.Add("Authorization", EffectiveBearer(apiKey));
                    request.Headers.Add("Accept", "text/event-stream");
                    request.Content = new StringContent(jsonData, Encoding.UTF8, "application/json");

                    using (var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                    {
                        linkedCts.CancelAfter(TimeSpan.FromSeconds(ModEntry.Config.QueryTimeout));

                        try
                        {
                            using (var response = await SharedHttpClient.SendAsync(
                                request,
                                HttpCompletionOption.ResponseHeadersRead,
                                linkedCts.Token))
                            {
                                telemetry.StatusCode = (int)response.StatusCode;

                                if (!response.IsSuccessStatusCode)
                                {
                                    string errContent = await response.Content.ReadAsStringAsync();
                                    int status = (int)response.StatusCode;
                                    bool isClientError = status == 400 || status == 422;

                                    // Try degradation on 400/422
                                    if (isClientError && stage < 2 &&
                                        (plan.AnyApiSuppression || LooksLikeUnknownParameterError(errContent)))
                                    {
                                        // Fast 上下文：不得静默降级续发，直接失败
                                        if (IsBioEditorFastContext(cacheContext))
                                        {
                                            ModEntry.SMonitor?.Log($"[LlmOpenAiBase] Fast context: server rejected thinking suppression ({status}); failing instead of degrading.", StardewModdingAPI.LogLevel.Warn);
                                            return new LlmResponse(errContent, status);
                                        }

                                        stage++;
                                        ModEntry.SMonitor?.Log($"[LlmOpenAiBase] Streaming got {status}, degrading to stage {stage}.", StardewModdingAPI.LogLevel.Debug);
                                        ApplyDowngradeStage(requestBody, stage, n_predict, cacheContext);
                                        jsonData = SerializePayloadWithCustomBody(requestBody, genParams.AllowCustomBody);
                                        LogFinalPayloadSuppression(jsonData, endpointUrl);
                                        continue;
                                    }

                                    // Retry on 429/5xx (same stage)
                                    if ((status == 429 || status >= 500) && retryAttempt < maxRetries - 1)
                                    {
                                        retryAttempt++;
                                        ModEntry.SMonitor?.Log($"[LlmOpenAiBase] Streaming got {status}, retrying (attempt {retryAttempt}/{maxRetries}).", StardewModdingAPI.LogLevel.Debug);
                                        await Task.Delay(250, ct);
                                        continue;
                                    }

                                    Log.Debug($"[LlmOpenAiBase] Streaming failed: {status}, Response: {errContent}");

                                    // LOCAL-004：本地端点且服务明确拒绝 SSE 且尚未下发 token → 最多降级一次非流式。
                                    // 已下发 token 或已取消时不降级（调用方已放弃本次请求），避免重复台词。
                                    if (localEndpoint &&
                                        !tokenEmitted &&
                                        !ct.IsCancellationRequested &&
                                        LooksLikeStreamUnsupportedError(errContent))
                                    {
                                        ModEntry.SMonitor?.Log(
                                            $"[LlmOpenAiBase] Local endpoint rejected SSE ({status}); retrying once without streaming.",
                                            StardewModdingAPI.LogLevel.Warn);
                                        return await ExecuteLocalNonStreamingAsync(messages, emitToken, ct, n_predict, cacheContext, telemetry);
                                    }

                                    return new LlmResponse(errContent, status);
                                }

                                // Success after degradation → write to blacklist
                                if (stage > 0 && hostModelKey != null)
                                {
                                    StrictHostStage.AddOrUpdate(hostModelKey, (byte)stage, (_, existing) => (byte)Math.Max(existing, stage));
                                    ModEntry.SMonitor?.Log($"[LlmOpenAiBase] Blacklist updated: {hostModelKey} → stage {stage}", StardewModdingAPI.LogLevel.Debug);
                                }

                                var fullContentBuilder = new StringBuilder();
                                bool degenerateDetected = false;
                                bool reasoningSeen = false;
                                bool reasoningEmitted = false;
                                long ttftReasoningMs = -1;

                                // LOCAL-004：本地端点且 Content-Type 不是 SSE 时，必须先读完整响应体才能定性。
                                // 此处读取在流式解析之前，此刻尚未下发任何 token。
                                bool probeNonSseBody = localEndpoint &&
                                    !IsSseMediaType(response.Content.Headers.ContentType?.MediaType);
                                string bufferedBody = probeNonSseBody ? await response.Content.ReadAsStringAsync() : null;
                                Stream bodyStream = probeNonSseBody
                                    ? new MemoryStream(Encoding.UTF8.GetBytes(bufferedBody))
                                    : await response.Content.ReadAsStreamAsync();
                                bool sawDataLine = false;

                                using (var reader = new StreamReader(bodyStream, Encoding.UTF8))
                                {
                                    while (!reader.EndOfStream && !linkedCts.Token.IsCancellationRequested)
                                    {
                                        string line = await reader.ReadLineAsync();
                                        if (line == null) break;

                                        line = line.Trim();
                                        if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("data:"))
                                            continue;

                                        sawDataLine = true;
                                        string data = line.Substring(5).Trim();
                                        if (data == "[DONE]") break;

                                        try
                                        {
                                            var chunkJson = JObject.Parse(data);
                                            var choices = chunkJson["choices"] as JArray;
                                            if (choices == null || choices.Count == 0) continue;

                                            if (!truncatedByLength &&
                                                string.Equals(choices[0].Value<string>("finish_reason"), "length", StringComparison.OrdinalIgnoreCase))
                                            {
                                                truncatedByLength = true;
                                            }

                                            var delta = choices[0]["delta"] as JObject;
                                            if (delta == null) continue;

                                            // Parse reasoning_content (parse-and-ignore)
                                            string reasoningToken = delta.Value<string>("reasoning_content");
                                            if (string.IsNullOrEmpty(reasoningToken))
                                                reasoningToken = delta.Value<string>("reasoning");

                                            if (!string.IsNullOrEmpty(reasoningToken))
                                            {
                                                if (IsBioEditorFastContext(cacheContext))
                                                    reasoningEmitted = true;

                                                if (!reasoningSeen)
                                                {
                                                    reasoningSeen = true;
                                                    ttftReasoningMs = ttftWatch.ElapsedMilliseconds;
                                                    // 仅在已尝试抑制时告警；Editor _Think 主动放行思考属预期行为，不应误报
                                                    if (plan.AnyApiSuppression)
                                                    {
                                                        ModEntry.SMonitor?.Log(
                                                            $"[LlmOpenAiBase] Model emitted reasoning_content — thinking suppression NOT honored by server. model={modelName}. Check FINAL PAYLOAD log.",
                                                            StardewModdingAPI.LogLevel.Warn);
                                                    }
                                                }
                                                continue; // Do NOT append to fullContentBuilder
                                            }

                                            string textToken = delta.Value<string>("content");
                                            if (!string.IsNullOrEmpty(textToken))
                                            {
                                                // TTFT: first content token
                                                if (!ttftLogged)
                                                {
                                                    ttftLogged = true;
                                                    telemetry.TtftMs = ttftWatch.ElapsedMilliseconds;
                                                    ModEntry.SMonitor?.Log(
                                                        $"[LlmOpenAiBase] TTFT(content)={ttftWatch.ElapsedMilliseconds}ms | TTFT(reasoning)={(ttftReasoningMs >= 0 ? ttftReasoningMs + "ms" : "n/a")} | endpoint={endpointUrl}",
                                                        StardewModdingAPI.LogLevel.Debug);
                                                }

                                                fullContentBuilder.Append(textToken);
                                                emitToken(textToken);

                                                if (!degenerateDetected &&
                                                    LooksLikeDegenerateRepetition(fullContentBuilder.ToString()))
                                                {
                                                    degenerateDetected = true;
                                                    ModEntry.SMonitor?.Log("[LlmOpenAiBase] Degenerate/repetitive output detected mid-stream; aborting.", StardewModdingAPI.LogLevel.Warn);
                                                    linkedCts.Cancel();
                                                    break;
                                                }
                                            }
                                        }
                                        catch (JsonException parseEx)
                                        {
                                            Log.Debug("[LlmOpenAiBase] Chunk JSON parse skip: " + parseEx.Message);
                                        }
                                    }
                                }

                                if (truncatedByLength)
                                {
                                    ModEntry.SMonitor?.Log(
                                        $"[LlmOpenAiBase] Output truncated by max_tokens={genParams.MaxTokens} (finish_reason=length). Raise the preset budget or shorten the request.",
                                        StardewModdingAPI.LogLevel.Warn);
                                }

                                if (degenerateDetected)
                                {
                                    return new LlmResponse("Discarded degenerate/repetitive streaming output.", 500);
                                }

                                string completeText = fullContentBuilder.ToString();

                                // LOCAL-004：确认不是 SSE 且尚未下发任何 token → 解析已收到的完整 JSON，不重复发送请求。
                                if (probeNonSseBody && !tokenEmitted && !sawDataLine && !ct.IsCancellationRequested)
                                {
                                    return ResolveLocalNonSseBody(bufferedBody, emitToken);
                                }

                                LlmTrafficLogger.LogIncoming(cacheContext, completeText);

                                // Fast 上下文：抑制未生效则显式失败，不得把部分结果报告为成功
                                if (IsBioEditorFastContext(cacheContext))
                                {
                                    if (reasoningEmitted)
                                    {
                                        ModEntry.SMonitor?.Log(
                                            $"[LlmOpenAiBase] Fast context: model emitted reasoning tokens despite suppression. model={modelName}, context={cacheContext}.",
                                            StardewModdingAPI.LogLevel.Warn);
                                        return new LlmResponse("Model emitted reasoning tokens despite suppression (BioEditor fast mode).", 502);
                                    }

                                    if (ContainsPairedThinkTags(completeText))
                                    {
                                        ModEntry.SMonitor?.Log(
                                            $"[LlmOpenAiBase] Fast context: model emitted think tags in content despite suppression. model={modelName}, context={cacheContext}.",
                                            StardewModdingAPI.LogLevel.Warn);
                                        return new LlmResponse("Model emitted think tags in content despite suppression (BioEditor fast mode).", 502);
                                    }
                                }

                                // B1：取消与流结束竞态 —— 调用方已取消时按 Cancelled 标注（IsSuccess 与文本不动）。
                                var finalResponse = new LlmResponse(completeText);
                                if (ct.IsCancellationRequested)
                                {
                                    finalResponse.EndReason = DialogueModels.LlmRequestEndReason.Cancelled;
                                }
                                return finalResponse;
                            }
                        }
                        catch (OperationCanceledException) when (ct.IsCancellationRequested)
                        {
                            // BOUNDARY：调用方取消 —— 立即返回，不消耗重试预算、不记为服务故障。
                            Log.Debug($"[LlmOpenAiBase] Streaming request cancelled by caller. endpoint={endpointUrl}");
                            return LlmResponse.Cancelled();
                        }
                        catch (OperationCanceledException)
                        {
                            // BOUNDARY：QueryTimeout 触发 —— 立即返回，不重试。
                            Log.Warning($"[LlmOpenAiBase] Streaming request timed out after {ModEntry.Config.QueryTimeout}s. endpoint={endpointUrl}");
                            return LlmResponse.Timeout();
                        }
                        catch (Exception ex)
                        {
                            Log.Debug("[LlmOpenAiBase] Streaming connection error: " + ex.Message);
                            return new LlmResponse(ex.Message, 500);
                        }
                    }
                }
            }

            // All attempts exhausted
            return new LlmResponse("All streaming attempts failed.", 500);
        }

        /// <summary>
        /// 将 role-based 消息列表序列化为 OpenAI 兼容的匿名对象列表。
        /// systemPromptString 作为首条 system 消息；responseStart 按原有位置追加到末尾 user 消息内容（仅一次）。
        /// </summary>
        internal static List<object> BuildChatMessages(
            string systemPromptString,
            IReadOnlyList<LlmChatMessage> messages,
            string responseStart)
        {
            if (messages == null)
                throw new ArgumentNullException(nameof(messages));

            var result = new List<object>();

            if (!string.IsNullOrWhiteSpace(systemPromptString))
                result.Add(new { role = "system", content = systemPromptString });

            for (int i = 0; i < messages.Count; i++)
            {
                var m = messages[i];
                if (m == null)
                    throw new ArgumentException("Chat message list contains a null message.", nameof(messages));

                string content = m.Content;
                bool isLast = (i == messages.Count - 1);
                if (isLast && string.Equals(m.Role, "user", StringComparison.Ordinal)
                    && !string.IsNullOrWhiteSpace(responseStart)
                    && !responseStart.Trim().Equals("responseStart", StringComparison.OrdinalIgnoreCase))
                {
                    content += "\n\n" + responseStart;
                }
                result.Add(new { role = m.Role, content = content });
            }

            return result;
        }

        /// <summary>
        /// 非流式 role-based 推理（OpenAI 兼容）。仅主对话路径使用。
        /// </summary>
        internal async Task<LlmResponse> RunChatInference(
            string systemPromptString,
            IReadOnlyList<LlmChatMessage> messages,
            CancellationToken ct,
            string responseStart = "",
            int n_predict = 2048,
            string cacheContext = "",
            bool allowRetry = true)
        {
            var msgList = BuildChatMessages(systemPromptString, messages, responseStart);

            return await ExecuteNonStreamingRequestAsync(msgList, n_predict, cacheContext, allowRetry, ct);
        }

        /// <summary>
        /// 流式 role-based 推理（OpenAI 兼容）。仅主对话路径使用。
        /// </summary>
        internal async Task<LlmResponse> RunStreamingChatInference(
            string systemPromptString,
            IReadOnlyList<LlmChatMessage> messages,
            Action<string> onToken,
            CancellationToken ct,
            string responseStart = "",
            int n_predict = 2048,
            string cacheContext = "")
        {
            if (AndroidHelper.IsAndroid)
            {
                var fallback = await RunChatInference(systemPromptString, messages, ct, responseStart, n_predict, cacheContext);
                if (fallback.IsSuccess && !string.IsNullOrWhiteSpace(fallback.Text))
                    onToken?.Invoke(fallback.Text);
                return fallback;
            }

            var msgList = BuildChatMessages(systemPromptString, messages, responseStart);
            return await ExecuteStreamingRequestAsync(msgList, onToken, ct, n_predict, cacheContext);
        }

        #endregion

        #region 真实 SSE 流式传输实现

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
            if (AndroidHelper.IsAndroid)
            {
                var fallback = await RunInference(systemPromptString, gameCacheString, npcCacheString, promptString, responseStart, n_predict, cacheContext);
                if (fallback.IsSuccess && !string.IsNullOrWhiteSpace(fallback.Text))
                {
                    onToken?.Invoke(fallback.Text);
                }
                return fallback;
            }

            LlmTrafficLogger.LogOutgoing(cacheContext, modelName,
                BuildEndpoint("chat/completions"),
                systemPromptString, gameCacheString, npcCacheString, promptString, responseStart);

            promptString =
                (gameCacheString ?? string.Empty) +
                (npcCacheString ?? string.Empty) +
                (promptString ?? string.Empty);

            var messages = new List<object>();

            if (!string.IsNullOrWhiteSpace(systemPromptString))
            {
                messages.Add(new { role = "system", content = systemPromptString });
            }

            if (!string.IsNullOrWhiteSpace(responseStart) &&
                !responseStart.Trim().Equals("responseStart", StringComparison.OrdinalIgnoreCase))
            {
                promptString += "\n\n" + responseStart;
            }

            messages.Add(new { role = "user", content = promptString });

            return await ExecuteStreamingRequestAsync(messages, onToken, ct, n_predict, cacheContext);
        }

#endregion

        private bool LooksLikeDegenerateRepetition(string text, int minLength = 60)
        {
            if (string.IsNullOrWhiteSpace(text) || text.Length < minLength) return false;

            int windowSize = Math.Min(text.Length, 400);
            string window = text.Substring(text.Length - windowSize);

            for (int unitLen = 1; unitLen <= 4; unitLen++)
            {
                if (window.Length < unitLen * 8) continue;

                string unit = window.Substring(window.Length - unitLen);
                if (string.IsNullOrWhiteSpace(unit)) continue;

                int repeatCount = 0;
                int pos = window.Length;
                while (pos - unitLen >= 0 && window.Substring(pos - unitLen, unitLen) == unit)
                {
                    repeatCount++;
                    pos -= unitLen;
                }

                if (repeatCount >= 20)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// LOCAL-004：本地端点 SSE 不可用时的非流式取回。收到完整响应后最多调用 onToken 一次。
        /// 每次业务请求至多触发一次：调用方要么立刻返回，要么因守卫不进入此处。
        /// </summary>
        private async Task<LlmResponse> ExecuteLocalNonStreamingAsync(
            List<object> messages,
            Action<string> onToken,
            CancellationToken ct,
            int n_predict,
            string cacheContext,
            LlmTrafficLogger.LlmRequestTelemetry telemetry)
        {
            // 已在流式入口取得本地租约，此处直接走核心，绝不二次取租约（并发=1 时会自锁）。
            LlmResponse result = await ExecuteNonStreamingCoreAsync(messages, n_predict, cacheContext, true, ct, telemetry);

            if (result.IsSuccess && !string.IsNullOrWhiteSpace(result.Text))
                onToken?.Invoke(result.Text);

            return result;
        }

        /// <summary>
        /// LOCAL-004：解析本地端点返回的非 SSE 完整 JSON（已收到响应体，不再重发请求）。
        /// 成功时 onToken 恰好被调用一次；解析失败返回失败 LlmResponse 并记 Warn。
        /// </summary>
        private LlmResponse ResolveLocalNonSseBody(string body, Action<string> emitToken)
        {
            try
            {
                string contentString = (JObject.Parse(body)["choices"] as JArray)?[0]?["message"]?["content"]?.ToString();

                if (string.IsNullOrWhiteSpace(contentString))
                {
                    ModEntry.SMonitor?.Log(
                        "[LlmOpenAiBase] Local endpoint answered without SSE: no message content in body.",
                        StardewModdingAPI.LogLevel.Warn);
                    return new LlmResponse("Local endpoint answered without SSE and without message content.", 502);
                }

                contentString = CleanRawResponse(contentString);
                emitToken?.Invoke(contentString);
                return new LlmResponse(contentString);
            }
            catch (JsonException ex)
            {
                ModEntry.SMonitor?.Log(
                    $"[LlmOpenAiBase] Local endpoint answered without SSE and body is not valid chat/completions JSON: {ex.Message}",
                    StardewModdingAPI.LogLevel.Warn);
                return new LlmResponse("Local endpoint answered without SSE and body is not valid chat/completions JSON.", 502);
            }
        }

        /// <summary>
        /// LOCAL-004：服务端是否明确拒绝 SSE（Auto 模式降级非流式的唯一依据）。
        /// </summary>
        private static bool LooksLikeStreamUnsupportedError(string response)
        {
            if (string.IsNullOrWhiteSpace(response)) return false;

            string text = response.ToLowerInvariant();
            if (!text.Contains("stream")) return false;

            return text.Contains("not supported") ||
                   text.Contains("unsupported") ||
                   text.Contains("not implemented") ||
                   text.Contains("does not support") ||
                   text.Contains("not enabled") ||
                   text.Contains("disabled");
        }

        private static bool IsSseMediaType(string mediaType) =>
            !string.IsNullOrEmpty(mediaType) &&
            mediaType.Equals("text/event-stream", StringComparison.OrdinalIgnoreCase);

        private bool LooksLikeErrorResponse(string response)
        {
            if (string.IsNullOrWhiteSpace(response)) return false;

            string text = response.ToLowerInvariant();

            return text.Contains("\"error\"") ||
                   text.Contains("\"code\":400") ||
                   text.Contains("\"code\":422") ||
                   text.Contains("bad request") ||
                   text.Contains("unknown parameter") ||
                   text.Contains("unrecognized parameter") ||
                   text.Contains("unsupported parameter");
        }

        #region 概率接口

        internal override Dictionary<string, double>[] RunInferenceProbabilities(
            string fullPrompt,
            int n_predict = 1)
        {
            throw new NotImplementedException();
        }

        #endregion
    }
}