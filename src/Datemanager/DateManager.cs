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
    ///   <item><term>Pending</term><description>已预约，NPC 提前到场等候中（原 DateWindowOpen=true, DateStarted=false）。</description></item>
    ///   <item><term>StagedActive</term><description>定点互动中（禁止随从跟随）。</description></item>
    ///   <item><term>WalkingActive</term><description>漫步伴游中（启用 FollowMovementTracker）。</description></item>
    ///   <item><term>Closing</term><description>告别对话播放中，等待玩家关闭对话框后清理（原 _farewellPending=true）。</description></item>
    /// </list>
    /// </summary>
    public enum DatePhase
    {
        None,
        Pending,        // 已预约，NPC 提前到场等候中
        StagedActive,   // 定点互动中（禁止随从跟随）
        WalkingActive,  // 漫步伴游中（启用 FollowMovementTracker）
        Closing         // 告别对话播放中
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

        /// <summary>VT-FOCUS-05: 会话建立形式，供 review 提示词区分散步约会与正式约会。Memory-only。</summary>
        internal DateManager.DateMode SessionMode { get; init; } = DateManager.DateMode.Scheduled;

        /// <summary>标记本次约会是否经历过定点后的漫步送归阶段（持久化在 Session 实例中，免疫 ResetDateState 异步擦除）。</summary>
        public bool HasWalkedAfterStaged { get; set; } = false;

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
            // VT-FOCUS-05: 散步约会（SessionMode == Follow）无"守时"语义，改用约会形式描述。
            bool isWalkDate = SessionMode == DateManager.DateMode.Follow;

            string latenessLine = isWalkDate
                ? "约会形式: 随性的同行散步约会（一路走走聊聊，没有固定流程）"
                : $"守时情况: {Lateness switch
                    {
                        LatenessLevel.TooEarly => "（玩家过早到达，早于 18:00）",
                        LatenessLevel.OnTime => "（玩家准时到达）",
                        LatenessLevel.SlightlyLate => "（玩家轻度迟到，19:00-21:00 之间到达）",
                        LatenessLevel.VeryLate => "（玩家严重迟到，21:00-22:00 之间到达）",
                        LatenessLevel.MissedWindow => "（玩家错过约会窗口，22:00 后才到达）",
                        _ => ""
                    }}";

            return $@"
=== 约会概要 ===
对象: {NpcName}
地点: {TargetLocation}
时间段: {StartTime} - {EndTime}
{latenessLine}

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

        // StagedActive 阶段对话计数（用于触发散步分支选择）
        private int _stagedDialogueCount = 0;

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

            MovementManager.Instance.OnFollowEndedCallback = npcName =>
            {
                if (Phase == DatePhase.WalkingActive && string.Equals(ActiveDateNpcName, npcName, StringComparison.OrdinalIgnoreCase))
                {
                    NPC npc = Game1.getCharacterFromName(npcName);
                    if (npc != null && TryTransitionPhase(DatePhase.WalkingActive, DatePhase.Closing))
                    {
                        TriggerFarewellDialogue(npc);
                    }
                }
            };
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
        /// 外部交互拦截调用接口：玩家右键点击处于等候状态的约会 NPC 时触发约会开场。
        /// 校验阶段 / 时间窗口 / 地点一致后，原地无黑屏开启 StagedActive 并播放问候。
        /// </summary>
        public bool TryStartDateOnInteraction(NPC npc)
        {
            if (!Context.IsWorldReady || npc == null) return false;
            if (Phase != DatePhase.Pending || CurrentDateMode != DateMode.Scheduled) return false;
            if (!string.Equals(npc.Name, ActiveDateNpcName, StringComparison.OrdinalIgnoreCase)) return false;

            // 检查时间窗口
            if (Game1.timeOfDay < EarliestDateTriggerTime || Game1.timeOfDay >= ScheduledDateTriggerCutoff)
                return false;

            // 检查地点是否一致
            string currentMap = Game1.player.currentLocation?.Name ?? "";
            if (!string.Equals(currentMap, ActiveDateLocation, StringComparison.OrdinalIgnoreCase))
                return false;

            int durationMinutes = DateRules.GetDateDurationMinutes(
                Game1.timeOfDay, OnTimeCutoff, SlightlyLateCutoff, ScheduledDurationMinutes);
            int rawEnd = Utility.ModifyTime(Game1.timeOfDay, durationMinutes);
            int scheduledEndTime = Math.Min(rawEnd, HardEndTime);

            StartScheduledDate(npc, scheduledEndTime);
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
            if (ActiveDateNpcName != npcName || !(Phase == DatePhase.StagedActive || Phase == DatePhase.WalkingActive)) return;

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
                                                && (Phase == DatePhase.StagedActive || Phase == DatePhase.WalkingActive)
                                                && ActiveDateNpcName == npcName
                                                && Game1.timeOfDay < DynamicEndTime;

        public void RecordDateDialogue(string speaker, string text)
        {
            if (CurrentDateMode is DateMode.Scheduled or DateMode.Follow && CurrentSession != null)
                CurrentSession.RecordDialogue(speaker, text);

            if (Phase == DatePhase.StagedActive)
            {
                _stagedDialogueCount++;
                if (_stagedDialogueCount >= 2)
                {
                    NPC npc = Game1.getCharacterFromName(ActiveDateNpcName);
                    if (npc != null)
                    {
                        // 延迟 800ms，待上一轮对话框平稳关闭后弹出选择分支
                        DelayedAction.functionAfterDelay(() =>
                        {
                            if (Phase == DatePhase.StagedActive && Game1.activeClickableMenu == null)
                                OfferWalkingTransition(npc);
                        }, 800);
                    }
                }
            }
        }

        public void RecordDateGift(string giftName, int taste)
        {
            if (CurrentDateMode is DateMode.Scheduled or DateMode.Follow && CurrentSession != null)
                CurrentSession.RecordGift(giftName, taste);
        }

        // ─────────────────────────────────────────────────────────────
        //  散步分支选择（StagedActive → WalkingActive / Closing）
        // ─────────────────────────────────────────────────────────────

        private void OfferWalkingTransition(NPC npc)
        {
            if (npc == null || !Context.IsWorldReady) return;
            if (Phase != DatePhase.StagedActive) return;

            bool isZh = LocalizedContentManager.CurrentLanguageCode == LocalizedContentManager.LanguageCode.zh;

            string inviteText = isZh
                ? "今晚坐在这里聊得很开心……时间还早，要一起散步走走吗？顺便送我回家？"
                : "I had a wonderful time sitting with you... It's still early, shall we take a walk together?";

            string option1 = isZh ? "好啊，我想再陪你走走。" : "Sure, I'd love to walk with you.";
            string option2 = isZh ? "今天有点晚了，早点休息吧。" : "It's getting late, let's call it a night.";

            var responses = new Response[]
            {
                new("AcceptWalking", option1),
                new("DeclineWalking", option2)
            };

            Game1.currentLocation.createQuestionDialogue(
                inviteText,
                responses,
                (who, whichAnswer) => HandleWalkingChoice(npc, whichAnswer),
                npc);
        }

        private void HandleWalkingChoice(NPC npc, string whichAnswer)
        {
            if (npc == null || string.IsNullOrEmpty(whichAnswer)) return;

            if (whichAnswer == "AcceptWalking")
            {
                if (!TryTransitionPhase(DatePhase.StagedActive, DatePhase.WalkingActive))
                {
                    TriggerFarewellDialogue(npc);
                    return;
                }

                // ─── 标记散步阶段，作为会话事实随 Session 传递给后台 Review 任务 ───
                if (CurrentSession != null)
                {
                    CurrentSession.HasWalkedAfterStaged = true;
                }

                npc.jump();
                npc.doEmote(32);
                MovementManager.Instance.StartDateFollow(npc, DynamicEndTime);
                ModEntry.SMonitor?.Log($"[DateManager] 玩家选择散步，切换至 WalkingActive，启动跟随。", LogLevel.Info);
            }
            else
            {
                if (TryTransitionPhase(DatePhase.StagedActive, DatePhase.Closing))
                {
                    TriggerFarewellDialogue(npc);
                }
            }
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
            if (!(Phase == DatePhase.StagedActive || Phase == DatePhase.WalkingActive) || CurrentDateMode != DateMode.Scheduled)
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
               && (Phase == DatePhase.StagedActive || Phase == DatePhase.WalkingActive)
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
            if (Phase == DatePhase.WalkingActive) return;

            Phase = DatePhase.WalkingActive;
            DynamicEndTime = endTime;

            // VT-FOCUS-04: 即时同行约会激活会话，使 walking 容器可回放时长/礼物事实。
            // ??= 保留 Scheduled→Follow 中途转换时已存在的预约段会话（保留其对话日志）。
            // VT-FOCUS-05: 标记会话建立形式为 Follow，供 review 提示词区分散步/正式约会。
            CurrentSession ??= new DateSessionData(npc.Name, ActiveDateLocation, Game1.timeOfDay)
            {
                SessionMode = DateManager.DateMode.Follow
            };

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

        private void StartScheduledDate(NPC npc, int endTime)
        {
            if (Phase != DatePhase.Pending) return;

            LatenessLevel lateness = DateRules.GetLatenessLevel(
                Game1.timeOfDay, EarliestDateTriggerTime, OnTimeCutoff, SlightlyLateCutoff, HardEndTime);

            if (lateness == LatenessLevel.TooEarly || lateness == LatenessLevel.MissedWindow)
            {
                Game1.showRedMessage(IsChineseLanguage ? "现在还不是赴约的时间。" : "It's not time for the date yet.");
                return;
            }

            if (!TryTransitionPhase(DatePhase.Pending, DatePhase.StagedActive))
                return;

            DynamicEndTime = endTime;
            CurrentSession = new DateSessionData(npc.Name, ActiveDateLocation, Game1.timeOfDay)
            {
                Lateness = lateness
            };

            npc.Halt();
            npc.controller = null;
            npc.temporaryController = null;
            npc.facePlayer(Game1.player);
            Game1.player.faceGeneralDirection(npc.getStandingPosition());

            int currentSessionVersion = StartNewDateGeneration();
            _ = FetchAndQueueGreetingAsync(npc.Name, ActiveDateLocation, lateness, endTime, currentSessionVersion);
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
                // 1. 现有逻辑：生成 Trait（夜间记忆巩固）
                var item = BuildDateWorkItem(session);

                ModEntry.SMonitor?.Log(
                    $"[DateManager] Submitting date review for {session.NpcName} to NightlyConsolidator.",
                    LogLevel.Debug);

                await NightlyConsolidator.RunAsync(new List<NightlyWorkItem> { item });

                // ─── 2. 新增：生成手账（Timeline Chronicle 归档）───
                await GenerateAndWriteDateChronicleAsync(session);
                // ────────────────────────────────────────────────────────
            }
            catch (Exception ex)
            {
                ModEntry.SMonitor?.Log(
                    $"[DateManager] Date review failed for {session.NpcName}: {ex.Message}",
                    LogLevel.Warn);
            }
        }

        /// <summary>
        /// 生成约会手账并安全写入 MemoryManager（Daily Timeline）。
        /// LLM 推理在 Task 线程池执行，ModData 写入封送回主线程队列。
        /// </summary>
        /// <param name="session">约会会话数据（包含 HasWalkedAfterStaged 状态）</param>
        /// <returns>异步任务</returns>
        private async Task GenerateAndWriteDateChronicleAsync(DateSessionData session)
        {
            if (session == null || string.IsNullOrWhiteSpace(session.NpcName))
                return;

            try
            {
                bool isZh = LocalizedContentManager.CurrentLanguageCode ==
                            LocalizedContentManager.LanguageCode.zh;

                // 1. 约会模式描述（基于 HasWalkedAfterStaged 状态）
                string modeDesc;
                if (session.SessionMode == DateMode.Follow)
                {
                    modeDesc = isZh ? "随性漫步" : "Casual Walk";
                }
                else if (session.HasWalkedAfterStaged)
                {
                    modeDesc = isZh ? "深情定点与漫步送归" : "Intimate Date & Walk Home";
                }
                else
                {
                    modeDesc = isZh ? "定点小聚" : "Quiet Gathering";
                }

                // 2. 礼物摘要提取
                string giftSummary = "";
                if (session.PlayerGaveGift && !string.IsNullOrWhiteSpace(session.GivenGiftName))
                {
                    string taste = session.GiftTaste switch
                    {
                        NPC.gift_taste_love => isZh ? "最爱的" : "favorite ",
                        NPC.gift_taste_like => isZh ? "喜欢的" : "nice ",
                        _ => ""
                    };
                    giftSummary = isZh
                        ? $"农夫送了我{taste}【{session.GivenGiftName}】"
                        : $"Farmer gave me {taste}[{session.GivenGiftName}]";
                }

                // 3. 对话片段摘录（最后 1-2 条，截短保护）
                string dialogueHighlights = "";
                if (session.DialogueLogs.Count > 0)
                {
                    var highlights = session.DialogueLogs
                        .TakeLast(2)
                        .Select(d => $"{d.Speaker}: {d.Text}");
                    dialogueHighlights = string.Join(" / ", highlights);
                    if (dialogueHighlights.Length > 80)
                        dialogueHighlights = dialogueHighlights.Substring(0, 80) + "...";
                }

                // 4. 调用 LLM（20 秒超时）
                var (sys, user) = DateFlowService.BuildChroniclePrompt(
                    session.NpcName,
                    session.TargetLocation,
                    session.StartTime,
                    session.EndTime,
                    modeDesc,
                    giftSummary,
                    dialogueHighlights);

                string chronicle = await DateFlowService.FetchLlmResponse(sys, user, 20000);

                // 5. 降级兜底：LLM 失败时使用模板化文本
                if (string.IsNullOrWhiteSpace(chronicle))
                {
                    chronicle = DateFlowService.BuildFallbackChronicle(
                        session.NpcName,
                        session.TargetLocation,
                        modeDesc,
                        session.PlayerGaveGift);

                    ModEntry.SMonitor?.Log(
                        $"[DateManager] LLM chronicle generation failed/timeout for {session.NpcName}. Using fallback template.",
                        LogLevel.Debug);
                }

                // 6. 主线程封送：写入 MemoryManager（三参数调用，默认参数自动生成日期标签）
                string chronicleSnapshot = chronicle;
                string npcNameSnapshot = session.NpcName;

                _mainThreadQueue.Enqueue(() =>
                {
                    // BOUNDARY: 世界未就绪（玩家已退出游戏）
                    if (!Context.IsWorldReady || Game1.player == null)
                    {
                        ModEntry.SMonitor?.Log(
                            $"[DateManager] Chronicle write skipped: world not ready (player offline).",
                            LogLevel.Debug);
                        return;
                    }

                    // ─── 规范调用：仅传 3 个参数，让 MemoryManager 内部自动生成日期标签与天数 ───
                    var result = MemoryManager.Instance.AddTimelineMemory(
                        npcNameSnapshot,
                        chronicleSnapshot,
                        MemoryTier.Daily);
                    // ────────────────────────────────────────────────────────────────────────────

                    if (result == MemoryOperationResult.Success)
                    {
                        // HUD 提示
                        string notif = isZh
                            ? $"今晚与 {npcNameSnapshot} 的约会已被记录在手账中。"
                            : $"Tonight's date with {npcNameSnapshot} has been recorded in your chronicle.";

                        Game1.addHUDMessage(new HUDMessage(notif, 2));

                        ModEntry.SMonitor?.Log(
                            $"[DateManager] Chronicle written for {npcNameSnapshot}: {chronicleSnapshot.Substring(0, Math.Min(50, chronicleSnapshot.Length))}...",
                            LogLevel.Info);
                    }
                    else
                    {
                        ModEntry.SMonitor?.Log(
                            $"[DateManager] MemoryManager.AddTimelineMemory failed: {result}",
                            LogLevel.Warn);
                    }
                });
            }
            catch (Exception ex)
            {
                ModEntry.SMonitor?.Log(
                    $"[DateManager] Chronicle generation pipeline exception for {session.NpcName}: {ex.Message}",
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
            // VT-FOCUS-05b: 散步约会无"守时"语义（Lateness 恒默认值），模式标注在此接入活跃 review。
            if (session.SessionMode == DateManager.DateMode.Follow)
                item.Events.Add("这是一次随性的同行散步约会（一路走走聊聊，没有固定流程）。");
            else if (!string.IsNullOrEmpty(latenessNote))
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
        //  NPC 提前到场等候调度
        // ─────────────────────────────────────────────────────────────

        /// <summary>
        /// 将约会 NPC 调度至约定地点的 WaitTile 等候。
        /// 同地图优先寻路，跨地图或寻路失败时降级安全 Warp。
        /// </summary>
        private bool TryScheduleNpcWaiting(NPC npc, DateLocationInfo location)
        {
            if (npc == null || location == null) return false;

            // 1. 地图加载检测
            var targetLocation = Game1.getLocationFromName(location.TargetMap);
            if (targetLocation == null)
            {
                ModEntry.SMonitor?.Log(
                    $"[DateManager] 目标地图 '{location.TargetMap}' 尚未加载，等待重试。",
                    LogLevel.Trace);
                return false;
            }

            // 2. WaitTile 兜底校验
            if (location.WaitTile == Vector2.Zero)
            {
                ModEntry.SMonitor?.Log(
                    $"[DateManager] 地点 '{location.LocationId}' 未配置有效 WaitTile，NPC 保持原地就位。",
                    LogLevel.Warn);
                npc.followSchedule = false;
                npc.Halt();
                return true;
            }

            // 3. 到场判定：同地图且距离 <= 1.5 格
            if (npc.currentLocation == targetLocation)
            {
                if (Vector2.Distance(npc.Tile, location.WaitTile) <= 1.5f)
                {
                    npc.faceDirection(location.DefaultFacingDirection);
                    npc.Halt();
                    npc.followSchedule = false;
                    return true;
                }

                // 同地图尝试 PathFindController 寻路
                var targetPoint = new xTile.Dimensions.Location((int)location.WaitTile.X, (int)location.WaitTile.Y);
                var pathController = new PathFindController(
                    npc,
                    targetLocation,
                    new Point(targetPoint.X, targetPoint.Y),
                    location.DefaultFacingDirection);

                if (pathController.pathToEndPoint != null && pathController.pathToEndPoint.Count > 0)
                {
                    npc.controller = pathController;
                    npc.followSchedule = false;
                    ModEntry.SMonitor?.Log($"[DateManager] {npc.Name} 开始在同地图内走向 WaitTile ({location.WaitTile.X}, {location.WaitTile.Y})。", LogLevel.Debug);
                    return true;
                }
            }

            // 4. 跨地图或寻路失败：规范使用 Game1.warpCharacter 瞬移就位
            ModEntry.SMonitor?.Log($"[DateManager] 跨地图或寻路无路径，将 {npc.Name} 安全传送至 {location.TargetMap} ({location.WaitTile.X}, {location.WaitTile.Y}) 等候。", LogLevel.Debug);
            Game1.warpCharacter(npc, location.TargetMap, location.WaitTile);
            npc.faceDirection(location.DefaultFacingDirection);
            npc.Halt();
            npc.controller = null;
            npc.followSchedule = false;
            return true;
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

        }

        private void OnTimeChanged(object sender, TimeChangedEventArgs e)
        {
            // 允许清理残留约会状态，但阻止新约会推进
            if (!ModEntry.Config.EnableDateSystem && Phase == DatePhase.None) return;

            if (Phase == DatePhase.None) return;

            // ── Pending 阶段（等待玩家到达）──────────────────────────
            if (Phase == DatePhase.Pending && CurrentDateMode == DateMode.Scheduled)
            {
                // 1. 17:30 - 18:00 提前调度入场
                if (e.NewTime >= 1730 && e.NewTime < EarliestDateTriggerTime)
                {
                    var npc = Game1.getCharacterFromName(ActiveDateNpcName);
                    if (npc != null && DateLocationRegistry.Locations.TryGetValue(ActiveDateLocation, out var locInfo))
                    {
                        TryScheduleNpcWaiting(npc, locInfo);
                    }
                }

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
            if (Phase != DatePhase.StagedActive && Phase != DatePhase.WalkingActive) return;
            if (e.NewTime < DynamicEndTime && e.NewTime < HardEndTime) return;

            if (CurrentDateMode == DateMode.Follow)
            {
                ModEntry.SMonitor?.Log($"[DateManager] Follow mode ended at {e.NewTime}.", LogLevel.Info);

                // VT-FOCUS-05: 自然结束同样进入会话复盘（与 EndDateGracefully / TriggerFarewellDialogue 对称）。
                // 无告别对话——静默收场，仅提交夜间巩固。会话按引用传入，ResetDateState 不影响 review。
                if (CurrentSession != null)
                {
                    CurrentSession.EndTime = e.NewTime;
                    _ = ReviewDateSessionAsync(CurrentSession);
                }

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
                else if ((Phase == DatePhase.StagedActive || Phase == DatePhase.WalkingActive) && CurrentSession != null)
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
        //  状态转换卫兵
        // ─────────────────────────────────────────────────────────────

        /// <summary>
        /// 验证状态转换的合法性，拦截非法跳跃。
        /// 失败时记录 Error 日志并返回 false。
        /// </summary>
        private bool TryTransitionPhase(DatePhase from, DatePhase to)
        {
            if (Phase != from)
            {
                ModEntry.SMonitor?.Log(
                    $"[DateManager] Phase transition rejected: expected {from}, actual {Phase}. " +
                    $"Attempted transition to {to}.",
                    LogLevel.Error);
                return false;
            }

            if (!IsValidTransition(from, to))
            {
                ModEntry.SMonitor?.Log(
                    $"[DateManager] Invalid phase transition: {from} → {to}.",
                    LogLevel.Error);
                return false;
            }

            ModEntry.SMonitor?.Log(
                $"[DateManager] Phase transition: {from} → {to}.",
                LogLevel.Debug);

            Phase = to;
            return true;
        }

        /// <summary>
        /// 定义合法的状态转换路径（包含随性伴游直接进入 WalkingActive 通道与紧急重置）。
        /// </summary>
        private static bool IsValidTransition(DatePhase from, DatePhase to)
        {
            return (from, to) switch
            {
                (DatePhase.None, DatePhase.Pending) => true,
                (DatePhase.Pending, DatePhase.StagedActive) => true,
                (DatePhase.Pending, DatePhase.WalkingActive) => true, // 随性散步约会通道（TryStartFollow）
                (DatePhase.StagedActive, DatePhase.WalkingActive) => true,
                (DatePhase.StagedActive, DatePhase.Closing) => true,
                (DatePhase.WalkingActive, DatePhase.Closing) => true,
                (_, DatePhase.None) => true, // 紧急重置通道（ResetDateState/Abort）
                _ => false
            };
        }

        // ─────────────────────────────────────────────────────────────
        //  状态重置
        // ─────────────────────────────────────────────────────────────

        private void ResetDateState()
        {
            // ★ 在清空字段前快照当前约会 NPC 姓名，供末尾日程恢复保护使用。
            string npcToRestoreSchedule = ActiveDateNpcName;

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
            _stagedDialogueCount = 0;

            // 清空待处理邀约队列
            _pendingInvites.Clear();

            // ★ 生命周期回收：清空所有路人的目击冷却记录
            _npcLastWitnessTime.Clear();

            // 清空队列中残留的回调，防止跨天或跨存档执行
            while (_mainThreadQueue.TryDequeue(out _))
            {
            }

            // 末尾：对当前待约 NPC 的日程进行恢复保护（仅当日程已被禁用时恢复）
            if (!string.IsNullOrEmpty(npcToRestoreSchedule))
            {
                var npc = Game1.getCharacterFromName(npcToRestoreSchedule);
                if (npc != null && !npc.followSchedule)
                {
                    MovementManager.Instance.TryRestoreSchedule(npc);
                }
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