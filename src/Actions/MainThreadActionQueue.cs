using System;
using System.Collections.Concurrent;
using StardewModdingAPI;

namespace ValleytalkReborn;

/// <summary>
/// 原生工具调用已全量移除（VT-NOTOOLS-T1）；
/// 本类仅保留主线程队列基础设施（由 Timeline / Bio 编辑器等非工具调用方使用）。
/// </summary>
internal static class MainThreadActionQueue
{
    private static readonly ConcurrentQueue<Action> _mainThreadActions = new();

    /// <summary>
    /// 必须在游戏主线程循环中调用。
    /// </summary>
    public static void ProcessMainThreadQueue()
    {
        while (_mainThreadActions.TryDequeue(out var action))
        {
            try
            {
                action?.Invoke();
            }
            catch (Exception ex)
            {
                LogError($"[MainThreadActionQueue] Queue execution error: {ex}");
            }
        }
    }

    /// <summary>线程安全投递一个主线程回调；由 ModEntry.OnUpdateTicked 的 ProcessMainThreadQueue 消费。</summary>
    public static void EnqueueMainThread(Action action)
    {
        if (action == null) return;
        _mainThreadActions.Enqueue(action);
    }

    private static void LogError(string message)
    {
        ModEntry.SMonitor?.Log(message, LogLevel.Error);
    }
}
