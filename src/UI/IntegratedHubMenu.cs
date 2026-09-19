using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;
using System;
using System.Collections.Generic;
using System.Linq;
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
            public Rectangle SmileSourceRect;
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

            _npcDropdown = new DropdownList(Rectangle.Empty)
            {
                HeaderPrefix = I18n.Hub.SelectNpcLabel(),
                OnItemSelected = name => SelectNpc(name)
            };

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
            _cachedEntries = _currentTab switch
            {
                0 => SafeGetMemories(),
                1 => SafeGetWorldEntries(),
                _ => new List<MemoryEntry>()
            };

            _listTopY = yPositionOnScreen + TopPadding + (_currentTab == 0 ? TabHeight + 8 : 0);

            if (_currentTab == 2 && !_tab2Initialized)
                InitializeTab2();

            if (_currentTab == 2 && _profileSubTab == 1)
                RefreshNpcCards();

            if (_currentTab == 2 && _bioTextBox != null && _bioTextBox.Selected
                && Game1.keyboardDispatcher.Subscriber == null)
                Game1.keyboardDispatcher.Subscriber = _bioTextBox;

            _archivedCount = (_currentTab == 0 && !string.IsNullOrEmpty(_currentNpcName))
                ? MemoryManager.Instance.GetArchivedCount(_currentNpcName)
                : 0;

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
            string safeContent = UiHelper.TruncateString(entry.Content, Game1.dialogueFont, 320f);

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
                if (_profileSubTab == 1)
                {
                    // NPC 档案宫格页支持滚轮直接翻页
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

                if (_aiExtractButtonRect.Contains(x, y))
                {
                    TryOpenDistillMenu();
                    return;
                }

                if (_manualAddRect.Contains(x, y))
                {
                    OpenAddMemory();
                    return;
                }

                if (_archiveButtonRect.Contains(x, y))
                {
                    if (string.IsNullOrEmpty(_currentNpcName))
                    {
                        Game1.playSound("cancel");
                        return;
                    }

                    Game1.playSound("bigSelect");
                    ReleaseKeyboard();
                    Game1.activeClickableMenu = new ArchivedMemoryMenu(_currentNpcName, this);
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
            _hoveredGlobalTooltip = null;
            int mx = Game1.getMouseX();
            int my = Game1.getMouseY();

            // 背景遮罩
            b.Draw(Game1.fadeToBlackRect,
                Game1.graphics.GraphicsDevice.Viewport.Bounds, Color.Black * 0.45f);

            // 双层精致木质边框
            IClickableMenu.drawTextureBox(b,
                xPositionOnScreen - 16, yPositionOnScreen - 16,
                width + 32, height + 32, Color.White);

            IClickableMenu.drawTextureBox(b,
                xPositionOnScreen - 8, yPositionOnScreen - 8,
                width + 16, height + 16, Color.White);

            // 主面板羊皮纸
            Game1.drawDialogueBox(xPositionOnScreen, yPositionOnScreen, width, height, false, true);

            // 标题
            string title = I18n.Hub.Title();
            var titleSize = Game1.dialogueFont.MeasureString(title);
            b.DrawString(Game1.dialogueFont, title,
                new Vector2(xPositionOnScreen + (width - titleSize.X) / 2f, yPositionOnScreen + 12),
                Game1.textColor);

            // 四个自适应 Tab
            DrawTab(b, _tabRects[0], I18n.Hub.TabNpcMemory(), _currentTab == 0, mx, my);
            DrawTab(b, _tabRects[1], I18n.Hub.TabWorldMemory(), _currentTab == 1, mx, my);
            DrawTab(b, _tabRects[2], I18n.Hub.TabProfile(), _currentTab == 2, mx, my);
            DrawTab(b, _tabRects[3], I18n.Hub.TabAdvanced(), _currentTab == 3, mx, my);

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

            if (_currentTab == 0 || _currentTab == 1)
                DrawBottomButtons(b);

            UiHelper.UpdateButtonScale(ref _closeButtonHoverScale, _closeButton, mx, my);
            _closeButton.scale = _closeButtonBaseScale * _closeButtonHoverScale;
            _closeButton.draw(b);

            if (_currentTab == 0)
                _npcDropdown.Draw(b);

            if (_currentTab == 0 || _currentTab == 1)
            {
                for (int i = 0; i < _deleteButtons.Count; i++)
                {
                    if (_deleteButtons[i].containsPoint(mx, my))
                    {
                        IClickableMenu.drawHoverText(b, _deleteButtons[i].hoverText, Game1.smallFont);
                        break;
                    }
                    if (i < _editButtons.Count && _editButtons[i].containsPoint(mx, my))
                    {
                        IClickableMenu.drawHoverText(b, _editButtons[i].hoverText, Game1.smallFont);
                        break;
                    }
                }
            }

            if (!string.IsNullOrEmpty(_hoveredGlobalTooltip))
            {
                IClickableMenu.drawHoverText(b, _hoveredGlobalTooltip, Game1.smallFont);
            }

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
            int mx = Game1.getMouseX();
            int my = Game1.getMouseY();
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

            const float fontScale = 0.8f;
            float fixedDateWidth = Game1.smallFont.MeasureString("2026-12-31 00:00").X * fontScale;
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

                if (rowRect.Contains(Game1.getMouseX(), Game1.getMouseY()))
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
                string text = UiHelper.TruncateString(fullRawText, Game1.dialogueFont, maxContentWidth, fontScale);
                string dateText = entry.CreatedAt.ToString("yyyy-MM-dd HH:mm");

                b.DrawString(Game1.dialogueFont, text,
                    new Vector2(contentStartX, rowY + 4),
                    textColor, 0f, Vector2.Zero, fontScale, SpriteEffects.None, 0.88f);

                b.DrawString(Game1.smallFont, dateText,
                    new Vector2(dateX, rowY + 6),
                    Color.Gray, 0f, Vector2.Zero, fontScale, SpriteEffects.None, 0.88f);

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
                Font = Game1.smallFont,
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
            _tab2RightColX = leftColX + 130 + 16;
            _tab2RightColW = (xPositionOnScreen + width - RightPadding) - _tab2RightColX;

            int subTabY = yPositionOnScreen + TopPadding;
            int subTabW = 160;
            _subTabPlayerRect = new Rectangle(leftColX, subTabY, subTabW, SubTabBarH);
            _subTabNpcRect = new Rectangle(leftColX + subTabW + 12, subTabY, subTabW, SubTabBarH);

            // 右上角筛选框位置（与分段条并排）
            _filterCheckboxRect = new Rectangle(leftColX + contentW - 170, subTabY + 2, 28, 28);

            // 玩家档案页控件布局
            int y = yPositionOnScreen + TopPadding + SubTabContentOffset + 6;
            _enableProfileCheckboxRect = new Rectangle(leftColX, y, 36, 36);

            int orientationY = y + 40;
            _orientationLabelRect = new Rectangle(leftColX, orientationY, 130, TabHeight);
            _orientationDropdownRect = new Rectangle(_tab2RightColX, orientationY, _tab2RightColW, TabHeight);
            _orientationDropdown?.SetHeaderBounds(_orientationDropdownRect);

            int safetyLabelY = orientationY + TabHeight + 8;
            _safetyLabelRect = new Rectangle(leftColX, safetyLabelY, contentW, 26);

            int safetySliderY = safetyLabelY + 28;
            int trackW = Math.Min(260, contentW - 80);
            int trackX = xPositionOnScreen + (width - trackW) / 2;
            _safetySliderRect = new Rectangle(trackX, safetySliderY, trackW, 24);

            int modeTextY = safetySliderY + 24 + 6;
            int descStartY = modeTextY + 24 + 4;

            int maxDescW = contentW - 20;
            int wrapW = (int)(maxDescW / 0.85f);
            string desc = Game1.parseText(I18n.Profile.SafetyDesc(), Game1.smallFont, wrapW);
            string[] descLines = desc.Split('\n');
            int lineSpacing = (int)(Game1.smallFont.LineSpacing * 0.82f);
            int descHeight = descLines.Length * lineSpacing;

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

            // ── 玩家档案 ──
            bool enabled = ModEntry.Config.EnablePlayerProfile;
            Rectangle enableSrc = enabled ? new Rectangle(236, 425, 9, 9) : new Rectangle(227, 425, 9, 9);
            b.Draw(Game1.mouseCursors, new Vector2(_enableProfileCheckboxRect.X, _enableProfileCheckboxRect.Y),
                enableSrc, Color.White, 0f, Vector2.Zero, 4f, SpriteEffects.None, 1f);
            b.DrawString(Game1.smallFont, I18n.Profile.EnableProfile(),
                new Vector2(_enableProfileCheckboxRect.X + 44, _enableProfileCheckboxRect.Y + 4),
                Game1.textColor);

            if (!enabled)
                return;

            b.DrawString(Game1.smallFont, I18n.Profile.OrientationLabel(),
                new Vector2(_orientationLabelRect.X, _orientationLabelRect.Y + 6), Game1.textColor);

            b.DrawString(Game1.smallFont, I18n.Profile.RomanceSafetyLabel(),
                new Vector2(_safetyLabelRect.X, _safetyLabelRect.Y + 2), Game1.textColor);

            int trackX = _safetySliderRect.X;
            int trackW = _safetySliderRect.Width;
            int trackY = _safetySliderRect.Y;

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
            var labelSize = Game1.smallFont.MeasureString(currentLabel);
            float labelX = xPositionOnScreen + (width - labelSize.X) / 2f;
            int labelY = trackY + 24 + 6;

            b.DrawString(Game1.smallFont, currentLabel, new Vector2(labelX, labelY), Game1.textColor);

            int maxDescW = width - LeftPadding - RightPadding - 20;
            int wrapW = (int)(maxDescW / 0.85f);
            string desc = Game1.parseText(I18n.Profile.SafetyDesc(), Game1.smallFont, wrapW);
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

            b.DrawString(Game1.smallFont, I18n.Profile.BioLabel(),
                new Vector2(_bioLabelRect.X, _bioLabelRect.Y + 4), Game1.textColor);

            IClickableMenu.drawTextureBox(b, _bioBoxRect.X - 4, _bioBoxRect.Y - 4, _bioBoxRect.Width + 8, _bioBoxRect.Height + 8, Color.White);

            _bioTextBox.Position = new Vector2(_bioBoxRect.X, _bioBoxRect.Y);
            _bioTextBox.Update(Game1.currentGameTime);
            _bioTextBox.Draw(b);

            if (string.IsNullOrWhiteSpace(_bioTextBox.Text))
            {
                string placeholder = Game1.parseText(I18n.Profile.BioPlaceholder(), Game1.smallFont, _bioBoxRect.Width - 28);
                b.DrawString(Game1.smallFont, placeholder, new Vector2(_bioBoxRect.X + 8, _bioBoxRect.Y + 8), Color.Gray * 0.7f);
            }

            string counterText = $"{_bioTextBox.Text?.Length ?? 0}/300";
            var counterSize = Game1.smallFont.MeasureString(counterText);
            Color counterColor = (_bioTextBox.Text?.Length ?? 0) >= 300 ? Color.Red : Color.Gray * 0.8f;
            b.DrawString(Game1.smallFont, counterText,
                new Vector2(_bioBoxRect.Right - counterSize.X - 8, _bioBoxRect.Bottom - counterSize.Y - 6), counterColor);

            bool saveHover = _saveButtonRect.Contains(mx, my);
            Color saveBg = saveHover ? Color.Wheat : Color.White;
            IClickableMenu.drawTextureBox(b, _saveButtonRect.X, _saveButtonRect.Y, _saveButtonRect.Width, _saveButtonRect.Height, saveBg);
            string saveText = I18n.Profile.SaveButton();
            var saveSize = Game1.dialogueFont.MeasureString(saveText);
            b.DrawString(Game1.dialogueFont, saveText,
                new Vector2(_saveButtonRect.X + (_saveButtonRect.Width - saveSize.X) / 2f,
                            _saveButtonRect.Y + (_saveButtonRect.Height - saveSize.Y) / 2f),
                saveHover ? Game1.textColor : Color.Black);

            _orientationDropdown.Draw(b);
        }

        private void DrawTab2SubTab(SpriteBatch b, Rectangle rect, string label, bool isActive, int mx, int my)
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

        // ── Tab2 NPC 宫格卡片重构核心 ──────────────────────────────────────

        /// <summary>
        /// 获取星露谷标准肖像“微笑”表情的源矩形切片。
        /// 星露谷 64x64 立绘双列排布：第0帧=正常，第1帧(64,0)=微笑开心。
        /// </summary>
        private static Rectangle GetSmilingPortraitSource(Texture2D portrait)
        {
            if (portrait == null) return Rectangle.Empty;

            // 标准 128px 宽双列立绘：第 1 帧微笑
            if (portrait.Width >= 128 && portrait.Height >= 64)
            {
                return new Rectangle(64, 0, 64, 64);
            }
            // 单列立绘变种：第 1 帧微笑位于正下方
            if (portrait.Width >= 64 && portrait.Height >= 128)
            {
                return new Rectangle(0, 64, 64, 64);
            }
            // 兜底截取首帧
            return new Rectangle(0, 0, Math.Min(64, portrait.Width), Math.Min(64, portrait.Height));
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
                catch
                {
                    // 无法加载头像时静默捕获，卡片中将优雅降级绘制
                }
            }

            _portraitCache[npcName] = loaded;
            return loaded;
        }

        private void RefreshNpcCards()
{
    _allNpcCards.Clear();
    var friendshipData = Game1.player?.friendshipData;

    // 1. 系统/过场演出用假人/特殊剧情专用角色黑名单
    var excludedNpcs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Grandpa",   // 爷爷
        "Governor",  // 州长
        "Gil",       // 吉尔
        "Bouncer",   // 赌场保镖
        "Birdie",    // 伯迪
        "Henchman",  // 仆从
        "MarlonFudge"// 1.6 矿洞/特殊剧情马龙
    };

    // 辅助判定：是否为剧情演出或无效假人
    bool IsInvalidOrEventNpc(string internalName, NPC npc)
    {
        if (string.IsNullOrWhiteSpace(internalName)) return true;

        // 命中硬编码黑名单
        if (excludedNpcs.Contains(internalName))
            return true;

        // 剧情演出假人常见命名模式：包含下划线、Event、Fake、Dummy 等
        if (internalName.Contains("_") || 
            internalName.Contains("Event", StringComparison.OrdinalIgnoreCase) ||
            internalName.Contains("Fake", StringComparison.OrdinalIgnoreCase) ||
            internalName.Contains("Dummy", StringComparison.OrdinalIgnoreCase))
            return true;

        // 关键：针对 Marlon 的变体（如 MarlonFudge, MarlonFestival 等）坚决剔除，
        // 只有纯正的 "Marlon" 才是本体
        bool isTrueMarlon = internalName.Equals("Marlon", StringComparison.OrdinalIgnoreCase);
        if (internalName.StartsWith("Marlon", StringComparison.OrdinalIgnoreCase) && !isTrueMarlon)
            return true;

        bool inFriendship = friendshipData != null && friendshipData.ContainsKey(internalName);

        // 如果获取到了运行时的 NPC 实例进一步校验
        if (npc != null)
        {
            // 通过当前过场的 actors 列表判定是否为过场临时演员
            if (Game1.CurrentEvent != null && Game1.CurrentEvent.actors != null && Game1.CurrentEvent.actors.Contains(npc))
                return true;

            // 修复核心：原版马龙不可社交(CanSocialize=false)且不在好感度中，但他是探险家公会核心NPC，必须放行！
            if (!isTrueMarlon && !npc.CanSocialize && !inFriendship)
                return true;
        }
        else
        {
            // 无运行时实例且不在好感度表中时：除了正统 Marlon 外，其他均视作无效
            if (!isTrueMarlon && !inFriendship)
                return true;
        }

        return false;
    }

    // 收集所有候选角色名字
    var rawCandidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    // 1) 优先从好感度列表（最可信）提取
    if (friendshipData != null)
    {
        foreach (var k in friendshipData.Keys)
        {
            if (!IsInvalidOrEventNpc(k, Game1.getCharacterFromName(k)))
                rawCandidates.Add(k);
        }
    }

    // 2) 从 CharacterData 基础表补充
    if (Game1.characterData != null)
    {
        foreach (var kvp in Game1.characterData)
        {
            string name = kvp.Key;
            if (!IsInvalidOrEventNpc(name, Game1.getCharacterFromName(name)))
                rawCandidates.Add(name);
        }
    }

    // 3) 从场景活跃角色补充
    foreach (var npc in Utility.getAllCharacters())
    {
        if (npc != null && (npc.IsVillager || npc.Name.Equals("Marlon", StringComparison.OrdinalIgnoreCase)) && !IsInvalidOrEventNpc(npc.Name, npc))
        {
            rawCandidates.Add(npc.Name);
        }
    }

    // 4) 确保正统马龙必须在候选列表内
    if (!rawCandidates.Contains("Marlon"))
    {
        rawCandidates.Add("Marlon");
    }

    // 2. 核心去重：按照 DisplayName 去重，防止任何模组克隆人导致双马龙
    var resolvedCards = new Dictionary<string, NpcCardInfo>(StringComparer.OrdinalIgnoreCase);

    foreach (var name in rawCandidates)
    {
        var portrait = SafeLoadPortrait(name);
        if (portrait == null || portrait.IsDisposed)
            continue;

        var smileRect = GetSmilingPortraitSource(portrait);
        if (smileRect.IsEmpty || smileRect.Width <= 0 || smileRect.Height <= 0)
            continue;

        string dispName = Game1.getCharacterFromName(name)?.displayName;
        if (string.IsNullOrWhiteSpace(dispName)) dispName = name;

        bool hasCustom = ModEntry.BioStorage != null && ModEntry.BioStorage.HasCustomOverlay(name);
        bool hasFriendship = friendshipData != null && friendshipData.ContainsKey(name);

        var card = new NpcCardInfo
        {
            Name = name,
            DisplayName = dispName,
            Portrait = portrait,
            SmileSourceRect = smileRect,
            HasCustomOverlay = hasCustom
        };

        // 如果遭遇同名 NPC（例如有两个“马龙”或两个“Marlon”），仲裁保留最正规的本体
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
                // 保留原有的正统马龙，跳过当前克隆人
                continue;
            }
            else
            {
                // 其他普通角色按 好感度 > 名称短 去重
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

            // 底部翻页控制器的高度预留
            const int bottomPagingBarH = 40;
            const int bottomMargin = 8;
            int bottomLimitY = yPositionOnScreen + height - BottomPadding;
            int availH = bottomLimitY - startY - bottomPagingBarH - bottomMargin;

            int cols = GetNpcCols(); // 5
            int rows = GetNpcRows(); // 2
            int perPage = cols * rows; // 10

            const int gapX = 12;
            const int gapY = 12;

            // 均分计算卡片宽高，不再做高度截断，让其撑满垂直与水平空间
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
                    ? new Rectangle(cardRect.Left + 6, cardRect.Top + 6, 22, 22)
                    : Rectangle.Empty;

                _visibleCardSlots.Add((card, cardRect, resetRect));
            }

            // 底部翻页控件对齐到面板底部
            int pagingY = bottomLimitY - bottomPagingBarH;
            int midX = xPositionOnScreen + width / 2;
            _prevPageBtnRect = new Rectangle(midX - 130, pagingY, 44, 36);
            _nextPageBtnRect = new Rectangle(midX + 86, pagingY, 44, 36);
        }

        private void HandleTab2NpcPageClick(int x, int y)
        {
            // 1. 过滤开关
            if (_filterCheckboxRect.Contains(x, y))
            {
                _filterCustomOnly = !_filterCustomOnly;
                _npcGridPage = 0;
                Game1.playSound("drumkit6");
                ApplyNpcFilter();
                return;
            }

            // 2. 翻页
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

            // 3. 点击卡片或重置按钮
            foreach (var slot in _visibleCardSlots)
            {
                if (slot.Card.HasCustomOverlay && slot.ResetBtnBounds.Contains(x, y))
                {
                    TryResetNpcBio(slot.Card.Name);
                    return;
                }

                if (slot.Bounds.Contains(x, y))
                {
                    _currentNpcName = slot.Card.Name;
                    Game1.playSound("bigSelect");
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

        // 参考 TimelineChronicleMenu 的原版箭头贴图切片 (mouseCursors)
        private static readonly Rectangle LeftArrowSource = new(352, 495, 12, 11);
        private static readonly Rectangle RightArrowSource = new(365, 495, 12, 11);

        private static void DrawArrowButton(SpriteBatch b, Rectangle rect, bool isLeft, bool enabled, int mx, int my)
        {
            bool hover = enabled && rect.Contains(mx, my);
            Color bg = enabled
                ? (hover ? new Color(255, 235, 205) : new Color(139, 90, 43))
                : Color.Gray * 0.5f;

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

            // ── 顶部右侧筛选控制 ──
            bool filterHover = _filterCheckboxRect.Contains(mx, my);
            Rectangle chkSrc = _filterCustomOnly ? new Rectangle(236, 425, 9, 9) : new Rectangle(227, 425, 9, 9);
            b.Draw(Game1.mouseCursors, new Vector2(_filterCheckboxRect.X, _filterCheckboxRect.Y),
                chkSrc, Color.White, 0f, Vector2.Zero, 3f, SpriteEffects.None, 1f);

            b.DrawString(Game1.smallFont, "仅看已自定义",
                new Vector2(_filterCheckboxRect.X + 32, _filterCheckboxRect.Y + 2),
                filterHover ? Game1.textColor : Color.DimGray);

            if (filterHover)
            {
                _hoveredGlobalTooltip = "只显示已经配置过自定义人设覆盖的 NPC";
            }

            // ── 宫格卡片绘制 ──
            if (_visibleCardSlots.Count == 0)
            {
                string emptyText = _filterCustomOnly ? "暂无任何自定义覆盖的 NPC" : "未发现任何 NPC 档案";
                var sz = Game1.dialogueFont.MeasureString(emptyText);
                b.DrawString(Game1.dialogueFont, emptyText,
                    new Vector2(xPositionOnScreen + (width - sz.X) / 2f, yPositionOnScreen + 260), Color.Gray);
            }

            foreach (var slot in _visibleCardSlots)
            {
                var card = slot.Card;
                var rect = slot.Bounds;
                bool isHovered = rect.Contains(mx, my);

                // 卡片外框背景
                Color cardBg = isHovered ? new Color(255, 245, 220) : Color.White;
                IClickableMenu.drawTextureBox(b, Game1.mouseCursors,
                    new Rectangle(432, 439, 9, 9),
                    rect.X, rect.Y, rect.Width, rect.Height, cardBg, 4f, false);

                // 悬浮高亮效果
                if (isHovered)
                {
                    b.Draw(Game1.staminaRect, rect, new Color(255, 215, 0) * 0.12f);
                    _hoveredGlobalTooltip = $"{card.DisplayName} - 点击打开人设编辑器";
                }

                // ── 1. 大比例头像框（占据卡片主要宽度与高度） ──
                // 卡片宽度通常在 115~135px 之间，设置头像框为 92~104px 左右，充满方块
                int portraitBoxSize = Math.Clamp(rect.Width - 28, 86, 106);
                int portraitX = rect.X + (rect.Width - portraitBoxSize) / 2;
                int portraitY = rect.Y + 12;
                var portraitBoxRect = new Rectangle(portraitX, portraitY, portraitBoxSize, portraitBoxSize);

                // 头像木纹/内凹边框
                IClickableMenu.drawTextureBox(b, Game1.mouseCursors,
                    new Rectangle(403, 383, 6, 6),
                    portraitBoxRect.X - 4, portraitBoxRect.Y - 4,
                    portraitBoxRect.Width + 8, portraitBoxRect.Height + 8,
                    new Color(225, 210, 185), 3f, false);

                // 绘制“微笑”肖像
                if (card.Portrait != null && !card.SmileSourceRect.IsEmpty)
                {
                    b.Draw(card.Portrait, portraitBoxRect, card.SmileSourceRect, Color.White);
                }
                else
                {
                    string placeholder = card.DisplayName.Length > 0 ? card.DisplayName.Substring(0, 1) : "?";
                    var psz = Game1.dialogueFont.MeasureString(placeholder);
                    b.DrawString(Game1.dialogueFont, placeholder,
                        new Vector2(portraitBoxRect.X + (portraitBoxRect.Width - psz.X) / 2f,
                                    portraitBoxRect.Y + (portraitBoxRect.Height - psz.Y) / 2f),
                        Color.Gray);
                }

                // ── 2. NPC 名称（垂直居中在头像底边与卡片底部之间） ──
                string dispName = UiHelper.TruncateString(card.DisplayName, Game1.dialogueFont, rect.Width - 14, 0.82f);
                var nameSz = Game1.dialogueFont.MeasureString(dispName) * 0.82f;
                // 垂直居中在头像底边与卡片底部之间
                float nameAreaH = rect.Bottom - portraitBoxRect.Bottom;
                float nameY = portraitBoxRect.Bottom + (nameAreaH - nameSz.Y) / 2f - 2;

                b.DrawString(Game1.dialogueFont, dispName,
                    new Vector2(rect.X + (rect.Width - nameSz.X) / 2f, nameY),
                    isHovered ? new Color(130, 45, 10) : Game1.textColor,
                    0f, Vector2.Zero, 0.82f, SpriteEffects.None, 1f);

                // ── 3. 角标样式状态标签（右上角浮动胶囊） ──
                // 仅在自定义时显示高亮星标角标；默认基准时不再显示，避免破坏构图
                if (card.HasCustomOverlay)
                {
                    string badgeText = "★ 自定义";
                    Color badgeBg = new Color(34, 139, 34); // 森绿
                    var badgeSz = Game1.smallFont.MeasureString(badgeText) * 0.65f;
                    int bw = (int)badgeSz.X + 8;
                    int bh = (int)badgeSz.Y + 2;
                    int bx = rect.Right - bw - 6;
                    int by = rect.Top + 6;

                    b.Draw(Game1.staminaRect, new Rectangle(bx, by, bw, bh), badgeBg * 0.95f);
                    b.DrawString(Game1.smallFont, badgeText, new Vector2(bx + 4, by + 1), Color.White, 0f, Vector2.Zero, 0.65f, SpriteEffects.None, 1f);
                }

                // ── 4. 自定义还原按钮 ↺（左上角浮动） ──
                if (card.HasCustomOverlay)
                {
                    bool resetHover = slot.ResetBtnBounds.Contains(mx, my);
                    if (resetHover)
                    {
                        _hoveredGlobalTooltip = $"还原 {card.DisplayName} 为默认人设";
                    }

                    Color resetBg = resetHover ? new Color(255, 90, 90) : new Color(200, 180, 150);
                    IClickableMenu.drawTextureBox(b, Game1.mouseCursors,
                        new Rectangle(432, 439, 9, 9),
                        slot.ResetBtnBounds.X, slot.ResetBtnBounds.Y,
                        slot.ResetBtnBounds.Width, slot.ResetBtnBounds.Height,
                        resetBg, 2f, false);

                    b.DrawString(Game1.smallFont, "↺",
                        new Vector2(slot.ResetBtnBounds.X + 4, slot.ResetBtnBounds.Y + 1),
                        resetHover ? Color.White : Color.Black, 0f, Vector2.Zero, 0.8f, SpriteEffects.None, 1f);
                }
            }

            // ── 底部翻页控制器 ──
            int totalPages = GetNpcTotalPages();
            int curPage = _npcGridPage + 1;

            bool prevEnabled = _npcGridPage > 0;
            bool nextEnabled = _npcGridPage < totalPages - 1;

            // 使用 Timeline 同款原生像素箭头渲染
            DrawArrowButton(b, _prevPageBtnRect, isLeft: true, enabled: prevEnabled, mx, my);

            string pageInfo = $"第 {curPage} / {totalPages} 页 (共 {_displayNpcCards.Count} 人)";
            var pageInfoSz = Game1.smallFont.MeasureString(pageInfo);
            b.DrawString(Game1.smallFont, pageInfo,
                new Vector2(xPositionOnScreen + (width - pageInfoSz.X) / 2f, _prevPageBtnRect.Y + (_prevPageBtnRect.Height - pageInfoSz.Y) / 2f),
                Game1.textColor);

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

                string archiveText = I18n.Memory.ArchiveButton(_archivedCount, MemoryManager.MaxArchivedMemoriesPerNpc);
                bool archiveHover = _archiveButtonRect.Contains(mx, my);
                IClickableMenu.drawTextureBox(b,
                    _archiveButtonRect.X, _archiveButtonRect.Y,
                    _archiveButtonRect.Width, _archiveButtonRect.Height,
                    archiveHover ? Color.Gold : Color.White);

                var archiveLabelSize = Game1.smallFont.MeasureString(archiveText);
                b.DrawString(Game1.smallFont, archiveText,
                    new Vector2(
                        _archiveButtonRect.X + (_archiveButtonRect.Width - archiveLabelSize.X) / 2f,
                        _archiveButtonRect.Y + (_archiveButtonRect.Height - archiveLabelSize.Y) / 2f),
                    Game1.textColor);
            }
            else
            {
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