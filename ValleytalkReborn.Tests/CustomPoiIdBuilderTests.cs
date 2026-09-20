using ValleytalkReborn;
using Xunit;

// 自定义 POI 标识符生成：唯一 / 冲突递增 / 消毒 / 空白名。
public class CustomPoiIdBuilderTests
{
    [Fact]
    public void BuildCustomPoiId_Unique_ReturnsCustomPrefixed()
    {
        var existing = new List<string>();
        Assert.Equal("Custom_MySpot", CustomPoiIdBuilder.BuildCustomPoiId("MySpot", existing));
    }

    [Fact]
    public void BuildCustomPoiId_ConflictOnce_ReturnsSuffix2()
    {
        var existing = new List<string> { "Custom_MySpot" };
        Assert.Equal("Custom_MySpot_2", CustomPoiIdBuilder.BuildCustomPoiId("MySpot", existing));
    }

    [Fact]
    public void BuildCustomPoiId_ConflictTwice_ReturnsSuffix3()
    {
        var existing = new List<string> { "Custom_MySpot", "Custom_MySpot_2" };
        Assert.Equal("Custom_MySpot_3", CustomPoiIdBuilder.BuildCustomPoiId("MySpot", existing));
    }

    [Fact]
    public void BuildCustomPoiId_SanitizesInvalidChars()
    {
        var existing = new List<string>();
        // '/' and ':' are invalid filename chars
        Assert.Equal("Custom_A_B_C", CustomPoiIdBuilder.BuildCustomPoiId("A/B:C", existing));
    }

    [Fact]
    public void BuildCustomPoiId_BlankName_ReturnsCustom()
    {
        var existing = new List<string>();
        Assert.Equal("Custom_Custom", CustomPoiIdBuilder.BuildCustomPoiId("   ", existing));
        // 空白名（空串）同样消毒为 "Custom"，若已存在则递增
        var existing2 = new List<string> { "Custom_Custom" };
        Assert.Equal("Custom_Custom_2", CustomPoiIdBuilder.BuildCustomPoiId("   ", existing2));
        Assert.Equal("Custom_Custom_2", CustomPoiIdBuilder.BuildCustomPoiId("", existing2));
    }
}
