using System;
using System.Collections.Concurrent;
using System.Threading.Tasks;
using StardewModdingAPI;
using StardewModdingAPI.Events;

namespace ValleytalkReborn;

internal static class MainThreadDispatcher
{
    private sealed class WorkItem
    {
        public Action Action { get; }
        public TaskCompletionSource<bool> Completion { get; }

        public WorkItem(Action action)
        {
            Action = action;
            Completion = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
        }
    }

    private static readonly ConcurrentQueue<WorkItem> Queue = new();

    private static IMonitor Monitor =>
        ModEntry.SMonitor;

    public static void Register(IModHelper helper)
    {
        helper.Events.GameLoop.UpdateTicked += OnUpdateTicked;

        Monitor?.Log(
            "[MainThreadDispatcher] Registered.",
            LogLevel.Debug);
    }

    public static Task RunOnMainThreadAsync(Action action)
    {
        if (action == null)
            return Task.CompletedTask;

        var workItem = new WorkItem(action);
        Queue.Enqueue(workItem);

        Monitor?.Log(
            $"[MainThreadDispatcher] Action queued. Pending: {Queue.Count}.",
            LogLevel.Trace);

        return workItem.Completion.Task;
    }

    private static void OnUpdateTicked(object sender, UpdateTickedEventArgs e)
    {
        const int maxActionsPerTick = 32;
        int processed = 0;

        while (processed < maxActionsPerTick &&
               Queue.TryDequeue(out var workItem))
        {
            processed++;

            try
            {
                workItem.Action();
                workItem.Completion.TrySetResult(true);
            }
            catch (Exception ex)
            {
                Monitor?.Log(
                    $"[MainThreadDispatcher] Main-thread action failed: {ex}",
                    LogLevel.Error);

                workItem.Completion.TrySetException(ex);
            }
        }

        if (processed > 0)
        {
            Monitor?.Log(
                $"[MainThreadDispatcher] Executed {processed} action(s). Remaining: {Queue.Count}.",
                LogLevel.Trace);
        }
    }
}