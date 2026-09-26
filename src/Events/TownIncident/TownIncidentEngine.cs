using System;
using System.Collections.Generic;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

namespace ValleytalkReborn;

/// <summary>
/// TIE-001: Town Incident Engine. Owns the independent SaveData key
/// "valleytalk.town-incidents", the deterministic Contest MVP shell
/// (Host=Gus, Champion=Abigail, Skeptic=Alex), phase resolution and choice
/// flags. Single-player only: in multiplayer no incident is created, mutated
/// or persisted. All handlers run on the SMAPI main thread; no UpdateTicked
/// subscription and no background work.
/// </summary>
internal static class TownIncidentEngine
{
    private const string SaveDataKey = "valleytalk.town-incidents";
    private const int CurrentSchemaVersion = 1;
    private const string ContestArchetypeId = "Contest";
    private const int ContestDurationDays = 8;
    private const int ContestTriggerDayOfMonth = 4;

    private static IModHelper _helper;
    private static IMonitor _monitor;
    private static TownIncidentData _data;
    private static bool _isSaveLoaded;
    private static bool _isDirty;
    private static bool _multiplayerDisabledLogged;

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

        if (_data.ActiveIncident == null && Game1.dayOfMonth == ContestTriggerDayOfMonth)
        {
            string scheduleKey = $"contest:{Game1.year}:{Game1.season}:{Game1.dayOfMonth}";
            if (_data.LastScheduleKey == scheduleKey)
                return;

            TryCreateContestShell(scheduleKey);
        }
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
            && incident.RuntimeFlags != null;
    }

    private static bool TryCreateContestShell(string scheduleKey)
    {
        if (!TryResolvePilotRoles(out var assignedRoles))
            return false;

        _data.ActiveIncident = new EventSlotContract
        {
            IncidentId = scheduleKey,
            ArchetypeId = ContestArchetypeId,
            StartGameDay = (int)Game1.Date.TotalDays,
            DurationDays = ContestDurationDays,
            ClimaxLocation = "Saloon",
            ClimaxTimeOfDay = 1900,
            AssignedRoles = assignedRoles,
            EventName = "Saloon Cook-Off",
            IncidentTheme = "A friendly cooking contest strains old rivalries in Pelican Town.",
            PhaseScripts = BuildContestPhaseScripts(),
            BranchOutcomes = BuildContestBranchOutcomes(),
            RuntimeFlags = new Dictionary<string, bool>(),
        };
        _data.LastScheduleKey = scheduleKey;
        MarkDirty();
        _monitor?.Log($"[TownIncident] Created Contest incident '{scheduleKey}' (Host=Gus, Champion=Abigail, Skeptic=Alex), duration {ContestDurationDays} days.", LogLevel.Info);
        return true;
    }

    private static bool TryResolvePilotRoles(out Dictionary<string, string> assignedRoles)
    {
        assignedRoles = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Host"] = "Gus",
            ["Champion"] = "Abigail",
            ["Skeptic"] = "Alex",
        };

        bool valid = assignedRoles.Count == 3;
        foreach (var npcName in assignedRoles.Values)
            valid &= !string.IsNullOrWhiteSpace(npcName);

        if (!valid)
        {
            _monitor?.Log("[TownIncident] MVP Contest role assignment could not resolve valid role names; incident not activated.", LogLevel.Error);
            return false;
        }

        return true;
    }

    private static IncidentPhase GetCurrentPhase(EventSlotContract incident)
    {
        int elapsed = (int)Game1.Date.TotalDays - incident.StartGameDay;
        if (elapsed <= 2)
            return IncidentPhase.Inception;
        if (elapsed <= 5)
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

    /// <summary>
    /// Deterministic static fallback script (usable without any LLM): briefs
    /// per NPC per phase for the Contest MVP.
    /// </summary>
    private static Dictionary<IncidentPhase, Dictionary<string, RolePhaseBrief>> BuildContestPhaseScripts()
    {
        return new Dictionary<IncidentPhase, Dictionary<string, RolePhaseBrief>>
        {
            [IncidentPhase.Inception] = new Dictionary<string, RolePhaseBrief>
            {
                ["Gus"] = new RolePhaseBrief
                {
                    Motivation = "Announce the Saloon Cook-Off, recruit contestants and fill every seat in the saloon.",
                    PublicOpinion = "Townsfolk are curious but noncommittal; some suspect it is just a saloon promotion.",
                },
                ["Abigail"] = new RolePhaseBrief
                {
                    Motivation = "Defend her title with a daring new recipe and prove the quiet miner's daughter can win twice.",
                    PublicOpinion = "Admirers call her fearless; traditionalists whisper that her ingredients are simply strange.",
                },
                ["Alex"] = new RolePhaseBrief
                {
                    Motivation = "Point out that the same regulars always win and push for an outside judge.",
                    PublicOpinion = "A handful of villagers admit the judging looks cozy, but most tell him to lighten up.",
                },
            },
            [IncidentPhase.Escalation] = new Dictionary<string, RolePhaseBrief>
            {
                ["Gus"] = new RolePhaseBrief
                {
                    Motivation = "Keep the contest from boiling over while rumors and side bets fill the saloon.",
                    PublicOpinion = "The town has split into camps; everyone has picked a favorite.",
                },
                ["Abigail"] = new RolePhaseBrief
                {
                    Motivation = "Train in secret, shrug off the sabotage rumors and let her cooking answer the doubt.",
                    PublicOpinion = "Her fans grow louder; her critics claim the fix is already in.",
                },
                ["Alex"] = new RolePhaseBrief
                {
                    Motivation = "Escalate the fairness complaints and float the idea of boycotting the finale.",
                    PublicOpinion = "More villagers start asking who is really judging.",
                },
            },
            [IncidentPhase.Climax] = new Dictionary<string, RolePhaseBrief>
            {
                ["Gus"] = new RolePhaseBrief
                {
                    Motivation = "Host a clean, dramatic finale and crown a winner the whole town can accept.",
                    PublicOpinion = "The saloon is packed; the whole town wants a fair result.",
                },
                ["Abigail"] = new RolePhaseBrief
                {
                    Motivation = "Plate her boldest dish yet and silence the doubters for good.",
                    PublicOpinion = "Even her critics admit the finale is must-see.",
                },
                ["Alex"] = new RolePhaseBrief
                {
                    Motivation = "Watch the judging like a hawk and demand transparency before conceding anything.",
                    PublicOpinion = "He promised to eat his words if the result is clean.",
                },
            },
        };
    }

    /// <summary>
    /// Deterministic keyword groups for the Contest MVP: the outer key is the
    /// RuntimeFlags flag that RecordChoice sets when the player's selected
    /// text contains any of the inner keywords.
    /// </summary>
    private static Dictionary<string, Dictionary<string, string>> BuildContestBranchOutcomes()
    {
        return new Dictionary<string, Dictionary<string, string>>
        {
            ["backed_champion"] = new Dictionary<string, string>
            {
                ["cheer"] = "The town sees the player openly backing the champion.",
                ["root for"] = "The town sees the player openly backing the champion.",
                ["encourage"] = "The town sees the player openly backing the champion.",
                ["you can win"] = "The town sees the player openly backing the champion.",
                ["believe in you"] = "The town sees the player openly backing the champion.",
            },
            ["backed_skeptic"] = new Dictionary<string, string>
            {
                ["rigged"] = "The player's doubts embolden the skeptic camp.",
                ["unfair"] = "The player's doubts embolden the skeptic camp.",
                ["fixed"] = "The player's doubts embolden the skeptic camp.",
                ["boycott"] = "The player's doubts embolden the skeptic camp.",
                ["doubt"] = "The player's doubts embolden the skeptic camp.",
            },
            ["stayed_neutral"] = new Dictionary<string, string>
            {
                ["wait and see"] = "The player keeps the town guessing about where they stand.",
                ["fair judge"] = "The player keeps the town guessing about where they stand.",
                ["may the best"] = "The player keeps the town guessing about where they stand.",
                ["let the cooking speak"] = "The player keeps the town guessing about where they stand.",
            },
        };
    }
}
