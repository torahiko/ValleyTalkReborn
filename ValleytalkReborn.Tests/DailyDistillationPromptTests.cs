// DailyDistillationPromptTests.cs
// ═══════════════════════════════════════════════════════════════════════════
// DD405-GENERATION：日记蒸馏 Prompt 与严格输出。
//
// 覆盖：
//   1) BuildDailyPrompts 正向契约——中英文资料段四字段（人设定调 / 旧日记延续感受 /
//      时刻标注的对话材料取事实 / 目标日期标签）、任务段输出格式、首次与延续两分支
//      的 [] 语义、采样标志、IsFinal 最终整理语义、纯函数性（语言取自请求捕获值，
//      与进程 locale 无关且输出确定）。
//   2) ParseDailyResult 严格解析——整个 trim 后必须是完整 JSON 数组；拒绝多条 /
//      附加解释 / 非数组 / 非字符串 / 空条目 / 换行 / 控制标签 / 超长（120 UTF-16
//      code units、中文 30 text elements、英文 18 词）/ 缺第一人称标记 / 姓名泄漏
//      （中文显示名完整子串 + ASCII 名完整词边界）；[] → Empty；相同正文允许
//      Success（Unchanged 由提交阶段判定）。
//   3) GenerateDailyAsync——BUG 校验路径（ErrorDetail 以 "BUG:" 开头）、Rows 空 →
//      NoHistory、Provider 参数契约（responseStart="[" / n_predict=256 / NoTools /
//      allowRetry=false）、受控 Provider 双形态：
//        · 支持取消：传入 ct 取消 → Cancelled；仅超时 → Failed/"timeout"（15s 下限
//          由工单合法范围决定，该用例真实等待 ~15 秒）；
//        · 忽略取消：GenerateDailyAsync 不得在底层 Task 结束前完成（不以 WaitAsync
//          弃等），超时令牌触发后迟到文本不接受。
//   受控 Provider 为确定桩，全程无真实网络调用。
// ═══════════════════════════════════════════════════════════════════════════

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using ValleytalkReborn;
using Xunit;

namespace ValleytalkReborn.Tests;

[Collection("StaticGlobalStateCollection")]
public class DailyDistillationPromptTests
{
    // ── 夹具 ──────────────────────────────────────────────────────────────

    private static DailyDistillationSnapshot Snapshot(IReadOnlyList<string> promptLines, bool truncated = false) => new()
    {
        NpcName = "Abigail",
        TargetDay = 1,
        InputFingerprint = "fingerprint",
        QualifyingCount = promptLines.Count,
        UncoveredQualifyingCount = promptLines.Count,
        Rows = new DailyDialogueLine[]
        {
            new() { RowKey = "row-0", SpeakerType = SpeakerType.NPC, Text = "morning!", DialogueType = "dialogue", TimeOfDay = 900, Qualifies = true }
        },
        PromptLines = promptLines,
        InputTruncated = truncated
    };

    private static DailyDistillationRequest Request(
        Llm provider,
        bool isChinese = false,
        string persona = "",
        string existing = null,
        string[] promptLines = null,
        bool truncated = false,
        int timeout = 30,
        bool isFinal = true,
        string npcName = "Abigail",
        string npcDisplayName = "Abigail") => new()
    {
        Provider = provider,
        NpcName = npcName,
        NpcDisplayName = npcDisplayName,
        PersonaSlice = persona,
        ExistingContent = existing,
        TargetDateLabel = isChinese ? "昨日" : "yesterday",
        Snapshot = Snapshot(promptLines ?? new[] { isChinese ? "[9:00] 阿比盖尔：早上好。" : "[9:00] Abigail: morning!" }, truncated),
        IsChinese = isChinese,
        IsFinal = isFinal,
        TimeoutSeconds = timeout
    };

    private static async Task<MemoryExtractResult> RunDailyAsync(DailyDistillationRequest request, CancellationToken ct = default)
    {
        using var scope = TestEnv.UseIsolatedLocale("en");
        return await MemoryExtractService.GenerateDailyAsync(request, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 受控 Provider 桩：记录入口参数，由 handler 决定取消语义与响应。
    /// TaskEnded 在 handler 正常产出响应后置位——若实现以 WaitAsync 弃等，
    /// GenerateDailyAsync 会在 TaskEnded 置位前返回，测试据此证明未弃等。
    /// </summary>
    private class DailyStubLlm : Llm
    {
        private readonly Func<CancellationToken, Task<LlmResponse>> _handler;

        public int CallCount;
        public bool TaskEnded;
        public string LastSystemPrompt;
        public string LastUserPrompt;
        public string LastGameCache;
        public string LastNpcCache;
        public string LastResponseStart;
        public int LastNPredict;
        public string LastCacheContext;
        public bool LastAllowRetry;

        protected DailyStubLlm(Func<CancellationToken, Task<LlmResponse>> handler) { _handler = handler; }

        public static DailyStubLlm Reply(string reply) =>
            new(_ => Task.FromResult(new LlmResponse(reply)));

        public static DailyStubLlm Failing(string errorMessage) =>
            new(_ => Task.FromResult(new LlmResponse(errorMessage, 500)));

        public static DailyStubLlm ProviderError() =>
            new(_ => Task.FromException<LlmResponse>(new HttpRequestException("net down")));

        public static DailyStubLlm SelfTimeout() =>
            new(_ => Task.FromResult(LlmResponse.Timeout()));

        /// <summary>支持取消形态：在传入令牌上挂起，令牌触发即以 OCE 结束（底层 Task 已结束）。</summary>
        public static DailyStubLlm HonoringCancel() =>
            new(async ct =>
            {
                await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
                return null;
            });

        /// <summary>支持取消形态：令牌触发后返回 Provider 取消响应（模仿 llama.cpp 排队取消）。</summary>
        public static DailyStubLlm HonoringCancelWithCancelledResponse() =>
            new(async ct =>
            {
                try
                {
                    await Task.Delay(Timeout.Infinite, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return LlmResponse.Cancelled();
                }
                return null;
            });

        public override bool IsHighlySensoredModel => false;

        public override string ExtraInstructions => string.Empty;

        internal override Task<LlmResponse> RunInference(
            string systemPromptString,
            string gameCacheString,
            string npcCacheString,
            string promptString,
            string responseStart = "",
            int n_predict = 2048,
            string cacheContext = "",
            bool allowRetry = true)
            => throw new InvalidOperationException("Daily path must call RunInferenceAsync");

        internal override async Task<LlmResponse> RunInferenceAsync(
            string systemPromptString,
            string gameCacheString,
            string npcCacheString,
            string promptString,
            CancellationToken ct,
            string responseStart = "",
            int n_predict = 2048,
            string cacheContext = "",
            bool allowRetry = true)
        {
            CallCount++;
            LastSystemPrompt = systemPromptString;
            LastUserPrompt = promptString;
            LastGameCache = gameCacheString;
            LastNpcCache = npcCacheString;
            LastResponseStart = responseStart;
            LastNPredict = n_predict;
            LastCacheContext = cacheContext;
            LastAllowRetry = allowRetry;
            var response = await _handler(ct).ConfigureAwait(false);
            TaskEnded = true;
            return response;
        }

        internal override Dictionary<string, double>[] RunInferenceProbabilities(string fullPrompt, int n_predict = 1)
            => throw new NotImplementedException();
    }

    /// <summary>忽略取消令牌的 Provider 形态（基类委托语义）：令牌触发后仍延迟至 Task 自然结束。</summary>
    private sealed class CancelIgnoringLlm : DailyStubLlm
    {
        public CancelIgnoringLlm() : base(async ct =>
        {
            await Task.Delay(300).ConfigureAwait(false);   // 刻意不观察 ct
            return new LlmResponse("[\"I felt calm today.\"]");
        }) { }
    }

    // ── BuildDailyPrompts：正向 Prompt 契约 ───────────────────────────────

    [Fact]
    public void BuildDailyPrompts_ZhFirstTime_ContainsFourMaterialFields()
    {
        var request = Request(null, isChinese: true, persona: "口癖资料XYZ", npcDisplayName: "阿比盖尔",
            promptLines: new[] { "[9:00] 阿比盖尔：早上好。", "[14:00] 农夫：晚上有空吗？" });

        var (sys, user) = MemoryExtractService.BuildDailyPrompts(request);

        // 人设定调资料段（System）+ 目标日期标签 + 时刻标注对话材料（取事实）。
        Assert.Contains("你就是【阿比盖尔】", sys);
        Assert.Contains("口癖资料XYZ", sys);
        Assert.Contains("昨日", user);
        Assert.Contains("[9:00] 阿比盖尔：早上好。", user);
        Assert.Contains("[14:00] 农夫：晚上有空吗？", user);
        Assert.Contains("用于取事实", user);
        Assert.Contains("<daily_dialogue>", user);

        // 首次分支：[] 合法、无旧日记资料段、任务段定义输出格式、双名禁令。
        Assert.Contains("礼貌路过", user);
        Assert.Contains("[\"日记正文\"]", user);
        Assert.DoesNotContain("旧日记", user);
        Assert.Contains("禁止出现你的名字【阿比盖尔 / Abigail】", user);
    }

    [Fact]
    public void BuildDailyPrompts_ZhWithExisting_ThreadContinuationContract()
    {
        var request = Request(null, isChinese: true, existing: "旧日记正文ABC");

        var (sys, user) = MemoryExtractService.BuildDailyPrompts(request);

        Assert.Contains("### 旧日记（用于延续感受）", user);
        Assert.Contains("旧日记正文ABC", user);
        Assert.Contains("余韵延续", user);
        Assert.Contains("不允许输出 []", user);
        Assert.DoesNotContain("首次落笔", user);
    }

    [Fact]
    public void BuildDailyPrompts_En_MirrorsContract()
    {
        var first = Request(null, isChinese: false, persona: "persona-slice-XYZ");
        var (sysFirst, userFirst) = MemoryExtractService.BuildDailyPrompts(first);
        Assert.Contains("You are Abigail", sysFirst);
        Assert.Contains("persona-slice-XYZ", sysFirst);
        Assert.Contains("yesterday", userFirst);
        Assert.Contains("[9:00] Abigail: morning!", userFirst);
        Assert.Contains("the farmer", userFirst);
        Assert.Contains("at most 18 words", userFirst);
        Assert.DoesNotContain("EXISTING DIARY", userFirst);

        var existing = Request(null, isChinese: false, existing: "old diary line ZZZ");
        var (_, userExisting) = MemoryExtractService.BuildDailyPrompts(existing);
        Assert.Contains("### EXISTING DIARY (thread to continue)", userExisting);
        Assert.Contains("old diary line ZZZ", userExisting);
        Assert.Contains("[] is not allowed", userExisting);
    }

    [Fact]
    public void BuildDailyPrompts_SamplingFlag_FollowsInputTruncated()
    {
        var truncated = Request(null, isChinese: true, truncated: true);
        var (_, userTruncated) = MemoryExtractService.BuildDailyPrompts(truncated);
        Assert.Contains("采样说明", userTruncated);

        var enTruncated = Request(null, isChinese: false, truncated: true);
        var (_, userEnTruncated) = MemoryExtractService.BuildDailyPrompts(enTruncated);
        Assert.Contains("SAMPLING NOTE", userEnTruncated);

        var full = Request(null, isChinese: true, truncated: false);
        var (_, userFull) = MemoryExtractService.BuildDailyPrompts(full);
        Assert.DoesNotContain("采样说明", userFull);
    }

    [Fact]
    public void BuildDailyPrompts_LanguageComesFromRequestNotProcessLocale()
    {
        using var scope = TestEnv.UseIsolatedLocale("en");
        var request = Request(null, isChinese: true, persona: "口癖：哼。");

        var (sys, user) = MemoryExtractService.BuildDailyPrompts(request);
        Assert.Contains("你就是【", sys);
        Assert.Contains("口癖：哼。", sys);
        Assert.Contains("昨日", user);

        // 纯函数：同一请求重复构建输出完全一致。
        var (sys2, user2) = MemoryExtractService.BuildDailyPrompts(request);
        Assert.Equal(sys, sys2);
        Assert.Equal(user, user2);
    }

    [Fact]
    public void BuildDailyPrompts_IsFinal_StatesDayEndedSemantics()
    {
        var final = Request(null, isChinese: true, isFinal: true);
        var (sysFinal, _) = MemoryExtractService.BuildDailyPrompts(final);
        Assert.Contains("已经结束", sysFinal);

        var interim = Request(null, isChinese: true, isFinal: false);
        var (sysInterim, _) = MemoryExtractService.BuildDailyPrompts(interim);
        Assert.Contains("中途整理", sysInterim);
    }

    // ── ParseDailyResult：严格解析 ────────────────────────────────────────

    [Fact]
    public void ParseDailyResult_ZhValid_ReturnsSingleCandidate()
    {
        var result = MemoryExtractService.ParseDailyResult(
            "[\"农夫冒雨给我送来一把伞，嘴上嫌弃，心里却是暖的。\"]",
            isChinese: true, "Abigail", "阿比盖尔");

        Assert.Equal(MemoryExtractStatus.Success, result.Status);
        var candidate = Assert.Single(result.Candidates);
        Assert.Equal("农夫冒雨给我送来一把伞，嘴上嫌弃，心里却是暖的。", candidate);
    }

    [Fact]
    public void ParseDailyResult_EnValid_ReturnsSingleCandidate()
    {
        var result = MemoryExtractService.ParseDailyResult(
            "[\"The farmer helped me mend the fence today and I felt calmer.\"]",
            isChinese: false, "Abigail", "Abigail");

        Assert.Equal(MemoryExtractStatus.Success, result.Status);
        Assert.Equal("The farmer helped me mend the fence today and I felt calmer.", Assert.Single(result.Candidates));
    }

    [Fact]
    public void ParseDailyResult_EmptyArray_ReturnsEmpty()
    {
        var result = MemoryExtractService.ParseDailyResult("[]", isChinese: true, "Abigail", "阿比盖尔");
        Assert.Equal(MemoryExtractStatus.Empty, result.Status);
        Assert.Empty(result.Candidates);
    }

    [Fact]
    public void ParseDailyResult_RejectsMultipleEntries()
    {
        var result = MemoryExtractService.ParseDailyResult(
            "[\"我今天很好。\", \"我也很好。\"]", isChinese: true, "Abigail", "Abigail");
        Assert.Equal(MemoryExtractStatus.Failed, result.Status);
        Assert.Empty(result.Candidates);
    }

    [Fact]
    public void ParseDailyResult_RejectsSurroundingExplanations()
    {
        var prefix = MemoryExtractService.ParseDailyResult(
            "好的：[\"我今天很好。\"]", isChinese: true, "Abigail", "Abigail");
        Assert.Equal(MemoryExtractStatus.Failed, prefix.Status);

        var suffix = MemoryExtractService.ParseDailyResult(
            "[\"我今天很好。\"] 以上就是日记。", isChinese: true, "Abigail", "Abigail");
        Assert.Equal(MemoryExtractStatus.Failed, suffix.Status);
    }

    [Fact]
    public void ParseDailyResult_RejectsNonArrayAndNonStringEntries()
    {
        Assert.Equal(MemoryExtractStatus.Failed,
            MemoryExtractService.ParseDailyResult("我今天心情不错。", true, "Abigail", "Abigail").Status);
        Assert.Equal(MemoryExtractStatus.Failed,
            MemoryExtractService.ParseDailyResult("{\"entry\": \"我今天心情不错。\"}", true, "Abigail", "Abigail").Status);
        Assert.Equal(MemoryExtractStatus.Failed,
            MemoryExtractService.ParseDailyResult("[42]", true, "Abigail", "Abigail").Status);
        Assert.Equal(MemoryExtractStatus.Failed,
            MemoryExtractService.ParseDailyResult("[null]", true, "Abigail", "Abigail").Status);
        Assert.Equal(MemoryExtractStatus.Failed,
            MemoryExtractService.ParseDailyResult("[[\"我今天心情不错。\"]]", true, "Abigail", "Abigail").Status);
    }

    [Fact]
    public void ParseDailyResult_RejectsEmptyOrWhitespaceEntry()
    {
        Assert.Equal(MemoryExtractStatus.Failed,
            MemoryExtractService.ParseDailyResult("[\"\"]", true, "Abigail", "Abigail").Status);
        Assert.Equal(MemoryExtractStatus.Failed,
            MemoryExtractService.ParseDailyResult("[\"   \"]", true, "Abigail", "Abigail").Status);
    }

    [Fact]
    public void ParseDailyResult_RejectsMultilineEntry()
    {
        var result = MemoryExtractService.ParseDailyResult(
            "[\"第一行\\n第二行\"]", isChinese: true, "Abigail", "Abigail");
        Assert.Equal(MemoryExtractStatus.Failed, result.Status);
    }

    [Fact]
    public void ParseDailyResult_RejectsControlTags()
    {
        Assert.Equal(MemoryExtractStatus.Failed,
            MemoryExtractService.ParseDailyResult("[\"我今天$m真的高兴\"]", true, "Abigail", "Abigail").Status);
        Assert.Equal(MemoryExtractStatus.Failed,
            MemoryExtractService.ParseDailyResult("[\"[ACTION:wave] 我今天来了\"]", true, "Abigail", "Abigail").Status);
        Assert.Equal(MemoryExtractStatus.Failed,
            MemoryExtractService.ParseDailyResult("[\"${lover} 我很想你\"]", true, "Abigail", "Abigail").Status);
    }

    [Fact]
    public void ParseDailyResult_RejectsOverlongAndKeepsBoundaries()
    {
        // 中文 text element 上限：30 通过，31 拒绝（均远低于 120 code units）。
        var at30 = MemoryExtractService.ParseDailyResult(
            "[\"" + "我" + new string('呀', 29) + "\"]", true, "Abigail", "Abigail");
        Assert.Equal(MemoryExtractStatus.Success, at30.Status);

        var at31 = MemoryExtractService.ParseDailyResult(
            "[\"" + "我" + new string('呀', 30) + "\"]", true, "Abigail", "Abigail");
        Assert.Equal(MemoryExtractStatus.Failed, at31.Status);

        // 120 UTF-16 code unit 上限：英文单词数极少但字符数超限仍拒绝。
        var overUnits = MemoryExtractService.ParseDailyResult(
            "[\"I " + new string('x', 120) + "\"]", false, "Abigail", "Abigail");
        Assert.Equal(MemoryExtractStatus.Failed, overUnits.Status);
    }

    [Fact]
    public void ParseDailyResult_RejectsMissingFirstPersonMarker()
    {
        // 中文：缺文字"我"。
        var zh = MemoryExtractService.ParseDailyResult(
            "[\"今天天气很好，心情也不错。\"]", true, "Abigail", "Abigail");
        Assert.Equal(MemoryExtractStatus.Failed, zh.Status);

        // 英文：18 词上限内但无独立 I/me/my/mine。
        var en = MemoryExtractService.ParseDailyResult(
            "[\"the farmer waved hello and the whole market smelled like fresh bread and warm pie this bright morning\"]",
            false, "Abigail", "Abigail");
        Assert.Equal(MemoryExtractStatus.Failed, en.Status);

        // "myself" 不是独立词 my —— 独立词边界要求。
        var myselfOnly = MemoryExtractService.ParseDailyResult(
            "[\"the farmer and myself fixed the coop together for hours until sunset\"]",
            false, "Abigail", "Abigail");
        Assert.Equal(MemoryExtractStatus.Failed, myselfOnly.Status);
    }

    [Fact]
    public void ParseDailyResult_NameLeak_ZhDisplayNameFullSubstring()
    {
        var leak = MemoryExtractService.ParseDailyResult(
            "[\"阿比盖尔今天对我很好。\"]", isChinese: true, "Abigail", "阿比盖尔");
        Assert.Equal(MemoryExtractStatus.Failed, leak.Status);

        var clean = MemoryExtractService.ParseDailyResult(
            "[\"我今天心情很平静。\"]", isChinese: true, "Abigail", "阿比盖尔");
        Assert.Equal(MemoryExtractStatus.Success, clean.Status);
    }

    [Fact]
    public void ParseDailyResult_NameLeak_AsciiFullWordBoundary()
    {
        // 完整词命中（忽略大小写）→ 拒绝。
        Assert.Equal(MemoryExtractStatus.Failed,
            MemoryExtractService.ParseDailyResult("[\"I met Abigail by the river today.\"]", false, "Abigail", "Abigail").Status);
        Assert.Equal(MemoryExtractStatus.Failed,
            MemoryExtractService.ParseDailyResult("[\"I met abigail by the river today.\"]", false, "Abigail", "Abigail").Status);

        // 词边界：更长的不同单词不构成泄漏。
        var differentWord = MemoryExtractService.ParseDailyResult(
            "[\"I met Abigaille by the river today.\"]", false, "Abigail", "Abigail");
        Assert.Equal(MemoryExtractStatus.Success, differentWord.Status);
    }

    [Fact]
    public void ParseDailyResult_EmptyNames_SkipNameCheck()
    {
        var result = MemoryExtractService.ParseDailyResult(
            "[\"我今天心情很平静。\"]", isChinese: true, npcName: null, npcDisplayName: "   ");
        Assert.Equal(MemoryExtractStatus.Success, result.Status);
    }

    // ── GenerateDailyAsync：请求校验与调度语义 ────────────────────────────

    [Fact]
    public async Task GenerateDailyAsync_InvalidRequestFields_ReturnsBugPrefix()
    {
        var nullResult = await MemoryExtractService.GenerateDailyAsync(null, CancellationToken.None);
        Assert.Equal(MemoryExtractStatus.Failed, nullResult.Status);
        Assert.StartsWith("BUG:", nullResult.ErrorDetail);

        var noProvider = await RunDailyAsync(Request(null));
        Assert.StartsWith("BUG:", noProvider.ErrorDetail);

        var emptyName = await RunDailyAsync(Request(DailyStubLlm.Reply("[]"), npcName: "   "));
        Assert.StartsWith("BUG:", emptyName.ErrorDetail);

        var noSnapshot = await RunDailyAsync(new DailyDistillationRequest
        {
            Provider = DailyStubLlm.Reply("[]"),
            NpcName = "Abigail",
            NpcDisplayName = "Abigail",
            Snapshot = null,
            TargetDateLabel = "yesterday",
            TimeoutSeconds = 30
        });
        Assert.StartsWith("BUG:", noSnapshot.ErrorDetail);

        var shortTimeout = await RunDailyAsync(Request(DailyStubLlm.Reply("[]"), timeout: 14));
        Assert.StartsWith("BUG:", shortTimeout.ErrorDetail);

        var longTimeout = await RunDailyAsync(Request(DailyStubLlm.Reply("[]"), timeout: 121));
        Assert.StartsWith("BUG:", longTimeout.ErrorDetail);
    }

    [Fact]
    public async Task GenerateDailyAsync_EmptyRows_ReturnsNoHistoryWithoutProviderCall()
    {
        var stub = DailyStubLlm.Reply("[]");
        var request = new DailyDistillationRequest
        {
            Provider = stub,
            NpcName = "Abigail",
            NpcDisplayName = "Abigail",
            PersonaSlice = "",
            ExistingContent = null,
            TargetDateLabel = "yesterday",
            Snapshot = new DailyDistillationSnapshot
            {
                NpcName = "Abigail", TargetDay = 1, InputFingerprint = "fp",
                Rows = Array.Empty<DailyDialogueLine>(), PromptLines = Array.Empty<string>()
            },
            IsChinese = false,
            IsFinal = true,
            TimeoutSeconds = 30
        };

        var result = await RunDailyAsync(request);

        Assert.Equal(MemoryExtractStatus.NoHistory, result.Status);
        Assert.Equal(0, stub.CallCount);
    }

    [Fact]
    public async Task GenerateDailyAsync_Success_PassesProviderContractArgs()
    {
        var stub = DailyStubLlm.Reply("[\"I finally felt at peace by the river today.\"]");
        var request = Request(stub, persona: "tone-anchor");

        var result = await RunDailyAsync(request);

        Assert.Equal(MemoryExtractStatus.Success, result.Status);
        Assert.Equal("I finally felt at peace by the river today.", Assert.Single(result.Candidates));
        Assert.Equal(1, stub.CallCount);
        Assert.Equal("[", stub.LastResponseStart);
        Assert.Equal(256, stub.LastNPredict);
        Assert.Equal(LlmContextTypes.NoTools, stub.LastCacheContext);
        Assert.False(stub.LastAllowRetry);
        Assert.Equal("", stub.LastGameCache);
        Assert.Equal("", stub.LastNpcCache);
        Assert.Contains("tone-anchor", stub.LastSystemPrompt);
        Assert.Contains("yesterday", stub.LastUserPrompt);
    }

    [Fact]
    public async Task GenerateDailyAsync_EmptyArray_NoExisting_ReturnsEmpty()
    {
        var result = await RunDailyAsync(Request(DailyStubLlm.Reply("[]")));
        Assert.Equal(MemoryExtractStatus.Empty, result.Status);
    }

    [Fact]
    public async Task GenerateDailyAsync_EmptyArray_WithExisting_ReturnsFailed()
    {
        var result = await RunDailyAsync(Request(DailyStubLlm.Reply("[]"), existing: "old diary"));
        Assert.Equal(MemoryExtractStatus.Failed, result.Status);
        Assert.Equal("empty array with existing diary", result.ErrorDetail);
        Assert.Empty(result.Candidates);
    }

    [Fact]
    public async Task GenerateDailyAsync_SameContentAsExisting_StillSuccess()
    {
        // 相同正文允许 Success——Unchanged 由提交阶段（DD404）判定。
        const string diary = "I feel the same as my old diary.";
        var result = await RunDailyAsync(Request(DailyStubLlm.Reply("[\"" + diary + "\"]"), existing: diary));
        Assert.Equal(MemoryExtractStatus.Success, result.Status);
        Assert.Equal(diary, Assert.Single(result.Candidates));
    }

    [Fact]
    public async Task GenerateDailyAsync_PreCancelled_HonoringProvider_ReturnsCancelled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var oceStub = DailyStubLlm.HonoringCancel();
        Assert.Equal(MemoryExtractStatus.Cancelled, (await RunDailyAsync(Request(oceStub), cts.Token)).Status);

        var cancelledResponseStub = DailyStubLlm.HonoringCancelWithCancelledResponse();
        Assert.Equal(MemoryExtractStatus.Cancelled, (await RunDailyAsync(Request(cancelledResponseStub), cts.Token)).Status);
    }

    [Fact]
    public async Task GenerateDailyAsync_PreCancelled_IgnoringProvider_WaitsForUnderlyingTask()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var stub = new CancelIgnoringLlm();

        var result = await RunDailyAsync(Request(stub), cts.Token);

        // 传入 ct 取消 → Cancelled；且底层 Task 已结束才返回（未以 WaitAsync 弃等）。
        Assert.Equal(MemoryExtractStatus.Cancelled, result.Status);
        Assert.True(stub.TaskEnded);
    }

    [Fact]
    public async Task GenerateDailyAsync_Timeout_HonoringProvider_ReturnsFailedTimeout()
    {
        // 工单合法范围下限 15s——本用例真实等待约 15 秒。
        var stub = DailyStubLlm.HonoringCancel();

        var result = await RunDailyAsync(Request(stub, timeout: 15));

        Assert.Equal(MemoryExtractStatus.Failed, result.Status);
        Assert.Equal("timeout", result.ErrorDetail);
        Assert.Empty(result.Candidates);
    }

    [Fact]
    public async Task GenerateDailyAsync_ProviderFailureResponse_ReturnsFailed()
    {
        var result = await RunDailyAsync(Request(DailyStubLlm.Failing("boom")));
        Assert.Equal(MemoryExtractStatus.Failed, result.Status);
        Assert.Contains("boom", result.ErrorDetail);
    }

    [Fact]
    public async Task GenerateDailyAsync_ProviderException_ReturnsFailedBoundary()
    {
        var result = await RunDailyAsync(Request(DailyStubLlm.ProviderError()));
        Assert.Equal(MemoryExtractStatus.Failed, result.Status);
        Assert.Contains("net down", result.ErrorDetail);
        Assert.False(result.ErrorDetail.StartsWith("BUG:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GenerateDailyAsync_ProviderSelfTimeout_NoToken_ReturnsFailedBoundary()
    {
        var result = await RunDailyAsync(Request(DailyStubLlm.SelfTimeout()));
        Assert.Equal(MemoryExtractStatus.Failed, result.Status);
        Assert.NotEqual("timeout", result.ErrorDetail);
    }
}
