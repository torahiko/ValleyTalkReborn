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
    /// 内置 1px 微量墨晕/加粗补偿，防止细笔画与符号在低像素网格下被吞。
    /// </summary>
    internal static class CustomFontManager
    {
        public const float SizeTitle = 24f;     // 顶栏 NPC 大标题
        public const float SizeRegular = 18f;   // Tab 标签、按钮、小节标题、单行/多行框文本
        public const float SizeSmall = 15f;     // 底部提示、说明、标签项

        private const string FontLatinFileName = "RobotoSlab-Medium.ttf";
        private const string FontCjkFileName = "HarmonyOS_Sans_SC_Medium.ttf";
        private const string FontLatinBoldFileName = "RobotoSlab-Bold.ttf";
        private const string FontCjkBoldFileName = "HarmonyOS_Sans_SC_Bold.ttf";

        private static FontSystem _fontSystem;
        private static FontSystem _boldFontSystem;

        private static readonly SpriteBatchFontRenderer _renderer = new SpriteBatchFontRenderer();
        private static bool _fallbackLogged;

        public static bool IsLoaded { get; private set; }
        public static bool IsBoldLoaded { get; private set; }

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
                    // 改为 1.0f：1:1 严格对齐物理像素，杜绝二次下采样吃掉 # 等细笔画
                    FontResolutionFactor = 1.0f
                });

                string cjkPath = Path.Combine(helper.DirectoryPath, "assets", "fonts", FontCjkFileName);
                if (!File.Exists(cjkPath))
                {
                    monitor.Log($"[FontManager] 缺少强制 CJK 字体: {cjkPath}，整体回退原版字体", LogLevel.Warn);
                    _fontSystem.Dispose();
                    _fontSystem = null;
                    return;
                }

                string latinPath = Path.Combine(helper.DirectoryPath, "assets", "fonts", FontLatinFileName);
                bool latinPresent = File.Exists(latinPath);
                if (latinPresent)
                {
                    _fontSystem.AddFont(File.ReadAllBytes(latinPath));
                }

                _fontSystem.AddFont(File.ReadAllBytes(cjkPath));
                IsLoaded = true;

                InitializeBoldChain(helper, monitor);
            }
            catch (Exception ex)
            {
                Log.Error($"[FontManager] 字体链装载失败，整体回退原版字体: {ex.Message}");
                _fontSystem?.Dispose();
                _fontSystem = null;
            }
        }

        private static void InitializeBoldChain(IModHelper helper, IMonitor monitor)
        {
            try
            {
                string cjkPath = Path.Combine(helper.DirectoryPath, "assets", "fonts", FontCjkFileName);

                _boldFontSystem = new FontSystem(new FontSystemSettings
                {
                    TextureWidth = 1024,
                    TextureHeight = 1024,
                    FontResolutionFactor = 1.0f
                });

                string latinBoldPath = Path.Combine(helper.DirectoryPath, "assets", "fonts", FontLatinBoldFileName);
                if (File.Exists(latinBoldPath))
                {
                    _boldFontSystem.AddFont(File.ReadAllBytes(latinBoldPath));
                }

                string cjkBoldPath = Path.Combine(helper.DirectoryPath, "assets", "fonts", FontCjkBoldFileName);
                if (File.Exists(cjkBoldPath))
                {
                    _boldFontSystem.AddFont(File.ReadAllBytes(cjkBoldPath));
                }
                else if (File.Exists(cjkPath))
                {
                    _boldFontSystem.AddFont(File.ReadAllBytes(cjkPath));
                }

                IsBoldLoaded = true;
            }
            catch (Exception ex)
            {
                Log.Error($"[FontManager] Bold 字体链装载失败: {ex.Message}");
                _boldFontSystem?.Dispose();
                _boldFontSystem = null;
                IsBoldLoaded = false;
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

            // 严格对齐整像素
            position = new Vector2(MathF.Round(position.X), MathF.Round(position.Y));

            DynamicSpriteFont font = GetFont(fontSize);
            if (font == null)
            {
                b.DrawString(Game1.smallFont, text, position, color, 0f, Vector2.Zero, scale, SpriteEffects.None, 1f);
                return;
            }

            try
            {
                _renderer.Batch = b;

                // 核心：微量墨晕（向右和向下微偏移 1px 补充骨肉感，防止细笔画断裂）
                // 采用本体颜色的 35% 透明度，自然饱满又不显粗暴描边
                Color bleedColor = color * 0.35f;
                font.DrawText(_renderer, text, new Vector2(position.X + 1, position.Y), bleedColor, 0f, Vector2.Zero, new Vector2(scale, scale));
                font.DrawText(_renderer, text, new Vector2(position.X, position.Y + 1), bleedColor, 0f, Vector2.Zero, new Vector2(scale, scale));

                // 绘制主体
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

        private static DynamicSpriteFont GetBoldFont(float fontSize)
        {
            if (IsBoldLoaded && _boldFontSystem != null)
            {
                try
                {
                    return _boldFontSystem.GetFont(fontSize);
                }
                catch (Exception ex)
                {
                    Log.Error($"[FontManager] GetBoldFont({fontSize}) 失败: {ex.Message}");
                }
            }
            return GetFont(fontSize);
        }

        public static Vector2 MeasureStringBold(string text, float fontSize = SizeTitle, float scale = 1f)
        {
            if (string.IsNullOrEmpty(text))
                return Vector2.Zero;

            DynamicSpriteFont font = GetBoldFont(fontSize);
            if (font == null)
                return Game1.dialogueFont.MeasureString(text) * scale;

            return font.MeasureString(text, new Vector2(scale, scale));
        }

        public static void DrawStringBold(SpriteBatch b, string text, Vector2 position, Color color, float fontSize = SizeTitle, float scale = 1f)
        {
            if (string.IsNullOrEmpty(text))
                return;

            position = new Vector2(MathF.Round(position.X), MathF.Round(position.Y));

            DynamicSpriteFont font = GetBoldFont(fontSize);
            if (font == null)
            {
                b.DrawString(Game1.dialogueFont, text, position, color, 0f, Vector2.Zero, scale, SpriteEffects.None, 1f);
                return;
            }

            try
            {
                _renderer.Batch = b;

                // Bold 标题同样叠加微墨晕
                Color bleedColor = color * 0.35f;
                font.DrawText(_renderer, text, new Vector2(position.X + 1, position.Y), bleedColor, 0f, Vector2.Zero, new Vector2(scale, scale));
                font.DrawText(_renderer, text, new Vector2(position.X, position.Y + 1), bleedColor, 0f, Vector2.Zero, new Vector2(scale, scale));

                font.DrawText(_renderer, text, position, color, 0f, Vector2.Zero, new Vector2(scale, scale));
            }
            catch (Exception ex)
            {
                if (!_fallbackLogged)
                {
                    Log.Warning($"[FontManager] DrawText(Bold) 运行期异常，已回退原版字体: {ex.Message}");
                    _fallbackLogged = true;
                }
                b.DrawString(Game1.dialogueFont, text, position, color, 0f, Vector2.Zero, scale, SpriteEffects.None, 1f);
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

            try
            {
                _boldFontSystem?.Dispose();
            }
            catch (Exception ex)
            {
                Log.Error($"[FontManager] Dispose(Bold) 异常: {ex.Message}");
            }
            finally
            {
                _boldFontSystem = null;
                IsBoldLoaded = false;
            }
        }

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