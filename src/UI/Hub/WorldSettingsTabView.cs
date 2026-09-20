#nullable enable
using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewValley;
using StardewValley.Menus;

namespace ValleytalkReborn.UI;

/// <summary>
/// Tab4 世界设定页：左侧子页导航 + 右侧内容区。
/// 本子页只实现「壳 + 路由框架」；DateAmbience/RelationNetwork/LocationFestival/PoiTuning
/// 的具体内容由 T4/T5/T6c/T7 各自在对应的空占位委托点中挂接。
/// </summary>
internal sealed class WorldSettingsTabView : HubTabViewBase
{
    // 与世界设定子页一一对应。T4=DateAmbience, T5=RelationNetwork, T6c=LocationFestival, T7=PoiTuning。
    public enum WorldSettingsSubPage { DateAmbience, RelationNetwork, LocationFestival, PoiTuning }

    // 布局常量（与 Hub 既有 LeftPadding/RightPadding/TopPadding/BottomPadding 一致）
    private const int LeftPadding = 40;
    private const int RightPadding = 40;
    private const int TopPadding = 110;
    private const int BottomPadding = 75;

    private const int SubPageListWidth = 150;
    private const int SubPageRowHeight = 40;
    private const int SubPageRowGap = 6;
    private const int SubPageListRightPad = 12;

    private WorldSettingsSubPage _currentSubPage;
    private readonly Rectangle[] _subPageRects = new Rectangle[4];
    private Rectangle _rightContentArea;

    // 子页实例（与枚举序对齐）。仅 DateAmbience（T4）接线；其余三槽留待 T5/T6c/T7。
    private readonly WorldSubPageBase?[] _pages = new WorldSubPageBase?[4];

    public WorldSettingsTabView(IntegratedHubMenu hub) : base(hub)
    {
        _pages[(int)WorldSettingsSubPage.DateAmbience] = new DateAmbiencePage(hub);
    }

    public override void OnActivated()
    {
        // 切入本子页时默认选中「日期与氛围」（T4），不丢子页各自状态。
        _currentSubPage = WorldSettingsSubPage.DateAmbience;
        _pages[(int)_currentSubPage]?.OnShown();
    }

    public override void OnDeactivated()
    {
        _pages[(int)_currentSubPage]?.OnHidden();
    }

    public override void Update(GameTime time)
    {
        _pages[(int)_currentSubPage]?.Update(time);
    }

    public override void Layout(Rectangle menuBounds, Rectangle contentBounds)
    {
        base.Layout(menuBounds, contentBounds);

        int listX = contentBounds.X;
        int listY = contentBounds.Y;
        for (int i = 0; i < _subPageRects.Length; i++)
        {
            _subPageRects[i] = new Rectangle(listX, listY + i * (SubPageRowHeight + SubPageRowGap),
                SubPageListWidth, SubPageRowHeight);
        }

        int rightX = listX + SubPageListWidth + SubPageListRightPad;
        _rightContentArea = new Rectangle(rightX, contentBounds.Y,
            contentBounds.Width - SubPageListWidth - SubPageListRightPad, contentBounds.Height);
        _pages[(int)_currentSubPage]?.Layout(_rightContentArea);
    }

    public override bool ReceiveLeftClick(int x, int y)
    {
        for (int i = 0; i < _subPageRects.Length; i++)
        {
            if (_subPageRects[i].Contains(x, y))
            {
                if (_currentSubPage != (WorldSettingsSubPage)i)
                {
                    _pages[(int)_currentSubPage]?.OnHidden();
                    _currentSubPage = (WorldSettingsSubPage)i;
                    Game1.playSound("smallSelect");
                    _pages[(int)_currentSubPage]?.OnShown();
                }
                return true;
            }
        }
        return _pages[(int)_currentSubPage]?.ReceiveLeftClick(x, y) ?? false;
    }

    public override bool ReceiveKeyPress(Keys key)
    {
        return _pages[(int)_currentSubPage]?.ReceiveKeyPress(key) ?? false;
    }

    public override bool ReceiveScrollWheel(int direction)
    {
        return _pages[(int)_currentSubPage]?.ReceiveScrollWheel(direction) ?? false;
    }

    public override void Draw(SpriteBatch b, int mx, int my)
    {
        // ── 左侧子页导航条 ──
        for (int i = 0; i < _subPageRects.Length; i++)
        {
            var page = (WorldSettingsSubPage)i;
            DrawSubPageRow(b, _subPageRects[i], page, page == _currentSubPage, mx, my);
        }

        // ── 右侧内容区底板 ──
        b.Draw(Game1.staminaRect, _rightContentArea, new Color(0, 0, 0) * 0.04f);

        // ── 委托当前子页绘制具体内容 ──
        _pages[(int)_currentSubPage]?.Draw(b, _rightContentArea, mx, my);
    }

    private void DrawSubPageRow(SpriteBatch b, Rectangle rect, WorldSettingsSubPage page, bool isActive, int mx, int my)
    {
        bool isHover = rect.Contains(mx, my);
        SetHoveredTooltip(isHover ? GetSubPageLabel(page) : null);

        Color bg = isActive ? new Color(210, 180, 140)
                 : isHover ? new Color(255, 235, 205)
                 : new Color(139, 90, 43);

        b.Draw(Game1.staminaRect, new Rectangle(rect.X + 2, rect.Y + 2, rect.Width - 4, rect.Height - 4), bg);
        IClickableMenu.drawTextureBox(b, Game1.mouseCursors,
            new Rectangle(432, 439, 9, 9),
            rect.X, rect.Y, rect.Width, rect.Height, bg, 4f, false);

        string label = GetSubPageLabel(page);
        var size = CustomFontManager.MeasureStringBold(label, HubUi.RegularFontSize);
        var pos = new Vector2(
            (int)MathF.Round(rect.X + (rect.Width - size.X) / 2f),
            (int)MathF.Round(rect.Y + (rect.Height - size.Y) / 2f));
        CustomFontManager.DrawStringBold(b, label, pos,
            isActive ? Game1.textColor : Color.White * 0.95f, HubUi.RegularFontSize);
    }

    private static string GetSubPageLabel(WorldSettingsSubPage page)
    {
        return page switch
        {
            WorldSettingsSubPage.DateAmbience => I18n.WorldSettings.DateAmbience(),
            WorldSettingsSubPage.RelationNetwork => I18n.WorldSettings.RelationNetwork(),
            WorldSettingsSubPage.LocationFestival => I18n.WorldSettings.LocationFestival(),
            WorldSettingsSubPage.PoiTuning => I18n.WorldSettings.PoiTuning(),
            _ => page.ToString()
        };
    }
}
