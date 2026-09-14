// AsyncBuilder.cs
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Menus;
using ValleytalkReborn.Platform;

namespace ValleytalkReborn;

public class AsyncBuilder
{
    private static readonly AsyncBuilder _instance = new AsyncBuilder();
    public static AsyncBuilder Instance => _instance;

    private static readonly HashSet<string> _aiDialogueNpcNames =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public static bool ConsumeAiDialogueFlag(string npcName)
    {
        return _aiDialogueNpcNames.Remove(npcName);
    }

    private readonly ConcurrentQueue<Action> _mainThreadActionQueue = new ConcurrentQueue<Action>();
    private readonly ConcurrentQueue<string> _streamTokenQueue = new ConcurrentQueue<string>();
    private readonly System.Text.StringBuilder _streamAccumulator = new System.Text.StringBuilder();
    private IClickableMenu _placeholderMenu = null;
    private bool _awaitingGeneration = false;
    private int _waitFrames = 0;
    private GenerationType _awaitedType = GenerationType.None;
    private NPC _speakingNpc = null;
    private string _currentDialogueKey = "";
    private string _originalLine = null;
    private IEnumerable<ConversationElement> _currentConversation = null;
    private StardewValley.Object _currentGift = null;
    private int _currentTaste = 0;
    private readonly HashSet<string> _requestedThisFrame = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private int _generationCooldownFrames = 0;
    private bool _isStreaming = false;
    private int _generationId = 0;
    private readonly HashSet<string> _pendingGiftNpcs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public bool AwaitingGeneration => _awaitingGeneration;
    public bool IsGeneratingDialogue { get; internal set; }
    public NPC SpeakingNpc => _speakingNpc;
    internal GenerationType AwaitedType => _awaitedType;
    public int GenerationCooldownFrames => _generationCooldownFrames;

    // 🌟 双重保险：抑制历史记录
    public static bool SuppressHistory { get; set; } = false;

    private AsyncBuilder()
    {
        if (ModEntry.SHelper != null)
        {
            ModEntry.SHelper.Events.GameLoop.UpdateTicked += OnUpdateTicked;
        }
    }

    public void Cleanup()
    {
        try
        {
            ResetState();
            while (_mainThreadActionQueue.TryDequeue(out _)) { }
            while (_streamTokenQueue.TryDequeue(out _)) { }
            _streamAccumulator.Clear();
            _requestedThisFrame.Clear();
            _aiDialogueNpcNames.Clear();
        }
        catch (Exception ex)
        {
            ModEntry.SMonitor?.Log($"[AsyncBuilder] Error during cleanup: {ex.Message}", LogLevel.Warn);
        }
    }

    private void ResetState()
    {
        _awaitingGeneration = false;
        IsGeneratingDialogue = false;
        _isStreaming = false;
        _waitFrames = 0;
        _placeholderMenu = null;
        _speakingNpc = null;
        _currentDialogueKey = string.Empty;
        _originalLine = null;
        _currentConversation = null;
        _currentGift = null;
        _currentTaste = 0;
        _awaitedType = GenerationType.None;
        while (_streamTokenQueue.TryDequeue(out _)) { }
        _streamAccumulator.Clear();
        _pendingGiftNpcs.Clear();
    }

    /// <summary>
    /// Aborts the current in-progress generation by incrementing the generation ID
    /// (causing the running PerformGeneration to discard its result) and resetting all state.
    /// Use this when preempting an in-progress generation with a higher-priority request.
    /// </summary>
    private void AbortCurrentGeneration()
    {
        _generationId++;
        ResetState();
    }

    private void OnUpdateTicked(object sender, UpdateTickedEventArgs e)
    {
        _requestedThisFrame.Clear();

        if (_generationCooldownFrames > 0)
            _generationCooldownFrames--;

        // 🌟 修复：生成期间强行冻结 NPC 动作与寻路，始终保持面向玩家，防止走出几步
        if (IsGeneratingDialogue && _speakingNpc != null)
        {
            _speakingNpc.Halt();
            _speakingNpc.movementPause = 20;
            if (Game1.player != null)
                _speakingNpc.facePlayer(Game1.player);
        }

        // 🌟 修复：正常消耗并缓冲流式 Token，但绝不调用 new DialogueBox 刷新界面
        // 杜绝因残缺符号导致原版 DialogueBox.closeDialogue() 触发自闭合与 NPC 解冻
        if (_isStreaming && _streamTokenQueue.Count > 0)
        {
            while (_streamTokenQueue.TryDequeue(out var token))
                _streamAccumulator.Append(token);
        }

        while (_mainThreadActionQueue.TryDequeue(out var action))
        {
            try { action?.Invoke(); }
            catch (Exception ex) { ModEntry.SMonitor?.Log($"[AsyncBuilder] Error executing main thread action: {ex.Message}", LogLevel.Error); }
        }

        if (_awaitingGeneration)
        {
            if (_awaitedType == GenerationType.conversation || _awaitedType == GenerationType.Gift)
            {
                if (Game1.activeClickableMenu == null)
                {
                    _awaitingGeneration = false;
                    _waitFrames = 0;
                    IClickableMenu db;
                    if (_speakingNpc != null)
                    {
                        // 🌟 使用纯空格占位，界面完全透明隐形，且触发 IsNullOrWhiteSpace 保护，绝不入库
                        SuppressHistory = true;
                        var placeholder = new DialogueBox(new Dialogue(_speakingNpc, "", "   "));
                        SuppressHistory = false;
                        Game1.activeClickableMenu = placeholder;
                        db = placeholder;
                    }
                    else
                    {
                        SuppressHistory = true;
                        var placeholder = new DialogueBox("   ");
                        SuppressHistory = false;
                        Game1.activeClickableMenu = placeholder;
                        db = placeholder;
                    }
                    _placeholderMenu = db;
                    IsGeneratingDialogue = true;
                    ModEntry.SMonitor?.Log(
                        $"[AsyncBuilder] ★ Starting PerformGeneration. type={_awaitedType}, npc={_speakingNpc?.Name}",
                        LogLevel.Debug);
                    _ = PerformGeneration(db);
                }
                else
                {
                    _waitFrames++;
                    if (_waitFrames > 120)
                    {
                        ModEntry.SMonitor?.Log("[AsyncBuilder] Timed out waiting for menu to close (conversation/gift), resetting.", LogLevel.Warn);
                        if (Game1.activeClickableMenu != null) Game1.exitActiveMenu();
                        ResetState();
                    }
                }
            }
            else
            {
                if (Game1.activeClickableMenu is DialogueBox db)
                {
                    ModEntry.SMonitor?.Log($"[AsyncBuilder] Taking over DialogueBox, starting generation.", LogLevel.Trace);
                    _awaitingGeneration = false;
                    _waitFrames = 0;
                    if (_speakingNpc != null)
                    {
                        // 🌟 使用纯空格占位，界面完全透明隐形，且触发 IsNullOrWhiteSpace 保护，绝不入库
                        SuppressHistory = true;
                        var newDb = new DialogueBox(new Dialogue(_speakingNpc, "", "   "));
                        SuppressHistory = false;
                        Game1.activeClickableMenu = newDb;
                        db = newDb;
                    }
                    _placeholderMenu = db;
                    IsGeneratingDialogue = true;
                    ModEntry.SMonitor?.Log(
                        $"[AsyncBuilder] ★ Starting PerformGeneration. type={_awaitedType}, npc={_speakingNpc?.Name}",
                        LogLevel.Debug);
                    _ = PerformGeneration(db);
                }
                else
                {
                    _waitFrames++;
                    ModEntry.SMonitor?.Log($"[AsyncBuilder] Waiting for DialogueBox, activeMenu={Game1.activeClickableMenu?.GetType().Name ?? "null"}, waitFrames={_waitFrames}", LogLevel.Trace);
                    if (_waitFrames > 120)
                    {
                        ModEntry.SMonitor?.Log("[AsyncBuilder] Timed out waiting for DialogueBox (Basic/Gift), resetting.", LogLevel.Warn);
                        if (Game1.activeClickableMenu != null) Game1.exitActiveMenu();
                        ResetState();
                    }
                }
            }
        }
    }

    private async Task PerformGeneration(IClickableMenu placeholder)
    {
        NPC npc = _speakingNpc;
        GenerationType currentType = _awaitedType;
        int myGenerationId = _generationId;
        try
        {
            Task<Dialogue> dialogueTask = currentType switch
            {
                GenerationType.Basic => GenerateNpc(),
                GenerationType.conversation => GenerateNpcResponse(),
                GenerationType.Gift => GenerateNpcGift(),
                _ => null
            };

            if (dialogueTask == null)
            {
                EnqueueToMainThread(() =>
                {
                    var menuToClose = _placeholderMenu;
                    ResetState();
                    _generationCooldownFrames = 5;
                    var errMsg = Util.GetString("uiErrorGeneric") ?? "（请求发生异常，请检查设置。）";
                    if (npc != null)
                        Game1.activeClickableMenu = new DialogueBox(new Dialogue(npc, "", $"$s {errMsg}"));
                    else
                        ShowFeedbackDialogue(menuToClose, errMsg);
                });
                return;
            }

            var newDialogue = await dialogueTask;

            EnqueueToMainThread(() =>
            {
                // Check generation ID: if it changed, this result is stale (preempted by Gift).
                if (_generationId != myGenerationId)
                {
                    ModEntry.SMonitor?.Log(
                        $"[AsyncBuilder] Discarding stale {currentType} result for {npc?.Name} (id mismatch).",
                        LogLevel.Debug);
                    return;
                }
                var menuToClose = _placeholderMenu;
                ResetState();
                _generationCooldownFrames = 5;

                // 🌟 修复：有台词产出时，直接原子化 DrawDialogue 接管，杜绝提前调用 exitActiveMenu() 产生单帧黑洞
                if (newDialogue != null && newDialogue.dialogues.Count > 0)
                {
                    if (npc != null)
                    {
                        npc.Halt();
                        npc.facePlayer(Game1.player);
                        _aiDialogueNpcNames.Add(npc.Name);
                    }

                    // 🌟【终极防线】：无条件保尾裁中，确保 DialogueBox 总页数绝不超过上限且末页（选项页）必保留
                    if (newDialogue.dialogues.Count > DialogueBuilder.MaxDialoguePages)
                    {
                        int originalCount = newDialogue.dialogues.Count;
                        var lastPage = newDialogue.dialogues.Last();
                        var preservedPages = newDialogue.dialogues.Take(DialogueBuilder.MaxDialoguePages - 1).ToList();
                        preservedPages.Add(lastPage);
                        newDialogue.dialogues.Clear();
                        newDialogue.dialogues.AddRange(preservedPages);
                        ModEntry.SMonitor?.Log($"[AsyncBuilder] Page guard trimmed {originalCount} -> {DialogueBuilder.MaxDialoguePages} pages (tail preserved) for {npc?.Name}", LogLevel.Warn);
                    }

                    Game1.DrawDialogue(newDialogue);

                    // 🌟【核心修复】：清洗星露谷原版内部标记、选项占位符以及肖像指令，防止 ${ 回应: } 泄露至历史库
                    string rawResponseText = string.Join(" ", newDialogue.dialogues.Select(d => d.Text));
                    string cleanResponseText = SanitizeDialogueForHistory(rawResponseText);

                    if (Game1.player != null && cleanResponseText.Contains("@"))
                        cleanResponseText = cleanResponseText.Replace("@", Game1.player.Name);

                    var (_, lastPlayerChoice) = RecentConversationTracker.GetRecentContext(npc.Name);
                    RecentConversationTracker.RecordResponse(npc.Name, cleanResponseText, lastPlayerChoice);

                    if (currentType == GenerationType.Gift)
                        DialogueHistoryManager.Instance.RecordGiftReaction(npc.Name, cleanResponseText);
                    else
                        DialogueHistoryManager.Instance.RecordNpcDialogue(npc.Name, cleanResponseText, "conversation");

                    // 🌟【业务逻辑 100% 完整保留】：向周围 512 像素（8 格）内的旁观 NPC 广播偷听内容
                    if (npc.currentLocation != null && Game1.player != null)
                    {
                        var farmerLabel = Util.GetString("generalFarmerLabel") ?? "农夫";
                        string speakerName = npc.displayName ?? npc.Name;

                        string eavesdropText;
                        if (!string.IsNullOrWhiteSpace(lastPlayerChoice))
                        {
                            eavesdropText = $"[Eavesdrop] {farmerLabel}对{speakerName}说：\"{lastPlayerChoice}\"，{speakerName}回应：\"{cleanResponseText}\"";
                        }
                        else
                        {
                            eavesdropText = $"[Eavesdrop] {farmerLabel}对{speakerName}说话，{speakerName}回应：\"{cleanResponseText}\"";
                        }

                        foreach (var nearbyNpc in npc.currentLocation.characters)
                        {
                            if (nearbyNpc == null || nearbyNpc == npc || nearbyNpc.Name.Equals(npc.Name, StringComparison.OrdinalIgnoreCase))
                                continue;

                            if (!nearbyNpc.IsVillager) continue;   // 过滤马、宠物、怪物等非村民实体

                            float dx = nearbyNpc.Position.X - Game1.player.Position.X;
                            float dy = nearbyNpc.Position.Y - Game1.player.Position.Y;
                            float distance = (float)Math.Sqrt(dx * dx + dy * dy);

                            // 与 ActionSubscriber.Talk.cs 统一使用 512 像素判定
                            if (distance <= 512f)
                            {
                                DialogueHistoryManager.Instance.RecordSystemEvent(
                                    nearbyNpc.Name,
                                    eavesdropText,
                                    "eavesdrop");
                            }
                        }
                    }
                }
                else
                {
                    // 仅当没有台词产生（如 speak_in_bubble 气泡模式或空对话）时，才显式关闭占位框
                    if (menuToClose != null && Game1.activeClickableMenu == menuToClose)
                        Game1.exitActiveMenu();
                }
            });
        }
        catch (OperationCanceledException)
        {
            EnqueueToMainThread(() => ResetState());
        }
        catch (Exception ex)
        {
            ModEntry.SMonitor?.Log($"Error generating NPC response: {ex.Message}", LogLevel.Error);
            EnqueueToMainThread(() =>
            {
                ResetState();
                _generationCooldownFrames = 5;
                var netMsg = Util.GetString("uiErrorNetwork") ?? "（请求发生异常或超时，请检查网络与设置。）";
                if (npc != null)
                    Game1.activeClickableMenu = new DialogueBox(new Dialogue(npc, "", $"$s {netMsg}"));
                else
                    ShowFeedbackDialogue(placeholder, netMsg);
            });
        }
    }

    /// <summary>
    /// 彻底剔除星露谷原版语法格式及未渲染模板标记
    /// </summary>
    private static string SanitizeDialogueForHistory(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        // 🌟 修复：彻底剔除星露谷原版选项拼接残留（如 "{ 回应:"、"{ Respond:" 等）
        text = Regex.Replace(text, @"\{?\s*(回应|Respond)\s*:\s*", "", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"#\$[a-zA-Z0-9_\-]+(\s+[^\#]+)?\#?", "");
        text = Regex.Replace(text, @"\$[a-zA-Z0-9]", "");
        return DialogueHistoryManager.SanitizeForStorage(text);
    }

    private static void ShowFeedbackDialogue(IClickableMenu placeholder, string message)
    {
        if (placeholder != null && Game1.activeClickableMenu == placeholder)
            Game1.activeClickableMenu = new DialogueBox(message);
        else if (Game1.activeClickableMenu == null)
            Game1.activeClickableMenu = new DialogueBox(message);
    }

    internal void EnqueueToMainThread(Action action)
    {
        if (action != null) _mainThreadActionQueue.Enqueue(action);
    }

    internal bool RequestNpcResponse(NPC currentNpc, IEnumerable<ConversationElement> currentConversation)
    {
        if (currentNpc == null) return false;

        // Only block when truly in gift-generation phase; avoid stale marks permanently locking subsequent dialogue
        if (_awaitedType == GenerationType.Gift && IsGeneratingDialogue)
        {
            ModEntry.SMonitor?.Log(
                $"[AsyncBuilder] Suppressed conversation request for {currentNpc.Name}: gift interaction in progress.",
                LogLevel.Trace);
            return false;
        }

        // Consume/clear any residual gift mark
        ConsumeGiftInteractionMark(currentNpc.Name);

        if (CheckAndWarnIfAwaiting()) return false;
        if (!TryClaimNpc(currentNpc.Name)) return false;

        _speakingNpc = currentNpc;
        _currentConversation = currentConversation;
        _currentDialogueKey = "conversation";
        _awaitedType = GenerationType.conversation;
        _awaitingGeneration = true;
        return true;
    }

    // 🌟【业务逻辑 100% 完整保留】：送礼多重抢占状态机
    internal void RequestNpcGiftResponse(NPC currentNpc, StardewValley.Object gift, int taste)
    {
        if (currentNpc == null) return;

        // Gift has higher priority than Basic: if a Basic request is pending
        // (not yet started generation), replace it instead of being silently dropped.
        if (_awaitingGeneration && _awaitedType == GenerationType.Basic && !IsGeneratingDialogue)
        {
            ModEntry.SMonitor?.Log(
                $"[AsyncBuilder] ★ Gift preempting PENDING Basic for {currentNpc.Name}.",
                LogLevel.Debug);
            // Reset only state flags, keep _requestedThisFrame intact so
            // TryClaimNpc() for this Gift request still works correctly.
            _awaitingGeneration = false;
            _awaitedType = GenerationType.None;
            _speakingNpc = null;
            _currentDialogueKey = string.Empty;
            _originalLine = null;
            _currentConversation = null;
            _waitFrames = 0;
            _requestedThisFrame.Remove(currentNpc.Name);
        }

        // Gift also preempts in-progress Basic generation (already in PerformGeneration).
        if (IsGeneratingDialogue && _awaitedType == GenerationType.Basic)
        {
            ModEntry.SMonitor?.Log(
                $"[AsyncBuilder] ★ Gift preempting IN-PROGRESS Basic for {currentNpc.Name}.",
                LogLevel.Debug);
            if (Game1.activeClickableMenu == _placeholderMenu)
                Game1.exitActiveMenu();
            AbortCurrentGeneration();
            _requestedThisFrame.Remove(currentNpc.Name);
        }

        // Gift also preempts in-progress Gift generation (e.g. player gifts the same NPC rapidly).
        if (IsGeneratingDialogue && _awaitedType == GenerationType.Gift)
        {
            ModEntry.SMonitor?.Log(
                $"[AsyncBuilder] ★ Gift preempting IN-PROGRESS Gift for {currentNpc.Name}.",
                LogLevel.Debug);
            if (Game1.activeClickableMenu == _placeholderMenu)
                Game1.exitActiveMenu();
            AbortCurrentGeneration();
            _requestedThisFrame.Remove(currentNpc.Name);
        }

        // Gift skips cooldown check; only block if generation is truly in progress.
        // Cooldown is for debouncing normal dialogue, not for high-priority gifts.
        if (_awaitingGeneration || IsGeneratingDialogue)
        {
            ModEntry.SMonitor?.Log("[AsyncBuilder] Gift request blocked: generation in progress.", LogLevel.Trace);
            return;
        }

        // Force-clear cooldown so Gift is never blocked by it.
        _generationCooldownFrames = 0;
        if (!TryClaimNpc(currentNpc.Name)) return;

        MarkGiftInteraction(currentNpc.Name);
        _speakingNpc = currentNpc;
        _currentGift = gift;
        _currentTaste = taste;
        _awaitedType = GenerationType.Gift;
        _awaitingGeneration = true;
        ModEntry.SMonitor?.Log(
            $"[AsyncBuilder] ★ Gift request queued for {currentNpc.Name}. awaitedType={_awaitedType}",
            LogLevel.Debug);
    }

    public void MarkGiftInteraction(string npcName)
    {
        if (!string.IsNullOrEmpty(npcName))
            _pendingGiftNpcs.Add(npcName);
    }

    public bool ConsumeGiftInteractionMark(string npcName)
    {
        if (string.IsNullOrEmpty(npcName)) return false;
        return _pendingGiftNpcs.Remove(npcName);
    }

    public bool HasGiftInteractionPending(string npcName)
    {
        if (string.IsNullOrEmpty(npcName)) return false;
        return _pendingGiftNpcs.Contains(npcName);
    }

    internal bool TryRequestNpcBasic(NPC currentNpc, string dialogueKey, string originalLine)
    {
        ModEntry.SMonitor?.Log(
            $"[AsyncBuilder] TryRequestNpcBasic called for {currentNpc?.Name}, key={dialogueKey}",
            LogLevel.Trace);

        if (currentNpc == null) return false;
        if (CheckAndWarnIfAwaiting()) return false;
        if (!TryClaimNpc(currentNpc.Name)) return false;

        _speakingNpc = currentNpc;
        _currentDialogueKey = dialogueKey;
        _originalLine = originalLine;
        _awaitedType = GenerationType.Basic;
        _awaitingGeneration = true;
        return true;
    }

    internal void RequestNpcBasic(NPC currentNpc, string dialogueKey, string originalLine)
    {
        TryRequestNpcBasic(currentNpc, dialogueKey, originalLine);
    }

    private bool TryClaimNpc(string npcName)
    {
        if (string.IsNullOrEmpty(npcName)) return false;
        if (_requestedThisFrame.Contains(npcName))
        {
            ModEntry.SMonitor?.Log($"[AsyncBuilder] Deduplicated same-frame request for {npcName}.", LogLevel.Trace);
            return false;
        }
        _requestedThisFrame.Add(npcName);
        return true;
    }

    private bool CheckAndWarnIfAwaiting()
    {
        if (_awaitingGeneration || IsGeneratingDialogue || _generationCooldownFrames > 0)
        {
            ModEntry.SMonitor?.Log("[AsyncBuilder] Request blocked: generation in progress or cooldown.", LogLevel.Trace);
            return true;
        }
        return false;
    }

    /// <summary>
    /// 清除生成冷却计数，供用户主动触发的选项点击或文本输入使用。
    /// 冷却是为了防止自动触发的重复请求，不应拦截用户的主动操作。
    /// </summary>
    public void ClearCooldown()
    {
        _generationCooldownFrames = 0;
    }

    // 🌟【业务逻辑 100% 完整保留】：流式回调委托正常注册与下发，保证 LlmDialogueService 正常走流式通道
    private async Task<Dialogue> GenerateNpcGift()
    {
        _isStreaming = true;
        Action<string> streamCallback = token => _streamTokenQueue.Enqueue(token);
        return await DialogueBuilder.Instance.GenerateGift(_speakingNpc, _currentGift, _currentTaste, streamCallback);
    }

    private async Task<Dialogue> GenerateNpc()
    {
        _isStreaming = true;
        Action<string> streamCallback = token => _streamTokenQueue.Enqueue(token);
        return await DialogueBuilder.Instance.Generate(_speakingNpc, _currentDialogueKey, _originalLine, streamCallback);
    }

    private async Task<Dialogue> GenerateNpcResponse()
    {
        var npc = _speakingNpc;
        var conversationList = _currentConversation?.ToList() ?? new List<ConversationElement>();

        _isStreaming = true;
        Action<string> streamCallback = token => _streamTokenQueue.Enqueue(token);

        var newDialogue = await DialogueBuilder.Instance.GenerateResponse(npc, conversationList, true, streamCallback);
        if (newDialogue == null) return null;
        return new Dialogue(npc, _currentDialogueKey, newDialogue);
    }
}

internal enum GenerationType
{
    None,
    Basic,
    conversation,
    Gift
}
