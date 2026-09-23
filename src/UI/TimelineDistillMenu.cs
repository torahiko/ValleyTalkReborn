#nullable enable

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
using ValleytalkReborn.UI;

namespace ValleytalkReborn;

/// <summary>
/// 时间线手账专属提炼/浓缩卡片菜单：
/// 遵循 BioEditorMenu 整体设计规范 —— 羊皮纸无缝底板、立体按压下沉动效、
/// 肖像感知、卡片层级高反差排版与 CustomFontManager 字体字重体系。
/// </summary>
internal class TimelineDistillMenu : IClickableMenu, IMemoryRefreshTarget
{
    private enum DistillState { Loading, Ready, Failed }

    // ── 尺寸与排版常量 ──
    private const int CardPaddingX = 20;
    private const int CardPaddingY = 14;
    private const int CardSpacing = 14;
    private const int HeaderH = 68;
    private const int ContentPadding = 24;

    private const float TitleFontSize = CustomFontManager.SizeTitle;       // 24f Bold (顶栏主标题)
    private const float ButtonFontSize = CustomFontManager.SizeRegular;    // 18f Bold (收录主按钮)
    private const float SectionHeaderSize = CustomFontManager.SizeRegular; // 18f Medium (草稿标号与溯源标题)
    private const float ContentFontSize = CustomFontManager.SizeRegular;   // 18f Medium (草稿正文)
    private const float TipFontSize = CustomFontManager.SizeSmall;         // 15f Medium (容量与说明)

    private readonly string _npcName;
    private readonly string _npcDisplayName;
    private readonly IClickableMenu? _returnMenu;
    private readonly MemoryTier _targetTier;
    private readonly List<MemoryEntry> _sourceEntriesToRemove;
    private readonly StardewTime? _dateFilter;
    private readonly CancellationTokenSource _cts = new();

    // 肖像与关闭按钮
    private Texture2D? _npcPortrait;
    private Rectangle _portraitSmileRect;
    private ClickableTextureComponent _closeButton = null!;
    private float _closeButtonHoverScale = 1f;
    private const float CloseButtonBaseScale = 3f;

    private DistillState _state = DistillState.Loading;
    private Task<MemoryExtractResult>? _task;
    private readonly List<string> _candidates = new();
    private readonly HashSet<string> _usedCandidates = new(StringComparer.OrdinalIgnoreCase);

    // 动态滚动与视口
    private int _contentTopY;
    private int _visibleHeight;
    private int _bodyWidth;
    private int _bodyX;
    private int _scrollOffset;
    private int _totalContentHeight;
    private string _hoveredTooltip = string.Empty;

    // 预排版缓存
    private readonly List<CandidateCardLayout> _cardLayouts = new();
    private Rectangle _sourceBoxRect = Rectangle.Empty;

    public TimelineDistillMenu(
        string npcName,
        IClickableMenu? returnMenu,
        MemoryTier targetTier = MemoryTier.Daily,
        List<MemoryEntry>? sourceEntriesToRemove = null,
        Task<MemoryExtractResult>? customTask = null,
        StardewTime? dateFilter = null)
    {
        _npcName = npcName;
        _returnMenu = returnMenu;
        _npcDisplayName = Game1.getCharacterFromName(npcName)?.displayName ?? npcName;
        _targetTier = targetTier;
        _sourceEntriesToRemove = sourceEntriesToRemove != null ? new List<MemoryEntry>(sourceEntriesToRemove) : new List<MemoryEntry>();
        _dateFilter = dateFilter;

        LoadNpcPortrait();
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

    private void LoadNpcPortrait()
    {
        try
        {
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

    private void UpdateLayout()
    {
        width = Math.Clamp(Game1.uiViewport.Width - 120, 780, 960);
        height = Math.Clamp(Game1.uiViewport.Height - 100, 540, 700);

        xPositionOnScreen = (Game1.uiViewport.Width - width) / 2;
        yPositionOnScreen = (Game1.uiViewport.Height - height) / 2;

        _closeButton = new ClickableTextureComponent(
            new Rectangle(xPositionOnScreen + width - 50, yPositionOnScreen + 16, 36, 36),
            Game1.mouseCursors, new Rectangle(337, 494, 12, 12), CloseButtonBaseScale)
        {
            hoverText = I18n.Memory.CloseButton()
        };

        _bodyX = xPositionOnScreen + ContentPadding;
        _bodyWidth = width - ContentPadding * 2;
        _contentTopY = yPositionOnScreen + HeaderH + 14;
        _visibleHeight = (yPositionOnScreen + height - 20) - _contentTopY;

        MeasureAll();
    }

    private void MeasureAll()
    {
        _cardLayouts.Clear();
        int curY = 0;

        // 1. 若为浓缩模式，计算“源碎片溯源框”
        if (_sourceEntriesToRemove.Count > 0)
        {
            int sourcePadY = 12;
            int textW = _bodyWidth - 36;
            int textTotalH = 0;

            foreach (var src in _sourceEntriesToRemove)
            {
                string line = $"• {src.Content}";
                string wrapped = Game1.parseText(line, Game1.smallFont, textW);
                textTotalH += (int)CustomFontManager.MeasureString(wrapped, ContentFontSize).Y + 6;
            }

            int boxH = 34 + textTotalH + sourcePadY * 2;
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
            string wrapped = Game1.parseText(cand ?? string.Empty, Game1.smallFont, maxTextPixelWidth);
            Vector2 textSize = CustomFontManager.MeasureString(wrapped, ContentFontSize);

            const int headerH = 38;
            int cardH = (int)(headerH + textSize.Y + CardPaddingY * 2);

            _cardLayouts.Add(new CandidateCardLayout
            {
                Index = i,
                Content = cand ?? string.Empty,
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

    public override void update(GameTime time)
    {
        base.update(time);

        if (_state == DistillState.Loading && _task != null && _task.IsCompleted)
            ApplyTaskResult();
    }

    private void ApplyTaskResult()
    {
        if (Game1.activeClickableMenu != this) return;

        if (_task == null || _task.IsFaulted || _task.IsCanceled)
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

        foreach (var layout in _cardLayouts)
        {
            int cardY = _contentTopY + layout.RelativeY - _scrollOffset;
            if (cardY + layout.Height < _contentTopY || cardY > _contentTopY + _visibleHeight)
                continue;

            int btnRightX = _bodyX + _bodyWidth - CardPaddingX;
            int btnY = cardY + CardPaddingY;

            // 1. 微调修改按钮
            var editRect = new Rectangle(btnRightX - 110 - 36, btnY - 2, 30, 30);
            if (!_usedCandidates.Contains(layout.Content) && editRect.Contains(x, y))
            {
                Game1.playSound("bigSelect");
                int candIdx = layout.Index;
                Game1.activeClickableMenu = new AddRuleInputMenu(
                    _npcName, this,
                    new MemoryEntry { Content = _candidates[candIdx] },
                    0,
                    customSubmit: editedText =>
                    {
                        _candidates[candIdx] = editedText;
                        MeasureAll();
                        return MemoryOperationResult.Success;
                    });
                return;
            }

            // 2. 收录按钮
            var addRect = new Rectangle(btnRightX - 110, btnY, 110, 30);
            if (!_usedCandidates.Contains(layout.Content) && addRect.Contains(x, y))
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

        int createdDay;
        if (_dateFilter.HasValue)
        {
            createdDay = MemoryManager.StardewTimeToGameDay(_dateFilter.Value);
        }
        else if (_sourceEntriesToRemove.Count > 0)
        {
            createdDay = _sourceEntriesToRemove.Max(m => m.CreatedDay);
        }
        else
        {
            createdDay = -1;
        }

        StardewTime targetTime = createdDay > 0
            ? MemoryManager.GameDayToStardewTime(createdDay)
            : new StardewTime(Game1.Date, Game1.timeOfDay);

        string dateLabel = MemoryManager.GenerateDateLabel(_targetTier, targetTime);

        var result = MemoryManager.Instance.AddTimelineMemory(_npcName, text, _targetTier, dateLabel, createdDay);

        if (result == MemoryOperationResult.Success)
        {
            _usedCandidates.Add(text);
            Game1.playSound("coin");
            Game1.addHUDMessage(new HUDMessage(I18n.TimelineDistill.CollectSuccess(), 1));

            if (_sourceEntriesToRemove.Count > 0)
            {
                MemoryManager.Instance.ArchiveTimelineMemories(_npcName, _sourceEntriesToRemove, "Distilled");
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

        if (Game1.options.doesInputListContain(Game1.options.menuButton, key))
            return;

        base.receiveKeyPress(key);
    }

    public void RefreshEntries()
    {
        MeasureAll();
    }

    // ── 渲染管线 ──────────────────────────────────────────────────────────

    public override void draw(SpriteBatch b)
    {
        int mx = Game1.getMouseX();
        int my = Game1.getMouseY();
        _hoveredTooltip = string.Empty;

        _returnMenu?.draw(b);
        b.Draw(Game1.fadeToBlackRect, Game1.graphics.GraphicsDevice.Viewport.Bounds, Color.Black * 0.5f);

        // 1. 双层羊皮纸木框底板
        IClickableMenu.drawTextureBox(b, xPositionOnScreen - 8, yPositionOnScreen - 8, width + 16, height + 16, Color.White);
        b.Draw(Game1.staminaRect, new Rectangle(xPositionOnScreen, yPositionOnScreen, width, height), new Color(245, 230, 205));
        b.Draw(
            Game1.menuTexture,
            new Rectangle(xPositionOnScreen, yPositionOnScreen, width, height),
            new Rectangle(64, 128, 64, 64),
            new Color(245, 230, 205));

        DrawHeader(b, mx, my);

        // 2. 内容区渲染
        if (_state == DistillState.Loading)
        {
            DrawLoadingState(b);
        }
        else
        {
            DrawContent(b, mx, my);
        }

        if (!string.IsNullOrEmpty(_hoveredTooltip))
            DrawHoverTextCustom(b, _hoveredTooltip);

        drawMouse(b);
    }

    private void DrawHeader(SpriteBatch b, int mx, int my)
    {
        int headX = xPositionOnScreen + ContentPadding;
        int headY = yPositionOnScreen + 14;

        // 44px 肖像框
        const int pSize = 44;
        var portraitRect = new Rectangle(headX, headY, pSize, pSize);

        b.Draw(Game1.staminaRect, new Rectangle(portraitRect.X - 1, portraitRect.Y - 1, portraitRect.Width + 2, portraitRect.Height + 2), new Color(225, 210, 185));
        IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(403, 383, 6, 6),
            portraitRect.X - 2, portraitRect.Y - 2, portraitRect.Width + 4, portraitRect.Height + 4,
            new Color(200, 175, 140), 2f, false);

        if (_npcPortrait != null && !_portraitSmileRect.IsEmpty)
            b.Draw(_npcPortrait, portraitRect, _portraitSmileRect, Color.White);
        else
        {
            string avatarFallback = string.IsNullOrEmpty(_npcName) ? "?" : _npcName.Substring(0, 1);
            var fsz = CustomFontManager.MeasureStringBold(avatarFallback, TitleFontSize);
            CustomFontManager.DrawStringBold(b, avatarFallback,
                new Vector2(portraitRect.X + (pSize - fsz.X) / 2f, portraitRect.Y + (pSize - fsz.Y) / 2f - 1),
                BioEditorMenu.TextMuted, TitleFontSize);
        }

        // 主标题
        string title = _sourceEntriesToRemove.Count > 0
            ? (_targetTier == MemoryTier.Weekly
                ? I18n.TimelineDistill.TitleWeekly(_npcDisplayName)
                : I18n.TimelineDistill.TitleChronicle(_npcDisplayName))
            : I18n.TimelineDistill.TitleDaily(_npcDisplayName);

        CustomFontManager.DrawStringBold(b, title, new Vector2(headX + pSize + 12, headY + 2), BioEditorMenu.TextPrimary, TitleFontSize);

        // 副说明与容量标签
        int currentCap = MemoryManager.Instance.GetTimelineMemories(_npcName, _targetTier).Count;
        int maxCap = MemoryManager.GetTierCapacity(_targetTier);
        string capText = I18n.TimelineDistill.Capacity(currentCap, maxCap);
        string subDesc = $"{capText}  ·  选择最能代表角色心路历程的卡片收录入记忆手账";
        CustomFontManager.DrawString(b, subDesc, new Vector2(headX + pSize + 14, headY + 28), BioEditorMenu.TextSecondary, TipFontSize);

        // 分割横线
        int sepY = yPositionOnScreen + HeaderH + 4;
        b.Draw(Game1.staminaRect, new Rectangle(headX, sepY, width - ContentPadding * 2, 2), Color.Gray * 0.35f);

        // 关闭按钮平滑缩放动效
        UiHelper.UpdateButtonScale(ref _closeButtonHoverScale, _closeButton, mx, my);
        _closeButton.scale = CloseButtonBaseScale * _closeButtonHoverScale;
        _closeButton.draw(b);
    }

    private void DrawLoadingState(SpriteBatch b)
    {
        var boxRect = new Rectangle(_bodyX + 40, _contentTopY + 60, _bodyWidth - 80, 160);

        b.Draw(Game1.staminaRect, new Rectangle(boxRect.X + 2, boxRect.Y + 2, boxRect.Width, boxRect.Height), Color.Black * 0.08f);
        b.Draw(Game1.staminaRect, boxRect, new Color(255, 252, 245));
        IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(432, 439, 9, 9),
            boxRect.X, boxRect.Y, boxRect.Width, boxRect.Height, new Color(223, 122, 4) * 0.65f, 2f, false);

        string loadingTitle = "✨ 正在萃取并构思提炼方案…";
        var tsz = CustomFontManager.MeasureStringBold(loadingTitle, TitleFontSize);
        CustomFontManager.DrawStringBold(b, loadingTitle,
            new Vector2(boxRect.X + (boxRect.Width - tsz.X) / 2f, boxRect.Y + 44),
            BioEditorMenu.TextPrimary, TitleFontSize);

        string loadingDesc = I18n.TimelineDistill.Loading();
        var dsz = CustomFontManager.MeasureString(loadingDesc, TipFontSize);
        CustomFontManager.DrawString(b, loadingDesc,
            new Vector2(boxRect.X + (boxRect.Width - dsz.X) / 2f, boxRect.Y + 88),
            BioEditorMenu.TextMuted, TipFontSize);
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

            b.Draw(Game1.staminaRect, new Rectangle(curBox.X + 2, curBox.Y + 2, curBox.Width, curBox.Height), Color.Black * 0.08f);
            b.Draw(Game1.staminaRect, curBox, new Color(250, 242, 228));
            IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(432, 439, 9, 9),
                curBox.X, curBox.Y, curBox.Width, curBox.Height,
                new Color(223, 122, 4) * 0.7f, 2f, false);

            string srcHeader = $"✦ {I18n.TimelineDistill.SourceHeader(_sourceEntriesToRemove.Count)}";
            CustomFontManager.DrawString(b, srcHeader, new Vector2(curBox.X + 16, curBox.Y + 10), BioEditorMenu.TextPrimary, SectionHeaderSize);

            int lineY = curBox.Y + 36;
            int textW = _bodyWidth - 36;
            foreach (var src in _sourceEntriesToRemove)
            {
                string wrapped = Game1.parseText($"• {src.Content}", Game1.smallFont, textW);
                CustomFontManager.DrawString(b, wrapped, new Vector2(curBox.X + 18, lineY), BioEditorMenu.TextSecondary, ContentFontSize);
                lineY += (int)CustomFontManager.MeasureString(wrapped, ContentFontSize).Y + 6;
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

            // 卡片阴影
            b.Draw(Game1.staminaRect, new Rectangle(cardRect.X + 2, cardRect.Y + 2, cardRect.Width, cardRect.Height), Color.Black * 0.08f);

            // 卡片底色与边框
            Color bg = isUsed ? new Color(245, 240, 235)
                     : (cardHover ? new Color(255, 248, 236) : new Color(255, 252, 246));

            Color borderCol = isUsed ? new Color(215, 205, 195)
                            : (cardHover ? new Color(210, 160, 60) : new Color(225, 200, 160));

            b.Draw(Game1.staminaRect, new Rectangle(cardRect.X + 1, cardRect.Y + 1, cardRect.Width - 2, cardRect.Height - 2), bg);
            IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(432, 439, 9, 9),
                cardRect.X, cardRect.Y, cardRect.Width, cardRect.Height, borderCol, 2f, false);

            // 未收录项左侧金色高亮条
            if (!isUsed)
            {
                b.Draw(Game1.staminaRect, new Rectangle(cardRect.X + 2, cardRect.Y + 4, 4, cardRect.Height - 8), new Color(223, 122, 4));
            }

            // 卡片 Header：标签
            string draftTag = $"✦ {I18n.TimelineDistill.DraftTag(layout.Index + 1)}";
            CustomFontManager.DrawString(b, draftTag,
                new Vector2(cardRect.X + CardPaddingX, cardRect.Y + CardPaddingY + 2),
                isUsed ? BioEditorMenu.TextMuted : BioEditorMenu.TextSecondary,
                SectionHeaderSize);

            // 卡片 Header 右侧按钮：微调 & 收录
            int btnRightX = cardRect.Right - CardPaddingX;
            int btnY = cardRect.Y + CardPaddingY;

            if (!isUsed)
            {
                // 收录按钮
                var addRect = new Rectangle(btnRightX - 110, btnY, 110, 30);
                string addText = $"✔ {I18n.TimelineDistill.CollectButton()}";
                DrawActionButton(b, addRect, addText, mx, my, isPrimary: true);

                // 微调按钮 (铅笔)
                var editRect = new Rectangle(addRect.X - 36, btnY - 2, 30, 30);
                bool editHover = editRect.Contains(mx, my);
                bool editPressed = editHover && IsLeftMouseDown();

                IconSource.DrawButton(
                    b,
                    ModEntry.CustomIcons,
                    editRect,
                    col: 15, baseRow: 1,
                    theme: IconTheme.Wood,
                    isPressed: editPressed,
                    layerDepth: 0.89f);

                if (editHover)
                    _hoveredTooltip = I18n.TimelineDistill.EditTooltip();
            }
            else
            {
                string stamped = $"✔ {I18n.TimelineDistill.CollectedStamp()}";
                var stampSize = CustomFontManager.MeasureString(stamped, SectionHeaderSize);
                CustomFontManager.DrawString(b, stamped,
                    new Vector2(btnRightX - stampSize.X, btnY + 4),
                    BioEditorMenu.TextSuccess, SectionHeaderSize);
            }

            // 卡片正文文字
            Vector2 textPos = new Vector2(cardRect.X + CardPaddingX + 4, cardRect.Y + CardPaddingY + 34);
            Color contentColor = isUsed ? BioEditorMenu.TextMuted : BioEditorMenu.TextPrimary;
            CustomFontManager.DrawString(b, layout.WrappedText, textPos, contentColor, ContentFontSize);
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
            b.Draw(Game1.staminaRect, new Rectangle(trackX, _contentTopY, 6, trackH), new Color(225, 215, 200));

            float ratio = (float)_visibleHeight / _totalContentHeight;
            int thumbH = Math.Max(32, (int)(trackH * ratio));
            int maxScroll = _totalContentHeight - _visibleHeight;
            int thumbY = _contentTopY + (int)((trackH - thumbH) * ((float)_scrollOffset / maxScroll));

            var thumbRect = new Rectangle(trackX - 1, thumbY, 8, thumbH);
            b.Draw(Game1.staminaRect, thumbRect, new Color(190, 130, 85));
            IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(432, 439, 9, 9),
                thumbRect.X, thumbRect.Y, thumbRect.Width, thumbRect.Height, new Color(110, 70, 35), 1.5f, false);
        }
    }

    private static bool IsLeftMouseDown()
    {
        try { return Game1.input.GetMouseState().LeftButton == ButtonState.Pressed; }
        catch { return false; }
    }

    private static void DrawActionButton(SpriteBatch b, Rectangle rect, string label, int mx, int my,
        bool isDanger = false, bool isPrimary = false, bool isEnabled = true)
    {
        bool isHover = isEnabled && rect.Contains(mx, my);
        bool isPressed = isHover && IsLeftMouseDown();

        Color bg;
        if (!isEnabled) bg = Color.LightGray * 0.6f;
        else if (isPrimary) bg = isHover ? Color.Gold : new Color(255, 220, 130);
        else if (isDanger) bg = isHover ? new Color(245, 105, 105) : new Color(210, 85, 80);
        else bg = isHover ? new Color(255, 240, 215) : new Color(225, 195, 155);

        int pressOffset = isPressed ? 1 : 0;
        if (isPressed) bg = Color.Lerp(bg, Color.Black, 0.14f);

        if (!isPressed)
            b.Draw(Game1.staminaRect, new Rectangle(rect.X + 2, rect.Y + 2, rect.Width, rect.Height), Color.Black * 0.15f);

        b.Draw(Game1.staminaRect, new Rectangle(rect.X + 1 + pressOffset, rect.Y + 1 + pressOffset, rect.Width - 2, rect.Height - 2), bg);

        IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(432, 439, 9, 9),
            rect.X + pressOffset, rect.Y + pressOffset, rect.Width, rect.Height,
            isPrimary ? new Color(210, 160, 60) : (isDanger ? new Color(175, 60, 55) : new Color(185, 150, 110)), 3f, false);

        Color textCol = !isEnabled ? BioEditorMenu.TextMuted
                      : isDanger ? BioEditorMenu.TextOnDarkBtn
                      : BioEditorMenu.TextOnLightBtn;

        var sz = CustomFontManager.MeasureStringBold(label, ButtonFontSize);
        CustomFontManager.DrawStringBold(b, label,
            new Vector2(
                rect.X + pressOffset + (rect.Width - sz.X) / 2f,
                rect.Y + pressOffset + (rect.Height - sz.Y) / 2f),
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
        public string Content = string.Empty;
        public string WrappedText = string.Empty;
        public int Height;
        public int RelativeY;
    }
}