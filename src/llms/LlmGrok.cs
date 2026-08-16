using System;
using System.Threading.Tasks;

namespace ValleytalkReborn;

internal class LlmGrok : LlmOpenAiBase, IGetModelNames
{
    public LlmGrok(string apiKey, string modelName = null)
    {
        url = "https://api.x.ai";
        this.apiKey = apiKey;
        this.modelName = modelName ?? "grok-3";
    }

    public override string ExtraInstructions => "";
    public override bool IsHighlySensoredModel => false;

    public async Task<string[]> GetModelNamesAsync()
    {
        return await CoreGetModelNamesAsync();
    }
}