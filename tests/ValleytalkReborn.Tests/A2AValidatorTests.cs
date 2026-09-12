using System.Collections.Generic;
using Xunit;

namespace ValleytalkReborn.Tests;

/// <summary>
/// Tests for A2AScriptValidator.
/// </summary>
public class A2AValidatorTests
{
    [Fact]
    public void TryValidate_ValidLines_ShouldSucceed()
    {
        var lines = new[]
        {
            new DialogueModels.A2ALine { SpeakerName = "Abigail", Line = "Hello!" },
            new DialogueModels.A2ALine { SpeakerName = "Sebastian", Line = "Hi there!" },
        };

        var participants = new List<string> { "Abigail", "Sebastian" };

        bool result = A2AScriptValidator.TryValidate(
            lines, participants, 2, out var validLines);

        Assert.True(result);
        Assert.Equal(2, validLines.Length);
    }

    [Fact]
    public void TryValidate_UnknownSpeaker_ShouldFail()
    {
        var lines = new[]
        {
            new DialogueModels.A2ALine { SpeakerName = "Unknown", Line = "Hello!" },
            new DialogueModels.A2ALine { SpeakerName = "Sebastian", Line = "Hi there!" },
        };

        var participants = new List<string> { "Abigail", "Sebastian" };

        bool result = A2AScriptValidator.TryValidate(
            lines, participants, 2, out var validLines);

        Assert.False(result);
        Assert.Null(validLines);
    }

    [Fact]
    public void TryValidate_TooFewLines_ShouldFail()
    {
        var lines = new[]
        {
            new DialogueModels.A2ALine { SpeakerName = "Abigail", Line = "Hello!" },
        };

        var participants = new List<string> { "Abigail", "Sebastian" };

        bool result = A2AScriptValidator.TryValidate(
            lines, participants, 2, out var validLines);

        Assert.False(result);
        Assert.Null(validLines);
    }

    [Fact]
    public void TryValidate_LinesWithMarkdown_ShouldFilter()
    {
        var lines = new[]
        {
            new DialogueModels.A2ALine { SpeakerName = "Abigail", Line = "**bold** text" },
            new DialogueModels.A2ALine { SpeakerName = "Sebastian", Line = "Hi there!" },
        };

        var participants = new List<string> { "Abigail", "Sebastian" };

        bool result = A2AScriptValidator.TryValidate(
            lines, participants, 2, out var validLines);

        Assert.False(result);
        Assert.Null(validLines);
    }
}
