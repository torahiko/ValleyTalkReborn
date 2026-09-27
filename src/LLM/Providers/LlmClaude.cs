using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Threading;
using System.Threading.Tasks;
using ValleytalkReborn.Platform;

namespace ValleytalkReborn;

internal class LlmClaude : Llm, IGetModelNames
{
    private readonly string apiKey;
    private readonly string modelName;

    class PromptElement
    {
#pragma warning disable IDE1006 // Naming Styles
        public string type { get; set; }
        public string text { get; set; }
        public object cache_control { get; set; }
#pragma warning restore IDE1006 // Naming Styles
    }

    /// <summary>
    /// 构建 Anthropic system 数组的分层 cache 结构。
    /// 三段各自独立 cache breakpoint：systemPrompt（全局静态，人设不变）→
    /// gameCache（按天级变化的游戏世界状态）→ npcCache（按天/事件级变化的 NPC 记忆与印象）。
    /// Anthropic 支持链式增量复用：只要前面的 breakpoint 命中，后面即使变化也只需为变化部分付费，
    /// 因此拆分粒度越细、越贴近实际变化频率，命中率越高。
    /// </summary>
    private static PromptElement[] BuildSystemBlocks(string systemPromptString, string gameCacheString, string npcCacheString)
    {
        var blocks = new List<PromptElement>
        {
            new() { type = "text", text = systemPromptString }
        };

        if (!string.IsNullOrWhiteSpace(gameCacheString))
        {
            blocks.Add(new PromptElement
            {
                type = "text",
                cache_control = new { type = "ephemeral" },
                text = gameCacheString
            });
        }

        if (!string.IsNullOrWhiteSpace(npcCacheString))
        {
            blocks.Add(new PromptElement
            {
                type = "text",
                cache_control = new { type = "ephemeral" },
                text = npcCacheString
            });
        }

        return blocks.ToArray();
    }

    /// <summary>
    /// 将内部 LlmChatMessage 列表标准化为 Anthropic messages 数组。
    /// 角色映射：user→user，assistant→assistant（其他角色视为编程错误）。
    /// 相邻同角色消息以 "\n\n" 合并，保留顺序与内容。
    /// responseStart 按现有语义作为末尾 assistant 消息追加（非空时）。
    /// </summary>
    internal static List<object> BuildClaudeMessages(IReadOnlyList<LlmChatMessage> messages, string responseStart)
    {
        if (messages == null) throw new ArgumentNullException(nameof(messages));

        var result = new List<object>();
        foreach (var m in messages)
        {
            if (m == null) throw new ArgumentException("Claude message list contains a null message.");
            if (m.Content == null) throw new ArgumentException("Claude message content is null.");

            string role;
            if (m.Role == "user") role = "user";
            else if (m.Role == "assistant") role = "assistant";
            else throw new ArgumentException($"Claude role-mapping error: unsupported role '{m.Role}'.");

            // 相邻同角色合并（以恰好两个换行符连接）
            if (result.Count > 0)
            {
                var last = result[result.Count - 1] as JObject;
                string lastRole = last?["role"]?.ToString();
                if (last != null && lastRole == role)
                {
                    last["content"] = last["content"]?.ToString() + "\n\n" + m.Content;
                    continue;
                }
            }
            result.Add(JObject.FromObject(new { role, content = m.Content }));
        }

        // 按现有 Claude 语义，responseStart 作为末尾 assistant 消息追加（非空时）
        if (!string.IsNullOrWhiteSpace(responseStart))
        {
            var last = result.Count > 0 ? result[result.Count - 1] as JObject : null;
            if (last != null && last["role"]?.ToString() == "assistant")
            {
                last["content"] = last["content"]?.ToString() + "\n\n" + responseStart;
            }
            else
            {
                result.Add(JObject.FromObject(new { role = "assistant", content = responseStart }));
            }
        }

        return result;
    }


    public LlmClaude(string apiKey, string modelName = null)
    {
        url = "https://api.anthropic.com/v1/messages";
        this.apiKey = apiKey;
        this.modelName = modelName ?? "claude-3-5-haiku-latest";
    }

    public Dictionary<string, string> CacheContexts { get; private set; } = new Dictionary<string, string>();

    public override string ExtraInstructions => "";
    public override bool IsHighlySensoredModel => true;

    public override bool SupportsStreamingWithTools => true;

    internal override async Task<LlmResponse> RunInference(
        string systemPromptString, string gameCacheString, string npcCacheString,
        string promptString, string responseStart = "", int n_predict = 2048,
        string cacheContext = "", bool allowRetry = true)
    {
        var anthropicTools = AgentToolDefinitions.GetAnthropicToolsArray();
        var tools = anthropicTools.Count > 0 ? (object)anthropicTools : null;
        var genParams = ResolveParameters(cacheContext);

        var inputString = JsonConvert.SerializeObject(new
        {
            thinking = new { type = "disabled" },
            model = this.modelName,
            max_tokens = genParams.MaxTokens,
            temperature = genParams.Temperature,
            top_p = genParams.TopP,
            system = BuildSystemBlocks(systemPromptString, gameCacheString, npcCacheString),
            messages = string.IsNullOrWhiteSpace(responseStart)
                ? new[] { new { role = "user", content = promptString } }
                : new object[]
                {
                    new { role = "user", content = promptString },
                    new { role = "assistant", content = responseStart }
                },
            tools
        }, new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });

        return await ExecuteClaudeNonStreamingAsync(inputString, allowRetry);
    }

    internal override async Task<LlmResponse> RunStreamingInference(
        string systemPromptString, string gameCacheString, string npcCacheString,
        string promptString, Action<string> onToken, CancellationToken ct,
        string responseStart = "", int n_predict = 2048,
        string cacheContext = "")
    {
        var anthropicTools = AgentToolDefinitions.GetAnthropicToolsArray();
        var tools = anthropicTools.Count > 0 ? (object)anthropicTools : null;
        var genParams = ResolveParameters(cacheContext);

        var inputString = JsonConvert.SerializeObject(new
        {
            thinking = new { type = "disabled" },
            model = modelName,
            max_tokens = genParams.MaxTokens,
            temperature = genParams.Temperature,
            top_p = genParams.TopP,
            stream = true,
            system = BuildSystemBlocks(systemPromptString, gameCacheString, npcCacheString),
            messages = string.IsNullOrWhiteSpace(responseStart)
                ? new[] { new { role = "user", content = promptString } }
                : new object[]
                {
                    new { role = "user", content = promptString },
                    new { role = "assistant", content = responseStart }
                },
            tools
        }, new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });

        return await ExecuteClaudeStreamingAsync(inputString, onToken, ct);
    }

    /// <summary>
    /// 共享的 Claude 非流式 HTTP 执行与响应解析（Anthropic messages API）。
    /// </summary>
    internal async Task<LlmResponse> ExecuteClaudeNonStreamingAsync(string inputString, bool allowRetry)
    {
        int retry = allowRetry ? 3 : 1;
        var fullUrl = url;

        if (AndroidHelper.IsAndroid && !NetworkHelper.IsNetworkAvailable())
            throw new InvalidOperationException("Network not available");

        string responseString = "";
        int apiResponseCode = 500;

        while (retry > 0)
        {
            try
            {
                var headers = new Dictionary<string, string>
                {
                    { "x-api-key", apiKey },
                    { "anthropic-version", "2023-06-01" },
                    { "anthropic-beta", "prompt-caching-2024-07-31" }
                };

                if (AndroidHelper.IsAndroid)
                {
                    responseString = await NetworkHelper.MakeRequestWithCustomHeadersAsync(fullUrl, inputString, headers);
                }
                else
                {
                    using var req = new HttpRequestMessage(HttpMethod.Post, fullUrl);
                    req.Content = new StringContent(inputString, Encoding.UTF8, "application/json");
                    foreach (var kvp in headers)
                        req.Headers.Add(kvp.Key, kvp.Value);

                    using var resp = await SharedHttpClient.SendAsync(req);
                    apiResponseCode = (int)resp.StatusCode;
                    responseString = await resp.Content.ReadAsStringAsync();
                }
                var responseJson = JObject.Parse(responseString);

                if (responseJson == null)
                    throw new Exception("Failed to parse response");

                if (!responseJson.TryGetValue("content", out var contentToken) || contentToken.Type == JTokenType.Null)
                {
                    retry--; continue;
                }

                var contentArray = contentToken as JArray;
                if (contentArray == null || !contentArray.HasValues)
                {
                    retry--; continue;
                }

                var toolResponse = new LlmResponse("", true);
                string textContent = null;

                foreach (var element in contentArray)
                {
                    var elementType = element["type"]?.ToString();
                    if (elementType == "tool_use")
                    {
                        var funcName = element["name"]?.ToString();
                        var inputToken = element["input"];
                        var funcArgs = inputToken != null ? inputToken.ToString(Formatting.None) : "{}";
                        if (!string.IsNullOrEmpty(funcName))
                            toolResponse.ToolCalls.Add(new ToolCallData { FunctionName = funcName, JsonArguments = funcArgs });
                    }
                    else if (elementType == "text" && textContent == null)
                    {
                        textContent = element["text"]?.ToString();
                    }
                }

                if (toolResponse.ToolCalls.Count > 0)
                {
                    toolResponse.Text = textContent ?? "";
                    Log.Debug($"[LlmClaude] Tool calls received: {toolResponse.ToolCalls.Count}");
                    return toolResponse;
                }

                if (!string.IsNullOrWhiteSpace(textContent))
                    return new LlmResponse(textContent);

                retry--;
            }
            catch (Exception ex)
            {
                if (ex.InnerException is HttpRequestException httpEx)
                    apiResponseCode = (int)(httpEx.StatusCode ?? 0);
                Log.Debug(ex.Message);
                Log.Debug("Retrying...");
                retry--;
                await Task.Delay(100);
            }
        }
        return new LlmResponse(responseString, apiResponseCode);
    }

    /// <summary>
    /// 共享的 Claude 流式 HTTP 执行与 SSE 解析（Anthropic messages API）。
    /// </summary>
    internal async Task<LlmResponse> ExecuteClaudeStreamingAsync(string inputString, Action<string> onToken, CancellationToken ct)
    {
        if (AndroidHelper.IsAndroid && !NetworkHelper.IsNetworkAvailable())
            throw new InvalidOperationException("Network not available");

        var fullText = new StringBuilder();
        var streamedToolCalls = new List<ToolCallData>();
        var toolBlocksByIndex = new Dictionary<int, (string name, StringBuilder args)>();

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Content = new StringContent(inputString, Encoding.UTF8, "application/json");
            request.Headers.Add("x-api-key", apiKey);
            request.Headers.Add("anthropic-version", "2023-06-01");
            request.Headers.Add("anthropic-beta", "prompt-caching-2024-07-31");

            using var response = await SharedHttpClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, ct);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync();
                throw new InvalidOperationException(
                    $"Claude streaming failed: HTTP {(int)response.StatusCode} - {errorBody}");
            }

            using var stream = await response.Content.ReadAsStreamAsync();
            using var reader = new System.IO.StreamReader(stream);

            while (!reader.EndOfStream && !ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync();
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (!line.StartsWith("data:")) continue;

                var data = line.Substring(5).Trim();
                if (data == "[DONE]") break;

                try
                {
                    var json = JObject.Parse(data);
                    var eventType = json["type"]?.ToString();

                    if (eventType == "content_block_start")
                    {
                        var blockType = json["content_block"]?["type"]?.ToString();
                        if (blockType == "tool_use")
                        {
                            int index = json["index"]?.Value<int>() ?? 0;
                            var blockName = json["content_block"]?["name"]?.ToString();
                            toolBlocksByIndex[index] = (blockName ?? "", new StringBuilder());
                        }
                        continue;
                    }

                    if (eventType == "content_block_delta")
                    {
                        var delta = json["delta"];
                        if (delta == null) continue;
                        var deltaType = delta["type"]?.ToString();
                        if (deltaType == "text_delta")
                        {
                            var text = delta["text"]?.ToString();
                            if (!string.IsNullOrEmpty(text))
                            {
                                fullText.Append(text);
                                onToken(text);
                            }
                        }
                        else if (deltaType == "input_json_delta")
                        {
                            int index = json["index"]?.Value<int>() ?? 0;
                            var partialJson = delta["partial_json"]?.ToString();
                            if (!string.IsNullOrEmpty(partialJson) && toolBlocksByIndex.TryGetValue(index, out var entry))
                                entry.args.Append(partialJson);
                        }
                        continue;
                    }

                    if (eventType == "content_block_stop")
                    {
                        int index = json["index"]?.Value<int>() ?? 0;
                        if (toolBlocksByIndex.TryGetValue(index, out var entry))
                        {
                            streamedToolCalls.Add(new ToolCallData
                            {
                                FunctionName = entry.name,
                                JsonArguments = entry.args.ToString()
                            });
                            toolBlocksByIndex.Remove(index);
                        }
                    }
                }
                catch { }
            }

            if (streamedToolCalls.Count > 0)
            {
                var toolResp = new LlmResponse(fullText.ToString(), true);
                toolResp.ToolCalls.AddRange(streamedToolCalls);
                return toolResp;
            }

            return new LlmResponse(fullText.ToString(), fullText.Length > 0);
        }
        catch (OperationCanceledException)
        {
            return new LlmResponse(fullText.ToString(), fullText.Length > 0);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[LlmClaude] Streaming failed, falling back to non-streaming");
            return await base.RunStreamingInference(null, null, null, null, null, ct);
        }
    }

    // ── Claude 主对话 role-based 入口（PROMPT-ARCH-04A） ──

    /// <summary>
    /// 非流式 Claude role-based 主对话推理。使用 BuildRuntimeChatMessages 产出的角色消息序列。
    /// </summary>
    internal async Task<LlmResponse> RunClaudeChatInference(
        string systemPromptString,
        string gameCacheString,
        string npcCacheString,
        IReadOnlyList<LlmChatMessage> messages,
        CancellationToken ct,
        string responseStart = "",
        int n_predict = 2048,
        string cacheContext = "",
        bool allowRetry = true)
    {
        var anthropicTools = AgentToolDefinitions.GetAnthropicToolsArray();
        var tools = anthropicTools.Count > 0 ? (object)anthropicTools : null;
        var genParams = ResolveParameters(cacheContext);

        var inputString = JsonConvert.SerializeObject(new
        {
            thinking = new { type = "disabled" },
            model = this.modelName,
            max_tokens = genParams.MaxTokens,
            temperature = genParams.Temperature,
            top_p = genParams.TopP,
            system = BuildSystemBlocks(systemPromptString, gameCacheString, npcCacheString),
            messages = BuildClaudeMessages(messages, responseStart),
            tools
        }, new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });

        return await ExecuteClaudeNonStreamingAsync(inputString, allowRetry);
    }

    /// <summary>
    /// 流式 Claude role-based 主对话推理。使用 BuildRuntimeChatMessages 产出的角色消息序列。
    /// </summary>
    internal async Task<LlmResponse> RunClaudeStreamingChatInference(
        string systemPromptString,
        string gameCacheString,
        string npcCacheString,
        IReadOnlyList<LlmChatMessage> messages,
        Action<string> onToken,
        CancellationToken ct,
        string responseStart = "",
        int n_predict = 2048,
        string cacheContext = "")
    {
        var anthropicTools = AgentToolDefinitions.GetAnthropicToolsArray();
        var tools = anthropicTools.Count > 0 ? (object)anthropicTools : null;
        var genParams = ResolveParameters(cacheContext);

        var inputString = JsonConvert.SerializeObject(new
        {
            thinking = new { type = "disabled" },
            model = this.modelName,
            max_tokens = genParams.MaxTokens,
            temperature = genParams.Temperature,
            top_p = genParams.TopP,
            stream = true,
            system = BuildSystemBlocks(systemPromptString, gameCacheString, npcCacheString),
            messages = BuildClaudeMessages(messages, responseStart),
            tools
        }, new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });

        return await ExecuteClaudeStreamingAsync(inputString, onToken, ct);
    }

    internal override Dictionary<string, double>[] RunInferenceProbabilities(string fullPrompt, int n_predict = 1)
    {
        throw new NotImplementedException();
    }

    public async Task<string[]> GetModelNamesAsync()
    {
        if (string.IsNullOrEmpty(apiKey))
        {
            return Array.Empty<string>();
        }
        
        try 
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.anthropic.com/v1/models");
            request.Headers.Add("x-api-key", apiKey);
            request.Headers.Add("anthropic-version", "2023-06-01");
            
            using var response = await SharedHttpClient.SendAsync(request);
            var responseString = await response.Content.ReadAsStringAsync();
            var responseJson = JObject.Parse(responseString);
            
            var models = responseJson["data"] as JArray;
            var modelNames = new List<string>();
            if (models != null)
            {
                foreach (var model in models)
                {
                    var idToken = model["id"];
                    if (idToken != null)
                    {
                        modelNames.Add(idToken.ToString());
                    }
                }
            }
            return modelNames.ToArray();
        }
        catch (Exception ex)
        {
            Log.Error(ex.Message);
            return Array.Empty<string>();
        }
    }
}