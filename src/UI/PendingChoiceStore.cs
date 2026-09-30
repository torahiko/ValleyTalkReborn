using System.Collections.Generic;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Menus;

namespace ValleytalkReborn.UI
{
    /// <summary>
    /// VT-UI-005 Stage 2：Custom 选择框的瞬态载荷。
    /// 由生成管线（FormatLine）写入，由主线程 OnMenuChanged 消费，全程仅存于内存。
    /// </summary>
    public sealed class PendingChoiceContext
    {
        public NPC Speaker { get; init; }

        /// <summary>SanitizeDialogueForHistory(theLine[0])，历史重建与去重依据。</summary>
        public string NpcLineSanitized { get; init; }

        /// <summary>≤3 条，空白过滤后。</summary>
        public List<string> Suggestions { get; init; }

        /// <summary>allowDateUI 且 DatePhase.None。</summary>
        public bool ShowDateOption { get; init; }

        /// <summary>Postfix 绑定的消费身份凭据。</summary>
        public IClickableMenu BoxRef { get; internal set; }
    }

    /// <summary>
    /// 单槽瞬态仓库：Set 为引用原子写（供 await 续体线程使用），
    /// BoxRef 绑定与消费均发生在主线程。
    /// </summary>
    public static class PendingChoiceStore
    {
        private static PendingChoiceContext _pending;

        /// <summary>引用写入（原子），覆盖旧载荷。</summary>
        public static void Set(PendingChoiceContext context)
        {
            _pending = context;
        }

        /// <summary>不清空，供 Postfix 绑定或 OnMenuChanged 探活。</summary>
        public static bool TryPeek(out PendingChoiceContext context)
        {
            context = _pending;
            return context != null;
        }

        /// <summary>
        /// BoxRef == closedBox 才返回并清空；不匹配（事件打断等）→ Clear + Trace + null。
        /// </summary>
        public static PendingChoiceContext ConsumeMatching(IClickableMenu closedBox)
        {
            var pending = _pending;

            if (pending != null && closedBox != null && ReferenceEquals(pending.BoxRef, closedBox))
            {
                _pending = null;
                return pending;
            }

            ModEntry.SMonitor?.Log(
                $"[PendingChoiceStore] discarded stale choice payload" +
                $" (speaker={pending?.Speaker?.Name ?? "null"}, pendingBox={pending?.BoxRef?.GetType().Name ?? "null"}, closedBox={closedBox?.GetType().Name ?? "null"}).",
                LogLevel.Trace);

            Clear();
            return null;
        }

        public static void Clear()
        {
            if (_pending == null) return;

            _pending = null;
            ModEntry.SMonitor?.Log("[PendingChoiceStore] cleared.", LogLevel.Trace);
        }
    }
}
