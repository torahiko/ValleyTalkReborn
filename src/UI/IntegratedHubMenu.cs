using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ValleytalkReborn
{
    /// <summary>
    /// 四合一综合管理面板：NPC记忆 / 世界记忆 / 农夫档案 / 高级设置。
    /// 实现 IMemoryRefreshTarget，使子对话框返回后可通过接口刷新列表。
    /// </summary>
    internal class IntegratedHubMenu : IClickableMenu, IMemoryRefreshTarget
    {
        // ── 布局常量 ──────────────────────────────────────────────────
        private const int TabBarY = 56;
        private const int TabHeight = 36;
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
        private int _listTopY;

        private readonly List<ClickableTextureComponent> _deleteButtons = new List<ClickableTextureComponent>();
        private readonly List<ClickableTextureComponent> _editButtons = new List<ClickableTextureComponent>();

        private ClickableTextureComponent _closeButton;
        private ClickableTextureComponent _upArrow;
        private ClickableTextureComponent _downArrow;
        private ClickableTextureComponent _scrollbar;
        private Rectangle _scrollbarRunner;

        // 按钮交互区
        private Rectangle _addButtonRect;         // 仅 Tab 1（世界记忆）
        private Rectangle _manualAddRect;        // Tab 0 手动录入
        private Rectangle _aiExtractButtonRect;  // Tab 0 AI 提炼

        private readonly Rectangle[] _tabRects = new Rectangle[4];
        private Rectangle _callsignRect;

        private DropdownList _npcDropdown;

        private float _closeButtonHoverScale;
        private float _upArrowHoverScale;
        private float _downArrowHoverScale;
        private readonly float _closeButtonBaseScale;
        private readonly float _upArrowBaseScale;
        private readonly float _downArrowBaseScale;

        // ── Tab2（农夫档案）状态 ────────────────────────────────────────
        private bool _tab2Initialized;
        private int _safetyModeIndex; // 0..3
        private List<(string Id, string Label)> _orientationItems;
        private DropdownList _orientationDropdown;
        private DialogueTextInputBox _bioTextBox;
        private int _tab2RightColW;
        private int _tab2RightColX;
        private Rectangle _enableProfileCheckboxRect;
        private Rectangle _orientationLabelRect;
        private Rectangle _orientationDropdownRect;
        private Rectangle _safetyLabelRect;
        private Rectangle _safetySliderRect;
        private Rectangle _bioLabelRect;
        private Rectangle _bioBoxRect;
        private Rectangle _saveButtonRect;

        // ── Tab3（高级设置）状态 ────────────────────────────────────────
        private Rectangle _tab3RowInfinite;
        private Rectangle _tab3RowVanillaFirst;
        private Rectangle _tab3RowRecordVanilla;
        private Rectangle _tab3CheckboxInfinite;
        private Rectangle _tab3CheckboxVanillaFirst;
        private Rectangle _tab3CheckboxRecordVanilla;

        public IntegratedHubMenu(string initialNpcName, int initialTab)
        {
            // 响应式宽高适配
            width = Math.Max(680, Math.Min(1000, Game1.uiViewport.Width - 80));
            height = Math.Max(500, Math.Min(680, Game1.uiViewport.Height - 80));
            xPositionOnScreen = (Game1.uiViewport.Width - width) / 2;
            yPositionOnScreen = (Game1.uiViewport.Height - height) / 2;

            // 关闭按钮
            _closeButton = new ClickableTextureComponent(
                new Rectangle(xPositionOnScreen + width - 60, yPositionOnScreen + 16, 44, 44),
                Game1.mouseCursors, new Rectangle(337, 494, 12, 12), 3.5f);
            _closeButtonBaseScale = 3.5f;
            _closeButton.hoverText = I18n.Memory.CloseButton();

            // 滚动控件
            _upArrow = new ClickableTextureComponent(
                new Rectangle(xPositionOnScreen + width - 48, yPositionOnScreen + TopPadding, 44, 48),
                Game1.mouseCursors, new Rectangle(421, 459, 11, 12), 4f);
            _upArrowBaseScale = 4f;

            _downArrow = new ClickableTextureComponent(
                new Rectangle(xPositionOnScreen + width - 48, yPositionOnScreen + height - BottomPadding, 44, 48),
                Game1.mouseCursors, new Rectangle(421, 472, 11, 12), 4f);
            _downArrowBaseScale = 4f;

            _scrollbarRunner = new Rectangle(
                xPositionOnScreen + width - 32,
                yPositionOnScreen + TopPadding + 50,
                12, height - TopPadding - BottomPadding - 80);
            _scrollbar = new ClickableTextureComponent(
                new Rectangle(_scrollbarRunner.X - 6, _scrollbarRunner.Y, 24, 40),
                Game1.mouseCursors, new Rectangle(435, 463, 6, 10), 4f);

            _currentNpcName = initialNpcName;
            _npcDropdown = new DropdownList(Rectangle.Empty)
            {
                HeaderPrefix = T("Hub.SelectNpcLabel", "NPC: "),
                OnItemSelected = name => SelectNpc(name)
            };

            // 计算所有自适应坐标
            RecalculateAllLayout();
            BuildNpcDropdownItems();

            exitFunction = () => Game1.playSound("bigDeSelect");

            _currentTab = Math.Clamp(initialTab, 0, 3);
            RefreshEntries();
        }

        // ── 布局统一计算 ────────────────────────────────────────────────

        private void RecalculateAllLayout()
        {
            int tabBaseX = xPositionOnScreen + LeftPadding;
            int tabBaseY = yPositionOnScreen + TabBarY;

            // 1. 4个 Tab 宽度动态等分撑满
            int totalTabSpace = width - LeftPadding - RightPadding;
            int tabW = (totalTabSpace - (TabGap * 3)) / 4;
            for (int i = 0; i < 4; i++)
            {
                _tabRects[i] = new Rectangle(tabBaseX + i * (tabW + TabGap), tabBaseY, tabW, TabHeight);
            }

            // 2. 底部添加与提炼按钮布局（响应式计算）
            int btnY = yPositionOnScreen + height - 60;

            // Tab 1 世界记忆单按钮（居中）
            int singleBtnW = Math.Min(300, totalTabSpace);
            _addButtonRect = new Rectangle(xPositionOnScreen + (width - singleBtnW) / 2, btnY, singleBtnW, 48);

            // Tab 0 双按钮（手动录入 + AI 提炼 并排自适应居中）
            int btnGap = 20;
            int dualBtnW = Math.Min(230, (totalTabSpace - btnGap) / 2);
            int dualTotalW = dualBtnW * 2 + btnGap;
            int dualStartX = xPositionOnScreen + (width - dualTotalW) / 2;

            _manualAddRect = new Rectangle(dualStartX, btnY, dualBtnW, 48);
            _aiExtractButtonRect = new Rectangle(dualStartX + dualBtnW + btnGap, btnY, dualBtnW, 48);

            _listTopY = yPositionOnScreen + TopPadding + (_currentTab == 0 ? TabHeight + 8 : 0);

            // 3. Tab0 子标题：左侧 NPC 下拉与右侧称谓按钮 1:1 对等排布
            int halfW = (totalTabSpace - 16) / 2;
            var dropdownRect = new Rectangle(tabBaseX, yPositionOnScreen + TopPadding, halfW, TabHeight);
            _callsignRect = new Rectangle(tabBaseX + halfW + 16, yPositionOnScreen + TopPadding, halfW, TabHeight);
            _npcDropdown?.SetHeaderBounds(dropdownRect);

            // 4. Tab3 高级设置行
            int leftColX = xPositionOnScreen + LeftPadding;
            int tab3Y = yPositionOnScreen + TopPadding + 8;
            int rowW = width - LeftPadding - RightPadding;
            _tab3RowInfinite = new Rectangle(leftColX - 8, tab3Y - 6, rowW, 58);
            _tab3CheckboxInfinite = new Rectangle(leftColX, tab3Y, 36, 36);

            _tab3RowVanillaFirst = new Rectangle(leftColX - 8, tab3Y + 68 - 6, rowW, 58);
            _tab3CheckboxVanillaFirst = new Rectangle(leftColX, tab3Y + 68, 36, 36);

            _tab3RowRecordVanilla = new Rectangle(leftColX - 8, tab3Y + 136 - 6, rowW, 58);
            _tab3CheckboxRecordVanilla = new Rectangle(leftColX, tab3Y + 136, 36, 36);

            // 5. Tab2 布局重算
            if (_tab2Initialized)
                RecalculateTab2Layout();
        }

        // ── 数据与刷新 ──────────────────────────────────────────────────

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

            _listTopY = yPositionOnScreen + TopPadding + (_currentTab == 0 ? TabHeight + 8 : 0);

            if (_currentTab == 2 && !_tab2Initialized)
                InitializeTab2();

            if (_currentTab == 2 && _bioTextBox != null && _bioTextBox.Selected
                && Game1.keyboardDispatcher.Subscriber == null)
                Game1.keyboardDispatcher.Subscriber = _bioTextBox;

            ClampStartIndex();
            RefreshActionButtons();
            PositionScrollComponents();
        }

        private List<MemoryEntry> SafeGetMemories()
        {
            if (string.IsNullOrEmpty(_currentNpcName))
                return new List<MemoryEntry>();
            var list = MemoryManager.Instance.GetMemories(_currentNpcName);
            return list ?? new List<MemoryEntry>();
        }

        private List<MemoryEntry> SafeGetWorldEntries()
        {
            var list = WorldMemoryManager.Instance.GetEntries();
            return list ?? new List<MemoryEntry>();
        }

        private void SwitchTab(int tab)
        {
            if (_currentTab == tab) return;

            if (_currentTab == 2 && Game1.keyboardDispatcher.Subscriber == _bioTextBox)
                Game1.keyboardDispatcher.Subscriber = null;

            _currentTab = tab;
            _startIndex = 0;
            Game1.playSound("smallSelect");
            RecalculateAllLayout();
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

            int currentCount = _currentTab == 0
                ? MemoryManager.Instance.GetManualMemoryCount(_currentNpcName)
                : ActiveEntries.Count;

            if (currentCount >= MaxEntriesForTab)
            {
                Game1.playSound("cancel");
                Game1.addHUDMessage(new HUDMessage(I18n.Memory.AddFailedFull(MaxEntriesForTab), 0));
                return;
            }

            Game1.playSound("bigSelect");
            ReleaseKeyboard();
            Game1.activeClickableMenu = new AddMemoryInputMenu(_currentNpcName, this, null, _currentTab);
        }

        /// <summary>打开当前选中 NPC 的 AI 提炼面板。</summary>
        private void TryOpenDistillMenu()
        {
            if (string.IsNullOrWhiteSpace(_currentNpcName))
            {
                Game1.playSound("cancel");
                return;
            }

            string displayName = Game1.getCharacterFromName(_currentNpcName)?.displayName ?? _currentNpcName;

            if (DialogueBuilder.Instance?.LlmDisabled == true)
            {
                Game1.playSound("cancel");
                Game1.addHUDMessage(new HUDMessage(I18n.Memory.DistillLlmDisabled(), 3));
                return;
            }

            if (!DialogueHistoryManager.Instance.HasHistory(_currentNpcName))
            {
                Game1.playSound("cancel");
                Game1.addHUDMessage(new HUDMessage(I18n.Memory.DistillNoHistory(displayName), 0));
                return;
            }

            Game1.playSound("bigSelect");
            ReleaseKeyboard();
            Game1.activeClickableMenu = new MemoryDistillMenu(_currentNpcName, this);
        }

        private void OpenEditMemory(MemoryEntry e)
        {
            ReleaseKeyboard();
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

        private void ReleaseKeyboard()
        {
            if (_bioTextBox != null && Game1.keyboardDispatcher.Subscriber == _bioTextBox)
                Game1.keyboardDispatcher.Subscriber = null;
        }

        // ── 输入处理 ────────────────────────────────────────────────────

        public override void receiveScrollWheelAction(int direction)
        {
            base.receiveScrollWheelAction(direction);

            if (_currentTab == 2)
            {
                if (_orientationDropdown.IsOpen)
                {
                    _orientationDropdown.ReceiveScrollWheel(direction);
                    return;
                }
                _bioTextBox?.ReceiveScrollWheel(direction);
                return;
            }

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
                    ReleaseKeyboard();
                    Game1.activeClickableMenu = new SetCallsignInputMenu(_currentNpcName, this);
                    return;
                }

                // 手动录入
                if (_manualAddRect.Contains(x, y))
                {
                    OpenAddMemory();
                    return;
                }

                // AI 提炼
                if (_aiExtractButtonRect.Contains(x, y))
                {
                    TryOpenDistillMenu();
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
            else if (_currentTab == 2)
            {
                HandleTab2Click(x, y);
            }
            else if (_currentTab == 3)
            {
                HandleTab3Click(x, y);
            }
        }

        private void HandleTab2Click(int x, int y)
        {
            if (_orientationDropdown.IsOpen && _orientationDropdown.ReceiveLeftClick(x, y))
                return;

            if (_enableProfileCheckboxRect.Contains(x, y))
            {
                ModEntry.Config.EnablePlayerProfile = !ModEntry.Config.EnablePlayerProfile;
                Game1.playSound("select");
                return;
            }

            if (!ModEntry.Config.EnablePlayerProfile)
                return;

            if (_orientationDropdown.HeaderBounds.Contains(x, y))
            {
                _orientationDropdown.ToggleOpen();
                Game1.playSound("select");
                return;
            }

            if (_safetySliderRect.Contains(x, y))
            {
                int trackX = _safetySliderRect.X;
                int trackW = _safetySliderRect.Width;
                int relativeX = Math.Clamp(x - trackX, 0, trackW);
                _safetyModeIndex = Math.Min(3, (int)(((float)relativeX / trackW) * 4));
                Game1.playSound("select");
                return;
            }

            if (_bioBoxRect.Contains(x, y))
            {
                _bioTextBox.Selected = true;
                Game1.keyboardDispatcher.Subscriber = _bioTextBox;
                _bioTextBox.ReceiveLeftClick(x, y);
                return;
            }

            if (_saveButtonRect.Contains(x, y))
            {
                SaveTab2();
                return;
            }
        }

        private void HandleTab3Click(int x, int y)
        {
            if (_tab3RowInfinite.Contains(x, y))
            {
                ModEntry.Config.EnableInfiniteChat = !ModEntry.Config.EnableInfiniteChat;
                Game1.playSound(ModEntry.Config.EnableInfiniteChat ? "coin" : "drumkit6");
                ModEntry.SHelper.WriteConfig(ModEntry.Config);
                return;
            }

            if (_tab3RowVanillaFirst.Contains(x, y))
            {
                ModEntry.Config.EnableVanillaFirst = !ModEntry.Config.EnableVanillaFirst;
                Game1.playSound(ModEntry.Config.EnableVanillaFirst ? "coin" : "drumkit6");
                ModEntry.SHelper.WriteConfig(ModEntry.Config);
                return;
            }

            if (_tab3RowRecordVanilla.Contains(x, y))
            {
                ModEntry.Config.RecordVanillaDialogue = !ModEntry.Config.RecordVanillaDialogue;
                Game1.playSound(ModEntry.Config.RecordVanillaDialogue ? "coin" : "drumkit6");
                ModEntry.SHelper.WriteConfig(ModEntry.Config);
                return;
            }
        }

        private void SaveTab2()
        {
            Game1.playSound("select");

            ModEntry.Config.PlayerSexualOrientation = _orientationDropdown.SelectedId ?? "";
            ModEntry.Config.RomanceSafetyMode = (SafetyModeLevel)_safetyModeIndex;

            try
            {
                ModEntry.SHelper.WriteConfig(ModEntry.Config);
            }
            catch (Exception ex)
            {
                ModEntry.SMonitor.Log($"[Hub] config save failed: {ex.Message}", LogLevel.Error);
                return;
            }

            string bioText = _bioTextBox.Text ?? "";
            if (bioText.Length > 300)
                bioText = bioText.Substring(0, 300);

            PlayerProfileManager.SaveCustomBio(bioText);
            Game1.addHUDMessage(new HUDMessage(T("PProfile.UI.ProfileSaved", "Profile saved!"), 2));
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

            if (_currentTab == 2 && !_orientationDropdown.IsOpen && _safetySliderRect.Contains(x, y))
            {
                int trackX = _safetySliderRect.X;
                int trackW = _safetySliderRect.Width;
                int relativeX = Math.Clamp(x - trackX, 0, trackW);
                int idx = Math.Min(3, (int)(((float)relativeX / trackW) * 4));
                if (idx != _safetyModeIndex)
                {
                    _safetyModeIndex = idx;
                    Game1.playSound("shwip");
                }
                return;
            }

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
            if (_bioTextBox != null && Game1.keyboardDispatcher.Subscriber == _bioTextBox)
            {
                if (key == Keys.Escape || key == Keys.Enter)
                {
                    _bioTextBox.Selected = false;
                    Game1.keyboardDispatcher.Subscriber = null;
                    return;
                }
                if (!DialogueTextInputBox.IsControlKeyDown())
                    _bioTextBox.RecieveSpecialInput(key);
                return;
            }

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

            // 背景遮罩
            b.Draw(Game1.fadeToBlackRect,
                Game1.graphics.GraphicsDevice.Viewport.Bounds, Color.Black * 0.45f);

            // 外部装饰边框
            IClickableMenu.drawTextureBox(b,
                xPositionOnScreen - 16, yPositionOnScreen - 16,
                width + 32, height + 32, Color.White);

            IClickableMenu.drawTextureBox(b,
                xPositionOnScreen - 8, yPositionOnScreen - 8,
                width + 16, height + 16, Color.White);

            // 主面板羊皮纸
            Game1.drawDialogueBox(xPositionOnScreen, yPositionOnScreen, width, height, false, true);

            // 标题
            string title = T("Hub.Title", "Management Hub");
            var titleSize = Game1.dialogueFont.MeasureString(title);
            b.DrawString(Game1.dialogueFont, title,
                new Vector2(xPositionOnScreen + (width - titleSize.X) / 2f, yPositionOnScreen + 12),
                Game1.textColor);

            // 四个自适应 Tab
            DrawTab(b, _tabRects[0], T("Hub.TabNpcMemory", "NPC Memories"), _currentTab == 0, mx, my);
            DrawTab(b, _tabRects[1], T("Hub.TabWorldMemory", "World Memories"), _currentTab == 1, mx, my);
            DrawTab(b, _tabRects[2], T("Hub.TabProfile", "Farmer Profile"), _currentTab == 2, mx, my);
            DrawTab(b, _tabRects[3], T("Hub.TabAdvanced", "Advanced Settings"), _currentTab == 3, mx, my);

            // 分隔线
            b.Draw(Game1.staminaRect,
                new Rectangle(xPositionOnScreen + LeftPadding,
                              yPositionOnScreen + TabBarY + TabHeight + 4,
                              width - LeftPadding - RightPadding, 2),
                Color.Gray * 0.4f);

            // 当前 Tab 内容
            if (_currentTab == 0)
            {
                DrawCallsignButton(b);
                DrawEntries(b);
            }
            else if (_currentTab == 1)
            {
                DrawEntries(b);
            }
            else if (_currentTab == 2)
            {
                DrawTab2(b);
            }
            else
            {
                DrawTab3(b);
            }

            // 添加/提炼按钮与容量计数（仅 Tab0 / Tab1）
            if (_currentTab == 0 || _currentTab == 1)
                DrawBottomButtons(b);

            // 关闭按钮
            UiHelper.UpdateButtonScale(ref _closeButtonHoverScale, _closeButton, mx, my);
            _closeButton.scale = _closeButtonBaseScale * _closeButtonHoverScale;
            _closeButton.draw(b);

            // 下拉叠层置顶绘制（Tab0）
            if (_currentTab == 0)
                _npcDropdown.Draw(b);

            base.draw(b);
            drawMouse(b);
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
                isActive ? Game1.textColor : Color.White * 0.95f);
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

        // ── Tab2（农夫档案）══════════════════════════════════════════════

        private string[] GetSafetyModeLabels() => new[]
        {
            T("PProfile.Safety.Off", "1: Unrestricted"),
            T("PProfile.Safety.Loose", "2: Relaxed"),
            T("PProfile.Safety.Moderate", "3: Hearts Matter"),
            T("PProfile.Safety.Strict", "4: Committed Only")
        };

        private void InitializeTab2()
        {
            _orientationItems = new List<(string, string)>
            {
                ("", T("PProfile.UI.None", "(None)")),
                ("Heterosexual", T("PProfile.Orientation.Heterosexual", "Straight")),
                ("Homosexual", T("PProfile.Orientation.Homosexual", "Gay/Lesbian")),
                ("Bisexual", T("PProfile.Orientation.Bisexual", "Bisexual")),
                ("Asexual", T("PProfile.Orientation.Asexual", "Asexual")),
            };

            string current = ModEntry.Config.PlayerSexualOrientation ?? "";
            string selectedId = "";
            foreach (var item in _orientationItems)
            {
                if (string.Equals(item.Id, current, StringComparison.OrdinalIgnoreCase))
                {
                    selectedId = item.Id;
                    break;
                }
            }

            _safetyModeIndex = Math.Clamp((int)ModEntry.Config.RomanceSafetyMode, 0, 3);

            _orientationDropdown = new DropdownList(Rectangle.Empty)
            {
                OnItemSelected = id => Game1.playSound("select")
            };
            _orientationDropdown.SetItems(_orientationItems, selectedId);

            _bioTextBox = new DialogueTextInputBox(300)
            {
                Font = Game1.smallFont,
                Selected = false
            };
            _bioTextBox.SetText(PlayerProfileManager.GetCustomBio() ?? string.Empty);

            RecalculateTab2Layout();
            _tab2Initialized = true;
        }

        private void RecalculateTab2Layout()
        {
            int leftColX = xPositionOnScreen + LeftPadding;
            int contentW = width - LeftPadding - RightPadding;
            _tab2RightColX = leftColX + 130 + 16;
            _tab2RightColW = (xPositionOnScreen + width - RightPadding) - _tab2RightColX;

            int y = yPositionOnScreen + TopPadding + 6;

            // 1. 启用开关
            _enableProfileCheckboxRect = new Rectangle(leftColX, y, 36, 36);

            // 2. 性取向
            int orientationY = y + 40;
            _orientationLabelRect = new Rectangle(leftColX, orientationY, 130, TabHeight);
            _orientationDropdownRect = new Rectangle(_tab2RightColX, orientationY, _tab2RightColW, TabHeight);
            _orientationDropdown?.SetHeaderBounds(_orientationDropdownRect);

            // 3. 恋爱尺度标题（独立一行）
            int safetyLabelY = orientationY + TabHeight + 8;
            _safetyLabelRect = new Rectangle(leftColX, safetyLabelY, contentW, 26);

            // 4. 滑动条本身在窗口水平正中央居中
            int safetySliderY = safetyLabelY + 28;
            int trackW = Math.Min(260, contentW - 80);
            int trackX = xPositionOnScreen + (width - trackW) / 2;
            _safetySliderRect = new Rectangle(trackX, safetySliderY, trackW, 24);

            // 5. 档位指示器（居中在滑块下方）+ 描述文本（居中在指示器下方）
            int modeTextY = safetySliderY + 24 + 6;
            int descStartY = modeTextY + 24 + 4;

            int maxDescW = contentW - 20;
            int wrapW = (int)(maxDescW / 0.85f);
            string desc = Game1.parseText(T("PProfile.Safety.Desc",
                "Sets how far romantic dialogue can go (the AI will try to follow this, but it isn't guaranteed 100% of the time).\n- Unrestricted: Flirty dialogue can happen with anyone.\n- Relaxed: Casual banter when the context fits.\n- Hearts Matter: Subtle romance only once friendship is high enough.\n- Committed Only: Romance is reserved for your dating partner or spouse."),
                Game1.smallFont, wrapW);

            string[] descLines = desc.Split('\n');
            int lineSpacing = (int)(Game1.smallFont.LineSpacing * 0.82f);
            int descHeight = descLines.Length * lineSpacing;

            // 6. 自定义 Bio
            int bioY = descStartY + descHeight + 10;
            _saveButtonRect = new Rectangle(xPositionOnScreen + width / 2 - 80, yPositionOnScreen + height - 60, 160, 44);

            int availableBioH = (_saveButtonRect.Y - 12) - bioY;
            int bioBoxH = Math.Clamp(availableBioH, 60, 130);

            _bioLabelRect = new Rectangle(leftColX, bioY, 130, TabHeight);
            _bioBoxRect = new Rectangle(_tab2RightColX, bioY, _tab2RightColW, bioBoxH);

            if (_bioTextBox != null)
            {
                _bioTextBox.Position = new Vector2(_bioBoxRect.X, bioY);
                _bioTextBox.Extent = new Vector2(_tab2RightColW, bioBoxH);
            }
        }

        private void DrawTab2(SpriteBatch b)
        {
            int mx = Game1.getMouseX();
            int my = Game1.getMouseY();

            // a) 启用开关
            bool enabled = ModEntry.Config.EnablePlayerProfile;
            Rectangle enableSrc = enabled
                ? new Rectangle(236, 425, 9, 9)
                : new Rectangle(227, 425, 9, 9);
            b.Draw(Game1.mouseCursors, new Vector2(_enableProfileCheckboxRect.X, _enableProfileCheckboxRect.Y),
                enableSrc, Color.White, 0f, Vector2.Zero, 4f, SpriteEffects.None, 1f);
            b.DrawString(Game1.smallFont, T("PProfile.UI.EnableProfile", "Enable Farmer Profile"),
                new Vector2(_enableProfileCheckboxRect.X + 44, _enableProfileCheckboxRect.Y + 4),
                Game1.textColor);

            if (!enabled)
                return;

            // b) 性取向标签
            b.DrawString(Game1.smallFont, T("PProfile.UI.SexualOrientation", "Orientation:"),
                new Vector2(_orientationLabelRect.X, _orientationLabelRect.Y + 6),
                Game1.textColor);

            // c) 恋爱尺度标题
            b.DrawString(Game1.smallFont, T("PProfile.UI.RomanceSafetyMode", "Romance Boundaries:"),
                new Vector2(_safetyLabelRect.X, _safetyLabelRect.Y + 2),
                Game1.textColor);

            int trackX = _safetySliderRect.X;
            int trackW = _safetySliderRect.Width;
            int trackY = _safetySliderRect.Y;

            // 轨道底槽
            IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(403, 383, 6, 6),
                trackX, trackY, trackW, 24, Color.White, 4f, false);

            // 细长竖线刻度
            for (int t = 0; t < 4; t++)
            {
                int tickX = trackX + (int)(t * ((float)trackW / 3f));
                b.Draw(Game1.mouseCursors,
                    new Rectangle(tickX - 2, trackY + 4, 4, 16),
                    new Rectangle(240, 428, 12, 12),
                    new Color(180, 180, 180));
            }

            // 滑动手柄
            int thumbX = trackX + (int)(_safetyModeIndex * ((float)trackW / 3f)) - 12;
            b.Draw(Game1.mouseCursors,
                new Rectangle(thumbX, trackY - 8, 24, 40),
                new Rectangle(435, 463, 6, 10),
                Color.White);

            // 档位指示器：位于滑动条下方并完全水平居中
            string[] safetyLabels = GetSafetyModeLabels();
            string currentLabel = safetyLabels[_safetyModeIndex];
            var labelSize = Game1.smallFont.MeasureString(currentLabel);
            float labelX = xPositionOnScreen + (width - labelSize.X) / 2f;
            int labelY = trackY + 24 + 6;

            b.DrawString(Game1.smallFont, currentLabel,
                new Vector2(labelX, labelY), Game1.textColor);

            // 规则描述：位于指示器下方，0.85 缩放且水平居中排布
            int maxDescW = width - LeftPadding - RightPadding - 20;
            int wrapW = (int)(maxDescW / 0.85f);
            string desc = Game1.parseText(T("PProfile.Safety.Desc",
                "Sets how far romantic dialogue can go (the AI will try to follow this, but it isn't guaranteed 100% of the time).\n- Unrestricted: Flirty dialogue can happen with anyone.\n- Relaxed: Casual banter when the context fits.\n- Hearts Matter: Subtle romance only once friendship is high enough.\n- Committed Only: Romance is reserved for your dating partner or spouse."),
                Game1.smallFont, wrapW);

            string[] descLines = desc.Split('\n');
            int lineSpacing = (int)(Game1.smallFont.LineSpacing * 0.82f);
            int descStartY = labelY + 24 + 4;

            for (int i = 0; i < descLines.Length; i++)
            {
                string line = descLines[i];
                float lineW = Game1.smallFont.MeasureString(line).X * 0.85f;
                float lineX = xPositionOnScreen + (width - lineW) / 2f;
                float lineY = descStartY + i * lineSpacing;
                b.DrawString(Game1.smallFont, line, new Vector2(lineX, lineY), Color.DimGray, 0f, Vector2.Zero, 0.85f, SpriteEffects.None, 1f);
            }

            // d) Bio 自定义文本框
            b.DrawString(Game1.smallFont, T("PProfile.UI.CustomBio", "About You:"),
                new Vector2(_bioLabelRect.X, _bioLabelRect.Y + 4),
                Game1.textColor);

            IClickableMenu.drawTextureBox(b, _bioBoxRect.X - 4, _bioBoxRect.Y - 4, _bioBoxRect.Width + 8, _bioBoxRect.Height + 8, Color.White);

            _bioTextBox.Position = new Vector2(_bioBoxRect.X, _bioBoxRect.Y);
            _bioTextBox.Update(Game1.currentGameTime);
            _bioTextBox.Draw(b);

            // 占位提示
            if (string.IsNullOrWhiteSpace(_bioTextBox.Text))
            {
                string placeholder = Game1.parseText(T("PProfile.UI.BioPlaceholder",
                    "e.g. Ex-Joja accountant, loves hot coffee, hates the rain."),
                    Game1.smallFont, _bioBoxRect.Width - 28);
                b.DrawString(Game1.smallFont, placeholder,
                    new Vector2(_bioBoxRect.X + 8, _bioBoxRect.Y + 8), Color.Gray * 0.7f);
            }

            // 字数统计
            string counterText = $"{_bioTextBox.Text?.Length ?? 0}/300";
            var counterSize = Game1.smallFont.MeasureString(counterText);
            Color counterColor = (_bioTextBox.Text?.Length ?? 0) >= 300 ? Color.Red : Color.Gray * 0.8f;
            b.DrawString(Game1.smallFont, counterText,
                new Vector2(_bioBoxRect.Right - counterSize.X - 8, _bioBoxRect.Bottom - counterSize.Y - 6),
                counterColor);

            // e) 保存按钮
            bool saveHover = _saveButtonRect.Contains(mx, my);
            Color saveBg = saveHover ? Color.Wheat : Color.White;
            IClickableMenu.drawTextureBox(b, _saveButtonRect.X, _saveButtonRect.Y,
                _saveButtonRect.Width, _saveButtonRect.Height, saveBg);
            string saveText = T("PProfile.UI.Save", "Save");
            var saveSize = Game1.dialogueFont.MeasureString(saveText);
            b.DrawString(Game1.dialogueFont, saveText,
                new Vector2(_saveButtonRect.X + (_saveButtonRect.Width - saveSize.X) / 2f,
                            _saveButtonRect.Y + (_saveButtonRect.Height - saveSize.Y) / 2f),
                saveHover ? Game1.textColor : Color.Black);

            _orientationDropdown.Draw(b);
        }

        // ── Tab3（高级设置）══════════════════════════════════════════════

        private void DrawTab3(SpriteBatch b)
        {
            int mx = Game1.getMouseX();
            int my = Game1.getMouseY();

            // 1. 无限对话选项行
            DrawSettingRow(b, _tab3RowInfinite, _tab3CheckboxInfinite, ModEntry.Config.EnableInfiniteChat,
                T("AdvancedSettings.InfiniteChat", "Unlimited Conversations"),
                T("AdvancedSettings.InfiniteChatTooltip", "Removes the daily limit on conversations with NPCs."),
                mx, my);

            // 2. 原版优先选项行
            DrawSettingRow(b, _tab3RowVanillaFirst, _tab3CheckboxVanillaFirst, ModEntry.Config.EnableVanillaFirst,
                T("AdvancedSettings.VanillaFirst", "Prioritize Vanilla Dialogue"),
                T("AdvancedSettings.VanillaFirstTooltip", "Wait until all native in-game dialogue is exhausted before triggering AI dialogue."),
                mx, my);

            // 3. 记录原版对话选项行
            DrawSettingRow(b, _tab3RowRecordVanilla, _tab3CheckboxRecordVanilla, ModEntry.Config.RecordVanillaDialogue,
                T("AdvancedSettings.RecordVanillaDialogue", "Record Vanilla Dialogue"),
                T("AdvancedSettings.RecordVanillaDialogueTooltip", "Inject vanilla lines into AI context. Turn off to prevent repetitive loops."),
                mx, my);

            // 置底居中的 Disclaimer
            string disclaimer = Game1.parseText(T("AdvancedSettings.Disclaimer",
                "Experimental feature. Due to AI generation mechanisms, dialogue may have unpredictable behavior."),
                Game1.smallFont, width - LeftPadding - RightPadding - 40);

            var disclaimerSize = Game1.smallFont.MeasureString(disclaimer);
            int disclaimerY = yPositionOnScreen + height - BottomPadding + 10;

            b.DrawString(Game1.smallFont, disclaimer,
                new Vector2(xPositionOnScreen + (width - disclaimerSize.X) / 2f, disclaimerY),
                Color.DimGray);
        }

        private void DrawSettingRow(SpriteBatch b, Rectangle rowRect, Rectangle checkboxRect, bool isChecked,
            string label, string desc, int mx, int my)
        {
            bool isHover = rowRect.Contains(mx, my);

            if (isHover)
            {
                b.Draw(Game1.staminaRect, rowRect, new Color(70, 130, 180) * 0.12f);
            }

            Rectangle src = isChecked
                ? new Rectangle(236, 425, 9, 9)
                : new Rectangle(227, 425, 9, 9);
            b.Draw(Game1.mouseCursors, new Vector2(checkboxRect.X, checkboxRect.Y + 2),
                src, Color.White, 0f, Vector2.Zero, 4f, SpriteEffects.None, 1f);

            b.DrawString(Game1.smallFont, label,
                new Vector2(checkboxRect.X + 44, checkboxRect.Y),
                isHover ? new Color(0, 0, 50) : Game1.textColor);

            b.DrawString(Game1.smallFont, desc,
                new Vector2(checkboxRect.X + 44, checkboxRect.Y + 22),
                Color.Gray * 0.9f);
        }

        private void DrawBottomButtons(SpriteBatch b)
        {
            int mx = Game1.getMouseX();
            int my = Game1.getMouseY();

            if (_currentTab == 0)
            {
                // 手动录入（左）
                string manualText = I18n.Memory.AddButton();
                bool manualHover = _manualAddRect.Contains(mx, my);
                IClickableMenu.drawTextureBox(b,
                    _manualAddRect.X, _manualAddRect.Y,
                    _manualAddRect.Width, _manualAddRect.Height,
                    manualHover ? Color.Gold : Color.White);

                var manualLabelSize = Game1.smallFont.MeasureString(manualText);
                b.DrawString(Game1.smallFont, manualText,
                    new Vector2(
                        _manualAddRect.X + (_manualAddRect.Width - manualLabelSize.X) / 2f,
                        _manualAddRect.Y + (_manualAddRect.Height - manualLabelSize.Y) / 2f),
                    Game1.textColor);

                // AI 提炼（右）
                string distillText = I18n.Memory.DistillButton();
                bool distillHover = _aiExtractButtonRect.Contains(mx, my);
                IClickableMenu.drawTextureBox(b,
                    _aiExtractButtonRect.X, _aiExtractButtonRect.Y,
                    _aiExtractButtonRect.Width, _aiExtractButtonRect.Height,
                    distillHover ? Color.Gold : Color.White);

                var distillLabelSize = Game1.smallFont.MeasureString(distillText);
                b.DrawString(Game1.smallFont, distillText,
                    new Vector2(
                        _aiExtractButtonRect.X + (_aiExtractButtonRect.Width - distillLabelSize.X) / 2f,
                        _aiExtractButtonRect.Y + (_aiExtractButtonRect.Height - distillLabelSize.Y) / 2f),
                    Game1.textColor);
            }
            else
            {
                // 世界记忆：单添加按钮
                string addText = I18n.Memory.AddWorldButton();
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
            }

            // 容量计数显示（Tab 0 统计 Manual 上限，Tab 1 统计世界记忆上限）
            string cap = _currentTab == 0
                ? $"{MemoryManager.Instance.GetManualMemoryCount(_currentNpcName)} / {MemoryManager.MaxMemoriesPerNpc}"
                : $"{ActiveEntries.Count} / {MaxEntriesForTab}";

            b.DrawString(Game1.smallFont, cap,
                new Vector2(xPositionOnScreen + width - RightPadding - 120, _listTopY - 10),
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
            width = Math.Max(680, Math.Min(1000, Game1.uiViewport.Width - 80));
            height = Math.Max(500, Math.Min(680, Game1.uiViewport.Height - 80));
            xPositionOnScreen = (Game1.uiViewport.Width - width) / 2;
            yPositionOnScreen = (Game1.uiViewport.Height - height) / 2;

            _closeButton.bounds = new Rectangle(xPositionOnScreen + width - 60, yPositionOnScreen + 16, 44, 44);

            _upArrow.bounds.X = xPositionOnScreen + width - 48;
            _downArrow.bounds.X = xPositionOnScreen + width - 48;
            _scrollbarRunner.X = xPositionOnScreen + width - 32;
            _scrollbar.bounds.X = _scrollbarRunner.X - 6;

            RecalculateAllLayout();
            RefreshEntries();
        }

        protected override void cleanupBeforeExit()
        {
            base.cleanupBeforeExit();

            if (_bioTextBox != null && Game1.keyboardDispatcher.Subscriber == _bioTextBox)
                Game1.keyboardDispatcher.Subscriber = null;
        }

        // ── i18n 转发 ───────────────────────────────────────────────────

        private static string T(string key, string fallback, object tokens = null)
        {
            return ModConfigMenu.GetUIString(key, fallback, tokens);
        }

        // ── 嵌套组件：NPC 下拉列表 ──────────────────────────────────────

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
            public string SelectedId => _selectedId;
            public Rectangle HeaderBounds => _headerRect;

            public DropdownList(Rectangle headerRect, int itemHeight = 44, int maxVisibleItems = 8)
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
                if (_isOpen) _scrollIndex = 0;
            }

            public void Close() => _isOpen = false;

            public bool ReceiveLeftClick(int x, int y)
            {
                if (!_isOpen) return false;

                if (_headerRect.Contains(x, y))
                {
                    _isOpen = false;
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
                        OnItemSelected?.Invoke(_selectedId);
                        return true;
                    }
                }

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

                Color headerBg = _isOpen ? new Color(210, 180, 140)
                              : hover ? new Color(255, 235, 205)
                              : new Color(139, 90, 43);

                IClickableMenu.drawTextureBox(b, Game1.mouseCursors,
                    new Rectangle(432, 439, 9, 9),
                    _headerRect.X, _headerRect.Y, _headerRect.Width, _headerRect.Height,
                    headerBg, 4f, false);

                string selLabel = _items.FirstOrDefault(it => it.Id == _selectedId).Label ?? "";
                if (string.IsNullOrEmpty(selLabel)) selLabel = "—";
                string label = (HeaderPrefix ?? "") + selLabel;
                var size = Game1.smallFont.MeasureString(label);

                b.DrawString(Game1.smallFont, label,
                    new Vector2(_headerRect.X + 16,
                        _headerRect.Y + (_headerRect.Height - size.Y) / 2f),
                    Color.White);

                SpriteEffects effect = _isOpen ? SpriteEffects.FlipVertically : SpriteEffects.None;
                Vector2 arrowPos = new Vector2(_headerRect.Right - 28, _headerRect.Y + (_headerRect.Height - 22) / 2f);

                b.Draw(Game1.mouseCursors, arrowPos,
                    new Rectangle(437, 450, 10, 11),
                    Color.White, 0f, Vector2.Zero, 2f, effect, 1f);

                if (!_isOpen) return;

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
                        selected ? Color.White : (ihover ? Game1.textColor : Color.Black));
                }
            }
        }
    }
}