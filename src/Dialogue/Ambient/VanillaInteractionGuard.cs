using StardewValley;

namespace ValleytalkReborn;

/// <summary>
/// 原版交互守卫：检测玩家是否正在与原版系统（对话、菜单、事件）交互。
/// 当检测到原版交互时，A2A 应暂停推进并丢弃待输出台词。
///
/// 所有访问必须在主线程进行。
/// </summary>
internal static class VanillaInteractionGuard
{
    /// <summary>
    /// 检查当前是否存在原版交互（对话、菜单、事件）。
    /// </summary>
    internal static bool HasActiveVanillaInteraction()
    {
        try
        {
            return Game1.dialogueUp
                || Game1.activeClickableMenu != null
                || Game1.eventUp
                || Game1.paused;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>
    /// 检查指定 NPC 是否正在参与原版对话。
    /// </summary>
    internal static bool IsNpcInVanillaDialogue(string npcName)
    {
        if (string.IsNullOrWhiteSpace(npcName))
            return false;

        try
        {
            if (!Game1.dialogueUp)
                return false;

            var speaker = Game1.currentSpeaker;
            return speaker != null &&
                   string.Equals(speaker.Name, npcName, System.StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 检查指定 NPC 是否正在参与任何原版对话。
    /// </summary>
    internal static bool IsNpcInAnyVanillaInteraction(string npcName)
    {
        return IsNpcInVanillaDialogue(npcName);
    }

    /// <summary>
    /// 判定当前是否处于"节日漫游态"——节日活动进行中、玩家可自由移动、且无对话/菜单遮挡。
    /// 这是节日漫游态的唯一权威判据，用于放行节日 Bark 浮字输出。
    ///
    /// 显式组合四轴，不依赖 CanMove 的内部实现形状（存储属性与计算属性两种假设下均正确）：
    ///   Game1.CurrentEvent != null
    ///   && Game1.CurrentEvent.isFestival
    ///   && Game1.player != null
    ///   && Game1.player.CanMove
    ///   && Game1.activeClickableMenu == null
    ///   && !Game1.dialogueUp
    ///
    /// 豁免正确性论证：
    /// - 节日致辞/评奖期间：dialogueUp=true 或 CanMove=false → IsFestivalRoam=false → 照常丢弃，无重叠弹字；
    /// - 商店/GMCM 菜单期间：activeClickableMenu != null → menu 轴独立熔断，照常丢弃；
    /// - 仅"无对话无菜单且 CanMove"的漫游态才放行 eventUp 单轴，允许节日 Bark 浮字显示。
    /// 注意：不含 Game1.eventUp 条件（CurrentEvent 非 null 即蕴含事件态）；不含 paused（调用方上下文已保证主线程非暂停）。
    /// </summary>
    internal static bool IsFestivalRoam()
    {
        try
        {
            return Game1.CurrentEvent != null
                && Game1.CurrentEvent.isFestival
                && Game1.player != null
                && Game1.player.CanMove
                && Game1.activeClickableMenu == null
                && !Game1.dialogueUp;
        }
        catch
        {
            return false;
        }
    }
}
