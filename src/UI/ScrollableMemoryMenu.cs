using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewValley;
using StardewValley.Menus;
using StardewModdingAPI;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ValleytalkReborn
{
    // ─── 工具类 ──────────────────────────────────────────────────────
    internal static class UiHelper
    {
        /// <summary>平滑缩放按钮悬停效果。</summary>
        public static void UpdateButtonScale(ref float scale, ClickableTextureComponent btn, int mx, int my)
        {
            float target = btn.containsPoint(mx, my) ? 1.15f : 1.0f;
            scale += (target - scale) * 0.2f;
        }
    }

    // ─── 主菜单 ──────────────────────────────────────────────────────
    internal class ScrollableMemoryMenu : IClickableMenu
    {
        private readonly string _npcName;
        private readonly IClickableMenu _ownerMenu;
        private int _currentTab = 0;

        private Rectangle _tabNpcRect;
        private Rectangle _tabWorldRect;
        private Rectangle _callsignRect;

        // 🌟 改为缓存列表，避免每次 draw/input 都重新排序和 ToList
        private List<MemoryEntry> _cachedEntries = new();

        private List<MemoryEntry> ActiveEntries => _cachedEntries;

        private int MaxEntriesForTab =>
            _currentTab == 0
                ? MemoryManager.MaxMemoriesPerNpc
                : WorldMemoryManager.MaxEntries;

        private Rectangle _addButtonRect;
        private readonly ClickableTextureComponent _closeButton;
        private readonly List<ClickableTextureComponent> _deleteButtons = new();
        private readonly List<ClickableTextureComponent> _editButtons = new();

        private ClickableTextureComponent _upArrow;
        private ClickableTextureComponent _downArrow;
        private ClickableTextureComponent _scrollbar;
        private Rectangle _scrollbarRunner;

        private int _startIndex;
        private bool _scrolling;
        private int _hoveredRow = -1;

        private float _closeButtonHoverScale;
        private float _upArrowHoverScale;
        private float _downArrowHoverScale;

        private readonly float _closeButtonBaseScale;
        private readonly float _upArrowBaseScale;
        private readonly float _downArrowBaseScale;

        // ─── 布局常量（统一管理） ────────────────────────────────────
        private const int MenuWidth = 1000;
        private const int MenuHeight = 600;
        private const int TabBarY = 56;
        private const int TabHeight = 36;
        private const int TabWidth = 200;
        private const int TabGap = 8;
        private const int TopPadding = 110;
        private const int BottomPadding = 75;
        private const int LineHeight = 46;
        private const int ButtonSize = 40;
        private const int LeftPadding = 40;
        private const int RightPadding = 40;

        public ScrollableMemoryMenu(string npcName, IClickableMenu parentMenu = null)
        {
            _npcName = npcName;
            _ownerMenu = parentMenu;

            xPositionOnScreen = (Game1.uiViewport.Width - MenuWidth) / 2;
            yPositionOnScreen = (Game1.uiViewport.Height - MenuHeight) / 2;
            width = MenuWidth;
            height = MenuHeight;

            _closeButton = new ClickableTextureComponent(
                new Rectangle(xPositionOnScreen + width - 60, yPositionOnScreen + 16, 44, 44),
                Game1.mouseCursors, new Rectangle(337, 494, 12, 12), 3.5f);

            _closeButtonBaseScale = 3.5f;
            _closeButton.hoverText = I18n.Memory.CloseButton();

            _addButtonRect = new Rectangle(
                xPositionOnScreen + width / 2 - 150,
                yPositionOnScreen + height - 60,
                300, 48);

            _upArrow = new ClickableTextureComponent(
                new Rectangle(xPositionOnScreen + width - 48,
                              yPositionOnScreen + TopPadding, 44, 48),
                Game1.mouseCursors, new Rectangle(421, 459, 11, 12), 4f);

            _upArrowBaseScale = 4f;

            _downArrow = new ClickableTextureComponent(
                new Rectangle(xPositionOnScreen + width - 48,
                              yPositionOnScreen + height - BottomPadding, 44, 48),
                Game1.mouseCursors, new Rectangle(421, 472, 11, 12), 4f);

            _downArrowBaseScale = 4f;

            _scrollbarRunner = new Rectangle(
                xPositionOnScreen + width - 32,
                yPositionOnScreen + TopPadding + 50,
                12,
                height - TopPadding - BottomPadding - 80);

            _scrollbar = new ClickableTextureComponent(
                new Rectangle(_scrollbarRunner.X - 6, _scrollbarRunner.Y, 24, 40),
                Game1.mouseCursors, new Rectangle(435, 463, 6, 10), 4f);

            int tabBaseX = xPositionOnScreen + LeftPadding;
            int tabBaseY = yPositionOnScreen + TabBarY;

            _tabNpcRect  = new Rectangle(tabBaseX, tabBaseY, TabWidth, TabHeight);
            _tabWorldRect = new Rectangle(tabBaseX + TabWidth + TabGap, tabBaseY, TabWidth, TabHeight);

            // 称谓框：宽 280，贴右边界留 60px 给关闭按钮，左侧空白充裕
            _callsignRect = new Rectangle(
                xPositionOnScreen + width - RightPadding - 60 - 280,
                tabBaseY,
                280,
                TabHeight);

            // 🌟 初始化时读取一次缓存数据
            RefreshEntries();

            exitFunction = () => Game1.playSound("bigDeSelect");
        }

        private void SwitchTab(int tab)
        {
            if (_currentTab == tab) return;

            _currentTab = tab;
            _startIndex = 0;
            Game1.playSound("smallSelect");

            // 🌟 切换 Tab 时重新读取缓存
            RefreshEntries();
        }

        private void RefreshActionButtons()
        {
            ClampStartIndex();

            _deleteButtons.Clear();
            _editButtons.Clear();

            var entries = ActiveEntries;
            int visibleCount = GetVisibleLineCount();

            for (int i = 0; i < visibleCount && _startIndex + i < entries.Count; i++)
            {
                int y = yPositionOnScreen + TopPadding + 10 + i * LineHeight;

                var del = new ClickableTextureComponent(
                    new Rectangle(xPositionOnScreen + width - RightPadding - 30, y, ButtonSize, ButtonSize),
                    Game1.mouseCursors, new Rectangle(322, 498, 12, 12), 2.5f);

                del.hoverText = I18n.Memory.DeleteButtonHover();
                _deleteButtons.Add(del);

                var edit = new ClickableTextureComponent(
                    new Rectangle(xPositionOnScreen + width - RightPadding - 80, y, ButtonSize, ButtonSize),
                    Game1.mouseCursors, new Rectangle(274, 284, 16, 16), 2.5f);

                edit.hoverText = I18n.Memory.EditButtonHover();
                _editButtons.Add(edit);
            }
        }

        private void ClampStartIndex()
        {
            var entries = ActiveEntries;
            int maxLines = GetVisibleLineCount();
            int maxStart = Math.Max(0, entries.Count - maxLines);
            _startIndex = Math.Clamp(_startIndex, 0, maxStart);
        }

        private int GetVisibleLineCount()
            => (height - TopPadding - BottomPadding) / LineHeight;

        private void SetScrollbarPosition()
        {
            var entries = ActiveEntries;
            int maxLines = GetVisibleLineCount();

            if (entries.Count <= maxLines) return;

            float pct = (float)_startIndex / (entries.Count - maxLines);
            _scrollbar.bounds.Y = _scrollbarRunner.Y +
                (int)(pct * (_scrollbarRunner.Height - _scrollbar.bounds.Height));
        }

        internal void RefreshEntries()
        {
            // 🌟 从 Manager 读取一次，并缓存
            _cachedEntries = _currentTab == 0
                ? MemoryManager.Instance.GetMemories(_npcName)
                : WorldMemoryManager.Instance.GetEntries();

            _startIndex = 0;
            RefreshActionButtons();
            SetScrollbarPosition();
        }

        public override void receiveScrollWheelAction(int direction)
        {
            base.receiveScrollWheelAction(direction);

            var entries = ActiveEntries;
            int maxLines = GetVisibleLineCount();

            if (direction > 0 && _startIndex > 0)
            {
                _startIndex--;
                Game1.playSound("shwip");
                SetScrollbarPosition();
                RefreshActionButtons();
            }
            else if (direction < 0 && _startIndex < Math.Max(0, entries.Count - maxLines))
            {
                _startIndex++;
                Game1.playSound("shwip");
                SetScrollbarPosition();
                RefreshActionButtons();
            }
        }

        public override void receiveLeftClick(int x, int y, bool playSound = true)
        {
            base.receiveLeftClick(x, y, playSound);

            if (_closeButton.containsPoint(x, y))
            {
                Game1.playSound("bigDeSelect");
                exitThisMenu();
                return;
            }

            if (_tabNpcRect.Contains(x, y))
            {
                SwitchTab(0);
                return;
            }

            if (_tabWorldRect.Contains(x, y))
            {
                SwitchTab(1);
                return;
            }

            if (_currentTab == 0 && _callsignRect.Contains(x, y))
            {
                Game1.playSound("bigSelect");
                Game1.activeClickableMenu = new SetCallsignInputMenu(_npcName, this);
                return;
            }

            if (_addButtonRect.Contains(x, y))
            {
                OpenAddMemoryDialog();
                return;
            }

            var entries = ActiveEntries;

            for (int i = 0; i < _deleteButtons.Count; i++)
            {
                int idx = _startIndex + i;
                if (idx >= entries.Count) continue;

                if (_deleteButtons[i].containsPoint(x, y))
                {
                    ConfirmDeleteMemory(entries[idx]);
                    return;
                }

                if (i < _editButtons.Count && _editButtons[i].containsPoint(x, y))
                {
                    Game1.playSound("bigSelect");
                    Game1.activeClickableMenu = new AddMemoryInputMenu(
                        _npcName, this, entries[idx], _currentTab);
                    return;
                }
            }

            int maxLines = GetVisibleLineCount();

            if (entries.Count > maxLines)
            {
                if (_upArrow.containsPoint(x, y) && _startIndex > 0)
                {
                    _startIndex--;
                    Game1.playSound("shwip");
                    SetScrollbarPosition();
                    RefreshActionButtons();
                }
                else if (_downArrow.containsPoint(x, y) &&
                         _startIndex < entries.Count - maxLines)
                {
                    _startIndex++;
                    Game1.playSound("shwip");
                    SetScrollbarPosition();
                    RefreshActionButtons();
                }
                else if (_scrollbarRunner.Contains(x, y) || _scrollbar.containsPoint(x, y))
                {
                    _scrolling = true;
                }
            }
        }

        public override void leftClickHeld(int x, int y)
        {
            base.leftClickHeld(x, y);

            var entries = ActiveEntries;
            int maxLines = GetVisibleLineCount();

            if (_scrolling && entries.Count > maxLines)
            {
                int yPos = Math.Max(_scrollbarRunner.Y,
                    Math.Min(y, _scrollbarRunner.Bottom - _scrollbar.bounds.Height));

                float pct = (float)(yPos - _scrollbarRunner.Y) /
                            (_scrollbarRunner.Height - _scrollbar.bounds.Height);

                _startIndex = (int)(pct * (entries.Count - maxLines));

                SetScrollbarPosition();
                RefreshActionButtons();
            }
        }

        public override void releaseLeftClick(int x, int y)
        {
            base.releaseLeftClick(x, y);
            _scrolling = false;
        }

        private void OpenAddMemoryDialog()
        {
            if (ActiveEntries.Count >= MaxEntriesForTab)
            {
                Game1.playSound("cancel");

                // 🌟 不再使用 Game1.drawObjectDialogue，避免吞掉当前菜单
                Game1.addHUDMessage(new HUDMessage(I18n.Memory.AddFailedFull(MaxEntriesForTab), 0));
                return;
            }

            Game1.playSound("bigSelect");
            Game1.activeClickableMenu = new AddMemoryInputMenu(_npcName, this, null, _currentTab);
        }

        private void ConfirmDeleteMemory(MemoryEntry entry)
        {
            int tabSnapshot = _currentTab;

            Game1.activeClickableMenu = new ConfirmationDialog(
                I18n.Memory.DeleteConfirm(entry.Content),
                _ =>
                {
                    if (tabSnapshot == 0)
                        MemoryManager.Instance.RemoveMemory(_npcName, entry.Id);
                    else
                        WorldMemoryManager.Instance.RemoveEntry(entry.Id);

                    Game1.playSound("trashcan");
                    RefreshEntries();
                    Game1.activeClickableMenu = this;
                },
                _ =>
                {
                    Game1.activeClickableMenu = this;
                });
        }

        public override void draw(SpriteBatch b)
        {
            int mx = Game1.getMouseX();
            int my = Game1.getMouseY();

            b.Draw(Game1.fadeToBlackRect,
                Game1.graphics.GraphicsDevice.Viewport.Bounds, Color.Black * 0.4f);

            IClickableMenu.drawTextureBox(b,
                xPositionOnScreen - 16, yPositionOnScreen - 16,
                width + 32, height + 32, Color.White);

            IClickableMenu.drawTextureBox(b,
                xPositionOnScreen - 8, yPositionOnScreen - 8,
                width + 16, height + 16, Color.White);

            Game1.drawDialogueBox(
                xPositionOnScreen, yPositionOnScreen, width, height, false, true);

            string title = I18n.Memory.MenuTitle();
            var titleSize = Game1.dialogueFont.MeasureString(title);

            b.DrawString(Game1.dialogueFont, title,
                new Vector2(xPositionOnScreen + (width - titleSize.X) / 2f,
                            yPositionOnScreen + 20),
                Game1.textColor);

            DrawTab(b, _tabNpcRect, I18n.Memory.TabNpc(_npcName), _currentTab == 0, mx, my);
            DrawTab(b, _tabWorldRect, I18n.Memory.TabWorld(), _currentTab == 1, mx, my);

            if (_currentTab == 0) DrawCallsignButton(b, mx, my);

            b.Draw(Game1.staminaRect,
                new Rectangle(xPositionOnScreen + LeftPadding,
                              yPositionOnScreen + TabBarY + TabHeight + 4,
                              width - LeftPadding - RightPadding, 2),
                Color.Gray * 0.5f);

            var entries = ActiveEntries;
            int visibleCount = GetVisibleLineCount();

            _hoveredRow = -1;

            if (entries.Count == 0)
            {
                string hint = _currentTab == 0
                    ? I18n.Memory.Empty()
                    : I18n.Memory.WorldEmpty();

                var hintSize = Game1.dialogueFont.MeasureString(hint);

                b.DrawString(Game1.dialogueFont, hint,
                    new Vector2(xPositionOnScreen + (width - hintSize.X) / 2f,
                                yPositionOnScreen + TopPadding + 60),
                    Color.Gray);
            }
            else
            {
                for (int i = 0; i < visibleCount && _startIndex + i < entries.Count; i++)
                {
                    int idx   = _startIndex + i;
                    var entry = entries[idx];
                    int rowY  = yPositionOnScreen + TopPadding + 10 + i * LineHeight;

                    var rowRect = new Rectangle(
                        xPositionOnScreen + LeftPadding - 16, rowY - 4,
                        width - LeftPadding - RightPadding + 16, LineHeight - 2);

                    if (rowRect.Contains(mx, my))
                    {
                        _hoveredRow = i;
                        b.Draw(Game1.staminaRect, rowRect, new Color(70, 130, 180) * 0.18f);
                    }

                    // 🌟 区分手动/自动记忆
                    bool isAuto = entry.Source == "Auto";
                    string prefix = isAuto ? "[自动] " : "";
                    string text = $"{idx + 1}. {prefix}{entry.Content}";
                    Color textColor = isAuto ? new Color(120, 140, 160) : Game1.textColor;

                    b.DrawString(Game1.dialogueFont, text,
                        new Vector2(xPositionOnScreen + LeftPadding, rowY),
                        textColor);

                    string dateText = entry.CreatedAt.ToString("yyyy-MM-dd HH:mm");
                    var    dateSize = Game1.smallFont.MeasureString(dateText);

                    b.DrawString(Game1.smallFont, dateText,
                        new Vector2(xPositionOnScreen + width - dateSize.X - RightPadding - 110, rowY + 4),
                        Color.Gray);

                    if (i < _editButtons.Count)   _editButtons[i].draw(b);
                    if (i < _deleteButtons.Count) _deleteButtons[i].draw(b);
                }

                if (entries.Count > visibleCount)
                {
                    UiHelper.UpdateButtonScale(ref _upArrowHoverScale, _upArrow, mx, my);
                    UiHelper.UpdateButtonScale(ref _downArrowHoverScale, _downArrow, mx, my);

                    _upArrow.scale = _upArrowBaseScale * _upArrowHoverScale;
                    _downArrow.scale = _downArrowBaseScale * _downArrowHoverScale;

                    _upArrow.draw(b);
                    _downArrow.draw(b);

                    IClickableMenu.drawTextureBox(b, Game1.mouseCursors,
                        new Rectangle(403, 383, 6, 6),
                        _scrollbarRunner.X, _scrollbarRunner.Y,
                        _scrollbarRunner.Width, _scrollbarRunner.Height,
                        Color.White, 4f, false);

                    SetScrollbarPosition();
                    _scrollbar.draw(b);
                }
            }

            string cap = $"{entries.Count} / {MaxEntriesForTab}";

            b.DrawString(Game1.smallFont, cap,
                new Vector2(xPositionOnScreen + width - RightPadding - 120,
                            yPositionOnScreen + TopPadding - 20),
                Color.Gray);

            string addText = _currentTab == 0
                ? I18n.Memory.AddButton()
                : I18n.Memory.AddWorldButton();

            bool addHover = _addButtonRect.Contains(mx, my);
            Color addColor = addHover ? Color.Gold : Color.White;

            IClickableMenu.drawTextureBox(b,
                _addButtonRect.X, _addButtonRect.Y,
                _addButtonRect.Width, _addButtonRect.Height, addColor);

            var addLabelSize = Game1.smallFont.MeasureString(addText);

            b.DrawString(Game1.smallFont, addText,
                new Vector2(
                    _addButtonRect.X + (_addButtonRect.Width - addLabelSize.X) / 2f,
                    _addButtonRect.Y + (_addButtonRect.Height - addLabelSize.Y) / 2f),
                Game1.textColor);

            UiHelper.UpdateButtonScale(ref _closeButtonHoverScale, _closeButton, mx, my);
            _closeButton.scale = _closeButtonBaseScale * _closeButtonHoverScale;
            _closeButton.draw(b);

            base.draw(b);
            drawMouse(b);
        }

        private static void DrawTab(
            SpriteBatch b, Rectangle rect, string label,
            bool isActive, int mx, int my)
        {
            Color bg = isActive ? new Color(210, 180, 140)
                     : rect.Contains(mx, my) ? new Color(255, 235, 205)
                     : new Color(139, 90, 43);

            IClickableMenu.drawTextureBox(b, Game1.mouseCursors,
                new Rectangle(432, 439, 9, 9),
                rect.X, rect.Y, rect.Width, rect.Height,
                bg, 4f, false);

            var labelSize = Game1.smallFont.MeasureString(label);

            b.DrawString(Game1.smallFont, label,
                new Vector2(
                    rect.X + (rect.Width - labelSize.X) / 2f,
                    rect.Y + (rect.Height - labelSize.Y) / 2f),
                isActive ? Game1.textColor : Color.White);
        }

        protected override void cleanupBeforeExit()
        {
            base.cleanupBeforeExit();

            if (_ownerMenu != null)
                Game1.activeClickableMenu = _ownerMenu;
        }

        private void DrawCallsignButton(SpriteBatch b, int mx, int my)
        {
            bool hover     = _callsignRect.Contains(mx, my);
            string callsign = MemoryManager.Instance.GetCustomCallsign(_npcName);
            bool hasValue  = !string.IsNullOrEmpty(callsign);

            Color bg = hover ? new Color(255, 235, 205) : new Color(210, 180, 140) * 0.8f;
            IClickableMenu.drawTextureBox(b, Game1.mouseCursors,
                new Rectangle(432, 439, 9, 9),
                _callsignRect.X, _callsignRect.Y, _callsignRect.Width, _callsignRect.Height,
                bg, 4f, false);

            bool isZh = LocalizedContentManager.CurrentLanguageCode == LocalizedContentManager.LanguageCode.zh;
            string prefix    = isZh ? "称呼: " : "Call me: ";
            string valueText = hasValue ? $"[{callsign}]" : (isZh ? "(点击设置)" : "(Click to set)");
            Color  valueColor = hover
                ? Game1.textColor
                : (hasValue ? Game1.textColor * 0.9f : Color.Gray);

            string fullText = prefix + valueText;
            var    textSize = Game1.smallFont.MeasureString(fullText);

            b.DrawString(Game1.smallFont, fullText,
                new Vector2(
                    _callsignRect.X + (_callsignRect.Width  - textSize.X) / 2f,
                    _callsignRect.Y + (_callsignRect.Height - textSize.Y) / 2f),
                valueColor);
        }
    }

    // ─── 专属称谓设置对话框 ──────────────────────────────────────────
    internal class SetCallsignInputMenu : IClickableMenu
    {
        private readonly string _npcName;
        private readonly ScrollableMemoryMenu _returnMenu;
        private readonly DialogueTextInputBox _inputBox;
        private readonly ClickableTextureComponent _okButton;
        private readonly ClickableTextureComponent _cancelButton;

        private float _okButtonHoverScale = 1f;
        private float _cancelButtonHoverScale = 1f;
        private const int MenuWidth  = 560;
        private const int MenuHeight = 240;

        public SetCallsignInputMenu(string npcName, ScrollableMemoryMenu returnMenu)
        {
            _npcName    = npcName;
            _returnMenu = returnMenu;

            xPositionOnScreen = (Game1.uiViewport.Width  - MenuWidth)  / 2;
            yPositionOnScreen = (Game1.uiViewport.Height - MenuHeight) / 2;
            width  = MenuWidth;
            height = MenuHeight;

            _inputBox = new DialogueTextInputBox(MemoryManager.MaxCallsignLength, 15)
            {
                Position  = new Vector2(xPositionOnScreen + 40, yPositionOnScreen + 100),
                Extent    = new Vector2(width - 80, 50),
                Font      = Game1.dialogueFont,
                TextColor = Game1.textColor,
                Selected  = true
            };

            string current = MemoryManager.Instance.GetCustomCallsign(_npcName);
            if (!string.IsNullOrEmpty(current))
                _inputBox.SetText(current);

            _inputBox.OnSubmit += sender => Submit(sender.Text);
            Game1.keyboardDispatcher.Subscriber = _inputBox;

            int btnY = yPositionOnScreen + height - 70;
            _okButton = new ClickableTextureComponent(
                new Rectangle(xPositionOnScreen + width - 2 * 24 - 54, btnY, 54, 54),
                Game1.mouseCursors,
                Game1.getSourceRectForStandardTileSheet(Game1.mouseCursors, 46, -1, -1), 0.9f);

            _cancelButton = new ClickableTextureComponent(
                new Rectangle(xPositionOnScreen + width - 3 * 24 - 2 * 54, btnY, 54, 54),
                Game1.mouseCursors,
                Game1.getSourceRectForStandardTileSheet(Game1.mouseCursors, 47, -1, -1), 0.9f);

            // 任何关闭路径（含手柄B键）都通过 exitFunction 归还键盘焦点
            exitFunction = () =>
            {
                if (Game1.keyboardDispatcher.Subscriber == _inputBox)
                    Game1.keyboardDispatcher.Subscriber = null;
            };
        }

        private void Submit(string text)
        {
            MemoryManager.Instance.SetCustomCallsign(_npcName, text);
            Game1.playSound("coin");
            ReturnToMemoryMenu();
        }

        private void ReturnToMemoryMenu()
        {
            // exitFunction 已负责清理键盘焦点，exitThisMenu 会触发它
            exitThisMenu();
            Game1.activeClickableMenu = _returnMenu;
        }

        public override void receiveLeftClick(int x, int y, bool playSound = true)
        {
            base.receiveLeftClick(x, y, playSound);

            if (_inputBox.ReceiveLeftClick(x, y)) return;

            if (_okButton.containsPoint(x, y))
            {
                Submit(_inputBox.Text);
            }
            else if (_cancelButton.containsPoint(x, y))
            {
                Game1.playSound("bigDeSelect");
                ReturnToMemoryMenu();
            }
        }

        public override void receiveKeyPress(Keys key)
        {
            if (Game1.keyboardDispatcher.Subscriber == _inputBox)
            {
                if (key == Keys.Escape)
                {
                    Game1.playSound("bigDeSelect");
                    ReturnToMemoryMenu();
                    return;
                }

                if (key == Keys.Enter)
                {
                    Submit(_inputBox.Text);
                    return;
                }

                if (!DialogueTextInputBox.IsControlKeyDown())
                    _inputBox.RecieveSpecialInput(key);

                return;
            }

            base.receiveKeyPress(key);
        }

        public override void draw(SpriteBatch b)
        {
            _inputBox.Update(Game1.currentGameTime);

            b.Draw(Game1.fadeToBlackRect, Game1.graphics.GraphicsDevice.Viewport.Bounds, Color.Black * 0.4f);
            IClickableMenu.drawTextureBox(b, xPositionOnScreen, yPositionOnScreen, width, height, Color.White);

            bool isZh  = LocalizedContentManager.CurrentLanguageCode == LocalizedContentManager.LanguageCode.zh;
            string title = isZh
                ? $"设置 {_npcName} 对你的专属称谓"
                : $"Set {_npcName}'s callsign for you";
            string hint  = isZh
                ? "留空则使用默认名字。称谓将在心声与对话中生效。"
                : "Leave blank for default. Used in barks and dialogues.";

            var titleSize = Game1.dialogueFont.MeasureString(title);
            b.DrawString(Game1.dialogueFont, title,
                new Vector2(xPositionOnScreen + (width - titleSize.X) / 2f, yPositionOnScreen + 18),
                Game1.textColor);

            var hintSize = Game1.smallFont.MeasureString(hint);
            b.DrawString(Game1.smallFont, hint,
                new Vector2(xPositionOnScreen + (width - hintSize.X) / 2f, yPositionOnScreen + 58),
                Color.Gray);

            _inputBox.Draw(b);

            int mx = Game1.getMouseX();
            int my = Game1.getMouseY();

            UiHelper.UpdateButtonScale(ref _okButtonHoverScale, _okButton, mx, my);
            UiHelper.UpdateButtonScale(ref _cancelButtonHoverScale, _cancelButton, mx, my);

            _okButton.scale = 0.9f * _okButtonHoverScale;
            _cancelButton.scale = 0.9f * _cancelButtonHoverScale;

            _okButton.draw(b);
            _cancelButton.draw(b);
            drawMouse(b);
        }
    }

    // ─── 添加/编辑记忆对话框 ──────────────────────────────────────────
    internal class AddMemoryInputMenu : IClickableMenu
    {
        private readonly string _npcName;
        private readonly ScrollableMemoryMenu _returnMenu;
        private readonly DialogueTextInputBox _inputBox;
        private readonly ClickableTextureComponent _okButton;
        private readonly ClickableTextureComponent _cancelButton;
        private readonly MemoryEntry _existingEntry;
        private readonly int _tab;

        private const int MenuWidth = 600;
        private const int MenuHeight = 280;
        private const int CharacterLimit = 60;
        private const int WarningThreshold = 50;

        private float _okButtonHoverScale = 1f;
        private float _cancelButtonHoverScale = 1f;

        private readonly float _okButtonBaseScale;
        private readonly float _cancelButtonBaseScale;

        public AddMemoryInputMenu(
            string npcName,
            ScrollableMemoryMenu returnMenu,
            MemoryEntry existingEntry = null,
            int tab = 0)
        {
            _npcName = npcName;
            _returnMenu = returnMenu;
            _existingEntry = existingEntry;
            _tab = tab;

            xPositionOnScreen = (Game1.uiViewport.Width - MenuWidth) / 2;
            yPositionOnScreen = (Game1.uiViewport.Height - MenuHeight) / 2;
            width = MenuWidth;
            height = MenuHeight;

            int lineHeight = Game1.dialogueFont.LineSpacing;

            _inputBox = new DialogueTextInputBox(CharacterLimit, WarningThreshold)
            {
                Position = new Vector2(xPositionOnScreen + 40,
                                       yPositionOnScreen + 100 + lineHeight),
                Extent = new Vector2(width - 80, 60),
                Font = Game1.dialogueFont,
                TextColor = Game1.textColor,
                Selected = true
            };

            if (_existingEntry != null)
                _inputBox.SetText(_existingEntry.Content);

            _inputBox.OnSubmit += sender => Submit(sender.Text);

            Game1.keyboardDispatcher.Subscriber = _inputBox;

            int btnY = yPositionOnScreen + height - 80 + lineHeight;

            _okButton = new ClickableTextureComponent(
                new Rectangle(xPositionOnScreen + width - 2 * 24 - 64, btnY, 64, 64),
                Game1.mouseCursors,
                Game1.getSourceRectForStandardTileSheet(Game1.mouseCursors, 46, -1, -1), 1f);

            _okButtonBaseScale = 1f;

            _cancelButton = new ClickableTextureComponent(
                new Rectangle(xPositionOnScreen + width - 3 * 24 - 2 * 64, btnY, 64, 64),
                Game1.mouseCursors,
                Game1.getSourceRectForStandardTileSheet(Game1.mouseCursors, 47, -1, -1), 1f);

            _cancelButtonBaseScale = 1f;
        }

        // 🌟 统一返回记忆菜单，并清理键盘焦点
        private void ReturnToMemoryMenu(bool refresh)
        {
            if (Game1.keyboardDispatcher.Subscriber == _inputBox)
                Game1.keyboardDispatcher.Subscriber = null;

            if (refresh)
                _returnMenu.RefreshEntries();

            Game1.activeClickableMenu = _returnMenu;
        }

        // 🌟 错误提示改用 HUD，不再吞菜单
        private void ShowErrorHud(string message)
        {
            Game1.playSound("cancel");
            Game1.addHUDMessage(new HUDMessage(message, 0));
        }

        private void Submit(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                Game1.playSound("cancel");
                ReturnToMemoryMenu(false);
                return;
            }

            string trimmed = text.Trim();
            MemoryOperationResult result;

            if (_tab == 1)
            {
                result = _existingEntry != null
                    ? WorldMemoryManager.Instance.EditEntry(_existingEntry.Id, trimmed)
                    : WorldMemoryManager.Instance.AddEntry(trimmed);
            }
            else
            {
                result = _existingEntry != null
                    ? MemoryManager.Instance.EditMemory(_npcName, _existingEntry.Id, trimmed)
                    : MemoryManager.Instance.AddMemory(_npcName, trimmed);
            }

            int maxLen = _tab == 1
                ? WorldMemoryManager.MaxEntryLength
                : MemoryManager.Instance.GetMaxMemoryLength();

            int maxCount = _tab == 1
                ? WorldMemoryManager.MaxEntries
                : MemoryManager.MaxMemoriesPerNpc;

            switch (result)
            {
                case MemoryOperationResult.Success:
                    Game1.playSound("coin");
                    break;

                case MemoryOperationResult.CapacityFull:
                    ShowErrorHud(I18n.Memory.AddFailedFull(maxCount));
                    return;

                case MemoryOperationResult.TooLong:
                    ShowErrorHud(I18n.Memory.AddFailedTooLong(maxLen));
                    return;

                case MemoryOperationResult.Duplicate:
                    ShowErrorHud(I18n.Memory.AddFailedDuplicate());
                    return;

                default:
                    ShowErrorHud(I18n.Memory.AddFailedDuplicate());
                    return;
            }

            ReturnToMemoryMenu(true);
        }

        public override void receiveScrollWheelAction(int direction)
        {
            base.receiveScrollWheelAction(direction);
            _inputBox.ReceiveScrollWheel(direction);
        }

        public override void receiveLeftClick(int x, int y, bool playSound = true)
        {
            base.receiveLeftClick(x, y, playSound);

            if (_inputBox.ReceiveLeftClick(x, y)) return;

            if (_inputBox.ContainsPoint(x, y))
            {
                Game1.keyboardDispatcher.Subscriber = _inputBox;
                _inputBox.Selected = true;
                return;
            }

            if (_okButton.containsPoint(x, y))
            {
                Game1.playSound("coin");
                Submit(_inputBox.Text);
            }
            else if (_cancelButton.containsPoint(x, y))
            {
                Game1.playSound("bigDeSelect");
                ReturnToMemoryMenu(false);
            }
        }

        public override void receiveKeyPress(Keys key)
        {
            if (Game1.keyboardDispatcher.Subscriber == _inputBox)
            {
                if (key == Keys.Escape)
                {
                    Game1.playSound("bigDeSelect");
                    ReturnToMemoryMenu(false);
                    return;
                }

                if (key == Keys.Enter)
                {
                    Game1.playSound("coin");
                    Submit(_inputBox.Text);
                    return;
                }

                if (!DialogueTextInputBox.IsControlKeyDown())
                    _inputBox.RecieveSpecialInput(key);

                return;
            }

            base.receiveKeyPress(key);
        }

        public override void draw(SpriteBatch b)
        {
            _inputBox.Update(Game1.currentGameTime);

            b.Draw(Game1.fadeToBlackRect,
                Game1.graphics.GraphicsDevice.Viewport.Bounds, Color.Black * 0.4f);

            IClickableMenu.drawTextureBox(b,
                xPositionOnScreen, yPositionOnScreen, width, height, Color.White);

            string title = _tab == 1
                ? (_existingEntry != null ? I18n.Memory.WorldEditTitle() : I18n.Memory.WorldAddTitle())
                : (_existingEntry != null
                    ? I18n.Memory.EditTitle(_npcName)
                    : I18n.Memory.AddTitle(_npcName));

            var titleSize = Game1.dialogueFont.MeasureString(title);

            b.DrawString(Game1.dialogueFont, title,
                new Vector2(xPositionOnScreen + (width - titleSize.X) / 2f,
                            yPositionOnScreen + 20),
                Game1.textColor);

            string hint = _tab == 1
                ? I18n.Memory.WorldAddHint(WorldMemoryManager.MaxEntryLength)
                : I18n.Memory.AddHint();

            var hintSize = Game1.smallFont.MeasureString(hint);

            b.DrawString(Game1.smallFont, hint,
                new Vector2(xPositionOnScreen + (width - hintSize.X) / 2f,
                            yPositionOnScreen + 20 + Game1.dialogueFont.LineSpacing),
                Color.Gray);

            _inputBox.Draw(b);

            int mx = Game1.getMouseX();
            int my = Game1.getMouseY();

            UiHelper.UpdateButtonScale(ref _okButtonHoverScale, _okButton, mx, my);
            UiHelper.UpdateButtonScale(ref _cancelButtonHoverScale, _cancelButton, mx, my);

            _okButton.scale = _okButtonBaseScale * _okButtonHoverScale;
            _cancelButton.scale = _cancelButtonBaseScale * _cancelButtonHoverScale;

            _okButton.draw(b);
            _cancelButton.draw(b);

            drawMouse(b);
        }

        protected override void cleanupBeforeExit()
        {
            base.cleanupBeforeExit();

            if (Game1.keyboardDispatcher.Subscriber == _inputBox)
                Game1.keyboardDispatcher.Subscriber = null;
        }
    }
}