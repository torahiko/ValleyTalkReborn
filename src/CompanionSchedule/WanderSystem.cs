using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;

namespace ValleytalkReborn
{
    /// <summary>
    /// 居家与农场游荡决策系统。
    /// 负责配偶在居家状态（IsStayHome）下的微移动、天气感知避雨、晴天农舍外展活动以及表情表现。
    /// </summary>
    internal static class WanderSystem
    {
        public const int WANDER_COOLDOWN_MIN = 1800;
        public const int WANDER_COOLDOWN_MAX = 3600;

        // Farm 游荡锚点半径（格）
        public const int FARM_WANDER_RADIUS = 8;

        /// <summary>
        /// 每 Tick 驱动游荡状态机。由 CSM.OnUpdateTicked 在 state.IsStayHome 为 true 时调用。
        /// </summary>
        public static void TickWander(SpouseScheduleState state)
        {
            var npc = state.TrackedNpc;
            if (npc == null || npc.currentLocation == null) return;
            if (Game1.activeClickableMenu != null || Game1.dialogueUp) return;

            // 正在回家中，不游荡
            if (state.IsReturningHome) return;

            // 正在出门途中，不重复触发
            if (state.IsDepartingToFarm) return;

            var loc = npc.currentLocation;
            bool isFarmHouse = string.Equals(loc.Name, "FarmHouse", StringComparison.OrdinalIgnoreCase);
            bool isFarm      = string.Equals(loc.Name, "Farm",      StringComparison.OrdinalIgnoreCase);

            // ★ 位置守卫：IsStayHome 配偶的游荡范围仅限 FarmHouse / Farm。
            // 在其他地图（例如被第三方模组送往 Town）不启动游荡、不 warp、不调用 FarmBusStopNavigator，
            // 允许第三方日程继续控制该角色。非法地图日志只记录一次，避免每 Tick 刷屏。
            if (!isFarmHouse && !isFarm)
            {
                if (!state.WanderLocationWarningLogged)
                {
                    ModEntry.SMonitor?.Log(
                        $"[WanderSystem] {npc.Name} is not in FarmHouse/Farm (current: '{loc.Name}') — skipping wander.",
                        LogLevel.Debug);
                    state.WanderLocationWarningLogged = true;
                }
                return;
            }

            // 回到合法地图，重置一次性警告标志，确保下次离开时能再提醒一次。
            state.WanderLocationWarningLogged = false;

            // ── 天气守卫（最外层）──
            bool badWeather = Game1.isRaining || Game1.isSnowing || Game1.isLightning;

            // 恶劣天气在 Farm 上：平滑走回室内，不游荡
            if (badWeather && isFarm)
            {
                ModEntry.SMonitor?.Log(
                    $"[WanderSystem] Bad weather — {npc.Name} returning indoors.", LogLevel.Debug);
                state.IsDepartingToFarm = true; // 占位，防止重入
                FarmBusStopNavigator.ReturnHome(npc, wentViaBusStop: false,
                    onArrivedHome: () =>
                    {
                        state.IsDepartingToFarm  = false;
                        state.HasDepartedToFarm  = false;
                        state.WanderCooldownTicks = WANDER_COOLDOWN_MIN;
                    },
                    onFail: () =>
                    {
                        state.IsDepartingToFarm = false;
                    });
                return;
            }

            // 恶劣天气且在 FarmHouse：只允许室内游荡，不出门（直接走下方冷却与游荡逻辑）

            if (state.WanderCooldownTicks > 0)
            {
                state.WanderCooldownTicks--;
                return;
            }

            // 守卫：被约会拦截、正在多图导航、当前正在被跟随、MovementManager 正在驱动此 NPC、或 controller 未完成
            if (IsBlockedByDate(npc)) return;
            if (MultiMapNavigator.Instance.IsNavigating(npc)) return;
            if (MovementManager.Instance.CurrentFollowingNpc == npc) return;
            if (MovementManager.Instance.IsNpcMoving(npc)) return;
            if (MovementManager.Instance.IsFollowing(npc)) return;
            if (npc.controller != null && !MovementPathfinding.IsPathDone(npc.controller)) return;

            if (npc.controller != null)
            {
                npc.controller = null;
                npc.addedSpeed = 0;
                npc.Halt();
            }

            // ── 晴天 + 在 FarmHouse + 尚未出门：触发出门流程 ──
            if (!badWeather && isFarmHouse && !state.HasDepartedToFarm)
            {
                var farm = Game1.getFarm();

                // Farm 地图不可用时保持 NPC 在室内原位，避免把角色丢进虚空。
                if (farm == null)
                {
                    ModEntry.SMonitor?.Log(
                        $"[WanderSystem] {npc.Name} sunny day — Farm map unavailable, cannot depart.",
                        LogLevel.Warn);
                    state.IsDepartingToFarm  = false;
                    state.OnFarmPoiArrived   = null;
                    state.WanderCooldownTicks = WANDER_COOLDOWN_MIN;
                    return;
                }

                var farmEntry = farm.GetMainFarmHouseEntry();
                var destTile  = MovementPathfinding.FindNearestWalkableTile(
                    farm, new Vector2(farmEntry.X, farmEntry.Y + 1), npc, 3);

                // ★ 幂等完成动作：无论 onArrived（已站在目标点）还是 OnFarmPoiArrived
                // （路径走完）谁先触发，只执行一次。清除出门标志、标记已抵达、设置正常冷却。
                bool arrived = false;
                void OnFarmArrived()
                {
                    if (arrived) return;
                    arrived = true;
                    state.IsDepartingToFarm  = false;
                    state.HasDepartedToFarm  = true;
                    state.WanderCooldownTicks = WANDER_COOLDOWN_MIN +
                        Game1.random.Next(WANDER_COOLDOWN_MAX - WANDER_COOLDOWN_MIN);
                    ModEntry.SMonitor?.Log(
                        $"[WanderSystem] {npc.Name} arrived on Farm for outdoor wander.", LogLevel.Info);
                }

                state.IsDepartingToFarm  = true;
                state.OnFarmPoiArrived   = null; // 清理本次可能残留的回调
                state.WanderCooldownTicks = WANDER_COOLDOWN_MIN; // 出门期间屏蔽冷却重置

                ModEntry.SMonitor?.Log(
                    $"[WanderSystem] {npc.Name} sunny day — departing FarmHouse to Farm.", LogLevel.Info);

                FarmBusStopNavigator.DepartFromFarmHouse(npc, "Farm", destTile,
                    onArrived: (_) => OnFarmArrived(),
                    onFail: () =>
                    {
                        state.OnFarmPoiArrived   = null;
                        state.IsDepartingToFarm  = false;
                        state.WanderCooldownTicks = WANDER_COOLDOWN_MIN;
                        ModEntry.SMonitor?.Log(
                            $"[WanderSystem] {npc.Name} depart to Farm failed — staying indoors.", LogLevel.Warn);
                    },
                    onPathStarted: () =>
                    {
                        // 路径已创建但 NPC 尚未抵达，不得在此标记已抵达。
                        // 把完成动作挂到 OnFarmPoiArrived，由 CSM.OnUpdateTicked 检测路径完成后调用。
                        state.OnFarmPoiArrived = OnFarmArrived;
                    });
                return;
            }

            // ── 40% 概率原地小动作 ──
            if (Game1.random.Next(100) < 40)
            {
                npc.faceDirection(Game1.random.Next(4));
                if (Game1.random.Next(100) < 50) npc.doEmote(Game1.random.Next(2) == 0 ? 32 : 8);
                state.WanderCooldownTicks = WANDER_COOLDOWN_MIN / 2 +
                    Game1.random.Next(WANDER_COOLDOWN_MIN / 2);
                return;
            }

            // ── 选取游荡目标点 ──
            var target = PickWanderTargetTile(npc, loc, isFarm);
            if (target == null)
            {
                state.WanderCooldownTicks = WANDER_COOLDOWN_MIN / 3;
                return;
            }

            if (!MovementPathfinding.TryCreatePath(npc, loc, target.Value, out var controller, out _))
            {
                state.WanderCooldownTicks = WANDER_COOLDOWN_MIN / 3;
                return;
            }

            npc.controller = controller;
            npc.addedSpeed = 0;
            state.WanderCooldownTicks = WANDER_COOLDOWN_MIN +
                Game1.random.Next(WANDER_COOLDOWN_MAX - WANDER_COOLDOWN_MIN);
        }

        /// <summary>
        /// 为游荡选取目标点：以当前 NPC 位置为中心，外圈逐层扫描可行走 tile。
        /// </summary>
        public static Vector2? PickWanderTargetTile(NPC npc, GameLocation loc, bool isFarm)
        {
            Point doorTile = Point.Zero;
            Vector2 farmAnchor = Vector2.Zero;

            if (!isFarm)
            {
                var exitWarp = loc.warps?.FirstOrDefault(
                    w => string.Equals(w?.TargetName, "Farm", StringComparison.OrdinalIgnoreCase));
                if (exitWarp != null) doorTile = new Point(exitWarp.X, exitWarp.Y);
            }
            else
            {
                farmAnchor = new Vector2(
                    Game1.getFarm().GetMainFarmHouseEntry().X,
                    Game1.getFarm().GetMainFarmHouseEntry().Y);
            }

            var candidates = new List<Vector2>();

            for (int r = 1; r <= 5; r++)
            {
                for (int x = -r; x <= r; x++)
                {
                    for (int y = -r; y <= r; y++)
                    {
                        if (Math.Abs(x) != r && Math.Abs(y) != r) continue;

                        var tile = new Vector2(npc.Tile.X + x, npc.Tile.Y + y);

                        // 室内：门口禁足区（warp 周围 2 格内不停留）
                        if (!isFarm && doorTile != Point.Zero)
                        {
                            if (Math.Abs(tile.X - doorTile.X) <= 1 &&
                                Math.Abs(tile.Y - doorTile.Y) <= 2)
                                continue;
                        }

                        // 室外：锚定在农舍门口 FARM_WANDER_RADIUS 格以内
                        if (isFarm && farmAnchor != Vector2.Zero)
                        {
                            if (Vector2.Distance(tile, farmAnchor) > FARM_WANDER_RADIUS)
                                continue;
                        }

                        if (MovementPathfinding.IsTileWalkable(loc, tile, npc))
                        {
                            // 排除已被玩家或其他 NPC 占用的 tile，避免游荡把人堆到同一格。
                            bool occupied = loc.characters.Any(c => c != null && c != npc && c.Tile == tile);
                            if (Game1.player.currentLocation == loc && Game1.player.Tile == tile)
                                occupied = true;

                            if (!occupied)
                                candidates.Add(tile);
                        }
                    }
                }

                if (candidates.Count >= 3) break;
            }

            if (candidates.Count == 0) return null;
            return candidates[Game1.random.Next(candidates.Count)];
        }

        private static bool IsBlockedByDate(NPC npc)
            => DateManager.Instance.Phase != DatePhase.None &&
               string.Equals(DateManager.Instance.ActiveDateNpcName, npc.Name,
                   StringComparison.OrdinalIgnoreCase);
    }
}
