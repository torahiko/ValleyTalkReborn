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

    public Dictionary<string, string> CacheContexts { get; private set; } = new Dictionary<string, string>();

    public override string ExtraInstructions => "";
    public override bool IsHighlySensoredModel => true;

    internal override async Task<LlmResponse> RunInference(
        string systemPromptString, string gameCacheString, string npcCacheString, 
        string promptString, string responseStart = "", int n_predict = 2048, 
        string cacheContext = "", bool allowRetry = true)
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

                if (!responseJson.TryGetValue("content", out var contentToken) || contentToken.Type == JTokenType.Null) 
                { 
                    retry--; continue; 
                }
                
                var contentArray = contentToken as JArray;
                if (contentArray == null || !contentArray.HasValues) 
                { 
                    retry--; continue; 
                }

                // ── Native Tool Calling & Text Extraction ──
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
                {
                    return new LlmResponse(textContent);
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
                await Task.Delay(100);
            }
        }
        return new LlmResponse(responseString, apiResponseCode);
    }

    internal override async Task<LlmResponse> RunStreamingInference(
        string systemPromptString, string gameCacheString, string npcCacheString,
        string promptString, Action<string> onToken, CancellationToken ct,
        string responseStart = "", int n_predict = 2048)
    {
        if (AndroidHelper.IsAndroid && !NetworkHelper.IsNetworkAvailable())
            throw new InvalidOperationException("Network not available");

        var inputString = JsonConvert.SerializeObject(new
        {
            thinking = new { type = "disabled" },
            model = modelName,
            max_tokens = n_predict,
            temperature = 0.9,
            top_p = 0.9,
            stream = true,
            system = new PromptElement[]
            {
                new() { type = "text", text = systemPromptString },
                new() { type = "text", cache_control = new { type = "ephemeral" }, text = gameCacheString }
            },
            messages = string.IsNullOrWhiteSpace(responseStart)
                ? new[] { new { role = "user", content = npcCacheString + promptString } }
                : new object[]
                {
                    new { role = "user", content = npcCacheString + promptString },
                    new { role = "assistant", content = responseStart }
                }
        });

        var fullText = new StringBuilder();

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
                    if (json["type"]?.ToString() != "content_block_delta") continue;
                    var delta = json["delta"]?["text"]?.ToString();
                    if (!string.IsNullOrEmpty(delta))
                    {
                        fullText.Append(delta);
                        onToken(delta);
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
            Log.Error(ex, "[LlmClaude] Streaming failed, falling back to non-streaming");
            return await base.RunStreamingInference(
                systemPromptString, gameCacheString, npcCacheString,
                promptString, onToken, ct, responseStart, n_predict);
        }
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