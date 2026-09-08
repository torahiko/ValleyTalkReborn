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
}
