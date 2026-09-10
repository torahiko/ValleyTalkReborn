using StardewValley;

namespace ValleytalkReborn
{
    /// <summary>
    /// PolyamorySweetLove 兼容桥接类（已委托至统一的 SpouseQueryService）。
    /// </summary>
    internal static class PolyamorySweetLoveBridge
    {
        internal static void TryInitialize()
        {
            // 保持无操作兼容，生命周期由 SpouseQueryService.Instance 统一接管
        }

        internal static bool IsOfficialSpouse(NPC npc)
            => SpouseQueryService.Instance.IsOfficialSpouse(npc);

        internal static bool IsUnofficialSpouse(NPC npc)
            => SpouseQueryService.Instance.IsUnofficialSpouse(npc);
    }
}
