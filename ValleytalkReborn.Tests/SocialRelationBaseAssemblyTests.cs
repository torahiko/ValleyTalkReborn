// SocialRelationBaseAssemblyTests.cs
// VT-SOCIAL-02-Phase2-EmotionAndRelationCoupling — RelationBase 装配接通
// <relationship_lens> 的契约测试（与 Tier2b 的 <social_lens> 正交互斥，互不注入）。
//
// 验证目标：
//   1) lens 标签在 RelationBase 输出中出现恰好 1 次（含未婚与婚后）。
//   2) 婚后冷战/室友态抑制盲目 marriageSentimentGood，婚姻感知听从动态透镜。
//   3) 和谐态保留原版甜言蜜语（抑制按原型门控，非一刀切）。
//
// Headless 夹具纪律（沿用 RelationshipAttitudeLensBuilderTests / GiftPipelineHistoryTests）：
// - TestEnv.UseIsolatedLocale 安装 FakeMonitor/FakeModHelper（PromptCache 静态构造与
//   RefreshPromptCache 的 catch 分支依赖非空 SHelper/SMonitor），Dispose 时还原。
// - FakePlayer.Install 提供带标量 Net 字段的 Farmer（farmer.Gender 读取必需）；
//   friendshipData/modData 属 NetStringDictionary 系（不承 NetFieldBase），本文件反射回填。
// - CharacterData 位于 StardewValley.GameData.dll（测试工程不引用），经 Game1.characterData
//   字段类型的泛型实参反射 Activator 构造；Gender 默认值即 Male，无需赋值。
// - 提示词文案经 Bio.PromptOverrides 注入 MARKER（Util.GetString 第 1 优先级），
//   断言与 i18n/内容包解耦。
// - BuildRelationBase 为 Prompts.PromptsBlocks（internal 嵌套类）且形参含 CharacterData，
//   统一经反射调用，TIE 解包后透传原始异常。

using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.Serialization;
using Netcode;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Mods;
using StardewValley.Network;
using ValleytalkReborn.Dialogue.Coordination;
using ValleytalkReborn;
using ValleytalkReborn.Social;
using ValleytalkReborn.Tests;
using Xunit;
// StardewValley.Character 与 ValleytalkReborn.Character 二义性消解。
using Character = ValleytalkReborn.Character;

[Collection("StaticGlobalStateCollection")]
public class SocialRelationBaseAssemblyTests : IDisposable
{
    private static readonly FieldInfo CharacterDataField =
        typeof(Game1).GetField("characterData", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
    private static readonly FieldInfo Game1InstanceField =
        typeof(Game1).GetField("game1", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
    private static readonly FieldInfo FriendshipDataField =
        typeof(Farmer).GetField("friendshipData", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
    private static readonly FieldInfo ModDataBackingField = FindModDataBackingField();
    private static readonly MethodInfo BuildRelationBaseMethod =
        typeof(Prompts).GetNestedType("PromptsBlocks", BindingFlags.NonPublic)
            ?.GetMethod("BuildRelationBase", BindingFlags.Static | BindingFlags.NonPublic);

    private readonly LocalizedContentManager.LanguageCode _originalLanguageCode;
    private readonly object _originalCharacterData;
    private readonly object _originalGame1;
    private readonly IDisposable _localeScope;
    private readonly IDisposable _playerScope;
    private readonly Farmer _farmer;

    public SocialRelationBaseAssemblyTests()
    {
        _originalLanguageCode = LocalizedContentManager.CurrentLanguageCode;
        _originalCharacterData = CharacterDataField?.GetValue(null);
        _originalGame1 = Game1InstanceField?.GetValue(null);

        // 顺序纪律：先 UseIsolatedLocale（装 FakeMonitor/FakeModHelper），再覆盖 Config，
        // 让 Dispose 恢复原始静态（见 TestEnv.UseIsolatedLocale 契约）。
        _localeScope = TestEnv.UseIsolatedLocale("en");
        _playerScope = FakePlayer.Install("虎彦");
        _farmer = Game1.player;

        // FakePlayer 通用补齐只覆盖 NetFieldBase 系标量；NetStringDictionary 系需显式回填。
        FriendshipDataField?.SetValue(_farmer, new NetStringDictionary<Friendship, NetRef<Friendship>>());
        ModDataBackingField?.SetValue(_farmer, new ModDataDictionary());
    }

    public void Dispose()
    {
        _playerScope?.Dispose();
        _localeScope?.Dispose();
        LocalizedContentManager.CurrentLanguageCode = _originalLanguageCode;
        CharacterDataField?.SetValue(null, _originalCharacterData);
        Game1InstanceField?.SetValue(null, _originalGame1);
    }

    private static FieldInfo FindModDataBackingField()
    {
        for (var t = typeof(Farmer); t != null && t != typeof(object); t = t.BaseType)
        {
            foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            {
                if (f.FieldType == typeof(ModDataDictionary)) return f;
            }
        }
        return null;
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0, index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }

    private void InstallFriendship(string npcName, FriendshipStatus status, bool roommate = false)
    {
        var friendship = new Friendship();
        friendship.Status = status;
        if (roommate) friendship.RoommateMarriage = true;
        var dict = (NetStringDictionary<Friendship, NetRef<Friendship>>)FriendshipDataField.GetValue(_farmer);
        dict.FieldDict[npcName] = new NetRef<Friendship>(friendship);
    }

    private Character MakeCharacter(string name, params (string key, string marker)[] promptOverrides)
    {
        var c = new Character(name, null);
        var bioField = typeof(Character).GetField("_bioData", BindingFlags.Instance | BindingFlags.NonPublic);
        var bio = new BioData();
        typeof(BioData).GetField("name", BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(bio, name);
        // CheckBio 守卫要求 Biography 非空，否则触发 Game1.content 重载路径（headless NRE）。
        bio.Biography = "Test biography for " + name + ".";
        foreach (var (key, marker) in promptOverrides)
            bio.PromptOverrides[key] = marker;
        bioField.SetValue(c, bio);
        return c;
    }

    private static object MakeNpcData()
    {
        var characterDataType = CharacterDataField.FieldType.GetGenericArguments()[1];
        return Activator.CreateInstance(characterDataType);
    }

    private static string InvokeBuildRelationBase(Character character, DialogueContext context, object npcData, string name)
    {
        try
        {
            return (string)BuildRelationBaseMethod.Invoke(null, new object[]
            {
                character, context, npcData, name, null, new ContextFlags { IncludeFarmDetails = false }
            });
        }
        catch (TargetInvocationException ex)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
            throw; // 不可达：Capture.Throw 总是抛出
        }
    }

    private void UseZhLanguage()
    {
        ModEntry.Config = new ModConfig { LanguageOverride = "zh" };
    }

    // ── 未婚：lens 恰好注入 1 次，与 Tier2b <social_lens> 正交（互不出现） ──
    // 保持 en 语言（无 LanguageOverride），断言英文 lens 锚点。

    [Fact]
    public void T1_Unmarried_InjectsRelationshipLensExactlyOnce()
    {
        var character = MakeCharacter("Alex",
            ("friendshipNote", "FRIENDSHIP-NOTE-MARKER"),
            ("friendshipCloseFriends", "CLOSE-FRIENDS-MARKER {{Hearts}} {{Note}}"));
        var context = new DialogueContext { Hearts = 7, Children = new List<ChildDescription>() };

        string output = InvokeBuildRelationBase(character, context, MakeNpcData(), "Alex");

        // 未婚基线文案照常输出（Util.GetString 第 1 优先级 marker 证明 unmarried 分支被执行）。
        Assert.Contains("FRIENDSHIP-NOTE-MARKER", output);
        Assert.Contains("CLOSE-FRIENDS-MARKER 7 FRIENDSHIP-NOTE-MARKER", output);
        // lens 恰好 1 次（未存档案 → 播种默认 7 心 → GuardedAcquaintance → 九宫格 Stranger 契约）。
        Assert.Equal(1, CountOccurrences(output, "<relationship_lens>"));
        Assert.Equal(1, CountOccurrences(output, "</relationship_lens>"));
        Assert.Contains("Perspective anchor", output);
        // 与 Tier2b 第三方透镜正交互斥。
        Assert.DoesNotContain("<social_lens", output);
    }

    // ── 婚后和谐：原版甜言蜜语保留（抑制按原型门控） ──

    [Fact]
    public void T2_Married_Harmonious_KeepsVanillaSweetTalk()
    {
        UseZhLanguage();
        InstallFriendship("Alex", FriendshipStatus.Married);
        var character = MakeCharacter("Alex",
            ("coreMarried", "CORE-MARRIED-MARKER {{Name}}"),
            ("childrenNone", "CHILDREN-NONE-MARKER"),
            ("generalTheMarriage", "THE-MARRIAGE-MARKER"),
            ("marriageSentimentGood", "SWEET-TALK-MARKER {{Name}}"));
        var context = new DialogueContext { Hearts = 14, Children = new List<ChildDescription>() };

        string output = InvokeBuildRelationBase(character, context, MakeNpcData(), "Alex");

        Assert.Contains("CORE-MARRIED-MARKER Alex", output);
        Assert.Contains("CHILDREN-NONE-MARKER", output);
        Assert.Contains("SWEET-TALK-MARKER Alex", output);
        Assert.Equal(1, CountOccurrences(output, "<relationship_lens>"));
        Assert.Contains("深层默契", output); // DomesticHarmonious zh 契约
    }

    // ── 婚后冷战：盲目 marriageSentimentGood 被抑制，lens 接管婚姻感知（14 心死锁修复） ──

    [Fact]
    public void T3_Married_ColdSpell_SuppressesBlindSweetTalk()
    {
        UseZhLanguage();
        InstallFriendship("Alex", FriendshipStatus.Married);
        SocialGraphService.Instance.SaveProfile(_farmer, "Alex", new SocialProfile { UnresolvedFriction = 45, Archetype = SocialArchetype.DomesticColdSpell });
        var character = MakeCharacter("Alex",
            ("coreMarried", "CORE-MARRIED-MARKER {{Name}}"),
            ("childrenNone", "CHILDREN-NONE-MARKER"),
            ("generalTheMarriage", "THE-MARRIAGE-MARKER"),
            ("marriageSentimentGood", "SWEET-TALK-MARKER {{Name}}"));
        var context = new DialogueContext { Hearts = 14, Children = new List<ChildDescription>() };

        string output = InvokeBuildRelationBase(character, context, MakeNpcData(), "Alex");

        Assert.Contains("CORE-MARRIED-MARKER Alex", output);
        // 14 心 > 12 本应输出 sentimentGood，冷战态下被抑制。
        Assert.DoesNotContain("SWEET-TALK-MARKER", output);
        Assert.Equal(1, CountOccurrences(output, "<relationship_lens>"));
        Assert.Contains("婚后指标", output);     // 婚后 lens 指标行
        Assert.Contains("心寒委屈", output);     // DomesticColdSpell zh 锚点
        Assert.Contains("真诚道歉", output);     // 冷战行为契约（解冻可能）
    }

    // ── 婚后室友态：coreRoommates 输出 + 抑制甜言蜜语 + 室友化契约 ──

    [Fact]
    public void T4_Roommate_SuppressesSweetTalkAndEmitsRoommateCore()
    {
        UseZhLanguage();
        InstallFriendship("Alex", FriendshipStatus.Married, roommate: true);
        SocialGraphService.Instance.SaveProfile(_farmer, "Alex", new SocialProfile { DomesticDistance = 60, Archetype = SocialArchetype.DomesticRoommate });
        var character = MakeCharacter("Alex",
            ("coreRoommates", "CORE-ROOMMATES-MARKER {{Name}}"),
            ("generalBeingRoommates", "BEING-ROOMMATES-MARKER"),
            ("marriageSentimentGood", "SWEET-TALK-MARKER {{Name}}"));
        var context = new DialogueContext { Hearts = 14, Children = new List<ChildDescription>() };

        string output = InvokeBuildRelationBase(character, context, MakeNpcData(), "Alex");

        Assert.Contains("CORE-ROOMMATES-MARKER Alex", output);
        Assert.DoesNotContain("SWEET-TALK-MARKER", output);
        Assert.Equal(1, CountOccurrences(output, "<relationship_lens>"));
        Assert.Contains("室友化", output); // DomesticRoommate zh 契约
    }
}
