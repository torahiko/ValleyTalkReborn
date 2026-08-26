namespace ValleytalkReborn;

/// <summary>
/// Dialogue system constants shared across modules.
/// </summary>
internal static class DialogueConstants
{
    /// <summary>
    /// Display range in tiles for dialogue bubbles.
    /// </summary>
    internal const int DisplayRangeTiles = 10;

    /// <summary>
    /// Display range squared (for distance comparison).
    /// </summary>
    internal const int DisplayRangeSquared =
        DisplayRangeTiles * DisplayRangeTiles;
}
