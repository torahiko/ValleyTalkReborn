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

namespace ValleytalkReborn;

/// <summary>
/// 记忆提炼菜单：自适应视口尺寸与 UI 缩放，展示候选与既有记忆并支持编辑/删除。
/// </summary>
internal class MemoryDistillMenu : IClickableMenu, IMemoryRefreshTarget
{
    private enum DistillState { Loading, Ready }

    private const int RowH = 48;
    private const int PlusSize = 34;
    private const int RowBtnSize = 38;

    private const int EditSourceX = 274;
    private const int EditSourceY = 284;
    private const int EditSourceSize = 16;
    private const float EditSourceScale = 2.4f;

    private const int DeleteSourceX = 322;
    private const int DeleteSourceY = 498;
    private const int DeleteSourceSize = 12;
    private const float DeleteSourceScale = 2.4f;

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
    private int _manualCount;
    private int _leftIndex;
    private int _rightIndex;

    private readonly List<Rectangle> _plusRects = new();
    private readonly List<ClickableTextureComponent> _editButtons = new();
    private readonly List<ClickableTextureComponent> _deleteButtons = new();
    private ClickableTextureComponent _closeButton;
    private float _closeButtonHoverScale = 1f;
    private string _hoveredTooltip = string.Empty;

    public MemoryDistillMenu(string npcName,
                             IClickableMenu returnMenu,
                             List<string> cachedCandidates = null,
                             HashSet<string> usedCandidates = null)
    {
        _npcName = npcName;
        _returnMenu = returnMenu;
        _npcDisplayName = Game1.getCharacterFromName(npcName)?.displayName ?? npcName;

        if (usedCandidates != null)
            _usedCandidates = usedCandidates;

        UpdateLayout();

        if (cachedCandidates != null && cachedCandidates.Count > 0)
        {
            _candidates = cachedCandidates;
            _state = DistillState.Ready;
        }
        else
        {
            List<string> existingManual = MemoryManager.Instance.GetMemories(_npcName)
                .Where(m => m.Source == "Manual")
                .Select(m => m.Content)
                .Take(10)
                .ToList();

            _task = MemoryExtractService.ExtractAsync(_npcName, _npcDisplayName, existingManual, _cts.Token);
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
        // 自适应宽高：留出屏幕安全边距，并设定上下限
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

        _contentTopY = yPositionOnScreen + 125;
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
                Game1.activeClickableMenu = new AddMemoryInputMenu(_npcName, this, _rightEntries[idx], 0);
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

        // 1. 深度遮罩（采用与归档菜单一致的 0.75f 深度遮罩，彻底阻断游戏画面穿透）
        // 1. 底层先绘制主菜单，再覆盖 40% 半透明遮罩
        _returnMenu?.draw(b);
        b.Draw(Game1.fadeToBlackRect, Game1.graphics.GraphicsDevice.Viewport.Bounds, Color.Black * 0.4f);

        // 2. 补齐两层木框 + 实体对话框（托住标题和内容）
        IClickableMenu.drawTextureBox(b,
            xPositionOnScreen - 16, yPositionOnScreen - 16,
            width + 32, height + 32, Color.White);
        IClickableMenu.drawTextureBox(b,
            xPositionOnScreen - 8, yPositionOnScreen - 8,
            width + 16, height + 16, Color.White);
        Game1.drawDialogueBox(xPositionOnScreen, yPositionOnScreen, width, height, false, true);

        // 顶部标题（此时已稳稳居于实体羊皮纸底框正上方）
        string title = I18n.Memory.DistillTitle(_npcDisplayName);
        Vector2 titleSize = Game1.dialogueFont.MeasureString(title);
        b.DrawString(Game1.dialogueFont, title,
            new Vector2(xPositionOnScreen + (width - titleSize.X) / 2f, yPositionOnScreen + 24),
            Game1.textColor);

        // 关闭按钮
        UiHelper.UpdateButtonScale(ref _closeButtonHoverScale, _closeButton, mx, my);
        _closeButton.scale = CloseButtonBaseScale * _closeButtonHoverScale;
        _closeButton.draw(b);

        if (_state == DistillState.Loading)
        {
            string loading = I18n.Memory.DistillLoading();
            Vector2 size = Game1.smallFont.MeasureString(loading);
            b.DrawString(Game1.smallFont, loading,
                new Vector2(xPositionOnScreen + (width - size.X) / 2f, yPositionOnScreen + (height - size.Y) / 2f),
                Game1.textColor);
        }
        else
        {
            DrawReady(b, mx, my);
        }

        // 浮动提示
        if (!string.IsNullOrEmpty(_hoveredTooltip))
        {
            IClickableMenu.drawHoverText(b, _hoveredTooltip, Game1.smallFont);
        }

        drawMouse(b);
    }

    private void DrawReady(SpriteBatch b, int mx, int my)
    {
        // 栏目标题
        b.DrawString(Game1.smallFont, I18n.Memory.DistillLeftTitle(),
            new Vector2(_leftColX, _headerY), Game1.textColor);
        b.DrawString(Game1.smallFont, I18n.Memory.DistillRightTitle(),
            new Vector2(_rightColX, _headerY), Game1.textColor);

        // 容量计数
        string cap = $"{_manualCount} / {MemoryManager.MaxMemoriesPerNpc}";
        Vector2 capSize = Game1.smallFont.MeasureString(cap);
        b.DrawString(Game1.smallFont, cap,
            new Vector2(_rightColX + _colW - capSize.X, _headerY), Color.Gray);

        // 中间分割线（贯通上下）
        int dividerHeight = (yPositionOnScreen + height - 40) - _headerY;
        b.Draw(Game1.staminaRect, new Rectangle(_dividerX, _headerY, 2, dividerHeight), Color.Gray * 0.4f);

        // 渲染左栏候选
        bool full = _manualCount >= MemoryManager.MaxMemoriesPerNpc;
        int visibleLeft = Math.Min(_visibleRows, Math.Max(0, _candidates.Count - _leftIndex));
        float maxLeftTextWidth = _colW - (PlusSize + 14);

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

            // 绘制按钮内加号
            Vector2 plusCharSize = Game1.smallFont.MeasureString("+");
            b.DrawString(Game1.smallFont, "+",
                new Vector2(rect.X + (rect.Width - plusCharSize.X) / 2f, rect.Y + (rect.Height - plusCharSize.Y) / 2f),
                used ? Color.Gray : Game1.textColor);

            // 候选文字及截断
            Color textColor = used ? Game1.textColor * 0.45f : Game1.textColor;
            string displayText = TruncateString(cand, Game1.smallFont, maxLeftTextWidth);

            Vector2 textPos = new Vector2(_leftColX + PlusSize + 12, rect.Y + (RowH - Game1.smallFont.LineSpacing) / 2f);
            b.DrawString(Game1.smallFont, displayText, textPos, textColor);

            // 文本区域悬停检测（若被截断则提供 Tooltip）
            Rectangle textBounds = new Rectangle((int)textPos.X, rect.Y, (int)maxLeftTextWidth, RowH);
            if (textBounds.Contains(mx, my) && displayText != cand)
            {
                _hoveredTooltip = cand;
            }
        }

        // 渲染右栏既有条目
        int visibleRight = Math.Min(_visibleRows, Math.Max(0, _rightEntries.Count - _rightIndex));
        float maxRightTextWidth = _colW - 95; // 预留编辑和删除两枚按钮的宽度

        for (int i = 0; i < visibleRight; i++)
        {
            int idx = _rightIndex + i;
            MemoryEntry entry = _rightEntries[idx];
            int rowY = _contentTopY + i * RowH;

            // 🌟 认知分层标签渲染（CORE-MEM-103）
            bool isRule = entry.Category == MemoryCategory.Behavior;
            string prefix = isRule ? I18n.Memory.RuleTag() : I18n.Memory.MemoryTag();
            Color textColor = isRule ? new Color(255, 215, 0)
                : (entry.Source == "Auto" ? new Color(130, 150, 170) : Game1.textColor);
            string fullText = $"{idx + 1}. {prefix}{entry.Content}";

            string displayText = TruncateString(fullText, Game1.smallFont, maxRightTextWidth);
            Vector2 textPos = new Vector2(_rightColX, rowY + (RowH - Game1.smallFont.LineSpacing) / 2f);
            b.DrawString(Game1.smallFont, displayText, textPos, textColor);

            if (i < _editButtons.Count) _editButtons[i].draw(b);
            if (i < _deleteButtons.Count) _deleteButtons[i].draw(b);

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
        _rightEntries = MemoryManager.Instance.GetMemories(_npcName);
        _manualCount = MemoryManager.Instance.GetManualMemoryCount(_npcName);
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
            int btnY = rowY + (RowH - RowBtnSize) / 2;

            _editButtons.Add(new ClickableTextureComponent(
                new Rectangle(_rightColX + _colW - 86, btnY, RowBtnSize, RowBtnSize),
                Game1.mouseCursors,
                new Rectangle(EditSourceX, EditSourceY, EditSourceSize, EditSourceSize),
                EditSourceScale));

            _deleteButtons.Add(new ClickableTextureComponent(
                new Rectangle(_rightColX + _colW - 42, btnY, RowBtnSize, RowBtnSize),
                Game1.mouseCursors,
                new Rectangle(DeleteSourceX, DeleteSourceY, DeleteSourceSize, DeleteSourceSize),
                DeleteSourceScale));
        }
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
                if (_returnMenu is ScrollableMemoryMenu parentMenu)
                    parentMenu.SetDistillCache(_candidates);
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

        MemoryOperationResult result = MemoryManager.Instance.AddMemory(_npcName, c, MemoryCategory.Fact);

        switch (result)
        {
            case MemoryOperationResult.Success:
                _usedCandidates.Add(c);
                Game1.playSound("coin");
                RefreshEntries();
                break;

            case MemoryOperationResult.Duplicate:
                _usedCandidates.Add(c);
                Game1.playSound("cancel");
                Game1.addHUDMessage(new HUDMessage(I18n.Memory.DistillDuplicate(), 3));
                break;

            case MemoryOperationResult.CapacityFull:
                Game1.playSound("cancel");
                Game1.addHUDMessage(new HUDMessage(I18n.Memory.AddFailedFull(MemoryManager.MaxMemoriesPerNpc), 3));
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
        Game1.activeClickableMenu = new ConfirmationDialog(
            I18n.Memory.DeleteConfirm(entry.Content),
            _ =>
            {
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
}