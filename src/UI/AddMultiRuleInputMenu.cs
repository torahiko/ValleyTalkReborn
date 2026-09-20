#nullable enable
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewValley;
using StardewValley.Menus;
using System;
using System.Collections.Generic;
using System.Linq;
using ValleytalkReborn.Services;

namespace ValleytalkReborn.UI;

/// <summary>
/// 规则批量新增弹窗：
/// 1. 采用 TimelineChronicleMenu 行走图动态裁剪出纯正像素小头像
/// 2. 分类与时效排布对齐，数字步进器无缝内嵌
/// 3. 卡片式 NPC 多选网格（微型头像、选中高亮、角标对勾）
/// 4. 底部现代化文字确认/取消操作栏
/// </summary>
internal class AddMultiRuleInputMenu : IClickableMenu
{
    private readonly IntegratedHubMenu _hub;
    private DialogueTextInputBox _inputBox = null!;

    private MemoryCategory _category = MemoryCategory.Fact;
    private int _durationMode = 2; // 0=Today, 1=Days, 2=Perm
    private NumberStepper? _dayStepper;

    private Rectangle _factCapsuleRect;
    private Rectangle _behaviorCapsuleRect;
    private Rectangle _durTodayRect;
    private Rectangle _durCustomRect;
    private Rectangle _durPermRect;

    // ── NPC 多选选项（包含行走图缓存与裁切帧） ──
    private record NpcOption(string Id, string DisplayName, Texture2D? Sprite, Rectangle SourceRect);
    private readonly List<NpcOption> _npcOptions = new();
    private readonly HashSet<string> _selectedNpcIds = new(StringComparer.OrdinalIgnoreCase);

    private Rectangle _npcGridBounds;
    private int _npcScrollOffset = 0;
    private const int NpcItemH = 38;

    // 快捷按钮
    private Rectangle _btnSelectAllRect;
    private Rectangle _btnInvertRect;
    private Rectangle _btnClearRect;

    // 底部主操作按钮
    private Rectangle _btnOkRect;
    private Rectangle _btnCancelRect;
    private ClickableTextureComponent _closeXButton = null!;

    private const int MenuWidth = 780;
    private const int MenuHeight = 600;

    public AddMultiRuleInputMenu(IntegratedHubMenu hub, string defaultScope)
    {
        _hub = hub;
        width = MenuWidth;
        height = MenuHeight;
        xPositionOnScreen = (Game1.uiViewport.Width - width) / 2;
        yPositionOnScreen = (Game1.uiViewport.Height - height) / 2;

        BuildNpcOptions();

        if (!string.IsNullOrEmpty(defaultScope) && defaultScope != "__ALL__")
            _selectedNpcIds.Add(defaultScope);
        else
            _selectedNpcIds.Add("WORLD");

        InitComponents();
    }

    private void BuildNpcOptions()
    {
        _npcOptions.Clear();
        _npcOptions.Add(new NpcOption("WORLD", "小镇共识", null, Rectangle.Empty));

        var candidates = NpcCandidateQueryService.GetCleanedCandidates();
        foreach (var c in candidates)
        {
            var (sprite, srcRect) = GetNpcWalkingHeadSprite(c.Id);
            _npcOptions.Add(new NpcOption(c.Id, c.DisplayName, sprite, srcRect));
        }
    }

    // ── 动态提取行走图面部核心逻辑（参考 TimelineChronicleMenu） ──
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

    private void InitComponents()
    {
        int padX = 36;
        int curY = yPositionOnScreen + 64;

        _closeXButton = new ClickableTextureComponent(
            new Rectangle(xPositionOnScreen + width - 48, yPositionOnScreen + 16, 32, 32),
            Game1.mouseCursors, new Rectangle(337, 494, 12, 12), 2.8f);

        // 1. 文本输入框
        _inputBox = new DialogueTextInputBox(RuleManager.MaxRuleLength, (int)(RuleManager.MaxRuleLength * 0.9f))
        {
            Position = new Vector2(xPositionOnScreen + padX, curY),
            Extent = new Vector2(width - padX * 2, 72),
            UseCustomFont = true,
            CustomFontSize = CustomFontManager.SizeRegular,
            CounterFontSize = CustomFontManager.SizeSmall,
            DrawFrame = true,
            ShowCharacterCount = true,
            AllowNewlines = true,
            TextColor = Game1.textColor,
            Selected = true
        };
        Game1.keyboardDispatcher.Subscriber = _inputBox;
        curY += 82;

        // 2. 类别与时效配置区
        int contentW = width - padX * 2;
        int groupW = (contentW - 20) / 2;

        int catBtnW = (groupW - 10) / 2;
        _factCapsuleRect = new Rectangle(xPositionOnScreen + padX, curY, catBtnW, 30);
        _behaviorCapsuleRect = new Rectangle(_factCapsuleRect.Right + 10, curY, catBtnW, 30);

        int durX = xPositionOnScreen + padX + groupW + 20;
        int durBtnW = (groupW - 12 - 70) / 2;
        _durTodayRect = new Rectangle(durX, curY, durBtnW, 30);
        _durPermRect = new Rectangle(_durTodayRect.Right + 6, curY, durBtnW, 30);
        _durCustomRect = new Rectangle(_durPermRect.Right + 6, curY, 70, 30);

        _dayStepper = new NumberStepper(new Rectangle(_durCustomRect.X + 2, _durCustomRect.Y + 1, _durCustomRect.Width - 4, 28), 3, 1, 99, 1, "d");

        curY += 40;

        // 3. NPC 网格快捷栏
        int quickBtnW = 60;
        _btnSelectAllRect = new Rectangle(xPositionOnScreen + width - padX - quickBtnW * 3 - 16, curY, quickBtnW, 26);
        _btnInvertRect = new Rectangle(_btnSelectAllRect.Right + 8, curY, quickBtnW, 26);
        _btnClearRect = new Rectangle(_btnInvertRect.Right + 8, curY, quickBtnW, 26);

        curY += 32;

        // NPC 网格布局
        int bottomReserve = 66;
        _npcGridBounds = new Rectangle(xPositionOnScreen + padX, curY, contentW, (yPositionOnScreen + height - bottomReserve) - curY);

        // 4. 底部确定与取消
        int footerY = yPositionOnScreen + height - 52;
        int btnW = 140;
        int btnH = 36;
        _btnCancelRect = new Rectangle(xPositionOnScreen + padX, footerY, btnW, btnH);
        _btnOkRect = new Rectangle(xPositionOnScreen + width - padX - btnW, footerY, btnW, btnH);
    }

    public override void receiveScrollWheelAction(int direction)
    {
        base.receiveScrollWheelAction(direction);
        int mx = Game1.getMouseX();
        int my = Game1.getMouseY();

        if (_npcGridBounds.Contains(mx, my))
        {
            int rowCount = (int)Math.Ceiling(_npcOptions.Count / 3.0);
            int visibleRows = _npcGridBounds.Height / NpcItemH;
            int maxOffset = Math.Max(0, rowCount - visibleRows);
            _npcScrollOffset = Math.Clamp(_npcScrollOffset - (direction > 0 ? 1 : -1), 0, maxOffset);
            Game1.playSound("shwip");
        }
    }

    public override void receiveLeftClick(int x, int y, bool playSound = true)
    {
        base.receiveLeftClick(x, y, playSound);

        if (_closeXButton.containsPoint(x, y))
        {
            Game1.playSound("bigDeSelect");
            CloseAndReturn();
            return;
        }

        if (_inputBox.ReceiveLeftClick(x, y)) return;
        if (_inputBox.ContainsPoint(x, y))
        {
            _inputBox.Selected = true;
            Game1.keyboardDispatcher.Subscriber = _inputBox;
            return;
        }

        if (_factCapsuleRect.Contains(x, y)) { _category = MemoryCategory.Fact; Game1.playSound("smallSelect"); return; }
        if (_behaviorCapsuleRect.Contains(x, y)) { _category = MemoryCategory.Behavior; Game1.playSound("smallSelect"); return; }

        if (_durTodayRect.Contains(x, y)) { _durationMode = 0; Game1.playSound("smallSelect"); return; }
        if (_durPermRect.Contains(x, y)) { _durationMode = 2; Game1.playSound("smallSelect"); return; }
        if (_durCustomRect.Contains(x, y))
        {
            if (_durationMode != 1)
            {
                _durationMode = 1;
                Game1.playSound("smallSelect");
            }
            else
            {
                _dayStepper?.ReceiveLeftClick(x, y);
            }
            return;
        }

        if (_btnSelectAllRect.Contains(x, y))
        {
            foreach (var opt in _npcOptions) _selectedNpcIds.Add(opt.Id);
            Game1.playSound("smallSelect");
            return;
        }
        if (_btnInvertRect.Contains(x, y))
        {
            foreach (var opt in _npcOptions)
            {
                if (_selectedNpcIds.Contains(opt.Id)) _selectedNpcIds.Remove(opt.Id);
                else _selectedNpcIds.Add(opt.Id);
            }
            Game1.playSound("smallSelect");
            return;
        }
        if (_btnClearRect.Contains(x, y))
        {
            _selectedNpcIds.Clear();
            Game1.playSound("smallSelect");
            return;
        }

        if (_npcGridBounds.Contains(x, y))
        {
            int cols = 3;
            int colW = (_npcGridBounds.Width - 16) / cols;
            int startRow = _npcScrollOffset;
            int visibleRows = _npcGridBounds.Height / NpcItemH;

            for (int r = 0; r < visibleRows; r++)
            {
                for (int c = 0; c < cols; c++)
                {
                    int index = (startRow + r) * cols + c;
                    if (index >= _npcOptions.Count) break;

                    var opt = _npcOptions[index];
                    var rect = new Rectangle(_npcGridBounds.X + 4 + c * (colW + 4), _npcGridBounds.Y + 4 + r * NpcItemH, colW, NpcItemH - 4);
                    if (rect.Contains(x, y))
                    {
                        if (_selectedNpcIds.Contains(opt.Id)) _selectedNpcIds.Remove(opt.Id);
                        else _selectedNpcIds.Add(opt.Id);
                        Game1.playSound("drumkit6");
                        return;
                    }
                }
            }
        }

        if (_btnOkRect.Contains(x, y))
        {
            Submit();
        }
        else if (_btnCancelRect.Contains(x, y))
        {
            Game1.playSound("bigDeSelect");
            CloseAndReturn();
        }
    }

    private void Submit()
    {
        string text = _inputBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(text))
        {
            Game1.playSound("cancel");
            return;
        }

        if (_selectedNpcIds.Count == 0)
        {
            Game1.playSound("cancel");
            Game1.addHUDMessage(new HUDMessage("请至少勾选一个适用对象", HUDMessage.error_type));
            return;
        }

        int duration = _durationMode switch
        {
            0 => 0,
            1 => _dayStepper?.Value ?? 1,
            _ => -1
        };

        int successCount = 0;
        int fullCount = 0;

        foreach (var target in _selectedNpcIds)
        {
            var r = RuleManager.Instance.AddRule(target, text, duration, _category);
            if (r == MemoryOperationResult.Success) successCount++;
            else if (r == MemoryOperationResult.CapacityFull) fullCount++;
        }

        if (successCount > 0)
        {
            Game1.playSound("coin");
            Game1.addHUDMessage(new HUDMessage($"✔ 已为 {successCount} 位对象添加规则", HUDMessage.newQuest_type));
            CloseAndReturn();
        }
        else if (fullCount > 0)
        {
            Game1.playSound("cancel");
            Game1.addHUDMessage(new HUDMessage(I18n.Memory.AddFailedFull(RuleManager.MaxRulesPerScope), HUDMessage.error_type));
        }
        else
        {
            Game1.playSound("cancel");
            Game1.addHUDMessage(new HUDMessage("添加失败：存在重复规则", HUDMessage.error_type));
        }
    }

    private void CloseAndReturn()
    {
        if (Game1.keyboardDispatcher.Subscriber == _inputBox)
            Game1.keyboardDispatcher.Subscriber = null;

        exitThisMenu();
        _hub.RefreshEntries();
        Game1.activeClickableMenu = _hub;
    }

    public override void receiveKeyPress(Keys key)
    {
        if (Game1.keyboardDispatcher.Subscriber == _inputBox)
        {
            if (key == Keys.Escape)
            {
                CloseAndReturn();
                return;
            }
            if (!DialogueTextInputBox.IsControlKeyDown())
                _inputBox.RecieveSpecialInput(key);
            return;
        }
        base.receiveKeyPress(key);
    }

    public override void draw(SpriteBatch b)
    {
        _inputBox.Update(Game1.currentGameTime);
        int mx = Game1.getMouseX();
        int my = Game1.getMouseY();

        b.Draw(Game1.fadeToBlackRect, Game1.graphics.GraphicsDevice.Viewport.Bounds, Color.Black * 0.5f);

        IClickableMenu.drawTextureBox(b, xPositionOnScreen - 8, yPositionOnScreen - 8, width + 16, height + 16, Color.White);
        b.Draw(Game1.staminaRect, new Rectangle(xPositionOnScreen, yPositionOnScreen, width, height), new Color(245, 230, 205));
        b.Draw(Game1.menuTexture, new Rectangle(xPositionOnScreen, yPositionOnScreen, width, height),
            new Rectangle(64, 128, 64, 64), new Color(245, 230, 205));

        string title = "新建规则 · 批量分派";
        var titleSz = CustomFontManager.MeasureStringBold(title, CustomFontManager.SizeTitle);
        CustomFontManager.DrawStringBold(b, title,
            new Vector2((int)MathF.Round(xPositionOnScreen + (width - titleSz.X) / 2f), yPositionOnScreen + 18),
            Game1.textColor, CustomFontManager.SizeTitle);

        _closeXButton.draw(b);
        _inputBox.Draw(b);

        DrawSegment(b, _factCapsuleRect, "既定事实", _category == MemoryCategory.Fact, mx, my);
        DrawSegment(b, _behaviorCapsuleRect, "行为准则", _category == MemoryCategory.Behavior, mx, my);

        DrawSegment(b, _durTodayRect, "仅今天", _durationMode == 0, mx, my);
        DrawSegment(b, _durPermRect, "永久有效", _durationMode == 2, mx, my);

        if (_durationMode == 1)
        {
            _dayStepper?.Draw(b);
        }
        else
        {
            DrawSegment(b, _durCustomRect, "指定天数", false, mx, my);
        }

        string npcHeader = $"适用对象（已勾选 {_selectedNpcIds.Count} 位）：";
        CustomFontManager.DrawString(b, npcHeader, new Vector2(_npcGridBounds.X, _btnSelectAllRect.Y + 4),
            Game1.textColor, CustomFontManager.SizeRegular);

        DrawQuickButton(b, _btnSelectAllRect, "全选", mx, my);
        DrawQuickButton(b, _btnInvertRect, "反选", mx, my);
        DrawQuickButton(b, _btnClearRect, "清空", mx, my);

        b.Draw(Game1.staminaRect, new Rectangle(_npcGridBounds.X + 1, _npcGridBounds.Y + 1, _npcGridBounds.Width - 2, _npcGridBounds.Height - 2), new Color(236, 222, 198) * 0.6f);
        IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(403, 383, 6, 6),
            _npcGridBounds.X, _npcGridBounds.Y, _npcGridBounds.Width, _npcGridBounds.Height,
            new Color(210, 190, 160) * 0.8f, 2f, false);

        int cols = 3;
        int colW = (_npcGridBounds.Width - 16) / cols;
        int startRow = _npcScrollOffset;
        int visibleRows = _npcGridBounds.Height / NpcItemH;

        for (int r = 0; r < visibleRows; r++)
        {
            for (int c = 0; c < cols; c++)
            {
                int index = (startRow + r) * cols + c;
                if (index >= _npcOptions.Count) break;

                var opt = _npcOptions[index];
                var cardRect = new Rectangle(_npcGridBounds.X + 4 + c * (colW + 4), _npcGridBounds.Y + 4 + r * NpcItemH, colW, NpcItemH - 4);

                bool isChecked = _selectedNpcIds.Contains(opt.Id);
                bool isHover = cardRect.Contains(mx, my);

                // 选中态复刻 RulesTabView 列表效果与配色（保持悬浮维持原状）
                Color bg = isChecked ? new Color(242, 226, 200)
                         : (isHover ? new Color(255, 246, 235) : Color.White * 0.6f);

                b.Draw(Game1.staminaRect, cardRect, bg);
                IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(432, 439, 9, 9),
                    cardRect.X, cardRect.Y, cardRect.Width, cardRect.Height,
                    isChecked ? new Color(210, 180, 135) : (isHover ? Color.Wheat : new Color(220, 205, 185) * 0.7f), 2f, false);

                // 微型头像框（24x24，绘制裁剪后的行走图帧）
                int avSize = 24;
                var avRect = new Rectangle(cardRect.X + 6, cardRect.Y + (cardRect.Height - avSize) / 2, avSize, avSize);

                if (opt.Sprite != null && !opt.SourceRect.IsEmpty)
                {
                    b.Draw(Game1.staminaRect, avRect, new Color(248, 236, 212));
                    IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(403, 383, 6, 6),
                        avRect.X - 1, avRect.Y - 1, avRect.Width + 2, avRect.Height + 2, new Color(190, 150, 110), 1.2f, false);

                    b.Draw(opt.Sprite, avRect, opt.SourceRect, Color.White);
                }
                else
                {
                    b.Draw(Game1.staminaRect, avRect, new Color(180, 130, 80) * 0.35f);
                    string sym = "❖";
                    var sz = CustomFontManager.MeasureString(sym, CustomFontManager.SizeSmall);
                    CustomFontManager.DrawString(b, sym, new Vector2(avRect.X + (avSize - sz.X) / 2f, avRect.Y + 2), new Color(180, 130, 80), CustomFontManager.SizeSmall);
                }

                // 名字：选中时与 RulesTabView 保持一致使用原版纯净主字色
                Color nameCol = Game1.textColor;
                CustomFontManager.DrawString(b, opt.DisplayName, new Vector2(avRect.Right + 6, cardRect.Y + (cardRect.Height - 20) / 2f), nameCol, CustomFontManager.SizeRegular);

                // 勾选徽记：契合暖木与麦杏色调的深木暖褐勾选标识
                if (isChecked)
                {
                    string checkMark = "✔";
                    var csz = CustomFontManager.MeasureStringBold(checkMark, CustomFontManager.SizeSmall);
                    CustomFontManager.DrawStringBold(b, checkMark,
                        new Vector2(cardRect.Right - csz.X - 8, cardRect.Y + (cardRect.Height - csz.Y) / 2f),
                        new Color(130, 85, 45), CustomFontManager.SizeSmall);
                }
            }
        }

        DrawBottomButton(b, _btnCancelRect, "取消 (Esc)", mx, my, isPrimary: false);
        DrawBottomButton(b, _btnOkRect, $"✔ 确定分派 ({_selectedNpcIds.Count})", mx, my, isPrimary: true);

        drawMouse(b);
    }

    private static void DrawSegment(SpriteBatch b, Rectangle rect, string label, bool isActive, int mx, int my)
    {
        bool isHover = rect.Contains(mx, my);
        Color bg = isActive ? (isHover ? Color.Gold : new Color(255, 220, 130))
                            : (isHover ? new Color(255, 240, 220) : Color.White);

        b.Draw(Game1.staminaRect, new Rectangle(rect.X + 1, rect.Y + 1, rect.Width - 2, rect.Height - 2), bg);
        IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(432, 439, 9, 9),
            rect.X, rect.Y, rect.Width, rect.Height, isActive ? new Color(200, 150, 50) : Color.Wheat, 2f, false);

        var sz = CustomFontManager.MeasureString(label, CustomFontManager.SizeSmall);
        CustomFontManager.DrawString(b, label,
            new Vector2(rect.X + (rect.Width - sz.X) / 2f, rect.Y + (rect.Height - sz.Y) / 2f),
            isActive ? Game1.textColor : Color.DimGray, CustomFontManager.SizeSmall);
    }

    private static void DrawQuickButton(SpriteBatch b, Rectangle rect, string label, int mx, int my)
    {
        bool isHover = rect.Contains(mx, my);
        Color bg = isHover ? new Color(255, 235, 205) : new Color(225, 212, 195);

        b.Draw(Game1.staminaRect, rect, bg);
        IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(403, 383, 6, 6),
            rect.X, rect.Y, rect.Width, rect.Height, new Color(190, 170, 145), 1.8f, false);

        var sz = CustomFontManager.MeasureString(label, CustomFontManager.SizeSmall);
        CustomFontManager.DrawString(b, label,
            new Vector2(rect.X + (rect.Width - sz.X) / 2f, rect.Y + (rect.Height - sz.Y) / 2f - 1),
            Game1.textColor, CustomFontManager.SizeSmall);
    }

    private static void DrawBottomButton(SpriteBatch b, Rectangle rect, string label, int mx, int my, bool isPrimary)
    {
        bool isHover = rect.Contains(mx, my);
        Color bg = isPrimary ? (isHover ? Color.Gold : new Color(255, 220, 130))
                             : (isHover ? new Color(255, 235, 205) : new Color(215, 185, 140));

        b.Draw(Game1.staminaRect, new Rectangle(rect.X + 2, rect.Y + 2, rect.Width, rect.Height), Color.Black * 0.12f);
        b.Draw(Game1.staminaRect, new Rectangle(rect.X + 1, rect.Y + 1, rect.Width - 2, rect.Height - 2), bg);
        IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(432, 439, 9, 9),
            rect.X, rect.Y, rect.Width, rect.Height, isPrimary ? new Color(210, 160, 60) : new Color(180, 140, 95), 2.5f, false);

        var sz = CustomFontManager.MeasureStringBold(label, CustomFontManager.SizeRegular);
        CustomFontManager.DrawStringBold(b, label,
            new Vector2(rect.X + (rect.Width - sz.X) / 2f, rect.Y + (rect.Height - sz.Y) / 2f),
            Game1.textColor, CustomFontManager.SizeRegular);
    }
}