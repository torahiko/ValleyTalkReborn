// DialogueBuilder.cs
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Mime;
using System.Runtime;
using System.Threading;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using StardewModdingAPI;
using StardewValley;
using StardewValley.Characters;

namespace ValleytalkReborn
{
    internal class DialogueBuilder
    {
        private static int responseIndex = 20000;
        public static DialogueBuilder Instance
        {
            get
            {
                if (_instance == null)
                {
                    _instance = new DialogueBuilder();
                }
                return _instance;
            }
        }

        public ModConfig Config { get; internal set; }
        private readonly Dictionary<string, DialogueContext> _npcContexts = new Dictionary<string, DialogueContext>();
        public bool LlmDisabled { get; set; } = false;

        public DialogueContext GetContext(string npcName)
        {
            if (string.IsNullOrEmpty(npcName)) return null;
            return _npcContexts.TryGetValue(npcName, out var ctx) ? ctx : null;
        }

        private void SetContext(string npcName, DialogueContext context)
        {
            if (string.IsNullOrEmpty(npcName)) return;
            _npcContexts[npcName] = context;
        }

        private static DialogueBuilder _instance;
        private Dictionary<string, Character> _characters;
        private Random _random;
        private int _patchDate;
        private Dictionary<string, bool> _patchCharacters;

        private DialogueBuilder()
        {
            _characters = new Dictionary<string, Character>();
            _random = new Random();
        }

        private void PopulateCharacters()
        {
            foreach (var npc in Game1.characterData.Keys)
            {
                if (!_characters.ContainsKey(npc))
                {
                    var npcObject = Game1.getCharacterFromName(npc);
                    GetCharacter(npcObject);
                }
            }
        }

        public Character GetCharacter(NPC instance)
        {
            if (instance == null)
            {
                return null;
            }
            if (!_characters.ContainsKey(instance.Name))
            {
                var newCharacter = new Character(
                    instance.Name, 
                    instance);
                _characters.Add(instance.Name, newCharacter);
            }
            return _characters[instance.Name];
        }

        public Character GetCharacterByName(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || !_characters.ContainsKey(name))
            {
                return null;
            }
            return _characters[name];
        }
        
        internal async Task<string> GenerateResponse(NPC instance, List<ConversationElement> conversation, bool dontSkipNext = false, Action<string> onStreamingToken = null)
        {
            var character = GetCharacter(instance);

            DialogueContext context = GetContext(instance.Name) ?? GetBasicContext(instance);

            // ── 🔑 [TURN TRACKING] 标记为连续对话（Turn 1+） ──
            context.IsActiveTurn = true;

            // ── 生成或复用会话 ID ──
            if (string.IsNullOrEmpty(context.DialogueSessionId))
            {
                context.DialogueSessionId = $"{instance.Name}_{Game1.Date?.TotalDays ?? 0}_{DateTime.UtcNow.Ticks}";
            }

            var fullHistory = context.ChatHistory.ToList();

            foreach (var elem in conversation)
            {
                string cleanedText = CleanHistoryText(elem.Text);
                string dedupKey = DialogueHistoryManager.SanitizeForStorage(cleanedText);
                if (string.IsNullOrWhiteSpace(dedupKey)) continue;
                if (fullHistory.Count > 0
                    && fullHistory.Last().IsPlayerLine == elem.IsPlayerLine
                    && string.Equals(DialogueHistoryManager.SanitizeForStorage(fullHistory.Last().Text), dedupKey,
                         StringComparison.OrdinalIgnoreCase))
                { continue; }
                else
                {
                    fullHistory.Add(new ConversationElement(cleanedText, elem.IsPlayerLine));
                }
            }

            var cleanHistory = new List<ConversationElement>();
            foreach (var elem in fullHistory)
            {
                if (cleanHistory.Count > 0 &&
                    cleanHistory.Last().IsPlayerLine == elem.IsPlayerLine &&
                    string.Equals(
                        DialogueHistoryManager.SanitizeForStorage(cleanHistory.Last().Text),
                        DialogueHistoryManager.SanitizeForStorage(elem.Text),
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                cleanHistory.Add(elem);
            }

            context.ChatHistory = cleanHistory;

            string latestPlayerInput = conversation.LastOrDefault(x => x.IsPlayerLine)?.Text ?? "";
            context.RoutingFlags = ContextRouter.Evaluate(instance, latestPlayerInput, ModEntry.Config.RomanceSafetyMode, context.ChatHistory);

            DynamicBarkManager.CancelBackgroundTasks(instance.Name);


            // 重置上轮短路状态，防止复用 context 时 IsDuplicate 误判
            context.LocallyExecutedAction = null;

            SetContext(instance.Name, context);

// 流式传输策略：仅当（a）调用方提供了回调，且（b）当前这一回合大概率不需要工具调用，
// 或者当前 Provider 已确认支持"流式 + 工具调用"（SupportsStreamingWithTools）时，才走流式通道。
// 原因：LlmClaude / LlmGemini 的流式路径目前不会附带 tools schema，
// 如果在期望工具调用的回合（跟随/移动/邀请/结束约会等）强行流式，模型不会产出工具调用，
// 导致对应的游戏内动作被静默忽略（无报错、无日志）。
            // FOLLOW/STEP/GOTO 已改为文本标签 + UI 按钮管道，不再需要工具 schema；
            // 仅约会邀请与约会中仍需 Native tool calling（schedule_date / end_current_date / speak_in_bubble）。
            bool toolsLikelyNeededThisTurn =
                context.RoutingFlags.IsInviteRequested
                || context.RoutingFlags.IsOnDate;

            bool useStreaming = onStreamingToken != null
                && (!toolsLikelyNeededThisTurn || Llm.Instance.SupportsStreamingWithTools);
            
            if (ModEntry.Config?.Debug ?? false)
            {
                ModEntry.SMonitor?.Log(
                    $"[DialogueBuilder] {instance.Name} | " +
                    $"Streaming={useStreaming} " +
                    $"Action={context.RoutingFlags.IsActionRequested}({context.RoutingFlags.RequestedAction}) " +
                    $"Move={context.RoutingFlags.IsMovementRequested} " +
                    $"Goto={context.RoutingFlags.IsGotoRequested} " +
                    $"Invite={context.RoutingFlags.IsInviteRequested} " +
                    $"OnDate={context.RoutingFlags.IsOnDate}",
                    LogLevel.Debug);
            }
            
            var theLine = await LlmDialogueService.Instance.GenerateDialogueAsync(
                character, context, useStreaming ? onStreamingToken : null);

            ApplyEmbodiedActions(instance, context, theLine);

            // speak_in_bubble sentinel: 气泡模式下跳过对白肢体解析
            if (theLine != null)
            {
                if (context.RoutingFlags.IsActionRequested
                    || context.RoutingFlags.IsMovementRequested
                    || context.RoutingFlags.IsFollowing
                    || context.RoutingFlags.IsOnDate)
                {
                    EmbodiedActionParser.ParseAndExecute(
                        instance, 
                        theLine, 
                        latestPlayerInput, 
                        allowFallbackEmotes: !context.RoutingFlags.IsSimpleGreeting); 
                }
                else
                {
                    EmbodiedActionParser.ParseEmotesAndFaceOnly(
                        instance, 
                        theLine, 
                        allowFallbackEmotes: !context.RoutingFlags.IsSimpleGreeting); 
                }
            }

            if (context.RoutingFlags.HasStoodUpPending)
            {
                string npcName = instance.Name;
                StardewValley.DelayedAction.functionAfterDelay(() =>
                {
                    StoodUpTracker.Instance.ClearStoodUp(npcName);
                }, 3000);
            }

            if (theLine == null)
                return null;

            // ── 意图许可标签（Consent Tag）提取与清洗 ──
            // LLM 若同意赴约/跟随，会在台词末尾附带 [UI:DATE_INVITE] / [UI:FOLLOW]。
            // 此处剥除标签用于显示，并记录标记以决定后续是否追加确定性操作按键。
            bool allowDateUI = false;
            bool allowFollowUI = false;

            if (theLine != null && theLine.Length > 0)
            {
                for (int i = 0; i < theLine.Length; i++)
                {
                    if (theLine[i] == null) continue;
                    if (theLine[i].Contains("[UI:DATE_INVITE]")) { allowDateUI = true;  theLine[i] = theLine[i].Replace("[UI:DATE_INVITE]", "").Trim(); }
                    if (theLine[i].Contains("[UI:FOLLOW]"))      { allowFollowUI = true; theLine[i] = theLine[i].Replace("[UI:FOLLOW]", "").Trim(); }
                }
            }

            string formattedLine = FormatLine(theLine, allowDateUI, allowFollowUI);
            return $"{(dontSkipNext ? "" : "skip#")}{formattedLine}";
        }

        internal async Task<Dialogue> GenerateGift(NPC instance, StardewValley.Object gift, int taste, Action<string> onStreamingToken = null)
        {
            var character = GetCharacter(instance);
            DialogueContext context = GetBasicContext(instance);

            // ── 🔑 [TURN TRACKING] 送礼是独立事件，标记为新开场 ──
            context.IsActiveTurn = false;
            context.DialogueSessionId = $"{instance.Name}_Gift_{Game1.Date?.TotalDays ?? 0}_{DateTime.UtcNow.Ticks}";

            // Gift reactions are independent events — clear old chat history to avoid
            // unrelated prior dialogue polluting the prompt, but keep a synthetic
            // "player gave gift" line so downstream SessionCache/history retains
            // the causal link for follow-up turns (e.g. "how did it taste?").
            bool isZh = LocalizedContentManager.CurrentLanguageCode
                .ToString().StartsWith("zh", StringComparison.OrdinalIgnoreCase);

            string giftName = gift.DisplayName ?? gift.Name ?? "礼物";
            string giftLine = isZh
                ? $"[农夫刚刚送给你一件礼物：{giftName}]"
                : $"[The farmer just gave you a gift: {giftName}]";

            context.ChatHistory = new List<ConversationElement>
            {
                new ConversationElement(giftLine, true)
                {
                    FuzzyTime = isZh ? "刚刚" : "Just now"
                }
            };

            context.Accept = gift;
            context.GiftTaste = taste;

            // Set birthday flag so Prompts.GetGift can render the birthday-specific branch.
            var npcData = instance.GetData();
            context.Birthday = npcData != null
                && Game1.dayOfMonth == npcData.BirthDay
                && Game1.season.ToString().Equals(
                    npcData.BirthSeason.ToString(),
                    StringComparison.OrdinalIgnoreCase);

            // Force full prompt path for gift reactions — disable SimpleGreeting fast-path
            // to ensure InjectPendingTopic runs and the gift is addressed in the response.
            context.RoutingFlags = new ContextFlags
            {
                IsSimpleGreeting        = false,
                IncludeMemories         = true,
                IncludeEnvironment      = true,
                IncludeFarmDetails      = true,
                IncludeShortTermContext = false,
                IncludeSafetyRules      = true,
            };

            SetContext(instance.Name, context);
            var theLine = await LlmDialogueService.Instance.GenerateDialogueAsync(character, context, onStreamingToken);

            if (theLine == null)
                return null;

            EmbodiedActionParser.ParseEmotesAndFaceOnly(
                instance, 
                theLine, 
                allowFallbackEmotes: !context.RoutingFlags.IsSimpleGreeting);
            
            string formattedLine = FormatLine(theLine);
            var newDialogue = new Dialogue(instance, $"Accept_{gift.Name}", formattedLine);
            return newDialogue;
        }

        internal async Task<Dialogue> Generate(NPC instance, string dialogueKey, string originalLine = "", Action<string> onStreamingToken = null)
        {
            if (Game1.fadeToBlack || Game1.eventUp || !Game1.hasLoadedGame)
            {
                return new Dialogue(instance, dialogueKey, originalLine ?? "...");
            }
            var character = GetCharacter(instance);
            DialogueContext context = GetBasicContext(instance);

            // ── 🔑 [TURN TRACKING] 新开场对话，标记为 Turn 0 ──
            context.IsActiveTurn = false;

            var splitKey = dialogueKey.Split('_');
            var firstElement = splitKey.Any() ? splitKey[0] : "";
            if (Enum.TryParse<RandomAction>(firstElement, true, out var randomAction))
            {
                context.RandomAct = randomAction;
            }
            if (Enum.TryParse<SpouseAction>(firstElement, true, out var spouseAction))
            {
                context.SpouseAct = spouseAction;
            }
            context.CanGiveGift = string.IsNullOrWhiteSpace(originalLine);
            
            // 🌟【核心修复】：传入真实的历史记录 context.ChatHistory，而非硬编码的 null！
            // 彻底解决路由器误判为 IsSimpleGreeting 导致的循环复读开场白
            context.RoutingFlags = ContextRouter.Evaluate(instance, string.Empty, ModEntry.Config.RomanceSafetyMode, context.ChatHistory);

            SetContext(instance.Name, context);
            context.ScheduleLine = originalLine;
            var theLine = await LlmDialogueService.Instance.GenerateDialogueAsync(character, context, onStreamingToken);
            
            if (theLine == null)
                return null;

            ApplyEmbodiedActions(instance, context, theLine);

            EmbodiedActionParser.ParseEmotesAndFaceOnly(
                instance, 
                theLine, 
                allowFallbackEmotes: !context.RoutingFlags.IsSimpleGreeting);

            string formattedLine = FormatLine(theLine);
            return new Dialogue(instance, dialogueKey, formattedLine);
        }
        
        private void ApplyEmbodiedActions(NPC instance, DialogueContext context, string[] theLine)
        {
            if (theLine == null || theLine.Length == 0) return;
            if (!context.RoutingFlags.IsMovementRequested) return;
            if (context.RoutingFlags.IsPathBlocked) return;
        }

        private string FormatLine(string[] theLine, bool allowDateUI = false, bool allowFollowUI = false)
        {
            if (theLine == null || theLine.Length == 0)
            {
                return "...";
            }

            // 🔧 防御性全行标签清洗：防止 LLM 偶发在行中输出 [UI:*] 标签污染对话按键。
            for (int i = 0; i < theLine.Length; i++)
            {
                theLine[i] = System.Text.RegularExpressions.Regex.Replace(
                    theLine[i],
                    @"\[UI:[^\]]+\]",
                    "",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase).Trim();
            }

            // 🌟【强力防抽风 1】：NPC 台词分页与超长字符限制
            string npcSpeech = theLine[0];
            if (string.IsNullOrWhiteSpace(npcSpeech))
            {
                npcSpeech = "...";
            }
            else
            {
                // 1. 限制通过 '#' 分割的显式翻页数量（最多允许 3 个 '#' 即最多 4 页台词）
                var pages = npcSpeech.Split(new[] { '#' }, StringSplitOptions.RemoveEmptyEntries);
                const int maxPages = 4;
                if (pages.Length > maxPages)
                {
                    npcSpeech = string.Join("#", pages.Take(maxPages)) + "...";
                }
                // 2. 限制单段文字的总字符上限（防止单段成千上万字撑死渲染）
                const int maxTotalChars = 600;
                if (npcSpeech.Length > maxTotalChars)
                {
                    npcSpeech = npcSpeech.Substring(0, maxTotalChars).TrimEnd() + "...";
                }
            }
            theLine[0] = npcSpeech;

            if (theLine.Length == 1 && ModEntry.Config.TypedResponses != "Always" && !allowDateUI && !allowFollowUI)
            {
                return theLine[0];
            }
            var sb = new StringBuilder();
            sb.Append(theLine[0]);
            var index = Interlocked.Increment(ref responseIndex);
            if (index > 29999) { Interlocked.Exchange(ref responseIndex, 20000); index = 20000; }
            sb.Append($"#$q {index} {SldConstants.DialogueKeyPrefix}Default#{Util.GetString("outputRespond")}");
            sb.Append($"#$r -999999 0 {SldConstants.DialogueKeyPrefix}Silent#{Util.GetString("outputStaySilent")}");

            // 🌟【强力防抽风 2】：限制快捷建议选项数量，最多只展示前 3 个，避免选项填满甚至超出屏幕
            int maxSuggestions = Math.Min(theLine.Length, 4); // 取 1 到 3
            for (int i = 1; i < maxSuggestions; i++)
            {
                if (string.IsNullOrWhiteSpace(theLine[i])) continue;
                sb.Append($"#$r -999998 0 {SldConstants.DialogueKeyPrefix}Next#");
                sb.Append(theLine[i]);
            }
            if (ModEntry.Config.TypedResponses != "Never")
            {
                sb.Append($"#$r -999997 0 {SldConstants.DialogueKeyPrefix}TypedResponse#{Util.GetString("uiTypeYourResponse")}");
            }

            bool isZh = LocalizedContentManager.CurrentLanguageCode == LocalizedContentManager.LanguageCode.zh;

            // 根据意图许可标记，动态追加确定性操作按键
            if (allowDateUI && DateManager.Instance.Phase == DatePhase.None)
            {
                string dateBtn = isZh ? "【敲定约会地点...】" : "【Choose Date Location...】";
                sb.Append($"#$r -999994 0 {SldConstants.DialogueKeyPrefix}ActionOpenDateMenu#{dateBtn}");
            }

            if (allowFollowUI && MovementManager.Instance != null && !MovementManager.Instance.HasActiveFollow)
            {
                string followBtn = isZh ? "【好的，跟上我吧】" : "【Come with me then】";
                sb.Append($"#$r -999995 0 {SldConstants.DialogueKeyPrefix}ActionConfirmFollow#{followBtn}");
            }

            return sb.ToString();
        }

        /// <summary>
        /// 尝试为当前上下文启动跟随（约会跟随或普通跟随）。
        /// </summary>
        private static bool TryStartFollowForContext(NPC npc)
        {
            var movement = MovementManager.Instance;
            if (movement == null)
            {
                ModEntry.SMonitor?.Log("[DialogueBuilder] Follow failed: MovementManager null", LogLevel.Warn);
                return false;
            }

            // 【新增守卫】待排期约会防杀
            if (DateManager.Instance != null
                && DateManager.Instance.Phase == DatePhase.Pending
                && DateManager.Instance.CurrentDateMode == DateManager.DateMode.Scheduled
                && string.Equals(DateManager.Instance.ActiveDateNpcName, npc.Name, StringComparison.OrdinalIgnoreCase))
            {
                bool isZh = LocalizedContentManager.CurrentLanguageCode == LocalizedContentManager.LanguageCode.zh;
                Game1.addHUDMessage(new HUDMessage(isZh ? $"今晚已和 {npc.displayName} 有约。" : $"You already have plans with {npc.displayName} tonight.", 3));
                ModEntry.SMonitor?.Log($"[DialogueBuilder] Regular follow refused: pending scheduled date for {npc.Name}", LogLevel.Info);
                return false;
            }

            bool isDateFollow = ModEntry.Config.EnableDateSystem && DateManager.Instance != null && DateManager.Instance.IsOnDate(npc.Name);
            if (isDateFollow)
            {
                bool ok = DateManager.Instance.TryStartFollow(npc);
                if (!ok)
                {
                    ModEntry.SMonitor?.Log($"[DialogueBuilder] Date follow refused by DateManager: {npc.Name}", LogLevel.Warn);
                    return false;
                }
            }
            else
            {
                // 普通跟随（时长 120 游戏分钟 → 换算为截止时刻，封顶 2600）
                int rawEnd = Utility.ModifyTime(Game1.timeOfDay, 120);
                int endTime = Math.Min(rawEnd, 2600);
                MovementManager.Instance.StartRegularFollow(npc, endTime);
                ModEntry.SMonitor?.Log($"[DialogueBuilder] Regular follow started: {npc.Name}, endTime={endTime}", LogLevel.Info);
            }

            // 启动成功后（date/regular 两路径共用）
            try { npc.doEmote(20); } catch (Exception ex) { ModEntry.SMonitor?.Log($"[DialogueBuilder] Emote failed: {ex.Message}", LogLevel.Warn); }
            try { Game1.playSound("dwop"); } catch (Exception ex) { ModEntry.SMonitor?.Log($"[DialogueBuilder] Sound failed: {ex.Message}", LogLevel.Warn); }
            bool isZhFollow = LocalizedContentManager.CurrentLanguageCode == LocalizedContentManager.LanguageCode.zh;
            Game1.addHUDMessage(new HUDMessage(isZhFollow ? $"{npc.displayName} 开始跟着你了" : $"{npc.displayName} is now following you", 3));
            return true;
        }

        public static bool HandleSpecialActionResponse(string responseKey, NPC npc)
        {
            if (string.IsNullOrEmpty(responseKey) || npc == null) return false;

            if (responseKey.EndsWith("ActionOpenDateMenu", StringComparison.OrdinalIgnoreCase))
            {
                Game1.dialogueUp = false;
                Game1.activeClickableMenu = null;
                Game1.player.forceCanMove();

                Game1.activeClickableMenu = new DateLocationPickerMenu(npc);
                return true;
            }

            if (responseKey.EndsWith("ActionConfirmFollow", StringComparison.OrdinalIgnoreCase))
            {
                Game1.dialogueUp = false;
                Game1.activeClickableMenu = null;
                Game1.player.forceCanMove();

                TryStartFollowForContext(npc);
                return true;
            }

            return false;
        }

        private static string CleanHistoryText(string raw)
        {
            return DialogueHistoryManager.SanitizeForStorage(raw);
        }

        private DialogueContext GetBasicContext(NPC instance)
        {
            var farmer = Game1.getPlayerOrEventFarmer();
            Season season;
            switch (Game1.currentSeason)
            {
                case "spring":
                    season = Season.Spring;
                    break;
                case "summer":
                    season = Season.Summer;
                    break;
                case "fall":
                    season = Season.Fall;
                    break;
                case "winter":
                    season = Season.Winter;
                    break;
                default:
                    throw new Exception("Invalid season");
            }
            string timeOfDay;
            switch (Game1.timeOfDay)
            {
                case <= 800:
                    timeOfDay = Util.GetString("generalEarlyMorning");
                    break;
                case <= 1130:
                    timeOfDay = Util.GetString("generalLateMorning");
                    break;
                case <= 1400:
                    timeOfDay = Util.GetString("generalMidday");
                    break;
                case <= 1700:
                    timeOfDay = Util.GetString("generalAfternoon");
                    break;
                case <= 2200:
                    timeOfDay = Util.GetString("generalEvening");
                    break;
                default:
                    timeOfDay = Util.GetString("generalLateNight");
                    break;
            }
            timeOfDay += $" ({(Game1.timeOfDay / 100) % 24}:{Game1.timeOfDay % 100:00})";
            Weekday day;
            switch (Game1.dayOfMonth % 7)
            {
                case 0:
                    day = Weekday.Sun;
                    break;
                case 1:
                    day = Weekday.Mon;
                    break;
                case 2:
                    day = Weekday.Tue;
                    break;
                case 3:
                    day = Weekday.Wed;
                    break;
                case 4:
                    day = Weekday.Thu;
                    break;
                case 5:
                    day = Weekday.Fri;
                    break;
                case 6:
                    day = Weekday.Sat;
                    break;
                default:
                    throw new Exception("Invalid day");
            }
            var children = ConvertChildren(farmer.getChildren());
            var weather = new List<string>();
            if (Game1.IsGreenRainingHere()) weather.Add("greenrain");
            else if (Game1.IsLightningHere()) weather.Add("storm");
            else if (Game1.IsRainingHere()) weather.Add("rain");
            else if (Game1.IsSnowingHere()) weather.Add("snow");
            else if (Game1.isDebrisWeather) weather.Add("wind");
            
            if (weather.Count == 0)
            {
                weather.Add("sun");
            }
            
            var hearts = farmer.friendshipData.ContainsKey(instance.Name) ? 
                    (
                        farmer.friendshipData[instance.Name].Points == 0 ? 
                                -1 : 
                                farmer.friendshipData[instance.Name].Points / 250
                    ) 
                    : -1;
            var context = new DialogueContext()
            {
                Season = season,
                DayOfSeason = Game1.dayOfMonth,
                TimeOfDay = timeOfDay,
                Hearts = hearts,
                Location = instance.currentLocation?.Name ?? "Unknown",
                Year = Game1.year,
                Day = day,
                MaleFarmer = farmer.IsMale,
                Inlaw = farmer.getSpouse()?.Name,
                Children = children,
                Married = farmer.getSpouse() != null,
                Spouse = farmer.getSpouse()?.Name,
                Weather = weather
            };

            int recentCount = ModEntry.Config.MemoryRecentCount > 0 ? ModEntry.Config.MemoryRecentCount : 20;
            var historyManager = DialogueHistoryManager.Instance;
            if (historyManager != null)
            {
                var historyEntries = historyManager.GetRecentHistory(instance.Name, recentCount);
                if (historyEntries.Count > 0)
                {
                    var timeNow = new StardewTime(Game1.Date, Game1.timeOfDay);
                    context.ChatHistory = historyEntries
                        .Where(e =>
                            e.DialogueType != "eavesdrop" && 
                            //e.DialogueType != "vanilla" &&   // ← 原版台词不进对话历史
                            e.DialogueType != "event" &&     // ← 剧情事件台词不进对话历史
                            e.DialogueType != "gift" &&      // ← 礼物系统条目不进对话历史
                            e.SpeakerType != SpeakerType.System)
                        .Select(e => new ConversationElement(CleanHistoryText(e.Text), e.SpeakerType == SpeakerType.Player)
                        {
                            FuzzyTime = DialogueHistoryAdapter.GetFuzzyTime(e.Timestamp, timeNow)
                        })
                        .Where(e => !string.IsNullOrWhiteSpace(e.Text) && 
                                    // ★ 剔除农夫历史中的无意义纯省略号占位
                                    !(e.IsPlayerLine && (e.Text.Trim() == "..." || e.Text.Trim() == "…" || e.Text.Trim() == "......")))
                        .ToList();
                }
            }

            return context;
        }

        private List<ChildDescription> ConvertChildren(List<Child> children)
        {
            if (children == null) return new List<ChildDescription>();
            return children.Select(c => new ChildDescription(c.Name, c.Gender == Gender.Male, c.Age)).ToList();
        }

        internal bool PatchNpc(NPC n,int probability=4,bool retainResult=false)
        {
            if (LlmDisabled || !ModEntry.Config.EnableMod || probability == 0)
            {
                return false;
            }
            if (ModEntry.Config.DisabledCharactersList.Contains(n.Name))
            {
                return false;
            }
            // 🌟【核心重构】：仅针对明确属于未授权内容包的自定义 NPC 进行独立拦截
            if (ModEntry.Config.RespectAuthorAiConsent && IsNpcFromBlockedPack(n))
            {
                return false;
            }
            if (probability < 4)
            {
                if (retainResult)
                {
                    if (_patchDate != Game1.Date.TotalDays || _patchCharacters == null)
                    {
                        _patchDate = Game1.Date.TotalDays;
                        _patchCharacters = new Dictionary<string, bool>();
                    }
                    if (_patchCharacters.ContainsKey(n.Name))
                    {
                        return _patchCharacters[n.Name];
                    }
                }
                if (probability == -1)
                {
                }
                else if (_random.Next(4) >= probability)
                {
                    if (retainResult)
                    {
                        _patchCharacters.Add(n.Name, false);
                    }
                    return false;
                }
                else if (retainResult)
                {
                    _patchCharacters.Add(n.Name, true);
                }
            }

            return true;
        }

        internal void ClearContext(string npcName)
        {
            if (!string.IsNullOrEmpty(npcName))
                _npcContexts.Remove(npcName);
        }

        /// <summary>
        /// Clears all NPC context caches at once.
        /// Called when the player shift-clicks the clear history button to clear everything.
        /// </summary>
        public void ClearAllContexts()
        {
            _npcContexts.Clear();
        }

        public void Cleanup()
        {
            try
            {
                _characters?.Clear();
                _characters = new Dictionary<string, Character>();
                _npcContexts.Clear();
                responseIndex = 20000;
                _patchCharacters?.Clear();
                _patchCharacters = null;
                _patchDate = 0;
                LlmDisabled = false;
                ModEntry.SMonitor?.Log("[DialogueBuilder] Cleaned up successfully.", LogLevel.Debug);
            }
            catch (Exception ex)
            {
                ModEntry.SMonitor?.Log($"[DialogueBuilder] Error during cleanup: {ex.Message}", LogLevel.Warn);
            }
        }

        /// <summary>
        /// 判定指定 NPC 是否归属于未声明 permitAiUse 的第三方内容包。
        /// </summary>
        private bool IsNpcFromBlockedPack(NPC n)
        {
            if (ModEntry.DisallowedContentPackIds == null || ModEntry.DisallowedContentPackIds.Count == 0)
            {
                return false;
            }

            if (_characters.Count == 0)
            {
                PopulateCharacters();
            }

            var character = GetCharacter(n);
            // 拥有有效本地设定/传记（原版角色、已内置适配角色）绝对放行
            if (character?.Bio != null && !character.Bio.Missing && !string.IsNullOrWhiteSpace(character.Bio.Biography))
            {
                return false;
            }

            // 针对无内置设定的纯第三方自定义 NPC：通过星露谷 1.6 的 characterData 匹配其资产归属
            var data = n.GetData();
            if (data != null)
            {
                string textureName = data.TextureName ?? string.Empty;

                foreach (var packId in ModEntry.DisallowedContentPackIds)
                {
                    if (!string.IsNullOrEmpty(textureName) && textureName.Contains(packId, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }

            // 检查 NPC 自身的 modData 键名是否包含未授权的包名
            if (n.modData != null && n.modData.Keys.Any())
            {
                foreach (var packId in ModEntry.DisallowedContentPackIds)
                {
                    if (n.modData.Keys.Any(k => k.Contains(packId, StringComparison.OrdinalIgnoreCase)))
                    {
                        return true;
                    }
                }
            }

            return false;
        }
    }
}

