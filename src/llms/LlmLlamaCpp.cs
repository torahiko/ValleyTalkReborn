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

internal class LlmLlamaCpp : Llm
{
    private static readonly HttpClient SharedHttpClient = new HttpClient
    {
        Timeout = TimeSpan.FromMinutes(1)
    };

    public LlmLlamaCpp(string url, string promptFormat)
    {
        this.url = url;
        PromptFormat = promptFormat;
    }

    public string PromptFormat { get; }
    public override string ExtraInstructions => "Include only the new line and any responses in the output, no descriptions or explanations.";

    public override bool IsHighlySensoredModel => false;

    internal string BuildPrompt(string systemPromptString, string promptString, string responseStart = "")
    {
        return PromptFormat
            .Replace("{system}", systemPromptString)
            .Replace("{prompt}", promptString)
            .Replace("{response_start}", responseStart);
    }

    internal override async Task<LlmResponse> RunInference(string systemPromptString, string gameCacheString, string npcCacheString, string promptString, string responseStart = "",int n_predict = 2048,string cacheContext="",bool allowRetry = true)
    {
        promptString = gameCacheString + npcCacheString + promptString;
        var fullPrompt = BuildPrompt(systemPromptString, promptString, responseStart);

        bool retry = true;
        
        if (AndroidHelper.IsAndroid && !NetworkHelper.IsNetworkAvailable())
        {
            throw new InvalidOperationException("Network not available");
        }
       
        string responseString = "";
        while (retry)
        {
            try
            {
                retry = false;
                var requestBody = new
                {
                    prompt = fullPrompt,
                    n_predict = n_predict,
                    stream = false,
                    temperature = n_predict == 1 ? 0 : 0.9,
                    top_p = 0.9,
                    min_p = 0.05,
                    repeat_penalty = 1.05,
                };

                if (AndroidHelper.IsAndroid)
                {
                    var jsonData = JsonConvert.SerializeObject(requestBody);
                    responseString = await NetworkHelper.MakeRequestAsync(url, jsonData);
                }
                else
                {
                    using var jsonContent = new StringContent(JsonConvert.SerializeObject(requestBody), Encoding.UTF8, "application/json");
                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(ModEntry.Config.QueryTimeout));
                    
                    var response = await SharedHttpClient.PostAsync(url, jsonContent, cts.Token);
                    responseString = await response.Content.ReadAsStringAsync();
                }

                var responseJson = JObject.Parse(responseString);

                var token_stats = responseJson["timings"] as JObject;
                AddToStats(token_stats);

                if (responseJson == null)
                {
                    throw new Exception("Failed to parse response");
                }

                var contentToken = responseJson["content"];
                if (!string.IsNullOrWhiteSpace(contentToken?.ToString()))
                {
                    return new LlmResponse(contentToken.ToString());
                }
                
                throw new Exception("No content in response");
            }
            catch (Exception ex)
            {
                Log.Debug(ex.Message);
                Log.Debug("Retrying...");
                retry = allowRetry;
                // 【优化】改为非阻塞异步等待 1 秒，游戏不会硬性冻结卡死
                await Task.Delay(1000);
            }
        }
        return new LlmResponse(responseString, 500);
    }
    
    internal override Dictionary<string,double>[] RunInferenceProbabilities(string fullPrompt, int n_predict = 1)
    {
        try
        {
            // 【优化】使用 Task.Run 隔离异步调用，规避 .Result 造成的死锁风险
            return Task.Run(async () =>
            {
                var jsonContent = new StringContent(
                    JsonConvert.SerializeObject(new 
                    {
                        prompt = fullPrompt,
                        n_predict = n_predict,
                        stream = false,
                        temperature = 0.8,
                        top_p = 0.88,
                        min_p = 0.05,
                        cache_prompt = true,
                        n_probs = 10
                    }),
                    Encoding.UTF8,
                    "application/json"
                );

                bool retry = true;
                while (retry)
                {
                    try
                    {
                        retry = false;
                        using var response = await SharedHttpClient.PostAsync(url, jsonContent);
                        var responseString = await response.Content.ReadAsStringAsync();
                        var responseJson = JObject.Parse(responseString);
                        
                        var token_stats = responseJson["timings"] as JObject;
                        AddToStats(token_stats);

                        if (responseJson == null)
                        {
                            throw new Exception("Failed to parse response");
                        }

                        var result = new List<Dictionary<string, double>>();
                        var probsToken = responseJson["completion_probabilities"]; 
                        if (probsToken is JArray probsArray) 
                        {
                            foreach (var prob in probsArray)
                            {
                                var probDict = new Dictionary<string,double>();
                                var innerProbsToken = prob["probs"];
                                if (innerProbsToken is JArray innerProbsArray) 
                                {
                                    foreach (var prop in innerProbsArray)
                                    {
                                        var token = prop["tok_str"]?.ToString(); 
                                        var probability = prop["prob"]?.Value<double>(); 
                                        if (token != null && probability.HasValue)
                                        {
                                            probDict[token] = probability.Value;
                                        }
                                    }
                                }
                                result.Add(probDict);
                            }
                        }
                        return result.ToArray();
                    }
                    catch(Exception ex)
                    {
                        Log.Debug(ex.Message);
                        Log.Debug("Retrying...");
                        retry = true;
                        await Task.Delay(1000);
                    }
                }
                return Array.Empty<Dictionary<string, double>>();
            }).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Log.Error(ex.Message);
            return Array.Empty<Dictionary<string, double>>();
        }
    }
}