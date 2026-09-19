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
        private const int TopPadding = 80; // 顶部预留出足够的对话框内边距
        private const int HeaderHeight = 96; // 扩展标题槽位高度，容纳各种语言的单行字高
        private const int TextBoxHeight = 240;
        private const int ButtonSize = 64;
        private const int Margin = 24;

        // Responsive runtime dimensions.
        private int _currentMenuWidth;
        private int _currentMenuHeight;
        private Vector2 _menuPosition;
        private Rectangle _menuBounds;

        // If this menu is wrapped, restore the wrapper instead of this menu.
        private IClickableMenu _menuToRestore;

        // Button hover animation fields.
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

            // Initialize responsive sizes before using them.
            _currentMenuWidth = Math.Min(MenuWidth, Game1.uiViewport.Width - 64);
            _currentMenuHeight = Math.Min(MenuHeight, Game1.uiViewport.Height - 64);
            var totalHeight = TopPadding + HeaderHeight + TextBoxHeight + ButtonSize * 2 + Margin * 2;

            _menuPosition = new Vector2(
                Math.Max(0, (Game1.uiViewport.Width - _currentMenuWidth) / 2),
                Math.Max(0, (Game1.uiViewport.Height - totalHeight) / 2)
            );

            _menuBounds = new Rectangle(
                (int)_menuPosition.X,
                (int)_menuPosition.Y,
                _currentMenuWidth,
                _currentMenuHeight
            );

            float textBoxWidth = Math.Max(64, _currentMenuWidth - 4 * Margin);

            _inputTextBox = new DialogueTextInputBox(300)
            {
                Position = new Vector2(_menuPosition.X + Margin * 2, _menuPosition.Y + TopPadding + HeaderHeight + 16),
                Extent = new Vector2(textBoxWidth, TextBoxHeight),
                Font = Game1.dialogueFont,
                TextColor = Game1.textColor,
                Selected = true
            };
            _inputTextBox.OnSubmit += sender =>
            {
                Game1.playSound("coin");
                Submit(sender.Text);
            };
            Game1.keyboardDispatcher.Subscriber = _inputTextBox;

            _okButton = new ClickableTextureComponent(
                new Rectangle(
                    (int)_menuPosition.X + _currentMenuWidth - 2 * Margin - ButtonSize,
                    (int)_menuPosition.Y + _currentMenuHeight - 2 * Margin - ButtonSize,
                    ButtonSize,
                    ButtonSize
                ),
                Game1.mouseCursors,
                Game1.getSourceRectForStandardTileSheet(Game1.mouseCursors, 46, -1, -1),
                1f
            );

            _cancelButton = new ClickableTextureComponent(
                new Rectangle(
                    (int)_menuPosition.X + _currentMenuWidth - 3 * Margin - 2 * ButtonSize,
                    (int)_menuPosition.Y + _currentMenuHeight - 2 * Margin - ButtonSize,
                    ButtonSize,
                    ButtonSize
                ),
                Game1.mouseCursors,
                Game1.getSourceRectForStandardTileSheet(Game1.mouseCursors, 47, -1, -1),
                1f
            );

            var springTownTilesheet = Game1.content.Load<Texture2D>("Maps\\spring_town");

            _clearHistory = new ClickableTextureComponent(
                new Rectangle(
                    (int)_menuPosition.X + 2 * Margin,
                    (int)_menuPosition.Y + _currentMenuHeight - 2 * Margin - ButtonSize,
                    ButtonSize,
                    ButtonSize
                ),
                springTownTilesheet,
                new Rectangle(224, 26, 16, 22),
                3f
            )
            {
                hoverText = I18n.DialogueInput.ClearHistoryHover(_npcName)
            };

            _viewHistory = new ClickableTextureComponent(
                new Rectangle(
                    (int)_menuPosition.X + 3 * Margin + ButtonSize,
                    (int)_menuPosition.Y + _currentMenuHeight - 2 * Margin - ButtonSize + 5,
                    ButtonSize,
                    ButtonSize
                ),
                Game1.mouseCursors,
                new Rectangle(189, 423, 15, 13),
                3.5f
            )
            {
                hoverText = I18n.DialogueInput.ViewHistoryHover(_npcName)
            };

            Recenter();
        }

        public void SetMenuToRestore(IClickableMenu menu)
        {
            _menuToRestore = menu;
        }

        public void RestoreFocus()
        {
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
            {
                Game1.keyboardDispatcher.Subscriber = null;
            }

            if (Game1.activeClickableMenu == this)
            {
                Game1.activeClickableMenu = null;
            }
        }

        public void Submit(string text)
        {
            _onTextSubmitted?.Invoke(text);
        }

        public void Recenter()
        {
            _currentMenuWidth = Math.Min(MenuWidth, Game1.uiViewport.Width - 64);
            _currentMenuHeight = Math.Min(MenuHeight, Game1.uiViewport.Height - 64);
            var totalHeight = TopPadding + HeaderHeight + TextBoxHeight + ButtonSize * 2 + Margin * 2;

            _menuPosition = new Vector2(
                Math.Max(0, (Game1.uiViewport.Width - _currentMenuWidth) / 2),
                Math.Max(0, (Game1.uiViewport.Height - totalHeight) / 2)
            );

            _menuBounds = new Rectangle(
                (int)_menuPosition.X,
                (int)_menuPosition.Y,
                _currentMenuWidth,
                _currentMenuHeight
            );

            xPositionOnScreen = _menuBounds.X;
            yPositionOnScreen = _menuBounds.Y;
            width = _currentMenuWidth;
            height = _currentMenuHeight;

            float textBoxWidth = Math.Max(64, _currentMenuWidth - 4 * Margin);
            _inputTextBox.Position = new Vector2(
                _menuPosition.X + Margin * 2,
                _menuPosition.Y + TopPadding + HeaderHeight + 16
            );
            _inputTextBox.Extent = new Vector2(textBoxWidth, TextBoxHeight);
            _inputTextBox.InvalidateLayout();

            _okButton.bounds = new Rectangle(
                (int)_menuPosition.X + _currentMenuWidth - 2 * Margin - ButtonSize,
                (int)_menuPosition.Y + _currentMenuHeight - 2 * Margin - ButtonSize,
                ButtonSize,
                ButtonSize
            );

            _cancelButton.bounds = new Rectangle(
                (int)_menuPosition.X + _currentMenuWidth - 3 * Margin - 2 * ButtonSize,
                (int)_menuPosition.Y + _currentMenuHeight - 2 * Margin - ButtonSize,
                ButtonSize,
                ButtonSize
            );

            _clearHistory.bounds = new Rectangle(
                (int)_menuPosition.X + 2 * Margin,
                (int)_menuPosition.Y + _currentMenuHeight - 2 * Margin - ButtonSize,
                ButtonSize,
                ButtonSize
            );

            _viewHistory.bounds = new Rectangle(
                (int)_menuPosition.X + 3 * Margin + ButtonSize,
                (int)_menuPosition.Y + _currentMenuHeight - 2 * Margin - ButtonSize + 5,
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

            Game1.drawDialogueBox(
                _menuBounds.X,
                _menuBounds.Y,
                _menuBounds.Width,
                _menuBounds.Height,
                false,
                true
            );

            var titleSize = CustomFontManager.MeasureString(_title, CustomFontManager.SizeTitle);

            Vector2 titlePos = new Vector2(
                _menuPosition.X + (_currentMenuWidth - titleSize.X) / 2f,
                _menuPosition.Y + TopPadding + (HeaderHeight - titleSize.Y) / 2f
            );

            CustomFontManager.DrawString(spriteBatch, _title, titlePos, Game1.textColor, CustomFontManager.SizeTitle);

            _inputTextBox.Draw(spriteBatch);

            string instruction = I18n.DialogueInput.Instruction();
            CustomFontManager.DrawString(spriteBatch, instruction,
                new Vector2(
                    _menuPosition.X + (_currentMenuWidth - CustomFontManager.MeasureString(instruction, CustomFontManager.SizeSmall).X) / 2,
                    _inputTextBox.Position.Y + _inputTextBox.Extent.Y + Margin * 1.2f
                ),
                Color.Gray, CustomFontManager.SizeSmall);

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

            // FONT-03: 悬浮提示使用矢量新字体渲染
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

        /// <summary>
        /// 悬浮提示的自定义矢量渲染：保留原版鼠标跟随位置与贴边 clamping，仅将文字替换为 CustomFontManager。
        /// </summary>
        private static void DrawHoverTextCustom(SpriteBatch b, string text)
        {
            if (string.IsNullOrEmpty(text))
                return;
            var sz = CustomFontManager.MeasureString(text, 17f);
            int boxW = (int)sz.X + 24;
            int boxH = (int)sz.Y + 24;
            int x = Game1.getOldMouseX() + 32;
            int y = Game1.getOldMouseY() + 32;
            var safe = Utility.getSafeArea();
            if (x + boxW > safe.Right)
                x = safe.Right - boxW;
            if (y + boxH > safe.Bottom)
            {
                x += 16;
                if (x + boxW > safe.Right)
                    x = safe.Right - boxW;
                y = safe.Bottom - boxH;
            }
            if (x < safe.Left)
                x = safe.Left;
            if (y < safe.Top)
                y = safe.Top;
            IClickableMenu.drawTextureBox(b, Game1.menuTexture, new Rectangle(0, 256, 60, 60),
                x, y, boxW, boxH, Color.White, 1f, false);
            CustomFontManager.DrawString(b, text, new Vector2(x + 12, y + 12), Game1.textColor, 17f);
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
            else if (_inputTextBox.ContainsPoint(x, y))
            {
                Game1.keyboardDispatcher.Subscriber = _inputTextBox;
            }
        }

        public override void receiveScrollWheelAction(int direction)
        {
            ReceiveScrollWheel(direction);
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

            if (!DialogueTextInputBox.IsControlKeyDown())
            {
                if (
                    key == Keys.Left ||
                    key == Keys.Right ||
                    key == Keys.Home ||
                    key == Keys.End ||
                    key == Keys.Delete ||
                    key == Keys.Back
                )
                {
                    _inputTextBox.RecieveSpecialInput(key);
                }
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