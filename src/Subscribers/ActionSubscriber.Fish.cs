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
/// </summary>
internal static class FishSubscriber
{
    private static bool _initialized   = false;
    private static bool _wasFishCaught = false;

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

    private static Func<FishingRod, Item> _getCaughtItem = null;
    private static bool _accessorResolved = false;

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
        bool isZh     = LocalizedContentManager.CurrentLanguageCode.ToString()
            .StartsWith("zh", StringComparison.OrdinalIgnoreCase);

        if (caught == null)
        {
            PerceptionManager.Instance.Record("Fish",
                isZh ? "你注意到农夫刚才在附近钓起了一条收获。" : "You noticed the farmer reeling in a catch nearby.",
                null, lifetime, isLandmark: false);
            return;
        }

        string fishName    = caught.DisplayName ?? caught.Name ?? (isZh ? "鱼" : "fish");
        string fishItemId  = caught.ItemId ?? string.Empty;
        bool   isLegendary = !string.IsNullOrEmpty(fishItemId) && LegendaryFishIds.Contains(fishItemId);

        string template;
        if (isLegendary)
        {
            ExtremeActivityTracker.NotifyLegendaryFishCaught();
            template = isZh
                ? PerceptionManager.PickVariant(new[]
                {
                    $"农夫刚刚钓起了一条【{fishName}】——那可是传说中的神鱼！消息已经在整个小镇传开了。",
                    $"你听到了外面的动静：农夫竟然把传说中的【{fishName}】钓了上来，大家都在热烈议论。",
                    $"现在小镇里几乎人尽皆知了——农夫捕获了极为罕见的传说鱼【{fishName}】。"
                })
                : PerceptionManager.PickVariant(new[]
                {
                    $"The farmer just caught a {fishName} — a legendary fish! Word spread through the whole valley.",
                    $"You heard the commotion: the farmer pulled a {fishName} out of the water. People are already talking.",
                    $"Everyone seems to know by now — the farmer caught a {fishName}, one of the rarest fish in the valley."
                });
        }
        else
        {
            template = isZh
                ? PerceptionManager.PickVariant(new[]
                {
                    $"你看到农夫刚才在附近的水边钓上来了一条【{fishName}】。",
                    $"农夫刚才就在不远处钓鱼，顺利拉上来了一条【{fishName}】。",
                    $"你瞥见农夫正举着刚刚钓到的【{fishName}】。"
                })
                : PerceptionManager.PickVariant(new[]
                {
                    $"You saw the farmer pull a {fishName} out of the water nearby.",
                    $"The farmer was fishing close by and just landed a {fishName}.",
                    $"You caught a glimpse of the farmer holding up a {fishName} they just caught."
                });
        }

        PerceptionManager.Instance.Record(
            key:           "Fish",
            template:      template,
            npcName:       null,
            lifetimeHours: isLegendary ? 20 : lifetime,
            isLandmark:    isLegendary
        );
    }

    private static Item TryGetCaughtFish()
    {
        if (Game1.player?.CurrentTool is not FishingRod rod) return null;

        if (!_accessorResolved)
        {
            _getCaughtItem    = BuildFishItemAccessor();
            _accessorResolved = true;
        }

        return _getCaughtItem?.Invoke(rod);
    }

    private static Func<FishingRod, Item> BuildFishItemAccessor()
    {
        var type = typeof(FishingRod);
        const BindingFlags bf = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        var prop = type.GetProperty("lastCatch", bf);
        if (prop != null && typeof(Item).IsAssignableFrom(prop.PropertyType))
            return r => prop.GetValue(r) as Item;

        var field = type.GetField("lastCatch", bf);
        if (field != null && typeof(Item).IsAssignableFrom(field.FieldType))
            return r => field.GetValue(r) as Item;

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

                    string qualifiedId = id.StartsWith("(") ? id : $"(O){id}";
                    return ItemRegistry.Create(qualifiedId, allowNull: true);
                }
                catch { return null; }
            };
        }
        return null;
    }
}