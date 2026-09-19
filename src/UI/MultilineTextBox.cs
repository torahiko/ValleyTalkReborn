using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewValley;
using StardewValley.Menus;

namespace ValleytalkReborn;

/// <summary>
/// 多行文本编辑框：在固定行数限制内编辑以 '\n' 分隔的长文本。
/// 实现 IKeyboardSubscriber，可直接作为 Game1.keyboardDispatcher.Subscriber 使用，
/// 由 SMAPI/游戏主循环通过 RecieveTextInput / RecieveSpecialInput 注入字符与功能键。
/// </summary>
internal sealed class MultilineTextBox : IKeyboardSubscriber
{
    private const int PaddingX = 10;
    private const int PaddingY = 8;

    private readonly int _maxLines;
    private readonly List<string> _lines;
    private int _cursorLine;
    private int _cursorCol;
    private int _scrollOffset;
    private double _cursorTimer;
    private bool _cursorVisible;
    private Rectangle _bounds;

    public Rectangle Bounds => _bounds;

    /// <summary>由 Layout 统一设定绘制区域；禁止在 Draw 内新建实例。</summary>
    public void SetBounds(Rectangle r) => _bounds = r;

    /// <summary>文本内容，以 '\n' 分隔；全部写入，仅受 maxLines 上限防御。</summary>
    public string Text
    {
        get => string.Join("\n", _lines);
        set
        {
            _lines.Clear();
            if (string.IsNullOrEmpty(value))
            {
                _lines.Add(string.Empty);
            }
            else
            {
                var parts = value.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
                foreach (var part in parts)
                {
                    if (_lines.Count >= _maxLines)
                        break;
                    _lines.Add(part);
                }
            }
            if (_lines.Count == 0)
                _lines.Add(string.Empty);
            ClampCursor();
            _scrollOffset = 0;
            EnsureCursorVisible();
            TextChanged?.Invoke();
        }
    }

    public bool Selected { get; set; }

    public event Action TextChanged;

    public MultilineTextBox(Rectangle bounds, int maxLines = 256)
    {
        _bounds = bounds;
        _maxLines = Math.Max(1, maxLines);
        _lines = new List<string> { string.Empty };
        _cursorLine = 0;
        _cursorCol = 0;
        _scrollOffset = 0;
        _cursorTimer = 0;
        _cursorVisible = true;
    }

    /// <summary>光标闪烁计时。</summary>
    public void Update(GameTime time)
    {
        if (!Selected)
            return;
        _cursorTimer += time.ElapsedGameTime.TotalMilliseconds;
        if (_cursorTimer >= 530)
        {
            _cursorTimer = 0;
            _cursorVisible = !_cursorVisible;
        }
    }

    // ── IKeyboardSubscriber 实现 ───────────────────────────────────────

    public void RecieveTextInput(char inputChar)
    {
        if (!Selected)
            return;
        ReceiveChar(inputChar);
    }

    public void RecieveTextInput(string text)
    {
        if (!Selected || string.IsNullOrEmpty(text))
            return;
        foreach (char c in text)
            ReceiveChar(c);
    }

    public void RecieveCommandInput(char command)
    {
        if (!Selected)
            return;
        // 命令输入（如粘贴整段）按字符逐次注入
        ReceiveChar(command);
    }

    public void RecieveSpecialInput(Keys key)
    {
        if (!Selected)
            return;
        ReceiveCommand(key);
    }

    // ── 公开逻辑接口（供菜单直接调用） ─────────────────────────────────

    /// <summary>注入单个字符：'\r'→换行，退格，超行拒绝。</summary>
    public void ReceiveChar(char c)
    {
        if (c == '\r' || c == '\n')
        {
            InsertNewline();
            return;
        }
        if (c == '\b')
        {
            Backspace();
            return;
        }
        if (char.IsControl(c) && c != ' ')
            return;

        if (_lines.Count >= _maxLines && _cursorLine >= _maxLines - 1
            && _cursorCol >= _lines[_lines.Count - 1].Length)
        {
            // 已达内容行数上限且光标在末行末尾：拒绝追加
            return;
        }

        string line = _lines[_cursorLine];
        _lines[_cursorLine] = line.Insert(_cursorCol, c.ToString());
        _cursorCol++;
        EnsureCursorVisible();
        TextChanged?.Invoke();
    }

    /// <summary>功能键：Up/Down 光标移动，左右行内移动。</summary>
    public void ReceiveCommand(Keys key)
    {
        switch (key)
        {
            case Keys.Enter:
                ReceiveChar('\n');
                return;
            case Keys.Up:
                MoveCursor(_cursorLine - 1, _cursorCol);
                break;
            case Keys.Down:
                MoveCursor(_cursorLine + 1, _cursorCol);
                break;
            case Keys.Left:
                if (_cursorCol > 0)
                    _cursorCol--;
                else if (_cursorLine > 0)
                {
                    // 跳到上一行末尾
                    _cursorLine--;
                    _cursorCol = _lines[_cursorLine].Length;
                }
                EnsureCursorVisible();
                break;
            case Keys.Right:
                if (_cursorCol < _lines[_cursorLine].Length)
                    _cursorCol++;
                else if (_cursorLine < _lines.Count - 1)
                {
                    _cursorLine++;
                    _cursorCol = 0;
                }
                EnsureCursorVisible();
                break;
            case Keys.Back:
                Backspace();
                break;
            case Keys.Delete:
                DeleteForward();
                break;
            case Keys.Home:
                _cursorCol = 0;
                EnsureCursorVisible();
                break;
            case Keys.End:
                _cursorCol = _lines[_cursorLine].Length;
                EnsureCursorVisible();
                break;
        }
    }

    /// <summary>滚动视口：direction&gt;0 向上翻，direction&lt;0 向下翻。</summary>
    public void Scroll(int direction)
    {
        int visible = VisibleLineCount();
        _scrollOffset = Math.Clamp(_scrollOffset - direction, 0, Math.Max(0, _lines.Count - visible));
    }

    public void Draw(SpriteBatch b)
    {
        // 底板
        IClickableMenu.drawTextureBox(b,
            Bounds.X, Bounds.Y, Bounds.Width, Bounds.Height,
            Color.White);

        float lineHeight = Game1.smallFont.MeasureString("A").Y;
        int contentX = Bounds.X + PaddingX;
        int contentY = Bounds.Y + PaddingY;
        int contentW = Bounds.Width - PaddingX * 2;
        int visibleLines = VisibleLineCount();

        // 文本裁剪区域
        var clip = new Rectangle(contentX, contentY, contentW, Bounds.Height - PaddingY * 2);

        for (int i = 0; i < visibleLines; i++)
        {
            int lineIndex = _scrollOffset + i;
            if (lineIndex >= _lines.Count)
                break;
            b.DrawString(Game1.smallFont, _lines[lineIndex],
                new Vector2(contentX, contentY + i * lineHeight),
                Game1.textColor);
        }

        // 光标
        if (Selected && _cursorVisible)
        {
            int caretLine = _cursorLine - _scrollOffset;
            if (caretLine >= 0 && caretLine < visibleLines)
            {
                string before = _cursorCol > 0 ? _lines[_cursorLine].Substring(0, _cursorCol) : string.Empty;
                float caretX = contentX + Game1.smallFont.MeasureString(before).X;
                float caretY = contentY + caretLine * lineHeight;
                int caretHeight = (int)lineHeight;
                b.Draw(Game1.staminaRect,
                    new Rectangle((int)caretX, (int)caretY, 2, caretHeight),
                    Game1.textColor);
            }
        }
    }

    // ── 内部操作 ───────────────────────────────────────────────────────

    private void InsertNewline()
    {
        if (_lines.Count >= _maxLines)
            return;

        string line = _lines[_cursorLine];
        string before = _cursorCol > 0 ? line.Substring(0, _cursorCol) : string.Empty;
        string after = _cursorCol < line.Length ? line.Substring(_cursorCol) : string.Empty;

        _lines[_cursorLine] = before;
        _lines.Insert(_cursorLine + 1, after);
        _cursorLine++;
        _cursorCol = 0;
        EnsureCursorVisible();
        TextChanged?.Invoke();
    }

    private void Backspace()
    {
        if (_cursorCol > 0)
        {
            string line = _lines[_cursorLine];
            _lines[_cursorLine] = line.Remove(_cursorCol - 1, 1);
            _cursorCol--;
            TextChanged?.Invoke();
        }
        else if (_cursorLine > 0)
        {
            // 合并到上一行末尾
            int prevLen = _lines[_cursorLine - 1].Length;
            _lines[_cursorLine - 1] += _lines[_cursorLine];
            _lines.RemoveAt(_cursorLine);
            _cursorLine--;
            _cursorCol = prevLen;
            EnsureCursorVisible();
            TextChanged?.Invoke();
        }
    }

    private void DeleteForward()
    {
        string line = _lines[_cursorLine];
        if (_cursorCol < line.Length)
        {
            _lines[_cursorLine] = line.Remove(_cursorCol, 1);
            TextChanged?.Invoke();
        }
        else if (_cursorLine < _lines.Count - 1)
        {
            _lines[_cursorLine] += _lines[_cursorLine + 1];
            _lines.RemoveAt(_cursorLine + 1);
            TextChanged?.Invoke();
        }
    }

    private void MoveCursor(int line, int col)
    {
        ClampCursor();
        line = Math.Clamp(line, 0, _lines.Count - 1);
        col = Math.Clamp(col, 0, _lines[line].Length);
        _cursorLine = line;
        _cursorCol = col;
        EnsureCursorVisible();
    }

    private void ClampCursor()
    {
        if (_lines.Count == 0)
            _lines.Add(string.Empty);
        _cursorLine = Math.Clamp(_cursorLine, 0, _lines.Count - 1);
        _cursorCol = Math.Clamp(_cursorCol, 0, _lines[_cursorLine].Length);
    }

    private void EnsureCursorVisible()
    {
        int visibleLines = VisibleLineCount();
        if (_cursorLine < _scrollOffset)
            _scrollOffset = _cursorLine;
        else if (_cursorLine >= _scrollOffset + visibleLines)
            _scrollOffset = _cursorLine - visibleLines + 1;
        _scrollOffset = Math.Max(0, _scrollOffset);
    }

    private int VisibleLineCount()
    {
        float lineHeight = Game1.smallFont.MeasureString("A").Y;
        return Math.Max(1, (Bounds.Height - PaddingY * 2) / (int)Math.Max(1, lineHeight));
    }
}
