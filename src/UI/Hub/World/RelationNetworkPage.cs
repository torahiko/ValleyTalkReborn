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

/// <summary>客观社交关系网子页：左列合并关系列表 + 右侧双 NPC 编辑表单。</summary>
internal sealed class RelationNetworkPage : WorldSubPageBase
{
    private const int RowHeight = 30;
    private const int RowGap = 4;
    private const int FormTopPad = 16;
    private const int LabelW = 110;
    private const int CtrlH = 32;
    private const int CtrlGap = 10;
    private const int BtnH = 32;
    private const int BtnGap = 8;
    private const int DescMaxChars = 500;
    private const int ListWidthPercent = 38;

    // ── 合并视图 ──────────────────────────────────────────────────────
    private readonly List<RelationEntry> _entries = new();
    private int _listScroll;
    private string? _selectedKey;
    private Rectangle _listArea;
    private Rectangle _formArea;

    private string? _baselineAssetError;

    private sealed class RelationEntry
    {
        public string Key = string.Empty;
        public string NpcA = "";
        public string NpcB = "";
        public string DisplayA = "";
        public string DisplayB = "";
        public bool IsBaseline;
        public bool IsCustom;   // 仅存在于覆盖层
        public bool IsTombstone; // 覆盖层 Disabled==true（删除基线对）
        public Dictionary<string, string> Descriptions = new();
    }

    // ── 缓存（OnShown 构建，Draw 只读）─────────────────────────────────
    private Dictionary<string, string> _displayNames = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, Texture2D?> _portraits = new(StringComparer.OrdinalIgnoreCase);
    private List<(string Id, string DisplayName)> _candidateItems = new();

    // ── 表单工作副本 ──────────────────────────────────────────────────
    private string _npcA = "";
    private string _npcB = "";
    private string _desc = "";
    private RelationEntry? _editingEntry; // 当前编辑的合并条目（null = 新建）

    // ── 控件 ──────────────────────────────────────────────────────────
    private readonly DropdownList _dropA;
    private readonly DropdownList _dropB;
    private readonly MultilineTextBox _descBox;

    private Rectangle _btnSave;
    private Rectangle _btnRevert;
    private Rectangle _btnDelete;
    private Rectangle _btnNew;

    private string? _saveError;
    private string? _statusMessage;

    private bool IsZh => LocalizedContentManager.CurrentLanguageCode == LocalizedContentManager.LanguageCode.zh;

    public RelationNetworkPage(IntegratedHubMenu hub) : base(hub)
    {
        _dropA = new DropdownList(Rectangle.Empty, CtrlH, 8);
        _dropB = new DropdownList(Rectangle.Empty, CtrlH, 8);
        _descBox = new MultilineTextBox(Rectangle.Empty, 8) { Text = "" };
    }

    // ── 子页生命周期 ──────────────────────────────────────────────────

    public override void OnShown()
    {
        BuildCaches();
        RebuildMergedView();
        if (_entries.Count > 0 && _selectedKey == null)
            SelectEntry(_entries[0].Key);
        else if (_selectedKey != null)
            SelectEntry(_selectedKey);
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

        _dropA.SetHeaderBounds(new Rectangle(x + LabelW, y, ctrlW, CtrlH)); y += CtrlH + CtrlGap + 60; // 留头像空间
        _dropB.SetHeaderBounds(new Rectangle(x + LabelW, y, ctrlW, CtrlH)); y += CtrlH + CtrlGap + 60;

        int descH = 100;
        _descBox.SetBounds(new Rectangle(x + LabelW, y, ctrlW, descH)); y += descH + CtrlGap + 8;

        int btnW = (w - BtnGap * 3) / 4;
        _btnSave = new Rectangle(x, y, btnW, BtnH);
        _btnRevert = new Rectangle(x + btnW + BtnGap, y, btnW, BtnH);
        _btnDelete = new Rectangle(x + (btnW + BtnGap) * 2, y, btnW, BtnH);
        _btnNew = new Rectangle(x + (btnW + BtnGap) * 3, y, btnW, BtnH);
    }

    public override void Update(GameTime time) => _descBox.Update(time);

    public override bool ReceiveLeftClick(int x, int y)
    {
        if (_listArea.Contains(x, y)) { HandleListClick(x, y); return true; }

        if (_dropA.ReceiveLeftClick(x, y)) { SyncNpcA(); return true; }
        if (_dropB.ReceiveLeftClick(x, y)) { SyncNpcB(); return true; }
        if (_descBox.Bounds.Contains(x, y)) { SelectDescBox(); return true; }

        if (_dropA.IsOpen || _dropB.IsOpen) { _dropA.Close(); _dropB.Close(); return true; }

        string? key = _selectedKey;
        if (_btnSave.Contains(x, y) && CanSave) { Save(); return true; }
        if (_btnRevert.Contains(x, y) && CanRevert) { RevertThis(); return true; }
        if (_btnDelete.Contains(x, y) && key != null) { DeleteThis(); return true; }
        if (_btnNew.Contains(x, y)) { NewRelation(); return true; }

        return false;
    }

    public override bool ReceiveScrollWheel(int direction)
    {
        if (_listArea.Contains(Game1.getMouseX(), Game1.getMouseY()))
        {
            _listScroll = Math.Clamp(_listScroll - direction, 0, Math.Max(0, _entries.Count - VisibleRowCount()));
            return true;
        }
        if (_dropA.ReceiveScrollWheel(direction)) return true;
        if (_dropB.ReceiveScrollWheel(direction)) return true;
        if (_descBox.Selected) { _descBox.Scroll(direction); return true; }
        if (_dropA.IsOpen || _dropB.IsOpen) return true;
        return false;
    }

    public override bool ReceiveKeyPress(Keys key)
    {
        if (_descBox.Selected && Game1.keyboardDispatcher.Subscriber == _descBox)
        {
            if (key == Keys.Escape) { _descBox.Selected = false; Game1.keyboardDispatcher.Subscriber = null; return true; }
            return false;
        }
        if (_dropA.IsOpen || _dropB.IsOpen)
            if (key == Keys.Escape) { _dropA.Close(); _dropB.Close(); return true; }
        return false;
    }

    public override void OnHidden()
    {
        _descBox.Selected = false;
        if (Game1.keyboardDispatcher.Subscriber == _descBox)
            Game1.keyboardDispatcher.Subscriber = null;
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
            bool selected = string.Equals(e.Key, _selectedKey, StringComparison.OrdinalIgnoreCase);
            bool hover = row.Contains(mx, my);
            if (selected) b.Draw(Game1.staminaRect, row, new Color(210, 180, 140));
            else if (hover) b.Draw(Game1.staminaRect, row, new Color(255, 235, 205));
            string tag = e.IsTombstone ? " [✕]" : (e.IsCustom ? " [+]" : "");
            string label = $"{e.DisplayA} ✕ {e.DisplayB}{tag}";
            CustomFontManager.DrawString(b, label, new Vector2(row.X + 8, row.Y + 5),
                selected ? Game1.textColor : Color.White * 0.92f, CustomFontManager.SizeRegular);
            y += RowHeight + RowGap;
        }
    }

    private void DrawForm(SpriteBatch b, int mx, int my)
    {
        b.Draw(Game1.staminaRect, _formArea, new Color(0, 0, 0) * 0.04f);
        int y = _formArea.Y + FormTopPad;
        int lx = _formArea.X + 12;

        DrawLabel(b, I18n.WorldSettings.NpcA(), lx, y);
        DrawPortraitAndName(b, _npcA, _dropA.HeaderBounds);
        _dropA.Draw(b);
        y += CtrlH + CtrlGap + 60;

        DrawLabel(b, I18n.WorldSettings.NpcB(), lx, y);
        DrawPortraitAndName(b, _npcB, _dropB.HeaderBounds);
        _dropB.Draw(b);
        y += CtrlH + CtrlGap + 60;

        DrawLabel(b, I18n.WorldSettings.Description(), lx, y);
        _descBox.Draw(b);
        y += 100 + CtrlGap + 8;

        DrawButton(b, _btnSave, I18n.WorldSettings.SaveRelation(), _btnSave.Contains(mx, my), CanSave);
        DrawButton(b, _btnRevert, I18n.WorldSettings.RevertRelation(), _btnRevert.Contains(mx, my), CanRevert);
        DrawButton(b, _btnDelete, I18n.WorldSettings.DeleteRelation(), _btnDelete.Contains(mx, my), _selectedKey != null);
        DrawButton(b, _btnNew, I18n.WorldSettings.NewRelation(), _btnNew.Contains(mx, my), _candidateItems.Count >= 2);

        string? msg = _saveError ?? _statusMessage;
        if (msg != null)
            CustomFontManager.DrawString(b, msg, new Vector2(lx, _formArea.Bottom - 28),
                _saveError != null ? Color.Red : new Color(60, 130, 60), CustomFontManager.SizeSmall);
    }

    private void DrawLabel(SpriteBatch b, string text, int x, int y) =>
        CustomFontManager.DrawStringBold(b, text, new Vector2(x, y + 6), Game1.textColor, CustomFontManager.SizeRegular);

    private void DrawPortraitAndName(SpriteBatch b, string npcId, Rectangle dropdownBounds)
    {
        if (string.IsNullOrEmpty(npcId)) return;
        int px = dropdownBounds.X;
        int py = dropdownBounds.Y - 56;
        if (_portraits.TryGetValue(npcId, out var portrait) && portrait != null)
        {
            var dest = new Rectangle(px, py, 48, 48);
            b.Draw(portrait, dest, new Rectangle(0, 0, portrait.Width, portrait.Height), Color.White, 0f, Vector2.Zero, SpriteEffects.None, 1f);
        }
        string name = _displayNames.TryGetValue(npcId, out var dn) ? dn : npcId;
        CustomFontManager.DrawString(b, name, new Vector2(px + 56, py + 16), Game1.textColor, CustomFontManager.SizeSmall);
    }

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

    private int VisibleRowCount() => (_listArea.Height - 16) / (RowHeight + RowGap);

    // ── 缓存构建（OnShown 一次）────────────────────────────────────────

    private void BuildCaches()
    {
        _displayNames.Clear();
        _portraits.Clear();
        _candidateItems.Clear();

        List<(string Id, string DisplayName)> candidates;
        try { candidates = NpcCandidateQueryService.GetCleanedCandidates(); }
        catch (Exception ex)
        {
            ModEntry.SMonitor?.Log($"[RelationNetwork] 候选集加载失败: {ex.Message}", LogLevel.Warn);
            candidates = new List<(string, string)>();
        }

        foreach (var (id, name) in candidates)
        {
            _displayNames[id] = name;
            _candidateItems.Add((id, name));
            Texture2D? portrait = null;
            try
            {
                var npc = Game1.getCharacterFromName(id);
                portrait = npc?.Portrait;
            }
            catch (Exception ex)
            {
                ModEntry.SMonitor?.Log($"[RelationNetwork] 头像加载失败({id}): {ex.Message}", LogLevel.Warn);
            }
            _portraits[id] = portrait;
        }
    }

    // ── 合并视图 ──────────────────────────────────────────────────────

    private void RebuildMergedView()
    {
        _entries.Clear();
        _baselineAssetError = null;

        // 基线
        var baseline = LoadBaseline();
        var baselineKeys = new HashSet<string>(StringComparer.Ordinal);
        if (baseline != null)
        {
            foreach (var r in baseline.Relations)
            {
                if (string.IsNullOrWhiteSpace(r.NpcA) || string.IsNullOrWhiteSpace(r.NpcB))
                {
                    ModEntry.SMonitor?.Log($"[RelationNetwork] 跳过含空白 NPC 名的基线关系", LogLevel.Warn);
                    continue;
                }
                string key = NpcRelationOverlayService.MakeKey(r.NpcA, r.NpcB);
                baselineKeys.Add(key);
                _entries.Add(MakeEntry(key, r.NpcA, r.NpcB, r.Descriptions, isBaseline: true));
            }
        }

        // 覆盖层
        var ov = ModEntry.NpcRelationOverlay?.LoadOrNull();
        if (ov != null)
        {
            foreach (var r in ov.Relations)
            {
                if (string.IsNullOrWhiteSpace(r.NpcA) || string.IsNullOrWhiteSpace(r.NpcB))
                {
                    ModEntry.SMonitor?.Log($"[RelationNetwork] 跳过含空白 NPC 名的覆盖关系", LogLevel.Warn);
                    continue;
                }
                string key = NpcRelationOverlayService.MakeKey(r.NpcA, r.NpcB);
                var existing = _entries.Find(e => string.Equals(e.Key, key, StringComparison.Ordinal));
                if (r.Disabled)
                {
                    // 墓碑：删除基线对
                    if (existing != null) { existing.IsTombstone = true; existing.IsCustom = false; existing.Descriptions = new Dictionary<string, string>(r.Descriptions); }
                    else
                    {
                        var tomb = MakeEntry(key, r.NpcA, r.NpcB, r.Descriptions, isBaseline: false);
                        tomb.IsTombstone = true;
                        _entries.Add(tomb);
                    }
                }
                else
                {
                    if (existing != null) { existing.Descriptions = new Dictionary<string, string>(r.Descriptions); existing.IsCustom = !existing.IsBaseline; existing.IsTombstone = false; }
                    else
                    {
                        var custom = MakeEntry(key, r.NpcA, r.NpcB, r.Descriptions, isBaseline: false);
                        custom.IsCustom = true;
                        _entries.Add(custom);
                    }
                }
            }
        }

        _entries.Sort((a, b) => string.Compare(a.Key, b.Key, StringComparison.Ordinal));
        _listScroll = Math.Clamp(_listScroll, 0, Math.Max(0, _entries.Count - VisibleRowCount()));
    }

    private NpcRelationsFile? LoadBaseline()
    {
        try
        {
            var loaded = ModEntry.SHelper.GameContent.Load<NpcRelationsFile>("ValleytalkReborn/NpcRelations");
            if (loaded == null)
            {
                _baselineAssetError = "基线资产返回 null";
                ModEntry.SMonitor?.Log("[RelationNetwork] 基线资产(ValleytalkReborn/NpcRelations)返回 null", LogLevel.Warn);
            }
            return loaded;
        }
        catch (Exception ex)
        {
            _baselineAssetError = ex.Message;
            ModEntry.SMonitor?.Log($"[RelationNetwork] 基线资产加载失败: {ex.Message}", LogLevel.Warn);
            return null;
        }
    }

    private RelationEntry MakeEntry(string key, string npcA, string npcB, Dictionary<string, string> descriptions, bool isBaseline)
    {
        ResolveDisplayNames(npcA, npcB, out string dispA, out string dispB);
        return new RelationEntry
        {
            Key = key, NpcA = npcA, NpcB = npcB, DisplayA = dispA, DisplayB = dispB,
            IsBaseline = isBaseline, Descriptions = new Dictionary<string, string>(descriptions),
        };
    }

    private void ResolveDisplayNames(string npcA, string npcB, out string dispA, out string dispB)
    {
        dispA = _displayNames.TryGetValue(npcA, out var a) ? a : npcA;
        dispB = _displayNames.TryGetValue(npcB, out var b) ? b : npcB;
    }

    // ── 列表交互 ──────────────────────────────────────────────────────

    private void HandleListClick(int x, int y)
    {
        int idx = (y - _listArea.Y - 8) / (RowHeight + RowGap) + _listScroll;
        if (idx >= 0 && idx < _entries.Count)
            SelectEntry(_entries[idx].Key);
    }

    private void SelectEntry(string key)
    {
        _selectedKey = key;
        _saveError = null;
        _statusMessage = null;
        var entry = _entries.Find(e => string.Equals(e.Key, key, StringComparison.Ordinal));
        if (entry == null) { ClearForm(); return; }

        _editingEntry = entry;
        _npcA = entry.NpcA;
        _npcB = entry.NpcB;
        string langKey = IsZh ? "zh" : "en";
        _desc = entry.Descriptions.TryGetValue(langKey, out var d) ? d : "";

        RefreshCandidateDropdowns();
        _descBox.Text = _desc;
        DeselectDescBox();
    }

    private void ClearForm()
    {
        _editingEntry = null;
        _npcA = _npcB = _desc = "";
        RefreshCandidateDropdowns();
        _descBox.Text = "";
        DeselectDescBox();
    }

    // ── 控件同步 ──────────────────────────────────────────────────────

    private void RefreshCandidateDropdowns()
    {
        _dropA.SetItems(_candidateItems, _npcA);
        _dropB.SetItems(_candidateItems, _npcB);
    }

    private void SyncNpcA()
    {
        if (!string.IsNullOrEmpty(_dropA.SelectedId)) _npcA = _dropA.SelectedId;
    }

    private void SyncNpcB()
    {
        if (!string.IsNullOrEmpty(_dropB.SelectedId)) _npcB = _dropB.SelectedId;
    }

    private void SelectDescBox()
    {
        _descBox.Selected = true;
        Game1.keyboardDispatcher.Subscriber = _descBox;
    }

    private void DeselectDescBox()
    {
        _descBox.Selected = false;
        if (Game1.keyboardDispatcher.Subscriber == _descBox)
            Game1.keyboardDispatcher.Subscriber = null;
    }

    // ── 操作语义 ──────────────────────────────────────────────────────

    private string LangKey => IsZh ? "zh" : "en";

    private bool CanSave => !string.IsNullOrEmpty(_npcA) && !string.IsNullOrEmpty(_npcB) && !string.Equals(_npcA, _npcB, StringComparison.OrdinalIgnoreCase);

    private bool CanRevert => _editingEntry != null && _editingEntry.IsBaseline && _editingEntry.IsCustom;

    private NpcRelationEntry BuildEntry()
    {
        // 规范对：NpcA/NpcB 按 MakeKey 排序序重写
        var sorted = NpcRelationOverlayService.MakeKey(_npcA, _npcB);
        var parts = sorted.Split('|');
        var entry = new NpcRelationEntry { NpcA = parts[0], NpcB = parts[1], Disabled = false };
        // 编辑既有覆盖条目时保留其他语言键
        Dictionary<string, string> descs = new();
        if (_editingEntry != null && _editingEntry.IsCustom && !_editingEntry.IsTombstone)
            foreach (var kv in _editingEntry.Descriptions)
                descs[kv.Key] = kv.Value;
        descs[LangKey] = _descBox.Text.Length > DescMaxChars ? _descBox.Text.Substring(0, DescMaxChars) : _descBox.Text;
        entry.Descriptions = descs;
        return entry;
    }

    private void Save()
    {
        var service = ModEntry.NpcRelationOverlay;
        if (service == null) { _saveError = "服务不可用"; return; }
        if (!CanSave) { _saveError = I18n.WorldSettings.NeedTwoNpcs(); return; }

        var ov = service.LoadOrNull() ?? new NpcRelationOverlayFile();
        string key = NpcRelationOverlayService.MakeKey(_npcA, _npcB);
        var entry = BuildEntry();

        // upsert：先移除同键再追加（MakeKey 输出组件保留输入大小写，收敛手工编辑的同对异写）
        ov.Relations.RemoveAll(r => NpcRelationOverlayService.MakeKey(r.NpcA, r.NpcB).Equals(key, StringComparison.OrdinalIgnoreCase));
        ov.Relations.Add(entry);

        if (service.Save(ov, out string err))
        {
            _statusMessage = I18n.WorldSettings.SavedRelation();
            _saveError = null;
            RebuildMergedView();
            Hub.RefreshEntries();
        }
        else { _saveError = string.IsNullOrEmpty(err) ? "保存失败" : err; _statusMessage = null; }
    }

    private void RevertThis()
    {
        if (!CanRevert) return;
        var service = ModEntry.NpcRelationOverlay;
        if (service == null) { _saveError = "服务不可用"; return; }
        string key = NpcRelationOverlayService.MakeKey(_npcA, _npcB);
        var ov = service.LoadOrNull() ?? new NpcRelationOverlayFile();
        int removed = ov.Relations.RemoveAll(r => NpcRelationOverlayService.MakeKey(r.NpcA, r.NpcB).Equals(key, StringComparison.OrdinalIgnoreCase));
        if (removed == 0) { _statusMessage = IsZh ? "无需还原" : "Nothing to revert"; return; }
        if (service.Save(ov, out string err))
        {
            _statusMessage = IsZh ? "已还原" : "Reverted";
            RebuildMergedView();
            Hub.RefreshEntries();
        }
        else { _saveError = string.IsNullOrEmpty(err) ? "还原失败" : err; }
    }

    private void DeleteThis()
    {
        if (_selectedKey == null) return;
        var service = ModEntry.NpcRelationOverlay;
        if (service == null) { _saveError = "服务不可用"; return; }
        var entry = _entries.Find(e => string.Equals(e.Key, _selectedKey, StringComparison.Ordinal));
        if (entry == null) return;

        var ov = service.LoadOrNull() ?? new NpcRelationOverlayFile();
        string key = entry.Key;

        if (entry.IsBaseline && !entry.IsTombstone)
        {
            // 删除基线对 → 写墓碑（Disabled=true；MakeKey 输出组件保留输入大小写，收敛手工编辑的同对异写）
            ov.Relations.RemoveAll(r => NpcRelationOverlayService.MakeKey(r.NpcA, r.NpcB).Equals(key, StringComparison.OrdinalIgnoreCase));
            ov.Relations.Add(new NpcRelationEntry { NpcA = entry.NpcA, NpcB = entry.NpcB, Disabled = true, Descriptions = new Dictionary<string, string>() });
        }
        else
        {
            // 删除自定义对（或已是墓碑）→ 直接移除（MakeKey 输出组件保留输入大小写，收敛手工编辑的同对异写）
            ov.Relations.RemoveAll(r => NpcRelationOverlayService.MakeKey(r.NpcA, r.NpcB).Equals(key, StringComparison.OrdinalIgnoreCase));
        }

        if (service.Save(ov, out string err))
        {
            _statusMessage = IsZh ? "已删除" : "Deleted";
            RebuildMergedView();
            SelectFirstOrDefault();
            Hub.RefreshEntries();
        }
        else { _saveError = string.IsNullOrEmpty(err) ? "删除失败" : err; }
    }

    private void NewRelation()
    {
        _selectedKey = null;
        _editingEntry = null;
        _npcA = _candidateItems.Count > 0 ? _candidateItems[0].Id : "";
        _npcB = _candidateItems.Count > 1 ? _candidateItems[1].Id : "";
        _desc = "";
        RefreshCandidateDropdowns();
        _descBox.Text = "";
        DeselectDescBox();
        _statusMessage = IsZh ? "已新建关系对，编辑后保存" : "New pair created, edit then save";
    }

    private void SelectFirstOrDefault()
    {
        if (_entries.Count > 0) SelectEntry(_entries[0].Key);
        else ClearForm();
    }
}
