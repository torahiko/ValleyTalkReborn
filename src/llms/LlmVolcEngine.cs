using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System.Threading;
using System.Threading.Tasks;
using ValleytalkReborn;
using ValleytalkReborn.Platform;

namespace ValleytalkReborn;

internal class LlmVolcEngine : Llm, IGetModelNames
{
    protected string apiKey;
    protected string modelName;

    // 【优化】复用 HttpClient，避免网络 Socket 耗尽
    private static readonly HttpClient SharedHttpClient = new HttpClient
    {
        Timeout = TimeSpan.FromMinutes(1)
    };

    record PromptElement
    {
        public string role { get; set; }
        public string content { get; set; }
    }

    public LlmVolcEngine(string apiKey, string modelName = null)
    {
        url = "https://ark.cn-beijing.volces.com/api/v3";

        this.apiKey = apiKey;
        this.modelName = modelName ?? "doubao-1.5-pro";
    }

    public override string ExtraInstructions => "";

    public override bool IsHighlySensoredModel => false;

    public string[] GetModelNames()
    {
        if (string.IsNullOrEmpty(apiKey))
        {
            return Array.Empty<string>();
        }
        return CoreGetModelNames();
    }

    internal override async Task<LlmResponse> RunInference(string systemPromptString, string gameCacheString, string npcCacheString, string promptString, string responseStart = "",int n_predict = 2048,string cacheContext="",bool allowRetry = true)
    {
        var tools = ModEntry.Config.UseNativeToolCalling
            ? (object)AgentToolDefinitions.GetOpenAiToolsArray()
            : null;

        var inputString = JsonConvert.SerializeObject(new
        {
            thinking = new { type = "disabled" },
            temperature = 0.9,
            top_p = 0.9,
            model = modelName,
            max_tokens = n_predict,
            messages = new PromptElement[]
            {
                new() { role = "system", content = systemPromptString },
                new() { role = "user", content = gameCacheString + npcCacheString + promptString }
            },
            tools
        });

        int retry = allowRetry ? 3 : 1;
        var fullUrl = $"{url}/chat/completions";
        
        if (AndroidHelper.IsAndroid && !NetworkHelper.IsNetworkAvailable())
        {
            throw new InvalidOperationException("Network not available");
        }

        int apiResponseCode = 500;
        string responseString = "";

        while (retry > 0)
        {
            try
            {
                responseString = await NetworkHelper.MakeRequestAsync(fullUrl, inputString, CancellationToken.None, apiKey);
                var responseJson = JObject.Parse(responseString);

                if (responseJson == null)
                {
                    throw new Exception("Failed to parse response");
                }
                
                if (!responseJson.TryGetValue("choices", out var choicesToken) || !(choicesToken is JArray choicesArray) || !choicesArray.HasValues) { retry--; continue; }

                var firstChoice = choicesArray.FirstOrDefault();
                if (firstChoice == null) { retry--; continue; }

                var messageToken = firstChoice["message"];
                if (messageToken == null) { retry--; continue; }

                // ── Native Tool Calling: extract tool_calls without early return ──
                var toolCallsToken = messageToken["tool_calls"] as JArray;
                var response = new LlmResponse("", true);
                if (toolCallsToken != null && toolCallsToken.HasValues)
                {
                    foreach (var tc in toolCallsToken)
                    {
                        var funcName = tc["function"]?["name"]?.ToString();
                        var funcArgs = tc["function"]?["arguments"]?.ToString() ?? "{}";
                        if (!string.IsNullOrEmpty(funcName))
                            response.ToolCalls.Add(new ToolCallData { FunctionName = funcName, JsonArguments = funcArgs });
                    }
                    if (response.ToolCalls.Count > 0)
                        Log.Debug($"[LlmVolcEngine] Tool calls received: {response.ToolCalls.Count}");
                }

                var contentToken = messageToken["content"];
                var text = contentToken != null ? contentToken.ToString() : string.Empty;
                response.Text = text;

                // Only retry when both content and tool_calls are completely absent
                if (string.IsNullOrWhiteSpace(text) && response.ToolCalls.Count == 0)
                {
                    retry--;
                    continue;
                }

                return response;
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
                // 【优化】改为非阻塞式异步等待
                await Task.Delay(100);
            }
        }
        return new LlmResponse(responseString, apiResponseCode);
    }

    internal override Dictionary<string, double>[] RunInferenceProbabilities(string fullPrompt, int n_predict = 1)
    {
        throw new NotImplementedException();
    }

    public string[] CoreGetModelNames(Dictionary<string, string> extraHeaders = null)
    {
        extraHeaders ??= new Dictionary<string, string>();
        
        try 
        {
            var fullUrl = $"{url}/models";
            
            // 【优化】使用 Task.Run 包装异步逻辑，防止 .Result 导致的死锁
            return Task.Run(async () =>
            {
                string responseString;
                if (AndroidHelper.IsAndroid && NetworkHelper.IsNetworkAvailable())
                {
                    responseString = await NetworkHelper.MakeRequestAsync(fullUrl, null, CancellationToken.None, apiKey);
                }
                else
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, fullUrl);
                    request.Headers.Add("Authorization", $"Bearer {apiKey}");
                    foreach (var header in extraHeaders)
                    {
                        request.Headers.Add(header.Key, header.Value);
                    }
                    using var response = await SharedHttpClient.SendAsync(request);
                    responseString = await response.Content.ReadAsStringAsync();
                }
                
                var responseJson = JObject.Parse(responseString);
                var dataToken = responseJson["data"];
                if (!(dataToken is JArray modelsArray))
                {
                    return Array.Empty<string>();
                }

                var modelNames = new List<string>();
                foreach (var model in modelsArray)
                {
                    var idToken = model["id"];
                    if (idToken != null)
                    {
                        modelNames.Add(idToken.ToString());
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