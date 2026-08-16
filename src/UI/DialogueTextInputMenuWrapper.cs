using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewValley.Menus;
using ValleytalkReborn.Platform;

namespace ValleytalkReborn
{
    /// <summary>
    /// Wrapper to integrate DialogueTextInputMenu with Stardew Valley's menu system.
    /// </summary>
    internal class DialogueTextInputMenuWrapper : IClickableMenu
    {
        private readonly DialogueTextInputMenu _innerMenu;

        public DialogueTextInputMenuWrapper(DialogueTextInputMenu innerMenu) : base()
        {
            _innerMenu = innerMenu;

            // If this menu is wrapped, submenus should restore the wrapper, not the inner menu.
            _innerMenu.SetMenuToRestore(this);

            // Sync base menu bounds so the game's menu system has correct size information.
            xPositionOnScreen = _innerMenu.MenuBounds.X;
            yPositionOnScreen = _innerMenu.MenuBounds.Y;
            width = _innerMenu.MenuBounds.Width;
            height = _innerMenu.MenuBounds.Height;

            // Adjust position for Android virtual keyboard.
            if (AndroidHelper.IsAndroid)
            {
                var adjustedPosition = AndroidHelper.AdjustPositionForKeyboard(
                    new Vector2(xPositionOnScreen, yPositionOnScreen)
                );

                xPositionOnScreen = (int)adjustedPosition.X;
                yPositionOnScreen = (int)adjustedPosition.Y;
            }
        }

        public override void draw(SpriteBatch b)
        {
            _innerMenu.draw(b);
        }

        public override void receiveLeftClick(int x, int y, bool playSound = true)
        {
            _innerMenu.ReceiveLeftClick(x, y);
        }

        public override void receiveKeyPress(Keys key)
        {
            _innerMenu.ReceiveKeyPress(key);
        }

        public override void receiveScrollWheelAction(int direction)
        {
            base.receiveScrollWheelAction(direction);
            _innerMenu.ReceiveScrollWheel(direction);
        }

        public override bool overrideSnappyMenuCursorMovementBan()
        {
            return true;
        }

        protected override void cleanupBeforeExit()
        {
            _innerMenu.Close();
            base.cleanupBeforeExit();
        }

        /// <summary>
        /// Passes window size changes down to the inner menu so it can recalculate responsive layout.
        /// </summary>
        public override void gameWindowSizeChanged(Rectangle oldBounds, Rectangle newBounds)
        {
            base.gameWindowSizeChanged(oldBounds, newBounds);
            _innerMenu.gameWindowSizeChanged(oldBounds, newBounds);
        }
    }
}