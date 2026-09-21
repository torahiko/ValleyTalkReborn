using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewValley;
using StardewValley.Menus;

namespace ValleytalkReborn
{
    /// <summary>
    /// 关注池标签编辑器：所见即所得胶囊标签，支持 Enter 确认、Esc 取消、自适应 40 字符甜点区间输入槽
    /// </summary>
    internal sealed class TagListEditor
    {
        // ── 关注短语甜点限制（最长支持约 20 汉字 / 40 英文字符） ──
        private const int MaxTagCharLimit = 40;

        private readonly List<string> _tags = new();
        private Rectangle _bounds;
        private readonly List<(Rectangle Rect, Rectangle DeleteRect, int Index)> _tagHitboxes = new();
        private Rectangle _addBtnRect;
        private bool _isAdding;
        private readonly TextBox _inputBox;

        public event Action OnChanged;
        public IReadOnlyList<string> Tags => _tags;
        public bool IsAdding => _isAdding;
        public Rectangle Bounds => _bounds;

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
                Vector2 sz = CustomFontManager.MeasureString(tag, 18.5f);
                int tagW = (int)sz.X + 26;

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

            // 🌟 核心优化：展开输入状态提供 220px 舒适视觉呼吸空间，同时 Clamp 保证绝不超出面板右边缘
            int addBtnW = _isAdding ? Math.Min(220, _bounds.Width - 10) : 68;
            int addBtnH = _isAdding ? 28 : rowH;

            if (curX + addBtnW > _bounds.Right)
            {
                curX = _bounds.X;
                curY += rowH + gap;
            }

            // 输入状态 Y 轴微调 1px，使其与已存在的胶囊在垂直基线上自然对齐
            int slotY = _isAdding ? curY - 1 : curY;
            _addBtnRect = new Rectangle(curX, slotY, addBtnW, addBtnH);

            _inputBox.X = _addBtnRect.X;
            _inputBox.Y = _addBtnRect.Y;
            _inputBox.Width = addBtnW;
            _inputBox.Height = addBtnH;
        }

        public bool ReceiveLeftClick(int x, int y)
        {
            // 1. 点击删除现有标签
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

            // 2. 点击进入添加状态
            if (_addBtnRect.Contains(x, y) && !_isAdding)
            {
                _isAdding = true;
                _inputBox.Text = "";
                _inputBox.Selected = true;
                Game1.keyboardDispatcher.Subscriber = _inputBox;
                RecalculateLayout();
                Game1.playSound("smallSelect");
                return true;
            }

            // 3. 点击外部提交输入
            if (_isAdding && !_addBtnRect.Contains(x, y))
            {
                CommitInput();
            }

            return false;
        }

        public bool ReceiveKeyPress(Keys key)
        {
            // 守卫：拦截 menuButton，防止 E 键在标签编辑器中误触关闭主菜单
            if (Game1.options.doesInputListContain(Game1.options.menuButton, key))
                return true;

            if (!_isAdding) return false;

            // 限制单条输入不超过 MaxTagCharLimit (40 字符)
            if (_inputBox.Text != null && _inputBox.Text.Length > MaxTagCharLimit)
            {
                _inputBox.Text = _inputBox.Text.Substring(0, MaxTagCharLimit);
                Game1.playSound("cancel");
            }

            if (key == Keys.Enter)
            {
                CommitInput();
                return true;
            }
            if (key == Keys.Escape)
            {
                CancelInput();
                return true;
            }

            return false;
        }

        public void CommitInput()
        {
            if (!_isAdding) return;
            string text = _inputBox.Text?.Trim() ?? "";

            // 安全截断至 40 字符
            if (text.Length > MaxTagCharLimit)
                text = text.Substring(0, MaxTagCharLimit);

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

        public void CancelInput()
        {
            _isAdding = false;
            _inputBox.Text = "";
            _inputBox.Selected = false;
            if (Game1.keyboardDispatcher.Subscriber == _inputBox)
                Game1.keyboardDispatcher.Subscriber = null;
            RecalculateLayout();
            Game1.playSound("bigDeSelect");
        }

        public void Draw(SpriteBatch b)
        {
            int mx = Game1.getMouseX();
            int my = Game1.getMouseY();

            // 1. 绘制现有胶囊标签：纯净羊皮纸底 + 柔和浅木框
            foreach (var (r, delR, idx) in _tagHitboxes)
            {
                bool isHover = r.Contains(mx, my);
                Color tagBg = isHover ? new Color(255, 250, 240) : new Color(248, 242, 232);
                Color tagBorder = isHover ? new Color(205, 185, 155) : new Color(228, 212, 190);

                // 内缩 3px 纯色垫底，外层套木框
                b.Draw(Game1.staminaRect, new Rectangle(r.X + 3, r.Y + 3, r.Width - 6, r.Height - 6), tagBg);
                IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(432, 439, 9, 9),
                    r.X, r.Y, r.Width, r.Height, tagBorder, 2f, false);

                CustomFontManager.DrawString(b, _tags[idx], new Vector2(r.X + 7, r.Y + 4), BioEditorMenu.TextPrimary, 18.5f);

                bool delHover = delR.Contains(mx, my);
                CustomFontManager.DrawString(b, "×", new Vector2(delR.X + 2, delR.Y - 1), delHover ? BioEditorMenu.TextDanger : BioEditorMenu.TextMuted, 18.5f);
            }

            // 2. 绘制新增输入状态 / "+ 新增" 按钮
            if (_isAdding)
            {
                var r = _addBtnRect;

                // 处于输入态时：直接套用激活态输入框标准（明亮底 + 纯正深红木 85, 40, 28）
                b.Draw(Game1.staminaRect, new Rectangle(r.X + 4, r.Y + 4, r.Width - 8, r.Height - 8), new Color(255, 252, 245));
                IClickableMenu.drawTextureBox(
                    b,
                    Game1.mouseCursors,
                    new Rectangle(432, 439, 9, 9),
                    r.X,
                    r.Y,
                    r.Width,
                    r.Height,
                    Color.White,
                    2f,
                    false
                );

                string text = _inputBox.Text ?? "";
                float textX = r.X + 8;

                if (!string.IsNullOrEmpty(text))
                {
                    Vector2 textSize = CustomFontManager.MeasureString(text, 17f);
                    float textY = r.Y + (r.Height - textSize.Y) / 2f;
                    CustomFontManager.DrawString(b, text, new Vector2(textX, textY), BioEditorMenu.TextPrimary, 17f);

                    if (_inputBox.Selected && (int)(Game1.currentGameTime.TotalGameTime.TotalMilliseconds / 500) % 2 == 0)
                    {
                        float cx = textX + textSize.X + 1;
                        int cursorH = 16;
                        float cursorY = r.Y + (r.Height - cursorH) / 2f;
                        b.Draw(Game1.staminaRect, new Rectangle((int)cx, (int)cursorY, 2, cursorH), BioEditorMenu.TextPrimary);
                    }
                }
                else
                {
                    string placeholder = "输入事物或焦点并回车...";
                    Vector2 phSize = CustomFontManager.MeasureString(placeholder, 15f);
                    float phY = r.Y + (r.Height - phSize.Y) / 2f;
                    CustomFontManager.DrawString(b, placeholder, new Vector2(textX, phY), BioEditorMenu.TextMuted, 15f);

                    if (_inputBox.Selected && (int)(Game1.currentGameTime.TotalGameTime.TotalMilliseconds / 500) % 2 == 0)
                    {
                        int cursorH = 16;
                        float cursorY = r.Y + (r.Height - cursorH) / 2f;
                        b.Draw(Game1.staminaRect, new Rectangle((int)textX, (int)cursorY, 2, cursorH), BioEditorMenu.TextPrimary);
                    }
                }
            }
            else
            {
                bool addHover = _addBtnRect.Contains(mx, my);
                Color addBg = addHover ? new Color(255, 235, 205) : new Color(225, 205, 175);
                b.Draw(Game1.staminaRect, new Rectangle(_addBtnRect.X + 3, _addBtnRect.Y + 3, _addBtnRect.Width - 6, _addBtnRect.Height - 6), addBg);
                IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(432, 439, 9, 9),
                    _addBtnRect.X, _addBtnRect.Y, _addBtnRect.Width, _addBtnRect.Height, new Color(200, 170, 135), 2f, false);
                CustomFontManager.DrawString(b, "+ 新增", new Vector2(_addBtnRect.X + 10, _addBtnRect.Y + 4), BioEditorMenu.TextPrimary, 18.5f);
            }
        }
    }
}