namespace ValleytalkReborn;

internal class ActivityHistory : IHistory
{
    private readonly string _activity;

    public ActivityHistory(string activity)
    {
        _activity = activity ?? string.Empty;
    }

    public string Format(string npcName)
    {
        return _activity;
    }
}