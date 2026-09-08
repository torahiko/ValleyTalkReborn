using System;
using System.Collections.Generic;
using System.Linq;

namespace ValleytalkReborn;

/// <summary>
/// Validates A2A script lines before they are enqueued for playback.
/// </summary>
internal static class A2AScriptValidator
{
    /// <summary>
    /// Maximum allowed length for a single dialogue line.
    /// </summary>
    private const int MaxLineLength = 200;

    /// <summary>
    /// Try to validate the parsed A2A lines against the expected participants and count.
    /// </summary>
    /// <param name="lines">Parsed lines from LLM output.</param>
    /// <param name="participants">Expected participant names.</param>
    /// <param name="expectedCount">Expected number of lines.</param>
    /// <param name="validLines">Output array of valid lines.</param>
    /// <returns>True if validation succeeded.</returns>
    internal static bool TryValidate(
        DialogueModels.A2ALine[] lines,
        IReadOnlyList<string> participants,
        int expectedCount,
        out DialogueModels.A2ALine[] validLines)
    {
        validLines = null;

        if (lines == null || lines.Length == 0)
            return false;

        var participantSet = new HashSet<string>(
            participants,
            StringComparer.OrdinalIgnoreCase);

        var filtered = lines
            .Where(line =>
                line != null &&
                !string.IsNullOrWhiteSpace(line.SpeakerName) &&
                !string.IsNullOrWhiteSpace(line.Line) &&
                participantSet.Contains(line.SpeakerName) &&
                line.Line.Length <= MaxLineLength)
            .Take(expectedCount)
            .ToArray();

        // Guard: 至少有 2 句才认为有效（原版无此校验，但防止空脚本）
        if (filtered.Length < 2)
            return false;

        validLines = filtered;
        return true;
    }
}
