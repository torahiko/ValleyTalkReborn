// Tier1SnapshotStore.cs
// VT3-B — Tier 1 快照会话存储。
//
// 管理 Tier 1 块的会话级缓存：同一开放对话内续用快照，避免每代重渲染稳定前缀。
// 全部 Memory，不落 ModData，不跨存档。
//
// 复用优先级（TryReuseSession，全部在 _lock 内）：
//   1. active 续用：同 NPC + 同 branch + 同地图 → 刷新 LastActivityUtc，返回 true
//   2. branch/location 不匹配 → 退役该 active 记录到 closed（ClosedAt=now）
//   3. closed 软继承：≤30s + 同地图 + 同 branch → 移回 active，返回 true
//   4. 否则新 Guid，返回 false
//
// VT3-B-1 追加：active 路径加 location 校验（拦截漏关窗孤儿会话 + 玩家已换地图的边界）；
// SessionRecord 含 Branch 字段用于软继承 branch 校验。

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

    private static readonly object _lock = new();

    // ── 双索引：active 会话 ──
    private static readonly Dictionary<string, SessionRecord> _activeBySession = new(System.StringComparer.Ordinal);
    private static readonly Dictionary<string, SessionRecord> _activeByNpc = new(System.StringComparer.OrdinalIgnoreCase);

    // ── closed 软继承索引 ──
    private static readonly Dictionary<string, SessionRecord> _recentClosedSessions = new(System.StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// 尝试复用指定 NPC 在指定 branch 下的 Tier 1 会话。
    /// 返回顺序：active 续用 → closed 软继承（30s）→ 新会话。全部判定在 _lock 内。
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

        lock (_lock)
        {
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

        lock (_lock)
        {
            _activeBySession[sessionId] = record;
            _activeByNpc[record.NpcName] = record;
        }
    }

    /// <summary>
    /// 按 sessionId 取快照。active 优先，closed 兜底；命中即刷新 LastActivityUtc。
    /// </summary>
    public static bool TryGetSnapshot(string sessionId, out Tier1SnapshotContext snapshot)
    {
        snapshot = null;
        if (string.IsNullOrEmpty(sessionId))
            return false;

        lock (_lock)
        {
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
    }

    /// <summary>
    /// 标记当前对话框关闭：将全部 active 记录转 closed（ClosedAt=now），同步维护双索引。
    /// </summary>
    public static void MarkCurrentDialogueClosed()
    {
        lock (_lock)
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

        lock (_lock)
        {
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
    }
}
