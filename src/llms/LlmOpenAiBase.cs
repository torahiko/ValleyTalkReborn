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
    internal enum ThinkingModeStrategy
    {
        /// <summary>不发送任何思考相关字段（适用于 Gemma、Llama、Mistral、GPT-4o、传统非推理模型）</summary>
        None,

        /// <summary>DeepSeek 规范：thinking: { type: "disabled" }</summary>
        DeepSeekOfficial,

        /// <summary>Gemini 系列规范：全方位覆盖 OneAPI/NewAPI/官方原生透传参数</summary>
        GeminiStyle,

        /// <summary>Anthropic / Claude 规范</summary>
        AnthropicStyle,

        /// <summary>SiliconFlow (硅基流动) 格式</summary>
        SiliconFlow,

        /// <summary>OpenRouter 格式：reasoning: { effort: "none" }</summary>
        OpenRouter
    }

    internal abstract class LlmOpenAiBase : Llm
    {
        protected string apiKey;
        protected string modelName;

        private static readonly HttpClient SharedHttpClient = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(2)
        };

        #region 模型列表

        protected async Task<string[]> CoreGetModelNamesAsync()
        {
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                return Array.Empty<string>();
            }

            try
            {
                string modelsUrl = BuildEndpoint("models");

                if (AndroidHelper.IsAndroid && NetworkHelper.IsNetworkAvailable())
                {
                    var headers = new Dictionary<string, string>
                    {
                        { "Authorization", "Bearer " + apiKey }
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
                        request.Headers.Add("Authorization", "Bearer " + apiKey);

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

        private bool IsPlainTextModel(string model)
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

        private bool IsLikelyReasoningModel(string model)
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

        protected virtual ThinkingModeStrategy DetectThinkingModeStrategy()
        {
            string model = modelName ?? string.Empty;
            string m = model.ToLowerInvariant();
            string endpoint = (url ?? string.Empty).ToLowerInvariant();

            if (IsPlainTextModel(model))
            {
                return ThinkingModeStrategy.None;
            }

            if (endpoint.Contains("openrouter.ai"))
            {
                return ThinkingModeStrategy.OpenRouter;
            }

            if (endpoint.Contains("siliconflow.cn") || endpoint.Contains("siliconflow.com"))
            {
                return ThinkingModeStrategy.SiliconFlow;
            }

            if (m.Contains("deepseek"))
            {
                return ThinkingModeStrategy.DeepSeekOfficial;
            }

            if (m.Contains("gemini"))
            {
                return ThinkingModeStrategy.GeminiStyle;
            }

            if (m.Contains("claude"))
            {
                return ThinkingModeStrategy.AnthropicStyle;
            }

            return ThinkingModeStrategy.None;
        }

        private void ApplyThinkingModeParameters(
            Dictionary<string, object> requestBody,
            ThinkingModeStrategy strategy)
        {
            switch (strategy)
            {
                case ThinkingModeStrategy.DeepSeekOfficial:
                    requestBody["thinking"] = new { type = "disabled" };
                    break;

                case ThinkingModeStrategy.GeminiStyle:
                    requestBody["thinking"] = new { type = "disabled", budget_tokens = 0 };
                    requestBody["thinking_config"] = new { thinking_budget = 0 };
                    requestBody["thinking_budget"] = 0;
                    requestBody["reasoning_effort"] = "none";
                    break;

                case ThinkingModeStrategy.AnthropicStyle:
                    requestBody["thinking"] = new { type = "disabled" };
                    requestBody["reasoning"] = new { effort = "none" };
                    break;

                case ThinkingModeStrategy.SiliconFlow:
                    requestBody["enable_thinking"] = false;
                    requestBody["include_reasoning"] = false;
                    break;

                case ThinkingModeStrategy.OpenRouter:
                    requestBody["reasoning"] = new { effort = "none" };
                    break;

                case ThinkingModeStrategy.None:
                default:
                    break;
            }
        }

        private void StripThinkingParameters(Dictionary<string, object> requestBody, int nPredict)
        {
            requestBody.Remove("thinking");
            requestBody.Remove("thinking_config");
            requestBody.Remove("thinking_budget");
            requestBody.Remove("disable_thinking");
            requestBody.Remove("enable_thinking");
            requestBody.Remove("reasoning");
            requestBody.Remove("reasoning_effort");
            requestBody.Remove("include_reasoning");
            requestBody.Remove("max_completion_tokens");

            requestBody["max_tokens"] = nPredict;
            requestBody["temperature"] = 0.9;
            requestBody["top_p"] = 0.9;
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

                // 跳过代码导入行
                if (trimmed.StartsWith("import ") || trimmed.StartsWith("export "))
                    continue;

                // 跳过注释行
                if (trimmed.StartsWith("//") || trimmed.StartsWith("/*") || trimmed == "*/")
                    continue;

                // 跳过空行（在找到 JSON 开头之前）
                if (!foundJsonStart && string.IsNullOrWhiteSpace(trimmed))
                    continue;

                // 检测 JSON 数组开头
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
            bool includeTools = true)
        {
            var requestBody = new Dictionary<string, object>
            {
                { "model", modelName },
                { "messages", messages },
                { "stream", stream }
            };

            bool reasoningModel = IsLikelyReasoningModel(modelName);

            if (reasoningModel)
            {
                requestBody["max_completion_tokens"] = nPredict;
                requestBody["reasoning_effort"] = "none";
            }
            else
            {
                requestBody["max_tokens"] = nPredict;
                requestBody["temperature"] = 0.8;
                requestBody["top_p"] = 0.9;
            }

            if (includeTools && ModEntry.Config.UseNativeToolCalling)
            {
                requestBody["tools"] = AgentToolDefinitions.GetOpenAiToolsArray();
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
            // 🌟 核心拦截器：为了兼容 CF 强制流式要求
            // 只要开启了流式设置，即使是不带打字效果的后台生成，也强制走 SSE 流式请求，
            // 只是将回调设为 null 进行静默缓冲，等全部接收完再一起返回。
            bool includeTools = cacheContext != "NO_TOOLS";
            if (ModEntry.Config.EnableStreaming && !AndroidHelper.IsAndroid)
            {
                return await RunStreamingInference(
                    systemPromptString, 
                    gameCacheString, 
                    npcCacheString, 
                    promptString, 
                    onToken: null, // 隐藏回调，静默接收
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

            messages.Add(new { role = "user", content = promptString });

            if (!string.IsNullOrWhiteSpace(responseStart) &&
                !responseStart.Trim().Equals("responseStart", StringComparison.OrdinalIgnoreCase))
            {
                messages.Add(new { role = "assistant", content = responseStart });
            }

            Dictionary<string, object> requestBody = BuildRequestBody(messages, n_predict, stream: false, includeTools);
            ThinkingModeStrategy strategy = DetectThinkingModeStrategy();
            ApplyThinkingModeParameters(requestBody, strategy);

            string endpointUrl = BuildEndpoint("chat/completions");

            int retryCount = allowRetry ? 3 : 1;
            string responseString = string.Empty;
            int statusCode = 500;
            bool strippedThinkingParameters = false;

            while (retryCount > 0)
            {
                try
                {
                    string jsonData = JsonConvert.SerializeObject(requestBody);

                    if (AndroidHelper.IsAndroid && NetworkHelper.IsNetworkAvailable())
                    {
                        var headers = new Dictionary<string, string>
                        {
                            { "Authorization", "Bearer " + apiKey }
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
                            request.Headers.Add("Authorization", "Bearer " + apiKey);
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
                        (strategy != ThinkingModeStrategy.None || LooksLikeUnknownParameterError(responseString)))
                    {
                        Log.Debug("[LlmOpenAiBase] Server rejected thinking parameters. Retrying with pure standard payload.");

                        StripThinkingParameters(requestBody, n_predict);
                        strategy = ThinkingModeStrategy.None;
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
            // Android 平台由于网络桥接库限制，回退为非流式
            if (AndroidHelper.IsAndroid)
            {
                var fallback = await RunInference(systemPromptString, gameCacheString, npcCacheString, promptString, responseStart, n_predict);
                if (fallback.IsSuccess && !string.IsNullOrWhiteSpace(fallback.Text))
                {
                    onToken?.Invoke(fallback.Text);
                }
                return fallback;
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

            messages.Add(new { role = "user", content = promptString });

            if (!string.IsNullOrWhiteSpace(responseStart) &&
                !responseStart.Trim().Equals("responseStart", StringComparison.OrdinalIgnoreCase))
            {
                messages.Add(new { role = "assistant", content = responseStart });
            }

            bool includeTools = cacheContext != "NO_TOOLS";
            Dictionary<string, object> requestBody = BuildRequestBody(messages, n_predict, stream: true, includeTools);
            ThinkingModeStrategy strategy = DetectThinkingModeStrategy();
            ApplyThinkingModeParameters(requestBody, strategy);

            string endpointUrl = BuildEndpoint("chat/completions");
            string jsonData = JsonConvert.SerializeObject(requestBody);

            using (var request = new HttpRequestMessage(HttpMethod.Post, endpointUrl))
            {
                request.Headers.Add("Authorization", "Bearer " + apiKey);
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
                                Log.Debug($"[LlmOpenAiBase] Streaming failed: {(int)response.StatusCode}, Response: {errContent}");
                                return new LlmResponse(errContent, (int)response.StatusCode);
                            }

                            var fullContentBuilder = new StringBuilder();
                            var toolCallsDict = new Dictionary<int, (string Name, StringBuilder Args)>();

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

                                        var delta = choices[0]["delta"];
                                        if (delta == null) continue;

                                        // 处理原生工具调用分片
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
                                        }

                                        // 捕获增量正文
                                        string textToken = delta["content"]?.ToString();
                                        if (!string.IsNullOrEmpty(textToken))
                                        {
                                            fullContentBuilder.Append(textToken);
                                            onToken?.Invoke(textToken);
                                        }
                                    }
                                    catch (Exception parseEx)
                                    {
                                        Log.Debug("[LlmOpenAiBase] Chunk JSON parse skip: " + parseEx.Message);
                                    }
                                }
                            }

                            string completeText = fullContentBuilder.ToString();

                            // 如果有工具调用产生，组装并返回
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

        #endregion

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