// SocialBackboneBuilderTests.cs
// REL-002/002B — Unit tests for SocialBackboneBuilder.Build.
// 纯内存装配路径：不读取任何游戏状态，显示名解析走纯委托。
// 覆盖票面验收：插入顺序一致性、单一公开标签候选选择（有标签/无标签/心数门禁）、
// 私密哨兵隔离、非法多行与超长标签可观察失败、空候选返回空串、序列化兼容。

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
        string label = null)
    {
        return new BioData.ListEntry
        {
            Description = description ?? "desc-" + Guid.NewGuid().ToString("N"),
            RequiredHearts = requiredHearts,
            PublicIdentity = label ?? string.Empty,
        };
    }

    private static string BuildEn(Dictionary<string, BioData.ListEntry> relationships, string speaker = "Alex")
        => SocialBackboneBuilder.Build(speaker, relationships, isZh: false, resolveDisplayName: key => "Name_" + key);

    private static string BuildZh(Dictionary<string, BioData.ListEntry> relationships, string speaker = "Alex")
        => SocialBackboneBuilder.Build(speaker, relationships, isZh: true, resolveDisplayName: key => "名字_" + key);

    // ── 排序一致性：字典插入顺序不影响输出 ──

    [Fact]
    public void InsertionOrderIndependence_OutputIsIdentical()
    {
        var orderA = new Dictionary<string, BioData.ListEntry>
        {
            ["George"] = Entry(label: "grandfather"),
            ["Evelyn"] = Entry(label: "grandmother"),
            ["Dusty"] = Entry(label: "family dog"),
        };
        var orderB = new Dictionary<string, BioData.ListEntry>
        {
            ["Dusty"] = Entry(label: "family dog"),
            ["Evelyn"] = Entry(label: "grandmother"),
            ["George"] = Entry(label: "grandfather"),
        };

        string enA = BuildEn(orderA);
        string enB = BuildEn(orderB);
        Assert.Equal(enA, enB, StringComparer.Ordinal);

        string zhA = BuildZh(orderA);
        string zhB = BuildZh(orderB);
        Assert.Equal(zhA, zhB, StringComparer.Ordinal);
    }

    // ── 候选选择：显式标签不受心数门槛抑制；无标签且心数大于 0 不输出 ──

    [Fact]
    public void ExplicitLabel_EmittedEvenWhenRequiredHeartsAboveZero()
    {
        var relationships = new Dictionary<string, BioData.ListEntry>
        {
            ["Haley"] = Entry(requiredHearts: 6, label: "peer"),
        };

        string output = BuildEn(relationships);

        Assert.Contains("Name_Haley (peer)", output, StringComparison.Ordinal);
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

    // ── 标签 null 或空白：RECOVERABLE 降级 ──

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

    [Fact]
    public void NullLabel_RequiredHeartsZero_NameOnly()
    {
        var relationships = new Dictionary<string, BioData.ListEntry>
        {
            ["Haley"] = new BioData.ListEntry
            {
                Description = "desc",
                RequiredHearts = 0,
                PublicIdentity = null, // JSON null 按 RECOVERABLE 降级为仅显示名
            },
        };

        string output = BuildEn(relationships);

        Assert.Contains("Name_Haley", output, StringComparison.Ordinal);
        Assert.DoesNotContain("(", output);
    }

    [Fact]
    public void WhitespaceLabel_IsTrimmedToNullAndDegradesToNameOnly()
    {
        var relationships = new Dictionary<string, BioData.ListEntry>
        {
            ["Haley"] = Entry(requiredHearts: 0, label: "   "),
        };

        string output = BuildEn(relationships);

        Assert.Contains("Name_Haley", output, StringComparison.Ordinal);
        Assert.DoesNotContain("(", output);
    }

    // ── 私密内容隔离：Description 绝不进入骨架段 ──

    [Fact]
    public void PrivateDescriptionSentinel_NeverLeaksIntoBackbone()
    {
        var relationships = new Dictionary<string, BioData.ListEntry>
        {
            ["George"] = Entry(description: "PUBLIC-OK PRIVATE-SENTINEL", label: "grandfather"),
            ["Evelyn"] = Entry(description: "another PRIVATE-SENTINEL line", requiredHearts: 0),
        };

        string zh = BuildZh(relationships);
        string en = BuildEn(relationships);

        Assert.DoesNotContain("PRIVATE-SENTINEL", zh);
        Assert.DoesNotContain("PRIVATE-SENTINEL", en);
    }

    // ── 双语条目格式：同一标签，中文括号与英文括号 ──

    [Fact]
    public void ChineseMode_FormatsWithFullWidthParensAndSemicolons()
    {
        var relationships = new Dictionary<string, BioData.ListEntry>
        {
            ["George"] = Entry(label: "grandfather"),
            ["Evelyn"] = Entry(label: "grandmother"),
            ["Dusty"] = Entry(),
        };

        string output = BuildZh(relationships);

        Assert.Contains("## 已知关系骨架", output, StringComparison.Ordinal);
        Assert.Contains("名字_George（grandfather）", output, StringComparison.Ordinal);
        Assert.Contains("名字_Evelyn（grandmother）", output, StringComparison.Ordinal);
        Assert.Contains("名字_Dusty", output, StringComparison.Ordinal);
        Assert.Contains("；", output, StringComparison.Ordinal);
    }

    [Fact]
    public void EnglishMode_FormatsWithAsciiParensAndSemicolons()
    {
        var relationships = new Dictionary<string, BioData.ListEntry>
        {
            ["George"] = Entry(label: "grandfather"),
            ["Evelyn"] = Entry(label: "grandmother"),
            ["Dusty"] = Entry(),
        };

        string output = BuildEn(relationships);

        Assert.Contains("## Known Relationship Backbone", output, StringComparison.Ordinal);
        Assert.Contains("Name_George (grandfather)", output, StringComparison.Ordinal);
        Assert.Contains("Name_Evelyn (grandmother)", output, StringComparison.Ordinal);
        Assert.Contains("Name_Dusty", output, StringComparison.Ordinal);
        Assert.Contains("; ", output, StringComparison.Ordinal);
    }

    // ── 自身排除与参数校验 ──

    [Fact]
    public void SelfEntry_ExcludedCaseInsensitively()
    {
        var relationships = new Dictionary<string, BioData.ListEntry>
        {
            ["alex"] = Entry(label: "self label"),
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

    // ── 非法条目可观察失败 ──

    [Fact]
    public void NullEntryValue_ThrowsInvalidOperation_EvenWhenExcludedAsCandidate()
    {
        var relationships = new Dictionary<string, BioData.ListEntry>
        {
            ["George"] = Entry(label: "grandfather"),
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
            ["   "] = Entry(label: "label"),
        };

        Assert.Throws<InvalidOperationException>(() => BuildEn(relationships));
    }

    [Fact]
    public void MultilineLabel_ThrowsInvalidOperation()
    {
        var relationships = new Dictionary<string, BioData.ListEntry>
        {
            ["George"] = Entry(label: "grand\nfather"),
        };

        Assert.Throws<InvalidOperationException>(() => BuildEn(relationships));
    }

    [Fact]
    public void OverlongLabel_ThrowsInvalidOperation()
    {
        var relationships = new Dictionary<string, BioData.ListEntry>
        {
            ["George"] = Entry(label: new string('x', 49)),
        };

        Assert.Throws<InvalidOperationException>(() => BuildEn(relationships));

        var atLimit = new Dictionary<string, BioData.ListEntry>
        {
            ["George"] = Entry(label: new string('x', 48)),
        };
        Assert.Contains("Name_George", BuildEn(atLimit), StringComparison.Ordinal);
    }

    [Fact]
    public void BlankDisplayName_ThrowsInvalidOperation()
    {
        var relationships = new Dictionary<string, BioData.ListEntry>
        {
            ["George"] = Entry(label: "grandfather"),
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
            ["George"] = Entry(label: "grandfather"),
        };
        var original = new InvalidTimeZoneException("resolver blew up");

        var thrown = Assert.Throws<InvalidTimeZoneException>(() =>
            SocialBackboneBuilder.Build("Alex", relationships, isZh: false, resolveDisplayName: key => throw original));

        Assert.Same(original, thrown);
    }

    // ── 序列化兼容 ──

    [Fact]
    public void PublicIdentity_SurvivesNewtonsoftRoundTrip()
    {
        var entry = new BioData.ListEntry
        {
            id = "George",
            Heading = "George",
            Description = "Your grandfather.",
            RequiredHearts = 2,
            PublicIdentity = "grandfather",
        };

        string json = JsonConvert.SerializeObject(entry);
        var restored = JsonConvert.DeserializeObject<BioData.ListEntry>(json);

        Assert.Equal("grandfather", restored.PublicIdentity);
        Assert.Equal(2, restored.RequiredHearts);
        Assert.Equal("Your grandfather.", restored.Description);
    }

    [Fact]
    public void LegacyJsonWithoutPublicIdentity_DeserializesToBlankLabel()
    {
        const string legacyJson = @"{ ""id"": ""George"", ""Heading"": ""George"", ""RequiredHearts"": 0, ""Description"": ""Old card."" }";

        var restored = JsonConvert.DeserializeObject<BioData.ListEntry>(legacyJson);

        Assert.Equal(string.Empty, restored.PublicIdentity);
        Assert.Equal("Old card.", restored.Description);
    }

    [Fact]
    public void BioDataRelationships_WithPublicIdentity_Deserializes()
    {
        const string bioJson = @"{ ""Relationships"": { ""George"": { ""PublicIdentity"": ""grandfather"" } } }";

        var bio = JsonConvert.DeserializeObject<BioData>(bioJson);

        Assert.Equal("grandfather", bio.Relationships["George"].PublicIdentity);
    }

    // ── 空候选返回空串 ──

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
