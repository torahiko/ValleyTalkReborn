// AsyncBuilderStreamingIntegrationTests.cs
// ═══════════════════════════════════════════════════════════════════════════
// VT-STREAM-03-AsyncBuilderIntegration
// ═══════════════════════════════════════════════════════════════════════════
// 混合测试策略：
//
//   1) 纯内存（无 Game1）—— 镜像 AsyncBuilder.BuildStreamCallback 的接线方式，
//      验证 raw chunk 经 StreamTokenPipeline 分流后进入 ConcurrentQueue<StreamSegment>，
//      并按 Text / Action / Portrait 正确分型。回调本体不触碰任何游戏态，
//      因此可在无头环境完整驱动。
//
//   2) PendingChoiceStore 凭据绑定 —— 用真实 AiStreamingDialogueBox 作为 BoxRef，
//      验证完成路径装配的载荷能被同框消费、被异框拒绝。
//      构造真实对白框需要与 AiStreamingDialogueBoxTests 相同的引擎静态垫片，
//      故本类同属 EngineStaticStateCollection 非并行集合。
//
//   3) 源码契约断言 —— PerformGeneration 为 private async 且深度耦合
//      Game1.player / NPC / Dialogue，无法在无头环境驱动；
//      挂载点、异常降级、状态清理等结构约束沿用 AsyncBuilderNarrationOverlayTests
//      已确立的「对源文件断言」模式。
//
// 纯内存测试：无 Provider、无网络、不读写存档。
// ═══════════════════════════════════════════════════════════════════════════

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using StardewValley;
using ValleytalkReborn;
using ValleytalkReborn.Dialogue.Coordination;
using ValleytalkReborn.UI;
using Xunit;

namespace ValleytalkReborn.Tests;

[Collection("EngineStaticStateCollection")]
public class AsyncBuilderStreamingIntegrationTests : IDisposable
{
    #region 无头引擎垫片（与 AiStreamingDialogueBoxTests 同源）

    private static readonly FieldInfo Game1InstanceField =
        typeof(Game1).GetField("game1", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

    private static readonly FieldInfo Game1OptionsField =
        typeof(Game1).GetField("instanceOptions", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

    private static readonly FieldInfo UiViewportField =
        typeof(Game1).GetField("uiViewport", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

    private readonly object _previousGame1;
    private readonly object _previousOptions;
    private readonly object _previousViewport;

    public AsyncBuilderStreamingIntegrationTests()
    {
        _previousGame1 = Game1InstanceField?.GetValue(null);
        _previousOptions = _previousGame1 == null ? null : Game1OptionsField?.GetValue(_previousGame1);
        _previousViewport = UiViewportField?.GetValue(null);

        InstallHeadlessShims();
    }

    public void Dispose()
    {
        // PendingChoiceStore 是进程级单槽：离开时必须清空，避免污染其他用例。
        PendingChoiceStore.Clear();

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
    /// 最小 Options 垫片：base DialogueBox 构造与 update 的 Game1.options.dialogueTyping
    /// 门禁都要能求值。snappyMenus/gamepadControls 必须置 false，避免输入子系统被触碰。
    /// </summary>
    private static void InstallHeadlessShims()
    {
        SetStaticField(typeof(Game1), "sounds", CreateNullSounds());

        var game1 = (Game1)FormatterServices.GetUninitializedObject(typeof(Game1));
        Game1InstanceField?.SetValue(null, game1);

        var options = (Options)FormatterServices.GetUninitializedObject(typeof(Options));
        SetField(options, "snappyMenus", false);
        SetField(options, "gamepadControls", false);
        SetField(options, "dialogueTyping", false);
        SetField(options, "showPortraits", false);
        SetField(game1, "instanceOptions", options);

        var uiViewportType = UiViewportField?.FieldType;
        object zeroViewport = uiViewportType == null
            ? null
            : Activator.CreateInstance(uiViewportType,
                Activator.CreateInstance(uiViewportType.GetField("Location")!.FieldType, 0, 0),
                Activator.CreateInstance(uiViewportType.GetField("Size")!.FieldType, 1280, 0));
        SetStaticField(typeof(Game1), "uiViewport", zeroViewport);
    }

    /// <summary>ISoundsHelper 无操作代理（依赖 DispatchProxy，免写 MonoGame 签名样板）。</summary>
    private static object CreateNullSounds()
    {
        Type soundsType = typeof(Game1)
            .GetField("sounds", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            .FieldType;

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

    private static void SetStaticField(Type type, string name, object value)
        => type.GetField(name, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
            ?.SetValue(null, value);

    #endregion

    #region 测试辅助

    /// <summary>
    /// 镜像 AsyncBuilder.BuildStreamCallback：后台线程只做「分流 + 并发入队」，
    /// 不触碰 Game1 / NPC / UI。此处刻意不引用 AsyncBuilder 私有方法，
    /// 以便无头环境下直接驱动同一条数据通路。
    /// </summary>
    private static Action<string> BuildCallback(StreamTokenPipeline pipeline, ConcurrentQueue<StreamSegment> queue)
    {
        return chunk =>
        {
            if (pipeline == null)
                return;

            foreach (StreamSegment segment in pipeline.Feed(chunk))
                queue.Enqueue(segment);
        };
    }

    /// <summary>模拟主线程 OnUpdateTicked 的 TryDequeue 批量消费。</summary>
    private static List<StreamSegment> Drain(ConcurrentQueue<StreamSegment> queue)
    {
        var drained = new List<StreamSegment>();
        while (queue.TryDequeue(out StreamSegment segment))
            drained.Add(segment);
        return drained;
    }

    private static string ConcatText(IEnumerable<StreamSegment> segments)
        => string.Concat(segments.Where(s => s.Type == StreamSegmentType.Text).Select(s => s.Payload));

    private static string ReadAsyncBuilderSource()
    {
        var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
        for (int i = 0; i < 12; i++)
        {
            var src = Path.Combine(dir.FullName, "src", "LLM", "Prompts", "Generation", "AsyncBuilder.cs");
            if (File.Exists(src)) return File.ReadAllText(src);
            if (dir.Parent == null) break;
            dir = dir.Parent;
        }
        return null;
    }

    private static string SourceOrFail()
    {
        var source = ReadAsyncBuilderSource();
        Assert.True(source != null,
            $"AsyncBuilder.cs not found. Searched upward from {AppDomain.CurrentDomain.BaseDirectory}.");
        return source;
    }

    #endregion

    #region 1. 流式回调：raw chunk -> 片段队列

    [Fact]
    public void StreamCallback_RawChunks_EnterSegmentQueueAsText()
    {
        var pipeline = new StreamTokenPipeline();
        var queue = new ConcurrentQueue<StreamSegment>();
        Action<string> callback = BuildCallback(pipeline, queue);

        callback("你好，");
        callback("今天");
        callback("过得怎么样？");

        List<StreamSegment> drained = Drain(queue);

        // Provider 的 delta 现已原样透传（不再经 StreamLineTracker 按行整形），
        // 因此碎片化 chunk 必须能无损拼回完整正文。
        Assert.Equal("你好，今天过得怎么样？", ConcatText(drained));
        Assert.All(drained, s => Assert.Equal(StreamSegmentType.Text, s.Type));
    }

    [Fact]
    public void StreamCallback_SplitsActionAndPortraitSegments()
    {
        var pipeline = new StreamTokenPipeline();
        var queue = new ConcurrentQueue<StreamSegment>();
        Action<string> callback = BuildCallback(pipeline, queue);

        callback("前面");
        callback("[ACTION:FOLLOW]后面");
        callback("$h");

        List<StreamSegment> drained = Drain(queue);

        // 管道在每个 chunk 结束时释放已缓冲正文，因此「后面」先于下一 chunk 的 $h 产出。
        Assert.Equal(
            new[] { StreamSegmentType.Text, StreamSegmentType.Action, StreamSegmentType.Text, StreamSegmentType.Portrait },
            drained.Select(s => s.Type).ToArray());
        Assert.Equal("前面", drained[0].Payload);
        Assert.Equal("ACTION:FOLLOW", drained[1].Payload);
        Assert.Equal("后面", drained[2].Payload);
        Assert.Equal("h", drained[3].Payload);
    }

    [Fact]
    public void StreamCallback_ActionSegmentPayload_IsWrappedForEmbodiedActionParser()
    {
        // 主线程消费点以 "[" + Payload + "]" 复原标签后交给 EmbodiedActionParser，
        // 故载荷必须保留 ACTION: 前缀，否则 FOLLOW/GOTO 等实体会被静默丢弃。
        var pipeline = new StreamTokenPipeline();
        var queue = new ConcurrentQueue<StreamSegment>();
        Action<string> callback = BuildCallback(pipeline, queue);

        callback("[ACTION:EMOTE:HEART]");

        StreamSegment action = Assert.Single(Drain(queue));
        Assert.Equal(StreamSegmentType.Action, action.Type);
        Assert.Equal("[ACTION:EMOTE:HEART]", "[" + action.Payload + "]");
    }

    [Fact]
    public void StreamCallback_OptionSection_StopsSegmentsButCollectsSuggestions()
    {
        var pipeline = new StreamTokenPipeline();
        var queue = new ConcurrentQueue<StreamSegment>();
        Action<string> callback = BuildCallback(pipeline, queue);

        callback("今天真不错。\n%");
        callback(" 去散步\n- 好的\n- 再见");

        List<StreamSegment> drained = Drain(queue);

        // '%' 之后不再产出任何片段（选项改由 Flush 汇入候选项）。
        Assert.Equal("今天真不错。", ConcatText(drained));
        Assert.DoesNotContain(drained, s => s.Type != StreamSegmentType.Text);

        foreach (StreamSegment tail in pipeline.Flush())
            queue.Enqueue(tail);

        Assert.Equal(new[] { "去散步", "好的", "再见" }, pipeline.GetCollectedSuggestions());
    }

    [Fact]
    public void StreamCallback_NullChunk_IsIgnoredWithoutDroppingFirstChunkPrefixRule()
    {
        var pipeline = new StreamTokenPipeline();
        var queue = new ConcurrentQueue<StreamSegment>();
        Action<string> callback = BuildCallback(pipeline, queue);

        callback(null);
        callback("");
        callback("- 你好");

        Assert.Equal("你好", ConcatText(Drain(queue)));
    }

    #endregion

    #region 2. PendingChoiceStore 凭据绑定

    [Fact]
    public void PendingChoiceStore_BindsStreamingBoxAsBoxRef()
    {
        var streamingBox = new AiStreamingDialogueBox(new NPC());
        streamingBox.AppendContent("今天天气不错", false);

        PendingChoiceStore.Set(new PendingChoiceContext
        {
            Speaker = new NPC(),
            NpcLineSanitized = AsyncBuilder.SanitizeDialogueForHistory(streamingBox.DisplayedPageText),
            Suggestions = new List<string> { "好的", "再见" },
            ShowDateOption = false,
            BoxRef = streamingBox
        });

        Assert.True(PendingChoiceStore.TryPeek(out PendingChoiceContext peeked));
        Assert.Same(streamingBox, peeked.BoxRef);
        Assert.Equal("今天天气不错", peeked.NpcLineSanitized);
        Assert.Equal(new[] { "好的", "再见" }, peeked.Suggestions);
        Assert.False(peeked.ShowDateOption);

        // 同框消费：ModEntry.OnMenuChanged 在流式框关闭时以 e.OldMenu 命中。
        PendingChoiceContext consumed = PendingChoiceStore.ConsumeMatching(streamingBox);

        Assert.NotNull(consumed);
        Assert.Same(streamingBox, consumed.BoxRef);
        Assert.False(PendingChoiceStore.TryPeek(out _));
    }

    [Fact]
    public void PendingChoiceStore_RejectsMismatchedBox()
    {
        var streamingBox = new AiStreamingDialogueBox(new NPC());
        var otherBox = new AiStreamingDialogueBox(new NPC());

        PendingChoiceStore.Set(new PendingChoiceContext
        {
            Speaker = new NPC(),
            NpcLineSanitized = "台词",
            Suggestions = new List<string> { "好的" },
            ShowDateOption = false,
            BoxRef = streamingBox
        });

        // 异框消费必须作废载荷：否则事件打断会弹出陈旧选择面板。
        Assert.Null(PendingChoiceStore.ConsumeMatching(otherBox));
        Assert.False(PendingChoiceStore.TryPeek(out _));
    }

    [Fact]
    public void StreamingBox_Faulted_AcceptsAppendWithoutLeakingAndKeepsErrorText()
    {
        var streamingBox = new AiStreamingDialogueBox(new NPC());
        streamingBox.AppendContent("部分文本", false);
        streamingBox.SetFaulted("（请求发生异常或超时，请检查网络与设置。）");

        Assert.Equal(StreamingDialogueState.Faulted, streamingBox.State);
        Assert.Equal(string.Empty, streamingBox.DisplayedPageText);

        // Faulted 后继续到达的在途 chunk 不得再污染显示文本。
        streamingBox.AppendContent("迟到片段", false);
        Assert.Equal(string.Empty, streamingBox.DisplayedPageText);
    }

    #endregion

    #region 3. AsyncBuilder 源码契约

    [Fact]
    public void AsyncBuilder_MountsStreamingBoxWithoutVanillaPlaceholder()
    {
        var source = SourceOrFail();

        // AI 对白占位统一使用流式框，严禁再创建原版 "   " 占位框。
        Assert.DoesNotContain("new DialogueBox(\"   \")", source);
        Assert.Contains("new AiStreamingDialogueBox(_speakingNpc)", source);
        Assert.Contains("_activePipeline = new StreamTokenPipeline();", source);
    }

    [Fact]
    public void AsyncBuilder_SuccessPath_NeverCallsDrawDialogue()
    {
        var source = SourceOrFail();

        Assert.DoesNotContain("Game1.DrawDialogue(newDialogue)", source);
    }

    [Fact]
    public void AsyncBuilder_ConsumesSegmentsOnMainThreadOnly()
    {
        var source = SourceOrFail();

        // 实体动作必须在主线程分发：ParseAndExecute 只能出现在 OnUpdateTicked 消费循环内。
        Assert.Contains("while (_streamSegmentQueue.TryDequeue(out var segment))", source);
        Assert.Contains("EmbodiedActionParser.ParseAndExecute(_speakingNpc, new[] { \"[\" + segment.Payload + \"]\" });", source);
        Assert.Contains("_streamingDialogueBox?.AppendContent(segment.Payload, false);", source);
    }

    [Fact]
    public void AsyncBuilder_StreamCallback_OnlyFeedsPipelineAndEnqueues()
    {
        var source = SourceOrFail();

        // 后台回调只允许「分流 + 入队」，不得直接触碰 UI / Game1。
        Assert.Contains("private Action<string> BuildStreamCallback()", source);
        Assert.Contains("foreach (StreamSegment segment in pipeline.Feed(chunk))", source);
        Assert.Contains("_streamSegmentQueue.Enqueue(segment);", source);
    }

    [Fact]
    public void AsyncBuilder_Completion_FlushesPipelineAndBindsChoicePayload()
    {
        var source = SourceOrFail();

        Assert.Contains("foreach (StreamSegment tail in pipelineToFlush.Flush())", source);
        Assert.Contains("PendingChoiceStore.Set(new PendingChoiceContext", source);
        Assert.Contains("BoxRef = boxToFinalize", source);
        Assert.Contains("boxToFinalize?.AppendContent(\"\", isComplete: true);", source);
    }

    [Fact]
    public void AsyncBuilder_ErrorPath_FaultsStreamingBoxInPlace()
    {
        var source = SourceOrFail();

        Assert.Contains("if (boxToFault != null && Game1.activeClickableMenu == boxToFault)", source);
        Assert.Contains("boxToFault.SetFaulted(netMsg);", source);
    }

    [Fact]
    public void AsyncBuilder_ResetAndCleanup_ClearSegmentQueueAndPipeline()
    {
        var source = SourceOrFail();

        // 存档切换 / 纪元失效必须清空流式状态，使在途 chunk 停止入队。
        // 置空语句各出现 3 次：字段声明 + Cleanup + ResetState。
        int queueDrains = source.Split("while (_streamSegmentQueue.TryDequeue(out _)) { }").Length - 1;
        int pipelineResets = source.Split("_activePipeline = null;").Length - 1;
        int boxResets = source.Split("_streamingDialogueBox = null;").Length - 1;

        Assert.Equal(2, queueDrains);
        Assert.Equal(3, pipelineResets);
        Assert.Equal(3, boxResets);
    }

    [Fact]
    public void AsyncBuilder_StreamLineTracker_IsNoLongerUsed()
    {
        // raw delta 已直通 StreamTokenPipeline；按行整形中转必须彻底移除。
        var source = SourceOrFail();
        Assert.DoesNotContain("StreamLineTracker", source);

        var service = ReadLlmDialogueServiceSource();
        Assert.True(service != null, "LlmDialogueService.cs not found.");
        Assert.DoesNotContain("new StreamLineTracker()", service);
        Assert.Contains("delta => onStreamingToken(delta)", service);
    }

    private static string ReadLlmDialogueServiceSource()
    {
        var dir = new DirectoryInfo(AppDomain.CurrentDomain.BaseDirectory);
        for (int i = 0; i < 12; i++)
        {
            var src = Path.Combine(dir.FullName, "src", "LLM", "Prompts", "Generation", "LlmDialogueService.cs");
            if (File.Exists(src)) return File.ReadAllText(src);
            if (dir.Parent == null) break;
            dir = dir.Parent;
        }
        return null;
    }

    #endregion
}
