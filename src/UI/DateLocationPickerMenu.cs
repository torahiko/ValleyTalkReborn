using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewValley;
using StardewValley.BellsAndWhistles;
using StardewValley.Menus;

namespace ValleytalkReborn
{
    public class DateLocationPickerMenu : IClickableMenu
    {
        private readonly NPC _targetNpc;
        private readonly List<DateLocationInfo> _availableLocations;
        private readonly List<ClickableComponent> _locationCards = new();

        private ClickableTextureComponent _prevPageButton;
        private ClickableTextureComponent _nextPageButton;

        private const int ItemsPerPage = 4;
        private int _currentPage = 0;
        private int _hoveredIndex = -1;
        private int _selectedIndex = -1;

        /// <summary>
        /// 卡片视图缓存（状态驱动）。在 UpdateCardLayout() 时一次性计算可用性、
        /// 锁定原因与折行描述，draw() 直接读取，消除每帧重复校验与字符串分配。
        /// </summary>
        private struct CardViewData
        {
            public DateLocationInfo Info;
            public bool IsAvailable;
            public string LockedReason;
            public string DescriptionText;
        }
        private readonly List<CardViewData> _currentPagedCards = new();

        public DateLocationPickerMenu(NPC npc)
        {
            _targetNpc = npc;
            _availableLocations = DateLocationRegistry.Locations.Values.ToList();

            // 🔧 清理正在排队/后台生成的 Bark / A2A 任务，防止打开菜单时被并发气泡顶掉
            DynamicBarkManager.CancelBackgroundTasks(npc.Name);

            width = 800;
            height = 560;
            Vector2 centeringOnScreen = Utility.getTopLeftPositionForCenteringOnScreen(width, height);
            xPositionOnScreen = (int)centeringOnScreen.X;
            yPositionOnScreen = (int)centeringOnScreen.Y;

            initializeUpperRightCloseButton();

            _prevPageButton = new ClickableTextureComponent(
                new Rectangle(xPositionOnScreen + 30, yPositionOnScreen + height - 55, 48, 44),
                Game1.mouseCursors,
                new Rectangle(352, 495, 12, 11),
                4f);

            _nextPageButton = new ClickableTextureComponent(
                new Rectangle(xPositionOnScreen + width - 78, yPositionOnScreen + height - 55, 48, 44),
                Game1.mouseCursors,
                new Rectangle(365, 495, 12, 11),
                4f);

            UpdateCardLayout();
        }

        private void UpdateCardLayout()
        {
            _locationCards.Clear();
            _currentPagedCards.Clear();
            int startY = yPositionOnScreen + 95;

            var pageItems = _availableLocations
                .Skip(_currentPage * ItemsPerPage)
                .Take(ItemsPerPage)
                .ToList();

            bool isZh = IsZhLanguage;
            int cardInnerWidth = width - 160; // 预留左右边距与时间徽章宽度，防止描述文本穿出卡片

            for (int i = 0; i < pageItems.Count; i++)
            {
                var info = pageItems[i];
                _locationCards.Add(new ClickableComponent(
                    new Rectangle(xPositionOnScreen + 40, startY + i * 95, width - 80, 85),
                    info.LocationId
                ));

                // ── 状态驱动：一次性计算可用性、锁定原因与折行描述，供 draw() 直接读取 ──
                bool isAvailable = info.IsAvailable(_targetNpc, out string lockedReason);
                string rawDescription = isAvailable
                    ? (isZh ? info.ContextDescriptionZh : info.ContextDescriptionEn)
                    : (isZh ? $"[不可用: {lockedReason}]" : $"[Locked: {lockedReason}]");
                string wrappedDesc = Game1.parseText(rawDescription, Game1.smallFont, cardInnerWidth);

                _currentPagedCards.Add(new CardViewData
                {
                    Info = info,
                    IsAvailable = isAvailable,
                    LockedReason = lockedReason,
                    DescriptionText = wrappedDesc
                });
            }

            // 页切换后重置选中索引，防止越界
            _selectedIndex = -1;
        }

        /// <summary>当前是否为中文语言环境（缓存供布局与绘制复用）。</summary>
        private bool IsZhLanguage =>
            LocalizedContentManager.CurrentLanguageCode == LocalizedContentManager.LanguageCode.zh;

        public override void performHoverAction(int x, int y)
        {
            base.performHoverAction(x, y);
            _hoveredIndex = -1;

            for (int i = 0; i < _locationCards.Count; i++)
            {
                if (_locationCards[i].containsPoint(x, y))
                {
                    _hoveredIndex = i;
                    break;
                }
            }

            _prevPageButton.tryHover(x, y);
            _nextPageButton.tryHover(x, y);
        }

        public override void receiveLeftClick(int x, int y, bool playSound = true)
        {
            base.receiveLeftClick(x, y, playSound);

            if (_currentPage > 0 && _prevPageButton.containsPoint(x, y))
            {
                _currentPage--;
                Game1.playSound("shwip");
                UpdateCardLayout();
                return;
            }

            int maxPage = (_availableLocations.Count - 1) / ItemsPerPage;
            if (_currentPage < maxPage && _nextPageButton.containsPoint(x, y))
            {
                _currentPage++;
                Game1.playSound("shwip");
                UpdateCardLayout();
                return;
            }

            // 点击判定直接遍历卡片做命中检测，避免依赖 _hoveredIndex（手柄/触摸屏/快速点击均可靠）
            int clickedIndex = -1;
            for (int i = 0; i < _locationCards.Count; i++)
            {
                if (_locationCards[i].containsPoint(x, y))
                {
                    clickedIndex = i;
                    break;
                }
            }

            if (clickedIndex >= 0 && clickedIndex < _currentPagedCards.Count)
            {
                AttemptSelectCard(clickedIndex);
            }
        }

        /// <summary>
        /// 尝试选中指定索引的卡片：若地点不可用则提示锁定原因，否则敲定约会。
        /// 供鼠标点击、键盘回车、手柄 A 键统一调用。
        /// </summary>
        private void AttemptSelectCard(int index)
        {
            if (index < 0 || index >= _currentPagedCards.Count)
                return;

            var view = _currentPagedCards[index];
            if (!view.IsAvailable)
            {
                Game1.playSound("cancel");
                string reason = view.LockedReason ?? (IsZhLanguage ? "该地点暂不可用" : "This location is currently unavailable");
                Game1.showRedMessage(reason);
                return;
            }

            Game1.playSound("drumkit6");
            OnLocationSelected(view.Info.LocationId);
            exitThisMenu();
        }

        // ══════════════════════════════════════════════════════════════════════
        //  键盘与手柄导航支持（主机/手柄党核心体验）
        // ══════════════════════════════════════════════════════════════════════
        /// <summary>允许本菜单接管键盘/手柄光标移动（禁用游戏默认的 snappy 光标吸附）。</summary>
        public override bool overrideSnappyMenuCursorMovementBan() => true;

        public override void receiveKeyPress(Keys key)
        {
            base.receiveKeyPress(key);

            int pageCount = _currentPagedCards.Count;

            // Esc 快捷退出
            if (key == Keys.Escape)
            {
                exitThisMenu();
                return;
            }

            // 翻页：左/右方向键或 PageUp/PageDown
            int maxPage = (_availableLocations.Count - 1) / ItemsPerPage;
            if (key == Keys.Left || key == Keys.PageUp)
            {
                if (_currentPage > 0)
                {
                    _currentPage--;
                    Game1.playSound("shwip");
                    UpdateCardLayout();
                }
                return;
            }
            if (key == Keys.Right || key == Keys.PageDown)
            {
                if (_currentPage < maxPage)
                {
                    _currentPage++;
                    Game1.playSound("shwip");
                    UpdateCardLayout();
                }
                return;
            }

            if (pageCount == 0)
                return;

            // 上下方向键切换选中卡片
            if (key == Keys.Up)
            {
                if (_selectedIndex <= 0)
                    _selectedIndex = pageCount - 1;
                else
                    _selectedIndex--;
                Game1.playSound("smallSelect");
                return;
            }
            if (key == Keys.Down)
            {
                if (_selectedIndex < 0 || _selectedIndex >= pageCount - 1)
                    _selectedIndex = 0;
                else
                    _selectedIndex++;
                Game1.playSound("smallSelect");
                return;
            }

            // Enter / Space 确认选中
            if (key == Keys.Enter || key == Keys.Space)
            {
                if (_selectedIndex < 0)
                    _selectedIndex = 0;
                AttemptSelectCard(_selectedIndex);
                return;
            }
        }

        public override void receiveGamePadButton(Buttons button)
        {
            base.receiveGamePadButton(button);

            int pageCount = _currentPagedCards.Count;

            // B 键 / Start 退出
            if (button == Buttons.B || button == Buttons.Start)
            {
                exitThisMenu();
                return;
            }

            // 翻页：LB / RB
            int maxPage = (_availableLocations.Count - 1) / ItemsPerPage;
            if (button == Buttons.LeftTrigger)
            {
                if (_currentPage > 0)
                {
                    _currentPage--;
                    Game1.playSound("shwip");
                    UpdateCardLayout();
                }
                return;
            }
            if (button == Buttons.RightTrigger)
            {
                if (_currentPage < maxPage)
                {
                    _currentPage++;
                    Game1.playSound("shwip");
                    UpdateCardLayout();
                }
                return;
            }

            if (pageCount == 0)
                return;

            // 十字键上下切换选中卡片
            if (button == Buttons.DPadUp || button == Buttons.LeftThumbstickUp)
            {
                if (_selectedIndex <= 0)
                    _selectedIndex = pageCount - 1;
                else
                    _selectedIndex--;
                Game1.playSound("smallSelect");
                return;
            }
            if (button == Buttons.DPadDown || button == Buttons.LeftThumbstickDown)
            {
                if (_selectedIndex < 0 || _selectedIndex >= pageCount - 1)
                    _selectedIndex = 0;
                else
                    _selectedIndex++;
                Game1.playSound("smallSelect");
                return;
            }

            // A 键确认选中
            if (button == Buttons.A)
            {
                if (_selectedIndex < 0)
                    _selectedIndex = 0;
                AttemptSelectCard(_selectedIndex);
                return;
            }
        }

        private void OnLocationSelected(string locationId)
        {
            bool isZh = LocalizedContentManager.CurrentLanguageCode == LocalizedContentManager.LanguageCode.zh;

            bool scheduled = DateManager.Instance.TryScheduleDate(
                _targetNpc,
                locationId,
                DateManager.DateOrigin.PlayerInitiated);

            if (scheduled)
            {
                _targetNpc.doEmote(32);

                string locDisplayName = DateLocationRegistry.Locations.TryGetValue(locationId, out var info)
                    ? (isZh ? info.DisplayNameZh : info.DisplayNameEn)
                    : locationId;

                string alert = isZh
                    ? $"已约定今晚与 {_targetNpc.displayName} 在【{locDisplayName}】见面！(18:00~21:30)"
                    : $"Date confirmed with {_targetNpc.displayName} at {locDisplayName}! (18:00~21:30)";
                Game1.showGlobalMessage(alert);

                string farewellLine = isZh
                    ? $"那就一言为定，今晚在{locDisplayName}见，不见不散！$h"
                    : $"It's a date! See you at {locDisplayName} tonight!$h";

                _targetNpc.CurrentDialogue.Clear();
                _targetNpc.CurrentDialogue.Push(new Dialogue(_targetNpc, null, farewellLine));
                Game1.drawDialogue(_targetNpc);
            }
        }

        public override void draw(SpriteBatch b)
        {
            bool isZh = IsZhLanguage;

            b.Draw(Game1.fadeToBlackRect, Game1.graphics.GraphicsDevice.Viewport.Bounds, Color.Black * 0.5f);

            drawTextureBox(b, Game1.menuTexture, new Rectangle(0, 256, 60, 60),
                xPositionOnScreen, yPositionOnScreen, width, height, Color.White);

            // 标题：使用 dialogueFont 居中绘制（中文环境下 SpriteText 易出现测量空白/乱码）
            string title = isZh ? $"选择今晚与 {_targetNpc.displayName} 赴约的地点" : $"Date with {_targetNpc.displayName}";
            Vector2 titleSize = Game1.dialogueFont.MeasureString(title);
            Vector2 titlePos = new Vector2(
                xPositionOnScreen + (width - titleSize.X) / 2f,
                yPositionOnScreen + 30);
            Utility.drawTextWithShadow(b, title, Game1.dialogueFont, titlePos, Game1.textColor);

            // ── 从视图缓存直接读取，消除每帧重复 IsAvailable 校验与字符串分配 ──
            for (int i = 0; i < _locationCards.Count; i++)
            {
                var card = _locationCards[i];
                var view = _currentPagedCards[i];
                bool isHovered = (_hoveredIndex == i);
                bool isSelected = (_selectedIndex == i);

                // 手柄/键盘选中高亮边框
                if (isSelected)
                {
                    IClickableMenu.drawTextureBox(b, Game1.menuTexture, new Rectangle(0, 256, 60, 60),
                        card.bounds.X - 4, card.bounds.Y - 4, card.bounds.Width + 8, card.bounds.Height + 8,
                        Color.Gold * 0.7f);
                }

                Color boxColor = !view.IsAvailable ? (Color.Gray * 0.7f) : (isHovered ? Color.Wheat : Color.White);
                drawTextureBox(b, Game1.menuTexture, new Rectangle(0, 256, 60, 60),
                    card.bounds.X, card.bounds.Y, card.bounds.Width, card.bounds.Height, boxColor);

                string displayName = isZh ? view.Info.DisplayNameZh : view.Info.DisplayNameEn;

                Utility.drawTextWithShadow(b, displayName, Game1.dialogueFont,
                    new Vector2(card.bounds.X + 20, card.bounds.Y + 12),
                    view.IsAvailable ? (isHovered ? Game1.textColor : new Color(64, 32, 16)) : Color.DarkRed);

                // 折行后的描述文本（Game1.parseText 已处理换行）
                Utility.drawTextWithShadow(b, view.DescriptionText, Game1.smallFont,
                    new Vector2(card.bounds.X + 22, card.bounds.Y + 48),
                    view.IsAvailable ? Color.DarkSlateGray : Color.DimGray);

                if (view.IsAvailable)
                {
                    Utility.drawTextWithShadow(b, "18:00 - 21:30", Game1.tinyFont,
                        new Vector2(card.bounds.Right - 110, card.bounds.Y + 16),
                        new Color(180, 80, 30));
                }
            }

            int maxPage = (_availableLocations.Count - 1) / ItemsPerPage;
            if (_currentPage > 0) _prevPageButton.draw(b);
            if (_currentPage < maxPage) _nextPageButton.draw(b);

            if (maxPage > 0)
            {
                string pageStr = $"{_currentPage + 1} / {maxPage + 1}";
                Vector2 textSize = Game1.smallFont.MeasureString(pageStr);
                Utility.drawTextWithShadow(b, pageStr, Game1.smallFont,
                    new Vector2(xPositionOnScreen + (width - textSize.X) / 2, yPositionOnScreen + height - 46),
                    Game1.textColor);
            }

            base.draw(b);
            drawMouse(b);
        }
    }
}
