#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using Newtonsoft.Json;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;
using ValleytalkReborn.Services;
using ValleytalkReborn.UI;

namespace ValleytalkReborn
{
    /// <summary>
    /// 简单的复选框辅助类（整数字号与无白缝精修版）
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
            b.Draw(Game1.mouseCursors, new Vector2(bounds.X, bounds.Y), src, Color.White, 0f, Vector2.Zero, 3f, SpriteEffects.None, 1f);

            if (!string.IsNullOrEmpty(label))
                CustomFontManager.DrawString(b, label, new Vector2(bounds.X + 34, bounds.Y + 4), BioEditorMenu.TextPrimary, CustomFontManager.SizeRegular);
        }
    }

    /// <summary>
    /// 角色人设沉浸式编辑器：
    /// 仅主标题、Tab、主动作按钮使用 Bold 字体，其余正文与栏目标题通通采用 Medium 字体，视觉层次清爽轻盈。
    /// 统一按钮高度 26px；Tab 3/4/5 复制按钮已换行至标签右侧。
    /// 导出改为直接复制 JSON 到剪贴板；动作按钮文字为炭黑；卡片边框为暖金棕。
    /// </summary>
    internal sealed class BioEditorMenu : IClickableMenu
    {
        // ── 布局尺寸常量 ──────────────────────────────────────────────────
        private const int HeaderH = 68;
        private const int TabBarH = 38;
        private const int FooterH = 56;
        private const int ContentPadding = 24;

        // 统一整数字号（避免亚像素模糊）
        private const float TitleFontSize = CustomFontManager.SizeTitle;       // 24f (Bold)
        private const float TabFontSize = CustomFontManager.SizeRegular;       // 18f (Bold)
        private const float ButtonFontSize = CustomFontManager.SizeRegular;    // 18f (Bold)

        private const float SectionHeaderSize = CustomFontManager.SizeRegular; // 18f (Medium: 栏目标题回归自然)
        private const float ContentFontSize = CustomFontManager.SizeRegular;   // 18f (Medium: 输入框与正文)
        private const float TipFontSize = CustomFontManager.SizeSmall;         // 15f (Medium: 提示与说明)

        // 统一复制/工具按钮规格（全 Tab 通用）
        private const int CopyBtnW = 100;
        private const int CopyBtnH = 26;   // ★ 统一高度
        private const int RowBtnH = 26;    // ★ 栏目标题行按钮统一高度
        private const int LabelRowGap = 6; // ★ 标签行与输入框的固定间距

        // ── 统一语义化字体配色体系（彻底告别生冷死黑与发脏冷灰） ──
        public static readonly Color TextPrimary   = new Color(45, 26, 14);   // 主文字/标题/常规字（提纯碳焦褐，边缘更锐利）
        public static readonly Color TextSecondary = new Color(112, 78, 52);  // 栏目标题/分类标签
        public static readonly Color TextMuted     = new Color(158, 138, 118);// 说明提示/占位符/禁用项
        public static readonly Color TextAccent    = new Color(175, 75, 20);  // 选中高亮/强调星标
        public static readonly Color TextSuccess   = new Color(40, 118, 48);  // 生效/已定制/成功
        public static readonly Color TextWarning   = new Color(195, 92, 18);  // 未保存/警告
        public static readonly Color TextDanger    = new Color(188, 46, 38);  // 危险/删除/清空
        public static readonly Color TextOnDark    = new Color(255, 248, 238);// 深木色底板反白文字
        public static readonly Color TextOnLightBtn = new Color(52, 28, 16);  // 按钮专属深色文字（浅木底/金黄底）
        public static readonly Color TextOnDarkBtn  = new Color(255, 250, 242);// 按钮专属反白文字（深木底/暗红底）

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
        private readonly BioEditorViewModel _vm;
        private int _activeTab;
        private string? _hoverText;

        private Texture2D? _npcPortrait;
        private Rectangle _portraitSmileRect;

        // 通用组件
        private ClickableTextureComponent _closeButton;
        private float _closeButtonHoverScale;              // 悬停缩放系数 (当前)
        private const float CloseButtonBaseScale = 3f;      // 基准缩放倍数
        private readonly Rectangle[] _tabRects = new Rectangle[5];
        private Rectangle _cancelRect;
        private Rectangle _saveRect;
        private Rectangle _resetPageRect; // 当前页恢复原版
        private Rectangle _resetAllRect;  // 全部恢复原版

        // ── Tab 1 控件（身份心理） ────────────────────────────────────────
        private DialogueTextInputBox _biographyBox;
        private TextBox _uniqueBox;
        private SimpleCheckbox _homeBedCheckbox;
        private Rectangle _scaffoldBtnRect;
        private Rectangle _uniqueCardRect;
        private Rectangle _homeBedCardRect;
        private Rectangle _copyBiographyRect;

        // ── Tab 2 控件（言行举止） ────────────────────────────────────────
        private DialogueTextInputBox _behaviorBox;
        private DialogueTextInputBox _dialogueExamplesBox;
        private Rectangle _behaviorScaffoldRect;
        private Rectangle _insertBreakRect;
        private Rectangle _insertChoiceRect;
        private Rectangle _tab2LeftColRect;
        private Rectangle _tab2RightColRect;
        private Rectangle _copyBehaviorRect;
        private Rectangle _copyDialogueExamplesRect;

        // ── Tab 3 控件（好感演变） ────────────────────────────────────────
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
        private Rectangle _copyStageTextRect;
        private Rectangle _copyStageBarkRect;

        // ── Tab 4 控件（社交关系） ────────────────────────────────────────
        private int _relListScrollOffset = 0;
        private bool _isDraggingTab4Scrollbar = false; // ★ 添加拖拽状态标记
        private TextBox _relSearchBox;
        private TextBox _relHeadingBox;
        private DialogueTextInputBox _relDescBox;

        private Rectangle _relLeftColRect;
        private Rectangle _relRightColRect;
        private Rectangle _relAddRect;
        private Rectangle _relDelRect;
        private readonly List<(Rectangle Rect, int Index)> _relVisibleItemRects = new();
        private Rectangle _copyRelDescRect;

        // ── Tab 5 控件（环境感知） ────────────────────────────────────────
        private SimpleCheckbox _enableBarkCheckbox;
        private TagListEditor _globalTagEditor;
        private DialogueTextInputBox _voiceBox;
        private DialogueTextInputBox _habitsBox;
        private DialogueTextInputBox _lensesBox;
        private Rectangle _scrapeRect;
        private Rectangle _tab5LeftColRect;
        private Rectangle _tab5RightColRect;
        private Rectangle _copyVoiceRect;
        private Rectangle _copyHabitsRect;
        private Rectangle _copyLensesRect;

        // ── VT-UI-02 作用域胶囊 / 导入导出 ──
        private Rectangle _scopeCapsuleRect;
        private Rectangle _importRect;
        private Rectangle _exportRect;

        // ── 构造函数 ──────────────────────────────────────────────────────
        public BioEditorMenu(string npcName, IClickableMenu returnMenu)
            : base(
                (Game1.uiViewport.Width - Math.Clamp(Game1.uiViewport.Width - 100, 920, 1160)) / 2,
                (Game1.uiViewport.Height - Math.Clamp(Game1.uiViewport.Height - 80, 600, 750)) / 2,
                Math.Clamp(Game1.uiViewport.Width - 100, 920, 1160),
                Math.Clamp(Game1.uiViewport.Height - 80, 600, 750),
                showUpperRightCloseButton: false)
        {
            this.allClickableComponents ??= new List<ClickableComponent>();

            _npcName = npcName;
            _returnMenu = returnMenu;

            _vm = new BioEditorViewModel(npcName, ModEntry.BioStorage!);
            _activeTab = 0;

            LoadNpcPortrait();

            // 1. 这些多行大文本框才使用 CreateTextInputBox：
            _biographyBox = CreateTextInputBox(4000);
            _behaviorBox = CreateTextInputBox(4000);
            _dialogueExamplesBox = CreateTextInputBox(4000);
            _stageTextBox = CreateTextInputBox(2000);
            _stageBarkBox = CreateTextInputBox(2000);
            _relDescBox = CreateTextInputBox(2000);
            _voiceBox = CreateTextInputBox(2000);
            _habitsBox = CreateTextInputBox(2000);
            _lensesBox = CreateTextInputBox(2000);

            // 2. 下面这三个是单行 TextBox，必须使用 new TextBox(...) 初始化：
            Texture2D boxTex = LoadTextBoxTexture();
            _uniqueBox = new TextBox(boxTex, null, Game1.smallFont, Game1.textColor);
            // 关闭原版 TextBox 的像素宽度截断（Text setter 内置递归截断会静默损毁程序化赋值的长文本）
            _uniqueBox.limitWidth = false;
            _uniqueBox.textLimit = 20;
            _relSearchBox = new TextBox(boxTex, null, Game1.smallFont, Game1.textColor);
            // 关闭原版 TextBox 的像素宽度截断（Text setter 内置递归截断会静默损毁程序化赋值的长文本）
            _relSearchBox.limitWidth = false;
            _relHeadingBox = new TextBox(boxTex, null, Game1.smallFont, Game1.textColor);
            // 关闭原版 TextBox 的像素宽度截断（Text setter 内置递归截断会静默损毁程序化赋值的长文本）
            _relHeadingBox.limitWidth = false;

            _homeBedCheckbox = new SimpleCheckbox("床位固定 (HomeLocationBed)", -1, 0, 0);
            _enableBarkCheckbox = new SimpleCheckbox("启用日常碎碎念 (AmbientBarks)", -1, 0, 0);

            _stageTagEditor = new TagListEditor(Rectangle.Empty);
            _stageTagEditor.OnChanged += () =>
            {
                _vm.SetStagePreoccupations(_stageTagEditor.Tags.Count > 0 ? _stageTagEditor.Tags.ToList() : null);
            };

            _globalTagEditor = new TagListEditor(Rectangle.Empty);
            _globalTagEditor.OnChanged += () =>
            {
                _vm.SyncGlobalPreoccupations(_globalTagEditor.Tags.Count > 0 ? _globalTagEditor.Tags.ToList() : null);
            };

            _heartsStepper = new NumberStepper(Rectangle.Empty, 0, 0, 14, 2, " 心");
            _heartsStepper.OnChanged += val =>
            {
                _vm.SetStageHearts(val);
            };

            _closeButton = new ClickableTextureComponent(
                new Rectangle(xPositionOnScreen + width - 52, yPositionOnScreen + 16, 36, 36),
                Game1.mouseCursors, new Rectangle(337, 494, 12, 12), CloseButtonBaseScale);

            _biographyBox.SetText(_vm.GetBiography());
            string rawUnique = _vm.GetUnique() ?? string.Empty;
            _uniqueBox.Text = rawUnique.Length > 20 ? rawUnique.Substring(0, 20) : rawUnique;

            _homeBedCheckbox.isChecked = _vm.GetHomeLocationBed();

            _enableBarkCheckbox.isChecked = _vm.Bio.EnableAmbientBarks;
            _globalTagEditor.SetTags(_vm.Bio.Preoccupations);
            SyncTab5BarkBoxes();

            _vm.InitNpcCatalog();
            _vm.RecomputeFilteredNpcs(_relSearchBox.Text);
            if (_vm.FilteredNpcs.Count > 0)
            {
                _vm.SelectRelationship(0);
                SelectRelationshipView(_vm.SelectedRelationshipNpc);
            }

            if (_vm.Bio.ProgressStates.Count > 0)
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

        // ── 界面几何布局重构 ─────────────────────────────────────────────────
        private void Layout()
        {
            _closeButton.bounds = new Rectangle(xPositionOnScreen + width - 52, yPositionOnScreen + 16, 36, 36);

            // 顶栏右侧：作用域胶囊 + 导入 + 导出（位于关闭按钮左侧，间距 8px）
            int hdrBtnY = yPositionOnScreen + 36;
            int hdrBtnH = 24;
            int rightEdge = _closeButton.bounds.Left - 8;
            _exportRect = new Rectangle(rightEdge - 70, hdrBtnY, 70, hdrBtnH);
            _importRect = new Rectangle(_exportRect.Left - 8 - 60, hdrBtnY, 60, hdrBtnH);
            _scopeCapsuleRect = new Rectangle(_importRect.Left - 8 - 110, hdrBtnY, 110, hdrBtnH);

            int contentLeft = xPositionOnScreen + ContentPadding;
            int contentW = width - ContentPadding * 2;

            // 1. Tab 栏
            int tabGap = 8;
            int tabW = (contentW - tabGap * (TabTitles.Length - 1)) / TabTitles.Length;
            int tabY = yPositionOnScreen + HeaderH + 4;
            for (int i = 0; i < TabTitles.Length; i++)
                _tabRects[i] = new Rectangle(contentLeft + i * (tabW + tabGap), tabY, tabW, TabBarH);

            // 2. 底部功能栏（四个按钮：取消 | 保存 || 当前页恢复原版 | 全部恢复原版）
            int footerY = yPositionOnScreen + height - FooterH + 10;
            int btnH = 38;
            int btnW = Math.Clamp((contentW - 36) / 4, 140, 200);

            _cancelRect = new Rectangle(contentLeft, footerY, btnW, btnH);
            _saveRect = new Rectangle(contentLeft + btnW + 12, footerY, btnW, btnH);

            _resetAllRect = new Rectangle(xPositionOnScreen + width - ContentPadding - btnW, footerY, btnW, btnH);
            _resetPageRect = new Rectangle(_resetAllRect.X - btnW - 12, footerY, btnW, btnH);

            // 3. 内容区总空间
            int bodyTop = tabY + TabBarH + 12;
            int bodyBottom = footerY - 14;
            int bodyH = bodyBottom - bodyTop;

            // ── Tab 1 布局 ──
            if (_activeTab == 0)
            {
                int cardH = 68;
                int topLabelH = 26;
                _scaffoldBtnRect = new Rectangle(contentLeft + contentW - 140, bodyTop, 140, RowBtnH);

                int bioH = bodyH - cardH - topLabelH - 24;
                SetBoxBounds(_biographyBox, contentLeft, bodyTop + topLabelH + 4, contentW, bioH);

                int cardY = (int)_biographyBox.Position.Y + (int)_biographyBox.Extent.Y + 16;
                int cardW = (contentW - 16) / 2;
                _uniqueCardRect = new Rectangle(contentLeft, cardY, cardW, cardH);
                _homeBedCardRect = new Rectangle(contentLeft + cardW + 16, cardY, cardW, cardH);

                _uniqueBox.X = _uniqueCardRect.X + 12;
                _uniqueBox.Y = _uniqueCardRect.Y + 28;
                _uniqueBox.Width = _uniqueCardRect.Width - 24;
                _uniqueBox.Height = 30;

                _homeBedCheckbox.bounds = new Rectangle(_homeBedCardRect.X + 16, _homeBedCardRect.Y + 26, 28, 28);

                // ★ 复制按钮：与「插入身份模板」按钮同一行、居中对齐
                _copyBiographyRect = new Rectangle(
                    _scaffoldBtnRect.Left - 8 - CopyBtnW,
                    _scaffoldBtnRect.Y + (_scaffoldBtnRect.Height - CopyBtnH) / 2,
                    CopyBtnW, CopyBtnH);
            }

            // ── Tab 2 布局 ──
            if (_activeTab == 1)
            {
                int colW = (contentW - 16) / 2;
                _tab2LeftColRect = new Rectangle(contentLeft, bodyTop, colW, bodyH);
                _tab2RightColRect = new Rectangle(contentLeft + colW + 16, bodyTop, colW, bodyH);

                int topBarH = 32;
                int bottomTipH = 24;
                int boxH = bodyH - topBarH - bottomTipH - 12;

                _behaviorScaffoldRect = new Rectangle(_tab2LeftColRect.Right - 130, bodyTop + 2, 130, RowBtnH);
                SetBoxBounds(_behaviorBox, _tab2LeftColRect.X, bodyTop + topBarH, colW, boxH);

                int toolBtnW = 95;
                _insertChoiceRect = new Rectangle(_tab2RightColRect.Right - toolBtnW, bodyTop + 2, toolBtnW, RowBtnH);
                _insertBreakRect = new Rectangle(_insertChoiceRect.Left - toolBtnW - 6, bodyTop + 2, toolBtnW, RowBtnH);
                SetBoxBounds(_dialogueExamplesBox, _tab2RightColRect.X, bodyTop + topBarH, colW, boxH);

                _copyBehaviorRect = new Rectangle(
                    _behaviorScaffoldRect.Left - 8 - CopyBtnW,
                    _behaviorScaffoldRect.Y + (_behaviorScaffoldRect.Height - CopyBtnH) / 2,
                    CopyBtnW, CopyBtnH);

                _copyDialogueExamplesRect = new Rectangle(
                    _insertBreakRect.Left - 6 - CopyBtnW,
                    _insertBreakRect.Y + (_insertBreakRect.Height - CopyBtnH) / 2,
                    CopyBtnW, CopyBtnH);
            }

            // ── Tab 3 布局 ──
            if (_activeTab == 2)
            {
                int leftColW = 240;
                int rightColW = contentW - leftColW - 16;
                _stageLeftColRect = new Rectangle(contentLeft, bodyTop, leftColW, bodyH);
                _stageRightColRect = new Rectangle(contentLeft + leftColW + 16, bodyTop, rightColW, bodyH);

                int rowH = 38;
                for (int i = 0; i < _stageRowRects.Length; i++)
                    _stageRowRects[i] = new Rectangle(_stageLeftColRect.X, _stageLeftColRect.Y + 34 + i * (rowH + 4), leftColW, rowH);

                int nextY = _stageLeftColRect.Y + 34 + Math.Min(_vm.Bio.ProgressStates.Count, 8) * (rowH + 4);
                _newStageRect = new Rectangle(_stageLeftColRect.X, nextY, leftColW, 34);

                int rightX = _stageRightColRect.X;
                int rightY = _stageRightColRect.Y;

                _heartsStepper.SetBounds(new Rectangle(rightX + 85, rightY, 110, 28));
                _gateMarriedPillRect = new Rectangle(rightX + 203, rightY, 95, 28);
                _gateJojaClosedPillRect = new Rectangle(rightX + 306, rightY, 105, 28);
                _gateJojaMemberPillRect = new Rectangle(rightX + 419, rightY, 105, 28);
                _deleteStageRect = new Rectangle(rightX + rightColW - 95, rightY, 95, 28);

                int flowY = rightY + 38;
                int tagEditorH = 56;
                int labelH = 26;   // ★ 与 RowBtnH 一致
                int availTextH = (bodyBottom - flowY) - tagEditorH - (labelH * 3) - 30;
                int singleBoxH = Math.Max(70, availTextH / 2);

                SetBoxBounds(_stageTextBox, rightX, flowY + labelH + LabelRowGap, rightColW, singleBoxH);
                flowY = (int)_stageTextBox.Position.Y + (int)_stageTextBox.Extent.Y + 14;

                SetBoxBounds(_stageBarkBox, rightX, flowY + labelH + LabelRowGap, rightColW, singleBoxH);
                flowY = (int)_stageBarkBox.Position.Y + (int)_stageBarkBox.Extent.Y + 14;

                _stageTagEditor.SetBounds(new Rectangle(rightX, flowY + labelH + LabelRowGap, rightColW, tagEditorH));

                // ★ 复制按钮：紧贴标签文字右侧，不再贴右端
                _copyStageTextRect = MakeLabelRightCopyRect("阶段态度演变 (Text)", rightX, (int)_stageTextBox.Position.Y - labelH - LabelRowGap);
                _copyStageBarkRect = MakeLabelRightCopyRect("碎碎念心智 (BarkMindset: 规定此时的心态与注意力)", rightX, (int)_stageBarkBox.Position.Y - labelH - LabelRowGap);
            }

            // ── Tab 4 布局 ──
            if (_activeTab == 3)
            {
                int leftColW = 250;
                int rightColW = contentW - leftColW - 16;
                _relLeftColRect = new Rectangle(contentLeft, bodyTop, leftColW, bodyH);
                _relRightColRect = new Rectangle(contentLeft + leftColW + 16, bodyTop, rightColW, bodyH);

                _relSearchBox.X = _relLeftColRect.X + 8;
                _relSearchBox.Y = _relLeftColRect.Y + 32;
                _relSearchBox.Width = _relLeftColRect.Width - 16;
                _relSearchBox.Height = 30;

                int relBtnW = (leftColW - 8) / 2;
                _relAddRect = new Rectangle(_relLeftColRect.X, _relLeftColRect.Bottom - 34, relBtnW, 34);
                _relDelRect = new Rectangle(_relLeftColRect.X + relBtnW + 8, _relLeftColRect.Bottom - 34, relBtnW, 34);

                int rightX = _relRightColRect.X;
                int flowY = bodyTop + 36;
                int labelH = 26;

                _relHeadingBox.X = rightX;
                _relHeadingBox.Y = flowY + labelH + LabelRowGap;
                _relHeadingBox.Width = rightColW;
                _relHeadingBox.Height = 32;
                flowY = _relHeadingBox.Y + _relHeadingBox.Height + 16;

                int bottomTipH = 26;
                int descH = bodyBottom - flowY - labelH - bottomTipH - LabelRowGap - 6;
                SetBoxBounds(_relDescBox, rightX, flowY + labelH + LabelRowGap, rightColW, Math.Max(90, descH));

                RecalculateTab4List();

                _copyRelDescRect = MakeLabelRightCopyRect("深层心理与互动细节 (Description)", rightX, (int)_relDescBox.Position.Y - labelH - LabelRowGap);
            }

            // ── Tab 5 布局 ──
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

                int rightX = _tab5RightColRect.X;
                int rightY = _tab5RightColRect.Y;
                int sectionH = (bodyH - 16) / 3;
                int labelH = 26;

                for (int i = 0; i < 3; i++)
                {
                    int blockTop = rightY + i * sectionH;
                    int boxY = blockTop + labelH + LabelRowGap;
                    int boxH = sectionH - labelH - LabelRowGap - 12;

                    if (i == 0) SetBoxBounds(_voiceBox, rightX, boxY, rightColW, boxH);
                    else if (i == 1) SetBoxBounds(_habitsBox, rightX, boxY, rightColW, boxH);
                    else SetBoxBounds(_lensesBox, rightX, boxY, rightColW, boxH);
                }

                _copyVoiceRect = MakeLabelRightCopyRect("口吻与态度 (Voice & Attitude)", rightX, (int)_voiceBox.Position.Y - labelH - LabelRowGap);
                _copyHabitsRect = MakeLabelRightCopyRect("口头习惯 (Spoken Habits)", rightX, (int)_habitsBox.Position.Y - labelH - LabelRowGap);
                _copyLensesRect = MakeLabelRightCopyRect("观察透镜 (Observation Lenses)", rightX, (int)_lensesBox.Position.Y - labelH - LabelRowGap);
            }
        }

        /// <summary>依据栏目标题文本长度，把「复制全部」紧贴标签右侧摆放（同标签行基线居中）。</summary>
        private static Rectangle MakeLabelRightCopyRect(string labelText, int labelX, int labelY)
        {
            float labelW = CustomFontManager.MeasureString(labelText, SectionHeaderSize).X;
            int x = labelX + (int)MathF.Ceiling(labelW) + 10;
            int y = labelY + (RowBtnH - CopyBtnH) / 2;
            return new Rectangle(x, y, CopyBtnW, CopyBtnH);
        }

        private static void SetBoxBounds(DialogueTextInputBox box, float x, float y, float w, float h)
        {
            box.Position = new Vector2(x, y);
            box.Extent = new Vector2(w, h);
            box.InvalidateLayout();
        }

        // ── 社交关系列表与精准过滤搜索 ────────────────────────────────────────
        private void RecalculateTab4List()
        {
            _relVisibleItemRects.Clear();
            int listTop = _relLeftColRect.Y + 68;
            int listAvailH = _relLeftColRect.Height - 68 - 46;
            const int rowH = 40; // 与 RulesTabView 一致采用 40px 标准项高
            int maxVisible = Math.Max(1, listAvailH / rowH);

            _relListScrollOffset = Math.Clamp(_relListScrollOffset, 0, Math.Max(0, _vm.FilteredNpcs.Count - maxVisible));

            bool hasScroll = _vm.FilteredNpcs.Count > maxVisible;
            int itemRightPad = hasScroll ? 15 : 6;
            int itemW = _relLeftColRect.Width - 12 - (hasScroll ? 9 : 0);

            for (int i = 0; i < maxVisible && _relListScrollOffset + i < _vm.FilteredNpcs.Count; i++)
            {
                int idx = _relListScrollOffset + i;
                var r = new Rectangle(_relLeftColRect.X + 6, listTop + i * rowH, itemW, rowH - 4);
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
                _vm.SetBiography(_biographyBox.Text);

                // ★ 用户打字或粘贴超过 20 字符时，实时回退截断，既安全又无需依赖底层 textLimit
                if (_uniqueBox.Text != null && _uniqueBox.Text.Length > 20)
                {
                    _uniqueBox.Text = _uniqueBox.Text.Substring(0, 20);
                }

                _vm.SetUnique(_uniqueBox.Text);
                _vm.SetHomeLocationBed(_homeBedCheckbox.isChecked);
            }
            else if (_activeTab == 1)
            {
                _behaviorBox.Update(time);
                _dialogueExamplesBox.Update(time);
                _vm.SyncTraitDescription("BehavioralRules", "Tone & Mannerisms Constraints", _behaviorBox.Text ?? string.Empty);
                _vm.SyncTraitDescription("DialogueExamples", "Dialogue Examples", _dialogueExamplesBox.Text ?? string.Empty);
            }
            else if (_activeTab == 2)
            {
                _stageTextBox.Update(time);
                _stageBarkBox.Update(time);
                _vm.SyncStageEditor(_stageTextBox.Text, _stageBarkBox.Text);
            }
            else if (_activeTab == 3)
            {
                _vm.SyncRelationshipEditor(_vm.SelectedRelationshipNpc, _relHeadingBox.Text ?? string.Empty, _relDescBox.Text ?? string.Empty);
            }
            else if (_activeTab == 4)
            {
                _voiceBox.Update(time);
                _habitsBox.Update(time);
                _lensesBox.Update(time);
                _vm.SyncAmbientBarks(_voiceBox.Text ?? string.Empty, _habitsBox.Text ?? string.Empty, _lensesBox.Text ?? string.Empty);
                _vm.SetEnableAmbientBarks(_enableBarkCheckbox.isChecked);
            }
        }

        // ── 交互输入分发 ──────────────────────────────────────────────────
        public override void leftClickHeld(int x, int y)
        {
            base.leftClickHeld(x, y);

            if (_activeTab == 3 && _isDraggingTab4Scrollbar)
            {
                int listAvailHeight = _relLeftColRect.Height - 68 - 46;
                int visibleItemCount = Math.Max(1, listAvailHeight / 40);
                int trackX = _relLeftColRect.Right - 9;
                int trackY = _relLeftColRect.Y + 68;
                int trackH = visibleItemCount * 40 - 4;
                var trackRect = new Rectangle(trackX, trackY, 5, trackH);

                int maxScroll = Math.Max(0, _vm.FilteredNpcs.Count - visibleItemCount);
                float visibleRatio = Math.Clamp((float)visibleItemCount / _vm.FilteredNpcs.Count, 0.15f, 1f);
                int thumbH = Math.Max(24, (int)(trackH * visibleRatio));

                UpdateTab4ScrollFromMouse(y, trackRect, thumbH, maxScroll);
            }
        }

        public override void releaseLeftClick(int x, int y)
        {
            base.releaseLeftClick(x, y);
            _isDraggingTab4Scrollbar = false;
        }

        private void UpdateTab4ScrollFromMouse(int mouseY, Rectangle trackRect, int thumbH, int maxScroll)
        {
            if (maxScroll <= 0 || trackRect.Height <= thumbH) return;
            float progress = Math.Clamp((float)(mouseY - trackRect.Y - thumbH / 2) / (trackRect.Height - thumbH), 0f, 1f);
            int newOffset = (int)Math.Round(progress * maxScroll);
            if (newOffset != _relListScrollOffset)
            {
                _relListScrollOffset = newOffset;
                RecalculateTab4List();
            }
        }

        public override void receiveLeftClick(int x, int y, bool playSound = true)
        {
            if (_closeButton.containsPoint(x, y))
            {
                Game1.playSound("bigDeSelect");
                TryCancel();
                return;
            }

            for (int i = 0; i < _tabRects.Length; i++)
            {
                if (_tabRects[i].Contains(x, y))
                {
                    SwitchTab(i);
                    return;
                }
            }

            if (_saveRect.Contains(x, y)) { SaveAndClose(); return; }
            if (_cancelRect.Contains(x, y)) { TryCancel(); return; }
            if (_resetPageRect.Contains(x, y)) { TryResetCurrentPage(); return; }
            if (_resetAllRect.Contains(x, y)) { TryResetAll(); return; }

            // 新按钮：作用域胶囊 / 导入 / 导出。文本框持有焦点时跳过，防止输入时误触。
            if (!AnyTextBoxHasFocus())
            {
                if (_scopeCapsuleRect.Contains(x, y)) { ToggleTargetScope(); return; }
                if (_importRect.Contains(x, y)) { ImportFromClipboard(); return; }
                if (_exportRect.Contains(x, y)) { ExportBio(); return; }
            }

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
            if (!AnyTextBoxHasFocus() && _copyBiographyRect.Contains(x, y)) { CopyBoxToClipboard("biography"); return; }
            UnfocusAll();
        }

        private void HandleTab2Click(int x, int y)
        {
            if (ContainsPoint(_behaviorBox, x, y)) { FocusDialogueBox(_behaviorBox, x, y); return; }
            if (ContainsPoint(_dialogueExamplesBox, x, y)) { FocusDialogueBox(_dialogueExamplesBox, x, y); return; }

            if (_behaviorScaffoldRect.Contains(x, y))
            {
                string scaffold = "[VOICE]\n- Tone: \n- Cadence: \n\n[SPEECH PATTERNS]\n- \n\n[MANNERISMS]\n- \n\n" +
                    "[IMMEDIATE REFLEXES]\n- \n\n[CONTEXT OVERRIDE]\n- ";
                _vm.SyncTraitDescription("BehavioralRules", "Tone & Mannerisms Constraints", scaffold);
                _behaviorBox.SetText(scaffold);
                Game1.playSound("coin");
                return;
            }

            if (_insertBreakRect.Contains(x, y))
            {
                string next = BioEditorViewModel.ApplyDialogueBreakInsert(_dialogueExamplesBox.Text);
                _dialogueExamplesBox.SetText(next);
                _vm.SyncTraitDescription("DialogueExamples", "Dialogue Examples", next);
                Game1.playSound("shiny4");
                return;
            }
            if (_insertChoiceRect.Contains(x, y))
            {
                string next = BioEditorViewModel.ApplyDialogueChoiceInsert(_dialogueExamplesBox.Text);
                _dialogueExamplesBox.SetText(next);
                _vm.SyncTraitDescription("DialogueExamples", "Dialogue Examples", next);
                Game1.playSound("shiny4");
                return;
            }

            if (!AnyTextBoxHasFocus() && _copyBehaviorRect.Contains(x, y)) { CopyBoxToClipboard("behavior"); return; }
            if (!AnyTextBoxHasFocus() && _copyDialogueExamplesRect.Contains(x, y)) { CopyBoxToClipboard("dialogueExamples"); return; }

            UnfocusAll();
        }

        private void HandleTab3Click(int x, int y)
        {
            int visibleStages = Math.Min(_vm.Bio.ProgressStates.Count, 8);
            for (int i = 0; i < visibleStages; i++)
            {
                if (_stageRowRects[i].Contains(x, y))
                {
                    SelectStage(i);
                    Game1.playSound("smallSelect");
                    return;
                }
            }

            if (_vm.CanAddStage && _newStageRect.Contains(x, y))
            {
                _vm.AddStage();
                SelectStage(_vm.SelectedStageIndex);
                Game1.playSound("newRecipe");
                Layout();
                return;
            }

            if (_vm.SelectedStageIndex < 0 || _vm.SelectedStageIndex >= _vm.Bio.ProgressStates.Count) return;

            if (_heartsStepper.ReceiveLeftClick(x, y)) return;

            if (_gateMarriedPillRect.Contains(x, y))
            {
                _vm.CycleRequireMarried();
                Game1.playSound("drumkit6");
                return;
            }

            if (_gateJojaClosedPillRect.Contains(x, y))
            {
                _vm.CycleJojaMartClosed();
                Game1.playSound("drumkit6");
                return;
            }

            if (_gateJojaMemberPillRect.Contains(x, y))
            {
                _vm.CycleJojaMember();
                Game1.playSound("drumkit6");
                return;
            }

            if (_deleteStageRect.Contains(x, y))
            {
                Game1.activeClickableMenu = new ConfirmationDialog(
                    $"确定删除好感档位 {_vm.SelectedStageIndex + 1}？",
                    _ =>
                    {
                        Game1.activeClickableMenu = this;
                        _vm.DeleteSelectedStage();
                        if (_vm.SelectedStageIndex >= 0) SelectStage(_vm.SelectedStageIndex);
                        Layout();
                    },
                    _ => Game1.activeClickableMenu = this);
                return;
            }

            if (_stageTagEditor.ReceiveLeftClick(x, y)) return;
            if (ContainsPoint(_stageTextBox, x, y)) { FocusDialogueBox(_stageTextBox, x, y); return; }
            if (ContainsPoint(_stageBarkBox, x, y)) { FocusDialogueBox(_stageBarkBox, x, y); return; }

            if (!AnyTextBoxHasFocus() && _copyStageTextRect.Contains(x, y)) { CopyBoxToClipboard("stageText"); return; }
            if (!AnyTextBoxHasFocus() && _copyStageBarkRect.Contains(x, y)) { CopyBoxToClipboard("stageBark"); return; }

            UnfocusAll();
        }

        private void HandleTab4Click(int x, int y)
        {
            // 1. 滚动条点击/开始拖拽检测
            int listAvailHeight = _relLeftColRect.Height - 68 - 46;
            int visibleItemCount = Math.Max(1, listAvailHeight / 40);
            if (_vm.FilteredNpcs.Count > visibleItemCount)
            {
                int trackX = _relLeftColRect.Right - 9;
                int trackY = _relLeftColRect.Y + 68;
                int trackH = visibleItemCount * 40 - 4;
                var trackRect = new Rectangle(trackX, trackY, 5, trackH);

                if (trackRect.Contains(x, y))
                {
                    _isDraggingTab4Scrollbar = true;
                    int maxScroll = _vm.FilteredNpcs.Count - visibleItemCount;
                    float visibleRatio = Math.Clamp((float)visibleItemCount / _vm.FilteredNpcs.Count, 0.15f, 1f);
                    int thumbH = Math.Max(24, (int)(trackH * visibleRatio));
                    UpdateTab4ScrollFromMouse(y, trackRect, thumbH, maxScroll);
                    return;
                }
            }

            if (new Rectangle(_relSearchBox.X, _relSearchBox.Y, _relSearchBox.Width, _relSearchBox.Height).Contains(x, y))
            {
                FocusTextBox(_relSearchBox);
                return;
            }

            foreach (var (rect, idx) in _relVisibleItemRects)
            {
                if (rect.Contains(x, y))
                {
                    _vm.SelectRelationship(idx);
                    SelectRelationshipView(_vm.SelectedRelationshipNpc);
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

            if (_relAddRect.Contains(x, y) && !string.IsNullOrEmpty(_vm.SelectedRelationshipNpc))
            {
                _vm.EnsureRelationship(_vm.SelectedRelationshipNpc);
                _vm.RecomputeFilteredNpcs(_relSearchBox.Text);
                RecalculateTab4List();
                Game1.playSound("coin");
                return;
            }
            if (_relDelRect.Contains(x, y) && !string.IsNullOrEmpty(_vm.SelectedRelationshipNpc))
            {
                string target = _vm.SelectedRelationshipNpc;
                if (string.Equals(target, "ThePlayer", StringComparison.OrdinalIgnoreCase))
                {
                    Game1.playSound("cancel");
                    Game1.addHUDMessage(new HUDMessage("玩家条目 (ThePlayer) 为核心设定，禁止删除", HUDMessage.error_type));
                    return;
                }
                Game1.activeClickableMenu = new ConfirmationDialog(
                    $"删除 {_npcName} → {target} 的独立人设关系？",
                    _ =>
                    {
                        Game1.activeClickableMenu = this;
                        _vm.RemoveRelationship(target);
                        _vm.RecomputeFilteredNpcs(_relSearchBox.Text);
                        RecalculateTab4List();
                        _vm.SelectRelationship(_vm.SelectedRelationshipIndex);
                        SelectRelationshipView(_vm.SelectedRelationshipNpc);
                    },
                    _ => Game1.activeClickableMenu = this);
                return;
            }

            if (!AnyTextBoxHasFocus() && _copyRelDescRect.Contains(x, y)) { CopyBoxToClipboard("relDesc"); return; }

            UnfocusAll();
        }

        private void HandleTab5Click(int x, int y)
        {
            if (_enableBarkCheckbox.bounds.Contains(x, y)) { _enableBarkCheckbox.receiveLeftClick(x, y); return; }
            if (_scrapeRect.Contains(x, y)) { ScrapeExamplesFromVm(); return; }
            if (_globalTagEditor.ReceiveLeftClick(x, y)) return;
            if (ContainsPoint(_voiceBox, x, y)) { FocusDialogueBox(_voiceBox, x, y); return; }
            if (ContainsPoint(_habitsBox, x, y)) { FocusDialogueBox(_habitsBox, x, y); return; }
            if (ContainsPoint(_lensesBox, x, y)) { FocusDialogueBox(_lensesBox, x, y); return; }

            if (!AnyTextBoxHasFocus() && _copyVoiceRect.Contains(x, y)) { CopyBoxToClipboard("voice"); return; }
            if (!AnyTextBoxHasFocus() && _copyHabitsRect.Contains(x, y)) { CopyBoxToClipboard("habits"); return; }
            if (!AnyTextBoxHasFocus() && _copyLensesRect.Contains(x, y)) { CopyBoxToClipboard("lenses"); return; }

            UnfocusAll();
        }

        public override void receiveKeyPress(Keys key)
        {
            // 1. Tag 编辑器正在输入时优先处理
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

            DialogueTextInputBox? activeBox = GetActiveDialogueBox();

            bool isAnyTextFocused = Game1.keyboardDispatcher.Subscriber != null
                                    || activeBox != null
                                    || (_activeTab == 0 && _uniqueBox.Selected)
                                    || (_activeTab == 3 && (_relSearchBox.Selected || _relHeadingBox.Selected));

            // 2. 文本框/输入控件处于激活输入状态
            if (isAnyTextFocused)
            {
                if (key == Keys.Escape)
                {
                    UnfocusAll();
                    Game1.playSound("bigDeSelect");
                    return;
                }

                if (activeBox != null && !DialogueTextInputBox.IsControlKeyDown())
                {
                    if (key == Keys.Left || key == Keys.Right || key == Keys.Home ||
                        key == Keys.End || key == Keys.Delete || key == Keys.Back)
                    {
                        activeBox.RecieveSpecialInput(key);
                        return;
                    }
                }

                if (_activeTab == 3 && _relSearchBox.Selected)
                {
                    base.receiveKeyPress(key);
                    _vm.RecomputeFilteredNpcs(_relSearchBox.Text);
                    RecalculateTab4List();
                    return;
                }

                base.receiveKeyPress(key);
                return;
            }

            // 3. 全局快捷键：保存 (Ctrl+S)
            if (key == Keys.S && (Keyboard.GetState().IsKeyDown(Keys.LeftControl) || Keyboard.GetState().IsKeyDown(Keys.RightControl)))
            {
                SaveAndClose();
                return;
            }

            // 4. 未聚焦任何输入框时，按 ESC 直接退出 / 提示保存取消
            if (key == Keys.Escape)
            {
                TryCancel();
                return;
            }

            // 5. 阻止菜单键（如 'E' 键）意外关闭本编辑器；若有其他按键透传给 base
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
                    int listAvailH = _relLeftColRect.Height - 68 - 46;
                    int maxVisible = Math.Max(1, listAvailH / 40);
                    int maxScroll = Math.Max(0, _vm.FilteredNpcs.Count - maxVisible);

                    _relListScrollOffset = Math.Clamp(_relListScrollOffset - (direction > 0 ? 1 : -1), 0, maxScroll);
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

            // 1. 外层木框
            IClickableMenu.drawTextureBox(b, xPositionOnScreen - 8, yPositionOnScreen - 8, width + 16, height + 16, Color.White);

            // 2. 内部羊皮纸平铺底板（先铺纯色防边缘漏黑）
            b.Draw(Game1.staminaRect, new Rectangle(xPositionOnScreen, yPositionOnScreen, width, height), new Color(245, 230, 205));
            b.Draw(
                Game1.menuTexture,
                new Rectangle(xPositionOnScreen, yPositionOnScreen, width, height),
                new Rectangle(64, 128, 64, 64),
                new Color(245, 230, 205)
            );

            DrawHeader(b, mx, my);

            for (int i = 0; i < _tabRects.Length; i++)
                DrawTabButton(b, _tabRects[i], TabTitles[i], _activeTab == i, mx, my);

            int sepY = yPositionOnScreen + HeaderH + TabBarH + 6;
            b.Draw(Game1.staminaRect, new Rectangle(xPositionOnScreen + ContentPadding, sepY, width - ContentPadding * 2, 2), Color.Gray * 0.35f);

            if (_activeTab == 0) DrawTab1(b, mx, my);
            else if (_activeTab == 1) DrawTab2(b, mx, my);
            else if (_activeTab == 2) DrawTab3(b, mx, my);
            else if (_activeTab == 3) DrawTab4(b, mx, my);
            else if (_activeTab == 4) DrawTab5(b, mx, my);

            // 底部操作按钮（顺序：取消 -> 保存 -> 当前页恢复原版 -> 全部恢复原版）
            DrawActionButton(b, _cancelRect, "返回 / 取消 (Esc)", mx, my, isDanger: false);
            DrawActionButton(b, _saveRect, "✔ 保存修改 (Ctrl+S)", mx, my, isPrimary: true);
            DrawActionButton(b, _resetPageRect, "当前页恢复原版", mx, my, isDanger: false);
            DrawActionButton(b, _resetAllRect, "全部恢复原版", mx, my, isDanger: true, isEnabled: _vm.HasOverlay || _vm.IsDirty);

            // ★ 关闭按钮平滑悬停动效（参考 IntegratedHubMenu）
            UiHelper.UpdateButtonScale(ref _closeButtonHoverScale, _closeButton, mx, my);
            _closeButton.scale = CloseButtonBaseScale * _closeButtonHoverScale;
            _closeButton.draw(b);

            if (!string.IsNullOrEmpty(_hoverText))
                DrawHoverTextCustom(b, _hoverText);

            drawMouse(b);
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
                x + 4, y + 4, boxW, boxH, Color.Black * 0.28f, 0.65f, false);

            IClickableMenu.drawTextureBox(b, Game1.menuTexture, new Rectangle(0, 256, 60, 60),
                x, y, boxW, boxH, new Color(255, 255, 250), 0.65f, false);

            float textY = y + (boxH - sz.Y) / 2f - 1;
            CustomFontManager.DrawString(b, text,
                new Vector2(x + padX, textY),
                TextPrimary, TipFontSize);
        }

        private void DrawHeader(SpriteBatch b, int mx, int my)
        {
            int headX = xPositionOnScreen + ContentPadding;
            int headY = yPositionOnScreen + 14;

            const int pSize = 44;
            var portraitRect = new Rectangle(headX, headY, pSize, pSize);

            b.Draw(Game1.staminaRect, new Rectangle(portraitRect.X - 1, portraitRect.Y - 1, portraitRect.Width + 2, portraitRect.Height + 2), new Color(225, 210, 185));
            IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(403, 383, 6, 6),
                portraitRect.X - 2, portraitRect.Y - 2, portraitRect.Width + 4, portraitRect.Height + 4,
                new Color(200, 175, 140), 2f, false);

            if (_npcPortrait != null && !_portraitSmileRect.IsEmpty)
                b.Draw(_npcPortrait, portraitRect, _portraitSmileRect, Color.White);
            else
            {
                string avatarFallback = string.IsNullOrEmpty(_npcName) ? "?" : _npcName.Substring(0, 1);
                CustomFontManager.DrawStringBold(b, avatarFallback, new Vector2(portraitRect.X + 14, portraitRect.Y + 6), TextMuted, TitleFontSize);
            }

            string disp = Game1.getCharacterFromName(_npcName)?.displayName ?? _npcName;
            CustomFontManager.DrawStringBold(b, $"{disp} ({_npcName}) · 人设工作台", new Vector2(headX + pSize + 12, headY + 2), TextPrimary, TitleFontSize);

            string status = _vm.IsDirty ? "● 存在未保存改动" : (_vm.HasOverlay ? "★ 自定义覆盖生效中" : "默认人设基准");
            Color statusCol = _vm.IsDirty ? TextWarning : (_vm.HasOverlay ? TextSuccess : TextMuted);
            CustomFontManager.DrawString(b, status, new Vector2(headX + pSize + 14, headY + 30), statusCol, TipFontSize);

            string scopeLabel = _vm.TargetScope == BioStorageService.BioScope.Local ? "本存档独占" : "全局生效";
            DrawActionButton(b, _scopeCapsuleRect, scopeLabel, mx, my, isPrimary: true);
            DrawActionButton(b, _importRect, "导入", mx, my, isPrimary: false);
            DrawActionButton(b, _exportRect, "导出", mx, my, isPrimary: false);
        }

        // ── 各 Tab 具体渲染 ───────────────────────────────────────────────
        private void DrawTab1(SpriteBatch b, int mx, int my)
        {
            CustomFontManager.DrawString(b, "身份设定与心理矛盾（保留 [IDENTITY] 与 [PSYCHOLOGICAL CONFLICTS] 分节符）",
                new Vector2(_biographyBox.Position.X, _biographyBox.Position.Y - RowBtnH - LabelRowGap), TextSecondary, SectionHeaderSize);

            DrawActionButton(b, _scaffoldBtnRect, "插入身份模板", mx, my, false);
            DrawActionButton(b, _copyBiographyRect, "复制全部", mx, my, false);
            DrawStyledDialogueBox(b, _biographyBox);

            DrawCard(b, _uniqueCardRect);
            CustomFontManager.DrawString(b, "特殊行为/身份标记 (Unique)",
                new Vector2(_uniqueBox.X, _uniqueCardRect.Y + 6), TextSecondary, SectionHeaderSize);
            DrawSingleLineBox(b, _uniqueBox);
            if (_uniqueBox.X <= mx && mx <= _uniqueBox.X + _uniqueBox.Width && _uniqueBox.Y <= my && my <= _uniqueBox.Y + _uniqueBox.Height)
                _hoverText = "用于限定 NPC 的特殊行为或状态（如 'behind the counter', 'holding a football'）。";

            DrawCard(b, _homeBedCardRect);
            CustomFontManager.DrawString(b, "就寝行为偏好",
                new Vector2(_homeBedCardRect.X + 16, _homeBedCardRect.Y + 6), TextSecondary, SectionHeaderSize);
            _homeBedCheckbox.draw(b, 0, 0, this);
            if (_homeBedCheckbox.bounds.Contains(mx, my))
                _hoverText = "勾选后，NPC 在深夜对话时会偏向使用专属卧房就寝语境。";
        }

        private void DrawTab2(SpriteBatch b, int mx, int my)
        {
            CustomFontManager.DrawString(b, "行为规则 (BehavioralRules)",
                new Vector2(_behaviorBox.Position.X, _behaviorBox.Position.Y - RowBtnH - LabelRowGap), TextSecondary, SectionHeaderSize);
            DrawActionButton(b, _behaviorScaffoldRect, "插入规则模板", mx, my, false);
            DrawActionButton(b, _copyBehaviorRect, "复制全部", mx, my, false);
            DrawStyledDialogueBox(b, _behaviorBox);

            CustomFontManager.DrawString(b, "对白范例 (Dialogue)",
                new Vector2(_dialogueExamplesBox.Position.X, _dialogueExamplesBox.Position.Y - RowBtnH - LabelRowGap), TextSecondary, SectionHeaderSize);
            DrawActionButton(b, _insertBreakRect, "+ 分段符", mx, my, false);
            DrawActionButton(b, _insertChoiceRect, "+ 玩家选项", mx, my, false);
            DrawActionButton(b, _copyDialogueExamplesRect, "复制全部", mx, my, false);

            if (_insertBreakRect.Contains(mx, my)) _hoverText = "插入 #$b#：在原版对话框中翻页。";
            if (_insertChoiceRect.Contains(mx, my)) _hoverText = "插入 % 选项：提供玩家可点击的分支回答。";

            DrawStyledDialogueBox(b, _dialogueExamplesBox);

            CustomFontManager.DrawString(b, "提示：支持原版表情符 ($0 / $s) 与换行分段；选项以 % 开头。",
                new Vector2(_dialogueExamplesBox.Position.X, _dialogueExamplesBox.Position.Y + _dialogueExamplesBox.Extent.Y + 6), TextMuted, TipFontSize);
        }

        private void DrawTab3(SpriteBatch b, int mx, int my)
        {
            DrawCard(b, _stageLeftColRect);
            CustomFontManager.DrawString(b, $"好感演变档位 ({_vm.Bio.ProgressStates.Count}/8)",
                new Vector2(_stageLeftColRect.X + 12, _stageLeftColRect.Y + 8), TextSecondary, SectionHeaderSize);

            int visibleStages = Math.Min(_vm.Bio.ProgressStates.Count, 8);
            for (int i = 0; i < visibleStages; i++)
            {
                var r = _stageRowRects[i];
                bool isSel = (i == _vm.SelectedStageIndex);
                bool isHover = r.Contains(mx, my);

                // 选中态使用沉稳深棕底色，以便衬托纯白文字
                Color bg = isSel ? new Color(150, 105, 60) : (isHover ? new Color(255, 235, 205) : Color.White);
                Color borderCol = isSel ? new Color(110, 70, 35) : Color.Wheat;

                b.Draw(Game1.staminaRect, new Rectangle(r.X + 1, r.Y + 1, r.Width - 2, r.Height - 2), bg);
                IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(432, 439, 9, 9), r.X, r.Y, r.Width, r.Height, borderCol, 2f, false);

                string gateSummary = _vm.BuildGateSummary(i);
                // 选中时文字使用白色高亮，未选中使用深色主文字
                CustomFontManager.DrawString(b, $"档位 {i + 1}  [{gateSummary}]", new Vector2(r.X + 12, r.Y + 8), isSel ? Color.White : TextPrimary, ContentFontSize);
            }

            if (_vm.CanAddStage)
                DrawActionButton(b, _newStageRect, "+ 新建好感档位", mx, my, false);

            if (_vm.SelectedStageIndex < 0 || _vm.SelectedStageIndex >= _vm.Bio.ProgressStates.Count)
            {
                CustomFontManager.DrawString(b, "从左侧列表选择或添加一个好感档位开始编辑。",
                    new Vector2(_stageRightColRect.X + 20, _stageRightColRect.Y + 40), TextMuted, ContentFontSize);
                return;
            }

            var stage = _vm.Bio.ProgressStates[_vm.SelectedStageIndex];

            CustomFontManager.DrawString(b, "激活门禁:", new Vector2(_stageRightColRect.X, _stageRightColRect.Y + 6), TextSecondary, SectionHeaderSize);
            _heartsStepper.Draw(b);

            DrawPillButton(b, _gateMarriedPillRect, stage.RequireMarried ? "已婚" : "不限婚姻", stage.RequireMarried, mx, my);
            if (_gateMarriedPillRect.Contains(mx, my))
            {
                _hoverText = stage.RequireMarried
                    ? "【激活条件：必须与该 NPC 结婚】\n仅当玩家与当前角色处于已婚状态时，本档位人设才会激活生效。"
                    : "【激活条件：不限婚姻】\n无论玩家单身、与该角色结婚还是与其他人结婚，均可进入本档位。";
            }

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

            // ★ Tab3 复制按钮已移至标签右侧
            CustomFontManager.DrawString(b, "阶段态度演变 (Text)",
                new Vector2(_stageTextBox.Position.X, _stageTextBox.Position.Y - RowBtnH - LabelRowGap), TextSecondary, SectionHeaderSize);
            DrawActionButton(b, _copyStageTextRect, "复制全部", mx, my, false);
            DrawStyledDialogueBox(b, _stageTextBox);

            CustomFontManager.DrawString(b, "碎碎念心智 (BarkMindset: 规定此时的心态与注意力)",
                new Vector2(_stageBarkBox.Position.X, _stageBarkBox.Position.Y - RowBtnH - LabelRowGap), TextSecondary, SectionHeaderSize);
            DrawActionButton(b, _copyStageBarkRect, "复制全部", mx, my, false);
            DrawStyledDialogueBox(b, _stageBarkBox);

            CustomFontManager.DrawString(b, "阶段专属关注池 (Preoccupations: 优先提及的事物)",
                new Vector2(_stageRightColRect.X, _stageTagEditor.Bounds.Y - RowBtnH - LabelRowGap), TextSecondary, SectionHeaderSize);
            _stageTagEditor.Draw(b);
        }

        private void DrawTab4(SpriteBatch b, int mx, int my)
        {
            DrawCard(b, _relLeftColRect);
            CustomFontManager.DrawString(b, "目标角色列表 (★已定制)",
                new Vector2(_relSearchBox.X, _relLeftColRect.Y + 8), TextSecondary, SectionHeaderSize);
            DrawSingleLineBox(b, _relSearchBox);
            if (string.IsNullOrEmpty(_relSearchBox.Text))
                CustomFontManager.DrawString(b, "搜索角色...", new Vector2(_relSearchBox.X + 8, _relSearchBox.Y + 6), TextMuted, ContentFontSize);

            // ── 绘制左侧 NPC 角色列表（复刻 RulesTabView 标准） ──
            bool isMouseDown = IsLeftMouseDown();

            foreach (var (itemRect, idx) in _relVisibleItemRects)
            {
                var name = _vm.FilteredNpcs[idx];
                bool isSel = (idx == _vm.SelectedRelationshipIndex);
                bool isHover = itemRect.Contains(mx, my);
                bool isItemPressed = isHover && isMouseDown;
                bool hasConfig = _vm.Bio.Relationships.ContainsKey(name);
                int pressOffset = isItemPressed ? 1 : 0;

                // 1. 底层微阴影
                if (!isItemPressed)
                {
                    b.Draw(Game1.staminaRect,
                        new Rectangle(itemRect.X + 1, itemRect.Y + 2, itemRect.Width, itemRect.Height),
                        RulesTheme.Shadow);
                }

                var drawRect = new Rectangle(itemRect.X, itemRect.Y + pressOffset, itemRect.Width, itemRect.Height);

                Color bg = isSel ? RulesTheme.SurfaceActive
                         : isItemPressed ? RulesTheme.SurfaceSunken
                         : isHover ? RulesTheme.SurfaceHover
                         : RulesTheme.SurfaceCard;

                Color borderCol = isSel ? RulesTheme.BorderBold
                                : isItemPressed ? RulesTheme.BorderBold
                                : isHover ? RulesTheme.BorderMid
                                : RulesTheme.BorderSoft;

                // 2. 卡片底衬与 2f 九宫格外框
                b.Draw(Game1.staminaRect, new Rectangle(drawRect.X + 1, drawRect.Y + 1, drawRect.Width - 2, drawRect.Height - 2), bg);
                IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(432, 439, 9, 9),
                    drawRect.X, drawRect.Y, drawRect.Width, drawRect.Height, borderCol, 2f, false);

                // 3. 选中项左侧金色高亮条
                if (isSel)
                {
                    b.Draw(Game1.staminaRect,
                        new Rectangle(drawRect.X + 2, drawRect.Y + 3, 4, drawRect.Height - 6),
                        RulesTheme.AccentGold);
                }

                // 4. 微型头像框 + 行走图切片
                int avatarSize = 28;
                int avatarX = drawRect.X + (isSel ? 8 : 6);
                var avatarRect = new Rectangle(avatarX, drawRect.Y + (drawRect.Height - avatarSize) / 2, avatarSize, avatarSize);

                b.Draw(Game1.staminaRect, avatarRect, RulesTheme.SurfaceSunken);
                IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(403, 383, 6, 6),
                    avatarRect.X - 1, avatarRect.Y - 1, avatarRect.Width + 2, avatarRect.Height + 2,
                    isSel ? RulesTheme.BorderBold : (isHover ? RulesTheme.BorderMid : RulesTheme.BorderSoft), 1.2f, false);

                var (headSprite, srcRect) = GetNpcWalkingHeadSprite(name);
                string disp = Game1.getCharacterFromName(name)?.displayName ?? name;

                if (headSprite is { IsDisposed: false } headTex && !srcRect.IsEmpty)
                {
                    b.Draw(headTex, avatarRect, srcRect, Color.White);
                }
                else
                {
                    string initial = string.IsNullOrEmpty(disp) ? "?" : disp.Substring(0, 1);
                    var initSz = CustomFontManager.MeasureString(initial, CustomFontManager.SizeSmall);
                    CustomFontManager.DrawString(b, initial,
                        new Vector2(avatarRect.X + (avatarSize - initSz.X) / 2f, avatarRect.Y + (avatarSize - initSz.Y) / 2f - 1),
                        RulesTheme.TextSecondary, CustomFontManager.SizeSmall);
                }
                
                // 5. 角色名称文本（选中项高亮为纯白色，其余悬停/常规使用深色）
                int textLeft = avatarRect.Right + 8;
                Color nameCol = isSel ? Color.White
                    : (isHover ? RulesTheme.TextCharcoal : RulesTheme.TextDarkBrown);

                CustomFontManager.DrawString(b, disp,
                    new Vector2(textLeft, drawRect.Y + (drawRect.Height - 20) / 2f),
                    nameCol, ContentFontSize);

                // 6. 右侧定制状态胶囊徽记 (★ 定制：永远常亮保持激活质感)
                if (hasConfig)
                {
                    string badgeText = "★";
                    var badgeSz = CustomFontManager.MeasureString(badgeText, CustomFontManager.SizeSmall);
                    int badgeW = 20;
                    var badgeRect = new Rectangle(drawRect.Right - badgeW - 6, drawRect.Y + (drawRect.Height - 18) / 2, badgeW, 18);

                    // 1. 恒定明艳的星露谷金橙底色（彻底抛弃发灰的 403 凹槽，用饱满纯净金底）
                    Color badgeBg = isSel ? new Color(245, 155, 20) : new Color(255, 182, 35);
                    Color badgeBorder = isSel ? RulesTheme.BorderBold : new Color(195, 120, 20);

                    // 纯色垫底 + 432 清晰木金外边框（绝不发脏发灰）
                    b.Draw(Game1.staminaRect, badgeRect, badgeBg);
                    IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(432, 439, 9, 9),
                        badgeRect.X, badgeRect.Y, badgeRect.Width, badgeRect.Height,
                        badgeBorder, 1.2f, false);

                    // 2. 星星采用纯白高亮字（金底 + 白星 = 游戏内金星/高阶品质奖章的标准质感，任何背景下都极度醒目）
                    CustomFontManager.DrawStringBold(b, badgeText,
                        new Vector2(badgeRect.X + (badgeRect.Width - badgeSz.X) / 2f, badgeRect.Y + (badgeRect.Height - badgeSz.Y) / 2f - 1),
                        Color.White, CustomFontManager.SizeSmall);
                }

                // 7. 选中金色勾选标记 (✔)
                if (isSel)
                {
                    string checkMark = "✔";
                    var csz = CustomFontManager.MeasureStringBold(checkMark, CustomFontManager.SizeSmall);
                    int checkX = hasConfig ? (drawRect.Right - 20 - 6 - (int)csz.X - 4) : (drawRect.Right - (int)csz.X - 8);
                    CustomFontManager.DrawStringBold(b, checkMark,
                        new Vector2(checkX, drawRect.Y + (drawRect.Height - csz.Y) / 2f),
                        RulesTheme.AccentGold, CustomFontManager.SizeSmall);
                }
            }

            // 8. 左栏轻量滚动指示条
            int listAvailHeight = _relLeftColRect.Height - 68 - 46;
            int visibleItemCount = Math.Max(1, listAvailHeight / 40);
            if (_vm.FilteredNpcs.Count > visibleItemCount)
            {
                int trackX = _relLeftColRect.Right - 9;
                int trackY = _relLeftColRect.Y + 68;
                int trackH = visibleItemCount * 40 - 4;
                var trackRect = new Rectangle(trackX, trackY, 5, trackH);

                float visibleRatio = Math.Clamp((float)visibleItemCount / _vm.FilteredNpcs.Count, 0.15f, 1f);
                int thumbH = Math.Max(24, (int)(trackH * visibleRatio));
                int maxScroll = _vm.FilteredNpcs.Count - visibleItemCount;
                int thumbY = trackY + (int)((trackH - thumbH) * ((float)_relListScrollOffset / maxScroll));

                b.Draw(Game1.staminaRect, trackRect, RulesTheme.SurfaceSunken);

                // 悬停或拖拽时高亮
                var thumbRect = new Rectangle(trackX - 1, thumbY, trackRect.Width + 2, thumbH);
                bool thumbHover = thumbRect.Contains(mx, my);
                Color thumbBg = _isDraggingTab4Scrollbar ? RulesTheme.BorderBold
                              : (thumbHover ? RulesTheme.AccentGold : RulesTheme.BorderMid);

                b.Draw(Game1.staminaRect, thumbRect, thumbBg);
                IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(403, 383, 6, 6),
                    thumbRect.X, thumbRect.Y, thumbRect.Width, thumbRect.Height, RulesTheme.BorderBold, 1f, false);
            }

            DrawActionButton(b, _relAddRect, "+ 定制关系", mx, my, false);
            DrawActionButton(b, _relDelRect, "- 清除", mx, my, isDanger: true);

            if (string.IsNullOrEmpty(_vm.SelectedRelationshipNpc))
            {
                CustomFontManager.DrawString(b, "从左侧列表选择目标角色。", new Vector2(_relRightColRect.X + 20, _relRightColRect.Y + 40), TextMuted, ContentFontSize);
                return;
            }

            string targetDisp = Game1.getCharacterFromName(_vm.SelectedRelationshipNpc)?.displayName ?? _vm.SelectedRelationshipNpc;
            CustomFontManager.DrawString(b, $"{_npcName} 对 {targetDisp} 的单向社交关系",
                new Vector2(_relHeadingBox.X, _relRightColRect.Y + 4), TextPrimary, TitleFontSize);

            CustomFontManager.DrawString(b, "关系称谓与定位 (Heading: 如 'Wife', 'Business Rival')",
                new Vector2(_relHeadingBox.X, _relHeadingBox.Y - RowBtnH - LabelRowGap), TextSecondary, SectionHeaderSize);
            DrawSingleLineBox(b, _relHeadingBox);

            CustomFontManager.DrawString(b, "深层心理与互动细节 (Description)",
                new Vector2(_relDescBox.Position.X, _relDescBox.Position.Y - RowBtnH - LabelRowGap), TextSecondary, SectionHeaderSize);
            DrawActionButton(b, _copyRelDescRect, "复制全部", mx, my, false);
            DrawStyledDialogueBox(b, _relDescBox);

            CustomFontManager.DrawString(b, "提示：如需双方互动感知，请在两人的编辑器中分别配置相互的关系定位。",
                new Vector2(_relDescBox.Position.X, _relDescBox.Position.Y + _relDescBox.Extent.Y + 6), TextMuted, TipFontSize);
        }

        private void DrawTab5(SpriteBatch b, int mx, int my)
        {
            DrawCard(b, _tab5LeftColRect);
            CustomFontManager.DrawString(b, "日常碎碎念总控",
                new Vector2(_tab5LeftColRect.X + 12, _tab5LeftColRect.Y + 8), TextSecondary, SectionHeaderSize);
            _enableBarkCheckbox.draw(b, 0, 0, this);

            DrawActionButton(b, _scrapeRect, "↺ 从原版对白智能抓取范例", mx, my, false);

            CustomFontManager.DrawString(b, "全局常态关注池 (Preoccupations)",
                new Vector2(_globalTagEditor.Bounds.X, _globalTagEditor.Bounds.Y - RowBtnH - LabelRowGap), TextSecondary, SectionHeaderSize);
            _globalTagEditor.Draw(b);

            // ★ Tab5 复制按钮已移至标签右侧
            CustomFontManager.DrawString(b, "口吻与态度 (Voice & Attitude)",
                new Vector2(_voiceBox.Position.X, _voiceBox.Position.Y - RowBtnH - LabelRowGap), TextSecondary, SectionHeaderSize);
            DrawActionButton(b, _copyVoiceRect, "复制全部", mx, my, false);
            DrawStyledDialogueBox(b, _voiceBox);
            if (ContainsPoint(_voiceBox, mx, my)) _hoverText = "限定碎碎念的基本语调、说话长短与即时情绪基调。";

            CustomFontManager.DrawString(b, "口头习惯 (Spoken Habits)",
                new Vector2(_habitsBox.Position.X, _habitsBox.Position.Y - RowBtnH - LabelRowGap), TextSecondary, SectionHeaderSize);
            DrawActionButton(b, _copyHabitsRect, "复制全部", mx, my, false);
            DrawStyledDialogueBox(b, _habitsBox);
            if (ContainsPoint(_habitsBox, mx, my)) _hoverText = "NPC 的口头禅、叹气声、常用起手式（如 'Well,', 'Sigh...'）。";

            CustomFontManager.DrawString(b, "观察透镜 (Observation Lenses)",
                new Vector2(_lensesBox.Position.X, _lensesBox.Position.Y - RowBtnH - LabelRowGap), TextSecondary, SectionHeaderSize);
            DrawActionButton(b, _copyLensesRect, "复制全部", mx, my, false);
            DrawStyledDialogueBox(b, _lensesBox);
            if (ContainsPoint(_lensesBox, mx, my)) _hoverText = "NPC 打量周围世界时的特殊视角（例如铁匠关注矿物与工具锈蚀，农夫关注作物与雨水）。";
        }

        private static void DrawStyledDialogueBox(SpriteBatch b, DialogueTextInputBox box)
        {
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

        private DialogueTextInputBox? GetActiveDialogueBox()
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
            bool isPressed = isHover && IsLeftMouseDown();

            Color bg = isActive ? new Color(210, 180, 140) : (isHover ? new Color(255, 235, 205) : new Color(139, 90, 43));

            int pressOffset = isPressed ? 1 : 0;
            if (isPressed) bg = Color.Lerp(bg, Color.Black, 0.14f);

            b.Draw(Game1.staminaRect, new Rectangle(rect.X + 2 + pressOffset, rect.Y + 2 + pressOffset, rect.Width - 4, rect.Height - 4), bg);
            IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(432, 439, 9, 9),
                rect.X + pressOffset, rect.Y + pressOffset, rect.Width, rect.Height, bg, 4f, false);

            // Tab 标签：走 Bold 18f，颜色保持原逻辑（大 tab 标题不改白字）
            var sz = CustomFontManager.MeasureStringBold(label, TabFontSize);
            CustomFontManager.DrawStringBold(b, label,
                new Vector2(rect.X + pressOffset + (rect.Width - sz.X) / 2f, rect.Y + pressOffset + (rect.Height - sz.Y) / 2f),
                isActive ? TextPrimary : (isHover ? Color.Wheat : TextOnDark), TabFontSize);
        }

        /// <summary>探测鼠标左键当前是否处于按下状态，用于按钮“下沉/弹起”的点击动效。</summary>
        private static bool IsLeftMouseDown()
        {
            try
            {
                return Game1.input.GetMouseState().LeftButton == ButtonState.Pressed;
            }
            catch
            {
                return false;
            }
        }

        private void DrawActionButton(SpriteBatch b, Rectangle rect, string label, int mx, int my,
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

            // 文字颜色与按钮底色明暗自适应
            Color btnTextCol;
            if (!isEnabled)
            {
                btnTextCol = TextMuted;
            }
            else if (isDanger)
            {
                btnTextCol = TextOnDarkBtn;
            }
            else
            {
                btnTextCol = TextOnLightBtn;
            }

            var sz = CustomFontManager.MeasureStringBold(label, ButtonFontSize);
            Vector2 textPos = new Vector2(
                rect.X + pressOffset + (rect.Width - sz.X) / 2f,
                rect.Y + pressOffset + (rect.Height - sz.Y) / 2f);

            CustomFontManager.DrawStringBold(b, label, textPos, btnTextCol, ButtonFontSize);
        }

        private static void DrawPillButton(SpriteBatch b, Rectangle rect, string label, bool isActive, int mx, int my)
        {
            bool isHover = rect.Contains(mx, my);
            bool isPressed = isHover && IsLeftMouseDown();

            Color bg = isActive ? (isHover ? Color.Gold : new Color(255, 220, 130)) : (isHover ? new Color(255, 235, 205) : Color.White);

            int pressOffset = isPressed ? 1 : 0;
            if (isPressed) bg = Color.Lerp(bg, Color.Black, 0.14f);

            b.Draw(Game1.staminaRect, new Rectangle(rect.X + 1 + pressOffset, rect.Y + 1 + pressOffset, rect.Width - 2, rect.Height - 2), bg);
            IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(432, 439, 9, 9),
                rect.X + pressOffset, rect.Y + pressOffset, rect.Width, rect.Height,
                isActive ? new Color(200, 150, 50) : Color.Wheat, 2f, false);

            var sz = CustomFontManager.MeasureString(label, ContentFontSize);
            Vector2 textPos = new Vector2(
                rect.X + pressOffset + (rect.Width - sz.X) / 2f,
                rect.Y + pressOffset + (rect.Height - sz.Y) / 2f);

            // ★ 主文字色
            CustomFontManager.DrawString(b, label, textPos, TextPrimary, ContentFontSize);
        }

        /// <summary>卡片：暖羊皮纸填充 + 星露谷式暖金棕边框（替代原灰调）。</summary>
        private static void DrawCard(SpriteBatch b, Rectangle rect)
        {
            b.Draw(Game1.staminaRect,
                new Rectangle(rect.X + 1, rect.Y + 1, rect.Width - 2, rect.Height - 2),
                new Color(242, 226, 196) * 0.75f);

            IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(403, 383, 6, 6),
                rect.X, rect.Y, rect.Width, rect.Height,
                new Color(223, 122, 4) * 0.7f, 2f, false);
        }

        private static void DrawSingleLineBox(SpriteBatch b, TextBox box) 
        {
            var boxRect = new Rectangle(box.X, box.Y, box.Width, box.Height);

            // 统一为 2f 整像素网格与 4px 压边安全边距（绝无白缝）
            const float frameScale = 2f;
            const int fillInset = 4;

            // 1. 内衬底色：未激活为淡雅微灰米色，激活为明亮羊皮纸
            Color innerBgColor = box.Selected
                ? new Color(255, 252, 245)
                : new Color(245, 240, 230);

            b.Draw(
                Game1.staminaRect,
                new Rectangle(boxRect.X + fillInset, boxRect.Y + fillInset, Math.Max(0, boxRect.Width - fillInset * 2), Math.Max(0, boxRect.Height - fillInset * 2)),
                innerBgColor);

            // 2. 边框：与大文本框 DialogueTextInputBox 保持绝对统一
            if (box.Selected)
            {
                // 激活时：纯正深红木框 rgb(85, 40, 28)
                IClickableMenu.drawTextureBox(
                    b,
                    Game1.mouseCursors,
                    new Rectangle(432, 439, 9, 9),
                    boxRect.X,
                    boxRect.Y,
                    boxRect.Width,
                    boxRect.Height,
                    Color.White,
                    frameScale,
                    false
                );
            }
            else
            {
                // 未激活时：淡雅柔和浅木 rgb(228, 212, 190)
                IClickableMenu.drawTextureBox(
                    b,
                    Game1.mouseCursors,
                    new Rectangle(432, 439, 9, 9),
                    boxRect.X,
                    boxRect.Y,
                    boxRect.Width,
                    boxRect.Height,
                    new Color(228, 212, 190),
                    frameScale,
                    false
                );
            }

            string text = box.Text ?? string.Empty;
            Vector2 textSize = CustomFontManager.MeasureString(text, ContentFontSize);
            float textX = boxRect.X + 10;
            float textY = boxRect.Y + (boxRect.Height - textSize.Y) / 2f - 1;

            if (!string.IsNullOrEmpty(text))
            {
                CustomFontManager.DrawString(b, text, new Vector2(textX, textY), TextPrimary, ContentFontSize);
            }

            // 3. 闪烁光标
            if (box.Selected)
            {
                float cx = textX + textSize.X + 1;
                int cursorH = (int)Math.Min(22, boxRect.Height - 10);
                float cursorY = boxRect.Y + (boxRect.Height - cursorH) / 2f;

                if ((int)(Game1.currentGameTime.TotalGameTime.TotalMilliseconds / 500) % 2 == 0)
                {
                    b.Draw(Game1.staminaRect, new Rectangle((int)cx, (int)cursorY, 2, cursorH), TextPrimary);
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
 
            // ★ 切到 Tab 2 时，重新从 VM 灌一次数据，防止被空框冲掉
            if (_activeTab == 1)
            {
                _behaviorBox.SetText(_vm.GetTraitDescriptionOrNull("BehavioralRules") ?? string.Empty);
                _dialogueExamplesBox.SetText(_vm.GetTraitDescriptionOrNull("DialogueExamples") ?? string.Empty);
            }

            Game1.playSound("smallSelect");
            Layout();
        }

        private void SelectStage(int idx)
        {
            if (idx < 0 || idx >= _vm.Bio.ProgressStates.Count) return;
            _vm.SelectStage(idx);
            var s = _vm.Bio.ProgressStates[idx];
            _stageTextBox.SetText(s.Text ?? string.Empty);
            _stageBarkBox.SetText(s.BarkMindset ?? string.Empty);
            _stageTagEditor.SetTags(s.Preoccupations);
            _heartsStepper.Value = s.RequiredHearts;
        }

        private void SelectRelationshipView(string npc)
        {
            if (_vm.FilteredNpcs.Count == 0) return;
            _relHeadingBox.Text = _vm.GetRelationshipHeadingOrNull(npc) ?? string.Empty;
            _relDescBox.SetText(_vm.GetRelationshipDescriptionOrNull(npc) ?? string.Empty);
        }

        private void SyncTab5BarkBoxes()
        {
            _voiceBox.SetText(_vm.GetAmbientVoice());
            _habitsBox.SetText(_vm.GetAmbientHabits());
            _lensesBox.SetText(_vm.GetAmbientLenses());
        }

        private void ScrapeExamplesFromVm()
        {
            var outcome = _vm.ScrapeDialogueExamples(out int added, out string scrapeErr);
            switch (outcome)
            {
                case BioEditorViewModel.ScrapeOutcome.NoLines:
                    Game1.addHUDMessage(new HUDMessage("未抓取到原版对白", HUDMessage.error_type));
                    return;
                case BioEditorViewModel.ScrapeOutcome.Failed:
                    Game1.addHUDMessage(new HUDMessage("抓取对白失败", HUDMessage.error_type));
                    return;
                case BioEditorViewModel.ScrapeOutcome.Success:
                default:
                    _dialogueExamplesBox.SetText(_vm.GetTraitDescriptionOrNull("DialogueExamples") ?? string.Empty);
                    if (added > 0)
                    {
                        Game1.playSound("newArtifact");
                        Game1.addHUDMessage(new HUDMessage($"已成功抓取 {added} 条原版对白至言行模块", HUDMessage.newQuest_type));
                    }
                    else
                    {
                        Game1.playSound("cancel");
                        Game1.addHUDMessage(new HUDMessage("未发现新增对白（可能已全部收录或对白池为空）", HUDMessage.error_type));
                    }
                    return;
            }
        }

        private void InsertScaffold()
        {
            string scaffold = _vm.BuildBiographyScaffold();
            _biographyBox.SetText(scaffold);
            _vm.SetBiography(scaffold);
            Game1.playSound("coin");
        }

        private void SaveAndClose()
        {
            if (!_vm.TrySave(out string err)) { Game1.addHUDMessage(new HUDMessage($"保存失败: {err}", HUDMessage.error_type)); return; }
            Game1.playSound("achievement");
            ExitAndReturn();
        }

        private void ToggleTargetScope()
        {
            BioStorageService.BioScope next = _vm.TargetScope == BioStorageService.BioScope.Local
                ? BioStorageService.BioScope.Global
                : BioStorageService.BioScope.Local;
            _vm.SetTargetScope(next);
            Game1.playSound("smallSelect");
            ModEntry.SMonitor?.Log($"[BioEditor] 作用域切换为: {next}", LogLevel.Info);
        }

        private void ImportFromClipboard()
        {
            string? clip;
            try
            {
                clip = TextCopy.ClipboardService.GetText();
            }
            catch (Exception ex)
            {
                Game1.playSound("cancel");
                Game1.addHUDMessage(new HUDMessage($"导入失败: {ex.Message}", HUDMessage.error_type));
                return;
            }

            if (_vm.TryImportBio(clip, out string err))
            {
                SyncAllControlsFromVm();
                Game1.playSound("coin");
                Game1.addHUDMessage(new HUDMessage("已从剪贴板导入人设（未保存，保存时按当前作用域落盘）", HUDMessage.newQuest_type));
            }
            else
            {
                Game1.playSound("cancel");
                Game1.addHUDMessage(new HUDMessage($"导入失败: {err}", HUDMessage.error_type));
            }
        }

        /// <summary>
        /// 导出当前人设：不再落地到文件，直接序列化为 JSON 复制到系统剪贴板，
        /// 便于在主菜单/编辑器外粘贴、备份或跨存档传递。
        /// </summary>
        private void ExportBio()
        {
            if (_vm.Bio == null)
            {
                Game1.playSound("cancel");
                Game1.addHUDMessage(new HUDMessage("导出失败: 无可导出数据", HUDMessage.error_type));
                return;
            }

            try
            {
                string json = JsonConvert.SerializeObject(_vm.Bio, Formatting.Indented);
                TextCopy.ClipboardService.SetText(json);

                Game1.playSound("coin");
                Game1.addHUDMessage(new HUDMessage(
                    $"已将 {_vm.NpcName} 的人设 JSON 复制到剪贴板，可直接粘贴备份",
                    HUDMessage.newQuest_type));
                ModEntry.SMonitor?.Log(
                    $"[BioEditor] 已复制 {_vm.NpcName} 人设 JSON 到剪贴板（{json.Length} 字符）",
                    LogLevel.Info);
            }
            catch (Exception ex)
            {
                Game1.playSound("cancel");
                Game1.addHUDMessage(new HUDMessage($"导出失败: {ex.Message}", HUDMessage.error_type));
            }
        }

        private void CopyBoxToClipboard(string boxKey)
        {
            DialogueTextInputBox? box = boxKey switch
            {
                "biography" => _biographyBox,
                "behavior" => _behaviorBox,
                "dialogueExamples" => _dialogueExamplesBox,
                "stageText" => _stageTextBox,
                "stageBark" => _stageBarkBox,
                "relDesc" => _relDescBox,
                "voice" => _voiceBox,
                "habits" => _habitsBox,
                "lenses" => _lensesBox,
                _ => null
            };
            if (box == null)
                return;

            try
            {
                TextCopy.ClipboardService.SetText(box.Text ?? string.Empty);
                Game1.playSound("coin");
                Game1.addHUDMessage(new HUDMessage("已复制全部文本至剪贴板，可在外部精修后 Ctrl+V 贴回", HUDMessage.newQuest_type));
            }
            catch (Exception ex)
            {
                Game1.playSound("cancel");
                Game1.addHUDMessage(new HUDMessage($"复制失败: {ex.Message}", HUDMessage.error_type));
            }
        }

        private bool AnyTextBoxHasFocus() =>
            _biographyBox.Selected || _uniqueBox.Selected || _behaviorBox.Selected || _dialogueExamplesBox.Selected ||
            _stageTextBox.Selected || _stageBarkBox.Selected || _relSearchBox.Selected || _relHeadingBox.Selected ||
            _relDescBox.Selected || _voiceBox.Selected || _habitsBox.Selected || _lensesBox.Selected;

        private void TryCancel()
        {
            if (!_vm.IsDirty)
            {
                ExitAndReturn();
                return;
            }
            Game1.activeClickableMenu = new ConfirmationDialog(
                "放弃未保存的所有修改？",
                _ => { Game1.activeClickableMenu = this; ExitAndReturn(); },
                _ => { Game1.activeClickableMenu = this; });
        }

        private void SyncAllControlsFromVm()
        {
            // ── Tab 1 初始数据 ──
            _biographyBox.SetText(_vm.GetBiography());
            _uniqueBox.Text = _vm.GetUnique();
            _homeBedCheckbox.isChecked = _vm.GetHomeLocationBed();

            // ★★★ 补上：Tab 2 初始数据（行为规则与对白范例） ★★★
            _behaviorBox.SetText(_vm.GetTraitDescriptionOrNull("BehavioralRules") ?? string.Empty);
            _dialogueExamplesBox.SetText(_vm.GetTraitDescriptionOrNull("DialogueExamples") ?? string.Empty);  
            
            // Tab 3
            if (_vm.Bio.ProgressStates.Count > 0)
                SelectStage(0);
            else
            {
                _stageTextBox.SetText(string.Empty);
                _stageBarkBox.SetText(string.Empty);
                _stageTagEditor.SetTags(null);
                _heartsStepper.Value = 0;
            }

            // Tab 4
            _vm.RecomputeFilteredNpcs(_relSearchBox.Text);
            RecalculateTab4List();
            if (_vm.FilteredNpcs.Count > 0)
            {
                _vm.SelectRelationship(_vm.SelectedRelationshipIndex >= 0 ? _vm.SelectedRelationshipIndex : 0);
                SelectRelationshipView(_vm.SelectedRelationshipNpc);
            }
            else
            {
                _relHeadingBox.Text = string.Empty;
                _relDescBox.SetText(string.Empty);
            }

// ── Tab 5 初始数据 ──
            _enableBarkCheckbox.isChecked = _vm.Bio.EnableAmbientBarks;
            _globalTagEditor.SetTags(_vm.Bio.Preoccupations);
            SyncTab5BarkBoxes();

            Layout();
        }

        private void TryResetCurrentPage()
        {
            string currentTabName = TabTitles[_activeTab];
            Game1.activeClickableMenu = new ConfirmationDialog(
                $"确定将【{currentTabName}】恢复为原版默认基准？\n（未点击保存前不会写入磁盘）",
                _ =>
                {
                    Game1.activeClickableMenu = this;
                    _vm.ResetTabToBaseline(_activeTab);
                    SyncActiveTabControls();
                    Game1.playSound("coin");
                    Game1.addHUDMessage(new HUDMessage($"已恢复【{currentTabName}】至原版基准", HUDMessage.newQuest_type));
                },
                _ => Game1.activeClickableMenu = this);
        }

        private void SyncActiveTabControls()
        {
            switch (_activeTab)
            {
                case 0:
                    _biographyBox.SetText(_vm.GetBiography());

                    // ★ 修改这里：同步时也强制按 20 字符对齐
                    string rawUnique = _vm.GetUnique() ?? string.Empty;
                    _uniqueBox.Text = rawUnique.Length > 20 ? rawUnique.Substring(0, 20) : rawUnique;

                    _homeBedCheckbox.isChecked = _vm.GetHomeLocationBed();
                    break;
                case 1:
                    _behaviorBox.SetText(_vm.GetTraitDescriptionOrNull("BehavioralRules") ?? string.Empty);
                    _dialogueExamplesBox.SetText(_vm.GetTraitDescriptionOrNull("DialogueExamples") ?? string.Empty);
                    break;
                case 2:
                    if (_vm.Bio.ProgressStates.Count > 0)
                        SelectStage(0);
                    else
                    {
                        _stageTextBox.SetText(string.Empty);
                        _stageBarkBox.SetText(string.Empty);
                        _stageTagEditor.SetTags(null);
                        _heartsStepper.Value = 0;
                    }
                    Layout();
                    break;
                case 3:
                    _vm.RecomputeFilteredNpcs(_relSearchBox.Text);
                    RecalculateTab4List();
                    if (_vm.FilteredNpcs.Count > 0)
                    {
                        _vm.SelectRelationship(0);
                        SelectRelationshipView(_vm.SelectedRelationshipNpc);
                    }
                    else
                    {
                        _relHeadingBox.Text = string.Empty;
                        _relDescBox.SetText(string.Empty);
                    }
                    break;
                case 4:
                    _enableBarkCheckbox.isChecked = _vm.GetEnableAmbientBarks();
                    _globalTagEditor.SetTags(_vm.Bio.Preoccupations);
                    SyncTab5BarkBoxes();
                    break;
            }
        }

        private void TryResetAll()
        {
            if (!_vm.HasOverlay && !_vm.IsDirty)
            {
                Game1.playSound("cancel");
                Game1.addHUDMessage(new HUDMessage("当前已经是原版基准，无需还原", HUDMessage.error_type));
                return;
            }

            Game1.activeClickableMenu = new ConfirmationDialog(
                $"确定将 {_npcName} 的全部人设恢复为原版，并删除自定义文件？",
                _ =>
                {
                    Game1.activeClickableMenu = this;
                    if (!_vm.TryReset(out string err))
                    {
                        Game1.addHUDMessage(new HUDMessage($"还原失败: {err}", HUDMessage.error_type));
                        return;
                    }
                    SyncAllControlsFromVm();
                    Game1.playSound("throw");
                    Game1.addHUDMessage(new HUDMessage($"已重置 {_npcName} 全部数据至原版", HUDMessage.achievement_type));
                },
                _ => Game1.activeClickableMenu = this);
        }

        private void ExitAndReturn()
        {
            if (_returnMenu != null)
            {
                if (_returnMenu is IMemoryRefreshTarget refreshTarget)
                {
                    refreshTarget.RefreshEntries();
                }
                else if (_returnMenu is IntegratedHubMenu hub)
                {
                    hub.RefreshEntries();
                }

                Game1.activeClickableMenu = _returnMenu;
            }
            else
            {
                Game1.exitActiveMenu();
            }
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