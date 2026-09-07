using HarmonyLib;
using StardewValley;

namespace ValleytalkReborn
{
    [HarmonyPatch(typeof(Dialogue), nameof(Dialogue.TryGetDialogue))]
    public class Dialogue_TryGetDialogue_Patch
    {
        public static bool Prefix(ref Dialogue __instance, ref Dialogue __result, NPC speaker, string translationKey)
        {
            if (speaker == null || string.IsNullOrEmpty(translationKey))
            {
                return true;
            }

            ModEntry.SMonitor.Log($"Dialogue.TryGetDialogue called for {speaker.Name} with key {translationKey}", StardewModdingAPI.LogLevel.Trace);

            if (!DialogueBuilder.Instance.PatchNpc(speaker, ModEntry.Config.GeneralFrequency, true))
            {
                return true;
            }

            // 🌟 核心修复：放行雨天键值（不再强行拦截返回 $$$%%% 假对话）。
            // 保证 VanillaFirst 能正常读取真实雨天文本注入上下文，彻底消灭雨天与农舍偶现的 $$$%%% 乱码。
            return true;
        }
    }
}