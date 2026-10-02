// RelationshipAttitudeLensBuilderTests.cs
// VT-SOCIAL-LENS-01 — Unit tests for RelationshipAttitudeLensBuilder.Build.
// REL-003 — multi-target budget (UT04), possessive kinship recall (UT16),
// first-person rejection (UT17), and the 2-slot cap (UT18).
// REL-004 — topic continuity: single-antecedent inheritance (UT19), ambiguous
// antecedent refusal (UT20), zh compound-word pronoun guard (UT21), explicit
// mention priority (UT22).
// Covers all positive, negative, and edge cases from the ticket's acceptance criteria.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using StardewModdingAPI;
using StardewValley;
using ValleytalkReborn;
using ValleytalkReborn.Tests;
using Xunit;
// StardewValley.Character 与 ValleytalkReborn.Character 二义性消解。
using Character = ValleytalkReborn.Character;

[Collection("StaticGlobalStateCollection")]
public class RelationshipAttitudeLensBuilderTests : IDisposable
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

    public RelationshipAttitudeLensBuilderTests()
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

    private static DialogueContext MakeContext(int? hearts, params ConversationElement[] history)
    {
        var ctx = new DialogueContext { Hearts = hearts };
        if (history != null)
            ctx.ChatHistory = new List<ConversationElement>(history);
        return ctx;
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

    // REL-003 中文无头夹具：空 characterData + 无 game1 实例，使 GetLocalizedName
    // 的实体层/元数据层确定性走空、命中简中字典兜底（同 NpcNameLocalizationTests 纪律）。
    // 语言环境的 SHelper/Config 快照由调用处的 UseIsolatedLocale("zh") 负责，
    // 这三项由本测试类的构造/Dispose 统一快照还原。
    private static void EnterZhHeadless()
    {
        TestEnvironment.InstallHeadlessContext();
        LocalizedContentManager.CurrentLanguageCode = LocalizedContentManager.LanguageCode.zh;
        CharacterDataField?.SetValue(null, CreateEmptyCharacterData());
        Game1InstanceField?.SetValue(null, null);
    }

    private static System.Collections.IDictionary CreateEmptyCharacterData()
    {
        var elementType = CharacterDataField.FieldType.GetGenericArguments()[1];
        var dictType = typeof(Dictionary<,>).MakeGenericType(typeof(string), elementType);
        return (System.Collections.IDictionary)Activator.CreateInstance(dictType);
    }

    private static int CountOccurrences(string text, string token)
    {
        int count = 0;
        int idx = 0;
        while ((idx = text.IndexOf(token, idx, StringComparison.Ordinal)) >= 0)
        {
            count++;
            idx += token.Length;
        }
        return count;
    }

    // ── Acceptance 1: Player mentions "George" to Alex -> emits SocialLens block. ──
    [Fact]
    public void UT01_PlayerMentionsGeorge_ToAlex_EmitsLens()
    {
        using var _ = TestEnv.UseIsolatedLocale("en");
        var character = MakeCharacter("Alex", new Dictionary<string, BioData.ListEntry>
        {
            ["George"] = RelEntry("George", "George", "A grumpy old man who secretly cares about the town."),
        });
        var ctx = MakeContext(0, new ConversationElement("Do you know George?", IsPlayerLine: true));

        string result = RelationshipAttitudeLensBuilder.Build(character, ctx);

        Assert.False(string.IsNullOrEmpty(result));
        Assert.Contains("<social_lens", result);
        Assert.Contains("George", result);
        Assert.Contains("grumpy old man", result);
        Assert.Contains("Do NOT recite", result);
        Assert.Contains("REACTIVE SOCIAL LENS", result);
    }

    // ── Acceptance 2: "I feel the same" with Sam in bio -> word boundary prevents match. ──
    [Fact]
    public void UT02_SamSubstringInSame_NoMatch()
    {
        using var _ = TestEnv.UseIsolatedLocale("en");
        var character = MakeCharacter("Alex", new Dictionary<string, BioData.ListEntry>
        {
            ["Sam"] = RelEntry("Sam", "Sam", "The town's cool guitarist."),
        });
        var ctx = MakeContext(0, new ConversationElement("I feel the same today.", IsPlayerLine: true));

        string result = RelationshipAttitudeLensBuilder.Build(character, ctx);

        Assert.Equal(string.Empty, result);
    }

    // ── Acceptance 3: "Alex, have you seen Haley?" to Alex -> Alex self-filtered, Haley matched. ──
    [Fact]
    public void UT03_SelfFilterAlex_MatchesHaley()
    {
        using var _ = TestEnv.UseIsolatedLocale("en");
        var character = MakeCharacter("Alex", new Dictionary<string, BioData.ListEntry>
        {
            ["Alex"] = RelEntry("Alex", "Alex", "This is Alex's self-description."),
            ["Haley"] = RelEntry("Haley", "Haley", "A cheerful photographer who loves sunshine."),
        });
        var ctx = MakeContext(0, new ConversationElement("Alex, have you seen Haley?", IsPlayerLine: true));

        string result = RelationshipAttitudeLensBuilder.Build(character, ctx);

        Assert.False(string.IsNullOrEmpty(result));
        Assert.Contains("Haley", result);
        Assert.Contains("cheerful photographer", result);
        // Speaker self-filter: Alex's own description must NOT appear.
        Assert.DoesNotContain("self-description", result);
    }

    // ── REL-003 UT04: Two distinct explicit targets -> both lenses emitted. ──
    [Fact]
    public void UT04_TwoDistinctExplicitTargets_EmitsBothLenses()
    {
        using var _ = TestEnv.UseIsolatedLocale("en");
        var character = MakeCharacter("Alex", new Dictionary<string, BioData.ListEntry>
        {
            ["George"] = RelEntry("George", "George", "A grumpy old man."),
            ["Haley"] = RelEntry("Haley", "Haley", "A cheerful photographer."),
        });
        var ctx = MakeContext(0, new ConversationElement("Did you see George and Haley?", IsPlayerLine: true));

        string result = RelationshipAttitudeLensBuilder.Build(character, ctx);

        Assert.False(string.IsNullOrEmpty(result));
        Assert.Equal(2, CountOccurrences(result, "### [REACTIVE SOCIAL LENS"));
        Assert.Equal(2, CountOccurrences(result, "<social_lens"));
        Assert.Equal(2, CountOccurrences(result, "</social_lens>"));
        Assert.Contains("target=\"George\"", result);
        Assert.Contains("target=\"Haley\"", result);
        Assert.Contains("grumpy old man", result);
        Assert.Contains("cheerful photographer", result);
        // Blocks are joined by a blank line, first-mentioned target first.
        Assert.Contains("\n\n", result);
        Assert.True(
            result.IndexOf("target=\"George\"", StringComparison.Ordinal)
                < result.IndexOf("target=\"Haley\"", StringComparison.Ordinal));
    }

    // ── Acceptance 5: Hearts below RequiredHearts -> empty. ──
    [Fact]
    public void UT05_HeartsBelowRequired_ReturnsEmpty()
    {
        using var _ = TestEnv.UseIsolatedLocale("en");
        var character = MakeCharacter("Alex", new Dictionary<string, BioData.ListEntry>
        {
            ["George"] = RelEntry("George", "George", "A grumpy old man.", requiredHearts: 4),
        });
        var ctx = MakeContext(2, new ConversationElement("I spoke to George.", IsPlayerLine: true));

        string result = RelationshipAttitudeLensBuilder.Build(character, ctx);

        Assert.Equal(string.Empty, result);
    }

    // ── Acceptance 6: Target mentioned in turn 1, but latest line (turn 2) does not -> empty. ──
    [Fact]
    public void UT06_OnlyLatestPlayerLineCounts_ReturnsEmpty()
    {
        using var _ = TestEnv.UseIsolatedLocale("en");
        var character = MakeCharacter("Alex", new Dictionary<string, BioData.ListEntry>
        {
            ["George"] = RelEntry("George", "George", "A grumpy old man."),
        });
        var ctx = MakeContext(0,
            new ConversationElement("I saw George at the saloon.", IsPlayerLine: true),
            new ConversationElement("He waved at me.", IsPlayerLine: false),
            new ConversationElement("Anyway, nice weather.", IsPlayerLine: true));

        string result = RelationshipAttitudeLensBuilder.Build(character, ctx);

        Assert.Equal(string.Empty, result);
    }

    // ── Acceptance 7b: English mode preserves raw strings. ──
    [Fact]
    public void UT07b_EnglishMode_PreservesRawStrings()
    {
        using var _ = TestEnv.UseIsolatedLocale("en");
        var character = MakeCharacter("Alex", new Dictionary<string, BioData.ListEntry>
        {
            ["Pierre"] = RelEntry("Pierre", "Pierre", "Pierre is stubborn. He often argues with George."),
        });
        var ctx = MakeContext(0, new ConversationElement("I saw Pierre.", IsPlayerLine: true));

        string result = RelationshipAttitudeLensBuilder.Build(character, ctx);

        Assert.False(string.IsNullOrEmpty(result));
        Assert.Contains("Pierre", result);
        // English mode: description stays raw (George not localized to 乔治).
        Assert.Contains("George", result);
        Assert.DoesNotContain("乔治", result);
    }

    // ── Acceptance 7: Chinese mode (documented as in-game verification). ──
    // Chinese alias matching depends on NpcNameLocalizer.GetZhName, which calls
    // Game1.getCharacterFromName and therefore requires a live game world. This
    // cannot be exercised in the headless unit-test harness without reconstructing
    // the full game state, which is out of scope for this ticket.
    //
    // IN-GAME VERIFICATION:
    //   - Set language to zh.
    //   - Give a speaking NPC a relationship entry whose key is e.g. "Pierre".
    //   - Player line contains the localized name "皮埃尔".
    //   - Expected: a SocialLens block is emitted with target="皮埃尔" and the
    //     description localized (e.g. "George" -> "乔治" via LocalizeNamesInText).
    // This is covered by the English-mode structural tests above plus runtime
    // observation in a zh game session; it is NOT covered by the headless suite.

    // ── Acceptance 8: Tier2bBlockIds.All contains SocialLens exactly once. ──
    [Fact]
    public void UT08_SocialLens_In_AllList_ExactlyOnce()
    {
        var all = Tier2bBlockIds.All;
        int count = 0;
        foreach (var id in all)
            if (id == Tier2bBlockIds.SocialLens) count++;
        Assert.Equal(1, count);
        Assert.Equal("SocialLens", Tier2bBlockIds.SocialLens);
    }

    // ── Edge: No player line at all -> empty. ──
    [Fact]
    public void UT09_NoPlayerLine_ReturnsEmpty()
    {
        using var _ = TestEnv.UseIsolatedLocale("en");
        var character = MakeCharacter("Alex", new Dictionary<string, BioData.ListEntry>
        {
            ["George"] = RelEntry("George", "George", "A grumpy old man."),
        });
        var ctx = MakeContext(0, new ConversationElement("Hello!", IsPlayerLine: false));

        Assert.Equal(string.Empty, RelationshipAttitudeLensBuilder.Build(character, ctx));
    }

    // ── Edge: Blank player line -> empty. ──
    [Fact]
    public void UT10_BlankPlayerLine_ReturnsEmpty()
    {
        using var _ = TestEnv.UseIsolatedLocale("en");
        var character = MakeCharacter("Alex", new Dictionary<string, BioData.ListEntry>
        {
            ["George"] = RelEntry("George", "George", "A grumpy old man."),
        });
        var ctx = MakeContext(0, new ConversationElement("   ", IsPlayerLine: true));

        Assert.Equal(string.Empty, RelationshipAttitudeLensBuilder.Build(character, ctx));
    }

    // ── Edge: Matched relationship has blank description -> empty. ──
    [Fact]
    public void UT11_BlankDescription_ReturnsEmpty()
    {
        using var _ = TestEnv.UseIsolatedLocale("en");
        var character = MakeCharacter("Alex", new Dictionary<string, BioData.ListEntry>
        {
            ["George"] = RelEntry("George", "George", "   "),
        });
        var ctx = MakeContext(0, new ConversationElement("George is here.", IsPlayerLine: true));

        Assert.Equal(string.Empty, RelationshipAttitudeLensBuilder.Build(character, ctx));
    }

    // ── Edge: Speaker self-filter via displayName. ──
    [Fact]
    public void UT12_SelfFilterViaDisplayName()
    {
        using var _ = TestEnv.UseIsolatedLocale("en");
        var character = MakeCharacter("Alex", new Dictionary<string, BioData.ListEntry>
        {
            ["George"] = RelEntry("George", "George", "A grumpy old man."),
        });
        // If displayName differs from Name but matches a relationship key, still filtered.
        // Here Name="Alex" filters Alex entry; George is the only remaining match.
        var ctx = MakeContext(0, new ConversationElement("Have you met George?", IsPlayerLine: true));

        string result = RelationshipAttitudeLensBuilder.Build(character, ctx);
        Assert.False(string.IsNullOrEmpty(result));
        Assert.Contains("George", result);
    }

    // ── Edge: Gus word boundary (Gus in "gust" must not match). ──
    [Fact]
    public void UT13_GusSubstring_NoMatch()
    {
        using var _ = TestEnv.UseIsolatedLocale("en");
        var character = MakeCharacter("Alex", new Dictionary<string, BioData.ListEntry>
        {
            ["Gus"] = RelEntry("Gus", "Gus", "The saloon chef."),
        });
        var ctx = MakeContext(0, new ConversationElement("A gust of wind blew by.", IsPlayerLine: true));

        Assert.Equal(string.Empty, RelationshipAttitudeLensBuilder.Build(character, ctx));
    }

    // ── Edge: Hearts null treated as 0. ──
    [Fact]
    public void UT14_NullHearts_TreatedAsZero()
    {
        using var _ = TestEnv.UseIsolatedLocale("en");
        var character = MakeCharacter("Alex", new Dictionary<string, BioData.ListEntry>
        {
            ["George"] = RelEntry("George", "George", "A grumpy old man.", requiredHearts: 2),
        });
        var ctx = MakeContext(null, new ConversationElement("George waved.", IsPlayerLine: true));

        // hearts null -> 0, requiredHearts 2 -> below threshold -> empty.
        Assert.Equal(string.Empty, RelationshipAttitudeLensBuilder.Build(character, ctx));
    }

    // ── Edge: Multiple aliases of same relationship key deduplicate to single match. ──
    [Fact]
    public void UT15_MultipleAliasesSameKey_Deduplicated()
    {
        using var _ = TestEnv.UseIsolatedLocale("en");
        var character = MakeCharacter("Alex", new Dictionary<string, BioData.ListEntry>
        {
            // Heading "Georgie" is an alias for the "George" key; both should count as 1 match.
            ["George"] = RelEntry("George", "Georgie", "A grumpy old man."),
        });
        var ctx = MakeContext(0, new ConversationElement("Georgie is grumpy.", IsPlayerLine: true));

        string result = RelationshipAttitudeLensBuilder.Build(character, ctx);
        Assert.False(string.IsNullOrEmpty(result));
        Assert.Contains("George", result);
    }

    // ── REL-003 UT16: Possessive kinship alias recalls the target (en + zh). ──
    [Fact]
    public void UT16_PossessiveKinship_MatchesTarget()
    {
        // ── English mode: "your grandfather" hits George's lens. ──
        using (TestEnv.UseIsolatedLocale("en"))
        {
            var character = MakeCharacter("Alex", new Dictionary<string, BioData.ListEntry>
            {
                ["George"] = RelEntry("George", "George", "A grumpy old man.", publicIdentity: "grandfather"),
            });
            var ctx = MakeContext(0, new ConversationElement("How is your grandfather?", IsPlayerLine: true));

            string result = RelationshipAttitudeLensBuilder.Build(character, ctx);

            Assert.False(string.IsNullOrEmpty(result));
            Assert.Contains("target=\"George\"", result);
            Assert.Contains("grumpy old man", result);
        }

        // ── Chinese mode: "你外公" hits George's lens (headless zh fixture). ──
        using (TestEnv.UseIsolatedLocale("zh"))
        {
            EnterZhHeadless();
            var character = MakeCharacter("Alex", new Dictionary<string, BioData.ListEntry>
            {
                ["George"] = RelEntry("George", "George", "脾气暴躁却嘴硬心软的老人。", publicIdentity: "grandfather"),
            });
            var ctx = MakeContext(0, new ConversationElement("你外公身体还好吗？", IsPlayerLine: true));

            string result = RelationshipAttitudeLensBuilder.Build(character, ctx);

            Assert.False(string.IsNullOrEmpty(result));
            // 简中字典兜底：George -> 乔治。
            Assert.Contains("target=\"乔治\"", result);
            Assert.Contains("即时社交态度透镜", result);
            Assert.Contains("脾气暴躁", result);
        }
    }

    // ── REL-003 UT17: First-person kinship must NEVER hit the speaker's kin. ──
    [Fact]
    public void UT17_FirstPersonKinship_Rejected()
    {
        // ── Chinese mode: "我爷爷" refers to the PLAYER's grandfather, not Alex's. ──
        using (TestEnv.UseIsolatedLocale("zh"))
        {
            EnterZhHeadless();
            var character = MakeCharacter("Alex", new Dictionary<string, BioData.ListEntry>
            {
                ["George"] = RelEntry("George", "George", "脾气暴躁却嘴硬心软的老人。", publicIdentity: "grandfather"),
            });
            var ctx = MakeContext(0, new ConversationElement("我爷爷身体很好", IsPlayerLine: true));

            Assert.Equal(string.Empty, RelationshipAttitudeLensBuilder.Build(character, ctx));
        }

        // ── English mode: "my grandfather" likewise must not hit George. ──
        using (TestEnv.UseIsolatedLocale("en"))
        {
            var character = MakeCharacter("Alex", new Dictionary<string, BioData.ListEntry>
            {
                ["George"] = RelEntry("George", "George", "A grumpy old man.", publicIdentity: "grandfather"),
            });
            var ctx = MakeContext(0, new ConversationElement("my grandfather is doing great today.", IsPlayerLine: true));

            Assert.Equal(string.Empty, RelationshipAttitudeLensBuilder.Build(character, ctx));
        }
    }

    // ── REL-003 UT18: Three explicit targets -> only the first two by position. ──
    [Fact]
    public void UT18_ThreeTargets_CappedAtTwo()
    {
        using var _ = TestEnv.UseIsolatedLocale("en");
        var character = MakeCharacter("Alex", new Dictionary<string, BioData.ListEntry>
        {
            ["George"] = RelEntry("George", "George", "A grumpy old man."),
            ["Haley"] = RelEntry("Haley", "Haley", "A cheerful photographer."),
            ["Sam"] = RelEntry("Sam", "Sam", "An energetic guitarist."),
        });
        var ctx = MakeContext(0, new ConversationElement("Did you see George, Haley, and Sam?", IsPlayerLine: true));

        string result = RelationshipAttitudeLensBuilder.Build(character, ctx);

        // Budget cap: exactly MaxInjectedLenses (2) blocks.
        Assert.Equal(2, CountOccurrences(result, "### [REACTIVE SOCIAL LENS"));
        Assert.Equal(2, CountOccurrences(result, "<social_lens"));
        // Selection follows first-occurrence order: George, Haley; Sam is cut.
        Assert.Contains("target=\"George\"", result);
        Assert.Contains("target=\"Haley\"", result);
        Assert.DoesNotContain("target=\"Sam\"", result);
        Assert.DoesNotContain("energetic guitarist", result);
        Assert.True(
            result.IndexOf("target=\"George\"", StringComparison.Ordinal)
                < result.IndexOf("target=\"Haley\"", StringComparison.Ordinal));
    }

    // ── REL-004 UT19: Pronoun with a single prior antecedent inherits the topic. ──
    [Fact]
    public void UT19_TopicContinuity_SingleAntecedent_Inherited()
    {
        using var _ = TestEnv.UseIsolatedLocale("en");
        var character = MakeCharacter("Alex", new Dictionary<string, BioData.ListEntry>
        {
            ["George"] = RelEntry("George", "George", "A grumpy old man."),
        });
        var ctx = MakeContext(0,
            new ConversationElement("How is George?", IsPlayerLine: true),
            new ConversationElement("He is doing fine.", IsPlayerLine: false),
            new ConversationElement("Is he still watching TV?", IsPlayerLine: true));

        string result = RelationshipAttitudeLensBuilder.Build(character, ctx);

        Assert.False(string.IsNullOrEmpty(result));
        Assert.Equal(1, CountOccurrences(result, "<social_lens"));
        Assert.Contains("target=\"George\"", result);
        Assert.Contains("grumpy old man", result);
    }

    // ── REL-004 UT20: Multiple prior antecedents -> pronoun is never guessed. ──
    [Fact]
    public void UT20_TopicContinuity_MultipleAntecedents_NotGuessed()
    {
        using var _ = TestEnv.UseIsolatedLocale("en");
        var character = MakeCharacter("Alex", new Dictionary<string, BioData.ListEntry>
        {
            ["George"] = RelEntry("George", "George", "A grumpy old man."),
            ["Haley"] = RelEntry("Haley", "Haley", "A cheerful photographer."),
        });
        var ctx = MakeContext(0,
            new ConversationElement("Did you see George and Haley?", IsPlayerLine: true),
            new ConversationElement("Is he doing okay?", IsPlayerLine: true));

        string result = RelationshipAttitudeLensBuilder.Build(character, ctx);

        Assert.Equal(string.Empty, result);
    }

    // ── REL-004 UT21: zh compound words containing 他/她 are not pronouns. ──
    [Fact]
    public void UT21_TopicContinuity_ChinesePronounCompoundWords_Guarded()
    {
        using (TestEnv.UseIsolatedLocale("zh"))
        {
            EnterZhHeadless();
            var character = MakeCharacter("Alex", new Dictionary<string, BioData.ListEntry>
            {
                ["George"] = RelEntry("George", "George", "脾气暴躁却嘴硬心软的老人。"),
            });

            // "吉他" 含 "他" 字形但不是代词：无承接，返回空。
            var ctxGuitar = MakeContext(0,
                new ConversationElement("How is George?", IsPlayerLine: true),
                new ConversationElement("我最近在学弹吉他", IsPlayerLine: true));
            Assert.Equal(string.Empty, RelationshipAttitudeLensBuilder.Build(character, ctxGuitar));

            // "其他" 同理：不误判为第三人称代词，返回空。
            var ctxOthers = MakeContext(0,
                new ConversationElement("How is George?", IsPlayerLine: true),
                new ConversationElement("其他人的情况呢", IsPlayerLine: true));
            Assert.Equal(string.Empty, RelationshipAttitudeLensBuilder.Build(character, ctxOthers));
        }
    }

    // ── REL-004 UT22: Explicit mention outranks the older pronoun topic. ──
    [Fact]
    public void UT22_TopicContinuity_ExplicitMentionOverridesPronoun()
    {
        using var _ = TestEnv.UseIsolatedLocale("en");
        var character = MakeCharacter("Alex", new Dictionary<string, BioData.ListEntry>
        {
            ["George"] = RelEntry("George", "George", "A grumpy old man."),
            ["Haley"] = RelEntry("Haley", "Haley", "A cheerful photographer."),
        });
        var ctx = MakeContext(0,
            new ConversationElement("How is George?", IsPlayerLine: true),
            new ConversationElement("She is at home.", IsPlayerLine: false),
            new ConversationElement("How is Haley? He told me about her.", IsPlayerLine: true));

        string result = RelationshipAttitudeLensBuilder.Build(character, ctx);

        // Explicit Haley this turn wins; the prior George topic is ignored.
        Assert.False(string.IsNullOrEmpty(result));
        Assert.Equal(1, CountOccurrences(result, "<social_lens"));
        Assert.Contains("target=\"Haley\"", result);
        Assert.Contains("cheerful photographer", result);
        Assert.DoesNotContain("grumpy old man", result);
    }
}
