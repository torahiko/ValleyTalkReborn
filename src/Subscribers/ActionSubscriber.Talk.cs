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
    private static bool _initialized = false;
    private static int _nullSpeakerTicks = 0;
    private const int NullToleranceTicks = 5;

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
        _lastTalkingNpc = null;
        _nullSpeakerTicks = 0;
        _initialized = false;
    }

    private static void OnUpdateTicked(object sender, UpdateTickedEventArgs e)
    {
        if (!e.IsMultipleOf(30)) return;
        var player = Game1.player;
        if (player == null || Game1.currentLocation == null) return;

        var currentSpeaker = Game1.currentSpeaker;

        if (currentSpeaker is NPC talkingNpc)
        {
            _nullSpeakerTicks = 0;
            if (talkingNpc != _lastTalkingNpc)
            {
                _lastTalkingNpc = talkingNpc;
                RecordNearbyOverhearing(talkingNpc);
            }
        }
        else
        {
            _nullSpeakerTicks++;
            if (_nullSpeakerTicks > NullToleranceTicks)
            {
                _lastTalkingNpc = null;
                _nullSpeakerTicks = 0;
            }
        }
    }

    private static void RecordNearbyOverhearing(NPC talkingNpc)
    {
        var currentLocation = Game1.currentLocation;
        var player = Game1.player;
        string talkingNpcName = talkingNpc.displayName ?? talkingNpc.Name;

        foreach (var npc in currentLocation.characters)
        {
            if (npc == talkingNpc) continue;

            float dx = npc.Position.X - player.Position.X;
            float dy = npc.Position.Y - player.Position.Y;
            float distance = (float)System.Math.Sqrt(dx * dx + dy * dy);

            // 8 tiles = 512 pixels
            if (distance <= 512f)
            {
                string template = PerceptionManager.PickVariant(new[]
                {
                    $"You overheard {talkingNpcName} having a conversation with the farmer nearby.",
                    $"You caught bits of a conversation between {talkingNpcName} and the farmer a moment ago.",
                    $"{talkingNpcName} was talking with the farmer close by — you couldn't help but overhear.",
                });

                PerceptionManager.Instance.Record("Talk", template, npc.Name,
                    ModEntry.Config.PerceptionTalkLifetime, isLandmark: false);
            }
        }
    }
}