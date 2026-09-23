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
    /// "引导式起号"四问问卷屏（2×2 网格）：
    /// 遵循 BioEditorMenu 设计规范 —— 采用星露谷经典暖橘/金棕木质配色体系，
    /// 集成深度思考/极速模式总控胶囊（状态贯穿后续所有起号层级）；
    /// 闪烁光标与示例占位符像素级绝对对齐。
    /// </summary>
    internal sealed class BioWizardQuestionnaireDialog : IClickableMenu
    {
        // ── 尺寸与布局常量 ──
        private const int MenuWidth = 920;
        private const int MenuHeight = 630;
        private const int ContentPadding = 24;
        private const int HeaderH = 68;
        private const int FooterH = 56;

        // 统一整数字号（避免亚像素模糊）
        private const float TitleFontSize = CustomFontManager.SizeTitle;       // 24f Bold (顶栏主标题)
        private const float ButtonFontSize = CustomFontManager.SizeRegular;    // 18f Bold (主动作按钮)
        private const float SectionHeaderSize = CustomFontManager.SizeRegular; // 18f Medium (栏目标题)
        private const float ContentFontSize = CustomFontManager.SizeRegular;   // 18f Medium (输入框文字与示例文字)
        private const float TipFontSize = CustomFontManager.SizeSmall;         // 15f Medium (提示、胶囊与小标签)

        private readonly string _npcName;
        private readonly new IClickableMenu _parentMenu;
        private readonly Action<string, bool> _onSubmit;

        // 肖像与关闭按钮
        private Texture2D? _npcPortrait;
        private Rectangle _portraitSmileRect;
        private ClickableTextureComponent _closeButton;
        private float _closeButtonHoverScale;
        private const float CloseButtonBaseScale = 3f;

        // 问卷卡片与输入框
        private readonly DialogueTextInputBox[] _boxes;
        private readonly Rectangle[] _cardRects;
        private int _focusedIndex;

        // 底部动作区域控件
        private Rectangle _thinkingPillRect; // ★ 思考模式 / 极速模式总控胶囊
        private Rectangle _aiFreeButtonRect;
        private Rectangle _cancelButtonRect;
        private Rectangle _okButtonRect;

        private string? _hoverText;

        private static readonly string[] QuestionHeaders = new[]
        {
            "1. 明面身份与生活日常",
            "2. 心理矛盾与深层弱点",
            "3. 说话风格与口吻习惯",
            "4. 对外来农夫第一印象"
        };

        private static readonly string[] QuestionPlaceholders = new[]
        {
            "例：单亲矿工 / 退役邮递员 / 守墓人之女……",
            "例：愧疚于一段旧关系 / 害怕被抛弃 / 强迫性囤积……",
            "例：慢条斯理带方言 / 粗鲁但心软 / 沉默寡言……",
            "例：好奇但警惕 / 热情好客 / 冷漠疏离……"
        };

        public BioWizardQuestionnaireDialog(string npcName, IClickableMenu parentMenu, Action<string, bool> onSubmit)
            : base(
                (Game1.uiViewport.Width - Math.Clamp(Game1.uiViewport.Width - 100, 880, MenuWidth)) / 2,
                (Game1.uiViewport.Height - Math.Clamp(Game1.uiViewport.Height - 80, 580, MenuHeight)) / 2,
                Math.Clamp(Game1.uiViewport.Width - 100, 880, MenuWidth),
                Math.Clamp(Game1.uiViewport.Height - 80, 580, MenuHeight),
                showUpperRightCloseButton: false)
        {
            _npcName = npcName;
            _parentMenu = parentMenu;
            _onSubmit = onSubmit;

            _boxes = new DialogueTextInputBox[4];
            _cardRects = new Rectangle[4];

            for (int i = 0; i < 4; i++)
            {
                _boxes[i] = new DialogueTextInputBox(300)
                {
                    CustomFontSize = ContentFontSize,
                    UseCustomFont = true,
                    AllowNewlines = false,
                    Selected = i == 0,
                    DrawFrame = true, // 原生暖橘红木质感底框
                    PlaceholderText = QuestionPlaceholders[i],
                    PlaceholderColor = new Color(175, 145, 115) // 温暖金木色提示词
                };
            }

            _focusedIndex = 0;
            Game1.keyboardDispatcher.Subscriber = _boxes[0];

            _closeButton = new ClickableTextureComponent(
                new Rectangle(xPositionOnScreen + width - 52, yPositionOnScreen + 16, 36, 36),
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
            int gridTop = yPositionOnScreen + HeaderH + 14;

            int footerY = yPositionOnScreen + height - FooterH + 10;
            int availGridH = footerY - 16 - gridTop;

            const int gapX = 16;
            const int gapY = 14;
            int cardW = (contentW - gapX) / 2;
            int cardH = (availGridH - gapY) / 2;

            for (int i = 0; i < 4; i++)
            {
                int col = i % 2;
                int row = i / 2;
                int cx = contentLeft + col * (cardW + gapX);
                int cy = gridTop + row * (cardH + gapY);

                _cardRects[i] = new Rectangle(cx, cy, cardW, cardH);

                int boxX = cx + 8;
                int boxY = cy + 34;
                int boxW = cardW - 16;
                int boxH = cardH - 42;

                _boxes[i].Position = new Vector2(boxX, boxY);
                _boxes[i].Extent = new Vector2(boxW, boxH);
                _boxes[i].InvalidateLayout();
            }

            // ★ 底部动作按钮布局（左侧：模式胶囊 + 自由发挥；右侧：取消 + 开始生成）
            const int btnH = 38;
            const int pillW = 160;
            const int aiBtnW = 210;
            const int cancelBtnW = 110;
            const int okBtnW = 200;

            _thinkingPillRect = new Rectangle(contentLeft, footerY, pillW, btnH);
            _aiFreeButtonRect = new Rectangle(_thinkingPillRect.Right + 10, footerY, aiBtnW, btnH);

            _okButtonRect = new Rectangle(xPositionOnScreen + width - ContentPadding - okBtnW, footerY, okBtnW, btnH);
            _cancelButtonRect = new Rectangle(_okButtonRect.X - cancelBtnW - 10, footerY, cancelBtnW, btnH);
        }

        public override void update(GameTime time)
        {
            base.update(time);
            _hoverText = null;
            for (int i = 0; i < _boxes.Length; i++)
                _boxes[i].Update(time);
        }

        public override void gameWindowSizeChanged(Rectangle oldBounds, Rectangle newBounds)
        {
            width = Math.Clamp(Game1.uiViewport.Width - 100, 880, MenuWidth);
            height = Math.Clamp(Game1.uiViewport.Height - 80, 580, MenuHeight);
            xPositionOnScreen = (Game1.uiViewport.Width - width) / 2;
            yPositionOnScreen = (Game1.uiViewport.Height - height) / 2;
            Layout();
        }

        public override void leftClickHeld(int x, int y)
        {
            base.leftClickHeld(x, y);
            if (_focusedIndex >= 0 && _focusedIndex < _boxes.Length)
                _boxes[_focusedIndex].LeftClickHeld(x, y);
        }

        public override void releaseLeftClick(int x, int y)
        {
            base.releaseLeftClick(x, y);
            if (_focusedIndex >= 0 && _focusedIndex < _boxes.Length)
                _boxes[_focusedIndex].ReleaseLeftClick(x, y);
        }

        public override void receiveScrollWheelAction(int direction)
        {
            base.receiveScrollWheelAction(direction);
            int mx = Game1.getMouseX();
            int my = Game1.getMouseY();
            for (int i = 0; i < _boxes.Length; i++)
            {
                if (_cardRects[i].Contains(mx, my))
                {
                    _boxes[i].ReceiveScrollWheel(direction);
                    return;
                }
            }
        }

        public override void receiveLeftClick(int x, int y, bool playSound = true)
        {
            if (_closeButton.containsPoint(x, y))
            {
                Game1.playSound("bigDeSelect");
                Cancel();
                return;
            }

            // 卡片区域点击聚焦
            for (int i = 0; i < _boxes.Length; i++)
            {
                if (_cardRects[i].Contains(x, y))
                {
                    SetFocus(i);
                    _boxes[i].ReceiveLeftClick(x, y);
                    return;
                }
            }

            // ★ 深度思考 / 极速模式切换胶囊
            if (_thinkingPillRect.Contains(x, y))
            {
                BioAiUiPrefs.EnableThinking = !BioAiUiPrefs.EnableThinking;
                Game1.playSound("drumkit6");
                return;
            }

            if (_aiFreeButtonRect.Contains(x, y))
            {
                SubmitEmpty();
                return;
            }

            if (_cancelButtonRect.Contains(x, y))
            {
                Cancel();
                return;
            }

            if (_okButtonRect.Contains(x, y))
            {
                TrySubmit();
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

            if (key == Keys.Tab)
            {
                SetFocus((_focusedIndex + 1) % _boxes.Length);
                Game1.playSound("smallSelect");
                return;
            }

            if (key == Keys.Enter)
            {
                TrySubmit();
                return;
            }

            if (DialogueTextInputBox.IsControlKeyDown())
            {
                if (key == Keys.A || key == Keys.C || key == Keys.X || key == Keys.Z || key == Keys.V)
                {
                    _boxes[_focusedIndex].RecieveSpecialInput(key);
                    return;
                }
            }

            if (key == Keys.Left || key == Keys.Right || key == Keys.Up || key == Keys.Down ||
                key == Keys.Home || key == Keys.End || key == Keys.Delete || key == Keys.Back)
            {
                _boxes[_focusedIndex].RecieveSpecialInput(key);
            }
        }

        private void SetFocus(int index)
        {
            if (index == _focusedIndex)
                return;

            _boxes[_focusedIndex].Selected = false;
            _focusedIndex = index;
            _boxes[_focusedIndex].Selected = true;
            Game1.keyboardDispatcher.Subscriber = _boxes[_focusedIndex];
        }

        private void TrySubmit()
        {
            Game1.playSound("coin");
            string compiled = CompileQuestionnaireAnswers(
                _boxes[0].Text, _boxes[1].Text, _boxes[2].Text, _boxes[3].Text);
            Game1.keyboardDispatcher.Subscriber = null;
            Game1.activeClickableMenu = _parentMenu;
            _onSubmit?.Invoke(compiled, BioAiUiPrefs.EnableThinking);
        }

        private void SubmitEmpty()
        {
            for (int i = 0; i < _boxes.Length; i++)
                _boxes[i].SetText(string.Empty);
            Game1.playSound("coin");
            Game1.keyboardDispatcher.Subscriber = null;
            Game1.activeClickableMenu = _parentMenu;
            _onSubmit?.Invoke(string.Empty, BioAiUiPrefs.EnableThinking);
        }

        private void Cancel()
        {
            Game1.playSound("cancel");
            Game1.keyboardDispatcher.Subscriber = null;
            Game1.activeClickableMenu = _parentMenu;
        }

        private static string CompileQuestionnaireAnswers(string q1, string q2, string q3, string q4)
        {
            var sb = new StringBuilder();
            string a1 = q1?.Trim() ?? string.Empty;
            string a2 = q2?.Trim() ?? string.Empty;
            string a3 = q3?.Trim() ?? string.Empty;
            string a4 = q4?.Trim() ?? string.Empty;

            if (!string.IsNullOrEmpty(a1))
                sb.AppendLine($"【明面身份与生活日常】{a1}");
            if (!string.IsNullOrEmpty(a2))
                sb.AppendLine($"【心理矛盾与深层弱点】{a2}");
            if (!string.IsNullOrEmpty(a3))
                sb.AppendLine($"【说话风格与口吻习惯】{a3}");
            if (!string.IsNullOrEmpty(a4))
                sb.AppendLine($"【对外来农夫第一印象】{a4}");

            return sb.ToString().TrimEnd();
        }

        public override void draw(SpriteBatch b)
        {
            int mx = Game1.getMouseX();
            int my = Game1.getMouseY();

            // 1. 全屏半透明遮罩
            b.Draw(Game1.fadeToBlackRect, Game1.graphics.GraphicsDevice.Viewport.Bounds, Color.Black * 0.5f);

            // 2. 外层木框与羊皮纸底板
            IClickableMenu.drawTextureBox(b, xPositionOnScreen - 8, yPositionOnScreen - 8, width + 16, height + 16, Color.White);

            b.Draw(Game1.staminaRect, new Rectangle(xPositionOnScreen, yPositionOnScreen, width, height), new Color(245, 230, 205));
            b.Draw(
                Game1.menuTexture,
                new Rectangle(xPositionOnScreen, yPositionOnScreen, width, height),
                new Rectangle(64, 128, 64, 64),
                new Color(245, 230, 205));

            DrawHeader(b, mx, my);

            // 3. 绘制 2×2 问卷卡片
            for (int i = 0; i < 4; i++)
                DrawQuestionCard(b, _cardRects[i], i, mx, my);

            // 4. ★ 底部动作区域控件（模式胶囊 + 自由发挥 + 取消 + 开始生成）
            string pillLabel = BioAiUiPrefs.EnableThinking ? "🧠 深度思考: 开启" : "⚡ 极速模式: 开启";
            DrawPillButton(b, _thinkingPillRect, pillLabel, BioAiUiPrefs.EnableThinking, mx, my);

            DrawActionButton(b, _aiFreeButtonRect, "★ 留空交给 AI 自由发挥", mx, my, isPrimary: false);
            DrawActionButton(b, _cancelButtonRect, "✕ 取消 (Esc)", mx, my, isDanger: false);
            DrawActionButton(b, _okButtonRect, "✔ 开始生成身份档案", mx, my, isPrimary: true);

            // 5. 悬停气泡处理
            if (_thinkingPillRect.Contains(mx, my))
            {
                _hoverText = BioAiUiPrefs.EnableThinking
                    ? "【深度思考模式：已开启】\n模型将进行更严密的多阶段长链推理，起号身份与矛盾内核更深刻，响应时间稍长。\n设置后后续步骤（言行/对白/阶梯/心智）均默认保持此模式。"
                    : "【极速生成模式：已开启】\n直接生成对白与规则，响应极快，轻快高效。\n设置后后续步骤均默认保持此模式。";
            }
            else if (_aiFreeButtonRect.Contains(mx, my))
                _hoverText = "【AI 自由起号】\n清空问卷并直接依据星露谷原版设定，让 AI 全权构思人物背景与心理矛盾。";
            else if (_okButtonRect.Contains(mx, my))
                _hoverText = "【开始推演】\n整合当前填写的设定支柱并作为上下文，启动 AI 生成管线。";

            if (!string.IsNullOrEmpty(_hoverText))
                DrawHoverTextCustom(b, _hoverText);

            drawMouse(b);
        }

        private void DrawHeader(SpriteBatch b, int mx, int my)
        {
            int headX = xPositionOnScreen + ContentPadding;
            int headY = yPositionOnScreen + 14;

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
                string avatarFallback = string.IsNullOrEmpty(_npcName) ? "?" : _npcName.Substring(0, 1);
                var fsz = CustomFontManager.MeasureStringBold(avatarFallback, TitleFontSize);
                CustomFontManager.DrawStringBold(b, avatarFallback,
                    new Vector2(portraitRect.X + (pSize - fsz.X) / 2f, portraitRect.Y + (pSize - fsz.Y) / 2f - 1),
                    BioEditorMenu.TextMuted, TitleFontSize);
            }

            string disp = Game1.getCharacterFromName(_npcName)?.displayName ?? _npcName;
            string title = $"引导式起号 · 角色种子问卷（{disp}）";
            CustomFontManager.DrawStringBold(b, title, new Vector2(headX + pSize + 12, headY + 2), BioEditorMenu.TextPrimary, TitleFontSize);

            const string subTitle = "构思核心特征引导 AI 具象化人设；亦可留空部分项交给 AI 基于原版脉络自由发挥。";
            CustomFontManager.DrawString(b, subTitle, new Vector2(headX + pSize + 14, headY + 30), BioEditorMenu.TextMuted, TipFontSize);

            int sepY = yPositionOnScreen + HeaderH + 4;
            b.Draw(Game1.staminaRect, new Rectangle(xPositionOnScreen + ContentPadding, sepY, width - ContentPadding * 2, 2), Color.Gray * 0.35f);

            UiHelper.UpdateButtonScale(ref _closeButtonHoverScale, _closeButton, mx, my);
            _closeButton.scale = CloseButtonBaseScale * _closeButtonHoverScale;
            _closeButton.draw(b);
        }

        private void DrawQuestionCard(SpriteBatch b, Rectangle rect, int index, int mx, int my)
        {
            bool focused = index == _focusedIndex;
            bool isHover = rect.Contains(mx, my);
            bool hasText = !string.IsNullOrWhiteSpace(_boxes[index].Text);

            // 1. 底层微阴影
            b.Draw(Game1.staminaRect, new Rectangle(rect.X + 2, rect.Y + 2, rect.Width, rect.Height), Color.Black * 0.08f);

            // 2. 卡片底衬颜色自适应
            Color bg = focused ? new Color(255, 252, 245)
                     : isHover ? new Color(252, 248, 238)
                     : new Color(246, 238, 224);

            b.Draw(Game1.staminaRect, new Rectangle(rect.X + 1, rect.Y + 1, rect.Width - 2, rect.Height - 2), bg);

            // 3. 卡片外框：暖金橙色 (432 纹理)
            Color borderCol = focused ? new Color(223, 122, 4) * 0.85f
                            : isHover ? new Color(210, 160, 60)
                            : new Color(215, 180, 135);

            IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(432, 439, 9, 9),
                rect.X, rect.Y, rect.Width, rect.Height, borderCol, 2f, false);

            if (focused)
            {
                b.Draw(Game1.staminaRect, new Rectangle(rect.X + 2, rect.Y + 4, 4, rect.Height - 8), new Color(223, 122, 4));
            }

            // 4. 标题与状态标
            float headerX = rect.X + (focused ? 16 : 14);
            CustomFontManager.DrawString(
                b, QuestionHeaders[index],
                new Vector2(headerX, rect.Y + 10),
                focused ? BioEditorMenu.TextPrimary : BioEditorMenu.TextSecondary,
                SectionHeaderSize);

            if (hasText)
            {
                const string badge = "✔ 已填写";
                var bsz = CustomFontManager.MeasureString(badge, TipFontSize);
                CustomFontManager.DrawString(b, badge, new Vector2(rect.Right - bsz.X - 14, rect.Y + 12), BioEditorMenu.TextSuccess, TipFontSize);
            }

            // 5. 文本框实际绘制（由 DialogueTextInputBox 渲染纯正暖橘红木外框与对齐的占位符）
            _boxes[index].Draw(b);
        }

        private static bool IsLeftMouseDown()
        {
            try { return Game1.input.GetMouseState().LeftButton == ButtonState.Pressed; }
            catch { return false; }
        }

        private static void DrawPillButton(SpriteBatch b, Rectangle rect, string label, bool isActive, int mx, int my)
        {
            bool isHover = rect.Contains(mx, my);
            bool isPressed = isHover && IsLeftMouseDown();

            Color bg = isActive
                ? (isHover ? Color.Gold : new Color(255, 220, 130))
                : (isHover ? new Color(255, 235, 205) : Color.White);

            int pressOffset = isPressed ? 1 : 0;
            if (isPressed) bg = Color.Lerp(bg, Color.Black, 0.14f);

            b.Draw(Game1.staminaRect, new Rectangle(rect.X + 1 + pressOffset, rect.Y + 1 + pressOffset, rect.Width - 2, rect.Height - 2), bg);
            IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(432, 439, 9, 9),
                rect.X + pressOffset, rect.Y + pressOffset, rect.Width, rect.Height,
                isActive ? new Color(200, 150, 50) : Color.Wheat, 2f, false);

            var sz = CustomFontManager.MeasureString(label, TipFontSize);
            Vector2 textPos = new Vector2(
                rect.X + pressOffset + (rect.Width - sz.X) / 2f,
                rect.Y + pressOffset + (rect.Height - sz.Y) / 2f);

            CustomFontManager.DrawString(b, label, textPos, BioEditorMenu.TextPrimary, TipFontSize);
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