using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewValley;
using StardewValley.Menus;
using System;
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

    internal class AddMemoryInputMenu : IClickableMenu
    {
        private readonly string _npcName;
        private readonly IClickableMenu _returnMenu;
        private readonly DialogueTextInputBox _inputBox;
        private readonly ClickableTextureComponent _okButton;
        private readonly ClickableTextureComponent _cancelButton;
        private readonly MemoryEntry _existingEntry;
        private readonly int _tab;
        private readonly Func<string, MemoryOperationResult> _customSubmit;

        private MemoryCategory _category;
        private Rectangle _factCapsuleRect;
        private Rectangle _behaviorCapsuleRect;

        // ── 调宽放高，给红木内衬留出充足安全空间 ──
        private const int MenuWidth = 720;
        private const int MenuHeight = 460;
        private const int TopPadding = 125; 
        private const int CharacterLimit = 60;
        private const int WarningThreshold = 50;

        private float _okButtonHoverScale = 1f;
        private float _cancelButtonHoverScale = 1f;

        private readonly float _okButtonBaseScale;
        private readonly float _cancelButtonBaseScale;

        public AddMemoryInputMenu(
            string npcName,
            IClickableMenu returnMenu,
            MemoryEntry existingEntry = null,
            int tab = 0,
            Func<string, MemoryOperationResult> customSubmit = null)
        {
            _npcName = npcName;
            _returnMenu = returnMenu;
            _existingEntry = existingEntry;
            _tab = tab;
            _customSubmit = customSubmit;

            _category = (existingEntry != null && existingEntry.Category == MemoryCategory.Behavior)
                ? MemoryCategory.Behavior
                : MemoryCategory.Fact;

            xPositionOnScreen = (Game1.uiViewport.Width - MenuWidth) / 2;
            yPositionOnScreen = (Game1.uiViewport.Height - MenuHeight) / 2;
            width = MenuWidth;
            height = MenuHeight;

            // 内部可用区域的水平 Padding
            const int inputPadX = 56;
            
            // 文本框位置：随 TopPadding 同步整体下移 20px
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

            // 分类胶囊按钮排版：跟随 inputY 下移 20px
            int capsuleY = inputY + inputH + 16;
            int totalSegW = width - inputPadX * 2;
            int segItemW = (totalSegW - 14) / 2;
            _factCapsuleRect = new Rectangle(xPositionOnScreen + inputPadX, capsuleY, segItemW, 38);
            _behaviorCapsuleRect = new Rectangle(xPositionOnScreen + inputPadX + segItemW + 14, capsuleY, segItemW, 38);

            if (_existingEntry != null)
                _inputBox.SetText(_existingEntry.Content);

            _inputBox.OnSubmit += sender => Submit(sender.Text);
            Game1.keyboardDispatcher.Subscriber = _inputBox;

            // 底部按钮坐标保持原位不变
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

        private void ReturnToMemoryMenu(bool refresh)
        {
            if (Game1.keyboardDispatcher.Subscriber == _inputBox)
                Game1.keyboardDispatcher.Subscriber = null;

            if (refresh && _returnMenu is IMemoryRefreshTarget refreshable)
                refreshable.RefreshEntries();

            Game1.activeClickableMenu = _returnMenu;
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
            MemoryOperationResult result;

            if (_tab == 1)
            {
                result = _existingEntry != null
                    ? WorldMemoryManager.Instance.EditEntry(_existingEntry.Id, trimmed)
                    : WorldMemoryManager.Instance.AddEntry(trimmed);
            }
            else
            {
                result = _existingEntry != null
                    ? MemoryManager.Instance.EditMemory(_npcName, _existingEntry.Id, trimmed, _category)
                    : MemoryManager.Instance.AddMemory(_npcName, trimmed, _category);
            }

            int maxLen = _tab == 1
                ? WorldMemoryManager.MaxEntryLength
                : MemoryManager.Instance.GetMaxMemoryLength();

            int maxCount = _tab == 1
                ? WorldMemoryManager.MaxEntries
                : MemoryManager.MaxMemoriesPerNpc;

            if (_customSubmit != null)
            {
                var r = _customSubmit(trimmed);
                switch (r)
                {
                    case MemoryOperationResult.Success:
                        Game1.playSound("coin");
                        ReturnToMemoryMenu(true);
                        return;

                    case MemoryOperationResult.CapacityFull:
                        ShowErrorHud(I18n.Memory.AddFailedFull(maxCount));
                        return;

                    case MemoryOperationResult.TooLong:
                        ShowErrorHud(I18n.Memory.AddFailedTooLong(maxLen));
                        return;

                    case MemoryOperationResult.Duplicate:
                        ShowErrorHud(I18n.Memory.AddFailedDuplicate());
                        return;

                    default:
                        ShowErrorHud(I18n.Memory.DistillFailed());
                        return;
                }
            }

            switch (result)
            {
                case MemoryOperationResult.Success:
                    Game1.playSound("coin");
                    break;

                case MemoryOperationResult.CapacityFull:
                    ShowErrorHud(I18n.Memory.AddFailedFull(maxCount));
                    return;

                case MemoryOperationResult.TooLong:
                    ShowErrorHud(I18n.Memory.AddFailedTooLong(maxLen));
                    return;

                case MemoryOperationResult.Duplicate:
                    ShowErrorHud(I18n.Memory.AddFailedDuplicate());
                    return;

                default:
                    ShowErrorHud(I18n.Memory.AddFailedDuplicate());
                    return;
            }

            ReturnToMemoryMenu(true);
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

            if (_tab == 0 && _customSubmit == null && (_factCapsuleRect.Contains(x, y) || _behaviorCapsuleRect.Contains(x, y)))
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

            if (_okButton.containsPoint(x, y))
            {
                Submit(_inputBox.Text);
            }
            else if (_cancelButton.containsPoint(x, y))
            {
                Game1.playSound("bigDeSelect");
                ReturnToMemoryMenu(false);
            }
        }

        public override void receiveKeyPress(Keys key)
        {
            if (Game1.keyboardDispatcher.Subscriber == _inputBox)
            {
                if (key == Keys.Escape)
                {
                    Game1.playSound("bigDeSelect");
                    ReturnToMemoryMenu(false);
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

            // 1. 原版主菜单底框
            Game1.drawDialogueBox(xPositionOnScreen, yPositionOnScreen, width, height, false, true);

            // 2. 本地化名字
            string dispName = Game1.getCharacterFromName(_npcName)?.displayName ?? _npcName;

            // 3. 顶部副标题（从 84 移到 104，整体下移 20px）
            string subtitle = _tab == 1 
                ? "WORLD MEMORY" 
                : (!string.IsNullOrEmpty(dispName) ? $"MEMORY • {dispName.ToUpper()}" : "NPC MEMORY");
            var subSize = CustomFontManager.MeasureString(subtitle, 13f);
            Vector2 subPos = new Vector2(
                MathF.Round(xPositionOnScreen + (width - subSize.X) / 2f),
                yPositionOnScreen + TopPadding
            );
            CustomFontManager.DrawString(b, subtitle, subPos, new Color(135, 98, 62), 13f);

            // 4. 主标题（跟随副标题同步下移）
            string title = _tab == 1
                ? (_existingEntry != null ? I18n.Memory.WorldEditTitle() : I18n.Memory.WorldAddTitle())
                : (_existingEntry != null
                    ? I18n.Memory.EditTitle(dispName)
                    : I18n.Memory.AddTitle(dispName));

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

            // 6. 类别分段选择器
            if (_tab == 0 && _customSubmit == null)
            {
                DrawCleanSegment(b, _factCapsuleRect, I18n.Memory.CategoryFactLabel(), _category == MemoryCategory.Fact, mx, my);
                DrawCleanSegment(b, _behaviorCapsuleRect, I18n.Memory.CategoryBehaviorLabel(), _category == MemoryCategory.Behavior, mx, my);

                string hint2 = _category == MemoryCategory.Fact ? I18n.Memory.CategoryFactHint() : I18n.Memory.CategoryBehaviorHint();
                int capsuleBottom = Math.Max(_factCapsuleRect.Bottom, _behaviorCapsuleRect.Bottom);

                CustomFontManager.DrawString(b, hint2,
                    new Vector2(xPositionOnScreen + 56, capsuleBottom + 10),
                    new Color(135, 110, 85), CustomFontManager.SizeSmall);
            }

            UiHelper.UpdateButtonScale(ref _okButtonHoverScale, _okButton, mx, my);
            UiHelper.UpdateButtonScale(ref _cancelButtonHoverScale, _cancelButton, mx, my);

            _okButton.scale = _okButtonBaseScale * _okButtonHoverScale;
            _cancelButton.scale = _cancelButtonBaseScale * _cancelButtonHoverScale;

            _okButton.draw(b);
            _cancelButton.draw(b);

            drawMouse(b);
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
            {
                CustomFontManager.DrawStringBold(b, label, textPos, textColor, CustomFontManager.SizeRegular);
            }
            else
            {
                CustomFontManager.DrawString(b, label, textPos, textColor, CustomFontManager.SizeRegular);
            }
        }

        protected override void cleanupBeforeExit()
        {
            base.cleanupBeforeExit();

            if (Game1.keyboardDispatcher.Subscriber == _inputBox)
                Game1.keyboardDispatcher.Subscriber = null;
        }
    }
}