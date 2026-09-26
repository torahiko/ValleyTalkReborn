#nullable enable

using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewValley;
using StardewValley.Menus;
using ValleytalkReborn.UI;

namespace ValleytalkReborn
{
    /// <summary>
    /// "引导式起号"合规门禁：社区公约知悉同意屏。
    /// 遵循 BioEditorMenu 设计规范 —— 仅主标题与动作按钮使用 Bold，正文与卡片标题采用 Medium 字体；
    /// 勾选"已知悉并同意"前同意按钮置灰且点击无效；同意后写 Config 并回调进入下一路由。
    /// </summary>
    internal sealed class BioWizardDisclaimerDialog : IClickableMenu
    {
        // ── 尺寸与布局常量 ──
        private const int MenuWidth = 780;
        private const int MenuHeight = 530;
        private const int ContentPadding = 24;
        private const int HeaderH = 68;
        private const int FooterH = 56;

        // 严格遵循 CustomFontManager 整数字号规范
        private const float TitleFontSize = CustomFontManager.SizeTitle;       // 24f Bold (顶栏大标题)
        private const float ButtonFontSize = CustomFontManager.SizeRegular;    // 18f Bold (底部动作按钮)
        private const float SectionHeaderSize = CustomFontManager.SizeRegular; // 18f Medium (卡片小标)
        private const float BodyFontSize = CustomFontManager.SizeSmall;        // 15f Medium (正文说明)
        private const float TipFontSize = CustomFontManager.SizeSmall;         // 15f Medium (提示与悬停气泡)

        private const int CardPadding = 12;
        private const int CardGap = 10;
        private const int CardHeight = 78;

        private readonly new IClickableMenu _parentMenu;
        private readonly Action _onAccepted;
        private readonly SimpleCheckbox _checkbox;

        // 右上角关闭按钮与动效
        private ClickableTextureComponent _closeButton;
        private float _closeButtonHoverScale;
        private const float CloseButtonBaseScale = 3f;

        private readonly Rectangle[] _cardRects = new Rectangle[3];
        private Rectangle _cancelRect;
        private Rectangle _acceptRect;

        private string? _hoverText;

        private static (string Header, string Body)[] Covenants()
        {
            return new[]
            {
                (I18n.Get("Bio.DisclaimerC1H"), I18n.Get("Bio.DisclaimerC1B")),
                (I18n.Get("Bio.DisclaimerC2H"), I18n.Get("Bio.DisclaimerC2B")),
                (I18n.Get("Bio.DisclaimerC3H"), I18n.Get("Bio.DisclaimerC3B")),
            };
        }

        public BioWizardDisclaimerDialog(IClickableMenu parentMenu, Action onAccepted)
            : base(
                (Game1.uiViewport.Width - Math.Clamp(Game1.uiViewport.Width - 100, 720, MenuWidth)) / 2,
                (Game1.uiViewport.Height - Math.Clamp(Game1.uiViewport.Height - 80, 480, MenuHeight)) / 2,
                Math.Clamp(Game1.uiViewport.Width - 100, 720, MenuWidth),
                Math.Clamp(Game1.uiViewport.Height - 80, 480, MenuHeight),
                showUpperRightCloseButton: false)
        {
            _parentMenu = parentMenu;
            _onAccepted = onAccepted;

            _checkbox = new SimpleCheckbox(I18n.Get("Bio.DisclaimerCheckbox"), -1, 0, 0);

            _closeButton = new ClickableTextureComponent(
                new Rectangle(xPositionOnScreen + width - 50, yPositionOnScreen + 16, 36, 36),
                Game1.mouseCursors, new Rectangle(337, 494, 12, 12), CloseButtonBaseScale);

            Layout();
        }

        private void Layout()
        {
            _closeButton.bounds = new Rectangle(xPositionOnScreen + width - 50, yPositionOnScreen + 16, 36, 36);

            int contentLeft = xPositionOnScreen + ContentPadding;
            int contentW = width - ContentPadding * 2;
            int cardStartY = yPositionOnScreen + HeaderH + 12;

            for (int i = 0; i < 3; i++)
            {
                _cardRects[i] = new Rectangle(contentLeft, cardStartY + i * (CardHeight + CardGap), contentW, CardHeight);
            }

            int checkboxY = _cardRects[2].Bottom + 14;
            _checkbox.bounds = new Rectangle(contentLeft + 4, checkboxY, 28, 28);

            int footerY = yPositionOnScreen + height - FooterH + 10;
            const int btnH = 38;
            const int acceptBtnW = 180;
            const int cancelBtnW = 130;

            _acceptRect = new Rectangle(xPositionOnScreen + width - ContentPadding - acceptBtnW, footerY, acceptBtnW, btnH);
            _cancelRect = new Rectangle(_acceptRect.X - cancelBtnW - 12, footerY, cancelBtnW, btnH);
        }

        public override void update(GameTime time)
        {
            base.update(time);
            _hoverText = null;
        }

        public override void gameWindowSizeChanged(Rectangle oldBounds, Rectangle newBounds)
        {
            width = Math.Clamp(Game1.uiViewport.Width - 100, 720, MenuWidth);
            height = Math.Clamp(Game1.uiViewport.Height - 80, 480, MenuHeight);
            xPositionOnScreen = (Game1.uiViewport.Width - width) / 2;
            yPositionOnScreen = (Game1.uiViewport.Height - height) / 2;
            Layout();
        }

        public override void receiveLeftClick(int x, int y, bool playSound = true)
        {
            if (_closeButton.containsPoint(x, y))
            {
                Game1.playSound("bigDeSelect");
                Game1.activeClickableMenu = _parentMenu;
                return;
            }

            if (_checkbox.bounds.Contains(x, y) ||
                new Rectangle(_checkbox.bounds.X, _checkbox.bounds.Y, width - ContentPadding * 2, _checkbox.bounds.Height).Contains(x, y))
            {
                _checkbox.receiveLeftClick(x, y);
                return;
            }

            if (_cancelRect.Contains(x, y))
            {
                Game1.playSound("bigDeSelect");
                Game1.activeClickableMenu = _parentMenu;
                return;
            }

            if (_acceptRect.Contains(x, y))
            {
                if (!_checkbox.isChecked)
                {
                    Game1.playSound("cancel");
                    Game1.addHUDMessage(new HUDMessage(I18n.Get("Bio.DisclaimerMustCheck"), HUDMessage.error_type));
                    return;
                }

                try
                {
                    // ★ 提取为局部变量收窄为非空 ModConfig，消除 CS8634
                    var config = ModEntry.Config;
                    if (config != null)
                    {
                        config.HasAcceptedBioWizardNotice = true;
                        ModEntry.SHelper?.WriteConfig(config);
                    }
                }
                catch (Exception ex)
                {
                    ModEntry.SMonitor?.Log($"[BioWizard] 合规门禁 Config 写入失败：{ex.Message}", StardewModdingAPI.LogLevel.Warn);
                    Game1.addHUDMessage(new HUDMessage(I18n.Get("Bio.DisclaimerConfigWarn"), HUDMessage.error_type));
                }

                Game1.playSound("coin");
                _onAccepted?.Invoke();
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

            // 2. 双层羊皮纸木框底板（统一采用 BioEditorMenu 规范）
            IClickableMenu.drawTextureBox(b, xPositionOnScreen - 8, yPositionOnScreen - 8, width + 16, height + 16, Color.White);

            b.Draw(Game1.staminaRect, new Rectangle(xPositionOnScreen, yPositionOnScreen, width, height), new Color(245, 230, 205));
            b.Draw(
                Game1.menuTexture,
                new Rectangle(xPositionOnScreen, yPositionOnScreen, width, height),
                new Rectangle(64, 128, 64, 64),
                new Color(245, 230, 205));

            DrawHeader(b, mx, my);

            // 3. 绘制三条公约卡片
            for (int i = 0; i < 3; i++)
            {
                var (hdr, body) = Covenants()[i];
                DrawCovenantCard(b, _cardRects[i], hdr, body, mx, my);
            }

            // 4. 复选框
            _checkbox.draw(b, 0, 0, this);

            // 5. 底部动作按钮
            BioEditorMenu.DrawActionButton(b, _cancelRect, I18n.Get("Bio.CancelEsc"), mx, my, ButtonFontSize,
                isDanger: false, isPrimary: false, isLeftMouseDownFunc: () => IsLeftMouseDown());
            BioEditorMenu.DrawActionButton(b, _acceptRect, I18n.Get("Bio.AcceptButton"), mx, my, ButtonFontSize,
                isDanger: false, isPrimary: true, isEnabled: _checkbox.isChecked, isLeftMouseDownFunc: () => IsLeftMouseDown());

            if (_cancelRect.Contains(mx, my))
                _hoverText = I18n.Get("Bio.DisclaimerCancelHover");
            else if (_acceptRect.Contains(mx, my))
            {
                _hoverText = _checkbox.isChecked
                    ? I18n.Get("Bio.DisclaimerAcceptHover")
                    : I18n.Get("Bio.DisclaimerBlockedHover");
            }

            // 6. 悬停说明气泡与鼠标光标
            if (!string.IsNullOrEmpty(_hoverText))
                BioEditorMenu.DrawButtonTooltip(b, _hoverText, TipFontSize);

            drawMouse(b);
        }

        private void DrawHeader(SpriteBatch b, int mx, int my)
        {
            int headX = xPositionOnScreen + ContentPadding;
            int headY = yPositionOnScreen + 16;

            // ★ 主标题：唯一使用 Bold 的顶栏文字，SizeTitle (24f)
            string title = I18n.Get("Bio.DisclaimerTitle");
            CustomFontManager.DrawStringBold(b, title, new Vector2(headX, headY), BioEditorMenu.TextPrimary, TitleFontSize);

            // ★ 副标题：使用 Medium 字体，SizeSmall (15f)
            string subtitle = I18n.Get("Bio.DisclaimerSubtitle");
            CustomFontManager.DrawString(b, subtitle, new Vector2(headX + 2, headY + 28), BioEditorMenu.TextMuted, TipFontSize);

            // 分割横线
            int sepY = yPositionOnScreen + HeaderH + 2;
            b.Draw(Game1.staminaRect, new Rectangle(headX, sepY, width - ContentPadding * 2, 2), Color.Gray * 0.35f);

            // 右上角关闭按钮悬停平滑动效
            UiHelper.UpdateButtonScale(ref _closeButtonHoverScale, _closeButton, mx, my);
            _closeButton.scale = CloseButtonBaseScale * _closeButtonHoverScale;
            _closeButton.draw(b);
        }

        private static void DrawCovenantCard(SpriteBatch b, Rectangle rect, string header, string body, int mx, int my)
        {
            bool isHover = rect.Contains(mx, my);

            // 1. 底层微阴影
            b.Draw(Game1.staminaRect, new Rectangle(rect.X + 2, rect.Y + 2, rect.Width, rect.Height), Color.Black * 0.08f);

            // 2. 卡片内衬底色
            Color bg = isHover ? new Color(255, 252, 245) : new Color(246, 238, 224);
            b.Draw(Game1.staminaRect, new Rectangle(rect.X + 1, rect.Y + 1, rect.Width - 2, rect.Height - 2), bg);

            // 3. 卡片 2f 九宫格木质边框
            Color borderCol = isHover ? new Color(210, 160, 60) : new Color(225, 212, 190);
            IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(432, 439, 9, 9),
                rect.X, rect.Y, rect.Width, rect.Height, borderCol, 2f, false);

            // 4. 左侧金色装饰指示条
            b.Draw(Game1.staminaRect, new Rectangle(rect.X + 2, rect.Y + 4, 4, rect.Height - 8),
                isHover ? new Color(210, 150, 40) : new Color(195, 155, 115));

            // 5. ★ 卡片标题：Medium 字体（DrawString）+ SizeRegular (18f)，自然素雅
            CustomFontManager.DrawString(
                b, header,
                new Vector2(rect.X + CardPadding + 6, rect.Y + CardPadding - 2),
                isHover ? BioEditorMenu.TextPrimary : BioEditorMenu.TextSecondary,
                SectionHeaderSize);

            // 6. ★ 正文：逐行折行渲染，Medium 字体 + SizeSmall (15f)
            float bodyY = rect.Y + CardPadding + SectionHeaderSize + 2;
            float maxBodyW = rect.Width - (CardPadding * 2) - 10;
            DrawWrappedBody(b, body, rect.X + CardPadding + 6, bodyY, maxBodyW, BioEditorMenu.TextPrimary * 0.88f, BodyFontSize);
        }

        /// <summary>
        /// 针对中西文混排的精确逐字符测量折行渲染，杜绝原版 split(' ') 导致中文无法换行的问题。
        /// </summary>
        private static void DrawWrappedBody(SpriteBatch b, string text, float x, float y, float maxWidth, Color color, float fontSize)
        {
            if (string.IsNullOrEmpty(text))
                return;

            float lineHeight = CustomFontManager.MeasureString("Ag", fontSize).Y + 2f;
            float curX = x;
            float curY = y;
            var sb = new StringBuilder();

            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (c == '\n')
                {
                    if (sb.Length > 0)
                    {
                        CustomFontManager.DrawString(b, sb.ToString(), new Vector2(curX, curY), color, fontSize);
                        sb.Clear();
                    }
                    curX = x;
                    curY += lineHeight;
                    continue;
                }

                string candidate = sb.ToString() + c;
                float measuredW = CustomFontManager.MeasureString(candidate, fontSize).X;

                if (measuredW > maxWidth && sb.Length > 0)
                {
                    CustomFontManager.DrawString(b, sb.ToString(), new Vector2(curX, curY), color, fontSize);
                    sb.Clear();
                    curX = x;
                    curY += lineHeight;
                }

                sb.Append(c);
            }

            if (sb.Length > 0)
            {
                CustomFontManager.DrawString(b, sb.ToString(), new Vector2(curX, curY), color, fontSize);
            }
        }

        private static bool IsLeftMouseDown()
        {
            try
            {
                return Game1.input.GetMouseState().LeftButton == ButtonState.Pressed;
            }
            catch
            {
                return false;
            }
        }
    }
}