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
    internal interface IMemoryRefreshTarget
    {
        void RefreshEntries();
    }

    internal static class UiHelper
    {
        public static void UpdateButtonScale(ref float scale, ClickableTextureComponent btn, int mx, int my)
        {
            float target = btn.containsPoint(mx, my) ? 1.15f : 1.0f;
            scale += (target - scale) * 0.2f;
        }

        public static string TruncateString(string text, SpriteFont font, float maxWidth, float scale = 1f)
        {
            if (string.IsNullOrEmpty(text) || font.MeasureString(text).X * scale <= maxWidth)
                return text;

            const string ellipsis = "...";
            float targetWidth = maxWidth - (font.MeasureString(ellipsis).X * scale);
            if (targetWidth <= 0)
                return ellipsis;

            int low = 0;
            int high = text.Length;
            int best = 0;

            while (low <= high)
            {
                int mid = (low + high) / 2;
                if (font.MeasureString(text.Substring(0, mid)).X * scale <= targetWidth)
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

    internal class ScrollableMemoryMenu : IClickableMenu, IMemoryRefreshTarget
    {
        private readonly string _npcName;
        private readonly IClickableMenu _ownerMenu;
        private int _currentTab = 0;

        private Rectangle _tabNpcRect;
        private Rectangle _tabWorldRect;
        private Rectangle _callsignRect;

        private List<MemoryEntry> _cachedEntries = new();
        private List<MemoryEntry> ActiveEntries => _cachedEntries;

        private List<string> _distillCandidates;   // null = 本会话尚无有效缓存；持有后视为只读
        private readonly HashSet<string> _distillUsed = new(StringComparer.OrdinalIgnoreCase);

        private int MaxEntriesForTab =>
            _currentTab == 0
                ? MemoryManager.MaxMemoriesPerNpc
                : WorldMemoryManager.MaxEntries;

        private Rectangle _addButtonRect;
        private Rectangle _manualAddRect;        // 仅 _currentTab == 0 生效（居中）
        private Rectangle _aiExtractButtonRect;  // 仅 _currentTab == 0 生效（最左）
        private Rectangle _archiveButtonRect;    // 仅 _currentTab == 0 生效（最右）
        private int _archivedCount;
        private readonly ClickableTextureComponent _closeButton;
        private readonly List<ClickableTextureComponent> _deleteButtons = new();
        private readonly List<ClickableTextureComponent> _editButtons = new();

        private ClickableTextureComponent _upArrow;
        private ClickableTextureComponent _downArrow;
        private ClickableTextureComponent _scrollbar;
        private Rectangle _scrollbarRunner;

        private int _startIndex;
        private bool _scrolling;
        private int _hoveredRow = -1;

        private float _closeButtonHoverScale;
        private float _upArrowHoverScale;
        private float _downArrowHoverScale;

        private readonly float _closeButtonBaseScale;
        private readonly float _upArrowBaseScale;
        private readonly float _downArrowBaseScale;

        private const int MenuWidth = 1000;
        private const int MenuHeight = 600;
        private const int TabBarY = 56;
        private const int TabHeight = 36;
        private const int TabWidth = 200;
        private const int TabGap = 8;
        private const int TopPadding = 110;
        private const int BottomPadding = 75;
        private const int LineHeight = 46;
        private const int ButtonSize = 32; // 16x16 的 2 倍点对点整数缩放
        private const int LeftPadding = 40;
        private const int RightPadding = 40;

        public ScrollableMemoryMenu(string npcName, IClickableMenu parentMenu = null)
        {
            _npcName = npcName;
            _ownerMenu = parentMenu;

            xPositionOnScreen = (Game1.uiViewport.Width - MenuWidth) / 2;
            yPositionOnScreen = (Game1.uiViewport.Height - MenuHeight) / 2;
            width = MenuWidth;
            height = MenuHeight;

            _closeButton = new ClickableTextureComponent(
                new Rectangle(xPositionOnScreen + width - 60, yPositionOnScreen + 16, 44, 44),
                Game1.mouseCursors, new Rectangle(337, 494, 12, 12), 3.5f);

            _closeButtonBaseScale = 3.5f;
            _closeButton.hoverText = I18n.Memory.CloseButton();

            int bottomBtnY = yPositionOnScreen + height - 60;
            const int extractBtnW = 210;
            const int addBtnW = 210;
            const int archiveBtnW = 160;

            _aiExtractButtonRect = new Rectangle(
                xPositionOnScreen + LeftPadding,
                bottomBtnY,
                extractBtnW, 48);

            _manualAddRect = new Rectangle(
                xPositionOnScreen + (width - addBtnW) / 2,
                bottomBtnY,
                addBtnW, 48);

            _archiveButtonRect = new Rectangle(
                xPositionOnScreen + width - RightPadding - archiveBtnW,
                bottomBtnY,
                archiveBtnW, 48);

            _addButtonRect = new Rectangle(
                xPositionOnScreen + (width - 300) / 2,
                bottomBtnY,
                300, 48);

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

            int tabBaseX = xPositionOnScreen + LeftPadding;
            int tabBaseY = yPositionOnScreen + TabBarY;

            _tabNpcRect = new Rectangle(tabBaseX, tabBaseY, TabWidth, TabHeight);
            _tabWorldRect = new Rectangle(tabBaseX + TabWidth + TabGap, tabBaseY, TabWidth, TabHeight);

            _callsignRect = new Rectangle(
                xPositionOnScreen + width - RightPadding - 60 - 280,
                tabBaseY,
                280,
                TabHeight);

            RefreshEntries();

            exitFunction = () => Game1.playSound("bigDeSelect");
        }

        private void SwitchTab(int tab)
        {
            if (_currentTab == tab) return;

            _currentTab = tab;
            _startIndex = 0;
            Game1.playSound("smallSelect");
            RefreshEntries();
        }

        private void RefreshActionButtons()
        {
            ClampStartIndex();

            _deleteButtons.Clear();
            _editButtons.Clear();

            var entries = ActiveEntries;
            int visibleCount = GetVisibleLineCount();

            for (int i = 0; i < visibleCount && _startIndex + i < entries.Count; i++)
            {
                // 行垂直居中偏移计算: (LineHeight 46 - ButtonSize 32) / 2 = 7
                int y = yPositionOnScreen + TopPadding + 10 + i * LineHeight + 7;

                // 删除按钮：使用 FullSpritesheet 上的关闭/X 图标
                var del = new ClickableTextureComponent(
                    new Rectangle(xPositionOnScreen + width - RightPadding - 32, y, ButtonSize, ButtonSize),
                    ModEntry.CustomIcons,
                    IconSource.Trash(IconTheme.Wood, IconState.Normal),
                    2f);
                del.hoverText = I18n.Memory.DeleteButtonHover();
                _deleteButtons.Add(del);

                // 编辑按钮：使用 FullSpritesheet 上的铅笔图标
                var edit = new ClickableTextureComponent(
                    new Rectangle(xPositionOnScreen + width - RightPadding - 72, y, ButtonSize, ButtonSize),
                    ModEntry.CustomIcons,
                    IconSource.Edit(IconTheme.Wood, IconState.Normal),
                    2f);
                edit.hoverText = I18n.Memory.EditButtonHover();
                _editButtons.Add(edit);
            }
        }

        private void ClampStartIndex()
        {
            var entries = ActiveEntries;
            int maxLines = GetVisibleLineCount();
            int maxStart = Math.Max(0, entries.Count - maxLines);
            _startIndex = Math.Clamp(_startIndex, 0, maxStart);
        }

        private int GetVisibleLineCount()
            => (height - TopPadding - BottomPadding) / LineHeight;

        private void SetScrollbarPosition()
        {
            var entries = ActiveEntries;
            int maxLines = GetVisibleLineCount();

            if (entries.Count <= maxLines) return;

            float pct = (float)_startIndex / (entries.Count - maxLines);
            _scrollbar.bounds.Y = _scrollbarRunner.Y +
                (int)(pct * (_scrollbarRunner.Height - _scrollbar.bounds.Height));
        }

        public void RefreshEntries()
        {
            if (_currentTab == 0)
            {
                var raw = MemoryManager.Instance.GetMemories(_npcName);
                _cachedEntries = raw
                    .OrderByDescending(e => e.Category == MemoryCategory.Behavior)
                    .ToList();
            }
            else
            {
                _cachedEntries = WorldMemoryManager.Instance.GetEntries();
            }

            _startIndex = 0;
            RefreshActionButtons();
            SetScrollbarPosition();

            _archivedCount = _currentTab == 0
                ? MemoryManager.Instance.GetArchivedCount(_npcName)
                : 0;
        }

        public override void receiveScrollWheelAction(int direction)
        {
            base.receiveScrollWheelAction(direction);

            var entries = ActiveEntries;
            int maxLines = GetVisibleLineCount();

            if (direction > 0 && _startIndex > 0)
            {
                _startIndex--;
                Game1.playSound("shwip");
                SetScrollbarPosition();
                RefreshActionButtons();
            }
            else if (direction < 0 && _startIndex < Math.Max(0, entries.Count - maxLines))
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

            if (_tabNpcRect.Contains(x, y))
            {
                SwitchTab(0);
                return;
            }

            if (_tabWorldRect.Contains(x, y))
            {
                SwitchTab(1);
                return;
            }

            if (_currentTab == 0 && _callsignRect.Contains(x, y))
            {
                Game1.playSound("bigSelect");
                Game1.activeClickableMenu = new SetCallsignInputMenu(_npcName, this);
                return;
            }

            if (_currentTab == 0)
            {
                if (_aiExtractButtonRect.Contains(x, y))
                {
                    TryOpenDistillMenu();
                    return;
                }

                if (_manualAddRect.Contains(x, y))
                {
                    OpenAddMemoryDialog();
                    return;
                }

                if (_archiveButtonRect.Contains(x, y))
                {
                    Game1.playSound("bigSelect");
                    Game1.activeClickableMenu = new ArchivedMemoryMenu(_npcName, this);
                    return;
                }
            }
            else if (_addButtonRect.Contains(x, y))
            {
                OpenAddMemoryDialog();
                return;
            }

            var entries = ActiveEntries;

            for (int i = 0; i < _deleteButtons.Count; i++)
            {
                int idx = _startIndex + i;
                if (idx >= entries.Count) continue;

                if (_deleteButtons[i].containsPoint(x, y))
                {
                    ConfirmDeleteMemory(entries[idx]);
                    return;
                }

                if (i < _editButtons.Count && _editButtons[i].containsPoint(x, y))
                {
                    Game1.playSound("bigSelect");
                    Game1.activeClickableMenu = new AddMemoryInputMenu(
                        _npcName, this, entries[idx], _currentTab);
                    return;
                }
            }

            int maxLines = GetVisibleLineCount();

            if (entries.Count > maxLines)
            {
                if (_upArrow.containsPoint(x, y) && _startIndex > 0)
                {
                    _startIndex--;
                    Game1.playSound("shwip");
                    SetScrollbarPosition();
                    RefreshActionButtons();
                }
                else if (_downArrow.containsPoint(x, y) &&
                         _startIndex < entries.Count - maxLines)
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

        public override void leftClickHeld(int x, int y)
        {
            base.leftClickHeld(x, y);

            var entries = ActiveEntries;
            int maxLines = GetVisibleLineCount();

            if (_scrolling && entries.Count > maxLines)
            {
                int yPos = Math.Max(_scrollbarRunner.Y,
                    Math.Min(y, _scrollbarRunner.Bottom - _scrollbar.bounds.Height));

                float pct = (float)(yPos - _scrollbarRunner.Y) /
                            (_scrollbarRunner.Height - _scrollbar.bounds.Height);

                _startIndex = (int)(pct * (entries.Count - maxLines));

                SetScrollbarPosition();
                RefreshActionButtons();
            }
        }

        public override void releaseLeftClick(int x, int y)
        {
            base.releaseLeftClick(x, y);
            _scrolling = false;
        }

        private void OpenAddMemoryDialog()
        {
            int currentCount = _currentTab == 0
                ? MemoryManager.Instance.GetManualMemoryCount(_npcName)
                : ActiveEntries.Count;
            if (currentCount >= MaxEntriesForTab)
            {
                Game1.playSound("cancel");
                Game1.addHUDMessage(new HUDMessage(I18n.Memory.AddFailedFull(MaxEntriesForTab), 0));
                return;
            }

            Game1.playSound("bigSelect");
            Game1.activeClickableMenu = new AddMemoryInputMenu(_npcName, this, null, _currentTab);
        }

        internal void SetDistillCache(List<string> candidates)
        {
            if (candidates == null || candidates.Count == 0)
                return;
            _distillCandidates = candidates;
        }

        private void TryOpenDistillMenu()
        {
            string displayName = Game1.getCharacterFromName(_npcName)?.displayName ?? _npcName;

            if (DialogueBuilder.Instance?.LlmDisabled == true)
            {
                Game1.playSound("cancel");
                Game1.addHUDMessage(new HUDMessage(I18n.Memory.DistillLlmDisabled(), 3));
                return;
            }

            if (!DialogueHistoryManager.Instance.HasHistory(_npcName))
            {
                Game1.playSound("cancel");
                Game1.addHUDMessage(new HUDMessage(I18n.Memory.DistillNoHistory(displayName), 0));
                return;
            }

            Game1.playSound("bigSelect");
            Game1.activeClickableMenu = new MemoryDistillMenu(_npcName, this);
        }

        private void ConfirmDeleteMemory(MemoryEntry entry)
        {
            int tabSnapshot = _currentTab;

            // 限制在 320 像素以内，防止超长文本或无空格英文字符撑爆确认弹窗
            string safeContent = UiHelper.TruncateString(entry.Content, Game1.dialogueFont, 320f);

            Game1.activeClickableMenu = new ConfirmationDialog(
                I18n.Memory.DeleteConfirm(safeContent),
                _ =>
                {
                    if (tabSnapshot == 0)
                    {
                        MemoryManager.Instance.ArchiveMemory(_npcName, entry, "ManualDeleted");
                        MemoryManager.Instance.RemoveMemory(_npcName, entry.Id);
                    }
                    else
                        WorldMemoryManager.Instance.RemoveEntry(entry.Id);

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

            b.Draw(Game1.fadeToBlackRect,
                Game1.graphics.GraphicsDevice.Viewport.Bounds, Color.Black * 0.4f);

            IClickableMenu.drawTextureBox(b,
                xPositionOnScreen - 16, yPositionOnScreen - 16,
                width + 32, height + 32, Color.White);

            IClickableMenu.drawTextureBox(b,
                xPositionOnScreen - 8, yPositionOnScreen - 8,
                width + 16, height + 16, Color.White);

            Game1.drawDialogueBox(
                xPositionOnScreen, yPositionOnScreen, width, height, false, true);

            string title = I18n.Memory.MenuTitle();
            var titleSize = Game1.dialogueFont.MeasureString(title);

            b.DrawString(Game1.dialogueFont, title,
                new Vector2(xPositionOnScreen + (width - titleSize.X) / 2f,
                    yPositionOnScreen + 5),
                Game1.textColor);

            DrawTab(b, _tabNpcRect, I18n.Memory.TabNpc(_npcName), _currentTab == 0, mx, my);
            DrawTab(b, _tabWorldRect, I18n.Memory.TabWorld(), _currentTab == 1, mx, my);

            if (_currentTab == 0) DrawCallsignButton(b, mx, my);

            b.Draw(Game1.staminaRect,
                new Rectangle(xPositionOnScreen + LeftPadding,
                              yPositionOnScreen + TabBarY + TabHeight + 4,
                              width - LeftPadding - RightPadding, 2),
                Color.Gray * 0.5f);

            var entries = ActiveEntries;
            int visibleCount = GetVisibleLineCount();

            _hoveredRow = -1;

            if (entries.Count == 0)
            {
                string hint = _currentTab == 0
                    ? I18n.Memory.Empty()
                    : I18n.Memory.WorldEmpty();

                var hintSize = Game1.dialogueFont.MeasureString(hint);

                b.DrawString(Game1.dialogueFont, hint,
                    new Vector2(xPositionOnScreen + (width - hintSize.X) / 2f,
                                yPositionOnScreen + TopPadding + 60),
                    Color.Gray);
            }
            else
            {
                // 统一文字缩放为 0.8f
                const float fontScale = 0.8f;

                // 预留固定日期宽度，避开右侧按钮（RightPadding + 85px），保证日期严格右对齐
                float fixedDateWidth = Game1.smallFont.MeasureString("2026-12-31 00:00").X * fontScale;
                float dateX = xPositionOnScreen + width - RightPadding - 85 - fixedDateWidth;

                // 内容起始 X 与最右限制（在日期左侧保留 16px 缓冲）
                float contentStartX = xPositionOnScreen + LeftPadding;
                float maxContentWidth = (dateX - 16) - contentStartX;

                bool isLeftMouseDown = Mouse.GetState().LeftButton == Microsoft.Xna.Framework.Input.ButtonState.Pressed;

                for (int i = 0; i < visibleCount && _startIndex + i < entries.Count; i++)
                {
                    int idx   = _startIndex + i;
                    var entry = entries[idx];
                    int rowY  = yPositionOnScreen + TopPadding + 10 + i * LineHeight;

                    var rowRect = new Rectangle(
                        xPositionOnScreen + LeftPadding - 16, rowY - 4,
                        width - LeftPadding - RightPadding + 16, LineHeight - 2);

                    if (rowRect.Contains(mx, my))
                    {
                        _hoveredRow = i;
                        b.Draw(Game1.staminaRect, rowRect, new Color(70, 130, 180) * 0.18f);
                    }

                    string prefix;
                    Color textColor;
                    if (_currentTab == 0)
                    {
                        bool isRule = entry.Category == MemoryCategory.Behavior;
                        prefix = isRule ? I18n.Memory.RuleTag() : I18n.Memory.MemoryTag();
                        textColor = entry.Source == "Auto" ? new Color(130, 150, 170) : Game1.textColor;
                    }
                    else
                    {
                        bool isAuto = entry.Source == "Auto";
                        prefix = isAuto ? I18n.Memory.AutoPrefix() : "";
                        textColor = isAuto ? new Color(120, 140, 160) : Game1.textColor;
                    }

                    // 截断到日期位置前
                    string fullRawText = $"{idx + 1}. {prefix}{entry.Content}";
                    string text = UiHelper.TruncateString(fullRawText, Game1.dialogueFont, maxContentWidth, fontScale);

                    string dateText = entry.CreatedAt.ToString("yyyy-MM-dd HH:mm");

                    // 绘制记忆内容（缩放 0.8f，垂直居中微调）
                    b.DrawString(Game1.dialogueFont, text,
                        new Vector2(contentStartX, rowY + 4),
                        textColor, 0f, Vector2.Zero, fontScale, SpriteEffects.None, 0.88f);

                    // 绘制日期文本（缩放 0.8f）
                    b.DrawString(Game1.smallFont, dateText,
                        new Vector2(dateX, rowY + 6),
                        Color.Gray, 0f, Vector2.Zero, fontScale, SpriteEffects.None, 0.88f);

                    // 绘制编辑按钮（带点击凹陷与 1px 下沉动效）
                    if (i < _editButtons.Count)
                    {
                        var btn = _editButtons[i];
                        bool isPressed = isLeftMouseDown && btn.containsPoint(mx, my);
                        IconSource.DrawButton(
                            b,
                            ModEntry.CustomIcons,
                            btn.bounds,
                            col: 15, baseRow: 1,
                            theme: IconTheme.Wood,
                            isPressed: isPressed,
                            layerDepth: 0.89f);
                    }
                    
                    // 绘制删除按钮（带点击凹陷与 1px 下沉动效）
                    if (i < _deleteButtons.Count)
                    {
                        var btn = _deleteButtons[i];
                        bool isPressed = isLeftMouseDown && btn.containsPoint(mx, my);
                        IconSource.DrawButton(
                            b,
                            ModEntry.CustomIcons,
                            btn.bounds,
                            col: 6, baseRow: 1, // ★ 修改为垃圾桶坐标（第 1 行，第 6 列）
                            theme: IconTheme.Wood,
                            isPressed: isPressed,
                            layerDepth: 0.89f);
                    }
                }

                if (entries.Count > visibleCount && Game1.activeClickableMenu == this)
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

            string cap = _currentTab == 0
                ? $"{MemoryManager.Instance.GetManualMemoryCount(_npcName)} / {MemoryManager.MaxMemoriesPerNpc}"
                : $"{entries.Count} / {MaxEntriesForTab}";

            var capSize = Game1.smallFont.MeasureString(cap);
            b.DrawString(Game1.smallFont, cap,
                new Vector2(xPositionOnScreen + width - RightPadding - capSize.X,
                            yPositionOnScreen + TopPadding - 14),
                Color.Gray);

            if (_currentTab == 0)
            {
                string distillText = I18n.Memory.DistillButton();
                bool distillHover = _aiExtractButtonRect.Contains(mx, my);
                IClickableMenu.drawTextureBox(b,
                    _aiExtractButtonRect.X, _aiExtractButtonRect.Y,
                    _aiExtractButtonRect.Width, _aiExtractButtonRect.Height,
                    distillHover ? Color.Gold : Color.White);
                var distillLabelSize = Game1.smallFont.MeasureString(distillText);
                b.DrawString(Game1.smallFont, distillText,
                    new Vector2(
                        _aiExtractButtonRect.X + (_aiExtractButtonRect.Width - distillLabelSize.X) / 2f,
                        _aiExtractButtonRect.Y + (_aiExtractButtonRect.Height - distillLabelSize.Y) / 2f),
                    Game1.textColor);

                string manualText = I18n.Memory.AddButton();
                bool manualHover = _manualAddRect.Contains(mx, my);
                IClickableMenu.drawTextureBox(b,
                    _manualAddRect.X, _manualAddRect.Y,
                    _manualAddRect.Width, _manualAddRect.Height,
                    manualHover ? Color.Gold : Color.White);
                var manualLabelSize = Game1.smallFont.MeasureString(manualText);
                b.DrawString(Game1.smallFont, manualText,
                    new Vector2(
                        _manualAddRect.X + (_manualAddRect.Width - manualLabelSize.X) / 2f,
                        _manualAddRect.Y + (_manualAddRect.Height - manualLabelSize.Y) / 2f),
                    Game1.textColor);

                string archiveText = I18n.Memory.ArchiveButton(_archivedCount, MemoryManager.MaxArchivedMemoriesPerNpc);
                bool archiveHover = _archiveButtonRect.Contains(mx, my);
                IClickableMenu.drawTextureBox(b,
                    _archiveButtonRect.X, _archiveButtonRect.Y,
                    _archiveButtonRect.Width, _archiveButtonRect.Height,
                    archiveHover ? Color.Gold : Color.White);
                var archiveLabelSize = Game1.smallFont.MeasureString(archiveText);
                b.DrawString(Game1.smallFont, archiveText,
                    new Vector2(
                        _archiveButtonRect.X + (_archiveButtonRect.Width - archiveLabelSize.X) / 2f,
                        _archiveButtonRect.Y + (_archiveButtonRect.Height - archiveLabelSize.Y) / 2f),
                    Game1.textColor);
            }
            else
            {
                string addText = I18n.Memory.AddWorldButton();

                bool addHover = _addButtonRect.Contains(mx, my);
                Color addColor = addHover ? Color.Gold : Color.White;

                IClickableMenu.drawTextureBox(b,
                    _addButtonRect.X, _addButtonRect.Y,
                    _addButtonRect.Width, _addButtonRect.Height, addColor);

                var addLabelSize = Game1.smallFont.MeasureString(addText);

                b.DrawString(Game1.smallFont, addText,
                    new Vector2(
                        _addButtonRect.X + (_addButtonRect.Width - addLabelSize.X) / 2f,
                        _addButtonRect.Y + (_addButtonRect.Height - addLabelSize.Y) / 2f),
                    Game1.textColor);
            }

            UiHelper.UpdateButtonScale(ref _closeButtonHoverScale, _closeButton, mx, my);
            _closeButton.scale = _closeButtonBaseScale * _closeButtonHoverScale;
            _closeButton.draw(b);

            base.draw(b);

            // 悬停气泡提示渲染
            for (int i = 0; i < _deleteButtons.Count; i++)
            {
                if (_deleteButtons[i].containsPoint(mx, my))
                {
                    IClickableMenu.drawHoverText(b, _deleteButtons[i].hoverText, Game1.smallFont);
                    break;
                }
                if (i < _editButtons.Count && _editButtons[i].containsPoint(mx, my))
                {
                    IClickableMenu.drawHoverText(b, _editButtons[i].hoverText, Game1.smallFont);
                    break;
                }
            }

            drawMouse(b);
        }

        private static void DrawTab(
            SpriteBatch b, Rectangle rect, string label,
            bool isActive, int mx, int my)
        {
            Color bg = isActive ? new Color(210, 180, 140)
                     : rect.Contains(mx, my) ? new Color(255, 235, 205)
                     : new Color(139, 90, 43);

            IClickableMenu.drawTextureBox(b, Game1.mouseCursors,
                new Rectangle(432, 439, 9, 9),
                rect.X, rect.Y, rect.Width, rect.Height,
                bg, 4f, false);

            var labelSize = Game1.smallFont.MeasureString(label);

            b.DrawString(Game1.smallFont, label,
                new Vector2(
                    rect.X + (rect.Width - labelSize.X) / 2f,
                    rect.Y + (rect.Height - labelSize.Y) / 2f),
                isActive ? Game1.textColor : Color.White);
        }

        protected override void cleanupBeforeExit()
        {
            base.cleanupBeforeExit();

            if (_ownerMenu != null && Game1.activeClickableMenu == this)
                Game1.activeClickableMenu = _ownerMenu;
        }

        private void DrawCallsignButton(SpriteBatch b, int mx, int my)
        {
            bool hover     = _callsignRect.Contains(mx, my);
            string callsign = MemoryManager.Instance.GetCustomCallsign(_npcName);
            bool hasValue  = !string.IsNullOrEmpty(callsign);

            Color bg = hover ? new Color(255, 235, 205) : new Color(210, 180, 140) * 0.8f;
            IClickableMenu.drawTextureBox(b, Game1.mouseCursors,
                new Rectangle(432, 439, 9, 9),
                _callsignRect.X, _callsignRect.Y, _callsignRect.Width, _callsignRect.Height,
                bg, 4f, false);

            string prefix = I18n.Memory.CallsignPrefix();
            string valueText = hasValue ? $"[{callsign}]" : I18n.Memory.CallsignUnset();
            Color  valueColor = hover
                ? Game1.textColor
                : (hasValue ? Game1.textColor * 0.9f : Color.Gray);

            string fullText = prefix + valueText;
            var    textSize = Game1.smallFont.MeasureString(fullText);

            b.DrawString(Game1.smallFont, fullText,
                new Vector2(
                    _callsignRect.X + (_callsignRect.Width  - textSize.X) / 2f,
                    _callsignRect.Y + (_callsignRect.Height - textSize.Y) / 2f),
                valueColor);
        }
    }

    internal class SetCallsignInputMenu : IClickableMenu
    {
        private readonly string _npcName;
        private readonly IClickableMenu _returnMenu;
        private readonly DialogueTextInputBox _inputBox;
        private readonly ClickableTextureComponent _okButton;
        private readonly ClickableTextureComponent _cancelButton;

        private float _okButtonHoverScale = 1f;
        private float _cancelButtonHoverScale = 1f;
        private const int MenuWidth  = 560;
        private const int MenuHeight = 240;

        public SetCallsignInputMenu(string npcName, IClickableMenu returnMenu)
        {
            _npcName    = npcName;
            _returnMenu = returnMenu;

            xPositionOnScreen = (Game1.uiViewport.Width  - MenuWidth)  / 2;
            yPositionOnScreen = (Game1.uiViewport.Height - MenuHeight) / 2;
            width  = MenuWidth;
            height = MenuHeight;

            _inputBox = new DialogueTextInputBox(MemoryManager.MaxCallsignLength, 15)
            {
                Position  = new Vector2(xPositionOnScreen + 40, yPositionOnScreen + 100),
                Extent    = new Vector2(width - 80, 50),
                Font      = Game1.dialogueFont,
                TextColor = Game1.textColor,
                Selected  = true
            };

            string current = MemoryManager.Instance.GetCustomCallsign(_npcName);
            if (!string.IsNullOrEmpty(current))
                _inputBox.SetText(current);

            _inputBox.OnSubmit += sender => Submit(sender.Text);
            Game1.keyboardDispatcher.Subscriber = _inputBox;

            int btnY = yPositionOnScreen + height - 70;
            _okButton = new ClickableTextureComponent(
                new Rectangle(xPositionOnScreen + width - 2 * 24 - 54, btnY, 54, 54),
                Game1.mouseCursors,
                new Rectangle(128, 256, 64, 64), 0.85f);

            _cancelButton = new ClickableTextureComponent(
                new Rectangle(xPositionOnScreen + width - 3 * 24 - 2 * 54, btnY, 54, 54),
                Game1.mouseCursors,
                new Rectangle(192, 256, 64, 64), 0.85f);

            exitFunction = () =>
            {
                if (Game1.keyboardDispatcher.Subscriber == _inputBox)
                    Game1.keyboardDispatcher.Subscriber = null;
            };
        }

        private void Submit(string text)
        {
            MemoryManager.Instance.SetCustomCallsign(_npcName, text);
            Game1.playSound("coin");
            ReturnToMemoryMenu();
        }

        private void ReturnToMemoryMenu()
        {
            exitThisMenu();
            Game1.activeClickableMenu = _returnMenu;
        }

        public override void receiveLeftClick(int x, int y, bool playSound = true)
        {
            base.receiveLeftClick(x, y, playSound);

            if (_inputBox.ReceiveLeftClick(x, y)) return;

            if (_okButton.containsPoint(x, y))
            {
                Submit(_inputBox.Text);
            }
            else if (_cancelButton.containsPoint(x, y))
            {
                Game1.playSound("bigDeSelect");
                ReturnToMemoryMenu();
            }
        }

        public override void receiveKeyPress(Keys key)
        {
            if (Game1.keyboardDispatcher.Subscriber == _inputBox)
            {
                if (key == Keys.Escape)
                {
                    Game1.playSound("bigDeSelect");
                    ReturnToMemoryMenu();
                    return;
                }

                if (key == Keys.Enter)
                {
                    Submit(_inputBox.Text);
                    return;
                }

                if (!DialogueTextInputBox.IsControlKeyDown())
                    _inputBox.RecieveSpecialInput(key);

                return;
            }

            base.receiveKeyPress(key);
        }

        public override void draw(SpriteBatch b)
        {
            _inputBox.Update(Game1.currentGameTime);

            b.Draw(Game1.fadeToBlackRect, Game1.graphics.GraphicsDevice.Viewport.Bounds, Color.Black * 0.4f);
            IClickableMenu.drawTextureBox(b, xPositionOnScreen, yPositionOnScreen, width, height, Color.White);

            string title = I18n.Memory.CallsignTitle(_npcName);
            string hint  = I18n.Memory.CallsignHint();

            var titleSize = Game1.dialogueFont.MeasureString(title);
            b.DrawString(Game1.dialogueFont, title,
                new Vector2(xPositionOnScreen + (width - titleSize.X) / 2f, yPositionOnScreen + 18),
                Game1.textColor);

            var hintSize = Game1.smallFont.MeasureString(hint);
            b.DrawString(Game1.smallFont, hint,
                new Vector2(xPositionOnScreen + (width - hintSize.X) / 2f, yPositionOnScreen + 58),
                Color.Gray);

            _inputBox.Draw(b);

            int mx = Game1.getMouseX();
            int my = Game1.getMouseY();

            UiHelper.UpdateButtonScale(ref _okButtonHoverScale, _okButton, mx, my);
            UiHelper.UpdateButtonScale(ref _cancelButtonHoverScale, _cancelButton, mx, my);

            _okButton.scale = 0.85f * _okButtonHoverScale;
            _cancelButton.scale = 0.85f * _cancelButtonHoverScale;

            _okButton.draw(b);
            _cancelButton.draw(b);
            drawMouse(b);
        }
    }

    internal class AddMemoryInputMenu : IClickableMenu
    {
        private readonly string _npcName;
        private readonly IClickableMenu _returnMenu;
        private readonly DialogueTextInputBox _inputBox;
        private readonly ClickableTextureComponent _okButton;
        private readonly ClickableTextureComponent _cancelButton;
        private readonly MemoryEntry _existingEntry;
        private readonly int _tab;
        private readonly Func<string, MemoryOperationResult> _customSubmit;

        private MemoryCategory _category;
        private Rectangle _factCapsuleRect;
        private Rectangle _behaviorCapsuleRect;

        private const int MenuWidth = 600;
        private const int MenuHeight = 344;
        private const int CharacterLimit = 60;
        private const int WarningThreshold = 50;

        private float _okButtonHoverScale = 1f;
        private float _cancelButtonHoverScale = 1f;

        private readonly float _okButtonBaseScale;
        private readonly float _cancelButtonBaseScale;

        public AddMemoryInputMenu(
            string npcName,
            IClickableMenu returnMenu,
            MemoryEntry existingEntry = null,
            int tab = 0,
            Func<string, MemoryOperationResult> customSubmit = null)
        {
            _npcName = npcName;
            _returnMenu = returnMenu;
            _existingEntry = existingEntry;
            _tab = tab;
            _customSubmit = customSubmit;

            _category = (existingEntry != null && existingEntry.Category == MemoryCategory.Behavior)
                ? MemoryCategory.Behavior
                : MemoryCategory.Fact;

            xPositionOnScreen = (Game1.uiViewport.Width - MenuWidth) / 2;
            yPositionOnScreen = (Game1.uiViewport.Height - MenuHeight) / 2;
            width = MenuWidth;
            height = MenuHeight;

            int lineHeight = Game1.dialogueFont.LineSpacing;

            _inputBox = new DialogueTextInputBox(CharacterLimit, WarningThreshold)
            {
                Position = new Vector2(xPositionOnScreen + 40,
                                       yPositionOnScreen + 100 + lineHeight),
                Extent = new Vector2(width - 80, 60),
                Font = Game1.dialogueFont,
                TextColor = Game1.textColor,
                Selected  = true
            };

            int capsuleY = yPositionOnScreen + 100 + lineHeight + 76;
            _factCapsuleRect = new Rectangle(xPositionOnScreen + 40, capsuleY, 250, 36);
            _behaviorCapsuleRect = new Rectangle(xPositionOnScreen + 40 + 250 + 12, capsuleY, 250, 36);

            if (_existingEntry != null)
                _inputBox.SetText(_existingEntry.Content);

            _inputBox.OnSubmit += sender => Submit(sender.Text);
            Game1.keyboardDispatcher.Subscriber = _inputBox;

            int btnY = yPositionOnScreen + height - 80 + lineHeight;

            _okButton = new ClickableTextureComponent(
                new Rectangle(xPositionOnScreen + width - 3 * 24 - 2 * 64, btnY, 64, 64),
                Game1.mouseCursors,
                new Rectangle(128, 256, 64, 64), 1f);
            _okButtonBaseScale = 1f;

            _cancelButton = new ClickableTextureComponent(
                new Rectangle(xPositionOnScreen + width - 2 * 24 - 64, btnY, 64, 64),
                Game1.mouseCursors,
                new Rectangle(192, 256, 64, 64), 1f);
            _cancelButtonBaseScale = 1f;
        }

        private void ReturnToMemoryMenu(bool refresh)
        {
            if (Game1.keyboardDispatcher.Subscriber == _inputBox)
                Game1.keyboardDispatcher.Subscriber = null;

            if (refresh && _returnMenu is IMemoryRefreshTarget refreshable)
                refreshable.RefreshEntries();

            Game1.activeClickableMenu = _returnMenu;
        }

        private void ShowErrorHud(string message)
        {
            Game1.playSound("cancel");
            Game1.addHUDMessage(new HUDMessage(message, 0));
        }

        private void Submit(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                Game1.playSound("cancel");
                return;
            }

            string trimmed = text.Trim();
            MemoryOperationResult result;

            if (_tab == 1)
            {
                result = _existingEntry != null
                    ? WorldMemoryManager.Instance.EditEntry(_existingEntry.Id, trimmed)
                    : WorldMemoryManager.Instance.AddEntry(trimmed);
            }
            else
            {
                result = _existingEntry != null
                    ? MemoryManager.Instance.EditMemory(_npcName, _existingEntry.Id, trimmed, _category)
                    : MemoryManager.Instance.AddMemory(_npcName, trimmed, _category);
            }

            int maxLen = _tab == 1
                ? WorldMemoryManager.MaxEntryLength
                : MemoryManager.Instance.GetMaxMemoryLength();

            int maxCount = _tab == 1
                ? WorldMemoryManager.MaxEntries
                : MemoryManager.MaxMemoriesPerNpc;

            // 自定义提交（如 Timeline 浓缩确认）：复用既有结果 HUD，不触碰 _tab 分支
            if (_customSubmit != null)
            {
                var r = _customSubmit(trimmed);
                switch (r)
                {
                    case MemoryOperationResult.Success:
                        Game1.playSound("coin");
                        ReturnToMemoryMenu(true);
                        return;

                    case MemoryOperationResult.CapacityFull:
                        ShowErrorHud(I18n.Memory.AddFailedFull(maxCount));
                        return;

                    case MemoryOperationResult.TooLong:
                        ShowErrorHud(I18n.Memory.AddFailedTooLong(maxLen));
                        return;

                    case MemoryOperationResult.Duplicate:
                        ShowErrorHud(I18n.Memory.AddFailedDuplicate());
                        return;

                    default:
                        ShowErrorHud(I18n.Memory.DistillFailed());
                        return;
                }
            }

            switch (result)
            {
                case MemoryOperationResult.Success:
                    Game1.playSound("coin");
                    break;

                case MemoryOperationResult.CapacityFull:
                    ShowErrorHud(I18n.Memory.AddFailedFull(maxCount));
                    return;

                case MemoryOperationResult.TooLong:
                    ShowErrorHud(I18n.Memory.AddFailedTooLong(maxLen));
                    return;

                case MemoryOperationResult.Duplicate:
                    ShowErrorHud(I18n.Memory.AddFailedDuplicate());
                    return;

                default:
                    ShowErrorHud(I18n.Memory.AddFailedDuplicate());
                    return;
            }

            ReturnToMemoryMenu(true);
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

            if (_tab == 0 && _customSubmit == null && (_factCapsuleRect.Contains(x, y) || _behaviorCapsuleRect.Contains(x, y)))
            {
                var clicked = _behaviorCapsuleRect.Contains(x, y)
                    ? MemoryCategory.Behavior
                    : MemoryCategory.Fact;
                if (clicked != _category)
                {
                    _category = clicked;
                    Game1.playSound("smallSelect");
                }
                return;
            }

            if (_okButton.containsPoint(x, y))
            {
                Submit(_inputBox.Text);
            }
            else if (_cancelButton.containsPoint(x, y))
            {
                Game1.playSound("bigDeSelect");
                ReturnToMemoryMenu(false);
            }
        }

        public override void receiveKeyPress(Keys key)
        {
            if (Game1.keyboardDispatcher.Subscriber == _inputBox)
            {
                if (key == Keys.Escape)
                {
                    Game1.playSound("bigDeSelect");
                    ReturnToMemoryMenu(false);
                    return;
                }

                if (key == Keys.Enter)
                {
                    Submit(_inputBox.Text);
                    return;
                }

                if (!DialogueTextInputBox.IsControlKeyDown())
                    _inputBox.RecieveSpecialInput(key);

                return;
            }

            base.receiveKeyPress(key);
        }

        public override void draw(SpriteBatch b)
        {
            _inputBox.Update(Game1.currentGameTime);

            b.Draw(Game1.fadeToBlackRect,
                Game1.graphics.GraphicsDevice.Viewport.Bounds, Color.Black * 0.4f);

            IClickableMenu.drawTextureBox(b,
                xPositionOnScreen, yPositionOnScreen, width, height, Color.White);

            string title = _tab == 1
                ? (_existingEntry != null ? I18n.Memory.WorldEditTitle() : I18n.Memory.WorldAddTitle())
                : (_existingEntry != null
                    ? I18n.Memory.EditTitle(_npcName)
                    : I18n.Memory.AddTitle(_npcName));

            var titleSize = Game1.dialogueFont.MeasureString(title);

            b.DrawString(Game1.dialogueFont, title,
                new Vector2(xPositionOnScreen + (width - titleSize.X) / 2f,
                            yPositionOnScreen + 20),
                Game1.textColor);

            string hint = _tab == 1
                ? I18n.Memory.WorldAddHint(WorldMemoryManager.MaxEntryLength)
                : I18n.Memory.AddHint(_npcName);

            var hintSize = Game1.smallFont.MeasureString(hint);

            b.DrawString(Game1.smallFont, hint,
                new Vector2(xPositionOnScreen + (width - hintSize.X) / 2f,
                            yPositionOnScreen + 20 + Game1.dialogueFont.LineSpacing),
                Color.Gray);

            _inputBox.Draw(b);

            int mx = Game1.getMouseX();
            int my = Game1.getMouseY();

            if (_tab == 0 && _customSubmit == null)
            {
                bool factSelected = _category == MemoryCategory.Fact;
                bool behaviorSelected = _category == MemoryCategory.Behavior;

                bool factHover = _factCapsuleRect.Contains(mx, my);
                bool behaviorHover = _behaviorCapsuleRect.Contains(mx, my);

                Color GetCapsuleBg(bool isSelected, bool isHover) =>
                    isSelected
                        ? (isHover ? new Color(255, 225, 120) : new Color(245, 205, 90))
                        : (isHover ? new Color(175, 125, 75) : new Color(139, 90, 43));

                Color GetCapsuleTextColor(bool isSelected, bool isHover) =>
                    isSelected
                        ? Game1.textColor
                        : (isHover ? Color.White : Color.White * 0.85f);

                Color factBg = GetCapsuleBg(factSelected, factHover);
                Color behaviorBg = GetCapsuleBg(behaviorSelected, behaviorHover);

                IClickableMenu.drawTextureBox(b,
                    _factCapsuleRect.X, _factCapsuleRect.Y, _factCapsuleRect.Width, _factCapsuleRect.Height,
                    factBg);
                IClickableMenu.drawTextureBox(b,
                    _behaviorCapsuleRect.X, _behaviorCapsuleRect.Y, _behaviorCapsuleRect.Width, _behaviorCapsuleRect.Height,
                    behaviorBg);

                string factLabel = I18n.Memory.CategoryFactLabel();
                string behaviorLabel = I18n.Memory.CategoryBehaviorLabel();

                var factSize = Game1.smallFont.MeasureString(factLabel);
                var behaviorSize = Game1.smallFont.MeasureString(behaviorLabel);

                Color factTextColor = GetCapsuleTextColor(factSelected, factHover);
                Color behaviorTextColor = GetCapsuleTextColor(behaviorSelected, behaviorHover);

                b.DrawString(Game1.smallFont, factLabel,
                    new Vector2(_factCapsuleRect.X + (_factCapsuleRect.Width - factSize.X) / 2f,
                                _factCapsuleRect.Y + (_factCapsuleRect.Height - factSize.Y) / 2f),
                    factTextColor);
                b.DrawString(Game1.smallFont, behaviorLabel,
                    new Vector2(_behaviorCapsuleRect.X + (_behaviorCapsuleRect.Width - behaviorSize.X) / 2f,
                                _behaviorCapsuleRect.Y + (_behaviorCapsuleRect.Height - behaviorSize.Y) / 2f),
                    behaviorTextColor);

                string hint2 = factSelected ? I18n.Memory.CategoryFactHint() : I18n.Memory.CategoryBehaviorHint();
                var hintSize2 = Game1.smallFont.MeasureString(hint2);
                int capsuleBottom = Math.Max(_factCapsuleRect.Bottom, _behaviorCapsuleRect.Bottom);
                b.DrawString(Game1.smallFont, hint2,
                    new Vector2(xPositionOnScreen + (width - hintSize2.X) / 2f,
                                capsuleBottom + 8),
                    Color.Gray);
            }

            UiHelper.UpdateButtonScale(ref _okButtonHoverScale, _okButton, mx, my);
            UiHelper.UpdateButtonScale(ref _cancelButtonHoverScale, _cancelButton, mx, my);

            _okButton.scale = _okButtonBaseScale * _okButtonHoverScale;
            _cancelButton.scale = _cancelButtonBaseScale * _cancelButtonHoverScale;

            _okButton.draw(b);
            _cancelButton.draw(b);

            drawMouse(b);
        }

        protected override void cleanupBeforeExit()
        {
            base.cleanupBeforeExit();

            if (Game1.keyboardDispatcher.Subscriber == _inputBox)
                Game1.keyboardDispatcher.Subscriber = null;
        }
    }
}