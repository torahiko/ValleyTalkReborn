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
        public static void Inject(string npcName, Prompts prompts)
        {
            if (string.IsNullOrEmpty(npcName) || prompts == null) return;

            var entries = DialogueHistoryManager.Instance.GetHistory(npcName)
                .Where(e => e.DialogueType == "eavesdrop")
                .TakeLast(2)
                .ToList();

            if (entries.Count == 0) return;

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

            prompts.CorePrompt += "\n\n" + sb.ToString();
        }
    }
}
