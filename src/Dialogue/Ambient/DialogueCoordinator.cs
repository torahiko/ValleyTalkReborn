using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using ValleytalkReborn;

namespace ValleytalkReborn;

/// <summary>
/// 对话协调器：唯一允许订阅 SMAPI GameLoop 事件的对象。
/// 负责将所有游戏循环事件分发给各个对话模块，并确保执行顺序：
/// 1. A2A ProcessPendingResults
/// 2. Ambient Bark ProcessPendingResults
/// 3. A2A Radar
/// 4. A2A Session Tick
/// 5. Ambient Bark Request Dispatch
/// 6. Ambient Bark State Tick
/// 7. MainThreadOutputQueue.Process(20)
/// </summary>
internal sealed class DialogueCoordinator
{
    private readonly IModHelper _helper;
    private readonly IMonitor _monitor;
    private readonly AmbientBarkModule _ambientBark;
    private readonly A2AModule _a2a;
    private readonly MainThreadOutputQueue _outputQueue;
    private readonly NpcReservationService _reservations;

    private bool _subscribed;

    private string _activeInteractingSpeaker = null;

    internal DialogueCoordinator(
        IModHelper helper,
        IMonitor monitor,
        AmbientBarkModule ambientBark,
        A2AModule a2a,
        MainThreadOutputQueue outputQueue,
        NpcReservationService reservations)
    {
        _helper = helper;
        _monitor = monitor;
        _ambientBark = ambientBark;
        _a2a = a2a;
        _outputQueue = outputQueue;
        _reservations = reservations;

        // 绑定 A2A 锁定回调：A2A 锁定时清理对应 NPC 的单人 Bark 状态
        _a2a.SessionManager.OnNpcA2ALocked = name => _ambientBark.ResetForNpc(name);

        // 绑定 A2A 冷静期查询：AmbientBark 雷达扫描时借此过滤掉刚结束
        // A2A 会话的 NPC，防止会话一散场就立刻把参与者重新拉进请求队列。
        _ambientBark.IsNpcInA2APostCooldown = name => _a2a.SessionManager.IsInPostSessionCooldown(name);
    }

    /// <summary>
    /// 订阅 SMAPI 游戏循环事件。只允许调用一次。
    /// </summary>
    internal void Subscribe()
    {
        if (_subscribed)
            return;

        _subscribed = true;

        // 绑定跟随系统回调：跟随开始/结束时通知 AmbientBark 重置状态
        MovementManager.Instance.OnFollowStartedCallback = npc => _ambientBark.ResetForFollow(npc);
        MovementManager.Instance.OnFollowEndedCallback = name => _ambientBark.ResetForNpc(name);

        _helper.Events.GameLoop.GameLaunched += OnGameLaunched;
        _helper.Events.GameLoop.DayStarted += OnDayStarted;
        _helper.Events.GameLoop.UpdateTicked += OnUpdateTicked;
        _helper.Events.GameLoop.ReturnedToTitle += OnReturnedToTitle;
    }

    /// <summary>
    /// 取消订阅 SMAPI 游戏循环事件。
    /// </summary>
    internal void Unsubscribe()
    {
        if (!_subscribed)
            return;

        _helper.Events.GameLoop.GameLaunched -= OnGameLaunched;
        _helper.Events.GameLoop.DayStarted -= OnDayStarted;
        _helper.Events.GameLoop.UpdateTicked -= OnUpdateTicked;
        _helper.Events.GameLoop.ReturnedToTitle -= OnReturnedToTitle;

        _subscribed = false;
    }

    private void OnGameLaunched(object sender, GameLaunchedEventArgs e)
    {
        try
        {
            _ambientBark.OnGameLaunched();
        }
        catch (System.Exception ex)
        {
            _monitor.Log($"[DialogueCoordinator] AmbientBark OnGameLaunched error: {ex.Message}", LogLevel.Error);
        }

        try
        {
            _a2a.OnGameLaunched();
        }
        catch (System.Exception ex)
        {
            _monitor.Log($"[DialogueCoordinator] A2A OnGameLaunched error: {ex.Message}", LogLevel.Error);
        }
    }

    private void OnDayStarted(object sender, DayStartedEventArgs e)
    {
        try
        {
            _ambientBark.OnDayStarted();
        }
        catch (System.Exception ex)
        {
            _monitor.Log($"[DialogueCoordinator] AmbientBark OnDayStarted error: {ex.Message}", LogLevel.Error);
        }

        try
        {
            _a2a.OnDayStarted();
        }
        catch (System.Exception ex)
        {
            _monitor.Log($"[DialogueCoordinator] A2A OnDayStarted error: {ex.Message}", LogLevel.Error);
        }

        try
        {
            PlayerStateScanner.OnDayStarted();
        }
        catch (System.Exception ex)
        {
            _monitor.Log($"[DialogueCoordinator] PlayerStateScanner OnDayStarted error: {ex.Message}", LogLevel.Error);
        }

        _reservations.Clear();
        _outputQueue.Clear();
    }

    private void OnUpdateTicked(object sender, UpdateTickedEventArgs e)
    {
        if (!Context.IsWorldReady || Game1.paused)
        {
            return;
        }

        // Guard: 凌晨 2 点后停止所有对话活动（与旧版 DynamicBarkManager 对齐）
        if (Game1.timeOfDay >= 2600)
        {
            ResetAllDialogueState("timeOfDay >= 2600");
            return;
        }

        var config = ModEntry.Config;

        // ════════════════════════════════════════════════════════════
        // 阶段 1：网络回传处理（任何时候都必须优先消化，绝不阻塞）
        // ════════════════════════════════════════════════════════════

        // 1. A2A 结果落地（如果在对话中返回，正常装填 Script，入睡则提前作废）
        if (config.EnableA2A)
        {
            try
            {
                _a2a.SessionManager.ProcessPendingResults();
            }
            catch (System.Exception ex)
            {
                _monitor.Log($"[DialogueCoordinator] A2A ProcessPendingResults error: {ex.Message}", LogLevel.Error);
            }
        }

        // 2. Bark 结果落地（★ 关键：必须在此处执行！若在对话中返回，
        //    由 AmbientBarkModule 内部逻辑处理 EchoStore 转化）
        if (config.EnableAmbientBarks)
        {
            try
            {
                _ambientBark.ProcessPendingResults();
            }
            catch (System.Exception ex)
            {
                _monitor.Log($"[DialogueCoordinator] Bark ProcessPendingResults error: {ex.Message}", LogLevel.Error);
            }
        }

        // ════════════════════════════════════════════════════════════
        // 阶段 2：交互拦截与挂起
        // ════════════════════════════════════════════════════════════

        // 检测玩家是否正在与 NPC 进行原版主对话
        bool isDialogueActive = Game1.dialogueUp;
        string currentSpeakerName = Game1.currentSpeaker?.Name;

        if (isDialogueActive)
        {
            if (!string.IsNullOrEmpty(currentSpeakerName))
            {
                _activeInteractingSpeaker = currentSpeakerName;

                // 单人 Bark：取消正在空中的 Bark 请求；已在队列里的头顶台词转入 ImmediateEchoStore
                _ambientBark.NotifyPlayerInteracted(currentSpeakerName, cooldownSeconds: 30);
            }

            // ★ A2A 模块：不需要调用 CancelForNpc，让它在后台静默挂起即可！
            // A2ASessionManager.TickA2ASessions 中的挂起检测会冻结播放推进。
            return;
        }
        else if (!string.IsNullOrEmpty(_activeInteractingSpeaker))
        {
            // 对话刚刚关闭的瞬间，再次确保 Bark 冷静期生效
            _ambientBark.NotifyPlayerInteracted(_activeInteractingSpeaker, cooldownSeconds: 30);
            _activeInteractingSpeaker = null;
        }

        if (Game1.activeClickableMenu != null || Game1.eventUp)
        {
            return;
        }

        // ════════════════════════════════════════════════════════════
        // 阶段 3：世界推进（TickA2A 在此处运行，入睡检查在此处彻底卡死任何非法播放）
        // ════════════════════════════════════════════════════════════

        // 3. A2A Radar
        if (config.EnableA2A)
        {
            try
            {
                _a2a.SessionManager.TickRadarCooldown();
            }
            catch (System.Exception ex)
            {
                _monitor.Log($"[DialogueCoordinator] A2A Radar error: {ex.Message}", LogLevel.Error);
            }

            // 4. A2A Session Tick
            try
            {
                _a2a.SessionManager.Tick();
            }
            catch (System.Exception ex)
            {
                _monitor.Log($"[DialogueCoordinator] A2A Session Tick error: {ex.Message}", LogLevel.Error);
            }
        }

        // 4.5 Ambient Bark Radar
        if (config.EnableAmbientBarks)
        {
            try
            {
                _ambientBark.TickRadarCooldown();
            }
            catch (System.Exception ex)
            {
                _monitor.Log($"[DialogueCoordinator] AmbientBark Radar error: {ex.Message}", LogLevel.Error);
            }
        }

        // 5. Ambient Bark Request Dispatch (guarded - controls new requests only)
        if (config.EnableAmbientBarks)
        {
            bool hasActiveA2APlayback = config.EnableA2A && _a2a.SessionManager.HasActivePlayback();
            try
            {
                _ambientBark.DispatchRequests(hasActiveA2APlayback);
            }
            catch (System.Exception ex)
            {
                _monitor.Log($"[DialogueCoordinator] AmbientBark Dispatch error: {ex.Message}", LogLevel.Error);
            }
        }

        // 6. Ambient Bark State Tick - ALWAYS called!
        // Module checks Config.EnableAmbientBarks internally and runs CleanupAll when disabled.
        try
        {
            _ambientBark.TickStates();
        }
        catch (System.Exception ex)
        {
            _monitor.Log($"[DialogueCoordinator] AmbientBark TickStates error: {ex.Message}", LogLevel.Error);
        }

        // 7. MainThreadOutputQueue.Process(20) (always called to drain queue)
        try
        {
            _outputQueue.Process(20);
        }
        catch (System.Exception ex)
        {
            _monitor.Log($"[DialogueCoordinator] OutputQueue Process error: {ex.Message}", LogLevel.Error);
        }
    }

    private void OnReturnedToTitle(object sender, ReturnedToTitleEventArgs e)
    {
        try
        {
            _ambientBark.OnReturnedToTitle();
        }
        catch (System.Exception ex)
        {
            _monitor.Log($"[DialogueCoordinator] AmbientBark OnReturnedToTitle error: {ex.Message}", LogLevel.Error);
        }

        try
        {
            _a2a.OnReturnedToTitle();
        }
        catch (System.Exception ex)
        {
            _monitor.Log($"[DialogueCoordinator] A2A OnReturnedToTitle error: {ex.Message}", LogLevel.Error);
        }

        _reservations.Clear();
        _outputQueue.Clear();
    }

    /// <summary>
    /// Reset all dialogue state. Called on day start, return to title,
    /// save load, config hot-reload, and module disable.
    /// </summary>
    internal void ResetAllDialogueState(string reason)
    {
        try
        {
            _a2a.SessionManager.CancelAll(reason, applyCooldown: false);
        }
        catch (System.Exception ex)
        {
            _monitor.Log($"[DialogueCoordinator] ResetAll A2A error: {ex.Message}", LogLevel.Error);
        }

        try
        {
            _ambientBark.CleanupAll();
        }
        catch (System.Exception ex)
        {
            _monitor.Log($"[DialogueCoordinator] ResetAll AmbientBark error: {ex.Message}", LogLevel.Error);
        }

        _reservations.Clear();
        _outputQueue.Clear();
        ImmediateEchoStore.Clear();
    }

    /// <summary>
    /// 获取 Ambient Bark 模块（用于兼容入口）。
    /// </summary>
    internal AmbientBarkModule AmbientBark => _ambientBark;

    /// <summary>
    /// 获取 A2A 模块（用于兼容入口）。
    /// </summary>
    internal A2AModule A2A => _a2a;
}