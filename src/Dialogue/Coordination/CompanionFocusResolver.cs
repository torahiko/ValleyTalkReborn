#nullable disable

using StardewModdingAPI;
using StardewValley;

namespace ValleytalkReborn;

/// <summary>
/// 仅主线程调用。从现有公共状态推导伴侣聚焦模式，不持有状态、不做任何写操作。
/// 判定顺序（短路）：null/生命周期 → 节日 → 约会系统（DateManager） → 普通跟随（MovementManager） → None。
/// </summary>
internal static class CompanionFocusResolver
{
    public static CompanionFocusMode Resolve(NPC npc)
    {
        if (npc == null
            || !Context.IsWorldReady
            || Game1.player == null)
        {
            return CompanionFocusMode.None;
        }

        // 节日场景语境主导，不进入聚焦态。
        if (Game1.CurrentEvent?.isFestival == true)
        {
            return CompanionFocusMode.None;
        }

        if (ModEntry.Config?.EnableDateSystem == true)
        {
            var date = DateManager.Instance;
            if (date != null && date.IsOnDate(npc.Name))
            {
                return date.CurrentDateMode == DateManager.DateMode.Follow
                    ? CompanionFocusMode.DateWalking
                    : CompanionFocusMode.DateSettled;
            }
        }

        if (MovementManager.Instance?.IsFollowing(npc) == true)
        {
            return CompanionFocusMode.RegularFollow;
        }

        return CompanionFocusMode.None;
    }
}
