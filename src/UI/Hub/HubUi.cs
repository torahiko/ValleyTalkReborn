using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;
using StardewValley.Menus;
using ValleytalkReborn;

namespace ValleytalkReborn.UI;

/// <summary>
/// 共享字体常量与 Tab 间复用的绘制原语。
/// 各 View 直接引用本类的字体常量，避免在 View 内重新定义别名。
/// </summary>
internal static class HubUi
{
    public const float TitleFontSize = CustomFontManager.SizeTitle;
    public const float TabFontSize = CustomFontManager.SizeRegular;
    public const float CardTitleFontSize = CustomFontManager.SizeRegular;
    public const float RegularFontSize = CustomFontManager.SizeRegular;
    public const float SmallFontSize = CustomFontManager.SizeSmall;

    /// <summary>
    /// 绘制鼠标悬停提示气泡（原 Hub.DrawHoverTextCustom 抽取）。
    /// </summary>
    public static void DrawHoverTextCustom(SpriteBatch b, string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        var sz = CustomFontManager.MeasureString(text, RegularFontSize);

        // ── 宽裕适中的内外边距 ──
        const int padX = 20;
        const int padY = 12;

        int boxW = (int)System.MathF.Ceiling(sz.X) + padX * 2;
        int boxH = (int)System.MathF.Ceiling(sz.Y) + padY * 2;

        int x = Game1.getOldMouseX() + 24;
        int y = Game1.getOldMouseY() + 24;
        var safe = Utility.getSafeArea();

        // 边界碰撞防溢出
        if (x + boxW > safe.Right)
            x = safe.Right - boxW;
        if (y + boxH > safe.Bottom)
        {
            x += 16;
            if (x + boxW > safe.Right)
                x = safe.Right - boxW;
            y = safe.Bottom - boxH;
        }
        if (x < safe.Left)
            x = safe.Left;
        if (y < safe.Top)
            y = safe.Top;

        // 1. 原版像素软阴影
        IClickableMenu.drawTextureBox(b, Game1.menuTexture, new Rectangle(0, 256, 60, 60),
            x + 4, y + 4, boxW, boxH, Color.Black * 0.28f, 0.65f, false);

        // 2. 星露谷原版暖白/浅亮羊皮纸底框（无白缝断层，边缘自带原生柔和像素勾边）
        IClickableMenu.drawTextureBox(b, Game1.menuTexture, new Rectangle(0, 256, 60, 60),
            x, y, boxW, boxH, new Color(255, 255, 250), 0.65f, false);

        // 3. 提示文字（垂直严格居中，间距舒适）
        float textY = y + (boxH - sz.Y) / 2f - 1;
        CustomFontManager.DrawString(b, text,
            new Vector2(x + padX, textY),
            Game1.textColor, RegularFontSize);
    }
}
