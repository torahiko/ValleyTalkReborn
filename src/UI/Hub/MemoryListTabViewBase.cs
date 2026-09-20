using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewValley;
using StardewValley.Menus;
using System.Collections.Generic;
using ValleytalkReborn;

namespace ValleytalkReborn.UI;

/// <summary>
/// Tab0/1 共享的列表引擎基类。
/// 封装滚动、行按钮、编辑/删除、绘制等通用逻辑，子类通过抽象钩子提供差异行为。
/// </summary>
internal abstract class MemoryListTabViewBase : HubTabViewBase
{
    protected const int TabHeight = 36;
    protected const int LineHeight = 46;
    protected const int ButtonSize = 32;
    protected const int TopPadding = 110;
    protected const int BottomPadding = 75;
    protected const int LeftPadding = 40;
    protected const int RightPadding = 40;

    protected List<MemoryEntry> CachedEntries = new List<MemoryEntry>();
    protected int StartIndex;
    protected int ListTopY;
    protected bool Scrolling;

    protected readonly List<ClickableTextureComponent> DeleteButtons = new();
    protected readonly List<ClickableTextureComponent> EditButtons = new();

    protected ClickableTextureComponent UpArrow;
    protected ClickableTextureComponent DownArrow;
    protected ClickableTextureComponent Scrollbar;
    protected Rectangle ScrollbarRunner;

    protected float UpArrowHoverScale;
    protected float DownArrowHoverScale;
    protected readonly float UpArrowBaseScale;
    protected readonly float DownArrowBaseScale;

    protected MemoryListTabViewBase(IntegratedHubMenu hub) : base(hub)
    {
        UpArrow = new ClickableTextureComponent(
            new Rectangle(0, 0, 44, 48),
            Game1.mouseCursors, new Rectangle(421, 459, 11, 12), 4f);
        UpArrowBaseScale = 4f;

        DownArrow = new ClickableTextureComponent(
            new Rectangle(0, 0, 44, 48),
            Game1.mouseCursors, new Rectangle(421, 472, 11, 12), 4f);
        DownArrowBaseScale = 4f;

        ScrollbarRunner = new Rectangle(0, 0, 12, 100);
        Scrollbar = new ClickableTextureComponent(
            new Rectangle(0, 0, 24, 40),
            Game1.mouseCursors, new Rectangle(435, 463, 6, 10), 4f);
    }

    // ── 抽象钩子 ──
    protected abstract string EmptyHint { get; }
    protected abstract int MaxEntries { get; }
    protected abstract int ListTopOffset { get; }
    protected abstract void GetRowPresentation(MemoryEntry e, out string prefix, out Color color);
    protected abstract void DeleteEntryCommitted(MemoryEntry entry);
    protected abstract IClickableMenu LaunchAddEditor();
    protected abstract void DrawBottomButtons(SpriteBatch b, int mx, int my);
    protected abstract List<MemoryEntry> SafeGetEntries();

    public sealed override void OnActivated()
    {
        StartIndex = 0;
    }

    public sealed override void OnDeactivated() { }

    public sealed override void Layout(Rectangle menuBounds, Rectangle contentBounds)
    {
        base.Layout(menuBounds, contentBounds);

        ListTopY = menuBounds.Y + TopPadding + ListTopOffset;

        UpArrow.bounds.X = menuBounds.X + menuBounds.Width - 48;
        DownArrow.bounds.X = menuBounds.X + menuBounds.Width - 48;
        ScrollbarRunner.X = menuBounds.X + menuBounds.Width - 32;
        Scrollbar.bounds.X = ScrollbarRunner.X - 6;

        OnLayout(menuBounds, contentBounds);
    }

    /// <summary>子类覆写以进行额外布局（如 _addButtonRect）。</summary>
    protected virtual void OnLayout(Rectangle menuBounds, Rectangle contentBounds) { }

    public sealed override void Draw(SpriteBatch b, int mx, int my)
    {
        OnDrawPre(b, mx, my);
        DrawEntries(b, mx, my);
        DrawBottomButtons(b, mx, my);
        OnDrawPost(b, mx, my);
    }

    /// <summary>子类覆写以在 DrawEntries 之前绘制（如称呼按钮）。</summary>
    protected virtual void OnDrawPre(SpriteBatch b, int mx, int my) { }

    /// <summary>子类覆写以在 DrawBottomButtons 之后绘制。</summary>
    protected virtual void OnDrawPost(SpriteBatch b, int mx, int my) { }

    public sealed override bool ReceiveLeftClick(int x, int y)
    {
        return OnReceiveLeftClick(x, y) || HandleListRowClicks(x, y);
    }

    /// <summary>子类覆写以处理额外点击（如添加按钮）。返回 true 表示已消费。</summary>
    protected virtual bool OnReceiveLeftClick(int x, int y) => false;

    public sealed override bool ReceiveScrollWheel(int direction)
    {
        if (OnReceiveScrollWheel(direction))
            return true;

        var entries = CachedEntries;
        int maxLines = GetVisibleLineCount();

        if (direction > 0 && StartIndex > 0)
        {
            StartIndex--;
            Game1.playSound("shwip");
            SetScrollbarPosition();
            RefreshActionButtons();
            return true;
        }
        else if (direction < 0 && StartIndex < System.Math.Max(0, entries.Count - maxLines))
        {
            StartIndex++;
            Game1.playSound("shwip");
            SetScrollbarPosition();
            RefreshActionButtons();
            return true;
        }
        return false;
    }

    /// <summary>子类覆写以优先处理滚轮如下拉框。返回 true 表示已消费。</summary>
    protected virtual bool OnReceiveScrollWheel(int direction) => false;

    public sealed override void LeftClickHeld(int x, int y)
    {
        var entries = CachedEntries;
        int maxLines = GetVisibleLineCount();

        if (Scrolling && entries.Count > maxLines)
        {
            int yPos = System.Math.Max(ScrollbarRunner.Y,
                System.Math.Min(y, ScrollbarRunner.Bottom - Scrollbar.bounds.Height));
            float pct = (float)(yPos - ScrollbarRunner.Y) /
                        (ScrollbarRunner.Height - Scrollbar.bounds.Height);
            StartIndex = (int)(pct * (entries.Count - maxLines));
            SetScrollbarPosition();
            RefreshActionButtons();
        }
    }

    public sealed override void ReleaseLeftClick(int x, int y)
    {
        Scrolling = false;
    }

    public sealed override void RefreshFromHub()
    {
        CachedEntries = SafeGetEntries();
        ListTopY = MenuBounds.Y + TopPadding + ListTopOffset;
        OnRefreshFromHub();
        ClampStartIndex();
        RefreshActionButtons();
        PositionScrollComponents();
    }

    /// <summary>子类覆写以在 RefreshFromHub 中添加自定义逻辑（如刷新 _archivedCount）。</summary>
    protected virtual void OnRefreshFromHub() { }

    // ── 内部方法 ──

    protected bool HandleListRowClicks(int x, int y)
    {
        var entries = CachedEntries;

        for (int i = 0; i < DeleteButtons.Count; i++)
        {
            int idx = StartIndex + i;
            if (idx >= entries.Count) continue;

            if (DeleteButtons[i].containsPoint(x, y))
            {
                ConfirmDeleteMemory(entries[idx]);
                return true;
            }

            if (i < EditButtons.Count && EditButtons[i].containsPoint(x, y))
            {
                Game1.playSound("bigSelect");
                OpenEditMemory(entries[idx]);
                return true;
            }
        }

        int maxLines = GetVisibleLineCount();
        if (entries.Count <= maxLines) return false;

        if (UpArrow.containsPoint(x, y) && StartIndex > 0)
        {
            StartIndex--;
            Game1.playSound("shwip");
            SetScrollbarPosition();
            RefreshActionButtons();
            return true;
        }
        else if (DownArrow.containsPoint(x, y) && StartIndex < entries.Count - maxLines)
        {
            StartIndex++;
            Game1.playSound("shwip");
            SetScrollbarPosition();
            RefreshActionButtons();
            return true;
        }
        else if (ScrollbarRunner.Contains(x, y) || Scrollbar.containsPoint(x, y))
        {
            Scrolling = true;
            return true;
        }

        return false;
    }

    protected void OpenEditMemory(MemoryEntry e)
    {
        Hub.ReleaseKeyboard();
        Game1.activeClickableMenu = new AddMemoryInputMenu(Hub.CurrentNpcName, Hub, e, Hub.CurrentTab);
    }

    protected void ConfirmDeleteMemory(MemoryEntry entry)
    {
        string safeContent = CustomFontManager.TruncateString(entry.Content, HubUi.RegularFontSize, 320f);

        Game1.activeClickableMenu = new ConfirmationDialog(
            I18n.Memory.DeleteConfirm(safeContent),
            _ =>
            {
                DeleteEntryCommitted(entry);
                Game1.playSound("trashcan");
                Hub.RefreshEntries();
                Game1.activeClickableMenu = Hub;
            },
            _ =>
            {
                Game1.activeClickableMenu = Hub;
            });
    }

    protected virtual void OpenAddMemory()
    {
        if (Hub.CurrentTab == 0 && string.IsNullOrEmpty(Hub.CurrentNpcName))
        {
            Game1.playSound("cancel");
            return;
        }

        int currentCount = CachedEntries.Count;
        if (currentCount >= MaxEntries)
        {
            Game1.playSound("cancel");
            Game1.addHUDMessage(new HUDMessage(I18n.Memory.AddFailedFull(MaxEntries), 0));
            return;
        }

        Game1.playSound("bigSelect");
        Hub.ReleaseKeyboard();
        Game1.activeClickableMenu = LaunchAddEditor();
    }

    protected void DrawEntries(SpriteBatch b, int mx, int my)
    {
        var entries = CachedEntries;
        int visibleCount = GetVisibleLineCount();

        if (entries.Count == 0)
        {
            var hintSize = CustomFontManager.MeasureString(EmptyHint, HubUi.RegularFontSize);
            CustomFontManager.DrawString(b, EmptyHint,
                new Vector2(MenuBounds.X + (MenuBounds.Width - hintSize.X) / 2f, ListTopY + 60),
                Color.Gray, HubUi.RegularFontSize);
            return;
        }

        float fixedDateWidth = CustomFontManager.MeasureString("2026-12-31 00:00", HubUi.SmallFontSize).X;
        float dateX = MenuBounds.X + MenuBounds.Width - RightPadding - 85 - fixedDateWidth;
        float contentStartX = MenuBounds.X + LeftPadding;
        float maxContentWidth = (dateX - 16) - contentStartX;

        for (int i = 0; i < visibleCount && StartIndex + i < entries.Count; i++)
        {
            int idx = StartIndex + i;
            var entry = entries[idx];
            int rowY = ListTopY + 10 + i * LineHeight;

            var rowRect = new Rectangle(
                MenuBounds.X + LeftPadding - 16, rowY - 4,
                MenuBounds.Width - LeftPadding - RightPadding + 16, LineHeight - 2);

            if (rowRect.Contains(mx, my))
                b.Draw(Game1.staminaRect, rowRect, new Color(70, 130, 180) * 0.18f);

            GetRowPresentation(entry, out string prefix, out Color textColor);

            string fullRawText = $"{idx + 1}. {prefix}{entry.Content}";
            string text = CustomFontManager.TruncateString(fullRawText, HubUi.RegularFontSize, maxContentWidth);
            string dateText = entry.CreatedAt.ToString("yyyy-MM-dd HH:mm");

            CustomFontManager.DrawString(b, text,
                new Vector2(contentStartX, rowY + 4),
                textColor, HubUi.RegularFontSize);

            CustomFontManager.DrawString(b, dateText,
                new Vector2(dateX, rowY + 6),
                Color.Gray, HubUi.SmallFontSize);

            bool isLeftMouseDown = Mouse.GetState().LeftButton == ButtonState.Pressed;

            if (i < EditButtons.Count)
            {
                var btn = EditButtons[i];
                bool isPressed = isLeftMouseDown && btn.containsPoint(mx, my);
                IconSource.DrawButton(b, btn, isPressed);
            }

            if (i < DeleteButtons.Count)
            {
                var btn = DeleteButtons[i];
                bool isPressed = isLeftMouseDown && btn.containsPoint(mx, my);
                IconSource.DrawButton(b, btn, isPressed);
            }
        }

        if (entries.Count > visibleCount)
        {
            UiHelper.UpdateButtonScale(ref UpArrowHoverScale, UpArrow, mx, my);
            UiHelper.UpdateButtonScale(ref DownArrowHoverScale, DownArrow, mx, my);
            UpArrow.scale = UpArrowBaseScale * UpArrowHoverScale;
            DownArrow.scale = DownArrowBaseScale * DownArrowHoverScale;
            UpArrow.draw(b);
            DownArrow.draw(b);

            IClickableMenu.drawTextureBox(b, Game1.mouseCursors,
                new Rectangle(403, 383, 6, 6),
                ScrollbarRunner.X, ScrollbarRunner.Y,
                ScrollbarRunner.Width, ScrollbarRunner.Height,
                Color.White, 4f, false);

            SetScrollbarPosition();
            Scrollbar.draw(b);
        }
    }

    protected void RefreshActionButtons()
    {
        ClampStartIndex();
        DeleteButtons.Clear();
        EditButtons.Clear();

        var entries = CachedEntries;
        int visibleCount = GetVisibleLineCount();

        for (int i = 0; i < visibleCount && StartIndex + i < entries.Count; i++)
        {
            int y = ListTopY + 10 + i * LineHeight + 7;

            var del = new ClickableTextureComponent(
                new Rectangle(MenuBounds.X + MenuBounds.Width - RightPadding - 32, y, ButtonSize, ButtonSize),
                ModEntry.CustomIcons,
                IconSource.Trash(IconTheme.Wood, IconState.Normal),
                2f)
            {
                hoverText = I18n.Memory.DeleteButtonHover()
            };
            DeleteButtons.Add(del);

            var edit = new ClickableTextureComponent(
                new Rectangle(MenuBounds.X + MenuBounds.Width - RightPadding - 72, y, ButtonSize, ButtonSize),
                ModEntry.CustomIcons,
                IconSource.Edit(IconTheme.Wood, IconState.Normal),
                2f)
            {
                hoverText = I18n.Memory.EditButtonHover()
            };
            EditButtons.Add(edit);
        }
    }

    protected void ClampStartIndex()
    {
        var entries = CachedEntries;
        int maxLines = GetVisibleLineCount();
        int maxStart = System.Math.Max(0, entries.Count - maxLines);
        StartIndex = System.Math.Clamp(StartIndex, 0, maxStart);
    }

    protected int GetVisibleLineCount()
        => (MenuBounds.Height - BottomPadding - ListTopY + MenuBounds.Y) / LineHeight;

    protected void PositionScrollComponents()
    {
        int top = ListTopY;
        UpArrow.bounds.Y = top;
        DownArrow.bounds.Y = MenuBounds.Y + MenuBounds.Height - BottomPadding;
        ScrollbarRunner.Y = top + 50;
        ScrollbarRunner.Height = (MenuBounds.Y + MenuBounds.Height - BottomPadding) - (top + 50);
        if (ScrollbarRunner.Height < 0) ScrollbarRunner.Height = 0;
        Scrollbar.bounds.Y = ScrollbarRunner.Y;
        SetScrollbarPosition();
    }

    protected void SetScrollbarPosition()
    {
        var entries = CachedEntries;
        int maxLines = GetVisibleLineCount();
        if (entries.Count <= maxLines) return;

        float pct = (float)StartIndex / (entries.Count - maxLines);
        Scrollbar.bounds.Y = ScrollbarRunner.Y +
            (int)(pct * (ScrollbarRunner.Height - Scrollbar.bounds.Height));
    }
}
