namespace ValleytalkReborn.Scanning;

using System.Collections.Generic;
using Microsoft.Xna.Framework;
using StardewValley;

/// <summary>
/// Stateless, bounded scanner that appends <see cref="ScanItem"/> entries to a buffer.
/// Implementations MUST have no instance fields and MUST bound the window to O((2R+1)^2).
/// </summary>
internal interface IEnvironmentDetector
{
    /// <summary>
    /// Scan a focal region and append items to <paramref name="buffer"/>.
    /// Exceptions are caught by the façade; the implementation only needs to keep the window bounded.
    /// </summary>
    /// <param name="location">Current game location.</param>
    /// <param name="centerTile">Center tile of the scan window.</param>
    /// <param name="radiusTiles">Radius in tiles (window side = 2*radius+1).</param>
    /// <param name="focalNpc">Anchor NPC (may be null); only entity detectors use this.</param>
    /// <param name="buffer">Target list. Never null.</param>
    void Scan(GameLocation location, Vector2 centerTile, int radiusTiles, NPC focalNpc, List<ScanItem> buffer);
}
