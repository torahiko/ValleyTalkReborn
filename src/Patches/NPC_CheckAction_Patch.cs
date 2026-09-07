// NPC_CheckAction_Patch.cs
using System;
using System.Linq;
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
            // ★ 原版交互优先：玩家与 NPC 交互时，取消该 NPC 参与的所有 A2A 会话
            if (__instance != null && !__instance.IsInvisible && !__instance.isSleeping.Value && who != null && who.CanMove)
                DynamicBarkManager.CancelA2AForNpc(__instance.Name, "Player interaction");

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

                if (Game1.player.friendshipData.TryGetValue(__instance.Name, out var caFriendship)) caFriendship.TalkedToToday = true;

                var displayName = __instance.displayName ?? __instance.Name ?? "NPC";
                var prompt = Util.GetString(character, "uiStartConversation", new { Name = displayName }) ?? $"What do you want to say to {displayName}?";

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
            // 🌟【健壮性修复】：确保未结识的 NPC（如克莱尔）在玩家的好感度字典中已建档，
            // 彻底防止因 TryGetValue 失败导致 fsc 为 null，进而引发无限对话拦截失效的 Bug
            // ══════════════════════════════════════════════
            if (!who.friendshipData.ContainsKey(__instance.Name))
            {
                who.friendshipData.Add(__instance.Name, new Friendship());
            }
            var fsc = who.friendshipData[__instance.Name];

            // ══════════════════════════════════════════════
            // ★★★ 第二道防线：将"已交谈"拦截前置 ★★★
            // 在检查原版对话之前，先判断是否已聊过天且未开启无限对话
            // ══════════════════════════════════════════════
            if (fsc.TalkedToToday && !ModEntry.Config.EnableInfiniteChat)
            {
                bool isRomantic = fsc.IsMarried() || fsc.IsDating() || fsc.IsEngaged();

                if (isRomantic)
                {
                    if (__instance.hasBeenKissedToday.Value)
                    {
                        // 今天已通过原版互动亲过，只显示爱心表情，不再触发对话
                        __instance.doEmote(20);
                        __result = true;
                        return false;
                    }
                    // 配偶/恋人：放行让原版处理亲亲逻辑，不拦截
                    return true;
                }
                else
                {
                    // 非恋爱关系且今天已聊过：只显示头顶气泡，不弹对话框
                    bool isChinese = StardewValley.LocalizedContentManager.CurrentLanguageCode == StardewValley.LocalizedContentManager.LanguageCode.zh;
                    int timeOfDay = StardewValley.Game1.timeOfDay;

                    string[] pool;

                    if (timeOfDay < 1200)
                    {
                        pool = isChinese
                            ? new[] { "早上好。", "嗯...", "你好。" }
                            : new[] { "Good morning.", "Hello.", "Greetings." };
                    }
                    else if (timeOfDay < 1800)
                    {
                        pool = isChinese
                            ? new[] { "下午好。", "嗯...", "你好。" }
                            : new[] { "Good afternoon.", "Hello.", "Greetings." };
                    }
                    else
                    {
                        pool = isChinese
                            ? new[] { "晚上好。", "嗯...", "你好。" }
                            : new[] { "Good evening.", "Hello.", "Greetings." };
                    }

                    string greeting = pool[StardewValley.Game1.random.Next(pool.Length)];

                    __instance.showTextAboveHead(greeting);
                    __result = true;
                    return false;
                }
            }

            // ══════════════════════════════════════════════
            // ★ 核心优先：原版对话优先逻辑 (清洗 $$$%%% 脏数据并放行有效原版对白)
            // ══════════════════════════════════════════════
            if (__instance.CurrentDialogue != null && __instance.CurrentDialogue.Count > 0)
            {
                // 🌟 第一道防线：清洗栈顶的 $$$%%% 幽灵脏数据
                // 如果栈顶残留的是生成标签或跳过标签，直接弹出销毁，防止原版直接把它画在屏幕上！
                while (__instance.CurrentDialogue.Count > 0)
                {
                    var peek = __instance.CurrentDialogue.Peek();
                    var peekText = peek?.dialogues?.FirstOrDefault()?.Text;
                    if (peekText != null && (
                        peekText == SldConstants.DialogueGenerationTag ||
                        peekText.Contains("$$$%%%") ||
                        peekText.StartsWith(SldConstants.DialogueSkipTag) ||
                        peekText.StartsWith("skip#")))
                    {
                        __instance.CurrentDialogue.Pop(); // 丢弃幽灵脏数据
                    }
                    else
                    {
                        break;
                    }
                }

                // 清洗完毕后，若依然存在真正的原版剧情/日常台词，且开启了 VanillaFirst，则放行原版
                if (ModEntry.Config.EnableVanillaFirst && __instance.CurrentDialogue.Count > 0)
                {
                    try
                    {
                        var curDiag = __instance.CurrentDialogue.Peek();
                        if (curDiag?.dialogues != null && curDiag.dialogues.Count > 0)
                        {
                            var rawText = string.Join(" ", curDiag.dialogues
                                .Where(x => x != null && !string.IsNullOrWhiteSpace(x.Text))
                                .Select(x => x.Text));

                            if (!string.IsNullOrWhiteSpace(rawText))
                            {
                                var cleanedText = DialogueHistoryManager.SanitizeForStorage(rawText);
                                if (!string.IsNullOrWhiteSpace(cleanedText))
                                {
                                    // 1. 记录发话者自身原版对话（供后续 AI 续聊直接读取）
                                    DialogueHistoryManager.Instance.RecordNpcDialogue(
                                        __instance.Name,
                                        cleanedText,
                                        "vanilla"
                                    );

                                    // 2. 偷听广播给 512 像素内的路人 NPC（供 EavesdropInjector 使用）
                                    if (__instance.currentLocation != null && Game1.player != null)
                                    {
                                        var farmerLabel = Util.GetString("generalFarmerLabel") ?? "农夫";
                                        var eavesdropText = $"{farmerLabel}与{__instance.displayName}交谈，{__instance.displayName}说道：\"{cleanedText}\"";

                                        foreach (var nearbyNpc in __instance.currentLocation.characters)
                                        {
                                            if (nearbyNpc == null || nearbyNpc == __instance) continue;

                                            float dx = nearbyNpc.Position.X - __instance.Position.X;
                                            float dy = nearbyNpc.Position.Y - __instance.Position.Y;
                                            float distance = (float)Math.Sqrt(dx * dx + dy * dy);

                                            if (distance <= 512f)
                                            {
                                                DialogueHistoryManager.Instance.RecordSystemEvent(nearbyNpc.Name, eavesdropText, "eavesdrop");
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        ModEntry.SMonitor?.Log($"[CheckAction] Failed to record vanilla/eavesdrop: {ex.Message}", LogLevel.Trace);
                    }

                    return true; // 正常放行原版对话框播放
                }
            }

            // ══════════════════════════════════════════════
            // 分支 B：InfiniteChat → 仅在确定原版无词、进入 AI 交互时标记续聊
            // ══════════════════════════════════════════════
            if (ModEntry.Config.EnableInfiniteChat && who.IsLocalPlayer)
            {
                if (fsc.TalkedToToday)
                {
                    fsc.TalkedToToday = false;
                    InfiniteChatTracker.SetContinuing(__instance.Name);
                }
            }

            // ══════════════════════════════════════════════
            // 分支 C：普通点击 → 触发 AI 对话与防死锁分支
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

            fsc.TalkedToToday = true;

            __instance.CurrentDialogue.Clear();

            bool isContinuing = InfiniteChatTracker.IsContinuing(__instance.Name);
            InfiniteChatTracker.Clear(__instance.Name);

            bool accepted = AsyncBuilder.Instance.TryRequestNpcBasic(
                __instance,
                isContinuing ? "InfiniteChat" : "default",
                "");

            if (!accepted)
                return true;

            // 🌟 立即定格 NPC 并面向玩家
            __instance.Halt();
            __instance.movementPause = 20;
            __instance.facePlayer(who);

            // 🌟 使用纯空格占位，界面完全透明隐形，且触发 IsNullOrWhiteSpace 保护，绝不入库
            AsyncBuilder.SuppressHistory = true;
            Game1.activeClickableMenu = new StardewValley.Menus.DialogueBox(
                new Dialogue(__instance, "", "   "));
            AsyncBuilder.SuppressHistory = false;

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