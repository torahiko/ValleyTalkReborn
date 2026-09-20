using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;
using StardewValley.Menus;
using System;
using System.Collections.Generic;
using System.Linq;
using ValleytalkReborn;
using ValleytalkReborn.Services;

namespace ValleytalkReborn.UI;

/// <summary>
/// Tab0 统一规则页（RULE-MERGE）：合并原 NPC 记忆 + 世界记忆为单一规则列表。
/// 支持 scope 过滤（全部/全局/单NPC），行右侧倒计时胶囊。
/// </summary>
internal sealed class RulesTabView : MemoryListTabViewBase
{
    // scope 过滤器：0=全部 1=全局 2=当前NPC
    private int _scopeFilter;
    private readonly List<Rectangle> _scopeSegRects = new();

    private Rectangle _addButtonRect;
    private Rectangle _scopeBarRect;

    private const int ScopeBarHeight = 34;
    private const int ScopeBarPadding = 4;

    public RulesTabView(IntegratedHubMenu hub) : base(hub) { }

    protected override string EmptyHint => I18n.Memory.Empty();
    protected override int MaxEntries => RuleManager.MaxRulesPerScope;
    protected override int ListTopOffset => ScopeBarHeight + 16;

    protected override List<MemoryEntry> SafeGetEntries()
    {
        var all = RuleManager.Instance.GetRules(null);
        if (all == null || all.Count == 0)
            return new List<MemoryEntry>();

        if (_scopeFilter == 0)
            return all;

        string scope = _scopeFilter == 1 ? "WORLD" : (Hub.CurrentNpcName ?? "");
        return all
            .Where(e => string.Equals(e.NpcName, scope, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    protected override void GetRowPresentation(MemoryEntry e, out string prefix, out Color color)
    {
        bool isWorld = string.Equals(e.NpcName, "WORLD", StringComparison.OrdinalIgnoreCase);
        bool isRule = e.Category == MemoryCategory.Behavior;

        if (isWorld)
            prefix = I18n.Memory.WorldScopeTag();
        else
            prefix = isRule ? I18n.Memory.RuleTag() : I18n.Memory.MemoryTag();

        color = isRule ? new Color(180, 80, 80) : Game1.textColor;
    }

    protected override void DeleteEntryCommitted(MemoryEntry entry)
    {
        RuleManager.Instance.RemoveRule(entry.Id);
    }

    protected override IClickableMenu LaunchAddEditor()
    {
        string defaultScope = _scopeFilter == 1 ? "WORLD"
            : (_scopeFilter == 2 ? (Hub.CurrentNpcName ?? "") : "WORLD");
        return new AddRuleInputMenu(defaultScope, Hub, null, true);
    }

    protected override void OnLayout(Rectangle menuBounds, Rectangle contentBounds)
    {
        int scopeBarY = menuBounds.Y + TopPadding + 2;
        _scopeBarRect = new Rectangle(menuBounds.X + LeftPadding, scopeBarY,
            contentBounds.Width, ScopeBarHeight);

        int segCount = 3;
        int totalSegW = contentBounds.Width - ScopeBarPadding * (segCount - 1);
        int segW = totalSegW / segCount;
        _scopeSegRects.Clear();
        for (int i = 0; i < segCount; i++)
        {
            _scopeSegRects.Add(new Rectangle(
                _scopeBarRect.X + i * (segW + ScopeBarPadding),
                _scopeBarRect.Y, segW, ScopeBarHeight));
        }

        int btnY = menuBounds.Y + menuBounds.Height - 60;
        int addBtnW = Math.Min(240, contentBounds.Width);
        _addButtonRect = new Rectangle(
            menuBounds.X + (menuBounds.Width - addBtnW) / 2, btnY, addBtnW, 48);
    }

    protected override bool OnReceiveLeftClick(int x, int y)
    {
        for (int i = 0; i < _scopeSegRects.Count; i++)
        {
            if (_scopeSegRects[i].Contains(x, y))
            {
                if (_scopeFilter != i)
                {
                    _scopeFilter = i;
                    Game1.playSound("smallSelect");
                    Hub.RefreshEntries();
                }
                return true;
            }
        }

        if (_addButtonRect.Contains(x, y))
        {
            OpenAddRule();
            return true;
        }

        return false;
    }

    private void OpenAddRule()
    {
        if (_scopeFilter == 2 && string.IsNullOrEmpty(Hub.CurrentNpcName))
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

    protected override void OnDrawPre(SpriteBatch b, int mx, int my)
    {
        DrawScopeBar(b, mx, my);
    }

    protected override void DrawBottomButtons(SpriteBatch b, int mx, int my)
    {
        bool addHover = _addButtonRect.Contains(mx, my);
        Color addBg = addHover ? new Color(255, 235, 205) : new Color(139, 90, 43);

        b.Draw(Game1.staminaRect, new Rectangle(_addButtonRect.X + 2, _addButtonRect.Y + 2, _addButtonRect.Width - 4, _addButtonRect.Height - 4), addBg);
        IClickableMenu.drawTextureBox(b, Game1.mouseCursors,
            new Rectangle(432, 439, 9, 9),
            _addButtonRect.X, _addButtonRect.Y,
            _addButtonRect.Width, _addButtonRect.Height,
            addBg, 4f, false);

        string addText = I18n.Memory.AddButton();
        var addLabelSize = CustomFontManager.MeasureStringBold(addText, HubUi.TabFontSize);
        CustomFontManager.DrawStringBold(b, addText,
            new Vector2(
                _addButtonRect.X + (_addButtonRect.Width - addLabelSize.X) / 2f,
                _addButtonRect.Y + (_addButtonRect.Height - addLabelSize.Y) / 2f),
            addHover ? Game1.textColor : Color.White, HubUi.TabFontSize);

        string cap = $"{CachedEntries.Count} / {MaxEntries}";
        CustomFontManager.DrawString(b, cap,
            new Vector2(MenuBounds.X + MenuBounds.Width - RightPadding - 120, ListTopY - 10),
            Color.Gray, HubUi.SmallFontSize);
    }

    protected override void OnDrawPost(SpriteBatch b, int mx, int my)
    {
        DrawCountdownCapsules(b);
    }

    private void DrawScopeBar(SpriteBatch b, int mx, int my)
    {
        string[] labels = { I18n.Hub.ScopeAll(), I18n.Hub.ScopeGlobal(), I18n.Hub.ScopeNpc() };
        for (int i = 0; i < _scopeSegRects.Count; i++)
        {
            var rect = _scopeSegRects[i];
            bool isActive = _scopeFilter == i;
            bool isHover = rect.Contains(mx, my);

            Color bg = isActive ? new Color(210, 180, 140)
                     : isHover ? new Color(255, 235, 205)
                     : new Color(139, 90, 43);

            b.Draw(Game1.staminaRect, new Rectangle(rect.X + 2, rect.Y + 2, rect.Width - 4, rect.Height - 4), bg);
            IClickableMenu.drawTextureBox(b, Game1.mouseCursors,
                new Rectangle(432, 439, 9, 9),
                rect.X, rect.Y, rect.Width, rect.Height, bg, 4f, false);

            var labelSize = CustomFontManager.MeasureStringBold(labels[i], HubUi.TabFontSize);
            CustomFontManager.DrawStringBold(b, labels[i],
                new Vector2(
                    rect.X + (rect.Width - labelSize.X) / 2f,
                    rect.Y + (rect.Height - labelSize.Y) / 2f),
                isActive ? Game1.textColor : Color.White * 0.95f, HubUi.TabFontSize);
        }
    }

    private void DrawCountdownCapsules(SpriteBatch b)
    {
        int today = (int)Game1.Date.TotalDays;
        var entries = CachedEntries;
        int visibleCount = GetVisibleLineCount();

        for (int i = 0; i < visibleCount && StartIndex + i < entries.Count; i++)
        {
            var entry = entries[StartIndex + i];
            int rowY = ListTopY + 10 + i * LineHeight + 4;

            string label;
            Color bg;
            if (entry.ExpireDay < 0)
            {
                label = I18n.Memory.CapsulePermanent();
                bg = new Color(120, 120, 120);
            }
            else
            {
                int remain = entry.ExpireDay - today;
                if (remain <= 0)
                {
                    label = I18n.Memory.CapsuleExpired();
                    bg = new Color(200, 160, 60);
                }
                else if (remain == 1)
                {
                    label = I18n.Memory.CapsuleDaysLeft(1);
                    bg = new Color(220, 170, 50);
                }
                else
                {
                    label = I18n.Memory.CapsuleDaysLeft(remain);
                    bg = new Color(100, 140, 180);
                }
            }

            var sz = CustomFontManager.MeasureStringBold(label, HubUi.SmallFontSize);
            int capsuleW = (int)sz.X + 16;
            int capsuleH = 24;
            int capsuleX = MenuBounds.X + MenuBounds.Width - RightPadding - 72 - capsuleW - 8;
            int capsuleY = rowY;

            b.Draw(Game1.staminaRect, new Rectangle(capsuleX + 2, capsuleY + 2, capsuleW - 4, capsuleH - 4), bg * 0.85f);
            IClickableMenu.drawTextureBox(b, Game1.mouseCursors,
                new Rectangle(403, 383, 6, 6),
                capsuleX, capsuleY, capsuleW, capsuleH,
                Color.White * 0.9f, 2f, false);

            CustomFontManager.DrawStringBold(b, label,
                new Vector2(capsuleX + (capsuleW - sz.X) / 2f, capsuleY + 4),
                Color.White, HubUi.SmallFontSize);
        }
    }
}
