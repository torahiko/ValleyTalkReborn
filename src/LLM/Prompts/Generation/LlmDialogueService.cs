using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Text.RegularExpressions;
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
    private volatile bool _isRequestInProgress = false;
    public bool IsRequestInProgress => _isRequestInProgress;

    private const int MAX_RETRY_ATTEMPTS = 1;   // 对话场景最多重试 1 次（共 2 次请求）
    private const int MAX_TIMEOUT_SECONDS = 120;
    private const int RETRY_DELAY_SECONDS = 5;

    private LlmDialogueService() { }  

    /// <summary>
    /// Generates AI dialogue for the given character using the provided context.
    /// Handles prompt assembly, LLM inference with timeout/retry, and output parsing.
    /// </summary>
    public async Task<string[]> GenerateDialogueAsync(Character character, DialogueContext context, Action<string> onStreamingToken = null)
    {
        // 严格的最外层状态管理，确保无论是正常 return 还是异常 throw，都能正确重置状态
        _isRequestInProgress = true;
        try
        {
            string[] results = Array.Empty<string>();

            // Reset cancellation flag for each new dialogue to prevent stale state
            character.IsUserCancelled = false;
            ModEntry.CancelButtonPluginInstance?.SetActiveCharacter(character);

            Prompts prompts = null;
            try
            {
                PlayerStateScanner.Scan(character.Name);
                prompts = new Prompts(context, character);

                // ── SystemPrompt 注入顺序：静态在前，动态在后，最大化 cache 命中率 ──
                // 原则：cache 基于前缀匹配，变动越频繁的内容越靠后，
                //       避免高频变化的内容污染前面稳定内容的缓存前缀。
                //
                // [基础 systemPrompt] ← GetSystemPrompt() 已在属性初始化中，纯静态，保持最前

                // S1: NPC 个人记忆（变动频率：每次记忆摘要更新后，约数天一次）
                var memoryCtx = MemoryManager.Instance.GetSmartMemoryContext(character.Name);
                if (!string.IsNullOrEmpty(memoryCtx))
                    prompts.SystemPrompt += "\n\n" + memoryCtx;

                // S2: 大世界记忆（变动频率：类似，数天一次）
                WorldMemoryManager.Instance.EnsureLoaded();
                var worldMemCtx = WorldMemoryManager.Instance.GetPromptText();
                if (!string.IsNullOrEmpty(worldMemCtx))
                    prompts.SystemPrompt += "\n\n" + worldMemCtx;

                // S3: EvolvedTraits（按玩家当前输入实时重排，变动频率：每轮对话）
                // 不能进 SystemPrompt：该段是 LlmClaude.cs 中零缓存的首段，每轮必变内容
                // 放在这里会完全破坏前缀缓存。改为字段注入，由 GetCorePrompt() 内部读取。
                prompts.PendingEvolvedTraitsBlock = EvolvedTraitManager.GetPromptBlock(character.Name, context);

                // S4: 感知层（拆分处理）
                //   - gossip（小镇传闻，每天去重，中频变化）→ SystemPrompt，作为背景设定
                //   - local perceptions（gift/eat，每轮实时事件）→ CorePrompt，通过字段注入
                var gossipBlock = PerceptionInjector.BuildGossipBlock(character.Name);
                if (!string.IsNullOrEmpty(gossipBlock))
                    prompts.SystemPrompt += "\n\n" + gossipBlock;

                prompts.PendingLocalPerceptionBlock = PerceptionInjector.BuildLocalBlock(character.Name);

                // ══════════════════════════════════════════════════════════════════════
                // S4.5: 偷听短期上下文（仅预览，不消费）
                // ══════════════════════════════════════════════════════════════════════
                prompts.PendingEavesdropBlock = EavesdropInjector.BuildBlock(character.Name);

                // ══════════════════════════════════════════════════════════════════════
                // S5: 配偶深夜等待事件（仅探测是否存在，不消费）
                // ══════════════════════════════════════════════════════════════════════
                bool hasSpouseWaiting = SpouseWaitingEvent.HasPendingSpouseDialogue(character.Name);
                if (hasSpouseWaiting)
                {
                    string porchCtx = SpouseWaitingEvent.GetPorchContext();
                    if (!string.IsNullOrEmpty(porchCtx))
                    {
                        prompts.PendingSpouseWaitingBlock = SpouseWaitingEvent.BuildStatusPrompt(porchCtx);
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error(ex, $"[ValleyTalk] Prompt packaging failed (Prompts.cs error): {character.Name}");
                ModEntry.SMonitor.Log($"Prompts Error StackTrace: {ex}", StardewModdingAPI.LogLevel.Error);
                // 构造 Prompt 失败，直接返回占位符。外层 finally 会正确处理 IsRequestInProgress
                return new string[] { "..." };
            }

            // ══════════════════════════════════════════════════════════════════════
            // S4.6: 近期互动余韵（改为字段注入）
            // ══════════════════════════════════════════════════════════════════════
            // 注意：Echo 是延迟消费的一次性内容（如共进晚餐的余韵），
            //       必须在 Prompt 组装成功后、网络请求发出前执行，
            //       避免因 Prompt 组装失败或用户提前取消导致 Echo 被无声吞没。
            prompts.PendingEchoBlock = ImmediateEchoStore.BuildEchoBlock(
                character.Name,
                character.StardewNpc?.currentLocation?.Name);

            // S4.7: 关系里程碑与修罗场（结婚倒计时 / 离婚日 / 花舞节伴侣与吃醋）
            prompts.PendingMilestoneBlock = RelationshipMilestoneManager.Instance.BuildMilestoneBlock(character);

            // ── 终局出口去重：优先保留下方 CorePrompt 的即时条目，剔除上方 SystemPrompt 的冗余条目 ──
            PromptDeduplicator.DeduplicatePrompts(prompts);

            // ══════════════════════════════════════════════════
            //  流式路径
            // ══════════════════════════════════════════════════
            // 流式决策已上移至 DialogueBuilder：其根据当前回合是否需要工具调用以及
            // Provider 是否支持"流式 + 工具调用"（SupportsStreamingWithTools）决定
            // 是否传入回调。此处仅在有回调时走流式通道，否则回退到非流式推理。
            if (onStreamingToken != null)
            {
                var tracker = new StreamLineTracker();
                using var cts = new CancellationTokenSource(
                    TimeSpan.FromSeconds(ModEntry.Config.QueryTimeout));
                character.CurrentDialogueCts = cts;

                if (ModEntry.Config.Debug)
                    LogDebugRequest(character, prompts, attemptNumber: 1);

                LlmResponse streamResult = null;
                try
                {
                    streamResult = await Llm.Instance.RunStreamingInference(
                        prompts.SystemPrompt,
                        prompts.GameConstantContext,
                        prompts.NpcConstantContext,
                        $"{prompts.CorePrompt}{prompts.Instructions}{prompts.Command}",
                        delta =>
                        {
                            var displayText = tracker.Feed(delta);
                            if (displayText != null)
                                onStreamingToken(displayText);
                        },
                        cts.Token,
                        prompts.ResponseStart);
                }
                catch (OperationCanceledException)
                {
                    Log.Debug($"Streaming cancelled for {character.Name}.");
                    character.CurrentDialogueCts = null;
                    ModEntry.CancelButtonPluginInstance?.SetActiveCharacter(null);
                  
                    // ══════════════════════════════════════════════════════════════════════
                    // 🔧 [PERCEPTION CLEANUP] 即使取消，也要清理已注入的感知，避免重复
                    // ══════════════════════════════════════════════════════════════════════
                    // 业务逻辑：
                    // 1. Prompt 已经组装完成（包含送礼、吃东西等感知）
                    // 2. 这些感知已经"展示"给 LLM（即使 LLM 未返回完整响应）
                    // 3. 如果不清理，下次对话会重复注入，导致"你刚刚收到礼物"反复出现
                    // 4. 清理操作幂等且安全，不会影响正常流程
                    // ══════════════════════════════════════════════════════════════════════
                    try
                    {
                        ConfirmDynamicBlocksConsumed(context, character, prompts);
                        PerceptionManager.Instance?.MarkAsConsolidated(character.Name);
                        PerceptionManager.Instance?.Evict("Eat");
                    }
                    catch (Exception cleanupEx)
                    {
                        Log.Warning($"Perception cleanup failed after streaming cancellation: {cleanupEx.Message}");
                    }
                  
                    return new[] { "..." };
                }

                character.CurrentDialogueCts = null;
                ModEntry.CancelButtonPluginInstance?.SetActiveCharacter(null);

                if (streamResult == null || !streamResult.IsSuccess)
                    return new[] { "..." };

                var streamToolCalls = streamResult.ToolCalls;
                if (streamToolCalls?.Count > 0)
                {
                    var npc = character.StardewNpc;
                    foreach (var tool in streamToolCalls)
                    {
                        bool usedBubble = AgentToolDispatcher.DispatchToolCall(npc, tool.FunctionName, tool.JsonArguments);
                        if (usedBubble) streamResult.UsedBubble = true;
                    }
                }

                // speak_in_bubble 与对话框互斥：气泡模式下跳过文本输出（与非流式分支保持一致）
                if (streamResult.UsedBubble)
                {
                    ModEntry.SMonitor?.Log(
                        $"[LlmDialogueService] {character.Name} used speak_in_bubble, skipping dialogue box (streaming).",
                        LogLevel.Trace);
                    return null;
                }

                var streamDialogueText = streamResult.Text;
                if (streamToolCalls?.Count > 0 && string.IsNullOrWhiteSpace(streamDialogueText))
                {
                    streamDialogueText = "- ...";
                }

                if (string.IsNullOrWhiteSpace(streamDialogueText))
                    return new[] { "..." };

                if (ModEntry.Config.Debug)
                {
                    ModEntry.SMonitor.Log(
                        $"[ValleyTalk] [Raw API Response] {character.Name}:\n{streamDialogueText}",
                        LogLevel.Debug);
                }

                var processed = ProcessLines(streamDialogueText, character, false, prompts.InjectedPrivateThoughts).ToArray();

                if (!string.IsNullOrWhiteSpace(prompts.GiveGift) && processed.Length > 0)
                    processed[0] += $"[{prompts.GiveGift}]";

                // Extract mood tag and persist session history for continuity
                if (processed.Length > 0)
                {
                    string mood = ExtractMoodTag(processed[0]);
                    StripMoodTag(processed);
                    SessionCache.Instance.MergeHistory(
                        character.Name,
                        context.ChatHistory,
                        SanitizeForSession(processed[0]),
                        mood);
                }

                ConfirmDynamicBlocksConsumed(context, character, prompts);
                // Mark perceptions as consolidated to prevent re-injection of "just received gift" next turn
                PerceptionManager.Instance.MarkAsConsolidated(character.Name);
                //本次对话已顺利消费该事件
                PerceptionManager.Instance.Evict("Eat");

                // if (ModEntry.Config.Debug)
                //     LogDebugContext(character, context, prompts, processed);

                return processed.Length > 0 ? processed : new[] { "..." };
            }

            // ══════════════════════════════════════════════════
            //  非流式路径（带重试）
            // ══════════════════════════════════════════════════
            int timeoutSeconds = ModEntry.Config.QueryTimeout;
            Exception lastException = null;
            LlmResponse result = null;
            int userConfiguredTimeout = timeoutSeconds;  // Save user's config as the ceiling floor
            bool isDebug = ModEntry.Config.Debug; // 提前缓存 Debug 配置，减少属性访问

            for (int attempt = 0; attempt <= MAX_RETRY_ATTEMPTS; attempt++)
            {
                // If user already cancelled, do not retry
                if (character.IsUserCancelled)
                {
                    results = Array.Empty<string>();
                    break;
                }

                // Execute with timeout
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
                character.CurrentDialogueCts = cts;
                string[] resultsInternal = Array.Empty<string>();

                try
                {
                    if (isDebug) LogDebugRequest(character, prompts, attempt + 1);

                    var inferenceTask = Llm.Instance.RunInference(
                        prompts.SystemPrompt,
                        $"{prompts.GameConstantContext}",
                        $"{prompts.NpcConstantContext}",
                        $"{prompts.CorePrompt}{prompts.Instructions}{prompts.Command}",
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
                            ModEntry.SMonitor?.Log(
                                $"[LlmDialogueService] {character.Name} used speak_in_bubble, skipping dialogue box (non-streaming).",
                                LogLevel.Trace);
                            results = null;
                            break;
                        }

                        var dialogueText = result.Text;
                        if (toolCalls?.Count > 0 && string.IsNullOrWhiteSpace(dialogueText))
                        {
                            dialogueText = "- ...";
                        }

                        if (isDebug && !string.IsNullOrWhiteSpace(dialogueText))
                        {
                            ModEntry.SMonitor.Log(
                                $"[ValleyTalk] [Raw API Response] {character.Name}:\n{dialogueText}",
                                LogLevel.Debug);
                        }

                        resultsInternal = ProcessLines(dialogueText, character, attempt > 2, prompts.InjectedPrivateThoughts).ToArray();
                    }
                }
                catch (OperationCanceledException)
                {
                    // User-initiated cancellation, silent handling, no error logging
                    Log.Debug($"AI request cancelled for {character.Name}.");
                  
                    // 🔧 清理已注入的感知，避免重复（与流式路径保持一致）
                    try
                    {
                        ConfirmDynamicBlocksConsumed(context, character, prompts);
                        PerceptionManager.Instance?.MarkAsConsolidated(character.Name);
                        PerceptionManager.Instance?.Evict("Eat");
                    }
                    catch (Exception cleanupEx)
                    {
                        Log.Warning($"Perception cleanup failed after cancellation: {cleanupEx.Message}");
                    }
                  
                    resultsInternal = new string[] { "..." };
                    results = resultsInternal;
                    break; // Exit retry loop, no further retries
                }
                catch (Exception ex)
                {
                    lastException = ex;
                    Log.Error(ex, $"Error generating AI response for {character.StardewNpc.displayName}");
                }

                if (resultsInternal.Length > 0)
                {
                    results = resultsInternal;
                    string mood = ExtractMoodTag(resultsInternal[0]);
                    StripMoodTag(resultsInternal);
                    SessionCache.Instance.MergeHistory(
                        character.Name,
                        context.ChatHistory,
                        SanitizeForSession(resultsInternal[0]),
                        mood);

                    ConfirmDynamicBlocksConsumed(context, character, prompts);
                    // Mark perceptions as consolidated to prevent re-injection of "just received gift" next turn
                    PerceptionManager.Instance.MarkAsConsolidated(character.Name);
                    //本次对话已顺利消费该事件
                    PerceptionManager.Instance.Evict("Eat");

                    // if (isDebug) LogDebugContext(character, context, prompts, resultsInternal);
                    break; // Success, exit retry loop
                }
                else
                {
                    Log.Warning("No valid response generated from AI model.");
                    if (result != null && !string.IsNullOrWhiteSpace(result.ErrorMessage))
                    {
                        Log.Warning($"API Error Message: {result.ErrorMessage}");
                    }
                    else if (result != null && !string.IsNullOrWhiteSpace(result.Text))
                    {
                        Log.Warning($"API Response: {result.Text}");
                    }

                    // if (isDebug) LogDebugContext(character, context, prompts, resultsInternal);

                    // 修复：仅在失败后，且准备进行下一次尝试前，才应用延迟和超时翻倍
                    if (attempt < MAX_RETRY_ATTEMPTS)
                    {
                        if (attempt >= 1) // 第一次失败重试不延迟，第二次及以后开始延迟和翻倍
                        {
                            await Task.Delay(TimeSpan.FromSeconds(RETRY_DELAY_SECONDS));
                            timeoutSeconds = Math.Min(timeoutSeconds * 2, Math.Max(MAX_TIMEOUT_SECONDS, userConfiguredTimeout));
                        }
                    }
                }
            }

            // Handle final result
            // speak_in_bubble sentinel: propagate null to caller (PerformGeneration handles it)
            if (results == null)
                return null;

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
            // 确保方法任何出口点都会释放标志位
            _isRequestInProgress = false;
        }
    }

    /// <summary>
    /// 在确认本轮 Prompt 已经成功发出/成功生成回复后调用。
    /// 三个一次性动态模块（Eavesdrop / SpouseWaiting / Echo）只有在这里
    /// 才真正被标记为"已消费"，避免 STOOD_UP / DATE_CONTEXT /
    /// SIMPLE_GREETING 等早返回分支静默丢弃了尚未展示给玩家的内容。
    /// </summary>
    private static void ConfirmDynamicBlocksConsumed(DialogueContext context, Character character, Prompts prompts)
    {
        if (prompts == null || character == null) return;

        if (!string.IsNullOrEmpty(prompts.PendingEavesdropBlock))
            DialogueHistoryManager.Instance?.ConsumeEavesdropEntries(character.Name);
        if (!string.IsNullOrEmpty(prompts.PendingSpouseWaitingBlock))
            SpouseWaitingEvent.ConfirmSpouseDialogueConsumed(character.Name);
        if (!string.IsNullOrEmpty(prompts.PendingEchoBlock))
            ImmediateEchoStore.ConsumeEcho(character.Name);

        // 💍 确认本轮已表达过关系里程碑/吃醋，今日后续交谈恢复常态陪伴
        if (!string.IsNullOrEmpty(prompts.PendingMilestoneBlock))
            RelationshipMilestoneManager.Instance.ConfirmConsumed(character.Name);
    }

    /// <summary>
    /// Parses the raw LLM output string into dialogue lines and response option lines.
    /// Lines starting with '-' are treated as dialogue content.
    /// Lines starting with '%' are treated as response options.
    /// </summary>
    /// <param name="resultString">Raw LLM output.</param>
    /// <param name="character">Target character (used for portrait validation).</param>
    /// <param name="relaxedValidation">When true, allows non‑standard dialogue formats (used for retry scenarios).</param>
    private IEnumerable<string> ProcessLines(string resultString, Character character, bool relaxedValidation = false, IReadOnlyList<string> privateThoughts = null)
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

            // Fallback: 如果 LLM 没有输出 '-' 前缀，就兜底将所有非 '%' 的文本视作对话正文
            // 注意：若 LLM 输出了混排格式（如第一行无前缀，第二行有 '-'），此处不触发，这是预期设计
            if (rawDialogueLines.Count == 0)
                rawDialogueLines = resultLines.Where(x => !x.StartsWith("%")).ToList();

            if (rawDialogueLines.Count == 0)
                return Array.Empty<string>();

            // ── Step 3: Strip the leading '-' marker from each line, then JOIN into one
            //           string BEFORE cleaning. This preserves $h/$b/$0 structure that
            //           the LLM placed within a single logical reply. ──
            var strippedParts = rawDialogueLines
                .Select(line =>
                {
                    // Only strip a single "- " or "-" prefix; never use TrimStart (it eats all leading '-' and spaces)
                    if (line.StartsWith("- ")) return line.Substring(2);
                    if (line.StartsWith("-")) return line.Substring(1);
                    return line;
                })
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .ToList();

            // Join with a single space — the LLM already embeds #$b# / $b where it wants
            // page breaks, so we must NOT insert extra #$b# between lines here.
            string joined = string.Join(" ", strippedParts);

            // ── Step 4: Run the full cleanup pipeline ONCE on the joined string ──
            string cleaned = DialogueCleaner.CommonCleanup(joined);
            cleaned = DialogueCleaner.DialogueLineCleanup(
                cleaned, character.ValidPortraits, ModEntry.FixPunctuation, relaxedValidation);

            // Fallback: fix AI misuse of page-break tokens
            cleaned = SanitizePageBreaks(cleaned);

            if (string.IsNullOrWhiteSpace(cleaned))
                return Array.Empty<string>();

            // Debug: log if we merged multiple lines
            if (strippedParts.Count > 1 && ModEntry.Config.Debug)
                Log.Debug($"Merged {strippedParts.Count} '-' lines into one dialogue for {character.Name}.");

            // ── Step 5: Response options ──
            var responseLines = rawResponseLines
                .Select(x =>
                {
                    // Only strip a single "% " or "%" prefix; never use TrimStart (it eats all leading '%' and spaces)
                    if (x.StartsWith("% ")) return x.Substring(2);
                    if (x.StartsWith("%")) return x.Substring(1);
                    return x;
                })
                .Select(x => DialogueCleaner.CommonCleanup(x))
                .Select(x => DialogueCleaner.ResponseLineCleanup(x, ModEntry.FixPunctuation))
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToList();

            // 私有思绪防泄漏：选项不得引用玩家不可知的内心上下文；台词已亲口说出则视为共享信息
            if (privateThoughts != null)
            {
                foreach (var thought in privateThoughts)
                {
                    if (string.IsNullOrWhiteSpace(thought)) continue;
                    string t = thought.Trim();
                    if (string.IsNullOrEmpty(t) || cleaned.Contains(t)) continue;
                    responseLines.RemoveAll(opt => opt != null && opt.Contains(t, StringComparison.OrdinalIgnoreCase));
                }
            }

            // 这里检查的是经过清理、过滤空白字符串后的最终 List 数量
            // 如果玩家选项少于 2 个（如只有1个"再见"），不具备选择意义，则直接清空
            if (responseLines.Count < 2)
                responseLines.Clear();
            // 🌟【新增防护】：如果选项超过 3 个，截取前 3 个，防止模型抽风输出十几行选项
            else if (responseLines.Count > 3)
                responseLines = responseLines.Take(3).ToList();

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
        sb.AppendLine($"║");
        sb.AppendLine($"║ 【Length Statistics】");
        sb.AppendLine($"║   CorePrompt Length: {prompts.CorePrompt.Length} chars");
        int totalPromptChars = (prompts.SystemPrompt?.Length ?? 0)
                             + (prompts.GameConstantContext?.Length ?? 0)
                             + (prompts.NpcConstantContext?.Length ?? 0)
                             + (prompts.Instructions?.Length ?? 0)
                             + (prompts.CorePrompt?.Length ?? 0)
                             + (prompts.Command?.Length ?? 0);
        // Mixed CJK/Latin payload ≈ 3.8 chars/token
        int estimatedTokens = (int)Math.Ceiling(totalPromptChars / 3.8);
        sb.AppendLine($"║   Total Request Length: {totalPromptChars} chars (~{estimatedTokens} Tokens)");
        sb.AppendLine($"╚═══════════════════════════════════════════════════════════════════");
        Log.Debug(sb.ToString());
    }

    /// <summary>
    /// Logs the game context (location, weather, time, etc.) and the final processed results.
    /// </summary>
    private void LogDebugContext(Character character, DialogueContext context, Prompts prompts, string[] resultsInternal)
    {
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

    /// <summary>
    /// Strips format markers from LLM output before writing to session cache.
    /// Removes #$b#/$h/[ACTION:...]/[MOOD:...]/[241] etc. so history stays clean.
    /// </summary>
    private static string SanitizeForSession(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        string s = Regex.Replace(text, @"#\$[^\#]+#?", "");
        s = Regex.Replace(s, @"\$[a-zA-Z0-9]", "");
        s = Regex.Replace(s, @"\[ACTION:.*?\]", "");
        s = Regex.Replace(s, @"\[UI:.*?\]", "");        // 清洗 UI 驱动标签，防止污染会话历史
        s = Regex.Replace(s, @"\[MOOD:\w+\]", "");
        s = Regex.Replace(s, @"\[\d+\]", "");
        // Clean up empty parentheses left after removing page-break tokens, e.g. (##) or ()
        s = Regex.Replace(s, @"\(\s*\)", "");
        s = Regex.Replace(s, @" {2,}", " ");
        return s.Trim();
    }

    /// <summary>
    /// Extracts [MOOD:xxx] tag from dialogue text. Returns the mood keyword or empty string.
    /// Convention: LLM outputs [MOOD:curious] etc. at end of line when tone shifts.
    /// </summary>
    private static string ExtractMoodTag(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";
        var match = System.Text.RegularExpressions.Regex.Match(text, @"\[MOOD:(\w+)\]");
        return match.Success ? match.Groups[1].Value.ToLower() : "";
    }

    /// <summary>
    /// Strips all [MOOD:xxx] tags from processed dialogue lines (in-place).
    /// Mood tags are metadata for session tracking, not meant for display.
    /// </summary>
    private static void StripMoodTag(string[] lines)
    {
        for (int i = 0; i < lines.Length; i++)
        {
            lines[i] = System.Text.RegularExpressions.Regex.Replace(lines[i], @"\[MOOD:\w+\]", "").Trim();
        }
    }

    /// <summary>
    /// Fix AI overuse of #$b# / #$e# tokens:
    /// 1. Remove page-break tokens inside or next to parentheses
    /// 2. Limit total page-break tokens to at most 1
    /// 3. Remove in-sentence page-break tokens (only keep after 。！？)
    /// </summary>
    private static string SanitizePageBreaks(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;
        // 1. Remove page-break tokens inside or adjacent to parentheses
        text = Regex.Replace(text, @"\(#\$[a-z]+#", "(");
        text = Regex.Replace(text, @"#\$[a-z]+#\)", ")");
        // 2. Remove page-break tokens right after an opening parenthesis
        text = Regex.Replace(text, @"\(#\$[a-z]+#\s*", "(");
        // 3. Limit total page-break tokens: keep at most 1
        int pageBreakCount = 0;
        text = Regex.Replace(text, @"#\$[a-z]+#", m =>
        {
            pageBreakCount++;
            return pageBreakCount <= 1 ? m.Value : " ";
        });
        // 4. Clean up extra spaces caused by removed tokens
        text = Regex.Replace(text, @" {2,}", " ");
        return text.Trim();
    }

    /// <summary>
    /// 内部终局提示词去重排查器。
    /// 严格排查以 "- " 开头的列表行，同时对元数据和示范台词实施完全白名单保护。
    /// </summary>
    private static class PromptDeduplicator
    {
        private static readonly string[] ProtectedPrefixes = new[]
        {
            "- 位置:", "- Location:",
            "- 时间:", "- Time:",
            "- 季节:", "- Season:",
            "- 天气氛围:", "- Weather:",
            "- 空间状态:", "- Spatial Status:",
            "- 社交关系:", "- Social Standing:",
            "- 状态:", "- Status:",
            "- 目标:", "- Goal:",
            "- ("      // 保护示范台词起笔
        };

        /// <summary>
        /// 智能去重：只对动态内容（CorePrompt）去重，保持静态内容（SystemPrompt）完全不变。
        /// </summary>
        /// <remarks>
        /// <para><strong>KV-Cache 保护原则：</strong></para>
        /// <para>1. SystemPrompt 必须保持完全静态，任何修改都会导致前缀缓存失效</para>
        /// <para>2. 前缀缓存的价值在于多轮对话中 SystemPrompt 一字不变，LLM 可复用已计算的 KV 状态</para>
        /// <para>3. 如果 SystemPrompt 和 CorePrompt 中存在重复内容（如 gossip），应在注入阶段就避免重复</para>
        /// <para>
        /// <strong>为何不直接删除去重器？</strong><br/>
        /// 保留此方法作为防御性编程手段，捕获潜在的动态注入错误（如未来新增模块意外重复注入）。
        /// 但去重操作仅限于 CorePrompt，绝不触碰 SystemPrompt。
        /// </para>
        /// </remarks>
        public static void DeduplicatePrompts(Prompts prompts)
        {
            if (prompts == null) return;

            var seenEntries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // ══════════════════════════════════════════════════════════════════════
            // [KV-CACHE PROTECTION] 只对 CorePrompt 去重，SystemPrompt 保持完全静态
            // ══════════════════════════════════════════════════════════════════════
          
            // 1. 先收集 SystemPrompt 中的条目（用于检测重复，但不修改 SystemPrompt）
            int systemEntriesCount = seenEntries.Count;
            CollectEntriesWithoutModifying(prompts.SystemPrompt, seenEntries);
            systemEntriesCount = seenEntries.Count - systemEntriesCount;

            // 2. 只清洗 CorePrompt（若与 SystemPrompt 撞车，CorePrompt 中的副本被移除）
            string originalCorePrompt = prompts.CorePrompt;
            prompts.CorePrompt = DeduplicateInternal(prompts.CorePrompt, seenEntries);

            // 3. SystemPrompt 完全不动，保持前缀缓存有效性
            // prompts.SystemPrompt 保持原样

            // 4. Debug 日志：记录去重结果
            if (ModEntry.Config?.Debug ?? false)
            {
                int coreEntriesRemoved = 0;
                if (!string.IsNullOrEmpty(originalCorePrompt))
                {
                    int originalLines = originalCorePrompt.Split('\n').Count(l => l.TrimStart().StartsWith("- "));
                    int finalLines = prompts.CorePrompt.Split('\n').Count(l => l.TrimStart().StartsWith("- "));
                    coreEntriesRemoved = originalLines - finalLines;
                }

                if (coreEntriesRemoved > 0 || systemEntriesCount > 0)
                {
                    ModEntry.SMonitor?.Log(
                        $"[PromptDeduplicator] SystemPrompt entries: {systemEntriesCount}, " +
                        $"CorePrompt duplicates removed: {coreEntriesRemoved}",
                        StardewModdingAPI.LogLevel.Debug);
                }
            }
        }

        /// <summary>
        /// 只收集 SystemPrompt 中的条目到 seenEntries，不做任何修改。
        /// 用于去重时让 CorePrompt 知晓 SystemPrompt 已有哪些内容，但绝不触碰 SystemPrompt 本身。
        /// </summary>
        private static void CollectEntriesWithoutModifying(string rawText, HashSet<string> seenEntries)
        {
            if (string.IsNullOrWhiteSpace(rawText))
                return;

            using (var reader = new StringReader(rawText))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    string trimmed = line.TrimStart();

                    if (trimmed.StartsWith("- ", StringComparison.Ordinal))
                    {
                        bool isProtected = false;
                        for (int i = 0; i < ProtectedPrefixes.Length; i++)
                        {
                            if (trimmed.StartsWith(ProtectedPrefixes[i], StringComparison.OrdinalIgnoreCase))
                            {
                                isProtected = true;
                                break;
                            }
                        }

                        if (!isProtected)
                        {
                            string content = trimmed.Substring(2).Trim();
                            if (content.Length >= 4)
                            {
                                seenEntries.Add(content); // 只收集，不修改任何内容
                            }
                        }
                    }
                }
            }
        }
        private static string DeduplicateInternal(string rawText, HashSet<string> seenEntries)
        {
            if (string.IsNullOrWhiteSpace(rawText))
                return rawText;

            var sb = new StringBuilder(rawText.Length);

            using (var reader = new StringReader(rawText))
            {
                string line;
                while ((line = reader.ReadLine()) != null)
                {
                    string trimmed = line.TrimStart();

                    // 严格且仅识别以 "- " 开头的列表行
                    if (trimmed.StartsWith("- ", StringComparison.Ordinal))
                    {
                        bool isProtected = false;
                        for (int i = 0; i < ProtectedPrefixes.Length; i++)
                        {
                            if (trimmed.StartsWith(ProtectedPrefixes[i], StringComparison.OrdinalIgnoreCase))
                            {
                                isProtected = true;
                                break;
                            }
                        }

                        if (!isProtected)
                        {
                            string content = trimmed.Substring(2).Trim();

                            // 长度 >= 4 的具体事件或新闻才排查
                            if (content.Length >= 4)
                            {
                                if (!seenEntries.Add(content))
                                {
                                    continue; // 命中重复，跳过写入（丢弃）
                                }
                            }
                        }
                    }

                    sb.AppendLine(line);
                }
            }

            return sb.ToString().TrimEnd();
        }
    }
}