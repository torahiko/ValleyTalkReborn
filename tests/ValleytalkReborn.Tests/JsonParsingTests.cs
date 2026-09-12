using Xunit;

namespace ValleytalkReborn.Tests;

/// <summary>
/// Tests for JSON parsing utilities.
/// </summary>
public class JsonParsingTests
{
    [Theory]
    [InlineData("[\"a\", \"b\"]")]
    [InlineData("`json\n[\"a\", \"b\"]\n`")]
    [InlineData("prefix [\"a\", \"b\"] suffix")]
    public void ExtractJsonArray_ShouldParseCommonFormats(string raw)
    {
        var result = DialogueParsing.ExtractJsonArrayString(raw);

        Assert.NotNull(result);
        Assert.StartsWith("[", result);
        Assert.EndsWith("]", result);
    }

    [Fact]
    public void ExtractJsonArray_InvalidInput_ShouldReturnNull()
    {
        var result = DialogueParsing.ExtractJsonArrayString("no json here");

        Assert.Null(result);
    }

    [Fact]
    public void ExtractJsonArray_NullInput_ShouldReturnNull()
    {
        var result = DialogueParsing.ExtractJsonArrayString(null);

        Assert.Null(result);
    }

    [Fact]
    public void ParseBarkJson_StandardArray_ShouldSucceed()
    {
        var result = DialogueParsing.ParseBarkJson("[\"hello\", \"world\"]");

        Assert.NotNull(result);
        Assert.Equal(2, result.Length);
        Assert.Equal("hello", result[0]);
        Assert.Equal("world", result[1]);
    }

    [Fact]
    public void ParseBarkJson_ObjectArray_ShouldFallback()
    {
        var result = DialogueParsing.ParseBarkJson("[{\"line\":\"hi\"}, {\"text\":\"bye\"}]");

        Assert.NotNull(result);
        Assert.Equal(2, result.Length);
        Assert.Equal("hi", result[0]);
        Assert.Equal("bye", result[1]);
    }

    [Fact]
    public void ParseBarkJson_NumberedListWithDash_ShouldExtractLevel5()
    {
        var raw = @" 1]
- 这破天冷得吉他弦都冻手，回头得给琴包加层棉。
[ 2]
- 嘿，你醒啦？今早想听我弹段新写的副歌不？
[ 3]
- 屋里这彩色帘子晃得人眼晕，不过比外头白茫茫一片强多了。
[ 4]
- 等会儿得喂鸡去，顺便想想晚上给咱俩做点啥热乎的。
[ 5]
- 雪下得挺厚，等会儿在屋里练几个滑板动作暖暖身子。";

        var result = DialogueParsing.ParseBarkJson(raw);

        Assert.NotNull(result);
        Assert.Equal(5, result.Length);
        Assert.Contains("这破天冷得吉他弦都冻手，回头得给琴包加层棉。", result);
        Assert.Contains("嘿，你醒啦？今早想听我弹段新写的副歌不？", result);
        Assert.Contains("屋里这彩色帘子晃得人眼晕，不过比外头白茫茫一片强多了。", result);
        Assert.Contains("等会儿得喂鸡去，顺便想想晚上给咱俩做点啥热乎的。", result);
        Assert.Contains("雪下得挺厚，等会儿在屋里练几个滑板动作暖暖身子。", result);
    }

    [Fact]
    public void ParseBarkJson_DashList_ShouldExtractLevel5()
    {
        var raw = "- 第一行台词\n- 第二行台词\n- 第三行台词";

        var result = DialogueParsing.ParseBarkJson(raw);

        Assert.NotNull(result);
        Assert.Equal(3, result.Length);
        Assert.Equal("第一行台词", result[0]);
        Assert.Equal("第二行台词", result[1]);
        Assert.Equal("第三行台词", result[2]);
    }

    [Fact]
    public void ParseBarkJson_NumberedDot_ShouldExtractLevel5()
    {
        var raw = "1. 早上好\n2. 中午好\n3. 晚上好";

        var result = DialogueParsing.ParseBarkJson(raw);

        Assert.NotNull(result);
        Assert.Equal(3, result.Length);
        Assert.Equal("早上好", result[0]);
        Assert.Equal("中午好", result[1]);
        Assert.Equal("晚上好", result[2]);
    }

    [Fact]
    public void ParseBarkJson_AngleBracketNumber_ShouldExtractLevel5()
    {
        var raw = "<1> 今天天气不错\n<2> 屋里挺暖和";

        var result = DialogueParsing.ParseBarkJson(raw);

        Assert.NotNull(result);
        Assert.Equal(2, result.Length);
        Assert.Equal("今天天气不错", result[0]);
        Assert.Equal("屋里挺暖和", result[1]);
    }

    [Fact]
    public void ParseBarkJson_ParenNumber_ShouldExtractLevel5()
    {
        var raw = "(1) 今天天气不错\n(2) 屋里挺暖和";

        var result = DialogueParsing.ParseBarkJson(raw);

        Assert.NotNull(result);
        Assert.Equal(2, result.Length);
        Assert.Equal("今天天气不错", result[0]);
        Assert.Equal("屋里挺暖和", result[1]);
    }

    [Fact]
    public void ParseBarkJson_ChineseBracketNumber_ShouldExtractLevel5()
    {
        var raw = "【1】今天天气不错\n【2】屋里挺暖和";

        var result = DialogueParsing.ParseBarkJson(raw);

        Assert.NotNull(result);
        Assert.Equal(2, result.Length);
        Assert.Equal("今天天气不错", result[0]);
        Assert.Equal("屋里挺暖和", result[1]);
    }

    [Fact]
    public void ParseBarkJson_CircledNumber_ShouldExtractLevel5()
    {
        var raw = "① 等会儿喂鸡\n② 屋里挺暖和";

        var result = DialogueParsing.ParseBarkJson(raw);

        Assert.NotNull(result);
        Assert.Equal(2, result.Length);
        Assert.Equal("等会儿喂鸡", result[0]);
        Assert.Equal("屋里挺暖和", result[1]);
    }

    [Fact]
    public void ParseBarkJson_NumberWithoutDelimiter_ShouldNotStrip()
    {
        // "号" 不是分隔符，不应被误删
        var raw = "3号矿洞的矿石不错";

        var result = DialogueParsing.ParseBarkJson(raw);

        Assert.NotNull(result);
        Assert.Single(result);
        Assert.Equal("3号矿洞的矿石不错", result[0]);
    }

    [Fact]
    public void ParseBarkJson_EmptyInput_ShouldReturnNull()
    {
        Assert.Null(DialogueParsing.ParseBarkJson(null));
        Assert.Null(DialogueParsing.ParseBarkJson(""));
        Assert.Null(DialogueParsing.ParseBarkJson("   "));
    }
}
