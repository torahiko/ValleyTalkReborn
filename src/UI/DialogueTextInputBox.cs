using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewValley;
using StardewValley.Menus;
using System;
using System.Collections.Generic;

namespace ValleytalkReborn
{
    /// <summary>
    /// A larger text input box specifically designed for dialogue responses.
    /// Supports character limit, counter display, and scrolling for overflow text.
    /// </summary>
    public class DialogueTextInputBox : IKeyboardSubscriber
    {
        public delegate void TextBoxEvent(DialogueTextInputBox sender);
        public event TextBoxEvent OnSubmit;

        public Vector2 Position { get; set; }
        public Vector2 Extent { get; set; }
        public Color TextColor { get; set; } = Game1.textColor;
        public SpriteFont Font { get; set; } = Game1.dialogueFont;
        public bool Selected { get; set; } = true;
        public string Text { get; private set; } = "";

        // Configurable character limit and warning threshold
        private readonly int _characterLimit;
        private readonly int _warningThreshold;
        private int _caretPosition = 0;
        private readonly Texture2D _backgroundTexture;

        // Scrolling fields
        private int _scrollOffset = 0;
        private int _visibleLineCount = 0;
        private ClickableTextureComponent _scrollUpArrow;
        private ClickableTextureComponent _scrollDownArrow;
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

        // Counter display area
        private const int CounterPadding = 8;

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

            // Create background texture
            _backgroundTexture = new Texture2D(Game1.graphics.GraphicsDevice, 1, 1);
            _backgroundTexture.SetData(new Color[] { Color.White });

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
            Text = text ?? "";
            _isTextDirty = true;
            _caretPosition = Text.Length;
        }

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
                var allLines = GetWrappedLines(Text);
                int maxVisibleStart = Math.Max(0, allLines.Count - _visibleLineCount);
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

            if (direction > 0 && _scrollOffset > 0)
            {
                // Scroll up (move view up = decrease offset)
                _scrollOffset = Math.Max(0, _scrollOffset - 3);
                Game1.playSound("shwip");
            }
            else if (direction < 0)
            {
                // Scroll down (move view down = increase offset)
                var allLines = GetWrappedLines(Text);
                int maxVisibleStart = Math.Max(0, allLines.Count - _visibleLineCount);
                if (_scrollOffset < maxVisibleStart)
                {
                    _scrollOffset = Math.Min(maxVisibleStart, _scrollOffset + 3);
                    Game1.playSound("shwip");
                }
            }
        }

        public void Draw(SpriteBatch spriteBatch)
        {
            // Draw textbox background using the game's standard texture box
            IClickableMenu.drawTextureBox(spriteBatch, (int)Position.X, (int)Position.Y,
                                         (int)Extent.X, (int)Extent.Y, Color.White);

            // Calculate text area with padding
            var textArea = new Rectangle((int)Position.X + 16, (int)Position.Y + 16,
                                       (int)Extent.X - 32, (int)Extent.Y - 32);

            // Calculate visible line count
            var lineHeight = (int)Font.MeasureString("A").Y;
            _visibleLineCount = Math.Max(1, textArea.Height / lineHeight);

            // Draw text with word wrapping and scrolling
            if (!string.IsNullOrEmpty(Text))
            {
                DrawWrappedTextWithScroll(spriteBatch, Text, textArea, Font, TextColor);
            }

            // Determine if scrolling is needed
            var allLines = GetWrappedLines(Text);
            _needsScrolling = allLines.Count > _visibleLineCount;

            // Draw scroll arrows if needed
            if (_needsScrolling)
            {
                DrawScrollArrows(spriteBatch);
            }

            // Draw character counter
            DrawCounter(spriteBatch);

            // Draw caret if selected
            if (Selected)
            {
                DrawCaret(spriteBatch, textArea);
            }
        }

        private void DrawWrappedTextWithScroll(SpriteBatch spriteBatch, string text, Rectangle area, SpriteFont font, Color color)
        {
            var lines = GetWrappedLines(text);
            var lineHeight = (int)font.MeasureString("A").Y;
            var y = area.Y;

            // Auto-scroll to keep caret visible
            EnsureCaretVisible(lines.Count);

            // Draw visible lines starting from scroll offset
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

            // Only draw arrows if they can be used
            if (_scrollOffset > 0)
                _scrollUpArrow.draw(spriteBatch);

            var allLines = GetWrappedLines(Text);
            int maxVisibleStart = Math.Max(0, allLines.Count - _visibleLineCount);
            if (_scrollOffset < maxVisibleStart)
                _scrollDownArrow.draw(spriteBatch);
        }

        private void DrawCounter(SpriteBatch spriteBatch)
        {
            string counterText = $"{Text.Length}/{_characterLimit}";
            var counterSize = Game1.smallFont.MeasureString(counterText);

            // Position: bottom-right of textbox
            float counterX = Position.X + Extent.X - counterSize.X - CounterPadding;
            float counterY = Position.Y + Extent.Y - counterSize.Y - CounterPadding;

            // Determine color based on character count
            Color counterColor;
            if (Text.Length >= _characterLimit)
                counterColor = Color.Red;
            else if (Text.Length >= _warningThreshold)
                counterColor = Color.Orange;
            else
                counterColor = Color.Gray;

            spriteBatch.DrawString(Game1.smallFont, counterText, new Vector2(counterX, counterY), counterColor);
        }

        /// <summary>
        /// Wraps text into lines based on actual pixel width measured by SpriteFont.
        /// </summary>
        private List<string> GetWrappedLines(string text)
        {
            if (_isTextDirty || _cachedWrappedLines == null)
            {
                _cachedWrappedLines = WrapTextByPixelWidth(text, (int)Extent.X - 64, Font);
                _isTextDirty = false;
            }
            return _cachedWrappedLines;
        }

        /// <summary>
        /// Wraps text into lines based on actual pixel width measured by SpriteFont.
        /// Any character (including punctuation) follows the line naturally.
        /// </summary>
        private List<string> WrapTextByPixelWidth(string text, int maxWidth, SpriteFont font)
        {
            var lines = new List<string>();
            if (string.IsNullOrEmpty(text))
                return lines;

            var currentLine = new System.Text.StringBuilder();
            var currentLineWidth = 0f;

            foreach (char c in text)
            {
                // Handle explicit newlines
                if (c == '\n')
                {
                    lines.Add(currentLine.ToString());
                    currentLine.Clear();
                    currentLineWidth = 0f;
                    continue;
                }

                // Measure the actual pixel width of this character
                var charWidth = font.MeasureString(c.ToString()).X;

                // Wrap if adding this character would exceed max width
                if (currentLineWidth + charWidth > maxWidth && currentLine.Length > 0)
                {
                    lines.Add(currentLine.ToString());
                    currentLine.Clear();
                    currentLineWidth = 0f;
                }

                currentLine.Append(c);
                currentLineWidth += charWidth;
            }

            // Add the last line
            if (currentLine.Length > 0)
            {
                lines.Add(currentLine.ToString());
            }

            return lines;
        }

        /// <summary>
        /// Calculates the line index where the caret is positioned.
        /// </summary>
        private int GetCaretLine(List<string> allLines)
        {
            if (_caretPosition < 0) _caretPosition = 0;
            if (_caretPosition > Text.Length) _caretPosition = Text.Length;

            var textBeforeCaret = Text.Substring(0, _caretPosition);
            var linesBeforeCaret = WrapTextByPixelWidth(textBeforeCaret, (int)Extent.X - 64, Font);

            return linesBeforeCaret.Count - 1;
        }

        /// <summary>
        /// Adjusts scroll offset to ensure the caret is visible.
        /// </summary>
        private void EnsureCaretVisible(int totalLines)
        {
            if (totalLines <= _visibleLineCount)
            {
                _scrollOffset = 0;
                return;
            }

            var caretLine = GetCaretLine(GetWrappedLines(Text));

            // If caret is above visible area, scroll up
            if (caretLine < _scrollOffset)
            {
                _scrollOffset = caretLine;
            }
            // If caret is below visible area, scroll down
            else if (caretLine >= _scrollOffset + _visibleLineCount)
            {
                _scrollOffset = caretLine - _visibleLineCount + 1;
            }

            // Clamp scroll offset
            int maxScroll = Math.Max(0, totalLines - _visibleLineCount);
            _scrollOffset = Math.Max(0, Math.Min(_scrollOffset, maxScroll));
        }

        private void DrawCaret(SpriteBatch spriteBatch, Rectangle textArea)
        {
            if (_caretPosition < 0) _caretPosition = 0;
            if (_caretPosition > Text.Length) _caretPosition = Text.Length;

            // Calculate caret position based on actual cursor position in text
            var textBeforeCaret = Text.Substring(0, _caretPosition);
            var lines = WrapTextByPixelWidth(textBeforeCaret, (int)Extent.X - 64, Font);
            var lineHeight = (int)Font.MeasureString("A").Y;

            int caretX, caretY;

            if (lines.Count == 0)
            {
                // No text, caret at start
                caretX = textArea.X;
                caretY = textArea.Y;
            }
            else
            {
                // Caret is at the end of the last line of text before cursor
                var lastLine = lines[lines.Count - 1];
                var lastLineWidth = Font.MeasureString(lastLine).X;

                caretX = textArea.X + (int)lastLineWidth;
                caretY = textArea.Y + (lines.Count - 1) * lineHeight;

                // If we're at the very end and the line is full, move to next line
                if (_caretPosition < Text.Length)
                {
                    var fullTextLines = WrapTextByPixelWidth(Text, (int)Extent.X - 64, Font);
                    if (lines.Count < fullTextLines.Count && lastLineWidth + Font.MeasureString("A").X > textArea.Width)
                    {
                        caretX = textArea.X;
                        caretY += lineHeight;
                    }
                }
            }

            // Adjust caret Y position based on scroll offset
            int visibleCaretLine = (lines.Count - 1) - _scrollOffset;
            if (visibleCaretLine >= 0 && visibleCaretLine < _visibleLineCount)
            {
                caretY = textArea.Y + visibleCaretLine * lineHeight;
                var caretRect = new Rectangle(caretX, caretY, 2, lineHeight);
                spriteBatch.Draw(Game1.staminaRect, caretRect, TextColor);
            }
        }

        public void RecieveTextInput(char inputChar)
        {
            // Handle backspace
            if (inputChar == '\b')
            {
                if (_caretPosition > 0 && Text.Length > 0)
                {
                    Text = Text.Remove(_caretPosition - 1, 1);
                    _caretPosition--;
                    _isTextDirty = true;
                }
                return;
            }

            // Handle Enter - submit the text
            if (inputChar == '\r' || inputChar == '\n')
            {
                OnSubmit?.Invoke(this);
                return;
            }

            // Skip other control characters (but allow space)
            if (char.IsControl(inputChar) && inputChar != ' ')
                return;

            // Insert printable character at cursor position (check character limit)
            if (Text.Length < _characterLimit)
            {
                Text = Text.Insert(_caretPosition, inputChar.ToString());
                _caretPosition++;
                _isTextDirty = true;
            }
            else
            {
                // Play sound effect when character limit is reached
                Game1.playSound("select");
            }
        }

        public void RecieveTextInput(string text)
        {
            foreach (char c in text)
            {
                RecieveTextInput(c);
            }
        }

        public void RecieveCommandInput(char command)
        {
            Keys key = (Keys)command;
            switch (key)
            {
                case Keys.Enter:
                    OnSubmit?.Invoke(this);
                    break;
            }
        }

        public void RecieveSpecialInput(Keys key)
        {
            // Handle Ctrl+key clipboard shortcuts
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
                    OnSubmit?.Invoke(this);
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

            // Detect key release: reset all state when key is no longer held
            if (!currentKeyState.IsKeyDown(_currentSpecialKey))
            {
                _currentSpecialKey = Keys.None;
                _keyPressTime = 0;
                _repeatCount = 0;
                return;
            }

            // Accumulate hold time
            _keyPressTime += (float)gameTime.ElapsedGameTime.TotalSeconds;

            // After initial delay, repeat at fixed interval
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

        private void StartKeyRepeat(Keys key)
        {
            // Only re-initialize if this is a different key or first press
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
            }catch (Exception ex)
            {
                Log.Debug($"Clipboard cut failed: {ex.Message}");
            }

        /// <summary>
        }
    }

}
