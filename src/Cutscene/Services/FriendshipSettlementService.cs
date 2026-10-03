using System;
using System.Collections.Generic;
using StardewModdingAPI;
using StardewValley;
using ValleytalkReborn.Social;

namespace ValleytalkReborn.Cutscene.Services
{
    /// <summary>
    /// 好感度结算与防刷服务：
    /// 1. 场景绝对分权：F9 创作者/沙盒模式零增减，仅大世界自然偶遇结算；
    /// 2. 严格每日硬顶防刷：同一 NPC 每天最多获得 +20 点（相当于打一次招呼），最多扣除 -20 点；
    /// 3. 左下角原版浮动提示：呈现“ALEX更喜欢你了。”或“ALEX有点难过。”反馈。
    /// </summary>
    public sealed class FriendshipSettlementService
    {
        public static FriendshipSettlementService Instance { get; } = new();

        public const int MaxDailyGainPerNpc = 20;
        public const int MaxDailyLossPerNpc = -20;

        private readonly Dictionary<string, int> _dailyFriendshipChanges = new(StringComparer.OrdinalIgnoreCase);

        private FriendshipSettlementService() { }

        /// <summary>
        /// 结算选项好感度并浮动提示
        /// </summary>
        public void ApplyFriendshipDelta(string npcName, int rawDelta, bool isSerendipity, string feedbackText = null)
        {
            string displayName = npcName;
            if (!string.IsNullOrWhiteSpace(npcName))
            {
                try
                {
                    var npc = Game1.getCharacterFromName(npcName);
                    if (npc != null && !string.IsNullOrWhiteSpace(npc.displayName))
                    {
                        displayName = npc.displayName;
                    }
                }
                catch
                {
                    // 游戏世界未就绪或处于测试环境时安全回退至 npcName
                }
            }

            // 1. 若配置关闭好感度变动，仅呈现文字反馈，不修改数值
            if (ModEntry.Config?.EnableCutsceneFriendshipChange == false)
            {
                ModEntry.SMonitor?.Log(
                    $"[FriendshipSettlement] Friendship change disabled in config for '{npcName}'.",
                    LogLevel.Trace);
                ShowHudFeedback(displayName, rawDelta, feedbackText);
                return;
            }

            // 2. F9 创作者/沙盒模式保护：一律不碰存档实际数值，防止无限手写刷分
            if (!isSerendipity)
            {
                ModEntry.SMonitor?.Log(
                    $"[FriendshipSettlement] Sandbox mode (F9/Workshop): skipping actual friendship point modification for '{npcName}' (raw delta: {rawDelta}).",
                    LogLevel.Debug);
                ShowHudFeedback(displayName, rawDelta, feedbackText);
                return;
            }

            // 3. 自然偶遇模式：应用每日硬顶限制防刷
            if (!string.IsNullOrWhiteSpace(npcName) && rawDelta != 0)
            {
                int allowedDelta = CalculateAllowedDelta(npcName, rawDelta);
                _dailyFriendshipChanges.TryGetValue(npcName, out int currentToday);

                if (allowedDelta != 0)
                {
                    _dailyFriendshipChanges[npcName] = currentToday + allowedDelta;

                    if (Game1.player?.friendshipData != null)
                    {
                        try
                        {
                            if (!Game1.player.friendshipData.TryGetValue(npcName, out var friendship))
                            {
                                friendship = new Friendship();
                                Game1.player.friendshipData[npcName] = friendship;
                            }
                            friendship.Points = Math.Clamp(friendship.Points + allowedDelta, 0, 2500);

                            ModEntry.SMonitor?.Log(
                                $"[FriendshipSettlement] Applied {allowedDelta} friendship points to {npcName} (Total today: {_dailyFriendshipChanges[npcName]}, raw: {rawDelta}).",
                                LogLevel.Info);
                        }
                        catch (Exception ex)
                        {
                            ModEntry.SMonitor?.Log(
                                $"[FriendshipSettlement] Error updating player friendship data: {ex.Message}",
                                LogLevel.Trace);
                        }
                    }
                }
                else
                {
                    ModEntry.SMonitor?.Log(
                        $"[FriendshipSettlement] Daily cap reached for {npcName} (Current today: {currentToday}), skipping delta {rawDelta}.",
                        LogLevel.Debug);
                }
            }

            // 3.5 VT-SOCIAL-04：即时刺激分流 — 自然偶遇（非 F9 沙盒）同步演化高维 SocialProfile，
            // 原版 points 的每日 ±20 硬顶保持不变。
            if (isSerendipity
                && !string.IsNullOrWhiteSpace(npcName)
                && rawDelta != 0
                && Context.IsWorldReady
                && Game1.player != null)
            {
                try
                {
                    SocialProfile profile = SocialGraphService.Instance.GetProfile(Game1.player, npcName);
                    if (rawDelta > 0)
                    {
                        profile.Affection = Math.Clamp(profile.Affection + Math.Max(1, rawDelta / 5), 0, 100);
                        profile.Trust = Math.Clamp(profile.Trust + (rawDelta >= 20 ? 2 : 1), 0, 100);
                        profile.UnresolvedFriction = Math.Clamp(profile.UnresolvedFriction - (rawDelta >= 20 ? 15 : 5), 0, 100);
                    }
                    else
                    {
                        profile.Affection = Math.Clamp(profile.Affection + rawDelta / 4, 0, 100);
                        profile.UnresolvedFriction = Math.Clamp(profile.UnresolvedFriction + Math.Abs(rawDelta) / 2, 0, 100);
                    }
                    SocialGraphService.Instance.SaveProfile(Game1.player, npcName, profile);
                }
                catch (Exception ex)
                {
                    // [RECOVERABLE] 非就绪/测试环境空引用：仅记录，保证 HUD 与原返回路径不崩。
                    ModEntry.SMonitor?.Log(
                        $"[FriendshipSettlement] Social profile ingestion skipped for '{npcName}': {ex.Message}",
                        LogLevel.Debug);
                }
            }

            // 4. 无论是否达到上限，始终向玩家展示戏剧化的视听反馈
            ShowHudFeedback(displayName, rawDelta, feedbackText);
        }

        /// <summary>
        /// 计算某角色在每日上限额度下允许增减的好感点数（-20 ~ +20）
        /// </summary>
        public int CalculateAllowedDelta(string npcName, int rawDelta)
        {
            if (string.IsNullOrWhiteSpace(npcName) || rawDelta == 0) return 0;

            _dailyFriendshipChanges.TryGetValue(npcName, out int currentToday);

            if (rawDelta > 0)
            {
                int remainingGain = Math.Max(0, MaxDailyGainPerNpc - currentToday);
                return Math.Clamp(rawDelta, 0, remainingGain);
            }
            else
            {
                int remainingLoss = Math.Min(0, MaxDailyLossPerNpc - currentToday);
                return Math.Clamp(rawDelta, remainingLoss, 0);
            }
        }

        /// <summary>
        /// 左下角呈现 Stardew Valley 风格的浮动提示
        /// </summary>
        public void ShowHudFeedback(string displayName, int delta, string customFeedback)
        {
            try
            {
                if (delta > 0)
                {
                    string msg = !string.IsNullOrWhiteSpace(customFeedback)
                        ? customFeedback
                        : $"{displayName}更喜欢你了。";
                    Game1.addHUDMessage(new HUDMessage(msg, HUDMessage.achievement_type));
                }
                else if (delta < 0)
                {
                    string msg = !string.IsNullOrWhiteSpace(customFeedback)
                        ? customFeedback
                        : $"{displayName}有点难过。";
                    Game1.addHUDMessage(new HUDMessage(msg, HUDMessage.error_type));
                }
                else if (!string.IsNullOrWhiteSpace(customFeedback))
                {
                    Game1.addHUDMessage(new HUDMessage(customFeedback, HUDMessage.newQuest_type));
                }
            }
            catch (Exception ex)
            {
                ModEntry.SMonitor?.Log($"[FriendshipSettlement] Failed to show HUD message: {ex.Message}", LogLevel.Trace);
            }
        }

        /// <summary>
        /// 换天时清空每日累计好感计数
        /// </summary>
        public void OnDayStarted()
        {
            _dailyFriendshipChanges.Clear();
        }

        /// <summary>
        /// 获取某角色今日剧情已增减的好感点数（供测试与调试）
        /// </summary>
        public int GetDailyChange(string npcName)
        {
            if (string.IsNullOrWhiteSpace(npcName)) return 0;
            return _dailyFriendshipChanges.TryGetValue(npcName, out int val) ? val : 0;
        }

        /// <summary>
        /// 重置状态（供测试与回标题）
        /// </summary>
        public void Reset()
        {
            _dailyFriendshipChanges.Clear();
        }
    }
}
