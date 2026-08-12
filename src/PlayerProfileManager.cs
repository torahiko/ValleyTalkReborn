using System.Collections.Generic;
using StardewModdingAPI;
using StardewValley;

namespace ValleytalkReborn
{
    /// <summary>
    /// Clean & Lean PlayerProfileManager:
    /// Strips out rule bloat. Relies on natural LLM understanding without micromanagement instructions.
    /// </summary>
    internal static class PlayerProfileManager
    {
        public static string BuildProfileText(NPC targetNpc, string playerInput = "", ContextFlags flags = null)
        {
            var config = ModEntry.Config;
            if (!config.EnablePlayerProfile)
                return string.Empty;

            var lines = new List<string>();

            // 0. 路由评估 — reuse caller-supplied flags to avoid double evaluation
            flags ??= ContextRouter.Evaluate(targetNpc, playerInput, config.RomanceSafetyMode);

            // 1. 安全边界 (保持最简底线拦截)
            if (flags.IncludeSafetyRules)
            {
                string safetyInstruction = GetSafetyInstruction(targetNpc, config.RomanceSafetyMode);
                if (!string.IsNullOrEmpty(safetyInstruction))
                {
                    lines.Add(safetyInstruction);
                    lines.Add("");
                }
            }
            
           // 2. 短期对话连续性
           // if (targetNpc != null && flags.IncludeShortTermContext)
           // {
           //    var (lastNpcResponse, lastPlayerChoice) = RecentConversationTracker.GetRecentContext(targetNpc.Name);
           //    {
           //         lines.Add("[Immediate Conversation Context]");
           //         lines.Add("You and the farmer just spoke a moment ago.");
           //         lines.Add($"Your previous statement to the farmer was: \"{lastNpcResponse}\"");

           //        if (!string.IsNullOrEmpty(lastPlayerChoice))
           //         {
           //             lines.Add($"The farmer responded: \"{lastPlayerChoice}\"");
           //          }

           //         lines.Add("Instruction: Continue the conversation naturally. Do not repeat greetings.");
           //          lines.Add("");
           //     }
           // }

           
            // 3. 玩家自定义规则与基础身份 (精简版)
            lines.Add("=== [PLAYER-DEFINED RULES & IDENTITY] ===");
            lines.Add("Strictly obey these player-established rules in-character without explaining or break roleplay:");
            lines.Add("- Address rules: MUST apply to EVERY response.");
            lines.Add("- Behavior/Fact rules: Apply whenever relevant to the context.");
            lines.Add("");

            string saveBio = GetSaveCustomBio();
            if (!string.IsNullOrWhiteSpace(saveBio))
            {
                lines.Add($"RULES:\n{saveBio.Trim()}\n");
            }

            string genderStr = Game1.player.IsMale ? "Male" : "Female";
            lines.Add($"Farmer Info: Gender={genderStr}, Orientation={config.PlayerSexualOrientation}");

            return string.Join("\n", lines);
        }

        private static string GetSaveCustomBio()
        {
            if (string.IsNullOrWhiteSpace(StardewModdingAPI.Constants.SaveFolderName) || ModEntry.SHelper == null)
                return "";

            string path = $"data/{StardewModdingAPI.Constants.SaveFolderName}/PlayerProfile.json";
            
            try
            {
                var saveData = ModEntry.SHelper.Data.ReadJsonFile<Dictionary<string, string>>(path);
                if (saveData != null && saveData.TryGetValue("PlayerCustomBio", out string bio))
                {
                    return bio;
                }
            }
            catch
            {
                // 静默忽略
            }

            return "";
        }

        private static string GetSafetyInstruction(NPC npc, SafetyModeLevel level)
        {
            if (level == SafetyModeLevel.Off || npc == null)
                return string.Empty;

            int hearts = 0;
            bool isRomanceActive = false;

            if (Game1.player.friendshipData.TryGetValue(npc.Name, out var friendship))
            {
                hearts = friendship.Points / 250;
                isRomanceActive = friendship.IsDating() || friendship.IsMarried();
            }

            return level switch
            {
                SafetyModeLevel.Strict => (!isRomanceActive)
                    ? "[Safety] Strictly platonic. No romance or flirtation."
                    : string.Empty,

                SafetyModeLevel.Moderate => (!isRomanceActive && hearts <= 7)
                    ? "[Safety] Close friends only. Keep it platonic and friendly."
                    : string.Empty,

                SafetyModeLevel.Loose => (hearts <= 4)
                    ? "[Safety] Keep a polite distance."
                    : string.Empty,

                _ => string.Empty
            };
        }
    }
}