using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Threading;
using ValleytalkReborn;
using System.Threading.Tasks;
using ValleytalkReborn.Platform;

namespace ValleytalkReborn;

internal class LlmClaude : Llm, IGetModelNames
{
    private readonly string apiKey;
    private readonly string modelName;

    private static readonly HttpClient SharedHttpClient = new HttpClient 
    { 
        Timeout = TimeSpan.FromMinutes(1) 
    };

    class PromptElement
    {
#pragma warning disable IDE1006 // Naming Styles
        public string type { get; set; }
        public string text { get; set; }
        public object cache_control { get; set; }
#pragma warning restore IDE1006 // Naming Styles
    }

    public LlmClaude(string apiKey, string modelName = null)
    {
        url = "https://api.anthropic.com/v1/messages";
        
        this.apiKey = apiKey;
        this.modelName = modelName ?? "claude-3-5-haiku-latest";
    }

    public Dictionary<string,string> CacheContexts { get; private set; } = new Dictionary<string, string>();

    public override string ExtraInstructions => "";

    public override bool IsHighlySensoredModel => true;

    internal override async Task<LlmResponse> RunInference(string systemPromptString, string gameCacheString, string npcCacheString, string promptString, string responseStart = "",int n_predict = 2048,string cacheContext="",bool allowRetry = true)
    {
        var promptCached = gameCacheString;
        var tools = ModEntry.Config.UseNativeToolCalling
            ? (object)AgentToolDefinitions.GetAnthropicToolsArray()
            : null;

        var inputString = JsonConvert.SerializeObject(new
        {
            thinking = new { type = "disabled" },
            model = this.modelName,
            max_tokens = n_predict,
            temperature = 0.9,
            top_p = 0.9,
            system = new PromptElement[]
            {
                new() { type = "text", text = systemPromptString },
                new() { type = "text", cache_control = new { type = "ephemeral" }, text = promptCached }
            },
            messages = string.IsNullOrWhiteSpace(responseStart)
                ? new[] { new { role = "user", content = npcCacheString + promptString } }
                : new object[]
                {
                    new { role = "user", content = npcCacheString + promptString },
                    new { role = "assistant", content = responseStart }
                },
            tools
        });

        int retry = allowRetry ? 3 : 1;
        var fullUrl = url;
        
        if (AndroidHelper.IsAndroid && !NetworkHelper.IsNetworkAvailable())
        {
            throw new InvalidOperationException("Network not available");
        }
        
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
                
                responseString = await NetworkHelper.MakeRequestWithCustomHeadersAsync(fullUrl, inputString, headers);
                var responseJson = JObject.Parse(responseString);

                if (responseJson == null)
                {
                    throw new Exception("Failed to parse response");
                }

                if (!responseJson.TryGetValue("content", out var contentToken) || contentToken.Type == JTokenType.Null) { retry--; continue; }
                var contentArray = contentToken as JArray;
                if (contentArray == null || !contentArray.HasValues) { retry--; continue; }

                // ── Native Tool Calling：检查 tool_use 类型的 content 块 ──
                var toolResponse = new LlmResponse("", true);
                string textContent = null;

                foreach (var element in contentArray)
                {
                    var elementType = element["type"]?.ToString();
                    if (elementType == "tool_use")
                    {
                        var funcName = element["name"]?.ToString();
                        var inputToken = element["input"];
                        var funcArgs = inputToken != null ? inputToken.ToString(Newtonsoft.Json.Formatting.None) : "{}";
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

                var firstContentElement = contentArray.FirstOrDefault();
                if (firstContentElement == null || firstContentElement["text"] == null) { retry--; continue; }

                var text = firstContentElement["text"].ToString();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    return new LlmResponse(text);
                }
                
                retry--;
            }
            catch (Exception ex)
            {
                if (ex.InnerException is HttpRequestException httpEx)
                {
                    apiResponseCode = (int)(httpEx.StatusCode ?? 0);
                }
                Log.Debug(ex.Message);
                Log.Debug("Retrying...");
                retry--;
                // 【优化】改为异步延迟
                await Task.Delay(100);
            }
        }
        return new LlmResponse(responseString, apiResponseCode);
    }

    internal override Dictionary<string, double>[] RunInferenceProbabilities(string fullPrompt, int n_predict = 1)
    {
        throw new NotImplementedException();
    }

    public string[] GetModelNames()
    {
        if (string.IsNullOrEmpty(apiKey))
        {
            return Array.Empty<string>();
        }
        
        try 
        {
            // 【优化】 Task.Run 包装防卡死，复用 SharedHttpClient
            return Task.Run(async () =>
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
            }).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Log.Error(ex.Message);
            return Array.Empty<string>();
        }
    }
}