using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewValley;
using StardewValley.Menus;
using System;

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
        private const int MenuWidth = 560;
        private const int MenuHeight = 240;

        public SetCallsignInputMenu(string npcName, IClickableMenu returnMenu)
        {
            _npcName = npcName;
            _returnMenu = returnMenu;

            xPositionOnScreen = (Game1.uiViewport.Width - MenuWidth) / 2;
            yPositionOnScreen = (Game1.uiViewport.Height - MenuHeight) / 2;
            width = MenuWidth;
            height = MenuHeight;

            _inputBox = new DialogueTextInputBox(MemoryManager.MaxCallsignLength, 15)
            {
                Position = new Vector2(xPositionOnScreen + 40, yPositionOnScreen + 100),
                Extent = new Vector2(width - 80, 50),
                Font = Game1.dialogueFont,
                TextColor = Game1.textColor,
                Selected = true
            };

            string current = MemoryManager.Instance.GetCustomCallsign(_npcName);
            if (!string.IsNullOrEmpty(current))
                _inputBox.SetText(current);

            _inputBox.OnSubmit += sender => Submit(sender.Text);
            Game1.keyboardDispatcher.Subscriber = _inputBox;

            int btnY = yPositionOnScreen + height - 70;
            _okButton = new ClickableTextureComponent(
                new Rectangle(xPositionOnScreen + width - 2 * 24 - 54, btnY, 54, 54),
                Game1.mouseCursors,
                new Rectangle(128, 256, 64, 64), 0.85f);

            _cancelButton = new ClickableTextureComponent(
                new Rectangle(xPositionOnScreen + width - 3 * 24 - 2 * 54, btnY, 54, 54),
                Game1.mouseCursors,
                new Rectangle(192, 256, 64, 64), 0.85f);

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
            exitThisMenu();
            Game1.activeClickableMenu = _returnMenu;
        }

        public override void receiveLeftClick(int x, int y, bool playSound = true)
        {
            base.receiveLeftClick(x, y, playSound);

            if (_inputBox.ReceiveLeftClick(x, y)) return;

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

            b.Draw(Game1.fadeToBlackRect, Game1.graphics.GraphicsDevice.Viewport.Bounds, Color.Black * 0.4f);
            IClickableMenu.drawTextureBox(b, xPositionOnScreen, yPositionOnScreen, width, height, Color.White);

            string title = I18n.Memory.CallsignTitle(_npcName);
            string hint = I18n.Memory.CallsignHint();

            var titleSize = Game1.dialogueFont.MeasureString(title);
            b.DrawString(Game1.dialogueFont, title,
                new Vector2(xPositionOnScreen + (width - titleSize.X) / 2f, yPositionOnScreen + 18),
                Game1.textColor);

            var hintSize = Game1.smallFont.MeasureString(hint);
            b.DrawString(Game1.smallFont, hint,
                new Vector2(xPositionOnScreen + (width - hintSize.X) / 2f, yPositionOnScreen + 58),
                Color.Gray);

            _inputBox.Draw(b);

            int mx = Game1.getMouseX();
            int my = Game1.getMouseY();

            UiHelper.UpdateButtonScale(ref _okButtonHoverScale, _okButton, mx, my);
            UiHelper.UpdateButtonScale(ref _cancelButtonHoverScale, _cancelButton, mx, my);

            _okButton.scale = 0.85f * _okButtonHoverScale;
            _cancelButton.scale = 0.85f * _cancelButtonHoverScale;

            _okButton.draw(b);
            _cancelButton.draw(b);
            drawMouse(b);
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

        private const int MenuWidth = 600;
        private const int MenuHeight = 344;
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

            int lineHeight = Game1.dialogueFont.LineSpacing;

            _inputBox = new DialogueTextInputBox(CharacterLimit, WarningThreshold)
            {
                Position = new Vector2(xPositionOnScreen + 40,
                                       yPositionOnScreen + 100 + lineHeight),
                Extent = new Vector2(width - 80, 60),
                Font = Game1.dialogueFont,
                TextColor = Game1.textColor,
                Selected = true
            };

            int capsuleY = yPositionOnScreen + 100 + lineHeight + 76;
            _factCapsuleRect = new Rectangle(xPositionOnScreen + 40, capsuleY, 250, 36);
            _behaviorCapsuleRect = new Rectangle(xPositionOnScreen + 40 + 250 + 12, capsuleY, 250, 36);

            if (_existingEntry != null)
                _inputBox.SetText(_existingEntry.Content);

            _inputBox.OnSubmit += sender => Submit(sender.Text);
            Game1.keyboardDispatcher.Subscriber = _inputBox;

            int btnY = yPositionOnScreen + height - 80 + lineHeight;

            _okButton = new ClickableTextureComponent(
                new Rectangle(xPositionOnScreen + width - 3 * 24 - 2 * 64, btnY, 64, 64),
                Game1.mouseCursors,
                new Rectangle(128, 256, 64, 64), 1f);
            _okButtonBaseScale = 1f;

            _cancelButton = new ClickableTextureComponent(
                new Rectangle(xPositionOnScreen + width - 2 * 24 - 64, btnY, 64, 64),
                Game1.mouseCursors,
                new Rectangle(192, 256, 64, 64), 1f);
            _cancelButtonBaseScale = 1f;
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

            // 自定义提交（如 Timeline 浓缩确认）：复用既有结果 HUD，不触碰 _tab 分支
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

            b.Draw(Game1.fadeToBlackRect,
                Game1.graphics.GraphicsDevice.Viewport.Bounds, Color.Black * 0.4f);

            IClickableMenu.drawTextureBox(b,
                xPositionOnScreen, yPositionOnScreen, width, height, Color.White);

            string title = _tab == 1
                ? (_existingEntry != null ? I18n.Memory.WorldEditTitle() : I18n.Memory.WorldAddTitle())
                : (_existingEntry != null
                    ? I18n.Memory.EditTitle(_npcName)
                    : I18n.Memory.AddTitle(_npcName));

            var titleSize = Game1.dialogueFont.MeasureString(title);

            b.DrawString(Game1.dialogueFont, title,
                new Vector2(xPositionOnScreen + (width - titleSize.X) / 2f,
                            yPositionOnScreen + 20),
                Game1.textColor);

            string hint = _tab == 1
                ? I18n.Memory.WorldAddHint(WorldMemoryManager.MaxEntryLength)
                : I18n.Memory.AddHint(_npcName);

            var hintSize = Game1.smallFont.MeasureString(hint);

            b.DrawString(Game1.smallFont, hint,
                new Vector2(xPositionOnScreen + (width - hintSize.X) / 2f,
                            yPositionOnScreen + 20 + Game1.dialogueFont.LineSpacing),
                Color.Gray);

            _inputBox.Draw(b);

            int mx = Game1.getMouseX();
            int my = Game1.getMouseY();

            if (_tab == 0 && _customSubmit == null)
            {
                bool factSelected = _category == MemoryCategory.Fact;
                bool behaviorSelected = _category == MemoryCategory.Behavior;

                bool factHover = _factCapsuleRect.Contains(mx, my);
                bool behaviorHover = _behaviorCapsuleRect.Contains(mx, my);

                Color GetCapsuleBg(bool isSelected, bool isHover) =>
                    isSelected
                        ? (isHover ? new Color(255, 225, 120) : new Color(245, 205, 90))
                        : (isHover ? new Color(175, 125, 75) : new Color(139, 90, 43));

                Color GetCapsuleTextColor(bool isSelected, bool isHover) =>
                    isSelected
                        ? Game1.textColor
                        : (isHover ? Color.White : Color.White * 0.85f);

                Color factBg = GetCapsuleBg(factSelected, factHover);
                Color behaviorBg = GetCapsuleBg(behaviorSelected, behaviorHover);

                IClickableMenu.drawTextureBox(b,
                    _factCapsuleRect.X, _factCapsuleRect.Y, _factCapsuleRect.Width, _factCapsuleRect.Height,
                    factBg);
                IClickableMenu.drawTextureBox(b,
                    _behaviorCapsuleRect.X, _behaviorCapsuleRect.Y, _behaviorCapsuleRect.Width, _behaviorCapsuleRect.Height,
                    behaviorBg);

                string factLabel = I18n.Memory.CategoryFactLabel();
                string behaviorLabel = I18n.Memory.CategoryBehaviorLabel();

                var factSize = Game1.smallFont.MeasureString(factLabel);
                var behaviorSize = Game1.smallFont.MeasureString(behaviorLabel);

                Color factTextColor = GetCapsuleTextColor(factSelected, factHover);
                Color behaviorTextColor = GetCapsuleTextColor(behaviorSelected, behaviorHover);

                b.DrawString(Game1.smallFont, factLabel,
                    new Vector2(_factCapsuleRect.X + (_factCapsuleRect.Width - factSize.X) / 2f,
                                _factCapsuleRect.Y + (_factCapsuleRect.Height - factSize.Y) / 2f),
                    factTextColor);
                b.DrawString(Game1.smallFont, behaviorLabel,
                    new Vector2(_behaviorCapsuleRect.X + (_behaviorCapsuleRect.Width - behaviorSize.X) / 2f,
                                _behaviorCapsuleRect.Y + (_behaviorCapsuleRect.Height - behaviorSize.Y) / 2f),
                    behaviorTextColor);

                string hint2 = factSelected ? I18n.Memory.CategoryFactHint() : I18n.Memory.CategoryBehaviorHint();
                var hintSize2 = Game1.smallFont.MeasureString(hint2);
                int capsuleBottom = Math.Max(_factCapsuleRect.Bottom, _behaviorCapsuleRect.Bottom);
                b.DrawString(Game1.smallFont, hint2,
                    new Vector2(xPositionOnScreen + (width - hintSize2.X) / 2f,
                                capsuleBottom + 8),
                    Color.Gray);
            }

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
}
