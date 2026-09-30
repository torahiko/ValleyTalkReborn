#nullable enable

using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewValley;
using StardewValley.Menus;

namespace ValleytalkReborn.UI
{
    /// <summary>
    /// 按钮视觉风格。配色逐值取自 Layer 1 D-3 表格。
    /// </summary>
    public enum ActionButtonStyle
    {
        Default,
        Primary,
        Danger,
        SoftRed,
        Romantic,
        Disabled
    }

    /// <summary>
    /// 统一动作按钮渲染器：阴影 + 底板 + 九宫格边框 + Bold 自适应文本。
    /// 无状态：hover 缩放由调用方以 ref float 持有，本类不缓存任何东西。
    /// 主线程专用，仅在菜单 Draw 阶段调用。
    /// </summary>
    internal static class ActionButtonRenderer
    {
        // ---- 私有只读色板（Layer 1 D-3）----
        private static readonly Color DefaultBgRest = new Color(225, 195, 155);
        private static readonly Color DefaultBgHover = new Color(255, 240, 215);
        private static readonly Color DefaultBorder = new Color(185, 150, 110);

        private static readonly Color PrimaryBgRest = new Color(255, 220, 130);
        private static readonly Color PrimaryBgHover = Color.Gold;
        private static readonly Color PrimaryBorder = new Color(210, 160, 60);

        private static readonly Color DangerBgRest = new Color(210, 85, 80);
        private static readonly Color DangerBgHover = new Color(245, 105, 105);
        private static readonly Color DangerBorder = new Color(175, 60, 55);

        private static readonly Color SoftRedBgRest = new Color(225, 118, 110);
        private static readonly Color SoftRedBgHover = new Color(245, 145, 138);
        private static readonly Color SoftRedBorder = new Color(180, 82, 75);

        private static readonly Color RomanticBgRest = new Color(245, 205, 215);
        private static readonly Color RomanticBgHover = new Color(255, 225, 235);
        private static readonly Color RomanticBorder = new Color(200, 120, 140);

        private static readonly Color DisabledBg = Color.LightGray * 0.6f;
        private static readonly Color DisabledBorder = Color.Gray * 0.5f;

        private static readonly Color GlowColor = new Color(255, 245, 220);
        private const float GlowMix = 0.55f;

        private static readonly Color TextOnLightBtn = new Color(52, 28, 16);
        private static readonly Color TextOnDarkBtn = new Color(255, 250, 242);
        private static readonly Color TextMuted = new Color(158, 138, 118);

        // 鼠标状态探针日志限发一次
        private static bool _mouseProbeLogged;

        /// <summary>
        /// 绘制一个动作按钮。整条绘制路径不捕获异常。
        /// </summary>
        public static void Draw(
            SpriteBatch b,
            Rectangle rect,
            string label,
            ref float hoverScale,
            int mx, int my,
            ActionButtonStyle style = ActionButtonStyle.Default,
            float fontSize = CustomFontManager.SizeRegular,
            bool isEnabled = true,
            bool hoverGlow = true,
            Func<bool>? isPressedFunc = null)
        {
            // 1. 状态
            bool isHover = isEnabled && style != ActionButtonStyle.Disabled && rect.Contains(mx, my);
            bool isPressed = isHover && (isPressedFunc != null ? isPressedFunc() : IsMouseDown());

            // 2. 动效
            float targetScale = (isHover && !isPressed) ? 1.025f : 1.0f;
            hoverScale += (targetScale - hoverScale) * 0.25f;

            int drawW = (int)MathF.Round(rect.Width * hoverScale);
            int drawH = (int)MathF.Round(rect.Height * hoverScale);
            int drawX = rect.X - (drawW - rect.Width) / 2;
            int drawY = rect.Y - (drawH - rect.Height) / 2;
            int pressOffset = isPressed ? 1 : 0;

            // 3. 阴影
            if (!isPressed)
            {
                b.Draw(
                    Game1.staminaRect,
                    new Rectangle(drawX + 1, drawY + (isHover ? 3 : 2), drawW, drawH),
                    Color.Black * (isHover ? 0.20f : 0.12f));
            }

            // 4. 配色
            Color bg;
            Color border;
            Color textCol;

            switch (style)
            {
                case ActionButtonStyle.Primary:
                    bg = isHover ? PrimaryBgHover : PrimaryBgRest;
                    border = PrimaryBorder;
                    textCol = TextOnLightBtn;
                    break;

                case ActionButtonStyle.Danger:
                    bg = isHover ? DangerBgHover : DangerBgRest;
                    border = DangerBorder;
                    textCol = TextOnDarkBtn;
                    break;

                case ActionButtonStyle.SoftRed:
                    bg = isHover ? SoftRedBgHover : SoftRedBgRest;
                    border = SoftRedBorder;
                    textCol = TextOnDarkBtn;
                    break;

                case ActionButtonStyle.Romantic:
                    bg = isHover ? RomanticBgHover : RomanticBgRest;
                    border = RomanticBorder;
                    textCol = TextOnLightBtn;
                    break;

                case ActionButtonStyle.Disabled:
                    bg = DisabledBg;
                    border = DisabledBorder;
                    textCol = TextMuted;
                    break;

                default:
                    bg = isHover ? DefaultBgHover : DefaultBgRest;
                    border = DefaultBorder;
                    textCol = TextOnLightBtn;
                    break;
            }

            if (isPressed)
                bg = Color.Lerp(bg, Color.Black, 0.12f);

            // 5. 底板
            Rectangle dynamicBox = new Rectangle(drawX + pressOffset, drawY + pressOffset, drawW, drawH);
            b.Draw(
                Game1.staminaRect,
                new Rectangle(dynamicBox.X + 1, dynamicBox.Y + 1, dynamicBox.Width - 2, dynamicBox.Height - 2),
                bg);

            // 6. 边框
            if (isEnabled && hoverGlow && style != ActionButtonStyle.Disabled && isHover)
                border = Color.Lerp(border, GlowColor, GlowMix);

            IClickableMenu.drawTextureBox(
                b, Game1.mouseCursors, new Rectangle(432, 439, 9, 9),
                dynamicBox.X, dynamicBox.Y, dynamicBox.Width, dynamicBox.Height,
                border, 3f, false);

            // 7. 文本（恒 Bold）
            Rectangle stableTextBounds = new Rectangle(rect.X + pressOffset, rect.Y + pressOffset, rect.Width, rect.Height);
            (string fitLabel, float fitScale) = BioLabelFitter.FitBold(label, rect.Width - 8, fontSize);
            Vector2 sz = CustomFontManager.MeasureStringBold(fitLabel, fontSize, fitScale);
            Vector2 pos = new Vector2(
                stableTextBounds.X + (stableTextBounds.Width - sz.X) / 2f,
                stableTextBounds.Y + (stableTextBounds.Height - sz.Y) / 2f - 1f);
            CustomFontManager.DrawStringBold(b, fitLabel, pos, textCol, fontSize, fitScale);
        }

        /// <summary>
        /// 鼠标左键按下状态。两次 BOUNDARY 探针后一次性降级为 false。
        /// </summary>
        private static bool IsMouseDown()
        {
            try
            {
                return Mouse.GetState().LeftButton == ButtonState.Pressed;
            }
            catch (Exception ex)
            {
                LogProbe("Mouse.GetState", ex);
            }

            try
            {
                return Game1.input.GetMouseState().LeftButton == ButtonState.Pressed;
            }
            catch (Exception ex)
            {
                LogProbe("Game1.input.GetMouseState", ex);
            }

            return false;
        }

        private static void LogProbe(string probe, Exception ex)
        {
            if (_mouseProbeLogged)
                return;

            _mouseProbeLogged = true;
            ModEntry.SMonitor?.Log($"[ActionButtonRenderer] 鼠标状态探针 {probe} 失败，已降级为未按下: {ex.Message}");
        }
    }
}
