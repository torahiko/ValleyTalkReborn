using System;
using System.Collections.Concurrent;
using Newtonsoft.Json.Linq;
using StardewModdingAPI;
using StardewValley;

namespace ValleytalkReborn;

/// <summary>
/// 静态工具分发器：将 Native Function Calling 返回的结构化工具调用映射到游戏行为。
///
/// 约定：
/// true  = 工具调用已被识别，并已进入主线程执行队列/延迟队列。
/// false = 参数无效、未知工具、当前不可执行或入队前校验失败。
///
/// 注意：
/// ProcessMainThreadQueue 应在 SMAPI 主线程循环中调用，例如 GameLoop.UpdateTicked。
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
        if (npc == null)
        {
            LogWarn("[AgentToolDispatcher] Tool call rejected: npc is null.");
            return false;
        }

        if (string.IsNullOrWhiteSpace(functionName))
        {
            LogWarn("[AgentToolDispatcher] Tool call rejected: functionName is empty.");
            return false;
        }

        if (!Context.IsWorldReady)
        {
            LogWarn($"[AgentToolDispatcher] Tool call ignored because world is not ready: '{functionName}'.");
            return false;
        }

        functionName = functionName.Trim();
        var args = ParseArguments(functionName, jsonArguments);

        try
        {
            if (string.Equals(functionName, AgentToolDefinitions.ToolScheduleDate, StringComparison.OrdinalIgnoreCase))
            {
                return ScheduleDate(npc, args);
            }

            if (string.Equals(functionName, AgentToolDefinitions.ToolEndDate, StringComparison.OrdinalIgnoreCase))
            {
                return EndDate(npc, args);
            }

            if (string.Equals(functionName, AgentToolDefinitions.ToolPhysicalAction, StringComparison.OrdinalIgnoreCase))
            {
                return TriggerPhysicalAction(npc, args);
            }

            if (string.Equals(functionName, AgentToolDefinitions.ToolSpeakInBubble, StringComparison.OrdinalIgnoreCase))
            {
                return SpeakInBubble(npc, args);
            }

            LogWarn($"[AgentToolDispatcher] Unknown tool function: '{functionName}'.");
            return false;
        }
        catch (Exception ex)
        {
            LogError($"[AgentToolDispatcher] Dispatch error for '{functionName}': {ex}");
            return false;
        }
    }

    private static bool ScheduleDate(NPC npc, JObject args)
    {
        var locationId = GetString(args, "location_id");
        if (string.IsNullOrWhiteSpace(locationId))
        {
            LogWarn("[AgentToolDispatcher] schedule_date: missing location_id.");
            return false;
        }

        RunDelayedOnMainThread(100, () =>
        {
            var target = ResolveNpc(npc);
            if (target == null)
            {
                LogWarn("[AgentToolDispatcher] schedule_date: target NPC is no longer valid.");
                return;
            }

            if (DateManager.Instance == null)
            {
                LogWarn("[AgentToolDispatcher] schedule_date: DateManager.Instance is null.");
                return;
            }

            bool ok = DateManager.Instance.TryScheduleDate(target, locationId);
            if (ok)
            {
                LogInfo($"[AgentToolDispatcher] Date scheduled: {target.Name} → {locationId}");
            }
            else
            {
                LogWarn($"[AgentToolDispatcher] Date scheduling failed: {target.Name} → {locationId}");
            }
        });

        return true;
    }

    private static bool EndDate(NPC npc, JObject args)
    {
        var reason = GetString(args, "reason");
        if (string.IsNullOrWhiteSpace(reason))
        {
            reason = "LLM_Decision";
        }

        RunDelayedOnMainThread(300, () =>
        {
            var target = ResolveNpc(npc);
            if (target == null)
            {
                LogWarn("[AgentToolDispatcher] end_current_date: target NPC is no longer valid.");
                return;
            }

            if (DateManager.Instance == null)
            {
                LogWarn("[AgentToolDispatcher] end_current_date: DateManager.Instance is null.");
                return;
            }

            DateManager.Instance.EndDateGracefully(target.Name, reason);
            LogInfo($"[AgentToolDispatcher] Date ended gracefully: {target.Name} (reason: {reason})");
        });

        return true;
    }

    private static bool TriggerPhysicalAction(NPC npc, JObject args)
    {
        var actionType = GetString(args, "action_type");
        if (string.IsNullOrWhiteSpace(actionType))
        {
            LogWarn("[AgentToolDispatcher] trigger_physical_action: missing action_type.");
            return false;
        }

                var tag = ActionTagExtensions.FromTagString(actionType);
        if (tag == ActionTag.None)
        {
            LogWarn($"[AgentToolDispatcher] trigger_physical_action: unknown action_type '{actionType}'.");
            return false;
        }

        RunOnMainThread(() =>
        {
            var target = ResolveNpc(npc);
            if (target == null)
            {
                LogWarn($"[AgentToolDispatcher] trigger_physical_action: target NPC is no longer valid for '{actionType}'.");
                return;
            }

            if (tag == ActionTag.Follow)
            {
                // ── 幂等性保护：如果已经在跟随，直接忽略重复调用 ──
                if (MovementManager.Instance != null &&
                    MovementManager.Instance.HasActiveFollow &&
                    MovementManager.Instance.CurrentFollowingNpc == target)
                {
                    LogDebug($"[AgentToolDispatcher] Follow ignored: {target.Name} is already following.");
                    return;
                }

                bool ok = false;
                if (DateManager.Instance != null)
                {
                    ok = DateManager.Instance.TryStartFollow(target);
                }

                if (ok)
                {
                    LogInfo($"[AgentToolDispatcher] Follow started via DateManager: {target.Name}");
                }
                else
                {
                    // Fallback: 如果不在约会状态或约会跟随失败，回退到普通的 MovementManager 跟随
                    if (MovementManager.Instance != null)
                    {
                        MovementManager.Instance.QueueMovement(target, ActionTag.Follow, skipDialogueWait: true);
                        LogInfo($"[AgentToolDispatcher] Follow fallback to MovementManager: {target.Name}");
                    }
                    else
                    {
                        LogWarn("[AgentToolDispatcher] trigger_physical_action (Follow): Both DateManager and MovementManager are null.");
                    }
                }
            }
            else if (tag == ActionTag.StayHome)
            {
                CompanionScheduleManager.Instance.SetStayHomeMode(target.Name);
                LogInfo($"[AgentToolDispatcher] StayHome set: {target.Name}");
            }
            else if (tag == ActionTag.AllDayFollow)
            {
                CompanionScheduleManager.Instance.SetAllDayFollow(target.Name);
                LogInfo($"[AgentToolDispatcher] AllDayFollow set: {target.Name}");
            }
            else
            {
                if (MovementManager.Instance == null)
                {
                    LogWarn("[AgentToolDispatcher] trigger_physical_action: MovementManager.Instance is null.");
                    return;
                }

                MovementManager.Instance.QueueMovement(target, tag, skipDialogueWait: true);
                LogDebug($"[AgentToolDispatcher] Physical action queued on main thread: {target.Name} → {tag}");
            }
        });

        return true;
    }

    private static bool SpeakInBubble(NPC npc, JObject args)
    {
        var text = GetString(args, "text");
        if (string.IsNullOrWhiteSpace(text))
        {
            LogWarn("[AgentToolDispatcher] speak_in_bubble: missing text.");
            return false;
        }

        // 气泡文本通常不适合带换行，统一压成空格
        text = text.Replace("\r", " ").Replace("\n", " ").Trim();

        int maxChars = AgentToolDefinitions.BubbleMaxChars;
        if (maxChars > 0 && text.Length > maxChars)
        {
            text = maxChars <= 3
                ? text.Substring(0, maxChars)
                : text.Substring(0, maxChars - 3) + "...";
        }

        var bubbleText = text;

        RunOnMainThread(() =>
        {
            var target = ResolveNpc(npc);
            if (target == null)
            {
                LogWarn("[AgentToolDispatcher] speak_in_bubble: target NPC is no longer valid.");
                return;
            }

            target.showTextAboveHead(bubbleText);
            LogDebug($"[AgentToolDispatcher] Bubble shown for {target.Name}: \"{bubbleText}\"");
        });

        return true;
    }

    private static JObject ParseArguments(string functionName, string jsonArguments)
    {
        if (string.IsNullOrWhiteSpace(jsonArguments))
        {
            return new JObject();
        }

        try
        {
            return JObject.Parse(jsonArguments);
        }
        catch (Exception ex)
        {
            LogWarn($"[AgentToolDispatcher] Failed to parse arguments for '{functionName}': {ex.Message}");
            return new JObject();
        }
    }

    private static void RunOnMainThread(Action action)
    {
        if (action == null)
        {
            return;
        }

        _mainThreadActions.Enqueue(() =>
        {
            try
            {
                if (!IsGameReadyForActions())
                {
                    LogWarn("[AgentToolDispatcher] Main-thread action skipped because world is not ready.");
                    return;
                }

                action();
            }
            catch (Exception ex)
            {
                LogError($"[AgentToolDispatcher] Main-thread action error: {ex}");
            }
        });
    }

    private static void RunDelayedOnMainThread(int delayMilliseconds, Action action)
    {
        if (action == null)
        {
            return;
        }

        _mainThreadActions.Enqueue(() =>
        {
            try
            {
                if (!IsGameReadyForActions())
                {
                    LogWarn("[AgentToolDispatcher] Delayed action skipped because world is not ready.");
                    return;
                }

                DelayedAction.functionAfterDelay(() =>
                {
                    try
                    {
                        if (!IsGameReadyForActions())
                        {
                            return;
                        }

                        action();
                    }
                    catch (Exception ex)
                    {
                        LogError($"[AgentToolDispatcher] Delayed action error: {ex}");
                    }
                }, Math.Max(0, delayMilliseconds));
            }
            catch (Exception ex)
            {
                LogError($"[AgentToolDispatcher] Failed to schedule delayed action: {ex}");
            }
        });
    }

    private static bool IsGameReadyForActions()
    {
        return Context.IsWorldReady && Game1.player != null;
    }

    /// <summary>
    /// 优先按名字重新解析 NPC，避免跨天后引用失效。
    /// 必须在主线程调用。
    /// </summary>
    private static NPC ResolveNpc(NPC original)
    {
        if (original == null)
        {
            return null;
        }

        try
        {
            var name = original.Name;
            if (!string.IsNullOrWhiteSpace(name))
            {
                var found = Game1.getCharacterFromName(name);
                if (found != null)
                {
                    return found;
                }
            }
        }
        catch
        {
            // 如果原引用状态异常，则退回原引用，由外层 try/catch 兜底。
        }

        return original;
    }

    private static string GetString(JObject obj, string key)
    {
        if (obj == null || string.IsNullOrWhiteSpace(key))
        {
            return string.Empty;
        }

        var token = obj[key];
        if (token == null)
        {
            return string.Empty;
        }

        string value;
        if (token.Type == JTokenType.String)
        {
            value = token.Value<string>();
        }
        else
        {
            value = token.ToString();
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return value.Trim();
    }

    private static void LogDebug(string message)
    {
        ModEntry.SMonitor?.Log(message, LogLevel.Debug);
    }

    private static void LogInfo(string message)
    {
        ModEntry.SMonitor?.Log(message, LogLevel.Info);
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