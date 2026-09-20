using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewValley;
using StardewValley.Menus;
using System;
using System.Collections.Generic;
using System.Linq;
using ValleytalkReborn;

namespace ValleytalkReborn.UI;

/// <summary>
/// Tab0 NPC 记忆：含下拉选 NPC、称呼、AI 提炼、手动添加、归档箱等完整 Tab0 业务逻辑。
/// </summary>
internal sealed class NpcMemoryTabView : MemoryListTabViewBase
{
    private DropdownList _npcDropdown;
    private Rectangle _callsignRect;
    private Rectangle _aiExtractButtonRect;
    private Rectangle _manualAddRect;
    private Rectangle _archiveButtonRect;
    private int _archivedCount;

    public NpcMemoryTabView(IntegratedHubMenu hub) : base(hub)
    {
        _npcDropdown = new DropdownList(Rectangle.Empty)
        {
            HeaderPrefix = I18n.Hub.SelectNpcLabel(),
            OnItemSelected = name => SelectNpc(name)
        };

        BuildNpcDropdownItems();
    }

    protected override string EmptyHint => I18n.Memory.Empty();
    protected override int MaxEntries => MemoryManager.MaxMemoriesPerNpc;
    protected override int ListTopOffset => TabHeight + 8;

    protected override List<MemoryEntry> SafeGetEntries()
    {
        if (string.IsNullOrEmpty(Hub.CurrentNpcName))
            return new List<MemoryEntry>();

        var list = MemoryManager.Instance.GetMemories(Hub.CurrentNpcName);
        if (list == null)
            return new List<MemoryEntry>();

        return list
            .OrderByDescending(e => e.Category == MemoryCategory.Behavior)
            .ToList();
    }

    protected override void GetRowPresentation(MemoryEntry e, out string prefix, out Color color)
    {
        bool isRule = e.Category == MemoryCategory.Behavior;
        prefix = isRule ? I18n.Memory.RuleTag() : I18n.Memory.MemoryTag();
        color = e.Source == "Auto" ? new Color(130, 150, 170) : Game1.textColor;
    }

    protected override void DeleteEntryCommitted(MemoryEntry entry)
    {
        MemoryManager.Instance.ArchiveMemory(Hub.CurrentNpcName, entry, "ManualDeleted");
        MemoryManager.Instance.RemoveMemory(Hub.CurrentNpcName, entry.Id);
    }

    protected override IClickableMenu LaunchAddEditor()
    {
        return new AddMemoryInputMenu(Hub.CurrentNpcName, Hub, null, 0);
    }

    protected override void OnLayout(Rectangle menuBounds, Rectangle contentBounds)
    {
        int tabBaseX = menuBounds.X + LeftPadding;
        int totalTabSpace = contentBounds.Width;
        int halfW = (totalTabSpace - 16) / 2;

        var dropdownRect = new Rectangle(tabBaseX, menuBounds.Y + TopPadding, halfW, TabHeight);
        _callsignRect = new Rectangle(tabBaseX + halfW + 16, menuBounds.Y + TopPadding, halfW, TabHeight);
        _npcDropdown.SetHeaderBounds(dropdownRect);

        int btnY = menuBounds.Y + menuBounds.Height - 60;
        const int extractBtnW = 210;
        const int addBtnW = 210;
        const int archiveBtnW = 160;

        if (totalTabSpace >= extractBtnW + addBtnW + archiveBtnW + 20)
        {
            _aiExtractButtonRect = new Rectangle(menuBounds.X + LeftPadding, btnY, extractBtnW, 48);
            _manualAddRect = new Rectangle(menuBounds.X + (menuBounds.Width - addBtnW) / 2, btnY, addBtnW, 48);
            _archiveButtonRect = new Rectangle(menuBounds.X + menuBounds.Width - RightPadding - archiveBtnW, btnY, archiveBtnW, 48);
        }
        else
        {
            int gap = 8;
            int avail = totalTabSpace - gap * 2;
            int arcW = System.Math.Max(120, avail * 160 / 580);
            int rem = avail - arcW;
            int eachW = rem / 2;

            _aiExtractButtonRect = new Rectangle(menuBounds.X + LeftPadding, btnY, eachW, 48);
            _manualAddRect = new Rectangle(menuBounds.X + LeftPadding + eachW + gap, btnY, eachW, 48);
            _archiveButtonRect = new Rectangle(menuBounds.X + LeftPadding + eachW * 2 + gap * 2, btnY, arcW, 48);
        }
    }

    protected override bool OnReceiveLeftClick(int x, int y)
    {
        if (_npcDropdown.ReceiveLeftClick(x, y))
            return true;

        if (_npcDropdown.HeaderBounds.Contains(x, y))
        {
            _npcDropdown.ToggleOpen();
            Game1.playSound("shwip");
            return true;
        }

        if (_callsignRect.Contains(x, y) && !string.IsNullOrEmpty(Hub.CurrentNpcName))
        {
            Game1.playSound("bigSelect");
            Hub.ReleaseKeyboard();
            Game1.activeClickableMenu = new SetCallsignInputMenu(Hub.CurrentNpcName, Hub);
            return true;
        }

        if (_aiExtractButtonRect.Contains(x, y))
        {
            TryOpenDistillMenu();
            return true;
        }

        if (_manualAddRect.Contains(x, y))
        {
            OpenAddMemory();
            return true;
        }

        if (_archiveButtonRect.Contains(x, y))
        {
            if (string.IsNullOrEmpty(Hub.CurrentNpcName))
            {
                Game1.playSound("cancel");
                return true;
            }

            Game1.playSound("bigSelect");
            Hub.ReleaseKeyboard();
            Game1.activeClickableMenu = new ArchivedMemoryMenu(Hub.CurrentNpcName, Hub);
            return true;
        }

        return false;
    }

    protected override bool OnReceiveScrollWheel(int direction)
    {
        if (_npcDropdown != null && _npcDropdown.IsOpen)
        {
            _npcDropdown.ReceiveScrollWheel(direction);
            return true;
        }
        return false;
    }

    public override bool ReceiveKeyPress(Keys key)
    {
        if (key == Keys.Escape && _npcDropdown != null && _npcDropdown.IsOpen)
        {
            _npcDropdown.Close();
            return true;
        }
        return false;
    }

    public override void DrawOverlay(SpriteBatch b)
    {
        _npcDropdown.Draw(b);
    }

    protected override void OnRefreshFromHub()
    {
        _archivedCount = string.IsNullOrEmpty(Hub.CurrentNpcName)
            ? 0
            : MemoryManager.Instance.GetArchivedCount(Hub.CurrentNpcName);
    }

    protected override void OnDrawPre(SpriteBatch b, int mx, int my)
    {
        DrawCallsignButton(b, mx, my);
    }

    protected override void DrawBottomButtons(SpriteBatch b, int mx, int my)
    {
        // AI 提炼按钮
        string distillText = I18n.Memory.DistillButton();
        bool distillHover = _aiExtractButtonRect.Contains(mx, my);
        Color distillBg = distillHover ? new Color(255, 235, 205) : new Color(139, 90, 43);

        b.Draw(Game1.staminaRect, new Rectangle(_aiExtractButtonRect.X + 2, _aiExtractButtonRect.Y + 2, _aiExtractButtonRect.Width - 4, _aiExtractButtonRect.Height - 4), distillBg);
        IClickableMenu.drawTextureBox(b, Game1.mouseCursors,
            new Rectangle(432, 439, 9, 9),
            _aiExtractButtonRect.X, _aiExtractButtonRect.Y,
            _aiExtractButtonRect.Width, _aiExtractButtonRect.Height,
            distillBg, 4f, false);

        var distillLabelSize = CustomFontManager.MeasureStringBold(distillText, HubUi.TabFontSize);
        CustomFontManager.DrawStringBold(b, distillText,
            new Vector2(
                _aiExtractButtonRect.X + (_aiExtractButtonRect.Width - distillLabelSize.X) / 2f,
                _aiExtractButtonRect.Y + (_aiExtractButtonRect.Height - distillLabelSize.Y) / 2f),
            distillHover ? Game1.textColor : Color.White, HubUi.TabFontSize);

        // 手动添加按钮
        string manualText = I18n.Memory.AddButton();
        bool manualHover = _manualAddRect.Contains(mx, my);
        Color manualBg = manualHover ? new Color(255, 235, 205) : new Color(139, 90, 43);

        b.Draw(Game1.staminaRect, new Rectangle(_manualAddRect.X + 2, _manualAddRect.Y + 2, _manualAddRect.Width - 4, _manualAddRect.Height - 4), manualBg);
        IClickableMenu.drawTextureBox(b, Game1.mouseCursors,
            new Rectangle(432, 439, 9, 9),
            _manualAddRect.X, _manualAddRect.Y,
            _manualAddRect.Width, _manualAddRect.Height,
            manualBg, 4f, false);

        var manualLabelSize = CustomFontManager.MeasureStringBold(manualText, HubUi.TabFontSize);
        CustomFontManager.DrawStringBold(b, manualText,
            new Vector2(
                _manualAddRect.X + (_manualAddRect.Width - manualLabelSize.X) / 2f,
                _manualAddRect.Y + (_manualAddRect.Height - manualLabelSize.Y) / 2f),
            manualHover ? Game1.textColor : Color.White, HubUi.TabFontSize);

        // 归档箱按钮
        string archiveText = I18n.Memory.ArchiveButton(_archivedCount, MemoryManager.MaxArchivedMemoriesPerNpc);
        bool archiveHover = _archiveButtonRect.Contains(mx, my);
        Color archiveBg = archiveHover ? new Color(255, 235, 205) : new Color(139, 90, 43);

        b.Draw(Game1.staminaRect, new Rectangle(_archiveButtonRect.X + 2, _archiveButtonRect.Y + 2, _archiveButtonRect.Width - 4, _archiveButtonRect.Height - 4), archiveBg);
        IClickableMenu.drawTextureBox(b, Game1.mouseCursors,
            new Rectangle(432, 439, 9, 9),
            _archiveButtonRect.X, _archiveButtonRect.Y,
            _archiveButtonRect.Width, _archiveButtonRect.Height,
            archiveBg, 4f, false);

        var archiveLabelSize = CustomFontManager.MeasureStringBold(archiveText, HubUi.TabFontSize);
        CustomFontManager.DrawStringBold(b, archiveText,
            new Vector2(
                _archiveButtonRect.X + (_archiveButtonRect.Width - archiveLabelSize.X) / 2f,
                _archiveButtonRect.Y + (_archiveButtonRect.Height - archiveLabelSize.Y) / 2f),
            archiveHover ? Game1.textColor : Color.White, HubUi.TabFontSize);

        // 容量标签
        string cap = $"{MemoryManager.Instance.GetManualMemoryCount(Hub.CurrentNpcName)} / {MemoryManager.MaxMemoriesPerNpc}";
        CustomFontManager.DrawString(b, cap,
            new Vector2(MenuBounds.X + MenuBounds.Width - RightPadding - 120, ListTopY - 10),
            Color.Gray, HubUi.SmallFontSize);
    }

    private void DrawCallsignButton(SpriteBatch b, int mx, int my)
    {
        bool hover = _callsignRect.Contains(mx, my);
        string callsign = MemoryManager.Instance.GetCustomCallsign(Hub.CurrentNpcName);
        bool hasValue = !string.IsNullOrEmpty(callsign);

        Color bg = hover ? new Color(255, 235, 205) : new Color(210, 180, 140) * 0.8f;
        b.Draw(Game1.staminaRect, new Rectangle(_callsignRect.X + 2, _callsignRect.Y + 2, _callsignRect.Width - 4, _callsignRect.Height - 4), bg);
        IClickableMenu.drawTextureBox(b, Game1.mouseCursors,
            new Rectangle(432, 439, 9, 9),
            _callsignRect.X, _callsignRect.Y, _callsignRect.Width, _callsignRect.Height,
            bg, 4f, false);

        string prefix = I18n.Memory.CallsignPrefix();
        string valueText = hasValue ? $"[{callsign}]" : I18n.Memory.CallsignUnset();
        Color valueColor = hover ? Game1.textColor : (hasValue ? Game1.textColor * 0.9f : Color.Gray);
        string fullText = prefix + valueText;
        var textSize = CustomFontManager.MeasureStringBold(fullText, HubUi.TabFontSize);

        CustomFontManager.DrawStringBold(b, fullText,
            new Vector2(_callsignRect.X + (_callsignRect.Width - textSize.X) / 2f,
                        _callsignRect.Y + (_callsignRect.Height - textSize.Y) / 2f),
            valueColor, HubUi.TabFontSize);
    }

    private void SelectNpc(string internalName)
    {
        Hub.CurrentNpcName = internalName;
        StartIndex = 0;
        Game1.playSound("bigSelect");
        Hub.RefreshEntries();
        // F11: 不重建下拉项（原始 SelectNpc 无此调用）
    }

    private void BuildNpcDropdownItems()
    {
        var items = new List<(string Id, string Label)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (!string.IsNullOrWhiteSpace(Hub.CurrentNpcName))
        {
            string label = Game1.getCharacterFromName(Hub.CurrentNpcName)?.displayName ?? Hub.CurrentNpcName;
            items.Add((Hub.CurrentNpcName, label));
            seen.Add(Hub.CurrentNpcName);
        }

        string recent = DialogueHistoryManager.Instance.GetMostRecentNpc();
        if (!string.IsNullOrEmpty(recent) && seen.Add(recent))
        {
            string label = Game1.getCharacterFromName(recent)?.displayName ?? recent;
            items.Add((recent, label));
        }

        var remaining = Game1.player.friendshipData.Keys
            .Where(k => !seen.Contains(k))
            .OrderBy(k => k, StringComparer.OrdinalIgnoreCase);

        foreach (var k in remaining)
        {
            string label = Game1.getCharacterFromName(k)?.displayName ?? k;
            items.Add((k, label));
        }

        _npcDropdown.SetItems(items, Hub.CurrentNpcName);
    }

    protected override void OpenAddMemory()
    {
        if (string.IsNullOrEmpty(Hub.CurrentNpcName))
        {
            Game1.playSound("cancel");
            return;
        }

        int currentCount = MemoryManager.Instance.GetManualMemoryCount(Hub.CurrentNpcName);
        if (currentCount >= MaxEntries)
        {
            Game1.playSound("cancel");
            Game1.addHUDMessage(new HUDMessage(I18n.Memory.AddFailedFull(MaxEntries), 0));
            return;
        }

        Game1.playSound("bigSelect");
        Hub.ReleaseKeyboard();
        Game1.activeClickableMenu = new AddMemoryInputMenu(Hub.CurrentNpcName, Hub, null, 0);
    }

    private void TryOpenDistillMenu()
    {
        if (string.IsNullOrWhiteSpace(Hub.CurrentNpcName))
        {
            Game1.playSound("cancel");
            return;
        }

        string displayName = Game1.getCharacterFromName(Hub.CurrentNpcName)?.displayName ?? Hub.CurrentNpcName;

        if (DialogueBuilder.Instance?.LlmDisabled == true)
        {
            Game1.playSound("cancel");
            Game1.addHUDMessage(new HUDMessage(I18n.Memory.DistillLlmDisabled(), 3));
            return;
        }

        if (!DialogueHistoryManager.Instance.HasHistory(Hub.CurrentNpcName))
        {
            Game1.playSound("cancel");
            Game1.addHUDMessage(new HUDMessage(I18n.Memory.DistillNoHistory(displayName), 0));
            return;
        }

        Game1.playSound("bigSelect");
        Hub.ReleaseKeyboard();
        Game1.activeClickableMenu = new MemoryDistillMenu(Hub.CurrentNpcName, Hub);
    }
}
