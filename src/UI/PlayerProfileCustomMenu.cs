using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewValley;
using StardewValley.Menus;
using System;
using System.Collections.Generic;
using StardewModdingAPI;

namespace ValleytalkReborn
{
    internal class PlayerProfileCustomMenu : IClickableMenu
    {
        private readonly string _npcName;

        // Menu dimensions (responsive sizing)
        private int _menuWidth;
        private int _menuHeight;

        private const int MenuWidthDefault = 900;
        private const int MenuHeightDefault = 650;
        private const int RowHeight = 48;
        private const int CheckboxSize = 36;
        private const int DropdownHeight = 40;
        private const int BioBoxHeight = 220;
        private const int SliderTrackHeight = 24;

        // Padding from viewport edges
        private const int ViewportPadding = 40;

        // Sexual orientation
        private readonly List<string> _orientationKeys = new List<string>
        {
            "Heterosexual", "Homosexual", "Bisexual", "Asexual"
        };
        private readonly List<string> _orientationOptions = new List<string>();
        private int _orientationIndex = 0;
        private bool _orientationDropdownOpen = false;

        // Romance safety mode
        private int _safetyModeIndex = 1;
        private readonly string[] _sliderLabels;
        private readonly string _safetyDesc;

        // Custom bio text box
        private DialogueTextInputBox _bioTextBox;
        private const int MaxBioLength = 300;
        private readonly string _bioPlaceholder;

        // Scroll support
        private ClickableTextureComponent _upArrow;
        private ClickableTextureComponent _downArrow;
        private ClickableTextureComponent _scrollbar;
        private Rectangle _scrollbarRunner;
        private int _scrollOffset;
        private bool _scrolling;
        private int _totalContentHeight = 600;

        // Cached rectangle positions for click detection (populated by UpdateLayout)
        private Rectangle _enableProfileCheckboxRect;
        private Rectangle _bioTextBoxRect;

        // Dropdown rectangles
        private Rectangle _orientationDropdownRect;
        private readonly List<Rectangle> _orientationDropdownItems = new List<Rectangle>();

        // Slider rectangles
        private Rectangle _sliderRect;
        private Rectangle _sliderThumbRect;
        private Rectangle _scissorRect;

        // Dropdown drawing base (Y position in content space)
        private int _orientationDropdownBaseY;

        // Dynamic column layout
        private int _leftMargin;
        private int _maxLabelWidth;
        private int _rightColumnX;
        private int _dropdownWidth;

        // Layout dirty flag - when true, UpdateLayout() is called at the start of draw()
        private bool _layoutDirty = true;

        public PlayerProfileCustomMenu(string npcName)
            : base(0, 0, MenuWidthDefault, MenuHeightDefault, true)
        {
            _npcName = npcName;

            // Calculate responsive menu dimensions based on viewport
            CalculateMenuDimensions();

            // Position the menu based on calculated dimensions
            xPositionOnScreen = (Game1.uiViewport.Width - _menuWidth) / 2;
            yPositionOnScreen = (Game1.uiViewport.Height - _menuHeight) / 2;

            // --- Load i18n translations with PProfile. prefix ---
            string noneOption = ModEntry.SHelper.Translation.Get("PProfile.UI.None").Default("(None)");

            _orientationOptions.Add(noneOption);
            foreach (var key in _orientationKeys)
            {
                string i18nKey = "PProfile.Orientation." + key;
                _orientationOptions.Add(ModEntry.SHelper.Translation.Get(i18nKey).Default(key));
            }

            _sliderLabels = new string[] {
                ModEntry.SHelper.Translation.Get("PProfile.Safety.Off").Default("1: Off"),
                ModEntry.SHelper.Translation.Get("PProfile.Safety.Loose").Default("2: Loose"),
                ModEntry.SHelper.Translation.Get("PProfile.Safety.Moderate").Default("3: Moderate"),
                ModEntry.SHelper.Translation.Get("PProfile.Safety.Strict").Default("4: Strict")
            };

            _safetyDesc = ModEntry.SHelper.Translation.Get("PProfile.Safety.Desc").Default("Protects you from unwanted romantic or flirtatious dialogue.\n- Off: Unrestricted.\n- Loose: Contextually appropriate.\n- Moderate: High-friendship NPCs subtle affection.\n- Strict: ONLY partners/spouses.");
            _bioPlaceholder = ModEntry.SHelper.Translation.Get("PProfile.UI.BioPlaceholder").Default("e.g: Former Joja accountant who loves hot coffee and hates rain. Recently moved to Stardew Valley to find a new purpose in life.");

            // 1. Calculate column layout
            CalculateColumnLayout();

            // 2. Instantiate text box
            _bioTextBox = new DialogueTextInputBox(MaxBioLength)
            {
                Position = new Vector2(_rightColumnX, yPositionOnScreen + 300),
                Extent = new Vector2(_dropdownWidth, BioBoxHeight),
                Font = Game1.smallFont,
                TextColor = Game1.textColor,
                Selected = false
            };

            // 3. Load config
            LoadFromConfig();

            _upArrow = new ClickableTextureComponent(
                new Rectangle(xPositionOnScreen + _menuWidth - 48, yPositionOnScreen + 80, 44, 48),
                Game1.mouseCursors, new Rectangle(421, 459, 11, 12), 4f);

            _downArrow = new ClickableTextureComponent(
                new Rectangle(xPositionOnScreen + _menuWidth - 48, yPositionOnScreen + _menuHeight - 56, 44, 48),
                Game1.mouseCursors, new Rectangle(421, 472, 11, 12), 4f);

            _scrollbarRunner = new Rectangle(
                xPositionOnScreen + _menuWidth - 32, yPositionOnScreen + 130, 12, _menuHeight - 186);

            _scrollbar = new ClickableTextureComponent(
                new Rectangle(_scrollbarRunner.X - 6, _scrollbarRunner.Y, 24, 40),
                Game1.mouseCursors, new Rectangle(435, 463, 6, 10), 4f);

            SetScrollbarPosition();
            exitFunction = () => { Game1.playSound("bigDeSelect"); };
        }

        private void CalculateColumnLayout()
        {
            _leftMargin = xPositionOnScreen + 40;

            float maxWidth = 0f;

            string enableLabel = ModEntry.SHelper.Translation.Get("PProfile.UI.EnableProfile").Default("Enable Player Profile");
            maxWidth = Math.Max(maxWidth, Game1.smallFont.MeasureString(enableLabel).X);

            string orientationLabel = ModEntry.SHelper.Translation.Get("PProfile.UI.SexualOrientation").Default("Sexual Orientation:");
            maxWidth = Math.Max(maxWidth, Game1.smallFont.MeasureString(orientationLabel).X);

            string safetyLabel = ModEntry.SHelper.Translation.Get("PProfile.UI.RomanceSafetyMode").Default("Romance Settings:");
            maxWidth = Math.Max(maxWidth, Game1.smallFont.MeasureString(safetyLabel).X);

            string bioLabel = ModEntry.SHelper.Translation.Get("PProfile.UI.CustomBio").Default("Custom Bio:");
            maxWidth = Math.Max(maxWidth, Game1.smallFont.MeasureString(bioLabel).X);

            _maxLabelWidth = (int)Math.Ceiling(maxWidth);
            _rightColumnX = _leftMargin + _maxLabelWidth + 30;
            _dropdownWidth = _menuWidth - (_rightColumnX - xPositionOnScreen) - 80;

            if (_dropdownWidth < 200)
                _dropdownWidth = 200;
        }

        private void CalculateMenuDimensions()
        {
            _menuWidth = Math.Min(MenuWidthDefault, Game1.uiViewport.Width - ViewportPadding * 2);
            _menuHeight = Math.Min(MenuHeightDefault, Game1.uiViewport.Height - ViewportPadding * 2);

            if (_menuWidth < 640)
                _menuWidth = 640;
            if (_menuHeight < 400)
                _menuHeight = 400;
        }

        public override void gameWindowSizeChanged(Rectangle oldBounds, Rectangle newBounds)
        {
            base.gameWindowSizeChanged(oldBounds, newBounds);

            CalculateMenuDimensions();

            xPositionOnScreen = (Game1.uiViewport.Width - _menuWidth) / 2;
            yPositionOnScreen = (Game1.uiViewport.Height - _menuHeight) / 2;

            CalculateColumnLayout();

            if (_bioTextBox != null)
            {
                _bioTextBox.Position = new Vector2(_rightColumnX, yPositionOnScreen + 300);
                _bioTextBox.Extent = new Vector2(_dropdownWidth, BioBoxHeight);
            }

            if (_upArrow != null)
            {
                _upArrow.bounds.X = xPositionOnScreen + _menuWidth - 48;
                _upArrow.bounds.Y = yPositionOnScreen + 80;
            }

            if (_downArrow != null)
            {
                _downArrow.bounds.X = xPositionOnScreen + _menuWidth - 48;
                _downArrow.bounds.Y = yPositionOnScreen + _menuHeight - 56;
            }

            _scrollbarRunner.X = xPositionOnScreen + _menuWidth - 32;
            _scrollbarRunner.Y = yPositionOnScreen + 130;
            _scrollbarRunner.Height = _menuHeight - 186;

            if (_scrollbar != null)
            {
                _scrollbar.bounds.X = _scrollbarRunner.X - 6;
                _scrollbar.bounds.Y = _scrollbarRunner.Y;
            }

            SetScrollbarPosition();
            _layoutDirty = true;
        }

        private void LoadFromConfig()
        {
            var config = ModEntry.Config;

            _orientationIndex = 0;
            if (!string.IsNullOrEmpty(config.PlayerSexualOrientation))
            {
                int idx = _orientationKeys.IndexOf(config.PlayerSexualOrientation);
                if (idx >= 0) _orientationIndex = idx + 1;
            }

            _safetyModeIndex = (int)config.RomanceSafetyMode;

            if (_bioTextBox != null)
            {
                if (!string.IsNullOrWhiteSpace(StardewModdingAPI.Constants.SaveFolderName))
                {
                    string path = $"data/{StardewModdingAPI.Constants.SaveFolderName}/PlayerProfile.json";
                    try
                    {
                        var saveData = ModEntry.SHelper.Data.ReadJsonFile<Dictionary<string, string>>(path);
                        
                        if (saveData != null && saveData.TryGetValue("PlayerCustomBio", out string savedBio))
                        {
                            _bioTextBox.SetText(savedBio);
                            ModEntry.SMonitor?.Log($"[ValleytalkReborn] Successfully loaded custom bio.", LogLevel.Trace);
                        }
                        else
                        {
                            _bioTextBox.SetText("");
                        }
                    }
                    catch (Exception ex)
                    {
                        ModEntry.SMonitor?.Log($"[ValleytalkReborn] Failed to read PlayerProfile.json: {ex.Message}", LogLevel.Error);
                        _bioTextBox.SetText("");
                    }
                }
                else
                {
                    _bioTextBox.SetText("");
                }
            }
        }

        public void SaveToConfig()
        {
            var config = ModEntry.Config;

            config.PlayerSexualOrientation = _orientationIndex > 0 ? _orientationKeys[_orientationIndex - 1] : "";
            config.RomanceSafetyMode = (SafetyModeLevel)_safetyModeIndex;

            // Save global settings to config.json
            ModEntry.SHelper.Data.WriteJsonFile("config.json", config);

            // Save save-specific custom bio
            if (!string.IsNullOrWhiteSpace(StardewModdingAPI.Constants.SaveFolderName))
            {
                string bioText = _bioTextBox.Text.Length > MaxBioLength
                    ? _bioTextBox.Text.Substring(0, MaxBioLength)
                    : _bioTextBox.Text;

                string path = $"data/{StardewModdingAPI.Constants.SaveFolderName}/PlayerProfile.json";
                
                try
                {
                    var saveData = new Dictionary<string, string>
                    {
                        { "PlayerCustomBio", bioText }
                    };
                    
                    ModEntry.SHelper.Data.WriteJsonFile(path, saveData);
                    ModEntry.SMonitor?.Log($"[ValleytalkReborn] Successfully saved custom bio.", LogLevel.Trace);
                }
                catch (Exception ex)
                {
                    ModEntry.SMonitor?.Log($"[ValleytalkReborn] Failed to save PlayerProfile.json: {ex.Message}", LogLevel.Error);
                }
            }
        }

        private void SetScrollbarPosition()
        {
            int visibleHeight = _menuHeight - 200;
            if (_totalContentHeight <= visibleHeight) return;
            float pct = (float)_scrollOffset / (_totalContentHeight - visibleHeight);
            _scrollbar.bounds.Y = _scrollbarRunner.Y + (int)(pct * (_scrollbarRunner.Height - _scrollbar.bounds.Height));
        }

        public override void receiveScrollWheelAction(int direction)
        {
            base.receiveScrollWheelAction(direction);

            if (_orientationDropdownOpen) return;

            _bioTextBox?.ReceiveScrollWheel(direction);

            int visibleHeight = _menuHeight - 200;
            if (_totalContentHeight <= visibleHeight) return;

            int oldOffset = _scrollOffset;

            if (direction > 0 && _scrollOffset > 0)
            {
                _scrollOffset = Math.Max(0, _scrollOffset - RowHeight);
                Game1.playSound("shwip");
            }
            else if (direction < 0 && _scrollOffset < _totalContentHeight - visibleHeight)
            {
                _scrollOffset = Math.Min(_totalContentHeight - visibleHeight, _scrollOffset + RowHeight);
                Game1.playSound("shwip");
            }

            if (_scrollOffset != oldOffset)
            {
                _layoutDirty = true;
            }

            SetScrollbarPosition();
        }

        public override void receiveLeftClick(int x, int y, bool playSound = true)
        {
            base.receiveLeftClick(x, y, playSound);

            int saveBtnY = yPositionOnScreen + _menuHeight - 60;
            if (x >= xPositionOnScreen + _menuWidth / 2 - 80 && x <= xPositionOnScreen + _menuWidth / 2 + 80
                && y >= saveBtnY && y <= saveBtnY + 44)
            {
                Game1.playSound("select");
                SaveToConfig();
                Game1.drawObjectDialogue(ModEntry.SHelper.Translation.Get("PProfile.UI.ProfileSaved").Default("Profile saved!"));
                exitThisMenu();
                return;
            }

            if (_enableProfileCheckboxRect.Contains(x, y))
            {
                ModEntry.Config.EnablePlayerProfile = !ModEntry.Config.EnablePlayerProfile;
                Game1.playSound("select");

                if (!ModEntry.Config.EnablePlayerProfile)
                {
                    _scrollOffset = 0;
                    SetScrollbarPosition();
                }
                _layoutDirty = true;
                return;
            }

            if (!ModEntry.Config.EnablePlayerProfile)
                return;

            if (_orientationDropdownOpen)
            {
                if (ProcessDropdownClick(_orientationDropdownItems, ref _orientationIndex, ref _orientationDropdownOpen, x, y)) return;
                if (_orientationDropdownRect.Contains(x, y)) { _orientationDropdownOpen = false; Game1.playSound("select"); return; }

                CloseAllDropdowns();
                return;
            }

            if (_orientationDropdownRect.Contains(x, y)) { _orientationDropdownOpen = true; Game1.playSound("select"); return; }

            int trackWidth = Math.Min(220, _dropdownWidth);
            if (_sliderRect.Contains(x, y))
            {
                int relativeX = Math.Max(0, Math.Min(trackWidth, x - _sliderRect.X));
                _safetyModeIndex = Math.Min(3, (int)(((float)relativeX / trackWidth) * 4));
                _layoutDirty = true;
                Game1.playSound("select");
                return;
            }

            if (_bioTextBoxRect.Contains(x, y))
            {
                _bioTextBox.Selected = true;
                Game1.keyboardDispatcher.Subscriber = _bioTextBox;
                _bioTextBox.ReceiveLeftClick(x, y);
                return;
            }

            int visibleHeight = _menuHeight - 200;
            if (_totalContentHeight > visibleHeight)
            {
                if (_upArrow.containsPoint(x, y) && _scrollOffset > 0)
                {
                    _scrollOffset = Math.Max(0, _scrollOffset - RowHeight);
                    SetScrollbarPosition();
                    Game1.playSound("shwip");
                    _layoutDirty = true;
                }
                else if (_downArrow.containsPoint(x, y) && _scrollOffset < _totalContentHeight - visibleHeight)
                {
                    _scrollOffset = Math.Min(_totalContentHeight - visibleHeight, _scrollOffset + RowHeight);
                    SetScrollbarPosition();
                    Game1.playSound("shwip");
                    _layoutDirty = true;
                }
                else if (_scrollbarRunner.Contains(x, y) || _scrollbar.containsPoint(x, y))
                {
                    _scrolling = true;
                }
            }
        }

        private bool ProcessDropdownClick(List<Rectangle> items, ref int index, ref bool isOpen, int x, int y)
        {
            for (int i = 0; i < items.Count; i++)
            {
                if (items[i].Contains(x, y))
                {
                    index = i;
                    isOpen = false;
                    Game1.playSound("select");
                    return true;
                }
            }
            return false;
        }

        private void CloseAllDropdowns()
        {
            _orientationDropdownOpen = false;
        }

        public override void releaseLeftClick(int x, int y)
        {
            base.releaseLeftClick(x, y);
            _scrolling = false;
        }

        public override void leftClickHeld(int x, int y)
        {
            base.leftClickHeld(x, y);
            int visibleHeight = _menuHeight - 200;

            if (!_orientationDropdownOpen)
            {
                if (_scrolling && _totalContentHeight > visibleHeight)
                {
                    int yPos = Math.Max(_scrollbarRunner.Y, Math.Min(y, _scrollbarRunner.Bottom - _scrollbar.bounds.Height));
                    float pct = (float)(yPos - _scrollbarRunner.Y) / (_scrollbarRunner.Height - _scrollbar.bounds.Height);
                    _scrollOffset = (int)(pct * (_totalContentHeight - visibleHeight));
                    SetScrollbarPosition();
                    _layoutDirty = true;
                }

                if (_sliderRect.Contains(x, y))
                {
                    int trackWidth = _sliderRect.Width;
                    int relativeX = Math.Max(0, Math.Min(trackWidth, x - _sliderRect.X));
                    int newIndex = Math.Min(3, (int)(((float)relativeX / trackWidth) * 4));

                    if (_safetyModeIndex != newIndex)
                    {
                        _safetyModeIndex = newIndex;
                        _layoutDirty = true;
                    }
                }
            }
        }

        public override void receiveKeyPress(Keys key)
        {
            if (Game1.keyboardDispatcher.Subscriber == _bioTextBox)
            {
                if (key == Keys.Escape || key == Keys.Enter)
                {
                    _bioTextBox.Selected = false;
                    Game1.keyboardDispatcher.Subscriber = null;
                    return;
                }
                if (!DialogueTextInputBox.IsControlKeyDown())
                {
                    _bioTextBox.RecieveSpecialInput(key);
                }
                return;
            }
            base.receiveKeyPress(key);
        }

        private void UpdateLayout()
        {
            if (!_layoutDirty)
                return;

            int contentY = yPositionOnScreen + 70;
            int scrollY = contentY - _scrollOffset;

            // Enable profile checkbox
            _enableProfileCheckboxRect = new Rectangle(_rightColumnX, scrollY, CheckboxSize, CheckboxSize);
            scrollY += RowHeight + 12;

            if (ModEntry.Config.EnablePlayerProfile)
            {
                // --- Sexual Orientation ---
                _orientationDropdownRect = new Rectangle(_rightColumnX, scrollY, _dropdownWidth, DropdownHeight);
                _orientationDropdownBaseY = scrollY;
                scrollY += DropdownHeight + 20;

                // --- Safety Slider ---
                int trackWidth = Math.Min(220, _dropdownWidth);
                _sliderRect = new Rectangle(_rightColumnX, scrollY + 10, trackWidth, SliderTrackHeight);

                int thumbX = _sliderRect.X + (int)(_safetyModeIndex * ((float)trackWidth / 3)) - 12;
                _sliderThumbRect = new Rectangle(thumbX, _sliderRect.Y - 8, 24, 40);

                scrollY += 48;

                string parsedDesc = Game1.parseText(_safetyDesc, Game1.smallFont, _dropdownWidth);
                scrollY += (int)Game1.smallFont.MeasureString(parsedDesc).Y + 20;

                // --- Bio ---
                _bioTextBoxRect = new Rectangle(_rightColumnX, scrollY, _dropdownWidth, BioBoxHeight);

                if (_bioTextBox != null)
                {
                    _bioTextBox.Position = new Vector2(_bioTextBoxRect.X, _bioTextBoxRect.Y);
                    _bioTextBox.Extent = new Vector2(_bioTextBoxRect.Width, BioBoxHeight);
                }

                scrollY += BioBoxHeight + 20;

                // Pre-build dropdown item rectangles
                RebuildDropdownItems(_orientationDropdownItems, _orientationDropdownRect, _orientationDropdownBaseY, _orientationOptions.Count);
            }
            else
            {
                if (_orientationDropdownItems.Count > 0)
                    _orientationDropdownItems.Clear();
            }

            _totalContentHeight = (scrollY + _scrollOffset) - contentY;
            _layoutDirty = false;
        }

        private void RebuildDropdownItems(List<Rectangle> itemRects, Rectangle baseRect, int baseY, int itemCount)
        {
            itemRects.Clear();
            int listWidth = baseRect.Width - 40;
            for (int i = 0; i < itemCount; i++)
            {
                int itemY = baseY + DropdownHeight + (i * DropdownHeight);
                itemRects.Add(new Rectangle(baseRect.X, itemY, listWidth, DropdownHeight));
            }
        }

        public override void draw(SpriteBatch b)
        {
            _bioTextBox?.Update(Game1.currentGameTime);

            UpdateLayout();

            b.Draw(Game1.fadeToBlackRect, Game1.graphics.GraphicsDevice.Viewport.Bounds, Color.Black * 0.5f);
            IClickableMenu.drawTextureBox(b, xPositionOnScreen, yPositionOnScreen, _menuWidth, _menuHeight, Color.White);

            string title = ModEntry.SHelper.Translation.Get("PProfile.UI.Title").Default("Farmer Profile & Persona");
            var titleSize = Game1.dialogueFont.MeasureString(title);
            b.DrawString(Game1.dialogueFont, title,
                new Vector2(xPositionOnScreen + (_menuWidth - titleSize.X) / 2, yPositionOnScreen + 20),
                Game1.textColor);

            DrawSaveButton(b, yPositionOnScreen + _menuHeight - 60);

            int contentY = yPositionOnScreen + 70;
            int scrollY = contentY - _scrollOffset;

            b.End();
            RasterizerState rasterizerState = new RasterizerState { ScissorTestEnable = true };
            b.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp, null, rasterizerState);

            _scissorRect = new Rectangle(xPositionOnScreen, contentY, _menuWidth, _menuHeight - 140);
            b.GraphicsDevice.ScissorRectangle = _scissorRect;

            DrawCheckbox(b, _enableProfileCheckboxRect, ModEntry.Config.EnablePlayerProfile);

            string enableLabel = ModEntry.SHelper.Translation.Get("PProfile.UI.EnableProfile").Default("Enable Player Profile");
            b.DrawString(Game1.smallFont, enableLabel,
                new Vector2(_rightColumnX + CheckboxSize + 12, scrollY + 6), Game1.textColor);

            scrollY += RowHeight + 12;

            if (ModEntry.Config.EnablePlayerProfile)
            {
                string selectText = ModEntry.SHelper.Translation.Get("PProfile.UI.Select").Default("Select...");

                // --- Sexual Orientation ---
                string orientationLabel = ModEntry.SHelper.Translation.Get("PProfile.UI.SexualOrientation").Default("Sexual Orientation:");
                b.DrawString(Game1.smallFont, orientationLabel, new Vector2(_leftMargin, scrollY), Game1.textColor);
                DrawDropdown(b, _orientationDropdownRect, _orientationIndex >= 0 ? _orientationOptions[_orientationIndex] : selectText, _orientationDropdownOpen);
                scrollY += DropdownHeight + 20;

                // --- Safety Slider ---
                string safetyLabel = ModEntry.SHelper.Translation.Get("PProfile.UI.RomanceSafetyMode").Default("Romance Settings:");
                b.DrawString(Game1.smallFont, safetyLabel, new Vector2(_leftMargin, scrollY), Game1.textColor);

                int trackWidth = Math.Min(220, _dropdownWidth);

                IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(403, 383, 6, 6), _sliderRect.X, _sliderRect.Y, _sliderRect.Width, _sliderRect.Height, Color.White, 4f, false);

                for (int t = 0; t < 4; t++)
                {
                    int tickX = _sliderRect.X + (int)(t * ((float)trackWidth / 3));
                    b.Draw(Game1.mouseCursors, new Rectangle(tickX - 2, _sliderRect.Y + 4, 4, SliderTrackHeight - 8), new Rectangle(240, 428, 12, 12), new Color(180, 180, 180));
                }

                b.Draw(Game1.mouseCursors, _sliderThumbRect, new Rectangle(435, 463, 6, 10), Color.White);

                b.DrawString(Game1.smallFont, _sliderLabels[_safetyModeIndex], new Vector2(_rightColumnX + trackWidth + 16, scrollY + 8), Game1.textColor);
                scrollY += 48;

                string parsedDesc = Game1.parseText(_safetyDesc, Game1.smallFont, _dropdownWidth);
                b.DrawString(Game1.smallFont, parsedDesc, new Vector2(_rightColumnX, scrollY), Color.DarkSlateGray);
                scrollY += (int)Game1.smallFont.MeasureString(parsedDesc).Y + 20;

                // --- Bio ---
                string bioLabel = ModEntry.SHelper.Translation.Get("PProfile.UI.CustomBio").Default("Custom Bio:");
                b.DrawString(Game1.smallFont, bioLabel, new Vector2(_leftMargin, scrollY), Game1.textColor);

                IClickableMenu.drawTextureBox(b, _bioTextBoxRect.X - 4, _bioTextBoxRect.Y - 4, _bioTextBoxRect.Width + 8, _bioTextBoxRect.Height + 8, Color.White);

                _bioTextBox.Draw(b);

                if (string.IsNullOrWhiteSpace(_bioTextBox.Text))
                {
                    string wrappedPlaceholder = Game1.parseText(_bioPlaceholder, Game1.smallFont, _bioTextBoxRect.Width - 32);
                    b.DrawString(Game1.smallFont, wrappedPlaceholder, new Vector2(_bioTextBoxRect.X + 12, _bioTextBoxRect.Y + 12), Color.Gray);
                }

                // 🌟 新增：右下角实时字数计数器 (Counter)
                string counterText = $"{_bioTextBox.Text.Length}/{MaxBioLength}";
                Vector2 counterSize = Game1.smallFont.MeasureString(counterText);
                Color counterColor = _bioTextBox.Text.Length >= MaxBioLength ? Color.Red : Color.Gray;
                
                b.DrawString(Game1.smallFont, counterText, 
                    new Vector2(_bioTextBoxRect.Right - counterSize.X - 8, _bioTextBoxRect.Bottom - counterSize.Y - 8), 
                    counterColor);

                scrollY += BioBoxHeight + 20;
            }

            b.End();

            b.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp);

            if (ModEntry.Config.EnablePlayerProfile)
            {
                DrawFloatingDropdowns(b);
            }

            b.End();
            b.Begin(SpriteSortMode.Deferred, BlendState.AlphaBlend, SamplerState.PointClamp);

            int visibleHeight = _menuHeight - 200;
            if (_totalContentHeight > visibleHeight)
            {
                _upArrow.draw(b);
                _downArrow.draw(b);
                IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(403, 383, 6, 6), _scrollbarRunner.X, _scrollbarRunner.Y, _scrollbarRunner.Width, _scrollbarRunner.Height, Color.White, 4f, false);
                _scrollbar.draw(b);
            }

            base.draw(b);
            drawMouse(b);
        }

        private void DrawFloatingDropdowns(SpriteBatch b)
        {
            if (_orientationDropdownOpen) DrawDropdownList(b, _orientationDropdownBaseY, _orientationDropdownRect, _orientationOptions, _orientationIndex, _orientationDropdownItems);
        }

        private void DrawDropdownList(SpriteBatch b, int baseY, Rectangle baseRect, List<string> options, int selectedIdx, List<Rectangle> cachedItemRects)
        {
            int listWidth = baseRect.Width - 40;
            Rectangle containerRect = new Rectangle(baseRect.X, baseY + DropdownHeight, listWidth, options.Count * DropdownHeight);

            IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(433, 451, 3, 3), containerRect.X, containerRect.Y, containerRect.Width, containerRect.Height, Color.White, 4f, false);

            for (int i = 0; i < options.Count; i++)
            {
                int itemY = baseY + DropdownHeight + (i * DropdownHeight);
                var itemRect = new Rectangle(baseRect.X, itemY, listWidth, DropdownHeight);

                bool isHover = i < cachedItemRects.Count && cachedItemRects[i].Contains(Game1.getMouseX(), Game1.getMouseY());

                if (i == selectedIdx || isHover)
                {
                    b.Draw(Game1.staminaRect, new Rectangle(itemRect.X + 4, itemRect.Y, itemRect.Width - 8, itemRect.Height), Color.Wheat);
                }

                if (i < options.Count - 1)
                {
                    b.Draw(Game1.staminaRect, new Rectangle(itemRect.X + 4, itemRect.Bottom - 1, itemRect.Width - 8, 1), Color.Black * 0.2f);
                }

                b.DrawString(Game1.smallFont, options[i], new Vector2(itemRect.X + 16, itemRect.Y + (itemRect.Height - Game1.smallFont.MeasureString(options[i]).Y) / 2), Game1.textColor);
            }
        }

        private void DrawDropdown(SpriteBatch b, Rectangle rect, string displayText, bool isOpen)
        {
            IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(433, 451, 3, 3), rect.X, rect.Y, rect.Width - 40, rect.Height, Color.White, 4f, false);

            string noneText = ModEntry.SHelper.Translation.Get("PProfile.UI.None").Default("(None)");
            string selectText = ModEntry.SHelper.Translation.Get("PProfile.UI.Select").Default("Select...");
            Color textColor = (displayText == selectText || displayText == noneText) ? Color.Gray : Game1.textColor;

            b.DrawString(Game1.smallFont, displayText, new Vector2(rect.X + 16, rect.Y + (rect.Height - Game1.smallFont.MeasureString(displayText).Y) / 2), textColor);

            Rectangle btnRect = new Rectangle(rect.Right - 40, rect.Y, 40, rect.Height);
            IClickableMenu.drawTextureBox(b, Game1.mouseCursors, new Rectangle(433, 451, 3, 3), btnRect.X, btnRect.Y, btnRect.Width, btnRect.Height, Color.White, 4f, false);

            b.Draw(Game1.mouseCursors, new Vector2(btnRect.X + 12, btnRect.Y + 12), new Rectangle(437, 450, 10, 11), Color.White, 0f, Vector2.Zero, 2f, SpriteEffects.None, 1f);
        }

        private void DrawCheckbox(SpriteBatch b, Rectangle rect, bool isChecked)
        {
            Rectangle sourceRect = isChecked ? new Rectangle(236, 425, 9, 9) : new Rectangle(227, 425, 9, 9);
            b.Draw(Game1.mouseCursors, new Vector2(rect.X, rect.Y), sourceRect, Color.White, 0f, Vector2.Zero, 4f, SpriteEffects.None, 1f);
        }

        private void DrawSaveButton(SpriteBatch b, int y)
        {
            IClickableMenu.drawTextureBox(b, xPositionOnScreen + _menuWidth / 2 - 80, y, 160, 44, Color.White);
            string saveText = ModEntry.SHelper.Translation.Get("PProfile.UI.Save").Default("Save");
            var saveTextSize = Game1.dialogueFont.MeasureString(saveText);
            b.DrawString(Game1.dialogueFont, saveText,
                new Vector2(xPositionOnScreen + _menuWidth / 2 - saveTextSize.X / 2, y + 10),
                Color.Black);
        }

        protected override void cleanupBeforeExit()
        {
            base.cleanupBeforeExit();
            if (Game1.keyboardDispatcher.Subscriber == _bioTextBox)
            {
                Game1.keyboardDispatcher.Subscriber = null;
            }
        }
    }
}