#nullable enable
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewValley;
using StardewValley.Menus;
using System;
using System.Collections.Generic;
using System.Linq;
using ValleytalkReborn.Services;
using ValleytalkReborn.UI;

namespace ValleytalkReborn
{
    internal interface IMemoryRefreshTarget
    {
        void RefreshEntries();
    }

    internal static class UiHelper
    {
        public static void UpdateButtonScale(ref float scale, ClickableTextureComponent btn, int mx, int my)
        {
            float target = btn.containsPoint(mx, my) ? 1.15f : 1.0f; 
            scale += (target - scale) * 0.2f;
        }

        public static string TruncateString(string text, SpriteFont font, float maxWidth, float scale = 1f)
        {
            if (string.IsNullOrEmpty(text) || font.MeasureString(text).X * scale <= maxWidth)
                return text; 

            const string ellipsis = "...";
            float targetWidth = maxWidth - (font.MeasureString(ellipsis).X * scale);
            if (targetWidth <= 0) 
                return ellipsis;

            int low = 0;
            int high = text.Length;
            int best = 0;

            while (low <= high) 
            {
                int mid = (low + high) / 2;
                if (font.MeasureString(text.Substring(0, mid)).X * scale <= targetWidth)
                {
                    best = mid;
                    low = mid + 1;
                }
                else
                {
                    high = mid - 1;
                }
            }

            return text.Substring(0, best) + ellipsis;
        }
    }

    internal class SetCallsignInputMenu : IClickableMenu
    {
        private readonly string _npcName;
        private readonly IClickableMenu _returnMenu;
        private readonly DialogueTextInputBox _inputBox;
        private readonly ClickableTextureComponent _okButton;
        private readonly ClickableTextureComponent _cancelButton;

        private float _okButtonHoverScale = 1f;
        private float _cancelButtonHoverScale = 1f;
        private readonly float _okButtonBaseScale;
        private readonly float _cancelButtonBaseScale;

        // ── 与 AddMemoryInputMenu 保持统一的视觉内衬与尺寸规范 ──
        private const int MenuWidth = 640;
        private const int MenuHeight = 330;
        private const int TopPadding = 125;

        public SetCallsignInputMenu(string npcName, IClickableMenu returnMenu)
        {
            _npcName = npcName;
            _returnMenu = returnMenu;

            xPositionOnScreen = (Game1.uiViewport.Width - MenuWidth) / 2;
            yPositionOnScreen = (Game1.uiViewport.Height - MenuHeight) / 2;
            width = MenuWidth;
            height = MenuHeight;

            const int inputPadX = 56;
            int inputY = yPositionOnScreen + TopPadding + 54;
            const int inputH = 56;

            _inputBox = new DialogueTextInputBox(MemoryManager.MaxCallsignLength, 15)
            {
                Position = new Vector2(xPositionOnScreen + inputPadX, inputY),
                Extent = new Vector2(width - inputPadX * 2, inputH),
                UseCustomFont = true,
                CustomFontSize = CustomFontManager.SizeRegular + 2f,
                CounterFontSize = CustomFontManager.SizeSmall + 2f,
                DrawFrame = true,
                ShowCharacterCount = true,
                AllowNewlines = false,
                TextColor = Game1.textColor,
                Selected = true
            };

            string current = MemoryManager.Instance.GetCustomCallsign(_npcName);
            if (!string.IsNullOrEmpty(current))
                _inputBox.SetText(current);

            _inputBox.OnSubmit += sender => Submit(sender.Text);
            Game1.keyboardDispatcher.Subscriber = _inputBox;

            int btnY = yPositionOnScreen + height - 76;

            _okButton = new ClickableTextureComponent(
                new Rectangle(xPositionOnScreen + width - inputPadX - 52, btnY, 52, 52),
                Game1.mouseCursors,
                Game1.getSourceRectForStandardTileSheet(Game1.mouseCursors, 46, -1, -1), 0.85f);
            _okButtonBaseScale = 0.85f;

            _cancelButton = new ClickableTextureComponent(
                new Rectangle(xPositionOnScreen + width - inputPadX - 52 * 2 - 16, btnY, 52, 52),
                Game1.mouseCursors,
                Game1.getSourceRectForStandardTileSheet(Game1.mouseCursors, 47, -1, -1), 0.85f);
            _cancelButtonBaseScale = 0.85f;

            exitFunction = () =>
            {
                if (Game1.keyboardDispatcher.Subscriber == _inputBox)
                    Game1.keyboardDispatcher.Subscriber = null;
            };
        }

        private void Submit(string text)
        {
            MemoryManager.Instance.SetCustomCallsign(_npcName, text);
            Game1.playSound("coin");
            ReturnToMemoryMenu();
        }

        private void ReturnToMemoryMenu()
        {
            if (Game1.keyboardDispatcher.Subscriber == _inputBox)
                Game1.keyboardDispatcher.Subscriber = null;

            exitThisMenu();
            Game1.activeClickableMenu = _returnMenu;
        }

        public override void receiveLeftClick(int x, int y, bool playSound = true)
        {
            base.receiveLeftClick(x, y, playSound);

            if (_inputBox.ReceiveLeftClick(x, y)) return;

            if (_inputBox.ContainsPoint(x, y))
            {
                Game1.keyboardDispatcher.Subscriber = _inputBox;
                _inputBox.Selected = true;
                return;
            }

            if (_okButton.containsPoint(x, y))
            {
                Submit(_inputBox.Text);
            }
            else if (_cancelButton.containsPoint(x, y))
            {
                Game1.playSound("bigDeSelect");
                ReturnToMemoryMenu();
            }
        }

        public override void receiveKeyPress(Keys key)
        {
            if (Game1.keyboardDispatcher.Subscriber == _inputBox)
            {
                if (key == Keys.Escape)
                {
                    Game1.playSound("bigDeSelect");
                    ReturnToMemoryMenu();
                    return;
                }

                if (key == Keys.Enter)
                {
                    Submit(_inputBox.Text);
                    return;
                }

                if (!DialogueTextInputBox.IsControlKeyDown())
                    _inputBox.RecieveSpecialInput(key);

                return;
            }

            base.receiveKeyPress(key);
        }

        public override void draw(SpriteBatch b)
        {
            _inputBox.Update(Game1.currentGameTime);

            // 1. 全屏黑色遮罩
            b.Draw(Game1.fadeToBlackRect, Game1.graphics.GraphicsDevice.Viewport.Bounds, Color.Black * 0.4f);

            // 2. 原版主菜单对话框背景
            Game1.drawDialogueBox(xPositionOnScreen, yPositionOnScreen, width, height, false, true);

            // 3. 顶部副标题
            string dispName = Game1.getCharacterFromName(_npcName)?.displayName ?? _npcName;
            string subtitle = !string.IsNullOrEmpty(dispName) ? $"CALLSIGN • {dispName.ToUpper()}" : "CALLSIGN";
            var subSize = CustomFontManager.MeasureString(subtitle, 13f);
            Vector2 subPos = new Vector2(
                MathF.Round(xPositionOnScreen + (width - subSize.X) / 2f),
                yPositionOnScreen + TopPadding
            );
            CustomFontManager.DrawString(b, subtitle, subPos, new Color(135, 98, 62), 13f);

            // 4. 双层阴影立体主标题
            string title = I18n.Memory.CallsignTitle(dispName);
            var titleSize = CustomFontManager.MeasureStringBold(title, CustomFontManager.SizeTitle);
            Vector2 titlePos = new Vector2(
                MathF.Round(xPositionOnScreen + (width - titleSize.X) / 2f),
                subPos.Y + subSize.Y + 2f
            );
            CustomFontManager.DrawStringBold(b, title, titlePos + new Vector2(0, 1f), new Color(225, 200, 160) * 0.85f, CustomFontManager.SizeTitle);
            CustomFontManager.DrawStringBold(b, title, titlePos, Game1.textColor, CustomFontManager.SizeTitle);

            // 5. 文本框渲染
            _inputBox.Draw(b);

            int mx = Game1.getMouseX();
            int my = Game1.getMouseY();

            // 6. 确定 / 取消 悬浮缩放与渲染
            UiHelper.UpdateButtonScale(ref _okButtonHoverScale, _okButton, mx, my);
            UiHelper.UpdateButtonScale(ref _cancelButtonHoverScale, _cancelButton, mx, my);

            _okButton.scale = _okButtonBaseScale * _okButtonHoverScale;
            _cancelButton.scale = _cancelButtonBaseScale * _cancelButtonHoverScale;

            _okButton.draw(b);
            _cancelButton.draw(b);

            drawMouse(b);
        }

        protected override void cleanupBeforeExit()
        {
            base.cleanupBeforeExit();

            if (Game1.keyboardDispatcher.Subscriber == _inputBox)
                Game1.keyboardDispatcher.Subscriber = null;
        }
    }

    /// <summary>
    /// 向后兼容入口：供时间线/归档箱等菜单复用文本编辑器（仅内容 + 自定义提交，不暴露 scope/duration UI）。
    /// RULE-MERGE：原 AddMemoryInputMenu 的 _tab==1 世界分支已删除，统一走 RuleManager 或 customSubmit。
    /// </summary>
    internal class AddRuleInputMenu : IClickableMenu
    {
        private readonly IntegratedHubMenu? _hub;
        private readonly IClickableMenu? _returnMenu;
        private DialogueTextInputBox _inputBox = null!;
        private ClickableTextureComponent _okButton = null!;
        private ClickableTextureComponent _cancelButton = null!;
        private readonly MemoryEntry? _existingEntry;
        private readonly bool _isNew;

        // 模式 A：完整规则表单（scope/duration/category）
        private string _scopeNpcName = "";
        private int _durationDays;
        private MemoryCategory _category;
        private Rectangle _factCapsuleRect;
        private Rectangle _behaviorCapsuleRect;
        private Rectangle _scopeDropdownRect;
        private DropdownList _scopeDropdown = null!;
        private int _durationMode;
        private Rectangle _durTodayRect;
        private Rectangle _durCustomRect;
        private Rectangle _durPermRect;
        private NumberStepper? _dayStepper;
        private Rectangle _dayStepperRect;

        // 模式 B：纯文本 + customSubmit（时间线/归档箱复用）
        private readonly Func<string, MemoryOperationResult>? _customSubmit;

        private const int MenuWidth = 720;
        private const int MenuHeight = 500;
        private const int TopPadding = 125;
        private const int CharacterLimit = 120;
        private const int WarningThreshold = 100;

        private float _okButtonHoverScale = 1f;
        private float _cancelButtonHoverScale = 1f;

        private float _okButtonBaseScale;
        private float _cancelButtonBaseScale;

        // ── 规则新增/编辑主构造器（RULE-MERGE）──
        public AddRuleInputMenu(
            string scopeNpcName,
            IntegratedHubMenu hub,
            MemoryEntry? existing,
            bool isNew)
        {
            _hub = hub;
            _returnMenu = null;
            _customSubmit = null;
            _existingEntry = existing;
            _isNew = isNew;

            _scopeNpcName = isNew ? scopeNpcName : (existing?.NpcName ?? scopeNpcName);
            _durationDays = isNew ? 0 : (existing?.ExpireDay < 0 ? -1 : (existing?.ExpireDay ?? 0));
            _category = (existing != null && existing.Category == MemoryCategory.Behavior)
                ? MemoryCategory.Behavior
                : MemoryCategory.Fact;

            if (!isNew)
            {
                if (existing != null)
                {
                    if (existing.ExpireDay < 0) _durationMode = 2;
                    else if (existing.ExpireDay == (int)Game1.Date.TotalDays + 1) _durationMode = 0;
                    else _durationMode = 1;
                }
            }

            InitMenu(scopeNpcName);
            BuildScopeDropdownItems();
        }

        // ── 向后兼容构造器：时间线/归档箱复用（仅内容 + customSubmit）──
        public AddRuleInputMenu(
            string npcName,
            IClickableMenu returnMenu,
            MemoryEntry? existingEntry,
            int tab,
            Func<string, MemoryOperationResult>? customSubmit = null)
        {
            _hub = null;
            _returnMenu = returnMenu;
            _customSubmit = customSubmit;
            _existingEntry = existingEntry;
            _isNew = existingEntry == null;

            _scopeNpcName = existingEntry?.NpcName ?? npcName;
            _durationDays = 0;
            _category = MemoryCategory.Fact;

            InitMenu(npcName);
        }

        private void InitMenu(string npcName)
        {
            // 最小影响隔离：纯文本编辑采用 640x360 紧凑尺寸，常规规则新增保持 720x500
            int menuW = _customSubmit != null ? 640 : MenuWidth;
            int menuH = _customSubmit != null ? 360 : MenuHeight;

            xPositionOnScreen = (Game1.uiViewport.Width - menuW) / 2;
            yPositionOnScreen = (Game1.uiViewport.Height - menuH) / 2;
            width = menuW;
            height = menuH;

            const int inputPadX = 56;
            int inputY = yPositionOnScreen + TopPadding + 54;
            const int inputH = 80;

            _inputBox = new DialogueTextInputBox(CharacterLimit, WarningThreshold)
            {
                Position = new Vector2(xPositionOnScreen + inputPadX, inputY),
                Extent = new Vector2(width - inputPadX * 2, inputH),
                UseCustomFont = true,
                CustomFontSize = CustomFontManager.SizeRegular + 2f,
                CounterFontSize = CustomFontManager.SizeSmall + 2f,
                DrawFrame = true,
                ShowCharacterCount = true,
                AllowNewlines = true,
                TextColor = Game1.textColor,
                Selected = true
            };

            if (_customSubmit == null)
            {
                int capsuleY = inputY + inputH + 16;
                int totalSegW = width - inputPadX * 2;
                int segItemW = (totalSegW - 14) / 2;
                _factCapsuleRect = new Rectangle(xPositionOnScreen + inputPadX, capsuleY, segItemW, 34);
                _behaviorCapsuleRect = new Rectangle(xPositionOnScreen + inputPadX + segItemW + 14, capsuleY, segItemW, 34);

                int scopeY = capsuleY + 34 + 12;
                _scopeDropdownRect = new Rectangle(xPositionOnScreen + inputPadX, scopeY, totalSegW, 34);
                _scopeDropdown = new DropdownList(_scopeDropdownRect)
                {
                    HeaderPrefix = "Scope: ",
                    OnItemSelected = name => _scopeNpcName = name
                };

                int durY = scopeY + 34 + 12;
                int durSegW = (totalSegW - 14) / 3;
                _durTodayRect = new Rectangle(xPositionOnScreen + inputPadX, durY, durSegW, 34);
                _durCustomRect = new Rectangle(xPositionOnScreen + inputPadX + durSegW + 7, durY, durSegW, 34);
                _durPermRect = new Rectangle(xPositionOnScreen + inputPadX + (durSegW + 7) * 2, durY, durSegW, 34);

                _dayStepperRect = new Rectangle(xPositionOnScreen + inputPadX + durSegW + 7, durY + 34 + 6, durSegW, 32);
                _dayStepper = new NumberStepper(_dayStepperRect, Math.Max(1, Math.Min(99, _durationDays)), 1, 99, 1, "d");
            }
            else
            {
                // 纯文本模式初始化占位，防止字段空引用
                _scopeDropdown = new DropdownList(Rectangle.Empty);
            }

            if (_existingEntry != null)
                _inputBox.SetText(_existingEntry.Content);

            _inputBox.OnSubmit += sender => Submit(sender.Text);
            Game1.keyboardDispatcher.Subscriber = _inputBox;

            int btnY = yPositionOnScreen + height - 76;

            _okButton = new ClickableTextureComponent(
                new Rectangle(xPositionOnScreen + width - inputPadX - 52, btnY, 52, 52),
                Game1.mouseCursors,
                Game1.getSourceRectForStandardTileSheet(Game1.mouseCursors, 46, -1, -1), 0.85f);
            _okButtonBaseScale = 0.85f;

            _cancelButton = new ClickableTextureComponent(
                new Rectangle(xPositionOnScreen + width - inputPadX - 52 * 2 - 16, btnY, 52, 52),
                Game1.mouseCursors,
                Game1.getSourceRectForStandardTileSheet(Game1.mouseCursors, 47, -1, -1), 0.85f);
            _cancelButtonBaseScale = 0.85f;
        }

        private void BuildScopeDropdownItems()
        {
            var items = new List<(string Id, string Label)> { ("WORLD", "[Global]") };
            var candidates = NpcCandidateQueryService.GetCleanedCandidates();
            foreach (var c in candidates)
                items.Add((c.Id, c.DisplayName));

            string selectedId = _scopeNpcName;
            if (!items.Any(it => string.Equals(it.Id, selectedId, StringComparison.OrdinalIgnoreCase)))
                selectedId = "WORLD";

            _scopeDropdown.SetItems(items, selectedId);
            _scopeNpcName = selectedId;
        }

        private void ReturnToMenu(bool refresh)
        {
            if (Game1.keyboardDispatcher.Subscriber == _inputBox)
                Game1.keyboardDispatcher.Subscriber = null;

            if (refresh)
            {
                if (_hub != null)
                {
                    _hub.RefreshEntries();
                }
                else if (_returnMenu is IMemoryRefreshTarget target)
                {
                    target.RefreshEntries();
                }
            }

            Game1.activeClickableMenu = _hub ?? _returnMenu;
        }

        private void ShowErrorHud(string message)
        {
            Game1.playSound("cancel");
            Game1.addHUDMessage(new HUDMessage(message, 0));
        }

        private void Submit(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                Game1.playSound("cancel");
                return;
            }

            string trimmed = text.Trim();

            // 模式 B：时间线/归档箱自定义提交
            if (_customSubmit != null)
            {
                var r = _customSubmit(trimmed);
                switch (r)
                {
                    case MemoryOperationResult.Success:
                        Game1.playSound("coin");
                        ReturnToMenu(true);
                        return;
                    case MemoryOperationResult.CapacityFull:
                        ShowErrorHud(I18n.Memory.AddFailedFull(RuleManager.MaxRulesPerScope));
                        return;
                    case MemoryOperationResult.Duplicate:
                        ShowErrorHud(I18n.Memory.AddFailedDuplicate());
                        return;
                    default:
                        ShowErrorHud(I18n.Memory.DistillFailed());
                        return;
                }
            }

            if (_durationMode == 1 && _dayStepper != null)
                _durationDays = _dayStepper.Value;
            else if (_durationMode == 2)
                _durationDays = -1;
            else
                _durationDays = 0;

            MemoryOperationResult result;
            if (_existingEntry != null)
            {
                result = RuleManager.Instance.EditRule(_existingEntry.Id, trimmed);
            }
            else
            {
                result = RuleManager.Instance.AddRule(_scopeNpcName, trimmed, _durationDays, _category);
            }

            switch (result)
            {
                case MemoryOperationResult.Success:
                    Game1.playSound("coin");
                    ReturnToMenu(true);
                    return;

                case MemoryOperationResult.CapacityFull:
                    ShowErrorHud(I18n.Memory.AddFailedFull(RuleManager.MaxRulesPerScope));
                    return;

                case MemoryOperationResult.Duplicate:
                    ShowErrorHud(I18n.Memory.AddFailedDuplicate());
                    return;

                default:
                    ShowErrorHud(I18n.Memory.DistillFailed());
                    return;
            }
        }

        public override void receiveScrollWheelAction(int direction)
        {
            base.receiveScrollWheelAction(direction);
            _inputBox.ReceiveScrollWheel(direction);
        }

        public override void receiveLeftClick(int x, int y, bool playSound = true)
        {
            base.receiveLeftClick(x, y, playSound);

            if (_inputBox.ReceiveLeftClick(x, y)) return;

            if (_inputBox.ContainsPoint(x, y))
            {
                Game1.keyboardDispatcher.Subscriber = _inputBox;
                _inputBox.Selected = true;
                return;
            }

            if (_customSubmit == null)
            {
                if (_scopeDropdown.IsOpen)
                {
                    if (_scopeDropdown.ReceiveLeftClick(x, y))
                        return;
                    _scopeDropdown.Close();
                    Game1.playSound("shwip");
                    return;
                }

                if (_scopeDropdownRect.Contains(x, y))
                {
                    _scopeDropdown.ToggleOpen();
                    Game1.playSound("shwip");
                    return;
                }
            }

            if (_isNew && _customSubmit == null && (_factCapsuleRect.Contains(x, y) || _behaviorCapsuleRect.Contains(x, y)))
            {
                var clicked = _behaviorCapsuleRect.Contains(x, y)
                    ? MemoryCategory.Behavior
                    : MemoryCategory.Fact;
                if (clicked != _category)
                {
                    _category = clicked;
                    Game1.playSound("smallSelect");
                }
                return;
            }

            if (_isNew && _customSubmit == null)
            {
                if (_durTodayRect.Contains(x, y)) { _durationMode = 0; Game1.playSound("smallSelect"); return; }
                if (_durCustomRect.Contains(x, y)) { _durationMode = 1; Game1.playSound("smallSelect"); return; }
                if (_durPermRect.Contains(x, y)) { _durationMode = 2; Game1.playSound("smallSelect"); return; }
                if (_durationMode == 1 && _dayStepper != null && _dayStepper.ReceiveLeftClick(x, y)) return;
            }

            if (_okButton.containsPoint(x, y))
            {
                Submit(_inputBox.Text);
            }
            else if (_cancelButton.containsPoint(x, y))
            {
                Game1.playSound("bigDeSelect");
                ReturnToMenu(false);
            }
        }

        public override void receiveKeyPress(Keys key)
        {
            if (Game1.keyboardDispatcher.Subscriber == _inputBox)
            {
                if (key == Keys.Escape)
                {
                    Game1.playSound("bigDeSelect");
                    ReturnToMenu(false);
                    return;
                }

                if (key == Keys.Enter && !DialogueTextInputBox.IsControlKeyDown())
                {
                    Submit(_inputBox.Text);
                    return;
                }

                if (!DialogueTextInputBox.IsControlKeyDown())
                    _inputBox.RecieveSpecialInput(key);

                return;
            }

            base.receiveKeyPress(key);
        }

        public override void draw(SpriteBatch b)
        {
            _inputBox.Update(Game1.currentGameTime);

            b.Draw(Game1.fadeToBlackRect,
                Game1.graphics.GraphicsDevice.Viewport.Bounds, Color.Black * 0.4f);

            Game1.drawDialogueBox(xPositionOnScreen, yPositionOnScreen, width, height, false, true);

            string dispName = Game1.getCharacterFromName(_scopeNpcName)?.displayName ?? _scopeNpcName;
            string subtitle = _existingEntry != null ? "EDIT RULE" : "ADD RULE";
            var subSize = CustomFontManager.MeasureString(subtitle, 13f);
            Vector2 subPos = new Vector2(
                MathF.Round(xPositionOnScreen + (width - subSize.X) / 2f),
                yPositionOnScreen + TopPadding
            );
            CustomFontManager.DrawString(b, subtitle, subPos, new Color(135, 98, 62), 13f);

            string title = _existingEntry != null
                ? (_isNew ? I18n.Memory.AddTitle(dispName) : I18n.Memory.EditTitle(dispName))
                : I18n.Memory.AddTitle(dispName);
            var titleSize = CustomFontManager.MeasureStringBold(title, CustomFontManager.SizeTitle);
            Vector2 titlePos = new Vector2(
                MathF.Round(xPositionOnScreen + (width - titleSize.X) / 2f),
                subPos.Y + subSize.Y + 2f
            );
            CustomFontManager.DrawStringBold(b, title, titlePos + new Vector2(0, 1f), new Color(225, 200, 160) * 0.85f, CustomFontManager.SizeTitle);
            CustomFontManager.DrawStringBold(b, title, titlePos, Game1.textColor, CustomFontManager.SizeTitle);

            _inputBox.Draw(b);

            int mx = Game1.getMouseX();
            int my = Game1.getMouseY();

            if (_isNew && _customSubmit == null)
            {
                DrawCleanSegment(b, _factCapsuleRect, I18n.Memory.CategoryFactLabel(), _category == MemoryCategory.Fact, mx, my);
                DrawCleanSegment(b, _behaviorCapsuleRect, I18n.Memory.CategoryBehaviorLabel(), _category == MemoryCategory.Behavior, mx, my);
            }
            else if (_customSubmit == null)
            {
                string catLabel = _category == MemoryCategory.Behavior
                    ? I18n.Memory.CategoryBehaviorLabel()
                    : I18n.Memory.CategoryFactLabel();
                var catSize = CustomFontManager.MeasureStringBold(catLabel, CustomFontManager.SizeSmall);
                CustomFontManager.DrawString(b, catLabel,
                    new Vector2(_factCapsuleRect.X, _factCapsuleRect.Y + 8),
                    new Color(135, 110, 85), CustomFontManager.SizeSmall);
            }

            if (_customSubmit == null)
            {
                DrawScopeSelector(b, mx, my);

                if (_isNew)
                    DrawDurationSelector(b, mx, my);
            }

            UiHelper.UpdateButtonScale(ref _okButtonHoverScale, _okButton, mx, my);
            UiHelper.UpdateButtonScale(ref _cancelButtonHoverScale, _cancelButton, mx, my);

            _okButton.scale = _okButtonBaseScale * _okButtonHoverScale;
            _cancelButton.scale = _cancelButtonBaseScale * _cancelButtonHoverScale;

            _okButton.draw(b);
            _cancelButton.draw(b);

            if (_customSubmit == null && _scopeDropdown != null)
            {
                _scopeDropdown.Draw(b);
            }

            drawMouse(b);
        }

        private void DrawScopeSelector(SpriteBatch b, int mx, int my)
        {
            if (_isNew)
            {
                _scopeDropdown.Draw(b);
            }
            else
            {
                string label = $"Scope: {_scopeNpcName}";
                var sz = CustomFontManager.MeasureStringBold(label, CustomFontManager.SizeSmall);
                CustomFontManager.DrawString(b, label,
                    new Vector2(_scopeDropdownRect.X, _scopeDropdownRect.Y + 8),
                    new Color(135, 110, 85), CustomFontManager.SizeSmall);
            }
        }

        private void DrawDurationSelector(SpriteBatch b, int mx, int my)
        {
            DrawCleanSegment(b, _durTodayRect, "Today", _durationMode == 0, mx, my);
            DrawCleanSegment(b, _durCustomRect, "Days", _durationMode == 1, mx, my);
            DrawCleanSegment(b, _durPermRect, "Perm", _durationMode == 2, mx, my);

            if (_durationMode == 1 && _dayStepper != null)
            {
                _dayStepper.Draw(b);
            }
        }

        private static void DrawCleanSegment(SpriteBatch b, Rectangle rect, string label, bool isActive, int mx, int my)
        {
            bool isHover = rect.Contains(mx, my);

            Color innerBg = isActive
                ? new Color(255, 252, 244)
                : (isHover ? new Color(248, 243, 235) : new Color(230, 218, 198) * 0.45f);

            b.Draw(Game1.staminaRect, new Rectangle(rect.X + 2, rect.Y + 2, rect.Width - 4, rect.Height - 4), innerBg);

            IClickableMenu.drawTextureBox(b, Game1.mouseCursors,
                new Rectangle(403, 383, 6, 6),
                rect.X, rect.Y, rect.Width, rect.Height,
                isActive ? Color.White : Color.White * 0.7f, 2f, false);

            if (isActive)
            {
                IClickableMenu.drawTextureBox(b, Game1.mouseCursors,
                    new Rectangle(432, 439, 9, 9),
                    rect.X - 1, rect.Y - 1, rect.Width + 2, rect.Height + 2,
                    Color.Gold * 0.45f, 2f, false);
            }

            var labelSize = CustomFontManager.MeasureStringBold(label, CustomFontManager.SizeRegular);
            Vector2 textPos = new Vector2(
                rect.X + (rect.Width - labelSize.X) / 2f, 
                rect.Y + (rect.Height - labelSize.Y) / 2f
            );

            Color textColor = isActive
                ? Game1.textColor
                : (isHover ? Game1.textColor * 0.9f : new Color(135, 110, 85));

            if (isActive)
                CustomFontManager.DrawStringBold(b, label, textPos, textColor, CustomFontManager.SizeRegular);
            else
                CustomFontManager.DrawString(b, label, textPos, textColor, CustomFontManager.SizeRegular);
        }

        protected override void cleanupBeforeExit() 
        {
            base.cleanupBeforeExit();

            if (Game1.keyboardDispatcher.Subscriber == _inputBox)
                Game1.keyboardDispatcher.Subscriber = null;
        }
    }
}