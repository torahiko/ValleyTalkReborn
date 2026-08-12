using System;
using StardewValley;
#nullable enable

namespace ValleytalkReborn;

/// <summary>
/// 表示星露谷物语中的时间点（不可变只读结构体）
/// </summary>
public readonly struct StardewTime : IComparable<StardewTime>, IEquatable<StardewTime>
{
    public Season Season { get; }
    public int DayOfMonth { get; }
    public int TimeOfDay { get; }
    public int Year { get; }

    /// <summary>
    /// 默认构造函数：第 1 年春季第 1 天 06:00
    /// </summary>
    public StardewTime()
    {
        Year = 1;
        Season = Season.Spring;
        DayOfMonth = 1;
        TimeOfDay = 600;
    }

    public StardewTime(WorldDate date, int time) 
        : this(date.Year, (Season)date.Season, date.DayOfMonth, time) { }

    public StardewTime(int year, Season season, int dayOfMonth, int timeOfDay)
    {
        Year = year;
        Season = season;
        DayOfMonth = dayOfMonth;
        TimeOfDay = timeOfDay;
    }

    /// <summary>
    /// 基于当前游戏日期加上或减去指定天数构造（默认时间 06:00）
    /// </summary>
    public StardewTime(int addDays)
    {
        var baseTime = new StardewTime(Game1.Date, 600);
        var target = baseTime.AddDays(addDays);
        
        Year = target.Year;
        Season = target.Season;
        DayOfMonth = target.DayOfMonth;
        TimeOfDay = target.TimeOfDay;
    }

    /// <summary>
    /// 将星露谷的时间格式 (如 600, 1430, 2600) 转换为一天中从 06:00 AM 开始累积的游戏分钟数
    /// </summary>
    private static double TimeToMinutes(int timeOfDay)
    {
        int hours = timeOfDay / 100;
        int minutes = timeOfDay % 100;
        return (hours - 6) * 60 + minutes;
    }

    /// <summary>
    /// 获取自第 0 年春季第 1 天 06:00 以来转换的精确总天数（包含日内时间的小数部分）
    /// </summary>
    public double TotalDays => 
        (Year * 112) + ((int)Season * 28) + (DayOfMonth - 1) + (TimeToMinutes(TimeOfDay) / 1200.0);

    /// <summary>
    /// 计算距离另一个时间点相差的天数
    /// </summary>
    public double DaysSince(StardewTime other) => other.TotalDays - TotalDays;

    public double DaysSince(int year, Season season, int dayOfMonth, int timeOfDay)
    {
        return DaysSince(new StardewTime(year, season, dayOfMonth, timeOfDay));
    }

    /// <summary>
    /// 生成描述时间差的本地化文本
    /// </summary>
    public string SinceDescription(StardewTime? other = null)
    {
        var compareTarget = other ?? new StardewTime(Game1.Date, Game1.timeOfDay);
        double days = DaysSince(compareTarget);
        
        var thisSeasonKey = Utility.getSeasonKey((StardewValley.Season)Season);
        var seasonDisplay = Game1.content.LoadString("Strings\\StringsFromCSFiles:" + thisSeasonKey);

        return days switch
        {
            < 0 => Util.GetString("timeInTheFuture"),
            < (double)1 / 120 => Util.GetString("timeJustNow"),            // ~10 游戏分钟内
            < (double)1 / 24 => Util.GetString("timeInTheLastHour"),        // ~50 游戏分钟内
            < 1 => compareTarget.DayOfMonth == DayOfMonth 
                ? Util.GetString("timeEarlierToday") 
                : Util.GetString("timeYesterday"),
            < 14 => Util.GetString("timeDaysAgo", new { days = (int)days }),
            < 56 => Util.GetString("timeDaysAgoSeasonDay", new { days = (int)days, day = DayOfMonth, season = seasonDisplay }),
            < 112 => compareTarget.Year == Year 
                ? Util.GetString("timeEarlierThisYear", new { day = DayOfMonth, season = seasonDisplay })
                : Util.GetString("timeLastYear", new { day = DayOfMonth, season = seasonDisplay }),
            _ => Util.GetString("timeALongTimeAgo", new { day = DayOfMonth, season = seasonDisplay, year = Year })
        };
    }

    /// <summary>
    /// 增加指定天数并返回新的 StardewTime 对象（高效率数学计算）
    /// </summary>
    public StardewTime AddDays(int offset)
    {
        int totalDays = (Year * 112) + ((int)Season * 28) + (DayOfMonth - 1) + offset;
        if (totalDays < 0) totalDays = 0;

        int newYear = totalDays / 112;
        int remDays = totalDays % 112;
        int newSeason = remDays / 28;
        int newDay = (remDays % 28) + 1;

        return new StardewTime(newYear, (Season)newSeason, newDay, TimeOfDay);
    }

    public int CompareTo(StardewTime other) => TotalDays.CompareTo(other.TotalDays);

    public bool After(StardewTime compareTo) => CompareTo(compareTo) > 0;

    public bool IsJustNow(StardewTime? other = null)
    {
        var compareTarget = other ?? new StardewTime(Game1.Date, Game1.timeOfDay);
        var elapsed = DaysSince(compareTarget);
        return elapsed >= 0 && elapsed < (1.0 / 120.0);
    }

    public bool Equals(StardewTime other) => TotalDays.Equals(other.TotalDays);
    public override bool Equals(object? obj) => obj is StardewTime other && Equals(other);  // ← 这里加了 ? 
    public override int GetHashCode() => HashCode.Combine(Year, Season, DayOfMonth, TimeOfDay);

    public static bool operator ==(StardewTime left, StardewTime right) => left.Equals(right);
    public static bool operator !=(StardewTime left, StardewTime right) => !left.Equals(right);
    public static bool operator <(StardewTime left, StardewTime right) => left.CompareTo(right) < 0;
    public static bool operator >(StardewTime left, StardewTime right) => left.CompareTo(right) > 0;
    public static bool operator <=(StardewTime left, StardewTime right) => left.CompareTo(right) <= 0;
    public static bool operator >=(StardewTime left, StardewTime right) => left.CompareTo(right) >= 0;
}