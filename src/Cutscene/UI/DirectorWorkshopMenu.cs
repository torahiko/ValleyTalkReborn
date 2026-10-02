#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;
using ValleytalkReborn.Cutscene.Generation;
using ValleytalkReborn.Cutscene.Storage;
using ValleytalkReborn.UI;

namespace ValleytalkReborn.Cutscene.UI
{
    /// <summary>
    /// 虚拟导演工坊面板：
    /// 继承星露谷经典羊皮纸木质设计规范，提供可视化演员席选角、剧本风格预设、自由命题指导与名场面历史重演
    /// </summary>
    public sealed class DirectorWorkshopMenu : IClickableMenu
    {
        // ── 尺寸与基础排版 ──
        private const int MenuWidth = 960;
        private const int MenuHeight = 630;
        private const int ContentPadding = 24;
        private const int HeaderH = 64;
        private const int TabH = 36;

        // 统一字号规范
        private const float TitleFontSize = CustomFontManager.SizeTitle;       // 24f Bold
        private const float RegularFontSize = CustomFontManager.SizeRegular;   // 18f Bold / Medium
        private const float SmallFontSize = CustomFontManager.SizeSmall;       // 15f Medium

        // 选项卡状态 (0: 即兴剧本工坊, 1: 历史名场面归档)
        private int _activeTab = 0;
        private Rectangle _tab0Rect;
        private Rectangle _tab1Rect;

        // ── Tab 0: 即兴创作数据与组件 ──
        private readonly List<ActorCandidate> _nearbyActors = new();
        private readonly HashSet<NPC> _selectedActors = new();
        private int _actorScrollIndex = 0;
        private const int VisibleActorRows = 5;
        private Rectangle _actorUpArrowRect;
        private Rectangle _actorDownArrowRect;

        // 剧本基调快捷标签
        private static readonly (string Label, string Description, string Prompt)[] TonePresets = new[]
        {
            ("☕ 轻松日常", "轻松幽默的小镇日常与生活趣事", "轻松幽默的小镇日常闲聊与趣事"),
            ("❤️ 浪漫私语", "温馨浪漫的二人独处或真情倾诉", "温馨浪漫的二人独处或真情倾诉"),
            ("🎭 戏剧冲突", "善意的恶作剧、误会或意外状况", "善意的恶作剧、误解与戏剧性的意外状况"),
            ("🍺 酒馆狂欢", "热闹的聚会狂欢、举杯与即兴庆祝", "热闹的聚会狂欢、举杯痛饮与即兴庆祝")
        };
        private int _selectedToneIndex = 0;
        private readonly Rectangle[] _toneButtonRects = new Rectangle[TonePresets.Length];
        private readonly float[] _toneHoverScales = new float[TonePresets.Length];

        // 自由命题输入框
        private readonly DialogueTextInputBox _intentInputBox;

        // 开拍按钮
        private Rectangle _actionButtonRect;
        private float _actionButtonHoverScale = 1f;

        // ── Tab 1: 历史剧目归档数据与组件 ──
        private List<ArchivedCutscene> _archivedCutscenes = new();
        private int _replayScrollIndex = 0;
        private const int VisibleReplayRows = 4;
        private Rectangle _replayUpArrowRect;
        private Rectangle _replayDownArrowRect;
        private readonly List<Rectangle> _replayPlayButtonRects = new();
        private readonly List<Rectangle> _replayDeleteButtonRects = new();
        private readonly List<float> _replayPlayHoverScales = new();
        private readonly List<float> _replayDeleteHoverScales = new();

        private string? _hoverText;

        public DirectorWorkshopMenu()
            : base(
                (Game1.uiViewport.Width - Math.Clamp(Game1.uiViewport.Width - 100, 880, MenuWidth)) / 2,
                (Game1.uiViewport.Height - Math.Clamp(Game1.uiViewport.Height - 80, 580, MenuHeight)) / 2,
                Math.Clamp(Game1.uiViewport.Width - 100, 880, MenuWidth),
                Math.Clamp(Game1.uiViewport.Height - 80, 580, MenuHeight),
                showUpperRightCloseButton: true)
        {
            for (int i = 0; i < TonePresets.Length; i++)
            {
                _toneHoverScales[i] = 1f;
            }

            // 初始化自由命题输入框
            _intentInputBox = new DialogueTextInputBox(500)
            {
                AllowNewlines = true,
                UseCustomFont = true,
                CustomFontSize = RegularFontSize,
                PlaceholderText = "输入自定义剧情命题或导演提示（可选，例如：在雪夜里关于热咖啡的闲聊...）"
            };

            // 扫描现场演员席
            RefreshNearbyActors();

            // 默认勾选距离最近的 1~2 名演员
            if (_nearbyActors.Count > 0)
            {
                _selectedActors.Add(_nearbyActors[0].Npc);
                if (_nearbyActors.Count > 1)
                {
                    _selectedActors.Add(_nearbyActors[1].Npc);
                }
            }

            // 加载历史归档剧目
            RefreshArchivedCutscenes();

            // 布局算位
            Layout();
        }

        private void RefreshNearbyActors()
        {
            _nearbyActors.Clear();
            if (!Context.IsWorldReady || Game1.player?.currentLocation == null)
                return;

            var loc = Game1.player.currentLocation;
            if (loc.characters == null) return;

            var villagers = loc.characters
                .Where(n => n != null && n.IsVillager && !string.IsNullOrWhiteSpace(n.Name))
                .OrderBy(n => Vector2.Distance(n.Tile, Game1.player.Tile))
                .Take(12)
                .ToList();

            foreach (var npc in villagers)
            {
                int dist = (int)Math.Round(Vector2.Distance(npc.Tile, Game1.player.Tile));
                int hearts = Game1.player.getFriendshipHeartLevelForNPC(npc.Name);
                _nearbyActors.Add(new ActorCandidate(npc, dist, hearts));
            }
        }

        private void RefreshArchivedCutscenes()
        {
            _archivedCutscenes = CutsceneStorageService.LoadAll();
        }

        private void Layout()
        {
            // 选项卡排版
            int tabY = yPositionOnScreen + HeaderH - 8;
            int tabW = 160;
            _tab0Rect = new Rectangle(xPositionOnScreen + ContentPadding, tabY, tabW, TabH);
            _tab1Rect = new Rectangle(_tab0Rect.Right + 8, tabY, tabW, TabH);

            // Tab 0 布局
            int contentTop = tabY + TabH + 14;
            int leftColW = 340;
            int rightColX = xPositionOnScreen + ContentPadding + leftColW + 20;
            int rightColW = width - (ContentPadding * 2) - leftColW - 20;

            // 演员上下滚动按钮
            int actorListH = VisibleActorRows * 62;
            _actorUpArrowRect = new Rectangle(xPositionOnScreen + ContentPadding + leftColW - 28, contentTop + 4, 24, 20);
            _actorDownArrowRect = new Rectangle(xPositionOnScreen + ContentPadding + leftColW - 28, contentTop + actorListH - 24, 24, 20);

            // 剧本基调按钮排版（2x2 网格）
            int toneBtnW = (rightColW - 12) / 2;
            int toneBtnH = 34;
            int toneTop = contentTop + 32;
            _toneButtonRects[0] = new Rectangle(rightColX, toneTop, toneBtnW, toneBtnH);
            _toneButtonRects[1] = new Rectangle(rightColX + toneBtnW + 12, toneTop, toneBtnW, toneBtnH);
            _toneButtonRects[2] = new Rectangle(rightColX, toneTop + toneBtnH + 8, toneBtnW, toneBtnH);
            _toneButtonRects[3] = new Rectangle(rightColX + toneBtnW + 12, toneTop + toneBtnH + 8, toneBtnW, toneBtnH);

            // 自由命题输入框排版
            int inputTop = _toneButtonRects[2].Bottom + 40;
            int inputH = 110;
            _intentInputBox.Position = new Vector2(rightColX, inputTop);
            _intentInputBox.Extent = new Vector2(rightColW, inputH);
            _intentInputBox.InvalidateLayout();

            // 底部开拍按钮
            int actionBtnH = 46;
            int actionBtnY = yPositionOnScreen + height - ContentPadding - actionBtnH;
            _actionButtonRect = new Rectangle(rightColX, actionBtnY, rightColW, actionBtnH);

            // Tab 1 滚动按钮
            int replayListH = VisibleReplayRows * 88;
            _replayUpArrowRect = new Rectangle(xPositionOnScreen + width - ContentPadding - 28, contentTop + 4, 24, 20);
            _replayDownArrowRect = new Rectangle(xPositionOnScreen + width - ContentPadding - 28, contentTop + replayListH - 24, 24, 20);
        }

        public override void gameWindowSizeChanged(Rectangle oldBounds, Rectangle newBounds)
        {
            xPositionOnScreen = (Game1.uiViewport.Width - width) / 2;
            yPositionOnScreen = (Game1.uiViewport.Height - height) / 2;
            initializeUpperRightCloseButton();
            Layout();
        }

        public override void update(GameTime time)
        {
            base.update(time);
            _hoverText = null;

            if (_activeTab == 0)
            {
                _intentInputBox.Update(time);
            }
        }

        // ── 交互事件分发 ──

        public override void receiveLeftClick(int x, int y, bool playSound = true)
        {
            base.receiveLeftClick(x, y, playSound);

            // 1. 选项卡切换
            if (_tab0Rect.Contains(x, y))
            {
                if (_activeTab != 0)
                {
                    _activeTab = 0;
                    Game1.playSound("smallSelect");
                }
                return;
            }
            if (_tab1Rect.Contains(x, y))
            {
                if (_activeTab != 1)
                {
                    _activeTab = 1;
                    RefreshArchivedCutscenes();
                    Game1.playSound("smallSelect");
                }
                return;
            }

            if (_activeTab == 0)
            {
                HandleTab0Click(x, y);
            }
            else
            {
                HandleTab1Click(x, y);
            }
        }

        private void HandleTab0Click(int x, int y)
        {
            // 演员席卡片点击（单选/多选，最多 3 人）
            int contentTop = _tab0Rect.Bottom + 14;
            int leftColW = 340;
            int startIdx = _actorScrollIndex;

            for (int i = 0; i < VisibleActorRows && (startIdx + i) < _nearbyActors.Count; i++)
            {
                var candidate = _nearbyActors[startIdx + i];
                int cardY = contentTop + 30 + i * 58;
                var cardRect = new Rectangle(xPositionOnScreen + ContentPadding, cardY, leftColW, 52);

                if (cardRect.Contains(x, y))
                {
                    if (_selectedActors.Contains(candidate.Npc))
                    {
                        _selectedActors.Remove(candidate.Npc);
                        Game1.playSound("drumkit6");
                    }
                    else
                    {
                        if (_selectedActors.Count >= 3)
                        {
                            Game1.playSound("cancel");
                            Game1.addHUDMessage(new HUDMessage("🎬 一场戏最多选定 3 位主要演员", HUDMessage.error_type));
                        }
                        else
                        {
                            _selectedActors.Add(candidate.Npc);
                            Game1.playSound("coin");
                        }
                    }
                    return;
                }
            }

            // 演员列表翻页
            if (_actorUpArrowRect.Contains(x, y) && _actorScrollIndex > 0)
            {
                _actorScrollIndex--;
                Game1.playSound("shwip");
                return;
            }
            if (_actorDownArrowRect.Contains(x, y) && _actorScrollIndex + VisibleActorRows < _nearbyActors.Count)
            {
                _actorScrollIndex++;
                Game1.playSound("shwip");
                return;
            }

            // 剧本基调点击
            for (int i = 0; i < TonePresets.Length; i++)
            {
                if (_toneButtonRects[i].Contains(x, y))
                {
                    _selectedToneIndex = (_selectedToneIndex == i) ? -1 : i;
                    Game1.playSound("smallSelect");
                    return;
                }
            }

            // 自由命题文本框聚焦
            _intentInputBox.ReceiveLeftClick(x, y);

            // 开拍按钮点击
            if (_actionButtonRect.Contains(x, y))
            {
                if (_selectedActors.Count == 0)
                {
                    Game1.playSound("cancel");
                    Game1.addHUDMessage(new HUDMessage("🎬 请至少在左侧选择 1 位参演村民", HUDMessage.error_type));
                    return;
                }

                Game1.playSound("reward");
                StartCutsceneCreation();
            }
        }

        private void HandleTab1Click(int x, int y)
        {
            // 归档重放翻页
            if (_replayUpArrowRect.Contains(x, y) && _replayScrollIndex > 0)
            {
                _replayScrollIndex--;
                Game1.playSound("shwip");
                return;
            }
            if (_replayDownArrowRect.Contains(x, y) && _replayScrollIndex + VisibleReplayRows < _archivedCutscenes.Count)
            {
                _replayScrollIndex++;
                Game1.playSound("shwip");
                return;
            }

            // 归档行内按钮
            int startIdx = _replayScrollIndex;
            for (int i = 0; i < VisibleReplayRows && (startIdx + i) < _archivedCutscenes.Count; i++)
            {
                var cutscene = _archivedCutscenes[startIdx + i];

                if (i < _replayPlayButtonRects.Count && _replayPlayButtonRects[i].Contains(x, y))
                {
                    // 重播剧目
                    Game1.playSound("bigSelect");
                    if (CutsceneStorageService.Replay(cutscene, out string error))
                    {
                        Game1.addHUDMessage(new HUDMessage($"🎬 正在重播《{cutscene.Title}》", HUDMessage.achievement_type));
                        exitThisMenu();
                    }
                    else
                    {
                        Game1.playSound("cancel");
                        Game1.addHUDMessage(new HUDMessage($"🎬 无法重播: {error}", HUDMessage.error_type));
                    }
                    return;
                }

                if (i < _replayDeleteButtonRects.Count && _replayDeleteButtonRects[i].Contains(x, y))
                {
                    // 删除剧目
                    Game1.playSound("trashcan");
                    CutsceneStorageService.Delete(cutscene.Id);
                    RefreshArchivedCutscenes();
                    return;
                }
            }
        }

        private void StartCutsceneCreation()
        {
            string finalIntent = string.Empty;
            if (_selectedToneIndex >= 0 && _selectedToneIndex < TonePresets.Length)
            {
                finalIntent = $"[基调: {TonePresets[_selectedToneIndex].Prompt}] ";
            }

            string userText = _intentInputBox.Text?.Trim() ?? string.Empty;
            if (!string.IsNullOrEmpty(userText))
            {
                finalIntent += userText;
            }

            var actors = _selectedActors.ToList();

            // 关闭菜单，解除输入焦点
            if (Game1.keyboardDispatcher.Subscriber == _intentInputBox)
                Game1.keyboardDispatcher.Subscriber = null;

            exitThisMenu();

            // 异步调用生成服务
            _ = CutsceneGeneratorService.GenerateAndPlayAsync(actors, string.IsNullOrWhiteSpace(finalIntent) ? null : finalIntent);
        }

        public override void receiveKeyPress(Keys key)
        {
            if (key == Keys.Escape)
            {
                if (Game1.keyboardDispatcher.Subscriber == _intentInputBox)
                    Game1.keyboardDispatcher.Subscriber = null;

                exitThisMenu();
                return;
            }

            base.receiveKeyPress(key);
        }

        public override void leftClickHeld(int x, int y)
        {
            base.leftClickHeld(x, y);
            if (_activeTab == 0) _intentInputBox.LeftClickHeld(x, y);
        }

        public override void releaseLeftClick(int x, int y)
        {
            base.releaseLeftClick(x, y);
            if (_activeTab == 0) _intentInputBox.ReleaseLeftClick(x, y);
        }

        public override void receiveScrollWheelAction(int direction)
        {
            base.receiveScrollWheelAction(direction);
            if (_activeTab == 0)
            {
                if (_intentInputBox.TextAreaBounds.Contains(Game1.getMouseX(), Game1.getMouseY()))
                {
                    _intentInputBox.ReceiveScrollWheel(direction);
                }
                else
                {
                    if (direction > 0 && _actorScrollIndex > 0) _actorScrollIndex--;
                    else if (direction < 0 && _actorScrollIndex + VisibleActorRows < _nearbyActors.Count) _actorScrollIndex++;
                }
            }
            else
            {
                if (direction > 0 && _replayScrollIndex > 0) _replayScrollIndex--;
                else if (direction < 0 && _replayScrollIndex + VisibleReplayRows < _archivedCutscenes.Count) _replayScrollIndex++;
            }
        }

        // ── 渲染管线 ──

        public override void draw(SpriteBatch b)
        {
            int mx = Game1.getMouseX();
            int my = Game1.getMouseY();

            // 1. 半透明全屏遮罩
            b.Draw(Game1.fadeToBlackRect, Game1.graphics.GraphicsDevice.Viewport.Bounds, Color.Black * 0.5f);

            // 2. 双层羊皮纸木框底板
            IClickableMenu.drawTextureBox(b, xPositionOnScreen - 8, yPositionOnScreen - 8, width + 16, height + 16, Color.White);
            b.Draw(Game1.staminaRect, new Rectangle(xPositionOnScreen, yPositionOnScreen, width, height), new Color(245, 230, 205));
            b.Draw(Game1.menuTexture, new Rectangle(xPositionOnScreen, yPositionOnScreen, width, height),
                new Rectangle(64, 128, 64, 64), new Color(245, 230, 205));

            // 3. 顶栏大标题
            CustomFontManager.DrawString(b, "🎬 虚拟导演工坊",
                new Vector2(xPositionOnScreen + ContentPadding, yPositionOnScreen + 18),
                BioEditorMenu.TextPrimary, TitleFontSize);

            CustomFontManager.DrawString(b, "挑选主演、定制情节，开启属于你的即兴电影时刻",
                new Vector2(xPositionOnScreen + ContentPadding + 220, yPositionOnScreen + 24),
                BioEditorMenu.TextMuted, SmallFontSize);

            // 4. 选项卡绘制
            DrawTabs(b, mx, my);

            // 5. 分页面渲染
            if (_activeTab == 0)
            {
                DrawTab0Studio(b, mx, my);
            }
            else
            {
                DrawTab1Archive(b, mx, my);
            }

            // 6. 原生关闭按钮与悬浮提示
            base.draw(b);

            if (!string.IsNullOrEmpty(_hoverText))
            {
                drawHoverText(b, _hoverText, Game1.smallFont);
            }

            drawMouse(b);
        }

        private void DrawTabs(SpriteBatch b, int mx, int my)
        {
            // Tab 0
            bool tab0Hover = _tab0Rect.Contains(mx, my);
            Color tab0Bg = (_activeTab == 0) ? new Color(255, 248, 236) : (tab0Hover ? new Color(250, 238, 220) : new Color(230, 212, 185));
            Color tab0Border = (_activeTab == 0) ? new Color(210, 160, 60) : new Color(190, 170, 140);
            b.Draw(Game1.staminaRect, _tab0Rect, tab0Bg);
            DrawBorder(b, _tab0Rect, tab0Border, (_activeTab == 0) ? 2 : 1);
            CustomFontManager.DrawString(b, "🎭 即兴剧本工坊",
                new Vector2(_tab0Rect.X + 16, _tab0Rect.Y + 8),
                (_activeTab == 0) ? BioEditorMenu.TextPrimary : BioEditorMenu.TextSecondary, RegularFontSize);

            // Tab 1
            bool tab1Hover = _tab1Rect.Contains(mx, my);
            Color tab1Bg = (_activeTab == 1) ? new Color(255, 248, 236) : (tab1Hover ? new Color(250, 238, 220) : new Color(230, 212, 185));
            Color tab1Border = (_activeTab == 1) ? new Color(210, 160, 60) : new Color(190, 170, 140);
            b.Draw(Game1.staminaRect, _tab1Rect, tab1Bg);
            DrawBorder(b, _tab1Rect, tab1Border, (_activeTab == 1) ? 2 : 1);
            CustomFontManager.DrawString(b, "📜 历史名场面",
                new Vector2(_tab1Rect.X + 20, _tab1Rect.Y + 8),
                (_activeTab == 1) ? BioEditorMenu.TextPrimary : BioEditorMenu.TextSecondary, RegularFontSize);
        }

        private void DrawTab0Studio(SpriteBatch b, int mx, int my)
        {
            int contentTop = _tab0Rect.Bottom + 14;
            int leftColW = 340;
            int rightColX = xPositionOnScreen + ContentPadding + leftColW + 20;
            int rightColW = width - (ContentPadding * 2) - leftColW - 20;

            // ── 左侧：现场演员席 ──
            string actorSectionTitle = $"现场演员席 ({_selectedActors.Count}/3)";
            CustomFontManager.DrawString(b, actorSectionTitle,
                new Vector2(xPositionOnScreen + ContentPadding, contentTop + 4),
                BioEditorMenu.TextPrimary, RegularFontSize);

            // 上下翻页指示
            if (_nearbyActors.Count > VisibleActorRows)
            {
                CustomFontManager.DrawString(b, "▲", new Vector2(_actorUpArrowRect.X, _actorUpArrowRect.Y),
                    _actorScrollIndex > 0 ? BioEditorMenu.TextPrimary : BioEditorMenu.TextMuted, SmallFontSize);
                CustomFontManager.DrawString(b, "▼", new Vector2(_actorDownArrowRect.X, _actorDownArrowRect.Y),
                    _actorScrollIndex + VisibleActorRows < _nearbyActors.Count ? BioEditorMenu.TextPrimary : BioEditorMenu.TextMuted, SmallFontSize);
            }

            if (_nearbyActors.Count == 0)
            {
                CustomFontManager.DrawString(b, "当前场景附近暂无空闲村民",
                    new Vector2(xPositionOnScreen + ContentPadding + 10, contentTop + 50),
                    BioEditorMenu.TextMuted, SmallFontSize);
            }
            else
            {
                int startIdx = _actorScrollIndex;
                for (int i = 0; i < VisibleActorRows && (startIdx + i) < _nearbyActors.Count; i++)
                {
                    var candidate = _nearbyActors[startIdx + i];
                    int cardY = contentTop + 30 + i * 58;
                    var cardRect = new Rectangle(xPositionOnScreen + ContentPadding, cardY, leftColW, 52);

                    bool isHover = cardRect.Contains(mx, my);
                    bool isSelected = _selectedActors.Contains(candidate.Npc);

                    // 卡片底板
                    Color cardBg = isSelected ? new Color(255, 245, 225) : (isHover ? new Color(250, 242, 230) : new Color(245, 236, 222));
                    Color borderCol = isSelected ? new Color(210, 160, 60) : (isHover ? new Color(195, 175, 145) : new Color(215, 200, 180));
                    b.Draw(Game1.staminaRect, cardRect, cardBg);
                    DrawBorder(b, cardRect, borderCol, isSelected ? 2 : 1);

                    // 肖像微缩框
                    int avatarBoxSize = 42;
                    var avatarRect = new Rectangle(cardRect.X + 5, cardRect.Y + 5, avatarBoxSize, avatarBoxSize);
                    b.Draw(Game1.staminaRect, avatarRect, new Color(230, 218, 200));
                    DrawBorder(b, avatarRect, new Color(180, 160, 135), 1);

                    if (candidate.Portrait != null && !candidate.PortraitSourceRect.IsEmpty)
                    {
                        b.Draw(candidate.Portrait, avatarRect, candidate.PortraitSourceRect, Color.White);
                    }

                    // 姓名与距离、好感度
                    string nameText = candidate.DisplayName;
                    CustomFontManager.DrawString(b, nameText,
                        new Vector2(avatarRect.Right + 10, cardRect.Y + 6),
                        isSelected ? BioEditorMenu.TextAccent : BioEditorMenu.TextPrimary, RegularFontSize);

                    string infoText = $"❤️ {candidate.FriendshipHearts}  |  距离 {candidate.DistanceTiles} 格";
                    CustomFontManager.DrawString(b, infoText,
                        new Vector2(avatarRect.Right + 10, cardRect.Y + 28),
                        BioEditorMenu.TextMuted, SmallFontSize);

                    // 选定状态指示
                    string statusTag = isSelected ? "✔ 主演" : "+ 登台";
                    Color statusColor = isSelected ? BioEditorMenu.TextSuccess : (isHover ? BioEditorMenu.TextSecondary : BioEditorMenu.TextMuted);
                    var tagSize = CustomFontManager.MeasureString(statusTag, SmallFontSize);
                    CustomFontManager.DrawString(b, statusTag,
                        new Vector2(cardRect.Right - tagSize.X - 10, cardRect.Y + 16),
                        statusColor, SmallFontSize);
                }
            }

            // ── 右侧：剧目编导台 ──
            CustomFontManager.DrawString(b, "剧本基调快捷预设",
                new Vector2(rightColX, contentTop + 4),
                BioEditorMenu.TextPrimary, RegularFontSize);

            for (int i = 0; i < TonePresets.Length; i++)
            {
                var rect = _toneButtonRects[i];
                bool isSelected = (_selectedToneIndex == i);
                bool isHover = rect.Contains(mx, my);

                var style = isSelected ? ActionButtonStyle.Primary : (isHover ? ActionButtonStyle.Default : ActionButtonStyle.Default);
                ActionButtonRenderer.Draw(b, rect, TonePresets[i].Label, ref _toneHoverScales[i], mx, my, style: style);

                if (isHover)
                {
                    _hoverText = TonePresets[i].Description;
                }
            }

            // 自由命题标签
            CustomFontManager.DrawString(b, "自定义命题与细节指导 (可选)",
                new Vector2(rightColX, _toneButtonRects[2].Bottom + 16),
                BioEditorMenu.TextPrimary, RegularFontSize);

            // 输入框
            DrawStyledDialogueBox(b, _intentInputBox);

            // 底部开拍按钮
            bool canAction = (_selectedActors.Count > 0);
            var actionStyle = canAction ? ActionButtonStyle.Primary : ActionButtonStyle.Disabled;
            string actionText = canAction ? $"🎬 开拍即兴短剧 ({_selectedActors.Count} 位主演)" : "🎬 请选择至少 1 位演员";
            ActionButtonRenderer.Draw(b, _actionButtonRect, actionText, ref _actionButtonHoverScale, mx, my, style: actionStyle);
        }

        private void DrawTab1Archive(SpriteBatch b, int mx, int my)
        {
            int contentTop = _tab1Rect.Bottom + 14;
            int listW = width - (ContentPadding * 2);

            CustomFontManager.DrawString(b, $"已归档的精彩即兴剧目 (共 {_archivedCutscenes.Count} 部)",
                new Vector2(xPositionOnScreen + ContentPadding, contentTop + 4),
                BioEditorMenu.TextPrimary, RegularFontSize);

            // 上下翻页指示
            if (_archivedCutscenes.Count > VisibleReplayRows)
            {
                CustomFontManager.DrawString(b, "▲", new Vector2(_replayUpArrowRect.X, _replayUpArrowRect.Y),
                    _replayScrollIndex > 0 ? BioEditorMenu.TextPrimary : BioEditorMenu.TextMuted, SmallFontSize);
                CustomFontManager.DrawString(b, "▼", new Vector2(_replayDownArrowRect.X, _replayDownArrowRect.Y),
                    _replayScrollIndex + VisibleReplayRows < _archivedCutscenes.Count ? BioEditorMenu.TextPrimary : BioEditorMenu.TextMuted, SmallFontSize);
            }

            _replayPlayButtonRects.Clear();
            _replayDeleteButtonRects.Clear();

            while (_replayPlayHoverScales.Count < VisibleReplayRows) _replayPlayHoverScales.Add(1f);
            while (_replayDeleteHoverScales.Count < VisibleReplayRows) _replayDeleteHoverScales.Add(1f);

            if (_archivedCutscenes.Count == 0)
            {
                CustomFontManager.DrawString(b, "暂无历史即兴剧本，快去【即兴剧本工坊】拍摄第一场戏吧！",
                    new Vector2(xPositionOnScreen + ContentPadding + 20, contentTop + 80),
                    BioEditorMenu.TextMuted, RegularFontSize);
            }
            else
            {
                int startIdx = _replayScrollIndex;
                for (int i = 0; i < VisibleReplayRows && (startIdx + i) < _archivedCutscenes.Count; i++)
                {
                    var cutscene = _archivedCutscenes[startIdx + i];
                    int cardY = contentTop + 34 + i * 88;
                    var cardRect = new Rectangle(xPositionOnScreen + ContentPadding, cardY, listW, 80);

                    bool isHover = cardRect.Contains(mx, my);

                    // 卡片底衬
                    Color cardBg = isHover ? new Color(255, 248, 236) : new Color(248, 240, 226);
                    Color borderCol = isHover ? new Color(210, 160, 60) : new Color(220, 205, 180);
                    b.Draw(Game1.staminaRect, cardRect, cardBg);
                    DrawBorder(b, cardRect, borderCol, 1);

                    // 剧本标题与时间
                    string title = string.IsNullOrWhiteSpace(cutscene.Title) ? "即兴剧本" : cutscene.Title;
                    CustomFontManager.DrawString(b, $"《{title}》",
                        new Vector2(cardRect.X + 16, cardRect.Y + 10),
                        BioEditorMenu.TextPrimary, RegularFontSize);

                    string timeStr = cutscene.CreatedAt.ToString("yyyy-MM-dd HH:mm");
                    CustomFontManager.DrawString(b, timeStr,
                        new Vector2(cardRect.X + 220, cardRect.Y + 12),
                        BioEditorMenu.TextMuted, SmallFontSize);

                    // 参演演员与地点
                    string actorsStr = (cutscene.ActorNames != null && cutscene.ActorNames.Count > 0)
                        ? string.Join(", ", cutscene.ActorNames)
                        : "村民";
                    string detailStr = $"主演: {actorsStr}  |  场景: {cutscene.LocationName}  |  动作数: {cutscene.ActionCount}";
                    CustomFontManager.DrawString(b, detailStr,
                        new Vector2(cardRect.X + 16, cardRect.Y + 36),
                        BioEditorMenu.TextSecondary, SmallFontSize);

                    // 用户指导意图
                    if (!string.IsNullOrWhiteSpace(cutscene.UserIntent))
                    {
                        string intentStr = CustomFontManager.TruncateString($"命题: {cutscene.UserIntent}", SmallFontSize, cardRect.Width - 280);
                        CustomFontManager.DrawString(b, intentStr,
                            new Vector2(cardRect.X + 16, cardRect.Y + 56),
                            BioEditorMenu.TextMuted, SmallFontSize);
                    }

                    // 右侧动作按钮（重播 & 删除）
                    int delBtnW = 70;
                    int playBtnW = 90;
                    int btnH = 32;
                    int btnY = cardRect.Y + (cardRect.Height - btnH) / 2;

                    var delRect = new Rectangle(cardRect.Right - delBtnW - 12, btnY, delBtnW, btnH);
                    var playRect = new Rectangle(delRect.X - playBtnW - 8, btnY, playBtnW, btnH);

                    _replayPlayButtonRects.Add(playRect);
                    _replayDeleteButtonRects.Add(delRect);

                    float playScale = _replayPlayHoverScales[i];
                    ActionButtonRenderer.Draw(b, playRect, "▶ 重播", ref playScale, mx, my, style: ActionButtonStyle.Primary);
                    _replayPlayHoverScales[i] = playScale;

                    float delScale = _replayDeleteHoverScales[i];
                    ActionButtonRenderer.Draw(b, delRect, "🗑 删除", ref delScale, mx, my, style: ActionButtonStyle.Danger);
                    _replayDeleteHoverScales[i] = delScale;
                }
            }
        }

        private static void DrawStyledDialogueBox(SpriteBatch b, DialogueTextInputBox box)
        {
            var bounds = box.TextAreaBounds;
            b.Draw(Game1.staminaRect, new Rectangle(bounds.X - 2, bounds.Y - 2, bounds.Width + 4, bounds.Height + 4), new Color(250, 245, 235));
            IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(432, 439, 9, 9),
                bounds.X - 3, bounds.Y - 3, bounds.Width + 6, bounds.Height + 6, new Color(200, 180, 150), 2f, false);
            box.Draw(b);
        }

        private static void DrawBorder(SpriteBatch b, Rectangle rect, Color color, int thickness)
        {
            b.Draw(Game1.staminaRect, new Rectangle(rect.X, rect.Y, rect.Width, thickness), color);
            b.Draw(Game1.staminaRect, new Rectangle(rect.X, rect.Bottom - thickness, rect.Width, thickness), color);
            b.Draw(Game1.staminaRect, new Rectangle(rect.X, rect.Y, thickness, rect.Height), color);
            b.Draw(Game1.staminaRect, new Rectangle(rect.Right - thickness, rect.Y, thickness, rect.Height), color);
        }

        /// <summary>
        /// 现场演员候选项
        /// </summary>
        private sealed class ActorCandidate
        {
            public NPC Npc { get; }
            public string DisplayName { get; }
            public int DistanceTiles { get; }
            public int FriendshipHearts { get; }
            public Texture2D? Portrait { get; }
            public Rectangle PortraitSourceRect { get; }

            public ActorCandidate(NPC npc, int distanceTiles, int friendshipHearts)
            {
                Npc = npc;
                DisplayName = !string.IsNullOrWhiteSpace(npc.displayName) ? npc.displayName : npc.Name;
                DistanceTiles = distanceTiles;
                FriendshipHearts = friendshipHearts;

                try
                {
                    if (npc.Portrait != null && !npc.Portrait.IsDisposed)
                    {
                        Portrait = npc.Portrait;
                    }
                    else
                    {
                        Portrait = Game1.content.Load<Texture2D>("Portraits\\" + npc.Name);
                    }

                    if (Portrait != null)
                    {
                        PortraitSourceRect = Game1.getSourceRectForStandardTileSheet(Portrait, 0, 64, 64);
                        if (!Portrait.Bounds.Contains(PortraitSourceRect))
                        {
                            PortraitSourceRect = new Rectangle(0, 0, Math.Min(64, Portrait.Width), Math.Min(64, Portrait.Height));
                        }
                    }
                }
                catch
                {
                    Portrait = null;
                    PortraitSourceRect = Rectangle.Empty;
                }
            }
        }
    }
}
