#nullable enable
using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
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

    public WorldSettingsTabView(IntegratedHubMenu hub) : base(hub) { }

    public override void OnActivated()
    {
        // 切入本子页时默认选中「日期与氛围」（T4），不丢子页各自状态。
        _currentSubPage = WorldSettingsSubPage.DateAmbience;
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
    }

    public override bool ReceiveLeftClick(int x, int y)
    {
        for (int i = 0; i < _subPageRects.Length; i++)
        {
            if (_subPageRects[i].Contains(x, y))
            {
                if (_currentSubPage != (WorldSettingsSubPage)i)
                {
                    _currentSubPage = (WorldSettingsSubPage)i;
                    Game1.playSound("smallSelect");
                }
                return true;
            }
        }
        return false;
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

        // ── 委托当前子页绘制具体内容（T4/T5/T6c/T7 各自填充）──
        switch (_currentSubPage)
        {
            case WorldSettingsSubPage.DateAmbience:
                DrawDateAmbience(b, _rightContentArea, mx, my);
                break;
            case WorldSettingsSubPage.RelationNetwork:
                DrawRelationNetwork(b, _rightContentArea, mx, my);
                break;
            case WorldSettingsSubPage.LocationFestival:
                DrawLocationFestival(b, _rightContentArea, mx, my);
                break;
            case WorldSettingsSubPage.PoiTuning:
                DrawPoiTuning(b, _rightContentArea, mx, my);
                break;
        }
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

    // ════════════════════════════════════════════════════════════════════════
    // 子页内容委托点（空占位）。T4/T5/T6c/T7 各自独立挂接，互不冲突。
    // ════════════════════════════════════════════════════════════════════════

    /// <summary>T4 日期与氛围：空占位，由 T4 工单填充。</summary>
    private void DrawDateAmbience(SpriteBatch b, Rectangle area, int mx, int my) { }

    /// <summary>T5 关系网络：空占位，由 T5 工单填充。</summary>
    private void DrawRelationNetwork(SpriteBatch b, Rectangle area, int mx, int my) { }

    /// <summary>T6c 地点与节日：空占位，由 T6c 工单填充。</summary>
    private void DrawLocationFestival(SpriteBatch b, Rectangle area, int mx, int my) { }

    /// <summary>T7 兴趣点调校：空占位，由 T7 工单填充。</summary>
    private void DrawPoiTuning(SpriteBatch b, Rectangle area, int mx, int my) { }
}
