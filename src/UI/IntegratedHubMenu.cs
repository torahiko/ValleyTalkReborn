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
using ValleytalkReborn.UI;

namespace ValleytalkReborn
{
    /// <summary>
    /// 三合一综合管理面板：规则 / 农夫档案 / 世界设置 / 高级设置。
    /// 实现 IMemoryRefreshTarget，使子对话框返回后可通过接口刷新列表。
    /// </summary>
    internal class IntegratedHubMenu : IClickableMenu, IMemoryRefreshTarget
    {
        // ── 布局常量 ──────────────────────────────────────────────────
        private const int TabBarY = 56;
        private const int TabHeight = 36;
        private const int TabGap = 8;
        private const int TopPadding = 110;
        private const int BottomPadding = 75;
        private const int LineHeight = 46;
        private const int ButtonSize = 32;
        private const int LeftPadding = 40;
        private const int RightPadding = 40;

        // 统一整数字号标准（杜绝亚像素采样模糊）
        private const float TitleFontSize = CustomFontManager.SizeTitle;       // 24f Bold (顶栏大标题)
        private const float TabFontSize = CustomFontManager.SizeRegular;       // 18f Bold (Tab、分段条、按钮文字)
        private const float CardTitleFontSize = CustomFontManager.SizeRegular; // 18f Bold (卡片名字、行标头)
        private const float RegularFontSize = CustomFontManager.SizeRegular;   // 18f Medium (正文、描述)
        private const float SmallFontSize = CustomFontManager.SizeSmall;       // 15f Medium (时间、角标、说明)

        // ── 状态 ────────────────────────────────────────────────────────
        private int _currentTab;
        private string _currentNpcName;
        private bool _wasObscured;

        private readonly Rectangle[] _tabRects = new Rectangle[4];
        private ClickableTextureComponent _closeButton;
        private float _closeButtonHoverScale;
        private readonly float _closeButtonBaseScale;

        private readonly IHubTabView?[] _tabViews = new IHubTabView?[4];

        // 暴露给 View 的属性
        public string CurrentNpcName
        {
            get => _currentNpcName;
            set => _currentNpcName = value;
        }
        public int CurrentTab => _currentTab;

        public IntegratedHubMenu(string initialNpcName, int initialTab)
        {
            width = Math.Max(700, Math.Min(1000, Game1.uiViewport.Width - 80));
            height = Math.Max(520, Math.Min(680, Game1.uiViewport.Height - 80));
            xPositionOnScreen = (Game1.uiViewport.Width - width) / 2;
            yPositionOnScreen = (Game1.uiViewport.Height - height) / 2;

            _closeButton = new ClickableTextureComponent(
                new Rectangle(xPositionOnScreen + width - 60, yPositionOnScreen + 16, 44, 44),
                Game1.mouseCursors, new Rectangle(337, 494, 12, 12), 3.5f);
            _closeButtonBaseScale = 3.5f;
            _closeButton.hoverText = I18n.Memory.CloseButton();

            if (!string.IsNullOrWhiteSpace(initialNpcName))
                _currentNpcName = initialNpcName;
            else
            {
                _currentNpcName = DialogueHistoryManager.Instance.GetMostRecentNpc();
                if (string.IsNullOrWhiteSpace(_currentNpcName))
                    _currentNpcName = Game1.player?.friendshipData?.Keys.FirstOrDefault() ?? "";
            }

            _tabViews[0] = new RulesTabView(this);
            _tabViews[1] = new ProfileTabView(this);
            _tabViews[2] = new WorldSettingsTabView(this);
            _tabViews[3] = new AdvancedSettingsTabView(this);

            RecalculateAllLayout();

            exitFunction = () => Game1.playSound("bigDeSelect");

            _currentTab = Math.Clamp(initialTab, 0, 3);
            RefreshEntries();
        }

        private void RecalculateAllLayout()
        {
            int tabBaseX = xPositionOnScreen + LeftPadding;
            int tabBaseY = yPositionOnScreen + TabBarY;

            int totalTabSpace = width - LeftPadding - RightPadding;
            int tabW = (totalTabSpace - (TabGap * 3)) / 4;
            for (int i = 0; i < 4; i++)
            {
                _tabRects[i] = new Rectangle(tabBaseX + i * (tabW + TabGap), tabBaseY, tabW, TabHeight);
            }

            // F1: 视图布局接线（各视图在 Layout 中计算自身矩形）
            var menuBounds = new Rectangle(xPositionOnScreen, yPositionOnScreen, width, height);
            var contentBounds = new Rectangle(xPositionOnScreen + LeftPadding, yPositionOnScreen + TopPadding,
                                              width - LeftPadding - RightPadding, height - TopPadding - BottomPadding);
            for (int i = 0; i < _tabViews.Length; i++)
                _tabViews[i]?.Layout(menuBounds, contentBounds);
        }

        // ── 数据与刷新 ──────────────────────────────────────────────────

        public void RefreshEntries()
        {
            if (_currentTab == 0)
                _tabViews[0]!.RefreshFromHub();
            else if (_currentTab == 1)
                _tabViews[1]!.RefreshFromHub();
            else if (_currentTab == 2)
                _tabViews[2]!.RefreshFromHub();
            else
                _tabViews[3]!.RefreshFromHub();
        }

        private void SwitchTab(int tab)
        {
            if (_currentTab == tab) return;

            // F5: OnDeactivated 先于赋值（视图自关下拉/释放键盘）
            _tabViews[_currentTab]?.OnDeactivated();

            _currentTab = tab;
            Game1.playSound("smallSelect");
            RecalculateAllLayout();
            _tabViews[tab]?.OnActivated();
            RefreshEntries();
        }

        internal void ReleaseKeyboard()
        {
            // bio 失焦由 ProfileTabView 内联处理；Tab0/1 打开子菜单时原语义即为 no-op
        }

        internal void NotifyWillBeObscured() => _wasObscured = true;

        // ── 输入处理 ────────────────────────────────────────────────────

        public override void receiveScrollWheelAction(int direction)
        {
            base.receiveScrollWheelAction(direction);

            if (_tabViews[_currentTab]?.ReceiveScrollWheel(direction) == true)
                return;
        }

        public override void receiveLeftClick(int x, int y, bool playSound = true)
        {
            base.receiveLeftClick(x, y, playSound);

            if (_closeButton.containsPoint(x, y))
            {
                Game1.playSound("bigDeSelect");
                RequestExit();
                return;
            }

            for (int t = 0; t < 4; t++)
            {
                if (_tabRects[t].Contains(x, y))
                {
                    SwitchTab(t);
                    return;
                }
            }

            _tabViews[_currentTab]?.ReceiveLeftClick(x, y);
        }

        public override void leftClickHeld(int x, int y)
        {
            base.leftClickHeld(x, y);
            _tabViews[_currentTab]?.LeftClickHeld(x, y);
        }

        public override void releaseLeftClick(int x, int y)
        {
            base.releaseLeftClick(x, y);
            _tabViews[_currentTab]?.ReleaseLeftClick(x, y);
        }

        public override void receiveKeyPress(Keys key)
        {
            if (_tabViews[_currentTab]?.ReceiveKeyPress(key) == true)
                return;

            if (key == Keys.Escape)
            {
                Game1.playSound("bigDeSelect");
                RequestExit();
                return;
            }

            base.receiveKeyPress(key);
        }

        private void RequestExit()
        {
            if (_tabViews[2] is HubTabViewBase ws && ws.HasUnsavedChanges)
            {
                Game1.activeClickableMenu = new ConfirmationDialog(
                    "世界设定中存在未保存的修改，退出后将丢失。仍要退出吗？",
                    _ =>
                    {
                        Game1.activeClickableMenu = this;
                        exitThisMenu(playSound: false);
                    },
                    _ => { Game1.activeClickableMenu = this; }
                );
                return;
            }
            exitThisMenu();
        }

        // ── 绘制 ────────────────────────────────────────────────────────

        public override void draw(SpriteBatch b)
        {
            int mx = Game1.getMouseX();
            int my = Game1.getMouseY();

            b.Draw(Game1.fadeToBlackRect,
                Game1.graphics.GraphicsDevice.Viewport.Bounds, Color.Black * 0.45f);

            IClickableMenu.drawTextureBox(b,
                xPositionOnScreen - 16, yPositionOnScreen - 16,
                width + 32, height + 32, Color.White);

            IClickableMenu.drawTextureBox(b,
                xPositionOnScreen - 8, yPositionOnScreen - 8,
                width + 16, height + 16, Color.White);

            Game1.drawDialogueBox(xPositionOnScreen, yPositionOnScreen, width, height, false, true);

            // 大标题坐标强制整像素对齐
            string title = I18n.Hub.Title();
            var titleSize = CustomFontManager.MeasureStringBold(title, TitleFontSize);
            Vector2 titlePos = new Vector2(
                (int)MathF.Round(xPositionOnScreen + (width - titleSize.X) / 2f),
                yPositionOnScreen + 12
            );
            CustomFontManager.DrawStringBold(b, title, titlePos, Game1.textColor, TitleFontSize);

            DrawTab(b, _tabRects[0], I18n.Hub.TabRules(), _currentTab == 0, mx, my);
            DrawTab(b, _tabRects[1], I18n.Hub.TabProfile(), _currentTab == 1, mx, my);
            DrawTab(b, _tabRects[2], I18n.Hub.TabWorldSettings(), _currentTab == 2, mx, my);
            DrawTab(b, _tabRects[3], I18n.Hub.TabAdvanced(), _currentTab == 3, mx, my);

            b.Draw(Game1.staminaRect,
                new Rectangle(xPositionOnScreen + LeftPadding,
                              yPositionOnScreen + TabBarY + TabHeight + 4,
                              width - LeftPadding - RightPadding, 2),
                Color.Gray * 0.4f);

            if (_currentTab == 0)
                _tabViews[0]!.Draw(b, mx, my);
            else if (_currentTab == 1)
                _tabViews[1]!.Draw(b, mx, my);
            else if (_currentTab == 2)
                _tabViews[2]!.Draw(b, mx, my);
            else
                _tabViews[3]!.Draw(b, mx, my);

            UiHelper.UpdateButtonScale(ref _closeButtonHoverScale, _closeButton, mx, my);
            _closeButton.scale = _closeButtonBaseScale * _closeButtonHoverScale;
            _closeButton.draw(b);

            // Tab 页顶层弹层
            _tabViews[_currentTab]?.DrawOverlay(b);

            // 悬停提示
            var tip = _tabViews[_currentTab]?.HoveredTooltip;
            if (!string.IsNullOrEmpty(tip))
                HubUi.DrawHoverTextCustom(b, tip);

            base.draw(b);
            drawMouse(b);
        }

        private void DrawTab(SpriteBatch b, Rectangle rect, string label, bool isActive, int mx, int my)
        {
            Color bg = isActive ? new Color(210, 180, 140)
                     : rect.Contains(mx, my) ? new Color(255, 235, 205)
                     : new Color(139, 90, 43);

            b.Draw(Game1.staminaRect, new Rectangle(rect.X + 2, rect.Y + 2, rect.Width - 4, rect.Height - 4), bg);
            IClickableMenu.drawTextureBox(b, Game1.mouseCursors,
                new Rectangle(432, 439, 9, 9),
                rect.X, rect.Y, rect.Width, rect.Height, bg, 4f, false);

            var labelSize = CustomFontManager.MeasureStringBold(label, TabFontSize);
            Vector2 textPos = new Vector2(
                (int)MathF.Round(rect.X + (rect.Width - labelSize.X) / 2f),
                (int)MathF.Round(rect.Y + (rect.Height - labelSize.Y) / 2f)
            );

            CustomFontManager.DrawStringBold(b, label, textPos,
                isActive ? Game1.textColor : Color.White * 0.95f, TabFontSize);
        }

        public override void update(GameTime time)
        {
            base.update(time);
            _tabViews[_currentTab]?.Update(time);

            bool obscured = Game1.activeClickableMenu != this;
            if (_wasObscured && !obscured && _currentTab == 1)
                _tabViews[1]!.OnReturnedFromChild();
            _wasObscured = obscured;
        }

        public override void gameWindowSizeChanged(Rectangle oldBounds, Rectangle newBounds)
        {
            width = Math.Max(700, Math.Min(1000, Game1.uiViewport.Width - 80));
            height = Math.Max(520, Math.Min(680, Game1.uiViewport.Height - 80));
            xPositionOnScreen = (Game1.uiViewport.Width - width) / 2;
            yPositionOnScreen = (Game1.uiViewport.Height - height) / 2;

            _closeButton.bounds = new Rectangle(xPositionOnScreen + width - 60, yPositionOnScreen + 16, 44, 44);

            RecalculateAllLayout();
            RefreshEntries();
        }

        protected override void cleanupBeforeExit()
        {
            base.cleanupBeforeExit();
            _tabViews[_currentTab]?.OnDeactivated();
        }
    }
}