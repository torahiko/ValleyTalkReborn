#nullable enable

using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewValley;
using StardewValley.Menus;
using ValleytalkReborn.UI;

namespace ValleytalkReborn
{
    /// <summary>
    /// 玩家对话自由文本输入菜单：
    /// 遵循 BioEditorMenu 整体视觉与动效体系 —— 羊皮纸无缝底板、立体悬浮平滑缩放、
    /// 仅主标题与主动作按钮使用 Bold 字体；集成角色肖像预览、历史记忆追溯与清空工具栏。
    /// </summary>
    public class DialogueTextInputMenu : IClickableMenu
    {
        public delegate void TextSubmittedDelegate(string input);

        // 统一整数字号（完全对齐 CustomFontManager 规范）
        private const float TitleFontSize = CustomFontManager.SizeTitle;       // 24f Bold (顶栏主标题)
        private const float ButtonFontSize = CustomFontManager.SizeRegular;    // 18f Bold (底部动作按钮)
        private const float ContentFontSize = CustomFontManager.SizeRegular;   // 18f Medium (输入框文字)
        private const float TipFontSize = CustomFontManager.SizeSmall;         // 15f Medium (副说明、提示与气泡)

        private const int MenuWidth = 960;
        private const int MenuHeight = 560;
        private const int ContentPadding = 24;
        private const int HeaderH = 68;
        private const int FooterH = 56;

        private readonly string _title;
        private readonly string _npcName;
        private readonly string _npcDisplayName;
        private readonly TextSubmittedDelegate _onTextSubmitted;
        private readonly DialogueTextInputBox _inputTextBox;

        // 肖像与关闭按钮
        private Texture2D? _npcPortrait;
        private Rectangle _portraitSmileRect;
        private ClickableTextureComponent _closeButton;
        private float _closeButtonHoverScale;
        private const float CloseButtonBaseScale = 3f;

        // 底部动作按钮矩形
        private Rectangle _viewHistoryRect;
        private Rectangle _clearHistoryRect;
        private Rectangle _cancelButtonRect;
        private Rectangle _okButtonRect;

        // ★ 按钮平滑悬停弹性动效系数
        private float _viewHistoryHoverScale = 1f;
        private float _clearHistoryHoverScale = 1f;
        private float _cancelButtonHoverScale = 1f;
        private float _okButtonHoverScale = 1f;

        private IClickableMenu? _menuToRestore;
        private string? _hoverText;

        public Rectangle MenuBounds => new(xPositionOnScreen, yPositionOnScreen, width, height);

        public DialogueTextInputMenu(string title, TextSubmittedDelegate callback, NPC currentNpc)
            : base(
                (Game1.uiViewport.Width - Math.Clamp(Game1.uiViewport.Width - 100, 840, MenuWidth)) / 2,
                (Game1.uiViewport.Height - Math.Clamp(Game1.uiViewport.Height - 80, 500, MenuHeight)) / 2,
                Math.Clamp(Game1.uiViewport.Width - 100, 840, MenuWidth),
                Math.Clamp(Game1.uiViewport.Height - 80, 500, MenuHeight),
                showUpperRightCloseButton: false)
        {
            _npcName = currentNpc?.Name ?? string.Empty;
            _npcDisplayName = currentNpc?.displayName ?? _npcName;
            _title = title ?? (string.IsNullOrEmpty(_npcName)
                ? I18n.DialogueInput.DefaultTitle()
                : I18n.DialogueInput.DefaultTitleWithNpc(_npcDisplayName));
            _onTextSubmitted = callback;

            // 文本框初始化（使用 18f 锐利字号，暖橘红木外框与内置占位符）
            string placeholder = !string.IsNullOrEmpty(_npcDisplayName)
                ? I18n.DialogueInput.PlaceholderWithName(_npcDisplayName)
                : I18n.DialogueInput.Placeholder();

            _inputTextBox = new DialogueTextInputBox(300)
            {
                AllowNewlines = false,
                UseCustomFont = true,
                CustomFontSize = ContentFontSize,
                CounterFontSize = TipFontSize,
                DrawFrame = true,
                ShowCharacterCount = true,
                TextColor = BioEditorMenu.TextPrimary,
                Selected = true,
                PlaceholderText = placeholder,
                PlaceholderColor = new Color(175, 145, 115)
            };

            _inputTextBox.OnSubmit += sender =>
            {
                Game1.playSound("coin");
                Submit(sender.Text);
            };

            _closeButton = new ClickableTextureComponent(
                new Rectangle(xPositionOnScreen + width - 50, yPositionOnScreen + 16, 36, 36),
                Game1.mouseCursors, new Rectangle(337, 494, 12, 12), CloseButtonBaseScale);

            LoadNpcPortrait();
            Layout();
            RestoreFocus();
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
            int bodyTop = yPositionOnScreen + HeaderH + 12;

            int footerY = yPositionOnScreen + height - FooterH + 10;
            const int btnH = 38;

            int boxH = footerY - 14 - bodyTop;
            _inputTextBox.Position = new Vector2(contentLeft, bodyTop);
            _inputTextBox.Extent = new Vector2(contentW, Math.Max(140, boxH));
            _inputTextBox.InvalidateLayout();

            // 底部按钮布局
            const int viewHistBtnW = 126;
            const int clearHistBtnW = 116;
            const int cancelBtnW = 120;
            const int okBtnW = 190;

            _viewHistoryRect = new Rectangle(contentLeft, footerY, viewHistBtnW, btnH);
            _clearHistoryRect = new Rectangle(_viewHistoryRect.Right + 10, footerY, clearHistBtnW, btnH);

            _okButtonRect = new Rectangle(xPositionOnScreen + width - ContentPadding - okBtnW, footerY, okBtnW, btnH);
            _cancelButtonRect = new Rectangle(_okButtonRect.X - cancelBtnW - 12, footerY, cancelBtnW, btnH);
        }

        public override void gameWindowSizeChanged(Rectangle oldBounds, Rectangle newBounds)
        {
            width = Math.Clamp(Game1.uiViewport.Width - 100, 840, MenuWidth);
            height = Math.Clamp(Game1.uiViewport.Height - 80, 500, MenuHeight);
            xPositionOnScreen = (Game1.uiViewport.Width - width) / 2;
            yPositionOnScreen = (Game1.uiViewport.Height - height) / 2;
            Layout();
        }

        public override void update(GameTime time)
        {
            base.update(time);
            _hoverText = null;
            _inputTextBox.Update(time);
        }

        public void SetMenuToRestore(IClickableMenu menu)
        {
            _menuToRestore = menu;
        }

        public void RestoreFocus()
        {
            _inputTextBox.Selected = true;
            Game1.keyboardDispatcher.Subscriber = _inputTextBox;
        }

        public void RestorePreviousMenu()
        {
            Game1.activeClickableMenu = _menuToRestore ?? this;
            RestoreFocus();
        }

        public void Close()
        {
            if (Game1.keyboardDispatcher.Subscriber == _inputTextBox)
                Game1.keyboardDispatcher.Subscriber = null;

            if (Game1.activeClickableMenu == this)
                Game1.activeClickableMenu = null;
        }

        public void Submit(string text)
        {
            _onTextSubmitted?.Invoke(text?.TrimEnd('\r', '\n') ?? string.Empty);
        }

        // ── 交互输入分发与 Wrapper 适配 ──────────────────────────────────────────

        public override void leftClickHeld(int x, int y)
        {
            base.leftClickHeld(x, y);
            _inputTextBox.LeftClickHeld(x, y);
        }

        public override void releaseLeftClick(int x, int y)
        {
            base.releaseLeftClick(x, y);
            _inputTextBox.ReleaseLeftClick(x, y);
        }

        public override void receiveScrollWheelAction(int direction)
        {
            base.receiveScrollWheelAction(direction);
            ReceiveScrollWheel(direction);
        }

        public void ReceiveScrollWheel(int direction)
        {
            _inputTextBox.ReceiveScrollWheel(direction);
        }

        public override void receiveLeftClick(int x, int y, bool playSound = true)
        {
            ReceiveLeftClick(x, y);
        }

        public void ReceiveLeftClick(int x, int y)
        {
            if (_closeButton.containsPoint(x, y))
            {
                Game1.playSound("bigDeSelect");
                Submit(string.Empty);
                return;
            }

            if (_inputTextBox.ReceiveLeftClick(x, y))
            {
                RestoreFocus();
                return;
            }

            if (_viewHistoryRect.Contains(x, y))
            {
                Game1.playSound("bigSelect");
                Game1.keyboardDispatcher.Subscriber = null;
                var target = _menuToRestore ?? this;
                Game1.activeClickableMenu = new TimelineChronicleMenu(_npcName, target, target, autoLockLatest: false);
                return;
            }

            if (_clearHistoryRect.Contains(x, y))
            {
                Game1.keyboardDispatcher.Subscriber = null;
                Game1.activeClickableMenu = new ClearHistoryScopeMenu(_npcName, this, scope =>
                {
                    Game1.playSound("trashcan");
                    string dispName = Game1.getCharacterFromName(_npcName)?.displayName ?? _npcName;
                    switch (scope)
                    {
                        case ClearHistoryScopeMenu.ClearScope.Today:
                            DialogueHistoryManager.Instance.ClearTodayHistory(_npcName);
                            SessionCache.Instance.Reset(_npcName);
                            RecentConversationTracker.Clear(_npcName);
                            DialogueBuilder.Instance?.ClearContext(_npcName);
                            Game1.addHUDMessage(new HUDMessage(
                                I18n.DialogueInput.ClearScopeHudToday(dispName), HUDMessage.achievement_type));
                            break;
                        case ClearHistoryScopeMenu.ClearScope.CurrentNpcAll:
                            ClearHistory(_npcName);
                            Game1.addHUDMessage(new HUDMessage(
                                I18n.DialogueInput.ClearScopeHudCurrentNpcAll(dispName), HUDMessage.achievement_type));
                            break;
                        case ClearHistoryScopeMenu.ClearScope.GlobalAll:
                            ClearHistory();
                            Game1.addHUDMessage(new HUDMessage(
                                I18n.DialogueInput.ClearScopeHudGlobalAll(), HUDMessage.achievement_type));
                            break;
                    }
                });
                return;
            }

            if (_cancelButtonRect.Contains(x, y))
            {
                Game1.playSound("cancel");
                Submit(string.Empty);
                return;
            }

            if (_okButtonRect.Contains(x, y))
            {
                Game1.playSound("coin");
                Submit(_inputTextBox.Text);
                return;
            }

            RestoreFocus();
        }

        public override void receiveKeyPress(Keys key)
        {
            ReceiveKeyPress(key);
        }

        public void ReceiveKeyPress(Keys key)
        {
            if (key == Keys.Escape)
            {
                Game1.playSound("cancel");
                Submit(string.Empty);
                return;
            }

            if (Game1.options.doesInputListContain(Game1.options.menuButton, key))
                return;

            if (key == Keys.Enter)
            {
                Game1.playSound("coin");
                Submit(_inputTextBox.Text);
                return;
            }

            if (DialogueTextInputBox.IsControlKeyDown())
            {
                if (key == Keys.A || key == Keys.C || key == Keys.X || key == Keys.Z || key == Keys.V)
                {
                    _inputTextBox.RecieveSpecialInput(key);
                    return;
                }
            }

            if (key == Keys.Left || key == Keys.Right || key == Keys.Up || key == Keys.Down ||
                key == Keys.Home || key == Keys.End || key == Keys.Delete || key == Keys.Back)
            {
                _inputTextBox.RecieveSpecialInput(key);
            }
        }

        protected override void cleanupBeforeExit()
        {
            Game1.keyboardDispatcher.Subscriber = null;
            base.cleanupBeforeExit();
        }

        private void ClearHistory()
        {
            DialogueHistoryManager.Instance.ClearAllHistory();
            SessionCache.Instance.ResetAll();
            RecentConversationTracker.Clear();
            DialogueBuilder.Instance?.ClearAllContexts();
        }

        private void ClearHistory(string npcName)
        {
            DialogueHistoryManager.Instance.ClearHistory(npcName);
            SessionCache.Instance.Reset(npcName);
            RecentConversationTracker.Clear(npcName);
            DialogueBuilder.Instance?.ClearContext(npcName);
        }

        // ── 渲染管线 ──────────────────────────────────────────────────────────

        public override void draw(SpriteBatch b)
        {
            int mx = Game1.getMouseX();
            int my = Game1.getMouseY();

            // ★ 核心修复 2：每一帧绘制开头无条件置空，彻底根治悬停气泡粘在鼠标上的 Bug
            _hoverText = null;

            // 1. 全屏半透明遮罩
            b.Draw(Game1.fadeToBlackRect, Game1.graphics.GraphicsDevice.Viewport.Bounds, Color.Black * 0.5f);

            // 2. 双层羊皮纸木框底板（对齐 BioEditorMenu）
            IClickableMenu.drawTextureBox(b, xPositionOnScreen - 8, yPositionOnScreen - 8, width + 16, height + 16, Color.White);

            b.Draw(Game1.staminaRect, new Rectangle(xPositionOnScreen, yPositionOnScreen, width, height), new Color(245, 230, 205));
            b.Draw(
                Game1.menuTexture,
                new Rectangle(xPositionOnScreen, yPositionOnScreen, width, height),
                new Rectangle(64, 128, 64, 64),
                new Color(245, 230, 205));

            DrawHeader(b, mx, my);

            // 3. 绘制输入文本框（自带暖橘红木外框与自适应占位符）
            _inputTextBox.Draw(b);

            // 4. ★ 核心修复 1：接入平滑弹性缩放、高亮边框与浮起阴影动效的现代化实体按钮
            DrawAnimatedActionButton(b, _viewHistoryRect, I18n.DialogueInput.ButtonHistory(), ref _viewHistoryHoverScale, mx, my, isPrimary: false);
            DrawAnimatedActionButton(b, _clearHistoryRect, I18n.DialogueInput.ButtonClearMemory(), ref _clearHistoryHoverScale, mx, my, isDanger: false);
            DrawAnimatedActionButton(b, _cancelButtonRect, I18n.DialogueInput.ButtonCancel(), ref _cancelButtonHoverScale, mx, my, isDanger: false);
            DrawAnimatedActionButton(b, _okButtonRect, I18n.DialogueInput.ButtonSend(), ref _okButtonHoverScale, mx, my, isPrimary: true);

            // 5. 悬停提示检测（鼠标离开感应区后当帧自然失效）
            if (_viewHistoryRect.Contains(mx, my))
            {
                _hoverText = !string.IsNullOrEmpty(_npcDisplayName)
                    ? I18n.DialogueInput.TooltipHistory(_npcDisplayName)
                    : I18n.DialogueInput.TooltipHistoryGeneric();
            }
            else if (_clearHistoryRect.Contains(mx, my))
            {
                _hoverText = !string.IsNullOrEmpty(_npcDisplayName)
                    ? I18n.DialogueInput.TooltipClearMemory(_npcDisplayName)
                    : I18n.DialogueInput.TooltipClearMemoryGeneric();
            }
            else if (_cancelButtonRect.Contains(mx, my))
            {
                _hoverText = I18n.DialogueInput.TooltipCancel();
            }
            else if (_okButtonRect.Contains(mx, my))
            {
                _hoverText = I18n.DialogueInput.TooltipSend();
            }

            if (!string.IsNullOrEmpty(_hoverText))
                DrawHoverTextCustom(b, _hoverText);

            drawMouse(b);
        }

        private void DrawHeader(SpriteBatch b, int mx, int my)
        {
            int headX = xPositionOnScreen + ContentPadding;
            int headY = yPositionOnScreen + 14;

            // 肖像框
            const int pSize = 44;
            var portraitRect = new Rectangle(headX, headY, pSize, pSize);

            b.Draw(Game1.staminaRect, new Rectangle(portraitRect.X - 1, portraitRect.Y - 1, portraitRect.Width + 2, portraitRect.Height + 2), new Color(225, 210, 185));
            IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(403, 383, 6, 6),
                portraitRect.X - 2, portraitRect.Y - 2, portraitRect.Width + 4, portraitRect.Height + 4,
                new Color(200, 175, 140), 2f, false);

            if (_npcPortrait != null && !_portraitSmileRect.IsEmpty)
                b.Draw(_npcPortrait, portraitRect, _portraitSmileRect, Color.White);
            else
            {
                string avatarFallback = string.IsNullOrEmpty(_npcDisplayName) ? "?" : _npcDisplayName.Substring(0, 1);
                var fsz = CustomFontManager.MeasureStringBold(avatarFallback, TitleFontSize);
                CustomFontManager.DrawStringBold(b, avatarFallback,
                    new Vector2(portraitRect.X + (pSize - fsz.X) / 2f, portraitRect.Y + (pSize - fsz.Y) / 2f - 1),
                    BioEditorMenu.TextMuted, TitleFontSize);
            }

            // ★ 主标题：唯一使用 Bold，SizeTitle (24f)
            CustomFontManager.DrawStringBold(b, _title, new Vector2(headX + pSize + 12, headY + 2), BioEditorMenu.TextPrimary, TitleFontSize);

            // ★ 副说明：Medium 字体，SizeSmall (15f)
            string subtitle = !string.IsNullOrEmpty(_npcDisplayName)
                ? I18n.DialogueInput.SubtitleWithName(_npcDisplayName)
                : I18n.DialogueInput.SubtitleGeneric();
            CustomFontManager.DrawString(b, subtitle, new Vector2(headX + pSize + 14, headY + 30), BioEditorMenu.TextMuted, TipFontSize);

            // 分割横线
            int sepY = yPositionOnScreen + HeaderH + 4;
            b.Draw(Game1.staminaRect, new Rectangle(headX, sepY, width - ContentPadding * 2, 2), Color.Gray * 0.35f);

            // 关闭按钮平滑缩放
            UiHelper.UpdateButtonScale(ref _closeButtonHoverScale, _closeButton, mx, my);
            _closeButton.scale = CloseButtonBaseScale * _closeButtonHoverScale;
            _closeButton.draw(b);
        }

        private static bool IsLeftMouseDown()
        {
            try { return Game1.input.GetMouseState().LeftButton == ButtonState.Pressed; }
            catch { return false; }
        }

        /// <summary>
        /// 带有平滑插值悬浮放大、高光边框及按压立体反馈的现代化实体动作按钮。
        /// </summary>
        private static void DrawAnimatedActionButton(
            SpriteBatch b,
            Rectangle rect,
            string label,
            ref float hoverScale,
            int mx, int my,
            bool isDanger = false,
            bool isPrimary = false,
            bool isEnabled = true)
        {
            bool isHover = isEnabled && rect.Contains(mx, my);
            bool isPressed = isHover && IsLeftMouseDown();

            // ★ 悬浮缩放平滑插值（0.25f 线性插值步进，悬浮时 1.04f 饱满微放大）
            float targetScale = (isHover && !isPressed) ? 1.04f : 1.0f;
            hoverScale += (targetScale - hoverScale) * 0.25f;

            int drawW = (int)MathF.Round(rect.Width * hoverScale);
            int drawH = (int)MathF.Round(rect.Height * hoverScale);
            int drawX = rect.X - (drawW - rect.Width) / 2;
            int drawY = rect.Y - (drawH - rect.Height) / 2;
            int pressOffset = isPressed ? 1 : 0;

            // 1. 悬停浮起立体投影（悬停时阴影沉降展开，按下时收起）
            if (!isPressed)
            {
                int shadowY = isHover ? 3 : 2;
                float shadowAlpha = isHover ? 0.22f : 0.14f;
                b.Draw(Game1.staminaRect,
                    new Rectangle(drawX + 1, drawY + shadowY, drawW, drawH),
                    Color.Black * shadowAlpha);
            }

            // 2. 悬停高亮底色反馈
            Color bg;
            if (!isEnabled)
            {
                bg = Color.LightGray * 0.6f;
            }
            else if (isPrimary)
            {
                // 主按钮：常态金黄，悬浮时高亮灿金
                bg = isHover ? new Color(255, 228, 105) : new Color(255, 208, 115);
            }
            else if (isDanger)
            {
                bg = isHover ? new Color(250, 115, 115) : new Color(210, 85, 80);
            }
            else
            {
                // 普通按钮：常态温润麦木色，悬停时明显提亮为明润象牙暖白
                bg = isHover ? new Color(255, 248, 235) : new Color(228, 202, 168);
            }

            if (isPressed)
                bg = Color.Lerp(bg, Color.Black, 0.14f);

            var btnBounds = new Rectangle(drawX + pressOffset, drawY + pressOffset, drawW, drawH);
            b.Draw(Game1.staminaRect,
                new Rectangle(btnBounds.X + 1, btnBounds.Y + 1, btnBounds.Width - 2, btnBounds.Height - 2),
                bg);

            // 3. 边框：悬停时呈现鲜亮暖金橙发光边框，强化交互焦点
            Color borderCol;
            if (!isEnabled)
            {
                borderCol = Color.Gray * 0.5f;
            }
            else if (isPrimary)
            {
                borderCol = isHover ? new Color(245, 160, 30) : new Color(205, 140, 45);
            }
            else if (isDanger)
            {
                borderCol = isHover ? new Color(195, 55, 50) : new Color(165, 50, 45);
            }
            else
            {
                borderCol = isHover ? new Color(215, 140, 50) : new Color(185, 145, 105);
            }

            IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(432, 439, 9, 9),
                btnBounds.X, btnBounds.Y, btnBounds.Width, btnBounds.Height,
                borderCol, 3f, false);

            // 4. 文字居中渲染
            Color textCol = !isEnabled ? BioEditorMenu.TextMuted
                          : isDanger ? BioEditorMenu.TextOnDarkBtn
                          : BioEditorMenu.TextOnLightBtn;

            ButtonTextRenderer.DrawButtonText(b, label, btnBounds, textCol, useBold: true);
        }

        private static void DrawHoverTextCustom(SpriteBatch b, string text)
        {
            if (string.IsNullOrEmpty(text)) return;

            var sz = CustomFontManager.MeasureString(text, TipFontSize);
            const int padX = 20;
            const int padY = 12;

            int boxW = (int)MathF.Ceiling(sz.X) + padX * 2;
            int boxH = (int)MathF.Ceiling(sz.Y) + padY * 2;

            int x = Game1.getOldMouseX() + 24;
            int y = Game1.getOldMouseY() + 24;
            var safe = Utility.getSafeArea();

            if (x + boxW > safe.Right) x = safe.Right - boxW;
            if (y + boxH > safe.Bottom)
            {
                x += 16;
                if (x + boxW > safe.Right) x = safe.Right - boxW;
                y = safe.Bottom - boxH;
            }
            if (x < safe.Left) x = safe.Left;
            if (y < safe.Top) y = safe.Top;

            IClickableMenu.drawTextureBox(b, Game1.menuTexture, new Rectangle(0, 256, 60, 60),
                x + 4, y + 4, boxW, boxH, Color.Black * 0.28f, 0.65f, false);

            IClickableMenu.drawTextureBox(b, Game1.menuTexture, new Rectangle(0, 256, 60, 60),
                x, y, boxW, boxH, new Color(255, 255, 250), 0.65f, false);

            float textY = y + (boxH - sz.Y) / 2f - 1;
            CustomFontManager.DrawString(b, text, new Vector2(x + padX, textY), BioEditorMenu.TextPrimary, TipFontSize);
        }
    }
}