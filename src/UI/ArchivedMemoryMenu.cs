using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewValley;
using StardewValley.Menus;
using StardewModdingAPI;
using System;
using System.Collections.Generic;
using System.Linq;
using ValleytalkReborn.UI;

namespace ValleytalkReborn
{
    internal class ArchivedMemoryMenu : IClickableMenu, IMemoryRefreshTarget
    {
        private readonly string _npcName;
        private readonly IClickableMenu _returnMenu;

        // 数据源注入
        private readonly Func<IReadOnlyList<MemoryEntry>> _listSource = null!;
        private readonly Func<string, MemoryOperationResult> _restoreAction = null!;
        private readonly Func<string, bool> _deleteAction = null!;
        private readonly int _capacity = 0;
        private readonly Func<string> _titleSource = null!;
        private readonly Func<string> _emptySource = null!;
        private readonly Func<string> _ruleHintSource = null!;
        private readonly Func<int> _clearAction = null!;
        private int NearFullThreshold => _capacity - 2;

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

        private readonly List<ClickableTextureComponent> _restoreButtons = new();
        private readonly List<ClickableTextureComponent> _deleteButtons = new();

        // 与 Timeline 同款木质风格清空按钮
        private Rectangle _clearButtonRect;

        // 布局参数
        private const int MenuWidth = 860;
        private const int MenuHeight = 600;
        private const int TopPadding = 100;
        private const int BottomPadding = 70;
        private const int LineHeight = 60;
        private const int LeftPadding = 36;
        private const int RightScrollArea = 52;

        private const int ButtonSize = 32;
        private const int ButtonGap = 8;

        public ArchivedMemoryMenu(string npcName, IClickableMenu returnMenu)
            : this(npcName, returnMenu,
                  () => MemoryManager.Instance.GetArchivedMemories(npcName),
                  id => MemoryManager.Instance.RestoreMemory(npcName, id),
                  id => MemoryManager.Instance.DeleteArchivedMemory(npcName, id),
                  MemoryManager.MaxArchivedMemoriesPerNpc,
                  () => I18n.Memory.ArchiveTitle(npcName),
                  () => I18n.Memory.ArchiveEmpty(),
                  () => I18n.Memory.ArchiveRuleHint(MemoryManager.MaxArchivedMemoriesPerNpc),
                  () => MemoryManager.Instance.ClearArchivedMemories(npcName))
        {
        }

        internal ArchivedMemoryMenu(string npcName, IClickableMenu returnMenu,
            Func<IReadOnlyList<MemoryEntry>> listSource,
            Func<string, MemoryOperationResult> restoreAction,
            Func<string, bool> deleteAction,
            int capacity,
            Func<string> titleSource,
            Func<string> emptySource,
            Func<string> ruleHintSource,
            Func<int> clearAction)
            : base(
                  (Game1.uiViewport.Width - MenuWidth) / 2,
                  (Game1.uiViewport.Height - MenuHeight) / 2,
                  MenuWidth,
                  MenuHeight,
                  false)
        {
            _npcName = npcName;
            _returnMenu = returnMenu;

            _listSource = listSource ?? throw new ArgumentNullException(nameof(listSource));
            _restoreAction = restoreAction ?? throw new ArgumentNullException(nameof(restoreAction));
            _deleteAction = deleteAction ?? throw new ArgumentNullException(nameof(deleteAction));
            _capacity = capacity;
            _titleSource = titleSource ?? throw new ArgumentNullException(nameof(titleSource));
            _emptySource = emptySource ?? throw new ArgumentNullException(nameof(emptySource));
            _ruleHintSource = ruleHintSource ?? throw new ArgumentNullException(nameof(ruleHintSource));
            _clearAction = clearAction ?? throw new ArgumentNullException(nameof(clearAction));

            // 右上角关闭按钮
            _closeButton = new ClickableTextureComponent(
                new Rectangle(xPositionOnScreen + width - 56, yPositionOnScreen + 16, 44, 44),
                Game1.mouseCursors, new Rectangle(337, 494, 12, 12), 3.5f)
            {
                hoverText = I18n.Memory.CloseButton()
            };
            _closeButtonBaseScale = 3.5f;

            // 滚动条与翻页箭头
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

            // 清空按钮布局：位于右上侧，滚动条左侧区域
            int clearW = 76;
            int clearH = 32;
            int clearX = xPositionOnScreen + width - RightScrollArea - clearW;
            int clearY = yPositionOnScreen + TopPadding - clearH - 6;
            _clearButtonRect = new Rectangle(clearX, clearY, clearW, clearH);

            RefreshEntries();
        }

        public void RefreshEntries()
        {
            _cachedEntries = _listSource().ToList();
            ClampStartIndex();
            SetScrollbarPosition();
            RefreshActionButtons();
        }

        private void RefreshActionButtons()
        {
            _restoreButtons.Clear();
            _deleteButtons.Clear();

            int visibleCount = GetVisibleLineCount();
            int delX = xPositionOnScreen + width - RightScrollArea - ButtonSize;
            int restoreX = delX - ButtonGap - ButtonSize;

            for (int i = 0; i < visibleCount && _startIndex + i < _cachedEntries.Count; i++)
            {
                int rowY = yPositionOnScreen + TopPadding + 6 + i * LineHeight;
                int btnY = rowY + (LineHeight - 4 - ButtonSize) / 2; // 垂直严格居中

                // 还原按钮（使用贴图集中的 Restore 旋转撤回箭头）
                var restoreBtn = new ClickableTextureComponent(
                    new Rectangle(restoreX, btnY, ButtonSize, ButtonSize),
                    ModEntry.CustomIcons,
                    IconSource.Restore(IconTheme.Wood, IconState.Normal),
                    2f)
                {
                    hoverText = I18n.Memory.ArchiveRestoreButton()
                };
                _restoreButtons.Add(restoreBtn);

                // 删除按钮（使用贴图集中的 Trash 垃圾桶）
                var delBtn = new ClickableTextureComponent(
                    new Rectangle(delX, btnY, ButtonSize, ButtonSize),
                    ModEntry.CustomIcons,
                    IconSource.Trash(IconTheme.Wood, IconState.Normal),
                    2f)
                {
                    hoverText = I18n.Memory.ArchiveDeleteButton()
                };
                _deleteButtons.Add(delBtn);
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

            // 清空按钮点击：无数据时播放 cancel 提示音；有数据时弹出二次确认框
            if (_clearButtonRect.Contains(x, y))
            {
                if (_cachedEntries.Count == 0)
                {
                    Game1.playSound("cancel");
                    return;
                }

                int countSnapshot = _cachedEntries.Count;
                Game1.playSound("bigSelect");
                Game1.activeClickableMenu = new ConfirmationDialog(
                    I18n.Memory.ArchiveClearConfirm(countSnapshot),
                    _ =>
                    {
                        _clearAction();
                        Game1.playSound("trashcan");
                        RefreshEntries();
                        Game1.activeClickableMenu = this;
                    },
                    _ =>
                    {
                        Game1.activeClickableMenu = this;
                    });
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
                if (i < _restoreButtons.Count && _restoreButtons[i].containsPoint(x, y))
                {
                    HandleRestore(_startIndex + i);
                    return;
                }
                if (i < _deleteButtons.Count && _deleteButtons[i].containsPoint(x, y))
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
            var result = _restoreAction(entry.Id);
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
            // 弹窗确认提示采用常规正文字号进行宽度测量与截断
            string safeContent = CustomFontManager.TruncateString(entry.Content, CustomFontManager.SizeRegular, 320f);
            Game1.activeClickableMenu = new ConfirmationDialog(
                I18n.Memory.ArchiveDeleteConfirm(safeContent),
                _ =>
                {
                    _deleteAction(entry.Id);
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

            // 背景遮罩
            b.Draw(Game1.fadeToBlackRect, Game1.graphics.GraphicsDevice.Viewport.Bounds, Color.Black * 0.45f);

            // 标准星露谷双层外边框与羊皮纸底框
            IClickableMenu.drawTextureBox(b,
                xPositionOnScreen - 16, yPositionOnScreen - 16,
                width + 32, height + 32, Color.White);
            IClickableMenu.drawTextureBox(b,
                xPositionOnScreen - 8, yPositionOnScreen - 8,
                width + 16, height + 16, Color.White);
            Game1.drawDialogueBox(xPositionOnScreen, yPositionOnScreen, width, height, false, true);

            // 标题（24f 整数大标题）
            string title = _titleSource();
            var titleSize = CustomFontManager.MeasureString(title, CustomFontManager.SizeTitle);
            CustomFontManager.DrawString(b, title,
                new Vector2(xPositionOnScreen + (width - titleSize.X) / 2f, yPositionOnScreen + 20),
                Game1.textColor, CustomFontManager.SizeTitle);

            // 归档记录容量文本（15f 整数小字）
            string countText = I18n.Memory.ArchiveCount(_cachedEntries.Count, _capacity);
            var countSize = CustomFontManager.MeasureString(countText, CustomFontManager.SizeSmall);
            Color countColor = _cachedEntries.Count >= NearFullThreshold
                ? new Color(255, 175, 70)
                : Color.Gray;

            CustomFontManager.DrawString(b, countText,
                new Vector2(_clearButtonRect.X - 12 - countSize.X,
                            _clearButtonRect.Y + (_clearButtonRect.Height - countSize.Y) / 2f),
                countColor, CustomFontManager.SizeSmall);

            // ─── 清空按钮（常态统一为木质样式，悬停一律高亮） ───
            bool clearHover = _clearButtonRect.Contains(mx, my);
            Color clearBg = clearHover ? new Color(255, 235, 205) : new Color(139, 90, 43);

            IClickableMenu.drawTextureBox(b, Game1.mouseCursors,
                new Rectangle(432, 439, 9, 9),
                _clearButtonRect.X, _clearButtonRect.Y,
                _clearButtonRect.Width, _clearButtonRect.Height,
                clearBg, 4f, false);

            string clearLabel = I18n.Memory.ArchiveClearButton();
            var clearLabelSize = CustomFontManager.MeasureString(clearLabel, CustomFontManager.SizeRegular);
            Vector2 clearTextPos = new Vector2(
                _clearButtonRect.X + (_clearButtonRect.Width - clearLabelSize.X) / 2f,
                _clearButtonRect.Y + (_clearButtonRect.Height - clearLabelSize.Y) / 2f);

            CustomFontManager.DrawString(b, clearLabel, clearTextPos, clearHover ? Game1.textColor : Color.White, CustomFontManager.SizeRegular);

            // 底部淘汰规则提示（15f 整数小字）
            string ruleHint = _ruleHintSource();
            var ruleHintSize = CustomFontManager.MeasureString(ruleHint, CustomFontManager.SizeSmall);
            CustomFontManager.DrawString(b, ruleHint,
                new Vector2(xPositionOnScreen + (width - ruleHintSize.X) / 2f, yPositionOnScreen + height - 56),
                Color.Gray * 0.85f, CustomFontManager.SizeSmall);

            // 关闭按钮
            UiHelper.UpdateButtonScale(ref _closeButtonHoverScale, _closeButton, mx, my);
            _closeButton.scale = _closeButtonBaseScale * _closeButtonHoverScale;
            _closeButton.draw(b);

            // 列表内容区域
            if (_cachedEntries.Count == 0)
            {
                string emptyText = _emptySource();
                var emptySize = CustomFontManager.MeasureString(emptyText, CustomFontManager.SizeRegular);

                CustomFontManager.DrawString(
                    b,
                    emptyText,
                    new Vector2(xPositionOnScreen + (width - emptySize.X) / 2f,
                                yPositionOnScreen + TopPadding + 80),
                    Color.Gray,
                    CustomFontManager.SizeRegular);
            }
            else
            {
                int visible = GetVisibleLineCount();
                int delX = xPositionOnScreen + width - RightScrollArea - ButtonSize;
                int restoreX = delX - ButtonGap - ButtonSize;

                float textStartX = xPositionOnScreen + LeftPadding + 8;
                float maxTextWidth = (restoreX - 16) - textStartX;

                bool isLeftMouseDown = Mouse.GetState().LeftButton == Microsoft.Xna.Framework.Input.ButtonState.Pressed;

                for (int vis = 0; vis < visible && _startIndex + vis < _cachedEntries.Count; vis++)
                {
                    int i = _startIndex + vis;
                    int rowY = yPositionOnScreen + TopPadding + 6 + vis * LineHeight;
                    var entry = _cachedEntries[i];

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

                    // 第一行：记忆文本（18f 规整正文）
                    string content = $"{i + 1}. {tag}{entry.Content}";
                    content = CustomFontManager.TruncateString(content, CustomFontManager.SizeRegular, maxTextWidth);
                    CustomFontManager.DrawString(b, content,
                        new Vector2(textStartX, rowY + 6),
                        contentColor, CustomFontManager.SizeRegular);

                    // 第二行：时间戳与预警（15f 规整小字）
                    string time = entry.ArchivedAt != default
                        ? entry.ArchivedAt.ToString("yyyy-MM-dd HH:mm")
                        : entry.CreatedAt.ToString("yyyy-MM-dd HH:mm");

                    CustomFontManager.DrawString(b, time,
                        new Vector2(textStartX, rowY + 34),
                        Color.Gray * 0.85f, CustomFontManager.SizeSmall);

                    if (_cachedEntries.Count >= _capacity && i == _cachedEntries.Count - 1)
                    {
                        float timeWidth = CustomFontManager.MeasureString(time, CustomFontManager.SizeSmall).X;
                        string endangeredTag = $"  •  {I18n.Memory.ArchiveEndangeredTag()}";
                        CustomFontManager.DrawString(b, endangeredTag,
                            new Vector2(textStartX + timeWidth, rowY + 34),
                            new Color(235, 95, 75), CustomFontManager.SizeSmall);
                    }

                    // 右侧操作按钮
                    if (vis < _restoreButtons.Count)
                    {
                        var btn = _restoreButtons[vis];
                        bool isPressed = isLeftMouseDown && btn.containsPoint(mx, my);
                        IconSource.DrawButton(b, btn, isPressed);
                    }

                    if (vis < _deleteButtons.Count)
                    {
                        var btn = _deleteButtons[vis];
                        bool isPressed = isLeftMouseDown && btn.containsPoint(mx, my);
                        IconSource.DrawButton(b, btn, isPressed);
                    }
                }

                // 滚动条与上下翻页
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
            }

            // 关闭按钮 Tooltip
            if (_closeButton.containsPoint(mx, my))
            {
                DrawHoverTextCustom(b, _closeButton.hoverText);
            }

            // 还原/删除按钮 Tooltip
            for (int i = 0; i < _restoreButtons.Count; i++)
            {
                if (_restoreButtons[i].containsPoint(mx, my))
                {
                    DrawHoverTextCustom(b, _restoreButtons[i].hoverText);
                    break;
                }
                if (i < _deleteButtons.Count && _deleteButtons[i].containsPoint(mx, my))
                {
                    DrawHoverTextCustom(b, _deleteButtons[i].hoverText);
                    break;
                }
            }

            base.draw(b);
            drawMouse(b);
        }

        /// <summary>
        /// 悬浮提示的自定义矢量渲染：使用整数字阶 SizeRegular (18f)
        /// </summary>
        private static void DrawHoverTextCustom(SpriteBatch b, string text)
        {
            var sz = CustomFontManager.MeasureString(text, CustomFontManager.SizeRegular);
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
            CustomFontManager.DrawString(b, text, new Vector2(x + 12, y + 12), Game1.textColor, CustomFontManager.SizeRegular);
        }
    }
}