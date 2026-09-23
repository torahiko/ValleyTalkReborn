#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewModdingAPI;
using StardewValley;
using StardewValley.GameData.Locations;
using StardewValley.Menus;
using StardewValley.TokenizableStrings;
using ValleytalkReborn.Services.Overlays;

namespace ValleytalkReborn.UI;

/// <summary>
/// 地点与节日编撰子页：
/// 1. 顶部模式切换（地点环境 / 节日日程），各享全屏完整编辑视野；
/// 2. 玩家自创节日绝对置顶显示，列表文字颜色统一炭黑呈现；
/// 3. 新建节日全历法自动探测空闲日期，单日唯一性严格校验；
/// 4. 原版节日禁止删除（置灰呈现），自创节日支持彻底删除；
/// 5. 节日名称输入框字体恢复为黑色，搜索框保持深灰色；
/// 6. 描述文本框字数限制严格收敛至 200 字以内；
/// 7. 动态扫描过滤无意义小地图（地窖、传送间等）与破损翻译；
/// 8. 地点铭牌根据当前语言动态本地化呈现。
/// </summary>
internal sealed class LocationFestivalPage : WorldSubPageBase
{
    private enum ViewMode { Locations, Festivals }
    private ViewMode _currentMode = ViewMode.Locations;

    private const int RowHeight = 40;
    private const int RowGap = 4;
    private const int LabelWidth = 86;
    private const int CtrlHeight = 32;
    private const int CtrlGap = 10;
    private const int ModeHeaderHeight = 34;

    // 自定义深灰色配置（介于纯黑与淡灰之间，专用于搜索框）
    private static readonly Color DarkGrayText = new(80, 80, 80);

    // ── 模式切换标签 ──
    private Rectangle _modeLocationsBtnRect;
    private Rectangle _modeFestivalsBtnRect;

    // ── 布局区域 ──
    private Rectangle _listRect;
    private Rectangle _formRect;

    // ── 搜索框 ──
    private readonly TextBox _searchBox;
    private Rectangle _searchBoxRect;
    private string _lastSearchQuery = "";
    private bool _isDraggingLeftScrollbar = false;

    // ── 基线缓存 ──
    private readonly Dictionary<string, string> _baselineLocations = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _baselineRegions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _baselineLocDescriptions = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, string> _baselineFestivals = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _baselineFestDescriptions = new(StringComparer.OrdinalIgnoreCase);

    // ── 1. 地点条目数据 ──
    private sealed class LocationEntry
    {
        public string Id = "";
        public string Name = "";
        public string Region = "";
        public string CustomDescription = "";
        public bool HasCustomOverlay;
    }
    private readonly List<LocationEntry> _allLocations = new();
    private readonly List<LocationEntry> _filteredLocations = new();
    private int _locScroll;
    private string? _selectedLocId;

    // ── 2. 节日条目数据 ──
    private sealed class FestivalEntry
    {
        public string Key = "";
        public string Name = "";
        public string Season = "spring";
        public int Day = 1;
        public bool IsBaseline;
        public bool IsCustom;
        public Dictionary<string, string> Names = new();
        public Dictionary<string, string> Descriptions = new();
    }
    private readonly List<FestivalEntry> _allFestivals = new();
    private readonly List<FestivalEntry> _filteredFestivals = new();
    private int _festScroll;
    private string? _selectedFestKey;
    private bool _isCreatingNewFest = false;

    // ── 节日下拉框状态机 ──
    private static readonly (string Id, string Label)[] SeasonOptions = new[]
    {
        ("spring", "春季 (Spring)"),
        ("summer", "夏季 (Summer)"),
        ("fall", "秋季 (Fall)"),
        ("winter", "冬季 (Winter)")
    };
    private bool _isSeasonDropdownOpen = false;
    private int _seasonDropdownScrollOffset = 0;
    private Rectangle _seasonHeaderRect;
    private string _selectedSeason = "spring";
    private const int DropdownItemHeight = 34;
    private const int DropdownMaxVisible = 4;

    // ── 表单输入控件 ──
    private readonly DialogueTextInputBox _descBox;
    private readonly TextBox _festNameBox;
    private readonly NumberStepper _dayStepper;
    private Rectangle _dayStepperRect;

    // ── 按钮区域 ──
    private Rectangle _btnSave;
    private Rectangle _btnRevert;
    private Rectangle _btnDelete;
    private Rectangle _btnNew;

    private string? _statusMessage;

    private static bool IsZh => LocalizedContentManager.CurrentLanguageCode == LocalizedContentManager.LanguageCode.zh;
    private static string LangKey => IsZh ? "zh" : "en";

    public LocationFestivalPage(IntegratedHubMenu hub) : base(hub)
    {
        Texture2D boxTex = Game1.content.Load<Texture2D>("LooseSprites\\textBox") ?? Game1.mouseCursors;
        _searchBox = new TextBox(boxTex, null, Game1.smallFont, DarkGrayText);
        _searchBox.limitWidth = false;

        _festNameBox = new TextBox(boxTex, null, Game1.smallFont, RulesTheme.TextCharcoal);
        _festNameBox.limitWidth = false;

        _dayStepper = new NumberStepper(Rectangle.Empty, 1, 1, 28, 1, " 日");

        _descBox = new DialogueTextInputBox(600, 500)
        {
            UseCustomFont = true,
            CustomFontSize = CustomFontManager.SizeRegular,
            TextColor = RulesTheme.TextPrimary,
            ShowCharacterCount = true,
            DrawFrame = true
        };
    }

    public override void OnShown()
    {
        BuildBaselineCache();
        RebuildLocationView();
        RebuildFestivalView();

        if (_currentMode == ViewMode.Locations && _filteredLocations.Count > 0 && _selectedLocId == null)
            SelectLocation(_filteredLocations[0].Id);
        else if (_currentMode == ViewMode.Festivals && _filteredFestivals.Count > 0 && _selectedFestKey == null && !_isCreatingNewFest)
            SelectFestival(_filteredFestivals[0].Key);
    }

    public override void Layout(Rectangle area)
    {
        int tabW = 160;
        int tabH = 30;
        _modeLocationsBtnRect = new Rectangle(area.X, area.Y, tabW, tabH);
        _modeFestivalsBtnRect = new Rectangle(area.X + tabW + 8, area.Y, tabW, tabH);

        int contentY = area.Y + ModeHeaderHeight + 4;
        int contentH = area.Bottom - contentY;

        int listWidth = (int)(area.Width * 0.35f);
        _listRect = new Rectangle(area.X, contentY, listWidth, contentH);

        int formX = _listRect.Right + 12;
        int formW = area.Right - formX;
        _formRect = new Rectangle(formX, contentY, formW, contentH);

        _searchBoxRect = new Rectangle(_listRect.X + 6, _listRect.Y + 6, _listRect.Width - 12, 30);
        _searchBox.X = _searchBoxRect.X;
        _searchBox.Y = _searchBoxRect.Y;
        _searchBox.Width = _searchBoxRect.Width;
        _searchBox.Height = _searchBoxRect.Height;

        int btnY = _formRect.Bottom - 36;
        int btnW = (_formRect.Width - 18) / 4;
        _btnSave = new Rectangle(_formRect.X, btnY, btnW, 32);
        _btnRevert = new Rectangle(_formRect.X + btnW + 6, btnY, btnW, 32);
        _btnDelete = new Rectangle(_formRect.X + (btnW + 6) * 2, btnY, btnW, 32);
        _btnNew = new Rectangle(_formRect.X + (btnW + 6) * 3, btnY, btnW, 32);

        LayoutRightForm();
    }

    private void LayoutRightForm()
    {
        int curX = _formRect.X + LabelWidth + 8;
        int curY = _formRect.Y + 12;
        int ctrlW = _formRect.Width - LabelWidth - 20;

        if (_currentMode == ViewMode.Festivals)
        {
            _festNameBox.X = curX;
            _festNameBox.Y = curY;
            _festNameBox.Width = ctrlW;
            _festNameBox.Height = CtrlHeight;
            curY += CtrlHeight + CtrlGap;

            int seasonW = 160;
            _seasonHeaderRect = new Rectangle(curX, curY, seasonW, CtrlHeight);
            _dayStepperRect = new Rectangle(curX + seasonW + 10, curY, 130, CtrlHeight);
            _dayStepper.SetBounds(_dayStepperRect);
            curY += CtrlHeight + CtrlGap + 2;
        }
        else
        {
            curY += 46 + CtrlGap;
        }

        int descTop = curY + 22;
        int descBottom = _btnSave.Y - 8;
        int descH = Math.Max(120, descBottom - descTop);

        _descBox.Position = new Vector2(_formRect.X + 12, descTop);
        _descBox.Extent = new Vector2(_formRect.Width - 24, descH);
        _descBox.InvalidateLayout();
    }

    public override void Update(GameTime time)
    {
        _descBox.Update(time);

        if (_searchBox.Text != _lastSearchQuery)
        {
            _lastSearchQuery = _searchBox.Text;
            FilterCurrentList();
        }
    }

    public override bool ReceiveLeftClick(int x, int y)
    {
        if (_currentMode == ViewMode.Festivals && _isSeasonDropdownOpen)
        {
            var dropListRect = GetDropdownMenuRect(_seasonHeaderRect);

            if (dropListRect.Contains(x, y))
            {
                int clickIdx = (y - dropListRect.Y - 4) / DropdownItemHeight + _seasonDropdownScrollOffset;
                if (clickIdx >= 0 && clickIdx < SeasonOptions.Length)
                {
                    _selectedSeason = SeasonOptions[clickIdx].Id;
                    _isSeasonDropdownOpen = false;
                    Game1.playSound("smallSelect");
                }
                return true;
            }

            if (_seasonHeaderRect.Contains(x, y))
            {
                _isSeasonDropdownOpen = false;
                Game1.playSound("shwip");
                return true;
            }

            _isSeasonDropdownOpen = false;
            return true;
        }

        if (_modeLocationsBtnRect.Contains(x, y) && _currentMode != ViewMode.Locations)
        {
            _currentMode = ViewMode.Locations;
            _isCreatingNewFest = false;
            _isSeasonDropdownOpen = false;
            _searchBox.Text = "";
            FilterCurrentList();
            if (_filteredLocations.Count > 0) SelectLocation(_filteredLocations[0].Id);
            Game1.playSound("smallSelect");
            LayoutRightForm();
            return true;
        }
        if (_modeFestivalsBtnRect.Contains(x, y) && _currentMode != ViewMode.Festivals)
        {
            _currentMode = ViewMode.Festivals;
            _isCreatingNewFest = false;
            _isSeasonDropdownOpen = false;
            _searchBox.Text = "";
            FilterCurrentList();
            if (_filteredFestivals.Count > 0) SelectFestival(_filteredFestivals[0].Key);
            Game1.playSound("smallSelect");
            LayoutRightForm();
            return true;
        }

        if (_currentMode == ViewMode.Festivals && _seasonHeaderRect.Contains(x, y))
        {
            _isSeasonDropdownOpen = true;
            int idx = Array.FindIndex(SeasonOptions, s => string.Equals(s.Id, _selectedSeason, StringComparison.OrdinalIgnoreCase));
            _seasonDropdownScrollOffset = Math.Clamp(idx >= 0 ? idx : 0, 0, Math.Max(0, SeasonOptions.Length - DropdownMaxVisible));
            Game1.playSound("shwip");
            DeselectAllBoxes();
            return true;
        }

        if (_searchBoxRect.Contains(x, y))
        {
            _searchBox.SelectMe();
            Game1.keyboardDispatcher.Subscriber = _searchBox;
            _descBox.Selected = false;
            _festNameBox.Selected = false;
            return true;
        }

        int listTop = _searchBoxRect.Bottom + 5;
        int listH = _listRect.Bottom - 4 - listTop;
        int visibleCount = listH / (RowHeight + RowGap);
        int totalCount = _currentMode == ViewMode.Locations ? _filteredLocations.Count : _filteredFestivals.Count;
        bool hasScroll = totalCount > visibleCount;

        if (hasScroll)
        {
            var trackRect = new Rectangle(_listRect.Right - 9, listTop, 5, listH);
            if (trackRect.Contains(x, y))
            {
                _isDraggingLeftScrollbar = true;
                int maxScroll = totalCount - visibleCount;
                float ratio = Math.Clamp((float)visibleCount / totalCount, 0.15f, 1f);
                int thumbH = Math.Max(24, (int)(trackRect.Height * ratio));
                UpdateScrollFromMouse(y, trackRect, thumbH, maxScroll);
                return true;
            }
        }

        if (_listRect.Contains(x, y) && y >= listTop)
        {
            int clickIdx = (y - listTop) / (RowHeight + RowGap);
            int scroll = _currentMode == ViewMode.Locations ? _locScroll : _festScroll;
            int actualIdx = scroll + clickIdx;

            if (actualIdx >= 0 && actualIdx < totalCount)
            {
                if (_currentMode == ViewMode.Locations)
                    SelectLocation(_filteredLocations[actualIdx].Id);
                else
                    SelectFestival(_filteredFestivals[actualIdx].Key);

                Game1.playSound("smallSelect");
                return true;
            }
            return true;
        }

        if (_currentMode == ViewMode.Festivals && new Rectangle(_festNameBox.X, _festNameBox.Y, _festNameBox.Width, _festNameBox.Height).Contains(x, y))
        {
            _festNameBox.SelectMe();
            Game1.keyboardDispatcher.Subscriber = _festNameBox;
            _descBox.Selected = false;
            _searchBox.Selected = false;
            return true;
        }

        if (_descBox.ContainsPoint(x, y))
        {
            _descBox.Selected = true;
            Game1.keyboardDispatcher.Subscriber = _descBox;
            _festNameBox.Selected = false;
            _searchBox.Selected = false;
            return true;
        }

        if (_currentMode == ViewMode.Festivals && _dayStepper.ReceiveLeftClick(x, y))
        {
            return true;
        }

        if (_btnSave.Contains(x, y))
        {
            if (_currentMode == ViewMode.Locations) SaveLocation();
            else SaveFestival();
            return true;
        }

        if (_btnRevert.Contains(x, y))
        {
            if (_currentMode == ViewMode.Locations) RequestRevertLocation();
            else RequestRevertFestival();
            return true;
        }

        // 8. 底部删除按钮交互（原版节日置灰不可点击）
        if (_btnDelete.Contains(x, y) && _currentMode == ViewMode.Festivals)
        {
            if (_isCreatingNewFest)
            {
                _isCreatingNewFest = false;
                if (_filteredFestivals.Count > 0)
                    SelectFestival(_filteredFestivals[0].Key);
                else
                    _selectedFestKey = null;
                Game1.playSound("smallSelect");
                return true;
            }

            var fest = !string.IsNullOrEmpty(_selectedFestKey)
                ? _allFestivals.FirstOrDefault(f => string.Equals(f.Key, _selectedFestKey, StringComparison.OrdinalIgnoreCase))
                : null;

            if (fest?.IsCustom == true)
            {
                RequestDeleteFestival();
            }
            return true;
        }

        if (_btnNew.Contains(x, y) && _currentMode == ViewMode.Festivals)
        {
            CreateNewFestival();
            return true;
        }

        DeselectAllBoxes();
        return false;
    }

    public override void LeftClickHeld(int x, int y)
    {
        if (_isDraggingLeftScrollbar)
        {
            int listTop = _searchBoxRect.Bottom + 5;
            int listH = _listRect.Bottom - 4 - listTop;
            int visibleCount = listH / (RowHeight + RowGap);
            int totalCount = _currentMode == ViewMode.Locations ? _filteredLocations.Count : _filteredFestivals.Count;
            int maxScroll = Math.Max(0, totalCount - visibleCount);

            var trackRect = new Rectangle(_listRect.Right - 9, listTop, 5, listH);
            float ratio = Math.Clamp((float)visibleCount / totalCount, 0.15f, 1f);
            int thumbH = Math.Max(24, (int)(trackRect.Height * ratio));
            UpdateScrollFromMouse(y, trackRect, thumbH, maxScroll);
        }
    }

    public override void ReleaseLeftClick(int x, int y)
    {
        _isDraggingLeftScrollbar = false;
    }

    private void UpdateScrollFromMouse(int mouseY, Rectangle trackRect, int thumbH, int maxScroll)
    {
        if (maxScroll <= 0 || trackRect.Height <= thumbH) return;
        float progress = Math.Clamp((float)(mouseY - trackRect.Y - thumbH / 2) / (trackRect.Height - thumbH), 0f, 1f);
        int newOffset = (int)Math.Round(progress * maxScroll);
        if (_currentMode == ViewMode.Locations) _locScroll = newOffset;
        else _festScroll = newOffset;
    }

    public override bool ReceiveScrollWheel(int direction)
    {
        int mx = Game1.getMouseX(), my = Game1.getMouseY();

        if (_currentMode == ViewMode.Festivals && _isSeasonDropdownOpen)
        {
            var dropListRect = GetDropdownMenuRect(_seasonHeaderRect);
            if (dropListRect.Contains(mx, my) || _seasonHeaderRect.Contains(mx, my))
            {
                int maxScroll = Math.Max(0, SeasonOptions.Length - DropdownMaxVisible);
                _seasonDropdownScrollOffset = Math.Clamp(_seasonDropdownScrollOffset - (direction > 0 ? 1 : -1), 0, maxScroll);
                Game1.playSound("shwip");
                return true;
            }
        }

        if (_listRect.Contains(mx, my))
        {
            int listTop = _searchBoxRect.Bottom + 5;
            int visible = (_listRect.Bottom - 4 - listTop) / (RowHeight + RowGap);
            if (_currentMode == ViewMode.Locations)
            {
                _locScroll = Math.Clamp(_locScroll - (direction > 0 ? 1 : -1), 0, Math.Max(0, _filteredLocations.Count - visible));
            }
            else
            {
                _festScroll = Math.Clamp(_festScroll - (direction > 0 ? 1 : -1), 0, Math.Max(0, _filteredFestivals.Count - visible));
            }
            return true;
        }

        if (_descBox.ContainsPoint(mx, my))
        {
            _descBox.ReceiveScrollWheel(direction);
            return true;
        }

        return false;
    }

    public override bool ReceiveKeyPress(Keys key)
    {
        if (_currentMode == ViewMode.Festivals && _isSeasonDropdownOpen && key == Keys.Escape)
        {
            _isSeasonDropdownOpen = false;
            return true;
        }

        if (_searchBox.Selected)
        {
            if (key == Keys.Escape || key == Keys.Enter)
            {
                _searchBox.Selected = false;
                if (Game1.keyboardDispatcher.Subscriber == _searchBox) Game1.keyboardDispatcher.Subscriber = null;
                Game1.playSound("smallSelect");
                return true;
            }
            return true;
        }

        if (_festNameBox.Selected)
        {
            if (key == Keys.Escape)
            {
                DeselectAllBoxes();
                return true;
            }
            return false;
        }

        if (_descBox.Selected && Game1.keyboardDispatcher.Subscriber == _descBox)
        {
            if (key == Keys.Escape)
            {
                DeselectAllBoxes();
                return true;
            }

            if (!DialogueTextInputBox.IsControlKeyDown())
            {
                if (key == Keys.Left || key == Keys.Right || key == Keys.Home ||
                    key == Keys.End || key == Keys.Delete || key == Keys.Back || key == Keys.Enter)
                {
                    _descBox.RecieveSpecialInput(key);
                    return true;
                }
            }
            return true;
        }

        return false;
    }

    public override void OnHidden()
    {
        _isDraggingLeftScrollbar = false;
        _isSeasonDropdownOpen = false;
        DeselectAllBoxes();
    }

    public override void ResetToBaseline()
    {
        var service = ModEntry.WorldSummaryOverlay;
        if (service == null)
        {
            Game1.playSound("cancel");
            Game1.addHUDMessage(new HUDMessage("⚠ 世界设定覆盖层服务未初始化", HUDMessage.error_type));
            return;
        }

        if (!service.HasOverlay)
        {
            Game1.playSound("cancel");
            Game1.addHUDMessage(new HUDMessage("本页已无自定义设置", HUDMessage.error_type));
            return;
        }

        _selectedLocId = null;
        _selectedFestKey = null;

        if (!service.DeleteOverlay(out string err))
        {
            Game1.playSound("cancel");
            Game1.addHUDMessage(new HUDMessage($"⚠ 恢复默认失败: {err}", HUDMessage.error_type));
            return;
        }

        OnShown();
        Game1.playSound("coin");
        Game1.addHUDMessage(new HUDMessage("✔ 已恢复默认配置", HUDMessage.newQuest_type));
        Hub.RefreshEntries();
    }

    private string _formSnapshot = "";

    private string BuildFormFingerprint()
    {
        if (_currentMode == ViewMode.Locations)
            return $"{_selectedLocId}|{_descBox.Text.Trim()}";
        return $"{_isCreatingNewFest}|{_selectedFestKey ?? "NEW"}|{_festNameBox.Text.Trim()}|{_descBox.Text.Trim()}|{_dayStepper.Value}|{_selectedSeason}";
    }

    private void RefreshFormSnapshot() => _formSnapshot = BuildFormFingerprint();

    public override bool HasUnsavedChanges => (_currentMode == ViewMode.Locations
            ? _selectedLocId != null
            : (_selectedFestKey != null || _isCreatingNewFest))
        && BuildFormFingerprint() != _formSnapshot;

    public override bool TryCommitUnsavedChanges()
    {
        if (!HasUnsavedChanges) return true;
        if (_currentMode == ViewMode.Locations) SaveLocation();
        else SaveFestival();
        return !HasUnsavedChanges;
    }

    private void DeselectAllBoxes()
    {
        _descBox.Selected = false;
        _festNameBox.Selected = false;
        _searchBox.Selected = false;
        if (Game1.keyboardDispatcher.Subscriber == _descBox ||
            Game1.keyboardDispatcher.Subscriber == _festNameBox ||
            Game1.keyboardDispatcher.Subscriber == _searchBox)
        {
            Game1.keyboardDispatcher.Subscriber = null;
        }
    }

    // ── 渲染管线 ──

    public override void Draw(SpriteBatch b, Rectangle area, int mx, int my)
    {
        DrawModeSwitcher(b, mx, my);
        DrawLeftList(b, mx, my);
        DrawRightForm(b, mx, my);

        if (_currentMode == ViewMode.Festivals && _isSeasonDropdownOpen)
        {
            DrawDropdownOverlay(b, mx, my);
        }
    }

    private void DrawModeSwitcher(SpriteBatch b, int mx, int my)
    {
        DrawTabPill(b, _modeLocationsBtnRect, "🗺️ 地点环境描述", _currentMode == ViewMode.Locations, mx, my);
        DrawTabPill(b, _modeFestivalsBtnRect, "🎪 节日庆典日程", _currentMode == ViewMode.Festivals, mx, my);
    }

    private static void DrawTabPill(SpriteBatch b, Rectangle rect, string label, bool isActive, int mx, int my)
    {
        bool isHover = rect.Contains(mx, my);
        Color bg = isActive ? RulesTheme.SurfaceActive : (isHover ? RulesTheme.SurfaceHover : RulesTheme.SurfaceCard);
        Color border = isActive ? RulesTheme.BorderBold : (isHover ? RulesTheme.BorderMid : RulesTheme.BorderSoft);

        b.Draw(Game1.staminaRect, new Rectangle(rect.X + 1, rect.Y + 1, rect.Width - 2, rect.Height - 2), bg);
        IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(432, 439, 9, 9),
            rect.X, rect.Y, rect.Width, rect.Height, border, 2f, false);

        if (isActive)
        {
            b.Draw(Game1.staminaRect, new Rectangle(rect.X + 3, rect.Bottom - 3, rect.Width - 6, 2), RulesTheme.AccentGold);
        }

        var sz = CustomFontManager.MeasureStringBold(label, CustomFontManager.SizeSmall);
        Color textCol = isActive ? RulesTheme.TextCharcoal : DarkGrayText;

        CustomFontManager.DrawStringBold(b, label,
            new Vector2(rect.X + (rect.Width - sz.X) / 2f, rect.Y + (rect.Height - sz.Y) / 2f),
            textCol, CustomFontManager.SizeSmall);
    }

    private void DrawLeftList(SpriteBatch b, int mx, int my)
    {
        DrawSectionCard(b, _listRect);

        DrawSingleLineBox(b, _searchBox, DarkGrayText);
        if (string.IsNullOrEmpty(_searchBox.Text))
        {
            string ph = _currentMode == ViewMode.Locations ? "🔍 搜索地点或区域..." : "🔍 搜索节日名称...";
            CustomFontManager.DrawString(b, ph, new Vector2(_searchBox.X + 8, _searchBox.Y + 6), DarkGrayText, CustomFontManager.SizeSmall);
        }

        b.Draw(Game1.staminaRect,
            new Rectangle(_listRect.X + 6, _searchBoxRect.Bottom + 3, _listRect.Width - 12, 1),
            RulesTheme.BorderSoft * 0.8f);

        int listTop = _searchBoxRect.Bottom + 5;
        int listH = _listRect.Bottom - 4 - listTop;
        int visibleCount = listH / (RowHeight + RowGap);

        int totalCount = _currentMode == ViewMode.Locations ? _filteredLocations.Count : _filteredFestivals.Count;
        bool hasScroll = totalCount > visibleCount;

        int itemRightPad = hasScroll ? 15 : 6;
        int itemW = _listRect.Width - 6 - itemRightPad;
        bool isMouseDown = Mouse.GetState().LeftButton == ButtonState.Pressed;

        for (int i = 0; i < visibleCount; i++)
        {
            int scroll = _currentMode == ViewMode.Locations ? _locScroll : _festScroll;
            int idx = scroll + i;
            if (idx >= totalCount) break;

            var rowRect = new Rectangle(_listRect.X + 6, listTop + i * (RowHeight + RowGap), itemW, RowHeight);
            bool isSel = !_isCreatingNewFest && (_currentMode == ViewMode.Locations
                ? string.Equals(_filteredLocations[idx].Id, _selectedLocId, StringComparison.OrdinalIgnoreCase)
                : string.Equals(_filteredFestivals[idx].Key, _selectedFestKey, StringComparison.OrdinalIgnoreCase));

            bool isHover = rowRect.Contains(mx, my);
            bool isPressed = isHover && isMouseDown;
            int pressOffset = isPressed ? 1 : 0;

            Color bg = isSel ? RulesTheme.SurfaceActive
                     : isPressed ? RulesTheme.SurfaceSunken
                     : isHover ? RulesTheme.SurfaceHover
                     : RulesTheme.SurfaceCard;

            if (!isPressed)
            {
                b.Draw(Game1.staminaRect, new Rectangle(rowRect.X + 1, rowRect.Y + 2, rowRect.Width, rowRect.Height), RulesTheme.Shadow);
            }

            var drawRect = new Rectangle(rowRect.X, rowRect.Y + pressOffset, rowRect.Width, rowRect.Height);
            b.Draw(Game1.staminaRect, new Rectangle(drawRect.X + 1, drawRect.Y + 1, drawRect.Width - 2, drawRect.Height - 2), bg);
            IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(432, 439, 9, 9),
                drawRect.X, drawRect.Y, drawRect.Width, drawRect.Height,
                isSel ? RulesTheme.BorderBold : (isHover ? RulesTheme.BorderMid : RulesTheme.BorderSoft), 2f, false);

            if (isSel)
            {
                b.Draw(Game1.staminaRect, new Rectangle(drawRect.X + 2, drawRect.Y + 3, 4, drawRect.Height - 6), RulesTheme.AccentGold);
            }

            if (_currentMode == ViewMode.Locations)
            {
                DrawLocationRow(b, _filteredLocations[idx], drawRect, isSel, isHover);
            }
            else
            {
                DrawFestivalRow(b, _filteredFestivals[idx], drawRect, isSel, isHover);
            }
        }

        if (hasScroll)
        {
            var trackRect = new Rectangle(_listRect.Right - 9, listTop, 5, listH);
            int scroll = _currentMode == ViewMode.Locations ? _locScroll : _festScroll;
            DrawScrollbarVisual(b, trackRect, visibleCount, totalCount, scroll, _isDraggingLeftScrollbar, mx, my);
        }
    }

    private static void DrawLocationRow(SpriteBatch b, LocationEntry item, Rectangle drawRect, bool isSel, bool isHover)
    {
        // ★ 核心改动：地名前面的铭牌智能进行多语言判定与转换
        string regionName = GetLocalizedRegion(item.Region);
        var regSz = CustomFontManager.MeasureString(regionName, CustomFontManager.SizeSmall);
        var regRect = new Rectangle(drawRect.X + 8, drawRect.Y + (drawRect.Height - 20) / 2, (int)regSz.X + 8, 20);

        b.Draw(Game1.staminaRect, regRect, RulesTheme.SurfaceSunken);
        IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(403, 383, 6, 6),
            regRect.X, regRect.Y, regRect.Width, regRect.Height, RulesTheme.BorderSoft, 1f, false);

        CustomFontManager.DrawString(b, regionName,
            new Vector2(regRect.X + (regRect.Width - regSz.X) / 2f, regRect.Y + (regRect.Height - regSz.Y) / 2f),
            Color.White, CustomFontManager.SizeSmall);

        int textX = regRect.Right + 8;
        string nameText = item.Name + (item.HasCustomOverlay ? " [*]" : "");
        string truncated = CustomFontManager.TruncateString(nameText, CustomFontManager.SizeSmall, drawRect.Right - textX - 8);

        CustomFontManager.DrawString(b, truncated, new Vector2(textX, drawRect.Y + 11),
            isSel ? RulesTheme.TextCharcoal : (isHover ? RulesTheme.TextCharcoal : RulesTheme.TextDarkBrown), CustomFontManager.SizeSmall);
    }

    private static void DrawFestivalRow(SpriteBatch b, FestivalEntry item, Rectangle drawRect, bool isSel, bool isHover)
    {
        string dateBadge = $"{GetSeasonShortZh(item.Season)}{item.Day}";
        var badgeRect = new Rectangle(drawRect.X + 8, drawRect.Y + (drawRect.Height - 20) / 2, 40, 20);

        Color badgeColor = item.Season switch
        {
            "spring" => new Color(145, 80, 115),
            "summer" => new Color(175, 115, 45),
            "fall" => new Color(165, 85, 40),
            _ => new Color(70, 115, 160)
        };

        b.Draw(Game1.staminaRect, badgeRect, badgeColor);
        IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(403, 383, 6, 6),
            badgeRect.X, badgeRect.Y, badgeRect.Width, badgeRect.Height, RulesTheme.BorderMid, 1f, false);

        var badgeSz = CustomFontManager.MeasureStringBold(dateBadge, CustomFontManager.SizeSmall);
        CustomFontManager.DrawStringBold(b, dateBadge,
            new Vector2(badgeRect.X + (badgeRect.Width - badgeSz.X) / 2f, badgeRect.Y + (badgeRect.Height - badgeSz.Y) / 2f),
            Color.White, CustomFontManager.SizeSmall);

        int textX = badgeRect.Right + 8;
        string label = item.Name;
        if (item.IsCustom) label += " [★自创]";

        string truncated = CustomFontManager.TruncateString(label, CustomFontManager.SizeSmall, drawRect.Right - textX - 8);
        CustomFontManager.DrawString(b, truncated, new Vector2(textX, drawRect.Y + 11), RulesTheme.TextCharcoal, CustomFontManager.SizeSmall);
    }

    private void DrawRightForm(SpriteBatch b, int mx, int my)
    {
        DrawSectionCard(b, _formRect);

        int lx = _formRect.X + 12;

        if (_currentMode == ViewMode.Locations)
        {
            var loc = _allLocations.FirstOrDefault(l => string.Equals(l.Id, _selectedLocId, StringComparison.OrdinalIgnoreCase));
            if (loc == null)
            {
                CustomFontManager.DrawString(b, "从左侧列表选择一个地点进行环境感知编撰",
                    new Vector2(_formRect.X + 24, _formRect.Y + 24), RulesTheme.TextSecondary, CustomFontManager.SizeRegular);
                return;
            }

            var infoRect = new Rectangle(lx, _formRect.Y + 10, _formRect.Width - 24, 46);
            b.Draw(Game1.staminaRect, infoRect, RulesTheme.SurfaceSunken);
            IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(403, 383, 6, 6),
                infoRect.X, infoRect.Y, infoRect.Width, infoRect.Height, RulesTheme.BorderSoft, 1.2f, false);

            CustomFontManager.DrawStringBold(b, $"📍 {loc.Name}", new Vector2(infoRect.X + 10, infoRect.Y + 8), RulesTheme.TextCharcoal, CustomFontManager.SizeRegular);
            CustomFontManager.DrawString(b, $"区域: {GetLocalizedRegion(loc.Region)}  |  标识: {loc.Id}", new Vector2(infoRect.X + 12, infoRect.Y + 26), RulesTheme.TextSecondary, CustomFontManager.SizeSmall);

            DrawFieldLabel(b, "场景氛围与环境描写 (AI 将根据此描述感知周围)", lx, (int)_descBox.Position.Y - 22);
            _descBox.Draw(b);

            DrawFormButton(b, _btnSave, "✔ 保存描述", mx, my, isPrimary: true);
            DrawFormButton(b, _btnRevert, "↺ 还原原版", mx, my, isPrimary: false, isEnabled: loc.HasCustomOverlay);

            if (!string.IsNullOrEmpty(_statusMessage))
            {
                Color msgCol = _statusMessage.StartsWith("✔") ? RulesTheme.AccentGreen : RulesTheme.AccentAmber;
                CustomFontManager.DrawString(b, _statusMessage, new Vector2(lx + 5, _btnSave.Y - 27), msgCol, CustomFontManager.SizeSmall);
            }
        }
        else
        {
            if (string.IsNullOrEmpty(_selectedFestKey) && !_isCreatingNewFest)
            {
                CustomFontManager.DrawString(b, "从左侧选择或新建一个节日日程",
                    new Vector2(_formRect.X + 24, _formRect.Y + 24), RulesTheme.TextSecondary, CustomFontManager.SizeRegular);
                return;
            }

            var fest = !string.IsNullOrEmpty(_selectedFestKey)
                ? _allFestivals.FirstOrDefault(f => string.Equals(f.Key, _selectedFestKey, StringComparison.OrdinalIgnoreCase))
                : null;

            DrawFieldLabel(b, "节日名称", lx, _festNameBox.Y + 4);
            DrawSingleLineBox(b, _festNameBox, RulesTheme.TextCharcoal);

            DrawFieldLabel(b, "举行日期", lx, _dayStepperRect.Y + 4);
            DrawDropdownHeader(b, _seasonHeaderRect, _selectedSeason, _isSeasonDropdownOpen, mx, my);
            _dayStepper.Draw(b);

            DrawFieldLabel(b, "节日风俗与庆典氛围 (居民将围绕此氛围交谈互动)", lx, (int)_descBox.Position.Y - 22);
            _descBox.Draw(b);

            var conflictFest = GetConflictingFestival(_selectedSeason, _dayStepper.Value);
            bool canSave = !string.IsNullOrWhiteSpace(_festNameBox.Text);

            DrawFormButton(b, _btnSave, "✔ 保存节日", mx, my, isPrimary: true, isEnabled: canSave && conflictFest == null);
            DrawFormButton(b, _btnRevert, "↺ 还原节日", mx, my, isPrimary: false, isEnabled: !_isCreatingNewFest && fest?.IsBaseline == true);

            // ★ 核心改动：原版节日不可删除（置灰呈现），仅自创节日支持删除
            string deleteBtnText = _isCreatingNewFest ? (IsZh ? "取消新建" : "Cancel") : (IsZh ? "删除节日" : "Delete Festival");
            bool canDelete = _isCreatingNewFest || (fest?.IsCustom == true);
            DrawFormButton(b, _btnDelete, deleteBtnText, mx, my, isPrimary: false, isEnabled: canDelete, isDanger: fest?.IsCustom == true);

            DrawFormButton(b, _btnNew, "+ 新建节日", mx, my, isPrimary: false);

            Vector2 statusPos = new(lx + 5, _btnSave.Y - 27);

            if (conflictFest != null)
            {
                CustomFontManager.DrawString(b, $"⚠ 该日期已有节日【{conflictFest.Name}】，同一天只能允许有一个节日！",
                    statusPos, RulesTheme.AccentRed, CustomFontManager.SizeSmall);
            }
            else if (!string.IsNullOrEmpty(_statusMessage))
            {
                bool isCreatePrompt = _statusMessage.Contains("已开启自创节日模式");
                Color msgCol = isCreatePrompt
                    ? Color.White
                    : (_statusMessage.StartsWith("✔") ? RulesTheme.AccentGreen : RulesTheme.AccentAmber);

                CustomFontManager.DrawString(b, _statusMessage, statusPos, msgCol, CustomFontManager.SizeSmall);
            }
        }
    }

    private void DrawDropdownHeader(SpriteBatch b, Rectangle headerRect, string selectedSeasonId, bool isOpen, int mx, int my)
    {
        bool isHover = headerRect.Contains(mx, my);
        Color bg = isOpen ? RulesTheme.SurfaceActive : (isHover ? RulesTheme.SurfaceHover : RulesTheme.SurfaceCard);
        Color border = isOpen ? RulesTheme.BorderBold : (isHover ? RulesTheme.BorderBold : RulesTheme.BorderSoft);

        b.Draw(Game1.staminaRect, new Rectangle(headerRect.X + 1, headerRect.Y + 1, headerRect.Width - 2, headerRect.Height - 2), bg);
        IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(432, 439, 9, 9),
            headerRect.X, headerRect.Y, headerRect.Width, headerRect.Height, border, 2f, false);

        string text = SeasonOptions.FirstOrDefault(s => s.Id == selectedSeasonId).Label ?? selectedSeasonId;

        CustomFontManager.DrawString(b, text,
            new Vector2(headerRect.X + 10, headerRect.Y + 5),
            RulesTheme.TextCharcoal, CustomFontManager.SizeRegular);

        int arrowY = headerRect.Y + (headerRect.Height - 12) / 2;
        Rectangle srcArrow = isOpen ? new Rectangle(421, 459, 11, 12) : new Rectangle(421, 472, 11, 12);
        b.Draw(Game1.mouseCursors, new Rectangle(headerRect.Right - 22, arrowY, 14, 14), srcArrow, Color.White);
    }

    private void DrawDropdownOverlay(SpriteBatch b, int mx, int my)
    {
        var menuRect = GetDropdownMenuRect(_seasonHeaderRect);

        b.Draw(Game1.staminaRect, new Rectangle(menuRect.X + 2, menuRect.Y + 3, menuRect.Width, menuRect.Height), Color.Black * 0.25f);
        b.Draw(Game1.staminaRect, new Rectangle(menuRect.X + 1, menuRect.Y + 1, menuRect.Width - 2, menuRect.Height - 2), RulesTheme.SurfaceCard);
        IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(432, 439, 9, 9),
            menuRect.X, menuRect.Y, menuRect.Width, menuRect.Height, RulesTheme.BorderBold, 2f, false);

        int count = Math.Min(SeasonOptions.Length, DropdownMaxVisible);
        bool hasScroll = SeasonOptions.Length > DropdownMaxVisible;
        int itemW = hasScroll ? menuRect.Width - 14 : menuRect.Width - 4;

        for (int i = 0; i < count; i++)
        {
            int optIdx = _seasonDropdownScrollOffset + i;
            if (optIdx >= SeasonOptions.Length) break;

            var opt = SeasonOptions[optIdx];
            var itemRect = new Rectangle(menuRect.X + 2, menuRect.Y + 4 + i * DropdownItemHeight, itemW, DropdownItemHeight);
            bool isHover = itemRect.Contains(mx, my);
            bool isSelected = string.Equals(opt.Id, _selectedSeason, StringComparison.OrdinalIgnoreCase);

            if (isHover || isSelected)
            {
                b.Draw(Game1.staminaRect, itemRect, isSelected ? RulesTheme.SurfaceActive : RulesTheme.SurfaceHover);
            }

            if (isSelected)
            {
                b.Draw(Game1.staminaRect, new Rectangle(itemRect.X, itemRect.Y + 3, 3, itemRect.Height - 6), RulesTheme.AccentGold);
            }

            int textClipX = itemRect.X + 10;
            var fontSz = CustomFontManager.MeasureString(opt.Label, CustomFontManager.SizeRegular);
            float textY = itemRect.Y + (itemRect.Height - fontSz.Y) / 2f;
            Color textCol = isSelected ? RulesTheme.TextCharcoal : (isHover ? RulesTheme.TextCharcoal : RulesTheme.TextDarkBrown);

            CustomFontManager.DrawString(b, opt.Label,
                new Vector2(textClipX, textY),
                textCol, CustomFontManager.SizeRegular);

            if (isSelected)
            {
                string check = "✔";
                var csz = CustomFontManager.MeasureStringBold(check, CustomFontManager.SizeRegular);
                CustomFontManager.DrawStringBold(b, check,
                    new Vector2(itemRect.Right - csz.X - 6, itemRect.Y + (itemRect.Height - csz.Y) / 2f),
                    RulesTheme.AccentGold, CustomFontManager.SizeRegular);
            }
        }

        if (hasScroll)
        {
            var trackRect = new Rectangle(menuRect.Right - 8, menuRect.Y + 4, 5, menuRect.Height - 8);
            b.Draw(Game1.staminaRect, trackRect, RulesTheme.SurfaceSunken);

            int maxScroll = SeasonOptions.Length - DropdownMaxVisible;
            float ratio = (float)DropdownMaxVisible / SeasonOptions.Length;
            int thumbH = Math.Max(20, (int)(trackRect.Height * ratio));
            int thumbY = trackRect.Y + (int)((trackRect.Height - thumbH) * ((float)_seasonDropdownScrollOffset / maxScroll));

            b.Draw(Game1.staminaRect, new Rectangle(trackRect.X - 1, thumbY, trackRect.Width + 2, thumbH), RulesTheme.BorderBold);
        }
    }

    private Rectangle GetDropdownMenuRect(Rectangle headerRect)
    {
        int count = Math.Min(SeasonOptions.Length, DropdownMaxVisible);
        int menuH = count * DropdownItemHeight + 8;
        return new Rectangle(headerRect.X, headerRect.Bottom + 2, headerRect.Width, menuH);
    }

    private static void DrawFieldLabel(SpriteBatch b, string text, int x, int y)
    {
        CustomFontManager.DrawStringBold(b, text, new Vector2(x, y), RulesTheme.TextDarkBrown, CustomFontManager.SizeRegular);
    }

    private static void DrawScrollbarVisual(SpriteBatch b, Rectangle trackRect, int visibleCount, int totalCount, int scrollOffset, bool isDragging, int mx, int my)
    {
        if (totalCount <= visibleCount || trackRect.Height <= 0) return;

        b.Draw(Game1.staminaRect, trackRect, RulesTheme.SurfaceSunken);
        b.Draw(Game1.staminaRect, new Rectangle(trackRect.X, trackRect.Y, 1, trackRect.Height), RulesTheme.BorderSoft * 0.5f);

        int maxScroll = totalCount - visibleCount;
        float visibleRatio = Math.Clamp((float)visibleCount / totalCount, 0.15f, 1f);
        int thumbH = Math.Max(24, (int)(trackRect.Height * visibleRatio));
        int thumbY = trackRect.Y + (int)((trackRect.Height - thumbH) * ((float)scrollOffset / maxScroll));
        var thumbRect = new Rectangle(trackRect.X - 1, thumbY, trackRect.Width + 2, thumbH);

        bool thumbHover = thumbRect.Contains(mx, my);
        Color thumbBg = isDragging ? RulesTheme.BorderBold : (thumbHover ? RulesTheme.AccentGold : RulesTheme.BorderMid);

        b.Draw(Game1.staminaRect, thumbRect, thumbBg);
        IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(403, 383, 6, 6),
            thumbRect.X, thumbRect.Y, thumbRect.Width, thumbRect.Height, RulesTheme.BorderBold, 1f, false);
    }

    private static void DrawSectionCard(SpriteBatch b, Rectangle rect)
    {
        b.Draw(Game1.staminaRect, new Rectangle(rect.X + 1, rect.Y + 1, rect.Width - 2, rect.Height - 2), RulesTheme.SurfacePanel);
        IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(432, 439, 9, 9),
            rect.X, rect.Y, rect.Width, rect.Height, RulesTheme.BorderSoft, 2f, false);
    }

    private static void DrawSingleLineBox(SpriteBatch b, TextBox box, Color? textColor = null)
    {
        var boxRect = new Rectangle(box.X, box.Y, box.Width, box.Height);
        b.Draw(Game1.staminaRect, new Rectangle(boxRect.X + 2, boxRect.Y + 2, boxRect.Width - 4, boxRect.Height - 4),
            box.Selected ? new Color(255, 252, 245) : new Color(245, 240, 230));

        IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(432, 439, 9, 9),
            boxRect.X, boxRect.Y, boxRect.Width, boxRect.Height,
            box.Selected ? RulesTheme.BorderBold : RulesTheme.BorderSoft, 2f, false);

        string text = box.Text ?? "";
        Vector2 sz = CustomFontManager.MeasureString(text, CustomFontManager.SizeRegular);
        Color col = textColor ?? RulesTheme.TextCharcoal;
        CustomFontManager.DrawString(b, text, new Vector2(boxRect.X + 8, boxRect.Y + (boxRect.Height - sz.Y) / 2f - 1), col, CustomFontManager.SizeRegular);

        if (box.Selected && (int)(Game1.currentGameTime.TotalGameTime.TotalMilliseconds / 500) % 2 == 0)
        {
            b.Draw(Game1.staminaRect, new Rectangle((int)(boxRect.X + 8 + sz.X + 1), boxRect.Y + 6, 2, boxRect.Height - 12), RulesTheme.TextPrimary);
        }
    }

    private static void DrawFormButton(SpriteBatch b, Rectangle rect, string label, int mx, int my,
        bool isPrimary = false, bool isEnabled = true, bool isDanger = false)
    {
        bool isHover = isEnabled && rect.Contains(mx, my);
        bool isPressed = isHover && Mouse.GetState().LeftButton == ButtonState.Pressed;
        int pressOffset = isPressed ? 1 : 0;

        Color bg = !isEnabled ? new Color(225, 215, 200)
                 : isDanger ? (isHover ? RulesTheme.SurfaceDangerHover : RulesTheme.SurfaceDanger)
                 : isPressed ? RulesTheme.SurfaceSunken
                 : isPrimary ? (isHover ? RulesTheme.SurfaceHover : RulesTheme.SurfaceActive)
                 : (isHover ? RulesTheme.SurfaceHover : RulesTheme.SurfaceCard);

        Color border = !isEnabled ? RulesTheme.BorderSoft
                     : isDanger ? RulesTheme.AccentRed
                     : isPressed ? RulesTheme.BorderBold
                     : isPrimary ? (isHover ? RulesTheme.BorderBold : RulesTheme.BorderMid)
                     : (isHover ? RulesTheme.BorderMid : RulesTheme.BorderSoft);

        if (!isPressed && isEnabled)
        {
            b.Draw(Game1.staminaRect, new Rectangle(rect.X + 1, rect.Y + 2, rect.Width, rect.Height), RulesTheme.Shadow);
        }

        var drawRect = new Rectangle(rect.X, rect.Y + pressOffset, rect.Width, rect.Height);
        b.Draw(Game1.staminaRect, new Rectangle(drawRect.X + 1, drawRect.Y + 1, drawRect.Width - 2, drawRect.Height - 2), bg);
        IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(432, 439, 9, 9),
            drawRect.X, drawRect.Y, drawRect.Width, drawRect.Height, border, 2f, false);

        var sz = CustomFontManager.MeasureStringBold(label, CustomFontManager.SizeSmall);
        CustomFontManager.DrawStringBold(b, label,
            new Vector2(drawRect.X + (drawRect.Width - sz.X) / 2f, drawRect.Y + (drawRect.Height - sz.Y) / 2f),
            isEnabled ? RulesTheme.TextCharcoal : RulesTheme.TextMuted, CustomFontManager.SizeSmall);
    }

    // ── 缓存与数据层同步 ──

    private void BuildBaselineCache()
    {
        _baselineLocations.Clear();
        _baselineRegions.Clear();
        _baselineLocDescriptions.Clear();
        _baselineFestivals.Clear();
        _baselineFestDescriptions.Clear();

        GameSummary? summary = null;

        try
        {
            summary = ModEntry.SHelper.GameContent.Load<GameSummary>(VtConstants.GameSummaryPath);
        }
        catch { }

        if (summary?.Locations?.Entries == null && summary?.Festivals?.Entries == null)
        {
            summary = LoadBaselineFromLocalFile();
        }

        if (summary != null)
        {
            if (summary.Locations?.Entries != null)
            {
                foreach (var kv in summary.Locations.Entries)
                {
                    if (!string.IsNullOrEmpty(kv.Key) && kv.Value != null)
                    {
                        if (IsExcludedLocation(null, kv.Key)) continue;

                        string dispName = ResolveI18nToken(string.IsNullOrEmpty(kv.Value.Name) ? kv.Key : kv.Value.Name);
                        if (IsInvalidDisplayName(dispName)) continue;

                        _baselineLocations[kv.Key] = dispName;
                        _baselineRegions[kv.Key] = kv.Value.Region ?? "Pelican Town";
                        _baselineLocDescriptions[kv.Key] = ResolveI18nToken(kv.Value.Description ?? "");
                    }
                }
            }
            if (summary.Festivals?.Entries != null)
            {
                foreach (var kv in summary.Festivals.Entries)
                {
                    if (!string.IsNullOrEmpty(kv.Key) && kv.Value != null)
                    {
                        _baselineFestivals[kv.Key] = ResolveI18nToken(string.IsNullOrEmpty(kv.Value.Name) ? kv.Key : kv.Value.Name);
                        _baselineFestDescriptions[kv.Key] = ResolveI18nToken(kv.Value.Description ?? "");
                    }
                }
            }
        }
        else
        {
            ModEntry.SMonitor?.Log("[LocationFestival] 基线数据加载失败，未能在游戏资产管道或本地目录找到有效的 GameSummary.json", LogLevel.Warn);
        }

        // 在基线文件载入完成后，扫描游戏内地图与节日数据（合并第三方 Mod）
        ScanGameLocations();
        ScanGameFestivals();
    }

    // ── 地图动态探测与过滤 ──

    private void ScanGameLocations()
{
    var visitedIds = new HashSet<string>(_baselineLocations.Keys, StringComparer.OrdinalIgnoreCase);

    // 记录已经出现的 (DisplayName + Region) 组合，防止大量同名无意义子地图刷屏
    var existingNameRegions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var kv in _baselineLocations)
    {
        string reg = _baselineRegions.GetValueOrDefault(kv.Key, "Pelican Town");
        existingNameRegions.Add($"{kv.Value}@@{reg}");
    }

    // 1. 扫描当前运行实例
    if (Game1.locations != null)
    {
        var locQueue = new List<GameLocation>(Game1.locations);
        for (int i = 0; i < locQueue.Count; i++)
        {
            var loc = locQueue[i];
            if (loc == null) continue;

            if (loc.buildings != null)
            {
                foreach (var b in loc.buildings)
                {
                    var indoors = b.indoors.Value;
                    if (indoors != null && !locQueue.Contains(indoors))
                    {
                        locQueue.Add(indoors);
                    }
                }
            }

            string locId = loc.NameOrUniqueName ?? loc.Name ?? "";
            if (string.IsNullOrWhiteSpace(locId)) continue;
            if (IsExcludedLocation(loc, locId)) continue;

            if (visitedIds.Add(locId))
            {
                string dispName = ResolveLocationDisplayName(loc, locId);

                // ★ 关键拦截：如果地图以 Custom_ 开头，却没有任何 DisplayName（解析结果依然等于 locId），说明是技术地图，直接丢弃
                if (locId.StartsWith("Custom_", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(dispName, locId, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (IsInvalidDisplayName(dispName)) continue;

                string region = InferLocationRegion(loc, locId);
                string uniqueKey = $"{dispName}@@{region}";

                // ★ 同名地图优化：如果同一区域下已经存在同名地图，跳过次级变体
                if (!existingNameRegions.Add(uniqueKey))
                    continue;

                _baselineLocations[locId] = dispName;
                _baselineRegions[locId] = region;
                if (!_baselineLocDescriptions.ContainsKey(locId))
                {
                    _baselineLocDescriptions[locId] = IsZh
                        ? $"{dispName}的现场环境。居民们在此活动与交流。"
                        : $"The environment of {dispName}.";
                }
            }
        }
    }

    // 2. 扫描 Data/Locations 静态注册表
    try
    {
        var dataLocs = Game1.content.Load<Dictionary<string, LocationData>>("Data/Locations");
        if (dataLocs != null)
        {
            foreach (var (locId, locData) in dataLocs)
            {
                if (string.IsNullOrWhiteSpace(locId)) continue;
                if (IsExcludedLocation(null, locId)) continue;

                if (visitedIds.Add(locId))
                {
                    string dispName = locId;
                    if (locData != null && !string.IsNullOrWhiteSpace(locData.DisplayName))
                    {
                        try
                        {
                            dispName = TokenParser.ParseText(locData.DisplayName);
                        }
                        catch { }
                    }

                    // ★ 关键拦截：如果属于 Custom_ 但没有提供可读 DisplayName，丢弃
                    if (locId.StartsWith("Custom_", StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(dispName, locId, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (IsInvalidDisplayName(dispName)) continue;

                    string region = InferLocationRegion(null, locId);
                    string uniqueKey = $"{dispName}@@{region}";

                    // ★ 同名地图优化：同一区域已存在同名地名则跳过
                    if (!existingNameRegions.Add(uniqueKey))
                        continue;

                    _baselineLocations[locId] = dispName;
                    _baselineRegions[locId] = region;
                    if (!_baselineLocDescriptions.ContainsKey(locId))
                    {
                        _baselineLocDescriptions[locId] = IsZh
                            ? $"{dispName}的现场环境。居民们在此活动与交流。"
                            : $"The environment of {dispName}.";
                    }
                }
            }
        }
    }
    catch (Exception ex)
    {
        ModEntry.SMonitor?.Log($"[LocationFestival] 扫描 Data/Locations 时提示: {ex.Message}", LogLevel.Trace);
    }
}

    private static bool IsExcludedLocation(GameLocation? loc, string locId)
{
    if (string.IsNullOrWhiteSpace(locId)) return true;

    // 1. 地牢、矿洞排除
    if (loc is StardewValley.Locations.MineShaft or StardewValley.Locations.VolcanoDungeon)
        return true;

    string typeName = loc?.GetType().Name ?? "";
    if (typeName.Contains("MineShaft", StringComparison.OrdinalIgnoreCase) ||
        typeName.Contains("VolcanoDungeon", StringComparison.OrdinalIgnoreCase) ||
        typeName.EndsWith("Dungeon", StringComparison.OrdinalIgnoreCase))
    {
        return true;
    }

    if (int.TryParse(locId, out _)) return true; // 纯数字矿洞层

    if (locId.StartsWith("UndergroundMine", StringComparison.OrdinalIgnoreCase) ||
        locId.StartsWith("VolcanoDungeon", StringComparison.OrdinalIgnoreCase) ||
        locId.StartsWith("MineShaft", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(locId, "Mine", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(locId, "Mines", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(locId, "SkullCave", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(locId, "Caldera", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(locId, "BugLand", StringComparison.OrdinalIgnoreCase))
    {
        return true;
    }

    string lower = locId.ToLowerInvariant();

    // 2. 排除地窖、联机废弃房间、临时过渡房
    if (lower.Contains("cellar") || lower.Contains("warproom") || lower.Contains("warp_room") || lower.Contains("_warp"))
        return true;

    // 3. 排除技术地图：过场动画、假层、废墟过渡、舞台、测试等
    if (lower.Contains("fake") ||           // 如 Custom_FakeEnchantedGrove2
        lower.Contains("ruins") ||          // 如 Custom_GrandpasShedRuins
        lower.Contains("cutscene") ||
        lower.Contains("intro") ||
        lower.Contains("eventroom") ||
        lower.Contains("holdingroom") ||
        lower.Contains("stagingroom") ||
        lower.Contains("backstage") ||
        lower.Contains("bufferroom") ||
        lower.Contains("testmap") ||
        lower.Contains("debug") ||
        lower.Contains("dummy") ||
        lower.Contains("sandbox") ||
        lower.StartsWith("temp") ||
        lower.Contains("-festival") ||      // 节日临时城镇副本
        lower.Contains("-eggfestival") ||
        lower.Contains("-fair"))
    {
        return true;
    }

    return false;
}

    /// <summary>
    /// 判定是否为未正确本地化或破损无效的地图名称（如 no translation 等）
    /// </summary>
    private static bool IsInvalidDisplayName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return true;
        string lower = name.ToLowerInvariant();
        if (lower.Contains("no translation") ||
            lower.Contains("missing translation") ||
            lower.Contains("(no translation:") ||
            (lower.StartsWith("{{") && lower.EndsWith("}}")))
        {
            return true;
        }
        return false;
    }

    private static string ResolveLocationDisplayName(GameLocation? loc, string locId)
    {
        try
        {
            if (loc != null && !string.IsNullOrWhiteSpace(loc.DisplayName) && !string.Equals(loc.DisplayName, locId, StringComparison.OrdinalIgnoreCase))
            {
                return TokenParser.ParseText(loc.DisplayName);
            }
        }
        catch { }

        try
        {
            var dataLocs = Game1.content.Load<Dictionary<string, LocationData>>("Data/Locations");
            if (dataLocs != null && dataLocs.TryGetValue(locId, out var locData) && !string.IsNullOrWhiteSpace(locData.DisplayName))
            {
                string parsed = TokenParser.ParseText(locData.DisplayName);
                if (!string.IsNullOrWhiteSpace(parsed)) return parsed;
            }
        }
        catch { }

        return locId;
    }

    private static string InferLocationRegion(GameLocation? loc, string locId)
    {
        if (loc != null)
        {
            try
            {
                if (loc.InIslandContext())
                    return "Ginger Island";
            }
            catch { }
        }

        string idUpper = locId.ToUpperInvariant();
        if (idUpper.Contains("ISLAND"))
            return "Ginger Island";
        if (idUpper.StartsWith("FARM") || idUpper.Contains("CABIN"))
            return "Farm";
        if (idUpper.Contains("RIDGESIDE") || idUpper.Contains("RSV"))
            return "Ridgeside Village";
        if (idUpper.Contains("EASTSCARP") || idUpper.Contains("SCARP"))
            return "East Scarp";
        if (idUpper.Contains("DESERT"))
            return "Calico Desert";
        if (idUpper.Contains("BEACH"))
            return "Beach";
        if (idUpper.Contains("MOUNTAIN") || idUpper.Contains("MINE") || idUpper.Contains("QUARRY"))
            return "Mountain";
        if (idUpper.Contains("FOREST") || idUpper.Contains("WOODS"))
            return "Cindersap Forest";
        if (idUpper.Contains("RAILROAD"))
            return "Railroad";

        return "Pelican Town";
    }

    /// <summary>
    /// 根据游戏当前语言对区域铭牌进行智能转换
    /// </summary>
    private static string GetLocalizedRegion(string? rawRegion)
    {
        if (string.IsNullOrWhiteSpace(rawRegion))
            return IsZh ? "小镇" : "Pelican Town";

        string reg = rawRegion.Trim();

        if (IsZh)
        {
            return reg.ToLowerInvariant() switch
            {
                "pelican town" or "pelicantown" or "town" or "pelican" => "小镇",
                "ginger island" or "gingerisland" or "island" => "姜岛",
                "farm" => "农场",
                "forest" or "cindersap forest" or "cindersap" => "森林",
                "mountain" or "mountains" => "山区",
                "beach" => "海滩",
                "desert" or "calico desert" or "calico" => "沙漠",
                "railroad" => "铁路",
                "woods" or "secret woods" or "secretwoods" => "秘密森林",
                "bus stop" or "busstop" => "车站",
                "backwoods" => "边远森林",
                "sewers" or "sewer" => "下水道",
                "swamp" or "witch swamp" => "女巫沼泽",
                "ridgeside" or "ridgeside village" or "rsv" => "脊线村",
                "east scarp" or "eastscarp" or "scarp" => "东围崖",
                "grampleton" => "格兰普顿",
                "zuzu city" or "zuzu" or "zuzucity" => "祖祖城",
                _ => reg
            };
        }
        else
        {
            return reg switch
            {
                "小镇" or "鹈鹕镇" => "Pelican Town",
                "姜岛" => "Ginger Island",
                "农场" => "Farm",
                "森林" or "煤油森林" => "Cindersap Forest",
                "山区" => "Mountain",
                "海滩" => "Beach",
                "沙漠" or "卡利科沙漠" => "Calico Desert",
                "铁路" => "Railroad",
                "秘密森林" => "Secret Woods",
                "车站" => "Bus Stop",
                "边远森林" => "Backwoods",
                "下水道" => "Sewers",
                "女巫沼泽" => "Witch's Swamp",
                "脊线村" => "Ridgeside Village",
                "东围崖" => "East Scarp",
                "格兰普顿" => "Grampleton",
                "祖祖城" => "Zuzu City",
                _ => reg
            };
        }
    }

    // ── 节日动态探测（第三方 Mod 兼容） ──

    private sealed class PassiveFestivalInfo
    {
        public string? Season { get; set; }
        public int StartDay { get; set; } = 1;
        public int EndDay { get; set; } = 1;
        public string? DisplayName { get; set; }
    }

    private void ScanGameFestivals()
    {
        // 1. 扫描标准节日表 Data/Festivals/FestivalDates
        try
        {
            var dates = Game1.content.Load<Dictionary<string, string>>("Data\\Festivals\\FestivalDates");
            if (dates != null)
            {
                foreach (var (key, rawName) in dates)
                {
                    if (string.IsNullOrWhiteSpace(key)) continue;

                    string festKey = key.Trim();
                    string name = ResolveI18nToken(rawName);
                    try
                    {
                        name = TokenParser.ParseText(name);
                    }
                    catch { }

                    if (string.IsNullOrWhiteSpace(name)) name = festKey;

                    if (!_baselineFestivals.ContainsKey(festKey))
                    {
                        _baselineFestivals[festKey] = name;
                        _baselineFestDescriptions[festKey] = IsZh
                            ? $"{name}庆典。居民们将在这一天欢聚一堂。"
                            : $"The celebration of {name}.";
                    }
                    else if (string.IsNullOrWhiteSpace(_baselineFestivals[festKey]))
                    {
                        _baselineFestivals[festKey] = name;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            ModEntry.SMonitor?.Log($"[LocationFestival] 扫描 FestivalDates 时提示: {ex.Message}", LogLevel.Trace);
        }

        // 2. 扫描星露谷 1.6+ 被动节日表 Data/PassiveFestivals
        try
        {
            var passives = Game1.content.Load<Dictionary<string, PassiveFestivalInfo>>("Data/PassiveFestivals");
            if (passives != null)
            {
                foreach (var (pId, pData) in passives)
                {
                    if (pData == null || string.IsNullOrWhiteSpace(pData.Season)) continue;
                    string season = pData.Season.Trim().ToLowerInvariant();

                    string baseName = pId;
                    if (!string.IsNullOrWhiteSpace(pData.DisplayName))
                    {
                        try
                        {
                            baseName = TokenParser.ParseText(pData.DisplayName);
                        }
                        catch { }
                    }

                    int start = Math.Clamp(pData.StartDay, 1, 28);
                    int end = pData.EndDay >= start ? Math.Clamp(pData.EndDay, start, 28) : start;

                    for (int d = start; d <= end; d++)
                    {
                        string key = $"{season}{d}";
                        if (!_baselineFestivals.ContainsKey(key))
                        {
                            string suffix = (start != end) ? (IsZh ? $" (第{d - start + 1}天)" : $" (Day {d - start + 1})") : "";
                            string fullName = baseName + suffix;

                            _baselineFestivals[key] = fullName;
                            _baselineFestDescriptions[key] = IsZh
                                ? $"{baseName}活动日程。居民们在此期间共同参与。"
                                : $"The schedule for {baseName}.";
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            ModEntry.SMonitor?.Log($"[LocationFestival] 扫描 PassiveFestivals 时提示: {ex.Message}", LogLevel.Trace);
        }
    }

    private static GameSummary? LoadBaselineFromLocalFile()
    {
        try
        {
            string[] possiblePaths = {
                "GameSummary.json",
                "assets/GameSummary.json",
                "content/GameSummary.json",
                "assets/data/GameSummary.json"
            };

            foreach (var relPath in possiblePaths)
            {
                string fullPath = System.IO.Path.Combine(ModEntry.SHelper.DirectoryPath, relPath);
                if (System.IO.File.Exists(fullPath))
                {
                    string json = System.IO.File.ReadAllText(fullPath);

                    if (json.Contains("\"Changes\""))
                    {
                        var cpWrapper = Newtonsoft.Json.JsonConvert.DeserializeObject<CpContentPatcherWrapper>(json);
                        var targetEntry = cpWrapper?.Changes?.FirstOrDefault()?.Entries;
                        if (targetEntry != null) return targetEntry;
                    }
                    else
                    {
                        var direct = Newtonsoft.Json.JsonConvert.DeserializeObject<GameSummary>(json);
                        if (direct?.Locations?.Entries != null || direct?.Festivals?.Entries != null) return direct;
                    }
                }
            }
        }
        catch (Exception ex)
        {
            ModEntry.SMonitor?.Log($"[LocationFestival] 从物理磁盘读取 GameSummary.json 出错: {ex.Message}", LogLevel.Error);
        }

        return null;
    }

    private static string ResolveI18nToken(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        const string prefix = "{{i18n:";
        const string suffix = "}}";
        if (text.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && text.EndsWith(suffix))
        {
            string key = text.Substring(prefix.Length, text.Length - prefix.Length - suffix.Length);
            var translation = ModEntry.SHelper.Translation.Get(key);
            if (translation.HasValue()) return translation.ToString();
        }
        return text;
    }

    private void RebuildLocationView()
    {
        _allLocations.Clear();
        var overlay = ModEntry.WorldSummaryOverlay?.LoadOrNull();

        foreach (var (id, name) in _baselineLocations)
        {
            if (IsExcludedLocation(null, id) || IsInvalidDisplayName(name)) continue;

            string desc = _baselineLocDescriptions.GetValueOrDefault(id, "");
            bool hasCustom = false;

            if (overlay?.LocationDescriptions != null && overlay.LocationDescriptions.TryGetValue(id, out var customDesc))
            {
                if (!string.IsNullOrWhiteSpace(customDesc))
                {
                    desc = customDesc;
                    hasCustom = true;
                }
            }

            _allLocations.Add(new LocationEntry
            {
                Id = id,
                Name = name,
                Region = _baselineRegions.GetValueOrDefault(id, "Pelican Town"),
                CustomDescription = desc,
                HasCustomOverlay = hasCustom
            });
        }

        _allLocations.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
        FilterCurrentList();
    }

    private void RebuildFestivalView()
    {
        _allFestivals.Clear();
        var overlay = ModEntry.WorldSummaryOverlay?.LoadOrNull();
        var handledKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. 载入自创节日与覆盖层
        if (overlay?.Festivals != null)
        {
            foreach (var (key, custom) in overlay.Festivals)
            {
                handledKeys.Add(key);
                bool isBaseline = _baselineFestivals.ContainsKey(key);
                string dispName = custom.Names.TryGetValue(LangKey, out var n) ? n : (isBaseline ? _baselineFestivals[key] : key);

                var descs = new Dictionary<string, string>(custom.Descriptions);
                if (!descs.ContainsKey(LangKey) || string.IsNullOrWhiteSpace(descs[LangKey]))
                {
                    if (isBaseline && _baselineFestDescriptions.TryGetValue(key, out var baseDesc))
                        descs[LangKey] = baseDesc;
                }

                _allFestivals.Add(new FestivalEntry
                {
                    Key = key,
                    Name = dispName,
                    Season = ExtractSeason(key),
                    Day = ExtractDay(key),
                    IsBaseline = isBaseline,
                    IsCustom = !isBaseline,
                    Names = new Dictionary<string, string>(custom.Names),
                    Descriptions = descs
                });
            }
        }

        // 2. 载入官方与 Mod 基线节日
        foreach (var (key, name) in _baselineFestivals)
        {
            if (handledKeys.Contains(key)) continue;

            var descs = new Dictionary<string, string>();
            if (_baselineFestDescriptions.TryGetValue(key, out var baseDesc))
            {
                descs["zh"] = baseDesc;
                descs["en"] = baseDesc;
            }

            _allFestivals.Add(new FestivalEntry
            {
                Key = key,
                Name = name,
                Season = ExtractSeason(key),
                Day = ExtractDay(key),
                IsBaseline = true,
                IsCustom = false,
                Descriptions = descs
            });
        }

        // 3. 自创置顶，原版次之（按历法日期排序）
        _allFestivals.Sort((a, b) =>
        {
            int rankA = a.IsCustom ? 0 : 1;
            int rankB = b.IsCustom ? 0 : 1;
            if (rankA != rankB) return rankA.CompareTo(rankB);
            return CompareFestivalDate(a, b);
        });

        FilterCurrentList();
    }

    private static int GetSeasonOrder(string season) => season.ToLowerInvariant() switch
    {
        "spring" => 0,
        "summer" => 1,
        "fall" => 2,
        "winter" => 3,
        _ => 0
    };

    private static int CompareFestivalDate(FestivalEntry a, FestivalEntry b)
    {
        int sA = GetSeasonOrder(a.Season);
        int sB = GetSeasonOrder(b.Season);
        if (sA != sB) return sA.CompareTo(sB);
        if (a.Day != b.Day) return a.Day.CompareTo(b.Day);
        return string.Compare(a.Key, b.Key, StringComparison.OrdinalIgnoreCase);
    }

    private void FilterCurrentList()
    {
        string q = _searchBox.Text.Trim();

        if (_currentMode == ViewMode.Locations)
        {
            _filteredLocations.Clear();
            if (string.IsNullOrEmpty(q))
            {
                _filteredLocations.AddRange(_allLocations);
            }
            else
            {
                foreach (var loc in _allLocations)
                {
                    if (loc.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                        loc.Region.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                        loc.Id.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                        loc.CustomDescription.Contains(q, StringComparison.OrdinalIgnoreCase))
                    {
                        _filteredLocations.Add(loc);
                    }
                }
            }
            int visible = Math.Max(1, (_listRect.Bottom - _searchBoxRect.Bottom - 9) / (RowHeight + RowGap));
            _locScroll = Math.Clamp(_locScroll, 0, Math.Max(0, _filteredLocations.Count - visible));
        }
        else
        {
            _filteredFestivals.Clear();
            if (string.IsNullOrEmpty(q))
            {
                _filteredFestivals.AddRange(_allFestivals);
            }
            else
            {
                foreach (var fest in _allFestivals)
                {
                    if (fest.Name.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                        fest.Key.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                        fest.Descriptions.Values.Any(d => d.Contains(q, StringComparison.OrdinalIgnoreCase)))
                    {
                        _filteredFestivals.Add(fest);
                    }
                }
            }
            int visible = Math.Max(1, (_listRect.Bottom - _searchBoxRect.Bottom - 9) / (RowHeight + RowGap));
            _festScroll = Math.Clamp(_festScroll, 0, Math.Max(0, _filteredFestivals.Count - visible));
        }
    }

    private void SelectLocation(string id)
    {
        _selectedLocId = id;
        _statusMessage = null;
        var loc = _allLocations.FirstOrDefault(l => string.Equals(l.Id, id, StringComparison.OrdinalIgnoreCase));
        if (loc != null)
        {
            _descBox.SetText(loc.CustomDescription);
        }
        DeselectAllBoxes();
        RefreshFormSnapshot();
    }

    private void SelectFestival(string key)
    {
        _isCreatingNewFest = false;
        _selectedFestKey = key;
        _statusMessage = null;
        _isSeasonDropdownOpen = false;

        var fest = _allFestivals.FirstOrDefault(f => string.Equals(f.Key, key, StringComparison.OrdinalIgnoreCase));
        if (fest != null)
        {
            _festNameBox.Text = fest.Names.TryGetValue(LangKey, out var n) ? n : fest.Name;
            _descBox.SetText(fest.Descriptions.TryGetValue(LangKey, out var d) ? d : "");
            _dayStepper.Value = fest.Day;
            _selectedSeason = fest.Season;
        }
        DeselectAllBoxes();
        RefreshFormSnapshot();
    }

    // ── 业务保存与二次确认 ──

    private void SaveLocation()
    {
        if (string.IsNullOrEmpty(_selectedLocId)) return;
        var service = ModEntry.WorldSummaryOverlay;
        if (service == null) return;

        var ov = service.LoadOrNull() ?? new WorldSummaryOverlayFile();
        string text = _descBox.Text.Trim();
        ov.LocationDescriptions[_selectedLocId] = text.Length > 200 ? text.Substring(0, 200) : text;
        ov.RemovedLocationDescriptionIds.Remove(_selectedLocId);

        if (service.Save(ov, out _))
        {
            string locId = _selectedLocId;
            RebuildLocationView();
            SelectLocation(locId);
            _statusMessage = "✔ 地点环境已保存生效";
            Game1.playSound("coin");
            Hub.RefreshEntries();
        }
    }

    private void RequestRevertLocation()
    {
        if (string.IsNullOrEmpty(_selectedLocId)) return;
        var loc = _allLocations.FirstOrDefault(l => string.Equals(l.Id, _selectedLocId, StringComparison.OrdinalIgnoreCase));
        if (loc == null || !loc.HasCustomOverlay) return;

        Game1.activeClickableMenu = new BioValveWarningDialog(
            Hub,
            "恢复原版环境描述",
            $"即将清除【{loc.Name}】的自定义描述：",
            new List<string>
            {
                "该地点的描述将恢复为原版默认文本",
                "自定义内容删除后无法找回"
            },
            "⚠ 确认恢复",
            () =>
            {
                Game1.activeClickableMenu = Hub;
                var service = ModEntry.WorldSummaryOverlay;
                if (service == null) return;

                var ov = service.LoadOrNull() ?? new WorldSummaryOverlayFile();
                ov.LocationDescriptions.Remove(_selectedLocId);
                ov.RemovedLocationDescriptionIds.Add(_selectedLocId);

                if (service.Save(ov, out string? _))
                {
                    string locId = _selectedLocId;
                    RebuildLocationView();
                    SelectLocation(locId);
                    _statusMessage = "✔ 已恢复原版环境设定";
                    Game1.playSound("coin");
                    Hub.RefreshEntries();
                }
            },
            "保持现状",
            () => Game1.activeClickableMenu = Hub);
    }

    private FestivalEntry? GetConflictingFestival(string season, int day)
    {
        return _allFestivals.FirstOrDefault(f =>
            f.Season.Equals(season, StringComparison.OrdinalIgnoreCase) &&
            f.Day == day &&
            (_isCreatingNewFest || !string.Equals(f.Key, _selectedFestKey, StringComparison.OrdinalIgnoreCase))
        );
    }

    private void SaveFestival()
    {
        if (string.IsNullOrWhiteSpace(_festNameBox.Text)) return;
        var service = ModEntry.WorldSummaryOverlay;
        if (service == null) return;

        var conflictFest = GetConflictingFestival(_selectedSeason, _dayStepper.Value);
        if (conflictFest != null)
        {
            Game1.playSound("cancel");
            _statusMessage = $"⚠ 同一天只能允许有一个节日！（已有：{conflictFest.Name}）";
            Game1.addHUDMessage(new HUDMessage($"同一天只能允许有一个节日（已有：{conflictFest.Name}）", HUDMessage.error_type));
            return;
        }

        var ov = service.LoadOrNull() ?? new WorldSummaryOverlayFile();

        string season = _selectedSeason;
        int day = _dayStepper.Value;
        string key = FestivalKeyBuilder.BuildFestivalKey(season, day);

        if (!_isCreatingNewFest && !string.IsNullOrEmpty(_selectedFestKey) && !string.Equals(_selectedFestKey, key, StringComparison.OrdinalIgnoreCase))
        {
            var oldFest = _allFestivals.FirstOrDefault(f => string.Equals(f.Key, _selectedFestKey, StringComparison.OrdinalIgnoreCase));
            if (oldFest != null && oldFest.IsCustom)
            {
                ov.Festivals.Remove(_selectedFestKey);
            }
        }

        CustomFestivalEntry entry;
        if (ov.Festivals.TryGetValue(key, out var existing) && existing != null)
            entry = existing;
        else
        {
            entry = new CustomFestivalEntry();
            ov.Festivals[key] = entry;
        }

        entry.Names ??= new Dictionary<string, string>();
        entry.Descriptions ??= new Dictionary<string, string>();

        string name = _festNameBox.Text.Trim();
        string desc = _descBox.Text.Trim();

        entry.Names[LangKey] = name;
        entry.Descriptions[LangKey] = desc.Length > 200 ? desc.Substring(0, 200) : desc;
        entry.IsCustomDate = !_baselineFestivals.ContainsKey(key);

        string otherLang = IsZh ? "en" : "zh";
        if (!entry.Names.ContainsKey(otherLang) || string.IsNullOrWhiteSpace(entry.Names[otherLang]))
            entry.Names[otherLang] = name;
        if (!entry.Descriptions.ContainsKey(otherLang) || string.IsNullOrWhiteSpace(entry.Descriptions[otherLang]))
            entry.Descriptions[otherLang] = desc;

        if (ov.RemovedFestivalKeys != null)
        {
            while (ov.RemovedFestivalKeys.Any(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase)))
            {
                var rk = ov.RemovedFestivalKeys.First(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase));
                ov.RemovedFestivalKeys.Remove(rk);
            }
        }

        if (service.Save(ov, out _))
        {
            _isCreatingNewFest = false;
            RebuildFestivalView();
            SelectFestival(key);
            _statusMessage = "✔ 节日设定已保存";
            Game1.playSound("coin");
            Hub.RefreshEntries();
        }
    }

    private void RequestRevertFestival()
    {
        if (string.IsNullOrEmpty(_selectedFestKey)) return;
        var fest = _allFestivals.FirstOrDefault(f => string.Equals(f.Key, _selectedFestKey, StringComparison.OrdinalIgnoreCase));
        if (fest == null || !fest.IsBaseline) return;

        Game1.activeClickableMenu = new BioValveWarningDialog(
            Hub,
            "还原官方节日配置",
            $"即将把【{fest.Name}】还原为原版默认：",
            new List<string>
            {
                "该节日的自定义配置将被清除，恢复官方默认"
            },
            "⚠ 确认还原",
            () =>
            {
                Game1.activeClickableMenu = Hub;
                var service = ModEntry.WorldSummaryOverlay;
                if (service == null) return;

                var ov = service.LoadOrNull() ?? new WorldSummaryOverlayFile();
                ov.Festivals.Remove(_selectedFestKey);

                if (ov.RemovedFestivalKeys != null)
                {
                    while (ov.RemovedFestivalKeys.Any(k => string.Equals(k, _selectedFestKey, StringComparison.OrdinalIgnoreCase)))
                    {
                        var rk = ov.RemovedFestivalKeys.First(k => string.Equals(k, _selectedFestKey, StringComparison.OrdinalIgnoreCase));
                        ov.RemovedFestivalKeys.Remove(rk);
                    }
                }

                if (service.Save(ov, out string? _))
                {
                    string targetKey = _selectedFestKey;
                    RebuildFestivalView();
                    SelectFestival(targetKey);
                    _statusMessage = "✔ 已还原为官方节日";
                    Game1.playSound("coin");
                    Hub.RefreshEntries();
                }
            },
            "保持现状",
            () => Game1.activeClickableMenu = Hub);
    }

    private void RequestDeleteFestival()
    {
        if (string.IsNullOrEmpty(_selectedFestKey)) return;
        var fest = _allFestivals.FirstOrDefault(f => string.Equals(f.Key, _selectedFestKey, StringComparison.OrdinalIgnoreCase));
        if (fest == null || !fest.IsCustom) return;

        string targetKey = _selectedFestKey;

        Game1.activeClickableMenu = new BioValveWarningDialog(
            Hub,
            IsZh ? "删除自创节日" : "Delete Custom Festival",
            IsZh ? $"即将彻底删除自创节日【{fest.Name}】：" : $"About to permanently delete custom festival '{fest.Name}':",
            IsZh
                ? new List<string> { "该节日的全部自定义配置将被移除，无法找回" }
                : new List<string> { "All custom configuration for this festival will be removed and cannot be recovered" },
            IsZh ? "⚠ 确认删除" : "⚠ Delete",
            () =>
            {
                Game1.activeClickableMenu = Hub;
                var service = ModEntry.WorldSummaryOverlay;
                if (service == null) return;

                var ov = service.LoadOrNull() ?? new WorldSummaryOverlayFile();
                ov.Festivals.Remove(targetKey);

                if (ov.RemovedFestivalKeys != null)
                {
                    while (ov.RemovedFestivalKeys.Any(k => string.Equals(k, targetKey, StringComparison.OrdinalIgnoreCase)))
                    {
                        var rk = ov.RemovedFestivalKeys.First(k => string.Equals(k, targetKey, StringComparison.OrdinalIgnoreCase));
                        ov.RemovedFestivalKeys.Remove(rk);
                    }
                }

                if (service.Save(ov, out string? _))
                {
                    RebuildFestivalView();

                    if (_filteredFestivals.Count > 0)
                        SelectFestival(_filteredFestivals[0].Key);
                    else
                        _selectedFestKey = null;

                    _statusMessage = IsZh ? "✔ 已彻底删除自创节日" : "✔ Custom festival deleted";
                    Game1.playSound("trashcan");
                    Hub.RefreshEntries();
                }
            },
            IsZh ? "保留节日" : "Keep Festival",
            () => Game1.activeClickableMenu = Hub);
    }

    private (string Season, int Day) FindFirstAvailableDate()
    {
        string[] seasons = { "spring", "summer", "fall", "winter" };
        foreach (var s in seasons)
        {
            for (int d = 1; d <= 28; d++)
            {
                if (!_allFestivals.Any(f => f.Season.Equals(s, StringComparison.OrdinalIgnoreCase) && f.Day == d))
                {
                    return (s, d);
                }
            }
        }
        return ("spring", 1);
    }

    private void CreateNewFestival()
    {
        _isCreatingNewFest = true;
        _selectedFestKey = null;
        _festNameBox.Text = "新自创纪念日";
        _descBox.SetText("小镇全体居民共同庆祝这一天。");

        var (freeSeason, freeDay) = FindFirstAvailableDate();
        _selectedSeason = freeSeason;
        _dayStepper.Value = freeDay;

        _isSeasonDropdownOpen = false;
        DeselectAllBoxes();
        _statusMessage = "已开启自创节日模式，请设定日期并保存。";
        RefreshFormSnapshot();
    }

    // ── 辅助解析方法 ──

    private static string ExtractSeason(string key)
    {
        foreach (var s in new[] { "spring", "summer", "fall", "winter" })
            if (key.StartsWith(s, StringComparison.OrdinalIgnoreCase)) return s;
        return "spring";
    }

    private static int ExtractDay(string key)
    {
        string digits = new string(key.SkipWhile(c => !char.IsDigit(c)).TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(digits, out int d) ? Math.Clamp(d, 1, 28) : 1;
    }

    private static string GetSeasonShortZh(string season) => season.ToLower() switch
    {
        "spring" => "春",
        "summer" => "夏",
        "fall" => "秋",
        "winter" => "冬",
        _ => "春"
    };

    // ── Content Patcher 格式反序列化辅助类 ──

    private sealed class CpContentPatcherWrapper
    {
        public List<CpChangeItem>? Changes { get; set; }
    }

    private sealed class CpChangeItem
    {
        public GameSummary? Entries { get; set; }
    }
}