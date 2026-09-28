using System;
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
    /// <summary>
    /// LOCAL-006：无门控日志点的正文脱敏截断。超过 max 的部分丢弃，并追加真实长度后缀便于定位。
    /// null / 空返回空串，不抛异常。
    /// 豁免清单见文件头隐私声明与 LlmDialogueService 的 Config.Debug 门控点（LOCAL-006 决策：均不改）。
    /// </summary>
    internal static string TruncateForLog(string body, int max = 200)
    {
        if (string.IsNullOrEmpty(body) || max <= 0)
        {
            return string.Empty;
        }

        return body.Length <= max
            ? body
            : body.Substring(0, max) + $"…(len={body.Length})";
    }

    // Gate: this channel only records traffic that has no module-level log of its own
    // (memory extraction / nightly consolidation). To extend: change ONLY this comparison,
    // never the call sites.
    private static bool ShouldLog(string cacheContext) =>
        ModEntry.Config?.Debug == true
        && string.Equals(cacheContext, LlmContextTypes.NoTools, StringComparison.Ordinal);

    // LOCAL-006 决策：本通道保持原样 —— 已由 Config.Debug + NO_TOOLS 白名单双重门控，改成截断会破坏
    // 记忆抽取 / 夜间整理的完整对话重建能力（重建规则见文件头 ①）。
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

    /// <summary>
    /// LOCAL-005：单次 LLM 请求的遥测快照（进程内 Memory 级，随请求生命周期存在，不持久化）。
    /// 只承载可安全落盘的标量字段：provider / host / model / context / attempt /
    /// HTTP status / 排队耗时 / TTFT / 总耗时 / 输出字符数。
    /// 严禁写入 API Key、Authorization Header、完整 Prompt 或完整响应正文。
    /// </summary>
    internal sealed class LlmRequestTelemetry
    {
        internal string Provider { get; }
        internal string Host { get; }
        internal string Model { get; }
        internal string Context { get; }
        internal int Attempt { get; set; } = 1;
        internal int StatusCode { get; set; }
        internal long QueueWaitMs { get; set; }
        internal long TtftMs { get; set; } = -1;
        internal long TotalMs { get; set; }
        internal int OutputChars { get; set; }

        internal LlmRequestTelemetry(string provider, string host, string model, string context)
        {
            Provider = string.IsNullOrWhiteSpace(provider) ? "unknown" : provider;
            Host = SafeHost(host);
            Model = string.IsNullOrWhiteSpace(model) ? "unknown" : model;
            Context = string.IsNullOrWhiteSpace(context) ? "(none)" : context;
        }

        /// <summary>
        /// 输出单行遥测日志。字段生成失败时只记 Error，绝不回退到打印原始请求/响应。
        /// </summary>
        internal void Log()
        {
            try
            {
                ModEntry.SMonitor?.Log(
                    "[LlmTraffic] REQUEST"
                    + $" provider={Provider}"
                    + $" | host={Host}"
                    + $" | model={Model}"
                    + $" | context={Context}"
                    + $" | attempt={Attempt}"
                    + $" | status={StatusCode}"
                    + $" | queueWaitMs={QueueWaitMs}"
                    + $" | ttftMs={(TtftMs >= 0 ? TtftMs.ToString() + "ms" : "n/a")}"
                    + $" | totalMs={TotalMs}"
                    + $" | chars={OutputChars}",
                    StardewModdingAPI.LogLevel.Debug);
            }
            catch (Exception ex)
            {
                ModEntry.SMonitor?.Log(
                    $"[LlmTraffic] Request telemetry generation failed: {ex.GetType().Name}. Request body and headers are never logged.",
                    StardewModdingAPI.LogLevel.Error);
            }
        }

        /// <summary>纯字符串/URI 解析取 Host；解析失败返回 unknown，不抛异常。</summary>
        private static string SafeHost(string url)
        {
            if (string.IsNullOrWhiteSpace(url)) return "unknown";

            return Uri.TryCreate(UrlHelper.EnsureScheme(url.Trim()), UriKind.Absolute, out Uri uri)
                ? uri.Host
                : "unknown";
        }
    }

    // LOCAL-006 决策：同上，维持全量。与 LlmDialogueService 的 [Raw API Response]（Config.Debug 门控）一并豁免。
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
}
