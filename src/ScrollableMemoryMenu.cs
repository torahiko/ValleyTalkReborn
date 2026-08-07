using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewValley;
using StardewValley.Menus;
using StardewModdingAPI;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ValleyTalk
{
    internal class ScrollableMemoryMenu : IClickableMenu
    {
        private readonly string _npcName;
        private readonly List<MemoryEntry> _entries;

        private Rectangle _addButtonRect;
        private readonly ClickableTextureComponent _closeButton;
        private readonly List<ClickableTextureComponent> _deleteButtons = new();
        private readonly List<ClickableTextureComponent> _editButtons = new(); // 新增：编辑按钮列表

        private ClickableTextureComponent _upArrow;
        private ClickableTextureComponent _downArrow;
        private ClickableTextureComponent _scrollbar;
        private Rectangle _scrollbarRunner;
        private int _startIndex;
        private bool _scrolling;
        private int _hoveredRow = -1;

        private const int MenuWidth = 1000;
        private const int MenuHeight = 600;
        private const int TopPadding = 90;
        private const int BottomPadding = 75;
        private const int LineHeight = 46;
        private const int ButtonSize = 40; // 统一按钮大小

        public ScrollableMemoryMenu(string npcName, IClickableMenu parentMenu = null)
        {
            _npcName = npcName;
            _parentMenu = parentMenu;
            _entries = MemoryManager.Instance.GetMemories(_npcName);

            xPositionOnScreen = (Game1.uiViewport.Width - MenuWidth) / 2;
            yPositionOnScreen = (Game1.uiViewport.Height - MenuHeight) / 2;
            width = MenuWidth;
            height = MenuHeight;

            _closeButton = new ClickableTextureComponent(
                new Rectangle(xPositionOnScreen + width - 60, yPositionOnScreen + 16, 44, 44),
                Game1.mouseCursors, new Rectangle(337, 494, 12, 12), 3.5f);
            _closeButton.hoverText = I18n.Memory.CloseButton();

            _addButtonRect = new Rectangle(
                xPositionOnScreen + width / 2 - 150,
                yPositionOnScreen + height - 60,
                300,
                48
            );

            _upArrow = new ClickableTextureComponent(
                new Rectangle(xPositionOnScreen + width - 48, yPositionOnScreen + TopPadding, 44, 48),
                Game1.mouseCursors, new Rectangle(421, 459, 11, 12), 4f);
            _downArrow = new ClickableTextureComponent(
                new Rectangle(xPositionOnScreen + width - 48, yPositionOnScreen + height - BottomPadding, 44, 48),
                Game1.mouseCursors, new Rectangle(421, 472, 11, 12), 4f);
            _scrollbarRunner = new Rectangle(
                xPositionOnScreen + width - 32,
                yPositionOnScreen + TopPadding + 50,
                12,
                height - TopPadding - BottomPadding - 80);
            _scrollbar = new ClickableTextureComponent(
                new Rectangle(_scrollbarRunner.X - 6, _scrollbarRunner.Y, 24, 40),
                Game1.mouseCursors, new Rectangle(435, 463, 6, 10), 4f);

            RefreshActionButtons(); // 初始化按钮

            exitFunction = () => { Game1.playSound("bigDeSelect"); };
        }

        private void RefreshActionButtons()
        {
            _deleteButtons.Clear();
            _editButtons.Clear();
            int visibleCount = GetVisibleLineCount();
            
            for (int i = 0; i < visibleCount && _startIndex + i < _entries.Count; i++)
            {
                int y = yPositionOnScreen + TopPadding + 10 + i * LineHeight;
                
                // Delete button (right side, leave gap for scrollbar)
                var deleteBtn = new ClickableTextureComponent(
                    new Rectangle(xPositionOnScreen + width - 70, y, ButtonSize, ButtonSize),
                    Game1.mouseCursors, new Rectangle(322, 498, 12, 12), 2.5f);
                deleteBtn.hoverText = I18n.Memory.DeleteButtonHover();
                _deleteButtons.Add(deleteBtn);

                // Edit button (gear icon, left of delete button)
                var editBtn = new ClickableTextureComponent(
                    new Rectangle(xPositionOnScreen + width - 120, y, ButtonSize, ButtonSize),
                    Game1.mouseCursors, new Rectangle(274, 284, 16, 16), 2.5f);
                editBtn.hoverText = I18n.Memory.EditButtonHover();
                _editButtons.Add(editBtn);
            }
        }

        private int GetVisibleLineCount()
        {
            return (height - TopPadding - BottomPadding) / LineHeight;
        }

        private void SetScrollbarPosition()
        {
            int maxLines = GetVisibleLineCount();
            if (_entries.Count <= maxLines) return;
            float pct = (float)_startIndex / (_entries.Count - maxLines);
            _scrollbar.bounds.Y = _scrollbarRunner.Y + (int)(pct * (_scrollbarRunner.Height - _scrollbar.bounds.Height));
        }

        public override void receiveScrollWheelAction(int direction)
        {
            base.receiveScrollWheelAction(direction);
            int maxLines = GetVisibleLineCount();
            if (direction > 0 && _startIndex > 0)
            {
                _startIndex--;
                Game1.playSound("shwip");
                SetScrollbarPosition();
                RefreshActionButtons();
            }
            else if (direction < 0 && _startIndex < Math.Max(0, _entries.Count - maxLines))
            {
                _startIndex++;
                Game1.playSound("shwip");
                SetScrollbarPosition();
                RefreshActionButtons();
            }
        }

        public override void receiveLeftClick(int x, int y, bool playSound = true)
        {
            base.receiveLeftClick(x, y, playSound);

            if (_closeButton.containsPoint(x, y))
            {
                Game1.playSound("bigDeSelect");
                exitThisMenu();
                return;
            }

            if (_addButtonRect.Contains(x, y))
            {
                OpenAddMemoryDialog();
                return;
            }

            for (int i = 0; i < _deleteButtons.Count; i++)
            {
                int entryIndex = _startIndex + i;
                if (entryIndex >= _entries.Count) continue;

                // 检查删除按钮点击
                if (_deleteButtons[i].containsPoint(x, y))
                {
                    ConfirmDeleteMemory(_entries[entryIndex]);
                    return;
                }
                
                // 检查编辑按钮点击
                if (i < _editButtons.Count && _editButtons[i].containsPoint(x, y))
                {
                    Game1.playSound("bigSelect");
                    Game1.activeClickableMenu = new AddMemoryInputMenu(_npcName, this, _entries[entryIndex]);
                    return;
                }
            }

            int maxLines = GetVisibleLineCount();
            if (_entries.Count > maxLines)
            {
                if (_upArrow.containsPoint(x, y) && _startIndex > 0)
                {
                    _startIndex--;
                    Game1.playSound("shwip");
                    SetScrollbarPosition();
                    RefreshActionButtons();
                }
                else if (_downArrow.containsPoint(x, y) && _startIndex < _entries.Count - maxLines)
                {
                    _startIndex++;
                    Game1.playSound("shwip");
                    SetScrollbarPosition();
                    RefreshActionButtons();
                }
                else if (_scrollbarRunner.Contains(x, y) || _scrollbar.containsPoint(x, y))
                {
                    _scrolling = true;
                }
            }
        }

        public override void releaseLeftClick(int x, int y)
        {
            base.releaseLeftClick(x, y);
            _scrolling = false;
        }

        public override void leftClickHeld(int x, int y)
        {
            base.leftClickHeld(x, y);
            int maxLines = GetVisibleLineCount();
            if (_scrolling && _entries.Count > maxLines)
            {
                int yPos = Math.Max(_scrollbarRunner.Y, Math.Min(y, _scrollbarRunner.Bottom - _scrollbar.bounds.Height));
                float pct = (float)(yPos - _scrollbarRunner.Y) / (_scrollbarRunner.Height - _scrollbar.bounds.Height);
                _startIndex = (int)(pct * (_entries.Count - maxLines));
                SetScrollbarPosition();
                RefreshActionButtons();
            }
        }

        private void OpenAddMemoryDialog()
        {
            int count = MemoryManager.Instance.GetMemoryCount(_npcName);
            if (count >= MemoryManager.MaxMemoriesPerNpc)
            {
                Game1.drawObjectDialogue(I18n.Memory.AddFailedFull(MemoryManager.MaxMemoriesPerNpc));
                return;
            }
            Game1.activeClickableMenu = new AddMemoryInputMenu(_npcName, this);
        }

        private void ConfirmDeleteMemory(MemoryEntry entry)
        {
            Game1.activeClickableMenu = new ConfirmationDialog(
                I18n.Memory.DeleteConfirm(entry.Content),
                (farmer) =>
                {
                    MemoryManager.Instance.RemoveMemory(_npcName, entry.Id);
                    RefreshEntries();
                    Game1.playSound("trashcan");
                    Game1.activeClickableMenu = this;
                },
                (farmer) =>
                {
                    Game1.activeClickableMenu = this;
                });
        }

        internal void RefreshEntries()
        {
            _entries.Clear();
            _entries.AddRange(MemoryManager.Instance.GetMemories(_npcName));
            _startIndex = 0;
            RefreshActionButtons();
            SetScrollbarPosition();
        }

        public override void draw(SpriteBatch spriteBatch)
        {
            spriteBatch.Draw(Game1.fadeToBlackRect, Game1.graphics.GraphicsDevice.Viewport.Bounds, Color.Black * 0.4f);
            IClickableMenu.drawTextureBox(spriteBatch, xPositionOnScreen - 16, yPositionOnScreen - 16, width + 32, height + 32, Color.White);
            IClickableMenu.drawTextureBox(spriteBatch, xPositionOnScreen - 8, yPositionOnScreen - 8, width + 16, height + 16, Color.White);
            Game1.drawDialogueBox(xPositionOnScreen, yPositionOnScreen, width, height, false, true);

            string title = I18n.Memory.Title(_npcName, _entries.Count);
            var titleSize = Game1.dialogueFont.MeasureString(title);
            spriteBatch.DrawString(Game1.dialogueFont, title,
                new Vector2(xPositionOnScreen + (width - titleSize.X) / 2, yPositionOnScreen + 24),
                Game1.textColor);

            int titleLineY = yPositionOnScreen + 60;
            spriteBatch.Draw(Game1.staminaRect,
                new Rectangle(xPositionOnScreen + 40, titleLineY, width - 80, 2),
                Color.Gray * 0.5f);

            if (_entries.Count == 0)
            {
                string hint = I18n.Memory.Empty();
                var hintSize = Game1.dialogueFont.MeasureString(hint);
                spriteBatch.DrawString(Game1.dialogueFont, hint,
                    new Vector2(xPositionOnScreen + (width - hintSize.X) / 2, yPositionOnScreen + 200),
                    Color.Gray);
            }
            else
            {
                int maxLines = GetVisibleLineCount();
                int mouseX = Game1.getMouseX();
                int mouseY = Game1.getMouseY();

                for (int i = 0; i < maxLines && _startIndex + i < _entries.Count; i++)
                {
                    int entryIndex = _startIndex + i;
                    var entry = _entries[entryIndex];
                    int y = yPositionOnScreen + TopPadding + 10 + i * LineHeight;
                    var rowRect = new Rectangle(xPositionOnScreen + 24, y - 4, width - 48, LineHeight - 2);

                    if (mouseX >= rowRect.X && mouseX <= rowRect.Right &&
                        mouseY >= rowRect.Y && mouseY <= rowRect.Bottom)
                    {
                        _hoveredRow = i;
                        spriteBatch.Draw(Game1.staminaRect, rowRect, new Color(70, 130, 180) * 0.18f);
                    }

                    string text = $"{entryIndex + 1}. {entry.Content}";
                    spriteBatch.DrawString(Game1.dialogueFont, text,
                        new Vector2(xPositionOnScreen + 40, y),
                        Game1.textColor);

                    string dateText = entry.CreatedAt.ToString("yyyy-MM-dd HH:mm");
                    var dateSize = Game1.smallFont.MeasureString(dateText);
                    spriteBatch.DrawString(Game1.smallFont, dateText,
                        new Vector2(xPositionOnScreen + width - dateSize.X - 150, y + 4),
                        Color.Gray);

                    if (i < _editButtons.Count) _editButtons[i].draw(spriteBatch);
                    if (i < _deleteButtons.Count) _deleteButtons[i].draw(spriteBatch);
                }

                if (_entries.Count > maxLines)
                {
                    _upArrow.draw(spriteBatch);
                    _downArrow.draw(spriteBatch);
                    IClickableMenu.drawTextureBox(spriteBatch, Game1.mouseCursors,
                        new Rectangle(403, 383, 6, 6),
                        _scrollbarRunner.X, _scrollbarRunner.Y, _scrollbarRunner.Width, _scrollbarRunner.Height,
                        Color.White, 4f, false);
                    _scrollbar.draw(spriteBatch);
                }
            }

            IClickableMenu.drawTextureBox(spriteBatch, _addButtonRect.X, _addButtonRect.Y, _addButtonRect.Width, _addButtonRect.Height, Color.White);
            string addText = I18n.Memory.AddButton();
            var addTextSize = Game1.smallFont.MeasureString(addText);
            spriteBatch.DrawString(Game1.smallFont, addText,
                new Vector2(_addButtonRect.X + (_addButtonRect.Width - addTextSize.X) / 2, _addButtonRect.Y + (_addButtonRect.Height - addTextSize.Y) / 2),
                Game1.textColor);

            _closeButton.draw(spriteBatch);
            drawMouse(spriteBatch);
        }

        protected override void cleanupBeforeExit()
        {
            base.cleanupBeforeExit();
            if (_parentMenu != null)
            {
                Game1.activeClickableMenu = _parentMenu;
            }
        }
    }

    /// <summary>
    /// 支持传入现有实体实现“编辑”复用
    /// </summary>
    internal class AddMemoryInputMenu : IClickableMenu
    {
        private readonly string _npcName;
        private readonly ScrollableMemoryMenu _returnMenu;
        private readonly DialogueTextInputBox _inputBox;
        private readonly ClickableTextureComponent _okButton;
        private readonly ClickableTextureComponent _cancelButton;
        private readonly MemoryEntry _existingEntry; // 用于判断是新增还是编辑
        
        private const int MenuWidth = 600;
        private const int MenuHeight = 280;
        private const int CharacterLimit = 30;
        private const int WarningThreshold = 25;

        // 修改构造函数，增加可选参数 MemoryEntry
        public AddMemoryInputMenu(string npcName, ScrollableMemoryMenu returnMenu, MemoryEntry existingEntry = null)
        {
            _npcName = npcName;
            _returnMenu = returnMenu;
            _existingEntry = existingEntry;
            
            xPositionOnScreen = (Game1.uiViewport.Width - MenuWidth) / 2;
            yPositionOnScreen = (Game1.uiViewport.Height - MenuHeight) / 2;
            width = MenuWidth;
            height = MenuHeight;

            int lineHeight = Game1.dialogueFont.LineSpacing;
            _inputBox = new DialogueTextInputBox(CharacterLimit, WarningThreshold)
            {
                Position = new Vector2(xPositionOnScreen + 40, yPositionOnScreen + 100 + lineHeight),
                Extent = new Vector2(width - 80, 60),
                Font = Game1.dialogueFont,
                TextColor = Game1.textColor,
                Selected = true
            };
            
            // 如果是编辑模式，将原有内容填入输入框 (确保你的 DialogueTextInputBox 支持 Text 属性赋值，或使用相应的方法插入字符)
            if (_existingEntry != null)
            {
                // Prefill with existing memory content for editing
                _inputBox.SetText(_existingEntry.Content); 
            }

            _inputBox.OnSubmit += (sender) => Submit(sender.Text);

            Game1.keyboardDispatcher.Subscriber = _inputBox;

            int btnY = yPositionOnScreen + height - 80 + lineHeight;
            _okButton = new ClickableTextureComponent(
                new Rectangle(xPositionOnScreen + width - 2 * 24 - 64, btnY, 64, 64),
                Game1.mouseCursors, Game1.getSourceRectForStandardTileSheet(Game1.mouseCursors, 46, -1, -1), 1f);
            _cancelButton = new ClickableTextureComponent(
                new Rectangle(xPositionOnScreen + width - 3 * 24 - 2 * 64, btnY, 64, 64),
                Game1.mouseCursors, Game1.getSourceRectForStandardTileSheet(Game1.mouseCursors, 47, -1, -1), 1f);
        }

        private void Submit(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                Game1.playSound("cancel");
                Game1.activeClickableMenu = _returnMenu;
                return;
            }

            int? result;
            
            if (_existingEntry != null)
            {
                result = MemoryManager.Instance.EditMemory(_npcName, _existingEntry.Id, text.Trim());
            }
            else
            {
                result = MemoryManager.Instance.AddMemory(_npcName, text.Trim());
            }

            if (result == 1)
            {
                Game1.playSound("coin");
            }
            else if (result == 0)
            {
                Game1.playSound("cancel");
                // 编辑失败一般不会走这里，但以防万一
                Game1.drawObjectDialogue(I18n.Memory.AddFailedFull(MemoryManager.MaxMemoriesPerNpc));
                return;
            }
            else if (result == -1)
            {
                Game1.playSound("cancel");
                Game1.drawObjectDialogue(I18n.Memory.AddFailedTooLong(MemoryManager.Instance.GetMaxMemoryLength()));
                return;
            }
            else // null = duplicate
            {
                Game1.playSound("cancel");
                Game1.drawObjectDialogue(I18n.Memory.AddFailedDuplicate());
                return;
            }

            _returnMenu.RefreshEntries();
            Game1.activeClickableMenu = _returnMenu;
        }

        public override void receiveScrollWheelAction(int direction)
        {
            base.receiveScrollWheelAction(direction);
            _inputBox.ReceiveScrollWheel(direction);
        }

        public override void receiveLeftClick(int x, int y, bool playSound = true)
        {
            base.receiveLeftClick(x, y, playSound);

            if (_inputBox.ReceiveLeftClick(x, y)) return;

            if (_inputBox.ContainsPoint(x, y))
            {
                Game1.keyboardDispatcher.Subscriber = _inputBox;
                _inputBox.Selected = true;
                return;
            }

            if (_okButton.containsPoint(x, y))
            {
                Game1.playSound("coin");
                Submit(_inputBox.Text);
            }
            else if (_cancelButton.containsPoint(x, y))
            {
                Game1.playSound("bigDeSelect");
                Game1.activeClickableMenu = _returnMenu;
            }
        }

        public override void receiveKeyPress(Keys key)
        {
            if (Game1.keyboardDispatcher.Subscriber == _inputBox)
            {
                if (key == Keys.Escape)
                {
                    Game1.playSound("bigDeSelect");
                    Game1.activeClickableMenu = _returnMenu;
                    return;
                }
                if (key == Keys.Enter)
                {
                    Game1.playSound("coin");
                    Submit(_inputBox.Text);
                    return;
                }
                _inputBox.RecieveSpecialInput(key);
                return;
            }
            base.receiveKeyPress(key);
        }

        public override void draw(SpriteBatch spriteBatch)
        {
            _inputBox.Update(Game1.currentGameTime);
            spriteBatch.Draw(Game1.fadeToBlackRect, Game1.graphics.GraphicsDevice.Viewport.Bounds, Color.Black * 0.4f);
            IClickableMenu.drawTextureBox(spriteBatch, xPositionOnScreen, yPositionOnScreen, width, height, Color.White);

            // Dynamic title: edit mode shows "Edit Memory", add mode shows "Add a memory for {name}"
            string title = _existingEntry != null ? I18n.Memory.EditTitle(_npcName) : I18n.Memory.AddTitle(_npcName); 
            var titleSize = Game1.dialogueFont.MeasureString(title);
            spriteBatch.DrawString(Game1.dialogueFont, title,
                new Vector2(xPositionOnScreen + (width - titleSize.X) / 2, yPositionOnScreen + 20),
                Game1.textColor);

            string hint = I18n.Memory.AddHint();
            var hintSize = Game1.smallFont.MeasureString(hint);
            int lineSpacing = Game1.dialogueFont.LineSpacing;
            spriteBatch.DrawString(Game1.smallFont, hint,
                new Vector2(xPositionOnScreen + (width - hintSize.X) / 2, yPositionOnScreen + 20 + lineSpacing),
                Color.Gray);

            _inputBox.Draw(spriteBatch);
            _okButton.draw(spriteBatch);
            _cancelButton.draw(spriteBatch);
            drawMouse(spriteBatch);
        }

        protected override void cleanupBeforeExit()
        {
            base.cleanupBeforeExit();
            if (Game1.keyboardDispatcher.Subscriber == _inputBox)
            {
                Game1.keyboardDispatcher.Subscriber = null;
            }
        }
    }
}