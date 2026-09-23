#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;
using ValleytalkReborn.Services;
using ValleytalkReborn.Services.Overlays;

namespace ValleytalkReborn.UI;

/// <summary>
/// 兴趣点勘测与出没偏好调谐子页：
/// 1. 深度融合 RulesTheme 语义化配色（暖羊皮纸背景、暗焦褐文字、金棕边框与九宫格外框）；
/// 2. 模式排布：伴侣出没偏好（默认首选） / 兴趣点勘测与建档；
/// 3. 自创兴趣点绝对置顶，别名独立显示；
/// 4. 传送测试时安全激活悬浮挂件，完备处理二次传送覆盖与原地唤醒边界。
/// </summary>
internal sealed class PoiTuningPage : WorldSubPageBase
{
    private enum ViewMode { NpcWeights, PoiCatalog }
    private ViewMode _currentMode = ViewMode.NpcWeights;

    private const int RowHeight = 40;
    private const int RowGap = 4;
    private const int ModeHeaderHeight = 34;
    private const int DescMaxChars = 200;
    private const int DefaultWeight = 50;

    private static readonly Color DarkGrayText = new(80, 80, 80);

    // ── 模式切换标签 ──
    private Rectangle _modeNpcBtnRect;
    private Rectangle _modePoiBtnRect;

    // ── 布局区域 ──
    private Rectangle _listRect;
    private Rectangle _formRect;

    // ── 搜索框与滚动条 ──
    private readonly TextBox _searchBox;
    private Rectangle _searchBoxRect;
    private string _lastSearchQuery = "";
    private bool _isDraggingLeftScrollbar = false;

    // ── 基线缓存 ──
    private readonly Dictionary<string, PoiAsset> _baselinePois = new(StringComparer.OrdinalIgnoreCase);

    // ── 1. 合并 POI 目录数据 ──
    private sealed class PoiListEntry
    {
        public string Id = "";
        public string Alias = "";
        public string MapName = "";
        public int TileX;
        public int TileY;
        public string SummaryZh = "";
        public string SummaryEn = "";
        public bool IsCustom;
        public bool HasCustomOverlay;
    }
    private readonly List<PoiListEntry> _allPois = new();
    private readonly List<PoiListEntry> _filteredPois = new();
    private int _poiListScroll;
    private string? _selectedPoiId;
    private bool _isCreatingNewPoi = false;

    // ── 2. 已婚伴侣候选集数据 ──
    private sealed class SpouseItem
    {
        public string Id = "";
        public string DisplayName = "";
        public bool HasCustomWeights;
    }
    private readonly List<SpouseItem> _allSpouses = new();
    private readonly List<SpouseItem> _filteredSpouses = new();
    private int _spouseListScroll;
    private string? _selectedSpouseId;
    private static readonly Dictionary<string, (Texture2D? Texture, Rectangle SourceRect)> _avatarCache = new(StringComparer.OrdinalIgnoreCase);

    // ── 现场踩点回弹上下文 ──
    private static string? _pendingReturnPoiId;
    private static string? _pendingReturnMap;
    private static int? _pendingReturnX;
    private static int? _pendingReturnY;

    public static void SetPendingContext(string poiId, string mapName, int x, int y)
    {
        _pendingReturnPoiId = poiId;
        _pendingReturnMap = mapName;
        _pendingReturnX = x;
        _pendingReturnY = y;
    }

    // ── 权重表状态机 ──
    private readonly Dictionary<string, NumberStepper> _weightSteppers = new(StringComparer.OrdinalIgnoreCase);
    private int _weightTableScroll;
    private bool _isDraggingWeightScrollbar = false;
    private readonly List<(Rectangle RowRect, PoiListEntry Entry, NumberStepper Stepper)> _visibleWeightRows = new();
    private Rectangle _weightTableArea;

    // ── 勘测、别名与描写编辑控件 ──
    private string? _captureMapName;
    private int _captureTileX;
    private int _captureTileY;
    private bool _hasCapture;

    private string? _pendingMapName;
    private int _pendingTileX;
    private int _pendingTileY;
    private bool _hasPendingCoordChange;

    private readonly TextBox _aliasBox;
    private Rectangle _aliasBoxRect;
    private readonly DialogueTextInputBox _descBox;

    // ── 操作按钮 ──
    private Rectangle _btnTeleport;
    private Rectangle _btnCapture;
    private Rectangle _btnSave;
    private Rectangle _btnRevert;
    private Rectangle _btnDelete;
    private Rectangle _btnNewPoi;

    // ── 别名数据本地持久化 ──
    private static Dictionary<string, string> _poiAliases = new(StringComparer.OrdinalIgnoreCase);
    private static string AliasesFilePath => Path.Combine(Constants.SavesPath, "_ValleyTalkReborn_Global", "poi_aliases.json");

    private string? _statusMessage;
    private string? _errorMessage;

    private static bool IsWorldReady => Context.IsWorldReady && Game1.currentLocation != null && Game1.player != null;
    private static bool IsZh => LocalizedContentManager.CurrentLanguageCode == LocalizedContentManager.LanguageCode.zh;

    public PoiTuningPage(IntegratedHubMenu hub) : base(hub)
    {
        Texture2D boxTex = Game1.content.Load<Texture2D>("LooseSprites\\textBox") ?? Game1.mouseCursors;
        _searchBox = new TextBox(boxTex, null, Game1.smallFont, DarkGrayText);
        // 关闭原版 TextBox 的像素宽度截断（Text setter 内置递归截断会静默损毁程序化赋值的长文本）
        _searchBox.limitWidth = false;
        _aliasBox = new TextBox(boxTex, null, Game1.smallFont, RulesTheme.TextCharcoal);
        // 关闭原版 TextBox 的像素宽度截断（Text setter 内置递归截断会静默损毁程序化赋值的长文本）
        _aliasBox.limitWidth = false;

        _descBox = new DialogueTextInputBox(DescMaxChars)
        {
            UseCustomFont = true,
            CustomFontSize = CustomFontManager.SizeRegular,
            TextColor = RulesTheme.TextPrimary,
            ShowCharacterCount = true,
            DrawFrame = true
        };

        LoadPoiAliases();
    }

    public override void OnShown()
    {
        LoadPoiAliases();
        BuildBaselineCache();
        BuildMarriedSpouses();
        RebuildMergedPoiView();
        SelectInitialSpouse();
        RefreshCaptureState();

        // 优先处理现场踩点回弹
        if (!string.IsNullOrEmpty(_pendingReturnPoiId))
        {
            _currentMode = ViewMode.PoiCatalog;
            SelectPoi(_pendingReturnPoiId);

            if (_pendingReturnMap != null && _pendingReturnX.HasValue && _pendingReturnY.HasValue)
            {
                _pendingMapName = _pendingReturnMap;
                _pendingTileX = _pendingReturnX.Value;
                _pendingTileY = _pendingReturnY.Value;
                _hasPendingCoordChange = true;
                _statusMessage = $"✔ 已应用实地抓取坐标: {GetLocalizedMapName(_pendingMapName)} ({_pendingTileX}, {_pendingTileY})，点击保存落盘";
            }

            _pendingReturnPoiId = null;
            _pendingReturnMap = null;
            _pendingReturnX = null;
            _pendingReturnY = null;
            LayoutRightForm();
            return;
        }

        if (_allPois.Count > 0 && _selectedPoiId == null && !_isCreatingNewPoi)
            SelectPoi(_allPois[0].Id);
    }

    public override void Layout(Rectangle area)
    {
        int tabW = 180;
        int tabH = 30;
        _modeNpcBtnRect = new Rectangle(area.X, area.Y, tabW, tabH);
        _modePoiBtnRect = new Rectangle(area.X + tabW + 8, area.Y, tabW, tabH);

        int contentY = area.Y + ModeHeaderHeight + 4;
        int contentH = area.Bottom - contentY;

        int listWidth = (int)(area.Width * 0.36f);
        _listRect = new Rectangle(area.X, contentY, listWidth, contentH);

        int formX = _listRect.Right + 12;
        int formW = area.Right - formX;
        _formRect = new Rectangle(formX, contentY, formW, contentH);

        _searchBoxRect = new Rectangle(_listRect.X + 6, _listRect.Y + 6, _listRect.Width - 12, 30);
        _searchBox.X = _searchBoxRect.X;
        _searchBox.Y = _searchBoxRect.Y;
        _searchBox.Width = _searchBoxRect.Width;
        _searchBox.Height = _searchBoxRect.Height;

        LayoutRightForm();
    }

    private void LayoutRightForm()
    {
        int fx = _formRect.X + 12;
        int fw = _formRect.Width - 24;

        if (_currentMode == ViewMode.NpcWeights)
        {
            int btnH = 34;
            int btnW = (fw - 12) / 2;
            int btnY = _formRect.Bottom - btnH - 6;

            _btnSave = new Rectangle(fx, btnY, btnW, btnH);
            _btnRevert = new Rectangle(fx + btnW + 12, btnY, btnW, btnH);

            int tableTop = _formRect.Y + 74;
            int tableH = btnY - tableTop - 12;
            _weightTableArea = new Rectangle(fx, tableTop, fw, tableH);
        }
        else
        {
            int btnY = _formRect.Bottom - 36;
            int btnW = (fw - 18) / 4;
            _btnSave = new Rectangle(fx, btnY, btnW, 32);
            _btnRevert = new Rectangle(fx + btnW + 6, btnY, btnW, 32);
            _btnDelete = new Rectangle(fx + (btnW + 6) * 2, btnY, btnW, 32);
            _btnNewPoi = new Rectangle(fx + (btnW + 6) * 3, btnY, btnW, 32);

            int topCardH = 92;
            int cardBtnW = (fw - 24 - 10) / 2;
            int cardBtnY = _formRect.Y + 10 + 54;
            _btnTeleport = new Rectangle(fx + 12, cardBtnY, cardBtnW, 28);
            _btnCapture = new Rectangle(fx + 12 + cardBtnW + 10, cardBtnY, cardBtnW, 28);

            int aliasTop = _formRect.Y + 10 + topCardH + 10;
            _aliasBoxRect = new Rectangle(fx, aliasTop + 20, fw, 30);
            _aliasBox.X = _aliasBoxRect.X;
            _aliasBox.Y = _aliasBoxRect.Y;
            _aliasBox.Width = _aliasBoxRect.Width;
            _aliasBox.Height = _aliasBoxRect.Height;

            int descTop = _aliasBoxRect.Bottom + 26;
            int descBottom = _btnSave.Y - 26;
            int descH = Math.Max(90, descBottom - descTop);

            _descBox.Position = new Vector2(fx, descTop);
            _descBox.Extent = new Vector2(fw, descH);
            _descBox.InvalidateLayout();
        }
    }

    public override void Update(GameTime time)
    {
        if (_currentMode == ViewMode.PoiCatalog)
            _descBox.Update(time);

        if (_searchBox.Text != _lastSearchQuery)
        {
            _lastSearchQuery = _searchBox.Text;
            FilterCurrentList();
        }
    }

    public override bool ReceiveLeftClick(int x, int y)
    {
        if (_modeNpcBtnRect.Contains(x, y) && _currentMode != ViewMode.NpcWeights)
        {
            _currentMode = ViewMode.NpcWeights;
            _isCreatingNewPoi = false;
            _searchBox.Text = "";
            FilterCurrentList();
            DeselectAllBoxes();
            Game1.playSound("smallSelect");
            LayoutRightForm();
            RefreshFormSnapshot();
            return true;
        }
        if (_modePoiBtnRect.Contains(x, y) && _currentMode != ViewMode.PoiCatalog)
        {
            _currentMode = ViewMode.PoiCatalog;
            _isCreatingNewPoi = false;
            _searchBox.Text = "";
            FilterCurrentList();
            DeselectAllBoxes();
            if (_filteredPois.Count > 0 && _selectedPoiId == null)
                SelectPoi(_filteredPois[0].Id);

            Game1.playSound("smallSelect");
            LayoutRightForm();
            RefreshFormSnapshot();
            return true;
        }

        if (_searchBoxRect.Contains(x, y))
        {
            _searchBox.SelectMe();
            Game1.keyboardDispatcher.Subscriber = _searchBox;
            DeselectAllBoxes(keepSearch: true);
            return true;
        }

        int listTop = _searchBoxRect.Bottom + 5;
        int listH = _listRect.Bottom - 4 - listTop;
        int visibleCount = listH / (RowHeight + RowGap);
        int totalCount = _currentMode == ViewMode.NpcWeights ? _filteredSpouses.Count : _filteredPois.Count;
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
                UpdateLeftScrollFromMouse(y, trackRect, thumbH, maxScroll);
                return true;
            }
        }

        if (_listRect.Contains(x, y) && y >= listTop)
        {
            int clickIdx = (y - listTop) / (RowHeight + RowGap);
            int scroll = _currentMode == ViewMode.NpcWeights ? _spouseListScroll : _poiListScroll;
            int actualIdx = scroll + clickIdx;

            if (actualIdx >= 0 && actualIdx < totalCount)
            {
                if (_currentMode == ViewMode.NpcWeights)
                    SelectSpouse(_filteredSpouses[actualIdx].Id);
                else
                    SelectPoi(_filteredPois[actualIdx].Id);

                Game1.playSound("smallSelect");
                return true;
            }
            return true;
        }

        if (_currentMode == ViewMode.NpcWeights)
        {
            foreach (var (_, _, stepper) in _visibleWeightRows)
            {
                if (stepper.ReceiveLeftClick(x, y))
                    return true;
            }

            int rowH = 34;
            int rowGap = 4;
            int maxVisibleRows = Math.Max(1, _weightTableArea.Height / (rowH + rowGap));
            if (_allPois.Count > maxVisibleRows)
            {
                var weightTrackRect = new Rectangle(_weightTableArea.Right - 8, _weightTableArea.Y + 2, 5, _weightTableArea.Height - 4);
                if (weightTrackRect.Contains(x, y))
                {
                    _isDraggingWeightScrollbar = true;
                    int maxScroll = _allPois.Count - maxVisibleRows;
                    float ratio = Math.Clamp((float)maxVisibleRows / _allPois.Count, 0.15f, 1f);
                    int thumbH = Math.Max(24, (int)(weightTrackRect.Height * ratio));
                    UpdateWeightScrollFromMouse(y, weightTrackRect, thumbH, maxScroll);
                    return true;
                }
            }

            if (_btnSave.Contains(x, y)) { SaveWeights(); return true; }
            if (_btnRevert.Contains(x, y) && _selectedSpouseId != null) { RequestRevertSpouseWeights(); return true; }
        }
        else
        {
            if (_aliasBoxRect.Contains(x, y))
            {
                _aliasBox.SelectMe();
                Game1.keyboardDispatcher.Subscriber = _aliasBox;
                _descBox.Selected = false;
                _searchBox.Selected = false;
                return true;
            }

            if (_descBox.ContainsPoint(x, y))
            {
                _descBox.Selected = true;
                _aliasBox.Selected = false;
                _searchBox.Selected = false;
                Game1.keyboardDispatcher.Subscriber = _descBox;
                return true;
            }

            if (_btnTeleport.Contains(x, y) && CanTeleport) { TeleportToSelected(); return true; }
            if (_btnCapture.Contains(x, y) && IsWorldReady) { CaptureCurrentTile(); return true; }

            if (_btnSave.Contains(x, y) && CanSaveCurrentPoi) { SavePoi(); return true; }
            if (_btnRevert.Contains(x, y) && CanRevertSelectedPoi) { RequestRevertSelectedPoi(); return true; }
            if (_btnDelete.Contains(x, y))
            {
                if (_isCreatingNewPoi)
                {
                    _isCreatingNewPoi = false;
                    if (_allPois.Count > 0) SelectPoi(_allPois[0].Id);
                    Game1.playSound("smallSelect");
                    LayoutRightForm();
                    return true;
                }
                if (CanDeleteSelectedPoi) { RequestDeleteSelectedPoi(); return true; }
            }
            if (_btnNewPoi.Contains(x, y)) { StartCreateNewPoi(); return true; }
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
            int totalCount = _currentMode == ViewMode.NpcWeights ? _filteredSpouses.Count : _filteredPois.Count;
            int maxScroll = Math.Max(0, totalCount - visibleCount);

            var trackRect = new Rectangle(_listRect.Right - 9, listTop, 5, listH);
            float ratio = Math.Clamp((float)visibleCount / totalCount, 0.15f, 1f);
            int thumbH = Math.Max(24, (int)(trackRect.Height * ratio));
            UpdateLeftScrollFromMouse(y, trackRect, thumbH, maxScroll);
        }
        else if (_isDraggingWeightScrollbar && _currentMode == ViewMode.NpcWeights)
        {
            int rowH = 34;
            int rowGap = 4;
            int maxVisibleRows = Math.Max(1, _weightTableArea.Height / (rowH + rowGap));
            int maxScroll = Math.Max(0, _allPois.Count - maxVisibleRows);

            var trackRect = new Rectangle(_weightTableArea.Right - 8, _weightTableArea.Y + 2, 5, _weightTableArea.Height - 4);
            float ratio = Math.Clamp((float)maxVisibleRows / _allPois.Count, 0.15f, 1f);
            int thumbH = Math.Max(24, (int)(trackRect.Height * ratio));
            UpdateWeightScrollFromMouse(y, trackRect, thumbH, maxScroll);
        }
    }

    public override void ReleaseLeftClick(int x, int y)
    {
        _isDraggingLeftScrollbar = false;
        _isDraggingWeightScrollbar = false;
    }

    private void UpdateLeftScrollFromMouse(int mouseY, Rectangle trackRect, int thumbH, int maxScroll)
    {
        if (maxScroll <= 0 || trackRect.Height <= thumbH) return;
        float progress = Math.Clamp((float)(mouseY - trackRect.Y - thumbH / 2) / (trackRect.Height - thumbH), 0f, 1f);
        int newOffset = (int)Math.Round(progress * maxScroll);
        if (_currentMode == ViewMode.NpcWeights) _spouseListScroll = newOffset;
        else _poiListScroll = newOffset;
    }

    private void UpdateWeightScrollFromMouse(int mouseY, Rectangle trackRect, int thumbH, int maxScroll)
    {
        if (maxScroll <= 0 || trackRect.Height <= thumbH) return;
        float progress = Math.Clamp((float)(mouseY - trackRect.Y - thumbH / 2) / (trackRect.Height - thumbH), 0f, 1f);
        _weightTableScroll = (int)Math.Round(progress * maxScroll);
    }

    public override bool ReceiveScrollWheel(int direction)
    {
        int mx = Game1.getMouseX(), my = Game1.getMouseY();

        if (_listRect.Contains(mx, my))
        {
            int listTop = _searchBoxRect.Bottom + 5;
            int visible = (_listRect.Bottom - 4 - listTop) / (RowHeight + RowGap);
            if (_currentMode == ViewMode.NpcWeights)
                _spouseListScroll = Math.Clamp(_spouseListScroll - (direction > 0 ? 1 : -1), 0, Math.Max(0, _filteredSpouses.Count - visible));
            else
                _poiListScroll = Math.Clamp(_poiListScroll - (direction > 0 ? 1 : -1), 0, Math.Max(0, _filteredPois.Count - visible));
            return true;
        }

        if (_currentMode == ViewMode.NpcWeights && _weightTableArea.Contains(mx, my))
        {
            int rowH = 34;
            int rowGap = 4;
            int visible = Math.Max(1, _weightTableArea.Height / (rowH + rowGap));
            _weightTableScroll = Math.Clamp(_weightTableScroll - (direction > 0 ? 1 : -1), 0, Math.Max(0, _allPois.Count - visible));
            return true;
        }

        if (_currentMode == ViewMode.PoiCatalog && _descBox.ContainsPoint(mx, my))
        {
            _descBox.ReceiveScrollWheel(direction);
            return true;
        }

        return false;
    }

    public override bool ReceiveKeyPress(Keys key)
    {
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

        if (_aliasBox.Selected)
        {
            if (key == Keys.Escape || key == Keys.Enter)
            {
                _aliasBox.Selected = false;
                if (Game1.keyboardDispatcher.Subscriber == _aliasBox) Game1.keyboardDispatcher.Subscriber = null;
                Game1.playSound("smallSelect");
                return true;
            }
            return true;
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
        _isDraggingWeightScrollbar = false;
        DeselectAllBoxes();

        // 离开本界面且无待回弹流程时，主动关闭现场挂件
        if (string.IsNullOrEmpty(_pendingReturnPoiId))
            PoiInspectHud.Close();
    }

    public override void ResetToBaseline()
    {
        var service = ModEntry.PoiPreferenceOverlay;
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

        _selectedPoiId = null;
        _isCreatingNewPoi = false;

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
        if (_currentMode == ViewMode.NpcWeights)
        {
            string fp = _selectedSpouseId ?? "";
            foreach (var kv in _weightSteppers)
                fp += "|" + kv.Key + ":" + kv.Value.Value;
            return fp;
        }

        if (_isCreatingNewPoi)
            return $"NEW|{_hasCapture}|{_captureMapName}|{_captureTileX}|{_captureTileY}|{_aliasBox.Text.Trim()}|{_descBox.Text.Trim()}";

        return $"{_selectedPoiId}|{_aliasBox.Text.Trim()}|{_descBox.Text.Trim()}|{_hasPendingCoordChange}|{_pendingMapName}|{_pendingTileX}|{_pendingTileY}";
    }

    private void RefreshFormSnapshot() => _formSnapshot = BuildFormFingerprint();

    public override bool HasUnsavedChanges => (_currentMode == ViewMode.NpcWeights
            ? _selectedSpouseId != null
            : (_isCreatingNewPoi || _selectedPoiId != null))
        && BuildFormFingerprint() != _formSnapshot;

    public override bool TryCommitUnsavedChanges()
    {
        if (!HasUnsavedChanges) return true;
        if (_currentMode == ViewMode.NpcWeights) SaveWeights();
        else SavePoi();
        return !HasUnsavedChanges;
    }

    private void DeselectAllBoxes(bool keepSearch = false)
    {
        _aliasBox.Selected = false;
        _descBox.Selected = false;
        if (!keepSearch) _searchBox.Selected = false;

        if (Game1.keyboardDispatcher.Subscriber == _descBox ||
            Game1.keyboardDispatcher.Subscriber == _aliasBox ||
            (!keepSearch && Game1.keyboardDispatcher.Subscriber == _searchBox))
        {
            Game1.keyboardDispatcher.Subscriber = null;
        }
    }

    // ── 渲染管线 ──

    public override void Draw(SpriteBatch b, Rectangle area, int mx, int my)
    {
        DrawModeSwitcher(b, mx, my);
        DrawLeftList(b, mx, my);

        if (_currentMode == ViewMode.NpcWeights)
            DrawSpouseWeightsForm(b, mx, my);
        else
            DrawPoiCatalogForm(b, mx, my);
    }

    private void DrawModeSwitcher(SpriteBatch b, int mx, int my)
    {
        DrawTabPill(b, _modeNpcBtnRect, "⚖️ 伴侣出没偏好调谐", _currentMode == ViewMode.NpcWeights, mx, my);
        DrawTabPill(b, _modePoiBtnRect, "📍 兴趣点勘测与建档", _currentMode == ViewMode.PoiCatalog, mx, my);
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
            b.Draw(Game1.staminaRect, new Rectangle(rect.X + 3, rect.Bottom - 3, rect.Width - 6, 2), RulesTheme.AccentGold);

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
            string ph = _currentMode == ViewMode.NpcWeights ? "🔍 搜索伴侣姓名..." : "🔍 搜索 POI 或地图...";
            CustomFontManager.DrawString(b, ph, new Vector2(_searchBox.X + 8, _searchBox.Y + 6), DarkGrayText, CustomFontManager.SizeSmall);
        }

        b.Draw(Game1.staminaRect,
            new Rectangle(_listRect.X + 6, _searchBoxRect.Bottom + 3, _listRect.Width - 12, 1),
            RulesTheme.BorderSoft * 0.8f);

        int listTop = _searchBoxRect.Bottom + 5;
        int listH = _listRect.Bottom - 4 - listTop;
        int visibleCount = listH / (RowHeight + RowGap);

        int totalCount = _currentMode == ViewMode.NpcWeights ? _filteredSpouses.Count : _filteredPois.Count;
        bool hasScroll = totalCount > visibleCount;

        if (totalCount == 0 && _currentMode == ViewMode.NpcWeights)
        {
            CustomFontManager.DrawString(b, "无已婚伴侣", new Vector2(_listRect.X + 16, listTop + 16), Color.White, CustomFontManager.SizeSmall);
            return;
        }

        int itemRightPad = hasScroll ? 15 : 6;
        int itemW = _listRect.Width - 6 - itemRightPad;
        bool isMouseDown = Mouse.GetState().LeftButton == ButtonState.Pressed;

        for (int i = 0; i < visibleCount; i++)
        {
            int scroll = _currentMode == ViewMode.NpcWeights ? _spouseListScroll : _poiListScroll;
            int idx = scroll + i;
            if (idx >= totalCount) break;

            var rowRect = new Rectangle(_listRect.X + 6, listTop + i * (RowHeight + RowGap), itemW, RowHeight);
            bool isSel = _currentMode == ViewMode.NpcWeights
                ? string.Equals(_filteredSpouses[idx].Id, _selectedSpouseId, StringComparison.OrdinalIgnoreCase)
                : (!_isCreatingNewPoi && string.Equals(_filteredPois[idx].Id, _selectedPoiId, StringComparison.OrdinalIgnoreCase));

            bool isHover = rowRect.Contains(mx, my);
            bool isPressed = isHover && isMouseDown;
            int pressOffset = isPressed ? 1 : 0;

            Color bg = isSel ? RulesTheme.SurfaceActive
                     : isPressed ? RulesTheme.SurfaceSunken
                     : isHover ? RulesTheme.SurfaceHover
                     : RulesTheme.SurfaceCard;

            if (!isPressed)
                b.Draw(Game1.staminaRect, new Rectangle(rowRect.X + 1, rowRect.Y + 2, rowRect.Width, rowRect.Height), RulesTheme.Shadow);

            var drawRect = new Rectangle(rowRect.X, rowRect.Y + pressOffset, rowRect.Width, rowRect.Height);
            b.Draw(Game1.staminaRect, new Rectangle(drawRect.X + 1, drawRect.Y + 1, drawRect.Width - 2, drawRect.Height - 2), bg);
            IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(432, 439, 9, 9),
                drawRect.X, drawRect.Y, drawRect.Width, drawRect.Height,
                isSel ? RulesTheme.BorderBold : (isHover ? RulesTheme.BorderMid : RulesTheme.BorderSoft), 2f, false);

            if (isSel)
                b.Draw(Game1.staminaRect, new Rectangle(drawRect.X + 2, drawRect.Y + 3, 4, drawRect.Height - 6), RulesTheme.AccentGold);

            if (_currentMode == ViewMode.NpcWeights)
                DrawSpouseRow(b, _filteredSpouses[idx], drawRect, isSel, isHover);
            else
                DrawPoiRow(b, _filteredPois[idx], drawRect, isSel, isHover);
        }

        if (hasScroll)
        {
            var trackRect = new Rectangle(_listRect.Right - 9, listTop, 5, listH);
            int scroll = _currentMode == ViewMode.NpcWeights ? _spouseListScroll : _poiListScroll;
            DrawScrollbarVisual(b, trackRect, visibleCount, totalCount, scroll, _isDraggingLeftScrollbar, mx, my);
        }
    }

    private static void DrawMapBadge(SpriteBatch b, Rectangle badgeRect, string rawMapName, bool isCustom)
    {
        string label = GetLocalizedMapName(rawMapName);
        var sz = CustomFontManager.MeasureString(label, CustomFontManager.SizeSmall);

        b.Draw(Game1.staminaRect, badgeRect, isCustom ? new Color(175, 115, 45) : RulesTheme.SurfaceSunken);
        IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(403, 383, 6, 6),
            badgeRect.X, badgeRect.Y, badgeRect.Width, badgeRect.Height, RulesTheme.BorderSoft, 1f, false);

        CustomFontManager.DrawString(b, label,
            new Vector2(badgeRect.X + (badgeRect.Width - sz.X) / 2f, badgeRect.Y + (badgeRect.Height - sz.Y) / 2f),
            Color.White, CustomFontManager.SizeSmall);
    }

    private static void DrawPoiRow(SpriteBatch b, PoiListEntry item, Rectangle drawRect, bool isSel, bool isHover)
    {
        string localizedMap = GetLocalizedMapName(item.MapName);
        var mapSz = CustomFontManager.MeasureString(localizedMap, CustomFontManager.SizeSmall);
        var mapRect = new Rectangle(drawRect.X + 8, drawRect.Y + (drawRect.Height - 20) / 2, (int)mapSz.X + 10, 20);

        DrawMapBadge(b, mapRect, item.MapName, item.IsCustom);

        int textX = mapRect.Right + 8;
        string label = !string.IsNullOrWhiteSpace(item.Alias) ? item.Alias : item.Id;

        if (item.IsCustom) label += " [★自创]";
        else if (item.HasCustomOverlay) label += " [*]";

        string truncated = CustomFontManager.TruncateString(label, CustomFontManager.SizeSmall, drawRect.Right - textX - 8);

        CustomFontManager.DrawString(b, truncated, new Vector2(textX, drawRect.Y + 11),
            isSel ? RulesTheme.TextCharcoal : (isHover ? RulesTheme.TextCharcoal : RulesTheme.TextDarkBrown), CustomFontManager.SizeSmall);
    }

    private static void DrawSpouseRow(SpriteBatch b, SpouseItem item, Rectangle drawRect, bool isSel, bool isHover)
    {
        int avatarSize = 28;
        var avatarRect = new Rectangle(drawRect.X + 8, drawRect.Y + (drawRect.Height - avatarSize) / 2, avatarSize, avatarSize);

        b.Draw(Game1.staminaRect, avatarRect, RulesTheme.SurfaceSunken);
        IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(403, 383, 6, 6),
            avatarRect.X - 1, avatarRect.Y - 1, avatarRect.Width + 2, avatarRect.Height + 2,
            isSel ? RulesTheme.BorderBold : (isHover ? RulesTheme.BorderMid : RulesTheme.BorderSoft), 1.2f, false);

        var (headSprite, srcRect) = GetNpcWalkingHeadSprite(item.Id);
        if (headSprite != null && !srcRect.IsEmpty)
        {
            b.Draw(headSprite, avatarRect, srcRect, Color.White);
        }
        else
        {
            string initial = string.IsNullOrEmpty(item.DisplayName) ? "?" : item.DisplayName.Substring(0, 1);
            var initSz = CustomFontManager.MeasureString(initial, CustomFontManager.SizeSmall);
            CustomFontManager.DrawString(b, initial,
                new Vector2(avatarRect.X + (avatarSize - initSz.X) / 2f, avatarRect.Y + (avatarSize - initSz.Y) / 2f - 1),
                RulesTheme.TextSecondary, CustomFontManager.SizeSmall);
        }

        int textX = avatarRect.Right + 8;
        string label = item.DisplayName + (item.HasCustomWeights ? " [★已定制]" : "");
        string truncated = CustomFontManager.TruncateString(label, CustomFontManager.SizeSmall, drawRect.Right - textX - 8);

        CustomFontManager.DrawString(b, truncated, new Vector2(textX, drawRect.Y + 11),
            isSel ? RulesTheme.TextCharcoal : (isHover ? RulesTheme.TextCharcoal : RulesTheme.TextDarkBrown), CustomFontManager.SizeSmall);
    }

    private void DrawSpouseWeightsForm(SpriteBatch b, int mx, int my)
    {
        DrawSectionCard(b, _formRect);

        if (_allSpouses.Count == 0)
        {
            int cx = _formRect.X + _formRect.Width / 2;
            int cy = _formRect.Y + _formRect.Height / 2 - 30;

            string title = "没有找到已结婚伴侣";
            string hint = "结婚后可在此自定义配偶在星露谷各兴趣点的日程出没偏好与驻留意愿。";

            var tSz = CustomFontManager.MeasureStringBold(title, CustomFontManager.SizeRegular);
            var hSz = CustomFontManager.MeasureString(hint, CustomFontManager.SizeSmall);

            CustomFontManager.DrawStringBold(b, title, new Vector2(cx - tSz.X / 2f, cy), Color.White, CustomFontManager.SizeRegular);
            CustomFontManager.DrawString(b, hint, new Vector2(cx - hSz.X / 2f, cy + 34), Color.White, CustomFontManager.SizeSmall);
            return;
        }

        var spouse = _allSpouses.FirstOrDefault(n => string.Equals(n.Id, _selectedSpouseId, StringComparison.OrdinalIgnoreCase));
        int lx = _formRect.X + 12;

        if (spouse == null)
        {
            CustomFontManager.DrawString(b, "从左侧列表选择一位已婚伴侣进行出没偏好调谐",
                new Vector2(_formRect.X + 24, _formRect.Y + 24), RulesTheme.TextSecondary, CustomFontManager.SizeRegular);
            return;
        }

        var infoRect = new Rectangle(lx, _formRect.Y + 10, _formRect.Width - 24, 46);
        b.Draw(Game1.staminaRect, infoRect, RulesTheme.SurfaceSunken);
        IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(403, 383, 6, 6),
            infoRect.X, infoRect.Y, infoRect.Width, infoRect.Height, RulesTheme.BorderSoft, 1.2f, false);

        string statusText = spouse.HasCustomWeights ? "★ 专属权重生效中" : "默认全 50 基准";
        Color statusCol = spouse.HasCustomWeights ? RulesTheme.AccentAmber : RulesTheme.TextSecondary;

        CustomFontManager.DrawStringBold(b, $"💍 {spouse.DisplayName} ({spouse.Id}) · 伴侣日程出没偏好", new Vector2(infoRect.X + 10, infoRect.Y + 8), RulesTheme.TextCharcoal, CustomFontManager.SizeRegular);
        CustomFontManager.DrawString(b, $"状态: {statusText}  |  数值范围: 0 (绝不前往) ~ 100 (极度向往)",
            new Vector2(infoRect.X + 12, infoRect.Y + 26), statusCol, CustomFontManager.SizeSmall);

        _visibleWeightRows.Clear();
        int rowH = 34;
        int rowGap = 4;
        int maxVisibleRows = Math.Max(1, _weightTableArea.Height / (rowH + rowGap));
        bool hasScroll = _allPois.Count > maxVisibleRows;
        int itemRightPad = hasScroll ? 14 : 4;
        int rowW = _weightTableArea.Width - itemRightPad;

        for (int i = 0; i < maxVisibleRows; i++)
        {
            int idx = _weightTableScroll + i;
            if (idx >= _allPois.Count) break;

            var entry = _allPois[idx];
            var rowRect = new Rectangle(_weightTableArea.X, _weightTableArea.Y + i * (rowH + rowGap), rowW, rowH);

            if (!_weightSteppers.TryGetValue(entry.Id, out var stepper))
            {
                stepper = new NumberStepper(Rectangle.Empty, DefaultWeight, 0, 100, 5, " 分");
                _weightSteppers[entry.Id] = stepper;
            }

            int stepperW = 140;
            int stepperX = rowRect.Right - stepperW - 6;
            int stepperY = rowRect.Y + (rowH - 28) / 2;
            stepper.SetBounds(new Rectangle(stepperX, stepperY, stepperW, 28));

            _visibleWeightRows.Add((rowRect, entry, stepper));

            b.Draw(Game1.staminaRect, rowRect, RulesTheme.SurfaceCard);
            IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(432, 439, 9, 9),
                rowRect.X, rowRect.Y, rowRect.Width, rowRect.Height, RulesTheme.BorderSoft, 1.5f, false);

            string mapTag = GetLocalizedMapName(entry.MapName);
            var tagSz = CustomFontManager.MeasureString(mapTag, CustomFontManager.SizeSmall);
            var tagRect = new Rectangle(rowRect.X + 6, rowRect.Y + (rowH - 20) / 2, (int)tagSz.X + 10, 20);
            DrawMapBadge(b, tagRect, entry.MapName, entry.IsCustom);

            int textX = tagRect.Right + 8;
            int maxTextW = stepperX - textX - 8;
            string displayName = !string.IsNullOrWhiteSpace(entry.Alias) ? entry.Alias : entry.Id;
            string truncated = CustomFontManager.TruncateString(displayName, CustomFontManager.SizeSmall, maxTextW);
            CustomFontManager.DrawString(b, truncated, new Vector2(textX, rowRect.Y + 9), RulesTheme.TextCharcoal, CustomFontManager.SizeSmall);

            stepper.Draw(b);
        }

        if (hasScroll)
        {
            var trackRect = new Rectangle(_weightTableArea.Right - 8, _weightTableArea.Y + 2, 5, _weightTableArea.Height - 4);
            DrawScrollbarVisual(b, trackRect, maxVisibleRows, _allPois.Count, _weightTableScroll, _isDraggingWeightScrollbar, mx, my);
        }

        DrawFormButton(b, _btnSave, "✔ 保存伴侣偏好", mx, my, isPrimary: true);
        DrawFormButton(b, _btnRevert, "↺ 恢复默认权重 (全 50)", mx, my, isPrimary: false, isEnabled: spouse.HasCustomWeights);

        string? msg = _errorMessage ?? _statusMessage;
        if (!string.IsNullOrEmpty(msg))
        {
            Color col = _errorMessage != null ? RulesTheme.AccentRed : RulesTheme.AccentGreen;
            CustomFontManager.DrawString(b, msg, new Vector2(lx, _btnSave.Y - 22), col, CustomFontManager.SizeSmall);
        }
    }

    private void DrawPoiCatalogForm(SpriteBatch b, int mx, int my)
    {
        DrawSectionCard(b, _formRect);

        int lx = _formRect.X + 12;
        var poi = !_isCreatingNewPoi
            ? _allPois.FirstOrDefault(p => string.Equals(p.Id, _selectedPoiId, StringComparison.OrdinalIgnoreCase))
            : null;

        if (poi == null && !_isCreatingNewPoi)
        {
            CustomFontManager.DrawString(b, "从左侧列表选择一个兴趣点进行巡礼，或点击下方【+ 新建兴趣点】",
                new Vector2(_formRect.X + 24, _formRect.Y + 24), RulesTheme.TextSecondary, CustomFontManager.SizeRegular);
            return;
        }

        // 1. 顶部信息卡片
        int topCardH = 92;
        var topCardRect = new Rectangle(lx, _formRect.Y + 10, _formRect.Width - 24, topCardH);
        b.Draw(Game1.staminaRect, topCardRect, RulesTheme.SurfaceSunken);
        IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(403, 383, 6, 6),
            topCardRect.X, topCardRect.Y, topCardRect.Width, topCardRect.Height, RulesTheme.BorderSoft, 1.2f, false);

        if (_isCreatingNewPoi)
        {
            CustomFontManager.DrawStringBold(b, "📍 新建自创兴趣点模式", new Vector2(topCardRect.X + 12, topCardRect.Y + 10), RulesTheme.TextCharcoal, CustomFontManager.SizeRegular);

            string captureInfo = _hasCapture
                ? $"已抓取目标: {GetLocalizedMapName(_captureMapName ?? "")} ({_captureTileX}, {_captureTileY})"
                : "尚未抓取坐标（请走到想要村民前往的瓦片后点击右下方【🎯 抓取当前坐标】）";
            Color captureCol = _hasCapture ? RulesTheme.AccentGreen : RulesTheme.TextSecondary;
            CustomFontManager.DrawString(b, captureInfo, new Vector2(topCardRect.X + 14, topCardRect.Y + 32), captureCol, CustomFontManager.SizeSmall);

            DrawFormButton(b, _btnTeleport, "🚀 传送测试", mx, my, isPrimary: false, isEnabled: false);
            DrawFormButton(b, _btnCapture, "🎯 抓取当前坐标", mx, my, isPrimary: true, isEnabled: IsWorldReady);
        }
        else if (poi != null)
        {
            string titleTag = poi.IsCustom ? "[★ 自创]" : (poi.HasCustomOverlay ? "[覆盖生效中]" : "[原版预设]");
            string displayTitle = !string.IsNullOrWhiteSpace(poi.Alias)
                ? $"📍 {poi.Alias}  {titleTag}"
                : $"📍 {poi.Id}  {titleTag}";

            CustomFontManager.DrawStringBold(b, displayTitle, new Vector2(topCardRect.X + 12, topCardRect.Y + 10), RulesTheme.TextCharcoal, CustomFontManager.SizeRegular);

            string coordText = _hasPendingCoordChange
                ? $"所在地图: {GetLocalizedMapName(_pendingMapName ?? "")}  |  坐标: ({_pendingTileX}, {_pendingTileY}) [★已重选坐标，待保存]"
                : $"所在地图: {GetLocalizedMapName(poi.MapName)}  |  坐标: ({poi.TileX}, {poi.TileY})";

            CustomFontManager.DrawString(b, coordText, new Vector2(topCardRect.X + 14, topCardRect.Y + 32), RulesTheme.TextCharcoal, CustomFontManager.SizeSmall);

            DrawFormButton(b, _btnTeleport, "🚀 传送测试", mx, my, isPrimary: false, isEnabled: CanTeleport);
            DrawFormButton(b, _btnCapture, "🎯 抓取新坐标绑定", mx, my, isPrimary: false, isEnabled: IsWorldReady);
        }

        // 2. 别名输入项
        CustomFontManager.DrawStringBold(b, "兴趣点自定义别名 (用于列表直观显示)", new Vector2(lx, _aliasBoxRect.Y - 18), RulesTheme.TextDarkBrown, CustomFontManager.SizeSmall);
        DrawSingleLineBox(b, _aliasBox);
        if (string.IsNullOrEmpty(_aliasBox.Text))
            CustomFontManager.DrawString(b, "点击输入别名（留空则显示原始标识）...", new Vector2(_aliasBox.X + 8, _aliasBox.Y + 6), DarkGrayText, CustomFontManager.SizeSmall);

        // 3. 环境描写输入框
        string channelTip = IsZh ? "中文环境描写 (Chinese Prompt Context)" : "English Environment Description";
        CustomFontManager.DrawStringBold(b, $"场景氛围与出没动机 ({channelTip})", new Vector2(lx, _descBox.Position.Y - 20), RulesTheme.TextDarkBrown, CustomFontManager.SizeSmall);
        _descBox.Draw(b);

        // 4. 底部 4 按钮底栏
        string saveLabel = _isCreatingNewPoi ? "✔ 保存新点位" : "✔ 保存配置";
        DrawFormButton(b, _btnSave, saveLabel, mx, my, isPrimary: true, isEnabled: CanSaveCurrentPoi);
        DrawFormButton(b, _btnRevert, "↺ 还原原版", mx, my, isPrimary: false, isEnabled: CanRevertSelectedPoi);

        string deleteLabel = _isCreatingNewPoi ? "取消新建" : "🗑️ 彻底删除";
        DrawFormButton(b, _btnDelete, deleteLabel, mx, my, isDanger: !_isCreatingNewPoi && CanDeleteSelectedPoi, isEnabled: _isCreatingNewPoi || CanDeleteSelectedPoi);

        DrawFormButton(b, _btnNewPoi, "➕ 新建兴趣点", mx, my, isPrimary: false, isEnabled: !_isCreatingNewPoi);

        string? msg = _errorMessage ?? _statusMessage;
        if (!string.IsNullOrEmpty(msg))
        {
            Color col = _errorMessage != null ? RulesTheme.AccentRed : RulesTheme.AccentGreen;
            CustomFontManager.DrawString(b, msg, new Vector2(lx + 4, _btnSave.Y - 24), col, CustomFontManager.SizeSmall);
        }
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
            b.Draw(Game1.staminaRect, new Rectangle(rect.X + 1, rect.Y + 2, rect.Width, rect.Height), RulesTheme.Shadow);

        var drawRect = new Rectangle(rect.X, rect.Y + pressOffset, rect.Width, rect.Height);
        b.Draw(Game1.staminaRect, new Rectangle(drawRect.X + 1, drawRect.Y + 1, drawRect.Width - 2, drawRect.Height - 2), bg);
        IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(432, 439, 9, 9),
            drawRect.X, drawRect.Y, drawRect.Width, drawRect.Height, border, 2f, false);

        var sz = CustomFontManager.MeasureStringBold(label, CustomFontManager.SizeSmall);
        CustomFontManager.DrawStringBold(b, label,
            new Vector2(drawRect.X + (drawRect.Width - sz.X) / 2f, drawRect.Y + (drawRect.Height - sz.Y) / 2f),
            isEnabled ? (isDanger ? Color.White : RulesTheme.TextCharcoal) : RulesTheme.TextMuted, CustomFontManager.SizeSmall);
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

    private static string GetLocalizedMapName(string mapName)
    {
        if (string.IsNullOrWhiteSpace(mapName)) return "未知";
        if (!IsZh) return mapName;

        try
        {
            var loc = Game1.getLocationFromName(mapName);
            if (loc != null && !string.IsNullOrWhiteSpace(loc.DisplayName))
                return loc.DisplayName;
        }
        catch { }

        return mapName switch
        {
            "Town" => "鹈鹕镇",
            "Beach" => "海滩",
            "Mountain" => "山地",
            "Forest" => "煤矿森林",
            "Farm" => "农场",
            "Saloon" => "星之果实餐吧",
            "SeedShop" => "皮埃尔杂货店",
            "Hospital" => "哈维诊所",
            "ArchaeologyHouse" => "博物馆",
            "Blacksmith" => "铁匠铺",
            "JoshHouse" => "乔治家",
            "HaleyHouse" => "艾米丽和海莉家",
            "SamHouse" => "山姆家",
            "LeahHouse" => "莉亚农舍",
            "ElliottHouse" => "艾利欧特木屋",
            "ScienceHouse" => "木匠工坊",
            "SebastianRoom" => "塞巴斯蒂安房间",
            "AdventureGuild" => "探险家公会",
            "BathHouse_Pool" => "水疗馆",
            "Mine" => "矿井",
            "Desert" => "卡利科沙漠",
            "Club" => "绿洲赌场",
            "SandyHouse" => "绿洲商店",
            "CommunityCenter" => "社区中心",
            "JojaMart" => "Joja超市",
            "MovieTheater" => "电影院",
            "ManorHouse" => "镇长庄园",
            "WizardHouse" => "法师塔",
            "Trailer" => "拖车",
            "Trailer_Big" => "潘妮新房",
            "IslandSouth" => "姜岛南部",
            "IslandNorth" => "姜岛北部",
            "IslandWest" => "姜岛西部",
            "IslandEast" => "姜岛东部",
            "IslandSouthEast" => "海盗湾",
            "IslandFieldOffice" => "野外考察室",
            "Subway" => "下水道",
            "Sewer" => "下水道",
            _ => mapName
        };
    }

    private void BuildBaselineCache()
    {
        _baselinePois.Clear();
        Dictionary<string, PoiAsset>? baseline = null;
        try
        {
            baseline = ModEntry.SHelper.GameContent.Load<Dictionary<string, PoiAsset>>(PoiRepository.POI_ASSET_KEY);
        }
        catch (Exception ex)
        {
            ModEntry.SMonitor?.Log($"[PoiTuning] POI 基线加载失败: {ex.Message}", LogLevel.Warn);
        }

        if (baseline != null)
        {
            foreach (var (k, v) in baseline)
            {
                if (!string.IsNullOrEmpty(k) && v != null)
                    _baselinePois[k] = v;
            }
        }
    }

    private void BuildMarriedSpouses()
    {
        _allSpouses.Clear();
        var ov = ModEntry.PoiPreferenceOverlay?.LoadOrNull();
        var rawCandidates = NpcCandidateQueryService.GetCleanedCandidates();

        foreach (var (id, disp) in rawCandidates)
        {
            if (!IsMarriedToFarmer(id))
                continue;

            bool hasCustom = ov?.NpcPreferences != null && ov.NpcPreferences.ContainsKey(id);
            _allSpouses.Add(new SpouseItem
            {
                Id = id,
                DisplayName = disp,
                HasCustomWeights = hasCustom
            });
        }
    }

    private static bool IsMarriedToFarmer(string npcName)
    {
        if (string.IsNullOrWhiteSpace(npcName)) return false;

        try
        {
            if (SpouseQueryService.Instance.IsMarried(npcName))
                return true;
        }
        catch { }

        if (Game1.player?.friendshipData != null &&
            Game1.player.friendshipData.TryGetValue(npcName, out var fs) && fs != null)
        {
            if (fs.IsMarried() || fs.IsRoommate())
                return true;
        }

        if (Game1.player != null && string.Equals(Game1.player.spouse, npcName, StringComparison.OrdinalIgnoreCase))
            return true;

        var character = Game1.getCharacterFromName(npcName);
        if (character != null && character.isMarried() && character.getSpouse() == Game1.player)
            return true;

        return false;
    }

    private void SelectInitialSpouse()
    {
        string? initial = Hub.CurrentNpcName;
        if (!string.IsNullOrEmpty(initial) && _allSpouses.Any(n => string.Equals(n.Id, initial, StringComparison.OrdinalIgnoreCase)))
            SelectSpouse(initial);
        else if (_allSpouses.Count > 0)
            SelectSpouse(_allSpouses[0].Id);
        else
            _selectedSpouseId = null;
    }

    private void SelectSpouse(string spouseId)
    {
        _selectedSpouseId = spouseId;
        _statusMessage = null;
        _errorMessage = null;
        LoadWorkingWeightsForSpouse();
    }

    private void SelectPoi(string poiId)
    {
        _isCreatingNewPoi = false;
        _selectedPoiId = poiId;
        _hasPendingCoordChange = false;
        _statusMessage = null;
        _errorMessage = null;

        var poi = _allPois.FirstOrDefault(p => string.Equals(p.Id, poiId, StringComparison.OrdinalIgnoreCase));
        if (poi != null)
        {
            _aliasBox.Text = poi.Alias;
            string desc = IsZh
                ? (!string.IsNullOrWhiteSpace(poi.SummaryZh) ? poi.SummaryZh : poi.SummaryEn)
                : (!string.IsNullOrWhiteSpace(poi.SummaryEn) ? poi.SummaryEn : poi.SummaryZh);
            _descBox.SetText(desc);
        }
        DeselectAllBoxes();
        RefreshFormSnapshot();
    }

    private void RefreshCaptureState()
    {
        _hasCapture = false;
        _captureMapName = null;
        _captureTileX = 0;
        _captureTileY = 0;
        _hasPendingCoordChange = false;
        RefreshFormSnapshot();
    }

    private void RebuildMergedPoiView()
    {
        _allPois.Clear();
        var ov = ModEntry.PoiPreferenceOverlay?.LoadOrNull();
        var customPoisMap = ov?.CustomPois ?? new Dictionary<string, PoiAsset>(StringComparer.OrdinalIgnoreCase);

        // 1. 基线 POI
        foreach (var (id, asset) in _baselinePois)
        {
            bool hasCustom = customPoisMap.TryGetValue(id, out var overriddenAsset) && overriddenAsset != null;
            var effectiveAsset = hasCustom ? overriddenAsset! : asset;

            _allPois.Add(new PoiListEntry
            {
                Id = id,
                Alias = _poiAliases.GetValueOrDefault(id, ""),
                MapName = effectiveAsset.MapName,
                TileX = effectiveAsset.TargetTile?.X ?? 0,
                TileY = effectiveAsset.TargetTile?.Y ?? 0,
                SummaryZh = effectiveAsset.DescriptionForLLM_Zh ?? "",
                SummaryEn = effectiveAsset.DescriptionForLLM ?? "",
                IsCustom = false,
                HasCustomOverlay = hasCustom
            });
        }

        // 2. 自创 POI
        var removedSet = new HashSet<string>(ov?.RemovedCustomPoiIds ?? new List<string>(), StringComparer.OrdinalIgnoreCase);
        foreach (var (id, asset) in customPoisMap)
        {
            if (_baselinePois.ContainsKey(id) || removedSet.Contains(id) || asset == null)
                continue;

            _allPois.Add(new PoiListEntry
            {
                Id = id,
                Alias = _poiAliases.GetValueOrDefault(id, ""),
                MapName = asset.MapName,
                TileX = asset.TargetTile?.X ?? 0,
                TileY = asset.TargetTile?.Y ?? 0,
                SummaryZh = asset.DescriptionForLLM_Zh ?? "",
                SummaryEn = asset.DescriptionForLLM ?? "",
                IsCustom = true,
                HasCustomOverlay = true
            });
        }

        // 自创置顶，别名/名称排序
        _allPois.Sort((a, b) =>
        {
            if (a.IsCustom != b.IsCustom) return a.IsCustom ? -1 : 1;
            string nameA = !string.IsNullOrWhiteSpace(a.Alias) ? a.Alias : a.Id;
            string nameB = !string.IsNullOrWhiteSpace(b.Alias) ? b.Alias : b.Id;
            return string.Compare(nameA, nameB, StringComparison.CurrentCultureIgnoreCase);
        });

        FilterCurrentList();
    }

    private void FilterCurrentList()
    {
        string q = _searchBox.Text.Trim();

        if (_currentMode == ViewMode.NpcWeights)
        {
            _filteredSpouses.Clear();
            if (string.IsNullOrEmpty(q))
            {
                _filteredSpouses.AddRange(_allSpouses);
            }
            else
            {
                foreach (var n in _allSpouses)
                {
                    if (n.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                        n.Id.Contains(q, StringComparison.OrdinalIgnoreCase))
                    {
                        _filteredSpouses.Add(n);
                    }
                }
            }
            int visible = Math.Max(1, (_listRect.Bottom - _searchBoxRect.Bottom - 9) / (RowHeight + RowGap));
            _spouseListScroll = Math.Clamp(_spouseListScroll, 0, Math.Max(0, _filteredSpouses.Count - visible));
        }
        else
        {
            _filteredPois.Clear();
            if (string.IsNullOrEmpty(q))
            {
                _filteredPois.AddRange(_allPois);
            }
            else
            {
                foreach (var p in _allPois)
                {
                    if (p.Id.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                        p.Alias.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                        p.MapName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                        GetLocalizedMapName(p.MapName).Contains(q, StringComparison.OrdinalIgnoreCase) ||
                        p.SummaryZh.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                        p.SummaryEn.Contains(q, StringComparison.OrdinalIgnoreCase))
                    {
                        _filteredPois.Add(p);
                    }
                }
            }
            int visible = Math.Max(1, (_listRect.Bottom - _searchBoxRect.Bottom - 9) / (RowHeight + RowGap));
            _poiListScroll = Math.Clamp(_poiListScroll, 0, Math.Max(0, _filteredPois.Count - visible));
        }
    }

    private void LoadWorkingWeightsForSpouse()
    {
        _weightSteppers.Clear();
        if (_selectedSpouseId == null) return;

        var ov = ModEntry.PoiPreferenceOverlay?.LoadOrNull();
        Dictionary<string, int> targetWeights = new(StringComparer.OrdinalIgnoreCase);

        if (ov?.NpcPreferences != null &&
            ov.NpcPreferences.TryGetValue(_selectedSpouseId, out var pref) &&
            pref?.PreferredPois != null)
        {
            foreach (var p in pref.PreferredPois)
            {
                if (!string.IsNullOrEmpty(p.PoiId))
                    targetWeights[p.PoiId] = Math.Clamp(p.Weight, 0, 100);
            }
        }

        foreach (var poi in _allPois)
        {
            int w = targetWeights.TryGetValue(poi.Id, out int val) ? val : DefaultWeight;
            _weightSteppers[poi.Id] = new NumberStepper(Rectangle.Empty, w, 0, 100, 5, " 分");
        }
        RefreshFormSnapshot();
    }

    private static void LoadPoiAliases()
    {
        try
        {
            string path = AliasesFilePath;
            if (File.Exists(path))
            {
                string json = File.ReadAllText(path);
                _poiAliases = Newtonsoft.Json.JsonConvert.DeserializeObject<Dictionary<string, string>>(json)
                              ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            }
        }
        catch { }
    }

    private static void SavePoiAliases()
    {
        try
        {
            string path = AliasesFilePath;
            string dir = Path.GetDirectoryName(path)!;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            string json = Newtonsoft.Json.JsonConvert.SerializeObject(_poiAliases, Newtonsoft.Json.Formatting.Indented);
            File.WriteAllText(path, json);
        }
        catch { }
    }

    private static (Texture2D? Texture, Rectangle SourceRect) GetNpcWalkingHeadSprite(string npcName)
    {
        if (string.IsNullOrWhiteSpace(npcName)) return (null, Rectangle.Empty);

        if (_avatarCache.TryGetValue(npcName, out var cached))
            return cached;

        NPC? npc = Game1.getCharacterFromName(npcName);
        Texture2D? texture = null;

        try
        {
            if (npc?.Sprite?.Texture != null && !npc.Sprite.Texture.IsDisposed)
                texture = npc.Sprite.Texture;
        }
        catch { }

        if (texture == null)
        {
            string assetName = npc?.getTextureName() ?? npcName;
            try
            {
                texture = Game1.content.Load<Texture2D>($"Characters\\{assetName}");
            }
            catch
            {
                return (null, Rectangle.Empty);
            }
        }

        if (texture == null || texture.IsDisposed)
            return (null, Rectangle.Empty);

        int frameWidth = 16;
        if (npc?.Sprite != null && npc.Sprite.SpriteWidth > 0)
            frameWidth = npc.Sprite.SpriteWidth;
        else if (texture.Width >= 64)
            frameWidth = texture.Width / 4;
        else if (texture.Width >= 32)
            frameWidth = texture.Width / 2;
        else
            frameWidth = texture.Width;

        int frameHeight = (npc?.Sprite != null && npc.Sprite.SpriteHeight > 0)
            ? npc.Sprite.SpriteHeight
            : Math.Min(texture.Height, frameWidth * 2);

        int topY = FindSpriteTopY(texture, frameWidth, frameHeight);
        int headHeight = Math.Min(frameWidth, texture.Height - topY);
        var result = (texture, new Rectangle(0, topY, frameWidth, headHeight));

        _avatarCache[npcName] = result;
        return result;
    }

    private static int FindSpriteTopY(Texture2D texture, int frameWidth, int frameHeight)
    {
        try
        {
            int checkWidth = Math.Min(frameWidth, texture.Width);
            int checkHeight = Math.Min(frameHeight, texture.Height);
            Color[] pixels = new Color[checkWidth * checkHeight];
            texture.GetData(0, new Rectangle(0, 0, checkWidth, checkHeight), pixels, 0, pixels.Length);

            for (int y = 0; y < checkHeight; y++)
            {
                for (int x = 0; x < checkWidth; x++)
                {
                    if (pixels[y * checkWidth + x].A > 20)
                        return y;
                }
            }
        }
        catch { }
        return 0;
    }

    private bool CanSaveCurrentPoi => _isCreatingNewPoi
        ? _hasCapture
        : !string.IsNullOrEmpty(_selectedPoiId);

    private bool CanRevertSelectedPoi => !_isCreatingNewPoi &&
        _allPois.FirstOrDefault(p => string.Equals(p.Id, _selectedPoiId, StringComparison.OrdinalIgnoreCase))?.HasCustomOverlay == true &&
        _baselinePois.ContainsKey(_selectedPoiId ?? "");

    private bool CanDeleteSelectedPoi => !_isCreatingNewPoi &&
        _allPois.FirstOrDefault(p => string.Equals(p.Id, _selectedPoiId, StringComparison.OrdinalIgnoreCase))?.IsCustom == true;

    private bool CanTeleport => !_isCreatingNewPoi && !string.IsNullOrEmpty(_selectedPoiId);

    private void StartCreateNewPoi()
    {
        _isCreatingNewPoi = true;
        _selectedPoiId = null;
        _aliasBox.Text = "";
        _descBox.SetText("");
        _hasPendingCoordChange = false;
        _statusMessage = "已开启新建模式：走到目标瓦片点击右上方【🎯 抓取当前坐标】即可保存。";
        _errorMessage = null;

        if (IsWorldReady)
            CaptureCurrentTile(silent: true);

        DeselectAllBoxes();
        Game1.playSound("smallSelect");
        LayoutRightForm();
        RefreshFormSnapshot();
    }

    private void CaptureCurrentTile(bool silent = false)
    {
        if (!IsWorldReady) return;
        if (PoiSamplingService.TryCaptureCurrentTile(out string mapName, out int tx, out int ty, out string reason))
        {
            if (_isCreatingNewPoi)
            {
                _captureMapName = mapName;
                _captureTileX = tx;
                _captureTileY = ty;
                _hasCapture = true;
            }
            else
            {
                var poi = _allPois.FirstOrDefault(p => string.Equals(p.Id, _selectedPoiId, StringComparison.OrdinalIgnoreCase));
                if (poi != null && !poi.IsCustom)
                {
                    if (!string.Equals(mapName, poi.MapName, StringComparison.OrdinalIgnoreCase))
                    {
                        _errorMessage = $"官方预设地点不可跨地图绑定到【{GetLocalizedMapName(mapName)}】！如需在新地图设立点位，请点击【➕ 新建兴趣点】。";
                        _statusMessage = null;
                        if (!silent) Game1.playSound("cancel");
                        return;
                    }
                }

                _pendingMapName = mapName;
                _pendingTileX = tx;
                _pendingTileY = ty;
                _hasPendingCoordChange = true;
            }

            _statusMessage = $"✔ 成功抓取: {GetLocalizedMapName(mapName)} ({tx}, {ty})";
            _errorMessage = null;
            if (!silent) Game1.playSound("coin");
        }
        else if (!silent)
        {
            _errorMessage = $"勘测失败: {reason}";
            _statusMessage = null;
            Game1.playSound("cancel");
        }
    }

    private void SavePoi()
    {
        var service = ModEntry.PoiPreferenceOverlay;
        if (service == null) { _errorMessage = "存储服务不可用"; return; }

        var ov = service.LoadOrNull() ?? new PoiOverlayFile();

        string text = _descBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(text))
            text = IsZh ? "村民在此悠闲驻足与观察四周。" : "Villagers linger here peacefully and observe their surroundings.";
        if (text.Length > DescMaxChars) text = text.Substring(0, DescMaxChars);

        string aliasText = _aliasBox.Text.Trim();

        if (_isCreatingNewPoi)
        {
            if (!_hasCapture || _captureMapName == null) return;

            var existingIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var id in ov.CustomPois.Keys) existingIds.Add(id);
            foreach (var id in _baselinePois.Keys) existingIds.Add(id);

            string newId = CustomPoiIdBuilder.BuildCustomPoiId($"POI_{_captureMapName}_{_captureTileX}_{_captureTileY}", existingIds);

            var newAsset = new PoiAsset
            {
                MapName = _captureMapName,
                TargetTile = new PoiTile { X = _captureTileX, Y = _captureTileY },
                Conditions = new PoiConditions(),
                StayMinutes = 90,
                DescriptionForLLM = !IsZh ? text : text,
                DescriptionForLLM_Zh = IsZh ? text : text
            };

            ov.CustomPois[newId] = newAsset;
            ov.RemovedCustomPoiIds.Remove(newId);

            if (!string.IsNullOrWhiteSpace(aliasText))
                _poiAliases[newId] = aliasText;

            if (service.Save(ov, out string err))
            {
                SavePoiAliases();
                _statusMessage = $"✔ 已新建并登记兴趣点【{(!string.IsNullOrWhiteSpace(aliasText) ? aliasText : newId)}】";
                _errorMessage = null;
                _isCreatingNewPoi = false;
                RefreshCaptureState();
                RebuildMergedPoiView();
                SelectPoi(newId);
                Game1.playSound("coin");
                Hub.RefreshEntries();
                LayoutRightForm();
            }
            else
            {
                _errorMessage = string.IsNullOrEmpty(err) ? "保存失败" : err;
                _statusMessage = null;
                Game1.playSound("cancel");
            }
        }
        else if (!string.IsNullOrEmpty(_selectedPoiId))
        {
            PoiAsset assetToSave;
            if (ov.CustomPois.TryGetValue(_selectedPoiId, out var existing) && existing != null)
            {
                assetToSave = existing;
            }
            else if (_baselinePois.TryGetValue(_selectedPoiId, out var basePoi))
            {
                assetToSave = new PoiAsset
                {
                    MapName = basePoi.MapName,
                    TargetTile = basePoi.TargetTile != null ? new PoiTile { X = basePoi.TargetTile.X, Y = basePoi.TargetTile.Y } : new PoiTile(),
                    Conditions = basePoi.Conditions,
                    StayMinutes = basePoi.StayMinutes,
                    DescriptionForLLM = basePoi.DescriptionForLLM,
                    DescriptionForLLM_Zh = basePoi.DescriptionForLLM_Zh
                };
            }
            else
            {
                var entry = _allPois.FirstOrDefault(p => string.Equals(p.Id, _selectedPoiId, StringComparison.OrdinalIgnoreCase));
                assetToSave = new PoiAsset { MapName = entry?.MapName ?? "", TargetTile = new PoiTile { X = entry?.TileX ?? 0, Y = entry?.TileY ?? 0 } };
            }

            if (_hasPendingCoordChange && _pendingMapName != null)
            {
                assetToSave.MapName = _pendingMapName;
                assetToSave.TargetTile = new PoiTile { X = _pendingTileX, Y = _pendingTileY };
            }

            if (IsZh)
            {
                assetToSave.DescriptionForLLM_Zh = text;
                if (string.IsNullOrWhiteSpace(assetToSave.DescriptionForLLM))
                    assetToSave.DescriptionForLLM = text;
            }
            else
            {
                assetToSave.DescriptionForLLM = text;
                if (string.IsNullOrWhiteSpace(assetToSave.DescriptionForLLM_Zh))
                    assetToSave.DescriptionForLLM_Zh = text;
            }

            ov.CustomPois[_selectedPoiId] = assetToSave;
            ov.RemovedCustomPoiIds.Remove(_selectedPoiId);

            if (!string.IsNullOrWhiteSpace(aliasText))
                _poiAliases[_selectedPoiId] = aliasText;
            else
                _poiAliases.Remove(_selectedPoiId);

            if (service.Save(ov, out string err))
            {
                SavePoiAliases();
                _statusMessage = $"✔ 兴趣点【{_selectedPoiId}】设定已成功保存";
                _errorMessage = null;
                _hasPendingCoordChange = false;
                string currentId = _selectedPoiId;
                RebuildMergedPoiView();
                SelectPoi(currentId);
                Game1.playSound("coin");
                Hub.RefreshEntries();
                LayoutRightForm();
            }
            else
            {
                _errorMessage = string.IsNullOrEmpty(err) ? "保存失败" : err;
                _statusMessage = null;
                Game1.playSound("cancel");
            }
        }
    }

    private void RequestRevertSelectedPoi()
    {
        if (string.IsNullOrEmpty(_selectedPoiId)) return;
        string targetId = _selectedPoiId;

        Game1.activeClickableMenu = new BioValveWarningDialog(
            Hub,
            "恢复原版兴趣点",
            $"即将把兴趣点【{targetId}】恢复为官方原版基准：",
            new List<string>
            {
                "坐标与描写将恢复为官方原版预设",
                "自定义别名将被一并清空"
            },
            "⚠ 确认恢复",
            () =>
            {
                Game1.activeClickableMenu = Hub;
                var service = ModEntry.PoiPreferenceOverlay;
                if (service == null) return;

                var ov = service.LoadOrNull() ?? new PoiOverlayFile();
                ov.CustomPois.Remove(targetId);

                _poiAliases.Remove(targetId);
                SavePoiAliases();

                if (service.Save(ov, out string? _))
                {
                    _hasPendingCoordChange = false;
                    RebuildMergedPoiView();
                    SelectPoi(targetId);
                    _aliasBox.Text = "";
                    _statusMessage = "✔ 已恢复原版官方预设并清除别名";
                    Game1.playSound("coin");
                    Hub.RefreshEntries();
                    LayoutRightForm();
                }
            },
            "保持现状",
            () => Game1.activeClickableMenu = Hub);
    }

    private void TeleportToSelected()
    {
        if (_selectedPoiId == null) return;
        var e = _allPois.FirstOrDefault(x => string.Equals(x.Id, _selectedPoiId, StringComparison.OrdinalIgnoreCase));
        if (e == null) return;

        string mapName = _hasPendingCoordChange && _pendingMapName != null ? _pendingMapName : e.MapName;
        int tx = _hasPendingCoordChange ? _pendingTileX : e.TileX;
        int ty = _hasPendingCoordChange ? _pendingTileY : e.TileY;

        string displayName = !string.IsNullOrWhiteSpace(e.Alias) ? e.Alias : e.Id;

        // ── ★ 核心防御：节日场地安全守卫 ──────────────────────────────────────
        if (IsFestivalBlockingWarp(mapName, out string blockReason))
        {
            Game1.playSound("cancel");
            _errorMessage = blockReason;
            _statusMessage = null;
            Game1.addHUDMessage(new HUDMessage(blockReason, HUDMessage.error_type));
            return;
        }

        try
        {
            // 激活安全悬浮挂件，自动防御重入与原地挂载
            PoiInspectHud.BeginSession(e.Id, displayName, mapName, tx, ty, hubTab: Hub.CurrentTab);

            Game1.exitActiveMenu();
            Game1.warpFarmer(mapName, tx, ty, 2);
        }
        catch (Exception ex)
        {
            ModEntry.SMonitor?.Log($"[PoiTuning] 传送失败 ({mapName} {tx},{ty}): {ex.Message}", LogLevel.Warn);
        }
    }

    /// <summary>
    /// 判定目标地图是否正被今日节日封锁或处于节日庆典中。
    /// </summary>
    private static bool IsFestivalBlockingWarp(string targetMapName, out string reason)
    {
        reason = string.Empty;

        // 1. 判定今天是否是节日
        if (Utility.isFestivalDay())
        {
            // 获取今天节日举办的具体地点（如 "Town", "Forest", "Beach" 等）
            string? festivalLocation = Game1.whereIsTodaysFest;

            if (!string.IsNullOrEmpty(festivalLocation) &&
                string.Equals(targetMapName, festivalLocation, StringComparison.OrdinalIgnoreCase))
            {
                // 获取节日开始时间（例如 900）
                int startTime = -1;
                try
                {
                    var festData = Game1.temporaryContent.Load<Dictionary<string, string>>("Data\\Festivals\\" + Game1.currentSeason + Game1.dayOfMonth);
                    if (festData != null && festData.TryGetValue("conditions", out string? cond))
                    {
                        string[] parts = cond.Split('/');
                        if (parts.Length > 1)
                            startTime = Convert.ToInt32(parts[1].Split(' ')[0]);
                    }
                }
                catch { }

                if (startTime > 0 && Game1.timeOfDay < startTime)
                {
                    reason = $"【{GetLocalizedMapName(targetMapName)}】正在筹备今日节日庆典（{startTime / 100}:00 前封闭），禁止传送测试！";
                    return true;
                }
                else
                {
                    reason = $"【{GetLocalizedMapName(targetMapName)}】当前正处于节日庆典中，强行传送会导致剧情事件错乱，已被系统拦截！";
                    return true;
                }
            }
        }

        // 2. 判定目标地图是否处于原版封锁状态（例如正在发生过场事件）
        if (Game1.eventUp)
        {
            reason = "当前正在进行过场动画/剧情事件，无法传送！";
            return true;
        }

        return false;
    }

    private void RequestDeleteSelectedPoi()
    {
        if (!CanDeleteSelectedPoi || _selectedPoiId == null) return;
        string targetId = _selectedPoiId;

        Game1.activeClickableMenu = new BioValveWarningDialog(
            Hub,
            "删除自创兴趣点",
            $"即将彻底删除自创兴趣点【{targetId}】：",
            new List<string>
            {
                "该兴趣点及其自定义别名将被彻底移除",
                "各 NPC 对该点的出没偏好引用将被一并清除"
            },
            "⚠ 确认删除",
            () =>
            {
                Game1.activeClickableMenu = Hub;
                var service = ModEntry.PoiPreferenceOverlay;
                if (service == null) return;

                var ov = service.LoadOrNull() ?? new PoiOverlayFile();
                ov.CustomPois.Remove(targetId);

                if (!ov.RemovedCustomPoiIds.Contains(targetId, StringComparer.OrdinalIgnoreCase))
                    ov.RemovedCustomPoiIds.Add(targetId);

                if (ov.NpcPreferences != null)
                {
                    foreach (var pref in ov.NpcPreferences.Values)
                        pref.PreferredPois?.RemoveAll(p => string.Equals(p.PoiId, targetId, StringComparison.OrdinalIgnoreCase));
                }

                _poiAliases.Remove(targetId);
                SavePoiAliases();

                if (service.Save(ov, out string? _))
                {
                    RebuildMergedPoiView();
                    if (_allPois.Count > 0) SelectPoi(_allPois[0].Id);
                    else _selectedPoiId = null;
    
                    _statusMessage = "✔ 已彻底删除自创兴趣点";
                    Game1.playSound("trashcan");
                    Hub.RefreshEntries();
                    LayoutRightForm();
                }
            },
            "保留兴趣点",
            () => Game1.activeClickableMenu = Hub);
    }

    private void SaveWeights()
    {
        if (_selectedSpouseId == null) return;
        var service = ModEntry.PoiPreferenceOverlay;
        if (service == null) { _errorMessage = "存储服务不可用"; return; }

        var ov = service.LoadOrNull() ?? new PoiOverlayFile();
        var pref = new NpcPreference();

        foreach (var poi in _allPois)
        {
            int weight = _weightSteppers.TryGetValue(poi.Id, out var stepper) ? stepper.Value : DefaultWeight;
            pref.PreferredPois.Add(new NpcPoiPreference
            {
                PoiId = poi.Id,
                Weight = Math.Clamp(weight, 0, 100)
            });
        }

        ov.NpcPreferences ??= new Dictionary<string, NpcPreference>(StringComparer.OrdinalIgnoreCase);
        ov.NpcPreferences[_selectedSpouseId] = pref;
        ov.RemovedNpcNames.Remove(_selectedSpouseId);

        if (service.Save(ov, out string err))
        {
            BuildMarriedSpouses();
            FilterCurrentList();
            _statusMessage = "✔ 伴侣出没意愿偏好已保存";
            _errorMessage = null;
            Game1.playSound("coin");
            Hub.RefreshEntries();
            RefreshFormSnapshot();
        }
        else
        {
            _errorMessage = string.IsNullOrEmpty(err) ? "保存失败" : err;
            _statusMessage = null;
            Game1.playSound("cancel");
        }
    }

    private void RequestRevertSpouseWeights()
    {
        if (_selectedSpouseId == null) return;
        var spouse = _allSpouses.FirstOrDefault(n => string.Equals(n.Id, _selectedSpouseId, StringComparison.OrdinalIgnoreCase));
        if (spouse == null || !spouse.HasCustomWeights) return;

        Game1.activeClickableMenu = new BioValveWarningDialog(
            Hub,
            "重置出没意愿",
            $"即将重置【{spouse.DisplayName}】的兴趣点出没意愿：",
            new List<string>
            {
                "全部兴趣点的出没意愿将恢复为默认值 50"
            },
            "⚠ 确认重置",
            () =>
            {
                Game1.activeClickableMenu = Hub;
                var service = ModEntry.PoiPreferenceOverlay;
                if (service == null) return;

                var ov = service.LoadOrNull() ?? new PoiOverlayFile();
                ov.NpcPreferences.Remove(_selectedSpouseId);

                if (!ov.RemovedNpcNames.Contains(_selectedSpouseId, StringComparer.OrdinalIgnoreCase))
                    ov.RemovedNpcNames.Add(_selectedSpouseId);

                if (service.Save(ov, out string? _))
                {
                    BuildMarriedSpouses();
                    FilterCurrentList();
                    LoadWorkingWeightsForSpouse();
                    _statusMessage = "✔ 已重置为默认基准权重 (全 50)";
                    Game1.playSound("coin");
                    Hub.RefreshEntries();
                }
            },
            "保持现状",
            () => Game1.activeClickableMenu = Hub);
    }
}