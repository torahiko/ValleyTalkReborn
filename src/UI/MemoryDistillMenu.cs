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
/// 记忆提炼菜单：以 Loading 态异步驱动 MemoryExtractService.ExtractAsync，
/// 转为 Ready 态后展示 AI 提炼出的候选记忆供玩家逐条入库（AddMemory / Source="Manual"），
/// 同时显示该 NPC 既有记忆并支持编辑/删除。
/// 一切 UI 变更只发生在主线程（update / receiveLeftClick / receiveScrollWheelAction / draw）；
/// 服务结果经 update 轮询 _task.IsCompleted 应用，绝不在任务回调中触碰 UI（D3 决策）。
/// </summary>
internal class MemoryDistillMenu : IClickableMenu, IMemoryRefreshTarget
{
    private enum DistillState { Loading, Ready }

    private const int MenuWidth = 1000;
    private const int MenuHeight = 600;
    private const int ColumnTop = 140;
    private const int RowH = 48;
    private const int VisibleRows = 8;
    private const int ColW = 440;
    private const int PlusSize = 36;
    private const int RowBtnSize = 40;

    private const int EditSourceX = 274;
    private const int EditSourceY = 284;
    private const int EditSourceSize = 16;
    private const float EditSourceScale = 2.5f;
    private const int DeleteSourceX = 322;
    private const int DeleteSourceY = 498;
    private const int DeleteSourceSize = 12;
    private const float DeleteSourceScale = 2.5f;

    private DistillState _state = DistillState.Loading;
    private readonly string _npcName;
    private readonly string _npcDisplayName;
    private readonly IClickableMenu _returnMenu;
    private readonly CancellationTokenSource _cts = new();
    private Task<MemoryExtractResult> _task;
    private List<string> _candidates = new();
    private HashSet<string> _usedCandidates = new(StringComparer.OrdinalIgnoreCase);
    private List<MemoryEntry> _rightEntries = new();
    private int _manualCount;
    private int _rightIndex;
    private List<Rectangle> _plusRects = new();
    private List<ClickableTextureComponent> _editButtons = new();
    private List<ClickableTextureComponent> _deleteButtons = new();
    private readonly ClickableTextureComponent _closeButton;
    private float _closeButtonHoverScale = 1f;
    private const float CloseButtonBaseScale = 3.5f;

    private int LeftColX => xPositionOnScreen + 40;
    private int RightColX => xPositionOnScreen + 520;
    private int DividerX => xPositionOnScreen + 500;

    public MemoryDistillMenu(string npcName, IClickableMenu returnMenu)
    {
        _npcName = npcName;
        _returnMenu = returnMenu;

        xPositionOnScreen = (Game1.uiViewport.Width - MenuWidth) / 2;
        yPositionOnScreen = (Game1.uiViewport.Height - MenuHeight) / 2;
        width = MenuWidth;
        height = MenuHeight;

        _npcDisplayName = Game1.getCharacterFromName(npcName)?.displayName ?? npcName;

        _closeButton = new ClickableTextureComponent(
            new Rectangle(xPositionOnScreen + width - 60, yPositionOnScreen + 16, 44, 44),
            Game1.mouseCursors, new Rectangle(337, 494, 12, 12), CloseButtonBaseScale);
        _closeButton.hoverText = I18n.Memory.CloseButton();

        // 快照已有 Manual 记忆作为去重基准
        List<string> existingManual = MemoryManager.Instance.GetMemories(_npcName)
            .Where(m => m.Source == "Manual")
            .Select(m => m.Content)
            .Take(10)
            .ToList();

        _task = MemoryExtractService.ExtractAsync(_npcName, _npcDisplayName, existingManual, _cts.Token);

        RefreshEntries();
    }

    // ──────────────────────────────────────────────────────────────
    // IClickableMenu
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

        // Loading 中除关闭外一律吞掉
        if (_state == DistillState.Loading)
            return;

        // 左栏：添加候选
        for (int i = 0; i < _plusRects.Count && i < _candidates.Count; i++)
        {
            if (_plusRects[i].Contains(x, y))
            {
                AddCandidate(i);
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

        if (_rightEntries.Count <= VisibleRows)
            return;

        int maxIndex = _rightEntries.Count - VisibleRows;
        int newIndex = direction > 0
            ? Math.Max(0, _rightIndex - 1)
            : Math.Min(maxIndex, _rightIndex + 1);

        if (newIndex != _rightIndex)
        {
            _rightIndex = newIndex;
            Game1.playSound("shwip");
            RebuildButtons();
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

        Game1.drawDialogueBox(xPositionOnScreen, yPositionOnScreen, width, height, false, true);

        string title = I18n.Memory.DistillTitle(_npcDisplayName);
        var titleSize = Game1.dialogueFont.MeasureString(title);
        b.DrawString(Game1.dialogueFont, title,
            new Vector2(xPositionOnScreen + (width - titleSize.X) / 2f, yPositionOnScreen + 20),
            Game1.textColor);

        // 关闭按钮（悬停缩放）
        UiHelper.UpdateButtonScale(ref _closeButtonHoverScale, _closeButton, mx, my);
        _closeButton.scale = CloseButtonBaseScale * _closeButtonHoverScale;
        _closeButton.draw(b);

        if (_state == DistillState.Loading)
        {
            string loading = I18n.Memory.DistillLoading();
            var size = Game1.smallFont.MeasureString(loading);
            b.DrawString(Game1.smallFont, loading,
                new Vector2(xPositionOnScreen + (width - size.X) / 2f,
                            yPositionOnScreen + MenuHeight / 2f - size.Y / 2f),
                Game1.textColor);
        }
        else
        {
            DrawReady(b, mx, my);
        }

        drawMouse(b);
    }

    public void RefreshEntries()
    {
        _rightEntries = MemoryManager.Instance.GetMemories(_npcName);
        _manualCount = MemoryManager.Instance.GetManualMemoryCount(_npcName);
        _rightIndex = 0;
        RebuildButtons();
    }

    protected override void cleanupBeforeExit()
    {
        base.cleanupBeforeExit();

        _cts.Cancel();

        if (_returnMenu != null && Game1.activeClickableMenu == this)
            Game1.activeClickableMenu = _returnMenu;
    }

    // ──────────────────────────────────────────────────────────────
    // 内部方法
    // ──────────────────────────────────────────────────────────────

    /// <summary>仅在 update 内被调用，且需确认本菜单仍为 active。</summary>
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
                RefreshEntries();
                _state = DistillState.Ready;
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

        MemoryOperationResult result = MemoryManager.Instance.AddMemory(_npcName, c);

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

        exitThisMenu(playSound: false);
    }

    private void CloseByUser()
    {
        if (_state == DistillState.Loading)
            _cts.Cancel();

        Game1.playSound("bigDeSelect");
        exitThisMenu(playSound: false);
    }

    private void RebuildButtons()
    {
        _plusRects.Clear();
        _editButtons.Clear();
        _deleteButtons.Clear();

        // 左栏 + 按钮（Ready 态才显示候选）
        if (_state == DistillState.Ready)
        {
            for (int i = 0; i < _candidates.Count; i++)
            {
                _plusRects.Add(new Rectangle(
                    LeftColX,
                    ColumnTop + 20 + i * RowH,
                    PlusSize, PlusSize));
            }
        }

        // 右栏 edit / delete 按钮
        int visibleRight = Math.Min(VisibleRows, Math.Max(0, _rightEntries.Count - _rightIndex));
        for (int i = 0; i < visibleRight; i++)
        {
            int rowY = ColumnTop + 20 + i * RowH;

            _editButtons.Add(new ClickableTextureComponent(
                new Rectangle(RightColX + ColW - 95, rowY, RowBtnSize, RowBtnSize),
                Game1.mouseCursors,
                new Rectangle(EditSourceX, EditSourceY, EditSourceSize, EditSourceSize),
                EditSourceScale));

            _deleteButtons.Add(new ClickableTextureComponent(
                new Rectangle(RightColX + ColW - 45, rowY, RowBtnSize, RowBtnSize),
                Game1.mouseCursors,
                new Rectangle(DeleteSourceX, DeleteSourceY, DeleteSourceSize, DeleteSourceSize),
                DeleteSourceScale));
        }
    }

    private void DrawReady(SpriteBatch b, int mx, int my)
    {
        // 列头
        b.DrawString(Game1.smallFont, I18n.Memory.DistillLeftTitle(),
            new Vector2(LeftColX, ColumnTop - 30), Game1.textColor);
        b.DrawString(Game1.smallFont, I18n.Memory.DistillRightTitle(),
            new Vector2(RightColX, ColumnTop - 30), Game1.textColor);

        // 右栏容量行（Manual 口径，非总数）
        string cap = $"{_manualCount} / {MemoryManager.MaxMemoriesPerNpc}";
        var capSize = Game1.smallFont.MeasureString(cap);
        b.DrawString(Game1.smallFont, cap,
            new Vector2(RightColX + ColW - capSize.X, ColumnTop - 30), Color.Gray);

        // 分隔竖线
        b.Draw(Game1.staminaRect,
            new Rectangle(DividerX, ColumnTop - 30, 1, MenuHeight - ColumnTop - 30),
            Color.Gray * 0.5f);

        // 左栏候选
        bool full = _manualCount >= MemoryManager.MaxMemoriesPerNpc;
        for (int i = 0; i < _candidates.Count; i++)
        {
            Rectangle rect = _plusRects[i];
            bool used = _usedCandidates.Contains(_candidates[i]);
            bool hover = rect.Contains(mx, my);

            Color boxColor;
            if (used || full)
                boxColor = Color.Gray * 0.6f;
            else if (hover)
                boxColor = Color.Gold;
            else
                boxColor = Color.White;

            IClickableMenu.drawTextureBox(b, Game1.mouseCursors,
                new Rectangle(432, 439, 9, 9),
                rect.X, rect.Y, rect.Width, rect.Height,
                boxColor, 4f, false);

            Color textColor = used ? Game1.textColor * 0.5f : Game1.textColor;
            b.DrawString(Game1.dialogueFont, _candidates[i],
                new Vector2(LeftColX + 48, rect.Y + 6),
                textColor);
        }

        // 右栏条目
        int visibleRight = Math.Min(VisibleRows, Math.Max(0, _rightEntries.Count - _rightIndex));
        for (int i = 0; i < visibleRight; i++)
        {
            int idx = _rightIndex + i;
            MemoryEntry entry = _rightEntries[idx];
            int rowY = ColumnTop + 20 + i * RowH;

            bool isAuto = entry.Source == "Auto";
            string prefix = isAuto ? I18n.Memory.AutoPrefix() : "";
            Color textColor = isAuto ? new Color(120, 140, 160) : Game1.textColor;
            string text = $"{idx + 1}. {prefix}{entry.Content}";

            b.DrawString(Game1.dialogueFont, text,
                new Vector2(RightColX, rowY),
                textColor);

            _editButtons[i].draw(b);
            _deleteButtons[i].draw(b);
        }
    }
}
