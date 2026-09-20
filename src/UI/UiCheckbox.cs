using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;
using StardewValley.Menus;

namespace ValleytalkReborn.UI;

/// <summary>最小复选框：无新贴图，用 staminaRect + 字体自绘勾选态。整行 label 区域均可点击。</summary>
internal sealed class UiCheckbox
{
    public Rectangle Bounds;
    public bool IsChecked;
    public string Label;

    private const int BoxSize = 28;
    private const int LabelOffsetX = 34;

    public UiCheckbox(Rectangle bounds, string label)
    {
        Bounds = bounds;
        Label = label;
    }

    /// <summary>命中含整行 label 区域即切换。</summary>
    public bool ReceiveLeftClick(int x, int y)
    {
        if (!Bounds.Contains(x, y))
            return false;
        IsChecked = !IsChecked;
        Game1.playSound("select");
        return true;
    }

    public void Draw(SpriteBatch b, int mx, int my)
    {
        var box = new Rectangle(Bounds.X, Bounds.Y, BoxSize, BoxSize);
        bool hover = Bounds.Contains(mx, my);

        // 底色
        b.Draw(Game1.staminaRect, box, hover ? new Color(255, 235, 205) : new Color(220, 205, 185) * 0.7f);
        IClickableMenu.drawTextureBox(b, Game1.mouseCursors,
            new Rectangle(432, 439, 9, 9),
            box.X, box.Y, box.Width, box.Height, Color.White, 2f, false);

        // 勾选标记
        if (IsChecked)
        {
            b.Draw(Game1.mouseCursors, new Vector2(box.X, box.Y),
                new Rectangle(236, 425, 9, 9), Color.White, 0f, Vector2.Zero, 3f, SpriteEffects.None, 1f);
        }

        if (!string.IsNullOrEmpty(Label))
        {
            CustomFontManager.DrawString(b, Label,
                new Vector2(Bounds.X + LabelOffsetX, Bounds.Y + 4),
                Game1.textColor, CustomFontManager.SizeRegular);
        }
    }
}
