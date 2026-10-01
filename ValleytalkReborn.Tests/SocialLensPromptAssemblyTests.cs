// SocialLensPromptAssemblyTests.cs
// REL-001 — Tier2b 装配序列接通 SocialLens 的契约测试。
// 仅验证动态块进入 AssembleTier2bSegment 的装配输出，不涉及模型回复质量。
// 纯字符串装配，不读取任何游戏状态。

using System.Collections.Generic;
using ValleytalkReborn;
using Xunit;

public class SocialLensPromptAssemblyTests
{
    private static InjectionPlan MakePlan(Dictionary<string, string> impulses)
    {
        return new InjectionPlan
        {
            ActiveImpulses = impulses,
        };
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        int count = 0;
        int index = 0;
        while ((index = haystack.IndexOf(needle, index, System.StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += needle.Length;
        }
        return count;
    }

    [Fact]
    public void SocialLensPresent_IsEmittedExactlyOnce()
    {
        const string marker = "SOCIALLENS-BLOCK-MARKER";
        var plan = MakePlan(new Dictionary<string, string>
        {
            [Tier2bBlockIds.SocialLens] = marker,
        });

        string segment = Prompts.AssembleTier2bSegment(plan);

        Assert.Contains(marker, segment);
        Assert.Equal(1, CountOccurrences(segment, marker));
    }

    [Fact]
    public void SocialLensAbsent_ExistingBlockOrderIsPreserved()
    {
        var plan = MakePlan(new Dictionary<string, string>
        {
            [Tier2bBlockIds.Gossip] = "GOSSIP-BLOCK-MARKER",
            [Tier2bBlockIds.Movement] = "MOVEMENT-BLOCK-MARKER",
        });

        string segment = Prompts.AssembleTier2bSegment(plan);

        int gossipIndex = segment.IndexOf("GOSSIP-BLOCK-MARKER", System.StringComparison.Ordinal);
        int movementIndex = segment.IndexOf("MOVEMENT-BLOCK-MARKER", System.StringComparison.Ordinal);
        Assert.True(gossipIndex >= 0, "Gossip block should be emitted.");
        Assert.True(movementIndex >= 0, "Movement block should be emitted.");
        Assert.True(gossipIndex < movementIndex, "Gossip must precede Movement per the existing sequence.");
        Assert.DoesNotContain("SOCIALLENS", segment);
    }
}
