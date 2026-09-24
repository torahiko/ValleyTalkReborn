using System;
using System.Collections.Concurrent;
using StardewValley;
using StardewValley.Menus;
using StardewModdingAPI;

namespace ValleytalkReborn
{
    /// <summary>
    /// AI 审阅会话工厂：统一创建 <see cref="BioAiReviewMenu"/> 并接线初始流式生成与追问闭环。
    /// 收敛 BioEditorMenu 中 5 处重复的 queue/review/BeginStreaming/ExecuteStreaming 接线块。
    /// </summary>
    internal static class BioAiReviewSession
    {
        /// <summary>
        /// 启动一次审阅会话。
        /// 前置契约：主线程、<see cref="BioAiRunner"/> 不忙（调用方守卫）、仅在 <see cref="BioAiPromptDialog"/> 提交回调内调用。
        /// </summary>
        public static void Start(
            IClickableMenu parentMenu,
            string sectionTitle,
            string baseSystemPrompt,
            string userPrompt,
            Func<string, bool> onAccepted,
            Action? onCancelled,
            bool enableThinking)
        {
            var queue = new ConcurrentQueue<string>();
            var review = new BioAiReviewMenu(sectionTitle, parentMenu, onAccepted, onCancelled,
                (menu, currentDraft) => OpenRefineDialog(menu, sectionTitle, baseSystemPrompt, currentDraft));
            review.BeginStreaming(queue);
            Game1.activeClickableMenu = review;
            BioAiRunner.ExecuteStreaming(baseSystemPrompt, userPrompt, enableThinking, queue,
                result => review.OnStreamSettled(result));
        }

        private static void OpenRefineDialog(
            BioAiReviewMenu review, string sectionTitle, string baseSystemPrompt, string currentDraft)
        {
            Game1.activeClickableMenu = new BioAiPromptDialog(
                $"追问优化 · {sectionTitle}", review,   // 父菜单 = 审阅窗本体：Esc/取消无损返回，草稿完好
                (demand, enableThinking) =>
                {
                    if (BioAiRunner.IsBusy)
                    {
                        ModEntry.SMonitor?.Log(
                            "[BioAiReviewSession] Refine requested while runner busy; ignored",
                            LogLevel.Debug);
                        return;   // 审阅窗保持 Settled，草稿完好
                    }
                    var (system, user) = BioPromptBuilder.BuildRefinementPrompt(baseSystemPrompt, currentDraft, demand);
                    var queue = new ConcurrentQueue<string>();
                    review.RestartStreamingForRefine(queue);
                    BioAiRunner.ExecuteStreaming(system, user, enableThinking, queue,
                        result => review.OnStreamSettled(result));
                });
        }
    }
}
