using System.Text;

namespace ValleytalkReborn;

/// <summary>
/// 环境自言自语（Ambient Bark）角色口吻结构化数据。
/// 由内容包 JSON 反序列化填充（Newtonsoft），运行时只读消费。
/// </summary>
public sealed class AmbientBarkPrompt
{
    private const string VoiceHeader = "[VOICE & ATTITUDE]";
    private const string HabitsHeader = "[SPOKEN HABITS]";
    private const string LensesHeader = "[OBSERVATION LENSES]";

    public string VoiceAndAttitude { get; set; } = string.Empty;
    public string SpokenHabits { get; set; } = string.Empty;
    public string ObservationLenses { get; set; } = string.Empty;

    public bool IsEmpty =>
        string.IsNullOrWhiteSpace(VoiceAndAttitude) &&
        string.IsNullOrWhiteSpace(SpokenHabits) &&
        string.IsNullOrWhiteSpace(ObservationLenses);

    /// <summary>Bark 视图：依序 (VoiceAndAttitude, SpokenHabits, ObservationLenses)</summary>
    public string BuildFull()
    {
        var sb = new StringBuilder();
        AppendSection(sb, VoiceHeader, VoiceAndAttitude);
        AppendSection(sb, HabitsHeader, SpokenHabits);
        AppendSection(sb, LensesHeader, ObservationLenses);
        return sb.ToString().TrimEnd();
    }

    /// <summary>A2A 视图：仅 (VoiceAndAttitude, SpokenHabits)</summary>
    public string BuildForA2A()
    {
        var sb = new StringBuilder();
        AppendSection(sb, VoiceHeader, VoiceAndAttitude);
        AppendSection(sb, HabitsHeader, SpokenHabits);
        return sb.ToString().TrimEnd();
    }

    private static void AppendSection(StringBuilder sb, string header, string field)
    {
        if (string.IsNullOrWhiteSpace(field)) return;
        sb.AppendLine(header);
        sb.AppendLine(field.Trim());
        sb.AppendLine();
    }
}
