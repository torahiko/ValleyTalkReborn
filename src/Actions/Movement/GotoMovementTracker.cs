using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Pathfinding;

namespace ValleytalkReborn.Movement
{
    internal sealed class GotoContext
    {
        public NPC Npc;
        public Action OnSuccess;
        public Action OnFail;
        public Vector2 Target;
        public GameLocation ExpectedLocation;
        public Vector2 LastTile;
        public int StuckTicks;
        public int RepathAttempts;
        public readonly GotoTimeoutCounter Timeout = new GotoTimeoutCounter(GotoMovementTracker.GOTO_TIMEOUT_TICKS);
        public SuspendedFollowSnapshot? SuspendedFollow;
    }

    

    /// <summary>
    /// 字典化的 GoTo 移动追踪器：支持多个 NPC 同时执行各自独立的 GoTo 寻路。
    /// 全部状态仅存活于会话内存，OnDayStarted / SaveLoaded 时必须清空。
    /// 主线程专属（PathFindController 强依赖主线程碰撞检测与状态更新）。
    /// </summary>
    internal sealed class GotoMovementTracker
    {
        private readonly Dictionary<string, GotoContext> _activeGotos = new();
        public const int GOTO_TIMEOUT_TICKS = 45 * 60;

        /// <summary>当 GoTo 结束且含有被挂起的约会跟随时，由外部恢复。</summary>
        internal Action<SuspendedFollowSnapshot?> OnSuspendedFollowToRestore;

        /// <summary>当 GoTo 结束（无论成功/失败）时触发，用于外部判断是否恢复原版日程。</summary>
        internal Action<NPC, bool> OnGotoEnded;

        // ───────────────────────────────────────────────
        //  公开 API
        // ───────────────────────────────────────────────

        /// <summary>启动新 Goto（取消该 NPC 旧 Goto）。寻路失败时调用 onFail。</summary>
        public void Start(NPC npc, Vector2 targetTile, Action onSuccess, Action onFail, SuspendedFollowSnapshot? suspendedFollow = null)
        {
            if (npc == null)
            {
                ModEntry.SMonitor?.Log("[GotoMovementTracker] Start: npc is null, ignored.", LogLevel.Warn);
                return;
            }

            if (_activeGotos.ContainsKey(npc.Name))
                EndGoto(_activeGotos[npc.Name], success: false, invokeCallbacks: false, facePlayer: false);

            var loc = npc.currentLocation ?? Game1.player?.currentLocation;
            if (loc == null)
            {
                ModEntry.SMonitor?.Log($"[GotoMovementTracker] Start: {npc.Name} has no current location.", LogLevel.Warn);
                onFail?.Invoke();
                return;
            }

            // 发起自主寻路前，尝试修正异常起点
            if (!MovementPathfinding.TryRecoverStartingTile(npc, loc))
                ModEntry.SMonitor?.Log($"[GotoMovementTracker] {npc.Name} start tile blocked and unrecoverable.", LogLevel.Warn);

            var resolved = MovementPathfinding.ResolveTargetAvoidingPlayer(
                targetTile,
                Game1.player?.Tile ?? Vector2.Zero,
                candidate => MovementPathfinding.IsTileWalkable(loc, candidate, npc));

            if (!MovementPathfinding.TryCreatePath(npc, loc, resolved, out var controller, out var finalTarget))
            {
                ModEntry.SMonitor?.Log(
                    $"[GotoMovementTracker] {npc.Name} pathfinding failed: ({resolved.X},{resolved.Y}) unreachable.",
                    LogLevel.Warn);
                OnSuspendedFollowToRestore?.Invoke(suspendedFollow);
                onFail?.Invoke();
                return;
            }

            var ctx = new GotoContext
            {
                Npc              = npc,
                OnSuccess        = onSuccess,
                OnFail           = onFail,
                Target           = finalTarget,
                ExpectedLocation = loc,
                LastTile         = npc.Tile,
                StuckTicks       = 0,
                RepathAttempts   = 0,
                SuspendedFollow  = suspendedFollow,
            };
            ctx.Timeout.Reset();

            _activeGotos[npc.Name] = ctx;

            npc.addedSpeed = 2;
            npc.controller  = controller;

            ModEntry.SMonitor?.Log(
                $"[GotoMovementTracker] {npc.Name} pathfinding to ({finalTarget.X},{finalTarget.Y}) on '{loc.Name}'.",
                LogLevel.Debug);
        }

        /// <summary>取消指定 NPC 的 Goto。</summary>
        public void Cancel(string npcName, bool invokeFailCallback = false)
        {
            if (_activeGotos.TryGetValue(npcName, out var ctx))
                EndGoto(ctx, success: false, invokeCallbacks: invokeFailCallback, facePlayer: false);
        }

        /// <summary>主循环轮询（快照遍历，防止回调中修改字典抛出异常）。</summary>
        public void TickAll()
        {
            if (_activeGotos.Count == 0)
                return;

            foreach (var ctx in _activeGotos.Values.ToList())
            {
                var npc = ctx.Npc;

                try
                {
                    if (Game1.player == null)
                    {
                        EndGoto(ctx, success: false, invokeCallbacks: true, facePlayer: false);
                        continue;
                    }

                    if (npc.currentLocation != ctx.ExpectedLocation)
                    {
                        ModEntry.SMonitor?.Log(
                            $"[GotoMovementTracker] {npc.Name} GoTo aborted: warped away from '{ctx.ExpectedLocation?.Name}'.",
                            LogLevel.Debug);
                        EndGoto(ctx, success: false, invokeCallbacks: true, facePlayer: false);
                        continue;
                    }

                    bool pathEnded =
                        npc.controller == null ||
                        MovementPathfinding.IsPathDead(npc.controller) ||
                        MovementPathfinding.IsPathDone(npc.controller);

                    bool nearTarget = Vector2.Distance(npc.Tile, ctx.Target) <= 1.75f;

                    // 卡死检测
                    if (!nearTarget && !pathEnded)
                    {
                        if (npc.Tile == ctx.LastTile)
                        {
                            ctx.StuckTicks++;
                            if (ctx.StuckTicks >= 90)
                            {
                                ctx.StuckTicks = 0;
                                ctx.RepathAttempts++;

                                if (ctx.RepathAttempts > 2)
                                {
                                    ModEntry.SMonitor?.Log(
                                        $"[GotoMovementTracker] {npc.Name} stuck too many times, failing GoTo.",
                                        LogLevel.Warn);
                                    EndGoto(ctx, success: false, invokeCallbacks: true, facePlayer: false);
                                    continue;
                                }

                                MovementPathfinding.TryRecoverStartingTile(npc, ctx.ExpectedLocation);
                                if (MovementPathfinding.TryCreatePath(npc, ctx.ExpectedLocation, ctx.Target, out var rep, out var ft))
                                {
                                    ctx.Target = ft;
                                    npc.controller = rep;
                                    npc.addedSpeed = 2;
                                    ModEntry.SMonitor?.Log(
                                        $"[GotoMovementTracker] {npc.Name} repath attempt #{ctx.RepathAttempts} success.",
                                        LogLevel.Debug);
                                }
                            }
                        }
                        else
                        {
                            ctx.LastTile = npc.Tile;
                            ctx.StuckTicks = 0;
                        }
                    }
                    else
                    {
                        ctx.StuckTicks = 0;
                    }

                    if (pathEnded)
                    {
                        EndGoto(ctx, success: nearTarget, invokeCallbacks: true, facePlayer: true);
                        continue;
                    }

                    if (ctx.Timeout.Tick())
                    {
                        ModEntry.SMonitor?.Log($"[GotoMovementTracker] {npc.Name} GoTo timed out.", LogLevel.Warn);
                        EndGoto(ctx, success: false, invokeCallbacks: true, facePlayer: true);
                    }
                }
                catch (Exception ex)
                {
                    ModEntry.SMonitor?.Log(
                        $"[GotoMovementTracker] {npc.Name} TickAll exception: {ex.Message}",
                        LogLevel.Error);
                    EndGoto(ctx, success: false, invokeCallbacks: true, facePlayer: false);
                }
            }
        }

        // ───────────────────────────────────────────────
        //  查询接口
        // ───────────────────────────────────────────────

        public bool IsMoving(string npcName) => _activeGotos.ContainsKey(npcName);

        public int ActiveCount => _activeGotos.Count;

        public NPC GetCurrentGotoNpc(string npcName)
            => _activeGotos.TryGetValue(npcName, out var ctx) ? ctx.Npc : null;

        /// <summary>返回字典中第一个活跃 Goto 的 NPC（用于兼容无参属性调用）。</summary>
        public NPC GetAnyActiveNpc()
        {
            foreach (var ctx in _activeGotos.Values)
                return ctx.Npc;
            return null;
        }

        /// <summary>清空所有活跃 GoTo（不触发回调）。由 OnDayStarted / SaveLoaded 调用。</summary>
        public void Clear()
        {
            foreach (var ctx in _activeGotos.Values)
            {
                var npc = ctx.Npc;
                npc.controller = null;
                npc.addedSpeed = 0;
                npc.Halt();
            }
            _activeGotos.Clear();
        }

        // ───────────────────────────────────────────────
        //  内部
        // ───────────────────────────────────────────────

        private void EndGoto(GotoContext ctx, bool success, bool invokeCallbacks, bool facePlayer)
        {
            if (ctx == null) return;

            var npc       = ctx.Npc;
            var successCb = ctx.OnSuccess;
            var failCb    = ctx.OnFail;
            var suspended = ctx.SuspendedFollow;

            // 重入安全：仅当字典中仍是当前 ctx 时才移除，防止回调重入导致的新任务被旧任务误删
            if (_activeGotos.TryGetValue(npc.Name, out var current) && ReferenceEquals(current, ctx))
                _activeGotos.Remove(npc.Name);

            npc.controller = null;
            npc.addedSpeed = 0;
            npc.Halt();

            if (facePlayer && Game1.player != null && npc.currentLocation == Game1.player.currentLocation)
                npc.faceGeneralDirection(Game1.player.getStandingPosition(), 0, false, false);

            // 预留恢复逻辑接口：被挂起的约会跟随由外部恢复
            if (suspended != null)
                OnSuspendedFollowToRestore?.Invoke(suspended);

            if (invokeCallbacks)
            {
                if (success) successCb?.Invoke();
                else         failCb?.Invoke();
            }

            // 通知外部 GoTo 已结束，由外部判断是否恢复原版日程
            OnGotoEnded?.Invoke(npc, success);
        }
    }
}

