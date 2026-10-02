using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewValley;

namespace ValleytalkReborn.Cutscene.UI
{
    /// <summary>
    /// 电影级沉浸互动选项覆盖层：在宽银幕黑边上方呈现半透明金边选择肢，支持全平台按键、手柄与鼠标交互
    /// </summary>
    public sealed class CinematicChoiceOverlay
    {
        private readonly string _promptText;
        private readonly List<string> _options;
        private readonly Action<int> _onSelected;

        private int _focusedIndex = 0;
        private bool _isCompleted = false;

        private KeyboardState _lastKeyState;
        private MouseState _lastMouseState;
        private GamePadState _lastPadState;

        public bool IsCompleted => _isCompleted;

        public CinematicChoiceOverlay(string promptText, List<string> options, Action<int> onSelected)
        {
            _promptText = promptText ?? string.Empty;
            _options = options != null && options.Count > 0 ? options : new List<string> { "继续" };
            _onSelected = onSelected;

            _lastKeyState = Keyboard.GetState();
            _lastMouseState = Mouse.GetState();
            _lastPadState = GamePad.GetState(PlayerIndex.One);
        }

        public void Update(GameTime time)
        {
            if (_isCompleted) return;

            var curKeyState = Keyboard.GetState();
            var curMouseState = Mouse.GetState();
            var curPadState = GamePad.GetState(PlayerIndex.One);

            // 1. 键盘数字快捷键（直接按 1, 2, 3... 立即触发对应选项）
            for (int i = 0; i < Math.Min(_options.Count, 9); i++)
            {
                var keyNum = Keys.D1 + i;
                var padNum = Keys.NumPad1 + i;
                if ((curKeyState.IsKeyDown(keyNum) && _lastKeyState.IsKeyUp(keyNum)) ||
                    (curKeyState.IsKeyDown(padNum) && _lastKeyState.IsKeyUp(padNum)))
                {
                    ConfirmOption(i);
                    return;
                }
            }

            // 2. 键盘/手柄 上下导航
            bool upPressed = (curKeyState.IsKeyDown(Keys.Up) && _lastKeyState.IsKeyUp(Keys.Up)) ||
                             (curKeyState.IsKeyDown(Keys.W) && _lastKeyState.IsKeyUp(Keys.W)) ||
                             (curPadState.DPad.Up == ButtonState.Pressed && _lastPadState.DPad.Up == ButtonState.Released);

            bool downPressed = (curKeyState.IsKeyDown(Keys.Down) && _lastKeyState.IsKeyUp(Keys.Down)) ||
                               (curKeyState.IsKeyDown(Keys.S) && _lastKeyState.IsKeyUp(Keys.S)) ||
                               (curPadState.DPad.Down == ButtonState.Pressed && _lastPadState.DPad.Down == ButtonState.Released);

            if (upPressed)
            {
                int prev = _focusedIndex;
                _focusedIndex = Math.Max(0, _focusedIndex - 1);
                if (_focusedIndex != prev) Game1.playSound("dialogueCharacter");
            }
            else if (downPressed)
            {
                int prev = _focusedIndex;
                _focusedIndex = Math.Min(_options.Count - 1, _focusedIndex + 1);
                if (_focusedIndex != prev) Game1.playSound("dialogueCharacter");
            }

            // 3. 键盘回车/空格 或 手柄 A 键确认
            bool enterPressed = (curKeyState.IsKeyDown(Keys.Enter) && _lastKeyState.IsKeyUp(Keys.Enter)) ||
                                (curKeyState.IsKeyDown(Keys.Space) && _lastKeyState.IsKeyUp(Keys.Space)) ||
                                (curPadState.Buttons.A == ButtonState.Pressed && _lastPadState.Buttons.A == ButtonState.Released);

            if (enterPressed && _focusedIndex >= 0 && _focusedIndex < _options.Count)
            {
                ConfirmOption(_focusedIndex);
                return;
            }

            // 4. 鼠标悬浮与点击检测
            var vp = Game1.graphics.GraphicsDevice.Viewport;
            int boxWidth = Math.Min(vp.Width - 120, 720);
            int itemHeight = 48;
            int itemSpacing = 10;
            int headerHeight = string.IsNullOrWhiteSpace(_promptText) ? 0 : 36;
            int totalContentHeight = headerHeight + _options.Count * (itemHeight + itemSpacing);
            int boxY = (vp.Height - totalContentHeight) / 2;
            int boxX = (vp.Width - boxWidth) / 2;

            int mouseX = curMouseState.X;
            int mouseY = curMouseState.Y;
            bool mouseLeftClicked = curMouseState.LeftButton == ButtonState.Pressed && _lastMouseState.LeftButton == ButtonState.Released;

            for (int i = 0; i < _options.Count; i++)
            {
                int itemY = boxY + headerHeight + i * (itemHeight + itemSpacing);
                var itemRect = new Rectangle(boxX, itemY, boxWidth, itemHeight);

                if (itemRect.Contains(mouseX, mouseY))
                {
                    if (_focusedIndex != i)
                    {
                        _focusedIndex = i;
                        Game1.playSound("dialogueCharacter");
                    }

                    if (mouseLeftClicked)
                    {
                        ConfirmOption(i);
                        return;
                    }
                }
            }

            _lastKeyState = curKeyState;
            _lastMouseState = curMouseState;
            _lastPadState = curPadState;
        }

        private void ConfirmOption(int index)
        {
            if (_isCompleted) return;

            _isCompleted = true;
            Game1.playSound("select");
            _onSelected?.Invoke(index);
        }

        public void Draw(SpriteBatch b, float blackBarHeight)
        {
            if (_isCompleted) return;

            var vp = Game1.graphics.GraphicsDevice.Viewport;
            int boxWidth = Math.Min(vp.Width - 120, 720);
            int itemHeight = 48;
            int itemSpacing = 10;
            int headerHeight = string.IsNullOrWhiteSpace(_promptText) ? 0 : 40;
            int totalContentHeight = headerHeight + _options.Count * (itemHeight + itemSpacing) + 20;

            // 居中偏下呈现，不挡住角色视线
            int boxY = Math.Max((int)blackBarHeight + 10, (vp.Height - totalContentHeight) / 2);
            int boxX = (vp.Width - boxWidth) / 2;

            // 1. 标题提示栏（若有）
            if (!string.IsNullOrWhiteSpace(_promptText))
            {
                var promptSize = Game1.smallFont.MeasureString(_promptText);
                var promptRect = new Rectangle(boxX, boxY, boxWidth, 34);

                // 雅致半透明底板
                b.Draw(Game1.staminaRect, promptRect, new Color(10, 14, 22, 220));
                DrawBorder(b, promptRect, new Color(218, 165, 32, 160), 1);

                Vector2 promptPos = new Vector2(boxX + (boxWidth - promptSize.X) / 2f, boxY + 6);
                b.DrawString(Game1.smallFont, _promptText, promptPos + new Vector2(1, 1), Color.Black * 0.8f);
                b.DrawString(Game1.smallFont, _promptText, promptPos, Color.Gold);
            }

            // 2. 选项按钮列表
            for (int i = 0; i < _options.Count; i++)
            {
                int itemY = boxY + headerHeight + i * (itemHeight + itemSpacing);
                var itemRect = new Rectangle(boxX, itemY, boxWidth, itemHeight);
                bool isFocused = (i == _focusedIndex);

                // 选项底板：高亮时金墨微光，普通时沉稳黑蓝
                Color bgColor = isFocused
                    ? new Color(36, 42, 56, 240)
                    : new Color(16, 20, 28, 210);
                b.Draw(Game1.staminaRect, itemRect, bgColor);

                // 边框：高亮时金边加粗
                Color borderColor = isFocused
                    ? new Color(255, 215, 0, 240)
                    : new Color(160, 145, 120, 140);
                DrawBorder(b, itemRect, borderColor, isFocused ? 2 : 1);

                // 选项文字
                string displayText = $"[{i + 1}]  {_options[i]}";
                var textSize = Game1.smallFont.MeasureString(displayText);
                Vector2 textPos = new Vector2(boxX + 24, itemY + (itemHeight - textSize.Y) / 2f);

                Color textColor = isFocused ? Color.Gold : Color.White * 0.95f;

                // 阴影与文本
                b.DrawString(Game1.smallFont, displayText, textPos + new Vector2(1, 1), Color.Black * 0.8f);
                b.DrawString(Game1.smallFont, displayText, textPos, textColor);

                // 高亮时右侧箭头标识
                if (isFocused)
                {
                    string arrow = "◀";
                    var arrowSize = Game1.smallFont.MeasureString(arrow);
                    Vector2 arrowPos = new Vector2(boxX + boxWidth - arrowSize.X - 20, textPos.Y);
                    b.DrawString(Game1.smallFont, arrow, arrowPos + new Vector2(1, 1), Color.Black * 0.8f);
                    b.DrawString(Game1.smallFont, arrow, arrowPos, Color.Gold);
                }
            }
        }

        private static void DrawBorder(SpriteBatch b, Rectangle rect, Color color, int thickness)
        {
            b.Draw(Game1.staminaRect, new Rectangle(rect.X, rect.Y, rect.Width, thickness), color);
            b.Draw(Game1.staminaRect, new Rectangle(rect.X, rect.Bottom - thickness, rect.Width, thickness), color);
            b.Draw(Game1.staminaRect, new Rectangle(rect.X, rect.Y, thickness, rect.Height), color);
            b.Draw(Game1.staminaRect, new Rectangle(rect.Right - thickness, rect.Y, thickness, rect.Height), color);
        }
    }
}
