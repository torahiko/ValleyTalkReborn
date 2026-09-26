#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewValley;
using StardewValley.Menus;
using ValleytalkReborn.Services;

namespace ValleytalkReborn.UI
{
    /// <summary>
    /// 规则批量新增与分派弹窗：
    /// 遵循 BioEditorMenu 视觉与动效体系 —— 羊皮纸无缝底板、立体按压下沉；
    /// 行为准则固定永久有效；全镇共识与专属村民无缝互斥；
    /// 解决搜索输入快捷键误触冲突与光标闪烁，大幅增强已选村民辨识度。
    /// </summary>
    internal sealed class AddMultiRuleInputMenu : IClickableMenu
    {
        // ── 尺寸与布局常量 ──
        private const int MenuWidth = 860;
        private const int MenuHeight = 650;
        private const int ContentPadding = 24;
        private const int HeaderH = 58;
        private const int FooterH = 54;

        // 统一整数字号（严格对齐 CustomFontManager）
        private const float TitleFontSize = CustomFontManager.SizeTitle;       // 24f Bold (顶栏主标题)
        private const float ButtonFontSize = CustomFontManager.SizeRegular;    // 18f Bold (主动作按钮)
        private const float SectionHeaderSize = CustomFontManager.SizeRegular; // 18f Medium (分类/栏目标题)
        private const float ContentFontSize = CustomFontManager.SizeRegular;   // 18f Medium (NPC 名字)
        private const float TipFontSize = CustomFontManager.SizeSmall;         // 15f Medium (胶囊、说明、小标签)

        private readonly IntegratedHubMenu _hub;
        private DialogueTextInputBox _inputBox = null!;

        // 规则属性
        private MemoryCategory _category = MemoryCategory.Fact;
        private int _durationMode = 2; // 0=Today, 1=Days, 2=Perm
        private NumberStepper? _dayStepper;

        // 顶栏关闭按钮
        private ClickableTextureComponent _closeXButton = null!;
        private float _closeButtonHoverScale;
        private const float CloseButtonBaseScale = 3f;

        // 属性配置区域胶囊
        private Rectangle _factCapsuleRect;
        private Rectangle _behaviorCapsuleRect;
        private Rectangle _durPermRect;
        private Rectangle _durTodayRect;
        private Rectangle _durCustomRect;

        // ── 分派范围与 NPC 数据 ──
        private record NpcOption(string Id, string DisplayName, Texture2D? Sprite, Rectangle SourceRect, bool IsDatable);
        private readonly List<NpcOption> _npcOptions = new();
        private readonly HashSet<string> _selectedNpcIds = new(StringComparer.OrdinalIgnoreCase);

        // 筛选与检索
        private enum NpcFilterMode { All, Datable, SelectedOnly }
        private NpcFilterMode _filterMode = NpcFilterMode.All;
        private TextBox _searchBox = null!;
        private readonly List<NpcOption> _filteredNpcs = new();

        // 胶囊与快捷按钮
        private Rectangle _worldScopePillRect; // 全局“全镇共识”专属大胶囊
        private Rectangle _filterAllRect;
        private Rectangle _filterDatableRect;
        private Rectangle _filterSelectedRect;
        private Rectangle _btnSelectAllRect;
        private Rectangle _btnInvertRect;
        private Rectangle _btnClearRect;

        // NPC 网格与滚动
        private Rectangle _npcGridBounds;
        private int _npcScrollOffset = 0;
        private const int NpcItemH = 38;

        // 底部动作按钮
        private Rectangle _btnCancelRect;
        private Rectangle _btnOkRect;

        private string? _hoverText;

        public AddMultiRuleInputMenu(IntegratedHubMenu hub, string defaultScope)
            : base(
                (Game1.uiViewport.Width - Math.Clamp(Game1.uiViewport.Width - 100, 800, MenuWidth)) / 2,
                (Game1.uiViewport.Height - Math.Clamp(Game1.uiViewport.Height - 80, 580, MenuHeight)) / 2,
                Math.Clamp(Game1.uiViewport.Width - 100, 800, MenuWidth),
                Math.Clamp(Game1.uiViewport.Height - 80, 580, MenuHeight),
                showUpperRightCloseButton: false)
        {
            _hub = hub;

            BuildNpcOptions();

            // 初始作用域分派
            if (!string.IsNullOrEmpty(defaultScope) && defaultScope != "__ALL__" && defaultScope != "WORLD")
                _selectedNpcIds.Add(defaultScope);
            else
                _selectedNpcIds.Add("WORLD");

            InitComponents();
            ApplyFilter();
        }

        private void BuildNpcOptions()
        {
            _npcOptions.Clear();
            var candidates = NpcCandidateQueryService.GetCleanedCandidates();

            foreach (var c in candidates)
            {
                var character = Game1.getCharacterFromName(c.Id);
                bool isDatable = character != null && character.datable.Value;
                var (sprite, srcRect) = GetNpcWalkingHeadSprite(c.Id);
                _npcOptions.Add(new NpcOption(c.Id, c.DisplayName, sprite, srcRect, isDatable));
            }
        }

        private void InitComponents()
        {
            int padX = ContentPadding;
            int contentW = width - padX * 2;

            _closeXButton = new ClickableTextureComponent(
                new Rectangle(xPositionOnScreen + width - 50, yPositionOnScreen + 16, 36, 36),
                Game1.mouseCursors, new Rectangle(337, 494, 12, 12), CloseButtonBaseScale);

            // 1. 规则文本输入框
            int curY = yPositionOnScreen + HeaderH + 8;
            _inputBox = new DialogueTextInputBox(RuleManager.MaxRuleLength, (int)(RuleManager.MaxRuleLength * 0.9f))
            {
                Position = new Vector2(xPositionOnScreen + padX, curY),
                Extent = new Vector2(contentW, 72),
                UseCustomFont = true,
                CustomFontSize = ContentFontSize,
                CounterFontSize = TipFontSize,
                DrawFrame = true,
                ShowCharacterCount = true,
                AllowNewlines = false,
                TextColor = BioEditorMenu.TextPrimary,
                Selected = true,
                PlaceholderText = I18n.AddRuleMenu.InputPlaceholder(RuleManager.MaxRuleLength),
                PlaceholderColor = new Color(175, 145, 115)
            };
            Game1.keyboardDispatcher.Subscriber = _inputBox;
            curY += 80;

            // 2. 规则属性栏（分类 + 时效）
            int catBtnW = 95;
            _factCapsuleRect = new Rectangle(xPositionOnScreen + padX + 46, curY, catBtnW, 28);
            _behaviorCapsuleRect = new Rectangle(_factCapsuleRect.Right + 8, curY, catBtnW, 28);

            // ★ 优化 1：“指定天”胶囊扩宽至 110px，舒展容纳步进器，不再被两端夹扁
            const int durCustomW = 110;
            const int durTodayW = 82;
            const int durPermW = 92;

            int durRight = xPositionOnScreen + width - padX;
            _durCustomRect = new Rectangle(durRight - durCustomW, curY, durCustomW, 28);
            _durTodayRect = new Rectangle(_durCustomRect.Left - 8 - durTodayW, curY, durTodayW, 28);
            _durPermRect = new Rectangle(_durTodayRect.Left - 8 - durPermW, curY, durPermW, 28);

            _dayStepper = new NumberStepper(new Rectangle(_durCustomRect.X + 2, _durCustomRect.Y, _durCustomRect.Width - 4, 28), 3, 1, 99, 1, "d");
            curY += 38;

            // 3. 检索与分派范围总控栏
            _worldScopePillRect = new Rectangle(xPositionOnScreen + padX, curY, 160, 30);

            // 搜索框
            Texture2D boxTex = LoadTextBoxTexture();
            _searchBox = new TextBox(boxTex, null, Game1.smallFont, Game1.textColor)
            {
                X = _worldScopePillRect.Right + 10,
                Y = curY + 1,
                Width = 135,
                Height = 28,
                limitWidth = false
            };

            // 筛选标签
            _filterAllRect = new Rectangle(_searchBox.X + _searchBox.Width + 8, curY + 1, 52, 28);
            _filterDatableRect = new Rectangle(_filterAllRect.Right + 6, curY + 1, 84, 28);
            _filterSelectedRect = new Rectangle(_filterDatableRect.Right + 6, curY + 1, 74, 28);

            // ★ 优化 2：全选、反选、清空快捷按钮
            const int qBtnW = 50;
            _btnClearRect = new Rectangle(xPositionOnScreen + width - padX - qBtnW, curY + 1, qBtnW, 28);
            _btnInvertRect = new Rectangle(_btnClearRect.Left - 6 - qBtnW, curY + 1, qBtnW, 28);
            _btnSelectAllRect = new Rectangle(_btnInvertRect.Left - 6 - qBtnW, curY + 1, qBtnW, 28);

            curY += 38;

            // 4. NPC 卡片网格区域
            int footerY = yPositionOnScreen + height - FooterH + 10;
            _npcGridBounds = new Rectangle(xPositionOnScreen + padX, curY, contentW, footerY - 14 - curY);

            // 5. 底部操作按钮
            const int btnH = 38;
            const int cancelBtnW = 130;
            const int okBtnW = 210;
            _btnCancelRect = new Rectangle(xPositionOnScreen + padX, footerY, cancelBtnW, btnH);
            _btnOkRect = new Rectangle(xPositionOnScreen + width - padX - okBtnW, footerY, okBtnW, btnH);
        }

        private static Texture2D LoadTextBoxTexture()
        {
            try { return Game1.content.Load<Texture2D>("LooseSprites\\textBox") ?? Game1.mouseCursors; }
            catch { return Game1.mouseCursors; }
        }

        private void ApplyFilter()
        {
            _filteredNpcs.Clear();
            string query = _searchBox.Text?.Trim().ToLowerInvariant() ?? string.Empty;

            foreach (var opt in _npcOptions)
            {
                if (!string.IsNullOrEmpty(query))
                {
                    bool matchName = opt.DisplayName.ToLowerInvariant().Contains(query);
                    bool matchId = opt.Id.ToLowerInvariant().Contains(query);
                    if (!matchName && !matchId)
                        continue;
                }

                if (_filterMode == NpcFilterMode.Datable && !opt.IsDatable)
                    continue;

                if (_filterMode == NpcFilterMode.SelectedOnly && !_selectedNpcIds.Contains(opt.Id))
                    continue;

                _filteredNpcs.Add(opt);
            }

            int rowCount = (int)Math.Ceiling(_filteredNpcs.Count / 3.0);
            int visibleRows = _npcGridBounds.Height / NpcItemH;
            int maxOffset = Math.Max(0, rowCount - visibleRows);
            _npcScrollOffset = Math.Clamp(_npcScrollOffset, 0, maxOffset);
        }

        public override void update(GameTime time)
        {
            base.update(time);
            _hoverText = null;
            _inputBox.Update(time);
        }

        public override void receiveScrollWheelAction(int direction)
        {
            base.receiveScrollWheelAction(direction);
            int mx = Game1.getMouseX();
            int my = Game1.getMouseY();

            if (_npcGridBounds.Contains(mx, my))
            {
                int rowCount = (int)Math.Ceiling(_filteredNpcs.Count / 3.0);
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

            // 1. 输入框焦点切换
            if (_inputBox.ReceiveLeftClick(x, y))
            {
                _searchBox.Selected = false;
                Game1.keyboardDispatcher.Subscriber = _inputBox;
                return;
            }

            var searchRect = new Rectangle(_searchBox.X, _searchBox.Y, _searchBox.Width, _searchBox.Height);
            if (searchRect.Contains(x, y))
            {
                _inputBox.Selected = false;
                _searchBox.SelectMe();
                Game1.keyboardDispatcher.Subscriber = _searchBox;
                return;
            }

            // 2. 类别与时效（★ 优化 4：行为准则互斥限制）
            if (_factCapsuleRect.Contains(x, y))
            {
                _category = MemoryCategory.Fact;
                Game1.playSound("smallSelect");
                return;
            }

            if (_behaviorCapsuleRect.Contains(x, y))
            {
                _category = MemoryCategory.Behavior;
                _durationMode = 2; // 行为准则强制固定为永久有效
                Game1.playSound("smallSelect");
                return;
            }

            // 时效点击处理（行为准则下禁用）
            if (_durPermRect.Contains(x, y))
            {
                _durationMode = 2;
                Game1.playSound("smallSelect");
                return;
            }

            if (_durTodayRect.Contains(x, y))
            {
                if (_category == MemoryCategory.Behavior)
                {
                    Game1.playSound("cancel");
                    Game1.addHUDMessage(new HUDMessage("行为准则为核心长期约束，固定为永久生效", HUDMessage.error_type));
                    return;
                }
                _durationMode = 0;
                Game1.playSound("smallSelect");
                return;
            }

            if (_durCustomRect.Contains(x, y))
            {
                if (_category == MemoryCategory.Behavior)
                {
                    Game1.playSound("cancel");
                    Game1.addHUDMessage(new HUDMessage("行为准则为核心长期约束，固定为永久生效", HUDMessage.error_type));
                    return;
                }

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

            // 3. 核心互斥逻辑：点击“全镇共识”
            if (_worldScopePillRect.Contains(x, y))
            {
                _selectedNpcIds.Clear();
                _selectedNpcIds.Add("WORLD");
                Game1.playSound("drumkit6");
                ApplyFilter();
                return;
            }

            // 4. 过滤模式标签
            if (_filterAllRect.Contains(x, y) && _filterMode != NpcFilterMode.All)
            {
                _filterMode = NpcFilterMode.All;
                Game1.playSound("smallSelect");
                ApplyFilter();
                return;
            }
            if (_filterDatableRect.Contains(x, y) && _filterMode != NpcFilterMode.Datable)
            {
                _filterMode = NpcFilterMode.Datable;
                Game1.playSound("smallSelect");
                ApplyFilter();
                return;
            }
            if (_filterSelectedRect.Contains(x, y) && _filterMode != NpcFilterMode.SelectedOnly)
            {
                _filterMode = NpcFilterMode.SelectedOnly;
                Game1.playSound("smallSelect");
                ApplyFilter();
                return;
            }

            // 5. 快捷全选 / 反选 / 清空（作用于当前过滤结果，并取消 WORLD）
            if (_btnSelectAllRect.Contains(x, y))
            {
                _selectedNpcIds.Remove("WORLD");
                foreach (var opt in _filteredNpcs)
                    _selectedNpcIds.Add(opt.Id);
                Game1.playSound("smallSelect");
                return;
            }
            if (_btnInvertRect.Contains(x, y))
            {
                _selectedNpcIds.Remove("WORLD");
                foreach (var opt in _filteredNpcs)
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
                ApplyFilter();
                return;
            }

            // 6. 核心互斥逻辑：点击 NPC 卡片
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
                        if (index >= _filteredNpcs.Count) break;

                        var opt = _filteredNpcs[index];
                        var rect = new Rectangle(_npcGridBounds.X + 4 + c * (colW + 4), _npcGridBounds.Y + 4 + r * NpcItemH, colW, NpcItemH - 4);
                        if (rect.Contains(x, y))
                        {
                            if (_selectedNpcIds.Contains("WORLD"))
                            {
                                _selectedNpcIds.Clear();
                                _selectedNpcIds.Add(opt.Id);
                            }
                            else
                            {
                                if (_selectedNpcIds.Contains(opt.Id))
                                    _selectedNpcIds.Remove(opt.Id);
                                else
                                    _selectedNpcIds.Add(opt.Id);
                            }

                            Game1.playSound("drumkit6");
                            if (_filterMode == NpcFilterMode.SelectedOnly)
                                ApplyFilter();
                            return;
                        }
                    }
                }
            }

            // 7. 提交 / 取消
            if (_btnOkRect.Contains(x, y)) Submit();
            else if (_btnCancelRect.Contains(x, y)) { Game1.playSound("bigDeSelect"); CloseAndReturn(); }
        }

        // ★ 优化 3：修复搜索村民文本框打字导致窗口关闭的问题
        public override void receiveKeyPress(Keys key)
        {
            if (key == Keys.Escape)
            {
                if (_searchBox.Selected)
                {
                    _searchBox.Selected = false;
                    if (Game1.keyboardDispatcher.Subscriber == _searchBox)
                        Game1.keyboardDispatcher.Subscriber = null;
                    Game1.playSound("bigDeSelect");
                    return;
                }

                CloseAndReturn();
                return;
            }

            // 搜索框输入处于激活状态：拦截菜单热键并实时应用筛选
            if (_searchBox.Selected)
            {
                // 严密拦截游戏全局快捷键（E/M/J/数字键等），防止误触发导致菜单被强制关闭
                if (Game1.options.doesInputListContain(Game1.options.menuButton, key) ||
                    Game1.options.doesInputListContain(Game1.options.journalButton, key) ||
                    Game1.options.doesInputListContain(Game1.options.mapButton, key) ||
                    Game1.options.doesInputListContain(Game1.options.inventorySlot1, key) ||
                    Game1.options.doesInputListContain(Game1.options.inventorySlot2, key))
                {
                    return;
                }

                ApplyFilter();
                return;
            }

            // 正文文本框快捷键输入
            if (_inputBox.Selected)
            {
                if (DialogueTextInputBox.IsControlKeyDown())
                {
                    if (key == Keys.A || key == Keys.C || key == Keys.X || key == Keys.Z || key == Keys.V)
                        _inputBox.RecieveSpecialInput(key);
                    return;
                }

                if (key == Keys.Left || key == Keys.Right || key == Keys.Up || key == Keys.Down ||
                    key == Keys.Home || key == Keys.End || key == Keys.Delete || key == Keys.Back)
                {
                    _inputBox.RecieveSpecialInput(key);
                    return;
                }

                if (key == Keys.Enter)
                {
                    Submit();
                    return;
                }
            }

            if (!Game1.options.doesInputListContain(Game1.options.menuButton, key))
                base.receiveKeyPress(key);
        }

        private void Submit()
        {
            string text = _inputBox.Text.Trim();
            if (string.IsNullOrWhiteSpace(text))
            {
                Game1.playSound("cancel");
                Game1.addHUDMessage(new HUDMessage(I18n.AddRuleMenu.ValidationEmptyContent(), HUDMessage.error_type));
                return;
            }

            if (text.Length > RuleManager.MaxRuleLength)
            {
                Game1.playSound("cancel");
                Game1.addHUDMessage(new HUDMessage(I18n.AddRuleMenu.ValidationExceedsLimit(RuleManager.MaxRuleLength), HUDMessage.error_type));
                return;
            }

            bool isWorld = _selectedNpcIds.Contains("WORLD");
            int npcCount = isWorld ? 0 : _selectedNpcIds.Count;
            if (!isWorld && npcCount == 0)
            {
                Game1.playSound("cancel");
                Game1.addHUDMessage(new HUDMessage(I18n.AddRuleMenu.ValidationNoNpcSelected(), HUDMessage.error_type));
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
                if (isWorld)
                    Game1.addHUDMessage(new HUDMessage(I18n.AddRuleMenu.SuccessHudGlobal(successCount), HUDMessage.newQuest_type));
                else
                    Game1.addHUDMessage(new HUDMessage(I18n.AddRuleMenu.SuccessHudMultiple(successCount, npcCount), HUDMessage.newQuest_type));
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
                Game1.addHUDMessage(new HUDMessage("添加失败：目标对象已存在相同规则", HUDMessage.error_type));
            }
        }


        private string FirstSelectedNpcName()
        {
            foreach (var id in _selectedNpcIds)
            {
                if (string.Equals(id, "WORLD", StringComparison.OrdinalIgnoreCase))
                    continue;
                var opt = _npcOptions.FirstOrDefault(o => string.Equals(o.Id, id, StringComparison.OrdinalIgnoreCase));
                if (opt != null)
                    return opt.DisplayName;
            }
            return "NPC";
        }
        private void CloseAndReturn()
        {
            Game1.keyboardDispatcher.Subscriber = null;
            exitThisMenu();
            _hub.RefreshEntries();
            Game1.activeClickableMenu = _hub;
        }

        // ── 渲染管线 ──────────────────────────────────────────────────────────

        public override void draw(SpriteBatch b)
        {
            int mx = Game1.getMouseX();
            int my = Game1.getMouseY();

            // 1. 全屏半透明遮罩
            b.Draw(Game1.fadeToBlackRect, Game1.graphics.GraphicsDevice.Viewport.Bounds, Color.Black * 0.5f);

            // 2. 双层羊皮纸木框底板
            IClickableMenu.drawTextureBox(b, xPositionOnScreen - 8, yPositionOnScreen - 8, width + 16, height + 16, Color.White);
            b.Draw(Game1.staminaRect, new Rectangle(xPositionOnScreen, yPositionOnScreen, width, height), new Color(245, 230, 205));
            b.Draw(Game1.menuTexture, new Rectangle(xPositionOnScreen, yPositionOnScreen, width, height),
                new Rectangle(64, 128, 64, 64), new Color(245, 230, 205));

            DrawHeader(b, mx, my);

            // 3. 规则正文输入框
            _inputBox.Draw(b);

            // 4. 属性配置栏
            int padX = ContentPadding;
            CustomFontManager.DrawString(b, I18n.AddRuleMenu.CategoryLabel(), new Vector2(xPositionOnScreen + padX, _factCapsuleRect.Y + 5), BioEditorMenu.TextSecondary, SectionHeaderSize);
            DrawPillButton(b, _factCapsuleRect, I18n.AddRuleMenu.CategoryFact(), _category == MemoryCategory.Fact, mx, my);
            DrawPillButton(b, _behaviorCapsuleRect, I18n.AddRuleMenu.CategoryBehavior(), _category == MemoryCategory.Behavior, mx, my);

            // ★ 优化 4：行为准则下时效锁定为永久有效，临时时效按钮置灰
            bool isBehavior = _category == MemoryCategory.Behavior;

            CustomFontManager.DrawString(b, "时效:", new Vector2(_durPermRect.Left - 44, _durPermRect.Y + 5), BioEditorMenu.TextSecondary, SectionHeaderSize);
            DrawPillButton(b, _durPermRect, "永久有效", _durationMode == 2, mx, my);
            DrawPillButton(b, _durTodayRect, "仅今天", !isBehavior && _durationMode == 0, mx, my, isEnabled: !isBehavior);

            if (!isBehavior && _durationMode == 1)
            {
                _dayStepper?.Draw(b);
            }
            else
            {
                DrawPillButton(b, _durCustomRect, "指定天", false, mx, my, isEnabled: !isBehavior);
            }

            // 5. 分派范围总控栏
            bool isWorld = _selectedNpcIds.Contains("WORLD");
            DrawWorldScopePill(b, _worldScopePillRect, isWorld, mx, my);

            // 搜索框（带常驻闪烁光标）
            DrawSingleLineBox(b, _searchBox);
            if (string.IsNullOrEmpty(_searchBox.Text) && !_searchBox.Selected)
            {
                CustomFontManager.DrawString(b, I18n.AddRuleMenu.NpcSearchPlaceholder(), new Vector2(_searchBox.X + 8, _searchBox.Y + 6), BioEditorMenu.TextMuted, TipFontSize);
            }

            // 筛选标签
            DrawPillButton(b, _filterAllRect, "全部", _filterMode == NpcFilterMode.All, mx, my);
            DrawPillButton(b, _filterDatableRect, "可婚单身", _filterMode == NpcFilterMode.Datable, mx, my);
            string selFilterLabel = $"已选({(_selectedNpcIds.Contains("WORLD") ? 0 : _selectedNpcIds.Count)})";
            DrawPillButton(b, _filterSelectedRect, selFilterLabel, _filterMode == NpcFilterMode.SelectedOnly, mx, my);

            // 快捷操作小按钮（无缝重构版）
            DrawQuickActionButton(b, _btnSelectAllRect, "全选", mx, my);
            DrawQuickActionButton(b, _btnInvertRect, "反选", mx, my);
            DrawQuickActionButton(b, _btnClearRect, "清空", mx, my);

            // 6. NPC 网格底槽与卡片
            b.Draw(Game1.staminaRect, new Rectangle(_npcGridBounds.X + 1, _npcGridBounds.Y + 1, _npcGridBounds.Width - 2, _npcGridBounds.Height - 2), new Color(242, 230, 208) * 0.7f);
            IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(432, 439, 9, 9),
                _npcGridBounds.X, _npcGridBounds.Y, _npcGridBounds.Width, _npcGridBounds.Height,
                new Color(223, 122, 4) * 0.65f, 2f, false);

            int cols = 3;
            int colW = (_npcGridBounds.Width - 16) / cols;
            int startRow = _npcScrollOffset;
            int visibleRows = _npcGridBounds.Height / NpcItemH;

            for (int r = 0; r < visibleRows; r++)
            {
                for (int c = 0; c < cols; c++)
                {
                    int index = (startRow + r) * cols + c;
                    if (index >= _filteredNpcs.Count) break;

                    var opt = _filteredNpcs[index];
                    var cardRect = new Rectangle(_npcGridBounds.X + 4 + c * (colW + 4), _npcGridBounds.Y + 4 + r * NpcItemH, colW, NpcItemH - 4);
                    DrawNpcCard(b, cardRect, opt, isWorld, mx, my);
                }
            }

            // 7. 底部主操作按钮

            DrawActionButton(b, _btnCancelRect, I18n.AddRuleMenu.ButtonCancel(), mx, my, isDanger: false, isPrimary: false);
            DrawActionButton(b, _btnOkRect, I18n.AddRuleMenu.ButtonConfirm(), mx, my, isDanger: false, isPrimary: true);

            // 8. ★ 优化 4：悬停气泡精准提示解释
            if (_factCapsuleRect.Contains(mx, my))
            {
                _hoverText = I18n.AddRuleMenu.TooltipCategory();
            }
            else if (_behaviorCapsuleRect.Contains(mx, my))
            {
                _hoverText = "【行为准则 (Behavior)】\n设定 NPC 的核心性格、说话习惯与行动红线（如称呼、特定偏好）。\nNPC 会尽可能严格作为准则执行。\n属于长期内在约束，固定为永久有效。";
            }
            else if (isBehavior && (_durTodayRect.Contains(mx, my) || _durCustomRect.Contains(mx, my)))
            {
                _hoverText = "【时效已锁定】\n当前分类为“行为准则”，属于角色的核心长期性格与对白约束，固定为永久生效，不可配置临时时效。";
            }
            else if (_worldScopePillRect.Contains(mx, my))
            {
                _hoverText = I18n.AddRuleMenu.TooltipScope();
            }
            else if (_filterDatableRect.Contains(mx, my))
            {
                _hoverText = "【仅显示可婚单身】\n快速筛选出小镇全部恋爱结婚候选人，便于批量配置情感与互动规则。";
            }

            if (!string.IsNullOrEmpty(_hoverText))
                DrawHoverTextCustom(b, _hoverText);

            drawMouse(b);
        }

        private void DrawHeader(SpriteBatch b, int mx, int my)
        {
            int headX = xPositionOnScreen + ContentPadding;
            int headY = yPositionOnScreen + 16;

            const string title = "新建规则 · 批量分派";
            CustomFontManager.DrawStringBold(b, title, new Vector2(headX, headY), BioEditorMenu.TextPrimary, TitleFontSize);
            bool isWorldDraw = _selectedNpcIds.Contains("WORLD");
            string subtitle = isWorldDraw ? I18n.AddRuleMenu.SubtitleGlobal() : I18n.AddRuleMenu.SubtitleNpc(FirstSelectedNpcName());
            CustomFontManager.DrawString(b, subtitle, new Vector2(headX + 2, headY + 28), BioEditorMenu.TextMuted, TipFontSize);

            int sepY = yPositionOnScreen + HeaderH + 2;
            b.Draw(Game1.staminaRect, new Rectangle(headX, sepY, width - ContentPadding * 2, 2), Color.Gray * 0.35f);

            UiHelper.UpdateButtonScale(ref _closeButtonHoverScale, _closeXButton, mx, my);
            _closeXButton.scale = CloseButtonBaseScale * _closeButtonHoverScale;
            _closeXButton.draw(b);
        }

        // ★ 优化 5：大幅加深已选中村民卡片的对比度与金木质感
        private void DrawNpcCard(SpriteBatch b, Rectangle cardRect, NpcOption opt, bool isWorldActive, int mx, int my)
        {
            bool isChecked = _selectedNpcIds.Contains(opt.Id);
            bool isHover = cardRect.Contains(mx, my);
            bool isPressed = isHover && IsLeftMouseDown();
            int pressOffset = isPressed ? 1 : 0;

            float alpha = isWorldActive ? (isHover ? 0.9f : 0.65f) : 1f;

            if (!isPressed)
                b.Draw(Game1.staminaRect, new Rectangle(cardRect.X + 1, cardRect.Y + 2, cardRect.Width, cardRect.Height), Color.Black * 0.08f);

            var drawRect = new Rectangle(cardRect.X, cardRect.Y + pressOffset, cardRect.Width, cardRect.Height);

            // ★ 选中态采用饱满的温润蜜金底色（彻底拉开与未选中的差距）
            Color bg = isChecked
                ? (isHover ? new Color(255, 226, 160) : new Color(252, 218, 145))
                : (isHover ? new Color(255, 246, 232) : new Color(248, 240, 226) * alpha);

            // ★ 选中态采用鲜明饱满的暖金橙木框
            Color borderCol = isChecked
                ? new Color(210, 110, 15)
                : (isHover ? new Color(210, 160, 60) : new Color(225, 205, 175) * alpha);

            b.Draw(Game1.staminaRect, new Rectangle(drawRect.X + 1, drawRect.Y + 1, drawRect.Width - 2, drawRect.Height - 2), bg);
            IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(432, 439, 9, 9),
                drawRect.X, drawRect.Y, drawRect.Width, drawRect.Height, borderCol, 2f, false);

            // ★ 选中项左侧 4px 亮金橙立体高亮条
            if (isChecked)
            {
                b.Draw(Game1.staminaRect, new Rectangle(drawRect.X + 2, drawRect.Y + 3, 4, drawRect.Height - 6), new Color(223, 122, 4));
            }

            // 行走图小头像框
            const int avSize = 24;
            var avRect = new Rectangle(drawRect.X + (isChecked ? 9 : 7), drawRect.Y + (drawRect.Height - avSize) / 2, avSize, avSize);

            b.Draw(Game1.staminaRect, avRect, new Color(248, 236, 212));
            IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(403, 383, 6, 6),
                avRect.X - 1, avRect.Y - 1, avRect.Width + 2, avRect.Height + 2,
                isChecked ? new Color(190, 140, 95) : new Color(220, 205, 185), 1.2f, false);

            if (opt.Sprite != null && !opt.SourceRect.IsEmpty)
            {
                b.Draw(opt.Sprite, avRect, opt.SourceRect, Color.White * alpha);
            }
            else
            {
                string initial = string.IsNullOrEmpty(opt.DisplayName) ? "?" : opt.DisplayName.Substring(0, 1);
                var isz = CustomFontManager.MeasureString(initial, TipFontSize);
                CustomFontManager.DrawString(b, initial,
                    new Vector2(avRect.X + (avSize - isz.X) / 2f, avRect.Y + (avSize - isz.Y) / 2f - 1),
                    BioEditorMenu.TextMuted, TipFontSize);
            }

            // 村民名字（选中时为锐利焦褐，清晰饱满）
            Color nameCol = isChecked ? BioEditorMenu.TextPrimary : BioEditorMenu.TextSecondary * alpha;
            CustomFontManager.DrawString(b, opt.DisplayName, new Vector2(avRect.Right + 8, drawRect.Y + (drawRect.Height - 20) / 2f), nameCol, ContentFontSize);

            // 勾选标识（鲜亮高反差）
            if (isChecked)
            {
                const string checkMark = "✔";
                var csz = CustomFontManager.MeasureStringBold(checkMark, TipFontSize);
                CustomFontManager.DrawStringBold(b, checkMark,
                    new Vector2(drawRect.Right - csz.X - 8, drawRect.Y + (drawRect.Height - csz.Y) / 2f),
                    new Color(195, 95, 10), TipFontSize);
            }
        }

        private static void DrawWorldScopePill(SpriteBatch b, Rectangle rect, bool isActive, int mx, int my)
        {
            bool isHover = rect.Contains(mx, my);
            bool isPressed = isHover && IsLeftMouseDown();
            int pressOffset = isPressed ? 1 : 0;

            Color bg = isActive
                ? (isHover ? Color.Gold : new Color(255, 220, 130))
                : (isHover ? new Color(255, 240, 225) : new Color(250, 242, 230));

            Color borderCol = isActive
                ? new Color(210, 150, 40)
                : (isHover ? new Color(210, 160, 60) : new Color(225, 205, 175));

            b.Draw(Game1.staminaRect, new Rectangle(rect.X + 1 + pressOffset, rect.Y + 1 + pressOffset, rect.Width - 2, rect.Height - 2), bg);
            IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(432, 439, 9, 9),
                rect.X + pressOffset, rect.Y + pressOffset, rect.Width, rect.Height, borderCol, 2f, false);

            string label = isActive ? "✔ 🌐 全镇共识" : "🌐 全镇共识";
            var sz = CustomFontManager.MeasureString(label, TipFontSize);
            CustomFontManager.DrawString(b, label,
                new Vector2(rect.X + pressOffset + (rect.Width - sz.X) / 2f, rect.Y + pressOffset + (rect.Height - sz.Y) / 2f - 1),
                isActive ? BioEditorMenu.TextPrimary : BioEditorMenu.TextSecondary, TipFontSize);
        }

        private static bool IsLeftMouseDown()
        {
            try { return Game1.input.GetMouseState().LeftButton == ButtonState.Pressed; }
            catch { return false; }
        }

        private static void DrawPillButton(SpriteBatch b, Rectangle rect, string label, bool isActive, int mx, int my, bool isEnabled = true)
        {
            bool isHover = isEnabled && rect.Contains(mx, my);
            bool isPressed = isHover && IsLeftMouseDown();

            Color bg;
            if (!isEnabled)
            {
                bg = new Color(235, 228, 220) * 0.7f; // 禁用灰木底
            }
            else if (isActive)
            {
                bg = isHover ? Color.Gold : new Color(255, 220, 130);
            }
            else
            {
                bg = isHover ? new Color(255, 235, 205) : Color.White;
            }

            int pressOffset = isPressed ? 1 : 0;
            if (isPressed) bg = Color.Lerp(bg, Color.Black, 0.14f);

            b.Draw(Game1.staminaRect, new Rectangle(rect.X + 1 + pressOffset, rect.Y + 1 + pressOffset, rect.Width - 2, rect.Height - 2), bg);
            IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(432, 439, 9, 9),
                rect.X + pressOffset, rect.Y + pressOffset, rect.Width, rect.Height,
                !isEnabled ? new Color(215, 205, 195) * 0.8f : (isActive ? new Color(200, 150, 50) : Color.Wheat), 2f, false);

            var sz = CustomFontManager.MeasureString(label, TipFontSize);
            CustomFontManager.DrawString(b, label,
                new Vector2(rect.X + pressOffset + (rect.Width - sz.X) / 2f, rect.Y + pressOffset + (rect.Height - sz.Y) / 2f - 1),
                !isEnabled ? BioEditorMenu.TextMuted : (isActive ? BioEditorMenu.TextPrimary : BioEditorMenu.TextSecondary), TipFontSize);
        }

        // ★ 优化 2：重写快捷按钮，使用整像素 2f 缩放与立体底层，彻底根除右侧和底部的白缝
        private static void DrawQuickActionButton(SpriteBatch b, Rectangle rect, string label, int mx, int my)
        {
            bool isHover = rect.Contains(mx, my);
            bool isPressed = isHover && IsLeftMouseDown();
            int pressOffset = isPressed ? 1 : 0;

            // 立体底层微阴影
            if (!isPressed)
                b.Draw(Game1.staminaRect, new Rectangle(rect.X + 1, rect.Y + 2, rect.Width, rect.Height), Color.Black * 0.10f);

            var drawRect = new Rectangle(rect.X + pressOffset, rect.Y + pressOffset, rect.Width, rect.Height);

            Color bg = isHover ? new Color(255, 238, 215) : new Color(246, 234, 216);
            Color borderCol = isHover ? new Color(210, 160, 60) : new Color(225, 205, 175);

            // 内衬全覆盖铺满，防止背景露出白缝
            b.Draw(Game1.staminaRect, new Rectangle(drawRect.X + 1, drawRect.Y + 1, drawRect.Width - 2, drawRect.Height - 2), bg);

            // 严格采用 2f 整像素 scale，消灭 1.6f 导致的浮点栅格断裂
            IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(432, 439, 9, 9),
                drawRect.X, drawRect.Y, drawRect.Width, drawRect.Height,
                borderCol, 2f, false);

            var sz = CustomFontManager.MeasureString(label, TipFontSize);
            CustomFontManager.DrawString(b, label,
                new Vector2(drawRect.X + (drawRect.Width - sz.X) / 2f, drawRect.Y + (drawRect.Height - sz.Y) / 2f - 1),
                BioEditorMenu.TextPrimary, TipFontSize);
        }

        // ★ 优化 3：修复搜索单行框光标闪烁
        private static void DrawSingleLineBox(SpriteBatch b, TextBox box)
        {
            var boxRect = new Rectangle(box.X, box.Y, box.Width, box.Height);
            Color innerBg = box.Selected ? new Color(255, 252, 245) : new Color(248, 242, 230);

            b.Draw(Game1.staminaRect, new Rectangle(boxRect.X + 2, boxRect.Y + 2, boxRect.Width - 4, boxRect.Height - 4), innerBg);
            IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(432, 439, 9, 9),
                boxRect.X, boxRect.Y, boxRect.Width, boxRect.Height,
                box.Selected ? Color.White : new Color(225, 195, 155), 1.8f, false);

            string text = box.Text ?? string.Empty;
            float textW = string.IsNullOrEmpty(text) ? 0f : CustomFontManager.MeasureString(text, TipFontSize).X;
            float tx = boxRect.X + 8;

            if (!string.IsNullOrEmpty(text))
            {
                float textH = CustomFontManager.MeasureString(text, TipFontSize).Y;
                float ty = boxRect.Y + (boxRect.Height - textH) / 2f - 1;
                CustomFontManager.DrawString(b, text, new Vector2(tx, ty), BioEditorMenu.TextPrimary, TipFontSize);
            }

            // 独立光标高度计算：即便 text 为空也常驻闪烁
            if (box.Selected && (int)(Game1.currentGameTime.TotalGameTime.TotalMilliseconds / 500) % 2 == 0)
            {
                int cursorH = Math.Min(18, boxRect.Height - 8);
                int cursorY = boxRect.Y + (boxRect.Height - cursorH) / 2;
                b.Draw(Game1.staminaRect, new Rectangle((int)(tx + textW + 1), cursorY, 2, cursorH), BioEditorMenu.TextPrimary);
            }
        }

        private static void DrawActionButton(SpriteBatch b, Rectangle rect, string label, int mx, int my,
            bool isDanger = false, bool isPrimary = false, bool isEnabled = true)
        {
            bool isHover = isEnabled && rect.Contains(mx, my);
            bool isPressed = isHover && IsLeftMouseDown();

            Color bg;
            if (!isEnabled) bg = Color.LightGray * 0.6f;
            else if (isPrimary) bg = isHover ? Color.Gold : new Color(255, 220, 130);
            else if (isDanger) bg = isHover ? new Color(245, 105, 105) : new Color(210, 85, 80);
            else bg = isHover ? new Color(255, 240, 215) : new Color(225, 195, 155);

            int pressOffset = isPressed ? 1 : 0;
            if (isPressed) bg = Color.Lerp(bg, Color.Black, 0.14f);

            if (!isPressed)
                b.Draw(Game1.staminaRect, new Rectangle(rect.X + 2, rect.Y + 2, rect.Width, rect.Height), Color.Black * 0.15f);

            b.Draw(Game1.staminaRect, new Rectangle(rect.X + 1 + pressOffset, rect.Y + 1 + pressOffset, rect.Width - 2, rect.Height - 2), bg);

            IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(432, 439, 9, 9),
                rect.X + pressOffset, rect.Y + pressOffset, rect.Width, rect.Height,
                isPrimary ? new Color(210, 160, 60) : (isDanger ? new Color(175, 60, 55) : new Color(185, 150, 110)), 3f, false);

            Color textCol = !isEnabled ? BioEditorMenu.TextMuted
                          : isDanger ? BioEditorMenu.TextOnDarkBtn
                          : BioEditorMenu.TextOnLightBtn;

            var drawRect = new Rectangle(rect.X + pressOffset, rect.Y + pressOffset, rect.Width, rect.Height);
            ButtonTextRenderer.DrawButtonText(b, label, drawRect, textCol, useBold: true);

        }

        private static void DrawHoverTextCustom(SpriteBatch b, string text)
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
                frameWidth = npc.Sprite.SpriteWidth;
            else if (texture.Width >= 64)
                frameWidth = texture.Width / 4;
            else if (texture.Width >= 32)
                frameWidth = texture.Width / 2;
            else
                frameWidth = texture.Width;

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
                            return y;
                    }
                }
            }
            catch { }
            return 0;
        }
    }
}