using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;
using StardewValley.Menus;

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
    
    public static Rectangle Restore(IconTheme theme = IconTheme.Wood, IconState state = IconState.Normal)
        => Get(6, 0, theme, state);  // 还原
    
    public static Rectangle House(IconTheme theme = IconTheme.Wood, IconState state = IconState.Normal)
        => Get(16, 0, theme, state);  // 小镇
    
    public static Rectangle House2(IconTheme theme = IconTheme.Wood, IconState state = IconState.Normal)
        => Get(16, 4, theme, state);  // 小镇白
    
    public static Rectangle Star2(IconTheme theme = IconTheme.Wood, IconState state = IconState.Normal)
        => Get(7, 5, theme, state);  // 星星白
    
    

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

    /// <summary>
    /// 直接基于组件自身 sourceRect 绘制按钮（自动处理按压切片偏移与下沉动效）。
    /// 后续更换图标只需修改 RefreshActionButtons 中的 IconSource.* 调用，本方法无需变动。
    /// </summary>
    public static void DrawButton(
        SpriteBatch b,
        ClickableTextureComponent btn,
        bool isPressed,
        float layerDepth = 0.89f)
    {
        if (btn == null || btn.texture == null) return;

        Rectangle srcRect = btn.sourceRect;

        // 按下态自动向下偏移 2 行（32 源像素），与 IconState.Pressed 的切片布局一致
        if (isPressed)
        {
            srcRect = new Rectangle(srcRect.X, srcRect.Y + Size * (int)IconState.Pressed, srcRect.Width, srcRect.Height);
        }

        float scale = (float)btn.bounds.Width / Size;
        float yOffset = isPressed ? 1f * scale : 0f;
        Vector2 drawPos = new Vector2(btn.bounds.X, btn.bounds.Y + yOffset);

        b.Draw(
            btn.texture,
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