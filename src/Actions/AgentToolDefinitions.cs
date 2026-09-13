using Newtonsoft.Json.Linq;

namespace ValleytalkReborn;

/// <summary>
/// 原生工具调用已全量移除（VT-NOTOOLS-T1）。
/// 方法保留为空数组提供者，使请求组装调用点编译不变。
/// </summary>
internal static class AgentToolDefinitions
{
    public static JArray GetOpenAiToolsArray() => new JArray();
    public static JArray GetAnthropicToolsArray() => new JArray();
    public static JArray GetGeminiToolsArray() => new JArray();
}
