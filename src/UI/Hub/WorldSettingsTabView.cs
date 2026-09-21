#nullable enable
using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewValley;
using StardewValley.Menus;

namespace ValleytalkReborn.UI;

/// <summary>
/// Tab4 世界设定页：左侧子页导航 + 右侧工作区。
/// 底部按钮下移贴近菜单边缘，画幅全高利用，消除空隙。
/// </summary>
internal sealed class WorldSettingsTabView : HubTabViewBase
{
    public enum WorldSettingsSubPage { DateAmbience, RelationNetwork, LocationFestival, PoiTuning }

    private const int NavColumnWidth = 190;
    private const int NavRowHeight = 44;
    private const int NavRowGap = 8;
    private const int HeaderHeight = 42;

    private Rectangle _leftColRect;
    private Rectangle _rightColRect;
    private Rectangle _rightPageWorkingArea;
    private Rectangle _saveBtnRect;
    private Rectangle _resetBtnRect;

    private WorldSettingsSubPage _currentSubPage;
    private readonly Rectangle[] _subPageRects = new Rectangle[4];
    private readonly WorldSubPageBase?[] _pages = new WorldSubPageBase?[4];

    public WorldSettingsTabView(IntegratedHubMenu hub) : base(hub)
    {
        _pages[(int)WorldSettingsSubPage.DateAmbience] = new DateAmbiencePage(hub);
        _pages[(int)WorldSettingsSubPage.RelationNetwork] = new RelationNetworkPage(hub);
        _pages[(int)WorldSettingsSubPage.LocationFestival] = new LocationFestivalPage(hub);
        _pages[(int)WorldSettingsSubPage.PoiTuning] = new PoiTuningPage(hub);
    }

    public override void OnActivated()
    {
        _currentSubPage = WorldSettingsSubPage.DateAmbience;

        if (_rightPageWorkingArea.Width > 0 && _rightPageWorkingArea.Height > 0)
        {
            _pages[(int)_currentSubPage]?.Layout(_rightPageWorkingArea);
        }

        _pages[(int)_currentSubPage]?.OnShown();
    }

    public override void OnDeactivated()
    {
        _pages[(int)_currentSubPage]?.OnHidden();
    }

    public override void Layout(Rectangle menuBounds, Rectangle contentBounds)
    {
        base.Layout(menuBounds, contentBounds);

        if (contentBounds.Width <= 0 || contentBounds.Height <= 0)
            return;

        int colGap = 12;
        int topY = contentBounds.Y;
        // 彻底撑满整个内容区垂直高度（底部按钮外移至外框衬底）
        int mainHeight = contentBounds.Height;

        // 左右两栏布局
        _leftColRect = new Rectangle(contentBounds.X, topY, NavColumnWidth, mainHeight);
        int rightX = _leftColRect.Right + colGap;
        int rightW = contentBounds.Right - rightX;
        _rightColRect = new Rectangle(rightX, topY, rightW, mainHeight);

        // 左侧导航卡片布局
        int itemW = _leftColRect.Width - 12;
        int startY = _leftColRect.Y + 40;
        for (int i = 0; i < _subPageRects.Length; i++)
        {
            _subPageRects[i] = new Rectangle(_leftColRect.X + 6, startY + i * (NavRowHeight + NavRowGap), itemW, NavRowHeight);
        }

        // 右侧子页面工作区：四周紧凑内缩 10px，顶部扣除标题栏
        _rightPageWorkingArea = new Rectangle(
            _rightColRect.X + 10,
            _rightColRect.Y + HeaderHeight + 8,
            _rightColRect.Width - 20,
            _rightColRect.Height - HeaderHeight - 16
        );

        // 底部按钮移至外框下边缘（对齐 RulesTabView 规范）
        int btnH = 36;
        int saveBtnW = 160;
        int resetBtnW = 140;
        int footerY = contentBounds.Bottom + 14;

        _saveBtnRect = new Rectangle(contentBounds.Right - saveBtnW, footerY, saveBtnW, btnH);
        _resetBtnRect = new Rectangle(_saveBtnRect.Left - resetBtnW - 10, footerY, resetBtnW, btnH);

        _pages[(int)_currentSubPage]?.Layout(_rightPageWorkingArea);
    }

    public override void Update(GameTime time)
    {
        base.Update(time);
        _pages[(int)_currentSubPage]?.Update(time);
    }

    public override bool ReceiveLeftClick(int x, int y)
    {
        // 1. 底部保存与重置按钮
        if (_saveBtnRect.Contains(x, y))
        {
            Game1.playSound("coin");
            Game1.addHUDMessage(new HUDMessage("✔ 世界设定已全部保存", HUDMessage.newQuest_type));
            return true;
        }

        if (_resetBtnRect.Contains(x, y))
        {
            Game1.playSound("trashcan");
            Game1.addHUDMessage(new HUDMessage($"已重置【{GetSubPageLabel(_currentSubPage)}】", HUDMessage.error_type));
            return true;
        }

        // 2. 左侧导航选项卡
        for (int i = 0; i < _subPageRects.Length; i++)
        {
            if (_subPageRects[i].Contains(x, y))
            {
                if (_currentSubPage != (WorldSettingsSubPage)i)
                {
                    _pages[(int)_currentSubPage]?.OnHidden();
                    _currentSubPage = (WorldSettingsSubPage)i;
                    Game1.playSound("smallSelect");

                    _pages[(int)_currentSubPage]?.Layout(_rightPageWorkingArea);
                    _pages[(int)_currentSubPage]?.OnShown();
                }
                return true;
            }
        }

        // 3. 子页面处理内部点击
        return _pages[(int)_currentSubPage]?.ReceiveLeftClick(x, y) ?? false;
    }

    // ── ★ 关键修复：转发鼠标按住拖拽事件给激活的子页面 ──
    public override void LeftClickHeld(int x, int y)
    {
        base.LeftClickHeld(x, y);
        _pages[(int)_currentSubPage]?.LeftClickHeld(x, y);
    }

    // ── ★ 关键修复：转发鼠标按键释放事件给激活的子页面 ──
    public override void ReleaseLeftClick(int x, int y)
    {
        base.ReleaseLeftClick(x, y);
        _pages[(int)_currentSubPage]?.ReleaseLeftClick(x, y);
    }

    public override bool ReceiveScrollWheel(int direction)
    {
        return _pages[(int)_currentSubPage]?.ReceiveScrollWheel(direction) ?? false;
    }

    public override bool ReceiveKeyPress(Keys key)
    {
        return _pages[(int)_currentSubPage]?.ReceiveKeyPress(key) ?? false;
    }

    public override void Draw(SpriteBatch b, int mx, int my)
    {
        SetHoveredTooltip("");

        DrawLeftColumn(b, mx, my);
        DrawRightColumn(b, mx, my);

        DrawActionButton(b, _saveBtnRect, "✔ 保存世界配置", mx, my, isPrimary: true);
        DrawActionButton(b, _resetBtnRect, "↺ 恢复本页默认", mx, my, isPrimary: false);
    }

    private void DrawLeftColumn(SpriteBatch b, int mx, int my)
    {
        DrawSectionCard(b, _leftColRect);

        CustomFontManager.DrawString(b, "设定模块",
            new Vector2(_leftColRect.X + 12, _leftColRect.Y + 12),
            RulesTheme.TextPrimary, CustomFontManager.SizeRegular);

        b.Draw(Game1.staminaRect,
            new Rectangle(_leftColRect.X + 8, _leftColRect.Y + 34, _leftColRect.Width - 16, 1),
            RulesTheme.BorderSoft * 0.85f);

        bool isMouseDown = Mouse.GetState().LeftButton == ButtonState.Pressed;

        for (int i = 0; i < _subPageRects.Length; i++)
        {
            var page = (WorldSettingsSubPage)i;
            var itemRect = _subPageRects[i];
            bool isSelected = (_currentSubPage == page);
            bool isHover = itemRect.Contains(mx, my);
            bool isPressed = isHover && isMouseDown;
            int pressOffset = isPressed ? 1 : 0;

            if (isHover)
            {
                SetHoveredTooltip(GetSubPageDescription(page));
            }

            Color bg = isSelected ? RulesTheme.SurfaceActive
                     : isPressed ? RulesTheme.SurfaceSunken
                     : isHover ? RulesTheme.SurfaceHover
                     : RulesTheme.SurfaceCard;

            Color borderCol = isSelected ? RulesTheme.BorderBold
                            : isPressed ? RulesTheme.BorderBold
                            : isHover ? RulesTheme.BorderMid
                            : RulesTheme.BorderSoft;

            if (!isPressed)
            {
                b.Draw(Game1.staminaRect,
                    new Rectangle(itemRect.X + 1, itemRect.Y + 2, itemRect.Width, itemRect.Height),
                    RulesTheme.Shadow);
            }

            var drawRect = new Rectangle(itemRect.X, itemRect.Y + pressOffset, itemRect.Width, itemRect.Height);

            b.Draw(Game1.staminaRect, new Rectangle(drawRect.X + 1, drawRect.Y + 1, drawRect.Width - 2, drawRect.Height - 2), bg);
            IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(432, 439, 9, 9),
                drawRect.X, drawRect.Y, drawRect.Width, drawRect.Height, borderCol, 2f, false);

            if (isSelected)
            {
                b.Draw(Game1.staminaRect,
                    new Rectangle(drawRect.X + 2, drawRect.Y + 3, 4, drawRect.Height - 6),
                    RulesTheme.AccentGold);
            }

            int avatarSize = 28;
            var avatarRect = new Rectangle(drawRect.X + 8, drawRect.Y + (drawRect.Height - avatarSize) / 2, avatarSize, avatarSize);
            b.Draw(Game1.staminaRect, avatarRect, RulesTheme.SurfaceSunken);
            IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(403, 383, 6, 6),
                avatarRect.X - 1, avatarRect.Y - 1, avatarRect.Width + 2, avatarRect.Height + 2,
                isSelected ? RulesTheme.BorderBold : (isHover ? RulesTheme.BorderMid : RulesTheme.BorderSoft), 1.2f, false);

            DrawSubPageIcon(b, page, avatarRect);

            int textLeft = avatarRect.Right + 8;
            Color nameCol = isSelected ? RulesTheme.TextCharcoal
                         : (isHover ? RulesTheme.TextCharcoal : RulesTheme.TextDarkBrown);

            CustomFontManager.DrawString(b, GetSubPageLabel(page),
                new Vector2(textLeft, drawRect.Y + (drawRect.Height - 20) / 2f),
                nameCol, CustomFontManager.SizeRegular);

            if (isSelected)
            {
                string checkMark = "✔";
                var csz = CustomFontManager.MeasureStringBold(checkMark, CustomFontManager.SizeSmall);
                CustomFontManager.DrawStringBold(b, checkMark,
                    new Vector2(drawRect.Right - csz.X - 8, drawRect.Y + (drawRect.Height - csz.Y) / 2f),
                    RulesTheme.AccentGold, CustomFontManager.SizeSmall);
            }
        }
    }

    private void DrawRightColumn(SpriteBatch b, int mx, int my)
    {
        DrawSectionCard(b, _rightColRect);

        string title = GetSubPageLabel(_currentSubPage);
        string desc = GetSubPageDescription(_currentSubPage);

        CustomFontManager.DrawStringBold(b, title,
            new Vector2(_rightColRect.X + 14, _rightColRect.Y + 12),
            RulesTheme.TextCharcoal, CustomFontManager.SizeRegular);

        float titleW = CustomFontManager.MeasureStringBold(title, CustomFontManager.SizeRegular).X;
        CustomFontManager.DrawString(b, $"- {desc}",
            new Vector2(_rightColRect.X + 22 + titleW, _rightColRect.Y + 14),
            RulesTheme.TextSecondary, CustomFontManager.SizeSmall);

        b.Draw(Game1.staminaRect,
            new Rectangle(_rightColRect.X + 8, _rightColRect.Y + HeaderHeight, _rightColRect.Width - 16, 1),
            RulesTheme.BorderSoft * 0.85f);

        _pages[(int)_currentSubPage]?.Draw(b, _rightPageWorkingArea, mx, my);
    }

    private static void DrawSubPageIcon(SpriteBatch b, WorldSettingsSubPage page, Rectangle bounds)
    {
        Rectangle src = page switch
        {
            WorldSettingsSubPage.DateAmbience => new Rectangle(211, 428, 7, 6),
            WorldSettingsSubPage.RelationNetwork => new Rectangle(66, 4, 14, 12),
            WorldSettingsSubPage.LocationFestival => new Rectangle(403, 496, 5, 14),
            WorldSettingsSubPage.PoiTuning => new Rectangle(147, 422, 8, 8),
            _ => Rectangle.Empty
        };

        if (src.Width > 0)
        {
            int targetSize = 18;
            float scale = (float)targetSize / Math.Max(src.Width, src.Height);
            int drawX = bounds.X + (bounds.Width - (int)(src.Width * scale)) / 2;
            int drawY = bounds.Y + (bounds.Height - (int)(src.Height * scale)) / 2;

            b.Draw(Game1.mouseCursors, new Vector2(drawX, drawY), src, Color.White, 0f, Vector2.Zero, scale, SpriteEffects.None, 0.9f);
        }
    }

    private static void DrawSectionCard(SpriteBatch b, Rectangle rect)
    {
        b.Draw(Game1.staminaRect, new Rectangle(rect.X + 1, rect.Y + 1, rect.Width - 2, rect.Height - 2),
            RulesTheme.SurfacePanel);
        IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(432, 439, 9, 9),
            rect.X, rect.Y, rect.Width, rect.Height, RulesTheme.BorderSoft, 2f, false);
    }

    private static void DrawActionButton(SpriteBatch b, Rectangle rect, string label, int mx, int my,
        bool isPrimary = false, bool isEnabled = true)
    {
        bool isHover = isEnabled && rect.Contains(mx, my);
        bool isPressed = isHover && Mouse.GetState().LeftButton == ButtonState.Pressed;
        int pressOffset = isPressed ? 1 : 0;

        Color bg = !isEnabled ? new Color(225, 215, 200)
                 : isPressed ? RulesTheme.SurfaceSunken
                 : isPrimary ? (isHover ? RulesTheme.SurfaceHover : RulesTheme.SurfaceActive)
                 : (isHover ? RulesTheme.SurfaceHover : RulesTheme.SurfaceCard);

        Color borderCol = !isEnabled ? RulesTheme.BorderSoft
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
            drawRect.X, drawRect.Y, drawRect.Width, drawRect.Height, borderCol, 2f, false);

        var sz = CustomFontManager.MeasureStringBold(label, CustomFontManager.SizeRegular);
        CustomFontManager.DrawStringBold(b, label,
            new Vector2(drawRect.X + (drawRect.Width - sz.X) / 2f, drawRect.Y + (drawRect.Height - sz.Y) / 2f),
            isEnabled ? RulesTheme.TextCharcoal : RulesTheme.TextMuted, CustomFontManager.SizeRegular);
    }

    private static string GetSubPageLabel(WorldSettingsSubPage page) => page switch
    {
        WorldSettingsSubPage.DateAmbience => I18n.WorldSettings.DateAmbience(),
        WorldSettingsSubPage.RelationNetwork => I18n.WorldSettings.RelationNetwork(),
        WorldSettingsSubPage.LocationFestival => I18n.WorldSettings.LocationFestival(),
        WorldSettingsSubPage.PoiTuning => I18n.WorldSettings.PoiTuning(),
        _ => page.ToString()
    };

    private static string GetSubPageDescription(WorldSettingsSubPage page) => page switch
    {
        WorldSettingsSubPage.DateAmbience => "约会事件与天气氛围",
        WorldSettingsSubPage.RelationNetwork => "居民社交网络与羁绊",
        WorldSettingsSubPage.LocationFestival => "区域探索与节日庆典",
        WorldSettingsSubPage.PoiTuning => "兴趣点权重与寻路偏好",
        _ => string.Empty
    };
}