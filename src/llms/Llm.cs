using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq; 
using System.Threading.Tasks;
using ValleytalkReborn;

namespace ValleytalkReborn;

internal abstract class Llm
{
    internal static Llm Instance {get; private set;}
    = new LlmDummy();
    
    internal static void SetLlm(Type llmType, string url ="", string promptFormat="", string apiKey="", string modelName = null)
    {
        var paramsDict = new Dictionary<string, string>
        {
            {"url", url},
            {"promptFormat", promptFormat},
            {"apiKey", apiKey},
            {"modelName", modelName}
        };
        Llm instance = CreateInstance(llmType, paramsDict);
        Instance = instance;
        
        // 【优化】防止 UI 卡死！先假设连接不可用，然后抛入后台线程去异步验证，验证成功后再悄悄启用
        DialogueBuilder.Instance.LlmDisabled = true; 
        Task.Run(async () => 
        {
            bool isDisabled = await CheckConnection(apiKey, modelName);
            DialogueBuilder.Instance.LlmDisabled = isDisabled;
        });
    }

    private static async Task<bool> CheckConnection(string apiKey, string modelName)
    {
        if (ModEntry.Config.SuppressConnectionCheck)
            return false;

        // 【新增防线】如果 API Key 或 Model Name 为空，直接判定连接不可用，拦截无效的网络测试请求
        if (string.IsNullOrWhiteSpace(apiKey) || string.IsNullOrWhiteSpace(modelName))
        {
            ModEntry.SMonitor.Log($"[ValleytalkReborn] API Key 或模型名称未填写，暂停模型连接测试。", StardewModdingAPI.LogLevel.Warn);
            return true; // Connection failed/disabled
        }

        var response = await Instance.RunInference("You are performing LLM connection testing", "Please just ", "respond with ", "'Connection successful'", allowRetry: false);
        if (!response.IsSuccess || response.Text.Length < 5)
        {
            ModEntry.SMonitor.Log($"Failed to connect to the model {modelName} using provider {Instance.GetType().Name}. ", StardewModdingAPI.LogLevel.Error);
            if (!string.IsNullOrWhiteSpace(response.ErrorMessage))
            {
                ModEntry.SMonitor.Log($"Error message: {response.ErrorMessage}", StardewModdingAPI.LogLevel.Error);
            }

            // 避免在连接测试失败后再重复卡顿调用 GetModelNames
            return true; // Connection failed
        }
        else
        {
            ModEntry.SMonitor.Log(Util.GetString("modelCheckSuccess", returnNull: true) ?? "Connected to the model successfully.", StardewModdingAPI.LogLevel.Info);
            return false; // Connection successful
        }
    }

    public static Llm CreateInstance(Type llmType, Dictionary<string, string> paramsDict)
    {
        var constructor = llmType.GetConstructors().OrderByDescending(x => x.GetParameters().Length).First();
        var parameters = constructor.GetParameters().Select(x =>
        {
            if (paramsDict.TryGetValue(x.Name, out var value))
            {
                return Convert.ChangeType(value, x.ParameterType);
            }
            return x.HasDefaultValue ? x.DefaultValue : null;
        }).ToArray();
        return (Llm)Activator.CreateInstance(llmType, parameters);
    }

    protected string url;
    private long _totalPrompts;
    private double _totalPromptTime;
    private long _totalInference;
    private double _totalInferenceTime;

    public abstract bool IsHighlySensoredModel { get; }
    public abstract string ExtraInstructions { get; }
    public string TokenStats => $"Prompt: {_totalPrompts} tokens in {_totalPromptTime}ms, Inference: {_totalInference} tokens in {_totalInferenceTime}ms";
    
    internal abstract Task<LlmResponse> RunInference(string systemPromptString, string gameCacheString, string npcCacheString, string promptString, string responseStart = "",int n_predict = 150,string cacheContext="",bool allowRetry = true);
    internal abstract Dictionary<string,double>[] RunInferenceProbabilities(string fullPrompt,int n_predict = 1);

    protected void AddToStats(JObject token_stats) 
    {
        if (token_stats == null) return; 

        _totalPrompts += token_stats.Value<long?>("prompt_n") ?? 0; 
        _totalPromptTime += token_stats.Value<double?>("prompt_ms") ?? 0.0; 
        _totalInference += token_stats.Value<long?>("predicted_n") ?? 0; 
        _totalInferenceTime += token_stats.Value<double?>("predicted_ms") ?? 0.0; 
    }

    internal double[] GetProbabilities(string prompt, string[][] options)
    {
        var map = BuildMap(options);
        return FindTokensRecursive(prompt, map, string.Empty);
    }

    private static Dictionary<string, int> BuildMap(string[][] options)
    {
        var map = new Dictionary<string, int>();
        for (int i = 0; i < options.Length; i++)
        {
            foreach (var option in options[i])
            {
                map[option] = i;
            }
        }
        return map;
    }

    private double[] FindTokensRecursive(string prompt, Dictionary<string, int> map, string prefix)
    {
        var maxOut = map.Max(x => x.Value);
        var fullPrompt = prompt + prefix;
        var tokens = RunInferenceProbabilities(fullPrompt, 1)[0];
        var result = new double[maxOut + 1];
        
        foreach (var token in tokens)
        {
            if (token.Value == 0) continue;

            if (map.TryGetValue(prefix + token.Key, out var value))
            {
                result[value] += token.Value;
            }
            else if (map.Any(x => x.Key.StartsWith(prefix + token.Key)))
            {
                var recurse = FindTokensRecursive(prompt, map, prefix + token.Key);
                for (int i = 0; i < recurse.Length; i++)
                {
                    result[i] += recurse[i] * token.Value;
                }
            }
        }
        return result;
    }
}