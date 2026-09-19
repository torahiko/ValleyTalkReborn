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
        // FONT-03: 全局字号整体加大一号，避免偏小偏细
        public const float SizeTitle = 25f;     // 顶栏 NPC 大标题（原 22f）
        public const float SizeRegular = 18.5f; // Tab 标签、按钮、小节标题、单行/多行框文本（原 17f）
        public const float SizeSmall = 15.5f;   // 底部提示、说明、标签项（原 14f）

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

        public static Vector2 MeasureString(string text, float fontSize = SizeRegular)
        {
            if (string.IsNullOrEmpty(text))
                return Vector2.Zero;

            DynamicSpriteFont font = GetFont(fontSize);
            if (font == null)
                return Game1.smallFont.MeasureString(text);

            // FontStashSharp 的 MeasureString 返回 Microsoft.Xna.Framework.Vector2，无需类型转换
            return font.MeasureString(text);
        }

        public static void DrawString(SpriteBatch b, string text, Vector2 position, Color color, float fontSize = SizeRegular)
        {
            if (string.IsNullOrEmpty(text))
                return;

            DynamicSpriteFont font = GetFont(fontSize);
            if (font == null)
            {
                b.DrawString(Game1.smallFont, text, position, color);
                return;
            }

            try
            {
                _renderer.Batch = b;
                font.DrawText(_renderer, text, position, color);
            }
            catch (Exception ex)
            {
                if (!_fallbackLogged)
                {
                    Log.Warning($"[FontManager] DrawText 运行期异常，已回退原版字体: {ex.Message}");
                    _fallbackLogged = true;
                }
                b.DrawString(Game1.smallFont, text, position, color);
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
