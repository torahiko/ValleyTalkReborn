
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
    /// <summary>
    /// 可滚动记忆菜单：查看、添加或删除与 NPC 绑定的记忆。
    /// 继承 IClickableMenu 以适配游戏原生 UI 系统。
    /// </summary>
    internal class ScrollableMemoryMenu : IClickableMenu
    {
        private readonly string _npcName;
        private readonly List<MemoryEntry> _entries;

        // UI 元素
        private Rectangle _addButtonRect;
        private readonly ClickableTextureComponent _closeButton;
        private readonly List<ClickableTextureComponent> _deleteButtons = new();

        // 滚动条
        private ClickableTextureComponent _upArrow;
        private ClickableTextureComponent _downArrow;
        private ClickableTextureComponent _scrollbar;
        private Rectangle _scrollbarRunner;
        private int _startIndex;
        private bool _scrolling;
        private int _hoveredRow = -1;

        // 布局常量
        private const int MenuWidth = 1000;
        private const int MenuHeight = 600;
        private const int TopPadding = 90;
        private const int BottomPadding = 75;
        private const int LineHeight = 46;
        private const int DeleteButtonSize = 40;
        private const int Margin = 24;

        public ScrollableMemoryMenu(string npcName, IClickableMenu parentMenu = null)
        {
            _npcName = npcName;
            _parentMenu = parentMenu;
            _entries = MemoryManager.Instance.GetMemories(_npcName);

            // 初始化位置
            xPositionOnScreen = (Game1.uiViewport.Width - MenuWidth) / 2;
            yPositionOnScreen = (Game1.uiViewport.Height - MenuHeight) / 2;
            width = MenuWidth;
            height = MenuHeight;

            // 关闭按钮（右上角）
            _closeButton = new ClickableTextureComponent(
                new Rectangle(xPositionOnScreen + width - 60, yPositionOnScreen + 16, 44, 44),
                Game1.mouseCursors, new Rectangle(337, 494, 12, 12), 3.5f);
            _closeButton.hoverText = I18n.Memory.CloseButton();

            // 添加按钮（底部居中）
            _addButtonRect = new Rectangle(
                xPositionOnScreen + width / 2 - 150,
                yPositionOnScreen + height - 60,
                300,
                48
            );

            // 滚动条
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

            // 初始化删除按钮
            RefreshDeleteButtons();

            exitFunction = () =>
            {
                Game1.playSound("bigDeSelect");
            };
        }

        private void RefreshDeleteButtons()
        {
            _deleteButtons.Clear();
            int visibleCount = GetVisibleLineCount();
            for (int i = 0; i < visibleCount && _startIndex + i < _entries.Count; i++)
            {
                int y = yPositionOnScreen + TopPadding + 10 + i * LineHeight;
                _deleteButtons.Add(new ClickableTextureComponent(
                    new Rectangle(xPositionOnScreen + width - 80, y, DeleteButtonSize, DeleteButtonSize),
                    Game1.mouseCursors, new Rectangle(322, 498, 12, 12), 2.5f));
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
                RefreshDeleteButtons();
            }
            else if (direction < 0 && _startIndex < Math.Max(0, _entries.Count - maxLines))
            {
                _startIndex++;
                Game1.playSound("shwip");
                SetScrollbarPosition();
                RefreshDeleteButtons();
            }
        }

        public override void receiveLeftClick(int x, int y, bool playSound = true)
        {
            base.receiveLeftClick(x, y, playSound);

            // 关闭按钮
            if (_closeButton.containsPoint(x, y))
            {
                Game1.playSound("bigDeSelect");
                exitThisMenu();
                return;
            }

            // 添加按钮
            if (_addButtonRect.Contains(x, y))
            {
                OpenAddMemoryDialog();
                return;
            }

            // 删除按钮
            for (int i = 0; i < _deleteButtons.Count; i++)
            {
                if (_deleteButtons[i].containsPoint(x, y))
                {
                    int entryIndex = _startIndex + i;
                    if (entryIndex < _entries.Count)
                    {
                        ConfirmDeleteMemory(_entries[entryIndex]);
                    }
                    return;
                }
            }

            // 滚动条
            int maxLines = GetVisibleLineCount();
            if (_entries.Count > maxLines)
            {
                if (_upArrow.containsPoint(x, y) && _startIndex > 0)
                {
                    _startIndex--;
                    Game1.playSound("shwip");
                    SetScrollbarPosition();
                    RefreshDeleteButtons();
                }
                else if (_downArrow.containsPoint(x, y) && _startIndex < _entries.Count - maxLines)
                {
                    _startIndex++;
                    Game1.playSound("shwip");
                    SetScrollbarPosition();
                    RefreshDeleteButtons();
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
                RefreshDeleteButtons();
            }
        }

        /// <summary>
        /// 打开添加记忆对话框
        /// </summary>
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

        /// <summary>
        /// 确认删除记忆
        /// </summary>
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

        /// <summary>
        /// 刷新记忆列表数据及UI状态
        /// </summary>
        internal void RefreshEntries()
        {
            _entries.Clear();
            _entries.AddRange(MemoryManager.Instance.GetMemories(_npcName));
            _startIndex = 0;
            RefreshDeleteButtons();
            SetScrollbarPosition();
        }

        public override void draw(SpriteBatch spriteBatch)
        {
            // 背景遮罩
            spriteBatch.Draw(Game1.fadeToBlackRect, Game1.graphics.GraphicsDevice.Viewport.Bounds, Color.Black * 0.4f);
            
            // 外层大框（两层嵌套，营造层次感）
            IClickableMenu.drawTextureBox(spriteBatch, xPositionOnScreen - 16, yPositionOnScreen - 16, width + 32, height + 32, Color.White);
            IClickableMenu.drawTextureBox(spriteBatch, xPositionOnScreen - 8, yPositionOnScreen - 8, width + 16, height + 16, Color.White);
            
            // 内层对话框
            Game1.drawDialogueBox(xPositionOnScreen, yPositionOnScreen, width, height, false, true);

            // 标题
            string title = I18n.Memory.Title(_npcName, _entries.Count);
            var titleSize = Game1.dialogueFont.MeasureString(title);
            spriteBatch.DrawString(Game1.dialogueFont, title,
                new Vector2(xPositionOnScreen + (width - titleSize.X) / 2, yPositionOnScreen + 24),
                Game1.textColor);

            // 标题下划线
            int titleLineY = yPositionOnScreen + 60;
            spriteBatch.Draw(Game1.staminaRect,
                new Rectangle(xPositionOnScreen + 40, titleLineY, width - 80, 2),
                Color.Gray * 0.5f);

            // 空状态提示
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
                // 绘制列表
                int maxLines = GetVisibleLineCount();
                int mouseX = Game1.getMouseX();
                int mouseY = Game1.getMouseY();

                for (int i = 0; i < maxLines && _startIndex + i < _entries.Count; i++)
                {
                    int entryIndex = _startIndex + i;
                    var entry = _entries[entryIndex];
                    int y = yPositionOnScreen + TopPadding + 10 + i * LineHeight;
                    var rowRect = new Rectangle(xPositionOnScreen + 24, y - 4, width - 48, LineHeight - 2);

                    // Hover 高亮背景
                    if (mouseX >= rowRect.X && mouseX <= rowRect.Right &&
                        mouseY >= rowRect.Y && mouseY <= rowRect.Bottom)
                    {
                        _hoveredRow = i;
                        spriteBatch.Draw(Game1.staminaRect, rowRect, new Color(70, 130, 180) * 0.18f);
                    }

                    // 序号 + 内容（使用 dialogueFont 更大更清晰）
                    string text = $"{entryIndex + 1}. {entry.Content}";
                    spriteBatch.DrawString(Game1.dialogueFont, text,
                        new Vector2(xPositionOnScreen + 40, y),
                        Game1.textColor);

                    // 创建时间（小字，右对齐）
                    string dateText = entry.CreatedAt.ToString("yyyy-MM-dd HH:mm");
                    var dateSize = Game1.smallFont.MeasureString(dateText);
                    spriteBatch.DrawString(Game1.smallFont, dateText,
                        new Vector2(xPositionOnScreen + width - dateSize.X - 100, y + 4),
                        Color.Gray);

                    // 删除按钮
                    if (i < _deleteButtons.Count)
                    {
                        _deleteButtons[i].draw(spriteBatch);
                    }
                }

                // 滚动条
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

            // 添加按钮
            IClickableMenu.drawTextureBox(
                spriteBatch,
                _addButtonRect.X,
                _addButtonRect.Y,
                _addButtonRect.Width,
                _addButtonRect.Height,
                Color.White
            );
            string addText = I18n.Memory.AddButton();
            var addTextSize = Game1.smallFont.MeasureString(addText);
            spriteBatch.DrawString(
                Game1.smallFont,
                addText,
                new Vector2(
                    _addButtonRect.X + (_addButtonRect.Width - addTextSize.X) / 2,
                    _addButtonRect.Y + (_addButtonRect.Height - addTextSize.Y) / 2
                ),
                Game1.textColor
            );

            // 关闭按钮
            _closeButton.draw(spriteBatch);

            // 鼠标
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
    /// 添加记忆的小型输入菜单（使用 DialogueTextInputBox 接收输入）
    /// 简化版：只保留一层边框
    /// </summary>

    internal class AddMemoryInputMenu : IClickableMenu
    {
        private readonly string _npcName;
        private readonly ScrollableMemoryMenu _returnMenu;
        private readonly DialogueTextInputBox _inputBox;
        private readonly ClickableTextureComponent _okButton;
        private readonly ClickableTextureComponent _cancelButton;
        private const int MenuWidth = 600;
        private const int MenuHeight = 280;

        // Character limit for memory input
        private const int CharacterLimit = 30;
        private const int WarningThreshold = 25;

        public AddMemoryInputMenu(string npcName, ScrollableMemoryMenu returnMenu)
        {
            _npcName = npcName;
            _returnMenu = returnMenu;
            xPositionOnScreen = (Game1.uiViewport.Width - MenuWidth) / 2;
            yPositionOnScreen = (Game1.uiViewport.Height - MenuHeight) / 2;
            width = MenuWidth;
            height = MenuHeight;

            // Input box - Y coordinate moved down by one line
            int lineHeight = Game1.dialogueFont.LineSpacing;
            _inputBox = new DialogueTextInputBox(CharacterLimit, WarningThreshold)
            {
                Position = new Vector2(xPositionOnScreen + 40, yPositionOnScreen + 100 + lineHeight),
                Extent = new Vector2(width - 80, 60),
                Font = Game1.dialogueFont,
                TextColor = Game1.textColor,
                Selected = true
            };
            _inputBox.OnSubmit += (sender) => Submit(sender.Text);

            Game1.keyboardDispatcher.Subscriber = _inputBox;

            // Buttons - also moved down by one line
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

            var result = MemoryManager.Instance.AddMemory(_npcName, text.Trim());

            if (result == 1)
            {
                Game1.playSound("coin");
                ModEntry.SMonitor?.Log($"[MemoryMenu] Added memory for {_npcName}: {text.Trim()}", LogLevel.Debug);
            }
            else if (result == 0)
            {
                Game1.playSound("cancel");
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

            // Handle scroll arrow clicks first
            if (_inputBox.ReceiveLeftClick(x, y))
                return;

            // Focus input box when clicked
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
                // 取消：Esc 返回上一级菜单
                if (key == Keys.Escape)
                {
                    Game1.playSound("bigDeSelect");
                    Game1.activeClickableMenu = _returnMenu;
                    return;
                }
                // 提交：Enter 确认输入
                if (key == Keys.Enter)
                {
                    Game1.playSound("coin");
                    Submit(_inputBox.Text);
                    return;
                }
                // 其他所有按键（退格、方向键、Delete、Home/End、长按等）：
                // - 字母/数字/空格由 TextInput 事件自动发送给输入框，无需手动处理
                // - 特殊按键需手动转发给输入框的 RecieveSpecialInput，否则它们永远不会被处理
                // - 不调用 base.receiveKeyPress，防止 E 键等触发默认菜单关闭行为
                _inputBox.RecieveSpecialInput(key);
                return;
            }
            base.receiveKeyPress(key);
        }

        public override void draw(SpriteBatch spriteBatch)
        {
            _inputBox.Update(Game1.currentGameTime);

            // Semi-transparent overlay
            spriteBatch.Draw(Game1.fadeToBlackRect, Game1.graphics.GraphicsDevice.Viewport.Bounds, Color.Black * 0.4f);

            // Simple rectangle box instead of dialogue bubble
            IClickableMenu.drawTextureBox(
                spriteBatch,
                xPositionOnScreen, yPositionOnScreen,
                width, height,
                Color.White
            );

            // Title
            string title = I18n.Memory.AddTitle(_npcName);
            var titleSize = Game1.dialogueFont.MeasureString(title);
            spriteBatch.DrawString(Game1.dialogueFont, title,
                new Vector2(xPositionOnScreen + (width - titleSize.X) / 2, yPositionOnScreen + 20),
                Game1.textColor);

            // Hint text below title with one line spacing
            string hint = I18n.Memory.AddHint();
            var hintSize = Game1.smallFont.MeasureString(hint);
            int lineSpacing = Game1.dialogueFont.LineSpacing;
            spriteBatch.DrawString(Game1.smallFont, hint,
                new Vector2(xPositionOnScreen + (width - hintSize.X) / 2, yPositionOnScreen + 20 + lineSpacing),
                Color.Gray);

            // Input box
            _inputBox.Draw(spriteBatch);

            // Buttons
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








