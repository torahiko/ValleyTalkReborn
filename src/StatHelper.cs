using System;
using System.Collections.Generic;
using System.Reflection;
using StardewModdingAPI;
using StardewValley;

namespace ValleytalkReborn;

/// <summary>
/// Provides safe access to Game1.stats fields that changed names or type across SDV versions.
/// Resolution is cached on first call — reflection runs once per session.
/// </summary>
internal static class StatHelper
{
    // ── Cache: stat key → accessor delegate ──────────────────────
    private static readonly Dictionary<string, Func<uint>> _cache = new();
    private static bool _probed = false;

    // Known field/property name variants per stat, in probe order
    private static readonly Dictionary<string, string[]> _candidates = new()
    {
        // crops shipped / harvested
        ["CropsHarvested"]  = new[] { "CropsShipped",    "cropsShipped",    "itemsShipped"     },
        // monsters killed
        ["MonstersKilled"]  = new[] { "MonstersKilled",  "monstersKilled"       },
        // geodes cracked
        ["GeodesCracked"]   = new[] { "GeodesCracked",   "geodesCracked"                        },
        // fish caught
        ["FishCaught"]      = new[] { "FishCaught",      "fishCaught"                },
        // steps taken
        ["StepsTaken"]      = new[] { "StepsTaken",      "stepsTaken"                           },
        // stone mined
        ["StoneMined"]      = new[] { "StoneMined",      "stoneMined",      "stoneGathered"     },
        // total money earned
        ["MoneyEarned"]     = new[] { "TotalMoneyEarned","totalMoneyEarned", "moneyEarned"      },
        // trees chopped
        ["TreesChopped"]    = new[] { "TreesChopped",    "treesChopped"},
        // trash cans checked
        ["TrashCansChecked"]  = new[] { "trashCansChecked",  "TrashCansChecked",  "garbageChecked"  },
    };

    /// <summary>Returns the stat value for the given logical key, or 0 if unavailable.</summary>
    public static uint Get(string logicalKey)
    {
        EnsureProbed();
        return _cache.TryGetValue(logicalKey, out var fn) ? fn() : 0u;
    }

    /// <summary>Returns true if the field was successfully resolved for the given logical key.</summary>
    public static bool IsFieldResolved(string logicalKey)
    {
        EnsureProbed();
        return _cache.ContainsKey(logicalKey);
    }

    // ─────────────────────────────────────────────────────────────

    private static void EnsureProbed()
    {
        if (_probed) return;
        _probed = true;

        var statsType = Game1.stats?.GetType();
        if (statsType == null) return;

        const BindingFlags bf = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        foreach (var kv in _candidates)
        {
            string logicalKey = kv.Key;
            foreach (string candidate in kv.Value)
            {
                // Try property first
                var prop = statsType.GetProperty(candidate, bf);
                if (prop != null && IsUintCompatible(prop.PropertyType))
                {
                    var p = prop; // capture
                    _cache[logicalKey] = () => ToUint(p.GetValue(Game1.stats));
                    ModEntry.SMonitor?.Log($"[StatHelper] {logicalKey} → property '{candidate}'", LogLevel.Debug);
                    break;
                }

                // Then field
                var field = statsType.GetField(candidate, bf);
                if (field != null && IsUintCompatible(field.FieldType))
                {
                    var f = field; // capture
                    _cache[logicalKey] = () => ToUint(f.GetValue(Game1.stats));
                    ModEntry.SMonitor?.Log($"[StatHelper] {logicalKey} → field '{candidate}'", LogLevel.Debug);
                    break;
                }
            }

            if (!_cache.ContainsKey(logicalKey)) ModEntry.SMonitor?.Log($"[StatHelper] {logicalKey} → NOT FOUND (will return 0)", LogLevel.Warn);
        }
    }

    private static bool IsUintCompatible(Type t)
        => t == typeof(uint) || t == typeof(int) || t == typeof(long)|| t == typeof(ulong) || t == typeof(float);

    private static uint ToUint(object val) => val switch
    {
        uint   u => u,
        int    i => i >= 0 ? (uint)i : 0u,
        long   l => l >= 0 ? (uint)l : 0u,
        ulong  u => (uint)u,
        float  f => f >= 0 ? (uint)f : 0u,
        _        => 0u
    };
}
