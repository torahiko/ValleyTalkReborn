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

internal class LlmGemini : Llm, IGetModelNames
{
    private readonly string apiKey;
    private readonly string modelName;

    private static readonly HttpClient SharedHttpClient = new HttpClient();

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
        var toolsPayload = ModEntry.Config.UseNativeToolCalling
            ? new[] { new { functionDeclarations = AgentToolDefinitions.GetGeminiToolsArray() } }
            : null;

        bool isGemmaModel = !string.IsNullOrEmpty(modelName) && modelName.IndexOf("gemma", StringComparison.OrdinalIgnoreCase) >= 0;

        object generationConfig = isGemmaModel
            ? (object)new { maxOutputTokens = n_predict, temperature = 0.9, topP = 0.9 }
            : new { maxOutputTokens = n_predict, temperature = 0.9, topP = 0.9, thinkingConfig = new { thinkingBudget } };

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
            generationConfig,
            tools = toolsPayload
        });

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

                var toolResponse = new LlmResponse("", true);
                string textPart = null;

                foreach (var part in partsArray)
                {
                    var funcCallToken = part["functionCall"];
                    if (funcCallToken != null && funcCallToken.Type != JTokenType.Null)
                    {
                        var funcName = funcCallToken["name"]?.ToString();
                        var argsToken = funcCallToken["args"];
                        var funcArgs = argsToken != null ? argsToken.ToString(Formatting.None) : "{}";
                        if (!string.IsNullOrEmpty(funcName))
                            toolResponse.ToolCalls.Add(new ToolCallData { FunctionName = funcName, JsonArguments = funcArgs });
                    }
                    else if (part["text"] != null && textPart == null)
                    {
                        textPart = part["text"].ToString();
                    }
                }

                if (toolResponse.ToolCalls.Count > 0)
                {
                    toolResponse.Text = textPart ?? "";
                    Log.Debug($"[LlmGemini] Tool calls received: {toolResponse.ToolCalls.Count}");
                    return toolResponse;
                }

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
        string responseStart = "", int n_predict = 2048)
    {
        if (AndroidHelper.IsAndroid && !NetworkHelper.IsNetworkAvailable())
            throw new InvalidOperationException("Network not available");

        bool isGemmaModel = !string.IsNullOrEmpty(modelName) && modelName.IndexOf("gemma", StringComparison.OrdinalIgnoreCase) >= 0;

        object generationConfig = isGemmaModel
            ? (object)new { maxOutputTokens = n_predict, temperature = 0.9, topP = 0.9 }
            : new { maxOutputTokens = n_predict, temperature = 0.9, topP = 0.9, thinkingConfig = new { thinkingBudget = 0 } };

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
        });

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
                    var text = parts?[0]?["text"]?.ToString();
                    if (!string.IsNullOrEmpty(text))
                    {
                        fullText.Append(text);
                        onToken(text);
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

    internal override Dictionary<string, double>[] RunInferenceProbabilities(string fullPrompt, int n_predict = 1)
    {
        throw new NotImplementedException();
    }
}