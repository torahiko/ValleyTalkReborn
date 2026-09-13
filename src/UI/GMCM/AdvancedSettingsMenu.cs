using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewValley;
using StardewValley.Menus;
using System;

namespace ValleytalkReborn
{
    /// <summary>
    /// Advanced Settings popup menu - provides toggle for Infinite Chat and other advanced options.
    /// </summary>
    public class AdvancedSettingsMenu : IClickableMenu
    {
        private const int MenuWidth = 800;
        private const int MenuHeight = 500;

        private readonly DialogueTextInputMenu _ownerMenu;

        // Close button
        private readonly ClickableTextureComponent _closeButton;
        private float _closeButtonHoverScale = 1f;
        private const float CloseButtonBaseScale = 4f;

        // Checkbox rectangles
        private readonly Rectangle _checkboxRect;
        private readonly Rectangle _vanillaFirstCheckboxRect;
        private readonly Rectangle _recordVanillaCheckboxRect;

        public AdvancedSettingsMenu(DialogueTextInputMenu ownerMenu)
            : base(
                  (Game1.uiViewport.Width - MenuWidth) / 2,
                  (Game1.uiViewport.Height - MenuHeight) / 2,
                  MenuWidth,
                  MenuHeight,
                  showUpperRightCloseButton: false
              )
        {
            _ownerMenu = ownerMenu;

            // Custom close button at top-right.
            _closeButton = new ClickableTextureComponent(
                new Rectangle(xPositionOnScreen + MenuWidth - 48, yPositionOnScreen + 16, 32, 32),
                Game1.mouseCursors,
                new Rectangle(337, 494, 12, 12),
                CloseButtonBaseScale
            );

            // Checkbox positions.
            _checkboxRect = new Rectangle(xPositionOnScreen + 64, yPositionOnScreen + 120, 36, 36);
            _vanillaFirstCheckboxRect = new Rectangle(xPositionOnScreen + 64, yPositionOnScreen + 180, 36, 36);
            _recordVanillaCheckboxRect = new Rectangle(xPositionOnScreen + 64, yPositionOnScreen + 240, 36, 36);
        }

        /// <summary>
        /// Gets the scaled bounds of a ClickableTextureComponent based on its current scale.
        /// </summary>
        private Rectangle GetScaledBounds(ClickableTextureComponent component, float baseScale, float hoverScale)
        {
            var bounds = component.bounds;
            float currentScale = baseScale * hoverScale;

            int offsetX = (int)((bounds.Width * (currentScale - 1)) / 2);
            int offsetY = (int)((bounds.Height * (currentScale - 1)) / 2);

            return new Rectangle(
                bounds.X - offsetX,
                bounds.Y - offsetY,
                (int)(bounds.Width * currentScale),
                (int)(bounds.Height * currentScale)
            );
        }

        public override void receiveLeftClick(int x, int y, bool playSound = true)
        {
            base.receiveLeftClick(x, y, playSound);

            Rectangle scaledCloseRect = GetScaledBounds(_closeButton, CloseButtonBaseScale, _closeButtonHoverScale);

            if (scaledCloseRect.Contains(x, y))
            {
                Game1.playSound("bigDeSelect");
                exitThisMenu();
                return;
            }

            if (_checkboxRect.Contains(x, y))
            {
                ModEntry.Config.EnableInfiniteChat = !ModEntry.Config.EnableInfiniteChat;
                Game1.playSound(ModEntry.Config.EnableInfiniteChat ? "coin" : "drumkit6");
                ModEntry.SHelper.WriteConfig(ModEntry.Config);
            }

            if (_vanillaFirstCheckboxRect.Contains(x, y))
            {
                ModEntry.Config.EnableVanillaFirst = !ModEntry.Config.EnableVanillaFirst;
                Game1.playSound(ModEntry.Config.EnableVanillaFirst ? "coin" : "drumkit6");
                ModEntry.SHelper.WriteConfig(ModEntry.Config);
            }

            if (_recordVanillaCheckboxRect.Contains(x, y))
            {
                ModEntry.Config.RecordVanillaDialogue = !ModEntry.Config.RecordVanillaDialogue;
                Game1.playSound(ModEntry.Config.RecordVanillaDialogue ? "coin" : "drumkit6");
                ModEntry.SHelper.WriteConfig(ModEntry.Config);
            }
        }

        public override void receiveKeyPress(Keys key)
        {
            if (key == Keys.Escape)
            {
                Game1.playSound("bigDeSelect");
                exitThisMenu();
            }
        }

        protected override void cleanupBeforeExit()
        {
            base.cleanupBeforeExit();
            _ownerMenu?.RestorePreviousMenu();
        }

        private void UpdateButtonScale(ref float hoverScale, ClickableTextureComponent button, int mouseX, int mouseY)
        {
            Rectangle scaledBounds = GetScaledBounds(button, CloseButtonBaseScale, hoverScale);

            if (scaledBounds.Contains(mouseX, mouseY))
                hoverScale = Math.Min(hoverScale + 0.05f, 1.15f);
            else
                hoverScale = Math.Max(hoverScale - 0.05f, 1f);
        }

        public override void draw(SpriteBatch b)
        {
            // Background overlay.
            b.Draw(
                Game1.fadeToBlackRect,
                Game1.graphics.GraphicsDevice.Viewport.Bounds,
                Color.Black * 0.4f
            );

            // Menu background.
            IClickableMenu.drawTextureBox(
                b,
                xPositionOnScreen,
                yPositionOnScreen,
                MenuWidth,
                MenuHeight,
                Color.White
            );

            // Title.
            string title = ModEntry.SHelper.Translation
                .Get("AdvancedSettings.Title")
                .Default("Advanced Settings");

            var titleSize = Game1.dialogueFont.MeasureString(title);

            b.DrawString(
                Game1.dialogueFont,
                title,
                new Vector2(xPositionOnScreen + (MenuWidth - titleSize.X) / 2, yPositionOnScreen + 32),
                Game1.textColor
            );

            int mouseX = Game1.getMouseX();
            int mouseY = Game1.getMouseY();

            // Close button.
            UpdateButtonScale(ref _closeButtonHoverScale, _closeButton, mouseX, mouseY);
            _closeButton.scale = CloseButtonBaseScale * _closeButtonHoverScale;
            _closeButton.draw(b);

            // Infinite Chat checkbox.
            Rectangle checkboxSource = ModEntry.Config.EnableInfiniteChat
                ? new Rectangle(236, 425, 9, 9)
                : new Rectangle(227, 425, 9, 9);

            b.Draw(
                Game1.mouseCursors,
                new Vector2(_checkboxRect.X, _checkboxRect.Y),
                checkboxSource,
                Color.White,
                0f,
                Vector2.Zero,
                4f,
                SpriteEffects.None,
                1f
            );

            string label = ModEntry.SHelper.Translation
                .Get("AdvancedSettings.InfiniteChat")
                .Default("Enable Infinite Chat");

            b.DrawString(
                Game1.smallFont,
                label,
                new Vector2(_checkboxRect.X + 44, _checkboxRect.Y + 4),
                Game1.textColor
            );

            // Vanilla-First checkbox.
            Rectangle vanillaFirstSource = ModEntry.Config.EnableVanillaFirst
                ? new Rectangle(236, 425, 9, 9)
                : new Rectangle(227, 425, 9, 9);

            b.Draw(
                Game1.mouseCursors,
                new Vector2(_vanillaFirstCheckboxRect.X, _vanillaFirstCheckboxRect.Y),
                vanillaFirstSource,
                Color.White,
                0f,
                Vector2.Zero,
                4f,
                SpriteEffects.None,
                1f
            );

            string vanillaFirstLabel = ModEntry.SHelper.Translation
                .Get("AdvancedSettings.VanillaFirst")
                .Default("Prioritize Vanilla Dialogue");

            b.DrawString(
                Game1.smallFont,
                vanillaFirstLabel,
                new Vector2(_vanillaFirstCheckboxRect.X + 44, _vanillaFirstCheckboxRect.Y + 4),
                Game1.textColor
            );

            // Record-Vanilla-Dialogue checkbox.
            Rectangle recordVanillaSource = ModEntry.Config.RecordVanillaDialogue
                ? new Rectangle(236, 425, 9, 9)
                : new Rectangle(227, 425, 9, 9);

            b.Draw(
                Game1.mouseCursors,
                new Vector2(_recordVanillaCheckboxRect.X, _recordVanillaCheckboxRect.Y),
                recordVanillaSource,
                Color.White,
                0f,
                Vector2.Zero,
                4f,
                SpriteEffects.None,
                1f
            );

            string recordVanillaLabel = ModEntry.SHelper.Translation
                .Get("AdvancedSettings.RecordVanillaDialogue")
                .Default("Record Vanilla Dialogue to Memory");

            b.DrawString(
                Game1.smallFont,
                recordVanillaLabel,
                new Vector2(_recordVanillaCheckboxRect.X + 44, _recordVanillaCheckboxRect.Y + 4),
                Game1.textColor
            );

            // Disclaimer.
            string disclaimer = ModEntry.SHelper.Translation
                .Get("AdvancedSettings.Disclaimer")
                .Default("Due to AI prediction mechanisms, this feature may have unpredictable behavior. Save often.");

            var disclaimerSize = Game1.smallFont.MeasureString(disclaimer);

            b.DrawString(
                Game1.smallFont,
                disclaimer,
                new Vector2(
                    xPositionOnScreen + (MenuWidth - disclaimerSize.X) / 2,
                    yPositionOnScreen + MenuHeight - 48
                ),
                Color.Gray
            );

            // Hover tooltips.
            if (_checkboxRect.Contains(mouseX, mouseY))
            {
                string tooltip = ModEntry.SHelper.Translation
                    .Get("AdvancedSettings.InfiniteChatTooltip")
                    .Default("Allows unlimited chat sessions without time limits.");

                IClickableMenu.drawHoverText(b, tooltip, Game1.smallFont);
            }
            else if (_vanillaFirstCheckboxRect.Contains(mouseX, mouseY))
            {
                string vanillaFirstTooltip = ModEntry.SHelper.Translation
                    .Get("AdvancedSettings.VanillaFirstTooltip")
                    .Default("Wait until all native in-game dialogue is exhausted before triggering AI dialogue.");

                IClickableMenu.drawHoverText(b, vanillaFirstTooltip, Game1.smallFont);
            }
            else if (_recordVanillaCheckboxRect.Contains(mouseX, mouseY))
            {
                string tooltip = ModEntry.SHelper.Translation
                    .Get("AdvancedSettings.RecordVanillaDialogueTooltip")
                    .Default("Inject vanilla lines into AI memory. Turn OFF to prevent the AI from obsessing over repetitive game dialogue.");

                IClickableMenu.drawHoverText(b, tooltip, Game1.smallFont);
            }

            drawMouse(b);
        }
    }
}