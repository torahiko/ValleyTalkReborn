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
    /// 对话/输入文本框：支持自适应字阶、CustomFontManager 原生接入、精准折行、平滑拖拽滚动条、光标定位、文本选区及右下角字数指示器。
    /// 内置羊皮纸风格底槽与聚焦高亮外框。（已进行像素级渲染防虚化校准）
    /// 选区状态为控件实例私有字段（Memory 作用域），无持久化/多人同步。
    /// </summary>
    public class DialogueTextInputBox : IKeyboardSubscriber
    {
        public delegate void TextBoxEvent(DialogueTextInputBox sender);
        public event TextBoxEvent OnSubmit;

        

        public string PlaceholderText { get; set; } = "";
        public Color PlaceholderColor { get; set; } = new Color(158, 138, 118); // 默认 TextMuted
        public Rectangle TextAreaBounds => GetTextArea(); // 对外暴露准确的文本区域几何
        
        ///////////////////////////////////////////////////////////////////
        // 折行缓存结构（替代旧版 List<string>）
        ///////////////////////////////////////////////////////////////////

        private struct VisualLine
        {
            public string Text;
            public int StartIndex;  // 在完整 Text 中的起始字符索引
            public int Length;      // 本行字符长度
        }

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

        private bool _selected = true;
        public bool Selected
        {
            get => _selected;
            set
            {
                _selected = value;
                // 失去焦点时重置点击连发计数，避免跨框多击误判
                if (!value)
                {
                    _clickCount = 0;
                    _isSelectingText = false;
                }
            }
        }

        public string Text { get; private set; } = "";

        ///////////////////////////////////////////////////////////////////
        // 选区状态（Memory 作用域：控件实例私有字段，无持久化）
        ///////////////////////////////////////////////////////////////////

        private int _caretPosition = 0;
        private int _selectionStart = 0;
        private int _selectionEnd = 0;
        private bool _isSelectingText = false;

        // 多击识别（双击选词 / 三击选段）
        private const double MultiClickThresholdMs = 400.0;
        private double _lastClickTime = -1000.0;
        private int _clickCount = 0;

        // 撤销状态（Memory 作用域：控件实例私有字段，随实例销毁，无持久化）
        private enum UndoRunKind { None, InsertChar, DeleteChar }
        private readonly struct UndoEntry
        {
            public readonly string Text;
            public readonly int Caret;
            public readonly int SelStart;
            public readonly int SelEnd;
            public UndoEntry(string text, int caret, int selStart, int selEnd)
            {
                Text = text; Caret = caret; SelStart = selStart; SelEnd = selEnd;
            }
        }
        private readonly List<UndoEntry> _undoStack = new();
        private UndoRunKind _lastUndoRunKind = UndoRunKind.None;
        private double _lastUndoRunMs = 0;
        private const double UndoCoalesceWindowMs = 600.0;
        private const int MaxUndoEntries = 100;

        /// <summary>是否存在选区</summary>
        public bool HasSelection => _selectionStart != _selectionEnd;

        /// <summary>选区较小端索引</summary>
        public int SelectionStart => Math.Min(_selectionStart, _selectionEnd);

        /// <summary>选区较大端索引</summary>
        public int SelectionEnd => Math.Max(_selectionStart, _selectionEnd);

        /// <summary>选区长度</summary>
        public int SelectionLength => Math.Abs(_selectionEnd - _selectionStart);

        /// <summary>选中文本（无选区返回空串）</summary>
        public string SelectedText => HasSelection ? Text.Substring(SelectionStart, SelectionLength) : "";

        ///////////////////////////////////////////////////////////////////
        // 配置与内部状态
        ///////////////////////////////////////////////////////////////////

        private readonly int _characterLimit;
        private readonly int _warningThreshold;

        // 滚动与滚动条状态
        private int _scrollOffset = 0;
        private int _visibleLineCount = 0;
        private bool _needsScrolling = false;
        private bool _lastNeedsScrolling = false;
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

        // 折行缓存
        private List<VisualLine> _cachedVisualLines;
        private bool _isTextDirty = true;

        private const int CounterPadding = 10;
        private DateTime _lastSubmitTime = DateTime.MinValue;

        // 选区高亮色（天青色半透明）
        private static readonly Color SelectionHighlightColor = new Color(120, 205, 255) * 0.42f;

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
        // 文本区矩形（绘制与命中共用）
        ///////////////////////////////////////////////////////////////////

        private Rectangle GetTextArea()
        {
            int bx = (int)Position.X;
            int by = (int)Position.Y;
            int bw = (int)Extent.X;
            int bh = (int)Extent.Y;

            int padX = 14;
            int padY = 12;
            int bottomReserved = ShowCharacterCount ? 24 : padY;

            return new Rectangle(
                bx + padX,
                by + padY,
                Math.Max(1, bw - padX * 2),
                Math.Max((int)MathF.Ceiling(GetLineHeight()), bh - padY - bottomReserved));
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
            string normalized = NormalizeLineEndings(text ?? "");
            if (string.Equals(normalized, Text, StringComparison.Ordinal))
                return;

            Text = normalized;

            if (Text.Length > _characterLimit)
            {
                Text = Text.Substring(0, _characterLimit);
            }

            _isTextDirty = true;
            _caretPosition = Text.Length;
            _selectionStart = _caretPosition;
            _selectionEnd = _caretPosition;
            _needsEnsureCaretVisible = true;

            _undoStack.Clear();
            _lastUndoRunKind = UndoRunKind.None;
        }

        /// <summary>流式追加：仅限主线程调用；剥离 \r；超 characterLimit 部分截断丢弃；
        /// 光标钉尾并请求滚动跟随；不写入撤销栈（流式文本无撤销语义）。</summary>
        public void AppendStreamingText(string str)
        {
            if (string.IsNullOrEmpty(str))
                return;

            str = str.Replace("\r", "");
            int space = _characterLimit - Text.Length;
            if (space <= 0)
                return;

            if (str.Length > space)
                str = str.Substring(0, space);

            Text += str;
            _caretPosition = _selectionStart = _selectionEnd = Text.Length;
            _isTextDirty = true;
            _needsEnsureCaretVisible = true;
        }

        public void InvalidateLayout()
        {
            _isTextDirty = true;
        }

        /// <summary>清除选区（双端点归拢至当前光标位置）</summary>
        public void ClearSelection()
        {
            _selectionStart = _caretPosition;
            _selectionEnd = _caretPosition;
        }

        /// <summary>删除选区内容，光标移至选区起始端</summary>
        public void DeleteSelection()
        {
            if (!HasSelection)
                return;

            int start = SelectionStart;
            int len = SelectionLength;
            PushUndo(UndoRunKind.None);
            Text = Text.Remove(start, len);
            _caretPosition = start;
            _selectionStart = start;
            _selectionEnd = start;
            _isTextDirty = true;
            _needsEnsureCaretVisible = true;
        }

        /// <summary>
        /// 由屏幕坐标反推字符索引（光标落位）。
        /// 空文本 → 0；行号越界 → Text.Length；relX≤0 → 行首；逐字 best-diff 兜底。
        /// </summary>
        public int GetCaretIndexFromPosition(float x, float y)
        {
            if (string.IsNullOrEmpty(Text))
                return 0;

            var lines = GetVisualLines();
            if (lines.Count == 0)
                return 0;

            var area = GetTextArea();
            int lineHeight = (int)MathF.Ceiling(GetLineHeight());

            float relX = x - area.X;
            int relY = (int)(y - area.Y);

            int lineIdx = (lineHeight > 0) ? (relY / lineHeight + _scrollOffset) : _scrollOffset;
            if (lineIdx < 0)
                lineIdx = 0;
            if (lineIdx >= lines.Count)
                return Text.Length;

            var line = lines[lineIdx];
            if (relX <= 0)
                return line.StartIndex;

            // 逐字 best-diff 定位
            string lineText = line.Text;
            int bestOffset = lineText.Length;
            float bestDiff = float.MaxValue;
            for (int i = 0; i <= lineText.Length; i++)
            {
                float w = MeasureString(lineText.Substring(0, i)).X;
                float diff = MathF.Abs(w - relX);
                if (diff < bestDiff)
                {
                    bestDiff = diff;
                    bestOffset = i;
                }
            }
            return line.StartIndex + bestOffset;
        }

        ///////////////////////////////////////////////////////////////////
        // 输入事件处理
        ///////////////////////////////////////////////////////////////////

        public bool ReceiveLeftClick(int x, int y)
        {
            // 1. 滚动条命中（仅在需要滚动时）
            if (_needsScrolling)
            {
                var track = GetScrollTrackBounds();
                var hitArea = new Rectangle(track.X - 6, track.Y, track.Width + 12, track.Height);

                if (hitArea.Contains(x, y))
                {
                    var thumb = GetScrollThumbBounds();
                    _needsEnsureCaretVisible = false;

                    if (thumb.Contains(x, y))
                    {
                        _isDraggingScrollbar = true;
                        _dragGrabOffsetY = y - thumb.Y;
                    }
                    else
                    {
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
            }

            // 2. 文本区（含框内非文本区）点击 → 光标落位 + 多击选区
            if (ContainsPoint(x, y))
            {
                double now = Game1.currentGameTime.TotalGameTime.TotalMilliseconds;
                if (now - _lastClickTime < MultiClickThresholdMs)
                    _clickCount++;
                else
                    _clickCount = 1;
                _lastClickTime = now;

                int idx = GetCaretIndexFromPosition(x, y);

                if (_clickCount >= 3)
                    SelectParagraphAt(idx);
                else if (_clickCount == 2)
                    SelectWordAt(idx);
                else
                {
                    _caretPosition = idx;
                    _selectionStart = idx;
                    _selectionEnd = idx;
                }
                _isSelectingText = true;
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

            if (_isSelectingText)
            {
                if (ContainsPoint(x, y))
                {
                    int idx = GetCaretIndexFromPosition(x, y);
                    _caretPosition = idx;
                    _selectionEnd = idx;
                }
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

            if (_isSelectingText)
                _isSelectingText = false;

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
            // 有选区时先删除选区，再于选区起始处插入
            if (HasSelection)
                DeleteSelection();

            if (!AllowNewlines && (str.Contains('\r') || str.Contains('\n')))
            {
                str = str.Replace("\r", "").Replace("\n", "");
                if (string.IsNullOrEmpty(str))
                    return;
            }

            if (Text.Length + str.Length <= _characterLimit)
            {
                bool isSingleCharInsert = str.Length == 1 && str != "\n";
                PushUndo(isSingleCharInsert ? UndoRunKind.InsertChar : UndoRunKind.None);
                Text = Text.Insert(_caretPosition, str);
                _caretPosition += str.Length;
                _selectionStart = _caretPosition;
                _selectionEnd = _caretPosition;
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
                        if (HasSelection)
                            CopySelectionToClipboard();
                        else
                            CopyAllToClipboard();
                        return;

                    case Keys.V:
                        PasteFromClipboard();
                        return;

                    case Keys.X:
                        if (HasSelection)
                            CutSelectionToClipboard();
                        else
                            CutAllToClipboard();
                        return;

                    case Keys.A:
                        _selectionStart = 0;
                        _selectionEnd = Text.Length;
                        _caretPosition = Text.Length;
                        _needsEnsureCaretVisible = true;
                        return;

                    case Keys.Z:
                        Undo();
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
                    ClearSelection();
                    StartKeyRepeat(key);
                    break;

                case Keys.Right:
                    if (_caretPosition < Text.Length)
                        _caretPosition++;
                    ClearSelection();
                    StartKeyRepeat(key);
                    break;

                case Keys.Home:
                    _caretPosition = 0;
                    ClearSelection();
                    StartKeyRepeat(key);
                    break;

                case Keys.End:
                    _caretPosition = Text.Length;
                    ClearSelection();
                    StartKeyRepeat(key);
                    break;

                case Keys.Up:
                    MoveCaretUp();
                    StartKeyRepeat(key);
                    break;

                case Keys.Down:
                    MoveCaretDown();
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

            if (_isSelectingText && mouseState.LeftButton == ButtonState.Released)
                _isSelectingText = false;

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

                // 内部温润羊皮纸底色
                Color innerBgColor = Selected
                    ? new Color(255, 252, 245)
                    : new Color(248, 242, 230);

                spriteBatch.Draw(
                    Game1.staminaRect,
                    new Rectangle(bx + fillInset, by + fillInset, bw - fillInset * 2, bh - fillInset * 2),
                    innerBgColor);

                if (Selected)
                {
                    // ★ 聚焦激活：纯正星露谷原版暖橘红木边框 (Color.White 呈现 432 切片的饱满暖橘原色)
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
                    // ★ 未激活：温润暖金木边框（彻底告别灰暗冷调）
                    IClickableMenu.drawTextureBox(
                        spriteBatch,
                        Game1.mouseCursors,
                        new Rectangle(432, 439, 9, 9),
                        bx, by, bw, bh,
                        new Color(225, 195, 155),
                        frameScale,
                        false);
                }
            }

            var textArea = GetTextArea();
            int lineHeight = (int)MathF.Ceiling(GetLineHeight());
            _visibleLineCount = Math.Max(1, textArea.Height / lineHeight);

            int totalVisualLines = GetTotalVisualLines();
            _needsScrolling = totalVisualLines > _visibleLineCount;

            // 契约 E：滚动条出现/消失当帧重排
            if (_needsScrolling != _lastNeedsScrolling)
                _isTextDirty = true;
            _lastNeedsScrolling = _needsScrolling;

            // 绘制文本
                if (!string.IsNullOrEmpty(Text))
            {
                // 选区高亮绘制于文字之下
                if (Selected && HasSelection)
                    DrawSelectionHighlight(spriteBatch, textArea, lineHeight);

                DrawWrappedTextWithScroll(spriteBatch, Text, textArea, TextColor);
            }
    else if (!string.IsNullOrEmpty(PlaceholderText))
    {
        // ★ 文本为空时渲染占位提示符：
        // 聚焦时光标在 textArea.X 闪烁，示例文字向右微移 4px 避免与光标切线重叠；未聚焦时光标隐藏，示例文字顶格
        float phX = textArea.X + (Selected ? 4f : 0f);
        Vector2 drawPos = new Vector2(phX, textArea.Y);

        if (UseCustomFont)
        {
            CustomFontManager.DrawString(spriteBatch, PlaceholderText, drawPos, PlaceholderColor, CustomFontSize);
        }
        else
        {
            spriteBatch.DrawString(Font, PlaceholderText, drawPos, PlaceholderColor, 0f, Vector2.Zero, EffectiveScale, SpriteEffects.None, 1f);
        }
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

        private void DrawSelectionHighlight(SpriteBatch spriteBatch, Rectangle area, int lineHeight)
        {
            int selStart = SelectionStart;
            int selEnd = SelectionEnd;
            if (selEnd <= selStart)
                return;

            var lines = GetVisualLines();

            for (int i = _scrollOffset; i < lines.Count && (i - _scrollOffset) < _visibleLineCount; i++)
            {
                var line = lines[i];
                int lineStart = line.StartIndex;
                int lineEnd = line.StartIndex + line.Length;

                int ovStart = Math.Max(lineStart, selStart);
                int ovEnd = Math.Min(lineEnd, selEnd);
                if (ovEnd <= ovStart)
                    continue;

                int beforeChars = ovStart - lineStart;
                int selChars = ovEnd - ovStart;
                string lineText = line.Text;

                float x = area.X;
                if (beforeChars > 0)
                    x += MeasureString(lineText.Substring(0, beforeChars)).X;

                float w = (selChars > 0)
                    ? MeasureString(lineText.Substring(beforeChars, selChars)).X
                    : 0f;

                int y = area.Y + (i - _scrollOffset) * lineHeight;
                int cx = Math.Max((int)MathF.Round(x), area.X);
                int cw = Math.Min((int)MathF.Round(x) + (int)MathF.Ceiling(Math.Max(1f, w)), area.Right) - cx;
                if (cw <= 0)
                    continue;

                spriteBatch.Draw(Game1.staminaRect, new Rectangle(cx, y, cw, lineHeight), SelectionHighlightColor);
            }
        }

        private void DrawWrappedTextWithScroll(
            SpriteBatch spriteBatch,
            string text,
            Rectangle area,
            Color color)
        {
            var lines = GetVisualLines();
            int lineHeight = (int)MathF.Ceiling(GetLineHeight());
            int currentY = area.Y;

            int totalVisualLines = Math.Max(lines.Count, GetCaretLine() + 1);
            EnsureCaretVisible(totalVisualLines);

            for (int i = _scrollOffset; i < lines.Count && (i - _scrollOffset) < _visibleLineCount; i++)
            {
                string lineStr = lines[i].Text;
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
            var lines = GetVisualLines();
            int caretLine = GetCaretLine();
            int visibleCaretLine = caretLine - _scrollOffset;

            if (visibleCaretLine < 0 || visibleCaretLine >= _visibleLineCount)
                return;

            float caretX = textArea.X;
            if (lines.Count > 0 && caretLine < lines.Count)
            {
                var line = lines[caretLine];
                int offsetInLine = Math.Clamp(_caretPosition - line.StartIndex, 0, line.Length);
                if (offsetInLine > 0)
                    caretX += MeasureString(line.Text.Substring(0, offsetInLine)).X;
            }

            int caretY = textArea.Y + visibleCaretLine * lineHeight;
            int caretHeight = Math.Max(6, lineHeight - 4);
            var caretRect = new Rectangle((int)MathF.Round(caretX), caretY + 2, 2, caretHeight);

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
            var lines = GetVisualLines();
            return Math.Max(lines.Count, GetCaretLine() + 1);
        }

        private List<VisualLine> GetVisualLines()
        {
            if (_isTextDirty || _cachedVisualLines == null)
            {
                _cachedVisualLines = BuildVisualLines(Text ?? "", GetWrapWidth());
                _isTextDirty = false;
            }

            return _cachedVisualLines;
        }

        private List<VisualLine> BuildVisualLines(string text, int maxWidth)
        {
            var lines = new List<VisualLine>();
            if (string.IsNullOrEmpty(text))
                return lines;

            maxWidth = Math.Max(20, maxWidth);
            string[] paragraphs = text.Split('\n');
            int charOffset = 0;

            for (int p = 0; p < paragraphs.Length; p++)
            {
                string paragraph = paragraphs[p];

                if (paragraph.Length == 0)
                {
                    // 空行（换行符本身）
                    lines.Add(new VisualLine { Text = "", StartIndex = charOffset, Length = 0 });
                    charOffset++; // 跳过 \n
                    continue;
                }

                int startIndex = 0;
                while (startIndex < paragraph.Length)
                {
                    string remaining = paragraph.Substring(startIndex);
                    int lineStart = charOffset;

                    if (MeasureString(remaining).X <= maxWidth)
                    {
                        lines.Add(new VisualLine { Text = remaining, StartIndex = lineStart, Length = remaining.Length });
                        charOffset += remaining.Length;
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

                    lines.Add(new VisualLine { Text = remaining.Substring(0, bestFit), StartIndex = lineStart, Length = bestFit });
                    startIndex += bestFit;
                    charOffset += bestFit;
                }

                charOffset++; // 跳过段落后的 \n
            }

            return lines;
        }

        private int GetCaretLine()
        {
            if (_caretPosition <= 0)
                return 0;

            if (_caretPosition > Text.Length)
                _caretPosition = Text.Length;

            var lines = GetVisualLines();
            for (int i = 0; i < lines.Count; i++)
            {
                var line = lines[i];
                int lineEnd = line.StartIndex + line.Length;
                if (_caretPosition >= line.StartIndex && _caretPosition <= lineEnd)
                    return i;
            }

            return Math.Max(0, lines.Count - 1);
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
        // 选区辅助
        ///////////////////////////////////////////////////////////////////

        private void SelectWordAt(int index)
        {
            if (string.IsNullOrEmpty(Text))
                return;

            index = Math.Clamp(index, 0, Text.Length);
            int start = index;
            int end = index;

            while (start > 0 && IsWordChar(Text[start - 1]))
                start--;
            while (end < Text.Length && IsWordChar(Text[end]))
                end++;

            // 若点击处非单词字符（如空白/标点），退化为选中单字符
            if (start == end)
            {
                start = index;
                end = Math.Min(index + 1, Text.Length);
            }

            _selectionStart = start;
            _selectionEnd = end;
            _caretPosition = end;
        }

        private void SelectParagraphAt(int index)
        {
            if (string.IsNullOrEmpty(Text))
                return;

            index = Math.Clamp(index, 0, Text.Length);
            int start = index;
            while (start > 0 && Text[start - 1] != '\n')
                start--;
            int end = index;
            while (end < Text.Length && Text[end] != '\n')
                end++;

            _selectionStart = start;
            _selectionEnd = end;
            _caretPosition = end;
        }

        private static bool IsWordChar(char c)
        {
            // 中英文/标点边界：空白、标点、符号均视为单词边界
            if (char.IsWhiteSpace(c))
                return false;
            if (char.IsPunctuation(c) || char.IsSymbol(c))
                return false;
            return true;
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
                    ClearSelection();
                    break;

                case Keys.Right:
                    if (_caretPosition < Text.Length)
                        _caretPosition++;
                    ClearSelection();
                    break;

                case Keys.Home:
                    _caretPosition = 0;
                    ClearSelection();
                    break;

                case Keys.End:
                    _caretPosition = Text.Length;
                    ClearSelection();
                    break;

                case Keys.Up:
                    MoveCaretUp();
                    break;

                case Keys.Down:
                    MoveCaretDown();
                    break;

                case Keys.Delete:
                    ExecuteDelete();
                    break;

                case Keys.Back:
                    ExecuteBackspace();
                    break;
            }
        }

        ///////////////////////////////////////////////////////////////////
        // 撤销（Ctrl+Z）
        ///////////////////////////////////////////////////////////////////

        /// <summary>在文本变更前压入变更前快照；相同类型且在合并窗口内则合并。</summary>
        private void PushUndo(UndoRunKind kind)
        {
            double now = Game1.currentGameTime?.TotalGameTime.TotalMilliseconds ?? 0;

            bool coalesce = kind != UndoRunKind.None
                && kind == _lastUndoRunKind
                && (now - _lastUndoRunMs) < UndoCoalesceWindowMs
                && _undoStack.Count > 0;

            if (!coalesce)
            {
                _undoStack.Add(new UndoEntry(Text, _caretPosition, _selectionStart, _selectionEnd));
                if (_undoStack.Count > MaxUndoEntries)
                    _undoStack.RemoveAt(0);
            }

            _lastUndoRunKind = kind;
            _lastUndoRunMs = now;
        }

        /// <summary>恢复最近一次快照；栈空则静默返回。</summary>
        private void Undo()
        {
            if (_undoStack.Count == 0)
                return;

            UndoEntry entry = _undoStack[_undoStack.Count - 1];
            _undoStack.RemoveAt(_undoStack.Count - 1);

            Text = entry.Text;
            _caretPosition = Math.Clamp(entry.Caret, 0, Text.Length);
            _selectionStart = Math.Clamp(entry.SelStart, 0, Text.Length);
            _selectionEnd = Math.Clamp(entry.SelEnd, 0, Text.Length);
            _isTextDirty = true;
            _needsEnsureCaretVisible = true;
            _lastUndoRunKind = UndoRunKind.None;
        }

        /// <summary>纵向光标移动（上一行）：基于当前列像素投影在目标行逐缝 best-diff 定位。</summary>
        private void MoveCaretUp()
        {
            var lines = GetVisualLines();
            if (lines.Count == 0)
                return;

            int curLine = GetCaretLine();
            if (curLine <= 0)
            {
                _caretPosition = 0;
                ClearSelection();
                _needsEnsureCaretVisible = true;
                return;
            }

            var cur = lines[curLine];
            int offsetInLine = Math.Clamp(_caretPosition - cur.StartIndex, 0, cur.Length);
            float currentX = MeasureString(cur.Text.Substring(0, offsetInLine)).X;

            var target = lines[curLine - 1];
            int bestOffset = 0;
            float bestDiff = float.MaxValue;
            for (int i = 0; i <= target.Length; i++)
            {
                float w = MeasureString(target.Text.Substring(0, i)).X;
                float diff = MathF.Abs(w - currentX);
                if (diff < bestDiff)
                {
                    bestDiff = diff;
                    bestOffset = i;
                }
            }

            _caretPosition = target.StartIndex + bestOffset;
            ClearSelection();
            _needsEnsureCaretVisible = true;
        }

        /// <summary>纵向光标移动（下一行）：基于当前列像素投影在目标行逐缝 best-diff 定位。</summary>
        private void MoveCaretDown()
        {
            var lines = GetVisualLines();
            if (lines.Count == 0)
                return;

            int curLine = GetCaretLine();
            if (curLine >= lines.Count - 1)
            {
                _caretPosition = Text.Length;
                ClearSelection();
                _needsEnsureCaretVisible = true;
                return;
            }

            var cur = lines[curLine];
            int offsetInLine = Math.Clamp(_caretPosition - cur.StartIndex, 0, cur.Length);
            float currentX = MeasureString(cur.Text.Substring(0, offsetInLine)).X;

            var target = lines[curLine + 1];
            int bestOffset = 0;
            float bestDiff = float.MaxValue;
            for (int i = 0; i <= target.Length; i++)
            {
                float w = MeasureString(target.Text.Substring(0, i)).X;
                float diff = MathF.Abs(w - currentX);
                if (diff < bestDiff)
                {
                    bestDiff = diff;
                    bestOffset = i;
                }
            }

            _caretPosition = target.StartIndex + bestOffset;
            ClearSelection();
            _needsEnsureCaretVisible = true;
        }

        private void ExecuteDelete()
        {
            if (HasSelection)
            {
                DeleteSelection();
                return;
            }

            if (_caretPosition < Text.Length)
            {
                PushUndo(UndoRunKind.DeleteChar);
                Text = Text.Remove(_caretPosition, 1);
                _isTextDirty = true;
                _needsEnsureCaretVisible = true;
            }
        }

        private void ExecuteBackspace()
        {
            if (HasSelection)
            {
                DeleteSelection();
                return;
            }

            if (_caretPosition > 0 && Text.Length > 0)
            {
                PushUndo(UndoRunKind.DeleteChar);
                Text = Text.Remove(_caretPosition - 1, 1);
                _caretPosition--;
                _selectionStart = _caretPosition;
                _selectionEnd = _caretPosition;
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

        private void CopySelectionToClipboard()
        {
            try
            {
                if (HasSelection)
                    TextCopy.ClipboardService.SetText(SelectedText);
            }
            catch (Exception ex)
            {
                Log.Debug($"Clipboard copy selection failed: {ex.Message}");
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

                bool hadSelection = HasSelection;
                if (hadSelection)
                    DeleteSelection();

                int remainingCapacity = _characterLimit - Text.Length;
                if (remainingCapacity <= 0)
                    return;

                if (clipboardText.Length > remainingCapacity)
                {
                    clipboardText = clipboardText.Substring(0, remainingCapacity);
                }

                if (!hadSelection)
                    PushUndo(UndoRunKind.None);
                Text = Text.Insert(_caretPosition, clipboardText);
                _caretPosition += clipboardText.Length;
                _selectionStart = _caretPosition;
                _selectionEnd = _caretPosition;
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
                PushUndo(UndoRunKind.None);
                Text = "";
                _caretPosition = 0;
                _selectionStart = 0;
                _selectionEnd = 0;
                _isTextDirty = true;
                _needsEnsureCaretVisible = true;
            }
            catch (Exception ex)
            {
                Log.Debug($"Clipboard cut failed: {ex.Message}");
            }
        }

        private void CutSelectionToClipboard()
        {
            try
            {
                if (!HasSelection)
                    return;

                TextCopy.ClipboardService.SetText(SelectedText);
                DeleteSelection();
            }
            catch (Exception ex)
            {
                Log.Debug($"Clipboard cut selection failed: {ex.Message}");
            }
        }
    }
}
