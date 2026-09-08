using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

namespace ValleytalkReborn;

/// <summary>
/// DynamicBarkManager 已降级为兼容门面。
/// 所有业务逻辑已迁移到 AmbientBarkModule、A2AModule 和对应的子服务中。
/// 本类仅保留静态转发入口，供 Harmony Patch 和其他遗留调用方使用。
/// </summary>
internal static partial class DynamicBarkManager
{
    // 常量已迁移到各模块，此处保留供 DynamicBarkManager 内部兼容（如内部常量引用）
    internal const int A2A_BREAK_RANGE_SQ = 25;
    internal const int A2A_PLAYBACK_MAX_SPREAD_SQ = 49;
    internal const int DISPLAY_RANGE_SQ = 100;

    /// <summary>
    /// 对 DialogueCoordinator 的静态引用，用于转发调用。
    /// 由 ModEntry 在装配 Coordinator 时设置。
    /// </summary>
    private static DialogueCoordinator _coordinator;

    /// <summary>
    /// 初始化兼容门面的内部引用。
    /// </summary>
    internal static void BindCoordinator(DialogueCoordinator coordinator)
    {
        _coordinator = coordinator;
    }

    // ══════════════════════════════════════════════════════════════
    //  生命周期入口（保留供旧调用方兼容）
    // ══════════════════════════════════════════════════════════════

    internal static void OnGameLaunched()
    {
        _coordinator?.AmbientBark.OnGameLaunched();
        _coordinator?.A2A.OnGameLaunched();
    }

    internal static void OnFollowStarted(NPC npc)
    {
        _coordinator?.AmbientBark.ResetForFollow(npc);
    }

    internal static void OnFollowEnded(string npcName)
    {
        _coordinator?.AmbientBark.ResetForNpc(npcName);
    }

    internal static void CancelBackgroundTasks(string npcName)
    {
        _coordinator?.AmbientBark.CancelForNpc(npcName);
    }

    internal static void OnDialogueDayStarted()
    {
        _coordinator?.ResetAllDialogueState("Legacy day-start entry");
    }

    internal static void OnDialogueReturnedToTitle()
    {
        _coordinator?.ResetAllDialogueState("Legacy returned-to-title entry");
    }

    internal static void OnAmbientBarkDayStarted()
    {
        // 清理逻辑已移至 OnDialogueDayStarted
    }

    internal static void OnAmbientBarkReturnedToTitle()
    {
        // 清理逻辑已移至 OnDialogueReturnedToTitle
    }

    internal static void OnAmbientBarkTick(UpdateTickedEventArgs e)
    {
        // 由 DialogueCoordinator 统一调度
    }

    internal static void OnA2ADayStarted()
    {
        // 清理逻辑已移至 OnDialogueDayStarted
    }

    internal static void OnA2AReturnedToTitle()
    {
        // 清理逻辑已移至 OnDialogueReturnedToTitle
    }

    internal static void OnA2ATick(UpdateTickedEventArgs e)
    {
        // 由 DialogueCoordinator 统一调度
    }

    internal static void OnOutputQueueProcess()
    {
        // 由 DialogueCoordinator 统一调度
    }

    // ══════════════════════════════════════════════════════════════
    //  A2A 公共 API（保留供 Harmony Patch 使用）
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// 取消指定 NPC 参与的所有 A2A 会话。
    /// 当玩家开始与 NPC 对话或送礼时调用，确保原版交互优先。
    /// </summary>
    internal static void CancelA2AForNpc(string npcName, string reason)
    {
        _coordinator?.A2A.SessionManager.CancelForNpc(npcName, reason);
        _coordinator?.AmbientBark.CancelForNpc(npcName);
    }

    /// <summary>
    /// 中断指定的 A2A 会话。
    /// </summary>
    internal static void InterruptA2ASession(
        DialogueModels.A2ASession session,
        string reason,
        bool applyHalfPersonalCooldown)
    {
        var mgr = _coordinator?.A2A.SessionManager;
        if (mgr == null || session == null) return;

        mgr.InterruptSession(session, reason, applyHalfPersonalCooldown);
    }

    /// <summary>
    /// 取消所有 A2A 会话。
    /// </summary>
    internal static void CancelAndClearAllA2ASessionsExternal(string reason)
    {
        _coordinator?.A2A.SessionManager.CancelAll(reason, applyCooldown: false);
    }

    /// <summary>
    /// 验证 A2A 输出是否仍然有效。
    /// </summary>
    internal static bool ValidateA2AOutput(string sessionId, int generation, string npcName)
    {
        return _coordinator?.A2A.SessionManager.ValidateA2AOutput(sessionId, generation, npcName) ?? false;
    }

    /// <summary>
    /// 获取下一个 A2A Generation 计数。
    /// </summary>
    internal static int NextA2AGeneration() =>
        _coordinator?.A2A.SessionManager.NextA2AGeneration() ?? 0;
}