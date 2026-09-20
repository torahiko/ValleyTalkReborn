using ValleytalkReborn.Services;
using Xunit;

// MakeKey 键归一化语义：顺序无关、大小写不敏感、字母序归一。
public class NpcRelationKeyTests
{
    [Fact]
    public void MakeKey_OrderIndependent()
    {
        Assert.Equal(NpcRelationOverlayService.MakeKey("Shane", "Sam"), NpcRelationOverlayService.MakeKey("Sam", "Shane"));
        // 大小写混合时，参数顺序交换仍归一为同一键（大小写敏感归一：排在前面的原样保留）
        Assert.Equal(NpcRelationOverlayService.MakeKey("SHANE", "sam"), NpcRelationOverlayService.MakeKey("sam", "SHANE"));
    }

    [Fact]
    public void MakeKey_AlphabeticalNormalization()
    {
        // "Sam" < "Shane"（字母序），故键 = "Sam|Shane"
        Assert.Equal("Sam|Shane", NpcRelationOverlayService.MakeKey("Shane", "Sam"));
        Assert.Equal("Sam|Shane", NpcRelationOverlayService.MakeKey("Sam", "Shane"));
    }

    [Fact]
    public void MakeKey_SameNpc()
    {
        Assert.Equal("Abigail|Abigail", NpcRelationOverlayService.MakeKey("Abigail", "Abigail"));
    }
}
