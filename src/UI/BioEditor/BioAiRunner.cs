using System;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using StardewModdingAPI;
using StardewValley;

namespace ValleytalkReborn;

internal enum BioAiResultKind
{
    Success,
    Failure,
    Cancelled,
    TimedOut
}

internal readonly struct BioAiResult
{
    public BioAiResultKind Kind { get; }
    public string Text { get; }
    public string Detail { get; }

    public BioAiResult(BioAiResultKind kind, string text = "", string detail = "")
    {
        Kind = kind;
        Text = text;
        Detail = detail;
    }
}

internal static class BioAiRunner
{
    private static CancellationTokenSource _activeCts;
    private static volatile bool _cancelRequestedByUser;
    private static int _generation;

    public static bool IsBusy => _activeCts != null;

    public static void ExecuteStreaming(
        string systemPrompt,
        string userPrompt,
        bool enableThinking,
        ConcurrentQueue<string> tokenSink,
        Action<BioAiResult> onResult)
    {
        if (IsBusy)
        {
            Game1.playSound("cancel");
            Game1.addHUDMessage(new HUDMessage("AI 正在构思中，请稍候", HUDMessage.error_type));
            return;
        }

        _cancelRequestedByUser = false;
        _activeCts = new CancellationTokenSource();
        int gen = ++_generation;
        CancellationTokenSource cts = _activeCts;
        string context = enableThinking
            ? $"{LlmContextTypes.Editor}_Think"
            : $"{LlmContextTypes.Editor}_Fast";

        Game1.playSound("wand");
        Game1.addHUDMessage(new HUDMessage("AI 正在构思方案…", HUDMessage.newQuest_type));

        _ = Task.Run(async () =>
        {
            BioAiResult result;

            try
            {
                LlmResponse response = await Llm.Instance.RunStreamingInference(
                    systemPrompt,
                    "",
                    "",
                    userPrompt,
                    token => tokenSink?.Enqueue(token),
                    cts.Token,
                    "",
                    2048,
                    context).ConfigureAwait(false);

                if (cts.IsCancellationRequested)
                {
                    result = _cancelRequestedByUser
                        ? new BioAiResult(BioAiResultKind.Cancelled)
                        : new BioAiResult(BioAiResultKind.TimedOut, detail: "请求超时或已中断");
                }
                else if (response.IsSuccess
                         && !string.IsNullOrWhiteSpace(response.Text)
                         && response.ToolCalls.Count == 0)
                {
                    result = new BioAiResult(BioAiResultKind.Success, CleanAiResponse(response.Text));
                }
                else
                {
                    string detail = string.IsNullOrWhiteSpace(response.ErrorMessage)
                        ? $"HTTP {response.ResponseCode}"
                        : response.ErrorMessage;
                    result = new BioAiResult(BioAiResultKind.Failure, detail: detail);
                }
            }
            catch (Exception ex)
            {
                ModEntry.SMonitor?.Log($"[BioAiRunner] Streaming inference failed: {ex}", LogLevel.Error);
                result = new BioAiResult(BioAiResultKind.Failure, detail: ex.Message);
            }

            AgentToolDispatcher.EnqueueMainThread(() =>
            {
                if (gen != _generation)
                    return;

                _activeCts = null;

                switch (result.Kind)
                {
                    case BioAiResultKind.Success:
                        break;

                    case BioAiResultKind.Failure:
                        Game1.playSound("cancel");
                        string detail = result.Detail.Length > 80 ? result.Detail[..80] : result.Detail;
                        Game1.addHUDMessage(new HUDMessage($"AI 生成失败：{detail}", HUDMessage.error_type));
                        break;

                    case BioAiResultKind.Cancelled:
                        break;

                    case BioAiResultKind.TimedOut:
                        Game1.playSound("cancel");
                        Game1.addHUDMessage(new HUDMessage(
                            "AI 请求超时，请检查网络或调大 QueryTimeout",
                            HUDMessage.error_type));
                        break;
                }

                if (onResult == null)
                    return;

                try
                {
                    onResult(result);
                }
                catch (Exception ex)
                {
                    ModEntry.SMonitor?.Log($"[BioAiRunner] Result callback failed: {ex}", LogLevel.Error);
                }
            });
        });
    }

    public static void CancelCurrentTask()
    {
        CancellationTokenSource cts = _activeCts;
        if (cts == null || cts.IsCancellationRequested)
            return;

        _cancelRequestedByUser = true;
        _activeCts = null;
        cts.Cancel();
        Game1.playSound("cancel");
        Game1.addHUDMessage(new HUDMessage("已取消 AI 请求", HUDMessage.error_type));
    }

    private static string CleanAiResponse(string raw)
    {
        string cleaned = raw.Trim();
        if (!cleaned.StartsWith("```", StringComparison.Ordinal))
            return cleaned;

        cleaned = Regex.Replace(cleaned, "^```[\\w-]*\\s*", string.Empty);
        if (cleaned.EndsWith("```", StringComparison.Ordinal))
            cleaned = cleaned[..^3];

        return cleaned.Trim();
    }
}
