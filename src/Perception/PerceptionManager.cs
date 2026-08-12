using System;
using System.Collections.Generic;
using System.Linq;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

namespace ValleytalkReborn;

/// <summary>
/// Manages two separate perception tracks:
///
///   Track 1 — _globalGossip (max 2): town-wide "Town Gossip" snapshots.
///     Triggered by major events (date ended, etc.). Shared by all NPCs. Simple FIFO.
///
///   Track 2 — _farmerBucket (max 3): the farmer's personal short-term perception pocket.
///     Uses Deduplicated-FIFO: same Key + NpcName (or Key + Location) replaces old entry.
///     Injected per-NPC through an eyewitness filter at prompt-build time.
/// </summary>
internal class PerceptionManager
{
    public static readonly PerceptionManager Instance = new PerceptionManager();

    private readonly object _lock = new object();

    // Track 1: global gossip snapshots (max 2, simple FIFO)
    private readonly Queue<PerceptionEntry> _globalGossip = new Queue<PerceptionEntry>();
    private const int MaxGossipEntries = 2;

    // Track 2: farmer's personal bucket (max 3, Deduplicated-FIFO)
    private readonly Queue<PerceptionEntry> _farmerBucket = new Queue<PerceptionEntry>();
    private const int MaxBucketEntries = 3;

    private PerceptionManager()
    {
        if (ModEntry.SHelper != null)
        {
            ModEntry.SHelper.Events.GameLoop.UpdateTicked += OnUpdateTicked;
            ModEntry.SHelper.Events.GameLoop.DayStarted   += OnDayStarted;
        }
    }

    // ─────────────────────────────────────────────
    //  Lifecycle
    // ─────────────────────────────────────────────

    public void Cleanup()
    {
        try
        {
            if (ModEntry.SHelper != null)
            {
                ModEntry.SHelper.Events.GameLoop.UpdateTicked -= OnUpdateTicked;
                ModEntry.SHelper.Events.GameLoop.DayStarted   -= OnDayStarted;
            }
            lock (_lock)
            {
                _globalGossip.Clear();
                _farmerBucket.Clear();
            }
        }
        catch (Exception ex)
        {
            ModEntry.SMonitor?.Log($"[PerceptionManager] Cleanup error: {ex.Message}", LogLevel.Debug);
        }
    }

    // ─────────────────────────────────────────────
    //  Public API
    // ─────────────────────────────────────────────

    public void Record(
        string key,
        string template,
        string npcName       = null,
        int    lifetimeHours = 2,
        bool   isGossip      = false,
        bool   isLandmark    = false,
        string itemId        = null,
        string locationName  = null)
    {
        if (!ShouldRecord(key, isGossip)) return;
        if (string.IsNullOrWhiteSpace(template)) return;

        string resolvedLocation = locationName
            ?? Game1.currentLocation?.Name
            ?? string.Empty;

        var entry = new PerceptionEntry
        {
            Key               = key,
            Template          = template,
            NpcName           = npcName ?? string.Empty,
            Timestamp         = DateTime.Now,
            RecordedTimeOfDay = Game1.timeOfDay,
            LifetimeHours     = lifetimeHours,
            IsGossip          = isGossip,
            IsLandmark        = isLandmark,
            LocationName      = resolvedLocation,
            ItemId            = itemId
        };

        lock (_lock)
        {
            if (isGossip)
                EnqueueGossip(entry);
            else
                EnqueueBucket(entry);
        }

        if (ModEntry.Config?.Debug == true)
        {
            string track = isGossip ? "Gossip" : "Bucket";
            ModEntry.SMonitor?.Log(
                $"[PerceptionManager] [{track}] '{key}' @ {resolvedLocation}: {template}",
                LogLevel.Debug);
        }
    }

    /// <summary>
    /// Open interface for recording a town-wide gossip snapshot from any major event.
    /// </summary>
    public void RecordGossip(string key, string template, int lifetimeHours = 20)
    {
        Record(
            key:          key,
            template:     template,
            npcName:      null,
            lifetimeHours: lifetimeHours,
            isGossip:     true,
            isLandmark:   false);
    }

    /// <summary>
    /// Returns all currently valid gossip snapshots (Track 1) for prompt injection.
    /// </summary>
    public List<PerceptionEntry> GetGossipSnapshots()
    {
        lock (_lock)
        {
            return _globalGossip
                .Where(IsPerceptionTimeValid)
                .ToList();
        }
    }

    /// <summary>
    /// Marks all entries for the given NPC as consolidated so they won't be
    /// re-injected during prompt build the next day.
    /// Called by NightlyConsolidationHook after packing events into a nightly work item.
    /// </summary>
    public void MarkAsConsolidated(string npcName)
    {
        if (string.IsNullOrEmpty(npcName)) return;
        lock (_lock)
        {
            foreach (var e in _farmerBucket)
            {
                if (e.NpcName.Equals(npcName, StringComparison.OrdinalIgnoreCase))
                    e.IsConsolidated = true;
            }
        }
    }

    /// <summary>
    /// Returns farmer bucket entries that pass the eyewitness filter for the given NPC.
    ///
    ///   Condition B — entry.NpcName == npcName  (directly targeted, e.g. received a gift)
    ///   Condition A — entry.LocationName == npc's current location  (eyewitness)
    ///   Condition C — entry.IsLandmark == true  (town-wide broadcast)
    ///   Condition D — none of the above → excluded
    /// </summary>
    public List<PerceptionEntry> GetFilteredBucketFor(string npcName, int max = 3)
    {
        if (string.IsNullOrEmpty(npcName)) return new List<PerceptionEntry>();

        string npcLocation = GetNpcCurrentLocation(npcName);

        lock (_lock)
        {
            return _farmerBucket
                .Where(IsPerceptionTimeValid)
                .Where(e => !e.IsConsolidated) // Skip entries already processed by nightly consolidation
                .Where(e => PassesEyewitnessFilter(e, npcName, npcLocation))
                .OrderByDescending(e => e.Timestamp)
                .Take(max)
                .ToList();
        }
    }

    /// <summary>
    /// Legacy accessor — returns filtered bucket entries.
    /// Kept for any external callers still using GetPerceptionsFor().
    /// </summary>
    public List<PerceptionEntry> GetPerceptionsFor(string npcName, int maxCount = 3)
        => GetFilteredBucketFor(npcName, maxCount);

    public IReadOnlyList<string> GetInteractedNpcNamesToday()
    {
        lock (_lock)
        {
            return _farmerBucket
                .Where(e => !string.IsNullOrEmpty(e.NpcName))
                .Select(e => e.NpcName)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    // ─────────────────────────────────────────────
    //  Private: queue management
    // ─────────────────────────────────────────────

    private void EnqueueGossip(PerceptionEntry entry)
    {
        while (_globalGossip.Count >= MaxGossipEntries) _globalGossip.Dequeue();
        _globalGossip.Enqueue(entry);
    }

    /// <summary>
    /// Deduplicated-FIFO for farmer bucket:
    ///   1. Remove any existing entry with same Key AND (same NpcName OR same LocationName).
    ///   2. Append new entry to tail.
    ///   3. If total still exceeds MaxBucketEntries, evict oldest from head.
    /// </summary>
    private void EnqueueBucket(PerceptionEntry entry)
    {
        PerceptionEntry duplicate = _farmerBucket.FirstOrDefault(e =>
            e.Key == entry.Key &&
            (
                (!string.IsNullOrEmpty(entry.NpcName)
                    && string.Equals(e.NpcName, entry.NpcName, StringComparison.OrdinalIgnoreCase))
                ||
                (string.IsNullOrEmpty(entry.NpcName)
                    && !string.IsNullOrEmpty(entry.LocationName)
                    && string.Equals(e.LocationName, entry.LocationName, StringComparison.OrdinalIgnoreCase))
            ));

        if (duplicate != null)
        {
            var remaining = _farmerBucket.Where(e => e != duplicate).ToList();
            _farmerBucket.Clear();
            foreach (var item in remaining)
                _farmerBucket.Enqueue(item);
        }

        while (_farmerBucket.Count >= MaxBucketEntries)
            _farmerBucket.Dequeue();

        _farmerBucket.Enqueue(entry);
    }

    // ─────────────────────────────────────────────
    //  Private: filter & validity
    // ─────────────────────────────────────────────

    private static bool PassesEyewitnessFilter(
        PerceptionEntry entry, string npcName, string npcLocation)
    {
        if (entry.IsLandmark) return true;

        if (!string.IsNullOrEmpty(entry.NpcName) &&
            entry.NpcName.Equals(npcName, StringComparison.OrdinalIgnoreCase))
            return true;

        if (!string.IsNullOrEmpty(entry.LocationName) &&
            !string.IsNullOrEmpty(npcLocation) &&
            entry.LocationName.Equals(npcLocation, StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    private static string GetNpcCurrentLocation(string npcName)
    {
        try { return Game1.getCharacterFromName(npcName)?.currentLocation?.Name ?? string.Empty; }
        catch { return string.Empty; }
    }

    private static int GetInGameMinutes(int timeOfDay)
        => (timeOfDay / 100) * 60 + (timeOfDay % 100);

    private static bool IsPerceptionTimeValid(PerceptionEntry entry)
    {
        if (entry == null) return false;
        if (entry.LifetimeHours >= 20) return true;

        int currentMins  = GetInGameMinutes(Game1.timeOfDay);
        int recordedMins = GetInGameMinutes(entry.RecordedTimeOfDay);

        if (currentMins < recordedMins) return false;
        return (currentMins - recordedMins) <= (entry.LifetimeHours * 60);
    }

    private static bool ShouldRecord(string key, bool isGossip)
    {
        if (!ModEntry.Config.EnablePerceptionSystem) return false;
        if (isGossip) return true;

        return key switch
        {
            "Eat"     => ModEntry.Config.EnablePerceptionEat,
            "Fish"    => ModEntry.Config.EnablePerceptionFish,
            "Chop"    => ModEntry.Config.EnablePerceptionChop,
            "Place"   => ModEntry.Config.EnablePerceptionPlace,
            "Talk"    => ModEntry.Config.EnableNearbyPerception,
            "Harvest" => ModEntry.Config.EnablePerceptionHarvest,
            "Gift"    => ModEntry.Config.EnablePerceptionGift,
            _         => true
        };
    }

    // ─────────────────────────────────────────────
    //  Event callbacks
    // ─────────────────────────────────────────────

    private void OnDayStarted(object sender, DayStartedEventArgs e)
    {
        lock (_lock)
        {
            _farmerBucket.Clear();
        }

        if (ModEntry.Config?.Debug == true)
            ModEntry.SMonitor?.Log(
                "[PerceptionManager] Farmer bucket cleared on new day.", LogLevel.Debug);
    }

    private void OnUpdateTicked(object sender, UpdateTickedEventArgs e)
    {
        if (!e.IsMultipleOf(60)) return;

        lock (_lock)
        {
            bool bucketDirty = _farmerBucket.Any(p => !IsPerceptionTimeValid(p));
            bool gossipDirty = _globalGossip.Any(p => !IsPerceptionTimeValid(p));

            if (!bucketDirty && !gossipDirty) return;

            if (bucketDirty)
            {
                var valid = _farmerBucket.Where(IsPerceptionTimeValid).ToList();
                _farmerBucket.Clear();
                foreach (var item in valid) _farmerBucket.Enqueue(item);
            }

            if (gossipDirty)
            {
                var valid = _globalGossip.Where(IsPerceptionTimeValid).ToList();
                _globalGossip.Clear();
                foreach (var item in valid) _globalGossip.Enqueue(item);
            }
        }
    }
}
