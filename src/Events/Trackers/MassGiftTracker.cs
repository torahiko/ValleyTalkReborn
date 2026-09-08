using System.Collections.Generic;
using System.Linq;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

namespace ValleytalkReborn;

/// <summary>
/// Module 8: Mass Gift Tracker
/// Detects two gossip-worthy gift patterns and writes to Track 1 (globalGossip) at DayEnding:
///
///   Pattern A — Same item given to 3+ different NPCs in one day.
///   Pattern B — Gifts given to 5+ different NPCs in one day.
///
/// Data is collected via RecordGift(), called directly from GiftSubscriber
/// on every confirmed gift event. No additional patches required.
/// </summary>
internal static class MassGiftTracker
{
    private static bool _initialized = false;

    // itemId → set of NPC names who received it today
    private static readonly Dictionary<string, HashSet<string>> _itemToRecipients
        = new(System.StringComparer.OrdinalIgnoreCase);

    // All NPC names who received any gift today
    private static readonly HashSet<string> _giftedNpcs
        = new(System.StringComparer.OrdinalIgnoreCase);

    public static void Initialize()
    {
        if (_initialized || ModEntry.SHelper == null) return;
        ModEntry.SHelper.Events.GameLoop.DayStarted  += OnDayStarted;
        ModEntry.SHelper.Events.GameLoop.DayEnding   += OnDayEnding;
        _initialized = true;
        ModEntry.SMonitor?.Log("[MassGiftTracker] Initialized.", LogLevel.Debug);
    }

    public static void Cleanup()
    {
        if (!_initialized || ModEntry.SHelper == null) return;
        ModEntry.SHelper.Events.GameLoop.DayStarted -= OnDayStarted;
        ModEntry.SHelper.Events.GameLoop.DayEnding  -= OnDayEnding;
        Reset();
        _initialized = false;
    }

    /// <summary>
    /// Called by GiftSubscriber.RecordGiftPerceptionFromPatch() on every confirmed gift.
    /// itemId may be null if the fallback poll path fired (item name unknown).
    /// </summary>
    public static void RecordGift(string npcName, string itemId)
    {
        if (string.IsNullOrEmpty(npcName)) return;

        _giftedNpcs.Add(npcName);

        if (!string.IsNullOrEmpty(itemId))
        {
            if (!_itemToRecipients.TryGetValue(itemId, out var recipients))
            {
                recipients = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
                _itemToRecipients[itemId] = recipients;
            }
            recipients.Add(npcName);
        }
    }

    // ─────────────────────────────────────────────

    private static void OnDayStarted(object sender, DayStartedEventArgs e) => Reset();

    private static void OnDayEnding(object sender, DayEndingEventArgs e)
    {
        if (!ModEntry.Config.EnablePerceptionSystem) return;

        EvaluateAndRecord();
    }

    private static void EvaluateAndRecord()
    {
        // Pattern A: same item → 3+ different NPCs
        foreach (var pair in _itemToRecipients)
        {
            if (pair.Value.Count < 3) continue;

            string itemId   = pair.Key;
            string itemName = GetItemDisplayName(itemId);

            string template = PerceptionManager.PickVariant(new[]
            {
                $"Heard the farmer was handing out [{itemName}] to everyone today — what are they up to?",
                $"Apparently the farmer gave [{itemName}] to half the town. Running a promotion or something?",
                $"People are saying the farmer distributed [{itemName}] all over the valley today.",
            });

            PerceptionManager.Instance.RecordGossip("MassGift_SameItem", template, lifetimeHours: 20);

            ModEntry.SMonitor?.Log(
                $"[MassGiftTracker] Pattern A fired: [{itemName}] → {pair.Value.Count} NPCs",
                LogLevel.Debug);

            // Only the highest-scoring same-item pattern, no duplicates
            break;
        }

        // Pattern B: 5+ different NPCs received any gift
        if (_giftedNpcs.Count >= 5)
        {
            string template = PerceptionManager.PickVariant(new[]
            {
                $"Word is the farmer gave gifts to {_giftedNpcs.Count} people today. They must really want to make friends.",
                $"The farmer was seen handing out gifts all over town — {_giftedNpcs.Count} people so far.",
                $"Apparently the farmer gifted nearly half the valley today. Popular as ever.",
            });

            PerceptionManager.Instance.RecordGossip("MassGift_Spread", template, lifetimeHours: 20);

            ModEntry.SMonitor?.Log(
                $"[MassGiftTracker] Pattern B fired: {_giftedNpcs.Count} different NPCs gifted",
                LogLevel.Debug);
        }
    }

    private static string GetItemDisplayName(string itemId)
    {
        try
        {
            var item = ItemRegistry.Create(itemId);
            return item?.DisplayName ?? item?.Name ?? itemId;
        }
        catch { return itemId; }
    }

    private static void Reset()
    {
        _itemToRecipients.Clear();
        _giftedNpcs.Clear();
    }
}
