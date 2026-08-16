using System.Collections.Generic;
using System.Linq;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

namespace ValleytalkReborn;

/// <summary>
/// Module 9: Extreme Activity Tracker (A-tier only)
///
/// Monitors only genuinely town-shaking events — legendary fish, half-million-gold days,
/// monster slaughter, compulsive trash diving, and new dating relationships.
/// Routine farming activities are intentionally excluded.
///
/// Results are cached in _pendingCandidate and consumed (once) by DailyHeadlineGenerator
/// at DayEnding. This module no longer writes to PerceptionManager directly.
/// </summary>
internal static class ExtremeActivityTracker
{
    private static bool _initialized = false;

    // ── Snapshot fields ──────────────────────────────────────────
    private static uint _snapMonstersKilled;
    private static uint _snapMoneyEarned;
    private static uint _snapFishCaught;
    private static bool _legendaryFishCaughtToday = false;
    private static Dictionary<string, bool> _snapDatingStatus = new();

    // ── Result cache (set at DayEnding, consumed by DailyHeadlineGenerator) ──
    private static ExtremeCandidate? _pendingCandidate = null;

    // ─────────────────────────────────────────────────────────────
    /// <summary>
    /// Represents the highest-scoring extreme activity of the day.
    /// Produced by EvaluateAndCache(), consumed by DailyHeadlineGenerator.
    /// </summary>
    internal readonly struct ExtremeCandidate
    {
        public readonly string Key;
        public readonly int    Score;
        public readonly string Template;
        public readonly string Context; // ✅ 新增：用于传递 NPC 名字等额外上下文

        public ExtremeCandidate(string key, int score, string template, string context = "")
        {
            Key      = key;
            Score    = score;
            Template = template;
            Context  = context;
        }
    }

    // ─────────────────────────────────────────────────────────────
    public static void Initialize()
    {
        if (_initialized || ModEntry.SHelper == null) return;
        ModEntry.SHelper.Events.GameLoop.DayStarted += OnDayStarted;
        ModEntry.SHelper.Events.GameLoop.DayEnding  += OnDayEnding;
        _initialized = true;
        ModEntry.SMonitor?.Log("[ExtremeActivityTracker] Initialized.", LogLevel.Debug);
    }

    public static void Cleanup()
    {
        if (!_initialized || ModEntry.SHelper == null) return;
        ModEntry.SHelper.Events.GameLoop.DayStarted -= OnDayStarted;
        ModEntry.SHelper.Events.GameLoop.DayEnding  -= OnDayEnding;
        _initialized = false;
        _pendingCandidate = null;
        _snapDatingStatus.Clear();
    }

    // ─────────────────────────────────────────────────────────────
    //  Public API
    // ─────────────────────────────────────────────────────────────
    /// <summary>
    /// Returns the highest-scoring extreme candidate for today, or null if none triggered.
    /// Consume-once: clears the cached value after returning it.
    /// </summary>
    public static ExtremeCandidate? TryGetExtremeCandidate()
    {
        var result = _pendingCandidate;
        _pendingCandidate = null;
        return result;
    }

    /// <summary>
    /// Called by FishingPerceptionHandler when a legendary fish is caught.
    /// </summary>
    public static void NotifyLegendaryFishCaught()
    {
        _legendaryFishCaughtToday = true;
        ModEntry.SMonitor?.Log(
            "[ExtremeActivityTracker] Legendary fish caught — flagged for today.",
            LogLevel.Debug);
    }

    // ─────────────────────────────────────────────────────────────
    //  Event handlers
    // ─────────────────────────────────────────────────────────────
    private static void OnDayStarted(object sender, DayStartedEventArgs e)
    {
        _pendingCandidate         = null;
        _legendaryFishCaughtToday = false;
        TakeSnapshot();
    }

    private static void OnDayEnding(object sender, DayEndingEventArgs e)
    {
        if (!ModEntry.Config.EnablePerceptionSystem) return;
        EvaluateAndCache();
    }

    // ─────────────────────────────────────────────────────────────
    private static void TakeSnapshot()
    {
        _snapMonstersKilled = StatHelper.Get("MonstersKilled");
        _snapMoneyEarned    = StatHelper.Get("MoneyEarned");
        _snapFishCaught     = StatHelper.Get("FishCaught");

        // ✅ 快照所有可约会NPC当前的恋爱状态（使用 Keys 遍历避免 SerializableDictionary 兼容问题）
        _snapDatingStatus.Clear();
        if (Game1.player?.friendshipData != null)
        {
            foreach (string npcName in Game1.player.friendshipData.Keys)
            {
                var friendship = Game1.player.friendshipData[npcName];
                _snapDatingStatus[npcName] = (friendship.Status == FriendshipStatus.Dating);
            }
        }
    }

    private static void EvaluateAndCache()
    {
        uint monstersKilled = SafeDelta(StatHelper.Get("MonstersKilled"), _snapMonstersKilled);
        uint moneyEarned    = SafeDelta(StatHelper.Get("MoneyEarned"),    _snapMoneyEarned);
        int  trashCount     = TrashCanTracker.GetTodayCount();

        if (ModEntry.Config.Debug)
        {
            ModEntry.SMonitor?.Log(
                $"[ExtremeActivityTracker] Deltas — kills:{monstersKilled} " +
                $"earned:{moneyEarned}g trash:{trashCount} " +
                $"legendaryFish:{_legendaryFishCaughtToday}",
                LogLevel.Debug);
        }

        var candidates = new List<ExtremeCandidate>();

        // ── A-tier: Legendary fish ───────────────────────────────────────
        if (_legendaryFishCaughtToday)
            candidates.Add(new ExtremeCandidate("LegendaryFish", 95,
                PerceptionManager.PickVariant(new[]
                {
                    "Willy could barely contain himself — the farmer apparently landed a legendary fish today. An unbelievable catch.",
                    "Elliott was there when it happened. The farmer actually pulled a legendary fish out of the water today. Willy nearly fainted.",
                    "Word spread fast: the farmer caught a legendary fish today. Willy's been telling everyone who'll listen.",
                })));

        // ── A-tier: Half-million gold in one day ─────────────────────────
        if (moneyEarned >= 500_000)
            candidates.Add(new ExtremeCandidate("RichDay", 90,
                PerceptionManager.PickVariant(new[]
                {
                    $"Pierre heard the farmer cleared over {moneyEarned:N0}g in a single day. He had to sit down after hearing that.",
                    $"Gus overheard it at the counter — the farmer apparently made {moneyEarned:N0}g today. In one day.",
                    $"Lewis was stunned. The farmer reportedly brought in {moneyEarned:N0}g today. The valley hasn't seen numbers like that.",
                })));

        // ── A-tier: Monster massacre (≥100 kills) ────────────────────────
        if (monstersKilled >= 100)
            candidates.Add(new ExtremeCandidate("Slayer", ScoreKills(monstersKilled),
                PerceptionManager.PickVariant(new[]
                {
                    $"Marlon was shaking his head in disbelief — the farmer took down {monstersKilled} monsters today. That's not farming, that's war.",
                    $"Gil's seen a lot of adventurers come through, but {monstersKilled} kills in one day? Even he was impressed.",
                    $"The Adventurer's Guild is buzzing. The farmer reportedly cleared {monstersKilled} monsters out of the mines today.",
                })));

        // ── A-tier: Compulsive trash diver (≥10 cans) ────────────────────
        if (trashCount >= 10)
            candidates.Add(new ExtremeCandidate("TrashCan",
                Scale((uint)trashCount, 10, 30, 70),
                PerceptionManager.PickVariant(new[]
                {
                    $"George was genuinely upset. The farmer went through {trashCount} trash cans today. Every single one.",
                    $"Haley looked mortified. Apparently the farmer rummaged through {trashCount} trash cans in broad daylight.",
                    $"Linus mentioned it quietly — {trashCount} trash cans today. He said he understands, but the neighbors don't.",
                })));

        // ── A-tier: Dating Announcement (Bouquet given) ──────────────────
        if (Game1.player?.friendshipData != null)
        {
            foreach (string npcName in Game1.player.friendshipData.Keys)
            {
                var friendship = Game1.player.friendshipData[npcName];
                bool wasDating = _snapDatingStatus.TryGetValue(npcName, out bool prev) && prev;
                bool isDating  = (friendship.Status == FriendshipStatus.Dating);

                if (isDating && !wasDating)
                {
                    NPC npc = Game1.getCharacterFromName(npcName);
                    string partnerName = npc?.displayName ?? npcName;

                    candidates.Add(new ExtremeCandidate(
                        "DatingAnnouncement",
                        85,
                        PerceptionManager.PickVariant(new[]
                        {
                            $"Town gossip says the farmer and {partnerName} have officially started dating.",
                            $"Pierre mentioned with a smile that the farmer bought a bouquet for {partnerName} recently.",
                            $"Word got around town that things are getting romantic between the farmer and {partnerName}."
                        }),
                        partnerName // ✅ 将 NPC 名字作为 Context 传递
                    ));
                    break; // 一天只可能和一个 NPC 确立关系
                }
            }
        }

        if (candidates.Count == 0) return;

        var winner = candidates.OrderByDescending(c => c.Score).First();
        _pendingCandidate = winner;
        ModEntry.SMonitor?.Log(
            $"[ExtremeActivityTracker] Cached A-tier candidate: [{winner.Key}] score={winner.Score}",
            LogLevel.Debug);
    }

    // ─────────────────────────────────────────────────────────────
    //  Scoring helpers
    // ─────────────────────────────────────────────────────────────
    private static int ScoreKills(uint v) => Scale(v, 100, 300, 70);

    private static int Scale(uint value, uint threshold, uint cap, int baseScore)
    {
        if (value < threshold) return 0;
        if (value >= cap) return 100;
        float t = (float)(value - threshold) / (cap - threshold);
        return baseScore + (int)(t * (100 - baseScore));
    }

    private static uint SafeDelta(uint current, uint snapshot)
        => current >= snapshot ? current - snapshot : 0;
}