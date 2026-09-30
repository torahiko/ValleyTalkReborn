#nullable enable

using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewValley;
using StardewValley.BellsAndWhistles;
using StardewValley.Menus;
using ValleytalkReborn.UI;
using ValleytalkReborn.Dialogue.Coordination;

namespace ValleytalkReborn
{
/// 
/// 自由文本输入菜单。
/// 右侧为 100% 原版原生立绘台与好感度宝石，左侧舒展对齐的原生风格自定义操作区。
/// 
public class DialogueTextInputMenu : IClickableMenu
{
public delegate void TextSubmittedDelegate(string input);

    // 像素级对齐原版 DialogueBox 规格
    private const int DialogueWidth = 1200;
    private const int DialogueHeight = 384;

    // 左侧文本区排版
    private const int TextLeftPadding = 34;
    private const int HeaderHeight = 44;
    private const int BottomBarHeight = 40;

    // 字阶配置
    private const float TitleFontSize = 22f;
    private const float SubtitleFontSize = 15f;
    private const float InputFontSize = 21f;
    private const float ButtonFontSize = 16f;
    private const float TipFontSize = CustomFontManager.SizeSmall;

    private readonly string _title;
    private readonly string _npcName;
    private readonly string _npcDisplayName;
    private readonly NPC? _currentNpc;
    private readonly TextSubmittedDelegate _onTextSubmitted;
    private readonly DialogueTextInputBox _inputTextBox;

    // 立绘与好感度数据
    private Texture2D? _npcPortrait;
    private Rectangle _portraitSourceRect;
    private Rectangle _friendshipJewel;
    private bool _hasFriendship;
    private int _friendshipHearts;
    private int _maxHearts = 10;

    // 底部动作按钮
    private Rectangle _viewHistoryRect;
    private Rectangle _clearHistoryRect;
    private Rectangle _cancelButtonRect;
    private Rectangle _okButtonRect;

    // 按钮悬停动效
    private float _viewHistoryHoverScale = 1f;
    private float _clearHistoryHoverScale = 1f;
    private float _cancelButtonHoverScale = 1f;
    private float _okButtonHoverScale = 1f;

    private IClickableMenu? _menuToRestore;
    private string? _hoverText;

    public Rectangle MenuBounds => new(xPositionOnScreen, yPositionOnScreen, width, height);

    public DialogueTextInputMenu(string title, TextSubmittedDelegate callback, NPC currentNpc)
        : base(0, 0, DialogueWidth, DialogueHeight, showUpperRightCloseButton: false)
    {
        _currentNpc = currentNpc;
        _npcName = currentNpc?.Name ?? string.Empty;
        _npcDisplayName = currentNpc?.displayName ?? _npcName;
        _title = title ?? (string.IsNullOrEmpty(_npcName)
            ? I18n.DialogueInput.DefaultTitle()
            : I18n.DialogueInput.DefaultTitleWithNpc(_npcDisplayName));
        _onTextSubmitted = callback;

        string placeholder = !string.IsNullOrEmpty(_npcDisplayName)
            ? I18n.DialogueInput.PlaceholderWithName(_npcDisplayName)
            : I18n.DialogueInput.Placeholder();

        _inputTextBox = new DialogueTextInputBox(1000)
        {
            AllowNewlines = false,
            UseCustomFont = true,
            CustomFontSize = InputFontSize,
            CounterFontSize = TipFontSize,
            // ★ 启用控件自身的底板槽与聚焦光晕外框（温润羊皮纸底色 + 原版暖橘木边框）
            DrawFrame = true,
            ShowCharacterCount = true,
            TextColor = BioEditorMenu.TextPrimary,
            Selected = true,
            PlaceholderText = placeholder,
            PlaceholderColor = new Color(175, 145, 115)
        };

        _inputTextBox.OnSubmit += sender =>
        {
            Game1.playSound("coin");
            Submit(sender.Text);
        };

        LoadNpcPortrait();
        LoadFriendshipData();
        Layout();
        RestoreFocus();
    }

    private void LoadNpcPortrait()
    {
        try
        {
            if (_currentNpc?.Portrait != null && !_currentNpc.Portrait.IsDisposed)
            {
                _npcPortrait = _currentNpc.Portrait;
            }
            else
            {
                var character = Game1.getCharacterFromName(_npcName);
                _npcPortrait = (character?.Portrait != null && !character.Portrait.IsDisposed)
                    ? character.Portrait
                    : Game1.content.Load<Texture2D>("Portraits/" + _npcName);
            }

            if (_npcPortrait != null)
            {
                _portraitSourceRect = Game1.getSourceRectForStandardTileSheet(_npcPortrait, 0, 64, 64);
                if (!_npcPortrait.Bounds.Contains(_portraitSourceRect))
                {
                    _portraitSourceRect = new Rectangle(0, 0, 64, 64);
                }
            }
        }
        catch
        {
            _npcPortrait = null;
            _portraitSourceRect = Rectangle.Empty;
        }
    }

    private void LoadFriendshipData()
    {
        _hasFriendship = false;
        _friendshipHearts = 0;
        _maxHearts = 10;

        if (string.IsNullOrEmpty(_npcName)) return;

        var speaker = _currentNpc ?? Game1.getCharacterFromName(_npcName);

        if (Game1.player != null && Game1.player.friendshipData.ContainsKey(_npcName))
        {
            _hasFriendship = true;
            _friendshipHearts = Game1.player.getFriendshipHeartLevelForNPC(_npcName);
            _maxHearts = Utility.GetMaximumHeartsForCharacter(speaker);
        }
    }

    private void Layout()
    {
        width = DialogueWidth;
        height = DialogueHeight;

        // 原版绝对居中锚定
        Vector2 centered = Utility.getTopLeftPositionForCenteringOnScreen(width, height, 0, 0);
        xPositionOnScreen = (int)centered.X;
        yPositionOnScreen = Game1.uiViewport.Height - height - 64;

        // 原版好感度宝石定位（x + width - 64, y + 256, 44, 44）
        _friendshipJewel = new Rectangle(xPositionOnScreen + width - 64, yPositionOnScreen + 256, 44, 44);

        // 原版立绘区起始 X 坐标与木梁分隔定位
        int xPositionOfPortraitArea = xPositionOnScreen + width - 448 + 4;
        int textLeft = xPositionOnScreen + TextLeftPadding;
        int textRightBound = xPositionOfPortraitArea - 40 - 20;
        int textWidth = textRightBound - textLeft;

        // 1. 顶栏排版
        int topY = yPositionOnScreen + 20;
        int sepY = topY + HeaderHeight;

        // 2. 底部动作条微调（贴紧对话框下缘木框）
        int bottomBarY = yPositionOnScreen + height - BottomBarHeight - 16;
        const int histBtnW = 126;
        const int clearBtnW = 114;
        const int sendBtnW = 110;
        const int cancelBtnW = 96;

        _viewHistoryRect = new Rectangle(textLeft, bottomBarY, histBtnW, BottomBarHeight);
        _clearHistoryRect = new Rectangle(_viewHistoryRect.Right + 10, bottomBarY, clearBtnW, BottomBarHeight);

        _cancelButtonRect = new Rectangle(textLeft + textWidth - cancelBtnW, bottomBarY, cancelBtnW, BottomBarHeight);
        _okButtonRect = new Rectangle(_cancelButtonRect.X - sendBtnW - 10, bottomBarY, sendBtnW, BottomBarHeight);

        // 3. 中间输入区高度自适应扩展
        int inputY = sepY + 10;
        int inputH = bottomBarY - 14 - inputY;

        _inputTextBox.Position = new Vector2(textLeft, inputY);
        _inputTextBox.Extent = new Vector2(textWidth, inputH);
        _inputTextBox.InvalidateLayout();
    }

    public override void gameWindowSizeChanged(Rectangle oldBounds, Rectangle newBounds)
    {
        Layout();
    }

    public override void update(GameTime time)
    {
        base.update(time);
        _hoverText = null;
        _inputTextBox.Update(time);
    }

    public void SetMenuToRestore(IClickableMenu menu)
    {
        _menuToRestore = menu;
    }

    public void RestoreFocus()
    {
        _inputTextBox.Selected = true;
        Game1.keyboardDispatcher.Subscriber = _inputTextBox;
    }

    public void RestorePreviousMenu()
    {
        Game1.activeClickableMenu = _menuToRestore ?? this;
        RestoreFocus();
    }

    public void Close()
    {
        if (Game1.keyboardDispatcher.Subscriber == _inputTextBox)
            Game1.keyboardDispatcher.Subscriber = null;

        if (Game1.activeClickableMenu == this)
            Game1.activeClickableMenu = null;
    }

    public void Submit(string text)
    {
        _onTextSubmitted?.Invoke(text?.TrimEnd('\r', '\n') ?? string.Empty);
    }

    // ── 交互分发 ──────────────────────────────────────────

    public override void leftClickHeld(int x, int y)
    {
        base.leftClickHeld(x, y);
        _inputTextBox.LeftClickHeld(x, y);
    }

    public override void releaseLeftClick(int x, int y)
    {
        base.releaseLeftClick(x, y);
        _inputTextBox.ReleaseLeftClick(x, y);
    }

    public override void receiveScrollWheelAction(int direction)
    {
        base.receiveScrollWheelAction(direction);
        ReceiveScrollWheel(direction);
    }

    public void ReceiveScrollWheel(int direction)
    {
        _inputTextBox.ReceiveScrollWheel(direction);
    }

    public override void receiveLeftClick(int x, int y, bool playSound = true)
    {
        ReceiveLeftClick(x, y);
    }

    public void ReceiveLeftClick(int x, int y)
    {
        if (_inputTextBox.ReceiveLeftClick(x, y))
        {
            RestoreFocus();
            return;
        }

        if (_viewHistoryRect.Contains(x, y))
        {
            Game1.playSound("bigSelect");
            Game1.keyboardDispatcher.Subscriber = null;
            var target = _menuToRestore ?? this;
            Game1.activeClickableMenu = new TimelineChronicleMenu(_npcName, target, target, autoLockLatest: false);
            return;
        }

        if (_clearHistoryRect.Contains(x, y))
        {
            Game1.keyboardDispatcher.Subscriber = null;
            Game1.activeClickableMenu = new ClearHistoryScopeMenu(_npcName, this, scope =>
            {
                Game1.playSound("trashcan");
                string dispName = Game1.getCharacterFromName(_npcName)?.displayName ?? _npcName;
                switch (scope)
                {
                    case ClearHistoryScopeMenu.ClearScope.Today:
                        DialogueHistoryManager.Instance.ClearTodayHistory(_npcName);
                        SessionCache.Instance.Reset(_npcName);
                        RecentConversationTracker.Clear(_npcName);
                        DialogueBuilder.Instance?.ClearContext(_npcName);
                        Game1.addHUDMessage(new HUDMessage(
                            I18n.DialogueInput.ClearScopeHudToday(dispName), HUDMessage.achievement_type));
                        break;
                    case ClearHistoryScopeMenu.ClearScope.CurrentNpcAll:
                        DialogueHistoryManager.Instance.ClearHistory(_npcName);
                        SessionCache.Instance.Reset(_npcName);
                        RecentConversationTracker.Clear(_npcName);
                        DialogueBuilder.Instance?.ClearContext(_npcName);
                        Game1.addHUDMessage(new HUDMessage(
                            I18n.DialogueInput.ClearScopeHudCurrentNpcAll(dispName), HUDMessage.achievement_type));
                        break;
                    case ClearHistoryScopeMenu.ClearScope.GlobalAll:
                        DialogueHistoryManager.Instance.ClearAllHistory();
                        SessionCache.Instance.ResetAll();
                        RecentConversationTracker.Clear();
                        DialogueBuilder.Instance?.ClearAllContexts();
                        Game1.addHUDMessage(new HUDMessage(
                            I18n.DialogueInput.ClearScopeHudGlobalAll(), HUDMessage.achievement_type));
                        break;
                }
            });
            return;
        }

        if (_cancelButtonRect.Contains(x, y))
        {
            Game1.playSound("bigDeSelect");
            Submit(string.Empty);
            return;
        }

        if (_okButtonRect.Contains(x, y))
        {
            if (!string.IsNullOrWhiteSpace(_inputTextBox.Text))
            {
                Game1.playSound("coin");
                Submit(_inputTextBox.Text);
            }
            return;
        }

        RestoreFocus();
    }

    public override void receiveKeyPress(Keys key)
    {
        ReceiveKeyPress(key);
    }

    public void ReceiveKeyPress(Keys key)
    {
        if (key == Keys.Escape)
        {
            Game1.playSound("bigDeSelect");
            Submit(string.Empty);
            return;
        }

        if (Game1.options.doesInputListContain(Game1.options.menuButton, key))
            return;

        if (key == Keys.Enter)
        {
            if (!string.IsNullOrWhiteSpace(_inputTextBox.Text))
            {
                Game1.playSound("coin");
                Submit(_inputTextBox.Text);
            }
            return;
        }

        if (DialogueTextInputBox.IsControlKeyDown())
        {
            if (key == Keys.A || key == Keys.C || key == Keys.X || key == Keys.Z || key == Keys.V)
            {
                _inputTextBox.RecieveSpecialInput(key);
                return;
            }
        }

        if (key == Keys.Left || key == Keys.Right || key == Keys.Up || key == Keys.Down ||
            key == Keys.Home || key == Keys.End || key == Keys.Delete || key == Keys.Back)
        {
            _inputTextBox.RecieveSpecialInput(key);
        }
    }

    protected override void cleanupBeforeExit()
    {
        if (Game1.keyboardDispatcher.Subscriber == _inputTextBox)
            Game1.keyboardDispatcher.Subscriber = null;

        base.cleanupBeforeExit();
    }

    // ── 渲染管线 ──────────────────────────────────────────

    public override void draw(SpriteBatch b)
    {
        int mx = Game1.getMouseX();
        int my = Game1.getMouseY();

        _hoverText = null;

        // 1. 全屏微暗遮罩
        b.Draw(Game1.fadeToBlackRect, Game1.graphics.GraphicsDevice.Viewport.Bounds, Color.Black * 0.25f);

        // 2. 原版 DialogueBox.drawBox 原生 9 切片大外框
        DrawNativeDialogueBox(b, xPositionOnScreen, yPositionOnScreen, width, height);

        // 3. 原版 DialogueBox.drawPortrait 原生立绘展台
        DrawNativePortraitArea(b);

        // 4. 左侧顶栏
        DrawHeader(b);

        // 5. 左侧文本输入区（直接委托给 DialogueTextInputBox 自带的温润羊皮纸底板和暖色原木边框）
        _inputTextBox.Draw(b);

        // 6. 底部动作工具栏（沉底排列）
        ActionButtonRenderer.Draw(b, _viewHistoryRect, I18n.DialogueInput.ButtonHistory(), ref _viewHistoryHoverScale, mx, my, fontSize: ButtonFontSize, style: ActionButtonStyle.Default);
        ActionButtonRenderer.Draw(b, _clearHistoryRect, I18n.DialogueInput.ButtonClearMemory(), ref _clearHistoryHoverScale, mx, my, fontSize: ButtonFontSize, style: ActionButtonStyle.Default);
        ActionButtonRenderer.Draw(b, _okButtonRect, I18n.DialogueInput.ButtonSend(), ref _okButtonHoverScale, mx, my, fontSize: ButtonFontSize, style: ActionButtonStyle.Primary);
        ActionButtonRenderer.Draw(b, _cancelButtonRect, I18n.DialogueInput.ButtonCancel(), ref _cancelButtonHoverScale, mx, my, fontSize: ButtonFontSize, style: ActionButtonStyle.Danger);

        // 7. 悬停气泡提示
        if (_hasFriendship && !_friendshipJewel.IsEmpty && _friendshipJewel.Contains(mx, my))
        {
            _hoverText = $"{_friendshipHearts}/{_maxHearts}<";
        }
        else if (_viewHistoryRect.Contains(mx, my))
        {
            _hoverText = !string.IsNullOrEmpty(_npcDisplayName)
                ? I18n.DialogueInput.TooltipHistory(_npcDisplayName)
                : I18n.DialogueInput.TooltipHistoryGeneric();
        }
        else if (_clearHistoryRect.Contains(mx, my))
        {
            _hoverText = !string.IsNullOrEmpty(_npcDisplayName)
                ? I18n.DialogueInput.TooltipClearMemory(_npcDisplayName)
                : I18n.DialogueInput.TooltipClearMemoryGeneric();
        }
        else if (_cancelButtonRect.Contains(mx, my))
        {
            _hoverText = I18n.DialogueInput.TooltipCancel();
        }
        else if (_okButtonRect.Contains(mx, my))
        {
            _hoverText = I18n.DialogueInput.TooltipSend();
        }

        if (!string.IsNullOrEmpty(_hoverText))
        {
            if (_hasFriendship && _friendshipJewel.Contains(mx, my))
            {
                SpriteText.drawStringWithScrollBackground(b, _hoverText, _friendshipJewel.Center.X - SpriteText.getWidthOfString(_hoverText, 999999) / 2, _friendshipJewel.Y - 64, "", 1f, null, SpriteText.ScrollTextAlignment.Left);
            }
            else
            {
                DrawHoverTextCustom(b, _hoverText);
            }
        }

        drawMouse(b);
    }

    private static void DrawNativeDialogueBox(SpriteBatch b, int xPos, int yPos, int boxWidth, int boxHeight)
    {
        b.Draw(Game1.mouseCursors, new Rectangle(xPos, yPos, boxWidth, boxHeight), new Rectangle(306, 320, 16, 16), Color.White);

        b.Draw(Game1.mouseCursors, new Rectangle(xPos, yPos - 20, boxWidth, 24), new Rectangle(275, 313, 1, 6), Color.White);
        b.Draw(Game1.mouseCursors, new Rectangle(xPos + 12, yPos + boxHeight, boxWidth - 20, 32), new Rectangle(275, 328, 1, 8), Color.White);
        b.Draw(Game1.mouseCursors, new Rectangle(xPos - 32, yPos + 24, 32, boxHeight - 28), new Rectangle(264, 325, 8, 1), Color.White);
        b.Draw(Game1.mouseCursors, new Rectangle(xPos + boxWidth, yPos, 28, boxHeight), new Rectangle(293, 324, 7, 1), Color.White);

        b.Draw(Game1.mouseCursors, new Vector2(xPos - 44, yPos - 28), new Rectangle(261, 311, 14, 13), Color.White, 0f, Vector2.Zero, 4f, SpriteEffects.None, 0.87f);
        b.Draw(Game1.mouseCursors, new Vector2(xPos + boxWidth - 8, yPos - 28), new Rectangle(291, 311, 12, 11), Color.White, 0f, Vector2.Zero, 4f, SpriteEffects.None, 0.87f);
        b.Draw(Game1.mouseCursors, new Vector2(xPos + boxWidth - 8, yPos + boxHeight - 8), new Rectangle(291, 326, 12, 12), Color.White, 0f, Vector2.Zero, 4f, SpriteEffects.None, 0.87f);
        b.Draw(Game1.mouseCursors, new Vector2(xPos - 44, yPos + boxHeight - 4), new Rectangle(261, 327, 14, 11), Color.White, 0f, Vector2.Zero, 4f, SpriteEffects.None, 0.87f);
    }

    private void DrawNativePortraitArea(SpriteBatch b)
    {
        if (width < 642) return;

        int xPositionOfPortraitArea = xPositionOnScreen + width - 448 + 4;
        int widthOfPortraitArea = xPositionOnScreen + width - xPositionOfPortraitArea;

        b.Draw(Game1.mouseCursors, new Rectangle(xPositionOfPortraitArea - 40, yPositionOnScreen, 36, height), new Rectangle(278, 324, 9, 1), Color.White);
        b.Draw(Game1.mouseCursors, new Vector2(xPositionOfPortraitArea - 40, yPositionOnScreen - 20), new Rectangle(278, 313, 10, 7), Color.White, 0f, Vector2.Zero, 4f, SpriteEffects.None, 0.88f);
        b.Draw(Game1.mouseCursors, new Vector2(xPositionOfPortraitArea - 40, yPositionOnScreen + height), new Rectangle(278, 328, 10, 8), Color.White, 0f, Vector2.Zero, 4f, SpriteEffects.None, 0.88f);

        int portraitBoxX = xPositionOfPortraitArea + 76;
        int portraitBoxY = yPositionOnScreen + height / 2 - 148 - 36;
        b.Draw(Game1.mouseCursors, new Vector2(xPositionOfPortraitArea - 8, yPositionOnScreen), new Rectangle(583, 411, 115, 97), Color.White, 0f, Vector2.Zero, 4f, SpriteEffects.None, 0.88f);

        if (_npcPortrait != null && !_portraitSourceRect.IsEmpty)
        {
            b.Draw(_npcPortrait, new Vector2(portraitBoxX + 16, portraitBoxY + 24), _portraitSourceRect, Color.White, 0f, Vector2.Zero, 4f, SpriteEffects.None, 0.88f);
        }

        if (!string.IsNullOrEmpty(_npcDisplayName))
        {
            SpriteText.drawStringHorizontallyCenteredAt(b, _npcDisplayName, xPositionOfPortraitArea + widthOfPortraitArea / 2, portraitBoxY + 296 + 16, 999999, -1, 999999, 1f, 0.88f, false, null, 99999);
        }

        if (_hasFriendship && !_friendshipJewel.IsEmpty)
        {
            Rectangle jewelSource = (_friendshipHearts >= 10 || _friendshipHearts >= _maxHearts)
                ? new Rectangle(269, 494, 11, 11)
                : new Rectangle(
                    Math.Max(140, 140 + (int)(Game1.currentGameTime.TotalGameTime.TotalMilliseconds % 1000.0 / 250.0) * 11),
                    Math.Max(532, 532 + _friendshipHearts / 2 * 11),
                    11, 11);

            b.Draw(Game1.mouseCursors, new Vector2(_friendshipJewel.X, _friendshipJewel.Y), jewelSource, Color.White, 0f, Vector2.Zero, 4f, SpriteEffects.None, 0.88f);
        }
    }

    private void DrawHeader(SpriteBatch b)
    {
        int textLeft = xPositionOnScreen + TextLeftPadding;
        int topY = yPositionOnScreen + 20;

        CustomFontManager.DrawStringBold(b, _title, new Vector2(textLeft, topY), BioEditorMenu.TextPrimary, TitleFontSize);

        string subtitle = !string.IsNullOrEmpty(_npcDisplayName)
            ? I18n.DialogueInput.SubtitleWithName(_npcDisplayName)
            : I18n.DialogueInput.SubtitleGeneric();
        CustomFontManager.DrawString(b, subtitle, new Vector2(textLeft, topY + 26), BioEditorMenu.TextMuted, SubtitleFontSize);

        int sepY = topY + HeaderHeight;
        int xPositionOfPortraitArea = xPositionOnScreen + width - 448 + 4;
        int textWidth = (xPositionOfPortraitArea - 40 - 20) - textLeft;
        b.Draw(Game1.staminaRect, new Rectangle(textLeft, sepY, textWidth, 1), Color.Gray * 0.35f);
    }

    internal static void DrawHoverTextCustom(SpriteBatch b, string text)
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
}


}