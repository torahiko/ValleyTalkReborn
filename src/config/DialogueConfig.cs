using StardewModdingAPI;

namespace ValleytalkReborn;

/// <summary>
/// 对话系统配置模型。
/// 第一版只迁移少量参数，不改变默认值，不支持热加载。
/// </summary>
internal sealed class DialogueConfig
{
    public bool EnableAmbientBarks { get; set; } = true;
    public bool EnableA2A { get; set; } = true;

    public int LlmTimeoutSeconds { get; set; } = 30;
    public int BarkApiCooldownTicks { get; set; } = 300;
    public int BarkQueueSize { get; set; } = 3;
    public int A2AMaxParticipants { get; set; } = 4;

    /// <summary>
    /// 校验并修正配置值到合法范围。
    /// </summary>
    internal void Validate(IMonitor monitor)
    {
        LlmTimeoutSeconds = Clamp(LlmTimeoutSeconds, 5, 120);
        BarkApiCooldownTicks = Clamp(BarkApiCooldownTicks, 30, 3600);
        BarkQueueSize = Clamp(BarkQueueSize, 1, 20);
        A2AMaxParticipants = Clamp(A2AMaxParticipants, 2, 4);

        monitor?.Log(
            "[DialogueConfig] 配置已校验。",
            LogLevel.Debug);
    }

    private static int Clamp(int value, int min, int max)
    {
        if (value < min) return min;
        if (value > max) return max;
        return value;
    }
}
