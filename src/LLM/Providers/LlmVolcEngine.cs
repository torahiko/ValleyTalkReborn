using System;
using System.Threading.Tasks;

namespace ValleytalkReborn;

internal class LlmVolcEngine : LlmOpenAiBase, IGetModelNames
{
    public LlmVolcEngine(string apiKey, string url, string modelName = null)
    {
        this.url = url;
        this.apiKey = apiKey;
        this.modelName = modelName;
    }

    public override string ExtraInstructions => "";
    public override bool IsHighlySensoredModel => false;

    public async Task<string[]> GetModelNamesAsync()
    {
        return await CoreGetModelNamesAsync();
    }
}