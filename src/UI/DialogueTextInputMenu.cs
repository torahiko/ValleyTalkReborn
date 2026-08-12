using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewValley;
using StardewValley.Menus;
using System;
using System.Collections.Generic;
using ValleytalkReborn;

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
        private const int MenuHeight = 600;
        private const int TextBoxHeight = 240;

        // Responsive runtime dimensions (clamped to viewport in Recenter)
        private int _currentMenuWidth;
        private int _currentMenuHeight;
        private const int ButtonSize = 64;
        private const int Margin = 24;

        private Vector2 _menuPosition;
        private Rectangle _menuBounds;

        // Button hover animation fields
        private float _okButtonHoverScale = 1f;
        private float _cancelButtonHoverScale = 1f;
        private float _clearHistoryHoverScale = 1f;
        private float _viewHistoryHoverScale = 1f;
        private float _memoryButtonHoverScale = 1f;
        private float _profileButtonHoverScale = 1f;
        private float _advancedSettingsButtonHoverScale = 1f;

        private readonly float _okButtonBaseScale;
        private readonly float _cancelButtonBaseScale;
        private readonly float _clearHistoryBaseScale;
        private readonly float _viewHistoryBaseScale;
        private readonly float _memoryButtonBaseScale;
        private readonly float _profileButtonBaseScale;
        private readonly float _advancedSettingsButtonBaseScale;

        public DialogueTextInputMenu(string title, TextSubmittedDelegate callback, NPC currentNpc)
        {
            _title = title ?? I18n.DialogueInput.DefaultTitle();
            var titleSize = Game1.dialogueFont.MeasureString(_title);
            _onTextSubmitted = callback;
            _npcName = currentNpc?.Name ?? "";

            var totalHeight = Margin * 8 + titleSize.Y + TextBoxHeight + ButtonSize * 2;

            _menuPosition = new Vector2(
                (Game1.uiViewport.Width - _currentMenuWidth) / 2,
                (Game1.uiViewport.Height - totalHeight) / 2
            );

            _menuBounds = new Rectangle((int)_menuPosition.X, (int)_menuPosition.Y, _currentMenuWidth, _currentMenuHeight);

            _inputTextBox = new DialogueTextInputBox(200)
            {
                Position = new Vector2(_menuPosition.X + Margin * 2, _menuPosition.Y + titleSize.Y + Margin * 5),
                Extent = new Vector2(MenuWidth - 4 * Margin, TextBoxHeight),
                Font = Game1.dialogueFont,
                TextColor = Game1.textColor,
                Selected = true
            };
            _inputTextBox.OnSubmit += (sender) => Submit(sender.Text);

            Game1.keyboardDispatcher.Subscriber = _inputTextBox;

            _okButton = new ClickableTextureComponent(
                new Rectangle((int)_menuPosition.X + MenuWidth - 2 * Margin - ButtonSize, (int)_menuPosition.Y + MenuHeight - 2 * Margin - ButtonSize, ButtonSize, ButtonSize),
                Game1.mouseCursors, Game1.getSourceRectForStandardTileSheet(Game1.mouseCursors, 46, -1, -1), 1f);
            _okButtonBaseScale = 1f;

            _cancelButton = new ClickableTextureComponent(
                new Rectangle((int)_menuPosition.X + MenuWidth - 3 * Margin - 2 * ButtonSize, (int)_menuPosition.Y + MenuHeight - 2 * Margin - ButtonSize, ButtonSize, ButtonSize),
                Game1.mouseCursors, Game1.getSourceRectForStandardTileSheet(Game1.mouseCursors, 47, -1, -1), 1f);
            _cancelButtonBaseScale = 1f;

            var springTownTilesheet = Game1.content.Load<Texture2D>("Maps\\spring_town");
            _clearHistory = new ClickableTextureComponent(
                new Rectangle((int)_menuPosition.X + 2 * Margin, (int)_menuPosition.Y + MenuHeight - 2 * Margin - ButtonSize, ButtonSize, ButtonSize),
                springTownTilesheet, new Rectangle(224, 26, 16, 22), 3f);
            _clearHistoryBaseScale = 3f;
            _clearHistory.hoverText = I18n.DialogueInput.ClearHistoryHover(_npcName);

            _viewHistory = new ClickableTextureComponent(
                new Rectangle((int)_menuPosition.X + 3 * Margin + ButtonSize, (int)_menuPosition.Y + MenuHeight - 2 * Margin - ButtonSize, ButtonSize, ButtonSize),
                Game1.mouseCursors, new Rectangle(189, 423, 15, 13), 3.5f);
            _viewHistoryBaseScale = 3.5f;
            _viewHistory.hoverText = I18n.DialogueInput.ViewHistoryHover(_npcName);

            _memoryButton = new ClickableTextureComponent(
                new Rectangle((int)_menuPosition.X + 4 * Margin + 2 * ButtonSize, (int)_menuPosition.Y + MenuHeight - 2 * Margin - ButtonSize, ButtonSize, ButtonSize),
                Game1.objectSpriteSheet,
                Game1.getSourceRectForStandardTileSheet(Game1.objectSpriteSheet, 434, 16, 16),
                3.5f
            );
            _memoryButtonBaseScale = 3.5f;
            _memoryButton.hoverText = I18n.Memory.ButtonHover(_npcName);

            // Blue Book Icon (Index 102 in objectSpriteSheet: Lost Book)
            _profileButton = new ClickableTextureComponent(
                new Rectangle((int)_menuPosition.X + 5 * Margin + 3 * ButtonSize, (int)_menuPosition.Y + MenuHeight - 2 * Margin - ButtonSize, ButtonSize, ButtonSize),
                Game1.objectSpriteSheet,
                Game1.getSourceRectForStandardTileSheet(Game1.objectSpriteSheet, 102, 16, 16),
                3.5f
            )
            {
                hoverText = I18n.Profile.ButtonHover() ?? "Farmer Profile & Persona"
            };
            _profileButtonBaseScale = 3.5f;

            // Golden Pumpkin Icon (Object ID 373)
            _advancedSettingsButton = new ClickableTextureComponent(
                new Rectangle((int)_menuPosition.X + 6 * Margin + 4 * ButtonSize, (int)_menuPosition.Y + MenuHeight - 2 * Margin - ButtonSize, ButtonSize, ButtonSize),
                Game1.objectSpriteSheet,
                Game1.getSourceRectForStandardTileSheet(Game1.objectSpriteSheet, 373, 16, 16),
                3.5f
            )
            {
                hoverText = ModEntry.SHelper.Translation.Get("AdvancedSettings.ButtonHover").Default("Advanced Settings")
            };
            _advancedSettingsButtonBaseScale = 3.5f;

            // Perform initial responsive layout
            Recenter();
        }

        // 【新增】：用于恢复输入框焦点
        public void RestoreFocus()
        {
            Game1.keyboardDispatcher.Subscriber = _inputTextBox;
        }

        // 【新增】：用于从其他子菜单返回当前菜单
        public void RestorePreviousMenu()
        {
            Game1.activeClickableMenu = this;
            RestoreFocus();
        }

        public void Close()
        {
            Game1.keyboardDispatcher.Subscriber = null;
            if (Game1.activeClickableMenu == this)
            {
                Game1.activeClickableMenu = null;
            }
        }

        // 【修复】：兜底清理焦点，防止菜单因其他原因意外销毁导致的焦点残留
        protected override void cleanupBeforeExit()
        {
            Game1.keyboardDispatcher.Subscriber = null;
            base.cleanupBeforeExit();
        }

        // 【修改】：使用标准的星露谷 draw 覆写方法
        public override void draw(SpriteBatch spriteBatch)
        {
            _inputTextBox.Update(Game1.currentGameTime);

            spriteBatch.Draw(Game1.fadeToBlackRect, Game1.graphics.GraphicsDevice.Viewport.Bounds, Color.Black * 0.4f);
            Game1.drawDialogueBox(_menuBounds.X, _menuBounds.Y, _menuBounds.Width, _menuBounds.Height, false, true);

            var titleSize = Game1.dialogueFont.MeasureString(_title);
            spriteBatch.DrawString(Game1.dialogueFont, _title, new Vector2(_menuPosition.X + (_currentMenuWidth - titleSize.X) / 2, _menuPosition.Y + 2 * Margin + titleSize.Y), Game1.textColor);

            _inputTextBox.Draw(spriteBatch);

            var instruction = I18n.DialogueInput.Instruction();
            spriteBatch.DrawString(Game1.smallFont, instruction, new Vector2(_menuPosition.X + (_currentMenuWidth - Game1.smallFont.MeasureString(instruction).X) / 2, _inputTextBox.Position.Y + _inputTextBox.Extent.Y + Margin * 1.5f), Color.Gray);

            // Update and draw buttons with hover animation
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

            if (_clearHistory.containsPoint(mouseX, mouseY)) IClickableMenu.drawHoverText(spriteBatch, _clearHistory.hoverText, Game1.smallFont);
            else if (_viewHistory.containsPoint(mouseX, mouseY)) IClickableMenu.drawHoverText(spriteBatch, _viewHistory.hoverText, Game1.smallFont);
            else if (_memoryButton.containsPoint(mouseX, mouseY)) IClickableMenu.drawHoverText(spriteBatch, _memoryButton.hoverText, Game1.smallFont);
            else if (_advancedSettingsButton.containsPoint(mouseX, mouseY)) IClickableMenu.drawHoverText(spriteBatch, _advancedSettingsButton.hoverText, Game1.smallFont);
            else if (_profileButton.containsPoint(mouseX, mouseY)) IClickableMenu.drawHoverText(spriteBatch, _profileButton.hoverText, Game1.smallFont);

            // 【修改】：绘制底层UI（如右上角关闭按钮等）
            base.draw(spriteBatch);

            // 【修改】：确保鼠标在所有 UI 绘制完毕后处于最上层
            if (!Game1.options.hardwareCursor)
            {
                spriteBatch.Draw(Game1.mouseCursors, new Vector2(mouseX, mouseY), Game1.getSourceRectForStandardTileSheet(Game1.mouseCursors, 0, 16, 16), Color.White, 0f, Vector2.Zero, 4f, SpriteEffects.None, 1f);
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

            if (_okButton.containsPoint(x, y)) { Game1.playSound("coin"); Submit(_inputTextBox.Text); }
            else if (_cancelButton.containsPoint(x, y)) { Game1.playSound("cancel"); Submit(""); }
            else if (_clearHistory.containsPoint(x, y))
            {
                if (Game1.input.GetKeyboardState().IsKeyDown(Keys.LeftShift))
                {
                    ShowConfirmation(I18n.DialogueInput.ClearAllConfirm(), () => { Game1.playSound("trashcan"); ClearHistory(); }, () => { });
                }
                else
                {
                    ShowConfirmation(I18n.DialogueInput.ClearOneConfirm(_npcName), () => { Game1.playSound("trashcan"); ClearHistory(_npcName); }, () => { });
                }
            }
            else if (_viewHistory.containsPoint(x, y))
            {
                Game1.playSound("bigSelect");
                Game1.keyboardDispatcher.Subscriber = null; // 【修复】打开子菜单前解绑焦点
                ShowHistoryDialogue();
            }
            else if (_memoryButton.containsPoint(x, y))
            {
                Game1.playSound("bigSelect");
                Game1.keyboardDispatcher.Subscriber = null; // 【修复】打开子菜单前解绑焦点
                Game1.activeClickableMenu = new ScrollableMemoryMenu(_npcName);
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
                Game1.keyboardDispatcher.Subscriber = null; // 【修复】打开子菜单前解绑焦点
                Game1.activeClickableMenu = new PlayerProfileCustomMenu(_npcName);
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
                Submit("");
            }
            else if (!DialogueTextInputBox.IsControlKeyDown())
            {
                _inputTextBox.RecieveSpecialInput(key);
            }
        }

        public void Recenter()
        {
            // Responsive sizing: clamp to viewport with a 32px margin on each side
            _currentMenuWidth = Math.Min(MenuWidth, Game1.uiViewport.Width - 64);
            _currentMenuHeight = Math.Min(MenuHeight, Game1.uiViewport.Height - 64);

            var titleSize = Game1.dialogueFont.MeasureString(_title);
            var totalHeight = Margin * 8 + titleSize.Y + TextBoxHeight + ButtonSize * 2;

            _menuPosition = new Vector2(
                (Game1.uiViewport.Width - _currentMenuWidth) / 2,
                (Game1.uiViewport.Height - totalHeight) / 2
            );

            _menuBounds = new Rectangle((int)_menuPosition.X, (int)_menuPosition.Y, MenuWidth, MenuHeight);

            _inputTextBox.Position = new Vector2(_menuPosition.X + Margin * 2, _menuPosition.Y + titleSize.Y + Margin * 5);
            _inputTextBox.Extent = new Vector2(_currentMenuWidth - 4 * Margin, TextBoxHeight);

            _okButton.bounds = new Rectangle((int)_menuPosition.X + _currentMenuWidth - 2 * Margin - ButtonSize, (int)_menuPosition.Y + _currentMenuHeight - 2 * Margin - ButtonSize, ButtonSize, ButtonSize);
            _cancelButton.bounds = new Rectangle((int)_menuPosition.X + _currentMenuWidth - 3 * Margin - 2 * ButtonSize, (int)_menuPosition.Y + _currentMenuHeight - 2 * Margin - ButtonSize, ButtonSize, ButtonSize);
            _clearHistory.bounds = new Rectangle((int)_menuPosition.X + 2 * Margin, (int)_menuPosition.Y + _currentMenuHeight - 2 * Margin - ButtonSize, ButtonSize, ButtonSize);
            _viewHistory.bounds = new Rectangle((int)_menuPosition.X + 3 * Margin + ButtonSize, (int)_menuPosition.Y + _currentMenuHeight - 2 * Margin - ButtonSize, ButtonSize, ButtonSize);
            _memoryButton.bounds = new Rectangle((int)_menuPosition.X + 4 * Margin + 2 * ButtonSize, (int)_menuPosition.Y + _currentMenuHeight - 2 * Margin - ButtonSize, ButtonSize, ButtonSize);
            _profileButton.bounds = new Rectangle((int)_menuPosition.X + 5 * Margin + 3 * ButtonSize, (int)_menuPosition.Y + _currentMenuHeight - 2 * Margin - ButtonSize, ButtonSize, ButtonSize);
            _advancedSettingsButton.bounds = new Rectangle((int)_menuPosition.X + 6 * Margin + 4 * ButtonSize, (int)_menuPosition.Y + MenuHeight - 2 * Margin - ButtonSize, ButtonSize, ButtonSize);
        }

        // 【新增】：窗口大小变更时重新布局，避免每帧调用 Recenter
        public override void gameWindowSizeChanged(Rectangle oldBounds, Rectangle newBounds)
        {
            base.gameWindowSizeChanged(oldBounds, newBounds);
            Recenter();
        }

        private void ShowConfirmation(string message, Action onConfirm, Action onCancel)
        {
            Game1.keyboardDispatcher.Subscriber = null; // 【修复】打开确认框前解绑焦点
            Game1.activeClickableMenu = new ConfirmationDialog(
                message,
                _ => { onConfirm(); RestorePreviousMenu(); },
                _ => { onCancel(); RestorePreviousMenu(); });
        }

        private void ShowHistoryDialogue()
        {
            var historyLines = DialogueHistoryManager.Instance.GetFormattedHistory(_npcName);
            if (historyLines == null || historyLines.Count == 0)
            {
                Game1.drawObjectDialogue(I18n.DialogueInput.HistoryEmpty(_npcName));
                return;
            }

            Game1.activeClickableMenu = new HistoryDialogue(this, I18n.DialogueInput.HistoryTitle(_npcName), historyLines);
        }

        public void Submit(string text)
        {
            Game1.playSound("coin");
            _onTextSubmitted?.Invoke(text);
        }

        private void ClearHistory()
        {
            DialogueHistoryManager.Instance.ClearAllHistory();
        }

        private void ClearHistory(string npcName)
        {
            DialogueHistoryManager.Instance.ClearHistory(npcName);
        }
    }

    // HistoryDialogue nested class
    internal class HistoryDialogue : IClickableMenu
    {
        private new readonly DialogueTextInputMenu _parentMenu;
        private readonly string _title;
        private readonly List<string> _wrappedLines = new List<string>();
        private readonly ClickableTextureComponent _upArrow;
        private readonly ClickableTextureComponent _downArrow;
        private readonly ClickableTextureComponent _scrollbar;
        private readonly Rectangle _scrollbarRunner;
        private int _startIndex;
        private bool _scrolling;
        private readonly int _maxLines;

        public HistoryDialogue(DialogueTextInputMenu parent, string title, List<string> historyLines)
            : base(Game1.uiViewport.Width / 2 - 400, Game1.uiViewport.Height / 2 - 300, 800, 600, true)
        {
            _parentMenu = parent;
            _title = title;

            int textWidth = width - 128;
            foreach (var line in historyLines)
            {
                string wrapped = Game1.parseText(line, Game1.dialogueFont, textWidth);
                _wrappedLines.AddRange(wrapped.Split('\n'));
                _wrappedLines.Add("");
            }
            if (_wrappedLines.Count > 0) _wrappedLines.RemoveAt(_wrappedLines.Count - 1);

            _maxLines = (height - 128) / Game1.dialogueFont.LineSpacing;

            _upArrow = new ClickableTextureComponent(new Rectangle(xPositionOnScreen + width - 48, yPositionOnScreen + 64, 44, 48), Game1.mouseCursors, new Rectangle(421, 459, 11, 12), 4f);
            _downArrow = new ClickableTextureComponent(new Rectangle(xPositionOnScreen + width - 48, yPositionOnScreen + height - 64, 44, 48), Game1.mouseCursors, new Rectangle(421, 472, 11, 12), 4f);
            _scrollbarRunner = new Rectangle(xPositionOnScreen + width - 32, yPositionOnScreen + 112, 12, height - 176);
            _scrollbar = new ClickableTextureComponent(new Rectangle(_scrollbarRunner.X - 6, _scrollbarRunner.Y, 24, 40), Game1.mouseCursors, new Rectangle(435, 463, 6, 10), 4f);

            _startIndex = Math.Max(0, _wrappedLines.Count - _maxLines);
            SetScrollbarPosition();
        }

        private void SetScrollbarPosition()
        {
            if (_wrappedLines.Count <= _maxLines) return;
            float pct = (float)_startIndex / (_wrappedLines.Count - _maxLines);
            _scrollbar.bounds.Y = _scrollbarRunner.Y + (int)(pct * (_scrollbarRunner.Height - _scrollbar.bounds.Height));
        }

        public override void receiveScrollWheelAction(int direction)
        {
            base.receiveScrollWheelAction(direction);
            if (direction > 0 && _startIndex > 0) { _startIndex--; Game1.playSound("shwip"); }
            else if (direction < 0 && _startIndex < Math.Max(0, _wrappedLines.Count - _maxLines)) { _startIndex++; Game1.playSound("shwip"); }
            SetScrollbarPosition();
        }

        public override void receiveLeftClick(int x, int y, bool playSound = true)
        {
            base.receiveLeftClick(x, y, playSound);
            if (_wrappedLines.Count <= _maxLines) return;

            if (_upArrow.containsPoint(x, y) && _startIndex > 0) { _startIndex--; SetScrollbarPosition(); Game1.playSound("shwip"); }
            else if (_downArrow.containsPoint(x, y) && _startIndex < Math.Max(0, _wrappedLines.Count - _maxLines)) { _startIndex++; SetScrollbarPosition(); Game1.playSound("shwip"); }
            else if (_scrollbarRunner.Contains(x, y) || _scrollbar.containsPoint(x, y)) _scrolling = true;
        }

        public override void releaseLeftClick(int x, int y) { base.releaseLeftClick(x, y); _scrolling = false; }

        public override void leftClickHeld(int x, int y)
        {
            base.leftClickHeld(x, y);
            if (_scrolling && _wrappedLines.Count > _maxLines)
            {
                int yPos = Math.Max(_scrollbarRunner.Y, Math.Min(y, _scrollbarRunner.Bottom - _scrollbar.bounds.Height));
                float pct = (float)(yPos - _scrollbarRunner.Y) / (_scrollbarRunner.Height - _scrollbar.bounds.Height);
                _startIndex = (int)(pct * (_wrappedLines.Count - _maxLines));
                SetScrollbarPosition();
            }
        }

        protected override void cleanupBeforeExit()
        {
            base.cleanupBeforeExit();
            if (_parentMenu != null)
            {
                _parentMenu.RestorePreviousMenu();
            }
        }

        public override void draw(SpriteBatch b)
        {
            b.Draw(Game1.fadeToBlackRect, Game1.graphics.GraphicsDevice.Viewport.Bounds, Color.Black * 0.4f);

            IClickableMenu.drawTextureBox(b, xPositionOnScreen - 16, yPositionOnScreen - 16, width + 32, height + 32, Color.White);
            IClickableMenu.drawTextureBox(b, xPositionOnScreen - 8, yPositionOnScreen - 8, width + 16, height + 16, Color.White);

            Game1.drawDialogueBox(xPositionOnScreen, yPositionOnScreen, width, height, false, true);

            var titleSize = Game1.dialogueFont.MeasureString(_title);
            b.DrawString(Game1.dialogueFont, _title, new Vector2(xPositionOnScreen + (width - titleSize.X) / 2, yPositionOnScreen + 32), Game1.textColor);

            for (int i = 0; i < _maxLines; i++)
            {
                if (_startIndex + i < _wrappedLines.Count)
                    b.DrawString(Game1.dialogueFont, _wrappedLines[_startIndex + i], new Vector2(xPositionOnScreen + 64, yPositionOnScreen + 96 + i * Game1.dialogueFont.LineSpacing), Game1.textColor);
            }

            if (_wrappedLines.Count > _maxLines)
            {
                _upArrow.draw(b);
                _downArrow.draw(b);
                IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(403, 383, 6, 6), _scrollbarRunner.X, _scrollbarRunner.Y, _scrollbarRunner.Width, _scrollbarRunner.Height, Color.White, 4f, false);
                _scrollbar.draw(b);
            }

            base.draw(b);
            drawMouse(b);
        }
    }
}