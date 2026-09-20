using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewValley;
using StardewValley.Menus;
using StardewModdingAPI;
using ValleytalkReborn.UI;

namespace ValleytalkReborn;

/// <summary>
/// 记忆提炼菜单：自适应视口尺寸与 UI 缩放，展示候选与既有记忆并支持编辑/删除。
/// 全面应用 CustomFontManager 矢量整数字阶与高品质对齐排版。
/// </summary>
internal class MemoryDistillMenu : IClickableMenu, IMemoryRefreshTarget
{
    private enum DistillState { Loading, Ready }

    private const int RowH = 48;
    private const int PlusSize = 34;
    private const int ButtonSize = 32;

    private const float CloseButtonBaseScale = 3.5f;

    // 动态布局坐标
    private int _contentTopY;
    private int _headerY;
    private int _colW;
    private int _leftColX;
    private int _rightColX;
    private int _dividerX;
    private int _visibleRows;

    private DistillState _state = DistillState.Loading;
    private readonly string _npcName;
    private readonly string _npcDisplayName;
    private readonly IClickableMenu _returnMenu;
    private readonly CancellationTokenSource _cts = new();
    private Task<MemoryExtractResult> _task;
    private List<string> _candidates = new();
    private readonly HashSet<string> _usedCandidates = new(StringComparer.OrdinalIgnoreCase);
    private List<MemoryEntry> _rightEntries = new();
    private readonly MemoryTier _targetTier = MemoryTier.Daily;
    private readonly List<MemoryEntry> _sourceEntriesToRemove;   // v2：浓缩确认后直接删除的源碎片（不再归档）
    private readonly StardewTime? _dateFilter;                   // v2：当前页日期（仅 Tab0 总结路径有值）
    private int _tierCount;
    private int _leftIndex;
    private int _rightIndex;

    private readonly List<Rectangle> _plusRects = new();
    private readonly List<ClickableTextureComponent> _editButtons = new();
    private readonly List<ClickableTextureComponent> _deleteButtons = new();
    private ClickableTextureComponent _closeButton;
    private float _closeButtonHoverScale = 1f;
    private string _hoveredTooltip = string.Empty;

    // T6：双模式开关。false = Manual 事实池（默认），true = Timeline 时间线；时间线写入仅可显式 opt-in
    private readonly bool _timelineMode;

    public MemoryDistillMenu(string npcName,
                             IClickableMenu returnMenu,
                             MemoryTier targetTier = MemoryTier.Daily,
                             List<MemoryEntry> sourceEntriesToRemove = null,
                             Task<MemoryExtractResult> customTask = null,
                             StardewTime? dateFilter = null,
                             List<string> cachedCandidates = null,
                             HashSet<string> usedCandidates = null,
                             bool timelineMode = false)
    {
        _npcName = npcName;
        _returnMenu = returnMenu;
        _npcDisplayName = Game1.getCharacterFromName(npcName)?.displayName ?? npcName;
        _targetTier = targetTier;
        _sourceEntriesToRemove = sourceEntriesToRemove ?? new List<MemoryEntry>();
        _dateFilter = dateFilter;
        _timelineMode = timelineMode;

        if (usedCandidates != null)
            _usedCandidates = usedCandidates;

        UpdateLayout();

        if (cachedCandidates != null && cachedCandidates.Count > 0)
        {
            _candidates = cachedCandidates;
            _state = DistillState.Ready;
        }
        else if (customTask != null)
        {
            _task = customTask;
            _state = DistillState.Loading;
        }
        else
        {
            // T6：Manual 模式只取 Manual 池；Timeline 模式取当前 tier 时间线
            List<string> existing = _timelineMode
                ? MemoryManager.Instance.GetTimelineMemories(_npcName, _targetTier).Select(m => m.Content).Take(10).ToList()
                : MemoryManager.Instance.GetMemories(_npcName).Where(m => m.Source == "Manual").Select(m => m.Content).Take(10).ToList();

            _task = MemoryExtractService.ExtractAsync(_npcName, _npcDisplayName, existing,
                _timelineMode ? _dateFilter : null, _cts.Token);
            _state = DistillState.Loading;
        }

        RefreshEntries();
    }

    // ──────────────────────────────────────────────────────────────
    // 布局与自适应适配
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 根据当前视口动态计算窗口大小、列宽与最大可见行数
    /// </summary>
    private void UpdateLayout()
    {
        width = Math.Clamp(Game1.uiViewport.Width - 96, 760, 1100);
        height = Math.Clamp(Game1.uiViewport.Height - 96, 480, 680);

        xPositionOnScreen = (Game1.uiViewport.Width - width) / 2;
        yPositionOnScreen = (Game1.uiViewport.Height - height) / 2;

        if (_closeButton == null)
        {
            _closeButton = new ClickableTextureComponent(
                new Rectangle(xPositionOnScreen + width - 56, yPositionOnScreen + 16, 44, 44),
                Game1.mouseCursors, new Rectangle(337, 494, 12, 12), CloseButtonBaseScale);
            _closeButton.hoverText = I18n.Memory.CloseButton();
        }
        else
        {
            _closeButton.bounds = new Rectangle(xPositionOnScreen + width - 56, yPositionOnScreen + 16, 44, 44);
        }

        _contentTopY = yPositionOnScreen + 122;
        _headerY = _contentTopY - 32;

        int sidePadding = 45;
        int colGap = 36;
        _colW = (width - sidePadding * 2 - colGap) / 2;
        _leftColX = xPositionOnScreen + sidePadding;
        _dividerX = _leftColX + _colW + colGap / 2;
        _rightColX = _dividerX + colGap / 2;

        int availableHeight = (yPositionOnScreen + height - 36) - _contentTopY;
        _visibleRows = Math.Max(4, availableHeight / RowH);

        RebuildButtons();
    }

    public override void gameWindowSizeChanged(Rectangle oldBounds, Rectangle newBounds)
    {
        base.gameWindowSizeChanged(oldBounds, newBounds);
        UpdateLayout();
    }

    // ──────────────────────────────────────────────────────────────
    // IClickableMenu 核心交互
    // ──────────────────────────────────────────────────────────────

    public override void update(GameTime time)
    {
        base.update(time);

        if (_state == DistillState.Loading && _task != null && _task.IsCompleted)
            ApplyResult();
    }

    public override void receiveLeftClick(int x, int y, bool playSound = true)
    {
        if (_closeButton.containsPoint(x, y))
        {
            CloseByUser();
            return;
        }

        if (_state == DistillState.Loading)
            return;

        // 左栏：添加候选
        for (int i = 0; i < _plusRects.Count; i++)
        {
            int idx = _leftIndex + i;
            if (idx >= _candidates.Count)
                break;

            if (_plusRects[i].Contains(x, y))
            {
                AddCandidate(idx);
                return;
            }
        }

        // 右栏：删除
        for (int i = 0; i < _deleteButtons.Count; i++)
        {
            int idx = _rightIndex + i;
            if (idx >= _rightEntries.Count)
                break;

            if (_deleteButtons[i].containsPoint(x, y))
            {
                ConfirmDelete(_rightEntries[idx]);
                return;
            }
        }

        // 右栏：编辑
        for (int i = 0; i < _editButtons.Count; i++)
        {
            int idx = _rightIndex + i;
            if (idx >= _rightEntries.Count)
                break;

            if (_editButtons[i].containsPoint(x, y))
            {
                Game1.playSound("bigSelect");
                Game1.activeClickableMenu = _timelineMode
                    ? new AddMemoryInputMenu(_npcName, this, _rightEntries[idx], 0,
                        customSubmit: text => MemoryManager.Instance.EditTimelineMemory(_npcName, _rightEntries[idx].Id, text))
                    : new AddMemoryInputMenu(_npcName, this, _rightEntries[idx], 0);
                return;
            }
        }
    }

    public override void receiveScrollWheelAction(int direction)
    {
        if (_state != DistillState.Ready)
            return;

        int mx = Game1.getMouseX();

        // 鼠标位于左栏：滚动候选
        if (mx < _dividerX)
        {
            int maxLeft = Math.Max(0, _candidates.Count - _visibleRows);
            if (maxLeft > 0)
            {
                int newIndex = direction > 0
                    ? Math.Max(0, _leftIndex - 1)
                    : Math.Min(maxLeft, _leftIndex + 1);

                if (newIndex != _leftIndex)
                {
                    _leftIndex = newIndex;
                    Game1.playSound("shwip");
                    RebuildButtons();
                }
            }
        }
        // 鼠标位于右栏：滚动既有记忆
        else
        {
            int maxRight = Math.Max(0, _rightEntries.Count - _visibleRows);
            if (maxRight > 0)
            {
                int newIndex = direction > 0
                    ? Math.Max(0, _rightIndex - 1)
                    : Math.Min(maxRight, _rightIndex + 1);

                if (newIndex != _rightIndex)
                {
                    _rightIndex = newIndex;
                    Game1.playSound("shwip");
                    RebuildButtons();
                }
            }
        }
    }

    public override void receiveKeyPress(Keys key)
    {
        if (key == Keys.Escape)
        {
            CloseByUser();
            return;
        }

        base.receiveKeyPress(key);
    }

    // ──────────────────────────────────────────────────────────────
    // 渲染
    // ──────────────────────────────────────────────────────────────

    public override void draw(SpriteBatch b)
    {
        int mx = Game1.getMouseX();
        int my = Game1.getMouseY();
        _hoveredTooltip = string.Empty;

        // 1. 底层先绘制主菜单，再覆盖半透明遮罩
        _returnMenu?.draw(b);
        b.Draw(Game1.fadeToBlackRect, Game1.graphics.GraphicsDevice.Viewport.Bounds, Color.Black * 0.45f);

        // 2. 双层木框 + 对话框底衬
        IClickableMenu.drawTextureBox(b,
            xPositionOnScreen - 16, yPositionOnScreen - 16,
            width + 32, height + 32, Color.White);
        IClickableMenu.drawTextureBox(b,
            xPositionOnScreen - 8, yPositionOnScreen - 8,
            width + 16, height + 16, Color.White);
        Game1.drawDialogueBox(xPositionOnScreen, yPositionOnScreen, width, height, false, true);

        // 顶部大标题（规范 24f）
        string title = I18n.Memory.DistillTitle(_npcDisplayName);
        Vector2 titleSize = CustomFontManager.MeasureString(title, CustomFontManager.SizeTitle);
        CustomFontManager.DrawString(b, title,
            new Vector2(xPositionOnScreen + (width - titleSize.X) / 2f, yPositionOnScreen + 22),
            Game1.textColor, CustomFontManager.SizeTitle);

        // 关闭按钮
        UiHelper.UpdateButtonScale(ref _closeButtonHoverScale, _closeButton, mx, my);
        _closeButton.scale = CloseButtonBaseScale * _closeButtonHoverScale;
        _closeButton.draw(b);

        if (_state == DistillState.Loading)
        {
            string loading = I18n.Memory.DistillLoading();
            Vector2 size = CustomFontManager.MeasureString(loading, CustomFontManager.SizeRegular);
            CustomFontManager.DrawString(b, loading,
                new Vector2(xPositionOnScreen + (width - size.X) / 2f, yPositionOnScreen + (height - size.Y) / 2f),
                Game1.textColor, CustomFontManager.SizeRegular);
        }
        else
        {
            DrawReady(b, mx, my);
        }

        // 高品质矢量悬浮提示
        if (!string.IsNullOrEmpty(_hoveredTooltip))
        {
            DrawHoverTextCustom(b, _hoveredTooltip);
        }

        drawMouse(b);
    }

    private void DrawReady(SpriteBatch b, int mx, int my)
    {
        // 栏目标题（18f 规整规范）
        CustomFontManager.DrawString(b, I18n.Memory.DistillLeftTitle(),
            new Vector2(_leftColX, _headerY), Game1.textColor, CustomFontManager.SizeRegular);
        CustomFontManager.DrawString(b, I18n.Memory.DistillRightTitle(),
            new Vector2(_rightColX, _headerY), Game1.textColor, CustomFontManager.SizeRegular);

        // 容量计数（15f 小字，垂直平齐对齐）
        int cap = _timelineMode ? MemoryManager.GetTierCapacity(_targetTier) : MemoryManager.MaxMemoriesPerNpc;
        string capText = $"{_tierCount} / {cap}";
        Vector2 capSize = CustomFontManager.MeasureString(capText, CustomFontManager.SizeSmall);
        CustomFontManager.DrawString(b, capText,
            new Vector2(_rightColX + _colW - capSize.X, _headerY + 2f), Color.Gray, CustomFontManager.SizeSmall);

        // 中间分割线
        int dividerHeight = (yPositionOnScreen + height - 40) - _headerY;
        b.Draw(Game1.staminaRect, new Rectangle(_dividerX, _headerY, 2, dividerHeight), Color.Gray * 0.35f);

        // 渲染左栏候选
        bool full = _tierCount >= (_timelineMode ? MemoryManager.GetTierCapacity(_targetTier) : MemoryManager.MaxMemoriesPerNpc);
        int visibleLeft = Math.Min(_visibleRows, Math.Max(0, _candidates.Count - _leftIndex));
        float maxLeftTextWidth = _colW - (PlusSize + 16);

        for (int i = 0; i < visibleLeft && i < _plusRects.Count; i++)
        {
            int idx = _leftIndex + i;
            string cand = _candidates[idx];
            Rectangle rect = _plusRects[i];
            bool used = _usedCandidates.Contains(cand);
            bool hover = rect.Contains(mx, my);

            Color boxColor = (used || full) ? Color.Gray * 0.6f : (hover ? Color.Gold : Color.White);

            // + 按钮底框
            IClickableMenu.drawTextureBox(b, Game1.mouseCursors,
                new Rectangle(432, 439, 9, 9),
                rect.X, rect.Y, rect.Width, rect.Height,
                boxColor, 3.8f, false);

            // 绘制按钮内加号（精确居中）
            Vector2 plusCharSize = CustomFontManager.MeasureString("+", CustomFontManager.SizeRegular);
            CustomFontManager.DrawString(b, "+",
                new Vector2(rect.X + (rect.Width - plusCharSize.X) / 2f, rect.Y + (rect.Height - plusCharSize.Y) / 2f),
                used ? Color.Gray : Game1.textColor, CustomFontManager.SizeRegular);

            // 候选文字及截断
            Color textColor = used ? Color.Gray * 0.8f : Game1.textColor;
            string displayText = CustomFontManager.TruncateString(cand, CustomFontManager.SizeRegular, maxLeftTextWidth);

            float textY = rect.Y + (RowH - CustomFontManager.MeasureString("A", CustomFontManager.SizeRegular).Y) / 2f - 1f;
            Vector2 textPos = new Vector2(_leftColX + PlusSize + 14, textY);
            CustomFontManager.DrawString(b, displayText, textPos, textColor, CustomFontManager.SizeRegular);

            // 文本区域悬停检测
            Rectangle textBounds = new Rectangle((int)textPos.X, rect.Y, (int)maxLeftTextWidth, RowH);
            if (textBounds.Contains(mx, my) && displayText != cand)
            {
                _hoveredTooltip = cand;
            }
        }

        // 渲染右栏既有条目
        int visibleRight = Math.Min(_visibleRows, Math.Max(0, _rightEntries.Count - _rightIndex));
        float maxRightTextWidth = _colW - 96;

        for (int i = 0; i < visibleRight; i++)
        {
            int idx = _rightIndex + i;
            MemoryEntry entry = _rightEntries[idx];
            int rowY = _contentTopY + i * RowH;

            // 认知分层标签与色彩
            bool isRule = entry.Category == MemoryCategory.Behavior;
            string prefix = isRule ? I18n.Memory.RuleTag() : I18n.Memory.MemoryTag();
            Color textColor = isRule ? new Color(175, 110, 15)
                : (entry.Source == "Auto" ? new Color(115, 135, 155) : Game1.textColor);
            string fullText = $"{idx + 1}. {prefix}{entry.Content}";

            string displayText = CustomFontManager.TruncateString(fullText, CustomFontManager.SizeRegular, maxRightTextWidth);
            float textY = rowY + (RowH - CustomFontManager.MeasureString("A", CustomFontManager.SizeRegular).Y) / 2f;
            Vector2 textPos = new Vector2(_rightColX, textY);
            CustomFontManager.DrawString(b, displayText, textPos, textColor, CustomFontManager.SizeRegular);

            bool isLeftMouseDown = Mouse.GetState().LeftButton == ButtonState.Pressed;

            // 绘制编辑按钮
            if (i < _editButtons.Count)
            {
                var btn = _editButtons[i];
                bool isPressed = isLeftMouseDown && btn.containsPoint(mx, my);
                IconSource.DrawButton(b, btn, isPressed);

                if (btn.containsPoint(mx, my))
                    _hoveredTooltip = btn.hoverText;
            }

            // 绘制删除按钮
            if (i < _deleteButtons.Count)
            {
                var btn = _deleteButtons[i];
                bool isPressed = isLeftMouseDown && btn.containsPoint(mx, my);
                IconSource.DrawButton(b, btn, isPressed);

                if (btn.containsPoint(mx, my))
                    _hoveredTooltip = btn.hoverText;
            }

            // 悬停显示完整内容
            Rectangle textBounds = new Rectangle((int)textPos.X, rowY, (int)maxRightTextWidth, RowH);
            if (textBounds.Contains(mx, my) && displayText != fullText)
            {
                _hoveredTooltip = fullText;
            }
        }
    }

    // ──────────────────────────────────────────────────────────────
    // 内部方法
    // ──────────────────────────────────────────────────────────────

    public void RefreshEntries()
    {
        _rightEntries = _timelineMode
            ? MemoryManager.Instance.GetTimelineMemories(_npcName, _targetTier)
            : MemoryManager.Instance.GetMemories(_npcName);
        _tierCount = _timelineMode
            ? _rightEntries.Count
            : MemoryManager.Instance.GetManualMemoryCount(_npcName);
        _rightIndex = 0;
        _leftIndex = 0;
        RebuildButtons();
    }

    private void RebuildButtons()
    {
        _plusRects.Clear();
        _editButtons.Clear();
        _deleteButtons.Clear();

        if (_state == DistillState.Ready)
        {
            int visibleLeft = Math.Min(_visibleRows, Math.Max(0, _candidates.Count - _leftIndex));
            for (int i = 0; i < visibleLeft; i++)
            {
                int rowY = _contentTopY + i * RowH;
                _plusRects.Add(new Rectangle(
                    _leftColX,
                    rowY + (RowH - PlusSize) / 2,
                    PlusSize, PlusSize));
            }
        }

        int visibleRight = Math.Min(_visibleRows, Math.Max(0, _rightEntries.Count - _rightIndex));
        for (int i = 0; i < visibleRight; i++)
        {
            int rowY = _contentTopY + i * RowH;
            int btnY = rowY + (RowH - ButtonSize) / 2;

            var edit = new ClickableTextureComponent(
                new Rectangle(_rightColX + _colW - 74, btnY, ButtonSize, ButtonSize),
                ModEntry.CustomIcons,
                IconSource.Edit(IconTheme.Wood, IconState.Normal),
                2f)
            {
                hoverText = I18n.Memory.EditButtonHover()
            };
            _editButtons.Add(edit);

            var del = new ClickableTextureComponent(
                new Rectangle(_rightColX + _colW - 36, btnY, ButtonSize, ButtonSize),
                ModEntry.CustomIcons,
                IconSource.Trash(IconTheme.Wood, IconState.Normal),
                2f)
            {
                hoverText = I18n.Memory.DeleteButtonHover()
            };
            _deleteButtons.Add(del);
        }
    }

    private void ApplyResult()
    {
        if (Game1.activeClickableMenu != this)
            return;

        if (_task.IsFaulted || _task.IsCanceled)
        {
            CloseToParent(I18n.Memory.DistillFailed(), 3, "cancel");
            return;
        }

        MemoryExtractResult result = _task.Result;

        switch (result.Status)
        {
            case MemoryExtractStatus.Success:
                _candidates = result.Candidates ?? new List<string>();
                _state = DistillState.Ready;
                RefreshEntries();
                Game1.playSound("smallSelect");
                break;

            case MemoryExtractStatus.Empty:
                CloseToParent(I18n.Memory.DistillEmpty(), 0, "cancel");
                break;

            case MemoryExtractStatus.NoHistory:
                CloseToParent(I18n.Memory.DistillNoHistory(_npcDisplayName), 0, "cancel");
                break;

            case MemoryExtractStatus.Failed:
                CloseToParent(I18n.Memory.DistillFailed(), 3, "cancel");
                break;

            case MemoryExtractStatus.Cancelled:
                CloseToParent(null, 0, "bigDeSelect");
                break;
        }
    }

    private void AddCandidate(int index)
    {
        string c = _candidates[index];

        if (_usedCandidates.Contains(c))
        {
            Game1.playSound("cancel");
            Game1.addHUDMessage(new HUDMessage(I18n.Memory.DistillDuplicate(), 3));
            return;
        }

        string dateLabel = null;
        int createdDay = -1;
        if (_timelineMode)
        {
            dateLabel = _dateFilter.HasValue
                ? MemoryManager.FormatGameDateLabel(_dateFilter.Value)
                : MemoryManager.FormatCurrentGameDateLabel();
            createdDay = _dateFilter.HasValue
                ? MemoryManager.StardewTimeToGameDay(_dateFilter.Value)
                : -1;
        }

        MemoryOperationResult result = _timelineMode
            ? MemoryManager.Instance.AddTimelineMemory(_npcName, c, _targetTier, dateLabel, createdDay)
            : MemoryManager.Instance.AddMemory(_npcName, c, MemoryCategory.Fact);

        switch (result)
        {
            case MemoryOperationResult.Success:
                _usedCandidates.Add(c);
                Game1.playSound("coin");
                if (_timelineMode && _sourceEntriesToRemove.Count > 0)
                {
                    MemoryManager.Instance.RemoveTimelineMemories(
                        _npcName, _sourceEntriesToRemove.Select(m => m.Id));
                    _sourceEntriesToRemove.Clear();
                }
                RefreshEntries();
                break;

            case MemoryOperationResult.Duplicate:
                _usedCandidates.Add(c);
                Game1.playSound("cancel");
                Game1.addHUDMessage(new HUDMessage(I18n.Memory.DistillDuplicate(), 3));
                break;

            case MemoryOperationResult.CapacityFull:
                Game1.playSound("cancel");
                Game1.addHUDMessage(new HUDMessage(I18n.Memory.AddFailedFull(
                    _timelineMode ? MemoryManager.GetTierCapacity(_targetTier) : MemoryManager.MaxMemoriesPerNpc), 3));
                break;

            case MemoryOperationResult.TooLong:
                Game1.playSound("cancel");
                Game1.addHUDMessage(new HUDMessage(I18n.Memory.AddFailedTooLong(MemoryManager.MaxMemoryLength), 3));
                break;

            default:
                Game1.playSound("cancel");
                Game1.addHUDMessage(new HUDMessage(I18n.Memory.DistillFailed(), 3));
                break;
        }
    }

    private void ConfirmDelete(MemoryEntry entry)
    {
        string safeContent = CustomFontManager.TruncateString(entry.Content, CustomFontManager.SizeRegular, 320f);
        Game1.activeClickableMenu = new ConfirmationDialog(
            I18n.Memory.DeleteConfirm(safeContent),
            _ =>
            {
                if (_timelineMode)
                    MemoryManager.Instance.RemoveTimelineMemory(_npcName, entry.Id);
                else
                    MemoryManager.Instance.RemoveMemory(_npcName, entry.Id);
                Game1.playSound("trashcan");
                RefreshEntries();
                Game1.activeClickableMenu = this;
            },
            _ =>
            {
                Game1.activeClickableMenu = this;
            });
    }

    private void CloseToParent(string hudMessage, int hudKind, string sound)
    {
        if (!string.IsNullOrEmpty(sound))
            Game1.playSound(sound);

        if (!string.IsNullOrEmpty(hudMessage))
            Game1.addHUDMessage(new HUDMessage(hudMessage, hudKind));

        if (_returnMenu is IMemoryRefreshTarget refreshable)
            refreshable.RefreshEntries();

        exitThisMenu(playSound: false);
    }

    private void CloseByUser()
    {
        if (_state == DistillState.Loading)
            _cts.Cancel();

        Game1.playSound("bigDeSelect");

        if (_returnMenu is IMemoryRefreshTarget refreshable)
            refreshable.RefreshEntries();

        exitThisMenu(playSound: false);
    }

    protected override void cleanupBeforeExit()
    {
        base.cleanupBeforeExit();

        _cts.Cancel();

        if (_returnMenu != null && Game1.activeClickableMenu == this)
            Game1.activeClickableMenu = _returnMenu;
    }

    /// <summary>
    /// 自定义矢量提示框绘制（杜绝系统 smallFont 模糊与屏幕溢出）
    /// </summary>
    private static void DrawHoverTextCustom(SpriteBatch b, string text)
    {
        if (string.IsNullOrEmpty(text)) return;

        var sz = CustomFontManager.MeasureString(text, CustomFontManager.SizeRegular);

        // ── 宽裕适中的内外边距，告别局促压迫感 ──
        const int padX = 20; 
        const int padY = 12; 

        int boxW = (int)MathF.Ceiling(sz.X) + padX * 2;
        int boxH = (int)MathF.Ceiling(sz.Y) + padY * 2;

        int x = Game1.getOldMouseX() + 24;
        int y = Game1.getOldMouseY() + 24;
        var safe = Utility.getSafeArea();

        // 屏幕边缘自动翻折避让
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

        // 1. 原版像素软阴影（比例设为 0.65f，边框细腻不笨重）
        IClickableMenu.drawTextureBox(b, Game1.menuTexture, new Rectangle(0, 256, 60, 60),
            x + 4, y + 4, boxW, boxH, Color.Black * 0.28f, 0.65f, false);

        // 2. 星露谷原版浅亮/暖白羊皮纸底框（解决 1f 下厚重挤压文字的问题）
        IClickableMenu.drawTextureBox(b, Game1.menuTexture, new Rectangle(0, 256, 60, 60),
            x, y, boxW, boxH, new Color(255, 255, 250), 0.65f, false);

        // 3. 提示文字精准垂直居中
        float textY = y + (boxH - sz.Y) / 2f - 1;
        CustomFontManager.DrawString(b, text, 
            new Vector2(x + padX, textY), 
            Game1.textColor, CustomFontManager.SizeRegular);
    }
}