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

    // Track 2: farmer's personal bucket (max 6, Deduplicated-FIFO)
    private readonly Queue<PerceptionEntry> _farmerBucket = new Queue<PerceptionEntry>();
    private const int MaxBucketEntries = 6;

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
    
    /// <summary>
    /// 显式驱逐指定的感知条目（用于即时身体/伴随状态消除，如宠物远离、脱下帽子等）。
    /// </summary>
    /// <param name="key">要清除的 PerceptionEntry Key（如 "PlayerPet"、"PlayerHat" 等）</param>
    public void Evict(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return;

        lock (_lock)
        {
            // 若队列中不存在该 Key，直接返回避免重新装载队列
            if (!_farmerBucket.Any(e => string.Equals(e.Key, key, StringComparison.OrdinalIgnoreCase)))
                return;

            // 过滤掉所有匹配指定 Key 的历史条目
            var remaining = _farmerBucket
                .Where(e => !string.Equals(e.Key, key, StringComparison.OrdinalIgnoreCase))
                .ToList();

            _farmerBucket.Clear();
            foreach (var item in remaining)
            {
                _farmerBucket.Enqueue(item);
            }
        }

        if (ModEntry.Config?.Debug == true)
        {
            ModEntry.SMonitor?.Log(
                $"[PerceptionManager] Evicted key '{key}' from farmer bucket.",
                LogLevel.Debug);
        }
    }
    

    public void RecordGossip(string key, string template, int lifetimeHours = 20)
    {
        Record(
            key:           key,
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
            foreach (var e in _farmerBucket)
            {
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
        NPC    npc         = GetNpcSafe(npcName);

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
                (!string.IsNullOrEmpty(e.NpcName) && !string.IsNullOrEmpty(entry.NpcName)
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
            "PlayerLowHealth"    => 7f,
            "PlayerExhausted"    => 7f,
            "PlayerFainted"      => 7f,
            "PlayerDrunk"        => 6f,
            "Talk"  => 6f,
            "PlayerHasPendant"   => 5f,
            "PlayerWeddingOutfit"=> 5f,
            "PlayerTired"        => 5f,
            "Eat"   => 4f,
            "Fish"  => 4f,
            "Chop"  => 3f,
            "Place" => 3f,
            _       => 2f
        };
    }

    private static float ComputeSalience(PerceptionEntry entry, NPC npc)
    {
        float base_ = GetBasePriority(entry);

        float decay = 1f;
        if (entry.LifetimeHours < 20)
        {
            int lifetimeMins = entry.LifetimeHours * 60;
            if (lifetimeMins <= 0) return 0f;

            int elapsedMins = GetInGameMinutes(Game1.timeOfDay)
                            - GetInGameMinutes(entry.RecordedTimeOfDay);

            decay = Math.Clamp(1f - (float)elapsedMins / lifetimeMins, 0f, 1f);
        }

        float personality = npc != null ? GetPersonalityMultiplier(entry, npc) : 1f;

        return base_ * decay * personality;
    }

    private static float GetPersonalityMultiplier(PerceptionEntry entry, NPC npc)
    {
        float? named = GetNamedOverride(entry.Key, npc.Name);
        if (named.HasValue) return named.Value;

        return entry.Key switch
        {
            "Gift" => npc.Manners == NPC.polite ? 1.4f : 1.0f,
            "Talk" => npc.SocialAnxiety == NPC.shy ? 0.5f : 1.2f,
            "Eat" => 1.0f,
            "Fish" => npc.Optimism == NPC.positive ? 1.2f : 0.8f,
            "Chop" => npc.Manners == NPC.rude ? 0.7f : 1.0f,
            "Place" => npc.SocialAnxiety == NPC.shy ? 0.6f : 1.0f,
            "Harvest" => npc.Optimism == NPC.positive ? 1.3f : 1.0f,
            _ => 1.0f
        };
    }

    private static float? GetNamedOverride(string key, string npcName)
    {
        return (key, npcName) switch
        {
            ("Harvest", "Linus") => 1.5f,
            ("Chop",    "Linus") => 0.1f,
            ("Fish",    "Linus") => 1.4f,

            ("Chop",    "Penny") => 0.1f,
            ("Harvest", "Penny") => 1.4f,
            ("Gift",    "Penny") => 1.5f,

            ("Eat",     "Harvey") => 1.5f,
            ("Fish",    "Harvey") => 1.2f,

            ("Chop",    "Leah") => 0.1f,
            ("Harvest", "Leah") => 1.3f,
            ("Place",   "Leah") => 1.3f,

            ("Fish",    "Willy") => 1.5f,

            ("Talk",    "Sebastian") => 0.3f,
            ("Place",   "Sebastian") => 0.4f,

            ("Gift",    "Emily") => 1.4f,
            ("Eat",     "Emily") => 1.2f,

            ("Chop",    "Haley") => 0.2f,
            ("Harvest", "Haley") => 0.4f,
            ("Fish",    "Haley") => 0.3f,

            ("Fish",    "Maru") => 1.3f,
            ("Harvest", "Maru") => 1.1f,

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

        // 玩家自身身体状态（PlayerStateScanner 产出的 Player* Key）属于面对面即时感知，
        // 不受地理定位判定限制——帽子、醉酒、疲惫等无需"在同一地图"即可被眼前 NPC 察觉。
        if (entry.Key.StartsWith("Player", StringComparison.OrdinalIgnoreCase)) return true;

        // 【核心修复】对话旁听（Talk）严格定向：只有指定的旁听者本人才能感知
        // 杜绝因同处于一个房间而将旁听记忆塞给说话者自己（如哈坎自己旁听自己）
        if (entry.Key == "Talk")
        {
            return !string.IsNullOrEmpty(entry.NpcName) &&
                   entry.NpcName.Equals(npcName, StringComparison.OrdinalIgnoreCase);
        }

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