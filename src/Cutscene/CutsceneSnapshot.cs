using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Pathfinding;

namespace ValleytalkReborn.Cutscene
{
    /// <summary>
    /// 过场现场快照：记录玩家与 NPC 初始状态（坐标、朝向、待机动画、在途日程与寻路控制器），
    /// 保证演出结束后幕后（黑屏下）零脏写、无感且忠实地复原现场。
    /// </summary>
    internal sealed class CutsceneSnapshot
    {
        public struct ActorState
        {
            public NPC Npc;
            public GameLocation OriginalLocation;
            public Vector2 OriginalTile;
            public int OriginalFacing;

            // 日程停靠待机/特殊动作状态（如 Alex 玩球、Sam 弹吉他、塞巴斯蒂安抽烟等）
            public string EndOfRouteBehaviorName;
            public bool DoingEndOfRouteAnimation;
            public bool GoingToDoEndOfRouteAnimation;

            // 原生日程寻路控制器状态（用于在途移动恢复）
            public bool HadActiveController;
            public List<Point> SavedRoute;
            public Point ControllerEndPoint;
            public int ControllerFacing;
            public PathFindController.endBehavior ControllerEndBehavior;
            public bool ControllerNpcSchedule;
            public SchedulePathDescription DirectionsToNewLocation;
            public bool FollowSchedule;
        }

        public Vector2 PlayerTile;
        public int PlayerFacing;
        public bool PlayerCanMove;
        public List<ActorState> ActorStates = new();

        /// <summary>
        /// 捕获当前状态快照
        /// </summary>
        public static CutsceneSnapshot Capture(IEnumerable<NPC> actors)
        {
            var snapshot = new CutsceneSnapshot
            {
                PlayerTile = Game1.player.Tile,
                PlayerFacing = Game1.player.FacingDirection,
                PlayerCanMove = Game1.player.CanMove
            };

            foreach (var npc in actors)
            {
                if (npc?.currentLocation == null) continue;

                var state = new ActorState
                {
                    Npc = npc,
                    OriginalLocation = npc.currentLocation,
                    OriginalTile = npc.Tile,
                    OriginalFacing = npc.FacingDirection,

                    EndOfRouteBehaviorName = npc.endOfRouteBehaviorName?.Value,
                    DoingEndOfRouteAnimation = npc.doingEndOfRouteAnimation?.Value ?? false,
                    GoingToDoEndOfRouteAnimation = npc.goingToDoEndOfRouteAnimation?.Value ?? false,

                    HadActiveController = npc.controller != null,
                    DirectionsToNewLocation = npc.DirectionsToNewLocation,
                    FollowSchedule = npc.followSchedule
                };

                if (npc.controller != null)
                {
                    if (npc.controller.pathToEndPoint != null)
                    {
                        state.SavedRoute = npc.controller.pathToEndPoint.ToList();
                    }
                    state.ControllerEndPoint = npc.controller.endPoint;
                    state.ControllerFacing = npc.controller.finalFacingDirection;
                    state.ControllerEndBehavior = npc.controller.endBehaviorFunction;
                    state.ControllerNpcSchedule = npc.controller.NPCSchedule;
                }

                snapshot.ActorStates.Add(state);
            }
            return snapshot;
        }

        /// <summary>
        /// 优雅复原：恢复玩家控制权，NPC 归位并复原待机动作与日程路线
        /// </summary>
        public void Restore()
        {
            try
            {
                // 1. 恢复玩家
                if (Game1.player != null)
                {
                    Game1.player.Position = PlayerTile * 64f;
                    Game1.player.faceDirection(PlayerFacing);
                    Game1.player.CanMove = PlayerCanMove; // 快照保真：还原接管前的真实值
                    Game1.player.freezePause = 0;
                }

                // 2. 恢复演员状态与原版日程
                foreach (var state in ActorStates)
                {
                    var npc = state.Npc;
                    if (npc == null) continue;

                    npc.Halt();
                    npc.controller = null;
                    npc.temporaryController = null;
                    npc.addedSpeed = 0;
                    npc.forceUpdateTimer = 0;
                    npc.isCharging = false;

                    // 归位
                    if (npc.currentLocation != state.OriginalLocation && state.OriginalLocation != null)
                    {
                        Game1.warpCharacter(npc, state.OriginalLocation.NameOrUniqueName, state.OriginalTile);
                    }
                    else
                    {
                        npc.setTilePosition(new Point((int)state.OriginalTile.X, (int)state.OriginalTile.Y));
                    }
                    npc.faceDirection(state.OriginalFacing);

                    // 2.1 恢复日程停靠待机/特殊动作（如 Alex 在树下玩球、Sam 弹吉他、塞巴斯蒂安抽烟等）
                    bool restoredAnimation = false;
                    string behaviorToRestore = state.EndOfRouteBehaviorName;

                    // 兜底：若快照中行为名为空，但快照记录了处于待机动作中，或当前日程停靠点有行为名
                    if (string.IsNullOrEmpty(behaviorToRestore))
                    {
                        var currentStop = npc.Schedule?
                            .Where(kv => kv.Key <= Game1.timeOfDay)
                            .OrderByDescending(kv => kv.Key)
                            .FirstOrDefault().Value;

                        if (currentStop != null && !string.IsNullOrEmpty(currentStop.endOfRouteBehavior))
                        {
                            if (Vector2.Distance(npc.Tile, new Vector2(currentStop.targetTile.X, currentStop.targetTile.Y)) < 2.5f)
                            {
                                behaviorToRestore = currentStop.endOfRouteBehavior;
                            }
                        }
                    }

                    // 仅当 NPC 在进入过场前确实处于动作态中时，才还原特殊动作，绝不根据残留历史行为名误判强行恢复
                    if (!string.IsNullOrEmpty(behaviorToRestore) && (state.DoingEndOfRouteAnimation || state.GoingToDoEndOfRouteAnimation))
                    {
                        try
                        {
                            npc.StartActivityRouteEndBehavior(behaviorToRestore, null);
                            restoredAnimation = true;
                            ModEntry.SMonitor?.Log(
                                $"[CutsceneSnapshot] Restored end-of-route activity '{behaviorToRestore}' for {npc.Name}.",
                                LogLevel.Debug);
                        }
                        catch (Exception ex)
                        {
                            ModEntry.SMonitor?.Log(
                                $"[CutsceneSnapshot] Failed to restore activity '{behaviorToRestore}' for {npc.Name}: {ex.Message}",
                                LogLevel.Warn);
                        }
                    }

                    if (!restoredAnimation)
                    {
                        // 动作未恢复时，必须彻底解除动作态与定身封锁，杜绝物理锁死
                        try
                        {
                            npc.EndActivityRouteEndBehavior();
                        }
                        catch { }
                        npc.doingEndOfRouteAnimation.Value = false;
                        npc.goingToDoEndOfRouteAnimation.Value = false;
                        npc.movementPause = 0;
                        npc.isCharging = false;
                        npc.Sprite?.StopAnimation();
                        try
                        {
                            typeof(Character).GetField("freezeMotion", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public)
                                ?.SetValue(npc, false);
                        }
                        catch { }
                    }

                    // 2.2 恢复在途移动（若演出前正在日程行进中）
                    if (!restoredAnimation && state.HadActiveController)
                    {
                        try
                        {
                            if (state.SavedRoute != null && state.SavedRoute.Count > 0)
                            {
                                var remainingStack = new Stack<Point>(state.SavedRoute.AsEnumerable().Reverse());
                                npc.controller = new PathFindController(remainingStack, npc, npc.currentLocation)
                                {
                                    endPoint = state.ControllerEndPoint,
                                    finalFacingDirection = state.ControllerFacing,
                                    endBehaviorFunction = state.ControllerEndBehavior,
                                    NPCSchedule = state.ControllerNpcSchedule
                                };
                                npc.DirectionsToNewLocation = state.DirectionsToNewLocation;
                                npc.followSchedule = state.FollowSchedule;

                                ModEntry.SMonitor?.Log(
                                    $"[CutsceneSnapshot] Resumed schedule route for {npc.Name} with {state.SavedRoute.Count} remaining steps.",
                                    LogLevel.Debug);
                            }
                            else if (state.DirectionsToNewLocation != null)
                            {
                                npc.DirectionsToNewLocation = state.DirectionsToNewLocation;
                                npc.followSchedule = state.FollowSchedule;
                                npc.controller = new PathFindController(
                                    npc,
                                    npc.currentLocation,
                                    state.DirectionsToNewLocation.targetTile,
                                    state.DirectionsToNewLocation.facingDirection,
                                    state.ControllerEndBehavior
                                );
                                ModEntry.SMonitor?.Log(
                                    $"[CutsceneSnapshot] Repathed schedule route for {npc.Name} towards ({state.DirectionsToNewLocation.targetTile.X},{state.DirectionsToNewLocation.targetTile.Y}).",
                                    LogLevel.Debug);
                            }
                        }
                        catch (Exception ex)
                        {
                            ModEntry.SMonitor?.Log(
                                $"[CutsceneSnapshot] Failed to resume controller for {npc.Name}: {ex.Message}",
                                LogLevel.Warn);
                        }
                    }

                    // 2.3 唤醒 NPC 后续日常日程（若未恢复在途移动与停靠动作，装载未来停靠点，如 14:00、18:00）
                    if (!restoredAnimation && !state.HadActiveController)
                    {
                        try
                        {
                            Movement.ScheduleRestorer restorer = new Movement.ScheduleRestorer(
                                (n, tile, onSuccess, onFail) => { },
                                n => false
                            );
                            restorer.TryRestoreSchedule(npc);
                        }
                        catch (Exception ex)
                        {
                            ModEntry.SMonitor?.Log(
                                $"[CutsceneSnapshot] Failed to restore schedule for {npc.Name}: {ex.Message}",
                                LogLevel.Warn);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                ModEntry.SMonitor?.Log(
                    $"[CutsceneSnapshot] Restore failed: {ex.Message}",
                    LogLevel.Error);
            }
        }
    }
}
