using System;
using System.Collections.Generic;
using System.Text;

namespace ValleytalkReborn;

/// <summary>
/// Stateless, single-channel logger for LLM traffic that lacks its own module-level logging
/// (memory extraction / nightly consolidation).
///
/// ① Reconstruction rule: the real user message = gameCache + npcCache + userPrompt;
///    when responseStart is non-empty, "\n\n" + responseStart is appended afterward.
/// ② Whitelist semantics: only the NO_TOOLS family is recorded here; the main dialogue,
///    Bark, and A2A are each responsible for their own logging and must not appear here.
/// ③ Privacy notice: logs contain full conversation text and player profiles — do NOT
///    share them publicly.
///
/// No state, no mutable statics; thread-safe (reads a config scalar + SMAPI Monitor).
/// Callable from any thread.
/// </summary>
internal static class LlmTrafficLogger
{
    // Gate: this channel only records traffic that has no module-level log of its own
    // (memory extraction / nightly consolidation). To extend: change ONLY this comparison,
    // never the call sites.
    private static bool ShouldLog(string cacheContext) =>
        ModEntry.Config?.Debug == true
        && string.Equals(cacheContext, LlmContextTypes.NoTools, StringComparison.Ordinal);

    public static void LogOutgoing(string cacheContext, string model, string endpoint,
        string systemPrompt, string gameCache, string npcCache, string userPrompt, string responseStart)
    {
        if (!ShouldLog(cacheContext)) return;

        var sb = new StringBuilder();
        sb.AppendLine("[LlmTraffic] ===== OUT >>>");
        sb.AppendLine($"context={cacheContext} | model={model} | endpoint={endpoint}");
        sb.AppendLine($"[system]\n{(systemPrompt ?? "(empty)")}");
        sb.AppendLine($"[gameCache]\n{(gameCache ?? "(empty)")}");
        sb.AppendLine($"[npcCache]\n{(npcCache ?? "(empty)")}");
        sb.AppendLine($"[userPrompt]\n{(userPrompt ?? "(empty)")}");
        sb.Append($"[responseStart]\n{(responseStart ?? "(empty)")}");
        ModEntry.SMonitor?.Log(sb.ToString(), StardewModdingAPI.LogLevel.Debug);
        ModEntry.SMonitor?.Log("[LlmTraffic] ===== OUT END", StardewModdingAPI.LogLevel.Debug);
    }

    public static void LogIncoming(string cacheContext, string rawText)
    {
        if (!ShouldLog(cacheContext)) return;

        var sb = new StringBuilder();
        sb.AppendLine("[LlmTraffic] ===== IN <<<");
        sb.Append($"context={cacheContext} | chars={rawText?.Length ?? 0}");
        ModEntry.SMonitor?.Log(sb.ToString(), StardewModdingAPI.LogLevel.Debug);
        ModEntry.SMonitor?.Log($"{{{(rawText ?? "(empty)")}}}", StardewModdingAPI.LogLevel.Debug);
        ModEntry.SMonitor?.Log("[LlmTraffic] ===== IN END", StardewModdingAPI.LogLevel.Debug);
    }

    public static void LogIncomingToolCalls(string cacheContext, List<ToolCallData> toolCalls)
    {
        if (!ShouldLog(cacheContext) || toolCalls == null || toolCalls.Count == 0) return;

        var sb = new StringBuilder();
        sb.AppendLine("[LlmTraffic] ===== IN(tool_calls) <<<");
        sb.Append($"context={cacheContext} | count={toolCalls.Count}");
        ModEntry.SMonitor?.Log(sb.ToString(), StardewModdingAPI.LogLevel.Debug);
        for (int i = 0; i < toolCalls.Count; i++)
        {
            var tc = toolCalls[i];
            ModEntry.SMonitor?.Log($"  [{i}] {tc.FunctionName} args={tc.JsonArguments}", StardewModdingAPI.LogLevel.Debug);
        }
        ModEntry.SMonitor?.Log("[LlmTraffic] ===== IN(tool_calls) END", StardewModdingAPI.LogLevel.Debug);
    }
}
