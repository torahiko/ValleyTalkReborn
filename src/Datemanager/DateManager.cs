using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Menus;
using StardewValley.Pathfinding;

namespace ValleytalkReborn
{
    // ══════════════════════════════════════════════════════════════
    //  约会阶段枚举（替换原来的 DateWindowOpen / DateStarted / DateConsumed 三个 bool）
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// 描述一次约会的生命周期阶段。
    /// <list type="bullet">
    ///   <item><term>None</term><description>无进行中约会。</description></item>
    ///   <item><term>Pending</term><description>已预约，等待玩家到达约会地点（原 DateWindowOpen=true, DateStarted=false）。</description></item>
    ///   <item><term>Active</term><description>约会进行中（原 DateStarted=true, DateConsumed=true）。</description></item>
    ///   <item><term>Closing</term><description>告别对话播放中，等待玩家关闭对话框后清理（原 _farewellPending=true）。</description></item>
    /// </list>
    /// </summary>
    public enum DatePhase
    {
        None,
        Pending,
        Active,
        Closing,
    }

    // ══════════════════════════════════════════════════════════════
    //  1-b. 待处理邀约类型（T7′ 前置数据结构）
    // ══════════════════════════════════════════════════════════════

    public enum DateInviteChannel
    {
        Verbal,
        Mail
    }

    public sealed record DatePendingInvite
    {
        public string NpcName     { get; init; } = "";
        public string LocationId  { get; init; } = "";
        public DateInviteChannel Channel { get; init; } = DateInviteChannel.Verbal;
    }

    // ══════════════════════════════════════════════════════════════
    //  2. 约会会话专属上下文收集容器
    // ══════════════════════════════════════════════════════════════
    public sealed record DialogueRecord(string Speaker, string Text, int GameTime);

    public sealed record GiftRecord(string ItemName, int Taste);

    public sealed record ActionRecord(string Description);

    public class DateSessionData
    {
        public string NpcName { get; }
        public string TargetLocation { get; }
        public int StartTime { get; }
        public int EndTime { get; set; }

        public LatenessLevel Lateness { get; set; } = LatenessLevel.OnTime;

        public List<DialogueRecord> DialogueLogs { get; } = new();
        public List<GiftRecord> GiftLogs { get; } = new();
        public List<ActionRecord> ActionLogs { get; } = new();

        public bool PlayerGaveGift { get; set; } = false;
        public string GivenGiftName { get; set; } = "";
        public int GiftTaste { get; set; } = -1;

        public DateSessionData(string npcName, string location, int startTime)
        {
            NpcName = npcName;
            TargetLocation = location;
            StartTime = startTime;
        }

        public void RecordDialogue(string speaker, string text)
            => DialogueLogs.Add(new DialogueRecord(speaker, text, Game1.timeOfDay));

        public void RecordGift(string giftName, int taste)
        {
            PlayerGaveGift = true;
            GivenGiftName = giftName;
            GiftTaste = taste;
            GiftLogs.Add(new GiftRecord(giftName, taste));
        }

        public void RecordAction(string description)
            => ActionLogs.Add(new ActionRecord(description));

        public string ToReviewPromptContext()
        {
            string latenessNote = Lateness switch
            {
                LatenessLevel.TooEarly => "（玩家过早到达，早于 18:00）",
                LatenessLevel.OnTime => "（玩家准时到达）",
                LatenessLevel.SlightlyLate => "（玩家轻度迟到，19:00-21:00 之间到达）",
                LatenessLevel.VeryLate => "（玩家严重迟到，21:00-22:00 之间到达）",
                LatenessLevel.MissedWindow => "（玩家错过约会窗口，22:00 后才到达）",
                _ => ""
            };

            return $@"
=== 约会概要 ===
对象: {NpcName}
地点: {TargetLocation}
时间段: {StartTime} - {EndTime}
守时情况: {latenessNote}

=== 互动对话流水 ===
{(DialogueLogs.Count > 0 ? string.Join("\n", DialogueLogs.Select(d => $"[{d.GameTime / 100:D2}:{d.GameTime % 100:D2}] [{d.Speaker}]: {d.Text}")) : "（两人安静相伴散步，未进行长篇交流）")}

=== 礼物与互动行为 ===
{(GiftLogs.Count > 0 ? string.Join("\n", GiftLogs.Select(g => $"玩家赠送了礼物【{g.ItemName}】(喜好评级: {g.Taste})")) : "（未赠送额外礼物）")}
{(ActionLogs.Count > 0 ? string.Join("\n", ActionLogs.Select(a => a.Description)) : "")}
".Trim();
        }
    }

    public enum LatenessLevel
    {
        TooEarly,    // < 18:00 到达（约会尚未可触发）
        OnTime,      // 18:00 ~ 19:00 到达
        SlightlyLate,// 19:00 ~ 21:00 到达
        VeryLate,    // 21:00 ~ 22:00 到达
        MissedWindow // >= 22:00 到达（错过约会窗口）
    }

    // ══════════════════════════════════════════════════════════════
    //  2b. 约会会话只读摘要（供聚焦路由消费）
    // ══════════════════════════════════════════════════════════════

    /// <summary>
    /// Memory-only 只读快照，仅主线程调用（与 Prompts 组装同一线程约束）。
    /// 描述一次约会会话的对话 / 礼物摘要。IsValid=false 表示会话不存在或 NPC 不匹配。
    /// </summary>
    public sealed record DateSessionDigest(
        bool IsValid,
        int StartGameTime,
        int DialogueExchangeCount,
        string? GivenGiftName,
        int GivenGiftTaste);

    // ══════════════════════════════════════════════════════════════
    //  3. 约会系统总状态机（DateManager）
    // ══════════════════════════════════════════════════════════════
    internal class DateManager : IDateStateProvider
    {
        // ─── Singleton ───────────────────────────────────────────────
        public static readonly DateManager Instance = new();

        // ─── 枚举 ────────────────────────────────────────────────────
        public enum DateMode
        {
            None,
            Scheduled,
            Follow
        }

        public enum DateOrigin
        {
            NpcInitiated,
            PlayerInitiated
        }

        // ─── 工具 ────────────────────────────────────────────────────
        private static bool IsChineseLanguage =>
            LocalizedContentManager.CurrentLanguageCode == LocalizedContentManager.LanguageCode.zh;

        // ─── 只读派生集合（供外部查询）───────────────────────────────
        public static Dictionary<string, string> LocationDisplayNames
        {
            get
            {
                if (DateLocationRegistry.Locations == null)
                    return new Dictionary<string, string>();

                return DateLocationRegistry.Locations.ToDictionary(
                    k => k.Key,
                    v => $"{v.Value.DisplayNameZh} / {v.Value.DisplayNameEn}",
                    StringComparer.OrdinalIgnoreCase);
            }
        }

        public static HashSet<string> WhitelistedLocations
        {
            get
            {
                if (DateLocationRegistry.Locations == null)
                    return new HashSet<string>();

                return new HashSet<string>(
                    DateLocationRegistry.Locations.Keys,
                    StringComparer.OrdinalIgnoreCase);
            }
        }

        // ─── 时间常量 ─────────────────────────────────────────────────
        private const int EarliestDateTriggerTime = 1800;
        private const int ScheduledDateTriggerCutoff = 2130;
        private const int OnTimeCutoff = 1900;
        private const int SlightlyLateCutoff = 2100;
        private const int HardEndTime = 2200;
        private const int FollowDurationMinutes = 120;
        private const int ScheduledDurationMinutes = 180;

        // 路人目击约会的冷却（游戏内分钟）
        private const int TownieWitnessCooldownGameMinutes = 60;

        // ─── 核心状态 ─────────────────────────────────────────────────

        /// <summary>当前约会阶段。替代原来的 DateWindowOpen / DateStarted / DateConsumed。</summary>
        public DatePhase Phase { get; private set; } = DatePhase.None;

        public string ActiveDateNpcName { get; private set; } = "";
        public string ActiveDateLocation { get; private set; } = "";
        public DateMode CurrentDateMode { get; private set; } = DateMode.None;
        public DateOrigin CurrentDateOrigin { get; private set; } = DateOrigin.NpcInitiated;
        public int DynamicEndTime { get; private set; } = HardEndTime;
        public DateSessionData CurrentSession { get; private set; }

        /// <summary>
        /// Memory-only 只读快照，仅主线程调用（与 Prompts 组装同一线程约束）。
        /// 返回指定 NPC 当前会话摘要；会话不存在 / 名字不匹配 / 异常时返回 IsValid=false 的默认 record。
        /// </summary>
        public DateSessionDigest BuildSessionDigest(string npcName)
        {
            const int Invalid = -1;

            try
            {
                if (string.IsNullOrWhiteSpace(npcName))
                    return new DateSessionDigest(false, 0, 0, null, Invalid);

                if (CurrentSession == null
                    || !string.Equals(CurrentSession.NpcName, npcName, StringComparison.OrdinalIgnoreCase))
                {
                    return new DateSessionDigest(false, 0, 0, null, Invalid);
                }

                var logs = CurrentSession.GiftLogs;
                string? giftName = null;
                int giftTaste = Invalid;

                if (logs != null && logs.Count > 0)
                {
                    var last = logs[logs.Count - 1];
                    giftName = last.ItemName;
                    giftTaste = last.Taste;
                }

                return new DateSessionDigest(
                    IsValid: true,
                    StartGameTime: CurrentSession.StartTime,
                    DialogueExchangeCount: CurrentSession.DialogueLogs.Count,
                    GivenGiftName: giftName,
                    GivenGiftTaste: giftTaste);
            }
            catch (Exception ex)
            {
                ModEntry.SMonitor?.Log($"[DateManager] BuildSessionDigest failed: {ex.Message}", LogLevel.Warn);
                return new DateSessionDigest(false, 0, 0, null, Invalid);
            }
        }

        // 外部系统仍需要的状态（保持 public，供 NpcReceiveGiftPatch 等使用）
        public bool SpouseMorningInvitePending { get; set; } = false;
        public bool HasGivenDateGiftThisSession { get; set; } = false;

        // ─── 待处理邀约队列（T7′ 前置状态）──────────────────────
        private readonly ConcurrentDictionary<string, DatePendingInvite> _pendingInvites = new(StringComparer.OrdinalIgnoreCase);

        // ─── 私有状态 ─────────────────────────────────────────────────

        // 路人 NPC 上次目击约会的游戏时间（用于每 NPC 独立的 60 分钟冷却）
        private readonly Dictionary<string, int> _npcLastWitnessTime =
            new(StringComparer.OrdinalIgnoreCase);

        // 约会会话版本号，用于拦截过期的异步 LLM 回调
        private int _dateSessionVersion = 0;

        // 主线程任务队列：异步线程将回调 Enqueue 到这里，UpdateTicked 统一 Drain

        private readonly ConcurrentQueue<Action> _mainThreadQueue = new();

        // 告别对话关闭轮询
        private string _farewellCloseNpcName = null;

        // ─── 构造 / 初始化 / 清理 ────────────────────────────────────

        private DateManager()
        {
        }

        /// <summary>
        /// 由 ModEntry.Entry() 显式调用，替代原来在构造函数中注册事件的反模式。
        /// </summary>
        public void Initialize(IModHelper helper)
        {
            helper.Events.GameLoop.TimeChanged += OnTimeChanged;
            helper.Events.GameLoop.UpdateTicked += OnUpdateTicked;
            helper.Events.GameLoop.DayStarted += OnDayStarted;
            helper.Events.GameLoop.DayEnding += OnDayEnding;
            helper.Events.GameLoop.SaveLoaded += OnSaveLoaded;
            helper.Events.GameLoop.ReturnedToTitle += OnReturnedToTitle;
            helper.Events.Player.Warped += OnPlayerWarped;
            helper.Events.Display.MenuChanged += OnMenuChanged;
        }

        public void Cleanup(IModHelper helper)
        {
            helper.Events.GameLoop.TimeChanged -= OnTimeChanged;
            helper.Events.GameLoop.UpdateTicked -= OnUpdateTicked;
            helper.Events.GameLoop.DayStarted -= OnDayStarted;
            helper.Events.GameLoop.DayEnding -= OnDayEnding;
            helper.Events.GameLoop.SaveLoaded -= OnSaveLoaded;
            helper.Events.GameLoop.ReturnedToTitle -= OnReturnedToTitle;
            helper.Events.Player.Warped -= OnPlayerWarped;
            helper.Events.Display.MenuChanged -= OnMenuChanged;

            if (!string.IsNullOrEmpty(ActiveDateNpcName))
            {
                ReleaseNpc(ActiveDateNpcName);
                ResetDateState();
            }
        }

        /// <summary>
        /// 当约会系统在设置中被关闭时静默重置约会状态，保留 SMAPI 事件注册以供再次开启。
        /// 绝不调用 Cleanup()，否则 SMAPI 事件会被注销，再次开启时系统变成僵尸。
        /// </summary>
        public void AbortActiveDateSilently()
        {
            if (!string.IsNullOrEmpty(ActiveDateNpcName))
            {
                ReleaseNpc(ActiveDateNpcName);
            }
            ResetDateState();
            ModEntry.SMonitor?.Log("[DateManager] AbortActiveDateSilently: date state reset (events preserved).", LogLevel.Info);
        }

        // ─────────────────────────────────────────────────────────────
        //  对外 API
        // ─────────────────────────────────────────────────────────────

        public bool TryScheduleDate(
            NPC npc,
            string locationId,
            DateOrigin origin = DateOrigin.NpcInitiated)
        {
            if (!ModEntry.Config.EnableDateSystem)
            {
                ModEntry.SMonitor?.Log("[DateManager] Schedule date rejected: Date system is disabled.", LogLevel.Debug);
                return false;
            }

            var world = CaptureWorldSnapshot();
            if (!DateRules.CanScheduleDate(world, locationId, ActiveDateNpcName, npc.Name,
                    WhitelistedLocations, ScheduledDateTriggerCutoff))
            {
                ModEntry.SMonitor?.Log(
                    $"[DateManager] Date rejected by rules: festival={world.IsFestivalDay}, " +
                    $"time={world.TimeOfDay}, loc={locationId}, busy={ActiveDateNpcName}.",
                    LogLevel.Debug);

                bool isZh = IsChineseLanguage;
                string reason;

                if (world.IsFestivalDay)
                    reason = isZh ? "今天是节日，无法安排约会。" : "Cannot schedule date during festivals.";
                else if (world.TimeOfDay >= ScheduledDateTriggerCutoff)
                    reason = isZh ? "太晚了，无法安排今晚的约会。" : "Too late to schedule a date tonight.";
                else if (!string.IsNullOrEmpty(ActiveDateNpcName) && ActiveDateNpcName != npc.Name)
                    reason = isZh ? $"你今晚已经和 {ActiveDateNpcName} 有约了。" : $"You already have a date with {ActiveDateNpcName} tonight.";
                else
                    reason = isZh ? "当前无法安排约会。" : "Cannot schedule date right now.";

                Game1.showRedMessage(reason);
                return false;
            }

            // 使用真实地图名（TargetMap）而非逻辑 ID，确保寻路与约会触发能正确比对 GameLocation.Name
            if (!DateLocationRegistry.Locations.TryGetValue(locationId, out var locationInfo))
            {
                ModEntry.SMonitor?.Log($"[DateManager] Invalid location ID: {locationId}", LogLevel.Error);
                Game1.showRedMessage(IsChineseLanguage ? "当前无法安排约会。" : "Cannot schedule date right now.");
                return false;
            }

            ActiveDateNpcName = npc.Name;
            ActiveDateLocation = locationInfo.TargetMap;
            CurrentDateMode = DateMode.Scheduled;
            CurrentDateOrigin = origin;
            Phase = DatePhase.Pending;
            DynamicEndTime = HardEndTime;

            ModEntry.SMonitor?.Log(
                $"[DateManager] Scheduled date confirmed: {npc.Name} @ {locationId} (origin={origin}).",
                LogLevel.Info);

            CompanionScheduleManager.Instance?.ClearScheduleForOverride("DateScheduled", npc.Name);
            return true;
        }

        // ─── 待处理邀约 API（T7′ 前置接口）──────────────────────

        /// <summary>窥视指定 NPC 的待处理邀约（不消费）。</summary>
        public bool TryPeekPendingInvite(string npcName, out DatePendingInvite inv)
        {
            inv = null;
            if (string.IsNullOrWhiteSpace(npcName)) return false;
            return _pendingInvites.TryGetValue(npcName, out inv);
        }

        /// <summary>原子读出并清除指定 NPC 的待处理邀约。</summary>
        public bool TryTakePendingInvite(string npcName, out DatePendingInvite inv)
        {
            inv = null;
            if (string.IsNullOrWhiteSpace(npcName)) return false;
            return _pendingInvites.TryRemove(npcName, out inv);
        }

        /// <summary>回滚写回一条邀约（用于消费失败时恢复）。</summary>
        public void TrySetPendingInvite(DatePendingInvite inv)
        {
            if (inv == null || string.IsNullOrWhiteSpace(inv.NpcName)) return;
            _pendingInvites[inv.NpcName] = inv;
        }

        public bool TryStartFollow(NPC npc)
        {
            if (!ModEntry.Config.EnableDateSystem)
            {
                ModEntry.SMonitor?.Log("[DateManager] Start follow rejected: Date system is disabled.", LogLevel.Debug);
                return false;
            }

            var world = CaptureWorldSnapshot();
            if (!DateRules.CanStartFollow(world, ActiveDateNpcName, npc.Name, HardEndTime))
            {
                ModEntry.SMonitor?.Log(
                    $"[DateManager] Follow rejected by rules: festival={world.IsFestivalDay}, " +
                    $"time={world.TimeOfDay}, busy={ActiveDateNpcName}.",
                    LogLevel.Debug);
                return false;
            }

            // 🔥 修复 3：Follow 开始时，使之前可能残留的 Scheduled LLM 任务失效
            InvalidateDateSession();

            int rawEndTime = Utility.ModifyTime(Game1.timeOfDay, FollowDurationMinutes);
            int followEndTime = Math.Min(rawEndTime, HardEndTime);

            ActiveDateNpcName = npc.Name;
            ActiveDateLocation = Game1.player.currentLocation?.Name ?? "";
            CurrentDateMode = DateMode.Follow;
            Phase = DatePhase.Pending;
            DynamicEndTime = followEndTime;

            ModEntry.SMonitor?.Log(
                $"[DateManager] Follow started: {npc.Name}, endTime={followEndTime}.",
                LogLevel.Info);

            StartDateFollowImmediate(npc, followEndTime);
            return true;
        }

        /// <summary>
        /// IDateStateProvider 接口实现。
        /// Phase >= Active 时视为"已消费"，外部调用通常不需要这个方法了，
        /// 但保留以维持接口兼容。
        /// </summary>
        [Obsolete("状态机已改用 DatePhase，此方法已废弃。")]
        public void ConsumeDate(string npcName)
        {
        }

        public void EndDateGracefully(string npcName, string reason = "Player_Requested")
        {
            if (ActiveDateNpcName != npcName || Phase != DatePhase.Active) return;

            ModEntry.SMonitor?.Log(
                $"[DateManager] Date ended gracefully: {npcName} (reason: {reason}).",
                LogLevel.Info);

            if (CurrentSession != null)
            {
                CurrentSession.EndTime = Game1.timeOfDay;
                _ = ReviewDateSessionAsync(CurrentSession);
            }

            ReleaseNpc(npcName);
            ResetDateState();
        }

        public bool IsOnDate(string npcName) => ModEntry.Config.EnableDateSystem
                                                && Phase == DatePhase.Active
                                                && ActiveDateNpcName == npcName
                                                && Game1.timeOfDay < DynamicEndTime;

        public void RecordDateDialogue(string speaker, string text)
        {
            if (CurrentDateMode == DateMode.Scheduled && CurrentSession != null)
                CurrentSession.RecordDialogue(speaker, text);
        }

        public void RecordDateGift(string giftName, int taste)
        {
            if (CurrentDateMode == DateMode.Scheduled && CurrentSession != null)
                CurrentSession.RecordGift(giftName, taste);
        }

        // ══════════════════════════════════════════════════════════════
        //  路人目击约会接口（供 BarkFocusRouter 调用）
        // ══════════════════════════════════════════════════════════════

        /// <summary>
        /// 检查该路人 NPC 是否可以在 Bark 中目击并关注当下的约会。
        /// 条件：约会 Active 阶段、该 NPC 不是约会对象本人、距上次目击已过去 ≥60 游戏分钟。
        /// </summary>
        public bool CanNpcWitnessDate(string bystanderName)
        {
            if (Phase != DatePhase.Active || CurrentDateMode != DateMode.Scheduled)
                return false;

            if (string.IsNullOrEmpty(ActiveDateNpcName) || string.IsNullOrEmpty(bystanderName))
                return false;

            // 约会主角本人不属于"路人目击"
            if (string.Equals(bystanderName, ActiveDateNpcName, StringComparison.OrdinalIgnoreCase))
                return false;

            if (_npcLastWitnessTime.TryGetValue(bystanderName, out int lastWitnessTime))
            {
                int elapsedMinutes = GameMinutesBetween(lastWitnessTime, Game1.timeOfDay);
                // 时间差未达 60 分钟（且无跨天异常回流）→ 冷却中
                if (elapsedMinutes >= 0 && elapsedMinutes < TownieWitnessCooldownGameMinutes)
                    return false;
            }

            return true;
        }

        /// <summary>
        /// 计算两个星露谷游戏时间之间相差的分钟数。
        /// 游戏时间格式为 HHMM（如 1800、1920），分钟部分为 0/10/20/30/40/50。
        /// </summary>
        private static int GameMinutesBetween(int earlierTime, int laterTime)
        {
            if (laterTime < earlierTime) return -1; // 跨天回流，视为无效

            int earlierHours = earlierTime / 100;
            int earlierMinutes = earlierTime % 100;
            int laterHours = laterTime / 100;
            int laterMinutes = laterTime % 100;

            int earlierTotal = earlierHours * 60 + earlierMinutes;
            int laterTotal = laterHours * 60 + laterMinutes;

            return laterTotal - earlierTotal;
        }

        /// <summary>
        /// 记录该路人 NPC 已在当前游戏时间目击了约会（进入 60 游戏分钟冷却）。
        /// 由 BarkFocusRouter 在成功注入焦点后调用。
        /// </summary>
        public void RecordNpcWitnessDate(string bystanderName)
        {
            if (string.IsNullOrWhiteSpace(bystanderName)) return;

            _npcLastWitnessTime[bystanderName] = Game1.timeOfDay;

            ModEntry.SMonitor?.Log(
                $"[DateManager] 路人 [{bystanderName}] 目击约会，记录时间 {Game1.timeOfDay}（60 分钟冷却生效）",
                LogLevel.Debug);
        }

        public bool CanTriggerTwoStageCallout()
            => CurrentDateMode == DateMode.Scheduled
               && Phase == DatePhase.Active
               && Game1.activeClickableMenu == null
               && !Game1.player.UsingTool
               && !Game1.player.isRidingHorse()
               && !Game1.eventUp
               && !Game1.isFestival();

        // ─────────────────────────────────────────────────────────────
        //  内部流程：开场
        // ─────────────────────────────────────────────────────────────

        private void StartDateFollowImmediate(NPC npc, int endTime)
        {
            if (Phase == DatePhase.Active) return;

            Phase = DatePhase.Active;
            DynamicEndTime = endTime;

            npc.controller = null;
            npc.temporaryController = null;
            npc.doingEndOfRouteAnimation.Value = false;
            npc.Schedule?.Clear();
            npc.CurrentDialogue.Clear();

            Vector2 spawnTile = FindSafeTileNearPlayer(npc);
            Game1.warpCharacter(npc, Game1.player.currentLocation, spawnTile);

            MovementManager.Instance.StartDateFollow(npc, endTime);
            ModEntry.SMonitor?.Log($"[DateManager] Immediate follow active for {npc.Name}.", LogLevel.Info);
        }

        private void StartScheduledDateWithFade(NPC npc, int endTime)
        {
            if (Phase == DatePhase.Active) return;

            LatenessLevel lateness = DateRules.GetLatenessLevel(
                Game1.timeOfDay, EarliestDateTriggerTime, OnTimeCutoff, SlightlyLateCutoff, HardEndTime);

            // 🔧 拦截异常到达时间：TooEarly（<18:00）/ MissedWindow（>=22:00）均不应开启约会，
            // 防止时间异常引发约会死锁。弹出红字提示并重置状态。
            if (lateness == LatenessLevel.TooEarly || lateness == LatenessLevel.MissedWindow)
            {
                ModEntry.SMonitor?.Log($"[DateManager] StartScheduledDateWithFade 被拦截：异常到达时间 {Game1.timeOfDay}（{lateness}）。", LogLevel.Warn);
                Game1.showRedMessage(IsChineseLanguage
                    ? "现在还不是赴约的时间。"
                    : "It's not time for the date yet.");
                ResetDateState();
                return;
            }

            Phase = DatePhase.Active;
            DynamicEndTime = endTime;

            CurrentSession = new DateSessionData(npc.Name, ActiveDateLocation, Game1.timeOfDay)
            {
                Lateness = lateness
            };

            string npcNameSnapshot = npc.Name;
            string locationSnapshot = ActiveDateLocation;
            int endTimeSnapshot = endTime;

            int currentSessionVersion = StartNewDateGeneration();
            Game1.globalFadeToBlack(() =>
            {
                npc.controller = null;
                npc.temporaryController = null;
                npc.doingEndOfRouteAnimation.Value = false;
                npc.Schedule?.Clear();
                npc.CurrentDialogue.Clear();

                Vector2 spawnTile = FindSafeTileNearPlayer(npc);
                Game1.warpCharacter(npc, Game1.player.currentLocation, spawnTile);
                npc.facePlayer(Game1.player);
                Game1.player.faceGeneralDirection(npc.getStandingPosition());

                _ = FetchAndQueueGreetingAsync(npcNameSnapshot, locationSnapshot, lateness, endTimeSnapshot, currentSessionVersion);
            });
        }

        /// <summary>独立的异步问候语获取方法。</summary>
        private async Task FetchAndQueueGreetingAsync(string npcName, string location, LatenessLevel lateness, int endTime, int sessionVersion)
        {
            var (sys, user) = DateFlowService.BuildGreetingPrompt(npcName, location, lateness);
            string greeting = await DateFlowService.FetchLlmResponse(sys, user, 2500);
            if (string.IsNullOrWhiteSpace(greeting))
                greeting = DateFlowService.BuildDefaultGreeting(lateness);

            _mainThreadQueue.Enqueue(() =>
            {
                if (sessionVersion != _dateSessionVersion || ActiveDateNpcName != npcName)
                {
                    ModEntry.SMonitor?.Log("[DateManager] 丢弃过期的 LLM 问候语 (Session Mismatch).", LogLevel.Debug);
                    return;
                }
                ShowGreeting(npcName, greeting, endTime);
            });
        }

        private void ShowGreeting(string npcName, string greeting, int endTime)
        {
            if (!Context.IsWorldReady) return;

            Game1.globalFadeToClear(() =>
            {
                NPC targetNpc = Game1.getCharacterFromName(npcName);
                if (targetNpc == null) return;

                targetNpc.doEmote(32);
                MovementManager.Instance.StartDateFollow(targetNpc, endTime);
                targetNpc.CurrentDialogue.Clear();
                targetNpc.CurrentDialogue.Push(new Dialogue(targetNpc, null, greeting));
                Game1.drawDialogue(targetNpc);
                RecordDateDialogue(targetNpc.Name, greeting);
            });
        }

        // ─────────────────────────────────────────────────────────────
        //  内部流程：结束
        // ─────────────────────────────────────────────────────────────

        private void TriggerFarewellDialogue(NPC npc)
        {
            // Phase 已在调用前由调用方设为 Closing，防止重复触发
            if (CurrentSession != null)
            {
                CurrentSession.EndTime = Game1.timeOfDay;
                _ = ReviewDateSessionAsync(CurrentSession);
            }

            MovementManager.Instance.StopDateFollow(npc);
            npc.Halt();
            npc.controller = null;
            npc.facePlayer(Game1.player);

            string npcNameSnapshot = npc.Name;
            string locationSnapshot = ActiveDateLocation;
            int currentSessionVersion = _dateSessionVersion;

            _ = FetchAndQueueFarewellAsync(npcNameSnapshot, locationSnapshot, currentSessionVersion);
        }

        private void ShowFarewellDialogue(string npcName, string line)
        {
            // 检查世界状态，以及约会是否还处于 Closing 阶段
            if (!Context.IsWorldReady || Phase != DatePhase.Closing || Game1.timeOfDay >= 2300)
            {
                ReleaseNpc(npcName);
                ResetDateState();
                return;
            }

            NPC npc = Game1.getCharacterFromName(npcName);
            if (npc == null)
            {
                ReleaseNpc(npcName);
                ResetDateState();
                return;
            }

            npc.CurrentDialogue.Clear();
            npc.CurrentDialogue.Push(new Dialogue(npc, null, line));
            Game1.drawDialogue(npc);

            // 记录正在等待关闭的 NPC，OnMenuChanged 事件会处理释放
            _farewellCloseNpcName = npcName;
        }

        /// <summary>独立的异步告别语获取方法。</summary>
        private async Task FetchAndQueueFarewellAsync(string npcName, string location, int sessionVersion)
        {
            var (sys, user) = DateFlowService.BuildFarewellPrompt(npcName, location);
            string farewellLine = await DateFlowService.FetchLlmResponse(sys, user, 10000);
            string line = string.IsNullOrWhiteSpace(farewellLine)
                ? DateFlowService.BuildDefaultFarewell()
                : farewellLine;

            _mainThreadQueue.Enqueue(() =>
            {
                if (sessionVersion != _dateSessionVersion || ActiveDateNpcName != npcName)
                {
                    ModEntry.SMonitor?.Log("[DateManager] 丢弃过期的 LLM 告别语 (Session Mismatch).", LogLevel.Debug);
                    return;
                }
                ShowFarewellDialogue(npcName, line);
            });
        }

        /// <summary>
        /// 事件驱动替代 PollFarewellClose 轮询：
        /// 当告别对话框被玩家关闭时，释放 NPC 并重置约会状态。
        /// </summary>
        private void OnMenuChanged(object sender, MenuChangedEventArgs e)
        {
            // 只关心告别对话的关闭
            if (_farewellCloseNpcName == null) return;

            // 旧菜单是 DialogueBox 且新菜单为 null → 对话框被真正关闭
            if (e.OldMenu is DialogueBox && e.NewMenu == null)
            {
                var name = _farewellCloseNpcName;
                _farewellCloseNpcName = null;

                ModEntry.SMonitor?.Log(
                    $"[DateManager] Farewell dialogue closed for {name}. Releasing NPC.",
                    LogLevel.Info);

                ReleaseNpc(name);
                ResetDateState();
            }
        }

        private async Task ReviewDateSessionAsync(DateSessionData session)
        {
            if (session == null) return;

            try
            {
                var item = BuildDateWorkItem(session);

                ModEntry.SMonitor?.Log(
                    $"[DateManager] Submitting date review for {session.NpcName} to NightlyConsolidator.",
                    LogLevel.Debug);

                // 复用夜间巩固管线：LLM复盘 → Trait写入 → 阅后即焚晨间话题
                await NightlyConsolidator.RunAsync(new List<NightlyWorkItem> { item });
            }
            catch (Exception ex)
            {
                ModEntry.SMonitor?.Log(
                    $"[DateManager] Date review failed for {session.NpcName}: {ex.Message}",
                    LogLevel.Warn);
            }
        }

        /// <summary>
        /// 将约会会话数据转换为 NightlyWorkItem，复用夜间记忆巩固管线。
        /// </summary>
        private static NightlyWorkItem BuildDateWorkItem(DateSessionData session)
        {
            var item = new NightlyWorkItem { NpcName = session.NpcName };

            // ── Events：约会基本事实 ──
            string latenessNote = session.Lateness switch
            {
                LatenessLevel.TooEarly      => "玩家过早到达约会地点（早于 18:00）。",
                LatenessLevel.OnTime         => "玩家准时到达约会地点。",
                LatenessLevel.SlightlyLate   => "玩家迟到（19:00-21:00 之间才到）。",
                LatenessLevel.VeryLate       => "玩家严重迟到（21:00 后才到）。",
                LatenessLevel.MissedWindow   => "玩家错过约会窗口（22:00 后才到）。",
                _                            => ""
            };
            if (!string.IsNullOrEmpty(latenessNote))
                item.Events.Add(latenessNote);

            // 约会时长（游戏内时间区间）
            item.Events.Add($"今天与农夫进行了一次约会，地点：{session.TargetLocation}，时间段：{session.StartTime}～{session.EndTime}。");

            // 行为记录
            foreach (var action in session.ActionLogs)
                if (!string.IsNullOrWhiteSpace(action.Description))
                    item.Events.Add(action.Description);

            // ── DialogueTurns：对话 + 礼物 ──
            foreach (var log in session.DialogueLogs)
                item.DialogueTurns.Add($"[{log.Speaker}]: {log.Text}");

            foreach (var gift in session.GiftLogs)
            {
                string tasteName = gift.Taste switch
                {
                    NPC.gift_taste_love    => "最爱",
                    NPC.gift_taste_like    => "喜欢",
                    NPC.gift_taste_dislike => "不喜欢",
                    NPC.gift_taste_hate    => "讨厌",
                    _                      => "普通"
                };
                item.DialogueTurns.Add($"农夫赠送了礼物【{gift.ItemName}】（{tasteName}）。");
            }

            // ── RelationshipContext：关系状态 ──
            var player = Game1.player;
            if (player != null)
            {
                int hearts = 0;
                if (player.friendshipData?.TryGetValue(session.NpcName, out var friendship) == true
                    && friendship != null)
                {
                    hearts = friendship.Points / NPC.friendshipPointsPerHeartLevel;
                    bool isSpouse = friendship.IsMarried() || friendship.IsRoommate();
                    item.RelationshipContext.Add(isSpouse
                        ? $"农夫与 {session.NpcName} 已婚，好感度 {hearts} 颗心。"
                        : $"农夫与 {session.NpcName} 好感度 {hearts} 颗心。");
                }
            }

            return item;
        }

        // ─────────────────────────────────────────────────────────────
        //  NPC 释放（清理约会状态，回家委托给 MovementManager）
        // ─────────────────────────────────────────────────────────────

        private void ReleaseNpc(string npcName)
        {
            NPC dateNpc = Game1.getCharacterFromName(npcName);
            if (dateNpc == null) return;

            // 清理约会专属 NPC 状态
            MovementManager.Instance.StopDateFollow(dateNpc);
            dateNpc.Halt();
            dateNpc.movementPause = 0;
            dateNpc.addedSpeed = 0;
            dateNpc.doingEndOfRouteAnimation.Value = false;
            dateNpc.controller = null;
            dateNpc.temporaryController = null;

            // 委托 MovementManager 负责回家 + 恢复日程
            MovementManager.Instance.TryRestoreSchedule(dateNpc);
        }

        // ─────────────────────────────────────────────────────────────
        //  辅助工具
        // ─────────────────────────────────────────────────────────────

        private static Vector2 FindSafeTileNearPlayer(NPC npc)
        {
            var location = Game1.player.currentLocation;
            var origin = Game1.player.Tile;

            Vector2[] candidates =
            {
                new(origin.X + 1, origin.Y),
                new(origin.X - 1, origin.Y),
                new(origin.X, origin.Y - 1),
                new(origin.X, origin.Y + 1),
            };

            foreach (var tile in candidates)
            {
                if (location.isTilePassable(
                        new xTile.Dimensions.Location((int)tile.X, (int)tile.Y),
                        Game1.viewport))
                    return tile;
            }

            return origin;
        }

        // ─────────────────────────────────────────────────────────────
        //  事件监听
        // ─────────────────────────────────────────────────────────────

        private void OnUpdateTicked(object sender, UpdateTickedEventArgs e)
        {
            // ① 统一 Drain 主线程队列（异步 LLM 结果在这里落地）
            while (_mainThreadQueue.TryDequeue(out var action))
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    ModEntry.SMonitor?.Log(
                        $"[DateManager] MainThread action error: {ex.Message}",
                        LogLevel.Warn);
                }
            }

            if (!ModEntry.Config.EnableDateSystem) return;

            // ② 约会触发检测（Pending 阶段，每 30 tick 轮询一次）
            if (Phase != DatePhase.Pending
                || CurrentDateMode != DateMode.Scheduled
                || string.IsNullOrEmpty(ActiveDateNpcName))
                return;

            // ★ 防抢占：Pending 阶段且已进入赴约窗口时，锁定原版日程，
            // 防止原版 schedule 引擎在 18:00/19:00 等节点把 NPC 拉走。
            if (Game1.timeOfDay >= EarliestDateTriggerTime)
            {
                NPC pendingNpc = Game1.getCharacterFromName(ActiveDateNpcName);
                if (pendingNpc != null)
                {
                    pendingNpc.followSchedule = false;
                    pendingNpc.controller = null;
                }
            }

            if (!e.IsMultipleOf(30)) return;

            var world = CaptureWorldSnapshot();
            if (!DateRules.ShouldTriggerDate(world, ActiveDateLocation,
                    EarliestDateTriggerTime, ScheduledDateTriggerCutoff))
                return;

            NPC npc = Game1.getCharacterFromName(ActiveDateNpcName);
            if (npc == null) return;

            int durationMinutes = DateRules.GetDateDurationMinutes(
                Game1.timeOfDay, OnTimeCutoff, SlightlyLateCutoff, ScheduledDurationMinutes);

            int rawEnd = Utility.ModifyTime(Game1.timeOfDay, durationMinutes);
            int scheduledEndTime = Math.Min(rawEnd, HardEndTime);
            StartScheduledDateWithFade(npc, scheduledEndTime);
        }

        private void OnTimeChanged(object sender, TimeChangedEventArgs e)
        {
            // 允许清理残留约会状态，但阻止新约会推进
            if (!ModEntry.Config.EnableDateSystem && Phase == DatePhase.None) return;

            if (Phase == DatePhase.None) return;

            // ── Pending 阶段（等待玩家到达）──────────────────────────
            if (Phase == DatePhase.Pending && CurrentDateMode == DateMode.Scheduled)
            {
                if (e.NewTime == SlightlyLateCutoff)
                {
                    string npcDisplayName =
                        Game1.getCharacterFromName(ActiveDateNpcName)?.displayName ?? ActiveDateNpcName;
                    bool isZh = IsChineseLanguage;
                    Game1.showGlobalMessage(isZh
                        ? $"{npcDisplayName} 已经在等你很久了……"
                        : $"{npcDisplayName} has been waiting for you for a long time...");
                    ModEntry.SMonitor?.Log(
                        $"[DateManager] Player has not arrived by {SlightlyLateCutoff}. NPC is getting impatient.",
                        LogLevel.Info);
                }

                if (e.NewTime >= ScheduledDateTriggerCutoff)
                {
                    ModEntry.SMonitor?.Log(
                        $"[DateManager] Player never arrived. Recording stood-up at {e.NewTime}.",
                        LogLevel.Info);
                    if (CurrentDateOrigin == DateOrigin.PlayerInitiated)
                        StoodUpTracker.Instance.RecordStoodUp(ActiveDateNpcName);
                    else
                        ModEntry.SMonitor?.Log(
                            "[DateManager] NPC-initiated date missed — no stood-up penalty.",
                            LogLevel.Info);

                    ReleaseNpc(ActiveDateNpcName);
                    ResetDateState();
                }

                return;
            }

            // ── Active 阶段：检查是否到结束时间 ──────────────────────
            if (Phase != DatePhase.Active) return;
            if (e.NewTime < DynamicEndTime && e.NewTime < HardEndTime) return;

            if (CurrentDateMode == DateMode.Follow)
            {
                ModEntry.SMonitor?.Log($"[DateManager] Follow mode ended at {e.NewTime}.", LogLevel.Info);
                ReleaseNpc(ActiveDateNpcName);
                ResetDateState();
                return;
            }

            // Scheduled 约会时间到，触发告别
            NPC dateNpc = Game1.getCharacterFromName(ActiveDateNpcName);
            if (dateNpc != null)
            {
                Phase = DatePhase.Closing; // 先切换，防止 TriggerFarewellDialogue 重入
                TriggerFarewellDialogue(dateNpc);
            }
            else
            {
                ReleaseNpc(ActiveDateNpcName);
                ResetDateState();
            }
        }

        private void OnPlayerWarped(object sender, WarpedEventArgs e)
        {
            if (Phase == DatePhase.None || string.IsNullOrEmpty(ActiveDateNpcName)) return;
            if (!DateRules.IsIllegalDateLocation(e.NewLocation.Name)) return;

            NPC partner = Game1.getCharacterFromName(ActiveDateNpcName);

            if (CurrentDateMode == DateMode.Follow)
            {
                ModEntry.SMonitor?.Log(
                    $"[DateManager] Entered hazardous location during follow. Releasing {ActiveDateNpcName}.",
                    LogLevel.Info);
                ReleaseNpc(ActiveDateNpcName);
                ResetDateState();
            }
            else if (CurrentDateMode == DateMode.Scheduled)
            {
                ModEntry.SMonitor?.Log(
                    $"[DateManager] Player abandoned date by going to illegal area!",
                    LogLevel.Warn);

                StoodUpTracker.Instance.RecordStoodUp(ActiveDateNpcName);
                bool isZh = IsChineseLanguage;
                Game1.showRedMessage(isZh
                    ? $"{ActiveDateNpcName} 发现你独自前往危险区域，伤心地回家了……"
                    : $"{ActiveDateNpcName} saw you leave alone and went home, heartbroken...");

                if (partner != null)
                {
                    // 直接 warp 回家，不走告别流程
                    Game1.warpCharacter(
                        partner,
                        partner.DefaultMap,
                        new Vector2(partner.DefaultPosition.X / 64f, partner.DefaultPosition.Y / 64f));
                }

                ReleaseNpc(ActiveDateNpcName);
                ResetDateState();
            }
        }

        private void OnDayEnding(object sender, DayEndingEventArgs e)
        {
            if (!string.IsNullOrEmpty(ActiveDateNpcName) && CurrentDateMode == DateMode.Scheduled)
            {
                if (Phase == DatePhase.Pending && CurrentDateOrigin == DateOrigin.PlayerInitiated)
                {
                    // 玩家主动约但没去赴约，直接睡觉算放鸽子
                    StoodUpTracker.Instance.RecordStoodUp(ActiveDateNpcName);
                }
                else if (Phase == DatePhase.Active && CurrentSession != null)
                {
                    // 约会中途去睡觉，补一次复盘结算
                    CurrentSession.EndTime = Game1.timeOfDay;
                    _ = ReviewDateSessionAsync(CurrentSession);
                    ModEntry.SMonitor?.Log(
                        $"[DateManager] Player slept during active date with {ActiveDateNpcName}. Settling session.",
                        LogLevel.Info);
                }
            }

            ReleaseNpc(ActiveDateNpcName);
            ResetDateState();
        }

        private void OnDayStarted(object sender, DayStartedEventArgs e)
        {
            ResetDateState();
        }

        private void OnSaveLoaded(object sender, SaveLoadedEventArgs e)
        {
            ResetDateState();
            StoodUpTracker.Instance.Load();
        }

        /// <summary>
        /// 玩家按 Esc 退回标题界面时，彻底清空当前约会状态，防止带入下一个存档。
        /// AbortActiveDateSilently 内部已 ReleaseNpc + ResetDateState。
        /// </summary>
        private void OnReturnedToTitle(object sender, ReturnedToTitleEventArgs e)
        {
            AbortActiveDateSilently();
        }

        // ─────────────────────────────────────────────────────────────
        //  状态重置
        // ─────────────────────────────────────────────────────────────

        private void ResetDateState()
        {
            InvalidateDateSession();
            Phase = DatePhase.None;
            ActiveDateNpcName = "";
            ActiveDateLocation = "";
            CurrentDateMode = DateMode.None;
            CurrentDateOrigin = DateOrigin.NpcInitiated;
            DynamicEndTime = HardEndTime;
            SpouseMorningInvitePending = false;
            HasGivenDateGiftThisSession = false;
            CurrentSession = null;
            _farewellCloseNpcName = null;

            // 清空待处理邀约队列
            _pendingInvites.Clear();

            // ★ 生命周期回收：清空所有路人的目击冷却记录
            _npcLastWitnessTime.Clear();

            // 清空队列中残留的回调，防止跨天或跨存档执行
            while (_mainThreadQueue.TryDequeue(out _))
            {
            }
        }

        /// <summary>从 Game1 捕获当前世界状态快照，供 DateRules 使用。</summary>
        private static DateWorldSnapshot CaptureWorldSnapshot() => new(
            TimeOfDay: Game1.timeOfDay,
            PlayerLocationName: Game1.player?.currentLocation?.Name ?? "",
            IsFestivalDay: Utility.isFestivalDay(Game1.dayOfMonth, Game1.season),
            IsWorldReady: Context.IsWorldReady
        );

        /// <summary>开启新一代约会会话，返回新的世代号。</summary>
        private int StartNewDateGeneration()
        {
            unchecked { return ++_dateSessionVersion; }
        }

        /// <summary>使当前会话失效（用于中断旧的异步 LLM 任务）。</summary>
        private void InvalidateDateSession()
        {
            unchecked { _dateSessionVersion++; }
        }
    }
}