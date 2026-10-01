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
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using StardewValley;
using StardewValley.Menus;
using ValleytalkReborn;
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

    private static readonly FieldInfo Game1PlayerField =
        typeof(Game1).GetField("_player", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

    /// <summary>基类 public friendshipJewel 字段（类型为 xTile.Dimensions.Rectangle）。</summary>
    private static readonly FieldInfo FriendshipJewelField =
        typeof(DialogueBox).GetField("friendshipJewel", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

    private readonly object _previousGame1;
    private readonly object _previousOptions;
    private readonly object _previousViewport;
    private readonly object _previousPlayer;

    public AiStreamingDialogueBoxTests()
    {
        _previousGame1 = Game1InstanceField?.GetValue(null);
        _previousOptions = _previousGame1 == null ? null : Game1OptionsField?.GetValue(_previousGame1);
        _previousViewport = UiViewportField?.GetValue(null);
        _previousPlayer = Game1PlayerField?.GetValue(null);

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
        Game1PlayerField?.SetValue(null, _previousPlayer);
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

    /// <summary>翻转 Game1.options.showPortraits，驱动 isPortraitBox() 的原版门禁。</summary>
    private static void SetShowPortraits(bool value)
    {
        object game1 = Game1InstanceField?.GetValue(null);
        object options = game1 == null ? null : Game1OptionsField?.GetValue(game1);
        SetField(options, "showPortraits", value);
    }

    /// <summary>
    /// 给 NPC 装上非空 Portrait 贴图占位。isPortraitBox() 只判空不触碰纹理内容，
    /// 而测试项目未引用 MonoGame.Framework，故经反射取类型并造无初始化实例。
    /// </summary>
    private static void GiveNpcPortrait(NPC npc)
    {
        // Texture2D 定义在 MonoGame.Framework 而非 Stardew Valley 程序集里，
        // 故经 Game1.mouseCursors 的声明类型直接取到该 Type 实例。
        // NPC.Portrait 在 1.6 是可写属性而非字段，故走 PropertyInfo。
        Type textureType = typeof(Game1)
            .GetField("mouseCursors", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .FieldType;

        typeof(NPC)
            .GetProperty("Portrait", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .SetValue(npc, FormatterServices.GetUninitializedObject(textureType));
    }

    /// <summary>
    /// 装上最小 Farmer 垫片，让 Close() 能走到「强制释放玩家移动」为止。
    /// 注意：forceCanMove() 内部还链着多个 NetField 支撑的 Player 状态，
    /// 无头垫片无法全部补齐，尾段的空引用由 <see cref="IgnoringHeadlessPlayerRelease"/> 吸收。
    /// </summary>
    private static void InstallFarmerShim()
    {
        var farmer = (Farmer)FormatterServices.GetUninitializedObject(typeof(Farmer));
        SetField(farmer, "movementDirections", new List<int>());

        // Character.Sprite 走 NetField 字段链，其 setter 在无头垫片上会空引用。
        // 故直接装配 NetField.Value，使 FarmerSprite getter 返回可写实例。
        Type farmerSpriteType = typeof(Farmer)
            .GetProperty("FarmerSprite", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
            .PropertyType;
        FieldInfo spriteRefField = typeof(StardewValley.Character)
            .GetField("sprite", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        object spriteRef = Activator.CreateInstance(spriteRefField.FieldType);
        spriteRefField.FieldType.GetProperty("Value")
            .SetValue(spriteRef, FormatterServices.GetUninitializedObject(farmerSpriteType));
        spriteRefField.SetValue(farmer, spriteRef);

        SetStaticField(typeof(Game1), "_player", farmer);
    }

    /// <summary>
    /// 执行一次「会走到 Close() 的强制释放玩家移动」序列，并吸收无头垫片在
    /// Farmer.forceCanMove() 尾段（NetField 支撑的 UsingTool / CurrentTool 等）的空引用。
    /// 释放序列本身不在本工单的无头验证范围内，此处只保证取消路由可被断言。
    /// </summary>
    private static void IgnoringHeadlessPlayerRelease(Action action)
    {
        try
        {
            action();
        }
        catch (NullReferenceException)
        {
            // 无头环境无法补齐 Farmer 的 NetField 状态，止步于 forceCanMove() 尾段。
        }
    }

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
    /// 400 个汉字在无立绘布局实测宽度 width - 16 = 1184 下高度 19056，远超 200。
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
    public void SetEmotion_MapsSemanticWordToVanillaPortraitCode()
    {
        // VT-STREAM-05：语义词归一到原版立绘表情码，不再原样透传。
        AiStreamingDialogueBox box = NewBox();

        box.SetEmotion("happy");

        Assert.Equal("$h", box.characterDialogue.CurrentEmotion);
    }

    [Fact]
    public void SetEmotion_MapsDollarPrefixedSemanticWordToVanillaPortraitCode()
    {
        AiStreamingDialogueBox box = NewBox();

        box.SetEmotion("$sad");

        Assert.Equal("$s", box.characterDialogue.CurrentEmotion);
    }

    [Fact]
    public void SetEmotion_MapsAliasesToVanillaPortraitCodes()
    {
        AiStreamingDialogueBox box = NewBox();

        box.SetEmotion("joy");
        Assert.Equal("$h", box.characterDialogue.CurrentEmotion);

        box.SetEmotion("unique");
        Assert.Equal("$u", box.characterDialogue.CurrentEmotion);

        box.SetEmotion("heart");
        Assert.Equal("$l", box.characterDialogue.CurrentEmotion);

        box.SetEmotion("rage");
        Assert.Equal("$a", box.characterDialogue.CurrentEmotion);

        box.SetEmotion("neutral");
        Assert.Equal("$neutral", box.characterDialogue.CurrentEmotion);
    }

    [Fact]
    public void SetEmotion_NumericFrameIndex_KeepsDigits()
    {
        AiStreamingDialogueBox box = NewBox();

        box.SetEmotion("6");

        Assert.Equal("$6", box.characterDialogue.CurrentEmotion);
    }

    [Fact]
    public void SetEmotion_UnknownCode_FallsBackToNeutralWithoutThrowing()
    {
        // RECOVERABLE 路径：未知码必须降级，不得阻断渲染。
        AiStreamingDialogueBox box = NewBox();

        box.SetEmotion("!!bogus!!");

        Assert.Equal("$neutral", box.characterDialogue.CurrentEmotion);
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

    #region VT-STREAM-05 情绪标记纵深清洗

    [Fact]
    public void AppendContent_StripsDollarEmotionCodeFromBody()
    {
        AiStreamingDialogueBox box = NewBox();

        box.AppendContent("$a Grrr", false);

        // 正文绝不能残留情绪码，裸文本内容必须完整保留。
        Assert.Equal(" Grrr", box.DisplayedPageText);
        Assert.Equal("$a", box.characterDialogue.CurrentEmotion);
    }

    [Fact]
    public void AppendContent_StripsSadEmotionCodeFromBody()
    {
        AiStreamingDialogueBox box = NewBox();

        box.AppendContent("Oh no $s", false);

        Assert.Equal("Oh no ", box.DisplayedPageText);
        Assert.Equal("$s", box.characterDialogue.CurrentEmotion);
    }

    [Fact]
    public void AppendContent_StripsBracketMoodTagFromBody()
    {
        AiStreamingDialogueBox box = NewBox();

        box.AppendContent("[MOOD:happy]Hi there", false);

        Assert.Equal("Hi there", box.DisplayedPageText);
        Assert.Equal("$h", box.characterDialogue.CurrentEmotion);
    }

    [Fact]
    public void SetContent_StripsEmotionCodesFromBody()
    {
        AiStreamingDialogueBox box = NewBox();

        box.SetContent("$a Grrr", true);

        Assert.Equal(" Grrr", box.DisplayedPageText);
        Assert.Equal("$a", box.characterDialogue.CurrentEmotion);
    }

    [Fact]
    public void AppendContent_EmotionOnlyChunk_StillDrivesEmotionWithoutTouchingText()
    {
        AiStreamingDialogueBox box = NewBox();

        box.AppendContent("$l", false);

        // 纯情绪 chunk 洗练后正文为空，不得因此把对白框打回思考态停滞。
        Assert.Equal("$l", box.characterDialogue.CurrentEmotion);
        Assert.Equal(StreamingDialogueState.Typing, box.State);
    }

    #endregion

    #region VT-STREAM-05 立绘区域

    [Fact]
    public void Constructor_WithSpeaker_EnablesPortraitFlag()
    {
        AiStreamingDialogueBox box = NewBox();

        Assert.True(box.characterDialogue.showPortrait);
    }

    [Fact]
    public void Constructor_WithoutSpeaker_LeavesDialogueUnbound()
    {
        AiStreamingDialogueBox box = new(null, "");

        Assert.Null(box.characterDialogue);
        Assert.False(box.isPortraitBox());
    }

    [Fact]
    public void IsPortraitBox_WithPortraitTextureAndOption_ReportsTrue()
    {
        SetShowPortraits(true);
        var npc = new NPC();
        GiveNpcPortrait(npc);

        AiStreamingDialogueBox box = new(npc, "");

        Assert.True(box.isPortraitBox());
    }

    [Fact]
    public void IsPortraitBox_WithoutPortraitTexture_FallsBackToNonPortraitLayout()
    {
        // BOUNDARY 路径：说话者没有立绘纹理时降级为非立绘宽布局，不得抛异常。
        SetShowPortraits(true);

        AiStreamingDialogueBox box = new(new NPC(), "");

        Assert.False(box.isPortraitBox());
    }

    [Fact]
    public void IsPortraitBox_WithPortraitDisabled_ReportsFalse()
    {
        SetShowPortraits(false);
        var npc = new NPC();
        GiveNpcPortrait(npc);

        AiStreamingDialogueBox box = new(npc, "");

        // 原版门禁：Game1.options.showPortraits 关闭时一律走非立绘布局。
        Assert.False(box.isPortraitBox());
    }

    [Fact]
    public void AppendContent_PortraitMode_SyncsEmotionAndKeepsBody()
    {
        SetShowPortraits(true);
        var npc = new NPC();
        GiveNpcPortrait(npc);
        AiStreamingDialogueBox box = new(npc, "");

        box.AppendContent("$l Love that", false);

        Assert.Equal("$l", box.characterDialogue.CurrentEmotion);
        Assert.Equal(" Love that", box.DisplayedPageText);
    }

    #endregion

    #region VT-STREAM-05 悬浮检测与取消按钮

    [Fact]
    public void PerformHoverAction_Headless_DoesNotThrow()
    {
        // BOUNDARY：无头环境无玩家/好感度数据，悬浮检测必须短路而不是空引用。
        AiStreamingDialogueBox box = NewBox();

        box.performHoverAction(10, 10);
            box.performHoverAction(box.x + box.width - 32, box.y + 320);
    }

    [Fact]
    public void ReceiveLeftClick_InsideCancelButton_InThinking_CancelsDialogue()
    {
        InstallFarmerShim();
        AiStreamingDialogueBox box = NewBox();
        Assert.Equal(StreamingDialogueState.Thinking, box.State);

        // 取消按钮锚在文字区右下角：点击命中矩形即中止本轮。
        int btnX = box.x + (box.width - 16) - 64;
        int btnY = box.y + box.height - 68;
        IgnoringHeadlessPlayerRelease(() => box.receiveLeftClick(btnX + 10, btnY + 10));

        Assert.Equal(StreamingDialogueState.Faulted, box.State);
    }

    [Fact]
    public void ReceiveLeftClick_InsideCancelButton_WhenStreamComplete_KeepsPageTurnBehaviour()
    {
        InstallFarmerShim();
        AiStreamingDialogueBox box = NewBox("abc");
        box.SetContent("abc", true);

        int btnX = box.x + (box.width - 16) - 64;
        int btnY = box.y + box.height - 68;
        box.receiveLeftClick(btnX + 10, btnY + 10);

        // 流已结束：取消按钮不再响应，退回原有的「拉满当前页」语义。
        Assert.Equal(StreamingDialogueState.Complete, box.State);
    }

    [Fact]
    public void ReceiveLeftClick_OutsideCancelButton_InThinking_DoesNotCancel()
    {
        InstallFarmerShim();
        AiStreamingDialogueBox box = NewBox();

        box.receiveLeftClick(box.x + 4, box.y + 4);

        Assert.Equal(StreamingDialogueState.Thinking, box.State);
    }

    [Fact]
    public void CancelCurrentDialogue_WithoutLiveCts_EntersFaultedWithoutThrowing()
    {
        // RECOVERABLE 路径：CTS 缺失时不得抛异常，仍须收束对白框并继续释放玩家移动。
        InstallFarmerShim();
        AiStreamingDialogueBox box = NewBox("partial");

        IgnoringHeadlessPlayerRelease(() => box.CancelCurrentDialogue());

        Assert.Equal(StreamingDialogueState.Faulted, box.State);
        Assert.Equal(string.Empty, box.DisplayedPageText);

        // 释放序列前半段（可无头求值部分）必须已经生效。
        Assert.True(Game1.player.CanMove);
        Assert.Empty(Game1.player.movementDirections);
    }

    #endregion

    #region 思考态波浪文字标点规范化

    /// <summary>
    /// 经反射调用绘制路径上的私有文本组装方法。draw() 本身需要 SpriteBatch，
    /// 无头环境无法构造，故只对其纯字符串部分建立断言。
    /// </summary>
    private static string BuildThinkingWaveTextForTest(string message, int clockMs)
        => (string)typeof(AiStreamingDialogueBox)
            .GetMethod("BuildThinkingWaveText", BindingFlags.Static | BindingFlags.NonPublic)
            .Invoke(null, new object[] { message, clockMs });

    /// <summary>返回字符串中最长的连续点号游程长度。</summary>
    private static int MaxConsecutiveDots(string text)
    {
        int longest = 0;
        int run = 0;
        foreach (char c in text)
        {
            run = c == '.' ? run + 1 : 0;
            if (run > longest)
                longest = run;
        }

        return longest;
    }

    [Theory]
    [InlineData("思考中...")]   // 仓库内 zh.json 的实际取值
    [InlineData("Thinking...")] // 仓库内 default.json 的实际取值
    [InlineData("思考中。")]
    [InlineData("思考中…")]
    [InlineData("思考中")]
    [InlineData("thinking")]
    public void BuildThinkingWaveText_AnyTrailingPunctuation_NeverExceedsThreeDots(string message)
    {
        // 覆盖整整两个动点周期（4 × 500ms），逐 100ms 采样。
        for (int clockMs = 0; clockMs < 4000; clockMs += 100)
        {
            string animated = BuildThinkingWaveTextForTest(message, clockMs);
            int longest = MaxConsecutiveDots(animated);
            Assert.True(longest <= 3,
                $"文案 '{message}' 在 {clockMs}ms 处产生了 {longest} 个连续点号：'{animated}'。");
        }
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(400, 0)]
    [InlineData(500, 1)]
    [InlineData(1000, 2)]
    [InlineData(1500, 3)]
    [InlineData(2000, 0)] // 4 点一循环，回到 0
    [InlineData(2500, 1)]
    public void BuildThinkingWaveText_AppendsZeroToThreeDotsOnFixedCycle(int clockMs, int expectedDots)
    {
        // 尾部标点必须被剥离后重算，否则 "Thinking..." 在 1500ms 处会拼成 6 个点。
        Assert.Equal(
            "Thinking" + new string('.', expectedDots),
            BuildThinkingWaveTextForTest("Thinking...", clockMs));
    }

    [Theory]
    [InlineData("思考中...", "思考中")]
    [InlineData("思考中。", "思考中")]
    [InlineData("思考中…", "思考中")]
    [InlineData("思考中", "思考中")]
    public void BuildThinkingWaveText_PreservesBaseText_StripsOnlyTrailingPunctuation(string message, string expectedBase)
    {
        // 时钟取 500ms 的整数倍，使点号数量为 0 或正数，便于比对基干。
        foreach (int clockMs in new[] { 0, 500, 1000, 1500 })
        {
            string animated = BuildThinkingWaveTextForTest(message, clockMs);
            Assert.StartsWith(expectedBase, animated);
            Assert.Equal(expectedBase.Length, animated.TrimEnd('.').Length);
        }
    }

    #endregion

    #region VT-STREAM-07 立绘晃动计时器

    /// <summary>
    /// 无头调用 update()。测试项目不直接引用 MonoGame.Framework（GameTime 不可静态书写），
    /// 故经 update 的参数类型反射取得该类型；两参同值规避构造函数参数次序差异，
    /// 确保 ElapsedGameTime 恒为 elapsedMs。
    /// </summary>
    private static void UpdateBox(AiStreamingDialogueBox box, int elapsedMs)
    {
        MethodInfo update = typeof(AiStreamingDialogueBox)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Single(m => m.Name == "update"
                && m.GetParameters().Length == 1
                && m.GetParameters()[0].ParameterType.Name == "GameTime");

        Type gameTimeType = update.GetParameters()[0].ParameterType;
        object time = Activator.CreateInstance(gameTimeType,
            new object[] { TimeSpan.FromMilliseconds(elapsedMs), TimeSpan.FromMilliseconds(elapsedMs) });

        update.Invoke(box, new[] { time });
    }

    [Fact]
    public void Update_DecrementsPortraitShakeTimerToZero_EndlessShakeFixed()
    {
        // 原版 DialogueBox 从不递减 newPortaitShakeTimer 的缺陷复现：
        // SetEmotion 置入 250ms 晃动后，update() 必须逐帧递减并精确归零。
        AiStreamingDialogueBox box = NewBox();
        FieldInfo shakeField = typeof(DialogueBox).GetField(
            "newPortaitShakeTimer", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(shakeField);
        shakeField.SetValue(box, 250);

        for (int frame = 0; frame < 40 && (int)shakeField.GetValue(box) > 0; frame++)
            UpdateBox(box, 16);

        // 40 帧 × 16ms = 640ms > 250ms：计时器必须已耗尽且归零（不残留、不为负）。
        Assert.Equal(0, (int)shakeField.GetValue(box));
    }

    [Fact]
    public void Update_ZeroShakeTimer_StaysZero()
    {
        AiStreamingDialogueBox box = NewBox();
        FieldInfo shakeField = typeof(DialogueBox).GetField(
            "newPortaitShakeTimer", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

        UpdateBox(box, 16);

        Assert.Equal(0, (int)shakeField.GetValue(box));
    }

    #endregion

    #region VT-STREAM-07 前导引导线纵深清洗

    [Fact]
    public void AppendContent_FirstScreenWhitespaceDashPrefix_IsFullyStripped()
    {
        AiStreamingDialogueBox box = NewBox();

        box.AppendContent(" - 你好", false);

        Assert.Equal("你好", box.DisplayedPageText);
    }

    [Fact]
    public void SetContent_FirstScreenDashPrefix_IsFullyStripped()
    {
        AiStreamingDialogueBox box = NewBox();

        box.SetContent("- 很高兴见到你", true);

        Assert.Equal("很高兴见到你", box.DisplayedPageText);
    }

    [Fact]
    public void AppendContent_DashOnlyChunk_DoesNotEmitBrokenDialogue()
    {
        // RECOVERABLE 路径：整段仅由 '-' 与空白组成时整体剥空。
        AiStreamingDialogueBox box = NewBox();

        box.AppendContent("-", false);

        Assert.Equal(string.Empty, box.DisplayedPageText);
    }

    [Fact]
    public void AppendContent_DashAfterFirstScreenText_IsPreserved()
    {
        // 首屏首字已写入后，句中破折号属合法正文，不再清洗。
        AiStreamingDialogueBox box = NewBox();

        box.AppendContent("第一句", false);
        box.AppendContent(" - 第二句", false);

        Assert.Equal("第一句 - 第二句", box.DisplayedPageText);
    }

    #endregion

    #region VT-STREAM-07 按节奏显示文字开关

    [Fact]
    public void ComputeDelay_RhythmicTypingDisabled_ReturnsConstantBaseDelay()
    {
        ModConfig original = ModEntry.Config;
        try
        {
            ModEntry.Config = new ModConfig { EnableRhythmicTyping = false };

            // 开关闭合时无视标点与换行，一律恒定 35ms 原版等间隔速度。
            Assert.Equal(35, AiStreamingDialogueBox.ComputeDelayMs("a, b", 2));   // 逗号
            Assert.Equal(35, AiStreamingDialogueBox.ComputeDelayMs("Done.", 5));  // 句号
            Assert.Equal(35, AiStreamingDialogueBox.ComputeDelayMs("a\nb", 2));   // 换行
            Assert.Equal(35, AiStreamingDialogueBox.ComputeDelayMs("嗯——", 3));   // 破折号
        }
        finally
        {
            ModEntry.Config = original;
        }
    }

    [Fact]
    public void ComputeDelay_NullConfig_PreservesRhythmicBehaviour()
    {
        // RECOVERABLE 路径：无头测试环境 Config 为 null 时按开启处理。
        ModConfig original = ModEntry.Config;
        try
        {
            ModEntry.Config = null;

            Assert.Equal(175, AiStreamingDialogueBox.ComputeDelayMs("a, b", 2));
            Assert.Equal(450, AiStreamingDialogueBox.ComputeDelayMs("a\nb", 2));
        }
        finally
        {
            ModEntry.Config = original;
        }
    }

    #endregion

    #region VT-STREAM-07 大模型友好断页

    [Fact]
    public void AppendContent_VanillaHashDollarB_SplitsPagesWithoutLeakingControlChars()
    {
        AiStreamingDialogueBox box = NewBox();

        box.AppendContent("第一页内容#$b#第二页内容", false);

        // "#$b#" 全串被整体吃掉：当前页封口，后续内容进入下一页。
        Assert.Equal("第一页内容", box.DisplayedPageText);
        Assert.True(box.IsCurrentPageSealed);
        Assert.Equal(1, box.PendingPageCount);
        Assert.Equal("第一页内容 第二页内容", box.GetFullDialogueText());

        // 任何一页正文都不得残留 "#$b#" 或 "$b" 控制符碎片。
        string full = box.GetFullDialogueText();
        Assert.DoesNotContain("#", full);
        Assert.DoesNotContain("$b", full);
    }

    [Fact]
    public void AppendContent_TrickleHashDollarB_Variant_IsConsumedWhole()
    {
        // 残缺形态 "#$b"（无尾 #）：同样整体吞掉，不留 "$b" 碎片。
        AiStreamingDialogueBox box = NewBox();

        box.AppendContent("第一页#$b", false);
        box.AppendContent("第二页", false);

        Assert.Equal("第一页", box.DisplayedPageText);
        string full = box.GetFullDialogueText();
        Assert.Equal("第一页 第二页", full);
        Assert.DoesNotContain("$b", full);
    }

    [Fact]
    public void AppendContent_LeadingDollarBHash_IsConsumedWithoutEmptyCrash()
    {
        // RECOVERABLE 路径：分屏标记命中下标 0 时跳过游标并封口，不崩不发空串。
        AiStreamingDialogueBox box = NewBox();

        box.AppendContent("$b#第二页内容", false);

        Assert.Equal(1, box.PendingPageCount);
        Assert.Equal("第二页内容", box.GetFullDialogueText());
        Assert.DoesNotContain("$b", box.GetFullDialogueText());
    }

    [Fact]
    public void AppendContent_ExplicitPageTag_SplitsPages()
    {
        AiStreamingDialogueBox box = NewBox();

        box.AppendContent("第一页内容[PAGE]第二页内容", false);

        Assert.Equal("第一页内容", box.DisplayedPageText);
        Assert.Equal(1, box.PendingPageCount);
        Assert.DoesNotContain("[PAGE]", box.GetFullDialogueText());
    }

    [Fact]
    public void AppendContent_MaxPageHeight300_KeepsMediumCjkSentenceOnFirstPage()
    {
        // 52 个汉字（无空格）实测高度 264px：旧 200px 阈值下必然截断，
        // 新 300px 阈值下整段留在首屏——容量翻倍的直接证据。
        AiStreamingDialogueBox box = NewBox();
        string sentence = new string('字', 52);

        box.AppendContent(sentence, false);

        Assert.False(box.IsCurrentPageSealed);
        Assert.Equal(0, box.PendingPageCount);
        Assert.Equal(sentence, box.DisplayedPageText);
    }

    [Fact]
    public void AppendContent_75CharChineseSentence_StaysOnFirstPage()
    {
        // 75 字中文复合长句整段留在首屏。注意：无头 SpriteText 的测高对
        // 无换行点的连续汉字串会纵向爆量（实测 69 连串 = 1182px），只有
        // 空格提供断词点；故本句按分句自然留出空格（与 LLM 实际输出一致）。
        AiStreamingDialogueBox box = NewBox();
        string sentence = string.Join(" ", Enumerable.Repeat("今天天气很不错呀，", 7)) + " 好吧再见。";
        Assert.Equal(75, sentence.Length);

        box.AppendContent(sentence, false);

        Assert.False(box.IsCurrentPageSealed);
        Assert.Equal(0, box.PendingPageCount);
        Assert.Equal(sentence, box.DisplayedPageText);
    }

    [Fact]
    public void AppendContent_SentenceSoftBreak_CutsRightAfterSentenceEnder()
    {
        // 语义句末软断行：高度溢出封口时截断点紧随句末标点，标点留在当前页。
        AiStreamingDialogueBox box = NewBox();
        string sentence = string.Concat(Enumerable.Repeat("我们聊了很久，今天真的很开心。", 30));

        box.AppendContent(sentence, false);

        Assert.True(box.IsCurrentPageSealed);
        Assert.EndsWith("。", box.DisplayedPageText);
        Assert.True(box.DisplayedPageText.Length < sentence.Length);
        Assert.True(box.PendingPageCount > 0);
    }

    /// <summary>经反射调用私有静态安全断点方法（纯字符串计算，无游戏态）。</summary>
    private static int ComputeSafeBreakIndexForTest(string text, int upperBound)
        => (int)typeof(AiStreamingDialogueBox)
            .GetMethod("ComputeSafeBreakIndex", BindingFlags.Static | BindingFlags.NonPublic)
            .Invoke(null, new object[] { text, upperBound });

    [Fact]
    public void ComputeSafeBreakIndex_SentenceEnderWithinWindow_CutsAfterPunctuation()
    {
        // "今天天气真好。我们去钓鱼吧" 句号在下标 6：upperBound=14 时截断点 = 7。
        Assert.Equal(7, ComputeSafeBreakIndexForTest("今天天气真好。我们去钓鱼吧", 14));
    }

    [Fact]
    public void ComputeSafeBreakIndex_SentenceEnder_PREFERREDOverWordBoundary()
    {
        // "Hello there. World"：句点在下标 11 -> 截断点 12，优先于词边界 13。
        Assert.Equal(12, ComputeSafeBreakIndexForTest("Hello there. World", 18));
    }

    [Fact]
    public void ComputeSafeBreakIndex_NoEnderInWindow_FallsBackToWordBoundary()
    {
        // 窗口内无句末标点：回退既有 ASCII 词边界断行。
        Assert.Equal(6, ComputeSafeBreakIndexForTest("hello world", 11));
    }

    [Fact]
    public void ComputeSafeBreakIndex_EnderOutsideWindow_IsIgnored()
    {
        // 句末标点落在 [upperBound - 24, upperBound] 窗口之外时不得回溯命中。
        string text = "。" + new string('好', 24);
        Assert.Equal(25, ComputeSafeBreakIndexForTest(text, 25));
    }

    [Fact]
    public void AppendContent_DashAfterEmotionCode_IsStillStripped()
    {
        // "$h - " 形态：情绪码剥离后首屏仍以引导线开头，前导破折号必须被洗净。
        AiStreamingDialogueBox box = NewBox();

        box.AppendContent("$h - 你好", false);

        Assert.Equal("$h", box.characterDialogue.CurrentEmotion);
        Assert.Equal("你好", box.DisplayedPageText);
    }

    [Fact]
    public void SetEmotion_FootballAndGridball_MapToUniquePortrait()
    {
        // VT-STREAM-07：Alex 专属格球语义归一到 $u 专属立绘。
        AiStreamingDialogueBox box = NewBox();

        box.SetEmotion("football");
        Assert.Equal("$u", box.characterDialogue.CurrentEmotion);

        box.SetEmotion("gridball");
        Assert.Equal("$u", box.characterDialogue.CurrentEmotion);

        box.SetEmotion("unique");
        Assert.Equal("$u", box.characterDialogue.CurrentEmotion);
    }

    [Fact]
    public void Update_StreamingBufferStarvation_DoesNotDumpMultipleCharsInSingleFrame()
    {
        // 验证打字机缓冲饥饿防一顿一顿倾泻：
        // 当打字机追平当前流式文本末尾后，计时器必须归零，严禁累积负时间。
        // 后续 chunk ("CD") 到达后，首帧（16ms）绝不得将 2 个字符瞬间倾泻完毕。
        AiStreamingDialogueBox box = NewBox();

        box.AppendContent("AB", false);
        // 推进直至 "AB" 两个字符全部揭示（35ms * 2 = 70ms）
        for (int i = 0; i < 6; i++)
            UpdateBox(box, 16);

        Assert.Equal(2, box.CharacterIndex);

        // 模拟网络延迟：空等 10 帧（160ms），打字机已处于饥饿态
        for (int i = 0; i < 10; i++)
            UpdateBox(box, 16);

        Assert.Equal(2, box.CharacterIndex);

        // 新 chunk 到达（2 个字符）
        box.AppendContent("CD", false);

        // 单帧（16ms）推进：16ms < 35ms，绝不得直接揭示到末尾 4！
        UpdateBox(box, 16);
        Assert.True(box.CharacterIndex < 4, $"首帧不应瞬间揭示全部字符，当前 index: {box.CharacterIndex}");
    }

    /// <summary>翻转 Options.dialogueTyping 门禁（构造垫片默认 false）。</summary>
    private static void SetDialogueTyping(bool value)
    {
        object game1 = Game1InstanceField?.GetValue(null);
        object options = game1 == null ? null : Game1OptionsField?.GetValue(game1);
        SetField(options, "dialogueTyping", value);
    }

    /// <summary>安装计数音效桩并清零计数（下一用例的 InstallHeadlessShims 会复位回空音效桩）。</summary>
    private static void InstallCountingSounds()
    {
        Type soundsType = typeof(Game1)
            .GetField("sounds", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .FieldType;

        MethodInfo generic = typeof(DispatchProxy)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .First(m => m.Name == "Create" && m.IsGenericMethodDefinition)
            .MakeGenericMethod(soundsType, typeof(CountingSoundsHelper));

        CountingSoundsHelper.PlayCount = 0;
        SetStaticField(typeof(Game1), "sounds", generic.Invoke(null, null));
    }

    [Fact]
    public void Update_StreamingTyping_PlaysStepSoundForEachRevealedChar()
    {
        // 音效门禁（票 VT-TYPEWRITER-PROMPT-CALIBRATION）：流未完成时，
        // 本页每个步进字符都播放打字音——含首字与页尾字。旧门禁
        // (_characterIndex > 1 && index < length) 只会为中间字符播音。
        SetDialogueTyping(true);
        InstallCountingSounds();
        AiStreamingDialogueBox box = NewBox("abc");

        for (int i = 0; i < 12 && box.CharacterIndex < 3; i++)
            UpdateBox(box, 16);

        Assert.Equal(3, box.CharacterIndex);
        Assert.Equal(3, CountingSoundsHelper.PlayCount);
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

/// <summary>
/// 计数音效桩：与 NullSoundsHelper 同构，但统计 Play* 调用次数，
/// 用于验证打字机步进音效门禁（票 VT-TYPEWRITER-PROMPT-CALIBRATION）。
/// 测试类固定为非并行集合，静态计数器安全。
/// </summary>
internal class CountingSoundsHelper : DispatchProxy
{
    public static int PlayCount;

    protected override object Invoke(MethodInfo targetMethod, object[] args)
    {
        if (targetMethod.Name.StartsWith("Play", StringComparison.Ordinal))
            PlayCount++;

        if (targetMethod.ReturnType == typeof(void))
            return null;

        if (targetMethod.ReturnType == typeof(bool))
            return false;

        if (targetMethod.ReturnType.IsByRef)
        {
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
