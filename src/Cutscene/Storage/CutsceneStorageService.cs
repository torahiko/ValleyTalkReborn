#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using ValleytalkReborn.Cutscene.Generation;
using ValleytalkReborn.Services;

namespace ValleytalkReborn.Cutscene.Storage
{
    /// <summary>
    /// 剧本归档与持久化回放服务：
    /// 负责即兴剧本在本地的安全原子落盘、历史索引读取以及就地重播
    /// </summary>
    public static class CutsceneStorageService
    {
        /// <summary>
        /// 获取剧本存储目录（优先使用当前存档的本地目录，未载档时回退至全局目录）
        /// </summary>
        public static string GetStorageDirectory()
        {
            string baseDir = StorageLayout.LocalBaseDir ?? StorageLayout.GlobalBaseDir;
            return Path.Combine(baseDir, "cutscenes");
        }

        /// <summary>
        /// 归档一个新生成的剧本
        /// </summary>
        public static bool Save(ArchivedCutscene cutscene)
        {
            if (cutscene == null || string.IsNullOrWhiteSpace(cutscene.RawJson))
                return false;

            try
            {
                string dir = GetStorageDirectory();
                string filePath = Path.Combine(dir, $"{cutscene.Id}.json");
                StorageJson.Write(filePath, cutscene);
                ModEntry.SMonitor?.Log($"[CutsceneStorage] Cutscene archived: '{cutscene.Title}' ({cutscene.Id})", LogLevel.Info);
                return true;
            }
            catch (Exception ex)
            {
                ModEntry.SMonitor?.Log($"[CutsceneStorage] Failed to save cutscene: {ex.Message}", LogLevel.Warn);
                return false;
            }
        }

        /// <summary>
        /// 加载所有归档剧本（按创建时间倒序排列）
        /// </summary>
        public static List<ArchivedCutscene> LoadAll()
        {
            var list = new List<ArchivedCutscene>();
            try
            {
                string dir = GetStorageDirectory();
                if (!Directory.Exists(dir))
                    return list;

                var files = Directory.GetFiles(dir, "*.json");
                foreach (var file in files)
                {
                    try
                    {
                        var item = StorageJson.Read<ArchivedCutscene>(file);
                        if (item != null && !string.IsNullOrWhiteSpace(item.RawJson))
                        {
                            list.Add(item);
                        }
                    }
                    catch (Exception ex)
                    {
                        ModEntry.SMonitor?.Log($"[CutsceneStorage] Error reading cutscene file {Path.GetFileName(file)}: {ex.Message}", LogLevel.Debug);
                    }
                }

                list.Sort((a, b) => b.CreatedAt.CompareTo(a.CreatedAt));
            }
            catch (Exception ex)
            {
                ModEntry.SMonitor?.Log($"[CutsceneStorage] Failed to load cutscenes: {ex.Message}", LogLevel.Warn);
            }
            return list;
        }

        /// <summary>
        /// 删除指定 ID 的归档剧本
        /// </summary>
        public static bool Delete(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return false;

            try
            {
                string dir = GetStorageDirectory();
                string filePath = Path.Combine(dir, $"{id}.json");
                if (File.Exists(filePath))
                {
                    File.Delete(filePath);
                    ModEntry.SMonitor?.Log($"[CutsceneStorage] Deleted cutscene {id}", LogLevel.Info);
                    return true;
                }
            }
            catch (Exception ex)
            {
                ModEntry.SMonitor?.Log($"[CutsceneStorage] Error deleting cutscene {id}: {ex.Message}", LogLevel.Warn);
            }
            return false;
        }

        /// <summary>
        /// 录像式回放归档剧本：与录制现场解耦，无论玩家当前身处何处，
        /// 都依据归档站位把剧组带入当前场景复现开场队形后开演（同图按录制坐标落位，跨图以玩家为锚点复现相对队形）。
        /// 调用方须先关闭激活菜单（Context.IsPlayerFree 含 activeClickableMenu == null 判定）。
        /// </summary>
        public static bool Replay(ArchivedCutscene cutscene, out string errorMessage)
        {
            errorMessage = string.Empty;
            if (cutscene == null || string.IsNullOrWhiteSpace(cutscene.RawJson))
            {
                errorMessage = "剧本数据为空";
                return false;
            }

            if (!Context.IsWorldReady || Game1.player?.currentLocation == null)
            {
                errorMessage = "游戏世界未就绪";
                return false;
            }

            if (VirtualDirector.Instance.IsActive)
            {
                errorMessage = "当前已有过场正在演出中";
                return false;
            }

            if (CutsceneGeneratorService.IsGenerating)
            {
                errorMessage = "剧本构思尚未结束，无法回放";
                return false;
            }

            // BOUNDARY: 仅拦截原版事件/对话态（快照-复原语义会被外部状态破坏）；
            // 菜单态由调用方先关闭菜单，故不在此处判定
            if (!Context.IsPlayerFree)
            {
                errorMessage = "玩家当前正处于事件中，无法开演";
                return false;
            }

            var location = Game1.player.currentLocation;
            var staged = StageRecordingActors(cutscene, location);

            if (!VirtualDirector.Instance.PlayScript(cutscene.RawJson, out errorMessage))
            {
                RollbackStagedActors(staged);
                return false;
            }

            ModEntry.SMonitor?.Log(
                $"[CutsceneStorage] Replaying cutscene '{cutscene.Title}' in {location.NameOrUniqueName} " +
                $"(recorded in {cutscene.LocationName}, staged {staged.Count} actors).",
                LogLevel.Info);
            return true;
        }

        /// <summary>
        /// 依据归档站位把录制演员带入目标场景并落位。
        /// 同图按录制坐标复现；跨图以玩家身旁为队形锚点、按录制相对间距展开。
        /// 旧归档（无站位记录）依据演员名单在玩家四周合成环形替补站位。
        /// </summary>
        private static List<(NPC Npc, GameLocation Location, Vector2 Tile, int Facing)> StageRecordingActors(
            ArchivedCutscene cutscene, GameLocation targetLocation)
        {
            var staged = new List<(NPC Npc, GameLocation Location, Vector2 Tile, int Facing)>();
            if (targetLocation == null || Game1.player == null)
                return staged;

            // 站位来源：优先归档站位（按名字去重，首个为准）；旧归档以演员名单合成环形替补站位
            var stances = cutscene.ActorStances?
                .Where(s => s != null && !string.IsNullOrWhiteSpace(s.Name))
                .GroupBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList() ?? new List<ArchivedActorStance>();

            if (stances.Count == 0 && cutscene.ActorNames != null)
            {
                Vector2 playerTile = Game1.player.Tile;
                Vector2[] ringOffsets = { new Vector2(2, 0), new Vector2(-2, 0), new Vector2(0, 2), new Vector2(-1, -2) };
                for (int i = 0; i < cutscene.ActorNames.Count; i++)
                {
                    stances.Add(new ArchivedActorStance
                    {
                        Name = cutscene.ActorNames[i],
                        TileX = (int)playerTile.X + (int)ringOffsets[i % ringOffsets.Length].X,
                        TileY = (int)playerTile.Y + (int)ringOffsets[i % ringOffsets.Length].Y,
                        Facing = 2
                    });
                }
            }

            if (stances.Count == 0)
                return staged;

            bool sameMap = string.Equals(cutscene.LocationName, targetLocation.NameOrUniqueName, StringComparison.OrdinalIgnoreCase);
            Vector2 anchorFinalTile = Vector2.Zero;
            Vector2 anchorRecordedTile = Vector2.Zero;
            bool hasAnchor = false;

            foreach (var stance in stances)
            {
                NPC npc = Game1.getCharacterFromName(stance.Name);
                if (npc == null)
                {
                    ModEntry.SMonitor?.Log(
                        $"[CutsceneStorage] Recorded actor '{stance.Name}' not found in world, skipped.",
                        LogLevel.Warn);
                    continue;
                }

                var recordedTile = new Vector2(stance.TileX, stance.TileY);
                Vector2 candidateTile;
                if (sameMap)
                {
                    candidateTile = recordedTile;
                }
                else if (!hasAnchor)
                {
                    // 跨图回放：首位演员落在玩家身旁，作为队形锚点
                    candidateTile = Game1.player.Tile + new Vector2(2, 0);
                }
                else
                {
                    // 其余演员按录制时的相对间距复现队形
                    candidateTile = anchorFinalTile + (recordedTile - anchorRecordedTile);
                }

                // 红线：强制换算到可通行格子，杜绝卡墙
                Vector2 safeTile = MovementPathfinding.FindNearestWalkableTile(targetLocation, candidateTile, npc, radius: 5);

                // 记录摆位前的真实状态，供开演失败时回滚
                staged.Add((npc, npc.currentLocation, npc.Tile, npc.FacingDirection));

                if (npc.currentLocation != targetLocation)
                {
                    Game1.warpCharacter(npc, targetLocation.NameOrUniqueName, safeTile);
                }
                else
                {
                    npc.setTilePosition(new Point((int)safeTile.X, (int)safeTile.Y));
                }
                npc.faceDirection(Math.Clamp(stance.Facing, 0, 3));

                if (!hasAnchor)
                {
                    anchorFinalTile = safeTile;
                    anchorRecordedTile = recordedTile;
                    hasAnchor = true;
                }
            }

            return staged;
        }

        /// <summary>
        /// 回滚已摆位的演员：把每个演员送回摆位前的场景与坐标
        /// </summary>
        private static void RollbackStagedActors(List<(NPC Npc, GameLocation Location, Vector2 Tile, int Facing)> staged)
        {
            foreach (var entry in staged)
            {
                try
                {
                    if (entry.Npc == null) continue;

                    if (entry.Location != null && entry.Npc.currentLocation != entry.Location)
                    {
                        Game1.warpCharacter(entry.Npc, entry.Location.NameOrUniqueName, entry.Tile);
                    }
                    else
                    {
                        entry.Npc.setTilePosition(new Point((int)entry.Tile.X, (int)entry.Tile.Y));
                    }
                    entry.Npc.faceDirection(Math.Clamp(entry.Facing, 0, 3));
                }
                catch (Exception ex)
                {
                    ModEntry.SMonitor?.Log(
                        $"[CutsceneStorage] Failed to rollback staged actor '{entry.Npc?.Name}': {ex.Message}",
                        LogLevel.Warn);
                }
            }
        }
    }
}
