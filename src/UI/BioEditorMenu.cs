using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewValley;
using StardewValley.Menus;

namespace ValleytalkReborn;

/// <summary>
/// 静态人设编辑器菜单：五 Tab 外壳，本票仅实现 Tab1（身份心理 / Unique / HomeLocationBed）。
/// 编辑副本为内存 _bio；仅 SaveOverlay 时落盘；不写存档、不联机同步。
/// 整体替换方式打开（Game1.activeClickableMenu 赋值），关闭时恢复 returnMenu。
/// </summary>
internal sealed class BioEditorMenu : IClickableMenu
{
    // ── 布局常量 ──────────────────────────────────────────────────────
    private const int HeaderH = 56;
    private const int TabBarH = 44;
    private const int FooterH = 52;
    private const int PadX = 20;
    private const int ListRowH = 32;

    private static readonly string[] TabTitles = new[]
    {
        "1.身份心理",
        "2.言行举止",
        "3.好感演变",
        "4.社交关系",
        "5.环境感知",
    };

    // ── 状态（全部 Memory 作用域，随菜单生命周期） ─────────────────────
    private readonly string _npcName;
    private readonly IClickableMenu? _returnMenu;
    private BioData _bio;
    private bool _hasOverlay;
    private bool _dirty;
    private int _activeTab; // 0..4

    private MultilineTextBox _biographyBox;
    private TextBox _uniqueBox;
    private OptionsCheckbox _homeBedCheckbox;

    private readonly Rectangle[] _tabRects = new Rectangle[5];
    private Rectangle _cancelRect;
    private Rectangle _saveRect;
    private Rectangle _resetRect;

    // 脏标记提示的绘制计时
    private double _saveFlashTimer;

    public BioEditorMenu(string npcName, IClickableMenu? returnMenu)
        : base(
              (Game1.uiViewport.Width - Math.Clamp(Game1.uiViewport.Width - 160, 760, 1040)) / 2,
              (Game1.uiViewport.Height - Math.Clamp(Game1.uiViewport.Height - 120, 480, 640)) / 2,
              Math.Clamp(Game1.uiViewport.Width - 160, 760, 1040),
              Math.Clamp(Game1.uiViewport.Height - 120, 480, 640),
              showUpperRightCloseButton: false)
    {
        _npcName = npcName;
        _returnMenu = returnMenu;

        _bio = ModEntry.BioStorage!.LoadEditableBio(npcName);
        _hasOverlay = ModEntry.BioStorage.HasCustomOverlay(npcName);
        _activeTab = 0;

        _biographyBox = new MultilineTextBox(Rectangle.Empty, maxLines: 512);

        // 原生 TextBox：LooseSprites/textBox 贴图（回退 mouseCursors），smallFont
        Texture2D? uniqueTexture = LoadTextBoxTexture();
        _uniqueBox = new TextBox(uniqueTexture, uniqueTexture, Game1.smallFont, Game1.textColor);
        _homeBedCheckbox = new OptionsCheckbox("床位固定 HomeLocationBed", -1, 0, 0);

        Layout();

        // 初始化 Tab1 控件初值
        _biographyBox.Text = _bio.Biography ?? string.Empty;
        _uniqueBox.Text = _bio.Unique ?? string.Empty;
        _uniqueBox.Selected = false;
        _homeBedCheckbox.isChecked = _bio.HomeLocationBed;
    }

    // ── 布局 ──────────────────────────────────────────────────────────

    private void Layout()
    {
        int contentTop = yPositionOnScreen + HeaderH;
        int contentLeft = xPositionOnScreen + PadX;
        int contentW = width - PadX * 2;

        // Tab 栏
        int tabGap = 6;
        int tabW = (contentW - tabGap * (TabTitles.Length - 1)) / TabTitles.Length;
        int tabY = yPositionOnScreen + HeaderH + 4;
        for (int i = 0; i < TabTitles.Length; i++)
        {
            _tabRects[i] = new Rectangle(
                contentLeft + i * (tabW + tabGap),
                tabY,
                tabW,
                TabBarH - 4);
        }

        // 页脚按钮
        int footerY = yPositionOnScreen + height - FooterH + 6;
        int btnH = FooterH - 12;
        int btnW = Math.Min(200, contentW / 3);

        _cancelRect = new Rectangle(contentLeft, footerY, btnW, btnH);
        _saveRect = new Rectangle(xPositionOnScreen + (width - btnW) / 2, footerY, btnW, btnH);
        _resetRect = new Rectangle(xPositionOnScreen + width - PadX - btnW, footerY, btnW, btnH);

        // Tab1 内容区
        int bodyTop = tabY + TabBarH - 4 + 8;
        int bodyBottom = yPositionOnScreen + height - FooterH - 4;
        int bodyH = bodyBottom - bodyTop;
        int bodyLeft = contentLeft;
        int bodyW = contentW;

        if (bodyH > 80)
        {
            int bioH = Math.Max(120, (int)(bodyH * 0.55f));

            // 保留旧实例的已编辑内容与焦点状态，避免 Tab 切换导致数据丢失
            var oldBio = _biographyBox;
            string preservedText = oldBio.Text;
            bool preservedSelected = oldBio.Selected;

            _biographyBox = new MultilineTextBox(
                new Rectangle(bodyLeft, bodyTop, bodyW, bioH),
                maxLines: 512);
            _biographyBox.Text = preservedText;
            _biographyBox.Selected = preservedSelected;

            int uniqueY = bodyTop + bioH + ListRowH + 8;
            int uniqueH = 40;
            _uniqueBox.X = bodyLeft;
            _uniqueBox.Y = uniqueY;
            _uniqueBox.Width = bodyW;
            _uniqueBox.Height = uniqueH;

            _homeBedCheckbox.bounds = new Rectangle(bodyLeft, uniqueY + uniqueH + 10, 36, 36);
        }
    }

    // ── 主线程回写（先比较后赋值，避免每帧分配） ──────────────────────

    public override void update(GameTime time)
    {
        base.update(time);

        _saveFlashTimer += time.ElapsedGameTime.TotalMilliseconds;

        if (_activeTab == 0)
        {
            _biographyBox.Update(time);
            string bio = _biographyBox.Text;
            if (bio != _bio.Biography)
            {
                _bio.Biography = bio;
                MarkDirty();
            }
            if (_uniqueBox.Text != (_bio.Unique ?? string.Empty))
            {
                _bio.Unique = _uniqueBox.Text;
                MarkDirty();
            }
            if (_homeBedCheckbox.isChecked != _bio.HomeLocationBed)
            {
                _bio.HomeLocationBed = _homeBedCheckbox.isChecked;
                MarkDirty();
            }
        }
    }

    public override void performHoverAction(int x, int y)
    {
        // 无悬停交互需求（保留接口）
    }

    public override void receiveLeftClick(int x, int y, bool playSound = true)
    {
        // Tab 切换
        for (int i = 0; i < _tabRects.Length; i++)
        {
            if (_tabRects[i].Contains(x, y))
            {
                SwitchTab(i);
                return;
            }
        }

        if (_activeTab == 0)
        {
            // Biography 框
            if (_biographyBox.Bounds.Contains(x, y))
            {
                FocusBox(_biographyBox);
                _biographyBox.Selected = true;
                Game1.keyboardDispatcher.Subscriber = _biographyBox;
                return;
            }

            // Unique 框
            if (new Rectangle(_uniqueBox.X, _uniqueBox.Y, _uniqueBox.Width, _uniqueBox.Height).Contains(x, y))
            {
                _biographyBox.Selected = false;
                _uniqueBox.SelectMe();
                _uniqueBox.Hover(x, y);
                Game1.keyboardDispatcher.Subscriber = _uniqueBox;
                return;
            }

            // HomeBed 复选框
            if (_homeBedCheckbox.bounds.Contains(x, y))
            {
                _homeBedCheckbox.receiveLeftClick(x, y);
                return;
            }
        }

        // 页脚按钮
        if (_saveRect.Contains(x, y))
        {
            SaveAndClose();
            return;
        }
        if (_cancelRect.Contains(x, y))
        {
            TryCancel();
            return;
        }
        if (_resetRect.Contains(x, y))
        {
            TryReset();
            return;
        }

        // 点击空白区：取消聚焦所有文本框
        Game1.keyboardDispatcher.Subscriber = null;
        _biographyBox.Selected = false;
        _uniqueBox.Selected = false;
    }

    public override void receiveScrollWheelAction(int direction)
    {
        if (_activeTab == 0 && _biographyBox.Selected)
        {
            _biographyBox.Scroll(direction);
        }
    }

    public override void receiveKeyPress(Keys key)
    {
        // ESC 始终触发取消确认流程
        if (key == Keys.Escape)
        {
            TryCancel();
            return;
        }

        // 唯一输入路径 = KeyboardDispatcher（字符/功能键均由 dispatcher 注入 subscriber）；
        // 此处不再转发，避免与 dispatcher 双处理。
        base.receiveKeyPress(key);
    }

    protected override void cleanupBeforeExit()
    {
        base.cleanupBeforeExit();
        if (Game1.keyboardDispatcher.Subscriber == _biographyBox
            || Game1.keyboardDispatcher.Subscriber == _uniqueBox)
        {
            Game1.keyboardDispatcher.Subscriber = null;
        }
        _biographyBox.Selected = false;
        _uniqueBox.Selected = false;
    }

    // ── 私有操作 ──────────────────────────────────────────────────────

    private void SwitchTab(int tab)
    {
        if (_activeTab == tab)
            return;
        // 当前 Tab 绑定已由 update() 持续回写，无需额外处理
        _activeTab = tab;
        Game1.playSound("smallSelect");
        Layout();
    }

    private void FocusBox(MultilineTextBox box)
    {
        _uniqueBox.Selected = false;
        Game1.keyboardDispatcher.Subscriber = box;
    }

    private void SaveAndClose()
    {
        if (!ModEntry.BioStorage!.SaveOverlay(_npcName, _bio, out string errorMessage))
        {
            Game1.addHUDMessage(new HUDMessage($"人设保存失败: {errorMessage}", HUDMessage.error_type));
            return;
        }
        Game1.playSound("achievement");
        ExitAndReturn();
    }

    private void ExitAndReturn()
    {
        if (_returnMenu != null)
        {
            Game1.activeClickableMenu = _returnMenu;
        }
        else
        {
            Game1.exitActiveMenu();
        }
    }

    private void TryCancel()
    {
        if (!_dirty)
        {
            ExitAndReturn();
            return;
        }

        Game1.activeClickableMenu = new ConfirmationDialog(
            "放弃未保存的修改？",
            _ =>
            {
                Game1.activeClickableMenu = this;
                ExitAndReturn();
            },
            _ =>
            {
                Game1.activeClickableMenu = this;
            });
    }

    private void TryReset()
    {
        if (!_hasOverlay)
        {
            Game1.playSound("cancel");
            return;
        }

        Game1.activeClickableMenu = new ConfirmationDialog(
            "恢复默认人设并删除覆盖文件？",
            _ =>
            {
                Game1.activeClickableMenu = this;
                if (!ModEntry.BioStorage!.ResetOverlay(_npcName, out string errReset))
                {
                    Game1.addHUDMessage(new HUDMessage($"还原失败: {errReset}", HUDMessage.error_type));
                    return;
                }
                _bio = ModEntry.BioStorage!.LoadEditableBio(_npcName);
                _dirty = false;
                _hasOverlay = false;
                // 重绑 Tab1 控件
                _biographyBox.Text = _bio.Biography ?? string.Empty;
                _uniqueBox.Text = _bio.Unique ?? string.Empty;
                _homeBedCheckbox.isChecked = _bio.HomeLocationBed;
                Game1.playSound("throw");
            },
            _ =>
            {
                Game1.activeClickableMenu = this;
            });
    }

    private void MarkDirty()
    {
        _dirty = true;
    }

    // ── 绘制 ──────────────────────────────────────────────────────────

    public override void draw(SpriteBatch b)
    {
        int mx = Game1.getMouseX();
        int my = Game1.getMouseY();

        // 背景遮罩
        b.Draw(Game1.fadeToBlackRect,
            Game1.graphics.GraphicsDevice.Viewport.Bounds, Color.Black * 0.45f);

        // 主面板
        Game1.drawDialogueBox(xPositionOnScreen, yPositionOnScreen, width, height, false, true);

        int contentLeft = xPositionOnScreen + PadX;
        int contentW = width - PadX * 2;

        // 标题
        string title = $"角色人设配置: {_npcName}";
        if (_dirty)
            title += " *";
        var titleSize = Game1.dialogueFont.MeasureString(title);
        b.DrawString(Game1.dialogueFont, title,
            new Vector2(xPositionOnScreen + (width - titleSize.X) / 2f, yPositionOnScreen + 12),
            Game1.textColor);

        // Tab 栏
        for (int i = 0; i < _tabRects.Length; i++)
        {
            DrawTab(b, _tabRects[i], TabTitles[i], _activeTab == i, mx, my);
        }

        // 分隔线
        int sepY = yPositionOnScreen + HeaderH + TabBarH + 4;
        b.Draw(Game1.staminaRect,
            new Rectangle(contentLeft, sepY, contentW, 2),
            Color.Gray * 0.4f);

        // Tab 内容
        if (_activeTab == 0)
        {
            DrawTab1(b, mx, my);
        }
        else
        {
            DrawPlaceholder(b, _activeTab);
        }

        // 页脚按钮
        DrawButton(b, _cancelRect, "取消", mx, my);
        DrawButton(b, _saveRect, "保存并应用", mx, my);
        DrawButton(b, _resetRect, "还原默认", mx, my);

        base.draw(b);
        drawMouse(b);
    }

    private void DrawTab(SpriteBatch b, Rectangle rect, string label, bool isActive, int mx, int my)
    {
        Color bg = isActive ? new Color(210, 180, 140)
                 : rect.Contains(mx, my) ? new Color(255, 235, 205)
                 : new Color(139, 90, 43);

        IClickableMenu.drawTextureBox(b, Game1.mouseCursors,
            new Rectangle(432, 439, 9, 9),
            rect.X, rect.Y, rect.Width, rect.Height, bg, 4f, false);

        var labelSize = Game1.smallFont.MeasureString(label);
        b.DrawString(Game1.smallFont, label,
            new Vector2(rect.X + (rect.Width - labelSize.X) / 2f,
                        rect.Y + (rect.Height - labelSize.Y) / 2f),
            isActive ? Game1.textColor : Color.White * 0.95f);
    }

    private void DrawTab1(SpriteBatch b, int mx, int my)
    {
        int bodyTop = _tabRects[0].Y + _tabRects[0].Height + 12;
        int bodyLeft = xPositionOnScreen + PadX;

        // Biography 标签
        string bioLabel = "身份与心理设定（保留 [IDENTITY]/[CORE CONFLICT] 等分节标记）";
        b.DrawString(Game1.smallFont, bioLabel,
            new Vector2(bodyLeft, bodyTop),
            Game1.textColor);
        int labelH = (int)Game1.smallFont.MeasureString(bioLabel).Y;

        // Biography 框（若 Layout 尚未同步 bounds 则使用已有 bounds）
        _biographyBox.Draw(b);

        // Unique 标签
        int uniqueLabelY = _biographyBox.Bounds.Y + _biographyBox.Bounds.Height + 6;
        b.DrawString(Game1.smallFont, "特征标记 Unique",
            new Vector2(bodyLeft, uniqueLabelY),
            Game1.textColor);

        // Unique 原生框
        DrawVanillaTextBox(b);

        // HomeBed 复选框（bounds 存绝对坐标，draw 偏移传 0,0）
        _homeBedCheckbox.draw(b, 0, 0, this);

        // 只读状态行
        int statusY = yPositionOnScreen + height - FooterH - 24;
        string status = _hasOverlay ? "自定义覆盖生效中" : "默认基准人设";
        Color statusColor = _hasOverlay ? new Color(60, 140, 60) : Color.Gray;
        if (_bio.Missing)
        {
            status = "该 NPC 无基线人设，编辑后保存即创建";
            statusColor = new Color(200, 140, 40);
        }
        b.DrawString(Game1.smallFont, status,
            new Vector2(bodyLeft, Math.Max(uniqueLabelY + 80, statusY)),
            statusColor);
    }

    private static Texture2D LoadTextBoxTexture()
    {
        try
        {
            var tex = Game1.content.Load<Texture2D>("LooseSprites\\textBox");
            if (tex != null)
                return tex;
        }
        catch
        {
            // 回退到 mouseCursors
        }
        return Game1.mouseCursors;
    }

    private void DrawVanillaTextBox(SpriteBatch b)
    {
        // 原生 TextBox 自绘（底板 + 文本 + 光标）
        _uniqueBox.Draw(b, true);
    }

    private void DrawPlaceholder(SpriteBatch b, int tab)
    {
        string msg = $"第 {tab + 1} 栏（{TabTitles[tab]}）建设中";
        var size = Game1.smallFont.MeasureString(msg);
        b.DrawString(Game1.smallFont, msg,
            new Vector2(xPositionOnScreen + (width - size.X) / 2f,
                        yPositionOnScreen + HeaderH + TabBarH + 40),
            Color.Gray);
    }

    private void DrawButton(SpriteBatch b, Rectangle rect, string label, int mx, int my)
    {
        Color bg = rect.Contains(mx, my) ? new Color(255, 235, 205) : new Color(210, 180, 140);
        IClickableMenu.drawTextureBox(b, Game1.mouseCursors,
            new Rectangle(432, 439, 9, 9),
            rect.X, rect.Y, rect.Width, rect.Height, bg, 4f, false);

        var size = Game1.smallFont.MeasureString(label);
        b.DrawString(Game1.smallFont, label,
            new Vector2(rect.X + (rect.Width - size.X) / 2f,
                        rect.Y + (rect.Height - size.Y) / 2f),
            Game1.textColor);
    }
}
