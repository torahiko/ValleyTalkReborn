using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;

namespace ValleytalkReborn.UI;

/// <summary>
/// 按钮主题风格
/// </summary>
public enum IconTheme
{
    Wood = 0,  // 木质风格：普通(0-1行) / 按下(2-3行)
    Stone = 4  // 灰色石板：普通(4-5行) / 按下(6-7行)
}

/// <summary>
/// 按钮切片交互状态（为避免与 XNA 的 ButtonState 冲突，命名为 IconState）
/// </summary>
public enum IconState
{
    Normal = 0,  // 默认平视状态
    Pressed = 2  // 按下/凹陷状态（偏移 2 行）
}

public static class IconSource
{
    private const int Size = 16;

    /// <summary>
    /// 获取指定图标的切片矩形
    /// </summary>
    /// <param name="col">列号（0-16）</param>
    /// <param name="baseRow">基准行号（0=基础控制组，1=操作状态组）</param>
    /// <param name="theme">主题风格（Wood 或 Stone）</param>
    /// <param name="state">状态（Normal 或 Pressed）</param>
    public static Rectangle Get(int col, int baseRow, IconTheme theme = IconTheme.Wood, IconState state = IconState.Normal)
    {
        int actualRow = baseRow + (int)theme + (int)state;
        return new Rectangle(col * Size, actualRow * Size, Size, Size);
    }

    // ==================== 常用图标快速获取 ====================
    // Row 1：操作类
    public static Rectangle Edit(IconTheme theme = IconTheme.Wood, IconState state = IconState.Normal)
        => Get(15, 1, theme, state); // 铅笔编辑

    public static Rectangle Trash(IconTheme theme = IconTheme.Wood, IconState state = IconState.Normal)
        => Get(6, 1, theme, state);  // 垃圾桶删除

    public static Rectangle Star(IconTheme theme = IconTheme.Wood, IconState state = IconState.Normal)
        => Get(7, 1, theme, state);  // 星星收藏

    public static Rectangle Lock(IconTheme theme = IconTheme.Wood, IconState state = IconState.Normal)
        => Get(11, 1, theme, state); // 上锁

    public static Rectangle Unlock(IconTheme theme = IconTheme.Wood, IconState state = IconState.Normal)
        => Get(12, 1, theme, state); // 解锁
 
    // Row 0：控制类
    public static Rectangle Close(IconTheme theme = IconTheme.Wood, IconState state = IconState.Normal)
        => Get(0, 0, theme, state);  // X / 关闭

    public static Rectangle Settings(IconTheme theme = IconTheme.Wood, IconState state = IconState.Normal)
        => Get(14, 0, theme, state); // 齿轮设置

    public static Rectangle Search(IconTheme theme = IconTheme.Wood, IconState themeState = IconState.Normal)
        => Get(11, 0, theme, themeState); // 放大镜

    public static Rectangle Refresh(IconTheme theme = IconTheme.Wood, IconState state = IconState.Normal)
        => Get(7, 0, theme, state);  // 刷新

    /// <summary>
    /// 专用的通用带点击下沉动效的绘制方法
    /// </summary>
    public static void DrawButton(
        SpriteBatch b,
        Texture2D texture,
        Rectangle bounds,
        int col,
        int baseRow,
        IconTheme theme,
        bool isPressed,
        float layerDepth = 0.89f)
    {
        if (texture == null) return;

        IconState state = isPressed ? IconState.Pressed : IconState.Normal;
        Rectangle srcRect = Get(col, baseRow, theme, state);

        float scale = (float)bounds.Width / Size;
        
        // 当鼠标按下时，将整个图标向下物理偏移 1 个源像素（乘上放大倍数），产生极佳的机械手感
        float yOffset = isPressed ? 1f * scale : 0f;
        Vector2 drawPos = new Vector2(bounds.X, bounds.Y + yOffset);

        b.Draw(
            texture,
            drawPos,
            srcRect,
            Color.White,
            0f,
            Vector2.Zero,
            scale,
            SpriteEffects.None,
            layerDepth
        );
    }
}