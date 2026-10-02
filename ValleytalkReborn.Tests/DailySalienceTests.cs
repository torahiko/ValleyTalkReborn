// DailySalienceTests.cs
// REL-005 — DailySalience（今日社交关注）Tier 1 块与旧 biographyRelationships 注入迁移。
//
// 覆盖：
//   1. BuildDailySalience 失败路径：character null / 无关系条目 / 心数不足 → string.Empty。
//   2. 中英文块头与条目格式（## 今日社交关注 / ## Today's Social Salience + * **heading**: desc）。
//   3. GetNpcConstantContext 不再包含旧版 "## 人际关系" / "## Relationships"
//      （REL-002 已知关系骨架保留，作正向对照）。
//   4. RelationshipAttitudeLensBuilder 非中文模式经官方本地化名
//      （1.6 CharacterData.DisplayName）匹配目标。
//
// 无头环境事实（沿用 NpcNameLocalizationTests / GiftPipelineHistoryTests 纪律）：
//   - Character ctor 订阅 SHelper.Events → zh 段必须先 UseIsolatedLocale 再构造；
//   - GetNpcConstantContext 的 Game1.getCharacterFromName 无 try/catch 保护 →
//     game1 垫片需携带空 _locations，使 ForEachLocation 空转并返回 null；
//   - SocialBackbone 的世界就绪门禁 → TestEnvironment.WithWorldReady(true, ...)；
//   - CharacterData 位于 StardewValley.GameData.dll（测试工程不引用），全程反射。

using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Runtime.Serialization;
using StardewModdingAPI;
using StardewValley;
using ValleytalkReborn;
using ValleytalkReborn.Tests;
using Xunit;
using Character = ValleytalkReborn.Character;

[Collection("StaticGlobalStateCollection")]
public class DailySalienceTests : IDisposable
{
    private static readonly FieldInfo CharacterDataField =
        typeof(Game1).GetField("characterData", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

    private static readonly FieldInfo Game1InstanceField =
        typeof(Game1).GetField("game1", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);

    private readonly IModHelper _originalSHelper;
    private readonly IMonitor _originalSMonitor;
    private readonly ModConfig _originalConfig;
    private readonly CultureInfo _originalLocale;
    private readonly string _originalLocaleCache;
    private readonly LocalizedContentManager.LanguageCode _originalLanguageCode;
    private readonly object _originalCharacterData;
    private readonly object _originalGame1;

    public DailySalienceTests()
    {
        // Snapshot the pre-test global state so we can unconditionally restore it
        // in Dispose(), even if a test assertion fails or throws.
        _originalSHelper = TestEnv.GetSHelper();
        _originalSMonitor = ModEntry.SMonitor;
        _originalConfig = ModEntry.Config;
        _originalLocale = TestEnv.GetLocaleField();
        _originalLocaleCache = TestEnv.GetLocaleCacheField();
        _originalLanguageCode = LocalizedContentManager.CurrentLanguageCode;
        _originalCharacterData = CharacterDataField?.GetValue(null);
        _originalGame1 = Game1InstanceField?.GetValue(null);
    }

    public void Dispose()
    {
        TestEnv.SetSHelper(_originalSHelper);
        ModEntry.SMonitor = _originalSMonitor;
        ModEntry.Config = _originalConfig;
        TestEnv.SetLocaleField(_originalLocale);
        TestEnv.SetLocaleCacheField(_originalLocaleCache);
        LocalizedContentManager.CurrentLanguageCode = _originalLanguageCode;
        CharacterDataField?.SetValue(null, _originalCharacterData);
        Game1InstanceField?.SetValue(null, _originalGame1);
    }

    // ───────────────────────────────────────────────────────────────────────
    // 测试支撑
    // ───────────────────────────────────────────────────────────────────────

    private static Character MakeCharacter(string name, Dictionary<string, BioData.ListEntry> relationships)
    {
        var c = new Character(name, null);
        var bioField = typeof(Character).GetField("_bioData", BindingFlags.Instance | BindingFlags.NonPublic);
        var bio = new BioData();
        typeof(BioData).GetField("name", BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(bio, name);
        bio.Biography = "Test biography for " + name + ".";
        if (relationships != null)
        {
            foreach (var kv in relationships)
                bio.Relationships[kv.Key] = kv.Value;
        }
        bioField.SetValue(c, bio);
        return c;
    }

    private static DialogueContext MakeContext(int? hearts)
    {
        return new DialogueContext { Hearts = hearts, ChatHistory = new List<ConversationElement>() };
    }

    private static BioData.ListEntry RelEntry(
        string id, string heading, string description,
        int requiredHearts = 0, string publicIdentity = null)
    {
        return new BioData.ListEntry
        {
            id = id,
            Heading = heading,
            Description = description,
            RequiredHearts = requiredHearts,
            PublicIdentity = publicIdentity ?? string.Empty,
        };
    }

    // game1 垫片携带空 _locations：GetNpcConstantContext 中未包裹 try/catch 的
    // Game1.getCharacterFromName 经 ForEachLocation 空转后确定性返回 null 而非 NRE。
    private static void InstallGame1LocationShim()
    {
        TestEnvironment.InstallHeadlessContext();
        var shim = Game1InstanceField?.GetValue(null);
        if (shim == null)
        {
            shim = FormatterServices.GetUninitializedObject(typeof(Game1));
            Game1InstanceField?.SetValue(null, shim);
        }
        typeof(Game1).GetField("_locations", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?.SetValue(shim, new List<GameLocation>());
    }

    // 中文无头夹具：CurrentLanguageCode=zh + 空 characterData + 位置垫片，
    // 使 GetLocalizedName 的实体层确定性走空、命中简中字典兜底（同 NpcNameLocalizationTests 纪律）。
    // 语言环境的 SHelper/Config 快照由调用处的 UseIsolatedLocale("zh") 负责，
    // CurrentLanguageCode/characterData/game1 由本测试类构造/Dispose 统一快照还原。
    private static void EnterZhHeadless()
    {
        InstallGame1LocationShim();
        LocalizedContentManager.CurrentLanguageCode = LocalizedContentManager.LanguageCode.zh;
        CharacterDataField?.SetValue(null, CreateEmptyCharacterData());
    }

    private static IDictionary CreateEmptyCharacterData()
    {
        var elementType = CharacterDataField.FieldType.GetGenericArguments()[1];
        var dictType = typeof(Dictionary<,>).MakeGenericType(typeof(string), elementType);
        return (IDictionary)Activator.CreateInstance(dictType);
    }

    private static object CreateCharacterData(string displayName)
    {
        var data = Activator.CreateInstance(CharacterDataField.FieldType.GetGenericArguments()[1]);
        // CharacterData（StardewValley.GameData.dll）的 DisplayName 是公共字段而非属性
        data.GetType().GetField("DisplayName", BindingFlags.Public | BindingFlags.Instance)
            ?.SetValue(data, displayName);
        return data;
    }

    private static Prompts MakePrompts(DialogueContext ctx, Character character, string name)
    {
        var prompts = (Prompts)FormatterServices.GetUninitializedObject(typeof(Prompts));
        SetInstanceField(prompts, "<Context>k__BackingField", ctx);
        SetInstanceField(prompts, "<Character>k__BackingField", character);
        SetInstanceField(prompts, "<Name>k__BackingField", name);
        return prompts;
    }

    private static void SetInstanceField(object target, string fieldName, object value)
    {
        target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?.SetValue(target, value);
    }

    // ───────────────────────────────────────────────────────────────────────
    // 1. BuildDailySalience 失败路径
    // ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void BuildDailySalience_NullCharacter_ReturnsEmpty()
    {
        using var _ = TestEnv.UseIsolatedLocale("en");
        Assert.Equal(string.Empty, Prompts.PromptsBlocks.BuildDailySalience(null, null));
    }

    [Fact]
    public void BuildDailySalience_NoRelationshipEntries_ReturnsEmpty()
    {
        using var _ = TestEnv.UseIsolatedLocale("en");
        var character = MakeCharacter("Alex", null);
        Assert.Equal(string.Empty, Prompts.PromptsBlocks.BuildDailySalience(character, MakeContext(0)));
    }

    [Fact]
    public void BuildDailySalience_HeartsBelowRequired_ReturnsEmpty()
    {
        using var _ = TestEnv.UseIsolatedLocale("en");
        var character = MakeCharacter("Alex", new Dictionary<string, BioData.ListEntry>
        {
            ["George"] = RelEntry("George", "George", "A grumpy old man.", requiredHearts: 4),
        });
        Assert.Equal(string.Empty, Prompts.PromptsBlocks.BuildDailySalience(character, MakeContext(2)));
    }

    // ───────────────────────────────────────────────────────────────────────
    // 2. 中英文块头与条目格式
    // ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void BuildDailySalience_EnglishMode_EmitsHeaderAndEntries()
    {
        using var _ = TestEnv.UseIsolatedLocale("en");
        var character = MakeCharacter("Alex", new Dictionary<string, BioData.ListEntry>
        {
            ["George"] = RelEntry("George", "George", "A grumpy old man."),
            ["Haley"] = RelEntry("Haley", "Haley", "A cheerful photographer."),
        });

        string result = Prompts.PromptsBlocks.BuildDailySalience(character, MakeContext(0));

        Assert.False(string.IsNullOrEmpty(result));
        Assert.StartsWith("## Today's Social Salience", result);
        Assert.Contains("In casual conversation today", result);
        Assert.Contains("* **George**: A grumpy old man.", result);
        Assert.Contains("* **Haley**: A cheerful photographer.", result);
        Assert.DoesNotContain("今日社交关注", result);
    }

    [Fact]
    public void BuildDailySalience_ChineseMode_EmitsLocalizedEntries()
    {
        using (TestEnv.UseIsolatedLocale("zh"))
        {
            EnterZhHeadless();
            var character = MakeCharacter("Alex", new Dictionary<string, BioData.ListEntry>
            {
                ["George"] = RelEntry("George", "George", "脾气暴躁却嘴硬心软的老人。"),
            });

            string result = Prompts.PromptsBlocks.BuildDailySalience(character, MakeContext(0));

            Assert.False(string.IsNullOrEmpty(result));
            Assert.StartsWith("## 今日社交关注", result);
            Assert.Contains("今天在日常闲聊中", result);
            // heading 经 GetLocalizedName → 简中字典兜底 "乔治"。
            Assert.Contains("* **乔治**: 脾气暴躁却嘴硬心软的老人。", result);
            Assert.DoesNotContain("Today's Social Salience", result);
        }
    }

    // ───────────────────────────────────────────────────────────────────────
    // 3. NpcConstantContext 纯净：旧版 biographyRelationships 标题移除
    // ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void NpcConstantContext_NoLongerContainsLegacyRelationshipsHeading()
    {
        // ── 中文环境 ──
        using (TestEnv.UseIsolatedLocale("zh"))
        {
            EnterZhHeadless();
            var character = MakeCharacter("Alex", new Dictionary<string, BioData.ListEntry>
            {
                ["George"] = RelEntry("George", "George", "脾气暴躁却嘴硬心软的老人。"),
            });
            var prompts = MakePrompts(MakeContext(0), character, "Alex");

            string result = null;
            TestEnvironment.WithWorldReady(true, () => result = prompts.NpcConstantContext);

            Assert.False(string.IsNullOrEmpty(result));
            // 正向对照：REL-002 已知关系骨架仍在常量上下文中（证明方法确实跑通）。
            Assert.Contains("## 已知关系骨架", result);
            // 旧版 biographyRelationships（每日轮换）标题彻底移除。
            Assert.DoesNotContain("## 人际关系", result);
            Assert.DoesNotContain("## Relationships", result);
        }

        // ── 英文环境 ──
        using (TestEnv.UseIsolatedLocale("en"))
        {
            InstallGame1LocationShim();
            // zh 子运行残留的 CurrentLanguageCode 必须显式拨回（Dispose 才统一还原）。
            LocalizedContentManager.CurrentLanguageCode = LocalizedContentManager.LanguageCode.en;
            var character = MakeCharacter("Alex", new Dictionary<string, BioData.ListEntry>
            {
                ["George"] = RelEntry("George", "George", "A grumpy old man."),
            });
            var prompts = MakePrompts(MakeContext(0), character, "Alex");

            string result = null;
            TestEnvironment.WithWorldReady(true, () => result = prompts.NpcConstantContext);

            Assert.False(string.IsNullOrEmpty(result));
            Assert.Contains("## Known Relationship Backbone", result);
            Assert.DoesNotContain("## Relationships", result);
            Assert.DoesNotContain("## 人际关系", result);
        }
    }

    // ───────────────────────────────────────────────────────────────────────
    // 4. 透镜：非中文模式经官方本地化名匹配（REL-005 isZh 守卫废除）
    // ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void LensBuilder_NonChineseMode_MatchesOfficialLocalizedName()
    {
        using (TestEnv.UseIsolatedLocale("en"))
        {
            InstallGame1LocationShim();
            LocalizedContentManager.CurrentLanguageCode = LocalizedContentManager.LanguageCode.en;
            var characterData = CreateEmptyCharacterData();
            characterData["ModNpc"] = CreateCharacterData("星尘");
            CharacterDataField?.SetValue(null, characterData);

            var character = MakeCharacter("Alex", new Dictionary<string, BioData.ListEntry>
            {
                ["ModNpc"] = RelEntry("ModNpc", "ModNpc", "A wandering stargazer."),
            });
            var ctx = MakeContext(0);
            ctx.ChatHistory.Add(new ConversationElement("我昨天在镇上遇到星尘了", IsPlayerLine: true));

            string result = RelationshipAttitudeLensBuilder.Build(character, ctx);

            Assert.False(string.IsNullOrEmpty(result));
            // 1.6 资产元数据层的官方译名 "星尘" 在非中文模式下同样参与匹配。
            Assert.Contains("target=\"星尘\"", result);
            Assert.Contains("A wandering stargazer.", result);
        }
    }
}
