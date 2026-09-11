using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using ValleytalkReborn.Movement;

namespace ValleytalkReborn
{
    public enum MovementType
    {
        None,
        Forward,
        Backward,
        Left,
        Right,
        Up,
        Down,
        Follow,
        StopFollow
    }

    /// <summary>
    /// Thin shell preserving public API. All logic lives in MovementCoordinator (MMR-05b).
    /// Event subscriptions remain here (SMAPI lifecycle); handlers forward to Coordinator.
    /// </summary>
    public class MovementManager
    {
        public static readonly MovementManager Instance = new();

        private readonly MovementCoordinator _coordinator = new();

        // ─── Callback forwarding (DialogueCoordinator sets these) ───

        internal Action<NPC> OnFollowStartedCallback
        {
            get => _coordinator.FollowTracker.OnFollowStartedCallback;
            set => _coordinator.FollowTracker.OnFollowStartedCallback = value;
        }

        internal Action<string> OnFollowEndedCallback
        {
            get => _coordinator.FollowTracker.OnFollowEndedCallback;
            set => _coordinator.FollowTracker.OnFollowEndedCallback = value;
        }

        // ─── Public query API ───

        public bool HasActiveDateFollow => _coordinator.FollowTracker.HasActiveDateFollow;
        public bool HasActiveFollow     => _coordinator.FollowTracker.HasActiveFollow;
        public NPC  CurrentFollowingNpc => _coordinator.FollowTracker.CurrentFollowingNpc;
        public NPC  CurrentGotoNpc      => _coordinator.GotoTracker.ActiveCount > 0 ? _coordinator.GotoTracker.GetAnyActiveNpc() : null;
        public bool IsStepActive        => _coordinator.StepTracker.ActiveCount > 0;
        public NPC  CurrentStepNpc      => _coordinator.StepTracker.GetAnyActiveNpc();
        public bool IsMoving            => _coordinator.GotoTracker.ActiveCount > 0;

        public bool IsFollowing(NPC npc) => _coordinator.FollowTracker.IsFollowing(npc);

        // ─── Lifecycle (SMAPI events stay in shell) ───

        public void Initialize(IModHelper helper)
        {
            helper.Events.GameLoop.UpdateTicked += OnUpdateTicked;
            helper.Events.GameLoop.DayStarted   += OnDayStarted;
            helper.Events.GameLoop.DayEnding    += OnDayEnding;
            helper.Events.GameLoop.SaveLoaded   += OnSaveLoaded;
        }

        public void Cleanup(IModHelper helper)
        {
            helper.Events.GameLoop.UpdateTicked -= OnUpdateTicked;
            helper.Events.GameLoop.DayStarted   -= OnDayStarted;
            helper.Events.GameLoop.DayEnding    -= OnDayEnding;
            helper.Events.GameLoop.SaveLoaded   -= OnSaveLoaded;
        }

        private void OnUpdateTicked(object sender, UpdateTickedEventArgs e) => _coordinator.OnUpdateTicked(e);
        private void OnDayStarted(object sender, DayStartedEventArgs e)     => _coordinator.OnDayStarted();
        private void OnSaveLoaded(object sender, SaveLoadedEventArgs e)     => _coordinator.OnSaveLoaded();

        /// <summary>
        /// 玩家昏睡倒地（凌晨 2:00 送回家）时的兜底清理。
        /// </summary>
        private void OnDayEnding(object sender, DayEndingEventArgs e) => _coordinator.OnDayEnding();

        // ─── Public action API ───

        /// <summary>
        /// Forwards to ScheduleRestorer.TryRestoreSchedule (preserved for external callers like DateManager).
        /// </summary>
        public void TryRestoreSchedule(NPC npc) => _coordinator.ScheduleRestorer.TryRestoreSchedule(npc);

        public bool IsNpcMoving(NPC npc) => _coordinator.IsNpcMoving(npc);

        public void QueueMovement(NPC npc, string actionType, bool skipDialogueWait = false)
            => _coordinator.QueueMovement(npc, actionType, skipDialogueWait);

        public void QueueMovement(NPC npc, ActionTag tag, bool skipDialogueWait = false)
        {
            if (npc == null || tag == ActionTag.None)
                return;

            // StopFollow has cross-system concern (DateManager.EndDateGracefully)
            if (tag == ActionTag.StopFollow)
            {
                StopFollow(npc);
                return;
            }

            _coordinator.QueueMovement(npc, tag, skipDialogueWait);
        }

        public void QueueEmote(NPC npc, int emoteId)
            => _coordinator.QueueEmote(npc, emoteId);

        public void StartDateFollow(NPC npc, int endTime)
            => _coordinator.StartDateFollow(npc, endTime);

        public void StopDateFollow(NPC npc)
            => _coordinator.StopDateFollow(npc);

        /// <summary>
        /// 玩家主动取消跟随。约会跟随额外通知 DateManager。
        /// </summary>
        public void StopFollow(NPC npc)
        {
            if (_coordinator.StopFollow(npc))
                DateManager.Instance.EndDateGracefully(npc.Name, "Player_Stopped_Follow");
        }

        /// <summary>兼容旧调用：失败时也会调用 onComplete。</summary>
        public void MoveToTile(NPC npc, Vector2 targetTile, Action onComplete = null)
            => _coordinator.MoveToTile(npc, targetTile, onComplete);

        /// <summary>成功/失败分开回调。</summary>
        public void MoveToTile(NPC npc, Vector2 targetTile, Action onComplete, Action onFail)
            => _coordinator.MoveToTile(npc, targetTile, onComplete, onFail);

        /// <summary>取消指定 NPC 当前的 MoveToTile / step / pending 动作。</summary>
        public void CancelMoveToTile(NPC npc, bool invokeFailCallback = false)
            => _coordinator.CancelMoveToTile(npc, invokeFailCallback);

        /// <summary>
        /// 由 CSM 直接调用，启动普通跟随（非约会跟随），截止指定时间。
        /// </summary>
        public void StartRegularFollow(NPC npc, int endTime)
            => _coordinator.StartRegularFollow(npc, endTime);
    }
}
