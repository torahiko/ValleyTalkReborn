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
    /// 对话/输入文本框：支持自适应字阶、字数限制、滚动、光标定位及剪贴板。
    /// </summary>
    public class DialogueTextInputBox : IKeyboardSubscriber
    {
        public delegate void TextBoxEvent(DialogueTextInputBox sender);
        public event TextBoxEvent OnSubmit;

        ///////////////////////////////////////////////////////////////////
        // 基础属性与自适应缩放
        ///////////////////////////////////////////////////////////////////

        public Vector2 Position { get; set; }

        private Vector2 _extent;
        public Vector2 Extent
        {
            get => _extent;
            set
            {
                if (_extent != value)
                {
                    _extent = value;
                    _isTextDirty = true;
                }
            }
        }

        public Color TextColor { get; set; } = Game1.textColor;

        private SpriteFont _font = Game1.dialogueFont;
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

        /// <summary>
        /// 手动指定缩放比例。若为 null，则自动根据 Extent.Y 与字体高度进行自适应匹配。
        /// </summary>
        public float? CustomScale { get; set; } = null;

        /// <summary>
        /// 最终生效的文字与光标缩放比例（自动计算或手动指定）。
        /// </summary>
        public float EffectiveScale
        {
            get
            {
                if (CustomScale.HasValue)
                    return CustomScale.Value;

                float rawLineHeight = Font.MeasureString("Ag").Y;
                if (rawLineHeight <= 0f)
                    return 1f;

                // 当文本框高度不足以容纳两行以上文字时（属于浅型单行输入框，如 50px、60px），
                // 自动将文字压缩至占框高的 60% 左右，留出充裕的上下呼吸感和边框内边距。
                if (Extent.Y < rawLineHeight * 1.65f)
                {
                    float targetHeight = Math.Min(Extent.Y * 0.60f, Extent.Y - 14f);
                    float autoScale = targetHeight / rawLineHeight;
                    return Math.Clamp(autoScale, 0.35f, 1f);
                }

                return 1f;
            }
        }

        public bool Selected { get; set; } = true;

        public string Text { get; private set; } = "";

        ///////////////////////////////////////////////////////////////////
        // 配置
        ///////////////////////////////////////////////////////////////////

        private readonly int _characterLimit;
        private readonly int _warningThreshold;

        ///////////////////////////////////////////////////////////////////
        // 内部状态
        ///////////////////////////////////////////////////////////////////

        private int _caretPosition = 0;

        // 滚动状态
        private int _scrollOffset = 0;
        private int _visibleLineCount = 0;
        private readonly ClickableTextureComponent _scrollUpArrow;
        private readonly ClickableTextureComponent _scrollDownArrow;
        private bool _needsScrolling = false;

        // 按键长按连发
        private const double InitialRepeatDelay = 0.5;
        private const double RepeatInterval = 0.03;
        private Keys _currentSpecialKey = Keys.None;
        private double _keyPressTime = 0;
        private int _repeatCount = 0;

        // 换行缓存
        private List<string> _cachedWrappedLines;
        private bool _isTextDirty = true;

        private const int CounterPadding = 8;
        private DateTime _lastSubmitTime = DateTime.MinValue;

        ///////////////////////////////////////////////////////////////////
        // 构造函数
        ///////////////////////////////////////////////////////////////////

        public DialogueTextInputBox() : this(200, 180)
        {
        }

        public DialogueTextInputBox(int characterLimit, int warningThreshold = 0)
        {
            _characterLimit = characterLimit;
            _warningThreshold = warningThreshold > 0 ? warningThreshold : (int)(characterLimit * 0.9);

            _scrollUpArrow = new ClickableTextureComponent(
                new Rectangle(0, 0, 32, 32),
                Game1.mouseCursors,
                new Rectangle(421, 459, 11, 12),
                3f
            );

            _scrollDownArrow = new ClickableTextureComponent(
                new Rectangle(0, 0, 32, 32),
                Game1.mouseCursors,
                new Rectangle(421, 472, 11, 12),
                3f
            );
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
                _scrollOffset = Math.Max(0, _scrollOffset - 3);
                Game1.playSound("shwip");
            }
            else if (direction < 0)
            {
                if (_scrollOffset < maxVisibleStart)
                {
                    _scrollOffset = Math.Min(maxVisibleStart, _scrollOffset + 3);
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
                InvokeSubmit();
                return;
            }

            if (char.IsControl(inputChar) && inputChar != ' ')
                return;

            if (Text.Length < _characterLimit)
            {
                Text = Text.Insert(_caretPosition, inputChar.ToString());
                _caretPosition++;
                _isTextDirty = true;
            }
            else
            {
                Game1.playSound("select");
            }
        }

        public void RecieveTextInput(string text)
        {
            if (string.IsNullOrEmpty(text))
                return;

            foreach (char c in text)
            {
                RecieveTextInput(c);
            }
        }

        public void RecieveCommandInput(char command)
        {
            if (command == '\r' || command == '\n')
            {
                InvokeSubmit();
                return;
            }

            Keys key = (Keys)command;
            if (key == Keys.Enter)
            {
                InvokeSubmit();
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
                    InvokeSubmit();
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
            // 绘制底框
            IClickableMenu.drawTextureBox(
                spriteBatch,
                (int)Position.X,
                (int)Position.Y,
                (int)Extent.X,
                (int)Extent.Y,
                Color.White
            );

            float scale = EffectiveScale;
            int lineHeight = (int)Math.Ceiling(Font.MeasureString("A").Y * scale);

            // 🌟 动态内边距：单行浅框自动在 Y 轴居中；多行框保留合理的顶部边距
            int padY;
            if (Extent.Y <= lineHeight * 2.1f)
            {
                padY = Math.Max(2, (int)((Extent.Y - lineHeight) / 2f));
            }
            else
            {
                padY = 14;
            }

            int padX = 14;
            var textArea = new Rectangle(
                (int)Position.X + padX,
                (int)Position.Y + padY,
                Math.Max(1, (int)Extent.X - padX * 2),
                Math.Max(lineHeight, (int)Extent.Y - padY * 2)
            );

            _visibleLineCount = Math.Max(1, textArea.Height / lineHeight);

            // 绘制文本
            if (!string.IsNullOrEmpty(Text))
            {
                DrawWrappedTextWithScroll(spriteBatch, Text, textArea, Font, TextColor);
            }

            // 滚动检测
            int totalVisualLines = GetTotalVisualLines();
            _needsScrolling = totalVisualLines > _visibleLineCount;

            if (_needsScrolling)
            {
                DrawScrollArrows(spriteBatch);
            }

            // 绘制字数指示器
            DrawCounter(spriteBatch);

            // 绘制光标
            if (Selected)
            {
                DrawCaret(spriteBatch, textArea);
            }
        }

        private void DrawWrappedTextWithScroll(
            SpriteBatch spriteBatch,
            string text,
            Rectangle area,
            SpriteFont font,
            Color color)
        {
            var lines = GetWrappedLines(text);
            float scale = EffectiveScale;
            int lineHeight = (int)Math.Ceiling(font.MeasureString("A").Y * scale);
            int y = area.Y;

            int totalVisualLines = Math.Max(lines.Count, GetCaretLine() + 1);
            EnsureCaretVisible(totalVisualLines);

            for (int i = _scrollOffset; i < lines.Count && (i - _scrollOffset) < _visibleLineCount; i++)
            {
                spriteBatch.DrawString(font, lines[i], new Vector2(area.X, y), color, 0f, Vector2.Zero, scale, SpriteEffects.None, 1f);
                y += lineHeight;
            }
        }

        private void DrawScrollArrows(SpriteBatch spriteBatch)
        {
            int arrowX = (int)Position.X + (int)Extent.X - 40;
            int upArrowY = (int)Position.Y + 4;
            int downArrowY = (int)Position.Y + (int)Extent.Y - 36;

            _scrollUpArrow.bounds = new Rectangle(arrowX, upArrowY, 32, 32);
            _scrollDownArrow.bounds = new Rectangle(arrowX, downArrowY, 32, 32);

            int totalVisualLines = GetTotalVisualLines();
            int maxVisibleStart = Math.Max(0, totalVisualLines - _visibleLineCount);

            if (_scrollOffset > 0)
                _scrollUpArrow.draw(spriteBatch);

            if (_scrollOffset < maxVisibleStart)
                _scrollDownArrow.draw(spriteBatch);
        }

        private void DrawCounter(SpriteBatch spriteBatch)
        {
            string counterText = $"{Text.Length}/{_characterLimit}";
            var counterSize = Game1.smallFont.MeasureString(counterText);

            float counterX = Position.X + Extent.X - counterSize.X - CounterPadding;

            // 浅型单行框中，计数器也自动随文字垂直居中
            float counterY = Extent.Y < 70
                ? Position.Y + (Extent.Y - counterSize.Y) / 2f
                : Position.Y + Extent.Y - counterSize.Y - CounterPadding;

            Color counterColor;
            if (Text.Length >= _characterLimit)
                counterColor = Color.Red;
            else if (Text.Length >= _warningThreshold)
                counterColor = Color.Orange;
            else
                counterColor = Color.Gray;

            spriteBatch.DrawString(Game1.smallFont, counterText, new Vector2(counterX, counterY), counterColor);
        }

        private void DrawCaret(SpriteBatch spriteBatch, Rectangle textArea)
        {
            float scale = EffectiveScale;
            int lineHeight = (int)Math.Ceiling(Font.MeasureString("A").Y * scale);
            int caretLine = GetCaretLine();
            int visibleCaretLine = caretLine - _scrollOffset;

            if (visibleCaretLine < 0 || visibleCaretLine >= _visibleLineCount)
                return;

            int wrapWidth = GetWrapWidth();
            string textBeforeCaret = Text.Substring(0, _caretPosition);

            int caretX = textArea.X;

            if (textBeforeCaret.Length > 0 && textBeforeCaret[textBeforeCaret.Length - 1] != '\n')
            {
                var linesBeforeCaret = WrapTextByPixelWidth(textBeforeCaret, wrapWidth, Font);

                if (caretLine < linesBeforeCaret.Count)
                {
                    caretX = textArea.X + (int)(Font.MeasureString(linesBeforeCaret[caretLine]).X * scale);
                }
            }

            int caretY = textArea.Y + visibleCaretLine * lineHeight;
            int caretHeight = Math.Max(4, lineHeight - 2);
            var caretRect = new Rectangle(caretX, caretY + 1, 2, caretHeight);

            spriteBatch.Draw(Game1.staminaRect, caretRect, TextColor);
        }

        ///////////////////////////////////////////////////////////////////
        // 换行与光标定位辅助
        ///////////////////////////////////////////////////////////////////

        private int GetWrapWidth()
        {
            return Math.Max(1, (int)Extent.X - 56);
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
                _cachedWrappedLines = WrapTextByPixelWidth(text ?? "", GetWrapWidth(), Font);
                _isTextDirty = false;
            }

            return _cachedWrappedLines;
        }

        private List<string> WrapTextByPixelWidth(string text, int maxWidth, SpriteFont font)
        {
            var lines = new List<string>();

            if (string.IsNullOrEmpty(text))
                return lines;

            maxWidth = Math.Max(1, maxWidth);
            float scale = EffectiveScale;

            var currentLine = new StringBuilder();
            float currentLineWidth = 0f;

            foreach (char c in text)
            {
                if (c == '\r')
                    continue;

                if (c == '\n')
                {
                    lines.Add(currentLine.ToString());
                    currentLine.Clear();
                    currentLineWidth = 0f;
                    continue;
                }

                float charWidth = font.MeasureString(c.ToString()).X * scale;

                if (currentLineWidth + charWidth > maxWidth && currentLine.Length > 0)
                {
                    lines.Add(currentLine.ToString());
                    currentLine.Clear();
                    currentLineWidth = 0f;
                }

                currentLine.Append(c);
                currentLineWidth += charWidth;
            }

            if (currentLine.Length > 0)
            {
                lines.Add(currentLine.ToString());
            }

            return lines;
        }

        private int GetCaretLine()
        {
            if (_caretPosition < 0)
                _caretPosition = 0;

            if (_caretPosition > Text.Length)
                _caretPosition = Text.Length;

            if (Text.Length == 0)
                return 0;

            int wrapWidth = GetWrapWidth();
            string textBeforeCaret = Text.Substring(0, _caretPosition);

            if (textBeforeCaret.Length == 0)
                return 0;

            var linesBeforeCaret = WrapTextByPixelWidth(textBeforeCaret, wrapWidth, Font);

            if (textBeforeCaret[textBeforeCaret.Length - 1] == '\n')
            {
                return linesBeforeCaret.Count;
            }

            int line = Math.Max(0, linesBeforeCaret.Count - 1);

            if (_caretPosition < Text.Length && linesBeforeCaret.Count > 0)
            {
                var fullTextLines = GetWrappedLines(Text);

                if (line < fullTextLines.Count - 1)
                {
                    float scale = EffectiveScale;
                    float lastLineWidth = Font.MeasureString(linesBeforeCaret[line]).X * scale;
                    float nextCharWidth = Font.MeasureString(Text[_caretPosition].ToString()).X * scale;

                    if (lastLineWidth + nextCharWidth > wrapWidth)
                    {
                        return line + 1;
                    }
                }
            }

            return line;
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
        // 按键操作辅助
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