using System;

namespace ValleytalkReborn;

/// <summary>节日键归一化 "season" + day 的合法性校验。纯函数。</summary>
internal static class FestivalKeyBuilder
{
    private static readonly string[] Seasons = { "spring", "summer", "fall", "winter" };

    /// <summary>"season" + day，season 须为四枚举之一小写、day 须 ∈ [1,28]。</summary>
    public static string BuildFestivalKey(string season, int day)
    {
        if (string.IsNullOrEmpty(season) || Array.IndexOf(Seasons, season) < 0)
            throw new ArgumentOutOfRangeException(nameof(season), season, "季节须为 spring/summer/fall/winter 之一");
        if (day < 1 || day > 28)
            throw new ArgumentOutOfRangeException(nameof(day), day, "日期须 ∈ [1, 28]");
        return $"{season}{day}";
    }
}
