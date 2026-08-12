using System.Collections.Generic;
using StardewValley;

namespace ValleytalkReborn
{
    /// <summary>
    /// Tracks short-term conversation context per NPC to allow seamless follow-up dialogue
    /// when the player re-engages an NPC shortly after a dialogue closes.
    /// </summary>
    public static class RecentConversationTracker
    {
        private static readonly Dictionary<string, string> LastResponses = new();
        private static readonly Dictionary<string, int> LastInteractionTimes = new();
        private static readonly Dictionary<string, string> LastPlayerChoice = new();

        /// <summary>
        /// Records the last generated response and optional player selection for an NPC.
        /// </summary>
        public static void RecordResponse(string npcName, string response, string playerChoice = null)
        {
            if (string.IsNullOrWhiteSpace(npcName) || string.IsNullOrWhiteSpace(response))
                return;

            LastResponses[npcName] = response.Trim();
            LastInteractionTimes[npcName] = Game1.timeOfDay;

            if (!string.IsNullOrWhiteSpace(playerChoice))
            {
                LastPlayerChoice[npcName] = playerChoice.Trim();
            }
            else
            {
                LastPlayerChoice.Remove(npcName);
            }
        }

        /// <summary>
        /// Records the farmer's selected response so it can be included as context
        /// when the NPC's next response is recorded.
        /// </summary>
        public static void RecordPlayerChoice(string npcName, string playerChoice)
        {
            if (string.IsNullOrWhiteSpace(npcName) || string.IsNullOrWhiteSpace(playerChoice))
                return;

            LastPlayerChoice[npcName] = playerChoice.Trim();
            LastInteractionTimes[npcName] = Game1.timeOfDay;
        }

        /// <summary>
        /// Gets the immediate conversation context if the farmer spoke to the NPC within the last 30 in-game minutes.
        /// </summary>
        public static (string lastResponse, string lastPlayerChoice) GetRecentContext(string npcName)
        {
            if (LastResponses.TryGetValue(npcName, out var lastResp) &&
                LastInteractionTimes.TryGetValue(npcName, out var lastTime))
            {
                // In-game time difference check (e.g. 900 to 930 = 30 mins)
                int timeDiff = Game1.timeOfDay - lastTime;
                if (timeDiff >= 0 && timeDiff <= 30)
                {
                    LastPlayerChoice.TryGetValue(npcName, out var playerChoice);
                    return (lastResp, playerChoice);
                }
            }
            return (null, null);
        }

        /// <summary>
        /// Clears all short-term context. Call this on DayStarted or SaveLoaded events.
        /// </summary>
        public static void Clear()
        {
            LastResponses.Clear();
            LastInteractionTimes.Clear();
            LastPlayerChoice.Clear();
        }
    }
}
