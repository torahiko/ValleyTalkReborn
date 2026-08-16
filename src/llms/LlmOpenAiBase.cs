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

internal abstract class LlmOpenAiBase : Llm
{
    protected string apiKey;
    protected string modelName;

    private static readonly HttpClient SharedHttpClient = new HttpClient
    {
        Timeout = TimeSpan.FromMinutes(1)
    };

    protected async Task<string[]> CoreGetModelNamesAsync()
    {
        if (string.IsNullOrEmpty(apiKey))
        {
            return Array.Empty<string>();
        }

        try
        {
            var modelsUrl = url.EndsWith("/") ? $"{url}v1/models" : $"{url}/v1/models";

            if (AndroidHelper.IsAndroid && NetworkHelper.IsNetworkAvailable())
            {
                var headers = new Dictionary<string, string>
                {
                    { "Authorization", $"Bearer {apiKey}" }
                };
                var responseString = await NetworkHelper.MakeRequestWithCustomHeadersAsync(modelsUrl, null, headers);
                return ParseModelNamesFromJson(responseString);
            }
            else
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, modelsUrl);
                request.Headers.Add("Authorization", $"Bearer {apiKey}");

                using var response = await SharedHttpClient.SendAsync(request);
                var responseString = await response.Content.ReadAsStringAsync();
                return ParseModelNamesFromJson(responseString);
            }
        }
        catch (Exception ex)
        {
            Log.Debug($"[LlmOpenAiBase] GetModelNames failed: {ex.Message}");
            return Array.Empty<string>();
        }
    }

    private string[] ParseModelNamesFromJson(string jsonString)
    {
        if (string.IsNullOrWhiteSpace(jsonString)) return Array.Empty<string>();
        var responseJson = JObject.Parse(jsonString);
        var modelsToken = responseJson["data"] as JArray;
        var modelNames = new List<string>();

        if (modelsToken != null)
        {
            foreach (var model in modelsToken)
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

    internal override async Task<LlmResponse> RunInference(
        string systemPromptString, string gameCacheString, string npcCacheString,
        string promptString, string responseStart = "", int n_predict = 2048,
        string cacheContext = "", bool allowRetry = true)
    {
        promptString = gameCacheString + npcCacheString + promptString;

        var messages = new List<object>();
        if (!string.IsNullOrWhiteSpace(systemPromptString))
        {
            messages.Add(new { role = "system", content = systemPromptString });
        }
        messages.Add(new { role = "user", content = promptString });

        if (!string.IsNullOrWhiteSpace(responseStart))
        {
            messages.Add(new { role = "assistant", content = responseStart });
        }

        var tools = ModEntry.Config.UseNativeToolCalling
            ? (object)AgentToolDefinitions.GetOpenAiToolsArray()
            : null;

        // ── 汇总主流厂商/中转站的“禁用/降低思考”参数 ──
        var requestBody = new
        {
            model = modelName,
            messages = messages,
            temperature = 0.9,
            top_p = 0.9,
            max_tokens = n_predict,
            tools = tools,

            // 1. Anthropic / Claude 样式规范
            thinking = new { type = "disabled" },

            // 2. DeepSeek R1 官方/第三方代理常见开关
            thinking_budget = 0,
            disable_thinking = true,

            // 3. OpenAI o1/o3/o3-mini 系列（降低推理消耗）
            reasoning_effort = "low",

            // 4. SiliconFlow / OpenRouter 等平台过滤思考过程字段
            include_reasoning = false,
            reasoning = false
        };

        var jsonData = JsonConvert.SerializeObject(requestBody);
        var endpointUrl = url.EndsWith("/") ? $"{url}v1/chat/completions" : $"{url}/v1/chat/completions";

        int retry = allowRetry ? 3 : 1;
        string responseString = "";
        int statusCode = 500;

        while (retry > 0)
        {
            try
            {
                if (AndroidHelper.IsAndroid && NetworkHelper.IsNetworkAvailable())
                {
                    var headers = new Dictionary<string, string>
                    {
                        { "Authorization", $"Bearer {apiKey}" }
                    };
                    responseString = await NetworkHelper.MakeRequestWithCustomHeadersAsync(endpointUrl, jsonData, headers);
                    statusCode = 200;
                }
                else
                {
                    using var request = new HttpRequestMessage(HttpMethod.Post, endpointUrl);
                    request.Headers.Add("Authorization", $"Bearer {apiKey}");
                    request.Content = new StringContent(jsonData, Encoding.UTF8, "application/json");

                    using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(ModEntry.Config.QueryTimeout));
                    using var response = await SharedHttpClient.SendAsync(request, cts.Token);

                    statusCode = (int)response.StatusCode;
                    responseString = await response.Content.ReadAsStringAsync();
                }

                var responseJson = JObject.Parse(responseString);
                var choices = responseJson["choices"] as JArray;
                if (choices == null || choices.Count == 0)
                {
                    retry--;
                    continue;
                }

                var firstChoice = choices[0];
                var messageToken = firstChoice["message"];
                if (messageToken == null)
                {
                    retry--;
                    continue;
                }

                // ── Native Tool Calling ──
                var toolResponse = new LlmResponse("", true);
                var toolCallsArray = messageToken["tool_calls"] as JArray;
                if (toolCallsArray != null && toolCallsArray.Count > 0)
                {
                    foreach (var tc in toolCallsArray)
                    {
                        var functionToken = tc["function"];
                        if (functionToken != null)
                        {
                            var funcName = functionToken["name"]?.ToString();
                            var funcArgs = functionToken["arguments"]?.ToString() ?? "{}";
                            if (!string.IsNullOrEmpty(funcName))
                            {
                                toolResponse.ToolCalls.Add(new ToolCallData { FunctionName = funcName, JsonArguments = funcArgs });
                            }
                        }
                    }
                }

                var contentStr = messageToken["content"]?.ToString();
                if (toolResponse.ToolCalls.Count > 0)
                {
                    toolResponse.Text = contentStr ?? "";
                    return toolResponse;
                }

                if (!string.IsNullOrWhiteSpace(contentStr))
                {
                    return new LlmResponse(contentStr);
                }

                retry--;
            }
            catch (Exception ex)
            {
                Log.Debug($"[LlmOpenAiBase] Request error: {ex.Message}");
                retry--;
                await Task.Delay(100);
            }
        }

        return new LlmResponse(responseString, statusCode);
    }

    internal override Dictionary<string, double>[] RunInferenceProbabilities(string fullPrompt, int n_predict = 1)
    {
        throw new NotImplementedException();
    }
}