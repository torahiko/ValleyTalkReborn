using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewValley;
using StardewValley.Menus;
using System;
using System.Collections.Generic;
using System.Text;

namespace ValleytalkReborn
{
    /// <summary>
    /// "引导式起号"四问问卷屏（2×2 网格）。
    /// 提交后回调 (compiledDemand, enableThinking) 进入身份起号生成管线；取消路径零污染（不清旗标、不回调）。
    /// 无向导旗标持有——旗标仅在下游 ExecuteWizardIdentityGeneration 内置位。
    /// </summary>
    internal sealed class BioWizardQuestionnaireDialog : IClickableMenu
    {
        private const int MenuWidth = 880;
        private const int MenuHeight = 600;

        private const float TitleSize = 22f;
        private const float QuestionHeaderSize = 17f;
        private const float PlaceholderSize = 15f;
        private const float ButtonFontSize = 18f;

        private const int CardWidth = 395;
        private const int CardHeight = 190;
        private const int CardGapX = 16;
        private const int CardGapY = 16;
        private const int GridOriginY = 78;

        private const int ButtonHeight = 44;
        private const int OkButtonWidth = 200;
        private const int AiButtonWidth = 230;
        private const int CancelButtonWidth = 130;

        private readonly string _npcName;
        private readonly new IClickableMenu _parentMenu;
        private readonly Action<string, bool> _onSubmit;

        private readonly DialogueTextInputBox[] _boxes;
        private readonly string[] _placeholders;

        private Rectangle _aiFreeButtonRect;
        private Rectangle _cancelButtonRect;
        private Rectangle _okButtonRect;

        private int _focusedIndex;

#pragma warning disable CS0649 // 悬停气泡文本：draw 中条件性赋值，空值即不绘制
        private string _hoverText;
#pragma warning restore CS0649

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
                (Game1.uiViewport.Width - MenuWidth) / 2,
                (Game1.uiViewport.Height - MenuHeight) / 2,
                MenuWidth,
                MenuHeight)
        {
            _npcName = npcName;
            _parentMenu = parentMenu;
            _onSubmit = onSubmit;

            _boxes = new DialogueTextInputBox[4];
            _placeholders = new string[4];

            int gridOriginX = xPositionOnScreen + (MenuWidth - (CardWidth * 2 + CardGapX)) / 2;

            for (int i = 0; i < 4; i++)
            {
                int col = i % 2;
                int row = i / 2;
                int cx = gridOriginX + col * (CardWidth + CardGapX);
                int cy = yPositionOnScreen + GridOriginY + row * (CardHeight + CardGapY);

                _boxes[i] = new DialogueTextInputBox(300)
                {
                    Position = new Vector2(cx + 14, cy + 44),
                    Extent = new Vector2(CardWidth - 28, CardHeight - 60),
                    CustomFontSize = 18f,
                    UseCustomFont = true,
                    AllowNewlines = false,
                    Selected = i == 0
                };

                _placeholders[i] = QuestionPlaceholders[i];
            }

            _focusedIndex = 0;

            int btnY = yPositionOnScreen + MenuHeight - ButtonHeight - 24;
            _okButtonRect = new Rectangle(xPositionOnScreen + MenuWidth - OkButtonWidth - 24, btnY, OkButtonWidth, ButtonHeight);
            _cancelButtonRect = new Rectangle(_okButtonRect.X - CancelButtonWidth - 14, btnY, CancelButtonWidth, ButtonHeight);
            _aiFreeButtonRect = new Rectangle(_cancelButtonRect.X - AiButtonWidth - 14, btnY, AiButtonWidth, ButtonHeight);

            Game1.keyboardDispatcher.Subscriber = _boxes[0];
        }

        public override void update(GameTime time)
        {
            base.update(time);
            for (int i = 0; i < _boxes.Length; i++)
                _boxes[i].Update(time);
        }

        public override void gameWindowSizeChanged(Rectangle oldBounds, Rectangle newBounds)
        {
            width = MenuWidth;
            height = MenuHeight;
            xPositionOnScreen = (Game1.uiViewport.Width - width) / 2;
            yPositionOnScreen = (Game1.uiViewport.Height - height) / 2;

            int gridOriginX = xPositionOnScreen + (MenuWidth - (CardWidth * 2 + CardGapX)) / 2;
            for (int i = 0; i < 4; i++)
            {
                int col = i % 2;
                int row = i / 2;
                int cx = gridOriginX + col * (CardWidth + CardGapX);
                int cy = yPositionOnScreen + GridOriginY + row * (CardHeight + CardGapY);
                _boxes[i].Position = new Vector2(cx + 14, cy + 44);
            }

            int btnY = yPositionOnScreen + MenuHeight - ButtonHeight - 24;
            _okButtonRect.X = xPositionOnScreen + MenuWidth - OkButtonWidth - 24;
            _okButtonRect.Y = btnY;
            _cancelButtonRect.X = _okButtonRect.X - CancelButtonWidth - 14;
            _cancelButtonRect.Y = btnY;
            _aiFreeButtonRect.X = _cancelButtonRect.X - AiButtonWidth - 14;
            _aiFreeButtonRect.Y = btnY;
        }

        public override void receiveLeftClick(int x, int y, bool playSound = true)
        {
            // 卡片区命中 => 切换焦点
            for (int i = 0; i < _boxes.Length; i++)
            {
                if (_boxes[i].ContainsPoint(x, y))
                {
                    SetFocus(i);
                    return;
                }
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
                return;
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
                    _boxes[_focusedIndex].RecieveSpecialInput(key);
                return;
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
                _boxes[i].SetText("");
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

            string a1 = q1?.Trim() ?? "";
            string a2 = q2?.Trim() ?? "";
            string a3 = q3?.Trim() ?? "";
            string a4 = q4?.Trim() ?? "";

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
            b.Draw(Game1.fadeToBlackRect, Game1.graphics.GraphicsDevice.Viewport.Bounds, Color.Black * 0.45f);
            Game1.drawDialogueBox(xPositionOnScreen, yPositionOnScreen, width, height, false, true);

            int mx = Game1.getMouseX();
            int my = Game1.getMouseY();

            // 标题
            string title = $"引导式起号 · 角色种子问卷（{_npcName}）";
            CustomFontManager.DrawStringBold(
                b, title,
                new Vector2(xPositionOnScreen + 28, yPositionOnScreen + 22),
                BioEditorMenu.TextPrimary, TitleSize);

            // 2×2 卡片
            int gridOriginX = xPositionOnScreen + (MenuWidth - (CardWidth * 2 + CardGapX)) / 2;
            for (int i = 0; i < 4; i++)
            {
                int col = i % 2;
                int row = i / 2;
                int cx = gridOriginX + col * (CardWidth + CardGapX);
                int cy = yPositionOnScreen + GridOriginY + row * (CardHeight + CardGapY);

                DrawCard(b, cx, cy, i, mx, my);
            }

            // 按钮（★ 金色 / ✕ 灰 / ✔ 金色主按钮）
            DrawButton(b, _aiFreeButtonRect, "★ 完全交给 AI 自由发挥", mx, my, isPrimary: false);
            DrawButton(b, _cancelButtonRect, "✕ 取消", mx, my, isDanger: false);
            DrawButton(b, _okButtonRect, "✔ 开始生成身份档案", mx, my, isPrimary: true);

            if (!string.IsNullOrEmpty(_hoverText))
                DrawHover(b, _hoverText);

            drawMouse(b);
        }

        private void DrawCard(SpriteBatch b, int x, int y, int index, int mx, int my)
        {
            bool focused = index == _focusedIndex;

            // 底衬
            b.Draw(Game1.staminaRect, new Rectangle(x + 3, y + 3, CardWidth, CardHeight), Color.Black * 0.07f);
            b.Draw(Game1.staminaRect, new Rectangle(x, y, CardWidth, CardHeight),
                focused ? new Color(255, 250, 235) : new Color(250, 242, 225));

            // 1px 边框
            IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(240, 240, 4, 4),
                x, y, CardWidth, CardHeight,
                focused ? new Color(210, 160, 60) : new Color(210, 195, 165), 2f, false);

            // 加粗小标
            CustomFontManager.DrawStringBold(
                b, QuestionHeaders[index],
                new Vector2(x + 12, y + 10),
                BioEditorMenu.TextPrimary, QuestionHeaderSize);

            // 文本框
            _boxes[index].Draw(b);

            // 占位符（框空时绘制）
            if (string.IsNullOrEmpty(_boxes[index].Text))
            {
                CustomFontManager.DrawString(
                    b, _placeholders[index],
                    new Vector2(x + 14 + 14, y + 44 + 12),
                    BioEditorMenu.TextMuted, PlaceholderSize);
            }
        }

        private void DrawButton(SpriteBatch b, Rectangle rect, string label, int mx, int my,
            bool isDanger = false, bool isPrimary = false, bool isEnabled = true)
        {
            bool isHover = isEnabled && rect.Contains(mx, my);
            bool isPressed = isHover && Game1.input.GetMouseState().LeftButton == ButtonState.Pressed;

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

            var sz = CustomFontManager.MeasureStringBold(label, ButtonFontSize);
            CustomFontManager.DrawStringBold(b, label,
                new Vector2(
                    rect.X + pressOffset + (rect.Width - sz.X) / 2f,
                    rect.Y + pressOffset + (rect.Height - sz.Y) / 2f),
                textCol, ButtonFontSize);
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
