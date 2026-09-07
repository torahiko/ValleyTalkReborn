using System;
using System.Collections.Generic;
using System.Linq;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

namespace ValleytalkReborn;

/// <summary>
/// Manages three separate perception queues:
///
///   Track 1 — _globalGossip (max 2): town-wide "Town Gossip" snapshots.
///     Triggered by major events (date ended, etc.). Shared by all NPCs. Simple FIFO.
///
///   Track 2 — the farmer's personal short-term perception pocket, split into two
///     independently-capped sub-buckets so that high-frequency player-state updates
///     (hat, drunk, tired, ...) can't crowd out low-frequency but high-salience
///     activity events (Gift, Talk, ...) via plain FIFO eviction:
///       - _playerStateBucket (max 6): entries whose Key starts with "Player".
///       - _activityBucket    (max 8): everything else (Gift/Talk/Eat/Fish/Chop/Place/Harvest/...).
///     Both use Deduplicated-FIFO: same Key + NpcName (or Key + Location) replaces old entry.
///     Injected per-NPC through an eyewitness filter at prompt-build time, merged and
///     sorted by salience: basePriority × timeDecay × personalityMultiplier.
/// </summary>
internal class PerceptionManager
{
    public static readonly PerceptionManager Instance = new PerceptionManager();

    private readonly object _lock = new object();

    // Track 1: 全局传闻快照（上限 2 条，简单 FIFO）
    private readonly Queue<PerceptionEntry> _globalGossip = new Queue<PerceptionEntry>();
    private const int MaxGossipEntries = 2;

    // Track 2: 玩家即时状态桶（上限 6 条，随身状态专用）
    private readonly Queue<PerceptionEntry> _playerStateBucket = new Queue<PerceptionEntry>();
    private const int MaxPlayerStateEntries = 6;

    // Track 2: 行为与互动事件桶（上限 8 条，保证关键行为不被过早驱逐）
    private readonly Queue<PerceptionEntry> _activityBucket = new Queue<PerceptionEntry>();
    private const int MaxActivityEntries = 8;

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
                _playerStateBucket.Clear();
                _activityBucket.Clear();
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

        string track;
        lock (_lock)
        {
            if (isGossip || isLandmark)   // Landmark 事件自动提升至 Track 1
            {
                EnqueueGossip(entry);
                track = "Gossip";
            }
            else if (key.StartsWith("Player", StringComparison.OrdinalIgnoreCase))
            {
                EnqueueBucket(_playerStateBucket, MaxPlayerStateEntries, entry);
                track = "PlayerState";
            }
            else
            {
                EnqueueBucket(_activityBucket, MaxActivityEntries, entry);
                track = "Activity";
            }
        }

        if (ModEntry.Config?.Debug == true)
        {
            ModEntry.SMonitor?.Log(
                $"[PerceptionManager] [{track}] '{key}' @ {resolvedLocation}: {template}",
                LogLevel.Debug);
        }
    }
    
    /// <summary>
    /// 显式驱逐指定的感知条目（用于即时身体/伴随状态消除，如宠物远离、脱下帽子等）。
    /// </summary>
    public void Evict(string key, bool fromGossip = false)
    {
        if (string.IsNullOrWhiteSpace(key)) return;

        bool removed;
        lock (_lock)
        {
            if (fromGossip)
            {
                removed = EvictFromQueue(_globalGossip, key);
            }
            else
            {
                bool a = EvictFromQueue(_playerStateBucket, key);
                bool b = EvictFromQueue(_activityBucket, key);
                removed = a || b;
            }
        }

        if (removed && ModEntry.Config?.Debug == true)
        {
            string track = fromGossip ? "gossip" : "farmer bucket";
            ModEntry.SMonitor?.Log(
                $"[PerceptionManager] Evicted key '{key}' from {track}.",
                LogLevel.Debug);
        }
    }

    private static bool EvictFromQueue(Queue<PerceptionEntry> queue, string key)
    {
        if (!queue.Any(e => string.Equals(e.Key, key, StringComparison.OrdinalIgnoreCase)))
            return false;

        var remaining = queue
            .Where(e => !string.Equals(e.Key, key, StringComparison.OrdinalIgnoreCase))
            .ToList();

        queue.Clear();
        foreach (var item in remaining)
            queue.Enqueue(item);

        return true;
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
            foreach (var e in _playerStateBucket.Concat(_activityBucket))
            {
                if (!e.NpcName.Equals(npcName, StringComparison.OrdinalIgnoreCase))
                    continue;

                // 礼物类感知依赖 lifetime 自然淡出，不应在单次对话后立刻失效
                if (e.Key.Equals("Gift", StringComparison.OrdinalIgnoreCase))
                    continue;

                e.IsConsolidated = true;
            }
        }
    }

    /// <summary>
    /// 获取通过目击过滤并按突出度排序的感知记录。
    /// </summary>
    public List<PerceptionEntry> GetFilteredBucketFor(string npcName, int max = 3)
    {
        if (string.IsNullOrEmpty(npcName)) return new List<PerceptionEntry>();

        string npcLocation = GetNpcCurrentLocation(npcName);
        NPC    npc         = GetNpcSafe(npcName);

        lock (_lock)
        {
            return _playerStateBucket
                .Concat(_activityBucket)
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
            return _playerStateBucket
                .Concat(_activityBucket)
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

    /// <summary>
    /// 向指定的个人桶写入一条记录（Deduplicated-FIFO）。
    /// 修复重点：随身身体状态（Player*）只根据 Key 去重，忽略地图差异，杜绝跨地图导致重复堆叠。
    /// </summary>
    private static void EnqueueBucket(Queue<PerceptionEntry> bucket, int maxEntries, PerceptionEntry entry)
    {
        bool isPlayerState = entry.Key.StartsWith("Player", StringComparison.OrdinalIgnoreCase);

        PerceptionEntry duplicate = bucket.FirstOrDefault(e =>
            e.Key == entry.Key &&
            (
                isPlayerState // 随身状态无需匹配地图或 NPC，只要同 Key 直接替换
                ||
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
            var remaining = bucket.Where(e => e != duplicate).ToList();
            bucket.Clear();
            foreach (var item in remaining)
                bucket.Enqueue(item);
        }

        while (bucket.Count >= maxEntries)
            bucket.Dequeue();

        bucket.Enqueue(entry);
    }

    // ─────────────────────────────────────────────
    //  Private: salience scoring
    // ─────────────────────────────────────────────

    private static float GetBasePriority(PerceptionEntry entry)
    {
        if (entry.IsLandmark) return 10f;

        return entry.Key switch
        {
            "Gift"               => 8f,
            "PlayerLowHealth"    => 7f,
            "PlayerExhausted"    => 7f,
            "PlayerFainted"      => 7f,
            "PlayerDrunk"        => 6f,
            "Talk"               => 6f,
            "PlayerHasPendant"   => 5f,
            "PlayerWeddingOutfit"=> 5f,
            "PlayerTired"        => 5f,
            "Eat"                => 4f,
            "Fish"               => 4f,
            "Chop"               => 3f,
            "Place"              => 3f,
            _                    => 2f
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
            "Gift"    => npc.Manners == NPC.polite ? 1.4f : 1.0f,
            "Talk"    => npc.SocialAnxiety == NPC.shy ? 0.5f : 1.2f,
            "Eat"     => 1.0f,
            "Fish"    => npc.Optimism == NPC.positive ? 1.2f : 0.8f,
            "Chop"    => npc.Manners == NPC.rude ? 0.7f : 1.0f,
            "Place"   => npc.SocialAnxiety == NPC.shy ? 0.6f : 1.0f,
            "Harvest" => npc.Optimism == NPC.positive ? 1.3f : 1.0f,
            _         => 1.0f
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

        // 玩家自身状态属于面对面直接观察，全场景对当前对话的 NPC 生效
        if (entry.Key.StartsWith("Player", StringComparison.OrdinalIgnoreCase)) return true;

        // 对话旁听严格定向：仅旁听者本人才能感知
        if (entry.Key == "Talk")
        {
            return !string.IsNullOrEmpty(entry.NpcName) &&
                   entry.NpcName.Equals(npcName, StringComparison.OrdinalIgnoreCase);
        }

        // 针对当前 NPC 的定向事件（如本人收礼）
        if (!string.IsNullOrEmpty(entry.NpcName) &&
            entry.NpcName.Equals(npcName, StringComparison.OrdinalIgnoreCase))
            return true;

        // 同地图目击事件（如旁观给他人送礼、就地钓鱼、砍树、放置物品等）
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

    private static void PurgeExpired(Queue<PerceptionEntry> queue)
    {
        if (!queue.Any(p => !IsPerceptionTimeValid(p))) return;

        var valid = queue.Where(IsPerceptionTimeValid).ToList();
        queue.Clear();
        foreach (var item in valid) queue.Enqueue(item);
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
        return variants[Game1.random.Next(variants.Length)];
    }

    // ─────────────────────────────────────────────
    //  Event callbacks
    // ─────────────────────────────────────────────

    private void OnDayStarted(object sender, DayStartedEventArgs e)
    {
        lock (_lock)
        {
            _playerStateBucket.Clear();
            _activityBucket.Clear();
            _globalGossip.Clear();
        }

        // 跨天或切存档时重置 Gossip 去重记录
        PerceptionInjector.ResetMentionedGossipKeys();

        if (ModEntry.Config?.Debug == true)
            ModEntry.SMonitor?.Log(
                "[PerceptionManager] Farmer buckets and gossip cleared on new day.",
                LogLevel.Debug);
    }

    private void OnUpdateTicked(object sender, UpdateTickedEventArgs e)
    {
        if (!e.IsMultipleOf(60)) return;

        lock (_lock)
        {
            PurgeExpired(_playerStateBucket);
            PurgeExpired(_activityBucket);
            PurgeExpired(_globalGossip);
        }
    }
}