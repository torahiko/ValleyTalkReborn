using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Tools;

namespace ValleyTalk;

/// <summary>
/// Subscribes to fish catch events and records perceptions when the player catches a fish.
/// </summary>
internal static class FishSubscriber
{
    public static void Initialize()
    {
        if (ModEntry.SHelper != null)
        {
            ModEntry.SHelper.Events.GameLoop.UpdateTicked += OnUpdateTicked;
        }
    }

    public static void Cleanup()
    {
        if (ModEntry.SHelper != null)
        {
            ModEntry.SHelper.Events.GameLoop.UpdateTicked -= OnUpdateTicked;
        }
    }

    private static void OnUpdateTicked(object sender, UpdateTickedEventArgs e)
    {
        if (!e.IsMultipleOf(15)) return;

        var player = Game1.player;
        if (player == null) return;

        if (player.CurrentTool is FishingRod rod)
        {
            if (rod.fishCaught)
            {
                RecordFishPerception("a fish");
            }
        }
    }

    private static void RecordFishPerception(string fishName)
    {
        string template = $"The farmer just caught {fishName}.";
        int lifetime = ModEntry.Config.PerceptionActionLifetime;

        PerceptionManager.Instance.Record("Fish", template, null, lifetime, false);
    }
}