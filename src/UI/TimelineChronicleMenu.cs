using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewValley;
using StardewValley.Menus;
using ValleytalkReborn.UI;

namespace ValleytalkReborn;

/// <summary>
/// 时间线手账面板（FEAT-MEM-300-T4）：
/// Tab0 按日翻页的对话记录查看（自适应气泡宽度、无截断多行展开、农夫/NPC 像素行走图头像对齐）；
/// Tab1-3 分层记忆（Daily/Weekly/Chronicle）自适应卡片勾选与浓缩；
/// 底栏支持动态上拉切换 NPC，并自动锁定最新聊天的 NPC。
/// </summary>
internal class TimelineChronicleMenu : IClickableMenu, IMemoryRefreshTarget
{
    private const int TabBarHeight = 44;
    private const int BottomBarHeight = 46;
    private const int LeftPadding = 48;
    private const int RightPadding = 48;
    private const int ItemSpacing = 10;
    private const float TextFontScale = 0.7f;

    // 头像排版常量（40x40 像素标准框）
    private const int AvatarSize = 40;
    private const int AvatarGap = 10;

    // 原版 Checkbox 贴图切片 (mouseCursors)
    private const int CheckboxUncheckedSourceX = 227;
    private const int CheckboxCheckedSourceX = 236;
    private const int CheckboxSourceY = 425;
    private const int CheckboxSize = 9;
    private const float CheckboxScale = 3.5f;

    // 原版图标贴图切片 (mouseCursors)
    private static readonly Rectangle LeftArrowSource = new(352, 495, 12, 11);
    private static readonly Rectangle RightArrowSource = new(365, 495, 12, 11);

    private const int MaxHistoryDays = 7;
    private const int CooldownSeconds = 30;
    private const int MinCondenseSelection = 2;

    private static long _lastSummarizeTickMs = 0;

    // 动态 NPC 状态
    private string _npcName;
    private string _npcDisplayName;
    private readonly IClickableMenu _returnMenu;
    private readonly IClickableMenu _ownerMenu;

    // NPC 行走图缓存（切换角色时更新）
    private Texture2D _currentNpcSprite;
    private Rectangle _currentNpcSourceRect;

    private int _currentTab; // 0=Chats, 1=Impressions, 2=Weekly, 3=Chronicle
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
    private Rectangle _actionButtonRect;

    // 底栏归档箱按钮（FEAT-AUTO-T6，独立于 Manual/Auto 归档箱）
    private Rectangle _archiveButtonRect;
    private int _archivedTimelineCount;

    // 底栏 NPC 上拉切换器
    private Rectangle _npcDropdownRect;
    private readonly DropupList _npcDropdown;

    private ClickableTextureComponent _closeButton;
    private float _closeButtonHoverScale = 1f;
    private string _hoveredTooltip = string.Empty;

    private StardewTime ViewDate => new StardewTime(Game1.Date, Game1.timeOfDay).AddDays(-_daysAgo);

    private bool CanPageLeft
    {
        get
        {
            if (_daysAgo >= MaxHistoryDays - 1) return false;
            StardewTime prevDate = new StardewTime(Game1.Date, Game1.timeOfDay).AddDays(-(_daysAgo + 1));
            return prevDate.Year >= 1;
        }
    }

    private bool CanPageRight => _daysAgo > 0;

    public TimelineChronicleMenu(string npcName = null, IClickableMenu returnMenu = null, IClickableMenu ownerMenu = null, bool autoLockLatest = true)
    {
        _returnMenu = returnMenu;
        _ownerMenu = ownerMenu ?? returnMenu;

        string latestNpc = GetMostRecentChattedNpc();
        if (autoLockLatest && !string.IsNullOrWhiteSpace(latestNpc))
            _npcName = latestNpc;
        else
            _npcName = !string.IsNullOrWhiteSpace(npcName) ? npcName : latestNpc;

        if (string.IsNullOrWhiteSpace(_npcName))
        {
            _npcName = Game1.player?.friendshipData != null
                ? Game1.player.friendshipData.Keys.FirstOrDefault() ?? ""
                : "";
        }

        _npcDisplayName = Game1.getCharacterFromName(_npcName)?.displayName ?? _npcName;

        _npcDropdown = new DropupList(Rectangle.Empty)
        {
            HeaderPrefix = I18n.Timeline.NpcDropdownPrefix(),
            OnItemSelected = name => SelectNpc(name)
        };

        UpdateLayout();
        BuildNpcDropdownItems();
        UpdateNpcSpriteCache();
        RefreshEntries();
    }

    // ──────────────────────────────────────────────────────────────
    // NPC 切换与行走图定位逻辑
    // ──────────────────────────────────────────────────────────────

    private void SelectNpc(string newNpcName)
    {
        if (string.Equals(_npcName, newNpcName, StringComparison.OrdinalIgnoreCase))
            return;

        _npcName = newNpcName;
        _npcDisplayName = Game1.getCharacterFromName(newNpcName)?.displayName ?? newNpcName;
        _daysAgo = 0;
        _startIndex = 0;
        _selectedEntryIds.Clear();

        Game1.playSound("bigSelect");
        _npcDropdown.SetSelectedId(_npcName);
        UpdateActionButtonLayout();
        UpdateNpcSpriteCache();
        RefreshEntries();
    }

    /// <summary>
    /// 加载并计算 NPC 行走图正脸头部切片（支持自动识别头顶留白，避免截断下巴）
    /// </summary>
    private void UpdateNpcSpriteCache()
    {
        _currentNpcSprite = GetOrLoadNpcSprite(_npcName);

        if (_currentNpcSprite != null)
        {
            NPC npc = Game1.getCharacterFromName(_npcName);

            int frameWidth = 16;
            if (npc?.Sprite != null && npc.Sprite.SpriteWidth > 0)
            {
                frameWidth = npc.Sprite.SpriteWidth;
            }
            else if (_currentNpcSprite.Width >= 64)
            {
                frameWidth = _currentNpcSprite.Width / 4;
            }
            else if (_currentNpcSprite.Width >= 32)
            {
                frameWidth = _currentNpcSprite.Width / 2;
            }
            else
            {
                frameWidth = _currentNpcSprite.Width;
            }

            int frameHeight = (npc?.Sprite != null && npc.Sprite.SpriteHeight > 0)
                ? npc.Sprite.SpriteHeight
                : Math.Min(_currentNpcSprite.Height, frameWidth * 2);

            int topY = FindSpriteTopY(_currentNpcSprite, frameWidth, frameHeight);
            int headHeight = Math.Min(frameWidth, _currentNpcSprite.Height - topY);
            _currentNpcSourceRect = new Rectangle(0, topY, frameWidth, headHeight);
        }
        else
        {
            _currentNpcSourceRect = Rectangle.Empty;
        }
    }

    private static int FindSpriteTopY(Texture2D texture, int frameWidth, int frameHeight)
    {
        try
        {
            int checkWidth = Math.Min(frameWidth, texture.Width);
            int checkHeight = Math.Min(frameHeight, texture.Height);
            Color[] pixels = new Color[checkWidth * checkHeight];
            texture.GetData(0, new Rectangle(0, 0, checkWidth, checkHeight), pixels, 0, pixels.Length);

            for (int y = 0; y < checkHeight; y++)
            {
                for (int x = 0; x < checkWidth; x++)
                {
                    if (pixels[y * checkWidth + x].A > 20)
                    {
                        return y;
                    }
                }
            }
        }
        catch { }
        return 0;
    }

    private static Texture2D GetOrLoadNpcSprite(string npcName)
    {
        if (string.IsNullOrWhiteSpace(npcName)) return null;

        NPC npc = Game1.getCharacterFromName(npcName);

        try
        {
            if (npc?.Sprite?.Texture != null)
                return npc.Sprite.Texture;
        }
        catch { }

        string assetName = npc?.getTextureName() ?? npcName;
        try
        {
            return Game1.content.Load<Texture2D>($"Characters\\{assetName}");
        }
        catch
        {
            return null;
        }
    }

    private static string GetMostRecentChattedNpc()
    {
        string bestNpc = null;
        long maxScore = -1;

        var candidateNpcs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (Game1.player?.friendshipData != null)
        {
            foreach (var k in Game1.player.friendshipData.Keys)
                candidateNpcs.Add(k);
        }

        string mgrRecent = DialogueHistoryManager.Instance.GetMostRecentNpc();
        if (!string.IsNullOrEmpty(mgrRecent))
            candidateNpcs.Add(mgrRecent);

        foreach (var name in candidateNpcs)
        {
            var history = DialogueHistoryManager.Instance.GetHistory(name);
            if (history == null || history.Count == 0) continue;

            var last = history[^1];
            long score = GetTimestampScore(last.Timestamp);
            if (score > maxScore)
            {
                maxScore = score;
                bestNpc = name;
            }
        }

        return bestNpc ?? mgrRecent ?? candidateNpcs.FirstOrDefault() ?? "";
    }

    private static long GetTimestampScore(StardewTime t)
    {
        return ((long)t.Year * 112L + (int)t.Season * 28L + t.DayOfMonth) * 10000L + t.TimeOfDay;
    }

    private void BuildNpcDropdownItems()
    {
        var items = new List<(string Id, string Label)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var candidateScores = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        if (Game1.player?.friendshipData != null)
        {
            foreach (var k in Game1.player.friendshipData.Keys)
                candidateScores[k] = -1;
        }

        string mgrRecent = DialogueHistoryManager.Instance.GetMostRecentNpc();
        if (!string.IsNullOrEmpty(mgrRecent) && !candidateScores.ContainsKey(mgrRecent))
            candidateScores[mgrRecent] = -1;

        if (!string.IsNullOrWhiteSpace(_npcName) && !candidateScores.ContainsKey(_npcName))
            candidateScores[_npcName] = -1;

        foreach (var k in candidateScores.Keys.ToList())
        {
            var history = DialogueHistoryManager.Instance.GetHistory(k);
            if (history != null && history.Count > 0)
                candidateScores[k] = GetTimestampScore(history[^1].Timestamp);
        }

        var sortedNpcs = candidateScores.Keys
            .OrderByDescending(k => candidateScores[k])
            .ThenBy(k => Game1.getCharacterFromName(k)?.displayName ?? k, StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var k in sortedNpcs)
        {
            if (seen.Contains(k)) continue;
            string label = Game1.getCharacterFromName(k)?.displayName ?? k;
            items.Add((k, label));
            seen.Add(k);
        }

        _npcDropdown.SetItems(items, _npcName);
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

        _archivedTimelineCount = MemoryManager.Instance.GetArchivedTimelineCount(_npcName);
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

    private void MeasureAllEntries()
    {
        _measuredEntries.Clear();
        int availableWidth = width - LeftPadding - RightPadding;

        if (_currentTab == 0)
        {
            int padX = 14;
            int padY = 10;
            int minBubbleWidth = 140;

            for (int i = 0; i < _todayChatEntries.Count; i++)
            {
                var entry = _todayChatEntries[i];
                string speaker = entry.SpeakerType switch
                {
                    SpeakerType.Player => I18n.Timeline.SpeakerFarmer(),
                    SpeakerType.System => I18n.Timeline.SpeakerScene(),
                    _ => _npcDisplayName
                };

                int reservedForAvatar = entry.SpeakerType == SpeakerType.System ? 0 : (AvatarSize + AvatarGap);
                int maxBubbleWidth = entry.SpeakerType == SpeakerType.System
                    ? Math.Max(260, (int)(availableWidth * 0.85f))
                    : Math.Max(220, availableWidth - reservedForAvatar);

                int maxTextPixelWidth = maxBubbleWidth - padX * 2;
                string wrapped = Game1.parseText(entry.Text ?? string.Empty, Game1.dialogueFont, (int)(maxTextPixelWidth / TextFontScale));
                Vector2 textSize = Game1.dialogueFont.MeasureString(wrapped) * TextFontScale;
                Vector2 speakerSize = Game1.smallFont.MeasureString(speaker);

                float contentInnerWidth = Math.Max(textSize.X, speakerSize.X);
                int bubbleWidth = (int)Math.Clamp(contentInnerWidth + padX * 2, minBubbleWidth, maxBubbleWidth);
                int bubbleHeight = (int)(speakerSize.Y + 4 + textSize.Y + padY * 2);

                int entryHeight = entry.SpeakerType == SpeakerType.System
                    ? bubbleHeight
                    : Math.Max(bubbleHeight, AvatarSize);

                _measuredEntries.Add(new MeasuredEntry
                {
                    Index = i,
                    Id = i.ToString(),
                    Speaker = speaker,
                    SpeakerType = entry.SpeakerType,
                    WrappedText = wrapped,
                    Width = bubbleWidth,
                    Height = entryHeight,
                    BubbleHeight = bubbleHeight,
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
                string dateLabel = MemoryManager.GetDisplayDateLabel(entry);

                int maxTextPixelWidth = maxCardWidth - padX * 2;
                string wrapped = Game1.parseText(entry.Content ?? string.Empty, Game1.dialogueFont, (int)(maxTextPixelWidth / TextFontScale));
                Vector2 textSize = Game1.dialogueFont.MeasureString(wrapped) * TextFontScale;
                Vector2 dateSize = Game1.smallFont.MeasureString(dateLabel);

                float headerWidthNeeded = dateSize.X + 80;
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
                    BubbleHeight = cardHeight,
                    InnerPadding = new Point(padX, padY)
                });
            }
        }
    }

    private void RebuildVisibleLayout()
    {
        _visibleLayouts.Clear();
        int visibleAreaHeight = yPositionOnScreen + height - BottomBarHeight - 16 - _contentTopY;
        int currentY = _contentTopY;

        for (int i = _startIndex; i < _measuredEntries.Count; i++)
        {
            var m = _measuredEntries[i];
            if (currentY + m.Height > _contentTopY + visibleAreaHeight && _visibleLayouts.Count > 0)
                break;

            var layout = new VisibleEntryLayout
            {
                Measured = m,
                BoxRect = Rectangle.Empty,
                AvatarRect = Rectangle.Empty,
                CheckboxRect = Rectangle.Empty,
                EditRect = Rectangle.Empty,
                DeleteRect = Rectangle.Empty
            };

            if (_currentTab == 0)
            {
                int bubbleY = currentY + (m.Height - m.BubbleHeight) / 2;
                int avatarY = currentY + (m.Height - AvatarSize) / 2;

                if (m.SpeakerType == SpeakerType.Player)
                {
                    int avatarX = xPositionOnScreen + width - RightPadding - AvatarSize;
                    int bubbleX = avatarX - AvatarGap - m.Width;
                    layout.AvatarRect = new Rectangle(avatarX, avatarY, AvatarSize, AvatarSize);
                    layout.BoxRect = new Rectangle(bubbleX, bubbleY, m.Width, m.BubbleHeight);
                }
                else if (m.SpeakerType == SpeakerType.System)
                {
                    int bubbleX = xPositionOnScreen + (width - m.Width) / 2;
                    layout.AvatarRect = Rectangle.Empty;
                    layout.BoxRect = new Rectangle(bubbleX, bubbleY, m.Width, m.BubbleHeight);
                }
                else
                {
                    int avatarX = xPositionOnScreen + LeftPadding;
                    int bubbleX = avatarX + AvatarSize + AvatarGap;
                    layout.AvatarRect = new Rectangle(avatarX, avatarY, AvatarSize, AvatarSize);
                    layout.BoxRect = new Rectangle(bubbleX, bubbleY, m.Width, m.BubbleHeight);
                }
            }
            else
            {
                bool hasCheckbox = _currentTab == 1 || _currentTab == 2;
                int startX = xPositionOnScreen + LeftPadding;

                if (hasCheckbox)
                {
                    int cbSize = (int)(CheckboxSize * CheckboxScale);
                    layout.CheckboxRect = new Rectangle(startX, currentY + (m.Height - cbSize) / 2, cbSize, cbSize);
                    startX += 44;
                }

                layout.BoxRect = new Rectangle(startX, currentY, m.Width, m.Height);

                const int btnSize = 32;
                int btnY = currentY + m.InnerPadding.Y - 2;
                layout.DeleteRect = new Rectangle(startX + m.Width - m.InnerPadding.X - btnSize, btnY, btnSize, btnSize);
                layout.EditRect = new Rectangle(layout.DeleteRect.X - btnSize - 8, btnY, btnSize, btnSize);
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
        if (!CanPageLeft)
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
        if (!CanPageRight)
        {
            Game1.playSound("cancel");
            return;
        }
        _daysAgo--;
        RefreshPagedChat();
        Game1.playSound("smallSelect");
    }

    // ──────────────────────────────────────────────────────────────
    // 布局计算
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
            _closeButton.hoverText = I18n.Timeline.CloseButton();
        }
        else
        {
            _closeButton.bounds = new Rectangle(xPositionOnScreen + width - 56, yPositionOnScreen + 16, 44, 44);
        }

        _tabBarY = yPositionOnScreen + 60;
        _contentTopY = _tabBarY + TabBarHeight + 28;

        int btnY = yPositionOnScreen + height - BottomBarHeight - 12;
        _leftArrowRect = new Rectangle(xPositionOnScreen + LeftPadding, btnY, 44, BottomBarHeight);
        _rightArrowRect = new Rectangle(xPositionOnScreen + width - RightPadding - 44, btnY, 44, BottomBarHeight);

        UpdateActionButtonLayout();
    }

    private void UpdateActionButtonLayout()
    {
        int btnY = yPositionOnScreen + height - BottomBarHeight - 12;
        const int archiveBtnW = 160;
        const int dropdownWidth = 190;
        const int gap = 12;

        if (_currentTab >= 0 && _currentTab <= 2)
        {
            string label = GetActionButtonLabel();
            int textWidth = (int)Game1.smallFont.MeasureString(label).X;
            int btnWidth = Math.Max(200, textWidth + 48);

            int totalWidth = dropdownWidth + gap + btnWidth + gap + archiveBtnW;
            int startX = xPositionOnScreen + (width - totalWidth) / 2;

            _npcDropdownRect = new Rectangle(startX, btnY, dropdownWidth, BottomBarHeight);
            _actionButtonRect = new Rectangle(startX + dropdownWidth + gap, btnY, btnWidth, BottomBarHeight);
            _archiveButtonRect = new Rectangle(startX + dropdownWidth + gap + btnWidth + gap, btnY, archiveBtnW, BottomBarHeight);
        }
        else
        {
            _actionButtonRect = Rectangle.Empty;
            int totalWidth = dropdownWidth + gap + archiveBtnW;
            int startX = xPositionOnScreen + (width - totalWidth) / 2;
            _npcDropdownRect = new Rectangle(startX, btnY, dropdownWidth, BottomBarHeight);
            _archiveButtonRect = new Rectangle(startX + dropdownWidth + gap, btnY, archiveBtnW, BottomBarHeight);
        }

        _npcDropdown?.SetHeaderBounds(_npcDropdownRect);
    }

    private string GetActionButtonLabel() => _currentTab switch
    {
        0 => I18n.Timeline.DistillThisPage(),
        1 => I18n.Timeline.ConsolidateToWeekly(_selectedEntryIds.Count),
        2 => I18n.Timeline.ElevateToChronicle(_selectedEntryIds.Count),
        _ => string.Empty
    };

    public override void gameWindowSizeChanged(Rectangle oldBounds, Rectangle newBounds)
    {
        base.gameWindowSizeChanged(oldBounds, newBounds);
        UpdateLayout();
        BuildNpcDropdownItems();
        RefreshEntries();
    }

    // ──────────────────────────────────────────────────────────────
    // 交互处理
    // ──────────────────────────────────────────────────────────────

    public override void receiveLeftClick(int x, int y, bool playSound = true)
    {
        if (_npcDropdown != null && _npcDropdown.ReceiveLeftClick(x, y))
            return;

        if (_closeButton.containsPoint(x, y))
        {
            Close();
            return;
        }

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
                    UpdateActionButtonLayout();
                    RefreshEntries();
                }
                return;
            }
        }

        if (_currentTab == 0)
        {
            if (_leftArrowRect.Contains(x, y) && CanPageLeft) { PageLeft(); return; }
            if (_rightArrowRect.Contains(x, y) && CanPageRight) { PageRight(); return; }
        }

        if (_archiveButtonRect != Rectangle.Empty && _archiveButtonRect.Contains(x, y))
        {
            Game1.playSound("bigSelect");
            Game1.activeClickableMenu = new ArchivedMemoryMenu(_npcName, this,
                () => MemoryManager.Instance.GetArchivedTimelineMemories(_npcName),
                id => MemoryManager.Instance.RestoreTimelineMemory(_npcName, id),
                id => MemoryManager.Instance.DeleteArchivedTimelineMemory(_npcName, id),
                MemoryManager.MaxArchivedTimelineMemoriesPerNpc,
                () => I18n.Timeline.ArchiveTitle(_npcName),
                () => I18n.Timeline.ArchiveEmpty(),
                () => I18n.Timeline.ArchiveRuleHint(MemoryManager.MaxArchivedTimelineMemoriesPerNpc),
                () => MemoryManager.Instance.ClearArchivedTimelineMemories(_npcName));
            return;
        }

        if (_currentTab >= 0 && _currentTab <= 2 && _actionButtonRect.Contains(x, y))
        {
            HandleActionButton();
            return;
        }

        if (_currentTab >= 1 && _currentTab <= 3)
        {
            foreach (var item in _visibleLayouts)
            {
                var entry = _tierEntries.FirstOrDefault(e => e.Id == item.Measured.Id);
                if (entry == null) continue;

                if (item.DeleteRect.Contains(x, y))
                {
                    ConfirmDelete(entry);
                    return;
                }

                if (item.EditRect.Contains(x, y))
                {
                    Game1.activeClickableMenu = new AddMemoryInputMenu(
                        _npcName, this, entry, 0,
                        customSubmit: text => MemoryManager.Instance.EditTimelineMemory(_npcName, entry.Id, text));
                    return;
                }

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
            I18n.Timeline.DeleteConfirm(entry.Content),
            _ =>
            {
                MemoryManager.Instance.ArchiveTimelineMemories(_npcName, new[] { entry }, "ManualDeleted");
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
            Game1.addHUDMessage(new HUDMessage(I18n.Timeline.DistillLlmDisabled(), 3));
            return;
        }

        if (_currentTab == 0)
        {
            long now = Environment.TickCount64;

            if (_lastSummarizeTickMs > 0)
            {
                long elapsedMs = now - _lastSummarizeTickMs;
                if (elapsedMs < CooldownSeconds * 1000L)
                {
                    int remaining = Math.Max(1, (int)Math.Ceiling((CooldownSeconds * 1000L - elapsedMs) / 1000.0));
                    Game1.playSound("cancel");
                    Game1.addHUDMessage(new HUDMessage(I18n.Timeline.CooldownHud(remaining), 3));
                    return;
                }
            }

            if (_todayChatEntries.Count == 0)
            {
                Game1.playSound("cancel");
                Game1.addHUDMessage(new HUDMessage(I18n.Timeline.DistillNoHistory(_npcDisplayName), 0));
                return;
            }

            _lastSummarizeTickMs = now;
            Game1.playSound("bigSelect");
            Game1.activeClickableMenu = new TimelineDistillMenu(_npcName, this, MemoryTier.Daily, dateFilter: ViewDate);
        }
        else if (_currentTab == 1 || _currentTab == 2)
        {
            if (_selectedEntryIds.Count < MinCondenseSelection)
            {
                Game1.playSound("cancel");
                Game1.addHUDMessage(new HUDMessage(I18n.Timeline.CondenseMinCount(MinCondenseSelection), 3));
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
            Game1.activeClickableMenu = new TimelineDistillMenu(_npcName, this, targetTier, selectedEntries, condenseTask);
        }
    }

    public override void receiveScrollWheelAction(int direction)
    {
        if (_npcDropdown != null && _npcDropdown.IsOpen)
        {
            _npcDropdown.ReceiveScrollWheel(direction);
            return;
        }

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
            if (_npcDropdown != null && _npcDropdown.IsOpen)
            {
                _npcDropdown.Close();
                return;
            }
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

        _returnMenu?.draw(b);
        b.Draw(Game1.fadeToBlackRect, Game1.graphics.GraphicsDevice.Viewport.Bounds, Color.Black * 0.4f);

        IClickableMenu.drawTextureBox(b, xPositionOnScreen - 16, yPositionOnScreen - 16, width + 32, height + 32, Color.White);
        IClickableMenu.drawTextureBox(b, xPositionOnScreen - 8, yPositionOnScreen - 8, width + 16, height + 16, Color.White);
        Game1.drawDialogueBox(xPositionOnScreen, yPositionOnScreen, width, height, false, true);

        string title = I18n.Timeline.Title(_npcDisplayName);
        Vector2 titleSize = Game1.dialogueFont.MeasureString(title);
        b.DrawString(Game1.dialogueFont, title,
            new Vector2(xPositionOnScreen + (width - titleSize.X) / 2f, yPositionOnScreen + 22),
            Game1.textColor);

        DrawTabBar(b, mx, my);

        if (_currentTab == 0)
            DrawTodayChat(b, mx, my, viewDate);
        else
            DrawTierMemories(b, mx, my);

        DrawBottomBar(b, mx, my);
        DrawScrollbar(b);

        UiHelper.UpdateButtonScale(ref _closeButtonHoverScale, _closeButton, mx, my);
        _closeButton.scale = 3.5f * _closeButtonHoverScale;
        _closeButton.draw(b);

        _npcDropdown?.DrawPopup(b);

        if (!_npcDropdown.IsOpen && string.IsNullOrEmpty(_hoveredTooltip) && _currentTab == 0)
        {
            if (_leftArrowRect.Contains(mx, my) && CanPageLeft)
                _hoveredTooltip = I18n.Timeline.PrevDay();
            else if (_rightArrowRect.Contains(mx, my) && CanPageRight)
                _hoveredTooltip = I18n.Timeline.NextDay();
        }
        if (!string.IsNullOrEmpty(_hoveredTooltip))
            IClickableMenu.drawHoverText(b, _hoveredTooltip, Game1.smallFont);

        drawMouse(b);
    }

    private void DrawTabBar(SpriteBatch b, int mx, int my)
    {
        int tabWidth = (width - LeftPadding - RightPadding) / 4;
        string[] labels =
        {
            I18n.Timeline.TabChats(),
            I18n.Timeline.TabImpressions(),
            I18n.Timeline.TabWeekly(),
            I18n.Timeline.TabChronicle()
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
        string dateText = _daysAgo == 0
            ? I18n.Timeline.TodayDate(MemoryManager.FormatGameDateLabel(viewDate))
            : MemoryManager.FormatGameDateLabel(viewDate);
        var dateSize = Game1.smallFont.MeasureString(dateText);
        b.DrawString(Game1.smallFont, dateText,
            new Vector2(xPositionOnScreen + (width - dateSize.X) / 2f, _contentTopY - 22),
            Color.Gray);

        if (_todayChatEntries.Count == 0)
        {
            string empty = I18n.Timeline.EmptyChats();
            var size = Game1.dialogueFont.MeasureString(empty);
            b.DrawString(Game1.dialogueFont, empty,
                new Vector2(xPositionOnScreen + (width - size.X) / 2f, _contentTopY + 40),
                Color.Gray);
            return;
        }

        foreach (var item in _visibleLayouts)
        {
            var m = item.Measured;

            // 1. 头像渲染
            if (!item.AvatarRect.IsEmpty)
            {
                bool isPlayer = m.SpeakerType == SpeakerType.Player;
                bool npcSpriteReady = _currentNpcSprite != null && !_currentNpcSourceRect.IsEmpty;
                bool playerReady = Game1.player?.FarmerRenderer != null;
                bool canDrawAvatar = isPlayer ? playerReady : npcSpriteReady;

                if (canDrawAvatar)
                {
                    // 木质底衬
                    IClickableMenu.drawTextureBox(b, Game1.mouseCursors,
                        new Rectangle(432, 439, 9, 9),
                        item.AvatarRect.X - 2, item.AvatarRect.Y - 2,
                        item.AvatarRect.Width + 4, item.AvatarRect.Height + 4,
                        Color.White, 2.5f, false);

                    if (isPlayer)
                    {
                        DrawFarmerAvatar(b, item.AvatarRect);
                    }
                    else
                    {
                        // 绘制 NPC 行走图正脸
                        b.Draw(
                            _currentNpcSprite,
                            item.AvatarRect,
                            _currentNpcSourceRect,
                            Color.White,
                            0f, Vector2.Zero, SpriteEffects.None, 0.89f);
                    }
                }
            }

            // 2. 气泡框
            Color boxBg = m.SpeakerType switch
            {
                SpeakerType.Player => new Color(230, 245, 235),
                SpeakerType.System => new Color(235, 235, 238),
                _ => new Color(255, 246, 232)
            };

            IClickableMenu.drawTextureBox(b, Game1.mouseCursors,
                new Rectangle(432, 439, 9, 9),
                item.BoxRect.X, item.BoxRect.Y, item.BoxRect.Width, item.BoxRect.Height,
                boxBg, 3.5f, false);

            Color speakerColor = m.SpeakerType switch
            {
                SpeakerType.Player => new Color(34, 110, 50),
                SpeakerType.System => Color.DimGray,
                _ => new Color(130, 65, 20)
            };

            // 3. 标签与文本
            Vector2 speakerPos = new Vector2(item.BoxRect.X + m.InnerPadding.X, item.BoxRect.Y + m.InnerPadding.Y);
            b.DrawString(Game1.smallFont, m.Speaker, speakerPos, speakerColor);

            Vector2 textPos = new Vector2(
                item.BoxRect.X + m.InnerPadding.X,
                speakerPos.Y + Game1.smallFont.LineSpacing - 2);

            Color textColor = m.SpeakerType == SpeakerType.System ? Color.DimGray : Game1.textColor;
            b.DrawString(Game1.dialogueFont, m.WrappedText, textPos, textColor, 0f, Vector2.Zero, TextFontScale, SpriteEffects.None, 0.88f);
        }
    }

    /// <summary>
    /// 原生绘制农夫真实外观（包含真实发型、肤色、眼睛与帽子，1:1 对齐像素框）
    /// </summary>
    private static void DrawFarmerAvatar(SpriteBatch b, Rectangle destRect)
    {
        if (Game1.player?.FarmerRenderer == null) return;

        // 农夫头部标准 16x16，缩放到 40px: scale = 40 / 16 = 2.5f
        float scale = (float)destRect.Width / 16f;
        Vector2 basePos = new Vector2(destRect.X, destRect.Y);

        // 1. 原生绘制农夫脸型、发型、眼睛、肤色、配饰（官方 ChatBox 同款标准接口）
        Game1.player.FarmerRenderer.drawMiniPortrat(
            b,
            basePos,
            0.89f,
            scale,
            2, // 朝正下方
            Game1.player,
            1f);

        // 2. 叠戴当前帽子（如果有）
        if (Game1.player.hat.Value != null)
        {
            // 原版 Hat.draw 内部强制乘以了 4f（pixelZoom）
            // 外部必须传入 scale / 4f，真实缩放倍率才正好等于 2.5f，绝不产生巨大化溢出
            float hatScale = scale / 4f;

            // Hat 贴图为 20x20，头像为 16x16
            // 水平对齐：X 偏移 -2 像素
            // 垂直对齐：Y 偏移 -5 像素，使帽檐与发际线精准贴合
            Vector2 hatPos = basePos + new Vector2(-2f * scale, -5f * scale);
            Game1.player.hat.Value.draw(b, hatPos, hatScale, 1f, 0.895f, 2);
        }
    }

    private void DrawTierMemories(SpriteBatch b, int mx, int my)
    {
        if (_tierEntries.Count == 0)
        {
            string empty = I18n.Timeline.EmptyTier();
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

            if (_currentTab == 1 || _currentTab == 2)
            {
                Rectangle src = selected
                    ? new Rectangle(CheckboxCheckedSourceX, CheckboxSourceY, CheckboxSize, CheckboxSize)
                    : new Rectangle(CheckboxUncheckedSourceX, CheckboxSourceY, CheckboxSize, CheckboxSize);

                b.Draw(Game1.mouseCursors,
                    new Vector2(item.CheckboxRect.X, item.CheckboxRect.Y),
                    src, Color.White, 0f, Vector2.Zero, CheckboxScale, SpriteEffects.None, 0.86f);
            }

            Color cardColor = selected
                ? new Color(255, 235, 205)
                : (hovered ? new Color(255, 248, 230) : new Color(252, 244, 234));

            IClickableMenu.drawTextureBox(b, Game1.mouseCursors,
                new Rectangle(432, 439, 9, 9),
                item.BoxRect.X, item.BoxRect.Y, item.BoxRect.Width, item.BoxRect.Height,
                cardColor, 3.5f, false);

            Vector2 datePos = new Vector2(item.BoxRect.X + m.InnerPadding.X, item.BoxRect.Y + m.InnerPadding.Y);
            b.DrawString(Game1.smallFont, m.DateLabel, datePos, new Color(110, 80, 50));

            bool isLeftMouseDown = Mouse.GetState().LeftButton == ButtonState.Pressed;

            bool editHover = item.EditRect.Contains(mx, my);
            bool editPressed = isLeftMouseDown && editHover;
            IconSource.DrawButton(
                b,
                ModEntry.CustomIcons,
                item.EditRect,
                col: 15, baseRow: 1,
                theme: IconTheme.Wood,
                isPressed: editPressed,
                layerDepth: 0.89f);

            bool delHover = item.DeleteRect.Contains(mx, my);
            bool delPressed = isLeftMouseDown && delHover;
            IconSource.DrawButton(
                b,
                ModEntry.CustomIcons,
                item.DeleteRect,
                col: 6, baseRow: 1,
                theme: IconTheme.Wood,
                isPressed: delPressed,
                layerDepth: 0.89f);

            if (!_npcDropdown.IsOpen)
            {
                if (editHover) _hoveredTooltip = I18n.Timeline.EditButtonHover();
                if (delHover) _hoveredTooltip = I18n.Timeline.DeleteButtonHover();
            }

            Vector2 textPos = new Vector2(
                item.BoxRect.X + m.InnerPadding.X,
                datePos.Y + Math.Max(Game1.smallFont.LineSpacing, 26));

            b.DrawString(Game1.dialogueFont, m.WrappedText, textPos, Game1.textColor, 0f, Vector2.Zero, TextFontScale, SpriteEffects.None, 0.88f);
        }
    }

    private void DrawBottomBar(SpriteBatch b, int mx, int my)
    {
        if (_currentTab == 0)
        {
            DrawArrowButton(b, _leftArrowRect, isLeft: true, enabled: CanPageLeft, mx, my);
            DrawArrowButton(b, _rightArrowRect, isLeft: false, enabled: CanPageRight, mx, my);
        }

        _npcDropdown?.DrawHeader(b);

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

        if (_archiveButtonRect != Rectangle.Empty)
        {
            string archiveText = I18n.Timeline.ArchiveButton(_archivedTimelineCount, MemoryManager.MaxArchivedTimelineMemoriesPerNpc);
            bool archiveHover = _archiveButtonRect.Contains(mx, my);
            Color archiveBg = archiveHover ? new Color(255, 235, 205) : new Color(139, 90, 43);

            IClickableMenu.drawTextureBox(b, Game1.mouseCursors,
                new Rectangle(432, 439, 9, 9),
                _archiveButtonRect.X, _archiveButtonRect.Y,
                _archiveButtonRect.Width, _archiveButtonRect.Height,
                archiveBg, 4f, false);

            var archiveLabelSize = Game1.smallFont.MeasureString(archiveText);
            Vector2 archiveTextPos = new Vector2(
                _archiveButtonRect.X + (_archiveButtonRect.Width - archiveLabelSize.X) / 2f,
                _archiveButtonRect.Y + (_archiveButtonRect.Height - archiveLabelSize.Y) / 2f);

            b.DrawString(Game1.smallFont, archiveText, archiveTextPos, archiveHover ? Game1.textColor : Color.White);
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

    private void DrawScrollbar(SpriteBatch b)
    {
        int maxStart = CalculateMaxStartIndex();
        if (maxStart <= 0) return;

        int trackX = xPositionOnScreen + width - RightPadding + 12;
        int trackTop = _contentTopY;
        int trackHeight = yPositionOnScreen + height - 16 - BottomBarHeight - trackTop;
        if (trackHeight <= 0) return;

        b.Draw(Game1.staminaRect, new Rectangle(trackX, trackTop, 6, trackHeight), Color.Black * 0.22f);

        float visibleRatio = (float)_visibleLayouts.Count / _measuredEntries.Count;
        int thumbHeight = Math.Max(28, (int)(trackHeight * visibleRatio));
        int thumbY = trackTop + (int)((trackHeight - thumbHeight) * ((float)_startIndex / maxStart));
        b.Draw(Game1.staminaRect, new Rectangle(trackX, thumbY, 6, thumbHeight), Color.Wheat * 0.85f);
    }

    // ──────────────────────────────────────────────────────────────
    // 嵌套辅助组件：DropupList（上拉列表栏）
    // ──────────────────────────────────────────────────────────────

    private sealed class DropupList
    {
        private Rectangle _headerRect;
        private readonly int _itemHeight;
        private readonly int _maxVisibleItems;

        private List<(string Id, string Label)> _items = new();
        private string _selectedId;
        private bool _isOpen;
        private int _scrollIndex;

        public Action<string> OnItemSelected;
        public string HeaderPrefix { get; set; }

        public bool IsOpen => _isOpen;
        public string SelectedId => _selectedId;

        public DropupList(Rectangle headerRect, int itemHeight = 38, int maxVisibleItems = 7)
        {
            _headerRect = headerRect;
            _itemHeight = itemHeight;
            _maxVisibleItems = maxVisibleItems;
        }

        public void SetHeaderBounds(Rectangle rect) => _headerRect = rect;

        public void SetItems(IReadOnlyList<(string Id, string Label)> items, string selectedId)
        {
            _items = new List<(string, string)>(items);
            _selectedId = selectedId;
            _scrollIndex = 0;
        }

        public void SetSelectedId(string selectedId) => _selectedId = selectedId;

        public void ToggleOpen()
        {
            _isOpen = !_isOpen;
            if (_isOpen)
            {
                int selectedIdx = _items.FindIndex(it => it.Id == _selectedId);
                if (selectedIdx >= 0)
                    _scrollIndex = Math.Clamp(selectedIdx - _maxVisibleItems / 2, 0, Math.Max(0, _items.Count - _maxVisibleItems));
                else
                    _scrollIndex = 0;
            }
        }

        public void Close() => _isOpen = false;

        public bool ReceiveLeftClick(int x, int y)
        {
            if (_headerRect.Contains(x, y))
            {
                ToggleOpen();
                Game1.playSound("shwip");
                return true;
            }

            if (!_isOpen) return false;

            int visible = Math.Min(_maxVisibleItems, _items.Count - _scrollIndex);
            for (int i = 0; i < visible; i++)
            {
                int iy = _headerRect.Y - (visible - i) * _itemHeight;
                var ir = new Rectangle(_headerRect.X, iy, _headerRect.Width, _itemHeight);
                if (ir.Contains(x, y))
                {
                    _selectedId = _items[_scrollIndex + i].Id;
                    _isOpen = false;
                    OnItemSelected?.Invoke(_selectedId);
                    return true;
                }
            }

            _isOpen = false;
            return true;
        }

        public bool ReceiveScrollWheel(int direction)
        {
            if (!_isOpen || _items.Count <= _maxVisibleItems) return false;

            if (direction > 0 && _scrollIndex > 0)
                _scrollIndex--;
            else if (direction < 0 && _scrollIndex < _items.Count - _maxVisibleItems)
                _scrollIndex++;
            else
                return false;

            return true;
        }

        public void DrawHeader(SpriteBatch b)
        {
            int mx = Game1.getMouseX(), my = Game1.getMouseY();
            bool hover = _headerRect.Contains(mx, my);

            Color headerBg = _isOpen ? new Color(210, 180, 140)
                          : hover ? new Color(255, 235, 205)
                          : new Color(139, 90, 43);

            IClickableMenu.drawTextureBox(b, Game1.mouseCursors,
                new Rectangle(432, 439, 9, 9),
                _headerRect.X, _headerRect.Y, _headerRect.Width, _headerRect.Height,
                headerBg, 4f, false);

            string selLabel = _items.FirstOrDefault(it => it.Id == _selectedId).Label ?? "";
            if (string.IsNullOrEmpty(selLabel)) selLabel = "—";
            string label = (HeaderPrefix ?? "") + selLabel;

            float maxTextW = _headerRect.Width - 36;
            string displayLabel = UiHelper.TruncateString(label, Game1.smallFont, maxTextW);
            var size = Game1.smallFont.MeasureString(displayLabel);

            b.DrawString(Game1.smallFont, displayLabel,
                new Vector2(_headerRect.X + 12, _headerRect.Y + (_headerRect.Height - size.Y) / 2f),
                hover && !_isOpen ? Game1.textColor : Color.White);

            SpriteEffects effect = _isOpen ? SpriteEffects.None : SpriteEffects.FlipVertically;
            Vector2 arrowPos = new Vector2(_headerRect.Right - 26, _headerRect.Y + (_headerRect.Height - 22) / 2f);

            b.Draw(Game1.mouseCursors, arrowPos,
                new Rectangle(437, 450, 10, 11),
                Color.White, 0f, Vector2.Zero, 2f, effect, 1f);
        }

        public void DrawPopup(SpriteBatch b)
        {
            if (!_isOpen) return;

            int mx = Game1.getMouseX(), my = Game1.getMouseY();
            int visible = Math.Min(_maxVisibleItems, _items.Count - _scrollIndex);

            for (int i = 0; i < visible; i++)
            {
                int idx = _scrollIndex + i;
                var item = _items[idx];
                int iy = _headerRect.Y - (visible - i) * _itemHeight;
                var ir = new Rectangle(_headerRect.X, iy, _headerRect.Width, _itemHeight);

                bool selected = item.Id == _selectedId;
                bool ihover = ir.Contains(mx, my);
                Color bg = selected ? new Color(210, 180, 140)
                         : ihover ? new Color(255, 235, 205)
                         : Color.White;

                IClickableMenu.drawTextureBox(b, Game1.mouseCursors,
                    new Rectangle(432, 439, 9, 9),
                    ir.X, ir.Y, ir.Width, ir.Height, bg, 4f, false);

                string truncatedLabel = UiHelper.TruncateString(item.Label, Game1.smallFont, ir.Width - 20);

                b.DrawString(Game1.smallFont, truncatedLabel,
                    new Vector2(ir.X + 8, ir.Y + (ir.Height - Game1.smallFont.LineSpacing) / 2f),
                    selected ? Color.White : (ihover ? Game1.textColor : Color.Black));
            }
        }
    }

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
        public int BubbleHeight;
        public Point InnerPadding;
    }

    private class VisibleEntryLayout
    {
        public MeasuredEntry Measured;
        public Rectangle BoxRect;
        public Rectangle AvatarRect;
        public Rectangle CheckboxRect;
        public Rectangle EditRect;
        public Rectangle DeleteRect;
    }

    private void Close()
    {
        Game1.playSound("bigDeSelect");
        if (_returnMenu is IMemoryRefreshTarget refreshable)
            refreshable.RefreshEntries();

        var targetMenu = _ownerMenu ?? _returnMenu;
        if (targetMenu == null)
        {
            exitThisMenu();
        }
        else if (Game1.activeClickableMenu == this)
        {
            Game1.activeClickableMenu = targetMenu;
            RestoreMenuFocus(targetMenu);
        }
    }

    protected override void cleanupBeforeExit()
    {
        base.cleanupBeforeExit();

        var targetMenu = _ownerMenu ?? _returnMenu;
        if (targetMenu != null && Game1.activeClickableMenu == this)
        {
            Game1.activeClickableMenu = targetMenu;
            RestoreMenuFocus(targetMenu);
        }
    }

    private static void RestoreMenuFocus(IClickableMenu menu)
    {
        if (menu is DialogueTextInputMenu textMenu)
        {
            textMenu.RestoreFocus();
        }
        else if (menu != null)
        {
            var method = menu.GetType().GetMethod("RestoreFocus", System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            if (method != null)
            {
                method.Invoke(menu, null);
            }
            else
            {
                var field = menu.GetType().GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                    .FirstOrDefault(f => typeof(DialogueTextInputMenu).IsAssignableFrom(f.FieldType));
                if (field?.GetValue(menu) is DialogueTextInputMenu innerTextMenu)
                {
                    innerTextMenu.RestoreFocus();
                }
            }
        }
    }
}