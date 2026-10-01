// StreamTokenPipelineTests.cs
// VT-STREAM-02: 流式 token 分流器单元测试。
//
// 覆盖四个规格场景：碎片标签跨 chunk 拼接、'%' 选项区分流、方括号熔断、
// 原版情绪码切分；另含首 chunk 前缀清洗、Reset 复用于下一轮流等回归项。
//
// 本类为纯 C# 算法测试，不触碰任何 Game1 静态状态。

using System.Collections.Generic;
using System.Linq;
using ValleytalkReborn.Dialogue.Coordination;
using Xunit;

namespace ValleytalkReborn.Tests;

public class StreamTokenPipelineTests
{
    /// <summary>把多个 chunk 依次送入并汇总所有片段（含 Flush）。</summary>
    private static List<StreamSegment> Run(params string[] chunks)
    {
        var pipeline = new StreamTokenPipeline();
        var all = new List<StreamSegment>();
        foreach (string chunk in chunks)
            all.AddRange(pipeline.Feed(chunk));
        all.AddRange(pipeline.Flush());
        return all;
    }

    private static List<StreamSegment> RunOne(string chunk) => Run(chunk);

    private static List<StreamSegment> OfType(IEnumerable<StreamSegment> segments, StreamSegmentType type)
        => segments.Where(s => s.Type == type).ToList();

    private static string ConcatText(IEnumerable<StreamSegment> segments)
        => string.Concat(segments.Where(s => s.Type == StreamSegmentType.Text).Select(s => s.Payload));

    // ── 规格场景 1：碎片标签跨 chunk 拼接 ──

    [Fact]
    public void Feed_FragmentedActionTag_AcrossChunks_ProducesActionAndText()
    {
        List<StreamSegment> segments = Run("[ACT", "ION:EMOTE:HEART]你好");

        List<StreamSegment> actions = OfType(segments, StreamSegmentType.Action);
        List<StreamSegment> texts = OfType(segments, StreamSegmentType.Text);

        Assert.Single(actions);
        Assert.Equal("ACTION:EMOTE:HEART", actions[0].Payload);
        Assert.Single(texts);
        Assert.Equal("你好", texts[0].Payload);
    }

    [Fact]
    public void Feed_FragmentedActionTag_DoesNotLeakRawBracketFragment()
    {
        string all = string.Concat(Run("[ACT", "ION:EMOTE:HEART]你好").Select(s => s.Payload));

        Assert.DoesNotContain("[ACT", all);
        Assert.DoesNotContain("]", all);
    }

    [Fact]
    public void Feed_FragmentedTag_AcrossManyChunks_StillJoins()
    {
        List<StreamSegment> segments = Run("[", "A", "C", "T", "I", "O", "N:", "FOLLOW", "]");

        List<StreamSegment> actions = OfType(segments, StreamSegmentType.Action);
        Assert.Single(actions);
        Assert.Equal("ACTION:FOLLOW", actions[0].Payload);
    }

    [Fact]
    public void Feed_CompleteUiTag_ProducesActionPayload()
    {
        List<StreamSegment> actions = OfType(RunOne("跟我走 [UI:FOLLOW]"), StreamSegmentType.Action);

        Assert.Single(actions);
        Assert.Equal("UI:FOLLOW", actions[0].Payload);
    }

    [Fact]
    public void Feed_MoodTag_ProducesPortraitPayload()
    {
        List<StreamSegment> portraits = OfType(RunOne("你好[MOOD:happy]"), StreamSegmentType.Portrait);

        Assert.Single(portraits);
        Assert.Equal("happy", portraits[0].Payload);
    }

    [Fact]
    public void Feed_TagIsCaseInsensitive()
    {
        List<StreamSegment> actions = OfType(RunOne("[action:EMOTE:HEART]"), StreamSegmentType.Action);

        Assert.Single(actions);
        Assert.Equal("action:EMOTE:HEART", actions[0].Payload);
    }

    [Fact]
    public void Feed_TextBeforeAndAfterTag_IsSplitIntoSeparateSegments()
    {
        List<StreamSegment> texts = OfType(RunOne("前面[UI:FOLLOW]后面"), StreamSegmentType.Text);

        Assert.Equal(2, texts.Count);
        Assert.Equal("前面", texts[0].Payload);
        Assert.Equal("后面", texts[1].Payload);
    }

    // ── 规格场景 2：'%' 选项区分流 ──

    [Fact]
    public void Feed_OptionSection_SuppressesAllSubsequentText()
    {
        List<StreamSegment> segments = Run("你好啊！\n%", " 建议回复\n- 我很好\n- 你呢");

        Assert.Equal("你好啊！", ConcatText(segments));
    }

    [Fact]
    public void Flush_OptionSection_CollectsSuggestions()
    {
        var pipeline = new StreamTokenPipeline();
        pipeline.Feed("你好啊！\n%").ToList();
        pipeline.Feed(" 建议回复\n- 我很好\n- 你呢").ToList();
        pipeline.Flush().ToList();

        Assert.Equal(new[] { "建议回复", "我很好", "你呢" }, pipeline.GetCollectedSuggestions());
    }

    [Fact]
    public void Feed_PercentAtStreamStart_EntersOptionSection()
    {
        List<StreamSegment> segments = Run("%\n- 选项甲");

        Assert.Empty(segments);
    }

    [Fact]
    public void Feed_PercentMidLine_DoesNotEnterOptionSection()
    {
        // '%' 不在行首时按普通文本处理（百分号可能出现在台词中间）
        List<StreamSegment> segments = RunOne("折扣 50%\n继续");

        Assert.Contains("50%", ConcatText(segments));
    }

    // ── VT-STREAM-03：换行延迟判定 ──

    [Fact]
    public void Feed_CrossLineDialogue_PreservesNewlineAsText()
    {
        // 常规对白流中的换行必须作为正文字符保留，驱动 SpriteText 折行与
        // 打字机 450ms 换行顿挫，防止词句跨行粘连。
        List<StreamSegment> segments = RunOne("第一句\n第二句");

        Assert.Equal("第一句\n第二句", ConcatText(segments));
    }

    [Fact]
    public void Feed_CrossLineDialogue_EmitsNewlineAsDedicatedTextSegment()
    {
        List<StreamSegment> texts = OfType(RunOne("第一句\n第二句"), StreamSegmentType.Text);

        Assert.Equal(3, texts.Count);
        Assert.Equal("第一句", texts[0].Payload);
        Assert.Equal("\n", texts[1].Payload);
        Assert.Equal("第二句", texts[2].Payload);
    }

    [Fact]
    public void Feed_CrossLineDialogue_CrlfCollapsesToSingleNewline()
    {
        // '\r' 与 '\n' 连续到达时待定标记幂等，不得产生两个换行片段。
        List<StreamSegment> segments = RunOne("第一句\r\n第二句");

        Assert.Equal("第一句\n第二句", ConcatText(segments));
    }

    [Fact]
    public void Flush_TrailingNewline_IsNotDiscarded()
    {
        // 流以换行结尾：待定换行没有后继字符可供判定，Flush 必须释放而非丢弃。
        var pipeline = new StreamTokenPipeline();
        pipeline.Feed("正文\n").ToList();

        List<StreamSegment> tail = OfType(pipeline.Flush(), StreamSegmentType.Text);

        Assert.Equal("\n", Assert.Single(tail).Payload);
    }

    [Fact]
    public void Reset_ClearsPendingNewlineForNextStream()
    {
        // 待定换行若跨轮残留，会在第二轮正文前凭空插入一个换行片段。
        var pipeline = new StreamTokenPipeline();
        pipeline.Feed("第一轮\n").ToList();
        pipeline.Reset();

        List<StreamSegment> second = OfType(pipeline.Feed("第二轮正文"), StreamSegmentType.Text);

        Assert.Equal("第二轮正文", Assert.Single(second).Payload);
    }

    [Fact]
    public void Flush_OptionSection_MissingDash_FallsBackToRawLine()
    {
        var pipeline = new StreamTokenPipeline();
        pipeline.Feed("正文\n%").ToList();
        pipeline.Feed("\n- 正常选项\n格式异常选项").ToList();
        pipeline.Flush().ToList();

        Assert.Equal(new[] { "正常选项", "格式异常选项" }, pipeline.GetCollectedSuggestions());
    }

    [Fact]
    public void Flush_OptionSection_CapsAtThreeSuggestions()
    {
        var pipeline = new StreamTokenPipeline();
        pipeline.Feed("正文\n%").ToList();
        pipeline.Feed("\n- 一\n- 二\n- 三\n- 四\n- 五").ToList();
        pipeline.Flush().ToList();

        IReadOnlyList<string> suggestions = pipeline.GetCollectedSuggestions();
        Assert.Equal(3, suggestions.Count);
        Assert.Equal(new[] { "一", "二", "三" }, suggestions);
    }

    [Fact]
    public void Flush_NoOptionSection_LeavesSuggestionsEmpty()
    {
        var pipeline = new StreamTokenPipeline();
        pipeline.Feed("普通台词").ToList();
        pipeline.Flush().ToList();

        Assert.Empty(pipeline.GetCollectedSuggestions());
    }

    [Fact]
    public void GetCollectedSuggestions_ReturnsReadOnlySnapshot()
    {
        // AsReadOnly() 包装应返回只读视图：IsReadOnly 为 true
        var pipeline2 = new StreamTokenPipeline();
        pipeline2.Feed("正文\n%").ToList();
        pipeline2.Feed("\n- 甲").ToList();
        pipeline2.Flush().ToList();

        IReadOnlyList<string> snapshot = pipeline2.GetCollectedSuggestions();
        var writable = snapshot as System.Collections.Generic.ICollection<string>;
        Assert.NotNull(writable);
        Assert.True(writable.IsReadOnly);
    }

    // ── 规格场景 3：方括号熔断 ──

    [Fact]
    public void Feed_UnclosedTagExceedingLimit_FlushesAsTextWithoutLosingCharacters()
    {
        // 65 个中文字符，超过 64 字符熔断阈值且始终无 ']'
        string overlong = new string('长', 65);
        List<StreamSegment> segments = RunOne("[这是" + overlong);

        string text = ConcatText(segments);

        // 熔断后内容作为纯文本输出，且不丢字
        Assert.StartsWith("[这是", text);
        Assert.Contains("长", text);
        Assert.DoesNotContain("]", text);
    }

    [Fact]
    public void Feed_UnclosedTagExceedingLimit_DoesNotHangAndKeepsStreaming()
    {
        var pipeline = new StreamTokenPipeline();
        var all = new List<StreamSegment>();
        all.AddRange(pipeline.Feed("[" + new string('x', 70)));
        all.AddRange(pipeline.Feed("后续正常台词"));

        // 熔断后必须继续正常处理后续文本
        Assert.Contains("后续正常台词", ConcatText(all));
    }

    [Fact]
    public void Flush_UnclosedTag_EmitsRemainingAsText()
    {
        List<StreamSegment> segments = Run("[UI:未闭合");

        Assert.Equal("[UI:未闭合", ConcatText(segments));
    }

    [Fact]
    public void Flush_UnclosedShortTag_EmitsAsTextNotAction()
    {
        List<StreamSegment> segments = Run("[UI:");

        Assert.Empty(OfType(segments, StreamSegmentType.Action));
        Assert.Equal("[UI:", ConcatText(segments));
    }

    // ── 规格场景 4：原版情绪码 ──

    [Fact]
    public void Feed_MoodCode_SplitsTextAroundIt()
    {
        List<StreamSegment> segments = RunOne("今天天气真好 $h 哈哈");

        List<StreamSegment> portraits = OfType(segments, StreamSegmentType.Portrait);
        Assert.Single(portraits);
        Assert.Equal("h", portraits[0].Payload);

        Assert.Equal("今天天气真好  哈哈", ConcatText(segments));
    }

    [Theory]
    [InlineData("$h")]
    [InlineData("$s")]
    [InlineData("$u")]
    [InlineData("$l")]
    [InlineData("$a")]
    public void Feed_SingleLetterMoodCodes_AreRecognized(string code)
    {
        List<StreamSegment> portraits = OfType(RunOne($"前{code}后"), StreamSegmentType.Portrait);

        Assert.Single(portraits);
        Assert.Equal(code.Substring(1), portraits[0].Payload);
    }

    [Fact]
    public void Feed_NeutralMoodCode_IsRecognized()
    {
        List<StreamSegment> portraits = OfType(RunOne("前$neutral后"), StreamSegmentType.Portrait);

        Assert.Single(portraits);
        Assert.Equal("neutral", portraits[0].Payload);
    }

    [Fact]
    public void Feed_DigitMoodCode_IsRecognized()
    {
        List<StreamSegment> portraits = OfType(RunOne("前$5后"), StreamSegmentType.Portrait);

        Assert.Single(portraits);
        Assert.Equal("5", portraits[0].Payload);
    }

    [Fact]
    public void Feed_DollarWithoutMoodCode_FallsBackToText()
    {
        // '$' 后跟非情绪码字符（如货币符号用法）时必须回退为文本，不得吞字
        List<StreamSegment> segments = RunOne("价格是$美元");

        Assert.Empty(OfType(segments, StreamSegmentType.Portrait));
        Assert.Equal("价格是$美元", ConcatText(segments));
    }

    [Fact]
    public void Feed_MoodCodeAtChunkEnd_DoesNotLoseDollarSign()
    {
        // '$' 落在 chunk 末尾，窗口不足；补齐后仍应识别为情绪码或至少不丢字
        var pipeline = new StreamTokenPipeline();
        List<StreamSegment> all = new List<StreamSegment>();
        all.AddRange(pipeline.Feed("前面$"));
        all.AddRange(pipeline.Feed("h后面"));
        all.AddRange(pipeline.Flush());

        // 无论是否跨 chunk 识别成功，字符总数不得丢失
        string everything = string.Concat(all.Select(s => s.Payload));
        Assert.Contains("前面", everything);
        Assert.Contains("后面", everything);
    }

    [Fact]
    public void Feed_MoodCodeInsideTagBuffer_IsNotScannedAsMood()
    {
        // 标签缓冲优先：方括号内的 '$' 不得被当作情绪码切走
        List<StreamSegment> actions = OfType(RunOne("[ACTION:EMOTE:HAPPY]"), StreamSegmentType.Action);

        Assert.Single(actions);
        Assert.Equal("ACTION:EMOTE:HAPPY", actions[0].Payload);
    }

    // ── 首 chunk 前缀清洗 ──

    [Fact]
    public void Feed_FirstChunkWithDashPrefix_StripsIt()
    {
        Assert.Equal("你好", ConcatText(Run("- 你好")));
    }

    [Fact]
    public void Feed_DashPrefixOnLaterChunk_IsNotStripped()
    {
        // 仅首 chunk 清洗前缀；后续 chunk 的 '- ' 属于正文字符
        var pipeline = new StreamTokenPipeline();
        pipeline.Feed("第一段").ToList();
        List<StreamSegment> second = pipeline.Feed("- 第二段").ToList();

        Assert.Contains("- 第二段", ConcatText(second));
    }

    [Fact]
    public void Feed_FirstChunkDashPrefix_SplitAcrossLeadingCharOnly()
    {
        // "- " 精确两字符才清洗，"-" 单独不成对时保留
        Assert.Equal("-你好", ConcatText(Run("-你好")));
    }

    // ── 边界与 Reset ──

    [Fact]
    public void Feed_NullOrEmptyChunk_YieldsNothing()
    {
        var pipeline = new StreamTokenPipeline();

        Assert.Empty(pipeline.Feed(null));
        Assert.Empty(pipeline.Feed(""));
    }

    [Fact]
    public void Feed_NullChunk_DoesNotConsumeFirstChunkFlag()
    {
        var pipeline = new StreamTokenPipeline();
        pipeline.Feed(null).ToList();

        // null chunk 不应把 _isFirstChunk 置否
        Assert.Equal("你好", ConcatText(pipeline.Feed("- 你好")));
    }

    [Fact]
    public void Reset_ClearsBufferAndStateForNextStream()
    {
        var pipeline = new StreamTokenPipeline();
        pipeline.Feed("第一轮台词\n%").ToList();
        pipeline.Feed("- 选项甲").ToList();
        pipeline.Flush().ToList();
        Assert.NotEmpty(pipeline.GetCollectedSuggestions());

        pipeline.Reset();

        Assert.Empty(pipeline.GetCollectedSuggestions());
        Assert.Equal("Thinking", StreamingProbeState(pipeline));
    }

    [Fact]
    public void Reset_AllowsSecondStreamToStartWithDashPrefix()
    {
        var pipeline = new StreamTokenPipeline();
        pipeline.Feed("- 第一轮").ToList();
        pipeline.Flush().ToList();

        pipeline.Reset();
        List<StreamSegment> second = pipeline.Feed("- 第二轮").ToList();

        Assert.Equal("第二轮", ConcatText(second));
    }

    [Fact]
    public void Reset_ClearsOptionSectionFlag()
    {
        var pipeline = new StreamTokenPipeline();
        pipeline.Feed("\n%").ToList();
        pipeline.Feed("- 甲").ToList();
        pipeline.Flush().ToList();

        pipeline.Reset();
        List<StreamSegment> second = pipeline.Feed("新一轮正文").ToList();

        // 若选项区标志未复位，第二轮正文会被错误吞掉
        Assert.Equal("新一轮正文", ConcatText(second));
    }

    [Fact]
    public void EmittedSegmentCount_TracksProducedSegments()
    {
        var pipeline = new StreamTokenPipeline();
        int before = pipeline.EmittedSegmentCount;
        pipeline.Feed("一段文本").ToList();

        Assert.True(pipeline.EmittedSegmentCount > before);
    }

    [Fact]
    public void Segment_RecordEquality_ComparesTypeAndPayload()
    {
        var a = new StreamSegment(StreamSegmentType.Text, "hi");
        var b = new StreamSegment(StreamSegmentType.Text, "hi");
        var c = new StreamSegment(StreamSegmentType.Action, "hi");

        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
    }

    /// <summary>读取分流器内部状态，仅用于断言 Reset 的复位效果。</summary>
    private static string StreamingProbeState(StreamTokenPipeline pipeline)
    {
        System.Reflection.FieldInfo field = typeof(StreamTokenPipeline).GetField(
            "_isFirstChunk",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        bool isFirst = (bool)field.GetValue(pipeline);
        return isFirst ? "Thinking" : "Typing";
    }

    // ── VT-STREAM-05：跨 chunk '$' 缓冲与语义情绪词 ──

    [Fact]
    public void Feed_DollarSplitFromSingleLetterCode_AcrossChunks_EmitsPortrait()
    {
        // '$' 落在 chunk 末尾，'h' 在下一 chunk：延迟判定后仍须命中单字母情绪码。
        List<StreamSegment> segments = Run("心情 $", "h 很好");

        List<StreamSegment> portraits = OfType(segments, StreamSegmentType.Portrait);
        Assert.Single(portraits);
        Assert.Equal("h", portraits[0].Payload);
        Assert.Equal("心情  很好", ConcatText(segments));
    }

    [Fact]
    public void Feed_DollarSplitFromSemanticWord_AcrossChunks_EmitsPortrait()
    {
        List<StreamSegment> segments = Run("He said $", "happy loudly");

        List<StreamSegment> portraits = OfType(segments, StreamSegmentType.Portrait);
        Assert.Single(portraits);
        Assert.Equal("happy", portraits[0].Payload);
        Assert.Equal("He said  loudly", ConcatText(segments));
    }

    [Fact]
    public void Feed_SemanticMoodWords_InSingleChunk_EmitPortrait()
    {
        Assert.Equal("happy", OfType(RunOne("$happy"), StreamSegmentType.Portrait).Single().Payload);
        Assert.Equal("sad", OfType(RunOne("$sad"), StreamSegmentType.Portrait).Single().Payload);
        Assert.Equal("angry", OfType(RunOne("$angry"), StreamSegmentType.Portrait).Single().Payload);
        Assert.Equal("surprised", OfType(RunOne("$surprised"), StreamSegmentType.Portrait).Single().Payload);
        Assert.Equal("love", OfType(RunOne("$love"), StreamSegmentType.Portrait).Single().Payload);
    }

    [Fact]
    public void Feed_SemanticMoodWord_IsNotSplitIntoSingleLetterPrefix()
    {
        // 回归：'$happy' 不得被切成 '$h' + "appy"。
        List<StreamSegment> segments = RunOne("$happy");

        Assert.Empty(OfType(segments, StreamSegmentType.Text));
        Assert.Equal("happy", OfType(segments, StreamSegmentType.Portrait).Single().Payload);
    }

    [Fact]
    public void Feed_NonEmotionDollarWord_IsPassedThroughAsText()
    {
        // '$house' 的字母段 'house' 不在情绪词表内，必须整段放行为正文。
        List<StreamSegment> segments = RunOne("a $house");

        Assert.Empty(OfType(segments, StreamSegmentType.Portrait));
        Assert.Equal("a $house", ConcatText(segments));
    }

    [Fact]
    public void Feed_NonEmotionDollarWord_SplitAcrossChunks_StillPassesThrough()
    {
        List<StreamSegment> segments = Run("a $", "house");

        Assert.Empty(OfType(segments, StreamSegmentType.Portrait));
        Assert.Equal("a $house", ConcatText(segments));
    }

    [Fact]
    public void Feed_SingleLetterCode_StillEmitsPortraitWhenFollowedByPunctuation()
    {
        List<StreamSegment> segments = RunOne("$h, hello");

        Assert.Equal("h", OfType(segments, StreamSegmentType.Portrait).Single().Payload);
        Assert.Equal(", hello", ConcatText(segments));
    }

    [Fact]
    public void Flush_DollarAtEndOfStream_EmitsItAsText()
    {
        // 流以 '$' 结尾：待定 '$' 永远等不到后继字符，Flush 必须按正文字符释放，不得丢字。
        List<StreamSegment> segments = Run("价格 $");

        Assert.Empty(OfType(segments, StreamSegmentType.Portrait));
        Assert.Equal("价格 $", ConcatText(segments));
    }

    [Fact]
    public void Reset_ClearsPendingDollarFlag()
    {
        var pipeline = new StreamTokenPipeline();
        pipeline.Feed("第一轮 $").ToList();

        pipeline.Reset();
        List<StreamSegment> second = pipeline.Feed("第二轮").ToList();

        // 若待定 '$' 未随 Reset 清除，第二轮的 '第' 会被误判为情绪码前缀而吞掉。
        Assert.Empty(OfType(second, StreamSegmentType.Portrait));
        Assert.Equal("第二轮", ConcatText(second));
    }
}
