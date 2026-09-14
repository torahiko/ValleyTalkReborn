using System;
using System.Collections.Generic;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

namespace ValleytalkReborn
{
    /// <summary>
    /// Manages deferred text input for dialogue responses
    /// </summary>
    public static class TextInputManager
    {
        /// <summary>
        /// 封装单次文本输入请求的数据包，避免全局分散变量
        /// </summary>
        private class InputRequest
        {
            public string Title { get; }
            public NPC Npc { get; }
            public string DialogueKey { get; }
            public List<ConversationElement> ResponseHistory { get; }

            public InputRequest(string title, NPC npc, string dialogueKey, List<ConversationElement> responseHistory)
            {
                Title = string.IsNullOrWhiteSpace(title) ? "Enter your response" : title;
                Npc = npc;
                DialogueKey = dialogueKey ?? string.Empty;
                ResponseHistory = responseHistory ?? new List<ConversationElement>();
            }
        }

        private static InputRequest _pendingRequest;
        private static bool _isInitialized;

        /// <summary>
        /// Initialize the text input manager with mod events
        /// </summary>
        public static void Initialize(IModEvents events)
        {
            if (_isInitialized) return;

            events.GameLoop.UpdateTicked += OnUpdateTicked;
            _isInitialized = true;
        }

        /// <summary>
        /// Cleans up event subscriptions and resets state.
        /// </summary>
        public static void Cleanup(IModEvents events)
        {
            if (_isInitialized && events != null)
            {
                events.GameLoop.UpdateTicked -= OnUpdateTicked;
                _isInitialized = false;
            }

            _pendingRequest = null;
        }

        /// <summary>
        /// Request text input - this will be handled on the next frame when no active menu is present
        /// </summary>
        public static void RequestTextInput(string title, NPC npc, string dialogueKey = "", List<ConversationElement> dialogueHistory = null)
        {
            if (npc == null)
            {
                ModEntry.SMonitor?.Log("[TextInputManager] Ignored RequestTextInput call because NPC was null.", LogLevel.Warn);
                return;
            }

            var request = new InputRequest(title, npc, dialogueKey, dialogueHistory);

            // 如果当前没有活跃菜单（如 Alt+点击直接触发），立即显示
            if (Game1.activeClickableMenu == null)
            {
                ShowTextInputMenu(request);
            }
            else
            {
                // 有菜单正在显示（如从对话选项触发），等菜单关闭后再显示
                _pendingRequest = request;
            }
        }

        private static void OnUpdateTicked(object sender, UpdateTickedEventArgs e)
        {
            if (_pendingRequest != null && Game1.activeClickableMenu == null)
            {
                var request = _pendingRequest;
                _pendingRequest = null;
                ShowTextInputMenu(request);
            }
        }

        private static void ShowTextInputMenu(InputRequest request)
        {
            try
            {
                // 通过 lambda 闭包直接将 request 传递给回调，无需依赖全局静态状态
                var textInputMenu = new DialogueTextInputMenu(request.Title, text => OnTextEntered(text, request), request.Npc);
                Game1.activeClickableMenu = new DialogueTextInputMenuWrapper(textInputMenu);
            }
            catch (Exception ex)
            {
                ModEntry.SMonitor?.Log($"Error showing text input menu: {ex.Message}", LogLevel.Error);
            }
        }

        private static void OnTextEntered(string enteredText, InputRequest request)
        {
            Game1.exitActiveMenu();

            if (string.IsNullOrWhiteSpace(enteredText))return;

            try
            {
                Game1.currentSpeaker = request.Npc;

                // 用户主动提交文本输入，清零冷却防止请求被静默丢弃
                AsyncBuilder.Instance.ClearCooldown();

                // Only record history after the request is successfully accepted
                var historyCopy = new List<ConversationElement>(request.ResponseHistory)
                {
                    new ConversationElement(enteredText, true)
                };

                bool accepted = AsyncBuilder.Instance.RequestNpcResponse(request.Npc, historyCopy.ToArray());
                if (accepted)
                {
                    DialogueHistoryManager.Instance.RecordPlayerDialogue(request.Npc.Name, enteredText);
                    request.Npc.grantConversationFriendship(Game1.player);
                }
                else
                {
                    ModEntry.SMonitor?.Log($"[TextInputManager] Request rejected by AsyncBuilder for {request.Npc.Name}.", LogLevel.Warn);
                }
            }
            catch (Exception ex)
            {
                ModEntry.SMonitor?.Log($"Error handling text input: {ex.Message}", LogLevel.Error);
            }
        }
    }
}