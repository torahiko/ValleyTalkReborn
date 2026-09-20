using ValleytalkReborn;
using Xunit;

// 纯函数静态类测试：覆盖往返、非法输入、边界、EnumerateGrid。
// 旧实现使用 t%30==0（整数取模），会误拒 1900/2000/2300 等合法墙钟时间、
// 并误纳 2550/2530；新实现改用 (t%100)%30==0（墙钟分钟位）。
// 注：契约中"末项 2550 / 合法 2550"与既定公式 (t%100)%30==0 及"共 40 项"
// 不自洽（2550 分钟位 50 非 30 网格；6..25×{00,30}=40 项末项为 2530）。
// 此处以显式公式与 40 项墙钟网格为权威（算法建议："小时 6..25 × {00, 30}"）。
public class TimeWindowConverterTests
{
    // ── 合法往返（含契约要求值，全部满足 (t%100)%30==0）──
    [Theory]
    [InlineData("1800-2130", 1800, 2130)]
    [InlineData("0600-0630", 600, 630)]
    [InlineData("0600-2530", 600, 2530)]
    [InlineData("1900-1930", 1900, 1930)]
    [InlineData("2000-2030", 2000, 2030)]
    [InlineData("2200-2230", 2200, 2230)]
    [InlineData("2300-2330", 2300, 2330)]
    [InlineData("1000-1030", 1000, 1030)]
    [InlineData("0900-0930", 900, 930)]
    [InlineData("1200-1230", 1200, 1230)]
    public void TryParse_Valid_Parses(string input, int expectedStart, int expectedEnd)
    {
        bool ok = TimeWindowConverter.TryParse(input, out int start, out int end);
        Assert.True(ok);
        Assert.Equal(expectedStart, start);
        Assert.Equal(expectedEnd, end);
    }

    [Theory]
    [InlineData(1800, 2130, "1800-2130")]
    [InlineData(600, 2530, "0600-2530")]
    [InlineData(2300, 2530, "2300-2530")]
    [InlineData(900, 930, "0900-0930")]
    public void Format_Valid_Formats(int start, int end, string expected)
    {
        Assert.Equal(expected, TimeWindowConverter.Format(start, end));
    }

    [Fact]
    public void RoundTrip_Preserves()
    {
        const string input = "1800-2130";
        Assert.True(TimeWindowConverter.TryParse(input, out int s, out int e));
        Assert.Equal(input, TimeWindowConverter.Format(s, e));
    }

    // ── 非法输入（含契约要求：660/690/720/1859/1855/2560/530/2600）──
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1800")]          // 无分隔符
    [InlineData("-2130")]         // 缺起始
    [InlineData("1800-")]         // 缺结束
    [InlineData("180-2130")]      // 非 4 位
    [InlineData("18000-2130")]    // 起始 5 位
    [InlineData("abcd-2130")]     // 非数字
    [InlineData("1800-2131")]     // 结束不在 30 网格
    [InlineData("2130-1800")]     // 起始 >= 结束
    [InlineData("0500-0600")]     // 起始 < 600
    [InlineData("1800-2600")]     // 结束 > 2550
    [InlineData("0660-0700")]     // 起始分钟位 60（非法）
    [InlineData("0690-0700")]     // 起始分钟位 90（非法）
    [InlineData("0700-0720")]     // 结束分钟位 20（非法）
    [InlineData("1859-1900")]     // 起始分钟位 59（非法）
    [InlineData("1855-1900")]     // 起始分钟位 55（非法）
    [InlineData("2550-2560")]     // 结束分钟位 60 且越界
    [InlineData("0530-0600")]     // 起始 < 600（长度合法但值越界）
    [InlineData("2550-2600")]     // 结束 > 2550
    public void TryParse_Invalid_ReturnsFalse_ZeroOutputs(string input)
    {
        bool ok = TimeWindowConverter.TryParse(input, out int start, out int end);
        Assert.False(ok);
        Assert.Equal(0, start);
        Assert.Equal(0, end);
    }

    // ── 2550 在墙钟网格下应被拒绝（旧实现 t%30==0 误纳之）──
    [Theory]
    [InlineData("0600-2550")]
    [InlineData("2500-2550")]
    public void TryParse_2550_WallClockInvalid_ReturnsFalse(string input)
    {
        Assert.False(TimeWindowConverter.TryParse(input, out _, out _));
    }

    // ── 边界 600 / 2530（墙钟最大网格值）────────────────────────
    [Fact]
    public void TryParse_BoundaryMinMax_Accepted()
    {
        Assert.True(TimeWindowConverter.TryParse("0600-2530", out _, out _));
    }

    [Fact]
    public void Format_OutOfRange_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TimeWindowConverter.Format(500, 1800));
        Assert.Throws<ArgumentOutOfRangeException>(() => TimeWindowConverter.Format(1800, 2600));
        Assert.Throws<ArgumentOutOfRangeException>(() => TimeWindowConverter.Format(1800, 1800));
        Assert.Throws<ArgumentOutOfRangeException>(() => TimeWindowConverter.Format(1800, 2550)); // 2550 分钟位 50
    }

    // ── EnumerateGrid 回归集 ───────────────────────────────────────
    [Fact]
    public void EnumerateGrid_Count_Is_40()
    {
        var grid = TimeWindowConverter.EnumerateGrid();
        Assert.Equal(40, grid.Count);
    }

    [Fact]
    public void EnumerateGrid_FirstThree_And_Last()
    {
        var grid = TimeWindowConverter.EnumerateGrid();
        Assert.Equal(600, grid[0]);
        Assert.Equal(630, grid[1]);
        Assert.Equal(700, grid[2]);
        Assert.Equal(2530, grid[grid.Count - 1]); // 6..25×{00,30} 末项；契约"末项 2550"与公式不自洽
    }

    [Fact]
    public void EnumerateGrid_Contains_And_NotContains()
    {
        var grid = TimeWindowConverter.EnumerateGrid();
        Assert.Contains(1900, grid);
        Assert.Contains(2330, grid);
        Assert.DoesNotContain(660, grid);
        Assert.DoesNotContain(690, grid);
        Assert.DoesNotContain(720, grid);
        Assert.DoesNotContain(2550, grid); // 墙钟网格不含 2550
    }

    [Fact]
    public void EnumerateGrid_EveryItem_OnWallClockGrid()
    {
        var grid = TimeWindowConverter.EnumerateGrid();
        foreach (int t in grid)
        {
            Assert.True(t >= 600 && t <= 2550);
            Assert.Equal(0, (t % 100) % 30);
        }
    }

    // ── 往返：对每个 v ∈ EnumerateGrid 且 v<2530 ───────────────────
    [Fact]
    public void EnumerateGrid_RoundTrip_All()
    {
        var grid = TimeWindowConverter.EnumerateGrid();
        foreach (int v in grid)
        {
            if (v >= 2530) continue;
            string formatted = TimeWindowConverter.Format(v, 2530);
            Assert.True(TimeWindowConverter.TryParse(formatted, out int s, out int e));
            Assert.Equal(v, s);
            Assert.Equal(2530, e);
        }
    }

    [Fact]
    public void Format_SameStartEnd_Throws()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TimeWindowConverter.Format(2530, 2530));
    }
}
