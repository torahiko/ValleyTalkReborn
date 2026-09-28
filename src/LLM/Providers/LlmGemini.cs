using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Threading;
using System.Threading.Tasks;
using ValleytalkReborn.Platform;

namespace ValleytalkReborn;

internal class LlmGemini : Llm, IGetModelNames
{
    private readonly string apiKey;
    private readonly string modelName;

    public LlmGemini(string apiKey, string modelName = null)
    {
        this.apiKey = apiKey;
        this.modelName = modelName ?? "gemini-2.5-flash";

        url = $"https://generativelanguage.googleapis.com/v1beta/models/{this.modelName}:generateContent?key=";
    }

    public Dictionary<string, string> CacheContexts { get; private set; } = new Dictionary<string, string>();

    public override string ExtraInstructions => "";
    public override bool IsHighlySensoredModel => false;

    public async Task<string[]> GetModelNamesAsync()
    {
        try
        {
            var modelsUrl = $"https://generativelanguage.googleapis.com/v1beta/models?key=" + apiKey;
            
            string responseString;
            if (AndroidHelper.IsAndroid && NetworkHelper.IsNetworkAvailable())
            {
                responseString = await NetworkHelper.MakeRequestAsync(modelsUrl);
            }
            else
            {
                responseString = await SharedHttpClient.GetStringAsync(modelsUrl);
            }
            
            var responseJson = JObject.Parse(responseString); 
            var modelsToken = responseJson["models"]; 
            var modelNames = new List<string>();
            
            if (modelsToken is JArray modelsArray) 
            {
                foreach (var model in modelsArray)
                {
                    var nameToken = model["name"]; 
                    if (nameToken != null)
                    {
                        var name = nameToken.ToString(); 
                        if (name.StartsWith("models/"))
                        {
                            name = name.Substring(7);
                        }
                        modelNames.Add(name);
                    }
                }
            }
            return modelNames.ToArray();
        }
        catch (Exception ex)
        {
            Log.Debug(ex.Message);
            return Array.Empty<string>();
        }
    }

    internal override async Task<LlmResponse> RunInference(
        string systemPromptString, string gameCacheString, string npcCacheString, 
        string promptString, string responseStart = "", int n_predict = 2048, 
        string cacheContext = "", bool allowRetry = true)
    {
        promptString = gameCacheString + npcCacheString + promptString;

        int thinkingBudget = 0;

        bool isGemmaModel = !string.IsNullOrEmpty(modelName) && modelName.IndexOf("gemma", StringComparison.OrdinalIgnoreCase) >= 0;

        // ★ 从单一数据源解析参数（自动完成 Bark/A2A 豁免）
        var genParams = ResolveParameters(cacheContext);

        object generationConfig = isGemmaModel
            ? (object)new { maxOutputTokens = genParams.MaxTokens, temperature = genParams.Temperature, topP = genParams.TopP }
            : new { maxOutputTokens = genParams.MaxTokens, temperature = genParams.Temperature, topP = genParams.TopP, thinkingConfig = new { thinkingBudget } };

        // 🌟 修复：精细化对齐 Gemini 官方 REST 接口标准（contents 设为数组结构）
        var jsonData = JsonConvert.SerializeObject(new
        {
            safetySettings = new[]
            {
                new { category = "HARM_CATEGORY_SEXUALLY_EXPLICIT", threshold = "BLOCK_NONE" },
                new { category = "HARM_CATEGORY_HARASSMENT", threshold = "BLOCK_MEDIUM_AND_ABOVE" }
            },
            system_instruction = new { parts = new[] { new { text = systemPromptString } } },
            contents = new[] { new { parts = new[] { new { text = promptString } } } },
            generationConfig
        }, new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });

        int retry = allowRetry ? 3 : 1;
        var fullUrl = url + apiKey;
        
        if (AndroidHelper.IsAndroid && !NetworkHelper.IsNetworkAvailable())
        {
            throw new InvalidOperationException("Network not available");
        }

        string responseString = "";
        int statusCode = 500;

        while (retry > 0)
        {
            try
            {
                if (AndroidHelper.IsAndroid)
                {
                    responseString = await NetworkHelper.MakeRequestAsync(fullUrl, jsonData);
                    statusCode = 200;
                }
                else
                {
                    using var jsonContent = new StringContent(jsonData, Encoding.UTF8, "application/json");
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(ModEntry.Config.QueryTimeout));
                    var response = await SharedHttpClient.PostAsync(fullUrl, jsonContent, cts.Token);
                    
                    statusCode = (int)response.StatusCode;
                    responseString = await response.Content.ReadAsStringAsync();
                }
                
                var responseJson = JObject.Parse(responseString); 
                if (responseJson == null)
                {
                    throw new Exception("Failed to parse response");
                }
                
                if (!responseJson.TryGetValue("candidates", out var candidatesToken) || !(candidatesToken is JArray candidatesArray) || !candidatesArray.HasValues) { retry--; continue; } 
                
                var firstCandidate = candidatesArray.FirstOrDefault();
                if (firstCandidate == null) { retry--; continue; } 

                var finishReasonToken = firstCandidate["finishReason"];
                if (finishReasonToken == null || finishReasonToken.ToString() != "STOP") { retry--; continue; } 
                
                var contentToken = firstCandidate["content"];
                if (contentToken == null) { retry--; continue; } 

                var partsToken = contentToken["parts"];
                if (!(partsToken is JArray partsArray) || !partsArray.HasValues) { retry--; continue; }

                var firstPart = partsArray.FirstOrDefault();
                if (firstPart == null) { retry--; continue; }

                var textToken = firstPart["text"];
                if (textToken == null) { retry--; continue; }
                
                var text = textToken.ToString(); 
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return new LlmResponse(text);
                }
                
                return new LlmResponse("Empty response", statusCode);
            }
            catch (Exception ex)
            {
                Log.Debug(ex.Message);
                Log.Debug("Retrying...");
                retry--;
                await Task.Delay(100);
            }
        }
        return new LlmResponse(responseString, statusCode);
    }

    internal override async Task<LlmResponse> RunStreamingInference(
        string systemPromptString, string gameCacheString, string npcCacheString,
        string promptString, Action<string> onToken, CancellationToken ct,
        string responseStart = "", int n_predict = 2048,
        string cacheContext = "")
    {
        if (AndroidHelper.IsAndroid && !NetworkHelper.IsNetworkAvailable())
            throw new InvalidOperationException("Network not available");

        bool isGemmaModel = !string.IsNullOrEmpty(modelName) && modelName.IndexOf("gemma", StringComparison.OrdinalIgnoreCase) >= 0;

        // ★ 从单一数据源解析参数（自动完成 Bark/A2A 豁免）
        var genParams = ResolveParameters(cacheContext);

        object generationConfig = isGemmaModel
            ? (object)new { maxOutputTokens = genParams.MaxTokens, temperature = genParams.Temperature, topP = genParams.TopP }
            : new { maxOutputTokens = genParams.MaxTokens, temperature = genParams.Temperature, topP = genParams.TopP, thinkingConfig = new { thinkingBudget = 0 } };

        var jsonData = JsonConvert.SerializeObject(new
        {
            safetySettings = new[]
            {
                new { category = "HARM_CATEGORY_SEXUALLY_EXPLICIT", threshold = "BLOCK_NONE" },
                new { category = "HARM_CATEGORY_HARASSMENT", threshold = "BLOCK_MEDIUM_AND_ABOVE" }
            },
            system_instruction = new { parts = new[] { new { text = systemPromptString } } },
            contents = new[] { new { parts = new[] { new { text = gameCacheString + npcCacheString + promptString } } } },
            generationConfig
        }, new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });

        var streamUrl = $"https://generativelanguage.googleapis.com/v1beta/models/" +
                        $"{modelName}:streamGenerateContent?alt=sse&key={apiKey}";

        var fullText = new StringBuilder();

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, streamUrl);
            request.Content = new StringContent(jsonData, Encoding.UTF8, "application/json");

            using var response = await SharedHttpClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, ct);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync();
                throw new InvalidOperationException(
                    $"Gemini streaming failed: HTTP {(int)response.StatusCode} - {errorBody}");
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
                    var candidates = json["candidates"] as JArray;
                    var parts = candidates?[0]?["content"]?["parts"] as JArray;

                    if (parts == null) continue;

                    foreach (var part in parts)
                    {
                        var text = part["text"]?.ToString();
                        if (!string.IsNullOrEmpty(text))
                        {
                            fullText.Append(text);
                            onToken(text);
                        }
                    }
                }
                catch { }
            }

            return new LlmResponse(fullText.ToString(), fullText.Length > 0);
        }
        catch (OperationCanceledException)
        {
            return new LlmResponse(fullText.ToString(), fullText.Length > 0);
        }
        catch (Exception ex)
        {
            Log.Error(ex, "[LlmGemini] Streaming failed, falling back to non-streaming");
            return await base.RunStreamingInference(
                systemPromptString, gameCacheString, npcCacheString,
                promptString, onToken, ct, responseStart, n_predict);
        }
    }

    /// <summary>
    /// 将内部 LlmChatMessage 序列转换为 Gemini contents 数组。
    /// 角色映射：user→user，assistant→model（其他角色视为编程错误）。
    /// 相邻同角色消息以恰好 "\n\n" 合并，保留顺序与全部内容。
    /// 纯函数，不发起 HTTP 请求；供流式与非流式 role-based 路径共用。
    /// </summary>
    internal static List<object> BuildGeminiContents(IReadOnlyList<LlmChatMessage> messages)
    {
        if (messages == null) throw new ArgumentNullException(nameof(messages));

        var result = new List<object>();
        foreach (var m in messages)
        {
            if (m == null) throw new ArgumentException("Gemini message list contains a null message.");
            if (m.Content == null) throw new ArgumentException("Gemini message content is null.");

            string role;
            if (m.Role == "user") role = "user";
            else if (m.Role == "assistant") role = "model";
            else throw new ArgumentException($"Gemini role-mapping error: unsupported role '{m.Role}'.");

            string text = m.Content;
            // 相邻同角色合并（以恰好两个换行符连接）
            if (result.Count > 0)
            {
                var last = result[result.Count - 1] as JObject;
                string lastRole = last?["role"]?.ToString();
                if (last != null && lastRole == role)
                {
                    string prevText = last["parts"]?[0]?["text"]?.ToString() ?? "";
                    last["parts"][0]["text"] = prevText + "\n\n" + text;
                    continue;
                }
            }
            result.Add(JObject.FromObject(new { role, parts = new[] { new { text } } }));
        }
        return result;
    }

    /// <summary>
    /// 构建 Gemini role-based 请求 payload（流式与非流式共用）。
    /// responseStart 不序列化（当前 Gemini 语义：参数保留但不插入 contents）。
    /// </summary>
    internal static string BuildGeminiRolePayload(
        string systemPromptString,
        IReadOnlyList<LlmChatMessage> messages,
        object generationConfig)
    {
        return JsonConvert.SerializeObject(new
        {
            safetySettings = new[]
            {
                new { category = "HARM_CATEGORY_SEXUALLY_EXPLICIT", threshold = "BLOCK_NONE" },
                new { category = "HARM_CATEGORY_HARASSMENT", threshold = "BLOCK_MEDIUM_AND_ABOVE" }
            },
            system_instruction = new { parts = new[] { new { text = systemPromptString } } },
            contents = BuildGeminiContents(messages),
            generationConfig
        }, new JsonSerializerSettings { NullValueHandling = NullValueHandling.Ignore });
    }

    // ── Gemini 主对话 role-based 入口（PROMPT-ARCH-04B） ──

    /// <summary>
    /// 非流式 Gemini role-based 主对话推理。使用 BuildRuntimeChatMessages 产出的角色消息序列。
    /// 保留 safetySettings / system_instruction / generationConfig / tools 与现有解析语义。
    /// responseStart 保留为参数但不序列化（与现有 Gemini 行为一致）。
    /// </summary>
    internal async Task<LlmResponse> RunGeminiChatInference(
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
        bool isGemmaModel = !string.IsNullOrEmpty(modelName) && modelName.IndexOf("gemma", StringComparison.OrdinalIgnoreCase) >= 0;
        var genParams = ResolveParameters(cacheContext);

        int thinkingBudget = 0;
        object generationConfig = isGemmaModel
            ? (object)new { maxOutputTokens = genParams.MaxTokens, temperature = genParams.Temperature, topP = genParams.TopP }
            : new { maxOutputTokens = genParams.MaxTokens, temperature = genParams.Temperature, topP = genParams.TopP, thinkingConfig = new { thinkingBudget } };

        var jsonData = BuildGeminiRolePayload(systemPromptString, messages, generationConfig);

        int retry = allowRetry ? 3 : 1;
        var fullUrl = url + apiKey;

        if (AndroidHelper.IsAndroid && !NetworkHelper.IsNetworkAvailable())
            throw new InvalidOperationException("Network not available");

        string responseString = "";
        int statusCode = 500;

        using (var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            linkedCts.CancelAfter(TimeSpan.FromSeconds(ModEntry.Config.QueryTimeout));
            var linkedToken = linkedCts.Token;

            while (retry > 0)
            {
                try
                {
                    if (AndroidHelper.IsAndroid)
                    {
                        responseString = await NetworkHelper.MakeRequestAsync(fullUrl, jsonData, linkedToken);
                        statusCode = 200;
                    }
                    else
                    {
                        using var jsonContent = new StringContent(jsonData, Encoding.UTF8, "application/json");
                        var response = await SharedHttpClient.PostAsync(fullUrl, jsonContent, linkedToken);

                        statusCode = (int)response.StatusCode;
                        responseString = await response.Content.ReadAsStringAsync();
                    }

                    var responseJson = JObject.Parse(responseString);
                    if (responseJson == null)
                        throw new Exception("Failed to parse response");

                    if (!responseJson.TryGetValue("candidates", out var candidatesToken) || !(candidatesToken is JArray candidatesArray) || !candidatesArray.HasValues) { retry--; continue; }

                    var firstCandidate = candidatesArray.FirstOrDefault();
                    if (firstCandidate == null) { retry--; continue; }

                    var finishReasonToken = firstCandidate["finishReason"];
                    if (finishReasonToken == null || finishReasonToken.ToString() != "STOP") { retry--; continue; }

                    var contentToken = firstCandidate["content"];
                    if (contentToken == null) { retry--; continue; }

                    var partsToken = contentToken["parts"];
                    if (!(partsToken is JArray partsArray) || !partsArray.HasValues) { retry--; continue; }

                    var firstPart = partsArray.FirstOrDefault();
                    if (firstPart == null) { retry--; continue; }

                    var textToken = firstPart["text"];
                    if (textToken == null) { retry--; continue; }

                    var text = textToken.ToString();
                    if (!string.IsNullOrWhiteSpace(text))
                        return new LlmResponse(text);

                    return new LlmResponse("Empty response", statusCode);
                }
                catch (OperationCanceledException)
                {
                    // 调用方取消或 QueryTimeout 触发：与现有 Gemini 及 OpenAI 路径一致的
                    // 可重试超时语义。取消令牌已通过 linkedCts 传递到 HTTP 操作。
                    Log.Debug("Gemini non-streaming request cancelled/timed out; retrying...");
                    retry--;
                    await Task.Delay(100);
                }
                catch (Exception ex)
                {
                    Log.Debug(ex.Message);
                    Log.Debug("Retrying...");
                    retry--;
                    await Task.Delay(100);
                }
            }
        }
        return new LlmResponse(responseString, statusCode);
    }

    /// <summary>
    /// 流式 Gemini role-based 主对话推理。使用 BuildRuntimeChatMessages 产出的角色消息序列。
    /// 保留 streamGenerateContent 端点、safetySettings / system_instruction / generationConfig /
    /// tools、SSE 解析与 function-call 解析。
    /// responseStart 保留为参数但不序列化（与现有 Gemini 行为一致）。
    /// 非取消异常后返回显式失败响应（不发起二次请求，避免 role 消息向字符串路径的有损重建）。
    /// </summary>
    internal async Task<LlmResponse> RunGeminiStreamingChatInference(
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
        if (AndroidHelper.IsAndroid && !NetworkHelper.IsNetworkAvailable())
            throw new InvalidOperationException("Network not available");

        bool isGemmaModel = !string.IsNullOrEmpty(modelName) && modelName.IndexOf("gemma", StringComparison.OrdinalIgnoreCase) >= 0;
        var genParams = ResolveParameters(cacheContext);

        object generationConfig = isGemmaModel
            ? (object)new { maxOutputTokens = genParams.MaxTokens, temperature = genParams.Temperature, topP = genParams.TopP }
            : new { maxOutputTokens = genParams.MaxTokens, temperature = genParams.Temperature, topP = genParams.TopP, thinkingConfig = new { thinkingBudget = 0 } };

        var jsonData = BuildGeminiRolePayload(systemPromptString, messages, generationConfig);

        var streamUrl = $"https://generativelanguage.googleapis.com/v1beta/models/" +
                        $"{modelName}:streamGenerateContent?alt=sse&key={apiKey}";

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, streamUrl);
            request.Content = new StringContent(jsonData, Encoding.UTF8, "application/json");

            using var response = await SharedHttpClient.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, ct);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync();
                throw new InvalidOperationException(
                    $"Gemini streaming failed: HTTP {(int)response.StatusCode} - {errorBody}");
            }

            using var stream = await response.Content.ReadAsStreamAsync();
            GeminiSseProcessResult sseResult = await ProcessGeminiSseAsync(stream, onToken, ct);

            if (!sseResult.Success)
            {
                // 分片解析失败（非取消）：已通过 ProcessGeminiSseAsync 记录错误，
                // 返回显式失败响应，保留已收集文本仅作诊断用途。
                return BuildGeminiStreamingFailureResponse(sseResult.ParseError, sseResult.Text);
            }

            return new LlmResponse(sseResult.Text, sseResult.Text.Length > 0);
        }
        catch (OperationCanceledException)
        {
            // 请求级取消（HttpClient.SendAsync / 上层 CancellationToken）：不视为解析失败。
            return new LlmResponse(string.Empty, false);
        }
        catch (Exception ex)
        {
            return BuildGeminiStreamingFailureResponse(ex, string.Empty);
        }
    }

    /// <summary>
    /// Gemini SSE 流处理结果。Success=false 且 ParseError != null 表示遇到非法 JSON 分片，
    /// 调用方应通过 BuildGeminiStreamingFailureResponse 返回显式失败（已收集文本仅作诊断）。
    /// </summary>
    internal sealed class GeminiSseProcessResult
    {
        public bool Success { get; set; }
        public string Text { get; set; }
        public Exception ParseError { get; set; }
    }

    /// <summary>
    /// 读取并解析 Gemini SSE 流（generateContent?alt=sse），提取文本分片。
    /// 遇到首个无法解析的非空 data 分片时：记录 Error 日志并以 Success=false 返回
    /// （不再静默忽略）。有效 JSON 但无文本内容的空内容分片不视为失败。
    /// 测试隔离点：接受原始 Stream，供离线注入 SSE 数据行（无需真实 HTTP）。
    /// </summary>
    internal async Task<GeminiSseProcessResult> ProcessGeminiSseAsync(Stream stream, Action<string> onToken, CancellationToken ct)
    {
        var result = new GeminiSseProcessResult { Success = true };
        var fullText = new StringBuilder();

        using var reader = new StreamReader(stream);
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
                var candidates = json["candidates"] as JArray;

                // 安全取首个 candidate：candidates 为空数组时 ?[0] 会抛异常，
                // 原代码依赖静默 catch{} 吞掉；现改为显式守卫，视为"有效但无内容"跳过。
                var firstCandidate = (candidates != null && candidates.Count > 0)
                    ? candidates[0]
                    : null;
                JArray parts = null;
                if (firstCandidate != null)
                {
                    var content = firstCandidate["content"] as JObject;
                    parts = content?["parts"] as JArray;
                }

                // 有效 JSON 但无内容分片（如空 candidates / 无 parts）→ 非失败，继续。
                if (parts == null) continue;

                foreach (var part in parts)
                {
                    var text = part["text"]?.ToString();
                    if (!string.IsNullOrEmpty(text))
                    {
                        fullText.Append(text);
                        onToken(text);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, $"[LlmGemini] Gemini role-based SSE chunk parse failed after {fullText.Length} chars of partial text; terminating stream.");
                result.Success = false;
                result.ParseError = ex;
                result.Text = fullText.ToString();
                return result;
            }
        }

        result.Text = fullText.ToString();
        return result;
    }

    /// <summary>
    /// 构建流式异常时的显式失败响应（测试隔离点）。
    /// 不发起二次请求；保留部分收集文本作为诊断内容，但标记为失败。
    /// </summary>
    internal LlmResponse BuildGeminiStreamingFailureResponse(Exception ex, string partialText)
    {
        Log.Error(ex, "[LlmGemini] Streaming failed; returning explicit failure without fallback request.");
        return new LlmResponse(partialText ?? "", 500, false);
    }

    internal override Dictionary<string, double>[] RunInferenceProbabilities(string fullPrompt, int n_predict = 1)
    {
        throw new NotImplementedException();
    }
}