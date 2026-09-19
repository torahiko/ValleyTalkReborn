using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;

namespace ValleytalkReborn
{
    /// <summary>
    /// 简单的复选框辅助类（用于替代原版 OptionsCheckbox）
    /// </summary>
    internal sealed class SimpleCheckbox
    {
        public Rectangle bounds;
        public bool isChecked;
        public string label;

        public SimpleCheckbox(string label, int unused, int x, int y)
        {
            this.label = label;
            this.bounds = new Rectangle(x, y, 28, 28);
        }

        public void receiveLeftClick(int x, int y)
        {
            if (bounds.Contains(x, y))
            {
                isChecked = !isChecked;
                Game1.playSound("select");
            }
        }

        public void draw(SpriteBatch b, int unused1, int unused2, IClickableMenu parent)
        {
            int mx = Game1.getMouseX();
            int my = Game1.getMouseY();
            Rectangle src = isChecked ? new Rectangle(236, 425, 9, 9) : new Rectangle(227, 425, 9, 9);
            b.Draw(Game1.mouseCursors, new Vector2(bounds.X, bounds.Y), src, Color.White, 0f, Vector2.Zero, 3.5f, SpriteEffects.None, 1f);

            if (!string.IsNullOrEmpty(label))
                // FONT-02: 复选框标签（原 smallFont → 18.5f）
                CustomFontManager.DrawString(b, label, new Vector2(bounds.X + 36, bounds.Y + 6), Game1.textColor, 18.5f);
        }
    }

    /// <summary>
    /// 角色人设沉浸式编辑器（现代化重构版）：
    /// 专为玩家设计的直观可视化布局、标签化关注池、所见即所得门禁与 NPC 快捷过滤。
    /// </summary>
    internal sealed class BioEditorMenu : IClickableMenu
    {
        // ── 布局尺寸常量 ──────────────────────────────────────────────────
        private const int HeaderH = 68;
        private const int TabBarH = 38;
        private const int FooterH = 56;
        private const int ContentPadding = 24;

        private static readonly string[] TabTitles = new[]
        {
            "1. 身份背景",
            "2. 言行举止",
            "3. 好感演变",
            "4. 社交关系",
            "5. 环境感知",
        };

        // ── 核心状态 ──────────────────────────────────────────────────────
        private readonly string _npcName;
        private readonly IClickableMenu _returnMenu;
        private BioData _bio;
        private bool _hasOverlay;
        private bool _dirty;
        private int _activeTab;
        private string _hoverText;

        private Texture2D _npcPortrait;
        private Rectangle _portraitSmileRect;

        // 通用组件
        private ClickableTextureComponent _closeButton;
        private readonly Rectangle[] _tabRects = new Rectangle[5];
        private Rectangle _cancelRect;
        private Rectangle _saveRect;
        private Rectangle _resetRect;

        // ── Tab 1 控件（身份心理） ────────────────────────────────────────
        private MultilineTextBox _biographyBox;
        private TextBox _uniqueBox;
        private SimpleCheckbox _homeBedCheckbox;
        private Rectangle _scaffoldBtnRect;
        private Rectangle _uniqueCardRect;
        private Rectangle _homeBedCardRect;

        private static readonly string BiographyScaffold =
            "[IDENTITY]\n- Identity: You are {NPC}.\n- Social Anchor: \n- Living Situation: \n\n" +
            "[PSYCHOLOGICAL CONFLICTS]\n- ";

        // ── Tab 2 控件（言行举止） ────────────────────────────────────────
        private MultilineTextBox _behaviorBox;
        private MultilineTextBox _dialogueExamplesBox;
        private Rectangle _behaviorScaffoldRect;
        private Rectangle _insertBreakRect;
        private Rectangle _insertChoiceRect;
        private Rectangle _tab2LeftColRect;
        private Rectangle _tab2RightColRect;

        // ── Tab 3 控件（好感演变） ────────────────────────────────────────
        private int _stageIdx = -1;
        private MultilineTextBox _stageTextBox;
        private MultilineTextBox _stageBarkBox;
        private TagListEditor _stageTagEditor;
        private NumberStepper _heartsStepper;

        private Rectangle _stageLeftColRect;
        private Rectangle _stageRightColRect;
        private readonly Rectangle[] _stageRowRects = new Rectangle[8];
        private Rectangle _newStageRect;
        private Rectangle _deleteStageRect;
        private Rectangle _gateMarriedPillRect;
        private Rectangle _gateJojaClosedPillRect;
        private Rectangle _gateJojaMemberPillRect;

        // ── Tab 4 控件（社交关系） ────────────────────────────────────────
        private int _relSelectedIndex = -1;
        private readonly List<string> _allNpcs = new();
        private readonly List<string> _filteredNpcs = new();
        private string _relSelectedNpc = "";
        private int _relListScrollOffset = 0;
        private TextBox _relSearchBox;
        private TextBox _relHeadingBox;
        private MultilineTextBox _relDescBox;

        private Rectangle _relLeftColRect;
        private Rectangle _relRightColRect;
        private Rectangle _relAddRect;
        private Rectangle _relDelRect;
        private readonly List<(Rectangle Rect, int Index)> _relVisibleItemRects = new();

        // ── Tab 5 控件（环境感知） ────────────────────────────────────────
        private SimpleCheckbox _enableBarkCheckbox;
        private TagListEditor _globalTagEditor;
        private MultilineTextBox _voiceBox;
        private MultilineTextBox _habitsBox;
        private MultilineTextBox _lensesBox;
        private Rectangle _scrapeRect;
        private Rectangle _tab5LeftColRect;
        private Rectangle _tab5RightColRect;

        // ── 构造函数 ──────────────────────────────────────────────────────
        public BioEditorMenu(string npcName, IClickableMenu returnMenu)
            : base(
                (Game1.uiViewport.Width - Math.Clamp(Game1.uiViewport.Width - 100, 920, 1160)) / 2,
                (Game1.uiViewport.Height - Math.Clamp(Game1.uiViewport.Height - 80, 600, 750)) / 2,
                Math.Clamp(Game1.uiViewport.Width - 100, 920, 1160),
                Math.Clamp(Game1.uiViewport.Height - 80, 600, 750),
                showUpperRightCloseButton: false)
        {
            _npcName = npcName;
            _returnMenu = returnMenu;

            _bio = ModEntry.BioStorage!.LoadEditableBio(npcName);
            _hasOverlay = ModEntry.BioStorage.HasCustomOverlay(npcName);
            _activeTab = 0;

            LoadNpcPortrait();

            // 初始化文本框组件
            _biographyBox = new MultilineTextBox(Rectangle.Empty, maxLines: 512);
            _behaviorBox = new MultilineTextBox(Rectangle.Empty, maxLines: 512);
            _dialogueExamplesBox = new MultilineTextBox(Rectangle.Empty, maxLines: 512);
            _stageTextBox = new MultilineTextBox(Rectangle.Empty, maxLines: 512);
            _stageBarkBox = new MultilineTextBox(Rectangle.Empty, maxLines: 512);
            _relDescBox = new MultilineTextBox(Rectangle.Empty, maxLines: 512);
            _voiceBox = new MultilineTextBox(Rectangle.Empty, maxLines: 512);
            _habitsBox = new MultilineTextBox(Rectangle.Empty, maxLines: 512);
            _lensesBox = new MultilineTextBox(Rectangle.Empty, maxLines: 512);

            Texture2D boxTex = LoadTextBoxTexture();
            _uniqueBox = new TextBox(boxTex, null, Game1.smallFont, Game1.textColor);
            _relSearchBox = new TextBox(boxTex, null, Game1.smallFont, Game1.textColor);
            _relHeadingBox = new TextBox(boxTex, null, Game1.smallFont, Game1.textColor);

            _homeBedCheckbox = new SimpleCheckbox("床位固定 (HomeLocationBed)", -1, 0, 0);
            _enableBarkCheckbox = new SimpleCheckbox("启用日常碎碎念 (AmbientBarks)", -1, 0, 0);

            // 标签胶囊编辑器与门禁步进器初始化
            _stageTagEditor = new TagListEditor(Rectangle.Empty);
            _stageTagEditor.OnChanged += () =>
            {
                if (_stageIdx >= 0 && _stageIdx < _bio.ProgressStates.Count)
                {
                    _bio.ProgressStates[_stageIdx].Preoccupations = _stageTagEditor.Tags.Count > 0 ? _stageTagEditor.Tags.ToList() : null;
                    MarkDirty();
                }
            };

            _globalTagEditor = new TagListEditor(Rectangle.Empty);
            _globalTagEditor.OnChanged += () =>
            {
                _bio.Preoccupations = _globalTagEditor.Tags.Count > 0 ? _globalTagEditor.Tags.ToList() : null;
                MarkDirty();
            };

            _heartsStepper = new NumberStepper(Rectangle.Empty, 0, 0, 14, 2, " 心"); 
            _heartsStepper.OnChanged += val =>
            {
                if (_stageIdx >= 0 && _stageIdx < _bio.ProgressStates.Count)
                {
                    _bio.ProgressStates[_stageIdx].RequiredHearts = val;
                    MarkDirty();
                }
            };

            _closeButton = new ClickableTextureComponent(
                new Rectangle(xPositionOnScreen + width - 52, yPositionOnScreen + 16, 36, 36),
                Game1.mouseCursors, new Rectangle(337, 494, 12, 12), 3f);

            // 装载初始数据
            _biographyBox.Text = _bio.Biography ?? string.Empty;
            _uniqueBox.Text = _bio.Unique ?? string.Empty;
            _homeBedCheckbox.isChecked = _bio.HomeLocationBed;

            _enableBarkCheckbox.isChecked = _bio.EnableAmbientBarks;
            _globalTagEditor.SetTags(_bio.Preoccupations);
            SyncTab5BarkBoxes();

            InitNpcList();
            if (_filteredNpcs.Count > 0)
                SelectRelationship(0);

            if (_bio.ProgressStates.Count > 0)
                SelectStage(0);

            Layout();
        }

        private void LoadNpcPortrait()
        {
            try
            {
                var character = Game1.getCharacterFromName(_npcName);
                _npcPortrait = (character?.Portrait != null && !character.Portrait.IsDisposed)
                    ? character.Portrait
                    : Game1.content.Load<Texture2D>("Portraits\\" + _npcName);

                if (_npcPortrait != null)
                {
                    if (_npcPortrait.Width >= 128 && _npcPortrait.Height >= 64)
                        _portraitSmileRect = new Rectangle(64, 0, 64, 64);
                    else if (_npcPortrait.Width >= 64 && _npcPortrait.Height >= 128)
                        _portraitSmileRect = new Rectangle(0, 64, 64, 64);
                    else
                        _portraitSmileRect = new Rectangle(0, 0, Math.Min(64, _npcPortrait.Width), Math.Min(64, _npcPortrait.Height));
                }
            }
            catch
            {
                _npcPortrait = null;
                _portraitSmileRect = Rectangle.Empty;
            }
        }

        // ── 界面几何布局重构 ──────────────────────────────────────────────
        private void Layout()
        {
            _closeButton.bounds = new Rectangle(xPositionOnScreen + width - 52, yPositionOnScreen + 16, 36, 36);

            int contentLeft = xPositionOnScreen + ContentPadding;
            int contentW = width - ContentPadding * 2;

            // 1. Tab 栏
            int tabGap = 8;
            int tabW = (contentW - tabGap * (TabTitles.Length - 1)) / TabTitles.Length;
            int tabY = yPositionOnScreen + HeaderH + 4;
            for (int i = 0; i < TabTitles.Length; i++)
                _tabRects[i] = new Rectangle(contentLeft + i * (tabW + tabGap), tabY, tabW, TabBarH);

            // 2. 底部功能栏
            int footerY = yPositionOnScreen + height - FooterH + 10;
            int btnH = 38;
            int btnW = Math.Clamp(contentW / 4, 150, 220);

            _cancelRect = new Rectangle(contentLeft, footerY, btnW, btnH);
            _resetRect = new Rectangle(contentLeft + btnW + 16, footerY, btnW, btnH);
            _saveRect = new Rectangle(xPositionOnScreen + width - ContentPadding - btnW, footerY, btnW, btnH);

            // 3. 内容区总空间
            int bodyTop = tabY + TabBarH + 12;
            int bodyBottom = footerY - 14;
            int bodyH = bodyBottom - bodyTop;

            // ── Tab 1 布局（身份心理） ──
            if (_activeTab == 0)
            {
                int cardH = 72;
                int bioH = bodyH - cardH - 36;
                _biographyBox.SetBounds(new Rectangle(contentLeft, bodyTop + 28, contentW, bioH));
                _scaffoldBtnRect = new Rectangle(contentLeft + contentW - 140, bodyTop, 140, 26);

                int cardY = _biographyBox.Bounds.Bottom + 12;
                int cardW = (contentW - 16) / 2;
                _uniqueCardRect = new Rectangle(contentLeft, cardY, cardW, cardH);
                _homeBedCardRect = new Rectangle(contentLeft + cardW + 16, cardY, cardW, cardH);

                _uniqueBox.X = _uniqueCardRect.X + 12;
                _uniqueBox.Y = _uniqueCardRect.Y + 32;
                _uniqueBox.Width = _uniqueCardRect.Width - 24;
                _uniqueBox.Height = 30;

                _homeBedCheckbox.bounds = new Rectangle(_homeBedCardRect.X + 16, _homeBedCardRect.Y + 30, 28, 28);
            }

            // ── Tab 2 布局（言行举止） ──
            if (_activeTab == 1)
            {
                int colW = (contentW - 16) / 2;
                _tab2LeftColRect = new Rectangle(contentLeft, bodyTop, colW, bodyH);
                _tab2RightColRect = new Rectangle(contentLeft + colW + 16, bodyTop, colW, bodyH);

                // 顶部预留 36px 给标题与按钮，底部预留 28px 给说明文字，留给输入框的可用高度为 bodyH - 64
                int headerOffset = 36;
                int boxH = bodyH - headerOffset - 28;

                // 左列：插入规则模板按钮
                _behaviorScaffoldRect = new Rectangle(_tab2LeftColRect.Right - 130, bodyTop + 2, 130, 26);
                _behaviorBox.SetBounds(new Rectangle(_tab2LeftColRect.X, bodyTop + headerOffset, colW, boxH));

                // 右列：对白编辑快捷按钮（微调紧凑，防止窄屏下遮挡标题）
                int toolBtnW = 95;
                _insertChoiceRect = new Rectangle(_tab2RightColRect.Right - toolBtnW, bodyTop + 2, toolBtnW, 26);
                _insertBreakRect = new Rectangle(_insertChoiceRect.Left - toolBtnW - 6, bodyTop + 2, toolBtnW, 26);

                _dialogueExamplesBox.SetBounds(new Rectangle(_tab2RightColRect.X, bodyTop + headerOffset, colW, boxH));
            }

            // ── Tab 3 布局（好感演变：彻底拉开文本与输入框间隙） ──
            if (_activeTab == 2)
            {
                int leftColW = 240;
                int rightColW = contentW - leftColW - 16;
                _stageLeftColRect = new Rectangle(contentLeft, bodyTop, leftColW, bodyH);
                _stageRightColRect = new Rectangle(contentLeft + leftColW + 16, bodyTop, rightColW, bodyH);

                int rowH = 38;
                for (int i = 0; i < _stageRowRects.Length; i++)
                    _stageRowRects[i] = new Rectangle(_stageLeftColRect.X, _stageLeftColRect.Y + 32 + i * (rowH + 4), leftColW, rowH);

                int nextY = _stageLeftColRect.Y + 32 + Math.Min(_bio.ProgressStates.Count, 8) * (rowH + 4);
                _newStageRect = new Rectangle(_stageLeftColRect.X, nextY, leftColW, 34);

                int rightX = _stageRightColRect.X;
                int rightY = _stageRightColRect.Y;

                // 门禁药丸栏高度预留
                _heartsStepper.SetBounds(new Rectangle(rightX + 90, rightY, 110, 28));
                _gateMarriedPillRect = new Rectangle(rightX + 208, rightY, 100, 28);
                _gateJojaClosedPillRect = new Rectangle(rightX + 316, rightY, 110, 28);
                _gateJojaMemberPillRect = new Rectangle(rightX + 434, rightY, 110, 28);
                _deleteStageRect = new Rectangle(rightX + rightColW - 100, rightY, 100, 28);

                // 关键修复：向下推开 38px 给标题行，彻底避开边框外扩
                int editorStartY = rightY + 38;
                int bottomSpace = 85; // 预留给下方关注池
                int textAreasH = bodyBottom - editorStartY - bottomSpace;

                // 均分两个文本框，每个文本框顶部预留安全距离给中文字体
                int singleBoxH = (textAreasH - 64) / 2;

                int box1Y = editorStartY + 28;
                _stageTextBox.SetBounds(new Rectangle(rightX, box1Y, rightColW, singleBoxH));

                int box2Y = _stageTextBox.Bounds.Bottom + 34;
                _stageBarkBox.SetBounds(new Rectangle(rightX, box2Y, rightColW, singleBoxH));

                _stageTagEditor.SetBounds(new Rectangle(rightX, bodyBottom - 64, rightColW, 60));
            }

            // ── Tab 4 布局（社交关系：彻底解决标题与框体贴合） ──
            if (_activeTab == 3)
            {
                int leftColW = 250;
                int rightColW = contentW - leftColW - 16;
                _relLeftColRect = new Rectangle(contentLeft, bodyTop, leftColW, bodyH);
                _relRightColRect = new Rectangle(contentLeft + leftColW + 16, bodyTop, rightColW, bodyH);

                // 搜索栏
                _relSearchBox.X = _relLeftColRect.X;
                _relSearchBox.Y = _relLeftColRect.Y + 28;
                _relSearchBox.Width = _relLeftColRect.Width;
                _relSearchBox.Height = 28;

                int relBtnW = (leftColW - 8) / 2;
                _relAddRect = new Rectangle(_relLeftColRect.X, _relLeftColRect.Bottom - 34, relBtnW, 34);
                _relDelRect = new Rectangle(_relLeftColRect.X + relBtnW + 8, _relLeftColRect.Bottom - 34, relBtnW, 34);

                int rightX = _relRightColRect.X;

                // 关键修复：单行框从 bodyTop + 68 开始，为上方顶层大标题和副标题留够充足空间
                _relHeadingBox.X = rightX;
                _relHeadingBox.Y = bodyTop + 68;
                _relHeadingBox.Width = rightColW;
                _relHeadingBox.Height = 32;

                // 多行描述框从单行框底边 + 42px 开始，给中间的提示文字留足 34px 空间
                int descTop = _relHeadingBox.Y + _relHeadingBox.Height + 42;
                int descH = bodyBottom - descTop - 28;
                _relDescBox.SetBounds(new Rectangle(rightX, descTop, rightColW, descH));

                RecalculateTab4List();
            }

            // ── Tab 5 布局（环境感知） ──
            if (_activeTab == 4)
            {
                int leftColW = 340;
                int rightColW = contentW - leftColW - 16;
                _tab5LeftColRect = new Rectangle(contentLeft, bodyTop, leftColW, bodyH);
                _tab5RightColRect = new Rectangle(contentLeft + leftColW + 16, bodyTop, rightColW, bodyH);

                _enableBarkCheckbox.bounds = new Rectangle(_tab5LeftColRect.X + 8, _tab5LeftColRect.Y + 28, 28, 28);
                _scrapeRect = new Rectangle(_tab5LeftColRect.X, _tab5LeftColRect.Y + 74, leftColW, 34);

                _globalTagEditor.SetBounds(new Rectangle(_tab5LeftColRect.X, _tab5LeftColRect.Y + 160, leftColW, bodyBottom - (_tab5LeftColRect.Y + 160)));

                int rightX = _tab5RightColRect.X;
                int rightY = _tab5RightColRect.Y;
                int sectionH = (bodyH - 52) / 3;

                _voiceBox.SetBounds(new Rectangle(rightX, rightY + 22, rightColW, sectionH - 24));
                _habitsBox.SetBounds(new Rectangle(rightX, rightY + sectionH + 22, rightColW, sectionH - 24));
                _lensesBox.SetBounds(new Rectangle(rightX, rightY + sectionH * 2 + 22, rightColW, sectionH - 24));
            }
        }

        // ── 社交关系列表与精准过滤搜索 ────────────────────────────────────────
        private void InitNpcList()
        {
            _allNpcs.Clear();
            var friendshipData = Game1.player?.friendshipData;

            // 1. 系统/过场演出用假人/特殊剧情专用角色黑名单（参考 IntegratedHubMenu）
            var excludedNpcs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Grandpa",   // 爷爷
                "Governor",  // 州长
                "Gil",       // 吉尔
                "Bouncer",   // 赌场保镖
                "Birdie",    // 伯迪
                "Henchman",  // 仆从
                "MarlonFudge"// 1.6 矿洞/特殊剧情马龙克隆体
            };

            // 辅助判定：是否为剧情演出或无效假人
            bool IsInvalidOrEventNpc(string internalName, NPC npc)
            {
                if (string.IsNullOrWhiteSpace(internalName)) return true;

                // 排除当前正在编辑的 NPC 本人
                if (string.Equals(internalName, _npcName, StringComparison.OrdinalIgnoreCase))
                    return true;

                // 命中硬编码黑名单
                if (excludedNpcs.Contains(internalName))
                    return true;

                // 剧情演出假人常见命名模式：包含下划线、Event、Fake、Dummy 等
                if (internalName.Contains("_") ||
                    internalName.Contains("Event", StringComparison.OrdinalIgnoreCase) ||
                    internalName.Contains("Fake", StringComparison.OrdinalIgnoreCase) ||
                    internalName.Contains("Dummy", StringComparison.OrdinalIgnoreCase))
                    return true;

                // 针对 Marlon 的克隆变体（如 MarlonFudge, MarlonFestival 等）坚决剔除，纯正的 "Marlon" 才是本体
                bool isTrueMarlon = internalName.Equals("Marlon", StringComparison.OrdinalIgnoreCase);
                if (internalName.StartsWith("Marlon", StringComparison.OrdinalIgnoreCase) && !isTrueMarlon)
                    return true;

                bool inFriendship = friendshipData != null && friendshipData.ContainsKey(internalName);

                // 如果获取到了运行时的 NPC 实例进一步校验
                if (npc != null)
                {
                    // 通过当前过场的 actors 列表判定是否为过场临时演员
                    if (Game1.CurrentEvent != null && Game1.CurrentEvent.actors != null && Game1.CurrentEvent.actors.Contains(npc))
                        return true;

                    // 原版马龙不可社交(CanSocialize=false)且不在好感度中，但他是核心NPC，必须放行；其他不可社交且无好感度的直接剔除
                    if (!isTrueMarlon && !npc.CanSocialize && !inFriendship)
                        return true;
                }
                else
                {
                    // 无运行时实例且不在好感度表中时：除了正统 Marlon 外，其他均视作无效
                    if (!isTrueMarlon && !inFriendship)
                        return true;
                }

                return false;
            }

            // 收集所有候选角色名字
            var rawCandidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 1) 优先从好感度列表（最可信）提取
            if (friendshipData != null)
            {
                foreach (var k in friendshipData.Keys)
                {
                    if (!IsInvalidOrEventNpc(k, Game1.getCharacterFromName(k)))
                        rawCandidates.Add(k);
                }
            }

            // 2) 从 CharacterData 基础表补充（支持拓展模组 NPC）
            if (Game1.characterData != null)
            {
                foreach (var kvp in Game1.characterData)
                {
                    string name = kvp.Key;
                    if (!IsInvalidOrEventNpc(name, Game1.getCharacterFromName(name)))
                        rawCandidates.Add(name);
                }
            }

            // 3) 从场景活跃角色补充
            foreach (var npc in Utility.getAllCharacters())
            {
                if (npc != null && (npc.IsVillager || npc.Name.Equals("Marlon", StringComparison.OrdinalIgnoreCase)) && !IsInvalidOrEventNpc(npc.Name, npc))
                {
                    rawCandidates.Add(npc.Name);
                }
            }

            // 4) 确保当前已存在的自定义关系角色（若有配置）不会被漏掉
            if (_bio.Relationships != null)
            {
                foreach (var configuredKey in _bio.Relationships.Keys)
                {
                    if (!string.Equals(configuredKey, _npcName, StringComparison.OrdinalIgnoreCase))
                        rawCandidates.Add(configuredKey);
                }
            }

            // 2. 核心去重：按 DisplayName 去重，防止任何模组克隆人导致双重名单
            var resolvedNpcs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var name in rawCandidates)
            {
                string dispName = Game1.getCharacterFromName(name)?.displayName;
                if (string.IsNullOrWhiteSpace(dispName)) dispName = name;

                if (resolvedNpcs.TryGetValue(dispName, out var existingInternalName))
                {
                    bool isCurrentTrueMarlon = name.Equals("Marlon", StringComparison.OrdinalIgnoreCase);
                    bool isExistingTrueMarlon = existingInternalName.Equals("Marlon", StringComparison.OrdinalIgnoreCase);

                    if (isCurrentTrueMarlon && !isExistingTrueMarlon)
                    {
                        resolvedNpcs[dispName] = name;
                    }
                    else if (!isCurrentTrueMarlon && isExistingTrueMarlon)
                    {
                        continue;
                    }
                    else
                    {
                        bool existingHasFriendship = friendshipData != null && friendshipData.ContainsKey(existingInternalName);
                        bool currentHasFriendship = friendshipData != null && friendshipData.ContainsKey(name);
                        if ((currentHasFriendship && !existingHasFriendship) ||
                            (currentHasFriendship == existingHasFriendship && name.Length < existingInternalName.Length))
                        {
                            resolvedNpcs[dispName] = name;
                        }
                    }
                }
                else
                {
                    resolvedNpcs[dispName] = name;
                }
            }

            _allNpcs.AddRange(resolvedNpcs.Values);
            FilterNpcList();
        }

        private void FilterNpcList()
        {
            _filteredNpcs.Clear();
            string query = _relSearchBox?.Text?.Trim() ?? "";

            // 优先排序已配置的角色
            var sorted = _allNpcs.OrderByDescending(n => _bio.Relationships.ContainsKey(n))
                                 .ThenBy(n => Game1.getCharacterFromName(n)?.displayName ?? n);

            foreach (var name in sorted)
            {
                string disp = Game1.getCharacterFromName(name)?.displayName ?? name;
                if (string.IsNullOrEmpty(query) ||
                    disp.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                    name.Contains(query, StringComparison.OrdinalIgnoreCase))
                {
                    _filteredNpcs.Add(name);
                }
            }
            RecalculateTab4List();
        }

        private void RecalculateTab4List()
        {
            _relVisibleItemRects.Clear();
            int listTop = _relLeftColRect.Y + 68;
            int listAvailH = _relLeftColRect.Height - 68 - 46;
            int rowH = 36;
            int maxVisible = Math.Max(1, listAvailH / rowH);

            _relListScrollOffset = Math.Clamp(_relListScrollOffset, 0, Math.Max(0, _filteredNpcs.Count - maxVisible));

            for (int i = 0; i < maxVisible && _relListScrollOffset + i < _filteredNpcs.Count; i++)
            {
                int idx = _relListScrollOffset + i;
                var r = new Rectangle(_relLeftColRect.X, listTop + i * rowH, _relLeftColRect.Width, rowH - 4);
                _relVisibleItemRects.Add((r, idx));
            }
        }

        // ── 帧更新 ────────────────────────────────────────────────────────
        public override void update(GameTime time)
        {
            base.update(time);
            _hoverText = null;

            if (_activeTab == 0)
            {
                _biographyBox.Update(time);
                if (_biographyBox.Text != _bio.Biography) { _bio.Biography = _biographyBox.Text; MarkDirty(); }
                if (_uniqueBox.Text != (_bio.Unique ?? string.Empty)) { _bio.Unique = _uniqueBox.Text; MarkDirty(); }
                if (_homeBedCheckbox.isChecked != _bio.HomeLocationBed) { _bio.HomeLocationBed = _homeBedCheckbox.isChecked; MarkDirty(); }
            }
            else if (_activeTab == 1)
            {
                _behaviorBox.Update(time);
                _dialogueExamplesBox.Update(time);

                string bText = _behaviorBox.Text ?? string.Empty;
                if (_bio.Traits.TryGetValue("BehavioralRules", out var bEntry) && bEntry != null)
                {
                    if (bEntry.Description != bText) { bEntry.Description = bText; MarkDirty(); }
                }
                else if (!string.IsNullOrEmpty(bText))
                {
                    EnsureTraitEntry("BehavioralRules", "Tone & Mannerisms Constraints").Description = bText;
                    MarkDirty();
                }

                string dText = _dialogueExamplesBox.Text ?? string.Empty;
                if (_bio.Traits.TryGetValue("DialogueExamples", out var dEntry) && dEntry != null)
                {
                    if (dEntry.Description != dText) { dEntry.Description = dText; MarkDirty(); }
                }
                else if (!string.IsNullOrEmpty(dText))
                {
                    EnsureTraitEntry("DialogueExamples", "Dialogue Examples").Description = dText;
                    MarkDirty();
                }
            }
            else if (_activeTab == 2)
            {
                if (_stageIdx >= 0 && _stageIdx < _bio.ProgressStates.Count)
                {
                    var s = _bio.ProgressStates[_stageIdx];
                    _stageTextBox.Update(time);
                    _stageBarkBox.Update(time);

                    if ((s.Text ?? "") != _stageTextBox.Text) { s.Text = _stageTextBox.Text; MarkDirty(); }
                    if ((s.BarkMindset ?? "") != _stageBarkBox.Text) { s.BarkMindset = _stageBarkBox.Text; MarkDirty(); }
                }
            }
            else if (_activeTab == 3)
            {
                if (!string.IsNullOrEmpty(_relSelectedNpc))
                {
                    string heading = _relHeadingBox.Text ?? string.Empty;
                    string desc = _relDescBox.Text ?? string.Empty;

                    if (_bio.Relationships.TryGetValue(_relSelectedNpc, out var rel) && rel != null)
                    {
                        if (rel.Heading != heading) { rel.Heading = heading; MarkDirty(); }
                        if (rel.Description != desc) { rel.Description = desc; MarkDirty(); }
                    }
                    else if (!string.IsNullOrEmpty(heading) || !string.IsNullOrEmpty(desc))
                    {
                        var entry = EnsureRelationshipEntry(_relSelectedNpc);
                        entry.Heading = heading;
                        entry.Description = desc;
                        MarkDirty();
                    }
                }
            }
            else if (_activeTab == 4)
            {
                _voiceBox.Update(time);
                _habitsBox.Update(time);
                _lensesBox.Update(time);

                string v = _voiceBox.Text ?? string.Empty;
                string h = _habitsBox.Text ?? string.Empty;
                string l = _lensesBox.Text ?? string.Empty;

                bool barkChanged = false;
                if (_bio.AmbientBarkPrompt != null)
                {
                    if (_bio.AmbientBarkPrompt.VoiceAndAttitude != v) { _bio.AmbientBarkPrompt.VoiceAndAttitude = v; barkChanged = true; }
                    if (_bio.AmbientBarkPrompt.SpokenHabits != h) { _bio.AmbientBarkPrompt.SpokenHabits = h; barkChanged = true; }
                    if (_bio.AmbientBarkPrompt.ObservationLenses != l) { _bio.AmbientBarkPrompt.ObservationLenses = l; barkChanged = true; }
                }
                else if (!string.IsNullOrEmpty(v) || !string.IsNullOrEmpty(h) || !string.IsNullOrEmpty(l))
                {
                    var p = EnsureAmbientBarkPrompt();
                    p.VoiceAndAttitude = v;
                    p.SpokenHabits = h;
                    p.ObservationLenses = l;
                    barkChanged = true;
                }
                if (barkChanged) MarkDirty();

                if (_enableBarkCheckbox.isChecked != _bio.EnableAmbientBarks)
                {
                    _bio.EnableAmbientBarks = _enableBarkCheckbox.isChecked;
                    MarkDirty();
                }
            }
        }

        // ── 交互输入分发 ──────────────────────────────────────────────────
        public override void receiveLeftClick(int x, int y, bool playSound = true)
        {
            if (_closeButton.containsPoint(x, y))
            {
                Game1.playSound("bigDeSelect");
                TryCancel();
                return;
            }

            // Tab 切换
            for (int i = 0; i < _tabRects.Length; i++)
            {
                if (_tabRects[i].Contains(x, y))
                {
                    SwitchTab(i);
                    return;
                }
            }

            // 底部操作
            if (_saveRect.Contains(x, y)) { SaveAndClose(); return; }
            if (_cancelRect.Contains(x, y)) { TryCancel(); return; }
            if (_resetRect.Contains(x, y)) { TryReset(); return; }

            // 各 Tab 分发
            if (_activeTab == 0) HandleTab1Click(x, y);
            else if (_activeTab == 1) HandleTab2Click(x, y);
            else if (_activeTab == 2) HandleTab3Click(x, y);
            else if (_activeTab == 3) HandleTab4Click(x, y);
            else if (_activeTab == 4) HandleTab5Click(x, y);
        }

        private void HandleTab1Click(int x, int y)
        {
            if (_biographyBox.Bounds.Contains(x, y)) { FocusBox(_biographyBox); return; }
            if (_scaffoldBtnRect.Contains(x, y)) { InsertScaffold(); return; }
            if (new Rectangle(_uniqueBox.X, _uniqueBox.Y, _uniqueBox.Width, _uniqueBox.Height).Contains(x, y)) { FocusTextBox(_uniqueBox); return; }
            if (_homeBedCheckbox.bounds.Contains(x, y)) { _homeBedCheckbox.receiveLeftClick(x, y); return; }
            UnfocusAll();
        }

        private void HandleTab2Click(int x, int y)
        {
            if (_behaviorBox.Bounds.Contains(x, y)) { FocusBox(_behaviorBox); return; }
            if (_dialogueExamplesBox.Bounds.Contains(x, y)) { FocusBox(_dialogueExamplesBox); return; }

            if (_behaviorScaffoldRect.Contains(x, y))
            {
                _behaviorBox.Text =
                    "[VOICE]\n- Tone: \n- Cadence: \n\n[SPEECH PATTERNS]\n- \n\n[MANNERISMS]\n- \n\n" +
                    "[IMMEDIATE REFLEXES]\n- \n\n[CONTEXT OVERRIDE]\n- ";
                MarkDirty();
                Game1.playSound("coin");
                return;
            }

            // 快捷插入对白分段符与选项
            if (_insertBreakRect.Contains(x, y))
            {
                _dialogueExamplesBox.Text = (_dialogueExamplesBox.Text ?? "") + "#$b#";
                MarkDirty();
                Game1.playSound("shiny4");
                return;
            }
            if (_insertChoiceRect.Contains(x, y))
            {
                _dialogueExamplesBox.Text = (_dialogueExamplesBox.Text ?? "").TrimEnd() + "\n% 选项文本内容";
                MarkDirty();
                Game1.playSound("shiny4");
                return;
            }

            UnfocusAll();
        }

        private void HandleTab3Click(int x, int y)
        {
            int visibleStages = Math.Min(_bio.ProgressStates.Count, 8);
            for (int i = 0; i < visibleStages; i++)
            {
                if (_stageRowRects[i].Contains(x, y))
                {
                    SelectStage(i);
                    Game1.playSound("smallSelect");
                    return;
                }
            }

            if (_bio.ProgressStates.Count < 8 && _newStageRect.Contains(x, y))
            {
                _bio.ProgressStates.Add(new BioData.ProgressStateEntry { RequiredHearts = 0 });
                SelectStage(_bio.ProgressStates.Count - 1);
                MarkDirty();
                Game1.playSound("newRecipe");
                Layout();
                return;
            }

            if (_stageIdx < 0 || _stageIdx >= _bio.ProgressStates.Count) return;
            var currentStage = _bio.ProgressStates[_stageIdx];

            // 门禁控件
            if (_heartsStepper.ReceiveLeftClick(x, y)) return;

            if (_gateMarriedPillRect.Contains(x, y))
            {
                currentStage.RequireMarried = !currentStage.RequireMarried;
                MarkDirty();
                Game1.playSound("drumkit6");
                return;
            }
            if (_gateJojaClosedPillRect.Contains(x, y))
            {
                currentStage.RequireJojaMartClosed = currentStage.RequireJojaMartClosed.HasValue
                    ? (currentStage.RequireJojaMartClosed.Value ? false : (bool?)null)
                    : true;
                MarkDirty();
                Game1.playSound("drumkit6");
                return;
            }
            if (_gateJojaMemberPillRect.Contains(x, y))
            {
                currentStage.RequireJojaMember = currentStage.RequireJojaMember.HasValue
                    ? (currentStage.RequireJojaMember.Value ? false : (bool?)null)
                    : true;
                MarkDirty();
                Game1.playSound("drumkit6");
                return;
            }

            if (_deleteStageRect.Contains(x, y))
            {
                Game1.activeClickableMenu = new ConfirmationDialog(
                    $"确定删除好感档位 {_stageIdx + 1}？",
                    _ =>
                    {
                        Game1.activeClickableMenu = this;
                        _bio.ProgressStates.RemoveAt(_stageIdx);
                        _stageIdx = Math.Min(_stageIdx, _bio.ProgressStates.Count - 1);
                        if (_stageIdx >= 0) SelectStage(_stageIdx);
                        MarkDirty();
                        Layout();
                    },
                    _ => Game1.activeClickableMenu = this);
                return;
            }

            if (_stageTagEditor.ReceiveLeftClick(x, y)) return;
            if (_stageTextBox.Bounds.Contains(x, y)) { FocusBox(_stageTextBox); return; }
            if (_stageBarkBox.Bounds.Contains(x, y)) { FocusBox(_stageBarkBox); return; }

            UnfocusAll();
        }

        private void HandleTab4Click(int x, int y)
        {
            if (new Rectangle(_relSearchBox.X, _relSearchBox.Y, _relSearchBox.Width, _relSearchBox.Height).Contains(x, y))
            {
                FocusTextBox(_relSearchBox);
                return;
            }

            foreach (var (rect, idx) in _relVisibleItemRects)
            {
                if (rect.Contains(x, y))
                {
                    SelectRelationship(idx);
                    Game1.playSound("smallSelect");
                    return;
                }
            }

            if (new Rectangle(_relHeadingBox.X, _relHeadingBox.Y, _relHeadingBox.Width, _relHeadingBox.Height).Contains(x, y))
            {
                FocusTextBox(_relHeadingBox);
                return;
            }
            if (_relDescBox.Bounds.Contains(x, y)) { FocusBox(_relDescBox); return; }

            if (_relAddRect.Contains(x, y) && !string.IsNullOrEmpty(_relSelectedNpc))
            {
                EnsureRelationshipEntry(_relSelectedNpc);
                MarkDirty();
                FilterNpcList();
                Game1.playSound("coin");
                return;
            }
            if (_relDelRect.Contains(x, y) && !string.IsNullOrEmpty(_relSelectedNpc))
            {
                string target = _relSelectedNpc;
                Game1.activeClickableMenu = new ConfirmationDialog(
                    $"删除 {_npcName} → {target} 的独立人设关系？",
                    _ =>
                    {
                        Game1.activeClickableMenu = this;
                        _bio.Relationships.Remove(target);
                        MarkDirty();
                        FilterNpcList();
                        SelectRelationship(_relSelectedIndex);
                    },
                    _ => Game1.activeClickableMenu = this);
                return;
            }

            UnfocusAll();
        }

        private void HandleTab5Click(int x, int y)
        {
            if (_enableBarkCheckbox.bounds.Contains(x, y)) { _enableBarkCheckbox.receiveLeftClick(x, y); return; }
            if (_scrapeRect.Contains(x, y)) { ScrapeExamples(); return; }
            if (_globalTagEditor.ReceiveLeftClick(x, y)) return;
            if (_voiceBox.Bounds.Contains(x, y)) { FocusBox(_voiceBox); return; }
            if (_habitsBox.Bounds.Contains(x, y)) { FocusBox(_habitsBox); return; }
            if (_lensesBox.Bounds.Contains(x, y)) { FocusBox(_lensesBox); return; }

            UnfocusAll();
        }

        public override void receiveKeyPress(Keys key)
        {
            // 支持 Ctrl + S 快速保存
            if (key == Keys.S && (Keyboard.GetState().IsKeyDown(Keys.LeftControl) || Keyboard.GetState().IsKeyDown(Keys.RightControl)))
            {
                SaveAndClose();
                return;
            }

            if (key == Keys.Escape)
            {
                TryCancel();
                return;
            }

            // 搜索框输入联动
            if (_activeTab == 3 && _relSearchBox.Selected)
            {
                base.receiveKeyPress(key);
                FilterNpcList();
                return;
            }

            base.receiveKeyPress(key);
        }

        public override void receiveScrollWheelAction(int direction)
        {
            int mx = Game1.getMouseX(), my = Game1.getMouseY();

            if (_activeTab == 0 && _biographyBox.Bounds.Contains(mx, my)) { _biographyBox.Scroll(direction); return; }
            if (_activeTab == 1)
            {
                if (_behaviorBox.Bounds.Contains(mx, my)) { _behaviorBox.Scroll(direction); return; }
                if (_dialogueExamplesBox.Bounds.Contains(mx, my)) { _dialogueExamplesBox.Scroll(direction); return; }
            }
            if (_activeTab == 2)
            {
                if (_stageTextBox.Bounds.Contains(mx, my)) { _stageTextBox.Scroll(direction); return; }
                if (_stageBarkBox.Bounds.Contains(mx, my)) { _stageBarkBox.Scroll(direction); return; }
            }
            if (_activeTab == 3)
            {
                if (_relLeftColRect.Contains(mx, my))
                {
                    _relListScrollOffset = Math.Clamp(_relListScrollOffset - (direction > 0 ? 1 : -1), 0, Math.Max(0, _filteredNpcs.Count - 6));
                    RecalculateTab4List();
                    return;
                }
                if (_relDescBox.Bounds.Contains(mx, my)) { _relDescBox.Scroll(direction); return; }
            }
            if (_activeTab == 4)
            {
                if (_voiceBox.Bounds.Contains(mx, my)) { _voiceBox.Scroll(direction); return; }
                if (_habitsBox.Bounds.Contains(mx, my)) { _habitsBox.Scroll(direction); return; }
                if (_lensesBox.Bounds.Contains(mx, my)) { _lensesBox.Scroll(direction); return; }
            }
        }

        public override void gameWindowSizeChanged(Rectangle oldBounds, Rectangle newBounds)
        {
            width = Math.Clamp(Game1.uiViewport.Width - 100, 920, 1160);
            height = Math.Clamp(Game1.uiViewport.Height - 80, 600, 750);
            xPositionOnScreen = (Game1.uiViewport.Width - width) / 2;
            yPositionOnScreen = (Game1.uiViewport.Height - height) / 2;
            Layout();
        }

        // ── 渲染管线 ──────────────────────────────────────────────────────
        public override void draw(SpriteBatch b)
        {
            int mx = Game1.getMouseX();
            int my = Game1.getMouseY();

            b.Draw(Game1.fadeToBlackRect, Game1.graphics.GraphicsDevice.Viewport.Bounds, Color.Black * 0.5f);

            // 1. 唯一外层经典木框
            IClickableMenu.drawTextureBox(b, xPositionOnScreen - 8, yPositionOnScreen - 8, width + 16, height + 16, Color.White);

            // 2. 内部羊皮纸平铺底板（纯底色，不带任何多余的粗边框和重叠饰角）
            b.Draw(
                Game1.menuTexture,
                new Rectangle(xPositionOnScreen, yPositionOnScreen, width, height),
                new Rectangle(64, 128, 64, 64),
                new Color(245, 230, 205)
            );

            DrawHeader(b); 

            for (int i = 0; i < _tabRects.Length; i++)
                DrawTabButton(b, _tabRects[i], TabTitles[i], _activeTab == i, mx, my);

            int sepY = yPositionOnScreen + HeaderH + TabBarH + 6;
            b.Draw(Game1.staminaRect, new Rectangle(xPositionOnScreen + ContentPadding, sepY, width - ContentPadding * 2, 2), Color.Gray * 0.35f);

            if (_activeTab == 0) DrawTab1(b, mx, my);
            else if (_activeTab == 1) DrawTab2(b, mx, my);
            else if (_activeTab == 2) DrawTab3(b, mx, my);
            else if (_activeTab == 3) DrawTab4(b, mx, my);
            else if (_activeTab == 4) DrawTab5(b, mx, my);

            DrawActionButton(b, _cancelRect, "返回 / 取消 (Esc)", mx, my, isDanger: false);
            DrawActionButton(b, _resetRect, "恢复原版基准", mx, my, isDanger: true, isEnabled: _hasOverlay);
            DrawActionButton(b, _saveRect, "✔ 保存修改 (Ctrl+S)", mx, my, isPrimary: true);

            _closeButton.draw(b);

            // FONT-03: 悬停提示绘制在最顶层（矢量新字体 + 原版九宫格底框 + 贴边 clamping）
            if (!string.IsNullOrEmpty(_hoverText))
                DrawHoverTextCustom(b, _hoverText);

            drawMouse(b);
        }

        /// <summary>
        /// 悬浮提示的自定义矢量渲染：保留原版鼠标跟随位置与贴边 clamping 行为
        /// （避免溢出屏幕），仅将文字替换为 CustomFontManager。
        /// </summary>
        private static void DrawHoverTextCustom(SpriteBatch b, string text)
        {
            var sz = CustomFontManager.MeasureString(text, 17f);
            int boxW = (int)sz.X + 24;
            int boxH = (int)sz.Y + 24;
            int x = Game1.getOldMouseX() + 32;
            int y = Game1.getOldMouseY() + 32;
            var safe = Utility.getSafeArea();
            if (x + boxW > safe.Right)
                x = safe.Right - boxW;
            if (y + boxH > safe.Bottom)
            {
                x += 16;
                if (x + boxW > safe.Right)
                    x = safe.Right - boxW;
                y = safe.Bottom - boxH;
            }
            if (x < safe.Left)
                x = safe.Left;
            if (y < safe.Top)
                y = safe.Top;
            IClickableMenu.drawTextureBox(b, Game1.menuTexture, new Rectangle(0, 256, 60, 60),
                x, y, boxW, boxH, Color.White, 1f, false);
            CustomFontManager.DrawString(b, text, new Vector2(x + 12, y + 12), Game1.textColor, 17f);
        }

        private void DrawHeader(SpriteBatch b)
        {
            int headX = xPositionOnScreen + ContentPadding;
            int headY = yPositionOnScreen + 14;

            const int pSize = 44;
            var portraitRect = new Rectangle(headX, headY, pSize, pSize);
            IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(403, 383, 6, 6),
                portraitRect.X - 2, portraitRect.Y - 2, portraitRect.Width + 4, portraitRect.Height + 4,
                new Color(225, 210, 185), 2f, false);

            if (_npcPortrait != null && !_portraitSmileRect.IsEmpty)
                b.Draw(_npcPortrait, portraitRect, _portraitSmileRect, Color.White);
            else
            {
                // FONT-02: 头像回退首字（空串防护，避免 ArgumentOutOfRangeException）
                string avatarFallback = string.IsNullOrEmpty(_npcName) ? "?" : _npcName.Substring(0, 1);
                CustomFontManager.DrawString(b, avatarFallback, new Vector2(portraitRect.X + 14, portraitRect.Y + 4), Color.Gray, CustomFontManager.SizeTitle);
            }

            string disp = Game1.getCharacterFromName(_npcName)?.displayName ?? _npcName;
            // FONT-02: 主标题（原 dialogueFont → SizeTitle）
            CustomFontManager.DrawString(b, $"{disp} ({_npcName}) · 人设工作台", new Vector2(headX + pSize + 12, headY + 2), Game1.textColor, CustomFontManager.SizeTitle);

            string status = _dirty ? "● 存在未保存改动" : (_hasOverlay ? "★ 自定义覆盖生效中" : "默认人设基准");
            Color statusCol = _dirty ? new Color(220, 90, 20) : (_hasOverlay ? new Color(30, 140, 40) : Color.DimGray);
            // FONT-02: 状态行（原 smallFont → 18.5f）
            CustomFontManager.DrawString(b, status, new Vector2(headX + pSize + 14, headY + 28), statusCol, 18.5f);
        }

        // ── 各 Tab 具体渲染 ───────────────────────────────────────────────
        private void DrawTab1(SpriteBatch b, int mx, int my)
        {
            int left = xPositionOnScreen + ContentPadding;
            // FONT-02: 小节标题（原 smallFont → 18.5f）
            CustomFontManager.DrawString(b, "身份设定与心理矛盾（保留 [IDENTITY] 与 [PSYCHOLOGICAL CONFLICTS] 分节符）",
                new Vector2(left, _biographyBox.Bounds.Y - 22), Game1.textColor, 18.5f);

            DrawActionButton(b, _scaffoldBtnRect, "插入身份模板", mx, my, false);
            _biographyBox.Draw(b);

            // 移除了多余的 DrawCard(_uniqueCardRect)，DrawSingleLineBox 内部已自带白框
            // FONT-02: 小节标题（原 smallFont → 18.5f）
            CustomFontManager.DrawString(b, "特殊行为/身份标记 (Unique)", new Vector2(_uniqueCardRect.X, _uniqueCardRect.Y + 6), Game1.textColor, 18.5f);
            DrawSingleLineBox(b, _uniqueBox);
            if (_uniqueBox.X <= mx && mx <= _uniqueBox.X + _uniqueBox.Width && _uniqueBox.Y <= my && my <= _uniqueBox.Y + _uniqueBox.Height)
                _hoverText = "用于限定 NPC 的特殊行为或状态（如 'behind the counter', 'holding a football'）。";

            // 就寝行为偏好
            // FONT-02: 小节标题（原 smallFont → 18.5f）
            CustomFontManager.DrawString(b, "就寝行为偏好", new Vector2(_homeBedCardRect.X, _homeBedCardRect.Y + 6), Game1.textColor, 18.5f);
            _homeBedCheckbox.draw(b, 0, 0, this);
            if (_homeBedCheckbox.bounds.Contains(mx, my))
                _hoverText = "勾选后，NPC 在深夜对话时会偏向使用专属卧房就寝语境。";
        }

        private void DrawTab2(SpriteBatch b, int mx, int my)
        {
            // 左列：标题与按钮垂直居中在 36px 区域内
            // FONT-02: 小节标题（原 smallFont → 18.5f）
            CustomFontManager.DrawString(b, "行为规则 (BehavioralRules)",
                new Vector2(_tab2LeftColRect.X, _tab2LeftColRect.Y + 6), Game1.textColor, 18.5f);
            DrawActionButton(b, _behaviorScaffoldRect, "插入规则模板", mx, my, false);
            _behaviorBox.Draw(b);

            // 右列：精简标题，避免与右侧小工具栏按钮撞车
            // FONT-02: 小节标题（原 smallFont → 18.5f）
            CustomFontManager.DrawString(b, "对白范例 (Dialogue)",
                new Vector2(_tab2RightColRect.X, _tab2RightColRect.Y + 6), Game1.textColor, 18.5f);
            DrawActionButton(b, _insertBreakRect, "+ 分段符", mx, my, false);
            DrawActionButton(b, _insertChoiceRect, "+ 玩家选项", mx, my, false);

            if (_insertBreakRect.Contains(mx, my)) _hoverText = "插入 #$b#：在原版对话框中翻页。";
            if (_insertChoiceRect.Contains(mx, my)) _hoverText = "插入 % 选项：提供玩家可点击的分支回答。";

            _dialogueExamplesBox.Draw(b);

            // 底部说明文字：位置固定在文本框下方 4 像素处，垂直居中在 28px 留白区域中
            // FONT-02: 底部灰色注释（原 smallFont + 0.88f scale → 15.5f，删除 scale 参数）
            CustomFontManager.DrawString(b, "提示：支持原版表情符 ($0 / $s) 与换行分段；选项以 % 开头。",
                new Vector2(_tab2RightColRect.X, _dialogueExamplesBox.Bounds.Bottom + 4), Color.DimGray, 15.5f);
        }

        private void DrawTab3(SpriteBatch b, int mx, int my)
        {
            // 左列：好感档位列表
            DrawCard(b, _stageLeftColRect);
            // FONT-02: 档位统计（原 smallFont → 18.5f）
            CustomFontManager.DrawString(b, $"好感演变档位 ({_bio.ProgressStates.Count}/8)",
                new Vector2(_stageLeftColRect.X + 10, _stageLeftColRect.Y + 8), Game1.textColor, 18.5f);

            int visibleStages = Math.Min(_bio.ProgressStates.Count, 8);
            for (int i = 0; i < visibleStages; i++)
            {
                var r = _stageRowRects[i];
                bool isSel = (i == _stageIdx);
                bool isHover = r.Contains(mx, my);

                Color bg = isSel ? new Color(215, 185, 140) : (isHover ? new Color(255, 235, 205) : Color.White);
                IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(432, 439, 9, 9), r.X, r.Y, r.Width, r.Height, bg, 2f, false);

                string gateSummary = BuildGateSummary(_bio.ProgressStates[i]);
                // FONT-02: 档位行（原 smallFont → 18.5f）
                CustomFontManager.DrawString(b, $"档位 {i + 1}  [{gateSummary}]", new Vector2(r.X + 12, r.Y + 8), isSel ? Game1.textColor : Color.Black, 18.5f);
            }

            if (_bio.ProgressStates.Count < 8)
                DrawActionButton(b, _newStageRect, "+ 新建好感档位", mx, my, false);

            if (_stageIdx < 0 || _stageIdx >= _bio.ProgressStates.Count)
            {
                // FONT-02: 空态提示（原 smallFont → 18.5f）
                CustomFontManager.DrawString(b, "从左侧列表选择或添加一个好感档位开始编辑。",
                    new Vector2(_stageRightColRect.X + 20, _stageRightColRect.Y + 40), Color.Gray, 18.5f);
                return;
            }

            var stage = _bio.ProgressStates[_stageIdx];

            // 门禁药丸控制条
            // FONT-02: 门禁标题（原 smallFont → 18.5f）
            CustomFontManager.DrawString(b, "激活门禁:", new Vector2(_stageRightColRect.X, _stageRightColRect.Y + 6), Game1.textColor, 18.5f);
            _heartsStepper.Draw(b);

            DrawPillButton(b, _gateMarriedPillRect, stage.RequireMarried ? "已婚" : "不限婚姻", stage.RequireMarried, mx, my);

            string jojaClosedText = stage.RequireJojaMartClosed.HasValue ? (stage.RequireJojaMartClosed.Value ? "超市:倒闭" : "超市:营业") : "超市:不限";
            DrawPillButton(b, _gateJojaClosedPillRect, jojaClosedText, stage.RequireJojaMartClosed.HasValue, mx, my);

            string jojaMemberText = stage.RequireJojaMember.HasValue ? (stage.RequireJojaMember.Value ? "会员:加入" : "会员:未入") : "会员:不限";
            DrawPillButton(b, _gateJojaMemberPillRect, jojaMemberText, stage.RequireJojaMember.HasValue, mx, my);

            DrawActionButton(b, _deleteStageRect, "删除此档", mx, my, isDanger: true);

            // 心智与碎碎念
            // 标题文字与输入框顶边留足 26px 距离，绝不贴边
            // FONT-02: 小节标题（原 smallFont → 18.5f）
            CustomFontManager.DrawString(b, "阶段态度演变 (Text)",
                new Vector2(_stageTextBox.Bounds.X, _stageTextBox.Bounds.Y - 26), Game1.textColor, 18.5f);
            _stageTextBox.Draw(b);

            // FONT-02: 小节标题（原 smallFont → 18.5f）
            CustomFontManager.DrawString(b, "碎碎念心智 (BarkMindset: 规定此时的心态与注意力)",
                new Vector2(_stageBarkBox.Bounds.X, _stageBarkBox.Bounds.Y - 26), Game1.textColor, 18.5f);
            _stageBarkBox.Draw(b);

            // FONT-02: 小节标题（原 smallFont → 18.5f）
            CustomFontManager.DrawString(b, "阶段专属关注池 (Preoccupations: 优先提及的事物)",
                new Vector2(_stageRightColRect.X, _stageBarkBox.Bounds.Bottom + 12), Game1.textColor, 18.5f);
            _stageTagEditor.Draw(b);
        }

        private void DrawTab4(SpriteBatch b, int mx, int my)
        {
            // 左列：NPC 检索列表
            DrawCard(b, _relLeftColRect);
            // FONT-02: 列表标题（原 smallFont → 18.5f）
            CustomFontManager.DrawString(b, "目标角色列表 (★已定制)", new Vector2(_relLeftColRect.X + 8, _relLeftColRect.Y + 6), Game1.textColor, 18.5f);
            DrawSingleLineBox(b, _relSearchBox);
            if (string.IsNullOrEmpty(_relSearchBox.Text))
                // FONT-02: 占位提示（原 smallFont → 18.5f）
                CustomFontManager.DrawString(b, "搜索角色...", new Vector2(_relSearchBox.X + 6, _relSearchBox.Y + 4), Color.Gray * 0.7f, 18.5f);

            foreach (var (r, idx) in _relVisibleItemRects)
            {
                var name = _filteredNpcs[idx];
                bool isSel = (idx == _relSelectedIndex);
                bool isHover = r.Contains(mx, my);
                bool hasConfig = _bio.Relationships.ContainsKey(name);

                Color bg = isSel ? new Color(215, 185, 140) : (isHover ? new Color(255, 235, 205) : Color.White);
                IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(432, 439, 9, 9), r.X, r.Y, r.Width, r.Height, bg, 2f, false);

                string disp = Game1.getCharacterFromName(name)?.displayName ?? name;
                if (hasConfig) disp += " ★";
                // FONT-02: 列表项（原 smallFont → 18.5f）
                CustomFontManager.DrawString(b, disp, new Vector2(r.X + 10, r.Y + 6), isSel ? Game1.textColor : (hasConfig ? new Color(160, 60, 0) : Color.Black), 18.5f);
            }

            DrawActionButton(b, _relAddRect, "+ 定制关系", mx, my, false);
            DrawActionButton(b, _relDelRect, "- 清除", mx, my, isDanger: true);

            // 右列：详情编辑
            if (string.IsNullOrEmpty(_relSelectedNpc))
            {
                // FONT-02: 空态提示（原 smallFont → 18.5f；合同清单未列此行，与 L1169 同构处理）
                CustomFontManager.DrawString(b, "从左侧列表选择目标角色。", new Vector2(_relRightColRect.X + 20, _relRightColRect.Y + 40), Color.Gray, 18.5f);
                return;
            }

            string targetDisp = Game1.getCharacterFromName(_relSelectedNpc)?.displayName ?? _relSelectedNpc;
            // FONT-02: 大标题（原 dialogueFont → SizeTitle）
            CustomFontManager.DrawString(b, $"{_npcName} 对 {targetDisp} 的单向社交关系",
                new Vector2(_relRightColRect.X, _relRightColRect.Y), Game1.textColor, CustomFontManager.SizeTitle);

            // 关系称谓标题：画在单行输入框上方 28px 处
            // FONT-02: 小节标题（原 smallFont → 18.5f）
            CustomFontManager.DrawString(b, "关系称谓与定位 (Heading: 如 'Wife', 'Business Rival')",
                new Vector2(_relHeadingBox.X, _relHeadingBox.Y - 28), Game1.textColor, 18.5f);
            DrawSingleLineBox(b, _relHeadingBox);

            // 详细描述标题：画在多行输入框上方 28px 处，彻底与下方的多行框边框脱离
            // FONT-02: 小节标题（原 smallFont → 18.5f）
            CustomFontManager.DrawString(b, "深层心理与互动细节 (Description)",
                new Vector2(_relDescBox.Bounds.X, _relDescBox.Bounds.Y - 28), Game1.textColor, 18.5f);
            _relDescBox.Draw(b);

            // FONT-02: 底部灰色注释（原 smallFont + 0.88f scale → 15.5f，删除 scale 参数）
            CustomFontManager.DrawString(b, "提示：如需双方互动感知，请在两人的编辑器中分别配置相互的关系定位。",
                new Vector2(_relRightColRect.X, _relDescBox.Bounds.Bottom + 6), Color.DimGray, 15.5f);
        }

        private void DrawTab5(SpriteBatch b, int mx, int my)
        {
            // 左列：碎碎念总控与全局关注池
            DrawCard(b, _tab5LeftColRect);
            // FONT-02: 小节标题（原 smallFont → 18.5f）
            CustomFontManager.DrawString(b, "日常碎碎念总控", new Vector2(_tab5LeftColRect.X + 10, _tab5LeftColRect.Y + 8), Game1.textColor, 18.5f);
            _enableBarkCheckbox.draw(b, 0, 0, this);

            DrawActionButton(b, _scrapeRect, "↺ 从原版对白智能抓取范例", mx, my, false);

            // FONT-02: 小节标题（原 smallFont → 18.5f）
            CustomFontManager.DrawString(b, "全局常态关注池 (Preoccupations)",
                new Vector2(_tab5LeftColRect.X + 4, _tab5LeftColRect.Y + 130), Game1.textColor, 18.5f);
            _globalTagEditor.Draw(b);

            // 右列：提示词三大核心组件
            // FONT-02: 小节标题（原 smallFont → 18.5f）
            CustomFontManager.DrawString(b, "口吻与态度 (Voice & Attitude)", new Vector2(_voiceBox.Bounds.X, _voiceBox.Bounds.Y - 20), Game1.textColor, 18.5f);
            _voiceBox.Draw(b);
            if (_voiceBox.Bounds.Contains(mx, my)) _hoverText = "限定碎碎念的基本语调、说话长短与即时情绪基调。";

            // FONT-02: 小节标题（原 smallFont → 18.5f）
            CustomFontManager.DrawString(b, "口头习惯 (Spoken Habits)", new Vector2(_habitsBox.Bounds.X, _habitsBox.Bounds.Y - 20), Game1.textColor, 18.5f);
            _habitsBox.Draw(b);
            if (_habitsBox.Bounds.Contains(mx, my)) _hoverText = "NPC 的口头禅、叹气声、常用起手式（如 'Well,', 'Sigh...'）。";

            // FONT-02: 小节标题（原 smallFont → 18.5f）
            CustomFontManager.DrawString(b, "观察透镜 (Observation Lenses)", new Vector2(_lensesBox.Bounds.X, _lensesBox.Bounds.Y - 20), Game1.textColor, 18.5f);
            _lensesBox.Draw(b);
            if (_lensesBox.Bounds.Contains(mx, my)) _hoverText = "NPC 打量周围世界时的特殊视角（例如铁匠关注矿物与工具锈蚀，农夫关注作物与雨水）。";
        }

        // ── 基础绘制与微型组件 ────────────────────────────────────────────
        private void DrawTabButton(SpriteBatch b, Rectangle rect, string label, bool isActive, int mx, int my)
        {
            bool isHover = rect.Contains(mx, my);
            Color bg = isActive ? new Color(215, 185, 140) : (isHover ? new Color(255, 235, 205) : new Color(145, 95, 45));

            IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(432, 439, 9, 9),
                rect.X, rect.Y, rect.Width, rect.Height, bg, 4f, false);

            // FONT-02: Tab 标签（原 smallFont → 18.5f；measure/draw 同点同字号）
            var sz = CustomFontManager.MeasureString(label, 18.5f);
            CustomFontManager.DrawString(b, label,
                new Vector2(rect.X + (rect.Width - sz.X) / 2f, rect.Y + (rect.Height - sz.Y) / 2f),
                isActive ? Game1.textColor : Color.White * 0.95f, 18.5f);
        }

        private void DrawActionButton(SpriteBatch b, Rectangle rect, string label, int mx, int my,
            bool isDanger = false, bool isPrimary = false, bool isEnabled = true)
        {
            bool isHover = isEnabled && rect.Contains(mx, my);
            Color bg;
            if (!isEnabled) bg = Color.LightGray * 0.6f;
            else if (isPrimary) bg = isHover ? Color.Gold : new Color(255, 220, 130);
            else if (isDanger) bg = isHover ? new Color(255, 115, 115) : new Color(245, 170, 170);
            else bg = isHover ? new Color(255, 235, 205) : new Color(215, 185, 140);

            IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(432, 439, 9, 9),
                rect.X, rect.Y, rect.Width, rect.Height, bg, 3f, false);

            // FONT-02: 功能按钮文本（原 smallFont → 18.5f；measure/draw 同点同字号）
            var sz = CustomFontManager.MeasureString(label, 18.5f);
            Color textCol = !isEnabled ? Color.Gray : (isHover ? Color.Black : Game1.textColor);
            CustomFontManager.DrawString(b, label,
                new Vector2(rect.X + (rect.Width - sz.X) / 2f, rect.Y + (rect.Height - sz.Y) / 2f), textCol, 18.5f);
        }

        private static void DrawPillButton(SpriteBatch b, Rectangle rect, string label, bool isActive, int mx, int my)
        {
            bool isHover = rect.Contains(mx, my);
            Color bg = isActive ? (isHover ? Color.Gold : new Color(255, 220, 130)) : (isHover ? new Color(255, 235, 205) : Color.White);

            IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(432, 439, 9, 9),
                rect.X, rect.Y, rect.Width, rect.Height, bg, 2f, false);

            // FONT-02: 胶囊按钮文本（原 smallFont → 18.5f；measure/draw 同点同字号）
            var sz = CustomFontManager.MeasureString(label, 18.5f);
            CustomFontManager.DrawString(b, label,
                new Vector2(rect.X + (rect.Width - sz.X) / 2f, rect.Y + (rect.Height - sz.Y) / 2f),
                isActive ? Game1.textColor : Color.DimGray, 18.5f);
        }

        private static void DrawCard(SpriteBatch b, Rectangle rect)
        {
            IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(403, 383, 6, 6),
                rect.X, rect.Y, rect.Width, rect.Height, new Color(230, 215, 190) * 0.6f, 2f, false);
        }

        private static void DrawSingleLineBox(SpriteBatch b, TextBox box)
        {
            var boxRect = new Rectangle(box.X, box.Y, box.Width, box.Height);

            // 1. 星露谷原版内凹羊皮纸槽纹理 (6x6 精细 9 宫格切片，边框极细且自带复古像素阴影)
            // 选中时微亮微黄，未选中时融入背景羊皮纸
            Color slotColor = box.Selected ? new Color(255, 248, 220) : new Color(228, 212, 184) * 0.9f;

            IClickableMenu.drawTextureBox(
                b,
                Game1.mouseCursors,
                new Rectangle(403, 383, 6, 6), // 专门用于窄卡片与微型槽的超细原版切片
                boxRect.X,
                boxRect.Y,
                boxRect.Width,
                boxRect.Height,
                slotColor,
                2f,   // 缩放控制在 2f，边缘只有极细的 2 像素阴影
                false
            );

            // 2. 聚焦时追加一层柔和的金色高亮细轮廓
            if (box.Selected)
            {
                IClickableMenu.drawTextureBox(
                    b,
                    Game1.mouseCursors,
                    new Rectangle(432, 439, 9, 9),
                    boxRect.X - 1,
                    boxRect.Y - 1,
                    boxRect.Width + 2,
                    boxRect.Height + 2,
                    Color.Gold * 0.45f,
                    2f,
                    false
                );
            }

            // 3. 文字垂直居中排布（避免被上下边框切到字）
            // FONT-02: 单行输入框文本（原 smallFont → 18.5f；measure/draw 同点同字号，光标随动新测量）
            string text = box.Text ?? string.Empty;
            Vector2 textSize = CustomFontManager.MeasureString(text, 18.5f);
            float textX = boxRect.X + 10;
            float textY = boxRect.Y + (boxRect.Height - textSize.Y) / 2f - 1;

            if (!string.IsNullOrEmpty(text))
            {
                CustomFontManager.DrawString(b, text, new Vector2(textX, textY), Game1.textColor, 18.5f);
            }

            // 4. 原生像素闪烁光标
            if (box.Selected)
            {
                float cx = textX + textSize.X + 1;
                int cursorH = (int)Math.Min(22, boxRect.Height - 10);
                float cursorY = boxRect.Y + (boxRect.Height - cursorH) / 2f;

                if ((int)(Game1.currentGameTime.TotalGameTime.TotalMilliseconds / 500) % 2 == 0)
                {
                    b.Draw(Game1.staminaRect, new Rectangle((int)cx, (int)cursorY, 2, cursorH), Game1.textColor);
                }
            }
        }

        // ── 业务回写辅助 ──────────────────────────────────────────────────
        private void FocusBox(MultilineTextBox box)
        {
            UnfocusAll();
            box.Selected = true;
            Game1.keyboardDispatcher.Subscriber = box;
        }

        private void FocusTextBox(TextBox box)
        {
            UnfocusAll();
            box.Selected = true;
            Game1.keyboardDispatcher.Subscriber = box;
        }

        private void UnfocusAll()
        {
            Game1.keyboardDispatcher.Subscriber = null;
            _biographyBox.Selected = false;
            _uniqueBox.Selected = false;
            _behaviorBox.Selected = false;
            _dialogueExamplesBox.Selected = false;
            _stageTextBox.Selected = false;
            _stageBarkBox.Selected = false;
            _relSearchBox.Selected = false;
            _relHeadingBox.Selected = false;
            _relDescBox.Selected = false;
            _voiceBox.Selected = false;
            _habitsBox.Selected = false;
            _lensesBox.Selected = false;
            _stageTagEditor.CommitInput();
            _globalTagEditor.CommitInput();
        }

        private void SwitchTab(int tab)
        {
            if (_activeTab == tab) return;
            UnfocusAll();
            _activeTab = tab;
            Game1.playSound("smallSelect");
            Layout();
        }

        private void MarkDirty() => _dirty = true;

        private void SelectStage(int idx)
        {
            if (idx < 0 || idx >= _bio.ProgressStates.Count) return;
            _stageIdx = idx;
            var s = _bio.ProgressStates[idx];
            _stageTextBox.Text = s.Text ?? string.Empty;
            _stageBarkBox.Text = s.BarkMindset ?? string.Empty;
            _stageTagEditor.SetTags(s.Preoccupations);
            _heartsStepper.Value = s.RequiredHearts;
        }

        private static string BuildGateSummary(BioData.ProgressStateEntry p)
        {
            var parts = new List<string>();
            if (p.RequiredHearts > 0) parts.Add($"≥{p.RequiredHearts}♥");
            if (p.RequireMarried) parts.Add("已婚");
            if (p.RequireJojaMartClosed == true) parts.Add("超市倒闭");
            if (p.RequireJojaMember == true) parts.Add("会员");
            return parts.Count > 0 ? string.Join("/", parts) : "无门禁";
        }

        private void SelectRelationship(int index)
        {
            if (_filteredNpcs.Count == 0) return;
            index = Math.Clamp(index, 0, _filteredNpcs.Count - 1);
            _relSelectedIndex = index;
            _relSelectedNpc = _filteredNpcs[index];

            if (_bio.Relationships.TryGetValue(_relSelectedNpc, out var r) && r != null)
            {
                _relHeadingBox.Text = r.Heading ?? string.Empty;
                _relDescBox.Text = r.Description ?? string.Empty;
            }
            else
            {
                _relHeadingBox.Text = string.Empty;
                _relDescBox.Text = string.Empty;
            }
        }

        private BioData.ListEntry EnsureRelationshipEntry(string npcName)
        {
            if (_bio.Relationships == null)
                _bio.Relationships = new Dictionary<string, BioData.ListEntry>();
            if (!_bio.Relationships.TryGetValue(npcName, out var entry) || entry == null)
            {
                entry = new BioData.ListEntry { id = npcName, Heading = string.Empty, Description = string.Empty, RequiredHearts = 0 };
                _bio.Relationships[npcName] = entry;
            }
            return entry;
        }

        private BioData.ListEntry EnsureTraitEntry(string key, string defaultHeading)
        {
            if (!_bio.Traits.TryGetValue(key, out var entry) || entry == null)
            {
                entry = new BioData.ListEntry { id = key, Heading = defaultHeading, Description = string.Empty, RequiredHearts = 0 };
                _bio.Traits[key] = entry;
            }
            return entry;
        }

        private AmbientBarkPrompt EnsureAmbientBarkPrompt()
        {
            _bio.AmbientBarkPrompt ??= new AmbientBarkPrompt();
            return _bio.AmbientBarkPrompt;
        }

        private void SyncTab5BarkBoxes()
        {
            var p = _bio.AmbientBarkPrompt;
            _voiceBox.Text = p?.VoiceAndAttitude ?? string.Empty;
            _habitsBox.Text = p?.SpokenHabits ?? string.Empty;
            _lensesBox.Text = p?.ObservationLenses ?? string.Empty;
        }

        private void ScrapeExamples()
        {
            try
            {
                var lines = DialogueScraper.FetchCleanDialogueExamples(_npcName, 3);
                if (lines == null || lines.Count == 0)
                {
                    Game1.addHUDMessage(new HUDMessage("未抓取到原版对白", HUDMessage.error_type));
                    return;
                }
                var entry = EnsureTraitEntry("DialogueExamples", "Dialogue Examples");
                var sb = new StringBuilder(entry.Description ?? "");
                int added = 0;
                foreach (var line in lines)
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    if (sb.ToString().Contains(line, StringComparison.OrdinalIgnoreCase)) continue;
                    if (sb.Length > 4000) break;
                    if (sb.Length > 0) sb.AppendLine();
                    sb.Append("- ").Append(line.Trim());
                    added++;
                }
                entry.Description = sb.ToString().TrimStart();
                if (added > 0) MarkDirty();
                Game1.playSound("newArtifact");
                Game1.addHUDMessage(new HUDMessage($"已成功抓取 {added} 条原版对白至言行模块", HUDMessage.newQuest_type));
            }
            catch (Exception ex)
            {
                ModEntry.SMonitor?.Log($"抓取对白失败: {ex.Message}", LogLevel.Warn);
                Game1.addHUDMessage(new HUDMessage("抓取对白失败", HUDMessage.error_type));
            }
        }

        private void InsertScaffold()
        {
            _biographyBox.Text = BiographyScaffold.Replace("{NPC}", _npcName);
            MarkDirty();
            Game1.playSound("coin");
        }

        private void SaveAndClose()
        {
            if (!ModEntry.BioStorage!.SaveOverlay(_npcName, _bio, out string err))
            {
                Game1.addHUDMessage(new HUDMessage($"保存失败: {err}", HUDMessage.error_type));
                return;
            }
            Game1.playSound("achievement");
            ExitAndReturn();
        }

        private void TryCancel()
        {
            if (!_dirty)
            {
                ExitAndReturn();
                return;
            }
            Game1.activeClickableMenu = new ConfirmationDialog(
                "放弃未保存的所有修改？",
                _ => { Game1.activeClickableMenu = this; ExitAndReturn(); },
                _ => { Game1.activeClickableMenu = this; });
        }

        private void TryReset()
        {
            if (!_hasOverlay)
            {
                Game1.playSound("cancel");
                return;
            }
            Game1.activeClickableMenu = new ConfirmationDialog(
                $"确定将 {_npcName} 还原为默认人设，并删除自定义覆盖？",
                _ =>
                {
                    Game1.activeClickableMenu = this;
                    if (!ModEntry.BioStorage!.ResetOverlay(_npcName, out string err))
                    {
                        Game1.addHUDMessage(new HUDMessage($"还原失败: {err}", HUDMessage.error_type));
                        return;
                    }
                    _bio = ModEntry.BioStorage!.LoadEditableBio(_npcName);
                    _dirty = false;
                    _hasOverlay = false;
                    _biographyBox.Text = _bio.Biography ?? string.Empty;
                    _uniqueBox.Text = _bio.Unique ?? string.Empty;
                    _homeBedCheckbox.isChecked = _bio.HomeLocationBed;
                    _globalTagEditor.SetTags(_bio.Preoccupations);
                    Game1.playSound("throw");
                },
                _ => Game1.activeClickableMenu = this);
        }

        private void ExitAndReturn()
        {
            if (_returnMenu != null) Game1.activeClickableMenu = _returnMenu;
            else Game1.exitActiveMenu();
        }

        protected override void cleanupBeforeExit()
        {
            base.cleanupBeforeExit();
            UnfocusAll();
        }

        private static Texture2D LoadTextBoxTexture()
        {
            try { return Game1.content.Load<Texture2D>("LooseSprites\\textBox") ?? Game1.mouseCursors; }
            catch { return Game1.mouseCursors; }
        }
    }
}
