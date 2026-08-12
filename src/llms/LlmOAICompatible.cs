using ValleytalkReborn;

namespace ValleytalkReborn;

internal class LlmOAICompatible : LlmOpenAiBase, IGetModelNames
{
    public LlmOAICompatible(string apiKey, string url, string modelName = null)
    {
        // 循环剥离所有可能的无效后缀，避免 URL 组合拼接引发问题
        string prevUrl;
        do
        {
            prevUrl = url;
            if (url.EndsWith("/")) url = url.Substring(0, url.Length - 1);
            if (url.EndsWith("/chat/completions")) url = url.Substring(0, url.Length - 17); // 修复：/chat/completions 长度是 17
            if (url.EndsWith("/v1")) url = url.Substring(0, url.Length - 3);
        } while (url != prevUrl);
        
        this.url = url;
        this.apiKey = apiKey;
        this.modelName = modelName ?? "mistral-large-latest";
    }

    public override string ExtraInstructions => "";

    public override bool IsHighlySensoredModel => false;

    public string[] GetModelNames()
    {
        return CoreGetModelNames();
    }
}