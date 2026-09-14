using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewValley;
using StardewValley.Menus;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ValleytalkReborn
{
    /// <summary>
    /// 四合一综合管理面板：NPC记忆 / 世界记忆 / 农夫档案 / 高级设置。
    /// 实现 IMemoryRefreshTarget，使子对话框（AddMemoryInputMenu / SetCallsignInputMenu）
    /// 返回后可通过接口刷新列表，而不依赖具体菜单类型（VT-HUB-103-R1）。
    /// </summary>
    internal class IntegratedHubMenu : IClickableMenu, IMemoryRefreshTarget
    {
        // ── 布局常量（沿用 ScrollableMemoryMenu） ──────────────────────
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

        // ── 状态 ────────────────────────────────────────────────────────
        private int _currentTab;
        private string _currentNpcName;
        private List<MemoryEntry> _cachedEntries = new List<MemoryEntry>();
        private int _startIndex;
        private int _listTopY; // 列表区顶部 Y（Tab0 需为下拉/称谓腾出子标题行）

        private readonly List<ClickableTextureComponent> _deleteButtons = new List<ClickableTextureComponent>();
        private readonly List<ClickableTextureComponent> _editButtons = new List<ClickableTextureComponent>();

        private ClickableTextureComponent _closeButton;
        private ClickableTextureComponent _upArrow;
        private ClickableTextureComponent _downArrow;
        private ClickableTextureComponent _scrollbar;
        private Rectangle _scrollbarRunner;
        private Rectangle _addButtonRect;
        private readonly Rectangle[] _tabRects = new Rectangle[4];
        private Rectangle _callsignRect;

        private DropdownList _npcDropdown;

        private float _closeButtonHoverScale;
        private float _upArrowHoverScale;
        private float _downArrowHoverScale;
        private readonly float _closeButtonBaseScale;
        private readonly float _upArrowBaseScale;
        private readonly float _downArrowBaseScale;

        public IntegratedHubMenu(string initialNpcName, int initialTab)
        {
            // 尺寸（R1 修订：高度上限 680，下限 480）
            width = Math.Max(640, Math.Min(1000, Game1.uiViewport.Width - 80));
            height = Math.Max(480, Math.Min(680, Game1.uiViewport.Height - 80));
            xPositionOnScreen = (Game1.uiViewport.Width - width) / 2;
            yPositionOnScreen = (Game1.uiViewport.Height - height) / 2;

            int tabBaseX = xPositionOnScreen + LeftPadding;
            int tabBaseY = yPositionOnScreen + TabBarY;

            // 四个 Tab 矩形
            _tabRects[0] = new Rectangle(tabBaseX, tabBaseY, TabWidth, TabHeight);
            _tabRects[1] = new Rectangle(tabBaseX + (TabWidth + TabGap) * 1, tabBaseY, TabWidth, TabHeight);
            _tabRects[2] = new Rectangle(tabBaseX + (TabWidth + TabGap) * 2, tabBaseY, TabWidth, TabHeight);
            _tabRects[3] = new Rectangle(tabBaseX + (TabWidth + TabGap) * 3, tabBaseY, TabWidth, TabHeight);

            // 关闭按钮
            _closeButton = new ClickableTextureComponent(
                new Rectangle(xPositionOnScreen + width - 60, yPositionOnScreen + 16, 44, 44),
                Game1.mouseCursors, new Rectangle(337, 494, 12, 12), 3.5f);
            _closeButtonBaseScale = 3.5f;
            _closeButton.hoverText = I18n.Memory.CloseButton();

            // 添加按钮
            _addButtonRect = new Rectangle(
                xPositionOnScreen + width / 2 - 150,
                yPositionOnScreen + height - 60, 300, 48);

            // 上下箭头
            _upArrow = new ClickableTextureComponent(
                new Rectangle(xPositionOnScreen + width - 48, yPositionOnScreen + TopPadding, 44, 48),
                Game1.mouseCursors, new Rectangle(421, 459, 11, 12), 4f);
            _upArrowBaseScale = 4f;

            _downArrow = new ClickableTextureComponent(
                new Rectangle(xPositionOnScreen + width - 48, yPositionOnScreen + height - BottomPadding, 44, 48),
                Game1.mouseCursors, new Rectangle(421, 472, 11, 12), 4f);
            _downArrowBaseScale = 4f;

            // 滚动条
            _scrollbarRunner = new Rectangle(
                xPositionOnScreen + width - 32,
                yPositionOnScreen + TopPadding + 50,
                12, height - TopPadding - BottomPadding - 80);
            _scrollbar = new ClickableTextureComponent(
                new Rectangle(_scrollbarRunner.X - 6, _scrollbarRunner.Y, 24, 40),
                Game1.mouseCursors, new Rectangle(435, 463, 6, 10), 4f);

            // Tab0 子标题行：左侧 NPC 下拉，右侧称谓按钮
            var dropdownRect = new Rectangle(tabBaseX, yPositionOnScreen + TopPadding, 280, TabHeight);
            _callsignRect = new Rectangle(
                xPositionOnScreen + width - RightPadding - 60 - 280,
                yPositionOnScreen + TopPadding, 280, TabHeight);

            _currentNpcName = initialNpcName;

            _npcDropdown = new DropdownList(dropdownRect);
            _npcDropdown.HeaderPrefix = T("Hub.SelectNpcLabel", "NPC: ");
            _npcDropdown.OnItemSelected = name => SelectNpc(name);
            BuildNpcDropdownItems();

            exitFunction = () => Game1.playSound("bigDeSelect");

            // 直接设置 _currentTab 并刷新，避免 SwitchTab 在 initialTab==0 时提前返回导致 RefreshEntries 未执行
            _currentTab = Math.Clamp(initialTab, 0, 3);
            RefreshEntries();
        }

        // ── 数据 ────────────────────────────────────────────────────────

        private List<MemoryEntry> ActiveEntries => _cachedEntries;

        private int MaxEntriesForTab =>
            _currentTab == 0 ? MemoryManager.MaxMemoriesPerNpc
            : _currentTab == 1 ? WorldMemoryManager.MaxEntries
            : 0;

        public void RefreshEntries()
        {
            _cachedEntries = _currentTab switch
            {
                0 => SafeGetMemories(),
                1 => SafeGetWorldEntries(),
                _ => new List<MemoryEntry>()
            };

            // 列表区顶部：Tab0 为下拉/称谓子标题腾出一行
            _listTopY = yPositionOnScreen + TopPadding + (_currentTab == 0 ? TabHeight + 8 : 0);

            ClampStartIndex();
            RefreshActionButtons();
            PositionScrollComponents();
        }

        private List<MemoryEntry> SafeGetMemories()
        {
            if (string.IsNullOrEmpty(_currentNpcName))
                return new List<MemoryEntry>();
            var list = MemoryManager.Instance.GetMemories(_currentNpcName);
            if (list == null)
                return new List<MemoryEntry>();
            return list;
        }

        private List<MemoryEntry> SafeGetWorldEntries()
        {
            var list = WorldMemoryManager.Instance.GetEntries();
            return list ?? new List<MemoryEntry>();
        }

        private void SwitchTab(int tab)
        {
            if (_currentTab == tab) return;
            _currentTab = tab;
            _startIndex = 0;
            Game1.playSound("smallSelect");
            RefreshEntries();
        }

        private void SelectNpc(string internalName)
        {
            _currentNpcName = internalName;
            _startIndex = 0;
            Game1.playSound("bigSelect");
            RefreshEntries();
        }

        private void BuildNpcDropdownItems()
        {
            var items = new List<(string Id, string Label)>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (!string.IsNullOrWhiteSpace(_currentNpcName))
            {
                string label = Game1.getCharacterFromName(_currentNpcName)?.displayName ?? _currentNpcName;
                items.Add((_currentNpcName, label));
                seen.Add(_currentNpcName);
            }

            var keys = Game1.player.friendshipData.Keys
                .Where(k => !seen.Contains(k))
                .OrderBy(k => k, StringComparer.OrdinalIgnoreCase);
            foreach (var k in keys)
            {
                string label = Game1.getCharacterFromName(k)?.displayName ?? k;
                items.Add((k, label));
                seen.Add(k);
            }

            _npcDropdown.SetItems(items, _currentNpcName);
        }

        private void OpenAddMemory()
        {
            if (_currentTab == 0 && string.IsNullOrEmpty(_currentNpcName))
            {
                Game1.playSound("cancel");
                return;
            }

            if (ActiveEntries.Count >= MaxEntriesForTab)
            {
                Game1.playSound("cancel");
                Game1.addHUDMessage(new HUDMessage(I18n.Memory.AddFailedFull(MaxEntriesForTab), 0));
                return;
            }

            Game1.playSound("bigSelect");
            Game1.activeClickableMenu = new AddMemoryInputMenu(_currentNpcName, this, null, _currentTab);
        }

        private void OpenEditMemory(MemoryEntry e)
        {
            Game1.activeClickableMenu = new AddMemoryInputMenu(_currentNpcName, this, e, _currentTab);
        }

        private void ConfirmDeleteMemory(MemoryEntry entry)
        {
            int tabSnapshot = _currentTab;

            Game1.activeClickableMenu = new ConfirmationDialog(
                I18n.Memory.DeleteConfirm(entry.Content),
                _ =>
                {
                    if (tabSnapshot == 0)
                        MemoryManager.Instance.RemoveMemory(_currentNpcName, entry.Id);
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

        // ── 输入 ────────────────────────────────────────────────────────

        public override void receiveScrollWheelAction(int direction)
        {
            base.receiveScrollWheelAction(direction);

            if (_npcDropdown.IsOpen)
            {
                _npcDropdown.ReceiveScrollWheel(direction);
                return;
            }

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

            // 下拉展开时优先消费点击
            if (_npcDropdown.IsOpen && _npcDropdown.ReceiveLeftClick(x, y))
                return;

            if (_closeButton.containsPoint(x, y))
            {
                Game1.playSound("bigDeSelect");
                exitThisMenu();
                return;
            }

            for (int t = 0; t < 4; t++)
            {
                if (_tabRects[t].Contains(x, y))
                {
                    SwitchTab(t);
                    return;
                }
            }

            if (_currentTab == 0)
            {
                if (_npcDropdown.HeaderBounds.Contains(x, y))
                {
                    _npcDropdown.ToggleOpen();
                    return;
                }

                if (_callsignRect.Contains(x, y) && !string.IsNullOrEmpty(_currentNpcName))
                {
                    Game1.playSound("bigSelect");
                    Game1.activeClickableMenu = new SetCallsignInputMenu(_currentNpcName, this);
                    return;
                }

                if (_addButtonRect.Contains(x, y))
                {
                    OpenAddMemory();
                    return;
                }

                HandleListRowClicks(x, y);
            }
            else if (_currentTab == 1)
            {
                if (_addButtonRect.Contains(x, y))
                {
                    OpenAddMemory();
                    return;
                }

                HandleListRowClicks(x, y);
            }

            // Tab2 / Tab3：无可交互控件
        }

        private void HandleListRowClicks(int x, int y)
        {
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
                    OpenEditMemory(entries[idx]);
                    return;
                }
            }

            int maxLines = GetVisibleLineCount();
            if (entries.Count <= maxLines) return;

            if (_upArrow.containsPoint(x, y) && _startIndex > 0)
            {
                _startIndex--;
                Game1.playSound("shwip");
                SetScrollbarPosition();
                RefreshActionButtons();
            }
            else if (_downArrow.containsPoint(x, y) && _startIndex < entries.Count - maxLines)
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

        public override void receiveKeyPress(Keys key)
        {
            if (key == Keys.Escape)
            {
                Game1.playSound("bigDeSelect");
                exitThisMenu();
                return;
            }

            base.receiveKeyPress(key);
        }

        // ── 绘制 ────────────────────────────────────────────────────────

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

            Game1.drawDialogueBox(xPositionOnScreen, yPositionOnScreen, width, height, false, true);

            // 标题
            string title = T("Hub.Title", "Management Hub");
            var titleSize = Game1.dialogueFont.MeasureString(title);
            b.DrawString(Game1.dialogueFont, title,
                new Vector2(xPositionOnScreen + (width - titleSize.X) / 2f, yPositionOnScreen + 20),
                Game1.textColor);

            // 四个 Tab
            DrawTab(b, _tabRects[0], T("Hub.TabNpcMemory", "NPC Memories"), _currentTab == 0, mx, my);
            DrawTab(b, _tabRects[1], T("Hub.TabWorldMemory", "World Memories"), _currentTab == 1, mx, my);
            DrawTab(b, _tabRects[2], T("Hub.TabProfile", "Farmer Profile"), _currentTab == 2, mx, my);
            DrawTab(b, _tabRects[3], T("Hub.TabAdvanced", "Advanced Settings"), _currentTab == 3, mx, my);

            // 分隔线
            b.Draw(Game1.staminaRect,
                new Rectangle(xPositionOnScreen + LeftPadding,
                              yPositionOnScreen + TabBarY + TabHeight + 4,
                              width - LeftPadding - RightPadding, 2),
                Color.Gray * 0.5f);

            // Tab 内容
            if (_currentTab == 0)
            {
                DrawCallsignButton(b);
                DrawEntries(b);
            }
            else if (_currentTab == 1)
            {
                DrawEntries(b);
            }
            else
            {
                DrawPlaceholder(b, _currentTab == 2
                    ? T("Hub.TabProfile", "Farmer Profile")
                    : T("Hub.TabAdvanced", "Advanced Settings"));
            }

            // 添加按钮与计数（仅 Tab0/1）
            if (_currentTab == 0 || _currentTab == 1)
                DrawAddButton(b);

            // 关闭按钮
            UiHelper.UpdateButtonScale(ref _closeButtonHoverScale, _closeButton, mx, my);
            _closeButton.scale = _closeButtonBaseScale * _closeButtonHoverScale;
            _closeButton.draw(b);

            // ★ 下拉叠层最后绘制（仅 Tab0），永不被列表/按钮遮挡
            if (_currentTab == 0)
                _npcDropdown.Draw(b);

            base.draw(b);
            drawMouse(b);
        }

        private void DrawPlaceholder(SpriteBatch b, string tabName)
        {
            string text = $"{tabName} — {T("Hub.EmptyNpcList", "No villagers available yet. Talk to or befriend a villager first.")}";
            var size = Game1.smallFont.MeasureString(text);
            b.DrawString(Game1.smallFont, text,
                new Vector2(xPositionOnScreen + (width - size.X) / 2f,
                            yPositionOnScreen + TopPadding + 60),
                Color.Gray);
        }

        private void DrawTab(SpriteBatch b, Rectangle rect, string label, bool isActive, int mx, int my)
        {
            Color bg = isActive ? new Color(210, 180, 140)
                     : rect.Contains(mx, my) ? new Color(255, 235, 205)
                     : new Color(139, 90, 43);
            IClickableMenu.drawTextureBox(b, Game1.mouseCursors,
                new Rectangle(432, 439, 9, 9),
                rect.X, rect.Y, rect.Width, rect.Height, bg, 4f, false);

            var labelSize = Game1.smallFont.MeasureString(label);
            b.DrawString(Game1.smallFont, label,
                new Vector2(rect.X + (rect.Width - labelSize.X) / 2f,
                            rect.Y + (rect.Height - labelSize.Y) / 2f),
                isActive ? Game1.textColor : Color.White);
        }

        private void DrawCallsignButton(SpriteBatch b)
        {
            int mx = Game1.getMouseX(), my = Game1.getMouseY();
            bool hover = _callsignRect.Contains(mx, my);
            string callsign = MemoryManager.Instance.GetCustomCallsign(_currentNpcName);
            bool hasValue = !string.IsNullOrEmpty(callsign);

            Color bg = hover ? new Color(255, 235, 205) : new Color(210, 180, 140) * 0.8f;
            IClickableMenu.drawTextureBox(b, Game1.mouseCursors,
                new Rectangle(432, 439, 9, 9),
                _callsignRect.X, _callsignRect.Y, _callsignRect.Width, _callsignRect.Height,
                bg, 4f, false);

            string prefix = I18n.Memory.CallsignPrefix();
            string valueText = hasValue ? $"[{callsign}]" : I18n.Memory.CallsignUnset();
            Color valueColor = hover ? Game1.textColor : (hasValue ? Game1.textColor * 0.9f : Color.Gray);
            string fullText = prefix + valueText;
            var textSize = Game1.smallFont.MeasureString(fullText);

            b.DrawString(Game1.smallFont, fullText,
                new Vector2(_callsignRect.X + (_callsignRect.Width - textSize.X) / 2f,
                            _callsignRect.Y + (_callsignRect.Height - textSize.Y) / 2f),
                valueColor);
        }

        private void DrawEntries(SpriteBatch b)
        {
            var entries = ActiveEntries;
            int visibleCount = GetVisibleLineCount();

            if (entries.Count == 0)
            {
                string hint = _currentTab == 0
                    ? I18n.Memory.Empty()
                    : I18n.Memory.WorldEmpty();
                var hintSize = Game1.dialogueFont.MeasureString(hint);
                b.DrawString(Game1.dialogueFont, hint,
                    new Vector2(xPositionOnScreen + (width - hintSize.X) / 2f, _listTopY + 60),
                    Color.Gray);
                return;
            }

            for (int i = 0; i < visibleCount && _startIndex + i < entries.Count; i++)
            {
                int idx = _startIndex + i;
                var entry = entries[idx];
                int rowY = _listTopY + 10 + i * LineHeight;

                var rowRect = new Rectangle(
                    xPositionOnScreen + LeftPadding - 16, rowY - 4,
                    width - LeftPadding - RightPadding + 16, LineHeight - 2);

                if (rowRect.Contains(Game1.getMouseX(), Game1.getMouseY()))
                    b.Draw(Game1.staminaRect, rowRect, new Color(70, 130, 180) * 0.18f);

                bool isAuto = entry.Source == "Auto";
                string prefix = isAuto ? I18n.Memory.AutoPrefix() : "";
                string text = $"{idx + 1}. {prefix}{entry.Content}";
                Color textColor = isAuto ? new Color(120, 140, 160) : Game1.textColor;

                b.DrawString(Game1.dialogueFont, text,
                    new Vector2(xPositionOnScreen + LeftPadding, rowY), textColor);

                string dateText = entry.CreatedAt.ToString("yyyy-MM-dd HH:mm");
                var dateSize = Game1.smallFont.MeasureString(dateText);
                b.DrawString(Game1.smallFont, dateText,
                    new Vector2(xPositionOnScreen + width - dateSize.X - RightPadding - 110, rowY + 4),
                    Color.Gray);

                if (i < _editButtons.Count) _editButtons[i].draw(b);
                if (i < _deleteButtons.Count) _deleteButtons[i].draw(b);
            }

            if (entries.Count > visibleCount)
            {
                UiHelper.UpdateButtonScale(ref _upArrowHoverScale, _upArrow, Game1.getMouseX(), Game1.getMouseY());
                UiHelper.UpdateButtonScale(ref _downArrowHoverScale, _downArrow, Game1.getMouseX(), Game1.getMouseY());
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

        private void DrawAddButton(SpriteBatch b)
        {
            string addText = _currentTab == 0 ? I18n.Memory.AddButton() : I18n.Memory.AddWorldButton();
            bool addHover = _addButtonRect.Contains(Game1.getMouseX(), Game1.getMouseY());
            Color addColor = addHover ? Color.Gold : Color.White;

            IClickableMenu.drawTextureBox(b,
                _addButtonRect.X, _addButtonRect.Y,
                _addButtonRect.Width, _addButtonRect.Height, addColor);

            var addLabelSize = Game1.smallFont.MeasureString(addText);
            b.DrawString(Game1.smallFont, addText,
                new Vector2(_addButtonRect.X + (_addButtonRect.Width - addLabelSize.X) / 2f,
                            _addButtonRect.Y + (_addButtonRect.Height - addLabelSize.Y) / 2f),
                Game1.textColor);

            var entries = ActiveEntries;
            string cap = $"{entries.Count} / {MaxEntriesForTab}";
            b.DrawString(Game1.smallFont, cap,
                new Vector2(xPositionOnScreen + width - RightPadding - 120, _listTopY - 20),
                Color.Gray);
        }

        // ── 滚动辅助 ────────────────────────────────────────────────────

        private bool _scrolling;

        private void RefreshActionButtons()
        {
            ClampStartIndex();
            _deleteButtons.Clear();
            _editButtons.Clear();

            var entries = ActiveEntries;
            int visibleCount = GetVisibleLineCount();

            for (int i = 0; i < visibleCount && _startIndex + i < entries.Count; i++)
            {
                int y = _listTopY + 10 + i * LineHeight;

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
            => (height - BottomPadding - _listTopY + yPositionOnScreen) / LineHeight;

        private void PositionScrollComponents()
        {
            int top = _listTopY;
            _upArrow.bounds.Y = top;
            _downArrow.bounds.Y = yPositionOnScreen + height - BottomPadding;
            _scrollbarRunner.Y = top + 50;
            _scrollbarRunner.Height = (yPositionOnScreen + height - BottomPadding) - (top + 50);
            if (_scrollbarRunner.Height < 0) _scrollbarRunner.Height = 0;
            _scrollbar.bounds.Y = _scrollbarRunner.Y;
            SetScrollbarPosition();
        }

        private void SetScrollbarPosition()
        {
            var entries = ActiveEntries;
            int maxLines = GetVisibleLineCount();
            if (entries.Count <= maxLines) return;

            float pct = (float)_startIndex / (entries.Count - maxLines);
            _scrollbar.bounds.Y = _scrollbarRunner.Y +
                (int)(pct * (_scrollbarRunner.Height - _scrollbar.bounds.Height));
        }

        // ── 窗口尺寸变化 ────────────────────────────────────────────────

        public override void gameWindowSizeChanged(Rectangle oldBounds, Rectangle newBounds)
        {
            width = Math.Max(640, Math.Min(1000, Game1.uiViewport.Width - 80));
            height = Math.Max(480, Math.Min(680, Game1.uiViewport.Height - 80));
            xPositionOnScreen = (Game1.uiViewport.Width - width) / 2;
            yPositionOnScreen = (Game1.uiViewport.Height - height) / 2;

            int tabBaseX = xPositionOnScreen + LeftPadding;
            int tabBaseY = yPositionOnScreen + TabBarY;

            _tabRects[0] = new Rectangle(tabBaseX, tabBaseY, TabWidth, TabHeight);
            _tabRects[1] = new Rectangle(tabBaseX + (TabWidth + TabGap) * 1, tabBaseY, TabWidth, TabHeight);
            _tabRects[2] = new Rectangle(tabBaseX + (TabWidth + TabGap) * 2, tabBaseY, TabWidth, TabHeight);
            _tabRects[3] = new Rectangle(tabBaseX + (TabWidth + TabGap) * 3, tabBaseY, TabWidth, TabHeight);

            _closeButton.bounds = new Rectangle(xPositionOnScreen + width - 60, yPositionOnScreen + 16, 44, 44);
            _addButtonRect = new Rectangle(xPositionOnScreen + width / 2 - 150, yPositionOnScreen + height - 60, 300, 48);

            // 更新箭头与滚动条的 X 坐标（跟随新的 width / xPositionOnScreen）
            _upArrow.bounds.X = xPositionOnScreen + width - 48;
            _downArrow.bounds.X = xPositionOnScreen + width - 48;
            _scrollbarRunner.X = xPositionOnScreen + width - 32;
            _scrollbar.bounds.X = _scrollbarRunner.X - 6;

            _npcDropdown.SetHeaderBounds(new Rectangle(tabBaseX, yPositionOnScreen + TopPadding, 280, TabHeight));
            _callsignRect = new Rectangle(
                xPositionOnScreen + width - RightPadding - 60 - 280,
                yPositionOnScreen + TopPadding, 280, TabHeight);

            RefreshEntries();
        }

        protected override void cleanupBeforeExit()
        {
            base.cleanupBeforeExit();
        }

        // ── i18n 转发 ───────────────────────────────────────────────────

        private static string T(string key, string fallback, object tokens = null)
        {
            return ModConfigMenu.GetUIString(key, fallback, tokens);
        }

        // ── 嵌套：NPC 下拉列表 ──────────────────────────────────────────

        private sealed class DropdownList
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
            public int ScrollIndex => _scrollIndex;

            public Rectangle HeaderBounds => _headerRect;

            public DropdownList(Rectangle headerRect, int itemHeight = 44, int maxVisibleItems = 8)
            {
                _headerRect = headerRect;
                _itemHeight = itemHeight;
                _maxVisibleItems = maxVisibleItems;
            }

            public void SetHeaderBounds(Rectangle rect)
            {
                _headerRect.X = rect.X;
                _headerRect.Y = rect.Y;
                _headerRect.Width = rect.Width;
                _headerRect.Height = rect.Height;
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
                if (_isOpen) _scrollIndex = 0;
            }

            public void Close() => _isOpen = false;

            public bool ReceiveLeftClick(int x, int y)
            {
                if (!_isOpen) return false;

                // 命中头部 → 收起
                if (_headerRect.Contains(x, y))
                {
                    _isOpen = false;
                    return true;
                }

                // 命中列表项 → 选中 + 收起 + 回调
                int visible = Math.Min(_maxVisibleItems, _items.Count - _scrollIndex);
                for (int i = 0; i < visible; i++)
                {
                    int iy = _headerRect.Bottom + i * _itemHeight;
                    var ir = new Rectangle(_headerRect.X, iy, _headerRect.Width, _itemHeight);
                    if (ir.Contains(x, y))
                    {
                        _selectedId = _items[_scrollIndex + i].Id;
                        _isOpen = false;
                        OnItemSelected?.Invoke(_selectedId);
                        return true;
                    }
                }

                // 命中外部 → 仅收起
                _isOpen = false;
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

                // 头部背景
                Color headerBg = _isOpen ? new Color(210, 180, 140)
                              : hover ? new Color(255, 235, 205)
                              : new Color(139, 90, 43);
                IClickableMenu.drawTextureBox(b, Game1.mouseCursors,
                    new Rectangle(432, 439, 9, 9),
                    _headerRect.X, _headerRect.Y, _headerRect.Width, _headerRect.Height,
                    headerBg, 4f, false);

                // 头部文字
                string selLabel = _items.FirstOrDefault(it => it.Id == _selectedId).Label ?? "";
                if (string.IsNullOrEmpty(selLabel)) selLabel = "—";
                string label = (HeaderPrefix ?? "") + selLabel + (_isOpen ? " ▴" : " ▾");
                var size = Game1.smallFont.MeasureString(label);
                b.DrawString(Game1.smallFont, label,
                    new Vector2(_headerRect.X + (_headerRect.Width - size.X) / 2f,
                                _headerRect.Y + (_headerRect.Height - size.Y) / 2f),
                    Color.White);

                if (!_isOpen) return;

                // 列表项
                int visible = Math.Min(_maxVisibleItems, _items.Count - _scrollIndex);
                for (int i = 0; i < visible; i++)
                {
                    int idx = _scrollIndex + i;
                    var item = _items[idx];
                    int iy = _headerRect.Bottom + i * _itemHeight;
                    var ir = new Rectangle(_headerRect.X, iy, _headerRect.Width, _itemHeight);

                    bool selected = item.Id == _selectedId;
                    bool ihover = ir.Contains(mx, my);
                    Color bg = selected ? new Color(210, 180, 140)
                             : ihover ? new Color(255, 235, 205)
                             : Color.White;

                    IClickableMenu.drawTextureBox(b, Game1.mouseCursors,
                        new Rectangle(432, 439, 9, 9),
                        ir.X, ir.Y, ir.Width, ir.Height, bg, 4f, false);

                    b.DrawString(Game1.smallFont, item.Label,
                        new Vector2(ir.X + 8, ir.Y + (ir.Height - Game1.smallFont.LineSpacing) / 2f),
                        Color.White);
                }
            }
        }
    }
}
