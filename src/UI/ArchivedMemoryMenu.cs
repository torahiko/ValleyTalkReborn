using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewValley;
using StardewValley.Menus;
using StardewModdingAPI;
using System;
using System.Collections.Generic;
using System.Linq;

namespace ValleytalkReborn
{
    internal class ArchivedMemoryMenu : IClickableMenu, IMemoryRefreshTarget
    {
        private readonly string _npcName;
        private readonly IClickableMenu _returnMenu;

        private List<MemoryEntry> _cachedEntries = new();
        private int _startIndex;
        private bool _scrolling;

        private ClickableTextureComponent _closeButton;
        private ClickableTextureComponent _upArrow;
        private ClickableTextureComponent _downArrow;
        private ClickableTextureComponent _scrollbar;
        private Rectangle _scrollbarRunner;

        private float _closeButtonHoverScale;
        private float _upArrowHoverScale;
        private float _downArrowHoverScale;

        private readonly float _closeButtonBaseScale;
        private readonly float _upArrowBaseScale;
        private readonly float _downArrowBaseScale;

        private readonly List<Rectangle> _restoreRects = new();
        private readonly List<Rectangle> _deleteRects = new();

        // 布局参数精修：适度拓宽并增高，给予内容充足的呼吸空间
        private const int MenuWidth = 860;
        private const int MenuHeight = 600;
        private const int TopPadding = 100;
        private const int BottomPadding = 70;
        private const int LineHeight = 60; // 适配上下双行结构，彻底消除穿模
        private const int LeftPadding = 36;
        private const int RightScrollArea = 52; // 专为滚动条保留的右侧安全区

        private const int ButtonWidth = 72;
        private const int ButtonHeight = 32;
        private const int ButtonGap = 8;
        private const int NearFullThreshold = MemoryManager.MaxArchivedMemoriesPerNpc - 2;

        public ArchivedMemoryMenu(string npcName, IClickableMenu returnMenu)
            : base(
                  (Game1.uiViewport.Width - MenuWidth) / 2,
                  (Game1.uiViewport.Height - MenuHeight) / 2,
                  MenuWidth,
                  MenuHeight,
                  false)
        {
            _npcName = npcName;
            _returnMenu = returnMenu;

            // 右上角关闭按钮
            _closeButton = new ClickableTextureComponent(
                new Rectangle(xPositionOnScreen + width - 56, yPositionOnScreen + 16, 44, 44),
                Game1.mouseCursors, new Rectangle(337, 494, 12, 12), 3.5f);
            _closeButtonBaseScale = 3.5f;
            _closeButton.hoverText = I18n.Memory.CloseButton();

            // 滚动条与上下箭头（固定靠右，独立区域）
            _upArrow = new ClickableTextureComponent(
                new Rectangle(xPositionOnScreen + width - 44, yPositionOnScreen + TopPadding, 40, 44),
                Game1.mouseCursors, new Rectangle(421, 459, 11, 12), 3.5f);
            _upArrowBaseScale = 3.5f;

            _downArrow = new ClickableTextureComponent(
                new Rectangle(xPositionOnScreen + width - 44, yPositionOnScreen + height - BottomPadding, 40, 44),
                Game1.mouseCursors, new Rectangle(421, 472, 11, 12), 3.5f);
            _downArrowBaseScale = 3.5f;

            _scrollbarRunner = new Rectangle(
                xPositionOnScreen + width - 30,
                yPositionOnScreen + TopPadding + 46,
                10,
                height - TopPadding - BottomPadding - 68);

            _scrollbar = new ClickableTextureComponent(
                new Rectangle(_scrollbarRunner.X - 5, _scrollbarRunner.Y, 20, 36),
                Game1.mouseCursors, new Rectangle(435, 463, 6, 10), 3.5f);

            RefreshEntries();
        }

        public void RefreshEntries()
        {
            _cachedEntries = MemoryManager.Instance.GetArchivedMemories(_npcName);
            ClampStartIndex();
            SetScrollbarPosition();
            RefreshActionButtons();
        }

        private void RefreshActionButtons()
        {
            _restoreRects.Clear();
            _deleteRects.Clear();

            int visibleCount = GetVisibleLineCount();
            int delX = xPositionOnScreen + width - RightScrollArea - ButtonWidth;
            int restoreX = delX - ButtonGap - ButtonWidth;

            for (int i = 0; i < visibleCount && _startIndex + i < _cachedEntries.Count; i++)
            {
                int rowY = yPositionOnScreen + TopPadding + 6 + i * LineHeight;
                int btnY = rowY + (LineHeight - 4 - ButtonHeight) / 2;

                _deleteRects.Add(new Rectangle(delX, btnY, ButtonWidth, ButtonHeight));
                _restoreRects.Add(new Rectangle(restoreX, btnY, ButtonWidth, ButtonHeight));
            }
        }

        private void ClampStartIndex()
        {
            int maxLines = GetVisibleLineCount();
            int maxStart = Math.Max(0, _cachedEntries.Count - maxLines);
            _startIndex = Math.Clamp(_startIndex, 0, maxStart);
        }

        private int GetVisibleLineCount() =>
            (height - TopPadding - BottomPadding) / LineHeight;

        private void SetScrollbarPosition()
        {
            int maxLines = GetVisibleLineCount();
            if (_scrollbar != null && _cachedEntries.Count > maxLines)
            {
                float pct = _startIndex / (float)Math.Max(1, _cachedEntries.Count - maxLines);
                _scrollbar.bounds.Y = _scrollbarRunner.Y + (int)(pct * (_scrollbarRunner.Height - _scrollbar.bounds.Height));
            }
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
            else if (direction < 0 && _startIndex < Math.Max(0, _cachedEntries.Count - maxLines))
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

            int maxLines = GetVisibleLineCount();
            if (_upArrow.containsPoint(x, y) && _startIndex > 0)
            {
                _startIndex--;
                Game1.playSound("shwip");
                SetScrollbarPosition();
                RefreshActionButtons();
                return;
            }
            if (_downArrow.containsPoint(x, y) && _startIndex < Math.Max(0, _cachedEntries.Count - maxLines))
            {
                _startIndex++;
                Game1.playSound("shwip");
                SetScrollbarPosition();
                RefreshActionButtons();
                return;
            }

            if (_cachedEntries.Count > maxLines && (_scrollbarRunner.Contains(x, y) || _scrollbar.containsPoint(x, y)))
            {
                _scrolling = true;
                int yPos = Math.Max(_scrollbarRunner.Y, Math.Min(y, _scrollbarRunner.Bottom - _scrollbar.bounds.Height));
                float pct = (float)(yPos - _scrollbarRunner.Y) / (_scrollbarRunner.Height - _scrollbar.bounds.Height);
                _startIndex = (int)(pct * (_cachedEntries.Count - maxLines));
                ClampStartIndex();
                SetScrollbarPosition();
                RefreshActionButtons();
                return;
            }

            int visibleCount = GetVisibleLineCount();
            for (int i = 0; i < visibleCount && _startIndex + i < _cachedEntries.Count; i++)
            {
                if (i >= _restoreRects.Count) break;

                if (_restoreRects[i].Contains(x, y))
                {
                    HandleRestore(_startIndex + i);
                    return;
                }
                if (_deleteRects[i].Contains(x, y))
                {
                    HandleDelete(_startIndex + i);
                    return;
                }
            }
        }

        public override void leftClickHeld(int x, int y)
        {
            base.leftClickHeld(x, y);

            int maxLines = GetVisibleLineCount();
            if (_scrolling && _cachedEntries.Count > maxLines)
            {
                int yPos = Math.Max(_scrollbarRunner.Y, Math.Min(y, _scrollbarRunner.Bottom - _scrollbar.bounds.Height));
                float pct = (float)(yPos - _scrollbarRunner.Y) / (_scrollbarRunner.Height - _scrollbar.bounds.Height);
                _startIndex = (int)(pct * (_cachedEntries.Count - maxLines));
                ClampStartIndex();
                SetScrollbarPosition();
                RefreshActionButtons();
            }
        }

        public override void releaseLeftClick(int x, int y)
        {
            base.releaseLeftClick(x, y);
            _scrolling = false;
        }

        public override void receiveKeyPress(Keys key)
        {
            if (key == Keys.Escape)
            {
                Game1.playSound("bigDeSelect");
                exitThisMenu();
            }
        }

        protected override void cleanupBeforeExit()
        {
            base.cleanupBeforeExit();
            if (_returnMenu != null && Game1.activeClickableMenu == this)
                Game1.activeClickableMenu = _returnMenu;
            if (_returnMenu is IMemoryRefreshTarget r)
                r.RefreshEntries();
        }

        private void HandleRestore(int i)
        {
            MemoryEntry entry = _cachedEntries[i];
            var result = MemoryManager.Instance.RestoreMemory(_npcName, entry.Id);
            switch (result)
            {
                case MemoryOperationResult.Success:
                    Game1.playSound("coin");
                    RefreshEntries();
                    break;
                case MemoryOperationResult.CapacityFull:
                    Game1.playSound("cancel");
                    Game1.addHUDMessage(new HUDMessage(I18n.Memory.AddFailedFull(MemoryManager.MaxMemoriesPerNpc), 3));
                    break;
                case MemoryOperationResult.Duplicate:
                    Game1.playSound("cancel");
                    Game1.addHUDMessage(new HUDMessage(I18n.Memory.ArchiveRestoreDuplicate(), 0));
                    break;
                case MemoryOperationResult.NotFound:
                default:
                    Game1.playSound("cancel");
                    Game1.addHUDMessage(new HUDMessage(I18n.Memory.ArchiveRestoreNotFound(), 0));
                    RefreshEntries();
                    break;
            }
        }

        private void HandleDelete(int i)
        {
            MemoryEntry entry = _cachedEntries[i];
            string safeContent = UiHelper.TruncateString(entry.Content, Game1.dialogueFont, 320f);
            Game1.activeClickableMenu = new ConfirmationDialog(
                I18n.Memory.ArchiveDeleteConfirm(safeContent),
                _ =>
                {
                    MemoryManager.Instance.DeleteArchivedMemory(_npcName, entry.Id);
                    Game1.playSound("trashcan");
                    RefreshEntries();
                    Game1.activeClickableMenu = this;
                },
                _ =>
                {
                    Game1.activeClickableMenu = this;
                });
        }

        public override void draw(SpriteBatch b)
        {
            int mx = Game1.getMouseX();
            int my = Game1.getMouseY();

            // 背景遮罩（0.45f 通透自然）
            b.Draw(Game1.fadeToBlackRect, Game1.graphics.GraphicsDevice.Viewport.Bounds, Color.Black * 0.45f);

            // 标准星露谷边框与对话框
            IClickableMenu.drawTextureBox(b,
                xPositionOnScreen - 16, yPositionOnScreen - 16,
                width + 32, height + 32, Color.White);
            IClickableMenu.drawTextureBox(b,
                xPositionOnScreen - 8, yPositionOnScreen - 8,
                width + 16, height + 16, Color.White);
            Game1.drawDialogueBox(xPositionOnScreen, yPositionOnScreen, width, height, false, true);

            // 标题
            string title = I18n.Memory.ArchiveTitle(_npcName);
            var titleSize = Game1.dialogueFont.MeasureString(title);
            b.DrawString(Game1.dialogueFont, title,
                new Vector2(xPositionOnScreen + (width - titleSize.X) / 2f, yPositionOnScreen + 20),
                Game1.textColor);

            // 归档记录计数指示器（右上侧排版）
            string countText = I18n.Memory.ArchiveCount(_cachedEntries.Count, MemoryManager.MaxArchivedMemoriesPerNpc);
            var countSize = Game1.smallFont.MeasureString(countText);
            Color countColor = _cachedEntries.Count >= NearFullThreshold
                ? new Color(255, 175, 70)
                : Color.Gray;
            b.DrawString(Game1.smallFont, countText,
                new Vector2(xPositionOnScreen + width - RightScrollArea - countSize.X, yPositionOnScreen + TopPadding - 16),
                countColor);

            // 底部淘汰规则提示
            string ruleHint = I18n.Memory.ArchiveRuleHint(MemoryManager.MaxArchivedMemoriesPerNpc);
            var ruleHintSize = Game1.smallFont.MeasureString(ruleHint);
            b.DrawString(Game1.smallFont, ruleHint,
                new Vector2(xPositionOnScreen + (width - ruleHintSize.X) / 2f, yPositionOnScreen + height - 52),
                Color.Gray * 0.85f);

            // 关闭按钮
            UiHelper.UpdateButtonScale(ref _closeButtonHoverScale, _closeButton, mx, my);
            _closeButton.scale = _closeButtonBaseScale * _closeButtonHoverScale;
            _closeButton.draw(b);

            // 空列表占位提示
            if (_cachedEntries.Count == 0)
            {
                string emptyText = I18n.Memory.ArchiveEmpty();
                float fontScale = 0.75f;
                var emptySize = Game1.dialogueFont.MeasureString(emptyText) * fontScale;

                b.DrawString(
                    Game1.dialogueFont,
                    emptyText,
                    new Vector2(xPositionOnScreen + (width - emptySize.X) / 2f,
                                yPositionOnScreen + TopPadding + 80),
                    Color.Gray,
                    0f,
                    Vector2.Zero,
                    fontScale,
                    SpriteEffects.None,
                    1f);

                base.draw(b);
                drawMouse(b);
                return;
            }

            int visible = GetVisibleLineCount();
            int delX = xPositionOnScreen + width - RightScrollArea - ButtonWidth;
            int restoreX = delX - ButtonGap - ButtonWidth;

            // 内容文本最大可用宽度（保留左内边距与右侧按钮缓冲）
            float textStartX = xPositionOnScreen + LeftPadding + 8;
            float maxTextWidth = (restoreX - 16) - textStartX;

            for (int vis = 0; vis < visible && _startIndex + vis < _cachedEntries.Count; vis++)
            {
                int i = _startIndex + vis;
                int rowY = yPositionOnScreen + TopPadding + 6 + vis * LineHeight;
                var entry = _cachedEntries[i];

                // 整行卡片底块（整行覆盖至按钮右缘）
                var cardRect = new Rectangle(
                    xPositionOnScreen + LeftPadding,
                    rowY,
                    width - LeftPadding - RightScrollArea + 4,
                    LineHeight - 4);

                bool isHovered = cardRect.Contains(mx, my);
                Color rowBg = isHovered ? new Color(70, 130, 180) * 0.18f : new Color(0, 0, 0) * 0.12f;
                b.Draw(Game1.staminaRect, cardRect, rowBg);

                bool isRule = entry.Category == MemoryCategory.Behavior;
                string tag = isRule ? I18n.Memory.RuleTag() : I18n.Memory.MemoryTag();
                Color contentColor = entry.Source == "Auto" ? new Color(130, 150, 170) : Game1.textColor;

                // 1. 第一行：记忆文本（截断并居上显示）
                string content = $"{i + 1}. {tag}{entry.Content}";
                content = UiHelper.TruncateString(content, Game1.smallFont, maxTextWidth);
                b.DrawString(Game1.smallFont, content,
                    new Vector2(textStartX, rowY + 7),
                    contentColor);

                // 2. 第二行：时间戳 + 淘汰预警（并排显示，彻底消除挤压）
                string time = entry.ArchivedAt != default
                    ? entry.ArchivedAt.ToString("yyyy-MM-dd HH:mm")
                    : entry.CreatedAt.ToString("yyyy-MM-dd HH:mm");

                b.DrawString(Game1.smallFont, time,
                    new Vector2(textStartX, rowY + 32),
                    Color.Gray * 0.85f);

                if (_cachedEntries.Count >= MemoryManager.MaxArchivedMemoriesPerNpc &&
                    i == _cachedEntries.Count - 1)
                {
                    float timeWidth = Game1.smallFont.MeasureString(time).X;
                    string endangeredTag = $"  •  {I18n.Memory.ArchiveEndangeredTag()}";
                    b.DrawString(Game1.smallFont, endangeredTag,
                        new Vector2(textStartX + timeWidth, rowY + 32),
                        new Color(235, 95, 75));
                }

                // 3. 右侧操作按钮
                var restoreRect = _restoreRects[vis];
                var deleteRect = _deleteRects[vis];

                bool rHover = restoreRect.Contains(mx, my);
                IClickableMenu.drawTextureBox(b,
                    restoreRect.X, restoreRect.Y, restoreRect.Width, restoreRect.Height,
                    rHover ? Color.Gold : Color.White);
                string restoreLabel = I18n.Memory.ArchiveRestoreButton();
                var restoreLabelSize = Game1.smallFont.MeasureString(restoreLabel);
                b.DrawString(Game1.smallFont, restoreLabel,
                    new Vector2(restoreRect.X + (restoreRect.Width - restoreLabelSize.X) / 2f,
                                restoreRect.Y + (restoreRect.Height - restoreLabelSize.Y) / 2f),
                    rHover ? Game1.textColor : Game1.textColor * 0.9f);

                bool dHover = deleteRect.Contains(mx, my);
                IClickableMenu.drawTextureBox(b,
                    deleteRect.X, deleteRect.Y, deleteRect.Width, deleteRect.Height,
                    dHover ? Color.Gold : Color.White);
                string deleteLabel = I18n.Memory.ArchiveDeleteButton();
                var deleteLabelSize = Game1.smallFont.MeasureString(deleteLabel);
                b.DrawString(Game1.smallFont, deleteLabel,
                    new Vector2(deleteRect.X + (deleteRect.Width - deleteLabelSize.X) / 2f,
                                deleteRect.Y + (deleteRect.Height - deleteLabelSize.Y) / 2f),
                    dHover ? Game1.textColor : Game1.textColor * 0.9f);
            }

            // 滚动条与翻页按钮
            if (_cachedEntries.Count > visible)
            {
                UiHelper.UpdateButtonScale(ref _upArrowHoverScale, _upArrow, mx, my);
                UiHelper.UpdateButtonScale(ref _downArrowHoverScale, _downArrow, mx, my);

                _upArrow.scale = _upArrowBaseScale * _upArrowHoverScale;
                _downArrow.scale = _downArrowBaseScale * _downArrowHoverScale;

                _upArrow.draw(b);
                _downArrow.draw(b);

                IClickableMenu.drawTextureBox(b, Game1.mouseCursors,
                    new Rectangle(403, 383, 6, 6),
                    _scrollbarRunner.X, _scrollbarRunner.Y,
                    _scrollbarRunner.Width, _scrollbarRunner.Height,
                    Color.White, 4f, false);

                SetScrollbarPosition();
                _scrollbar.draw(b);
            }

            base.draw(b);
            drawMouse(b);
        }
    }
}