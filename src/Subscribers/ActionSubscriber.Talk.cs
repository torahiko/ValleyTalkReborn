using System;
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

        bool isZh = LocalizedContentManager.CurrentLanguageCode.ToString()
            .StartsWith("zh", StringComparison.OrdinalIgnoreCase);

        foreach (var npc in currentLocation.characters)
        {
            // 排除当前正在交谈的 NPC 本人
            if (npc == talkingNpc || npc.Name.Equals(talkingNpc.Name, StringComparison.OrdinalIgnoreCase))
                continue;

            float dx = npc.Position.X - player.Position.X;
            float dy = npc.Position.Y - player.Position.Y;
            float distance = (float)System.Math.Sqrt(dx * dx + dy * dy);

            // 8 tiles = 512 pixels
            if (distance <= 512f)
            {
                string template = isZh
                    ? PerceptionManager.PickVariant(new[]
                    {
                        $"你刚才在附近隐约听到 {talkingNpcName} 正在和农夫交谈。",
                        $"你注意到 {talkingNpcName} 刚才就在旁边跟农夫聊着什么。",
                        $"{talkingNpcName} 刚才正在和农夫说话——你在旁边不经意听到了几句。"
                    })
                    : PerceptionManager.PickVariant(new[]
                    {
                        $"You overheard {talkingNpcName} having a conversation with the farmer nearby.",
                        $"You caught bits of a conversation between {talkingNpcName} and the farmer a moment ago.",
                        $"{talkingNpcName} was talking with the farmer close by — you couldn't help but overhear."
                    });

                PerceptionManager.Instance.Record("Talk", template, npc.Name,
                    ModEntry.Config.PerceptionTalkLifetime, isLandmark: false);
            }
        }
    }
}