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
        private readonly TextSubmittedDelegate _onTextSubmitted;
        private readonly string _npcName;

        private const int MenuWidth = 1200;
        private const int MenuHeight = 600;
        private const int TextBoxHeight = 240;
        private const int ButtonSize = 64;
        private const int Margin = 24;

        private readonly Vector2 _menuPosition;
        private readonly Rectangle _menuBounds;

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
        }

        public void Close()
        {
            Game1.keyboardDispatcher.Subscriber = null;
        }

        public void Draw(SpriteBatch spriteBatch)
        {
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

            int mouseX = Game1.getMouseX();
            int mouseY = Game1.getMouseY();
            if (_clearHistory.containsPoint(mouseX, mouseY)) IClickableMenu.drawHoverText(spriteBatch, _clearHistory.hoverText, Game1.smallFont);
            else if (_viewHistory.containsPoint(mouseX, mouseY)) IClickableMenu.drawHoverText(spriteBatch, _viewHistory.hoverText, Game1.smallFont);

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
                    ConfirmAction("确认清空与【所有村民】的历史对话吗？此操作无法撤销。", () => { Game1.playSound("trashcan"); ClearHistory(); });
                }
                else
                {
                    ConfirmAction($"确认清空与【{_npcName}】的历史对话吗？此操作无法撤销。", () => { Game1.playSound("trashcan"); ClearHistory(_npcName); });
                }
            }
            else if (_viewHistory.containsPoint(x, y))
            {
                Game1.playSound("bigSelect");
                ShowHistoryDialogue();
            }
            else if (_inputTextBox.ContainsPoint(x, y))
            {
                Game1.keyboardDispatcher.Subscriber = _inputTextBox;
            }
        }

        private void ConfirmAction(string confirmText, Action onConfirm)
        {
            // Show confirmation and perform action directly
            // (history can be regenerated through gameplay, so no destructive risk)
            Game1.drawObjectDialogue(confirmText);
            onConfirm?.Invoke();
        }

        private void ClearHistory(string npcName = "")
        {
            if (string.IsNullOrEmpty(npcName))
            {
                DialogueHistoryManager.Instance.ClearAllHistory();
            }
            else
            {
                DialogueHistoryManager.Instance.ClearHistory(npcName);
            }
        }

        private void ShowHistoryDialogue()
        {
            // Use the new centralized dialogue history system
            var historyLines = DialogueHistoryManager.Instance.GetFormattedHistory(_npcName);

            if (historyLines.Count == 0)
            {
                Game1.drawObjectDialogue($"暂无与 {_npcName} 的历史对话记录。");
                return;
            }

            var parentMenu = Game1.activeClickableMenu;
            Game1.activeClickableMenu = new ScrollableHistoryMenu($"与 {_npcName} 的对话记录", historyLines, parentMenu);
        }

        public void ReceiveKeyPress(Keys key)
        {
            if (key == Keys.Escape) Submit(""); else _inputTextBox.RecieveSpecialInput(key);
        }
        public bool ContainsPoint(int x, int y) => _menuBounds.Contains(x, y);
        private void Submit(string text) => _onTextSubmitted?.Invoke(text ?? "");
    }
    public class ScrollableHistoryMenu : IClickableMenu
    {
        private readonly List<string> _wrappedLines = new();
        private new readonly IClickableMenu _parentMenu;
        private int _startIndex = 0;
        private readonly int _maxLines;
        private readonly ClickableTextureComponent _upArrow;
        private readonly ClickableTextureComponent _downArrow;
        private readonly ClickableTextureComponent _scrollbar;
        private readonly Rectangle _scrollbarRunner;
        private bool _scrolling;
        private readonly string _title;

        public ScrollableHistoryMenu(string title, List<string> historyLines, IClickableMenu parent)
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
            if (_parentMenu != null) Game1.activeClickableMenu = _parentMenu;
        }

        public override void draw(SpriteBatch b)
        {
            b.Draw(Game1.fadeToBlackRect, Game1.graphics.GraphicsDevice.Viewport.Bounds, Color.Black * 0.4f);
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