using System;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;

namespace ValleytalkReborn
{
    /// <summary>
    /// 配偶前往 POI 的离场路由与动作表现系统。
    /// 负责农场内寻路、三图体系对接、跨地图就近出口扫描、安全抵达标记以及就位动画播放。
    /// </summary>
    internal static class SpouseDepartureRouter
    {

        /// <summary>
        /// 当前客户端是否为中文环境。写入时定语言——语言切换必经回标题→读档，
        /// 而 ResetAllStates 在 SaveLoaded 会清空全部状态，不存在跨语言残留。
        /// </summary>
        private static bool IsZhClient =>
            LocalizedContentManager.CurrentLanguageCode == LocalizedContentManager.LanguageCode.zh;

        /// <summary>
        /// 获取本地化的 POI 描述。zh 客户端优先使用 DescriptionForLLM_Zh，为空时回退英文原文。
        /// </summary>
        private static string GetLocalizedPoiDescription(PoiAsset asset)
        {
            if (asset == null) return "";
            if (IsZhClient && !string.IsNullOrWhiteSpace(asset.DescriptionForLLM_Zh))
                return asset.DescriptionForLLM_Zh;
            return asset.DescriptionForLLM ?? "";
        }
        /// <summary>
        /// 执行指定的 POI 日程条目。
        /// </summary>
        public static void ExecutePoiEntry(
            NPC npc,
            ScheduledPoiEntry entry,
            SpouseScheduleState state,
            Action<SpouseScheduleState, ScheduleContextPhase, string> transitionContext)
        {
            if (npc == null || entry?.Asset == null) return;

            var asset = entry.Asset;
            var target = new Vector2(asset.TargetTile?.X ?? 0, asset.TargetTile?.Y ?? 0);

            ModEntry.SMonitor?.Log(
                $"[DepartureRouter] {npc.Name} → '{entry.PoiId}' (map={asset.MapName}, tile={target.X},{target.Y}) @ {entry.DepartureTime}",
                LogLevel.Info);

            // 切换为在途上下文
            transitionContext(state, ScheduleContextPhase.TravelingToPoi, entry.PoiId);

            bool targetIsOnFarm =
                string.Equals(asset.MapName, "Farm", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(asset.MapName, "FarmHouse", StringComparison.OrdinalIgnoreCase);

            if (targetIsOnFarm)
            {
                if (!string.Equals(npc.currentLocation?.Name, asset.MapName, StringComparison.OrdinalIgnoreCase))
                {
                    if (string.Equals(asset.MapName, "Farm", StringComparison.OrdinalIgnoreCase))
                    {
                        var farmEntry = Game1.getFarm().GetMainFarmHouseEntry();
                        Game1.warpCharacter(npc, "Farm", new Point(farmEntry.X, farmEntry.Y + 1));
                    }
                    else
                    {
                        var (homeMap, homeTile) = SpouseQueryService.Instance.GetHomeDestination(npc);
                        Game1.warpCharacter(npc, homeMap, new Point((int)homeTile.X, (int)homeTile.Y));
                    }
                }

                var loc = npc.currentLocation;
                if (loc == null) return;

                // 验证 POI 目标点是否可走，并寻找附近安全点
                var requestedTarget = new Vector2(asset.TargetTile?.X ?? 0, asset.TargetTile?.Y ?? 0);
                target = MovementPathfinding.FindNearestWalkableTile(loc, requestedTarget, npc, 3);

                state.WentViaBusStop = false; // 目的地在农场同侧，出发时没有经过巴士站

                if (Vector2.Distance(npc.Tile, target) < 1f)
                {
                    // 已经站在目标点上，视为立即抵达。
                    MarkEntryArrived(entry, Game1.timeOfDay);
                    state.PreviousPoiId = entry.PoiId;
                    transitionContext(state, ScheduleContextPhase.ActiveAtPoi, GetLocalizedPoiDescription(asset));
                    TryPlayAnimation(npc, asset.CsharpAnimation);
                    return;
                }

                if (MovementPathfinding.TryCreatePath(npc, loc, target, out var controller, out _))
                {
                    npc.controller = controller;
                    npc.addedSpeed = 2;
                    StartFarmPoiWatch(npc, state, entry, transitionContext);
                }
                else
                {
                    ModEntry.SMonitor?.Log(
                        $"[DepartureRouter] {npc.Name} cannot path to '{entry.PoiId}' on farm — standing in place.",
                        LogLevel.Info);
                    // 寻路失败也视为"抵达"（原地站着），否则会永远卡在"未到达"状态导致回家判断失灵。
                    MarkEntryArrived(entry, Game1.timeOfDay);
                    state.PreviousPoiId = entry.PoiId;
                    transitionContext(state, ScheduleContextPhase.ActiveAtPoi, GetLocalizedPoiDescription(asset));
                    TryPlayAnimation(npc, asset.CsharpAnimation);
                }
                return;
            }

            // 非农场目的地：交由 FarmBusStopNavigator 协调
            string currentMap = npc.currentLocation?.Name ?? "";
            bool startedFromFarmHouse = string.Equals(currentMap, "FarmHouse", StringComparison.OrdinalIgnoreCase);
            bool startedFromFarm = string.Equals(currentMap, "Farm", StringComparison.OrdinalIgnoreCase);

            void OnDepartArrived(bool wentViaBusStop)
            {
                state.WentViaBusStop = wentViaBusStop;
                MarkEntryArrived(entry, Game1.timeOfDay);
                state.PreviousPoiId = entry.PoiId;
                transitionContext(state, ScheduleContextPhase.ActiveAtPoi, GetLocalizedPoiDescription(asset));
                TryPlayAnimation(npc, asset.CsharpAnimation);
                ModEntry.SMonitor?.Log(
                    $"[DepartureRouter] {npc.Name} arrived at '{entry.PoiId}' (viaBusStop={wentViaBusStop}).",
                    LogLevel.Info);
            }

            void OnDepartFail()
            {
                ModEntry.SMonitor?.Log(
                    $"[DepartureRouter] {npc.Name} failed to reach '{entry.PoiId}' — marking arrived in place to avoid getting stuck.",
                    LogLevel.Warn);
                MarkEntryArrived(entry, Game1.timeOfDay);
                state.PreviousPoiId = entry.PoiId;
                transitionContext(state, ScheduleContextPhase.ActiveAtPoi, GetLocalizedPoiDescription(asset));
            }

            // 只有当目的地恰好是"农场本身的某个坐标"时才会走到这个分支：此时 NPC 是刚发起寻路，
            // 还没真正到达，必须挂一个到达监听——复用 StartFarmPoiWatch 的 IsPathDone 轮询模式，
            // 不能提前把 EndTime 定死，否则又会退化成"发起寻路即视为抵达"的假到达问题。
            void OnPathStarted()
            {
                state.WentViaBusStop = false;
                StartFarmPoiWatch(npc, state, entry, transitionContext);
            }

            if (startedFromFarmHouse)
            {
                FarmBusStopNavigator.DepartFromFarmHouse(npc, asset.MapName, target, OnDepartArrived, OnDepartFail, OnPathStarted);
            }
            else if (startedFromFarm)
            {
                FarmBusStopNavigator.ContinueDepartFromFarm(npc, asset.MapName, target, OnDepartArrived, OnDepartFail, OnPathStarted);
            }
            else
            {
                // 非农舍/非农场出发：统一走就地寻路或就近退场，拒绝返回农场二次折返
                DepartFromOtherLocation(npc, state, entry, transitionContext);
            }
        }

        public static void StartFarmPoiWatch(
            NPC npc,
            SpouseScheduleState state,
            ScheduledPoiEntry entry,
            Action<SpouseScheduleState, ScheduleContextPhase, string> transitionContext)
        {
            var asset = entry.Asset;
            state.OnFarmPoiArrived = () =>
            {
                MarkEntryArrived(entry, Game1.timeOfDay);
                state.PreviousPoiId = entry.PoiId;
                transitionContext(state, ScheduleContextPhase.ActiveAtPoi, GetLocalizedPoiDescription(asset));
                ModEntry.SMonitor?.Log($"[DepartureRouter] {npc.Name} arrived at farm POI '{entry.PoiId}'.", LogLevel.Info);
                TryPlayAnimation(npc, asset.CsharpAnimation);
            };
        }

        public static void MarkEntryArrived(ScheduledPoiEntry entry, int arrivalTime)
        {
            if (entry == null) return;
            int stay = entry.StayMinutes > 0 ? entry.StayMinutes : (entry.Asset?.StayMinutes ?? 90);
            entry.EndTime = Math.Min(MovementPathfinding.SafeAddGameTime(arrivalTime, stay), 1990);
        }

        public static void DepartFromOtherLocation(
            NPC npc,
            SpouseScheduleState state,
            ScheduledPoiEntry entry,
            Action<SpouseScheduleState, ScheduleContextPhase, string> transitionContext)
        {
            var asset = entry.Asset;
            var target = new Vector2(asset.TargetTile?.X ?? 0, asset.TargetTile?.Y ?? 0);
            var loc = npc.currentLocation;

            state.WentViaBusStop = false;

            if (loc == null)
            {
                MultiMapNavigator.WarpDirectTo(npc, asset.MapName, target);
                MarkEntryArrived(entry, Game1.timeOfDay);
                state.PreviousPoiId = entry.PoiId;
                transitionContext(state, ScheduleContextPhase.ActiveAtPoi, GetLocalizedPoiDescription(asset));
                TryPlayAnimation(npc, asset.CsharpAnimation);
                return;
            }

            // 1. 同地图：如果当前已经在目标地图，直接在本地寻路走过去，绝不跨图
            if (string.Equals(loc.Name, asset.MapName, StringComparison.OrdinalIgnoreCase))
            {
                var safeTarget = MovementPathfinding.FindNearestWalkableTile(loc, target, npc, 3);
                if (Vector2.Distance(npc.Tile, safeTarget) < 1.5f)
                {
                    MarkEntryArrived(entry, Game1.timeOfDay);
                    state.PreviousPoiId = entry.PoiId;
                    transitionContext(state, ScheduleContextPhase.ActiveAtPoi, GetLocalizedPoiDescription(asset));
                    TryPlayAnimation(npc, asset.CsharpAnimation);
                    return;
                }

                if (MovementPathfinding.TryCreatePath(npc, loc, safeTarget, out var controller, out _))
                {
                    npc.controller = controller;
                    npc.addedSpeed = 2;
                    StartFarmPoiWatch(npc, state, entry, transitionContext);
                }
                else
                {
                    MarkEntryArrived(entry, Game1.timeOfDay);
                    state.PreviousPoiId = entry.PoiId;
                    transitionContext(state, ScheduleContextPhase.ActiveAtPoi, GetLocalizedPoiDescription(asset));
                    TryPlayAnimation(npc, asset.CsharpAnimation);
                }
                return;
            }

            // 2. 跨地图：寻找距离当前 NPC 最近且可通行的出口 Warp（拒绝盲选 FirstOrDefault）
            Warp nearestWarp = null;
            Vector2 bestExitTile = Vector2.Zero;
            float minDistance = float.MaxValue;

            if (loc.warps != null && loc.warps.Count > 0)
            {
                foreach (var w in loc.warps)
                {
                    if (w == null || string.IsNullOrWhiteSpace(w.TargetName)) continue;

                    var rawWarpTile = new Vector2(w.X, w.Y);
                    float dist = Vector2.Distance(npc.Tile, rawWarpTile);
                    if (dist < minDistance)
                    {
                        var walkable = MovementPathfinding.FindWalkableTileNearWarp(loc, rawWarpTile, npc);
                        if (walkable != Vector2.Zero)
                        {
                            minDistance = dist;
                            nearestWarp = w;
                            bestExitTile = walkable;
                        }
                    }
                }
            }

            // 室内如果没有显式 Warp（部分室内门靠 TouchAction 触发），兜底直接瞬移
            if (nearestWarp == null)
            {
                ModEntry.SMonitor?.Log(
                    $"[DepartureRouter] {npc.Name} has no valid exit on '{loc.Name}' — warping directly to '{entry.PoiId}'.",
                    LogLevel.Warn);
                MultiMapNavigator.WarpDirectTo(npc, asset.MapName, target);
                MarkEntryArrived(entry, Game1.timeOfDay);
                state.PreviousPoiId = entry.PoiId;
                transitionContext(state, ScheduleContextPhase.ActiveAtPoi, GetLocalizedPoiDescription(asset));
                TryPlayAnimation(npc, asset.CsharpAnimation);
                return;
            }

            // 走到最近的出口，离场后直接瞬移至目的地
            MovementManager.Instance.MoveToTile(npc, bestExitTile,
                onComplete: () =>
                {
                    MultiMapNavigator.WarpDirectTo(npc, asset.MapName, target);
                    MarkEntryArrived(entry, Game1.timeOfDay);
                    state.PreviousPoiId = entry.PoiId;
                    transitionContext(state, ScheduleContextPhase.ActiveAtPoi, GetLocalizedPoiDescription(asset));
                    TryPlayAnimation(npc, asset.CsharpAnimation);
                    ModEntry.SMonitor?.Log(
                        $"[DepartureRouter] {npc.Name} exited '{loc.Name}' via nearest warp → arrived at '{entry.PoiId}'.",
                        LogLevel.Info);
                },
                onFail: () =>
                {
                    ModEntry.SMonitor?.Log(
                        $"[DepartureRouter] {npc.Name} path to nearest exit on '{loc.Name}' failed — warping directly to '{entry.PoiId}'.",
                        LogLevel.Warn);
                    MultiMapNavigator.WarpDirectTo(npc, asset.MapName, target);
                    MarkEntryArrived(entry, Game1.timeOfDay);
                    state.PreviousPoiId = entry.PoiId;
                    transitionContext(state, ScheduleContextPhase.ActiveAtPoi, GetLocalizedPoiDescription(asset));
                    TryPlayAnimation(npc, asset.CsharpAnimation);
                });
        }

        public static void TryPlayAnimation(NPC npc, string animationName)
        {
            if (string.IsNullOrWhiteSpace(animationName)) return;
            try
            {
                switch (animationName)
                {
                    case "PlayArcade":  npc.faceDirection(3); npc.doEmote(16); break;
                    case "SitOnBench":  npc.faceDirection(2);                  break;
                    case "FishingPose": npc.faceDirection(2); npc.doEmote(32); break;
                    case "BrowseShop":  npc.faceDirection(0);                  break;
                    default:
                        ModEntry.SMonitor?.Log(
                            $"[DepartureRouter] Unknown animation '{animationName}' for {npc.Name}.", LogLevel.Debug);
                        break;
                }
            }
            catch (Exception ex)
            {
                ModEntry.SMonitor?.Log($"[DepartureRouter] TryPlayAnimation error: {ex.Message}", LogLevel.Warn);
            }
        }
    }
}
