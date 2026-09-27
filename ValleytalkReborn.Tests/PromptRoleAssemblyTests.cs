// PromptRoleAssemblyTests.cs
// PROMPT-ARCH-03 — role-based message assembly regression tests.
// Verifies: role mapping (farmer=user, NPC=assistant), message order
// (dynamic context → continuity → chat history → trigger), latest-farmer-after-NPC,
// no legacy "- Farmer:"/"- NPC:" transcript, DynamicContext de-duplication,
// and responseStart applied exactly once by the role-based Provider helper.
// No HTTP requests, no active LLM Provider required.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Threading;
using StardewModdingAPI;
using StardewValley;
using ValleytalkReborn;
using Xunit;

[Collection("StaticGlobalStateCollection")]
public class PromptRoleAssemblyTests : IDisposable
{
    private readonly ModConfig _originalConfig;
    private readonly LocalizedContentManager.LanguageCode _originalLanguageCode;
    private readonly IModHelper _originalSHelper;
    private readonly IMonitor _originalSMonitor;
    private readonly bool _needRestore;

    public PromptRoleAssemblyTests()
    {
        InstallHeadlessContext();
        // 在访问 Character 之前注入 SHelper，确保 PromptCache 静态构造器不因 SHelper 为 null 而失败。
        // 测试结束后恢复原始值，避免污染后续测试。
        var sHelperField = typeof(ModEntry).GetField("<SHelper>k__BackingField",
            BindingFlags.Static | BindingFlags.NonPublic);
        _originalSHelper = (IModHelper)sHelperField?.GetValue(null);
        _originalSMonitor = ModEntry.SMonitor;
        _originalConfig = ModEntry.Config;
        _originalLanguageCode = LocalizedContentManager.CurrentLanguageCode;
        _needRestore = _originalSHelper == null;
        if (_needRestore)
        {
            ModEntry.SMonitor = new ValleytalkReborn.Tests.FakeMonitor();
            ModEntry.Config = new ModConfig();
            sHelperField?.SetValue(null, new ValleytalkReborn.Tests.FakeModHelper(string.Empty));
        }
    }

    public void Dispose()
    {
        ModEntry.Config = _originalConfig;
        LocalizedContentManager.CurrentLanguageCode = _originalLanguageCode;
        if (_needRestore)
        {
            var sHelperField = typeof(ModEntry).GetField("<SHelper>k__BackingField",
                BindingFlags.Static | BindingFlags.NonPublic);
            sHelperField?.SetValue(null, null);
            ModEntry.SMonitor = _originalSMonitor;
        }
    }

    private static bool _ctxInstalled;
    private static void InstallHeadlessContext()
    {
        if (_ctxInstalled) return;
        _ctxInstalled = true;

        AppDomain.CurrentDomain.AssemblyResolve += (sender, args) =>
        {
            string name = new AssemblyName(args.Name).Name;
            string path = Path.Combine(
                AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", "..",
                "Stardew Valley", "smapi-internal", name + ".dll");
            return File.Exists(path) ? Assembly.LoadFrom(path) : null;
        };

        var runner = FormatterServices.GetUninitializedObject(typeof(GameRunner));
        var instancesField = typeof(GameRunner).GetField("gameInstances",
            BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance);
        instancesField.SetValue(runner, Activator.CreateInstance(instancesField.FieldType));
        typeof(GameRunner).GetField("instance",
            BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static)?.SetValue(null, runner);
        Game1.hasLocalClientsOnly = false;
    }

    private static void SetLanguageEnvironment(string languageOverride, LocalizedContentManager.LanguageCode gameLanguage)
    {
        ModEntry.Config = new ModConfig { LanguageOverride = languageOverride };
        LocalizedContentManager.CurrentLanguageCode = gameLanguage;
    }

    private static void SetPrivateField(object target, string fieldName, object value)
    {
        var field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
        field?.SetValue(target, value);
    }

    private static ValleytalkReborn.Character MakeCharacter(string name)
    {
        var c = (ValleytalkReborn.Character)FormatterServices.GetUninitializedObject(typeof(ValleytalkReborn.Character));
        var nameField = typeof(ValleytalkReborn.Character).GetField("<Name>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic);
        nameField?.SetValue(c, name);
        return c;
    }

    private static DialogueContext MakeContext(List<ConversationElement> history, bool includeShortTerm = true)
    {
        var ctx = new DialogueContext();
        ctx.ChatHistory = history;
        ctx.RoutingFlags = new ContextFlags
        {
            IncludeSafetyRules = true,
            IncludeShortTermContext = includeShortTerm,
            IncludeMemories = true,
            IncludeEnvironment = true,
            IncludeFarmDetails = true,
            IsSimpleGreeting = false,
        };
        return ctx;
    }

    private static Prompts MakeRolePrompts(DialogueContext context, ValleytalkReborn.Character character,
        string systemPrompt = "SYS-CONTEXT", string gameConst = "GAME-CONTEXT", string npcConst = "")
    {
        var prompts = (Prompts)FormatterServices.GetUninitializedObject(typeof(Prompts));
        prompts.SystemPrompt = systemPrompt;
        prompts.GameConstantContext = gameConst;
        prompts.NpcConstantContext = npcConst;
        SetPrivateField(prompts, "_corePlan", new InjectionPlan
        {
            Tier1Snapshot = new Tier1SnapshotContext(new Dictionary<string, string>()),
            ActiveImpulses = new Dictionary<string, string>(),
        });
        SetPrivateField(prompts, "<Context>k__BackingField", context);
        SetPrivateField(prompts, "<Character>k__BackingField", character);
        return prompts;
    }

    private static int IndexOfContent(IReadOnlyList<LlmChatMessage> messages, string contentSubstr) =>
        Enumerable.Range(0, messages.Count)
            .FirstOrDefault(i => messages[i].Content.Contains(contentSubstr, StringComparison.Ordinal), -1);

    private static string RoleOf(IReadOnlyList<LlmChatMessage> messages, int index) =>
        messages[index].Role;

    // ── BuildChatMessages：角色映射 ──

    [Fact]
    public void BuildChatMessages_FarmerIsUser_NpcIsAssistant()
    {
        SetLanguageEnvironment(string.Empty, LocalizedContentManager.LanguageCode.en);
        IReadOnlyList<LlmChatMessage> messages = new List<LlmChatMessage>
        {
            new LlmChatMessage("user", "Farmer said hello"),
            new LlmChatMessage("assistant", "NPC replied"),
        };

        var result = LlmOpenAiBase.BuildChatMessages("system", messages, "");

        Assert.Equal(3, result.Count); // system + user + assistant
        Assert.Equal("system", RoleOfSerialized(result[0]));
        Assert.Equal("user", RoleOfSerialized(result[1]));
        Assert.Equal("assistant", RoleOfSerialized(result[2]));
    }

    // ── BuildChatMessages：responseStart 只出现一次（追加到末尾 user 消息） ──

    [Fact]
    public void BuildChatMessages_ResponseStart_ExactlyOnce()
    {
        SetLanguageEnvironment(string.Empty, LocalizedContentManager.LanguageCode.en);
        var responseStart = "[In-Character Dialogue]:";
        IReadOnlyList<LlmChatMessage> messages = new List<LlmChatMessage>
        {
            new LlmChatMessage("user", "context"),
            new LlmChatMessage("assistant", "prev NPC"),
            new LlmChatMessage("user", "trigger"),
        };

        var result = LlmOpenAiBase.BuildChatMessages("system", messages, responseStart);

        // responseStart 仅在末尾 user 消息中出现一次
        int occurrences = result.Count(m => ContentOfSerialized(m).Contains(responseStart, StringComparison.Ordinal));
        Assert.Equal(1, occurrences);
        Assert.Contains(responseStart, ContentOfSerialized(result[result.Count - 1]));
    }

    // ── BuildRuntimeChatMessages：消息顺序（动态上下文 → 历史 → 触发） ──

    [Fact]
    public void BuildRuntimeChatMessages_Order_ContextBeforeHistoryBeforeTrigger()
    {
        SetLanguageEnvironment(string.Empty, LocalizedContentManager.LanguageCode.en);
        var history = new List<ConversationElement>
        {
            new ConversationElement("Farmer: hi", true),
            new ConversationElement("NPC: hello", false),
        };
        var prompts = MakeRolePrompts(MakeContext(history), MakeCharacter("Abigail"));

        var messages = prompts.BuildRuntimeChatMessages();

        int iContext = IndexOfContent(messages, "GAME-CONTEXT");
        int iFarmer = IndexOfContent(messages, "Farmer: hi");
        int iNpc = IndexOfContent(messages, "NPC: hello");
        int iTrigger = IndexOfContent(messages, "RESPONSE_TRIGGER");

        Assert.True(iContext >= 0, "Dynamic context missing");
        Assert.True(iFarmer >= 0, "Farmer history missing");
        Assert.True(iNpc >= 0, "NPC history missing");
        Assert.True(iTrigger >= 0, "Trigger missing");
        Assert.True(iContext < iFarmer && iFarmer < iNpc && iNpc < iTrigger,
            $"Order wrong: context={iContext} farmer={iFarmer} npc={iNpc} trigger={iTrigger}");
    }

    // ── BuildRuntimeChatMessages：最新农夫输入在 NPC 助手轮次之后 ──

    [Fact]
    public void BuildRuntimeChatMessages_MostRecentFarmerAfterNpc()
    {
        SetLanguageEnvironment(string.Empty, LocalizedContentManager.LanguageCode.en);
        var history = new List<ConversationElement>
        {
            new ConversationElement("NPC: first", false),
            new ConversationElement("Farmer: latest", true),
        };
        var prompts = MakeRolePrompts(MakeContext(history), MakeCharacter("Abigail"));

        var messages = prompts.BuildRuntimeChatMessages();

        int iNpc = IndexOfContent(messages, "NPC: first");
        int iFarmer = IndexOfContent(messages, "Farmer: latest");

        Assert.True(iNpc >= 0, "NPC turn missing");
        Assert.True(iFarmer > iNpc, $"Latest farmer ({iFarmer}) should follow NPC ({iNpc})");
        Assert.Equal("assistant", RoleOf(messages, iNpc));
        Assert.Equal("user", RoleOf(messages, iFarmer));
    }

    // ── BuildRuntimeChatMessages：不含旧 "- Farmer:" / "- NPC:" 格式 ──

    [Fact]
    public void BuildRuntimeChatMessages_NoLegacyFarmerNpcFormat()
    {
        SetLanguageEnvironment(string.Empty, LocalizedContentManager.LanguageCode.en);
        var history = new List<ConversationElement>
        {
            new ConversationElement("secret player text", true),
            new ConversationElement("secret npc text", false),
        };
        var prompts = MakeRolePrompts(MakeContext(history), MakeCharacter("Abigail"));

        var messages = prompts.BuildRuntimeChatMessages();

        var allContent = string.Join("\n", messages.Select(m => m.Content));
        Assert.DoesNotContain("- Farmer:", allContent);
        Assert.DoesNotContain("- Abigail:", allContent);
        Assert.Contains("secret player text", allContent);
        Assert.Contains("secret npc text", allContent);
    }

    // ── BuildRuntimeChatMessages：DynamicContext 去重（与 SystemPrompt 重复则省略） ──

    [Fact]
    public void BuildRuntimeChatMessages_DynamicContext_DedupAgainstSystemPrompt()
    {
        SetLanguageEnvironment(string.Empty, LocalizedContentManager.LanguageCode.en);
        var prompts = MakeRolePrompts(
            MakeContext(new List<ConversationElement>()),
            MakeCharacter("Abigail"),
            systemPrompt: "- GOSSIP-SHARED-9999",
            gameConst: "- GOSSIP-SHARED-9999\n- UNIQUE-GAME-0000");

        var messages = prompts.BuildRuntimeChatMessages();

        var dynamicUserMsg = messages.FirstOrDefault(m => m.Role == "user" && m.Content.Contains("UNIQUE-GAME-0000"));
        Assert.NotNull(dynamicUserMsg);
        Assert.DoesNotContain("GOSSIP-SHARED-9999", dynamicUserMsg.Content);
    }

    // ── BuildRuntimeChatMessages：动态上下文为空时不含 user 上下文消息 ──

    [Fact]
    public void BuildRuntimeChatMessages_EmptyDynamicContext_OmitsIt()
    {
        SetLanguageEnvironment(string.Empty, LocalizedContentManager.LanguageCode.en);
        var prompts = MakeRolePrompts(
            MakeContext(new List<ConversationElement>()),
            MakeCharacter("Abigail"),
            systemPrompt: string.Empty,
            gameConst: string.Empty,
            npcConst: string.Empty);

        var messages = prompts.BuildRuntimeChatMessages();

        Assert.Single(messages);
        Assert.Equal("user", messages[0].Role);
        Assert.Contains("RESPONSE_TRIGGER", messages[0].Content);
    }

    // ── BuildRuntimeChatMessages：衔接轮次保留 FuzzyTime 前缀 ──

    [Fact]
    public void BuildRuntimeChatMessages_ContinuityTurns_RetainFuzzyTime()
    {
        SetLanguageEnvironment(string.Empty, LocalizedContentManager.LanguageCode.en);
        var history = new List<ConversationElement>();
        var ctx = MakeContext(history);
        var character = MakeCharacter("Abigail");
        var prompts = MakeRolePrompts(ctx, character);

        // 向 SessionCache 注入带 FuzzyTime 的衔接轮次（与 GetContinuityTurns 的筛选语义一致）。
        var session = SessionCache.Instance.GetOrCreate("Abigail");
        session.RecentTurns.Add(new ConversationElement("player turn with time", true) { FuzzyTime = "Morning" });
        session.RecentTurns.Add(new ConversationElement("npc turn no time", false) { FuzzyTime = "" });

        var messages = prompts.BuildRuntimeChatMessages();

        // 找到衔接轮次消息（在标题 user 消息之后，触发后缀之前）
        var roleMessages = messages.Where(m => m.Content.Contains("player turn with time") || m.Content.Contains("npc turn no time")).ToList();
        Assert.Equal(2, roleMessages.Count);

        var playerMsg = roleMessages.First(m => m.Content.Contains("player turn with time"));
        var npcMsg = roleMessages.First(m => m.Content.Contains("npc turn no time"));

        // 玩家轮次：FuzzyTime 非空，应带前缀；角色为 user
        Assert.Equal("user", playerMsg.Role);
        Assert.StartsWith("[Morning] ", playerMsg.Content);

        // NPC 轮次：FuzzyTime 为空，无前缀；角色为 assistant
        Assert.Equal("assistant", npcMsg.Role);
        Assert.DoesNotContain("[", npcMsg.Content);
        Assert.StartsWith("npc turn no time", npcMsg.Content);
    }

    // ── ExecuteNonStreamingRequestAsync：取消令牌传播到 HTTP 请求路径 ──

    [Fact]
    public void ExecuteNonStreamingRequestAsync_PreCancelledToken_ReturnsQuickly()
    {
        // 使用预取消令牌：HttpClient.SendAsync 在发起网络请求前即抛出，
        // 证明取消令牌已传递到 HTTP 请求路径（无需真实网络）。
        var provider = new LlmOpenAi("test-key", "gpt-4o");
        var messages = new List<object> { new { role = "user", content = "hi" } };
        using var cts = new CancellationTokenSource();
        cts.Cancel(); // 立即取消

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var response = provider.ExecuteNonStreamingRequestAsync(messages, 256, "", true, cts.Token).Result;
        sw.Stop();

        // 预取消令牌应在远小于配置超时的时间内返回（取消被传递到 HTTP 层）。
        Assert.True(sw.ElapsedMilliseconds < 5000, $"Expected fast cancellation, took {sw.ElapsedMilliseconds}ms");
        Assert.False(response.IsSuccess);
    }

    // ── 辅助 ──

    private static string RoleOfSerialized(object msg)
    {
        var type = msg.GetType();
        return (string)type.GetProperty("role").GetValue(msg);
    }

    private static string ContentOfSerialized(object msg)
    {
        var type = msg.GetType();
        return (string)type.GetProperty("content").GetValue(msg);
    }
}
