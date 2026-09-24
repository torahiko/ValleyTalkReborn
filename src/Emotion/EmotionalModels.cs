namespace ValleytalkReborn;

public sealed class EmotionalBaseline
{
    public float Valence { get; set; } = 0.05f;
    public float Arousal { get; set; } = 0.50f;
    public float Openness { get; set; } = 0.50f;
}

public sealed class EmotionalBias
{
    public float Valence { get; set; } = 0f;
    public float Arousal { get; set; } = 0f;
    public float Openness { get; set; } = 0f;
}

public sealed class TodayScene
{
    public string Id = "";
    public string Scene = "";
    public string Tag = "";
    public EmotionalBias Bias = new();
    public string Preoccupation = "";
}

public readonly record struct EmotionSnapshot(
    float Valence, float Arousal, float Openness, float BaselineOpenness);

public static class EmotionShockIds
{
    public static string Weather(string npc) => $"Weather:{npc}:Weather";
    public static string Neglect(string npc) => $"Neglect:{npc}:Cold";
    public static string Gift(string npc, string tasteKey) => $"Gift:{npc}:{tasteKey}";
    public static string Dialogue(string npc, string kind) => $"Dialogue:{npc}:{kind}";
    public static string Debug(string npc) => $"Debug:{npc}";
}
