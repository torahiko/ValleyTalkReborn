// NPC_CheckAction_Patch.cs
using HarmonyLib;
using StardewValley;
using StardewModdingAPI;

namespace ValleytalkReborn
{
    [HarmonyPatch(typeof(NPC), nameof(NPC.checkAction))]
    [HarmonyPriority(Priority.First)]
    public class NPC_CheckAction_Patch
    {
        public static bool TriggerKeyWasDown = false;

        public static SButton InitiateTypedDialogueKey => ModEntry.Config?.InitiateTypedDialogueKey ?? SButton.LeftAlt;

        public static bool IsTriggerKeyDown()
        {
            if (ModEntry.SHelper?.Input == null) return false;

            var input = ModEntry.SHelper.Input;
            var targetKey = InitiateTypedDialogueKey;

            if (input.IsDown(targetKey))
                return true;

            if (targetKey == SButton.LeftAlt || targetKey == SButton.RightAlt)
                return input.IsDown(SButton.LeftAlt) || input.IsDown(SButton.RightAlt);

            if (targetKey == SButton.LeftControl || targetKey == SButton.RightControl)
                return input.IsDown(SButton.LeftControl) || input.IsDown(SButton.RightControl);

            if (targetKey == SButton.LeftShift || targetKey == SButton.RightShift)
                return input.IsDown(SButton.LeftShift) || input.IsDown(SButton.RightShift);

            return false;
        }

        public static bool Prefix(ref NPC __instance, ref bool __result, Farmer who, GameLocation l)
        {
            if (__instance == null || who == null) return true;
            if (__instance.IsInvisible || __instance.isSleeping.Value || !who.CanMove)
                return true;

            bool wasTriggerKeyDown = TriggerKeyWasDown;
            TriggerKeyWasDown = false;

            // ══════════════════════════════════════════════
            // 分支 A：Alt + 点击 → 唤醒自定义文本输入框 (绝对优先)
            // ══════════════════════════════════════════════
            if (wasTriggerKeyDown)
            {
                if (!ModEntry.Config.EnableMod || !DialogueBuilder.Instance.PatchNpc(__instance))
                    return true;

                DialogueBuilder.Instance.ClearContext(__instance.Name);
                var character = DialogueBuilder.Instance.GetCharacter(__instance);

                if (Game1.player.friendshipData.TryGetValue(__instance.Name, out var caFriendship))caFriendship.TalkedToToday = true;

                var displayName = __instance.displayName ?? __instance.Name ?? "NPC";
                var prompt = Util.GetString(character, "uiStartConversation", new { Name = displayName })?? $"What do you want to say to {displayName}?";

                TextInputManager.RequestTextInput(prompt, __instance);

                ModEntry.SHelper?.Input?.Suppress(SButton.MouseRight);
                ModEntry.SHelper?.Input?.Suppress(SButton.MouseLeft);
                __result = true;
                return false;
            }

            if (!ModEntry.Config.EnableMod || !DialogueBuilder.Instance.PatchNpc(__instance))
                return true;

            // ══════════════════════════════════════════════
            // 保护：送礼与特定道具检测
            // ══════════════════════════════════════════════
            if (who.ActiveObject != null && who.ActiveObject.canBeGivenAsGift())
                return true;

            // ══════════════════════════════════════════════
            // 分支 B：InfiniteChat → 标记续聊语境
            // ══════════════════════════════════════════════
            if (ModEntry.Config.EnableInfiniteChat && who.IsLocalPlayer)
            {
                if (who.friendshipData.TryGetValue(__instance.Name, out var fs) && fs.TalkedToToday)
                {
                    fs.TalkedToToday = false;
                    InfiniteChatTracker.SetContinuing(__instance.Name);
                }
            }

            // ══════════════════════════════════════════════
            // 分支 C：普通点击 → 触发 AI 对话 / 亲吻与防死锁分支
            // ══════════════════════════════════════════════
            if (__instance.hasTemporaryMessageAvailable()) return true;
            if (__instance.currentMarriageDialogue.Count > 0) return true;

            if (AsyncBuilder.Instance.AwaitingGeneration || AsyncBuilder.Instance.IsGeneratingDialogue)
            {
                __result = true;
                return false;
            }

            if (AsyncBuilder.Instance.GenerationCooldownFrames > 0)
                return true;

            // ★ 保护：有原版特殊行为的 NPC 完全放行
            // （商店、任务触发等不依赖 CurrentDialogue 的逻辑）
            if (HasVanillaSpecialAction(__instance))
                return true;

            if (who.friendshipData.TryGetValue(__instance.Name, out var fsc) && fsc.TalkedToToday)
            {
                if (!ModEntry.Config.EnableInfiniteChat)
                {
                    bool isRomantic = fsc.IsMarried() || fsc.IsDating() || fsc.IsEngaged();

                    if (isRomantic)
                    {
                        if (__instance.hasBeenKissedToday.Value)
                        {
                            // TriggerKissReaction 已经会显示 bark，不需要再加气泡
                            __instance.doEmote(20);
                            __result = true;
                            return false;
                        }
                        return true;
                    }
                    else
                    {
                        bool isChinese = StardewValley.LocalizedContentManager.CurrentLanguageCode == StardewValley.LocalizedContentManager.LanguageCode.zh;
                        int timeOfDay = StardewValley.Game1.timeOfDay;

                        string[] pool;

                        if (timeOfDay < 1200) // 上午 (6:00 - 11:50)
                        {
                            pool = isChinese
                                ? new[] { "早上好！", "早啊！", "你好~" }
                                : new[] { "Morning!", "Good morning!", "Hey there!" };
                        }
                        else if (timeOfDay < 1800) // 下午 (12:00 - 17:50)
                        {
                            pool = isChinese
                                ? new[] { "下午好！", "你好呀！", "嗨！" }
                                : new[] { "Good afternoon!", "Hey there!", "Hi!" };
                        }
                        else // 傍晚与夜间 (18:00 - 26:00)
                        {
                            pool = isChinese
                                ? new[] { "晚上好！", "这么晚还忙呢？", "嗨，晚上好~" }
                                : new[] { "Evening!", "Good evening!", "Hey, working late?" };
                        }
                        
                        string greeting = pool[StardewValley.Game1.random.Next(pool.Length)];

                        __instance.showTextAboveHead(greeting);
                        __result = true;
                        return false;
                    }
                }
            }

            if (ModEntry.Config.EnableVanillaFirst)
            {
                if (__instance.CurrentDialogue != null && __instance.CurrentDialogue.Count > 0)
                    return true;
            }

            if (fsc != null) fsc.TalkedToToday = true;

            __instance.CurrentDialogue.Clear();

            bool isContinuing = InfiniteChatTracker.IsContinuing(__instance.Name);
            InfiniteChatTracker.Clear(__instance.Name);

            bool accepted = AsyncBuilder.Instance.TryRequestNpcBasic(
                __instance,
                isContinuing ? "InfiniteChat" : "default",
                "");

            if (!accepted)
                return true;

            Game1.activeClickableMenu = new StardewValley.Menus.DialogueBox(
                new Dialogue(__instance, "", "   "));

            ModEntry.SHelper?.Input?.Suppress(SButton.MouseRight);
            ModEntry.SHelper?.Input?.Suppress(SButton.MouseLeft);

            __result = true;
            return false;
        }

        /// <summary>
        /// 判断该 NPC 是否在原版 checkAction 中有非对话的特殊行为
        /// （弹出商店菜单、触发特殊事件等）。对这类 NPC 应完全放行原版逻辑。
        /// </summary>
        private static bool HasVanillaSpecialAction(NPC npc)
        {
            switch (npc.Name)
            {
                // 自身即为商店入口的特殊 NPC
                case "Krobus":
                case "Dwarf":
                case "Sandy":
                    return true;

                // 在自己的店里时才会触发商店，闲逛时为普通 NPC
                case "Marnie":
                    return IsInsideOwnShop(npc, "AnimalShop");
                case "Pierre":
                    return IsInsideOwnShop(npc, "SeedShop");
                case "Robin":
                    return IsInsideOwnShop(npc, "ScienceHouse");
                case "Clint":
                    return IsInsideOwnShop(npc, "Blacksmith");
                case "Willy":
                    return IsInsideOwnShop(npc, "FishShop");
                case "Gus":
                    return IsInsideOwnShop(npc, "Saloon");
                case "Harvey":
                    return IsInsideOwnShop(npc, "Hospital");

                default:
                    return false;
            }
        }

        /// <summary>
        /// 检查 NPC 当前是否在其所属的商店建筑内。
        /// </summary>
        private static bool IsInsideOwnShop(NPC npc, string locationName)
        {
            return npc.currentLocation?.Name == locationName;
        }
    }
}