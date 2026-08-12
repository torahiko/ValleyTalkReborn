using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

namespace ValleytalkReborn;

/// <summary>
/// Subscribes to dialogue events to enable nearby NPCs to "overhear" conversations.
/// </summary>
internal static class TalkSubscriber
{
    private static NPC _lastTalkingNpc = null;

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
        if (!e.IsMultipleOf(30)) return;

        var player = Game1.player;
        if (player == null || Game1.currentLocation == null) return;

        // Check if player is in dialogue with someone
        var currentSpeaker = Game1.currentSpeaker;
        if (currentSpeaker is NPC talkingNpc && talkingNpc != _lastTalkingNpc)
        {
            RecordNearbyOverhearing(talkingNpc);
        }

        _lastTalkingNpc = currentSpeaker as NPC;
    }

    private static void RecordNearbyOverhearing(NPC talkingNpc)
    {
        var currentLocation = Game1.currentLocation;
        var player = Game1.player;

        // Find NPCs within 8 tiles
        foreach (var npc in currentLocation.characters)
        {
            if (npc == talkingNpc) continue;

            float dx = npc.Position.X - player.Position.X;
            float dy = npc.Position.Y - player.Position.Y;
            float distance = (float)System.Math.Sqrt(dx * dx + dy * dy);

            // 8 tiles = 512 pixels (64px per tile)
            if (distance <= 512f)
            {
                string npcName = npc.Name;
                string talkingNpcName = talkingNpc.displayName ?? talkingNpc.Name;

                string template = $"Overheard {talkingNpcName} chatting with the farmer.";

                PerceptionManager.Instance.Record("Talk", template, npcName, ModEntry.Config.PerceptionTalkLifetime, isLandmark: false);
            }
        }
    }
}