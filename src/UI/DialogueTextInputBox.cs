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
    /// 对话/输入文本框：支持自适应字阶、CustomFontManager 原生接入、精准折行、滚动、光标定位及右下角字数指示器。
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

        // 滚动状态
        private int _scrollOffset = 0;
        private int _visibleLineCount = 0;
        private readonly ClickableTextureComponent _scrollUpArrow;
        private readonly ClickableTextureComponent _scrollDownArrow;
        private bool _needsScrolling = false;

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

            _scrollUpArrow = new ClickableTextureComponent(
                new Rectangle(0, 0, 24, 24),
                Game1.mouseCursors,
                new Rectangle(421, 459, 11, 12),
                2.2f
            );

            _scrollDownArrow = new ClickableTextureComponent(
                new Rectangle(0, 0, 24, 24),
                Game1.mouseCursors,
                new Rectangle(421, 472, 11, 12),
                2.2f
            );
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

            if (_scrollUpArrow.containsPoint(x, y) && _scrollOffset > 0)
            {
                _scrollOffset--;
                Game1.playSound("shwip");
                return true;
            }

            if (_scrollDownArrow.containsPoint(x, y))
            {
                int totalVisualLines = GetTotalVisualLines();
                int maxVisibleStart = Math.Max(0, totalVisualLines - _visibleLineCount);

                if (_scrollOffset < maxVisibleStart)
                {
                    _scrollOffset++;
                    Game1.playSound("shwip");
                    return true;
                }
            }

            return false;
        }

        public void ReceiveScrollWheel(int direction)
        {
            if (!_needsScrolling)
                return;

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
                        return;

                    case Keys.Enter:
                        InvokeSubmit();
                        return;
                }
            }

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
                Color slotColor = Selected ? new Color(255, 250, 235) : new Color(238, 222, 198) * 0.92f;
                IClickableMenu.drawTextureBox(spriteBatch, Game1.mouseCursors, new Rectangle(403, 383, 6, 6),
                    bx, by, bw, bh, slotColor, 2f, false);

                if (Selected)
                {
                    IClickableMenu.drawTextureBox(spriteBatch, Game1.mouseCursors, new Rectangle(432, 439, 9, 9),
                        bx - 1, by - 1, bw + 2, bh + 2, Color.Gold * 0.45f, 2f, false);
                }
            }

            int lineHeight = (int)MathF.Ceiling(GetLineHeight());

            int padX = 14;
            int padY = 12;

            // 底部预留安全边距，防止文本内容压在右下角大号指示器上
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

            // 绘制滚动箭头
            if (_needsScrolling)
            {
                DrawScrollArrows(spriteBatch);
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

        private void DrawScrollArrows(SpriteBatch spriteBatch)
        {
            int arrowX = (int)Position.X + (int)Extent.X - 28;
            int upArrowY = (int)Position.Y + 8;
            int downArrowY = (int)Position.Y + (int)Extent.Y - 30;

            _scrollUpArrow.bounds = new Rectangle(arrowX, upArrowY, 20, 20);
            _scrollDownArrow.bounds = new Rectangle(arrowX, downArrowY, 20, 20);

            int totalVisualLines = GetTotalVisualLines();
            int maxVisibleStart = Math.Max(0, totalVisualLines - _visibleLineCount);

            if (_scrollOffset > 0)
                _scrollUpArrow.draw(spriteBatch);

            if (_scrollOffset < maxVisibleStart)
                _scrollDownArrow.draw(spriteBatch);
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

        /// <summary>
        /// 绘制右下角字符数指示器（强制整像素对齐）
        /// </summary>
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

            float rightOffset = _needsScrolling ? 36f : (CounterPadding + 4);
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
            int reserved = _needsScrolling ? 48 : 28;
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
            if (totalVisualLines <= _visibleLineCount)
            {
                _scrollOffset = 0;
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

            int maxScroll = Math.Max(0, totalVisualLines - _visibleLineCount);
            _scrollOffset = Math.Max(0, Math.Min(_scrollOffset, maxScroll));
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
            }
        }

        private void ExecuteBackspace()
        {
            if (_caretPosition > 0 && Text.Length > 0)
            {
                Text = Text.Remove(_caretPosition - 1, 1);
                _caretPosition--;
                _isTextDirty = true;
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
            }
            catch (Exception ex)
            {
                Log.Debug($"Clipboard cut failed: {ex.Message}");
            }
        }
    }
}