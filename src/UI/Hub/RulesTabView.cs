#nullable enable
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;
using System;
using System.Collections.Generic;
using System.Linq;
using ValleytalkReborn.Services;

namespace ValleytalkReborn.UI;

/// <summary>
/// TAB0 规则三栏一体化工作台（左栏满容多显一行 + 提示语统一木炭黑精修版）
/// </summary>
internal sealed class RulesTabView : IHubTabView
{
    private readonly IntegratedHubMenu _hub;

    // ── 布局区域 ──
    private Rectangle _leftColRect;
    private Rectangle _midColRect;
    private Rectangle _rightColRect;
    private Rectangle _addBtnRect;
    private Rectangle _archiveBtnRect;
    private Rectangle _saveAndExitBtnRect;

    // ── 左栏状态（Scope） ──
    private record ScopeItem(string Id, string DisplayName, Texture2D? Sprite, Rectangle SourceRect, Rectangle? IconRect = null);
    private readonly List<ScopeItem> _scopeItems = new();
    private int _selectedScopeIndex = 0;
    private int _leftScrollOffset = 0;
    private const int ScopeItemHeight = 40;
    private const int PinnedCount = 2; // 前 2 项（全部、小镇共识）常驻置顶固定
    private bool _isDraggingLeftScrollbar = false;

    // ── 中栏状态（规则卡片） ──
    private readonly List<MemoryEntry> _filteredRules = new();
    private int _selectedRuleIndex = -1;
    private int _midScrollOffset = 0;
    private const int RuleCardHeight = 76;
    private bool _isDraggingMidScrollbar = false;

    // ── 右栏状态（内联编辑） ──
    private MemoryEntry? _activeEditingEntry;
    private DialogueTextInputBox? _editInputBox;
    private MemoryCategory _editCategory;
    private Rectangle _editFactCapsuleRect;
    private Rectangle _editBehaviorCapsuleRect;
    private Rectangle _saveBtnRect;
    private Rectangle _deleteBtnRect;

    public string HoveredTooltip { get; private set; } = "";

    public RulesTabView(IntegratedHubMenu hub)
    {
        _hub = hub;
        BuildScopeItems();
    }

    private void BuildScopeItems()
    {
        _scopeItems.Clear();

        _scopeItems.Add(new ScopeItem("__ALL__", I18n.Hub.ScopeAll(), null, Rectangle.Empty,
            IconSource.Star(IconTheme.Wood, IconState.Normal)));
        _scopeItems.Add(new ScopeItem("WORLD", I18n.Hub.ScopeGlobal(), null, Rectangle.Empty,
            IconSource.House(IconTheme.Wood, IconState.Normal)));

        var candidates = NpcCandidateQueryService.GetCleanedCandidates();
        foreach (var c in candidates)
        {
            var (sprite, srcRect) = GetNpcWalkingHeadSprite(c.Id);
            _scopeItems.Add(new ScopeItem(c.Id, c.DisplayName, sprite, srcRect));
        }
    }

    private static (Texture2D? Texture, Rectangle SourceRect) GetNpcWalkingHeadSprite(string npcName)
    {
        if (string.IsNullOrWhiteSpace(npcName)) return (null, Rectangle.Empty);

        NPC? npc = Game1.getCharacterFromName(npcName);
        Texture2D? texture = null;

        try
        {
            if (npc?.Sprite?.Texture != null && !npc.Sprite.Texture.IsDisposed)
                texture = npc.Sprite.Texture;
        }
        catch { }

        if (texture == null)
        {
            string assetName = npc?.getTextureName() ?? npcName;
            try
            {
                texture = Game1.content.Load<Texture2D>($"Characters\\{assetName}");
            }
            catch
            {
                return (null, Rectangle.Empty);
            }
        }

        if (texture == null || texture.IsDisposed)
            return (null, Rectangle.Empty);

        int frameWidth = 16;
        if (npc?.Sprite != null && npc.Sprite.SpriteWidth > 0)
        {
            frameWidth = npc.Sprite.SpriteWidth;
        }
        else if (texture.Width >= 64)
        {
            frameWidth = texture.Width / 4;
        }
        else if (texture.Width >= 32)
        {
            frameWidth = texture.Width / 2;
        }
        else
        {
            frameWidth = texture.Width;
        }

        int frameHeight = (npc?.Sprite != null && npc.Sprite.SpriteHeight > 0)
            ? npc.Sprite.SpriteHeight
            : Math.Min(texture.Height, frameWidth * 2);

        int topY = FindSpriteTopY(texture, frameWidth, frameHeight);
        int headHeight = Math.Min(frameWidth, texture.Height - topY);
        return (texture, new Rectangle(0, topY, frameWidth, headHeight));
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

    public void Layout(Rectangle menuBounds, Rectangle contentBounds)
    {
        int colGap = 12;
        int totalW = contentBounds.Width;
        int topY = contentBounds.Y;
        int h = contentBounds.Height;

        int leftW = (int)(totalW * 0.26f);
        int midW = (int)(totalW * 0.42f);
        int rightW = totalW - leftW - midW - colGap * 2;

        _leftColRect = new Rectangle(contentBounds.X, topY, leftW, h);
        _midColRect = new Rectangle(_leftColRect.Right + colGap, topY, midW, h);
        _rightColRect = new Rectangle(_midColRect.Right + colGap, topY, rightW, h);

        _addBtnRect = new Rectangle(_midColRect.Right - 94, _midColRect.Y + 8, 88, 28);

        int editPad = 14;
        int inputY = _rightColRect.Y + 68;
        int inputH = Math.Clamp(h - 190, 110, 180);

        _editInputBox = new DialogueTextInputBox(RuleManager.MaxRuleLength, (int)(RuleManager.MaxRuleLength * 0.9f))
        {
            Position = new Vector2(_rightColRect.X + editPad, inputY),
            Extent = new Vector2(_rightColRect.Width - editPad * 2, inputH),
            UseCustomFont = true,
            CustomFontSize = CustomFontManager.SizeRegular,
            CounterFontSize = CustomFontManager.SizeSmall,
            DrawFrame = true,
            ShowCharacterCount = true,
            AllowNewlines = true,
            TextColor = RulesTheme.TextPrimary,
            Selected = false
        };

        int catY = inputY + inputH + 16;
        int catW = (_rightColRect.Width - editPad * 2 - 8) / 2;
        _editFactCapsuleRect = new Rectangle(_rightColRect.X + editPad, catY, catW, 30);
        _editBehaviorCapsuleRect = new Rectangle(_rightColRect.X + editPad + catW + 8, catY, catW, 30);

        int btnY = _rightColRect.Bottom - 44;
        int btnW = (_rightColRect.Width - editPad * 2 - 10) / 2;
        _saveBtnRect = new Rectangle(_rightColRect.X + editPad, btnY, btnW, 34);
        _deleteBtnRect = new Rectangle(_rightColRect.X + editPad + btnW + 10, btnY, btnW, 34);

        int saveExitW = 200;
        int saveExitH = 40;
        int footerY = contentBounds.Bottom + 19;
        int centerX = contentBounds.X + (contentBounds.Width - saveExitW) / 2;
        _saveAndExitBtnRect = new Rectangle(centerX, footerY, saveExitW, saveExitH);

        int archiveBtnW = 146;
        int archiveBtnH = 38;
        _archiveBtnRect = new Rectangle(contentBounds.Right - archiveBtnW, footerY + 1, archiveBtnW, archiveBtnH);
    }

    public void RefreshFromHub()
    {
        CheckAndArchiveExpiredRules();

        if (_selectedScopeIndex < 0 || _selectedScopeIndex >= _scopeItems.Count)
            _selectedScopeIndex = 0;

        string curScope = _scopeItems[_selectedScopeIndex].Id;
        _filteredRules.Clear();

        if (curScope == "__ALL__")
            _filteredRules.AddRange(RuleManager.Instance.GetRules(null));
        else
            _filteredRules.AddRange(RuleManager.Instance.GetRules(curScope));

        if (_activeEditingEntry != null)
        {
            int idx = _filteredRules.FindIndex(r => r.Id == _activeEditingEntry.Id);
            if (idx >= 0)
            {
                _selectedRuleIndex = idx;
                _activeEditingEntry = _filteredRules[idx];
            }
            else
            {
                _selectedRuleIndex = -1;
                _activeEditingEntry = null;
            }
        }

        SyncEditorWithActiveEntry();
    }

    private static void CheckAndArchiveExpiredRules()
    {
        int today = (int)Game1.Date.TotalDays;
        var allRules = RuleManager.Instance.GetRules(null).ToList();
        bool changed = false;

        foreach (var r in allRules)
        {
            if (r.ExpireDay >= 0 && r.ExpireDay < today)
            {
                RuleManager.Instance.RemoveRule(r.Id);
                RuleArchiveManager.ArchiveRule(r);
                changed = true;
            }
        }

        if (changed)
        {
            RuleManager.Instance.Save();
        }
    }

    private void SyncEditorWithActiveEntry()
    {
        if (_activeEditingEntry != null)
        {
            _editInputBox?.SetText(_activeEditingEntry.Content);
            _editCategory = _activeEditingEntry.Category;
        }
        else
        {
            _editInputBox?.SetText("");
            if (_editInputBox != null) _editInputBox.Selected = false;
        }
    }

    public void OnActivated() => RefreshFromHub();

    public void OnDeactivated()
    {
        if (_editInputBox != null)
        {
            _editInputBox.Selected = false;
            if (Game1.keyboardDispatcher.Subscriber == _editInputBox)
                Game1.keyboardDispatcher.Subscriber = null;
        }
    }

    public void OnReturnedFromChild() => RefreshFromHub();

    public void Update(GameTime time) => _editInputBox?.Update(time);

    public bool ReceiveScrollWheel(int direction)
    {
        int mx = Game1.getMouseX();
        int my = Game1.getMouseY();

        if (_leftColRect.Contains(mx, my))
        {
            int pinnedTopY = _leftColRect.Y + 4;
            int pinnedTotalH = PinnedCount * ScopeItemHeight + 3;
            int scrollAreaTopY = pinnedTopY + pinnedTotalH;
            int scrollAreaH = _leftColRect.Bottom - 4 - scrollAreaTopY;
            int scrollableCount = Math.Max(0, _scopeItems.Count - PinnedCount);
            int visibleMax = Math.Max(1, scrollAreaH / ScopeItemHeight);
            int maxScroll = Math.Max(0, scrollableCount - visibleMax);

            _leftScrollOffset = Math.Clamp(_leftScrollOffset - (direction > 0 ? 1 : -1), 0, maxScroll);
            Game1.playSound("shwip");
            return true;
        }

        if (_midColRect.Contains(mx, my))
        {
            int visibleCount = Math.Max(1, (_midColRect.Height - 48) / (RuleCardHeight + 8));
            int maxScroll = Math.Max(0, _filteredRules.Count - visibleCount);
            _midScrollOffset = Math.Clamp(_midScrollOffset - (direction > 0 ? 1 : -1), 0, maxScroll);
            Game1.playSound("shwip");
            return true;
        }

        if (_rightColRect.Contains(mx, my) && _editInputBox != null)
        {
            _editInputBox.ReceiveScrollWheel(direction);
            return true;
        }

        return false;
    }

    public bool ReceiveLeftClick(int x, int y)
    {
        if (_archiveBtnRect.Contains(x, y))
        {
            OpenArchiveMenu();
            return true;
        }

        if (_saveAndExitBtnRect.Contains(x, y))
        {
            SaveAndExit();
            return true;
        }

        // ── 左栏交互处理（置顶项 + 独立滚动区） ──
        if (_leftColRect.Contains(x, y))
        {
            int pinnedTopY = _leftColRect.Y + 4;
            int itemWNormal = _leftColRect.Width - 12;

            // 1. 检测前 2 个置顶项
            for (int i = 0; i < Math.Min(PinnedCount, _scopeItems.Count); i++)
            {
                var pRect = new Rectangle(_leftColRect.X + 6, pinnedTopY + i * ScopeItemHeight, itemWNormal, ScopeItemHeight - 4);
                if (pRect.Contains(x, y))
                {
                    if (_selectedScopeIndex != i)
                    {
                        _selectedScopeIndex = i;
                        _midScrollOffset = 0;
                        _selectedRuleIndex = -1;
                        _activeEditingEntry = null;
                        Game1.playSound("smallSelect");
                        RefreshFromHub();
                    }
                    return true;
                }
            }

            // 2. 检测下方独立滚动的 NPC 列表（高度极致利用，多容纳一行）
            int pinnedTotalH = PinnedCount * ScopeItemHeight + 3;
            int scrollAreaTopY = pinnedTopY + pinnedTotalH;
            int scrollAreaH = _leftColRect.Bottom - 4 - scrollAreaTopY;
            int scrollableCount = Math.Max(0, _scopeItems.Count - PinnedCount);
            int visibleMax = Math.Max(1, scrollAreaH / ScopeItemHeight);
            bool hasLeftScroll = scrollableCount > visibleMax;

            if (hasLeftScroll)
            {
                var trackRect = new Rectangle(_leftColRect.Right - 9, scrollAreaTopY, 5, scrollAreaH);
                if (trackRect.Contains(x, y))
                {
                    _isDraggingLeftScrollbar = true;
                    int maxScroll = scrollableCount - visibleMax;
                    float visibleRatio = Math.Clamp((float)visibleMax / scrollableCount, 0.15f, 1f);
                    int thumbH = Math.Max(24, (int)(trackRect.Height * visibleRatio));
                    UpdateLeftScrollFromMouse(y, trackRect, thumbH, maxScroll);
                    return true;
                }
            }

            int itemRightPad = hasLeftScroll ? 15 : 6;
            int itemW = _leftColRect.Width - 6 - itemRightPad;

            for (int i = 0; i < visibleMax && (_leftScrollOffset + i) < scrollableCount; i++)
            {
                int itemIdx = PinnedCount + _leftScrollOffset + i;
                var itemRect = new Rectangle(_leftColRect.X + 6, scrollAreaTopY + i * ScopeItemHeight, itemW, ScopeItemHeight - 4);
                if (itemRect.Contains(x, y))
                {
                    if (_selectedScopeIndex != itemIdx)
                    {
                        _selectedScopeIndex = itemIdx;
                        _midScrollOffset = 0;
                        _selectedRuleIndex = -1;
                        _activeEditingEntry = null;
                        Game1.playSound("smallSelect");
                        RefreshFromHub();
                    }
                    return true;
                }
            }
            return true;
        }

        if (_addBtnRect.Contains(x, y))
        {
            Game1.playSound("bigSelect");
            _hub.ReleaseKeyboard();
            string currentScope = _scopeItems[_selectedScopeIndex].Id;
            Game1.activeClickableMenu = new AddMultiRuleInputMenu(_hub, currentScope);
            return true;
        }

        // ── 中栏交互与滑块检测 ──
        if (_midColRect.Contains(x, y))
        {
            int listTopY = _midColRect.Y + 44;
            int visibleCount = (_midColRect.Height - 48) / (RuleCardHeight + 8);
            bool hasMidScroll = _filteredRules.Count > visibleCount;

            if (hasMidScroll)
            {
                var trackRect = new Rectangle(_midColRect.Right - 9, listTopY, 5, _midColRect.Bottom - 8 - listTopY);
                if (trackRect.Contains(x, y))
                {
                    _isDraggingMidScrollbar = true;
                    int maxScroll = _filteredRules.Count - visibleCount;
                    float visibleRatio = Math.Clamp((float)visibleCount / _filteredRules.Count, 0.15f, 1f);
                    int thumbH = Math.Max(24, (int)(trackRect.Height * visibleRatio));
                    UpdateMidScrollFromMouse(y, trackRect, thumbH, maxScroll);
                    return true;
                }
            }

            int cardW = hasMidScroll ? _midColRect.Width - 22 : _midColRect.Width - 16;

            for (int i = 0; i < visibleCount && (_midScrollOffset + i) < _filteredRules.Count; i++)
            {
                int entryIdx = _midScrollOffset + i;
                var entry = _filteredRules[entryIdx];
                var cardRect = new Rectangle(_midColRect.X + 8, listTopY + i * (RuleCardHeight + 8), cardW, RuleCardHeight);

                int trashSize = 20;
                var trashRect = new Rectangle(cardRect.Right - trashSize - 10, cardRect.Bottom - trashSize - 8, trashSize, trashSize);
                if (trashRect.Contains(x, y))
                {
                    RequestDeleteConfirmation(entry);
                    return true;
                }

                if (cardRect.Contains(x, y))
                {
                    _selectedRuleIndex = entryIdx;
                    _activeEditingEntry = entry;
                    SyncEditorWithActiveEntry();
                    Game1.playSound("smallSelect");
                    return true;
                }
            }
            return true;
        }

        if (_rightColRect.Contains(x, y) && _activeEditingEntry != null)
        {
            if (_editInputBox != null)
            {
                if (_editInputBox.ContainsPoint(x, y))
                {
                    _editInputBox.Selected = true;
                    Game1.keyboardDispatcher.Subscriber = _editInputBox;
                    return true;
                }
                else
                {
                    _editInputBox.Selected = false;
                    if (Game1.keyboardDispatcher.Subscriber == _editInputBox)
                        Game1.keyboardDispatcher.Subscriber = null;
                }
            }

            if (_editFactCapsuleRect.Contains(x, y))
            {
                _editCategory = MemoryCategory.Fact;
                Game1.playSound("smallSelect");
                return true;
            }
            if (_editBehaviorCapsuleRect.Contains(x, y))
            {
                _editCategory = MemoryCategory.Behavior;
                Game1.playSound("smallSelect");
                return true;
            }

            if (_saveBtnRect.Contains(x, y))
            {
                CommitEdit();
                return true;
            }

            if (_deleteBtnRect.Contains(x, y))
            {
                RequestDeleteConfirmation(_activeEditingEntry);
                return true;
            }

            return true;
        }

        return false;
    }

    private void UpdateLeftScrollFromMouse(int mouseY, Rectangle trackRect, int thumbH, int maxScroll)
    {
        if (maxScroll <= 0 || trackRect.Height <= thumbH) return;
        float progress = Math.Clamp((float)(mouseY - trackRect.Y - thumbH / 2) / (trackRect.Height - thumbH), 0f, 1f);
        _leftScrollOffset = (int)Math.Round(progress * maxScroll);
    }

    private void UpdateMidScrollFromMouse(int mouseY, Rectangle trackRect, int thumbH, int maxScroll)
    {
        if (maxScroll <= 0 || trackRect.Height <= thumbH) return;
        float progress = Math.Clamp((float)(mouseY - trackRect.Y - thumbH / 2) / (trackRect.Height - thumbH), 0f, 1f);
        _midScrollOffset = (int)Math.Round(progress * maxScroll);
    }

    private void CommitEdit()
    {
        if (_activeEditingEntry == null || _editInputBox == null) return;

        string newText = _editInputBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(newText))
        {
            Game1.playSound("cancel");
            return;
        }

        var result = RuleManager.Instance.EditRule(_activeEditingEntry.Id, newText);
        if (result == MemoryOperationResult.Success)
        {
            _activeEditingEntry.Category = _editCategory;
            RuleManager.Instance.Save();
            Game1.playSound("coin");
            Game1.addHUDMessage(new HUDMessage("✔ 规则已更新保存", HUDMessage.newQuest_type));
            RefreshFromHub();
        }
        else if (result == MemoryOperationResult.Duplicate)
        {
            Game1.playSound("cancel");
            Game1.addHUDMessage(new HUDMessage(I18n.Memory.AddFailedDuplicate(), HUDMessage.error_type));
        }
    }

    private void SaveAndExit()
    {
        if (_activeEditingEntry != null && _editInputBox != null)
        {
            string newText = _editInputBox.Text.Trim();
            if (!string.IsNullOrWhiteSpace(newText))
            {
                var result = RuleManager.Instance.EditRule(_activeEditingEntry.Id, newText);
                if (result == MemoryOperationResult.Success)
                {
                    _activeEditingEntry.Category = _editCategory;
                }
            }
        }

        RuleManager.Instance.Save();
        Game1.playSound("coin");
        Game1.addHUDMessage(new HUDMessage("✔ 规则已保存", HUDMessage.newQuest_type));
        _hub.exitThisMenu();
    }

    private void RequestDeleteConfirmation(MemoryEntry entry)
    {
        string snippet = entry.Content.Length > 18
            ? entry.Content.Substring(0, 18) + "..."
            : entry.Content;

        Game1.activeClickableMenu = new ConfirmationDialog(
            $"确定删除此规则？\n“{snippet}”\n（删除后将自动存入规则归档箱）",
            _ =>
            {
                Game1.activeClickableMenu = _hub;
                RuleManager.Instance.RemoveRule(entry.Id);
                RuleArchiveManager.ArchiveRule(entry);
                RuleManager.Instance.Save();

                Game1.playSound("trashcan");
                if (_activeEditingEntry?.Id == entry.Id)
                {
                    _activeEditingEntry = null;
                    _selectedRuleIndex = -1;
                }
                RefreshFromHub();
            },
            _ => Game1.activeClickableMenu = _hub);
    }

    private void OpenArchiveMenu()
    {
        Game1.playSound("bigSelect");
        _hub.ReleaseKeyboard();
        string currentScope = _scopeItems[_selectedScopeIndex].Id;

        Game1.activeClickableMenu = new ArchivedMemoryMenu(
            currentScope,
            _hub,
            listSource: () => RuleArchiveManager.GetArchivedRules(currentScope),
            restoreAction: id =>
            {
                var result = RuleArchiveManager.RestoreRule(id);
                if (result == MemoryOperationResult.Success)
                {
                    RefreshFromHub();
                }
                return result;
            },
            deleteAction: id =>
            {
                bool ok = RuleArchiveManager.DeleteArchivedRule(id);
                RefreshFromHub();
                return ok;
            },
            capacity: RuleArchiveManager.MaxCapacity,
            titleSource: () => $"规则归档箱 · {(currentScope == "__ALL__" ? "全部作用域" : (currentScope == "WORLD" ? "小镇共识" : currentScope))}",
            emptySource: () => "归档箱内暂无已淘汰或删除的规则记录",
            ruleHintSource: () => $"规则被删除或过期后自动归档（上限 {RuleArchiveManager.MaxCapacity} 条，超出将自动清理最旧记录）",
            clearAction: () =>
            {
                int count = RuleArchiveManager.ClearArchivedRules(currentScope);
                RefreshFromHub();
                return count;
            }
        );
    }

    public bool ReceiveKeyPress(Keys key)
    {
        if (_editInputBox != null && Game1.keyboardDispatcher.Subscriber == _editInputBox)
        {
            if (key == Keys.Escape)
            {
                _editInputBox.Selected = false;
                Game1.keyboardDispatcher.Subscriber = null;
                return true;
            }

            if (key == Keys.S && (Keyboard.GetState().IsKeyDown(Keys.LeftControl) || Keyboard.GetState().IsKeyDown(Keys.RightControl)))
            {
                CommitEdit();
                return true;
            }

            return false;
        }
        return false;
    }

    public void LeftClickHeld(int x, int y)
    {
        if (_isDraggingLeftScrollbar)
        {
            int pinnedTopY = _leftColRect.Y + 4;
            int pinnedTotalH = PinnedCount * ScopeItemHeight + 3;
            int scrollAreaTopY = pinnedTopY + pinnedTotalH;
            int scrollAreaH = _leftColRect.Bottom - 4 - scrollAreaTopY;
            int scrollableCount = Math.Max(0, _scopeItems.Count - PinnedCount);
            int visibleMax = Math.Max(1, scrollAreaH / ScopeItemHeight);
            int maxScroll = Math.Max(0, scrollableCount - visibleMax);

            var trackRect = new Rectangle(_leftColRect.Right - 9, scrollAreaTopY, 5, scrollAreaH);
            float visibleRatio = Math.Clamp((float)visibleMax / scrollableCount, 0.15f, 1f);
            int thumbH = Math.Max(24, (int)(trackRect.Height * visibleRatio));
            UpdateLeftScrollFromMouse(y, trackRect, thumbH, maxScroll);
        }
        else if (_isDraggingMidScrollbar)
        {
            int visibleCount = Math.Max(1, (_midColRect.Height - 48) / (RuleCardHeight + 8));
            int maxScroll = Math.Max(0, _filteredRules.Count - visibleCount);
            int listTopY = _midColRect.Y + 44;
            var trackRect = new Rectangle(_midColRect.Right - 9, listTopY, 5, _midColRect.Bottom - 8 - listTopY);
            float visibleRatio = Math.Clamp((float)visibleCount / _filteredRules.Count, 0.15f, 1f);
            int thumbH = Math.Max(24, (int)(trackRect.Height * visibleRatio));
            UpdateMidScrollFromMouse(y, trackRect, thumbH, maxScroll);
        }
    }

    public void ReleaseLeftClick(int x, int y)
    {
        _isDraggingLeftScrollbar = false;
        _isDraggingMidScrollbar = false;
    }

    public void Draw(SpriteBatch b, int mx, int my)
    {
        HoveredTooltip = "";

        DrawLeftColumn(b, mx, my);
        DrawMiddleColumn(b, mx, my);
        DrawRightColumn(b, mx, my);

        // ── 底部操作区 ──
        DrawActionButton(b, _saveAndExitBtnRect, "✔ 保存并退出", mx, my, isPrimary: true);

        string curScope = _scopeItems[_selectedScopeIndex].Id;
        int archivedCount = RuleArchiveManager.GetArchivedRules(curScope).Count;
        DrawArchiveButtonWithBadge(b, _archiveBtnRect, "📦 归档箱", archivedCount, RuleArchiveManager.MaxCapacity, mx, my);
    }

    public void DrawOverlay(SpriteBatch b) { }

    private void DrawLeftColumn(SpriteBatch b, int mx, int my)
    {
        DrawSectionCard(b, _leftColRect);

        int pinnedTopY = _leftColRect.Y + 4;
        bool isMouseDown = Mouse.GetState().LeftButton == ButtonState.Pressed;
        int itemWNormal = _leftColRect.Width - 12;

        // ── 1. 渲染始终置顶悬浮项（全部作用域 + 小镇共识） ──
        for (int i = 0; i < Math.Min(PinnedCount, _scopeItems.Count); i++)
        {
            var item = _scopeItems[i];
            var itemRect = new Rectangle(_leftColRect.X + 6, pinnedTopY + i * ScopeItemHeight, itemWNormal, ScopeItemHeight - 4);
            DrawScopeCard(b, item, i, itemRect, isMouseDown, mx, my);
        }

        // ── 2. 置顶与可滚动区之间的精美过渡分割线 ──
        int pinnedTotalH = PinnedCount * ScopeItemHeight + 3;
        int dividerY = pinnedTopY + pinnedTotalH - 2;
        b.Draw(Game1.staminaRect, new Rectangle(_leftColRect.X + 8, dividerY, _leftColRect.Width - 16, 1), RulesTheme.BorderSoft * 0.85f);

        // ── 3. 渲染可独立滚动的 NPC 列表（释放全部垂直可用高度） ──
        int scrollAreaTopY = pinnedTopY + pinnedTotalH;
        int scrollAreaH = _leftColRect.Bottom - 4 - scrollAreaTopY;
        int scrollableCount = Math.Max(0, _scopeItems.Count - PinnedCount);
        int visibleMax = Math.Max(1, scrollAreaH / ScopeItemHeight);
        bool hasScrollbar = scrollableCount > visibleMax;

        int itemRightPad = hasScrollbar ? 15 : 6;
        int itemW = _leftColRect.Width - 6 - itemRightPad;

        for (int i = 0; i < visibleMax && (_leftScrollOffset + i) < scrollableCount; i++)
        {
            int itemIdx = PinnedCount + _leftScrollOffset + i;
            var item = _scopeItems[itemIdx];
            var itemRect = new Rectangle(_leftColRect.X + 6, scrollAreaTopY + i * ScopeItemHeight, itemW, ScopeItemHeight - 4);
            DrawScopeCard(b, item, itemIdx, itemRect, isMouseDown, mx, my);
        }

        // ── 4. 专属滚动条（只在下方 NPC 滚动区内显示） ──
        if (hasScrollbar)
        {
            var trackRect = new Rectangle(_leftColRect.Right - 9, scrollAreaTopY, 5, scrollAreaH);
            DrawScrollbarVisual(b, trackRect, visibleMax, scrollableCount, _leftScrollOffset, _isDraggingLeftScrollbar, mx, my);
        }
    }

    private void DrawScopeCard(SpriteBatch b, ScopeItem item, int itemIdx, Rectangle itemRect, bool isMouseDown, int mx, int my)
    {
        bool isSelected = _selectedScopeIndex == itemIdx;
        bool isHover = itemRect.Contains(mx, my);
        bool isPressed = isHover && isMouseDown;
        int pressOffset = isPressed ? 1 : 0;

        Color bg = isSelected ? RulesTheme.SurfaceActive
                 : isPressed ? RulesTheme.SurfaceSunken
                 : isHover ? RulesTheme.SurfaceHover
                 : RulesTheme.SurfaceCard;

        Color borderCol = isSelected ? RulesTheme.BorderBold
                        : isPressed ? RulesTheme.BorderBold
                        : isHover ? RulesTheme.BorderMid
                        : RulesTheme.BorderSoft;

        if (!isPressed)
        {
            b.Draw(Game1.staminaRect,
                new Rectangle(itemRect.X + 1, itemRect.Y + 2, itemRect.Width, itemRect.Height),
                RulesTheme.Shadow);
        }

        var drawRect = new Rectangle(itemRect.X, itemRect.Y + pressOffset, itemRect.Width, itemRect.Height);

        b.Draw(Game1.staminaRect, new Rectangle(drawRect.X + 1, drawRect.Y + 1, drawRect.Width - 2, drawRect.Height - 2), bg);
        IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(432, 439, 9, 9),
            drawRect.X, drawRect.Y, drawRect.Width, drawRect.Height, borderCol, 2f, false);

        if (isSelected)
        {
            b.Draw(Game1.staminaRect,
                new Rectangle(drawRect.X + 2, drawRect.Y + 3, 4, drawRect.Height - 6),
                RulesTheme.AccentGold);
        }

        // 微型头像框
        int avatarSize = 28;
        var avatarRect = new Rectangle(drawRect.X + 6, drawRect.Y + (drawRect.Height - avatarSize) / 2, avatarSize, avatarSize);

        b.Draw(Game1.staminaRect, avatarRect, RulesTheme.SurfaceSunken);
        IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(403, 383, 6, 6),
            avatarRect.X - 1, avatarRect.Y - 1, avatarRect.Width + 2, avatarRect.Height + 2,
            isSelected ? RulesTheme.BorderBold : (isHover ? RulesTheme.BorderMid : RulesTheme.BorderSoft), 1.2f, false);

        if (item.Sprite is { IsDisposed: false } sprite && !item.SourceRect.IsEmpty)
        {
            // NPC 头像
            b.Draw(sprite, avatarRect, item.SourceRect, Color.White);
        }
        else if (item.IconRect is Rectangle iconSrc && ModEntry.CustomIcons is { IsDisposed: false } icons)
        {
            // 作用域图标（全部规则 = 星星，小镇共识 = 小镇）——始终原色显示，不随选中/悬停变灰；
            // 图集失效（返回标题清理/读档重建间隙）时落入下方兜底符号，绝不空纹理绘制
            b.Draw(icons, avatarRect, iconSrc, Color.White);
        }
        else
        {
            // 兜底：未知项仍画符号
            string symbol = "❖";
            var symSz = CustomFontManager.MeasureString(symbol, CustomFontManager.SizeSmall);
            CustomFontManager.DrawString(b, symbol,
                new Vector2(avatarRect.X + (avatarSize - symSz.X) / 2f, avatarRect.Y + (avatarSize - symSz.Y) / 2f - 1),
                isSelected ? RulesTheme.AccentGold : RulesTheme.TextSecondary, CustomFontManager.SizeSmall);
        }

        // 人名文本：深炭黑（木炭黑），非生硬纯黑
        int textLeft = avatarRect.Right + 8;
        Color nameCol = isSelected ? RulesTheme.TextCharcoal
                     : (isHover ? RulesTheme.TextCharcoal : RulesTheme.TextDarkBrown);
        CustomFontManager.DrawString(b, item.DisplayName,
            new Vector2(textLeft, drawRect.Y + (drawRect.Height - 20) / 2f),
            nameCol, CustomFontManager.SizeRegular);

        // 规则数量小徽记
        int count = item.Id == "__ALL__"
            ? RuleManager.Instance.GetRules(null).Count
            : RuleManager.Instance.GetRuleCount(item.Id);

        string countStr = count.ToString();
        var countSz = CustomFontManager.MeasureString(countStr, CustomFontManager.SizeSmall);
        int badgeW = (int)Math.Max(20, countSz.X + 8);
        var badgeRect = new Rectangle(drawRect.Right - badgeW - 6, drawRect.Y + (drawRect.Height - 18) / 2, badgeW, 18);

        Color badgeBg = isSelected ? RulesTheme.AccentGold : RulesTheme.SurfaceSunken;
        b.Draw(Game1.staminaRect, badgeRect, badgeBg);
        IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(403, 383, 6, 6),
            badgeRect.X, badgeRect.Y, badgeRect.Width, badgeRect.Height,
            isSelected ? RulesTheme.BorderBold : RulesTheme.BorderSoft, 1.2f, false);

        CustomFontManager.DrawString(b, countStr,
            new Vector2(badgeRect.X + (badgeRect.Width - countSz.X) / 2f, badgeRect.Y + (badgeRect.Height - countSz.Y) / 2f - 1),
            isSelected ? RulesTheme.TextOnAccent : RulesTheme.TextSecondary, CustomFontManager.SizeSmall);

        if (isSelected)
        {
            string checkMark = "✔";
            var csz = CustomFontManager.MeasureStringBold(checkMark, CustomFontManager.SizeSmall);
            CustomFontManager.DrawStringBold(b, checkMark,
                new Vector2(badgeRect.X - csz.X - 5, drawRect.Y + (drawRect.Height - csz.Y) / 2f),
                RulesTheme.AccentGold, CustomFontManager.SizeSmall);
        }
    }

    private void DrawMiddleColumn(SpriteBatch b, int mx, int my)
    {
        DrawSectionCard(b, _midColRect);

        string scopeHeader = _scopeItems[_selectedScopeIndex].DisplayName;
        CustomFontManager.DrawString(b, $"{scopeHeader} ({_filteredRules.Count})",
            new Vector2(_midColRect.X + 12, _midColRect.Y + 12),
            RulesTheme.TextPrimary, CustomFontManager.SizeRegular);

        bool isMouseDown = Mouse.GetState().LeftButton == ButtonState.Pressed;
        bool addHover = _addBtnRect.Contains(mx, my);
        bool addPressed = addHover && isMouseDown;
        int addOffset = addPressed ? 1 : 0;

        Color addBg = addPressed ? RulesTheme.SurfaceSunken
                    : addHover ? RulesTheme.SurfaceHover
                    : RulesTheme.SurfaceActive;

        Color addBorder = addPressed ? RulesTheme.BorderBold
                        : addHover ? RulesTheme.BorderBold
                        : RulesTheme.BorderMid;

        if (!addPressed)
        {
            b.Draw(Game1.staminaRect,
                new Rectangle(_addBtnRect.X + 1, _addBtnRect.Y + 2, _addBtnRect.Width, _addBtnRect.Height),
                RulesTheme.Shadow);
        }

        var addDrawRect = new Rectangle(_addBtnRect.X, _addBtnRect.Y + addOffset, _addBtnRect.Width, _addBtnRect.Height);
        b.Draw(Game1.staminaRect, new Rectangle(addDrawRect.X + 1, addDrawRect.Y + 1, addDrawRect.Width - 2, addDrawRect.Height - 2), addBg);
        IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(432, 439, 9, 9),
            addDrawRect.X, addDrawRect.Y, addDrawRect.Width, addDrawRect.Height, addBorder, 2f, false);

        string addText = "+ 新增规则";
        var addSz = CustomFontManager.MeasureStringBold(addText, CustomFontManager.SizeSmall);
        CustomFontManager.DrawStringBold(b, addText,
            new Vector2(addDrawRect.X + (addDrawRect.Width - addSz.X) / 2f, addDrawRect.Y + (addDrawRect.Height - addSz.Y) / 2f),
            RulesTheme.TextPrimary, CustomFontManager.SizeSmall);

        int listTopY = _midColRect.Y + 44;
        int visibleCount = (_midColRect.Height - 48) / (RuleCardHeight + 8);
        bool hasMidScroll = _filteredRules.Count > visibleCount;

        if (_filteredRules.Count == 0)
        {
            string empty = "当前作用域下暂无规则\n点击右上角 “+ 新增规则” 创建";
            var esz = CustomFontManager.MeasureString(empty, CustomFontManager.SizeRegular);
            // 提示文字换用木炭黑 TextCharcoal
            CustomFontManager.DrawString(b, empty,
                new Vector2(_midColRect.X + (_midColRect.Width - esz.X) / 2f, _midColRect.Y + (_midColRect.Height - esz.Y) / 2f - 10),
                RulesTheme.TextCharcoal, CustomFontManager.SizeRegular);
        }

        int today = (int)Game1.Date.TotalDays;
        int cardW = hasMidScroll ? _midColRect.Width - 22 : _midColRect.Width - 16;

        for (int i = 0; i < visibleCount && (_midScrollOffset + i) < _filteredRules.Count; i++)
        {
            int entryIdx = _midScrollOffset + i;
            var entry = _filteredRules[entryIdx];
            var cardRect = new Rectangle(_midColRect.X + 8, listTopY + i * (RuleCardHeight + 8), cardW, RuleCardHeight);

            bool isSelected = _selectedRuleIndex == entryIdx;
            bool isHover = cardRect.Contains(mx, my);
            bool isCardPressed = isHover && isMouseDown;
            int cardOffset = isCardPressed ? 1 : 0;

            int trashSize = 20;
            var trashRect = new Rectangle(cardRect.Right - trashSize - 10, cardRect.Bottom - trashSize - 8, trashSize, trashSize);
            bool isTrashHover = trashRect.Contains(mx, my);
            bool isTrashPressed = isTrashHover && isMouseDown;
            if (isTrashHover)
            {
                HoveredTooltip = "删除此规则并归档";
            }

            if (!isCardPressed)
            {
                b.Draw(Game1.staminaRect,
                    new Rectangle(cardRect.X + 1, cardRect.Y + 2, cardRect.Width, cardRect.Height),
                    RulesTheme.Shadow);
            }

            var cardDrawRect = new Rectangle(cardRect.X, cardRect.Y + cardOffset, cardRect.Width, cardRect.Height);

            Color cardBg = isSelected ? RulesTheme.SurfaceActive
                         : isCardPressed ? RulesTheme.SurfaceSunken
                         : isHover ? RulesTheme.SurfaceHover
                         : RulesTheme.SurfaceCard;

            Color borderCol = isSelected ? RulesTheme.BorderBold
                            : isCardPressed ? RulesTheme.BorderBold
                            : isHover ? RulesTheme.BorderMid
                            : RulesTheme.BorderSoft;

            b.Draw(Game1.staminaRect, new Rectangle(cardDrawRect.X + 1, cardDrawRect.Y + 1, cardDrawRect.Width - 2, cardDrawRect.Height - 2), cardBg);
            IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(432, 439, 9, 9),
                cardDrawRect.X, cardDrawRect.Y, cardDrawRect.Width, cardDrawRect.Height, borderCol, 2f, false);

            if (isSelected)
            {
                b.Draw(Game1.staminaRect,
                    new Rectangle(cardDrawRect.X + 2, cardDrawRect.Y + 3, 4, cardDrawRect.Height - 6),
                    RulesTheme.AccentGold);
            }

            int curTagX = cardDrawRect.X + 12;
            int tagY = cardDrawRect.Y + 8;

            string scopeLabel = entry.NpcName == "WORLD" ? "小镇共识" : entry.NpcName;
            DrawMiniBadge(b, ref curTagX, tagY, scopeLabel,
                entry.NpcName == "WORLD" ? RulesTheme.AccentBlue : RulesTheme.TextSecondary,
                RulesTheme.SurfaceSunken);

            bool isBehavior = entry.Category == MemoryCategory.Behavior;
            DrawMiniBadge(b, ref curTagX, tagY, isBehavior ? "行为准则" : "既定事实",
                isBehavior ? RulesTheme.AccentAmber : RulesTheme.AccentGreen,
                RulesTheme.SurfaceSunken);

            int remaining = Math.Max(0, entry.ExpireDay - today);
            string durLabel = entry.ExpireDay < 0 ? "永久" : $"剩 {remaining} 天";
            Color durCol = entry.ExpireDay < 0 ? RulesTheme.TextSecondary
                : (remaining <= 1 ? RulesTheme.AccentRed
                    : RulesTheme.AccentAmber);

            var durSz = CustomFontManager.MeasureString(durLabel, CustomFontManager.SizeSmall);
            CustomFontManager.DrawString(b, durLabel, new Vector2(cardDrawRect.Right - durSz.X - 10, tagY + 1), durCol, CustomFontManager.SizeSmall);

            int textMaxW = cardDrawRect.Width - 24 - (trashSize + 12);
            string contentTrunc = UiHelper.TruncateString(entry.Content, Game1.smallFont, textMaxW);
            CustomFontManager.DrawString(b, contentTrunc, new Vector2(cardDrawRect.X + 12, cardDrawRect.Y + 34),
                RulesTheme.TextPrimary, CustomFontManager.SizeRegular);

            var trashDrawRect = new Rectangle(
                trashRect.X + (isTrashPressed ? 1 : 0),
                trashRect.Y + (isTrashPressed ? 1 : 0) + cardOffset,
                trashRect.Width,
                trashRect.Height);

            var trashSrc = IconSource.Trash(IconTheme.Wood, IconState.Normal);
            Color trashTint = isTrashPressed ? RulesTheme.AccentRed * 0.8f
                            : isTrashHover ? RulesTheme.AccentRed
                            : RulesTheme.TextMuted;
            if (ModEntry.CustomIcons is { IsDisposed: false } trashIcons)
                b.Draw(trashIcons, trashDrawRect, trashSrc, trashTint);
        }

        // ── 渲染中栏滑动块指示器 ──
        if (hasMidScroll)
        {
            var trackRect = new Rectangle(_midColRect.Right - 9, listTopY, 5, _midColRect.Bottom - 8 - listTopY);
            DrawScrollbarVisual(b, trackRect, visibleCount, _filteredRules.Count, _midScrollOffset, _isDraggingMidScrollbar, mx, my);
        }
    }

    private static void DrawScrollbarVisual(SpriteBatch b, Rectangle trackRect, int visibleCount, int totalCount, int scrollOffset, bool isDragging, int mx, int my)
    {
        if (totalCount <= visibleCount || trackRect.Height <= 0) return;

        b.Draw(Game1.staminaRect, trackRect, RulesTheme.SurfaceSunken);
        b.Draw(Game1.staminaRect, new Rectangle(trackRect.X, trackRect.Y, 1, trackRect.Height), RulesTheme.BorderSoft * 0.5f);

        int maxScroll = totalCount - visibleCount;
        float visibleRatio = Math.Clamp((float)visibleCount / totalCount, 0.15f, 1f);
        int thumbH = Math.Max(24, (int)(trackRect.Height * visibleRatio));
        int thumbY = trackRect.Y + (int)((trackRect.Height - thumbH) * ((float)scrollOffset / maxScroll));
        var thumbRect = new Rectangle(trackRect.X - 1, thumbY, trackRect.Width + 2, thumbH);

        bool thumbHover = thumbRect.Contains(mx, my);
        Color thumbBg = isDragging ? RulesTheme.BorderBold
                      : (thumbHover ? RulesTheme.AccentGold : RulesTheme.BorderMid);

        b.Draw(Game1.staminaRect, thumbRect, thumbBg);
        IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(403, 383, 6, 6),
            thumbRect.X, thumbRect.Y, thumbRect.Width, thumbRect.Height, RulesTheme.BorderBold, 1f, false);
    }

    private void DrawRightColumn(SpriteBatch b, int mx, int my)
    {
        DrawSectionCard(b, _rightColRect);

        if (_activeEditingEntry == null)
        {
            string hint = "← 在中栏选择一条规则\n进行查看或就地编辑";
            var hsz = CustomFontManager.MeasureString(hint, CustomFontManager.SizeRegular);
            // 提示文字换用木炭黑 TextCharcoal
            CustomFontManager.DrawString(b, hint,
                new Vector2(_rightColRect.X + (_rightColRect.Width - hsz.X) / 2f, _rightColRect.Y + (_rightColRect.Height - hsz.Y) / 2f - 20),
                RulesTheme.TextCharcoal, CustomFontManager.SizeRegular);
            return;
        }

        CustomFontManager.DrawStringBold(b, "规则检视与编辑",
            new Vector2(_rightColRect.X + 14, _rightColRect.Y + 12),
            RulesTheme.TextPrimary, CustomFontManager.SizeRegular);

        string scopeOwner = $"归属对象: {(_activeEditingEntry.NpcName == "WORLD" ? "小镇共识" : _activeEditingEntry.NpcName)}";
        CustomFontManager.DrawString(b, scopeOwner,
            new Vector2(_rightColRect.X + 14, _rightColRect.Y + 38),
            RulesTheme.TextSecondary, CustomFontManager.SizeSmall);

        _editInputBox?.Draw(b);

        DrawSegmentButton(b, _editFactCapsuleRect, "既定事实", _editCategory == MemoryCategory.Fact, mx, my);
        DrawSegmentButton(b, _editBehaviorCapsuleRect, "行为准则", _editCategory == MemoryCategory.Behavior, mx, my);

        DrawActionButton(b, _saveBtnRect, "✔ 保存修改", mx, my, isPrimary: true);
        DrawActionButton(b, _deleteBtnRect, "删除规则", mx, my, isDanger: true);
    }

    private static void DrawMiniBadge(SpriteBatch b, ref int curX, int y, string text, Color textCol, Color bgCol)
    {
        var sz = CustomFontManager.MeasureString(text, CustomFontManager.SizeSmall);
        var rect = new Rectangle(curX, y, (int)sz.X + 10, 20);
        b.Draw(Game1.staminaRect, rect, bgCol);
        IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(432, 439, 9, 9),
            rect.X, rect.Y, rect.Width, rect.Height, textCol * 0.45f, 1.5f, false);

        CustomFontManager.DrawString(b, text, new Vector2(rect.X + 5, rect.Y + 1), textCol, CustomFontManager.SizeSmall);
        curX += rect.Width + 6;
    }

    private static void DrawSectionCard(SpriteBatch b, Rectangle rect)
    {
        b.Draw(Game1.staminaRect, new Rectangle(rect.X + 1, rect.Y + 1, rect.Width - 2, rect.Height - 2),
            RulesTheme.SurfacePanel);
        IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(432, 439, 9, 9),
            rect.X, rect.Y, rect.Width, rect.Height, RulesTheme.BorderSoft, 2f, false);
    }

    private static void DrawSegmentButton(SpriteBatch b, Rectangle rect, string label, bool isActive, int mx, int my)
    {
        bool isHover = rect.Contains(mx, my);
        bool isPressed = isHover && Mouse.GetState().LeftButton == ButtonState.Pressed;
        int pressOffset = isPressed ? 1 : 0;

        Color bg = isActive
            ? (isPressed ? RulesTheme.SurfaceSunken : (isHover ? RulesTheme.SurfaceHover : RulesTheme.SurfaceActive))
            : (isPressed ? RulesTheme.SurfaceSunken : (isHover ? RulesTheme.SurfaceHover : RulesTheme.SurfaceCard));

        Color borderCol = isActive
            ? RulesTheme.BorderBold
            : (isPressed ? RulesTheme.BorderBold : (isHover ? RulesTheme.BorderMid : RulesTheme.BorderSoft));

        if (!isPressed && isActive)
        {
            b.Draw(Game1.staminaRect, new Rectangle(rect.X + 1, rect.Y + 2, rect.Width, rect.Height), RulesTheme.Shadow);
        }

        var drawRect = new Rectangle(rect.X, rect.Y + pressOffset, rect.Width, rect.Height);

        b.Draw(Game1.staminaRect, new Rectangle(drawRect.X + 1, drawRect.Y + 1, drawRect.Width - 2, drawRect.Height - 2), bg);
        IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(432, 439, 9, 9),
            drawRect.X, drawRect.Y, drawRect.Width, drawRect.Height, borderCol, 2f, false);

        var sz = CustomFontManager.MeasureString(label, CustomFontManager.SizeSmall);
        CustomFontManager.DrawString(b, label,
            new Vector2(drawRect.X + (drawRect.Width - sz.X) / 2f, drawRect.Y + (drawRect.Height - sz.Y) / 2f),
            isActive ? RulesTheme.TextPrimary : RulesTheme.TextSecondary, CustomFontManager.SizeSmall);
    }

    private static void DrawActionButton(SpriteBatch b, Rectangle rect, string label, int mx, int my,
        bool isPrimary = false, bool isDanger = false)
    {
        bool isHover = rect.Contains(mx, my);
        bool isPressed = isHover && Mouse.GetState().LeftButton == ButtonState.Pressed;
        int pressOffset = isPressed ? 1 : 0;

        Color bg;
        if (isDanger)
        {
            bg = isPressed ? RulesTheme.SurfaceSunken
               : isHover ? RulesTheme.SurfaceDangerHover
               : RulesTheme.SurfaceDanger;
        }
        else if (isPrimary)
        {
            bg = isPressed ? RulesTheme.SurfaceSunken
               : isHover ? RulesTheme.SurfaceHover
               : RulesTheme.SurfaceActive;
        }
        else
        {
            bg = isPressed ? RulesTheme.SurfaceSunken
               : isHover ? RulesTheme.SurfaceHover
               : RulesTheme.SurfaceCard;
        }

        Color borderCol = isDanger ? (isPressed ? RulesTheme.BorderBold : RulesTheme.AccentRed)
                        : isPrimary ? (isPressed ? RulesTheme.BorderBold : (isHover ? RulesTheme.BorderBold : RulesTheme.BorderMid))
                        : (isPressed ? RulesTheme.BorderBold : (isHover ? RulesTheme.BorderMid : RulesTheme.BorderSoft));

        if (!isPressed)
        {
            b.Draw(Game1.staminaRect, new Rectangle(rect.X + 1, rect.Y + 2, rect.Width, rect.Height), RulesTheme.Shadow);
        }

        var drawRect = new Rectangle(rect.X, rect.Y + pressOffset, rect.Width, rect.Height);

        b.Draw(Game1.staminaRect, new Rectangle(drawRect.X + 1, drawRect.Y + 1, drawRect.Width - 2, drawRect.Height - 2), bg);
        IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(432, 439, 9, 9),
            drawRect.X, drawRect.Y, drawRect.Width, drawRect.Height, borderCol, 2f, false);

        var sz = CustomFontManager.MeasureStringBold(label, CustomFontManager.SizeRegular);
        CustomFontManager.DrawStringBold(b, label,
            new Vector2(drawRect.X + (drawRect.Width - sz.X) / 2f, drawRect.Y + (drawRect.Height - sz.Y) / 2f),
            isDanger ? RulesTheme.AccentRed : RulesTheme.TextPrimary, CustomFontManager.SizeRegular);
    }

    private static void DrawArchiveButtonWithBadge(SpriteBatch b, Rectangle rect, string label, int count, int maxCapacity, int mx, int my)
    {
        bool isHover = rect.Contains(mx, my);
        bool isPressed = isHover && Mouse.GetState().LeftButton == ButtonState.Pressed;
        int pressOffset = isPressed ? 1 : 0;

        Color bg = isPressed ? RulesTheme.SurfaceSunken
                 : isHover ? RulesTheme.SurfaceHover
                 : RulesTheme.SurfaceCard;

        Color borderCol = isPressed ? RulesTheme.BorderBold
                        : isHover ? RulesTheme.BorderMid
                        : RulesTheme.BorderSoft;

        if (!isPressed)
        {
            b.Draw(Game1.staminaRect, new Rectangle(rect.X + 1, rect.Y + 2, rect.Width, rect.Height), RulesTheme.Shadow);
        }

        var drawRect = new Rectangle(rect.X, rect.Y + pressOffset, rect.Width, rect.Height);

        b.Draw(Game1.staminaRect, new Rectangle(drawRect.X + 1, drawRect.Y + 1, drawRect.Width - 2, drawRect.Height - 2), bg);
        IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(432, 439, 9, 9),
            drawRect.X, drawRect.Y, drawRect.Width, drawRect.Height, borderCol, 2f, false);

        var labelSz = CustomFontManager.MeasureStringBold(label, CustomFontManager.SizeSmall);
        string badgeText = count > 0 ? $"{count}" : "0";
        var badgeSz = CustomFontManager.MeasureString(badgeText, CustomFontManager.SizeSmall);
        int badgeW = (int)Math.Max(22, badgeSz.X + 8);
        int badgeH = 18;

        int totalContentW = (int)labelSz.X + 6 + badgeW;
        float startX = drawRect.X + (drawRect.Width - totalContentW) / 2f;

        CustomFontManager.DrawStringBold(b, label,
            new Vector2(startX, drawRect.Y + (drawRect.Height - labelSz.Y) / 2f),
            RulesTheme.TextPrimary, CustomFontManager.SizeSmall);

        var badgeRect = new Rectangle((int)(startX + labelSz.X + 6), drawRect.Y + (drawRect.Height - badgeH) / 2, badgeW, badgeH);
        Color badgeBg = count > 0 ? RulesTheme.SurfaceActive : RulesTheme.SurfaceSunken;
        Color badgeBorder = count > 0 ? RulesTheme.BorderBold : RulesTheme.BorderSoft;

        b.Draw(Game1.staminaRect, badgeRect, badgeBg);
        IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(403, 383, 6, 6),
            badgeRect.X, badgeRect.Y, badgeRect.Width, badgeRect.Height, badgeBorder, 1.2f, false);

        CustomFontManager.DrawString(b, badgeText,
            new Vector2(badgeRect.X + (badgeRect.Width - badgeSz.X) / 2f, badgeRect.Y + (badgeRect.Height - badgeSz.Y) / 2f - 1),
            count > 0 ? RulesTheme.TextPrimary : RulesTheme.TextSecondary, CustomFontManager.SizeSmall);
    }
}

/// <summary>
/// 规则工作台统一配色体系。
/// </summary>
internal static class RulesTheme
{
    // ── 文字（层次递进） ──
    public static readonly Color TextCharcoal  = new(42, 32, 24);   // 沉静深炭黑（木炭黑，人名主色/重要提示）
    public static readonly Color TextDarkBrown = new(58, 44, 32);   // 深茶褐（未选中常态）
    public static readonly Color TextPrimary   = new(58, 42, 30);
    public static readonly Color TextSecondary = new(112, 90, 72);
    public static readonly Color TextMuted     = new(152, 132, 114);
    public static readonly Color TextOnAccent  = new(255, 252, 244);

    // ── 表面（明度从深到浅）──
    public static readonly Color SurfaceSunken = new(240, 228, 206);
    public static readonly Color SurfacePanel  = new(248, 240, 226);
    public static readonly Color SurfaceCard   = new(254, 250, 240);
    public static readonly Color SurfaceHover  = new(255, 253, 246);
    public static readonly Color SurfaceActive = new(252, 238, 208);

    // ── 危险专用表面 ──
    public static readonly Color SurfaceDanger      = new(253, 244, 244);
    public static readonly Color SurfaceDangerHover = new(255, 234, 234);

    // ── 边框（三层层级）──
    public static readonly Color BorderSoft = new(224, 208, 184);
    public static readonly Color BorderMid  = new(202, 176, 140);
    public static readonly Color BorderBold = new(172, 122, 68);

    // ── 语义强调 ──
    public static readonly Color AccentGold  = new(196, 138, 42);
    public static readonly Color AccentRed   = new(180, 62, 62);
    public static readonly Color AccentGreen = new(62, 116, 78);
    public static readonly Color AccentBlue  = new(60, 96, 144);
    public static readonly Color AccentAmber = new(176, 106, 44);

    // ── 阴影 ──
    public static readonly Color Shadow = new Color(58, 42, 30) * 0.08f;
}

/// <summary>
/// 规则专用归档管理器（独立存档存储，与 TimelineChronicleMenu 彻底隔离）
/// </summary>
internal static class RuleArchiveManager
{
    public const int MaxCapacity = 100;
    private static List<MemoryEntry>? _cache;

    private static string GetFilePath()
    {
        try
        {
            if (Context.IsWorldReady && !string.IsNullOrEmpty(Constants.CurrentSavePath))
            {
                return System.IO.Path.Combine(Constants.CurrentSavePath, "rules_archive.json");
            }
        }
        catch { }

        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        return System.IO.Path.Combine(baseDir, "rules_archive.json");
    }

    public static List<MemoryEntry> GetAll()
    {
        if (_cache != null) return _cache;

        string path = GetFilePath();
        if (System.IO.File.Exists(path))
        {
            try
            {
                string json = System.IO.File.ReadAllText(path);
                _cache = Newtonsoft.Json.JsonConvert.DeserializeObject<List<MemoryEntry>>(json) ?? new List<MemoryEntry>();
                return _cache;
            }
            catch { }
        }

        _cache = new List<MemoryEntry>();
        return _cache;
    }

    public static void Save()
    {
        if (_cache == null) return;
        try
        {
            string path = GetFilePath();
            string? dir = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !System.IO.Directory.Exists(dir))
            {
                System.IO.Directory.CreateDirectory(dir);
            }

            string json = Newtonsoft.Json.JsonConvert.SerializeObject(_cache, Newtonsoft.Json.Formatting.Indented);
            System.IO.File.WriteAllText(path, json);
        }
        catch { }
    }

    public static IReadOnlyList<MemoryEntry> GetArchivedRules(string scope)
    {
        var all = GetAll();
        if (string.IsNullOrEmpty(scope) || scope == "__ALL__")
            return all.ToList();

        return all.Where(r => string.Equals(r.NpcName, scope, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    public static void ArchiveRule(MemoryEntry entry)
    {
        var list = GetAll();
        list.RemoveAll(r => r.Id == entry.Id);

        try
        {
            entry.ArchivedAt = DateTime.Now;
        }
        catch { }

        list.Insert(0, entry);

        while (list.Count > MaxCapacity)
        {
            list.RemoveAt(list.Count - 1);
        }

        Save();
    }

    public static MemoryOperationResult RestoreRule(string id)
    {
        var list = GetAll();
        var entry = list.FirstOrDefault(r => r.Id == id);
        if (entry == null) return MemoryOperationResult.NotFound;

        int duration = entry.ExpireDay < 0 ? -1 : Math.Max(1, entry.ExpireDay - (int)Game1.Date.TotalDays);
        if (entry.ExpireDay >= 0 && entry.ExpireDay < (int)Game1.Date.TotalDays)
        {
            duration = 1;
        }

        var result = RuleManager.Instance.AddRule(entry.NpcName, entry.Content, duration, entry.Category);
        if (result == MemoryOperationResult.Success)
        {
            list.Remove(entry);
            Save();
            RuleManager.Instance.Save();
        }
        return result;
    }

    public static bool DeleteArchivedRule(string id)
    {
        var list = GetAll();
        int removed = list.RemoveAll(r => r.Id == id);
        if (removed > 0)
        {
            Save();
            return true;
        }
        return false;
    }

    public static int ClearArchivedRules(string scope)
    {
        var list = GetAll();
        int count;
        if (string.IsNullOrEmpty(scope) || scope == "__ALL__")
        {
            count = list.Count;
            list.Clear();
        }
        else
        {
            count = list.RemoveAll(r => string.Equals(r.NpcName, scope, StringComparison.OrdinalIgnoreCase));
        }

        if (count > 0)
        {
            Save();
        }
        return count;
    }
}