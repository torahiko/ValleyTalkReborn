using System;
using System.Collections.Concurrent;
using System.Text;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewValley;
using StardewValley.Menus;

namespace ValleytalkReborn
{
    internal sealed class BioAiReviewMenu : IClickableMenu
    {
        private enum ReviewPhase
        {
            Thinking,
            Streaming,
            Settled
        }

        private const int MenuWidth = 1000;
        private const int MenuHeight = 600;
        private const int ButtonSize = 48;

        private readonly string _sectionTitle;
        private readonly new IClickableMenu _parentMenu;
        private readonly Action<string> _onAccepted;
        private readonly DialogueTextInputBox _reviewTextBox;
        private readonly ClickableTextureComponent _acceptButton;
        private readonly ClickableTextureComponent _cancelButton;

        private Rectangle _stopButtonRect;
        private ConcurrentQueue<string> _streamQueue;
        private ReviewPhase _phase = ReviewPhase.Thinking;
        private string _headerText = "AI 正在构思…";

        public BioAiReviewMenu(string sectionTitle, IClickableMenu parentMenu, Action<string> onAccepted)
            : base(
                (Game1.uiViewport.Width - MenuWidth) / 2,
                (Game1.uiViewport.Height - MenuHeight) / 2,
                MenuWidth,
                MenuHeight)
        {
            _sectionTitle = sectionTitle ?? "";
            _parentMenu = parentMenu;
            _onAccepted = onAccepted;

            _reviewTextBox = new DialogueTextInputBox(6000)
            {
                AllowNewlines = true,
                UseCustomFont = true,
                CustomFontSize = 22f,
                Position = new Vector2(xPositionOnScreen + 32, yPositionOnScreen + 100),
                Extent = new Vector2(width - 64, height - 190),
                Selected = false
            };

            _acceptButton = new ClickableTextureComponent(
                Rectangle.Empty,
                Game1.mouseCursors,
                Game1.getSourceRectForStandardTileSheet(Game1.mouseCursors, 46, -1, -1),
                1f);

            _cancelButton = new ClickableTextureComponent(
                Rectangle.Empty,
                Game1.mouseCursors,
                Game1.getSourceRectForStandardTileSheet(Game1.mouseCursors, 47, -1, -1),
                1f);

            Layout();
        }

        public string ReviewText => _reviewTextBox.Text;

        public void BeginStreaming(ConcurrentQueue<string> tokenQueue)
        {
            _streamQueue = tokenQueue;
        }

        public void OnStreamSettled(BioAiResult result)
        {
            if (!ReferenceEquals(Game1.activeClickableMenu, this))
                return;

            if (_phase == ReviewPhase.Settled)
                return;

            PumpTokens();

            switch (result.Kind)
            {
                case BioAiResultKind.Success:
                    if (_reviewTextBox.Text.StartsWith("```", StringComparison.Ordinal))
                        _reviewTextBox.SetText(result.Text);

                    EnterSettled($"✔ 审阅【{_sectionTitle}】生成结果（可自由修改）");
                    Game1.playSound("shiny4");
                    break;

                case BioAiResultKind.Failure:
                case BioAiResultKind.TimedOut:
                    if (string.IsNullOrWhiteSpace(_reviewTextBox.Text))
                    {
                        CloseToParent();
                        return;
                    }

                    EnterSettled("生成中断：可编辑现有内容，或点 ✕ 放弃");
                    break;

                case BioAiResultKind.Cancelled:
                    if (string.IsNullOrWhiteSpace(_reviewTextBox.Text))
                    {
                        CloseToParent();
                        return;
                    }

                    EnterSettled("已停止生成：可编辑现有内容，或点 ✕ 放弃");
                    break;
            }
        }

        private void EnterSettled(string headerText)
        {
            _reviewTextBox.Selected = true;
            Game1.keyboardDispatcher.Subscriber = _reviewTextBox;
            _phase = ReviewPhase.Settled;
            _headerText = headerText;
        }

        private void CloseToParent()
        {
            if (ReferenceEquals(Game1.keyboardDispatcher.Subscriber, _reviewTextBox))
                Game1.keyboardDispatcher.Subscriber = null;

            Game1.activeClickableMenu = _parentMenu;
        }

        public override void update(GameTime time)
        {
            base.update(time);
            _reviewTextBox.Update(time);
            PumpTokens();
        }

        private void PumpTokens()
        {
            // 批量排空：单帧只取一个 token 会把渲染吞吐钉死在帧率（~60 token/s），
            // 快速供应商必然积压；且落定后 Settled 守卫会永久丢弃积压尾部，导致审阅框缺尾。
            if (_phase == ReviewPhase.Settled || _streamQueue == null || _streamQueue.IsEmpty)
                return;

            var batch = new StringBuilder();
            while (_streamQueue.TryDequeue(out string token))
                batch.Append(token);

            if (batch.Length == 0)
                return;

            _reviewTextBox.AppendStreamingText(batch.ToString());
            if (_phase == ReviewPhase.Thinking)
            {
                _phase = ReviewPhase.Streaming;
                _headerText = $"正在流式生成【{_sectionTitle}】…";
            }
        }

        public override void receiveLeftClick(int x, int y, bool playSound = true)
        {
            if (_phase != ReviewPhase.Settled)
            {
                if (_stopButtonRect.Contains(x, y))
                {
                    Game1.playSound("cancel");
                    BioAiRunner.CancelCurrentTask();
                }

                return;
            }

            if (_reviewTextBox.ReceiveLeftClick(x, y))
            {
                Game1.keyboardDispatcher.Subscriber = _reviewTextBox;
                return;
            }

            if (_acceptButton.containsPoint(x, y))
            {
                Game1.playSound("coin");
                string confirmedText = _reviewTextBox.Text;
                CloseToParent();
                _onAccepted?.Invoke(confirmedText);
                return;
            }

            if (_cancelButton.containsPoint(x, y))
            {
                Game1.playSound("cancel");
                CloseToParent();
            }
        }

        public override void receiveKeyPress(Keys key)
        {
            if (key == Keys.Escape)
            {
                if (_phase != ReviewPhase.Settled)
                {
                    Game1.playSound("cancel");
                    BioAiRunner.CancelCurrentTask();
                }
                else
                {
                    CloseToParent();
                }

                return;
            }

            if (Game1.options.doesInputListContain(Game1.options.menuButton, key))
                return;

            if (_phase != ReviewPhase.Settled)
                return;

            if (DialogueTextInputBox.IsControlKeyDown())
            {
                if (key == Keys.A || key == Keys.C || key == Keys.X || key == Keys.Z || key == Keys.V)
                    _reviewTextBox.RecieveSpecialInput(key);

                return;
            }

            if (key == Keys.Left || key == Keys.Right || key == Keys.Up || key == Keys.Down ||
                key == Keys.Home || key == Keys.End || key == Keys.Delete || key == Keys.Back ||
                key == Keys.Enter)
            {
                _reviewTextBox.RecieveSpecialInput(key);
            }
        }

        public override void receiveScrollWheelAction(int direction)
        {
            _reviewTextBox.ReceiveScrollWheel(direction);
        }

        public override void gameWindowSizeChanged(Rectangle oldBounds, Rectangle newBounds)
        {
            Layout();
        }

        private void Layout()
        {
            xPositionOnScreen = (Game1.uiViewport.Width - MenuWidth) / 2;
            yPositionOnScreen = (Game1.uiViewport.Height - MenuHeight) / 2;
            width = MenuWidth;
            height = MenuHeight;

            _reviewTextBox.Position = new Vector2(xPositionOnScreen + 32, yPositionOnScreen + 100);
            _reviewTextBox.Extent = new Vector2(width - 64, height - 190);
            _reviewTextBox.InvalidateLayout();

            int buttonY = yPositionOnScreen + height - 70;
            _stopButtonRect = new Rectangle(xPositionOnScreen + width - 244, buttonY, 212, 42);
            _acceptButton.bounds = new Rectangle(xPositionOnScreen + width - 80, buttonY - 3, ButtonSize, ButtonSize);
            _cancelButton.bounds = new Rectangle(_acceptButton.bounds.X - 56, buttonY - 3, ButtonSize, ButtonSize);
        }

        public override void draw(SpriteBatch b)
        {
            b.Draw(
                Game1.fadeToBlackRect,
                Game1.graphics.GraphicsDevice.Viewport.Bounds,
                Color.Black * 0.5f);

            Game1.drawDialogueBox(xPositionOnScreen, yPositionOnScreen, width, height, false, true);

            Color headerColor = _phase == ReviewPhase.Thinking
                ? BioEditorMenu.TextWarning
                : BioEditorMenu.TextPrimary;
            CustomFontManager.DrawStringBold(
                b,
                _headerText,
                new Vector2(xPositionOnScreen + 32, yPositionOnScreen + 38),
                headerColor,
                24f);

            _reviewTextBox.Draw(b);

            if (_phase != ReviewPhase.Settled)
                DrawStopButton(b);
            else
            {
                _acceptButton.draw(b);
                _cancelButton.draw(b);
            }

            drawMouse(b);
        }

        private void DrawStopButton(SpriteBatch b)
        {
            Color background = _stopButtonRect.Contains(Game1.getMouseX(), Game1.getMouseY())
                ? new Color(235, 95, 88)
                : BioEditorMenu.TextDanger;

            IClickableMenu.drawTextureBox(
                b,
                Game1.mouseCursors,
                new Rectangle(432, 439, 9, 9),
                _stopButtonRect.X,
                _stopButtonRect.Y,
                _stopButtonRect.Width,
                _stopButtonRect.Height,
                background,
                3f,
                false);

            const string label = "■ 停止生成 (Esc)";
            Vector2 size = CustomFontManager.MeasureStringBold(label, 18f);
            CustomFontManager.DrawStringBold(
                b,
                label,
                new Vector2(
                    _stopButtonRect.X + (_stopButtonRect.Width - size.X) / 2f,
                    _stopButtonRect.Y + (_stopButtonRect.Height - size.Y) / 2f),
                BioEditorMenu.TextOnDarkBtn,
                18f);
        }
    }
}
