namespace ValleytalkReborn.Scanning.Detectors;

using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;

/// <summary>
/// Detects actionable tiles (TV, bulletin board, sink, etc.) within a bounded window.
/// Stateless; all state is passed in via <see cref="Scan"/>.
/// </summary>
internal sealed class TileActionDetector : IEnvironmentDetector
{
    private static bool IsChineseLanguage =>
        LocalizedContentManager.CurrentLanguageCode == LocalizedContentManager.LanguageCode.zh;

    private static readonly Dictionary<string, (string En, string Zh)> ActionMap =
        new(StringComparer.OrdinalIgnoreCase)
    {
        // ── 1.6 反编译对齐设施 ──
        ["Billboard"]     = ("the town bulletin board", "小镇公告栏与求助任务板"),
        ["SpecialOrders"] = ("the special orders board", "小镇特别任务委托板"),
        ["Kitchen"]       = ("a kitchen stove", "厨房灶台"),
        ["Mailbox"]       = ("the mailbox", "信箱"),
        ["Jukebox"]       = ("the jukebox", "点唱机"),
        ["PrizeMachine"]  = ("the mayor's prize machine", "镇长家的抽奖机"),
        ["Bookseller"]    = ("the bookseller's balloon cart", "书商的书摊热气球"),
        ["DyePot"]        = ("the dye pots", "染料盆"),
        ["Tailoring"]     = ("the sewing machine", "缝纫机工作台"),
        ["Forge"]         = ("the volcano forge", "火山锻造台"),

        // ── 子类 / 数据层安全兜底 ──
        ["TV"]            = ("a television", "电视机"),
        ["Calendar"]      = ("a calendar", "日历挂历"),
        ["Sink"]          = ("a sink", "水槽/洗手池"),
    };

    /// <inheritdoc/>
    public void Scan(GameLocation location, Vector2 centerTile, int radiusTiles, NPC focalNpc, List<ScanItem> buffer)
    {
        if (location == null) return;

        var seenDescriptions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unknownTokens = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        int minX = (int)centerTile.X - radiusTiles;
        int maxX = (int)centerTile.X + radiusTiles;
        int minY = (int)centerTile.Y - radiusTiles;
        int maxY = (int)centerTile.Y + radiusTiles;

        for (int x = minX; x <= maxX; x++)
        {
            for (int y = minY; y <= maxY; y++)
            {
                string action = location.doesTileHaveProperty(x, y, "Action", "Buildings");
                if (string.IsNullOrWhiteSpace(action)) continue;

                string token = action.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
                string desc = DescribeAction(token);

                if (desc == null)
                {
                    if (ModEntry.Config?.Debug == true &&
                        unknownTokens.Add(token))
                    {
                        ModEntry.SMonitor?.Log(
                            $"[TileActionDetector] Unknown action token: '{token}' at ({x},{y}) in {location.Name}",
                            LogLevel.Trace);
                    }
                    continue;
                }

                if (!seenDescriptions.Add(desc)) continue;

                buffer.Add(new ScanItem(desc, new Vector2(x, y), 2, ScanItem.TypeTileAction));
            }
        }
    }

    private static string DescribeAction(string actionToken)
    {
        if (string.IsNullOrWhiteSpace(actionToken)) return null;

        if (ActionMap.TryGetValue(actionToken, out var mapping))
            return IsChineseLanguage ? mapping.Zh : mapping.En;

        return null;
    }
}
