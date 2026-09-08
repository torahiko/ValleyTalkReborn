using System;
using System.Collections.Generic;
using StardewValley;
using StardewModdingAPI;

namespace ValleytalkReborn
{
    /// <summary>
    /// Fires a follow-up LLM dialogue turn after the NPC completes a GoTo movement,
    /// giving the NPC a chance to react to having arrived.
    /// </summary>
    public static class GotoCompletionInjector
    {
        public static void InjectCompletion(NPC npc, string originalPlayerInput)
        {
            if (npc == null) return;

            string injectedInput = string.IsNullOrWhiteSpace(originalPlayerInput)
                ? "[SYSTEM: You just finished moving to the requested location. React naturally to having arrived.]"
                : $"[SYSTEM: The player previously said: \"{originalPlayerInput}\". You have just arrived at the destination. Please respond in character now that you are there.]";

            ModEntry.SMonitor?.Log(
                $"[GotoCompletionInjector] Injecting completion prompt for {npc.Name}: {injectedInput}",
                LogLevel.Debug);

            StardewValley.DelayedAction.functionAfterDelay(() =>
            {
                try
                {
                    var character = DialogueBuilder.Instance.GetCharacter(npc);
                    if (character == null)
                    {
                        ModEntry.SMonitor?.Log(
                            $"[GotoCompletionInjector] Character not found for {npc.Name}, skipping.",
                            LogLevel.Warn);
                        return;
                    }

                    var syntheticHistory = new List<ConversationElement>
                    {
                        new ConversationElement(injectedInput, IsPlayerLine: true)
                    };

                    _ = DialogueBuilder.Instance.GenerateResponse(npc, syntheticHistory, dontSkipNext: true);
                }
                catch (Exception ex)
                {
                    ModEntry.SMonitor?.Log(
                        $"[GotoCompletionInjector] Injection failed for {npc.Name}: {ex.Message}",
                        LogLevel.Warn);
                }
            }, 600);
        }
    }
}
