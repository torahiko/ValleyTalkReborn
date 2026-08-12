using System;
using HarmonyLib;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using StardewValley;
using ValleytalkReborn.Plugins;

namespace ValleytalkReborn
{
    public partial class ModEntry : Mod
    {
        public static IMonitor SMonitor;
        public static IModHelper SHelper { get; private set; }
        public static ModConfig Config;

        /// <summary>
        /// Harmony instance saved as a member so it can be unpatched on exit.
        /// </summary>
        private Harmony _harmony;

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
                        {"Dummy", typeof(LlmDummy)},
#endif
                        {"LlamaCpp", typeof(LlmLlamaCpp)},
                        {"Google", typeof(LlmGemini)},
                        {"Anthropic", typeof(LlmClaude)},
                        {"OpenAI", typeof(LlmOpenAi)},
                        {"Mistral", typeof(LlmMistral)},
                        {"DeepSeek", typeof(LlmDeepSeek)},
                        {"VolcEngine", typeof(LlmVolcEngine)},
                        {"OpenAiCompatible", typeof(LlmOAICompatible)}
                    };
                }
                return _llmMap;
            }
        }
        public static bool BlockModdedContent { get; private set; } = false;
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
                    _fixPunctuation = suffixes.Count == 1 || suffixes.Any(x => x == ".en" || x == ".fr" || x == ".de" || x == ".es" || x == ".tr" || x == ".pt" || x == ".it" || x == ".nl" || x == ".pl" || x == ".id");
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

            // If already initialized (e.g. second run in same process), clean up first
            if (_isInitialized)
            {
                Cleanup();
            }

            // Subscribe to game lifecycle events
            helper.Events.GameLoop.GameLaunched += OnGameLaunched;
            helper.Events.GameLoop.SaveLoaded += OnSaveLoaded;
            helper.Events.GameLoop.DayStarted += OnDayStarted;

            // 拦截外部输入，防止打字时触发其他MOD的热键
            helper.Events.Input.ButtonPressed += OnButtonPressed;

            Config = Helper.ReadConfig<ModConfig>();

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
                _hasPatched = true;
                Log.Debug("[ValleyTalkReborn] Harmony patches applied successfully.");
            }

            _isInitialized = true;

            // 注册夜间记忆固化系统
            NightlyConsolidationHook.Register(helper);

            // 加载/刷新模组的核心功能模块
            OnConfigChanged();

            Log.Debug($"[{DateTime.Now}] Mod loaded");
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

            Llm.SetLlm(llmType, modelName: Config.ModelName, apiKey: Config.ApiKey, url: Config.ServerAddress, promptFormat: Config.PromptFormat);

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

                Log.Debug("[ValleyTalkReborn] Action Awareness System initialized.");
            }
            catch (Exception ex)
            {
                Log.Error($"[ValleyTalkReborn] Error initializing Action Awareness System: {ex.Message}");
            }

            Log.Debug("[ValleyTalkReborn] 模组已开启并已更新 LLM 与模组配置。");
        }

        /// <summary>
        /// 注册用于测试 Agent 接口与物理分发器的 SMAPI 控制台指令
        /// </summary>
        private void RegisterDebugConsoleCommands(IModHelper helper)
        {
            helper.ConsoleCommands.Add("vt_test_date", "测试 Agent 约会预约分发器\n用法: vt_test_date <NPC名字> <地点ID>", (cmd, args) =>
            {
                string npcName = args.Length > 0 ? args[0] : "Abigail";
                string location = args.Length > 1 ? args[1] : "Saloon";

                var npc = Game1.getCharacterFromName(npcName);
                if (npc == null) { Monitor.Log($"[Test] 未找到 NPC: {npcName}", LogLevel.Error); return; }

                Monitor.Log($"[Test] >>> 正在测试分发 schedule_date: {npcName} -> {location}", LogLevel.Info);
                AgentToolDispatcher.DispatchToolCall(npc, "schedule_date", $"{{\"location_id\":\"{location}\"}}");
            });

            helper.ConsoleCommands.Add("vt_test_action", "测试 Agent 物理动作分发器\n用法: vt_test_action <NPC名字> <动作类型>", (cmd, args) =>
            {
                string npcName = args.Length > 0 ? args[0] : "Abigail";
                string action = args.Length > 1 ? args[1] : "FOLLOW";

                var npc = Game1.getCharacterFromName(npcName);
                if (npc == null) { Monitor.Log($"[Test] 未找到 NPC: {npcName}", LogLevel.Error); return; }

                Monitor.Log($"[Test] >>> 正在测试分发 trigger_physical_action: {npcName} -> {action}", LogLevel.Info);
                AgentToolDispatcher.DispatchToolCall(npc, "trigger_physical_action", $"{{\"action_type\":\"{action}\"}}");
            });

            helper.ConsoleCommands.Add("vt_test_end", "测试 Agent 自然解约分发器\n用法: vt_test_end <NPC名字>", (cmd, args) =>
            {
                string npcName = args.Length > 0 ? args[0] : "Abigail";

                var npc = Game1.getCharacterFromName(npcName);
                if (npc == null) { Monitor.Log($"[Test] 未找到 NPC: {npcName}", LogLevel.Error); return; }

                Monitor.Log($"[Test] >>> 正在测试分发 end_current_date: {npcName}", LogLevel.Info);
                AgentToolDispatcher.DispatchToolCall(npc, "end_current_date", "{\"reason\":\"console_command_test\"}");
            });

            helper.ConsoleCommands.Add("vt_test_llm_tools", "测试大模型 Native Tool Calling 是否正确返回 JSON", async (cmd, args) =>
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
        }

        /// <summary>
        /// Intercepts and suppresses keyboard input when our custom text box is active.
        /// </summary>
        [EventPriority(EventPriority.High)]
        private void OnButtonPressed(object sender, ButtonPressedEventArgs e)
        {
            if (!Config.EnableMod) return;

            if (Game1.keyboardDispatcher?.Subscriber is DialogueTextInputBox)
            {
                bool isCtrlPressed = Game1.input.GetKeyboardState().IsKeyDown(Microsoft.Xna.Framework.Input.Keys.LeftControl) || 
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

                if (isCtrlPressed && (e.Button == SButton.C || e.Button == SButton.V || e.Button == SButton.X || e.Button == SButton.A || e.Button == SButton.Z))
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
        /// Performs comprehensive cleanup of all static resources, event subscriptions.
        /// </summary>
        private void Cleanup()
        {
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
                    Log.Debug("[ValleyTalkReborn] Harmony instance cleared, but patches retained to prevent AccessViolationException.");
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

                    PerceptionManager.Instance?.Cleanup();
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

                try
                {
                    _llmMap = null;
                    _fixPunctuation = null;
                    _locale = null;
                    _localeCache = string.Empty;
                    _localeCacheFixPunctuation = string.Empty;
                    BlockModdedContent = false;
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
                try { Log.Error($"[ValleyTalkReborn] Critical error during cleanup: {ex.Message}"); } catch { }
            }
        }

        private static void CheckContentPacks()
        {
            var contentPacks = SHelper.ModRegistry.GetAll().Where(p => p.IsContentPack).ToList();
            var blockedContentPacks = contentPacks
                .Where(p => !SldConstants.PermitListContentPacks.Contains(p.Manifest.UniqueID))
                .Where(p =>
                        !p.Manifest.ExtraFields.ContainsKey("PermitAiUse") ||
                        !(p.Manifest.ExtraFields["PermitAiUse"] as bool? ?? false)
                );
            if (blockedContentPacks.Any())
            {
                SMonitor.Log("Note: Content packs have been found that don't have mod author approval for use with AI.", LogLevel.Warn);
                SMonitor.Log("While content from content packs will be displayed in-game, it will not be use for AI dialogue generation.", LogLevel.Warn);
                SMonitor.Log($"Content packs without author approval: {string.Join(", ", blockedContentPacks.Select(p => p.Manifest.Name))}", LogLevel.Info);
                SMonitor.Log("Mod authors can permit their content to be used in dialogue generation by adding \"permitAiUse\":true to their mod's manifest.", LogLevel.Warn);
                BlockModdedContent = true;
            }
        }

        private void OnGameLaunched(object sender, GameLaunchedEventArgs e)
        {
            ModConfigMenu.Register(this);
        }

        private void OnSaveLoaded(object sender, SaveLoadedEventArgs e)
        {
            DialogueHistoryManager.Instance.Load();
            RecentConversationTracker.Clear();
        }

        private void OnDayStarted(object sender, DayStartedEventArgs e)
        {
            RecentConversationTracker.Clear();
            NPC_CurrentDialogue_Patch.ClearDedupState();
            NPC_CheckForNewCurrentDialogue_Patch.ClearDedupState();
        }
    }
}