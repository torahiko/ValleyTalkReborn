# 本会话 MCP 证据摘录

日期：2026-10-02；设计基线 HEAD：46fbbf5abbf66f8115a29d45ce15fcd38a0b3a34；分支：master。

以下是本会话 tools.mcp__rider__read_file 输出的选段，L 为读取时的源行号。它们证明现有接口；设计新增接口在 declarations 文档中单独标为尚未实现。
Rider analyze_calls 未解析出 C# AddEntry 的 callable；调用关系采用实际源代码读取证据，不把该工具失败当成调用关系证据。

## E01 — src/Config/ModConfig.cs

```text
L20:     public class ModConfig
L21:     {
L22:         // ── 老版本配置迁移备份字段（仅用于迁移，不对外暴露） ──
L23:         #pragma warning disable CS0414 // 字段经反射读取，非真正未使用
L24:         [Obsolete("Legacy field for migration from pre-1.7 versions. Will be removed in v1.8.0.")]
L25:         [Newtonsoft.Json.JsonProperty("_legacyApiKey")]
L26:         private string _legacyApiKey = null;
L27:
L28:         [Obsolete("Legacy field for migration from pre-1.7 versions. Will be removed in v1.8.0.")]
L29:         [Newtonsoft.Json.JsonProperty("_legacyServerAddress")]
L30:         private string _legacyServerAddress = null;
L31:
L32:         [Obsolete("Legacy field for migration from pre-1.7 versions. Will be removed in v1.8.0.")]
L33:         [Newtonsoft.Json.JsonProperty("_legacyModelName")]
L34:         private string _legacyModelName = null;
L35:         #pragma warning restore CS0414
L233:         /// <summary>
L234:         /// 时间线自动总结：每日 8 点判定，昨日互动 >=3 自动写日记。默认开启。
L235:         /// </summary>
L236:         public bool AutoSummarizeDaily { get; set; } = true;
L237:
L238:         /// <summary>
L239:         /// 时间线自动总结：每周一，近 7 天日记 >=3 自动浓缩周报。默认开启。
L240:         /// </summary>
L241:         public bool AutoSummarizeWeekly { get; set; } = true;
L242:
L243:         /// <summary>
L244:         /// 时间线自动总结：每季 1 日，上季周报 >=2 自动浓缩季报。默认开启。
L245:         /// </summary>
L246:         public bool AutoSummarizeSeason { get; set; } = true;
L247:
L248:         /// <summary>
L249:         /// 时间线自动总结：每年春 1，上年季报 >=2 自动浓缩年报。默认开启。
L250:         /// </summary>
L251:         public bool AutoSummarizeYearly { get; set; } = true;
L333:         /// <summary>
L334:         /// 校验并修正对话相关配置值到合法范围。
L335:         /// </summary>
L336:         public void ValidateDialogueConfig(IMonitor monitor)
L337:         {
L338:             LlmTimeoutSeconds = Clamp(LlmTimeoutSeconds, 5, 120);
L339:             LocalMaxConcurrentRequests = Clamp(LocalMaxConcurrentRequests, 1, 4);
L340:             BarkApiCooldownTicks = Clamp(BarkApiCooldownTicks, 30, 3600);
L341:             BarkQueueSize = Clamp(BarkQueueSize, 1, 20);
L342:             A2AMaxParticipants = Clamp(A2AMaxParticipants, 2, 4);
L343:             BarkDwellScans = Clamp(BarkDwellScans, 1, 10);
L344:             MicroSocialMidFriendshipChance = Math.Clamp(MicroSocialMidFriendshipChance, 0.0f, 1.0f);
L345:             PromptHistoryWindow = Clamp(PromptHistoryWindow, 1, 20);
L346:
```

## E02 — src/UI/GMCM/ModConfigMenu.cs

```text
L108:         internal static void Register(ModEntry modEntry)
L109:         {
L110:             _modEntry = modEntry;
L111:             ModManifest = modEntry.ModManifest;
L112:             ConfigMenu = GetConfigMenu(modEntry);
L113:
L114:             if (ConfigMenu == null)
L115:             {
L116:                 modEntry.Monitor.Log(GetUIString("configGmcmNotInstalled", "Generic Mod Config Menu not installed."),
L117:                     LogLevel.Warn);
L118:                 return;
L119:             }
L120:
L121:             if (!ModEntry.LlmMap.ContainsKey(ModEntry.Config.Provider))
L122:             {
L123:                 ModEntry.Config.Provider = "OpenAiCompatible";
L124:             }
L125:
L126:             ConfigMenu.Unregister(ModManifest);
L127:             ConfigMenu.Register(
L128:                 mod: ModManifest,
L129:                 reset: () => ModEntry.Config = new ModConfig(),
L130:                 save: () =>
L131:                 {
L132:                     modEntry.Helper.WriteConfig(ModEntry.Config);
L133:
L134:                     ModEntry.CleanupOnConfigToggle();
L135:
L136:                     Llm.RecreateHttpClient();
L137:
L138:                     // ★ 保存后即时生效：刷新第三方授权名单与 DialogueBuilder 配置引用
L139:                     ModEntry.CheckContentPacks();
L140:                     DialogueBuilder.Instance.Config = ModEntry.Config;
L499:             // ── 时间线自动总结 ──
L500:             ConfigMenu.AddSectionTitle(
L501:                 mod: ModManifest,
L502:                 text: () => GetUIString("configSectionAutoSummarize", "Timeline Auto-Summary")
L503:             );
L504:
L505:             ConfigMenu.AddBoolOption(
L506:                 mod: ModManifest,
L507:                 name: () => GetUIString("configAutoSummarizeDaily", "Auto-write Daily Diary"),
L508:                 tooltip: () => GetUIString("configAutoSummarizeDailyTooltip",
L509:                     "Every day at 8:00, if yesterday's interactions >= 3, automatically write a diary entry."),
L510:                 getValue: () => ModEntry.Config.AutoSummarizeDaily,
L511:                 setValue: value => ModEntry.Config.AutoSummarizeDaily = value
L512:             );
L513:
L514:             ConfigMenu.AddBoolOption(
L515:                 mod: ModManifest,
L516:                 name: () => GetUIString("configAutoSummarizeWeekly", "Auto-summarize Weekly Report"),
L517:                 tooltip: () => GetUIString("configAutoSummarizeWeeklyTooltip",
L518:                     "Every Monday, if the past 7 days have >= 3 diary entries, automatically condense into a weekly report."),
L519:                 getValue: () => ModEntry.Config.AutoSummarizeWeekly,
L520:                 setValue: value => ModEntry.Config.AutoSummarizeWeekly = value
```

## E03 — src/Config/IGenericModConfigMenuApi.cs

```text
L55:         void AddBoolOption(IManifest mod, Func<bool> getValue, Action<bool> setValue, Func<string> name, Func<string> tooltip = null, string fieldId = null);
L68:         void AddNumberOption(IManifest mod, Func<int> getValue, Action<int> setValue, Func<string> name, Func<string> tooltip = null, int? min = null, int? max = null, int? interval = null, Func<int, string> formatValue = null, string fieldId = null);
L92:         void AddTextOption(IManifest mod, Func<string> getValue, Action<string> setValue, Func<string> name, Func<string> tooltip = null, string[] allowedValues = null, Func<string, string> formatAllowedValue = null, string fieldId = null);
```

## E04 — src/Core/ModEntry.cs

```text
L338:            Config = Helper.ReadConfig<ModConfig>();
L370:                 Config.ProviderProfiles[Config.Provider] = currentProfile;
L371:                 Helper.WriteConfig(Config);
L372:
L373:                 // ★ 原汁原味的完整 Log，完全保留！
L374:                 Monitor.Log($"[ModEntry] 已迁移 Provider={Config.Provider} 的配置（ApiKey={!string.IsNullOrEmpty(currentProfile.ApiKey)}, ModelName={currentProfile.ModelName}）", LogLevel.Info);
L375:             }
L376:
L377:             // ── 高级参数边界校验（每次启动都执行） ──
L378:             Config.ValidateDialogueConfig(Monitor);
L424:             Config = Helper.ReadConfig<ModConfig>();
L425:             Config.ValidateDialogueConfig(Monitor);
L471:         public static void OnConfigChanged()
L472:         {
L473:             Config = SHelper.ReadConfig<ModConfig>();
L474:
L475:             DialogueBuilder.Instance.Config = Config;
L476:
L477:             if (!Config.EnableMod)
L1206:                 try
L1207:                 {
L1208:                     TimelineAutoSummaryScheduler.Instance?.Cleanup();
L1209:                 }
L1210:                 catch (Exception ex)
L1211:                 {
L1212:                     Log.Error($"[ValleyTalkReborn] Error cleaning TimelineAutoSummaryScheduler: {ex.Message}");
L1213:                 }
L1530:         private void OnMenuChanged(object sender, MenuChangedEventArgs e)
L1531:         {
L1532:             if (!Config.EnableMod) return;
L1533:
L1534:             if (e.OldMenu is StardewValley.Menus.DialogueBox oldDb && e.NewMenu == null)
L1535:             {
L1536:                 var speaker = oldDb.characterDialogue?.speaker ?? Game1.currentSpeaker;
L1537:                 if (speaker != null)
L1538:                 {
L1539:                     LastSpokenNPC = speaker;
L1540:                 }
L1541:
L1542:                 // VT-UI-002：关框瞬间消费待挂载跟随（pending 取出即清空，最多一次机会）
L1543:                 if (speaker != null
L1544:                     && !MovementManager.Instance.HasActiveFollow
L1545:                     && MovementManager.Instance.TryConsumePendingFollow(speaker.Name))
L1546:                 {
L1547:                     DialogueBuilder.TryStartFollowForContext(speaker);
L1548:                 }
L1549:
L1550:                 // 修复：对话框关闭后清除 currentSpeaker，防止原版引擎误判为可对话
L1551:                 if (Game1.currentSpeaker != null)
L1552:                 {
L1553:                     Game1.currentSpeaker = null;
L1554:                 }
L1555:
L1556:                 // VT3-B：对话框关闭 → 退役 active Tier 1 会话到 closed 软继承窗口
L1557:                 Tier1SnapshotStore.MarkCurrentDialogueClosed();
L1558:
L1559:                 // VT-UI-005 Stage 2：Custom 风格换壳——原版框关闭同帧弹出浮动选择框。
L1560:                 // BoxRef 不匹配（事件打断/陈旧载荷）时 ConsumeMatching 内部作废，不弹面板。
L1561:                 if (Config.ChoiceBoxStyle == ChoiceBoxStyle.Custom && PendingChoiceStore.TryPeek(out _))
L1562:                 {
L1563:                     var choiceContext = PendingChoiceStore.ConsumeMatching(e.OldMenu);
L1564:                     if (choiceContext != null)
L1565:                     {
L1566:                         choiceContext.Speaker.Halt();
L1567:                         choiceContext.Speaker.movementPause = 20;
L1568:                         choiceContext.Speaker.facePlayer(Game1.player);
L1569:                         Game1.activeClickableMenu = new DialogueChoiceMenu(choiceContext);
L1570:                     }
L1571:                 }
L1572:             }
L1608:             DialogueHistoryManager.Instance.Load();
L1643:         private void OnReturnedToTitle(object sender, ReturnedToTitleEventArgs e)
L1644:         {
L1645:             // VT-FARM-CACHE-04: 先于 Cleanup 作废农场摘要缓存，避免清理链异常时遗留前存档文本。
L1646:             FarmStateScanner.InvalidateCache();
L1647:             SMonitor.Log("[FarmStateScanner] Farm summary cache invalidated on ReturnedToTitle.", LogLevel.Debug);
L1648:
L1649:             Cleanup();
L1650:
L1651:             // VT-UI-005 Stage 2：标题界面强制作废待消费选择载荷
L1652:             PendingChoiceStore.Clear();
L1653:             // ── 情绪系统瞬态重置（返回标题） ──
L1654:             DialogueBuilder.Instance.ResetTransientEmotionState();
L1655:             SMonitor.Log("[ModEntry] Returned to title screen — all manager caches cleaned up.", LogLevel.Debug);
L1662:         private void OnUpdateTicked(object sender, UpdateTickedEventArgs e)
L1663:         {
L1664:             MainThreadActionQueue.ProcessMainThreadQueue();
```

## E05 — src/Models/history/DialogueHistoryManager.cs

```text
L179:         public void RecordSessionEnd(string npcName)
L180:         {
L181:             string marker = Util.GetString("sessionEndMarker");
L182:             if (string.IsNullOrWhiteSpace(marker))
L183:             {
L184:                 ModEntry.SMonitor?.Log(
L185:                     "[DialogueHistoryManager] sessionEndMarker i18n key missing - session-end not recorded.",
L186:                     LogLevel.Trace);
L187:                 return;
L188:             }
L189:             AddEntry(npcName, new DialogueHistoryEntry("System", marker, SpeakerType.System, "session-end"));
L190:         }
L222:         public List<DialogueHistoryEntry> GetHistory(string npcName)
L223:         {
L224:             lock (_historyLock)
L225:             {
L226:                 if (_history.TryGetValue(npcName, out var entries))
L227:                     return entries.ToList();
L228:                 return new List<DialogueHistoryEntry>();
L229:             }
L230:         }
L231:
L232:         public List<string> GetFormattedHistory(string npcName)
L233:         {
L234:             var entries = GetHistory(npcName);
L235:             return entries.Select(e => e.Format(npcName)).ToList();
L236:         }
L237:
L238:         public List<DialogueHistoryEntry> GetRecentHistory(string npcName, int count)
L239:         {
L240:             var entries = GetHistory(npcName);
L241:             return entries.TakeLast(Math.Min(count, entries.Count)).ToList();
L300:         /// <summary>
L301:         /// 该 NPC 今日是否已开口（NPC 口径）：仅统计 SpeakerType.NPC 且非 eavesdrop/gift 的当日条目。
L302:         /// 玩家台词在生成前落库（TextInputHandler / Dialogue_ChoiceResponse / Event_AnswerDialogue），
L303:         /// 刻意不计入——否则"玩家先开口的当日首次对话"会错过 narration。
L304:         /// </summary>
L305:         public int GetTodayTurnCount(string npcName)
L306:         {
L307:             lock (_historyLock)
L308:             {
L309:                 if (string.IsNullOrWhiteSpace(npcName) || !_history.TryGetValue(npcName, out var list))
L310:                     return 0;
L311:
L312:                 int curYear = Game1.Date.Year;
L313:                 var curSeason = (Season)Game1.Date.Season;
L314:                 int curDay = Game1.Date.DayOfMonth;
L315:
L316:                 return list.Count(e => e.SpeakerType == SpeakerType.NPC
L317:                                        && e.DialogueType != "eavesdrop"
L318:                                        && e.DialogueType != "gift"
L319:                                        && e.Timestamp.Year == curYear
L320:                                        && e.Timestamp.Season == curSeason
L321:                                        && e.Timestamp.DayOfMonth == curDay);
L322:             }
L323:         }
L382:         private void AddEntry(string npcName, DialogueHistoryEntry entry)
L383:         {
L384:             lock (_historyLock)
L385:             {
L386:                 if (!_history.TryGetValue(npcName, out var list))
L387:                 {
L388:                     list = new List<DialogueHistoryEntry>();
L389:                     _history[npcName] = list;
L390:                 }
L391:
L392:                 if (_lastEntry.TryGetValue(npcName, out var last))
L393:                 {
L394:                     if (entry.IsDuplicateOf(last))
L395:                     {
L396:                         return;
L397:                     }
L398:                 }
L399:
L400:                 list.Add(entry);
L401:                 _lastEntry[npcName] = entry;
L402:                 _lastActiveNpc = npcName;
L403:
L404:                 if (list.Count > MaxEntriesPerNpc)
L405:                 {
L406:                     list.RemoveRange(0, list.Count - MaxEntriesPerNpc);
L407:                 }
L408:             }
L409:
L410:             TryTriggerCompression(npcName);
L637:         public static SerializableEntry FromEntry(DialogueHistoryEntry entry)
L638:         {
L639:             return new SerializableEntry
L640:             {
L641:                 Id = entry.Id,
L642:                 SpeakerName = entry.SpeakerName,
L643:                 Text = entry.Text,
L644:                 SpeakerType = entry.SpeakerType,
L645:                 DialogueType = entry.DialogueType,
L646:                 Year = entry.Timestamp.Year,
L647:                 Season = entry.Timestamp.Season,
L648:                 Day = entry.Timestamp.DayOfMonth,
L649:                 TimeOfDay = entry.Timestamp.TimeOfDay,
L650:                 UtcTimestampMs = entry.UtcTimestampMs,
L651:                 GiftName = entry.GiftName,
L652:                 GiftTaste = entry.GiftTaste
L653:             };
L654:         }
L655:
L656:         public DialogueHistoryEntry ToEntry()
L657:         {
L658:             var time = new StardewTime(Year, Season, Day, TimeOfDay);
L659:             var entry = new DialogueHistoryEntry(SpeakerName, Text, SpeakerType, time, DialogueType)
L660:             {
L661:                 GiftName = GiftName,
L662:                 GiftTaste = GiftTaste,
L663:                 UtcTimestampMs = UtcTimestampMs
L664:             };
L665:             return entry;
```

## E06 — src/Models/history/DialogueHistoryEntry.cs

```text
L24:         [JsonConstructor]
L25:         public DialogueHistoryEntry()
L26:         {
L27:             Id = Guid.NewGuid();
L28:         }
L29:
L30:         public DialogueHistoryEntry(string speakerName, string text, SpeakerType speakerType, string dialogueType = "")
L31:             : this()
L32:         {
L33:             SpeakerName = speakerName;
L34:             Text = text;
L35:             SpeakerType = speakerType;
L36:             DialogueType = dialogueType;
L37:
L38:             // 仅在世界就绪时安全获取当前游戏时间
L39:             if (Context.IsWorldReady)
L40:             {
L41:                 Timestamp = new StardewTime(Game1.year, (Season)Game1.season, Game1.dayOfMonth, Game1.timeOfDay);
L42:             }
L54:         public Guid Id { get; }
L55:
L56:         /// <summary>
L57:         /// Who spoke this line (NPC name, "Player", or "System")
L58:         /// </summary>
L59:         public string SpeakerName { get; set; } = "";
L60:
L61:         /// <summary>
L62:         /// The dialogue text
L63:         /// </summary>
L64:         public string Text { get; set; } = "";
L65:
L66:         /// <summary>
L67:         /// Whether this was spoken by the NPC, Player, or System
L68:         /// </summary>
L69:         public SpeakerType SpeakerType { get; set; }
L70:
L71:         /// <summary>
L72:         /// Category: "dialogue", "conversation", "gift", "event", "marriage", "eavesdrop"
L73:         /// </summary>
L74:         public string DialogueType { get; set; } = "";
L75:
L76:         /// <summary>
L77:         /// When this was recorded (game time)
L78:         /// </summary>
L79:         public StardewTime Timestamp { get; set; }
L80:
L81:         /// <summary>
L82:         /// Physical write-order timestamp (UTC ms). Stable tiebreaker when multiple
L83:         /// NPCs share the same in-game timeOfDay, eliminating HashSet iteration drift.
L84:         /// </summary>
L85:         public long UtcTimestampMs { get; set; } = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
```

## E07 — src/Memory/MemoryManager.cs

```text
L32: public class MemoryEntry
L33: {
L34:     public string Id { get; set; } = Guid.NewGuid().ToString();
L35:     public string NpcName { get; set; } = "";
L36:     public string Content { get; set; } = "";
L37:     public DateTime CreatedAt { get; set; } = DateTime.Now;
L38:     public string Source { get; set; } = "Manual";
L39:     public MemoryCategory Category { get; set; } = MemoryCategory.Behavior;
L40:
L41:     // ── 1.6 分级记忆扩展（旧字段全保留，反序列化兼容）──
L42:     public MemoryType Type { get; set; } = MemoryType.Fact;
L43:     public int Importance { get; set; } = 3;          // 写入时 clamp 到 1..5
L44:     public int CreatedDay { get; set; }               // = Game1.Date.TotalDays；0 表示"未知（旧数据）"
L45:     public int ExpireDay { get; set; } = -1;          // -1 永久；Promise 默认 CreatedDay+7
L46:     public string TriggerLocation { get; set; } = "";
L47:     public string TargetDayHint { get; set; } = "";   // Promise 专用：LLM 原文如 "Weekend"
L48:     public bool IsFulfilled { get; set; } = false;    // Promise 专用
L49:     public int LastPromptedDay { get; set; } = -1;    // 保留字段：旧档兼容；CORE-MEM-102 分层重写后不再读写
L50:     public DateTime ArchivedAt { get; set; } = default; // 归档时刻；default=从未归档（CORE-MEM-101）
L51:
L52:     // ── Timeline 分层（FEAT-MEM-300-T1）──
L53:     public MemoryTier Tier { get; set; } = MemoryTier.Daily;
L54:     public string DateLabel { get; set; } = "";   // 游戏内日历戳，如 "[Y1 春 7日]"，以入库时所在页面日期为准
L55:     public string ArchiveReason { get; set; } = ""; // 归档原因："Distilled" | "ManualDeleted" | ""（未归档）
L56:
L57:     // ── RULE-MERGE：规则统一存储（追加字段，旧字段不变）──
L58:     public bool AutoArchive { get; set; } = true;   // 过期时是否自动入归档箱（规则默认 true）
L59: }
L61: public enum MemoryOperationResult
L62: {
L63:     Success,
L64:     Duplicate,
L65:     TooLong,
L66:     CapacityFull,
L67:     NotFound
L68: }
L69:
L70: internal class MemoryManager : IMemoryProvider
L71: {
L72:     public static readonly MemoryManager Instance = new MemoryManager();
L73:
L74:     private const string SaveDataKey         = "valleytalk.npc-memories";
L75:     private const string CallsignSaveDataKey = "valleytalk.npc-callsigns";
L76:     private const string ArchiveSaveDataKey = "valleytalk.npc-archived-memories";
L77:     private const string TimelineArchiveSaveDataKey = "valleytalk.npc-timeline-archived-memories";
L78:     private const string CategoryMigrationFlagKey = "valleytalk.memory-category-migrated";
L79:     private const string TimelineSaveDataKey = "valleytalk.npc-timeline-memories";
L90:     public const int MaxDailyTimelineMemories = 30;
L91:     public const int MaxWeeklyTimelineMemories = 10;
L92:     public const int MaxChronicleTimelineMemories = 10;
L93:     public const int MaxYearlyTimelineMemories = 5;
L94:     public const int MaxArchivedTimelineMemoriesPerNpc = 30; // 时间线归档箱滚动上限（FEAT-AUTO-T6）
L98:     private Dictionary<string, List<MemoryEntry>> _memories = new();
L99:     private Dictionary<string, string> _customCallsigns = new(StringComparer.OrdinalIgnoreCase);
L100:     private Dictionary<string, List<MemoryEntry>> _archivedMemories = new(StringComparer.OrdinalIgnoreCase);
L101:     private Dictionary<string, List<MemoryEntry>> _archivedTimelineMemories = new(StringComparer.OrdinalIgnoreCase);
L102:     private Dictionary<string, List<MemoryEntry>> _timelineMemories = new(StringComparer.OrdinalIgnoreCase);
L103:     private bool _isLoaded = false;
L104:     private bool _loadFailed = false; // 加载失败时拒绝覆写 SaveData
L105:
L106:     private static bool IsChineseLanguage =>
L107:         LocalizedContentManager.CurrentLanguageCode == LocalizedContentManager.LanguageCode.zh;
L108:
L109:     private MemoryManager() { }
L110:
L111:     public void Initialize(IModHelper helper)
L112:     {
L113:         helper.Events.GameLoop.SaveLoaded -= OnSaveLoaded;
L114:         helper.Events.GameLoop.DayStarted -= OnDayStarted;
L115:         helper.Events.GameLoop.SaveLoaded += OnSaveLoaded;
L116:         helper.Events.GameLoop.DayStarted += OnDayStarted;
L117:     }
L238:
L239:             // ── Timeline 读取（FEAT-MEM-300-T1，key 不存在时静默回退空字典）──
L240:             var loadedTimeline = ModEntry.SHelper.Data.ReadSaveData<Dictionary<string, List<MemoryEntry>>>(TimelineSaveDataKey);
L241:             _timelineMemories = loadedTimeline != null
L242:                 ? new Dictionary<string, List<MemoryEntry>>(loadedTimeline, StringComparer.OrdinalIgnoreCase)
L243:                 : new Dictionary<string, List<MemoryEntry>>(StringComparer.OrdinalIgnoreCase);
L244:             _timelineMemories = _timelineMemories
L245:                 .Where(kv => !string.IsNullOrWhiteSpace(kv.Key) && kv.Value != null)
L246:                 .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
L523:     /// <summary>同尺度差值：daysAgo = date.DaysSince(now)，返回 CurrentGameDay - daysAgo；未来日期钳制为今日。</summary>
L524:     public static int StardewTimeToGameDay(StardewTime date)
L525:     {
L526:         if (!Context.IsWorldReady) return 0;
L527:         int daysAgo = (int)Math.Round(Math.Max(0, date.DaysSince(new StardewTime(Game1.Date, Game1.timeOfDay))));
L528:         return Math.Max(0, CurrentGameDay() - daysAgo);
L529:     }
L530:
L531:     public MemoryOperationResult AddTimelineMemory(string npcName, string content, MemoryTier tier,
L532:         string dateLabel = null, int createdDay = -1)
L533:     {
L534:         if (string.IsNullOrWhiteSpace(npcName) || string.IsNullOrWhiteSpace(content))
L535:             return MemoryOperationResult.NotFound;
L536:
L537:         EnsureLoaded();
L538:
L539:         if (_loadFailed)
L602:     public MemoryOperationResult EditTimelineMemory(string npcName, string entryId, string newContent)
L603:     {
L604:         if (string.IsNullOrWhiteSpace(npcName) || string.IsNullOrWhiteSpace(entryId))
L605:             return MemoryOperationResult.NotFound;
L606:
L607:         if (!_timelineMemories.TryGetValue(npcName, out var list))
L608:             return MemoryOperationResult.NotFound;
L609:
L610:         var entry = list.FirstOrDefault(m => m.Id == entryId);
L611:         if (entry == null) return MemoryOperationResult.NotFound;
L612:
L613:         if (string.IsNullOrWhiteSpace(newContent))
L614:             return MemoryOperationResult.NotFound;
L615:
L616:         var trimmed = SmartTruncate(newContent.Trim(), MaxMemoryLength);
L617:
L618:         if (list.Any(m => m.Id != entryId && m.Tier == entry.Tier && string.Equals(m.Content, trimmed, StringComparison.OrdinalIgnoreCase)))
L619:             return MemoryOperationResult.Duplicate;
L620:
L621:         entry.Content = trimmed;
L622:         SaveTimeline();
L623:         return MemoryOperationResult.Success;
L624:     }
L664:     private void SaveTimeline()
L665:     {
L666:         if (_loadFailed)
L667:         {
L668:             ModEntry.SMonitor?.Log(
L669:                 "[MemoryManager] Write refused: last load failed, refusing to overwrite SaveData.",
L670:                 LogLevel.Error);
L671:             return;
L672:         }
L673:
L674:         try
L675:         {
L676:             if (!Context.IsWorldReady || ModEntry.SHelper == null) return;
L677:             ModEntry.SHelper.Data.WriteSaveData(TimelineSaveDataKey, _timelineMemories);
L678:         }
L679:         catch (Exception ex)
L680:         {
L681:             ModEntry.SMonitor?.Log($"[MemoryManager] SaveTimeline failed: {ex.Message}", LogLevel.Warn);
L682:         }
```

## E08 — src/Memory/MemoryExtractService.cs

```text
L13: internal enum MemoryExtractStatus { Success, Empty, NoHistory, Cancelled, Failed }
L14:
L15: internal sealed class MemoryExtractResult
L16: {
L17:     public MemoryExtractStatus Status { get; internal set; }
L18:     public List<string> Candidates { get; internal set; } = new();  // 0..3 条，已 Trim、非空、已去重
L19:     public string ErrorDetail { get; internal set; } = "";          // 仅日志用
L20: }
L28:     public const int HistoryPullCount = 30;   // 先拉 30 条再过滤
L29:     public const int HistoryUseCount = 12;    // 过滤后取末 12 条
L30:     public const int MaxCandidates = 3;
L31:
L32:     /// <summary>
L33:     /// 公共 LLM 推理执行器：RunInference + 超时 + OCE 分类 + 跨档守卫 + 空响应五件套。
L34:     /// </summary>
L35:     private static async Task<(bool Ok, LlmResponse Resp, MemoryExtractStatus Status, string Error)> ExecuteInferenceAsync(
L36:         string sysPrompt, string userPrompt, int nPredict, string expectedFolder, CancellationToken ct)
L37:     {
L38:         using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
L39:         timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(ModEntry.Config.LlmTimeoutSeconds, 15, 120)));
L40:
L41:         LlmResponse resp;
L42:         try
L43:         {
L44:             resp = await Llm.Instance.RunInference(
L45:                 systemPromptString: sysPrompt,
L46:                 gameCacheString: "",
L47:                 npcCacheString: "",
L48:                 promptString: userPrompt,
L49:                 responseStart: "[",
L50:                 n_predict: nPredict,
L51:                 cacheContext: LlmContextTypes.NoTools,
L52:                 allowRetry: false
L53:             ).WaitAsync(timeoutCts.Token);
L54:         }
L55:         catch (OperationCanceledException)
L56:         {
L57:             if (ct.IsCancellationRequested)
L58:                 return (false, null, MemoryExtractStatus.Cancelled, "external cancellation");
L59:             return (false, null, MemoryExtractStatus.Failed, "timeout");
L60:         }
L61:         catch (Exception ex)
L62:         {
L63:             return (false, null, MemoryExtractStatus.Failed, ex.Message);
L64:         }
L65:
L66:         if (Constants.SaveFolderName != expectedFolder)
L67:             return (false, null, MemoryExtractStatus.Cancelled, "save folder changed");
L68:
L69:         if (!resp.IsSuccess || string.IsNullOrWhiteSpace(resp.Text))
L70:             return (false, null, MemoryExtractStatus.Failed,
L71:                 "empty or failed llm response: " + (resp.ErrorMessage ?? "(no error message)"));
L72:
L73:         return (true, resp, default, "");
L267:     internal static string BuildPersonaSlice(string npcName)
L268:     {
L269:         if (string.IsNullOrWhiteSpace(npcName)) return "";
L270:
L271:         var npc = Context.IsWorldReady ? Game1.getCharacterFromName(npcName) : null;
L272:         var character = npc == null ? null : DialogueBuilder.Instance?.GetCharacter(npc);
L273:         if (npc == null || character == null) return "";
L274:
L275:         var bio = character.Bio;
L276:         if (bio == null || bio.Missing) return "";
L277:
L278:         string desc = "";
L279:         if (bio.Traits.TryGetValue("BehavioralRules", out var rule) && !string.IsNullOrWhiteSpace(rule?.Description))
L280:             desc = rule.Description;
L281:
L282:         if (string.IsNullOrWhiteSpace(desc)) return "";
L283:
L284:         var stageText = ProgressStateResolver.ResolveActiveEntry(npc, bio.ProgressStates)?.Text ?? "";
L285:
L311:         string expectedFolder = Constants.SaveFolderName;
L312:
L313:         // 拉取并过滤对话记录
L314:         var entries = DialogueHistoryManager.Instance.GetRecentHistory(npcName, HistoryPullCount);
L315:         var filtered = entries
L316:             .Where(e => e.DialogueType != "eavesdrop")
L317:             .Where(e => !string.IsNullOrWhiteSpace(e.Text))
L318:             .ToList();
L319:
L320:         if (dateFilter.HasValue)
L321:         {
L322:             filtered = filtered
L323:                 .Where(e => e.Timestamp.Year == dateFilter.Value.Year
L324:                          && e.Timestamp.Season == dateFilter.Value.Season
L325:                          && e.Timestamp.DayOfMonth == dateFilter.Value.DayOfMonth)
L326:                 .ToList();
L327:         }
L328:
L329:         var useEntries = filtered
L330:             .Skip(Math.Max(0, filtered.Count - HistoryUseCount))
L331:             .ToList();
L332:
L333:         if (useEntries.Count == 0)
L334:         {
L335:             result.Status = MemoryExtractStatus.NoHistory;
L336:             ModEntry.SMonitor.Log($"[MemoryExtractService] NoHistory for [{npcName}]: no usable dialogue history.", LogLevel.Debug);
L337:             return result;
L338:         }
L339:
L340:         bool isZh = I18n.IsChinese;
L530:     private static MemoryExtractResult ParseAndCollectResult(
L531:         string raw,
L532:         MemoryExtractResult result,
L533:         string logContextName,
L534:         string tag,
L535:         IReadOnlyList<string> existingMemories = null)
L559:         var dedup = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
L560:         var existingSet = existingMemories != null
L561:             ? new HashSet<string>(existingMemories.Where(m => !string.IsNullOrWhiteSpace(m)), StringComparer.OrdinalIgnoreCase)
L562:             : null;
L563:
L564:         foreach (var token in array)
L565:         {
L566:             if (token.Type != JTokenType.String) continue;
L567:             string v = token.Value<string>()?.Trim();
L568:             if (string.IsNullOrWhiteSpace(v)) continue;
L569:             if (!dedup.Add(v)) continue;
L570:             if (existingSet != null && existingSet.Contains(v)) continue;
L571:
L572:             result.Candidates.Add(v);
L573:             if (result.Candidates.Count >= MaxCandidates) break;
L574:         }
L575:
L576:         if (result.Candidates.Count == 0)
L577:         {
L578:             result.Status = MemoryExtractStatus.Empty;
L579:             ModEntry.SMonitor.Log($"[MemoryExtractService] {tag} empty for [{logContextName}]: no candidates extracted.", LogLevel.Debug);
L580:         }
L581:         else
L582:         {
L583:             result.Status = MemoryExtractStatus.Success;
L584:             ModEntry.SMonitor.Log($"[MemoryExtractService] {tag} success for [{logContextName}]: {result.Candidates.Count} candidate(s): [{string.Join(", ", result.Candidates)}]", LogLevel.Debug);
L585:         }
```

## E09 — src/Memory/TimelineAutoSummaryScheduler.cs

```text
L12: /// <summary>自动总结类型。</summary>
L13: internal enum AutoSummaryType { Daily, Weekly, Season, Yearly }
L14:
L15: /// <summary>一条自动总结任务（轻量描述，源条目在执行期解析，不做快照）。</summary>
L16: internal sealed class AutoSummaryTask
L17: {
L18:     public string NpcName { get; set; } = "";
L19:     public string NpcDisplayName { get; set; } = "";
L20:     public AutoSummaryType Type { get; set; }
L21:     public StardewTime TargetDate { get; set; }   // 各层统一为"昨天"
L22: }
L23:
L24: /// <summary>
L25: /// 时间线自动总结调度器：每日/每周/每季/每年扫描一次，将符合条件的 NPC 入队，
L26: /// 由 OneSecondUpdateTicked 的节流器逐条触发生成。
L27: /// 队列/处理标志/冷却为进程内 Memory；日哨兵写入 player.modData（存档持久）。
L28: /// 仅主机（Context.IsMainPlayer）启用。
L29: /// </summary>
L30: internal sealed class TimelineAutoSummaryScheduler
L31: {
L32:     public static TimelineAutoSummaryScheduler Instance { get; } = new();
L33:
L34:     private const int ThrottleSeconds = 35;
L35:     private const string LastScanDayKey = "valleytalk.autosummary-lastscan";
L36:
L37:     private readonly Queue<AutoSummaryTask> _queue = new();
L38:     private bool _isProcessing;
L39:     private int _cooldownSecondsRemaining;
L40:
L41:     private TimelineAutoSummaryScheduler() { }
L42:
L43:     public void Initialize(IModHelper helper)
L44:     {
L45:         helper.Events.GameLoop.DayStarted -= OnDayStarted;
L46:         helper.Events.GameLoop.SaveLoaded -= OnSaveLoaded;
L47:         helper.Events.GameLoop.OneSecondUpdateTicked -= OnOneSecondUpdateTicked;
L48:         helper.Events.GameLoop.DayStarted += OnDayStarted;
L49:         helper.Events.GameLoop.SaveLoaded += OnSaveLoaded;
L50:         helper.Events.GameLoop.OneSecondUpdateTicked += OnOneSecondUpdateTicked;
L51:     }
L52:
L53:     public void Cleanup()
L54:     {
L55:         _queue.Clear();
L56:         _isProcessing = false;
L57:         _cooldownSecondsRemaining = 0;
L58:     }
L59:
L60:     private void OnDayStarted(object sender, DayStartedEventArgs e) => TryScanAndEnqueue();
L61:     private void OnSaveLoaded(object sender, SaveLoadedEventArgs e) => TryScanAndEnqueue();
L62:
L63:     private void OnOneSecondUpdateTicked(object sender, OneSecondUpdateTickedEventArgs e)
L64:     {
L65:         if (!Context.IsWorldReady || !ModEntry.Config.EnableMod) return;
L66:
L67:         if (_cooldownSecondsRemaining > 0)
L68:         {
L69:             _cooldownSecondsRemaining--;
L70:             return;
L71:         }
L72:
L73:         if (!_isProcessing && _queue.Count > 0)
L74:         {
L75:             _isProcessing = true;
L76:             _ = ProcessNextTaskAsync();
L77:         }
L78:     }
L79:
L80:     private void TryScanAndEnqueue()
L81:     {
L82:         if (!Context.IsWorldReady || !Context.IsMainPlayer || !ModEntry.Config.EnableMod) return;
L83:
L84:         var now = new StardewTime(Game1.Date, Game1.timeOfDay);
L85:         int today = MemoryManager.StardewTimeToGameDay(now);
L86:         string sentinel = Game1.player.modData.TryGetValue(LastScanDayKey, out var s) ? s : "";
L87:         if (sentinel == today.ToString())
L88:         {
L151:             // d. 日报（昨日与农夫互动 >= 3 次，且昨日无日记）
L152:             if (ModEntry.Config.AutoSummarizeDaily)
L153:             {
L154:                 bool hasDailyForYesterday = MemoryManager.Instance.GetTimelineMemories(name, MemoryTier.Daily)
L155:                     .Any(e =>
L156:                     {
L157:                         if (e.CreatedDay <= 0) return false;
L158:                         var t = MemoryManager.GameDayToStardewTime(e.CreatedDay);
L159:                         return t.Year == yesterday.Year && t.Season == yesterday.Season && t.DayOfMonth == yesterday.DayOfMonth;
L160:                     });
L161:
L162:                 if (!hasDailyForYesterday)
L163:                 {
L164:                     int playerLines = DialogueHistoryManager.Instance.GetRecentHistory(name, 30)
L165:                         .Count(e => e.SpeakerType == SpeakerType.Player
L166:                             && e.Timestamp.Year == yesterday.Year
L167:                             && e.Timestamp.Season == yesterday.Season
L168:                             && e.Timestamp.DayOfMonth == yesterday.DayOfMonth);
L169:                     if (playerLines >= 3)
L170:                     {
L171:                         _queue.Enqueue(new AutoSummaryTask
L172:                         {
L173:                             NpcName = name,
L174:                             NpcDisplayName = displayName,
L175:                             Type = AutoSummaryType.Daily,
L176:                             TargetDate = yesterday
L177:                         });
L178:                         enqueued++;
L179:                     }
L180:                 }
L181:             }
L182:         }
L183:
L184:         Game1.player.modData[LastScanDayKey] = today.ToString();
L185:         if (enqueued > 0)
L186:         {
L187:             ModEntry.SMonitor?.Log($"[TimelineAutoSummary] Enqueued {enqueued} automatic summary task(s) (game day {today}).", LogLevel.Info);
L188:         }
L191:     private async Task ProcessNextTaskAsync()
L192:     {
L193:         AutoSummaryTask task = null;
L194:         try
L195:         {
L196:             if (_queue.Count == 0) return;
L197:             task = _queue.Dequeue();
L198:
L199:             // 守卫链：任一命中即丢弃任务（已出队），finally 复位节流。
L200:             if (!Context.IsWorldReady || !Context.IsMainPlayer)
L201:             {
L202:                 ModEntry.SMonitor?.Log($"[TimelineAutoSummary] Task [{task.Type}] for [{task.NpcName}] dropped: world not ready or not main player.", LogLevel.Debug);
L203:                 return;
L204:             }
L205:             if (DialogueBuilder.Instance?.LlmDisabled == true)
L206:             {
L207:                 ModEntry.SMonitor?.Log($"[TimelineAutoSummary] Task [{task.Type}] for [{task.NpcName}] dropped: LLM disabled.", LogLevel.Debug);
L208:                 return;
L209:             }
L210:             if (PeriodAlreadyCovered(task))
L211:             {
L212:                 ModEntry.SMonitor?.Log($"[TimelineAutoSummary] Task [{task.Type}] for [{task.NpcName}] dropped: period already covered.", LogLevel.Debug);
L213:                 return;
L214:             }
L215:
L216:             int targetDay = MemoryManager.StardewTimeToGameDay(task.TargetDate);
L217:             MemoryExtractResult result;
L218:
L219:             if (task.Type == AutoSummaryType.Daily)
L220:             {
L221:                 var existingContents = MemoryManager.Instance.GetTimelineMemories(task.NpcName, MemoryTier.Daily)
L222:                     .Select(e => e.Content).Take(10).ToList();
L223:                 result = await MemoryExtractService.ExtractAsync(
L224:                     task.NpcName, task.NpcDisplayName, existingContents, task.TargetDate, CancellationToken.None);
L225:             }
L226:             else
L227:             {
L228:                 var sourceEntities = ResolveSourceEntities(task);
L229:                 var sourceContents = sourceEntities.Select(e => e.Content).ToList();
L230:                 if (sourceContents.Count < 2)
L231:                 {
L232:                     ModEntry.SMonitor?.Log($"[TimelineAutoSummary] Task [{task.Type}] for [{task.NpcName}] dropped: only {sourceContents.Count} source(s) (need >= 2).", LogLevel.Debug);
L233:                     return;
L234:                 }
L235:                 result = await MemoryExtractService.CondenseAsync(
L236:                     task.NpcName, task.NpcDisplayName, sourceContents, CondenseTier(task.Type), CancellationToken.None);
L237:             }
L238:
L239:             if (result == null || result.Status != MemoryExtractStatus.Success
L240:                 || result.Candidates == null || result.Candidates.Count == 0)
L241:             {
L242:                 ModEntry.SMonitor?.Log($"[TimelineAutoSummary] Task [{task.Type}] for [{task.NpcName}] produced no result (status={result?.Status}, error={result?.ErrorDetail}).", LogLevel.Debug);
L243:                 return;
L244:             }
L245:
L246:             var capturedTask = task;
L247:             var capturedResult = result;
L248:             MainThreadActionQueue.EnqueueMainThread(() => CompleteTask(capturedTask, capturedResult, targetDay));
L249:         }
L250:         catch (Exception ex)
L251:         {
L252:             ModEntry.SMonitor?.Log($"[TimelineAutoSummary] ProcessNextTaskAsync error: {ex}", LogLevel.Warn);
L253:         }
L254:         finally
L255:         {
L256:             _isProcessing = false;
L257:             _cooldownSecondsRemaining = ThrottleSeconds;
L258:         }
L262:     private void CompleteTask(AutoSummaryTask task, MemoryExtractResult result, int targetDay)
L263:     {
L264:         try
L265:         {
L266:             if (!Context.IsWorldReady)
L267:             {
L268:                 ModEntry.SMonitor?.Log($"[TimelineAutoSummary] CompleteTask [{task.Type}] for [{task.NpcName}] skipped: world not ready.", LogLevel.Debug);
L269:                 return;
L270:             }
L271:
L272:             var targetTier = TargetTier(task.Type);
L273:             string bestText = result.Candidates.FirstOrDefault();
L274:             if (string.IsNullOrWhiteSpace(bestText)) return;
L275:
L276:             var addResult = MemoryManager.Instance.AddTimelineMemory(task.NpcName, bestText, targetTier, null, targetDay);
L277:
L278:             if (addResult == MemoryOperationResult.Success)
L279:             {
L280:                 var sourceEntities = ResolveSourceEntities(task);
L281:                 if (sourceEntities.Count > 0)
L282:                 {
L283:                     MemoryManager.Instance.ArchiveTimelineMemories(task.NpcName, sourceEntities, "Distilled");
L284:                     MemoryManager.Instance.RemoveTimelineMemories(task.NpcName, sourceEntities.Select(e => e.Id).ToList());
L285:                 }
L317:     /// <summary>按任务类型解析源条目实体（主线程调用）。</summary>
L318:     private List<MemoryEntry> ResolveSourceEntities(AutoSummaryTask task)
L319:     {
L320:         if (task.Type == AutoSummaryType.Daily) return new List<MemoryEntry>();
L321:
L322:         if (task.Type == AutoSummaryType.Weekly)
L323:         {
L324:             int targetDayNum = DateToDayNumber(task.TargetDate);
L325:             int windowStart = targetDayNum - 6;
L326:             return MemoryManager.Instance.GetTimelineMemories(task.NpcName, MemoryTier.Daily)
L327:                 .Where(e =>
L328:                 {
L329:                     if (e.CreatedDay <= 0) return false;
L330:                     int d = DateToDayNumber(MemoryManager.GameDayToStardewTime(e.CreatedDay));
L331:                     return d >= windowStart && d <= targetDayNum;
L332:                 })
L333:                 .OrderBy(e => e.CreatedDay)
L334:                 .ToList();
L335:         }
L336:
L337:         if (task.Type == AutoSummaryType.Season)
L338:         {
L339:             return MemoryManager.Instance.GetTimelineMemories(task.NpcName, MemoryTier.Weekly)
L340:                 .Where(e =>
L341:                 {
L342:                     if (e.CreatedDay <= 0) return false;
L343:                     var t = MemoryManager.GameDayToStardewTime(e.CreatedDay);
L344:                     return t.Year == task.TargetDate.Year && t.Season == task.TargetDate.Season;
L345:                 })
L346:                 .OrderBy(e => e.CreatedDay)
L347:                 .ToList();
L348:         }
L349:
L350:         // Yearly
L351:         return MemoryManager.Instance.GetTimelineMemories(task.NpcName, MemoryTier.Chronicle)
L352:             .Where(e =>
L353:             {
L354:                 if (e.CreatedDay <= 0) return false;
L355:                 return MemoryManager.GameDayToStardewTime(e.CreatedDay).Year == task.TargetDate.Year;
L356:             })
L357:             .OrderBy(e => e.CreatedDay)
L358:             .ToList();
L359:     }
L360:
L361:     private bool PeriodAlreadyCovered(AutoSummaryTask task)
L362:     {
L363:         var entries = MemoryManager.Instance.GetTimelineMemories(task.NpcName, TargetTier(task.Type));
L364:         foreach (var e in entries)
L365:         {
L366:             if (e.CreatedDay <= 0) continue;
L367:             var t = MemoryManager.GameDayToStardewTime(e.CreatedDay);
L368:             bool match = task.Type switch
L369:             {
L370:                 AutoSummaryType.Daily => t.Year == task.TargetDate.Year && t.Season == task.TargetDate.Season && t.DayOfMonth == task.TargetDate.DayOfMonth,
L371:                 AutoSummaryType.Weekly => t.Year == task.TargetDate.Year && t.Season == task.TargetDate.Season && (t.DayOfMonth - 1) / 7 == (task.TargetDate.DayOfMonth - 1) / 7,
L372:                 AutoSummaryType.Season => t.Year == task.TargetDate.Year && t.Season == task.TargetDate.Season,
L373:                 AutoSummaryType.Yearly => t.Year == task.TargetDate.Year,
L374:                 _ => false
L375:             };
L376:             if (match) return true;
L377:         }
L378:         return false;
```

## E10 — src/LLM/Providers/Llm.cs

```text
L333:     internal abstract Task<LlmResponse> RunInference(
L334:         string systemPromptString,
L335:         string gameCacheString,
L336:         string npcCacheString,
L337:         string promptString,
L338:         string responseStart = "",
L339:         int n_predict = 2048,
L340:         string cacheContext = "",
L341:         bool allowRetry = true);
L342:
L343:     /// <summary>
L344:     /// 可取消的推理入口（B2）。默认实现委托给 RunInference 并忽略令牌：
L345:     /// 未覆盖的实现者（Dummy / Claude / Gemini）行为与改动前完全一致。
L346:     /// 覆盖者把令牌贯穿到 HTTP 与重试等待——取消不再是"弃等"，后台请求不再空跑。
L347:     /// </summary>
L348:     internal virtual Task<LlmResponse> RunInferenceAsync(
L349:         string systemPromptString,
L350:         string gameCacheString,
L351:         string npcCacheString,
L352:         string promptString,
L353:         CancellationToken ct,
L354:         string responseStart = "",
L355:         int n_predict = 2048,
L356:         string cacheContext = "",
L357:         bool allowRetry = true)
L358:     {
L359:         return RunInference(systemPromptString, gameCacheString, npcCacheString, promptString, responseStart, n_predict, cacheContext, allowRetry);
L360:     }
```

## E11 — src/LLM/Prompts/Generation/AsyncBuilder.cs

```text
L62:     public bool AwaitingGeneration => _awaitingGeneration;
L63:     public bool IsGeneratingDialogue { get; internal set; }
L64:     public NPC SpeakingNpc => _speakingNpc;
L65:     internal GenerationType AwaitedType => _awaitedType;
L66:     public int GenerationCooldownFrames => _generationCooldownFrames;
```

## E12 — src/UI/PendingChoiceStore.cs

```text
L43:         /// <summary>不清空，供 Postfix 绑定或 OnMenuChanged 探活。</summary>
L44:         public static bool TryPeek(out PendingChoiceContext context)
L45:         {
L46:             context = _pending;
L47:             return context != null;
L48:         }
L49:
L50:         /// <summary>
L51:         /// BoxRef == closedBox 才返回并清空；不匹配（事件打断等）→ Clear + Trace + null。
L52:         /// </summary>
L53:         public static PendingChoiceContext ConsumeMatching(IClickableMenu closedBox)
L54:         {
L55:             var pending = _pending;
L56:
L57:             if (pending != null && closedBox != null && ReferenceEquals(pending.BoxRef, closedBox))
L58:             {
L59:                 _pending = null;
L60:                 return pending;
L61:             }
L62:
L63:             ModEntry.SMonitor?.Log(
L64:                 $"[PendingChoiceStore] discarded stale choice payload" +
L65:                 $" (speaker={pending?.Speaker?.Name ?? "null"}, pendingBox={pending?.BoxRef?.GetType().Name ?? "null"}, closedBox={closedBox?.GetType().Name ?? "null"}).",
L66:                 LogLevel.Trace);
L67:
L68:             Clear();
L69:             return null;
```

## E13 — src/Actions/MainThreadActionQueue.cs

```text
L18:     public static void ProcessMainThreadQueue()
L19:     {
L20:         while (_mainThreadActions.TryDequeue(out var action))
L21:         {
L22:             try
L23:             {
L24:                 action?.Invoke();
L25:             }
L26:             catch (Exception ex)
L27:             {
L28:                 LogError($"[MainThreadActionQueue] Queue execution error: {ex}");
L29:             }
L30:         }
L31:     }
L32:
L33:     /// <summary>线程安全投递一个主线程回调；由 ModEntry.OnUpdateTicked 的 ProcessMainThreadQueue 消费。</summary>
L34:     public static void EnqueueMainThread(Action action)
L35:     {
L36:         if (action == null) return;
L37:         _mainThreadActions.Enqueue(action);
L38:     }
```

## E14 — src/LLM/Prompts/Generation/DialogueBuilder.cs

```text
L1250:             // 检查 NPC 自身的 modData 键名是否包含未授权的包名
L1251:             if (n.modData != null && n.modData.Keys.Any())
L1252:             {
L1253:                 foreach (var packId in ModEntry.DisallowedContentPackIds)
L1254:                 {
L1255:                     if (n.modData.Keys.Any(k => k.Contains(packId, StringComparison.OrdinalIgnoreCase)))
L1256:                     {
L1257:                         return true;
```

## E15 — src/LLM/Providers/LlmLlamaCpp.cs

```text
L67:     /// <summary>
L68:     /// 可取消的非流式入口（B2）：令牌贯穿闸门排队与核心（HTTP / 重试等待），
L69:     /// 取消真正中止请求并即时释放闸门租约，不再"弃等"。遥测语义与 RunInference 一致。
L70:     /// </summary>
L71:     internal override async Task<LlmResponse> RunInferenceAsync(
L72:         string systemPromptString, string gameCacheString, string npcCacheString,
L73:         string promptString, CancellationToken ct,
L74:         string responseStart = "", int n_predict = 2048,
L75:         string cacheContext = "", bool allowRetry = true)
L76:     {
L77:         // LOCAL-005：本地端点（回环 / 私网）请求进入进程内并发闸门；云端地址拿到空租约。
L78:         LocalRequestLease lease;
L79:
L80:         try
L81:         {
L82:             lease = await LocalRequestThrottle.AcquireAsync(url, ct);
L83:         }
L84:         catch (OperationCanceledException)
L85:         {
L86:             // BOUNDARY：排队阶段被取消 —— 不进入 HTTP，不记为服务故障。
L87:             Log.Debug($"[LlmLlamaCpp] Local request cancelled while queued; no HTTP sent. endpoint={url}");
L88:             return LlmResponse.Cancelled();
L89:         }
L90:
L91:         var telemetry = new LlmTrafficLogger.LlmRequestTelemetry(nameof(LlmLlamaCpp), url, null, string.Empty);
L92:
L93:         using (lease)
L94:         {
L95:             telemetry.QueueWaitMs = lease.QueueWaitMs;
L96:             var totalWatch = System.Diagnostics.Stopwatch.StartNew();
L97:
L98:             LlmResponse result = await RunInferenceCoreAsync(
L99:                 systemPromptString, gameCacheString, npcCacheString,
L100:                 promptString, responseStart, n_predict, ct, allowRetry, telemetry);
```

## E16 — ValleytalkReborn.Tests/ValleytalkReborn.Tests.csproj

```text
L3:   <PropertyGroup>
L4:     <TargetFramework>net6.0</TargetFramework>
L5:     <ImplicitUsings>enable</ImplicitUsings>
L6:     <Nullable>disable</Nullable>
L7:     <IsPackable>false</IsPackable>
L8:     <SuppressTfmSupportBuildErrors>true</SuppressTfmSupportBuildErrors>
L9:     <NoWarn>NU1605;NU1701;CS8632</NoWarn>
L10:   </PropertyGroup>
L11:
L12:   <ItemGroup>
L13:     <PackageReference Include="coverlet.collector" Version="3.2.0" />
L14:     <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.5.0" />
L15:     <PackageReference Include="xunit" Version="2.4.2" />
L16:     <PackageReference Include="xunit.runner.visualstudio" Version="2.4.5" />
L23:   <ItemGroup>
L24:     <ProjectReference Include="..\src\ValleytalkReborn.csproj">
L25:       <PrivateAssets>all</PrivateAssets>
L26:     </ProjectReference>
```

## E17 — src/ValleytalkReborn.csproj

```text
L3:     <Version>1.0.0</Version>
L4:     <TargetFramework>net6.0</TargetFramework>
L5:     <LangVersion>latest</LangVersion>
L6:     <GamePath>H:\ValleytalkReborn\Stardew Valley</GamePath>
L7:     <EnableHarmony>true</EnableHarmony>
L8:     <Platforms>AnyCPU;x64</Platforms>
L25:   <ItemGroup>
L26:     <PackageReference Include="FontStashSharp.MonoGame" Version="1.3.9" />
L27:     <PackageReference Include="newtonsoft.json" Version="13.0.5-beta1" />
L28:     <PackageReference Include="Pathoschild.Stardew.ModBuildConfig" Version="4.4.0" />
L29:     <PackageReference Include="TextCopy" Version="6.2.1" />
```

## E18 — 补充搜索证据

- search_text("configAutoSummarizeDaily", paths=["src/i18n/**"])：src/i18n/default.json 与 src/i18n/zh.json 各在 L372-L373 有现有日记 UI 文案。
- search_text("Game1.eventUp", paths=["src/**"])：src/UI/DialogueChoiceMenu.cs L255；src/Patches/Event_AnswerDialogue_Patch.cs L25。
- read_file("src/AssemblyInfo.cs")：L4 InternalsVisibleTo("ValleytalkReborn.Tests")。
- search_text("BuildManifestOptions", paths=["src/UI/GMCM/**"])：空结果；现有注册点是 E02 的 Register。
- search_file("**/AGENTS.md")：空结果；本任务遵循用户在聊天中提供的 AGENTS.md 指令。

## E19 — 新增设计声明

read_file("docs/architecture/daily-distillation-declarations.md") 已在本会话成功返回。D1-D6 为 Architect 提议的新接口/ModData key/常量，其基础类型与接入接口来自 E01-E18/E20；这些声明不代表功能已实现。

## E20 — 已确认的高层存储缺口与日期可见性

本会话 read_file("src/Memory/MemoryManager.cs") 补充摘录。归档入口返回Success而SaveArchivedTimeline为void；CurrentGameDay是private，外部调用采用公开StardewTimeToGameDay或主线程Game1.Date.TotalDays。

```text
L685:     /// <summary>将时间线条目只入归档箱（不删除时间线条目，由调用方显式 RemoveTimelineMemories）。</summary>
L686:     public MemoryOperationResult ArchiveTimelineMemories(string npcName, IReadOnlyList<MemoryEntry> entries, string archiveReason)
L687:     {
L688:         if (string.IsNullOrWhiteSpace(npcName) || entries == null)
L689:         {
L690:             ModEntry.SMonitor?.Log("[MemoryManager] ArchiveTimelineMemories: npcName empty or entries null.", LogLevel.Trace);
L691:             return MemoryOperationResult.NotFound;
L692:         }
L693:
L694:         EnsureLoaded();
L695:
L696:         if (_loadFailed)
L697:         {
L698:             ModEntry.SMonitor?.Log("[MemoryManager] ArchiveTimelineMemories refused: last load failed, refusing to mutate state.", LogLevel.Error);
L699:             return MemoryOperationResult.CapacityFull;
L700:         }
L701:
L702:         var list = entries.Where(e => e != null && !string.IsNullOrWhiteSpace(e.Id)).ToList();
L703:         if (list.Count == 0)
L704:         {
L705:             ModEntry.SMonitor?.Log("[MemoryManager] ArchiveTimelineMemories: no valid entries.", LogLevel.Trace);
L706:             return MemoryOperationResult.NotFound;
L707:         }
L708:
L709:         if (!_archivedTimelineMemories.TryGetValue(npcName, out var archiveList))
L710:         {
L711:             archiveList = new List<MemoryEntry>();
L712:             _archivedTimelineMemories[npcName] = archiveList;
L713:         }
L714:
L715:         foreach (var entry in list)
L716:         {
L717:             entry.ArchivedAt = DateTime.Now;
L718:             entry.ArchiveReason = archiveReason ?? "";
L719:             archiveList.Insert(0, entry);
L720:         }
L721:
L722:         while (archiveList.Count > MaxArchivedTimelineMemoriesPerNpc)
L723:             archiveList.RemoveAt(archiveList.Count - 1);
L724:
L725:         SaveArchivedTimeline();
L726:         ModEntry.SMonitor?.Log(
L727:             $"[MemoryManager] Archived {list.Count} timeline memories for [{npcName}] (reason: {archiveReason}).",
L728:             LogLevel.Info);
L729:         return MemoryOperationResult.Success;
L1463:     private static int CurrentGameDay() =>
L1464:         Context.IsWorldReady ? (int)Game1.Date.TotalDays : 0;
L1465:
L1466:     /// <summary>
L1467:     /// 在 [maxLen*0.6, maxLen] 区间内找最后一个句末标点（。！？!?.…）截断（含标点）；
L1468:     /// 找不到则硬切 maxLen。
L1469:     /// </summary>
L1470:     internal static string SmartTruncate(string content, int maxLen)
L1471:     {
```



## E21 — 时间类型与一基日历逆变换

本会话 read_file("src/Utils/StardewTime.cs") 选段。

```text
L10: public readonly struct StardewTime : IComparable<StardewTime>, IEquatable<StardewTime>
L11: {
L12:     public Season Season { get; }
L13:     public int DayOfMonth { get; }
L14:     public int TimeOfDay { get; }
L15:     public int Year { get; }
L28:     public StardewTime(WorldDate date, int time)
L29:         : this(date.Year, (Season)date.Season, date.DayOfMonth, time) { }
L30:
L31:     public StardewTime(int year, Season season, int dayOfMonth, int timeOfDay)
L32:     {
L33:         Year = year;
L34:         Season = season;
L35:         DayOfMonth = dayOfMonth;
L36:         TimeOfDay = timeOfDay;
L37:     }
L110:     public StardewTime AddDays(int offset)
L111:     {
L112:         int totalDays = (Year * 112) + ((int)Season * 28) + (DayOfMonth - 1) + offset;
L113:         if (totalDays < 0) totalDays = 0;
L114:
L115:         int newYear = totalDays / 112;
L116:         int remDays = totalDays % 112;
L117:         int newSeason = remDays / 28;
L118:         int newDay = (remDays % 28) + 1;
L119:
L120:         return new StardewTime(newYear, (Season)newSeason, newDay, TimeOfDay);
L121:     }
L122:
L123:     public int CompareTo(StardewTime other) => TotalDays.CompareTo(other.TotalDays);
```

## E22 — 归档写入失败与日期转换

本会话 read_file("src/Memory/MemoryManager.cs") 选段。

```text
L377:     private void SaveArchivedTimeline()
L378:     {
L379:         if (_loadFailed)
L380:         {
L381:             ModEntry.SMonitor?.Log(
L382:                 "[MemoryManager] Write refused: last load failed, refusing to overwrite SaveData.",
L383:                 LogLevel.Error);
L384:             return;
L385:         }
L386:
L387:         try
L388:         {
L389:             if (!Context.IsWorldReady || ModEntry.SHelper == null) return;
L390:             ModEntry.SHelper.Data.WriteSaveData(TimelineArchiveSaveDataKey, _archivedTimelineMemories);
L391:         }
L392:         catch (Exception ex)
L393:         {
L394:             ModEntry.SMonitor?.Log($"[MemoryManager] SaveArchivedTimeline failed: {ex.Message}", LogLevel.Warn);
L395:         }
L423:     };
L424:
L425:     /// <summary>
L426:     /// 将游戏总天数 (Game1.Date.TotalDays) 还原为 StardewTime。
L427:     /// 星露谷每年 112 天，每季 28 天，TotalDays 从 1 开始。
L428:     /// </summary>
L429:     public static StardewTime GameDayToStardewTime(int totalDays)
L430:     {
L431:         if (totalDays <= 0) totalDays = CurrentGameDay();
L432:         int year = (totalDays - 1) / 112 + 1;
L433:         int dayOfSeason = (totalDays - 1) % 28 + 1;
L434:         Season season = (Season)(((totalDays - 1) / 28) % 4);
L435:         return new StardewTime(year, season, dayOfSeason, 600);
```
