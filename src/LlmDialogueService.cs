using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using StardewModdingAPI;
using StardewValley;

namespace ValleytalkReborn;

/// <summary>
/// Service responsible for AI dialogue generation (prompt assembly, LLM inference, retry, output parsing).
/// Extracted from Character.cs to enforce Single Responsibility Principle.
/// </summary>
public class LlmDialogueService
{
    public static LlmDialogueService Instance { get; } = new LlmDialogueService();

    // 重试与超时常量
    /// <summary>
    /// Indicates whether an LLM inference request is currently in progress.
    /// Used by NightlyConsolidationHook to wait for pending requests before packing events.
    /// </summary>
    public bool IsRequestInProgress { get; private set; } = false;

    private const int MAX_RETRY_ATTEMPTS = 4;
    private const int MAX_TIMEOUT_SECONDS = 120;
    private const int RETRY_DELAY_SECONDS = 5;

    private LlmDialogueService()
    {
    }

    /// <summary>
    /// Generates AI dialogue for the given character using the provided context.
    /// Handles prompt assembly, LLM inference with timeout/retry, and output parsing.
    /// </summary>
    public async Task<string[]> GenerateDialogueAsync(Character character, DialogueContext context)
    {
        string[] results = Array.Empty<string>();
        IsRequestInProgress = true;
        Prompts prompts = null;

        try
        {
            // Reset cancellation flag for each new dialogue to prevent stale state
            character.IsUserCancelled = false;

            // Register this character as the active dialogue character for cancel button plugin
            ModEntry.CancelButtonPluginInstance?.SetActiveCharacter(character);

            // Wrap prompt creation in try-catch to prevent packaging failures from crashing
            try
            {
            prompts = new Prompts(context, character);

            // S1: Tab1 NPC 个人记忆（最高优先级，前置）
            var memoryCtx = MemoryManager.Instance.GetSmartMemoryContext(character.Name);
            if (!string.IsNullOrEmpty(memoryCtx))
                prompts.SystemPrompt = memoryCtx + "\n\n" + prompts.SystemPrompt;

            // S2: Tab2 大世界记忆（紧跟 Tab1）
            WorldMemoryManager.Instance.EnsureLoaded();
            var worldMemCtx = WorldMemoryManager.Instance.GetPromptText();
            if (!string.IsNullOrEmpty(worldMemCtx))
                prompts.SystemPrompt += "\n\n" + worldMemCtx;

            // S3: 感知层（Town Gossip + Immediate Observations）
            PerceptionInjector.Inject(character.Name, prompts);

            // S5: EvolvedTraits（NPC 对农夫的长期印象）
            var evolvedBlock = EvolvedTraitManager.GetPromptBlock(character.Name);
            if (!string.IsNullOrEmpty(evolvedBlock))
                prompts.SystemPrompt += "\n\n" + evolvedBlock;

        }
        catch (Exception ex)
        {
            Log.Error(ex, $"[ValleyTalk] Prompt packaging failed (Prompts.cs error): {character.Name}");
            ModEntry.SMonitor.Log($"Prompts Error StackTrace: {ex}", StardewModdingAPI.LogLevel.Error);
            return new string[] { "..." };
        }

        int timeoutSeconds = ModEntry.Config.QueryTimeout;
        Exception lastException = null;
        LlmResponse result = null;

        for (int attempt = 0; attempt <= MAX_RETRY_ATTEMPTS; attempt++)
        {
            try
            {
                // Apply delay before retry (no delay for first or second attempt)
                if (attempt >= 2)
                {
                    await Task.Delay(TimeSpan.FromSeconds(RETRY_DELAY_SECONDS));
                    // Double the timeout but cap at MAX_TIMEOUT_SECONDS
                    timeoutSeconds = Math.Min(timeoutSeconds * 2, MAX_TIMEOUT_SECONDS);
                }

                // If user already cancelled, do not retry
                if (character.IsUserCancelled)
                {
                    results = Array.Empty<string>();
                    break;
                }

                // Execute with timeout
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
                character.CurrentDialogueCts = cts;

                string[] resultsInternal;

                try
                {
                    // Debug logging for request context (extracted method)
                    LogDebugRequest(character, prompts, attempt + 1);
                    
                    var inferenceTask = Llm.Instance.RunInference(
                        prompts.SystemPrompt,
                        $"{prompts.GameConstantContext}",
                        $"{prompts.NpcConstantContext}",
                        $"{prompts.CorePrompt}{prompts.Instructions}{prompts.Command}",  // Instructions 后移
                        prompts.ResponseStart
                    );

                    result = await inferenceTask.WaitAsync(cts.Token);

                    if (result.IsSuccess)
                    {
                        var toolCalls = result.ToolCalls;
                        if (toolCalls?.Count > 0)
                        {
                            var npc = character.StardewNpc;
                            foreach (var tool in toolCalls)
                            {
                                bool usedBubble = AgentToolDispatcher.DispatchToolCall(npc, tool.FunctionName, tool.JsonArguments);
                                if (usedBubble) result.UsedBubble = true;
                            }
                        }

                        // speak_in_bubble 与对话框互斥：气泡模式下跳过文本输出
                        if (result.UsedBubble)
                        {
                            resultsInternal = Array.Empty<string>();
                            results = Array.Empty<string>();
                            break;
                        }

                        var dialogueText = result.Text;
                        if (toolCalls?.Count > 0 && string.IsNullOrWhiteSpace(dialogueText))
                        {
                            dialogueText = "- ...";
                        }
                        // ← 加在这里，ProcessLines 之前，打印原始输出
                        if (ModEntry.Config.Debug && !string.IsNullOrWhiteSpace(dialogueText))
                        {
                            ModEntry.SMonitor.Log(
                                $"[ValleyTalk] [Raw API Response] {character.Name}:\n{dialogueText}",
                                LogLevel.Debug);
                        }
                        resultsInternal = ProcessLines(dialogueText, character, attempt > 2).ToArray();
                    }
                    else
                    {
                        resultsInternal = Array.Empty<string>();
                    }
                }
                catch (OperationCanceledException)
                {
                    // User-initiated cancellation, silent handling, no error logging
                    Log.Debug($"AI request cancelled for {character.Name}.");
                    resultsInternal = new string[] { "..." };
                    break; // Exit retry loop, no further retries
                }
                catch (Exception ex)
                {
                    Log.Error(ex, $"Error generating AI response for {character.StardewNpc.displayName}");
                    throw;
                }

                if (resultsInternal.Length > 0)
                {
                    results = resultsInternal;
                    DialogueHistoryManager.Instance.ConsumeEavesdropEntries(character.Name);
                    break; // Success, exit retry loop
                }

                Log.Warning("No valid response generated from AI model.");
                if (result != null && !string.IsNullOrWhiteSpace(result.ErrorMessage))
                {
                    Log.Warning($"API Error Message: {result.ErrorMessage}");
                }
                else if (result != null && !string.IsNullOrWhiteSpace(result.Text))
                {
                    Log.Warning($"API Response: {result.Text}");
                }

                // Additional debug logging for context (extracted)
                LogDebugContext(character, context, prompts, resultsInternal);
            }
            catch (Exception ex)
            {
                lastException = ex;

                // If this is the last attempt, don't continue
                if (attempt == MAX_RETRY_ATTEMPTS)
                {
                    break;
                }
            }
        }

        // Handle final result
        if (results.Length == 0 && lastException != null)
        {
            ModEntry.SMonitor.Log($"Error generating AI response for {character.Name}: {lastException}",
                StardewModdingAPI.LogLevel.Error);
            results = new string[] { "..." };
        }

        if (!string.IsNullOrWhiteSpace(prompts?.GiveGift) && results.Length > 0)
        {
            results[0] += $"[{prompts.GiveGift}]";
        }

        character.CurrentDialogueCts = null;
        ModEntry.CancelButtonPluginInstance?.SetActiveCharacter(null);
        return results;
        }
        finally
        {
            IsRequestInProgress = false;
        }
    }

    /// <summary>
    /// Parses the raw LLM output string into dialogue lines and response option lines.
    /// Lines starting with '-' are treated as dialogue content.
    /// Lines starting with '%' are treated as response options.
    /// </summary>
    /// <param name="resultString">Raw LLM output.</param>
    /// <param name="character">Target character (used for portrait validation).</param>
    /// <param name="relaxedValidation">When true, allows non‑standard dialogue formats (used for retry scenarios).</param>
private IEnumerable<string> ProcessLines(string resultString, Character character, bool relaxedValidation = false)
{
    try
    {
        // ── Step 1: Split lines ──
        var resultLines = resultString.Split('\n')
            .Select(x => x.Replace("\r", "").Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToList();

        // ── Step 2: Separate dialogue lines from response option lines ──
        var rawDialogueLines = resultLines.Where(x => x.StartsWith("-")).ToList();
        var rawResponseLines = resultLines.Where(x => x.StartsWith("%")).ToList();

        // If no '-' lines at all, fall back to non-'%' lines
        if (rawDialogueLines.Count == 0)
            rawDialogueLines = resultLines.Where(x => !x.StartsWith("%")).ToList();

        if (rawDialogueLines.Count == 0)
            return Array.Empty<string>();

        // ── Step 3: Strip the leading '-' marker from each line, then JOIN into one
        //string BEFORE cleaning. This preserves $h/$b/$0 structure that
        //           the LLM placed within a single logical reply. ──
        var strippedParts = rawDialogueLines
            .Select(line => line.TrimStart('-', ' '))   // only strip the '-' prefix
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToList();

        // Join with a single space — the LLM already embeds #$b# / $b where it wants
        // page breaks, so we must NOT insert extra #$b# between lines here.
        string joined = string.Join(" ", strippedParts);

        // ── Step 4: Run the full cleanup pipeline ONCE on the joined string ──
        string cleaned = DialogueCleaner.CommonCleanup(joined);
        cleaned = DialogueCleaner.DialogueLineCleanup(
            cleaned, character.ValidPortraits, ModEntry.FixPunctuation, relaxedValidation);

        if (string.IsNullOrWhiteSpace(cleaned))
            return Array.Empty<string>();

        // Debug: log if we merged multiple lines
        if (strippedParts.Count > 1 && ModEntry.Config.Debug)
            Log.Debug($"Merged {strippedParts.Count} '-' lines into one dialogue for {character.Name}.");

        // ── Step 5: Response options ──
        var responseLines = rawResponseLines
            .Select(x => DialogueCleaner.CommonCleanup(x))
            .Select(x => DialogueCleaner.ResponseLineCleanup(x, ModEntry.FixPunctuation))
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToList();

        if (responseLines.Count < 2)
            responseLines.Clear();

        var finalResult = new List<string> { cleaned };
        finalResult.AddRange(responseLines);
        return finalResult;
    }
    catch (Exception ex)
    {
        Log.Error($"ProcessLines exception: {ex.Message}\n{ex.StackTrace}");
        return Array.Empty<string>();
    }
}

    /// <summary>
    /// Logs the full request context (System, GameConstant, NpcConstant, CorePrompt, etc.) in a formatted box.
    /// </summary>
    private void LogDebugRequest(Character character, Prompts prompts, int attemptNumber)
    {
        if (!ModEntry.Config.Debug) return;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"╔═══════════════════════════════════════════════════════════════════");
        sb.AppendLine($"║ [AI Request Context] {character.Name} (Attempt {attemptNumber})");
        sb.AppendLine($"╠═══════════════════════════════════════════════════════════════════");
        sb.AppendLine($"║ 【System】");
        foreach (var line in prompts.SystemPrompt.Split('\n'))
        {
            sb.AppendLine($"║   {line.TrimEnd()}");
        }
        sb.AppendLine($"║");
        sb.AppendLine($"║ 【GameConstantContext】");
        foreach (var line in prompts.GameConstantContext.Split('\n'))
        {
            sb.AppendLine($"║   {line.TrimEnd()}");
        }
        sb.AppendLine($"║");
        sb.AppendLine($"║ 【NpcConstantContext】");
        foreach (var line in prompts.NpcConstantContext.Split('\n'))
        {
            sb.AppendLine($"║   {line.TrimEnd()}");
        }
        sb.AppendLine($"║");
        sb.AppendLine($"║ 【Instructions】"); // 🌟【日志渲染前置】：与推理顺序保持一致
        foreach (var line in prompts.Instructions.Split('\n'))
        {
            sb.AppendLine($"║   {line.TrimEnd()}");
        }
        sb.AppendLine($"║");
        sb.AppendLine($"║ 【CorePrompt】");
        foreach (var line in prompts.CorePrompt.Split('\n'))
        {
            sb.AppendLine($"║   {line.TrimEnd()}");
        }
        sb.AppendLine($"║");
        sb.AppendLine($"║ 【Command】");
        foreach (var line in prompts.Command.Split('\n'))
        {
            sb.AppendLine($"║   {line.TrimEnd()}");
        }
        sb.AppendLine($"║");
        sb.AppendLine($"║ 【ResponseStart】");
        foreach (var line in prompts.ResponseStart.Split('\n'))
        {
            sb.AppendLine($"║   {line.TrimEnd()}");
        }
        sb.AppendLine($"╚═══════════════════════════════════════════════════════════════════");
        Log.Debug(sb.ToString());
    }

    /// <summary>
    /// Logs the game context (location, weather, time, etc.) and the final processed results.
    /// </summary>
    private void LogDebugContext(Character character, DialogueContext context, Prompts prompts, string[] resultsInternal)
    {
        if (!ModEntry.Config.Debug) return;

        Log.Debug($"Context:");
        Log.Debug($"-------------------");
        Log.Debug($"Name: {character.Name}");
        Log.Debug($"Marriage: {context.Married}");
        Log.Debug($"Birthday: {context.Birthday}");
        Log.Debug($"Location: {context.Location}");
        Log.Debug($"Weather: {string.Concat(context.Weather)}");
        Log.Debug($"Time of Day: {context.TimeOfDay}");
        Log.Debug($"Day of Season: {context.DayOfSeason}");
        Log.Debug($"Gift: {context.Accept}");
        Log.Debug($"Spouse Action: {context.SpouseAct}");
        Log.Debug($"Random Action: {context.RandomAct}");
        if (!string.IsNullOrEmpty(context.ScheduleLine))
        {
            Log.Debug($"Original Line: {context.ScheduleLine}");
        }
        Log.Debug($"-------------------");
        Log.Debug($"System Prompt: {prompts.SystemPrompt}");
        Log.Debug($"Game Constant Context: {prompts.GameConstantContext}");
        Log.Debug($"NPC Constant Context: {prompts.NpcConstantContext}");
        Log.Debug($"Instructions: {prompts.Instructions}");
        Log.Debug($"Core Prompt: {prompts.CorePrompt}");
        Log.Debug($"Command: {prompts.Command}");
        Log.Debug($"Response Start: {prompts.ResponseStart}");
        Log.Debug($"-------------------");
        if (resultsInternal.Length > 0)
        {
            Log.Debug($"Results: {resultsInternal[0]}");
            if (resultsInternal.Length > 1)
            {
                foreach (var resultLine in resultsInternal.Skip(1))
                {
                    Log.Debug($"Response: {resultLine}");
                }
            }
        }
        else
        {
            Log.Debug("Results: (empty)");
        }
        Log.Debug("--------------------------------------------------");
    }
}