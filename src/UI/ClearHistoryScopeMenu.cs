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
    /// 对话历史与记忆清除作用域选择弹窗：
    /// 废除覆盖按钮的狭窄下拉框，重构为风险层级卡片选择器；
    /// 包含肖像感知、立体按压动效、固定高清晰文字色与确认按钮警示态。
    /// </summary>
    internal class ClearHistoryScopeMenu : IClickableMenu
    {
        public enum ClearScope
        {
            Today,
            CurrentNpcAll,
            GlobalAll
        }

        // ── 尺寸与布局常量 ──
        private const int MenuWidth = 760;
        private const int MenuHeight = 460;
        private const int ContentPadding = 24;
        private const int HeaderH = 62;
        private const int FooterH = 56;

        private const float TitleFontSize = CustomFontManager.SizeTitle;       // 24f Bold (顶栏标题)
        private const float ButtonFontSize = CustomFontManager.SizeRegular;    // 18f Bold (底部主按钮)
        private const float CardHeaderSize = CustomFontManager.SizeRegular;    // 18f Bold (选项主标题)
        private const float TipFontSize = CustomFontManager.SizeSmall;         // 15f Medium (顶栏副提示)
        private const float DescFontSize = 15.5f;                              // 15.5f Medium (选项说明文字，清晰辨识)

        private readonly string _npcName;
        private readonly DialogueTextInputMenu _ownerMenu;
        private readonly Action<ClearScope> _onConfirm;

        // 肖像与关闭按钮
        private Texture2D? _npcPortrait;
        private Rectangle _portraitSmileRect;
        private ClickableTextureComponent _closeButton;
        private float _closeButtonHoverScale;
        private const float CloseButtonBaseScale = 3f;

        // 三段式卡片与选择状态
        private record ScopeCardOption(ClearScope Scope, string Title, string Description);
        private readonly List<ScopeCardOption> _scopeOptions = new();
        private readonly Rectangle[] _cardRects = new Rectangle[3];
        private ClearScope _selectedScope = ClearScope.Today;

        // 底部动作按钮
        private Rectangle _cancelButtonRect;
        private Rectangle _confirmButtonRect;
        private float _cancelHoverScale = 1f;
        private float _confirmHoverScale = 1f;

        public ClearHistoryScopeMenu(string npcName, DialogueTextInputMenu ownerMenu, Action<ClearScope> onConfirm)
            : base(
                (Game1.uiViewport.Width - Math.Min(MenuWidth, Game1.uiViewport.Width - 40)) / 2,
                (Game1.uiViewport.Height - Math.Min(MenuHeight, Game1.uiViewport.Height - 40)) / 2,
                Math.Min(MenuWidth, Game1.uiViewport.Width - 40),
                Math.Min(MenuHeight, Game1.uiViewport.Height - 40),
                showUpperRightCloseButton: false)
        {
            _npcName = npcName;
            _ownerMenu = ownerMenu;
            _onConfirm = onConfirm;

            string dispName = Game1.getCharacterFromName(_npcName)?.displayName ?? _npcName;

            // 构建带有说明的三段式选项
            _scopeOptions.Add(new ScopeCardOption(
                ClearScope.Today,
                I18n.DialogueInput.ClearScopeToday(dispName),
                I18n.DialogueInput.ClearScopeTodayDesc()));

            _scopeOptions.Add(new ScopeCardOption(
                ClearScope.CurrentNpcAll,
                I18n.DialogueInput.ClearScopeCurrentNpcAll(dispName),
                I18n.DialogueInput.ClearScopeCurrentNpcAllDesc(dispName)));

            _scopeOptions.Add(new ScopeCardOption(
                ClearScope.GlobalAll,
                I18n.DialogueInput.ClearScopeGlobalAll(),
                I18n.DialogueInput.ClearScopeGlobalAllDesc()));

            _closeButton = new ClickableTextureComponent(
                new Rectangle(xPositionOnScreen + width - 50, yPositionOnScreen + 16, 36, 36),
                Game1.mouseCursors, new Rectangle(337, 494, 12, 12), CloseButtonBaseScale);

            LoadNpcPortrait();
            Layout();
        }

        private void LoadNpcPortrait()
        {
            try
            {
                var character = Game1.getCharacterFromName(_npcName);
                _npcPortrait = (character?.Portrait != null && !character.Portrait.IsDisposed)
                    ? character.Portrait
                    : Game1.content.Load<Texture2D>("Portraits\\" + _npcName);

                if (_npcPortrait != null)
                {
                    if (_npcPortrait.Width >= 128 && _npcPortrait.Height >= 64)
                        _portraitSmileRect = new Rectangle(64, 0, 64, 64);
                    else if (_npcPortrait.Width >= 64 && _npcPortrait.Height >= 128)
                        _portraitSmileRect = new Rectangle(0, 64, 64, 64);
                    else
                        _portraitSmileRect = new Rectangle(0, 0, Math.Min(64, _npcPortrait.Width), Math.Min(64, _npcPortrait.Height));
                }
            }
            catch
            {
                _npcPortrait = null;
                _portraitSmileRect = Rectangle.Empty;
            }
        }

        private void Layout()
        {
            _closeButton.bounds = new Rectangle(xPositionOnScreen + width - 50, yPositionOnScreen + 16, 36, 36);

            int contentLeft = xPositionOnScreen + ContentPadding;
            int contentW = width - ContentPadding * 2;
            int startY = yPositionOnScreen + HeaderH + 16;

            const int cardH = 80;
            const int cardGap = 12;

            for (int i = 0; i < 3; i++)
            {
                _cardRects[i] = new Rectangle(contentLeft, startY + i * (cardH + cardGap), contentW, cardH);
            }

            int footerY = yPositionOnScreen + height - FooterH + 8;
            const int btnH = 40;
            const int cancelBtnW = 140;
            const int confirmBtnW = 200;

            _cancelButtonRect = new Rectangle(contentLeft, footerY, cancelBtnW, btnH);
            _confirmButtonRect = new Rectangle(xPositionOnScreen + width - ContentPadding - confirmBtnW, footerY, confirmBtnW, btnH);
        }

        public override void gameWindowSizeChanged(Rectangle oldBounds, Rectangle newBounds)
        {
            width = Math.Min(MenuWidth, Game1.uiViewport.Width - 40);
            height = Math.Min(MenuHeight, Game1.uiViewport.Height - 40);
            xPositionOnScreen = (Game1.uiViewport.Width - width) / 2;
            yPositionOnScreen = (Game1.uiViewport.Height - height) / 2;
            Layout();
        }

        public override void receiveLeftClick(int x, int y, bool playSound = true)
        {
            base.receiveLeftClick(x, y, playSound);

            if (_closeButton.containsPoint(x, y))
            {
                Cancel();
                return;
            }

            // 点击卡片切换选项
            for (int i = 0; i < _cardRects.Length; i++)
            {
                if (_cardRects[i].Contains(x, y))
                {
                    if (_selectedScope != _scopeOptions[i].Scope)
                    {
                        _selectedScope = _scopeOptions[i].Scope;
                        Game1.playSound("drumkit6");
                    }
                    return;
                }
            }

            if (_confirmButtonRect.Contains(x, y))
            {
                Confirm();
                return;
            }

            if (_cancelButtonRect.Contains(x, y))
            {
                Cancel();
            }
        }

        public override void receiveKeyPress(Keys key)
        {
            if (key == Keys.Escape)
            {
                Cancel();
                return;
            }

            if (Game1.options.doesInputListContain(Game1.options.menuButton, key))
                return;

            base.receiveKeyPress(key);
        }

        private void Confirm()
        {
            Game1.playSound("coin");
            exitThisMenu();
            try
            {
                _onConfirm?.Invoke(_selectedScope);
            }
            finally
            {
                _ownerMenu?.RestorePreviousMenu();
            }
        }

        private void Cancel()
        {
            Game1.playSound("bigDeSelect");
            exitThisMenu();
            _ownerMenu?.RestorePreviousMenu();
        }

        // ── 渲染管线 ──────────────────────────────────────────────────────────

        public override void draw(SpriteBatch b)
        {
            int mx = Game1.getMouseX();
            int my = Game1.getMouseY();

            // 1. 全屏半透明遮罩
            b.Draw(Game1.fadeToBlackRect, Game1.graphics.GraphicsDevice.Viewport.Bounds, Color.Black * 0.5f);

            // 2. 双层羊皮纸木框底板
            IClickableMenu.drawTextureBox(b, xPositionOnScreen - 8, yPositionOnScreen - 8, width + 16, height + 16, Color.White);

            b.Draw(Game1.staminaRect, new Rectangle(xPositionOnScreen, yPositionOnScreen, width, height), new Color(245, 230, 205));
            b.Draw(
                Game1.menuTexture,
                new Rectangle(xPositionOnScreen, yPositionOnScreen, width, height),
                new Rectangle(64, 128, 64, 64),
                new Color(245, 230, 205));

            DrawHeader(b, mx, my);

            // 3. 绘制三项范围选择卡片
            for (int i = 0; i < 3; i++)
            {
                DrawScopeCard(b, _cardRects[i], _scopeOptions[i], mx, my);
            }

            // 4. 底部动作按钮（仅在选中国际危险选项时，确认按钮变红警示）
            bool isGlobalDanger = _selectedScope == ClearScope.GlobalAll;
            string confirmLabel = isGlobalDanger
                ? I18n.DialogueInput.ClearScopeConfirmDanger()
                : I18n.DialogueInput.ClearScopeConfirm();

            ActionButtonRenderer.Draw(b, _cancelButtonRect, I18n.DialogueInput.ClearScopeCancel(), ref _cancelHoverScale, mx, my,
                style: ActionButtonStyle.Default, fontSize: ButtonFontSize, isEnabled: true,
                isPressedFunc: () => IsLeftMouseDown());
            ActionButtonRenderer.Draw(b, _confirmButtonRect, confirmLabel, ref _confirmHoverScale, mx, my,
                style: isGlobalDanger ? ActionButtonStyle.Danger : ActionButtonStyle.Primary,
                fontSize: ButtonFontSize, isEnabled: true,
                isPressedFunc: () => IsLeftMouseDown());

            drawMouse(b);
        }

        private void DrawHeader(SpriteBatch b, int mx, int my)
        {
            int headX = xPositionOnScreen + ContentPadding;
            int headY = yPositionOnScreen + 14;

            // 40px NPC 肖像框
            const int pSize = 40;
            var portraitRect = new Rectangle(headX, headY, pSize, pSize);

            b.Draw(Game1.staminaRect, new Rectangle(portraitRect.X - 1, portraitRect.Y - 1, portraitRect.Width + 2, portraitRect.Height + 2), new Color(225, 210, 185));
            IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(403, 383, 6, 6),
                portraitRect.X - 2, portraitRect.Y - 2, portraitRect.Width + 4, portraitRect.Height + 4,
                new Color(200, 175, 140), 2f, false);

            if (_npcPortrait != null && !_portraitSmileRect.IsEmpty)
                b.Draw(_npcPortrait, portraitRect, _portraitSmileRect, Color.White);
            else
            {
                string avatarFallback = string.IsNullOrEmpty(_npcName) ? "?" : _npcName.Substring(0, 1);
                var fsz = CustomFontManager.MeasureStringBold(avatarFallback, TitleFontSize);
                CustomFontManager.DrawStringBold(b, avatarFallback,
                    new Vector2(portraitRect.X + (pSize - fsz.X) / 2f, portraitRect.Y + (pSize - fsz.Y) / 2f - 1),
                    BioEditorMenu.TextMuted, TitleFontSize);
            }

            // 主标题
            string title = I18n.DialogueInput.ClearScopeTitle();
            CustomFontManager.DrawStringBold(b, title, new Vector2(headX + pSize + 12, headY), BioEditorMenu.TextPrimary, TitleFontSize);

            // 副提示（高对比度浓郁深茶褐，彻底告别与羊皮纸底板融为一体）
            string hint = I18n.DialogueInput.ClearScopeHint();
            CustomFontManager.DrawString(b, hint, new Vector2(headX + pSize + 14, headY + 28), new Color(75, 50, 32), TipFontSize);

            // 顶栏分割横线
            int sepY = yPositionOnScreen + HeaderH + 2;
            b.Draw(Game1.staminaRect, new Rectangle(headX, sepY, width - ContentPadding * 2, 2), Color.Gray * 0.35f);

            // 右上角关闭按钮平滑动效
            UiHelper.UpdateButtonScale(ref _closeButtonHoverScale, _closeButton, mx, my);
            _closeButton.scale = CloseButtonBaseScale * _closeButtonHoverScale;
            _closeButton.draw(b);
        }

        private void DrawScopeCard(SpriteBatch b, Rectangle rect, ScopeCardOption opt, int mx, int my)
        {
            bool isSelected = _selectedScope == opt.Scope;
            bool isHover = rect.Contains(mx, my);
            bool isPressed = isHover && IsLeftMouseDown();
            bool isDangerOption = opt.Scope == ClearScope.GlobalAll;
            int pressOffset = isPressed ? 1 : 0;

            // 1. 底层微阴影
            if (!isPressed)
                b.Draw(Game1.staminaRect, new Rectangle(rect.X + 1, rect.Y + 2, rect.Width, rect.Height), Color.Black * 0.08f);

            var drawRect = new Rectangle(rect.X, rect.Y + pressOffset, rect.Width, rect.Height);

            // 2. 底色处理（高透亮清爽底色，与文字形成极高视觉反差）
            Color bg;
            if (isSelected)
            {
                bg = isDangerOption
                    ? (isHover ? new Color(255, 235, 230) : new Color(255, 244, 240))
                    : (isHover ? new Color(255, 248, 232) : new Color(255, 244, 222));
            }
            else
            {
                bg = isHover ? new Color(255, 255, 250) : new Color(254, 251, 244);
            }

            // 3. 边框颜色
            Color borderCol;
            if (isSelected)
            {
                borderCol = isDangerOption ? new Color(215, 60, 50) : new Color(210, 110, 15);
            }
            else
            {
                borderCol = isHover ? new Color(195, 150, 85) : new Color(210, 185, 150);
            }

            b.Draw(Game1.staminaRect, new Rectangle(drawRect.X + 1, drawRect.Y + 1, drawRect.Width - 2, drawRect.Height - 2), bg);
            IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(432, 439, 9, 9),
                drawRect.X, drawRect.Y, drawRect.Width, drawRect.Height, borderCol, 2f, false);

            // 4. 激活项左侧立体指示条
            if (isSelected)
            {
                Color barCol = isDangerOption ? new Color(215, 60, 50) : new Color(223, 122, 4);
                b.Draw(Game1.staminaRect, new Rectangle(drawRect.X + 2, drawRect.Y + 4, 4, drawRect.Height - 8), barCol);
            }

            // 5. 单选圆点指示器（Radio Indicator）
            const int radioSize = 20;
            int radioX = drawRect.X + 16;
            int radioY = drawRect.Y + (drawRect.Height - radioSize) / 2;
            var radioRect = new Rectangle(radioX, radioY, radioSize, radioSize);

            IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(432, 439, 9, 9),
                radioRect.X, radioRect.Y, radioRect.Width, radioRect.Height,
                isSelected ? borderCol : new Color(190, 170, 145), 1.4f, false);

            if (isSelected)
            {
                Color dotColor = isDangerOption ? new Color(215, 60, 50) : new Color(223, 122, 4);
                b.Draw(Game1.staminaRect, new Rectangle(radioRect.X + 4, radioRect.Y + 4, radioRect.Width - 8, radioRect.Height - 8), dotColor);
            }

            // 6. ★ 选项文本渲染（加粗标题 + 纯正深焦褐/暗绯红说明，杜绝融于底色）
            int textLeft = radioRect.Right + 14;

            Color titleColor = isSelected && isDangerOption ? new Color(185, 45, 35) : BioEditorMenu.TextPrimary;
            CustomFontManager.DrawStringBold(b, opt.Title, new Vector2(textLeft, drawRect.Y + 14), titleColor, CardHeaderSize);

            Color descColor = isSelected && isDangerOption ? new Color(125, 40, 30) : new Color(68, 48, 34);
            CustomFontManager.DrawString(b, opt.Description, new Vector2(textLeft, drawRect.Y + 44), descColor, DescFontSize);
        }

        private static bool IsLeftMouseDown()
        {
            try { return Game1.input.GetMouseState().LeftButton == ButtonState.Pressed; }
            catch { return false; }
        }
    }
}