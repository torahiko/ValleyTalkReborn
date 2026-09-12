using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using StardewModdingAPI;
using StardewValley;

namespace ValleytalkReborn;

/// <summary>
/// A2A 会话管理器：负责 A2A 会话的完整生命周期。
/// 不访问 AmbientBarkStateStore，不直接清理 Bark Queue。
/// 只能通过 NpcReservationService 与 Bark 模块形成资源互斥。
/// </summary>
internal sealed class A2ASessionManager
{
    private const int A2A_RADAR_RANGE_TILES = 12;
    private const int A2A_RADAR_RANGE_SQ = A2A_RADAR_RANGE_TILES * A2A_RADAR_RANGE_TILES;
    private const int A2A_STARE_TICKS_REQUIRED = 180;
    private const int A2A_PAIR_RANGE_SQ = 9;
    private const int A2A_GROUP_RANGE_SQ = 16;
    internal const int A2A_BREAK_RANGE_SQ = 25;
    internal const int A2A_PLAYBACK_MAX_SPREAD_SQ = 49;
    private const int A2A_SPEAK_INTERVAL_TICKS = 240;
    private const int A2A_COOLDOWN_TICKS = 7200;
    private const int A2A_PERSONAL_COOLDOWN_TICKS = 1800;
    private const int A2A_MAX_PARTICIPANTS = 4;
    private const int A2A_ROUNDS_PER_SPEAKER = 2;
    private const int A2A_ROUNDS_TWO_PERSON = 6;
    private const int A2A_IDLE_TIMEOUT_TICKS = 600;
    private const int RADAR_INTERVAL_TICKS = 60;

    /// <summary>
    /// A2A 会话结束（无论正常完结还是被打断）后，参与者进入的
    /// "冷静期" —— 阻止 AmbientBark 雷达在此期间把刚结束对话的
    /// NPC 立刻重新拉进单人 Bark 请求队列，避免"话音刚落瞬间又开始
    /// 自言自语"的割裂感。15 秒 = 900 ticks。
    /// 注意：这不影响 A2A 自身的重新配对——如果参与者仍在 A2A 雷达
    /// 范围内聚集，A2A 会照常重新走凝视流程；此冷却只挡 AmbientBark
    /// 这一侧的雷达扫描。
    /// </summary>
    private const int A2A_POST_SESSION_COOLDOWN_TICKS = 900;

    /// <summary>
    /// 被打断的话尾可被"续接"请求引用的有效窗口（游戏分钟）。
    /// 超过此窗口或跨天后，话尾按过期处理，不再触发续接注入。
    /// 120 游戏分钟 = 2 游戏小时。
    /// </summary>
    private const int A2A_RESUME_WINDOW_GAME_MINUTES = 120;

    private readonly NpcReservationService _reservations;
    private readonly MainThreadOutputQueue _outputQueue;
    private readonly LlmRequestGateway _llmGateway;
    private readonly ModConfig _config;
    private readonly A2APromptBuilder _promptBuilder;

    private readonly List<DialogueModels.A2ASession> _activeA2ASessions = new List<DialogueModels.A2ASession>();
    private readonly Dictionary<string, int> _a2aCooldowns = new Dictionary<string, int>();
    private readonly Dictionary<string, int> _a2aPersonalCooldowns = new Dictionary<string, int>();

    /// <summary>
    /// NPC 名 → 剩余 ticks。存在于此字典中即代表该 NPC 仍处于
    /// A2A 结束后的 AmbientBark 冷静期内。
    /// </summary>
    private readonly Dictionary<string, int> _a2aPostSessionCooldowns = new Dictionary<string, int>();

    /// <summary>
    /// 每对 NPC 上一轮会话的最后一句台词，用于跨轮防复读注入。
    /// Key 为 pairKey（与 _a2aCooldowns 相同），Value 为尾句文本。
    /// 换天时清空。
    /// </summary>
    private readonly Dictionary<string, string> _a2aPreviousTopics = new Dictionary<string, string>();

    /// <summary>
    /// 被打断会话的"话尾"暂存：pairKey → (录制日, 录制时刻, 尾句)。
    /// 仅当会话播放过 ≥2 句后被中断才写入；自然收尾不写。
    /// 派发时一次性消费（consume-once），跨天/超窗口自动失效。
    /// 换天与回标题时清空，纯 Memory 作用域，不跨存档。
    /// </summary>
    private readonly Dictionary<string, (int Day, int TimeOfDay, string Tail)> _a2aInterruptedTails = new();

    private int _a2aSessionGeneration = 0;

    private readonly ConcurrentQueue<DialogueModels.A2ACompletedResult> _pendingA2AResults =
        new ConcurrentQueue<DialogueModels.A2ACompletedResult>();

    private int _radarCooldown = 0;

    /// <summary>
    /// 当 NPC 被 A2A 锁定（凝视完成）时触发，用于通知外部模块清理该 NPC 的单人状态。
    /// </summary>
    internal Action<string> OnNpcA2ALocked { get; set; }

    internal A2ASessionManager(
        NpcReservationService reservations,
        MainThreadOutputQueue outputQueue,
        LlmRequestGateway llmGateway,
        ModConfig config,
        A2APromptBuilder promptBuilder)
    {
        _reservations = reservations;
        _outputQueue = outputQueue;
        _llmGateway = llmGateway;
        _config = config;
        _promptBuilder = promptBuilder;
    }

    /// <summary>
    /// 获取下一个 A2A Generation 计数。
    /// </summary>
    internal int NextA2AGeneration() =>
        Interlocked.Increment(ref _a2aSessionGeneration);

    /// <summary>
    /// 是否存在正在播放的 A2A 会话。
    /// </summary>
    internal bool HasActivePlayback()
    {
        return _activeA2ASessions.Any(s => s.MembersLocked);
    }

    /// <summary>
    /// 该 NPC 是否仍处于 A2A 会话结束后的 AmbientBark 冷静期内。
    /// 供 AmbientBarkModule 在雷达扫描阶段查询，阻止刚结束 A2A 的
    /// NPC 立刻被重新纳入单人 Bark 候选。
    /// </summary>
    internal bool IsInPostSessionCooldown(string npcName)
    {
        return !string.IsNullOrWhiteSpace(npcName)
            && _a2aPostSessionCooldowns.ContainsKey(npcName);
    }

    /// <summary>
    /// A2A  Tick 主入口（不含结果处理）。
    /// </summary>
    internal void Tick()
    {
        if (!_config.EnableA2A)
        {
            // 如果 A2A 被禁用，清理所有会话但不推进 Tick
            CancelAll("A2A disabled", applyCooldown: false);
            return;
        }

        TickA2ACooldowns();
        TickA2ASessions();
    }

    /// <summary>
     /// 推进雷达冷却并触发 A2A 聚集扫描。
     /// </summary>
    internal void TickRadarCooldown()
    {
        if (!_config.EnableA2A) return;

        _radarCooldown--;
        if (_radarCooldown <= 0)
        {
            _radarCooldown = RADAR_INTERVAL_TICKS;
            PerformRadarScan();
        }
    }

    /// <summary>
    /// 处理 A2A LLM 回传结果。
    /// </summary>
    internal void ProcessPendingResults()
    {
        while (_pendingA2AResults.TryDequeue(out var result))
        {
            if (result == null || string.IsNullOrWhiteSpace(result.SessionId))
                continue;

            var session = _activeA2ASessions.FirstOrDefault(s => s.SessionId == result.SessionId);

            if (session == null || session.IsCancelled)
            {
                continue;
            }

            if (!session.HasAllParticipantsResolved())
            {
                session.FinishRequest();
                session.RequestCts = null;
                continue;
            }

            var participants = session.ResolveParticipants();

            if (participants.Any(n => DialogueUtilities.IsNpcSleeping(n)))
            {
                // ★ 优化：直接调用 Interrupt，不留悬空垃圾
                // 若回传到达时成员已入睡，立即销毁会话释放资源，
                // 避免对话期间会话以"占有"状态悬挂在内存中。
                InterruptA2ASession(session, "LLM结果到达时成员已入睡，会话取消", applyHalfPersonalCooldown: false, stashInterruptedTail: true);
                continue;
            }

            if (result.EndReason == DialogueModels.LlmRequestEndReason.Cancelled || result.Cancelled)
            {
                ModEntry.SMonitor?.Log(
                    $"[A2A] 取消态结果到达存活会话，按 fallback 处理：{string.Join(" & ", session.ParticipantNames)}",
                    LogLevel.Trace);
            }

            DialogueModels.A2ALine[] lines = null;

            if (!result.UseFallback && !string.IsNullOrWhiteSpace(result.RawJson))
            {
                // 变更说明：不再由调用方先用 ExtractJsonArrayString 预截取一次。
                // ParseA2AScript 内部（TryDeserializeSpeakerLineArray）已经会
                // 在完整原始文本上做"先整体尝试反序列化，失败则逐个扫描候选
                // 片段，闭合但内容不对就继续找下一个"的全套流程，是比这里
                // 更完整、更健壮的实现。
                // 若仍在这里先截一次，等于把候选范围提前锁死在"第一个闭合的
                // 方括号片段"——一旦模型在真正脚本前面输出了一段无关但恰好
                // 闭合的方括号文本（思考过程、编号提示等），真正脚本会被裁
                // 在这一刀之外，内部扫描再强健也够不到。
                // 因此直接把完整原始文本交给 ParseA2AScript，避免候选范围
                // 被过早裁窄。
                lines = DialogueParsing.ParseA2AScript(result.RawJson, participants);

                if (lines == null || lines.Length == 0)
                {
                    string parseNames = string.Join(" & ", session.ParticipantNames);
                    ModEntry.SMonitor?.Log(
                        $"[A2A] 脚本解析失败，使用 fallback：{parseNames} | RawJson={result.RawJson}",
                        LogLevel.Warn);
                }
            }

            if (lines == null || lines.Length == 0)
                lines = BuildFallbackScript(session.ParticipantNames, result.IsChinese);

            if (!A2AScriptValidator.TryValidate(
                lines,
                session.ParticipantNames,
                session.RoundsLeft,
                out var validLines))
            {
                validLines = BuildFallbackScript(
                    session.ParticipantNames,
                    result.IsChinese);
            }

            foreach (var line in validLines)
                session.Script.Enqueue(line);

            session.RoundsLeft = session.Script.Count;
            session.FinishRequest();
            session.RequestCts = null;

            string names = string.Join(" & ", session.ParticipantNames);
            ModEntry.SMonitor?.Log(
                $"[A2A] 脚本就绪，共 {session.Script.Count} 条：{names}",
                LogLevel.Debug);
        }
    }

    /// <summary>
    /// 取消指定 NPC 参与的所有 A2A 会话。
    /// </summary>
    internal void CancelForNpc(string npcName, string reason)
    {
        if (string.IsNullOrWhiteSpace(npcName))
            return;

        var sessions = _activeA2ASessions
            .Where(session => session.ParticipantNames.Contains(
                npcName,
                StringComparer.OrdinalIgnoreCase))
            .ToList();

        foreach (var session in sessions)
        {
            InterruptA2ASession(session, reason, applyHalfPersonalCooldown: true, stashInterruptedTail: true);
        }
    }

    /// <summary>
    /// 取消所有 A2A 会话。
    /// </summary>
    internal void CancelAll(string reason, bool applyCooldown)
    {
        foreach (var session in _activeA2ASessions.ToList())
        {
            InterruptA2ASession(session, reason, applyHalfPersonalCooldown: applyCooldown, stashInterruptedTail: true);
        }
    }

    /// <summary>
    /// 当原版交互激活时中断所有播放中的 A2A 会话。
    /// 由 DialogueCoordinator 在提前返回前调用。
    /// </summary>
    internal void InterruptOnVanillaInteraction()
    {
        if (VanillaInteractionGuard.HasActiveVanillaInteraction())
        {
            CancelAll("Vanilla interaction started", applyCooldown: false);
        }
    }

    /// <summary>
    /// 换天清理。
    /// </summary>
    internal void OnDayStarted()
    {
        CancelAll("Day started", applyCooldown: false);
        _a2aCooldowns.Clear();
        _a2aPersonalCooldowns.Clear();
        _a2aPostSessionCooldowns.Clear();
        _a2aPreviousTopics.Clear();
        _a2aInterruptedTails.Clear();

        // 排空异步回传残留，防止次日处理到前日滞留的过期结果
        while (_pendingA2AResults.TryDequeue(out _)) { }

        // 重置跨存档静态八卦缓存，避免旧存档的"当日已聊过"标记污染新日
        A2APromptBuilder.ResetGossipCache();
    }

    /// <summary>
    /// 返回标题清理。
    /// </summary>
    internal void OnReturnedToTitle()
    {
        CancelAll("Returned to title", applyCooldown: false);
        _a2aCooldowns.Clear();
        _a2aPersonalCooldowns.Clear();
        _a2aPostSessionCooldowns.Clear();
        _a2aPreviousTopics.Clear();
        _a2aInterruptedTails.Clear();

        while (_pendingA2AResults.TryDequeue(out _)) { }
        A2APromptBuilder.ResetGossipCache();
    }

    /// <summary>
    /// 验证 A2A 输出是否仍然有效。
    /// </summary>
    internal bool ValidateA2AOutput(string sessionId, int generation, string npcName)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
            return false;

        var session = _activeA2ASessions.FirstOrDefault(s =>
            s.SessionId == sessionId &&
            s.ParticipantNames.Contains(npcName, StringComparer.OrdinalIgnoreCase));

        if (session == null)
            return false;

        if (session.IsCancelled)
            return false;

        if (session.IsEnding)
            return false;

        if (session.Generation != generation)
            return false;

        if (VanillaInteractionGuard.HasActiveVanillaInteraction())
            return false;

        return true;
    }

    private void TickA2ACooldowns()
    {
        TickCooldownDict(_a2aCooldowns);
        TickCooldownDict(_a2aPersonalCooldowns);
        TickCooldownDict(_a2aPostSessionCooldowns);
    }

    private static void TickCooldownDict(Dictionary<string, int> dict)
    {
        if (dict.Count == 0) return;

        List<string> toRemove = null;

        foreach (var key in dict.Keys.ToList())
        {
            dict[key]--;

            if (dict[key] <= 0)
                (toRemove ??= new List<string>()).Add(key);
        }

        if (toRemove != null)
        {
            foreach (var key in toRemove)
                dict.Remove(key);
        }
    }

    private void TickA2ASessions()
    {
        for (int i = _activeA2ASessions.Count - 1; i >= 0; i--)
        {
            var session = _activeA2ASessions[i];
            var participants = session.ResolveParticipants();

            if (participants.Count == 0 || participants.Any(n => DialogueUtilities.IsNpcSleeping(n)))
            {
                InterruptA2ASession(session, "会话成员已入睡，会话取消", applyHalfPersonalCooldown: true, stashInterruptedTail: true);
                continue;
            }

            if (!session.MembersLocked)
            {
                // ★ 挂起保护：如果玩家正在与其中某人对话，冻结凝视超时计时，不中断聚集
                if (Game1.dialogueUp || Game1.activeClickableMenu is StardewValley.Menus.DialogueBox)
                {
                    continue;
                }

                session.IdleTicks++;

                if (!session.AllInStareRange() || session.IdleTicks > A2A_IDLE_TIMEOUT_TICKS)
                {
                    InterruptA2ASession(session, "凝视期成员离开或闲置超时，聚集取消", applyHalfPersonalCooldown: false, stashInterruptedTail: true);
                }

                continue;
            }

            // ★ 播放/请求期物理破裂判定（移除了 Game1.dialogueUp 销毁触发）
            if (session.ShouldBreakDuringPlayback() || session.AllParticipantsOutOfDisplayRange())
            {
                InterruptA2ASession(session, "会话成员走散或离开视野，会话中断", applyHalfPersonalCooldown: true, stashInterruptedTail: true);
                continue;
            }

            // ★ 核心挂起检测：玩家正处于对话中时，会话保持存活，仅冻结播放推进
            if (Game1.dialogueUp || Game1.activeClickableMenu is StardewValley.Menus.DialogueBox)
            {
                // 确保对话结束后有约 1.5 秒（90 ticks）的自然转场停顿，避免关掉对话框瞬间秒冒字
                if (session.ReadCooldownTicks < 90)
                {
                    session.ReadCooldownTicks = 90;

                    // ★ 首次挂起时记录日志（ReadCooldownTicks 从小于 90 跳变到 90 的瞬间）
                    string names = string.Join(" & ", session.ParticipantNames);
                    ModEntry.SMonitor?.Log(
                        $"[A2A] 会话挂起（玩家对话中）：{names}",
                        LogLevel.Trace);
                }
                continue; // 跳过本 Tick，静默等待玩家关闭对话框
            }

            if (session.RoundsLeft <= 0 && session.Script.Count == 0)
            {
                string pairKey = DialogueUtilities.MakePairKey(session.ParticipantNames);
                // 2 人配对拉长冷却；3+ 人群体维持原时长（群体再聚集概率低，保留活跃度）
                _a2aCooldowns[pairKey] = session.ParticipantNames.Count == 2 ? 14400 : 7200;

                foreach (var name in session.ParticipantNames)
                {
                    _a2aPersonalCooldowns[name] = A2A_PERSONAL_COOLDOWN_TICKS;
                }

                // 正常自然收尾——移交最后展示过的台词给 ImmediateEchoStore，
                // 供玩家紧接着点开任一参与者时还原"刚聊完"的现场感。
                // 注意：只在这个分支（正常结束）记录，被打断/取消的路径
                // 一律不记，理由见 ImmediateEchoStore 类注释。
                if (session.RecentSpokenLines.Count > 0)
                {
                    string locationName = participants.FirstOrDefault()?.currentLocation?.Name;
                    ImmediateEchoStore.RecordA2A(
                        session.ParticipantNames,
                        session.RecentSpokenLines,
                        locationName);

                    // 记录尾句供下轮防复读使用（只在正常结束路径写入，打断路径不写）
                    string pairKeyForTopic = DialogueUtilities.MakePairKey(session.ParticipantNames);
                    _a2aPreviousTopics[pairKeyForTopic] = session.RecentSpokenLines.Last().Item2;
                }

                InterruptA2ASession(session, "会话结束", applyHalfPersonalCooldown: false, stashInterruptedTail: false);
                continue;
            }

            if (session.Script.Count == 0)
            {
                if (session.TryStartRequest())
                {
                    string pairKeyForPrev = DialogueUtilities.MakePairKey(session.ParticipantNames);

                    // 续接优先：存在未过期的被打断话尾时，注入续接行并跳过防复读；
                    // 否则走既有跨轮防复读路径（读 _a2aPreviousTopics）。
                    string resumeTail = TryConsumeInterruptedTail(pairKeyForPrev);
                    string prevTopic = null;
                    if (resumeTail == null)
                        _a2aPreviousTopics.TryGetValue(pairKeyForPrev, out prevTopic);

                    var request = _promptBuilder.Build(session, prevTopic, resumeTail);

                    if (request == null)
                    {
                        foreach (var line in BuildFallbackScript(session.ParticipantNames, IsChineseLanguage))
                            session.Script.Enqueue(line);

                        session.RoundsLeft = session.Script.Count;
                        session.FinishRequest();
                    }
                    else
                    {
                        session.RequestCts = new CancellationTokenSource();
                        var token = session.RequestCts.Token;
                        _ = Task.Run(() => FetchA2AScriptAsync(session.SessionId, request, token));
                        // ★ 修复：长文本上下文受 Config.Debug 门控保护，未开启时降级为简短状态行
                        if (_config?.Debug == true)
                        {
                            ModEntry.SMonitor?.Log(
                                $"[A2A] 发送脚本请求：{request.NamesLog}\n--- System Prompt ---\n{request.SystemPrompt}\n--- User Prompt ---\n{request.UserPrompt}",
                                LogLevel.Debug);
                        }
                        else
                        {
                            ModEntry.SMonitor?.Log($"[A2A] 发送脚本请求：{request.NamesLog}", LogLevel.Trace);
                        }
                    }
                }

                continue;
            }

            if (session.ReadCooldownTicks > 0)
            {
                session.ReadCooldownTicks--;
                continue;
            }

            if (session.Script.Count > 0)
            {
                var line = session.Script.Dequeue();
                session.RoundsLeft--;

                if (line == null || string.IsNullOrWhiteSpace(line.SpeakerName))
                {
                    session.ReadCooldownTicks = A2A_SPEAK_INTERVAL_TICKS;
                    continue;
                }

                var npc = Game1.getCharacterFromName(line.SpeakerName);

                if (npc != null
                    && Game1.player != null
                    && npc.currentLocation == Game1.player.currentLocation
                    && !DialogueUtilities.IsNpcSleeping(npc)
                    && DialogueUtilities.IsInRangeSquared(npc, (Farmer)Game1.player, DialogueConstants.DisplayRangeSquared))
                {
                    _outputQueue.EnqueueA2A(
                        session.SessionId,
                        session.Generation,
                        npc.Name,
                        line.Line,
                        3500);

                    session.RecentSpokenLines.Enqueue((npc.Name, line.Line));
                    if (session.RecentSpokenLines.Count > 2)
                        session.RecentSpokenLines.Dequeue();
                }
                else
                {
                    ModEntry.SMonitor?.Log(
                        $"[A2A] {npc?.Name ?? line.SpeakerName} 离场或不在视野，跳过该句",
                        LogLevel.Trace);
                }

                session.ReadCooldownTicks = A2A_SPEAK_INTERVAL_TICKS;
            }
        }
    }

    private void PerformRadarScan()
    {
        if (Game1.player == null) return;

        var allNpcs = Game1.currentLocation?.characters;
        if (allNpcs == null || allNpcs.Count == 0) return;

        var a2aNearbyNpcs = new List<NPC>();

        // ★ 排除玩家正在对话的 NPC（防止 A2A 拉入正在与玩家交谈的对象）
        string currentSpeakerName = Game1.currentSpeaker?.Name;

        foreach (var npc in allNpcs)
        {
            if (npc == null || DialogueUtilities.IsNpcSleeping(npc) || !npc.IsVillager)
                continue;

            // ★ 排除玩家正在对话的 NPC
            if (!string.IsNullOrEmpty(currentSpeakerName)
                && string.Equals(npc.Name, currentSpeakerName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (DialogueUtilities.IsInRangeSquared(npc, (Farmer)Game1.player, A2A_RADAR_RANGE_SQ))
                a2aNearbyNpcs.Add(npc);
        }

        var visited = new HashSet<int>();
        var clusters = new List<List<NPC>>();

        for (int i = 0; i < a2aNearbyNpcs.Count; i++)
        {
            if (visited.Contains(i)) continue;

            var cluster = new List<NPC> { a2aNearbyNpcs[i] };
            visited.Add(i);

            for (int j = i + 1; j < a2aNearbyNpcs.Count; j++)
            {
                if (visited.Contains(j)) continue;

                bool canJoin = cluster.Count == 1
                    ? cluster.Any(m => DialogueUtilities.IsInRangeSquared(m, a2aNearbyNpcs[j], A2A_PAIR_RANGE_SQ))
                    : cluster.Any(m => DialogueUtilities.IsInRangeSquared(m, a2aNearbyNpcs[j], A2A_GROUP_RANGE_SQ));

                if (canJoin)
                {
                    cluster.Add(a2aNearbyNpcs[j]);
                    visited.Add(j);
                }
            }

            if (cluster.Count >= 2)
                clusters.Add(cluster);
        }

        foreach (var cluster in clusters)
            TryEstablishA2ASession(cluster);
    }

    private void TryEstablishA2ASession(List<NPC> cluster)
    {
        if (cluster == null || cluster.Count < 2)
            return;

        var clusterNames = cluster
            .Where(n => n != null)
            .Select(n => n.Name)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

        // 查找是否已有包含这组 NPC 的未锁定凝视会话
        var existingSession = _activeA2ASessions.FirstOrDefault(session =>
        {
            if (session.MembersLocked)
                return false;
            var sessionNames = session.ParticipantNames
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();
            return sessionNames.SequenceEqual(
                clusterNames,
                StringComparer.OrdinalIgnoreCase);
        });

        if (existingSession != null)
        {
            existingSession.IdleTicks = 0;
            existingSession.StareTicks += RADAR_INTERVAL_TICKS;

            // 只在 MembersLocked 从 false → true 的瞬间记录一次
            if (existingSession.StareTicks >= A2A_STARE_TICKS_REQUIRED
                && !existingSession.MembersLocked)
            {
                existingSession.MembersLocked = true;
                // 通知外部模块（如 AmbientBark）清理被锁定 NPC 的单人状态
                if (OnNpcA2ALocked != null)
                {
                    foreach (var npcName in existingSession.ParticipantNames)
                        OnNpcA2ALocked.Invoke(npcName);
                }
                string names = string.Join(" & ", existingSession.ParticipantNames);
                ModEntry.SMonitor?.Log(
                    $"[A2A] 凝视完成，成员锁定：{names}，准备交谈。",
                    LogLevel.Debug);
            }
            else
            {
                ModEntry.SMonitor?.Log(
                    $"[A2A] stare progress: {existingSession.StareTicks}/{A2A_STARE_TICKS_REQUIRED}",
                    LogLevel.Trace);
            }
            return;
        }

        var candidateNpcs = cluster
            .Where(n =>
            {
                if (n == null)
                    return false;

                // 排除个人冷却期内的 NPC
                if (_a2aPersonalCooldowns.ContainsKey(n.Name))
                    return false;

                // 排除已被其他模块占用的 NPC（包括 AmbientBark、其他 A2A 会话）
                if (_reservations.IsReserved(n.Name))
                    return false;

                // ★ 排除玩家正在对话的 NPC（二次防御）
                if (Game1.currentSpeaker != null
                    && string.Equals(n.Name, Game1.currentSpeaker.Name, StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }

                return true;
            })
            .ToList();

        if (candidateNpcs.Count < 2)
            return;

        // 与旧版对齐：排除已属于其他未锁定会话的成员，避免同一 NPC 处于多个未锁定会话
        var availableNpcs = candidateNpcs
            .Where(n => !_activeA2ASessions.Any(s =>
                s.ParticipantNames.Contains(n.Name, StringComparer.OrdinalIgnoreCase)))
            .ToList();

        if (availableNpcs.Count < 2)
            return;

        int maxParticipants = Math.Max(
            2,
            Math.Min(4, _config.A2AMaxParticipants));

        if (availableNpcs.Count > maxParticipants)
        {
            availableNpcs = availableNpcs
                .OrderBy(n => DialogueUtilities.DistanceSqToPlayer(n))
                .Take(maxParticipants)
                .ToList();
        }

        string pairKey = DialogueUtilities.MakePairKey(availableNpcs.Select(n => n.Name));
        if (_a2aCooldowns.TryGetValue(pairKey, out int remaining) && remaining > 0)
            return;

        int candidateCount = availableNpcs.Count;
        int rounds = candidateCount == 2
            ? A2A_ROUNDS_TWO_PERSON
            : candidateCount * A2A_ROUNDS_PER_SPEAKER;

        var sessionId = Guid.NewGuid().ToString("N");
        var session = new DialogueModels.A2ASession
        {
            SessionId = sessionId,
            Generation = NextA2AGeneration(),
            StareTicks = 0,
            IdleTicks = 0,
            RoundsLeft = rounds,
            ReservationOwner =
                DialogueModels.A2ASession.GetReservationOwner(sessionId)
        };

        var reservedNames = new List<string>();
        bool allAvailable = true;

        foreach (var npc in availableNpcs)
        {
            if (!_reservations.TryReserve(npc.Name, session.ReservationOwner))
            {
                allAvailable = false;
                break;
            }
            reservedNames.Add(npc.Name);
        }

        if (!allAvailable)
        {
            foreach (var reservedName in reservedNames)
                _reservations.Release(reservedName, session.ReservationOwner);
            return;
        }

        session.ParticipantNames.AddRange(availableNpcs.Select(n => n.Name));
        _activeA2ASessions.Add(session);

        string names2 = string.Join(" & ", session.ParticipantNames);
        ModEntry.SMonitor?.Log(
            $"[A2A] 发现聚集：{names2}，开始凝视倒计时。",
            LogLevel.Debug);
    }

    /// <summary>
    /// Internal method to interrupt a specific A2A session.
    /// Called by DynamicBarkManager.InterruptA2ASession.
    /// </summary>
    internal void InterruptSession(
        DialogueModels.A2ASession session,
        string reason,
        bool applyHalfPersonalCooldown)
    {
        InterruptA2ASession(session, reason, applyHalfPersonalCooldown, stashInterruptedTail: true);
    }

    private void InterruptA2ASession(
        DialogueModels.A2ASession session,
        string reason,
        bool applyHalfPersonalCooldown,
        bool stashInterruptedTail)
    {
        if (session == null) return;

        if (session.IsEnding)
            return;

        session.MarkEnding();

        session.Cancel();

        try
        {
            session.RequestCts?.Cancel();
        }
        catch
        {
            // ignored
        }

        session.RequestCts = null;

        // ★ 被打断话尾暂存：仅当会话播放过 ≥2 句后被中断才记录，
        // 供后续同 pair 在窗口期内重聚时续接注入。自然收尾不写。
        if (stashInterruptedTail && session.RecentSpokenLines.Count >= 2)
        {
            string pairKey = DialogueUtilities.MakePairKey(session.ParticipantNames);
            string tail = session.RecentSpokenLines.Last().Item2;
            _a2aInterruptedTails[pairKey] = (Game1.Date.TotalDays, Game1.timeOfDay, tail);
            // 同步既有防复读：后续会话（即使过窗口退化为常规路径）免费覆盖
            _a2aPreviousTopics[pairKey] = tail;

            ModEntry.SMonitor?.Log(
                $"[A2A] 已暂存被打断话尾：{pairKey} → {tail}",
                StardewModdingAPI.LogLevel.Trace);
        }

        if (applyHalfPersonalCooldown)
        {
            int halfCooldown = A2A_PERSONAL_COOLDOWN_TICKS / 2;
            foreach (var name in session.ParticipantNames)
            {
                _a2aPersonalCooldowns[name] =
                    _a2aPersonalCooldowns.TryGetValue(name, out int existing)
                        ? Math.Max(existing, halfCooldown)
                        : halfCooldown;
            }
        }

        // 无论会话是正常结束还是被打断，统一给参与者一个 AmbientBark
        // 冷静期，防止 AmbientBark 雷达在同一瞬间把他们重新拉进请求队列。
        foreach (var name in session.ParticipantNames)
        {
            _a2aPostSessionCooldowns[name] =
                _a2aPostSessionCooldowns.TryGetValue(name, out int existingPost)
                    ? Math.Max(existingPost, A2A_POST_SESSION_COOLDOWN_TICKS)
                    : A2A_POST_SESSION_COOLDOWN_TICKS;
        }

        ReleaseSessionReservations(session);
        _activeA2ASessions.Remove(session);

        string names = string.Join(" & ", session.ParticipantNames);
        ModEntry.SMonitor?.Log($"[A2A] {reason}：{names}", LogLevel.Debug);
    }

    /// <summary>
    /// 消费（consume-once）指定 pair 的被打断话尾。
    /// 无记录 / 跨天 / 超窗口 → 清除并返回 null；否则移除并返回尾句。
    /// </summary>
    private string TryConsumeInterruptedTail(string pairKey)
    {
        if (!_a2aInterruptedTails.TryGetValue(pairKey, out var entry))
            return null;

        if (entry.Day != Game1.Date.TotalDays)
        {
            _a2aInterruptedTails.Remove(pairKey);
            return null;
        }

        int elapsed = Game1.timeOfDay - entry.TimeOfDay;
        if (elapsed < 0 || elapsed > A2A_RESUME_WINDOW_GAME_MINUTES)
        {
            _a2aInterruptedTails.Remove(pairKey);
            return null;
        }

        _a2aInterruptedTails.Remove(pairKey);
        return entry.Tail;
    }

    private void ReleaseSessionReservations(DialogueModels.A2ASession session)
    {
        if (session == null) return;

        var owner = string.IsNullOrWhiteSpace(session.ReservationOwner)
            ? "A2A"
            : session.ReservationOwner;

        foreach (var name in session.ParticipantNames)
            _reservations.Release(name, owner);
    }

    private async Task FetchA2AScriptAsync(string sessionId, DialogueModels.A2ARequest request, CancellationToken ct)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(sessionId) || request == null)
                return;

            ModEntry.SMonitor?.Log(
                $"[A2A] 发送脚本请求：{request.NamesLog}",
                LogLevel.Debug);

            var response = await _llmGateway.ExecuteAsync(
                LlmContextTypes.A2A,
                request.SystemPrompt,
                request.UserPrompt,
                ct);

            if (response == null || !response.IsSuccess || string.IsNullOrWhiteSpace(response.Text))
            {
                ModEntry.SMonitor?.Log(
                    $"[A2A] LLM 返回异常：{request.NamesLog} | Success={response?.IsSuccess} | Raw={response?.Text ?? "<null>"}",
                    LogLevel.Warn);

                EnqueueA2AFallback(sessionId, request.IsChinese);
                return;
            }

            _pendingA2AResults.Enqueue(new DialogueModels.A2ACompletedResult
            {
                SessionId = sessionId,
                RawJson = response.Text,
                IsChinese = request.IsChinese
            });
        }
        catch (OperationCanceledException)
        {
            bool isExplicitCancel = ct.IsCancellationRequested;
            var endReason = isExplicitCancel
                ? DialogueModels.LlmRequestEndReason.Cancelled
                : DialogueModels.LlmRequestEndReason.Timeout;

            if (!isExplicitCancel)
            {
                ModEntry.SMonitor?.Log(
                    "[A2A] 脚本请求超时，使用 fallback",
                    LogLevel.Debug);
            }

            _pendingA2AResults.Enqueue(new DialogueModels.A2ACompletedResult
            {
                SessionId = sessionId,
                Cancelled = isExplicitCancel,
                UseFallback = !isExplicitCancel,
                IsChinese = request?.IsChinese ?? false,
                EndReason = endReason
            });
        }
        catch (Exception ex)
        {
            ModEntry.SMonitor?.Log($"[A2A] 脚本请求异常：{ex.Message}", LogLevel.Warn);
            EnqueueA2AFallback(sessionId, request?.IsChinese ?? false);
        }
    }

    private void EnqueueA2AFallback(string sessionId, bool isChinese)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return;

        _pendingA2AResults.Enqueue(new DialogueModels.A2ACompletedResult
        {
            SessionId = sessionId,
            UseFallback = true,
            IsChinese = isChinese
        });
    }

    internal DialogueModels.A2ALine[] BuildFallbackScript(List<string> names, bool isChinese)
    {
        if (names == null || names.Count < 2)
            return Array.Empty<DialogueModels.A2ALine>();

        int totalRounds = names.Count == 2
            ? A2A_ROUNDS_TWO_PERSON
            : names.Count * A2A_ROUNDS_PER_SPEAKER;

        var result = new List<DialogueModels.A2ALine>();

        for (int i = 0; i < totalRounds; i++)
        {
            string name = names[i % names.Count];

            result.Add(new DialogueModels.A2ALine
            {
                SpeakerName = name,
                Line = AmbientBarkModule.GetRandomFallbackPublic(isChinese)
            });
        }

        return result.ToArray();
    }

    private static bool IsChineseLanguage =>
        LocalizedContentManager.CurrentLanguageCode == LocalizedContentManager.LanguageCode.zh;
}
