using System;
using System.Collections.Concurrent;
using StardewValley;

namespace ValleytalkReborn;

/// <summary>
/// Interface for validating A2A output before display.
/// Injected into MainThreadOutputQueue to remove static coupling.
/// </summary>
internal interface IA2AOutputValidator
{
    /// <summary>
    /// Validate whether an A2A output should be displayed.
    /// </summary>
    bool Validate(
        string sessionId,
        int generation,
        string npcName);
}

/// <summary>
/// 主线程输出队列：统一收集 Bark 和 A2A 的气泡输出请求，
/// 在主线程统一处理显示，确保所有游戏对象访问发生在主线程。
/// </summary>
internal sealed class MainThreadOutputQueue
{
    /// <summary>
    /// 输出项数据。
    /// </summary>
    internal sealed class Item
    {
        public string NpcName { get; set; }
        public string Text { get; set; }
        public int Duration { get; set; } = 3500;
        public string Source { get; set; }

        // A2A 校验字段（Bark 输出时为 null）
        public string A2ASessionId { get; set; }
        public int A2AGeneration { get; set; } = -1;

        // MicroSocial 直出标记（桥记录点守卫）
        public bool IsMicroSocialBark { get; set; }
    }

    private readonly ConcurrentQueue<Item> _queue =
        new ConcurrentQueue<Item>();
    private readonly IA2AOutputValidator _a2aValidator;

    internal MainThreadOutputQueue(IA2AOutputValidator a2aValidator)
    {
        _a2aValidator = a2aValidator;
    }

    /// <summary>
    /// 将输出请求加入队列。
    /// </summary>
    internal void Enqueue(string npcName, string text, int duration, string source, bool isMicroSocialBark = false)
    {
        if (string.IsNullOrWhiteSpace(npcName) ||
            string.IsNullOrWhiteSpace(text))
            return;

        _queue.Enqueue(new Item
        {
            NpcName = npcName,
            Text = text,
            Duration = duration,
            Source = source,
            IsMicroSocialBark = isMicroSocialBark
        });
    }

    /// <summary>
    /// 将 A2A 输出请求加入队列（带 SessionId/Generation 校验）。
    /// </summary>
    internal void EnqueueA2A(string sessionId, int generation, string npcName, string text, int duration)
    {
        if (string.IsNullOrWhiteSpace(sessionId) ||
            string.IsNullOrWhiteSpace(npcName) ||
            string.IsNullOrWhiteSpace(text))
            return;

        _queue.Enqueue(new Item
        {
            A2ASessionId = sessionId,
            A2AGeneration = generation,
            NpcName = npcName,
            Text = text,
            Duration = duration,
            Source = "A2A"
        });
    }

    /// <summary>
    /// 清空队列。
    /// </summary>
    internal void Clear()
    {
        while (_queue.TryDequeue(out _)) { }
    }

    /// <summary>
    /// 清除指定类型（如 "Bark"）的所有未播放队列条目，其余类型条目保持原有顺序不变。
    /// 访问模型：纯主线程调用（与 Enqueue/Process 一致），不引入新锁。
    /// 空队列 O(1) 快速返回，无 LINQ/GC 分配。
    /// </summary>
    public void ClearType(string type)
    {
        if (string.IsNullOrEmpty(type) || _queue.IsEmpty)
            return;

        // 仅遍历当前快照数量：先出队，非目标类型重新入队，目标类型直接丢弃。
        int count = _queue.Count;
        for (int i = 0; i < count; i++)
        {
            if (!_queue.TryDequeue(out var item))
                break;
            if (item != null &&
                string.Equals(item.Source, type, StringComparison.OrdinalIgnoreCase))
            {
                continue; // 丢弃目标类型条目
            }
            _queue.Enqueue(item);
        }
    }

    /// <summary>
    /// 处理队列中的输出请求（必须在主线程调用）。
    /// </summary>
    internal void Process(int maxPerTick)
    {
        int processed = 0;

        while (processed < maxPerTick &&
               _queue.TryDequeue(out var item))
        {
            processed++;

            if (item == null || Game1.player == null)
                continue;

            // ★ Bark 门禁：Bark 关闭时直接丢弃 Bark 条目，零副作用（不播放、不写历史、不触发状态）；A2A 条目不受影响
            if (string.Equals(item.Source, "Bark", StringComparison.OrdinalIgnoreCase)
                && !ModEntry.Config.EnableAmbientBarks)
            {
                continue;
            }

            // A2A 输出校验：检查会话是否仍然有效
            if (!string.IsNullOrEmpty(item.A2ASessionId))
            {
                if (!_a2aValidator.Validate(
                    item.A2ASessionId,
                    item.A2AGeneration,
                    item.NpcName))
                {
                    ModEntry.SMonitor?.Log(
                        $"[A2A] 输出丢弃：会话已失效或 Generation 不匹配 ({item.NpcName})",
                        StardewModdingAPI.LogLevel.Debug);
                    continue;
                }

                // 检查原版交互守卫
                bool vanillaBlocked =
                    (VanillaInteractionGuard.HasActiveVanillaInteraction() && !VanillaInteractionGuard.IsFestivalRoam())
                    || VanillaInteractionGuard.IsNpcInAnyVanillaInteraction(item.NpcName);
                if (vanillaBlocked)
                {
                    ModEntry.SMonitor?.Log(
                        $"[A2A] 输出丢弃：原版交互中 ({item.NpcName})",
                        StardewModdingAPI.LogLevel.Debug);
                    continue;
                }
            }
            else if (VanillaInteractionGuard.HasActiveVanillaInteraction()
                     && !VanillaInteractionGuard.IsFestivalRoam())
            {
                // Bark 输出在原版交互期间也丢弃；
                // 节日漫游态（eventUp 单轴、无对话无菜单、CanMove）除外——放行节日 Bark 浮字
                continue;
            }

            var npc = Game1.getCharacterFromName(item.NpcName);

            if (npc == null ||
                DialogueUtilities.IsNpcSleeping(npc) ||
                !(VanillaInteractionGuard.IsFestivalRoam()
                    ? DialogueUtilities.IsInRangeSquaredDuringFestival(
                        npc,
                        Game1.player,
                        DialogueConstants.DisplayRangeSquared)
                    : DialogueUtilities.IsInRangeSquared(
                        npc,
                        Game1.player,
                        DialogueConstants.DisplayRangeSquared)))
            {
                continue;
            }

            try
            {
                string text = DialogueUtilities.ResolveDialogueTokens(item.Text);

                npc.showTextAboveHead(
                    text,
                    duration: item.Duration);

                if (item.IsMicroSocialBark)
                    FreshBarkBridgeStore.Record(npc.Name, text);

                ModEntry.SMonitor?.Log(
                    $"[{item.Source ?? "Dialogue"}] {npc.Name}: \"{text}\"",
                    StardewModdingAPI.LogLevel.Debug);
            }
            catch (Exception ex)
            {
                ModEntry.SMonitor?.Log(
                    $"[DialogueOutput] 显示失败：{ex.Message}",
                    StardewModdingAPI.LogLevel.Warn);
            }
        }
    }
}
