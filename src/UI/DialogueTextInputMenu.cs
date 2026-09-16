using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewValley;
using StardewValley.Menus;
using System;
using System.Collections.Generic;

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
        private readonly ClickableTextureComponent _memoryButton;
        private readonly ClickableTextureComponent _profileButton;
        private readonly ClickableTextureComponent _advancedSettingsButton;
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
        private float _memoryButtonHoverScale = 1f;
        private float _profileButtonHoverScale = 1f;
        private float _advancedSettingsButtonHoverScale = 1f;

        private readonly float _okButtonBaseScale = 1f;
        private readonly float _cancelButtonBaseScale = 1f;
        private readonly float _clearHistoryBaseScale = 3f;
        private readonly float _viewHistoryBaseScale = 3.5f;
        private readonly float _memoryButtonBaseScale = 3.5f;
        private readonly float _profileButtonBaseScale = 3.5f;
        private readonly float _advancedSettingsButtonBaseScale = 3.5f;

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

            _memoryButton = new ClickableTextureComponent(
                new Rectangle(
                    (int)_menuPosition.X + 4 * Margin + 2 * ButtonSize,
                    (int)_menuPosition.Y + _currentMenuHeight - 2 * Margin - ButtonSize,
                    ButtonSize,
                    ButtonSize
                ),
                Game1.objectSpriteSheet,
                Game1.getSourceRectForStandardTileSheet(Game1.objectSpriteSheet, 434, 16, 16),
                3.5f
            )
            {
                hoverText = I18n.Memory.ButtonHover(_npcName)
            };

            _profileButton = new ClickableTextureComponent(
                new Rectangle(
                    (int)_menuPosition.X + 5 * Margin + 3 * ButtonSize,
                    (int)_menuPosition.Y + _currentMenuHeight - 2 * Margin - ButtonSize,
                    ButtonSize,
                    ButtonSize
                ),
                Game1.objectSpriteSheet,
                Game1.getSourceRectForStandardTileSheet(Game1.objectSpriteSheet, 102, 16, 16),
                3.5f
            )
            {
                hoverText = I18n.Profile.ButtonHover()
            };

            _advancedSettingsButton = new ClickableTextureComponent(
                new Rectangle(
                    (int)_menuPosition.X + 6 * Margin + 4 * ButtonSize,
                    (int)_menuPosition.Y + _currentMenuHeight - 2 * Margin - ButtonSize,
                    ButtonSize,
                    ButtonSize
                ),
                Game1.objectSpriteSheet,
                Game1.getSourceRectForStandardTileSheet(Game1.objectSpriteSheet, 373, 16, 16),
                3.5f
            )
            {
                hoverText = I18n.AdvancedSettings.ButtonHover()
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

            _memoryButton.bounds = new Rectangle(
                (int)_menuPosition.X + 4 * Margin + 2 * ButtonSize,
                (int)_menuPosition.Y + _currentMenuHeight - 2 * Margin - ButtonSize,
                ButtonSize,
                ButtonSize
            );

            _profileButton.bounds = new Rectangle(
                (int)_menuPosition.X + 5 * Margin + 3 * ButtonSize,
                (int)_menuPosition.Y + _currentMenuHeight - 2 * Margin - ButtonSize,
                ButtonSize,
                ButtonSize
            );

            _advancedSettingsButton.bounds = new Rectangle(
                (int)_menuPosition.X + 6 * Margin + 4 * ButtonSize,
                (int)_menuPosition.Y + _currentMenuHeight - 2 * Margin - ButtonSize,
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

            var titleSize = Game1.dialogueFont.MeasureString(_title);

            // 在充足的预留空间与槽位内绝对居中，安全包裹在边框内
            Vector2 titlePos = new Vector2(
                _menuPosition.X + (_currentMenuWidth - titleSize.X) / 2f,
                _menuPosition.Y + TopPadding + (HeaderHeight - titleSize.Y) / 2f
            );

            spriteBatch.DrawString(
                Game1.dialogueFont,
                _title,
                titlePos,
                Game1.textColor
            );

            _inputTextBox.Draw(spriteBatch);

            string instruction = I18n.DialogueInput.Instruction();
            spriteBatch.DrawString(
                Game1.smallFont,
                instruction,
                new Vector2(
                    _menuPosition.X + (_currentMenuWidth - Game1.smallFont.MeasureString(instruction).X) / 2,
                    _inputTextBox.Position.Y + _inputTextBox.Extent.Y + Margin * 1.2f
                ),
                Color.Gray
            );

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

            UpdateButtonScale(ref _memoryButtonHoverScale, _memoryButton, mouseX, mouseY);
            _memoryButton.scale = _memoryButtonBaseScale * _memoryButtonHoverScale;
            _memoryButton.draw(spriteBatch);

            UpdateButtonScale(ref _profileButtonHoverScale, _profileButton, mouseX, mouseY);
            _profileButton.scale = _profileButtonBaseScale * _profileButtonHoverScale;
            _profileButton.draw(spriteBatch);

            UpdateButtonScale(ref _advancedSettingsButtonHoverScale, _advancedSettingsButton, mouseX, mouseY);
            _advancedSettingsButton.scale = _advancedSettingsButtonBaseScale * _advancedSettingsButtonHoverScale;
            _advancedSettingsButton.draw(spriteBatch);

            if (_clearHistory.containsPoint(mouseX, mouseY))
                IClickableMenu.drawHoverText(spriteBatch, _clearHistory.hoverText, Game1.smallFont);
            else if (_viewHistory.containsPoint(mouseX, mouseY))
                IClickableMenu.drawHoverText(spriteBatch, _viewHistory.hoverText, Game1.smallFont);
            else if (_memoryButton.containsPoint(mouseX, mouseY))
                IClickableMenu.drawHoverText(spriteBatch, _memoryButton.hoverText, Game1.smallFont);
            else if (_advancedSettingsButton.containsPoint(mouseX, mouseY))
                IClickableMenu.drawHoverText(spriteBatch, _advancedSettingsButton.hoverText, Game1.smallFont);
            else if (_profileButton.containsPoint(mouseX, mouseY))
                IClickableMenu.drawHoverText(spriteBatch, _profileButton.hoverText, Game1.smallFont);

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
                if (Game1.input.GetKeyboardState().IsKeyDown(Keys.LeftShift))
                {
                    ShowConfirmation(
                        I18n.DialogueInput.ClearAllConfirm(),
                        () =>
                        {
                            Game1.playSound("trashcan");
                            ClearHistory();
                        },
                        () => { }
                    );
                }
                else
                {
                    ShowConfirmation(
                        I18n.DialogueInput.ClearOneConfirm(_npcName),
                        () =>
                        {
                            Game1.playSound("trashcan");
                            ClearHistory(_npcName);
                        },
                        () => { }
                    );
                }
            }
            else if (_viewHistory.containsPoint(x, y))
            {
                Game1.playSound("bigSelect");
                Game1.keyboardDispatcher.Subscriber = null;
                var target = _menuToRestore ?? this;
                Game1.activeClickableMenu = new TimelineChronicleMenu(_npcName, target, target);
            }
            else if (_memoryButton.containsPoint(x, y))
            {
                Game1.playSound("bigSelect");
                Game1.keyboardDispatcher.Subscriber = null;
                Game1.activeClickableMenu = new ScrollableMemoryMenu(_npcName, _menuToRestore ?? this);
            }
            else if (_advancedSettingsButton.containsPoint(x, y))
            {
                Game1.playSound("bigSelect");
                Game1.keyboardDispatcher.Subscriber = null;
                Game1.activeClickableMenu = new AdvancedSettingsMenu(this);
            }
            else if (_profileButton.containsPoint(x, y))
            {
                Game1.playSound("bigSelect");
                Game1.keyboardDispatcher.Subscriber = null;
                // 🌟 关键修改：将当前的包装器/菜单作为 ownerMenu 传给 Profile 菜单
                Game1.activeClickableMenu = new PlayerProfileCustomMenu(_npcName, _menuToRestore ?? this);
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

        
        // ⚠️ 不要在此处转发 Ctrl+Key！KeyboardDispatcher 已通过
        //IKeyboardSubscriber 路由。重复转发会导致粘贴/剪切/复制执行两次。
       //参照 AddMemoryInputMenu.receiveKeyPress 的正确写法。
        public void ReceiveKeyPress(Keys key)
        {
            if (key == Keys.Escape)
            {
                Game1.playSound("cancel");
                Submit("");
                return;
            }

            // Guard: Ctrl 组合键（C/V/X/A）由 KeyboardDispatcher 通过
            // IKeyboardSubscriber.RecieveSpecialInput 直接路由，此处不可重复转发。
            // 仅在没有 Ctrl 修饰时才转发导航/编辑键。
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

        private void ShowConfirmation(string message, Action onConfirm, Action onCancel)
        {
            Game1.keyboardDispatcher.Subscriber = null;
            Game1.activeClickableMenu = new ConfirmationDialog(
                message,
                _ =>
                {
                    onConfirm();
                    RestorePreviousMenu();
                },
                _ =>
                {
                    onCancel();
                    RestorePreviousMenu();
                }
            );
        }

        private void ClearHistory()
        {
            DialogueHistoryManager.Instance.ClearAllHistory();

            // Clear in-memory session caches so NPCs truly forget the conversation
            SessionCache.Instance.ResetAll();
            RecentConversationTracker.Clear();
            DialogueBuilder.Instance?.ClearAllContexts();
        }

        private void ClearHistory(string npcName)
        {
            DialogueHistoryManager.Instance.ClearHistory(npcName);

            // Clear in-memory session caches so NPCs truly forget the conversation
            SessionCache.Instance.Reset(npcName);
            RecentConversationTracker.Clear(npcName);
            DialogueBuilder.Instance?.ClearContext(npcName);
        }
    }

}