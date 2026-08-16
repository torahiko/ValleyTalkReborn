using System;
using System.Threading.Tasks;

namespace ValleytalkReborn;

internal class LlmMistral : LlmOpenAiBase, IGetModelNames
{
    public LlmMistral(string apiKey, string modelName = null)
    {
        url = "https://api.mistral.ai";
        this.apiKey = apiKey;
        this.modelName = modelName ?? "mistral-large-latest";
    }

    public override string ExtraInstructions => "";
    public override bool IsHighlySensoredModel => false;

    public async Task<string[]> GetModelNamesAsync()
    {
        return await CoreGetModelNamesAsync();
    }
}