using System;
using System.Collections.Generic;
using System.Linq;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;

namespace ValleytalkReborn.Movement
{
    internal enum PendingPhase
    {
        /// <summary>等待对话框出现（刚入队时）</summary>
        WaitingDialogueOpen,
        /// <summary>对话框已出现，正在倒计时等待关闭</summary>
        WaitingDialogueClose,
        /// <summary>倒计时归零，正在轮询确认菜单已关闭</summary>
        WaitingMenuGone,
    }

    internal sealed class PendingContext
    {
        public NPC Npc;
        public MovementType Movement;

        // 表情可以和移动动作共存
        public NPC EmoteNpc;
        public int EmoteId = -1;

        public PendingPhase Phase;
        public int CountdownTimer;        // WaitingDialogueClose 阶段的倒计时
        public int DialogueOpenWaitTicks; // WaitingDialogueOpen 阶段的等待计数
        public int MenuCloseRetry;        // WaitingMenuGone 阶段的重试计数
    }

    /// <summary>
    /// 字典化的 Pending 动作队列：支持多个 NPC 同时排队各自的对话等待 + 动作发射。
    /// 全部状态仅存活于会话内存，OnDayStarted / SaveLoaded 时必须清空（ResetAll）。
    /// 主线程专属（对话框 / 菜单状态轮询、doEmote、ExecuteMovement 回调均依赖主线程）。
    /// 动作执行通过构造注入的 ExecuteMovement 回调提供；Queue 本身不实现动作。
    /// MMR-03-FIX1：构造签名仅保留 executeMovement 一个参数（emote 由 Queue 内部 DelayedAction 承接）。
    /// </summary>
    internal sealed class PendingActionQueue
    {
        // ───────────────────────────────────────────────
        //  常量（数值 = 现有实现原样搬移，禁止改动）
        // ───────────────────────────────────────────────

        private const int DIALOGUE_OPEN_TIMEOUT_TICKS = 120; // 原 DIALOGUE_OPEN_WAIT_MAX
        private const int MENU_GONE_TIMEOUT_TICKS     = 60;  // 原 MENU_CLOSE_RETRY_MAX
        private const int PENDING_COUNTDOWN            = 150; // 原 PENDING_COUNTDOWN

        // ───────────────────────────────────────────────
        //  字段
        // ───────────────────────────────────────────────

        private readonly Dictionary<string, PendingContext> _pending = new(); // Key = npc.Name，Memory 作用域
        private readonly Action<NPC, MovementType> _executeMovementCallback;

        public PendingActionQueue(Action<NPC, MovementType> executeMovement)
        {
            _executeMovementCallback = executeMovement ?? throw new ArgumentNullException(nameof(executeMovement));
        }

        // ───────────────────────────────────────────────
        //  公开 API
        // ───────────────────────────────────────────────

        /// <summary>当前活跃 Pending 数。</summary>
        public int ActiveCount => _pending.Count;

        /// <summary>指定 NPC 是否存在活跃 Pending。</summary>
        public bool HasPending(string npcName)
            => !string.IsNullOrEmpty(npcName) && _pending.ContainsKey(npcName);

        /// <summary>入队一个移动动作（对话框等待 + 动作队列）。同 NPC 同动作幂等。</summary>
        public void Enqueue(NPC npc, MovementType movement, bool skipDialogueWait = false)
        {
            if (npc == null)
            {
                ModEntry.SMonitor?.Log("[PendingActionQueue] Enqueue: npc is null, ignored.", LogLevel.Trace);
                return;
            }

            if (_pending.TryGetValue(npc.Name, out var existing) && existing.Movement == movement)
            {
                // 幂等：同 NPC 同一动作已在队列中，直接 return
                return;
            }

            var ctx = new PendingContext
            {
                Npc      = npc,
                Movement = movement,
            };

            if (skipDialogueWait)
            {
                ctx.Phase          = PendingPhase.WaitingMenuGone;
                ctx.CountdownTimer = 0;
            }
            else
            {
                ctx.Phase          = PendingPhase.WaitingDialogueOpen;
                ctx.CountdownTimer = PENDING_COUNTDOWN;
            }

            _pending[npc.Name] = ctx;

            ModEntry.SMonitor?.Log(
                $"[PendingActionQueue] {npc.Name} enqueue movement={movement}, skipDialogueWait={skipDialogueWait}.",
                LogLevel.Trace);
        }

        /// <summary>入队一个表情。若该 NPC 已有 Pending 则附加（不动 Movement / Phase）；否则新建。</summary>
        public void EnqueueEmote(NPC npc, int emoteId)
        {
            if (npc == null)
            {
                ModEntry.SMonitor?.Log("[PendingActionQueue] EnqueueEmote: npc is null, ignored.", LogLevel.Trace);
                return;
            }

            if (emoteId == -1)
                return;

            if (npc.IsInvisible || npc.currentLocation == null)
                return;

            if (_pending.TryGetValue(npc.Name, out var existing))
            {
                // 有移动动作正在等待，附加 Emote 到现有 pending
                existing.EmoteNpc = npc;
                existing.EmoteId  = emoteId;
                return;
            }

            // 没有移动动作在等待，单独为 Emote 创建一个 pending（沿用现有行为：从 WaitingDialogueOpen 起步）
            var ctx = new PendingContext
            {
                Npc             = npc,
                Movement        = MovementType.None,
                EmoteNpc        = npc,
                EmoteId         = emoteId,
                Phase           = PendingPhase.WaitingDialogueOpen,
                CountdownTimer  = PENDING_COUNTDOWN,
            };

            _pending[npc.Name] = ctx;

            ModEntry.SMonitor?.Log(
                $"[PendingActionQueue] {npc.Name} enqueue emote only, emoteId={emoteId}.",
                LogLevel.Trace);
        }

        /// <summary>取消指定 NPC 的 Pending。仅移除条目，绝不触发动作。</summary>
        public void Cancel(string npcName)
        {
            if (string.IsNullOrEmpty(npcName))
                return;

            if (_pending.Remove(npcName))
            {
                ModEntry.SMonitor?.Log($"[PendingActionQueue] {npcName} cancelled.", LogLevel.Trace);
            }
            else
            {
                ModEntry.SMonitor?.Log($"[PendingActionQueue] Cancel: {npcName} not found, no-op.", LogLevel.Trace);
            }
        }

        /// <summary>主循环轮询（快照遍历，per-ctx try/catch，单条异常不得中断其余 NPC）。</summary>
        public void TickAll()
        {
            if (_pending.Count == 0)
                return;

            foreach (var ctx in _pending.Values.ToList())
            {
                try
                {
                    if (ctx.Npc == null)
                    {
                        ModEntry.SMonitor?.Log(
                            "[PendingActionQueue] TickAll: ctx.Npc is null, removing entry.",
                            LogLevel.Debug);
                        RemoveIfCurrent(ctx);
                        continue;
                    }

                    switch (ctx.Phase)
                    {
                        case PendingPhase.WaitingDialogueOpen:
                            HandleDialogueOpen(ctx);
                            break;
                        case PendingPhase.WaitingDialogueClose:
                            HandleDialogueClose(ctx);
                            break;
                        case PendingPhase.WaitingMenuGone:
                            HandleMenuGone(ctx);
                            break;
                    }
                }
                catch (Exception ex)
                {
                    var name = ctx.Npc?.Name ?? "(null)";
                    ModEntry.SMonitor?.Log(
                        $"[PendingActionQueue] TickAll unhandled exception for {name}: {ex}",
                        LogLevel.Error);
                    RemoveIfCurrent(ctx);
                }
            }
        }

        /// <summary>清空所有活跃 Pending。仅移除全部条目，绝不触发任何动作。由 OnDayStarted / SaveLoaded 调用。</summary>
        public void ResetAll()
        {
            var snapshot = _pending.Values.ToList();
            _pending.Clear();

            if (snapshot.Count > 0)
                ModEntry.SMonitor?.Log(
                    $"[PendingActionQueue] ResetAll cleared {snapshot.Count} pending entries.",
                    LogLevel.Debug);
        }

        // ───────────────────────────────────────────────
        //  状态机分派
        // ───────────────────────────────────────────────

        private void HandleDialogueOpen(PendingContext ctx)
        {
            // ★ MMR-03 唯一授权的语义收紧：per-NPC 对话归属判定。
            // 原实现为 (Game1.activeClickableMenu != null || Game1.dialogueUp)，
            // 多实例化后必须收窄为当前说话者正是本 ctx.Npc，否则 NPC A 的对话框会误触发 NPC B。
            if (Game1.dialogueUp && Game1.currentSpeaker == ctx.Npc)
            {
                ctx.Phase = PendingPhase.WaitingDialogueClose;
                return;
            }

            ctx.DialogueOpenWaitTicks++;

            if (ctx.DialogueOpenWaitTicks >= DIALOGUE_OPEN_TIMEOUT_TICKS)
            {
                ModEntry.SMonitor?.Log(
                    $"[PendingActionQueue] {ctx.Npc.Name} dialogue open wait timed out after {DIALOGUE_OPEN_TIMEOUT_TICKS} ticks, proceeding without dialogue.",
                    LogLevel.Debug);

                // 超时：跳过等待，进入菜单轮询阶段（沿用现有行为，不立即发射）
                ctx.Phase          = PendingPhase.WaitingMenuGone;
                ctx.CountdownTimer = 0;
            }
        }

        private void HandleDialogueClose(PendingContext ctx)
        {
            if (ctx.CountdownTimer > 0)
            {
                ctx.CountdownTimer--;
                return;
            }

            // 倒计时归零，进入轮询阶段
            ctx.Phase = PendingPhase.WaitingMenuGone;
        }

        private void HandleMenuGone(PendingContext ctx)
        {
            if (Game1.activeClickableMenu != null || Game1.dialogueUp)
            {
                ctx.MenuCloseRetry++;

                if (ctx.MenuCloseRetry > MENU_GONE_TIMEOUT_TICKS)
                {
                    ModEntry.SMonitor?.Log(
                        $"[PendingActionQueue] {ctx.Npc.Name} pending action aborted: menu/dialogue could not be closed after {MENU_GONE_TIMEOUT_TICKS} ticks.",
                        LogLevel.Warn);

                    // 超时：放弃执行，仅移除字典条目，不触发任何动作
                    RemoveIfCurrent(ctx);
                    return;
                }

                // 沿用现有行为：只关闭对话框，避免误关其他 UI
                if (Game1.activeClickableMenu is DialogueBox)
                {
                    try
                    {
                        Game1.activeClickableMenu.exitThisMenu(false);
                        Game1.dialogueUp = false;
                        Game1.player?.forceCanMove();
                    }
                    catch (Exception ex)
                    {
                        ModEntry.SMonitor?.Log(
                            $"[PendingActionQueue] {ctx.Npc.Name} exitThisMenu error: {ex.Message}",
                            LogLevel.Warn);
                    }
                }
                else if (Game1.dialogueUp && Game1.activeClickableMenu == null)
                {
                    // dialogueUp 残留但没有实际菜单时，温和清理
                    Game1.dialogueUp = false;
                    Game1.player?.forceCanMove();
                }

                return;
            }

            // 菜单已关闭，执行动作
            ctx.MenuCloseRetry = 0;
            FirePendingAction(ctx);
        }

        /// <summary>发射动作。必须先移除字典条目（重入安全）再触发回调。</summary>
        private void FirePendingAction(PendingContext ctx)
        {
            // 重入安全：仅当字典当前映射仍为该 ctx 时移除，然后才触发动作
            if (!RemoveIfCurrent(ctx))
                return;

            // Emote 先触发（延迟执行，lambda 内部 try/catch，异常不得阻断移动）
            if (ctx.EmoteNpc != null && ctx.EmoteId != -1)
            {
                var emoteNpc = ctx.EmoteNpc;
                var emoteId  = ctx.EmoteId;

                StardewValley.DelayedAction.functionAfterDelay(() =>
                {
                    try
                    {
                        emoteNpc?.doEmote(emoteId);
                    }
                    catch (Exception ex)
                    {
                        ModEntry.SMonitor?.Log(
                            $"[PendingActionQueue] doEmote error: {ex.Message}",
                            LogLevel.Warn);
                    }
                }, 1);
            }

            // 移动动作后触发
            if (ctx.Movement != MovementType.None && ctx.Npc != null)
            {
                _executeMovementCallback(ctx.Npc, ctx.Movement);
            }
        }

        // ───────────────────────────────────────────────
        //  内部辅助
        // ───────────────────────────────────────────────

        /// <summary>重入安全移除：仅当字典中映射的仍是当前实例时才移除并返回 true，防止重入导致新任务被误删。</summary>
        private bool RemoveIfCurrent(PendingContext ctx)
        {
            if (ctx?.Npc == null)
                return false;

            if (_pending.TryGetValue(ctx.Npc.Name, out var current) && ReferenceEquals(current, ctx))
            {
                _pending.Remove(ctx.Npc.Name);
                return true;
            }

            return false;
        }
    }
}
