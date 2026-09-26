using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using StardewModdingAPI;

namespace ValleytalkReborn;

/// <summary>
/// TIE-002: Town Incident Scriptwriter. Attempts to replace the static
/// fallback script of a newly created incident with an LLM-generated one.
/// All Game1-dependent input (language) is captured by the caller on the
/// main thread and passed in; the request and JSON parsing run
/// asynchronously and touch no game state. A validated candidate is
/// returned to the caller, which must apply it on the main thread via
/// AsyncBuilder.EnqueueToMainThread. Every failure path retains the
/// already-installed static fallback and never retries.
/// </summary>
internal sealed class TownIncidentScriptwriter
{
    internal const string SourceIdentifier = "TownIncidentScriptwriter";

    internal const int MaxBriefTextLength = 200;
    internal const int MaxEventNameLength = 40;
    internal const int MaxThemeLength = 160;
    internal const int MaxKeywordLength = 40;
    internal const int MaxOutcomeTextLength = 200;

    private static readonly IncidentPhase[] RequiredPhases =
    {
        IncidentPhase.Inception,
        IncidentPhase.Escalation,
        IncidentPhase.Climax,
    };

    private readonly LlmRequestGateway _gateway;

    internal TownIncidentScriptwriter(LlmRequestGateway gateway)
    {
        _gateway = gateway;
    }

    /// <summary>
    /// Runs one scriptwriter request for the shell. Returns the validated
    /// candidate, or null after logging the specific failure (the static
    /// fallback stays installed in that case).
    /// </summary>
    internal async Task<EventSlotContract> GenerateAsync(
        EventSlotContract shell,
        bool isChinese,
        CancellationToken cancellationToken)
    {
        if (Llm.Instance == null)
        {
            ModEntry.SMonitor?.Log(
                $"[{SourceIdentifier}] Incident '{shell.IncidentId}': LLM instance unavailable; static fallback retained.",
                LogLevel.Warn);
            return null;
        }

        string systemPrompt = TownIncidentTemplateCatalog.BuildSystemPrompt(isChinese);
        string userPrompt = TownIncidentTemplateCatalog.BuildUserPrompt(shell, isChinese);

        LlmResponse response = await _gateway.ExecuteAsync(
            SourceIdentifier, systemPrompt, userPrompt, cancellationToken);

        if (response == null)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                ModEntry.SMonitor?.Log(
                    $"[{SourceIdentifier}] Incident '{shell.IncidentId}': request cancelled; static fallback retained.",
                    LogLevel.Debug);
            }
            else
            {
                ModEntry.SMonitor?.Log(
                    $"[{SourceIdentifier}] Incident '{shell.IncidentId}': request timed out or returned no response; static fallback retained.",
                    LogLevel.Warn);
            }
            return null;
        }

        if (!response.IsSuccess || string.IsNullOrWhiteSpace(response.Text))
        {
            ModEntry.SMonitor?.Log(
                $"[{SourceIdentifier}] Incident '{shell.IncidentId}': LLM request failed ({response.ErrorMessage ?? "empty response"}); static fallback retained.",
                LogLevel.Warn);
            return null;
        }

        EventSlotContract candidate = TryParseScript(response.Text, shell);
        if (candidate == null)
            return null;

        if (!TryValidate(candidate, shell, out string validationError))
        {
            ModEntry.SMonitor?.Log(
                $"[{SourceIdentifier}] Incident '{shell.IncidentId}': candidate script rejected: {validationError}; static fallback retained.",
                LogLevel.Error);
            return null;
        }

        return candidate;
    }

    /// <summary>
    /// Installs the language-appropriate static Contest fallback into the
    /// shell so the incident is immediately usable without any LLM.
    /// </summary>
    internal static EventSlotContract CreateFallback(EventSlotContract shell, bool isChinese)
    {
        shell.PhaseScripts = isChinese
            ? TownIncidentTemplateCatalog.BuildContestPhaseScriptsZh()
            : TownIncidentTemplateCatalog.BuildContestPhaseScriptsEn();
        shell.BranchOutcomes = isChinese
            ? TownIncidentTemplateCatalog.BuildContestBranchOutcomesZh()
            : TownIncidentTemplateCatalog.BuildContestBranchOutcomesEn();
        shell.RuntimeFlags ??= new Dictionary<string, bool>();
        return shell;
    }

    /// <summary>
    /// Extracts and deserializes the expected object shape from raw LLM
    /// text (tolerating markdown fences and prose wrappers), then maps it
    /// to a candidate contract. Returns null on any parse failure, logging
    /// Warn with the parser failure type — never the untrusted response.
    /// </summary>
    internal static EventSlotContract TryParseScript(string rawText, EventSlotContract shell)
    {
        if (string.IsNullOrWhiteSpace(rawText))
        {
            LogParseFailure(shell, "empty response text");
            return null;
        }

        string cleaned = rawText.Replace("```json", "").Replace("```", "").Trim();
        string jsonSpan = JsonStructureScanner.ExtractFirstClosedBracketSpan(cleaned, '{', '}');
        if (jsonSpan == null)
        {
            LogParseFailure(shell, "no closed JSON object found");
            return null;
        }

        ScriptDto dto;
        try
        {
            dto = JsonConvert.DeserializeObject<ScriptDto>(jsonSpan);
        }
        catch (Exception ex) when (ex is JsonException)
        {
            LogParseFailure(shell, ex.GetType().Name);
            return null;
        }

        if (dto == null)
        {
            LogParseFailure(shell, "deserialized to null");
            return null;
        }

        return MapDtoToCandidate(dto, shell);
    }

    /// <summary>
    /// Validates a candidate script against its shell: identity echo, role
    /// assignment, phase coverage, branch dictionaries and text length caps.
    /// Unknown role names and changed physical assignments are rejected.
    /// </summary>
    internal static bool TryValidate(EventSlotContract candidate, EventSlotContract shell, out string validationError)
    {
        validationError = null;

        if (candidate == null)
        {
            validationError = "candidate is null";
            return false;
        }
        if (shell == null)
        {
            validationError = "shell is null";
            return false;
        }

        if (!string.Equals(candidate.IncidentId, shell.IncidentId, StringComparison.Ordinal))
        {
            validationError = $"IncidentId mismatch (expected '{shell.IncidentId}', got '{candidate.IncidentId}')";
            return false;
        }

        if (!string.Equals(candidate.ArchetypeId, shell.ArchetypeId, StringComparison.Ordinal))
        {
            validationError = $"ArchetypeId mismatch (expected '{shell.ArchetypeId}', got '{candidate.ArchetypeId}')";
            return false;
        }

        if (!HasSameRoleAssignment(candidate.AssignedRoles, shell.AssignedRoles))
        {
            validationError = "assigned role set changed or contains unknown role names";
            return false;
        }

        if (!TryValidateText(candidate.EventName, MaxEventNameLength, "EventName", out validationError)
            || !TryValidateText(candidate.IncidentTheme, MaxThemeLength, "IncidentTheme", out validationError))
            return false;

        if (candidate.PhaseScripts == null)
        {
            validationError = "PhaseScripts is missing";
            return false;
        }

        foreach (IncidentPhase phase in RequiredPhases)
        {
            if (!candidate.PhaseScripts.TryGetValue(phase, out var briefs))
            {
                validationError = $"phase {phase} is missing";
                return false;
            }

            foreach (string npcName in shell.AssignedRoles.Values)
            {
                if (briefs == null || !briefs.TryGetValue(npcName, out var brief) || brief == null)
                {
                    validationError = $"phase {phase} has no brief for assigned NPC '{npcName}'";
                    return false;
                }

                if (!TryValidateText(brief.Motivation, MaxBriefTextLength, $"{phase}.{npcName}.Motivation", out validationError)
                    || !TryValidateText(brief.PublicOpinion, MaxBriefTextLength, $"{phase}.{npcName}.PublicOpinion", out validationError))
                    return false;
            }

            foreach (string npcKey in briefs.Keys)
            {
                if (!IsAssignedNpc(shell, npcKey))
                {
                    validationError = $"phase {phase} contains brief for unassigned NPC '{npcKey}'";
                    return false;
                }
            }
        }

        if (candidate.BranchOutcomes == null
            || candidate.BranchOutcomes.Count != shell.BranchOutcomes.Count)
        {
            validationError = "branch group key set changed";
            return false;
        }

        foreach (string groupKey in shell.BranchOutcomes.Keys)
        {
            if (!candidate.BranchOutcomes.TryGetValue(groupKey, out var keywords) || keywords == null || keywords.Count == 0)
            {
                validationError = $"branch group '{groupKey}' is missing or empty";
                return false;
            }

            foreach (var keywordKv in keywords)
            {
                if (!TryValidateText(keywordKv.Key, MaxKeywordLength, $"branch '{groupKey}' keyword", out validationError)
                    || !TryValidateText(keywordKv.Value, MaxOutcomeTextLength, $"branch '{groupKey}' outcome", out validationError))
                    return false;
            }
        }

        return true;
    }

    private static bool HasSameRoleAssignment(Dictionary<string, string> candidateRoles, Dictionary<string, string> shellRoles)
    {
        if (candidateRoles == null || candidateRoles.Count != shellRoles.Count)
            return false;

        foreach (var shellRole in shellRoles)
        {
            if (!candidateRoles.TryGetValue(shellRole.Key, out var npcName)
                || !string.Equals(npcName, shellRole.Value, StringComparison.Ordinal))
                return false;
        }

        return true;
    }

    private static bool IsAssignedNpc(EventSlotContract shell, string npcName)
    {
        foreach (var assigned in shell.AssignedRoles.Values)
        {
            if (string.Equals(assigned, npcName, StringComparison.Ordinal))
                return true;
        }

        return false;
    }

    private static bool TryValidateText(string text, int maxLength, string fieldName, out string validationError)
    {
        validationError = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            validationError = $"{fieldName} is empty";
            return false;
        }
        if (text.Length > maxLength)
        {
            validationError = $"{fieldName} exceeds {maxLength} characters ({text.Length})";
            return false;
        }
        return true;
    }

    private static EventSlotContract MapDtoToCandidate(ScriptDto dto, EventSlotContract shell)
    {
        return new EventSlotContract
        {
            IncidentId = dto.IncidentId,
            ArchetypeId = dto.ArchetypeId,
            StartGameDay = shell.StartGameDay,
            DurationDays = shell.DurationDays,
            ClimaxLocation = shell.ClimaxLocation,
            ClimaxTimeOfDay = shell.ClimaxTimeOfDay,
            AssignedRoles = dto.AssignedRoles ?? new Dictionary<string, string>(StringComparer.Ordinal),
            EventName = dto.EventName,
            IncidentTheme = dto.IncidentTheme,
            PhaseScripts = MapPhaseScripts(dto.PhaseScripts),
            BranchOutcomes = dto.BranchOutcomes ?? new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal),
            // Live runtime flags are merged back by the main-thread apply step.
            RuntimeFlags = new Dictionary<string, bool>(),
        };
    }

    private static Dictionary<IncidentPhase, Dictionary<string, RolePhaseBrief>> MapPhaseScripts(
        Dictionary<string, Dictionary<string, RoleBriefDto>> dtoPhases)
    {
        var result = new Dictionary<IncidentPhase, Dictionary<string, RolePhaseBrief>>();
        if (dtoPhases == null)
            return result;

        foreach (var phaseKv in dtoPhases)
        {
            // Unknown phase names and null bodies surface as missing-phase
            // errors in TryValidate instead of crashing here.
            if (!Enum.TryParse(phaseKv.Key, ignoreCase: true, out IncidentPhase phase) || phaseKv.Value == null)
                continue;

            var briefs = new Dictionary<string, RolePhaseBrief>(StringComparer.Ordinal);
            foreach (var npcKv in phaseKv.Value)
            {
                if (npcKv.Value == null)
                    continue;

                briefs[npcKv.Key] = new RolePhaseBrief
                {
                    Motivation = npcKv.Value.Motivation,
                    PublicOpinion = npcKv.Value.PublicOpinion,
                };
            }

            result[phase] = briefs;
        }

        return result;
    }

    private static void LogParseFailure(EventSlotContract shell, string failureType)
    {
        ModEntry.SMonitor?.Log(
            $"[{SourceIdentifier}] Incident '{shell.IncidentId}': JSON deserialization failed ({failureType}); static fallback retained.",
            LogLevel.Warn);
    }

    /// <summary>Strict DTO mirroring the expected LLM output object shape.</summary>
    private sealed class ScriptDto
    {
        public string IncidentId { get; set; }
        public string ArchetypeId { get; set; }
        public Dictionary<string, string> AssignedRoles { get; set; }
        public string EventName { get; set; }
        public string IncidentTheme { get; set; }
        public Dictionary<string, Dictionary<string, RoleBriefDto>> PhaseScripts { get; set; }
        public Dictionary<string, Dictionary<string, string>> BranchOutcomes { get; set; }
    }

    private sealed class RoleBriefDto
    {
        public string Motivation { get; set; }
        public string PublicOpinion { get; set; }
    }
}
