using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;
using StardewValley.Menus;

namespace ValleytalkReborn
{
    /// <summary>
    /// 关注池标签编辑器：将逗号分隔的难读字符串转换为可增删的交互标签
    /// </summary>
    internal sealed class TagListEditor
    {
        private readonly List<string> _tags = new();
        private Rectangle _bounds;
        private readonly List<(Rectangle Rect, Rectangle DeleteRect, int Index)> _tagHitboxes = new();
        private Rectangle _addBtnRect;
        private bool _isAdding;
        private readonly TextBox _inputBox;

        public event Action OnChanged;
        public IReadOnlyList<string> Tags => _tags;

        public TagListEditor(Rectangle bounds)
        {
            _bounds = bounds;
            _inputBox = new TextBox(Game1.content.Load<Texture2D>("LooseSprites\\textBox"), null, Game1.smallFont, Game1.textColor);
        }

        public void SetBounds(Rectangle bounds)
        {
            _bounds = bounds;
            RecalculateLayout();
        }

        public void SetTags(IEnumerable<string> tags)
        {
            _tags.Clear();
            if (tags != null)
            {
                foreach (var t in tags)
                {
                    if (!string.IsNullOrWhiteSpace(t))
                        _tags.Add(t.Trim());
                }
            }
            RecalculateLayout();
        }

        private void RecalculateLayout()
        {
            _tagHitboxes.Clear();
            int curX = _bounds.X;
            int curY = _bounds.Y;
            int rowH = 26;
            int gap = 6;

            for (int i = 0; i < _tags.Count; i++)
            {
                string tag = _tags[i];
                Vector2 sz = Game1.smallFont.MeasureString(tag);
                int tagW = (int)sz.X + 26; // 留出文字和 [x] 的位置

                if (curX + tagW > _bounds.Right)
                {
                    curX = _bounds.X;
                    curY += rowH + gap;
                }

                var r = new Rectangle(curX, curY, tagW, rowH);
                var delR = new Rectangle(r.Right - 20, r.Y + 3, 18, 20);
                _tagHitboxes.Add((r, delR, i));
                curX += tagW + gap;
            }

            // 新增按钮
            int addBtnW = 68;
            if (curX + addBtnW > _bounds.Right)
            {
                curX = _bounds.X;
                curY += rowH + gap;
            }
            _addBtnRect = new Rectangle(curX, curY, addBtnW, rowH);

            _inputBox.X = _addBtnRect.X;
            _inputBox.Y = _addBtnRect.Y - 2;
            _inputBox.Width = 140;
            _inputBox.Height = 28;
        }

        public bool ReceiveLeftClick(int x, int y)
        {
            // 点击删除标签
            foreach (var (r, delR, idx) in _tagHitboxes)
            {
                if (delR.Contains(x, y))
                {
                    _tags.RemoveAt(idx);
                    RecalculateLayout();
                    OnChanged?.Invoke();
                    Game1.playSound("trashcan");
                    return true;
                }
            }

            // 点击"+添加"
            if (_addBtnRect.Contains(x, y) && !_isAdding)
            {
                _isAdding = true;
                _inputBox.Text = "";
                _inputBox.Selected = true;
                Game1.keyboardDispatcher.Subscriber = _inputBox;
                Game1.playSound("smallSelect");
                return true;
            }

            // 提交输入
            if (_isAdding && !_inputBox.Selected)
            {
                CommitInput();
            }

            return false;
        }

        public void CommitInput()
        {
            if (!_isAdding) return;
            string text = _inputBox.Text?.Trim() ?? "";
            if (!string.IsNullOrEmpty(text) && !_tags.Contains(text))
            {
                _tags.Add(text);
                OnChanged?.Invoke();
                Game1.playSound("coin");
            }
            _isAdding = false;
            _inputBox.Selected = false;
            if (Game1.keyboardDispatcher.Subscriber == _inputBox)
                Game1.keyboardDispatcher.Subscriber = null;
            RecalculateLayout();
        }

        public void Draw(SpriteBatch b)
        {
            int mx = Game1.getMouseX();
            int my = Game1.getMouseY();

            foreach (var (r, delR, idx) in _tagHitboxes)
            {
                bool isHover = r.Contains(mx, my);
                // 绘制药丸底色
                IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(403, 383, 6, 6),
                    r.X, r.Y, r.Width, r.Height, isHover ? new Color(245, 230, 210) : new Color(230, 220, 200), 2f, false);

                b.DrawString(Game1.smallFont, _tags[idx], new Vector2(r.X + 6, r.Y + 4), Game1.textColor);

                // 删除 'x'
                bool delHover = delR.Contains(mx, my);
                b.DrawString(Game1.smallFont, "×", new Vector2(delR.X + 2, delR.Y - 1), delHover ? Color.Red : Color.DimGray);
            }

            if (_isAdding)
            {
                // 使用同款细腻羊皮纸槽
                IClickableMenu.drawTextureBox(
                    b,
                    Game1.mouseCursors,
                    new Rectangle(403, 383, 6, 6),
                    _inputBox.X,
                    _inputBox.Y,
                    _inputBox.Width,
                    _inputBox.Height,
                    new Color(255, 248, 220),
                    2f,
                    false
                );
                _inputBox.Draw(b);
            }
            else
            {
                bool addHover = _addBtnRect.Contains(mx, my);
                IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(432, 439, 9, 9),
                    _addBtnRect.X, _addBtnRect.Y, _addBtnRect.Width, _addBtnRect.Height, addHover ? new Color(255, 235, 205) : new Color(215, 195, 160), 2f, false);
                b.DrawString(Game1.smallFont, "+ 新增", new Vector2(_addBtnRect.X + 10, _addBtnRect.Y + 4), Game1.textColor);
            }
        }
    }
}
