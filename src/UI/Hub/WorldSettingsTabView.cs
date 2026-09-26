#nullable enable
using System;
using System.Collections.Generic;
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
    public enum WorldSettingsSubPage { DateAmbience, LocationFestival, PoiTuning }

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
    private readonly Rectangle[] _subPageRects = new Rectangle[3];
    private readonly WorldSubPageBase?[] _pages = new WorldSubPageBase?[3];

    public WorldSettingsTabView(IntegratedHubMenu hub) : base(hub)
    {
        _pages[(int)WorldSettingsSubPage.DateAmbience] = new DateAmbiencePage(hub);
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

    public override bool HasUnsavedChanges
    {
        get { for (int i = 0; i < _pages.Length; i++) { if (_pages[i]?.HasUnsavedChanges == true) return true; } return false; }
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
            int committed = 0;
            bool anyFailed = false;
            for (int i = 0; i < _pages.Length; i++)
            {
                var p = _pages[i];
                if (p == null || !p.HasUnsavedChanges) continue;
                if (p.TryCommitUnsavedChanges()) committed++;
                else anyFailed = true;
            }

            if (committed > 0)
                Game1.addHUDMessage(new HUDMessage(I18n.WorldSettings.SaveHudSuccess(committed), HUDMessage.newQuest_type));
            else if (anyFailed)
                Game1.addHUDMessage(new HUDMessage(I18n.WorldSettings.SaveHudPartialFail(), HUDMessage.error_type));
            else
            {
                Game1.playSound("smallSelect");
                Game1.addHUDMessage(new HUDMessage(I18n.WorldSettings.SaveHudAllSaved(), HUDMessage.newQuest_type));
            }
            return true;
        }

        if (_resetBtnRect.Contains(x, y))
        {
            Game1.playSound("trashcan");
            var page = _pages[(int)_currentSubPage];
            string label = GetSubPageLabel(_currentSubPage);
            Game1.activeClickableMenu = new BioValveWarningDialog(
                Hub,
                I18n.WorldSettings.ResetDialogTitle(),
                I18n.WorldSettings.ResetDialogSubtitle(label),
                new List<string>
                {
                    I18n.WorldSettings.ResetDialogBullet1(),
                    I18n.WorldSettings.ResetDialogBullet2()
                },
                I18n.WorldSettings.ResetDialogConfirm(),
                () => { Game1.activeClickableMenu = Hub; page?.ResetToBaseline(); },
                I18n.WorldSettings.ResetDialogCancel(),
                () => { Game1.activeClickableMenu = Hub; });
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

        string saveBtnText = I18n.WorldSettings.SaveButton();
        var (fittedSave, saveScale) = FitTextToWidth(saveBtnText, _saveBtnRect.Width - 16, CustomFontManager.SizeRegular, true);
        DrawActionButton(b, _saveBtnRect, fittedSave, mx, my, isPrimary: true, scale: saveScale);

        string resetBtnText = I18n.WorldSettings.ResetButton();
        var (fittedReset, resetScale) = FitTextToWidth(resetBtnText, _resetBtnRect.Width - 16, CustomFontManager.SizeRegular, true);
        DrawActionButton(b, _resetBtnRect, fittedReset, mx, my, isPrimary: false, scale: resetScale);
    }

    private void DrawLeftColumn(SpriteBatch b, int mx, int my)
    {
        DrawSectionCard(b, _leftColRect);

        CustomFontManager.DrawString(b, I18n.WorldSettings.NavTitle(),
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

            string rawLabel = GetSubPageLabel(page);
            int availableWidth = drawRect.Width - (avatarRect.Right + 8 - textLeft) - 28;
            var (fittedLabel, scale) = FitTextToWidth(rawLabel, availableWidth, CustomFontManager.SizeRegular, false);
            CustomFontManager.DrawString(b, fittedLabel,
                new Vector2(textLeft, drawRect.Y + (drawRect.Height - 20) / 2f),
                nameCol, CustomFontManager.SizeRegular, scale: scale);

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
        bool isPrimary = false, bool isEnabled = true, float scale = 1f)
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

        var sz = CustomFontManager.MeasureStringBold(label, CustomFontManager.SizeRegular) * scale;
        CustomFontManager.DrawStringBold(b, label,
            new Vector2(drawRect.X + (drawRect.Width - sz.X) / 2f, drawRect.Y + (drawRect.Height - sz.Y) / 2f),
            isEnabled ? RulesTheme.TextCharcoal : RulesTheme.TextMuted, CustomFontManager.SizeRegular, scale: scale);
    }

    private static string GetSubPageLabel(WorldSettingsSubPage page) => page switch
    {
        WorldSettingsSubPage.DateAmbience => I18n.WorldSettings.DateAmbience(),
        WorldSettingsSubPage.LocationFestival => I18n.WorldSettings.LocationFestival(),
        WorldSettingsSubPage.PoiTuning => I18n.WorldSettings.PoiTuning(),
        _ => page.ToString()
    };

    private static string GetSubPageDescription(WorldSettingsSubPage page) => page switch
    {
        WorldSettingsSubPage.DateAmbience => I18n.WorldSettings.DateAmbienceDesc(),
        WorldSettingsSubPage.LocationFestival => I18n.WorldSettings.LocationFestivalDesc(),
        WorldSettingsSubPage.PoiTuning => I18n.WorldSettings.PoiTuningDesc(),
        _ => string.Empty
    };

    /// <summary>
    /// 自适应文本至指定宽度：先缩放（1.0 → 0.9 → 0.75），再截断至完整单词/字符。
    /// </summary>
    private static (string fittedText, float scale) FitTextToWidth(string text, float maxWidth, float fontSize, bool isBold)
    {
        if (string.IsNullOrEmpty(text)) return (string.Empty, 1f);

        Func<string, float, Vector2> measureFunc = isBold
            ? (t, fs) => CustomFontManager.MeasureStringBold(t, fs)
            : (t, fs) => CustomFontManager.MeasureString(t, fs);

        float[] scales = { 1f, 0.9f, 0.75f };
        foreach (var scale in scales)
        {
            var measured = measureFunc(text, fontSize) * scale;
            if (measured.X <= maxWidth) return (text, scale);
        }

        float finalScale = 0.75f;
        int maxChars = (int)(text.Length * (maxWidth / (measureFunc(text, fontSize).X * finalScale)));
        if (maxChars <= 0) return ("...", finalScale);

        string truncated = text.Substring(0, Math.Min(maxChars, text.Length));

        if (!I18n.IsChinese && truncated.Contains(" "))
        {
            int lastSpace = truncated.LastIndexOf(' ');
            if (lastSpace > 0) truncated = truncated.Substring(0, lastSpace);
        }

        truncated += I18n.IsChinese ? "…" : "...";

        int guard = 0;
        while (truncated.Length > 1 && (measureFunc(truncated, fontSize) * finalScale).X > maxWidth)
        {
            truncated = truncated.Substring(0, truncated.Length - (I18n.IsChinese ? 2 : 4)) + (I18n.IsChinese ? "…" : "...");
            if (++guard > 100) return ("...", finalScale);
        }

        return (truncated, finalScale);
    }
}