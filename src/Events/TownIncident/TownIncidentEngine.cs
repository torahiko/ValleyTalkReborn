using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using ValleytalkReborn.Dialogue.Coordination;

namespace ValleytalkReborn;

/// <summary>
/// TIE-001: Town Incident Engine. Owns the independent SaveData key
/// "valleytalk.town-incidents", the deterministic shell of every archetype it
/// schedules, phase resolution and choice flags. Single-player only: in
/// multiplayer no incident is created, mutated or persisted. All handlers run
/// on the SMAPI main thread; no UpdateTicked subscription and no background
/// work. Scheduling happens only from DayStarted, where the engine reads the
/// game date on the main thread.
/// </summary>
internal static class TownIncidentEngine
{
    private const string SaveDataKey = "valleytalk.town-incidents";
    private const int CurrentSchemaVersion = 1;
    private const string ContestArchetypeId = "Contest";
    private const int ContestDurationDays = 8;
    private const int ContestTriggerDayOfMonth = 4;

    /// <summary>
    /// TIE-009B schedule ruling: one Contest slot every season starting on day 4
    /// (occupying days 4-11) and two rotating slots starting on days 12 and 18,
    /// each six days long (12-17 and 18-23). The derived start days keep the
    /// rotating slots clear of the Contest span by construction.
    /// </summary>
    private const int RotatingSlotDurationDays = 6;
    private const int FirstRotatingSlotStartDay = ContestTriggerDayOfMonth + ContestDurationDays;
    private const int SecondRotatingSlotStartDay = FirstRotatingSlotStartDay + RotatingSlotDurationDays;

    /// <summary>
    /// TIE-009B: rotation order of the non-Contest archetypes, with
    /// seasonIndex = (year - 1) * 4 + seasonNumber (0=Spring). Slot one takes
    /// RotationOrder[seasonIndex % 3], slot two RotationOrder[(seasonIndex + 1) % 3].
    /// </summary>
    private static readonly string[] RotationOrder = { "Friction", "Mystery", "Collaboration" };

    private static readonly string[] SeasonNames = { "Spring", "Summer", "Fall", "Winter" };

    /// <summary>
    /// TIE-009B: fixed cast per archetype, index-aligned with
    /// <see cref="IncidentArchetypeDefinition.RequiredRoles"/> so no discovery
    /// scan and no randomness is involved in assigning participants.
    /// </summary>
    private static readonly Dictionary<string, string[]> FixedCasts = new(StringComparer.Ordinal)
    {
        ["Contest"] = new[] { "Gus", "Abigail", "Alex" },       // Host, Champion, Skeptic
        ["Friction"] = new[] { "George", "Marnie", "Alex" },    // Victim, Culprit, Witness
        ["Mystery"] = new[] { "Pierre", "Sebastian", "Leah" },  // Loser, Suspect, Investigator
        ["Collaboration"] = new[] { "Robin", "Sam", "Haley" },  // Organizer, Worker, Slacker
    };

    /// <summary>
    /// TIE-CAST-001: curated anchor pool per archetype, keyed by archetype id.
    /// The pool's first entry is the anchor for the archetype's first RequiredRole
    /// (Host/Victim/Loser/Organizer). Defaults equal the FixedCasts anchor names;
    /// pool changes are ticket-gated data edits only — no all-town random selection.
    /// </summary>
    private static readonly Dictionary<string, string[]> AnchorPools = new(StringComparer.Ordinal)
    {
        ["Contest"] = new[] { "Gus" },          // Host
        ["Friction"] = new[] { "George" },      // Victim
        ["Mystery"] = new[] { "Pierre" },       // Loser
        ["Collaboration"] = new[] { "Robin" },  // Organizer
    };

    private static IModHelper _helper;
    private static IMonitor _monitor;
    private static TownIncidentData _data;
    private static bool _isSaveLoaded;
    private static bool _isDirty;
    private static bool _multiplayerDisabledLogged;

    // TIE-002: in-flight scriptwriter state — memory-only, reset with the
    // rest of the memory state (never persisted).
    private static CancellationTokenSource _scriptwriterCts;
    private static Task _scriptwriterTask;
    private static string _scriptwriterRequestedIncidentId;

    internal static EventSlotContract CurrentIncident => _data?.ActiveIncident;

    internal static void Initialize(IModHelper helper, IMonitor monitor)
    {
        _helper = helper;
        _monitor = monitor;

        Unsubscribe();
        helper.Events.GameLoop.SaveLoaded += OnSaveLoaded;
        helper.Events.GameLoop.DayStarted += OnDayStarted;
        helper.Events.GameLoop.Saving += OnSaving;
        helper.Events.GameLoop.ReturnedToTitle += OnReturnedToTitle;

        // ModEntry re-arms the engine from its own SaveLoaded handler after a
        // title-return teardown; a handler subscribed mid-raise never sees that
        // raise, so catch up here. This keeps ReadSaveData confined to the
        // SaveLoaded event (the !IsSaveLoaded guard prevents a duplicate load
        // when the engine's own handler already ran earlier in the same raise).
        if (Context.IsWorldReady && !_isSaveLoaded)
            LoadFromSave();
    }

    internal static void Cleanup()
    {
        Unsubscribe();
        ResetMemoryState();
    }

    /// <summary>
    /// Returns the current-phase acting brief for an assigned participant.
    /// Bounded dictionary lookup plus phase calculation only.
    /// </summary>
    internal static bool TryGetActorBrief(string npcName, out string briefText)
    {
        briefText = null;

        if (!_isSaveLoaded)
        {
            _monitor?.Log("[TownIncident] TryGetActorBrief rejected: SaveLoaded has not occurred.", LogLevel.Trace);
            return false;
        }

        var incident = _data?.ActiveIncident;
        if (incident == null)
            return false;

        if (!TryGetRole(incident, npcName, out string role))
            return false;

        IncidentPhase phase = GetCurrentPhase(incident);
        if (!incident.PhaseScripts.TryGetValue(phase, out var phaseBriefs)
            || !phaseBriefs.TryGetValue(npcName, out var brief))
            return false;

        briefText = $"[{role}] Motivation: {brief.Motivation} Public opinion: {brief.PublicOpinion}";
        return true;
    }

    /// <summary>
    /// Records a player dialogue choice for an assigned participant and sets
    /// the deterministic RuntimeFlags of every keyword group the selected text
    /// matches (case-insensitive substring match).
    /// </summary>
    internal static void RecordChoice(string npcName, string selectedText)
    {
        if (string.IsNullOrWhiteSpace(selectedText))
        {
            _monitor?.Log("[TownIncident] RecordChoice ignored: selected text is empty.", LogLevel.Trace);
            return;
        }

        if (CheckMultiplayerDisabled())
            return;

        var incident = _data?.ActiveIncident;
        if (incident == null)
            return;

        if (!TryGetRole(incident, npcName, out _))
            return;

        bool changed = false;
        foreach (var group in incident.BranchOutcomes)
        {
            if (incident.RuntimeFlags.TryGetValue(group.Key, out bool alreadySet) && alreadySet)
                continue;

            bool matched = false;
            foreach (var keyword in group.Value.Keys)
            {
                if (selectedText.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                {
                    matched = true;
                    break;
                }
            }

            if (matched)
            {
                incident.RuntimeFlags[group.Key] = true;
                changed = true;
            }
        }

        if (changed)
        {
            MarkDirty();
            _monitor?.Log($"[TownIncident] RecordChoice: choice of '{npcName}' matched keyword group(s); runtime flags updated.", LogLevel.Trace);
        }
    }

    internal static void MarkDirty()
    {
        _isDirty = true;
    }

    private static void OnSaveLoaded(object sender, SaveLoadedEventArgs e)
    {
        LoadFromSave();
    }

    private static void OnDayStarted(object sender, DayStartedEventArgs e)
    {
        // TIE-004/TIE-007：每日传闻认领与配额重置（内存态；多人模式下无认领，重置无害）。
        TownIncidentRumorRelay.ResetDailyClaims();

        if (CheckMultiplayerDisabled())
            return;

        var incident = _data.ActiveIncident;
        if (incident != null)
        {
            int elapsed = (int)Game1.Date.TotalDays - incident.StartGameDay;
            if (elapsed >= incident.DurationDays)
            {
                _data.ActiveIncident = null;
                MarkDirty();
                _monitor?.Log($"[TownIncident] Incident '{incident.IncidentId}' ended after {incident.DurationDays} days; cleared.", LogLevel.Info);
            }
        }

        if (_data.ActiveIncident == null)
            TryCreateIncidentForSchedule(Game1.year, Game1.season.ToString(), Game1.dayOfMonth);
    }

    private static void OnSaving(object sender, SavingEventArgs e)
    {
        if (!_isDirty)
            return;

        if (CheckMultiplayerDisabled())
            return;

        try
        {
            _helper.Data.WriteSaveData(SaveDataKey, _data);
            _isDirty = false;
        }
        catch (Exception ex)
        {
            // Declared failure path: keep _isDirty so the next Saving retries.
            _monitor?.Log($"[TownIncident] Failed to write save data '{SaveDataKey}': {ex}", LogLevel.Error);
        }
    }

    private static void OnReturnedToTitle(object sender, ReturnedToTitleEventArgs e)
    {
        ResetMemoryState();
    }

    private static void LoadFromSave()
    {
        ResetMemoryState();

        try
        {
            var loaded = _helper.Data.ReadSaveData<TownIncidentData>(SaveDataKey);
            if (loaded == null)
            {
                _data = new TownIncidentData { SchemaVersion = CurrentSchemaVersion };
                _monitor?.Log($"[TownIncident] No save data under '{SaveDataKey}'; starting with an empty model.", LogLevel.Trace);
            }
            else if (IsStructurallyValid(loaded))
            {
                _data = loaded;
            }
            else
            {
                _monitor?.Log($"[TownIncident] Save data '{SaveDataKey}' is structurally invalid; in-memory state replaced with an empty model.", LogLevel.Error);
                _data = new TownIncidentData { SchemaVersion = CurrentSchemaVersion };
            }
        }
        catch (Exception ex)
        {
            _monitor?.Log($"[TownIncident] Failed to read save data '{SaveDataKey}' ({ex.GetType().Name}): {ex.Message}", LogLevel.Error);
            _data = new TownIncidentData { SchemaVersion = CurrentSchemaVersion };
        }

        _isSaveLoaded = true;

        CheckMultiplayerDisabled();
    }

    private static bool IsStructurallyValid(TownIncidentData data)
    {
        var incident = data.ActiveIncident;
        if (incident == null)
            return true;

        return incident.AssignedRoles != null
            && incident.PhaseScripts != null
            && incident.BranchOutcomes != null
            && incident.RuntimeFlags != null
            // TIE-009B: a persisted archetype id the catalog cannot resolve is a
            // structurally invalid slot (e.g. a hand-edited or future save).
            && TownIncidentArchetypeCatalog.TryGetDefinition(incident.ArchetypeId, out _);
    }

    /// <summary>
    /// TIE-009B: activates the scheduled archetype of one calendar day. Returns
    /// true only when a new incident became active; every other outcome leaves
    /// <see cref="TownIncidentData.ActiveIncident"/> and
    /// <see cref="TownIncidentData.LastScheduleKey"/> untouched.
    /// </summary>
    internal static bool TryCreateIncidentForSchedule(int year, string season, int dayOfMonth)
    {
        if (!TryResolveSchedule(year, season, dayOfMonth, out string archetypeId, out string scheduleKey))
            return false;

        var active = _data.ActiveIncident;
        if (active != null)
        {
            _monitor?.Log(
                $"[TownIncident] Schedule slot '{scheduleKey}' skipped: incident '{active.IncidentId}' is still active.",
                LogLevel.Info);
            return false;
        }

        if (string.Equals(_data.LastScheduleKey, scheduleKey, StringComparison.Ordinal))
            return false;

        return TryActivateArchetype(archetypeId, scheduleKey);
    }

    /// <summary>
    /// TIE-009B: the Contest slot fires every season on day 4; the two rotating
    /// slots carry the rotation order. The schedule key keeps today's Contest
    /// format and lowercases the archetype id of every other slot.
    /// </summary>
    private static bool TryResolveSchedule(
        int year, string season, int dayOfMonth, out string archetypeId, out string scheduleKey)
    {
        archetypeId = null;
        scheduleKey = null;

        if (dayOfMonth == ContestTriggerDayOfMonth)
            archetypeId = ContestArchetypeId;
        else if (!TryResolveRotatingArchetype(year, season, dayOfMonth, out archetypeId))
            return false;

        scheduleKey = $"{archetypeId.ToLowerInvariant()}:{year}:{season}:{dayOfMonth}";
        return true;
    }

    private static bool TryResolveRotatingArchetype(int year, string season, int dayOfMonth, out string archetypeId)
    {
        archetypeId = null;

        int slotOffset;
        if (dayOfMonth == FirstRotatingSlotStartDay)
            slotOffset = 0;
        else if (dayOfMonth == SecondRotatingSlotStartDay)
            slotOffset = 1;
        else
            return false;

        // TIE-009B-R1: the season arrives as Game1.season.ToString(); resolve it
        // case-insensitively so capitalized enum names and the lowercase season
        // key of persisted schedule keys ("contest:1:spring:4") both match. The
        // schedule key itself keeps the casing it was given — old-save dedup
        // depends on it.
        int seasonNumber = Array.FindIndex(
            SeasonNames, name => string.Equals(name, season, StringComparison.OrdinalIgnoreCase));
        if (seasonNumber < 0)
            return false;

        int seasonIndex = (year - 1) * 4 + seasonNumber;
        archetypeId = RotationOrder[(seasonIndex + slotOffset) % RotationOrder.Length];
        return true;
    }

    /// <summary>
    /// TIE-009B: builds one shell from its archetype definition and activates it
    /// through the hard order validate → fallback → persist → scriptwriter.
    /// The shell is validated before the fallback is installed because the
    /// fallback maps role keys onto the assigned NPCs and throws on an
    /// incomplete cast.
    /// </summary>
    private static bool TryActivateArchetype(string archetypeId, string scheduleKey)
    {
        if (!TownIncidentArchetypeCatalog.TryGetDefinition(archetypeId, out var definition))
        {
            _monitor?.Log(
                $"[TownIncident] Schedule slot '{scheduleKey}': unknown archetype id '{archetypeId}'; incident not activated.",
                LogLevel.Error);
            return false;
        }

        if (!TryResolveCast(definition, scheduleKey, out var assignedRoles))
            return false;

        // TIE-002: language is Game1-dependent input — capture it here on the
        // main thread before any asynchronous work begins.
        bool isChinese = LocalizedContentManager.CurrentLanguageCode == LocalizedContentManager.LanguageCode.zh;

        var incident = new EventSlotContract
        {
            IncidentId = scheduleKey,
            ArchetypeId = definition.ArchetypeId,
            StartGameDay = (int)Game1.Date.TotalDays,
            DurationDays = definition.DefaultDurationDays,
            ClimaxLocation = definition.DefaultClimaxLocation,
            ClimaxTimeOfDay = definition.DefaultClimaxTimeOfDay,
            AssignedRoles = assignedRoles,
            EventName = definition.EventName,
            IncidentTheme = definition.IncidentTheme,
        };

        if (!TownIncidentScriptwriter.TryValidateShell(incident, out string shellError))
        {
            _monitor?.Log(
                $"[TownIncident] Schedule slot '{scheduleKey}': shell rejected: {shellError}; incident not activated.",
                LogLevel.Error);
            return false;
        }

        // TIE-002: install the language-appropriate static fallback first —
        // the incident is fully usable before the LLM request starts.
        TownIncidentScriptwriter.CreateFallback(incident, isChinese);

        _data.ActiveIncident = incident;
        _data.LastScheduleKey = scheduleKey;
        MarkDirty();

        string cast = string.Join(", ", incident.AssignedRoles.Select(kv => $"{kv.Key}={kv.Value}"));
        _monitor?.Log(
            $"[TownIncident] Created {definition.ArchetypeId} incident '{scheduleKey}' ({cast}), duration {incident.DurationDays} days.",
            LogLevel.Info);

        KickOffScriptwriter(incident, isChinese);
        return true;
    }

    /// <summary>
    /// TIE-002: starts at most one scriptwriter request per newly created
    /// incident. The LLM call and JSON parsing run on a worker thread via
    /// LlmRequestGateway; no game state is touched there.
    /// </summary>
    private static void KickOffScriptwriter(EventSlotContract incident, bool isChinese)
    {
        if (_scriptwriterRequestedIncidentId == incident.IncidentId)
            return;

        _scriptwriterRequestedIncidentId = incident.IncidentId;
        _scriptwriterCts = new CancellationTokenSource();
        var scriptwriter = new TownIncidentScriptwriter(new LlmRequestGateway(ModEntry.Config.LlmTimeoutSeconds));
        _scriptwriterTask = RunScriptwriterAsync(scriptwriter, incident, isChinese, _scriptwriterCts.Token);
    }

    private static async Task RunScriptwriterAsync(
        TownIncidentScriptwriter scriptwriter,
        EventSlotContract incident,
        bool isChinese,
        CancellationToken cancellationToken)
    {
        EventSlotContract candidate = await scriptwriter.GenerateAsync(incident, isChinese, cancellationToken);
        if (candidate == null)
            return; // failure already logged; the static fallback stays installed

        // Applying the validated script mutates engine state — back to the
        // main thread through AsyncBuilder's action queue.
        AsyncBuilder.Instance.EnqueueToMainThread(() =>
        {
            var active = _data?.ActiveIncident;
            if (active == null || !string.Equals(active.IncidentId, candidate.IncidentId, StringComparison.Ordinal))
            {
                _monitor?.Log(
                    $"[TownIncident] Scriptwriter result for incident '{candidate.IncidentId}' is no longer active; discarded.",
                    LogLevel.Debug);
                return;
            }

            candidate.RuntimeFlags = new Dictionary<string, bool>(active.RuntimeFlags);
            _data.ActiveIncident = candidate;
            MarkDirty();
            _monitor?.Log($"[TownIncident] Applied LLM-generated script to incident '{candidate.IncidentId}'.", LogLevel.Info);
        });
    }

    /// <summary>
    /// TIE-CAST-001: cast eligibility gate. The name must resolve to a real NPC
    /// (not an animal or unknown name), be a villager, and not sit on the rumor
    /// outsider blacklist. Probe-proven on 1.6.15: NPC.isVillager() and
    /// Game1.getCharacterFromName are the sanctioned lookups.
    /// </summary>
    internal static bool IsValidCastNpc(string npcName)
    {
        return Game1.getCharacterFromName(npcName) is NPC npc
            && npc.IsVillager
            && !TownIncidentRumorRelay.OutsiderBlacklistView.Contains(npcName);
    }

    /// <summary>
    /// TIE-CAST-001: FNV-1a 64 over "{scheduleKey}|{uniqueGameId}". Deterministic
    /// across processes (no string.GetHashCode / HashCode.Combine). The seed is
    /// NOT persisted; the authoritative cast source is the persisted ActiveIncident.
    /// </summary>
    internal static long ComputeCastSeed(string scheduleKey, uint uniqueGameId)
    {
        const ulong FnvOffsetBasis = 14695981039346656037UL;
        const ulong FnvPrime = 1099511628211UL;

        string input = $"{scheduleKey}|{uniqueGameId}";
        ulong hash = FnvOffsetBasis;
        foreach (char c in input)
        {
            hash ^= c;
            hash *= FnvPrime;
        }

        return unchecked((long)hash);
    }

    /// <summary>
    /// TIE-CAST-001: production entry — wires the real providers (graph-scanner
    /// neighbors with the hearts gate, the cast validity predicate, and the
    /// friendship hearts provider) and delegates to the pure ladder.
    /// </summary>
    internal static bool TryResolveCast(
        IncidentArchetypeDefinition definition, string scheduleKey,
        out Dictionary<string, string> assignedRoles)
    {
        return TryResolveCastCore(
            definition,
            scheduleKey,
            GetHeartsActiveNeighbors,
            IsValidCastNpc,
            NpcPersonaRelationScanner.GetFriendshipHearts,
            out assignedRoles);
    }

    /// <summary>
    /// TIE-CAST-001: pure, headless-runnable cast ladder.
    /// L1: seeded anchor pick from the curated pool, then fill remaining roles from
    /// the anchor's hearts-active neighbors (validity + hearts gates, seeded,
    /// excluding the anchor). L2: 2-hop (neighbors-of-neighbors) with the same
    /// filters. L3: FixedCasts verbatim.
    /// </summary>
    internal static bool TryResolveCastCore(
        IncidentArchetypeDefinition definition, string scheduleKey,
        Func<string, IReadOnlyList<string>> neighborsProvider,
        Func<string, bool> validityPredicate,
        Func<string, int> heartsProvider,
        out Dictionary<string, string> assignedRoles)
    {
        assignedRoles = null;

        if (!AnchorPools.TryGetValue(definition.ArchetypeId, out var anchorPool)
            || anchorPool == null || anchorPool.Length == 0)
        {
            _monitor?.Log(
                $"[TownIncident] Archetype '{definition.ArchetypeId}' has no complete anchor pool; incident not activated.",
                LogLevel.Error);
            return false;
        }

        if (string.IsNullOrWhiteSpace(scheduleKey))
        {
            _monitor?.Log(
                $"[TownIncident] Archetype '{definition.ArchetypeId}': malformed schedule key; falling back to fixed cast.",
                LogLevel.Error);
            return TryApplyFixedCast(definition, out assignedRoles);
        }

        long seed = ComputeCastSeed(scheduleKey, 0);
        int roleCount = definition.RequiredRoles.Count;

        string anchor = anchorPool[StableIndex(seed, anchorPool.Length, 0)];

        var roles = new Dictionary<string, string>(StringComparer.Ordinal);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { anchor };
        roles[definition.RequiredRoles[0]] = anchor;

        // L1: hearts-active 1-hop neighbors of the anchor.
        var l1Neighbors = GetActiveNeighbors(anchor, neighborsProvider, validityPredicate, heartsProvider);
        FillRemainingRoles(definition, roles, used, l1Neighbors, seed, 1);

        // L2: 2-hop (neighbors-of-neighbors), same filters.
        if (roles.Count < roleCount)
        {
            var l2Candidates = CollectTwoHop(anchor, l1Neighbors, neighborsProvider, validityPredicate, heartsProvider);
            FillRemainingRoles(definition, roles, used, l2Candidates, seed, 2);
        }

        // L3: FixedCasts verbatim.
        if (roles.Count < roleCount)
        {
            _monitor?.Log(
                $"[TownIncident] Archetype '{definition.ArchetypeId}' graph could not fill all roles; falling back to fixed cast.",
                LogLevel.Info);
            return TryApplyFixedCast(definition, out assignedRoles);
        }

        assignedRoles = roles;
        return true;
    }

    /// <summary>
    /// TIE-CAST-001: production neighbor provider — raw scanner neighbors filtered
    /// to edges that are hearts-active in either direction.
    /// </summary>
    private static IReadOnlyList<string> GetHeartsActiveNeighbors(string npcName)
    {
        if (!NpcPersonaRelationScanner.TryGetNeighbors(npcName, out var neighbors))
            return Array.Empty<string>();

        var active = new List<string>();
        foreach (var neighbor in neighbors)
        {
            if (NpcPersonaRelationScanner.HasActiveRelationInAnyDirection(npcName, neighbor))
                active.Add(neighbor);
        }

        return active;
    }

    /// <summary>
    /// TIE-CAST-001: filters a raw neighbor list through the validity and hearts
    /// gates. Provider throws are the declared BOUNDARY failure path — treat as
    /// empty graph / hearts-0 and continue, logging at Debug.
    /// </summary>
    private static IReadOnlyList<string> GetActiveNeighbors(
        string npcName,
        Func<string, IReadOnlyList<string>> neighborsProvider,
        Func<string, bool> validityPredicate,
        Func<string, int> heartsProvider)
    {
        IReadOnlyList<string> raw;
        try
        {
            raw = neighborsProvider(npcName);
        }
        catch (Exception ex)
        {
            _monitor?.Log(
                $"[TownIncident] Neighbor provider threw for '{npcName}'; treating graph as empty: {ex.Message}",
                LogLevel.Debug);
            raw = Array.Empty<string>();
        }

        if (raw == null || raw.Count == 0)
            return Array.Empty<string>();

        var active = new List<string>(raw.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in raw)
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            if (!seen.Add(candidate)) continue;
            if (string.Equals(candidate, npcName, StringComparison.OrdinalIgnoreCase)) continue;

            if (!validityPredicate(candidate)) continue;

            int hearts;
            try
            {
                hearts = heartsProvider(candidate);
            }
            catch (Exception ex)
            {
                _monitor?.Log(
                    $"[TownIncident] Hearts provider threw for '{candidate}'; treating hearts as 0: {ex.Message}",
                    LogLevel.Debug);
                hearts = 0;
            }

            if (hearts < 0) continue;

            active.Add(candidate);
        }

        return active;
    }

    /// <summary>
    /// TIE-CAST-001: fills the remaining roles (RequiredRoles[roles.Count ..]) from
    /// a deterministic, seeded ordering of the candidate list, excluding the anchor
    /// and any already-assigned NPC.
    /// </summary>
    private static void FillRemainingRoles(
        IncidentArchetypeDefinition definition,
        Dictionary<string, string> roles,
        HashSet<string> used,
        IReadOnlyList<string> candidates,
        long seed,
        int salt)
    {
        int roleCount = definition.RequiredRoles.Count;
        if (roles.Count >= roleCount)
            return;

        foreach (var candidate in OrderCandidates(candidates, seed, salt))
        {
            if (roles.Count >= roleCount)
                break;
            if (used.Add(candidate))
                roles[definition.RequiredRoles[roles.Count]] = candidate;
        }
    }

    /// <summary>
    /// TIE-CAST-001: deterministic, seeded candidate ordering — sort OrdinalIgnoreCase
    /// then rotate by a seed-derived offset. No Random, no string.GetHashCode,
    /// no HashCode.Combine.
    /// </summary>
    private static List<string> OrderCandidates(IReadOnlyList<string> candidates, long seed, int salt)
    {
        var ordered = candidates
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(c => c, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (ordered.Count <= 1)
            return ordered;

        int offset = StableIndex(seed, ordered.Count, salt);
        var rotated = new List<string>(ordered.Count);
        for (int i = 0; i < ordered.Count; i++)
            rotated.Add(ordered[(i + offset) % ordered.Count]);

        return rotated;
    }

    /// <summary>
    /// TIE-CAST-001: deterministic 64-bit-seed-derived index (0..count-1) via pure
    /// integer arithmetic only.
    /// </summary>
    private static int StableIndex(long seed, int count, int salt)
    {
        ulong x = unchecked((ulong)seed + unchecked((ulong)(uint)salt * 0x9E3779B9UL));
        return (int)(x % (uint)count);
    }

    /// <summary>
    /// TIE-CAST-001: 2-hop candidate collection — the active neighbors of each L1
    /// neighbor, excluding the anchor. Already-used NPCs are excluded later during
    /// <see cref="FillRemainingRoles"/>.
    /// </summary>
    private static List<string> CollectTwoHop(
        string anchor,
        IReadOnlyList<string> l1Neighbors,
        Func<string, IReadOnlyList<string>> neighborsProvider,
        Func<string, bool> validityPredicate,
        Func<string, int> heartsProvider)
    {
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var n1 in l1Neighbors)
        {
            var n2List = GetActiveNeighbors(n1, neighborsProvider, validityPredicate, heartsProvider);
            foreach (var n2 in n2List)
            {
                if (string.Equals(n2, anchor, StringComparison.OrdinalIgnoreCase)) continue;
                result.Add(n2);
            }
        }

        return result.ToList();
    }

    /// <summary>
    /// TIE-009B (kept verbatim): installs the fixed cast of an archetype onto its
    /// <see cref="IncidentArchetypeDefinition.RequiredRoles"/> names. This is the
    /// TIE-CAST-001 level-3 fallback and the pre-existing failure behavior.
    /// </summary>
    private static bool TryApplyFixedCast(
        IncidentArchetypeDefinition definition, out Dictionary<string, string> assignedRoles)
    {
        assignedRoles = null;

        if (!FixedCasts.TryGetValue(definition.ArchetypeId, out var cast)
            || cast.Length != definition.RequiredRoles.Count)
        {
            _monitor?.Log(
                $"[TownIncident] Archetype '{definition.ArchetypeId}' has no complete role assignment; incident not activated.",
                LogLevel.Error);
            return false;
        }

        var roles = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 0; i < definition.RequiredRoles.Count; i++)
            roles[definition.RequiredRoles[i]] = cast[i];

        foreach (var npcName in roles.Values)
        {
            if (string.IsNullOrWhiteSpace(npcName))
            {
                _monitor?.Log(
                    $"[TownIncident] Archetype '{definition.ArchetypeId}' role assignment could not resolve valid role names; incident not activated.",
                    LogLevel.Error);
                return false;
            }
        }

        assignedRoles = roles;
        return true;
    }

    private static IncidentPhase GetCurrentPhase(EventSlotContract incident)
    {
        int elapsed = (int)Game1.Date.TotalDays - incident.StartGameDay;
        return GetPhaseForElapsedDay(elapsed, incident.DurationDays);
    }

    /// <summary>
    /// TIE-009B: duration-derived three-act boundary, shared arithmetic with the
    /// scriptwriter prompt window
    /// (<see cref="TownIncidentTemplateCatalog.BuildPhaseWindowText(int, bool)"/>):
    /// boundary = ceil(D/3); Inception covers elapsed days below the boundary,
    /// Escalation below twice the boundary, Climax the rest. An 8-day shell keeps
    /// the pre-TIE-009B split of 0-2 / 3-5 / 6-7.
    /// </summary>
    internal static IncidentPhase GetPhaseForElapsedDay(int elapsedDay, int durationDays)
    {
        int boundary = (durationDays + 2) / 3;
        if (elapsedDay < boundary)
            return IncidentPhase.Inception;
        if (elapsedDay < 2 * boundary)
            return IncidentPhase.Escalation;
        return IncidentPhase.Climax;
    }

    private static bool TryGetRole(EventSlotContract incident, string npcName, out string role)
    {
        role = null;
        foreach (var kv in incident.AssignedRoles)
        {
            if (string.Equals(kv.Value, npcName, StringComparison.Ordinal))
            {
                role = kv.Key;
                return true;
            }
        }

        return false;
    }

    private static bool CheckMultiplayerDisabled()
    {
        if (!Context.IsMultiplayer)
            return false;

        if (!_multiplayerDisabledLogged)
        {
            _monitor?.Log("[TownIncident] Town Incident Engine is disabled in multiplayer: incidents are not created, mutated or persisted.", LogLevel.Info);
            _multiplayerDisabledLogged = true;
        }

        return true;
    }

    private static void ResetMemoryState()
    {
        _scriptwriterCts?.Cancel();
        _scriptwriterCts = null;
        _scriptwriterTask = null;
        _scriptwriterRequestedIncidentId = null;

        // TIE-004/TIE-007：传闻认领与配额随内存态一起重置（覆盖 SaveLoaded 与 ReturnedToTitle）。
        TownIncidentRumorRelay.ResetDailyClaims();

        _data = null;
        _isSaveLoaded = false;
        _isDirty = false;
        _multiplayerDisabledLogged = false;
    }

    private static void Unsubscribe()
    {
        _helper?.Events.GameLoop.SaveLoaded -= OnSaveLoaded;
        _helper?.Events.GameLoop.DayStarted -= OnDayStarted;
        _helper?.Events.GameLoop.Saving -= OnSaving;
        _helper?.Events.GameLoop.ReturnedToTitle -= OnReturnedToTitle;
    }
}
