using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;

namespace ValleytalkReborn.Movement
{
    internal sealed class StepContext
    {
        public NPC Npc;
        public Vector2 TargetPixel;
        public int Timeout;
    }

    /// <summary>
    /// 字典化的单步像素插值追踪器：支持多个 NPC 同时执行各自独立的单步滑行。
    /// 全部状态仅存活于会话内存，OnDayStarted / ResetAllState 时必须清空。
    /// 主线程专属（修改 NPC 坐标与朝向动画）。
    /// </summary>
    internal sealed class StepMovementTracker
    {
        // ───────────────────────────────────────────────
        //  常量
        // ───────────────────────────────────────────────

        private const float ARRIVE_DISTANCE_PIXELS = 2f;
        private const float SPEED_GAIN = 0.25f;
        private const float MIN_SPEED = 1f;
        private const float MAX_SPEED = 4f;
        private const int DEFAULT_TIMEOUT_TICKS = 60;

        // ───────────────────────────────────────────────
        //  字段
        // ───────────────────────────────────────────────

        private readonly Dictionary<string, StepContext> _activeSteps = new();
        private readonly Action<NPC> _clearNpcMovement;

        public StepMovementTracker(Action<NPC> clearNpcMovement)
        {
            _clearNpcMovement = clearNpcMovement ?? (_ => { });
        }

        // ───────────────────────────────────────────────
        //  公开 API
        // ───────────────────────────────────────────────

        /// <summary>启动单步像素插值。若该 NPC 已在滑行，先取消旧任务。</summary>
        public void Start(NPC npc, Vector2 targetPixel, int timeout = DEFAULT_TIMEOUT_TICKS)
        {
            if (npc == null)
            {
                ModEntry.SMonitor?.Log("[StepMovementTracker] Start: npc is null, ignored.", LogLevel.Trace);
                return;
            }

            if (_activeSteps.ContainsKey(npc.Name))
                Cancel(npc.Name);

            _activeSteps[npc.Name] = new StepContext
            {
                Npc         = npc,
                TargetPixel = targetPixel,
                Timeout     = timeout,
            };

            ModEntry.SMonitor?.Log(
                $"[StepMovementTracker] {npc.Name} stepping to ({targetPixel.X},{targetPixel.Y}), timeout={timeout}.",
                LogLevel.Debug);
        }

        /// <summary>取消指定 NPC 的 Step 移动。</summary>
        public void Cancel(string npcName)
        {
            if (!_activeSteps.TryGetValue(npcName, out var ctx))
                return;

            RemoveAndClear(ctx);

            ModEntry.SMonitor?.Log($"[StepMovementTracker] {npcName} step cancelled.", LogLevel.Debug);
        }

        /// <summary>清空所有活跃 Step（逐个恢复状态）。由 OnDayStarted / ResetAllState 调用。</summary>
        public void ResetAll()
        {
            var count = _activeSteps.Count;
            foreach (var ctx in _activeSteps.Values.ToList())
                RemoveAndClear(ctx);

            _activeSteps.Clear();

            if (count > 0)
                ModEntry.SMonitor?.Log($"[StepMovementTracker] ResetAll cleared {count} active steps.", LogLevel.Debug);
        }

        /// <summary>主循环轮询插值计算（快照遍历，防止回调中修改字典抛出异常）。</summary>
        public void TickAll()
        {
            // 零分配快速退出（绝大多数帧无 Step）
            if (_activeSteps.Count == 0)
                return;

            foreach (var ctx in _activeSteps.Values.ToList())
            {
                try
                {
                    // 地图脱节保护：NPC 为 null 或与玩家不在同一地图时安全退出
                    if (ctx.Npc?.currentLocation == null || Game1.player?.currentLocation == null ||
                        ctx.Npc.currentLocation != Game1.player.currentLocation)
                    {
                        ModEntry.SMonitor?.Log(
                            $"[StepMovementTracker] {ctx.Npc?.Name} step aborted: map desync.",
                            LogLevel.Debug);
                        RemoveAndClear(ctx);
                        continue;
                    }

                    var diff = ctx.TargetPixel - ctx.Npc.Position;
                    var dist = diff.Length();

                    ctx.Timeout--;

                    // 到达或超时判定（吸附语义）
                    if (dist <= ARRIVE_DISTANCE_PIXELS || ctx.Timeout <= 0)
                    {
                        // 到达兜底：对齐坐标并清理
                        ctx.Npc.Position = ctx.TargetPixel;
                        RemoveAndClear(ctx);

                        ModEntry.SMonitor?.Log(
                            $"[StepMovementTracker] {ctx.Npc.Name} step finished at ({ctx.TargetPixel.X},{ctx.TargetPixel.Y}).",
                            LogLevel.Debug);
                        continue;
                    }

                    // 逼近计算（dist > ARRIVE_DISTANCE_PIXELS 杜绝除零风险）
                    var speed = Math.Clamp(dist * SPEED_GAIN, MIN_SPEED, MAX_SPEED);
                    ctx.Npc.Position += (diff / dist) * speed;

                    // 朝向更新
                    if (Math.Abs(diff.X) >= Math.Abs(diff.Y) && diff.X != 0)
                        ctx.Npc.faceDirection(diff.X > 0 ? 1 : 3);
                    else if (diff.Y != 0)
                        ctx.Npc.faceDirection(diff.Y > 0 ? 2 : 0);

                    // 触发原移动动画
                    if (Game1.currentGameTime != null)
                        ctx.Npc.animateInFacingDirection(Game1.currentGameTime);
                }
                catch (Exception ex)
                {
                    ModEntry.SMonitor?.Log(
                        $"[StepMovementTracker] Error ticking step for {ctx.Npc?.Name}: {ex}",
                        LogLevel.Error);
                    RemoveAndClear(ctx);
                }
            }
        }

        // ───────────────────────────────────────────────
        //  查询接口
        // ───────────────────────────────────────────────

        public bool IsActive(string npcName) => _activeSteps.ContainsKey(npcName);

        public int ActiveCount => _activeSteps.Count;

        /// <summary>返回字典中第一个活跃 Step 的 NPC（用于兼容无参属性调用）。</summary>
        public NPC GetAnyActiveNpc() => _activeSteps.Values.FirstOrDefault()?.Npc;

        // ───────────────────────────────────────────────
        //  内部辅助
        // ───────────────────────────────────────────────

        /// <summary>重入安全移除：仅当字典中映射的仍是当前实例时才移除，防止重入导致新任务被误删。</summary>
        private void RemoveAndClear(StepContext ctx)
        {
            if (ctx?.Npc == null)
                return;

            if (_activeSteps.TryGetValue(ctx.Npc.Name, out var current) && ReferenceEquals(current, ctx))
                _activeSteps.Remove(ctx.Npc.Name);

            _clearNpcMovement(ctx.Npc);
        }
    }
}
