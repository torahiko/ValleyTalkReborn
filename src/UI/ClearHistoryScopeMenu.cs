using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewValley;
using StardewValley.Menus;
using System;
using System.Collections.Generic;

namespace ValleytalkReborn
{
    internal class ClearHistoryScopeMenu : IClickableMenu
    {
        public enum ClearScope
        {
            Today,
            CurrentNpcAll,
            GlobalAll
        }

        private const int MenuWidth = 620;
        private const int MenuHeight = 320;

        private readonly string _npcName;
        private readonly DialogueTextInputMenu _ownerMenu;
        private readonly Action<ClearScope> _onConfirm;

        private readonly ClickableTextureComponent _okButton;
        private readonly ClickableTextureComponent _cancelButton;
        private readonly Rectangle _dropdownHeaderRect;
        private readonly List<(ClearScope Scope, string Label, Rectangle Rect)> _dropdownItems;

        private ClearScope _selectedScope = ClearScope.Today;
        private bool _isDropdownOpen = false;

        private float _okHoverScale = 1f;
        private float _cancelHoverScale = 1f;

        public ClearHistoryScopeMenu(string npcName, DialogueTextInputMenu ownerMenu, Action<ClearScope> onConfirm)
            : base((Game1.uiViewport.Width - MenuWidth) / 2, (Game1.uiViewport.Height - MenuHeight) / 2, MenuWidth,
                MenuHeight)
        {
            _npcName = npcName;
            _ownerMenu = ownerMenu;
            _onConfirm = onConfirm;

            string dispName = Game1.getCharacterFromName(_npcName)?.displayName ?? _npcName;

            int ddW = MenuWidth - 120;
            int ddH = 44;
            _dropdownHeaderRect = new Rectangle(xPositionOnScreen + 60, yPositionOnScreen + 130, ddW, ddH);

            _dropdownItems = new List<(ClearScope, string, Rectangle)>
            {
                (ClearScope.Today, I18n.DialogueInput.ClearScopeToday(dispName),
                    new Rectangle(_dropdownHeaderRect.X, _dropdownHeaderRect.Bottom + 0 * ddH, ddW, ddH)),
                (ClearScope.CurrentNpcAll, I18n.DialogueInput.ClearScopeCurrentNpcAll(dispName),
                    new Rectangle(_dropdownHeaderRect.X, _dropdownHeaderRect.Bottom + 1 * ddH, ddW, ddH)),
                (ClearScope.GlobalAll, I18n.DialogueInput.ClearScopeGlobalAll(),
                    new Rectangle(_dropdownHeaderRect.X, _dropdownHeaderRect.Bottom + 2 * ddH, ddW, ddH))
            };

            int btnY = yPositionOnScreen + MenuHeight - 74;

            _okButton = new ClickableTextureComponent(
                new Rectangle(xPositionOnScreen + MenuWidth - 60 - 54, btnY, 54, 54),
                Game1.mouseCursors,
                Game1.getSourceRectForStandardTileSheet(Game1.mouseCursors, 46, -1, -1),
                1f);

            _cancelButton = new ClickableTextureComponent(
                new Rectangle(xPositionOnScreen + MenuWidth - 60 - 54 - 66 - 54, btnY, 54, 54),
                Game1.mouseCursors,
                Game1.getSourceRectForStandardTileSheet(Game1.mouseCursors, 47, -1, -1),
                1f);
        }

        public override void receiveLeftClick(int x, int y, bool playSound = true)
        {
            base.receiveLeftClick(x, y, playSound);

            if (_isDropdownOpen)
            {
                foreach (var item in _dropdownItems)
                {
                    if (item.Rect.Contains(x, y))
                    {
                        _selectedScope = item.Scope;
                        _isDropdownOpen = false;
                        Game1.playSound("select");
                        return;
                    }
                }

                _isDropdownOpen = false;
                return;
            }

            if (_dropdownHeaderRect.Contains(x, y))
            {
                _isDropdownOpen = !_isDropdownOpen;
                Game1.playSound("shwip");
                return;
            }

            if (_okButton.containsPoint(x, y))
            {
                Game1.playSound("coin");
                exitThisMenu();
                try
                {
                    _onConfirm?.Invoke(_selectedScope);
                }
                finally
                {
                    _ownerMenu?.RestorePreviousMenu();
                }

                return;
            }

            if (_cancelButton.containsPoint(x, y))
            {
                Game1.playSound("bigDeSelect");
                exitThisMenu();
                _ownerMenu?.RestorePreviousMenu();
                return;
            }
        }

        public override void receiveKeyPress(Keys key)
        {
            if (key == Keys.Escape)
            {
                Game1.playSound("bigDeSelect");
                exitThisMenu();
                _ownerMenu?.RestorePreviousMenu();
                return;
            }

            base.receiveKeyPress(key);
        }

        private string GetScopeLabel(ClearScope scope)
        {
            foreach (var item in _dropdownItems)
            {
                if (item.Scope == scope) return item.Label;
            }

            return string.Empty;
        }

        public override void draw(SpriteBatch b)
        {
            b.Draw(Game1.fadeToBlackRect, Game1.graphics.GraphicsDevice.Viewport.Bounds, Color.Black * 0.45f);

            Game1.drawDialogueBox(xPositionOnScreen, yPositionOnScreen, width, height, false, true);

            string title = I18n.DialogueInput.ClearScopeTitle();
            var titleSize = CustomFontManager.MeasureString(title, CustomFontManager.SizeTitle);
            CustomFontManager.DrawString(b, title,
                new Vector2(xPositionOnScreen + (width - titleSize.X) / 2f, yPositionOnScreen + 36),
                Game1.textColor, CustomFontManager.SizeTitle);

            CustomFontManager.DrawString(b, I18n.DialogueInput.ClearScopeHint(),
                new Vector2(_dropdownHeaderRect.X, yPositionOnScreen + 95),
                Color.Gray, CustomFontManager.SizeSmall);

            int mx = Game1.getMouseX();
            int my = Game1.getMouseY();

            UiHelper.UpdateButtonScale(ref _okHoverScale, _okButton, mx, my);
            _okButton.scale = 1f * _okHoverScale;
            _okButton.draw(b);

            UiHelper.UpdateButtonScale(ref _cancelHoverScale, _cancelButton, mx, my);
            _cancelButton.scale = 1f * _cancelHoverScale;
            _cancelButton.draw(b);

            // Dropdown header background
            Color headerBg;
            if (_isDropdownOpen)
                headerBg = new Color(210, 180, 140);
            else if (_dropdownHeaderRect.Contains(mx, my))
                headerBg = new Color(255, 235, 205);
            else
                headerBg = new Color(139, 90, 43);

            IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(432, 439, 9, 9),
                _dropdownHeaderRect.X, _dropdownHeaderRect.Y, _dropdownHeaderRect.Width, _dropdownHeaderRect.Height,
                headerBg, 4f, false);

            // Current item text (white, vertically centered)
            string currentLabel = GetScopeLabel(_selectedScope);
            var labelSize = CustomFontManager.MeasureString(currentLabel, CustomFontManager.SizeRegular);
            CustomFontManager.DrawString(b, currentLabel,
                new Vector2(_dropdownHeaderRect.X + 12,
                    _dropdownHeaderRect.Y + (_dropdownHeaderRect.Height - labelSize.Y) / 2f),
                Color.White, CustomFontManager.SizeRegular);

            // Arrow texture
            var arrowRect = new Rectangle(_dropdownHeaderRect.Right - 30,
                _dropdownHeaderRect.Y + (_dropdownHeaderRect.Height - 11) / 2, 10, 11);
            SpriteEffects effects = _isDropdownOpen ? SpriteEffects.FlipVertically : SpriteEffects.None;
            b.Draw(Game1.mouseCursors, arrowRect, new Rectangle(437, 450, 10, 11), Color.White, 0f, Vector2.Zero,
                effects, 0f);

            // Dropdown items when open
            if (_isDropdownOpen)
            {
                foreach (var item in _dropdownItems)
                {
                    bool isSelected = item.Scope == _selectedScope;
                    bool isHover = item.Rect.Contains(mx, my);

                    Color itemBg;
                    if (isSelected)
                        itemBg = new Color(210, 180, 140);
                    else if (isHover)
                        itemBg = new Color(255, 235, 205);
                    else
                        itemBg = Color.White;

                    IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(432, 439, 9, 9),
                        item.Rect.X, item.Rect.Y, item.Rect.Width, item.Rect.Height,
                        itemBg, 4f, false);

                    Color textColor;
                    if (isSelected)
                        textColor = Color.White;
                    else if (isHover)
                        textColor = Game1.textColor;
                    else
                        textColor = Color.Black;

                    var itemLabelSize = CustomFontManager.MeasureString(item.Label, CustomFontManager.SizeRegular);
                    CustomFontManager.DrawString(b, item.Label,
                        new Vector2(item.Rect.X + 12,
                            item.Rect.Y + (item.Rect.Height - itemLabelSize.Y) / 2f),
                        textColor, CustomFontManager.SizeRegular);
                }
            }

            drawMouse(b);
        }
    }
}