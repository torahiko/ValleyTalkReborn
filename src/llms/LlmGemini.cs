using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
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

internal class LlmGemini : Llm, IGetModelNames
{
    private string apiKey;
    private string modelName;

    // 复用 HttpClient，避免循环中重复创建引发套接字耗尽
    private static readonly HttpClient SharedHttpClient = new HttpClient();

    public LlmGemini(string apiKey, string modelName = null)
    {
        this.apiKey = apiKey;
        this.modelName = modelName ?? "gemini-2.5-flash";

        url = $"https://generativelanguage.googleapis.com/v1beta/models/{this.modelName}:generateContent?key=";
    }

    public Dictionary<string,string> CacheContexts { get; private set; } = new Dictionary<string, string>();

    public override string ExtraInstructions => "";

    public override bool IsHighlySensoredModel => false;

    public string[] GetModelNames()
    {
        try
        {
            var modelsUrl = $"https://generativelanguage.googleapis.com/v1beta/models?key=" + apiKey;
            
            return Task.Run(async () =>
            {
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
            }).GetAwaiter().GetResult();
        }
        catch(Exception ex)
        {
            Log.Debug(ex.Message);
            return Array.Empty<string>();
        }
    }

    internal override async Task<LlmResponse> RunInference(string systemPromptString, string gameCacheString, string npcCacheString, string promptString, string responseStart = "", int n_predict = 2048, string cacheContext = "", bool allowRetry = true)
    {
        promptString = gameCacheString + npcCacheString + promptString;

        int thinkingBudget = 0;
        var toolsPayload = ModEntry.Config.UseNativeToolCalling
            ? new[] { new { functionDeclarations = AgentToolDefinitions.GetGeminiToolsArray() } }
            : null;

        // 判断当前模型是否为 Gemma 模型，适配思维配置
        bool isGemmaModel = !string.IsNullOrEmpty(modelName) && modelName.IndexOf("gemma", StringComparison.OrdinalIgnoreCase) >= 0;

        object generationConfig;
        if (isGemmaModel)
        {
            generationConfig = new
            {
                maxOutputTokens = n_predict,
                temperature = 0.9,
                topP = 0.9
            };
        }
        else
        {
            generationConfig = new
            {
                maxOutputTokens = n_predict,
                temperature = 0.9,
                topP = 0.9,
                thinkingConfig = new { thinkingBudget }
            };
        }

        var jsonData = JsonConvert.SerializeObject(new
        {
            safetySettings = new[]
            {
                new {category = "HARM_CATEGORY_SEXUALLY_EXPLICIT", threshold = "BLOCK_NONE"},
                new {category = "HARM_CATEGORY_HARASSMENT", threshold = "BLOCK_MEDIUM_AND_ABOVE"}
            },
            system_instruction = new { parts = new { text = systemPromptString } },
            contents = new { parts = new { text = promptString } },
            generationConfig = generationConfig,
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
                        var funcArgs = argsToken != null ? argsToken.ToString(Newtonsoft.Json.Formatting.None) : "{}";
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
            catch(Exception ex)
            {
                Log.Debug(ex.Message);
                Log.Debug("Retrying...");
                retry--;
                await Task.Delay(100);
            }
        }
        return new LlmResponse(responseString, statusCode);
    }

    internal override Dictionary<string, double>[] RunInferenceProbabilities(string fullPrompt, int n_predict = 1)
    {
        throw new System.NotImplementedException();
    }
}