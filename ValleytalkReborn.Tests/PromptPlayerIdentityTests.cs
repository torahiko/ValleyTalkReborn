// PromptPlayerIdentityTests.cs
// VT-CONTEXT-01 — consistent player identity in runtime prompt assembly.
// Verifies: the current Game1.player.Name labels player turns in the role-based
// history window, the session-continuity turns, the non-role transcript
// (BuildCurrentConversation) and the continuity block (BuildSessionContinuity);
// the bilingual identity statement states that "{player name}" and 农夫/Farmer
// are the same person; blank player names fall back to generalFarmerLabel
// (农夫 in zh, Farmer in en) with no empty labels; and a blank fallback is
// surfaced as an Error + exception instead of an empty speaker label.
// No HTTP requests, no active LLM Provider required.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Serialization;
using System.Text;
using System.Text.Json;
using StardewModdingAPI;
using StardewValley;
using ValleytalkReborn;
using ValleytalkReborn.Tests;
using Xunit;

[Collection("StaticGlobalStateCollection")]
public class PromptPlayerIdentityTests : IDisposable
{
    private const string PlayerName = "虎彦";
    private const string NpcName = "IdentityNpc";

    private readonly ModConfig _originalConfig;
    private readonly LocalizedContentManager.LanguageCode _originalLanguageCode;
    private readonly IModHelper _originalSHelper;
    private readonly IMonitor _originalSMonitor;
    private readonly bool _needRestore;

    public PromptPlayerIdentityTests()
    {
        TestEnvironment.InstallHeadlessContext();
        // 在访问 Character 之前注入 SHelper，确保 PromptCache 静态构造器不因 SHelper 为 null 而失败。
        var sHelperField = typeof(ModEntry).GetField("<SHelper>k__BackingField",
            BindingFlags.Static | BindingFlags.NonPublic);
        _originalSHelper = (IModHelper)sHelperField?.GetValue(null);
        _originalSMonitor = ModEntry.SMonitor;
        _originalConfig = ModEntry.Config;
        _originalLanguageCode = LocalizedContentManager.CurrentLanguageCode;
        _needRestore = _originalSHelper == null;
        if (_needRestore)
        {
            ModEntry.SMonitor = new FakeMonitor();
            ModEntry.Config = new ModConfig();
            sHelperField?.SetValue(null, new FakeModHelper(string.Empty));
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
        SetPrivateField(c, "<Name>k__BackingField", name);
        // 预置 Bio：无存档环境下 Character.Bio 的懒加载会触碰 Game1.getCharacterFromName（NRE）。
        // 预置后 Util.GetString(character, "generalFarmerLabel") 走正常的覆盖/缓存/i18n 查找链。
        SetPrivateField(c, "_bioData", new BioData { Biography = "测试用人物设定", Missing = true });
        return c;
    }

    private static DialogueContext MakeContext(List<ConversationElement> history)
    {
        var ctx = new DialogueContext();
        ctx.ChatHistory = history;
        ctx.RoutingFlags = new ContextFlags
        {
            IncludeSafetyRules = true,
            IncludeShortTermContext = true,
            IncludeMemories = true,
            IncludeEnvironment = true,
            IncludeFarmDetails = true,
            IsSimpleGreeting = false,
        };
        return ctx;
    }

    private static Prompts MakeRolePrompts(DialogueContext context, ValleytalkReborn.Character character)
    {
        var prompts = (Prompts)FormatterServices.GetUninitializedObject(typeof(Prompts));
        prompts.SystemPrompt = "SYS-CONTEXT";
        prompts.GameConstantContext = "GAME-CONTEXT";
        prompts.NpcConstantContext = string.Empty;
        SetPrivateField(prompts, "_corePlan", new InjectionPlan
        {
            Tier1Snapshot = new Tier1SnapshotContext(new Dictionary<string, string>()),
            ActiveImpulses = new Dictionary<string, string>(),
        });
        SetPrivateField(prompts, "<Context>k__BackingField", context);
        SetPrivateField(prompts, "<Character>k__BackingField", character);
        return prompts;
    }

    private static LlmChatMessage MessageContaining(IReadOnlyList<LlmChatMessage> messages, string substr)
    {
        return messages.FirstOrDefault(m => m.Content.Contains(substr, StringComparison.Ordinal))
               ?? throw new Xunit.Sdk.XunitException($"No message contains '{substr}'. Messages:\n"
                   + string.Join("\n", messages.Select(m => $"[{m.Role}] {m.Content}")));
    }

    private static string ContentPackLabel(string file)
    {
        string repoRoot = Path.GetFullPath(Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "..", "..", "..", ".."));
        string path = Path.Combine(repoRoot, "ContentPack", "i18n", file);
        Assert.True(File.Exists(path), $"i18n file not found: {path}");
        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        Assert.True(doc.RootElement.TryGetProperty("generalFarmerLabel", out var value),
            $"generalFarmerLabel missing in {path}");
        return value.GetString();
    }

    /// <summary>将 generalFarmerLabel 注入 I18n 静态字典（测试环境无真实 i18n 加载路径）。</summary>
    private static IDisposable UseI18nFarmerLabel(string label)
    {
        var type = typeof(I18n);
        var english = type.GetField("_english", BindingFlags.Static | BindingFlags.NonPublic);
        var locale = type.GetField("_locale", BindingFlags.Static | BindingFlags.NonPublic);
        var localeName = type.GetField("_localeName", BindingFlags.Static | BindingFlags.NonPublic);
        var previous = new[] { english?.GetValue(null), locale?.GetValue(null), localeName?.GetValue(null) };
        var dict = new Dictionary<string, string>();
        if (label != null) dict["generalFarmerLabel"] = label;
        english?.SetValue(null, dict);
        locale?.SetValue(null, dict);
        return new I18nScope(english, locale, localeName, previous);
    }

    private sealed class I18nScope : IDisposable
    {
        private readonly FieldInfo _english;
        private readonly FieldInfo _locale;
        private readonly FieldInfo _localeName;
        private readonly object[] _previous;
        private bool _disposed;

        public I18nScope(FieldInfo english, FieldInfo locale, FieldInfo localeName, object[] previous)
        {
            _english = english;
            _locale = locale;
            _localeName = localeName;
            _previous = previous;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _english?.SetValue(null, _previous[0]);
            _locale?.SetValue(null, _previous[1]);
            _localeName?.SetValue(null, _previous[2]);
        }
    }

    private sealed class CapturingMonitor : IMonitor
    {
        public readonly StringBuilder Captured = new StringBuilder();
        public bool IsVerbose => false;
        public void Log(string message, LogLevel level) => Captured.AppendLine($"[{level}] {message}");
        public void LogOnce(string message, LogLevel level) => Captured.AppendLine($"[{level}] {message}");
        public void VerboseLog(string message) { }
        public void VerboseLog(ref StardewModdingAPI.Framework.Logging.VerboseLogStringHandler handler) { }
    }

    // ── 1. 玩家名标识 role-based 历史与衔接轮次（zh） ──

    [Fact]
    public void RoleHistory_PlayerLinesLabelledWithPlayerName()
    {
        SetLanguageEnvironment("zh", LocalizedContentManager.LanguageCode.zh);
        var history = new List<ConversationElement>
        {
            new ConversationElement("今天天气不错", true),
            new ConversationElement("嗯，确实不错", false),
        };
        var character = MakeCharacter(NpcName);
        var prompts = MakeRolePrompts(MakeContext(history), character);

        using (FakePlayer.Install(PlayerName))
        {
            var messages = prompts.BuildRuntimeChatMessages();

            var playerMsg = MessageContaining(messages, "今天天气不错");
            var npcMsg = MessageContaining(messages, "嗯，确实不错");
            Assert.Equal("user", playerMsg.Role);
            Assert.Equal("assistant", npcMsg.Role);
            Assert.StartsWith($"{PlayerName}: ", playerMsg.Content, StringComparison.Ordinal);
            Assert.Equal("嗯，确实不错", npcMsg.Content);
        }
    }

    [Fact]
    public void ContinuityTurns_PlayerLineLabelledWithPlayerName_KeepsFuzzyTime()
    {
        SetLanguageEnvironment("zh", LocalizedContentManager.LanguageCode.zh);
        var character = MakeCharacter(NpcName);
        var prompts = MakeRolePrompts(MakeContext(new List<ConversationElement>()), character);
        var session = SessionCache.Instance.GetOrCreate(NpcName);
        session.RecentTurns.Add(new ConversationElement("早上我来过一趟", true) { FuzzyTime = "Morning" });
        session.RecentTurns.Add(new ConversationElement("我记得你", false) { FuzzyTime = "" });

        using (FakePlayer.Install(PlayerName))
        {
            var messages = prompts.BuildRuntimeChatMessages();

            var playerMsg = MessageContaining(messages, "早上我来过一趟");
            var npcMsg = MessageContaining(messages, "我记得你");
            Assert.Equal("user", playerMsg.Role);
            Assert.Equal("assistant", npcMsg.Role);
            Assert.Equal($"[Morning] {PlayerName}: 早上我来过一趟", playerMsg.Content);
            Assert.Equal("我记得你", npcMsg.Content);
        }
    }

    // ── 2. 身份说明：玩家名与 农夫/Farmer 是同一人 ──

    [Fact]
    public void TriggerSuffix_StatesPlayerNameAndFarmerAreSameIdentity_Zh()
    {
        SetLanguageEnvironment("zh", LocalizedContentManager.LanguageCode.zh);
        var character = MakeCharacter(NpcName);
        var prompts = MakeRolePrompts(MakeContext(new List<ConversationElement>()), character);

        using (FakePlayer.Install(PlayerName))
        {
            var messages = prompts.BuildRuntimeChatMessages();
            string trigger = messages[messages.Count - 1].Content;

            Assert.Equal("user", messages[messages.Count - 1].Role);
            Assert.Contains(PlayerName, trigger);
            Assert.Contains("农夫/Farmer", trigger);
            Assert.Contains("不是两个角色", trigger);
            Assert.Contains("RESPONSE_TRIGGER", trigger);
        }
    }

    [Fact]
    public void TriggerSuffix_StatesPlayerNameAndFarmerAreSameIdentity_En()
    {
        SetLanguageEnvironment(string.Empty, LocalizedContentManager.LanguageCode.en);
        var history = new List<ConversationElement>
        {
            new ConversationElement("good morning", true),
            new ConversationElement("morning", false),
        };
        var character = MakeCharacter(NpcName);
        var prompts = MakeRolePrompts(MakeContext(history), character);

        using (FakePlayer.Install("Alex"))
        {
            var messages = prompts.BuildRuntimeChatMessages();
            string trigger = messages[messages.Count - 1].Content;

            var playerMsg = MessageContaining(messages, "good morning");
            Assert.StartsWith("Alex: ", playerMsg.Content, StringComparison.Ordinal);
            Assert.Contains("Alex", trigger);
            Assert.Contains("农夫/Farmer", trigger);
            Assert.Contains("same person", trigger);
            Assert.Contains("RESPONSE_TRIGGER", trigger);
        }
    }

    // ── 3. 非 role-based 装配路径（Tier 2a）同样使用玩家名 ──

    [Fact]
    public void BuildCurrentConversation_PlayerLinesUsePlayerName_NpcLabelUnchanged()
    {
        SetLanguageEnvironment("zh", LocalizedContentManager.LanguageCode.zh);
        var history = new List<ConversationElement>
        {
            new ConversationElement("你今天看起来很累", true) { FuzzyTime = "Morning" },
            new ConversationElement("还好吧", false),
        };
        var character = MakeCharacter(NpcName);

        using (FakePlayer.Install(PlayerName))
        {
            string prompt = Prompts.PromptsBlocks.BuildCurrentConversation(
                character, MakeContext(history), new ContextFlags { IncludeShortTermContext = true },
                new HashSet<string>(), NpcName);

            Assert.Contains($"- [Morning] {PlayerName}: 你今天看起来很累", prompt);
            Assert.Contains($"- {NpcName}: 还好吧", prompt);
        }
    }

    [Fact]
    public void BuildSessionContinuity_PlayerLinesUsePlayerName_NpcLabelUnchanged()
    {
        SetLanguageEnvironment("zh", LocalizedContentManager.LanguageCode.zh);
        var character = MakeCharacter(NpcName);
        var session = SessionCache.Instance.GetOrCreate(NpcName);
        session.RecentTurns.Add(new ConversationElement("上次说到雨季", true) { FuzzyTime = "Morning" });
        session.RecentTurns.Add(new ConversationElement("雨要来了", false));

        using (FakePlayer.Install(PlayerName))
        {
            string prompt = Prompts.PromptsBlocks.BuildSessionContinuity(
                character, MakeContext(new List<ConversationElement>()), true);

            Assert.Contains($"- [Morning] {PlayerName}: 上次说到雨季", prompt);
            Assert.Contains($"- {NpcName}: 雨要来了", prompt);
        }
    }

    [Fact]
    public void ConversationStream_CarriesIdentityAndSingleTurnBoundary_Zh()
    {
        SetLanguageEnvironment("zh", LocalizedContentManager.LanguageCode.zh);
        var prompts = MakeRolePrompts(MakeContext(new List<ConversationElement>()), MakeCharacter(NpcName));

        using (FakePlayer.Install(PlayerName))
        {
            string stream = prompts.ConversationStream;

            Assert.Contains("[IDENTITY]", stream);
            Assert.Contains("RESPONSE_TRIGGER", stream);
            Assert.Contains("完成选项区后立即交回对话回合", stream);
        }
    }

    // ── 4. 玩家名为空白：回退本地化 generalFarmerLabel ──

    [Fact]
    public void BlankPlayerName_Zh_FallsBackToLocalizedFarmerLabel()
    {
        SetLanguageEnvironment("zh", LocalizedContentManager.LanguageCode.zh);
        string label = ContentPackLabel("zh.json");
        Assert.Equal("农夫", label);

        var history = new List<ConversationElement> { new ConversationElement("你好", true) };
        var character = MakeCharacter(NpcName);
        var prompts = MakeRolePrompts(MakeContext(history), character);

        using (FakePlayer.Install("   "))
        using (UseI18nFarmerLabel(label))
        {
            var messages = prompts.BuildRuntimeChatMessages();

            var playerMsg = MessageContaining(messages, "你好");
            Assert.StartsWith($"{label}: ", playerMsg.Content, StringComparison.Ordinal);
            Assert.DoesNotContain($"{PlayerName}: ", string.Join("\n", messages.Select(m => m.Content)));
            Assert.Contains(label, messages[messages.Count - 1].Content);
        }
    }

    [Fact]
    public void BlankPlayerName_En_FallsBackToLocalizedFarmerLabel()
    {
        SetLanguageEnvironment(string.Empty, LocalizedContentManager.LanguageCode.en);
        string label = ContentPackLabel("default.json");
        Assert.False(string.IsNullOrWhiteSpace(label));
        Assert.Contains("Farmer", label, StringComparison.OrdinalIgnoreCase);

        var history = new List<ConversationElement> { new ConversationElement("hello there", true) };
        var character = MakeCharacter(NpcName);
        var prompts = MakeRolePrompts(MakeContext(history), character);

        using (FakePlayer.Install(string.Empty))
        using (UseI18nFarmerLabel(label))
        {
            var messages = prompts.BuildRuntimeChatMessages();

            var playerMsg = MessageContaining(messages, "hello there");
            Assert.StartsWith($"{label}: ", playerMsg.Content, StringComparison.Ordinal);
            Assert.Contains(label, messages[messages.Count - 1].Content);
        }
    }

    [Fact]
    public void BlankPlayerName_NpcLinesAndRolesUnchanged()
    {
        SetLanguageEnvironment(string.Empty, LocalizedContentManager.LanguageCode.en);
        var history = new List<ConversationElement>
        {
            new ConversationElement("player line", true),
            new ConversationElement("npc line", false),
        };
        var character = MakeCharacter(NpcName);
        var prompts = MakeRolePrompts(MakeContext(history), character);

        using (FakePlayer.Install(""))
        using (UseI18nFarmerLabel("Farmer"))
        {
            var messages = prompts.BuildRuntimeChatMessages();

            Assert.Equal("user", MessageContaining(messages, "player line").Role);
            Assert.Equal("assistant", MessageContaining(messages, "npc line").Role);
            Assert.Equal("npc line", MessageContaining(messages, "npc line").Content);
        }
    }

    // ── 5. 玩家名与回退标签均空白：BUG，记录 Error 并暴露失败 ──

    [Fact]
    public void BlankPlayerName_AndBlankLabel_ThrowsAndLogsError()
    {
        SetLanguageEnvironment(string.Empty, LocalizedContentManager.LanguageCode.en);
        var history = new List<ConversationElement> { new ConversationElement("hello", true) };
        var character = MakeCharacter(NpcName);
        var prompts = MakeRolePrompts(MakeContext(history), character);

        var capture = new CapturingMonitor();
        var originalMonitor = ModEntry.SMonitor;
        ModEntry.SMonitor = capture;
        try
        {
            using (FakePlayer.Install("  "))
            using (UseI18nFarmerLabel(null))
            {
                var ex = Assert.Throws<InvalidOperationException>(() => prompts.BuildRuntimeChatMessages());
                Assert.Contains("generalFarmerLabel", ex.Message);
                Assert.Contains("generalFarmerLabel", capture.Captured.ToString());
                Assert.Contains("[Error]", capture.Captured.ToString());
            }
        }
        finally
        {
            ModEntry.SMonitor = originalMonitor;
        }
    }

    // ── 6. ContentPack 回退标签保持原值且非空 ──

    [Fact]
    public void ContentPackGeneralFarmerLabel_UnchangedAndNonEmpty()
    {
        Assert.Equal("农夫", ContentPackLabel("zh.json"));
        Assert.Equal("the farmer", ContentPackLabel("default.json"));
    }
}
