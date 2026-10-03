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
        /// 跨图回放的原点锚：发起 warp 前以玩家真实状态捕获，开演时覆写快照玩家字段，
        /// 谢幕后经快照 Restore 的跨图分支归还原点（Memory 瞬态，与 _pendingReplay 同步消费）
        /// </summary>
        private static VirtualDirector.PlayerAnchor? _pendingOriginReturn;

        /// <summary>
        /// 录像式回放归档剧本：回放回归录制现场——农夫按归档 PlayerStance 落回录制地图，
        /// 克隆演员按归档站位在录制现场复现开场队形后开演（真人本体零接触）。
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

            // 同图快路径：玩家已身处录制现场，克隆演员就地摆位开演（无原点锚，快照保持真实现场）
            var current = Game1.player.currentLocation;
            if (string.Equals(current.NameOrUniqueName, cutscene.LocationName, StringComparison.Ordinal))
            {
                return StartReplayOnStage(cutscene, current, null, out errorMessage);
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

            // 原点锚：发起 warp 前以玩家真实状态捕获，供谢幕归还原点；
            // 同图快速路径不设置锚（保持 null，快照沿用 Capture 的真实现场）
            _pendingOriginReturn = new VirtualDirector.PlayerAnchor(
                current.NameOrUniqueName, Game1.player.Tile, Game1.player.FacingDirection);
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
                var originAnchor = _pendingOriginReturn;
                _pendingOriginReturn = null;

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

                // BOUNDARY: 换图落地的淡入窗口是本模组主动发起 warp 的预期瞬态，
                // 不构成阻断（Context.IsPlayerFree 的 fading 判定会在此确定性误杀）；
                // 仅原版事件/剧情/菜单才是真阻断
                if (VirtualDirector.IsPlayerBlockedByVanillaState())
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

                if (!StartReplayOnStage(pending, e.NewLocation, originAnchor, out var playError))
                {
                    ModEntry.SMonitor?.Log(
                        $"[CutsceneStorage] Pending replay of '{pending.Title}' failed to start: {playError}",
                        LogLevel.Warn);
                    Game1.addHUDMessage(new HUDMessage($"🎬 回放开演失败: {playError}", HUDMessage.error_type));
                    return;
                }
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
        /// 在录制现场以克隆演员开演回放：克隆摆位 → 编译（演员覆盖表阻断真人解析）→ 播放。
        /// originAnchor 非空时为跨图回放：开演时以原点锚覆写快照玩家字段，谢幕后归还原点。
        /// 开演失败时幕后摘除全部克隆；开演成功后的摘除由 SceneEnded → DisposeAll 承担。
        /// </summary>
        private static bool StartReplayOnStage(ArchivedCutscene cutscene, GameLocation stage,
            VirtualDirector.PlayerAnchor? originAnchor, out string errorMessage)
        {
            errorMessage = string.Empty;

            var clones = CutsceneCloneService.StageClones(cutscene, stage);
            if (clones.Count == 0)
            {
                errorMessage = "无法召集任何录制演员";
                ModEntry.SMonitor?.Log(
                    $"[CutsceneStorage] Replay of '{cutscene.Title}' aborted: no clone actors could be staged.",
                    LogLevel.Warn);
                return false;
            }

            // 演员覆盖表：以克隆内部名与显示名双键映射，确保 FindNpc 永不解析到同图真人
            var actorOverrides = new Dictionary<string, NPC>(StringComparer.OrdinalIgnoreCase);
            foreach (var clone in clones)
            {
                actorOverrides[clone.Name] = clone;
                if (!string.IsNullOrEmpty(clone.displayName))
                {
                    actorOverrides[clone.displayName] = clone;
                }
            }

            if (!VirtualDirector.Instance.PlayScript(cutscene.RawJson, stage, actorOverrides, originAnchor, out errorMessage))
            {
                CutsceneCloneService.DisposeAll();
                return false;
            }

            ModEntry.SMonitor?.Log(
                $"[CutsceneStorage] Replaying cutscene '{cutscene.Title}' in {stage.NameOrUniqueName} " +
                $"(recorded in {cutscene.LocationName}, staged {clones.Count} clone actors).",
                LogLevel.Info);
            return true;
        }
    }
}
