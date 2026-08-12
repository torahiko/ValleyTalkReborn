using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Mime;
using System.Runtime;
using System.Threading;
using System.Text;
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
        
        internal async Task<string> GenerateResponse(NPC instance, List<ConversationElement> conversation, bool dontSkipNext = false)
        {
            var character = GetCharacter(instance);

            DialogueContext context = GetContext(instance.Name) ?? GetBasicContext(instance);

            // =========================================================================
            // 🌟【双重防线去重洗涤】防止文本微小差异绕过 HashSet，并干掉任何相邻重复行
            // =========================================================================

            // 1. 文本标准化函数：忽略空格、#$b# 标记、肖像符以及 @ / 玩家名差异
            string NormalizeText(string txt)
            {
                if (string.IsNullOrWhiteSpace(txt)) return string.Empty;
                string clean = System.Text.RegularExpressions.Regex.Replace(txt, @"[\$\#\s@]", "");
                if (StardewValley.Game1.player != null)
                    clean = clean.Replace(StardewValley.Game1.player.Name, "");
                return clean;
            }

            var fullHistory = context.ChatHistory.ToList();

            // 2. 使用标准化文本进行 HashSet 校验，防止因格式差异导致的二次追加
            var existingKeys = new HashSet<(string, bool)>(
                fullHistory.Select(e => (NormalizeText(e.Text), e.IsPlayerLine)));

            foreach (var elem in conversation)
            {
                if (existingKeys.Add((NormalizeText(elem.Text), elem.IsPlayerLine)))
                {
                    fullHistory.Add(elem);
                }
            }

            // 3. 防御性洗涤：相邻去重（如果连续两条都是同一个 Speaker 说的完全相同的内容，直接抹掉后一条）
            var cleanHistory = new List<ConversationElement>();
            foreach (var elem in fullHistory)
            {
                if (cleanHistory.Count > 0 &&
                    cleanHistory.Last().IsPlayerLine == elem.IsPlayerLine &&
                    NormalizeText(cleanHistory.Last().Text) == NormalizeText(elem.Text))
                {
                    // 匹配到连续重复台词，跳过追加
                    continue;
                }
                cleanHistory.Add(elem);
            }

            context.ChatHistory = cleanHistory;
            // =========================================================================
            
            // ==========================================
            // 【新增路由通电】提取玩家最新输入，瞬间完成意图与状态判定
            // ==========================================
            string latestPlayerInput = conversation.LastOrDefault(x => x.IsPlayerLine)?.Text ?? "";
            context.RoutingFlags = ContextRouter.Evaluate(instance, latestPlayerInput, ModEntry.Config.RomanceSafetyMode, context.ChatHistory);

            // 玩家主动发起对话，立即取消该 NPC 的后台 bark 请求（抢占规则）
            DynamicBarkManager.CancelBackgroundTasks(instance.Name);

            SetContext(instance.Name, context);
            var theLine = await LlmDialogueService.Instance.GenerateDialogueAsync(character, context);

            // ==========================================
            // 🌟 具身动作注入与解析 (降维打击 2.0)
            // ==========================================
            // 1. 强制注入缺失的移动标签（如果有移动请求且未被 LLM 处理）
            ApplyEmbodiedActions(instance, context, theLine);

            // 2. ★ 统一解析所有具身动作（包括表情、朝向、移动等）
            // 找到这部分代码：
            if (context.RoutingFlags.IsMovementRequested
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
               // 送礼场景不触发问候，直接保持或显式传入路由标志
                EmbodiedActionParser.ParseEmotesAndFaceOnly(
                    instance, 
                    theLine, 
                    allowFallbackEmotes: !context.RoutingFlags.IsSimpleGreeting); 
            }

            // Phase 4：放鸽子清算后，清除记仇缓存（仅在 Prompt 已注入该 Flag 时）
            if (context.RoutingFlags.HasStoodUpPending)
            {
                string npcName = instance.Name;
                // 延迟清除：确保这次对话完整播放后再清，防止多次触发
                StardewValley.DelayedAction.functionAfterDelay(() =>
                {
                    StoodUpTracker.Instance.ClearStoodUp(npcName);
                }, 3000); // 对话结束后 3 秒清除
            }

            string formattedLine = FormatLine(theLine);
            return $"{(dontSkipNext ? "" : "skip#")}{formattedLine}";
        }

        internal async Task<Dialogue> GenerateGift(NPC instance, StardewValley.Object gift, int taste)
        {
            var character = GetCharacter(instance);
            DialogueContext context = GetBasicContext(instance);
            context.Accept = gift;
            context.GiftTaste = taste;
            
            // ==========================================
            // 【新增路由通电】送礼属于物理动作，直接评估环境与状态
            // ==========================================
            context.RoutingFlags = ContextRouter.Evaluate(instance, "", ModEntry.Config.RomanceSafetyMode, null);

            SetContext(instance.Name, context);
            var theLine = await LlmDialogueService.Instance.GenerateDialogueAsync(character, context);

            // 送礼不涉及移动，只解析表情与朝向
            EmbodiedActionParser.ParseEmotesAndFaceOnly(
                instance, 
                theLine, 
                allowFallbackEmotes: !context.RoutingFlags.IsSimpleGreeting);
            
            string formattedLine = FormatLine(theLine);
            var newDialogue = new Dialogue(instance, $"Accept_{gift.Name}", formattedLine);
            return newDialogue;
        }

        internal async Task<Dialogue> Generate(NPC instance, string dialogueKey, string originalLine = "")
        {
            // 拦截：如果是游戏刚启动、正在读档或画面全黑，直接返回原版占位对话，不发网络请求
            if (Game1.fadeToBlack || Game1.eventUp || !Game1.hasLoadedGame)
            {
                return new Dialogue(instance, dialogueKey, originalLine ?? "...");
            }
            var character = GetCharacter(instance);
            DialogueContext context = GetBasicContext(instance);
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
            
            // ==========================================
            // 【新增路由通电】普通对话/系统事件触发时的状态评估
            // ==========================================
            // Pass empty string: originalLine is a game-internal key, not player input.
            context.RoutingFlags = ContextRouter.Evaluate(instance, string.Empty, ModEntry.Config.RomanceSafetyMode, null);

            SetContext(instance.Name, context);
            context.ScheduleLine = originalLine;
            var theLine = await LlmDialogueService.Instance.GenerateDialogueAsync(character, context);
            
            // ==========================================
            // 🌟 具身动作注入与解析 (降维打击 2.0)
            // ==========================================
            // 1. 强制注入缺失的移动标签（如果有移动请求且未被 LLM 处理）
            ApplyEmbodiedActions(instance, context, theLine);

            // 2. ★ 系统触发的对话不应产生移动，只解析表情与朝向
            EmbodiedActionParser.ParseEmotesAndFaceOnly(instance, theLine);

            string formattedLine = FormatLine(theLine);
            return new Dialogue(instance, dialogueKey, formattedLine);
        }
        
        /// <summary>
        /// 降维打击 2.0 逻辑封装：
        /// 当雷达检测到玩家发起动作指令且未撞墙时，若 LLM 未输出 [ACTION:] 且没有拒绝，C# 强行注入标签并挂起执行。
        /// tool calling 模式下同样注入兜底，防止 LLM 只输出文字而漏调工具的情况。
        /// 注意：此方法不再调用 EmbodiedActionParser.ParseAndExecute，仅负责强制注入标签。
        /// 解析工作由外层统一调用 EmbodiedActionParser.ParseAndExecute 完成。
        /// </summary>
        private void ApplyEmbodiedActions(NPC instance, DialogueContext context, string[] theLine)
        {
            if (theLine == null || theLine.Length == 0) return;
            if (!context.RoutingFlags.IsMovementRequested) return;
            if (context.RoutingFlags.IsPathBlocked) return;

            string firstLine = theLine[0];

            bool isRefusal = firstLine.Contains("不行")   || firstLine.Contains("才不")   ||
                             firstLine.Contains("我不要") || firstLine.Contains("没门")   ||
                             firstLine.Contains("算了吧") || firstLine.Contains("退不了") ||
                             firstLine.Contains("做不到") || firstLine.Contains("过不去") ||
                             firstLine.Contains("can't")  || firstLine.Contains("won't")  ||
                             firstLine.Contains("no way") || firstLine.Contains("i refuse");

            // In native tool-calling mode the action arrives via ToolCalls, not as an inline tag.
            // In fallback mode the LLM is prompted to output the tag itself.
            // Either way there is nothing to inject here.
            // 注意：这里不再调用 EmbodiedActionParser.ParseAndExecute
            // 解析由外层统一处理，确保表情、朝向、移动等所有标签都能被正确解析
        }

        private string FormatLine(string[] theLine)
        {
            if (theLine == null || theLine.Length == 0)
            {
                return string.Empty;
            }
            if (theLine.Length == 1 && ModEntry.Config.TypedResponses != "Always")
            {
                return theLine[0];
            }
            var sb = new StringBuilder();
            sb.Append(theLine[0]);
            var index = Interlocked.Increment(ref responseIndex);
            if (index > 29999) { Interlocked.Exchange(ref responseIndex, 20000); index = 20000; }
            sb.Append($"#$q {index} {SldConstants.DialogueKeyPrefix}Default#{Util.GetString("outputRespond")}");
            sb.Append($"#$r -999999 0 {SldConstants.DialogueKeyPrefix}Silent#{Util.GetString("outputStaySilent")}");

            for (int i = 1; i < theLine.Length; i++)
            {
                sb.Append($"#$r -999998 0 {SldConstants.DialogueKeyPrefix}Next#");
                sb.Append(theLine[i]);
            }
            if (ModEntry.Config.TypedResponses != "Never")
            {
                sb.Append($"#$r -999997 0 {SldConstants.DialogueKeyPrefix}TypedResponse#{Util.GetString("uiTypeYourResponse")}");
            }
            return sb.ToString();
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
            if (Game1.IsRainingHere()) weather.Add("rain");
            if (Game1.IsSnowingHere()) weather.Add("snow");
            if (Game1.IsLightningHere()) weather.Add("lightning");
            if (Game1.IsGreenRainingHere()) weather.Add("green rain");
            
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

            // 从 DialogueHistoryManager 加载该 NPC 的历史对话（含 vanilla 原生台词）
            // 过滤掉窃听/系统事件，只保留真正的对话内容；条数由 MemoryRecentCount 控制
            int recentCount = ModEntry.Config.MemoryRecentCount > 0 ? ModEntry.Config.MemoryRecentCount : 20;
            var historyEntries = DialogueHistoryManager.Instance.GetRecentHistory(instance.Name, recentCount);
            if (historyEntries.Count > 0)
            {
                context.ChatHistory = historyEntries
                    .Where(e => e.DialogueType != "eavesdrop" && e.SpeakerType != SpeakerType.System)
                    .Select(e => new ConversationElement(e.Text, e.SpeakerType == SpeakerType.Player))
                    .ToList();
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
            if (ModEntry.BlockModdedContent)
            {
                if (_characters.Count == 0)
                {
                    PopulateCharacters();
                }
                var character = GetCharacter(n);
                if (string.IsNullOrWhiteSpace(character?.Bio?.Biography ?? ""))
                {
                    return false;
                }
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
                    // To do - ask for interaction type
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
    }
}