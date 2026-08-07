using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewValley;
using StardewValley.Menus;
using System;
using System.Collections.Generic;

namespace ValleyTalk
{
    public class DialogueTextInputMenu
    {
        public delegate void TextSubmittedDelegate(string input);

        private readonly string _title;
        private readonly DialogueTextInputBox _inputTextBox;
        private readonly ClickableTextureComponent _okButton;
        private readonly ClickableTextureComponent _cancelButton;
        private readonly ClickableTextureComponent _clearHistory;
        private readonly ClickableTextureComponent _viewHistory;
        private readonly ClickableTextureComponent _memoryButton;
        private readonly TextSubmittedDelegate _onTextSubmitted;
        private readonly string _npcName;

        private const int MenuWidth = 1200;
        private const int MenuHeight = 600;
        private const int TextBoxHeight = 240;
        private const int ButtonSize = 64;
        private const int Margin = 24;

        private Vector2 _menuPosition;
        private Rectangle _menuBounds;

        public DialogueTextInputMenu(string title, TextSubmittedDelegate callback, NPC currentNpc)
        {
            _title = title ?? "Enter your response";
            var titleSize = Game1.dialogueFont.MeasureString(_title);
            _onTextSubmitted = callback;
            _npcName = currentNpc?.Name ?? "";

            var totalHeight = Margin * 8 + titleSize.Y + TextBoxHeight + ButtonSize * 2;

            _menuPosition = new Vector2(
                (Game1.uiViewport.Width - MenuWidth) / 2,
                (Game1.uiViewport.Height - totalHeight) / 2
            );

            _menuBounds = new Rectangle((int)_menuPosition.X, (int)_menuPosition.Y, MenuWidth, MenuHeight);

            _inputTextBox = new DialogueTextInputBox(500)
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

            _cancelButton = new ClickableTextureComponent(
                new Rectangle((int)_menuPosition.X + MenuWidth - 3 * Margin - 2 * ButtonSize, (int)_menuPosition.Y + MenuHeight - 2 * Margin - ButtonSize, ButtonSize, ButtonSize),
                Game1.mouseCursors, Game1.getSourceRectForStandardTileSheet(Game1.mouseCursors, 47, -1, -1), 1f);

            var springTownTilesheet = Game1.content.Load<Texture2D>("Maps\\spring_town");
            _clearHistory = new ClickableTextureComponent(
                new Rectangle((int)_menuPosition.X + 2 * Margin, (int)_menuPosition.Y + MenuHeight - 2 * Margin - ButtonSize, ButtonSize, ButtonSize),
                springTownTilesheet, new Rectangle(224, 26, 16, 22), 3f);
            _clearHistory.hoverText = $"清空与 {_npcName} 的对话记录 ([Shift]清空所有)";

            _viewHistory = new ClickableTextureComponent(
                new Rectangle((int)_menuPosition.X + 3 * Margin + ButtonSize, (int)_menuPosition.Y + MenuHeight - 2 * Margin - ButtonSize, ButtonSize, ButtonSize),
                Game1.mouseCursors, new Rectangle(189, 423, 15, 13), 3.5f);
            _viewHistory.hoverText = $"查看与 {_npcName} 的历史对话记录";

            _memoryButton = new ClickableTextureComponent(
                new Rectangle((int)_menuPosition.X + 4 * Margin + 2 * ButtonSize, (int)_menuPosition.Y + MenuHeight - 2 * Margin - ButtonSize, ButtonSize, ButtonSize),
                Game1.mouseCursors, new Rectangle(152, 222, 16, 16), 3.2f);
            _memoryButton.hoverText = $"管理我与 {_npcName} 的记忆 (最多10条)";
        }

        public void Close()
        {
            Game1.keyboardDispatcher.Subscriber = null;
        }

        public void Draw(SpriteBatch spriteBatch)
        {
            // Recenter the menu on each frame to handle window resizing
            Recenter();

            spriteBatch.Draw(Game1.fadeToBlackRect, Game1.graphics.GraphicsDevice.Viewport.Bounds, Color.Black * 0.4f);
            Game1.drawDialogueBox(_menuBounds.X, _menuBounds.Y, _menuBounds.Width, _menuBounds.Height, false, true);

            var titleSize = Game1.dialogueFont.MeasureString(_title);
            spriteBatch.DrawString(Game1.dialogueFont, _title, new Vector2(_menuPosition.X + (MenuWidth - titleSize.X) / 2, _menuPosition.Y + 2 * Margin + titleSize.Y), Game1.textColor);

            _inputTextBox.Draw(spriteBatch);

            var instruction = "按下 Enter 发送，或点击 OK。按下 Esc 取消。";
            spriteBatch.DrawString(Game1.smallFont, instruction, new Vector2(_menuPosition.X + (MenuWidth - Game1.smallFont.MeasureString(instruction).X) / 2, _inputTextBox.Position.Y + _inputTextBox.Extent.Y + Margin * 1.5f), Color.Gray);

            _okButton.draw(spriteBatch);
            _cancelButton.draw(spriteBatch);
            _clearHistory.draw(spriteBatch);
            _viewHistory.draw(spriteBatch);
            _memoryButton.draw(spriteBatch);

            int mouseX = Game1.getMouseX();
            int mouseY = Game1.getMouseY();
            if (_clearHistory.containsPoint(mouseX, mouseY)) IClickableMenu.drawHoverText(spriteBatch, _clearHistory.hoverText, Game1.smallFont);
            else if (_viewHistory.containsPoint(mouseX, mouseY)) IClickableMenu.drawHoverText(spriteBatch, _viewHistory.hoverText, Game1.smallFont);
            else if (_memoryButton.containsPoint(mouseX, mouseY)) IClickableMenu.drawHoverText(spriteBatch, _memoryButton.hoverText, Game1.smallFont);

            if (!Game1.options.hardwareCursor)
            {
                spriteBatch.Draw(Game1.mouseCursors, new Vector2(mouseX, mouseY), Game1.getSourceRectForStandardTileSheet(Game1.mouseCursors, 0, 16, 16), Color.White, 0f, Vector2.Zero, 4f, SpriteEffects.None, 1f);
            }
        }

        public void ReceiveLeftClick(int x, int y)
        {
            if (_okButton.containsPoint(x, y)) { Game1.playSound("coin"); Submit(_inputTextBox.Text); }
            else if (_cancelButton.containsPoint(x, y)) { Game1.playSound("cancel"); Submit(""); }
            else if (_clearHistory.containsPoint(x, y))
            {
                if (Game1.input.GetKeyboardState().IsKeyDown(Keys.LeftShift))
                {
                    ShowConfirmation("确认清空与【所有村民】的历史对话吗？此操作无法撤销。", () => { Game1.playSound("trashcan"); ClearHistory(); }, () => { });
                }
                else
                {
                    ShowConfirmation($"确认清空与【{_npcName}】的历史对话吗？此操作无法撤销。", () => { Game1.playSound("trashcan"); ClearHistory(_npcName); }, () => { });
                }
            }
            else if (_viewHistory.containsPoint(x, y))
            {
                Game1.playSound("bigSelect");
                ShowHistoryDialogue();
            }
            else if (_memoryButton.containsPoint(x, y))
            {
                Game1.playSound("bigSelect");
                Game1.activeClickableMenu = new ScrollableMemoryMenu(_npcName);
            }
            else if (_inputTextBox.ContainsPoint(x, y))
            {
                Game1.keyboardDispatcher.Subscriber = _inputTextBox;
            }
        }

        public void ReceiveKeyPress(Keys key)
        {
            if (key == Keys.Escape)
            {
                Submit("");
            }
            else
            {
                _inputTextBox.RecieveSpecialInput(key);
            }
        }

        /// <summary>
        /// Recalculates the menu position to keep it centered on screen.
        /// </summary>
        public void Recenter()
        {
            var titleSize = Game1.dialogueFont.MeasureString(_title);
            var totalHeight = Margin * 8 + titleSize.Y + TextBoxHeight + ButtonSize * 2;

            _menuPosition = new Vector2(
                (Game1.uiViewport.Width - MenuWidth) / 2,
                (Game1.uiViewport.Height - totalHeight) / 2
            );

            _menuBounds = new Rectangle((int)_menuPosition.X, (int)_menuPosition.Y, MenuWidth, MenuHeight);

            // Update text box position
            _inputTextBox.Position = new Vector2(_menuPosition.X + Margin * 2, _menuPosition.Y + titleSize.Y + Margin * 5);

            // Update button positions
            _okButton.bounds = new Rectangle((int)_menuPosition.X + MenuWidth - 2 * Margin - ButtonSize, (int)_menuPosition.Y + MenuHeight - 2 * Margin - ButtonSize, ButtonSize, ButtonSize);
            _cancelButton.bounds = new Rectangle((int)_menuPosition.X + MenuWidth - 3 * Margin - 2 * ButtonSize, (int)_menuPosition.Y + MenuHeight - 2 * Margin - ButtonSize, ButtonSize, ButtonSize);
            _clearHistory.bounds = new Rectangle((int)_menuPosition.X + 2 * Margin, (int)_menuPosition.Y + MenuHeight - 2 * Margin - ButtonSize, ButtonSize, ButtonSize);
            _viewHistory.bounds = new Rectangle((int)_menuPosition.X + 3 * Margin + ButtonSize, (int)_menuPosition.Y + MenuHeight - 2 * Margin - ButtonSize, ButtonSize, ButtonSize);
            _memoryButton.bounds = new Rectangle((int)_menuPosition.X + 4 * Margin + 2 * ButtonSize, (int)_menuPosition.Y + MenuHeight - 2 * Margin - ButtonSize, ButtonSize, ButtonSize);
        }

        public void ShowHistoryDialogue()
        {
            // ... existing code to show history ...
        }

        private void ClearHistory(string npcName = null)
        {
            // ... existing code to clear history ...
        }

        private void ShowConfirmation(string message, Action onConfirm, Action onCancel)
        {
            // ... existing code for confirmation ...
        }

        private void Submit(string text)
        {
            _onTextSubmitted?.Invoke(text);
        }
    }
}
