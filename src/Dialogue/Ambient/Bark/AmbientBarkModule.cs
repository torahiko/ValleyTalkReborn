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
/// Ambient Bark 模块：负责单人 NPC 自言自语的完整生命周期。
/// 持有独立的状态存储、Prompt 构建器和 LLM 网关引用。
/// 不直接访问 A2A 私有状态，通过委托查询 A2A 冷静期。
/// </summary>
internal sealed class AmbientBarkModule : IDialogueModule
{
    private const int DISPLAY_MIN_TICKS = 1200;
    private const int DISPLAY_MAX_TICKS = 3600;
    private const int DISPLAY_FOLLOW_MIN_TICKS = 600;
    private const int DISPLAY_FOLLOW_MAX_TICKS = 1800;
    private const int RADAR_RANGE_TILES = 5;
    private const int RADAR_RANGE_SQ = RADAR_RANGE_TILES * RADAR_RANGE_TILES;
    private const int MAX_BARK_QUEUE_SIZE = 3;
    private const int API_COOLDOWN_TICKS = 300;

    /// <summary>
    /// 单条台词浮字可见时长对应的 Ticks（3500ms × 60fps）。
    /// 与 _outputQueue.Enqueue 的 duration=3500 一致，用于节日播报通道占用计时。
    /// </summary>
    private const int DISPLAY_LINE_VISIBLE_TICKS = 210;

    /// <summary>
    /// 首播轮询间隔：在结果尚未到达前用于短周期检测。
    /// </summary>
    private const int FIRST_BARK_POLL_TICKS = 20;

    /// <summary>
    /// Reservation owner identifier for Ambient Bark.
    /// </summary>
    internal const string BarkReservationOwner = "AmbientBark";

    private readonly AmbientBarkStateStore _stateStore;
    private readonly BarkPromptBuilder _promptBuilder;
    private readonly NpcReservationService _reservations;
    private readonly MainThreadOutputQueue _outputQueue;

    private readonly LlmRequestGateway _llmGateway;

    // ★ 实时读取全局 Config，避免 GMCM reset 后引用断开导致模块读到旧实例
    private static ModConfig Config => ModEntry.Config;

    /// <summary>
    /// 当前是否处于节日活动地图（Event.isFestival）。节日临时 Actor 的 currentLocation 可能为 null，
    /// 故节日判定统一走 Event 标志，不做 location 解引用。
    /// </summary>
    private static bool IsFestivalNow => Game1.CurrentEvent?.isFestival == true;

    private readonly Queue<NPC> _globalRequestQueue = new Queue<NPC>();
    private int _globalRequestCooldown = 0;
    private int _radarCooldown = 0;

    /// <summary>
    /// 节日雷达源一次性诊断日志门控（Memory）。CleanupAll 中复位。
    /// </summary>
    private bool _loggedFestivalRadarSource;

    /// <summary>
    /// 由 DialogueCoordinator 注入，用于查询某 NPC 是否仍处于
    /// A2A 会话结束后的冷静期内。保持"不直接访问 A2A 私有状态"的
    /// 模块边界——只借助一个只读查询委托。
    /// </summary>
    internal Func<string, bool> IsNpcInA2APostCooldown { get; set; }

    /// <summary>
    /// 停留意图判定计数器：NPC 连续被雷达命中的扫描次数。
    /// 从 State 迁移到 Module 层，避免持久化非必要数据。
    /// </summary>
    private readonly Dictionary<string, int> _proximityScans =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

    private static readonly Random _rng = new Random();

    private static readonly string[] FallbackBarksZh =
    {
        "嗯...时间过得真慢。",
        "差不多该动一动了。",
        "真是好天气呢。",
        "今天有点安静啊。",
        "不知道大家在做什么。"
    };

    private static readonly string[] FallbackBarksEn =
    {
        "Nice day outside.",
        "Hmm...Time's dragging a bit.",
        "Nice weather, huh...",
        "I wonder...",
        "Pretty quiet around here, isn't it..."
    };

    private readonly ConcurrentQueue<DialogueModels.BarkLlmResult> _pendingBarkResults =
        new ConcurrentQueue<DialogueModels.BarkLlmResult>();

    internal AmbientBarkModule(
        AmbientBarkStateStore stateStore,
        BarkPromptBuilder promptBuilder,
        NpcReservationService reservations,
        MainThreadOutputQueue outputQueue,
        LlmRequestGateway llmGateway,
        ModConfig config)
    {
        _stateStore = stateStore;
        _promptBuilder = promptBuilder;
        _reservations = reservations;
        _outputQueue = outputQueue;
        _llmGateway = llmGateway;
    }

    public void OnGameLaunched()
    {
        // Ambient Bark 不需要启动时初始化
    }

    public void OnDayStarted()
    {
        CleanupAll();
    }

    public void OnReturnedToTitle()
    {
        CleanupAll();
    }

    /// <summary>
    /// 处理 Bark LLM 回传结果（包含迟到回传的潜意识回收）。
    /// </summary>
    internal void ProcessPendingResults()
    {
        while (_pendingBarkResults.TryDequeue(out var result))
        {
            if (result == null || string.IsNullOrEmpty(result.NpcName))
                continue;

            if (!_stateStore.TryGet(result.NpcName, out var state))
                continue;

            lock (state)
            {
                // 本地安全回收方法：将迟到/打断的有效台词存入 ImmediateEchoStore
                void SalvageToEchoStore()
                {
                    if (result.Barks != null && result.Barks.Length > 0)
                    {
                        var tail = result.Barks
                            .Where(b => !string.IsNullOrWhiteSpace(b))
                            .Take(2)
                            .ToList();

                        if (tail.Count > 0)
                        {
                            var npcChar = Game1.getCharacterFromName(result.NpcName);
                            string loc = npcChar?.currentLocation?.Name ?? Game1.currentLocation?.Name;
                            ImmediateEchoStore.RecordBark(result.NpcName, tail, loc);

                            ModEntry.SMonitor?.Log(
                                $"[AmbientBark] 成功将打断/迟到的 Bark 回传回收到 ImmediateEchoStore：{result.NpcName}（{string.Join(" / ", tail)}）",
                                LogLevel.Debug);
                        }
                    }
                }

                // ★ 潜意识利用 2：请求虽被取消或 RequestId 不匹配（迟到的结果），仍将有效台词存入 EchoStore
                if (state.RequestId != result.RequestId)
                {
                    SalvageToEchoStore();
                    continue;
                }

                // ★ 修复 1：请求被取消时，清理状态并赋予短暂冷静期，严禁自动重新入队
                if (result.EndReason == DialogueModels.LlmRequestEndReason.Cancelled)
                {
                    state.IsRequesting = false;
                    SalvageToEchoStore();
                    state.CooldownTicksRemaining = 1800; // 30 秒
                    _proximityScans.Remove(result.NpcName);
                    continue;
                }

                if (result.Cancelled)
                {
                    state.IsRequesting = false;
                    SalvageToEchoStore();
                    state.CooldownTicksRemaining = 1800; // 30 秒
                    continue;
                }

                if (result.UseFallback || result.Barks == null || result.Barks.Length == 0)
                {
                    PushFallbackLocked(state, result.IsChinese);
                    continue;
                }

                // ★ 潜意识利用 3：NPC 在回传时刻已被其他交互/A2A 占用，转存潜意识而不弹字
                if (IsReservedByOther(result.NpcName))
                {
                    ModEntry.SMonitor?.Log($"[AmbientBark] NPC 已被占用，Bark 转存至 ImmediateEchoStore：{result.NpcName}",
                        LogLevel.Debug);
                    SalvageToEchoStore();
                    state.IsRequesting = false;
                    state.BarkQueue.Clear();
                    state.CooldownTicksRemaining = 3600; // 60 秒
                    continue;
                }

                foreach (var bark in result.Barks)
                {
                    if (!string.IsNullOrWhiteSpace(bark))
                        state.BarkQueue.Enqueue(bark.Trim());
                }

                state.IsRequesting = false;

                // 回传到达瞬间唤醒显示计数器，允许下一 tick 立即出队第一条台词
                if (!state.HasPlayedFirst && state.DisplayCountdown > 0)
                    state.DisplayCountdown = 0;
            }
        }
    }

    /// <summary>
    /// 执行雷达扫描并将 NPC 加入全局请求队列。
    /// </summary>
    internal void PerformRadarScan()
    {
        if (!Config.EnableAmbientBarks) return;

        if (Game1.player == null) return;

        bool isFestival = IsFestivalNow;

        // 节日临时 Actor 的 currentLocation 可能为 null，故节日内直接以 Event.actors 为扫描源
        var allNpcs = GetScanCandidates();

        var nearbyNpcs = new List<NPC>();
        bool isPlayerMoving = Game1.player.isMoving();

        // 节日雷达源一次性诊断日志：确认节日 NPC 容器假设（actors vs characters）
        if (isFestival && !_loggedFestivalRadarSource)
        {
            int actorCount = Game1.CurrentEvent?.actors?.Count ?? 0;
            int charCount  = Game1.currentLocation?.characters?.Count ?? 0;
            ModEntry.SMonitor?.Log(
                $"[AmbientBark] Festival radar source: actors={actorCount}, characters={charCount}",
                LogLevel.Debug);

            foreach (var actor in (Game1.CurrentEvent?.actors ?? Enumerable.Empty<NPC>()).Where(n => n != null).Take(3))
            {
                string curLocDesc;
                if (actor.currentLocation == null)
                    curLocDesc = "null";
                else if (actor.currentLocation == Game1.currentLocation)
                    curLocDesc = "match";
                else
                    curLocDesc = "other";

                bool inRange = DialogueUtilities.IsInRangeSquaredDuringFestival(
                    actor,
                    (Farmer)Game1.player,
                    RADAR_RANGE_SQ);

                ModEntry.SMonitor?.Log(
                    $"[AmbientBark]  actor {actor.Name}: curLoc={curLocDesc}, inRange={inRange}",
                    LogLevel.Debug);
            }

            _loggedFestivalRadarSource = true;
        }

        foreach (var npc in allNpcs)
        {
            if (npc == null) continue;

            // 节日临时 Actor 不做村民检查（非村民临时 Actor 由后续 character?.Bio == null 兜底）；
            // IsNpcSleeping 保持原样，不做节日特判；非节日保持原判定不变。
            if (DialogueUtilities.IsNpcSleeping(npc) || (!isFestival && !npc.IsVillager))
                continue;

            bool inRange = isFestival
                ? DialogueUtilities.IsInRangeSquaredDuringFestival(
                    npc,
                    (Farmer)Game1.player,
                    RADAR_RANGE_SQ)
                : DialogueUtilities.IsInRangeSquared(
                    npc,
                    (Farmer)Game1.player,
                    RADAR_RANGE_SQ);

            if (!_proximityScans.TryGetValue(npc.Name, out int currentScans))
                currentScans = 0;

            if (inRange)
            {
                currentScans++;
            }
            else
            {
                currentScans = 0;
            }

            _proximityScans[npc.Name] = currentScans;

            int requiredScans;
            if (isFestival)
            {
                // 节日驻留门槛：moving 至少 5 次扫描，static 至少 4 次
                requiredScans = isPlayerMoving
                    ? Math.Max(Config.BarkDwellScans + 2, 5)
                    : Math.Max(Config.BarkDwellScans, 4);
            }
            else
            {
                // ★ 修复 3：意图检测
                // 如果玩家正处于移动状态（可能正跑向 NPC），大幅提高驻留门槛（至少 3 次雷达扫描即 3 秒）；
                // 只有静止/徘徊时才采用默认 BarkDwellScans。
                requiredScans = isPlayerMoving
                    ? Math.Max(Config.BarkDwellScans + 2, 3)
                    : Math.Max(Config.BarkDwellScans, 1);
            }

            if (!inRange || currentScans < requiredScans)
                continue;

            nearbyNpcs.Add(npc);
        }

        var candidates = new List<DialogueModels.NpcCandidate>();

        foreach (var npc in nearbyNpcs)
        {
            // 节日下仅允许命中既有缓存（Character.StardewNpc 必然绑定真实实体），
            // 绝不创建绑定临时 Actor 的新 Character → DialogueBuilder 缓存零污染。
            // 封闭性论证：PerformRadarScan 是 Bark 管线唯一入口；节日下仅缓存命中的村民会入队，
            // 故下游 BarkPromptBuilder.Build / EnrichWithDynamicState 的 GetCharacter(临时Actor) 必然缓存命中、
            // 不触发创建 → 无需改动 DialogueBuilder 内部任何代码。
            // 非节日保持原路径逐字符不变。
            var character = isFestival
                ? DialogueBuilder.Instance?.GetCharacterByName(npc?.Name)
                : DialogueBuilder.Instance?.GetCharacter(npc);

            if (character?.Bio == null ||
                !character.Bio.EnableAmbientBarks ||
                string.IsNullOrWhiteSpace(character.Bio.AmbientBarkPrompt) ||
                !character.HasValidBio)   // ★ Bios 门禁
                continue;

            if (_reservations.IsReserved(npc.Name))
                continue;

            // A2A 会话刚结束的冷静期内，不把该 NPC 重新纳入 Bark 候选，
            // 避免"话音刚落瞬间又开始自言自语"的割裂感
            if (IsNpcInA2APostCooldown?.Invoke(npc.Name) == true)
                continue;

            if (_globalRequestQueue.Contains(npc))
                continue;

            if (_stateStore.TryGet(npc.Name, out var state))
            {
                bool skip = false;

                lock (state)
                {
                    if (state.IsInCooldown() ||
                        state.BarkQueue.Count > 0 ||
                        state.IsRequesting)
                        skip = true;
                }

                if (skip) continue;
            }

            int priority = DialogueUtilities.CalculatePriority(npc);

            if (priority > 0)
            {
                candidates.Add(new DialogueModels.NpcCandidate
                {
                    Npc = npc,
                    Priority = priority
                });
            }
        }

        if (candidates.Count == 0) return;

        var sorted = candidates
            .OrderByDescending(c => c.Priority)
            .ThenBy(_ => _rng.Next())
            .ToList();

        int toAdd = Math.Min(Config.BarkQueueSize - _globalRequestQueue.Count, sorted.Count);
        if (toAdd <= 0) return;

        foreach (var candidate in sorted.Take(toAdd))
        {
            var npc = candidate.Npc;
            var npcState = _stateStore.GetOrCreate(npc.Name);

            lock (npcState)
            {
                npcState.BarkQueue.Clear();
                npcState.IsRequesting = false;
                npcState.HasPlayedFirst = false;
                npcState.DisplayCountdown = FIRST_BARK_POLL_TICKS;
                npcState.CooldownTicksRemaining = null;
                npcState.ReplaceCts();
            }

            _globalRequestQueue.Enqueue(npc);

            ModEntry.SMonitor?.Log(
                $"[AmbientBark] {npc.Name} added to queue (Priority: {candidate.Priority}).",
                LogLevel.Debug);
        }
    }

    /// <summary>
    /// 当玩家主动与 NPC 发起主对话时调用：截获队列中未播放的心声至 ImmediateEchoStore，并施加冷却。
    /// </summary>
    internal void NotifyPlayerInteracted(string npcName, int cooldownSeconds = 30)
    {
        if (string.IsNullOrWhiteSpace(npcName))
            return;

        int cooldownTicks = cooldownSeconds * 60; // 30 秒 = 1800 Ticks

        // 边界防御：如果 NPC 不在当前地图或不存在，只施加冷却但跳过状态清理
        var npc = Game1.getCharacterFromName(npcName);
        bool npcInCurrentLocation = npc != null
            && npc.currentLocation != null
            && Game1.currentLocation != null
            && npc.currentLocation == Game1.currentLocation;

        if (!npcInCurrentLocation)
        {
            ModEntry.SMonitor?.Log(
                $"[AmbientBark] NotifyPlayerInteracted: {npcName} 不在当前地图或不存在，跳过状态清理但施加冷却",
                LogLevel.Trace);

            var stateOutOfMap = _stateStore.GetOrCreate(npcName);
            lock (stateOutOfMap)
            {
                stateOutOfMap.CooldownTicksRemaining = cooldownTicks;
            }

            return;
        }

        RemoveFromGlobalQueue(npcName);
        _proximityScans.Remove(npcName);

        string locationName = npc.currentLocation.Name;

        if (_stateStore.TryGet(npcName, out var state))
        {
            lock (state)
            {
                // 潜意识利用 1：截获队列中未来得及冒出头顶的台词
                if (state.BarkQueue.Count > 0)
                {
                    var unplayedTail = state.BarkQueue.Take(2).ToList();
                    ImmediateEchoStore.RecordBark(npcName, unplayedTail, locationName);

                    ModEntry.SMonitor?.Log(
                        $"[AmbientBark] 玩家交互打断：已将 {npcName} 队列中未播放的 {unplayedTail.Count} 条台词转入潜意识",
                        LogLevel.Debug);
                }

                state.ReplaceCts();
                state.IsRequesting = false;
                state.BarkQueue.Clear();
                state.HasPlayedFirst = false;
                state.DisplayCountdown = 0;
                state.CooldownTicksRemaining = cooldownTicks; // 始终刷新冷却
            }

            ModEntry.SMonitor?.Log(
                $"[AmbientBark] 玩家已与 {npcName} 交互，已中断 Bark 并锁定冷静期 {cooldownSeconds}s",
                LogLevel.Debug);
        }
        else
        {
            var newState = _stateStore.GetOrCreate(npcName);
            lock (newState)
            {
                newState.CooldownTicksRemaining = cooldownTicks;
            }

            ModEntry.SMonitor?.Log(
                $"[AmbientBark] {npcName} 首次交互，已创建 State 并施加 {cooldownSeconds}s 冷却",
                LogLevel.Trace);
        }
    }

    /// <summary>
    /// 调度并派发 Bark API 请求。
    /// </summary>
    internal void DispatchRequests(bool hasActiveA2APlayback)
    {
        if (!Config.EnableAmbientBarks) return;

        if (_globalRequestCooldown > 0)
        {
            _globalRequestCooldown--;
            return;
        }

        if (hasActiveA2APlayback)
            return;

        if (_globalRequestQueue.Count == 0)
            return;

        var npc = _globalRequestQueue.Dequeue();

        if (npc == null || DialogueUtilities.IsNpcSleeping(npc) || _reservations.IsReserved(npc.Name))
        {
            _globalRequestCooldown = API_COOLDOWN_TICKS;
            return;
        }

        if (!_stateStore.TryGet(npc.Name, out var st))
        {
            _globalRequestCooldown = API_COOLDOWN_TICKS;
            return;
        }

        var request = _promptBuilder.Build(npc);
        bool canDispatch = false;
        CancellationToken token = default;
        int requestId = 0;

        lock (st)
        {
            if (request != null &&
                !st.IsInCooldown() &&
                st.BarkQueue.Count == 0 &&
                !st.IsRequesting)
            {
                st.ReplaceCts();
                st.IsRequesting = true;
                requestId = st.RequestId;
                token = st.BackgroundCts.Token;
                canDispatch = true;
            }
        }

        if (canDispatch)
        {
            ModEntry.SMonitor?.Log(
                $"[AmbientBark] Dispatching API request for {npc.Name} (queue: {_globalRequestQueue.Count}).",
                LogLevel.Debug);

            _ = Task.Run(() => FetchBarksAsync(request, token, requestId));
        }

        _globalRequestCooldown = Config.BarkApiCooldownTicks;
    }

    /// <summary>
    /// 推进单人 Bark 状态 Tick。
    /// </summary>
    internal void TickStates()
    {
        if (!Config.EnableAmbientBarks)
        {
            CleanupAll();
            return;
        }

        bool festival = IsFestivalNow;
        // 节日内任一 NPC 正在占用播报通道时，其余节日 NPC 出队延迟（每 tick 仅计算一次）
        bool festivalAnyBusy = festival && _stateStore.Snapshot().Any(kv => kv.Value.BusyTicksRemaining > 0);

        foreach (var kv in _stateStore.Snapshot())
        {
            var npcName = kv.Key;
            var state = kv.Value;
            var npc = Game1.getCharacterFromName(npcName);

            if (npc == null || DialogueUtilities.IsNpcSleeping(npc))
            {
                RemoveFromGlobalQueue(npcName);
                _stateStore.HardReset(npcName);
                _stateStore.TryRemove(npcName, out _);
                _proximityScans.Remove(npcName);
                continue;
            }

            lock (state)
            {
                // 推进占用计时递减（每 Tick 减 1）
                if (state.BusyTicksRemaining > 0)
                {
                    state.BusyTicksRemaining--;
                }

                // 推进冷却递减（每 Tick 减 1）
                if (state.CooldownTicksRemaining.HasValue && state.CooldownTicksRemaining.Value > 0)
                {
                    state.CooldownTicksRemaining--;
                }

                // 1. 处于冷却期中，跳过调度
                if (state.IsInCooldown())
                {
                    continue;
                }

                // 2. 冷却已过且无活跃台词与请求：只清运行态字段，保留思绪记忆
                //    （避免把 LastThreadTail / RecentBarks 连同 State 对象一起回收）
                else if (state.BarkQueue.Count == 0 &&
                         !state.IsRequesting &&
                         !state.HasPlayedFirst &&
                         state.CooldownTicksRemaining.HasValue)
                {
                    _stateStore.ClearRuntimeStateOnly(npcName);
                    _proximityScans.Remove(npcName);
                    continue;
                }

                // 3. 展示推进逻辑
                if (state.BarkQueue.Count > 0)
                {
                    TickDisplayLocked(npc, state, festivalAnyBusy);
                }
                else if (state.HasPlayedFirst)
                {
                    FinalizeThreadLocked(npc, state);
                }
            }
        }
    }

    /// <summary>
    /// 推进展示倒计时并出队台词（锁内调用）
    /// </summary>
    private void TickDisplayLocked(NPC npc, AmbientBarkStateStore.State state, bool festivalAnyBusy)
    {
        if (Game1.activeClickableMenu != null || Game1.dialogueUp || DialogueUtilities.IsNpcSleeping(npc))
            return;

        // A2A 播放期间不显示
        if (IsReservedByOther(npc.Name))
            return;

        // 节日内若其他 NPC 正在占用播报通道，延迟本轮出队，避免多 NPC 同时堆字
        if (festivalAnyBusy)
        {
            state.DisplayCountdown = FIRST_BARK_POLL_TICKS;
            return;
        }

        state.DisplayCountdown--;
        if (state.DisplayCountdown > 0)
            return;

        // 距离保护：玩家过远时挂起倒计时，使用短周期轮询检测
        // 节日临时 Actor 的 currentLocation 可能为 null，故节日内使用 IsInRangeSquaredDuringFestival
        bool inDisplayRange = IsFestivalNow
            ? DialogueUtilities.IsInRangeSquaredDuringFestival(npc, (Farmer)Game1.player, DialogueConstants.DisplayRangeSquared)
            : DialogueUtilities.IsInRangeSquared(npc, (Farmer)Game1.player, DialogueConstants.DisplayRangeSquared);

        if (!inDisplayRange)
        {
            state.DisplayCountdown = FIRST_BARK_POLL_TICKS;
            return;
        }

        // 出队台词
        string bark = state.BarkQueue.Dequeue();
        state.AddRecentBark(bark);
        state.HasPlayedFirst = true;

        // 计算下一句间隔
        state.DisplayCountdown = DialogueUtilities.NextDisplayInterval(npc, _rng);

        _outputQueue.Enqueue(npc.Name, bark, 3500, "Bark");

        // 标记本 NPC 进入播报占用态，占用期间其余节日 NPC 出队延迟
        state.BusyTicksRemaining = DISPLAY_LINE_VISIBLE_TICKS;
    }

    /// <summary>
    /// 台词播完，原子写入记忆并进入冷却（锁内调用）
    /// </summary>
    private static void FinalizeThreadLocked(NPC npc, AmbientBarkStateStore.State state)
    {
        // 记录思绪尾句（最后 2 条）
        state.LastThreadTail.Clear();
        var recentList = state.RecentBarks.ToList();
        int tailStart = Math.Max(0, recentList.Count - 2);
        for (int i = tailStart; i < recentList.Count; i++)
            state.LastThreadTail.Add(recentList[i]);

        // 一轮自言自语自然播完——移交同一份尾句给 ImmediateEchoStore，
        // 供玩家紧接着点开这个 NPC 时还原"刚嘀咕完"的现场感
        ImmediateEchoStore.RecordBark(npc.Name, state.LastThreadTail, npc.currentLocation?.Name);

        state.LastThreadEndedAt = DateTime.UtcNow;
        state.LastThreadGameTimeOfDay = Game1.timeOfDay;
        state.LastThreadSaveDayNumber = Game1.Date.TotalDays;

        state.HasPlayedFirst = false;
        state.DisplayCountdown = 0;

        // ★ 冷却时间三级选择：节日 300 秒（避免节日内台词扎堆），跟随/约会 60 秒，日常 120 秒
        // 120 秒是为了给"短间隔延续"（≤3分钟）留出足够的缓冲窗口，
        // 同时避免 NPC 话太密集像话痨
        bool isFollowOrDate = DialogueUtilities.IsFollowingSafe(npc) || DialogueUtilities.IsOnDate(npc);
        int cooldownSeconds;
        if (IsFestivalNow)
            cooldownSeconds = 300;
        else if (isFollowOrDate)
            cooldownSeconds = 60;
        else
            cooldownSeconds = 120;

        state.CooldownTicksRemaining = cooldownSeconds * 60;

        ModEntry.SMonitor?.Log(
            $"[AmbientBark] {npc.Name} 台词播完，已记录思绪收尾，冷却 {cooldownSeconds}s（{state.CooldownTicksRemaining} ticks）",
            LogLevel.Trace);
    }

    /// <summary>
    /// 节日漫游结束（DialogueCoordinator 在 eventUp true→false 边沿调用）。
    /// 严格按序：① 将各 NPC 队列残余台词打捞到 ImmediateEchoStore（locationName=null）；
    /// ② 清空运行态字段（保留思绪记忆）；③ 清空全局请求队列。
    /// </summary>
    internal void OnFestivalRoamEnded()
    {
        int salvagedNpcCount = 0;

        foreach (var kv in _stateStore.Snapshot())
        {
            var npcName = kv.Key;
            var state = kv.Value;

            try
            {
                lock (state)
                {
                    if (state.BarkQueue.Count > 0)
                    {
                        var tail = state.BarkQueue.Take(2).ToList();
                        ImmediateEchoStore.RecordBark(npcName, tail, null);
                        salvagedNpcCount++;
                    }

                    state.BarkQueue.Clear();

                    if (state.IsRequesting)
                        state.ReplaceCts();

                    state.IsRequesting = false;
                    state.DisplayCountdown = 0;
                    state.BusyTicksRemaining = 0;
                }
            }
            catch (Exception ex)
            {
                ModEntry.SMonitor?.Log(
                    $"[AmbientBark] OnFestivalRoamEnded 处理 {npcName} 异常：{ex.Message}",
                    LogLevel.Warn);
            }
        }

        // 无论上面是否异常，最终清除可能滞留的节日临时 Actor 引用
        _globalRequestQueue.Clear();

        ModEntry.SMonitor?.Log(
            $"[AmbientBark] Festival roam ended: salvaged & cleared {salvagedNpcCount} npc queue(s)",
            LogLevel.Debug);
    }

    /// <summary>
    /// 节日语境下的雷达扫描源：节日内 Event.actors 与玩家必然同处活动地图，
    /// 直接以 actors 为候选；否则回退到当前地图 characters。
    /// </summary>
    private IEnumerable<NPC> GetScanCandidates()
    {
        if (IsFestivalNow)
        {
            var actors = Game1.CurrentEvent.actors;
            if (actors != null && actors.Count > 0)
                return actors;
        }

        return Game1.currentLocation?.characters ?? Enumerable.Empty<NPC>();
    }

    /// 推进雷达冷却并触发雷达扫描。
    /// </summary>
    internal void TickRadarCooldown()
    {
        _radarCooldown--;
        if (_radarCooldown <= 0)
        {
            _radarCooldown = 60;
            PerformRadarScan();
        }
    }

    /// <summary>
    /// 清理所有状态和队列。
    /// </summary>
    internal void CleanupAll()
    {
        foreach (var kv in _stateStore.Snapshot())
        {
            _stateStore.HardReset(kv.Key);
        }

        _stateStore.Clear();
        _globalRequestQueue.Clear();
        _globalRequestCooldown = 0;
        _radarCooldown = 0;
        _proximityScans.Clear();
        _loggedFestivalRadarSource = false;

        while (_pendingBarkResults.TryDequeue(out _))
        {
        }
    }

    /// <summary>
    /// 强制重置指定 NPC 的状态（用于 follower 等场景）。
    /// </summary>
    internal void ResetForFollow(NPC npc)
    {
        if (npc == null) return;

        var character = DialogueBuilder.Instance?.GetCharacter(npc);
        if (character == null) return;

        var bio = character.Bio;
        if (bio == null || !bio.EnableAmbientBarks || string.IsNullOrWhiteSpace(bio.AmbientBarkPrompt) ||
            !character.HasValidBio)   // ★ Bios 门禁
            return;

        RemoveFromGlobalQueue(npc);
        _stateStore.HardReset(npc.Name);
        _proximityScans.Remove(npc.Name);

        var npcState = _stateStore.GetOrCreate(npc.Name);

        lock (npcState)
        {
            npcState.BarkQueue.Clear();
            npcState.IsRequesting = false;
            npcState.HasPlayedFirst = false;
            npcState.DisplayCountdown = FIRST_BARK_POLL_TICKS;
            npcState.CooldownTicksRemaining = null;
            npcState.ReplaceCts();
        }

        _globalRequestQueue.Enqueue(npc);

        ModEntry.SMonitor?.Log(
            $"[AmbientBark] Follow started for {npc.Name}: bark state reset, follow-context request queued.",
            LogLevel.Debug);
    }

    internal void CancelForNpc(string npcName)
    {
        if (string.IsNullOrEmpty(npcName)) return;

        if (!_stateStore.TryGet(npcName, out var state)) return;

        lock (state)
        {
            if (!state.IsRequesting) return;

            state.ReplaceCts();
            state.IsRequesting = false;

            ModEntry.SMonitor?.Log(
                $"[AmbientBark] Background bark request cancelled for {npcName}.",
                LogLevel.Debug);
        }
    }

    /// <summary>
    /// 重置指定 NPC 的 Bark 状态（用于跟随结束等场景）。
    /// </summary>
    internal void ResetForNpc(string npcName)
    {
        if (string.IsNullOrEmpty(npcName)) return;

        RemoveFromGlobalQueue(npcName);
        _stateStore.HardReset(npcName);
        _stateStore.TryRemove(npcName, out _);
        _proximityScans.Remove(npcName);
    }

    internal bool HasAnyRequesting()
    {
        foreach (var kv in _stateStore.Snapshot())
        {
            lock (kv.Value)
            {
                if (kv.Value.IsRequesting)
                    return true;
            }
        }

        return false;
    }

    private static void PushFallbackLocked(AmbientBarkStateStore.State state, bool isChinese)
    {
        state.BarkQueue.Enqueue(GetRandomFallback(isChinese));
        state.IsRequesting = false;

        if (!state.HasPlayedFirst && state.DisplayCountdown > 0)
            state.DisplayCountdown = 0;

        ModEntry.SMonitor?.Log("[AmbientBark] 降级兜底：已推送 fallback bark", LogLevel.Debug);
    }

    private static string GetRandomFallback(bool isChinese)
    {
        var pool = isChinese ? FallbackBarksZh : FallbackBarksEn;
        return pool[_rng.Next(pool.Length)];
    }

    private static bool IsChineseLanguage =>
        LocalizedContentManager.CurrentLanguageCode == LocalizedContentManager.LanguageCode.zh;

    /// <summary>
    /// 从全局请求队列中移除指定 NPC（公开接口，供 DialogueCoordinator 调用）。
    /// </summary>
    internal void RemoveFromQueueIfPresent(string npcName)
    {
        if (string.IsNullOrWhiteSpace(npcName))
            return;

        RemoveFromGlobalQueue(npcName);

        ModEntry.SMonitor?.Log(
            $"[AmbientBark] {npcName} 已从全局请求队列移除",
            LogLevel.Trace);
    }

    private void RemoveFromGlobalQueue(NPC npc)
    {
        RemoveFromGlobalQueue(npc?.Name);
    }

    private void RemoveFromGlobalQueue(string npcName)
    {
        if (_globalRequestQueue.Count == 0 || string.IsNullOrEmpty(npcName))
            return;

        var items = _globalRequestQueue.ToArray();
        _globalRequestQueue.Clear();

        foreach (var item in items)
        {
            if (item != null && !string.Equals(item.Name, npcName, StringComparison.OrdinalIgnoreCase))
                _globalRequestQueue.Enqueue(item);
        }
    }

    private async Task FetchBarksAsync(DialogueModels.BarkRequest request, CancellationToken ct, int requestId)
    {
        try
        {
            if (request == null)
                return;

            // ★ 修复：长文本上下文受 Config.Debug 门控保护，未开启时彻底静默
            if (Config?.Debug == true)
            {
                ModEntry.SMonitor?.Log(
                    $"[AmbientBark] Bark 请求上下文 ({request.NpcName}):\n[SYSTEM]\n{request.SystemPrompt}\n\n[USER]\n{request.UserPrompt}",
                    LogLevel.Debug);
            }

            var response = await _llmGateway.ExecuteAsync(
                LlmContextTypes.Bark,
                request.SystemPrompt,
                request.UserPrompt,
                ct);

            if (ct.IsCancellationRequested)
            {
                _pendingBarkResults.Enqueue(new DialogueModels.BarkLlmResult
                {
                    NpcName = request.NpcName,
                    RequestId = requestId,
                    Cancelled = true,
                    IsChinese = request.IsChinese
                });
                return;
            }

            if (response == null || !response.IsSuccess || string.IsNullOrWhiteSpace(response.Text))
            {
                ModEntry.SMonitor?.Log(
                    $"[AmbientBark] LLM 返回异常：{request.NpcName} | Success={response?.IsSuccess} | Raw={response?.Text ?? "<null>"}",
                    LogLevel.Warn);

                EnqueueBarkFallback(request, requestId);
                return;
            }

            var barks = DialogueParsing.ParseBarkJson(response.Text);

            if (barks == null || barks.Length == 0)
            {
                ModEntry.SMonitor?.Log(
                    $"[AmbientBark] 五级解析全部失败，使用 fallback：{request.NpcName} | Raw={response.Text}",
                    LogLevel.Warn);

                EnqueueBarkFallback(request, requestId);
                return;
            }

            _pendingBarkResults.Enqueue(new DialogueModels.BarkLlmResult
            {
                NpcName = request.NpcName,
                RequestId = requestId,
                Barks = barks,
                IsChinese = request.IsChinese
            });
        }
        catch (OperationCanceledException)
        {
            var endReason = ct.IsCancellationRequested
                ? DialogueModels.LlmRequestEndReason.Cancelled
                : DialogueModels.LlmRequestEndReason.Timeout;

            _pendingBarkResults.Enqueue(new DialogueModels.BarkLlmResult
            {
                NpcName = request.NpcName,
                RequestId = requestId,
                Cancelled = ct.IsCancellationRequested,
                IsChinese = request.IsChinese,
                EndReason = endReason
            });
        }
        catch (Exception ex)
        {
            ModEntry.SMonitor?.Log($"[AmbientBark] Bark 请求异常：{ex.Message}", LogLevel.Warn);
            EnqueueBarkFallback(request, requestId);
        }
    }

    private void EnqueueBarkFallback(DialogueModels.BarkRequest request, int requestId)
    {
        if (request == null) return;

        _pendingBarkResults.Enqueue(new DialogueModels.BarkLlmResult
        {
            NpcName = request.NpcName,
            RequestId = requestId,
            UseFallback = true,
            IsChinese = request.IsChinese
        });
    }
 
    internal static string GetRandomFallbackPublic(bool isChinese)
    {
        return GetRandomFallback(isChinese);
    }

    internal bool IsReservedByOther(string npcName)
    {
        if (string.IsNullOrWhiteSpace(npcName))
            return false;

        return _reservations.IsReserved(npcName)
               && !_reservations.IsReservedBy(npcName, BarkReservationOwner);
    }
}