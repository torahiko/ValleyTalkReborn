using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;
using StardewValley.Menus;
using System.Collections.Generic;

namespace ValleytalkReborn.UI;

/// <summary>
/// Tab1 世界记忆：薄子类，实现 MemoryListTabViewBase 的抽象钩子。
/// </summary>
internal sealed class WorldMemoryTabView : MemoryListTabViewBase
{
    private Rectangle _addButtonRect;

    public WorldMemoryTabView(IntegratedHubMenu hub) : base(hub) { }

    protected override string EmptyHint => I18n.Memory.WorldEmpty();
    protected override int MaxEntries => WorldMemoryManager.MaxEntries;
    protected override int ListTopOffset => 0;

    protected override List<MemoryEntry> SafeGetEntries()
    {
        var list = WorldMemoryManager.Instance.GetEntries();
        return list ?? new List<MemoryEntry>();
    }

    protected override void GetRowPresentation(MemoryEntry e, out string prefix, out Color color)
    {
        bool isAuto = e.Source == "Auto";
        prefix = isAuto ? I18n.Memory.AutoPrefix() : "";
        color = isAuto ? new Color(120, 140, 160) : Game1.textColor;
    }

    protected override void DeleteEntryCommitted(MemoryEntry entry)
    {
        WorldMemoryManager.Instance.RemoveEntry(entry.Id);
    }

    protected override IClickableMenu LaunchAddEditor()
    {
        return new AddMemoryInputMenu(Hub.CurrentNpcName, Hub, null, 1);
    }

    protected override void OnLayout(Rectangle menuBounds, Rectangle contentBounds)
    {
        int totalTabSpace = contentBounds.Width;
        int singleBtnW = System.Math.Min(300, totalTabSpace);
        int btnY = menuBounds.Y + menuBounds.Height - 60;
        _addButtonRect = new Rectangle(menuBounds.X + (menuBounds.Width - singleBtnW) / 2, btnY, singleBtnW, 48);
    }

    protected override bool OnReceiveLeftClick(int x, int y)
    {
        if (_addButtonRect.Contains(x, y))
        {
            OpenAddMemory();
            return true;
        }
        return false;
    }

    protected override void DrawBottomButtons(SpriteBatch b, int mx, int my)
    {
        // 添加按钮
        bool addHover = _addButtonRect.Contains(mx, my);
        Color addBg = addHover ? new Color(255, 235, 205) : new Color(139, 90, 43);

        b.Draw(Game1.staminaRect, new Rectangle(_addButtonRect.X + 2, _addButtonRect.Y + 2, _addButtonRect.Width - 4, _addButtonRect.Height - 4), addBg);
        IClickableMenu.drawTextureBox(b, Game1.mouseCursors,
            new Rectangle(432, 439, 9, 9),
            _addButtonRect.X, _addButtonRect.Y,
            _addButtonRect.Width, _addButtonRect.Height,
            addBg, 4f, false);

        string addText = I18n.Memory.AddWorldButton();
        var addLabelSize = CustomFontManager.MeasureStringBold(addText, HubUi.TabFontSize);
        CustomFontManager.DrawStringBold(b, addText,
            new Vector2(
                _addButtonRect.X + (_addButtonRect.Width - addLabelSize.X) / 2f,
                _addButtonRect.Y + (_addButtonRect.Height - addLabelSize.Y) / 2f),
            addHover ? Game1.textColor : Color.White, HubUi.TabFontSize);

        // 容量标签
        string cap = $"{CachedEntries.Count} / {MaxEntries}";
        CustomFontManager.DrawString(b, cap,
            new Vector2(MenuBounds.X + MenuBounds.Width - RightPadding - 120, ListTopY - 10),
            Color.Gray, HubUi.SmallFontSize);
    }
}
