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
    internal void Enqueue(string npcName, string text, int duration, string source)
    {
        if (string.IsNullOrWhiteSpace(npcName) ||
            string.IsNullOrWhiteSpace(text))
            return;

        _queue.Enqueue(new Item
        {
            NpcName = npcName,
            Text = text,
            Duration = duration,
            Source = source
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
                if (VanillaInteractionGuard.HasActiveVanillaInteraction() ||
                    VanillaInteractionGuard.IsNpcInAnyVanillaInteraction(item.NpcName))
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

            bool festivalBark = string.Equals(item.Source, "Bark", StringComparison.Ordinal)
                                && Game1.CurrentEvent?.isFestival == true;

            if (npc == null ||
                DialogueUtilities.IsNpcSleeping(npc) ||
                !(festivalBark
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
