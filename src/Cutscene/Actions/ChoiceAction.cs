using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;
using ValleytalkReborn.Cutscene.Services;
using ValleytalkReborn.Cutscene.UI;

namespace ValleytalkReborn.Cutscene.Actions
{
    /// <summary>
    /// 对话分支选项定义
    /// </summary>
    public sealed class ChoiceOption
    {
        public string Text { get; set; } = string.Empty;
        public int FriendshipDelta { get; set; } = 0;
        public string FeedbackMessage { get; set; }
        public List<IDirectorAction> Actions { get; set; } = new();

        public ChoiceOption(string text, int friendshipDelta = 0, string feedbackMessage = null, List<IDirectorAction> actions = null)
        {
            Text = text ?? string.Empty;
            FriendshipDelta = friendshipDelta;
            FeedbackMessage = feedbackMessage;
            if (actions != null)
            {
                Actions = actions;
            }
        }
    }

    /// <summary>
    /// 互动抉择动作：在宽银幕上呈现选项覆盖层，根据玩家选择触发微量好感度变动、左下角浮动提示与动态分支动作注入
    /// </summary>
    public sealed class ChoiceAction : IDirectorAction
    {
        public bool WaitForCompletion { get; set; } = true;

        private readonly string _prompt;
        private readonly List<ChoiceOption> _options;
        private readonly NPC _targetActor;
        private bool _isSerendipity;
        private bool _isCompleted;
        private CinematicChoiceOverlay _overlay;

        public string Prompt => _prompt;
        public IReadOnlyList<ChoiceOption> Options => _options;
        public NPC TargetActor => _targetActor;
        public bool IsSerendipity
        {
            get => _isSerendipity;
            set => _isSerendipity = value;
        }

        public ChoiceAction(
            string prompt,
            List<ChoiceOption> options,
            NPC targetActor = null,
            bool isSerendipity = false)
        {
            _prompt = prompt ?? string.Empty;
            _options = options != null && options.Count > 0
                ? new List<ChoiceOption>(options)
                : new List<ChoiceOption> { new("继续") };
            _targetActor = targetActor;
            _isSerendipity = isSerendipity;
        }

        public void Enter()
        {
            _isCompleted = false;

            if (_options.Count == 0)
            {
                _isCompleted = true;
                return;
            }

            var optionTexts = _options.Select(o => o.Text).ToList();
            _overlay = new CinematicChoiceOverlay(_prompt, optionTexts, OnOptionSelected);

            VirtualDirector.Instance?.ShowChoiceOverlay(_overlay);

            ModEntry.SMonitor?.Log(
                $"[ChoiceAction] Prompting player with {_options.Count} choices for {(_targetActor != null ? _targetActor.Name : "Cutscene")}.",
                LogLevel.Debug);
        }

        private void OnOptionSelected(int index)
        {
            if (index < 0 || index >= _options.Count)
            {
                _isCompleted = true;
                return;
            }

            var chosen = _options[index];
            ModEntry.SMonitor?.Log(
                $"[ChoiceAction] Player selected choice [{index + 1}]: '{chosen.Text}' (Friendship: {chosen.FriendshipDelta}).",
                LogLevel.Info);

            // 1. 好感度结算与左下角浮动提示（带每日防刷硬顶与 F9 免结算保护）
            FriendshipSettlementService.Instance.ApplyFriendshipDelta(
                _targetActor?.Name,
                chosen.FriendshipDelta,
                _isSerendipity,
                chosen.FeedbackMessage);

            // 2. 动态向导演动作队列首部注入该分支专属动作序列
            if (chosen.Actions != null && chosen.Actions.Count > 0)
            {
                VirtualDirector.Instance?.PrependActions(chosen.Actions);
            }

            // 3. 关闭选项覆盖层并标记动作完成
            VirtualDirector.Instance?.HideChoiceOverlay();
            _isCompleted = true;
        }

        public bool Update(GameTime time)
        {
            return _isCompleted;
        }

        public void Exit()
        {
            VirtualDirector.Instance?.HideChoiceOverlay();
            _overlay = null;
        }
    }
}
