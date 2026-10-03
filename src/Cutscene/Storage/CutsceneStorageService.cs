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
        /// 跨图回放待演回执：warpFarmer 已发起，农夫落地录制现场后由
        /// <see cref="OnPlayerWarped"/> 接续摆位开演
        /// </summary>
        private static ArchivedCutscene? _pendingReplay;

        /// <summary>
        /// 录像式回放归档剧本：回放回归录制现场——农夫按归档 PlayerStance 落回录制地图，
        /// 剧组按归档站位在录制现场复现开场队形后开演。
        /// 同图直接就位；跨图先经原版 warpFarmer 换图，落地后于 OnPlayerWarped 接续开演。
        /// 调用方须先关闭激活菜单（Context.IsPlayerFree 含 activeClickableMenu == null 判定）。
        /// 返回 true 语义为"回放已受理"（跨图路径含 warp 在途）。
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

            // 同图快路径：玩家已身处录制现场，就地摆位开演
            var current = Game1.player.currentLocation;
            if (string.Equals(current.NameOrUniqueName, cutscene.LocationName, StringComparison.Ordinal))
            {
                var staged = StageRecordingActors(cutscene, current);
                if (!VirtualDirector.Instance.PlayScript(cutscene.RawJson, out errorMessage))
                {
                    RollbackStagedActors(staged);
                    return false;
                }

                ModEntry.SMonitor?.Log(
                    $"[CutsceneStorage] Replaying cutscene '{cutscene.Title}' in {current.NameOrUniqueName} " +
                    $"(recorded in {cutscene.LocationName}, staged {staged.Count} actors).",
                    LogLevel.Info);
                return true;
            }

            // 跨图路径：解析录制现场，农夫经原版 warpFarmer 换图，落地后接续开演
            var stage = Game1.getLocationFromName(cutscene.LocationName);
            if (stage == null)
            {
                ModEntry.SMonitor?.Log(
                    $"[CutsceneStorage] Recorded stage '{cutscene.LocationName}' not found for replay, aborted.",
                    LogLevel.Warn);
                errorMessage = $"录制的场景 '{cutscene.LocationName}' 不存在";
                return false;
            }

            ComputePlayerLanding(cutscene, stage, out int landingX, out int landingY, out int landingFacing);

            _pendingReplay = cutscene;
            Game1.warpFarmer(stage.NameOrUniqueName, landingX, landingY, landingFacing);
            ModEntry.SMonitor?.Log(
                $"[CutsceneStorage] Replaying cutscene '{cutscene.Title}' — warping to recorded stage " +
                $"'{stage.NameOrUniqueName}' at ({landingX},{landingY}).",
                LogLevel.Info);
            return true;
        }

        /// <summary>
        /// Player.Warped 路由入口（ModEntry 主线程调用）：跨图回放的农夫落地接续点。
        /// 本地农夫到达录制现场 → 摆位开演；被改道去别处或落地即被占用 → 丢弃待演回放并报错。
        /// </summary>
        public static void OnPlayerWarped(StardewModdingAPI.Events.WarpedEventArgs e)
        {
            try
            {
                var pending = _pendingReplay;
                if (pending == null)
                    return;
                // 待演回放只由本地农夫的换图消费，联机远端农夫切图与此无关
                if (!e.IsLocalPlayer)
                    return;
                _pendingReplay = null;

                // 到达地图与录制现场不符：warp 被原版事件/剧情改道，丢弃本次回放
                if (e.NewLocation == null ||
                    !string.Equals(e.NewLocation.NameOrUniqueName, pending.LocationName, StringComparison.Ordinal))
                {
                    ModEntry.SMonitor?.Log(
                        $"[CutsceneStorage] Pending replay of '{pending.Title}' aborted: player arrived at " +
                        $"'{e.NewLocation?.NameOrUniqueName ?? "<null>"}' instead of recorded stage '{pending.LocationName}'.",
                        LogLevel.Warn);
                    Game1.addHUDMessage(new HUDMessage("🎬 回放换图被改道，无法抵达录制现场", HUDMessage.error_type));
                    return;
                }

                if (!Context.IsPlayerFree)
                {
                    ModEntry.SMonitor?.Log(
                        $"[CutsceneStorage] Pending replay of '{pending.Title}' aborted: player not free after warp.",
                        LogLevel.Warn);
                    Game1.addHUDMessage(new HUDMessage("🎬 回放落地时被事件占用，无法开演", HUDMessage.error_type));
                    return;
                }

                if (VirtualDirector.Instance.IsActive)
                {
                    ModEntry.SMonitor?.Log(
                        $"[CutsceneStorage] Pending replay of '{pending.Title}' aborted: director already active after warp.",
                        LogLevel.Warn);
                    return;
                }

                var staged = StageRecordingActors(pending, e.NewLocation);
                if (!VirtualDirector.Instance.PlayScript(pending.RawJson, out var playError))
                {
                    RollbackStagedActors(staged);
                    ModEntry.SMonitor?.Log(
                        $"[CutsceneStorage] Pending replay of '{pending.Title}' failed to start: {playError}",
                        LogLevel.Warn);
                    Game1.addHUDMessage(new HUDMessage($"🎬 回放开演失败: {playError}", HUDMessage.error_type));
                    return;
                }

                ModEntry.SMonitor?.Log(
                    $"[CutsceneStorage] Replaying cutscene '{pending.Title}' in {e.NewLocation.NameOrUniqueName} " +
                    $"(recorded in {pending.LocationName}, staged {staged.Count} actors).",
                    LogLevel.Info);
            }
            catch (Exception ex)
            {
                _pendingReplay = null;
                ModEntry.SMonitor?.Log($"[CutsceneStorage] Pending replay continuation failed: {ex}", LogLevel.Error);
            }
        }

        /// <summary>
        /// 计算农夫回放落地格：新归档取 PlayerStance（录制时农夫真实站位，可行性由录制事实保证）；
        /// 旧归档取首位演员站位下方一格，经最近可走格归一（RECOVERABLE 兜底）。
        /// </summary>
        private static void ComputePlayerLanding(
            ArchivedCutscene cutscene, GameLocation stage, out int landingX, out int landingY, out int landingFacing)
        {
            var stance = cutscene.PlayerStance;
            if (stance != null && (stance.TileX != 0 || stance.TileY != 0))
            {
                landingX = stance.TileX;
                landingY = stance.TileY;
                landingFacing = Math.Clamp(stance.Facing, 0, 3);
                return;
            }

            var firstStance = cutscene.ActorStances?.FirstOrDefault(s => s != null && !string.IsNullOrWhiteSpace(s.Name));
            var fallbackTile = firstStance != null
                ? new Vector2(firstStance.TileX, firstStance.TileY + 1)
                : Game1.player.Tile;
            // 可走性探针须为活体 Character：vanilla isCollidingPosition 对 null character 一律判碰撞；
            // pathfinding 语义下角色互撞豁免，探针仅影响地形/家具判定，故借首位演员作 probe
            var probe = firstStance != null ? Game1.getCharacterFromName(firstStance.Name) : null;
            var landing = MovementPathfinding.FindNearestWalkableTile(stage, fallbackTile, probe, radius: 5);
            landingX = (int)landing.X;
            landingY = (int)landing.Y;
            landingFacing = 2;
        }

        /// <summary>
        /// 依据归档站位把录制演员带入录制现场并落位（调用点保证已身处录制地图：
        /// 同图就地就位，跨图经农夫 warp 落地后到达）。
        /// 旧归档（无站位记录）依据演员名单在农夫四周合成环形替补站位。
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

                // 恒按录制坐标落位（回放已回归录制现场），红线：强制换算到可通行格子，杜绝卡墙
                var candidateTile = new Vector2(stance.TileX, stance.TileY);
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
