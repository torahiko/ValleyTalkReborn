using System.Collections.Generic;
using System.Linq;
using System.Threading;
using StardewValley;

namespace ValleytalkReborn;

/// <summary>
/// Dialogue system data models (extracted from DynamicBarkManager).
/// </summary>
internal static class DialogueModels
{
    /// <summary>
    /// Reason an LLM request ended.
    /// </summary>
    internal enum LlmRequestEndReason
    {
        Success,
        Failed,
        Timeout,
        Cancelled
    }

    internal sealed class OverheadLineRequest
    {
        public string NpcName { get; set; }
        public string Text { get; set; }
        public int Duration { get; set; } = 3500;
    }

    internal sealed class BarkRequest
    {
        public string NpcName { get; set; }
        public string SystemPrompt { get; set; }
        public string UserPrompt { get; set; }
        public bool IsChinese { get; set; }
    }

    internal sealed class BarkLlmResult
    {
        public string NpcName { get; set; }
        public int RequestId { get; set; }
        public string[] Barks { get; set; }
        public bool UseFallback { get; set; }
        public bool Cancelled { get; set; }
        public bool IsChinese { get; set; }
        public LlmRequestEndReason EndReason { get; set; } = LlmRequestEndReason.Success;
    }

    internal sealed class A2ARequest
    {
        public string SystemPrompt { get; set; }
        public string UserPrompt { get; set; }
        public bool IsChinese { get; set; }
        public string NamesLog { get; set; }
    }

    internal sealed class A2ALine
    {
        public string SpeakerName { get; set; }
        public string Line { get; set; }
    }

    internal sealed class A2ACompletedResult
    {
        public string SessionId { get; set; }
        public string RawJson { get; set; }
        public bool UseFallback { get; set; }
        public bool Cancelled { get; set; }
        public bool IsChinese { get; set; }
        public LlmRequestEndReason EndReason { get; set; } = LlmRequestEndReason.Success;
    }

    internal sealed class NpcCandidate
    {
        public NPC Npc { get; set; }
        public int Priority { get; set; }
    }

    /// <summary>
    /// A2A session state machine (N-person, supports 2-4).
    /// Note: Participants only store NPC names, not NPC object references.
    /// </summary>
    internal sealed class A2ASession
    {
        public string SessionId { get; set; }
        public List<string> ParticipantNames { get; } = new List<string>();
        public bool MembersLocked { get; set; } = false;
        public int StareTicks { get; set; } = 0;
        public int IdleTicks { get; set; } = 0;
        public int RoundsLeft { get; set; } = 0;
        public int ReadCooldownTicks { get; set; } = 0;
        public Queue<A2ALine> Script { get; } = new Queue<A2ALine>();

        /// <summary>
        /// Session generation counter, incremented each time a new session is created.
        /// </summary>
        public int Generation { get; set; } = 0;

        /// <summary>
        /// Reservation owner string for this session NPC locks.
        /// </summary>
        public string ReservationOwner { get; set; }

        private int _isEnding = 0;
        public bool IsEnding => Interlocked.CompareExchange(ref _isEnding, 0, 0) == 1;

        private int _isCancelled = 0;

        public void Cancel() => Interlocked.Exchange(ref _isCancelled, 1);

        public bool IsCancelled => Interlocked.CompareExchange(ref _isCancelled, 0, 0) == 1;

        public CancellationTokenSource RequestCts { get; set; }

        /// <summary>
        /// Mark session as ending (idempotent).
        /// </summary>
        public void MarkEnding() => Interlocked.Exchange(ref _isEnding, 1);

        private int _isRequesting = 0;

        public bool TryStartRequest() => Interlocked.CompareExchange(ref _isRequesting, 1, 0) == 0;

        public void FinishRequest() => Interlocked.Exchange(ref _isRequesting, 0);

        /// <summary>
        /// Build a reservation owner string for a given session ID.
        /// </summary>
        public static string GetReservationOwner(string sessionId)
        {
            return $"A2A:{sessionId}";
        }

        /// <summary>
        /// Resolve current participant NPC objects on the main thread.
        /// </summary>
        public List<NPC> ResolveParticipants()
        {
            var result = new List<NPC>();

            foreach (var name in ParticipantNames)
            {
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                var npc = Game1.getCharacterFromName(name);

                if (npc != null)
                    result.Add(npc);
            }

            return result;
        }

        /// <summary>
        /// Check whether all declared participants can be resolved to NPC objects.
        /// </summary>
        public bool HasAllParticipantsResolved()
        {
            if (ParticipantNames.Count < 2)
                return false;

            var participants = ResolveParticipants();

            if (participants.Count != ParticipantNames.Count)
                return false;

            return !participants.Any(n => DialogueUtilities.IsNpcSleeping(n));
        }

        /// <summary>
        /// Check whether all participants are within stare range (main thread only).
        /// </summary>
        public bool AllInStareRange()
        {
            if (!HasAllParticipantsResolved())
                return false;

            var participants = ResolveParticipants();

            for (int i = 0; i < participants.Count; i++)
            {
                for (int j = i + 1; j < participants.Count; j++)
                {
                    if (!DialogueUtilities.IsInRangeSquared(participants[i], participants[j], A2ASessionManager.A2A_BREAK_RANGE_SQ))
                        return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Check whether the session should break during playback (main thread only).
        /// </summary>
        public bool ShouldBreakDuringPlayback()
        {
            var participants = ResolveParticipants();

            if (participants.Count < 2) return true;

            var firstLoc = participants[0]?.currentLocation;
            if (firstLoc == null) return true;

            for (int i = 0; i < participants.Count; i++)
            {
                if (participants[i] == null) return true;
                if (participants[i].currentLocation != firstLoc) return true;

                for (int j = i + 1; j < participants.Count; j++)
                {
                    if (participants[j] == null) return true;
                    if (participants[j].currentLocation != firstLoc) return true;

                    if (!DialogueUtilities.IsInRangeSquared(participants[i], participants[j], A2ASessionManager.A2A_PLAYBACK_MAX_SPREAD_SQ))
                        return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Check whether all participants are outside display range (main thread only).
        /// </summary>
        public bool AllParticipantsOutOfDisplayRange()
        {
            if (ParticipantNames.Count == 0) return true;

            var participants = ResolveParticipants();

            bool anyNearPlayer = Game1.player != null && participants.Any(n =>
                n != null && DialogueUtilities.IsInRangeSquared(n, (Farmer)Game1.player, AmbientBarkModule.DISPLAY_RANGE_SQ));

            return !anyNearPlayer;
        }
    }
}
