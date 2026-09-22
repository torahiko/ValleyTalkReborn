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

    internal abstract class LlmOpenAiBase : Llm
    {
        protected string apiKey;
        protected string modelName;

        /// <summary>
        /// 本地无 Key 端点（Ollama / LMStudio / 回环）使用占位 Bearer，避免 HttpClient 拒绝空授权头；
        /// 云端保留真实 Key。纯函数，无副作用。
        /// </summary>
        protected static string EffectiveBearer(string apiKey) =>
            string.IsNullOrWhiteSpace(apiKey) ? "Bearer local" : "Bearer " + apiKey;

        public override bool SupportsStreamingWithTools => true;

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> StrictHostStage
            = new System.Collections.Concurrent.ConcurrentDictionary<string, byte>();

        #region 模型列表

        protected async Task<string[]> CoreGetModelNamesAsync()
        {
            // 本地无 Key 端点放行模型列表拉取；云端空 Key 仍拦截，避免无效请求。
            if (string.IsNullOrWhiteSpace(apiKey) && !UrlHelper.IsLoopbackUrl(url))
            {
                return Array.Empty<string>();
            }
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                Log.Debug("[LlmOpenAiBase] Local endpoint, fetching models without API key.");
            }

            try
            {
                string modelsUrl = BuildEndpoint("models");

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

                    return ParseModelNamesFromJson(responseString);
                }
                else
                {
                    using (var request = new HttpRequestMessage(HttpMethod.Get, modelsUrl))
                    {
                        request.Headers.Add("Authorization", EffectiveBearer(apiKey));

                        using (var response = await SharedHttpClient.SendAsync(request))
                        {
                            string responseString = await response.Content.ReadAsStringAsync();
                            return ParseModelNamesFromJson(responseString);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Debug("[LlmOpenAiBase] GetModelNames failed: " + ex.Message);
                return Array.Empty<string>();
            }
        }

        private string[] ParseModelNamesFromJson(string jsonString)
        {
            if (string.IsNullOrWhiteSpace(jsonString)) return Array.Empty<string>();

            try
            {
                JObject responseJson = JObject.Parse(jsonString);
                JArray modelsToken = responseJson["data"] as JArray;

                if (modelsToken == null)
                {
                    return Array.Empty<string>();
                }

                var modelNames = new List<string>();

                foreach (JToken model in modelsToken)
                {
                    JToken idToken = model["id"];
                    if (idToken != null && !string.IsNullOrWhiteSpace(idToken.ToString()))
                    {
                        modelNames.Add(idToken.ToString());
                    }
                }

                return modelNames.ToArray();
            }
            catch (Exception ex)
            {
                Log.Debug("[LlmOpenAiBase] Parse model list failed: " + ex.Message);
                return Array.Empty<string>();
            }
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
            bool includeTools = true,
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

            // 推理模型与后台环境气泡/剧本（Bark / A2A）均不挂载原生 tools
            // ── VT-NOTOOLS-T6: 工具调用已全量移除；以 Count 守卫替换 UseNativeToolCalling ──
            var openAiTools = AgentToolDefinitions.GetOpenAiToolsArray();
            if (includeTools && !reasoningModel && openAiTools.Count > 0)
            {
                requestBody["tools"] = openAiTools;
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
            // 排除 Bark 和 A2A 挂载工具调用
            bool includeTools = cacheContext != LlmContextTypes.NoTools
                             && cacheContext != LlmContextTypes.Bark
                             && cacheContext != LlmContextTypes.A2A
                             && (string.IsNullOrEmpty(cacheContext)
                                 || !cacheContext.StartsWith(LlmContextTypes.Editor, StringComparison.OrdinalIgnoreCase));

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

            Dictionary<string, object> requestBody = BuildRequestBody(messages, n_predict, stream: false, includeTools, cacheContext);
            ThinkingSuppressionPlan plan = EvaluateThinkingSuppression(modelName, url, cacheContext);
            ApplyThinkingSuppression(requestBody, plan);

            string endpointUrl = BuildEndpoint("chat/completions");

            int retryCount = allowRetry ? 3 : 1;
            string responseString = string.Empty;
            int statusCode = 500;
            bool strippedThinkingParameters = false;

            while (retryCount > 0)
            {
                try
                {
                    var genParams = ResolveParameters(cacheContext);
                    string jsonData = SerializePayloadWithCustomBody(requestBody, genParams.AllowCustomBody);
                    LogFinalPayloadSuppression(jsonData, endpointUrl);

                    if (AndroidHelper.IsAndroid && NetworkHelper.IsNetworkAvailable())
                    {
                        var headers = new Dictionary<string, string>
                        {
                            { "Authorization", EffectiveBearer(apiKey) }
                        };

                        responseString = await NetworkHelper.MakeRequestWithCustomHeadersAsync(
                            endpointUrl,
                            jsonData,
                            headers);

                        statusCode = LooksLikeErrorResponse(responseString) ? 400 : 200;
                    }
                    else
                    {
                        using (var request = new HttpRequestMessage(HttpMethod.Post, endpointUrl))
                        {
                            request.Headers.Add("Authorization", EffectiveBearer(apiKey));
                            request.Content = new StringContent(jsonData, Encoding.UTF8, "application/json");

                            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(ModEntry.Config.QueryTimeout)))
                            using (var response = await SharedHttpClient.SendAsync(request, cts.Token))
                            {
                                statusCode = (int)response.StatusCode;
                                responseString = await response.Content.ReadAsStringAsync();
                            }
                        }
                    }

                    bool isClientError = statusCode == 400 || statusCode == 422;

                    if (isClientError && !strippedThinkingParameters &&
                        (plan.AnyApiSuppression || LooksLikeUnknownParameterError(responseString)))
                    {
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
                            await Task.Delay(250);
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

                    var toolResponse = new LlmResponse(string.Empty, true);
                    JArray toolCallsArray = messageToken["tool_calls"] as JArray;

                    if (toolCallsArray != null && toolCallsArray.Count > 0)
                    {
                        foreach (JToken toolCall in toolCallsArray)
                        {
                            JToken functionToken = toolCall["function"];
                            if (functionToken == null) continue;

                            string functionName = functionToken["name"]?.ToString();
                            string functionArguments = functionToken["arguments"]?.ToString() ?? "{}";

                            if (!string.IsNullOrWhiteSpace(functionName))
                            {
                                toolResponse.ToolCalls.Add(new ToolCallData
                                {
                                    FunctionName = functionName,
                                    JsonArguments = functionArguments
                                });
                            }
                        }
                    }

                    string contentString = messageToken["content"]?.ToString();

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
                            await Task.Delay(250);
                        }
                        continue;
                    }

                    if (toolResponse.ToolCalls.Count > 0)
                    {
                        toolResponse.Text = contentString ?? string.Empty;
                        return toolResponse;
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
                catch (OperationCanceledException ex)
                {
                    Log.Debug("[LlmOpenAiBase] Request timeout: " + ex.Message);
                    retryCount--;
                }
                catch (Exception ex)
                {
                    Log.Debug("[LlmOpenAiBase] Request error: " + ex.Message);
                    retryCount--;
                }

                if (retryCount > 0)
                {
                    await Task.Delay(250);
                }
            }

            return new LlmResponse(responseString, statusCode);
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

            // 排除 Bark 和 A2A 挂载工具调用
            bool includeTools = cacheContext != LlmContextTypes.NoTools
                             && cacheContext != LlmContextTypes.Bark
                             && cacheContext != LlmContextTypes.A2A
                             && (string.IsNullOrEmpty(cacheContext)
                                 || !cacheContext.StartsWith(LlmContextTypes.Editor, StringComparison.OrdinalIgnoreCase));

            Dictionary<string, object> requestBody = BuildRequestBody(messages, n_predict, stream: true, includeTools, cacheContext);
            ThinkingSuppressionPlan plan = EvaluateThinkingSuppression(modelName, url, cacheContext);
            ApplyThinkingSuppression(requestBody, plan);

            string endpointUrl = BuildEndpoint("chat/completions");
            var genParams = ResolveParameters(cacheContext);
            string jsonData = SerializePayloadWithCustomBody(requestBody, genParams.AllowCustomBody);
            LogFinalPayloadSuppression(jsonData, endpointUrl);

            // TTFT observation
            var ttftWatch = new System.Diagnostics.Stopwatch();
            bool ttftLogged = false;

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
            if (hostModelKey != null && StrictHostStage.TryGetValue(hostModelKey, out rememberedStage) && rememberedStage > 0)
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
                                if (!response.IsSuccessStatusCode)
                                {
                                    string errContent = await response.Content.ReadAsStringAsync();
                                    int status = (int)response.StatusCode;
                                    bool isClientError = status == 400 || status == 422;

                                    // Try degradation on 400/422
                                    if (isClientError && stage < 2 &&
                                        (plan.AnyApiSuppression || LooksLikeUnknownParameterError(errContent)))
                                    {
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
                                        await Task.Delay(250);
                                        continue;
                                    }

                                    Log.Debug($"[LlmOpenAiBase] Streaming failed: {status}, Response: {errContent}");
                                    return new LlmResponse(errContent, status);
                                }

                                // Success after degradation → write to blacklist
                                if (stage > 0 && hostModelKey != null)
                                {
                                    StrictHostStage.AddOrUpdate(hostModelKey, (byte)stage, (_, existing) => (byte)Math.Max(existing, stage));
                                    ModEntry.SMonitor?.Log($"[LlmOpenAiBase] Blacklist updated: {hostModelKey} → stage {stage}", StardewModdingAPI.LogLevel.Debug);
                                }

                                var fullContentBuilder = new StringBuilder();
                                var toolCallsDict = new Dictionary<int, (string Name, StringBuilder Args)>();
                                bool degenerateDetected = false;
                                bool reasoningSeen = false;
                                long ttftReasoningMs = -1;

                                using (var stream = await response.Content.ReadAsStreamAsync())
                                using (var reader = new StreamReader(stream, Encoding.UTF8))
                                {
                                    while (!reader.EndOfStream && !linkedCts.Token.IsCancellationRequested)
                                    {
                                        string line = await reader.ReadLineAsync();
                                        if (line == null) break;

                                        line = line.Trim();
                                        if (string.IsNullOrWhiteSpace(line) || !line.StartsWith("data:"))
                                            continue;

                                        string data = line.Substring(5).Trim();
                                        if (data == "[DONE]") break;

                                        try
                                        {
                                            var chunkJson = JObject.Parse(data);
                                            var choices = chunkJson["choices"] as JArray;
                                            if (choices == null || choices.Count == 0) continue;

                                            var delta = choices[0]["delta"] as JObject;
                                            if (delta == null) continue;

                                            // Parse reasoning_content (parse-and-ignore)
                                            string reasoningToken = delta.Value<string>("reasoning_content");
                                            if (string.IsNullOrEmpty(reasoningToken))
                                                reasoningToken = delta.Value<string>("reasoning");

                                            if (!string.IsNullOrEmpty(reasoningToken))
                                            {
                                                if (!reasoningSeen)
                                                {
                                                    reasoningSeen = true;
                                                    ttftReasoningMs = ttftWatch.ElapsedMilliseconds;
                                                    ModEntry.SMonitor?.Log(
                                                        $"[LlmOpenAiBase] Model emitted reasoning_content — thinking suppression NOT honored by server. model={modelName}. Check FINAL PAYLOAD log.",
                                                        StardewModdingAPI.LogLevel.Warn);
                                                }
                                                continue; // Do NOT append to fullContentBuilder
                                            }

                                            var toolCalls = delta["tool_calls"] as JArray;
                                            if (toolCalls != null)
                                            {
                                                foreach (var tc in toolCalls)
                                                {
                                                    int index = tc.Value<int?>("index") ?? 0;
                                                    var fn = tc["function"];
                                                    if (fn != null)
                                                    {
                                                        string fnName = fn.Value<string>("name");
                                                        string fnArgs = fn.Value<string>("arguments");

                                                        if (!toolCallsDict.ContainsKey(index))
                                                        {
                                                            toolCallsDict[index] = (fnName ?? string.Empty, new StringBuilder());
                                                        }

                                                        if (!string.IsNullOrEmpty(fnName) && string.IsNullOrEmpty(toolCallsDict[index].Name))
                                                        {
                                                            toolCallsDict[index] = (fnName, toolCallsDict[index].Args);
                                                        }

                                                        if (!string.IsNullOrEmpty(fnArgs))
                                                        {
                                                            toolCallsDict[index].Args.Append(fnArgs);
                                                        }
                                                    }
                                                }
                                                continue;
                                            }

                                            string textToken = delta.Value<string>("content");
                                            if (!string.IsNullOrEmpty(textToken))
                                            {
                                                // TTFT: first content token
                                                if (!ttftLogged)
                                                {
                                                    ttftLogged = true;
                                                    ModEntry.SMonitor?.Log(
                                                        $"[LlmOpenAiBase] TTFT(content)={ttftWatch.ElapsedMilliseconds}ms | TTFT(reasoning)={(ttftReasoningMs >= 0 ? ttftReasoningMs + "ms" : "n/a")} | endpoint={endpointUrl}",
                                                        StardewModdingAPI.LogLevel.Debug);
                                                }

                                                fullContentBuilder.Append(textToken);
                                                onToken?.Invoke(textToken);

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

                                if (degenerateDetected)
                                {
                                    return new LlmResponse("Discarded degenerate/repetitive streaming output.", 500);
                                }

                                string completeText = fullContentBuilder.ToString();

                                LlmTrafficLogger.LogIncoming(cacheContext, completeText);

                                if (toolCallsDict.Count > 0)
                                {
                                    var toolResp = new LlmResponse(completeText, true);
                                    foreach (var kvp in toolCallsDict)
                                    {
                                        toolResp.ToolCalls.Add(new ToolCallData
                                        {
                                            FunctionName = kvp.Value.Name,
                                            JsonArguments = kvp.Value.Args.ToString()
                                        });
                                    }

                                    LlmTrafficLogger.LogIncomingToolCalls(cacheContext, toolResp.ToolCalls);
                                    return toolResp;
                                }

                                return new LlmResponse(completeText);
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            return new LlmResponse("Request timed out or cancelled.", 408);
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