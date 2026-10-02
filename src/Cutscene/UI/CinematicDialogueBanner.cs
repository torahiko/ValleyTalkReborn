using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewValley;
using StardewValley.BellsAndWhistles;

namespace ValleytalkReborn.Cutscene.UI
{
    /// <summary>
    /// 电影级沉浸对白横幅：在宽银幕黑边上方呈现半透明磨砂黑字幕、角色大立绘与打字机动效
    /// </summary>
    public sealed class CinematicDialogueBanner
    {
        private readonly NPC _speaker;
        private readonly string _fullText;
        private readonly string _displayName;
        private readonly Texture2D _portraitTexture;
        private readonly Rectangle _portraitSourceRect;

        private float _revealedLength;
        private float _charsPerSecond = 32f;
        private float _elapsedSeconds;
        private int _lastSoundCharIndex;
        private bool _isCompleted;
        private bool _isTextFullyRevealed;

        private KeyboardState _lastKeyState;
        private MouseState _lastMouseState;

        public bool IsCompleted => _isCompleted;

        public CinematicDialogueBanner(NPC speaker, string text)
        {
            _speaker = speaker ?? throw new ArgumentNullException(nameof(speaker));
            _fullText = text ?? string.Empty;
            _displayName = !string.IsNullOrWhiteSpace(speaker.displayName) ? speaker.displayName : speaker.Name;

            // 动态加载立绘
            try
            {
                if (speaker.Portrait != null && !speaker.Portrait.IsDisposed)
                {
                    _portraitTexture = speaker.Portrait;
                }
                else
                {
                    _portraitTexture = Game1.content.Load<Texture2D>("Portraits\\" + speaker.Name);
                }

                if (_portraitTexture != null)
                {
                    _portraitSourceRect = Game1.getSourceRectForStandardTileSheet(_portraitTexture, 0, 64, 64);
                    if (!_portraitTexture.Bounds.Contains(_portraitSourceRect))
                    {
                        _portraitSourceRect = new Rectangle(0, 0, Math.Min(64, _portraitTexture.Width), Math.Min(64, _portraitTexture.Height));
                    }
                }
            }
            catch
            {
                _portraitTexture = null;
                _portraitSourceRect = Rectangle.Empty;
            }

            _revealedLength = 0f;
            _elapsedSeconds = 0f;
            _lastSoundCharIndex = 0;
            _isCompleted = false;
            _isTextFullyRevealed = false;
            _lastKeyState = Keyboard.GetState();
            _lastMouseState = Mouse.GetState();
        }

        public void Update(GameTime time)
        {
            if (_isCompleted) return;

            float dt = (float)time.ElapsedGameTime.TotalSeconds;
            _elapsedSeconds += dt;

            // 打字机递增
            if (!_isTextFullyRevealed)
            {
                _revealedLength += dt * _charsPerSecond;
                int currentVisibleCount = (int)_revealedLength;

                if (currentVisibleCount >= _fullText.Length)
                {
                    _revealedLength = _fullText.Length;
                    _isTextFullyRevealed = true;
                }
                else if (currentVisibleCount > _lastSoundCharIndex && currentVisibleCount % 2 == 0)
                {
                    char c = _fullText[Math.Min(currentVisibleCount, _fullText.Length - 1)];
                    if (!char.IsWhiteSpace(c) && !char.IsPunctuation(c))
                    {
                        Game1.playSound("dialogueCharacter");
                    }
                    _lastSoundCharIndex = currentVisibleCount;
                }
            }

            // 交互输入处理（空格或鼠标左键）
            var curKeyState = Keyboard.GetState();
            var curMouseState = Mouse.GetState();

            bool spacePressed = curKeyState.IsKeyDown(Keys.Space) && _lastKeyState.IsKeyUp(Keys.Space);
            bool clickPressed = curMouseState.LeftButton == ButtonState.Pressed && _lastMouseState.LeftButton == ButtonState.Released;

            if (spacePressed || clickPressed)
            {
                if (!_isTextFullyRevealed)
                {
                    // 第一次按键：立即展开全文
                    _revealedLength = _fullText.Length;
                    _isTextFullyRevealed = true;
                }
                else
                {
                    // 第二次按键：关闭本句对白进入下一幕
                    _isCompleted = true;
                }
            }

            _lastKeyState = curKeyState;
            _lastMouseState = curMouseState;

            // 自然等待超时（全文展示完毕后停留合理时长自动结束，例如长文本最多 4 秒）
            float readingBufferSeconds = Math.Clamp(_fullText.Length * 0.08f + 1.2f, 2.0f, 4.5f);
            if (_isTextFullyRevealed && _elapsedSeconds >= (_fullText.Length / _charsPerSecond) + readingBufferSeconds)
            {
                _isCompleted = true;
            }
        }

        public void Draw(SpriteBatch b, float blackBarHeight)
        {
            if (_isCompleted) return;

            var vp = Game1.graphics.GraphicsDevice.Viewport;

            int bannerHeight = 150;
            int bannerWidth = Math.Min(vp.Width - 100, 980);
            int bannerX = (vp.Width - bannerWidth) / 2;
            
            // 贴近下黑边上方，留出 12 像素呼吸间距
            int bannerY = vp.Height - (int)Math.Max(blackBarHeight, 50f) - bannerHeight - 12;

            // 1. 半透明磨砂黑背景横幅
            var bgRect = new Rectangle(bannerX, bannerY, bannerWidth, bannerHeight);
            b.Draw(Game1.staminaRect, bgRect, new Color(12, 14, 20, 230));

            // 外边框线（微弱金色渐变质感）
            DrawBorder(b, bgRect, new Color(218, 165, 32, 180), 2);

            // 2. 角色立绘展台
            int portraitBoxSize = 118;
            int portraitX = bannerX + 16;
            int portraitY = bannerY + (bannerHeight - portraitBoxSize) / 2;

            var portraitBoxRect = new Rectangle(portraitX, portraitY, portraitBoxSize, portraitBoxSize);
            b.Draw(Game1.staminaRect, portraitBoxRect, new Color(25, 28, 38, 240));
            DrawBorder(b, portraitBoxRect, new Color(180, 140, 60, 200), 1);

            if (_portraitTexture != null && !_portraitSourceRect.IsEmpty)
            {
                int pad = 4;
                var destRect = new Rectangle(portraitX + pad, portraitY + pad, portraitBoxSize - pad * 2, portraitBoxSize - pad * 2);
                b.Draw(_portraitTexture, destRect, _portraitSourceRect, Color.White);
            }

            // 3. 说话者姓名
            int textLeft = portraitX + portraitBoxSize + 22;
            int textWidth = bannerWidth - (textLeft - bannerX) - 24;

            Vector2 namePos = new Vector2(textLeft, bannerY + 18);
            // 阴影
            b.DrawString(Game1.dialogueFont, _displayName, namePos + new Vector2(2, 2), Color.Black * 0.8f, 0f, Vector2.Zero, 0.9f, SpriteEffects.None, 0.95f);
            // 本文
            b.DrawString(Game1.dialogueFont, _displayName, namePos, new Color(255, 222, 115), 0f, Vector2.Zero, 0.9f, SpriteEffects.None, 0.96f);

            // 4. 当前已打出的台词
            int visibleChars = Math.Min((int)_revealedLength, _fullText.Length);
            string currentVisibleText = _fullText.Substring(0, visibleChars);
            string wrappedText = Game1.parseText(currentVisibleText, Game1.smallFont, textWidth);

            Vector2 textPos = new Vector2(textLeft, bannerY + 54);
            // 文字阴影
            b.DrawString(Game1.smallFont, wrappedText, textPos + new Vector2(1, 1), Color.Black * 0.9f, 0f, Vector2.Zero, 1.05f, SpriteEffects.None, 0.95f);
            // 文字正体
            b.DrawString(Game1.smallFont, wrappedText, textPos, Color.White, 0f, Vector2.Zero, 1.05f, SpriteEffects.None, 0.96f);

            // 5. 翻页 / 继续跳过提示
            if (_isTextFullyRevealed)
            {
                float pulse = (float)Math.Sin(_elapsedSeconds * 6f) * 0.3f + 0.7f;
                string prompt = "▼ 空格 / 点击继续";
                Vector2 promptSize = Game1.tinyFont.MeasureString(prompt);
                Vector2 promptPos = new Vector2(bannerX + bannerWidth - promptSize.X - 16, bannerY + bannerHeight - promptSize.Y - 10);
                
                b.DrawString(Game1.tinyFont, prompt, promptPos, new Color(218, 165, 32) * pulse, 0f, Vector2.Zero, 1f, SpriteEffects.None, 0.97f);
            }
        }

        private static void DrawBorder(SpriteBatch b, Rectangle rect, Color color, int thickness)
        {
            // 上
            b.Draw(Game1.staminaRect, new Rectangle(rect.X, rect.Y, rect.Width, thickness), color);
            // 下
            b.Draw(Game1.staminaRect, new Rectangle(rect.X, rect.Bottom - thickness, rect.Width, thickness), color);
            // 左
            b.Draw(Game1.staminaRect, new Rectangle(rect.X, rect.Y, thickness, rect.Height), color);
            // 右
            b.Draw(Game1.staminaRect, new Rectangle(rect.Right - thickness, rect.Y, thickness, rect.Height), color);
        }
    }
}
