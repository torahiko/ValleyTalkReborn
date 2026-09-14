namespace ValleytalkReborn.Scanning;

using Microsoft.Xna.Framework;

/// <summary>
/// Single scanned focal element produced by an <see cref="IEnvironmentDetector"/>.
/// Never null-description; implementations must skip empty/blank entries.
/// </summary>
internal readonly struct ScanItem
{
    public const string TypeNpc = "NPC";
    public const string TypeTerrain = "Terrain";
    public const string TypeObject = "Object";
    public const string TypeFurniture = "Furniture";
    public const string TypeTileAction = "TileAction";
    public const string TypeLandmark = "Landmark";
    public const string TypeAnimal = "Animal";

    /// <summary>Human-readable description. Never null.</summary>
    public readonly string Description;

    /// <summary>Tile coordinates (integer space).</summary>
    public readonly Vector2 Tile;

    /// <summary>1 = Tier1 (immediate), 2 = Tier2, 3 = Tier3.</summary>
    public readonly int Priority;

    /// <summary>One of the Type* constants.</summary>
    public readonly string Type;

    public ScanItem(string description, Vector2 tile, int priority, string type)
    {
        Description = description;
        Tile = tile;
        Priority = priority;
        Type = type;
    }
}
