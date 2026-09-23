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
    /// AI 提示词与润色方向引导输入弹窗：
    /// 遵循 BioEditorMenu 规范 —— 双层无缝羊皮纸底板、按压下沉动效、纯正暖橘红木外框，
    /// 仅主标题与主动作按钮使用 Bold，副说明、胶囊与占位符回归 Medium 字体。
    /// </summary>
    internal sealed class BioAiPromptDialog : IClickableMenu
    {
        // ── 尺寸与布局常量 ──
        private const int MenuWidth = 720;
        private const int MenuHeight = 390;
        private const int ContentPadding = 24;
        private const int HeaderH = 68;
        private const int FooterH = 56;

        // 统一整数字号（完全继承 CustomFontManager 规范）
        private const float TitleFontSize = CustomFontManager.SizeTitle;       // 24f Bold (顶栏主标题)
        private const float ButtonFontSize = CustomFontManager.SizeRegular;    // 18f Bold (主动作按钮)
        private const float ContentFontSize = CustomFontManager.SizeRegular;   // 18f Medium (输入框文字与胶囊)
        private const float TipFontSize = CustomFontManager.SizeSmall;         // 15f Medium (提示、占位符、气泡)

        private readonly Action<string, bool> _onSubmit;
        private readonly new IClickableMenu _parentMenu;
        private readonly string _targetTitle;
        private readonly string _titleText;
        private readonly bool _allowEmptyDemand;
        private readonly DialogueTextInputBox _inputBox;

        // 顶栏关闭按钮
        private ClickableTextureComponent _closeButton;
        private float _closeButtonHoverScale;
        private const float CloseButtonBaseScale = 3f;

        // 底部动作区域
        private Rectangle _thinkingPillRect;
        private Rectangle _cancelBtnRect;
        private Rectangle _okBtnRect;

        private string? _hoverText;

        public BioAiPromptDialog(string targetTitle, IClickableMenu parentMenu, Action<string, bool> onSubmit, bool allowEmptyDemand = false)
            : base(
                (Game1.uiViewport.Width - Math.Clamp(Game1.uiViewport.Width - 100, 680, MenuWidth)) / 2,
                (Game1.uiViewport.Height - Math.Clamp(Game1.uiViewport.Height - 80, 360, MenuHeight)) / 2,
                Math.Clamp(Game1.uiViewport.Width - 100, 680, MenuWidth),
                Math.Clamp(Game1.uiViewport.Height - 80, 360, MenuHeight),
                showUpperRightCloseButton: false)
        {
            _onSubmit = onSubmit;
            _parentMenu = parentMenu;
            _targetTitle = targetTitle ?? string.Empty;
            _titleText = $"AI 引导 · {_targetTitle}";
            _allowEmptyDemand = allowEmptyDemand;

            _inputBox = new DialogueTextInputBox(600)
            {
                CustomFontSize = ContentFontSize,
                UseCustomFont = true,
                AllowNewlines = false,
                Selected = true,
                ShowCharacterCount = true,
                DrawFrame = true, // ★ 启用纯正暖橘红木外框，彻底告别 403 灰框
                PlaceholderText = _allowEmptyDemand
                    ? "可选：输入期望调整的方向与风格要求（可留空直接生成）..."
                    : "输入您期望的调整方向、细节增补或特定语气（Enter 快速生成）...",
                PlaceholderColor = new Color(175, 145, 115) // ★ 温暖金木色提示词
            };

            _closeButton = new ClickableTextureComponent(
                new Rectangle(xPositionOnScreen + width - 50, yPositionOnScreen + 16, 36, 36),
                Game1.mouseCursors, new Rectangle(337, 494, 12, 12), CloseButtonBaseScale);

            Layout();
            Game1.keyboardDispatcher.Subscriber = _inputBox;
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

            // ★ 文本框直接占据卡片全尺寸，内部 padding 自动保证文字居中
            _inputBox.Position = new Vector2(contentLeft, bodyTop);
            _inputBox.Extent = new Vector2(contentW, boxH);
            _inputBox.InvalidateLayout();

            // 底部操作控件布局
            const int pillW = 160;
            const int okBtnW = 160;
            const int cancelBtnW = 120;

            _thinkingPillRect = new Rectangle(contentLeft, footerY, pillW, btnH);
            _okBtnRect = new Rectangle(xPositionOnScreen + width - ContentPadding - okBtnW, footerY, okBtnW, btnH);
            _cancelBtnRect = new Rectangle(_okBtnRect.X - cancelBtnW - 12, footerY, cancelBtnW, btnH);
        }

        public override void update(GameTime time)
        {
            base.update(time);
            _hoverText = null;
            _inputBox.Update(time);
        }

        public override void gameWindowSizeChanged(Rectangle oldBounds, Rectangle newBounds)
        {
            width = Math.Clamp(Game1.uiViewport.Width - 100, 680, MenuWidth);
            height = Math.Clamp(Game1.uiViewport.Height - 80, 360, MenuHeight);
            xPositionOnScreen = (Game1.uiViewport.Width - width) / 2;
            yPositionOnScreen = (Game1.uiViewport.Height - height) / 2;
            Layout();
        }

        public override void leftClickHeld(int x, int y)
        {
            base.leftClickHeld(x, y);
            _inputBox.LeftClickHeld(x, y);
        }

        public override void releaseLeftClick(int x, int y)
        {
            base.releaseLeftClick(x, y);
            _inputBox.ReleaseLeftClick(x, y);
        }

        public override void receiveLeftClick(int x, int y, bool playSound = true)
        {
            if (_closeButton.containsPoint(x, y))
            {
                Close(false);
                return;
            }

            if (_inputBox.ReceiveLeftClick(x, y))
            {
                Game1.keyboardDispatcher.Subscriber = _inputBox;
                return;
            }

            if (_thinkingPillRect.Contains(x, y))
            {
                BioAiUiPrefs.EnableThinking = !BioAiUiPrefs.EnableThinking;
                Game1.playSound("drumkit6");
                return;
            }

            if (_cancelBtnRect.Contains(x, y))
            {
                Close(false);
                return;
            }

            if (_okBtnRect.Contains(x, y))
            {
                TrySubmit();
            }
        }

        public override void receiveKeyPress(Keys key)
        {
            if (key == Keys.Escape)
            {
                Close(false);
                return;
            }

            if (Game1.options.doesInputListContain(Game1.options.menuButton, key))
                return;

            if (key == Keys.Enter)
            {
                TrySubmit();
                return;
            }

            if (DialogueTextInputBox.IsControlKeyDown())
            {
                if (key == Keys.A || key == Keys.C || key == Keys.X || key == Keys.Z || key == Keys.V)
                {
                    _inputBox.RecieveSpecialInput(key);
                    return;
                }
            }

            if (key == Keys.Left || key == Keys.Right || key == Keys.Up || key == Keys.Down ||
                key == Keys.Home || key == Keys.End || key == Keys.Delete || key == Keys.Back)
            {
                _inputBox.RecieveSpecialInput(key);
            }
        }

        private void TrySubmit()
        {
            string text = _inputBox.Text?.Trim() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(text))
            {
                if (_allowEmptyDemand)
                {
                    Game1.playSound("coin");
                    Close(true);
                    _onSubmit?.Invoke(string.Empty, BioAiUiPrefs.EnableThinking);
                }
                else
                {
                    Game1.playSound("cancel");
                    Game1.addHUDMessage(new HUDMessage("请先输入期望的调整方向（或点取消放弃）", HUDMessage.error_type));
                }
                return;
            }

            Game1.playSound("coin");
            Close(true);
            _onSubmit?.Invoke(text, BioAiUiPrefs.EnableThinking);
        }

        private void Close(bool submitted)
        {
            Game1.keyboardDispatcher.Subscriber = null;

            if (!submitted)
                Game1.playSound("bigDeSelect");

            Game1.activeClickableMenu = _parentMenu;
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

            // 3. ★ 直接绘制输入文本框（完全消除 403 灰凹槽，自带纯正暖橘红木外框与对齐的占位符）
            _inputBox.Draw(b);

            // 4. 底部动作区
            DrawThinkingToggleButton(b, _thinkingPillRect, BioAiUiPrefs.EnableThinking, mx, my);

            bool canSubmit = _allowEmptyDemand || !string.IsNullOrWhiteSpace(_inputBox.Text);
            DrawActionButton(b, _cancelBtnRect, "✕ 取消 (Esc)", mx, my, isDanger: false, isPrimary: false);
            DrawActionButton(b, _okBtnRect, "✔ 开始生成", mx, my, isDanger: false, isPrimary: true, isEnabled: canSubmit);

            // 5. 悬停提示文本处理
            if (_thinkingPillRect.Contains(mx, my))
            {
                _hoverText = BioAiUiPrefs.EnableThinking
                    ? "【深度思考模式：已开启】\n模型将进行更严密的多阶段推理与推演，构思更细腻，响应时间稍长。\n推荐用于复杂身世背景、深层矛盾或好感演变等关键节点。"
                    : "【极速生成模式：已开启】\n直接生成对白与规则，响应极快，轻快高效。\n适合简短对白、口头禅修正等快速润色任务。";
            }
            else if (_okBtnRect.Contains(mx, my))
            {
                _hoverText = canSubmit
                    ? "【开始生成】\n将当前指示提交给大模型开始构思与推演。"
                    : "【前置要求】\n当前项目需要明确指导，请在上方输入框填写调整方向。";
            }

            if (!string.IsNullOrEmpty(_hoverText))
                DrawHoverTextCustom(b, _hoverText);

            drawMouse(b);
        }

        private void DrawHeader(SpriteBatch b, int mx, int my)
        {
            int headX = xPositionOnScreen + ContentPadding;
            int headY = yPositionOnScreen + 16;

            // ★ 主标题：唯一使用 Bold，SizeTitle (24f)
            CustomFontManager.DrawStringBold(b, _titleText, new Vector2(headX, headY), BioEditorMenu.TextPrimary, TitleFontSize);

            // ★ 副说明：Medium 字体，SizeSmall (15f)
            const string sub = "请在下方输入框明确指导 AI 的构思方向（例如：翻译为其他语言、改得更加活泼一些等。）；";
            CustomFontManager.DrawString(b, sub, new Vector2(headX + 2, headY + 28), BioEditorMenu.TextMuted, TipFontSize);

            // 分割横线
            int sepY = yPositionOnScreen + HeaderH + 2;
            b.Draw(Game1.staminaRect, new Rectangle(headX, sepY, width - ContentPadding * 2, 2), Color.Gray * 0.35f);

            // 关闭按钮平滑缩放动效
            UiHelper.UpdateButtonScale(ref _closeButtonHoverScale, _closeButton, mx, my);
            _closeButton.scale = CloseButtonBaseScale * _closeButtonHoverScale;
            _closeButton.draw(b);
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

        /// <summary>
        /// 绘制符合 BioEditorMenu 动作栏规范的思考/极速模式立体开关按钮
        /// </summary>
        private static void DrawThinkingToggleButton(SpriteBatch b, Rectangle rect, bool isThinking, int mx, int my)
        {
            bool isHover = rect.Contains(mx, my);
            bool isPressed = isHover && IsLeftMouseDown();
            int pressOffset = isPressed ? 1 : 0;

            // 开启时呈现金黄主色，极速时呈现浅木棕色（与同排动作按钮色阶一致）
            Color bg = isThinking
                ? (isHover ? Color.Gold : new Color(255, 220, 130))
                : (isHover ? new Color(255, 240, 215) : new Color(225, 195, 155));

            if (isPressed)
                bg = Color.Lerp(bg, Color.Black, 0.14f);

            // 1. 底层立体微阴影（按下时瞬时收起）
            if (!isPressed)
            {
                b.Draw(Game1.staminaRect,
                    new Rectangle(rect.X + 2, rect.Y + 2, rect.Width, rect.Height),
                    Color.Black * 0.15f);
            }

            // 2. 内衬填充
            b.Draw(Game1.staminaRect,
                new Rectangle(rect.X + 1 + pressOffset, rect.Y + 1 + pressOffset, rect.Width - 2, rect.Height - 2),
                bg);

            // 3. 3f 厚原木九宫格边框（与同排主按钮 3f 保持完全一致）
            Color borderCol = isThinking ? new Color(210, 160, 60) : new Color(185, 150, 110);
            IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(432, 439, 9, 9),
                rect.X + pressOffset, rect.Y + pressOffset, rect.Width, rect.Height, borderCol, 3f, false);

            // 4. 文字标示统一采用 18f Bold 粗体与居中对齐
            string label = isThinking ? "思考模式:深度" : "思考模式:快速";
            var sz = CustomFontManager.MeasureStringBold(label, ButtonFontSize);
            CustomFontManager.DrawStringBold(b, label,
                new Vector2(
                    rect.X + pressOffset + (rect.Width - sz.X) / 2f,
                    rect.Y + pressOffset + (rect.Height - sz.Y) / 2f),
                BioEditorMenu.TextOnLightBtn, ButtonFontSize);
        }

        private static void DrawActionButton(SpriteBatch b, Rectangle rect, string label, int mx, int my,
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