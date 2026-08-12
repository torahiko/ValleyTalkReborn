using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
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

    /// <summary>
    /// NPC names whose current dialogue was AI-generated.
    /// Set just before DrawDialogue so the vanilla Patch can skip duplicate recording.
    /// Cleared by the Patch after it reads the flag.
    /// </summary>
    private static readonly HashSet<string> _aiDialogueNpcNames =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public static bool ConsumeAiDialogueFlag(string npcName)
    {
        return _aiDialogueNpcNames.Remove(npcName);
    }

    private readonly ConcurrentQueue<Action> _mainThreadActionQueue = new ConcurrentQueue<Action>();

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

    /// <summary>
    /// 防抖集合：记录本帧内已发起请求的 NPC 名字，下一帧自动清空。
    /// 彻底防止同帧内多个系统对同一 NPC 重复触发。
    /// </summary>
    private readonly HashSet<string> _requestedThisFrame = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    public bool AwaitingGeneration => _awaitingGeneration;
    public bool IsGeneratingDialogue { get; internal set; }
    public NPC SpeakingNpc => _speakingNpc;

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
        _waitFrames = 0;
        _placeholderMenu = null;
        _speakingNpc = null;
        _currentDialogueKey = string.Empty;
        _originalLine = null;
        _currentConversation = null;
        _currentGift = null;
        _currentTaste = 0;
        _awaitedType = GenerationType.None;
    }

    private void OnUpdateTicked(object sender, UpdateTickedEventArgs e)
    {
        // 每帧开始时清空防抖集合，确保下一次玩家交互可以正常触发
        _requestedThisFrame.Clear();

        while (_mainThreadActionQueue.TryDequeue(out var action))
        {
            try { action?.Invoke(); }
            catch (Exception ex) { ModEntry.SMonitor?.Log($"[AsyncBuilder] Error: {ex.Message}", LogLevel.Error); }
        }

        if (_awaitingGeneration)
        {
            if (Game1.activeClickableMenu is DialogueBox db)
            {
                _awaitingGeneration = false;
                _waitFrames = 0;

                if (_speakingNpc != null)
                {
                    var thinkingMsg = Util.GetString("ui.thinking") ?? "思考中...";
                    db = new DialogueBox(new Dialogue(_speakingNpc, "", $"$0 {thinkingMsg}"));
                    Game1.activeClickableMenu = db;
                }

                _placeholderMenu = db;
                IsGeneratingDialogue = true;
                _ = PerformGeneration(db);
            }
            else
            {
                _waitFrames++;
                if (_waitFrames > 30)
                {
                    ModEntry.SMonitor?.Log("[AsyncBuilder] Timed out waiting for DialogueBox, resetting.", LogLevel.Warn);
                    ResetState();
                }
            }
        }
    }

    private async Task PerformGeneration(IClickableMenu placeholder)
    {
        NPC npc = _speakingNpc;
        GenerationType currentType = _awaitedType;

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
                    ResetState();
                    var errMsg = Util.GetString("uiErrorGeneric") ?? "（请求发生异常，请检查设置。）";
                    if (npc != null)
                        Game1.activeClickableMenu = new DialogueBox(new Dialogue(npc, "", $"$s {errMsg}"));
                    else
                        ShowFeedbackDialogue(placeholder, errMsg);
                });
                return;
            }

            var newDialogue = await dialogueTask;

            EnqueueToMainThread(() =>
            {
                ResetState();
                ClosePlaceholder(placeholder);
                if (newDialogue != null && newDialogue.dialogues.Count > 0)
                {
                    // 【修复】在 DrawDialogue 之前打标，让 Patch 跳过 vanilla 记录
                    _aiDialogueNpcNames.Add(npc.Name);

                    Game1.DrawDialogue(newDialogue);

                    string responseText = string.Join(" ", newDialogue.dialogues.Select(d => d.Text));

                    if (Game1.player != null && responseText.Contains("@"))
                        responseText = responseText.Replace("@", Game1.player.Name);

                    var (_, lastPlayerChoice) = RecentConversationTracker.GetRecentContext(npc.Name);
                    RecentConversationTracker.RecordResponse(npc.Name, responseText, lastPlayerChoice);

                    if (currentType == GenerationType.Gift)
                        DialogueHistoryManager.Instance.RecordGiftReaction(npc.Name, responseText);
                    else
                        DialogueHistoryManager.Instance.RecordNpcDialogue(npc.Name, responseText, "conversation");
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
                var netMsg = Util.GetString("uiErrorNetwork") ?? "（请求发生异常或超时，请检查网络与设置。）";
                if (npc != null)
                    Game1.activeClickableMenu = new DialogueBox(new Dialogue(npc, "", $"$s {netMsg}"));
                else
                    ShowFeedbackDialogue(placeholder, netMsg);
            });
        }
    }

    private static void ClosePlaceholder(IClickableMenu placeholder)
    {
        if (placeholder != null && Game1.activeClickableMenu == placeholder)
            Game1.exitActiveMenu();
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

    internal void RequestNpcResponse(NPC currentNpc, IEnumerable<ConversationElement> currentConversation)
    {
        if (currentNpc == null) return;
        if (CheckAndWarnIfAwaiting()) return;
        if (!TryClaimNpc(currentNpc.Name)) return;

        _speakingNpc = currentNpc;
        _currentConversation = currentConversation;
        _awaitedType = GenerationType.conversation;
        _awaitingGeneration = true;
    }

    internal void RequestNpcGiftResponse(NPC currentNpc, StardewValley.Object gift, int taste)
    {
        if (currentNpc == null) return;
        if (CheckAndWarnIfAwaiting()) return;
        if (!TryClaimNpc(currentNpc.Name)) return;

        _speakingNpc = currentNpc;
        _currentGift = gift;
        _currentTaste = taste;
        _awaitedType = GenerationType.Gift;
        _awaitingGeneration = true;
    }

    internal void RequestNpcBasic(NPC currentNpc, string dialogueKey, string originalLine)
    {
        if (currentNpc == null) return;
        if (CheckAndWarnIfAwaiting()) return;
        if (!TryClaimNpc(currentNpc.Name)) return;

        _speakingNpc = currentNpc;
        _currentDialogueKey = dialogueKey;
        _originalLine = originalLine;
        _awaitedType = GenerationType.Basic;
        _awaitingGeneration = true;
    }

    /// <summary>
    /// 尝试为本帧"认领"一个 NPC 的请求槽位。
    /// 同一帧内同一个 NPC 只能被认领一次，后续调用直接返回 false。
    /// </summary>
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
        if (_awaitingGeneration || IsGeneratingDialogue)
        {
            ModEntry.SMonitor?.Log("[AsyncBuilder] Request blocked: generation already in progress.", LogLevel.Trace);
            return true;
        }
        return false;
    }

    private async Task<Dialogue> GenerateNpcGift() => await DialogueBuilder.Instance.GenerateGift(_speakingNpc, _currentGift, _currentTaste);
    private async Task<Dialogue> GenerateNpc() => await DialogueBuilder.Instance.Generate(_speakingNpc, _currentDialogueKey, _originalLine);
    private async Task<Dialogue> GenerateNpcResponse()
    {
        var npc = _speakingNpc;
        var conversationList = _currentConversation?.ToList() ?? new List<ConversationElement>();
        var newDialogue = await DialogueBuilder.Instance.GenerateResponse(npc, conversationList, true);
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