// GameLocation_Mailbox_Patch.cs
// VM-002R: Harmony Prefix on GameLocation.mailbox() — the sole call site is the
// tile action "Mailbox" branch (GameLocation.cs:9929). While a ValleyMail message
// is pending, the vanilla mailbox opens the ValleyLetterViewerMenu instead; the
// moment no ValleyMail is pending the prefix passes through untouched, so vanilla
// letters always show. Any failure degrades to vanilla (the mailbox must never
// become unopenable).
using System;
using HarmonyLib;
using StardewValley;

namespace ValleytalkReborn
{
    [HarmonyPatch(typeof(GameLocation), nameof(GameLocation.mailbox))]
    public class GameLocation_Mailbox_Patch
    {
        public static bool Prefix()
        {
            try
            {
                if (!ValleyMailManager.HasPendingMail())
                {
                    return true;
                }
                Game1.activeClickableMenu = new ValleyLetterViewerMenu(ValleyMailManager.PeekNextPendingMail(), isReadOnly: false);
                return false;
            }
            catch (Exception ex)
            {
                ModEntry.SMonitor?.Log($"[ValleyMail] Mailbox intercept failed; falling back to vanilla mailbox: {ex}", StardewModdingAPI.LogLevel.Error);
                return true;
            }
        }
    }
}
