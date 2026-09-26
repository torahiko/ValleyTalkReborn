// PromptRuntimeAssemblyTests.cs
// PROMPT-ARCH-02 — runtime assembly contract tests for the two Provider-facing
// helpers (BuildRuntimeSystemPrompt / BuildRuntimeConversationPrompt) using
// sentinel prompt values. Both inference paths share the same helpers, so
// byte-identity across invocations pins the streaming/non-streaming contract.
// No HTTP requests, no active LLM Provider required.

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text;
using StardewModdingAPI;
using StardewModdingAPI.Framework.Logging;
using StardewValley;
using ValleytalkReborn;
using Xunit;

[Collection("StaticGlobalStateCollection")]
public class PromptRuntimeAssemblyTests : IDisposable
{
    private readonly ModConfig _originalConfig;
    private readonly LocalizedContentManager.LanguageCode _originalLanguageCode;
    private readonly IMonitor _originalLogMonitor;

    public PromptRuntimeAssemblyTests()
    {
        _originalConfig = ModEntry.Config;
        _originalLanguageCode = LocalizedContentManager.CurrentLanguageCode;
        _originalLogMonitor = Log.Logger.Monitor;
    }

    public void Dispose()
    {
        ModEntry.Config = _originalConfig;
        LocalizedContentManager.CurrentLanguageCode = _originalLanguageCode;
        Log.Initialize(_originalLogMonitor);
    }

    private static void SetLanguageEnvironment(string languageOverride, LocalizedContentManager.LanguageCode gameLanguage)
    {
        ModEntry.Config = new ModConfig { LanguageOverride = languageOverride };
        LocalizedContentManager.CurrentLanguageCode = gameLanguage;
    }

    private static void SetPrivateField(Prompts target, string fieldName, object value)
    {
        typeof(Prompts).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)
            ?.SetValue(target, value);
    }

    private static InjectionPlan MakeSentinelPlan()
    {
        return new InjectionPlan
        {
            Tier1Snapshot = new Tier1SnapshotContext(new Dictionary<string, string>
            {
                [Tier1BlockIds.GameState] = "TIER1-SENTINEL",
            }),
            ActiveImpulses = new Dictionary<string, string>
            {
                [Tier2bBlockIds.Gossip] = "TIER2B-SENTINEL",
            },
        };
    }

    private static Prompts MakeSentinelPrompts()
    {
        var prompts = (Prompts)FormatterServices.GetUninitializedObject(typeof(Prompts));
        prompts.SystemPrompt = "SYS-SENTINEL";
        prompts.GameConstantContext = "GAMECONST-SENTINEL";
        prompts.NpcConstantContext = "NPCCONST-SENTINEL";
        prompts.Instructions = "INSTR-SENTINEL";
        prompts.Command = "CMD-SENTINEL";
        prompts.CorePrompt = "CORE-SENTINEL";
        prompts.ResponseStart = "RESP-SENTINEL";
        SetPrivateField(prompts, "_corePlan", MakeSentinelPlan());
        SetPrivateField(prompts, "_sessionContinuitySegment", "CONTINUITY-SENTINEL");
        SetPrivateField(prompts, "_currentConversationSegment", "CONVERSATION-SENTINEL");
        return prompts;
    }

    /// <summary>
    /// 为去重回归测试构建 Prompts：SystemPrompt 与 DynamicContext 均以 "- " 列表行注入。
    /// DynamicContext 由 GameConstantContext 提供（Tier 1/2b 置空），便于精确控制动态列表内容。
    /// </summary>
    private static Prompts MakeDedupPrompts(string systemPrompt, string dynamicText)
    {
        var prompts = (Prompts)FormatterServices.GetUninitializedObject(typeof(Prompts));
        prompts.SystemPrompt = systemPrompt;
        prompts.GameConstantContext = dynamicText;
        prompts.NpcConstantContext = string.Empty;
        prompts.Instructions = "INSTR";
        prompts.Command = "CMD";
        prompts.CorePrompt = "CORE";
        prompts.ResponseStart = "RESP";
        SetPrivateField(prompts, "_corePlan", new InjectionPlan
        {
            Tier1Snapshot = new Tier1SnapshotContext(new Dictionary<string, string>()),
            ActiveImpulses = new Dictionary<string, string>(),
        });
        SetPrivateField(prompts, "_sessionContinuitySegment", "CONTINUITY-SEG");
        SetPrivateField(prompts, "_currentConversationSegment", "CONVERSATION-SEG");
        return prompts;
    }

    // ── BuildRuntimeSystemPrompt：SystemPrompt + "\n\n" + StaticInstructionContext ──

    [Fact]
    public void BuildRuntimeSystemPrompt_IsSystemPlusStaticInstructionContext()
    {
        SetLanguageEnvironment(string.Empty, LocalizedContentManager.LanguageCode.en);
        var prompts = MakeSentinelPrompts();

        string result = LlmDialogueService.BuildRuntimeSystemPrompt(prompts);

        Assert.Equal("SYS-SENTINEL\n\nINSTR-SENTINEL\n\nCMD-SENTINEL", result);
        Assert.Equal(prompts.SystemPrompt + "\n\n" + prompts.StaticInstructionContext, result);
    }

    [Fact]
    public void BuildRuntimeSystemPrompt_ExcludesDynamicConversationAndResponseStart()
    {
        SetLanguageEnvironment(string.Empty, LocalizedContentManager.LanguageCode.en);
        var prompts = MakeSentinelPrompts();

        string result = LlmDialogueService.BuildRuntimeSystemPrompt(prompts);

        Assert.DoesNotContain("GAMECONST-SENTINEL", result);
        Assert.DoesNotContain("NPCCONST-SENTINEL", result);
        Assert.DoesNotContain("TIER1-SENTINEL", result);
        Assert.DoesNotContain("TIER2B-SENTINEL", result);
        Assert.DoesNotContain("CONTINUITY-SENTINEL", result);
        Assert.DoesNotContain("CONVERSATION-SENTINEL", result);
        Assert.DoesNotContain("RESP-SENTINEL", result);
        Assert.DoesNotContain("CORE-SENTINEL", result);
    }

    // ── BuildRuntimeConversationPrompt：DynamicContext + "\n\n" + ConversationStream ──

    [Fact]
    public void BuildRuntimeConversationPrompt_IsDynamicContextPlusConversationStream()
    {
        SetLanguageEnvironment(string.Empty, LocalizedContentManager.LanguageCode.en);
        var prompts = MakeSentinelPrompts();

        string result = LlmDialogueService.BuildRuntimeConversationPrompt(prompts);

        Assert.Equal(prompts.DynamicContext + "\n\n" + prompts.ConversationStream, result);
        Assert.StartsWith("GAMECONST-SENTINEL\n\nNPCCONST-SENTINEL\n\nTIER1-SENTINEL", result);
        Assert.Contains("CONTINUITY-SENTINEL\n\nCONVERSATION-SENTINEL", result);
        Assert.Contains("<response_trigger>", result);
    }

    // ── PROMPT-ARCH-02A: 运行时对话载荷去重回归（重复动态条目仅在匹配 SystemPrompt 时被移除） ──

    [Fact]
    public void BuildRuntimeConversationPrompt_DuplicateOfSystemPrompt_IsOmitted()
    {
        SetLanguageEnvironment(string.Empty, LocalizedContentManager.LanguageCode.en);
        var prompts = MakeDedupPrompts(
            systemPrompt: "- SHARED-GOSSIP-1234",
            dynamicText: "- SHARED-GOSSIP-1234\n- UNIQUE-DYNAMIC-5678");

        string result = LlmDialogueService.BuildRuntimeConversationPrompt(prompts);

        Assert.DoesNotContain("SHARED-GOSSIP-1234", result);
        Assert.Contains("UNIQUE-DYNAMIC-5678", result);
    }

    [Fact]
    public void BuildRuntimeConversationPrompt_Tier2bDuplicateOfSystemPrompt_IsOmitted()
    {
        SetLanguageEnvironment(string.Empty, LocalizedContentManager.LanguageCode.en);
        var prompts = (Prompts)FormatterServices.GetUninitializedObject(typeof(Prompts));
        prompts.SystemPrompt = "- TIER2B-DUP-1234";
        prompts.GameConstantContext = "- UNIQUE-GAME-5678";
        prompts.NpcConstantContext = string.Empty;
        prompts.Instructions = "INSTR";
        prompts.Command = "CMD";
        prompts.CorePrompt = "CORE";
        prompts.ResponseStart = "RESP";
        SetPrivateField(prompts, "_corePlan", new InjectionPlan
        {
            Tier1Snapshot = new Tier1SnapshotContext(new Dictionary<string, string>()),
            ActiveImpulses = new Dictionary<string, string>
            {
                [Tier2bBlockIds.Gossip] = "- TIER2B-DUP-1234",
            },
        });
        SetPrivateField(prompts, "_sessionContinuitySegment", "CONTINUITY-SEG");
        SetPrivateField(prompts, "_currentConversationSegment", "CONVERSATION-SEG");

        string result = LlmDialogueService.BuildRuntimeConversationPrompt(prompts);

        Assert.DoesNotContain("TIER2B-DUP-1234", result);
        Assert.Contains("UNIQUE-GAME-5678", result);
    }

    [Fact]
    public void BuildRuntimeConversationPrompt_UniqueDynamicEntry_Retained()
    {
        SetLanguageEnvironment(string.Empty, LocalizedContentManager.LanguageCode.en);
        var prompts = MakeDedupPrompts(
            systemPrompt: "- ONLY-IN-SYSTEM-1111",
            dynamicText: "- ONLY-IN-DYNAMIC-2222");

        string result = LlmDialogueService.BuildRuntimeConversationPrompt(prompts);

        Assert.Contains("ONLY-IN-DYNAMIC-2222", result);
        Assert.DoesNotContain("ONLY-IN-SYSTEM-1111", result);
    }

    [Fact]
    public void BuildRuntimeConversationPrompt_ConversationStream_Unchanged()
    {
        SetLanguageEnvironment(string.Empty, LocalizedContentManager.LanguageCode.en);
        var prompts = MakeDedupPrompts(
            systemPrompt: "- SHARED-AAA-1111",
            dynamicText: "- SHARED-AAA-1111\n- UNIQUE-BBB-2222");
        string before = prompts.ConversationStream;

        string result = LlmDialogueService.BuildRuntimeConversationPrompt(prompts);

        Assert.Equal(before, prompts.ConversationStream);
        Assert.Contains(before, result);
    }

    [Fact]
    public void BuildRuntimeConversationPrompt_SystemPrompt_Unchanged()
    {
        SetLanguageEnvironment(string.Empty, LocalizedContentManager.LanguageCode.en);
        string systemPrompt = "- SHARED-CCC-3333\n- SYSTEM-ONLY-4444";
        var prompts = MakeDedupPrompts(
            systemPrompt: systemPrompt,
            dynamicText: "- SHARED-CCC-3333\n- UNIQUE-DDD-5555");

        LlmDialogueService.BuildRuntimeConversationPrompt(prompts);

        Assert.Equal(systemPrompt, prompts.SystemPrompt);
    }

    [Fact]
    public void BuildRuntimeConversationPrompt_DedupDeterministic_AcrossBothPaths()
    {
        SetLanguageEnvironment(string.Empty, LocalizedContentManager.LanguageCode.en);
        var prompts = MakeDedupPrompts(
            systemPrompt: "- SHARED-EEE-6666",
            dynamicText: "- SHARED-EEE-6666\n- UNIQUE-FFF-7777");

        string streaming = LlmDialogueService.BuildRuntimeConversationPrompt(prompts);
        string nonStreaming = LlmDialogueService.BuildRuntimeConversationPrompt(prompts);

        Assert.Equal(nonStreaming, streaming);
        Assert.DoesNotContain("SHARED-EEE-6666", streaming);
        Assert.Contains("UNIQUE-FFF-7777", streaming);
    }

    [Fact]
    public void BuildRuntimeConversationPrompt_ExcludesSystemStaticAndResponseStart()
    {
        SetLanguageEnvironment(string.Empty, LocalizedContentManager.LanguageCode.en);
        var prompts = MakeSentinelPrompts();

        string result = LlmDialogueService.BuildRuntimeConversationPrompt(prompts);

        Assert.DoesNotContain("SYS-SENTINEL", result);
        Assert.DoesNotContain("INSTR-SENTINEL", result);
        Assert.DoesNotContain("CMD-SENTINEL", result);
        Assert.DoesNotContain("RESP-SENTINEL", result);
        Assert.DoesNotContain("CORE-SENTINEL", result);
        int iGame = result.IndexOf("GAMECONST-SENTINEL", StringComparison.Ordinal);
        int iNpc = result.IndexOf("NPCCONST-SENTINEL", StringComparison.Ordinal);
        int iTier1 = result.IndexOf("TIER1-SENTINEL", StringComparison.Ordinal);
        int iTier2b = result.IndexOf("TIER2B-SENTINEL", StringComparison.Ordinal);
        int iContinuity = result.IndexOf("CONTINUITY-SENTINEL", StringComparison.Ordinal);
        int iConversation = result.IndexOf("CONVERSATION-SENTINEL", StringComparison.Ordinal);
        Assert.True(iGame < iNpc && iNpc < iTier1 && iTier1 < iTier2b && iTier2b < iContinuity && iContinuity < iConversation,
            "RuntimeConversationPrompt must keep DynamicContext before ConversationStream");
    }

    // ── 双路径一致性：流式与非流式共用同一组助手，输出必须字节一致 ──

    [Fact]
    public void RuntimeSections_StreamingAndNonStreamingAreByteIdentical()
    {
        SetLanguageEnvironment(string.Empty, LocalizedContentManager.LanguageCode.en);
        var prompts = MakeSentinelPrompts();

        string streamingSystem = LlmDialogueService.BuildRuntimeSystemPrompt(prompts);
        string streamingConversation = LlmDialogueService.BuildRuntimeConversationPrompt(prompts);
        string nonStreamingSystem = LlmDialogueService.BuildRuntimeSystemPrompt(prompts);
        string nonStreamingConversation = LlmDialogueService.BuildRuntimeConversationPrompt(prompts);

        Assert.Equal(nonStreamingSystem, streamingSystem);
        Assert.Equal(nonStreamingConversation, streamingConversation);
    }

    // ── 失败路径 ──

    [Fact]
    public void RuntimeHelpers_NullPrompts_ThrowArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => LlmDialogueService.BuildRuntimeSystemPrompt(null));
        Assert.Throws<ArgumentNullException>(() => LlmDialogueService.BuildRuntimeConversationPrompt(null));
    }

    [Fact]
    public void BuildRuntimeSystemPrompt_AllSegmentsEmpty_PreservesEmptyValue()
    {
        SetLanguageEnvironment(string.Empty, LocalizedContentManager.LanguageCode.en);
        var prompts = (Prompts)FormatterServices.GetUninitializedObject(typeof(Prompts));
        prompts.SystemPrompt = string.Empty;
        prompts.Instructions = string.Empty;
        prompts.Command = string.Empty;

        Assert.Equal(string.Empty, LlmDialogueService.BuildRuntimeSystemPrompt(prompts));
    }

    [Fact]
    public void BuildRuntimeConversationPrompt_EmptyDynamicContext_OmitsItWithoutFallback()
    {
        SetLanguageEnvironment(string.Empty, LocalizedContentManager.LanguageCode.en);
        var prompts = (Prompts)FormatterServices.GetUninitializedObject(typeof(Prompts));
        prompts.SystemPrompt = string.Empty;
        prompts.GameConstantContext = string.Empty;
        prompts.NpcConstantContext = string.Empty;
        SetPrivateField(prompts, "_corePlan", new InjectionPlan
        {
            Tier1Snapshot = new Tier1SnapshotContext(new Dictionary<string, string>()),
            ActiveImpulses = new Dictionary<string, string>(),
        });

        string result = LlmDialogueService.BuildRuntimeConversationPrompt(prompts);

        // 动态段全空 → 仅剩 ConversationStream（触发后缀），无捏造的动态内容
        Assert.Equal(prompts.ConversationStream, result);
    }

    // ── LogDebugRequest：运行时拓扑展示顺序与统计口径 ──

    private sealed class CapturingMonitor : IMonitor
    {
        public readonly StringBuilder Captured = new StringBuilder();
        public bool IsVerbose => false;
        public void Log(string message, LogLevel level) => Captured.AppendLine(message);
        public void LogOnce(string message, LogLevel level) => Captured.AppendLine(message);
        public void VerboseLog(string message) { }
        public void VerboseLog(ref VerboseLogStringHandler handler) { }
    }

    [Fact]
    public void LogDebugRequest_DisplaysRuntimeSectionsInCallOrder()
    {
        SetLanguageEnvironment(string.Empty, LocalizedContentManager.LanguageCode.en);
        var prompts = MakeSentinelPrompts();
        var character = (ValleytalkReborn.Character)FormatterServices.GetUninitializedObject(typeof(ValleytalkReborn.Character));

        var capture = new CapturingMonitor();
        Log.Initialize(capture);
        var method = typeof(LlmDialogueService).GetMethod("LogDebugRequest",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        method.Invoke(LlmDialogueService.Instance, new object[] { character, prompts, 1 });

        string captured = capture.Captured.ToString();
        int iRuntimeSystem = captured.IndexOf("[RuntimeSystemPrompt]", StringComparison.Ordinal);
        int iRuntimeConversation = captured.IndexOf("[RuntimeConversationPrompt]", StringComparison.Ordinal);
        int iResponse = captured.IndexOf("[ResponseStart]", StringComparison.Ordinal);
        Assert.True(iRuntimeSystem >= 0 && iRuntimeConversation >= 0 && iResponse >= 0,
            $"All three runtime section labels must be present. Captured:\n{captured}");
        Assert.True(iRuntimeSystem < iRuntimeConversation, "RuntimeSystemPrompt must precede RuntimeConversationPrompt");
        Assert.True(iRuntimeConversation < iResponse, "RuntimeConversationPrompt must precede ResponseStart");
        Assert.Contains("Total Runtime Length", captured);
        Assert.DoesNotContain("CorePrompt Length", captured);
    }
}
