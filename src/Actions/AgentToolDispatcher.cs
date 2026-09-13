using System;
using System.Collections.Concurrent;
using StardewModdingAPI;
using StardewValley;

namespace ValleytalkReborn;

/// <summary>
/// 原生工具调用已全量移除（VT-NOTOOLS-T1）；
/// 本类仅保留主线程队列基础设施与拒绝桩。
/// </summary>
internal static class AgentToolDispatcher
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
                LogError($"[AgentToolDispatcher] Queue execution error: {ex}");
            }
        }
    }

    public static bool DispatchToolCall(NPC npc, string functionName, string jsonArguments)
    {
        ModEntry.SMonitor?.Log($"[AgentToolDispatcher] Tool calling has been fully removed; rejected: '{functionName}'.", LogLevel.Debug);
        return false;
    }

    private static void LogDebug(string message)
    {
        ModEntry.SMonitor?.Log(message, LogLevel.Debug);
    }

    private static void LogWarn(string message)
    {
        ModEntry.SMonitor?.Log(message, LogLevel.Warn);
    }

    private static void LogError(string message)
    {
        ModEntry.SMonitor?.Log(message, LogLevel.Error);
    }
}
