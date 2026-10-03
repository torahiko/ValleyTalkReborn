using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewModdingAPI;
using StardewValley;
using StardewValley.BellsAndWhistles;
using StardewValley.Menus;

namespace ValleytalkReborn
{
    /// <summary>
    /// VM-004R: archived-mail drawer (800x600 vanilla dialogue-box parchment).
    /// Shows only ValleyMailManager.GetArchivedMails(), copied and reversed so the
    /// newest read sits on top; never mutates the input list. Four visible 128px
    /// rows between a scroll-up and scroll-down arrow, wheel + arrow scrolling.
    /// Clicking a row reopens the letter in ValleyLetterViewerMenu(isReadOnly:
    /// true) — no attachment granting, no archiving, no chaining (VM-003R
    /// contract). A sender without a portrait asset falls back to the vanilla
    /// envelope sprite (same mouseCursors source as the mailbox indicator).
    /// </summary>
    public sealed class ValleyMailDrawerMenu : IClickableMenu
    {
        private const int VisibleRows = 4;
        private const int RowHeight = 128;
        private const int ListTop = 48;

        private readonly List<ValleyMailMessage> _mails;
        private int _scrollOffset;

        private readonly ClickableTextureComponent _scrollUpButton;
        private readonly ClickableTextureComponent _scrollDownButton;

        // Portrait cache: null value = sender has no portrait asset (fallback used).
        private readonly Dictionary<string, Texture2D> _portraitCache = new Dictionary<string, Texture2D>();

        public ValleyMailDrawerMenu(IReadOnlyList<ValleyMailMessage> archivedMails)
            : base((int)Utility.getTopLeftPositionForCenteringOnScreen(800, 600, 0, 0).X, (int)Utility.getTopLeftPositionForCenteringOnScreen(800, 600, 0, 0).Y, 800, 600, true)
        {
            _mails = new List<ValleyMailMessage>();
            for (int i = archivedMails.Count - 1; i >= 0; i--)
            {
                _mails.Add(archivedMails[i]);
            }

            // Arrows reuse the verified paging source rects from the letter viewer
            // (scale 3f so both fit inside the 600px box around the 4x128 row list).
            _scrollUpButton = new ClickableTextureComponent(new Rectangle(xPositionOnScreen + width / 2 - 18, yPositionOnScreen + 8, 36, 33), Game1.mouseCursors, new Rectangle(352, 495, 12, 11), 3f, false);
            _scrollDownButton = new ClickableTextureComponent(new Rectangle(xPositionOnScreen + width / 2 - 18, yPositionOnScreen + height - 38, 36, 33), Game1.mouseCursors, new Rectangle(365, 495, 12, 11), 3f, false);
        }

        public override void receiveLeftClick(int x, int y, bool playSound = true)
        {
            if (upperRightCloseButton != null && upperRightCloseButton.containsPoint(x, y))
            {
                if (playSound)
                {
                    Game1.playSound("bigDeSelect", null);
                }
                exitThisMenu(true);
                return;
            }
            if (_scrollUpButton.containsPoint(x, y))
            {
                SetScrollOffset(_scrollOffset - 1);
                return;
            }
            if (_scrollDownButton.containsPoint(x, y))
            {
                SetScrollOffset(_scrollOffset + 1);
                return;
            }
            for (int slot = 0; slot < VisibleRows; slot++)
            {
                int index = _scrollOffset + slot;
                if (index >= _mails.Count)
                {
                    break;
                }
                if (!GetRowBounds(slot).Contains(x, y))
                {
                    continue;
                }
                ValleyMailMessage mail = _mails[index];
                try
                {
                    // Read-only re-read: VM-003R guarantees no attachments, no
                    // archiving and no chaining for isReadOnly viewers.
                    Game1.activeClickableMenu = new ValleyLetterViewerMenu(mail, isReadOnly: true);
                }
                catch (Exception ex)
                {
                    ModEntry.SMonitor?.Log($"[ValleyMail] Failed to reopen archived mail '{mail?.MailId}'; no menu assigned: {ex}", LogLevel.Error);
                }
                return;
            }
        }

        public override void receiveScrollWheelAction(int direction)
        {
            SetScrollOffset(_scrollOffset - direction);
        }

        public override void receiveKeyPress(Keys key)
        {
            if (key == Keys.None)
            {
                return;
            }
            if (Game1.options.doesInputListContain(Game1.options.menuButton, key))
            {
                exitThisMenu(true);
                return;
            }
            base.receiveKeyPress(key);
        }

        public override void draw(SpriteBatch b)
        {
            if (!Game1.options.showClearBackgrounds)
            {
                b.Draw(Game1.fadeToBlackRect, Game1.graphics.GraphicsDevice.Viewport.Bounds, Color.Black * 0.4f);
            }
            Game1.drawDialogueBox(xPositionOnScreen, yPositionOnScreen, width, height, false, true);

            if (_mails.Count == 0)
            {
                string title = ModConfigMenu.GetUIString("drawerEmptyTitle", "Mail Drawer");
                string body = ModConfigMenu.GetUIString("drawerEmptyBody", "No archived letters yet.");
                SpriteText.drawStringHorizontallyCenteredAt(b, title, xPositionOnScreen + width / 2, yPositionOnScreen + height / 2 - 60, 999999, -1, 999999, 1f, 0.88f, false, null, 99999);
                SpriteText.drawStringHorizontallyCenteredAt(b, body, xPositionOnScreen + width / 2, yPositionOnScreen + height / 2 + 10, 999999, -1, 999999, 1f, 0.88f, false, null, 99999);
            }
            else
            {
                for (int slot = 0; slot < VisibleRows; slot++)
                {
                    int index = _scrollOffset + slot;
                    if (index >= _mails.Count)
                    {
                        break;
                    }
                    DrawMailRow(b, _mails[index], GetRowBounds(slot));
                }
            }

            _scrollUpButton.draw(b);
            _scrollDownButton.draw(b);
            base.draw(b);
            base.drawMouse(b, false, -1);
        }

        // ── Row rendering ──

        private Rectangle GetRowBounds(int slot)
        {
            return new Rectangle(xPositionOnScreen + 16, yPositionOnScreen + ListTop + slot * RowHeight, width - 32, RowHeight);
        }

        private void DrawMailRow(SpriteBatch b, ValleyMailMessage mail, Rectangle row)
        {
            DrawSenderPortrait(b, mail, row.X + 8, row.Y);

            int textLeft = row.X + 152;
            int textRight = row.Right - 176;
            int textCenter = (textLeft + textRight) / 2;

            string title = string.IsNullOrEmpty(mail.Title) ? mail.MailId : mail.Title;
            SpriteText.drawStringHorizontallyCenteredAt(b, title, textCenter, row.Y + 28, 999999, -1, 999999, 1f, 0.88f, false, null, textRight - textLeft);
            SpriteText.drawStringHorizontallyCenteredAt(b, GetMailDateText(mail), textCenter, row.Y + 76, 999999, -1, 999999, 1f, 0.88f, false, null, textRight - textLeft);

            if (!string.IsNullOrEmpty(mail.Tag))
            {
                SpriteText.drawStringHorizontallyCenteredAt(b, mail.Tag, row.Right - 88, row.Y + 52, 999999, -1, 999999, 1f, 0.88f, false, Color.Orange, 160);
            }
        }

        private void DrawSenderPortrait(SpriteBatch b, ValleyMailMessage mail, int x, int y)
        {
            Texture2D portrait = GetSenderPortrait(mail.SenderNpcName);
            if (portrait != null)
            {
                // 64px neutral frame at 2f = 128px, filling the row height.
                b.Draw(portrait, new Vector2(x, y), new Rectangle(0, 0, 64, 64), Color.White, 0f, Vector2.Zero, 2f, SpriteEffects.None, 0.9f);
            }
            else
            {
                // Envelope fallback (15x13 at 4f) centered in the 128px portrait box.
                b.Draw(Game1.mouseCursors, new Vector2(x + 34, y + 38), new Rectangle(189, 423, 15, 13), Color.White, 0f, Vector2.Zero, 4f, SpriteEffects.None, 0.9f);
            }
        }

        private Texture2D GetSenderPortrait(string senderName)
        {
            if (senderName == null)
            {
                return null;
            }
            if (_portraitCache.TryGetValue(senderName, out Texture2D cached))
            {
                return cached;
            }
            Texture2D portrait = null;
            try
            {
                portrait = Game1.content.Load<Texture2D>("Portraits\\" + senderName);
            }
            catch (Exception ex)
            {
                // Declared RECOVERABLE path: Trace once per sender, envelope fallback.
                ModEntry.SMonitor?.Log($"[ValleyMail] No portrait for '{senderName}' ({ex.GetType().Name}); envelope fallback used.", LogLevel.Trace);
                portrait = null;
            }
            _portraitCache[senderName] = portrait;
            return portrait;
        }

        /// <summary>
        /// Relative-date label for the delivery day. getDateString(offset) adds the
        /// offset onto the raw day-of-month (Utility.cs:3678), so any offset that
        /// leaves the current month renders garbage — fall back to the raw day.
        /// </summary>
        private string GetMailDateText(ValleyMailMessage mail)
        {
            try
            {
                int offset = (int)(mail.DeliveryDay - Game1.Date.TotalDays);
                int targetDay = Game1.dayOfMonth + offset;
                if (offset > 0 || targetDay < 1 || targetDay > 28)
                {
                    return "Day " + mail.DeliveryDay;
                }
                return Utility.getDateString(offset);
            }
            catch (Exception ex)
            {
                ModEntry.SMonitor?.Log($"[ValleyMail] getDateString failed for mail '{mail.MailId}'; falling back to the raw day: {ex.GetType().Name}", LogLevel.Trace);
                return "Day " + mail.DeliveryDay;
            }
        }

        private void SetScrollOffset(int value)
        {
            int max = Math.Max(0, _mails.Count - VisibleRows);
            _scrollOffset = Math.Clamp(value, 0, max);
        }
    }
}
