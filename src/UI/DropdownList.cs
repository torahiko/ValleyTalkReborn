using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;
using StardewValley.Menus;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ValleytalkReborn.UI
{
    internal sealed class DropdownList
    {
        private Rectangle _headerRect;
        private readonly int _itemHeight;
        private readonly int _maxVisibleItems;

        private List<(string Id, string Label)> _items = new List<(string, string)>();
        private string _selectedId;
        private bool _isOpen;
        private int _scrollIndex;

        public Action<string> OnItemSelected;
        public string HeaderPrefix { get; set; }

        public bool IsOpen => _isOpen;
        public string SelectedId => _selectedId;
        public Rectangle HeaderBounds => _headerRect;

        public DropdownList(Rectangle headerRect, int itemHeight = 38, int maxVisibleItems = 7)
        {
            _headerRect = headerRect;
            _itemHeight = itemHeight;
            _maxVisibleItems = maxVisibleItems;
        }

        public void SetHeaderBounds(Rectangle rect)
        {
            _headerRect = rect;
        }

        public void SetItems(IReadOnlyList<(string Id, string Label)> items, string selectedId)
        {
            _items = new List<(string, string)>(items);
            _selectedId = selectedId;
            _scrollIndex = 0;
        }

        public void ToggleOpen()
        {
            _isOpen = !_isOpen;
            if (_isOpen)
            {
                int selectedIdx = _items.FindIndex(it => it.Id == _selectedId);
                if (selectedIdx >= 0)
                    _scrollIndex = Math.Clamp(selectedIdx - _maxVisibleItems / 2, 0, Math.Max(0, _items.Count - _maxVisibleItems));
                else
                    _scrollIndex = 0;
            }
        }

        public void Close() => _isOpen = false;

        public bool ReceiveLeftClick(int x, int y)
        {
            if (!_isOpen) return false;

            // 点击头部自身：收起下拉框
            if (_headerRect.Contains(x, y))
            {
                _isOpen = false;
                Game1.playSound("shwip");
                return true;
            }

            int visible = Math.Min(_maxVisibleItems, _items.Count - _scrollIndex);
            for (int i = 0; i < visible; i++)
            {
                int iy = _headerRect.Bottom + i * _itemHeight;
                var ir = new Rectangle(_headerRect.X, iy, _headerRect.Width, _itemHeight);
                if (ir.Contains(x, y))
                {
                    _selectedId = _items[_scrollIndex + i].Id;
                    _isOpen = false;
                    Game1.playSound("smallSelect");
                    OnItemSelected?.Invoke(_selectedId);
                    return true;
                }
            }

            // 点击到下拉列表外的空白处：自动收起并吞掉本次点击，防止误触底层控件
            _isOpen = false;
            Game1.playSound("shwip");
            return true;
        }

        public bool ReceiveScrollWheel(int direction)
        {
            if (!_isOpen || _items.Count <= _maxVisibleItems) return false;

            if (direction > 0 && _scrollIndex > 0)
                _scrollIndex--;
            else if (direction < 0 && _scrollIndex < _items.Count - _maxVisibleItems)
                _scrollIndex++;
            else
                return false;

            return true;
        }

        public void Draw(SpriteBatch b)
        {
            int mx = Game1.getMouseX(), my = Game1.getMouseY();
            bool hover = _headerRect.Contains(mx, my);

            Color headerBg = _isOpen ? new Color(210, 180, 140)
                          : hover ? new Color(255, 238, 210)
                          : new Color(139, 90, 43);

            // 实体内衬防漏白
            b.Draw(Game1.staminaRect, new Rectangle(_headerRect.X + 2, _headerRect.Y + 2, _headerRect.Width - 4, _headerRect.Height - 4), headerBg);
            IClickableMenu.drawTextureBox(b, Game1.mouseCursors,
                new Rectangle(432, 439, 9, 9),
                _headerRect.X, _headerRect.Y, _headerRect.Width, _headerRect.Height,
                headerBg, 3.0f, false);

            string selLabel = _items.FirstOrDefault(it => it.Id == _selectedId).Label ?? "";
            if (string.IsNullOrEmpty(selLabel)) selLabel = "—";
            string label = (HeaderPrefix ?? "") + selLabel;

            var size = CustomFontManager.MeasureStringBold(label, CustomFontManager.SizeRegular);
            CustomFontManager.DrawStringBold(b, label,
                new Vector2(_headerRect.X + 16,
                    _headerRect.Y + (_headerRect.Height - size.Y) / 2f),
                hover && !_isOpen ? Game1.textColor : Color.White, CustomFontManager.SizeRegular);

            SpriteEffects effect = _isOpen ? SpriteEffects.FlipVertically : SpriteEffects.None;
            Vector2 arrowPos = new Vector2(_headerRect.Right - 30, _headerRect.Y + (_headerRect.Height - 22) / 2f);

            b.Draw(Game1.mouseCursors, arrowPos,
                new Rectangle(437, 450, 10, 11),
                Color.White, 0f, Vector2.Zero, 2.0f, effect, 1f);

            if (!_isOpen) return;

            // ── 下拉弹层列表绘制 ──
            int visible = Math.Min(_maxVisibleItems, _items.Count - _scrollIndex);
            int totalPopupHeight = visible * _itemHeight;
            var popupRect = new Rectangle(_headerRect.X, _headerRect.Bottom, _headerRect.Width, totalPopupHeight);

            // 1. 弹层整体阴影与基底色（消除缝隙的核心：整体打底）
            b.Draw(Game1.staminaRect,
                new Rectangle(popupRect.X + 2, popupRect.Y + 2, popupRect.Width, popupRect.Height),
                Color.Black * 0.25f);
            b.Draw(Game1.staminaRect, popupRect, new Color(252, 246, 236));

            // 2. 纯平绘制子项背景与文字，避免逐项 drawTextureBox 拼接出的白缝
            for (int i = 0; i < visible; i++)
            {
                int idx = _scrollIndex + i;
                var item = _items[idx];
                int iy = _headerRect.Bottom + i * _itemHeight;
                var ir = new Rectangle(_headerRect.X, iy, _headerRect.Width, _itemHeight);

                bool selected = item.Id == _selectedId;
                bool ihover = ir.Contains(mx, my);

                if (selected)
                {
                    b.Draw(Game1.staminaRect, ir, new Color(210, 175, 130));
                }
                else if (ihover)
                {
                    b.Draw(Game1.staminaRect, ir, new Color(255, 245, 218));
                }

                // 绘制细分割线（非末行）
                if (i < visible - 1)
                {
                    b.Draw(Game1.staminaRect,
                        new Rectangle(ir.X + 4, ir.Bottom - 1, ir.Width - 8, 1),
                        new Color(215, 195, 170) * 0.55f);
                }

                Color textColor = selected ? Color.White
                                : ihover ? new Color(130, 50, 15)
                                : Game1.textColor;

                string displayLabel = CustomFontManager.TruncateString(item.Label, CustomFontManager.SizeRegular, ir.Width - 36);

                if (selected || ihover)
                {
                    CustomFontManager.DrawStringBold(b, displayLabel,
                        new Vector2(ir.X + 16, ir.Y + (ir.Height - CustomFontManager.MeasureStringBold("A", CustomFontManager.SizeRegular).Y) / 2f),
                        textColor, CustomFontManager.SizeRegular);
                }
                else
                {
                    CustomFontManager.DrawString(b, displayLabel,
                        new Vector2(ir.X + 16, ir.Y + (ir.Height - CustomFontManager.MeasureString("A", CustomFontManager.SizeRegular).Y) / 2f),
                        textColor, CustomFontManager.SizeRegular);
                }
            }

            // 3. 弹层整体统一加外边框
            IClickableMenu.drawTextureBox(b, Game1.mouseCursors,
                new Rectangle(432, 439, 9, 9),
                popupRect.X, popupRect.Y, popupRect.Width, popupRect.Height,
                new Color(210, 180, 140), 2.5f, false);
        }
    }
}
