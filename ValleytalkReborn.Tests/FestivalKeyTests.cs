using ValleytalkReborn;
using Xunit;

// 节日键归一化 "season" + day 的合法性校验。
public class FestivalKeyTests
{
    [Theory]
    [InlineData("spring", 1, "spring1")]
    [InlineData("summer", 13, "summer13")]
    [InlineData("fall", 28, "fall28")]
    [InlineData("winter", 21, "winter21")]
    public void BuildFestivalKey_Valid_ReturnsKey(string season, int day, string expected)
    {
        Assert.Equal(expected, FestivalKeyBuilder.BuildFestivalKey(season, day));
    }

    [Theory]
    [InlineData("spring", 1, "spring1")]
    [InlineData("summer", 28, "summer28")]
    [InlineData("fall", 14, "fall14")]
    [InlineData("winter", 9, "winter9")]
    public void BuildFestivalKey_BoundaryDays_Accepted(string season, int day, string expected)
    {
        Assert.Equal(expected, FestivalKeyBuilder.BuildFestivalKey(season, day));
    }

    [Theory]
    [InlineData("Spring")]
    [InlineData("SPRING")]
    [InlineData("autumn")]
    [InlineData("")]
    [InlineData("monsoon")]
    public void BuildFestivalKey_InvalidSeason_Throws(string season)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => FestivalKeyBuilder.BuildFestivalKey(season, 1));
    }

    [Theory]
    [InlineData("spring", 0)]
    [InlineData("spring", 29)]
    [InlineData("spring", -1)]
    [InlineData("summer", 32)]
    public void BuildFestivalKey_InvalidDay_Throws(string season, int day)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => FestivalKeyBuilder.BuildFestivalKey(season, day));
    }
}
