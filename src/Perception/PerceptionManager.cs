using System;
using System.Collections.Generic;
using System.Linq;
using System.Collections.Concurrent;
using StardewModdingAPI.Events;
using StardewModdingAPI;
using StardewValley;

namespace ValleyTalk;

/// <summary>
/// Singleton manager for storing, querying, and cleaning up perception records.
/// Each behavior type keeps only the latest record — new overwrites old.
/// </summary>
internal class PerceptionManager
{
    /// <summary>Singleton instance.</summary>
    public static readonly PerceptionManager Instance = new PerceptionManager();

    /// <summary>Key = composite "[{action}]_{npcName|GLOBAL}", Value = latest perception record.</summary>
    private readonly ConcurrentDictionary<string, PerceptionEntry> _perceptions = new();

    /// <summary>Timer for periodic cleanup (unused, kept for future use).</summary>
    private TimeSpan _lastCleanup = TimeSpan.Zero;

    private PerceptionManager()
    {
        // Subscribe to game loop for periodic cleanup
        if (ModEntry.SHelper != null)
        {
            ModEntry.SHelper.Events.GameLoop.UpdateTicked += OnUpdateTicked;
            ModEntry.SHelper.Events.GameLoop.DayStarted += OnDayStarted;
        }
    }

    /// <summary>
    /// Cleans up event subscriptions. Called when the game is exiting.
    /// </summary>
    public void Cleanup()
    {
        try
        {
            if (ModEntry.SHelper != null)
            {
                ModEntry.SHelper.Events.GameLoop.UpdateTicked -= OnUpdateTicked;
                ModEntry.SHelper.Events.GameLoop.DayStarted -= OnDayStarted;
            }
            _perceptions.Clear();
        }
        catch (Exception ex)
        {
            ModEntry.SMonitor?.Log($"[PerceptionManager] Cleanup error: {ex.Message}", LogLevel.Debug);
        }
    }

    /// <summary>
    /// Records a behavior perception. New records with the same key overwrite old ones.
    /// Respects all config switches via ShouldRecord().
    /// Uses composite key: $"[{key}]_{npcName ?? "GLOBAL"}" to prevent
    /// global events from overwriting NPC-specific events of the same action type.
    /// </summary>
    public void Record(string key, string template, string npcName = null, int lifetimeMinutes = 5, bool isGlobal = false, string itemId = null)
    {
        if (!ShouldRecord(key, isGlobal)) return;
        if (string.IsNullOrWhiteSpace(template)) return;

        string compositeKey = $"[{key}]_{npcName ?? "GLOBAL"}";
        _perceptions[compositeKey] = new PerceptionEntry
        {
            Key = key,
            Template = template,
            NpcName = npcName ?? "",
            Timestamp = DateTime.Now,
            LifetimeMinutes = lifetimeMinutes,
            IsGlobal = isGlobal,
            ItemId = itemId
        };

        if (ModEntry.Config.Debug)
        {
            ModEntry.SMonitor?.Log($"[Perception] Recorded '{compositeKey}': {template}", LogLevel.Debug);
        }
    }

    /// <summary>
    /// Gets perceptions that a specific NPC knows about.
    /// Returns up to maxCount entries, ordered by time (newest first).
    /// Includes both NPC-specific perceptions and global (town-wide) perceptions.
    /// </summary>
    public List<PerceptionEntry> GetPerceptionsFor(string npcName, int maxCount = 3)
    {
        var now = DateTime.Now;
        var results = _perceptions.Values
            .Where(p => IsPerceptionValidForNpc(p, npcName, now))
            .OrderByDescending(p => p.Timestamp)
            .Take(maxCount)
            .ToList();
        return results;
    }

    /// <summary>
    /// Checks whether a perception is still valid (not expired) and visible to the given NPC.
    /// </summary>
    private bool IsPerceptionValidForNpc(PerceptionEntry entry, string npcName, DateTime now)
    {
        // Check expiration
        if ((now - entry.Timestamp).TotalMinutes > entry.LifetimeMinutes)
            return false;

        // Global perceptions are visible to everyone
        if (entry.IsGlobal)
            return true;

        // NPC-specific perceptions are only visible to that NPC
        if (!string.IsNullOrEmpty(entry.NpcName) && entry.NpcName.Equals(npcName, StringComparison.OrdinalIgnoreCase))
            return true;

        // Same-map perceptions (no specific NPC) are visible to all NPCs on the same map
        if (string.IsNullOrEmpty(entry.NpcName))
        {
            // For same-map perception, check if NPC is on same map as player
            return IsNpcOnSameMap(npcName);
        }

        return false;
    }

    /// <summary>
    /// Checks if an NPC is on the same map as the player.
    /// </summary>
    private bool IsNpcOnSameMap(string npcName)
    {
        try
        {
            var currentLocation = Game1.currentLocation;
            if (currentLocation == null) return false;

            return currentLocation.characters.Any(npc =>
                npc.Name.Equals(npcName, StringComparison.OrdinalIgnoreCase) ||
                (npc.displayName != null && npc.displayName.Equals(npcName, StringComparison.OrdinalIgnoreCase)));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Checks all config switches to determine if a behavior should be recorded.
    /// </summary>
    private bool ShouldRecord(string key, bool isGlobal)
    {
        // Master switch
        if (!ModEntry.Config.EnablePerceptionSystem) return false;

        // Layer switches
        if (isGlobal && !ModEntry.Config.EnableGlobalPerception) return false;
        if (!isGlobal && key == "Talk" && !ModEntry.Config.EnableNearbyPerception) return false;
        if (!isGlobal && key != "Talk" && !ModEntry.Config.EnableSameMapPerception) return false;

        // Behavior-level switches
        return key switch
        {
            "Eat" => ModEntry.Config.EnablePerceptionEat,
            "Fish" => ModEntry.Config.EnablePerceptionFish,
            "Chop" => ModEntry.Config.EnablePerceptionChop,
            "Place" => ModEntry.Config.EnablePerceptionPlace,
            "Talk" => ModEntry.Config.EnablePerceptionTalk,
            "Harvest" => ModEntry.Config.EnablePerceptionHarvest,
            _ => true
        };
    }

    /// <summary>
    /// Periodic cleanup of expired entries. Called every game tick but only processes once per minute.
    /// Optimized to avoid allocating a new list unless there are actually expired entries.
    /// </summary>
    private void OnUpdateTicked(object sender, UpdateTickedEventArgs e)
    {
        if (!e.IsMultipleOf(60)) return; // Only run every 60 ticks (~1 second)

        var now = DateTime.Now;
        // Use a local list only if we actually find expired entries — avoids GC pressure
        List<string> expiredKeys = null;

        foreach (var kvp in _perceptions)
        {
            if ((now - kvp.Value.Timestamp).TotalMinutes > kvp.Value.LifetimeMinutes)
            {
                expiredKeys ??= new List<string>();
                expiredKeys.Add(kvp.Key);
            }
        }

        if (expiredKeys != null)
        {
            foreach (var key in expiredKeys)
            {
                _perceptions.TryRemove(key, out _);
            }
        }
    }

    /// <summary>
    /// Clears all perceptions when a new day starts (handles harvest reset automatically).
    /// </summary>
    private void OnDayStarted(object sender, DayStartedEventArgs e)
    {
        _perceptions.Clear();

        if (ModEntry.Config.Debug)
        {
            ModEntry.SMonitor?.Log("[Perception] All perceptions cleared on new day.", LogLevel.Debug);
        }
    }
}
