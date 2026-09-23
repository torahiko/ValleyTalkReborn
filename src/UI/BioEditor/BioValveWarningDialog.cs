#nullable enable

using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewValley;
using StardewValley.Menus;
using ValleytalkReborn.UI;

namespace ValleytalkReborn
{
    /// <summary>
    /// 统一警示/确认弹窗（AI 阀门门禁、删除/重置确认、向导推进提示）：
    /// 遵循 BioEditorMenu 整体设计规范 —— 双层无缝羊皮纸底板、立体按压下沉动效、
    /// 风险继续按钮标红（底色红、边框深红、文字纯净反白，字体不标红）、平滑悬停缩放。
    /// continueIsDanger=false 时右侧继续按钮呈现金黄主操作样式（用于向导推进等正面操作）。
    /// </summary>
    internal sealed class BioValveWarningDialog : IClickableMenu
    {
        private const int MenuWidth = 640;
        private const int MenuHeight = 380;
        private const int ContentPadding = 24;
        private const int HeaderH = 58;
        private const int FooterH = 54;

        private const float TitleFontSize = CustomFontManager.SizeTitle;       // 24f Bold
        private const float ButtonFontSize = CustomFontManager.SizeRegular;    // 18f Bold
        private const float SectionHeaderSize = CustomFontManager.SizeRegular; // 18f Medium
        private const float TipFontSize = CustomFontManager.SizeSmall;         // 15f Medium

        private readonly new IClickableMenu _parentMenu;
        private readonly string _title;
        private readonly string _subtitle;
        private readonly List<string> _warnings;
        private readonly string _continueText;
        private readonly Action _onContinue;
        private readonly string _fixText;
        private readonly Action _onFix;
        private readonly string? _tip;
        private readonly bool _continueIsDanger;

        private ClickableTextureComponent _closeButton;
        private float _closeButtonHoverScale;
        private const float CloseButtonBaseScale = 3f;

        private Rectangle _cardRect;
        private Rectangle _fixBtnRect;
        private Rectangle _continueBtnRect;

        public BioValveWarningDialog(
            IClickableMenu parentMenu,
            string title,
            string subtitle,
            List<string> warnings,
            string continueText,
            Action onContinue,
            string fixText,
            Action onFix,
            string? tip = null,
            bool continueIsDanger = true)
            : base(
                (Game1.uiViewport.Width - MenuWidth) / 2,
                (Game1.uiViewport.Height - MenuHeight) / 2,
                MenuWidth,
                MenuHeight,
                showUpperRightCloseButton: false)
        {
            _parentMenu = parentMenu;
            _title = title;
            _subtitle = subtitle;
            _warnings = warnings;
            _continueText = continueText;
            _onContinue = onContinue;
            _fixText = fixText;
            _onFix = onFix;
            _tip = tip;
            _continueIsDanger = continueIsDanger;

            _closeButton = new ClickableTextureComponent(
                new Rectangle(xPositionOnScreen + width - 48, yPositionOnScreen + 14, 36, 36),
                Game1.mouseCursors, new Rectangle(337, 494, 12, 12), CloseButtonBaseScale);

            Layout();
        }

        private void Layout()
        {
            _closeButton.bounds = new Rectangle(xPositionOnScreen + width - 48, yPositionOnScreen + 14, 36, 36);

            int contentLeft = xPositionOnScreen + ContentPadding;
            int contentW = width - ContentPadding * 2;
            int cardTop = yPositionOnScreen + HeaderH + 8;

            int footerY = yPositionOnScreen + height - FooterH + 10;
            int cardH = footerY - 14 - cardTop;

            _cardRect = new Rectangle(contentLeft, cardTop, contentW, cardH);

            const int btnH = 38;
            const int fixBtnW = 190;
            const int continueBtnW = 190;

            _fixBtnRect = new Rectangle(contentLeft, footerY, fixBtnW, btnH);
            _continueBtnRect = new Rectangle(xPositionOnScreen + width - ContentPadding - continueBtnW, footerY, continueBtnW, btnH);
        }

        public override void receiveLeftClick(int x, int y, bool playSound = true)
        {
            if (_closeButton.containsPoint(x, y))
            {
                Game1.playSound("bigDeSelect");
                Game1.activeClickableMenu = _parentMenu;
                return;
            }

            if (_fixBtnRect.Contains(x, y))
            {
                Game1.playSound("smallSelect");
                _onFix?.Invoke();
                return;
            }

            if (_continueBtnRect.Contains(x, y))
            {
                Game1.playSound("coin");
                _onContinue?.Invoke();
                return;
            }
        }

        public override void receiveKeyPress(Keys key)
        {
            if (key == Keys.Escape)
            {
                Game1.playSound("bigDeSelect");
                Game1.activeClickableMenu = _parentMenu;
                return;
            }

            if (Game1.options.doesInputListContain(Game1.options.menuButton, key))
                return;

            base.receiveKeyPress(key);
        }

        public override void draw(SpriteBatch b)
        {
            int mx = Game1.getMouseX();
            int my = Game1.getMouseY();

            // 1. 全屏半透明遮罩
            b.Draw(Game1.fadeToBlackRect, Game1.graphics.GraphicsDevice.Viewport.Bounds, Color.Black * 0.5f);

            // 2. 双层羊皮纸木框底板
            IClickableMenu.drawTextureBox(b, xPositionOnScreen - 8, yPositionOnScreen - 8, width + 16, height + 16, Color.White);
            b.Draw(Game1.staminaRect, new Rectangle(xPositionOnScreen, yPositionOnScreen, width, height), new Color(245, 230, 205));
            b.Draw(Game1.menuTexture, new Rectangle(xPositionOnScreen, yPositionOnScreen, width, height),
                new Rectangle(64, 128, 64, 64), new Color(245, 230, 205));

            // 顶栏
            int headX = xPositionOnScreen + ContentPadding;
            int headY = yPositionOnScreen + 14;

            CustomFontManager.DrawStringBold(b, _title, new Vector2(headX, headY), BioEditorMenu.TextPrimary, TitleFontSize);
            CustomFontManager.DrawString(b, _subtitle, new Vector2(headX, headY + 28), BioEditorMenu.TextSecondary, TipFontSize);

            int sepY = yPositionOnScreen + HeaderH + 2;
            b.Draw(Game1.staminaRect, new Rectangle(headX, sepY, width - ContentPadding * 2, 2), Color.Gray * 0.35f);

            UiHelper.UpdateButtonScale(ref _closeButtonHoverScale, _closeButton, mx, my);
            _closeButton.scale = CloseButtonBaseScale * _closeButtonHoverScale;
            _closeButton.draw(b);

            // 3. 警示卡片区域（暖浅底衬 + 432金框 + 微阴影）
            b.Draw(Game1.staminaRect, new Rectangle(_cardRect.X + 2, _cardRect.Y + 2, _cardRect.Width, _cardRect.Height), Color.Black * 0.08f);
            b.Draw(Game1.staminaRect, new Rectangle(_cardRect.X + 1, _cardRect.Y + 1, _cardRect.Width - 2, _cardRect.Height - 2), new Color(254, 248, 238));
            IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(432, 439, 9, 9),
                _cardRect.X, _cardRect.Y, _cardRect.Width, _cardRect.Height,
                new Color(223, 122, 4) * 0.7f, 2f, false);

            // 逐行绘制警告条目
            int textY = _cardRect.Y + 14;
            const int lineH = 26;
            for (int i = 0; i < _warnings.Count; i++)
            {
                CustomFontManager.DrawString(b, "•", new Vector2(_cardRect.X + 16, textY), BioEditorMenu.TextWarning, SectionHeaderSize);
                CustomFontManager.DrawString(b, _warnings[i], new Vector2(_cardRect.X + 32, textY + 1), BioEditorMenu.TextPrimary, TipFontSize);
                textY += lineH;
            }

            // 底部提示（可选，传入空则隐藏）
            if (!string.IsNullOrEmpty(_tip))
                CustomFontManager.DrawString(b, _tip, new Vector2(_cardRect.X + 16, _cardRect.Bottom - 26), BioEditorMenu.TextMuted, TipFontSize);

            // 4. 底部动作按钮
            // 完善按钮（常规浅木按钮）
            DrawActionButton(b, _fixBtnRect, _fixText, mx, my, isDanger: false, isPrimary: false);
            // ★ 继续按钮：风险场景标红背景（文字采用 TextOnDarkBtn 纯净白，绝不标红）；正面推进场景呈金黄主操作样式
            DrawActionButton(b, _continueBtnRect, _continueText, mx, my, isDanger: _continueIsDanger, isPrimary: !_continueIsDanger);

            drawMouse(b);
        }

        private static bool IsLeftMouseDown()
        {
            try { return Game1.input.GetMouseState().LeftButton == ButtonState.Pressed; }
            catch { return false; }
        }

        private static void DrawActionButton(SpriteBatch b, Rectangle rect, string label, int mx, int my,
            bool isDanger = false, bool isPrimary = false, bool isEnabled = true)
        {
            bool isHover = isEnabled && rect.Contains(mx, my);
            bool isPressed = isHover && IsLeftMouseDown();

            Color bg;
            if (!isEnabled) bg = Color.LightGray * 0.6f;
            else if (isPrimary) bg = isHover ? Color.Gold : new Color(255, 220, 130);
            else if (isDanger) bg = isHover ? new Color(245, 95, 95) : new Color(215, 75, 70); // ★ 标红背景
            else bg = isHover ? new Color(255, 240, 215) : new Color(225, 195, 155);

            int pressOffset = isPressed ? 1 : 0;
            if (isPressed) bg = Color.Lerp(bg, Color.Black, 0.14f);

            if (!isPressed)
                b.Draw(Game1.staminaRect, new Rectangle(rect.X + 2, rect.Y + 2, rect.Width, rect.Height), Color.Black * 0.15f);

            b.Draw(Game1.staminaRect, new Rectangle(rect.X + 1 + pressOffset, rect.Y + 1 + pressOffset, rect.Width - 2, rect.Height - 2), bg);

            IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(432, 439, 9, 9),
                rect.X + pressOffset, rect.Y + pressOffset, rect.Width, rect.Height,
                isPrimary ? new Color(210, 160, 60) : (isDanger ? new Color(175, 60, 55) : new Color(185, 150, 110)), 3f, false);

            // ★ 字体颜色：标红按钮使用 TextOnDarkBtn（纯白/象牙反白），严禁标红
            Color textCol = !isEnabled ? BioEditorMenu.TextMuted
                          : isDanger ? BioEditorMenu.TextOnDarkBtn
                          : BioEditorMenu.TextOnLightBtn;

            var sz = CustomFontManager.MeasureStringBold(label, ButtonFontSize);
            CustomFontManager.DrawStringBold(b, label,
                new Vector2(
                    rect.X + pressOffset + (rect.Width - sz.X) / 2f,
                    rect.Y + pressOffset + (rect.Height - sz.Y) / 2f),
                textCol, ButtonFontSize);
        }
    }
}
