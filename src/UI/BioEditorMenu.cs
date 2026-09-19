using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewModdingAPI;
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
    private readonly IClickableMenu _returnMenu;
    private BioData _bio;
    private bool _hasOverlay;
    private bool _dirty;
    private int _activeTab; // 0..4

    // ── Tab1 控件 ─────────────────────────────────────────────────────
    private MultilineTextBox _biographyBox;
    private TextBox _uniqueBox;
    private OptionsCheckbox _homeBedCheckbox;

    // 仅当 _bio.Missing 或 Biography 为空时，Tab1 显示的"插入身份模板"按钮
    private Rectangle _scaffoldRect;

    private static readonly string BiographyScaffold =
        "[IDENTITY]\n- Identity: You are {NPC}.\n- Social Anchor: \n- Living Situation: \n\n" +
        "[PSYCHOLOGICAL CONFLICTS]\n- ";

    // ── Tab2 控件（言行举止） ──────────────────────────────────────────
    private MultilineTextBox _behaviorBox;          // Traits["BehavioralRules"].Description
    private MultilineTextBox _dialogueExamplesBox;  // Traits["DialogueExamples"].Description
    private Rectangle _behaviorScaffoldRect;

    private static readonly string BehaviorScaffold =
        "[VOICE]\n- Tone: \n- Cadence: \n\n[SPEECH PATTERNS]\n- \n\n[MANNERISMS]\n- \n\n" +
        "[IMMEDIATE REFLEXES]\n- \n\n[CONTEXT OVERRIDE]\n- ";

    // ── Tab3 控件（好感演变 / 档位列表） ──────────────────────────────
    private int _stageIdx = -1;                       // 选中档位下标（-1 = 未选）
    private MultilineTextBox _stageTextBox;           // 选中档 Text
    private MultilineTextBox _stageBarkBox;           // 选中档 BarkMindset
    private TextBox _stagePreoccBox;                  // 选中档 Preoccupations（逗号分隔单行）

    // 门禁编辑态（仅当用户触碰门禁编辑时写入；ApplyGateEditors 提交）
    private bool _gateEditMode;
    private int _gateHearts;           // 心数候选（循环 0/3/7/8）
    private bool _gateMarried;         // 已婚候选
    private string _gateSpouse = "";   // 婚配对象内部名候选（空 = unset）
    private TextBox _gateSpouseBox;    // 婚配对象内部名编辑框
    private int _gateJojaMember;       // 三态：0=不限制(null) 1=要求true 2=要求false
    private int _gateJojaClosed;       // 三态：0=不限制(null) 1=要求true 2=要求false

    private static readonly string BarkMindsetScaffold = "[STAGE: ]\n- Mindset: \n- Attention Flow: ";

    private readonly Rectangle[] _stageRowRects = new Rectangle[8]; // 档位行 + [新建档位]
    private Rectangle _newStageRect;
    private Rectangle _gateToggleRect;   // [编辑门禁] 切换钮
    private Rectangle _gateHeartsRect;   // 心数循环钮
    private Rectangle _gateMarriedRect;  // 已婚 CheckBox
    private Rectangle _gateSpouseRect;   // 婚配对象单行框
    private Rectangle _gateMemberRect;   // Joja 会员三态钮
    private Rectangle _gateClosedRect;   // Joja 倒闭三态钮

    // ── Tab4 控件（社交关系） ──────────────────────────────────────────
    private int _relSelectedIndex = -1;                 // 选中关系下标（_relCandidates）
    private List<string> _relCandidates = new List<string>();
    private string _relSelectedNpc = "";                // 当前选中关系 NPC 内部名
    private TextBox _relHeadingBox;                     // 关系 Heading
    private MultilineTextBox _relDescBox;               // 关系 Description
    private Rectangle _relPrevRect;                     // ◀
    private Rectangle _relNextRect;                     // ▶
    private Rectangle _relAddRect;                      // [添加关系]
    private Rectangle _relDelRect;                      // [删除关系]
    private Rectangle _relListRect;                     // 当前关系名展示区

    // ── Tab5 控件（环境感知） ──────────────────────────────────────────
    private OptionsCheckbox _enableBarkCheckbox;        // EnableAmbientBarks
    private MultilineTextBox _voiceBox;                 // AmbientBarkPrompt.VoiceAndAttitude
    private MultilineTextBox _habitsBox;                // AmbientBarkPrompt.SpokenHabits
    private MultilineTextBox _lensesBox;                // AmbientBarkPrompt.ObservationLenses
    private TextBox _globalPreoccBox;                   // 全局 Preoccupations 逗号单行
    private Rectangle _scrapeRect;                      // [↺ 从游戏原版对白中抓取 3 组范例]

    // 各 Tab 多行框的绘制区域（由 Layout() 统一计算，Draw 内仅 SetBounds + Draw，禁止 new）
    private Rectangle _behaviorDrawRect;
    private Rectangle _dialogueExamplesDrawRect;
    private Rectangle _stageTextDrawRect;
    private Rectangle _stageBarkDrawRect;
    private Rectangle _relDescDrawRect;
    private Rectangle _voiceDrawRect;
    private Rectangle _habitsDrawRect;
    private Rectangle _lensesDrawRect;

    private readonly Rectangle[] _tabRects = new Rectangle[5];
    private Rectangle _cancelRect;
    private Rectangle _saveRect;
    private Rectangle _resetRect;

    // 脏标记提示的绘制计时
    private double _saveFlashTimer;

    public BioEditorMenu(string npcName, IClickableMenu returnMenu)
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

        // Tab2 控件（言行举止）
        _behaviorBox = new MultilineTextBox(Rectangle.Empty, maxLines: 512);
        _dialogueExamplesBox = new MultilineTextBox(Rectangle.Empty, maxLines: 512);

        // Tab3 控件（档位列表）
        _stageTextBox = new MultilineTextBox(Rectangle.Empty, maxLines: 512);
        _stageBarkBox = new MultilineTextBox(Rectangle.Empty, maxLines: 512);

        // Tab4 控件（社交关系）
        _relDescBox = new MultilineTextBox(Rectangle.Empty, maxLines: 512);

        // Tab5 控件（环境感知）
        _voiceBox = new MultilineTextBox(Rectangle.Empty, maxLines: 512);
        _habitsBox = new MultilineTextBox(Rectangle.Empty, maxLines: 512);
        _lensesBox = new MultilineTextBox(Rectangle.Empty, maxLines: 512);

        // 原生 TextBox：LooseSprites/textBox 贴图（回退 mouseCursors），smallFont
        Texture2D uniqueTexture = LoadTextBoxTexture();
        _uniqueBox = new TextBox(uniqueTexture, uniqueTexture, Game1.smallFont, Game1.textColor);
        _stagePreoccBox = new TextBox(uniqueTexture, null, Game1.smallFont, Game1.textColor);
        _gateSpouseBox = new TextBox(uniqueTexture, null, Game1.smallFont, Game1.textColor);
        _relHeadingBox = new TextBox(uniqueTexture, null, Game1.smallFont, Game1.textColor);
        _globalPreoccBox = new TextBox(uniqueTexture, null, Game1.smallFont, Game1.textColor);
        _homeBedCheckbox = new OptionsCheckbox("床位固定 HomeLocationBed", -1, 0, 0);
        _enableBarkCheckbox = new OptionsCheckbox("启用碎碎念 AmbientBarks", -1, 0, 0);

        Layout();

        // 初始化 Tab1 控件初值
        _biographyBox.Text = _bio.Biography ?? string.Empty;
        _uniqueBox.Text = _bio.Unique ?? string.Empty;
        _uniqueBox.Selected = false;
        _homeBedCheckbox.isChecked = _bio.HomeLocationBed;

        // 初始化 Tab5 控件初值
        _enableBarkCheckbox.isChecked = _bio.EnableAmbientBarks;
        _globalPreoccBox.Text = JoinPreocc(_bio.Preoccupations);
        SyncTab5BarkBoxes();

        // 初始化 Tab4 候选列表
        BuildRelCandidates();
        if (_relCandidates.Count > 0)
            SelectRelationship(0);
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

            // 模板按钮：位于 Biography 框右下角上方，尺寸在绘制时按当前文本测量
            int scW = 150;
            int scH = 28;
            _scaffoldRect = new Rectangle(
                bodyLeft + bodyW - scW,
                bodyTop - scH - 4,
                scW, scH);
        }

        // ── Tab2 内容区（言行举止：两个编辑块纵向排布，各约一半） ────────
        if (bodyH > 80)
        {
            int halfH = (bodyH - ListRowH) / 2;
            int behaviorH = Math.Max(60, halfH);
            int dialogueH = Math.Max(60, bodyH - behaviorH - ListRowH);

            // 行为规则块
            var oldBehavior = _behaviorBox;
            _behaviorBox = new MultilineTextBox(
                new Rectangle(bodyLeft, bodyTop, bodyW, behaviorH), maxLines: 512);
            _behaviorBox.Text = oldBehavior.Text;
            _behaviorBox.Selected = oldBehavior.Selected;

            // 对白范例块
            int dialogueTop = bodyTop + behaviorH + ListRowH;
            var oldDialogue = _dialogueExamplesBox;
            _dialogueExamplesBox = new MultilineTextBox(
                new Rectangle(bodyLeft, dialogueTop, bodyW, dialogueH), maxLines: 512);
            _dialogueExamplesBox.Text = oldDialogue.Text;
            _dialogueExamplesBox.Selected = oldDialogue.Selected;

            // 行为规则模板按钮（位于该框右上角外侧）
            _behaviorScaffoldRect = new Rectangle(
                bodyLeft + bodyW - 150,
                bodyTop - 28 - 4,
                150, 28);
        }

        // ── Tab3 内容区（好感演变：上半档位列表 + 下半选中档编辑器） ────
        if (bodyH > 120)
        {
            int listH = Math.Min(bodyH / 2, _stageRowRects.Length * ListRowH);
            int rowY = bodyTop;
            for (int i = 0; i < _stageRowRects.Length; i++)
            {
                _stageRowRects[i] = new Rectangle(bodyLeft, rowY + i * ListRowH, bodyW, ListRowH);
            }
            _newStageRect = new Rectangle(bodyLeft, rowY + _stageRowRects.Length * ListRowH, bodyW, ListRowH);

            int editorTop = rowY + listH + 8;
            int editorH = bodyBottom - editorTop;
            if (editorH > 80)
            {
                int gateRowH = 36;
                int textH = Math.Max(40, (editorH - gateRowH * 2 - ListRowH) / 2);

                _stageTextBox.SetBounds(new Rectangle(bodyLeft, editorTop, bodyW, textH));

                // 门禁编辑区
                int gateY = editorTop + textH + 4;
                _gateToggleRect = new Rectangle(bodyLeft, gateY, 110, gateRowH - 6);
                _gateHeartsRect = new Rectangle(bodyLeft + 116, gateY, 60, gateRowH - 6);
                _gateMarriedRect = new Rectangle(bodyLeft + 182, gateY, 90, gateRowH - 6);
                _gateSpouseRect = new Rectangle(bodyLeft + 278, gateY, bodyW - 278, gateRowH - 6);

                int gate2Y = gateY + gateRowH;
                _gateMemberRect = new Rectangle(bodyLeft, gate2Y, 130, gateRowH - 6);
                _gateClosedRect = new Rectangle(bodyLeft + 136, gate2Y, 130, gateRowH - 6);

                // BarkMindset 块
                int barkY = gate2Y + gateRowH + 4;
                int barkH = Math.Max(30, (bodyBottom - barkY) / 2);
                _stageBarkBox.SetBounds(new Rectangle(bodyLeft, barkY, bodyW, barkH));

                // Preoccupations 单行框
                int preoccY = barkY + barkH + 4;
                _stagePreoccBox.X = bodyLeft;
                _stagePreoccBox.Y = preoccY;
                _stagePreoccBox.Width = bodyW;
                _stagePreoccBox.Height = 36;

                // 运行时绘制区（依赖 gateEditMode，由每次切换/布局时重算）
                ComputeTab3DrawRects(bodyLeft, bodyW);
            }
        }

        // Tab2 多行框绘制区（行为规则 / 对白范例）
        if (bodyH > 80)
        {
            int behaviorH = Math.Max(120, (int)(bodyH * 0.45f));
            _behaviorDrawRect = new Rectangle(bodyLeft, bodyTop, bodyW, behaviorH);
            int dialogueTop = bodyTop + behaviorH + ListRowH + 8;
            int dialogueH = Math.Max(80, bodyBottom - dialogueTop - 20);
            _dialogueExamplesDrawRect = new Rectangle(bodyLeft, dialogueTop, bodyW, dialogueH);
        }

        // Tab4 多行框绘制区（Description）
        if (bodyH > 120)
        {
            int descBoxH = Math.Max(60, (yPositionOnScreen + height - FooterH) - (bodyTop + 130));
            _relDescDrawRect = new Rectangle(bodyLeft, bodyTop + 96, bodyW, descBoxH);
        }

        // Tab5 多行框绘制区（Bark 三框，各 ~50px）
        if (bodyH > 120)
        {
            int barkBoxH = 50;
            int voiceY = bodyTop + 48;
            _voiceDrawRect = new Rectangle(bodyLeft, voiceY, bodyW, barkBoxH);
            _habitsDrawRect = new Rectangle(bodyLeft, voiceY + barkBoxH + 24, bodyW, barkBoxH);
            _lensesDrawRect = new Rectangle(bodyLeft, voiceY + (barkBoxH + 24) * 2, bodyW, barkBoxH);
        }

        // ── Tab4 内容区（社交关系：导航 + Heading/Description 编辑） ──────
        if (bodyH > 120)
        {
            int navH = 32;
            int navY = bodyTop;
            int arrowW = 40;
            _relPrevRect = new Rectangle(bodyLeft, navY, arrowW, navH);
            _relNextRect = new Rectangle(bodyLeft + bodyW - arrowW, navY, arrowW, navH);
            _relListRect = new Rectangle(bodyLeft + arrowW + 4, navY, bodyW - 2 * (arrowW + 4), navH);

            int btnY = navY + navH + 4;
            int relBtnW = Math.Min(110, (bodyW - 4) / 2);
            _relAddRect = new Rectangle(bodyLeft, btnY, relBtnW, 28);
            _relDelRect = new Rectangle(bodyLeft + relBtnW + 4, btnY, relBtnW, 28);

            int headingY = btnY + 28 + ListRowH + 20;
            int editorW = bodyW;
            _relHeadingBox = _relHeadingBox ?? new TextBox(Game1.mouseCursors, null, Game1.smallFont, Game1.textColor);
        }

        // ── Tab5 内容区（环境感知：Bark 三框 + 开关 + 全局池 + 抓取） ────
        if (bodyH > 120)
        {
            int y = bodyTop + 40;
            _scrapeRect = new Rectangle(bodyLeft, y, Math.Min(280, bodyW), 28);
        }
    }

    /// <summary>Tab3 运行时绘制区：依赖 gateEditMode，每次切换门禁/布局时由调用方触发。</summary>
    private void ComputeTab3DrawRects(int bodyLeft, int bodyW)
    {
        if (_stageIdx < 0 || _stageIdx >= _bio.ProgressStates.Count)
        {
            _stageTextDrawRect = Rectangle.Empty;
            _stageBarkDrawRect = Rectangle.Empty;
            return;
        }
        int textY = (_gateEditMode ? _gateClosedRect.Bottom : _gateToggleRect.Bottom) + 8;
        int textLabelH = (int)Game1.smallFont.MeasureString("阶段态度 Text").Y;
        int editorW = (xPositionOnScreen + width - PadX) - bodyLeft;
        int textH = Math.Max(60, (yPositionOnScreen + height - FooterH) - (textY + textLabelH + 2 + 160));
        _stageTextDrawRect = new Rectangle(bodyLeft, textY + textLabelH + 2, editorW, textH);

        int barkY = _stageTextDrawRect.Bottom + 6;
        int barkLabelH = (int)Game1.smallFont.MeasureString("碎碎念心智 BarkMindset").Y;
        int barkH = Math.Max(40, (yPositionOnScreen + height - FooterH) - (barkY + barkLabelH + 2 + 60));
        _stageBarkDrawRect = new Rectangle(bodyLeft, barkY + barkLabelH + 2, editorW, barkH);
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
        else if (_activeTab == 1)
        {
            UpdateTab2(time);
        }
        else if (_activeTab == 2)
        {
            UpdateTab3(time);
        }
        else if (_activeTab == 3)
        {
            UpdateTab4();
        }
        else if (_activeTab == 4)
        {
            UpdateTab5(time);
        }
    }

    // ── Tab2 回写（言行举止） ──────────────────────────────────────────

    private void UpdateTab2(GameTime time)
    {
        _behaviorBox.Update(time);
        _dialogueExamplesBox.Update(time);

        // 惰性写入：先取现值比较，仅在实际差异时创建条目 + 写入
        string behaviorText = _behaviorBox.Text ?? string.Empty;
        if (_bio.Traits.TryGetValue("BehavioralRules", out var behavior) && behavior != null)
        {
            if (behavior.Description != behaviorText)
            {
                behavior.Description = behaviorText;
                MarkDirty();
            }
        }
        else if (!string.IsNullOrEmpty(behaviorText))
        {
            EnsureTraitEntry("BehavioralRules", "Behavioral Rules").Description = behaviorText;
            MarkDirty();
        }

        string examplesText = _dialogueExamplesBox.Text ?? string.Empty;
        if (_bio.Traits.TryGetValue("DialogueExamples", out var examples) && examples != null)
        {
            if (examples.Description != examplesText)
            {
                examples.Description = examplesText;
                MarkDirty();
            }
        }
        else if (!string.IsNullOrEmpty(examplesText))
        {
            EnsureTraitEntry("DialogueExamples", "Dialogue Examples").Description = examplesText;
            MarkDirty();
        }
    }

    /// <summary>确保 Traits 中存在指定键的 ListEntry；无则创建（RequiredHearts=0）。</summary>
    private BioData.ListEntry EnsureTraitEntry(string key, string defaultHeading)
    {
        if (!_bio.Traits.TryGetValue(key, out var entry) || entry == null)
        {
            entry = new BioData.ListEntry
            {
                id = key,
                Heading = defaultHeading,
                Description = string.Empty,
                RequiredHearts = 0
            };
            _bio.Traits[key] = entry;
        }
        return entry;
    }

    // ── Tab3 回写（好感演变） ──────────────────────────────────────────

    private void UpdateTab3(GameTime time)
    {
        if (_stageIdx < 0 || _stageIdx >= _bio.ProgressStates.Count)
            return;

        var stage = _bio.ProgressStates[_stageIdx];

        _stageTextBox.Update(time);
        _stageBarkBox.Update(time);

        if ((stage.Text ?? "") != _stageTextBox.Text)
        {
            stage.Text = _stageTextBox.Text;
            MarkDirty();
        }
        if ((stage.BarkMindset ?? "") != _stageBarkBox.Text)
        {
            stage.BarkMindset = _stageBarkBox.Text;
            MarkDirty();
        }

        // Preoccupations 逗号行 → List（空则置 null）
        string preoccText = _stagePreoccBox.Text ?? string.Empty;
        var parsed = new List<string>();
        foreach (var raw in preoccText.Split(new[] { ',', '，' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var t = raw.Trim();
            if (!string.IsNullOrEmpty(t))
                parsed.Add(t);
        }
        bool changed = false;
        if (parsed.Count == 0)
        {
            if (stage.Preoccupations != null)
            {
                stage.Preoccupations = null;
                changed = true;
            }
        }
        else
        {
            if (stage.Preoccupations == null
                || stage.Preoccupations.Count != parsed.Count
                || !ListsEqual(stage.Preoccupations, parsed))
            {
                stage.Preoccupations = parsed;
                changed = true;
            }
        }
        if (changed)
            MarkDirty();
    }

    private static bool ListsEqual(List<string> a, List<string> b)
    {
        for (int i = 0; i < a.Count; i++)
            if (a[i] != b[i]) return false;
        return true;
    }

    /// <summary>选中档位（基于列表顺序，不重排）。</summary>
    private void SelectStage(int idx)
    {
        if (idx < 0 || idx >= _bio.ProgressStates.Count)
            return;
        ApplyGateEditors();
        _stageIdx = idx;
        var stage = _bio.ProgressStates[idx];
        _stageTextBox.Text = stage.Text ?? string.Empty;
        _stageBarkBox.Text = stage.BarkMindset ?? string.Empty;
        _stagePreoccBox.Text = stage.Preoccupations != null ? string.Join(", ", stage.Preoccupations) : string.Empty;
        SyncGateEditors();
        // 选中档变更影响编辑器绘制区
        int bodyLeft = xPositionOnScreen + PadX;
        ComputeTab3DrawRects(bodyLeft, (xPositionOnScreen + width - PadX) - bodyLeft);
    }

    /// <summary>确保当前选中档存在（仅当用户实际编辑时调用）。</summary>
    private BioData.ProgressStateEntry EnsureStageEntry()
    {
        if (_stageIdx >= 0 && _stageIdx < _bio.ProgressStates.Count)
            return _bio.ProgressStates[_stageIdx];
        var entry = new BioData.ProgressStateEntry { RequiredHearts = _gateHearts };
        _bio.ProgressStates.Add(entry);
        _stageIdx = _bio.ProgressStates.Count - 1;
        return entry;
    }

    private void SyncGateEditors()
    {
        if (_stageIdx < 0 || _stageIdx >= _bio.ProgressStates.Count)
            return;
        var p = _bio.ProgressStates[_stageIdx];
        _gateHearts = p.RequiredHearts;
        _gateMarried = p.RequireMarried;
        _gateSpouse = p.RequirePlayerMarriedTo ?? string.Empty;
        _gateSpouseBox.Text = _gateSpouse;
        _gateJojaMember = p.RequireJojaMember.HasValue ? (p.RequireJojaMember.Value ? 1 : 2) : 0;
        _gateJojaClosed = p.RequireJojaMartClosed.HasValue ? (p.RequireJojaMartClosed.Value ? 1 : 2) : 0;
    }

    private void ApplyGateEditors()
    {
        if (!_gateEditMode || _stageIdx < 0 || _stageIdx >= _bio.ProgressStates.Count)
            return;
        var p = _bio.ProgressStates[_stageIdx];
        p.RequiredHearts = _gateHearts;
        p.RequireMarried = _gateMarried;
        p.RequirePlayerMarriedTo = string.IsNullOrWhiteSpace(_gateSpouseBox.Text) ? null : _gateSpouseBox.Text.Trim();
        p.RequireJojaMember = _gateJojaMember == 0 ? (bool?)null : (_gateJojaMember == 1);
        p.RequireJojaMartClosed = _gateJojaClosed == 0 ? (bool?)null : (_gateJojaClosed == 1);
        MarkDirty();
    }

    /// <summary>绿点：逐项 AND 求值当前状态是否满足该档全部门禁。</summary>
    private bool GateSatisfiedNow(BioData.ProgressStateEntry p)
    {
        try
        {
            // 心数
            int hearts = 0;
            var player = Game1.player;
            if (player?.friendshipData != null
                && player.friendshipData.TryGetValue(_npcName, out var fs) && fs != null)
                hearts = fs.Points / 250;
            if (hearts < p.RequiredHearts) return false;

            // 婚姻
            if (p.RequireMarried && !ProgressStateResolver.IsMarriedToPlayer(_npcName)) return false;

            // 指定配偶
            if (!string.IsNullOrWhiteSpace(p.RequirePlayerMarriedTo)
                && !ProgressStateResolver.IsMarriedToPlayer(p.RequirePlayerMarriedTo)) return false;

            // Joja 会员（本地玩家旗标）
            if (p.RequireJojaMember.HasValue && p.RequireJojaMember.Value != ProgressStateResolver.CheckJojaMember())
                return false;

            // Joja 倒闭（世界级旗标）
            if (p.RequireJojaMartClosed.HasValue
                && p.RequireJojaMartClosed.Value != ProgressStateResolver.CheckJojaMartClosed())
                return false;

            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Tab3 点击处理：档位列表选择 / 新建 / 编辑器控件。返回 true 表示已处理。</summary>
    private bool HandleTab3Click(int x, int y)
    {
        // 档位列表行选择
        int visibleStages = Math.Min(_bio.ProgressStates.Count, _stageRowRects.Length);
        for (int i = 0; i < visibleStages; i++)
        {
            if (_stageRowRects[i].Contains(x, y))
            {
                SelectStage(i);
                Game1.playSound("smallSelect");
                return true;
            }
        }

        // 新建档位
        bool hasRoom = _bio.ProgressStates.Count < _stageRowRects.Length;
        if (hasRoom && _newStageRect.Contains(x, y))
        {
            ApplyGateEditors();
            var entry = new BioData.ProgressStateEntry { RequiredHearts = _gateHearts };
            _bio.ProgressStates.Add(entry);
            SelectStage(_bio.ProgressStates.Count - 1);
            Game1.playSound("newRecipe");
            return true;
        }

        if (_stageIdx < 0 || _stageIdx >= _bio.ProgressStates.Count)
        {
            Game1.keyboardDispatcher.Subscriber = null;
            _stageTextBox.Selected = false;
            _stageBarkBox.Selected = false;
            return false;
        }

        var stage = _bio.ProgressStates[_stageIdx];

        // 阶段态度 Text 框
        if (_stageTextBox.Bounds.Contains(x, y))
        {
            _stageTextBox.Selected = true;
            _stageBarkBox.Selected = false;
            Game1.keyboardDispatcher.Subscriber = _stageTextBox;
            return true;
        }
        // BarkMindset 框
        if (_stageBarkBox.Bounds.Contains(x, y))
        {
            // 新建档首次聚焦时自动填模板
            if (string.IsNullOrWhiteSpace(_stageBarkBox.Text) && string.IsNullOrWhiteSpace(stage.BarkMindset))
                _stageBarkBox.Text = BarkMindsetScaffold;
            _stageBarkBox.Selected = true;
            _stageTextBox.Selected = false;
            Game1.keyboardDispatcher.Subscriber = _stageBarkBox;
            return true;
        }
        // Preoccupations 框
        if (new Rectangle(_stagePreoccBox.X, _stagePreoccBox.Y, _stagePreoccBox.Width, _stagePreoccBox.Height).Contains(x, y))
        {
            _stagePreoccBox.SelectMe();
            _stageTextBox.Selected = false;
            _stageBarkBox.Selected = false;
            Game1.keyboardDispatcher.Subscriber = _stagePreoccBox;
            return true;
        }

        // 门禁编辑区
        if (_gateToggleRect.Contains(x, y))
        {
            _gateEditMode = !_gateEditMode;
            if (!_gateEditMode)
                ApplyGateEditors(); // 退出编辑模式 → 确认写入
            else
                SyncGateEditors();  // 进入编辑模式 → 从档位同步到编辑器
            // 门禁形态切换影响 Tab3 编辑器绘制区，重算
            int bodyLeft = xPositionOnScreen + PadX;
            ComputeTab3DrawRects(bodyLeft, (xPositionOnScreen + width - PadX) - bodyLeft);
            Game1.playSound("drumkit6");
            return true;
        }

        if (_gateEditMode)
        {
            if (_gateHeartsRect.Contains(x, y))
            {
                _gateHearts = _gateHearts switch { 0 => 3, 3 => 7, 7 => 8, _ => 0 };
                Game1.playSound("drumkit6");
                return true;
            }
            if (_gateMarriedRect.Contains(x, y))
            {
                _gateMarried = !_gateMarried;
                Game1.playSound("drumkit6");
                return true;
            }
            if (_gateSpouseRect.Contains(x, y))
            {
                _gateSpouseBox.SelectMe();
                _stageTextBox.Selected = false;
                _stageBarkBox.Selected = false;
                Game1.keyboardDispatcher.Subscriber = _gateSpouseBox;
                return true;
            }
            if (_gateMemberRect.Contains(x, y))
            {
                _gateJojaMember = (_gateJojaMember + 1) % 3; // 0=不限制 1=true 2=false
                Game1.playSound("drumkit6");
                return true;
            }
            if (_gateClosedRect.Contains(x, y))
            {
                _gateJojaClosed = (_gateJojaClosed + 1) % 3;
                Game1.playSound("drumkit6");
                return true;
            }
        }

        Game1.keyboardDispatcher.Subscriber = null;
        _stageTextBox.Selected = false;
        _stageBarkBox.Selected = false;
        return false;
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

            // 模板按钮：仅 Biography 为空时生效
            if (ShouldShowScaffold() && _scaffoldRect.Contains(x, y))
            {
                InsertScaffold();
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
        else if (_activeTab == 1)
        {
            // 行为规则框
            if (_behaviorBox.Bounds.Contains(x, y))
            {
                _dialogueExamplesBox.Selected = false;
                _behaviorBox.Selected = true;
                Game1.keyboardDispatcher.Subscriber = _behaviorBox;
                return;
            }
            // 行为规则模板按钮
            if (string.IsNullOrWhiteSpace(_behaviorBox.Text) && _behaviorScaffoldRect.Contains(x, y))
            {
                _behaviorBox.Text = BehaviorScaffold;
                MarkDirty();
                Game1.playSound("coin");
                return;
            }
            // 对白范例框
            if (_dialogueExamplesBox.Bounds.Contains(x, y))
            {
                _behaviorBox.Selected = false;
                _dialogueExamplesBox.Selected = true;
                Game1.keyboardDispatcher.Subscriber = _dialogueExamplesBox;
                return;
            }
            // 失去焦点
            Game1.keyboardDispatcher.Subscriber = null;
            _behaviorBox.Selected = false;
            _dialogueExamplesBox.Selected = false;
        }
        else if (_activeTab == 2)
        {
            if (HandleTab3Click(x, y))
                return;
        }
        else if (_activeTab == 3)
        {
            if (HandleTab4Click(x, y))
                return;
        }
        else if (_activeTab == 4)
        {
            if (HandleTab5Click(x, y))
                return;
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
        else if (_activeTab == 1)
        {
            if (_behaviorBox.Selected) _behaviorBox.Scroll(direction);
            else if (_dialogueExamplesBox.Selected) _dialogueExamplesBox.Scroll(direction);
        }
        else if (_activeTab == 2)
        {
            if (_stageTextBox.Selected) _stageTextBox.Scroll(direction);
            else if (_stageBarkBox.Selected) _stageBarkBox.Scroll(direction);
        }
        else if (_activeTab == 3)
        {
            if (_relDescBox.Selected) _relDescBox.Scroll(direction);
        }
        else if (_activeTab == 4)
        {
            if (_voiceBox.Selected) _voiceBox.Scroll(direction);
            else if (_habitsBox.Selected) _habitsBox.Scroll(direction);
            else if (_lensesBox.Selected) _lensesBox.Scroll(direction);
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
        // 无条件清理键盘订阅与全部框焦点，避免退出后打字进入残留输入
        Game1.keyboardDispatcher.Subscriber = null;
        _biographyBox.Selected = false;
        _uniqueBox.Selected = false;
        _behaviorBox.Selected = false;
        _dialogueExamplesBox.Selected = false;
        _stageTextBox.Selected = false;
        _stageBarkBox.Selected = false;
        _relDescBox.Selected = false;
        _voiceBox.Selected = false;
        _habitsBox.Selected = false;
        _lensesBox.Selected = false;
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
        ShowLintSummary();

        if (!ModEntry.BioStorage!.SaveOverlay(_npcName, _bio, out string errorMessage))
        {
            Game1.addHUDMessage(new HUDMessage($"人设保存失败: {errorMessage}", HUDMessage.error_type));
            return;
        }
        Game1.playSound("achievement");
        ExitAndReturn();
    }

    /// <summary>
    /// 保存前体检：逐项记录问题日志并以 HUD 提示数量（不阻断保存）。
    /// BioLinter 尚未合入（VT-BIO-07）时跳过，保留接线占位。
    /// </summary>
    private void ShowLintSummary()
    {
#if false // TODO VT-BIO-07: 合入 BioLinter 后移除此 guard 并恢复调用
        var issues = BioLinter.Lint(_bio);
        foreach (var issue in issues)
        {
            ModEntry.SMonitor?.Log($"[BioLinter] {issue.Code}: {issue.Message}", LogLevel.Info);
        }
        if (issues.Count > 0)
        {
            Game1.addHUDMessage(new HUDMessage(
                $"体检提示 {issues.Count} 项（已记录日志，不阻断保存）",
                HUDMessage.newQuest_type));
        }
#endif
    }

    private bool ShouldShowScaffold()
    {
        return _bio.Missing || string.IsNullOrWhiteSpace(_bio.Biography);
    }

    private void InsertScaffold()
    {
        string template = BiographyScaffold.Replace("{NPC}", _npcName);
        _biographyBox.Text = template;
        MarkDirty();
        Game1.playSound("coin");
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
        else if (_activeTab == 1)
        {
            DrawTab2(b, mx, my);
        }
        else if (_activeTab == 2)
        {
            DrawTab3(b, mx, my);
        }
        else if (_activeTab == 3)
        {
            DrawTab4(b, mx, my);
        }
        else if (_activeTab == 4)
        {
            DrawTab5(b, mx, my);
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

        // 空人设时显示"插入身份模板"按钮
        if (ShouldShowScaffold())
        {
            DrawButton(b, _scaffoldRect, "插入身份模板", mx, my);
        }

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

    // ── Tab2 绘制（言行举止） ──────────────────────────────────────────

    private void DrawTab2(SpriteBatch b, int mx, int my)
    {
        int bodyTop = _tabRects[0].Y + _tabRects[0].Height + 12;
        int bodyLeft = xPositionOnScreen + PadX;

        // 行为规则标签
        string label1 = "行为规则（保留 [VOICE]/[IMMEDIATE REFLEXES] 等分节）";
        b.DrawString(Game1.smallFont, label1, new Vector2(bodyLeft, bodyTop), Game1.textColor);

        // 行为规则模板按钮
        if (string.IsNullOrWhiteSpace(_behaviorBox.Text))
        {
            DrawButton(b, _behaviorScaffoldRect, "插入规则模板", mx, my);
        }

        // 行为规则框
        _behaviorBox.Draw(b);

        // 对白范例标签
        int dialogueTop = _dialogueExamplesBox.Bounds.Y;
        string label2 = "对白范例（含 #$b#/$s/% 应答等指令符，谨慎编辑）";
        b.DrawString(Game1.smallFont, label2,
            new Vector2(bodyLeft, dialogueTop - Game1.smallFont.MeasureString(label2).Y - 4),
            Game1.textColor);

        // 对白范例框
        _dialogueExamplesBox.Draw(b);
    }

    // ── Tab3 绘制（好感演变 / 档位列表） ──────────────────────────────

    private void DrawTab3(SpriteBatch b, int mx, int my)
    {
        int bodyTop = _tabRects[0].Y + _tabRects[0].Height + 12;
        int bodyLeft = xPositionOnScreen + PadX;

        // 上半：档位列表
        int visibleStages = Math.Min(_bio.ProgressStates.Count, _stageRowRects.Length);
        for (int i = 0; i < visibleStages; i++)
        {
            DrawStageRow(b, i, mx, my);
        }
        if (_bio.ProgressStates.Count > _stageRowRects.Length)
        {
            string hint = $"共 {_bio.ProgressStates.Count} 档，编辑请先选中";
            b.DrawString(Game1.smallFont, hint,
                new Vector2(bodyLeft, _stageRowRects[_stageRowRects.Length - 1].Bottom + 2),
                Color.Gray);
        }
        // 新建档位行
        if (_bio.ProgressStates.Count < _stageRowRects.Length)
        {
            DrawButton(b, _newStageRect, "+ 新建档位", mx, my);
        }

        // 下半：选中档编辑器
        if (_stageIdx < 0 || _stageIdx >= _bio.ProgressStates.Count)
        {
            b.DrawString(Game1.smallFont, "请从上方选择一个档位进行编辑",
                new Vector2(bodyLeft, _newStageRect.Bottom + 12), Color.Gray);
            return;
        }

        int editorTop = _newStageRect.Bottom + 8;
        var stage = _bio.ProgressStates[_stageIdx];

        // 门禁徽标行（只读摘要）
        string gateSummary = BuildGateSummary(stage);
        b.DrawString(Game1.smallFont, $"门禁: {gateSummary}",
            new Vector2(bodyLeft, editorTop), Game1.textColor);
        DrawButton(b, _gateToggleRect, _gateEditMode ? "完成门禁" : "编辑门禁", mx, my);

        if (_gateEditMode)
        {
            int gateY = _gateToggleRect.Y;
            DrawButton(b, _gateHeartsRect, $"心≥{_gateHearts}", mx, my);

            // 已婚 CheckBox
            Color marriedBg = _gateMarried ? new Color(120, 200, 120) : new Color(210, 180, 140);
            IClickableMenu.drawTextureBox(b, Game1.mouseCursors,
                new Rectangle(432, 439, 9, 9),
                _gateMarriedRect.X, _gateMarriedRect.Y, _gateMarriedRect.Width, _gateMarriedRect.Height,
                marriedBg, 4f, false);
            b.DrawString(Game1.smallFont, "已婚",
                new Vector2(_gateMarriedRect.X + 6, _gateMarriedRect.Y + 6), Game1.textColor);

            // 婚配对象框
            DrawSingleLineBox(b, _gateSpouseBox, "婚配对象(内部名)", mx, my);

            // Joja 会员三态
            string[] jojaMemberLabels = { "Joja会员:不限", "Joja会员:是", "Joja会员:否" };
            DrawButton(b, _gateMemberRect, jojaMemberLabels[_gateJojaMember], mx, my);

            // Joja 倒闭三态
            string[] jojaClosedLabels = { "Joja倒闭:不限", "Joja倒闭:是", "Joja倒闭:否" };
            DrawButton(b, _gateClosedRect, jojaClosedLabels[_gateJojaClosed], mx, my);
        }

        // 阶段态度 Text（仅同步绘制区，不新建实例）
        _stageTextBox.SetBounds(_stageTextDrawRect);
        _stageTextBox.Draw(b);

        // BarkMindset
        _stageBarkBox.SetBounds(_stageBarkDrawRect);
        _stageBarkBox.Draw(b);

        // Preoccupations
        int preoccY = _stageBarkBox.Bounds.Bottom + 6;
        b.DrawString(Game1.smallFont, "阶段关注池（逗号分隔，留空=沿用全局池）",
            new Vector2(bodyLeft, preoccY), Game1.textColor);
        int preoccLabelH = (int)Game1.smallFont.MeasureString("阶段关注池").Y;
        _stagePreoccBox.X = bodyLeft;
        _stagePreoccBox.Y = preoccY + preoccLabelH + 2;
        _stagePreoccBox.Width = _stageTextBox.Bounds.Width;
        _stagePreoccBox.Height = 32;
        DrawSingleLineBox(b, _stagePreoccBox, null, mx, my);
    }

    private void DrawStageRow(SpriteBatch b, int i, int mx, int my)
    {
        var rect = _stageRowRects[i];
        bool selected = (i == _stageIdx);
        bool satisfied = GateSatisfiedNow(_bio.ProgressStates[i]);
        Color bg = selected ? new Color(210, 180, 140)
                 : rect.Contains(mx, my) ? new Color(255, 235, 205)
                 : new Color(139, 90, 43);

        IClickableMenu.drawTextureBox(b, Game1.mouseCursors,
            new Rectangle(432, 439, 9, 9),
            rect.X, rect.Y, rect.Width, rect.Height, bg, 4f, false);

        // 绿点
        Color dot = satisfied ? new Color(80, 200, 80) : new Color(180, 180, 180);
        b.Draw(Game1.staminaRect, new Rectangle(rect.X + 6, rect.Y + 12, 8, 8), dot);

        // 行文本：档{序号} {门禁徽标} {Text 首行截断24}
        string firstLine = _bio.ProgressStates[i].Text ?? string.Empty;
        int nl = firstLine.IndexOf('\n');
        if (nl >= 0) firstLine = firstLine.Substring(0, nl);
        if (firstLine.Length > 24) firstLine = firstLine.Substring(0, 24) + "…";
        string gate = BuildGateSummary(_bio.ProgressStates[i]);
        string text = $"档{i + 1} [{gate}] {firstLine}";
        b.DrawString(Game1.smallFont, text,
            new Vector2(rect.X + 20, rect.Y + (rect.Height - Game1.smallFont.MeasureString(text).Y) / 2f),
            Game1.textColor);
    }

    private static string BuildGateSummary(BioData.ProgressStateEntry p)
    {
        var parts = new System.Collections.Generic.List<string>();
        if (p.RequireMarried) parts.Add("已婚");
        if (!string.IsNullOrWhiteSpace(p.RequirePlayerMarriedTo)) parts.Add($"婚配{p.RequirePlayerMarriedTo}");
        if (p.RequireJojaMartClosed == true) parts.Add("Joja倒闭");
        if (p.RequireJojaMember == true) parts.Add("Joja会员");
        if (parts.Count > 0) return string.Join("/", parts);
        return $"≥{p.RequiredHearts}心";
    }

    private static void DrawSingleLineBox(SpriteBatch b, TextBox box, string label, int mx, int my)
    {
        IClickableMenu.drawTextureBox(b, box.X, box.Y, box.Width, box.Height, Color.White);
        if (!string.IsNullOrEmpty(box.Text))
        {
            b.DrawString(Game1.smallFont, box.Text,
                new Vector2(box.X + 8, box.Y + 8), Game1.textColor);
        }
        if (box.Selected)
        {
            float cx = box.X + 8 + Game1.smallFont.MeasureString(box.Text ?? string.Empty).X;
            b.Draw(Game1.staminaRect, new Rectangle((int)cx, box.Y + 6, 2, 24), Game1.textColor);
        }
    }

    // ── Tab4 逻辑（社交关系） ──────────────────────────────────────────

    private void BuildRelCandidates()
    {
        _relCandidates.Clear();
        if (Game1.player?.friendshipData != null)
        {
            foreach (var name in Game1.player.friendshipData.Keys)
            {
                if (!string.Equals(name, _npcName, StringComparison.OrdinalIgnoreCase))
                    _relCandidates.Add(name);
            }
        }
        _relCandidates.Sort(StringComparer.OrdinalIgnoreCase);
    }

    private void SelectRelationship(int index)
    {
        if (_relCandidates.Count == 0)
        {
            _relSelectedIndex = -1;
            _relSelectedNpc = "";
            _relHeadingBox.Text = "";
            _relDescBox.Text = "";
            return;
        }
        index = Math.Clamp(index, 0, _relCandidates.Count - 1);
        _relSelectedIndex = index;
        _relSelectedNpc = _relCandidates[index];
        if (_bio.Relationships.TryGetValue(_relSelectedNpc, out var entry) && entry != null)
        {
            _relHeadingBox.Text = entry.Heading ?? string.Empty;
            _relDescBox.Text = entry.Description ?? string.Empty;
        }
        else
        {
            _relHeadingBox.Text = string.Empty;
            _relDescBox.Text = string.Empty;
        }
    }

    private void UpdateTab4()
    {
        if (string.IsNullOrEmpty(_relSelectedNpc))
            return;

        string headingText = _relHeadingBox.Text ?? string.Empty;
        string descText = _relDescBox.Text ?? string.Empty;

        // 惰性写入：先取现值比较，仅在实际差异时创建条目 + 写入
        if (_bio.Relationships.TryGetValue(_relSelectedNpc, out var entry) && entry != null)
        {
            bool changed = false;
            if (entry.Heading != headingText) { entry.Heading = headingText; changed = true; }
            if (entry.Description != descText) { entry.Description = descText; changed = true; }
            if (changed) MarkDirty();
        }
        else if (!string.IsNullOrEmpty(headingText) || !string.IsNullOrEmpty(descText))
        {
            var newEntry = EnsureRelationshipEntry(_relSelectedNpc);
            newEntry.Heading = headingText;
            newEntry.Description = descText;
            MarkDirty();
        }
    }

    private BioData.ListEntry EnsureRelationshipEntry(string npcName)
    {
        if (_bio.Relationships == null)
            _bio.Relationships = new Dictionary<string, BioData.ListEntry>();
        if (!_bio.Relationships.TryGetValue(npcName, out var entry) || entry == null)
        {
            entry = new BioData.ListEntry
            {
                id = npcName,
                Heading = string.Empty,
                Description = string.Empty,
                RequiredHearts = 0
            };
            _bio.Relationships[npcName] = entry;
        }
        return entry;
    }

    private bool HandleTab4Click(int x, int y)
    {
        if (_relPrevRect.Contains(x, y))
        {
            SelectRelationship(_relSelectedIndex - 1);
            Game1.playSound("smallSelect");
            return true;
        }
        if (_relNextRect.Contains(x, y))
        {
            SelectRelationship(_relSelectedIndex + 1);
            Game1.playSound("smallSelect");
            return true;
        }
        if (_relHeadingBox != null && new Rectangle(_relHeadingBox.X, _relHeadingBox.Y, _relHeadingBox.Width, _relHeadingBox.Height).Contains(x, y))
        {
            _relHeadingBox.SelectMe();
            _relDescBox.Selected = false;
            Game1.keyboardDispatcher.Subscriber = _relHeadingBox;
            return true;
        }
        if (_relDescBox.Bounds.Contains(x, y))
        {
            _relHeadingBox.Selected = false;
            _relDescBox.Selected = true;
            Game1.keyboardDispatcher.Subscriber = _relDescBox;
            return true;
        }
        if (_relAddRect.Contains(x, y))
        {
            Game1.activeClickableMenu = new ConfirmationDialog(
                $"添加一条 {_npcName} 的关系条目？（请先确认目标 NPC 内部名）",
                _ =>
                {
                    Game1.activeClickableMenu = this;
                    AddRelationshipPrompt();
                },
                _ => Game1.activeClickableMenu = this);
            return true;
        }
        if (_relDelRect.Contains(x, y) && !string.IsNullOrEmpty(_relSelectedNpc))
        {
            string target = _relSelectedNpc;
            Game1.activeClickableMenu = new ConfirmationDialog(
                $"删除 {_npcName} → {target} 的关系条目？",
                _ =>
                {
                    Game1.activeClickableMenu = this;
                    _bio.Relationships.Remove(target);
                    MarkDirty();
                    BuildRelCandidates();
                    SelectRelationship(Math.Min(_relSelectedIndex, _relCandidates.Count - 1));
                },
                _ => Game1.activeClickableMenu = this);
            return true;
        }
        Game1.keyboardDispatcher.Subscriber = null;
        _relHeadingBox.Selected = false;
        _relDescBox.Selected = false;
        return false;
    }

    private void AddRelationshipPrompt()
    {
        // 添加到候选列表末尾（使用 displayName 回退的内部名占位，用户可后续编辑）
        string target = _relSelectedNpc;
        if (string.IsNullOrWhiteSpace(target)) return;
        if (!_relCandidates.Contains(target))
        {
            _relCandidates.Add(target);
            _bio.Relationships[target] = new BioData.ListEntry
            {
                id = target, Heading = string.Empty, Description = string.Empty, RequiredHearts = 0
            };
            SelectRelationship(_relCandidates.Count - 1);
            MarkDirty();
        }
    }

    // ── Tab5 逻辑（环境感知） ──────────────────────────────────────────

    private void SyncTab5BarkBoxes()
    {
        var prompt = _bio.AmbientBarkPrompt;
        _voiceBox.Text = prompt?.VoiceAndAttitude ?? string.Empty;
        _habitsBox.Text = prompt?.SpokenHabits ?? string.Empty;
        _lensesBox.Text = prompt?.ObservationLenses ?? string.Empty;
    }

    private AmbientBarkPrompt EnsureAmbientBarkPrompt()
    {
        if (_bio.AmbientBarkPrompt == null)
            _bio.AmbientBarkPrompt = new AmbientBarkPrompt();
        return _bio.AmbientBarkPrompt;
    }

    private void UpdateTab5(GameTime time)
    {
        _voiceBox.Update(time);
        _habitsBox.Update(time);
        _lensesBox.Update(time);

        string voiceText = _voiceBox.Text ?? string.Empty;
        string habitsText = _habitsBox.Text ?? string.Empty;
        string lensesText = _lensesBox.Text ?? string.Empty;

        // 惰性创建：仅当三框任一存在实际差异时才创建 AmbientBarkPrompt
        bool barkChanged = false;
        if (_bio.AmbientBarkPrompt != null)
        {
            if (_bio.AmbientBarkPrompt.VoiceAndAttitude != voiceText) { _bio.AmbientBarkPrompt.VoiceAndAttitude = voiceText; barkChanged = true; }
            if (_bio.AmbientBarkPrompt.SpokenHabits != habitsText) { _bio.AmbientBarkPrompt.SpokenHabits = habitsText; barkChanged = true; }
            if (_bio.AmbientBarkPrompt.ObservationLenses != lensesText) { _bio.AmbientBarkPrompt.ObservationLenses = lensesText; barkChanged = true; }
        }
        else if (!string.IsNullOrEmpty(voiceText) || !string.IsNullOrEmpty(habitsText) || !string.IsNullOrEmpty(lensesText))
        {
            var prompt = EnsureAmbientBarkPrompt();
            prompt.VoiceAndAttitude = voiceText;
            prompt.SpokenHabits = habitsText;
            prompt.ObservationLenses = lensesText;
            barkChanged = true;
        }

        bool changed = barkChanged;
        if (_enableBarkCheckbox.isChecked != _bio.EnableAmbientBarks)
        {
            _bio.EnableAmbientBarks = _enableBarkCheckbox.isChecked;
            changed = true;
        }

        // 全局 Preoccupations 逗号行 → List（空 → new List<string>()，顶层池允许显式清空）
        string preoccText = _globalPreoccBox.Text ?? string.Empty;
        var parsed = new List<string>();
        foreach (var raw in preoccText.Split(new[] { ',', '，' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var t = raw.Trim();
            if (!string.IsNullOrEmpty(t) && !parsed.Contains(t))
                parsed.Add(t);
        }
        const int MaxPreocc = 12;
        bool truncated = false;
        while (parsed.Count > MaxPreocc) { parsed.RemoveAt(parsed.Count - 1); truncated = true; }
        if (truncated)
        {
            _globalPreoccBox.Text = string.Join(", ", parsed);
            Game1.addHUDMessage(new HUDMessage($"全局关注池已截断至上限 {MaxPreocc} 项", HUDMessage.error_type));
        }
        if (!ListStringEqual(_bio.Preoccupations, parsed))
        {
            _bio.Preoccupations = parsed;
            changed = true;
        }
        if (changed) MarkDirty();
    }

    private static bool ListStringEqual(List<string> a, List<string> b)
    {
        if (a == null && b == null) return true;
        if (a == null || b == null) return false;
        if (a.Count != b.Count) return false;
        for (int i = 0; i < a.Count; i++)
            if (a[i] != b[i]) return false;
        return true;
    }

    private static string JoinPreocc(List<string> list) => list == null ? "" : string.Join(", ", list);

    private bool HandleTab5Click(int x, int y)
    {
        if (_voiceBox.Bounds.Contains(x, y))
        {
            _voiceBox.Selected = true;
            _habitsBox.Selected = false;
            _lensesBox.Selected = false;
            Game1.keyboardDispatcher.Subscriber = _voiceBox;
            return true;
        }
        if (_habitsBox.Bounds.Contains(x, y))
        {
            _voiceBox.Selected = false;
            _habitsBox.Selected = true;
            _lensesBox.Selected = false;
            Game1.keyboardDispatcher.Subscriber = _habitsBox;
            return true;
        }
        if (_lensesBox.Bounds.Contains(x, y))
        {
            _voiceBox.Selected = false;
            _habitsBox.Selected = false;
            _lensesBox.Selected = true;
            Game1.keyboardDispatcher.Subscriber = _lensesBox;
            return true;
        }
        if (_enableBarkCheckbox.bounds.Contains(x, y))
        {
            _enableBarkCheckbox.receiveLeftClick(x, y);
            return true;
        }
        if (new Rectangle(_globalPreoccBox.X, _globalPreoccBox.Y, _globalPreoccBox.Width, _globalPreoccBox.Height).Contains(x, y))
        {
            _globalPreoccBox.SelectMe();
            Game1.keyboardDispatcher.Subscriber = _globalPreoccBox;
            return true;
        }
        if (_scrapeRect.Contains(x, y))
        {
            ScrapeExamples();
            return true;
        }
        Game1.keyboardDispatcher.Subscriber = null;
        _voiceBox.Selected = false;
        _habitsBox.Selected = false;
        _lensesBox.Selected = false;
        return false;
    }

    /// <summary>从游戏原版对白抓取范例，追加至 Traits["DialogueExamples"].Description（幂等、不重复、不动既有）。</summary>
    private void ScrapeExamples()
    {
        try
        {
            var lines = DialogueScraper.FetchCleanDialogueExamples(_npcName, 3);
            if (lines == null || lines.Count == 0)
            {
                Game1.addHUDMessage(new HUDMessage("未抓取到可用对白", HUDMessage.error_type));
                return;
            }
            var entry = EnsureTraitEntry("DialogueExamples", "Dialogue Examples");
            var sb = new StringBuilder(entry.Description ?? "");
            int added = 0;
            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                if (sb.ToString().Contains(line, StringComparison.OrdinalIgnoreCase)) continue; // 幂等
                if (sb.Length > 4000) // 上限保护
                {
                    Game1.addHUDMessage(new HUDMessage("对白范例已达上限，停止追加", HUDMessage.error_type));
                    break;
                }
                if (sb.Length > 0) sb.AppendLine();
                sb.Append("- ").Append(line.Trim());
                added++;
            }
            entry.Description = sb.ToString().TrimStart();
            if (added > 0) MarkDirty();
            Game1.playSound("newArtifact");
            Game1.addHUDMessage(new HUDMessage(
                $"已追加 {added} 条对白范例（在 Tab2 对白范例区查看/编辑）",
                HUDMessage.newQuest_type));
        }
        catch (Exception ex)
        {
            ModEntry.SMonitor?.Log($"[BioEditor] 抓取对白失败({_npcName}): {ex.Message}", LogLevel.Warn);
            Game1.addHUDMessage(new HUDMessage("抓取对白失败", HUDMessage.error_type));
        }
    }

    // ── Tab4 绘制（社交关系） ──────────────────────────────────────────

    private void DrawTab4(SpriteBatch b, int mx, int my)
    {
        int bodyTop = _tabRects[0].Y + _tabRects[0].Height + 12;
        int bodyLeft = xPositionOnScreen + PadX;
        int editorW = (xPositionOnScreen + width - PadX) - bodyLeft;

        // ◀ 当前关系名 ▶ 导航
        DrawButton(b, _relPrevRect, "◀", mx, my);
        string current = string.IsNullOrEmpty(_relSelectedNpc)
            ? "(无)"
            : $"{_relSelectedNpc} ({_relSelectedIndex + 1}/{_relCandidates.Count})";
        IClickableMenu.drawTextureBox(b, Game1.mouseCursors,
            new Rectangle(432, 439, 9, 9),
            _relListRect.X, _relListRect.Y, _relListRect.Width, _relListRect.Height, Color.White, 4f, false);
        var curSize = Game1.smallFont.MeasureString(current);
        b.DrawString(Game1.smallFont, current,
            new Vector2(_relListRect.X + (_relListRect.Width - curSize.X) / 2f,
                        _relListRect.Y + (_relListRect.Height - curSize.Y) / 2f),
            Game1.textColor);
        DrawButton(b, _relNextRect, "▶", mx, my);

        // 添加/删除按钮
        DrawButton(b, _relAddRect, "添加关系", mx, my);
        DrawButton(b, _relDelRect, "删除关系", mx, my);

        // RequiredHearts 只读徽标
        if (!string.IsNullOrEmpty(_relSelectedNpc)
            && _bio.Relationships.TryGetValue(_relSelectedNpc, out var relEntry) && relEntry != null)
        {
            string badge = $"RequiredHearts: {relEntry.RequiredHearts}";
            b.DrawString(Game1.smallFont, badge,
                new Vector2(bodyLeft, _relAddRect.Bottom + 4), Color.Gray);
        }

        // Heading 标签 + 框
        int headingY = _relDelRect.Bottom + ListRowH;
        b.DrawString(Game1.smallFont, "Heading", new Vector2(bodyLeft, headingY), Game1.textColor);
        int hlh = (int)Game1.smallFont.MeasureString("Heading").Y;
        _relHeadingBox.X = bodyLeft;
        _relHeadingBox.Y = headingY + hlh + 2;
        _relHeadingBox.Width = editorW;
        _relHeadingBox.Height = 32;
        DrawSingleLineBox(b, _relHeadingBox, null, mx, my);

        // Description 标签 + 框
        int descY = _relHeadingBox.Y + _relHeadingBox.Height + 8;
        b.DrawString(Game1.smallFont, "Description", new Vector2(bodyLeft, descY), Game1.textColor);
        _relDescBox.SetBounds(_relDescDrawRect);
        _relDescBox.Draw(b);

        // 底部交叉设定提示行
        string note = "部分关系可能由其他角色卡交叉注入（如 Morris→Lewis），如需改动请编辑对应 NPC。";
        b.DrawString(Game1.smallFont, note,
            new Vector2(bodyLeft, (yPositionOnScreen + height - FooterH) - 24), Color.Gray);
    }

    // ── Tab5 绘制（环境感知） ──────────────────────────────────────────

    private void DrawTab5(SpriteBatch b, int mx, int my)
    {
        int bodyTop = _tabRects[0].Y + _tabRects[0].Height + 12;
        int bodyLeft = xPositionOnScreen + PadX;
        int editorW = (xPositionOnScreen + width - PadX) - bodyLeft;

        // EnableAmbientBarks 开关
        b.DrawString(Game1.smallFont, _enableBarkCheckbox.label ?? "",
            new Vector2(bodyLeft, bodyTop), Game1.textColor);
        _enableBarkCheckbox.bounds = new Rectangle(bodyLeft + 240, bodyTop, 36, 36);
        _enableBarkCheckbox.draw(b, 0, 0, this);

        int curY = bodyTop + 40;

        // 抓取按钮
        DrawButton(b, _scrapeRect, "↺ 从原版对白抓取 3 组范例", mx, my);
        curY = _scrapeRect.Bottom + 8;

        // Voice & Attitude
        DrawTab5Box(b, "口吻 Voice & Attitude", _voiceBox, _voiceDrawRect, mx, my);
        // Spoken Habits
        DrawTab5Box(b, "口头习惯 Spoken Habits", _habitsBox, _habitsDrawRect, mx, my);
        // Observation Lenses
        DrawTab5Box(b, "观察透镜 Observation Lenses", _lensesBox, _lensesDrawRect, mx, my);

        // 全局关注池
        string preoccLabel = "全局关注池（逗号分隔，上限 12）";
        b.DrawString(Game1.smallFont, preoccLabel, new Vector2(bodyLeft, curY), Game1.textColor);
        int plh = (int)Game1.smallFont.MeasureString(preoccLabel).Y;
        _globalPreoccBox.X = bodyLeft;
        _globalPreoccBox.Y = curY + plh + 2;
        _globalPreoccBox.Width = editorW;
        _globalPreoccBox.Height = 32;
        DrawSingleLineBox(b, _globalPreoccBox, null, mx, my);
    }

    private void DrawTab5Box(SpriteBatch b, string label, MultilineTextBox box, Rectangle rect, int mx, int my)
    {
        b.DrawString(Game1.smallFont, label, new Vector2(rect.X, rect.Y - 20), Game1.textColor);
        box.SetBounds(rect);
        box.Draw(b);
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
