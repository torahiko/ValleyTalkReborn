using System;
using System.Threading.Tasks;

namespace ValleytalkReborn;

internal class LlmOAICompatible : LlmOpenAiBase, IGetModelNames
{
    public LlmOAICompatible(string apiKey, string url, string modelName = null)
    {
        this.url = UrlHelper.NormalizeBaseUrl(url);
        this.apiKey = apiKey;
        this.modelName = modelName ?? "mistral-large-latest";
    }

    public override string ExtraInstructions => "";

    /// <summary>
    /// 自适应审查敏感型模型判定：Claude、GPT、Gemini 等主流大厂模型自动走平滑安全通道，
    /// 避免触发安全审查机制导致的拒答或元指令重定向。
    /// </summary>
    public override bool IsHighlySensoredModel
    {
        get
        {
            if (string.IsNullOrWhiteSpace(modelName)) return false;
            string m = modelName.ToLowerInvariant();
            return m.Contains("claude") ||
                   m.Contains("gpt") ||
                   m.Contains("gemini") ||
                   m.Contains("o1") ||
                   m.Contains("o3") ||
                   m.Contains("o4");
        }
    }

    public async Task<string[]> GetModelNamesAsync()
    {
        return await CoreGetModelNamesAsync();
    }
}