using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewValley;
using StardewValley.Menus;
using System;

namespace ValleytalkReborn
{
    /// <summary>
    /// A confirmation dialog with Yes/No buttons that properly blocks input to underlying menus.
    /// </summary>
    public class ConfirmationDialog : IClickableMenu
    {
        private readonly string _message;
        private readonly Action<Farmer> _onConfirm;
        private readonly Action<Farmer> _onCancel;

        private readonly ClickableTextureComponent _yesButton; 
        private readonly ClickableTextureComponent _noButton;

        private const int DialogWidth = 800;
        private const int DialogHeight = 373;
        private const int ButtonSize = 64;

        private float _yesButtonHoverScale = 1f;
        private float _noButtonHoverScale = 1f;

        private readonly float _yesButtonBaseScale = 1f;
        private readonly float _noButtonBaseScale = 1f;

        private bool _hasActed = false;

        public ConfirmationDialog(string message, Action<Farmer> onConfirm, Action<Farmer> onCancel)
            : base(
                  (Game1.uiViewport.Width - DialogWidth) / 2,
                  (Game1.uiViewport.Height - DialogHeight) / 2,
                  DialogWidth,
                  DialogHeight,
                  true
              )
        {
            _message = message ?? "";
            _onConfirm = onConfirm;
            _onCancel = onCancel;

            _yesButton = new ClickableTextureComponent(
                new Rectangle(
                    xPositionOnScreen + DialogWidth / 2 - 80,
                    yPositionOnScreen + DialogHeight - 24 - ButtonSize,
                    ButtonSize,
                    ButtonSize
                ),
                Game1.mouseCursors,
                Game1.getSourceRectForStandardTileSheet(Game1.mouseCursors, 46, -1, -1),
                1f
            );

            _noButton = new ClickableTextureComponent(
                new Rectangle(
                    xPositionOnScreen + DialogWidth / 2 + 16,
                    yPositionOnScreen + DialogHeight - 24 - ButtonSize,
                    ButtonSize,
                    ButtonSize
                ),
                Game1.mouseCursors,
                Game1.getSourceRectForStandardTileSheet(Game1.mouseCursors, 47, -1, -1),
                1f
            );

            if (upperRightCloseButton != null)
            {
                upperRightCloseButton.bounds.X = xPositionOnScreen + width - 36;
                upperRightCloseButton.bounds.Y = yPositionOnScreen + 64;
            }
        }

        private void UpdateButtonScale(ref float currentScale, ClickableTextureComponent button, int mouseX, int mouseY)
        {
            bool hover = button.containsPoint(mouseX, mouseY);
            float target = hover ? 1.15f : 1.0f;
            currentScale += (target - currentScale) * 0.2f;
        }

        public override void receiveLeftClick(int x, int y, bool playSound = true)
        {
            base.receiveLeftClick(x, y, playSound);

            if (_hasActed)
                return;

            if (_yesButton.containsPoint(x, y))
            {
                Game1.playSound("coin");
                _hasActed = true;
                _onConfirm?.Invoke(Game1.player);
                exitThisMenu();
            }
            else if (_noButton.containsPoint(x, y))
            {
                Game1.playSound("cancel");
                _hasActed = true;
                _onCancel?.Invoke(Game1.player);
                exitThisMenu();
            }
        }

        public override void receiveKeyPress(Keys key)
        {
            if (_hasActed)
                return;

            if (key == Keys.Escape)
            {
                Game1.playSound("cancel");
                exitThisMenu();
            }
        }

        protected override void cleanupBeforeExit()
        {
            base.cleanupBeforeExit();

            // If the player closed the dialog without choosing Yes/No, treat it as Cancel.
            if (!_hasActed)
            {
                _onCancel?.Invoke(Game1.player);
            }
        }

        public override void draw(SpriteBatch b)
        {
            b.Draw(
                Game1.fadeToBlackRect,
                Game1.graphics.GraphicsDevice.Viewport.Bounds,
                Color.Black * 0.5f
            );

            Game1.drawDialogueBox(xPositionOnScreen, yPositionOnScreen, width, height, false, true);

            string wrappedMessage = Game1.parseText(_message, Game1.dialogueFont, width - 128);
            var msgSize = Game1.dialogueFont.MeasureString(wrappedMessage);

            float textX = xPositionOnScreen + (width - msgSize.X) / 2;
            float textY = yPositionOnScreen + (height - msgSize.Y) / 2 - 10;

            b.DrawString(
                Game1.dialogueFont,
                wrappedMessage,
                new Vector2(textX, textY),
                Game1.textColor
            );

            int mouseX = Game1.getMouseX();
            int mouseY = Game1.getMouseY();

            UpdateButtonScale(ref _yesButtonHoverScale, _yesButton, mouseX, mouseY);
            _yesButton.scale = _yesButtonBaseScale * _yesButtonHoverScale;
            _yesButton.draw(b);

            UpdateButtonScale(ref _noButtonHoverScale, _noButton, mouseX, mouseY);
            _noButton.scale = _noButtonBaseScale * _noButtonHoverScale;
            _noButton.draw(b);

            base.draw(b);

            if (!Game1.options.hardwareCursor)
            {
                b.Draw(
                    Game1.mouseCursors,
                    new Vector2(Game1.getMouseX(), Game1.getMouseY()),
                    Game1.getSourceRectForStandardTileSheet(Game1.mouseCursors, 0, 16, 16),
                    Color.White,
                    0f,
                    Vector2.Zero,
                    4f,
                    SpriteEffects.None,
                    1f
                );
            }
        }
    }
}