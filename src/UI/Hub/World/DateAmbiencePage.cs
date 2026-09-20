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

/// <summary>约会氛围工坊子页：左列地点合并视图 + 右侧表单。</summary>
internal sealed class DateAmbiencePage : WorldSubPageBase
{
    // ── 布局常量 ──────────────────────────────────────────────────────
    private const int RowHeight = 30;
    private const int RowGap = 4;
    private const int FormTopPad = 16;
    private const int LabelW = 130;
    private const int CtrlH = 32;
    private const int CtrlGap = 10;
    private const int BtnH = 32;
    private const int BtnGap = 8;
    private const int DescMaxChars = 500;
    private const int ListWidthPercent = 38;

    // ── 合并视图 ──────────────────────────────────────────────────────
    private readonly List<ListEntry> _entries = new();
    private int _listScroll;
    private string? _selectedId;
    private Rectangle _listArea;
    private Rectangle _formArea;

    private sealed class ListEntry
    {
        public string Id = string.Empty;
        public string DisplayName = string.Empty;
        public bool IsBaseline;
        public bool IsCustom;
    }

    // ── 表单工作副本 ──────────────────────────────────────────────────
    private string _nameZh = "", _nameEn = "";
    private string _selectedMapId = "";
    private int _hearts = 4;
    private int _start = 1800, _end = 2130;
    private bool _allowRain;
    private string _descZh = "", _descEn = "";
    private bool _isCustom;
    private bool _baselineExists;

    // ── 控件 ──────────────────────────────────────────────────────────
    private readonly MultilineTextBox _nameBoxZh;
    private readonly MultilineTextBox _nameBoxEn;
    private readonly DropdownList _mapDropdown;
    private readonly NumberStepper _heartsStepper;
    private readonly DropdownList _startDropdown;
    private readonly DropdownList _endDropdown;
    private readonly UiCheckbox _rainCheckbox;
    private readonly MultilineTextBox _descBoxZh;
    private readonly MultilineTextBox _descBoxEn;

    private Rectangle _btnSave;
    private Rectangle _btnRevert;
    private Rectangle _btnDelete;
    private Rectangle _btnNew;

    private string? _saveError;
    private string? _statusMessage;

    private bool IsZh => LocalizedContentManager.CurrentLanguageCode == LocalizedContentManager.LanguageCode.zh;

    public DateAmbiencePage(IntegratedHubMenu hub) : base(hub)
    {
        _nameBoxZh = new MultilineTextBox(Rectangle.Empty, 1);
        _nameBoxEn = new MultilineTextBox(Rectangle.Empty, 1);
        _descBoxZh = new MultilineTextBox(Rectangle.Empty, 8) { Text = "" };
        _descBoxEn = new MultilineTextBox(Rectangle.Empty, 8) { Text = "" };

        _mapDropdown = new DropdownList(Rectangle.Empty, CtrlH, 7) { HeaderPrefix = "" };
        _heartsStepper = new NumberStepper(Rectangle.Empty, 4, 0, 14, 1, " 心");
        _startDropdown = new DropdownList(Rectangle.Empty, CtrlH, 8);
        _endDropdown = new DropdownList(Rectangle.Empty, CtrlH, 8);
        _rainCheckbox = new UiCheckbox(Rectangle.Empty, "");
    }

    // ── 子页生命周期 ──────────────────────────────────────────────────

    public override void OnShown()
    {
        RebuildMergedView();
        if (_entries.Count > 0 && _selectedId == null)
            SelectEntry(_entries[0].Id);
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
        int x = _formArea.X;
        int y = _formArea.Y + FormTopPad;
        int w = _formArea.Width;
        int ctrlW = w - LabelW - 12;

        _nameBoxZh.SetBounds(new Rectangle(x + LabelW, y, ctrlW, CtrlH)); y += CtrlH + CtrlGap;
        _nameBoxEn.SetBounds(new Rectangle(x + LabelW, y, ctrlW, CtrlH)); y += CtrlH + CtrlGap;
        _mapDropdown.SetHeaderBounds(new Rectangle(x + LabelW, y, ctrlW, CtrlH)); y += CtrlH + CtrlGap;
        _heartsStepper.SetBounds(new Rectangle(x + LabelW, y, ctrlW, CtrlH)); y += CtrlH + CtrlGap;
        _startDropdown.SetHeaderBounds(new Rectangle(x + LabelW, y, ctrlW, CtrlH)); y += CtrlH + CtrlGap;
        _endDropdown.SetHeaderBounds(new Rectangle(x + LabelW, y, ctrlW, CtrlH)); y += CtrlH + CtrlGap;
        _rainCheckbox.Bounds = new Rectangle(x + LabelW, y, ctrlW, 28); y += 36 + CtrlGap;

        int descH = 90;
        _descBoxZh.SetBounds(new Rectangle(x + LabelW, y, ctrlW, descH)); y += descH + CtrlGap;
        _descBoxEn.SetBounds(new Rectangle(x + LabelW, y, ctrlW, descH)); y += descH + CtrlGap + 8;

        int btnW = (w - BtnGap * 3) / 4;
        _btnSave = new Rectangle(x, y, btnW, BtnH);
        _btnRevert = new Rectangle(x + btnW + BtnGap, y, btnW, BtnH);
        _btnDelete = new Rectangle(x + (btnW + BtnGap) * 2, y, btnW, BtnH);
        _btnNew = new Rectangle(x + (btnW + BtnGap) * 3, y, btnW, BtnH);
    }

    public override void Update(GameTime time)
    {
        _nameBoxZh.Update(time);
        _nameBoxEn.Update(time);
        _descBoxZh.Update(time);
        _descBoxEn.Update(time);
    }

    public override bool ReceiveLeftClick(int x, int y)
    {
        // 列表区
        if (_listArea.Contains(x, y))
        {
            HandleListClick(x, y);
            return true;
        }

        // 控件
        if (_nameBoxZh.Bounds.Contains(x, y)) { SelectTextbox(_nameBoxZh); return true; }
        if (_nameBoxEn.Bounds.Contains(x, y)) { SelectTextbox(_nameBoxEn); return true; }
        if (_descBoxZh.Bounds.Contains(x, y)) { SelectTextbox(_descBoxZh); return true; }
        if (_descBoxEn.Bounds.Contains(x, y)) { SelectTextbox(_descBoxEn); return true; }

        if (_mapDropdown.ReceiveLeftClick(x, y)) return true;
        if (_heartsStepper.ReceiveLeftClick(x, y)) return true;
        if (_startDropdown.ReceiveLeftClick(x, y)) { SyncStartFromDropdown(); return true; }
        if (_endDropdown.ReceiveLeftClick(x, y)) { SyncEndFromDropdown(); return true; }
        if (_rainCheckbox.ReceiveLeftClick(x, y)) { _allowRain = _rainCheckbox.IsChecked; return true; }

        // 关闭下拉穿透
        if (_mapDropdown.IsOpen || _startDropdown.IsOpen || _endDropdown.IsOpen)
        {
            _mapDropdown.Close();
            _startDropdown.Close();
            _endDropdown.Close();
            return true;
        }

        // 操作按钮
        if (_btnSave.Contains(x, y) && CanSave) { Save(); return true; }
        if (_btnRevert.Contains(x, y) && _baselineExists && !_isCustom) { RevertThis(); return true; }
        if (_btnDelete.Contains(x, y) && _selectedId != null) { DeleteThis(); return true; }
        if (_btnNew.Contains(x, y)) { NewCustom(); return true; }

        return false;
    }

    public override bool ReceiveScrollWheel(int direction)
    {
        if (_listArea.Contains(Game1.getMouseX(), Game1.getMouseY()))
        {
            _listScroll = Math.Clamp(_listScroll - direction, 0, Math.Max(0, _entries.Count - VisibleRowCount()));
            return true;
        }
        if (_mapDropdown.ReceiveScrollWheel(direction)) return true;
        if (_startDropdown.ReceiveScrollWheel(direction)) return true;
        if (_endDropdown.ReceiveScrollWheel(direction)) return true;
        if (_descBoxZh.Selected) { _descBoxZh.Scroll(direction); return true; }
        if (_descBoxEn.Selected) { _descBoxEn.Scroll(direction); return true; }
        if (_mapDropdown.IsOpen || _startDropdown.IsOpen || _endDropdown.IsOpen) return true;
        return false;
    }

    public override bool ReceiveKeyPress(Keys key)
    {
        MultilineTextBox? active = ActiveTextbox();
        if (active != null && Game1.keyboardDispatcher.Subscriber == active)
        {
            if (key == Keys.Escape)
            {
                active.Selected = false;
                Game1.keyboardDispatcher.Subscriber = null;
                return true;
            }
            return false;
        }
        if (_mapDropdown.IsOpen || _startDropdown.IsOpen || _endDropdown.IsOpen)
        {
            if (key == Keys.Escape)
            {
                _mapDropdown.Close();
                _startDropdown.Close();
                _endDropdown.Close();
                return true;
            }
        }
        return false;
    }

    public override void OnHidden()
    {
        DeselectTextboxes();
    }

    // ── 绘制 ──────────────────────────────────────────────────────────

    public override void Draw(SpriteBatch b, Rectangle area, int mx, int my)
    {
        DrawList(b, mx, my);
        DrawForm(b, mx, my);
    }

    private void DrawList(SpriteBatch b, int mx, int my)
    {
        b.Draw(Game1.staminaRect, _listArea, new Color(0, 0, 0) * 0.04f);
        int y = _listArea.Y + 8;
        int end = Math.Min(_entries.Count, _listScroll + VisibleRowCount());
        for (int i = _listScroll; i < end; i++)
        {
            var e = _entries[i];
            var row = new Rectangle(_listArea.X + 6, y, _listArea.Width - 12, RowHeight);
            bool selected = string.Equals(e.Id, _selectedId, StringComparison.OrdinalIgnoreCase);
            bool hover = row.Contains(mx, my);
            if (selected) b.Draw(Game1.staminaRect, row, new Color(210, 180, 140));
            else if (hover) b.Draw(Game1.staminaRect, row, new Color(255, 235, 205));
            string tag = e.IsCustom ? " [+]" : (e.IsBaseline ? "" : " [*]");
            CustomFontManager.DrawString(b, e.DisplayName + tag,
                new Vector2(row.X + 8, row.Y + 5),
                selected ? Game1.textColor : Color.White * 0.92f,
                CustomFontManager.SizeRegular);
            y += RowHeight + RowGap;
        }
    }

    private void DrawForm(SpriteBatch b, int mx, int my)
    {
        b.Draw(Game1.staminaRect, _formArea, new Color(0, 0, 0) * 0.04f);

        int y = _formArea.Y + FormTopPad;
        int lx = _formArea.X + 12;

        DrawLabel(b, "名称 Zh", lx, y); _nameBoxZh.Draw(b); y += CtrlH + CtrlGap;
        DrawLabel(b, "名称 En", lx, y); _nameBoxEn.Draw(b); y += CtrlH + CtrlGap;
        DrawLabel(b, "目标地图", lx, y); _mapDropdown.Draw(b); y += CtrlH + CtrlGap;
        DrawLabel(b, "好感门槛", lx, y); _heartsStepper.Draw(b); y += CtrlH + CtrlGap;
        DrawLabel(b, "开始时间", lx, y); _startDropdown.Draw(b); y += CtrlH + CtrlGap;
        DrawLabel(b, "结束时间", lx, y); _endDropdown.Draw(b); y += CtrlH + CtrlGap;

        _rainCheckbox.Label = "雨天开放";
        DrawLabel(b, "雨天开放", lx, y);
        _rainCheckbox.Draw(b, mx, my);
        y += 36 + CtrlGap;

        DrawLabel(b, "氛围 Zh", lx, y); _descBoxZh.Draw(b); y += 90 + CtrlGap;
        DrawLabel(b, "氛围 En", lx, y); _descBoxEn.Draw(b); y += 90 + CtrlGap + 8;

        DrawButton(b, _btnSave, "保存", _btnSave.Contains(mx, my), CanSave);
        DrawButton(b, _btnRevert, "还原此条", _btnRevert.Contains(mx, my), _baselineExists && !_isCustom);
        DrawButton(b, _btnDelete, "删除此条", _btnDelete.Contains(mx, my), _selectedId != null);
        DrawButton(b, _btnNew, "新建", _btnNew.Contains(mx, my), true);

        if (_statusMessage != null)
            CustomFontManager.DrawString(b, _statusMessage, new Vector2(lx, _formArea.Bottom - 28), new Color(60, 130, 60), CustomFontManager.SizeSmall);
        else if (_saveError != null)
            CustomFontManager.DrawString(b, _saveError, new Vector2(lx, _formArea.Bottom - 28), Color.Red, CustomFontManager.SizeSmall);
    }

    private void DrawLabel(SpriteBatch b, string text, int x, int y)
    {
        CustomFontManager.DrawStringBold(b, text, new Vector2(x, y + 6), Game1.textColor, CustomFontManager.SizeRegular);
    }

    private void DrawButton(SpriteBatch b, Rectangle rect, string label, bool hover, bool enabled)
    {
        var bg = !enabled ? new Color(120, 110, 100)
               : hover ? new Color(175, 115, 55)
               : new Color(139, 90, 43);
        IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(432, 439, 9, 9),
            rect.X, rect.Y, rect.Width, rect.Height, bg, 4f, false);
        var sz = CustomFontManager.MeasureStringBold(label, CustomFontManager.SizeRegular);
        CustomFontManager.DrawStringBold(b, label,
            new Vector2(rect.X + (rect.Width - sz.X) / 2f, rect.Y + (rect.Height - sz.Y) / 2f),
            enabled ? Color.White : Color.White * 0.5f, CustomFontManager.SizeRegular);
    }

    private int VisibleRowCount() => (_listArea.Height - 16) / (RowHeight + RowGap);

    // ── 列表交互 ──────────────────────────────────────────────────────

    private void HandleListClick(int x, int y)
    {
        int idx = (y - _listArea.Y - 8) / (RowHeight + RowGap) + _listScroll;
        if (idx >= 0 && idx < _entries.Count)
            SelectEntry(_entries[idx].Id);
    }

    private void RebuildMergedView()
    {
        _entries.Clear();
        var reg = DateLocationRegistry.Locations;
        var ov = ModEntry.DateLocationOverlay?.LoadOrNull();

        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (reg != null)
            foreach (var id in reg.Keys)
                ids.Add(id);
        if (ov?.Entries != null)
            foreach (var id in ov.Entries.Keys)
                ids.Add(id);
        if (ov?.RemovedLocationIds != null)
            foreach (var id in ov.RemovedLocationIds)
                ids.Remove(id);

        foreach (var id in ids)
        {
            reg.TryGetValue(id, out var baseline);
            var isCustom = id.StartsWith("Custom_", StringComparison.OrdinalIgnoreCase);
            string name;
            if (baseline != null)
                name = IsZh ? (!string.IsNullOrEmpty(baseline.DisplayNameZh) ? baseline.DisplayNameZh : baseline.DisplayNameEn)
                            : (!string.IsNullOrEmpty(baseline.DisplayNameEn) ? baseline.DisplayNameEn : baseline.DisplayNameZh);
            else if (ov?.Entries != null && ov.Entries.TryGetValue(id, out var e))
                name = IsZh ? (!string.IsNullOrEmpty(e.DisplayNameZh) ? e.DisplayNameZh : e.DisplayNameEn)
                            : (!string.IsNullOrEmpty(e.DisplayNameEn) ? e.DisplayNameEn : e.DisplayNameZh);
            else
                name = id;
            _entries.Add(new ListEntry { Id = id, DisplayName = name, IsBaseline = baseline != null, IsCustom = isCustom });
        }

        _entries.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.CurrentCultureIgnoreCase));
        _listScroll = Math.Clamp(_listScroll, 0, Math.Max(0, _entries.Count - VisibleRowCount()));
    }

    private void SelectEntry(string id)
    {
        _selectedId = id;
        _saveError = null;
        _statusMessage = null;
        var reg = DateLocationRegistry.Locations;
        var ov = ModEntry.DateLocationOverlay?.LoadOrNull();

        reg.TryGetValue(id, out var baseline);
        DateLocationInfo? entry = null;
        ov?.Entries?.TryGetValue(id, out entry);
        var info = entry ?? baseline;

        _isCustom = id.StartsWith("Custom_", StringComparison.OrdinalIgnoreCase);
        _baselineExists = baseline != null;

        if (info != null)
        {
            _nameZh = info.DisplayNameZh;
            _nameEn = info.DisplayNameEn;
            _selectedMapId = info.TargetMap;
            _hearts = Math.Clamp(info.RequiredHearts, 0, 14);
            _allowRain = info.AllowRainyDays;
            // 审计：输入来自基线/覆盖层 TimeWindow 字符串；TryParse 守卫，失败则 _start/_end 保持上一值。
            TimeWindowConverter.TryParse(info.TimeWindow, out _start, out _end);
            _descZh = info.ContextDescriptionZh;
            _descEn = info.ContextDescriptionEn;
        }
        else
        {
            _nameZh = _nameEn = id;
            _selectedMapId = "";
            _hearts = 4; _start = 1800; _end = 2130; _allowRain = true;
            _descZh = _descEn = "";
        }

        _nameBoxZh.Text = _nameZh;
        _nameBoxEn.Text = _nameEn;
        _descBoxZh.Text = _descZh;
        _descBoxEn.Text = _descEn;
        _rainCheckbox.IsChecked = _allowRain;
        _heartsStepper.Value = _hearts;

        RefreshMapsDropdown();
        RefreshTimeDropdowns();
        DeselectTextboxes();
    }

    // ── 控件同步 ──────────────────────────────────────────────────────

    private void RefreshMapsDropdown()
    {
        var maps = ModEntry.WorldSummaryOverlay != null ? GetAvailableMapsSafe() : new List<DynamicAssetQueryService.MapInfo>();
        var items = new List<(string Id, string Label)>();
        string sel = _selectedMapId;
        bool found = false;
        foreach (var m in maps)
        {
            items.Add((m.Id, string.IsNullOrEmpty(m.DisplayName) ? m.Id : m.DisplayName));
            if (string.Equals(m.Id, sel, StringComparison.OrdinalIgnoreCase)) found = true;
        }
        if (!found && !string.IsNullOrEmpty(sel))
            items.Add((sel, sel + " (?)"));
        _mapDropdown.SetItems(items, found ? sel : (items.Count > 0 ? items[0].Id : ""));
        if (!found) _selectedMapId = items.Count > 0 ? items[0].Id : "";
    }

    private List<DynamicAssetQueryService.MapInfo> GetAvailableMapsSafe()
    {
        try { return DynamicAssetQueryService.GetAvailableMaps(); }
        catch (Exception ex) { ModEntry.SMonitor?.Log($"[DateAmbience] 采集地图失败: {ex.Message}", LogLevel.Warn); return new List<DynamicAssetQueryService.MapInfo>(); }
    }

    // 审计结论：下拉选项源 = EnumerateGrid()（墙钟 30 分钟网格，单一事实源），
    // 不再存在 HHmm 整数递增生成时间的代码路径。
    private void RefreshTimeDropdowns()
    {
        var items = new List<(string Id, string Label)>();
        foreach (int t in TimeWindowConverter.EnumerateGrid())
        {
            string label = $"{t / 100:D2}:{t % 100:D2}";
            items.Add((t.ToString(), label));
        }
        _startDropdown.SetItems(items, _start.ToString());
        _endDropdown.SetItems(items, _end.ToString());
    }

    // 审计：s/e 来自下拉 SelectedId，而下拉选项源 = EnumerateGrid()（必为合法网格值）；TryParse 二次守卫。
    private void SyncStartFromDropdown()
    {
        if (int.TryParse(_startDropdown.SelectedId, out int s) && TimeWindowConverter.TryParse($"{s:D4}-{_end:D4}", out _, out _))
            _start = s;
    }

    // 审计：同上，e 来自下拉，_start 已由本方法或 TryParse 置为合法值。
    private void SyncEndFromDropdown()
    {
        if (int.TryParse(_endDropdown.SelectedId, out int e) && TimeWindowConverter.TryParse($"{_start:D4}-{e:D4}", out _, out _))
            _end = e;
    }

    private void SelectTextbox(MultilineTextBox tb)
    {
        DeselectTextboxes();
        tb.Selected = true;
        Game1.keyboardDispatcher.Subscriber = tb;
    }

    private void DeselectTextboxes()
    {
        foreach (var tb in new[] { _nameBoxZh, _nameBoxEn, _descBoxZh, _descBoxEn })
        {
            tb.Selected = false;
            if (Game1.keyboardDispatcher.Subscriber == tb)
                Game1.keyboardDispatcher.Subscriber = null;
        }
        _mapDropdown.Close();
        _startDropdown.Close();
        _endDropdown.Close();
    }

    private MultilineTextBox? ActiveTextbox()
    {
        if (_nameBoxZh.Selected) return _nameBoxZh;
        if (_nameBoxEn.Selected) return _nameBoxEn;
        if (_descBoxZh.Selected) return _descBoxZh;
        if (_descBoxEn.Selected) return _descBoxEn;
        return null;
    }

    // ── 操作语义 ──────────────────────────────────────────────────────

    private bool CanSave =>
        !string.IsNullOrWhiteSpace(_nameZh) &&
        !string.IsNullOrWhiteSpace(_nameEn) &&
        !string.IsNullOrWhiteSpace(_selectedMapId) &&
        !string.IsNullOrWhiteSpace(_descZh) &&
        !string.IsNullOrWhiteSpace(_descEn);

    private DateLocationInfo BuildInfo()
    {
        var info = new DateLocationInfo
        {
            LocationId = _selectedId ?? "",
            TargetMap = _selectedMapId,
            DisplayNameZh = _nameBoxZh.Text.Trim(),
            DisplayNameEn = _nameBoxEn.Text.Trim(),
            RequiredHearts = _hearts,
            AllowRainyDays = _allowRain,
            // 审计：_start/_end 仅可能 = 下拉选项(合法网格) 或 TryParse 成功值，Format 必然合法。
            TimeWindow = TimeWindowConverter.Format(_start, _end),
            ContextDescriptionZh = _descBoxZh.Text.Length > DescMaxChars ? _descBoxZh.Text.Substring(0, DescMaxChars) : _descBoxZh.Text,
            ContextDescriptionEn = _descBoxEn.Text.Length > DescMaxChars ? _descBoxEn.Text.Substring(0, DescMaxChars) : _descBoxEn.Text,
        };
        return info;
    }

    private void Save()
    {
        var service = ModEntry.DateLocationOverlay;
        if (service == null)
        {
            _saveError = "服务不可用";
            ModEntry.SMonitor?.Log("[DateAmbience] DateLocationOverlay 服务为 null", LogLevel.Error);
            return;
        }

        var ov = service.LoadOrNull() ?? new DateLocationOverlayFile();
        string id = _selectedId ?? "";
        if (string.IsNullOrEmpty(id)) return;

        ov.Entries[id] = BuildInfo();

        if (service.Save(ov, out string err))
        {
            _statusMessage = "已保存";
            _saveError = null;
            RebuildMergedView();
            Hub.RefreshEntries();
        }
        else
        {
            _saveError = string.IsNullOrEmpty(err) ? "保存失败" : err;
            _statusMessage = null;
        }
    }

    private void RevertThis()
    {
        var service = ModEntry.DateLocationOverlay;
        if (service == null || _selectedId == null) return;
        var ov = service.LoadOrNull() ?? new DateLocationOverlayFile();
        ov.Entries.Remove(_selectedId);
        ov.RemovedLocationIds.Remove(_selectedId);
        if (service.Save(ov, out string err))
        {
            _statusMessage = "已还原";
            RebuildMergedView();
            Hub.RefreshEntries();
        }
        else _saveError = string.IsNullOrEmpty(err) ? "还原失败" : err;
    }

    private void DeleteThis()
    {
        var service = ModEntry.DateLocationOverlay;
        if (service == null || _selectedId == null) return;
        var ov = service.LoadOrNull() ?? new DateLocationOverlayFile();
        ov.Entries.Remove(_selectedId);
        if (_baselineExists && !ov.RemovedLocationIds.Contains(_selectedId))
            ov.RemovedLocationIds.Add(_selectedId);
        if (service.Save(ov, out string err))
        {
            _statusMessage = "已删除";
            RebuildMergedView();
            SelectFirstOrDefault();
            Hub.RefreshEntries();
        }
        else _saveError = string.IsNullOrEmpty(err) ? "删除失败" : err;
    }

    private void NewCustom()
    {
        string baseName = IsZh ? "新自定义地点" : "New Custom";
        string id = "Custom_" + SanitizeFileName(baseName);
        var ov = ModEntry.DateLocationOverlay?.LoadOrNull() ?? new DateLocationOverlayFile();
        int suffix = 2;
        while (ov.Entries.ContainsKey(id) || DateLocationRegistry.Locations.ContainsKey(id))
        {
            id = $"Custom_{SanitizeFileName(baseName)}_{suffix}";
            suffix++;
        }
        ov.Entries[id] = new DateLocationInfo
        {
            LocationId = id, DisplayNameZh = baseName, DisplayNameEn = baseName,
            TargetMap = "", RequiredHearts = 4, AllowRainyDays = true,
            TimeWindow = "1800-2130",
            ContextDescriptionZh = "", ContextDescriptionEn = ""
        };
        if (ModEntry.DateLocationOverlay?.Save(ov, out _) == true)
        {
            RebuildMergedView();
            SelectEntry(id);
            Hub.RefreshEntries();
        }
    }

    private void SelectFirstOrDefault()
    {
        if (_entries.Count > 0) SelectEntry(_entries[0].Id);
        else { _selectedId = null; }
    }

    private static string SanitizeFileName(string name)
    {
        char[] invalid = System.IO.Path.GetInvalidFileNameChars();
        string s = name;
        foreach (char c in invalid) s = s.Replace(c, '_');
        return string.IsNullOrWhiteSpace(s) ? "Custom" : s.Trim();
    }
}
