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

/// <summary>
/// 客观社交关系网子页：
/// 1. 复刻 RulesTabView 标准滑动条（支持拖拽与精确点击）；
/// 2. 左栏满容多显一行，消除空隙；
/// 3. 玩家自创永远置顶显示，双向防重拦截；
/// 4. 搜索框打字/退格精准，快捷键防误触。
/// </summary>
internal sealed class RelationNetworkPage : WorldSubPageBase
{
    private const int RowHeight = 40; // 微调高度紧凑化
    private const int RowGap = 4;     // 紧缩间距，垂直空间极致利用
    private const int LabelWidth = 116;
    private const int CtrlHeight = 32;
    private const int CtrlGap = 10;

    private sealed class RelationEntry
    {
        public string Key = string.Empty;
        public string NpcA = "";
        public string NpcB = "";
        public string DisplayA = "";
        public string DisplayB = "";
        public bool IsBaseline;
        public bool IsCustom;
        public bool IsTombstone;
        public Dictionary<string, string> Descriptions = new();
    }

    private readonly List<RelationEntry> _entries = new();
    private readonly List<RelationEntry> _filteredEntries = new();
    private int _listScroll;
    private bool _isDraggingLeftScrollbar = false; // ★ 复刻 RulesTabView 滑动条拖拽状态
    private string? _selectedKey;
    private bool _isCreatingNew;
    private Rectangle _listRect;
    private Rectangle _formRect;

    // ── 搜索框 ──
    private readonly TextBox _searchBox;
    private Rectangle _searchBoxRect;
    private string _lastSearchQuery = "";

    // ── NPC 缓存与候选集 ──
    private readonly Dictionary<string, string> _displayNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (Texture2D? Texture, Rectangle SourceRect)> _walkingSprites = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(string Id, string DisplayName)> _candidateItems = new();

    // ── 表单工作副本 ──
    private string _npcA = "";
    private string _npcB = "";
    private RelationEntry? _editingEntry;
    private string? _statusMessage;

    // ── 独立下拉框状态机 ──
    private enum ActiveDropdown { None, NpcA, NpcB }
    private ActiveDropdown _activeDropdown = ActiveDropdown.None;
    private int _dropdownScrollOffset = 0;
    private Rectangle _dropAHeaderRect;
    private Rectangle _dropBHeaderRect;
    private Rectangle _avBoxA;
    private Rectangle _avBoxB;
    private const int DropdownItemHeight = 34;
    private const int DropdownMaxVisible = 6;

    // 跑马灯 Scissor 裁切状态机
    private static readonly RasterizerState ScissorRasterizer = new() { CullMode = CullMode.None, ScissorTestEnable = true };

    private readonly DialogueTextInputBox _descBox;

    private Rectangle _btnSave;
    private Rectangle _btnRevert;
    private Rectangle _btnDelete;
    private Rectangle _btnNew;

    private static bool IsZh => LocalizedContentManager.CurrentLanguageCode == LocalizedContentManager.LanguageCode.zh;
    private static string LangKey => IsZh ? "zh" : "en";

    public RelationNetworkPage(IntegratedHubMenu hub) : base(hub)
    {
        Texture2D boxTex = Game1.content.Load<Texture2D>("LooseSprites\\textBox") ?? Game1.mouseCursors;
        _searchBox = new TextBox(boxTex, null, Game1.smallFont, RulesTheme.TextCharcoal);
        // 关闭原版 TextBox 的像素宽度截断（Text setter 内置递归截断会静默损毁程序化赋值的长文本）
        _searchBox.limitWidth = false;

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
        BuildCaches();
        RebuildMergedView();
        if (_filteredEntries.Count > 0 && _selectedKey == null)
        {
            SelectEntry(_filteredEntries[0].Key);
        }
        else if (_selectedKey != null)
        {
            SelectEntry(_selectedKey);
        }
    }

    public override void Layout(Rectangle area)
    {
        int listWidth = (int)(area.Width * 0.34f);
        _listRect = new Rectangle(area.X, area.Y, listWidth, area.Height);

        int formX = _listRect.Right + 12;
        int formW = area.Right - formX;
        _formRect = new Rectangle(formX, area.Y, formW, area.Height);

        // 1. 左栏搜索框（紧贴顶部）
        _searchBoxRect = new Rectangle(_listRect.X + 6, _listRect.Y + 6, _listRect.Width - 12, 30);
        _searchBox.X = _searchBoxRect.X;
        _searchBox.Y = _searchBoxRect.Y;
        _searchBox.Width = _searchBoxRect.Width;
        _searchBox.Height = _searchBoxRect.Height;

        // 2. 右栏表单
        int curY = _formRect.Y + 14;
        curY += 26; // 预留标语高度

        int avX = _formRect.X + 12 + LabelWidth + 6;
        int dropX = avX + 32 + 10;
        int dropW = _formRect.Right - dropX - 28;

        _avBoxA = new Rectangle(avX, curY, 32, 32);
        _dropAHeaderRect = new Rectangle(dropX, curY, dropW, CtrlHeight);
        curY += CtrlHeight + CtrlGap;

        _avBoxB = new Rectangle(avX, curY, 32, 32);
        _dropBHeaderRect = new Rectangle(dropX, curY, dropW, CtrlHeight);
        curY += CtrlHeight + CtrlGap + 6;

        int btnY = _formRect.Bottom - 36;
        int btnW = (_formRect.Width - 18) / 4;
        _btnSave = new Rectangle(_formRect.X, btnY, btnW, 32);
        _btnRevert = new Rectangle(_formRect.X + btnW + 6, btnY, btnW, 32);
        _btnDelete = new Rectangle(_formRect.X + (btnW + 6) * 2, btnY, btnW, 32);
        _btnNew = new Rectangle(_formRect.X + (btnW + 6) * 3, btnY, btnW, 32);

        int descTop = curY + 22;
        int descBottom = btnY - 8;
        int descH = Math.Max(110, descBottom - descTop);

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
            FilterEntries();
        }
    }

    public override bool ReceiveLeftClick(int x, int y)
    {
        // ── 1. 下拉框展开浮层优先处理 ──
        if (_activeDropdown != ActiveDropdown.None)
        {
            Rectangle headerRect = _activeDropdown == ActiveDropdown.NpcA ? _dropAHeaderRect : _dropBHeaderRect;
            var dropListRect = GetDropdownMenuRect(headerRect);

            if (dropListRect.Contains(x, y))
            {
                int clickIdx = (y - dropListRect.Y - 4) / DropdownItemHeight + _dropdownScrollOffset;
                if (clickIdx >= 0 && clickIdx < _candidateItems.Count)
                {
                    if (_activeDropdown == ActiveDropdown.NpcA)
                        _npcA = _candidateItems[clickIdx].Id;
                    else
                        _npcB = _candidateItems[clickIdx].Id;

                    _activeDropdown = ActiveDropdown.None;
                    Game1.playSound("smallSelect");
                    TrySyncToExistingEntry();
                }
                return true;
            }

            if (headerRect.Contains(x, y))
            {
                _activeDropdown = ActiveDropdown.None;
                Game1.playSound("shwip");
                return true;
            }

            _activeDropdown = ActiveDropdown.None;
            return true;
        }

        // ── 2. 点击触发下拉框展开 ──
        if (_dropAHeaderRect.Contains(x, y))
        {
            _activeDropdown = ActiveDropdown.NpcA;
            int idx = _candidateItems.FindIndex(c => string.Equals(c.Id, _npcA, StringComparison.OrdinalIgnoreCase));
            _dropdownScrollOffset = Math.Clamp(idx >= 0 ? idx : 0, 0, Math.Max(0, _candidateItems.Count - DropdownMaxVisible));
            Game1.playSound("shwip");
            DeselectTextBoxes();
            return true;
        }

        if (_dropBHeaderRect.Contains(x, y))
        {
            _activeDropdown = ActiveDropdown.NpcB;
            int idx = _candidateItems.FindIndex(c => string.Equals(c.Id, _npcB, StringComparison.OrdinalIgnoreCase));
            _dropdownScrollOffset = Math.Clamp(idx >= 0 ? idx : 0, 0, Math.Max(0, _candidateItems.Count - DropdownMaxVisible));
            Game1.playSound("shwip");
            DeselectTextBoxes();
            return true;
        }

        // ── 3. 搜索框焦点 ──
        if (_searchBoxRect.Contains(x, y))
        {
            _searchBox.SelectMe();
            Game1.keyboardDispatcher.Subscriber = _searchBox;
            _descBox.Selected = false;
            return true;
        }

        // ── 4. 左侧列表交互（含 RulesTabView 标准滚动条检测） ──
        int listTop = _searchBoxRect.Bottom + 5;
        int listH = _listRect.Bottom - 4 - listTop;
        int visibleCount = listH / (RowHeight + RowGap);
        bool hasScroll = _filteredEntries.Count > visibleCount;

        if (hasScroll)
        {
            var trackRect = new Rectangle(_listRect.Right - 9, listTop, 5, listH);
            if (trackRect.Contains(x, y))
            {
                _isDraggingLeftScrollbar = true;
                int maxScroll = _filteredEntries.Count - visibleCount;
                float visibleRatio = Math.Clamp((float)visibleCount / _filteredEntries.Count, 0.15f, 1f);
                int thumbH = Math.Max(24, (int)(trackRect.Height * visibleRatio));
                UpdateLeftScrollFromMouse(y, trackRect, thumbH, maxScroll);
                return true;
            }
        }

        int itemRightPad = hasScroll ? 15 : 6;
        int itemW = _listRect.Width - 6 - itemRightPad;

        if (_listRect.Contains(x, y) && y >= listTop)
        {
            for (int i = 0; i < visibleCount && (_listScroll + i) < _filteredEntries.Count; i++)
            {
                var rowRect = new Rectangle(_listRect.X + 6, listTop + i * (RowHeight + RowGap), itemW, RowHeight);
                if (rowRect.Contains(x, y))
                {
                    SelectEntry(_filteredEntries[_listScroll + i].Key);
                    Game1.playSound("smallSelect");
                    return true;
                }
            }
            return true;
        }

        // ── 5. 描述文本框焦点 ──
        if (_descBox.ContainsPoint(x, y))
        {
            _descBox.Selected = true;
            Game1.keyboardDispatcher.Subscriber = _descBox;
            _searchBox.Selected = false;
            return true;
        }

        // ── 6. 底部操作按钮 ──
        if (_btnSave.Contains(x, y) && CanSave) { Save(); return true; }
        if (_btnRevert.Contains(x, y) && CanRevert) { RevertThis(); return true; }
        if (_btnDelete.Contains(x, y) && (_selectedKey != null || _editingEntry != null)) { RequestDeleteWithConfirmation(); return true; }
        if (_btnNew.Contains(x, y)) { NewRelation(); return true; }

        DeselectTextBoxes();
        return false;
    }

    // 1. 改为 public override
    public override void LeftClickHeld(int x, int y)
    {
        if (_isDraggingLeftScrollbar)
        {
            int listTop = _searchBoxRect.Bottom + 5;
            int listH = _listRect.Bottom - 4 - listTop;
            int visibleCount = listH / (RowHeight + RowGap);
            int maxScroll = Math.Max(0, _filteredEntries.Count - visibleCount);

            var trackRect = new Rectangle(_listRect.Right - 9, listTop, 5, listH);
            float visibleRatio = Math.Clamp((float)visibleCount / _filteredEntries.Count, 0.15f, 1f);
            int thumbH = Math.Max(24, (int)(trackRect.Height * visibleRatio));
            UpdateLeftScrollFromMouse(y, trackRect, thumbH, maxScroll);
        }
    }

    public override void ReleaseLeftClick(int x, int y)
    {
        _isDraggingLeftScrollbar = false;
    }

    private void UpdateLeftScrollFromMouse(int mouseY, Rectangle trackRect, int thumbH, int maxScroll)
    {
        if (maxScroll <= 0 || trackRect.Height <= thumbH) return;
        float progress = Math.Clamp((float)(mouseY - trackRect.Y - thumbH / 2) / (trackRect.Height - thumbH), 0f, 1f);
        _listScroll = (int)Math.Round(progress * maxScroll);
    }

    public override bool ReceiveScrollWheel(int direction)
    {
        int mx = Game1.getMouseX(), my = Game1.getMouseY();

        if (_activeDropdown != ActiveDropdown.None)
        {
            Rectangle headerRect = _activeDropdown == ActiveDropdown.NpcA ? _dropAHeaderRect : _dropBHeaderRect;
            var dropListRect = GetDropdownMenuRect(headerRect);
            if (dropListRect.Contains(mx, my) || headerRect.Contains(mx, my))
            {
                int maxScroll = Math.Max(0, _candidateItems.Count - DropdownMaxVisible);
                _dropdownScrollOffset = Math.Clamp(_dropdownScrollOffset - (direction > 0 ? 1 : -1), 0, maxScroll);
                Game1.playSound("shwip");
                return true;
            }
        }

        if (_listRect.Contains(mx, my))
        {
            int listTop = _searchBoxRect.Bottom + 5;
            int visible = (_listRect.Bottom - 4 - listTop) / (RowHeight + RowGap);
            _listScroll = Math.Clamp(_listScroll - (direction > 0 ? 1 : -1), 0, Math.Max(0, _filteredEntries.Count - visible));
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
        if (_activeDropdown != ActiveDropdown.None && key == Keys.Escape)
        {
            _activeDropdown = ActiveDropdown.None;
            return true;
        }

        // 搜索框防误触守卫（原生底层自动正确退格，不写冗余 Substring）
        if (_searchBox.Selected)
        {
            if (key == Keys.Escape || key == Keys.Enter)
            {
                _searchBox.Selected = false;
                if (Game1.keyboardDispatcher.Subscriber == _searchBox)
                    Game1.keyboardDispatcher.Subscriber = null;
                Game1.playSound("smallSelect");
                return true;
            }
            return true;
        }

        if (_descBox.Selected && Game1.keyboardDispatcher.Subscriber == _descBox)
        {
            if (key == Keys.Escape)
            {
                DeselectTextBoxes();
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
        _activeDropdown = ActiveDropdown.None;
        _isDraggingLeftScrollbar = false;
        DeselectTextBoxes();
    }

    private void DeselectTextBoxes()
    {
        _descBox.Selected = false;
        _searchBox.Selected = false;
        if (Game1.keyboardDispatcher.Subscriber == _descBox || Game1.keyboardDispatcher.Subscriber == _searchBox)
            Game1.keyboardDispatcher.Subscriber = null;
    }

    private Rectangle GetDropdownMenuRect(Rectangle headerRect)
    {
        int count = Math.Min(_candidateItems.Count, DropdownMaxVisible);
        int menuH = count * DropdownItemHeight + 8;
        return new Rectangle(headerRect.X, headerRect.Bottom + 2, headerRect.Width, menuH);
    }

    public override void Draw(SpriteBatch b, Rectangle area, int mx, int my)
    {
        DrawLeftList(b, mx, my);
        DrawRightForm(b, mx, my);

        if (_activeDropdown != ActiveDropdown.None)
        {
            DrawDropdownOverlay(b, mx, my);
        }
    }

    private void DrawLeftList(SpriteBatch b, int mx, int my)
    {
        DrawSectionCard(b, _listRect);

        // 1. 顶部搜索框
        DrawSingleLineBox(b, _searchBox);
        if (string.IsNullOrEmpty(_searchBox.Text))
        {
            CustomFontManager.DrawString(b, "🔍 搜索人物或关系...",
                new Vector2(_searchBox.X + 8, _searchBox.Y + 6),
                RulesTheme.TextMuted, CustomFontManager.SizeSmall);
        }

        // 分割线
        b.Draw(Game1.staminaRect,
            new Rectangle(_listRect.X + 6, _searchBoxRect.Bottom + 3, _listRect.Width - 12, 1),
            RulesTheme.BorderSoft * 0.8f);

        // 2. 极致排版列表（满容多显一行）
        int listTop = _searchBoxRect.Bottom + 5;
        int listH = _listRect.Bottom - 4 - listTop;
        int visibleCount = listH / (RowHeight + RowGap);
        bool hasScroll = _filteredEntries.Count > visibleCount;

        int itemRightPad = hasScroll ? 15 : 6;
        int itemW = _listRect.Width - 6 - itemRightPad;

        bool isMouseDown = Mouse.GetState().LeftButton == ButtonState.Pressed;

        for (int i = 0; i < visibleCount && (_listScroll + i) < _filteredEntries.Count; i++)
        {
            int idx = _listScroll + i;
            var item = _filteredEntries[idx];
            var rowRect = new Rectangle(_listRect.X + 6, listTop + i * (RowHeight + RowGap), itemW, RowHeight);

            bool isSel = string.Equals(item.Key, _selectedKey, StringComparison.OrdinalIgnoreCase);
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

            // 双 NPC 24px 微型头像并排展示
            int avatarSize = 24;
            var avARect = new Rectangle(drawRect.X + 6, drawRect.Y + (drawRect.Height - avatarSize) / 2, avatarSize, avatarSize);
            var avBRect = new Rectangle(drawRect.X + 6 + avatarSize + 3, drawRect.Y + (drawRect.Height - avatarSize) / 2, avatarSize, avatarSize);

            DrawMiniAvatar(b, item.NpcA, avARect, isSel);
            DrawMiniAvatar(b, item.NpcB, avBRect, isSel);

            int textLeft = avBRect.Right + 6;
            string label = $"{item.DisplayA} ⇄ {item.DisplayB}";
            if (item.IsTombstone) label += " [废弃]";
            else if (item.IsCustom) label += " [★自创]";

            string truncatedLabel = CustomFontManager.TruncateString(label, CustomFontManager.SizeSmall, drawRect.Right - textLeft - 6);
            Color textCol = item.IsTombstone ? RulesTheme.TextMuted
                          : (item.IsCustom ? RulesTheme.AccentAmber : RulesTheme.TextCharcoal);

            CustomFontManager.DrawString(b, truncatedLabel,
                new Vector2(textLeft, drawRect.Y + (drawRect.Height - 18) / 2f),
                textCol, CustomFontManager.SizeSmall);
        }

        // 3. 渲染 RulesTabView 标准滑动条
        if (hasScroll)
        {
            var trackRect = new Rectangle(_listRect.Right - 9, listTop, 5, listH);
            DrawScrollbarVisual(b, trackRect, visibleCount, _filteredEntries.Count, _listScroll, _isDraggingLeftScrollbar, mx, my);
        }
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
        Color thumbBg = isDragging ? RulesTheme.BorderBold
                      : (thumbHover ? RulesTheme.AccentGold : RulesTheme.BorderMid);

        b.Draw(Game1.staminaRect, thumbRect, thumbBg);
        IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(403, 383, 6, 6),
            thumbRect.X, thumbRect.Y, thumbRect.Width, thumbRect.Height, RulesTheme.BorderBold, 1f, false);
    }

    private void DrawRightForm(SpriteBatch b, int mx, int my)
    {
        DrawSectionCard(b, _formRect);

        if (string.IsNullOrEmpty(_selectedKey) && !_isCreatingNew)
        {
            CustomFontManager.DrawString(b, "从左侧列表选择一对人物关系进行查看与编辑",
                new Vector2(_formRect.X + 24, _formRect.Y + 24), RulesTheme.TextSecondary, CustomFontManager.SizeRegular);
            return;
        }

        int lx = _formRect.X + 12;

        // ── 0. 顶部双向生效提醒标语与防重警告 ──
        bool isDuplicate = IsDuplicateRelation(_npcA, _npcB);
        string bannerTip = isDuplicate
            ? "⚠ 该双向羁绊在小镇中已存在，无法重复添加！"
            : "⇄ 双向社交纽带（双方自动互通生效，无需对调重复添加）";
        Color bannerTipCol = isDuplicate ? RulesTheme.AccentRed : RulesTheme.AccentAmber;

        CustomFontManager.DrawStringBold(b, bannerTip,
            new Vector2(lx, _formRect.Y + 14), bannerTipCol, CustomFontManager.SizeSmall);

        // ── 1. 关系成员 (一) 行 ──
        DrawFieldLabel(b, "关系成员 (一)", lx, _dropAHeaderRect.Y + 4);
        DrawFixedAvatarBox(b, _npcA, _avBoxA);
        DrawDropdownHeader(b, _dropAHeaderRect, _npcA, _activeDropdown == ActiveDropdown.NpcA, mx, my);

        // ── 2. 关系成员 (二) 行 ──
        DrawFieldLabel(b, "关系成员 (二)", lx, _dropBHeaderRect.Y + 4);
        DrawFixedAvatarBox(b, _npcB, _avBoxB);
        DrawDropdownHeader(b, _dropBHeaderRect, _npcB, _activeDropdown == ActiveDropdown.NpcB, mx, my);

        // ── 3. 关系背景描述 ──
        DrawFieldLabel(b, "相互羁绊与背景描述", lx, (int)_descBox.Position.Y - 22);
        _descBox.Draw(b);

        // ── 4. 底部按钮（语义自适应与防重拦截） ──
        string deleteBtnText = (_editingEntry != null && _editingEntry.IsCustom)
            ? "彻底删除"
            : ((_editingEntry != null && _editingEntry.IsTombstone) ? "恢复羁绊" : "废弃羁绊");

        DrawFormButton(b, _btnSave, "✔ 保存关系", mx, my, isPrimary: true, isEnabled: CanSave);
        DrawFormButton(b, _btnRevert, "↺ 还原默认", mx, my, isPrimary: false, isEnabled: CanRevert);
        DrawFormButton(b, _btnDelete, deleteBtnText, mx, my, isPrimary: false, isEnabled: _editingEntry != null, isDanger: _editingEntry?.IsCustom == true);
        DrawFormButton(b, _btnNew, "+ 新建自创", mx, my, isPrimary: false);

        if (!string.IsNullOrEmpty(_statusMessage))
        {
            Color msgCol = _statusMessage.StartsWith("✔") ? RulesTheme.AccentGreen : RulesTheme.AccentAmber;
            CustomFontManager.DrawString(b, _statusMessage,
                new Vector2(lx, _btnSave.Y - 22), msgCol, CustomFontManager.SizeSmall);
        }
    }

    private void DrawFixedAvatarBox(SpriteBatch b, string npcId, Rectangle rect)
    {
        b.Draw(Game1.staminaRect, rect, RulesTheme.SurfaceSunken);
        IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(403, 383, 6, 6),
            rect.X - 1, rect.Y - 1, rect.Width + 2, rect.Height + 2, RulesTheme.BorderMid, 1.2f, false);

        var sprite = GetNpcAvatar(npcId);
        if (sprite.Texture != null && !sprite.SourceRect.IsEmpty)
        {
            b.Draw(sprite.Texture, rect, sprite.SourceRect, Color.White);
        }
        else
        {
            string initial = string.IsNullOrEmpty(npcId) ? "?" : npcId.Substring(0, 1);
            var isz = CustomFontManager.MeasureString(initial, CustomFontManager.SizeRegular);
            CustomFontManager.DrawString(b, initial,
                new Vector2(rect.X + (rect.Width - isz.X) / 2f, rect.Y + (rect.Height - isz.Y) / 2f),
                RulesTheme.TextMuted, CustomFontManager.SizeRegular);
        }
    }

    private void DrawDropdownHeader(SpriteBatch b, Rectangle headerRect, string npcId, bool isOpen, int mx, int my)
    {
        bool isHover = headerRect.Contains(mx, my);
        Color bg = isOpen ? RulesTheme.SurfaceActive : (isHover ? RulesTheme.SurfaceHover : RulesTheme.SurfaceCard);
        Color border = isOpen ? RulesTheme.BorderBold : (isHover ? RulesTheme.BorderBold : RulesTheme.BorderSoft);

        b.Draw(Game1.staminaRect, new Rectangle(headerRect.X + 1, headerRect.Y + 1, headerRect.Width - 2, headerRect.Height - 2), bg);
        IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(432, 439, 9, 9),
            headerRect.X, headerRect.Y, headerRect.Width, headerRect.Height, border, 2f, false);

        string disp = GetNpcDisplayName(npcId);
        string text = string.IsNullOrEmpty(disp) ? "选择角色..." : $"{disp} ({npcId})";

        CustomFontManager.DrawString(b, text,
            new Vector2(headerRect.X + 10, headerRect.Y + 5),
            RulesTheme.TextCharcoal, CustomFontManager.SizeRegular);

        int arrowY = headerRect.Y + (headerRect.Height - 12) / 2;
        Rectangle srcArrow = isOpen ? new Rectangle(421, 459, 11, 12) : new Rectangle(421, 472, 11, 12);
        b.Draw(Game1.mouseCursors, new Rectangle(headerRect.Right - 22, arrowY, 14, 14), srcArrow, Color.White);
    }

    private void DrawDropdownOverlay(SpriteBatch b, int mx, int my)
    {
        Rectangle headerRect = _activeDropdown == ActiveDropdown.NpcA ? _dropAHeaderRect : _dropBHeaderRect;
        string currentSelectedId = _activeDropdown == ActiveDropdown.NpcA ? _npcA : _npcB;
        var menuRect = GetDropdownMenuRect(headerRect);

        b.Draw(Game1.staminaRect, new Rectangle(menuRect.X + 2, menuRect.Y + 3, menuRect.Width, menuRect.Height), Color.Black * 0.25f);
        b.Draw(Game1.staminaRect, new Rectangle(menuRect.X + 1, menuRect.Y + 1, menuRect.Width - 2, menuRect.Height - 2), RulesTheme.SurfaceCard);
        IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(432, 439, 9, 9),
            menuRect.X, menuRect.Y, menuRect.Width, menuRect.Height, RulesTheme.BorderBold, 2f, false);

        int count = Math.Min(_candidateItems.Count, DropdownMaxVisible);
        bool hasScroll = _candidateItems.Count > DropdownMaxVisible;
        int itemW = hasScroll ? menuRect.Width - 14 : menuRect.Width - 4;

        int rightReserve = 28;
        int maxTextWidth = itemW - rightReserve - 12;

        for (int i = 0; i < count; i++)
        {
            int optIdx = _dropdownScrollOffset + i;
            if (optIdx >= _candidateItems.Count) break;

            var opt = _candidateItems[optIdx];
            var itemRect = new Rectangle(menuRect.X + 2, menuRect.Y + 4 + i * DropdownItemHeight, itemW, DropdownItemHeight);
            bool isHover = itemRect.Contains(mx, my);
            bool isSelected = string.Equals(opt.Id, currentSelectedId, StringComparison.OrdinalIgnoreCase);

            if (isHover || isSelected)
            {
                b.Draw(Game1.staminaRect, itemRect, isSelected ? RulesTheme.SurfaceActive : RulesTheme.SurfaceHover);
            }

            if (isSelected)
            {
                b.Draw(Game1.staminaRect, new Rectangle(itemRect.X, itemRect.Y + 3, 3, itemRect.Height - 6), RulesTheme.AccentGold);
            }

            int textClipX = itemRect.X + 10;
            var textClipRect = new Rectangle(textClipX, itemRect.Y, maxTextWidth, itemRect.Height);

            string fullLabel = $"{opt.DisplayName} ({opt.Id})";
            var fontSz = CustomFontManager.MeasureString(fullLabel, CustomFontManager.SizeRegular);
            float textY = itemRect.Y + (itemRect.Height - fontSz.Y) / 2f;
            Color textCol = isSelected ? RulesTheme.TextCharcoal : (isHover ? RulesTheme.TextCharcoal : RulesTheme.TextDarkBrown);

            bool isOverflow = fontSz.X > maxTextWidth;

            if (isHover && isOverflow)
            {
                float overflowDist = fontSz.X - maxTextWidth;
                double seconds = Game1.currentGameTime.TotalGameTime.TotalSeconds * 1.5;
                float pingPong = (float)((1.0 - Math.Cos(seconds)) / 2.0);
                float scrollOffset = pingPong * overflowDist;

                var prevScissor = b.GraphicsDevice.ScissorRectangle;
                var prevRasterizer = b.GraphicsDevice.RasterizerState;
                b.End();
                b.GraphicsDevice.ScissorRectangle = Rectangle.Intersect(prevScissor, textClipRect);
                b.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp, DepthStencilState.None, ScissorRasterizer);

                CustomFontManager.DrawString(b, fullLabel,
                    new Vector2(textClipX - scrollOffset, textY),
                    textCol, CustomFontManager.SizeRegular);

                b.End();
                b.GraphicsDevice.ScissorRectangle = prevScissor;
                b.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp, DepthStencilState.None, prevRasterizer);
            }
            else
            {
                string displayLabel = isOverflow
                    ? CustomFontManager.TruncateString(fullLabel, CustomFontManager.SizeRegular, maxTextWidth)
                    : fullLabel;

                CustomFontManager.DrawString(b, displayLabel,
                    new Vector2(textClipX, textY),
                    textCol, CustomFontManager.SizeRegular);
            }

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

            int maxScroll = _candidateItems.Count - DropdownMaxVisible;
            float ratio = (float)DropdownMaxVisible / _candidateItems.Count;
            int thumbH = Math.Max(20, (int)(trackRect.Height * ratio));
            int thumbY = trackRect.Y + (int)((trackRect.Height - thumbH) * ((float)_dropdownScrollOffset / maxScroll));

            b.Draw(Game1.staminaRect, new Rectangle(trackRect.X - 1, thumbY, trackRect.Width + 2, thumbH), RulesTheme.BorderBold);
        }
    }

    private void DrawMiniAvatar(SpriteBatch b, string npcId, Rectangle rect, bool isSelected)
    {
        b.Draw(Game1.staminaRect, rect, RulesTheme.SurfaceSunken);
        IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(403, 383, 6, 6),
            rect.X - 1, rect.Y - 1, rect.Width + 2, rect.Height + 2,
            isSelected ? RulesTheme.BorderBold : RulesTheme.BorderSoft, 1f, false);

        var sprite = GetNpcAvatar(npcId);
        if (sprite.Texture != null && !sprite.SourceRect.IsEmpty)
        {
            b.Draw(sprite.Texture, rect, sprite.SourceRect, Color.White);
        }
        else
        {
            string initial = string.IsNullOrEmpty(npcId) ? "?" : npcId.Substring(0, 1);
            var isz = CustomFontManager.MeasureString(initial, CustomFontManager.SizeSmall);
            CustomFontManager.DrawString(b, initial,
                new Vector2(rect.X + (rect.Width - isz.X) / 2f, rect.Y + (rect.Height - isz.Y) / 2f - 1),
                RulesTheme.TextMuted, CustomFontManager.SizeSmall);
        }
    }

    private static void DrawFieldLabel(SpriteBatch b, string text, int x, int y)
    {
        CustomFontManager.DrawStringBold(b, text, new Vector2(x, y), RulesTheme.TextDarkBrown, CustomFontManager.SizeRegular);
    }

    private static void DrawSectionCard(SpriteBatch b, Rectangle rect)
    {
        b.Draw(Game1.staminaRect, new Rectangle(rect.X + 1, rect.Y + 1, rect.Width - 2, rect.Height - 2), RulesTheme.SurfacePanel);
        IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(432, 439, 9, 9),
            rect.X, rect.Y, rect.Width, rect.Height, RulesTheme.BorderSoft, 2f, false);
    }

    private static void DrawSingleLineBox(SpriteBatch b, TextBox box)
    {
        var boxRect = new Rectangle(box.X, box.Y, box.Width, box.Height);
        b.Draw(Game1.staminaRect, new Rectangle(boxRect.X + 2, boxRect.Y + 2, boxRect.Width - 4, boxRect.Height - 4),
            box.Selected ? new Color(255, 252, 245) : new Color(245, 240, 230));

        IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(432, 439, 9, 9),
            boxRect.X, boxRect.Y, boxRect.Width, boxRect.Height,
            box.Selected ? RulesTheme.BorderBold : RulesTheme.BorderSoft, 2f, false);

        string text = box.Text ?? "";
        Vector2 sz = CustomFontManager.MeasureString(text, CustomFontManager.SizeRegular);
        CustomFontManager.DrawString(b, text, new Vector2(boxRect.X + 8, boxRect.Y + (boxRect.Height - sz.Y) / 2f - 1), RulesTheme.TextCharcoal, CustomFontManager.SizeRegular);

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

    // ── 缓存、搜索过滤与核心置顶排序引擎 ──

    private void BuildCaches()
    {
        _candidateItems.Clear();
        var idSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var candidates = NpcCandidateQueryService.GetCleanedCandidates();
            foreach (var (id, name) in candidates)
            {
                if (idSet.Add(id)) _candidateItems.Add((id, name));
            }
        }
        catch { }

        var baseline = LoadBaseline();
        if (baseline?.Relations != null)
        {
            foreach (var r in baseline.Relations)
            {
                TryRegisterNpc(r.NpcA, idSet);
                TryRegisterNpc(r.NpcB, idSet);
            }
        }

        var ov = ModEntry.NpcRelationOverlay?.LoadOrNull();
        if (ov?.Relations != null)
        {
            foreach (var r in ov.Relations)
            {
                TryRegisterNpc(r.NpcA, idSet);
                TryRegisterNpc(r.NpcB, idSet);
            }
        }

        _candidateItems.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.CurrentCultureIgnoreCase));
    }

    private void TryRegisterNpc(string? id, HashSet<string> idSet)
    {
        if (string.IsNullOrWhiteSpace(id) || idSet.Contains(id)) return;
        idSet.Add(id);
        _candidateItems.Add((id, GetNpcDisplayName(id)));
    }

    private void RebuildMergedView()
    {
        _entries.Clear();

        var baseline = LoadBaseline();
        var baselineKeys = new HashSet<string>(StringComparer.Ordinal);
        if (baseline != null)
        {
            foreach (var r in baseline.Relations)
            {
                if (string.IsNullOrWhiteSpace(r.NpcA) || string.IsNullOrWhiteSpace(r.NpcB)) continue;
                string key = NpcRelationOverlayService.MakeKey(r.NpcA, r.NpcB);
                baselineKeys.Add(key);
                _entries.Add(MakeEntry(key, r.NpcA, r.NpcB, r.Descriptions, isBaseline: true));
            }
        }

        var ov = ModEntry.NpcRelationOverlay?.LoadOrNull();
        if (ov != null)
        {
            foreach (var r in ov.Relations)
            {
                if (string.IsNullOrWhiteSpace(r.NpcA) || string.IsNullOrWhiteSpace(r.NpcB)) continue;
                string key = NpcRelationOverlayService.MakeKey(r.NpcA, r.NpcB);
                var existing = _entries.Find(e => string.Equals(e.Key, key, StringComparison.Ordinal));
                if (r.Disabled)
                {
                    if (existing != null)
                    {
                        existing.IsTombstone = true;
                        existing.IsCustom = false;
                        existing.Descriptions = new Dictionary<string, string>(r.Descriptions);
                    }
                    else
                    {
                        var tomb = MakeEntry(key, r.NpcA, r.NpcB, r.Descriptions, isBaseline: false);
                        tomb.IsTombstone = true;
                        _entries.Add(tomb);
                    }
                }
                else
                {
                    if (existing != null)
                    {
                        existing.Descriptions = new Dictionary<string, string>(r.Descriptions);
                        existing.IsCustom = !existing.IsBaseline;
                        existing.IsTombstone = false;
                    }
                    else
                    {
                        var custom = MakeEntry(key, r.NpcA, r.NpcB, r.Descriptions, isBaseline: false);
                        custom.IsCustom = true;
                        _entries.Add(custom);
                    }
                }
            }
        }

        // 自创永远置顶，废弃沉底
        _entries.Sort((a, b) =>
        {
            int rankA = (a.IsCustom && !a.IsTombstone) ? 0 : (!a.IsTombstone ? 1 : 2);
            int rankB = (b.IsCustom && !b.IsTombstone) ? 0 : (!b.IsTombstone ? 1 : 2);
            if (rankA != rankB) return rankA.CompareTo(rankB);
            return string.Compare(a.DisplayA, b.DisplayA, StringComparison.CurrentCultureIgnoreCase);
        });

        FilterEntries();
    }

    private void FilterEntries()
    {
        string query = _searchBox.Text.Trim();
        _filteredEntries.Clear();

        if (string.IsNullOrEmpty(query))
        {
            _filteredEntries.AddRange(_entries);
        }
        else
        {
            foreach (var e in _entries)
            {
                if (e.DisplayA.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    e.DisplayB.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    e.NpcA.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    e.NpcB.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                    e.Descriptions.Values.Any(d => d.Contains(query, StringComparison.OrdinalIgnoreCase)))
                {
                    _filteredEntries.Add(e);
                }
            }
        }

        int listTop = _searchBoxRect.Bottom + 5;
        int visibleCount = Math.Max(1, (_listRect.Bottom - 4 - listTop) / (RowHeight + RowGap));
        _listScroll = Math.Clamp(_listScroll, 0, Math.Max(0, _filteredEntries.Count - visibleCount));
    }

    private static NpcRelationsFile? LoadBaseline()
    {
        try { return ModEntry.SHelper.GameContent.Load<NpcRelationsFile>("ValleytalkReborn/NpcRelations"); }
        catch { return null; }
    }

    private RelationEntry MakeEntry(string key, string npcA, string npcB, Dictionary<string, string> descriptions, bool isBaseline)
    {
        return new RelationEntry
        {
            Key = key, NpcA = npcA, NpcB = npcB,
            DisplayA = GetNpcDisplayName(npcA),
            DisplayB = GetNpcDisplayName(npcB),
            IsBaseline = isBaseline,
            Descriptions = new Dictionary<string, string>(descriptions)
        };
    }

    private void SelectEntry(string key)
    {
        _selectedKey = key;
        _isCreatingNew = false;
        _statusMessage = null;
        _activeDropdown = ActiveDropdown.None;

        var entry = _entries.Find(e => string.Equals(e.Key, key, StringComparison.Ordinal));
        if (entry == null) { ClearForm(); return; }

        _editingEntry = entry;
        _npcA = entry.NpcA;
        _npcB = entry.NpcB;

        string currentText = entry.Descriptions.TryGetValue(LangKey, out var d) ? d : "";
        _descBox.SetText(currentText);

        DeselectTextBoxes();
    }

    /// <summary>
    /// 下拉框选中 NPC 后，若当前 (_npcA, _npcB) 组合在 _entries 中已存在，
    /// 自动切到该条目并载入其描述，避免用户误覆盖已有关系。
    /// </summary>
    private void TrySyncToExistingEntry()
    {
        if (string.IsNullOrEmpty(_npcA) || string.IsNullOrEmpty(_npcB)) return;
        if (string.Equals(_npcA, _npcB, StringComparison.OrdinalIgnoreCase)) return;

        string key = NpcRelationOverlayService.MakeKey(_npcA, _npcB);
        var entry = _entries.Find(e => string.Equals(e.Key, key, StringComparison.Ordinal));
        if (entry != null)
        {
            SelectEntry(key);
        }
    }

    private void ClearForm()
    {
        _editingEntry = null;
        _npcA = _candidateItems.Count > 0 ? _candidateItems[0].Id : "";
        _npcB = _candidateItems.Count > 1 ? _candidateItems[1].Id : "";
        _descBox.SetText("");
        DeselectTextBoxes();
    }

    private bool IsDuplicateRelation(string a, string b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b) || string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
            return false;

        string targetKey = NpcRelationOverlayService.MakeKey(a, b);
        return _entries.Any(e => !e.IsTombstone && string.Equals(e.Key, targetKey, StringComparison.OrdinalIgnoreCase) && e.Key != _selectedKey);
    }

    private bool CanSave =>
        !string.IsNullOrEmpty(_npcA) &&
        !string.IsNullOrEmpty(_npcB) &&
        !string.Equals(_npcA, _npcB, StringComparison.OrdinalIgnoreCase) &&
        !IsDuplicateRelation(_npcA, _npcB);

    private bool CanRevert => _editingEntry != null && _editingEntry.IsBaseline;

    private void Save()
    {
        if (!CanSave) return;
        var service = ModEntry.NpcRelationOverlay;
        if (service == null) return;

        if (IsDuplicateRelation(_npcA, _npcB))
        {
            Game1.playSound("cancel");
            _statusMessage = "该双向关系已存在，无需重复添加！";
            Game1.addHUDMessage(new HUDMessage("该双向关系已存在，无需重复添加", HUDMessage.error_type));
            return;
        }

        string key = NpcRelationOverlayService.MakeKey(_npcA, _npcB);
        var ov = service.LoadOrNull() ?? new NpcRelationOverlayFile();

        var sorted = NpcRelationOverlayService.MakeKey(_npcA, _npcB);
        var parts = sorted.Split('|');
        var entry = new NpcRelationEntry { NpcA = parts[0], NpcB = parts[1], Disabled = false };

        Dictionary<string, string> descs = new();
        if (_editingEntry != null && _editingEntry.Descriptions != null)
        {
            foreach (var kv in _editingEntry.Descriptions)
                descs[kv.Key] = kv.Value;
        }

        string text = _descBox.Text.Trim();
        descs[LangKey] = text.Length > 500 ? text.Substring(0, 500) : text;
        entry.Descriptions = descs;

        ov.Relations.RemoveAll(r => NpcRelationOverlayService.MakeKey(r.NpcA, r.NpcB).Equals(key, StringComparison.OrdinalIgnoreCase));
        ov.Relations.Add(entry);

        if (service.Save(ov, out _))
        {
            _statusMessage = "✔ 双向羁绊已保存（双方自动同步生效）";
            RebuildMergedView();
            SelectEntry(key);
            Game1.playSound("coin");
            Hub.RefreshEntries();
        }
    }

    private void RevertThis()
    {
        if (!CanRevert) return;
        var service = ModEntry.NpcRelationOverlay;
        if (service == null) return;

        string key = NpcRelationOverlayService.MakeKey(_npcA, _npcB);
        var ov = service.LoadOrNull() ?? new NpcRelationOverlayFile();
        ov.Relations.RemoveAll(r => NpcRelationOverlayService.MakeKey(r.NpcA, r.NpcB).Equals(key, StringComparison.OrdinalIgnoreCase));

        if (service.Save(ov, out _))
        {
            _statusMessage = "✔ 已还原为官方默认关系";
            RebuildMergedView();
            SelectEntry(key);
            Game1.playSound("coin");
            Hub.RefreshEntries();
        }
    }

    private void RequestDeleteWithConfirmation()
    {
        if (_editingEntry == null) return;
        string nameA = GetNpcDisplayName(_npcA);
        string nameB = GetNpcDisplayName(_npcB);

        if (_editingEntry.IsTombstone)
        {
            ExecuteToggleOrDelete();
            return;
        }

        string prompt = _editingEntry.IsCustom
            ? $"确定要彻底删除自创关系【{nameA} ⇄ {nameB}】吗？"
            : $"确定要废弃官方关系【{nameA} ⇄ {nameB}】吗？";

        Game1.activeClickableMenu = new ConfirmationDialog(
            prompt,
            _ =>
            {
                Game1.activeClickableMenu = Hub;
                ExecuteToggleOrDelete();
            },
            _ =>
            {
                Game1.activeClickableMenu = Hub;
            }
        );
    }

    private void ExecuteToggleOrDelete()
    {
        if (_editingEntry == null) return;
        var service = ModEntry.NpcRelationOverlay;
        if (service == null) return;

        var ov = service.LoadOrNull() ?? new NpcRelationOverlayFile();
        string key = _editingEntry.Key;

        if (_editingEntry.IsCustom)
        {
            ov.Relations.RemoveAll(r => NpcRelationOverlayService.MakeKey(r.NpcA, r.NpcB).Equals(key, StringComparison.OrdinalIgnoreCase));
            _statusMessage = "已彻底删除自创关系";
        }
        else if (_editingEntry.IsBaseline)
        {
            if (_editingEntry.IsTombstone)
            {
                ov.Relations.RemoveAll(r => NpcRelationOverlayService.MakeKey(r.NpcA, r.NpcB).Equals(key, StringComparison.OrdinalIgnoreCase));
                _statusMessage = "✔ 已重新恢复官方羁绊";
            }
            else
            {
                ov.Relations.RemoveAll(r => NpcRelationOverlayService.MakeKey(r.NpcA, r.NpcB).Equals(key, StringComparison.OrdinalIgnoreCase));
                ov.Relations.Add(new NpcRelationEntry { NpcA = _editingEntry.NpcA, NpcB = _editingEntry.NpcB, Disabled = true, Descriptions = new Dictionary<string, string>() });
                _statusMessage = "已废弃该关系（双方视作普通镇民）";
            }
        }

        if (service.Save(ov, out _))
        {
            RebuildMergedView();
            if (_filteredEntries.Count > 0)
            {
                SelectEntry(_editingEntry.IsCustom ? _filteredEntries[0].Key : key);
            }
            else ClearForm();

            Game1.playSound(_editingEntry.IsTombstone ? "coin" : "trashcan");
            Hub.RefreshEntries();
        }
    }

    private void NewRelation()
    {
        _selectedKey = null;
        _editingEntry = null;
        _isCreatingNew = true;
        _npcA = _candidateItems.Count > 0 ? _candidateItems[0].Id : "";
        _npcB = _candidateItems.Count > 1 ? _candidateItems[1].Id : "";
        _descBox.SetText("");
        _statusMessage = "已开启新建自创关系模式";
        DeselectTextBoxes();
    }

    private (Texture2D? Texture, Rectangle SourceRect) GetNpcAvatar(string npcId)
    {
        if (string.IsNullOrWhiteSpace(npcId)) return (null, Rectangle.Empty);
        if (_walkingSprites.TryGetValue(npcId, out var cached)) return cached;

        var sprite = LoadSpriteRobust(npcId);
        _walkingSprites[npcId] = sprite;
        return sprite;
    }

    private string GetNpcDisplayName(string npcId)
    {
        if (string.IsNullOrWhiteSpace(npcId)) return "";
        if (_displayNames.TryGetValue(npcId, out var cached)) return cached;

        string disp = ResolveDisplayName(npcId);
        _displayNames[npcId] = disp;
        return disp;
    }

    private static string ResolveDisplayName(string npcId)
    {
        NPC? npc = Game1.getCharacterFromName(npcId);
        if (!string.IsNullOrEmpty(npc?.displayName)) return npc.displayName;

        if (IsZh)
        {
            return npcId switch
            {
                "Kent" => "肯特",
                "Leo" => "雷欧",
                "Sandy" => "桑迪",
                "Marlon" => "马龙",
                "Gil" => "吉尔",
                "Gunther" => "冈瑟",
                "Krobus" => "科罗布斯",
                "Dwarf" => "矮人",
                "Wizard" => "法师",
                "Willy" => "威利",
                _ => npcId
            };
        }

        return npcId;
    }

    private static (Texture2D? Texture, Rectangle SourceRect) LoadSpriteRobust(string npcName)
    {
        if (string.IsNullOrWhiteSpace(npcName)) return (null, Rectangle.Empty);

        NPC? npc = Game1.getCharacterFromName(npcName);
        Texture2D? charTexture = null;
 
        try
        {
            if (npc?.Sprite?.Texture != null && !npc.Sprite.Texture.IsDisposed)
                charTexture = npc.Sprite.Texture;
        }
        catch { } 
 
        if (charTexture == null)
        {
            string assetName = npc?.getTextureName() ?? npcName;
            try { charTexture = Game1.content.Load<Texture2D>($"Characters\\{assetName}"); }
            catch { }
        }

        if (charTexture != null && !charTexture.IsDisposed)
        {
            int frameWidth = (npc?.Sprite != null && npc.Sprite.SpriteWidth > 0)
                ? npc.Sprite.SpriteWidth
                : (charTexture.Width >= 64 ? charTexture.Width / 4 : (charTexture.Width >= 32 ? charTexture.Width / 2 : charTexture.Width));

            int frameHeight = (npc?.Sprite != null && npc.Sprite.SpriteHeight > 0)
                ? npc.Sprite.SpriteHeight
                : Math.Min(charTexture.Height, frameWidth * 2);

            int topY = FindSpriteTopY(charTexture, frameWidth, frameHeight);
            int headHeight = Math.Min(frameWidth, charTexture.Height - topY);
            return (charTexture, new Rectangle(0, topY, frameWidth, headHeight));
        }

        try
        {
            var portraitTex = (npc?.Portrait != null && !npc.Portrait.IsDisposed)
                ? npc.Portrait
                : Game1.content.Load<Texture2D>($"Portraits\\{npcName}");

            if (portraitTex != null && !portraitTex.IsDisposed)
            {
                return (portraitTex, new Rectangle(0, 0, Math.Min(64, portraitTex.Width), Math.Min(64, portraitTex.Height)));
            }
        }
        catch { }

        return (null, Rectangle.Empty);
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
                    {
                        return y;
                    }
                }
            }
        }
        catch { }
        return 0;
    }
}