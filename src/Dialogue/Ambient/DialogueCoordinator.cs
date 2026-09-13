using System;
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

    // 状态跟踪：当前正在与玩家交互的 NPC 名字（null = 未交互）
    private string _activeInteractingSpeaker = null;

    /// <summary>
    /// 是否已观察到节日漫游态（eventUp + CanMove + 无对话无菜单）。
    /// 用于在 eventUp false→... 边沿触发 OnFestivalRoamEnded 清理。纯运行时记忆。
    /// </summary>
    private bool _festivalRoamObserved;

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
        _a2a.SessionManager.OnNpcA2ALocked = npcName =>
        {
            try
            {
                // 1. 硬重置 Bark 状态（清空队列、取消请求、清空思绪记忆）
                _ambientBark.ResetForNpc(npcName);

                // 2. 从全局请求队列移除（防止已入队但未派发的请求）
                _ambientBark.RemoveFromQueueIfPresent(npcName);

                monitor.Log(
                    $"[DialogueCoordinator] A2A 锁定 {npcName}，已清理 Bark 状态",
                    LogLevel.Trace);
            }
            catch (Exception ex)
            {
                monitor.Log(
                    $"[DialogueCoordinator] A2A 锁定回调异常：{npcName} | {ex.Message}",
                    LogLevel.Error);
            }
        };

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
        _helper.Events.Player.Warped += OnPlayerWarped; // 传送/地图切换时清理交互状态
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
        _helper.Events.Player.Warped -= OnPlayerWarped;

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

    /// <summary>
    /// 当前事件态下是否允许推进 Ambient Bark。
    /// 判据收敛到守卫层（VanillaInteractionGuard.IsFestivalRoam），
    /// 与 MainThreadOutputQueue 的 Bark 显示侧豁免共享同一实现，杜绝两侧门禁漂移。
    /// 非事件期恒为 true；事件期仅节日漫游态放行，心事件/剧情事件一律冻结。
    /// </summary>
    private static bool IsBarkAllowedInCurrentEvent()
    {
        if (!Game1.eventUp)
            return true;
        return VanillaInteractionGuard.IsFestivalRoam();
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
        // 阶段 2：交互拦截与状态跳变检测（基于发话者身份跃迁）
        // ════════════════════════════════════════════════════════════

        // 同时检测 dialogueUp 和 DialogueBox 菜单，捕获 dialogueUp 延迟设置的边界
        bool isDialogueActive = Game1.dialogueUp
            || Game1.activeClickableMenu is StardewValley.Menus.DialogueBox;
        string currentSpeaker = isDialogueActive ? Game1.currentSpeaker?.Name : null;

        // 发话者身份发生变化（包含：开始交谈、关闭对话、中途换人）
        if (!string.Equals(_activeInteractingSpeaker, currentSpeaker, StringComparison.OrdinalIgnoreCase))
        {
            // 旧角色离开对话态：施加 30s 冷却
            if (!string.IsNullOrEmpty(_activeInteractingSpeaker))
            {
                _ambientBark.NotifyPlayerInteracted(_activeInteractingSpeaker, cooldownSeconds: 30);
                _monitor.Log(
                    $"[DialogueCoordinator] {_activeInteractingSpeaker} 离开对话态，刷新 30s 冷却",
                    LogLevel.Trace);
            }

            // 新角色进入对话态：立即截断 Bark
            if (!string.IsNullOrEmpty(currentSpeaker))
            {
                _ambientBark.NotifyPlayerInteracted(currentSpeaker, cooldownSeconds: 30);
                _monitor.Log(
                    $"[DialogueCoordinator] {currentSpeaker} 进入对话态，打断 Bark 并锁定",
                    LogLevel.Trace);
            }

            _activeInteractingSpeaker = currentSpeaker;
        }

        // ★ 对话进行中：挂起所有推进逻辑
        if (isDialogueActive)
        {
            return;
        }

        // 菜单遮挡时挂起（eventUp 不再在此处一刀切，改由下方 isBarkAllowed 按节日/剧情分流）
        if (Game1.activeClickableMenu != null)
        {
            return;
        }

        // 当前事件态下是否允许推进 Ambient Bark（判据收敛到守卫层，与显示侧豁免共享实现）
        bool isBarkAllowed = IsBarkAllowedInCurrentEvent();

        // 节日漫游结束边沿：eventUp 从 true 回落 → 清理节日残留队列，打捞未播放台词
        if (_festivalRoamObserved && !Game1.eventUp)
        {
            _festivalRoamObserved = false;
            try
            {
                _ambientBark.OnFestivalRoamEnded();
            }
            catch (System.Exception ex)
            {
                _monitor.Log($"[DialogueCoordinator] FestivalRoamEnded error: {ex.Message}", LogLevel.Error);
            }
        }
        else if (Game1.eventUp && isBarkAllowed)
        {
            _festivalRoamObserved = true;
        }

        // ════════════════════════════════════════════════════════════
        // 阶段 3：世界推进（TickA2A 在此处运行，入睡检查在此处彻底卡死任何非法播放）
        // ════════════════════════════════════════════════════════════

        // 3. A2A Radar（节日内冻结，避免节日 A2A 活动）
        if (config.EnableA2A && !Game1.eventUp)
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

        // 4.5 Ambient Bark Radar（节日漫游放行，剧情事件静默）
        if (config.EnableAmbientBarks && isBarkAllowed)
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
        if (config.EnableAmbientBarks && isBarkAllowed)
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

        // 6. Ambient Bark State Tick（节日漫游放行，剧情事件静默；模块内部仍自查 Config）
        if (isBarkAllowed)
        {
            try
            {
                _ambientBark.TickStates();
            }
            catch (System.Exception ex)
            {
                _monitor.Log($"[DialogueCoordinator] AmbientBark TickStates error: {ex.Message}", LogLevel.Error);
            }
        }

        // 7. MainThreadOutputQueue.Process(20) (always called to drain queue，无 gate)
        try
        {
            _outputQueue.Process(20);
        }
        catch (System.Exception ex)
        {
            _monitor.Log($"[DialogueCoordinator] OutputQueue Process error: {ex.Message}", LogLevel.Error);
        }
    }

    private void OnPlayerWarped(object sender, StardewModdingAPI.Events.WarpedEventArgs e)
    {
        // 玩家传送/地图切换时，清理活跃交互状态
        // 防止 Event/传送导致 _activeInteractingSpeaker 悬空引用
        if (!string.IsNullOrEmpty(_activeInteractingSpeaker))
        {
            _monitor.Log(
                $"[DialogueCoordinator] 玩家传送（{e.OldLocation?.Name} → {e.NewLocation?.Name}），清理活跃交互状态：{_activeInteractingSpeaker}",
                LogLevel.Trace);

            _activeInteractingSpeaker = null;
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

        _festivalRoamObserved = false;
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

        // 清理状态跟踪字段
        _activeInteractingSpeaker = null;
        _festivalRoamObserved = false;
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