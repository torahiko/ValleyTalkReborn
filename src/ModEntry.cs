using System;
using HarmonyLib;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using System.Collections.Generic;
using System.Linq;
using System.Globalization;
using ValleyTalk.Plugins;
namespace ValleyTalk
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
        /// Indicates whether the mod has been initialized (to prevent duplicate subscriptions).
        /// </summary>
        private static bool _isInitialized = false;

        /// <summary>
        /// Cancel button plugin instance.
        /// </summary>
        private CancelButtonPlugin _cancelButtonPlugin;

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

            // Defensive cleanup: attempt to unload any leftover patches from a previous session
            try
            {
                new Harmony(ModManifest.UniqueID).UnpatchAll(ModManifest.UniqueID);
            }
            catch (Exception ex)
            {
                Monitor.Log($"[ValleyTalk] Defensive Harmony.UnpatchAll failed: {ex.Message}", LogLevel.Trace);
            }

            // If already initialized (e.g. second run in same process), clean up first
            if (_isInitialized)
            {
                Cleanup();
            }

            // Subscribe to game lifecycle events
            helper.Events.GameLoop.GameLaunched += OnGameLaunched;
            helper.Events.GameLoop.SaveLoaded += OnSaveLoaded;

            Config = Helper.ReadConfig<ModConfig>();

            // Load cancel button plugin
            if (Config.EnableCancelButton)
            {
                _cancelButtonPlugin = new CancelButtonPlugin(helper, Monitor, Config.EnableCancelButton);
            }
            else
            {
                Monitor.Log("Cancel button plugin disabled (config off).", LogLevel.Debug);
            }

            SMonitor = Monitor;

            if (!Config.EnableMod)
            {
                return;
            }

            // Initialize the text input manager
            TextInputManager.Initialize();

            // Initialize cross-platform compatible logging
            Log.Initialize(Monitor);

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

            DialogueBuilder.Instance.Config = Config;

            CheckContentPacks();

            // Initialize Action Awareness System
            try
            {
                EatSubscriber.Initialize();
                FishSubscriber.Initialize();
                WorldSubscriber.Initialize();
                HarvestSubscriber.Initialize();
                TalkSubscriber.Initialize();

                Log.Debug("[ValleyTalk] Action Awareness System initialized.");
            }
            catch (Exception ex)
            {
                Log.Error($"[ValleyTalk] Error initializing Action Awareness System: {ex.Message}");
            }

            // Save Harmony instance as a member so it can be unpatched on exit
            _harmony = new Harmony(ModManifest.UniqueID);
            _harmony.PatchAll();

            _isInitialized = true;

            Log.Debug($"[{DateTime.Now}] Mod loaded");
        }

        /// <summary>
        /// Performs comprehensive cleanup of all static resources, event subscriptions, and Harmony patches.
        /// This is critical to prevent AccessViolationException on second launch.
        /// </summary>
        private void Cleanup()
        {
            try
            {
                // 1. Unload all Harmony patches applied by this mod
                try
                {
                    _harmony?.UnpatchAll(ModManifest.UniqueID);
                    _harmony = null;
                    Log.Debug("[ValleyTalk] Harmony patches unloaded.");
                }
                catch (Exception ex)
                {
                    Log.Error($"[ValleyTalk] Error unpatching Harmony: {ex.Message}");
                }

                // 2. Clean up MemoryManager singleton
                try
                {
                    MemoryManager.Instance?.Cleanup();
                }
                catch (Exception ex)
                {
                    Log.Error($"[ValleyTalk] Error cleaning MemoryManager: {ex.Message}");
                }

                // 3. Clean up TextInputManager
                try
                {
                    TextInputManager.Cleanup();
                }
                catch (Exception ex)
                {
                    Log.Error($"[ValleyTalk] Error cleaning TextInputManager: {ex.Message}");
                }

                // 4. Clean up AsyncBuilder singleton
                try
                {
                    AsyncBuilder.Instance?.Cleanup();
                }
                catch (Exception ex)
                {
                    Log.Error($"[ValleyTalk] Error cleaning AsyncBuilder: {ex.Message}");
                }

                // 5. Clean up DialogueHistoryManager singleton
                try
                {
                    DialogueHistoryManager.Instance?.Cleanup();
                }
                catch (Exception ex)
                {
                    Log.Error($"[ValleyTalk] Error cleaning DialogueHistoryManager: {ex.Message}");
                }

                // 6. Clean up DialogueBuilder singleton
                try
                {
                    DialogueBuilder.Instance?.Cleanup();
                }
                catch (Exception ex)
                {
                    Log.Error($"[ValleyTalk] Error cleaning DialogueBuilder: {ex.Message}");
                }

                // 7. Clean up ModInteropManager singleton
                try
                {
                    ModInteropManager.Instance?.Cleanup();
                }
                catch (Exception ex)
                {
                    Log.Error($"[ValleyTalk] Error cleaning ModInteropManager: {ex.Message}");
                }

                // 8b. Clean up Action Awareness System
                try
                {
                    EatSubscriber.Cleanup();
                    FishSubscriber.Cleanup();
                    WorldSubscriber.Cleanup();
                    HarvestSubscriber.Cleanup();
                    TalkSubscriber.Cleanup();

                    PerceptionManager.Instance?.Cleanup();
                }
                catch (Exception ex)
                {
                    Log.Error($"[ValleyTalk] Error cleaning Action Awareness System: {ex.Message}");
                }

                // 8. Reset static fields in ModEntry
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
                    Log.Error($"[ValleyTalk] Error resetting ModEntry statics: {ex.Message}");
                }

                // 9. Dispose cancel button plugin
                try
                {
                    _cancelButtonPlugin?.Dispose();
                    _cancelButtonPlugin = null;
                }
                catch (Exception ex)
                {
                    Log.Error($"[ValleyTalk] Error disposing cancel button plugin: {ex.Message}");
                }

                // 10. Reset Log monitor reference
                Log.Cleanup();

                _isInitialized = false;

                Log.Debug("[ValleyTalk] Full cleanup completed.");
            }
            catch (Exception ex)
            {
                // Last-resort error handling - never throw from cleanup
                try { Log.Error($"[ValleyTalk] Critical error during cleanup: {ex.Message}"); } catch { }
            }
        }

        private void CheckContentPacks()
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
                Monitor.Log("Note: Content packs have been found that don't have mod author approval for use with AI.", LogLevel.Warn);
                Monitor.Log("While content from content packs will be displayed in-game, it will not be use for AI dialogue generation.", LogLevel.Warn);
                Monitor.Log($"Content packs without author approval: {string.Join(", ", blockedContentPacks.Select(p => p.Manifest.Name))}", LogLevel.Info);
                Monitor.Log("Mod authors can permit their content to be used in dialogue generation by adding \"permitAiUse\":true to their mod's manifest.", LogLevel.Warn);
                BlockModdedContent = true;
            }
        }

        private void OnGameLaunched(object sender, GameLaunchedEventArgs e)
        {
            ModConfigMenu.Register(this);
        }

        private void OnSaveLoaded(object sender, SaveLoadedEventArgs e)
        {
            // Load dialogue history from save data (only when a save is loaded)
            DialogueHistoryManager.Instance.Load();
        }
    }
}
