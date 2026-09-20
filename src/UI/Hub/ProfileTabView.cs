using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewValley;
using StardewValley.Menus;
using System;
using System.Collections.Generic;
using System.Linq;
using ValleytalkReborn;
using ValleytalkReborn.Services;

namespace ValleytalkReborn.UI;

/// <summary>
/// Tab2 农夫档案：玩家档案 + NPC 宫格子页。
/// 含性取向/安全滑块/bio 输入/保存，以及 NPC 宫格浏览/筛选/还原/跳转 BioEditor。
/// </summary>
internal sealed class ProfileTabView : HubTabViewBase
{
    private const int SubTabBarH = 34;
    private const int SubTabContentOffset = 44;
    private const int TopPadding = 110;
    private const int BottomPadding = 75;
    private const int LeftPadding = 40;
    private const int RightPadding = 40;

    private static readonly Rectangle LeftArrowSource = new(352, 495, 12, 11);
    private static readonly Rectangle RightArrowSource = new(365, 495, 12, 11);
    private static readonly Dictionary<string, Texture2D> PortraitCache = new(StringComparer.OrdinalIgnoreCase);

    private bool _tab2Initialized;
    private int _safetyModeIndex;
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

    private int _profileSubTab = 0;
    private Rectangle _subTabPlayerRect;
    private Rectangle _subTabNpcRect;

    private int _npcGridPage;
    private bool _filterCustomOnly;
    private Rectangle _filterCheckboxRect;
    private Rectangle _prevPageBtnRect;
    private Rectangle _nextPageBtnRect;

    private sealed class NpcCardInfo
    {
        public string Name;
        public string DisplayName;
        public Texture2D Portrait;
        public Rectangle DefaultSourceRect;
        public Rectangle SmileSourceRect;
        public bool HasCustomOverlay;
    }

    private List<NpcCardInfo> _allNpcCards = new();
    private List<NpcCardInfo> _displayNpcCards = new();
    private readonly List<(NpcCardInfo Card, Rectangle Bounds, Rectangle ResetBtnBounds)> _visibleCardSlots = new();

    public ProfileTabView(IntegratedHubMenu hub) : base(hub) { }

    public override void OnActivated() { }

    public override void OnDeactivated()
    {
        if (_bioTextBox != null && Game1.keyboardDispatcher.Subscriber == _bioTextBox)
            Game1.keyboardDispatcher.Subscriber = null;
        _orientationDropdown?.Close();
    }

    public override void OnReturnedFromChild()
    {
        RecalculateTab2Layout();
        if (_profileSubTab == 1)
            RefreshNpcCards();
    }

    public override void Layout(Rectangle menuBounds, Rectangle contentBounds)
    {
        base.Layout(menuBounds, contentBounds);
        if (_tab2Initialized)
            RecalculateTab2Layout();
    }

    public override void Update(GameTime time)
    {
        base.Update(time);
    }

    public override bool ReceiveLeftClick(int x, int y)
    {
        if (_orientationDropdown != null && _orientationDropdown.ReceiveLeftClick(x, y))
            return true;

        if (_subTabPlayerRect.Contains(x, y))
        {
            if (_profileSubTab != 0)
            {
                _profileSubTab = 0;
                Game1.playSound("smallSelect");
            }
            return true;
        }
        if (_subTabNpcRect.Contains(x, y))
        {
            if (_profileSubTab != 1)
            {
                _profileSubTab = 1;
                _orientationDropdown?.Close();
                if (_bioTextBox != null && Game1.keyboardDispatcher.Subscriber == _bioTextBox)
                    Game1.keyboardDispatcher.Subscriber = null;
                Game1.playSound("smallSelect");
                RefreshNpcCards();
            }
            return true;
        }

        if (_profileSubTab == 1)
            return HandleTab2NpcPageClick(x, y);

        if (_enableProfileCheckboxRect.Contains(x, y))
        {
            ModEntry.Config.EnablePlayerProfile = !ModEntry.Config.EnablePlayerProfile;
            Game1.playSound("select");
            return true;
        }

        if (!ModEntry.Config.EnablePlayerProfile)
            return false;

        if (_orientationDropdown != null && _orientationDropdown.HeaderBounds.Contains(x, y))
        {
            _orientationDropdown.ToggleOpen();
            Game1.playSound("shwip");
            return true;
        }

        if (_safetySliderRect.Contains(x, y))
        {
            int trackX = _safetySliderRect.X;
            int trackW = _safetySliderRect.Width;
            int relativeX = Math.Clamp(x - trackX, 0, trackW);
            _safetyModeIndex = Math.Min(3, (int)(((float)relativeX / trackW) * 4));
            Game1.playSound("select");
            return true;
        }

        if (_bioBoxRect.Contains(x, y))
        {
            _bioTextBox.Selected = true;
            Game1.keyboardDispatcher.Subscriber = _bioTextBox;
            _bioTextBox.ReceiveLeftClick(x, y);
            return true;
        }
        else
        {
            if (_bioTextBox != null && Game1.keyboardDispatcher.Subscriber == _bioTextBox)
                Game1.keyboardDispatcher.Subscriber = null;
        }

        if (_saveButtonRect.Contains(x, y))
        {
            SaveTab2();
            return true;
        }

        return false;
    }

    public override bool ReceiveScrollWheel(int direction)
    {
        if (_profileSubTab == 1)
        {
            int totalPages = GetNpcTotalPages();
            if (direction < 0 && _npcGridPage < totalPages - 1)
            {
                _npcGridPage++;
                Game1.playSound("shwip");
                RecalculateNpcGridLayout();
                return true;
            }
            else if (direction > 0 && _npcGridPage > 0)
            {
                _npcGridPage--;
                Game1.playSound("shwip");
                RecalculateNpcGridLayout();
                return true;
            }
            return false;
        }

        if (_orientationDropdown != null && _orientationDropdown.IsOpen)
        {
            _orientationDropdown.ReceiveScrollWheel(direction);
            return true;
        }
        _bioTextBox?.ReceiveScrollWheel(direction);
        return false;
    }

    public override bool ReceiveKeyPress(Keys key)
    {
        if (_bioTextBox != null && Game1.keyboardDispatcher.Subscriber == _bioTextBox)
        {
            if (key == Keys.Escape || key == Keys.Enter)
            {
                _bioTextBox.Selected = false;
                Game1.keyboardDispatcher.Subscriber = null;
                return true;
            }
            if (!DialogueTextInputBox.IsControlKeyDown())
                _bioTextBox.RecieveSpecialInput(key);
            return true;
        }

        if (key == Keys.Escape && _orientationDropdown != null && _orientationDropdown.IsOpen)
        {
            _orientationDropdown.Close();
            return true;
        }

        return false;
    }

    public override void LeftClickHeld(int x, int y)
    {
        if (_orientationDropdown != null && !_orientationDropdown.IsOpen && _safetySliderRect.Contains(x, y))
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
        }
    }

    public override void Draw(SpriteBatch b, int mx, int my)
    {
        DrawTab2(b, mx, my);
    }

    public override void DrawOverlay(SpriteBatch b)
    {
        if (_profileSubTab == 0)
            _orientationDropdown?.Draw(b);
    }

    public override void RefreshFromHub()
    {
        if (!_tab2Initialized)
            InitializeTab2();
        if (_profileSubTab == 1)
            RefreshNpcCards();
        if (_bioTextBox != null && _bioTextBox.Selected && Game1.keyboardDispatcher.Subscriber == null)
            Game1.keyboardDispatcher.Subscriber = _bioTextBox;
    }

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
            CustomFontSize = HubUi.RegularFontSize,
            CounterFontSize = HubUi.SmallFontSize + 1,
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
        int leftColX = MenuBounds.X + LeftPadding;
        int contentW = MenuBounds.Width - LeftPadding - RightPadding;

        int subTabY = MenuBounds.Y + TopPadding;
        int subTabW = 160;
        _subTabPlayerRect = new Rectangle(leftColX, subTabY, subTabW, SubTabBarH);
        _subTabNpcRect = new Rectangle(leftColX + subTabW + 12, subTabY, subTabW, SubTabBarH);

        _filterCheckboxRect = new Rectangle(leftColX + contentW - 170, subTabY + 2, 28, 28);

        int bioBoxW = Math.Min(contentW - 32, 680);
        int bioBoxX = MenuBounds.X + (MenuBounds.Width - bioBoxW) / 2;

        int currentY = MenuBounds.Y + TopPadding + SubTabContentOffset + 4;
        _enableProfileCheckboxRect = new Rectangle(bioBoxX, currentY, 27, 27);

        currentY += 40;
        const int dropdownH = 38;
        _orientationLabelRect = new Rectangle(bioBoxX, currentY, bioBoxW, 22);
        currentY += 24;
        _orientationDropdownRect = new Rectangle(bioBoxX, currentY, bioBoxW, dropdownH);
        _orientationDropdown?.SetHeaderBounds(_orientationDropdownRect);

        currentY += dropdownH + 12;
        _safetyLabelRect = new Rectangle(bioBoxX, currentY, bioBoxW, 22);
        currentY += 24;

        int trackW = Math.Min(260, contentW - 80);
        int trackX = MenuBounds.X + (MenuBounds.Width - trackW) / 2;
        _safetySliderRect = new Rectangle(trackX, currentY, trackW, 22);

        _saveButtonRect = new Rectangle(MenuBounds.X + MenuBounds.Width / 2 - 80, MenuBounds.Y + MenuBounds.Height - 56, 160, 42);

        int bioY = currentY + 22 + 4 + 22 + 2 + 24 + 24;
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
            ModEntry.SMonitor.Log($"[Hub] config save failed: {ex.Message}", StardewModdingAPI.LogLevel.Error);
            return;
        }

        string bioText = _bioTextBox.Text ?? "";
        if (bioText.Length > 300)
            bioText = bioText.Substring(0, 300);

        PlayerProfileManager.SaveCustomBio(bioText);
        Game1.addHUDMessage(new HUDMessage(I18n.Profile.SavedHud(), 2));
    }

    private void DrawTab2(SpriteBatch b, int mx, int my)
    {
        DrawTab2SubTab(b, _subTabPlayerRect, "玩家档案", _profileSubTab == 0, mx, my);
        DrawTab2SubTab(b, _subTabNpcRect, "NPC 档案", _profileSubTab == 1, mx, my);

        if (_profileSubTab == 1)
        {
            DrawTab2NpcGridPage(b);
            return;
        }

        bool enabled = ModEntry.Config.EnablePlayerProfile;
        Rectangle enableSrc = enabled ? new Rectangle(236, 425, 9, 9) : new Rectangle(227, 425, 9, 9);
        b.Draw(Game1.mouseCursors, new Vector2(_enableProfileCheckboxRect.X, _enableProfileCheckboxRect.Y),
            enableSrc, Color.White, 0f, Vector2.Zero, 3f, SpriteEffects.None, 1f);

        CustomFontManager.DrawStringBold(b, I18n.Profile.EnableProfile(),
            new Vector2(_enableProfileCheckboxRect.X + 36, _enableProfileCheckboxRect.Y + (_enableProfileCheckboxRect.Height - CustomFontManager.MeasureStringBold("A", HubUi.RegularFontSize).Y) / 2f),
            Game1.textColor, HubUi.RegularFontSize);

        if (!enabled)
            return;

        CustomFontManager.DrawStringBold(b, I18n.Profile.OrientationLabel(),
            new Vector2(_orientationLabelRect.X, _orientationLabelRect.Y), Game1.textColor, HubUi.RegularFontSize);

        CustomFontManager.DrawStringBold(b, I18n.Profile.RomanceSafetyLabel(),
            new Vector2(_safetyLabelRect.X, _safetyLabelRect.Y), Game1.textColor, HubUi.RegularFontSize);

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
        var labelSize = CustomFontManager.MeasureStringBold(currentLabel, HubUi.RegularFontSize);
        float labelX = MenuBounds.X + (MenuBounds.Width - labelSize.X) / 2f;
        int labelY = trackY + 22 + 4;

        CustomFontManager.DrawStringBold(b, currentLabel, new Vector2(labelX, labelY), Game1.textColor, HubUi.RegularFontSize);

        int maxDescW = MenuBounds.Width - LeftPadding - RightPadding - 20;
        string desc = Game1.parseText(I18n.Profile.SafetyDesc(), Game1.smallFont, maxDescW);
        string[] descLines = desc.Split('\n');
        int lineSpacing = (int)CustomFontManager.MeasureString("A", HubUi.RegularFontSize).Y + 2;
        int descStartY = labelY + 22 + 2;

        for (int i = 0; i < descLines.Length; i++)
        {
            string line = descLines[i];
            float lineW = CustomFontManager.MeasureString(line, HubUi.RegularFontSize).X;
            float lineX = MenuBounds.X + (MenuBounds.Width - lineW) / 2f;
            float lineY = descStartY + i * lineSpacing;
            CustomFontManager.DrawString(b, line, new Vector2(lineX, lineY), Color.DimGray, HubUi.RegularFontSize);
        }

        CustomFontManager.DrawStringBold(b, I18n.Profile.BioLabel(),
            new Vector2(_bioLabelRect.X + 2, _bioLabelRect.Y), Game1.textColor, HubUi.RegularFontSize);

        _bioTextBox.Position = new Vector2(_bioBoxRect.X, _bioBoxRect.Y);
        _bioTextBox.Update(Game1.currentGameTime);
        _bioTextBox.Draw(b);

        if (string.IsNullOrWhiteSpace(_bioTextBox.Text))
        {
            var sz = CustomFontManager.MeasureString(I18n.Profile.BioPlaceholder(), HubUi.RegularFontSize);
            CustomFontManager.DrawString(b, I18n.Profile.BioPlaceholder(),
                new Vector2(_bioBoxRect.X + 16, _bioBoxRect.Y + (_bioBoxRect.Height - sz.Y) / 2f),
                Color.Gray, HubUi.RegularFontSize);
        }

        bool saveHover = _saveButtonRect.Contains(mx, my);
        Color saveBg = saveHover ? new Color(255, 235, 205) : new Color(139, 90, 43);
        b.Draw(Game1.staminaRect, new Rectangle(_saveButtonRect.X + 2, _saveButtonRect.Y + 2, _saveButtonRect.Width - 4, _saveButtonRect.Height - 4), saveBg);
        IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(432, 439, 9, 9),
            _saveButtonRect.X, _saveButtonRect.Y, _saveButtonRect.Width, _saveButtonRect.Height, saveBg, 4f, false);

        string saveText = I18n.Profile.SaveButton();
        var saveSize = CustomFontManager.MeasureStringBold(saveText, HubUi.TabFontSize);
        CustomFontManager.DrawStringBold(b, saveText,
            new Vector2(_saveButtonRect.X + (_saveButtonRect.Width - saveSize.X) / 2f, _saveButtonRect.Y + (_saveButtonRect.Height - saveSize.Y) / 2f),
            saveHover ? Game1.textColor : Color.White, HubUi.TabFontSize);
    }

    private void DrawTab2SubTab(SpriteBatch b, Rectangle rect, string label, bool isActive, int mx, int my)
    {
        Color bg = isActive ? new Color(210, 180, 140)
                 : rect.Contains(mx, my) ? new Color(255, 235, 205)
                 : new Color(139, 90, 43);

        b.Draw(Game1.staminaRect, new Rectangle(rect.X + 2, rect.Y + 2, rect.Width - 4, rect.Height - 4), bg);
        IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(432, 439, 9, 9),
            rect.X, rect.Y, rect.Width, rect.Height, bg, 4f, false);

        var labelSize = CustomFontManager.MeasureStringBold(label, HubUi.TabFontSize);
        CustomFontManager.DrawStringBold(b, label,
            new Vector2(rect.X + (rect.Width - labelSize.X) / 2f, rect.Y + (rect.Height - labelSize.Y) / 2f),
            isActive ? Game1.textColor : Color.White * 0.95f, HubUi.TabFontSize);
    }

    private string[] GetSafetyModeLabels() => new[]
    {
        I18n.Profile.SafetyOff(),
        I18n.Profile.SafetyLoose(),
        I18n.Profile.SafetyModerate(),
        I18n.Profile.SafetyStrict()
    };

    private bool HandleTab2NpcPageClick(int x, int y)
    {
        bool hasFilter = _displayNpcCards.Count > 0;
        if (hasFilter && _filterCheckboxRect.Contains(x, y))
        {
            _filterCustomOnly = !_filterCustomOnly;
            _npcGridPage = 0;
            Game1.playSound("drumkit6");
            ApplyNpcFilter();
            return true;
        }

        int totalPages = GetNpcTotalPages();
        if (_prevPageBtnRect.Contains(x, y) && _npcGridPage > 0)
        {
            _npcGridPage--;
            Game1.playSound("shwip");
            RecalculateNpcGridLayout();
            return true;
        }
        if (_nextPageBtnRect.Contains(x, y) && _npcGridPage < totalPages - 1)
        {
            _npcGridPage++;
            Game1.playSound("shwip");
            RecalculateNpcGridLayout();
            return true;
        }

        foreach (var slot in _visibleCardSlots)
        {
            if (slot.Card.HasCustomOverlay && slot.ResetBtnBounds.Contains(x, y))
            {
                TryResetNpcBio(slot.Card.Name);
                return true;
            }

            if (slot.Bounds.Contains(x, y))
            {
                Game1.playSound("bigSelect");
                Hub.NotifyWillBeObscured();
                Game1.activeClickableMenu = new BioEditorMenu(slot.Card.Name, Hub);
                return true;
            }
        }

        return false;
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
                Game1.activeClickableMenu = Hub;
                if (!ModEntry.BioStorage.ResetOverlay(target, out string err))
                {
                    Game1.addHUDMessage(new HUDMessage(err, 3));
                }
                else
                {
                    Game1.playSound("throw");
                    RefreshNpcCards();
                }
            },
            _ =>
            {
                Game1.activeClickableMenu = Hub;
            });
    }

    private void RefreshNpcCards()
    {
        var cleanedCandidates = NpcCandidateQueryService.GetCleanedCandidates();
        var cards = new List<NpcCardInfo>();

        foreach (var candidate in cleanedCandidates)
        {
            var portrait = SafeLoadPortrait(candidate.Id);
            if (portrait == null || portrait.IsDisposed)
                continue;

            var smileRect = GetSmilingPortraitSource(portrait);
            if (smileRect.IsEmpty || smileRect.Width <= 0 || smileRect.Height <= 0)
                continue;

            var defaultRect = GetDefaultPortraitSource(portrait);
            bool hasCustom = ModEntry.BioStorage != null && ModEntry.BioStorage.HasCustomOverlay(candidate.Id);

            cards.Add(new NpcCardInfo
            {
                Name = candidate.Id,
                DisplayName = candidate.DisplayName,
                Portrait = portrait,
                DefaultSourceRect = defaultRect,
                SmileSourceRect = smileRect,
                HasCustomOverlay = hasCustom
            });
        }

        _allNpcCards = cards
            .OrderByDescending(x => x.HasCustomOverlay)
            .ThenBy(x => x.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        ApplyNpcFilter();
    }

    private void ApplyNpcFilter()
    {
        _displayNpcCards = _filterCustomOnly
            ? _allNpcCards.Where(x => x.HasCustomOverlay).ToList()
            : new List<NpcCardInfo>(_allNpcCards);

        int totalPages = GetNpcTotalPages();
        if (_npcGridPage >= totalPages)
            _npcGridPage = Math.Max(0, totalPages - 1);

        RecalculateNpcGridLayout();
    }

    private const int NpcCols = 5;
    private const int NpcRows = 2;
    private const int NpcItemsPerPage = NpcCols * NpcRows;

    private int GetNpcTotalPages() => (_displayNpcCards.Count + NpcItemsPerPage - 1) / NpcItemsPerPage;

    private void RecalculateNpcGridLayout()
    {
        _visibleCardSlots.Clear();

        int leftColX = MenuBounds.X + LeftPadding;
        int contentW = MenuBounds.Width - LeftPadding - RightPadding;
        int startY = MenuBounds.Y + TopPadding + SubTabContentOffset;

        const int bottomPagingBarH = 40;
        const int bottomMargin = 8;
        int bottomLimitY = MenuBounds.Y + MenuBounds.Height - BottomPadding;
        int availH = bottomLimitY - startY - bottomPagingBarH - bottomMargin;

        int cols = NpcCols;
        int rows = NpcRows;
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
        int midX = MenuBounds.X + MenuBounds.Width / 2;
        _prevPageBtnRect = new Rectangle(midX - 130, pagingY, 44, 36);
        _nextPageBtnRect = new Rectangle(midX + 86, pagingY, 44, 36);
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
            filterHover ? Game1.textColor : Color.DimGray, HubUi.RegularFontSize);

        if (filterHover)
        {
            SetHoveredTooltip("只显示已经配置过自定义人设覆盖的 NPC");
        }

        if (_visibleCardSlots.Count == 0)
        {
            string emptyText = _filterCustomOnly ? "暂无任何自定义覆盖的 NPC" : "未发现任何 NPC 档案";
            var sz = CustomFontManager.MeasureString(emptyText, HubUi.RegularFontSize);
            CustomFontManager.DrawString(b, emptyText,
                new Vector2(MenuBounds.X + (MenuBounds.Width - sz.X) / 2f, MenuBounds.Y + 260), Color.Gray, HubUi.RegularFontSize);
        }

        bool isLeftMouseDown = Mouse.GetState().LeftButton == ButtonState.Pressed;

        foreach (var slot in _visibleCardSlots)
        {
            var card = slot.Card;
            var rect = slot.Bounds;
            bool isHovered = rect.Contains(mx, my);

            b.Draw(Game1.staminaRect, new Rectangle(rect.X + 2, rect.Y + 2, rect.Width, rect.Height), Color.Black * 0.12f);
            b.Draw(Game1.staminaRect, new Rectangle(rect.X + 2, rect.Y + 2, rect.Width - 4, rect.Height - 4), isHovered ? new Color(255, 248, 235) : new Color(254, 247, 238));

            Color cardBorder = isHovered ? new Color(220, 185, 140) : new Color(210, 190, 165);
            IClickableMenu.drawTextureBox(b, Game1.mouseCursors,
                new Rectangle(432, 439, 9, 9),
                rect.X, rect.Y, rect.Width, rect.Height, cardBorder, 3.0f, false);

            if (isHovered)
            {
                b.Draw(Game1.staminaRect, rect, new Color(255, 215, 0) * 0.08f);
                SetHoveredTooltip($"{card.DisplayName} - 点击打开人设编辑器");
            }

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

            Rectangle targetSourceRect = isHovered ? card.SmileSourceRect : card.DefaultSourceRect;

            if (card.Portrait != null && !targetSourceRect.IsEmpty)
            {
                b.Draw(card.Portrait, innerPortraitRect, targetSourceRect, Color.White);
            }
            else
            {
                string placeholder = card.DisplayName.Length > 0 ? card.DisplayName.Substring(0, 1) : "?";
                var psz = CustomFontManager.MeasureStringBold(placeholder, HubUi.TitleFontSize);
                CustomFontManager.DrawStringBold(b, placeholder,
                    new Vector2(innerPortraitRect.X + (innerPortraitRect.Width - psz.X) / 2f,
                                innerPortraitRect.Y + (innerPortraitRect.Height - psz.Y) / 2f),
                    Color.Gray, HubUi.TitleFontSize);
            }

            string dispName = CustomFontManager.TruncateString(card.DisplayName, HubUi.CardTitleFontSize, rect.Width - 12);
            var nameSz = CustomFontManager.MeasureStringBold(dispName, HubUi.CardTitleFontSize);

            float nameAreaH = rect.Bottom - portraitBoxRect.Bottom;
            float nameY = portraitBoxRect.Bottom + (nameAreaH - nameSz.Y) / 2f - 2;

            CustomFontManager.DrawStringBold(b, dispName,
                new Vector2(rect.X + (rect.Width - nameSz.X) / 2f, nameY),
                isHovered ? new Color(130, 45, 10) : Game1.textColor,
                HubUi.CardTitleFontSize);

            if (card.HasCustomOverlay)
            {
                string badgeText = "★ 自定义";
                Color badgeBg = new Color(34, 139, 34);
                var badgeSz = CustomFontManager.MeasureStringBold(badgeText, HubUi.SmallFontSize);
                int bw = (int)badgeSz.X + 8;
                int bh = (int)badgeSz.Y + 4;
                int bx = rect.Right - bw - 4;
                int by = rect.Top + 4;

                b.Draw(Game1.staminaRect, new Rectangle(bx, by, bw, bh), badgeBg * 0.95f);
                CustomFontManager.DrawStringBold(b, badgeText, new Vector2(bx + 4, by + 2), Color.White, HubUi.SmallFontSize);
            }

            if (card.HasCustomOverlay)
            {
                bool resetHover = slot.ResetBtnBounds.Contains(mx, my);
                if (resetHover)
                {
                    SetHoveredTooltip($"还原 {card.DisplayName} 为默认人设");
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

        int totalPages = GetNpcTotalPages();
        int curPage = _npcGridPage + 1;

        bool prevEnabled = _npcGridPage > 0;
        bool nextEnabled = _npcGridPage < totalPages - 1;

        DrawArrowButton(b, _prevPageBtnRect, isLeft: true, enabled: prevEnabled, mx, my);

        string pageInfo = $"第 {curPage} / {totalPages} 页 (共 {_displayNpcCards.Count} 人)";
        var pageInfoSz = CustomFontManager.MeasureStringBold(pageInfo, HubUi.RegularFontSize);
        CustomFontManager.DrawStringBold(b, pageInfo,
            new Vector2(MenuBounds.X + (MenuBounds.Width - pageInfoSz.X) / 2f, _prevPageBtnRect.Y + (_prevPageBtnRect.Height - pageInfoSz.Y) / 2f),
            Game1.textColor, HubUi.RegularFontSize);

        DrawArrowButton(b, _nextPageBtnRect, isLeft: false, enabled: nextEnabled, mx, my);
    }

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

    private static Texture2D SafeLoadPortrait(string npcName)
    {
        if (string.IsNullOrWhiteSpace(npcName)) return null;

        if (PortraitCache.TryGetValue(npcName, out var tex) && tex != null && !tex.IsDisposed)
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

        PortraitCache[npcName] = loaded;
        return loaded;
    }
}