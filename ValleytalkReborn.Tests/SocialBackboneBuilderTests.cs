// SocialBackboneBuilderTests.cs
// REL-002 — Unit tests for SocialBackboneBuilder.Build.
// 纯内存装配路径：不读取任何游戏状态，显示名解析走纯委托。
// 覆盖票面验收 1-8：插入顺序无关性、候选选择、双语标签与回退、
// 私密哨兵隔离、非法输入可观察失败、序列化兼容、空候选返回空串。

using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using ValleytalkReborn;
using Xunit;

public class SocialBackboneBuilderTests
{
    private static BioData.ListEntry Entry(
        string description = null,
        int requiredHearts = 0,
        string en = null,
        string zh = null)
    {
        return new BioData.ListEntry
        {
            Description = description ?? "desc-" + Guid.NewGuid().ToString("N"),
            RequiredHearts = requiredHearts,
            PublicIdentityEn = en ?? string.Empty,
            PublicIdentityZh = zh ?? string.Empty,
        };
    }

    private static string BuildEn(Dictionary<string, BioData.ListEntry> relationships, string speaker = "Alex")
        => SocialBackboneBuilder.Build(speaker, relationships, isZh: false, resolveDisplayName: key => "Name_" + key);

    private static string BuildZh(Dictionary<string, BioData.ListEntry> relationships, string speaker = "Alex")
        => SocialBackboneBuilder.Build(speaker, relationships, isZh: true, resolveDisplayName: key => "名字_" + key);

    // ── 验收 1：字典插入顺序不影响输出 ──

    [Fact]
    public void InsertionOrderIndependence_OutputIsIdentical()
    {
        var orderA = new Dictionary<string, BioData.ListEntry>
        {
            ["George"] = Entry(en: "grandfather", zh: "祖父辈亲属"),
            ["Evelyn"] = Entry(en: "grandmother", zh: "祖母辈亲属"),
            ["Dusty"] = Entry(en: "family dog", zh: "家犬"),
        };
        var orderB = new Dictionary<string, BioData.ListEntry>
        {
            ["Dusty"] = Entry(en: "family dog", zh: "家犬"),
            ["Evelyn"] = Entry(en: "grandmother", zh: "祖母辈亲属"),
            ["George"] = Entry(en: "grandfather", zh: "祖父辈亲属"),
        };

        string enA = BuildEn(orderA);
        string enB = BuildEn(orderB);
        Assert.Equal(enA, enB, StringComparer.Ordinal);

        string zhA = BuildZh(orderA);
        string zhB = BuildZh(orderB);
        Assert.Equal(zhA, zhB, StringComparer.Ordinal);
    }

    // ── 验收 2：显式公开标签不受心数门槛抑制；无标签且心数大于 0 不输出 ──

    [Fact]
    public void ExplicitLabel_EmittedEvenWhenRequiredHeartsAboveZero()
    {
        var relationships = new Dictionary<string, BioData.ListEntry>
        {
            ["Haley"] = Entry(requiredHearts: 6, en: "town peer", zh: "同镇伙伴"),
        };

        string output = BuildEn(relationships);

        Assert.Contains("Name_Haley (town peer)", output, StringComparison.Ordinal);
    }

    [Fact]
    public void NoLabel_RequiredHeartsAboveZero_ExcludedFromBackbone()
    {
        var relationships = new Dictionary<string, BioData.ListEntry>
        {
            ["Haley"] = Entry(requiredHearts: 6, description: "SECRET-SENTINEL private description"),
        };

        string output = BuildEn(relationships);

        Assert.Equal(string.Empty, output);
        Assert.DoesNotContain("SECRET-SENTINEL", output);
    }

    // ── 验收 3：无标签且 RequiredHearts 等于 0 只输出名字 ──

    [Fact]
    public void NoLabel_RequiredHeartsZero_NameOnly()
    {
        var relationships = new Dictionary<string, BioData.ListEntry>
        {
            ["Haley"] = Entry(requiredHearts: 0),
        };

        string output = BuildEn(relationships);

        Assert.Contains("Name_Haley", output, StringComparison.Ordinal);
        Assert.DoesNotContain("(", output);
    }

    // ── 验收 4：Description 私密内容绝不进入骨架段 ──

    [Fact]
    public void PrivateDescriptionSentinel_NeverLeaksIntoBackbone()
    {
        var relationships = new Dictionary<string, BioData.ListEntry>
        {
            ["George"] = Entry(description: "PUBLIC-OK PRIVATE-SENTINEL", en: "grandfather", zh: "祖父辈亲属"),
            ["Evelyn"] = Entry(description: "another PRIVATE-SENTINEL line", requiredHearts: 0),
        };

        string zh = BuildZh(relationships);
        string en = BuildEn(relationships);

        Assert.DoesNotContain("PRIVATE-SENTINEL", zh);
        Assert.DoesNotContain("PRIVATE-SENTINEL", en);
    }

    // ── 验收 5：中英文标签优先级与跨语言回退 ──

    [Fact]
    public void ChineseMode_PrefersZhLabel_ThenEnLabel_ThenNameOnly()
    {
        var relationships = new Dictionary<string, BioData.ListEntry>
        {
            ["George"] = Entry(en: "grandfather", zh: "祖父辈亲属"),
            ["Evelyn"] = Entry(en: "grandmother"),
            ["Dusty"] = Entry(),
        };

        string output = BuildZh(relationships);

        Assert.Contains("## 已知关系骨架", output, StringComparison.Ordinal);
        Assert.Contains("名字_George（祖父辈亲属）", output, StringComparison.Ordinal);
        Assert.Contains("名字_Evelyn（grandmother）", output, StringComparison.Ordinal);
        Assert.Contains("名字_Dusty", output, StringComparison.Ordinal);
        Assert.Contains("；", output, StringComparison.Ordinal);
    }

    [Fact]
    public void EnglishMode_PrefersEnLabel_ThenZhLabel_ThenNameOnly()
    {
        var relationships = new Dictionary<string, BioData.ListEntry>
        {
            ["George"] = Entry(en: "grandfather", zh: "祖父辈亲属"),
            ["Evelyn"] = Entry(zh: "祖母辈亲属"),
            ["Dusty"] = Entry(),
        };

        string output = BuildEn(relationships);

        Assert.Contains("## Known Relationship Backbone", output, StringComparison.Ordinal);
        Assert.Contains("Name_George (grandfather)", output, StringComparison.Ordinal);
        Assert.Contains("Name_Evelyn (祖母辈亲属)", output, StringComparison.Ordinal);
        Assert.Contains("Name_Dusty", output, StringComparison.Ordinal);
        Assert.Contains("; ", output, StringComparison.Ordinal);
    }

    // ── 验收 6：自身排除与非法输入可观察失败 ──

    [Fact]
    public void SelfEntry_ExcludedCaseInsensitively()
    {
        var relationships = new Dictionary<string, BioData.ListEntry>
        {
            ["alex"] = Entry(en: "self label"),
        };

        string output = BuildEn(relationships, speaker: "Alex");

        Assert.Equal(string.Empty, output);
    }

    [Fact]
    public void BlankSpeakerName_ThrowsArgument()
    {
        var relationships = new Dictionary<string, BioData.ListEntry>();

        Assert.Throws<ArgumentException>(() =>
            SocialBackboneBuilder.Build("  ", relationships, isZh: false, resolveDisplayName: key => key));
    }

    [Fact]
    public void NullRelationships_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            SocialBackboneBuilder.Build("Alex", null, isZh: false, resolveDisplayName: key => key));
    }

    [Fact]
    public void NullResolver_ThrowsArgumentNull()
    {
        Assert.Throws<ArgumentNullException>(() =>
            SocialBackboneBuilder.Build("Alex", new Dictionary<string, BioData.ListEntry>(), isZh: false, resolveDisplayName: null));
    }

    [Fact]
    public void NullEntryValue_ThrowsInvalidOperation_EvenWhenExcludedAsCandidate()
    {
        var relationships = new Dictionary<string, BioData.ListEntry>
        {
            ["George"] = Entry(en: "grandfather"),
            ["Ghost"] = null, // RequiredHearts 默认 0 本可入选，但值校验先行
        };

        var ex = Assert.Throws<InvalidOperationException>(() => BuildEn(relationships));
        Assert.Contains("Alex", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Ghost", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void BlankEntryKey_ThrowsInvalidOperation()
    {
        var relationships = new Dictionary<string, BioData.ListEntry>
        {
            ["   "] = Entry(en: "label"),
        };

        Assert.Throws<InvalidOperationException>(() => BuildEn(relationships));
    }

    [Fact]
    public void MultilineLabel_ThrowsInvalidOperation()
    {
        var relationships = new Dictionary<string, BioData.ListEntry>
        {
            ["George"] = Entry(en: "grand\nfather"),
        };

        Assert.Throws<InvalidOperationException>(() => BuildEn(relationships));
    }

    [Fact]
    public void OverlongLabel_ThrowsInvalidOperation()
    {
        var relationships = new Dictionary<string, BioData.ListEntry>
        {
            ["George"] = Entry(en: new string('x', 49)),
        };

        Assert.Throws<InvalidOperationException>(() => BuildEn(relationships));

        var atLimit = new Dictionary<string, BioData.ListEntry>
        {
            ["George"] = Entry(en: new string('x', 48)),
        };
        Assert.Contains("Name_George", BuildEn(atLimit), StringComparison.Ordinal);
    }

    [Fact]
    public void BlankDisplayName_ThrowsInvalidOperation()
    {
        var relationships = new Dictionary<string, BioData.ListEntry>
        {
            ["George"] = Entry(en: "grandfather"),
        };

        var ex = Assert.Throws<InvalidOperationException>(() =>
            SocialBackboneBuilder.Build("Alex", relationships, isZh: false, resolveDisplayName: key => "   "));
        Assert.Contains("George", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ResolverException_PropagatesOriginalException()
    {
        var relationships = new Dictionary<string, BioData.ListEntry>
        {
            ["George"] = Entry(en: "grandfather"),
        };
        var original = new InvalidTimeZoneException("resolver blew up");

        var thrown = Assert.Throws<InvalidTimeZoneException>(() =>
            SocialBackboneBuilder.Build("Alex", relationships, isZh: false, resolveDisplayName: key => throw original));

        Assert.Same(original, thrown);
    }

    // ── 验收 7：序列化往返与旧卡兼容 ──

    [Fact]
    public void PublicIdentityFields_SurviveNewtonsoftRoundTrip()
    {
        var entry = new BioData.ListEntry
        {
            id = "George",
            Heading = "George",
            Description = "Your grandfather.",
            RequiredHearts = 2,
            PublicIdentityEn = "grandfather",
            PublicIdentityZh = "祖父辈亲属",
        };

        string json = JsonConvert.SerializeObject(entry);
        var restored = JsonConvert.DeserializeObject<BioData.ListEntry>(json);

        Assert.Equal("grandfather", restored.PublicIdentityEn);
        Assert.Equal("祖父辈亲属", restored.PublicIdentityZh);
        Assert.Equal(2, restored.RequiredHearts);
        Assert.Equal("Your grandfather.", restored.Description);
    }

    [Fact]
    public void LegacyJsonWithoutNewFields_DeserializesToBlankLabels()
    {
        const string legacyJson = @"{ ""id"": ""George"", ""Heading"": ""George"", ""RequiredHearts"": 0, ""Description"": ""Old card."" }";

        var restored = JsonConvert.DeserializeObject<BioData.ListEntry>(legacyJson);

        Assert.Equal(string.Empty, restored.PublicIdentityEn);
        Assert.Equal(string.Empty, restored.PublicIdentityZh);
        Assert.Equal("Old card.", restored.Description);
    }

    [Fact]
    public void BioDataRelationships_WithPublicIdentityLabels_Deserializes()
    {
        const string bioJson = @"{ ""Relationships"": { ""George"": { ""PublicIdentityEn"": ""grandfather"", ""PublicIdentityZh"": ""祖父辈亲属"" } } }";

        var bio = JsonConvert.DeserializeObject<BioData>(bioJson);

        Assert.Equal("grandfather", bio.Relationships["George"].PublicIdentityEn);
        Assert.Equal("祖父辈亲属", bio.Relationships["George"].PublicIdentityZh);
    }

    // ── 验收 8：无公开候选返回空串 ──

    [Fact]
    public void NoPublicCandidates_ReturnsEmptyString()
    {
        var relationships = new Dictionary<string, BioData.ListEntry>
        {
            ["Haley"] = Entry(requiredHearts: 4),
            ["Sam"] = Entry(requiredHearts: 10),
        };

        Assert.Equal(string.Empty, BuildEn(relationships));
        Assert.Equal(string.Empty, BuildEn(new Dictionary<string, BioData.ListEntry>()));
    }
}
