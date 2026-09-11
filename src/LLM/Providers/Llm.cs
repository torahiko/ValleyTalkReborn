using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

namespace ValleytalkReborn;

/// <summary>
/// LLM 调用上下文类型常量定义，用于区分不同业务场景的参数策略。
/// </summary>
internal static class LlmContextTypes
{
    /// <summary>主对话（玩家与 NPC 交互）：使用 GMCM 配置的自定义参数。</summary>
    public const string Main = "";
    
    /// <summary>Bark 随地心声（NPC 自发环境气泡）：固定短小稳定参数，禁用自定义 Body。</summary>
    public const string Bark = "Bark";
    
    /// <summary>A2A 多人对话（NPC 间自发交互）：固定多轮 JSON 空间，禁用自定义 Body。</summary>
    public const string A2A = "A2A";
    
    /// <summary>禁用工具调用的上下文标记（现有代码已使用）。</summary>
    public const string NoTools = "NO_TOOLS";
}

internal abstract class Llm
{
    internal static Llm Instance { get; private set; } = new LlmDummy();
    
    internal static void SetLlm(Type llmType, string url = "", string promptFormat = "", string apiKey = "", string modelName = null)
    {
        var paramsDict = new Dictionary<string, string>
        {
            { "url", url },
            { "promptFormat", promptFormat },
            { "apiKey", apiKey },
            { "modelName", modelName }
        };
        
        Llm instance = CreateInstance(llmType, paramsDict);
        Instance = instance;
        
        // 【优化】先假设连接不可用，抛入后台线程去异步验证，验证成功后再悄悄启用，防止游戏 UI 卡死
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
            ModEntry.SMonitor.Log($"Failed to connect to the model {modelName} using provider {Instance.GetType().Name}.", StardewModdingAPI.LogLevel.Error);
            if (!string.IsNullOrWhiteSpace(response.ErrorMessage))
            {
                ModEntry.SMonitor.Log($"Error message: {response.ErrorMessage}", StardewModdingAPI.LogLevel.Error);
            }

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

    /// <summary>
    /// 生成参数容器：封装 Temperature、TopP、MaxTokens 与 CustomBody 允许标志。
    /// </summary>
    internal readonly struct GenerationParameters
    {
        public float Temperature { get; }
        public float TopP { get; }
        public int MaxTokens { get; }
        public bool AllowCustomBody { get; }

        public GenerationParameters(float temperature, float topP, int maxTokens, bool allowCustomBody)
        {
            Temperature = temperature;
            TopP = topP;
            MaxTokens = maxTokens;
            AllowCustomBody = allowCustomBody;
        }
    }

    /// <summary>
    /// 生成参数预设工厂：为不同业务场景提供语义化的参数配置。
    /// </summary>
    internal static class GenerationParametersPresets
    {
        /// <summary>
        /// Bark 随地心声：短小、稳定、严禁自定义参数污染。
        /// - Temperature: 0.8（略低于默认，减少随机性）
        /// - TopP: 0.9（标准值）
        /// - MaxTokens: 400（约 200 中文字符，足够一句完整心声）
        /// - AllowCustomBody: false（禁止玩家的极客配置影响环境气泡）
        /// </summary>
        public static GenerationParameters ForBark() => new GenerationParameters(
            temperature: 0.8f,
            topP: 0.9f,
            maxTokens: 400,
            allowCustomBody: false
        );

        /// <summary>
        /// A2A 多人剧本：固定多轮 JSON 空间、适度灵活、绝不截断。
        /// - Temperature: 0.85（略高于 Bark，增加对话多样性）
        /// - TopP: 0.9（标准值）
        /// - MaxTokens: 1024（足够容纳多轮 NPC 对话的 JSON 数组）
        /// - AllowCustomBody: false（禁止玩家配置干扰 NPC 间互动）
        /// </summary>
        public static GenerationParameters ForA2A() => new GenerationParameters(
            temperature: 0.85f,
            topP: 0.9f,
            maxTokens: 1024,
            allowCustomBody: false
        );

        /// <summary>
        /// 玩家主对话：尊崇 ModConfig 单一数据源，允许极客 Custom Body 注入。
        /// - 所有参数从 ModEntry.Config 读取（玩家在 GMCM 高级页面设置）
        /// - AllowCustomBody: true（允许 Custom Body JSON 深合并）
        /// - 自动 Clamp 到合法范围，防御手动编辑 config.json 引入的非法值
        /// </summary>
        public static GenerationParameters ForMainDialogue()
        {
            var config = ModEntry.Config;

            // 若 Config 为 null（极端加载时序问题），安全回退到硬编码默认值
            float temperature = config?.Temperature ?? 0.9f;
            float topP = config?.TopP ?? 0.9f;
            int maxTokens = config?.MaxTokens ?? 1024;

            // Clamp 防御：阻止手动编辑 config.json 引入的非法值
            temperature = Math.Clamp(temperature, 0.0f, 2.0f);
            topP = Math.Clamp(topP, 0.0f, 1.0f);
            maxTokens = Math.Clamp(maxTokens, 100, 8192);

            return new GenerationParameters(
                temperature: temperature,
                topP: topP,
                maxTokens: maxTokens,
                allowCustomBody: true
            );
        }
    }

    public abstract bool IsHighlySensoredModel { get; }
    public abstract string ExtraInstructions { get; }

    /// <summary>
    /// True if this provider's RunStreamingInference implementation attaches the native
    /// tool schema and correctly parses streamed tool-call deltas end-to-end.
    /// Providers that only stream plain text (no tool_calls support in the streaming path)
    /// must leave this false, or callers may silently lose expected tool calls when
    /// streaming is requested on a turn that needs one.
    /// Default: false. Override to true only once verified.
    /// </summary>
    public virtual bool SupportsStreamingWithTools => false;
    public string TokenStats => $"Prompt: {_totalPrompts} tokens in {_totalPromptTime}ms, Inference: {_totalInference} tokens in {_totalInferenceTime}ms";
    
    /// <summary>
    /// 根据上下文类型解析生成参数。单一数据源入口。
    /// </summary>
    /// <param name="cacheContext">上下文标识（对应 RunInference 的 cacheContext 参数）。</param>
    /// <returns>该上下文对应的 GenerationParameters。</returns>
    internal static GenerationParameters ResolveParameters(string cacheContext)
    {
        if (string.Equals(cacheContext, LlmContextTypes.Bark, StringComparison.OrdinalIgnoreCase))
        {
            ModEntry.SMonitor.Log("[Llm] Resolving parameters for Bark (ambient thought bubbles).", StardewModdingAPI.LogLevel.Debug);
            return GenerationParametersPresets.ForBark();
        }

        if (string.Equals(cacheContext, LlmContextTypes.A2A, StringComparison.OrdinalIgnoreCase))
        {
            ModEntry.SMonitor.Log("[Llm] Resolving parameters for A2A (NPC-to-NPC conversations).", StardewModdingAPI.LogLevel.Debug);
            return GenerationParametersPresets.ForA2A();
        }

        // 主对话或其他未知上下文，统一回退到主对话配置
        ModEntry.SMonitor.Log($"[Llm] Resolving parameters for Main dialogue (context: {cacheContext ?? "(default)"}).", StardewModdingAPI.LogLevel.Debug);
        return GenerationParametersPresets.ForMainDialogue();
    }

    internal abstract Task<LlmResponse> RunInference(
        string systemPromptString, 
        string gameCacheString, 
        string npcCacheString, 
        string promptString, 
        string responseStart = "", 
        int n_predict = 2048, 
        string cacheContext = "", 
        bool allowRetry = true);

    internal abstract Dictionary<string, double>[] RunInferenceProbabilities(string fullPrompt, int n_predict = 1);

    internal virtual async Task<LlmResponse> RunStreamingInference(
        string systemPromptString,
        string gameCacheString,
        string npcCacheString,
        string promptString,
        Action<string> onToken,
        CancellationToken ct,
        string responseStart = "",
        int n_predict = 2048,
        string cacheContext = "")
    {
        var result = await RunInference(
            systemPromptString, gameCacheString, npcCacheString,
            promptString, responseStart, n_predict);
            
        if (result.IsSuccess && !string.IsNullOrWhiteSpace(result.Text))
            onToken(result.Text);
            
        return result;
    }

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