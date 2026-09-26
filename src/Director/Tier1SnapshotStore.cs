// Tier1SnapshotStore.cs
// VT3-B — Tier 1 快照会话存储。
//
// 管理 Tier 1 块的会话级缓存：同一开放对话内续用快照，避免每代重渲染稳定前缀。
// 全部 Memory，不落 ModData，不跨存档。
//
// 复用优先级（TryReuseSession）：
//   1. active 续用：同 NPC + 同 branch + 同地图 → 刷新 LastActivityUtc，返回 true
//   2. branch/location 不匹配 → 退役该 active 记录到 closed（ClosedAt=now）
//   3. closed 软继承：≤30s + 同地图 + 同 branch → 移回 active，返回 true
//   4. 否则新 Guid，返回 false
//
// VT3-B-1 追加：active 路径加 location 校验（拦截漏关窗孤儿会话 + 玩家已换地图的边界）；
// SessionRecord 含 Branch 字段用于软继承 branch 校验。
//
// 线程模型：MonoGame 主线程单线程执行，所有调用点均在主线程，无需锁保护字典操作。

using System;
using System.Collections.Generic;
using System.Linq;
using StardewModdingAPI;
using StardewValley;

namespace ValleytalkReborn;

/// <summary>
/// Tier 1 快照会话存储。主线程访问。全部 Memory。
/// </summary>
internal static class Tier1SnapshotStore
{
    // ── 常量 ──
    private const int SoftReuseSeconds = 30;
    private const int OrphanMinutes = 5;
    private const int ClosedRetentionSeconds = 60;

    // ── 双索引：active 会话 ──
    private static readonly Dictionary<string, SessionRecord> _activeBySession = new(System.StringComparer.Ordinal);
    private static readonly Dictionary<string, SessionRecord> _activeByNpc = new(System.StringComparer.OrdinalIgnoreCase);

    // ── closed 软继承索引 ──
    private static readonly Dictionary<string, SessionRecord> _recentClosedSessions = new(System.StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 尝试复用指定 NPC 在指定 branch 下的 Tier 1 会话。
    /// 返回顺序：active 续用 → closed 软继承（30s）→ 新会话。
    /// </summary>
    public static bool TryReuseSession(Character character, InstructionsBranch branch, out string sessionId)
    {
        sessionId = string.Empty;

        if (character == null)
        {
            ModEntry.SMonitor?.Log("[Director] TryReuseSession: character is null, new session.", LogLevel.Debug);
            return false;
        }

        var npc = character.StardewNpc;
        if (npc == null)
        {
            ModEntry.SMonitor?.Log($"[Director] TryReuseSession: StardewNpc is null for {character.Name}, new session.", LogLevel.Debug);
            return false;
        }

        string npcName = character.Name;
        string currentLocation = npc.currentLocation?.NameOrUniqueName ?? npc.currentLocation?.Name ?? string.Empty;
        int currentGameDay = Context.IsWorldReady ? Game1.Date.TotalDays : 0;

        // ── 1. active 续用：同 NPC + 同 branch + 同地图 + 同游戏日 ──
        if (_activeByNpc.TryGetValue(npcName, out var active))
        {
            string activeLoc = active.LocationName ?? string.Empty;
            if (active.CreatedGameDay != currentGameDay)
            {
                RetireActiveRecord(active);
                ModEntry.SMonitor?.Log($"[Director] Session retired (day changed) for {npcName}", LogLevel.Debug);
            }
            else if (active.Branch == branch
                && string.Equals(activeLoc, currentLocation, StringComparison.Ordinal))
            {
                active.LastActivityUtc = DateTime.UtcNow;
                ModEntry.SMonitor?.Log($"[Director] Tier 1 session continued for {npcName}", LogLevel.Debug);
                sessionId = active.SessionId;
                return true;
            }
            else
            {
                // ── 2. branch/location 不匹配 → 退役 ──
                RetireActiveRecord(active);
            }
        }

        // ── 3. closed 软继承：≤30s + 同地图 + 同 branch + 同游戏日 ──
        if (_recentClosedSessions.TryGetValue(npcName, out var closed)
            && closed.ClosedAt.HasValue)
        {
            double elapsed = (DateTime.UtcNow - closed.ClosedAt.Value).TotalSeconds;
            string closedLoc = closed.LocationName ?? string.Empty;
            if (elapsed <= SoftReuseSeconds
                && elapsed >= 0
                && string.Equals(closedLoc, currentLocation, StringComparison.Ordinal)
                && closed.Branch == branch
                && closed.CreatedGameDay == currentGameDay)
            {
                _recentClosedSessions.Remove(npcName);
                closed.ClosedAt = null;
                closed.LastActivityUtc = DateTime.UtcNow;
                _activeBySession[closed.SessionId] = closed;
                _activeByNpc[npcName] = closed;
                ModEntry.SMonitor?.Log($"[Director] Tier 1 soft-reuse for {npcName}", LogLevel.Debug);
                sessionId = closed.SessionId;
                return true;
            }
        }

        // ── 4. 新会话 ──
        sessionId = Guid.NewGuid().ToString("N");
        return false;
    }

    /// <summary>
    /// 注册 active 会话（新会话或续用后首次写入快照时调用）。写入 active 双索引。
    /// </summary>
    public static void RegisterActiveSession(
        string sessionId,
        string npcName,
        string locationName,
        InstructionsBranch branch,
        Tier1SnapshotContext snapshot)
    {
        if (string.IsNullOrEmpty(sessionId))
            return;

        var record = new SessionRecord
        {
            SessionId = sessionId,
            NpcName = npcName ?? string.Empty,
            LocationName = locationName ?? string.Empty,
            Branch = branch,
            Snapshot = snapshot,
            LastActivityUtc = DateTime.UtcNow,
            ClosedAt = null,
            CreatedGameDay = Context.IsWorldReady ? Game1.Date.TotalDays : 0,
        };

        _activeBySession[sessionId] = record;
        _activeByNpc[record.NpcName] = record;
    }

    /// <summary>
    /// 按 sessionId 取快照。active 优先，closed 兜底；命中即刷新 LastActivityUtc。
    /// </summary>
    public static bool TryGetSnapshot(string sessionId, out Tier1SnapshotContext snapshot)
    {
        snapshot = null;
        if (string.IsNullOrEmpty(sessionId))
            return false;

        if (_activeBySession.TryGetValue(sessionId, out var active))
        {
            active.LastActivityUtc = DateTime.UtcNow;
            snapshot = active.Snapshot;
            return snapshot != null;
        }

        // closed 兜底（软继承窗口内仍可用）
        foreach (var kvp in _recentClosedSessions)
        {
            if (string.Equals(kvp.Value.SessionId, sessionId, StringComparison.Ordinal))
            {
                var closed = kvp.Value;
                if (closed.ClosedAt.HasValue
                    && (DateTime.UtcNow - closed.ClosedAt.Value).TotalSeconds <= SoftReuseSeconds)
                {
                    closed.LastActivityUtc = DateTime.UtcNow;
                    snapshot = closed.Snapshot;
                    return snapshot != null;
                }
                return false;
            }
        }

        return false;
    }

    /// <summary>
    /// 标记当前对话框关闭：将全部 active 记录转 closed（ClosedAt=now），同步维护双索引。
    /// </summary>
    public static void MarkCurrentDialogueClosed()
    {
        if (_activeByNpc.Count == 0)
            return;

        foreach (var kvp in _activeByNpc.ToList())
        {
            var record = kvp.Value;
            record.ClosedAt = DateTime.UtcNow;
            _activeBySession.Remove(record.SessionId);
            _recentClosedSessions[kvp.Key] = record;
            ModEntry.SMonitor?.Log($"[Director] Tier 1 session closed for {record.NpcName}", LogLevel.Debug);
        }
        _activeByNpc.Clear();
    }

    /// <summary>
    /// 周期性清理（每 60 帧调用）：超 5 分钟未活动的 active 会话直接驱逐并删除
    /// （不转 closed）；closed 超 60 秒彻底删除。
    /// </summary>
    public static void PeriodicCleanup()
    {
        var now = DateTime.UtcNow;
        var orphanThreshold = TimeSpan.FromMinutes(OrphanMinutes);
        var closedRetention = TimeSpan.FromSeconds(ClosedRetentionSeconds);

        // ── 驱逐 orphan active 会话（直接删除，不转 closed）──
        var orphanSessionIds = new List<string>();
        foreach (var kvp in _activeBySession)
        {
            if (now - kvp.Value.LastActivityUtc > orphanThreshold)
                orphanSessionIds.Add(kvp.Key);
        }
        foreach (var sid in orphanSessionIds)
        {
            if (_activeBySession.TryGetValue(sid, out var rec))
            {
                _activeBySession.Remove(sid);
                _activeByNpc.Remove(rec.NpcName);
                ModEntry.SMonitor?.Log($"[Director] Orphan session evicted: {sid}", LogLevel.Debug);
            }
        }

        // ── 彻底删除超时的 closed 记录 ──
        var expiredClosedNpcs = new List<string>();
        foreach (var kvp in _recentClosedSessions)
        {
            if (kvp.Value.ClosedAt.HasValue && now - kvp.Value.ClosedAt.Value > closedRetention)
                expiredClosedNpcs.Add(kvp.Key);
        }
        foreach (var npc in expiredClosedNpcs)
        {
            _recentClosedSessions.Remove(npc);
        }
    }

    // ── Gossip 会话级冻结 ──

    /// <summary>
    /// 取指定会话的冻结 Gossip。会话存在且 CachedGossip 不为 null 时返回 true
    /// （gossip 可为 string.Empty）；会话无效 / 无活跃记录 / CachedGossip 仍为 null 返回 false。
    /// </summary>
    public static bool TryGetSessionGossip(string sessionId, out string gossip)
    {
        gossip = null;

        if (string.IsNullOrEmpty(sessionId))
        {
            ModEntry.SMonitor?.Log("[Director] TryGetSessionGossip: empty session id, invalid gossip lookup.", LogLevel.Warn);
            return false;
        }

        if (_activeBySession.TryGetValue(sessionId, out var record) && record.CachedGossip != null)
        {
            gossip = record.CachedGossip;
            return true;
        }

        return false;
    }

    /// <summary>
    /// 写入指定活跃会话的冻结 Gossip。null 归一化为 string.Empty。
    /// 不创建新记录；空 sessionId → Warn；无活跃记录 → Error（违反注册契约）。
    /// </summary>
    public static void SetSessionGossip(string sessionId, string gossip)
    {
        if (string.IsNullOrEmpty(sessionId))
        {
            ModEntry.SMonitor?.Log("[Director] SetSessionGossip: empty session id, no state mutated.", LogLevel.Warn);
            return;
        }

        if (!_activeBySession.TryGetValue(sessionId, out var record))
        {
            ModEntry.SMonitor?.Log("[Director] SetSessionGossip: no active session found for id " + sessionId, LogLevel.Error);
            return;
        }

        record.CachedGossip = gossip ?? string.Empty;
    }

    /// <summary>
    /// 清空全部 Memory 会话状态（active / closed）。标题期/读档/跨天生命周期重置。
    /// </summary>
    public static void ClearAll()
    {
        _activeBySession.Clear();
        _activeByNpc.Clear();
        _recentClosedSessions.Clear();
    }

    // ── helpers ──

    private static void RetireActiveRecord(SessionRecord record)
    {
        _activeBySession.Remove(record.SessionId);
        _activeByNpc.Remove(record.NpcName);
        record.ClosedAt = DateTime.UtcNow;
        _recentClosedSessions[record.NpcName] = record;
    }

    /// <summary>
    /// 私有会话记录。含 Branch 字段用于软继承 branch 校验（M4），
    /// CreatedGameDay 用于游戏日守卫（VT3-B-2）。
    /// </summary>
    private sealed class SessionRecord
    {
        public string SessionId { get; init; } = string.Empty;
        public string NpcName { get; init; } = string.Empty;
        public string LocationName { get; init; } = string.Empty;
        public InstructionsBranch Branch { get; init; }
        public Tier1SnapshotContext Snapshot { get; set; }
        public DateTime LastActivityUtc { get; set; }
        public DateTime? ClosedAt { get; set; }
        public int CreatedGameDay { get; init; } = 0;

        /// <summary>
        /// Gossip 脉冲的会话级冻结副本。null = 本会话尚未探测候选池；
        /// string.Empty = 本会话已探测但无可用候选；非空 = 本会话的冻结 gossip 文本。
        /// Memory-only；由 BuildPlan 在会话首次构建 gossip 时写入。
        /// </summary>
        public string CachedGossip { get; set; } = null;
    }
}
