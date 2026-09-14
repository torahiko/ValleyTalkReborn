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

        private ClickableTextureComponent _closeButton;
        private ClickableTextureComponent _prevPageButton;
        private ClickableTextureComponent _nextPageButton;

        private float _closeHoverScale;
        private float _prevHoverScale;
        private float _nextHoverScale;

        private const float CloseButtonBaseScale = 3.5f;
        private const float ArrowButtonBaseScale = 4f;

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

            // 清理排队/后台生成的 Bark / A2A 任务，防止打开菜单时被气泡顶掉
            DynamicBarkManager.CancelBackgroundTasks(npc.Name);

            RecalculateDimensions();
            UpdateCardLayout();

            exitFunction = () => Game1.playSound("bigDeSelect");
        }

        private void RecalculateDimensions()
        {
            width = Math.Max(720, Math.Min(840, Game1.uiViewport.Width - 80));
            height = Math.Max(520, Math.Min(580, Game1.uiViewport.Height - 80));
            xPositionOnScreen = (Game1.uiViewport.Width - width) / 2;
            yPositionOnScreen = (Game1.uiViewport.Height - height) / 2;

            // 关闭按钮（与 IntegratedHubMenu 保持一致）
            _closeButton = new ClickableTextureComponent(
                new Rectangle(xPositionOnScreen + width - 60, yPositionOnScreen + 16, 44, 44),
                Game1.mouseCursors,
                new Rectangle(337, 494, 12, 12),
                CloseButtonBaseScale)
            {
                hoverText = Game1.content.LoadString("Strings\\UI:ItemHover_Close")
            };

            // 底部翻页按钮
            _prevPageButton = new ClickableTextureComponent(
                new Rectangle(xPositionOnScreen + 44, yPositionOnScreen + height - 56, 48, 44),
                Game1.mouseCursors,
                new Rectangle(352, 495, 12, 11),
                ArrowButtonBaseScale);

            _nextPageButton = new ClickableTextureComponent(
                new Rectangle(xPositionOnScreen + width - 92, yPositionOnScreen + height - 56, 48, 44),
                Game1.mouseCursors,
                new Rectangle(365, 495, 12, 11),
                ArrowButtonBaseScale);
        }

        private void UpdateCardLayout()
        {
            _locationCards.Clear();
            _currentPagedCards.Clear();

            int startY = yPositionOnScreen + 74;
            int cardH = 88;
            int cardGap = 12;

            var pageItems = _availableLocations
                .Skip(_currentPage * ItemsPerPage)
                .Take(ItemsPerPage)
                .ToList();

            bool isZh = IsZhLanguage;
            int cardW = width - 80;
            // 预留右侧时间胶囊空间与内边距
            int wrapWidth = (int)((cardW - 170) / 0.85f);

            for (int i = 0; i < pageItems.Count; i++)
            {
                var info = pageItems[i];
                _locationCards.Add(new ClickableComponent(
                    new Rectangle(xPositionOnScreen + 40, startY + i * (cardH + cardGap), cardW, cardH),
                    info.LocationId
                ));

                bool isAvailable = info.IsAvailable(_targetNpc, out string lockedReason);
                string rawDescription = isAvailable
                    ? (isZh ? info.ContextDescriptionZh : info.ContextDescriptionEn)
                    : (isZh ? $"[不可用: {lockedReason}]" : $"[Locked: {lockedReason}]");

                string wrappedDesc = Game1.parseText(rawDescription, Game1.smallFont, wrapWidth);

                _currentPagedCards.Add(new CardViewData
                {
                    Info = info,
                    IsAvailable = isAvailable,
                    LockedReason = lockedReason,
                    DescriptionText = wrappedDesc
                });
            }

            _selectedIndex = -1;
        }

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
        }

        public override void receiveLeftClick(int x, int y, bool playSound = true)
        {
            base.receiveLeftClick(x, y, playSound);

            if (_closeButton.containsPoint(x, y))
            {
                Game1.playSound("bigDeSelect");
                exitThisMenu();
                return;
            }

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

        public override bool overrideSnappyMenuCursorMovementBan() => true;

        public override void receiveKeyPress(Keys key)
        {
            base.receiveKeyPress(key);

            int pageCount = _currentPagedCards.Count;

            if (key == Keys.Escape)
            {
                exitThisMenu();
                return;
            }

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

            if (button == Buttons.B || button == Buttons.Start)
            {
                exitThisMenu();
                return;
            }

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

        public override void gameWindowSizeChanged(Rectangle oldBounds, Rectangle newBounds)
        {
            base.gameWindowSizeChanged(oldBounds, newBounds);
            RecalculateDimensions();
            UpdateCardLayout();
        }

        public override void draw(SpriteBatch b)
        {
            bool isZh = IsZhLanguage;
            int mx = Game1.getMouseX();
            int my = Game1.getMouseY();

            // 1. 背景暗化遮罩
            b.Draw(Game1.fadeToBlackRect, Game1.graphics.GraphicsDevice.Viewport.Bounds, Color.Black * 0.45f);

            // 2. 双层木框外边框
            IClickableMenu.drawTextureBox(b, xPositionOnScreen - 16, yPositionOnScreen - 16, width + 32, height + 32, Color.White);
            IClickableMenu.drawTextureBox(b, xPositionOnScreen - 8, yPositionOnScreen - 8, width + 16, height + 16, Color.White);

            // 3. 羊皮纸主对话底框
            Game1.drawDialogueBox(xPositionOnScreen, yPositionOnScreen, width, height, false, true);

            // 4. 标题居中绘制（向上提升，预留呼吸感）
            string title = isZh ? $"选择今晚与 {_targetNpc.displayName} 赴约的地点" : $"Date with {_targetNpc.displayName}";
            Vector2 titleSize = Game1.dialogueFont.MeasureString(title);
            Vector2 titlePos = new Vector2(
                xPositionOnScreen + (width - titleSize.X) / 2f,
                yPositionOnScreen + 16);
            b.DrawString(Game1.dialogueFont, title, titlePos, Game1.textColor);

            // 5. 标题下方精致分割线
            b.Draw(Game1.staminaRect,
                new Rectangle(xPositionOnScreen + 40, yPositionOnScreen + 60, width - 80, 2),
                Color.Gray * 0.4f);

            // 6. 卡片列表绘制
            for (int i = 0; i < _locationCards.Count; i++)
            {
                var card = _locationCards[i];
                var view = _currentPagedCards[i];
                bool isHovered = (_hoveredIndex == i);
                bool isSelected = (_selectedIndex == i);

                // 手柄/键盘选中金边外框
                if (isSelected)
                {
                    IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(432, 439, 9, 9),
                        card.bounds.X - 4, card.bounds.Y - 4, card.bounds.Width + 8, card.bounds.Height + 8,
                        Color.Gold * 0.9f, 4f, false);
                }

                // 卡片底色（区分悬停与可用状态）
                Color cardBgColor = !view.IsAvailable
                    ? new Color(200, 200, 200) * 0.55f
                    : (isHovered ? new Color(255, 235, 205) : new Color(245, 238, 225));

                IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(432, 439, 9, 9),
                    card.bounds.X, card.bounds.Y, card.bounds.Width, card.bounds.Height,
                    cardBgColor, 4f, false);

                // 地点名称：0.80f 缩放规整绘制，消除与下方文字的垂直冲突
                string displayName = isZh ? view.Info.DisplayNameZh : view.Info.DisplayNameEn;
                Color nameColor = view.IsAvailable
                    ? (isHovered ? new Color(120, 40, 10) : Game1.textColor)
                    : Color.DimGray;

                b.DrawString(Game1.dialogueFont, displayName,
                    new Vector2(card.bounds.X + 18, card.bounds.Y + 12),
                    nameColor, 0f, Vector2.Zero, 0.80f, SpriteEffects.None, 1f);

                // 右侧时间徽章胶囊
                if (view.IsAvailable)
                {
                    int badgeW = 116;
                    int badgeH = 26;
                    int badgeX = card.bounds.Right - badgeW - 16;
                    int badgeY = card.bounds.Y + 12;

                    IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(432, 439, 9, 9),
                        badgeX, badgeY, badgeW, badgeH,
                        new Color(230, 210, 185) * 0.75f, 3f, false);

                    string timeStr = "18:00 - 21:30";
                    Vector2 timeSize = Game1.tinyFont.MeasureString(timeStr);
                    b.DrawString(Game1.tinyFont, timeStr,
                        new Vector2(badgeX + (badgeW - timeSize.X) / 2f, badgeY + (badgeH - timeSize.Y) / 2f + 1),
                        new Color(175, 75, 25));
                }

                // 描述文本：0.85f 紧凑渲染
                Color descColor = view.IsAvailable
                    ? Color.DarkSlateGray
                    : new Color(160, 45, 45);

                b.DrawString(Game1.smallFont, view.DescriptionText,
                    new Vector2(card.bounds.X + 20, card.bounds.Y + 46),
                    descColor, 0f, Vector2.Zero, 0.85f, SpriteEffects.None, 1f);
            }

            // 7. 底部导航与页码指示器
            int maxPage = (_availableLocations.Count - 1) / ItemsPerPage;

            if (_currentPage > 0)
            {
                UiHelper.UpdateButtonScale(ref _prevHoverScale, _prevPageButton, mx, my);
                _prevPageButton.scale = ArrowButtonBaseScale * _prevHoverScale;
                _prevPageButton.draw(b);
            }

            if (_currentPage < maxPage)
            {
                UiHelper.UpdateButtonScale(ref _nextHoverScale, _nextPageButton, mx, my);
                _nextPageButton.scale = ArrowButtonBaseScale * _nextHoverScale;
                _nextPageButton.draw(b);
            }

            if (maxPage > 0)
            {
                string pageStr = $"{_currentPage + 1} / {maxPage + 1}";
                Vector2 textSize = Game1.smallFont.MeasureString(pageStr);
                b.DrawString(Game1.smallFont, pageStr,
                    new Vector2(xPositionOnScreen + (width - textSize.X) / 2f, yPositionOnScreen + height - 48),
                    Game1.textColor * 0.9f);
            }

            // 8. 关闭按钮（置顶绘制）
            UiHelper.UpdateButtonScale(ref _closeHoverScale, _closeButton, mx, my);
            _closeButton.scale = CloseButtonBaseScale * _closeHoverScale;
            _closeButton.draw(b);

            drawMouse(b);
        }
    }
}