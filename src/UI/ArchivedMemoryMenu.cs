#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;
using ValleytalkReborn.UI;

namespace ValleytalkReborn
{
    /// <summary>
    /// 归档记忆管理菜单：
    /// 遵循 BioEditorMenu 视觉与动效规范 —— 羊皮纸无缝底板、立体按压下沉；
    /// 自动根据场景区分展示左上角图标（常规规则显示垃圾桶，TimelineChronicleMenu 保持收纳箱/肖像现状）；
    /// 彻底消除清空按钮边缘白缝；全面规范条目标签为 [小镇共识] / [行为守则] / [手帐] / [周记] / [大事记]。
    /// </summary>
    internal class ArchivedMemoryMenu : IClickableMenu, IMemoryRefreshTarget
    {
        // ── 尺寸与布局常量 ──
        private const int MenuWidth = 880;
        private const int MenuHeight = 620;
        private const int ContentPadding = 24;
        private const int HeaderH = 68;
        private const int FooterH = 50;
        private const int LineHeight = 62;
        private const int RightScrollArea = 48;
        private const int ButtonSize = 30;
        private const int ButtonGap = 8;

        // 统一整数字号（完全继承 CustomFontManager 规范）
        private const float TitleFontSize = CustomFontManager.SizeTitle;       // 24f Bold (顶栏大标题)
        private const float ButtonFontSize = CustomFontManager.SizeRegular;    // 18f Bold (主动作按钮)
        private const float ContentFontSize = CustomFontManager.SizeRegular;   // 18f Medium (记忆内容)
        private const float TipFontSize = CustomFontManager.SizeSmall;         // 15f Medium (时间戳、容量与提示)

        private readonly string _npcName;
        private readonly IClickableMenu _returnMenu;

        // 判断是否为 TimelineChronicleMenu 调用上下文
        private bool IsFromTimeline => _returnMenu != null && _returnMenu.GetType().Name.IndexOf("Timeline", StringComparison.OrdinalIgnoreCase) >= 0;

        // 肖像与关闭按钮
        private Texture2D? _npcPortrait;
        private Rectangle _portraitSmileRect;
        private ClickableTextureComponent _closeButton;
        private float _closeButtonHoverScale;
        private const float CloseButtonBaseScale = 3f;

        // 数据源与回调
        private readonly Func<IReadOnlyList<MemoryEntry>> _listSource;
        private readonly Func<string, MemoryOperationResult> _restoreAction;
        private readonly Func<string, bool> _deleteAction;
        private readonly int _capacity;
        private readonly Func<string> _titleSource;
        private readonly Func<string> _emptySource;
        private readonly Func<string> _ruleHintSource;
        private readonly Func<int> _clearAction;
        private int NearFullThreshold => _capacity - 2;

        private List<MemoryEntry> _cachedEntries = new();
        private int _startIndex;
        private bool _scrolling;

        // 滚动组件
        private ClickableTextureComponent _upArrow = null!;
        private ClickableTextureComponent _downArrow = null!;
        private ClickableTextureComponent _scrollbar = null!;
        private Rectangle _scrollbarRunner;
        private float _upArrowHoverScale;
        private float _downArrowHoverScale;
        private const float ArrowBaseScale = 3f;

        // 列表行内按钮
        private readonly List<ClickableTextureComponent> _restoreButtons = new();
        private readonly List<ClickableTextureComponent> _deleteButtons = new();

        // 顶栏操作区
        private Rectangle _clearButtonRect;
        private Rectangle _capacityPillRect;

        private string? _hoverText;

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
                  (Game1.uiViewport.Width - Math.Clamp(Game1.uiViewport.Width - 100, 820, MenuWidth)) / 2,
                  (Game1.uiViewport.Height - Math.Clamp(Game1.uiViewport.Height - 80, 560, MenuHeight)) / 2,
                  Math.Clamp(Game1.uiViewport.Width - 100, 820, MenuWidth),
                  Math.Clamp(Game1.uiViewport.Height - 80, 560, MenuHeight),
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

            _closeButton = new ClickableTextureComponent(
                new Rectangle(xPositionOnScreen + width - 50, yPositionOnScreen + 16, 36, 36),
                Game1.mouseCursors, new Rectangle(337, 494, 12, 12), CloseButtonBaseScale)
            {
                hoverText = I18n.Memory.CloseButton()
            };

            LoadNpcPortrait();
            Layout();
            RefreshEntries();
        }

        private void LoadNpcPortrait()
        {
            try
            {
                if (string.Equals(_npcName, "WORLD", StringComparison.OrdinalIgnoreCase))
                {
                    _npcPortrait = null;
                    _portraitSmileRect = Rectangle.Empty;
                    return;
                }

                var character = Game1.getCharacterFromName(_npcName);
                _npcPortrait = (character?.Portrait != null && !character.Portrait.IsDisposed)
                    ? character.Portrait
                    : Game1.content.Load<Texture2D>("Portraits\\" + _npcName);

                if (_npcPortrait != null)
                {
                    if (_npcPortrait.Width >= 128 && _npcPortrait.Height >= 64)
                        _portraitSmileRect = new Rectangle(64, 0, 64, 64);
                    else if (_npcPortrait.Width >= 64 && _npcPortrait.Height >= 128)
                        _portraitSmileRect = new Rectangle(0, 64, 64, 64);
                    else
                        _portraitSmileRect = new Rectangle(0, 0, Math.Min(64, _npcPortrait.Width), Math.Min(64, _npcPortrait.Height));
                }
            }
            catch
            {
                _npcPortrait = null;
                _portraitSmileRect = Rectangle.Empty;
            }
        }

        private void Layout()
        {
            _closeButton.bounds = new Rectangle(xPositionOnScreen + width - 50, yPositionOnScreen + 16, 36, 36);

            int listTop = yPositionOnScreen + HeaderH + 12;
            int listBottom = yPositionOnScreen + height - FooterH;

            // 顶栏右侧按钮排布：容量胶囊 + 清空归档按钮
            const int clearBtnW = 105;
            const int clearBtnH = 30;
            const int pillW = 110;
            const int pillH = 30;

            int rightEdge = _closeButton.bounds.Left - 10;
            _clearButtonRect = new Rectangle(rightEdge - clearBtnW, yPositionOnScreen + 20, clearBtnW, clearBtnH);
            _capacityPillRect = new Rectangle(_clearButtonRect.Left - 8 - pillW, yPositionOnScreen + 20, pillW, pillH);

            // 右侧滚动条区域
            int arrowSize = 36;
            int arrowX = xPositionOnScreen + width - ContentPadding - arrowSize + 4;

            _upArrow = new ClickableTextureComponent(
                new Rectangle(arrowX, listTop + 4, arrowSize, arrowSize),
                Game1.mouseCursors, new Rectangle(421, 459, 11, 12), ArrowBaseScale);

            _downArrow = new ClickableTextureComponent(
                new Rectangle(arrowX, listBottom - arrowSize - 4, arrowSize, arrowSize),
                Game1.mouseCursors, new Rectangle(421, 472, 11, 12), ArrowBaseScale);

            int trackY = _upArrow.bounds.Bottom + 6;
            int trackH = _downArrow.bounds.Y - 6 - trackY;

            _scrollbarRunner = new Rectangle(arrowX + (arrowSize - 8) / 2, trackY, 8, Math.Max(20, trackH));

            _scrollbar = new ClickableTextureComponent(
                new Rectangle(_scrollbarRunner.X - 4, _scrollbarRunner.Y, 16, 32),
                Game1.mouseCursors, new Rectangle(435, 463, 6, 10), ArrowBaseScale);
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
            int delX = xPositionOnScreen + width - ContentPadding - RightScrollArea - ButtonSize;
            int restoreX = delX - ButtonGap - ButtonSize;
            int listTop = yPositionOnScreen + HeaderH + 12;

            for (int i = 0; i < visibleCount && _startIndex + i < _cachedEntries.Count; i++)
            {
                int rowY = listTop + i * LineHeight;
                int btnY = rowY + (LineHeight - 6 - ButtonSize) / 2;

                var restoreBtn = new ClickableTextureComponent(
                    new Rectangle(restoreX, btnY, ButtonSize, ButtonSize),
                    ModEntry.CustomIcons,
                    IconSource.Restore(IconTheme.Wood, IconState.Normal),
                    2f)
                {
                    hoverText = I18n.Memory.ArchiveRestoreButton()
                };
                _restoreButtons.Add(restoreBtn);

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

        private int GetVisibleLineCount()
        {
            int listTop = yPositionOnScreen + HeaderH + 12;
            int listBottom = yPositionOnScreen + height - FooterH;
            return Math.Max(1, (listBottom - listTop) / LineHeight);
        }

        private void SetScrollbarPosition()
        {
            int maxLines = GetVisibleLineCount();
            if (_scrollbar != null && _cachedEntries.Count > maxLines)
            {
                float pct = _startIndex / (float)Math.Max(1, _cachedEntries.Count - maxLines);
                _scrollbar.bounds.Y = _scrollbarRunner.Y + (int)(pct * (_scrollbarRunner.Height - _scrollbar.bounds.Height));
            }
        }

        public override void update(GameTime time)
        {
            base.update(time);
            _hoverText = null;
        }

        public override void gameWindowSizeChanged(Rectangle oldBounds, Rectangle newBounds)
        {
            width = Math.Clamp(Game1.uiViewport.Width - 100, 820, MenuWidth);
            height = Math.Clamp(Game1.uiViewport.Height - 80, 560, MenuHeight);
            xPositionOnScreen = (Game1.uiViewport.Width - width) / 2;
            yPositionOnScreen = (Game1.uiViewport.Height - height) / 2;
            Layout();
            RefreshEntries();
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

            // 清空归档
            if (_clearButtonRect.Contains(x, y))
            {
                if (_cachedEntries.Count == 0)
                {
                    Game1.playSound("cancel");
                    return;
                }

                int countSnapshot = _cachedEntries.Count;
                Game1.playSound("bigSelect");
                Game1.activeClickableMenu = new BioValveWarningDialog(
                    this,
                    I18n.Memory.ArchiveClearConfirmTitle(),
                    I18n.Memory.ArchiveClearConfirmSubtitle(countSnapshot),
                    new List<string> { I18n.Memory.ArchiveClearConfirmWarning() },
                    I18n.Dialog.ConfirmDelete(),
                    () =>
                    {
                        _clearAction();
                        Game1.playSound("trashcan");
                        RefreshEntries();
                        Game1.activeClickableMenu = this;
                    },
                    I18n.Dialog.Keep(),
                    () => Game1.activeClickableMenu = this);
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
                UpdateScrollFromMouse(y);
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

        private void UpdateScrollFromMouse(int y)
        {
            int maxLines = GetVisibleLineCount();
            int yPos = Math.Max(_scrollbarRunner.Y, Math.Min(y, _scrollbarRunner.Bottom - _scrollbar.bounds.Height));
            float pct = (float)(yPos - _scrollbarRunner.Y) / (_scrollbarRunner.Height - _scrollbar.bounds.Height);
            _startIndex = (int)(pct * (_cachedEntries.Count - maxLines));
            ClampStartIndex();
            SetScrollbarPosition();
            RefreshActionButtons();
        }

        public override void leftClickHeld(int x, int y)
        {
            base.leftClickHeld(x, y);
            int maxLines = GetVisibleLineCount();
            if (_scrolling && _cachedEntries.Count > maxLines)
            {
                UpdateScrollFromMouse(y);
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
                return;
            }

            if (!Game1.options.doesInputListContain(Game1.options.menuButton, key))
                base.receiveKeyPress(key);
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
                    Game1.addHUDMessage(new HUDMessage(I18n.Memory.AddFailedFull(MemoryManager.MaxMemoriesPerNpc), HUDMessage.error_type));
                    break;
                case MemoryOperationResult.Duplicate:
                    Game1.playSound("cancel");
                    Game1.addHUDMessage(new HUDMessage(I18n.Memory.ArchiveRestoreDuplicate(), HUDMessage.error_type));
                    break;
                case MemoryOperationResult.NotFound:
                default:
                    Game1.playSound("cancel");
                    Game1.addHUDMessage(new HUDMessage(I18n.Memory.ArchiveRestoreNotFound(), HUDMessage.error_type));
                    RefreshEntries();
                    break;
            }
        }

        private void HandleDelete(int i)
        {
            MemoryEntry entry = _cachedEntries[i];
            string safeContent = CustomFontManager.TruncateString(entry.Content, TipFontSize, 500f);
            Game1.activeClickableMenu = new BioValveWarningDialog(
                this,
                I18n.Memory.ArchiveDeleteConfirmTitle(),
                I18n.Memory.ArchiveDeleteConfirmSubtitle(),
                new List<string> { safeContent },
                I18n.Dialog.ConfirmDelete(),
                () =>
                {
                    _deleteAction(entry.Id);
                    Game1.playSound("trashcan");
                    RefreshEntries();
                    Game1.activeClickableMenu = this;
                },
                I18n.Dialog.Keep(),
                () => Game1.activeClickableMenu = this);
        }

        // ★ 优化 3：条目标签统一核心方法（彻底废除 [记忆]，支持手帐/周记/大事记细分）
        private string GetEntryTag(MemoryEntry entry)
        {
            if (IsFromTimeline)
            {
                string catStr = entry.Category.ToString();
                string srcStr = entry.Source ?? string.Empty;

                // 1. 周记判断
                if (catStr.IndexOf("Week", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    srcStr.IndexOf("Week", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    catStr.Contains("周") || srcStr.Contains("周"))
                {
                    return "[周记] ";
                }

                // 2. 大事记判断
                if (catStr.IndexOf("Chronicle", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    catStr.IndexOf("Milestone", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    catStr.IndexOf("Major", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    catStr.IndexOf("Epoch", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    catStr.Contains("大事") || srcStr.Contains("大事") ||
                    srcStr.IndexOf("Milestone", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    return "[大事记] ";
                }

                // 3. 默认手帐
                return "[手帐] ";
            }

            // 规则/NPC 记忆管理场景：彻底告别 [记忆]
            if (entry.Category == MemoryCategory.Behavior)
            {
                return "[行为守则] ";
            }

            if (string.Equals(_npcName, "WORLD", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(entry.Source, "WORLD", StringComparison.OrdinalIgnoreCase))
            {
                return "[小镇共识] ";
            }

            return "[既定事实] ";
        }

        // ── 渲染管线 ──────────────────────────────────────────────────────────

        public override void draw(SpriteBatch b)
        {
            int mx = Game1.getMouseX();
            int my = Game1.getMouseY();

            // 1. 全屏半透明遮罩
            b.Draw(Game1.fadeToBlackRect, Game1.graphics.GraphicsDevice.Viewport.Bounds, Color.Black * 0.5f);

            // 2. 双层羊皮纸木框底板（无缝防黑边）
            IClickableMenu.drawTextureBox(b, xPositionOnScreen - 8, yPositionOnScreen - 8, width + 16, height + 16, Color.White);
            b.Draw(Game1.staminaRect, new Rectangle(xPositionOnScreen, yPositionOnScreen, width, height), new Color(245, 230, 205));
            b.Draw(Game1.menuTexture, new Rectangle(xPositionOnScreen, yPositionOnScreen, width, height),
                new Rectangle(64, 128, 64, 64), new Color(245, 230, 205));

            DrawHeader(b, mx, my);

            // 3. 归档记录列表
            int maxLines = GetVisibleLineCount();
            int listTop = yPositionOnScreen + HeaderH + 12;

            if (_cachedEntries.Count == 0)
            {
                string emptyText = _emptySource();
                var emptySize = CustomFontManager.MeasureString(emptyText, ContentFontSize);
                CustomFontManager.DrawString(
                    b, emptyText,
                    new Vector2(xPositionOnScreen + (width - emptySize.X) / 2f, listTop + 100),
                    BioEditorMenu.TextMuted, ContentFontSize);
            }
            else
            {
                int visible = maxLines;
                int delX = xPositionOnScreen + width - ContentPadding - RightScrollArea - ButtonSize;
                int restoreX = delX - ButtonGap - ButtonSize;

                float textStartX = xPositionOnScreen + ContentPadding + 14;
                float maxTextWidth = (restoreX - 16) - textStartX;
                bool isLeftMouseDown = IsLeftMouseDown();

                for (int vis = 0; vis < visible && _startIndex + vis < _cachedEntries.Count; vis++)
                {
                    int i = _startIndex + vis;
                    int rowY = listTop + vis * LineHeight;
                    var entry = _cachedEntries[i];

                    var cardRect = new Rectangle(
                        xPositionOnScreen + ContentPadding,
                        rowY,
                        width - (ContentPadding * 2) - RightScrollArea + 8,
                        LineHeight - 6);

                    bool isHovered = cardRect.Contains(mx, my);

                    // 卡片底层微阴影
                    b.Draw(Game1.staminaRect, new Rectangle(cardRect.X + 1, cardRect.Y + 2, cardRect.Width, cardRect.Height), Color.Black * 0.08f);

                    // 温润羊皮纸卡片底衬
                    Color cardBg = isHovered ? new Color(255, 248, 236) : new Color(250, 242, 230);
                    Color borderCol = isHovered ? new Color(210, 160, 60) : new Color(225, 205, 175);

                    b.Draw(Game1.staminaRect, new Rectangle(cardRect.X + 1, cardRect.Y + 1, cardRect.Width - 2, cardRect.Height - 2), cardBg);
                    IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(432, 439, 9, 9),
                        cardRect.X, cardRect.Y, cardRect.Width, cardRect.Height, borderCol, 2f, false);

                    // ★ 优化 3：获取规范统一后的条目标签
                    string tag = GetEntryTag(entry);
                    Color contentColor = entry.Source == "Auto" ? BioEditorMenu.TextSecondary : BioEditorMenu.TextPrimary;

                    // 第一行：正文（18f Medium）
                    string content = $"{i + 1}. {tag}{entry.Content}";
                    content = CustomFontManager.TruncateString(content, ContentFontSize, maxTextWidth);
                    CustomFontManager.DrawString(b, content, new Vector2(textStartX, rowY + 8), contentColor, ContentFontSize);

                    // 第二行：时间戳（15f Medium，TextMuted）
                    string time = entry.ArchivedAt != default
                        ? entry.ArchivedAt.ToString("yyyy-MM-dd HH:mm")
                        : entry.CreatedAt.ToString("yyyy-MM-dd HH:mm");

                    CustomFontManager.DrawString(b, time, new Vector2(textStartX, rowY + 34), BioEditorMenu.TextMuted, TipFontSize);

                    // 濒危被淘汰预警标
                    if (_cachedEntries.Count >= _capacity && i == _cachedEntries.Count - 1)
                    {
                        float timeWidth = CustomFontManager.MeasureString(time, TipFontSize).X;
                        string endangeredTag = $"  •  {I18n.Memory.ArchiveEndangeredTag()}";
                        CustomFontManager.DrawString(b, endangeredTag,
                            new Vector2(textStartX + timeWidth, rowY + 34),
                            BioEditorMenu.TextDanger, TipFontSize);
                    }

                    // 右侧动作按钮（Restore & Delete）
                    if (vis < _restoreButtons.Count)
                    {
                        var btn = _restoreButtons[vis];
                        bool isBtnHover = btn.containsPoint(mx, my);
                        bool isPressed = isLeftMouseDown && isBtnHover;
                        DrawRowIconButton(b, btn, isPressed, isBtnHover);
                        IconSource.DrawButton(b, btn, isPressed);
                    }

                    if (vis < _deleteButtons.Count)
                    {
                        var btn = _deleteButtons[vis];
                        bool isBtnHover = btn.containsPoint(mx, my);
                        bool isPressed = isLeftMouseDown && isBtnHover;
                        DrawRowIconButton(b, btn, isPressed, isBtnHover);
                        IconSource.DrawButton(b, btn, isPressed);
                    }
                }

                // 4. 滚动条与翻页箭头
                if (_cachedEntries.Count > visible)
                {
                    UiHelper.UpdateButtonScale(ref _upArrowHoverScale, _upArrow, mx, my);
                    UiHelper.UpdateButtonScale(ref _downArrowHoverScale, _downArrow, mx, my);

                    _upArrow.scale = ArrowBaseScale * _upArrowHoverScale;
                    _downArrow.scale = ArrowBaseScale * _downArrowHoverScale;

                    _upArrow.draw(b);
                    _downArrow.draw(b);

                    // 暖木色滑轨（彻底告别冷灰 403 槽）
                    b.Draw(Game1.staminaRect, _scrollbarRunner, new Color(230, 218, 202));
                    IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(432, 439, 9, 9),
                        _scrollbarRunner.X - 1, _scrollbarRunner.Y - 1, _scrollbarRunner.Width + 2, _scrollbarRunner.Height + 2,
                        new Color(215, 195, 165), 1f, false);

                    SetScrollbarPosition();
                    _scrollbar.draw(b);
                }
            }

            // 5. 底部淘汰规则提示语（15f Medium，TextMuted）
            string ruleHint = _ruleHintSource();
            var ruleHintSize = CustomFontManager.MeasureString(ruleHint, TipFontSize);
            CustomFontManager.DrawString(b, ruleHint,
                new Vector2(xPositionOnScreen + (width - ruleHintSize.X) / 2f, yPositionOnScreen + height - 36),
                BioEditorMenu.TextMuted, TipFontSize);

            // 6. Tooltip 悬停提示
            if (_closeButton.containsPoint(mx, my))
                _hoverText = _closeButton.hoverText;
            else if (_clearButtonRect.Contains(mx, my) && _cachedEntries.Count > 0)
                _hoverText = "【清空全部归档】\n永久清除当前所有的归档记忆，释放存储空间。";
            else
            {
                for (int i = 0; i < _restoreButtons.Count; i++)
                {
                    if (_restoreButtons[i].containsPoint(mx, my))
                    {
                        _hoverText = _restoreButtons[i].hoverText;
                        break;
                    }
                    if (i < _deleteButtons.Count && _deleteButtons[i].containsPoint(mx, my))
                    {
                        _hoverText = _deleteButtons[i].hoverText;
                        break;
                    }
                }
            }

            if (!string.IsNullOrEmpty(_hoverText))
                DrawHoverTextCustom(b, _hoverText);

            drawMouse(b);
        }

        private void DrawHeader(SpriteBatch b, int mx, int my)
        {
            int headX = xPositionOnScreen + ContentPadding;
            int headY = yPositionOnScreen + 14;

            const int pSize = 44;
            var portraitRect = new Rectangle(headX, headY, pSize, pSize);

            // 图标外相框
            b.Draw(Game1.staminaRect, new Rectangle(portraitRect.X - 1, portraitRect.Y - 1, portraitRect.Width + 2, portraitRect.Height + 2), new Color(225, 210, 185));
            IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(403, 383, 6, 6),
                portraitRect.X - 2, portraitRect.Y - 2, portraitRect.Width + 4, portraitRect.Height + 4,
                new Color(200, 175, 140), 2f, false);

            // ★ 优化 1：左上角图标智能分流（Timeline 保持收纳箱/肖像现状，规则归档显示垃圾桶）
            if (IsFromTimeline)
            {
                if (_npcPortrait != null && !_portraitSmileRect.IsEmpty)
                {
                    b.Draw(_npcPortrait, portraitRect, _portraitSmileRect, Color.White);
                }
                else
                {
                    string avatarFallback = string.IsNullOrEmpty(_npcName) ? "?" : (string.Equals(_npcName, "WORLD", StringComparison.OrdinalIgnoreCase) ? "🌐" : _npcName.Substring(0, 1));
                    var fsz = CustomFontManager.MeasureStringBold(avatarFallback, TitleFontSize);
                    CustomFontManager.DrawStringBold(b, avatarFallback,
                        new Vector2(portraitRect.X + (pSize - fsz.X) / 2f, portraitRect.Y + (pSize - fsz.Y) / 2f - 1),
                        BioEditorMenu.TextMuted, TitleFontSize);
                }
            }
            else
            {
                // 规则/普通记忆回收站：渲染原木风垃圾桶图标
                if (ModEntry.CustomIcons != null)
                {
                    Rectangle trashSrc = IconSource.Talk(IconTheme.Wood, IconState.Normal);
                    const int iconSize = 48;
                    var iconRect = new Rectangle(
                        portraitRect.X + (pSize - iconSize) / 2,
                        portraitRect.Y + (pSize - iconSize) / 2,
                        iconSize, iconSize);
                    b.Draw(ModEntry.CustomIcons, iconRect, trashSrc, Color.White);
                }
                else
                {
                    const string trashFallback = "🗑";
                    var fsz = CustomFontManager.MeasureStringBold(trashFallback, TitleFontSize);
                    CustomFontManager.DrawStringBold(b, trashFallback,
                        new Vector2(portraitRect.X + (pSize - fsz.X) / 2f, portraitRect.Y + (pSize - fsz.Y) / 2f - 1),
                        BioEditorMenu.TextPrimary, TitleFontSize);
                }
            }

            // 大标题（24f Bold，TextPrimary）
            string title = _titleSource();
            CustomFontManager.DrawStringBold(b, title, new Vector2(headX + pSize + 12, headY + 2), BioEditorMenu.TextPrimary, TitleFontSize);

            // 副标题说明
            string subtitle = IsFromTimeline ? "浏览、还原或彻底删除被收纳的历史随笔。" : "浏览、还原或彻底删除被淘汰置换的规则。";
            CustomFontManager.DrawString(b, subtitle, new Vector2(headX + pSize + 14, headY + 30), BioEditorMenu.TextMuted, TipFontSize);

            // 容量胶囊
            string countText = I18n.Memory.ArchiveCount(_cachedEntries.Count, _capacity);
            bool isNearFull = _cachedEntries.Count >= NearFullThreshold;
            DrawCapacityBadge(b, _capacityPillRect, countText, isNearFull);

            // ★ 优化 2：清空按钮（无缝重构版，彻底根除右侧与底部白缝）
            string clearLabel = I18n.Memory.ArchiveClearButton();
            DrawActionButton(b, _clearButtonRect, clearLabel, mx, my, isDanger: true, isEnabled: _cachedEntries.Count > 0);

            // 分割横线
            int sepY = yPositionOnScreen + HeaderH + 4;
            b.Draw(Game1.staminaRect, new Rectangle(xPositionOnScreen + ContentPadding, sepY, width - ContentPadding * 2, 2), Color.Gray * 0.35f);

            // 关闭按钮平滑缩放
            UiHelper.UpdateButtonScale(ref _closeButtonHoverScale, _closeButton, mx, my);
            _closeButton.scale = CloseButtonBaseScale * _closeButtonHoverScale;
            _closeButton.draw(b);
        }

        private static void DrawCapacityBadge(SpriteBatch b, Rectangle rect, string text, bool isNearFull)
        {
            Color bg = isNearFull ? new Color(255, 235, 215) : new Color(248, 240, 226);
            Color borderCol = isNearFull ? BioEditorMenu.TextWarning : new Color(225, 205, 175);
            Color textCol = isNearFull ? BioEditorMenu.TextWarning : BioEditorMenu.TextSecondary;

            b.Draw(Game1.staminaRect, rect, bg);
            IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(432, 439, 9, 9),
                rect.X, rect.Y, rect.Width, rect.Height, borderCol, 2f, false);

            var sz = CustomFontManager.MeasureString(text, TipFontSize);
            CustomFontManager.DrawString(b, text,
                new Vector2(rect.X + (rect.Width - sz.X) / 2f, rect.Y + (rect.Height - sz.Y) / 2f - 1),
                textCol, TipFontSize);
        }

        private static void DrawRowIconButton(SpriteBatch b, ClickableTextureComponent btn, bool isPressed, bool isHover)
        {
            var r = btn.bounds;
            int pressOffset = isPressed ? 1 : 0;
            var drawRect = new Rectangle(r.X + pressOffset, r.Y + pressOffset, r.Width, r.Height);

            Color bg = isPressed ? new Color(230, 210, 185) : (isHover ? new Color(255, 242, 220) : new Color(245, 232, 212));
            Color borderCol = isHover ? new Color(210, 160, 60) : new Color(225, 205, 175);

            b.Draw(Game1.staminaRect, drawRect, bg);
            IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(432, 439, 9, 9),
                drawRect.X, drawRect.Y, drawRect.Width, drawRect.Height, borderCol, 1.6f, false);
        }

        private static bool IsLeftMouseDown()
        {
            try { return Game1.input.GetMouseState().LeftButton == ButtonState.Pressed; }
            catch { return false; }
        }

        // ★ 优化 2：彻底消灭白缝的动作按钮绘制方法
        private static void DrawActionButton(SpriteBatch b, Rectangle rect, string label, int mx, int my,
            bool isDanger = false, bool isPrimary = false, bool isEnabled = true)
        {
            bool isHover = isEnabled && rect.Contains(mx, my);
            bool isPressed = isHover && IsLeftMouseDown();
            int pressOffset = isPressed ? 1 : 0;

            Color bg;
            if (!isEnabled) bg = Color.LightGray * 0.6f;
            else if (isPrimary) bg = isHover ? Color.Gold : new Color(255, 220, 130);
            else if (isDanger) bg = isHover ? new Color(245, 105, 105) : new Color(210, 85, 80);
            else bg = isHover ? new Color(255, 240, 215) : new Color(225, 195, 155);

            if (isPressed) bg = Color.Lerp(bg, Color.Black, 0.14f);

            // 1. 底层立体阴影
            if (!isPressed)
                b.Draw(Game1.staminaRect, new Rectangle(rect.X + 1, rect.Y + 2, rect.Width, rect.Height), Color.Black * 0.15f);

            var drawRect = new Rectangle(rect.X + pressOffset, rect.Y + pressOffset, rect.Width, rect.Height);

            // 2. 底板全覆盖，坚决不留 1px 裸露边距，防白缝
            b.Draw(Game1.staminaRect, drawRect, bg);

            // 3. 严格采用 2f 整像素 scale，杜绝浮点栅格断裂
            Color borderCol = isPrimary ? new Color(210, 160, 60) : (isDanger ? new Color(175, 60, 55) : new Color(185, 150, 110));
            IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(432, 439, 9, 9),
                drawRect.X, drawRect.Y, drawRect.Width, drawRect.Height, borderCol, 2f, false);

            Color textCol = !isEnabled ? BioEditorMenu.TextMuted
                          : isDanger ? BioEditorMenu.TextOnDarkBtn
                          : BioEditorMenu.TextOnLightBtn;

            var sz = CustomFontManager.MeasureStringBold(label, ButtonFontSize);
            CustomFontManager.DrawStringBold(b, label,
                new Vector2(drawRect.X + (drawRect.Width - sz.X) / 2f, drawRect.Y + (drawRect.Height - sz.Y) / 2f),
                textCol, ButtonFontSize);
        }

        private static void DrawHoverTextCustom(SpriteBatch b, string text)
        {
            if (string.IsNullOrEmpty(text)) return;

            var sz = CustomFontManager.MeasureString(text, TipFontSize);
            const int padX = 20;
            const int padY = 12;

            int boxW = (int)MathF.Ceiling(sz.X) + padX * 2;
            int boxH = (int)MathF.Ceiling(sz.Y) + padY * 2;

            int x = Game1.getOldMouseX() + 24;
            int y = Game1.getOldMouseY() + 24;
            var safe = Utility.getSafeArea();

            if (x + boxW > safe.Right) x = safe.Right - boxW;
            if (y + boxH > safe.Bottom)
            {
                x += 16;
                if (x + boxW > safe.Right) x = safe.Right - boxW;
                y = safe.Bottom - boxH;
            }
            if (x < safe.Left) x = safe.Left;
            if (y < safe.Top) y = safe.Top;

            IClickableMenu.drawTextureBox(b, Game1.menuTexture, new Rectangle(0, 256, 60, 60),
                x + 4, y + 4, boxW, boxH, Color.Black * 0.28f, 0.65f, false);

            IClickableMenu.drawTextureBox(b, Game1.menuTexture, new Rectangle(0, 256, 60, 60),
                x, y, boxW, boxH, new Color(255, 255, 250), 0.65f, false);

            float textY = y + (boxH - sz.Y) / 2f - 1;
            CustomFontManager.DrawString(b, text, new Vector2(x + padX, textY), BioEditorMenu.TextPrimary, TipFontSize);
        }
    }
}