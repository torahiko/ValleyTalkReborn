using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewValley;
using StardewValley.Menus;
using System;

namespace ValleytalkReborn
{
    internal sealed class BioAiPromptDialog : IClickableMenu
    {
        private const int MenuWidth = 680;
        private const int MenuHeight = 340;
        private const int ButtonSize = 48;
        private const float HoverFontSize = 16f;

        private readonly Action<string, bool> _onSubmit;
        private readonly new IClickableMenu _parentMenu;
        private readonly string _targetTitle;
        private readonly string _titleText;
        private readonly bool _allowEmptyDemand;
        private readonly DialogueTextInputBox _inputBox;
        private readonly ClickableTextureComponent _okBtn;
        private readonly ClickableTextureComponent _cancelBtn;
        private Rectangle _thinkingPillRect;
        private static bool _enableThinking;
        private string _hoverText;

        public BioAiPromptDialog(string targetTitle, IClickableMenu parentMenu, Action<string, bool> onSubmit, bool allowEmptyDemand = false)
            : base(
                (Game1.uiViewport.Width - MenuWidth) / 2,
                (Game1.uiViewport.Height - MenuHeight) / 2,
                MenuWidth,
                MenuHeight)
        {
            _onSubmit = onSubmit;
            _parentMenu = parentMenu;
            _targetTitle = targetTitle ?? "";
            _titleText = $"AI 润色 · {_targetTitle}";
            _allowEmptyDemand = allowEmptyDemand;

            _inputBox = new DialogueTextInputBox(600)
            {
                Position = new Vector2(xPositionOnScreen + 32, yPositionOnScreen + 90),
                Extent = new Vector2(width - 64, 130),
                CustomFontSize = 20f,
                UseCustomFont = true,
                AllowNewlines = false,
                Selected = true,
                ShowCharacterCount = true
            };

            int buttonY = yPositionOnScreen + height - 72;
            _okBtn = new ClickableTextureComponent(
                new Rectangle(xPositionOnScreen + width - 80, buttonY, ButtonSize, ButtonSize),
                Game1.mouseCursors,
                Game1.getSourceRectForStandardTileSheet(Game1.mouseCursors, 46, -1, -1),
                1f);

            _cancelBtn = new ClickableTextureComponent(
                new Rectangle(_okBtn.bounds.X - 60, buttonY, ButtonSize, ButtonSize),
                Game1.mouseCursors,
                Game1.getSourceRectForStandardTileSheet(Game1.mouseCursors, 47, -1, -1),
                1f);

            _thinkingPillRect = new Rectangle(
                xPositionOnScreen + 32,
                buttonY + (ButtonSize - 32) / 2,
                170,
                32);

            Game1.keyboardDispatcher.Subscriber = _inputBox;
        }

        public override void update(GameTime time)
        {
            base.update(time);
            _inputBox.Update(time);
        }

        public override void receiveLeftClick(int x, int y, bool playSound = true)
        {
            if (_inputBox.ReceiveLeftClick(x, y))
                return;

            if (_thinkingPillRect.Contains(x, y))
            {
                _enableThinking = !_enableThinking;
                Game1.playSound("smallSelect");
                return;
            }

            if (_okBtn.containsPoint(x, y))
            {
                TrySubmit();
                return;
            }

            if (_cancelBtn.containsPoint(x, y))
                Close(false);
        }

        public override void receiveKeyPress(Keys key)
        {
            if (key == Keys.Escape)
            {
                Game1.playSound("cancel");
                Close(false);
                return;
            }

            if (Game1.options.doesInputListContain(Game1.options.menuButton, key))
                return;

            if (key == Keys.Enter)
            {
                TrySubmit();
                return;
            }

            if (DialogueTextInputBox.IsControlKeyDown())
            {
                if (key == Keys.A || key == Keys.C || key == Keys.X || key == Keys.Z || key == Keys.V)
                    _inputBox.RecieveSpecialInput(key);
                return;
            }

            if (key == Keys.Left || key == Keys.Right || key == Keys.Up || key == Keys.Down ||
                key == Keys.Home || key == Keys.End || key == Keys.Delete || key == Keys.Back)
            {
                _inputBox.RecieveSpecialInput(key);
            }
        }

        private void TrySubmit()
        {
            if (string.IsNullOrWhiteSpace(_inputBox.Text))
            {
                if (_allowEmptyDemand)
                {
                    Game1.playSound("coin");
                    Close(true);
                    _onSubmit?.Invoke(string.Empty, _enableThinking);
                }
                else
                {
                    Game1.playSound("cancel");
                }
                return;
            }

            Game1.playSound("coin");
            Close(true);
            _onSubmit?.Invoke(_inputBox.Text.Trim(), _enableThinking);
        }

        private void Close(bool submitted)
        {
            Game1.keyboardDispatcher.Subscriber = null;

            if (!submitted)
                Game1.playSound("bigDeSelect");

            Game1.activeClickableMenu = _parentMenu;
        }

        public override void draw(SpriteBatch b)
        {
            b.Draw(
                Game1.fadeToBlackRect,
                Game1.graphics.GraphicsDevice.Viewport.Bounds,
                Color.Black * 0.45f);

            Game1.drawDialogueBox(xPositionOnScreen, yPositionOnScreen, width, height, false, true);

            CustomFontManager.DrawStringBold(
                b,
                _titleText,
                new Vector2(xPositionOnScreen + 32, yPositionOnScreen + 38),
                BioEditorMenu.TextPrimary,
                22f);

            _inputBox.Draw(b);

            Color pillColor = _enableThinking ? Color.Gold : new Color(235, 220, 195);
            string pillText = _enableThinking ? "深度思考: 开" : "极速模式: 开";
            IClickableMenu.drawTextureBox(
                b,
                Game1.mouseCursors,
                new Rectangle(432, 439, 9, 9),
                _thinkingPillRect.X,
                _thinkingPillRect.Y,
                _thinkingPillRect.Width,
                _thinkingPillRect.Height,
                pillColor,
                2f,
                false);

            Vector2 pillTextSize = CustomFontManager.MeasureString(pillText, HoverFontSize);
            CustomFontManager.DrawString(
                b,
                pillText,
                new Vector2(
                    _thinkingPillRect.X + (_thinkingPillRect.Width - pillTextSize.X) / 2f,
                    _thinkingPillRect.Y + (_thinkingPillRect.Height - pillTextSize.Y) / 2f - 1f),
                BioEditorMenu.TextPrimary,
                HoverFontSize);

            int mouseX = Game1.getMouseX();
            int mouseY = Game1.getMouseY();
            _hoverText = _thinkingPillRect.Contains(mouseX, mouseY)
                ? _enableThinking
                    ? "启用模型进行多阶段推理，回答更完整严谨，但响应时间更长。\n适合复杂设定与需要反复推敲的需求。"
                    : "直接生成简洁回复，响应更快，适合日常快速润色。\n适合明确、简短且不要求深入推理的需求。"
                : null;

            _okBtn.draw(b);
            _cancelBtn.draw(b);

            if (!string.IsNullOrEmpty(_hoverText))
                DrawHoverBubble(b, _hoverText);

            drawMouse(b);
        }

        private static void DrawHoverBubble(SpriteBatch b, string text)
        {
            Vector2 textSize = CustomFontManager.MeasureString(text, HoverFontSize);
            const int padX = 16;
            const int padY = 10;

            int boxWidth = (int)MathF.Ceiling(textSize.X) + padX * 2;
            int boxHeight = (int)MathF.Ceiling(textSize.Y) + padY * 2;
            int x = Game1.getOldMouseX() + 24;
            int y = Game1.getOldMouseY() + 24;
            Rectangle safe = Utility.getSafeArea();

            if (x + boxWidth > safe.Right)
                x = safe.Right - boxWidth;
            if (y + boxHeight > safe.Bottom)
                y = safe.Bottom - boxHeight;
            if (x < safe.Left)
                x = safe.Left;
            if (y < safe.Top)
                y = safe.Top;

            IClickableMenu.drawTextureBox(
                b,
                Game1.menuTexture,
                new Rectangle(0, 256, 60, 60),
                x + 4,
                y + 4,
                boxWidth,
                boxHeight,
                Color.Black * 0.28f,
                0.65f,
                false);

            IClickableMenu.drawTextureBox(
                b,
                Game1.menuTexture,
                new Rectangle(0, 256, 60, 60),
                x,
                y,
                boxWidth,
                boxHeight,
                new Color(255, 255, 250),
                0.65f,
                false);

            CustomFontManager.DrawString(
                b,
                text,
                new Vector2(x + padX, y + padY),
                BioEditorMenu.TextPrimary,
                HoverFontSize);
        }
    }
}
