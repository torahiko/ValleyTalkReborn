using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Newtonsoft.Json;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

namespace ValleytalkReborn
{
    // ─── PolyamorySweet 接口定义 ──────────────────────────────────────────
    public interface IPolyamorySweetApi
    {
        Dictionary<string, NPC> GetSpouses(Farmer farmer, bool all = false);
    }

    public interface ISweetRoomsAPI
    {
        Point GetSpouseTileOffset(NPC spouse);
        Point GetSpouseTile(NPC spouse);
        Point GetSpouseRoomCornerTile(NPC spouse);
        void ResetRooms(GameLocation location);
    }

    // ─── 数据类 ──────────────────────────────────────────────
    public class PoiConditions
    {
        [JsonProperty("AllowedSeasons")] public List<string> AllowedSeasons { get; set; } = new();
        [JsonProperty("AllowedWeather")]  public List<string> AllowedWeather  { get; set; } = new();
        [JsonProperty("TimeRange")]       public List<int>    TimeRange       { get; set; } = new();
    }

    public class PoiTile
    {
        [JsonProperty("X")] public int X { get; set; }
        [JsonProperty("Y")] public int Y { get; set; }
    }

    public class PoiAsset
    {
        [JsonProperty("MapName")]           public string        MapName           { get; set; } = "";
        [JsonProperty("TargetTile")]        public PoiTile       TargetTile        { get; set; } = new();
        [JsonProperty("Conditions")]        public PoiConditions Conditions        { get; set; } = new();
        [JsonProperty("CsharpAnimation")]   public string        CsharpAnimation   { get; set; } = "";
        [JsonProperty("DescriptionForLLM")] public string        DescriptionForLLM { get; set; } = "";

        /// <summary>该地点建议停留的游戏分钟数。未在 JSON 里配置时默认为 90 分钟。</summary>
        [JsonProperty("StayMinutes")]       public int           StayMinutes       { get; set; } = 90;
    }

    public class NpcPoiPreference
    {
        [JsonProperty("PoiId")]  public string PoiId  { get; set; } = "";
        [JsonProperty("Weight")] public int    Weight { get; set; } = 50;
    }

    public class NpcPreference
    {
        [JsonProperty("PreferredPois")] public List<NpcPoiPreference> PreferredPois { get; set; } = new();
    }

    internal class ScheduledPoiEntry
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
        /// CompanionScheduleManager.MarkEntryArrived）；在此之前为 null，
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
        private const string POI_ASSET_KEY  = "ValleytalkReborn/GlobalPoiAssets";
        private const string PREF_ASSET_KEY = "ValleytalkReborn/NpcPreferences";
        private const int    MaxOutCount    = 4;

        private const int WANDER_COOLDOWN_MIN = 1800;
        private const int WANDER_COOLDOWN_MAX = 3600;

        // Farm 游荡锚点半径（格）
        private const int FARM_WANDER_RADIUS = 8;

        private Dictionary<string, PoiAsset>      _poiAssets      = new(StringComparer.OrdinalIgnoreCase);
        private Dictionary<string, NpcPreference> _npcPreferences = new(StringComparer.OrdinalIgnoreCase);
        private bool _assetsLoaded     = false;
        private bool _eventsSubscribed = false;

        private readonly Dictionary<string, SpouseScheduleState> _states
            = new(StringComparer.OrdinalIgnoreCase);

        // PolyamorySweet APIs
        private readonly Dictionary<string, Queue<string>> _recentPoiHistory
            = new(StringComparer.OrdinalIgnoreCase);

        // PolyamorySweet APIs
        private IPolyamorySweetApi _psApi           = null;
        private ISweetRoomsAPI     _sweetRoomsApi   = null;
        //private bool _psApiResolved       = false;
        //private bool _sweetRoomsResolved  = false;

        private static readonly string[] PolyamoryModIds =
        {
            "ApryllForever.PolyamorySweetLove",
            "ApryllForever.PolyamorySweet",
            "Omegasis.PolyamorySweetLove",
            "PeacefulEnd.PolyamorySweet"
        };

        private CompanionScheduleManager() { }

        // ──────────────────────────────────────────────────────
        //  初始化
        // ──────────────────────────────────────────────────────
        private void EnsureEventsSubscribed()
        {
            if (_eventsSubscribed || ModEntry.SHelper == null) return;
            ModEntry.SHelper.Events.GameLoop.GameLaunched += OnGameLaunched;
            ModEntry.SHelper.Events.GameLoop.SaveLoaded   += OnSaveLoaded;
            ModEntry.SHelper.Events.GameLoop.DayStarted   += OnDayStarted;
            ModEntry.SHelper.Events.GameLoop.TimeChanged  += OnTimeChanged;
            ModEntry.SHelper.Events.GameLoop.DayEnding    += OnDayEnding;
            ModEntry.SHelper.Events.GameLoop.UpdateTicked += OnUpdateTicked;
            _eventsSubscribed = true;
        }

        private void OnGameLaunched(object sender, GameLaunchedEventArgs e)
        {
            ResolvePsApi();
            ResolveSweetRoomsApi();
        }

        private void ResolvePsApi()
        {
            if (_psApi != null) return; 

            foreach (var modId in PolyamoryModIds)
            {
                try
                {
                    _psApi = ModEntry.SHelper.ModRegistry.GetApi<IPolyamorySweetApi>(modId);
                    if (_psApi != null)
                    {
                        ModEntry.SMonitor?.Log($"[CSM] PolyamorySweet connected ({modId}).", LogLevel.Info);
                        return;
                    }
                }
                catch { }
            }
            ModEntry.SMonitor?.Log("[CSM] PolyamorySweet not found — vanilla spouse mode.", LogLevel.Info);
        }

        private void ResolveSweetRoomsApi()
        {
            if (_sweetRoomsApi != null) return; // ★ 只要已连上就直接退出

            foreach (var modId in PolyamoryModIds)
            {
                try
                {
                    _sweetRoomsApi = ModEntry.SHelper.ModRegistry.GetApi<ISweetRoomsAPI>(modId);
                    if (_sweetRoomsApi != null)
                    {
                        ModEntry.SMonitor?.Log($"[CSM] SweetRoomsAPI connected ({modId}).", LogLevel.Info);
                        return;
                    }
                }
                catch { }
            }
        }

        // ──────────────────────────────────────────────────────
        //  对外接口
        // ──────────────────────────────────────────────────────
        public string GetActivePoiContext(string npcName)
        {
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

        public static bool IsLegalSpouse(string npcName)
        {
            if (string.IsNullOrWhiteSpace(npcName)) return false;
            Instance.ResolvePsApi();
            var player = Game1.player;
            if (player == null) return false;

            if (Instance._psApi != null)
            {
                try
                {
                    var spouses = Instance._psApi.GetSpouses(player, all: true);
                    if (spouses?.ContainsKey(npcName) == true) return true;
                }
                catch { }
            }

            if (player.friendshipData?.TryGetValue(npcName, out var f) == true
                && f != null && (f.IsMarried() || f.IsRoommate()))
                return true;

            return string.Equals(player.spouse, npcName, StringComparison.OrdinalIgnoreCase);
        }

        // ──────────────────────────────────────────────────────
        //  资产加载
        // ──────────────────────────────────────────────────────
        public void LoadAssets()
        {
            if (_assetsLoaded) return;
            try
            {
                _poiAssets = ModEntry.SHelper.GameContent
                    .Load<Dictionary<string, PoiAsset>>(POI_ASSET_KEY)
                    ?? new(StringComparer.OrdinalIgnoreCase);

                _npcPreferences = ModEntry.SHelper.GameContent
                    .Load<Dictionary<string, NpcPreference>>(PREF_ASSET_KEY)
                    ?? new(StringComparer.OrdinalIgnoreCase);

                _assetsLoaded = true;
                ModEntry.SMonitor?.Log(
                    $"[CSM] Assets loaded — {_poiAssets.Count} POIs, {_npcPreferences.Count} prefs.",
                    LogLevel.Info);
            }
            catch (Exception ex)
            {
                ModEntry.SMonitor?.Log($"[CSM] Asset load failed: {ex.Message}", LogLevel.Error);
                _poiAssets      = new(StringComparer.OrdinalIgnoreCase);
                _npcPreferences = new(StringComparer.OrdinalIgnoreCase);
            }
        }

        public void ReloadAssets() { _assetsLoaded = false; LoadAssets(); }

        // ──────────────────────────────────────────────────────
        //  事件回调
        // ──────────────────────────────────────────────────────
        private void OnSaveLoaded(object sender, SaveLoadedEventArgs e)
        {
            _assetsLoaded = false;
            LoadAssets();
            ResetAllStates();

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

            if (!_assetsLoaded) LoadAssets();
        }

        private void OnTimeChanged(object sender, TimeChangedEventArgs e)
        {
            if (Utility.isFestivalDay(Game1.dayOfMonth, Game1.season)) return;

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

            if (e.NewTime == 2000)
                MultiMapNavigator.Instance.CancelAll();

            // 分批回家调度：18:00~21:00 每小时随机召回一名配偶；22:00 全部召回。
            // 错峰触发是为了绕开 MovementManager._goto 单槽竞争（详见 RecallAllRemainingSpouses）。
            if (e.NewTime is 1800 or 1900 or 2000 or 2100)
            {
                TryRecallOneSpouse(e.NewTime);
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

                if (!state.IsStayHome && state.Queue.Count > 0)
                {
                    if (!_recentPoiHistory.TryGetValue(npcName, out var q))
                    {
                        q = new Queue<string>();
                        _recentPoiHistory[npcName] = q;
                    }
                    foreach (var entry in state.Queue.Where(en => en.Executed && en.PoiId != null))
                    {
                        q.Enqueue(entry.PoiId);
                    }
                    // 限制历史上限为 6 个（约 2 天访问量）
                    while (q.Count > 6) q.Dequeue();
                }
            }

            // 清理不再处于合法婚姻关系的历史键
            var staleKeys = _recentPoiHistory.Keys
                .Where(name => !IsLegalSpouse(name))
                .ToList();
            foreach (var key in staleKeys)
                _recentPoiHistory.Remove(key);

            ResetAllStates();
            MultiMapNavigator.Instance.CancelAll();
        }

        // ──────────────────────────────────────────────────────
        //  UpdateTicked — 居家游荡驱动
        // ──────────────────────────────────────────────────────
        private void OnUpdateTicked(object sender, UpdateTickedEventArgs e)
        {
            if (!Context.IsWorldReady) return;
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
                    TickWander(state);
                    continue;
                }

                if (state.WaitingForPlayerToLeave)
                    TickWaitForPlayerLeave(state);
            }
        }

        private void TickWander(SpouseScheduleState state)
        {
            var npc = state.TrackedNpc;
            if (npc == null || npc.currentLocation == null) return;
            if (Game1.activeClickableMenu != null || Game1.dialogueUp) return;

            // 正在回家中，不游荡
            if (state.IsReturningHome) return;

            // 正在出门途中，不重复触发
            if (state.IsDepartingToFarm) return;

            var loc = npc.currentLocation;
            bool isFarmHouse = string.Equals(loc.Name, "FarmHouse", StringComparison.OrdinalIgnoreCase);
            bool isFarm      = string.Equals(loc.Name, "Farm",      StringComparison.OrdinalIgnoreCase);

            // ── 天气守卫（最外层）──
            bool badWeather = Game1.isRaining || Game1.isSnowing || Game1.isLightning;

            // 恶劣天气在 Farm 上：平滑走回室内，不游荡
            if (badWeather && isFarm)
            {
                ModEntry.SMonitor?.Log(
                    $"[CSM] Bad weather — {npc.Name} returning indoors.", LogLevel.Debug);
                state.IsDepartingToFarm = true; // 占位，防止重入
                FarmBusStopNavigator.ReturnHome(npc, wentViaBusStop: false,
                    onArrivedHome: () =>
                    {
                        state.IsDepartingToFarm  = false;
                        state.HasDepartedToFarm  = false;
                        state.WanderCooldownTicks = WANDER_COOLDOWN_MIN;
                    },
                    onFail: () =>
                    {
                        state.IsDepartingToFarm = false;
                    });
                return;
            }

            // 恶劣天气且在 FarmHouse：只允许室内游荡，不出门
            // （直接落到下面的冷却/游荡逻辑，不执行出门分支）

            if (state.WanderCooldownTicks > 0)
            {
                state.WanderCooldownTicks--;
                return;
            }

            if (IsBlockedByDate(npc)) return;
            if (MultiMapNavigator.Instance.IsNavigating(npc)) return;
            if (MovementManager.Instance.CurrentFollowingNpc == npc) return;
            // 全局 _goto 单槽：若任一 NPC 正在 GoTo 途中，让位并重试下一帧，避免静默覆盖
            if (MovementManager.Instance.IsMoving) return;
            if (npc.controller != null && !MovementPathfinding.IsPathDone(npc.controller)) return;

            if (npc.controller != null)
            {
                npc.controller = null;
                npc.addedSpeed = 0;
                npc.Halt();
            }

            // ── 晴天 + 在 FarmHouse + 尚未出门：触发出门流程 ──
            if (!badWeather && isFarmHouse && !state.HasDepartedToFarm)
            {
                var farm      = Game1.getFarm();
                var farmEntry = farm.GetMainFarmHouseEntry();
                var destTile  = MovementPathfinding.FindNearestWalkableTile(
                    farm, new Vector2(farmEntry.X, farmEntry.Y + 1), npc, 3);

                state.IsDepartingToFarm   = true;
                state.WanderCooldownTicks = WANDER_COOLDOWN_MIN; // 出门期间屏蔽冷却重置

                ModEntry.SMonitor?.Log(
                    $"[CSM] {npc.Name} sunny day — departing FarmHouse to Farm.", LogLevel.Info);

                FarmBusStopNavigator.DepartFromFarmHouse(npc, "Farm", destTile,
                    onArrived: (_) =>
                    {
                        state.IsDepartingToFarm   = false;
                        state.HasDepartedToFarm   = true;
                        state.WanderCooldownTicks = WANDER_COOLDOWN_MIN +
                            Game1.random.Next(WANDER_COOLDOWN_MAX - WANDER_COOLDOWN_MIN);
                        ModEntry.SMonitor?.Log(
                            $"[CSM] {npc.Name} arrived on Farm for outdoor wander.", LogLevel.Info);
                    },
                    onFail: () =>
                    {
                        state.IsDepartingToFarm   = false;
                        state.WanderCooldownTicks = WANDER_COOLDOWN_MIN;
                        ModEntry.SMonitor?.Log(
                            $"[CSM] {npc.Name} depart to Farm failed — staying indoors.", LogLevel.Warn);
                    });
                return;
            }

            // ── 40% 概率原地小动作 ──
            if (Game1.random.Next(100) < 40)
            {
                npc.faceDirection(Game1.random.Next(4));
                if (Game1.random.Next(100) < 50) npc.doEmote(Game1.random.Next(2) == 0 ? 32 : 8);
                state.WanderCooldownTicks = WANDER_COOLDOWN_MIN / 2 +
                    Game1.random.Next(WANDER_COOLDOWN_MIN / 2);
                return;
            }

            // ── 选取游荡目标点 ──
            var target = PickWanderTargetTile(npc, loc, isFarm);
            if (target == null)
            {
                state.WanderCooldownTicks = WANDER_COOLDOWN_MIN / 3;
                return;
            }

            if (!MovementPathfinding.TryCreatePath(npc, loc, target.Value, out var controller, out _))
            {
                state.WanderCooldownTicks = WANDER_COOLDOWN_MIN / 3;
                return;
            }

            npc.controller = controller;
            npc.addedSpeed = 0;
            state.WanderCooldownTicks = WANDER_COOLDOWN_MIN +
                Game1.random.Next(WANDER_COOLDOWN_MAX - WANDER_COOLDOWN_MIN);
        }

        /// <summary>
        /// 为游荡选取目标点：以当前 NPC 位置为中心，外圈逐层扫描可行走 tile，
        /// 收集候选后随机选一个。职责与 GetDispersedWalkableTile（防重叠）不同，独立实现。
        /// </summary>
        private static Vector2? PickWanderTargetTile(NPC npc, GameLocation loc, bool isFarm)
        {
            // 获取约束锚点
            Point doorTile  = Point.Zero;
            Vector2 farmAnchor = Vector2.Zero;

            if (!isFarm)
            {
                // 室内：取 FarmHouse→Farm warp 坐标作为禁足中心
                var exitWarp = loc.warps?.FirstOrDefault(
                    w => string.Equals(w?.TargetName, "Farm", StringComparison.OrdinalIgnoreCase));
                if (exitWarp != null) doorTile = new Point(exitWarp.X, exitWarp.Y);
            }
            else
            {
                farmAnchor = new Vector2(
                    Game1.getFarm().GetMainFarmHouseEntry().X,
                    Game1.getFarm().GetMainFarmHouseEntry().Y);
            }

            var candidates = new List<Vector2>();

            for (int r = 1; r <= 5; r++)
            {
                for (int x = -r; x <= r; x++)
                {
                    for (int y = -r; y <= r; y++)
                    {
                        if (Math.Abs(x) != r && Math.Abs(y) != r) continue; // 仅外圈

                        var tile = new Vector2(npc.Tile.X + x, npc.Tile.Y + y);

                        // 室内：门口禁足区（warp 周围 2 格内不停留）
                        if (!isFarm && doorTile != Point.Zero)
                        {
                            if (Math.Abs(tile.X - doorTile.X) <= 1 &&
                                Math.Abs(tile.Y - doorTile.Y) <= 2)
                                continue;
                        }

                        // 室外：锚定在农舍门口 FARM_WANDER_RADIUS 格以内
                        if (isFarm && farmAnchor != Vector2.Zero)
                        {
                            if (Vector2.Distance(tile, farmAnchor) > FARM_WANDER_RADIUS)
                                continue;
                        }

                        if (MovementPathfinding.IsTileWalkable(loc, tile, npc))
                            candidates.Add(tile);
                    }
                }

                if (candidates.Count >= 3) break; // 找到足够候选就停止扩展
            }

            if (candidates.Count == 0) return null;
            return candidates[Game1.random.Next(candidates.Count)];
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
        public List<NPC> GetAllMarriedNpcs()
        {
            var results = new Dictionary<string, NPC>(StringComparer.OrdinalIgnoreCase);
            var player  = Game1.player;
            if (player == null) return new();

            if (_psApi != null)
            {
                try
                {
                    var spouses = _psApi.GetSpouses(player, all: true);
                    if (spouses != null)
                        foreach (var kv in spouses)
                            if (kv.Value != null) results.TryAdd(kv.Key, kv.Value);
                }
                catch { }
            }

            if (player.friendshipData != null)
                foreach (var pair in player.friendshipData.Pairs)
                    if (pair.Value != null && (pair.Value.IsMarried() || pair.Value.IsRoommate()))
                        if (!results.ContainsKey(pair.Key))
                        {
                            var c = Game1.getCharacterFromName(pair.Key);
                            if (c != null) results[pair.Key] = c;
                        }

            if (results.Count == 0 && !string.IsNullOrEmpty(player.spouse))
            {
                var c = Game1.getCharacterFromName(player.spouse);
                if (c != null) results[player.spouse] = c;
            }

            return results.Values.ToList();
        }

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
            var legalPois = FilterLegalPois(npc.Name);
            if (legalPois.Count == 0)
            {
                ModEntry.SMonitor?.Log($"[CSM] No legal POIs for {npc.Name} — fallback to stay home.", LogLevel.Info);
                state.IsStayHome           = true;
                TransitionScheduleContext(state, ScheduleContextPhase.AllDayStayHome);
                state.WanderCooldownTicks  = Game1.random.Next(60, 180);
                return;
            }

            var entries = PickPoisWeighted(legalPois, npc.Name, baseSlot, usedDepartureTimes);
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
        //  全局出发时间去重
        // ──────────────────────────────────────────────────────
        private static int AllocateUniqueDepartureTime(
            int desired,
            int earliest,
            int latest,
            HashSet<int> usedTimes)
        {
            int time = Math.Max(earliest, Math.Min(latest, desired));

            // 优先按 10 分钟递增寻找未占用时间
            for (int i = 0; i < 120; i++)
            {
                int candidate = time + i * 10;
                if (candidate > latest || candidate >= 2000)
                    break;

                if (!usedTimes.Contains(candidate))
                {
                    usedTimes.Add(candidate);
                    return candidate;
                }
            }

            // 向前寻找
            for (int i = 1; i < 120; i++)
            {
                int candidate = time - i * 10;
                if (candidate < earliest)
                    break;

                if (!usedTimes.Contains(candidate))
                {
                    usedTimes.Add(candidate);
                    return candidate;
                }
            }

            // 实在无法错开时，仍然返回合法时间
            return time;
        }

        // ──────────────────────────────────────────────────────
        //  加权随机选 POI
        // ──────────────────────────────────────────────────────
        private List<ScheduledPoiEntry> PickPoisWeighted(
            Dictionary<string, PoiAsset> legalPois,
            string npcName,
            int baseSlot,
            HashSet<int> usedDepartureTimes)
        {
            _npcPreferences.TryGetValue(npcName, out var prefs);
            var baseWeights = prefs?.PreferredPois
                .ToDictionary(p => p.PoiId, p => p.Weight, StringComparer.OrdinalIgnoreCase)
                ?? new(StringComparer.OrdinalIgnoreCase);

            _recentPoiHistory.TryGetValue(npcName, out var historyQueue);
            var recentList = historyQueue?.ToList() ?? new List<string>();

            int currentSlot = baseSlot;
            int pickCount = Math.Min(Game1.random.Next(1, 4), legalPois.Count);
            var entries = new List<ScheduledPoiEntry>();
            var remainingPois = new Dictionary<string, PoiAsset>(legalPois, StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < pickCount; i++)
            {
                if (currentSlot >= 1800 || remainingPois.Count == 0) break;

                // 仅保留当前时刻后至少拥有 60 分钟窗口的 POI，彻底杜绝时间逆流
                var validCandidates = remainingPois.Where(kv =>
                {
                    var cond = kv.Value.Conditions ?? new PoiConditions();
                    int latest = cond.TimeRange?.Count == 2 ? cond.TimeRange[1] : 1900;
                    return latest >= currentSlot + 60;
                }).ToList();

                if (validCandidates.Count == 0) break;

                // 动态疲劳度衰减计算
                var weightedPool = validCandidates.Select(kv =>
                {
                    int w = baseWeights.TryGetValue(kv.Key, out int customWeight) ? customWeight : 50;

                    // 疲劳度衰减：近期访问过的 POI 大幅削减权重
                    int recentIndex = recentList.LastIndexOf(kv.Key);
                    if (recentIndex != -1)
                    {
                        int stepsBack = recentList.Count - 1 - recentIndex;
                        float penalty = stepsBack switch
                        {
                            0 => 0.25f, // 昨天刚去过
                            1 => 0.55f, // 前天去过
                            _ => 0.80f
                        };
                        w = Math.Max(5, (int)(w * penalty));
                    }
                    return (PoiId: kv.Key, Asset: kv.Value, Weight: w);
                }).ToList();

                int totalWeight = weightedPool.Sum(c => c.Weight);
                if (totalWeight <= 0) break;

                int roll = Game1.random.Next(totalWeight);
                int acc = 0;
                (string PoiId, PoiAsset Asset) selected = default;
                foreach (var candidate in weightedPool)
                {
                    acc += candidate.Weight;
                    if (roll < acc)
                    {
                        selected = (candidate.PoiId, candidate.Asset);
                        break;
                    }
                }

                if (selected.PoiId == null) break;

                var cond = selected.Asset.Conditions ?? new PoiConditions();
                int earliest = cond.TimeRange?.Count == 2 ? cond.TimeRange[0] : 700;
                int latest   = cond.TimeRange?.Count == 2 ? cond.TimeRange[1] : 1900;

                // 全局出发时间去重：每一站都分配独立无冲突的时间槽
                int depart = AllocateUniqueDepartureTime(currentSlot, earliest, latest, usedDepartureTimes);

                // 必须提取 POI 资产中配置的 StayMinutes 并赋值给 entry
                int stay = selected.Asset.StayMinutes > 0 ? selected.Asset.StayMinutes : 90;

                entries.Add(new ScheduledPoiEntry
                {
                    PoiId         = selected.PoiId,
                    Asset         = selected.Asset,
                    DepartureTime = depart,
                    StayMinutes   = stay
                });

                remainingPois.Remove(selected.PoiId);

                // 下一站推进：当前出发时间 + 停留时长 + 20~40 分钟的路程/缓冲时间
                currentSlot = MovementPathfinding.SafeAddGameTime(depart, stay + Game1.random.Next(2, 5) * 10);
            }

            entries.Sort((a, b) => a.DepartureTime.CompareTo(b.DepartureTime));
            return entries;
        }

        // ──────────────────────────────────────────────────────
        //  POI 过滤
        // ──────────────────────────────────────────────────────
        private Dictionary<string, PoiAsset> FilterLegalPois(string npcName)
        {
            if (!_assetsLoaded) LoadAssets();

            string season  = Game1.season.ToString().ToLowerInvariant();
            string weather = GetCurrentWeatherString();
            var result = new Dictionary<string, PoiAsset>(StringComparer.OrdinalIgnoreCase);
            if (_poiAssets == null) return result;

            foreach (var (poiId, asset) in _poiAssets)
            {
                if (asset == null) continue;
                var cond = asset.Conditions ?? new PoiConditions();

                if (cond.AllowedSeasons?.Count > 0 &&
                    !cond.AllowedSeasons.Any(s => s.Equals(season, StringComparison.OrdinalIgnoreCase)))
                    continue;

                if (cond.AllowedWeather?.Count > 0 &&
                    !cond.AllowedWeather.Any(w => w.Equals(weather, StringComparison.OrdinalIgnoreCase)))
                    continue;

                if (cond.TimeRange?.Count == 2)
                {
                    if (cond.TimeRange[1] <= Game1.timeOfDay || cond.TimeRange[0] >= 2000)
                        continue;
                }

                result[poiId] = asset;
            }

            ModEntry.SMonitor?.Log(
                $"[CSM] FilterLegalPois({npcName}): {result.Count}/{_poiAssets.Count} POIs pass " +
                $"(season={season}, weather={weather})",
                LogLevel.Debug);
            return result;
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
                ExecutePoiEntry(npc, next, state);
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
        /// NPC 真正抵达某个 POI 时调用，此时才把 EndTime 固定为"当前时间 + StayMinutes"。
        /// 这是"10分钟秒回家"bug 的根本修复点：EndTime 不再基于下单时的静态预测，
        /// 而是基于实际到达时刻，避免寻路延迟/中途被打断导致停留时间被压缩甚至为负。
        /// </summary>
        private static void MarkEntryArrived(ScheduledPoiEntry entry, int arrivalTime)
        {
            if (entry == null) return;
            int stay = entry.StayMinutes > 0 ? entry.StayMinutes : (entry.Asset?.StayMinutes ?? 90);
            entry.EndTime = Math.Min(MovementPathfinding.SafeAddGameTime(arrivalTime, stay), 1990);
        }

        private void ExecutePoiEntry(NPC npc, ScheduledPoiEntry entry, SpouseScheduleState state)
        {
            var asset  = entry.Asset;
            var target = new Vector2(asset.TargetTile?.X ?? 0, asset.TargetTile?.Y ?? 0);

            ModEntry.SMonitor?.Log(
                $"[CSM] {npc.Name} → '{entry.PoiId}' (map={asset.MapName}, tile={target.X},{target.Y}) @ {entry.DepartureTime}",
                LogLevel.Info);

            // ★ 离开前一状态，切换为在途上下文
            TransitionScheduleContext(state, ScheduleContextPhase.TravelingToPoi, entry.PoiId);

            bool targetIsOnFarm =
                string.Equals(asset.MapName, "Farm",      StringComparison.OrdinalIgnoreCase) ||
                string.Equals(asset.MapName, "FarmHouse", StringComparison.OrdinalIgnoreCase);

            if (targetIsOnFarm)
            {
                if (!string.Equals(npc.currentLocation?.Name, asset.MapName, StringComparison.OrdinalIgnoreCase))
                {
                    if (string.Equals(asset.MapName, "Farm", StringComparison.OrdinalIgnoreCase))
                    {
                        var farmEntry = Game1.getFarm().GetMainFarmHouseEntry();
                        Game1.warpCharacter(npc, "Farm", new Point(farmEntry.X, farmEntry.Y + 1));
                    }
                    else
                    {
                        var (homeMap, homeTile) = GetHomeDestinationPublic(npc);
                        Game1.warpCharacter(npc, homeMap, new Point((int)homeTile.X, (int)homeTile.Y));
                    }
                }

                var loc = npc.currentLocation;
                if (loc == null) return;

                // 验证 POI 目标点是否可走，并寻找附近安全点
                var requestedTarget = new Vector2(asset.TargetTile?.X ?? 0, asset.TargetTile?.Y ?? 0);
                target = MovementPathfinding.FindNearestWalkableTile(loc, requestedTarget, npc, 3);

                state.WentViaBusStop = false; // 目的地在农场同侧，出发时没有经过巴士站

                if (Vector2.Distance(npc.Tile, target) < 1f)
                {
                    // 已经站在目标点上，视为立即抵达。
                    MarkEntryArrived(entry, Game1.timeOfDay);
                    state.PreviousPoiId = entry.PoiId;
                    TransitionScheduleContext(state, ScheduleContextPhase.ActiveAtPoi, asset.DescriptionForLLM);
                    TryPlayAnimation(npc, asset.CsharpAnimation);
                    return;
                }

                if (MovementPathfinding.TryCreatePath(npc, loc, target, out var controller, out _))
                {
                    npc.controller = controller;
                    npc.addedSpeed = 2;
                    StartFarmPoiWatch(npc, state, entry);
                }
                else
                {
                    ModEntry.SMonitor?.Log(
                        $"[CSM] {npc.Name} cannot path to '{entry.PoiId}' on farm — standing in place.",
                        LogLevel.Info);
                    // 寻路失败也视为"抵达"（原地站着），否则会永远卡在"未到达"状态导致回家判断失灵。
                    MarkEntryArrived(entry, Game1.timeOfDay);
                    state.PreviousPoiId = entry.PoiId;
                    TransitionScheduleContext(state, ScheduleContextPhase.ActiveAtPoi, asset.DescriptionForLLM);
                    TryPlayAnimation(npc, asset.CsharpAnimation);
                }
                return;
            }

            // 非农场目的地：统一交给 FarmBusStopNavigator 处理"农舍/农场 → (可能经巴士站) → 目的地"。
            string currentMap = npc.currentLocation?.Name ?? "";
            bool startedFromFarmHouse = string.Equals(currentMap, "FarmHouse", StringComparison.OrdinalIgnoreCase);
            bool startedFromFarm      = string.Equals(currentMap, "Farm",      StringComparison.OrdinalIgnoreCase);

            void OnDepartArrived(bool wentViaBusStop)
            {
                state.WentViaBusStop = wentViaBusStop;
                MarkEntryArrived(entry, Game1.timeOfDay);
                // ★ 注入 POI 专属日程上下文，记录上一站 ID
                state.PreviousPoiId = entry.PoiId;
                TransitionScheduleContext(state, ScheduleContextPhase.ActiveAtPoi, asset.DescriptionForLLM);
                TryPlayAnimation(npc, asset.CsharpAnimation);
                ModEntry.SMonitor?.Log(
                    $"[CSM] {npc.Name} arrived at '{entry.PoiId}' (viaBusStop={wentViaBusStop}).",
                    LogLevel.Info);
            }

            void OnDepartFail()
            {
                ModEntry.SMonitor?.Log(
                    $"[CSM] {npc.Name} failed to reach '{entry.PoiId}' — marking arrived in place to avoid getting stuck.",
                    LogLevel.Warn);
                MarkEntryArrived(entry, Game1.timeOfDay);
                state.PreviousPoiId = entry.PoiId;
                TransitionScheduleContext(state, ScheduleContextPhase.ActiveAtPoi, asset.DescriptionForLLM);
            }

            // 只有当目的地恰好是"农场本身的某个坐标"时才会走到这个分支：此时 NPC 是刚发起寻路，
            // 还没真正到达，必须挂一个到达监听——复用 StartFarmPoiWatch 的 IsPathDone 轮询模式，
            // 不能提前把 EndTime 定死，否则又会退化成"发起寻路即视为抵达"的假到达问题。
            void OnPathStarted()
            {
                state.WentViaBusStop = false;
                StartFarmPoiWatch(npc, state, entry);
            }

            if (startedFromFarmHouse)
            {
                FarmBusStopNavigator.DepartFromFarmHouse(npc, asset.MapName, target, OnDepartArrived, OnDepartFail, OnPathStarted);
            }
            else if (startedFromFarm)
            {
                FarmBusStopNavigator.ContinueDepartFromFarm(npc, asset.MapName, target, OnDepartArrived, OnDepartFail, OnPathStarted);
            }
            else
            {
                // 非农舍/非农场出发：统一走就地寻路或就近退场，拒绝返回农场二次折返
                DepartFromOtherLocation(npc, state, entry);
            }
        }

        private void StartFarmPoiWatch(NPC npc, SpouseScheduleState state, ScheduledPoiEntry entry)
        {
            var asset = entry.Asset;
            state.OnFarmPoiArrived = () =>
            {
                MarkEntryArrived(entry, Game1.timeOfDay);
                state.PreviousPoiId = entry.PoiId;
                TransitionScheduleContext(state, ScheduleContextPhase.ActiveAtPoi, asset.DescriptionForLLM);
                ModEntry.SMonitor?.Log($"[CSM] {npc.Name} arrived at farm POI '{entry.PoiId}'.", LogLevel.Info);
                TryPlayAnimation(npc, asset.CsharpAnimation);
            };
        }

        private static Vector2 FindFarmExitTile(NPC npc)
        {
            try
            {
                var farm = Game1.getFarm();
                if (farm?.warps == null) goto fallback;

                var exits = farm.warps
                    .Where(w => w != null
                        && !string.Equals(w.TargetName, "FarmHouse", StringComparison.OrdinalIgnoreCase)
                        && !string.Equals(w.TargetName, "Cellar",    StringComparison.OrdinalIgnoreCase))
                    .ToList();

                if (exits.Count == 0) goto fallback;

                var nearest = exits
                    .OrderBy(w => Vector2.Distance(npc.Tile, new Vector2(w.X, w.Y)))
                    .First();

                var warpTile = new Vector2(nearest.X, nearest.Y);
                return MovementPathfinding.FindWalkableTileNearWarp(farm, warpTile, npc);
            }
            catch { }

        fallback:
            var entry = Game1.getFarm().GetMainFarmHouseEntry();
            return new Vector2(entry.X, entry.Y);
        }

        /// <summary>
        /// 从当前仍在外面的配偶中随机选一名触发回家。
        /// </summary>
        private void TryRecallOneSpouse(int currentTime)
        {
            var eligible = _states.Values
                .Where(s => !s.IsStayHome && !s.IsReturningHome)
                .ToList();

            if (eligible.Count == 0) return;

            var target = eligible[Game1.random.Next(eligible.Count)];
            RecallSpouseNow(target, currentTime);
        }

        /// <summary>
        /// 把所有仍在外面的配偶全部触发回家。
        /// 2200 时就算多个 MoveToTile 互相覆盖、部分 NPC 没走完平滑回家动画，玩家也不在乎——
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

        private static void TryPlayAnimation(NPC npc, string animationName)
        {
            if (string.IsNullOrWhiteSpace(animationName)) return;
            try
            {
                switch (animationName)
                {
                    case "PlayArcade":  npc.faceDirection(3); npc.doEmote(16); break;
                    case "SitOnBench":  npc.faceDirection(2);                  break;
                    case "FishingPose": npc.faceDirection(2); npc.doEmote(32); break;
                    case "BrowseShop":  npc.faceDirection(0);                  break;
                    default:
                        ModEntry.SMonitor?.Log(
                            $"[CSM] Unknown animation '{animationName}' for {npc.Name}.", LogLevel.Debug);
                        break;
                }
            }
            catch (Exception ex)
            {
                ModEntry.SMonitor?.Log($"[CSM] TryPlayAnimation error: {ex.Message}", LogLevel.Warn);
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

        private static string GetCurrentWeatherString()
        {
            if (Game1.isLightning) return "Stormy";
            if (Game1.isRaining)   return "Rainy";
            if (Game1.isSnowing)   return "Snowy";
            return "Sunny";
        }

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
            _assetsLoaded = false;
            _poiAssets?.Clear();
            _npcPreferences?.Clear();
            ResetAllStates();
        }

        // ──────────────────────────────────────────────────────
        //  ★ 核心：支持多配偶独立住所（static，兼容外部调用）
        // ──────────────────────────────────────────────────────
        public static (string MapName, Vector2 Tile) GetHomeDestinationPublic(NPC npc)
        {
            var inst = _instance;

            // 1. 优先使用 PolyamorySweet 的房间 API
            if (inst?._sweetRoomsApi != null)
            {
                try
                {
                    Point cornerTile = inst._sweetRoomsApi.GetSpouseRoomCornerTile(npc);
                    // 直接固定为 FarmHouse，避免坐标被误认为 Farm
                    string mapName = "FarmHouse";
                    ModEntry.SMonitor?.Log(
                        $"[CSM] Using SweetRooms for {npc.Name} → {mapName} ({cornerTile.X}, {cornerTile.Y})",
                        LogLevel.Debug);
                    return (mapName, new Vector2(cornerTile.X, cornerTile.Y));
                }
                catch (Exception ex)
                {
                    ModEntry.SMonitor?.Log($"[CSM] SweetRooms API failed for {npc.Name}: {ex.Message}", LogLevel.Warn);
                }
            }

            // 2. 后备：使用 npc.DefaultMap
            if (!string.IsNullOrWhiteSpace(npc.DefaultMap))
            {
                var homeLoc = Game1.getLocationFromName(npc.DefaultMap);
                if (homeLoc != null)
                {
                    var warpToFarm = homeLoc.warps?.FirstOrDefault(w =>
                        w != null && string.Equals(w.TargetName, "Farm", StringComparison.OrdinalIgnoreCase));
                    if (warpToFarm != null)
                        return (npc.DefaultMap, new Vector2(warpToFarm.X, warpToFarm.Y));

                    return (npc.DefaultMap, new Vector2(8, 9));
                }
            }

            // 3. 最终后备：原版农舍
            try
            {
                var entry = Game1.getFarm().GetMainFarmHouseEntry();
                return ("FarmHouse", new Vector2(entry.X, entry.Y));
            }
            catch
            {
                return ("FarmHouse", new Vector2(8, 9));
            }
        }


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

                var legalPois = FilterLegalPois(npc.Name);
                if (legalPois.Count > 0)
                {
                    var usedTimes = new HashSet<int>();
                    var entries = PickPoisWeighted(legalPois, npc.Name, Game1.timeOfDay, usedTimes);
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
        //  非农舍/农场出发（中途打断、在外部地图时）的通用离场导航
        //  核心原则：就地就近退场，直接前往目的地，绝不回农场兜圈子
        // ──────────────────────────────────────────────────────
        private void DepartFromOtherLocation(NPC npc, SpouseScheduleState state, ScheduledPoiEntry entry)
        {
            var asset  = entry.Asset;
            var target = new Vector2(asset.TargetTile?.X ?? 0, asset.TargetTile?.Y ?? 0);
            var loc    = npc.currentLocation;

            state.WentViaBusStop = false;

            if (loc == null)
            {
                MultiMapNavigator.WarpDirectTo(npc, asset.MapName, target);
                MarkEntryArrived(entry, Game1.timeOfDay);
                state.PreviousPoiId = entry.PoiId;
                TransitionScheduleContext(state, ScheduleContextPhase.ActiveAtPoi, asset.DescriptionForLLM);
                TryPlayAnimation(npc, asset.CsharpAnimation);
                return;
            }

            // 1. 同地图：如果当前已经在目标地图，直接在本地寻路走过去，绝不跨图
            if (string.Equals(loc.Name, asset.MapName, StringComparison.OrdinalIgnoreCase))
            {
                var safeTarget = MovementPathfinding.FindNearestWalkableTile(loc, target, npc, 3);
                if (Vector2.Distance(npc.Tile, safeTarget) < 1.5f)
                {
                    MarkEntryArrived(entry, Game1.timeOfDay);
                    state.PreviousPoiId = entry.PoiId;
                    TransitionScheduleContext(state, ScheduleContextPhase.ActiveAtPoi, asset.DescriptionForLLM);
                    TryPlayAnimation(npc, asset.CsharpAnimation);
                    return;
                }

                if (MovementPathfinding.TryCreatePath(npc, loc, safeTarget, out var controller, out _))
                {
                    npc.controller = controller;
                    npc.addedSpeed = 2;
                    StartFarmPoiWatch(npc, state, entry);
                }
                else
                {
                    MarkEntryArrived(entry, Game1.timeOfDay);
                    state.PreviousPoiId = entry.PoiId;
                    TransitionScheduleContext(state, ScheduleContextPhase.ActiveAtPoi, asset.DescriptionForLLM);
                    TryPlayAnimation(npc, asset.CsharpAnimation);
                }
                return;
            }

            // 2. 跨地图：寻找距离当前 NPC 最近且可通行的出口 Warp（拒绝盲选 FirstOrDefault）
            Warp nearestWarp = null;
            Vector2 bestExitTile = Vector2.Zero;
            float minDistance = float.MaxValue;

            if (loc.warps != null && loc.warps.Count > 0)
            {
                foreach (var w in loc.warps)
                {
                    if (w == null || string.IsNullOrWhiteSpace(w.TargetName)) continue;

                    var rawWarpTile = new Vector2(w.X, w.Y);
                    float dist = Vector2.Distance(npc.Tile, rawWarpTile);
                    if (dist < minDistance)
                    {
                        var walkable = MovementPathfinding.FindWalkableTileNearWarp(loc, rawWarpTile, npc);
                        if (walkable != Vector2.Zero)
                        {
                            minDistance = dist;
                            nearestWarp = w;
                            bestExitTile = walkable;
                        }
                    }
                }
            }

            // 室内如果没有显式 Warp（部分室内门靠 TouchAction 触发），兜底直接瞬移
            if (nearestWarp == null)
            {
                ModEntry.SMonitor?.Log(
                    $"[CSM] {npc.Name} has no valid exit on '{loc.Name}' — warping directly to '{entry.PoiId}'.",
                    LogLevel.Warn);
                MultiMapNavigator.WarpDirectTo(npc, asset.MapName, target);
                MarkEntryArrived(entry, Game1.timeOfDay);
                state.PreviousPoiId = entry.PoiId;
                TransitionScheduleContext(state, ScheduleContextPhase.ActiveAtPoi, asset.DescriptionForLLM);
                TryPlayAnimation(npc, asset.CsharpAnimation);
                return;
            }

            // 走到最近的出口，离场后直接瞬移至目的地
            MovementManager.Instance.MoveToTile(npc, bestExitTile,
                onComplete: () =>
                {
                    MultiMapNavigator.WarpDirectTo(npc, asset.MapName, target);
                    MarkEntryArrived(entry, Game1.timeOfDay);
                    state.PreviousPoiId = entry.PoiId;
                    TransitionScheduleContext(state, ScheduleContextPhase.ActiveAtPoi, asset.DescriptionForLLM);
                    TryPlayAnimation(npc, asset.CsharpAnimation);
                    ModEntry.SMonitor?.Log(
                        $"[CSM] {npc.Name} exited '{loc.Name}' via nearest warp → arrived at '{entry.PoiId}'.",
                        LogLevel.Info);
                },
                onFail: () =>
                {
                    ModEntry.SMonitor?.Log(
                        $"[CSM] {npc.Name} path to nearest exit on '{loc.Name}' failed — warping directly to '{entry.PoiId}'.",
                        LogLevel.Warn);
                    MultiMapNavigator.WarpDirectTo(npc, asset.MapName, target);
                    MarkEntryArrived(entry, Game1.timeOfDay);
                    state.PreviousPoiId = entry.PoiId;
                    TransitionScheduleContext(state, ScheduleContextPhase.ActiveAtPoi, asset.DescriptionForLLM);
                    TryPlayAnimation(npc, asset.CsharpAnimation);
                });
        }

        private static Vector2 FindFarmHouseExitTile(NPC npc)
        {
            var farmHouse = Game1.getLocationFromName("FarmHouse");
            if (farmHouse?.warps != null)
            {
                foreach (var warp in farmHouse.warps)
                {
                    if (warp != null &&
                        string.Equals(warp.TargetName, "Farm", StringComparison.OrdinalIgnoreCase))
                    {
                        var tile = new Vector2(warp.X, warp.Y);
                        return MovementPathfinding.FindWalkableTileNearWarp(farmHouse, tile, npc);
                    }
                }
            }

            var farm = Game1.getFarm();
            if (farm?.warps != null)
            {
                foreach (var warp in farm.warps) 
                {
                    if (warp != null &&
                        string.Equals(warp.TargetName, "FarmHouse", StringComparison.OrdinalIgnoreCase))
                    {
                        var tile = new Vector2(warp.TargetX, warp.TargetY);
                        if (farmHouse != null)
                            return MovementPathfinding.FindWalkableTileNearWarp(farmHouse, tile, npc);
                        return tile;
                    }
                }
            }

            ModEntry.SMonitor?.Log(
                "[CSM] WARNING: Could not find FarmHouse exit dynamically — using fallback (6, 14).",
                LogLevel.Warn);
            return new Vector2(6, 14);
        }
    }
}