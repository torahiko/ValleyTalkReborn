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
/// 约会氛围工坊子页：地图过滤引擎 + 单语言沉浸式配置面板 + 删除二次确认。
/// </summary>
internal sealed class DateAmbiencePage : WorldSubPageBase
{
    private const int RowHeight = 42;
    private const int RowGap = 6;
    private const int LabelWidth = 86;
    private const int CtrlHeight = 32;
    private const int CtrlGap = 10;

    private sealed class DateLocItem
    {
        public string Id = "";
        public string DisplayName = "";
        public string TargetMap = "";
        public bool IsCustom;
        public bool IsBaseline;
    }

    private readonly List<DateLocItem> _items = new();
    private int _scrollOffset;
    private string? _selectedId;
    private Rectangle _listRect;
    private Rectangle _formRect;

    // ── 控件 ──
    private readonly TextBox _nameBox;
    private readonly NumberStepper _heartsStepper;
    private readonly NumberStepper _startHourStepper;
    private readonly NumberStepper _endHourStepper;
    private readonly SimpleCheckbox _rainCheckbox;
    private readonly DialogueTextInputBox _descInputBox;

    // ── 内置下拉框 ──
    private Rectangle _mapDropdownHeaderRect;
    private bool _isMapDropdownOpen = false;
    private int _dropdownScrollOffset = 0;
    private readonly List<(string Id, string DisplayName)> _allMapOptions = new();
    private const int DropdownItemHeight = 34;
    private const int DropdownMaxVisible = 6;

    // 跑马灯用 Scissor 裁切状态机（超长文本悬停平滑往返滚动时，物理剪裁到选项格子内）
    private static readonly RasterizerState ScissorRasterizer = new() { CullMode = CullMode.None, ScissorTestEnable = true };

    // 步进器坐标缓存
    private Rectangle _heartsRect;
    private Rectangle _startHourRect;
    private Rectangle _endHourRect;

    private Rectangle _btnSave;
    private Rectangle _btnRevert;
    private Rectangle _btnDelete;
    private Rectangle _btnNew;

    // 表单工作数据
    private string _currentMap = "Town";
    private int _startVal = 1800;
    private int _endVal = 2130;
    private bool _isCustom;
    private bool _baselineExists;
    private string? _statusTip;

    private static bool IsZh => LocalizedContentManager.CurrentLanguageCode == LocalizedContentManager.LanguageCode.zh;

    public DateAmbiencePage(IntegratedHubMenu hub) : base(hub)
    {
        Texture2D boxTex = Game1.content.Load<Texture2D>("LooseSprites\\textBox") ?? Game1.mouseCursors;
        _nameBox = new TextBox(boxTex, null, Game1.smallFont, RulesTheme.TextCharcoal);
        // 关闭原版 TextBox 的像素宽度截断（Text setter 内置递归截断会静默损毁程序化赋值的长文本）
        _nameBox.limitWidth = false;

        _heartsStepper = new NumberStepper(Rectangle.Empty, 4, 0, 14, 1, I18n.WorldSettings.DateAmbiencePage.HeartsSuffix());
        _startHourStepper = new NumberStepper(Rectangle.Empty, 18, 6, 26, 1, ":00");
        _endHourStepper = new NumberStepper(Rectangle.Empty, 22, 6, 26, 1, ":00");

        _rainCheckbox = new SimpleCheckbox(I18n.WorldSettings.DateAmbiencePage.AllowRainyDays(), -1, 0, 0);

        _descInputBox = new DialogueTextInputBox(600, 500)
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
        LoadAllAvailableMaps();
        RefreshMergedList();
        if (_items.Count > 0 && _selectedId == null)
        {
            SelectLocation(_items[0].Id);
        }
    }

    public override void Layout(Rectangle area)
    {
        int listWidth = (int)(area.Width * 0.34f);
        _listRect = new Rectangle(area.X, area.Y, listWidth, area.Height);

        int formX = _listRect.Right + 12;
        int formW = area.Right - formX;
        _formRect = new Rectangle(formX, area.Y, formW, area.Height);

        int curX = _formRect.X + LabelWidth + 8;
        int curY = _formRect.Y + 8;
        int ctrlW = _formRect.Width - LabelWidth - 16;

        // 1. 地点名称
        _nameBox.X = curX;
        _nameBox.Y = curY;
        _nameBox.Width = ctrlW;
        _nameBox.Height = CtrlHeight;
        curY += CtrlHeight + CtrlGap;

        // 2. 目标地图下拉框
        _mapDropdownHeaderRect = new Rectangle(curX, curY, ctrlW, CtrlHeight);
        curY += CtrlHeight + CtrlGap;

        // 3. 好感门槛与雨天选项同行
        _heartsRect = new Rectangle(curX, curY, 130, CtrlHeight);
        _heartsStepper.SetBounds(_heartsRect);

        _rainCheckbox.bounds = new Rectangle(curX + 150, curY + 2, 28, 28);
        curY += CtrlHeight + CtrlGap;

        // 4. 生效时段
        int timeW = 110;
        _startHourRect = new Rectangle(curX, curY, timeW, CtrlHeight);
        _startHourStepper.SetBounds(_startHourRect);

        _endHourRect = new Rectangle(curX + timeW + 36, curY, timeW, CtrlHeight);
        _endHourStepper.SetBounds(_endHourRect);
        curY += CtrlHeight + CtrlGap + 2;

        // 5. 底部按钮固定在底边
        int btnY = _formRect.Bottom - 36;
        int btnW = (_formRect.Width - 18) / 4;
        _btnSave = new Rectangle(_formRect.X, btnY, btnW, 32);
        _btnRevert = new Rectangle(_formRect.X + btnW + 6, btnY, btnW, 32);
        _btnDelete = new Rectangle(_formRect.X + (btnW + 6) * 2, btnY, btnW, 32);
        _btnNew = new Rectangle(_formRect.X + (btnW + 6) * 3, btnY, btnW, 32);

        // 6. 环境氛围输入框充分撑满剩余垂直空间
        int descTop = curY;
        int descBottom = btnY - 8;
        int descH = Math.Max(110, descBottom - descTop);

        _descInputBox.Position = new Vector2(curX, descTop);
        _descInputBox.Extent = new Vector2(ctrlW, descH);
        _descInputBox.InvalidateLayout();
    }

    public override void Update(GameTime time)
    {
        _descInputBox.Update(time);
    }

    public override bool ReceiveLeftClick(int x, int y)
    {
        // 1. 下拉框展开处理
        if (_isMapDropdownOpen)
        {
            var dropListRect = GetDropdownMenuRect();
            if (dropListRect.Contains(x, y))
            {
                int clickIdx = (y - dropListRect.Y - 4) / DropdownItemHeight + _dropdownScrollOffset;
                if (clickIdx >= 0 && clickIdx < _allMapOptions.Count)
                {
                    _currentMap = _allMapOptions[clickIdx].Id;
                    _isMapDropdownOpen = false;
                    Game1.playSound("smallSelect");
                }
                return true;
            }

            if (_mapDropdownHeaderRect.Contains(x, y))
            {
                _isMapDropdownOpen = false;
                Game1.playSound("shwip");
                return true;
            }

            _isMapDropdownOpen = false;
            return true;
        }

        // 2. 左侧列表项选择
        if (_listRect.Contains(x, y))
        {
            int listTop = _listRect.Y + 8;
            int visibleCount = (_listRect.Height - 16) / (RowHeight + RowGap);
            for (int i = 0; i < visibleCount && (_scrollOffset + i) < _items.Count; i++)
            {
                var rowRect = new Rectangle(_listRect.X + 6, listTop + i * (RowHeight + RowGap), _listRect.Width - 12, RowHeight);
                if (rowRect.Contains(x, y))
                {
                    SelectLocation(_items[_scrollOffset + i].Id);
                    Game1.playSound("smallSelect");
                    return true;
                }
            }
            return true;
        }

        // 3. 展开下拉框
        if (_mapDropdownHeaderRect.Contains(x, y))
        {
            _isMapDropdownOpen = true;
            int idx = _allMapOptions.FindIndex(m => string.Equals(m.Id, _currentMap, StringComparison.OrdinalIgnoreCase));
            if (idx >= 0)
            {
                _dropdownScrollOffset = Math.Clamp(idx, 0, Math.Max(0, _allMapOptions.Count - DropdownMaxVisible));
            }
            Game1.playSound("shwip");
            UnfocusInputs();
            return true;
        }

        // 4. 输入框焦点
        if (new Rectangle(_nameBox.X, _nameBox.Y, _nameBox.Width, _nameBox.Height).Contains(x, y))
        {
            _nameBox.SelectMe();
            Game1.keyboardDispatcher.Subscriber = _nameBox;
            _descInputBox.Selected = false;
            return true;
        }

        if (_descInputBox.ContainsPoint(x, y))
        {
            _descInputBox.Selected = true;
            Game1.keyboardDispatcher.Subscriber = _descInputBox;
            _nameBox.Selected = false;
            return true;
        }

        // 5. 步进器与复选框
        if (_heartsStepper.ReceiveLeftClick(x, y)) return true;
        if (_startHourStepper.ReceiveLeftClick(x, y)) return true;
        if (_endHourStepper.ReceiveLeftClick(x, y)) return true;
        if (_rainCheckbox.bounds.Contains(x, y))
        {
            _rainCheckbox.receiveLeftClick(x, y);
            return true;
        }

        // 6. 底部操作按钮
        if (_btnSave.Contains(x, y)) { SaveCurrent(); return true; }
        if (_btnRevert.Contains(x, y) && _baselineExists && !_isCustom) { RevertCurrent(); return true; }
        if (_btnDelete.Contains(x, y) && _selectedId != null) { RequestDeleteWithConfirmation(); return true; }
        if (_btnNew.Contains(x, y)) { CreateNewCustom(); return true; }

        UnfocusInputs();
        return false;
    }

    public override bool ReceiveScrollWheel(int direction)
    {
        int mx = Game1.getMouseX(), my = Game1.getMouseY();

        if (_isMapDropdownOpen)
        {
            var dropListRect = GetDropdownMenuRect();
            if (dropListRect.Contains(mx, my) || _mapDropdownHeaderRect.Contains(mx, my))
            {
                int maxScroll = Math.Max(0, _allMapOptions.Count - DropdownMaxVisible);
                _dropdownScrollOffset = Math.Clamp(_dropdownScrollOffset - (direction > 0 ? 1 : -1), 0, maxScroll);
                Game1.playSound("shwip");
                return true;
            }
        }

        if (_listRect.Contains(mx, my))
        {
            int visible = (_listRect.Height - 16) / (RowHeight + RowGap);
            _scrollOffset = Math.Clamp(_scrollOffset - (direction > 0 ? 1 : -1), 0, Math.Max(0, _items.Count - visible));
            return true;
        }

        if (_descInputBox.ContainsPoint(mx, my))
        {
            _descInputBox.ReceiveScrollWheel(direction);
            return true;
        }

        return false;
    }

    public override bool ReceiveKeyPress(Keys key)
    {
        if (_isMapDropdownOpen && key == Keys.Escape)
        {
            _isMapDropdownOpen = false;
            return true;
        }

        if (_descInputBox.Selected && Game1.keyboardDispatcher.Subscriber == _descInputBox)
        {
            if (key == Keys.Escape) { UnfocusInputs(); return true; }
            return false;
        }

        if (_nameBox.Selected)
        {
            if (key == Keys.Escape) { UnfocusInputs(); return true; }
            return false;
        }

        return false;
    }

    public override void OnHidden()
    {
        _isMapDropdownOpen = false;
        UnfocusInputs();
    }

    public override void ResetToBaseline()
    {
        var service = ModEntry.DateLocationOverlay;
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

        _selectedId = null;

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
        return $"{_selectedId}|{_nameBox.Text.Trim()}|{_currentMap}|{_heartsStepper.Value}|{_startHourStepper.Value}|{_endHourStepper.Value}|{_rainCheckbox.isChecked}|{_descInputBox.Text.Trim()}";
    }

    private void RefreshFormSnapshot() => _formSnapshot = BuildFormFingerprint();

    public override bool HasUnsavedChanges => !string.IsNullOrEmpty(_selectedId) && BuildFormFingerprint() != _formSnapshot;

    public override bool TryCommitUnsavedChanges()
    {
        if (!HasUnsavedChanges) return true;
        SaveCurrent();
        return !HasUnsavedChanges;
    }

    private void UnfocusInputs()
    {
        _descInputBox.Selected = false;
        _nameBox.Selected = false;
        if (Game1.keyboardDispatcher.Subscriber == _descInputBox || Game1.keyboardDispatcher.Subscriber == _nameBox)
            Game1.keyboardDispatcher.Subscriber = null;
    }

    private Rectangle GetDropdownMenuRect()
    {
        int count = Math.Min(_allMapOptions.Count, DropdownMaxVisible);
        int menuH = count * DropdownItemHeight + 8;
        return new Rectangle(_mapDropdownHeaderRect.X, _mapDropdownHeaderRect.Bottom + 2, _mapDropdownHeaderRect.Width, menuH);
    }

    public override void Draw(SpriteBatch b, Rectangle area, int mx, int my)
    {
        DrawLeftList(b, mx, my);
        DrawRightForm(b, mx, my);

        if (_isMapDropdownOpen)
        {
            DrawDropdownOverlay(b, mx, my);
        }
    }

    private void DrawLeftList(SpriteBatch b, int mx, int my)
    {
        DrawSectionCard(b, _listRect);

        int listTop = _listRect.Y + 8;
        int visibleCount = (_listRect.Height - 16) / (RowHeight + RowGap);
        bool isMouseDown = Mouse.GetState().LeftButton == ButtonState.Pressed;

        for (int i = 0; i < visibleCount && (_scrollOffset + i) < _items.Count; i++)
        {
            int idx = _scrollOffset + i;
            var item = _items[idx];
            var rowRect = new Rectangle(_listRect.X + 6, listTop + i * (RowHeight + RowGap), _listRect.Width - 12, RowHeight);

            bool isSel = string.Equals(item.Id, _selectedId, StringComparison.OrdinalIgnoreCase);
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

            var iconSrc = new Rectangle(211, 428, 7, 6);
            b.Draw(Game1.mouseCursors, new Vector2(drawRect.X + 8, drawRect.Y + 13), iconSrc, Color.White, 0f, Vector2.Zero, 2.5f, SpriteEffects.None, 0.9f);

            string title = item.DisplayName;
            if (item.IsCustom) title += I18n.WorldSettings.DateAmbiencePage.CustomTag();
            CustomFontManager.DrawString(b, title, new Vector2(drawRect.X + 34, drawRect.Y + 11), RulesTheme.TextCharcoal, CustomFontManager.SizeRegular);
        }
    }

    private void DrawRightForm(SpriteBatch b, int mx, int my)
    {
        DrawSectionCard(b, _formRect);

        if (string.IsNullOrEmpty(_selectedId))
        {
            CustomFontManager.DrawString(b, I18n.WorldSettings.DateAmbiencePage.EmptySelectionHint(),
                new Vector2(_formRect.X + 24, _formRect.Y + 24), RulesTheme.TextSecondary, CustomFontManager.SizeRegular);
            return;
        }

        int lx = _formRect.X + 12;

        // 1. 地点名称
        DrawFieldLabel(b, I18n.WorldSettings.DateAmbiencePage.FieldLocationName(), lx, _nameBox.Y + 4);
        DrawSingleLineBox(b, _nameBox);

        // 2. 对应地图
        DrawFieldLabel(b, I18n.WorldSettings.DateAmbiencePage.FieldTargetMap(), lx, _mapDropdownHeaderRect.Y + 4);
        DrawDropdownHeader(b, mx, my);

        // 3. 好感与天气
        DrawFieldLabel(b, I18n.WorldSettings.DateAmbiencePage.FieldHeartsWeather(), lx, _heartsRect.Y + 4);
        _heartsStepper.Draw(b);
        _rainCheckbox.draw(b, 0, 0, Hub);

        // 4. 生效时段
        DrawFieldLabel(b, I18n.WorldSettings.DateAmbiencePage.FieldTimeWindow(), lx, _startHourRect.Y + 4);
        _startHourStepper.Draw(b);
        CustomFontManager.DrawString(b, I18n.WorldSettings.DateAmbiencePage.TimeWindowTo(),
            new Vector2(_startHourRect.Right + 10, _startHourRect.Y + 4), RulesTheme.TextSecondary, CustomFontManager.SizeRegular);
        _endHourStepper.Draw(b);

        // 5. 环境氛围
        DrawFieldLabel(b, I18n.WorldSettings.DateAmbiencePage.FieldAmbience(), lx, (int)_descInputBox.Position.Y);
        _descInputBox.Draw(b);

        // 6. 底部四个按钮
        string saveBtnText = I18n.WorldSettings.DateAmbiencePage.SaveButton();
        var (fittedSave, saveScale) = WorldSettingsTabView.FitTextToWidth(saveBtnText, _btnSave.Width - 16, CustomFontManager.SizeRegular, true);
        DrawFormButton(b, _btnSave, fittedSave, mx, my, isPrimary: true, scale: saveScale);

        string revertBtnText = I18n.WorldSettings.DateAmbiencePage.RevertButton();
        var (fittedRevert, revertScale) = WorldSettingsTabView.FitTextToWidth(revertBtnText, _btnRevert.Width - 16, CustomFontManager.SizeRegular, true);
        DrawFormButton(b, _btnRevert, fittedRevert, mx, my, isPrimary: false, isEnabled: _baselineExists && !_isCustom, scale: revertScale);

        string deleteBtnText = I18n.WorldSettings.DateAmbiencePage.DeleteButton();
        var (fittedDelete, deleteScale) = WorldSettingsTabView.FitTextToWidth(deleteBtnText, _btnDelete.Width - 16, CustomFontManager.SizeRegular, true);
        DrawFormButton(b, _btnDelete, fittedDelete, mx, my, isPrimary: false, isEnabled: _selectedId != null, scale: deleteScale);

        string newBtnText = I18n.WorldSettings.DateAmbiencePage.NewButton();
        var (fittedNew, newScale) = WorldSettingsTabView.FitTextToWidth(newBtnText, _btnNew.Width - 16, CustomFontManager.SizeRegular, true);
        DrawFormButton(b, _btnNew, fittedNew, mx, my, isPrimary: false, scale: newScale);

        if (!string.IsNullOrEmpty(_statusTip))
        {
            CustomFontManager.DrawString(b, _statusTip, new Vector2(lx, _btnSave.Y - 22), RulesTheme.AccentGreen, CustomFontManager.SizeSmall);
        }
    }

    private void DrawDropdownHeader(SpriteBatch b, int mx, int my)
    {
        bool isHover = _mapDropdownHeaderRect.Contains(mx, my);
        Color bg = _isMapDropdownOpen ? RulesTheme.SurfaceActive : (isHover ? RulesTheme.SurfaceHover : RulesTheme.SurfaceCard);
        Color border = _isMapDropdownOpen ? RulesTheme.BorderBold : (isHover ? RulesTheme.BorderBold : RulesTheme.BorderSoft);

        b.Draw(Game1.staminaRect, new Rectangle(_mapDropdownHeaderRect.X + 1, _mapDropdownHeaderRect.Y + 1, _mapDropdownHeaderRect.Width - 2, _mapDropdownHeaderRect.Height - 2), bg);
        IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(432, 439, 9, 9),
            _mapDropdownHeaderRect.X, _mapDropdownHeaderRect.Y, _mapDropdownHeaderRect.Width, _mapDropdownHeaderRect.Height, border, 2f, false);

        var curOption = _allMapOptions.FirstOrDefault(m => string.Equals(m.Id, _currentMap, StringComparison.OrdinalIgnoreCase));
        string disp = curOption != default ? curOption.DisplayName : _currentMap;

        CustomFontManager.DrawString(b, $"📍 {disp}",
            new Vector2(_mapDropdownHeaderRect.X + 8, _mapDropdownHeaderRect.Y + 5),
            RulesTheme.TextCharcoal, CustomFontManager.SizeRegular);

        int arrowY = _mapDropdownHeaderRect.Y + (_mapDropdownHeaderRect.Height - 12) / 2;
        Rectangle srcArrow = _isMapDropdownOpen ? new Rectangle(421, 459, 11, 12) : new Rectangle(421, 472, 11, 12);
        b.Draw(Game1.mouseCursors, new Rectangle(_mapDropdownHeaderRect.Right - 22, arrowY, 14, 14), srcArrow, Color.White);
    }

    private void DrawDropdownOverlay(SpriteBatch b, int mx, int my)
    {
        var menuRect = GetDropdownMenuRect();

        // 1. 浮层背部深度阴影
        b.Draw(Game1.staminaRect, new Rectangle(menuRect.X + 2, menuRect.Y + 3, menuRect.Width, menuRect.Height), Color.Black * 0.25f);

        // 2. 菜单框底板与外框
        b.Draw(Game1.staminaRect, new Rectangle(menuRect.X + 1, menuRect.Y + 1, menuRect.Width - 2, menuRect.Height - 2), RulesTheme.SurfaceCard);
        IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(432, 439, 9, 9),
            menuRect.X, menuRect.Y, menuRect.Width, menuRect.Height, RulesTheme.BorderBold, 2f, false);

        int count = Math.Min(_allMapOptions.Count, DropdownMaxVisible);
        bool hasScroll = _allMapOptions.Count > DropdownMaxVisible;
        int itemW = hasScroll ? menuRect.Width - 14 : menuRect.Width - 4;

        // 右侧保留宽度：选中勾选符 (✔) + 滚动条间距
        int rightReserve = 28;
        int maxTextWidth = itemW - rightReserve - 10;

        for (int i = 0; i < count; i++)
        {
            int optIdx = _dropdownScrollOffset + i;
            if (optIdx >= _allMapOptions.Count) break;

            var opt = _allMapOptions[optIdx];
            var itemRect = new Rectangle(menuRect.X + 2, menuRect.Y + 4 + i * DropdownItemHeight, itemW, DropdownItemHeight);
            bool isHover = itemRect.Contains(mx, my);
            bool isSelected = string.Equals(opt.Id, _currentMap, StringComparison.OrdinalIgnoreCase);

            if (isHover || isSelected)
            {
                b.Draw(Game1.staminaRect, itemRect, isSelected ? RulesTheme.SurfaceActive : RulesTheme.SurfaceHover);
            }

            if (isSelected)
            {
                b.Draw(Game1.staminaRect, new Rectangle(itemRect.X, itemRect.Y + 3, 3, itemRect.Height - 6), RulesTheme.AccentGold);
            }

            // 文本可用区域（用于 Scissor 裁切盒，防止超长文字往外漏）
            int textClipX = itemRect.X + 10;
            var textClipRect = new Rectangle(textClipX, itemRect.Y, maxTextWidth, itemRect.Height);

            var fontSz = CustomFontManager.MeasureString(opt.DisplayName, CustomFontManager.SizeRegular);
            float textY = itemRect.Y + (itemRect.Height - fontSz.Y) / 2f;
            Color textCol = isSelected ? RulesTheme.TextCharcoal : (isHover ? RulesTheme.TextCharcoal : RulesTheme.TextDarkBrown);

            bool isOverflow = fontSz.X > maxTextWidth;

            // ── 核心：悬停且超长时平滑左右跑马灯，未悬停时静态截断 ──
            if (isHover && isOverflow)
            {
                // 计算平滑往返位移（Ping-Pong）
                float overflowDist = fontSz.X - maxTextWidth;
                double seconds = Game1.currentGameTime.TotalGameTime.TotalSeconds * 1.5;
                // 余弦往返插值：0 ~ 1 往复平滑运动
                float pingPong = (float)((1.0 - Math.Cos(seconds)) / 2.0);
                float scrollOffset = pingPong * overflowDist;

                // 切换到带 ScissorTest 的批处理，对当前选项进行物理视口裁切
                var prevScissor = b.GraphicsDevice.ScissorRectangle;
                var prevRasterizer = b.GraphicsDevice.RasterizerState;
                b.End();
                b.GraphicsDevice.ScissorRectangle = Rectangle.Intersect(prevScissor, textClipRect);
                b.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp, DepthStencilState.None, ScissorRasterizer);

                CustomFontManager.DrawString(b, opt.DisplayName,
                    new Vector2(textClipX - scrollOffset, textY),
                    textCol, CustomFontManager.SizeRegular);

                // 还原批处理状态
                b.End();
                b.GraphicsDevice.ScissorRectangle = prevScissor;
                b.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp, DepthStencilState.None, prevRasterizer);
            }
            else
            {
                // 未悬停时：超长直接截断并追加省略号，绝不超出最大宽度
                string displayLabel = isOverflow
                    ? CustomFontManager.TruncateString(opt.DisplayName, CustomFontManager.SizeRegular, maxTextWidth)
                    : opt.DisplayName;

                CustomFontManager.DrawString(b, displayLabel,
                    new Vector2(textClipX, textY),
                    textCol, CustomFontManager.SizeRegular);
            }

            // 绘制右侧选中标记 (✔)
            if (isSelected)
            {
                string check = "✔";
                var csz = CustomFontManager.MeasureStringBold(check, CustomFontManager.SizeRegular);
                CustomFontManager.DrawStringBold(b, check,
                    new Vector2(itemRect.Right - csz.X - 6, itemRect.Y + (itemRect.Height - csz.Y) / 2f),
                    RulesTheme.AccentGold, CustomFontManager.SizeRegular);
            }
        }

        // 3. 滚动条
        if (hasScroll)
        {
            var trackRect = new Rectangle(menuRect.Right - 8, menuRect.Y + 4, 5, menuRect.Height - 8);
            b.Draw(Game1.staminaRect, trackRect, RulesTheme.SurfaceSunken);

            int maxScroll = _allMapOptions.Count - DropdownMaxVisible;
            float ratio = (float)DropdownMaxVisible / _allMapOptions.Count;
            int thumbH = Math.Max(20, (int)(trackRect.Height * ratio));
            int thumbY = trackRect.Y + (int)((trackRect.Height - thumbH) * ((float)_dropdownScrollOffset / maxScroll));

            b.Draw(Game1.staminaRect, new Rectangle(trackRect.X - 1, thumbY, trackRect.Width + 2, thumbH), RulesTheme.BorderBold);
        }
    }

    private static void DrawFieldLabel(SpriteBatch b, string text, int x, int y)
    {
        CustomFontManager.DrawStringBold(b, text, new Vector2(x, y), RulesTheme.TextDarkBrown, CustomFontManager.SizeRegular);
    }

    private static void DrawFormButton(SpriteBatch b, Rectangle rect, string label, int mx, int my, bool isPrimary = false, bool isEnabled = true, float scale = 1f)
    {
        bool isHover = isEnabled && rect.Contains(mx, my);
        bool isPressed = isHover && Mouse.GetState().LeftButton == ButtonState.Pressed;
        int pressOffset = isPressed ? 1 : 0;

        Color bg = !isEnabled ? new Color(225, 215, 200)
                 : isPressed ? RulesTheme.SurfaceSunken
                 : isPrimary ? (isHover ? RulesTheme.SurfaceHover : RulesTheme.SurfaceActive)
                 : (isHover ? RulesTheme.SurfaceHover : RulesTheme.SurfaceCard);

        Color border = !isEnabled ? RulesTheme.BorderSoft
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

        var sz = CustomFontManager.MeasureStringBold(label, CustomFontManager.SizeSmall) * scale;
        CustomFontManager.DrawStringBold(b, label,
            new Vector2(drawRect.X + (drawRect.Width - sz.X) / 2f, drawRect.Y + (drawRect.Height - sz.Y) / 2f),
            isEnabled ? RulesTheme.TextCharcoal : RulesTheme.TextMuted, CustomFontManager.SizeSmall, scale: scale);
    }

    // ── 智能地图过滤引擎（排除功能性死角，保留大地图与拓展户外） ──

    // ── 智能约会地图过滤引擎（仅保留户外大场景与标志性公共休闲室内） ──

    private void LoadAllAvailableMaps()
    {
        _allMapOptions.Clear();
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. 扫描存档内所有活跃地图实例
        if (Context.IsWorldReady && Game1.locations != null)
        {
            foreach (var loc in Game1.locations)
            {
                if (loc == null) continue;
                string id = loc.NameOrUniqueName ?? loc.Name ?? "";
                if (string.IsNullOrWhiteSpace(id) || set.Contains(id)) continue;

                if (IsValidDateMap(loc, id, loc.DisplayName))
                {
                    set.Add(id);
                    string disp = ResolveMapDisplayName(loc.DisplayName, id);
                    _allMapOptions.Add((id, $"{disp} ({id})"));
                }
            }
        }

        // 2. 扫描动态服务注册的地图（兼容部分未预载的第三方 Mod 大地图）
        try
        {
            var dynamicMaps = DynamicAssetQueryService.GetAvailableMaps();
            if (dynamicMaps != null)
            {
                foreach (var m in dynamicMaps)
                {
                    if (string.IsNullOrWhiteSpace(m.Id) || set.Contains(m.Id)) continue;
                    if (IsValidDateMap(null, m.Id, m.DisplayName))
                    {
                        set.Add(m.Id);
                        string disp = ResolveMapDisplayName(m.DisplayName, m.Id);
                        _allMapOptions.Add((m.Id, $"{disp} ({m.Id})"));
                    }
                }
            }
        }
        catch { }

        // 3. 原版核心约会大地图保底（确保空档或离线时也能选用经典场景）
        var coreDatingMaps = new (string Id, string NameZh, string NameEn)[]
        {
            ("Town", "鹈鹕镇", "Pelican Town"),
            ("Beach", "海滩", "Beach"),
            ("Mountain", "山区", "Mountain"),
            ("Forest", "煤矿森林", "Cindersap Forest"),
            ("Woods", "秘密森林", "Secret Woods"),
            ("Desert", "卡利科沙漠", "Calico Desert"),
            ("Saloon", "星果沙龙", "The Stardrop Saloon"),
            ("Railroad", "铁路", "Railroad"),
            ("Farm", "农场", "Farm")
        };

        foreach (var (cId, cZh, cEn) in coreDatingMaps)
        {
            if (!set.Contains(cId))
            {
                string disp = IsZh ? cZh : cEn;
                _allMapOptions.Add((cId, $"{disp} ({cId})"));
                set.Add(cId);
            }
        }

        _allMapOptions.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.CurrentCultureIgnoreCase));
    }

    /// <summary>
    /// 约会场景准入研判：严格剔除私人居所与功能房间，仅保留户外大地图与公共浪漫场所
    /// </summary>
    private static bool IsValidDateMap(GameLocation? loc, string id, string? rawDisp)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        string lower = id.ToLowerInvariant();

        // 1. 排除私人住宅、卧室、小屋（没人会在别人卧室约会）
        if (lower.Contains("house") ||
            lower.Contains("home") ||
            lower.Contains("bedroom") ||
            lower.Contains("cabin") ||
            lower.Contains("trailer") ||
            lower.Contains("tent") ||
            lower.Contains("shack") ||
            lower.Contains("hut") ||
            lower.Contains("manor") ||
            lower.Contains("room"))
        {
            // 特例豁免：星果沙龙与博物馆允许作为约会点
            if (!string.Equals(id, "Saloon", StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(id, "ArchaeologyHouse", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        // 2. 排除功能性、生产性、地下、危险区域
        if (lower.Contains("cellar") ||
            lower.Contains("barn") ||
            lower.Contains("coop") ||
            lower.Contains("shed") ||
            lower.Contains("greenhouse") ||
            lower.Contains("farmcave") ||
            lower.Contains("slimehutch") ||
            lower.Contains("basement") ||
            lower.Contains("locker") ||
            lower.Contains("attic") ||
            (lower.Contains("bathhouse_") && !lower.Contains("pool")) ||
            lower.Contains("sewer") ||
            lower.Contains("bugland") ||
            lower.Contains("witchswamp") ||
            lower.Contains("mine") ||
            lower.Contains("cave") ||
            lower.Contains("dungeon") ||
            lower.Contains("volcano"))
        {
            return false;
        }

        // 3. 排除技术地图、过场动画、假层、废墟
        if (lower.Contains("fake") ||
            lower.Contains("ruins") ||
            lower.Contains("cutscene") ||
            lower.Contains("intro") ||
            lower.Contains("event") ||
            lower.Contains("holding") ||
            lower.Contains("staging") ||
            lower.Contains("backstage") ||
            lower.Contains("warp") ||
            lower.Contains("buffer") ||
            lower.Contains("test") ||
            lower.Contains("debug") ||
            lower.Contains("dummy") ||
            lower.StartsWith("temp"))
        {
            return false;
        }

        // 4. 检查 DisplayName 是否破损或缺失本地化
        if (IsInvalidDisplayName(rawDisp)) return false;

        // Custom_ 开头但 DisplayName 依然等于原 ID 的假地图直接丢弃
        if (id.StartsWith("Custom_", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(rawDisp, id, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        // 5. 准入规则：
        // A. 户外大场景（IsOutdoors == true）直接通过
        if (loc != null && loc.IsOutdoors)
            return true;

        // B. 原版知名室内公共约会场所白名单
        if (string.Equals(id, "Saloon", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(id, "MovieTheater", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(id, "IslandResort", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(id, "ArchaeologyHouse", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(id, "BathHouse_Pool", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // C. 第三方 Mod 中的公共社交/休闲场所（酒馆、咖啡馆、餐厅、广场、海滨）
        if (lower.Contains("tavern") ||
            lower.Contains("inn") ||
            lower.Contains("cafe") ||
            lower.Contains("restaurant") ||
            lower.Contains("lounge") ||
            lower.Contains("plaza") ||
            lower.Contains("square") ||
            lower.Contains("park"))
        {
            return true;
        }

        // 其余未识别的封闭室内场景全部拒绝
        return false;
    }

    private static bool IsInvalidDisplayName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return true;
        string lower = name.ToLowerInvariant();
        return lower.Contains("no translation") ||
               lower.Contains("missing translation") ||
               lower.Contains("(no translation:") ||
               (lower.StartsWith("{{") && lower.EndsWith("}}"));
    }

    private static string ResolveMapDisplayName(string? rawDisp, string id)
    {
        string disp = id;
        if (!string.IsNullOrWhiteSpace(rawDisp)) 
        {
            try
            {
                disp = StardewValley.TokenizableStrings.TokenParser.ParseText(rawDisp);
            }
            catch
            {
                disp = rawDisp;
            }
        }

        if (disp.StartsWith("Custom_", StringComparison.OrdinalIgnoreCase))
        {
            disp = disp.Substring(7);
        }

        return disp;
    }

    private void RefreshMergedList()
    {
        _items.Clear();
        var baselineDict = DateLocationRegistry.Locations ?? new();
        var overlay = ModEntry.DateLocationOverlay?.LoadOrNull();

        var allIds = new HashSet<string>(baselineDict.Keys, StringComparer.OrdinalIgnoreCase);
        if (overlay?.Entries != null)
        {
            foreach (var id in overlay.Entries.Keys) allIds.Add(id);
        }
        if (overlay?.RemovedLocationIds != null)
        {
            foreach (var id in overlay.RemovedLocationIds) allIds.Remove(id);
        }

        foreach (var id in allIds)
        {
            baselineDict.TryGetValue(id, out var baseline);
            DateLocationInfo? ovEntry = null;
            overlay?.Entries?.TryGetValue(id, out ovEntry);

            var info = ovEntry ?? baseline;
            string displayName = IsZh ? (info?.DisplayNameZh ?? id) : (info?.DisplayNameEn ?? id);
            if (string.IsNullOrWhiteSpace(displayName)) displayName = id;

            _items.Add(new DateLocItem
            {
                Id = id,
                DisplayName = displayName,
                TargetMap = info?.TargetMap ?? "",
                IsBaseline = baseline != null,
                IsCustom = id.StartsWith("Custom_", StringComparison.OrdinalIgnoreCase)
            });
        }

        _items.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.CurrentCultureIgnoreCase));
    }

    private void SelectLocation(string id)
    {
        _selectedId = id;
        _statusTip = null;
        _isMapDropdownOpen = false;

        var baselineDict = DateLocationRegistry.Locations ?? new();
        var overlay = ModEntry.DateLocationOverlay?.LoadOrNull();

        baselineDict.TryGetValue(id, out var baseline);
        DateLocationInfo? ovEntry = null;
        overlay?.Entries?.TryGetValue(id, out ovEntry);

        var info = ovEntry ?? baseline;
        _baselineExists = baseline != null;
        _isCustom = id.StartsWith("Custom_", StringComparison.OrdinalIgnoreCase);

        if (info != null)
        {
            _nameBox.Text = IsZh ? info.DisplayNameZh : info.DisplayNameEn;
            _currentMap = string.IsNullOrEmpty(info.TargetMap) ? id : info.TargetMap;
            _heartsStepper.Value = Math.Clamp(info.RequiredHearts, 0, 14);
            _rainCheckbox.isChecked = info.AllowRainyDays;

            TimeWindowConverter.TryParse(info.TimeWindow, out _startVal, out _endVal);
            _startHourStepper.Value = _startVal / 100;
            _endHourStepper.Value = _endVal / 100;

            _descInputBox.SetText(IsZh ? info.ContextDescriptionZh : info.ContextDescriptionEn);
        }
        UnfocusInputs();
        RefreshFormSnapshot();
    }

    private void SaveCurrent()
    {
        if (string.IsNullOrEmpty(_selectedId)) return;
        var service = ModEntry.DateLocationOverlay;
        if (service == null) return;

        var ov = service.LoadOrNull() ?? new DateLocationOverlayFile();

        DateLocationRegistry.Locations.TryGetValue(_selectedId, out var baseline);
        ov.Entries.TryGetValue(_selectedId, out var existing);
        var original = existing ?? baseline;

        string currentText = _descInputBox.Text.Trim();
        string currentName = _nameBox.Text.Trim();

        var entry = new DateLocationInfo
        {
            LocationId = _selectedId,
            TargetMap = _currentMap,
            RequiredHearts = _heartsStepper.Value,
            AllowRainyDays = _rainCheckbox.isChecked,
            TimeWindow = $"{_startHourStepper.Value * 100:D4}-{_endHourStepper.Value * 100:D4}",

            DisplayNameZh = IsZh ? currentName : (original?.DisplayNameZh ?? currentName),
            DisplayNameEn = !IsZh ? currentName : (original?.DisplayNameEn ?? currentName),
            ContextDescriptionZh = IsZh ? currentText : (original?.ContextDescriptionZh ?? currentText),
            ContextDescriptionEn = !IsZh ? currentText : (original?.ContextDescriptionEn ?? currentText)
        };

        ov.Entries[_selectedId] = entry;
        ov.RemovedLocationIds.Remove(_selectedId);

        if (service.Save(ov, out _))
        {
            _statusTip = "✔ 约会氛围已保存";
            RefreshMergedList();
            Game1.playSound("coin");
            Game1.addHUDMessage(new HUDMessage("✔ 约会配置已更新", HUDMessage.newQuest_type));
            Hub.RefreshEntries();
            RefreshFormSnapshot();
        }
    }

    private void RevertCurrent()
    {
        if (string.IsNullOrEmpty(_selectedId) || !_baselineExists) return;
        var service = ModEntry.DateLocationOverlay;
        if (service == null) return;

        var ov = service.LoadOrNull() ?? new DateLocationOverlayFile();
        ov.Entries.Remove(_selectedId);
        ov.RemovedLocationIds.Remove(_selectedId);

        if (service.Save(ov, out _))
        {
            _statusTip = "✔ 已还原默认基准";
            RefreshMergedList();
            SelectLocation(_selectedId);
            Game1.playSound("coin");
            Hub.RefreshEntries();
        }
    }

    // ── 删除二次确认 ──
    private void RequestDeleteWithConfirmation()
    {
        if (string.IsNullOrEmpty(_selectedId)) return;
        string locName = _nameBox.Text.Trim();
        if (string.IsNullOrEmpty(locName)) locName = _selectedId;

        Game1.activeClickableMenu = new BioValveWarningDialog(
            Hub,
            I18n.WorldSettings.DateAmbiencePage.DeleteDialogTitle(),
            I18n.WorldSettings.DateAmbiencePage.DeleteDialogSubtitle(locName),
            new List<string>
            {
                I18n.WorldSettings.DateAmbiencePage.DeleteDialogBullet1(),
                I18n.WorldSettings.DateAmbiencePage.DeleteDialogBullet2()
            },
            I18n.WorldSettings.DateAmbiencePage.DeleteDialogConfirm(),
            () =>
            {
                Game1.activeClickableMenu = Hub;
                ExecuteDelete();
            },
            I18n.WorldSettings.DateAmbiencePage.DeleteDialogCancel(),
            () => { Game1.activeClickableMenu = Hub; });
    }

    private void ExecuteDelete()
    {
        if (string.IsNullOrEmpty(_selectedId)) return;
        var service = ModEntry.DateLocationOverlay;
        if (service == null) return;

        var ov = service.LoadOrNull() ?? new DateLocationOverlayFile();
        ov.Entries.Remove(_selectedId);
        if (_baselineExists && !ov.RemovedLocationIds.Contains(_selectedId))
            ov.RemovedLocationIds.Add(_selectedId);

        if (service.Save(ov, out _))
        {
            _statusTip = I18n.WorldSettings.DateAmbiencePage.DeleteSuccessHud();
            RefreshMergedList();
            if (_items.Count > 0) SelectLocation(_items[0].Id);
            Game1.playSound("trashcan");
            Hub.RefreshEntries();
        }
    }

    private void CreateNewCustom()
    {
        string newId = "Custom_Date_" + DateTime.Now.ToString("MMddHHmmss");
        var ov = ModEntry.DateLocationOverlay?.LoadOrNull() ?? new DateLocationOverlayFile();

        ov.Entries[newId] = new DateLocationInfo
        {
            LocationId = newId,
            DisplayNameZh = I18n.WorldSettings.DateAmbiencePage.NewLocationDefaultNameZh(),
            DisplayNameEn = I18n.WorldSettings.DateAmbiencePage.NewLocationDefaultNameEn(),
            TargetMap = !string.IsNullOrEmpty(_currentMap) ? _currentMap : "Town",
            RequiredHearts = 4,
            AllowRainyDays = true,
            TimeWindow = "1800-2200",
            ContextDescriptionZh = I18n.WorldSettings.DateAmbiencePage.NewLocationDefaultDescZh(),
            ContextDescriptionEn = I18n.WorldSettings.DateAmbiencePage.NewLocationDefaultDescEn()
        };

        if (ModEntry.DateLocationOverlay?.Save(ov, out _) == true)
        {
            RefreshMergedList();
            SelectLocation(newId);
            Game1.playSound("coin");
            Hub.RefreshEntries();
        }
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
}