using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewValley;
using StardewValley.Menus;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ValleyTalk
{
    /// <summary>
    /// 记忆管理窗口：查看、添加、删除与 NPC 绑定的记忆。
    /// 独立 IClickableMenu，不混入聊天输入框。
    /// </summary>
    internal class ScrollableMemoryMenu : IClickableMenu
    {
        private readonly string _npcName;
        private readonly List<MemoryEntry> _entries;

        // UI 元素
        private readonly ClickableTextureComponent _addButton;
        private readonly ClickableTextureComponent _closeButton;
        private readonly List<ClickableTextureComponent> _deleteButtons = new();

        // 滚动
        private ClickableTextureComponent _upArrow;
        private ClickableTextureComponent _downArrow;
        private ClickableTextureComponent _scrollbar;
        private Rectangle _scrollbarRunner;
        private int _startIndex;
        private bool _scrolling;

        // 布局常量
        private const int MenuWidth = 1000;
        private const int MenuHeight = 600;
        private const int TopPadding = 80;
        private const int BottomPadding = 80;
        private const int LineHeight = 48;
        private const int DeleteButtonSize = 36;
        private const int Margin = 24;

        // 父菜单（关闭时返回）
        private readonly IClickableMenu _parentMenu;

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
                Game1.mouseCursors, new Rectangle(294, 429, 12, 12), 3f);

            // 添加按钮（底部居中）
            _addButton = new ClickableTextureComponent(
                new Rectangle(xPositionOnScreen + width / 2 - 80, yPositionOnScreen + height - 60, 160, 44),
                Game1.mouseCursors, new Rectangle(0, 0, 16, 16), 1f);

            // 滚动条
            _upArrow = new ClickableTextureComponent(
                new Rectangle(xPositionOnScreen + width - 48, yPositionOnScreen + TopPadding, 44, 48),
                Game1.mouseCursors, new Rectangle(421, 459, 11, 12), 4f);
            _downArrow = new ClickableTextureComponent(
                new Rectangle(xPositionOnScreen + width - 48, yPositionOnScreen + height - BottomPadding, 44, 48),
                Game1.mouseCursors, new Rectangle(421, 472, 11, 12), 4f);
            _scrollbarRunner = new Rectangle(xPositionOnScreen + width - 32, yPositionOnScreen + TopPadding + 50, 12, height - TopPadding - BottomPadding - 100);
            _scrollbar = new ClickableTextureComponent(
                new Rectangle(_scrollbarRunner.X - 6, _scrollbarRunner.Y, 24, 40),
                Game1.mouseCursors, new Rectangle(435, 463, 6, 10), 4f);

            // 初始化删除按钮
            RefreshDeleteButtons();

            exitFunction = (IClickableMenu m) => {
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
                    Game1.mouseCursors, new Rectangle(294, 429, 12, 12), 2.5f));
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
            if (_addButton.containsPoint(x, y))
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
        /// 打开添加记忆的输入对话框
        /// </summary>
        private void OpenAddMemoryDialog()
        {
            // 检查是否已满
            int count = MemoryManager.Instance.GetMemoryCount(_npcName);
            if (count >= MemoryManager.MaxMemoriesPerNpc)
            {
                Game1.drawObjectDialogue($"该角色的记忆已满（{MemoryManager.MaxMemoriesPerNpc}/{MemoryManager.MaxMemoriesPerNpc}），请先删除不重要的记忆。");
                return;
            }

            // 打开输入菜单
            Game1.activeClickableMenu = new AddMemoryInputMenu(_npcName, this);
        }

        /// <summary>
        /// 确认删除记忆
        /// </summary>
        private void ConfirmDeleteMemory(MemoryEntry entry)
        {
            Game1.activeClickableMenu = new ConfirmationDialog(
                $"确定要删除记忆 \"{entry.Content}\" 吗？",
                (farmer) =>
                {
                    MemoryManager.Instance.RemoveMemory(_npcName, entry.Id);
                    RefreshEntries();
                    Game1.playSound("trashcan");
                    Game1.activeClickableMenu = this;
                });
        }

        private void RefreshEntries()
        {
            _entries.Clear();
            _entries.AddRange(MemoryManager.Instance.GetMemories(_npcName));
            _startIndex = 0;
            RefreshDeleteButtons();
            SetScrollbarPosition();
        }

        public override void draw(SpriteBatch spriteBatch)
        {
            // 背景
            spriteBatch.Draw(Game1.fadeToBlackRect, Game1.graphics.GraphicsDevice.Viewport.Bounds, Color.Black * 0.4f);
            IClickableMenu.drawTextureBox(spriteBatch, xPositionOnScreen - 16, yPositionOnScreen - 16, width + 32, height + 32, Color.White);
            IClickableMenu.drawTextureBox(spriteBatch, xPositionOnScreen - 8, yPositionOnScreen - 8, width + 16, height + 16, Color.White);
            Game1.drawDialogueBox(xPositionOnScreen, yPositionOnScreen, width, height, false, true);

            // 标题
            string title = $"与 {_npcName} 的记忆 ({_entries.Count}/{MemoryManager.MaxMemoriesPerNpc})";
            var titleSize = Game1.dialogueFont.MeasureString(title);
            spriteBatch.DrawString(Game1.dialogueFont, title,
                new Vector2(xPositionOnScreen + (width - titleSize.X) / 2, yPositionOnScreen + 24),
                Game1.textColor);

            // 空状态提示
            if (_entries.Count == 0)
            {
                string hint = "暂无记忆。点击下方按钮添加。";
                var hintSize = Game1.smallFont.MeasureString(hint);
                spriteBatch.DrawString(Game1.smallFont, hint,
                    new Vector2(xPositionOnScreen + (width - hintSize.X) / 2, yPositionOnScreen + 120),
                    Color.Gray);
            }
            else
            {
                // 记忆列表
                int maxLines = GetVisibleLineCount();
                for (int i = 0; i < maxLines && _startIndex + i < _entries.Count; i++)
                {
                    int entryIndex = _startIndex + i;
                    var entry = _entries[entryIndex];
                    int y = yPositionOnScreen + TopPadding + 10 + i * LineHeight;

                    // 编号 + 内容
                    string text = $"{entryIndex + 1}. {entry.Content}";
                    spriteBatch.DrawString(Game1.smallFont, text,
                        new Vector2(xPositionOnScreen + 40, y),
                        Game1.textColor);

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
            _addButton.draw(spriteBatch);
            string addText = "+ 添加记忆";
            var addTextSize = Game1.smallFont.MeasureString(addText);
            spriteBatch.DrawString(Game1.smallFont, addText,
                new Vector2(_addButton.bounds.X + (_addButton.bounds.Width - addTextSize.X) / 2,
                            _addButton.bounds.Y + 12),
                Game1.textColor);

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
    /// 添加记忆的小型输入菜单（复用 DialogueTextInputBox 风格的输入）
    /// </summary>
    internal class AddMemoryInputMenu : IClickableMenu
    {
        private readonly string _npcName;
        private readonly ScrollableMemoryMenu _returnMenu;
        private readonly DialogueTextInputBox _inputBox;
        private readonly ClickableTextureComponent _okButton;
        private readonly ClickableTextureComponent _cancelButton;

        private const int MenuWidth = 700;
        private const int MenuHeight = 300;

        public AddMemoryInputMenu(string npcName, ScrollableMemoryMenu returnMenu)
        {
            _npcName = npcName;
            _returnMenu = returnMenu;

            xPositionOnScreen = (Game1.uiViewport.Width - MenuWidth) / 2;
            yPositionOnScreen = (Game1.uiViewport.Height - MenuHeight) / 2;
            width = MenuWidth;
            height = MenuHeight;

            // 输入框
            _inputBox = new DialogueTextInputBox(200)
            {
                Position = new Vector2(xPositionOnScreen + 40, yPositionOnScreen + 100),
                Extent = new Vector2(width - 80, 60),
                Font = Game1.dialogueFont,
                TextColor = Game1.textColor,
                Selected = true
            };
            _inputBox.OnSubmit += (sender) => Submit(sender.Text);

            Game1.keyboardDispatcher.Subscriber = _inputBox;

            // 按钮
            _okButton = new ClickableTextureComponent(
                new Rectangle(xPositionOnScreen + width - 2 * 24 - 64, yPositionOnScreen + height - 80, 64, 64),
                Game1.mouseCursors, Game1.getSourceRectForStandardTileSheet(Game1.mouseCursors, 46, -1, -1), 1f);
            _cancelButton = new ClickableTextureComponent(
                new Rectangle(xPositionOnScreen + width - 3 * 24 - 2 * 64, yPositionOnScreen + height - 80, 64, 64),
                Game1.mouseCursors, Game1.getSourceRectForStandardTileSheet(Game1.mouseCursors, 47, -1, -1), 1f);
        }

        private void Submit(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return;

            var result = MemoryManager.Instance.AddMemory(_npcName, text.Trim());

            if (result == true)
            {
                Game1.playSound("coin");
            }
            else if (result == false)
            {
                // 已满
                Game1.playSound("cancel");
            }
            else // null = 重复
            {
                Game1.drawObjectDialogue("该记忆已存在。");
            }

            // 返回记忆管理菜单
            _returnMenu.RefreshEntries();
            Game1.activeClickableMenu = _returnMenu;
        }

        public override void receiveLeftClick(int x, int y, bool playSound = true)
        {
            base.receiveLeftClick(x, y, playSound);

            if (_okButton.containsPoint(x, y))
            {
                Submit(_inputBox.Text);
            }
            else if (_cancelButton.containsPoint(x, y))
            {
                Game1.playSound("bigDeSelect");
                Game1.activeClickableMenu = _returnMenu;
            }
        }

        public override void draw(SpriteBatch spriteBatch)
        {
            // 背景
            spriteBatch.Draw(Game1.fadeToBlackRect, Game1.graphics.GraphicsDevice.Viewport.Bounds, Color.Black * 0.4f);
            IClickableMenu.drawTextureBox(spriteBatch, xPositionOnScreen - 8, yPositionOnScreen - 8, width + 16, height + 16, Color.White);
            Game1.drawDialogueBox(xPositionOnScreen, yPositionOnScreen, width, height, false, true);

            // 标题
            string title = $"为 {_npcName} 添加记忆";
            var titleSize = Game1.dialogueFont.MeasureString(title);
            spriteBatch.DrawString(Game1.dialogueFont, title,
                new Vector2(xPositionOnScreen + (width - titleSize.X) / 2, yPositionOnScreen + 24),
                Game1.textColor);

            // 输入框
            _inputBox.Draw(spriteBatch);

            // 提示
            string hint = "输入你想让 NPC 记住的事（如：我喜欢草莓）";
            var hintSize = Game1.smallFont.MeasureString(hint);
            spriteBatch.DrawString(Game1.smallFont, hint,
                new Vector2(xPositionOnScreen + (width - hintSize.X) / 2, yPositionOnScreen + 180),
                Color.Gray);

            // 按钮
            _okButton.draw(spriteBatch);
            _cancelButton.draw(spriteBatch);

            // 鼠标
            drawMouse(spriteBatch);
        }

        protected override void cleanupBeforeExit()
        {
            base.cleanupBeforeExit();
            Game1.keyboardDispatcher.Subscriber = null;
        }
    }
}
