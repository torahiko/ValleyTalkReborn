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
    /// 对话/输入文本框：支持自适应字阶、CustomFontManager 原生接入、精准折行、滚动及光标定位。
    /// 内置羊皮纸风格底槽与聚焦高亮外框。
    /// </summary>
    public class DialogueTextInputBox : IKeyboardSubscriber
    {
        public delegate void TextBoxEvent(DialogueTextInputBox sender);
        public event TextBoxEvent OnSubmit;

        ///////////////////////////////////////////////////////////////////
        // 基础属性与字体设置
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

        /// <summary>
        /// 是否由文本框自身绘制底板槽与聚焦光晕外框（默认开启，解决在部分菜单中背景消失的问题）
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

        private const int CounterPadding = 8;
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
            return h > 0 ? (float)Math.Ceiling(h) + 2f : 24f;
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

            // 允许 Enter 键换行
            if (inputChar == '\r' || inputChar == '\n')
            {
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
                InsertText("\n");
                return;
            }

            Keys key = (Keys)command;
            if (key == Keys.Enter)
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

            // 1. 如果开启了绘制外框，渲染和 BioEditor 同款的羊皮纸槽与发光边框
            if (DrawFrame)
            {
                // 底板槽纹理
                Color slotColor = Selected ? new Color(255, 250, 235) : new Color(238, 222, 198) * 0.92f;
                IClickableMenu.drawTextureBox(spriteBatch, Game1.mouseCursors, new Rectangle(403, 383, 6, 6),
                    bx, by, bw, bh, slotColor, 2f, false);

                // 聚焦时光晕
                if (Selected)
                {
                    IClickableMenu.drawTextureBox(spriteBatch, Game1.mouseCursors, new Rectangle(432, 439, 9, 9),
                        bx - 1, by - 1, bw + 2, bh + 2, Color.Gold * 0.45f, 2f, false);
                }
            }

            float lineHeight = GetLineHeight();

            // 内边距规划：左右各留 12px，上下留 10px
            int padX = 12;
            int padY = 10;

            var textArea = new Rectangle(
                bx + padX,
                by + padY,
                Math.Max(1, bw - padX * 2),
                Math.Max((int)lineHeight, bh - padY * 2)
            );

            _visibleLineCount = Math.Max(1, (int)(textArea.Height / lineHeight));

            // 检测是否需要滚动
            int totalVisualLines = GetTotalVisualLines();
            _needsScrolling = totalVisualLines > _visibleLineCount;

            // 绘制文本
            if (!string.IsNullOrEmpty(Text))
            {
                DrawWrappedTextWithScroll(spriteBatch, Text, textArea, TextColor);
            }

            if (_needsScrolling)
            {
                DrawScrollArrows(spriteBatch);
            }

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
            Color color)
        {
            var lines = GetWrappedLines(text);
            float lineHeight = GetLineHeight();
            float y = area.Y;

            int totalVisualLines = Math.Max(lines.Count, GetCaretLine() + 1);
            EnsureCaretVisible(totalVisualLines);

            for (int i = _scrollOffset; i < lines.Count && (i - _scrollOffset) < _visibleLineCount; i++)
            {
                string lineStr = lines[i];
                if (UseCustomFont)
                {
                    CustomFontManager.DrawString(spriteBatch, lineStr, new Vector2(area.X, y), color, CustomFontSize);
                }
                else
                {
                    spriteBatch.DrawString(Font, lineStr, new Vector2(area.X, y), color, 0f, Vector2.Zero, EffectiveScale, SpriteEffects.None, 1f);
                }
                y += lineHeight;
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
            float lineHeight = GetLineHeight();
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
                    caretX = textArea.X + (int)MeasureString(linesBeforeCaret[caretLine]).X;
                }
            }

            int caretY = (int)(textArea.Y + visibleCaretLine * lineHeight);
            int caretHeight = Math.Max(6, (int)lineHeight - 4);
            var caretRect = new Rectangle(caretX, caretY + 2, 2, caretHeight);

            // 闪烁光标
            if ((int)(Game1.currentGameTime.TotalGameTime.TotalMilliseconds / 500) % 2 == 0)
            {
                spriteBatch.Draw(Game1.staminaRect, caretRect, TextColor);
            }
        }

        ///////////////////////////////////////////////////////////////////
        // 核心换行算法
        ///////////////////////////////////////////////////////////////////

        private int GetWrapWidth()
        {
            // 基础内边距消耗 24px；当需要滚动时，留出右侧 28px 给滚动箭头
            int reserved = _needsScrolling ? 48 : 24;
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