using System;
using System.Collections.Generic;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using StardewModdingAPI;
using StardewValley;
using StardewValley.BellsAndWhistles;
using StardewValley.Menus;
using StardewValley.Triggers;

namespace ValleytalkReborn
{
    /// <summary>
    /// VM-003R: replica of the vanilla LetterViewerMenu parchment layout for
    /// ValleyMail messages. 1280x720 centered letterBG sheet, SpriteText body
    /// split by height into sections, vanilla back/forward paging with "shwip".
    /// Attachments: Item slots are prebuilt via ItemRegistry at construction and
    /// collected interactively; Money is collected by clicking the money line.
    /// Close path (cleanupBeforeExit) stays non-interactive per the D3 revision:
    /// uncollected money is credited, item overflow goes to ground debris via
    /// addItemToInventory + createItemDebris (never a chained ItemGrabMenu).
    /// A fresh mail is archived, its TriggerActionOnRead runs, then the next
    /// ValleyMail or a non-empty vanilla mailbox chains in. isReadOnly (drawer
    /// re-read) hides attachments and skips archive/trigger/chaining entirely.
    /// </summary>
    public sealed class ValleyLetterViewerMenu : IClickableMenu
    {
        private const string LetterBgAssetName = "LooseSprites\\letterBG";

        private readonly ValleyMailMessage _mail;
        private readonly bool _isReadOnly;

        private Texture2D _letterTexture;
        private readonly int _whichBG = 0;
        private float _scale;

        private List<string> _mailMessage = new List<string>();
        private int _page;

        private ClickableTextureComponent _backButton;
        private ClickableTextureComponent _forwardButton;

        // Collectible Item attachments: slot parallel to the attachment it grants.
        private readonly List<ClickableComponent> _itemSlots = new List<ClickableComponent>();
        private readonly List<MailAttachment> _slotAttachments = new List<MailAttachment>();

        // Clickable money line at the letter bottom; text is re-rendered each frame.
        private ClickableComponent _moneySlot;

        public ValleyLetterViewerMenu(ValleyMailMessage mail, bool isReadOnly = false)
            : base((int)Utility.getTopLeftPositionForCenteringOnScreen(1280, 720, 0, 0).X, (int)Utility.getTopLeftPositionForCenteringOnScreen(1280, 720, 0, 0).Y, 1280, 720, true)
        {
            _mail = mail;
            _isReadOnly = isReadOnly;
            Game1.playSound("shwip", null);
            _backButton = new ClickableTextureComponent(new Rectangle(xPositionOnScreen + 32, yPositionOnScreen + height - 32 - 64, 48, 44), Game1.mouseCursors, new Rectangle(352, 495, 12, 11), 4f, false)
            {
                myID = 101,
                rightNeighborID = 102
            };
            _forwardButton = new ClickableTextureComponent(new Rectangle(xPositionOnScreen + width - 32 - 48, yPositionOnScreen + height - 32 - 64, 48, 44), Game1.mouseCursors, new Rectangle(365, 495, 12, 11), 4f, false)
            {
                myID = 102,
                leftNeighborID = 101
            };
            _letterTexture = Game1.temporaryContent.Load<Texture2D>(LetterBgAssetName);

            // VM-003R flow 2: per source page, substitute "@" with the player name
            // then split into height-constrained render sections (LetterViewerMenu.cs:46/:158).
            foreach (string page in _mail.Pages)
            {
                string text = (page ?? string.Empty).Replace("@", Game1.player.Name);
                _mailMessage.AddRange(SpriteText.getStringBrokenIntoSectionsOfHeight(text, width - 64, height - 128));
            }

            if (!_isReadOnly)
            {
                BuildAttachmentSlots();
            }
        }

        public override void receiveLeftClick(int x, int y, bool playSound = true)
        {
            if (_scale < 1f)
            {
                return;
            }
            if (_backButton.containsPoint(x, y) && _page > 0)
            {
                _page--;
                Game1.playSound("shwip", null);
                return;
            }
            if (_forwardButton.containsPoint(x, y) && _page < _mailMessage.Count - 1)
            {
                _page++;
                Game1.playSound("shwip", null);
                return;
            }
            if (upperRightCloseButton != null && readyToClose() && upperRightCloseButton.containsPoint(x, y))
            {
                if (playSound)
                {
                    Game1.playSound("bigDeSelect", null);
                }
                exitThisMenu(true);
                return;
            }
            if (!_isReadOnly && _moneySlot != null && _moneySlot.containsPoint(x, y))
            {
                MailAttachment money = FindFirstUncollectedMoney();
                if (money != null)
                {
                    Game1.playSound("coin", null);
                    Game1.player.Money += money.MoneyAmount;
                    money.IsCollected = true;
                    ModEntry.SMonitor?.Log($"[ValleyMail] Money attachment ({money.MoneyAmount}g) collected from mail '{_mail.MailId}'.", LogLevel.Trace);
                }
                return;
            }
            if (!_isReadOnly)
            {
                for (int i = 0; i < _itemSlots.Count; i++)
                {
                    ClickableComponent slot = _itemSlots[i];
                    if (slot.containsPoint(x, y) && slot.item != null)
                    {
                        Game1.playSound("coin", null);
                        // Overflow beyond the inventory is queued by the vanilla
                        // mechanism into Game1.nextClickableMenu (Game1.cs:18439).
                        Game1.player.addItemByMenuIfNecessary(slot.item, null, false);
                        slot.item = null;
                        _slotAttachments[i].IsCollected = true;
                        ModEntry.SMonitor?.Log($"[ValleyMail] Item attachment collected via menu from mail '{_mail.MailId}'.", LogLevel.Trace);
                        _itemSlots.RemoveAt(i);
                        _slotAttachments.RemoveAt(i);
                        return;
                    }
                }
            }
        }

        public override void receiveKeyPress(Keys key)
        {
            if (key == Keys.None)
            {
                return;
            }
            if (Game1.options.doesInputListContain(Game1.options.menuButton, key) && readyToClose())
            {
                exitThisMenu(true);
                return;
            }
            base.receiveKeyPress(key);
        }

        public override void update(GameTime time)
        {
            base.update(time);
            _forwardButton.visible = _page < _mailMessage.Count - 1;
            _backButton.visible = _page > 0;
            if (_scale < 1f)
            {
                _scale += time.ElapsedGameTime.Milliseconds * 0.003f;
                if (_scale >= 1f)
                {
                    _scale = 1f;
                }
            }
            if (_page < _mailMessage.Count - 1 && !_forwardButton.containsPoint(Game1.getOldMouseX(), Game1.getOldMouseY()))
            {
                _forwardButton.scale = 4f + (float)Math.Sin(time.TotalGameTime.Milliseconds / 201.06192982974676) / 1.5f;
            }
        }

        public override void draw(SpriteBatch b)
        {
            if (!Game1.options.showClearBackgrounds)
            {
                b.Draw(Game1.fadeToBlackRect, Game1.graphics.GraphicsDevice.Viewport.Bounds, Color.Black * 0.4f);
            }
            b.Draw(_letterTexture, new Vector2(xPositionOnScreen + width / 2, yPositionOnScreen + height / 2), new Rectangle(_whichBG % 4 * 320, _whichBG >= 4 ? 204 + (_whichBG / 4 - 1) * 180 : 0, 320, 180), Color.White, 0f, new Vector2(160f, 90f), 4f * _scale, SpriteEffects.None, 0.86f);
            if (_scale == 1f)
            {
                SpriteText.drawString(b, _mailMessage[_page], xPositionOnScreen + 32, yPositionOnScreen + 32, 999999, width - 64, 999999, 0.75f, 0.865f, false, -1, "", null, SpriteText.ScrollTextAlignment.Left);
                if (!_isReadOnly)
                {
                    foreach (ClickableComponent slot in _itemSlots)
                    {
                        b.Draw(_letterTexture, slot.bounds, new Rectangle(_whichBG * 24, 180, 24, 24), Color.White);
                        if (slot.item != null)
                        {
                            slot.item.drawInMenu(b, new Vector2(slot.bounds.X + 16, slot.bounds.Y + 16), slot.scale);
                        }
                    }
                    MailAttachment money = FindFirstUncollectedMoney();
                    if (money != null)
                    {
                        string moneyText = Game1.content.LoadString("Strings\\UI:LetterViewer_MoneyIncluded", money.MoneyAmount);
                        SpriteText.drawString(b, moneyText, xPositionOnScreen + width / 2 - SpriteText.getWidthOfString(moneyText, 999999) / 2, yPositionOnScreen + height - 96, 999999, -1, 9999, 0.75f, 0.865f, false, -1, "", null, SpriteText.ScrollTextAlignment.Left);
                    }
                }
                base.draw(b);
                _forwardButton.draw(b);
                _backButton.draw(b);
            }
            if (!Game1.options.SnappyMenus || _scale >= 1f)
            {
                base.drawMouse(b, false, -1);
            }
        }

        protected override void cleanupBeforeExit()
        {
            if (!_isReadOnly)
            {
                // D3 revision: the close path must stay non-interactive — money is
                // credited directly and item overflow drops as ground debris instead
                // of chaining an ItemGrabMenu (close-time menu stacking hazard).
                foreach (MailAttachment attachment in _mail.Attachments)
                {
                    if (attachment == null || attachment.IsCollected || attachment.Type != MailAttachmentType.Money)
                    {
                        continue;
                    }
                    Game1.player.Money += attachment.MoneyAmount;
                    attachment.IsCollected = true;
                    ModEntry.SMonitor?.Log($"[ValleyMail] Uncollected money auto-collected on close from mail '{_mail.MailId}'.", LogLevel.Trace);
                }
                for (int i = _itemSlots.Count - 1; i >= 0; i--)
                {
                    Item item = _itemSlots[i].item;
                    MailAttachment attachment = _slotAttachments[i];
                    if (item == null)
                    {
                        continue;
                    }
                    if (Game1.player.couldInventoryAcceptThisItem(item))
                    {
                        // Fully added -> null; partial fit -> same item with the
                        // remainder stack (Farmer.cs:3605 doc).
                        Item remainder = Game1.player.addItemToInventory(item);
                        if (remainder != null)
                        {
                            Game1.createItemDebris(remainder, Game1.player.getStandingPosition(), Game1.player.FacingDirection, Game1.currentLocation);
                            ModEntry.SMonitor?.Log("[ValleyMail] Attachment overflowed the inventory on close; remainder dropped at the player's feet.", LogLevel.Warn);
                        }
                    }
                    else
                    {
                        Game1.createItemDebris(item, Game1.player.getStandingPosition(), Game1.player.FacingDirection, Game1.currentLocation);
                        ModEntry.SMonitor?.Log("[ValleyMail] Inventory could not accept the attachment on close; item dropped at the player's feet.", LogLevel.Warn);
                    }
                    attachment.IsCollected = true;
                    _itemSlots[i].item = null;
                }
                _itemSlots.Clear();
                _slotAttachments.Clear();

                ValleyMailManager.MarkCurrentMailAsRead(_mail);

                if (!string.IsNullOrWhiteSpace(_mail.TriggerActionOnRead))
                {
                    // Parameter order verified: error first, then exception.
                    if (!TriggerActionManager.TryRunAction(_mail.TriggerActionOnRead, out string error, out Exception actionException))
                    {
                        ModEntry.SMonitor?.Log($"[ValleyMail] TriggerActionOnRead failed for mail '{_mail.MailId}': {error} {actionException}", LogLevel.Warn);
                    }
                }

                if (!Game1.eventUp)
                {
                    if (ValleyMailManager.HasPendingMail())
                    {
                        ValleyMailMessage next = ValleyMailManager.PeekNextPendingMail();
                        if (next == null)
                        {
                            ModEntry.SMonitor?.Log("[ValleyMail] Chaining aborted: pending mail existed but PeekNextPendingMail returned null.", LogLevel.Error);
                        }
                        else
                        {
                            Game1.activeClickableMenu = new ValleyLetterViewerMenu(next);
                        }
                    }
                    else if (Game1.mailbox.Count > 0)
                    {
                        // Hard gate: calling mailbox() on an empty box pops the vanilla
                        // "no mail today" dialogue (GameLocation.cs:12485-12489).
                        Game1.currentLocation.mailbox();
                    }
                }
            }
            base.cleanupBeforeExit();
        }

        // ── Attachment setup helpers ──

        private void BuildAttachmentSlots()
        {
            List<MailAttachment> itemAttachments = new List<MailAttachment>();
            foreach (MailAttachment attachment in _mail.Attachments)
            {
                if (attachment == null || attachment.IsCollected || attachment.Type != MailAttachmentType.Item)
                {
                    continue;
                }
                itemAttachments.Add(attachment);
            }

            // Row of vanilla-styled 96x96 slots centered on the letter bottom
            // (geometry mirrors the vanilla itemsToGrab bounds).
            int startX = xPositionOnScreen + width / 2 - itemAttachments.Count * 96 / 2;
            int slotY = yPositionOnScreen + height - 32 - 96;
            for (int i = 0; i < itemAttachments.Count; i++)
            {
                MailAttachment attachment = itemAttachments[i];
                Item item;
                try
                {
                    item = ItemRegistry.Create(attachment.QualifiedItemId, attachment.Stack, attachment.Quality);
                }
                catch (Exception ex)
                {
                    // Declared failure path: drop the attachment, reading continues.
                    ModEntry.SMonitor?.Log($"[ValleyMail] ItemRegistry.Create failed for '{attachment.QualifiedItemId}' in mail '{_mail.MailId}': {ex}", LogLevel.Error);
                    attachment.IsCollected = true;
                    continue;
                }
                var slot = new ClickableComponent(new Rectangle(startX + i * 96, slotY, 96, 96), "")
                {
                    item = item
                };
                _itemSlots.Add(slot);
                _slotAttachments.Add(attachment);
            }

            MailAttachment money = FindFirstUncollectedMoney();
            if (money != null)
            {
                string moneyText = Game1.content.LoadString("Strings\\UI:LetterViewer_MoneyIncluded", money.MoneyAmount);
                int moneyWidth = SpriteText.getWidthOfString(moneyText, 999999);
                _moneySlot = new ClickableComponent(new Rectangle(xPositionOnScreen + width / 2 - moneyWidth / 2, yPositionOnScreen + height - 96, moneyWidth, SpriteText.getHeightOfString(moneyText, 999999)), "");
            }
        }

        private MailAttachment FindFirstUncollectedMoney()
        {
            foreach (MailAttachment attachment in _mail.Attachments)
            {
                if (attachment != null && !attachment.IsCollected && attachment.Type == MailAttachmentType.Money)
                {
                    return attachment;
                }
            }
            return null;
        }
    }
}
