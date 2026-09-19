using System;
using System.IO;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewModdingAPI;
using StardewValley;
using FontStashSharp;
using FontStashSharp.Interfaces;

namespace ValleytalkReborn
{
    /// <summary>
    /// 自定义字体管理器：基于 FontStashSharp，采用"西文优先 + CJK 回退"双字体 Fallback 链。
    /// Fallback 由 FontSystem.AddFont 的添加顺序天然实现，本模块对字符种类完全无感知。
    /// 作用域: Memory（IsLoaded / _fallbackLogged）、Config（字体文件名常量）。
    /// </summary>
    internal static class CustomFontManager
    {
        // 字号调整为整数，避免光栅化产生亚像素模糊
        public const float SizeTitle = 24f;     // 顶栏 NPC 大标题
        public const float SizeRegular = 18f;   // Tab 标签、按钮、小节标题、单行/多行框文本
        public const float SizeSmall = 15f;     // 底部提示、说明、标签项

        // 作用域: Config 常量
        private const string FontLatinFileName = "GoogleSans-Medium.ttf";
        private const string FontCjkFileName = "HarmonyOS_Sans_SC_Medium.ttf";

        // 作用域: Memory，仅 Cleanup() 释放
        private static FontSystem _fontSystem;

        // 每次绘制前由 DrawString 注入，绘制后不持有
        private static readonly SpriteBatchFontRenderer _renderer = new SpriteBatchFontRenderer();

        // 作用域: Memory，运行期 DrawText 回退日志节流
        private static bool _fallbackLogged;

        public static bool IsLoaded { get; private set; }

        public static void Initialize(IModHelper helper, IMonitor monitor)
        {
            if (IsLoaded)
                return;

            try
            {
                _fontSystem = new FontSystem(new FontSystemSettings
                {
                    TextureWidth = 1024,
                    TextureHeight = 1024,
                    // 提升超采样率至 2.0f，保证在高缩放或微小偏移下的文字清晰度
                    FontResolutionFactor = 2.0f
                });

                string cjkPath = Path.Combine(helper.DirectoryPath, "assets", "fonts", FontCjkFileName);
                if (!File.Exists(cjkPath))
                {
                    monitor.Log($"[FontManager] 缺少强制 CJK 字体: {cjkPath}，整体回退原版字体", LogLevel.Warn);
                    _fontSystem.Dispose();
                    _fontSystem = null;
                    return;
                }

                // 第一优先：西文 / 数字 / ASCII 标点
                string latinPath = Path.Combine(helper.DirectoryPath, "assets", "fonts", FontLatinFileName);
                bool latinPresent = File.Exists(latinPath);
                if (latinPresent)
                {
                    _fontSystem.AddFont(File.ReadAllBytes(latinPath));
                }
                else
                {
                    monitor.Log("[FontManager] 缺少西文字体 GoogleSans-Medium，西文降级由 HarmonyOS 渲染", LogLevel.Warn);
                }

                // 第二优先：CJK / 中文标点 / 符号回退
                _fontSystem.AddFont(File.ReadAllBytes(cjkPath));

                IsLoaded = true;

                if (latinPresent)
                {
                    Log.Information("[FontManager] 字体链装载成功: GoogleSans(Medium) -> HarmonyOS Sans SC(Medium)");
                }
                else
                {
                    Log.Information("[FontManager] 字体链装载成功: HarmonyOS Sans SC(Medium) 单字体模式");
                }
            }
            catch (Exception ex)
            {
                Log.Error($"[FontManager] 字体链装载失败，整体回退原版字体: {ex.Message}");
                _fontSystem?.Dispose();
                _fontSystem = null;
            }
        }

        public static DynamicSpriteFont GetFont(float fontSize)
        {
            if (!IsLoaded || _fontSystem == null)
                return null;

            try
            {
                return _fontSystem.GetFont(fontSize);
            }
            catch (Exception ex)
            {
                Log.Error($"[FontManager] GetFont({fontSize}) 失败: {ex.Message}");
                return null;
            }
        }

        public static Vector2 MeasureString(string text, float fontSize = SizeRegular, float scale = 1f)
        {
            if (string.IsNullOrEmpty(text))
                return Vector2.Zero;

            DynamicSpriteFont font = GetFont(fontSize);
            if (font == null)
                return Game1.smallFont.MeasureString(text) * scale;

            return font.MeasureString(text, new Vector2(scale, scale));
        }

        /// <summary>
        /// 按最大宽度截断文本（带省略号），使用 CustomFontManager 测量。
        /// 用于替代 UiHelper.TruncateString 中需要 SpriteFont 的重载。
        /// 未装载时回退 Game1.smallFont 测量，绝不抛异常。
        /// </summary>
        public static string TruncateString(string text, float fontSize, float maxWidth, float scale = 1f)
        {
            if (string.IsNullOrEmpty(text))
                return text;

            Func<string, float> measure;
            DynamicSpriteFont font = GetFont(fontSize);
            if (font != null)
                measure = s => font.MeasureString(s, new Vector2(scale, scale)).X;
            else
                measure = s => Game1.smallFont.MeasureString(s).X * scale;

            if (measure(text) <= maxWidth)
                return text;

            const string ellipsis = "...";
            float targetWidth = maxWidth - measure(ellipsis);
            if (targetWidth <= 0)
                return ellipsis;

            int low = 0, high = text.Length, best = 0;
            while (low <= high)
            {
                int mid = (low + high) / 2;
                if (measure(text.Substring(0, mid)) <= targetWidth)
                {
                    best = mid;
                    low = mid + 1;
                }
                else
                    high = mid - 1;
            }
            return text.Substring(0, best) + ellipsis;
        }

        public static void DrawString(SpriteBatch b, string text, Vector2 position, Color color, float fontSize = SizeRegular, float scale = 1f)
        {
            if (string.IsNullOrEmpty(text))
                return;

            // 强制对齐到整像素点，防止居中计算的小数坐标导致采样双线性模糊
            position = new Vector2(MathF.Floor(position.X), MathF.Floor(position.Y));

            DynamicSpriteFont font = GetFont(fontSize);
            if (font == null)
            {
                b.DrawString(Game1.smallFont, text, position, color, 0f, Vector2.Zero, scale, SpriteEffects.None, 1f);
                return;
            }

            try
            {
                _renderer.Batch = b;
                font.DrawText(_renderer, text, position, color, 0f, Vector2.Zero, new Vector2(scale, scale));
            }
            catch (Exception ex)
            {
                if (!_fallbackLogged)
                {
                    Log.Warning($"[FontManager] DrawText 运行期异常，已回退原版字体: {ex.Message}");
                    _fallbackLogged = true;
                }
                b.DrawString(Game1.smallFont, text, position, color, 0f, Vector2.Zero, scale, SpriteEffects.None, 1f);
            }
        }

        public static void Cleanup()
        {
            try
            {
                _fontSystem?.Dispose();
            }
            catch (Exception ex)
            {
                Log.Error($"[FontManager] Dispose 异常: {ex.Message}");
            }
            finally
            {
                _fontSystem = null;
                IsLoaded = false;
            }
        }

        /// <summary>
        /// IFontStashRenderer 实现：仅做"字形 → SpriteBatch.Draw"的纯转发，
        /// 绝不调用 Batch.Begin/End，绝不持有纹理引用。
        /// 所有类型均为 Microsoft.Xna.Framework.*，与 SpriteBatch 原生匹配。
        /// </summary>
        private sealed class SpriteBatchFontRenderer : IFontStashRenderer
        {
            public SpriteBatch Batch;

            GraphicsDevice IFontStashRenderer.GraphicsDevice => (Batch as GraphicsResource)?.GraphicsDevice;

            void IFontStashRenderer.Draw(Texture2D texture, Vector2 pos, Rectangle? src, Color color, float rotation, Vector2 scale, float depth)
            {
                Batch.Draw(texture, pos, src, color, rotation, Vector2.Zero, scale, SpriteEffects.None, depth);
            }
        }
    }
}