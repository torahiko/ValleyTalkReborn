using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;

namespace ValleytalkReborn.UI;

/// <summary>
/// Tab3 高级设置页：四项布尔开关 + 底部免责声明。
/// 域、点击、绘制均从原 IntegratedHubMenu 原样搬移。
/// </summary>
internal sealed class AdvancedSettingsTabView : HubTabViewBase
{
    // 布局常量（与 Hub 原 LeftPadding/RightPadding/TopPadding/BottomPadding 值相同）
    private const int LeftPadding = 40;
    private const int RightPadding = 40;
    private const int TopPadding = 110;
    private const int BottomPadding = 75;

    // 行矩形（Layout 中计算）
    private Rectangle _tab3RowInfinite;
    private Rectangle _tab3RowVanillaFirst;
    private Rectangle _tab3RowRecordVanilla;
    private Rectangle _tab3RowRecordEvent;
    private Rectangle _tab3CheckboxInfinite;
    private Rectangle _tab3CheckboxVanillaFirst;
    private Rectangle _tab3CheckboxRecordVanilla;
    private Rectangle _tab3CheckboxRecordEvent;

    public AdvancedSettingsTabView(IntegratedHubMenu hub) : base(hub) { }

    public override void Layout(Rectangle menuBounds, Rectangle contentBounds)
    {
        base.Layout(menuBounds, contentBounds);

        int leftColX = menuBounds.X + LeftPadding;
        int tab3Y = menuBounds.Y + TopPadding + 8;
        int rowW = menuBounds.Width - LeftPadding - RightPadding;

        _tab3RowInfinite = new Rectangle(leftColX - 8, tab3Y - 6, rowW, 58);
        _tab3CheckboxInfinite = new Rectangle(leftColX, tab3Y, 36, 36);

        _tab3RowVanillaFirst = new Rectangle(leftColX - 8, tab3Y + 68 - 6, rowW, 58);
        _tab3CheckboxVanillaFirst = new Rectangle(leftColX, tab3Y + 68, 36, 36);

        _tab3RowRecordVanilla = new Rectangle(leftColX - 8, tab3Y + 136 - 6, rowW, 58);
        _tab3CheckboxRecordVanilla = new Rectangle(leftColX, tab3Y + 136, 36, 36);

        _tab3RowRecordEvent = new Rectangle(leftColX - 8, tab3Y + 204 - 6, rowW, 58);
        _tab3CheckboxRecordEvent = new Rectangle(leftColX, tab3Y + 204, 36, 36);
    }

    public override bool ReceiveLeftClick(int x, int y)
    {
        if (_tab3RowInfinite.Contains(x, y))
        {
            ModEntry.Config.EnableInfiniteChat = !ModEntry.Config.EnableInfiniteChat;
            Game1.playSound(ModEntry.Config.EnableInfiniteChat ? "coin" : "drumkit6");
            ModEntry.SHelper.WriteConfig(ModEntry.Config);
            return true;
        }

        if (_tab3RowVanillaFirst.Contains(x, y))
        {
            ModEntry.Config.EnableVanillaFirst = !ModEntry.Config.EnableVanillaFirst;
            Game1.playSound(ModEntry.Config.EnableVanillaFirst ? "coin" : "drumkit6");
            ModEntry.SHelper.WriteConfig(ModEntry.Config);
            return true;
        }

        if (_tab3RowRecordVanilla.Contains(x, y))
        {
            ModEntry.Config.RecordVanillaDialogue = !ModEntry.Config.RecordVanillaDialogue;
            Game1.playSound(ModEntry.Config.RecordVanillaDialogue ? "coin" : "drumkit6");
            ModEntry.SHelper.WriteConfig(ModEntry.Config);
            return true;
        }

        if (_tab3RowRecordEvent.Contains(x, y))
        {
            ModEntry.Config.RecordEventDialogue = !ModEntry.Config.RecordEventDialogue;
            Game1.playSound(ModEntry.Config.RecordEventDialogue ? "coin" : "drumkit6");
            ModEntry.SHelper.WriteConfig(ModEntry.Config);
            return true;
        }

        return false;
    }

    public override void Draw(SpriteBatch b, int mx, int my)
    {
        DrawSettingRow(b, _tab3RowInfinite, _tab3CheckboxInfinite, ModEntry.Config.EnableInfiniteChat,
            I18n.AdvancedSettings.InfiniteChat(),
            I18n.AdvancedSettings.InfiniteChatTooltip(), mx, my);

        DrawSettingRow(b, _tab3RowVanillaFirst, _tab3CheckboxVanillaFirst, ModEntry.Config.EnableVanillaFirst,
            I18n.AdvancedSettings.VanillaFirst(),
            I18n.AdvancedSettings.VanillaFirstTooltip(), mx, my);

        DrawSettingRow(b, _tab3RowRecordVanilla, _tab3CheckboxRecordVanilla, ModEntry.Config.RecordVanillaDialogue,
            I18n.AdvancedSettings.RecordVanillaDialogue(),
            I18n.AdvancedSettings.RecordVanillaDialogueTooltip(), mx, my);

        DrawSettingRow(b, _tab3RowRecordEvent, _tab3CheckboxRecordEvent, ModEntry.Config.RecordEventDialogue,
            I18n.AdvancedSettings.RecordEventDialogue(),
            I18n.AdvancedSettings.RecordEventDialogueTooltip(), mx, my);

        // 底部免责声明
        string disclaimer = Game1.parseText(I18n.AdvancedSettings.Disclaimer(),
            Game1.smallFont, MenuBounds.Width - LeftPadding - RightPadding - 40);

        var disclaimerSize = CustomFontManager.MeasureString(disclaimer, HubUi.RegularFontSize);
        int disclaimerY = MenuBounds.Y + MenuBounds.Height - BottomPadding + 10;

        CustomFontManager.DrawString(b, disclaimer,
            new Vector2(MenuBounds.X + (MenuBounds.Width - disclaimerSize.X) / 2f, disclaimerY),
            Color.DimGray, HubUi.RegularFontSize);
    }

    private void DrawSettingRow(SpriteBatch b, Rectangle rowRect, Rectangle checkboxRect, bool isChecked,
        string label, string desc, int mx, int my)
    {
        bool isHover = rowRect.Contains(mx, my);

        if (isHover)
        {
            b.Draw(Game1.staminaRect, rowRect, new Color(70, 130, 180) * 0.12f);
        }

        Rectangle src = isChecked
            ? new Rectangle(236, 425, 9, 9)
            : new Rectangle(227, 425, 9, 9);
        b.Draw(Game1.mouseCursors, new Vector2(checkboxRect.X, checkboxRect.Y + 2),
            src, Color.White, 0f, Vector2.Zero, 4f, SpriteEffects.None, 1f);

        CustomFontManager.DrawStringBold(b, label,
            new Vector2(checkboxRect.X + 44, checkboxRect.Y),
            isHover ? new Color(0, 0, 50) : Game1.textColor, HubUi.RegularFontSize);

        CustomFontManager.DrawString(b, desc,
            new Vector2(checkboxRect.X + 44, checkboxRect.Y + 22),
            Color.Gray * 0.9f, HubUi.SmallFontSize);
    }
}
