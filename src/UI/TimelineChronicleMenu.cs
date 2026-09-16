using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewValley;
using StardewValley.Menus;

namespace ValleytalkReborn;

/// <summary>
/// 时间线手账面板（FEAT-MEM-300-T4）：
/// Tab0 按日翻页的对话记录查看（自适应气泡宽度、无截断多行展开）；
/// Tab1-3 分层记忆（Daily/Weekly/Chronicle）自适应卡片勾选与浓缩。
/// </summary>
internal class TimelineChronicleMenu : IClickableMenu, IMemoryRefreshTarget
{
    private const int TabBarHeight = 44;
    private const int BottomBarHeight = 46;
    private const int LeftPadding = 48;
    private const int RightPadding = 48;
    private const int ItemSpacing = 10;
    private const float TextFontScale = 0.82f; // 文本渲染缩放，兼顾清晰度与排版容量

    // 原版 Checkbox 贴图切片 (mouseCursors)
    private const int CheckboxUncheckedSourceX = 227;
    private const int CheckboxCheckedSourceX = 236;
    private const int CheckboxSourceY = 425;
    private const int CheckboxSize = 9;
    private const float CheckboxScale = 3.5f;

    // 原版图标贴图切片 (mouseCursors)
    private static readonly Rectangle LeftArrowSource = new(352, 495, 12, 11);
    private static readonly Rectangle RightArrowSource = new(365, 495, 12, 11);
    private static readonly Rectangle DeleteIconSource = new(269, 471, 15, 15); // 原版红叉
    private static readonly Rectangle EditIconSource = new(274, 412, 11, 11);   // 原版编辑铅笔

    private const int MaxHistoryDays = 7;
    private const int CooldownSeconds = 30;
    private const int MinCondenseSelection = 2;

    // 总结冷却
    private static long _lastSummarizeTickMs = long.MinValue;

    private readonly string _npcName;
    private readonly string _npcDisplayName;
    private readonly IClickableMenu _returnMenu;
    private readonly IClickableMenu _ownerMenu;

    private int _currentTab; // 0=今日对话, 1=Daily, 2=Weekly, 3=Chronicle
    private int _daysAgo;
    private readonly List<DialogueHistoryEntry> _todayChatEntries = new();
    private List<MemoryEntry> _tierEntries = new();
    private readonly HashSet<string> _selectedEntryIds = new(StringComparer.OrdinalIgnoreCase);

    // 动态排版与滚动
    private readonly List<MeasuredEntry> _measuredEntries = new();
    private readonly List<VisibleEntryLayout> _visibleLayouts = new();
    private int _startIndex;

    private int _contentTopY;
    private int _tabBarY;
    private Rectangle _leftArrowRect;
    private Rectangle _rightArrowRect;
    private Rectangle _actionButtonRect; // 底部居中按钮
    private ClickableTextureComponent _closeButton;
    private float _closeButtonHoverScale = 1f;
    private string _hoveredTooltip = string.Empty;

    private StardewTime ViewDate => new StardewTime(Game1.Date, Game1.timeOfDay).AddDays(-_daysAgo);

    public TimelineChronicleMenu(string npcName, IClickableMenu returnMenu, IClickableMenu ownerMenu = null)
    {
        _npcName = npcName;
        _returnMenu = returnMenu;
        _ownerMenu = ownerMenu;
        _npcDisplayName = Game1.getCharacterFromName(npcName)?.displayName ?? npcName;

        UpdateLayout();
        RefreshEntries();
    }

    // ──────────────────────────────────────────────────────────────
    // 数据刷新与预排版计算
    // ──────────────────────────────────────────────────────────────

    public void RefreshEntries()
    {
        _selectedEntryIds.Clear();
        _startIndex = 0;

        if (_currentTab == 0)
            RefreshPagedChat();
        else
        {
            _tierEntries = MemoryManager.Instance.GetTimelineMemories(_npcName, TierForTab(_currentTab));
            MeasureAllEntries();
        }

        UpdateActionButtonLayout();
        RebuildVisibleLayout();
    }

    private void RefreshPagedChat()
    {
        StardewTime view = ViewDate;
        var all = DialogueHistoryManager.Instance.GetHistory(_npcName);

        _todayChatEntries.Clear();
        foreach (var e in all)
        {
            if (e.DialogueType == "eavesdrop") continue;
            if (string.IsNullOrWhiteSpace(e.Text)) continue;
            if (e.Timestamp.Year != view.Year || e.Timestamp.Season != view.Season || e.Timestamp.DayOfMonth != view.DayOfMonth) continue;
            _todayChatEntries.Add(e);
        }
        _startIndex = 0;
        MeasureAllEntries();
        UpdateActionButtonLayout();
        RebuildVisibleLayout();
    }

    private static MemoryTier TierForTab(int tab) => tab switch
    {
        1 => MemoryTier.Daily,
        2 => MemoryTier.Weekly,
        _ => MemoryTier.Chronicle
    };

    /// <summary>
    /// 对当前 Tab 的全部文本进行自适应折行与宽高预测量（无任何文字截断）
    /// </summary>
    private void MeasureAllEntries()
    {
        _measuredEntries.Clear();
        int availableWidth = width - LeftPadding - RightPadding;

        if (_currentTab == 0)
        {
            int maxBubbleWidth = Math.Max(260, (int)(availableWidth * 0.85f));
            int minBubbleWidth = 140;
            int padX = 14;
            int padY = 10;

            for (int i = 0; i < _todayChatEntries.Count; i++)
            {
                var entry = _todayChatEntries[i];
                string speaker = entry.SpeakerType switch
                {
                    SpeakerType.Player => I18n.IsChinese ? "农夫" : "Farmer",
                    SpeakerType.System => I18n.IsChinese ? "[场景]" : "[Scene]",
                    _ => _npcDisplayName
                };

                int maxTextPixelWidth = maxBubbleWidth - padX * 2;
                string wrapped = Game1.parseText(entry.Text ?? string.Empty, Game1.dialogueFont, (int)(maxTextPixelWidth / TextFontScale));
                Vector2 textSize = Game1.dialogueFont.MeasureString(wrapped) * TextFontScale;
                Vector2 speakerSize = Game1.smallFont.MeasureString(speaker);

                float contentInnerWidth = Math.Max(textSize.X, speakerSize.X);
                int bubbleWidth = (int)Math.Clamp(contentInnerWidth + padX * 2, minBubbleWidth, maxBubbleWidth);
                int bubbleHeight = (int)(speakerSize.Y + 4 + textSize.Y + padY * 2);

                _measuredEntries.Add(new MeasuredEntry
                {
                    Index = i,
                    Id = i.ToString(),
                    Speaker = speaker,
                    SpeakerType = entry.SpeakerType,
                    WrappedText = wrapped,
                    Width = bubbleWidth,
                    Height = bubbleHeight,
                    InnerPadding = new Point(padX, padY)
                });
            }
        }
        else
        {
            bool hasCheckbox = _currentTab == 1 || _currentTab == 2;
            int checkboxTotalOffset = hasCheckbox ? 44 : 0;
            int maxCardWidth = availableWidth - checkboxTotalOffset;
            int minCardWidth = Math.Min(300, maxCardWidth);
            int padX = 16;
            int padY = 12;

            for (int i = 0; i < _tierEntries.Count; i++)
            {
                var entry = _tierEntries[i];
                string dateLabel = string.IsNullOrEmpty(entry.DateLabel) ? "--" : entry.DateLabel;

                // 预留卡片内部右上角编辑/删除按钮空间
                int maxTextPixelWidth = maxCardWidth - padX * 2;
                string wrapped = Game1.parseText(entry.Content ?? string.Empty, Game1.dialogueFont, (int)(maxTextPixelWidth / TextFontScale));
                Vector2 textSize = Game1.dialogueFont.MeasureString(wrapped) * TextFontScale;
                Vector2 dateSize = Game1.smallFont.MeasureString(dateLabel);

                float headerWidthNeeded = dateSize.X + 80; // 日期 + 按钮空间
                float contentInnerWidth = Math.Max(textSize.X, headerWidthNeeded);
                int cardWidth = (int)Math.Clamp(contentInnerWidth + padX * 2, minCardWidth, maxCardWidth);
                int cardHeight = (int)(Math.Max(dateSize.Y, 28) + 6 + textSize.Y + padY * 2);

                _measuredEntries.Add(new MeasuredEntry
                {
                    Index = i,
                    Id = entry.Id,
                    DateLabel = dateLabel,
                    WrappedText = wrapped,
                    Width = cardWidth,
                    Height = cardHeight,
                    InnerPadding = new Point(padX, padY)
                });
            }
        }
    }

    /// <summary>
    /// 根据当前起始索引计算屏幕实际可见区域内的项布局矩形
    /// </summary>
    private void RebuildVisibleLayout()
    {
        _visibleLayouts.Clear();
        int visibleAreaHeight = yPositionOnScreen + height - BottomBarHeight - 16 - _contentTopY;
        int currentY = _contentTopY;

        for (int i = _startIndex; i < _measuredEntries.Count; i++)
        {
            var m = _measuredEntries[i];
            if (currentY + m.Height > _contentTopY + visibleAreaHeight && _visibleLayouts.Count > 0)
                break; // 空间不足以容纳下一项完整内容时截断渲染，保持视觉美观且不遮挡底栏

            var layout = new VisibleEntryLayout
            {
                Measured = m,
                BoxRect = Rectangle.Empty,
                CheckboxRect = Rectangle.Empty,
                EditRect = Rectangle.Empty,
                DeleteRect = Rectangle.Empty
            };

            if (_currentTab == 0)
            {
                // Tab 0 对话气泡水平定位
                int bubbleX;
                if (m.SpeakerType == SpeakerType.Player)
                    bubbleX = xPositionOnScreen + width - RightPadding - m.Width; // 农夫靠右
                else if (m.SpeakerType == SpeakerType.System)
                    bubbleX = xPositionOnScreen + (width - m.Width) / 2;          // 场景居中
                else
                    bubbleX = xPositionOnScreen + LeftPadding;                   // NPC 靠左

                layout.BoxRect = new Rectangle(bubbleX, currentY, m.Width, m.Height);
            }
            else
            {
                // Tab 1-3 记忆卡片水平定位
                bool hasCheckbox = _currentTab == 1 || _currentTab == 2;
                int startX = xPositionOnScreen + LeftPadding;

                if (hasCheckbox)
                {
                    int cbSize = (int)(CheckboxSize * CheckboxScale);
                    layout.CheckboxRect = new Rectangle(startX, currentY + (m.Height - cbSize) / 2, cbSize, cbSize);
                    startX += 44;
                }

                layout.BoxRect = new Rectangle(startX, currentY, m.Width, m.Height);

                // 编辑与删除按钮定位于卡片内右上角
                int btnSize = 28;
                int btnY = currentY + m.InnerPadding.Y - 2;
                layout.DeleteRect = new Rectangle(startX + m.Width - m.InnerPadding.X - btnSize, btnY, btnSize, btnSize);
                layout.EditRect = new Rectangle(layout.DeleteRect.X - btnSize - 6, btnY, btnSize, btnSize);
            }

            _visibleLayouts.Add(layout);
            currentY += m.Height + ItemSpacing;
        }
    }

    private int CalculateMaxStartIndex()
    {
        if (_measuredEntries.Count == 0) return 0;
        int visibleAreaHeight = yPositionOnScreen + height - BottomBarHeight - 16 - _contentTopY;
        int accHeight = 0;
        int maxStart = 0;

        for (int i = _measuredEntries.Count - 1; i >= 0; i--)
        {
            accHeight += _measuredEntries[i].Height + ItemSpacing;
            if (accHeight > visibleAreaHeight)
            {
                maxStart = Math.Min(i + 1, _measuredEntries.Count - 1);
                break;
            }
        }
        return maxStart;
    }

    private void PageLeft()
    {
        if (_daysAgo >= MaxHistoryDays - 1)
        {
            Game1.playSound("cancel");
            return;
        }
        _daysAgo++;
        RefreshPagedChat();
        Game1.playSound("smallSelect");
    }

    private void PageRight()
    {
        if (_daysAgo <= 0)
        {
            Game1.playSound("cancel");
            return;
        }
        _daysAgo--;
        RefreshPagedChat();
        Game1.playSound("smallSelect");
    }

    // ──────────────────────────────────────────────────────────────
    // 布局与尺寸
    // ──────────────────────────────────────────────────────────────

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
                Game1.mouseCursors, new Rectangle(337, 494, 12, 12), 3.5f);
            _closeButton.hoverText = I18n.Memory.CloseButton();
        }
        else
        {
            _closeButton.bounds = new Rectangle(xPositionOnScreen + width - 56, yPositionOnScreen + 16, 44, 44);
        }

        _tabBarY = yPositionOnScreen + 60;
        _contentTopY = _tabBarY + TabBarHeight + 28;

        // 两侧固定翻页箭头
        int btnY = yPositionOnScreen + height - BottomBarHeight - 12;
        _leftArrowRect = new Rectangle(xPositionOnScreen + LeftPadding, btnY, 44, BottomBarHeight);
        _rightArrowRect = new Rectangle(xPositionOnScreen + width - RightPadding - 44, btnY, 44, BottomBarHeight);

        UpdateActionButtonLayout();
    }

    /// <summary>
    /// 居中动作按钮：计算多语言宽度并水平绝对居中
    /// </summary>
    private void UpdateActionButtonLayout()
    {
        if (_currentTab < 0 || _currentTab > 2)
        {
            _actionButtonRect = Rectangle.Empty;
            return;
        }

        string label = GetActionButtonLabel();
        int textWidth = (int)Game1.smallFont.MeasureString(label).X;
        int btnWidth = Math.Max(220, textWidth + 56);
        int btnY = yPositionOnScreen + height - BottomBarHeight - 12;
        int btnX = xPositionOnScreen + (width - btnWidth) / 2;

        _actionButtonRect = new Rectangle(btnX, btnY, btnWidth, BottomBarHeight);
    }

    private string GetActionButtonLabel() => _currentTab switch
    {
        0 => I18n.IsChinese ? "总结当前页" : "Summarize Page",
        1 => I18n.IsChinese ? "浓缩为每周" : "Condense to Weekly",
        2 => I18n.IsChinese ? "浓缩为编年" : "Condense to Chronicle",
        _ => string.Empty
    };

    public override void gameWindowSizeChanged(Rectangle oldBounds, Rectangle newBounds)
    {
        base.gameWindowSizeChanged(oldBounds, newBounds);
        UpdateLayout();
        RefreshEntries();
    }

    // ──────────────────────────────────────────────────────────────
    // 交互处理
    // ──────────────────────────────────────────────────────────────

    public override void receiveLeftClick(int x, int y, bool playSound = true)
    {
        if (_closeButton.containsPoint(x, y))
        {
            Close();
            return;
        }

        // Tab 切换
        int tabWidth = (width - LeftPadding - RightPadding) / 4;
        for (int t = 0; t < 4; t++)
        {
            Rectangle tabRect = new Rectangle(xPositionOnScreen + LeftPadding + t * tabWidth + 4, _tabBarY, tabWidth - 8, TabBarHeight);
            if (tabRect.Contains(x, y))
            {
                if (_currentTab != t)
                {
                    _currentTab = t;
                    Game1.playSound("smallSelect");
                    RefreshEntries();
                }
                return;
            }
        }

        // 翻页箭头（仅 Tab 0）
        if (_currentTab == 0)
        {
            if (_leftArrowRect.Contains(x, y) && _daysAgo < MaxHistoryDays - 1) { PageLeft(); return; }
            if (_rightArrowRect.Contains(x, y) && _daysAgo > 0) { PageRight(); return; }
        }

        // 居中动作按钮
        if (_currentTab >= 0 && _currentTab <= 2 && _actionButtonRect.Contains(x, y))
        {
            HandleActionButton();
            return;
        }

        // 点击记忆卡片、勾选框或行级按钮（Tab 1/2/3）
        if (_currentTab >= 1 && _currentTab <= 3)
        {
            foreach (var item in _visibleLayouts)
            {
                var entry = _tierEntries.FirstOrDefault(e => e.Id == item.Measured.Id);
                if (entry == null) continue;

                // 删除
                if (item.DeleteRect.Contains(x, y))
                {
                    ConfirmDelete(entry);
                    return;
                }

                // 编辑
                if (item.EditRect.Contains(x, y))
                {
                    Game1.activeClickableMenu = new AddMemoryInputMenu(
                        _npcName, this, entry, 0,
                        customSubmit: text => MemoryManager.Instance.EditTimelineMemory(_npcName, entry.Id, text));
                    return;
                }

                // 勾选（仅 Tab 1/2）
                if (_currentTab == 1 || _currentTab == 2)
                {
                    if (item.CheckboxRect.Contains(x, y) || item.BoxRect.Contains(x, y))
                    {
                        ToggleSelection(entry.Id);
                        return;
                    }
                }
            }
        }
    }

    private void ToggleSelection(string id)
    {
        if (_selectedEntryIds.Contains(id))
            _selectedEntryIds.Remove(id);
        else
            _selectedEntryIds.Add(id);
        Game1.playSound("smallSelect");
    }

    private void ConfirmDelete(MemoryEntry entry)
    {
        Game1.activeClickableMenu = new ConfirmationDialog(
            I18n.Memory.DeleteConfirm(entry.Content),
            _ =>
            {
                MemoryManager.Instance.RemoveTimelineMemory(_npcName, entry.Id);
                Game1.playSound("trashcan");
                RefreshEntries();
                Game1.activeClickableMenu = this;
            },
            _ =>
            {
                Game1.activeClickableMenu = this;
            });
    }

    private void HandleActionButton()
    {
        if (DialogueBuilder.Instance?.LlmDisabled == true)
        {
            Game1.playSound("cancel");
            Game1.addHUDMessage(new HUDMessage(I18n.Memory.DistillLlmDisabled(), 3));
            return;
        }

        if (_currentTab == 0)
        {
            long now = Environment.TickCount64;
            long elapsedMs = now - _lastSummarizeTickMs;
            if (elapsedMs < CooldownSeconds * 1000L)
            {
                int remaining = (int)((CooldownSeconds * 1000L - elapsedMs) / 1000L);
                Game1.playSound("cancel");
                Game1.addHUDMessage(new HUDMessage(
                    I18n.IsChinese ? $"总结冷却中，请 {remaining} 秒后再试" : $"Summarize cooldown: {remaining}s", 3));
                return;
            }

            if (_todayChatEntries.Count == 0)
            {
                Game1.playSound("cancel");
                Game1.addHUDMessage(new HUDMessage(I18n.Memory.DistillNoHistory(_npcDisplayName), 0));
                return;
            }

            _lastSummarizeTickMs = now;
            Game1.playSound("bigSelect");
            Game1.activeClickableMenu = new MemoryDistillMenu(_npcName, this, MemoryTier.Daily, dateFilter: ViewDate, timelineMode: true);
        }
        else if (_currentTab == 1 || _currentTab == 2)
        {
            if (_selectedEntryIds.Count < MinCondenseSelection)
            {
                Game1.playSound("cancel");
                Game1.addHUDMessage(new HUDMessage(
                    I18n.IsChinese ? "请至少勾选 2 条记忆进行浓缩" : "Select at least 2 entries to condense", 3));
                return;
            }

            var selectedEntries = _tierEntries.Where(e => _selectedEntryIds.Contains(e.Id)).ToList();
            MemoryTier targetTier = _currentTab == 1 ? MemoryTier.Weekly : MemoryTier.Chronicle;
            var condenseTask = MemoryExtractService.CondenseAsync(
                _npcName, _npcDisplayName,
                selectedEntries.Select(e => e.Content).ToList(),
                targetTier,
                CancellationToken.None);

            Game1.playSound("bigSelect");
            Game1.activeClickableMenu = new MemoryDistillMenu(_npcName, this, targetTier, selectedEntries, condenseTask, timelineMode: true);
        }
    }

    public override void receiveScrollWheelAction(int direction)
    {
        int maxStart = CalculateMaxStartIndex();
        if (maxStart <= 0) return;

        int newIndex = direction > 0
            ? Math.Max(0, _startIndex - 1)
            : Math.Min(maxStart, _startIndex + 1);

        if (newIndex != _startIndex)
        {
            _startIndex = newIndex;
            Game1.playSound("shwip");
            RebuildVisibleLayout();
        }
    }

    public override void receiveKeyPress(Keys key)
    {
        if (key == Keys.Escape)
        {
            Close();
            return;
        }

        base.receiveKeyPress(key);
    }

    // ──────────────────────────────────────────────────────────────
    // 渲染绘制
    // ──────────────────────────────────────────────────────────────

    public override void draw(SpriteBatch b)
    {
        int mx = Game1.getMouseX();
        int my = Game1.getMouseY();
        _hoveredTooltip = string.Empty;
        StardewTime viewDate = ViewDate;

        // 1. 底层背景与遮罩
        _returnMenu?.draw(b);
        b.Draw(Game1.fadeToBlackRect, Game1.graphics.GraphicsDevice.Viewport.Bounds, Color.Black * 0.4f);

        // 2. 双重外框与主对话底框
        IClickableMenu.drawTextureBox(b, xPositionOnScreen - 16, yPositionOnScreen - 16, width + 32, height + 32, Color.White);
        IClickableMenu.drawTextureBox(b, xPositionOnScreen - 8, yPositionOnScreen - 8, width + 16, height + 16, Color.White);
        Game1.drawDialogueBox(xPositionOnScreen, yPositionOnScreen, width, height, false, true);

        // 3. 标题（使用通用连字符 '-' 避免英文环境在中点 '·' 缺失抛出异常）
        string title = I18n.IsChinese ? $"时间线手账 - {_npcDisplayName}" : $"Timeline Chronicle - {_npcDisplayName}";
        Vector2 titleSize = Game1.dialogueFont.MeasureString(title);
        b.DrawString(Game1.dialogueFont, title,
            new Vector2(xPositionOnScreen + (width - titleSize.X) / 2f, yPositionOnScreen + 22),
            Game1.textColor);

        // 4. Tab 栏
        DrawTabBar(b, mx, my);

        // 5. 正文内容
        if (_currentTab == 0)
            DrawTodayChat(b, mx, my, viewDate);
        else
            DrawTierMemories(b, mx, my);

        // 6. 底栏操作区
        DrawBottomBar(b, mx, my);

        // 7. 滚动条
        DrawScrollbar(b);

        // 8. 关闭按钮
        UiHelper.UpdateButtonScale(ref _closeButtonHoverScale, _closeButton, mx, my);
        _closeButton.scale = 3.5f * _closeButtonHoverScale;
        _closeButton.draw(b);

        // 9. 浮动提示
        if (!string.IsNullOrEmpty(_hoveredTooltip))
            IClickableMenu.drawHoverText(b, _hoveredTooltip, Game1.smallFont);

        drawMouse(b);
    }

    private void DrawTabBar(SpriteBatch b, int mx, int my)
    {
        int tabWidth = (width - LeftPadding - RightPadding) / 4;
        string[] labels =
        {
            I18n.IsChinese ? "今日对话" : "Today",
            I18n.IsChinese ? "每日" : "Daily",
            I18n.IsChinese ? "每周" : "Weekly",
            I18n.IsChinese ? "编年" : "Chronicle"
        };

        for (int t = 0; t < 4; t++)
        {
            Rectangle rect = new Rectangle(xPositionOnScreen + LeftPadding + t * tabWidth + 4, _tabBarY, tabWidth - 8, TabBarHeight);
            bool active = _currentTab == t;
            bool hover = rect.Contains(mx, my);

            Color bg = active ? new Color(210, 180, 140)
                : hover ? new Color(255, 235, 205)
                : new Color(139, 90, 43);

            IClickableMenu.drawTextureBox(b, Game1.mouseCursors,
                new Rectangle(432, 439, 9, 9),
                rect.X, rect.Y, rect.Width, rect.Height, bg, 4f, false);

            var labelSize = Game1.smallFont.MeasureString(labels[t]);
            b.DrawString(Game1.smallFont, labels[t],
                new Vector2(rect.X + (rect.Width - labelSize.X) / 2f, rect.Y + (rect.Height - labelSize.Y) / 2f),
                active ? Game1.textColor : (hover ? Color.Wheat : Color.White));
        }

        int lineY = _tabBarY + TabBarHeight + 4;
        b.Draw(Game1.staminaRect, new Rectangle(xPositionOnScreen + LeftPadding, lineY, width - LeftPadding - RightPadding, 2), Color.Gray * 0.4f);
    }

    private void DrawTodayChat(SpriteBatch b, int mx, int my, StardewTime viewDate)
    {
        // 顶部日期指示
        string dateText = _daysAgo == 0
            ? (I18n.IsChinese ? $"今天 - {MemoryManager.FormatGameDateLabel(viewDate)}" : $"Today - {MemoryManager.FormatGameDateLabel(viewDate)}")
            : MemoryManager.FormatGameDateLabel(viewDate);
        var dateSize = Game1.smallFont.MeasureString(dateText);
        b.DrawString(Game1.smallFont, dateText,
            new Vector2(xPositionOnScreen + (width - dateSize.X) / 2f, _contentTopY - 22),
            Color.Gray);

        if (_todayChatEntries.Count == 0)
        {
            string empty = I18n.IsChinese ? "这一天还没有和 TA 的对话记录" : "No conversations with them on this day";
            var size = Game1.dialogueFont.MeasureString(empty);
            b.DrawString(Game1.dialogueFont, empty,
                new Vector2(xPositionOnScreen + (width - size.X) / 2f, _contentTopY + 40),
                Color.Gray);
            return;
        }

        // 自适应对话气泡渲染
        foreach (var item in _visibleLayouts)
        {
            var m = item.Measured;
            Color boxBg = m.SpeakerType switch
            {
                SpeakerType.Player => new Color(230, 245, 235), // 农夫：柔和淡青
                SpeakerType.System => new Color(235, 235, 238), // 系统场景：浅石灰
                _ => new Color(255, 246, 232)                   // NPC：经典羊皮纸奶白
            };

            // 绘制气泡边框底盒
            IClickableMenu.drawTextureBox(b, Game1.mouseCursors,
                new Rectangle(432, 439, 9, 9),
                item.BoxRect.X, item.BoxRect.Y, item.BoxRect.Width, item.BoxRect.Height,
                boxBg, 3.5f, false);

            // 说话者角色标签
            Color speakerColor = m.SpeakerType switch
            {
                SpeakerType.Player => new Color(34, 110, 50),
                SpeakerType.System => Color.DimGray,
                _ => new Color(130, 65, 20)
            };

            Vector2 speakerPos = new Vector2(item.BoxRect.X + m.InnerPadding.X, item.BoxRect.Y + m.InnerPadding.Y);
            b.DrawString(Game1.smallFont, m.Speaker, speakerPos, speakerColor);

            // 完整无裁切正文
            Vector2 textPos = new Vector2(
                item.BoxRect.X + m.InnerPadding.X,
                speakerPos.Y + Game1.smallFont.LineSpacing - 2);

            Color textColor = m.SpeakerType == SpeakerType.System ? Color.DimGray : Game1.textColor;
            b.DrawString(Game1.dialogueFont, m.WrappedText, textPos, textColor, 0f, Vector2.Zero, TextFontScale, SpriteEffects.None, 0.88f);
        }
    }

    private void DrawTierMemories(SpriteBatch b, int mx, int my)
    {
        if (_tierEntries.Count == 0)
        {
            string empty = I18n.IsChinese ? "这一层还没有记忆" : "No memories at this tier yet";
            var size = Game1.dialogueFont.MeasureString(empty);
            b.DrawString(Game1.dialogueFont, empty,
                new Vector2(xPositionOnScreen + (width - size.X) / 2f, _contentTopY + 40),
                Color.Gray);
            return;
        }

        foreach (var item in _visibleLayouts)
        {
            var m = item.Measured;
            bool selected = _selectedEntryIds.Contains(m.Id);
            bool hovered = item.BoxRect.Contains(mx, my);

            // 1. 勾选框（Tab 1/2）
            if (_currentTab == 1 || _currentTab == 2)
            {
                Rectangle src = selected
                    ? new Rectangle(CheckboxCheckedSourceX, CheckboxSourceY, CheckboxSize, CheckboxSize)
                    : new Rectangle(CheckboxUncheckedSourceX, CheckboxSourceY, CheckboxSize, CheckboxSize);

                b.Draw(Game1.mouseCursors,
                    new Vector2(item.CheckboxRect.X, item.CheckboxRect.Y),
                    src, Color.White, 0f, Vector2.Zero, CheckboxScale, SpriteEffects.None, 0.86f);
            }

            // 2. 自适应记忆卡片底盒（选中带有暖金底色）
            Color cardColor = selected
                ? new Color(255, 235, 205)
                : (hovered ? new Color(255, 248, 230) : new Color(252, 244, 234));

            IClickableMenu.drawTextureBox(b, Game1.mouseCursors,
                new Rectangle(432, 439, 9, 9),
                item.BoxRect.X, item.BoxRect.Y, item.BoxRect.Width, item.BoxRect.Height,
                cardColor, 3.5f, false);

            // 3. 头部：日期标签
            Vector2 datePos = new Vector2(item.BoxRect.X + m.InnerPadding.X, item.BoxRect.Y + m.InnerPadding.Y);
            b.DrawString(Game1.smallFont, m.DateLabel, datePos, new Color(110, 80, 50));

            // 4. 头部右侧：编辑与删除按钮（原生贴图图标）
            DrawIconButton(b, item.EditRect, EditIconSource, mx, my, iconScale: 1.8f);
            DrawIconButton(b, item.DeleteRect, DeleteIconSource, mx, my, iconScale: 1.4f);

            if (item.EditRect.Contains(mx, my)) _hoveredTooltip = I18n.Memory.EditButtonHover();
            if (item.DeleteRect.Contains(mx, my)) _hoveredTooltip = I18n.Memory.DeleteButtonHover();

            // 5. 记忆完整正文（自适应多行折行，杜绝截断）
            Vector2 textPos = new Vector2(
                item.BoxRect.X + m.InnerPadding.X,
                datePos.Y + Math.Max(Game1.smallFont.LineSpacing, 26));

            b.DrawString(Game1.dialogueFont, m.WrappedText, textPos, Game1.textColor, 0f, Vector2.Zero, TextFontScale, SpriteEffects.None, 0.88f);
        }
    }

    private void DrawBottomBar(SpriteBatch b, int mx, int my)
    {
        // 1. 左右翻页箭头（Tab 0 独有）
        if (_currentTab == 0)
        {
            bool canPageLeft = _daysAgo < MaxHistoryDays - 1;
            bool canPageRight = _daysAgo > 0;

            DrawArrowButton(b, _leftArrowRect, isLeft: true, enabled: canPageLeft, mx, my);
            DrawArrowButton(b, _rightArrowRect, isLeft: false, enabled: canPageRight, mx, my);
        }

        // 2. 居中动作按钮（Tab 0/1/2）
        if (_currentTab >= 0 && _currentTab <= 2)
        {
            string label = GetActionButtonLabel();
            bool hover = _actionButtonRect.Contains(mx, my);
            Color bg = hover ? new Color(255, 235, 205) : new Color(139, 90, 43);

            IClickableMenu.drawTextureBox(b, Game1.mouseCursors,
                new Rectangle(432, 439, 9, 9),
                _actionButtonRect.X, _actionButtonRect.Y, _actionButtonRect.Width, _actionButtonRect.Height,
                bg, 4f, false);

            var labelSize = Game1.smallFont.MeasureString(label);
            Vector2 textPos = new Vector2(
                _actionButtonRect.X + (_actionButtonRect.Width - labelSize.X) / 2f,
                _actionButtonRect.Y + (_actionButtonRect.Height - labelSize.Y) / 2f);

            b.DrawString(Game1.smallFont, label, textPos, hover ? Game1.textColor : Color.White);
        }
    }

    private static void DrawArrowButton(SpriteBatch b, Rectangle rect, bool isLeft, bool enabled, int mx, int my)
    {
        bool hover = enabled && rect.Contains(mx, my);
        Color bg = enabled
            ? (hover ? new Color(255, 235, 205) : new Color(139, 90, 43))
            : Color.Gray * 0.5f;

        IClickableMenu.drawTextureBox(b, Game1.mouseCursors,
            new Rectangle(432, 439, 9, 9),
            rect.X, rect.Y, rect.Width, rect.Height, bg, 4f, false);

        Rectangle src = isLeft ? LeftArrowSource : RightArrowSource;
        float arrowScale = 2.4f;
        int iconW = (int)(src.Width * arrowScale);
        int iconH = (int)(src.Height * arrowScale);
        Vector2 iconPos = new Vector2(
            rect.X + (rect.Width - iconW) / 2f,
            rect.Y + (rect.Height - iconH) / 2f);

        Color tint = enabled ? (hover ? Color.White : Color.Wheat) : Color.Gray * 0.5f;
        b.Draw(Game1.mouseCursors, iconPos, src, tint, 0f, Vector2.Zero, arrowScale, SpriteEffects.None, 0.86f);
    }

    private static void DrawIconButton(SpriteBatch b, Rectangle rect, Rectangle srcRect, int mx, int my, float iconScale)
    {
        bool hover = rect.Contains(mx, my);
        Color boxColor = hover ? new Color(255, 235, 205) : Color.White;

        IClickableMenu.drawTextureBox(b, Game1.mouseCursors,
            new Rectangle(432, 439, 9, 9),
            rect.X, rect.Y, rect.Width, rect.Height, boxColor, 2f, false);

        int iconW = (int)(srcRect.Width * iconScale);
        int iconH = (int)(srcRect.Height * iconScale);
        Vector2 iconPos = new Vector2(
            rect.X + (rect.Width - iconW) / 2f,
            rect.Y + (rect.Height - iconH) / 2f);

        Color tint = hover ? Color.White : new Color(220, 220, 220);
        b.Draw(Game1.mouseCursors, iconPos, srcRect, tint, 0f, Vector2.Zero, iconScale, SpriteEffects.None, 0.86f);
    }

    private void DrawScrollbar(SpriteBatch b)
    {
        int maxStart = CalculateMaxStartIndex();
        if (maxStart <= 0) return;

        int trackX = xPositionOnScreen + width - RightPadding + 12;
        int trackTop = _contentTopY;
        int trackHeight = yPositionOnScreen + height - 16 - BottomBarHeight - trackTop;
        if (trackHeight <= 0) return;

        // 轨道底槽
        b.Draw(Game1.staminaRect, new Rectangle(trackX, trackTop, 6, trackHeight), Color.Black * 0.22f);

        // 动态滑块
        float visibleRatio = (float)_visibleLayouts.Count / _measuredEntries.Count;
        int thumbHeight = Math.Max(28, (int)(trackHeight * visibleRatio));
        int thumbY = trackTop + (int)((trackHeight - thumbHeight) * ((float)_startIndex / maxStart));
        b.Draw(Game1.staminaRect, new Rectangle(trackX, thumbY, 6, thumbHeight), Color.Wheat * 0.85f);
    }

    // ──────────────────────────────────────────────────────────────
    // 辅助数据结构
    // ──────────────────────────────────────────────────────────────

    private class MeasuredEntry
    {
        public int Index;
        public string Id;
        public string Speaker;
        public SpeakerType SpeakerType;
        public string DateLabel;
        public string WrappedText;
        public int Width;
        public int Height;
        public Point InnerPadding;
    }

    private class VisibleEntryLayout
    {
        public MeasuredEntry Measured;
        public Rectangle BoxRect;
        public Rectangle CheckboxRect;
        public Rectangle EditRect;
        public Rectangle DeleteRect;
    }

    // ──────────────────────────────────────────────────────────────
    // 关闭与清理
    // ──────────────────────────────────────────────────────────────

    private void Close()
    {
        Game1.playSound("bigDeSelect");
        if (_returnMenu is IMemoryRefreshTarget refreshable)
            refreshable.RefreshEntries();

        if (_ownerMenu == null)
            exitThisMenu();
        else if (Game1.activeClickableMenu == this)
            Game1.activeClickableMenu = _ownerMenu;
    }

    protected override void cleanupBeforeExit()
    {
        base.cleanupBeforeExit();

        if (_ownerMenu != null && Game1.activeClickableMenu == this)
            Game1.activeClickableMenu = _ownerMenu;
    }
}