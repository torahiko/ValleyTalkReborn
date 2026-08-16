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
///     Sorted by salience: basePriority × timeDecay × personalityMultiplier.
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
            ModEntry.SHelper.Events.GameLoop.DayStarted   += OnDayStarted;}
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
            RecordedTimeOfDay = Game1.timeOfDay,
            LifetimeHours     = lifetimeHours,
            IsGossip          = isGossip,
            IsLandmark        = isLandmark,
            LocationName      = resolvedLocation,
            ItemId            = itemId
        };

        lock (_lock)
        {
            if (isGossip || isLandmark)   // Landmark 事件提升到 Track 1
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

    public void RecordGossip(string key, string template, int lifetimeHours = 20)
    {
        Record(
            key:key,
            template:      template,
            npcName:       null,
            lifetimeHours: lifetimeHours,
            isGossip:      true,
            isLandmark:    false);
    }

    public List<PerceptionEntry> GetGossipSnapshots()
    {
        lock (_lock)
        {
            return _globalGossip
                .Where(IsPerceptionTimeValid)
                .ToList();
        }
    }

    public void MarkAsConsolidated(string npcName)
    {
        if (string.IsNullOrEmpty(npcName)) return;
        lock (_lock)
        {
            foreach (var e in _farmerBucket){
                if (e.NpcName.Equals(npcName, StringComparison.OrdinalIgnoreCase))
                    e.IsConsolidated = true;
            }
        }
    }

    /// <summary>
    /// Returns farmer bucket entries that pass the eyewitness filter for the given NPC,
    /// sorted by salience = basePriority × timeDecay × personalityMultiplier.
    /// </summary>
    public List<PerceptionEntry> GetFilteredBucketFor(string npcName, int max = 3)
    {
        if (string.IsNullOrEmpty(npcName)) return new List<PerceptionEntry>();

        string npcLocation = GetNpcCurrentLocation(npcName);
        NPC    npc         = GetNpcSafe(npcName);  // 查一次，下面复用

        lock (_lock)
        {
            return _farmerBucket
                .Where(IsPerceptionTimeValid)
                .Where(e => !e.IsConsolidated)
                .Where(e => PassesEyewitnessFilter(e, npcName, npcLocation))
                .OrderByDescending(e => ComputeSalience(e, npc))
                .Take(max)
                .ToList();
        }
    }

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
        // Key dedup: new entry with same Key replaces old one to keep Track 1 diverse
        var existing = _globalGossip.FirstOrDefault(e =>
            string.Equals(e.Key, entry.Key, StringComparison.OrdinalIgnoreCase));

        if (existing != null)
        {
            var remaining = _globalGossip.Where(e => e != existing).ToList();
            _globalGossip.Clear();
            foreach (var item in remaining)
                _globalGossip.Enqueue(item);
        }

        while (_globalGossip.Count >= MaxGossipEntries) _globalGossip.Dequeue();
        _globalGossip.Enqueue(entry);
    }

    private void EnqueueBucket(PerceptionEntry entry)
    {
        PerceptionEntry duplicate = _farmerBucket.FirstOrDefault(e =>
            e.Key == entry.Key &&
            (
                (!string.IsNullOrEmpty(e.NpcName)&& !string.IsNullOrEmpty(entry.NpcName)
                    && string.Equals(e.NpcName, entry.NpcName, StringComparison.OrdinalIgnoreCase))
                ||
                (string.IsNullOrEmpty(e.NpcName)
                    && string.IsNullOrEmpty(entry.NpcName)
                    && !string.IsNullOrEmpty(e.LocationName)
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
    //  Private: salience scoring
    // ─────────────────────────────────────────────

    private static float GetBasePriority(PerceptionEntry entry)
    {
        if (entry.IsLandmark) return 10f;

        return entry.Key switch
        {
            "Gift"  => 8f,
            "Talk"  => 6f,
            "Eat"   => 4f,
            "Fish"  => 4f,
            "Chop"  => 3f,
            "Place" => 3f,
            _       => 3f
        };
    }

    /// <summary>
    /// Salience = basePriority × timeDecay × personalityMultiplier.
    /// npc may be null (e.g. NPC not currently loaded); personality factor defaults to 1.0 in that case.
    /// </summary>
    private static float ComputeSalience(PerceptionEntry entry, NPC npc)
    {
        float base_ = GetBasePriority(entry);

        // Time decay
        float decay = 1f;
        if (entry.LifetimeHours < 20)
        {
            int lifetimeMins = entry.LifetimeHours * 60;
            if (lifetimeMins <= 0) return 0f;

            int elapsedMins = GetInGameMinutes(Game1.timeOfDay)
                            - GetInGameMinutes(entry.RecordedTimeOfDay);

            decay = Math.Clamp(1f - (float)elapsedMins / lifetimeMins, 0f, 1f);
        }

        // Personality multiplier
        float personality = npc != null ? GetPersonalityMultiplier(entry, npc) : 1f;

        return base_ * decay * personality;
    }

    /// <summary>
    /// Returns a multiplier [0.1, 1.5] that adjusts salience based on the NPC's personality
    /// and the event type.
    ///
    /// Sources used (all exposed by SDV's NPC class, no reflection needed):
    ///   npc.Manners       — 0=neutral, 1=polite, 2=rude
    ///   npc.SocialAnxiety — 0=outgoing, 1=shy
    ///   npc.Optimism      — 0=positive, 1=negative
    ///
    /// A small set of named overrides handles NPCs whose personality is better described
    /// by their lore than by these three flags (e.g. Linus, Penny, Harvey).
    ///
    /// Rule of thumb:
    ///   1.5 = this NPC would almost certainly bring this up
    ///   1.0 = neutral / no strong opinion
    ///   0.5 = probably wouldn't care much
    ///   0.1 = very unlikely to notice or mention this
    /// </summary>
    private static float GetPersonalityMultiplier(PerceptionEntry entry, NPC npc)
    {
        // ── Named overrides (lore-based, highest priority) ──────────────────
        float? named = GetNamedOverride(entry.Key, npc.Name);
        if (named.HasValue) return named.Value;

        // ── Trait-based rules ───────────────────────────────────────────────
        return entry.Key switch
        {
            "Gift" =>
                // Polite NPCs are more touched by gifts; rude ones react but differently —
                // the template text handles tone, salience stays high for both.
                npc.Manners == NPC.polite ? 1.4f : 1.0f,

            "Talk" =>
                // Outgoing NPCs are more interested in nearby conversations;
                // shy NPCs tend to look away.
                npc.SocialAnxiety == NPC.shy ? 0.5f : 1.2f,

            "Eat" =>
                // Universally noticeable regardless of personality.
                1.0f,

            "Fish" =>
                // Positive / optimistic NPCs are more impressed by a catch.
                npc.Optimism == NPC.positive ? 1.2f : 0.8f,

            "Chop" =>
                // Rude NPCs don't mind noise; polite/positive ones may find it jarring.
                npc.Manners == NPC.rude ? 0.7f : 1.0f,

            "Place" =>
                // Outgoing NPCs are more observant of their surroundings.
                npc.SocialAnxiety == NPC.shy ? 0.6f : 1.0f,

            "Harvest" =>
                // Landmark — everyone hears town-wide news, but optimistic NPCs
                // are more likely to bring it up in conversation.
                npc.Optimism == NPC.positive ? 1.3f : 1.0f,

            _ => 1.0f
        };
    }

    /// <summary>
    /// Named overrides for NPCs whose personality is better described by lore
    /// than by the Manners/SocialAnxiety/Optimism flags.
    /// Returns null if no override applies for this (key, npcName) pair.
    /// </summary>
    private static float? GetNamedOverride(string key, string npcName)
    {
        return (key, npcName) switch
        {
            // Linus lives in nature — deeply moved by farming/foraging news, dislikes chopping
            ("Harvest", "Linus") => 1.5f,
            ("Chop",    "Linus") => 0.1f,  // would genuinely be bothered
            ("Fish",    "Linus") => 1.4f,

            // Penny is gentle and dislikes destruction; loves seeing the farmer care for things
            ("Chop",    "Penny") => 0.1f,
            ("Harvest", "Penny") => 1.4f,
            ("Gift",    "Penny") => 1.5f,

            // Harvey is observant and health-conscious — notices what people eat
            ("Eat",     "Harvey") => 1.5f,
            ("Fish",    "Harvey") => 1.2f,  // appreciates outdoor activity

            // Leah is an artist who appreciates nature; hates seeing trees felled
            ("Chop",    "Leah") => 0.1f,
            ("Harvest", "Leah") => 1.3f,
            ("Place",   "Leah") => 1.3f,   // notices things placed in the world

            // Willy is a fisherman — any fish catch is big news to him
            ("Fish",    "Willy") => 1.5f,

            // Sebastian is introverted — not interested in social observations
            ("Talk",    "Sebastian") => 0.3f,
            ("Place",   "Sebastian") => 0.4f,

            // Emily is expressive and warm — notices gifts and acts of care
            ("Gift",    "Emily") => 1.4f,
            ("Eat",     "Emily") => 1.2f,

            // Haley is fashion-conscious — not interested in outdoor farm work
            ("Chop",    "Haley")    => 0.2f,
            ("Harvest", "Haley")    => 0.4f,
            ("Fish",    "Haley")    => 0.3f,

            // Maru is curious and scientific — interested in unusual catches
            ("Fish",    "Maru")    => 1.3f,
            ("Harvest", "Maru")    => 1.1f,

            _ => null
        };
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

    private static NPC GetNpcSafe(string npcName)
    {
        try { return Game1.getCharacterFromName(npcName); }
        catch { return null; }
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
    //  Utility
    // ─────────────────────────────────────────────

    internal static string PickVariant(string[] variants)
    {
        if (variants == null || variants.Length == 0) return string.Empty;
        if (variants.Length == 1) return variants[0];
        int index = (int)(Game1.ticks % (uint)variants.Length);
        return variants[index];
    }

    // ─────────────────────────────────────────────
    //  Event callbacks
    // ─────────────────────────────────────────────

    private void OnDayStarted(object sender, DayStartedEventArgs e)
    {
        lock (_lock)
        {
            _farmerBucket.Clear();
            _globalGossip.Clear();
        }
        if (ModEntry.Config?.Debug == true)
            ModEntry.SMonitor?.Log(
                "[PerceptionManager] Farmer bucket and gossip cleared on new day.",
                LogLevel.Debug);
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