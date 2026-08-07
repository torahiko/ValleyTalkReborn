using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewValley;
using StardewValley.Menus;
using System;

namespace ValleyTalk
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

        public ConfirmationDialog(string message, Action<Farmer> onConfirm, Action<Farmer> onCancel)
            : base(
                  (Game1.uiViewport.Width - DialogWidth) / 2,
                  (Game1.uiViewport.Height - DialogHeight) / 2,
                  DialogWidth,
                  DialogHeight,
                  true)
        {
            _message = message;
            _onConfirm = onConfirm;
            _onCancel = onCancel;

            // Create Yes button (checkmark)
            _yesButton = new ClickableTextureComponent(
                new Rectangle(
                    xPositionOnScreen + DialogWidth / 2 - 80,
                    yPositionOnScreen + DialogHeight - 24 - ButtonSize,
                    ButtonSize, ButtonSize),
                Game1.mouseCursors,
                Game1.getSourceRectForStandardTileSheet(Game1.mouseCursors, 46, -1, -1),
                1f);

            // Create No button (X)
            _noButton = new ClickableTextureComponent(
                new Rectangle(
                    xPositionOnScreen + DialogWidth / 2 + 16,
                    yPositionOnScreen + DialogHeight - 24 - ButtonSize,
                    ButtonSize, ButtonSize),
                Game1.mouseCursors,
                Game1.getSourceRectForStandardTileSheet(Game1.mouseCursors, 47, -1, -1),
                1f);
        }

        public override void receiveLeftClick(int x, int y, bool playSound = true)
        {
            base.receiveLeftClick(x, y, playSound);

            if (_yesButton.containsPoint(x, y))
            {
                Game1.playSound("coin");
                _onConfirm?.Invoke(Game1.player);
            }
            else if (_noButton.containsPoint(x, y))
            {
                Game1.playSound("cancel");
                _onCancel?.Invoke(Game1.player);
            }
        }

        public override void receiveKeyPress(Keys key)
        {
            if (key == Keys.Escape)
            {
                Game1.playSound("cancel");
                _onCancel?.Invoke(Game1.player);
            }
        }

        public override void draw(SpriteBatch b)
        {
            // Draw semi-transparent overlay
            b.Draw(Game1.fadeToBlackRect, Game1.graphics.GraphicsDevice.Viewport.Bounds, Color.Black * 0.5f);

            // Draw dialog box
            Game1.drawDialogueBox(xPositionOnScreen, yPositionOnScreen, width, height, false, true);

            // Draw message
            var msgSize = Game1.dialogueFont.MeasureString(_message);
            var textY = yPositionOnScreen + (height - msgSize.Y) / 2 - 10;
            b.DrawString(Game1.dialogueFont, _message,
                new Vector2(
                    xPositionOnScreen + (width - msgSize.X) / 2,
                    textY),
                Game1.textColor);

            // Draw buttons
            _yesButton.draw(b);
            _noButton.draw(b);

            // Draw mouse cursor
            if (!Game1.options.hardwareCursor)
            {
                b.Draw(Game1.mouseCursors,
                    new Vector2(Game1.getMouseX(), Game1.getMouseY()),
                    Game1.getSourceRectForStandardTileSheet(Game1.mouseCursors, 0, 16, 16),
                    Color.White, 0f, Vector2.Zero, 4f, SpriteEffects.None, 1f);
            }

            base.draw(b);
        }
    }
}