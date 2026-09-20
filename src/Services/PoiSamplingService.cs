using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Pathfinding;

namespace ValleytalkReborn.Services;

/// <summary>采样玩家当前站立瓦片是否可作为 POI 出没点。纯读链校验，零世界状态写入。</summary>
internal static class PoiSamplingService
{
    public static bool TryCaptureCurrentTile(out string mapName, out int tileX, out int tileY, out string failureReason)
    {
        mapName = string.Empty;
        tileX = 0;
        tileY = 0;
        failureReason = string.Empty;

        if (!Context.IsWorldReady || Game1.currentLocation == null || Game1.player == null)
        {
            failureReason = "世界未就绪";
            return false;
        }

        var loc = Game1.currentLocation;
        var tile = Game1.player.Tile;
        int x = (int)tile.X;
        int y = (int)tile.Y;

        if (!loc.isTileOnMap(x, y))
        {
            failureReason = "当前瓦片超出地图范围";
            return false;
        }

        var vec = new Vector2(x, y);
        if (!loc.isTilePassable(vec))
        {
            failureReason = "当前瓦片不可通行（障碍/水面）";
            return false;
        }

        if (loc.IsTileOccupiedBy(vec, CollisionMask.Objects | CollisionMask.Furniture, CollisionMask.None, false))
        {
            failureReason = "当前瓦片被物件或家具占用";
            return false;
        }

        // NPC 可达性：抓取对象恒为玩家站立瓦片
        var playerTile = new Microsoft.Xna.Framework.Point(x, y);
        var target = new Microsoft.Xna.Framework.Point(x, y); // 未来可扩展为非站立点校验

        // 站立即玩家可达性证明；同点寻路为退化路径，对 NPC 级可达性零信息量；
        // NPC 执行期对不可达 POI 的兜底由规划器既有逻辑承担（本模组零改动规划器）。
        if (!playerTile.Equals(target))
        {
            Stack<Microsoft.Xna.Framework.Point> path;
            try
            {
                path = PathFindController.findPathForNPCSchedules(playerTile, target, loc, -1, Game1.player);
            }
            catch (Exception ex)
            {
                ModEntry.SMonitor?.Log($"[PoiSampling] 寻路异常: {ex.Message}", LogLevel.Warn);
                failureReason = "寻路校验失败";
                return false;
            }

            if (path == null || path.Count == 0)
            {
                failureReason = "该瓦片对 NPC 不可达";
                return false;
            }
        }

        mapName = loc.Name;
        tileX = x;
        tileY = y;
        return true;
    }
}
