using System;
using System.Collections.Generic;
using System.Linq;
using StardewModdingAPI;
using StardewValley;

namespace ValleytalkReborn;

/// <summary>
/// Lightweight cross-turn session state per NPC.
/// Survives dialogue box close/reopen within the same day.
/// </summary>
public class SessionCache
{
    public static SessionCache Instance { get; } = new();

    private readonly Dictionary<string, SessionEntry> _cache = new();
    private const int MaxTurns = 8;
    private const int ExpiryMinutes = 15;

    public class SessionEntry
    {
        public List<ConversationElement> RecentTurns { get; } = new();
        public string EmotionalTone { get; set; } = "";
        public DateTime LastActivity { get; set; } = DateTime.Now;
        public int LastUpdatedYear { get; set; } = 1;
        public Season LastUpdatedSeason { get; set; } = Season.Spring;
        public int LastUpdatedDay { get; set; } = 1;
public bool IsExpired => (DateTime.Now - LastActivity).TotalMinutes > ExpiryMinutes;
    }

    private SessionCache() { }

    public SessionEntry GetOrCreate(string npcName)
    {
        if (!_cache.TryGetValue(npcName, out var entry) || entry.IsExpired)
        {
            entry = new SessionEntry();
            _cache[npcName] = entry;
        }
        entry.LastActivity = DateTime.Now;
        return entry;
    }

    /// <summary>
    /// Merge ChatHistory lines into the persistent session after a successful turn.
    /// Only adds lines not already recorded (dedup by content).
    /// </summary>
    public void MergeHistory(string npcName, IEnumerable<ConversationElement> history, string npcReply, string mood)
    {
        var entry = GetOrCreate(npcName);
        bool isZh = LocalizedContentManager.CurrentLanguageCode.ToString().StartsWith("zh", StringComparison.OrdinalIgnoreCase);

        foreach (var element in history)
        {
            // Avoid duplicates when dialogue box reopens with carry-over lines
            if (!entry.RecentTurns.Any(t => t.Text == element.Text && t.IsPlayerLine == element.IsPlayerLine))
                entry.RecentTurns.Add(new ConversationElement(element.Text, element.IsPlayerLine)
                {
                    FuzzyTime = element.FuzzyTime
                });
        }

        if (!string.IsNullOrWhiteSpace(npcReply))
        {
            if (!entry.RecentTurns.Any(t => t.Text == npcReply && !t.IsPlayerLine))
                entry.RecentTurns.Add(new ConversationElement(npcReply, false)
                {
                    FuzzyTime = isZh ? "刚刚" : "Just now"
                });
        }

        // Trim to cap
        while (entry.RecentTurns.Count > MaxTurns)
            entry.RecentTurns.RemoveAt(0);

        if (!string.IsNullOrWhiteSpace(mood))
            entry.EmotionalTone = mood;

        // Record current game date for cross-day detection
        if (Context.IsWorldReady)
        {
            entry.LastUpdatedYear = Game1.year;
            entry.LastUpdatedSeason = (Season)Game1.season;
            entry.LastUpdatedDay = Game1.dayOfMonth;
        }
    }

    public void ResetAll() => _cache.Clear();

    public void Reset(string npcName) => _cache.Remove(npcName);
}
