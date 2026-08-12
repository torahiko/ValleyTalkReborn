using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;
using StardewValley.Menus;

namespace ValleytalkReborn
{
    /// <summary>
    /// A simple "Thinking..." window to show during AI generation, with wavy bouncing text animation.
    /// </summary>
    internal class ThinkingWindow : IClickableMenu
    {
        private readonly string _message;
        private int _animationFrame;
        private float _animationTimer;
        private float _totalTime; // 累积运行时间，用于正弦波连续平滑过渡

        // Margin dimensions
        private const int Margin = 24;
        private readonly Vector2 _maxMessageSize;

        public ThinkingWindow(string message = "Thinking") : base()
        {
            _message = message ?? "Thinking";
            // 预先计算带有 3 个点时的最大尺寸，确保窗口尺寸固定
            _maxMessageSize = Game1.dialogueFont.MeasureString(_message + "...");

            _animationFrame = 0;
            _animationTimer = 0f;
            _totalTime = 0f;

            // Center the window
            this.width = (int)_maxMessageSize.X + 6 * Margin;
            this.height = (int)_maxMessageSize.Y + 5 * Margin; // 稍微加高一点，留足上下跳动的空间
            this.xPositionOnScreen = (Game1.uiViewport.Width - this.width) / 2;
            this.yPositionOnScreen = (Game1.uiViewport.Height - this.height) / 2;
        }

        public override void update(GameTime time)
        {
            base.update(time);

            float elapsedSeconds = (float)time.ElapsedGameTime.TotalSeconds;
            float elapsedMs = (float)time.ElapsedGameTime.TotalMilliseconds;

            _totalTime += elapsedSeconds;

            // 更新末尾“...”的点数变化 (每 500ms 增加一个点)
            _animationTimer += elapsedMs;
            if (_animationTimer >= 500f)
            {
                _animationFrame = (_animationFrame + 1) % 4; // 0, 1, 2, 3 个点
                _animationTimer = 0f;
            }
        }

        public override void draw(SpriteBatch b)
        {
            // 绘制半透明背景
            b.Draw(Game1.fadeToBlackRect, Game1.graphics.GraphicsDevice.Viewport.Bounds, Color.Black * 0.3f);

            // 绘制对话框外框
            Game1.drawDialogueBox(this.xPositionOnScreen, this.yPositionOnScreen, this.width, this.height, false, true);

            // 动态拼接文本
            string dots = new string('.', _animationFrame);
            string animatedMessage = _message + dots;

            // 居中基准 X 坐标与 Y 坐标（在默认居中的基础上往下调了 12 像素，可根据需要自行微调此数值）
            float textOffsetY = 28f;
            float startX = this.xPositionOnScreen + (this.width - _maxMessageSize.X) / 2f;
            float baseY = this.yPositionOnScreen + (this.height - _maxMessageSize.Y) / 2f + textOffsetY;

            // ------------------ 动效参数配置 ------------------
            float amplitude = 5f;       // 上下跳动的最大高度 (像素数，建议 4~6)
            float speed = 7f;           // 波浪起伏的速度 (越大跳得越快)
            float charWaveOffset = 0.4f; // 字符之间的相位差 (决定波浪的紧密程度)
            // --------------------------------------------------

            float currentX = startX;

            // 逐字绘制并注入正弦波纵向偏移
            for (int i = 0; i < animatedMessage.Length; i++)
            {
                string charStr = animatedMessage[i].ToString();

                // 利用 Math.Sin 算每个字符当前的 Y 轴偏移
                float yOffset = (float)Math.Sin(_totalTime * speed + i * charWaveOffset) * amplitude;

                Vector2 charPos = new Vector2(currentX, baseY + yOffset);
                b.DrawString(Game1.dialogueFont, charStr, charPos, Game1.textColor);

                // 游标向右推进单个字符的宽度
                currentX += Game1.dialogueFont.MeasureString(charStr).X;
            }

            // 绘制鼠标指针
            if (!Game1.options.hardwareCursor)
            {
                b.Draw(Game1.mouseCursors, new Vector2(Game1.getMouseX(), Game1.getMouseY()),
                    Game1.getSourceRectForStandardTileSheet(Game1.mouseCursors, 0, 16, 16),
                    Color.White, 0f, Vector2.Zero, 4f, SpriteEffects.None, 1f);
            }
        }

        // 拦截输入操作
        public override void receiveLeftClick(int x, int y, bool playSound = true) { }
        public override void receiveKeyPress(Microsoft.Xna.Framework.Input.Keys key) { }
        public override bool overrideSnappyMenuCursorMovementBan() => true;
    }
}