// Farm_MailboxIndicator_Patch.cs
// VM-002R: Harmony Postfix on Farm.draw(SpriteBatch b). When only ValleyMail is
// pending (vanilla mailbox empty), draws the vanilla-style waving envelope above
// the player's mailbox; when the vanilla envelope is already drawing it stays
// silent to avoid double indicators. 技术阶梯说明：原版信封绘制位于 Farm.draw 批内；
// 世界批次为 SpriteSortMode.Deferred（Game1.cs:13618），SMAPI Display.RenderedWorld
// 追加绘制无法复现批内位置，故取 Harmony Postfix（阶梯第 3 级正当使用）。
using System;
using HarmonyLib;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using StardewValley;

namespace ValleytalkReborn
{
    [HarmonyPatch(typeof(Farm), nameof(Farm.draw))]
    public class Farm_MailboxIndicator_Patch
    {
        private static bool _drawFailureLogged;

        public static void Postfix(SpriteBatch b)
        {
            if (Game1.mailbox.Count > 0 || !ValleyMailManager.HasPendingMail())
            {
                return;
            }
            try
            {
                // Verbatim vanilla envelope block (Farm.cs:1468-1472).
                float yOffset = 4f * (float)Math.Round(Math.Sin(Game1.currentGameTime.TotalGameTime.TotalMilliseconds / 250.0), 2);
                Point mailbox_position = Game1.player.getMailboxPosition();
                float draw_layer = (float)((mailbox_position.X + 1) * 64) / 10000f + (float)(mailbox_position.Y * 64) / 10000f;
                b.Draw(Game1.mouseCursors, Game1.GlobalToLocal(Game1.viewport, new Vector2(mailbox_position.X * 64, mailbox_position.Y * 64 - 96 - 48 + yOffset)), new Rectangle(141, 465, 20, 24), Color.White * 0.75f, 0f, Vector2.Zero, 4f, SpriteEffects.None, draw_layer + 1E-06f);
                b.Draw(Game1.mouseCursors, Game1.GlobalToLocal(Game1.viewport, new Vector2(mailbox_position.X * 64 + 32 + 4, mailbox_position.Y * 64 - 64 - 24 - 8 + yOffset)), new Rectangle(189, 423, 15, 13), Color.White, 0f, new Vector2(7f, 6f), 4f, SpriteEffects.None, draw_layer + 1E-05f);
            }
            catch (Exception ex)
            {
                // Pure decoration: warn once per session, skip this frame's indicator.
                if (!_drawFailureLogged)
                {
                    _drawFailureLogged = true;
                    ModEntry.SMonitor?.Log($"[ValleyMail] Envelope indicator draw failed (indicator disabled for this session): {ex}", StardewModdingAPI.LogLevel.Warn);
                }
            }
        }
    }
}
