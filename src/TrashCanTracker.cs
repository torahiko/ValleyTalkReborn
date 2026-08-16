using System;
using HarmonyLib;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

namespace ValleytalkReborn;

/// <summary>
/// Detects trash can interactions for the gossip system.
///
/// Detection strategy (automatic fallback):
///   Primary   — StatHelper.Get("TrashCansChecked") delta (zero reflection overhead after first call)
///   Fallback  — Harmony postfix on GameLocation.checkAction, tile-based detection
///
/// Only counting is done here. Writing to Track 1 is delegated to ExtremeActivityTracker
/// so the scoring system can decide priority among all extreme activities.
/// </summary>
internal static class TrashCanTracker
{
    private static bool _initialized  = false;
    private static int  _trashCount   = 0;

    // Snapshot taken at DayStarted (Stats path)
    private static uint _snapTrashCount = 0;

    // Whether Stats path is available (resolved on first DayStarted after Initialize)
    private static bool _useStatPath   = false;
    private static bool _pathResolved  = false;

    public static void Initialize(Harmony harmony)
    {
        if (_initialized || ModEntry.SHelper == null) return;

        ModEntry.SHelper.Events.GameLoop.DayStarted += OnDayStarted;
        ModEntry.SHelper.Events.GameLoop.DayEnding  += OnDayEnding;

        // Register patch regardless — it only fires if _useStatPath == false
        try
        {
            harmony.Patch(
                original: AccessTools.Method(typeof(GameLocation), nameof(GameLocation.checkAction)),
                postfix:  new HarmonyMethod(typeof(TrashCanTracker), nameof(Postfix_CheckAction))
            );
            ModEntry.SMonitor?.Log("[TrashCanTracker] Harmony patch registered.", LogLevel.Debug);
        }
        catch (Exception ex)
        {
            ModEntry.SMonitor?.Log(
                $"[TrashCanTracker] Harmony patch failed: {ex.Message}", LogLevel.Warn);
        }

        _initialized = true;
        ModEntry.SMonitor?.Log("[TrashCanTracker] Initialized.", LogLevel.Debug);
    }

    public static void Cleanup()
    {
        if (!_initialized || ModEntry.SHelper == null) return;
        ModEntry.SHelper.Events.GameLoop.DayStarted -= OnDayStarted;
        ModEntry.SHelper.Events.GameLoop.DayEnding  -= OnDayEnding;
        _trashCount   = 0;
        _initialized  = false;
        // Harmony patch follows process lifecycle — no unpatch needed
    }

    /// <summary>Returns today's confirmed trash can search count.</summary>
    public static int GetTodayCount() => _trashCount;

    // ─────────────────────────────────────────────
    //  Day lifecycle
    // ─────────────────────────────────────────────

    private static void OnDayStarted(object sender, DayStartedEventArgs e)
    {
        _trashCount = 0;

        // Resolve detection path on first DayStarted (StatHelper needs Game1.stats ready)
        if (!_pathResolved)
        {
            _pathResolved = true;
            _useStatPath = StatHelper.IsFieldResolved("TrashCansChecked");

            ModEntry.SMonitor?.Log(
                $"[TrashCanTracker] Detection path: {(_useStatPath ? "Stats" : "Harmony patch")}",
                LogLevel.Debug);
        }
        else
        {
            _snapTrashCount = StatHelper.Get("TrashCansChecked");
        }
    }

    private static void OnDayEnding(object sender, DayEndingEventArgs e)
    {
        // Stats path: compute delta at end of day
        if (_useStatPath)
        {
            uint current = StatHelper.Get("TrashCansChecked");
            _trashCount  = (int)(current >= _snapTrashCount ? current - _snapTrashCount : 0);
        }
        // Patch path: _trashCount already accumulated via Postfix_CheckAction
    }

    // ─────────────────────────────────────────────
    //  Harmony postfix (only active when Stats path unavailable)
    // ─────────────────────────────────────────────

    private static void Postfix_CheckAction(
        GameLocation __instance,
        xTile.Dimensions.Location tileLocation,
        Farmer who,
        bool __result)
    {
        // Skip if Stats path is handling detection, or action didn't succeed
        if (_useStatPath || !__result) return;
        if (who == null || !who.IsLocalPlayer) return;

        try
        {
            // SDV 1.6: trash cans are Town map features, not objects in the objects dict.
            // Detect via tile property "Action" == "Garbage X" on the Buildings layer.
            var location = __instance;
            var tile     = new Vector2(tileLocation.X, tileLocation.Y);

            // Primary: check tile action property
            string action = location.doesTileHaveProperty(
                tileLocation.X, tileLocation.Y, "Action", "Buildings");

            if (action != null &&
                action.StartsWith("Garbage", StringComparison.OrdinalIgnoreCase))
            {
                _trashCount++;
                ModEntry.SMonitor?.Log(
                    $"[TrashCanTracker] Trash searched via patch (today: {_trashCount})",
                    LogLevel.Debug);
                return;
            }

            // Fallback: check objects dict (for mod-added trash cans)
            if (__instance.objects.TryGetValue(tile, out var obj) &&
                obj.Name != null &&
                obj.Name.Contains("Trash", StringComparison.OrdinalIgnoreCase))
            {
                _trashCount++;
                ModEntry.SMonitor?.Log(
                    $"[TrashCanTracker] Trash searched via object fallback (today: {_trashCount})",
                    LogLevel.Debug);
            }
        }
        catch (Exception ex)
        {
            ModEntry.SMonitor?.Log(
                $"[TrashCanTracker] Postfix error: {ex.Message}", LogLevel.Trace);
        }
    }
}
