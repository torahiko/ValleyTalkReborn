#nullable enable
using System;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Menus;
using ValleytalkReborn.Services;
using ValleytalkReborn.Services.Overlays;

namespace ValleytalkReborn.UI;

/// <summary>
/// 传送踩点现场悬浮 HUD：
/// 1. 不阻断玩家自由移动，过图后悬浮在屏幕顶部；
/// 2. 完备的重入边界防御：同一坐标零延迟唤醒、会话覆盖重置；
/// 3. 实时环境通行探测 + 智能脱困算法；
/// 4. 视觉深度对齐星露谷羊皮纸底衬、红木外框与木质浮雕按钮体系。
/// </summary>
internal static class PoiInspectHud
{
    private static IModHelper? _helper;
    private static IMonitor? _monitor;

    public static bool IsActive { get; private set; }
    private static bool _isWaitingForWarp;

    private static string _poiId = "";
    private static string _poiDisplayName = "";
    private static string _targetMap = "";
    private static int _origTileX;
    private static int _origTileY;
    private static int _hubTab = 2;

    // 控件几何区域
    private static Rectangle _cardRect;
    private static Rectangle _btnCaptureAndTweak;
    private static Rectangle _btnConfirm;
    private static Rectangle _btnUnstuck;
    private static Rectangle _btnClose;

    public static void Initialize(IModHelper helper, IMonitor monitor)
    {
        _helper = helper;
        _monitor = monitor;

        helper.Events.Display.RenderedHud += OnRenderedHud;
        helper.Events.Input.ButtonPressed += OnButtonPressed;
        helper.Events.GameLoop.UpdateTicked += OnUpdateTicked;
    }

    /// <summary>
    /// 启动现场勘测会话（传送前调用，具备完全的重复重入防御）。
    /// </summary>
    public static void BeginSession(string poiId, string poiDisplayName, string targetMap, int origX, int origY, int hubTab = 2)
    {
        // 1. 强行截断旧会话，重置所有状态
        Close();

        _poiId = poiId;
        _poiDisplayName = poiDisplayName;
        _targetMap = targetMap;
        _origTileX = origX;
        _origTileY = origY;
        _hubTab = hubTab;

        // 2. 边界检测：若玩家肉身本就位于目标地图和坐标，不等待过图黑屏，直接原地唤醒
        if (Context.IsWorldReady &&
            Game1.currentLocation != null &&
            string.Equals(Game1.currentLocation.Name, targetMap, StringComparison.OrdinalIgnoreCase) &&
            Game1.player != null &&
            Game1.player.TilePoint == new Point(origX, origY))
        {
            _isWaitingForWarp = false;
            IsActive = true;
            Game1.playSound("shiny4");
            return;
        }

        _isWaitingForWarp = true;
        IsActive = false;
    }

    public static void Close()
    {
        IsActive = false;
        _isWaitingForWarp = false;
    }

    private static void OnUpdateTicked(object? sender, UpdateTickedEventArgs e)
    {
        if (!_isWaitingForWarp) return;

        // 判定黑屏淡入完全结束且玩家恢复自由控制
        if (Context.IsPlayerFree && !Game1.fadeToBlack && !Game1.eventUp)
        {
            _isWaitingForWarp = false;
            IsActive = true;
            Game1.playSound("shiny4");
        }
    }

    private static void OnRenderedHud(object? sender, RenderedHudEventArgs e)
    {
        if (!IsActive || !Context.IsWorldReady || Game1.activeClickableMenu != null)
            return;

        SpriteBatch b = e.SpriteBatch;
        int mx = Game1.getMouseX();
        int my = Game1.getMouseY();

        int cardW = 580;
        int cardH = 108;
        int cardX = (Game1.uiViewport.Width - cardW) / 2;
        int cardY = 16;
        _cardRect = new Rectangle(cardX, cardY, cardW, cardH);

        // 1. 底层大投影
        b.Draw(Game1.staminaRect, new Rectangle(_cardRect.X + 3, _cardRect.Y + 4, _cardRect.Width, _cardRect.Height), Color.Black * 0.35f);

        // 2. 外层经典木框（使用 4f 标准缩放，纯正星露谷风味）
        IClickableMenu.drawTextureBox(b, _cardRect.X - 6, _cardRect.Y - 6, _cardRect.Width + 12, _cardRect.Height + 12, Color.White);

        // 3. 羊皮纸双层平铺衬底（防止边缘漏底）
        Color parchmentColor = new Color(245, 230, 205);
        b.Draw(Game1.staminaRect, _cardRect, parchmentColor);
        b.Draw(
            Game1.menuTexture,
            _cardRect,
            new Rectangle(64, 128, 64, 64),
            parchmentColor
        );

        // 4. 第一行：当前勘测目标（左侧金棕胶囊徽记 + 碳黑标题）
        int contentX = _cardRect.X + 14;
        int topY = _cardRect.Y + 12;

        string titleBadge = I18n.PoiHud.TitleBadge();
        var badgeSz = CustomFontManager.MeasureStringBold(titleBadge, CustomFontManager.SizeSmall);
        var badgeRect = new Rectangle(contentX, topY, (int)badgeSz.X + 12, 22);

        b.Draw(Game1.staminaRect, badgeRect, new Color(175, 115, 45));
        IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(403, 383, 6, 6),
            badgeRect.X, badgeRect.Y, badgeRect.Width, badgeRect.Height, RulesTheme.BorderBold, 1.2f, false);
        CustomFontManager.DrawStringBold(b, titleBadge, new Vector2(badgeRect.X + 6, badgeRect.Y + 3), Color.White, CustomFontManager.SizeSmall);

        string title = _poiDisplayName;
        CustomFontManager.DrawStringBold(b, title, new Vector2(badgeRect.Right + 8, topY + 1), RulesTheme.TextCharcoal, CustomFontManager.SizeRegular);

        // 5. 第二行：玩家当前所处实时坐标与通行校验状态
        Point curTile = Game1.player.TilePoint;
        string curMap = Game1.currentLocation?.Name ?? I18n.PoiHud.UnknownMap();
        bool isPassable = PoiSamplingService.TryCaptureCurrentTile(out _, out _, out _, out string failureReason);

        int statusY = topY + 28;
        string locInfo = I18n.PoiHud.LocationLabel(curMap ?? string.Empty, curTile.X, curTile.Y);
        CustomFontManager.DrawString(b, locInfo, new Vector2(contentX + 2, statusY), RulesTheme.TextPrimary, CustomFontManager.SizeSmall);

        var locSz = CustomFontManager.MeasureString(locInfo, CustomFontManager.SizeSmall);
        string statusText = isPassable ? I18n.PoiHud.StatusPassable() : I18n.PoiHud.StatusBlocked(failureReason);
        Color statusColor = isPassable ? RulesTheme.AccentGreen : RulesTheme.AccentRed;
        CustomFontManager.DrawStringBold(b, statusText, new Vector2(contentX + 2 + locSz.X + 12, statusY), statusColor, CustomFontManager.SizeSmall);

        // 6. 第三行：操作按钮栏（木质按键风格）
        int btnH = 30;
        int btnY = _cardRect.Bottom - btnH - 12;
        int btnGap = 8;
        int btnW1 = 168; // 抓取并微调
        int btnW2 = 132; // 确认点位
        int btnW3 = 125; // 智能脱困
        int btnW4 = 85;  // 退出

        _btnCaptureAndTweak = new Rectangle(contentX, btnY, btnW1, btnH);
        _btnConfirm = new Rectangle(_btnCaptureAndTweak.Right + btnGap, btnY, btnW2, btnH);
        _btnUnstuck = new Rectangle(_btnConfirm.Right + btnGap, btnY, btnW3, btnH);
        _btnClose = new Rectangle(_btnUnstuck.Right + btnGap, btnY, btnW4, btnH);

        DrawWoodActionButton(b, _btnCaptureAndTweak, I18n.PoiHud.ButtonCapture(), _btnCaptureAndTweak.Contains(mx, my), isPassable, isPrimary: true);
        DrawWoodActionButton(b, _btnConfirm, I18n.PoiHud.ButtonConfirm(), _btnConfirm.Contains(mx, my), isPassable);
        DrawWoodActionButton(b, _btnUnstuck, I18n.PoiHud.ButtonUnstuck(), _btnUnstuck.Contains(mx, my), true, isPrimary: !isPassable);
        DrawWoodActionButton(b, _btnClose, I18n.PoiHud.ButtonClose(), _btnClose.Contains(mx, my), true, isDanger: true);
    }

    private static void OnButtonPressed(object? sender, ButtonPressedEventArgs e)
    {
        if (!IsActive || Game1.activeClickableMenu != null)
            return;

        if (e.Button != SButton.MouseLeft)
            return;

        int mx = Game1.getMouseX();
        int my = Game1.getMouseY();

        if (!_cardRect.Contains(mx, my))
            return;

        _helper?.Input.Suppress(e.Button);

        if (_btnCaptureAndTweak.Contains(mx, my))
        {
            HandleCaptureAndTweak();
            return;
        }

        if (_btnConfirm.Contains(mx, my))
        {
            HandleConfirmDirectly();
            return;
        }

        if (_btnUnstuck.Contains(mx, my))
        {
            HandleUnstuck();
            return;
        }

        if (_btnClose.Contains(mx, my))
        {
            Close();
            Game1.playSound("bigDeSelect");
            return;
        }
    }

    private static void HandleCaptureAndTweak()
    {
        if (!PoiSamplingService.TryCaptureCurrentTile(out string mapName, out int tx, out int ty, out string reason))
        {
            Game1.addHUDMessage(new HUDMessage(I18n.PoiHud.CaptureFailedHud(reason), HUDMessage.error_type));
            Game1.playSound("cancel");
            return;
        }

        Close();
        Game1.playSound("coin");

        PoiTuningPage.SetPendingContext(_poiId, mapName, tx, ty);
        ModEntry.OpenHubMenu(_hubTab);
    }

    private static void HandleConfirmDirectly()
    {
        if (!PoiSamplingService.TryCaptureCurrentTile(out string mapName, out int tx, out int ty, out string reason))
        {
            Game1.addHUDMessage(new HUDMessage(I18n.PoiHud.ConfirmFailedHud(reason), HUDMessage.error_type));
            Game1.playSound("cancel");
            return;
        }

        var service = ModEntry.PoiPreferenceOverlay;
        if (service != null)
        {
            var ov = service.LoadOrNull() ?? new PoiOverlayFile();
            if (ov.CustomPois.TryGetValue(_poiId, out var existing) && existing != null)
            {
                existing.MapName = mapName;
                existing.TargetTile = new PoiTile { X = tx, Y = ty };
            }
            else
            {
                ov.CustomPois[_poiId] = new PoiAsset
                {
                    MapName = mapName,
                    TargetTile = new PoiTile { X = tx, Y = ty },
                    Conditions = new PoiConditions(),
                    StayMinutes = 90,
                    DescriptionForLLM = "Villagers linger here peacefully and observe their surroundings.",
                    DescriptionForLLM_Zh = "村民在此悠闲驻足与观察四周。"
                };
            }

            ov.RemovedCustomPoiIds.Remove(_poiId);
            service.Save(ov, out _);
        }

        Close();
        Game1.playSound("achievement");
        Game1.addHUDMessage(new HUDMessage(I18n.PoiHud.ConfirmSuccessHud(_poiDisplayName, tx, ty), HUDMessage.newQuest_type));
    }

    private static void HandleUnstuck()
    {
        if (!Context.IsWorldReady || Game1.currentLocation == null || Game1.player == null)
            return;

        var loc = Game1.currentLocation;
        var center = Game1.player.TilePoint;

        Point? safeTile = FindNearestSafeTile(loc, center, maxRadius: 30);
        if (safeTile.HasValue)
        {
            Point target = safeTile.Value;
            Game1.player.Position = new Vector2(target.X * 64f, target.Y * 64f);
            Game1.player.Halt();

            Game1.playSound("wand");
            Game1.addHUDMessage(new HUDMessage(I18n.PoiHud.UnstuckSuccessHud(target.X, target.Y), HUDMessage.newQuest_type));
        }
        else
        {
            Game1.playSound("cancel");
            Game1.addHUDMessage(new HUDMessage(I18n.PoiHud.UnstuckFailedHud(), HUDMessage.error_type));
        }
    }

    private static Point? FindNearestSafeTile(GameLocation loc, Point center, int maxRadius = 30)
    {
        Point? bestPoint = null;
        float minDistanceSq = float.MaxValue;

        for (int r = 1; r <= maxRadius; r++)
        {
            for (int dx = -r; dx <= r; dx++)
            {
                for (int dy = -r; dy <= r; dy++)
                {
                    if (Math.Abs(dx) != r && Math.Abs(dy) != r)
                        continue;

                    int tx = center.X + dx;
                    int ty = center.Y + dy;

                    if (IsTilePassableAndSafe(loc, tx, ty))
                    {
                        float distSq = dx * dx + dy * dy;
                        if (distSq < minDistanceSq)
                        {
                            minDistanceSq = distSq;
                            bestPoint = new Point(tx, ty);
                        }
                    }
                }
            }

            if (bestPoint.HasValue)
                return bestPoint.Value;
        }

        return null;
    }

    private static bool IsTilePassableAndSafe(GameLocation loc, int x, int y)
    {
        if (loc == null || !loc.isTileOnMap(x, y))
            return false;

        var vec = new Vector2(x, y);
        if (!loc.isTilePassable(vec))
            return false;

        if (loc.IsTileOccupiedBy(vec, CollisionMask.Objects | CollisionMask.Furniture | CollisionMask.Buildings, CollisionMask.None, false))
            return false;

        if (loc.terrainFeatures.TryGetValue(vec, out var feature) && !feature.isPassable())
            return false;

        return true;
    }

    /// <summary>
    /// 标准木质浮雕按钮渲染（严格使用 2f 整像素比例与压边机制，彻底消除右下白缝）。
    /// </summary>
    private static void DrawWoodActionButton(SpriteBatch b, Rectangle rect, string label, bool isHover, bool isEnabled, bool isPrimary = false, bool isDanger = false)
    {
        bool isPressed = isEnabled && isHover && Mouse.GetState().LeftButton == ButtonState.Pressed;
        int pressOffset = isPressed ? 1 : 0;

        // 1. 按钮底色自适应
        Color bg;
        if (!isEnabled) bg = Color.LightGray * 0.65f;
        else if (isPrimary) bg = isHover ? Color.Gold : new Color(255, 220, 130);
        else if (isDanger) bg = isHover ? new Color(245, 105, 105) : new Color(210, 85, 80);
        else bg = isHover ? new Color(255, 240, 215) : new Color(225, 195, 155);

        // 2. 边框颜色
        Color border;
        if (!isEnabled) border = RulesTheme.BorderSoft;
        else if (isPrimary) border = new Color(210, 160, 60);
        else if (isDanger) border = new Color(175, 60, 55);
        else border = isHover ? RulesTheme.BorderBold : new Color(185, 150, 110);

        // 3. 底层微阴影
        if (!isPressed && isEnabled)
        {
            b.Draw(Game1.staminaRect, new Rectangle(rect.X + 2, rect.Y + 2, rect.Width, rect.Height), Color.Black * 0.2f);
        }

        var drawRect = new Rectangle(rect.X, rect.Y + pressOffset, rect.Width, rect.Height);

        // 4. ★ 核心防白缝：采用整像素 2f 缩放与 3px 安全内衬
        const float frameScale = 2f;
        const int fillInset = 3;

        // 4.1 先铺满一层与边框/深底色一致的防漏底垫，即使有半像素空隙也绝不会透出白色
        b.Draw(Game1.staminaRect, drawRect, border * 0.5f);

        // 4.2 内部纯色内衬（内缩 fillInset 像素，紧密锁死在 432 九宫格内边缘）
        b.Draw(Game1.staminaRect,
            new Rectangle(drawRect.X + fillInset, drawRect.Y + fillInset,
                Math.Max(0, drawRect.Width - fillInset * 2), Math.Max(0, drawRect.Height - fillInset * 2)),
            bg);

        // 4.3 绘制 432 九宫格外框（严禁浮点数，强制 2f 整数缩放）
        IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(432, 439, 9, 9),
            drawRect.X, drawRect.Y, drawRect.Width, drawRect.Height, border, frameScale, false);

        // 5. 文字颜色与居中
        Color btnTextCol = !isEnabled ? RulesTheme.TextMuted
            : isDanger ? new Color(255, 250, 242)
            : RulesTheme.TextCharcoal;

        ButtonTextRenderer.DrawButtonText(b, label, drawRect, btnTextCol, useBold: true);
    }
} 