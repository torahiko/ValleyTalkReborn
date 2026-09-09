using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace ValleytalkReborn;

/// <summary>
/// Ambient Bark 状态存储：纯数据容器，职责单一。
/// 只存储运行时必需的最小状态集，不包含游戏逻辑判断。
/// </summary>
internal sealed class AmbientBarkStateStore
{
    /// <summary>
    /// 单个 NPC 的 Bark 状态 - 极简结构
    /// </summary>
    internal sealed class State
    {
        // ── 台词队列 ──
        public Queue<string> BarkQueue { get; } = new Queue<string>();

        // ── 请求控制 ──
        public CancellationTokenSource BackgroundCts { get; private set; } = new CancellationTokenSource();
        public int RequestId { get; private set; }
        public bool IsRequesting { get; set; }

        // ── 展示控制 ──
        public int DisplayCountdown { get; set; }
        public bool HasPlayedFirst { get; set; }

        // ── 冷却控制（Tick 计数，与游戏暂停/加速完全同步）──

        /// <summary>
        /// 冷却剩余 Ticks（游戏帧）。null 或 &lt;= 0 表示立即可触发。
        /// 使用 Tick 而非 DateTime，确保与游戏暂停/加速完全同步。
        /// 30 秒 = 1800 Ticks（60 FPS）。
        /// </summary>
        public int? CooldownTicksRemaining { get; set; }

        // ── 记忆系统（三档时间路由）──

        /// <summary>
        /// 上一段思绪的最后 2 条原文（按时间顺序）。
        /// 用于短间隔（≤3分钟）延续判断。
        /// </summary>
        public List<string> LastThreadTail { get; } = new List<string>(2);

        /// <summary>
        /// 上一段思绪结束时的真实时钟时间（UTC）。
        /// 用于计算分钟间隔。
        /// </summary>
        public DateTime? LastThreadEndedAt { get; set; }

        /// <summary>
        /// 上一段思绪结束时的游戏内时间（HHmm 格式，如 1430 表示 14:30）。
        /// 用于判断是否还在同一时段（避免"早上想的事到晚上还在接"）。
        /// </summary>
        public int LastThreadGameTimeOfDay { get; set; }

        /// <summary>
        /// 上一段思绪结束时的存档日期（Game1.Date.TotalDays）。
        /// 用于跨天判断——但注意：读档会导致这个值回退，所以跨天检测只能作为"肯定不是同一天"的否定判据。
        /// </summary>
        public int LastThreadSaveDayNumber { get; set; }

        /// <summary>
        /// 最近 3 条台词（用于中间隔"避免重复"参考）。
        /// 不用于延续，只用于新鲜度检查。
        /// </summary>
        public Queue<string> RecentBarks { get; } = new Queue<string>(3);

        // ── 辅助方法 ──

        public void AddRecentBark(string bark)
        {
            if (string.IsNullOrWhiteSpace(bark)) return;

            RecentBarks.Enqueue(bark);
            if (RecentBarks.Count > 3)
                RecentBarks.Dequeue();
        }

        public void ReplaceCts()
        {
            try
            {
                BackgroundCts?.Cancel();
                BackgroundCts?.Dispose();
            }
            catch
            {
                // 忽略取消/释放异常
            }

            BackgroundCts = new CancellationTokenSource();
            RequestId++;
        }

        /// <summary>
        /// 硬重置：清空所有状态，包括思绪记忆。
        /// 使用场景：被 A2A 打断、跟随状态切换等"外部事件中断"时——
        /// 这些场景下延续上一段思绪没有意义（A2A 参与者/话题都变了，
        /// 强行接续会污染新一轮生成的上下文）。
        /// </summary>
        public void Clear()
        {
            ClearRuntimeState();
            CooldownTicksRemaining = null; // 硬重置彻底清空冷却
            LastThreadTail.Clear();
            LastThreadEndedAt = null;
            LastThreadGameTimeOfDay = 0;
            LastThreadSaveDayNumber = 0;
            RecentBarks.Clear();
        }

        /// <summary>
        /// 软重置：只清空运行态字段（队列、请求状态、展示进度），保留思绪记忆。
        /// 使用场景：冷却自然过期、准备释放内存对象时——NPC 并未被外部事件打断，
        /// 刚结束的这段思绪应该能被下一次生成读到（BuildMemoryContext 依赖这些字段）。
        /// </summary>
        public void ClearRuntimeState()
        {
            ReplaceCts();
            BarkQueue.Clear();
            IsRequesting = false;
            DisplayCountdown = 0;
            HasPlayedFirst = false;
            CooldownTicksRemaining = null; // 必须清空为 null，关闭 TickStates 分支 2 的触发条件
        }

        /// <summary>
        /// 判断当前是否处于冷却期
        /// </summary>
        public bool IsInCooldown()
        {
            if (!CooldownTicksRemaining.HasValue)
                return false;

            return CooldownTicksRemaining.Value > 0;
        }
    }

    private readonly ConcurrentDictionary<string, State> _states =
        new ConcurrentDictionary<string, State>(StringComparer.OrdinalIgnoreCase);

    internal bool TryGet(string npcName, out State state)
    {
        return _states.TryGetValue(npcName, out state);
    }

    internal State GetOrCreate(string npcName)
    {
        return _states.GetOrAdd(npcName, _ => new State());
    }

    internal bool TryRemove(string npcName, out State state)
    {
        return _states.TryRemove(npcName, out state);
    }

    internal IEnumerable<KeyValuePair<string, State>> Snapshot()
    {
        return _states.ToList();
    }

    internal void Clear()
    {
        foreach (var kv in _states)
        {
            lock (kv.Value)
            {
                kv.Value.Clear();
            }
        }
        _states.Clear();
    }

    /// <summary>
    /// 硬重置指定 NPC 的状态（用于强制清理，包括思绪记忆）。
    /// 调用场景：A2A 锁定、跟随状态切换等"被外部事件打断"的情况。
    /// </summary>
    internal void HardReset(string npcName)
    {
        if (!_states.TryGetValue(npcName, out var state))
            return;

        lock (state)
        {
            state.Clear();
        }

        ModEntry.SMonitor?.Log(
            $"[AmbientBark] Hard reset: {npcName} (cleared all states including memory)",
            StardewModdingAPI.LogLevel.Debug);
    }

    /// <summary>
    /// 软重置：仅清空运行态字段，保留思绪记忆（LastThreadTail 等）。
    /// 调用场景：TickStates 中"冷却已过、准备释放内存对象"的常规清理——
    /// NPC 并未被外部事件打断，这段思绪应该留给下一次生成参考。
    /// </summary>
    internal void ClearRuntimeStateOnly(string npcName)
    {
        if (!_states.TryGetValue(npcName, out var state))
            return;

        lock (state)
        {
            state.ClearRuntimeState();
        }

        ModEntry.SMonitor?.Log(
            $"[AmbientBark] Soft reset: {npcName} (cleared runtime states, kept memory)",
            StardewModdingAPI.LogLevel.Trace);
    }
}