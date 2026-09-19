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
    /// 简单的复选框辅助类
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
            Rectangle src = isChecked ? new Rectangle(236, 425, 9, 9) : new Rectangle(227, 425, 9, 9);
            b.Draw(Game1.mouseCursors, new Vector2(bounds.X, bounds.Y), src, Color.White, 0f, Vector2.Zero, 3.5f, SpriteEffects.None, 1f);

            if (!string.IsNullOrEmpty(label))
                CustomFontManager.DrawString(b, label, new Vector2(bounds.X + 36, bounds.Y + 6), Game1.textColor, 18.5f);
        }
    }

    /// <summary>
    /// 角色人设沉浸式编辑器（高品质排版重构版）：
    /// 引入 DialogueTextInputBox 自动换行引擎与 CustomFontManager 舒适字号，彻底告别贴边与字小。
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
        private DialogueTextInputBox _biographyBox;
        private TextBox _uniqueBox;
        private SimpleCheckbox _homeBedCheckbox;
        private Rectangle _scaffoldBtnRect;
        private Rectangle _uniqueCardRect;
        private Rectangle _homeBedCardRect;

        private static readonly string BiographyScaffold =
            "[IDENTITY]\n- Identity: You are {NPC}.\n- Social Anchor: \n- Living Situation: \n\n" +
            "[PSYCHOLOGICAL CONFLICTS]\n- ";

        // ── Tab 2 控件（言行举止） ────────────────────────────────────────
        private DialogueTextInputBox _behaviorBox;
        private DialogueTextInputBox _dialogueExamplesBox;
        private Rectangle _behaviorScaffoldRect;
        private Rectangle _insertBreakRect;
        private Rectangle _insertChoiceRect;
        private Rectangle _tab2LeftColRect;
        private Rectangle _tab2RightColRect;

        // ── Tab 3 控件（好感演变） ────────────────────────────────────────
        private int _stageIdx = -1;
        private DialogueTextInputBox _stageTextBox;
        private DialogueTextInputBox _stageBarkBox;
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
        private DialogueTextInputBox _relDescBox;

        private Rectangle _relLeftColRect;
        private Rectangle _relRightColRect;
        private Rectangle _relAddRect;
        private Rectangle _relDelRect;
        private readonly List<(Rectangle Rect, int Index)> _relVisibleItemRects = new();

        // ── Tab 5 控件（环境感知） ────────────────────────────────────────
        private SimpleCheckbox _enableBarkCheckbox;
        private TagListEditor _globalTagEditor;
        private DialogueTextInputBox _voiceBox;
        private DialogueTextInputBox _habitsBox;
        private DialogueTextInputBox _lensesBox;
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
            // 声明本菜单是全屏独占的模态窗口
            this.allClickableComponents ??= new List<ClickableComponent>();

            _npcName = npcName;
            _returnMenu = returnMenu;

            _bio = ModEntry.BioStorage!.LoadEditableBio(npcName);
            _hasOverlay = ModEntry.BioStorage.HasCustomOverlay(npcName);
            _activeTab = 0;

            LoadNpcPortrait();

            // 升级为自带自动换行与字符测宽的 DialogueTextInputBox
            _biographyBox = CreateTextInputBox(4000);
            _behaviorBox = CreateTextInputBox(4000);
            _dialogueExamplesBox = CreateTextInputBox(4000);
            _stageTextBox = CreateTextInputBox(2000);
            _stageBarkBox = CreateTextInputBox(2000);
            _relDescBox = CreateTextInputBox(2000);
            _voiceBox = CreateTextInputBox(2000);
            _habitsBox = CreateTextInputBox(2000);
            _lensesBox = CreateTextInputBox(2000);

            Texture2D boxTex = LoadTextBoxTexture();
            _uniqueBox = new TextBox(boxTex, null, Game1.smallFont, Game1.textColor);
            _relSearchBox = new TextBox(boxTex, null, Game1.smallFont, Game1.textColor);
            _relHeadingBox = new TextBox(boxTex, null, Game1.smallFont, Game1.textColor);

            _homeBedCheckbox = new SimpleCheckbox("床位固定 (HomeLocationBed)", -1, 0, 0);
            _enableBarkCheckbox = new SimpleCheckbox("启用日常碎碎念 (AmbientBarks)", -1, 0, 0);

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
            _biographyBox.SetText(_bio.Biography ?? string.Empty);
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

        private static DialogueTextInputBox CreateTextInputBox(int maxChars)
        {
            return new DialogueTextInputBox(maxChars)
            {
                Font = Game1.smallFont,
                TextColor = Game1.textColor,
                Selected = false
            };
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

        // ── 界面几何布局重构（自上而下严格流式排版，根治错位） ─────────────────
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
                int cardH = 68;
                int topLabelH = 26;
                _scaffoldBtnRect = new Rectangle(contentLeft + contentW - 140, bodyTop, 140, 26);

                // 主文本框自适应占据上方空间
                int bioH = bodyH - cardH - topLabelH - 24;
                SetBoxBounds(_biographyBox, contentLeft, bodyTop + topLabelH + 4, contentW, bioH);

                // 下方两张卡片流式放置，X 轴坐标严格对齐
                int cardY = (int)_biographyBox.Position.Y + (int)_biographyBox.Extent.Y + 16;
                int cardW = (contentW - 16) / 2;
                _uniqueCardRect = new Rectangle(contentLeft, cardY, cardW, cardH);
                _homeBedCardRect = new Rectangle(contentLeft + cardW + 16, cardY, cardW, cardH);

                // 单行输入框与卡片内边距统一样式（居中靠左 12px）
                _uniqueBox.X = _uniqueCardRect.X + 12;
                _uniqueBox.Y = _uniqueCardRect.Y + 28;
                _uniqueBox.Width = _uniqueCardRect.Width - 24;
                _uniqueBox.Height = 30;

                _homeBedCheckbox.bounds = new Rectangle(_homeBedCardRect.X + 16, _homeBedCardRect.Y + 26, 28, 28);
            }

            // ── Tab 2 布局（言行举止） ──
            if (_activeTab == 1)
            {
                int colW = (contentW - 16) / 2;
                _tab2LeftColRect = new Rectangle(contentLeft, bodyTop, colW, bodyH);
                _tab2RightColRect = new Rectangle(contentLeft + colW + 16, bodyTop, colW, bodyH);

                int topBarH = 32;
                int bottomTipH = 24;
                int boxH = bodyH - topBarH - bottomTipH - 12;

                _behaviorScaffoldRect = new Rectangle(_tab2LeftColRect.Right - 130, bodyTop + 2, 130, 26);
                SetBoxBounds(_behaviorBox, _tab2LeftColRect.X, bodyTop + topBarH, colW, boxH);

                int toolBtnW = 95;
                _insertChoiceRect = new Rectangle(_tab2RightColRect.Right - toolBtnW, bodyTop + 2, toolBtnW, 26);
                _insertBreakRect = new Rectangle(_insertChoiceRect.Left - toolBtnW - 6, bodyTop + 2, toolBtnW, 26);
                SetBoxBounds(_dialogueExamplesBox, _tab2RightColRect.X, bodyTop + topBarH, colW, boxH);
            }

            // ── Tab 3 布局（好感演变） ──
            if (_activeTab == 2)
            {
                int leftColW = 240;
                int rightColW = contentW - leftColW - 16;
                _stageLeftColRect = new Rectangle(contentLeft, bodyTop, leftColW, bodyH);
                _stageRightColRect = new Rectangle(contentLeft + leftColW + 16, bodyTop, rightColW, bodyH);

                int rowH = 38;
                for (int i = 0; i < _stageRowRects.Length; i++)
                    _stageRowRects[i] = new Rectangle(_stageLeftColRect.X, _stageLeftColRect.Y + 34 + i * (rowH + 4), leftColW, rowH);

                int nextY = _stageLeftColRect.Y + 34 + Math.Min(_bio.ProgressStates.Count, 8) * (rowH + 4);
                _newStageRect = new Rectangle(_stageLeftColRect.X, nextY, leftColW, 34);

                int rightX = _stageRightColRect.X;
                int rightY = _stageRightColRect.Y;

                // 门禁药丸栏
                _heartsStepper.SetBounds(new Rectangle(rightX + 85, rightY, 110, 28));
                _gateMarriedPillRect = new Rectangle(rightX + 203, rightY, 95, 28);
                _gateJojaClosedPillRect = new Rectangle(rightX + 306, rightY, 105, 28);
                _gateJojaMemberPillRect = new Rectangle(rightX + 419, rightY, 105, 28);
                _deleteStageRect = new Rectangle(rightX + rightColW - 95, rightY, 95, 28);

                // 动态流式拆分两个多行框与底部关注池
                int flowY = rightY + 38;
                int tagEditorH = 56;
                int labelH = 22;
                int availTextH = (bodyBottom - flowY) - tagEditorH - (labelH * 3) - 30;
                int singleBoxH = Math.Max(70, availTextH / 2);

                // 阶段态度
                SetBoxBounds(_stageTextBox, rightX, flowY + labelH, rightColW, singleBoxH);
                flowY = (int)_stageTextBox.Position.Y + (int)_stageTextBox.Extent.Y + 14;

                // 碎碎念心智
                SetBoxBounds(_stageBarkBox, rightX, flowY + labelH, rightColW, singleBoxH);
                flowY = (int)_stageBarkBox.Position.Y + (int)_stageBarkBox.Extent.Y + 14;

                // 关注池
                _stageTagEditor.SetBounds(new Rectangle(rightX, flowY + labelH, rightColW, tagEditorH));
            }

            // ── Tab 4 布局（社交关系） ──
            if (_activeTab == 3)
            {
                int leftColW = 250;
                int rightColW = contentW - leftColW - 16;
                _relLeftColRect = new Rectangle(contentLeft, bodyTop, leftColW, bodyH);
                _relRightColRect = new Rectangle(contentLeft + leftColW + 16, bodyTop, rightColW, bodyH);

                // 搜索栏流式对齐
                _relSearchBox.X = _relLeftColRect.X + 8;
                _relSearchBox.Y = _relLeftColRect.Y + 32;
                _relSearchBox.Width = _relLeftColRect.Width - 16;
                _relSearchBox.Height = 30;

                int relBtnW = (leftColW - 8) / 2;
                _relAddRect = new Rectangle(_relLeftColRect.X, _relLeftColRect.Bottom - 34, relBtnW, 34);
                _relDelRect = new Rectangle(_relLeftColRect.X + relBtnW + 8, _relLeftColRect.Bottom - 34, relBtnW, 34);

                int rightX = _relRightColRect.X;
                int flowY = bodyTop;
                int labelH = 22;

                // 顶层大标题预留 36px
                flowY += 36;

                // 称谓单行框
                _relHeadingBox.X = rightX;
                _relHeadingBox.Y = flowY + labelH;
                _relHeadingBox.Width = rightColW;
                _relHeadingBox.Height = 32;
                flowY = _relHeadingBox.Y + _relHeadingBox.Height + 16;

                // 详细描述框自适应铺满剩余垂直空间
                int bottomTipH = 26;
                int descH = bodyBottom - flowY - labelH - bottomTipH - 6;
                SetBoxBounds(_relDescBox, rightX, flowY + labelH, rightColW, Math.Max(90, descH));

                RecalculateTab4List();
            }

            // ── Tab 5 布局（环境感知） ──
            if (_activeTab == 4)
            {
                int leftColW = 340;
                int rightColW = contentW - leftColW - 16;
                _tab5LeftColRect = new Rectangle(contentLeft, bodyTop, leftColW, bodyH);
                _tab5RightColRect = new Rectangle(contentLeft + leftColW + 16, bodyTop, rightColW, bodyH);

                _enableBarkCheckbox.bounds = new Rectangle(_tab5LeftColRect.X + 12, _tab5LeftColRect.Y + 32, 28, 28);
                _scrapeRect = new Rectangle(_tab5LeftColRect.X + 8, _tab5LeftColRect.Y + 76, leftColW - 16, 34);

                int tagEditorTop = _tab5LeftColRect.Y + 152;
                _globalTagEditor.SetBounds(new Rectangle(_tab5LeftColRect.X + 8, tagEditorTop, leftColW - 16, bodyBottom - tagEditorTop - 8));

                // 右列三大卡片：自适应三等分，每段内部 Label 与 Box 紧密贴合
                int rightX = _tab5RightColRect.X;
                int rightY = _tab5RightColRect.Y;
                int sectionH = (bodyH - 16) / 3;
                int labelH = 22;

                for (int i = 0; i < 3; i++)
                {
                    int blockTop = rightY + i * sectionH;
                    int boxY = blockTop + labelH;
                    int boxH = sectionH - labelH - 12;

                    if (i == 0) SetBoxBounds(_voiceBox, rightX, boxY, rightColW, boxH);
                    else if (i == 1) SetBoxBounds(_habitsBox, rightX, boxY, rightColW, boxH);
                    else SetBoxBounds(_lensesBox, rightX, boxY, rightColW, boxH);
                }
            }
        }

        private static void SetBoxBounds(DialogueTextInputBox box, float x, float y, float w, float h)
        {
            box.Position = new Vector2(x, y);
            box.Extent = new Vector2(w, h);
            box.InvalidateLayout();
        }

        // ── 社交关系列表与精准过滤搜索 ────────────────────────────────────────
        private void InitNpcList()
        {
            _allNpcs.Clear();
            var friendshipData = Game1.player?.friendshipData;

            var excludedNpcs = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "Grandpa", "Governor", "Gil", "Bouncer", "Birdie", "Henchman", "MarlonFudge"
            };

            bool IsInvalidOrEventNpc(string internalName, NPC npc)
            {
                if (string.IsNullOrWhiteSpace(internalName)) return true;
                if (string.Equals(internalName, _npcName, StringComparison.OrdinalIgnoreCase)) return true;
                if (excludedNpcs.Contains(internalName)) return true;

                if (internalName.Contains("_") ||
                    internalName.Contains("Event", StringComparison.OrdinalIgnoreCase) ||
                    internalName.Contains("Fake", StringComparison.OrdinalIgnoreCase) ||
                    internalName.Contains("Dummy", StringComparison.OrdinalIgnoreCase))
                    return true;

                bool isTrueMarlon = internalName.Equals("Marlon", StringComparison.OrdinalIgnoreCase);
                if (internalName.StartsWith("Marlon", StringComparison.OrdinalIgnoreCase) && !isTrueMarlon)
                    return true;

                bool inFriendship = friendshipData != null && friendshipData.ContainsKey(internalName);

                if (npc != null)
                {
                    if (Game1.CurrentEvent != null && Game1.CurrentEvent.actors != null && Game1.CurrentEvent.actors.Contains(npc))
                        return true;
                    if (!isTrueMarlon && !npc.CanSocialize && !inFriendship)
                        return true;
                }
                else
                {
                    if (!isTrueMarlon && !inFriendship)
                        return true;
                }

                return false;
            }

            var rawCandidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (friendshipData != null)
            {
                foreach (var k in friendshipData.Keys)
                {
                    if (!IsInvalidOrEventNpc(k, Game1.getCharacterFromName(k)))
                        rawCandidates.Add(k);
                }
            }

            if (Game1.characterData != null)
            {
                foreach (var kvp in Game1.characterData)
                {
                    string name = kvp.Key;
                    if (!IsInvalidOrEventNpc(name, Game1.getCharacterFromName(name)))
                        rawCandidates.Add(name);
                }
            }

            foreach (var npc in Utility.getAllCharacters())
            {
                if (npc != null && (npc.IsVillager || npc.Name.Equals("Marlon", StringComparison.OrdinalIgnoreCase)) && !IsInvalidOrEventNpc(npc.Name, npc))
                {
                    rawCandidates.Add(npc.Name);
                }
            }

            if (_bio.Relationships != null)
            {
                foreach (var configuredKey in _bio.Relationships.Keys)
                {
                    if (!string.Equals(configuredKey, _npcName, StringComparison.OrdinalIgnoreCase))
                        rawCandidates.Add(configuredKey);
                }
            }

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
            if (ContainsPoint(_biographyBox, x, y)) { FocusDialogueBox(_biographyBox, x, y); return; }
            if (_scaffoldBtnRect.Contains(x, y)) { InsertScaffold(); return; }
            if (new Rectangle(_uniqueBox.X, _uniqueBox.Y, _uniqueBox.Width, _uniqueBox.Height).Contains(x, y)) { FocusTextBox(_uniqueBox); return; }
            if (_homeBedCheckbox.bounds.Contains(x, y)) { _homeBedCheckbox.receiveLeftClick(x, y); return; }
            UnfocusAll();
        }

        private void HandleTab2Click(int x, int y)
        {
            if (ContainsPoint(_behaviorBox, x, y)) { FocusDialogueBox(_behaviorBox, x, y); return; }
            if (ContainsPoint(_dialogueExamplesBox, x, y)) { FocusDialogueBox(_dialogueExamplesBox, x, y); return; }

            if (_behaviorScaffoldRect.Contains(x, y))
            {
                _behaviorBox.SetText(
                    "[VOICE]\n- Tone: \n- Cadence: \n\n[SPEECH PATTERNS]\n- \n\n[MANNERISMS]\n- \n\n" +
                    "[IMMEDIATE REFLEXES]\n- \n\n[CONTEXT OVERRIDE]\n- ");
                MarkDirty();
                Game1.playSound("coin");
                return;
            }

            if (_insertBreakRect.Contains(x, y))
            {
                _dialogueExamplesBox.SetText((_dialogueExamplesBox.Text ?? "") + "#$b#");
                MarkDirty();
                Game1.playSound("shiny4");
                return;
            }
            if (_insertChoiceRect.Contains(x, y))
            {
                _dialogueExamplesBox.SetText((_dialogueExamplesBox.Text ?? "").TrimEnd() + "\n% 选项文本内容");
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

            if (_heartsStepper.ReceiveLeftClick(x, y)) return;

            // 婚姻门禁
            if (_gateMarriedPillRect.Contains(x, y))
            {
                currentStage.RequireMarried = !currentStage.RequireMarried;
                MarkDirty();
                Game1.playSound("drumkit6");
                return;
            }

// 超市状态药丸（三态：null -> true -> false -> null）
            if (_gateJojaClosedPillRect.Contains(x, y))
            {
                currentStage.RequireJojaMartClosed = currentStage.RequireJojaMartClosed.HasValue
                    ? (currentStage.RequireJojaMartClosed.Value ? false : (bool?)null)
                    : true;

                // 互斥保护：若超市已倒闭，则不可能还是 Joja 会员
                if (currentStage.RequireJojaMartClosed == true && currentStage.RequireJojaMember == true)
                {
                    currentStage.RequireJojaMember = null;
                }

                MarkDirty();
                Game1.playSound("drumkit6");
                return;
            }

// Joja 会员药丸（三态：null -> true -> false -> null）
            if (_gateJojaMemberPillRect.Contains(x, y))
            {
                currentStage.RequireJojaMember = currentStage.RequireJojaMember.HasValue
                    ? (currentStage.RequireJojaMember.Value ? false : (bool?)null)
                    : true;

                // 互斥保护：若已加入 Joja 会员，则超市绝对不可能倒闭
                if (currentStage.RequireJojaMember == true && currentStage.RequireJojaMartClosed == true)
                {
                    currentStage.RequireJojaMartClosed = null;
                }

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
            if (ContainsPoint(_stageTextBox, x, y)) { FocusDialogueBox(_stageTextBox, x, y); return; }
            if (ContainsPoint(_stageBarkBox, x, y)) { FocusDialogueBox(_stageBarkBox, x, y); return; }

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
            if (ContainsPoint(_relDescBox, x, y)) { FocusDialogueBox(_relDescBox, x, y); return; }

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
            if (ContainsPoint(_voiceBox, x, y)) { FocusDialogueBox(_voiceBox, x, y); return; }
            if (ContainsPoint(_habitsBox, x, y)) { FocusDialogueBox(_habitsBox, x, y); return; }
            if (ContainsPoint(_lensesBox, x, y)) { FocusDialogueBox(_lensesBox, x, y); return; }

            UnfocusAll();
        }

        public override void receiveKeyPress(Keys key)
        {
            // ── 1. 优先拦截星露谷原版菜单键（如默认的 'E' 键） ──
            // 无论是正在输入文本，还是处于未选中任何框的闲置状态，都坚决拦截 menuButton，防止误触关闭丢进度
            if (Game1.options.doesInputListContain(Game1.options.menuButton, key))
            {
                // 如果当前任何文本框正处于聚焦状态，输入法的 'e' / 'E' 会由 RecieveTextInput 正确录入，
                // 这里直接 return，阻止冒泡到基类导致窗口被关闭
                return;
            }

            // ── 2. 标签编辑器正在输入时的按键接管 ──
            if (_activeTab == 2 && _stageTagEditor != null && _stageTagEditor.IsAdding)
            {
                if (_stageTagEditor.ReceiveKeyPress(key))
                    return;
            }
            if (_activeTab == 4 && _globalTagEditor != null && _globalTagEditor.IsAdding)
            {
                if (_globalTagEditor.ReceiveKeyPress(key))
                    return;
            }

            // ── 3. 检测是否存在任何处于激活状态的文本输入框 ──
            DialogueTextInputBox activeBox = GetActiveDialogueBox();

            bool isAnyTextFocused = Game1.keyboardDispatcher.Subscriber != null
                                    || activeBox != null
                                    || (_activeTab == 0 && _uniqueBox.Selected)
                                    || (_activeTab == 3 && (_relSearchBox.Selected || _relHeadingBox.Selected));

            // ── 4. 处于文本编辑状态下的按键逻辑 ──
            if (isAnyTextFocused)
            {
                // 按 Esc 仅退出当前输入框的焦点，不关闭主编辑器
                if (key == Keys.Escape)
                {
                    UnfocusAll();
                    Game1.playSound("bigDeSelect");
                    return;
                }

                // 将方向键、退格、删除、Home/End 等特殊移动控制键转发给多行输入框
                if (activeBox != null && !DialogueTextInputBox.IsControlKeyDown())
                {
                    if (key == Keys.Left || key == Keys.Right || key == Keys.Home ||
                        key == Keys.End || key == Keys.Delete || key == Keys.Back)
                    {
                        activeBox.RecieveSpecialInput(key);
                        return;
                    }
                }

                // Tab 4 社交关系搜索框即时响应搜索
                if (_activeTab == 3 && _relSearchBox.Selected)
                {
                    base.receiveKeyPress(key);
                    FilterNpcList();
                    return;
                }

                base.receiveKeyPress(key);
                return;
            }

            // ── 5. 全局功能快捷键（未聚焦文本时生效） ──

            // Ctrl + S 快速保存并应用
            if (key == Keys.S && (Keyboard.GetState().IsKeyDown(Keys.LeftControl) || Keyboard.GetState().IsKeyDown(Keys.RightControl)))
            {
                SaveAndClose();
                return;
            }

            // 未聚焦文本时按 Esc，执行安全的离开检查（未保存会有二次确认弹窗）
            if (key == Keys.Escape)
            {
                TryCancel();
                return;
            }

            // 其它常规按键兜底调用基类（再次确保原版 menuButton 绝不穿透）
            if (!Game1.options.doesInputListContain(Game1.options.menuButton, key))
            {
                base.receiveKeyPress(key);
            }
        }

        public override void receiveScrollWheelAction(int direction)
        {
            int mx = Game1.getMouseX(), my = Game1.getMouseY();

            if (_activeTab == 0 && ContainsPoint(_biographyBox, mx, my)) { _biographyBox.ReceiveScrollWheel(direction); return; }
            if (_activeTab == 1)
            {
                if (ContainsPoint(_behaviorBox, mx, my)) { _behaviorBox.ReceiveScrollWheel(direction); return; }
                if (ContainsPoint(_dialogueExamplesBox, mx, my)) { _dialogueExamplesBox.ReceiveScrollWheel(direction); return; }
            }
            if (_activeTab == 2)
            {
                if (ContainsPoint(_stageTextBox, mx, my)) { _stageTextBox.ReceiveScrollWheel(direction); return; }
                if (ContainsPoint(_stageBarkBox, mx, my)) { _stageBarkBox.ReceiveScrollWheel(direction); return; }
            }
            if (_activeTab == 3)
            {
                if (_relLeftColRect.Contains(mx, my))
                {
                    _relListScrollOffset = Math.Clamp(_relListScrollOffset - (direction > 0 ? 1 : -1), 0, Math.Max(0, _filteredNpcs.Count - 6));
                    RecalculateTab4List();
                    return;
                }
                if (ContainsPoint(_relDescBox, mx, my)) { _relDescBox.ReceiveScrollWheel(direction); return; }
            }
            if (_activeTab == 4)
            {
                if (ContainsPoint(_voiceBox, mx, my)) { _voiceBox.ReceiveScrollWheel(direction); return; }
                if (ContainsPoint(_habitsBox, mx, my)) { _habitsBox.ReceiveScrollWheel(direction); return; }
                if (ContainsPoint(_lensesBox, mx, my)) { _lensesBox.ReceiveScrollWheel(direction); return; }
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

            // 2. 内部羊皮纸平铺底板
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

            if (!string.IsNullOrEmpty(_hoverText))
                DrawHoverTextCustom(b, _hoverText);

            drawMouse(b);
        }

        private static void DrawHoverTextCustom(SpriteBatch b, string text)
        {
            var sz = CustomFontManager.MeasureString(text, 17f);
            int boxW = (int)sz.X + 24;
            int boxH = (int)sz.Y + 24;
            int x = Game1.getOldMouseX() + 32;
            int y = Game1.getOldMouseY() + 32;
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
                string avatarFallback = string.IsNullOrEmpty(_npcName) ? "?" : _npcName.Substring(0, 1);
                CustomFontManager.DrawString(b, avatarFallback, new Vector2(portraitRect.X + 14, portraitRect.Y + 4), Color.Gray, CustomFontManager.SizeTitle);
            }

            string disp = Game1.getCharacterFromName(_npcName)?.displayName ?? _npcName;
            CustomFontManager.DrawString(b, $"{disp} ({_npcName}) · 人设工作台", new Vector2(headX + pSize + 12, headY + 2), Game1.textColor, CustomFontManager.SizeTitle);

            string status = _dirty ? "● 存在未保存改动" : (_hasOverlay ? "★ 自定义覆盖生效中" : "默认人设基准");
            Color statusCol = _dirty ? new Color(220, 90, 20) : (_hasOverlay ? new Color(30, 140, 40) : Color.DimGray);
            CustomFontManager.DrawString(b, status, new Vector2(headX + pSize + 14, headY + 28), statusCol, 18.5f);
        }

        // ── 各 Tab 具体渲染 ───────────────────────────────────────────────
        // ── 各 Tab 具体渲染（Label 锚定在自身输入框上方，杜绝错位） ───────────
        private void DrawTab1(SpriteBatch b, int mx, int my)
        {
            // 顶部 Label
            CustomFontManager.DrawString(b, "身份设定与心理矛盾（保留 [IDENTITY] 与 [PSYCHOLOGICAL CONFLICTS] 分节符）",
                new Vector2(_biographyBox.Position.X, _biographyBox.Position.Y - 24), Game1.textColor, 18.5f);

            DrawActionButton(b, _scaffoldBtnRect, "插入身份模板", mx, my, false);
            DrawStyledDialogueBox(b, _biographyBox);

            // 卡片 1: Unique
            DrawCard(b, _uniqueCardRect);
            CustomFontManager.DrawString(b, "特殊行为/身份标记 (Unique)",
                new Vector2(_uniqueBox.X, _uniqueCardRect.Y + 6), Game1.textColor, 18.5f);
            DrawSingleLineBox(b, _uniqueBox);
            if (_uniqueBox.X <= mx && mx <= _uniqueBox.X + _uniqueBox.Width && _uniqueBox.Y <= my && my <= _uniqueBox.Y + _uniqueBox.Height)
                _hoverText = "用于限定 NPC 的特殊行为或状态（如 'behind the counter', 'holding a football'）。";

            // 卡片 2: HomeBed
            DrawCard(b, _homeBedCardRect);
            CustomFontManager.DrawString(b, "就寝行为偏好",
                new Vector2(_homeBedCardRect.X + 16, _homeBedCardRect.Y + 6), Game1.textColor, 18.5f);
            _homeBedCheckbox.draw(b, 0, 0, this);
            if (_homeBedCheckbox.bounds.Contains(mx, my))
                _hoverText = "勾选后，NPC 在深夜对话时会偏向使用专属卧房就寝语境。";
        }

        private void DrawTab2(SpriteBatch b, int mx, int my)
        {
            // 左列：Label 与 Box 严格对齐
            CustomFontManager.DrawString(b, "行为规则 (BehavioralRules)",
                new Vector2(_behaviorBox.Position.X, _behaviorBox.Position.Y - 24), Game1.textColor, 18.5f);
            DrawActionButton(b, _behaviorScaffoldRect, "插入规则模板", mx, my, false);
            DrawStyledDialogueBox(b, _behaviorBox);

            // 右列：Label 与 Box 严格对齐
            CustomFontManager.DrawString(b, "对白范例 (Dialogue)",
                new Vector2(_dialogueExamplesBox.Position.X, _dialogueExamplesBox.Position.Y - 24), Game1.textColor, 18.5f);
            DrawActionButton(b, _insertBreakRect, "+ 分段符", mx, my, false);
            DrawActionButton(b, _insertChoiceRect, "+ 玩家选项", mx, my, false);

            if (_insertBreakRect.Contains(mx, my)) _hoverText = "插入 #$b#：在原版对话框中翻页。";
            if (_insertChoiceRect.Contains(mx, my)) _hoverText = "插入 % 选项：提供玩家可点击的分支回答。";

            DrawStyledDialogueBox(b, _dialogueExamplesBox);

            // 底部提示文字流式跟随
            CustomFontManager.DrawString(b, "提示：支持原版表情符 ($0 / $s) 与换行分段；选项以 % 开头。",
                new Vector2(_dialogueExamplesBox.Position.X, _dialogueExamplesBox.Position.Y + _dialogueExamplesBox.Extent.Y + 6), Color.DimGray, 15.5f);
        }

        private void DrawTab3(SpriteBatch b, int mx, int my)
        {
            DrawCard(b, _stageLeftColRect);
            CustomFontManager.DrawString(b, $"好感演变档位 ({_bio.ProgressStates.Count}/8)",
                new Vector2(_stageLeftColRect.X + 12, _stageLeftColRect.Y + 8), Game1.textColor, 18.5f);

            int visibleStages = Math.Min(_bio.ProgressStates.Count, 8);
            for (int i = 0; i < visibleStages; i++)
            {
                var r = _stageRowRects[i];
                bool isSel = (i == _stageIdx);
                bool isHover = r.Contains(mx, my);

                Color bg = isSel ? new Color(215, 185, 140) : (isHover ? new Color(255, 235, 205) : Color.White);
                IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(432, 439, 9, 9), r.X, r.Y, r.Width, r.Height, bg, 2f, false);

                string gateSummary = BuildGateSummary(_bio.ProgressStates[i]);
                CustomFontManager.DrawString(b, $"档位 {i + 1}  [{gateSummary}]", new Vector2(r.X + 12, r.Y + 8), isSel ? Game1.textColor : Color.Black, 18.5f);
            }

            if (_bio.ProgressStates.Count < 8)
                DrawActionButton(b, _newStageRect, "+ 新建好感档位", mx, my, false);

            if (_stageIdx < 0 || _stageIdx >= _bio.ProgressStates.Count)
            {
                CustomFontManager.DrawString(b, "从左侧列表选择或添加一个好感档位开始编辑。",
                    new Vector2(_stageRightColRect.X + 20, _stageRightColRect.Y + 40), Color.Gray, 18.5f);
                return;
            }

            var stage = _bio.ProgressStates[_stageIdx];

            // 门禁药丸控制条
            CustomFontManager.DrawString(b, "激活门禁:", new Vector2(_stageRightColRect.X, _stageRightColRect.Y + 6), Game1.textColor, 18.5f);
            _heartsStepper.Draw(b);

// 1. 婚姻门禁
            DrawPillButton(b, _gateMarriedPillRect, stage.RequireMarried ? "已婚" : "不限婚姻", stage.RequireMarried, mx, my);
            if (_gateMarriedPillRect.Contains(mx, my))
            {
                _hoverText = stage.RequireMarried
                    ? "【激活条件：必须与该 NPC 结婚】\n仅当玩家与当前角色处于已婚状态时，本档位人设才会激活生效。"
                    : "【激活条件：不限婚姻】\n无论玩家单身、与该角色结婚还是与其他人结婚，均可进入本档位。";
            }

// 2. 超市倒闭门禁
            string jojaClosedText = stage.RequireJojaMartClosed.HasValue ? (stage.RequireJojaMartClosed.Value ? "超市:倒闭" : "超市:营业") : "超市:不限";
            DrawPillButton(b, _gateJojaClosedPillRect, jojaClosedText, stage.RequireJojaMartClosed.HasValue, mx, my);
            if (_gateJojaClosedPillRect.Contains(mx, my))
            {
                if (stage.RequireJojaMartClosed == true)
                    _hoverText = "【激活条件：Joja 超市已倒闭】\n判定玩家完成了活动中心全部献祭线并驱逐了 Joja 超市。\n注：此时会自动解除与 Joja 会员的冲突。";
                else if (stage.RequireJojaMartClosed == false)
                    _hoverText = "【激活条件：Joja 超市保持营业】\n判定小镇超市依然正常开门，尚未完成社区中心献祭。";
                else
                    _hoverText = "【激活条件：不限超市状态】\n不对 Joja 超市是否倒闭做任何前置要求（默认状态）。";
            }

// 3. Joja 会员门禁
            string jojaMemberText = stage.RequireJojaMember.HasValue ? (stage.RequireJojaMember.Value ? "会员:加入" : "会员:未入") : "会员:不限";
            DrawPillButton(b, _gateJojaMemberPillRect, jojaMemberText, stage.RequireJojaMember.HasValue, mx, my);
            if (_gateJojaMemberPillRect.Contains(mx, my))
            {
                if (stage.RequireJojaMember == true)
                    _hoverText = "【激活条件：玩家是 Joja 会员】\n判定农夫已花费 5000G 在莫里斯处购买了 Joja 会员资格。\n注：此时超市绝不会倒闭，与“超市:倒闭”互斥。";
                else if (stage.RequireJojaMember == false)
                    _hoverText = "【激活条件：玩家未加入 Joja】\n判定农夫拒绝了 Joja 会员，坚持走传统村民路线。";
                else
                    _hoverText = "【激活条件：不限会员身份】\n不对玩家是否购买 Joja 会员做任何限制（默认状态）。";
            }

            DrawActionButton(b, _deleteStageRect, "删除此档", mx, my, isDanger: true);

            // 三个流式块的 Label，直接通过对应 Box 的 Position 反向精准锚定
            CustomFontManager.DrawString(b, "阶段态度演变 (Text)",
                new Vector2(_stageTextBox.Position.X, _stageTextBox.Position.Y - 22), Game1.textColor, 18.5f);
            DrawStyledDialogueBox(b, _stageTextBox);

            CustomFontManager.DrawString(b, "碎碎念心智 (BarkMindset: 规定此时的心态与注意力)",
                new Vector2(_stageBarkBox.Position.X, _stageBarkBox.Position.Y - 22), Game1.textColor, 18.5f);
            DrawStyledDialogueBox(b, _stageBarkBox);

            CustomFontManager.DrawString(b, "阶段专属关注池 (Preoccupations: 优先提及的事物)",
                new Vector2(_stageRightColRect.X, _stageTagEditor.Bounds.Y - 22), Game1.textColor, 18.5f);
            _stageTagEditor.Draw(b);
        }

        private void DrawTab4(SpriteBatch b, int mx, int my)
        {
            // 左列
            DrawCard(b, _relLeftColRect);
            CustomFontManager.DrawString(b, "目标角色列表 (★已定制)",
                new Vector2(_relSearchBox.X, _relLeftColRect.Y + 8), Game1.textColor, 18.5f);
            DrawSingleLineBox(b, _relSearchBox);
            if (string.IsNullOrEmpty(_relSearchBox.Text))
                CustomFontManager.DrawString(b, "搜索角色...", new Vector2(_relSearchBox.X + 8, _relSearchBox.Y + 6), Color.Gray * 0.7f, 18.5f);

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
                CustomFontManager.DrawString(b, disp, new Vector2(r.X + 10, r.Y + 6), isSel ? Game1.textColor : (hasConfig ? new Color(160, 60, 0) : Color.Black), 18.5f);
            }

            DrawActionButton(b, _relAddRect, "+ 定制关系", mx, my, false);
            DrawActionButton(b, _relDelRect, "- 清除", mx, my, isDanger: true);

            // 右列
            if (string.IsNullOrEmpty(_relSelectedNpc))
            {
                CustomFontManager.DrawString(b, "从左侧列表选择目标角色。", new Vector2(_relRightColRect.X + 20, _relRightColRect.Y + 40), Color.Gray, 18.5f);
                return;
            }

            string targetDisp = Game1.getCharacterFromName(_relSelectedNpc)?.displayName ?? _relSelectedNpc;
            CustomFontManager.DrawString(b, $"{_npcName} 对 {targetDisp} 的单向社交关系",
                new Vector2(_relHeadingBox.X, _relRightColRect.Y + 4), Game1.textColor, CustomFontManager.SizeTitle);

            CustomFontManager.DrawString(b, "关系称谓与定位 (Heading: 如 'Wife', 'Business Rival')",
                new Vector2(_relHeadingBox.X, _relHeadingBox.Y - 22), Game1.textColor, 18.5f);
            DrawSingleLineBox(b, _relHeadingBox);

            CustomFontManager.DrawString(b, "深层心理与互动细节 (Description)",
                new Vector2(_relDescBox.Position.X, _relDescBox.Position.Y - 22), Game1.textColor, 18.5f);
            DrawStyledDialogueBox(b, _relDescBox);

            CustomFontManager.DrawString(b, "提示：如需双方互动感知，请在两人的编辑器中分别配置相互的关系定位。",
                new Vector2(_relDescBox.Position.X, _relDescBox.Position.Y + _relDescBox.Extent.Y + 6), Color.DimGray, 15.5f);
        }

        private void DrawTab5(SpriteBatch b, int mx, int my)
        {
            // 左列
            DrawCard(b, _tab5LeftColRect);
            CustomFontManager.DrawString(b, "日常碎碎念总控",
                new Vector2(_tab5LeftColRect.X + 12, _tab5LeftColRect.Y + 8), Game1.textColor, 18.5f);
            _enableBarkCheckbox.draw(b, 0, 0, this);

            DrawActionButton(b, _scrapeRect, "↺ 从原版对白智能抓取范例", mx, my, false);

            CustomFontManager.DrawString(b, "全局常态关注池 (Preoccupations)",
                new Vector2(_globalTagEditor.Bounds.X, _globalTagEditor.Bounds.Y - 22), Game1.textColor, 18.5f);
            _globalTagEditor.Draw(b);

            // 右列：三大提示词卡片，Label 直接跟随各自 Box 的 X 与 Y - 22
            CustomFontManager.DrawString(b, "口吻与态度 (Voice & Attitude)",
                new Vector2(_voiceBox.Position.X, _voiceBox.Position.Y - 22), Game1.textColor, 18.5f);
            DrawStyledDialogueBox(b, _voiceBox);
            if (ContainsPoint(_voiceBox, mx, my)) _hoverText = "限定碎碎念的基本语调、说话长短与即时情绪基调。";

            CustomFontManager.DrawString(b, "口头习惯 (Spoken Habits)",
                new Vector2(_habitsBox.Position.X, _habitsBox.Position.Y - 22), Game1.textColor, 18.5f);
            DrawStyledDialogueBox(b, _habitsBox);
            if (ContainsPoint(_habitsBox, mx, my)) _hoverText = "NPC 的口头禅、叹气声、常用起手式（如 'Well,', 'Sigh...'）。";

            CustomFontManager.DrawString(b, "观察透镜 (Observation Lenses)",
                new Vector2(_lensesBox.Position.X, _lensesBox.Position.Y - 22), Game1.textColor, 18.5f);
            DrawStyledDialogueBox(b, _lensesBox);
            if (ContainsPoint(_lensesBox, mx, my)) _hoverText = "NPC 打量周围世界时的特殊视角（例如铁匠关注矿物与工具锈蚀，农夫关注作物与雨水）。";
        }

        /// <summary>
        /// 绘制精细的 DialogueTextInputBox 边框与内衬，留足呼吸空间
        private static void DrawStyledDialogueBox(SpriteBatch b, DialogueTextInputBox box)
        {
            // DialogueTextInputBox 自身已内置高品质底槽与聚焦高亮逻辑，直接委托给 Draw 即可
            box.Draw(b);
        }
        private static bool ContainsPoint(DialogueTextInputBox box, int x, int y)
        {
            return x >= box.Position.X && x <= box.Position.X + box.Extent.X &&
                   y >= box.Position.Y && y <= box.Position.Y + box.Extent.Y;
        }

        private void FocusDialogueBox(DialogueTextInputBox box, int x, int y)
        {
            UnfocusAll();
            box.Selected = true;
            Game1.keyboardDispatcher.Subscriber = box;
            box.ReceiveLeftClick(x, y);
        }

        private DialogueTextInputBox GetActiveDialogueBox()
        {
            if (_activeTab == 0 && _biographyBox.Selected) return _biographyBox;
            if (_activeTab == 1)
            {
                if (_behaviorBox.Selected) return _behaviorBox;
                if (_dialogueExamplesBox.Selected) return _dialogueExamplesBox;
            }
            if (_activeTab == 2)
            {
                if (_stageTextBox.Selected) return _stageTextBox;
                if (_stageBarkBox.Selected) return _stageBarkBox;
            }
            if (_activeTab == 3 && _relDescBox.Selected) return _relDescBox;
            if (_activeTab == 4)
            {
                if (_voiceBox.Selected) return _voiceBox;
                if (_habitsBox.Selected) return _habitsBox;
                if (_lensesBox.Selected) return _lensesBox;
            }
            return null;
        }

        private void DrawTabButton(SpriteBatch b, Rectangle rect, string label, bool isActive, int mx, int my)
        {
            bool isHover = rect.Contains(mx, my);
            Color bg = isActive ? new Color(215, 185, 140) : (isHover ? new Color(255, 235, 205) : new Color(145, 95, 45));

            IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(432, 439, 9, 9),
                rect.X, rect.Y, rect.Width, rect.Height, bg, 4f, false);

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

            Color slotColor = box.Selected ? new Color(255, 248, 220) : new Color(228, 212, 184) * 0.9f;

            IClickableMenu.drawTextureBox(
                b,
                Game1.mouseCursors,
                new Rectangle(403, 383, 6, 6),
                boxRect.X,
                boxRect.Y,
                boxRect.Width,
                boxRect.Height,
                slotColor,
                2f,
                false
            );

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

            string text = box.Text ?? string.Empty;
            Vector2 textSize = CustomFontManager.MeasureString(text, 18.5f);
            float textX = boxRect.X + 10;
            float textY = boxRect.Y + (boxRect.Height - textSize.Y) / 2f - 1;

            if (!string.IsNullOrEmpty(text))
            {
                CustomFontManager.DrawString(b, text, new Vector2(textX, textY), Game1.textColor, 18.5f);
            }

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
        private void FocusTextBox(TextBox box)
        {
            UnfocusAll();
            box.SelectMe();
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
            _stageTextBox.SetText(s.Text ?? string.Empty);
            _stageBarkBox.SetText(s.BarkMindset ?? string.Empty);
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
                _relDescBox.SetText(r.Description ?? string.Empty);
            }
            else
            {
                _relHeadingBox.Text = string.Empty;
                _relDescBox.SetText(string.Empty);
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
            _voiceBox.SetText(p?.VoiceAndAttitude ?? string.Empty);
            _habitsBox.SetText(p?.SpokenHabits ?? string.Empty);
            _lensesBox.SetText(p?.ObservationLenses ?? string.Empty);
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
                _dialogueExamplesBox.SetText(entry.Description);
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
            _biographyBox.SetText(BiographyScaffold.Replace("{NPC}", _npcName));
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
                    _biographyBox.SetText(_bio.Biography ?? string.Empty);
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