#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;
using ValleytalkReborn.Services.Overlays;

namespace ValleytalkReborn.UI;

/// <summary>地点与节日编撰子页：上块=地点描述（只读目录，仅编辑描述）；下块=节日（编辑原版 + 自创纪念日）。</summary>
internal sealed class LocationFestivalPage : WorldSubPageBase
{
    private const int RowHeight = 28;
    private const int RowGap = 4;
    private const int FormTopPad = 12;
    private const int LabelW = 120;
    private const int CtrlH = 30;
    private const int CtrlGap = 8;
    private const int BtnH = 30;
    private const int BtnGap = 6;
    private const int DescMaxChars = 500;

    // ── 基线缓存（OnShown 一次）─────────────────────────────────────────
    private Dictionary<string, string> _baselineLocations = new(StringComparer.OrdinalIgnoreCase); // id -> Name
    private Dictionary<string, string> _baselineRegions = new(StringComparer.OrdinalIgnoreCase);  // id -> Region
    private Dictionary<string, string> _baselineFestivals = new(StringComparer.OrdinalIgnoreCase); // key -> Name
    private string? _baselineAssetError;

    // ── 地点描述区 ───────────────────────────────────────────────────
    private readonly List<LocationListEntry> _locationEntries = new();
    private int _locScroll;
    private string? _selectedLocId;
    private Rectangle _locListArea;
    private Rectangle _locFormArea;
    private readonly MultilineTextBox _locDescBox = new(Rectangle.Empty, 6);
    private Rectangle _btnLocSave;
    private Rectangle _btnLocRevert;
    private string? _locStatus;
    private string? _locError;

    private sealed class LocationListEntry
    {
        public string Id = "";
        public string Name = "";
        public string Region = "";
    }

    // ── 节日区 ───────────────────────────────────────────────────────
    private readonly List<FestivalListEntry> _festivalEntries = new();
    private int _festScroll;
    private string? _selectedFestKey;
    private Rectangle _festListArea;
    private Rectangle _festFormArea;
    private readonly DropdownList _seasonDropdown = new(Rectangle.Empty, CtrlH, 4);
    private readonly NumberStepper _dayStepper;
    private readonly MultilineTextBox _festNameBoxZh = new(Rectangle.Empty, 1);
    private readonly MultilineTextBox _festNameBoxEn = new(Rectangle.Empty, 1);
    private readonly MultilineTextBox _festDescBoxZh = new(Rectangle.Empty, 4);
    private readonly MultilineTextBox _festDescBoxEn = new(Rectangle.Empty, 4);
    private Rectangle _btnFestSave;
    private Rectangle _btnFestDelete;
    private Rectangle _btnFestNew;
    private string? _festStatus;
    private string? _festError;
    private string _newSeason = "spring";
    private int _newDay = 1;

    private sealed class FestivalListEntry
    {
        public string Key = "";
        public string Name = "";
        public bool IsBaseline;
        public bool IsCustom;
        public bool IsTombstone; // 基线对被删除（还原态）
    }

    private bool IsZh => LocalizedContentManager.CurrentLanguageCode == LocalizedContentManager.LanguageCode.zh;
    private string LangKey => IsZh ? "zh" : "en";

    public LocationFestivalPage(IntegratedHubMenu hub) : base(hub)
    {
        _dayStepper = new NumberStepper(Rectangle.Empty, 1, 1, 28, 1, "");
    }

    public override void OnShown()
    {
        BuildBaselineCache();
        RebuildLocationView();
        RebuildFestivalView();
        if (_locationEntries.Count > 0 && _selectedLocId == null)
            SelectLocation(_locationEntries[0].Id);
        else if (_selectedLocId != null)
            SelectLocation(_selectedLocId);
        if (_festivalEntries.Count > 0 && _selectedFestKey == null)
            SelectFestival(_festivalEntries[0].Key);
        else if (_selectedFestKey != null)
            SelectFestival(_selectedFestKey);
    }

    public override void Layout(Rectangle area)
    {
        int x = area.X;
        int w = area.Width;
        int halfH = area.Height / 2;

        _locListArea = new Rectangle(x, area.Y, w * 38 / 100, halfH);
        _locFormArea = new Rectangle(x + w * 38 / 100 + 12, area.Y, w * 62 / 100 - 12, halfH);
        LayoutLocationForm();

        int festY = area.Y + halfH + 8;
        int festH = area.Height - halfH - 8;
        _festListArea = new Rectangle(x, festY, w * 38 / 100, festH);
        _festFormArea = new Rectangle(x + w * 38 / 100 + 12, festY, w * 62 / 100 - 12, festH);
        LayoutFestivalForm();
    }

    private void LayoutLocationForm()
    {
        int x = _locFormArea.X + 8;
        int y = _locFormArea.Y + FormTopPad;
        int w = _locFormArea.Width - 16;
        _locDescBox.SetBounds(new Rectangle(x, y, w, 120)); y += 128;
        int btnW = (w - BtnGap) / 2;
        _btnLocSave = new Rectangle(x, y, btnW, BtnH);
        _btnLocRevert = new Rectangle(x + btnW + BtnGap, y, btnW, BtnH);
    }

    private void LayoutFestivalForm()
    {
        int x = _festFormArea.X + 8;
        int y = _festFormArea.Y + FormTopPad;
        int w = _festFormArea.Width - 16;

        _seasonDropdown.SetHeaderBounds(new Rectangle(x + LabelW, y, w - LabelW, CtrlH)); y += CtrlH + CtrlGap;
        _dayStepper.SetBounds(new Rectangle(x + LabelW, y, w - LabelW, CtrlH)); y += CtrlH + CtrlGap;
        _festNameBoxZh.SetBounds(new Rectangle(x + LabelW, y, w - LabelW, CtrlH)); y += CtrlH + CtrlGap;
        _festNameBoxEn.SetBounds(new Rectangle(x + LabelW, y, w - LabelW, CtrlH)); y += CtrlH + CtrlGap;
        int descH = 56;
        _festDescBoxZh.SetBounds(new Rectangle(x + LabelW, y, w - LabelW, descH)); y += descH + CtrlGap;
        _festDescBoxEn.SetBounds(new Rectangle(x + LabelW, y, w - LabelW, descH)); y += descH + CtrlGap + 4;
        int btnW = (w - BtnGap * 2) / 3;
        _btnFestSave = new Rectangle(x, y, btnW, BtnH);
        _btnFestDelete = new Rectangle(x + btnW + BtnGap, y, btnW, BtnH);
        _btnFestNew = new Rectangle(x + (btnW + BtnGap) * 2, y, btnW, BtnH);
    }

    public override void Update(GameTime time)
    {
        _locDescBox.Update(time);
        _festNameBoxZh.Update(time);
        _festNameBoxEn.Update(time);
        _festDescBoxZh.Update(time);
        _festDescBoxEn.Update(time);
    }

    public override bool ReceiveLeftClick(int x, int y)
    {
        if (_locListArea.Contains(x, y)) { HandleLocListClick(x, y); return true; }
        if (_festListArea.Contains(x, y)) { HandleFestListClick(x, y); return true; }

        // 地点表单
        if (_locDescBox.Bounds.Contains(x, y)) { SelectLocDesc(); return true; }
        if (_btnLocSave.Contains(x, y) && CanSaveLocation) { SaveLocation(); return true; }
        if (_btnLocRevert.Contains(x, y) && _selectedLocId != null) { RevertLocation(); return true; }

        // 节日表单
        if (_seasonDropdown.ReceiveLeftClick(x, y)) return true;
        if (_dayStepper.ReceiveLeftClick(x, y)) return true;
        if (_festNameBoxZh.Bounds.Contains(x, y)) { SelectFestBox(_festNameBoxZh); return true; }
        if (_festNameBoxEn.Bounds.Contains(x, y)) { SelectFestBox(_festNameBoxEn); return true; }
        if (_festDescBoxZh.Bounds.Contains(x, y)) { SelectFestBox(_festDescBoxZh); return true; }
        if (_festDescBoxEn.Bounds.Contains(x, y)) { SelectFestBox(_festDescBoxEn); return true; }
        if (_seasonDropdown.IsOpen) { _seasonDropdown.Close(); return true; }

        if (_btnFestSave.Contains(x, y) && CanSaveFestival) { SaveFestival(); return true; }
        if (_btnFestDelete.Contains(x, y) && CanDeleteFestival) { DeleteFestival(); return true; }
        if (_btnFestNew.Contains(x, y)) { NewFestival(); return true; }

        return false;
    }

    public override bool ReceiveScrollWheel(int direction)
    {
        int mx = Game1.getMouseX(), my = Game1.getMouseY();
        if (_locListArea.Contains(mx, my)) { _locScroll = ClampScroll(_locScroll - direction, _locationEntries.Count, _locListArea); return true; }
        if (_festListArea.Contains(mx, my)) { _festScroll = ClampScroll(_festScroll - direction, _festivalEntries.Count, _festListArea); return true; }
        if (_seasonDropdown.ReceiveScrollWheel(direction)) return true;
        foreach (var tb in new[] { _locDescBox, _festNameBoxZh, _festNameBoxEn, _festDescBoxZh, _festDescBoxEn })
            if (tb.Selected) { tb.Scroll(direction); return true; }
        if (_seasonDropdown.IsOpen) return true;
        return false;
    }

    public override bool ReceiveKeyPress(Keys key)
    {
        if (AnyTextboxSelected() && Game1.keyboardDispatcher.Subscriber is MultilineTextBox)
        {
            if (key == Keys.Escape) { DeselectAllBoxes(); return true; }
            return false;
        }
        if (_seasonDropdown.IsOpen && key == Keys.Escape) { _seasonDropdown.Close(); return true; }
        return false;
    }

    public override void OnHidden() => DeselectAllBoxes();

    private int ClampScroll(int value, int total, Rectangle area)
    {
        int visible = Math.Max(0, (area.Height - 16) / (RowHeight + RowGap));
        return Math.Clamp(value, 0, Math.Max(0, total - visible));
    }

    // ── 绘制 ──────────────────────────────────────────────────────────

    public override void Draw(SpriteBatch b, Rectangle area, int mx, int my)
    {
        DrawLocationList(b, mx, my);
        DrawLocationForm(b, mx, my);
        DrawFestivalList(b, mx, my);
        DrawFestivalForm(b, mx, my);
    }

    private void DrawLocationList(SpriteBatch b, int mx, int my)
    {
        b.Draw(Game1.staminaRect, _locListArea, new Color(0, 0, 0) * 0.04f);
        int y = _locListArea.Y + 6;
        int end = Math.Min(_locationEntries.Count, _locScroll + VisibleCount(_locListArea));
        for (int i = _locScroll; i < end; i++)
        {
            var e = _locationEntries[i];
            var row = new Rectangle(_locListArea.X + 4, y, _locListArea.Width - 8, RowHeight);
            bool selected = string.Equals(e.Id, _selectedLocId, StringComparison.OrdinalIgnoreCase);
            bool hover = row.Contains(mx, my);
            if (selected) b.Draw(Game1.staminaRect, row, new Color(210, 180, 140));
            else if (hover) b.Draw(Game1.staminaRect, row, new Color(255, 235, 205));
            CustomFontManager.DrawString(b, $"{e.Name} ({e.Region})", new Vector2(row.X + 6, row.Y + 5),
                selected ? Game1.textColor : Color.White * 0.92f, CustomFontManager.SizeRegular);
            y += RowHeight + RowGap;
        }
    }

    private void DrawLocationForm(SpriteBatch b, int mx, int my)
    {
        b.Draw(Game1.staminaRect, _locFormArea, new Color(0, 0, 0) * 0.04f);
        int x = _locFormArea.X + 8;
        int y = _locFormArea.Y + FormTopPad - 14;
        CustomFontManager.DrawStringBold(b, I18n.WorldSettings.LocationDescription(), new Vector2(x, y), Game1.textColor, CustomFontManager.SizeRegular);
        _locDescBox.Draw(b);
        y = _btnLocSave.Y;
        DrawButton(b, _btnLocSave, I18n.WorldSettings.Save(), _btnLocSave.Contains(mx, my), CanSaveLocation);
        DrawButton(b, _btnLocRevert, I18n.WorldSettings.RevertThisLoc(), _btnLocRevert.Contains(mx, my), _selectedLocId != null);
        string? msg = _locError ?? _locStatus;
        if (msg != null)
            CustomFontManager.DrawString(b, msg, new Vector2(x, _locFormArea.Bottom - 24),
                _locError != null ? Color.Red : new Color(60, 130, 60), CustomFontManager.SizeSmall);
    }

    private void DrawFestivalList(SpriteBatch b, int mx, int my)
    {
        b.Draw(Game1.staminaRect, _festListArea, new Color(0, 0, 0) * 0.04f);
        int y = _festListArea.Y + 6;
        int end = Math.Min(_festivalEntries.Count, _festScroll + VisibleCount(_festListArea));
        for (int i = _festScroll; i < end; i++)
        {
            var e = _festivalEntries[i];
            var row = new Rectangle(_festListArea.X + 4, y, _festListArea.Width - 8, RowHeight);
            bool selected = string.Equals(e.Key, _selectedFestKey, StringComparison.OrdinalIgnoreCase);
            bool hover = row.Contains(mx, my);
            if (selected) b.Draw(Game1.staminaRect, row, new Color(210, 180, 140));
            else if (hover) b.Draw(Game1.staminaRect, row, new Color(255, 235, 205));
            string tag = e.IsCustom ? " [+]" : "";
            CustomFontManager.DrawString(b, e.Name + tag, new Vector2(row.X + 6, row.Y + 5),
                selected ? Game1.textColor : Color.White * 0.92f, CustomFontManager.SizeRegular);
            y += RowHeight + RowGap;
        }
    }

    private void DrawFestivalForm(SpriteBatch b, int mx, int my)
    {
        b.Draw(Game1.staminaRect, _festFormArea, new Color(0, 0, 0) * 0.04f);
        int x = _festFormArea.X + 8;
        int y = _festFormArea.Y + FormTopPad;
        int lw = _festFormArea.Width - 16;

        CustomFontManager.DrawStringBold(b, I18n.WorldSettings.Festival(), new Vector2(x, y - 14), Game1.textColor, CustomFontManager.SizeRegular);
        DrawLabel(b, I18n.WorldSettings.Season(), x, y); _seasonDropdown.Draw(b); y += CtrlH + CtrlGap;
        DrawLabel(b, I18n.WorldSettings.Day(), x, y); _dayStepper.Draw(b); y += CtrlH + CtrlGap;
        DrawLabel(b, I18n.WorldSettings.FestivalNameZh(), x, y); _festNameBoxZh.Draw(b); y += CtrlH + CtrlGap;
        DrawLabel(b, I18n.WorldSettings.FestivalNameEn(), x, y); _festNameBoxEn.Draw(b); y += CtrlH + CtrlGap;
        DrawLabel(b, I18n.WorldSettings.FestivalDescZh(), x, y); _festDescBoxZh.Draw(b); y += 56 + CtrlGap;
        DrawLabel(b, I18n.WorldSettings.FestivalDescEn(), x, y); _festDescBoxEn.Draw(b); y += 56 + CtrlGap + 4;

        DrawButton(b, _btnFestSave, I18n.WorldSettings.SaveFestival(), _btnFestSave.Contains(mx, my), CanSaveFestival);
        DrawButton(b, _btnFestDelete, I18n.WorldSettings.DeleteFestival(), _btnFestDelete.Contains(mx, my), CanDeleteFestival);
        DrawButton(b, _btnFestNew, I18n.WorldSettings.NewCustomFestival(), _btnFestNew.Contains(mx, my), true);

        string? msg = _festError ?? _festStatus ?? FestivalValidationHint();
        if (msg != null)
            CustomFontManager.DrawString(b, msg, new Vector2(x, _festFormArea.Bottom - 24),
                _festError != null ? Color.Red : new Color(60, 130, 60), CustomFontManager.SizeSmall);
    }

    private void DrawLabel(SpriteBatch b, string text, int x, int y) =>
        CustomFontManager.DrawStringBold(b, text, new Vector2(x, y + 5), Game1.textColor, CustomFontManager.SizeRegular);

    private void DrawButton(SpriteBatch b, Rectangle rect, string label, bool hover, bool enabled)
    {
        var bg = !enabled ? new Color(120, 110, 100) : hover ? new Color(175, 115, 55) : new Color(139, 90, 43);
        IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(432, 439, 9, 9),
            rect.X, rect.Y, rect.Width, rect.Height, bg, 4f, false);
        var sz = CustomFontManager.MeasureStringBold(label, CustomFontManager.SizeRegular);
        CustomFontManager.DrawStringBold(b, label,
            new Vector2(rect.X + (rect.Width - sz.X) / 2f, rect.Y + (rect.Height - sz.Y) / 2f),
            enabled ? Color.White : Color.White * 0.5f, CustomFontManager.SizeRegular);
    }

    private int VisibleCount(Rectangle area) => Math.Max(0, (area.Height - 12) / (RowHeight + RowGap));

    // ── 基线缓存（OnShown 一次）─────────────────────────────────────────

    private void BuildBaselineCache()
    {
        _baselineLocations.Clear();
        _baselineRegions.Clear();
        _baselineFestivals.Clear();
        _baselineAssetError = null;

        GameSummary? summary = LoadBaseline();
        if (summary == null) return;

        if (summary.Locations?.Entries != null)
            foreach (var kv in summary.Locations.Entries)
                if (!string.IsNullOrEmpty(kv.Key) && kv.Value != null)
                {
                    _baselineLocations[kv.Key] = string.IsNullOrEmpty(kv.Value.Name) ? kv.Key : kv.Value.Name;
                    _baselineRegions[kv.Key] = kv.Value.Region ?? "";
                }

        if (summary.Festivals?.Entries != null)
            foreach (var kv in summary.Festivals.Entries)
                if (!string.IsNullOrEmpty(kv.Key) && kv.Value != null)
                    _baselineFestivals[kv.Key] = string.IsNullOrEmpty(kv.Value.Name) ? kv.Key : kv.Value.Name;
    }

    private GameSummary? LoadBaseline()
    {
        try
        {
            var loaded = ModEntry.SHelper.GameContent.Load<GameSummary>(VtConstants.GameSummaryPath);
            if (loaded == null)
            {
                _baselineAssetError = "基线资产返回 null";
                ModEntry.SMonitor?.Log("[LocationFestival] 基线资产(GameSummary)返回 null", LogLevel.Warn);
            }
            return loaded;
        }
        catch (Exception ex)
        {
            _baselineAssetError = ex.Message;
            ModEntry.SMonitor?.Log($"[LocationFestival] 基线资产加载失败: {ex.Message}", LogLevel.Warn);
            return null;
        }
    }

    // ── 地点区 ───────────────────────────────────────────────────────

    private void RebuildLocationView()
    {
        _locationEntries.Clear();
        foreach (var id in _baselineLocations.Keys)
            _locationEntries.Add(new LocationListEntry { Id = id, Name = _baselineLocations[id], Region = _baselineRegions.GetValueOrDefault(id, "") });
        _locationEntries.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
        _locScroll = ClampScroll(_locScroll, _locationEntries.Count, _locListArea);
    }

    private void HandleLocListClick(int x, int y)
    {
        int idx = (y - _locListArea.Y - 6) / (RowHeight + RowGap) + _locScroll;
        if (idx >= 0 && idx < _locationEntries.Count)
            SelectLocation(_locationEntries[idx].Id);
    }

    private void SelectLocation(string id)
    {
        _selectedLocId = id;
        _locError = null;
        _locStatus = null;
        if (!_baselineLocations.ContainsKey(id)) { _locDescBox.Text = ""; return; }

        var ov = ModEntry.WorldSummaryOverlay?.LoadOrNull();
        string desc = "";
        if (ov != null && ov.LocationDescriptions.TryGetValue(id, out var d)) desc = d;
        _locDescBox.Text = desc;
        DeselectAllBoxes();
    }

    private bool CanSaveLocation => !string.IsNullOrEmpty(_selectedLocId) && _baselineLocations.ContainsKey(_selectedLocId);

    private void SaveLocation()
    {
        if (!CanSaveLocation) return;
        var service = ModEntry.WorldSummaryOverlay;
        if (service == null) { _locError = "服务不可用"; return; }
        var ov = service.LoadOrNull() ?? new WorldSummaryOverlayFile();
        ov.LocationDescriptions[_selectedLocId!] = _locDescBox.Text.Length > DescMaxChars ? _locDescBox.Text.Substring(0, DescMaxChars) : _locDescBox.Text;
        ov.RemovedLocationDescriptionIds.Remove(_selectedLocId!);
        if (service.Save(ov, out string err))
        {
            _locStatus = I18n.WorldSettings.Saved();
            _locError = null;
            Hub.RefreshEntries();
        }
        else { _locError = string.IsNullOrEmpty(err) ? "保存失败" : err; _locStatus = null; }
    }

    private void RevertLocation()
    {
        if (_selectedLocId == null) return;
        var service = ModEntry.WorldSummaryOverlay;
        if (service == null) { _locError = "服务不可用"; return; }
        var ov = service.LoadOrNull() ?? new WorldSummaryOverlayFile();
        ov.LocationDescriptions.Remove(_selectedLocId!);
        if (!ov.RemovedLocationDescriptionIds.Contains(_selectedLocId!))
            ov.RemovedLocationDescriptionIds.Add(_selectedLocId!);
        if (service.Save(ov, out string err))
        {
            _locStatus = I18n.WorldSettings.SavedLoc();
            RebuildLocationView();
            SelectLocation(_selectedLocId!);
            Hub.RefreshEntries();
        }
        else { _locError = string.IsNullOrEmpty(err) ? "还原失败" : err; _locStatus = null; }
    }

    private void SelectLocDesc()
    {
        DeselectAllBoxes();
        _locDescBox.Selected = true;
        Game1.keyboardDispatcher.Subscriber = _locDescBox;
    }

    // ── 节日区 ───────────────────────────────────────────────────────

    private void RebuildFestivalView()
    {
        _festivalEntries.Clear();
        var ov = ModEntry.WorldSummaryOverlay?.LoadOrNull();
        var overlayKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (ov != null)
        {
            foreach (var kv in ov.Festivals)
            {
                overlayKeys.Add(kv.Key);
                var isBaseline = _baselineFestivals.ContainsKey(kv.Key);
                var entry = new FestivalListEntry
                {
                    Key = kv.Key,
                    Name = kv.Value.Names.TryGetValue(LangKey, out var n) ? n : (isBaseline ? _baselineFestivals[kv.Key] : kv.Key),
                    IsBaseline = isBaseline,
                    IsCustom = kv.Value.IsCustomDate && !isBaseline,
                };
                _festivalEntries.Add(entry);
            }
            // 还原态（RemovedFestivalKeys）= 基线被删除 → 标为墓碑显示
            foreach (var key in ov.RemovedFestivalKeys)
            {
                if (_baselineFestivals.ContainsKey(key))
                {
                    var entry = _festivalEntries.Find(e => string.Equals(e.Key, key, StringComparison.OrdinalIgnoreCase));
                    if (entry != null) entry.IsTombstone = true;
                }
            }
        }

        // 基线中存在但覆盖层未触及的节日
        foreach (var key in _baselineFestivals.Keys)
        {
            if (overlayKeys.Contains(key)) continue;
            _festivalEntries.Add(new FestivalListEntry { Key = key, Name = _baselineFestivals[key], IsBaseline = true });
        }

        _festivalEntries.Sort((a, b) => string.Compare(a.Key, b.Key, StringComparison.Ordinal));
        _festScroll = ClampScroll(_festScroll, _festivalEntries.Count, _festListArea);
    }

    private void HandleFestListClick(int x, int y)
    {
        int idx = (y - _festListArea.Y - 6) / (RowHeight + RowGap) + _festScroll;
        if (idx >= 0 && idx < _festivalEntries.Count)
            SelectFestival(_festivalEntries[idx].Key);
    }

    private void SelectFestival(string key)
    {
        _selectedFestKey = key;
        _festError = null;
        _festStatus = null;
        var ov = ModEntry.WorldSummaryOverlay?.LoadOrNull();
        CustomFestivalEntry? entry = null;
        if (ov != null) ov.Festivals.TryGetValue(key, out entry);

        bool isBaseline = _baselineFestivals.ContainsKey(key);
        if (entry != null)
        {
            _festNameBoxZh.Text = entry.Names.TryGetValue("zh", out var nameZh) ? nameZh : "";
            _festNameBoxEn.Text = entry.Names.TryGetValue("en", out var nameEn) ? nameEn : "";
            _festDescBoxZh.Text = entry.Descriptions.TryGetValue("zh", out var descZh) ? descZh : "";
            _festDescBoxEn.Text = entry.Descriptions.TryGetValue("en", out var descEn) ? descEn : "";
            _newSeason = key.Length > 1 ? ExtractSeason(key) : "spring";
            _newDay = ExtractDay(key);
        }
        else
        {
            _festNameBoxZh.Text = _festNameBoxEn.Text = _festDescBoxZh.Text = _festDescBoxEn.Text = "";
        }
        RefreshSeasonDropdown();
        DeselectAllBoxes();
    }

    private void RefreshSeasonDropdown()
    {
        var items = new (string Id, string Label)[] { ("spring", "Spring"), ("summer", "Summer"), ("fall", "Fall"), ("winter", "Winter") };
        _seasonDropdown.SetItems(items, _newSeason);
    }

    // 自创纪念日保存约束：描述与名称均须至少一语言非空（空名会被 T6 运行时静默跳过，须源头阻断）
    private bool CanSaveFestival =>
        !string.IsNullOrEmpty(_selectedFestKey) &&
        (!string.IsNullOrWhiteSpace(_festDescBoxZh.Text) || !string.IsNullOrWhiteSpace(_festDescBoxEn.Text)) &&
        (!string.IsNullOrWhiteSpace(_festNameBoxZh.Text) || !string.IsNullOrWhiteSpace(_festNameBoxEn.Text));

    // 自创纪念日校验提示：描述已填但名称全空时，给出状态栏提示（空名会被 T6 运行时静默跳过）
    private string? FestivalValidationHint()
    {
        if (CanSaveFestival || string.IsNullOrEmpty(_selectedFestKey)) return null;
        bool hasDesc = !string.IsNullOrWhiteSpace(_festDescBoxZh.Text) || !string.IsNullOrWhiteSpace(_festDescBoxEn.Text);
        bool hasName = !string.IsNullOrWhiteSpace(_festNameBoxZh.Text) || !string.IsNullOrWhiteSpace(_festNameBoxEn.Text);
        if (hasDesc && !hasName) return IsZh ? "名称至少填写一种语言" : "Name required in at least one language";
        return null;
    }

    private bool CanDeleteFestival
    {
        get
        {
            if (_selectedFestKey == null) return false;
            var e = _festivalEntries.Find(x => string.Equals(x.Key, _selectedFestKey, StringComparison.OrdinalIgnoreCase));
            return e != null && e.IsCustom && !e.IsBaseline; // 仅删除覆盖层自有自创条目
        }
    }

    private void SaveFestival()
    {
        if (!CanSaveFestival) return;
        var service = ModEntry.WorldSummaryOverlay;
        if (service == null) { _festError = "服务不可用"; return; }
        string key = _selectedFestKey ?? "";
        if (string.IsNullOrEmpty(key)) return;

        var ov = service.LoadOrNull() ?? new WorldSummaryOverlayFile();

        // 冲突提示（确定性共存，不阻断）
        if (_baselineFestivals.ContainsKey(key))
            _festStatus = I18n.WorldSettings.VanillaCoexistWarning();

        CustomFestivalEntry entry;
        bool existed = ov.Festivals.TryGetValue(key, out var existing);
        if (existed) entry = existing;
        else
        {
            entry = new CustomFestivalEntry();
            ov.Festivals[key] = entry;
        }

        // 写当前语言键，保留他语言键
        if (entry.Names == null) entry.Names = new Dictionary<string, string>();
        if (entry.Descriptions == null) entry.Descriptions = new Dictionary<string, string>();
        entry.Names[LangKey] = _festNameBoxZh.Text;
        entry.Descriptions[LangKey] = _festDescBoxZh.Text.Length > DescMaxChars ? _festDescBoxZh.Text.Substring(0, DescMaxChars) : _festDescBoxZh.Text;
        // 自创 = 键不在基线；编辑原版节日时 IsCustomDate=false
        entry.IsCustomDate = !_baselineFestivals.ContainsKey(key);

        ov.RemovedFestivalKeys.Remove(key);

        if (service.Save(ov, out string err))
        {
            _festStatus = I18n.WorldSettings.SavedLoc();
            _festError = null;
            RebuildFestivalView();
            Hub.RefreshEntries();
        }
        else { _festError = string.IsNullOrEmpty(err) ? "保存失败" : err; _festStatus = null; }
    }

    private bool IsCustomSelected()
    {
        // 当前编辑的 key 由季节/日期步进器决定；若与基线键不同则视为自创
        string newKey;
        try { newKey = FestivalKeyBuilder.BuildFestivalKey(_newSeason, _newDay); }
        catch { return false; }
        return !_baselineFestivals.ContainsKey(newKey) && !string.Equals(newKey, _selectedFestKey, StringComparison.OrdinalIgnoreCase) == false ? true : !_baselineFestivals.ContainsKey(newKey);
    }

    private void DeleteFestival()
    {
        if (!CanDeleteFestival) return;
        if (_selectedFestKey == null) return;
        var service = ModEntry.WorldSummaryOverlay;
        if (service == null) { _festError = "服务不可用"; return; }
        var ov = service.LoadOrNull() ?? new WorldSummaryOverlayFile();
        ov.Festivals.Remove(_selectedFestKey);
        if (service.Save(ov, out string err))
        {
            _festStatus = I18n.WorldSettings.SavedLoc();
            RebuildFestivalView();
            SelectFirstFestival();
            Hub.RefreshEntries();
        }
        else { _festError = string.IsNullOrEmpty(err) ? "删除失败" : err; _festStatus = null; }
    }

    private void NewFestival()
    {
        string newKey;
        try { newKey = FestivalKeyBuilder.BuildFestivalKey(_newSeason, _newDay); }
        catch (ArgumentOutOfRangeException ex) { _festError = $"{ex.ParamName} 非法"; return; }

        _selectedFestKey = newKey;
        _festNameBoxZh.Text = _festNameBoxEn.Text = "";
        _festDescBoxZh.Text = _festDescBoxEn.Text = "";
        _festStatus = IsZh ? "编辑后保存" : "Edit then save";
        DeselectAllBoxes();
        if (_baselineFestivals.ContainsKey(newKey))
            _festStatus = I18n.WorldSettings.VanillaCoexistWarning();
    }

    private void SelectFirstFestival()
    {
        if (_festivalEntries.Count > 0) SelectFestival(_festivalEntries[0].Key);
        else { _selectedFestKey = null; }
    }

    // ── 键盘 ──────────────────────────────────────────────────────────

    private void SelectFestBox(MultilineTextBox tb)
    {
        DeselectAllBoxes();
        tb.Selected = true;
        Game1.keyboardDispatcher.Subscriber = tb;
    }

    private void DeselectAllBoxes()
    {
        foreach (var tb in new[] { _locDescBox, _festNameBoxZh, _festNameBoxEn, _festDescBoxZh, _festDescBoxEn })
        {
            tb.Selected = false;
            if (Game1.keyboardDispatcher.Subscriber == tb)
                Game1.keyboardDispatcher.Subscriber = null;
        }
        _seasonDropdown.Close();
    }

    private bool AnyTextboxSelected()
    {
        return new[] { _locDescBox, _festNameBoxZh, _festNameBoxEn, _festDescBoxZh, _festDescBoxEn }.Any(tb => tb.Selected);
    }

    // ── 工具 ──────────────────────────────────────────────────────────

    private static string ExtractSeason(string key)
    {
        foreach (var s in new[] { "spring", "summer", "fall", "winter" })
            if (key.StartsWith(s, StringComparison.OrdinalIgnoreCase)) return s;
        return "spring";
    }

    private static int ExtractDay(string key)
    {
        string suffix = new string(key.SkipWhile(c => !char.IsDigit(c)).ToArray());
        return int.TryParse(suffix, out int d) ? d : 1;
    }
}
