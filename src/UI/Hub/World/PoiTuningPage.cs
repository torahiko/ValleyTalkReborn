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
using ValleytalkReborn.Services;
using ValleytalkReborn.Services.Overlays;

namespace ValleytalkReborn.UI;

/// <summary>兴趣点调谐子页：NPC 下拉 + 合并 POI 目录 + 每 NPC 权重步进表 + 抓取/传送踩点。</summary>
internal sealed class PoiTuningPage : WorldSubPageBase
{
    private const int RowHeight = 28;
    private const int RowGap = 4;
    private const int FormTopPad = 12;
    private const int LabelW = 150;
    private const int CtrlH = 30;
    private const int CtrlGap = 8;
    private const int BtnH = 30;
    private const int BtnGap = 6;
    private const int DescMaxChars = 500;
    private const int ListWidthPercent = 36;
    private const int DefaultWeight = 50;

    // ── 基线 POI 缓存（OnShown 一次）────────────────────────────────────
    private Dictionary<string, string> _baselinePoiNames = new(StringComparer.OrdinalIgnoreCase); // id -> MapName
    private string? _baselineAssetError;

    // ── NPC 候选 ──────────────────────────────────────────────────────
    private List<(string Id, string DisplayName)> _candidateItems = new();
    private string? _selectedNpc;

    // ── 合并 POI 目录 ──────────────────────────────────────────────────
    private readonly List<PoiListEntry> _entries = new();
    private int _listScroll;
    private string? _selectedPoiId;
    private Rectangle _listArea;
    private Rectangle _formArea;

    private sealed class PoiListEntry
    {
        public string Id = "";
        public string MapName = "";
        public string Summary = "";
        public bool IsCustom;
    }

    // ── 权重工作副本（全量，含默认 50）────────────────────────────────
    private Dictionary<string, int> _workingWeights = new(StringComparer.OrdinalIgnoreCase);
    private string? _captureMapName;
    private int _captureTileX;
    private int _captureTileY;
    private bool _hasCapture;

    // ── 抓取/新建 POI 表单 ────────────────────────────────────────────
    private string _newDescZh = "";
    private string _newDescEn = "";

    // ── 控件 ──────────────────────────────────────────────────────────
    private readonly DropdownList _npcDropdown;
    private readonly MultilineTextBox _newDescBoxZh = new(Rectangle.Empty, 3);
    private readonly MultilineTextBox _newDescBoxEn = new(Rectangle.Empty, 3);
    private readonly Dictionary<string, NumberStepper> _weightSteppersById = new(StringComparer.OrdinalIgnoreCase);
    private Rectangle _btnSave;
    private Rectangle _btnRevert;
    private Rectangle _btnCapture;
    private Rectangle _btnDeletePoi;
    private Rectangle _btnTeleport;
    private Rectangle _btnNewPoi;

    private string? _saveError;
    private string? _statusMessage;
    private bool IsWorldReady => Context.IsWorldReady && Game1.currentLocation != null && Game1.player != null;
    private bool IsZh => LocalizedContentManager.CurrentLanguageCode == LocalizedContentManager.LanguageCode.zh;

    public PoiTuningPage(IntegratedHubMenu hub) : base(hub)
    {
        _npcDropdown = new DropdownList(Rectangle.Empty, CtrlH, 8) { HeaderPrefix = "" };
    }

    public override void OnShown()
    {
        BuildBaselineCache();
        BuildCandidates();
        SelectInitialNpc();
        RefreshCaptureState();
    }

    public override void Layout(Rectangle area)
    {
        int x = area.X;
        int y = area.Y;
        int w = area.Width;
        int listW = w * ListWidthPercent / 100;

        _listArea = new Rectangle(x, y, listW, area.Height);
        _formArea = new Rectangle(x + listW + 16, y, w - listW - 16, area.Height);
        LayoutForm();
    }

    private void LayoutForm()
    {
        int x = _formArea.X + 8;
        int y = _formArea.Y + FormTopPad;
        int w = _formArea.Width - 16;
        int ctrlW = w - 16;

        _npcDropdown.SetHeaderBounds(new Rectangle(x, y, ctrlW, CtrlH)); y += CtrlH + CtrlGap + 4;

        // 抓取/新建表单
        _newDescBoxZh.SetBounds(new Rectangle(x, y, ctrlW, 48)); y += 52;
        _newDescBoxEn.SetBounds(new Rectangle(x, y, ctrlW, 48)); y += 52;

        int btnW = (w - BtnGap * 2) / 3;
        _btnCapture = new Rectangle(x, y, btnW, BtnH);
        _btnNewPoi = new Rectangle(x + btnW + BtnGap, y, btnW, BtnH);
        _btnTeleport = new Rectangle(x + (btnW + BtnGap) * 2, y, btnW, BtnH);
        y += BtnH + CtrlGap;

        // 删除选中 POI
        _btnDeletePoi = new Rectangle(x, y, btnW, BtnH);
        y += BtnH + CtrlGap + 4;

        // 权重保存/还原
        int bottomBtnW = (w - BtnGap) / 2;
        _btnSave = new Rectangle(x, _formArea.Bottom - BtnH - 8, bottomBtnW, BtnH);
        _btnRevert = new Rectangle(x + bottomBtnW + BtnGap, _formArea.Bottom - BtnH - 8, bottomBtnW, BtnH);
    }

    public override void Update(GameTime time)
    {
        foreach (var s in _weightSteppersById.Values) s.Value = s.Value; // no-op kept for symmetry
        _newDescBoxZh.Update(time);
        _newDescBoxEn.Update(time);
    }

    public override bool ReceiveLeftClick(int x, int y)
    {
        if (_listArea.Contains(x, y)) { HandleListClick(x, y); return true; }

        if (_npcDropdown.ReceiveLeftClick(x, y)) { SyncNpc(); return true; }

        if (_newDescBoxZh.Bounds.Contains(x, y)) { SelectBox(_newDescBoxZh); return true; }
        if (_newDescBoxEn.Bounds.Contains(x, y)) { SelectBox(_newDescBoxEn); return true; }

        if (_npcDropdown.IsOpen) { _npcDropdown.Close(); return true; }

        if (_btnCapture.Contains(x, y) && IsWorldReady) { CaptureCurrentTile(); return true; }
        if (_btnNewPoi.Contains(x, y) && CanCreateNewPoi) { CreateNewPoi(); return true; }
        if (_btnTeleport.Contains(x, y) && CanTeleport) { TeleportToSelected(); return true; }
        if (_btnDeletePoi.Contains(x, y) && _selectedPoiId != null) { DeleteSelectedPoi(); return true; }
        if (_btnSave.Contains(x, y)) { SaveWeights(); return true; }
        if (_btnRevert.Contains(x, y) && _selectedNpc != null) { RevertNpc(); return true; }

        return false;
    }

    public override bool ReceiveScrollWheel(int direction)
    {
        int mx = Game1.getMouseX(), my = Game1.getMouseY();
        if (_listArea.Contains(mx, my))
        {
            int visible = VisibleCount(_listArea);
            _listScroll = Math.Clamp(_listScroll - direction, 0, Math.Max(0, _entries.Count - visible));
            return true;
        }
        if (_npcDropdown.ReceiveScrollWheel(direction)) return true;
        foreach (var tb in new[] { _newDescBoxZh, _newDescBoxEn })
            if (tb.Selected) { tb.Scroll(direction); return true; }
        if (_npcDropdown.IsOpen) return true;
        return false;
    }

    public override bool ReceiveKeyPress(Keys key)
    {
        if (IsActiveBoxSelected() && Game1.keyboardDispatcher.Subscriber is MultilineTextBox)
        {
            if (key == Keys.Escape) { DeselectAllBoxes(); return true; }
            return false;
        }
        if (_npcDropdown.IsOpen && key == Keys.Escape) { _npcDropdown.Close(); return true; }
        return false;
    }

    public override void OnHidden() => DeselectAllBoxes();

    // ── 绘制 ──────────────────────────────────────────────────────────

    public override void Draw(SpriteBatch b, Rectangle area, int mx, int my)
    {
        DrawList(b, mx, my);
        DrawForm(b, mx, my);
    }

    private void DrawList(SpriteBatch b, int mx, int my)
    {
        b.Draw(Game1.staminaRect, _listArea, new Color(0, 0, 0) * 0.04f);
        int y = _listArea.Y + 6;
        int end = Math.Min(_entries.Count, _listScroll + VisibleCount(_listArea));
        for (int i = _listScroll; i < end; i++)
        {
            var e = _entries[i];
            var row = new Rectangle(_listArea.X + 4, y, _listArea.Width - 8, RowHeight);
            bool selected = string.Equals(e.Id, _selectedPoiId, StringComparison.OrdinalIgnoreCase);
            bool hover = row.Contains(mx, my);
            if (selected) b.Draw(Game1.staminaRect, row, new Color(210, 180, 140));
            else if (hover) b.Draw(Game1.staminaRect, row, new Color(255, 235, 205));
            string tag = e.IsCustom ? " [+]" : "";
            string label = $"{e.Id} ({e.MapName}){tag}";
            CustomFontManager.DrawString(b, CustomFontManager.TruncateString(label, CustomFontManager.SizeRegular, row.Width - 12),
                new Vector2(row.X + 6, row.Y + 5), selected ? Game1.textColor : Color.White * 0.92f, CustomFontManager.SizeRegular);
            y += RowHeight + RowGap;
        }
    }

    private void DrawForm(SpriteBatch b, int mx, int my)
    {
        b.Draw(Game1.staminaRect, _formArea, new Color(0, 0, 0) * 0.04f);
        int x = _formArea.X + 8;
        int y = _formArea.Y + FormTopPad;
        int w = _formArea.Width - 16;
        int ctrlW = w - 16;

        CustomFontManager.DrawStringBold(b, IsZh ? "NPC" : "NPC", new Vector2(x, y + 6), Game1.textColor, CustomFontManager.SizeRegular);
        _npcDropdown.Draw(b);
        y += CtrlH + CtrlGap + 4;

        // 抓取提示
        string captureLabel = _hasCapture
            ? (IsZh ? $"已抓取: {_captureMapName} ({_captureTileX},{_captureTileY})" : $"Captured: {_captureMapName} ({_captureTileX},{_captureTileY})")
            : (IsZh ? "尚未抓取坐标" : "No tile captured");
        CustomFontManager.DrawString(b, captureLabel, new Vector2(x, y - 2), Color.White * 0.8f, CustomFontManager.SizeSmall);

        _newDescBoxZh.Draw(b); y += 52;
        _newDescBoxEn.Draw(b); y += 52;

        int btnW = (w - BtnGap * 2) / 3;
        DrawButton(b, _btnCapture, I18n.WorldSettings.Capture(), _btnCapture.Contains(mx, my), IsWorldReady);
        DrawButton(b, _btnNewPoi, I18n.WorldSettings.NewPoi(), _btnNewPoi.Contains(mx, my), CanCreateNewPoi);
        DrawButton(b, _btnTeleport, I18n.WorldSettings.Teleport(), _btnTeleport.Contains(mx, my), CanTeleport);
        y += BtnH + CtrlGap;

        // 权重表
        DrawLabel(b, I18n.WorldSettings.WeightsPerNpc(), x, _btnDeletePoi.Y - 4);
        DrawButton(b, _btnDeletePoi, I18n.WorldSettings.DeletePoi(), _btnDeletePoi.Contains(mx, my), CanDeleteSelected);
        DrawWeightTable(b, mx, my, ref y);

        DrawButton(b, _btnSave, I18n.WorldSettings.SaveWeights(), _btnSave.Contains(mx, my), true);
        DrawButton(b, _btnRevert, I18n.WorldSettings.RevertNpc(), _btnRevert.Contains(mx, my), _selectedNpc != null);

        string? msg = _saveError ?? _statusMessage;
        if (msg != null)
            CustomFontManager.DrawString(b, msg, new Vector2(x, _formArea.Bottom - 56),
                _saveError != null ? Color.Red : new Color(60, 130, 60), CustomFontManager.SizeSmall);
    }

    private void DrawWeightTable(SpriteBatch b, int mx, int my, ref int y)
    {
        if (_selectedNpc == null || _entries.Count == 0)
        {
            DrawLabel(b, I18n.WorldSettings.SelectNpcFirst(), x: _formArea.X + 8, y: y);
            return;
        }
        // 同步步进器集合
        SyncWeightSteppers();
        foreach (var entry in _entries)
        {
            string id = entry.Id;
            if (!_weightSteppersById.TryGetValue(id, out var stepper)) continue;
            int rowY = y;
            var label = CustomFontManager.TruncateString(id, CustomFontManager.SizeSmall, 130f);
            CustomFontManager.DrawString(b, label, new Vector2(_formArea.X + 8, rowY + 6), Color.White * 0.85f, CustomFontManager.SizeSmall);
            stepper.SetBounds(new Rectangle(_formArea.X + 8 + 138, rowY, _formArea.Width - 16 - 138, CtrlH - 2));
            stepper.Draw(b);
            y += CtrlH + 2;
        }
    }

    private void DrawLabel(SpriteBatch b, string text, int x, int y) =>
        CustomFontManager.DrawStringBold(b, text, new Vector2(x, y), Game1.textColor, CustomFontManager.SizeSmall);

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

    // ── 缓存构建（OnShown 一次）─────────────────────────────────────────

    private void BuildBaselineCache()
    {
        _baselinePoiNames.Clear();
        _baselineAssetError = null;
        Dictionary<string, PoiAsset> baseline;
        try
        {
            baseline = ModEntry.SHelper.GameContent.Load<Dictionary<string, PoiAsset>>(PoiRepository.POI_ASSET_KEY)
                      ?? new Dictionary<string, PoiAsset>(StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            _baselineAssetError = ex.Message;
            ModEntry.SMonitor?.Log($"[PoiTuning] POI 基线加载失败: {ex.Message}", LogLevel.Warn);
            baseline = new Dictionary<string, PoiAsset>(StringComparer.OrdinalIgnoreCase);
        }
        foreach (var kv in baseline)
            if (!string.IsNullOrEmpty(kv.Key))
                _baselinePoiNames[kv.Key] = string.IsNullOrEmpty(kv.Value.MapName) ? kv.Key : kv.Value.MapName;
    }

    private void BuildCandidates()
    {
        _candidateItems.Clear();
        try { _candidateItems = NpcCandidateQueryService.GetCleanedCandidates(); }
        catch (Exception ex) { ModEntry.SMonitor?.Log($"[PoiTuning] 候选集加载失败: {ex.Message}", LogLevel.Warn); }
    }

    private void SelectInitialNpc()
    {
        string? initial = Hub.CurrentNpcName;
        if (!string.IsNullOrEmpty(initial) && _candidateItems.Any(c => string.Equals(c.Id, initial, StringComparison.OrdinalIgnoreCase)))
            _selectedNpc = initial;
        else if (_candidateItems.Count > 0)
            _selectedNpc = _candidateItems[0].Id;
        else
            _selectedNpc = null;
        RefreshNpcDropdown();
        LoadWorkingWeightsForNpc();
    }

    private void SyncNpc()
    {
        if (!string.IsNullOrEmpty(_npcDropdown.SelectedId) && !string.Equals(_npcDropdown.SelectedId, _selectedNpc, StringComparison.OrdinalIgnoreCase))
        {
            _selectedNpc = _npcDropdown.SelectedId;
            LoadWorkingWeightsForNpc();
        }
    }

    private void RefreshNpcDropdown()
    {
        _npcDropdown.SetItems(_candidateItems, _selectedNpc ?? "");
    }

    private void RefreshCaptureState()
    {
        _hasCapture = false;
        _captureMapName = null;
        _captureTileX = 0;
        _captureTileY = 0;
    }

    private void RebuildMergedView()
    {
        _entries.Clear();
        var ov = ModEntry.PoiPreferenceOverlay?.LoadOrNull();
        var customKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (ov?.CustomPois != null)
            foreach (var id in ov.CustomPois.Keys)
                customKeys.Add(id);

        foreach (var id in _baselinePoiNames.Keys)
        {
            if (ov?.RemovedCustomPoiIds != null && ov.RemovedCustomPoiIds.Contains(id, StringComparer.OrdinalIgnoreCase))
                continue; // 基线 POI 不走 RemovedCustomPoiIds（仅自定义用）；保留显示
            _entries.Add(new PoiListEntry { Id = id, MapName = _baselinePoiNames[id], Summary = "", IsCustom = false });
        }
        if (ov?.CustomPois != null)
        {
            foreach (var kv in ov.CustomPois)
            {
                if (ov.RemovedCustomPoiIds != null && ov.RemovedCustomPoiIds.Contains(kv.Key, StringComparer.OrdinalIgnoreCase))
                    continue;
                string summary = kv.Value.DescriptionForLLM_Zh ?? kv.Value.DescriptionForLLM ?? "";
                _entries.Add(new PoiListEntry { Id = kv.Key, MapName = kv.Value.MapName, Summary = summary, IsCustom = true });
            }
        }
        _entries.Sort((a, b) => string.Compare(a.Id, b.Id, StringComparison.OrdinalIgnoreCase));
        int visible = VisibleCount(_listArea);
        _listScroll = Math.Clamp(_listScroll, 0, Math.Max(0, _entries.Count - visible));
    }

    private void HandleListClick(int x, int y)
    {
        int idx = (y - _listArea.Y - 6) / (RowHeight + RowGap) + _listScroll;
        if (idx >= 0 && idx < _entries.Count)
            SelectPoi(_entries[idx].Id);
    }

    private void SelectPoi(string id)
    {
        _selectedPoiId = id;
        _saveError = null;
        _statusMessage = null;
    }

    private void SyncWeightSteppers()
    {
        if (_selectedNpc == null) return;
        var existing = new HashSet<string>(_weightSteppersById.Keys, StringComparer.OrdinalIgnoreCase);
        foreach (var entry in _entries)
        {
            if (existing.Contains(entry.Id)) continue;
            int w = _workingWeights.TryGetValue(entry.Id, out var val) ? val : DefaultWeight;
            _weightSteppersById[entry.Id] = new NumberStepper(Rectangle.Empty, w, 0, 100, 1, "");
        }
        // 移除已不在目录的步进器
        foreach (var id in _weightSteppersById.Keys.ToList())
            if (!_entries.Any(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase)))
                _weightSteppersById.Remove(id);
    }

    private void LoadWorkingWeightsForNpc()
    {
        _workingWeights.Clear();
        if (_selectedNpc == null) return;
        // 默认全 50
        foreach (var entry in _entries)
            _workingWeights[entry.Id] = DefaultWeight;

        var ov = ModEntry.PoiPreferenceOverlay?.LoadOrNull();
        NpcPreference? pref = null;
        bool overlayHasNpc = ov?.NpcPreferences != null && ov.NpcPreferences.TryGetValue(_selectedNpc, out pref) && pref?.PreferredPois != null;
        if (overlayHasNpc)
        {
            // 覆盖层有该 NPC → 整体覆盖基线：以覆盖层列表为准，未列出的 POI 不写入（整体覆盖语义）
            _workingWeights.Clear();
            foreach (var p in pref.PreferredPois)
                if (!string.IsNullOrEmpty(p.PoiId))
                    _workingWeights[p.PoiId] = Math.Clamp(p.Weight, 0, 100);
        }
        // 基线 SchedulePlanner 资产不再被本页直接读取——覆盖层整体覆盖语义下，未配置的 POI 不进入该 NPC 的加权池
    }

    // ── 抓取 / 新建 / 传送 ────────────────────────────────────────────

    private bool CanCreateNewPoi => _hasCapture && !string.IsNullOrWhiteSpace(_newDescBoxZh.Text) && !string.IsNullOrWhiteSpace(_newDescBoxEn.Text);

    private bool CanTeleport
    {
        get
        {
            if (_selectedPoiId == null) return false;
            var e = _entries.Find(x => string.Equals(x.Id, _selectedPoiId, StringComparison.OrdinalIgnoreCase));
            if (e == null) return false;
            // 目标地图必须在采集列表内（防御校验）
            return IsMapAvailable(e.MapName);
        }
    }

    private bool CanDeleteSelected
    {
        get
        {
            if (_selectedPoiId == null) return false;
            var e = _entries.Find(x => string.Equals(x.Id, _selectedPoiId, StringComparison.OrdinalIgnoreCase));
            return e != null && e.IsCustom; // 仅删除自定义 POI
        }
    }

    private bool IsMapAvailable(string mapName)
    {
        try { return DynamicAssetQueryService.GetAvailableMaps().Any(m => string.Equals(m.Id, mapName, StringComparison.OrdinalIgnoreCase)); }
        catch { return false; }
    }

    private void CaptureCurrentTile()
    {
        if (!IsWorldReady) return;
        if (PoiSamplingService.TryCaptureCurrentTile(out string mapName, out int tx, out int ty, out string reason))
        {
            _captureMapName = mapName;
            _captureTileX = tx;
            _captureTileY = ty;
            _hasCapture = true;
            _statusMessage = IsZh ? $"已抓取 {mapName} ({tx},{ty})" : $"Captured {mapName} ({tx},{ty})";
            _saveError = null;
        }
        else
        {
            _saveError = reason;
            _statusMessage = null;
        }
    }

    private void CreateNewPoi()
    {
        if (!CanCreateNewPoi || _captureMapName == null) return;
        var service = ModEntry.PoiPreferenceOverlay;
        if (service == null) { _saveError = "服务不可用"; return; }
        var ov = service.LoadOrNull() ?? new PoiOverlayFile();

        var existingIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in ov.CustomPois.Keys) existingIds.Add(id);
        string newId = CustomPoiIdBuilder.BuildCustomPoiId($"POI_{_captureMapName}_{_captureTileX}_{_captureTileY}", existingIds);

        ov.CustomPois[newId] = new PoiAsset
        {
            MapName = _captureMapName,
            TargetTile = new PoiTile { X = _captureTileX, Y = _captureTileY },
            Conditions = new PoiConditions(),
            StayMinutes = 90,
            DescriptionForLLM = _newDescBoxEn.Text.Length > DescMaxChars ? _newDescBoxEn.Text.Substring(0, DescMaxChars) : _newDescBoxEn.Text,
            DescriptionForLLM_Zh = _newDescBoxZh.Text.Length > DescMaxChars ? _newDescBoxZh.Text.Substring(0, DescMaxChars) : _newDescBoxZh.Text,
        };
        ov.RemovedCustomPoiIds.Remove(newId);

        if (service.Save(ov, out string err))
        {
            _statusMessage = IsZh ? $"已新建 {newId}" : $"Created {newId}";
            _saveError = null;
            _newDescBoxZh.Text = "";
            _newDescBoxEn.Text = "";
            RefreshCaptureState();
            RebuildMergedView();
            Hub.RefreshEntries();
        }
        else { _saveError = string.IsNullOrEmpty(err) ? "保存失败" : err; _statusMessage = null; }
    }

    private void TeleportToSelected()
    {
        if (!CanTeleport || _selectedPoiId == null) return;
        var e = _entries.Find(x => string.Equals(x.Id, _selectedPoiId, StringComparison.OrdinalIgnoreCase));
        if (e == null) return;
        if (!IsMapAvailable(e.MapName)) { _saveError = IsZh ? "目标地图不在采集列表" : "Target map unavailable"; return; }

        var poi = ModEntry.PoiPreferenceOverlay?.LoadOrNull()?.CustomPois?.GetValueOrDefault(e.Id);
        int tx = poi?.TargetTile?.X ?? 0;
        int ty = poi?.TargetTile?.Y ?? 0;

        try
        {
            Game1.exitActiveMenu();
            Game1.warpFarmer(e.MapName, tx, ty, 2);
        }
        catch (Exception ex)
        {
            ModEntry.SMonitor?.Log($"[PoiTuning] 传送失败({e.MapName} {tx},{ty}): {ex.Message}", LogLevel.Warn);
            _saveError = IsZh ? "传送失败，请重新打开 Hub" : "Teleport failed; reopen Hub";
        }
    }

    private void DeleteSelectedPoi()
    {
        if (!CanDeleteSelected || _selectedPoiId == null) return;
        var service = ModEntry.PoiPreferenceOverlay;
        if (service == null) { _saveError = "服务不可用"; return; }
        var ov = service.LoadOrNull() ?? new PoiOverlayFile();
        ov.CustomPois.Remove(_selectedPoiId);
        if (!ov.RemovedCustomPoiIds.Contains(_selectedPoiId))
            ov.RemovedCustomPoiIds.Add(_selectedPoiId);
        if (service.Save(ov, out string err))
        {
            _statusMessage = IsZh ? "已删除" : "Deleted";
            RebuildMergedView();
            SelectFirstPoi();
            Hub.RefreshEntries();
        }
        else { _saveError = string.IsNullOrEmpty(err) ? "删除失败" : err; _statusMessage = null; }
    }

    private void SaveWeights()
    {
        if (_selectedNpc == null) return;
        var service = ModEntry.PoiPreferenceOverlay;
        if (service == null) { _saveError = "服务不可用"; return; }
        var ov = service.LoadOrNull() ?? new PoiOverlayFile();

        // 整体覆盖语义：写入全量列表（含默认 50）
        var pref = new NpcPreference();
        SyncWeightSteppers();
        foreach (var entry in _entries)
        {
            int w = _weightSteppersById.TryGetValue(entry.Id, out var stepper) ? stepper.Value : DefaultWeight;
            pref.PreferredPois.Add(new NpcPoiPreference { PoiId = entry.Id, Weight = Math.Clamp(w, 0, 100) });
        }
        if (ov.NpcPreferences == null) ov.NpcPreferences = new Dictionary<string, NpcPreference>(StringComparer.OrdinalIgnoreCase);
        ov.NpcPreferences[_selectedNpc] = pref;
        ov.RemovedNpcNames.Remove(_selectedNpc);

        if (service.Save(ov, out string err))
        {
            _statusMessage = IsZh ? "已保存" : "Saved";
            _saveError = null;
            Hub.RefreshEntries();
        }
        else { _saveError = string.IsNullOrEmpty(err) ? "保存失败" : err; _statusMessage = null; }
    }

    private void RevertNpc()
    {
        if (_selectedNpc == null) return;
        var service = ModEntry.PoiPreferenceOverlay;
        if (service == null) { _saveError = "服务不可用"; return; }
        var ov = service.LoadOrNull() ?? new PoiOverlayFile();
        ov.NpcPreferences.Remove(_selectedNpc);
        if (!ov.RemovedNpcNames.Contains(_selectedNpc))
            ov.RemovedNpcNames.Add(_selectedNpc);
        if (service.Save(ov, out string err))
        {
            _statusMessage = IsZh ? "已还原（回默认 50）" : "Reverted (default 50)";
            LoadWorkingWeightsForNpc();
            Hub.RefreshEntries();
        }
        else { _saveError = string.IsNullOrEmpty(err) ? "还原失败" : err; _statusMessage = null; }
    }

    private void SelectFirstPoi()
    {
        if (_entries.Count > 0) SelectPoi(_entries[0].Id);
        else _selectedPoiId = null;
    }

    // ── 键盘 ──────────────────────────────────────────────────────────

    private void SelectBox(MultilineTextBox tb)
    {
        DeselectAllBoxes();
        tb.Selected = true;
        Game1.keyboardDispatcher.Subscriber = tb;
    }

    private void DeselectAllBoxes()
    {
        foreach (var tb in new[] { _newDescBoxZh, _newDescBoxEn })
        {
            tb.Selected = false;
            if (Game1.keyboardDispatcher.Subscriber == tb)
                Game1.keyboardDispatcher.Subscriber = null;
        }
        _npcDropdown.Close();
    }

    private bool IsActiveBoxSelected()
    {
        return new[] { _newDescBoxZh, _newDescBoxEn }.Any(tb => tb.Selected);
    }
}
