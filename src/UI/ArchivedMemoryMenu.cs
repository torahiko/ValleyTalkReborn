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

        private const int MenuWidth = 760;
        private const int MenuHeight = 560;
        private const int TopPadding = 110;
        private const int BottomPadding = 75;
        private const int LineHeight = 46;
        private const int LeftPadding = 40;
        private const int RightPadding = 40;
        private const int ButtonWidth = 74;
        private const int ButtonHeight = 36;
        private const int ButtonGap = 10;
        private const int RightReserved = ButtonWidth + 12 + ButtonWidth;
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

            _closeButton = new ClickableTextureComponent(
                new Rectangle(xPositionOnScreen + width - 60, yPositionOnScreen + 16, 44, 44),
                Game1.mouseCursors, new Rectangle(337, 494, 12, 12), 3.5f);
            _closeButtonBaseScale = 3.5f;
            _closeButton.hoverText = I18n.Memory.CloseButton();

            _upArrow = new ClickableTextureComponent(
                new Rectangle(xPositionOnScreen + width - 48,
                              yPositionOnScreen + TopPadding, 44, 48),
                Game1.mouseCursors, new Rectangle(421, 459, 11, 12), 4f);
            _upArrowBaseScale = 4f;

            _downArrow = new ClickableTextureComponent(
                new Rectangle(xPositionOnScreen + width - 48,
                              yPositionOnScreen + height - BottomPadding, 44, 48),
                Game1.mouseCursors, new Rectangle(421, 472, 11, 12), 4f);
            _downArrowBaseScale = 4f;

            _scrollbarRunner = new Rectangle(
                xPositionOnScreen + width - 32,
                yPositionOnScreen + TopPadding + 50,
                12,
                height - TopPadding - BottomPadding - 80);
            _scrollbar = new ClickableTextureComponent(
                new Rectangle(_scrollbarRunner.X - 6, _scrollbarRunner.Y, 24, 40),
                Game1.mouseCursors, new Rectangle(435, 463, 6, 10), 4f);

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
            for (int i = 0; i < visibleCount && _startIndex + i < _cachedEntries.Count; i++)
            {
                int y = yPositionOnScreen + TopPadding + 10 + i * LineHeight;
                int delX = xPositionOnScreen + width - RightPadding - ButtonWidth;
                int restoreX = delX - ButtonGap - ButtonWidth;
                _deleteRects.Add(new Rectangle(delX, y + (LineHeight - ButtonHeight) / 2, ButtonWidth, ButtonHeight));
                _restoreRects.Add(new Rectangle(restoreX, y + (LineHeight - ButtonHeight) / 2, ButtonWidth, ButtonHeight));
            }
        }

        private void ClampStartIndex()
        {
            int maxLines = GetVisibleLineCount();
            if (_startIndex > Math.Max(0, _cachedEntries.Count - maxLines))
                _startIndex = Math.Max(0, _cachedEntries.Count - maxLines);
            if (_startIndex < 0)
                _startIndex = 0;
        }

        private int GetVisibleLineCount() =>
            (height - TopPadding - BottomPadding - 20) / LineHeight;

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
            Game1.activeClickableMenu = new ConfirmationDialog(
                I18n.Memory.ArchiveDeleteConfirm(entry.Content),
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
            base.draw(b);

            b.Draw(Game1.staminaRect, Game1.graphics.GraphicsDevice.Viewport.Bounds, new Color(0, 0, 0) * 0.75f);

            IClickableMenu.drawTextureBox(b,
                xPositionOnScreen - 16, yPositionOnScreen - 16,
                width + 32, height + 32, Color.White);
            IClickableMenu.drawTextureBox(b,
                xPositionOnScreen - 8, yPositionOnScreen - 8,
                width + 16, height + 16, Color.White);
            Game1.drawDialogueBox(xPositionOnScreen, yPositionOnScreen, width, height, false, true);

            int mx = Game1.getMouseX();
            int my = Game1.getMouseY();

            // 标题
            string title = I18n.Memory.ArchiveTitle(_npcName);
            var titleSize = Game1.dialogueFont.MeasureString(title);
            b.DrawString(Game1.dialogueFont, title,
                new Vector2(xPositionOnScreen + (width - titleSize.X) / 2f, yPositionOnScreen + 24),
                Game1.textColor);

            // 🌟 归档记录指示器：置于列表窗口右上角（向下微调至 TopPadding - 14）
            string countText = I18n.Memory.ArchiveCount(_cachedEntries.Count, MemoryManager.MaxArchivedMemoriesPerNpc);
            var countSize = Game1.smallFont.MeasureString(countText);
            Color countColor = _cachedEntries.Count >= NearFullThreshold
                ? new Color(255, 185, 80)
                : Color.Gray;
            b.DrawString(Game1.smallFont, countText,
                new Vector2(xPositionOnScreen + width - RightPadding - countSize.X, yPositionOnScreen + TopPadding - 14),
                countColor);

            // 淘汰规则提示：移至底部边缘上方（-64px），避免与底框材质重叠
            string ruleHint = I18n.Memory.ArchiveRuleHint(MemoryManager.MaxArchivedMemoriesPerNpc);
            var ruleHintSize = Game1.smallFont.MeasureString(ruleHint);
            b.DrawString(Game1.smallFont, ruleHint,
                new Vector2(xPositionOnScreen + (width - ruleHintSize.X) / 2f, yPositionOnScreen + height - 64),
                Color.Gray);

            UiHelper.UpdateButtonScale(ref _closeButtonHoverScale, _closeButton, mx, my);
            _closeButton.scale = _closeButtonBaseScale * _closeButtonHoverScale;
            _closeButton.draw(b);

            if (_cachedEntries.Count == 0)
            {
                string emptyText = I18n.Memory.ArchiveEmpty();
                float fontScale = 0.65f; // 在这里调整字号缩放比例（1.0f 为原大）
                var emptySize = Game1.dialogueFont.MeasureString(emptyText) * fontScale;

                b.DrawString(
                    Game1.dialogueFont,
                    emptyText,
                    new Vector2(xPositionOnScreen + (width - emptySize.X) / 2f,
                        yPositionOnScreen + TopPadding + 60),
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

            int contentW = width - LeftPadding - RightPadding - RightReserved - 16;
            int visible = GetVisibleLineCount();
            for (int vis = 0; vis < visible && _startIndex + vis < _cachedEntries.Count; vis++)
            {
                int i = _startIndex + vis;
                int y = yPositionOnScreen + TopPadding + 10 + vis * LineHeight;
                var entry = _cachedEntries[i];

                b.Draw(Game1.staminaRect,
                    new Rectangle(xPositionOnScreen + LeftPadding, y, contentW, LineHeight - 2),
                    new Color(60, 60, 60) * 0.18f);

                bool isRule = entry.Category == MemoryCategory.Behavior;
                string tag = isRule ? I18n.Memory.RuleTag() : I18n.Memory.MemoryTag();
                Color contentColor = entry.Source == "Auto" ? new Color(130, 150, 170) : Game1.textColor;

                string content = $"{i + 1}. {tag}{entry.Content}";
                content = TruncateString(content, Game1.smallFont, contentW);
                b.DrawString(Game1.smallFont, content,
                    new Vector2(xPositionOnScreen + LeftPadding + 4, y + 4),
                    contentColor);

                string time = entry.ArchivedAt != default
                    ? entry.ArchivedAt.ToString("yyyy-MM-dd HH:mm")
                    : entry.CreatedAt.ToString("yyyy-MM-dd HH:mm");
                b.DrawString(Game1.smallFont, time,
                    new Vector2(xPositionOnScreen + LeftPadding + 4, y + 4 + Game1.smallFont.LineSpacing),
                    Color.Gray);

                if (_cachedEntries.Count >= MemoryManager.MaxArchivedMemoriesPerNpc &&
                    i == _cachedEntries.Count - 1)
                {
                    string endangeredTag = I18n.Memory.ArchiveEndangeredTag();
                    var endangeredSize = Game1.smallFont.MeasureString(endangeredTag);
                    b.DrawString(Game1.smallFont, endangeredTag,
                        new Vector2(xPositionOnScreen + LeftPadding + contentW - endangeredSize.X - 4,
                                    y + 4 + Game1.smallFont.LineSpacing),
                        new Color(230, 100, 70));
                }

                var restoreRect = _restoreRects[vis];
                var deleteRect = _deleteRects[vis];

                IClickableMenu.drawTextureBox(b,
                    restoreRect.X, restoreRect.Y, restoreRect.Width, restoreRect.Height,
                    restoreRect.Contains(mx, my) ? Color.Gold : Color.White);
                string restoreLabel = I18n.Memory.ArchiveRestoreButton();
                var restoreLabelSize = Game1.smallFont.MeasureString(restoreLabel);
                b.DrawString(Game1.smallFont, restoreLabel,
                    new Vector2(restoreRect.X + (restoreRect.Width - restoreLabelSize.X) / 2f,
                                restoreRect.Y + (restoreRect.Height - restoreLabelSize.Y) / 2f),
                    Game1.textColor);

                IClickableMenu.drawTextureBox(b,
                    deleteRect.X, deleteRect.Y, deleteRect.Width, deleteRect.Height,
                    deleteRect.Contains(mx, my) ? Color.Gold : Color.White);
                string deleteLabel = I18n.Memory.ArchiveDeleteButton();
                var deleteLabelSize = Game1.smallFont.MeasureString(deleteLabel);
                b.DrawString(Game1.smallFont, deleteLabel,
                    new Vector2(deleteRect.X + (deleteRect.Width - deleteLabelSize.X) / 2f,
                                deleteRect.Y + (deleteRect.Height - deleteLabelSize.Y) / 2f),
                    Game1.textColor);
            }

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

        private static string TruncateString(string text, SpriteFont font, float maxWidth)
        {
            if (string.IsNullOrEmpty(text) || font.MeasureString(text).X <= maxWidth)
                return text;

            const string ellipsis = "...";
            float targetWidth = maxWidth - font.MeasureString(ellipsis).X;
            if (targetWidth <= 0)
                return ellipsis;

            int low = 0;
            int high = text.Length;
            int best = 0;

            while (low <= high)
            {
                int mid = (low + high) / 2;
                if (font.MeasureString(text.Substring(0, mid)).X <= targetWidth)
                {
                    best = mid;
                    low = mid + 1;
                }
                else
                {
                    high = mid - 1;
                }
            }

            return text.Substring(0, best) + ellipsis;
        }
    }
}