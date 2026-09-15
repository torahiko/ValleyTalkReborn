namespace ValleytalkReborn.Scanning.Detectors;

using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;

/// <summary>
/// Detects static landmarks (fountain, playground, dock, etc.) within a bounded window.
/// Stateless; all state is passed in via <see cref="Scan"/>.
/// </summary>
internal sealed class StaticLandmarkDetector : IEnvironmentDetector
{
    private static bool IsChineseLanguage =>
        LocalizedContentManager.CurrentLanguageCode == LocalizedContentManager.LanguageCode.zh;

    private readonly struct LandmarkEntry
    {
        public readonly string En;
        public readonly string Zh;
        public readonly Rectangle Area;

        public LandmarkEntry(string en, string zh, Rectangle area)
        {
            En = en;
            Zh = zh;
            Area = area;
        }
    }

    private static readonly Dictionary<string, List<LandmarkEntry>> Registry =
        new(StringComparer.OrdinalIgnoreCase)
    {
        ["Town"] = new()
        {
            new("the town fountain", "小镇中央喷泉", new Rectangle(40, 53, 4, 3)),
            new("the children's playground", "儿童游乐场", new Rectangle(21, 68, 6, 5)),
            new("the town graveyard", "小镇墓园", new Rectangle(53, 75, 6, 7)),
            new("the clinic benches", "诊所前长椅", new Rectangle(40, 56, 3, 2)),
        },
        ["Beach"] = new()
        {
            new("Willy's fishing dock", "威利鱼店木码头", new Rectangle(48, 30, 8, 12)),
            new("the tidal pool bridge", "潮汐池木桥", new Rectangle(58, 13, 3, 3)),
        },
        ["Mountain"] = new()
        {
            new("the mine entrance platform", "矿洞前木栈桥", new Rectangle(54, 7, 5, 3)),
            new("the lake island", "湖心岛", new Rectangle(36, 38, 7, 6)),
        },
        ["Saloon"] = new()
        {
            new("the arcade corner", "街机角落", new Rectangle(18, 16, 4, 3)),
            new("the piano and instruments corner", "钢琴与乐器角", new Rectangle(14, 18, 4, 3)),
        },
        ["JoshHouse"] = new()
        {
            new("a television", "电视机", new Rectangle(12, 13, 2, 2)),
            new("the warm fireplace", "壁炉", new Rectangle(3, 14, 2, 2)),
        },
    };

    /// <inheritdoc/>
    public void Scan(GameLocation location, Vector2 centerTile, int radiusTiles, NPC focalNpc, List<ScanItem> buffer)
    {
        if (location == null) return;

        string locationName = location.Name ?? string.Empty;
        if (!Registry.TryGetValue(locationName, out var landmarks)) return;

        var seenDescriptions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        int px = (int)centerTile.X;
        int py = (int)centerTile.Y;

        foreach (var entry in landmarks)
        {
            Rectangle rect = entry.Area;

            int dx = rect.X > px ? rect.X - px : (px > rect.Right - 1 ? px - (rect.Right - 1) : 0);
            int dy = rect.Y > py ? rect.Y - py : (py > rect.Bottom - 1 ? py - (rect.Bottom - 1) : 0);
            int dist = Math.Max(dx, dy);

            if (dist > radiusTiles) continue;

            // R6: nearest boundary projection
            int bx = Math.Clamp(px, rect.Left, rect.Right - 1);
            int by = Math.Clamp(py, rect.Top, rect.Bottom - 1);

            if (bx == px && by == py)
            {
                bool closerToTop = (py - rect.Top) <= (rect.Bottom - 1 - py);
                by = closerToTop ? rect.Top : rect.Bottom - 1;
            }

            string desc = IsChineseLanguage ? entry.Zh : entry.En;

            if (!seenDescriptions.Add(desc)) continue;

            buffer.Add(new ScanItem(desc, new Vector2(bx, by), 3, ScanItem.TypeLandmark));

            if (ModEntry.Config?.Debug == true)
            {
                ModEntry.SMonitor?.Log(
                    $"[StaticLandmarkDetector] {location.Name} / {desc} / dist={dist}",
                    LogLevel.Trace);
            }
        }
    }
}
