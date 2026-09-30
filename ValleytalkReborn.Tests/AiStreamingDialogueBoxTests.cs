// AiStreamingDialogueBoxTests.cs
// VT-STREAM-01-CoreDialogueBox: 流式对白框的标点节奏、流式吸附与跳过行为测试。
//
// 无头环境要点（均已实测确认，非假设）：
//   1) Game1.uiViewport 是 xTile.Dimensions.Rectangle 结构体，永不为 null；
//      无图形上下文时 Height 为 0，构造器据此降级到 720p 标称坐标。
//   2) base DialogueBox(x, y, w, h) 构造函数第一行就读 Game1.options.SnappyMenus，
//      而 Game1.options 走 game1.instanceOptions —— game1 为 null 时必然 NRE。
//      因此本文件安装 Options 垫片后再构造对话框。
//   3) Game1.content 为 null 时原版 Dialogue 构造函数会在
//      TranslateArraysOfStrings 内部 NRE；AiStreamingDialogueBox 据此改用
//      FormatterServices 无初始化垫片，故测试无需启动内容管线。
//   4) Options 垫片必须把 snappyMenus 置 false：SnappyMenus getter 形如
//      snappyMenus && gamepadControls && Game1.input.GetMouseState()...，
//      置 false 后短路，不会触碰未初始化的输入设备。
//
// HAZARD: Game1.game1 / Game1.options 是进程级静态。任何翻转它们的行为都会
// 污染同进程内其他用例，因此本类固定为非并行集合（见 TestCollections）。

using System;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using StardewValley;
using StardewValley.Menus;
using ValleytalkReborn.UI;
using Xunit;

namespace ValleytalkReborn.Tests;

[Collection("EngineStaticStateCollection")]
public class AiStreamingDialogueBoxTests : IDisposable
{
    private static readonly FieldInfo Game1InstanceField =
        typeof(Game1).GetField("game1", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

    private static readonly FieldInfo Game1OptionsField =
        typeof(Game1).GetField("instanceOptions", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

    private static readonly FieldInfo UiViewportField =
        typeof(Game1).GetField("uiViewport", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

    /// <summary>基类 public friendshipJewel 字段（类型为 xTile.Dimensions.Rectangle）。</summary>
    private static readonly FieldInfo FriendshipJewelField =
        typeof(DialogueBox).GetField("friendshipJewel", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

    private readonly object _previousGame1;
    private readonly object _previousOptions;
    private readonly object _previousViewport;

    public AiStreamingDialogueBoxTests()
    {
        _previousGame1 = Game1InstanceField?.GetValue(null);
        _previousOptions = _previousGame1 == null ? null : Game1OptionsField?.GetValue(_previousGame1);
        _previousViewport = UiViewportField?.GetValue(null);

        InstallHeadlessShims();
    }

    public void Dispose()
    {
        if (_previousGame1 == null)
        {
            Game1InstanceField?.SetValue(null, null);
        }
        else
        {
            Game1OptionsField?.SetValue(_previousGame1, _previousOptions);
            Game1InstanceField?.SetValue(null, _previousGame1);
        }

        UiViewportField?.SetValue(null, _previousViewport);
    }

    /// <summary>
    /// 安装最小 Options 垫片：让 base DialogueBox 构造函数与 update 的
    /// Game1.options.dialogueTyping 门禁可求值，且不触碰图形/输入子系统。
    /// </summary>
    private static void InstallHeadlessShims()
    {
        // Game1.sounds 是**静态** ISoundsHelper 字段（经 Game1.playSound 读取）。
        // 无头环境无音频实现，注入无操作代理，使音效调用安全返回 false。
        SetStaticField(typeof(Game1), "sounds", CreateNullSounds());

        var game1 = (Game1)FormatterServices.GetUninitializedObject(typeof(Game1));
        Game1InstanceField?.SetValue(null, game1);

        var options = (Options)FormatterServices.GetUninitializedObject(typeof(Options));
        SetField(options, "snappyMenus", false);
        SetField(options, "gamepadControls", false);
        SetField(options, "dialogueTyping", false);
        SetField(options, "showPortraits", false);

        SetInstanceField(game1, "instanceOptions", options);
        Game1InstanceField?.SetValue(null, game1);

        // 视口高度置零（Location 0,0 + Size 1280x0），强制走 720p 降级分支。
        var uiViewportType = typeof(Game1).GetField("uiViewport", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            ?.FieldType;
        object zeroViewport = uiViewportType == null
            ? null
            : Activator.CreateInstance(uiViewportType,
                Activator.CreateInstance(uiViewportType.GetField("Location")!.FieldType, 0, 0),
                Activator.CreateInstance(uiViewportType.GetField("Size")!.FieldType, 1280, 0));
        SetStaticField(typeof(Game1), "uiViewport", zeroViewport);
    }

    /// <summary>
    /// 构造 ISoundsHelper 的无操作代理。代理类型由运行时生成，
    /// 静态上只需满足接口即可。
    /// </summary>
    private static object CreateNullSounds()
    {
        Type soundsType = typeof(Game1)
            .GetField("sounds", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .FieldType;

        // DispatchProxy.Create<T, TProxy>() 只有泛型重载；测试项目未引用 MonoGame，
        // 无法静态书写 T，故经反射调用泛型定义。
        MethodInfo generic = typeof(DispatchProxy)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .First(m => m.Name == "Create" && m.IsGenericMethodDefinition)
            .MakeGenericMethod(soundsType, typeof(NullSoundsHelper));

        return generic.Invoke(null, null);
    }

    private static void SetField(object instance, string name, object value)
        => instance.GetType()
            .GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            ?.SetValue(instance, value);

    private static void SetInstanceField(object instance, string name, object value)
        => SetField(instance, name, value);

    private static void SetStaticField(Type type, string name, object value)
        => type.GetField(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            ?.SetValue(null, value);

    private static AiStreamingDialogueBox NewBox(string initialText = "")
    {
        // Game1.content 保持 null，驱动 Dialogue 无初始化垫片分支。
        Assert.Null(Game1.content);
        return new AiStreamingDialogueBox(new NPC(), initialText);
    }

    #region 构造与无头边界

    [Fact]
    public void Constructor_WithNullSpeaker_DoesNotBindCharacterDialogue()
    {
        AiStreamingDialogueBox box = new(null, "");

        Assert.Null(box.characterDialogue);
        Assert.Equal(1200, box.width);
        Assert.Equal(384, box.height);
    }

    [Fact]
    public void Constructor_HeadlessViewport_UsesFallbackLayoutWithoutThrowing()
    {
        // uiViewport.Height == 0 -> 降级 720 -> y = 720 - 384 - 64 = 272
        AiStreamingDialogueBox box = NewBox();

        Assert.Equal(272, box.y);
    }

    [Fact]
    public void Constructor_NullSpeaker_LaysOutNonPortraitWidth()
    {
        AiStreamingDialogueBox box = new(null, "");

        // 无 characterDialogue -> isPortraitBox() 为 false -> 文字区宽度 = width - 64
        Assert.False(box.isPortraitBox());
    }

    [Fact]
    public void Constructor_WithSpeaker_UsesUninitializedDialogueShimWhenContentIsNull()
    {
        AiStreamingDialogueBox box = NewBox();

        Assert.NotNull(box.characterDialogue);
        Assert.NotNull(box.characterDialogue.speaker);
    }

    [Fact]
    public void Constructor_SetsFriendshipJewelToVanillaSize()
    {
        AiStreamingDialogueBox box = NewBox();

        // friendshipJewel 是 xTile.Dimensions.Rectangle，测试项目未引用 xTile，
        // 故经反射读取其几何属性。
        (int X, int Y, int W, int H) = ReadFriendshipJewel(box);

        Assert.Equal(44, W);
        Assert.Equal(44, H);
        Assert.Equal(box.x + box.width - 64, X);
        Assert.Equal(box.y + 256, Y);
    }

    /// <summary>
    /// 读取基类 friendshipJewel（XNA Rectangle）的 X/Y/Width/Height 字段。
    /// 测试项目未引用 MonoGame.Framework，故只能经反射取值。
    /// </summary>
    private static (int X, int Y, int W, int H) ReadFriendshipJewel(AiStreamingDialogueBox box)
    {
        object rect = FriendshipJewelField.GetValue(box);
        Type rectType = rect.GetType();
        return (
            (int)rectType.GetField("X").GetValue(rect),
            (int)rectType.GetField("Y").GetValue(rect),
            (int)rectType.GetField("Width").GetValue(rect),
            (int)rectType.GetField("Height").GetValue(rect));
    }

    [Fact]
    public void Constructor_EmptyText_StartsInThinkingState()
    {
        Assert.Equal(StreamingDialogueState.Thinking, NewBox("").State);
    }

    [Fact]
    public void Constructor_WithInitialText_StartsInTypingState()
    {
        AiStreamingDialogueBox box = NewBox("Hello");

        Assert.Equal(StreamingDialogueState.Typing, box.State);
        Assert.Equal("Hello", box.DisplayedPageText);
    }

    #endregion

    #region 标点节奏与连续标点折叠

    [Fact]
    public void ComputeDelay_NonPunctuation_ReturnsBaseInterval()
    {
        Assert.Equal(35, AiStreamingDialogueBox.ComputeDelayMs("abc", 1));
    }

    [Fact]
    public void ComputeDelay_LightPunctuation_AddsLightPause()
    {
        // "a, b"：逗号后接空格，非折叠 -> 35 + 140
        Assert.Equal(175, AiStreamingDialogueBox.ComputeDelayMs("a, b", 2));
    }

    [Fact]
    public void ComputeDelay_LongTerminator_AddsLongPause()
    {
        // "Done."：句点位于末尾 -> 35 + 380
        Assert.Equal(415, AiStreamingDialogueBox.ComputeDelayMs("Done.", 5));
    }

    [Fact]
    public void ComputeDelay_Newline_AddsLongestPause()
    {
        Assert.Equal(450, AiStreamingDialogueBox.ComputeDelayMs("a\nb", 2));
    }

    [Fact]
    public void ComputeDelay_ConsecutivePunctuation_FoldsIntoSinglePause()
    {
        // "？？？"：三个问号连续 -> 仅末字注入停顿
        string text = "？？？";

        Assert.Equal(35, AiStreamingDialogueBox.ComputeDelayMs(text, 1));
        Assert.Equal(35, AiStreamingDialogueBox.ComputeDelayMs(text, 2));
        Assert.Equal(415, AiStreamingDialogueBox.ComputeDelayMs(text, 3));
    }

    [Fact]
    public void ComputeDelay_TripleDot_FoldsIntoSingleLongPause()
    {
        // "Wait..." -> 两个点折叠，第三个触发一次长停顿
        string text = "Wait...";

        Assert.Equal(35, AiStreamingDialogueBox.ComputeDelayMs(text, 5));
        Assert.Equal(35, AiStreamingDialogueBox.ComputeDelayMs(text, 6));
        Assert.Equal(415, AiStreamingDialogueBox.ComputeDelayMs(text, 7));
    }

    [Fact]
    public void ComputeDelay_MixedPunctuationRun_OnlyLastCharPauses()
    {
        // "What?!" -> ? 与 ! 互为连续标点，仅 ! 触发长停顿
        string text = "What?!";

        Assert.Equal(35, AiStreamingDialogueBox.ComputeDelayMs(text, 5));
        Assert.Equal(415, AiStreamingDialogueBox.ComputeDelayMs(text, 6));
    }

    [Fact]
    public void ComputeDelay_DashRun_FoldsWithDashPause()
    {
        // "——" -> 首字折叠，末字触发 320ms 延宕停顿
        string text = "——";

        Assert.Equal(35, AiStreamingDialogueBox.ComputeDelayMs(text, 1));
        Assert.Equal(355, AiStreamingDialogueBox.ComputeDelayMs(text, 2));
    }

    [Fact]
    public void ComputeDelay_OutOfRange_ReturnsBaseInterval()
    {
        Assert.Equal(35, AiStreamingDialogueBox.ComputeDelayMs("abc", 0));
        Assert.Equal(35, AiStreamingDialogueBox.ComputeDelayMs("abc", 4));
        Assert.Equal(35, AiStreamingDialogueBox.ComputeDelayMs(null, 1));
    }

    #endregion

    #region SetContent / AppendContent

    [Fact]
    public void SetContent_ReplacesTextAndResetsCursor()
    {
        AiStreamingDialogueBox box = NewBox("first");
        box.CharacterIndex.ShouldBeZero();

        box.SetContent("second", true);

        Assert.Equal("second", box.DisplayedPageText);
        Assert.Equal(0, box.CharacterIndex);
        Assert.Equal(StreamingDialogueState.Typing, box.State);
    }

    [Fact]
    public void SetContent_EmptyText_EntersThinkingState()
    {
        AiStreamingDialogueBox box = NewBox("something");

        box.SetContent("");

        Assert.Equal(StreamingDialogueState.Thinking, box.State);
        Assert.Equal(string.Empty, box.DisplayedPageText);
    }

    [Fact]
    public void SetContent_StripsControlTags()
    {
        AiStreamingDialogueBox box = NewBox();

        box.SetContent("He{llo} there");

        Assert.Equal("Hello there", box.DisplayedPageText);
    }

    [Fact]
    public void AppendContent_FirstChunk_LeavesThinkingState()
    {
        AiStreamingDialogueBox box = NewBox();
        Assert.Equal(StreamingDialogueState.Thinking, box.State);

        box.AppendContent("Hi", false);

        Assert.Equal(StreamingDialogueState.Typing, box.State);
        Assert.Equal("Hi", box.DisplayedPageText);
    }

    [Fact]
    public void AppendContent_AccumulatesChunks()
    {
        AiStreamingDialogueBox box = NewBox();

        box.AppendContent("Hel", false);
        box.AppendContent("lo ", false);
        box.AppendContent("world", true);

        Assert.Equal("Hello world", box.DisplayedPageText);
    }

    [Fact]
    public void AppendContent_EmptyChunk_IsNoOp()
    {
        AiStreamingDialogueBox box = NewBox("abc");

        box.AppendContent("", false);

        Assert.Equal("abc", box.DisplayedPageText);
    }

    [Fact]
    public void AppendContent_CursorNeverExceedsBufferLength()
    {
        AiStreamingDialogueBox box = NewBox("longer text");

        // 缩短缓冲后游标不得越界
        box.SetContent("ab", false);
        box.CharacterIndex.ShouldBeZero();

        box.AppendContent("cd", false);
        Assert.Equal(4, box.DisplayedPageText.Length);
        Assert.True(box.CharacterIndex <= box.DisplayedPageText.Length);
    }

    [Fact]
    public void AppendContent_FastForward_KeepsCursorSnappedToNewTail()
    {
        AiStreamingDialogueBox box = NewBox("Hel");
        box.receiveLeftClick(0, 0);
        Assert.True(box.IsFastForwardActive);
        Assert.Equal(3, box.CharacterIndex);

        box.AppendContent("lo world", false);

        Assert.Equal("Hello world", box.DisplayedPageText);
        Assert.Equal(11, box.CharacterIndex);
    }

    [Fact]
    public void AppendContent_StreamCompleteWithFastForward_EntersCompleteState()
    {
        AiStreamingDialogueBox box = NewBox("Hi");
        box.receiveLeftClick(0, 0);

        box.AppendContent(" there", true);

        Assert.Equal(StreamingDialogueState.Complete, box.State);
        Assert.Equal(8, box.CharacterIndex);
    }

    [Fact]
    public void AppendContent_StreamCompleteWithoutFastForward_StaysTyping()
    {
        AiStreamingDialogueBox box = NewBox("Hi");

        box.AppendContent(" there", true);

        // 游标尚未揭示到末尾，不得提前进入 Complete
        Assert.Equal(StreamingDialogueState.Typing, box.State);
        Assert.Equal(0, box.CharacterIndex);
    }

    [Fact]
    public void SetFaulted_ClearsTextAndEntersFaultedState()
    {
        AiStreamingDialogueBox box = NewBox("partial");

        box.SetFaulted("stream failed");

        Assert.Equal(StreamingDialogueState.Faulted, box.State);
        Assert.Equal(string.Empty, box.DisplayedPageText);
        Assert.Equal(0, box.CharacterIndex);
    }

    [Fact]
    public void AppendContent_AfterFaulted_IsIgnored()
    {
        AiStreamingDialogueBox box = NewBox("partial");
        box.SetFaulted("stream failed");

        box.AppendContent("more", true);

        Assert.Equal(StreamingDialogueState.Faulted, box.State);
        Assert.Equal(string.Empty, box.DisplayedPageText);
    }

    #endregion

    #region 跳过行为

    [Fact]
    public void ReceiveLeftClick_InTyping_SnapsCursorToEndOfAvailableText()
    {
        AiStreamingDialogueBox box = NewBox("Hello streaming world");

        box.receiveLeftClick(0, 0);

        Assert.Equal(21, box.CharacterIndex);
        Assert.Equal(box.DisplayedPageText.Length, box.CharacterIndex);
    }

    [Fact]
    public void ReceiveLeftClick_InTyping_ActivatesFastForwardFlag()
    {
        AiStreamingDialogueBox box = NewBox("abc");

        box.receiveLeftClick(0, 0);

        Assert.True(box.IsFastForwardActive);
    }

    [Fact]
    public void ReceiveLeftClick_InTypingWithStreamComplete_EntersCompleteState()
    {
        AiStreamingDialogueBox box = NewBox("abc");
        box.SetContent("abc", true);

        box.receiveLeftClick(0, 0);

        Assert.Equal(StreamingDialogueState.Complete, box.State);
    }

    [Fact]
    public void ReceiveLeftClick_InThinking_DoesNotAdvanceCursor()
    {
        AiStreamingDialogueBox box = NewBox("");

        box.receiveLeftClick(0, 0);

        Assert.Equal(StreamingDialogueState.Thinking, box.State);
        Assert.Equal(0, box.CharacterIndex);
    }

    #endregion

    #region 显式 '#' 分页

    [Fact]
    public void AppendContent_ExplicitHash_SealsCurrentPageAndQueuesRemainder()
    {
        AiStreamingDialogueBox box = NewBox();

        box.AppendContent("第1页#第2页", false);

        // '#' 之前的文本留在当前页并封口，之后的内容压入 backlog。
        Assert.Equal("第1页", box.DisplayedPageText);
        Assert.True(box.IsCurrentPageSealed);
        Assert.Equal(1, box.PendingPageCount);
        Assert.Equal("第1页 第2页", box.GetFullDialogueText());
    }

    [Fact]
    public void AppendContent_ExplicitHash_EntersWaitingForPageTurnAfterPageIsDrained()
    {
        AiStreamingDialogueBox box = NewBox();

        box.AppendContent("第1页#第2页", true);
        Assert.Equal(StreamingDialogueState.Typing, box.State);

        // 打完当前页 -> 等待翻页，而不是直接 Complete。
        box.receiveLeftClick(0, 0);

        Assert.Equal(StreamingDialogueState.WaitingForPageTurn, box.State);
    }

    [Fact]
    public void ReceiveLeftClick_InWaitingForPageTurn_DequeuesNextPageAndResumesTyping()
    {
        AiStreamingDialogueBox box = NewBox();
        box.AppendContent("第1页#第2页", true);
        box.receiveLeftClick(0, 0);
        Assert.Equal(StreamingDialogueState.WaitingForPageTurn, box.State);

        box.receiveLeftClick(0, 0);

        Assert.Equal("第2页", box.DisplayedPageText);
        Assert.Equal(StreamingDialogueState.Typing, box.State);
        Assert.Equal(0, box.CharacterIndex);
        Assert.False(box.IsCurrentPageSealed);
        Assert.Equal(0, box.PendingPageCount);
    }

    [Fact]
    public void ReceiveLeftClick_InWaitingForPageTurn_ThenFinishLastPage_EntersComplete()
    {
        AiStreamingDialogueBox box = NewBox();
        box.AppendContent("第1页#第2页", true);
        box.receiveLeftClick(0, 0);
        box.receiveLeftClick(0, 0);

        Assert.Equal(StreamingDialogueState.Typing, box.State);

        // 末页快进拉满 -> 无后续页 -> Complete。
        box.receiveLeftClick(0, 0);

        Assert.Equal(StreamingDialogueState.Complete, box.State);
    }

    [Fact]
    public void ReceiveLeftClick_EmptyBacklogAtPageTurn_CollapsesToComplete()
    {
        // RECOVERABLE 路径：'#' 结尾使当前页封口但 backlog 为空，
        // 打完本页后进入等待翻页，此时翻页必须收束为 Complete 而非死锁。
        AiStreamingDialogueBox box = NewBox();
        box.AppendContent("只有一页#", true);

        box.receiveLeftClick(0, 0);
        Assert.Equal(StreamingDialogueState.WaitingForPageTurn, box.State);
        Assert.Equal(0, box.PendingPageCount);

        box.receiveLeftClick(0, 0);

        Assert.Equal(StreamingDialogueState.Complete, box.State);
    }

    #endregion

    #region 高度溢出自动分页

    /// <summary>
    /// 构造必然超过 MaxPageHeight 的长文本：无头环境下 SpriteText 单行高 48，
    /// 400 个汉字在 752 宽下实测高度 20028，远超 200。
    /// </summary>
    private static string VeryLongText(string marker)
        => string.Concat(Enumerable.Repeat(marker, 400));

    [Fact]
    public void AppendContent_HeightOverflow_SealsPageAtMaxHeightAndQueuesRemainder()
    {
        AiStreamingDialogueBox box = NewBox();
        string text = VeryLongText("字");

        box.AppendContent(text, false);

        Assert.True(box.IsCurrentPageSealed);
        Assert.True(box.DisplayedPageText.Length < text.Length,
            "超长文本必须在 200px 高度处截断，首屏不得吞下全文。");
        Assert.True(box.PendingPageCount > 0, "溢出部分必须压入 backlog 等待翻页。");
    }

    [Fact]
    public void AppendContent_HeightOverflow_DoesNotSplitAsciiWords()
    {
        AiStreamingDialogueBox box = NewBox();
        string text = string.Join(" ", Enumerable.Repeat("hello", 2000));

        box.AppendContent(text, false);

        // 断行必须落在空格处：末字符不得是单词内部字符。
        Assert.True(box.IsCurrentPageSealed);
        char last = box.DisplayedPageText[box.DisplayedPageText.Length - 1];
        Assert.True(char.IsWhiteSpace(last) || !char.IsLetter(last),
            $"禁止从 ASCII 单词中间断行，实际末字符 '{last}'。");
    }

    [Fact]
    public void AppendContent_HeightOverflow_EntersWaitingForPageTurnAfterDrain()
    {
        AiStreamingDialogueBox box = NewBox();
        box.AppendContent(VeryLongText("字"), true);

        box.receiveLeftClick(0, 0);

        Assert.Equal(StreamingDialogueState.WaitingForPageTurn, box.State);
    }

    [Fact]
    public void AppendContent_PageBudgetExhausted_TruncatesWithMarker()
    {
        // BOUNDARY 路径：显式 '#' 撑破 10 页上限时熔断，末页附加 "..."。
        AiStreamingDialogueBox box = NewBox();

        box.AppendContent(string.Join("#", Enumerable.Repeat("页", 15)), false);

        // 当前页 + backlog 恰好封顶 10 页，第 11 页起被丢弃。
        Assert.Equal(9, box.PendingPageCount);
        Assert.Contains("...", box.GetFullDialogueText());
        Assert.Equal(10, box.GetFullDialogueText().Split(new[] { '页' }, StringSplitOptions.RemoveEmptyEntries).Length);
    }

    [Fact]
    public void AppendContent_AfterPageBudgetExhausted_DropsFurtherText()
    {
        AiStreamingDialogueBox box = NewBox();
        box.AppendContent(string.Join("#", Enumerable.Repeat("页", 15)), false);

        string afterTruncation = box.GetFullDialogueText();

        box.AppendContent("#" + string.Join("#", Enumerable.Repeat("新", 10)), false);

        // 熔断后不得再吞字，也不得重复追加截断标记。
        Assert.Equal(afterTruncation, box.GetFullDialogueText());
    }

    [Fact]
    public void AppendContent_LeadingHash_StillReachesPendingPageInsteadOfStalling()
    {
        // 首字符即 '#'：当前页为空、全量落在 backlog。此时不得停在 Thinking，
        // 否则玩家永远等不到第一次翻页。
        AiStreamingDialogueBox box = NewBox();

        box.AppendContent("#后置内容", false);

        Assert.Equal(StreamingDialogueState.WaitingForPageTurn, box.State);
        Assert.Equal(1, box.PendingPageCount);

        box.receiveLeftClick(0, 0);

        Assert.Equal("后置内容", box.DisplayedPageText);
        Assert.Equal(StreamingDialogueState.Typing, box.State);
    }

    [Fact]
    public void SetContent_LeadingHash_DoesNotStallInThinking()
    {
        AiStreamingDialogueBox box = NewBox("初始");

        box.SetContent("#后置内容", true);

        Assert.Equal(StreamingDialogueState.WaitingForPageTurn, box.State);
        box.receiveLeftClick(0, 0);
        Assert.Equal("后置内容", box.DisplayedPageText);
    }

    #endregion

    #region GetFullDialogueText / SetEmotion

    [Fact]
    public void GetFullDialogueText_JoinsHistoryCurrentAndBacklogAcrossPages()
    {
        AiStreamingDialogueBox box = NewBox();
        box.AppendContent("第一段#第二段#第三段", true);

        // 尚未翻页：当前页 + backlog 拼接即为全文本。
        Assert.Equal("第一段 第二段 第三段", box.GetFullDialogueText());

        // 点击序列：拉满第 1 页 -> 翻到第 2 页 -> 拉满第 2 页 -> 翻到第 3 页。
        box.receiveLeftClick(0, 0);
        Assert.Equal(StreamingDialogueState.WaitingForPageTurn, box.State);

        box.receiveLeftClick(0, 0);
        Assert.Equal("第二段", box.DisplayedPageText);

        box.receiveLeftClick(0, 0);
        Assert.Equal(StreamingDialogueState.WaitingForPageTurn, box.State);

        box.receiveLeftClick(0, 0);

        // 两页已归档，当前页为第三段。
        Assert.Equal("第三段", box.DisplayedPageText);
        Assert.Equal("第一段 第二段 第三段", box.GetFullDialogueText());
    }

    [Fact]
    public void GetFullDialogueText_EmptyStream_ReturnsEmptyString()
    {
        Assert.Equal(string.Empty, NewBox().GetFullDialogueText());
    }

    [Fact]
    public void SetEmotion_AddsDollarPrefix()
    {
        AiStreamingDialogueBox box = NewBox();

        box.SetEmotion("happy");

        Assert.Equal("$happy", box.characterDialogue.CurrentEmotion);
    }

    [Fact]
    public void SetEmotion_KeepsExistingDollarPrefix()
    {
        AiStreamingDialogueBox box = NewBox();

        box.SetEmotion("$sad");

        Assert.Equal("$sad", box.characterDialogue.CurrentEmotion);
    }

    [Fact]
    public void SetEmotion_UnknownCode_FallsBackToDefaultWithoutThrowing()
    {
        // RECOVERABLE 路径：未知码必须降级，不得阻断渲染。
        AiStreamingDialogueBox box = NewBox();

        box.SetEmotion("!!bogus!!");

        Assert.Equal("$0", box.characterDialogue.CurrentEmotion);
    }

    [Fact]
    public void SetEmotion_BlankOrNullSpeaker_IsIgnored()
    {
        AiStreamingDialogueBox withSpeaker = NewBox();
        string before = withSpeaker.characterDialogue.CurrentEmotion;

        withSpeaker.SetEmotion("   ");
        withSpeaker.SetEmotion(null);

        // 空白/空输入不得改动对白框表情（无头垫片的默认值恒为 $neutral）。
        Assert.Equal(before, withSpeaker.characterDialogue.CurrentEmotion);

        AiStreamingDialogueBox withoutSpeaker = new(null, "");
        withoutSpeaker.SetEmotion("happy");
        Assert.Null(withoutSpeaker.characterDialogue);
    }

    #endregion
}

/// <summary>xUnit 断言的零值简写，避免重复样板。</summary>
internal static class AiStreamingDialogueBoxAssertExtensions
{
    internal static void ShouldBeZero(this int actual) => Assert.Equal(0, actual);
}

/// <summary>
/// 无操作音效桩：Game1.playSound 委托给 game1.sounds.PlayLocal，
/// 无头环境下不存在真实音频实现，注入本桩使音效调用安全返回。
/// 依赖 DispatchProxy 而非手写实现，避免为 ISoundsHelper 的全部成员（含需要
/// 引用 MonoGame 的 Vector2/SoundContext 签名）编写样板。
/// </summary>
internal class NullSoundsHelper : DispatchProxy
{
    protected override object Invoke(MethodInfo targetMethod, object[] args)
    {
        if (targetMethod.ReturnType == typeof(void))
            return null;

        if (targetMethod.ReturnType == typeof(bool))
            return false;

        if (targetMethod.ReturnType.IsByRef)
        {
            // out ICue / out IAudioSource：交出同一个无操作桩实例
            Type elementType = targetMethod.ReturnType.GetElementType();
            args[Array.IndexOf(targetMethod.GetParameters(), targetMethod.ReturnParameter)] =
                Create(elementType);
            return null;
        }

        return targetMethod.ReturnType.IsValueType
            ? Activator.CreateInstance(targetMethod.ReturnType)
            : null;
    }

    private static object Create(Type type)
        => type.GetConstructor(Type.EmptyTypes) != null
            ? Activator.CreateInstance(type)
            : CreateUninitialized(type);

    private static object CreateUninitialized(Type type)
        => FormatterServices.GetUninitializedObject(
            type.GetInterfaces().FirstOrDefault() ?? type);
}
