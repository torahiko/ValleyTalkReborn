using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;
using StardewValley.Menus;

namespace ValleytalkReborn
{
    /// <summary>
    /// 数值步进器：用 [ < ] 4心 [ > ] 彻底替代循环盲盒点击
    /// </summary>
    internal sealed class NumberStepper
    {
        private Rectangle _bounds;
        private Rectangle _leftBtn;
        private Rectangle _rightBtn;
        public int Value { get; set; }
        public int Min { get; }
        public int Max { get; }
        public int Step { get; }
        public string Suffix { get; }
        public event Action<int> OnChanged;

        public NumberStepper(Rectangle bounds, int initial, int min, int max, int step, string suffix = "")
        {
            _bounds = bounds;
            Value = initial;
            Min = min;
            Max = max;
            Step = step;
            Suffix = suffix;
            Layout();
        }

        public void SetBounds(Rectangle bounds)
        {
            _bounds = bounds;
            Layout();
        }

        private void Layout()
        {
            int btnW = 28;
            _leftBtn = new Rectangle(_bounds.X, _bounds.Y, btnW, _bounds.Height);
            _rightBtn = new Rectangle(_bounds.Right - btnW, _bounds.Y, btnW, _bounds.Height);
        }

        public bool ReceiveLeftClick(int x, int y)
        {
            if (_leftBtn.Contains(x, y))
            {
                if (Value > Min)
                {
                    Value = Math.Max(Min, Value - Step);
                    OnChanged?.Invoke(Value);
                    Game1.playSound("drumkit6");
                }
                return true;
            }
            if (_rightBtn.Contains(x, y))
            {
                if (Value < Max)
                {
                    Value = Math.Min(Max, Value + Step);
                    OnChanged?.Invoke(Value);
                    Game1.playSound("drumkit6");
                }
                return true;
            }
            return false;
        }

        public void Draw(SpriteBatch b)
        {
            int mx = Game1.getMouseX();
            int my = Game1.getMouseY();

            // 背景槽
            IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(403, 383, 6, 6),
                _bounds.X, _bounds.Y, _bounds.Width, _bounds.Height, Color.White * 0.8f, 2f, false);

            // 减按钮
            bool lHover = _leftBtn.Contains(mx, my);
            IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(432, 439, 9, 9),
                _leftBtn.X, _leftBtn.Y, _leftBtn.Width, _leftBtn.Height, lHover ? new Color(255, 235, 205) : new Color(220, 200, 175), 2f, false);
            b.DrawString(Game1.smallFont, "<", new Vector2(_leftBtn.X + 9, _leftBtn.Y + 4), Value > Min ? Game1.textColor : Color.Gray);

            // 加按钮
            bool rHover = _rightBtn.Contains(mx, my);
            IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(432, 439, 9, 9),
                _rightBtn.X, _rightBtn.Y, _rightBtn.Width, _rightBtn.Height, rHover ? new Color(255, 235, 205) : new Color(220, 200, 175), 2f, false);
            b.DrawString(Game1.smallFont, ">", new Vector2(_rightBtn.X + 9, _rightBtn.Y + 4), Value < Max ? Game1.textColor : Color.Gray);

            // 中间数值
            string label = $"{Value}{Suffix}";
            Vector2 sz = Game1.smallFont.MeasureString(label);
            b.DrawString(Game1.smallFont, label, new Vector2(_bounds.X + (_bounds.Width - sz.X) / 2f, _bounds.Y + 5), Game1.textColor);
        }
    }
}
