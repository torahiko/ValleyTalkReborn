using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewValley;

namespace ValleytalkReborn.UI
{
    /// <summary>
    /// 按钮文本自适应渲染器：三级字号降级 + 截断兜底。
    /// 确保多语言文本在固定宽度容器内可读。
    /// </summary>
    public static class ButtonTextRenderer
    {
        private static readonly float[] FontSizes = { 18f, 16f, 14f };
        private const float FallbackFontSize = 18f;
        private const float MinFontSize = 14f;
        private const float SafePadding = 0.9f;
        private const int HardFallbackMaxChars = 20;

        /// <summary>
        /// 测量并选择最优字号，返回渲染参数。
        /// </summary>
        /// <param name="text">待渲染的按钮文本</param>
        /// <param name="maxWidth">容器最大宽度（像素）</param>
        /// <param name="useBold">是否使用 Bold 字体</param>
        /// <returns>优化后的文本内容与最终字号</returns>
        public static (string finalText, float finalFontSize) PrepareButtonText(
            string text,
            float maxWidth,
            bool useBold = true)
        {
            if (string.IsNullOrEmpty(text))
            {
                ModEntry.SMonitor?.Log("[ButtonTextRenderer] 传入 null/空文本", LogLevel.Warn);
                return ("", FallbackFontSize);
            }

            if (maxWidth <= 0)
            {
                ModEntry.SMonitor?.Log($"[ButtonTextRenderer] 容器尺寸无效 maxWidth={maxWidth}", LogLevel.Error);
                return (text, FallbackFontSize);
            }

            foreach (float fontSize in FontSizes)
            {
                Vector2 size;
                try
                {
                    size = useBold
                        ? CustomFontManager.MeasureStringBold(text, fontSize)
                        : CustomFontManager.MeasureString(text, fontSize);
                }
                catch (Exception ex)
                {
                    ModEntry.SMonitor?.Log($"[ButtonTextRenderer] MeasureString 异常 fontSize={fontSize}: {ex.Message}", LogLevel.Error);
                    return (text, FallbackFontSize);
                }

                if (size.X <= maxWidth)
                    return (text, fontSize);
            }

            string truncated;
            try
            {
                truncated = CustomFontManager.TruncateString(text, MinFontSize, maxWidth);
            }
            catch (Exception ex)
            {
                ModEntry.SMonitor?.Log($"[ButtonTextRenderer] TruncateString 异常: {ex.Message}", LogLevel.Error);
                truncated = text.Substring(0, Math.Min(text.Length, HardFallbackMaxChars));
            }
            return (truncated, MinFontSize);
        }

        /// <summary>
        /// 直接绘制自适应按钮文本（居中）。
        /// </summary>
        /// <param name="b">SpriteBatch</param>
        /// <param name="text">原始文本</param>
        /// <param name="bounds">按钮矩形区域</param>
        /// <param name="color">文本颜色</param>
        /// <param name="useBold">是否使用 Bold 字体</param>
        public static void DrawButtonText(
            SpriteBatch b,
            string text,
            Rectangle bounds,
            Color color,
            bool useBold = true)
        {
            var (finalText, finalFontSize) = PrepareButtonText(text, bounds.Width * SafePadding, useBold);

            if (string.IsNullOrEmpty(finalText))
                return;

            Vector2 textSize = useBold
                ? CustomFontManager.MeasureStringBold(finalText, finalFontSize)
                : CustomFontManager.MeasureString(finalText, finalFontSize);

            float x = bounds.X + (bounds.Width - textSize.X) / 2f;
            float y = bounds.Y + (bounds.Height - textSize.Y) / 2f;

            if (useBold)
                CustomFontManager.DrawStringBold(b, finalText, new Vector2(x, y), color, finalFontSize);
            else
                CustomFontManager.DrawString(b, finalText, new Vector2(x, y), color, finalFontSize);
        }
    }
}
