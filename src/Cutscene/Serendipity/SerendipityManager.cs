using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using ValleytalkReborn.Cutscene.Generation;

namespace ValleytalkReborn.Cutscene.Serendipity
{
    /// <summary>
    /// 偶遇事件总控管理器：驱动偶遇雷达轮询、冷却管控、驻足按键交互、预约互斥与即兴开映
    /// </summary>
    public sealed class SerendipityManager
    {
        public static SerendipityManager Instance { get; } = new();

        private int _tickCounter = 0;
        private const int RadarIntervalTicks = 60; // 每 60 帧（1秒）做一次轻量雷达扫描

        private SerendipityManager() { }

        /// <summary>
        /// 每帧更新驱动（挂载于 SMAPI UpdateTicked）
        /// </summary>
        public void Update(UpdateTickedEventArgs e)
        {
            if (!Context.IsWorldReady || Game1.player?.currentLocation == null)
                return;

            // 1. 若浮标处于激活状态，每帧驱动倒计时与距离监控
            if (SerendipityBeacon.Instance.IsActive)
            {
                float dt = (float)Game1.currentGameTime.ElapsedGameTime.TotalSeconds;
                SerendipityBeacon.Instance.Update(dt);
            }

            // 2. 降低雷达扫描频率，每秒评估一次
            _tickCounter++;
            if (_tickCounter < RadarIntervalTicks)
                return;
            _tickCounter = 0;

            // 门禁检查：过场进行中、剧本构思中或总开关关闭时跳过
            if (VirtualDirector.Instance.IsActive || CutsceneGeneratorService.IsGenerating)
                return;

            if (ModEntry.Config?.EnableSerendipityCutscenes != true)
                return;

            // 玩家必须处于自由态（无打开的菜单、未在原版事件、未在垂钓或战斗锁定中）
            if (!Context.IsPlayerFree)
            {
                if (SerendipityBeacon.Instance.IsActive)
                    SerendipityBeacon.Instance.Dismiss();
                return;
            }

            // 每日触发上限门禁
            int maxDaily = ModEntry.Config?.SerendipityMaxDailyCount ?? 1;
            if (!SerendipityCooldownStore.Instance.CanTriggerToday(maxDaily))
            {
                if (SerendipityBeacon.Instance.IsActive)
                    SerendipityBeacon.Instance.Dismiss();
                return;
            }

            // 若浮标已在等待玩家交互中，保持等待，不发起新的扫描
            if (SerendipityBeacon.Instance.IsActive)
                return;

            // 3. 执行雷达扫描
            int totalDays = Game1.Date.TotalDays;
            if (SerendipityRadar.TryFindCandidate(
                Game1.player.currentLocation,
                Game1.player.Tile,
                totalDays,
                out var matchedActors,
                out var matchedSituation))
            {
                string mode = ModEntry.Config?.SerendipityTriggerMode ?? "Prompt";
                if (string.Equals(mode, "Auto", StringComparison.OrdinalIgnoreCase))
                {
                    ModEntry.SMonitor?.Log(
                        $"[SerendipityManager] Auto-triggering cutscene '{matchedSituation.Title}' with {matchedActors.Count} actors.",
                        LogLevel.Info);
                    TriggerEncounter(matchedActors, matchedSituation);
                }
                else
                {
                    ModEntry.SMonitor?.Log(
                        $"[SerendipityManager] Found serendipity candidate '{matchedSituation.Title}', displaying beacon.",
                        LogLevel.Debug);
                    SerendipityBeacon.Instance.Activate(matchedActors, matchedSituation);
                }
            }
        }

        /// <summary>
        /// 输入按键响应（挂载于 SMAPI ButtonsChanged）
        /// </summary>
        public void OnButtonsChanged(ButtonsChangedEventArgs e)
        {
            if (!SerendipityBeacon.Instance.IsActive || !SerendipityBeacon.Instance.IsPlayerInRange())
                return;

            if (!Context.IsPlayerFree)
                return;

            // 监听 E 键或原版交互操作键（支持手柄按键与自定义改键）
            bool actionPressed = e.Pressed.Contains(SButton.E) ||
                                 e.Pressed.Any(b => b.IsActionButton());

            if (actionPressed)
            {
                var actors = SerendipityBeacon.Instance.TargetActors;
                var situation = SerendipityBeacon.Instance.Situation;
                SerendipityBeacon.Instance.Dismiss();

                if (actors != null && actors.Count > 0 && situation != null)
                {
                    ModEntry.SMonitor?.Log(
                        $"[SerendipityManager] Player interacted with beacon, starting cutscene '{situation.Title}'.",
                        LogLevel.Info);
                    TriggerEncounter(actors, situation);
                }
            }
        }

        /// <summary>
        /// 执行偶遇剧情：预约 NPC、记录冷却并启动异步生成与放映
        /// </summary>
        public void TriggerEncounter(List<NPC> actors, SituationDefinition situation)
        {
            if (actors == null || actors.Count == 0 || situation == null)
                return;

            if (VirtualDirector.Instance.IsActive || CutsceneGeneratorService.IsGenerating)
            {
                ModEntry.SMonitor?.Log(
                    $"[SerendipityManager] Skipping encounter '{situation.Title}' — director active or generating.",
                    LogLevel.Debug);
                return;
            }

            ModEntry.SMonitor?.Log(
                $"[SerendipityManager] Triggering encounter cutscene '{situation.Title}' with {string.Join(", ", actors.Select(a => a.Name))}.",
                LogLevel.Info);

            // 1. 记录冷却与每日计数
            SerendipityCooldownStore.Instance.RecordTrigger(
                actors.Select(a => a.Name),
                Game1.Date.TotalDays,
                Game1.timeOfDay);

            // 2. 尝试为参演 NPC 办理预约（互斥屏蔽 A2A 与 AmbientBark 冒泡）
            try
            {
                var coordinator = ModEntry.Coordinator;
                if (coordinator != null)
                {
                    foreach (var actor in actors)
                    {
                        coordinator.Reservations?.TryReserve(actor.Name, "VirtualDirector");
                    }
                }
            }
            catch (Exception ex)
            {
                ModEntry.SMonitor?.Log($"[SerendipityManager] Reservation failed: {ex.Message}", LogLevel.Trace);
            }

            // 3. 异步启动大模型编剧与主线程开演
            _ = CutsceneGeneratorService.GenerateAndPlayAsync(actors, situation.IntentPrompt);
        }

        /// <summary>
        /// 绘制驻足观摩浮标（挂载于 SMAPI RenderedWorld）
        /// </summary>
        public void Draw(SpriteBatch b)
        {
            if (SerendipityBeacon.Instance.IsActive)
            {
                SerendipityBeacon.Instance.Draw(b);
            }
        }

        /// <summary>
        /// 换天时重置状态
        /// </summary>
        public void OnDayStarted()
        {
            SerendipityBeacon.Instance.Dismiss();
            SerendipityCooldownStore.Instance.ResetDay();
        }

        /// <summary>
        /// 回标题时全量清理
        /// </summary>
        public void OnReturnedToTitle()
        {
            SerendipityBeacon.Instance.Dismiss();
            SerendipityCooldownStore.Instance.Clear();
        }
    }
}
