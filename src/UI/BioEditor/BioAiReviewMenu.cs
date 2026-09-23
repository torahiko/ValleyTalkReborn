#nullable enable

using System;
using System.Collections.Concurrent;
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
    /// AI 流式生成与人设审阅确认菜单：
    /// 遵循 BioEditorMenu 视觉与动效体系 —— 羊皮纸无缝底板、按压下沉动效、纯正暖橘红木框，
    /// 仅主标题与动作按钮使用 Bold 字体，状态说明与审阅正文回归 Medium 字体。
    /// </summary>
    internal sealed class BioAiReviewMenu : IClickableMenu
    {
        private enum ReviewPhase
        {
            Thinking,
            Streaming,
            Settled
        }

        // ── 尺寸与布局常量 ──
        private const int MenuWidth = 980;
        private const int MenuHeight = 620;
        private const int ContentPadding = 24;
        private const int HeaderH = 68;
        private const int FooterH = 56;

        // 统一整数字号（完全继承 CustomFontManager 规范）
        private const float TitleFontSize = CustomFontManager.SizeTitle;       // 24f Bold (顶栏标题)
        private const float ButtonFontSize = CustomFontManager.SizeRegular;    // 18f Bold (底部动作按钮)
        private const float ContentFontSize = CustomFontManager.SizeRegular;   // 18f Medium (输入框审阅正文)
        private const float TipFontSize = CustomFontManager.SizeSmall;         // 15f Medium (副说明与气泡)

        private readonly string _sectionTitle;
        private readonly new IClickableMenu _parentMenu;
        private readonly Func<string, bool> _onAccepted;
        private readonly Action? _onCancelled;
        private readonly DialogueTextInputBox _reviewTextBox;

        // 顶栏关闭按钮
        private ClickableTextureComponent _closeButton;
        private float _closeButtonHoverScale;
        private const float CloseButtonBaseScale = 3f;

        // 底部动作按钮
        private Rectangle _stopButtonRect;
        private Rectangle _copyButtonRect;
        private Rectangle _cancelButtonRect;
        private Rectangle _acceptButtonRect;

        private ConcurrentQueue<string>? _streamQueue;
        private ReviewPhase _phase = ReviewPhase.Thinking;
        private string _statusSubtitle = "正在连接大模型并构建思考链路…";
        private string? _hoverText;

        public BioAiReviewMenu(string sectionTitle, IClickableMenu parentMenu, Func<string, bool> onAccepted, Action? onCancelled = null)
            : base(
                (Game1.uiViewport.Width - Math.Clamp(Game1.uiViewport.Width - 100, 880, MenuWidth)) / 2,
                (Game1.uiViewport.Height - Math.Clamp(Game1.uiViewport.Height - 80, 560, MenuHeight)) / 2,
                Math.Clamp(Game1.uiViewport.Width - 100, 880, MenuWidth),
                Math.Clamp(Game1.uiViewport.Height - 80, 560, MenuHeight),
                showUpperRightCloseButton: false)
        {
            _sectionTitle = sectionTitle ?? string.Empty;
            _parentMenu = parentMenu;
            _onAccepted = onAccepted;
            _onCancelled = onCancelled;

            _reviewTextBox = new DialogueTextInputBox(6000)
            {
                AllowNewlines = true,
                UseCustomFont = true,
                CustomFontSize = ContentFontSize,
                Selected = false,
                DrawFrame = true, // ★ 启用原生暖金/暖橘红木外框，消除 403 灰凹槽
                PlaceholderText = "AI 正在构思生成方案，即将在此流式呈现…",
                PlaceholderColor = new Color(175, 145, 115) // ★ 温暖金木色提示词
            };

            _closeButton = new ClickableTextureComponent(
                new Rectangle(xPositionOnScreen + width - 50, yPositionOnScreen + 16, 36, 36),
                Game1.mouseCursors, new Rectangle(337, 494, 12, 12), CloseButtonBaseScale);

            Layout();
        }

        public string ReviewText => _reviewTextBox.Text;

        public void BeginStreaming(ConcurrentQueue<string> tokenQueue)
        {
            _streamQueue = tokenQueue;
        }

        public void OnStreamSettled(BioAiResult result)
        {
            if (!ReferenceEquals(Game1.activeClickableMenu, this))
                return;

            if (_phase == ReviewPhase.Settled)
                return;

            PumpTokens();

            switch (result.Kind)
            {
                case BioAiResultKind.Success:
                    if (_reviewTextBox.Text.StartsWith("```", StringComparison.Ordinal))
                        _reviewTextBox.SetText(result.Text);

                    EnterSettled("生成完成：您可以直接在下方自由修改，满意后点击「应用生成内容」");
                    Game1.playSound("shiny4");
                    break;

                case BioAiResultKind.Failure:
                case BioAiResultKind.TimedOut:
                    if (string.IsNullOrWhiteSpace(_reviewTextBox.Text))
                    {
                        CloseToParent();
                        return;
                    }

                    EnterSettled("生成中断：已保留当前生成的内容，可编辑后应用或放弃");
                    break;

                case BioAiResultKind.Cancelled:
                    if (string.IsNullOrWhiteSpace(_reviewTextBox.Text))
                    {
                        CloseToParent();
                        return;
                    }

                    EnterSettled("已停止生成：已保留前半段内容，可编辑后应用或放弃");
                    break;
            }
        }

        private void EnterSettled(string subtitle)
        {
            _reviewTextBox.Selected = true;
            Game1.keyboardDispatcher.Subscriber = _reviewTextBox;
            _phase = ReviewPhase.Settled;
            _statusSubtitle = subtitle;
        }

        private void CloseToParent()
        {
            if (ReferenceEquals(Game1.keyboardDispatcher.Subscriber, _reviewTextBox))
                Game1.keyboardDispatcher.Subscriber = null;

            Game1.activeClickableMenu = _parentMenu;
        }

        public override void update(GameTime time)
        {
            base.update(time);
            _hoverText = null;
            _reviewTextBox.Update(time);
            PumpTokens();
        }

        private void PumpTokens()
        {
            if (_phase == ReviewPhase.Settled || _streamQueue == null || _streamQueue.IsEmpty)
                return;

            var batch = new StringBuilder();
            while (_streamQueue.TryDequeue(out string? token)) // ★ string? 消除 CS8600
            {
                if (token != null)
                    batch.Append(token);
            }

            if (batch.Length == 0)
                return;

            _reviewTextBox.AppendStreamingText(batch.ToString());
            if (_phase == ReviewPhase.Thinking)
            {
                _phase = ReviewPhase.Streaming;
                _statusSubtitle = "正在实时接收并流式渲染生成内容…";
            }
        }

        public override void gameWindowSizeChanged(Rectangle oldBounds, Rectangle newBounds)
        {
            width = Math.Clamp(Game1.uiViewport.Width - 100, 880, MenuWidth);
            height = Math.Clamp(Game1.uiViewport.Height - 80, 560, MenuHeight);
            xPositionOnScreen = (Game1.uiViewport.Width - width) / 2;
            yPositionOnScreen = (Game1.uiViewport.Height - height) / 2;
            Layout();
        }

        private void Layout()
        {
            _closeButton.bounds = new Rectangle(xPositionOnScreen + width - 50, yPositionOnScreen + 16, 36, 36);

            int contentLeft = xPositionOnScreen + ContentPadding;
            int contentW = width - ContentPadding * 2;
            int bodyTop = yPositionOnScreen + HeaderH + 12;

            int footerY = yPositionOnScreen + height - FooterH + 10;
            const int btnH = 38;

            int availTextH = footerY - 14 - bodyTop;

            // ★ 文本框直接铺满内容区，由 DrawFrame 绘制原生暖金/暖橘边框
            _reviewTextBox.Position = new Vector2(contentLeft, bodyTop);
            _reviewTextBox.Extent = new Vector2(contentW, availTextH);
            _reviewTextBox.InvalidateLayout();

            // 底部按钮
            const int stopBtnW = 180;
            const int copyBtnW = 110;
            const int cancelBtnW = 130;
            const int acceptBtnW = 190;

            _stopButtonRect = new Rectangle(xPositionOnScreen + width - ContentPadding - stopBtnW, footerY, stopBtnW, btnH);

            _copyButtonRect = new Rectangle(contentLeft, footerY, copyBtnW, btnH);
            _acceptButtonRect = new Rectangle(xPositionOnScreen + width - ContentPadding - acceptBtnW, footerY, acceptBtnW, btnH);
            _cancelButtonRect = new Rectangle(_acceptButtonRect.X - cancelBtnW - 12, footerY, cancelBtnW, btnH);
        }

        public override void leftClickHeld(int x, int y)
        {
            base.leftClickHeld(x, y);
            _reviewTextBox.LeftClickHeld(x, y);
        }

        public override void releaseLeftClick(int x, int y)
        {
            base.releaseLeftClick(x, y);
            _reviewTextBox.ReleaseLeftClick(x, y);
        }

        public override void receiveScrollWheelAction(int direction)
        {
            base.receiveScrollWheelAction(direction);
            _reviewTextBox.ReceiveScrollWheel(direction);
        }

        public override void receiveLeftClick(int x, int y, bool playSound = true)
        {
            if (_closeButton.containsPoint(x, y))
            {
                Game1.playSound("bigDeSelect");
                HandleExitOrCancel();
                return;
            }

            if (_phase != ReviewPhase.Settled)
            {
                if (_stopButtonRect.Contains(x, y))
                {
                    Game1.playSound("cancel");
                    BioAiRunner.CancelCurrentTask();
                }
                return;
            }

            if (_copyButtonRect.Contains(x, y))
            {
                CopyDraftToClipboard();
                return;
            }

            if (_cancelButtonRect.Contains(x, y))
            {
                Game1.playSound("cancel");
                CloseToParent();
                try { _onCancelled?.Invoke(); }
                catch (Exception ex)
                {
                    ModEntry.SMonitor?.Log($"[BioAiReviewMenu] Cancel callback failed: {ex}", StardewModdingAPI.LogLevel.Error);
                }
                return;
            }

            if (_acceptButtonRect.Contains(x, y))
            {
                string confirmedText = _reviewTextBox.Text;
                bool applied;
                try
                {
                    applied = _onAccepted?.Invoke(confirmedText) ?? true;
                }
                catch (Exception ex)
                {
                    ModEntry.SMonitor?.Log($"[BioAiReviewMenu] Apply callback failed: {ex}", StardewModdingAPI.LogLevel.Error);
                    applied = false;
                }

                if (applied)
                {
                    Game1.playSound("coin");
                    CloseToParent();
                }
                else
                {
                    Game1.playSound("cancel");
                    _statusSubtitle = "内容格式有误：请在文本框中修正后再次点击「应用」";
                }
                return;
            }

            if (_reviewTextBox.ReceiveLeftClick(x, y))
            {
                Game1.keyboardDispatcher.Subscriber = _reviewTextBox;
            }
        }

        public override void receiveKeyPress(Keys key)
        {
            if (key == Keys.Escape)
            {
                HandleExitOrCancel();
                return;
            }

            if (Game1.options.doesInputListContain(Game1.options.menuButton, key))
                return;

            if (_phase != ReviewPhase.Settled)
                return;

            if (DialogueTextInputBox.IsControlKeyDown())
            {
                if (key == Keys.A || key == Keys.C || key == Keys.X || key == Keys.Z || key == Keys.V)
                {
                    _reviewTextBox.RecieveSpecialInput(key);
                    return;
                }
            }

            if (key == Keys.Left || key == Keys.Right || key == Keys.Up || key == Keys.Down ||
                key == Keys.Home || key == Keys.End || key == Keys.Delete || key == Keys.Back ||
                key == Keys.Enter)
            {
                _reviewTextBox.RecieveSpecialInput(key);
            }
        }

        private void HandleExitOrCancel()
        {
            if (_phase != ReviewPhase.Settled)
            {
                Game1.playSound("cancel");
                BioAiRunner.CancelCurrentTask();
            }
            else
            {
                CloseToParent();
                try { _onCancelled?.Invoke(); }
                catch (Exception ex)
                {
                    ModEntry.SMonitor?.Log($"[BioAiReviewMenu] Cancel callback failed: {ex}", StardewModdingAPI.LogLevel.Error);
                }
            }
        }

        private void CopyDraftToClipboard()
        {
            try
            {
                TextCopy.ClipboardService.SetText(_reviewTextBox.Text ?? string.Empty);
                Game1.playSound("coin");
                Game1.addHUDMessage(new HUDMessage("已复制审阅文本至剪贴板", HUDMessage.newQuest_type));
            }
            catch (Exception ex)
            {
                Game1.playSound("cancel");
                Game1.addHUDMessage(new HUDMessage($"复制失败: {ex.Message}", HUDMessage.error_type));
            }
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

            // 3. ★ 直接绘制审阅文本框（彻底删除 403 灰凹槽，自带纯正暖橘红木外框）
            _reviewTextBox.Draw(b);

            // 4. 底部动作按钮
            if (_phase != ReviewPhase.Settled)
            {
                DrawActionButton(b, _stopButtonRect, "■ 停止生成 (Esc)", mx, my, isDanger: true);
                if (_stopButtonRect.Contains(mx, my))
                    _hoverText = "【中断生成】\n立即停止大模型推理，已生成的文本片段将被保留供编辑。";
            }
            else
            {
                DrawActionButton(b, _copyButtonRect, "📋 复制文本", mx, my, isPrimary: false);
                DrawActionButton(b, _cancelButtonRect, "✕ 放弃 (Esc)", mx, my, isDanger: false);
                DrawActionButton(b, _acceptButtonRect, "✔ 应用生成内容", mx, my, isPrimary: true);

                if (_copyButtonRect.Contains(mx, my))
                    _hoverText = "【复制文本】\n将当前审阅框中的全部内容复制到系统剪贴板。";
                else if (_cancelButtonRect.Contains(mx, my))
                    _hoverText = "【放弃修改】\n关闭当前审阅窗口，不应用本次 AI 生成的任何内容。";
                else if (_acceptButtonRect.Contains(mx, my))
                    _hoverText = "【应用并保存】\n将当前文本写入人设档案的对应项，生效改动。";
            }

            // 5. 悬停气泡与光标
            if (!string.IsNullOrEmpty(_hoverText))
                DrawHoverTextCustom(b, _hoverText);

            drawMouse(b);
        }

        private void DrawHeader(SpriteBatch b, int mx, int my)
        {
            int headX = xPositionOnScreen + ContentPadding;
            int headY = yPositionOnScreen + 16;

            // ★ 主标题：唯一使用 Bold，SizeTitle (24f)
            string title = $"AI 方案审阅 · {_sectionTitle}";
            CustomFontManager.DrawStringBold(b, title, new Vector2(headX, headY), BioEditorMenu.TextPrimary, TitleFontSize);

            // ★ 状态副说明：Medium 字体，SizeSmall (15f)，色彩语义化感知
            Color subColor = _phase == ReviewPhase.Thinking ? BioEditorMenu.TextWarning
                           : _phase == ReviewPhase.Streaming ? BioEditorMenu.TextAccent
                           : BioEditorMenu.TextSuccess;

            CustomFontManager.DrawString(b, _statusSubtitle, new Vector2(headX + 2, headY + 28), subColor, TipFontSize);

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
            try
            {
                return Game1.input.GetMouseState().LeftButton == ButtonState.Pressed;
            }
            catch
            {
                return false;
            }
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