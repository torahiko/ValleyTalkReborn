using HarmonyLib;
using StardewValley;
using StardewModdingAPI;

namespace ValleytalkReborn
{
    [HarmonyPatch(typeof(NPC), nameof(NPC.checkAction))]
    [HarmonyPriority(Priority.First)]
    public class NPC_CheckAction_Patch
    {
        public static SButton InitiateTypedDialogueKey => ModEntry.Config?.InitiateTypedDialogueKey ?? SButton.LeftAlt;

        public static bool Prefix(ref NPC __instance, ref bool __result, Farmer who, GameLocation l)
        {
            if (__instance == null || who == null) return true;

            if (__instance.IsInvisible || __instance.isSleeping.Value || !who.CanMove)
                return true;

            if (!ModEntry.Config.EnableMod || !DialogueBuilder.Instance.PatchNpc(__instance))
                return true;

            bool wasTriggerKeyDown = ModEntry.SHelper?.Input?.IsDown(InitiateTypedDialogueKey) ?? false;

            // ══════════════════════════════════════════════
            // 分支 A：Alt + 点击 → 自定义文本输入
            // ══════════════════════════════════════════════
            if (wasTriggerKeyDown)
            {
                if (__instance.Sprite.CurrentAnimation != null) return true;
                if (__instance.currentMarriageDialogue.Count > 0) return true;
                if (__instance.hasTemporaryMessageAvailable()) return true;

                DialogueBuilder.Instance.ClearContext(__instance.Name);
                var character = DialogueBuilder.Instance.GetCharacter(__instance);

                if (Game1.player.friendshipData.TryGetValue(__instance.Name, out var caFriendship))
                    caFriendship.TalkedToToday = true;

                var displayName = __instance.displayName ?? __instance.Name ?? "NPC";
                var prompt = Util.GetString(character, "uiStartConversation", new { Name = displayName }) ?? $"What do you want to say to {displayName}?";

                TextInputManager.RequestTextInput(prompt, __instance);

                __result = false;
                return false;
            }

            // ══════════════════════════════════════════════
            // 分支 B：再次对话（InfiniteChat）→ 触发 AI 续聊
            // ══════════════════════════════════════════════
            if (ModEntry.Config.EnableInfiniteChat && who.IsLocalPlayer)
            {
                if (__instance.Sprite.CurrentAnimation == null &&
                    __instance.currentMarriageDialogue.Count == 0 &&
                    !__instance.hasTemporaryMessageAvailable() &&
                    !__instance.isMoving() &&
                    who.ActiveObject == null && who.CurrentItem == null)
                {
                    bool hasTalkedToday = who.friendshipData.TryGetValue(__instance.Name, out var fs) && fs.TalkedToToday;
                    if (hasTalkedToday)
                    {
                        // 把 hasBeenKissedToday 设为 true，让 KISS mod 看到后主动跳过
                        __instance.hasBeenKissedToday.Value = true;
                        __instance.CurrentDialogue.Clear();

                        // 【修复 1】直接在屏幕画出占位对话框，唤醒 AsyncBuilder
                        Game1.activeClickableMenu = new StardewValley.Menus.DialogueBox(new Dialogue(__instance, "", "   "));

                        Game1.currentSpeaker = __instance;
                        AsyncBuilder.Instance.RequestNpcBasic(__instance, "InfiniteChat", "");

                        __result = true;
                        return false;
                    }
                }
            }

            // ══════════════════════════════════════════════
            // 分支 C：普通点击 → 触发 AI 对话
            // ══════════════════════════════════════════════
            if (__instance.hasTemporaryMessageAvailable()) return true;
            if (__instance.currentMarriageDialogue.Count > 0) return true;

            // 拦截重复点击
            if (AsyncBuilder.Instance.AwaitingGeneration || AsyncBuilder.Instance.IsGeneratingDialogue)
            {
                __result = true;
                return false;
            }

            if (ModEntry.Config.EnableVanillaFirst)
            {
                if (__instance.CurrentDialogue != null && __instance.CurrentDialogue.Count > 0)
                    return true;

                bool hasTalkedToday = who.friendshipData.TryGetValue(__instance.Name, out var fs2) && fs2.TalkedToToday;
                if (!hasTalkedToday)
                    return true;
            }

            if (who.friendshipData.TryGetValue(__instance.Name, out var friendship))
                friendship.TalkedToToday = true;

            __instance.CurrentDialogue.Clear();

            // 【修复 2】彻底抛弃 Push，直接打开 DialogueBox，根除幽灵空白框
            Game1.activeClickableMenu = new StardewValley.Menus.DialogueBox(new Dialogue(__instance, "", "   "));

            Game1.currentSpeaker = __instance;
            AsyncBuilder.Instance.RequestNpcBasic(__instance, "default", "");

            // 【修复 3】必须返回 false 拦截原版引擎的执行
            __result = true;
            return false;
        }
    }
} 