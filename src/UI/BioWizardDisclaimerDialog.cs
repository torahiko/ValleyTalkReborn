using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewValley;
using StardewValley.Menus;
using System;

namespace ValleytalkReborn
{
    /// <summary>
    /// "引导式起号"合规门禁：社区公约知悉同意屏。
    /// 勾选"已知悉并同意"前同意按钮灰显且点击无效；同意后写 Config 并回调进入下一路由。
    /// 无向导旗标持有、无文本框、无分发器订阅——无 CloseToParent 时序问题。
    /// </summary>
    internal sealed class BioWizardDisclaimerDialog : IClickableMenu
    {
        private const int MenuWidth = 740;
        private const int MenuHeight = 460;

        private const float TitleSize = 22f;
        private const float CardHeaderSize = 17f;
        private const float CardBodySize = 16f;

        private const int CardPadding = 14;
        private const int CardGap = 10;

        private const int ButtonHeight = 44;
        private const int ButtonWidth = 150;

        private readonly new IClickableMenu _parentMenu;
        private readonly Action _onAccepted;
        private readonly SimpleCheckbox _checkbox;

        private readonly Rectangle _cancelRect;
        private readonly Rectangle _acceptRect;

        private readonly string _titleText = "关于 AI 创作与社区公约";

#pragma warning disable CS0649 // 悬停气泡文本：draw 中条件性赋值，空值即不绘制
        private string _hoverText;
#pragma warning restore CS0649

        public BioWizardDisclaimerDialog(IClickableMenu parentMenu, Action onAccepted)
            : base(
                (Game1.uiViewport.Width - MenuWidth) / 2,
                (Game1.uiViewport.Height - MenuHeight) / 2,
                MenuWidth,
                MenuHeight)
        {
            _parentMenu = parentMenu;
            _onAccepted = onAccepted;

            int checkboxY = yPositionOnScreen + MenuHeight - ButtonHeight - 90;
            _checkbox = new SimpleCheckbox("我已知悉并同意上述公约（勾选后不再提示）", -1,
                xPositionOnScreen + 36, checkboxY);

            int btnY = yPositionOnScreen + MenuHeight - ButtonHeight - 28;
            _acceptRect = new Rectangle(xPositionOnScreen + MenuWidth - ButtonWidth - 28, btnY, ButtonWidth, ButtonHeight);
            _cancelRect = new Rectangle(_acceptRect.X - ButtonWidth - 16, btnY, ButtonWidth, ButtonHeight);
        }

        public override void receiveLeftClick(int x, int y, bool playSound = true)
        {
            _checkbox.receiveLeftClick(x, y);

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
                    return;
                }

                try
                {
                    if (ModEntry.Config != null)
                        ModEntry.Config.HasAcceptedBioWizardNotice = true;
                    ModEntry.SHelper?.WriteConfig(ModEntry.Config);
                }
                catch (Exception ex)
                {
                    ModEntry.SMonitor?.Log($"[BioWizard] 合规门禁 Config 写入失败：{ex.Message}", StardewModdingAPI.LogLevel.Warn);
                    Game1.addHUDMessage(new HUDMessage("配置写入失败（本次仍可继续使用，下次启动会再次提示）", HUDMessage.error_type));
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
            }
        }

        public override void draw(SpriteBatch b)
        {
            b.Draw(Game1.fadeToBlackRect, Game1.graphics.GraphicsDevice.Viewport.Bounds, Color.Black * 0.45f);

            Game1.drawDialogueBox(xPositionOnScreen, yPositionOnScreen, width, height, false, true);

            int mx = Game1.getMouseX();
            int my = Game1.getMouseY();

            // 标题（纯文本，无 emoji）
            CustomFontManager.DrawStringBold(
                b, _titleText,
                new Vector2(xPositionOnScreen + 28, yPositionOnScreen + 24),
                BioEditorMenu.TextPrimary, TitleSize);

            // 三条公约卡片
            int cardX = xPositionOnScreen + 28;
            int cardWidth = MenuWidth - 56;
            int cardY = yPositionOnScreen + 70;
            int cardHeight = 88;

            DrawCovenantCard(b, cardX, cardY, cardWidth, cardHeight,
                "本地沙盒用途",
                "本功能仅在您本地运行、调用您自行配置的大模型服务商；不会上传任何角色数据到第三方服务器。您对自己的使用行为负全部责任。");
            DrawCovenantCard(b, cardX, cardY + (cardHeight + CardGap), cardWidth, cardHeight,
                "版权归属 ConcernedApe 与原作者",
                "《星露谷物语》所有原创内容（角色、对白、美术、音乐等）归 ConcernedApe 及对应原作者所有；AI 生成内容亦须遵守此版权框架，不得主张对原作品享有排他权利。");
            DrawCovenantCard(b, cardX, cardY + (cardHeight + CardGap) * 2, cardWidth, cardHeight,
                "社区分享须标注 AI-Assisted 且禁止商用",
                "向社区分享 AI 生成的角色档案时，须在显著位置标注「AI-Assisted」；任何基于本功能产出的内容不得用于直接或间接商业牟利。");

            // 复选框
            _checkbox.draw(b, 0, 0, this);

            // 取消 / 同意按钮
            DrawButton(b, _cancelRect, "✕ 取消", mx, my, isDanger: false, isPrimary: false);
            DrawButton(b, _acceptRect, "✔ 同意并继续", mx, my, isDanger: false, isPrimary: true, isEnabled: _checkbox.isChecked);

            if (!string.IsNullOrEmpty(_hoverText))
                DrawHover(b, _hoverText);

            drawMouse(b);
        }

        private void DrawCovenantCard(SpriteBatch b, int x, int y, int w, int h, string header, string body)
        {
            // 浅色底衬
            b.Draw(Game1.staminaRect, new Rectangle(x + 4, y + 4, w, h), Color.Black * 0.08f);
            b.Draw(Game1.staminaRect, new Rectangle(x, y, w, h), new Color(252, 246, 232));

            // 1px 像素边框
            IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(240, 240, 4, 4),
                x, y, w, h, new Color(205, 185, 150), 2f, false);

            // 加粗小标
            CustomFontManager.DrawStringBold(
                b, header,
                new Vector2(x + CardPadding, y + CardPadding - 2),
                BioEditorMenu.TextPrimary, CardHeaderSize);

            // 正文（自动折行）
            float bodyY = y + CardPadding + CardHeaderSize + 6;
            float bodyWidth = w - CardPadding * 2;
            DrawWrappedBody(b, body, x + CardPadding, bodyY, bodyWidth, BioEditorMenu.TextSecondary, CardBodySize);
        }

        private static void DrawWrappedBody(SpriteBatch b, string text, float x, float y, float maxWidth, Color color, float fontSize)
        {
            // 简易逐行折行绘制
            var words = text.Split(' ');
            float cx = x;
            float cy = y;
            float lineHeight = CustomFontManager.MeasureString("测试", fontSize).Y + 2f;
            float spaceWidth = CustomFontManager.MeasureString(" ", fontSize).X;

            foreach (var word in words)
            {
                var wordSize = CustomFontManager.MeasureString(word, fontSize);
                if (cx > x && cx + spaceWidth + wordSize.X > x + maxWidth)
                {
                    cx = x;
                    cy += lineHeight;
                }
                if (cx > x)
                {
                    cx += spaceWidth;
                }
                CustomFontManager.DrawString(b, word, new Vector2(cx, cy), color, fontSize);
                cx += wordSize.X;
            }
        }

        private void DrawButton(SpriteBatch b, Rectangle rect, string label, int mx, int my,
            bool isDanger = false, bool isPrimary = false, bool isEnabled = true)
        {
            bool isHover = isEnabled && rect.Contains(mx, my);
            bool isPressed = isHover && IsLeftMouseDown();

            Color bg;
            if (!isEnabled) bg = Color.LightGray * 0.6f;
            else if (isPrimary) bg = isHover ? Color.Gold : new Color(255, 220, 130);
            else if (isDanger) bg = isHover ? new Color(245, 105, 105) : new Color(210, 85, 80);
            else bg = isHover ? new Color(255, 240, 215) : new Color(225, 195, 155);

            int pressOffset = isPressed ? 1 : 0;
            if (isPressed) bg = Color.Lerp(bg, Color.Black, 0.14f);

            if (!isPressed)
                b.Draw(Game1.staminaRect, new Rectangle(rect.X + 2, rect.Y + 2, rect.Width, rect.Height), Color.Black * 0.15f);

            b.Draw(Game1.staminaRect, new Rectangle(rect.X + 1 + pressOffset, rect.Y + 1 + pressOffset, rect.Width - 2, rect.Height - 2), bg);

            IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(432, 439, 9, 9),
                rect.X + pressOffset, rect.Y + pressOffset, rect.Width, rect.Height,
                isPrimary ? new Color(210, 160, 60) : (isDanger ? new Color(175, 60, 55) : new Color(185, 150, 110)), 3f, false);

            Color textCol;
            if (!isEnabled) textCol = BioEditorMenu.TextMuted;
            else if (isDanger) textCol = BioEditorMenu.TextOnDarkBtn;
            else textCol = BioEditorMenu.TextOnLightBtn;

            var sz = CustomFontManager.MeasureStringBold(label, BioEditorManager_ButtonFontSize);
            CustomFontManager.DrawStringBold(b, label,
                new Vector2(
                    rect.X + pressOffset + (rect.Width - sz.X) / 2f,
                    rect.Y + pressOffset + (rect.Height - sz.Y) / 2f),
                textCol, BioEditorManager_ButtonFontSize);
        }

        // 共享字号常量（对齐 BioEditorMenu.ButtonFontSize）
        private const float BioEditorManager_ButtonFontSize = 18f;

        private static bool IsLeftMouseDown()
        {
            return Game1.input.GetMouseState().LeftButton == ButtonState.Pressed;
        }

        private static void DrawHover(SpriteBatch b, string text)
        {
            var sz = CustomFontManager.MeasureString(text, 15f);
            const int padX = 14;
            const int padY = 10;
            int bw = (int)MathF.Ceiling(sz.X) + padX * 2;
            int bh = (int)MathF.Ceiling(sz.Y) + padY * 2;
            int x = Game1.getOldMouseX() + 24;
            int y = Game1.getOldMouseY() + 24;
            var safe = Utility.getSafeArea();
            if (x + bw > safe.Right) x = safe.Right - bw;
            if (y + bh > safe.Bottom) y = safe.Bottom - bh;
            if (x < safe.Left) x = safe.Left;
            if (y < safe.Top) y = safe.Top;

            IClickableMenu.drawTextureBox(b, Game1.menuTexture, new Rectangle(0, 256, 60, 60), x, y, bw, bh, new Color(255, 255, 250), 0.65f, false);
            CustomFontManager.DrawString(b, text, new Vector2(x + padX, y + padY), BioEditorMenu.TextPrimary, 15f);
        }
    }
}
