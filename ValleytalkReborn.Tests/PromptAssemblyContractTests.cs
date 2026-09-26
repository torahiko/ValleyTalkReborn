// PromptAssemblyContractTests.cs
// PROMPT-ARCH-01 — contract tests for unified locale resolution (ResolveIsChinese),
// JoinPromptSegments joining semantics, and the named prompt boundaries
// (StaticInstructionContext / DynamicContext / ConversationStream), plus the
// LogDebugRequest boundary-label ordering. Pure boundary tests: Prompts instances
// are built field-by-field via internal setters / reflection so no game state is
// touched; only ModEntry.Config, LocalizedContentManager.CurrentLanguageCode and
// the Log monitor are mutated (snapshot + restore, serialized collection).

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text;
using StardewModdingAPI;
using StardewModdingAPI.Framework.Logging;
using StardewValley;
using ValleytalkReborn;
using ValleytalkReborn.Tests;
using Xunit;

[Collection("StaticGlobalStateCollection")]
public class PromptAssemblyContractTests : IDisposable
{
    private readonly ModConfig _originalConfig;
    private readonly LocalizedContentManager.LanguageCode _originalLanguageCode;
    private readonly IMonitor _originalLogMonitor;

    public PromptAssemblyContractTests()
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

    // ── ResolveIsChinese：LanguageOverride 优先，回退游戏语言代码 ──

    [Fact]
    public void ResolveIsChinese_ZhOverride_ReturnsTrue()
    {
        SetLanguageEnvironment("zh", LocalizedContentManager.LanguageCode.en);
        Assert.True(Prompts.ResolveIsChinese());
    }

    [Fact]
    public void ResolveIsChinese_ZhCnOverride_ReturnsTrue()
    {
        SetLanguageEnvironment("zh-CN", LocalizedContentManager.LanguageCode.en);
        Assert.True(Prompts.ResolveIsChinese());
    }

    [Fact]
    public void ResolveIsChinese_EnOverride_ReturnsFalseEvenWhenGameIsZh()
    {
        SetLanguageEnvironment("en", LocalizedContentManager.LanguageCode.zh);
        Assert.False(Prompts.ResolveIsChinese());
    }

    [Fact]
    public void ResolveIsChinese_EmptyOverride_GameZh_ReturnsTrue()
    {
        SetLanguageEnvironment(string.Empty, LocalizedContentManager.LanguageCode.zh);
        Assert.True(Prompts.ResolveIsChinese());
    }

    [Fact]
    public void ResolveIsChinese_EmptyOverride_NonZhGame_ReturnsFalse()
    {
        SetLanguageEnvironment(string.Empty, LocalizedContentManager.LanguageCode.en);
        Assert.False(Prompts.ResolveIsChinese());
    }

    [Fact]
    public void ResolveIsChinese_NullOverride_FallsBackToGameLanguage()
    {
        SetLanguageEnvironment(null, LocalizedContentManager.LanguageCode.zh);
        Assert.True(Prompts.ResolveIsChinese());

        SetLanguageEnvironment(null, LocalizedContentManager.LanguageCode.en);
        Assert.False(Prompts.ResolveIsChinese());
    }

    [Fact]
    public void ResolveIsChinese_WhitespaceOverride_FallsBackToGameLanguage()
    {
        SetLanguageEnvironment("   ", LocalizedContentManager.LanguageCode.zh);
        Assert.True(Prompts.ResolveIsChinese());
    }

    [Fact]
    public void ResolveIsChinese_UnsupportedOverrideValue_ReturnsFalse()
    {
        SetLanguageEnvironment("default", LocalizedContentManager.LanguageCode.zh);
        Assert.False(Prompts.ResolveIsChinese());

        SetLanguageEnvironment("mod", LocalizedContentManager.LanguageCode.zh);
        Assert.False(Prompts.ResolveIsChinese());
    }

    // ── JoinPromptSegments：null/空段忽略，段间恰好两个换行符 ──

    [Fact]
    public void JoinPromptSegments_AllNullOrEmpty_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, Prompts.JoinPromptSegments());
        Assert.Equal(string.Empty, Prompts.JoinPromptSegments(null, null));
        Assert.Equal(string.Empty, Prompts.JoinPromptSegments(string.Empty, ""));
        Assert.Equal(string.Empty, Prompts.JoinPromptSegments(null, string.Empty, null));
    }

    [Fact]
    public void JoinPromptSegments_OmitsNullOrEmptySegments()
    {
        Assert.Equal("A", Prompts.JoinPromptSegments(null, "A"));
        Assert.Equal("B", Prompts.JoinPromptSegments(string.Empty, "B", ""));
        Assert.Equal("A", Prompts.JoinPromptSegments("A", null));
    }

    [Fact]
    public void JoinPromptSegments_InsertsExactlyTwoNewlinesBetweenSegments()
    {
        Assert.Equal("A\n\nB", Prompts.JoinPromptSegments("A", "B"));
        Assert.Equal("A\n\nB", Prompts.JoinPromptSegments("A", null, "B"));
        Assert.Equal("A\n\nB\n\nC", Prompts.JoinPromptSegments("A", "", "B", null, "C"));
    }

    [Fact]
    public void JoinPromptSegments_PreservesSegmentBytes()
    {
        Assert.Equal(" A \n\nB\nC", Prompts.JoinPromptSegments(" A ", "B\nC"));
    }

    // ── 边界视图：以 internal setter / 反射注入哨兵值，不触发任何游戏态 ──

    private static Prompts MakeBoundaryPrompts()
    {
        var prompts = (Prompts)FormatterServices.GetUninitializedObject(typeof(Prompts));
        prompts.SystemPrompt = "SYS-SENTINEL";
        prompts.GameConstantContext = "GAMECONST-SENTINEL";
        prompts.NpcConstantContext = "NPCCONST-SENTINEL";
        prompts.Instructions = "INSTR-SENTINEL";
        prompts.Command = "CMD-SENTINEL";
        prompts.CorePrompt = "CORE-SENTINEL";
        prompts.ResponseStart = "RESP-SENTINEL";
        return prompts;
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

    [Fact]
    public void StaticInstructionContext_ContainsInstructionsAndCommand_ExcludesCorePrompt()
    {
        SetLanguageEnvironment(string.Empty, LocalizedContentManager.LanguageCode.en);
        var prompts = MakeBoundaryPrompts();

        string result = prompts.StaticInstructionContext;

        Assert.Equal("INSTR-SENTINEL\n\nCMD-SENTINEL", result);
        Assert.DoesNotContain("CORE-SENTINEL", result);
    }

    [Fact]
    public void ConversationStream_ContainsConversationOnly_ExcludesInstructionsAndCommand()
    {
        SetLanguageEnvironment(string.Empty, LocalizedContentManager.LanguageCode.en);
        var prompts = MakeBoundaryPrompts();
        SetPrivateField(prompts, "_sessionContinuitySegment", "CONTINUITY-SENTINEL");
        SetPrivateField(prompts, "_currentConversationSegment", "CONVERSATION-SENTINEL");

        string result = prompts.ConversationStream;

        Assert.Contains("CONTINUITY-SENTINEL", result);
        Assert.Contains("CONVERSATION-SENTINEL", result);
        Assert.Contains("<response_trigger>", result);
        Assert.Contains("CONTINUITY-SENTINEL\n\nCONVERSATION-SENTINEL", result);
        Assert.DoesNotContain("INSTR-SENTINEL", result);
        Assert.DoesNotContain("CMD-SENTINEL", result);
        Assert.DoesNotContain("GAMECONST-SENTINEL", result);
        Assert.DoesNotContain("NPCCONST-SENTINEL", result);
        Assert.DoesNotContain("CORE-SENTINEL", result);
    }

    [Fact]
    public void DynamicContext_ComposesConstantsAndTiersInOrder_ExcludesInstructionsAndCommand()
    {
        SetLanguageEnvironment(string.Empty, LocalizedContentManager.LanguageCode.en);
        var prompts = MakeBoundaryPrompts();
        SetPrivateField(prompts, "_corePlan", MakeSentinelPlan());

        string result = prompts.DynamicContext;

        int iGame = result.IndexOf("GAMECONST-SENTINEL", StringComparison.Ordinal);
        int iNpc = result.IndexOf("NPCCONST-SENTINEL", StringComparison.Ordinal);
        int iTier1 = result.IndexOf("TIER1-SENTINEL", StringComparison.Ordinal);
        int iTier2b = result.IndexOf("TIER2B-SENTINEL", StringComparison.Ordinal);
        Assert.True(iGame >= 0 && iNpc >= 0 && iTier1 >= 0 && iTier2b >= 0,
            $"DynamicContext must contain all four segments; got: {result}");
        Assert.True(iGame < iNpc && iNpc < iTier1 && iTier1 < iTier2b,
            "DynamicContext must follow GameConstant → NpcConstant → Tier1 → Tier2b order");
        Assert.DoesNotContain("INSTR-SENTINEL", result);
        Assert.DoesNotContain("CMD-SENTINEL", result);
        Assert.DoesNotContain("CORE-SENTINEL", result);
    }

    [Fact]
    public void DynamicContext_WithoutAssembleCore_Throws()
    {
        SetLanguageEnvironment(string.Empty, LocalizedContentManager.LanguageCode.en);
        var prompts = MakeBoundaryPrompts();

        Assert.Throws<InvalidOperationException>(() => prompts.DynamicContext);
    }

    // ── LogDebugRequest：命名边界标签与顺序（经反射调用 + 捕获监视器观察） ──

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
    public void LogDebugRequest_LabelsBoundariesInRequiredOrder()
    {
        SetLanguageEnvironment(string.Empty, LocalizedContentManager.LanguageCode.en);
        var prompts = MakeBoundaryPrompts();
        SetPrivateField(prompts, "_corePlan", MakeSentinelPlan());
        SetPrivateField(prompts, "_sessionContinuitySegment", "CONTINUITY-SENTINEL");
        SetPrivateField(prompts, "_currentConversationSegment", "CONVERSATION-SENTINEL");
        var character = (ValleytalkReborn.Character)FormatterServices.GetUninitializedObject(typeof(ValleytalkReborn.Character));

        var capture = new CapturingMonitor();
        Log.Initialize(capture);
        var method = typeof(LlmDialogueService).GetMethod("LogDebugRequest",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(method);
        method.Invoke(LlmDialogueService.Instance, new object[] { character, prompts, 1 });

        string captured = capture.Captured.ToString();
        int iSystem = captured.IndexOf("[System]", StringComparison.Ordinal);
        int iStatic = captured.IndexOf("[StaticInstructionContext]", StringComparison.Ordinal);
        int iDynamic = captured.IndexOf("[DynamicContext]", StringComparison.Ordinal);
        int iStream = captured.IndexOf("[ConversationStream]", StringComparison.Ordinal);
        int iResponse = captured.IndexOf("[ResponseStart]", StringComparison.Ordinal);
        Assert.True(iSystem >= 0 && iStatic >= 0 && iDynamic >= 0 && iStream >= 0 && iResponse >= 0,
            $"All five boundary labels must be present. Captured:\n{captured}");
        Assert.True(iSystem < iStatic, "System must precede StaticInstructionContext");
        Assert.True(iStatic < iDynamic, "StaticInstructionContext must precede DynamicContext");
        Assert.True(iDynamic < iStream, "DynamicContext must precede ConversationStream");
        Assert.True(iStream < iResponse, "ConversationStream must precede ResponseStart");
        Assert.Contains("legacy", captured);
    }
}
