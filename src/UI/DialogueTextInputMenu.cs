using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewValley;
using StardewValley.Menus;
using System;

namespace ValleytalkReborn
{
    public class DialogueTextInputMenu : IClickableMenu
    {
        public delegate void TextSubmittedDelegate(string input);

        // 统一字号阶梯（调整为清晰的偶数字号，避免字体光栅化发虚）
        private const float TitleFontSize = 30f;       // Bold 粗体大标题（沉稳有力）
        private const float SubtitleFontSize = 14f;    // 顶部小徽标/副标题字号
        private const float TextBoxFontSize = 26f;     // 文本框正文字号（偶数字号，字形锐利清晰不发虚）
        private const float CounterFontSize = 20f;     // 文本框右下角指示器字号
        private const float InstructionFontSize = 18f; // 底部操作提示字号

        private readonly string _title;
        private readonly DialogueTextInputBox _inputTextBox;
        private readonly ClickableTextureComponent _okButton;
        private readonly ClickableTextureComponent _cancelButton;
        private readonly ClickableTextureComponent _clearHistory;
        private readonly ClickableTextureComponent _viewHistory;
        private readonly TextSubmittedDelegate _onTextSubmitted;
        private readonly string _npcName;

        private const int MenuWidth = 1200;
        private const int MenuHeight = 640;
        
        // 布局参数：将 TopPadding 提升至 116，彻底避开 drawDialogueBox 顶部的厚木框，标题与副标题顺畅下沉
        private const int TopPadding = 116;
        private const int HeaderHeight = 64;
        private const int ButtonSize = 64;
        private const int Margin = 24;

        // 响应式尺寸
        private int _currentMenuWidth;
        private int _currentMenuHeight;
        private Vector2 _menuPosition;
        private Rectangle _menuBounds;

        private IClickableMenu _menuToRestore;

        // 按钮悬浮缩放动画
        private float _okButtonHoverScale = 1f;
        private float _cancelButtonHoverScale = 1f;
        private float _clearHistoryHoverScale = 1f;
        private float _viewHistoryHoverScale = 1f;

        private readonly float _okButtonBaseScale = 1f;
        private readonly float _cancelButtonBaseScale = 1f;
        private readonly float _clearHistoryBaseScale = 3f;
        private readonly float _viewHistoryBaseScale = 3.5f;

        public Rectangle MenuBounds => _menuBounds;

        public DialogueTextInputMenu(string title, TextSubmittedDelegate callback, NPC currentNpc)
        {
            _npcName = currentNpc?.Name ?? "";
            _title = title ?? (string.IsNullOrEmpty(_npcName)
                ? I18n.DialogueInput.DefaultTitle()
                : I18n.DialogueInput.DefaultTitleWithNpc(_npcName));
            _onTextSubmitted = callback;

            _inputTextBox = new DialogueTextInputBox(300)
            {
                AllowNewlines = false,
                UseCustomFont = true,
                CustomFontSize = TextBoxFontSize, // 26f 整数锐利字号
                CounterFontSize = CounterFontSize,
                DrawFrame = true,
                TextColor = Game1.textColor,
                Selected = true
            };

            _inputTextBox.OnSubmit += sender =>
            {
                Game1.playSound("coin");
                Submit(sender.Text);
            };

            _okButton = new ClickableTextureComponent(
                Rectangle.Empty,
                Game1.mouseCursors,
                Game1.getSourceRectForStandardTileSheet(Game1.mouseCursors, 46, -1, -1),
                1f
            );

            _cancelButton = new ClickableTextureComponent(
                Rectangle.Empty,
                Game1.mouseCursors,
                Game1.getSourceRectForStandardTileSheet(Game1.mouseCursors, 47, -1, -1),
                1f
            );

            var springTownTilesheet = Game1.content.Load<Texture2D>("Maps\\spring_town");

            _clearHistory = new ClickableTextureComponent(
                Rectangle.Empty,
                springTownTilesheet,
                new Rectangle(224, 26, 16, 22),
                3f
            )
            {
                hoverText = I18n.DialogueInput.ClearHistoryHover(_npcName)
            };

            // 蓝色书籍切片
            _viewHistory = new ClickableTextureComponent(
                Rectangle.Empty,
                Game1.objectSpriteSheet,
                Game1.getSourceRectForStandardTileSheet(Game1.objectSpriteSheet, 102, 16, 16),
                3.5f
            )
            {
                hoverText = I18n.DialogueInput.ViewHistoryHover(_npcName)
            };

            Recenter();
            RestoreFocus();
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
            _onTextSubmitted?.Invoke(text?.TrimEnd('\r', '\n') ?? "");
        }

        public void Recenter()
        {
            _currentMenuWidth = Math.Clamp(Game1.uiViewport.Width - 64, 800, MenuWidth);
            _currentMenuHeight = Math.Clamp(Game1.uiViewport.Height - 64, 540, MenuHeight);

            // 严格对齐屏幕物理像素中心
            xPositionOnScreen = (Game1.uiViewport.Width - _currentMenuWidth) / 2;
            yPositionOnScreen = (Game1.uiViewport.Height - _currentMenuHeight) / 2;

            _menuPosition = new Vector2(xPositionOnScreen, yPositionOnScreen);
            _menuBounds = new Rectangle(xPositionOnScreen, yPositionOnScreen, _currentMenuWidth, _currentMenuHeight);
            width = _currentMenuWidth;
            height = _currentMenuHeight;

            // 1. 文本框顶部 Y 坐标（完全顺延避开下移后的标题区）
            int contentTopY = yPositionOnScreen + TopPadding + HeaderHeight + 4;
            float textBoxWidth = _currentMenuWidth - 4 * Margin;

            // 2. 底部按钮 Y 坐标
            int bottomButtonsY = yPositionOnScreen + _currentMenuHeight - Margin - ButtonSize - 13;

            // 3. 底部提示语字高
            int instructionH = (int)Math.Ceiling(CustomFontManager.MeasureString("A", InstructionFontSize).Y);

            // 4. 文本框纵向完全拉伸
            int textBoxHeight = bottomButtonsY - 12 - instructionH - 10 - contentTopY;

            _inputTextBox.Position = new Vector2(xPositionOnScreen + Margin * 2, contentTopY);
            _inputTextBox.Extent = new Vector2(textBoxWidth, Math.Max(120, textBoxHeight));
            _inputTextBox.InvalidateLayout();

            // 底部操作按钮布局
            _okButton.bounds = new Rectangle(
                xPositionOnScreen + _currentMenuWidth - 2 * Margin - ButtonSize,
                bottomButtonsY,
                ButtonSize,
                ButtonSize
            );

            _cancelButton.bounds = new Rectangle(
                _okButton.bounds.X - Margin - ButtonSize,
                bottomButtonsY,
                ButtonSize,
                ButtonSize
            );

            _clearHistory.bounds = new Rectangle(
                xPositionOnScreen + 2 * Margin,
                bottomButtonsY,
                ButtonSize,
                ButtonSize
            );

            // 蓝色书与垃圾桶视觉中心线对齐
            _viewHistory.bounds = new Rectangle(
                _clearHistory.bounds.X + ButtonSize + Margin,
                bottomButtonsY + 5,
                ButtonSize,
                ButtonSize
            );
        }

        public override void gameWindowSizeChanged(Rectangle oldBounds, Rectangle newBounds)
        {
            base.gameWindowSizeChanged(oldBounds, newBounds);
            Recenter();
        }

        public override void draw(SpriteBatch spriteBatch)
        {
            _inputTextBox.Update(Game1.currentGameTime);

            spriteBatch.Draw(
                Game1.fadeToBlackRect,
                Game1.graphics.GraphicsDevice.Viewport.Bounds,
                Color.Black * 0.4f
            );

            // 星露谷原版主菜单大底框
            Game1.drawDialogueBox(
                _menuBounds.X,
                _menuBounds.Y,
                _menuBounds.Width,
                _menuBounds.Height,
                false,
                true
            );

            // 1. 顶部小徽标/副标题（TALKING WITH XXXX）向下平移，完全脱离木框遮挡
            string displaySubtitle = !string.IsNullOrEmpty(_npcName) ? $"TALKING WITH {_npcName.ToUpper()}" : "DIALOGUE INPUT";
            var subSize = CustomFontManager.MeasureString(displaySubtitle, SubtitleFontSize);
            Vector2 subPos = new Vector2(
                MathF.Round(xPositionOnScreen + (_currentMenuWidth - subSize.X) / 2f),
                MathF.Round(yPositionOnScreen + TopPadding)
            );
            CustomFontManager.DrawString(spriteBatch, displaySubtitle, subPos, new Color(135, 98, 62), SubtitleFontSize);

            // 2. 主标题绘制（伴随整体自然下沉，并使用整数点位消除阴影发虚）
            var titleSize = CustomFontManager.MeasureStringBold(_title, TitleFontSize);
            Vector2 titlePos = new Vector2(
                MathF.Round(xPositionOnScreen + (_currentMenuWidth - titleSize.X) / 2f),
                MathF.Round(subPos.Y + subSize.Y + 4f)
            );
            CustomFontManager.DrawStringBold(spriteBatch, _title, titlePos + new Vector2(0, 1f), new Color(225, 200, 160) * 0.85f, TitleFontSize);
            CustomFontManager.DrawStringBold(spriteBatch, _title, titlePos, Game1.textColor, TitleFontSize);

            // 3. 文本框渲染
            _inputTextBox.Draw(spriteBatch);

            // 4. 底部提示语
            string instruction = I18n.DialogueInput.Instruction();
            var instructionSize = CustomFontManager.MeasureString(instruction, InstructionFontSize);
            Vector2 instructionPos = new Vector2(
                MathF.Round(xPositionOnScreen + (_currentMenuWidth - instructionSize.X) / 2f),
                MathF.Round(_inputTextBox.Position.Y + _inputTextBox.Extent.Y + 10f)
            );
            CustomFontManager.DrawString(spriteBatch, instruction, instructionPos, Color.Gray * 0.9f, InstructionFontSize);

            // 5. 按钮绘制与动效
            int mouseX = Game1.getMouseX();
            int mouseY = Game1.getMouseY();

            UpdateButtonScale(ref _okButtonHoverScale, _okButton, mouseX, mouseY);
            _okButton.scale = _okButtonBaseScale * _okButtonHoverScale;
            _okButton.draw(spriteBatch);

            UpdateButtonScale(ref _cancelButtonHoverScale, _cancelButton, mouseX, mouseY);
            _cancelButton.scale = _cancelButtonBaseScale * _cancelButtonHoverScale;
            _cancelButton.draw(spriteBatch);

            UpdateButtonScale(ref _clearHistoryHoverScale, _clearHistory, mouseX, mouseY);
            _clearHistory.scale = _clearHistoryBaseScale * _clearHistoryHoverScale;
            _clearHistory.draw(spriteBatch);

            UpdateButtonScale(ref _viewHistoryHoverScale, _viewHistory, mouseX, mouseY);
            _viewHistory.scale = _viewHistoryBaseScale * _viewHistoryHoverScale;
            _viewHistory.draw(spriteBatch);

            // 6. 悬停提示
            if (_clearHistory.containsPoint(mouseX, mouseY))
                DrawHoverTextCustom(spriteBatch, _clearHistory.hoverText);
            else if (_viewHistory.containsPoint(mouseX, mouseY))
                DrawHoverTextCustom(spriteBatch, _viewHistory.hoverText);

            base.draw(spriteBatch);

            if (!Game1.options.hardwareCursor)
            {
                spriteBatch.Draw(
                    Game1.mouseCursors,
                    new Vector2(mouseX, mouseY),
                    Game1.getSourceRectForStandardTileSheet(Game1.mouseCursors, 0, 16, 16),
                    Color.White,
                    0f,
                    Vector2.Zero,
                    4f,
                    SpriteEffects.None,
                    1f
                );
            }
        }

        private void UpdateButtonScale(ref float currentScale, ClickableTextureComponent button, int mouseX, int mouseY)
        {
            bool hover = button.containsPoint(mouseX, mouseY);
            float target = hover ? 1.15f : 1.0f;
            currentScale += (target - currentScale) * 0.2f;
        }

        private static void DrawHoverTextCustom(SpriteBatch b, string text)
        {
            if (string.IsNullOrEmpty(text))
                return;

            var sz = CustomFontManager.MeasureString(text, CustomFontManager.SizeRegular);

            // ── 扩大呼吸感与内边距 ──
            const int padX = 20;
            const int padY = 12;

            int boxW = (int)MathF.Ceiling(sz.X) + padX * 2;
            int boxH = (int)MathF.Ceiling(sz.Y) + padY * 2;

            int x = Game1.getOldMouseX() + 24;
            int y = Game1.getOldMouseY() + 24;
            var safe = Utility.getSafeArea();

            // 屏幕边缘自动翻折避让
            if (x + boxW > safe.Right) x = safe.Right - boxW;
            if (y + boxH > safe.Bottom)
            {
                x += 16;
                if (x + boxW > safe.Right) x = safe.Right - boxW;
                y = safe.Bottom - boxH;
            }
            if (x < safe.Left) x = safe.Left;
            if (y < safe.Top) y = safe.Top;

            // 1. 原版像素软阴影（0.65f 比例，精致不笨重）
            IClickableMenu.drawTextureBox(b, Game1.menuTexture, new Rectangle(0, 256, 60, 60),
                x + 4, y + 4, boxW, boxH, Color.Black * 0.28f, 0.65f, false);

            // 2. 星露谷原版暖白/浅亮羊皮纸底框（解决 1f 比例下边角过厚吃字的问题）
            IClickableMenu.drawTextureBox(b, Game1.menuTexture, new Rectangle(0, 256, 60, 60),
                x, y, boxW, boxH, new Color(255, 255, 250), 0.65f, false);

            // 3. 提示文字精准垂直居中
            float textY = y + (boxH - sz.Y) / 2f - 1;
            CustomFontManager.DrawString(b, text,
                new Vector2(x + padX, textY),
                Game1.textColor, CustomFontManager.SizeRegular);
        }

        public override void receiveLeftClick(int x, int y, bool playSound = true)
        {
            ReceiveLeftClick(x, y);
        }

        public void ReceiveLeftClick(int x, int y)
        {
            if (_inputTextBox.ReceiveLeftClick(x, y))
                return;

            if (_okButton.containsPoint(x, y))
            {
                Game1.playSound("coin");
                Submit(_inputTextBox.Text);
            }
            else if (_cancelButton.containsPoint(x, y))
            {
                Game1.playSound("cancel");
                Submit("");
            }
            else if (_clearHistory.containsPoint(x, y))
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
            }
            else if (_viewHistory.containsPoint(x, y))
            {
                Game1.playSound("bigSelect");
                Game1.keyboardDispatcher.Subscriber = null;
                var target = _menuToRestore ?? this;
                Game1.activeClickableMenu = new TimelineChronicleMenu(_npcName, target, target, autoLockLatest: false);
            }
            else
            {
                RestoreFocus();
            }
        }

        public override void receiveScrollWheelAction(int direction)
        {
            ReceiveScrollWheel(direction);
        }

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

        public void ReceiveScrollWheel(int direction)
        {
            _inputTextBox.ReceiveScrollWheel(direction);
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
                Submit("");
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
                if (key == Keys.A || key == Keys.C || key == Keys.X)
                {
                    _inputTextBox.RecieveSpecialInput(key);
                }
                return;
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
    }
}