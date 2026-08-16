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
    /// A larger text input box specifically designed for dialogue responses.
    /// Supports character limit, counter display, scrolling, caret movement, and clipboard shortcuts.
    /// </summary>
    public class DialogueTextInputBox : IKeyboardSubscriber
    {
        public delegate void TextBoxEvent(DialogueTextInputBox sender);
        public event TextBoxEvent OnSubmit;

        ///////////////////////////////////////////////////////////////////
        // Basic properties
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

        public bool Selected { get; set; } = true;

        public string Text { get; private set; } = "";

        ///////////////////////////////////////////////////////////////////
        // Configuration
        ///////////////////////////////////////////////////////////////////

        private readonly int _characterLimit;
        private readonly int _warningThreshold;

        ///////////////////////////////////////////////////////////////////
        // Internal state
        ///////////////////////////////////////////////////////////////////

        private int _caretPosition = 0;

        // Scrolling fields
        private int _scrollOffset = 0;
        private int _visibleLineCount = 0;
        private readonly ClickableTextureComponent _scrollUpArrow;
        private readonly ClickableTextureComponent _scrollDownArrow;
        private bool _needsScrolling = false;

        // Key repeat configuration
        private const double InitialRepeatDelay = 0.5;
        private const double RepeatInterval = 0.03;
        private Keys _currentSpecialKey = Keys.None;
        private double _keyPressTime = 0;
        private int _repeatCount = 0;

        // Cached wrapped lines to avoid per-frame allocation
        private List<string> _cachedWrappedLines;
        private bool _isTextDirty = true;

        // Counter display padding
        private const int CounterPadding = 8;

        // Prevent duplicate submit events caused by multiple input paths.
        private DateTime _lastSubmitTime = DateTime.MinValue;

        ///////////////////////////////////////////////////////////////////
        // Constructors
        ///////////////////////////////////////////////////////////////////

        /// <summary>
        /// Creates a new DialogueTextInputBox with default settings (200 char limit).
        /// </summary>
        public DialogueTextInputBox() : this(200, 180)
        {
        }

        /// <summary>
        /// Creates a new DialogueTextInputBox with custom character limit.
        /// </summary>
        /// <param name="characterLimit">Maximum number of characters allowed.</param>
        /// <param name="warningThreshold">Character count at which the counter turns orange.</param>
        public DialogueTextInputBox(int characterLimit, int warningThreshold = 0)
        {
            _characterLimit = characterLimit;
            _warningThreshold = warningThreshold > 0 ? warningThreshold : (int)(characterLimit * 0.9);

            // Initialize scroll arrows
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
        // Public helpers
        ///////////////////////////////////////////////////////////////////

        public bool ContainsPoint(float x, float y)
        {
            return x >= Position.X && y >= Position.Y &&
                   x <= Position.X + Extent.X && y <= Position.Y + Extent.Y;
        }

        /// <summary>
        /// Sets the text content directly and moves the caret to the end.
        /// Used for pre-filling the box in edit mode.
        /// </summary>
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

        /// <summary>
        /// Call this after changing layout-related values such as Extent or Font.
        /// </summary>
        public void InvalidateLayout()
        {
            _isTextDirty = true;
        }

        ///////////////////////////////////////////////////////////////////
        // Input handling
        ///////////////////////////////////////////////////////////////////

        /// <summary>
        /// Handles left click for scroll arrows.
        /// Returns true if the click was handled by this text box.
        /// </summary>
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

        /// <summary>
        /// Handles scroll wheel input.
        /// </summary>
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
            // Handle backspace.
            if (inputChar == '\b')
            {
                ExecuteBackspace();
                return;
            }

            // Handle Enter - submit the text.
            if (inputChar == '\r' || inputChar == '\n')
            {
                InvokeSubmit();
                return;
            }

            // Skip other control characters, but allow space.
            if (char.IsControl(inputChar) && inputChar != ' ')
                return;

            // Insert printable character at cursor position.
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
            // Handle Ctrl+key clipboard shortcuts.
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

        /// <summary>
        /// Updates key repeat state. Should be called once per frame.
        /// </summary>
        public void Update(GameTime gameTime)
        {
            if (_currentSpecialKey == Keys.None)
                return;

            var currentKeyState = Game1.input.GetKeyboardState();

            // Detect key release.
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
        // Drawing
        ///////////////////////////////////////////////////////////////////

        public void Draw(SpriteBatch spriteBatch)
        {
            // Draw textbox background using the game's standard texture box.
            IClickableMenu.drawTextureBox(
                spriteBatch,
                (int)Position.X,
                (int)Position.Y,
                (int)Extent.X,
                (int)Extent.Y,
                Color.White
            );

            // Calculate text area with padding.
            var textArea = new Rectangle(
                (int)Position.X + 16,
                (int)Position.Y + 16,
                (int)Extent.X - 32,
                (int)Extent.Y - 32
            );

            // Calculate visible line count.
            int lineHeight = (int)Font.MeasureString("A").Y;
            _visibleLineCount = Math.Max(1, textArea.Height / lineHeight);

            // Draw text with wrapping and scrolling.
            if (!string.IsNullOrEmpty(Text))
            {
                DrawWrappedTextWithScroll(spriteBatch, Text, textArea, Font, TextColor);
            }

            // Determine if scrolling is needed.
            int totalVisualLines = GetTotalVisualLines();
            _needsScrolling = totalVisualLines > _visibleLineCount;

            // Draw scroll arrows if needed.
            if (_needsScrolling)
            {
                DrawScrollArrows(spriteBatch);
            }

            // Draw character counter.
            DrawCounter(spriteBatch);

            // Draw caret if selected.
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

            // 这里 MeasureString 返回的是 Vector2，Y 是 float，需要强制转换成 int
            int lineHeight = (int)font.MeasureString("A").Y;
            int y = area.Y;

            int totalVisualLines = Math.Max(lines.Count, GetCaretLine() + 1);
            EnsureCaretVisible(totalVisualLines);

            for (int i = _scrollOffset; i < lines.Count && (i - _scrollOffset) < _visibleLineCount; i++)
            {
                spriteBatch.DrawString(font, lines[i], new Vector2(area.X, y), color);
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
            float counterY = Position.Y + Extent.Y - counterSize.Y - CounterPadding;

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
            int lineHeight = (int)Font.MeasureString("A").Y;
            int caretLine = GetCaretLine();
            int visibleCaretLine = caretLine - _scrollOffset;

            if (visibleCaretLine < 0 || visibleCaretLine >= _visibleLineCount)
                return;

            int wrapWidth = GetWrapWidth();
            string textBeforeCaret = Text.Substring(0, _caretPosition);

            int caretX = textArea.X;

            // If caret is not immediately after an explicit newline, try to place it after the current line text.
            if (textBeforeCaret.Length > 0 && textBeforeCaret[textBeforeCaret.Length - 1] != '\n')
            {
                var linesBeforeCaret = WrapTextByPixelWidth(textBeforeCaret, wrapWidth, Font);

                if (caretLine < linesBeforeCaret.Count)
                {
                    caretX = textArea.X + (int)Font.MeasureString(linesBeforeCaret[caretLine]).X;
                }
            }

            int caretY = textArea.Y + visibleCaretLine * lineHeight;
            var caretRect = new Rectangle(caretX, caretY, 2, lineHeight);

            spriteBatch.Draw(Game1.staminaRect, caretRect, TextColor);
        }

        ///////////////////////////////////////////////////////////////////
        // Text wrapping / caret helpers
        ///////////////////////////////////////////////////////////////////

        private int GetWrapWidth()
        {
            return Math.Max(1, (int)Extent.X - 64);
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

        /// <summary>
        /// Wraps text into lines based on actual pixel width measured by SpriteFont.
        /// </summary>
        private List<string> WrapTextByPixelWidth(string text, int maxWidth, SpriteFont font)
        {
            var lines = new List<string>();

            if (string.IsNullOrEmpty(text))
                return lines;

            maxWidth = Math.Max(1, maxWidth);

            var currentLine = new StringBuilder();
            float currentLineWidth = 0f;

            foreach (char c in text)
            {
                // Ignore stray carriage returns. Normalized text should use '\n'.
                if (c == '\r')
                    continue;

                // Handle explicit newlines.
                if (c == '\n')
                {
                    lines.Add(currentLine.ToString());
                    currentLine.Clear();
                    currentLineWidth = 0f;
                    continue;
                }

                float charWidth = font.MeasureString(c.ToString()).X;

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

        /// <summary>
        /// Calculates the visual line index where the caret is positioned.
        /// </summary>
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

            // If the caret is immediately after an explicit newline, it belongs to the next line.
            if (textBeforeCaret[textBeforeCaret.Length - 1] == '\n')
            {
                return linesBeforeCaret.Count;
            }

            int line = Math.Max(0, linesBeforeCaret.Count - 1);

            // If the caret sits exactly on an automatic wrap boundary, prefer showing it on the next line.
            if (_caretPosition < Text.Length && linesBeforeCaret.Count > 0)
            {
                var fullTextLines = GetWrappedLines(Text);

                if (line < fullTextLines.Count - 1)
                {
                    float lastLineWidth = Font.MeasureString(linesBeforeCaret[line]).X;
                    float nextCharWidth = Font.MeasureString(Text[_caretPosition].ToString()).X;

                    if (lastLineWidth + nextCharWidth > wrapWidth)
                    {
                        return line + 1;
                    }
                }
            }

            return line;
        }

        /// <summary>
        /// Adjusts scroll offset to ensure the caret is visible.
        /// </summary>
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
        // Editing helpers
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

            // Prevent duplicate submit events from multiple input paths.
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

        ///////////////////////////////////////////////////////////////////
        // Clipboard
        ///////////////////////////////////////////////////////////////////

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