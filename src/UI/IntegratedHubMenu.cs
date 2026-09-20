using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;
using System;
using System.Collections.Generic;
using System.Linq;
using ValleytalkReborn.Services;
using ValleytalkReborn.UI;

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
        private const int ButtonSize = 32;
        private const int LeftPadding = 40;
        private const int RightPadding = 40;

        // 统一整数字号标准（杜绝亚像素采样模糊）
        private const float TitleFontSize = CustomFontManager.SizeTitle;       // 24f Bold (顶栏大标题)
        private const float TabFontSize = CustomFontManager.SizeRegular;       // 18f Bold (Tab、分段条、按钮文字)
        private const float CardTitleFontSize = CustomFontManager.SizeRegular; // 18f Bold (卡片名字、行标头)
        private const float RegularFontSize = CustomFontManager.SizeRegular;   // 18f Medium (正文、描述)
        private const float SmallFontSize = CustomFontManager.SizeSmall;       // 15f Medium (时间、角标、说明)

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
        private Rectangle _manualAddRect;        // Tab 0 手动录入（居中）
        private Rectangle _aiExtractButtonRect;  // Tab 0 AI 提炼（最左）
        private Rectangle _archiveButtonRect;    // Tab 0 归档箱（最右）
        private int _archivedCount;

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
        private Rectangle _enableProfileCheckboxRect;
        private Rectangle _orientationLabelRect;
        private Rectangle _orientationDropdownRect;
        private Rectangle _safetyLabelRect;
        private Rectangle _safetySliderRect;
        private Rectangle _bioLabelRect;
        private Rectangle _bioBoxRect;
        private Rectangle _saveButtonRect;

        // ── Tab2 分段条与宫格状态 ───────────────────────────────────────
        private const int SubTabBarH = 34;
        private const int SubTabContentOffset = 44;
        private int _profileSubTab = 0; // 0=玩家档案, 1=NPC档案宫格
        private Rectangle _subTabPlayerRect;
        private Rectangle _subTabNpcRect;
        private bool _wasObscured;

        // NPC 档案宫格卡片实体与分页
        private sealed class NpcCardInfo
        {
            public string Name;
            public string DisplayName;
            public Texture2D Portrait;
            public Rectangle DefaultSourceRect;  // 默认/平静表情
            public Rectangle SmileSourceRect;    // 微笑表情
            public bool HasCustomOverlay;
        }

        private List<NpcCardInfo> _allNpcCards = new List<NpcCardInfo>();
        private List<NpcCardInfo> _displayNpcCards = new List<NpcCardInfo>();
        private int _npcGridPage = 0;
        private bool _filterCustomOnly = false;
        private Rectangle _filterCheckboxRect;
        private Rectangle _prevPageBtnRect;
        private Rectangle _nextPageBtnRect;
        private readonly List<(NpcCardInfo Card, Rectangle Bounds, Rectangle ResetBtnBounds)> _visibleCardSlots = new();

        private string _hoveredGlobalTooltip = null;

        private readonly IHubTabView?[] _tabViews = new IHubTabView?[4];

        // 暴露给 View 的属性
        public string CurrentNpcName
        {
            get => _currentNpcName;
            set => _currentNpcName = value;
        }
        public int CurrentTab => _currentTab;

        // ── Tab3（高级设置）状态 ────────────────────────────────────────
        private Rectangle _tab3RowInfinite;
        private Rectangle _tab3RowVanillaFirst;
        private Rectangle _tab3RowRecordVanilla;
        private Rectangle _tab3RowRecordEvent;
        private Rectangle _tab3CheckboxInfinite;
        private Rectangle _tab3CheckboxVanillaFirst;
        private Rectangle _tab3CheckboxRecordVanilla;
        private Rectangle _tab3CheckboxRecordEvent;

        public IntegratedHubMenu(string initialNpcName, int initialTab)
        {
            width = Math.Max(700, Math.Min(1000, Game1.uiViewport.Width - 80));
            height = Math.Max(520, Math.Min(680, Game1.uiViewport.Height - 80));
            xPositionOnScreen = (Game1.uiViewport.Width - width) / 2;
            yPositionOnScreen = (Game1.uiViewport.Height - height) / 2;

            _closeButton = new ClickableTextureComponent(
                new Rectangle(xPositionOnScreen + width - 60, yPositionOnScreen + 16, 44, 44),
                Game1.mouseCursors, new Rectangle(337, 494, 12, 12), 3.5f);
            _closeButtonBaseScale = 3.5f;
            _closeButton.hoverText = I18n.Memory.CloseButton();

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

            if (!string.IsNullOrWhiteSpace(initialNpcName))
            {
                _currentNpcName = initialNpcName;
            }
            else
            {
                _currentNpcName = DialogueHistoryManager.Instance.GetMostRecentNpc();
                if (string.IsNullOrWhiteSpace(_currentNpcName))
                {
                    _currentNpcName = Game1.player?.friendshipData?.Keys.FirstOrDefault() ?? "";
                }
            }

            _tabViews[3] = new AdvancedSettingsTabView(this);
            _tabViews[1] = new WorldMemoryTabView(this);
            _tabViews[0] = new NpcMemoryTabView(this);

            RecalculateAllLayout();

            exitFunction = () => Game1.playSound("bigDeSelect");

            _currentTab = Math.Clamp(initialTab, 0, 3);
            RefreshEntries();
        }

        // ── 布局统一计算 ────────────────────────────────────────────────

        private void RecalculateAllLayout()
        {
            int tabBaseX = xPositionOnScreen + LeftPadding;
            int tabBaseY = yPositionOnScreen + TabBarY;

            int totalTabSpace = width - LeftPadding - RightPadding;
            int tabW = (totalTabSpace - (TabGap * 3)) / 4;
            for (int i = 0; i < 4; i++)
            {
                _tabRects[i] = new Rectangle(tabBaseX + i * (tabW + TabGap), tabBaseY, tabW, TabHeight);
            }

            int btnY = yPositionOnScreen + height - 60;
            int singleBtnW = Math.Min(300, totalTabSpace);
            _addButtonRect = new Rectangle(xPositionOnScreen + (width - singleBtnW) / 2, btnY, singleBtnW, 48);

            const int extractBtnW = 210;
            const int addBtnW = 210;
            const int archiveBtnW = 160;

            if (totalTabSpace >= extractBtnW + addBtnW + archiveBtnW + 20)
            {
                _aiExtractButtonRect = new Rectangle(xPositionOnScreen + LeftPadding, btnY, extractBtnW, 48);
                _manualAddRect = new Rectangle(xPositionOnScreen + (width - addBtnW) / 2, btnY, addBtnW, 48);
                _archiveButtonRect = new Rectangle(xPositionOnScreen + width - RightPadding - archiveBtnW, btnY, archiveBtnW, 48);
            }
            else
            {
                int gap = 8;
                int avail = totalTabSpace - gap * 2;
                int arcW = Math.Max(120, avail * 160 / 580);
                int rem = avail - arcW;
                int eachW = rem / 2;

                _aiExtractButtonRect = new Rectangle(xPositionOnScreen + LeftPadding, btnY, eachW, 48);
                _manualAddRect = new Rectangle(xPositionOnScreen + LeftPadding + eachW + gap, btnY, eachW, 48);
                _archiveButtonRect = new Rectangle(xPositionOnScreen + LeftPadding + eachW * 2 + gap * 2, btnY, arcW, 48);
            }

            _listTopY = yPositionOnScreen + TopPadding + (_currentTab == 0 ? TabHeight + 8 : 0);

            int halfW = (totalTabSpace - 16) / 2;
            var dropdownRect = new Rectangle(tabBaseX, yPositionOnScreen + TopPadding, halfW, TabHeight);
            _callsignRect = new Rectangle(tabBaseX + halfW + 16, yPositionOnScreen + TopPadding, halfW, TabHeight);
            _npcDropdown?.SetHeaderBounds(dropdownRect);

            int leftColX = xPositionOnScreen + LeftPadding;
            int tab3Y = yPositionOnScreen + TopPadding + 8;
            int rowW = width - LeftPadding - RightPadding;
            _tab3RowInfinite = new Rectangle(leftColX - 8, tab3Y - 6, rowW, 58);
            _tab3CheckboxInfinite = new Rectangle(leftColX, tab3Y, 36, 36);

            _tab3RowVanillaFirst = new Rectangle(leftColX - 8, tab3Y + 68 - 6, rowW, 58);
            _tab3CheckboxVanillaFirst = new Rectangle(leftColX, tab3Y + 68, 36, 36);

            _tab3RowRecordVanilla = new Rectangle(leftColX - 8, tab3Y + 136 - 6, rowW, 58);
            _tab3CheckboxRecordVanilla = new Rectangle(leftColX, tab3Y + 136, 36, 36);

            _tab3RowRecordEvent = new Rectangle(leftColX - 8, tab3Y + 204 - 6, rowW, 58);
            _tab3CheckboxRecordEvent = new Rectangle(leftColX, tab3Y + 204, 36, 36);

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
            _cachedEntries = new List<MemoryEntry>();

            if (_currentTab == 2 && !_tab2Initialized)
                InitializeTab2();

            if (_currentTab == 2 && _profileSubTab == 1)
                RefreshNpcCards();

            if (_currentTab == 2 && _bioTextBox != null && _bioTextBox.Selected
                && Game1.keyboardDispatcher.Subscriber == null)
                Game1.keyboardDispatcher.Subscriber = _bioTextBox;

            if (_currentTab == 0)
                _tabViews[0]!.RefreshFromHub();
            else if (_currentTab == 1)
                _tabViews[1]!.RefreshFromHub();
        }

        private List<MemoryEntry> SafeGetMemories()
        {
            if (string.IsNullOrEmpty(_currentNpcName))
                return new List<MemoryEntry>();

            var list = MemoryManager.Instance.GetMemories(_currentNpcName);
            if (list == null)
                return new List<MemoryEntry>();

            return list
                .OrderByDescending(e => e.Category == MemoryCategory.Behavior)
                .ToList();
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

            _npcDropdown?.Close();
            _orientationDropdown?.Close();

            _tabViews[_currentTab]?.OnDeactivated();

            _currentTab = tab;
            Game1.playSound("smallSelect");
            RecalculateAllLayout();
            RefreshEntries();

            _tabViews[tab]?.OnActivated();
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

            string recent = DialogueHistoryManager.Instance.GetMostRecentNpc();
            if (!string.IsNullOrEmpty(recent) && seen.Add(recent))
            {
                string label = Game1.getCharacterFromName(recent)?.displayName ?? recent;
                items.Add((recent, label));
            }

            var remaining = Game1.player.friendshipData.Keys
                .Where(k => !seen.Contains(k))
                .OrderBy(k => k, StringComparer.OrdinalIgnoreCase);

            foreach (var k in remaining)
            {
                string label = Game1.getCharacterFromName(k)?.displayName ?? k;
                items.Add((k, label));
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
            string safeContent = CustomFontManager.TruncateString(entry.Content, RegularFontSize, 320f);

            Game1.activeClickableMenu = new ConfirmationDialog(
                I18n.Memory.DeleteConfirm(safeContent),
                _ =>
                {
                    if (tabSnapshot == 0)
                    {
                        MemoryManager.Instance.ArchiveMemory(_currentNpcName, entry, "ManualDeleted");
                        MemoryManager.Instance.RemoveMemory(_currentNpcName, entry.Id);
                    }
                    else
                    {
                        WorldMemoryManager.Instance.RemoveEntry(entry.Id);
                    }

                    Game1.playSound("trashcan");
                    RefreshEntries();
                    Game1.activeClickableMenu = this;
                },
                _ =>
                {
                    Game1.activeClickableMenu = this;
                });
        }

        internal void ReleaseKeyboard()
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
                if (_profileSubTab == 1)
                {
                    int totalPages = GetNpcTotalPages();
                    if (direction < 0 && _npcGridPage < totalPages - 1)
                    {
                        _npcGridPage++;
                        Game1.playSound("shwip");
                        RecalculateNpcGridLayout();
                    }
                    else if (direction > 0 && _npcGridPage > 0)
                    {
                        _npcGridPage--;
                        Game1.playSound("shwip");
                        RecalculateNpcGridLayout();
                    }
                    return;
                }

                if (_orientationDropdown != null && _orientationDropdown.IsOpen)
                {
                    _orientationDropdown.ReceiveScrollWheel(direction);
                    return;
                }
                _bioTextBox?.ReceiveScrollWheel(direction);
                return;
            }

            if (_npcDropdown != null && _npcDropdown.IsOpen)
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

            if (_currentTab == 2 && _profileSubTab == 0 && _orientationDropdown != null && _orientationDropdown.ReceiveLeftClick(x, y))
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
                if (_tabViews[0]!.ReceiveLeftClick(x, y)) return;
            }
            else if (_currentTab == 1)
            {
                if (_tabViews[1]!.ReceiveLeftClick(x, y)) return;
            }
            else if (_currentTab == 2)
            {
                HandleTab2Click(x, y);
            }
            else if (_currentTab == 3)
            {
                if (_tabViews[3]!.ReceiveLeftClick(x, y)) return;
            }
        }

        private void HandleTab2Click(int x, int y)
        {
            if (_subTabPlayerRect.Contains(x, y))
            {
                if (_profileSubTab != 0)
                {
                    _profileSubTab = 0;
                    Game1.playSound("smallSelect");
                }
                return;
            }
            if (_subTabNpcRect.Contains(x, y))
            {
                if (_profileSubTab != 1)
                {
                    _profileSubTab = 1;
                    _orientationDropdown?.Close();
                    ReleaseKeyboard();
                    Game1.playSound("smallSelect");
                    RefreshNpcCards();
                }
                return;
            }

            if (_profileSubTab == 1)
            {
                HandleTab2NpcPageClick(x, y);
                return;
            }

            if (_enableProfileCheckboxRect.Contains(x, y))
            {
                ModEntry.Config.EnablePlayerProfile = !ModEntry.Config.EnablePlayerProfile;
                Game1.playSound("select");
                return;
            }

            if (!ModEntry.Config.EnablePlayerProfile)
                return;

            // 性取向下拉框点击：打开/关闭并拦截，杜绝穿透
            if (_orientationDropdown != null && _orientationDropdown.HeaderBounds.Contains(x, y))
            {
                _orientationDropdown.ToggleOpen();
                Game1.playSound("shwip");
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
            else
            {
                ReleaseKeyboard();
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

            if (_tab3RowRecordEvent.Contains(x, y))
            {
                ModEntry.Config.RecordEventDialogue = !ModEntry.Config.RecordEventDialogue;
                Game1.playSound(ModEntry.Config.RecordEventDialogue ? "coin" : "drumkit6");
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
            Game1.addHUDMessage(new HUDMessage(I18n.Profile.SavedHud(), 2));
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

            if (_currentTab == 2 && _orientationDropdown != null && !_orientationDropdown.IsOpen && _safetySliderRect.Contains(x, y))
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
                if (_npcDropdown != null && _npcDropdown.IsOpen)
                {
                    _npcDropdown.Close();
                    return;
                }
                if (_orientationDropdown != null && _orientationDropdown.IsOpen)
                {
                    _orientationDropdown.Close();
                    return;
                }

                Game1.playSound("bigDeSelect");
                exitThisMenu();
                return;
            }

            base.receiveKeyPress(key);
        }

        // ── 绘制 ────────────────────────────────────────────────────────

        public override void draw(SpriteBatch b)
        {
            _hoveredGlobalTooltip = null;
            int mx = Game1.getMouseX();
            int my = Game1.getMouseY();

            b.Draw(Game1.fadeToBlackRect,
                Game1.graphics.GraphicsDevice.Viewport.Bounds, Color.Black * 0.45f);

            IClickableMenu.drawTextureBox(b,
                xPositionOnScreen - 16, yPositionOnScreen - 16,
                width + 32, height + 32, Color.White);

            IClickableMenu.drawTextureBox(b,
                xPositionOnScreen - 8, yPositionOnScreen - 8,
                width + 16, height + 16, Color.White);

            Game1.drawDialogueBox(xPositionOnScreen, yPositionOnScreen, width, height, false, true);

            string title = I18n.Hub.Title();
            var titleSize = CustomFontManager.MeasureStringBold(title, TitleFontSize);
            CustomFontManager.DrawStringBold(b, title,
                new Vector2(xPositionOnScreen + (width - titleSize.X) / 2f, yPositionOnScreen + 12),
                Game1.textColor, TitleFontSize);

            DrawTab(b, _tabRects[0], I18n.Hub.TabNpcMemory(), _currentTab == 0, mx, my);
            DrawTab(b, _tabRects[1], I18n.Hub.TabWorldMemory(), _currentTab == 1, mx, my);
            DrawTab(b, _tabRects[2], I18n.Hub.TabProfile(), _currentTab == 2, mx, my);
            DrawTab(b, _tabRects[3], I18n.Hub.TabAdvanced(), _currentTab == 3, mx, my);

            b.Draw(Game1.staminaRect,
                new Rectangle(xPositionOnScreen + LeftPadding,
                              yPositionOnScreen + TabBarY + TabHeight + 4,
                              width - LeftPadding - RightPadding, 2),
                Color.Gray * 0.4f);

            if (_currentTab == 0)
                _tabViews[0]!.Draw(b, mx, my);
            else if (_currentTab == 1)
                _tabViews[1]!.Draw(b, mx, my);
            else if (_currentTab == 2)
                DrawTab2(b);
            else
                _tabViews[3]!.Draw(b, mx, my);

            UiHelper.UpdateButtonScale(ref _closeButtonHoverScale, _closeButton, mx, my);
            _closeButton.scale = _closeButtonBaseScale * _closeButtonHoverScale;
            _closeButton.draw(b);

            // 确保最顶层弹窗在所有控件之上渲染
            if (_currentTab == 0)
                _npcDropdown?.Draw(b);
            else if (_currentTab == 2 && _profileSubTab == 0)
                _orientationDropdown?.Draw(b);

            // Tab 页顶层弹层
            _tabViews[_currentTab]?.DrawOverlay(b);

            if (!string.IsNullOrEmpty(_hoveredGlobalTooltip))
            {
                DrawHoverTextCustom(b, _hoveredGlobalTooltip);
            }

            base.draw(b);
            drawMouse(b);
        }

        private void DrawTab(SpriteBatch b, Rectangle rect, string label, bool isActive, int mx, int my)
        {
            Color bg = isActive ? new Color(210, 180, 140)
                     : rect.Contains(mx, my) ? new Color(255, 235, 205)
                     : new Color(139, 90, 43);

            b.Draw(Game1.staminaRect, new Rectangle(rect.X + 2, rect.Y + 2, rect.Width - 4, rect.Height - 4), bg);
            IClickableMenu.drawTextureBox(b, Game1.mouseCursors,
                new Rectangle(432, 439, 9, 9),
                rect.X, rect.Y, rect.Width, rect.Height, bg, 4f, false);

            var labelSize = CustomFontManager.MeasureStringBold(label, TabFontSize);
            CustomFontManager.DrawStringBold(b, label,
                new Vector2(rect.X + (rect.Width - labelSize.X) / 2f,
                            rect.Y + (rect.Height - labelSize.Y) / 2f),
                isActive ? Game1.textColor : Color.White * 0.95f, TabFontSize);
        }

        private void DrawCallsignButton(SpriteBatch b)
        {
            int mx = Game1.getMouseX(), my = Game1.getMouseY();
            bool hover = _callsignRect.Contains(mx, my);
            string callsign = MemoryManager.Instance.GetCustomCallsign(_currentNpcName);
            bool hasValue = !string.IsNullOrEmpty(callsign);

            Color bg = hover ? new Color(255, 235, 205) : new Color(210, 180, 140) * 0.8f;
            b.Draw(Game1.staminaRect, new Rectangle(_callsignRect.X + 2, _callsignRect.Y + 2, _callsignRect.Width - 4, _callsignRect.Height - 4), bg);
            IClickableMenu.drawTextureBox(b, Game1.mouseCursors,
                new Rectangle(432, 439, 9, 9),
                _callsignRect.X, _callsignRect.Y, _callsignRect.Width, _callsignRect.Height,
                bg, 4f, false);

            string prefix = I18n.Memory.CallsignPrefix();
            string valueText = hasValue ? $"[{callsign}]" : I18n.Memory.CallsignUnset();
            Color valueColor = hover ? Game1.textColor : (hasValue ? Game1.textColor * 0.9f : Color.Gray);
            string fullText = prefix + valueText;
            var textSize = CustomFontManager.MeasureStringBold(fullText, TabFontSize);

            CustomFontManager.DrawStringBold(b, fullText,
                new Vector2(_callsignRect.X + (_callsignRect.Width - textSize.X) / 2f,
                            _callsignRect.Y + (_callsignRect.Height - textSize.Y) / 2f),
                valueColor, TabFontSize);
        }

        private void DrawEntries(SpriteBatch b)
        {
            int mx = Game1.getMouseX();
            int my = Game1.getMouseY();
            var entries = ActiveEntries;
            int visibleCount = GetVisibleLineCount();

            if (entries.Count == 0)
            {
                string hint = _currentTab == 0
                    ? I18n.Memory.Empty()
                    : I18n.Memory.WorldEmpty();
                var hintSize = CustomFontManager.MeasureString(hint, RegularFontSize);
                CustomFontManager.DrawString(b, hint,
                    new Vector2(xPositionOnScreen + (width - hintSize.X) / 2f, _listTopY + 60),
                    Color.Gray, RegularFontSize);
                return;
            }

            float fixedDateWidth = CustomFontManager.MeasureString("2026-12-31 00:00", SmallFontSize).X;
            float dateX = xPositionOnScreen + width - RightPadding - 85 - fixedDateWidth;
            float contentStartX = xPositionOnScreen + LeftPadding;
            float maxContentWidth = (dateX - 16) - contentStartX;

            for (int i = 0; i < visibleCount && _startIndex + i < entries.Count; i++)
            {
                int idx = _startIndex + i;
                var entry = entries[idx];
                int rowY = _listTopY + 10 + i * LineHeight;

                var rowRect = new Rectangle(
                    xPositionOnScreen + LeftPadding - 16, rowY - 4,
                    width - LeftPadding - RightPadding + 16, LineHeight - 2);

                if (rowRect.Contains(mx, my))
                    b.Draw(Game1.staminaRect, rowRect, new Color(70, 130, 180) * 0.18f);

                string prefix;
                Color textColor;
                if (_currentTab == 0)
                {
                    bool isRule = entry.Category == MemoryCategory.Behavior;
                    prefix = isRule ? I18n.Memory.RuleTag() : I18n.Memory.MemoryTag();
                    textColor = entry.Source == "Auto" ? new Color(130, 150, 170) : Game1.textColor;
                }
                else
                {
                    bool isAuto = entry.Source == "Auto";
                    prefix = isAuto ? I18n.Memory.AutoPrefix() : "";
                    textColor = isAuto ? new Color(120, 140, 160) : Game1.textColor;
                }

                string fullRawText = $"{idx + 1}. {prefix}{entry.Content}";
                string text = CustomFontManager.TruncateString(fullRawText, RegularFontSize, maxContentWidth);
                string dateText = entry.CreatedAt.ToString("yyyy-MM-dd HH:mm");

                CustomFontManager.DrawString(b, text,
                    new Vector2(contentStartX, rowY + 4),
                    textColor, RegularFontSize);

                CustomFontManager.DrawString(b, dateText,
                    new Vector2(dateX, rowY + 6),
                    Color.Gray, SmallFontSize);

                bool isLeftMouseDown = Mouse.GetState().LeftButton == Microsoft.Xna.Framework.Input.ButtonState.Pressed;

                if (i < _editButtons.Count)
                {
                    var btn = _editButtons[i];
                    bool isPressed = isLeftMouseDown && btn.containsPoint(mx, my);
                    IconSource.DrawButton(b, btn, isPressed);
                }

                if (i < _deleteButtons.Count)
                {
                    var btn = _deleteButtons[i];
                    bool isPressed = isLeftMouseDown && btn.containsPoint(mx, my);
                    IconSource.DrawButton(b, btn, isPressed);
                }
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

        public override void update(GameTime time)
        {
            base.update(time);

            bool obscured = Game1.activeClickableMenu != this;
            if (_wasObscured && !obscured && _currentTab == 2)
            {
                RecalculateTab2Layout();
                if (_profileSubTab == 1)
                    RefreshNpcCards();
            }
            _wasObscured = obscured;
        }

        // ── Tab2（档案设置）══════════════════════════════════════════════

        private string[] GetSafetyModeLabels() => new[]
        {
            I18n.Profile.SafetyOff(),
            I18n.Profile.SafetyLoose(),
            I18n.Profile.SafetyModerate(),
            I18n.Profile.SafetyStrict()
        };

        private void InitializeTab2()
        {
            _orientationItems = new List<(string, string)>
            {
                ("", I18n.Profile.OrientationNone()),
                ("Heterosexual", I18n.Profile.OrientationHeterosexual()),
                ("Homosexual", I18n.Profile.OrientationHomosexual()),
                ("Bisexual", I18n.Profile.OrientationBisexual()),
                ("Asexual", I18n.Profile.OrientationAsexual()),
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
                UseCustomFont = true,
                CustomFontSize = RegularFontSize,
                CounterFontSize = SmallFontSize + 1,
                DrawFrame = true,
                ShowCharacterCount = true,
                AllowNewlines = true,
                Selected = false
            };
            _bioTextBox.SetText(PlayerProfileManager.GetCustomBio() ?? string.Empty);

            RecalculateTab2Layout();
            RefreshNpcCards();
            _tab2Initialized = true;
        }

        private void RecalculateTab2Layout()
        {
            int leftColX = xPositionOnScreen + LeftPadding;
            int contentW = width - LeftPadding - RightPadding;

            int subTabY = yPositionOnScreen + TopPadding;
            int subTabW = 160;
            _subTabPlayerRect = new Rectangle(leftColX, subTabY, subTabW, SubTabBarH);
            _subTabNpcRect = new Rectangle(leftColX + subTabW + 12, subTabY, subTabW, SubTabBarH);

            _filterCheckboxRect = new Rectangle(leftColX + contentW - 170, subTabY + 2, 28, 28);

            // 1. 自我介绍文本框与主输入控件的宽度基准
            int bioBoxW = Math.Min(contentW - 32, 680);
            int bioBoxX = xPositionOnScreen + (width - bioBoxW) / 2;

            // 2. 启用总开关复选框（尺寸微调优化：从 36 缩小为 27）
            int currentY = yPositionOnScreen + TopPadding + SubTabContentOffset + 4;
            _enableProfileCheckboxRect = new Rectangle(bioBoxX, currentY, 27, 27);

            // 3. 性取向下拉框
            currentY += 40;
            const int dropdownH = 38;
            _orientationLabelRect = new Rectangle(bioBoxX, currentY, bioBoxW, 22);
            currentY += 24; // 标题与下拉框本体的间距
            _orientationDropdownRect = new Rectangle(bioBoxX, currentY, bioBoxW, dropdownH);
            _orientationDropdown?.SetHeaderBounds(_orientationDropdownRect);

            // 4. 浪漫安全模式滑块
            currentY += dropdownH + 12;
            _safetyLabelRect = new Rectangle(bioBoxX, currentY, bioBoxW, 22);
            currentY += 24;

            int trackW = Math.Min(260, contentW - 80);
            int trackX = xPositionOnScreen + (width - trackW) / 2;
            _safetySliderRect = new Rectangle(trackX, currentY, trackW, 22);

            int modeTextY = currentY + 22 + 4;
            int descStartY = modeTextY + 22 + 2;

            int maxDescW = contentW - 20;
            string desc = Game1.parseText(I18n.Profile.SafetyDesc(), Game1.smallFont, maxDescW);
            string[] descLines = desc.Split('\n');
            int lineSpacing = (int)CustomFontManager.MeasureString("A", RegularFontSize).Y + 2;
            int descHeight = descLines.Length * lineSpacing;

            // 5. 底部保存按钮与文本框自适应
            _saveButtonRect = new Rectangle(xPositionOnScreen + width / 2 - 80, yPositionOnScreen + height - 56, 160, 42);

            int bioY = descStartY + descHeight + 24;
            int bioBoxH = Math.Max(70, (_saveButtonRect.Y - 12) - bioY);

            _bioLabelRect = new Rectangle(bioBoxX, bioY - 24, bioBoxW, 22);
            _bioBoxRect = new Rectangle(bioBoxX, bioY, bioBoxW, bioBoxH);

            if (_bioTextBox != null)
            {
                _bioTextBox.Position = new Vector2(_bioBoxRect.X, _bioBoxRect.Y);
                _bioTextBox.Extent = new Vector2(_bioBoxRect.Width, _bioBoxRect.Height);
                _bioTextBox.InvalidateLayout();
            }

            RecalculateNpcGridLayout();
        }

        private void DrawTab2(SpriteBatch b)
        {
            int mx = Game1.getMouseX();
            int my = Game1.getMouseY();

            DrawTab2SubTab(b, _subTabPlayerRect, "玩家档案", _profileSubTab == 0, mx, my);
            DrawTab2SubTab(b, _subTabNpcRect, "NPC 档案", _profileSubTab == 1, mx, my);

            if (_profileSubTab == 1)
            {
                DrawTab2NpcGridPage(b);
                return;
            }

            // ── 玩家档案启用开关（尺寸缩小至 3f 比例） ──
            bool enabled = ModEntry.Config.EnablePlayerProfile;
            Rectangle enableSrc = enabled ? new Rectangle(236, 425, 9, 9) : new Rectangle(227, 425, 9, 9);
            b.Draw(Game1.mouseCursors, new Vector2(_enableProfileCheckboxRect.X, _enableProfileCheckboxRect.Y),
                enableSrc, Color.White, 0f, Vector2.Zero, 3f, SpriteEffects.None, 1f);

            CustomFontManager.DrawStringBold(b, I18n.Profile.EnableProfile(),
                new Vector2(_enableProfileCheckboxRect.X + 36, _enableProfileCheckboxRect.Y + (_enableProfileCheckboxRect.Height - CustomFontManager.MeasureStringBold("A", RegularFontSize).Y) / 2f),
                Game1.textColor, RegularFontSize);

            if (!enabled)
                return;

            // ── 性取向标签 ──
            CustomFontManager.DrawStringBold(b, I18n.Profile.OrientationLabel(),
                new Vector2(_orientationLabelRect.X, _orientationLabelRect.Y), Game1.textColor, RegularFontSize);

            CustomFontManager.DrawStringBold(b, I18n.Profile.RomanceSafetyLabel(),
                new Vector2(_safetyLabelRect.X, _safetyLabelRect.Y), Game1.textColor, RegularFontSize);

            int trackX = _safetySliderRect.X;
            int trackW = _safetySliderRect.Width;
            int trackY = _safetySliderRect.Y;

            b.Draw(Game1.staminaRect, new Rectangle(trackX + 2, trackY + 2, trackW - 4, 20), new Color(245, 230, 205));
            IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(403, 383, 6, 6),
                trackX, trackY, trackW, 24, Color.White, 4f, false);

            for (int t = 0; t < 4; t++)
            {
                int tickX = trackX + (int)(t * ((float)trackW / 3f));
                b.Draw(Game1.mouseCursors,
                    new Rectangle(tickX - 2, trackY + 4, 4, 16),
                    new Rectangle(240, 428, 12, 12), new Color(180, 180, 180));
            }

            int thumbX = trackX + (int)(_safetyModeIndex * ((float)trackW / 3f)) - 12;
            b.Draw(Game1.mouseCursors,
                new Rectangle(thumbX, trackY - 8, 24, 40),
                new Rectangle(435, 463, 6, 10), Color.White);

            string[] safetyLabels = GetSafetyModeLabels();
            string currentLabel = safetyLabels[_safetyModeIndex];
            var labelSize = CustomFontManager.MeasureStringBold(currentLabel, RegularFontSize);
            float labelX = xPositionOnScreen + (width - labelSize.X) / 2f;
            int labelY = trackY + 22 + 4;

            CustomFontManager.DrawStringBold(b, currentLabel, new Vector2(labelX, labelY), Game1.textColor, RegularFontSize);

            int maxDescW = width - LeftPadding - RightPadding - 20;
            string desc = Game1.parseText(I18n.Profile.SafetyDesc(), Game1.smallFont, maxDescW);
            string[] descLines = desc.Split('\n');
            int lineSpacing = (int)CustomFontManager.MeasureString("A", RegularFontSize).Y + 2;
            int descStartY = labelY + 22 + 2;

            for (int i = 0; i < descLines.Length; i++)
            {
                string line = descLines[i];
                float lineW = CustomFontManager.MeasureString(line, RegularFontSize).X;
                float lineX = xPositionOnScreen + (width - lineW) / 2f;
                float lineY = descStartY + i * lineSpacing;
                CustomFontManager.DrawString(b, line, new Vector2(lineX, lineY), Color.DimGray, RegularFontSize);
            }

            CustomFontManager.DrawStringBold(b, I18n.Profile.BioLabel(),
                new Vector2(_bioLabelRect.X + 2, _bioLabelRect.Y), Game1.textColor, RegularFontSize);

            _bioTextBox.Position = new Vector2(_bioBoxRect.X, _bioBoxRect.Y);
            _bioTextBox.Update(Game1.currentGameTime);
            _bioTextBox.Draw(b);

            if (string.IsNullOrWhiteSpace(_bioTextBox.Text))
            {
                string placeholder = Game1.parseText(I18n.Profile.BioPlaceholder(), Game1.smallFont, _bioBoxRect.Width - 28);
                CustomFontManager.DrawString(b, placeholder, new Vector2(_bioBoxRect.X + 14, _bioBoxRect.Y + 12), Color.Gray * 0.7f, SmallFontSize);
            }

            bool saveHover = _saveButtonRect.Contains(mx, my);
            Color saveBg = saveHover ? new Color(255, 235, 205) : new Color(139, 90, 43);

            b.Draw(Game1.staminaRect, new Rectangle(_saveButtonRect.X + 2, _saveButtonRect.Y + 2, _saveButtonRect.Width - 4, _saveButtonRect.Height - 4), saveBg);
            IClickableMenu.drawTextureBox(b, Game1.mouseCursors,
                new Rectangle(432, 439, 9, 9),
                _saveButtonRect.X, _saveButtonRect.Y, _saveButtonRect.Width, _saveButtonRect.Height,
                saveBg, 4f, false);

            string saveText = I18n.Profile.SaveButton();
            var saveSize = CustomFontManager.MeasureStringBold(saveText, TabFontSize);
            CustomFontManager.DrawStringBold(b, saveText,
                new Vector2(_saveButtonRect.X + (_saveButtonRect.Width - saveSize.X) / 2f,
                            _saveButtonRect.Y + (_saveButtonRect.Height - saveSize.Y) / 2f),
                saveHover ? Game1.textColor : Color.White, TabFontSize);
        }

        private void DrawTab2SubTab(SpriteBatch b, Rectangle rect, string label, bool isActive, int mx, int my)
        {
            Color bg = isActive ? new Color(210, 180, 140)
                     : rect.Contains(mx, my) ? new Color(255, 235, 205)
                     : new Color(139, 90, 43);

            b.Draw(Game1.staminaRect, new Rectangle(rect.X + 2, rect.Y + 2, rect.Width - 4, rect.Height - 4), bg);
            IClickableMenu.drawTextureBox(b, Game1.mouseCursors,
                new Rectangle(432, 439, 9, 9),
                rect.X, rect.Y, rect.Width, rect.Height, bg, 4f, false);

            var labelSize = CustomFontManager.MeasureStringBold(label, TabFontSize);
            CustomFontManager.DrawStringBold(b, label,
                new Vector2(rect.X + (rect.Width - labelSize.X) / 2f,
                            rect.Y + (rect.Height - labelSize.Y) / 2f),
                isActive ? Game1.textColor : Color.White * 0.95f, TabFontSize);
        }

        // ── Tab2 NPC 宫格卡片重构核心 ──────────────────────────────────────

        private static Rectangle GetSmilingPortraitSource(Texture2D portrait)
        {
            if (portrait == null) return Rectangle.Empty;

            if (portrait.Width >= 128 && portrait.Height >= 64)
            {
                return new Rectangle(64, 0, 64, 64);
            }
            if (portrait.Width >= 64 && portrait.Height >= 128)
            {
                return new Rectangle(0, 64, 64, 64);
            }
            return new Rectangle(0, 0, Math.Min(64, portrait.Width), Math.Min(64, portrait.Height));
        }

        private static Rectangle GetDefaultPortraitSource(Texture2D portrait)
        {
            if (portrait == null) return Rectangle.Empty;
            int w = Math.Min(64, portrait.Width);
            int h = Math.Min(64, portrait.Height);
            return new Rectangle(0, 0, w, h);
        }

        private static readonly Dictionary<string, Texture2D> _portraitCache = new(StringComparer.OrdinalIgnoreCase);

        private Texture2D SafeLoadPortrait(string npcName)
        {
            if (string.IsNullOrWhiteSpace(npcName)) return null;

            if (_portraitCache.TryGetValue(npcName, out var tex) && tex != null && !tex.IsDisposed)
                return tex;

            Texture2D loaded = null;
            var character = Game1.getCharacterFromName(npcName);
            if (character?.Portrait != null && !character.Portrait.IsDisposed)
            {
                loaded = character.Portrait;
            }
            else
            {
                try
                {
                    loaded = Game1.content.Load<Texture2D>("Portraits\\" + npcName);
                }
                catch { }
            }

            _portraitCache[npcName] = loaded;
            return loaded;
        }

        private void RefreshNpcCards()
        {
            _allNpcCards.Clear();
            var friendshipData = Game1.player?.friendshipData;

            var excludedNpcs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Grandpa", "Governor", "Gil", "Bouncer", "Birdie", "Henchman", "MarlonFudge"
            };

            bool IsInvalidOrEventNpc(string internalName, NPC npc)
            {
                if (string.IsNullOrWhiteSpace(internalName)) return true;
                if (excludedNpcs.Contains(internalName)) return true;

                if (internalName.Contains("_") || 
                    internalName.Contains("Event", StringComparison.OrdinalIgnoreCase) ||
                    internalName.Contains("Fake", StringComparison.OrdinalIgnoreCase) ||
                    internalName.Contains("Dummy", StringComparison.OrdinalIgnoreCase))
                    return true;

                bool isTrueMarlon = internalName.Equals("Marlon", StringComparison.OrdinalIgnoreCase);
                if (internalName.StartsWith("Marlon", StringComparison.OrdinalIgnoreCase) && !isTrueMarlon)
                    return true;

                bool inFriendship = friendshipData != null && friendshipData.ContainsKey(internalName);

                if (npc != null)
                {
                    if (Game1.CurrentEvent != null && Game1.CurrentEvent.actors != null && Game1.CurrentEvent.actors.Contains(npc))
                        return true;

                    if (!isTrueMarlon && !npc.CanSocialize && !inFriendship)
                        return true;
                }
                else
                {
                    if (!isTrueMarlon && !inFriendship)
                        return true;
                }

                return false;
            }

            var rawCandidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (friendshipData != null)
            {
                foreach (var k in friendshipData.Keys)
                {
                    if (!IsInvalidOrEventNpc(k, Game1.getCharacterFromName(k)))
                        rawCandidates.Add(k);
                }
            }

            if (Game1.characterData != null)
            {
                foreach (var kvp in Game1.characterData)
                {
                    string name = kvp.Key;
                    if (!IsInvalidOrEventNpc(name, Game1.getCharacterFromName(name)))
                        rawCandidates.Add(name);
                }
            }

            foreach (var npc in Utility.getAllCharacters())
            {
                if (npc != null && (npc.IsVillager || npc.Name.Equals("Marlon", StringComparison.OrdinalIgnoreCase)) && !IsInvalidOrEventNpc(npc.Name, npc))
                {
                    rawCandidates.Add(npc.Name);
                }
            }

            if (!rawCandidates.Contains("Marlon"))
                rawCandidates.Add("Marlon");

            var resolvedCards = new Dictionary<string, NpcCardInfo>(StringComparer.OrdinalIgnoreCase);

            foreach (var name in rawCandidates)
            {
                var portrait = SafeLoadPortrait(name);
                if (portrait == null || portrait.IsDisposed)
                    continue;

                var smileRect = GetSmilingPortraitSource(portrait);
                if (smileRect.IsEmpty || smileRect.Width <= 0 || smileRect.Height <= 0)
                    continue;

                var defaultRect = GetDefaultPortraitSource(portrait);

                string dispName = Game1.getCharacterFromName(name)?.displayName;
                if (string.IsNullOrWhiteSpace(dispName)) dispName = name;

                bool hasCustom = ModEntry.BioStorage != null && ModEntry.BioStorage.HasCustomOverlay(name);
                bool hasFriendship = friendshipData != null && friendshipData.ContainsKey(name);

                var card = new NpcCardInfo
                {
                    Name = name,
                    DisplayName = dispName,
                    Portrait = portrait,
                    DefaultSourceRect = defaultRect,
                    SmileSourceRect = smileRect,
                    HasCustomOverlay = hasCustom
                };

                if (resolvedCards.TryGetValue(dispName, out var existing))
                {
                    bool isCurrentTrue = name.Equals("Marlon", StringComparison.OrdinalIgnoreCase);
                    bool isExistingTrue = existing.Name.Equals("Marlon", StringComparison.OrdinalIgnoreCase);

                    if (isCurrentTrue && !isExistingTrue)
                    {
                        resolvedCards[dispName] = card;
                    }
                    else if (!isCurrentTrue && isExistingTrue)
                    {
                        continue;
                    }
                    else
                    {
                        bool existingHasFriendship = friendshipData != null && friendshipData.ContainsKey(existing.Name);
                        if ((hasFriendship && !existingHasFriendship) || 
                            (hasFriendship == existingHasFriendship && name.Length < existing.Name.Length))
                        {
                            resolvedCards[dispName] = card;
                        }
                    }
                }
                else
                {
                    resolvedCards[dispName] = card;
                }
            }

            _allNpcCards = resolvedCards.Values
                .OrderByDescending(x => x.HasCustomOverlay)
                .ThenBy(x => x.DisplayName, StringComparer.CurrentCultureIgnoreCase)
                .ToList();

            ApplyNpcFilter();
        }

        private void ApplyNpcFilter()
        {
            if (_filterCustomOnly)
            {
                _displayNpcCards = _allNpcCards.Where(x => x.HasCustomOverlay).ToList();
            }
            else
            {
                _displayNpcCards = new List<NpcCardInfo>(_allNpcCards);
            }

            int totalPages = GetNpcTotalPages();
            if (_npcGridPage >= totalPages)
                _npcGridPage = Math.Max(0, totalPages - 1);

            RecalculateNpcGridLayout();
        }

        private int GetNpcCols() => 5;
        private int GetNpcRows() => 2;

        private int GetNpcItemsPerPage() => GetNpcCols() * GetNpcRows();

        private int GetNpcTotalPages()
        {
            int perPage = Math.Max(1, GetNpcItemsPerPage());
            return Math.Max(1, (int)Math.Ceiling((float)_displayNpcCards.Count / perPage));
        }

        private void RecalculateNpcGridLayout()
        {
            _visibleCardSlots.Clear();

            int leftColX = xPositionOnScreen + LeftPadding;
            int contentW = width - LeftPadding - RightPadding;
            int startY = yPositionOnScreen + TopPadding + SubTabContentOffset;

            const int bottomPagingBarH = 40;
            const int bottomMargin = 8;
            int bottomLimitY = yPositionOnScreen + height - BottomPadding;
            int availH = bottomLimitY - startY - bottomPagingBarH - bottomMargin;

            int cols = GetNpcCols();
            int rows = GetNpcRows();
            int perPage = cols * rows;

            const int gapX = 12;
            const int gapY = 12;

            int cardW = (contentW - (cols - 1) * gapX) / cols;
            int cardH = Math.Max(120, (availH - (rows - 1) * gapY) / rows);

            int startIndex = _npcGridPage * perPage;
            int count = Math.Min(perPage, _displayNpcCards.Count - startIndex);

            for (int i = 0; i < count; i++)
            {
                var card = _displayNpcCards[startIndex + i];
                int r = i / cols;
                int c = i % cols;

                int cx = leftColX + c * (cardW + gapX);
                int cy = startY + r * (cardH + gapY);
                var cardRect = new Rectangle(cx, cy, cardW, cardH);

                var resetRect = card.HasCustomOverlay
                    ? new Rectangle(cardRect.Left + 6, cardRect.Top + 6, 26, 26)
                    : Rectangle.Empty;

                _visibleCardSlots.Add((card, cardRect, resetRect));
            }

            int pagingY = bottomLimitY - bottomPagingBarH;
            int midX = xPositionOnScreen + width / 2;
            _prevPageBtnRect = new Rectangle(midX - 130, pagingY, 44, 36);
            _nextPageBtnRect = new Rectangle(midX + 86, pagingY, 44, 36);
        }

        private void HandleTab2NpcPageClick(int x, int y)
        {
            if (_filterCheckboxRect.Contains(x, y))
            {
                _filterCustomOnly = !_filterCustomOnly;
                _npcGridPage = 0;
                Game1.playSound("drumkit6");
                ApplyNpcFilter();
                return;
            }

            int totalPages = GetNpcTotalPages();
            if (_prevPageBtnRect.Contains(x, y) && _npcGridPage > 0)
            {
                _npcGridPage--;
                Game1.playSound("shwip");
                RecalculateNpcGridLayout();
                return;
            }
            if (_nextPageBtnRect.Contains(x, y) && _npcGridPage < totalPages - 1)
            {
                _npcGridPage++;
                Game1.playSound("shwip");
                RecalculateNpcGridLayout();
                return;
            }

            foreach (var slot in _visibleCardSlots)
            {
                if (slot.Card.HasCustomOverlay && slot.ResetBtnBounds.Contains(x, y))
                {
                    TryResetNpcBio(slot.Card.Name);
                    return;
                }
                
                if (slot.Bounds.Contains(x, y))
                {
                    Game1.playSound("bigSelect");
                    _wasObscured = true; // 关键：主动记录已被子菜单遮挡
                    Game1.activeClickableMenu = new BioEditorMenu(slot.Card.Name, this);
                    return;
                }
            }
        }

        private void TryResetNpcBio(string npcName)
        {
            if (string.IsNullOrEmpty(npcName) || ModEntry.BioStorage == null)
                return;

            if (!ModEntry.BioStorage.HasCustomOverlay(npcName))
            {
                Game1.playSound("cancel");
                return;
            }

            string target = npcName;
            string disp = Game1.getCharacterFromName(target)?.displayName ?? target;

            Game1.activeClickableMenu = new ConfirmationDialog(
                $"删除 {disp} 的自定义人设覆盖并恢复默认基准？",
                _ =>
                {
                    Game1.activeClickableMenu = this;
                    if (!ModEntry.BioStorage.ResetOverlay(target, out string err))
                    {
                        Game1.addHUDMessage(new HUDMessage($"还原失败: {err}", HUDMessage.error_type));
                        return;
                    }
                    Game1.playSound("throw");
                    RefreshNpcCards();
                },
                _ => Game1.activeClickableMenu = this);
        }

        private static readonly Rectangle LeftArrowSource = new(352, 495, 12, 11);
        private static readonly Rectangle RightArrowSource = new(365, 495, 12, 11);

        private static void DrawArrowButton(SpriteBatch b, Rectangle rect, bool isLeft, bool enabled, int mx, int my)
        {
            bool hover = enabled && rect.Contains(mx, my);
            Color bg = enabled
                ? (hover ? new Color(255, 235, 205) : new Color(139, 90, 43))
                : Color.Gray * 0.5f;

            b.Draw(Game1.staminaRect, new Rectangle(rect.X + 2, rect.Y + 2, rect.Width - 4, rect.Height - 4), bg);
            IClickableMenu.drawTextureBox(b, Game1.mouseCursors,
                new Rectangle(432, 439, 9, 9),
                rect.X, rect.Y, rect.Width, rect.Height, bg, 4f, false);

            Rectangle src = isLeft ? LeftArrowSource : RightArrowSource;
            float arrowScale = 2.4f;
            int iconW = (int)(src.Width * arrowScale);
            int iconH = (int)(src.Height * arrowScale);
            Vector2 iconPos = new Vector2(
                rect.X + (rect.Width - iconW) / 2f,
                rect.Y + (rect.Height - iconH) / 2f);

            Color tint = enabled ? (hover ? Color.White : Color.Wheat) : Color.Gray * 0.5f;
            b.Draw(Game1.mouseCursors, iconPos, src, tint, 0f, Vector2.Zero, arrowScale, SpriteEffects.None, 0.86f);
        }

        private void DrawTab2NpcGridPage(SpriteBatch b)
        {
            int mx = Game1.getMouseX();
            int my = Game1.getMouseY();

            bool filterHover = _filterCheckboxRect.Contains(mx, my);
            Rectangle chkSrc = _filterCustomOnly ? new Rectangle(236, 425, 9, 9) : new Rectangle(227, 425, 9, 9);
            b.Draw(Game1.mouseCursors, new Vector2(_filterCheckboxRect.X, _filterCheckboxRect.Y),
                chkSrc, Color.White, 0f, Vector2.Zero, 3f, SpriteEffects.None, 1f);

            CustomFontManager.DrawStringBold(b, "仅看已自定义",
                new Vector2(_filterCheckboxRect.X + 32, _filterCheckboxRect.Y + 2),
                filterHover ? Game1.textColor : Color.DimGray, RegularFontSize);

            if (filterHover)
            {
                _hoveredGlobalTooltip = "只显示已经配置过自定义人设覆盖的 NPC";
            }

            if (_visibleCardSlots.Count == 0)
            {
                string emptyText = _filterCustomOnly ? "暂无任何自定义覆盖的 NPC" : "未发现任何 NPC 档案";
                var sz = CustomFontManager.MeasureString(emptyText, RegularFontSize);
                CustomFontManager.DrawString(b, emptyText,
                    new Vector2(xPositionOnScreen + (width - sz.X) / 2f, yPositionOnScreen + 260), Color.Gray, RegularFontSize);
            }

            bool isLeftMouseDown = Mouse.GetState().LeftButton == Microsoft.Xna.Framework.Input.ButtonState.Pressed;

            foreach (var slot in _visibleCardSlots)
            {
                var card = slot.Card;
                var rect = slot.Bounds;
                bool isHovered = rect.Contains(mx, my);

                // 1. 卡片外层底板与边框
                b.Draw(Game1.staminaRect, new Rectangle(rect.X + 2, rect.Y + 2, rect.Width, rect.Height), Color.Black * 0.12f);
                b.Draw(Game1.staminaRect, new Rectangle(rect.X + 2, rect.Y + 2, rect.Width - 4, rect.Height - 4), isHovered ? new Color(255, 248, 235) : new Color(254, 247, 238));

                Color cardBorder = isHovered ? new Color(220, 185, 140) : new Color(210, 190, 165);
                IClickableMenu.drawTextureBox(b, Game1.mouseCursors,
                    new Rectangle(432, 439, 9, 9),
                    rect.X, rect.Y, rect.Width, rect.Height, cardBorder, 3.0f, false);

                if (isHovered)
                {
                    b.Draw(Game1.staminaRect, rect, new Color(255, 215, 0) * 0.08f);
                    _hoveredGlobalTooltip = $"{card.DisplayName} - 点击打开人设编辑器";
                }

                // 2. 头像框（适度放大，保持卡片原有高宽比）
                int portraitBoxSize = Math.Clamp(rect.Width - 20, 96, 120);
                int portraitX = rect.X + (rect.Width - portraitBoxSize) / 2;
                int portraitY = rect.Y + 10;
                var portraitBoxRect = new Rectangle(portraitX, portraitY, portraitBoxSize, portraitBoxSize);

                b.Draw(Game1.staminaRect,
                    new Rectangle(portraitBoxRect.X + 2, portraitBoxRect.Y + 2, portraitBoxRect.Width, portraitBoxRect.Height),
                    Color.Black * 0.18f);

                IClickableMenu.drawTextureBox(b, Game1.mouseCursors,
                    new Rectangle(293, 360, 24, 24),
                    portraitBoxRect.X, portraitBoxRect.Y,
                    portraitBoxRect.Width, portraitBoxRect.Height,
                    Color.White, 4f, false);

                var innerPortraitRect = new Rectangle(
                    portraitBoxRect.X + 4,
                    portraitBoxRect.Y + 4,
                    portraitBoxRect.Width - 8,
                    portraitBoxRect.Height - 8);

                // 悬停时切换微笑/开心立绘
                Rectangle targetSourceRect = isHovered ? card.SmileSourceRect : card.DefaultSourceRect;

                if (card.Portrait != null && !targetSourceRect.IsEmpty)
                {
                    b.Draw(card.Portrait, innerPortraitRect, targetSourceRect, Color.White);
                }
                else
                {
                    string placeholder = card.DisplayName.Length > 0 ? card.DisplayName.Substring(0, 1) : "?";
                    var psz = CustomFontManager.MeasureStringBold(placeholder, TitleFontSize);
                    CustomFontManager.DrawStringBold(b, placeholder,
                        new Vector2(innerPortraitRect.X + (innerPortraitRect.Width - psz.X) / 2f,
                                    innerPortraitRect.Y + (innerPortraitRect.Height - psz.Y) / 2f),
                        Color.Gray, TitleFontSize);
                }

                // 3. NPC 名字紧贴在头像下方（居中对齐）
                string dispName = CustomFontManager.TruncateString(card.DisplayName, CardTitleFontSize, rect.Width - 12);
                var nameSz = CustomFontManager.MeasureStringBold(dispName, CardTitleFontSize);

                float nameAreaH = rect.Bottom - portraitBoxRect.Bottom;
                float nameY = portraitBoxRect.Bottom + (nameAreaH - nameSz.Y) / 2f - 2;

                CustomFontManager.DrawStringBold(b, dispName,
                    new Vector2(rect.X + (rect.Width - nameSz.X) / 2f, nameY),
                    isHovered ? new Color(130, 45, 10) : Game1.textColor,
                    CardTitleFontSize);

                // 4. 【补回】自定义覆盖角标标签
                if (card.HasCustomOverlay)
                {
                    string badgeText = "★ 自定义";
                    Color badgeBg = new Color(34, 139, 34);
                    var badgeSz = CustomFontManager.MeasureStringBold(badgeText, SmallFontSize);
                    int bw = (int)badgeSz.X + 8;
                    int bh = (int)badgeSz.Y + 4;
                    int bx = rect.Right - bw - 4;
                    int by = rect.Top + 4;

                    b.Draw(Game1.staminaRect, new Rectangle(bx, by, bw, bh), badgeBg * 0.95f);
                    CustomFontManager.DrawStringBold(b, badgeText, new Vector2(bx + 4, by + 2), Color.White, SmallFontSize);
                }

                // 5. 还原按钮
                if (card.HasCustomOverlay)
                {
                    bool resetHover = slot.ResetBtnBounds.Contains(mx, my);
                    if (resetHover)
                    {
                        _hoveredGlobalTooltip = $"还原 {card.DisplayName} 为默认人设";
                    }

                    bool resetPressed = isLeftMouseDown && resetHover;
                    IconState iconState = resetPressed ? IconState.Pressed : IconState.Normal;

                    Color resetBg = resetHover ? new Color(255, 235, 205) : new Color(210, 180, 140) * 0.9f;
                    b.Draw(Game1.staminaRect, new Rectangle(slot.ResetBtnBounds.X + 1, slot.ResetBtnBounds.Y + 1, slot.ResetBtnBounds.Width - 2, slot.ResetBtnBounds.Height - 2), resetBg);
                    IClickableMenu.drawTextureBox(b, Game1.mouseCursors,
                        new Rectangle(432, 439, 9, 9),
                        slot.ResetBtnBounds.X, slot.ResetBtnBounds.Y,
                        slot.ResetBtnBounds.Width, slot.ResetBtnBounds.Height,
                        resetBg, 2.0f, false);

                    if (ModEntry.CustomIcons != null)
                    {
                        Rectangle restoreSrc = IconSource.Restore(IconTheme.Wood, iconState);
                        int iconTargetSize = 18;
                        int ix = slot.ResetBtnBounds.X + (slot.ResetBtnBounds.Width - iconTargetSize) / 2;
                        int iy = slot.ResetBtnBounds.Y + (slot.ResetBtnBounds.Height - iconTargetSize) / 2;
                        if (resetPressed)
                        {
                            iy += 1;
                        }

                        b.Draw(ModEntry.CustomIcons,
                            new Rectangle(ix, iy, iconTargetSize, iconTargetSize),
                            restoreSrc, Color.White);
                    }
                }
            }

            // 6. 分页与底部页码
            int totalPages = GetNpcTotalPages();
            int curPage = _npcGridPage + 1;

            bool prevEnabled = _npcGridPage > 0;
            bool nextEnabled = _npcGridPage < totalPages - 1;

            DrawArrowButton(b, _prevPageBtnRect, isLeft: true, enabled: prevEnabled, mx, my);

            string pageInfo = $"第 {curPage} / {totalPages} 页 (共 {_displayNpcCards.Count} 人)";
            var pageInfoSz = CustomFontManager.MeasureStringBold(pageInfo, RegularFontSize);
            CustomFontManager.DrawStringBold(b, pageInfo,
                new Vector2(xPositionOnScreen + (width - pageInfoSz.X) / 2f, _prevPageBtnRect.Y + (_prevPageBtnRect.Height - pageInfoSz.Y) / 2f),
                Game1.textColor, RegularFontSize);

            DrawArrowButton(b, _nextPageBtnRect, isLeft: false, enabled: nextEnabled, mx, my);
        }

        // ── Tab3（高级设置）══════════════════════════════════════════════

        private void DrawTab3(SpriteBatch b)
        {
            int mx = Game1.getMouseX();
            int my = Game1.getMouseY();

            DrawSettingRow(b, _tab3RowInfinite, _tab3CheckboxInfinite, ModEntry.Config.EnableInfiniteChat,
                I18n.AdvancedSettings.InfiniteChat(),
                I18n.AdvancedSettings.InfiniteChatTooltip(), mx, my);

            DrawSettingRow(b, _tab3RowVanillaFirst, _tab3CheckboxVanillaFirst, ModEntry.Config.EnableVanillaFirst,
                I18n.AdvancedSettings.VanillaFirst(),
                I18n.AdvancedSettings.VanillaFirstTooltip(), mx, my);

            DrawSettingRow(b, _tab3RowRecordVanilla, _tab3CheckboxRecordVanilla, ModEntry.Config.RecordVanillaDialogue,
                I18n.AdvancedSettings.RecordVanillaDialogue(),
                I18n.AdvancedSettings.RecordVanillaDialogueTooltip(), mx, my);

            DrawSettingRow(b, _tab3RowRecordEvent, _tab3CheckboxRecordEvent, ModEntry.Config.RecordEventDialogue,
                I18n.AdvancedSettings.RecordEventDialogue(),
                I18n.AdvancedSettings.RecordEventDialogueTooltip(), mx, my);

            string disclaimer = Game1.parseText(I18n.AdvancedSettings.Disclaimer(),
                Game1.smallFont, width - LeftPadding - RightPadding - 40);

            var disclaimerSize = CustomFontManager.MeasureString(disclaimer, RegularFontSize);
            int disclaimerY = yPositionOnScreen + height - BottomPadding + 10;

            CustomFontManager.DrawString(b, disclaimer,
                new Vector2(xPositionOnScreen + (width - disclaimerSize.X) / 2f, disclaimerY),
                Color.DimGray, RegularFontSize);
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

            CustomFontManager.DrawStringBold(b, label,
                new Vector2(checkboxRect.X + 44, checkboxRect.Y),
                isHover ? new Color(0, 0, 50) : Game1.textColor, RegularFontSize);

            CustomFontManager.DrawString(b, desc,
                new Vector2(checkboxRect.X + 44, checkboxRect.Y + 22),
                Color.Gray * 0.9f, SmallFontSize);
        }

        private void DrawBottomButtons(SpriteBatch b)
        {
            int mx = Game1.getMouseX();
            int my = Game1.getMouseY();

            if (_currentTab == 0)
            {
                string distillText = I18n.Memory.DistillButton();
                bool distillHover = _aiExtractButtonRect.Contains(mx, my);
                Color distillBg = distillHover ? new Color(255, 235, 205) : new Color(139, 90, 43);

                b.Draw(Game1.staminaRect, new Rectangle(_aiExtractButtonRect.X + 2, _aiExtractButtonRect.Y + 2, _aiExtractButtonRect.Width - 4, _aiExtractButtonRect.Height - 4), distillBg);
                IClickableMenu.drawTextureBox(b, Game1.mouseCursors,
                    new Rectangle(432, 439, 9, 9),
                    _aiExtractButtonRect.X, _aiExtractButtonRect.Y,
                    _aiExtractButtonRect.Width, _aiExtractButtonRect.Height,
                    distillBg, 4f, false);

                var distillLabelSize = CustomFontManager.MeasureStringBold(distillText, TabFontSize);
                CustomFontManager.DrawStringBold(b, distillText,
                    new Vector2(
                        _aiExtractButtonRect.X + (_aiExtractButtonRect.Width - distillLabelSize.X) / 2f,
                        _aiExtractButtonRect.Y + (_aiExtractButtonRect.Height - distillLabelSize.Y) / 2f),
                    distillHover ? Game1.textColor : Color.White, TabFontSize);

                string manualText = I18n.Memory.AddButton();
                bool manualHover = _manualAddRect.Contains(mx, my);
                Color manualBg = manualHover ? new Color(255, 235, 205) : new Color(139, 90, 43);

                b.Draw(Game1.staminaRect, new Rectangle(_manualAddRect.X + 2, _manualAddRect.Y + 2, _manualAddRect.Width - 4, _manualAddRect.Height - 4), manualBg);
                IClickableMenu.drawTextureBox(b, Game1.mouseCursors,
                    new Rectangle(432, 439, 9, 9),
                    _manualAddRect.X, _manualAddRect.Y,
                    _manualAddRect.Width, _manualAddRect.Height,
                    manualBg, 4f, false);

                var manualLabelSize = CustomFontManager.MeasureStringBold(manualText, TabFontSize);
                CustomFontManager.DrawStringBold(b, manualText,
                    new Vector2(
                        _manualAddRect.X + (_manualAddRect.Width - manualLabelSize.X) / 2f,
                        _manualAddRect.Y + (_manualAddRect.Height - manualLabelSize.Y) / 2f),
                    manualHover ? Game1.textColor : Color.White, TabFontSize);

                string archiveText = I18n.Memory.ArchiveButton(_archivedCount, MemoryManager.MaxArchivedMemoriesPerNpc);
                bool archiveHover = _archiveButtonRect.Contains(mx, my);
                Color archiveBg = archiveHover ? new Color(255, 235, 205) : new Color(139, 90, 43);

                b.Draw(Game1.staminaRect, new Rectangle(_archiveButtonRect.X + 2, _archiveButtonRect.Y + 2, _archiveButtonRect.Width - 4, _archiveButtonRect.Height - 4), archiveBg);
                IClickableMenu.drawTextureBox(b, Game1.mouseCursors,
                    new Rectangle(432, 439, 9, 9),
                    _archiveButtonRect.X, _archiveButtonRect.Y,
                    _archiveButtonRect.Width, _archiveButtonRect.Height,
                    archiveBg, 4f, false);

                var archiveLabelSize = CustomFontManager.MeasureStringBold(archiveText, TabFontSize);
                CustomFontManager.DrawStringBold(b, archiveText,
                    new Vector2(
                        _archiveButtonRect.X + (_archiveButtonRect.Width - archiveLabelSize.X) / 2f,
                        _archiveButtonRect.Y + (_archiveButtonRect.Height - archiveLabelSize.Y) / 2f),
                    archiveHover ? Game1.textColor : Color.White, TabFontSize);
            }
            else
            {
                string addText = I18n.Memory.AddWorldButton();
                bool addHover = _addButtonRect.Contains(mx, my);
                Color addBg = addHover ? new Color(255, 235, 205) : new Color(139, 90, 43);

                b.Draw(Game1.staminaRect, new Rectangle(_addButtonRect.X + 2, _addButtonRect.Y + 2, _addButtonRect.Width - 4, _addButtonRect.Height - 4), addBg);
                IClickableMenu.drawTextureBox(b, Game1.mouseCursors,
                    new Rectangle(432, 439, 9, 9),
                    _addButtonRect.X, _addButtonRect.Y,
                    _addButtonRect.Width, _addButtonRect.Height,
                    addBg, 4f, false);

                var addLabelSize = CustomFontManager.MeasureStringBold(addText, TabFontSize);
                CustomFontManager.DrawStringBold(b, addText,
                    new Vector2(
                        _addButtonRect.X + (_addButtonRect.Width - addLabelSize.X) / 2f,
                        _addButtonRect.Y + (_addButtonRect.Height - addLabelSize.Y) / 2f),
                    addHover ? Game1.textColor : Color.White, TabFontSize);
            }

            string cap = _currentTab == 0
                ? $"{MemoryManager.Instance.GetManualMemoryCount(_currentNpcName)} / {MemoryManager.MaxMemoriesPerNpc}"
                : $"{ActiveEntries.Count} / {MaxEntriesForTab}";

            CustomFontManager.DrawString(b, cap,
                new Vector2(xPositionOnScreen + width - RightPadding - 120, _listTopY - 10),
                Color.Gray, SmallFontSize);
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
                int y = _listTopY + 10 + i * LineHeight + 7;

                var del = new ClickableTextureComponent(
                    new Rectangle(xPositionOnScreen + width - RightPadding - 32, y, ButtonSize, ButtonSize),
                    ModEntry.CustomIcons,
                    IconSource.Trash(IconTheme.Wood, IconState.Normal),
                    2f)
                {
                    hoverText = I18n.Memory.DeleteButtonHover()
                };
                _deleteButtons.Add(del);

                var edit = new ClickableTextureComponent(
                    new Rectangle(xPositionOnScreen + width - RightPadding - 72, y, ButtonSize, ButtonSize),
                    ModEntry.CustomIcons,
                    IconSource.Edit(IconTheme.Wood, IconState.Normal),
                    2f)
                {
                    hoverText = I18n.Memory.EditButtonHover()
                };
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
            width = Math.Max(700, Math.Min(1000, Game1.uiViewport.Width - 80));
            height = Math.Max(520, Math.Min(680, Game1.uiViewport.Height - 80));
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

        // ── 嵌套组件：下拉列表（无缝平铺，彻底杜绝选项白缝） ─────────

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

                var size = CustomFontManager.MeasureStringBold(label, TabFontSize);
                CustomFontManager.DrawStringBold(b, label,
                    new Vector2(_headerRect.X + 16,
                        _headerRect.Y + (_headerRect.Height - size.Y) / 2f),
                    hover && !_isOpen ? Game1.textColor : Color.White, TabFontSize);

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

                    string displayLabel = CustomFontManager.TruncateString(item.Label, RegularFontSize, ir.Width - 36);

                    if (selected || ihover)
                    {
                        CustomFontManager.DrawStringBold(b, displayLabel,
                            new Vector2(ir.X + 16, ir.Y + (ir.Height - CustomFontManager.MeasureStringBold("A", RegularFontSize).Y) / 2f),
                            textColor, RegularFontSize);
                    }
                    else
                    {
                        CustomFontManager.DrawString(b, displayLabel,
                            new Vector2(ir.X + 16, ir.Y + (ir.Height - CustomFontManager.MeasureString("A", RegularFontSize).Y) / 2f),
                            textColor, RegularFontSize);
                    }
                }

                // 3. 弹层整体统一加外边框
                IClickableMenu.drawTextureBox(b, Game1.mouseCursors,
                    new Rectangle(432, 439, 9, 9),
                    popupRect.X, popupRect.Y, popupRect.Width, popupRect.Height,
                    new Color(210, 180, 140), 2.5f, false);
            }
        }

        private static void DrawHoverTextCustom(SpriteBatch b, string text)
        {
            if (string.IsNullOrEmpty(text)) return;

            var sz = CustomFontManager.MeasureString(text, RegularFontSize);

            // ── 宽裕适中的内外边距 ──
            const int padX = 20; 
            const int padY = 12; 

            int boxW = (int)MathF.Ceiling(sz.X) + padX * 2;
            int boxH = (int)MathF.Ceiling(sz.Y) + padY * 2;

            int x = Game1.getOldMouseX() + 24;
            int y = Game1.getOldMouseY() + 24;
            var safe = Utility.getSafeArea();

            // 边界碰撞防溢出
            if (x + boxW > safe.Right)
                x = safe.Right - boxW;
            if (y + boxH > safe.Bottom)
            {
                x += 16;
                if (x + boxW > safe.Right)
                    x = safe.Right - boxW;
                y = safe.Bottom - boxH;
            }
            if (x < safe.Left)
                x = safe.Left;
            if (y < safe.Top)
                y = safe.Top;

            // 1. 原版像素软阴影
            IClickableMenu.drawTextureBox(b, Game1.menuTexture, new Rectangle(0, 256, 60, 60),
                x + 4, y + 4, boxW, boxH, Color.Black * 0.28f, 0.65f, false);

            // 2. 星露谷原版暖白/浅亮羊皮纸底框（无白缝断层，边缘自带原生柔和像素勾边）
            IClickableMenu.drawTextureBox(b, Game1.menuTexture, new Rectangle(0, 256, 60, 60),
                x, y, boxW, boxH, new Color(255, 255, 250), 0.65f, false);

            // 3. 提示文字（垂直严格居中，间距舒适）
            float textY = y + (boxH - sz.Y) / 2f - 1;
            CustomFontManager.DrawString(b, text, 
                new Vector2(x + padX, textY), 
                Game1.textColor, RegularFontSize);
        }
    }
}