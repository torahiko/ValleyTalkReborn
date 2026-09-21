using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewValley;
using StardewValley.Menus;
using System;
using System.Collections.Generic;
using System.Text;

namespace ValleytalkReborn
{
    /// <summary>
    /// 对话/输入文本框：支持自适应字阶、CustomFontManager 原生接入、精准折行、平滑拖拽滚动条、光标定位及右下角字数指示器。
    /// 内置羊皮纸风格底槽与聚焦高亮外框。（已进行像素级渲染防虚化校准）
    /// </summary>
    public class DialogueTextInputBox : IKeyboardSubscriber
    {
        public delegate void TextBoxEvent(DialogueTextInputBox sender);
        public event TextBoxEvent OnSubmit;

        ///////////////////////////////////////////////////////////////////
        // 基础属性与字体设置
        ///////////////////////////////////////////////////////////////////

        private Vector2 _position;
        public Vector2 Position
        {
            get => _position;
            set => _position = new Vector2(MathF.Round(value.X), MathF.Round(value.Y));
        }

        private Vector2 _extent;
        public Vector2 Extent
        {
            get => _extent;
            set
            {
                Vector2 rounded = new Vector2(MathF.Round(value.X), MathF.Round(value.Y));
                if (_extent != rounded)
                {
                    _extent = rounded;
                    _isTextDirty = true;
                }
            }
        }

        public Color TextColor { get; set; } = Game1.textColor;

        /// <summary>
        /// 是否允许换行符（为 false 时拦截回车键录入，杜绝发送瞬间闪现换行）
        /// </summary>
        public bool AllowNewlines { get; set; } = true;

        /// <summary>
        /// 是否显示右下角字符数指示器（如 0/300）
        /// </summary>
        public bool ShowCharacterCount { get; set; } = true;

        /// <summary>
        /// 是否由文本框自身绘制底板槽与聚焦光晕外框
        /// </summary>
        public bool DrawFrame { get; set; } = true;

        /// <summary>
        /// 是否使用 CustomFontManager 进行平滑排版与绘制
        /// </summary>
        public bool UseCustomFont { get; set; } = true;

        /// <summary>
        /// CustomFontManager 的渲染字号
        /// </summary>
        public float CustomFontSize { get; set; } = 18f;

        /// <summary>
        /// 右下角指示器字号（放大一圈，提升辨识度）
        /// </summary>
        public float CounterFontSize { get; set; } = 19f;

        private SpriteFont _font = Game1.smallFont;
        public SpriteFont Font
        {
            get => _font;
            set
            {
                if (_font != value)
                {
                    _font = value;
                    _isTextDirty = true;
                }
            }
        }

        public float? CustomScale { get; set; } = null;

        public float EffectiveScale
        {
            get
            {
                if (CustomScale.HasValue)
                    return CustomScale.Value;
                return 1f;
            }
        }

        public bool Selected { get; set; } = true;
        public string Text { get; private set; } = "";

        ///////////////////////////////////////////////////////////////////
        // 配置与内部状态
        ///////////////////////////////////////////////////////////////////

        private readonly int _characterLimit;
        private readonly int _warningThreshold;

        private int _caretPosition = 0;

        // 滚动与滚动条状态
        private int _scrollOffset = 0;
        private int _visibleLineCount = 0;
        private bool _needsScrolling = false;
        private bool _needsEnsureCaretVisible = false;

        private bool _isDraggingScrollbar = false;
        private int _dragGrabOffsetY = 0;
        private bool _isHoveringThumb = false;

        // 按键长按连发
        private const double InitialRepeatDelay = 0.45;
        private const double RepeatInterval = 0.035;
        private Keys _currentSpecialKey = Keys.None;
        private double _keyPressTime = 0;
        private int _repeatCount = 0;

        // 换行缓存
        private List<string> _cachedWrappedLines;
        private bool _isTextDirty = true;

        private const int CounterPadding = 10;
        private DateTime _lastSubmitTime = DateTime.MinValue;

        ///////////////////////////////////////////////////////////////////
        // 构造函数
        ///////////////////////////////////////////////////////////////////

        public DialogueTextInputBox() : this(2000, 1800)
        {
        }

        public DialogueTextInputBox(int characterLimit, int warningThreshold = 0)
        {
            _characterLimit = characterLimit;
            _warningThreshold = warningThreshold > 0 ? warningThreshold : (int)(characterLimit * 0.9);
        }

        ///////////////////////////////////////////////////////////////////
        // 文本测宽适配（统一中枢）
        ///////////////////////////////////////////////////////////////////

        private Vector2 MeasureString(string str)
        {
            if (string.IsNullOrEmpty(str))
                return Vector2.Zero;

            if (UseCustomFont)
            {
                return CustomFontManager.MeasureString(str, CustomFontSize);
            }

            return Font.MeasureString(str) * EffectiveScale;
        }

        private float GetLineHeight()
        {
            float h = MeasureString("测试Ag").Y;
            return h > 0 ? MathF.Ceiling(h) + 2f : 24f;
        }

        ///////////////////////////////////////////////////////////////////
        // 滚动条几何计算
        ///////////////////////////////////////////////////////////////////

        private Rectangle GetScrollTrackBounds()
        {
            int bx = (int)Position.X;
            int by = (int)Position.Y;
            int bw = (int)Extent.X;
            int bh = (int)Extent.Y;

            int trackWidth = 8;
            int trackX = bx + bw - 16;
            int trackY = by + 10;
            int trackHeight = Math.Max(20, bh - 20);

            return new Rectangle(trackX, trackY, trackWidth, trackHeight);
        }

        private int GetThumbHeight(int trackHeight, int totalVisualLines)
        {
            if (totalVisualLines <= 0) return trackHeight;
            float ratio = (float)_visibleLineCount / totalVisualLines;
            return Math.Clamp((int)(trackHeight * ratio), 22, trackHeight);
        }

        private Rectangle GetScrollThumbBounds()
        {
            var track = GetScrollTrackBounds();
            int totalVisualLines = GetTotalVisualLines();
            int maxScroll = Math.Max(0, totalVisualLines - _visibleLineCount);
            int thumbHeight = GetThumbHeight(track.Height, totalVisualLines);

            if (maxScroll <= 0)
                return new Rectangle(track.X, track.Y, track.Width, track.Height);

            int availableTrack = track.Height - thumbHeight;
            int thumbY = track.Y + (int)MathF.Round(availableTrack * ((float)_scrollOffset / maxScroll));

            return new Rectangle(track.X, thumbY, track.Width, thumbHeight);
        }

        private void UpdateScrollFromThumbPosition(int thumbY)
        {
            int totalVisualLines = GetTotalVisualLines();
            int maxScroll = Math.Max(0, totalVisualLines - _visibleLineCount);
            if (maxScroll <= 0) return;

            var track = GetScrollTrackBounds();
            int thumbHeight = GetThumbHeight(track.Height, totalVisualLines);
            int availableTrack = track.Height - thumbHeight;

            if (availableTrack > 0)
            {
                float pct = (float)(thumbY - track.Y) / availableTrack;
                pct = Math.Clamp(pct, 0f, 1f);
                int newOffset = (int)MathF.Round(pct * maxScroll);
                if (newOffset != _scrollOffset)
                {
                    _scrollOffset = newOffset;
                    _needsEnsureCaretVisible = false;
                }
            }
        }

        ///////////////////////////////////////////////////////////////////
        // 辅助方法
        ///////////////////////////////////////////////////////////////////

        public bool ContainsPoint(float x, float y)
        {
            return x >= Position.X && y >= Position.Y &&
                   x <= Position.X + Extent.X && y <= Position.Y + Extent.Y;
        }

        public void SetText(string text)
        {
            Text = NormalizeLineEndings(text ?? "");

            if (Text.Length > _characterLimit)
            {
                Text = Text.Substring(0, _characterLimit);
            }

            _isTextDirty = true;
            _caretPosition = Text.Length;
            _needsEnsureCaretVisible = true;
        }

        public void InvalidateLayout()
        {
            _isTextDirty = true;
        }

        ///////////////////////////////////////////////////////////////////
        // 输入事件处理
        ///////////////////////////////////////////////////////////////////

        public bool ReceiveLeftClick(int x, int y)
        {
            if (!_needsScrolling)
                return false;

            var track = GetScrollTrackBounds();
            // 扩展判定区域，方便鼠标精准抓取
            var hitArea = new Rectangle(track.X - 6, track.Y, track.Width + 12, track.Height);

            if (hitArea.Contains(x, y))
            {
                var thumb = GetScrollThumbBounds();
                _needsEnsureCaretVisible = false;

                if (thumb.Contains(x, y))
                {
                    // 抓取滑块开始拖拽
                    _isDraggingScrollbar = true;
                    _dragGrabOffsetY = y - thumb.Y;
                }
                else
                {
                    // 点击滑轨空白处：直接定位
                    int thumbHeight = thumb.Height;
                    int availableTrack = track.Height - thumbHeight;

                    if (availableTrack > 0)
                    {
                        int targetThumbY = y - thumbHeight / 2;
                        int totalVisualLines = GetTotalVisualLines();
                        int maxScroll = Math.Max(0, totalVisualLines - _visibleLineCount);

                        float pct = (float)(targetThumbY - track.Y) / availableTrack;
                        pct = Math.Clamp(pct, 0f, 1f);
                        _scrollOffset = (int)MathF.Round(pct * maxScroll);
                        _dragGrabOffsetY = thumbHeight / 2;
                    }
                    _isDraggingScrollbar = true;
                    Game1.playSound("shwip");
                }
                return true;
            }

            return false;
        }

        public bool LeftClickHeld(int x, int y)
        {
            if (_isDraggingScrollbar)
            {
                UpdateScrollFromThumbPosition(y - _dragGrabOffsetY);
                return true;
            }
            return false;
        }

        public bool ReleaseLeftClick(int x, int y)
        {
            if (_isDraggingScrollbar)
            {
                _isDraggingScrollbar = false;
                return true;
            }
            return false;
        }

        public void ReceiveScrollWheel(int direction)
        {
            if (!_needsScrolling)
                return;

            _needsEnsureCaretVisible = false;
            int totalVisualLines = GetTotalVisualLines();
            int maxVisibleStart = Math.Max(0, totalVisualLines - _visibleLineCount);

            if (direction > 0 && _scrollOffset > 0)
            {
                _scrollOffset = Math.Max(0, _scrollOffset - 2);
                Game1.playSound("shwip");
            }
            else if (direction < 0)
            {
                if (_scrollOffset < maxVisibleStart)
                {
                    _scrollOffset = Math.Min(maxVisibleStart, _scrollOffset + 2);
                    Game1.playSound("shwip");
                }
            }
        }

        public void RecieveTextInput(char inputChar)
        {
            if (inputChar == '\b')
            {
                ExecuteBackspace();
                return;
            }

            if (inputChar == '\r' || inputChar == '\n')
            {
                if (AllowNewlines)
                    InsertText("\n");
                return;
            }

            if (char.IsControl(inputChar) && inputChar != ' ')
                return;

            InsertText(inputChar.ToString());
        }

        public void RecieveTextInput(string text)
        {
            if (string.IsNullOrEmpty(text))
                return;

            InsertText(text);
        }

        private void InsertText(string str)
        {
            if (!AllowNewlines && (str.Contains('\r') || str.Contains('\n')))
            {
                str = str.Replace("\r", "").Replace("\n", "");
                if (string.IsNullOrEmpty(str))
                    return;
            }

            if (Text.Length + str.Length <= _characterLimit)
            {
                Text = Text.Insert(_caretPosition, str);
                _caretPosition += str.Length;
                _isTextDirty = true;
                _needsEnsureCaretVisible = true;
            }
            else
            {
                Game1.playSound("select");
            }
        }

        public void RecieveCommandInput(char command)
        {
            if (command == '\r' || command == '\n')
            {
                if (AllowNewlines)
                    InsertText("\n");
                return;
            }

            Keys key = (Keys)command;
            if (key == Keys.Enter && AllowNewlines)
            {
                InsertText("\n");
            }
        }

        public void RecieveSpecialInput(Keys key)
        {
            if (IsControlKeyDown())
            {
                switch (key)
                {
                    case Keys.C:
                        CopyAllToClipboard();
                        return;

                    case Keys.V:
                        PasteFromClipboard();
                        return;

                    case Keys.X:
                        CutAllToClipboard();
                        return;

                    case Keys.A:
                        _caretPosition = Text.Length;
                        _needsEnsureCaretVisible = true;
                        return;

                    case Keys.Enter:
                        InvokeSubmit();
                        return;
                }
            }

            _needsEnsureCaretVisible = true;

            switch (key)
            {
                case Keys.Left:
                    if (_caretPosition > 0)
                        _caretPosition--;
                    StartKeyRepeat(key);
                    break;

                case Keys.Right:
                    if (_caretPosition < Text.Length)
                        _caretPosition++;
                    StartKeyRepeat(key);
                    break;

                case Keys.Home:
                    _caretPosition = 0;
                    StartKeyRepeat(key);
                    break;

                case Keys.End:
                    _caretPosition = Text.Length;
                    StartKeyRepeat(key);
                    break;

                case Keys.Delete:
                    ExecuteDelete();
                    StartKeyRepeat(key);
                    break;

                case Keys.Back:
                    ExecuteBackspace();
                    StartKeyRepeat(key);
                    break;

                case Keys.Enter:
                    if (AllowNewlines)
                        InsertText("\n");
                    break;
            }
        }

        public void Update(GameTime gameTime)
        {
            // 1. 拖拽追踪与鼠标状态检测
            int mouseX = Game1.getMouseX();
            int mouseY = Game1.getMouseY();
            var mouseState = Game1.input.GetMouseState();

            if (_isDraggingScrollbar)
            {
                if (mouseState.LeftButton == ButtonState.Released)
                {
                    _isDraggingScrollbar = false;
                }
                else
                {
                    UpdateScrollFromThumbPosition(mouseY - _dragGrabOffsetY);
                }
            }

            _isHoveringThumb = _needsScrolling && GetScrollThumbBounds().Contains(mouseX, mouseY);

            // 2. 键盘连发检测
            if (_currentSpecialKey == Keys.None)
                return;

            var currentKeyState = Game1.input.GetKeyboardState();

            if (!currentKeyState.IsKeyDown(_currentSpecialKey))
            {
                _currentSpecialKey = Keys.None;
                _keyPressTime = 0;
                _repeatCount = 0;
                return;
            }

            _keyPressTime += gameTime.ElapsedGameTime.TotalSeconds;

            if (_keyPressTime >= InitialRepeatDelay)
            {
                var repeatsNeeded = (int)((_keyPressTime - InitialRepeatDelay) / RepeatInterval) + 1;

                while (_repeatCount < repeatsNeeded)
                {
                    ExecuteSpecialKeyAction(_currentSpecialKey);
                    _repeatCount++;
                }
            }
        }

        ///////////////////////////////////////////////////////////////////
        // 绘制逻辑
        ///////////////////////////////////////////////////////////////////

        public void Draw(SpriteBatch spriteBatch)
        {
            int bx = (int)Position.X;
            int by = (int)Position.Y;
            int bw = (int)Extent.X;
            int bh = (int)Extent.Y;

            // 1. 底板槽与聚焦光晕外框
            if (DrawFrame)
            {
                const float frameScale = 2f;
                const int fillInset = 4;

                Color innerBgColor = Selected
                    ? new Color(255, 252, 245)
                    : new Color(245, 240, 230);

                spriteBatch.Draw(
                    Game1.staminaRect,
                    new Rectangle(bx + fillInset, by + fillInset, bw - fillInset * 2, bh - fillInset * 2),
                    innerBgColor);

                if (Selected)
                {
                    IClickableMenu.drawTextureBox(
                        spriteBatch,
                        Game1.mouseCursors,
                        new Rectangle(432, 439, 9, 9),
                        bx, by, bw, bh,
                        Color.White,
                        frameScale,
                        false);
                }
                else
                {
                    IClickableMenu.drawTextureBox(
                        spriteBatch,
                        Game1.mouseCursors,
                        new Rectangle(432, 439, 9, 9),
                        bx, by, bw, bh,
                        new Color(228, 212, 190),
                        frameScale,
                        false);
                }
            }

            int lineHeight = (int)MathF.Ceiling(GetLineHeight());
            int padX = 14;
            int padY = 12;

            int bottomReserved = ShowCharacterCount ? 24 : padY;

            var textArea = new Rectangle(
                bx + padX,
                by + padY,
                Math.Max(1, bw - padX * 2),
                Math.Max(lineHeight, bh - padY - bottomReserved)
            );

            _visibleLineCount = Math.Max(1, textArea.Height / lineHeight);

            int totalVisualLines = GetTotalVisualLines();
            _needsScrolling = totalVisualLines > _visibleLineCount;

            // 绘制文本
            if (!string.IsNullOrEmpty(Text))
            {
                DrawWrappedTextWithScroll(spriteBatch, Text, textArea, TextColor);
            }

            // 绘制新版滚动条
            if (_needsScrolling)
            {
                DrawScrollBar(spriteBatch);
            }

            // 绘制光标
            if (Selected)
            {
                DrawCaret(spriteBatch, textArea);
            }

            // 绘制右下角字符数指示器
            if (ShowCharacterCount && _characterLimit > 0)
            {
                DrawCharacterCounter(spriteBatch, bx, by, bw, bh);
            }
        }

        private void DrawWrappedTextWithScroll(
            SpriteBatch spriteBatch,
            string text,
            Rectangle area,
            Color color)
        {
            var lines = GetWrappedLines(text);
            int lineHeight = (int)MathF.Ceiling(GetLineHeight());
            int currentY = area.Y;

            int totalVisualLines = Math.Max(lines.Count, GetCaretLine() + 1);
            EnsureCaretVisible(totalVisualLines);

            for (int i = _scrollOffset; i < lines.Count && (i - _scrollOffset) < _visibleLineCount; i++)
            {
                string lineStr = lines[i];
                Vector2 drawPos = new Vector2(area.X, currentY);

                if (UseCustomFont)
                {
                    CustomFontManager.DrawString(spriteBatch, lineStr, drawPos, color, CustomFontSize);
                }
                else
                {
                    spriteBatch.DrawString(Font, lineStr, drawPos, color, 0f, Vector2.Zero, EffectiveScale, SpriteEffects.None, 1f);
                }
                currentY += lineHeight;
            }
        }

        /// <summary>
        /// 绘制像素风木质下拉滑轨与滑块
        /// </summary>
        private void DrawScrollBar(SpriteBatch spriteBatch)
        {
            var track = GetScrollTrackBounds();
            var thumb = GetScrollThumbBounds();

            // 1. 绘制滑轨底槽（内嵌微暗羊皮质感）
            Color trackBg = new Color(225, 218, 205);
            Color trackBorder = new Color(195, 185, 170);
            DrawBorderedRect(spriteBatch, track, trackBg, trackBorder);

            // 2. 绘制滑块（星露谷原版木质暖棕风格）
            Color thumbBg = (_isDraggingScrollbar || _isHoveringThumb)
                ? new Color(190, 130, 85)   // 抓取/悬停高亮
                : new Color(160, 100, 65);  // 平常木色

            Color thumbBorder = new Color(95, 55, 30);
            DrawBorderedRect(spriteBatch, thumb, thumbBg, thumbBorder);

            // 3. 滑块内高光（立体微凸起质感）
            if (thumb.Width > 2 && thumb.Height > 2)
            {
                Color highlight = new Color(225, 175, 135);
                spriteBatch.Draw(Game1.staminaRect, new Rectangle(thumb.X + 1, thumb.Y + 1, thumb.Width - 2, 1), highlight);
                spriteBatch.Draw(Game1.staminaRect, new Rectangle(thumb.X + 1, thumb.Y + 1, 1, thumb.Height - 2), highlight);
            }
        }

        private void DrawBorderedRect(SpriteBatch spriteBatch, Rectangle rect, Color fillColor, Color borderColor)
        {
            // 填充背景
            spriteBatch.Draw(Game1.staminaRect, rect, fillColor);

            // 1px 像素边框
            spriteBatch.Draw(Game1.staminaRect, new Rectangle(rect.X, rect.Y, rect.Width, 1), borderColor);
            spriteBatch.Draw(Game1.staminaRect, new Rectangle(rect.X, rect.Bottom - 1, rect.Width, 1), borderColor);
            spriteBatch.Draw(Game1.staminaRect, new Rectangle(rect.X, rect.Y, 1, rect.Height), borderColor);
            spriteBatch.Draw(Game1.staminaRect, new Rectangle(rect.Right - 1, rect.Y, 1, rect.Height), borderColor);
        }

        private void DrawCaret(SpriteBatch spriteBatch, Rectangle textArea)
        {
            int lineHeight = (int)MathF.Ceiling(GetLineHeight());
            int caretLine = GetCaretLine();
            int visibleCaretLine = caretLine - _scrollOffset;

            if (visibleCaretLine < 0 || visibleCaretLine >= _visibleLineCount)
                return;

            string textBeforeCaret = Text.Substring(0, _caretPosition);
            int caretX = textArea.X;

            if (textBeforeCaret.Length > 0 && textBeforeCaret[textBeforeCaret.Length - 1] != '\n')
            {
                var linesBeforeCaret = WrapTextByPixelWidth(textBeforeCaret, GetWrapWidth());
                if (caretLine < linesBeforeCaret.Count)
                {
                    caretX = textArea.X + (int)MathF.Round(MeasureString(linesBeforeCaret[caretLine]).X);
                }
            }

            int caretY = textArea.Y + visibleCaretLine * lineHeight;
            int caretHeight = Math.Max(6, lineHeight - 4);
            var caretRect = new Rectangle(caretX, caretY + 2, 2, caretHeight);

            // 闪烁光标
            if ((int)(Game1.currentGameTime.TotalGameTime.TotalMilliseconds / 500) % 2 == 0)
            {
                spriteBatch.Draw(Game1.staminaRect, caretRect, TextColor);
            }
        }

        private void DrawCharacterCounter(SpriteBatch spriteBatch, int bx, int by, int bw, int bh)
        {
            string counterText = $"{Text.Length}/{_characterLimit}";

            Vector2 counterSize = UseCustomFont
                ? CustomFontManager.MeasureString(counterText, CounterFontSize)
                : Font.MeasureString(counterText) * (EffectiveScale * 1.2f);

            Color counterColor;
            if (Text.Length >= _characterLimit)
            {
                counterColor = new Color(225, 75, 60);
            }
            else if (Text.Length >= _warningThreshold)
            {
                counterColor = new Color(235, 140, 40);
            }
            else
            {
                counterColor = new Color(130, 105, 80, 200);
            }

            float rightOffset = _needsScrolling ? 28f : (CounterPadding + 4);
            int posX = (int)MathF.Round(bx + bw - counterSize.X - rightOffset);
            int posY = (int)MathF.Round(by + bh - counterSize.Y - CounterPadding + 2);
            Vector2 counterPos = new Vector2(posX, posY);

            if (UseCustomFont)
            {
                CustomFontManager.DrawString(spriteBatch, counterText, counterPos, counterColor, CounterFontSize);
            }
            else
            {
                spriteBatch.DrawString(Font, counterText, counterPos, counterColor, 0f, Vector2.Zero, EffectiveScale * 1.2f, SpriteEffects.None, 1f);
            }
        }

        ///////////////////////////////////////////////////////////////////
        // 核心折行算法
        ///////////////////////////////////////////////////////////////////

        private int GetWrapWidth()
        {
            int reserved = _needsScrolling ? 34 : 28;
            return Math.Max(10, (int)Extent.X - reserved);
        }

        private int GetTotalVisualLines()
        {
            var lines = GetWrappedLines(Text);
            return Math.Max(lines.Count, GetCaretLine() + 1);
        }

        private List<string> GetWrappedLines(string text)
        {
            if (_isTextDirty || _cachedWrappedLines == null)
            {
                _cachedWrappedLines = WrapTextByPixelWidth(text ?? "", GetWrapWidth());
                _isTextDirty = false;
            }

            return _cachedWrappedLines;
        }

        private List<string> WrapTextByPixelWidth(string text, int maxWidth)
        {
            var lines = new List<string>();
            if (string.IsNullOrEmpty(text))
                return lines;

            maxWidth = Math.Max(20, maxWidth);
            string[] rawParagraphs = text.Split('\n');

            foreach (var paragraph in rawParagraphs)
            {
                if (paragraph.Length == 0)
                {
                    lines.Add("");
                    continue;
                }

                int startIndex = 0;
                while (startIndex < paragraph.Length)
                {
                    string remaining = paragraph.Substring(startIndex);
                    if (MeasureString(remaining).X <= maxWidth)
                    {
                        lines.Add(remaining);
                        break;
                    }

                    int low = 1;
                    int high = remaining.Length;
                    int bestFit = 1;

                    while (low <= high)
                    {
                        int mid = (low + high) / 2;
                        string sub = remaining.Substring(0, mid);
                        if (MeasureString(sub).X <= maxWidth) 
                        {
                            bestFit = mid;
                            low = mid + 1;
                        }
                        else
                        {
                            high = mid - 1;
                        }
                    }

                    lines.Add(remaining.Substring(0, bestFit));
                    startIndex += bestFit;
                }
            }

            return lines;
        }

        private int GetCaretLine()
        {
            if (_caretPosition <= 0)
                return 0;

            if (_caretPosition > Text.Length)
                _caretPosition = Text.Length;

            string textBeforeCaret = Text.Substring(0, _caretPosition);
            var linesBeforeCaret = WrapTextByPixelWidth(textBeforeCaret, GetWrapWidth());

            if (textBeforeCaret.EndsWith('\n'))
            {
                return linesBeforeCaret.Count;
            }

            return Math.Max(0, linesBeforeCaret.Count - 1);
        }

        private void EnsureCaretVisible(int totalVisualLines)
        {
            int maxScroll = Math.Max(0, totalVisualLines - _visibleLineCount);

            if (totalVisualLines <= _visibleLineCount)
            {
                _scrollOffset = 0;
                _needsEnsureCaretVisible = false;
                return;
            }

            // 如果当前不是因为键入/光标移动而触发的重绘，仅做上下限保护，防止打架
            if (!_needsEnsureCaretVisible)
            {
                _scrollOffset = Math.Clamp(_scrollOffset, 0, maxScroll);
                return;
            }

            int caretLine = GetCaretLine();

            if (caretLine < _scrollOffset)
            {
                _scrollOffset = caretLine;
            }
            else if (caretLine >= _scrollOffset + _visibleLineCount)
            {
                _scrollOffset = caretLine - _visibleLineCount + 1;
            }

            _scrollOffset = Math.Clamp(_scrollOffset, 0, maxScroll);
            _needsEnsureCaretVisible = false;
        }

        ///////////////////////////////////////////////////////////////////
        // 按键辅助与剪贴板
        ///////////////////////////////////////////////////////////////////

        private void StartKeyRepeat(Keys key)
        {
            if (_currentSpecialKey != key)
            {
                _currentSpecialKey = key;
                _keyPressTime = 0;
                _repeatCount = 0;
            }
        }

        private void ExecuteSpecialKeyAction(Keys key)
        {
            _needsEnsureCaretVisible = true;
            switch (key)
            {
                case Keys.Left:
                    if (_caretPosition > 0)
                        _caretPosition--;
                    break;

                case Keys.Right:
                    if (_caretPosition < Text.Length)
                        _caretPosition++;
                    break;

                case Keys.Home:
                    _caretPosition = 0;
                    break;

                case Keys.End:
                    _caretPosition = Text.Length;
                    break;

                case Keys.Delete:
                    ExecuteDelete();
                    break;

                case Keys.Back:
                    ExecuteBackspace();
                    break;
            }
        }

        private void ExecuteDelete()
        {
            if (_caretPosition < Text.Length)
            {
                Text = Text.Remove(_caretPosition, 1);
                _isTextDirty = true;
                _needsEnsureCaretVisible = true;
            }
        }

        private void ExecuteBackspace()
        {
            if (_caretPosition > 0 && Text.Length > 0)
            {
                Text = Text.Remove(_caretPosition - 1, 1);
                _caretPosition--;
                _isTextDirty = true;
                _needsEnsureCaretVisible = true;
            }
        }

        private void InvokeSubmit()
        {
            var now = DateTime.UtcNow;
            if ((now - _lastSubmitTime).TotalMilliseconds < 100)
                return;

            _lastSubmitTime = now;
            OnSubmit?.Invoke(this);
        }

        private string NormalizeLineEndings(string text)
        {
            if (string.IsNullOrEmpty(text))
                return "";

            return text.Replace("\r\n", "\n").Replace('\r', '\n');
        }

        private string SanitizeClipboardText(string text)
        {
            if (string.IsNullOrEmpty(text))
                return "";

            text = NormalizeLineEndings(text);

            var sb = new StringBuilder(text.Length);
            foreach (char c in text)
            {
                if (c == '\n' || !char.IsControl(c))
                {
                    sb.Append(c);
                }
            }

            return sb.ToString();
        }

        public static bool IsControlKeyDown()
        {
            var state = Keyboard.GetState();
            return state.IsKeyDown(Keys.LeftControl) || state.IsKeyDown(Keys.RightControl);
        }

        private void CopyAllToClipboard()
        {
            try
            {
                if (!string.IsNullOrEmpty(Text))
                {
                    TextCopy.ClipboardService.SetText(Text);
                }
            }
            catch (Exception ex)
            {
                Log.Debug($"Clipboard copy failed: {ex.Message}");
            }
        }

        private void PasteFromClipboard()
        {
            try
            {
                string clipboardText = TextCopy.ClipboardService.GetText();
                clipboardText = SanitizeClipboardText(clipboardText);

                if (string.IsNullOrEmpty(clipboardText))
                    return;

                int remainingCapacity = _characterLimit - Text.Length;
                if (remainingCapacity <= 0)
                    return;

                if (clipboardText.Length > remainingCapacity)
                {
                    clipboardText = clipboardText.Substring(0, remainingCapacity);
                }

                Text = Text.Insert(_caretPosition, clipboardText);
                _caretPosition += clipboardText.Length;
                _isTextDirty = true;
                _needsEnsureCaretVisible = true;
            }
            catch (Exception ex)
            {
                Log.Debug($"Clipboard paste failed: {ex.Message}");
            }
        }

        private void CutAllToClipboard()
        {
            try
            {
                if (string.IsNullOrEmpty(Text))
                    return;

                TextCopy.ClipboardService.SetText(Text);
                Text = "";
                _caretPosition = 0;
                _isTextDirty = true;
                _needsEnsureCaretVisible = true;
            }
            catch (Exception ex)
            {
                Log.Debug($"Clipboard cut failed: {ex.Message}");
            }
        }
    }
}