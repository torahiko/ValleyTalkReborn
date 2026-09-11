using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

namespace ValleytalkReborn
{
    public class ScheduledPoiEntry
    {
        public string   PoiId         { get; set; }
        public PoiAsset Asset         { get; set; }
        public int      DepartureTime { get; set; }
        public bool     Executed      { get; set; } = false;

        /// <summary>
        /// 该 POI 建议停留的游戏分钟数。到达 POI 之后才会据此计算 EndTime——
        /// 不要在下单阶段就把 EndTime 定死，否则寻路延迟/失败会让停留时间被压缩甚至变负。
        /// </summary>
        public int StayMinutes { get; set; } = 90;

        /// <summary>
        /// 实际的停留结束时间。只有在 NPC 真正抵达该 POI 时才会被赋值（见
        /// SpouseDepartureRouter.MarkEntryArrived）；在此之前为 null，
        /// TryExecuteNextEntry 不会因为它而触发回家判断。
        /// </summary>
        public int? EndTime { get; set; } = null;
    }

    // ─── 等待玩家离开的原因 ──────────────────────────────────────
    /// <summary>
    /// 日程上下文生命周期阶段。统一驱动 LLM 注入上下文的"先销毁旧值、再写入新值"。
    /// </summary>
    internal enum ScheduleContextPhase
    {
        None = 0,
        AllDayStayHome,
        TravelingToPoi,
        ActiveAtPoi,
        ReturningHome,
        ArrivedHomeEarly,
        WaitingForPlayer,
        FollowingPlayer
    }

    internal enum PlayerLeaveWaitReason
    {
        None = 0,
        DepartToSchedule,
        ReturnHome,
    }

    internal class SpouseScheduleState
    {
        public NPC                     TrackedNpc           { get; set; }
        public List<ScheduledPoiEntry> Queue                { get; } = new();
        public bool                    Dispatched           { get; set; } = false;
        public bool                    IsStayHome           { get; set; } = false;
        public bool                    IsReturningHome      { get; set; } = false;
        public bool                    WaitingForPlayerToLeave { get; set; } = false;
        /// <summary>true=等待出发去POI，false=等待回家。由 TickWaitForPlayerLeave 区分行为。</summary>
        public bool                    WaitingToDepart      { get; set; } = false;
        /// <summary>明确区分"等待出发"和"等待回家"，替代 WaitingToDepart。</summary>
        public PlayerLeaveWaitReason   LeaveWaitReason      { get; set; } = PlayerLeaveWaitReason.None;
        public Action                  OnFarmPoiArrived     { get; set; } = null;
        public string                  ActivePoiDescription { get; set; } = "";
        public int                     WanderCooldownTicks  { get; set; } = 0;
        /// <summary>正在执行 DepartFromFarmHouse 出门流程中，防止 TickWander 重复触发。</summary>
        public bool                    IsDepartingToFarm     { get; set; } = false;
        /// <summary>是否已经走出过 FarmHouse 到 Farm（晴天游荡模式已激活）。</summary>
        public bool                    HasDepartedToFarm     { get; set; } = false;
        /// <summary>当前上下文生命周期阶段。由 TransitionScheduleContext 统一维护。</summary>
        public ScheduleContextPhase    CurrentPhase         { get; set; } = ScheduleContextPhase.None;
        /// <summary>上一个活跃 POI 的 ID，用于回程/提前回家时生成"after spending time at {PoiId}"上下文。</summary>
        public string                  PreviousPoiId        { get; set; } = "";

        /// <summary>
        /// 出发去当前/上一个 POI 时，是否走的是"经巴士站"的对侧路线。
        /// 由 FarmBusStopNavigator 在出发成功后写入，回家时读取以决定是否对称地再走一次巴士站。
        /// 与具体某个 ScheduledPoiEntry 无关，避免队列被清空/重建后找不到状态。
        /// </summary>
        public bool                    WentViaBusStop       { get; set; } = false;

        /// <summary>等待玩家离开当前地图的计时器（tick 数），用于 TickWaitForPlayerLeave 的超时兜底。</summary>
        public int                     PlayerLeaveWaitTicks { get; set; } = 0;
    }

    // ─── 主管理器 ──────────────────────────────────────────────────────────
    public class CompanionScheduleManager
    {
        // ─── 单例 ──────────────────────────────────────────────────────
        private static readonly CompanionScheduleManager _instance = new();
        public static CompanionScheduleManager Instance
        {
            get { _instance.EnsureEventsSubscribed(); return _instance; }
        }

        // ─── 字段 ──────────────────────────────────────────────────────
        private const int    MaxOutCount    = 4;

        private readonly PoiRepository _poiRepo = new(ModEntry.SHelper);
        private readonly SchedulePlanner _planner = new(ModEntry.SHelper);
        private bool _eventsSubscribed = false;

        private readonly Dictionary<string, SpouseScheduleState> _states
            = new(StringComparer.OrdinalIgnoreCase);

        private CompanionScheduleManager() { }

        // ──────────────────────────────────────────────────────
        //  初始化
        // ──────────────────────────────────────────────────────
        private void EnsureEventsSubscribed()
        {
            if (_eventsSubscribed || ModEntry.SHelper == null) return;
            // ★ GameLaunched 不再在此订阅：SMAPI API 解析已统一由 SpouseQueryService 在 ModEntry 接管
            ModEntry.SHelper.Events.GameLoop.SaveLoaded   += OnSaveLoaded;
            ModEntry.SHelper.Events.GameLoop.DayStarted   += OnDayStarted;
            ModEntry.SHelper.Events.GameLoop.TimeChanged  += OnTimeChanged;
            ModEntry.SHelper.Events.GameLoop.DayEnding    += OnDayEnding;
            ModEntry.SHelper.Events.GameLoop.UpdateTicked += OnUpdateTicked;
            _eventsSubscribed = true;
        }

        // ──────────────────────────────────────────────────────
        //  对外接口
        // ──────────────────────────────────────────────────────
        public string GetActivePoiContext(string npcName)
        {
            if (!ModEntry.Config.EnableSpouseSchedule) return "";
            if (string.IsNullOrWhiteSpace(npcName)) return "";
            if (!_states.TryGetValue(npcName, out var s)) return "";
            return string.IsNullOrWhiteSpace(s.ActivePoiDescription)
                ? ""
                : $"[Right now you are: {s.ActivePoiDescription}]";
        }

        /// <summary>
        /// 统一上下文生命周期转换器。严格遵循"先销毁旧值，再写入新值"的顺序，
        /// 确保任意时刻 NPC 的 LLM 上下文都精准反映其当前所处状态。
        /// </summary>
        private static void TransitionScheduleContext(
            SpouseScheduleState state,
            ScheduleContextPhase newPhase,
            string extraContext = "")
        {
            // 1. 严格对齐周期：无论前一状态是什么，先注销旧上下文
            state.ActivePoiDescription = "";
            state.CurrentPhase = newPhase;

            // 2. 根据新状态注入精准的即时上下文
            state.ActivePoiDescription = newPhase switch
            {
                ScheduleContextPhase.AllDayStayHome =>
                    "relaxing at home today, spending a quiet and peaceful day around the farmhouse",

                ScheduleContextPhase.TravelingToPoi =>
                    $"on the way to {extraContext.Replace('_', ' ')}, walking through the valley",

                ScheduleContextPhase.ActiveAtPoi =>
                    extraContext,

                ScheduleContextPhase.ReturningHome =>
                    string.IsNullOrWhiteSpace(extraContext)
                        ? "heading back home to the farm"
                        : $"heading back home to the farm after spending time at {extraContext.Replace('_', ' ')}",

                ScheduleContextPhase.ArrivedHomeEarly =>
                    "back at the farmhouse, unwinding and resting after going out earlier today",

                ScheduleContextPhase.WaitingForPlayer =>
                    "spending time with you before continuing with the day's routine",

                ScheduleContextPhase.FollowingPlayer =>
                    "accompanying you and spending time together",

                _ => ""
            };
        }

        // ★ 配偶判定已统一委托至 SpouseQueryService
        public static bool IsLegalSpouse(string npcName)
            => SpouseQueryService.Instance.IsMarried(npcName);

        // ──────────────────────────────────────────────────────
        //  资产加载
        // ──────────────────────────────────────────────────────
        public void LoadAssets()
        {
            _poiRepo.LoadAssets();
            _planner.LoadPreferences();
            ModEntry.SMonitor?.Log(
                $"[CSM] Assets loaded — {_poiRepo.TotalPoiCount} POIs, {_planner.TotalPrefCount} prefs.",
                LogLevel.Info);
        }

        public void ReloadAssets()
        {
            _poiRepo.ReloadAssets();
            _planner.ReloadPreferences();
        }

        // ──────────────────────────────────────────────────────
        //  事件回调
        // ──────────────────────────────────────────────────────
        private void OnSaveLoaded(object sender, SaveLoadedEventArgs e)
        {
            LoadAssets();
            ResetAllStates();

            if (!ModEntry.Config.EnableSpouseSchedule) return;

            if (!Utility.isFestivalDay(Game1.dayOfMonth, Game1.season)
                && Game1.timeOfDay >= 700 && Game1.timeOfDay < 2000)
                DispatchAllSchedules();
        }

        private void OnDayStarted(object sender, DayStartedEventArgs e)
        {
            ResetAllStates();

            if (Utility.isFestivalDay(Game1.dayOfMonth, Game1.season))
            {
                ModEntry.SMonitor?.Log("[CSM] Festival day — scheduler dormant.", LogLevel.Info);
                return;
            }

            if (!_poiRepo.IsLoaded || !_planner.IsLoaded) LoadAssets();
        }

        private void OnTimeChanged(object sender, TimeChangedEventArgs e)
        {
            if (Utility.isFestivalDay(Game1.dayOfMonth, Game1.season)) return;
            if (!ModEntry.Config.EnableSpouseSchedule) return;

            if (e.NewTime == 700)
            {
                DispatchAllSchedules();
                return;
            }

            if (e.NewTime is >= 700 and < 2000)
            {
                foreach (var state in _states.Values.ToArray())
                    TryExecuteNextEntry(state, e.NewTime);
            }

            // 18:00 全员并发召回（MMR-07）：带实体互斥守卫，跳过游荡/移动/导航中的配偶。
            if (e.NewTime == 1800)
            {
                RecallAllSpousesConcurrent(e.NewTime);
            }
            else if (e.NewTime == 2200)
            {
                RecallAllRemainingSpouses();
            }
        }

        private void OnDayEnding(object sender, DayEndingEventArgs e)
        {
            // 换日安全网：玩家提前睡觉时，将所有尚未回家的配偶直接瞬移到 FarmHouse，
            // 此时玩家已进入换日流程看不到瞬移过程，无需平滑退场。
            foreach (var kvp in _states)
            {
                var s = kvp.Value;
                if (s.IsStayHome) continue; // 已在家，跳过

                var npc = s.TrackedNpc;
                if (npc == null) continue;

                try
                {
                    var (homeMap, homeTile) = GetHomeDestinationPublic(npc);
                    Game1.warpCharacter(npc, homeMap, new Point((int)homeTile.X, (int)homeTile.Y));
                    ModEntry.SMonitor?.Log(
                        $"[CSM] DayEnding safety warp: {npc.Name} → {homeMap} ({homeTile.X},{homeTile.Y}).",
                        LogLevel.Debug);
                }
                catch (Exception ex)
                {
                    ModEntry.SMonitor?.Log(
                        $"[CSM] DayEnding safety warp failed for {npc.Name}: {ex.Message}", LogLevel.Warn);
                }
            }

            // 收集当天已执行 POI 到历史疲劳度记录
            foreach (var kvp in _states)
            {
                var npcName = kvp.Key;
                var state = kvp.Value;

                if (!state.IsStayHome)
                {
                    List<ScheduledPoiEntry> snapshot;
                    lock (state.Queue)
                    {
                        snapshot = state.Queue.ToList();
                    }
                    _planner.RecordExecutedHistory(npcName, snapshot);
                }
            }

            // 清理不再处于合法婚姻关系的历史键
            _planner.CleanupStaleHistory(name => SpouseQueryService.Instance.IsMarried(name));

            ResetAllStates();
            MultiMapNavigator.Instance.CancelAll();
        }

        // ──────────────────────────────────────────────────────
        //  UpdateTicked — 居家游荡驱动
        // ──────────────────────────────────────────────────────
        private void OnUpdateTicked(object sender, UpdateTickedEventArgs e)
        {
            if (!Context.IsWorldReady) return;
            if (!ModEntry.Config.EnableSpouseSchedule) return;
            if (Utility.isFestivalDay(Game1.dayOfMonth, Game1.season)) return;
            if (Game1.timeOfDay < 700 || Game1.timeOfDay >= 2000) return;

            foreach (var state in _states.Values)
            {
                if (state.OnFarmPoiArrived != null)
                {
                    var npc = state.TrackedNpc;
                    if (npc != null && npc.controller != null && MovementPathfinding.IsPathDone(npc.controller))
                    {
                        npc.controller = null;
                        npc.addedSpeed = 0;
                        npc.Halt();
                        var cb = state.OnFarmPoiArrived;
                        state.OnFarmPoiArrived = null;
                        cb.Invoke();
                    }
                }

                if (state.IsStayHome)
                {
                    WanderSystem.TickWander(state);
                    continue;
                }

                if (state.WaitingForPlayerToLeave)
                    TickWaitForPlayerLeave(state);
            }
        }

        /// <summary>
        /// 等待玩家离开的超时上限（tick）。约合游戏内 60 分钟 / 现实约 45 秒。
        /// 超过后不再苦等，强制执行原定动作，防止玩家长时间停留同地图导致日程永久瘫痪。
        /// </summary>
        private const int PLAYER_LEAVE_WAIT_TIMEOUT_TICKS = 2700;

        private void TickWaitForPlayerLeave(SpouseScheduleState state)
        {
            var npc = state.TrackedNpc;
            if (npc == null) return;

            bool sameLocation = string.Equals(
                npc.currentLocation?.Name, Game1.player?.currentLocation?.Name,
                StringComparison.OrdinalIgnoreCase);

            state.PlayerLeaveWaitTicks++;

            bool timedOut = state.PlayerLeaveWaitTicks > PLAYER_LEAVE_WAIT_TIMEOUT_TICKS;

            if (!sameLocation || timedOut)
            {
                if (sameLocation && timedOut)
                {
                    ModEntry.SMonitor?.Log(
                        $"[CSM] {npc.Name} wait-for-player timed out after {state.PlayerLeaveWaitTicks} ticks — proceeding anyway.",
                        LogLevel.Info);
                }

                state.WaitingForPlayerToLeave = false;
                state.PlayerLeaveWaitTicks    = 0;

                var reason = state.LeaveWaitReason;
                state.LeaveWaitReason = PlayerLeaveWaitReason.None;

                if (reason == PlayerLeaveWaitReason.DepartToSchedule)
                {
                    var farmEntry = Game1.getFarm().GetMainFarmHouseEntry();
                    Game1.warpCharacter(npc, "Farm", new Point(farmEntry.X, farmEntry.Y + 1));
                    ModEntry.SMonitor?.Log(
                        $"[CSM] {npc.Name} proceeding to farm to start schedule (playerLeft={!sameLocation}).",
                        LogLevel.Info);
                }
                else if (reason == PlayerLeaveWaitReason.ReturnHome)
                {
                    ModEntry.SMonitor?.Log(
                        $"[CSM] {npc.Name} now heading home (playerLeft={!sameLocation}).",
                        LogLevel.Info);
                    ExecuteReturnHome(npc, state);
                }
            }
        }

        // ──────────────────────────────────────────────────────
        //  配偶列表
        // ──────────────────────────────────────────────────────
        // ★ 配偶查询已统一委托至 SpouseQueryService
        public List<NPC> GetAllMarriedNpcs()
            => SpouseQueryService.Instance.GetAllMarriedNpcs();

        // ──────────────────────────────────────────────────────
        //  调度
        // ──────────────────────────────────────────────────────
        private void DispatchAllSchedules()
        {
            var spouses = GetAllMarriedNpcs();
            if (spouses.Count == 0) return;

            int outCount;
            if (spouses.Count <= 2)
                outCount = spouses.Count;
            else if (spouses.Count <= 4)
                outCount = spouses.Count - 1;
            else
                outCount = 4;

            int stayHomeCount = spouses.Count - outCount;

            HashSet<string> stayHomeNames;
            List<NPC>       goingOut;

            if (stayHomeCount == 0)
            {
                goingOut      = spouses;
                stayHomeNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            }
            else
            {
                var shuffled  = spouses.OrderBy(_ => Game1.random.Next()).ToList();
                goingOut      = shuffled.Take(outCount).ToList();

                var goingOutNames = new HashSet<string>(
                    goingOut.Select(n => n.Name), StringComparer.OrdinalIgnoreCase);
                stayHomeNames = new HashSet<string>(
                    spouses.Select(n => n.Name).Where(name => !goingOutNames.Contains(name)),
                    StringComparer.OrdinalIgnoreCase);
            }

            ModEntry.SMonitor?.Log(
                $"[CSM] Dispatching — total={spouses.Count}, " +
                $"out=[{string.Join(", ", goingOut.Select(n => n.Name))}]" +
                (stayHomeNames.Count > 0
                    ? $", stayHome=[{string.Join(", ", stayHomeNames)}]"
                    : ""),
                LogLevel.Info);

            int slotIndex = 0;
            var usedDepartureTimes = new HashSet<int>();
            foreach (var npc in spouses)
            {
                var state = GetOrCreateState(npc);
                if (state.Dispatched) continue;

                if (stayHomeNames.Contains(npc.Name))
                {
                    state.Dispatched           = true;
                    state.IsStayHome           = true;
                    TransitionScheduleContext(state, ScheduleContextPhase.AllDayStayHome);
                    state.WanderCooldownTicks  = Game1.random.Next(60, 180);
                    continue;
                }

                int baseSlot = 800 + slotIndex * 100;
                slotIndex++;
                BuildSchedule(npc, state, baseSlot, usedDepartureTimes);
                state.Dispatched = true;
            }
        }

        private void BuildSchedule(NPC npc, SpouseScheduleState state, int baseSlot, HashSet<int> usedDepartureTimes)
        {
            var legalPois = _poiRepo.GetFilteredPois(npc.Name);
            if (legalPois.Count == 0)
            {
                ModEntry.SMonitor?.Log($"[CSM] No legal POIs for {npc.Name} — fallback to stay home.", LogLevel.Info);
                state.IsStayHome           = true;
                TransitionScheduleContext(state, ScheduleContextPhase.AllDayStayHome);
                state.WanderCooldownTicks  = Game1.random.Next(60, 180);
                return;
            }

            var entries = _planner.BuildSchedule(legalPois, npc.Name, baseSlot, usedDepartureTimes);
            lock (state.Queue)
            {
                state.Queue.Clear();
                state.Queue.AddRange(entries);
            }

            ModEntry.SMonitor?.Log(
                $"[CSM] Schedule for {npc.Name}: " +
                $"{string.Join(" → ", entries.Select(e => $"{e.PoiId}@{e.DepartureTime}"))}",
                LogLevel.Info);
        }

        // ──────────────────────────────────────────────────────
        //  日程执行
        // ──────────────────────────────────────────────────────

        private void TryExecuteNextEntry(SpouseScheduleState state, int currentTime)
        {
            var npc = state.TrackedNpc;
            if (npc == null) return;

            if (state.IsStayHome) return;
            if (state.IsReturningHome) return;

            if (IsBlockedByDate(npc)) return;
            if (MultiMapNavigator.Instance.IsNavigating(npc)) return;

            ScheduledPoiEntry next;
            ScheduledPoiEntry currentActive;
            bool allExecuted;
            lock (state.Queue)
            {
                next = state.Queue.FirstOrDefault(e => !e.Executed && e.DepartureTime <= currentTime);
                if (next != null) next.Executed = true;

                // 队列按 DepartureTime 升序排列，"最后一个已执行"就是当前正在进行/刚出发的那一站。
                currentActive = state.Queue.LastOrDefault(e => e.Executed);
                allExecuted   = state.Queue.Count > 0 && state.Queue.All(e => e.Executed);
            }

            if (next != null)
            {
                SpouseDepartureRouter.ExecutePoiEntry(npc, next, state, TransitionScheduleContext);
                return;
            }

            bool queueEmpty;
            lock (state.Queue) { queueEmpty = state.Queue.Count == 0; }

            if (queueEmpty)
            {
                TryReturnHome(npc, state);
                return;
            }

            if (!allExecuted) return; // 还有条目在等出发时间，不用做任何事

            // 全部条目都已 Executed。是否该回家取决于 currentActive 的到达状态：
            //   - EndTime == null：说明 NPC 还没抵达（还在路上/寻路中），不能回家。
            //   - EndTime.HasValue 且 currentTime >= EndTime：停留时间已满，回家。
            // 仍未回家的配偶由 OnTimeChanged 的分批召回（1800~2100 每小时召回一名，2200 全部召回）兜底，
            // 防止卡到打烊/过夜。
            if (currentActive == null)
            {
                TryReturnHome(npc, state);
                return;
            }

            if (currentActive.EndTime.HasValue && currentTime >= currentActive.EndTime.Value)
            {
                ModEntry.SMonitor?.Log(
                    $"[CSM] {npc.Name} finished stay at '{currentActive.PoiId}' (EndTime={currentActive.EndTime}). Heading home.",
                    LogLevel.Info);
                TryReturnHome(npc, state);
            }
        }

        /// <summary>
        /// 18:00 并发召回所有外出配偶，带四重实体互斥守卫。
        /// 被跳过的配偶由 22:00 兜底 RecallAllRemainingSpouses 收口。
        /// </summary>
        private void RecallAllSpousesConcurrent(int currentTime)
        {
            int dispatchedCount = 0;
            int totalCount = 0;

            foreach (var state in _states.Values.ToArray())
            {
                var npc = state.TrackedNpc;
                if (npc == null) continue;
                if (state.IsStayHome || state.IsReturningHome) continue;

                totalCount++;

                if (MovementManager.Instance.IsFollowing(npc)) continue;
                if (MovementManager.Instance.IsNpcMoving(npc)) continue;
                if (npc.controller != null) continue;
                if (MultiMapNavigator.Instance.IsNavigating(npc)) continue;

                try { RecallSpouseNow(state, currentTime); dispatchedCount++; }
                catch (Exception ex) { ModEntry.SMonitor?.Log($"[CSM] Recall dispatch failed for {npc.Name}: {ex.Message}", LogLevel.Error); }
            }

            ModEntry.SMonitor?.Log(
                $"[CSM] evening return: dispatched {dispatchedCount}/{totalCount} spouses.", LogLevel.Info);
        }

        /// <summary>
        /// 把所有仍在外面的配偶全部触发回家。
        /// 2200 时就算多个 MoveToTile 互相覆盖、部分 NPC 没走完平滑回家动画。
        /// 换日安全网（OnDayEnding）会把漏网之鱼直接瞬移回家。分批回家只是尽力而为。
        /// RecallSpouseNow 内部的 Context.IsWorldReady 守卫负责拦截换日期间不该执行的回调。
        /// </summary>
        private void RecallAllRemainingSpouses()
        {
            foreach (var state in _states.Values.ToArray())
            {
                if (state.IsStayHome || state.IsReturningHome) continue;
                RecallSpouseNow(state, Game1.timeOfDay);
            }
        }

        /// <summary>
        /// 清理寻路状态后触发单个配偶的回家流程。
        /// </summary>
        private void RecallSpouseNow(SpouseScheduleState state, int currentTime)
        {
            // 换日期间 Context.IsWorldReady 会变为 false，此时延迟回调不应执行
            if (!Context.IsWorldReady) return;

            var npc = state.TrackedNpc;
            if (npc == null) return;
            if (state.IsStayHome || state.IsReturningHome) return; // 二次确认，延迟期间状态可能已变

            ModEntry.SMonitor?.Log(
                $"[CSM] Recalling {npc.Name} home at {currentTime}.", LogLevel.Info);

            // 清理进行中的寻路，防止召回后指令冲突
            MultiMapNavigator.Instance.Cancel(npc.Name);
            MovementManager.Instance.CancelMoveToTile(npc, invokeFailCallback: false);
            npc.controller = null;
            npc.addedSpeed = 0;
            npc.Halt();

            // 清空队列中未执行条目，防止 TryExecuteNextEntry 继续推进
            lock (state.Queue)
            {
                state.Queue.Clear();
            }

            // 重置出门标志，防止回家后 TickWander 重新触发出门
            state.IsDepartingToFarm = false;

            TryReturnHome(npc, state);
        }

        private void TryReturnHome(NPC npc, SpouseScheduleState state)
        {
            if (npc == null || state.IsReturningHome) return;

            if (string.Equals(npc.currentLocation?.Name, Game1.player?.currentLocation?.Name,
                    StringComparison.OrdinalIgnoreCase))
            {
                if (!state.WaitingForPlayerToLeave)
                {
                    state.WaitingForPlayerToLeave = true;
                    state.WaitingToDepart         = false;
                    state.PlayerLeaveWaitTicks    = 0;
                    state.LeaveWaitReason         = PlayerLeaveWaitReason.ReturnHome;
                    TransitionScheduleContext(state, ScheduleContextPhase.WaitingForPlayer);
                    ModEntry.SMonitor?.Log(
                        $"[CSM] {npc.Name} ready to go home but player is here — waiting.",
                        LogLevel.Info);
                }
                return;
            }

            state.WaitingForPlayerToLeave = false;
            ExecuteReturnHome(npc, state);
        }

        /// <summary>
        /// "平滑退场"模式：不判断玩家是否与 NPC 同图，时间到了直接触发回家流程。
        /// 供不希望在这一天做"等玩家离开"体验的调用方使用（比如约会/特殊事件结束后的收尾）。
        /// </summary>
        public void ForceReturnHomeNow(string npcName)
        {
            if (string.IsNullOrWhiteSpace(npcName)) return;
            if (!_states.TryGetValue(npcName, out var state)) return;
            var npc = state.TrackedNpc;
            if (npc == null) return;

            state.WaitingForPlayerToLeave = false;
            state.PlayerLeaveWaitTicks    = 0;
            state.LeaveWaitReason         = PlayerLeaveWaitReason.None;
            ExecuteReturnHome(npc, state);
        }

        private void ExecuteReturnHome(NPC npc, SpouseScheduleState state)
        {
            if (npc == null || state.IsReturningHome)
                return;

            state.IsReturningHome         = true;
            state.WaitingForPlayerToLeave = false;
            state.WaitingToDepart         = false;
            state.LeaveWaitReason         = PlayerLeaveWaitReason.None;
            TransitionScheduleContext(state, ScheduleContextPhase.ReturningHome, state.PreviousPoiId);

            var currentMap = npc.currentLocation?.Name ?? "";
            ModEntry.SMonitor?.Log(
                $"[CSM] {npc.Name} returning home from '{currentMap}' (viaBusStop={state.WentViaBusStop}).",
                LogLevel.Info);

            // 回家路线是否经巴士站，由出发时记录的 WentViaBusStop 决定——只有当初走的是
            // "农场→巴士站→对侧目的地" 这条路线，回家才对称地再经一次巴士站。
            FarmBusStopNavigator.ReturnHome(
                npc,
                state.WentViaBusStop,
                onArrivedHome: () =>
                {
                    state.IsReturningHome      = false;
                    state.IsStayHome           = true;
                    state.WentViaBusStop       = false;
                    state.WanderCooldownTicks  = Game1.random.Next(60, 180);
                    TransitionScheduleContext(state, ScheduleContextPhase.ArrivedHomeEarly);
                    ApplyHomeDispersal(npc);
                    ModEntry.SMonitor?.Log($"[CSM] {npc.Name} arrived home.", LogLevel.Info);
                },
                onFail: () =>
                {
                    // FarmBusStopNavigator 内部已经做了兜底瞬移，这里只需要把状态收尾成"已到家"，
                    // 避免因为某一段寻路失败就让 NPC 卡在 IsReturningHome=true 里出不来。
                    state.IsReturningHome      = false;
                    state.IsStayHome           = true;
                    state.WentViaBusStop       = false;
                    state.WanderCooldownTicks  = Game1.random.Next(60, 180);
                    TransitionScheduleContext(state, ScheduleContextPhase.ArrivedHomeEarly);
                    ApplyHomeDispersal(npc);
                    ModEntry.SMonitor?.Log(
                        $"[CSM] {npc.Name} return-home had a pathing hiccup but was warped inside as fallback.",
                        LogLevel.Warn);
                });
        }


        /// <summary>
        /// 到家后对 NPC 落脚点做离散处理，避免多配偶精准重叠在同一坐标。
        /// </summary>
        private static void ApplyHomeDispersal(NPC npc)
        {
            if (npc?.currentLocation == null) return;
            var dispersed = GetDispersedWalkableTile(npc.currentLocation, npc.Tile, npc);
            if (dispersed != npc.Tile)
            {
                npc.position.X = dispersed.X * 64f;
                npc.position.Y = dispersed.Y * 64f;
            }
        }

        // ──────────────────────────────────────────────────────
        //  工具方法
        // ──────────────────────────────────────────────────────
        private void ResetAllStates()
        {
            foreach (var s in _states.Values)
            {
                var npc = s.TrackedNpc;
                if (npc != null)
                {
                    MultiMapNavigator.Instance.Cancel(npc.Name);
                    MovementManager.Instance.CancelMoveToTile(npc, invokeFailCallback: false);
                    npc.controller = null;
                    npc.addedSpeed = 0;
                    npc.Halt();
                }
                lock (s.Queue) { s.Queue.Clear(); }

                // 重置出门标志，防止跨日残留导致 TickWander 误判
                s.IsDepartingToFarm = false;
                s.HasDepartedToFarm = false;
            }
            _states.Clear();
        }

        private SpouseScheduleState GetOrCreateState(NPC npc)
        {
            if (!_states.TryGetValue(npc.Name, out var s))
            {
                s = new SpouseScheduleState { TrackedNpc = npc };
                _states[npc.Name] = s;
            }
            else
            {
                s.TrackedNpc = npc;
            }
            return s;
        }

        private static bool IsBlockedByDate(NPC npc)
            => DateManager.Instance.Phase != DatePhase.None &&
               string.Equals(DateManager.Instance.ActiveDateNpcName, npc.Name,
                   StringComparison.OrdinalIgnoreCase);

        // ──────────────────────────────────────────────────────
        //  兼容桩
        // ──────────────────────────────────────────────────────
        public void ClearScheduleForOverride(string reason = "Override", string npcName = null)
        {
            if (string.IsNullOrWhiteSpace(npcName))
            {
                foreach (var state in _states.Values)
                {
                    lock (state.Queue) { state.Queue.Clear(); }
                    state.IsReturningHome         = false;
                    state.WaitingForPlayerToLeave = false;
                    state.WaitingToDepart         = false;
                    state.OnFarmPoiArrived        = null;
                    TransitionScheduleContext(state, ScheduleContextPhase.None);
                    state.IsStayHome              = false;
                }
                ModEntry.SMonitor?.Log(
                    $"[CSM] ClearScheduleForOverride: all queues cleared (reason={reason}).",
                    LogLevel.Debug);
                return;
            }

            if (!_states.TryGetValue(npcName, out var s)) return;

            lock (s.Queue) { s.Queue.Clear(); }
            s.IsReturningHome         = false;
            s.WaitingForPlayerToLeave = false;
            s.WaitingToDepart         = false;
            s.OnFarmPoiArrived        = null;
            TransitionScheduleContext(s, ScheduleContextPhase.None);
            s.IsStayHome              = false;

            var npc = s.TrackedNpc;
            if (npc != null)
            {
                npc.controller = null;
                npc.addedSpeed = 0;
                npc.Halt();
            }

            ModEntry.SMonitor?.Log(
                $"[CSM] ClearScheduleForOverride: {npcName} queue cleared (reason={reason}).",
                LogLevel.Info);
        }

        public void SetStayHomeMode(string npcName)
        {
            if (string.IsNullOrWhiteSpace(npcName)) return;

            var npc = Game1.getCharacterFromName(npcName);
            if (npc == null) return;

            var state = GetOrCreateState(npc);

            lock (state.Queue) { state.Queue.Clear(); }
            state.IsStayHome              = true;
            state.IsReturningHome         = false;
            state.WaitingForPlayerToLeave = false;
            state.WaitingToDepart         = false;
            state.OnFarmPoiArrived        = null;
            TransitionScheduleContext(state, ScheduleContextPhase.AllDayStayHome);
            state.WanderCooldownTicks     = 60;

            if (!string.Equals(npc.currentLocation?.Name, "Farm",      StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(npc.currentLocation?.Name, "FarmHouse", StringComparison.OrdinalIgnoreCase))
            {
                var (homeMap, homeTile) = GetHomeDestinationPublic(npc);
                Game1.warpCharacter(npc, homeMap, new Point((int)homeTile.X, (int)homeTile.Y));
            }

            npc.controller = null;
            npc.addedSpeed = 0;
            npc.Halt();
            MultiMapNavigator.Instance.Cancel(npc.Name);
            MovementManager.Instance.CancelMoveToTile(npc, invokeFailCallback: false);

            ModEntry.SMonitor?.Log($"[CSM] SetStayHomeMode: {npcName} will stay home today.", LogLevel.Info);
        }

        public void SetAllDayFollow(string npcName)
        {
            if (string.IsNullOrWhiteSpace(npcName)) return;

            var npc = Game1.getCharacterFromName(npcName);
            if (npc == null) return;

            var state = GetOrCreateState(npc);

            lock (state.Queue) { state.Queue.Clear(); }
            state.IsStayHome              = false;
            state.IsReturningHome         = false;
            state.WaitingForPlayerToLeave = false;
            state.WaitingToDepart         = false;
            state.OnFarmPoiArrived        = null;
            TransitionScheduleContext(state, ScheduleContextPhase.FollowingPlayer);
            state.Dispatched              = true;

            npc.controller = null;
            npc.addedSpeed = 0;
            npc.Halt();
            MultiMapNavigator.Instance.Cancel(npc.Name);
            MovementManager.Instance.CancelMoveToTile(npc, invokeFailCallback: false);

            int endTime = 2000;
            MovementManager.Instance.StartRegularFollow(npc, endTime);

            ModEntry.SMonitor?.Log($"[CSM] SetAllDayFollow: {npcName} will follow until {endTime}.", LogLevel.Info);
        }

        public void Cleanup()
        {
            _planner.ClearHistory();
            ResetAllStates();
        }

        /// <summary>
        /// 当配偶日程在设置中被关闭时，将当前仍在外部的配偶安全送回农舍，防止被遗弃在外。
        /// </summary>
        public void SafeDismissAllSpousesToHome()
        {
            foreach (var state in _states.Values.ToArray())
            {
                var npc = state.TrackedNpc;
                if (npc == null) continue;

                // 已在农舍内，仅重置状态
                if (string.Equals(npc.currentLocation?.Name, "FarmHouse", StringComparison.OrdinalIgnoreCase))
                    continue;

                try
                {
                    var (homeMap, homeTile) = GetHomeDestinationPublic(npc);
                    Game1.warpCharacter(npc, homeMap, new Point((int)homeTile.X, (int)homeTile.Y));
                    npc.Halt();
                    npc.controller = null;
                    npc.addedSpeed = 0;
                }
                catch (Exception ex)
                {
                    ModEntry.SMonitor?.Log(
                        $"[CSM] Failed to safely warp {npc.Name} home on toggle off: {ex.Message}",
                        LogLevel.Warn);
                }
            }
            ResetAllStates();
            ModEntry.SMonitor?.Log("[CSM] SafeDismissAllSpousesToHome completed.", LogLevel.Info);
        }

        // ──────────────────────────────────────────────────────
        //  ★ 核心：支持多配偶独立住所（static，兼容外部调用）
        // ──────────────────────────────────────────────────────
        // ★ 住所定位已统一委托至 SpouseQueryService
        public static (string MapName, Vector2 Tile) GetHomeDestinationPublic(NPC npc)
            => SpouseQueryService.Instance.GetHomeDestination(npc);


        /// <summary>
        /// 在目标瓦片周围寻找真正离散、且当前没有其他 NPC 占据的可行空地。
        /// 用于多配偶回家时避免精准重叠在同一坐标。
        /// </summary>
        public static Vector2 GetDispersedWalkableTile(GameLocation loc, Vector2 center, NPC npc, int maxRadius = 3)
        {
            if (loc == null) return center;

            var candidates = new List<Vector2>();
            for (int r = 1; r <= maxRadius; r++)
            {
                for (int x = -r; x <= r; x++)
                {
                    for (int y = -r; y <= r; y++)
                    {
                        if (Math.Abs(x) != r && Math.Abs(y) != r) continue; // 仅检查当前半径的外圈
                        var tile = new Vector2(center.X + x, center.Y + y);
                        if (MovementPathfinding.IsTileWalkable(loc, tile, npc))
                        {
                            // 农舍内：避开正门落脚点周围通道，防止配偶离散后堵门
                            if (string.Equals(loc.Name, "FarmHouse", StringComparison.OrdinalIgnoreCase))
                            {
                                if (Math.Abs(tile.X - center.X) <= 1 && tile.Y >= center.Y - 1)
                                    continue;
                            }

                            // 检查是否有其他 NPC 正好站在此处
                            bool occupiedByOtherNpc = loc.characters.Any(c => c != null && c != npc && c.Tile == tile);
                            if (!occupiedByOtherNpc)
                                candidates.Add(tile);
                        }
                    }
                }
                // 当前半径只要找到了可用离散点，就从中随机抽取一个返回，优先靠近中心
                if (candidates.Count > 0)
                {
                    return candidates[Game1.random.Next(candidates.Count)];
                }
            }

            return center;
        }

        // ──────────────────────────────────────────────────────
        //  跟随结束后恢复日程
        // ──────────────────────────────────────────────────────
        public void ResumeScheduleAfterFollow(NPC npc)
        {
            if (npc == null) return;
            if (!_states.TryGetValue(npc.Name, out var state)) return;

            if (Game1.timeOfDay < 1800)
            {
                ModEntry.SMonitor?.Log(
                    $"[CSM] {npc.Name} follow ended before 18:00 — rebuilding schedule.", LogLevel.Info);

                lock (state.Queue) { state.Queue.Clear(); }
                state.IsStayHome              = false;
                state.IsReturningHome         = false;
                state.WaitingForPlayerToLeave = false;
                state.WaitingToDepart         = false;
                state.OnFarmPoiArrived        = null;
                state.ActivePoiDescription    = "";

                var legalPois = _poiRepo.GetFilteredPois(npc.Name);
                if (legalPois.Count > 0)
                {
                    var usedTimes = new HashSet<int>();
                    var entries = _planner.BuildSchedule(legalPois, npc.Name, Game1.timeOfDay, usedTimes);
                    lock (state.Queue) { state.Queue.AddRange(entries); }

                    ModEntry.SMonitor?.Log(
                        $"[CSM] {npc.Name} rebuilt schedule: " +
                        $"{string.Join(" → ", entries.Select(e => $"{e.PoiId}@{e.DepartureTime}"))}",
                        LogLevel.Info);
                }

                if (string.Equals(npc.currentLocation?.Name, "Farm",      StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(npc.currentLocation?.Name, "FarmHouse", StringComparison.OrdinalIgnoreCase))
                {
                    ModEntry.SMonitor?.Log($"[CSM] {npc.Name} already on farm, schedule will tick normally.", LogLevel.Debug);
                }
                else
                {
                    state.WaitingForPlayerToLeave = true;
                    state.LeaveWaitReason         = PlayerLeaveWaitReason.DepartToSchedule;
                    state.PlayerLeaveWaitTicks    = 0;
                    TransitionScheduleContext(state, ScheduleContextPhase.WaitingForPlayer);
                    ModEntry.SMonitor?.Log(
                        $"[CSM] {npc.Name} is at '{npc.currentLocation?.Name}' — waiting for player to leave before departing.",
                        LogLevel.Info);
                }
            }
            else
            {
                ModEntry.SMonitor?.Log(
                    $"[CSM] {npc.Name} follow ended at or after 18:00 — going home.", LogLevel.Info);

                lock (state.Queue) { state.Queue.Clear(); }
                state.IsReturningHome         = false;
                state.WaitingForPlayerToLeave = false;
                state.WaitingToDepart         = false;
                state.OnFarmPoiArrived        = null;

                TryReturnHome(npc, state);
            }
        }

        public bool HasCustomScheduleToday(string npcName)
        {
            if (string.IsNullOrWhiteSpace(npcName)) return false;
            return _states.TryGetValue(npcName, out var s) && s.Dispatched;
        }

        public bool IsStayHomeActive(string npcName = null)
        {
            if (string.IsNullOrWhiteSpace(npcName))
                return _states.Values.Any(s => s.IsStayHome);
            return _states.TryGetValue(npcName, out var s) && s.IsStayHome;
        }

        // ──────────────────────────────────────────────────────
    }
}