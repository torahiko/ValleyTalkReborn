using System;
using System.Collections.Generic;
using System.Reflection;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Tools;

namespace ValleytalkReborn;

/// <summary>
/// Subscribes to fish catch events and records perceptions when the player catches a fish.
/// Legendary fish trigger an IsLandmark broadcast; regular fish are eyewitness-only.
///
/// FishingRod field resolution is cached on first use via a compiled delegate,
/// so reflection only runs once per session regardless of how many fish are caught.
/// </summary>
internal static class FishSubscriber
{
    private static bool _initialized   = false;
    private static bool _wasFishCaught = false;

    /// <summary>
    /// ItemIds of all legendary fish (base game + Qi's Extended Family).
    /// These trigger a town-wide Landmark broadcast instead of a local eyewitness entry.
    /// </summary>
    private static readonly HashSet<string> LegendaryFishIds =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "159",  // Crimsonfish
            "160",  // Angler
            "163",  // Legend
            "164",  // Mutant Carp
            "682",  // Glacierfish
            "775",  // Legend II
            "876",  // Angler II
            "877",  // Glacierfish Jr.
            "878",  // Ms. Angler
            "879",  // Son of Crimsonfish
        };

    // ── Cached accessor ──────────────────────────────────────────
    // Populated on first fish catch; null = "not yet resolved".
    // Func<FishingRod, Item>  — returns the caught Item, or null if unavailable.
    private static Func<FishingRod, Item> _getCaughtItem = null;
    private static bool _accessorResolved = false;

    // ─────────────────────────────────────────────────────────────

    public static void Initialize()
    {
        if (_initialized || ModEntry.SHelper == null) return;
        ModEntry.SHelper.Events.GameLoop.UpdateTicked += OnUpdateTicked;
        _initialized = true;
    }

    public static void Cleanup()
    {
        if (!_initialized || ModEntry.SHelper == null) return;
        ModEntry.SHelper.Events.GameLoop.UpdateTicked -= OnUpdateTicked;
        _wasFishCaught    = false;
        _initialized      = false;
        // 保留 _getCaughtItem 缓存 — FishingRod 类型在一次游戏会话里不会改变
    }

    private static void OnUpdateTicked(object sender, UpdateTickedEventArgs e)
    {
        if (!e.IsMultipleOf(15)) return;
        var player = Game1.player;
        if (player == null) return;

        bool currentlyCaught = player.CurrentTool is FishingRod rod && rod.fishCaught;

        if (currentlyCaught && !_wasFishCaught)
            RecordFishPerception();

        _wasFishCaught = currentlyCaught;
    }

    private static void RecordFishPerception()
    {
        int  lifetime = ModEntry.Config.PerceptionActionLifetime;
        Item caught   = TryGetCaughtFish();

        if (caught == null)
        {
            PerceptionManager.Instance.Record("Fish",
                "You noticed the farmer reeling in a catch nearby.",
                null, lifetime, isLandmark: false);
            return;
        }

        string fishName   = caught.DisplayName ?? caught.Name ?? "fish";
        string fishItemId = caught.ItemId ?? string.Empty;
        bool   isLegendary = !string.IsNullOrEmpty(fishItemId)&& LegendaryFishIds.Contains(fishItemId);

        string template = isLegendary
            ? PerceptionManager.PickVariant(new[]
            {
                $"The farmer just caught a {fishName} — a legendary fish! Word spread through the whole valley.",
                $"You heard the commotion: the farmer pulled a {fishName} out of the water. People are already talking.",
                $"Everyone seems to know by now — the farmer caught a {fishName}, one of the rarest fish in the valley.",
            })
            : PerceptionManager.PickVariant(new[]
            {
                $"You saw the farmer pull a {fishName} out of the water nearby.",
                $"The farmer was fishing close by and just landed a {fishName}.",
                $"You caught a glimpse of the farmer holding up a {fishName} they just caught.",
            });

        PerceptionManager.Instance.Record(
            key:           "Fish",
            template:      template,
            npcName:       null,
            lifetimeHours: isLegendary ? 20 : lifetime,  // 传说鱼全天有效，普通鱼保持短暂
            isLandmark:    isLegendary
            );
    }

    // ─────────────────────────────────────────────────────────────
    //  FishingRod field resolution (cached, runs reflection once)
    // ─────────────────────────────────────────────────────────────

    private static Item TryGetCaughtFish()
    {
        if (Game1.player?.CurrentTool is not FishingRod rod) return null;

        // Build and cache the accessor on first call
        if (!_accessorResolved)
        {
            _getCaughtItem    = BuildFishItemAccessor();
            _accessorResolved = true;

            ModEntry.SMonitor?.Log(
                _getCaughtItem != null
                    ? "[FishSubscriber] FishingRod item accessor resolved successfully."
                    : "[FishSubscriber] FishingRod item accessor could not be resolved — fish name will be unavailable.",
                LogLevel.Debug);
        }

        return _getCaughtItem?.Invoke(rod);
    }

    /// <summary>
    /// Probes FishingRod for the caught-fish field/property using known names across
    /// SDV versions, and returns a cached delegate. Returns null if nothing is found.
    ///
    /// Probe order (most → least specific):
    ///   1. Property "lastCatch"    (Item)          — 1.6.8+
    ///   2. Field   "lastCatch"     (Item)          — some builds
    ///   3. Field   "whichFish"     (string/NetString) → ItemRegistry.Create
    /// </summary>
    private static Func<FishingRod, Item> BuildFishItemAccessor()
    {
        var type = typeof(FishingRod);
        const BindingFlags bf = BindingFlags.Public| BindingFlags.NonPublic
                              | BindingFlags.Instance;

        // ── 1. Property "lastCatch" returning Item ───────────────
        var prop = type.GetProperty("lastCatch", bf);
        if (prop != null && typeof(Item).IsAssignableFrom(prop.PropertyType))
            return r => prop.GetValue(r) as Item;

        // ── 2. Field "lastCatch" of type Item ────────────────────
        var field = type.GetField("lastCatch", bf);
        if (field != null && typeof(Item).IsAssignableFrom(field.FieldType))
            return r => field.GetValue(r) as Item;

        // ── 3. Field "whichFish" (string or NetString) ───────────
// ── 3. Field "whichFish" (string or NetString) ───────────
        var whichFishField = type.GetField("whichFish", bf);
        if (whichFishField != null)
        {
            return r =>
            {
                try
                {
                    var raw = whichFishField.GetValue(r);
                    string id = raw as string ?? (raw?.GetType().GetProperty("Value")?.GetValue(raw) as string);
                    if (string.IsNullOrEmpty(id)) return null;

                    // 补齐 SDV 1.6 标准对象限定前缀 (O)，例如 "(O)159"
                    string qualifiedId = id.StartsWith("(") ? id : $"(O){id}";
                    return ItemRegistry.Create(qualifiedId, allowNull: true);
                }
                catch { return null; }
            };
        }
        return null;  // 全部探测失败，兜底路径会用通用模板
    }
}