// EavesdropInjector.cs
using System.Linq;
using System.Text;
using StardewValley;

namespace ValleytalkReborn
{
    /// <summary>
    /// Injects eavesdrop history entries into the dialogue prompt before LLM inference.
    /// Called after perception injection (S4) so eavesdrop context appears last in SystemPrompt.
    /// </summary>
    internal static class EavesdropInjector
    {
        /// <summary>
        /// Builds the eavesdrop context block for the given NPC.
        /// Returns a formatted string ready to be injected into prompts, or empty string if no eavesdrop context exists.
        /// </summary>
        public static string BuildBlock(string npcName)
        {
            if (string.IsNullOrEmpty(npcName)) return string.Empty;

            var entries = DialogueHistoryManager.Instance.GetHistory(npcName)
                .Where(e => e.DialogueType == "eavesdrop")
                .TakeLast(2)
                .ToList();

            if (entries.Count == 0) return string.Empty;

            bool isZh = LocalizedContentManager.CurrentLanguageCode
                .ToString()
                .StartsWith("zh", System.StringComparison.OrdinalIgnoreCase);

            var sb = new StringBuilder();
            sb.AppendLine("<eavesdrop_context>");
            sb.AppendLine(isZh
                ? "你刚才在附近偶然听到了以下对话片段。若与当前话题自然相关可含蓄提及，不要机械复述。"
                : "You just overheard the following nearby. Mention only if naturally relevant; do not repeat mechanically.");

            foreach (var entry in entries)
                sb.AppendLine($"- {entry.Text}");

            sb.AppendLine("</eavesdrop_context>");

            // 消费时机后移至 LlmDialogueService：确认「本轮 Prompt 已成功送出」之后，
            // 由 ConfirmDynamicBlocksConsumed 统一调用 ConsumeEavesdropEntries。
            // 此处只做纯构建，保证 peek 语义——同一 npcName 可安全调用多次、幂等返回相同内容。
            return sb.ToString();
        }
    }
}