using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace ValleytalkReborn;

/// <summary>
/// Ambient Bark 状态存储：负责 NPC Bark 生命周期的纯数据状态管理。
/// 不访问 Game1、A2A Session、A2Cooldown 或任何其他非 Bark 逻辑。
/// 主线程读写均通过本类的锁保护。
/// </summary>
internal sealed class AmbientBarkStateStore
{
    /// <summary>
    /// 单个 NPC 的 Bark 状态。纯数据，不含游戏对象引用。
    /// </summary>
    internal sealed class State
    {
        public Queue<string> BarkQueue { get; } = new Queue<string>();
        public CancellationTokenSource BackgroundCts { get; private set; } = new CancellationTokenSource();
        public int RequestId { get; private set; }
        public int LifeTicks { get; set; }
        public int CooldownTicks { get; set; }
        public int DisplayCountdown { get; set; }
        public bool IsRequesting { get; set; }
        public bool HasPlayedFirst { get; set; } = false;
        public List<string> RecentBarks { get; } = new List<string>();

        private const int MAX_RECENT = 10;

        public void AddRecentBark(string bark)
        {
            if (string.IsNullOrWhiteSpace(bark)) return;
            RecentBarks.Add(bark);
            if (RecentBarks.Count > MAX_RECENT)
                RecentBarks.RemoveAt(0);
        }

        public void ReplaceCts()
        {
            try
            {
                BackgroundCts?.Cancel();
            }
            catch
            {
                // ignored
            }

            // Note: Old CTS is intentionally not disposed here.
            // Old tokens may still be referenced by background tasks.
            BackgroundCts = new CancellationTokenSource();
            RequestId++;
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
        return _states.GetOrAdd(
            npcName,
            _ => new State());
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
        _states.Clear();
    }

    /// <summary>
    /// 安全重置指定 NPC 的状态。
    /// </summary>
    internal void HardReset(string npcName)
    {
        if (!_states.TryGetValue(npcName, out var state))
            return;

        lock (state)
        {
            state.ReplaceCts();
            state.BarkQueue.Clear();
            state.IsRequesting = false;
            state.LifeTicks = 0;
            state.CooldownTicks = 0;
            state.HasPlayedFirst = false;
        }

        ModEntry.SMonitor?.Log($"[AmbientBark] Hard reset for {npcName}.", StardewModdingAPI.LogLevel.Debug);
    }
}
