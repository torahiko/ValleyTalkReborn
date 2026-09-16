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

namespace ValleytalkReborn;

/// <summary>
/// 时间线手账专属提炼/浓缩卡片菜单：
/// 1. 采用单栏全宽卡片式排版，无截断自适应高度，完全契合手账气泡美学；
/// 2. 浓缩模式在顶部清晰溯源展示参与融合的源碎片；
/// 3. 支持在草稿卡片上直接微调内容或一键采纳收录。
/// </summary>
internal class TimelineDistillMenu : IClickableMenu, IMemoryRefreshTarget
{
    private enum DistillState { Loading, Ready, Failed }

    private const float TextFontScale = 0.8f;
    private const int CardPaddingX = 18;
    private const int CardPaddingY = 14;
    private const int CardSpacing = 14;

    // 图标切片 (mouseCursors)
    private static readonly Rectangle EditIconSource = new(274, 412, 11, 11);

    private readonly string _npcName;
    private readonly string _npcDisplayName;
    private readonly IClickableMenu _returnMenu;
    private readonly MemoryTier _targetTier;
    private readonly List<MemoryEntry> _sourceEntriesToRemove;
    private readonly StardewTime? _dateFilter;
    private readonly CancellationTokenSource _cts = new();

    private DistillState _state = DistillState.Loading;
    private Task<MemoryExtractResult> _task;
    private readonly List<string> _candidates = new();
    private readonly HashSet<string> _usedCandidates = new(StringComparer.OrdinalIgnoreCase);

    // 动态布局计算
    private int _contentTopY;
    private int _visibleHeight;
    private int _bodyWidth;
    private int _bodyX;
    private int _scrollOffset;
    private int _totalContentHeight;

    private ClickableTextureComponent _closeButton;
    private float _closeButtonHoverScale = 1f;
    private string _hoveredTooltip = string.Empty;

    // 预排版缓存
    private readonly List<CandidateCardLayout> _cardLayouts = new();
    private Rectangle _sourceBoxRect = Rectangle.Empty;

    public TimelineDistillMenu(
        string npcName,
        IClickableMenu returnMenu,
        MemoryTier targetTier = MemoryTier.Daily,
        List<MemoryEntry> sourceEntriesToRemove = null,
        Task<MemoryExtractResult> customTask = null,
        StardewTime? dateFilter = null)
    {
        _npcName = npcName;
        _returnMenu = returnMenu;
        _npcDisplayName = Game1.getCharacterFromName(npcName)?.displayName ?? npcName;
        _targetTier = targetTier;
        _sourceEntriesToRemove = sourceEntriesToRemove != null ? new List<MemoryEntry>(sourceEntriesToRemove) : new List<MemoryEntry>();
        _dateFilter = dateFilter;

        UpdateLayout();

        if (customTask != null)
        {
            _task = customTask;
            _state = DistillState.Loading;
        }
        else
        {
            var existing = MemoryManager.Instance.GetTimelineMemories(_npcName, _targetTier)
                .Select(m => m.Content).Take(10).ToList();

            _task = MemoryExtractService.ExtractAsync(_npcName, _npcDisplayName, existing, _dateFilter, _cts.Token);
            _state = DistillState.Loading;
        }
    }

    // ──────────────────────────────────────────────────────────────
    // 布局自适应与排版测量
    // ──────────────────────────────────────────────────────────────

    private void UpdateLayout()
    {
        width = Math.Clamp(Game1.uiViewport.Width - 120, 740, 940);
        height = Math.Clamp(Game1.uiViewport.Height - 100, 520, 680);

        xPositionOnScreen = (Game1.uiViewport.Width - width) / 2;
        yPositionOnScreen = (Game1.uiViewport.Height - height) / 2;

        _closeButton = new ClickableTextureComponent(
            new Rectangle(xPositionOnScreen + width - 56, yPositionOnScreen + 16, 44, 44),
            Game1.mouseCursors, new Rectangle(337, 494, 12, 12), 3.5f)
        {
            hoverText = I18n.Memory.CloseButton()
        };

        _bodyX = xPositionOnScreen + 48;
        _bodyWidth = width - 96;
        _contentTopY = yPositionOnScreen + 100;
        _visibleHeight = (yPositionOnScreen + height - 36) - _contentTopY;

        MeasureAll();
    }

    private void MeasureAll()
    {
        _cardLayouts.Clear();
        int curY = 0;

        // 1. 若为浓缩模式，计算“源碎片溯源框”
        if (_sourceEntriesToRemove.Count > 0)
        {
            int sourcePadY = 10;
            int textW = _bodyWidth - 32;
            int textTotalH = 0;

            foreach (var src in _sourceEntriesToRemove)
            {
                string line = $"• {src.Content}";
                string wrapped = Game1.parseText(line, Game1.smallFont, textW);
                textTotalH += (int)Game1.smallFont.MeasureString(wrapped).Y + 4;
            }

            int boxH = 26 + textTotalH + sourcePadY * 2;
            _sourceBoxRect = new Rectangle(_bodyX, _contentTopY + curY, _bodyWidth, boxH);
            curY += boxH + 16;
        }
        else
        {
            _sourceBoxRect = Rectangle.Empty;
        }

        // 2. 测量每一张草稿卡片（全宽无截断展开）
        int maxTextPixelWidth = _bodyWidth - CardPaddingX * 2;

        for (int i = 0; i < _candidates.Count; i++)
        {
            string cand = _candidates[i];
            string wrapped = Game1.parseText(cand ?? string.Empty, Game1.dialogueFont, (int)(maxTextPixelWidth / TextFontScale));
            Vector2 textSize = Game1.dialogueFont.MeasureString(wrapped) * TextFontScale;

            const int headerH = 34;
            int cardH = (int)(headerH + textSize.Y + CardPaddingY * 2);

            _cardLayouts.Add(new CandidateCardLayout
            {
                Index = i,
                Content = cand,
                WrappedText = wrapped,
                Height = cardH,
                RelativeY = curY
            });

            curY += cardH + CardSpacing;
        }

        _totalContentHeight = curY;
        ClampScroll();
    }

    private void ClampScroll()
    {
        int maxScroll = Math.Max(0, _totalContentHeight - _visibleHeight);
        _scrollOffset = Math.Clamp(_scrollOffset, 0, maxScroll);
    }

    public override void gameWindowSizeChanged(Rectangle oldBounds, Rectangle newBounds)
    {
        base.gameWindowSizeChanged(oldBounds, newBounds);
        UpdateLayout();
    }

    // ──────────────────────────────────────────────────────────────
    // 异步状态更新与交互
    // ──────────────────────────────────────────────────────────────

    public override void update(GameTime time)
    {
        base.update(time);

        if (_state == DistillState.Loading && _task != null && _task.IsCompleted)
            ApplyTaskResult();
    }

    private void ApplyTaskResult()
    {
        if (Game1.activeClickableMenu != this) return;

        if (_task.IsFaulted || _task.IsCanceled)
        {
            CloseToParent(I18n.Memory.DistillFailed(), 3, "cancel");
            return;
        }

        var result = _task.Result;
        switch (result.Status)
        {
            case MemoryExtractStatus.Success:
                _candidates.Clear();
                if (result.Candidates != null)
                    _candidates.AddRange(result.Candidates);
                _state = DistillState.Ready;
                Game1.playSound("smallSelect");
                MeasureAll();
                break;

            case MemoryExtractStatus.Empty:
                CloseToParent(I18n.Memory.DistillEmpty(), 0, "cancel");
                break;

            case MemoryExtractStatus.NoHistory:
                CloseToParent(I18n.Memory.DistillNoHistory(_npcDisplayName), 0, "cancel");
                break;

            default:
                CloseToParent(I18n.Memory.DistillFailed(), 3, "cancel");
                break;
        }
    }

    public override void receiveLeftClick(int x, int y, bool playSound = true)
    {
        if (_closeButton.containsPoint(x, y))
        {
            CloseByUser();
            return;
        }

        if (_state != DistillState.Ready) return;

        // 检查卡片操作按钮
        foreach (var layout in _cardLayouts)
        {
            int cardY = _contentTopY + layout.RelativeY - _scrollOffset;
            if (cardY + layout.Height < _contentTopY || cardY > _contentTopY + _visibleHeight)
                continue;

            // 1. 微调修改按钮
            if (layout.EditBtnRect.Contains(x, y))
            {
                Game1.playSound("bigSelect");
                int candIdx = layout.Index;
                Game1.activeClickableMenu = new AddMemoryInputMenu(
                    _npcName, this,
                    existingEntry: new MemoryEntry { Content = _candidates[candIdx] },
                    tab: 0,
                    customSubmit: editedText =>
                    {
                        _candidates[candIdx] = editedText;
                        MeasureAll();
                        return MemoryOperationResult.Success;
                    });
                return;
            }

            // 2. 收录按钮
            if (!_usedCandidates.Contains(layout.Content) && layout.AddBtnRect.Contains(x, y))
            {
                CollectCandidate(layout.Index);
                return;
            }
        }
    }

    private void CollectCandidate(int index)
    {
        string text = _candidates[index];
        if (_usedCandidates.Contains(text)) return;

        int currentCap = MemoryManager.Instance.GetTimelineMemories(_npcName, _targetTier).Count;
        int maxCap = MemoryManager.GetTierCapacity(_targetTier);
        if (currentCap >= maxCap)
        {
            Game1.playSound("cancel");
            Game1.addHUDMessage(new HUDMessage(I18n.Memory.AddFailedFull(maxCap), 3));
            return;
        }

        string dateLabel = _dateFilter.HasValue
            ? MemoryManager.FormatGameDateLabel(_dateFilter.Value)
            : MemoryManager.FormatCurrentGameDateLabel();
        int createdDay = _dateFilter.HasValue
            ? MemoryManager.StardewTimeToGameDay(_dateFilter.Value)
            : -1;

        var result = MemoryManager.Instance.AddTimelineMemory(_npcName, text, _targetTier, dateLabel, createdDay);

        // M1：细化结果处理
        if (result == MemoryOperationResult.Success)
        {
            _usedCandidates.Add(text);
            Game1.playSound("coin");
            Game1.addHUDMessage(new HUDMessage(I18n.TimelineDistill.CollectSuccess(), 1));

            // 若由浓缩产生，收录后顺带安全销毁源碎片
            if (_sourceEntriesToRemove.Count > 0)
            {
                MemoryManager.Instance.RemoveTimelineMemories(_npcName, _sourceEntriesToRemove.Select(m => m.Id));
                _sourceEntriesToRemove.Clear();
            }

            MeasureAll();
        }
        else if (result == MemoryOperationResult.Duplicate)
        {
            Game1.playSound("cancel");
            Game1.addHUDMessage(new HUDMessage(I18n.Memory.DistillDuplicate(), 3));
        }
        else if (result == MemoryOperationResult.CapacityFull)
        {
            Game1.playSound("cancel");
            Game1.addHUDMessage(new HUDMessage(I18n.Memory.AddFailedFull(maxCap), 3));
        }
        else
        {
            Game1.playSound("cancel");
            Game1.addHUDMessage(new HUDMessage(I18n.Memory.DistillFailed(), 3));
        }
    }

    public override void receiveScrollWheelAction(int direction)
    {
        if (_state != DistillState.Ready || _totalContentHeight <= _visibleHeight) return;

        _scrollOffset += (direction > 0 ? -48 : 48);
        ClampScroll();
        Game1.playSound("shwip");
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

    public void RefreshEntries()
    {
        MeasureAll();
    }

    // ──────────────────────────────────────────────────────────────
    // 渲染
    // ──────────────────────────────────────────────────────────────

    public override void draw(SpriteBatch b)
    {
        int mx = Game1.getMouseX();
        int my = Game1.getMouseY();
        _hoveredTooltip = string.Empty;

        // 背景半透遮罩与木框
        _returnMenu?.draw(b);
        b.Draw(Game1.fadeToBlackRect, Game1.graphics.GraphicsDevice.Viewport.Bounds, Color.Black * 0.45f);

        IClickableMenu.drawTextureBox(b, xPositionOnScreen - 16, yPositionOnScreen - 16, width + 32, height + 32, Color.White);
        IClickableMenu.drawTextureBox(b, xPositionOnScreen - 8, yPositionOnScreen - 8, width + 16, height + 16, Color.White);
        Game1.drawDialogueBox(xPositionOnScreen, yPositionOnScreen, width, height, false, true);

        // 标题与副标题
        string title = _sourceEntriesToRemove.Count > 0
            ? (_targetTier == MemoryTier.Weekly
                ? I18n.TimelineDistill.TitleWeekly(_npcDisplayName)
                : I18n.TimelineDistill.TitleChronicle(_npcDisplayName))
            : I18n.TimelineDistill.TitleDaily(_npcDisplayName);

        Vector2 titleSize = Game1.dialogueFont.MeasureString(title);
        b.DrawString(Game1.dialogueFont, title,
            new Vector2(xPositionOnScreen + (width - titleSize.X) / 2f, yPositionOnScreen + 22),
            Game1.textColor);

        int currentCap = MemoryManager.Instance.GetTimelineMemories(_npcName, _targetTier).Count;
        int maxCap = MemoryManager.GetTierCapacity(_targetTier);
        string capText = I18n.TimelineDistill.Capacity(currentCap, maxCap);
        // 向上挪移指示器位置至 _contentTopY - 36，避免侵入词条卡片内容区
        b.DrawString(Game1.smallFont, capText, new Vector2(_bodyX, _contentTopY - 36), Color.DimGray);

        // 关闭按钮
        UiHelper.UpdateButtonScale(ref _closeButtonHoverScale, _closeButton, mx, my);
        _closeButton.scale = 3.5f * _closeButtonHoverScale;
        _closeButton.draw(b);

        if (_state == DistillState.Loading)
        {
            string loading = I18n.TimelineDistill.Loading();
            var size = Game1.dialogueFont.MeasureString(loading);
            b.DrawString(Game1.dialogueFont, loading,
                new Vector2(xPositionOnScreen + (width - size.X) / 2f, yPositionOnScreen + (height - size.Y) / 2f),
                Game1.textColor);
        }
        else
        {
            DrawContent(b, mx, my);
        }

        if (!string.IsNullOrEmpty(_hoveredTooltip))
            IClickableMenu.drawHoverText(b, _hoveredTooltip, Game1.smallFont);

        drawMouse(b);
    }

    private void DrawContent(SpriteBatch b, int mx, int my)
    {
        // 开启裁剪视口防止内容穿模
        var oldScissor = b.GraphicsDevice.ScissorRectangle;
        var scissorRect = new Rectangle(_bodyX - 4, _contentTopY, _bodyWidth + 24, _visibleHeight);
        b.End();
        b.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp, DepthStencilState.None,
            new RasterizerState { CullMode = CullMode.None, ScissorTestEnable = true });
        b.GraphicsDevice.ScissorRectangle = Rectangle.Intersect(oldScissor, scissorRect);

        // 1. 绘制源碎片溯源框（浓缩模式）
        if (_sourceEntriesToRemove.Count > 0 && _sourceBoxRect != Rectangle.Empty)
        {
            int boxY = _sourceBoxRect.Y - _scrollOffset;
            var curBox = new Rectangle(_sourceBoxRect.X, boxY, _sourceBoxRect.Width, _sourceBoxRect.Height);

            IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(432, 439, 9, 9),
                curBox.X, curBox.Y, curBox.Width, curBox.Height,
                new Color(245, 235, 220), 3f, false);

            string srcHeader = I18n.TimelineDistill.SourceHeader(_sourceEntriesToRemove.Count);
            b.DrawString(Game1.smallFont, srcHeader, new Vector2(curBox.X + 14, curBox.Y + 8), new Color(110, 70, 30));

            int lineY = curBox.Y + 30;
            int textW = _bodyWidth - 32;
            foreach (var src in _sourceEntriesToRemove)
            {
                string wrapped = Game1.parseText($"• {src.Content}", Game1.smallFont, textW);
                b.DrawString(Game1.smallFont, wrapped, new Vector2(curBox.X + 16, lineY), Color.DimGray);
                lineY += (int)Game1.smallFont.MeasureString(wrapped).Y + 4;
            }
        }

        // 2. 绘制草稿卡片流
        foreach (var layout in _cardLayouts)
        {
            int cardY = _contentTopY + layout.RelativeY - _scrollOffset;
            if (cardY + layout.Height < _contentTopY - 20 || cardY > _contentTopY + _visibleHeight + 20)
                continue;

            var cardRect = new Rectangle(_bodyX, cardY, _bodyWidth, layout.Height);
            bool isUsed = _usedCandidates.Contains(layout.Content);
            bool cardHover = cardRect.Contains(mx, my);

            // 卡片底框
            Color cardColor = isUsed
                ? new Color(240, 240, 240)
                : (cardHover ? new Color(255, 248, 230) : new Color(252, 244, 234));

            IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(432, 439, 9, 9),
                cardRect.X, cardRect.Y, cardRect.Width, cardRect.Height,
                cardColor, 3.5f, false);

            // 卡片 Header：标签
            string draftTag = I18n.TimelineDistill.DraftTag(layout.Index + 1);
            b.DrawString(Game1.smallFont, draftTag,
                new Vector2(cardRect.X + CardPaddingX, cardRect.Y + CardPaddingY),
                new Color(130, 85, 45));

            // 卡片 Header 右侧按钮：微调 & 收录
            int btnRightX = cardRect.Right - CardPaddingX;
            int btnY = cardRect.Y + CardPaddingY - 2;

            if (!isUsed)
            {
                // 收录按钮
                string addText = I18n.TimelineDistill.CollectButton();
                int addW = (int)Game1.smallFont.MeasureString(addText).X + 24;
                layout.AddBtnRect = new Rectangle(btnRightX - addW, btnY, addW, 28);
                bool addHover = layout.AddBtnRect.Contains(mx, my);

                IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(432, 439, 9, 9),
                    layout.AddBtnRect.X, layout.AddBtnRect.Y, layout.AddBtnRect.Width, layout.AddBtnRect.Height,
                    addHover ? new Color(255, 235, 205) : new Color(139, 90, 43), 3f, false);

                var addSize = Game1.smallFont.MeasureString(addText);
                b.DrawString(Game1.smallFont, addText,
                    new Vector2(layout.AddBtnRect.X + (layout.AddBtnRect.Width - addSize.X) / 2f,
                                layout.AddBtnRect.Y + (layout.AddBtnRect.Height - addSize.Y) / 2f),
                    addHover ? Game1.textColor : Color.White);

                // 微调按钮 (铅笔)
                layout.EditBtnRect = new Rectangle(layout.AddBtnRect.X - 34, btnY, 28, 28);
                bool editHover = layout.EditBtnRect.Contains(mx, my);

                IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(432, 439, 9, 9),
                    layout.EditBtnRect.X, layout.EditBtnRect.Y, layout.EditBtnRect.Width, layout.EditBtnRect.Height,
                    editHover ? new Color(255, 235, 205) : Color.White, 2f, false);

                float iconScale = 1.6f;
                int iconW = (int)(EditIconSource.Width * iconScale);
                int iconH = (int)(EditIconSource.Height * iconScale);
                Vector2 iconPos = new Vector2(
                    layout.EditBtnRect.X + (layout.EditBtnRect.Width - iconW) / 2f,
                    layout.EditBtnRect.Y + (layout.EditBtnRect.Height - iconH) / 2f);
                b.Draw(Game1.mouseCursors, iconPos, EditIconSource, editHover ? Color.White : Color.DimGray, 0f, Vector2.Zero, iconScale, SpriteEffects.None, 0.86f);

                if (layout.EditBtnRect.Contains(mx, my))
                    _hoveredTooltip = I18n.TimelineDistill.EditTooltip();
            }
            else
            {
                layout.AddBtnRect = Rectangle.Empty;
                layout.EditBtnRect = Rectangle.Empty;
                string stamped = I18n.TimelineDistill.CollectedStamp();
                var stampSize = Game1.smallFont.MeasureString(stamped);
                b.DrawString(Game1.smallFont, stamped,
                    new Vector2(btnRightX - stampSize.X, btnY + 4),
                    new Color(40, 140, 60));
            }

            // 卡片内容文字（完全不截断自适应展开）
            Vector2 textPos = new Vector2(cardRect.X + CardPaddingX, cardRect.Y + CardPaddingY + 28);
            Color contentColor = isUsed ? Color.Gray * 0.7f : Game1.textColor;
            b.DrawString(Game1.dialogueFont, layout.WrappedText, textPos, contentColor, 0f, Vector2.Zero, TextFontScale, SpriteEffects.None, 0.88f);
        }

        b.End();
        b.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp, DepthStencilState.None,
            new RasterizerState { CullMode = CullMode.None, ScissorTestEnable = false });
        b.GraphicsDevice.ScissorRectangle = oldScissor;

        // 3. 滚动条
        if (_totalContentHeight > _visibleHeight)
        {
            int trackX = _bodyX + _bodyWidth + 8;
            int trackH = _visibleHeight;
            b.Draw(Game1.staminaRect, new Rectangle(trackX, _contentTopY, 6, trackH), Color.Black * 0.22f);

            float ratio = (float)_visibleHeight / _totalContentHeight;
            int thumbH = Math.Max(32, (int)(trackH * ratio));
            int maxScroll = _totalContentHeight - _visibleHeight;
            int thumbY = _contentTopY + (int)((trackH - thumbH) * ((float)_scrollOffset / maxScroll));
            b.Draw(Game1.staminaRect, new Rectangle(trackX, thumbY, 6, thumbH), Color.Wheat * 0.85f);
        }
    }

    private void CloseToParent(string hudMessage, int hudKind, string sound)
    {
        if (!string.IsNullOrEmpty(sound)) Game1.playSound(sound);
        if (!string.IsNullOrEmpty(hudMessage)) Game1.addHUDMessage(new HUDMessage(hudMessage, hudKind));
        if (_returnMenu is IMemoryRefreshTarget refreshable) refreshable.RefreshEntries();
        exitThisMenu(playSound: false);
    }

    private void CloseByUser()
    {
        if (_state == DistillState.Loading) _cts.Cancel();
        Game1.playSound("bigDeSelect");
        if (_returnMenu is IMemoryRefreshTarget refreshable) refreshable.RefreshEntries();
        exitThisMenu(playSound: false);
    }

    protected override void cleanupBeforeExit()
    {
        base.cleanupBeforeExit();
        _cts.Cancel();
        if (_returnMenu != null && Game1.activeClickableMenu == this)
            Game1.activeClickableMenu = _returnMenu;
    }

    private class CandidateCardLayout
    {
        public int Index;
        public string Content;
        public string WrappedText;
        public int Height;
        public int RelativeY;
        public Rectangle AddBtnRect;
        public Rectangle EditBtnRect;
    }
}