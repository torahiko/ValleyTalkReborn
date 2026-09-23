using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewValley;
using StardewValley.Menus;
using System;
using System.Collections.Generic;
using System.Linq;
using ValleytalkReborn;
using ValleytalkReborn.Services;

namespace ValleytalkReborn.UI;

/// <summary>
/// Tab2 农夫档案：玩家档案（精细化居中排版、大容量签名框、多语言自适应引用线、支持纵向平滑滚动） + NPC 宫格子页。
/// </summary>
internal sealed class ProfileTabView : HubTabViewBase
{
    private const int SubTabBarH = 34;
    private const int TopPadding = 110;
    private const int BottomPadding = 75;
    private const int LeftPadding = 40;
    private const int RightPadding = 40;

    private static readonly Rectangle LeftArrowSource = new(352, 495, 12, 11);
    private static readonly Rectangle RightArrowSource = new(365, 495, 12, 11);
    private static readonly Dictionary<string, Texture2D> PortraitCache = new(StringComparer.OrdinalIgnoreCase);

    private bool _tab2Initialized;
    private int _safetyModeIndex;
    private List<(string Id, string Label)> _orientationItems;
    private DropdownList _orientationDropdown;
    private DialogueTextInputBox _bioTextBox;

    // ── 玩家档案滚动视窗状态 ──
    private Rectangle _playerViewportRect;
    private Rectangle _scrollbarTrackRect;
    private int _scrollOffset = 0;
    private int _totalContentHeight = 0;
    private bool _isDraggingScrollbar = false;

    // ── 玩家档案内容控件虚拟区域 ──
    private Rectangle _enableProfileCheckboxRect;
    private Rectangle _orientationLabelRect;
    private Rectangle _orientationDropdownRect;
    private Rectangle _safetyLabelRect;
    private Rectangle _safetySliderRect;
    private Rectangle _safetyQuoteLineRect;
    private List<string> _safetyDescLines = new();
    private Rectangle _bioLabelRect;
    private Rectangle _bioBoxRect;
    private Rectangle _saveButtonRect;

    // ── 子页签（加长并居中） ──
    private int _profileSubTab = 0;
    private Rectangle _subTabPlayerRect;
    private Rectangle _subTabNpcRect;

    // ── NPC 宫格相关 ──
    private int _npcGridPage;
    private bool _filterCustomOnly;
    private Rectangle _filterCheckboxRect;
    private Rectangle _prevPageBtnRect;
    private Rectangle _nextPageBtnRect;

    private sealed class NpcCardInfo
    {
        public string Name;
        public string DisplayName;
        public Texture2D Portrait;
        public Rectangle DefaultSourceRect;
        public Rectangle SmileSourceRect;
        public bool HasCustomOverlay;
    }

    private List<NpcCardInfo> _allNpcCards = new();
    private List<NpcCardInfo> _displayNpcCards = new();
    private readonly List<(NpcCardInfo Card, Rectangle Bounds, Rectangle ResetBtnBounds)> _visibleCardSlots = new();

    public ProfileTabView(IntegratedHubMenu hub) : base(hub) { }

    public override void OnActivated() { }

    public override void OnDeactivated()
    {
        _isDraggingScrollbar = false;
        if (_bioTextBox != null && Game1.keyboardDispatcher.Subscriber == _bioTextBox)
            Game1.keyboardDispatcher.Subscriber = null;
        _orientationDropdown?.Close();
    }

    public override void OnReturnedFromChild()
    {
        RecalculateTab2Layout();
        if (_profileSubTab == 1)
            RefreshNpcCards();
    }

    public override void Layout(Rectangle menuBounds, Rectangle contentBounds)
    {
        base.Layout(menuBounds, contentBounds);
        if (_tab2Initialized)
            RecalculateTab2Layout();
    }

    public override void Update(GameTime time)
    {
        base.Update(time);

        if (_isDraggingScrollbar && Mouse.GetState().LeftButton == ButtonState.Released)
        {
            _isDraggingScrollbar = false;
        }

        if (_profileSubTab == 0 && _bioTextBox != null)
        {
            _bioTextBox.Position = new Vector2(_bioBoxRect.X, _bioBoxRect.Y - _scrollOffset);
            _bioTextBox.Update(time);
        }
    }

    public override bool ReceiveLeftClick(int x, int y)
    {
        if (_orientationDropdown != null && _orientationDropdown.ReceiveLeftClick(x, y))
            return true;

        if (_subTabPlayerRect.Contains(x, y))
        {
            if (_profileSubTab != 0)
            {
                _profileSubTab = 0;
                _isDraggingScrollbar = false;
                Game1.playSound("smallSelect");
            }
            return true;
        }
        if (_subTabNpcRect.Contains(x, y))
        {
            if (_profileSubTab != 1)
            {
                _profileSubTab = 1;
                _isDraggingScrollbar = false;
                _orientationDropdown?.Close();
                if (_bioTextBox != null && Game1.keyboardDispatcher.Subscriber == _bioTextBox)
                    Game1.keyboardDispatcher.Subscriber = null;
                Game1.playSound("smallSelect");
                RefreshNpcCards();
            }
            return true;
        }

        if (_profileSubTab == 1)
            return HandleTab2NpcPageClick(x, y);

        // ── 玩家档案页交互 ──

        // 保存按钮
        if (_saveButtonRect.Contains(x, y))
        {
            SaveTab2();
            return true;
        }

        // 滚动条拖拽判定
        int maxScroll = Math.Max(0, _totalContentHeight - _playerViewportRect.Height);
        if (maxScroll > 0 && _scrollbarTrackRect.Contains(x, y))
        {
            _isDraggingScrollbar = true;
            _orientationDropdown?.Close();
            UpdateScrollFromMouse(y);
            return true;
        }

        // 视口控件交互判定
        if (_playerViewportRect.Contains(x, y))
        {
            int off = _scrollOffset;

            // 启用档案复选框
            var actualChkHit = new Rectangle(_enableProfileCheckboxRect.X, _enableProfileCheckboxRect.Y - off, _enableProfileCheckboxRect.Width + 300, _enableProfileCheckboxRect.Height);
            if (actualChkHit.Contains(x, y))
            {
                ModEntry.Config.EnablePlayerProfile = !ModEntry.Config.EnablePlayerProfile;
                Game1.playSound("select");
                RecalculateTab2Layout();
                return true;
            }

            if (!ModEntry.Config.EnablePlayerProfile)
                return false;

            // 性取向下拉菜单 Header（居中区域）
            var actualDropHit = new Rectangle(_orientationDropdownRect.X, _orientationDropdownRect.Y - off, _orientationDropdownRect.Width, _orientationDropdownRect.Height);
            if (_orientationDropdown != null && actualDropHit.Contains(x, y))
            {
                _orientationDropdown.ToggleOpen();
                Game1.playSound("shwip");
                return true;
            }

            // 安全滑块（居中区域）
            var actualSliderHit = new Rectangle(_safetySliderRect.X, _safetySliderRect.Y - off, _safetySliderRect.Width, _safetySliderRect.Height);
            if (actualSliderHit.Contains(x, y))
            {
                int trackX = actualSliderHit.X;
                int trackW = actualSliderHit.Width;
                int relativeX = Math.Clamp(x - trackX, 0, trackW);
                int newIdx = Math.Min(3, (int)(((float)relativeX / trackW) * 4));
                if (newIdx != _safetyModeIndex)
                {
                    _safetyModeIndex = newIdx;
                    Game1.playSound("select");
                    RecalculateTab2Layout();
                }
                return true;
            }

            // Bio 文本框
            var actualBioHit = new Rectangle(_bioBoxRect.X, _bioBoxRect.Y - off, _bioBoxRect.Width, _bioBoxRect.Height);
            if (actualBioHit.Contains(x, y))
            {
                _bioTextBox.Selected = true;
                Game1.keyboardDispatcher.Subscriber = _bioTextBox;
                _bioTextBox.ReceiveLeftClick(x, y);
                return true;
            }
            else
            {
                if (_bioTextBox != null && Game1.keyboardDispatcher.Subscriber == _bioTextBox)
                    Game1.keyboardDispatcher.Subscriber = null;
            }
        }
        else
        {
            if (_bioTextBox != null && Game1.keyboardDispatcher.Subscriber == _bioTextBox)
                Game1.keyboardDispatcher.Subscriber = null;
        }

        return false;
    }

    public override bool ReceiveScrollWheel(int direction)
    {
        if (_profileSubTab == 1)
        {
            int totalPages = GetNpcTotalPages();
            if (direction < 0 && _npcGridPage < totalPages - 1)
            {
                _npcGridPage++;
                Game1.playSound("shwip");
                RecalculateNpcGridLayout();
                return true;
            }
            else if (direction > 0 && _npcGridPage > 0)
            {
                _npcGridPage--;
                Game1.playSound("shwip");
                RecalculateNpcGridLayout();
                return true;
            }
            return false;
        }

        if (_orientationDropdown != null && _orientationDropdown.IsOpen)
        {
            _orientationDropdown.ReceiveScrollWheel(direction);
            return true;
        }

        var actualBioBox = new Rectangle(_bioBoxRect.X, _bioBoxRect.Y - _scrollOffset, _bioBoxRect.Width, _bioBoxRect.Height);
        int mx = Game1.getMouseX();
        int my = Game1.getMouseY();
        if (actualBioBox.Contains(mx, my))
        {
            _bioTextBox?.ReceiveScrollWheel(direction);
            return true;
        }

        int maxScroll = Math.Max(0, _totalContentHeight - _playerViewportRect.Height);
        if (maxScroll > 0)
        {
            int newScroll = Math.Clamp(_scrollOffset - (direction > 0 ? 44 : -44), 0, maxScroll);
            if (newScroll != _scrollOffset)
            {
                _scrollOffset = newScroll;
                _orientationDropdown?.Close();
                Game1.playSound("shwip");
                return true;
            }
        }

        return false;
    }

    public override bool ReceiveKeyPress(Keys key)
    {
        if (_bioTextBox != null && Game1.keyboardDispatcher.Subscriber == _bioTextBox)
        {
            if (key == Keys.Escape || key == Keys.Enter)
            {
                _bioTextBox.Selected = false;
                Game1.keyboardDispatcher.Subscriber = null;
                return true;
            }
            if (!DialogueTextInputBox.IsControlKeyDown())
                _bioTextBox.RecieveSpecialInput(key);
            return true;
        }

        if (key == Keys.Escape && _orientationDropdown != null && _orientationDropdown.IsOpen)
        {
            _orientationDropdown.Close();
            return true;
        }

        return false;
    }

    public override void LeftClickHeld(int x, int y)
    {
        if (_isDraggingScrollbar)
        {
            UpdateScrollFromMouse(y);
            return;
        }

        if (_orientationDropdown != null && !_orientationDropdown.IsOpen)
        {
            var actualSliderHit = new Rectangle(_safetySliderRect.X, _safetySliderRect.Y - _scrollOffset, _safetySliderRect.Width, _safetySliderRect.Height);
            if (actualSliderHit.Contains(x, y))
            {
                int trackX = actualSliderHit.X;
                int trackW = actualSliderHit.Width;
                int relativeX = Math.Clamp(x - trackX, 0, trackW);
                int idx = Math.Min(3, (int)(((float)relativeX / trackW) * 4));
                if (idx != _safetyModeIndex)
                {
                    _safetyModeIndex = idx;
                    Game1.playSound("shwip");
                    RecalculateTab2Layout();
                }
            }
        }
    }

    public override void Draw(SpriteBatch b, int mx, int my)
    {
        SetHoveredTooltip("");
        DrawTab2(b, mx, my);
    }

    public override void DrawOverlay(SpriteBatch b)
    {
        if (_profileSubTab == 0 && _orientationDropdown != null && _orientationDropdown.IsOpen)
        {
            _orientationDropdown.Draw(b);
        }
    }

    public override void RefreshFromHub()
    {
        if (!_tab2Initialized)
            InitializeTab2();
        if (_profileSubTab == 1)
            RefreshNpcCards();
        if (_bioTextBox != null && _bioTextBox.Selected && Game1.keyboardDispatcher.Subscriber == null)
            Game1.keyboardDispatcher.Subscriber = _bioTextBox;
    }

    private void InitializeTab2()
    {
        _orientationItems = new List<(string, string)>
        {
            ("", I18n.Profile.OrientationNone()),
            ("Heterosexual", I18n.Profile.OrientationHeterosexual()),
            ("Homosexual", I18n.Profile.OrientationHomosexual()),
            ("Bisexual", I18n.Profile.OrientationBisexual()),
            ("Asexual", I18n.Profile.OrientationAsexual()),
        };

        string current = PlayerProfileManager.TryGetCustomOrientation(out var stored)
            ? (stored ?? "")
            : (ModEntry.Config.PlayerSexualOrientation ?? "");
        string selectedId = "";
        foreach (var item in _orientationItems)
        {
            if (string.Equals(item.Id, current, StringComparison.OrdinalIgnoreCase))
            {
                selectedId = item.Id;
                break;
            }
        }

        _safetyModeIndex = Math.Clamp((int)ModEntry.Config.RomanceSafetyMode, 0, 3);

        _orientationDropdown = new DropdownList(Rectangle.Empty)
        {
            OnItemSelected = id => Game1.playSound("select")
        };
        _orientationDropdown.SetItems(_orientationItems, selectedId);

        _bioTextBox = new DialogueTextInputBox(300)
        {
            UseCustomFont = true,
            CustomFontSize = HubUi.RegularFontSize,
            CounterFontSize = HubUi.SmallFontSize + 1,
            DrawFrame = true,
            ShowCharacterCount = true,
            AllowNewlines = true,
            Selected = false
        };
        _bioTextBox.SetText(PlayerProfileManager.GetCustomBio() ?? string.Empty);

        RecalculateTab2Layout();
        RefreshNpcCards();
        _tab2Initialized = true;
    }

    private void RecalculateTab2Layout()
    {
        int leftColX = MenuBounds.X + LeftPadding;
        int contentW = MenuBounds.Width - LeftPadding - RightPadding;

        // ── 1. 子页签按钮显著加长并强制居中 ──
        int subTabY = MenuBounds.Y + TopPadding;
        int subTabW = 240; // 按钮加长至 240px
        int tabGap = 16;
        int totalSubTabsW = subTabW * 2 + tabGap;
        int subTabsStartX = MenuBounds.X + (MenuBounds.Width - totalSubTabsW) / 2; // 整体居中

        _subTabPlayerRect = new Rectangle(subTabsStartX, subTabY, subTabW, SubTabBarH);
        _subTabNpcRect = new Rectangle(subTabsStartX + subTabW + tabGap, subTabY, subTabW, SubTabBarH);

        _filterCheckboxRect = new Rectangle(leftColX + contentW - 170, subTabY + 2, 28, 28);

        // ── 底部保存按钮（独立吸底，气派居中） ──
        int saveW = 190;
        int saveH = 42;
        _saveButtonRect = new Rectangle(MenuBounds.X + (MenuBounds.Width - saveW) / 2, MenuBounds.Y + MenuBounds.Height - 64, saveW, saveH);

        // ── 玩家档案视口（横向满宽） ──
        int viewportTopY = subTabY + SubTabBarH + 20;
        int viewportBottomY = _saveButtonRect.Y - 16;
        int scrollbarW = 6;
        int scrollbarRightPad = 10;

        _playerViewportRect = new Rectangle(leftColX, viewportTopY, contentW - scrollbarW - scrollbarRightPad, Math.Max(180, viewportBottomY - viewportTopY));

        int trackX = _playerViewportRect.Right + scrollbarRightPad - 2;
        int trackY = _playerViewportRect.Y + 4;
        int trackH = _playerViewportRect.Height - 8;
        _scrollbarTrackRect = new Rectangle(trackX, trackY, scrollbarW, trackH);

        // ── 内部控件自适应排版 ──
        int innerX = _playerViewportRect.X + 4;
        int innerW = _playerViewportRect.Width - 8;
        int curY = _playerViewportRect.Y;

        _enableProfileCheckboxRect = new Rectangle(innerX, curY, 27, 27);
        curY += 46;

        if (ModEntry.Config.EnablePlayerProfile)
        {
            // ── 2. 性取向下拉菜单尺寸减少 50%，强制水平居中 ──
            const int dropdownH = 38;
            int dropdownW = Math.Min(460, (int)(innerW * 0.65f));
            int dropdownX = _playerViewportRect.X + (_playerViewportRect.Width - dropdownW) / 2; // 强制水平居中

            _orientationLabelRect = new Rectangle(dropdownX, curY, dropdownW, 22);
            curY += 28;

            _orientationDropdownRect = new Rectangle(dropdownX, curY, dropdownW, dropdownH);
            curY += dropdownH + 28;

            // ── 3. 安全滑块尺寸减少 50%，强制水平居中 ──
            int sliderW = Math.Min(240, innerW / 2); // 尺寸减少约 50%
            int sliderX = _playerViewportRect.X + (_playerViewportRect.Width - sliderW) / 2; // 强制水平居中

            _safetyLabelRect = new Rectangle(sliderX, curY, sliderW, 22);
            curY += 28;

            _safetySliderRect = new Rectangle(sliderX, curY, sliderW, 24);
            curY += 34;

            // 档位名称占位
            curY += 28;

            // ── 多语言自适应“琥珀金引用线”区域 ──
            string rawDesc = I18n.Profile.SafetyDesc();
            float descMaxWidth = innerW - 28;
            _safetyDescLines = WrapTextForDisplay(rawDesc, descMaxWidth, HubUi.RegularFontSize);

            int lineH = (int)MathF.Ceiling(CustomFontManager.MeasureString("Ag", HubUi.RegularFontSize).Y) + 5;
            int descTotalTextH = Math.Max(lineH, _safetyDescLines.Count * lineH);

            _safetyQuoteLineRect = new Rectangle(innerX, curY, innerW, descTotalTextH + 8);
            curY += _safetyQuoteLineRect.Height + 28;

            // ── 4. Bio 签名文本框（高度增加 50%：150px -> 225px） ──
            _bioLabelRect = new Rectangle(innerX, curY, innerW, 22);
            curY += 28;

            int bioBoxH = 225; // 高度继续增加 50%
            _bioBoxRect = new Rectangle(innerX, curY, innerW, bioBoxH);
            curY += bioBoxH + 24;
        }
        else
        {
            curY += 40;
        }

        _totalContentHeight = curY - _playerViewportRect.Y;

        int maxScroll = Math.Max(0, _totalContentHeight - _playerViewportRect.Height);
        _scrollOffset = Math.Clamp(_scrollOffset, 0, maxScroll);

        if (_bioTextBox != null)
        {
            _bioTextBox.Position = new Vector2(_bioBoxRect.X, _bioBoxRect.Y - _scrollOffset);
            _bioTextBox.Extent = new Vector2(_bioBoxRect.Width, _bioBoxRect.Height);
            _bioTextBox.InvalidateLayout();
        }

        var actualDropdown = new Rectangle(_orientationDropdownRect.X, _orientationDropdownRect.Y - _scrollOffset, _orientationDropdownRect.Width, _orientationDropdownRect.Height);
        _orientationDropdown?.SetHeaderBounds(actualDropdown);

        RecalculateNpcGridLayout();
    }

    private static List<string> WrapTextForDisplay(string text, float maxWidth, float fontSize)
    {
        var lines = new List<string>();
        if (string.IsNullOrWhiteSpace(text))
            return lines;

        maxWidth = Math.Max(40f, maxWidth);
        string normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        string[] paragraphs = normalized.Split('\n');

        foreach (var para in paragraphs)
        {
            if (string.IsNullOrEmpty(para))
            {
                lines.Add("");
                continue;
            }

            if (CustomFontManager.MeasureString(para, fontSize).X <= maxWidth)
            {
                lines.Add(para);
                continue;
            }

            if (para.Contains(' '))
            {
                string[] words = para.Split(' ');
                string curLine = "";

                foreach (var word in words)
                {
                    string testLine = string.IsNullOrEmpty(curLine) ? word : curLine + " " + word;
                    if (CustomFontManager.MeasureString(testLine, fontSize).X <= maxWidth)
                    {
                        curLine = testLine;
                    }
                    else
                    {
                        if (!string.IsNullOrEmpty(curLine))
                        {
                            lines.Add(curLine);
                            curLine = "";
                        }

                        if (CustomFontManager.MeasureString(word, fontSize).X > maxWidth)
                        {
                            string wordPart = "";
                            for (int c = 0; c < word.Length; c++)
                            {
                                string testPart = wordPart + word[c];
                                if (CustomFontManager.MeasureString(testPart, fontSize).X <= maxWidth)
                                {
                                    wordPart = testPart;
                                }
                                else
                                {
                                    lines.Add(wordPart);
                                    wordPart = word[c].ToString();
                                }
                            }
                            curLine = wordPart;
                        }
                        else
                        {
                            curLine = word;
                        }
                    }
                }

                if (!string.IsNullOrEmpty(curLine))
                    lines.Add(curLine);
            }
            else
            {
                string curLine = "";
                for (int c = 0; c < para.Length; c++)
                {
                    string testLine = curLine + para[c];
                    if (CustomFontManager.MeasureString(testLine, fontSize).X <= maxWidth)
                    {
                        curLine = testLine;
                    }
                    else
                    {
                        lines.Add(curLine);
                        curLine = para[c].ToString();
                    }
                }

                if (!string.IsNullOrEmpty(curLine))
                    lines.Add(curLine);
            }
        }

        return lines;
    }

    private void UpdateScrollFromMouse(int mouseY)
    {
        int maxScroll = Math.Max(0, _totalContentHeight - _playerViewportRect.Height);
        if (maxScroll <= 0 || _scrollbarTrackRect.Height <= 0) return;

        float visibleRatio = Math.Clamp((float)_playerViewportRect.Height / _totalContentHeight, 0.15f, 1f);
        int thumbH = Math.Max(24, (int)(_scrollbarTrackRect.Height * visibleRatio));
        if (_scrollbarTrackRect.Height <= thumbH) return;

        float progress = Math.Clamp((float)(mouseY - _scrollbarTrackRect.Y - thumbH / 2) / (_scrollbarTrackRect.Height - thumbH), 0f, 1f);
        _scrollOffset = (int)Math.Round(progress * maxScroll);
    }

    private void SaveTab2()
    {
        Game1.playSound("select");

        PlayerProfileManager.SaveCustomOrientation(_orientationDropdown.SelectedId ?? "");
        ModEntry.Config.RomanceSafetyMode = (SafetyModeLevel)_safetyModeIndex;

        try
        {
            ModEntry.SHelper.WriteConfig(ModEntry.Config);
        }
        catch (Exception ex)
        {
            ModEntry.SMonitor.Log($"[Hub] config save failed: {ex.Message}", StardewModdingAPI.LogLevel.Error);
            return;
        }

        string bioText = _bioTextBox.Text ?? "";
        if (bioText.Length > 300)
            bioText = bioText.Substring(0, 300);

        PlayerProfileManager.SaveCustomBio(bioText);
        Game1.addHUDMessage(new HUDMessage(I18n.Profile.SavedHud(), 2));
    }

    private void DrawTab2(SpriteBatch b, int mx, int my)
    {
        DrawTab2SubTab(b, _subTabPlayerRect, "玩家档案", _profileSubTab == 0, mx, my);
        DrawTab2SubTab(b, _subTabNpcRect, "NPC 档案", _profileSubTab == 1, mx, my);

        if (_profileSubTab == 1)
        {
            DrawTab2NpcGridPage(b);
            return;
        }

        // 1. 剪裁视口，渲染通透无框内容
        Rectangle prevScissor = b.GraphicsDevice.ScissorRectangle;
        RasterizerState prevRasterizer = b.GraphicsDevice.RasterizerState;

        b.End();
        Rectangle scissor = Rectangle.Intersect(_playerViewportRect, b.GraphicsDevice.Viewport.Bounds);
        b.GraphicsDevice.ScissorRectangle = scissor;

        RasterizerState scissorState = new RasterizerState
        {
            ScissorTestEnable = true,
            CullMode = CullMode.None
        };

        b.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp, DepthStencilState.None, scissorState);

        DrawPlayerProfileContent(b, mx, my);

        b.End();
        b.GraphicsDevice.ScissorRectangle = prevScissor;
        b.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp, DepthStencilState.None, prevRasterizer);

        // 2. 绘制右侧专属滚动条
        int maxScroll = Math.Max(0, _totalContentHeight - _playerViewportRect.Height);
        if (maxScroll > 0)
        {
            DrawScrollbarVisual(b, _scrollbarTrackRect, _playerViewportRect.Height, _totalContentHeight, _scrollOffset, _isDraggingScrollbar, mx, my);
        }

        // 3. 底部固定保存按钮
        DrawSaveButton(b, mx, my);
    }

    private void DrawPlayerProfileContent(SpriteBatch b, int mx, int my)
    {
        int off = _scrollOffset;

        bool enabled = ModEntry.Config.EnablePlayerProfile;
        Rectangle enableSrc = enabled ? new Rectangle(236, 425, 9, 9) : new Rectangle(227, 425, 9, 9);
        var chkPos = new Vector2(_enableProfileCheckboxRect.X, _enableProfileCheckboxRect.Y - off);
        b.Draw(Game1.mouseCursors, chkPos, enableSrc, Color.White, 0f, Vector2.Zero, 3f, SpriteEffects.None, 1f);

        CustomFontManager.DrawStringBold(b, I18n.Profile.EnableProfile(),
            new Vector2(chkPos.X + 36, chkPos.Y + (_enableProfileCheckboxRect.Height - CustomFontManager.MeasureStringBold("A", HubUi.RegularFontSize).Y) / 2f),
            RulesTheme.TextPrimary, HubUi.RegularFontSize);

        if (!enabled)
        {
            string hint = "勾选上方选项以启用个性化档案，并在与村民的交互中生效。";
            CustomFontManager.DrawString(b, hint, new Vector2(chkPos.X, chkPos.Y + 40), RulesTheme.TextMuted, HubUi.RegularFontSize);
            return;
        }

        // 性取向配置（居中）
        var orientLabelPos = new Vector2(_orientationLabelRect.X, _orientationLabelRect.Y - off);
        CustomFontManager.DrawStringBold(b, I18n.Profile.OrientationLabel(), orientLabelPos, RulesTheme.TextPrimary, HubUi.RegularFontSize);

        var actualDropdownRect = new Rectangle(_orientationDropdownRect.X, _orientationDropdownRect.Y - off, _orientationDropdownRect.Width, _orientationDropdownRect.Height);
        _orientationDropdown?.SetHeaderBounds(actualDropdownRect);
        if (_orientationDropdown != null && !_orientationDropdown.IsOpen)
        {
            _orientationDropdown.Draw(b);
        }

        // 浪漫安全模式标题（居中）
        var safetyLabelPos = new Vector2(_safetyLabelRect.X, _safetyLabelRect.Y - off);
        CustomFontManager.DrawStringBold(b, I18n.Profile.RomanceSafetyLabel(), safetyLabelPos, RulesTheme.TextPrimary, HubUi.RegularFontSize);

        // 安全滑块本体（减半居中）
        int trackX = _safetySliderRect.X;
        int trackW = _safetySliderRect.Width;
        int trackY = _safetySliderRect.Y - off;

        b.Draw(Game1.staminaRect, new Rectangle(trackX + 2, trackY + 2, trackW - 4, 20), RulesTheme.SurfaceSunken);
        IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(403, 383, 6, 6),
            trackX, trackY, trackW, 24, Color.White, 4f, false);

        for (int t = 0; t < 4; t++)
        {
            int tickX = trackX + (int)(t * ((float)trackW / 3f));
            b.Draw(Game1.mouseCursors,
                new Rectangle(tickX - 2, trackY + 4, 4, 16),
                new Rectangle(240, 428, 12, 12), new Color(180, 180, 180));
        }

        int thumbX = trackX + (int)(_safetyModeIndex * ((float)trackW / 3f)) - 12;
        b.Draw(Game1.mouseCursors,
            new Rectangle(thumbX, trackY - 8, 24, 40),
            new Rectangle(435, 463, 6, 10), Color.White);

        // 当前档位名称（琥珀金居中高亮）
        string[] safetyLabels = GetSafetyModeLabels();
        string currentLabel = $"★  {safetyLabels[_safetyModeIndex]}";
        var labelSize = CustomFontManager.MeasureStringBold(currentLabel, HubUi.RegularFontSize);
        float labelX = trackX + (trackW - labelSize.X) / 2f;
        int labelY = trackY + 28;

        CustomFontManager.DrawStringBold(b, currentLabel, new Vector2(labelX, labelY), RulesTheme.AccentAmber, HubUi.RegularFontSize);

        // ── 说明文本逐行居中渲染 ──
        int quoteY = _safetyQuoteLineRect.Y - off;
        int lineSpacing = (int)MathF.Ceiling(CustomFontManager.MeasureString("Ag", HubUi.RegularFontSize).Y) + 5;

        for (int i = 0; i < _safetyDescLines.Count; i++)
        {
            string line = _safetyDescLines[i];
            float lineW = CustomFontManager.MeasureString(line, HubUi.RegularFontSize).X;
            float lineX = _playerViewportRect.X + (_playerViewportRect.Width - lineW) / 2f;
            CustomFontManager.DrawString(b, line, new Vector2(lineX, quoteY + i * lineSpacing), RulesTheme.TextSecondary, HubUi.RegularFontSize);
        }

        // Bio 文本框（225px 饱满大高度）
        var bioLabelPos = new Vector2(_bioLabelRect.X, _bioLabelRect.Y - off);
        CustomFontManager.DrawStringBold(b, I18n.Profile.BioLabel(), bioLabelPos, RulesTheme.TextPrimary, HubUi.RegularFontSize);

        var bioDrawBox = new Rectangle(_bioBoxRect.X, _bioBoxRect.Y - off, _bioBoxRect.Width, _bioBoxRect.Height);
        _bioTextBox.Position = new Vector2(bioDrawBox.X, bioDrawBox.Y);
        _bioTextBox.Draw(b);

        if (string.IsNullOrWhiteSpace(_bioTextBox.Text))
        {
            CustomFontManager.DrawString(b, I18n.Profile.BioPlaceholder(),
                new Vector2(bioDrawBox.X + 16, bioDrawBox.Y + 14),
                RulesTheme.TextMuted, HubUi.RegularFontSize);
        }
    }

    private static void DrawScrollbarVisual(SpriteBatch b, Rectangle trackRect, int visibleHeight, int totalHeight, int scrollOffset, bool isDragging, int mx, int my)
    {
        if (totalHeight <= visibleHeight || trackRect.Height <= 0) return;

        b.Draw(Game1.staminaRect, trackRect, RulesTheme.SurfaceSunken * 0.8f);
        b.Draw(Game1.staminaRect, new Rectangle(trackRect.X, trackRect.Y, 1, trackRect.Height), RulesTheme.BorderSoft * 0.5f);

        int maxScroll = totalHeight - visibleHeight;
        float visibleRatio = Math.Clamp((float)visibleHeight / totalHeight, 0.15f, 1f);
        int thumbH = Math.Max(24, (int)(trackRect.Height * visibleRatio));
        int thumbY = trackRect.Y + (int)((trackRect.Height - thumbH) * ((float)scrollOffset / maxScroll));
        var thumbRect = new Rectangle(trackRect.X - 1, thumbY, trackRect.Width + 2, thumbH);

        bool thumbHover = thumbRect.Contains(mx, my);
        Color thumbBg = isDragging ? RulesTheme.BorderBold
                      : (thumbHover ? RulesTheme.AccentGold : RulesTheme.BorderMid);

        b.Draw(Game1.staminaRect, thumbRect, thumbBg);
        IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(403, 383, 6, 6),
            thumbRect.X, thumbRect.Y, thumbRect.Width, thumbRect.Height, RulesTheme.BorderBold, 1f, false);
    }

    private void DrawSaveButton(SpriteBatch b, int mx, int my)
    {
        bool isMouseDown = Mouse.GetState().LeftButton == ButtonState.Pressed;
        bool saveHover = _saveButtonRect.Contains(mx, my);
        bool savePressed = saveHover && isMouseDown;
        int pressOffset = savePressed ? 1 : 0;

        Color saveBg = savePressed ? RulesTheme.SurfaceSunken
                     : saveHover ? RulesTheme.SurfaceHover
                     : RulesTheme.SurfaceActive;

        Color saveBorder = savePressed ? RulesTheme.BorderBold
                         : saveHover ? RulesTheme.BorderBold
                         : RulesTheme.BorderMid;

        if (!savePressed)
        {
            b.Draw(Game1.staminaRect,
                new Rectangle(_saveButtonRect.X + 1, _saveButtonRect.Y + 2, _saveButtonRect.Width, _saveButtonRect.Height),
                RulesTheme.Shadow);
        }

        var drawRect = new Rectangle(_saveButtonRect.X, _saveButtonRect.Y + pressOffset, _saveButtonRect.Width, _saveButtonRect.Height);

        b.Draw(Game1.staminaRect, new Rectangle(drawRect.X + 1, drawRect.Y + 1, drawRect.Width - 2, drawRect.Height - 2), saveBg);
        IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(432, 439, 9, 9),
            drawRect.X, drawRect.Y, drawRect.Width, drawRect.Height, saveBorder, 2f, false);

        string saveText = I18n.Profile.SaveButton();
        var saveSize = CustomFontManager.MeasureStringBold(saveText, HubUi.TabFontSize);
        CustomFontManager.DrawStringBold(b, saveText,
            new Vector2(drawRect.X + (drawRect.Width - saveSize.X) / 2f, drawRect.Y + (drawRect.Height - saveSize.Y) / 2f),
            RulesTheme.TextPrimary, HubUi.TabFontSize);
    }

    private void DrawTab2SubTab(SpriteBatch b, Rectangle rect, string label, bool isActive, int mx, int my)
    {
        Color bg = isActive ? new Color(210, 180, 140)
                 : rect.Contains(mx, my) ? new Color(255, 235, 205)
                 : new Color(139, 90, 43);

        b.Draw(Game1.staminaRect, new Rectangle(rect.X + 2, rect.Y + 2, rect.Width - 4, rect.Height - 4), bg);
        IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(432, 439, 9, 9),
            rect.X, rect.Y, rect.Width, rect.Height, bg, 4f, false);

        var labelSize = CustomFontManager.MeasureStringBold(label, HubUi.TabFontSize);
        CustomFontManager.DrawStringBold(b, label,
            new Vector2(rect.X + (rect.Width - labelSize.X) / 2f, rect.Y + (rect.Height - labelSize.Y) / 2f),
            isActive ? Game1.textColor : Color.White * 0.95f, HubUi.TabFontSize);
    }

    private string[] GetSafetyModeLabels() => new[]
    {
        I18n.Profile.SafetyOff(),
        I18n.Profile.SafetyLoose(),
        I18n.Profile.SafetyModerate(),
        I18n.Profile.SafetyStrict()
    };

    private bool HandleTab2NpcPageClick(int x, int y)
    {
        var filterClickArea = new Rectangle(_filterCheckboxRect.X, _filterCheckboxRect.Y - 2, _filterCheckboxRect.Width + 140, _filterCheckboxRect.Height + 4);
        if (filterClickArea.Contains(x, y))
        {
            _filterCustomOnly = !_filterCustomOnly;
            _npcGridPage = 0;
            Game1.playSound("drumkit6");
            ApplyNpcFilter();
            return true;
        }

        int totalPages = GetNpcTotalPages();
        if (_prevPageBtnRect.Contains(x, y) && _npcGridPage > 0)
        {
            _npcGridPage--;
            Game1.playSound("shwip");
            RecalculateNpcGridLayout();
            return true;
        }
        if (_nextPageBtnRect.Contains(x, y) && _npcGridPage < totalPages - 1)
        {
            _npcGridPage++;
            Game1.playSound("shwip");
            RecalculateNpcGridLayout();
            return true;
        }

        foreach (var slot in _visibleCardSlots)
        {
            if (slot.Card.HasCustomOverlay && slot.ResetBtnBounds.Contains(x, y))
            {
                TryResetNpcBio(slot.Card.Name);
                return true;
            }

            if (slot.Bounds.Contains(x, y))
            {
                Game1.playSound("bigSelect");
                Hub.NotifyWillBeObscured();
                Game1.activeClickableMenu = new BioEditorMenu(slot.Card.Name, Hub);
                return true;
            }
        }

        return false;
    }

    private void TryResetNpcBio(string npcName)
    {
        if (string.IsNullOrEmpty(npcName) || ModEntry.BioStorage == null)
            return;

        if (!ModEntry.BioStorage.HasCustomOverlay(npcName))
        {
            Game1.playSound("cancel");
            return;
        }

        string target = npcName;
        string disp = Game1.getCharacterFromName(target)?.displayName ?? target;

        Game1.activeClickableMenu = new BioValveWarningDialog(
            Hub,
            "删除自定义人设",
            $"即将删除 {disp} 的自定义人设覆盖：",
            new List<string>
            {
                "该角色的全部自定义人设将被清除，恢复默认基准",
                "删除后无法找回，建议先在人设编辑器中导出备份"
            },
            "⚠ 确认删除",
            () =>
            {
                Game1.activeClickableMenu = Hub;
                if (!ModEntry.BioStorage.ResetOverlay(target, out string err))
                {
                    Game1.addHUDMessage(new HUDMessage(err, 3));
                }
                else
                {
                    Game1.playSound("throw");
                    RefreshNpcCards();
                }
            },
            "保留人设",
            () => { Game1.activeClickableMenu = Hub; });
    }

    private void RefreshNpcCards()
    {
        var cleanedCandidates = NpcCandidateQueryService.GetCleanedCandidates();
        var cards = new List<NpcCardInfo>();

        foreach (var candidate in cleanedCandidates)
        {
            var portrait = SafeLoadPortrait(candidate.Id);
            if (portrait == null || portrait.IsDisposed)
                continue;

            var smileRect = GetSmilingPortraitSource(portrait);
            if (smileRect.IsEmpty || smileRect.Width <= 0 || smileRect.Height <= 0)
                continue;

            var defaultRect = GetDefaultPortraitSource(portrait);
            bool hasCustom = ModEntry.BioStorage != null && ModEntry.BioStorage.HasCustomOverlay(candidate.Id);

            cards.Add(new NpcCardInfo
            {
                Name = candidate.Id,
                DisplayName = candidate.DisplayName,
                Portrait = portrait,
                DefaultSourceRect = defaultRect,
                SmileSourceRect = smileRect,
                HasCustomOverlay = hasCustom
            });
        }

        _allNpcCards = cards
            .OrderByDescending(x => x.HasCustomOverlay)
            .ThenBy(x => x.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        ApplyNpcFilter();
    }

    private void ApplyNpcFilter()
    {
        _displayNpcCards = _filterCustomOnly
            ? _allNpcCards.Where(x => x.HasCustomOverlay).ToList()
            : new List<NpcCardInfo>(_allNpcCards);

        int totalPages = GetNpcTotalPages();
        if (_npcGridPage >= totalPages)
            _npcGridPage = Math.Max(0, totalPages - 1);

        RecalculateNpcGridLayout();
    }

    private const int NpcCols = 5;
    private const int NpcRows = 2;
    private const int NpcItemsPerPage = NpcCols * NpcRows;

    private int GetNpcTotalPages() => Math.Max(1, (_displayNpcCards.Count + NpcItemsPerPage - 1) / NpcItemsPerPage);

    private void RecalculateNpcGridLayout()
    {
        _visibleCardSlots.Clear();

        int leftColX = MenuBounds.X + LeftPadding;
        int contentW = MenuBounds.Width - LeftPadding - RightPadding;
        int startY = MenuBounds.Y + TopPadding + 64; // 原来是 + 44，改为 + 64

        const int bottomPagingBarH = 40;
        const int bottomMargin = 8;
        int bottomLimitY = MenuBounds.Y + MenuBounds.Height - BottomPadding + 20; // 增加 + 20
        int availH = bottomLimitY - startY - bottomPagingBarH - bottomMargin;

        int cols = NpcCols;
        int rows = NpcRows;
        int perPage = cols * rows;

        const int gapX = 12;
        const int gapY = 12;

        int cardW = (contentW - (cols - 1) * gapX) / cols;
        int cardH = Math.Max(120, (availH - (rows - 1) * gapY) / rows);

        int startIndex = _npcGridPage * perPage;
        int count = Math.Min(perPage, _displayNpcCards.Count - startIndex);

        for (int i = 0; i < count; i++)
        {
            var card = _displayNpcCards[startIndex + i];
            int r = i / cols;
            int c = i % cols;

            int cx = leftColX + c * (cardW + gapX);
            int cy = startY + r * (cardH + gapY);
            var cardRect = new Rectangle(cx, cy, cardW, cardH);

            var resetRect = card.HasCustomOverlay
                ? new Rectangle(cardRect.Left + 6, cardRect.Top + 6, 26, 26)
                : Rectangle.Empty;

            _visibleCardSlots.Add((card, cardRect, resetRect));
        }

        int pagingY = bottomLimitY - bottomPagingBarH;
        int midX = MenuBounds.X + MenuBounds.Width / 2;
        _prevPageBtnRect = new Rectangle(midX - 130, pagingY, 44, 36);
        _nextPageBtnRect = new Rectangle(midX + 86, pagingY, 44, 36);
    }

    private void DrawTab2NpcGridPage(SpriteBatch b)
    {
        int mx = Game1.getMouseX();
        int my = Game1.getMouseY();

        var filterHoverArea = new Rectangle(_filterCheckboxRect.X, _filterCheckboxRect.Y - 2, _filterCheckboxRect.Width + 140, _filterCheckboxRect.Height + 4);
        bool filterHover = filterHoverArea.Contains(mx, my);

        Rectangle chkSrc = _filterCustomOnly ? new Rectangle(236, 425, 9, 9) : new Rectangle(227, 425, 9, 9);
        b.Draw(Game1.mouseCursors, new Vector2(_filterCheckboxRect.X, _filterCheckboxRect.Y),
            chkSrc, Color.White, 0f, Vector2.Zero, 3f, SpriteEffects.None, 1f);

        CustomFontManager.DrawStringBold(b, "仅看已自定义",
            new Vector2(_filterCheckboxRect.X + 32, _filterCheckboxRect.Y + 2),
            filterHover ? Game1.textColor : Color.DimGray, HubUi.RegularFontSize);

        if (filterHover)
        {
            SetHoveredTooltip("只显示已经配置过自定义人设覆盖的 NPC");
        }

        if (_visibleCardSlots.Count == 0)
        {
            string emptyText = _filterCustomOnly ? "暂无任何自定义覆盖的 NPC" : "未发现任何 NPC 档案";
            var sz = CustomFontManager.MeasureString(emptyText, HubUi.RegularFontSize);
            CustomFontManager.DrawString(b, emptyText,
                new Vector2(MenuBounds.X + (MenuBounds.Width - sz.X) / 2f, MenuBounds.Y + 260), Color.Gray, HubUi.RegularFontSize);
        }

        bool isLeftMouseDown = Mouse.GetState().LeftButton == ButtonState.Pressed;

        foreach (var slot in _visibleCardSlots)
        {
            var card = slot.Card;
            var rect = slot.Bounds;
            bool isHovered = rect.Contains(mx, my);

            b.Draw(Game1.staminaRect, new Rectangle(rect.X + 2, rect.Y + 2, rect.Width, rect.Height), Color.Black * 0.12f);
            b.Draw(Game1.staminaRect, new Rectangle(rect.X + 2, rect.Y + 2, rect.Width - 4, rect.Height - 4), isHovered ? new Color(255, 248, 235) : new Color(254, 247, 238));

            Color cardBorder = isHovered ? new Color(220, 185, 140) : new Color(210, 190, 165);
            IClickableMenu.drawTextureBox(b, Game1.mouseCursors,
                new Rectangle(432, 439, 9, 9),
                rect.X, rect.Y, rect.Width, rect.Height, cardBorder, 3.0f, false);

            if (isHovered)
            {
                b.Draw(Game1.staminaRect, rect, new Color(255, 215, 0) * 0.08f);
                SetHoveredTooltip($"{card.DisplayName} - 点击打开人设编辑器");
            }

            int portraitBoxSize = Math.Clamp(rect.Width - 20, 96, 120);
            int portraitX = rect.X + (rect.Width - portraitBoxSize) / 2;
            int portraitY = rect.Y + 10;
            var portraitBoxRect = new Rectangle(portraitX, portraitY, portraitBoxSize, portraitBoxSize);

            b.Draw(Game1.staminaRect,
                new Rectangle(portraitBoxRect.X + 2, portraitBoxRect.Y + 2, portraitBoxRect.Width, portraitBoxRect.Height),
                Color.Black * 0.18f);

            IClickableMenu.drawTextureBox(b, Game1.mouseCursors,
                new Rectangle(293, 360, 24, 24),
                portraitBoxRect.X, portraitBoxRect.Y,
                portraitBoxRect.Width, portraitBoxRect.Height,
                Color.White, 4f, false);

            var innerPortraitRect = new Rectangle(
                portraitBoxRect.X + 4,
                portraitBoxRect.Y + 4,
                portraitBoxRect.Width - 8,
                portraitBoxRect.Height - 8);

            Rectangle targetSourceRect = isHovered ? card.SmileSourceRect : card.DefaultSourceRect;

            if (card.Portrait != null && !targetSourceRect.IsEmpty)
            {
                b.Draw(card.Portrait, innerPortraitRect, targetSourceRect, Color.White);
            }
            else
            {
                string placeholder = card.DisplayName.Length > 0 ? card.DisplayName.Substring(0, 1) : "?";
                var psz = CustomFontManager.MeasureStringBold(placeholder, HubUi.TitleFontSize);
                CustomFontManager.DrawStringBold(b, placeholder,
                    new Vector2(innerPortraitRect.X + (innerPortraitRect.Width - psz.X) / 2f,
                                innerPortraitRect.Y + (innerPortraitRect.Height - psz.Y) / 2f),
                    Color.Gray, HubUi.TitleFontSize);
            }

            string dispName = CustomFontManager.TruncateString(card.DisplayName, HubUi.CardTitleFontSize, rect.Width - 12);
            var nameSz = CustomFontManager.MeasureStringBold(dispName, HubUi.CardTitleFontSize);

            float nameAreaH = rect.Bottom - portraitBoxRect.Bottom;
            float nameY = portraitBoxRect.Bottom + (nameAreaH - nameSz.Y) / 2f - 2;

            CustomFontManager.DrawStringBold(b, dispName,
                new Vector2(rect.X + (rect.Width - nameSz.X) / 2f, nameY),
                isHovered ? new Color(130, 45, 10) : Game1.textColor,
                HubUi.CardTitleFontSize);

            if (card.HasCustomOverlay)
            {
                string badgeText = "★ 自定义";
                Color badgeBg = new Color(34, 139, 34);
                var badgeSz = CustomFontManager.MeasureStringBold(badgeText, HubUi.SmallFontSize);
                int bw = (int)badgeSz.X + 8;
                int bh = (int)badgeSz.Y + 4;
                int bx = rect.Right - bw - 4;
                int by = rect.Top + 4;

                b.Draw(Game1.staminaRect, new Rectangle(bx, by, bw, bh), badgeBg * 0.95f);
                CustomFontManager.DrawStringBold(b, badgeText, new Vector2(bx + 4, by + 2), Color.White, HubUi.SmallFontSize);
            }

            if (card.HasCustomOverlay)
            {
                bool resetHover = slot.ResetBtnBounds.Contains(mx, my);
                if (resetHover)
                {
                    SetHoveredTooltip($"还原 {card.DisplayName} 为默认人设");
                }

                bool resetPressed = isLeftMouseDown && resetHover;
                IconState iconState = resetPressed ? IconState.Pressed : IconState.Normal;

                Color resetBg = resetHover ? new Color(255, 235, 205) : new Color(210, 180, 140) * 0.9f;
                b.Draw(Game1.staminaRect, new Rectangle(slot.ResetBtnBounds.X + 1, slot.ResetBtnBounds.Y + 1, slot.ResetBtnBounds.Width - 2, slot.ResetBtnBounds.Height - 2), resetBg);
                IClickableMenu.drawTextureBox(b, Game1.mouseCursors,
                    new Rectangle(432, 439, 9, 9),
                    slot.ResetBtnBounds.X, slot.ResetBtnBounds.Y,
                    slot.ResetBtnBounds.Width, slot.ResetBtnBounds.Height,
                    resetBg, 2.0f, false);

                if (ModEntry.CustomIcons is { IsDisposed: false })
                {
                    Rectangle restoreSrc = IconSource.Restore(IconTheme.Wood, iconState);
                    int iconTargetSize = 18;
                    int ix = slot.ResetBtnBounds.X + (slot.ResetBtnBounds.Width - iconTargetSize) / 2;
                    int iy = slot.ResetBtnBounds.Y + (slot.ResetBtnBounds.Height - iconTargetSize) / 2;
                    if (resetPressed)
                    {
                        iy += 1;
                    }

                    b.Draw(ModEntry.CustomIcons,
                        new Rectangle(ix, iy, iconTargetSize, iconTargetSize),
                        restoreSrc, Color.White);
                }
            }
        }

        int totalPages = GetNpcTotalPages();
        int curPage = _npcGridPage + 1;

        bool prevEnabled = _npcGridPage > 0;
        bool nextEnabled = _npcGridPage < totalPages - 1;

        DrawArrowButton(b, _prevPageBtnRect, isLeft: true, enabled: prevEnabled, mx, my);

        string pageInfo = $"第 {curPage} / {totalPages} 页 (共 {_displayNpcCards.Count} 人)";
        var pageInfoSz = CustomFontManager.MeasureStringBold(pageInfo, HubUi.RegularFontSize);
        CustomFontManager.DrawStringBold(b, pageInfo,
            new Vector2(MenuBounds.X + (MenuBounds.Width - pageInfoSz.X) / 2f, _prevPageBtnRect.Y + (_prevPageBtnRect.Height - pageInfoSz.Y) / 2f),
            Game1.textColor, HubUi.RegularFontSize);

        DrawArrowButton(b, _nextPageBtnRect, isLeft: false, enabled: nextEnabled, mx, my);
    }

    private static void DrawArrowButton(SpriteBatch b, Rectangle rect, bool isLeft, bool enabled, int mx, int my)
    {
        bool hover = enabled && rect.Contains(mx, my);
        Color bg = enabled
            ? (hover ? new Color(255, 235, 205) : new Color(139, 90, 43))
            : Color.Gray * 0.5f;

        b.Draw(Game1.staminaRect, new Rectangle(rect.X + 2, rect.Y + 2, rect.Width - 4, rect.Height - 4), bg);
        IClickableMenu.drawTextureBox(b, Game1.mouseCursors,
            new Rectangle(432, 439, 9, 9),
            rect.X, rect.Y, rect.Width, rect.Height, bg, 4f, false);

        Rectangle src = isLeft ? LeftArrowSource : RightArrowSource;
        float arrowScale = 2.4f;
        int iconW = (int)(src.Width * arrowScale);
        int iconH = (int)(src.Height * arrowScale);
        Vector2 iconPos = new Vector2(
            rect.X + (rect.Width - iconW) / 2f,
            rect.Y + (rect.Height - iconH) / 2f);

        Color tint = enabled ? (hover ? Color.White : Color.Wheat) : Color.Gray * 0.5f;
        b.Draw(Game1.mouseCursors, iconPos, src, tint, 0f, Vector2.Zero, arrowScale, SpriteEffects.None, 0.86f);
    }

    private static Rectangle GetSmilingPortraitSource(Texture2D portrait)
    {
        if (portrait == null) return Rectangle.Empty;

        if (portrait.Width >= 128 && portrait.Height >= 64)
        {
            return new Rectangle(64, 0, 64, 64);
        }
        if (portrait.Width >= 64 && portrait.Height >= 128)
        {
            return new Rectangle(0, 64, 64, 64);
        }
        return new Rectangle(0, 0, Math.Min(64, portrait.Width), Math.Min(64, portrait.Height));
    }

    private static Rectangle GetDefaultPortraitSource(Texture2D portrait)
    {
        if (portrait == null) return Rectangle.Empty;
        int w = Math.Min(64, portrait.Width);
        int h = Math.Min(64, portrait.Height);
        return new Rectangle(0, 0, w, h);
    }

    private static Texture2D SafeLoadPortrait(string npcName)
    {
        if (string.IsNullOrWhiteSpace(npcName)) return null;

        if (PortraitCache.TryGetValue(npcName, out var tex) && tex != null && !tex.IsDisposed)
            return tex;

        Texture2D loaded = null;
        var character = Game1.getCharacterFromName(npcName);
        if (character?.Portrait != null && !character.Portrait.IsDisposed)
        {
            loaded = character.Portrait;
        }
        else
        {
            try
            {
                loaded = Game1.content.Load<Texture2D>("Portraits\\" + npcName);
            }
            catch { }
        }

        PortraitCache[npcName] = loaded;
        return loaded;
    }
}