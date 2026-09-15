using System;
using HarmonyLib;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using StardewValley;
using ValleytalkReborn.Plugins;
using Microsoft.Xna.Framework;

namespace ValleytalkReborn
{
    public partial class ModEntry : Mod
    {
        public static IMonitor SMonitor;
        public static IModHelper SHelper { get; private set; }
        public static ModConfig Config;

        /// <summary>
        /// A2A 输出验证转发器（供 MainThreadOutputQueue 使用）。
        /// </summary>
        internal static bool A2AOutputValidator(string sessionId, int generation, string npcName)
        {
            return _dialogueCoordinator?.A2A.SessionManager.ValidateA2AOutput(sessionId, generation, npcName) ?? false;
        }

        /// <summary>
        /// ★ GMCM 配置保存时调用：当 EnableAmbientBarks 或 EnableA2A 被关闭时，
        /// 立即清理残留状态，实现零延迟即时生效。
        /// </summary>
        public static void CleanupOnConfigToggle()
        {
            // ★ 总开关关闭：全量重置对话系统（含输出队列与 EchoStore），立即静音
            if (!Config.EnableMod)
            {
                _dialogueCoordinator?.ResetAllDialogueState("EnableMod disabled");
                SMonitor?.Log("[ModEntry] EnableMod disabled — all dialogue state reset.", LogLevel.Debug);
                return;
            }

            // ── 关闭时：立即清理进行中的对话与残余状态 ──
            if (!Config.EnableAmbientBarks)
            {
                _dialogueCoordinator?.AmbientBark.CleanupAll();
                SMonitor?.Log("[ModEntry] EnableAmbientBarks disabled — AmbientBark state cleaned up.", LogLevel.Debug);
            }

            if (!Config.EnableA2A)
            {
                _dialogueCoordinator?.A2A.SessionManager.CancelAll("Config disabled A2A", applyCooldown: false);
                SMonitor?.Log("[ModEntry] EnableA2A disabled — all A2A sessions cancelled.", LogLevel.Debug);
            }

            if (Config.EnableAmbientBarks)
            {
                // ★ 重新开启（或每次 save）：无条件软复位，抹除 NPC 残留的 CooldownTicksRemaining 冷却。
                // CleanupAll 是幂等的——若已清理过则无副作用。确保玩家关闭后再开启时，
                // 雷达扫描不会因 IsInCooldown() 而跳过 NPC。
                _dialogueCoordinator?.AmbientBark.CleanupAll();
                SMonitor?.Log("[ModEntry] EnableAmbientBarks on save — soft reset for clean restart.", LogLevel.Debug);
            }

            // A2A 重新开启无需额外操作：CancelAll 已清除会话，雷达会在下次 Tick 自然发现新相遇。
        }

        /// <summary>
        /// 暴露 DialogueCoordinator 引用，供 GMCM 配置菜单在开关切换时执行清理/复位。
        /// Config 读取现已改为实时（AmbientBarkModule.Config => ModEntry.Config），无需重建。
        /// </summary>
        internal static DialogueCoordinator Coordinator => _dialogueCoordinator;

        /// <summary>
        /// A2A output validator implementation for MainThreadOutputQueue.
        /// </summary>
        internal sealed class A2AOutputValidatorImpl : IA2AOutputValidator
        {
            public bool Validate(string sessionId, int generation, string npcName)
            {
                return A2AOutputValidator(sessionId, generation, npcName);
            }
        }

        /// <summary>
        /// Harmony instance saved as a member so it can be unpatched on exit.
        /// </summary>
        private static Harmony _harmony;

        /// <summary>
        /// 对话协调器：唯一允许订阅 SMAPI GameLoop 事件的对象。
        /// </summary>
        private static DialogueCoordinator _dialogueCoordinator;

        /// <summary>
        /// Flag to ensure Harmony patches are only applied once per game process.
        /// </summary>
        private static bool _hasPatched = false;

        /// <summary>
        /// Indicates whether the mod has been initialized (to prevent duplicate subscriptions).
        /// </summary>
        private static bool _isInitialized = false;

        /// <summary>
        /// Cancel button plugin instance.
        /// </summary>
        private static CancelButtonPlugin _cancelButtonPlugin;

        private int _lastDialogueCloseTick = -9999;

        /// <summary>最近一次对话的 NPC（内部名 Name 用于记忆键）。跨存档必须在 OnSaveLoaded/Cleanup 置空。</summary>
        public static NPC LastSpokenNPC { get; internal set; }

        /// <summary>
        /// Exposes the cancel button plugin instance so Character can register itself as active.
        /// </summary>
        internal static CancelButtonPlugin CancelButtonPluginInstance => _cancelButtonPlugin;

        public static Dictionary<string, Type> LlmMap
        {
            get
            {
                if (_llmMap == null)
                {
                    // Build dictionary of LLM types (things that inherit from the LLM class)
                    _llmMap = new Dictionary<string, Type>(StringComparer.InvariantCultureIgnoreCase)
                    {
#if DEBUG
                        { "Dummy", typeof(LlmDummy) },
#endif
                        { "LlamaCpp", typeof(LlmLlamaCpp) },
                        { "Google", typeof(LlmGemini) },
                        { "Anthropic", typeof(LlmClaude) },
                        { "OpenAI", typeof(LlmOpenAi) },
                        { "Mistral", typeof(LlmMistral) },
                        { "Grok", typeof(LlmGrok) },
                        { "DeepSeek", typeof(LlmDeepSeek) },
                        { "VolcEngine", typeof(LlmVolcEngine) },
                        { "OpenAiCompatible", typeof(LlmOAICompatible) },

                        // ★ 别名容错映射：防止历史配置或手滑输入引发 KeyNotFoundException 崩溃
                        { "LlmOAICompatible", typeof(LlmOAICompatible) },
                        { "LlmGemini", typeof(LlmGemini) },
                        { "LlmClaude", typeof(LlmClaude) },
                        { "LlmOpenAi", typeof(LlmOpenAi) },
                        { "LlmDeepSeek", typeof(LlmDeepSeek) },
                        { "LlmGrok", typeof(LlmGrok) }
                    };
                }

                return _llmMap;
            }
        }
        /// <summary>
        /// 存储未声明 permitAiUse 的第三方内容包 ID 集合。
        /// </summary>
        public static HashSet<string> DisallowedContentPackIds { get; private set; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 向后兼容属性，始终返回 false。已由 DisallowedContentPackIds + RespectAuthorAiConsent 替代。
        /// Character.cs 中的历史调用将正常走 StardewNpc.Dialogue 分支。
        /// </summary>
        [Obsolete("Use DisallowedContentPackIds and RespectAuthorAiConsent instead.")]
        public static bool BlockModdedContent => false;
        private static CultureInfo _locale;

        public static string Language
        {
            get
            {
                GetLocale();
                return _locale.DisplayName;
            }
        }

        public static IEnumerable<string> LanguageFileSuffixes
        {
            get
            {
                GetLocale();
                if (_locale != null && _locale.Name != "en-US")
                {
                    var workingLocal = _locale;
                    while (!string.IsNullOrEmpty(workingLocal?.Name))
                    {
                        yield return $".{workingLocal.Name}";
                        workingLocal = workingLocal.Parent;
                    }
                }

                yield return string.Empty;
            }
        }

        private static string _localeCache = string.Empty;

        private static void GetLocale()
        {
            if (_locale != null && SHelper.Translation.Locale == _localeCache) return;

            try
            {
                _locale = CultureInfo.GetCultureInfo(SHelper.Translation.Locale);
                _localeCache = SHelper.Translation.Locale;
            }
            catch (Exception)
            {
                _locale = null;
                _localeCache = string.Empty;
            }

            if (_locale == null)
            {
                _locale = CultureInfo.GetCultureInfo("en-US");
                _localeCache = SHelper.Translation.Locale;
            }
        }

        private static bool? _fixPunctuation = null;
        private static string _localeCacheFixPunctuation = string.Empty;
        private static Dictionary<string, Type> _llmMap;

        public static bool FixPunctuation
        {
            get
            {
                if (_fixPunctuation == null || _localeCacheFixPunctuation != SHelper.Translation.Locale)
                {
                    var suffixes = LanguageFileSuffixes.ToList();
                    _fixPunctuation = suffixes.Count == 1 || suffixes.Any(x =>
                        x == ".en" || x == ".fr" || x == ".de" || x == ".es" || x == ".tr" || x == ".pt" ||
                        x == ".it" || x == ".nl" || x == ".pl" || x == ".id");
                    _localeCacheFixPunctuation = SHelper.Translation.Locale;
                }

                return _fixPunctuation.Value;
            }
        }

        public override object GetApi()
        {
            return new ValleyTalkInterface();
        }

        public override void Entry(IModHelper helper)
        {
            SHelper = helper;
            SMonitor = Monitor;

            // ★ 统一配偶查询服务（多婚/原版配偶判定、住所定位）
            SpouseQueryService.Instance.Initialize(helper);

            // 1. 先清理旧状态（如果已初始化过）
            if (_isInitialized)
            {
                Cleanup();
            }

            // 2. 再初始化新状态
            // 约会地点配置需最先加载（DateManager 的地点白名单校验依赖它）
            DateLocationRegistry.Initialize(helper);
            DateManager.Instance.Initialize(helper);
            InvitationManager.Instance.Initialize(helper);
            MemoryManager.Instance.Initialize(helper);
            WorldMemoryManager.Instance.Initialize(helper);
            MovementManager.Instance.Initialize(helper);
            RelationshipMilestoneManager.Instance.Initialize(Helper, Monitor);

            // Subscribe to game lifecycle events（DialogueCoordinator 将订阅 GameLoop 事件）
            helper.Events.GameLoop.SaveLoaded += OnSaveLoaded;
            helper.Events.Display.MenuChanged += OnMenuChanged;

            // ModEntry 保留 DayStarted 和 ReturnedToTitle 订阅（清理非对话系统状态）
            helper.Events.GameLoop.DayStarted += OnDayStarted;
            helper.Events.GameLoop.ReturnedToTitle += OnReturnedToTitle;

            // 🌟 Agent tool dispatcher thread-safe queue: process pending actions on main thread
            helper.Events.GameLoop.UpdateTicked += OnUpdateTicked;

            // 拦截外部输入，防止打字时触发其他MOD的热键
            helper.Events.Input.ButtonPressed += OnButtonPressed;

            // ★ 取消跟随热键（与 QuickReplyKey 共用 ButtonPressed 事件，在 OnButtonPressed 内部分流）
            helper.Events.Input.ButtonPressed += OnDismissFollowerButtonPressed;

            // ★ 监听 CP 热重载（使用规范的 AssetsInvalidated 事件）
            helper.Events.Content.AssetsInvalidated += OnAssetsInvalidated;

           Config = Helper.ReadConfig<ModConfig>();

            // ── 老版本配置自动迁移（一次性执行） ──
            if (Config.ProviderProfiles == null || Config.ProviderProfiles.Count == 0)
            {
                Monitor.Log("[ModEntry] 检测到老版本配置，正在迁移到 ProviderProfile 架构...", LogLevel.Info);

                Config.ProviderProfiles = new System.Collections.Generic.Dictionary<string, ProviderProfile>(StringComparer.OrdinalIgnoreCase);

                // 从私有备份字段读取旧值（通过反射）
                var configType = typeof(ModConfig);
                var legacyApiKey = configType.GetField("_legacyApiKey",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(Config) as string;
                var legacyServerAddress = configType.GetField("_legacyServerAddress",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(Config) as string;
                var legacyModelName = configType.GetField("_legacyModelName",
                    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)?.GetValue(Config) as string;

                // ★ 这里做一次别名判断：不管是 OpenAiCompatible 还是 LlmOAICompatible 都能填上 OpenRouter 默认端点
                bool isOpenAiCompat = string.Equals(Config.Provider, "OpenAiCompatible", StringComparison.OrdinalIgnoreCase) 
                                   || string.Equals(Config.Provider, "LlmOAICompatible", StringComparison.OrdinalIgnoreCase);

                var currentProfile = new ProviderProfile
                {
                    ApiKey = legacyApiKey ?? string.Empty,
                    ServerAddress = legacyServerAddress ?? (isOpenAiCompat
                        ? "https://openrouter.ai/api/v1"
                        : string.Empty),
                    ModelName = legacyModelName ?? string.Empty,
                    CustomBodyJson = string.Empty
                };

                Config.ProviderProfiles[Config.Provider] = currentProfile;
                Helper.WriteConfig(Config);

                // ★ 原汁原味的完整 Log，完全保留！
                Monitor.Log($"[ModEntry] 已迁移 Provider={Config.Provider} 的配置（ApiKey={!string.IsNullOrEmpty(currentProfile.ApiKey)}, ModelName={currentProfile.ModelName}）", LogLevel.Info);
            }

            // ── 高级参数边界校验（每次启动都执行） ──
            Config.ValidateDialogueConfig(Monitor);

            // Load cancel button plugin
            _cancelButtonPlugin = new CancelButtonPlugin(helper, Monitor);

            SMonitor = Monitor;

            // 初始化基础设施（无论开关状态如何都保持初始化，以便后续随时开启）
            TextInputManager.Initialize(helper.Events);
            Log.Initialize(Monitor);

            // 注册 Agent 工具调试控制台命令
            RegisterDebugConsoleCommands(helper);

            // Ensure Harmony PatchAll is executed strictly ONCE per process session
            if (!_hasPatched)
            {
                _harmony = new Harmony(ModManifest.UniqueID);
                _harmony.PatchAll();
                TextBoxCursorEnhancer.ApplyPatches(_harmony, this.Helper);
                _hasPatched = true;
                Log.Debug("[ValleyTalkReborn] Harmony patches applied successfully.");
            }

            _isInitialized = true;

            // Initialize SpouseWaitingEvent (伴侣深夜等待事件)
            SpouseWaitingEvent.Initialize();

            // 注册夜间记忆固化系统
            NightlyConsolidationHook.Register(helper);

            // 加载/刷新模组的核心功能模块
            OnConfigChanged();

            // ★ 装配对话协调器（唯一 SMAPI GameLoop 事件订阅入口）
            Config = Helper.ReadConfig<ModConfig>();
            Config.ValidateDialogueConfig(Monitor);

            InitializeDialogueCoordinator();

            helper.Events.GameLoop.GameLaunched += OnGameLaunched;

            Log.Debug($"[{DateTime.Now}] Mod loaded");
        }

        /// <summary>
        /// 装配对话协调器：构建所有模块、绑定 DynamicBarkManager 并订阅 SMAPI 事件。
        /// 在 Entry 与 OnSaveLoaded 中复用，确保返回标题后重新读档时协调器被复活。
        /// </summary>
        private void InitializeDialogueCoordinator()
        {
            _dialogueCoordinator?.Unsubscribe();

            var npcReservations = new NpcReservationService();
            var outputQueue = new MainThreadOutputQueue(new A2AOutputValidatorImpl());
            var llmGateway = new LlmRequestGateway(Config.LlmTimeoutSeconds);

            var ambientBarkStateStore = new AmbientBarkStateStore();
            var barkPromptBuilder = new BarkPromptBuilder(ambientBarkStateStore);
            var ambientBarkModule = new AmbientBarkModule(
                ambientBarkStateStore,
                barkPromptBuilder,
                npcReservations,
                outputQueue,
                llmGateway,
                Config);

            var a2aPromptBuilder = new A2APromptBuilder();
            var a2aSessionManager = new A2ASessionManager(
                npcReservations, outputQueue, llmGateway, a2aPromptBuilder);
            var a2aModule = new A2AModule(a2aSessionManager);

            _dialogueCoordinator = new DialogueCoordinator(
                Helper, Monitor, ambientBarkModule, a2aModule, outputQueue, npcReservations);

            DynamicBarkManager.BindCoordinator(_dialogueCoordinator);
            _dialogueCoordinator.Subscribe();
        }

        /// <summary>
        /// 当玩家在 GMCM 保存配置或重新加载配置时调用的处理逻辑
        /// </summary>
        public static void OnConfigChanged()
        {
            Config = SHelper.ReadConfig<ModConfig>();

            DialogueBuilder.Instance.Config = Config;

            if (!Config.EnableMod)
            {
                Log.Debug("[ValleyTalkReborn] 模组当前已关闭，对话系统将实时切回原生模式。");
                return;
            }

#if DEBUG
            if (Config.Debug)
            {
                Log.Debug("###############################################");
                Log.Debug("###############################################");
                Log.Debug("###############################################");
            }
#endif

            if (!LlmMap.TryGetValue(Config.Provider, out var llmType))
            {
                Log.Error($"Invalid LLM type: {Config.Provider}");
                return;
            }

            Llm.SetLlm(llmType, modelName: Config.ModelName, apiKey: Config.ApiKey, url: Config.ServerAddress,
                promptFormat: Config.PromptFormat);

            CheckContentPacks();

            // Initialize Action Awareness System
            try
            {
                EatSubscriber.Initialize();
                FishSubscriber.Initialize();
                WorldSubscriber.Initialize();
                HarvestSubscriber.Initialize();
                TalkSubscriber.Initialize();
                GiftSubscriber.Initialize();

                MassGiftTracker.Initialize();
                ConsecutiveTalkTracker.Initialize();
                ExtremeActivityTracker.Initialize();
                DailyHeadlineGenerator.Initialize();

                TrashCanTracker.Initialize(_harmony);

                Log.Debug("[ValleyTalkReborn] Action Awareness System initialized.");
            }
            catch (Exception ex)
            {
                Log.Error($"[ValleyTalkReborn] Error initializing Action Awareness System: {ex.Message}");
            }

            Log.Debug("[ValleyTalkReborn] Module enabled; LLM and module configurations updated.");
        }

        /// <summary>
        /// 注册用于测试 Agent 接口与物理分发器的 SMAPI 控制台指令
        /// </summary>
        private void RegisterDebugConsoleCommands(IModHelper helper)
        {
            helper.ConsoleCommands.Add("vt_test_llm_tools", "测试大模型 Native Tool Calling 是否正确返回 JSON",
                async (cmd, args) =>
                {
                    string npcName = args.Length > 0 ? args[0] : "Abigail";
                    Monitor.Log($"[Test] 正在向大模型发送约会测试请求（Target: {npcName}）...", LogLevel.Info);

                    var systemPrompt = $"You are {npcName} from Stardew Valley. Speak in character.";
                    var userPrompt = "Hey, do you want to go on a date with me at the Saloon tonight at 20:00?";

                    var response = await Llm.Instance.RunInference(systemPrompt, "", "", userPrompt);

                    Monitor.Log($"[LLM 文本回应]: {response.Text}", LogLevel.Info);
                    Monitor.Log($"[LLM 解析到的工具调用数量]: {response.ToolCalls?.Count ?? 0}", LogLevel.Info);

                    if (response.ToolCalls != null && response.ToolCalls.Count > 0)
                    {
                        foreach (var tool in response.ToolCalls)
                        {
                            Monitor.Log($"  -> 工具名: {tool.FunctionName}", LogLevel.Warn);
                            Monitor.Log($"  -> 参数: {tool.JsonArguments}", LogLevel.Warn);
                        }
                    }
                });

        // ── Promise 管理控制台命令（MEM-08 新增）──
        helper.ConsoleCommands.Add("vt_promises", "查看指定 NPC 的待履约约定。用法: vt_promises <npcName>",
            async (cmd, args) =>
            {
                if (args.Length < 1)
                {
                    Monitor.Log("[用法] vt_promises <npcName>  例: vt_promises Abigail", LogLevel.Info);
                    return;
                }

                string npcName = args[0];

                await MainThreadDispatcher.RunOnMainThreadAsync(() =>
                {
                    var promises = MemoryManager.Instance.GetActivePromises(npcName);
                    if (promises == null || promises.Count == 0)
                    {
                        Monitor.Log($"[{npcName}] 无待履约约定。", LogLevel.Info);
                        return;
                    }

                    Monitor.Log($"[{npcName}] 待履约约定 ({promises.Count} 条):", LogLevel.Info);
                    foreach (var p in promises)
                    {
                        Monitor.Log($"id={p.Id} | day={p.TargetDayHint} | loc={p.TriggerLocation} | imp={p.Importance} | {p.Content}", LogLevel.Info);
                    }
                });
            });

        helper.ConsoleCommands.Add("vt_fulfill", "标记指定 Promise 为已履约。用法: vt_fulfill <npcName> <memoryId>",
            async (cmd, args) =>
            {
                if (args.Length < 2)
                {
                    Monitor.Log("[用法] vt_fulfill <npcName> <memoryId>  例: vt_fulfill Abigail abc-123", LogLevel.Info);
                    return;
                }

                string npcName = args[0];
                string memoryId = args[1];

                await MainThreadDispatcher.RunOnMainThreadAsync(() =>
                {
                    bool ok = MemoryManager.Instance.MarkPromiseFulfilled(npcName, memoryId);
                    if (ok)
                        Monitor.Log($"已标记履约: [{npcName}] {memoryId}", LogLevel.Info);
                    else
                        Monitor.Log($"未找到约定 {memoryId} (NPC: {npcName})", LogLevel.Warn);
                });
            });

        helper.ConsoleCommands.Add("vt_extract_test", "测试记忆提取服务。用法: vt_extract_test <npcName>",
            async (cmd, args) =>
            {
                string npcName = args.Length > 0 ? args[0] : "Abigail";

                string npcDisplayName = npcName;
                await MainThreadDispatcher.RunOnMainThreadAsync(() =>
                {
                    var npc = Game1.getCharacterFromName(npcName);
                    if (npc != null && !string.IsNullOrWhiteSpace(npc.displayName))
                        npcDisplayName = npc.displayName;
                });

                var existingManual = MemoryManager.Instance.GetMemories(npcName)
                    .Where(m => m.Source == "Manual")
                    .Select(m => m.Content)
                    .ToList();

                Monitor.Log($"[Test] Running MemoryExtractService for [{npcName}] (display: {npcDisplayName})...", LogLevel.Info);

                var result = await MemoryExtractService.ExtractAsync(
                    npcName,
                    npcDisplayName,
                    existingManual,
                    System.Threading.CancellationToken.None);

                Monitor.Log($"[Test] status={result.Status}; candidates={result.Candidates.Count}; error={result.ErrorDetail}", LogLevel.Info);
            });
        }

        /// <summary>
        /// Intercepts and suppresses keyboard input when our custom text box is active.
        /// </summary>
        [EventPriority(EventPriority.High)]
        private void OnButtonPressed(object sender, ButtonPressedEventArgs e)
        {
            if (!Config.EnableMod) return;
            // 在任何点击事件触发时，记录此刻 ALT 键是否按下
            // 必须在这里记录，因为 checkAction 执行时 ALT 状态已丢失
            if (e.Button == SButton.MouseRight || e.Button == SButton.MouseLeft)
            {
                NPC_CheckAction_Patch.TriggerKeyWasDown = NPC_CheckAction_Patch.IsTriggerKeyDown();
            }

            // 快捷键追问判定逻辑（原有代码不动）
            if (Context.IsPlayerFree && e.Button == Config.QuickReplyKey)
            {
                int tickDiff = Game1.ticks - _lastDialogueCloseTick;

                if (tickDiff > 0 && tickDiff <= 300 && LastSpokenNPC != null)
                {
                    // ★ 确保目标 NPC 具备 AI 对话资格：无有效 Bios 时不弹输入框、不发请求。
                    // 注意：此处直接 return 且不 Suppress，将按键交还原版处理。
                    if (!DialogueBuilder.Instance.PatchNpc(LastSpokenNPC))
                        return;
                    bool sameLocation = Game1.player.currentLocation == LastSpokenNPC.currentLocation;
                    float distance = sameLocation
                        ? Vector2.Distance(Game1.player.Position, LastSpokenNPC.Position)
                        : float.MaxValue;

                    if (!sameLocation || distance > 256f)
                    {
                        Game1.addHUDMessage(new HUDMessage(
                            I18n.Follower.TooFarAway(LastSpokenNPC.displayName), 3));
                    }
                    else
                    {
                        Game1.playSound("bigSelect");
                        TextInputManager.RequestTextInput($"与 {LastSpokenNPC.displayName} 交谈", LastSpokenNPC);
                    }

                    Helper.Input.Suppress(e.Button);
                    return;
                }
            }

            if (e.Button == Config.OpenHubMenuKey
                && Context.IsPlayerFree
                && Game1.activeClickableMenu == null
                && Game1.keyboardDispatcher?.Subscriber is not DialogueTextInputBox)
            {
                Helper.Input.Suppress(e.Button);
                OpenHubMenu(0);
                return;
            }

            if (Game1.keyboardDispatcher?.Subscriber is DialogueTextInputBox)
            {
                bool isCtrlPressed =
                    Game1.input.GetKeyboardState().IsKeyDown(Microsoft.Xna.Framework.Input.Keys.LeftControl) ||
                    Game1.input.GetKeyboardState().IsKeyDown(Microsoft.Xna.Framework.Input.Keys.RightControl);

                if (e.Button == SButton.Escape || e.Button == SButton.Enter ||
                    e.Button == SButton.Back || e.Button == SButton.Delete ||
                    e.Button == SButton.Left || e.Button == SButton.Right ||
                    e.Button == SButton.Up || e.Button == SButton.Down ||
                    e.Button == SButton.LeftControl || e.Button == SButton.RightControl ||
                    e.Button == SButton.LeftShift || e.Button == SButton.RightShift)
                {
                    return;
                }

                if (isCtrlPressed && (e.Button == SButton.C || e.Button == SButton.V || e.Button == SButton.X ||
                                      e.Button == SButton.A || e.Button == SButton.Z))
                {
                    return;
                }

                if (e.Button.TryGetKeyboard(out Microsoft.Xna.Framework.Input.Keys _))
                {
                    Helper.Input.Suppress(e.Button);
                }
            }
        }

        /// <summary>
        /// 取消跟随热键处理：弹出确认框 → 气泡反馈 → 调用 MovementManager.StopFollow。
        /// 普通跟随走 BeginSmoothDeparture；约会跟随走 StopDateFollow + EndDateGracefully。
        /// </summary>
        internal static void OpenHubMenu(int targetTab)
        {
            if (!Context.IsWorldReady || Game1.activeClickableMenu != null) return;

            targetTab = Math.Clamp(targetTab, 0, 3);

            string npcName = null;

            // a) 优先使用最近对话的 NPC
            if (LastSpokenNPC != null && !string.IsNullOrWhiteSpace(LastSpokenNPC.Name))
            {
                npcName = LastSpokenNPC.Name;
            }
            else
            {
                // b) 回退：按字母序取第一个有效村民
                foreach (var name in Game1.player.friendshipData.Keys
                             .OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
                {
                    if (Game1.getCharacterFromName(name) != null)
                    {
                        npcName = name;
                        break;
                    }
                }
                // c) 仍无 → npcName 保持 null（Hub 空态）
            }

            Game1.playSound("bigSelect");
            Game1.activeClickableMenu = new IntegratedHubMenu(npcName, targetTab);
            SMonitor.Log($"[ModEntry] Hub opened (tab={targetTab}, npc={npcName ?? "none"})", LogLevel.Trace);
        }

        private void OnDismissFollowerButtonPressed(object sender, ButtonPressedEventArgs e)
        {
            if (!Config.EnableMod) return;

            if (!Context.IsWorldReady || !Context.IsPlayerFree)
                return;

            if (e.Button != Config.DismissFollowerKey)
                return;

            var movement = MovementManager.Instance;
            if (movement.HasActiveFollow)
            {
                Helper.Input.Suppress(e.Button);
                TryShowDismissConfirmation(movement);
                return;
            }

            TryRecruitFacingNpc();
        }

        /// <summary>
        /// 查找玩家正前方可招募的 NPC（面对面快捷招募用）。
        /// </summary>
        private static NPC FindRecruitableFacingNpc()
        {
            var loc = Game1.currentLocation;
            if (loc == null) return null;

            var characters = loc.characters;
            if (characters == null || characters.Count == 0) return null;

            var player = Game1.player;
            var baseTile = player.Tile;

            // 手工计算前方格子（不使用未经验证的 Utility.getFacingDirectionTile）
            Microsoft.Xna.Framework.Vector2 frontTile = player.FacingDirection switch
            {
                0 => new Microsoft.Xna.Framework.Vector2(baseTile.X, baseTile.Y - 1), // Up
                1 => new Microsoft.Xna.Framework.Vector2(baseTile.X + 1, baseTile.Y), // Right
                2 => new Microsoft.Xna.Framework.Vector2(baseTile.X, baseTile.Y + 1), // Down
                3 => new Microsoft.Xna.Framework.Vector2(baseTile.X - 1, baseTile.Y), // Left
                _ => baseTile
            };

            var toolLoc = player.GetToolLocation();
            var toolTile = new Microsoft.Xna.Framework.Vector2((int)(toolLoc.X / 64f), (int)(toolLoc.Y / 64f));

            NPC best = null;
            float bestDistSq = float.MaxValue;

            foreach (var npc in characters)
            {
                if (npc == null || !npc.IsVillager || DialogueUtilities.IsNpcSleeping(npc))
                    continue;

                if (npc.Tile == frontTile || npc.Tile == toolTile
                    || Microsoft.Xna.Framework.Vector2.Distance(npc.Tile, frontTile) <= 1.2f)
                {
                    float distSq = DialogueUtilities.DistanceSqToPlayer(npc);
                    if (distSq < bestDistSq)
                    {
                        bestDistSq = distSq;
                        best = npc;
                    }
                }
            }

            return best;
        }

        /// <summary>
        /// 面对面快捷招募 NPC 跟随（热键 G 无活跃跟随时触发）。
        /// </summary>
        private void TryRecruitFacingNpc()
        {
            if (!Context.IsWorldReady || !Context.IsPlayerFree || Game1.currentLocation == null)
                return;

            if (!ModEntry.Config.EnableMod)
                return;

            var target = FindRecruitableFacingNpc();
            if (target == null) return;

            Helper.Input.Suppress(Config.DismissFollowerKey);

            // 忙碌守卫
            if (MovementManager.Instance.IsNpcMoving(target) || target.controller != null)
            {
                Game1.addHUDMessage(new HUDMessage(
                    I18n.Follower.BusyHud(target.displayName), 3));
                return;
            }

            // 好感度门槛
            bool isExempt = SpouseQueryService.Instance.IsMarried(target.Name);
            int hearts = 0;
            if (Game1.player.friendshipData.TryGetValue(target.Name, out var fs) && fs != null)
            {
                if (fs.IsDating() || fs.IsRoommate() || fs.IsMarried()) isExempt = true;
                hearts = fs.Points / 250;
            }

            if (!isExempt && hearts < 4)
            {
                Game1.showRedMessage(
                    I18n.Follower.NotFamiliar(target.displayName));
                try { target.doEmote(28); } catch { }
                try { Game1.playSound("cancel"); } catch { }
                return;
            }

            // CSM 上下文收口
            CompanionScheduleManager.Instance.ClearScheduleForOverride("HotkeyRecruited", target.Name);

            // 面向对齐
            target.faceGeneralDirection(Game1.player.getStandingPosition());
            Game1.player.faceGeneralDirection(target.getStandingPosition());

            // 统一启动
            string hud = I18n.Follower.StartFollowingHud(
                target.displayName, Config.DismissFollowerKey.ToString());
            DialogueBuilder.TryStartFollowForContext(target, hud);
        }

        private static void TryShowDismissConfirmation(MovementManager movement)
        {
            NPC npc = movement.CurrentFollowingNpc;
            if (npc == null)
                return;

            bool isDate = movement.HasActiveDateFollow;
            string prompt = isDate
                ? I18n.Follower.DismissDateConfirm(npc.displayName)
                : I18n.Follower.DismissFollowConfirm(npc.displayName);

            var responses = new Response[]
            {
                new Response("Yes", I18n.Follower.ConfirmYes()),
                new Response("No",  I18n.Follower.ConfirmNo())
            };

            Game1.currentLocation.createQuestionDialogue(
                prompt,
                responses,
                (farmer, answerKey) =>
                {
                    if (answerKey != "Yes")
                        return;

                    // ★ 二次确认：弹窗期间跟随可能已被其他路径终止（如时间到期、NPC 对话等），
                    // 此时 CurrentFollowingNpc 已变，旧 npc 引用不再有效，必须拦截。
                    if (MovementManager.Instance.CurrentFollowingNpc != npc)
                        return;

                    DismissFollower(npc, isDate);
                }
            );
        }

        private static void DismissFollower(NPC npc, bool isDate)
        {
            // 头顶气泡反馈：非阻塞、不等 LLM；NPC 不在视野内时静默无害
            if (isDate)
            {
                npc.showTextAboveHead(I18n.Follower.DateLeaveHeadText());
                npc.doEmote(24);
            }
            else
            {
                npc.showTextAboveHead(I18n.Follower.FollowLeaveHeadText());
                npc.doEmote(32);
            }

            // 核心释放：
            // - 普通跟随 → StopFollowInternal → BeginSmoothDeparture → ResumeScheduleAfterFollow
            // - 约会跟随 → StopDateFollow → DateManager.EndDateGracefully（结算 Session，回家）
            MovementManager.Instance.StopFollow(npc);
        }

        /// <summary>
        /// Performs comprehensive cleanup of all static resources, event subscriptions.
        /// </summary>
        private void Cleanup()
        {
            try
            {
                _dialogueCoordinator?.Unsubscribe();
                _dialogueCoordinator = null;
            }
            catch (Exception ex)
            {
                Log.Error($"[ValleyTalkReborn] Error unsubscribing DialogueCoordinator: {ex.Message}");
            }

            try
            {
                try
                {
                    DialogueHistoryManager.Instance?.SaveSync();
                    Log.Debug("[ValleyTalkReborn] Dialogue history saved during cleanup.");
                }
                catch (Exception ex)
                {
                    Log.Error($"[ValleyTalkReborn] Error saving dialogue history during cleanup: {ex.Message}");
                }

                try
                {
                    _harmony = null;
                    Log.Debug(
                        "[ValleyTalkReborn] Harmony instance cleared, but patches retained to prevent AccessViolationException.");
                }
                catch (Exception ex)
                {
                    Log.Error($"[ValleyTalkReborn] Error clearing Harmony reference: {ex.Message}");
                }

                try
                {
                    MemoryManager.Instance?.Cleanup();
                }
                catch (Exception ex)
                {
                    Log.Error($"[ValleyTalkReborn] Error cleaning MemoryManager: {ex.Message}");
                }

                // ★ 清理 PendingTopicManager（跨存档卫生，修复 A1-1/A3-1）
                try
                {
                    PendingTopicManager.Instance?.Cleanup();
                }
                catch (Exception ex)
                {
                    Log.Error($"[ValleyTalkReborn] Error cleaning PendingTopicManager: {ex.Message}");
                }

                // ★ 清理 EvolvedTraitManager（标题期重置，修复折叠链废除后的状态残留）
                try
                {
                    EvolvedTraitManager.Reset();
                }
                catch (Exception ex)
                {
                    Log.Error($"[ValleyTalkReborn] Error resetting EvolvedTraitManager: {ex.Message}");
                }

                try
                {
                    MovementManager.Instance?.Cleanup(SHelper);
                }
                catch (Exception ex)
                {
                    Log.Error($"[ValleyTalkReborn] Error cleaning MovementManager: {ex.Message}");
                }

                try
                {
                    TextInputManager.Cleanup(SHelper.Events);
                }
                catch (Exception ex)
                {
                    Log.Error($"[ValleyTalkReborn] Error cleaning TextInputManager: {ex.Message}");
                }

                try
                {
                    AsyncBuilder.Instance?.Cleanup();
                }
                catch (Exception ex)
                {
                    Log.Error($"[ValleyTalkReborn] Error cleaning AsyncBuilder: {ex.Message}");
                }

                try
                {
                    DialogueHistoryManager.Instance?.Cleanup();
                }
                catch (Exception ex)
                {
                    Log.Error($"[ValleyTalkReborn] Error cleaning DialogueHistoryManager: {ex.Message}");
                }

                try
                {
                    DialogueBuilder.Instance?.Cleanup();
                }
                catch (Exception ex)
                {
                    Log.Error($"[ValleyTalkReborn] Error cleaning DialogueBuilder: {ex.Message}");
                }

                try
                {
                    ModInteropManager.Instance?.Cleanup();
                }
                catch (Exception ex)
                {
                    Log.Error($"[ValleyTalkReborn] Error cleaning ModInteropManager: {ex.Message}");
                }

                try
                {
                    EatSubscriber.Cleanup();
                    FishSubscriber.Cleanup();
                    WorldSubscriber.Cleanup();
                    HarvestSubscriber.Cleanup();
                    TalkSubscriber.Cleanup();
                    GiftSubscriber.Cleanup();

                    MassGiftTracker.Cleanup();
                    ConsecutiveTalkTracker.Cleanup();
                    ExtremeActivityTracker.Cleanup();
                    DailyHeadlineGenerator.Cleanup();

                    PerceptionManager.Instance?.Cleanup();
                    PlayerStateScanner.ResetOnSaveExit();
                    SpouseWaitingEvent.Cleanup();
                }
                catch (Exception ex)
                {
                    Log.Error($"[ValleyTalkReborn] Error cleaning Action Awareness System: {ex.Message}");
                }

                try
                {
                    WorldMemoryManager.Instance?.Cleanup();
                }
                catch (Exception ex)
                {
                    Log.Error($"[ValleyTalkReborn] Error cleaning WorldMemoryManager: {ex.Message}");
                }

                // ★ 清理关系注册表缓存
                try
                {
                    NpcRelationRegistry.Instance.Cleanup();
                }
                catch (Exception ex)
                {
                    Log.Error($"[ValleyTalkReborn] Error cleaning NpcRelationRegistry: {ex.Message}");
                }

                // ★ 清理伴侣日程管理器资产与状态缓存
                try
                {
                    CompanionScheduleManager.Instance.Cleanup();
                }
                catch (Exception ex)
                {
                    Log.Error($"[ValleyTalkReborn] Error cleaning CompanionScheduleManager: {ex.Message}");
                }

                try
                {
                    RelationshipMilestoneManager.Instance.Cleanup();
                }
                catch (Exception ex)
                {
                    Log.Error("[ValleyTalkReborn] Error cleaning RelationshipMilestoneManager: " + ex.Message);
                }

                // ★ 清理 DateManager（取消事件订阅并重置状态）
                try
                {
                    DateManager.Instance?.Cleanup(SHelper);
                }
                catch (Exception ex)
                {
                    Log.Error($"[ValleyTalkReborn] Error cleaning DateManager: {ex.Message}");
                }

                // ★ 清理 InvitationManager（取消事件订阅）
                try
                {
                    InvitationManager.Instance?.Cleanup(SHelper);
                }
                catch (Exception ex)
                {
                    Log.Error($"[ValleyTalkReborn] Error cleaning InvitationManager: {ex.Message}");
                }

                try
                {
                    _llmMap = null;
                    _fixPunctuation = null;
                    _locale = null;
                    _localeCache = string.Empty;
                    _localeCacheFixPunctuation = string.Empty;
                    DisallowedContentPackIds.Clear();
                    LastSpokenNPC = null;
                }
                catch (Exception ex)
                {
                    Log.Error($"[ValleyTalkReborn] Error resetting ModEntry statics: {ex.Message}");
                }

                try
                {
                    RecentConversationTracker.Clear();
                }
                catch (Exception ex)
                {
                    Log.Error($"[ValleyTalkReborn] Error clearing RecentConversationTracker: {ex.Message}");
                }

                try
                {
                    _cancelButtonPlugin?.Dispose();
                    _cancelButtonPlugin = null;
                }
                catch (Exception ex)
                {
                    Log.Error($"[ValleyTalkReborn] Error disposing cancel button plugin: {ex.Message}");
                }

                Log.Cleanup();
                _isInitialized = false;

                Log.Debug("[ValleyTalkReborn] Full cleanup completed.");
            }
            catch (Exception ex)
            {
                try
                {
                    Log.Error($"[ValleyTalkReborn] Critical error during cleanup: {ex.Message}");
                }
                catch
                {
                }
            }
        }

        internal static void CheckContentPacks()
        {
            DisallowedContentPackIds.Clear();

            // 若玩家在 GMCM 中选择自主接管本地控制权，跳过扫描与限制
            if (!Config.RespectAuthorAiConsent)
            {
                SMonitor.Log(Util.GetString("logPermitAiUseBypassed"), LogLevel.Info);
                return;
            }

            var contentPacks = SHelper.ModRegistry.GetAll().Where(p => p.IsContentPack).ToList();
            var blockedContentPacks = contentPacks
                .Where(p => !SldConstants.PermitListContentPacks.Contains(p.Manifest.UniqueID))
                .Where(p =>
                    p.Manifest.ExtraFields == null ||
                    !p.Manifest.ExtraFields.TryGetValue("PermitAiUse", out var val) ||
                    !(val as bool? ?? false)
                )
                .ToList();

            if (blockedContentPacks.Any())
            {
                foreach (var pack in blockedContentPacks)
                {
                    DisallowedContentPackIds.Add(pack.Manifest.UniqueID);
                }

                SMonitor.Log(
                    Util.GetString("logPermitAiUseBlocked", new { count = blockedContentPacks.Count }),
                    LogLevel.Info
                );
                string packNames = string.Join(", ", blockedContentPacks.Select(p => p.Manifest.Name));
                SMonitor.Log(
                    Util.GetString("logPermitAiUseList", new { list = packNames }),
                    LogLevel.Trace
                );
                SMonitor.Log(Util.GetString("logPermitAiUseHint"), LogLevel.Info);
            }
        }

        private void OnGameLaunched(object sender, GameLaunchedEventArgs e)
        {
            ModConfigMenu.Register(this);
            SpouseQueryService.Instance.ResolveApis();
            _dialogueCoordinator?.Subscribe();
        }

        /// <summary>
        /// 当 CP 资源被其他 Mod 或热重载刷新失效时，自动重新加载所有关联数据
        /// </summary>
        private void OnAssetsInvalidated(object sender, AssetsInvalidatedEventArgs e)
        {
            // 刷新 NPC 关系表
            if (e.NamesWithoutLocale.Any(an => an.IsEquivalentTo("ValleytalkReborn/NpcRelations")))
            {
                NpcRelationRegistry.Instance.Reload(Helper, Monitor);
            }

            // 刷新 POI 地点与 NPC 喜好资产表
            if (e.NamesWithoutLocale.Any(an => an.IsEquivalentTo("ValleytalkReborn/GlobalPoiAssets") ||
                                               an.IsEquivalentTo("ValleytalkReborn/NpcPreferences")))
            {
                CompanionScheduleManager.Instance.ReloadAssets();
            }

            // ★ CP 热重载时刷新约会地点表
            if (e.NamesWithoutLocale.Any(an => an.IsEquivalentTo(DateLocationRegistry.ASSET_KEY)))
            {
                DateLocationRegistry.ReloadAssets();
            }
        }

        private void OnMenuChanged(object sender, MenuChangedEventArgs e)
        {
            if (!Config.EnableMod) return;

            if (e.OldMenu is StardewValley.Menus.DialogueBox oldDb && e.NewMenu == null)
            {
                var speaker = oldDb.characterDialogue?.speaker ?? Game1.currentSpeaker;
                if (speaker != null)
                {
                    LastSpokenNPC = speaker;
                    _lastDialogueCloseTick = Game1.ticks;
                }

                // 修复：对话框关闭后清除 currentSpeaker，防止原版引擎误判为可对话
                if (Game1.currentSpeaker != null)
                {
                    Game1.currentSpeaker = null;
                }
            }
        }

        private void OnSaveLoaded(object sender, SaveLoadedEventArgs e)
        {
            LastSpokenNPC = null;

            // 修复：返回标题后 Cleanup 会销毁 _dialogueCoordinator，
            // 重新读档时必须重建并订阅，否则 A2A 雷达与 AmbientBark 状态机将永久停摆。
            InitializeDialogueCoordinator();

            // 修复：返回标题后 Cleanup 会取消 TextInputManager 的 UpdateTicked 订阅，
            // 重新读档时必须重新初始化，否则从对话选项触发的自定义回复会卡在 pending。
            TextInputManager.Initialize(Helper.Events);

            // 每次读档后重建 CancelButtonPlugin，防止 ReturnedToTitle 销毁后失效
            if (_cancelButtonPlugin == null)
            {
                _cancelButtonPlugin = new CancelButtonPlugin(Helper, Monitor);
            }

            DialogueHistoryManager.Instance.Load();
            RecentConversationTracker.Clear();
            SessionCache.Instance.ResetAll();
            EatSubscriber.Initialize();

            // ★ 存档加载完成后载入 NPC 关系
            NpcRelationRegistry.Instance.LoadAll(Helper, Monitor);

            // ★ 存档加载后从 CP 管道载入约会地点
            DateLocationRegistry.LoadAssets();
        }

        private void OnDayStarted(object sender, DayStartedEventArgs e)
        {
            PlayerStateScanner.OnDayStarted();
            RecentConversationTracker.Clear();
            //NPC_CurrentDialogue_Patch.ClearDedupState();
            NPC_CheckForNewCurrentDialogue_Patch.ClearDedupState();
            SessionCache.Instance.ResetAll();
        }

        /// <summary>
        /// Called when the player returns to the title screen.
        /// </summary>
        private void OnReturnedToTitle(object sender, ReturnedToTitleEventArgs e)
        {
            Cleanup();
            SMonitor.Log("[ModEntry] Returned to title screen — all manager caches cleaned up.", LogLevel.Debug);
        }

        /// <summary>
        /// Called every game tick. Processes the AgentToolDispatcher's thread-safe action queue
        /// to safely execute background-thread requests (e.g. DelayedAction registrations) on the main thread.
        /// </summary>
        private void OnUpdateTicked(object sender, UpdateTickedEventArgs e)
        {
            AgentToolDispatcher.ProcessMainThreadQueue();
        }
    }
}
