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
/// 时间线手账面板（FEAT-MEM-300-T4）：Tab0 按日翻页的对话总结入口，Tab1-3 分层记忆（Daily/Weekly/Chronicle）勾选浓缩。
/// 无手动录入通道：所有入库只能经 MemoryDistillMenu 审核确认（T3）。
/// </summary>
internal class TimelineChronicleMenu : IClickableMenu, IMemoryRefreshTarget
{
    private const int RowH = 48;
    private const int TabBarHeight = 44;
    private const int BottomBarHeight = 46;
    private const int LeftPadding = 48;
    private const int RightPadding = 48;
    private const int ContentStartXOffset = 150; // 正文起点（dateLabel 列右侧）
    private const int CheckboxSourceX = 227;
    private const int CheckboxSourceY = 236;
    private const int CheckboxSize = 9;
    private const float CheckboxScale = 3f;

    private const int MaxHistoryDays = 7;
    private const int CooldownSeconds = 30;
    private const int MinCondenseSelection = 2;

    // 总结冷却（进程内，Memory scope）
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
    private readonly List<Rectangle> _checkboxRects = new();
    private int _startIndex;

    private int _contentTopY;
    private int _tabBarY;
    private Rectangle _leftArrowRect;
    private Rectangle _rightArrowRect;
    private Rectangle _leftActionButtonRect;
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
    // 刷新与分页
    // ──────────────────────────────────────────────────────────────

    public void RefreshEntries()
    {
        _selectedEntryIds.Clear();
        _startIndex = 0;

        if (_currentTab == 0)
            RefreshPagedChat();
        else
            _tierEntries = MemoryManager.Instance.GetTimelineMemories(_npcName, TierForTab(_currentTab));

        RebuildActionButtons();
        SetScrollbarPosition();
    }

    private void RefreshPagedChat()
    {
        StardewTime view = ViewDate;
        var all = DialogueHistoryManager.Instance.GetHistory(_npcName);
        // 逐帧 LINQ 在历史规模（≤300）下可接受，但缓存 ViewDate 避免重复构造
        _todayChatEntries.Clear();
        foreach (var e in all)
        {
            if (e.DialogueType == "eavesdrop") continue;
            if (string.IsNullOrWhiteSpace(e.Text)) continue;
            if (e.Timestamp.Year != view.Year || e.Timestamp.Season != view.Season || e.Timestamp.DayOfMonth != view.DayOfMonth) continue;
            _todayChatEntries.Add(e);
        }
        _startIndex = 0;
        RebuildActionButtons();
        SetScrollbarPosition();
        // 注意：契约 3——不清 _selectedEntryIds（翻页保留勾选）
    }

    private static MemoryTier TierForTab(int tab) => tab switch
    {
        1 => MemoryTier.Daily,
        2 => MemoryTier.Weekly,
        _ => MemoryTier.Chronicle
    };

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
    // 布局
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

        // 底栏
        int btnY = yPositionOnScreen + height - BottomBarHeight - 8;
        _leftArrowRect = new Rectangle(xPositionOnScreen + LeftPadding, btnY, 44, BottomBarHeight);
        _rightArrowRect = new Rectangle(xPositionOnScreen + width - RightPadding - 44, btnY, 44, BottomBarHeight);
        _leftActionButtonRect = new Rectangle(xPositionOnScreen + LeftPadding + 56, btnY, 200, BottomBarHeight);
    }

    public override void gameWindowSizeChanged(Rectangle oldBounds, Rectangle newBounds)
    {
        base.gameWindowSizeChanged(oldBounds, newBounds);
        UpdateLayout();
        RefreshEntries();
    }

    private void RebuildActionButtons()
    {
        _checkboxRects.Clear();
        int visible = GetVisibleCount();
        for (int i = 0; i < visible && _currentTab >= 1 && _currentTab <= 2; i++)
        {
            int rowY = _contentTopY + i * RowH;
            _checkboxRects.Add(new Rectangle(
                _contentStartX() - CheckboxSize * 4 - 8,
                rowY + (RowH - (int)(CheckboxSize * CheckboxScale)) / 2,
                (int)(CheckboxSize * CheckboxScale),
                (int)(CheckboxSize * CheckboxScale)));
        }
    }

    private int GetVisibleCount()
    {
        int contentHeight = yPositionOnScreen + height - 8 - BottomBarHeight - _contentTopY;
        return Math.Max(4, contentHeight / RowH);
    }

    private void SetScrollbarPosition()
    {
        // 滚动条几何随可视区计算（简化：绘制时直接使用）
    }

    private int _contentStartX() => xPositionOnScreen + LeftPadding + ContentStartXOffset;

    // ──────────────────────────────────────────────────────────────
    // 交互
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
            if (_leftArrowRect.Contains(x, y)) { PageLeft(); return; }
            if (_rightArrowRect.Contains(x, y)) { PageRight(); return; }
        }

        // 左动作按钮（Tab 0/1/2 绘制并响应；Tab 3 不绘制不响应）
        if (_currentTab >= 0 && _currentTab <= 2 && _leftActionButtonRect.Contains(x, y))
        {
            HandleLeftActionButton();
            return;
        }

        // 勾选（Tab 1/2）
        if (_currentTab == 1 || _currentTab == 2)
        {
            for (int i = 0; i < _checkboxRects.Count; i++)
            {
                int idx = _startIndex + i;
                if (idx >= _tierEntries.Count) break;
                if (_checkboxRects[i].Contains(x, y))
                {
                    ToggleSelection(_tierEntries[idx].Id);
                    return;
                }
            }

            // 行级操作区域命中（编辑/删除在 DrawTierMemories 中几何一致）
            int visible = GetVisibleCount();
            for (int i = 0; i < visible; i++)
            {
                int idx = _startIndex + i;
                if (idx >= _tierEntries.Count) break;
                int rowY = _contentTopY + i * RowH;
                Rectangle rowRect = new Rectangle(xPositionOnScreen + LeftPadding, rowY, width - LeftPadding - RightPadding, RowH);

                // 删除按钮
                Rectangle delRect = new Rectangle(xPositionOnScreen + width - RightPadding - 40, rowY + (RowH - 32) / 2, 32, 32);
                if (delRect.Contains(x, y))
                {
                    ConfirmDelete(_tierEntries[idx]);
                    return;
                }

                // 编辑按钮
                Rectangle editRect = new Rectangle(xPositionOnScreen + width - RightPadding - 80, rowY + (RowH - 32) / 2, 32, 32);
                if (editRect.Contains(x, y))
                {
                    Game1.activeClickableMenu = new AddMemoryInputMenu(
                        _npcName, this, _tierEntries[idx], 0,
                        customSubmit: text => MemoryManager.Instance.EditTimelineMemory(_npcName, _tierEntries[idx].Id, text));
                    return;
                }

                if (rowRect.Contains(x, y))
                {
                    ToggleSelection(_tierEntries[idx].Id);
                    return;
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

    private void HandleLeftActionButton()
    {
        // LLM 禁用守卫
        if (DialogueBuilder.Instance?.LlmDisabled == true)
        {
            Game1.playSound("cancel");
            Game1.addHUDMessage(new HUDMessage(I18n.Memory.DistillLlmDisabled(), 3));
            return;
        }

        if (_currentTab == 0)
        {
            // 30s 冷却守卫（仅在确将打开蒸馏菜单时消耗）
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

            // 当前页无对话
            if (_todayChatEntries.Count == 0)
            {
                Game1.playSound("cancel");
                Game1.addHUDMessage(new HUDMessage(I18n.Memory.DistillNoHistory(_npcDisplayName), 0));
                return;
            }

            _lastSummarizeTickMs = now;
            Game1.playSound("bigSelect");
            Game1.activeClickableMenu = new MemoryDistillMenu(_npcName, this, MemoryTier.Daily, dateFilter: ViewDate);
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
            Game1.activeClickableMenu = new MemoryDistillMenu(_npcName, this, targetTier, selectedEntries, condenseTask);
        }
        // Tab 3：Chronicle 不做升档——方法直接返回
    }

    public override void receiveScrollWheelAction(int direction)
    {
        int visible = GetVisibleCount();
        int total = _currentTab == 0 ? _todayChatEntries.Count : _tierEntries.Count;
        int maxStart = Math.Max(0, total - visible);
        if (maxStart <= 0) return;

        int newIndex = direction > 0
            ? Math.Max(0, _startIndex - 1)
            : Math.Min(maxStart, _startIndex + 1);

        if (newIndex != _startIndex)
        {
            _startIndex = newIndex;
            Game1.playSound("shwip");
            RebuildActionButtons();
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
    // 渲染
    // ──────────────────────────────────────────────────────────────

    public override void draw(SpriteBatch b)
    {
        int mx = Game1.getMouseX();
        int my = Game1.getMouseY();
        _hoveredTooltip = string.Empty;
        StardewTime viewDate = ViewDate; // 契约 10：draw 内至多求值一次

        // 1. 底层主菜单 + 遮罩
        _returnMenu?.draw(b);
        b.Draw(Game1.fadeToBlackRect, Game1.graphics.GraphicsDevice.Viewport.Bounds, Color.Black * 0.4f);

        // 2. 双木框 + 对话框
        IClickableMenu.drawTextureBox(b, xPositionOnScreen - 16, yPositionOnScreen - 16, width + 32, height + 32, Color.White);
        IClickableMenu.drawTextureBox(b, xPositionOnScreen - 8, yPositionOnScreen - 8, width + 16, height + 16, Color.White);
        Game1.drawDialogueBox(xPositionOnScreen, yPositionOnScreen, width, height, false, true);

        // 3. 标题
        string title = I18n.IsChinese ? $"时间线手账 · {_npcDisplayName}" : $"Timeline Chronicle · {_npcDisplayName}";
        Vector2 titleSize = Game1.dialogueFont.MeasureString(title);
        b.DrawString(Game1.dialogueFont, title,
            new Vector2(xPositionOnScreen + (width - titleSize.X) / 2f, yPositionOnScreen + 22),
            Game1.textColor);

        // 4. Tab 栏
        DrawTabBar(b, mx, my);

        // 5. 内容
        if (_currentTab == 0)
            DrawTodayChat(b, mx, my, viewDate);
        else
            DrawTierMemories(b, mx, my);

        // 6. 底栏
        DrawBottomBar(b, mx, my);

        // 7. 滚动条（绘制层：仅内容超出时）
        DrawScrollbar(b);

        // 8. 关闭钮
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
            Color bg = active ? new Color(210, 180, 140)
                : rect.Contains(mx, my) ? new Color(255, 235, 205)
                : new Color(139, 90, 43);

            IClickableMenu.drawTextureBox(b, Game1.mouseCursors,
                new Rectangle(432, 439, 9, 9),
                rect.X, rect.Y, rect.Width, rect.Height, bg, 4f, false);

            var labelSize = Game1.smallFont.MeasureString(labels[t]);
            b.DrawString(Game1.smallFont, labels[t],
                new Vector2(rect.X + (rect.Width - labelSize.X) / 2f, rect.Y + (rect.Height - labelSize.Y) / 2f),
                active ? Game1.textColor : Color.White);
        }

        // 分割线（栏下）
        int lineY = _tabBarY + TabBarHeight + 4;
        b.Draw(Game1.staminaRect, new Rectangle(xPositionOnScreen + LeftPadding, lineY, width - LeftPadding - RightPadding, 2), Color.Gray * 0.4f);
    }

    private void DrawBottomBar(SpriteBatch b, int mx, int my)
    {
        // 翻页箭头（仅 Tab 0）
        if (_currentTab == 0)
        {
            DrawArrowButton(b, _leftArrowRect, "◀", mx, my);
            DrawArrowButton(b, _rightArrowRect, "▶", mx, my);
        }

        // 左动作按钮（Tab 0/1/2；Tab 3 不绘制）
        if (_currentTab >= 0 && _currentTab <= 2)
        {
            string label = _currentTab switch
            {
                0 => I18n.IsChinese ? "✦ 总结当前页" : "✦ Summarize this page",
                1 => I18n.IsChinese ? "✦ 浓缩为每周" : "✦ Condense to Weekly",
                _ => I18n.IsChinese ? "✦ 浓缩为编年" : "✦ Condense to Chronicle"
            };

            bool hover = _leftActionButtonRect.Contains(mx, my);
            Color bg = hover ? new Color(255, 235, 205) : new Color(139, 90, 43);
            IClickableMenu.drawTextureBox(b, Game1.mouseCursors,
                new Rectangle(432, 439, 9, 9),
                _leftActionButtonRect.X, _leftActionButtonRect.Y, _leftActionButtonRect.Width, _leftActionButtonRect.Height,
                bg, 4f, false);

            var labelSize = Game1.smallFont.MeasureString(label);
            b.DrawString(Game1.smallFont, label,
                new Vector2(
                    _leftActionButtonRect.X + (_leftActionButtonRect.Width - labelSize.X) / 2f,
                    _leftActionButtonRect.Y + (_leftActionButtonRect.Height - labelSize.Y) / 2f),
                Color.White);
        }
    }

    private static void DrawArrowButton(SpriteBatch b, Rectangle rect, string label, int mx, int my)
    {
        bool hover = rect.Contains(mx, my);
        Color bg = hover ? new Color(255, 235, 205) : new Color(139, 90, 43);
        IClickableMenu.drawTextureBox(b, Game1.mouseCursors,
            new Rectangle(432, 439, 9, 9),
            rect.X, rect.Y, rect.Width, rect.Height, bg, 4f, false);

        var labelSize = Game1.smallFont.MeasureString(label);
        b.DrawString(Game1.smallFont, label,
            new Vector2(rect.X + (rect.Width - labelSize.X) / 2f, rect.Y + (rect.Height - labelSize.Y) / 2f),
            Color.White);
    }

    private void DrawScrollbar(SpriteBatch b)
    {
        int visible = GetVisibleCount();
        int total = _currentTab == 0 ? _todayChatEntries.Count : _tierEntries.Count;
        if (total <= visible) return;

        int trackX = xPositionOnScreen + width - RightPadding + 8;
        int trackTop = _contentTopY;
        int trackHeight = yPositionOnScreen + height - 8 - BottomBarHeight - trackTop;
        if (trackHeight <= 0) return;

        b.Draw(Game1.staminaRect, new Rectangle(trackX, trackTop, 6, trackHeight), Color.Gray * 0.25f);

        float ratio = (float)visible / total;
        int thumbHeight = Math.Max(24, (int)(trackHeight * ratio));
        int maxStart = total - visible;
        int thumbY = trackTop + (int)((trackHeight - thumbHeight) * ((float)_startIndex / maxStart));
        b.Draw(Game1.staminaRect, new Rectangle(trackX, thumbY, 6, thumbHeight), Color.Gray * 0.7f);
    }

    private void DrawTodayChat(SpriteBatch b, int mx, int my, StardewTime viewDate)
    {
        int visible = GetVisibleCount();
        int contentWidth = width - LeftPadding - RightPadding;

        // 页日期指示（列表首行上方居中）
        string dateText = _daysAgo == 0
            ? (I18n.IsChinese ? $"今天 · {MemoryManager.FormatGameDateLabel(viewDate)}" : $"Today · {MemoryManager.FormatGameDateLabel(viewDate)}")
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

        float maxTextWidth = contentWidth - 16;
        for (int i = 0; i < visible; i++)
        {
            int idx = _startIndex + i;
            if (idx >= _todayChatEntries.Count) break;

            var e = _todayChatEntries[idx];
            int rowY = _contentTopY + i * RowH;
            bool isSystem = e.SpeakerType == SpeakerType.System;

            string speaker = e.SpeakerType switch
            {
                SpeakerType.Player => I18n.IsChinese ? "农夫" : "Farmer",
                SpeakerType.System => I18n.IsChinese ? "（系统）" : "(System)",
                _ => _npcDisplayName
            };

            string prefix = isSystem
                ? (I18n.IsChinese ? "〔场景〕" : "[Scene] ")
                : $"{speaker}: ";

            string fullText = prefix + (e.Text ?? "");
            Color textColor = isSystem ? Color.Gray : Game1.textColor;
            string display = UiHelper.TruncateString(fullText, Game1.dialogueFont, maxTextWidth, 1f);

            Vector2 textPos = new Vector2(xPositionOnScreen + LeftPadding, rowY + (RowH - Game1.dialogueFont.LineSpacing) / 2f);
            b.DrawString(Game1.dialogueFont, display, textPos, textColor);

            // 悬停提示（截断时）
            Rectangle textBounds = new Rectangle((int)textPos.X, rowY, (int)maxTextWidth, RowH);
            if (textBounds.Contains(mx, my) && display != fullText)
                _hoveredTooltip = fullText;
        }
    }

    private void DrawTierMemories(SpriteBatch b, int mx, int my)
    {
        int visible = GetVisibleCount();
        int contentWidth = width - LeftPadding - RightPadding;
        float maxTextWidth = contentWidth - ContentStartXOffset - 96; // 预留 edit/delete 按钮

        for (int i = 0; i < visible; i++)
        {
            int idx = _startIndex + i;
            if (idx >= _tierEntries.Count) break;

            var entry = _tierEntries[idx];
            int rowY = _contentTopY + i * RowH;

            // ① Checkbox（Tab 1/2）
            if (_currentTab == 1 || _currentTab == 2)
            {
                Rectangle cbRect = _checkboxRects[i];
                bool selected = _selectedEntryIds.Contains(entry.Id);
                IClickableMenu.drawTextureBox(b, Game1.mouseCursors,
                    new Rectangle(CheckboxSourceX, CheckboxSourceY, CheckboxSize, CheckboxSize),
                    cbRect.X, cbRect.Y, cbRect.Width, cbRect.Height, Color.White, CheckboxScale, false);

                if (selected)
                {
                    // 选中标记：中心小方块
                    string check = "✓";
                    var checkSize = Game1.smallFont.MeasureString(check);
                    b.DrawString(Game1.smallFont, check,
                        new Vector2(cbRect.X + (cbRect.Width - checkSize.X) / 2f, cbRect.Y + (cbRect.Height - checkSize.Y) / 2f),
                        Game1.textColor);
                }
            }

            // ② dateLabel
            string dateLabel = string.IsNullOrEmpty(entry.DateLabel) ? "—" : entry.DateLabel;
            b.DrawString(Game1.smallFont, dateLabel,
                new Vector2(xPositionOnScreen + LeftPadding, rowY + (RowH - Game1.smallFont.LineSpacing) / 2f),
                Color.Gray);

            // ③ 正文
            string fullText = entry.Content ?? "";
            string display = UiHelper.TruncateString(fullText, Game1.dialogueFont, maxTextWidth, 0.82f);
            Vector2 textPos = new Vector2(_contentStartX(), rowY + (RowH - Game1.dialogueFont.LineSpacing) / 2f);
            b.DrawString(Game1.dialogueFont, display, textPos, Game1.textColor);

            if (mx >= textPos.X && mx <= textPos.X + maxTextWidth && my >= rowY && my < rowY + RowH && display != fullText)
                _hoveredTooltip = fullText;

            // ④ edit / delete 按钮
            Rectangle editRect = new Rectangle(xPositionOnScreen + width - RightPadding - 80, rowY + (RowH - 32) / 2, 32, 32);
            Rectangle delRect = new Rectangle(xPositionOnScreen + width - RightPadding - 40, rowY + (RowH - 32) / 2, 32, 32);
            DrawSmallButton(b, editRect, "✎", mx, my);
            DrawSmallButton(b, delRect, "✕", mx, my);

            if (editRect.Contains(mx, my)) _hoveredTooltip = I18n.Memory.EditButtonHover();
            if (delRect.Contains(mx, my)) _hoveredTooltip = I18n.Memory.DeleteButtonHover();
        }

        // 空态（Tab 1-3 列表为空）
        if (_tierEntries.Count == 0)
        {
            string empty = I18n.IsChinese ? "这一层还没有记忆" : "No memories at this tier yet";
            var size = Game1.dialogueFont.MeasureString(empty);
            b.DrawString(Game1.dialogueFont, empty,
                new Vector2(xPositionOnScreen + (width - size.X) / 2f, _contentTopY + 40),
                Color.Gray);
        }
    }

    private static void DrawSmallButton(SpriteBatch b, Rectangle rect, string glyph, int mx, int my)
    {
        IClickableMenu.drawTextureBox(b, Game1.mouseCursors,
            new Rectangle(432, 439, 9, 9),
            rect.X, rect.Y, rect.Width, rect.Height, Color.White, 2.5f, false);

        var size = Game1.smallFont.MeasureString(glyph);
        b.DrawString(Game1.smallFont, glyph,
            new Vector2(rect.X + (rect.Width - size.X) / 2f, rect.Y + (rect.Height - size.Y) / 2f),
            rect.Contains(mx, my) ? Color.Gold : Game1.textColor);
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
