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
        private string _selectedId = string.Empty;
        private bool _isOpen;
        private int _scrollIndex;

        public Action<string> OnItemSelected;
        public string HeaderPrefix { get; set; } = string.Empty;

        public bool IsOpen => _isOpen;
        public string SelectedId => _selectedId;
        public Rectangle HeaderBounds => _headerRect;

        // 统一原木九宫格缩放倍率，规避浮点数取整导致纹理缝隙
        private const float BoxScale = 4f;

        public DropdownList(Rectangle headerRect, int itemHeight = 36, int maxVisibleItems = 7)
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
            _items = items != null ? new List<(string, string)>(items) : new List<(string, string)>();
            _selectedId = selectedId ?? string.Empty;
            _scrollIndex = 0;
        }

        public void ToggleOpen()
        {
            _isOpen = !_isOpen;
            if (_isOpen)
            {
                int selectedIdx = _items.FindIndex(it => string.Equals(it.Id, _selectedId, StringComparison.OrdinalIgnoreCase));
                if (selectedIdx >= 0)
                    _scrollIndex = Math.Clamp(selectedIdx - _maxVisibleItems / 2, 0, Math.Max(0, _items.Count - _maxVisibleItems));
                else
                    _scrollIndex = 0;
            }
        }

        public void Close() => _isOpen = false;

        private Rectangle GetRowRect(int visualIndex)
        {
            int iy = _headerRect.Bottom + 2 + (visualIndex * _itemHeight);
            return new Rectangle(_headerRect.X + 4, iy, _headerRect.Width - 8, _itemHeight);
        }

        public bool ReceiveLeftClick(int x, int y)
        {
            if (!_isOpen) return false;

            // 点击表头自身收起
            if (_headerRect.Contains(x, y))
            {
                _isOpen = false;
                Game1.playSound("shwip");
                return true;
            }

            int visible = Math.Min(_maxVisibleItems, Math.Max(0, _items.Count - _scrollIndex));
            for (int i = 0; i < visible; i++)
            {
                var ir = GetRowRect(i);
                if (ir.Contains(x, y))
                {
                    int itemIdx = _scrollIndex + i;
                    if (itemIdx >= 0 && itemIdx < _items.Count)
                    {
                        _selectedId = _items[itemIdx].Id;
                        _isOpen = false;
                        Game1.playSound("smallSelect");
                        OnItemSelected?.Invoke(_selectedId);
                        return true;
                    }
                }
            }

            // 点击外部收起并拦截穿透
            _isOpen = false;
            Game1.playSound("shwip");
            return true;
        }

        public bool ReceiveScrollWheel(int direction)
        {
            if (!_isOpen || _items.Count <= _maxVisibleItems) return false;

            if (direction > 0 && _scrollIndex > 0)
            {
                _scrollIndex--;
                return true;
            }
            if (direction < 0 && _scrollIndex < _items.Count - _maxVisibleItems)
            {
                _scrollIndex++;
                return true;
            }

            return false;
        }

        public void Draw(SpriteBatch b)
        {
            int mx = Game1.getMouseX(), my = Game1.getMouseY();
            bool hover = _headerRect.Contains(mx, my);

            // 表头背景：常规深木，悬停琥珀暖棕，展开时适度凹陷暗色
            Color headerBg = _isOpen ? new Color(115, 70, 30)
                          : hover ? new Color(175, 115, 55)
                          : new Color(139, 90, 43);

            // 1. 绘制表头内衬底色与边框
            b.Draw(Game1.staminaRect, _headerRect, headerBg);
            IClickableMenu.drawTextureBox(b, Game1.mouseCursors,
                new Rectangle(432, 439, 9, 9),
                _headerRect.X, _headerRect.Y, _headerRect.Width, _headerRect.Height,
                headerBg, BoxScale, false);

            string selLabel = _items.FirstOrDefault(it => string.Equals(it.Id, _selectedId, StringComparison.OrdinalIgnoreCase)).Label ?? "";
            if (string.IsNullOrEmpty(selLabel)) selLabel = "—";
            string label = (HeaderPrefix ?? "") + selLabel;

            float maxTextW = _headerRect.Width - 44;
            string displayLabel = CustomFontManager.TruncateString(label, CustomFontManager.SizeRegular, maxTextW);
            var size = CustomFontManager.MeasureStringBold(displayLabel, CustomFontManager.SizeRegular);

            CustomFontManager.DrawStringBold(b, displayLabel,
                new Vector2(_headerRect.X + 16, _headerRect.Y + (_headerRect.Height - size.Y) / 2f),
                Color.White, CustomFontManager.SizeRegular);

            SpriteEffects effect = _isOpen ? SpriteEffects.FlipVertically : SpriteEffects.None;
            Vector2 arrowPos = new Vector2(_headerRect.Right - 28, _headerRect.Y + (_headerRect.Height - 22) / 2f);

            b.Draw(Game1.mouseCursors, arrowPos,
                new Rectangle(437, 450, 10, 11),
                Color.White, 0f, Vector2.Zero, 2.0f, effect, 1f);

            if (!_isOpen) return;

            // ── 2. 下拉弹层列表绘制 ──
            int visible = Math.Max(1, Math.Min(_maxVisibleItems, _items.Count - _scrollIndex));
            int totalPopupHeight = visible * _itemHeight;
            var popupRect = new Rectangle(_headerRect.X, _headerRect.Bottom, _headerRect.Width, totalPopupHeight + 4);

            // 整体阴影
            b.Draw(Game1.staminaRect, new Rectangle(popupRect.X + 3, popupRect.Y + 3, popupRect.Width, popupRect.Height), Color.Black * 0.25f);

            // 弹窗浅木色底色（比表头浅一阶，拉开层级）
            Color popupBg = new Color(158, 108, 62);
            b.Draw(Game1.staminaRect, popupRect, popupBg);

            // 必须先画九宫格底框，防止后画遮挡选项
            IClickableMenu.drawTextureBox(b, Game1.mouseCursors,
                new Rectangle(432, 439, 9, 9),
                popupRect.X, popupRect.Y, popupRect.Width, popupRect.Height,
                popupBg, BoxScale, false);

            if (_items.Count == 0)
            {
                string emptyTip = "暂无候选 NPC";
                var tipSize = CustomFontManager.MeasureString(emptyTip, CustomFontManager.SizeRegular);
                CustomFontManager.DrawString(b, emptyTip,
                    new Vector2(popupRect.X + (popupRect.Width - tipSize.X) / 2f, popupRect.Y + (popupRect.Height - tipSize.Y) / 2f),
                    Color.White * 0.8f, CustomFontManager.SizeRegular);
                return;
            }

            // 逐行渲染选项
            for (int i = 0; i < visible; i++)
            {
                int idx = _scrollIndex + i;
                if (idx < 0 || idx >= _items.Count) continue;

                var item = _items[idx];
                var rowRect = GetRowRect(i);

                bool selected = string.Equals(item.Id, _selectedId, StringComparison.OrdinalIgnoreCase);
                bool ihover = rowRect.Contains(mx, my);

                if (selected)
                {
                    // 选中项：暖金木色底 + 亮金竖条标记
                    b.Draw(Game1.staminaRect, rowRect, new Color(180, 126, 75));
                    b.Draw(Game1.staminaRect, new Rectangle(rowRect.X, rowRect.Y + 2, 4, rowRect.Height - 4), new Color(255, 215, 95));
                }
                else if (ihover)
                {
                    // 悬停高亮：温润柔和的浅琥珀木色高亮
                    b.Draw(Game1.staminaRect, rowRect, new Color(185, 132, 80));
                }

                // 细分隔线
                if (i < visible - 1)
                {
                    b.Draw(Game1.staminaRect,
                        new Rectangle(rowRect.X + 4, rowRect.Bottom - 1, rowRect.Width - 8, 1),
                        Color.Black * 0.2f);
                }

                // 选项文字（白色系）
                string itemText = CustomFontManager.TruncateString(item.Label, CustomFontManager.SizeRegular, rowRect.Width - 28);
                var textSize = CustomFontManager.MeasureString(itemText, CustomFontManager.SizeRegular);
                Vector2 textPos = new Vector2(rowRect.X + 14, rowRect.Y + (_itemHeight - textSize.Y) / 2f);

                if (selected || ihover)
                {
                    CustomFontManager.DrawStringBold(b, itemText, textPos, Color.White, CustomFontManager.SizeRegular);
                }
                else
                {
                    CustomFontManager.DrawString(b, itemText, textPos, Color.White * 0.92f, CustomFontManager.SizeRegular);
                }
            }

            // 3. 滚动条指示器
            if (_items.Count > _maxVisibleItems) 
            {
                int trackX = popupRect.Right - 8;
                int trackY = popupRect.Y + 4;
                int trackH = popupRect.Height - 8;

                b.Draw(Game1.staminaRect, new Rectangle(trackX, trackY, 3, trackH), Color.Black * 0.25f);

                float ratio = (float)visible / _items.Count;
                int thumbH = Math.Max(16, (int)(trackH * ratio));
                int maxScroll = _items.Count - _maxVisibleItems;
                int thumbY = trackY + (int)((trackH - thumbH) * ((float)_scrollIndex / maxScroll));

                // 滑块使用柔和高亮木色
                b.Draw(Game1.staminaRect, new Rectangle(trackX, thumbY, 3, thumbH), new Color(225, 175, 110));
            }
        }
    }
}