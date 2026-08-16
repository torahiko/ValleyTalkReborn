using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using GenericModConfigMenu;
using StardewModdingAPI;

namespace ValleytalkReborn
{
    internal static class ModConfigMenu
    {
        private static IGenericModConfigMenuApi ConfigMenu;
        private static IManifest ModManifest;
        private static ModEntry _modEntry;
        private static string[] _cachedModelNames = null;
        private static string _cachedProvider = null;

        private static Dictionary<int, string> freqs = new Dictionary<int, string>()
        {
            { 0, "Never (0%)" },
            { 1, "Rarely (25%)" },
            { 2, "Occasionally (50%)" },
            { 3, "Mostly (75%)" },
            { 4, "Always (100%)" }
        };

        private static readonly Dictionary<string, int> freqReverseLookup = new Dictionary<string, int>
        {
            { "Never (0%)", 0 },
            { "Rarely (25%)", 1 },
            { "Occasionally (50%)", 2 },
            { "Mostly (75%)", 3 },
            { "Always (100%)", 4 }
        };

        private static string GetUIString(string key, string fallback, object tokens = null)
        {
            string result = null;
            if (_modEntry != null && _modEntry.Helper != null && _modEntry.Helper.Translation != null)
            {
                var smapiTranslation = _modEntry.Helper.Translation.Get(key);
                if (smapiTranslation.HasValue())
                {
                    result = smapiTranslation.ToString();
                }
            }

            if (string.IsNullOrEmpty(result))
            {
                string cacheResult = Util.GetString(key, returnNull: true);
                if (!string.IsNullOrEmpty(cacheResult))
                {
                    result = cacheResult;
                }
            }

            if (string.IsNullOrEmpty(result))
            {
                result = fallback;
            }

            if (tokens != null && result != null)
            {
                foreach (var token in tokens.GetType().GetProperties())
                {
                    var tokenName = "{{" + token.Name + "}}";
                    result = result.Replace(tokenName, token.GetValue(tokens)?.ToString() ?? "");
                }
            }

            return result;
        }

        internal static void Register(ModEntry modEntry)
        {
            _modEntry = modEntry;
            var Config = ModEntry.Config;
            ModManifest = modEntry.ModManifest;
            ConfigMenu = GetConfigMenu(modEntry);

            if (ConfigMenu == null)
            {
                modEntry.Monitor.Log(GetUIString("configGmcmNotInstalled", "Generic Mod Config Menu not installed."),
                    LogLevel.Warn);
                return;
            }

            // 重新注册前先取消注册，实现 UI 动态刷新
            ConfigMenu.Unregister(ModManifest);
            ConfigMenu.Register(
                mod: ModManifest,
                reset: () => ModEntry.Config = new ModConfig(),
                save: () =>
                {
                    modEntry.Helper.WriteConfig(ModEntry.Config);

                    // 🌟 核心修复：触发后台异步刷新模型缓存，彻底避免 Save 时 UI 假死
                    RefreshModelNamesCacheAsync();

                    // 如果模型名已经选择/填写，再进行 Llm 的实例化与网络连接校验
                    if (!string.IsNullOrWhiteSpace(ModEntry.Config.ModelName))
                    {
                        SetLlm();
                    }

                    // 重新注册界面，展示更新后的下拉菜单
                    Register(modEntry);
                }
            );

            // ── 基础功能开关 ──────────────────────────────────────
            ConfigMenu.AddBoolOption(
                mod: ModManifest,
                name: () => GetUIString("configEnable", "Enable Mod"),
                tooltip: () => GetUIString("configEnableTooltip", "Enable or disable the mod."),
                getValue: () => Config.EnableMod,
                setValue: value => Config.EnableMod = value
            );

#if DEBUG
            ConfigMenu.AddBoolOption(
                mod: ModManifest,
                name: () => GetUIString("configLogging", "Enable Logging"),
                tooltip: () => GetUIString("configLoggingTooltip", "Enable or disable logging of prompts and responses."),
                getValue: () => Config.Debug,
                setValue: value => Config.Debug = value
            );
#endif

            // ── AI 模型与服务商设置 ──────────────────────────────
            var llmTypes = ModEntry.LlmMap.Keys.ToArray();
            ConfigMenu.AddTextOption(
                mod: ModManifest,
                name: () => GetUIString("configProvider", "AI Model Provider"),
                getValue: () => Config.Provider,
                setValue: value =>
                {
                    if (value == Config.Provider) return;
                    Config.ApiKey = "";
                    Config.Provider = value;
                    _cachedModelNames = null;
                    // 🌟 切换服务商时也使用异步刷新
                    RefreshModelNamesCacheAsync();
                },
                allowedValues: llmTypes,
                fieldId: "Provider"
            );

            var llmType = ModEntry.LlmMap[Config.Provider];
            var constructorParameters = llmType.GetConstructors().First().GetParameters().Select(x => x.Name).ToArray();

            if (constructorParameters.Contains("apiKey", StringComparer.OrdinalIgnoreCase))
            {
                ConfigMenu.AddTextOption(
                    mod: ModManifest,
                    name: () => GetUIString("configApiKey", "API Key"),
                    tooltip: () => GetUIString("configApiKeyTooltip", "API Key for the AI model provider."),
                    getValue: () => Config.ApiKey,
                    setValue: (value) => Config.ApiKey = value,
                    fieldId: "ApiKey"
                );
            }

            if (constructorParameters.Contains("modelName", StringComparer.OrdinalIgnoreCase))
            {
                // 手动输入框
                ConfigMenu.AddTextOption(
                    mod: ModManifest,
                    name: () => GetUIString("configModelName", "Model Name"),
                    tooltip: () => GetUIString("configModelNameTooltip", "Name of the AI model to use."),
                    getValue: () => Config.ModelName,
                    setValue: (value) => Config.ModelName = value,
                    fieldId: "ModelName"
                );

                // 快捷下拉框选择
                if (_cachedModelNames != null && _cachedModelNames.Length > 0)
                {
                    var quickSelectOptions = new List<string> { "--- Select to auto-fill ---" };
                    quickSelectOptions.AddRange(_cachedModelNames);
                    ConfigMenu.AddTextOption(
                        mod: ModManifest,
                        name: () => GetUIString("configQuickSelect", "Quick Select Model"),
                        tooltip: () => GetUIString("configQuickSelectTooltip", "Select a model and click Save to fill into Model Name."),
                        getValue: () => "--- Select to auto-fill ---",
                        setValue: (value) =>
                        {
                            if (value != "--- Select to auto-fill ---")
                            {
                                Config.ModelName = value;
                            }
                        },
                        allowedValues: quickSelectOptions.ToArray(),
                        fieldId: "QuickSelectModel"
                    );
                }
                else
                {
                    ConfigMenu.AddParagraph(
                        mod: ModManifest,
                        text: () => GetUIString("configFetchHint", "Enter API Key and click 'Save' to fetch available models.")
                    );
                }
            }

            if (constructorParameters.Contains("url", StringComparer.OrdinalIgnoreCase))
            {
                ConfigMenu.AddTextOption(
                    mod: ModManifest,
                    name: () => GetUIString("configServerAddress", "Server Address"),
                    tooltip: () => GetUIString("configServerAddressTooltip",
                        "URL of the server for local and Open AI compatible models."),
                    getValue: () => Config.ServerAddress,
                    setValue: (value) => Config.ServerAddress = value,
                    fieldId: "ServerAddress"
                );
            }

            // ── 对话与输出选项 ──────────────────────────────────
            ConfigMenu.AddBoolOption(
                mod: ModManifest,
                name: () => GetUIString("configStreaming", "Enable Streaming Output"),
                tooltip: () => GetUIString("configStreamingTooltip", "Show AI responses word-by-word as they arrive. Only applies to typed conversations."),
                getValue: () => Config.EnableStreaming,
                setValue: value => Config.EnableStreaming = value
            );

            ConfigMenu.AddBoolOption(
                mod: ModManifest,
                name: () => GetUIString("configTranslation", "Translate Outputs"),
                tooltip: () => GetUIString("configTranslationTooltip",
                    "Translate the AI model outputs to the game language (without i18n pack)."),
                getValue: () => Config.ApplyTranslation,
                setValue: (value) => { Config.ApplyTranslation = value; }
            );

            ConfigMenu.AddTextOption(
                mod: ModManifest,
                name: () => GetUIString("configFrequencyGeneral", "Frequency of general lines"),
                tooltip: () => GetUIString("configFrequencyGeneralTooltip",
                    "How often should the mod generate general lines."),
                getValue: () => freqs[Config.GeneralFrequency],
                setValue: (value) =>
                {
                    Config.GeneralFrequency =
                        freqReverseLookup.TryGetValue(value, out var k) ? k : Config.GeneralFrequency;
                },
                allowedValues: freqs.Values.ToArray()
            );

            ConfigMenu.AddTextOption(
                mod: ModManifest,
                name: () => GetUIString("configFrequencyGift", "Frequency of gift responses"),
                tooltip: () =>
                    GetUIString("configFrequencyGiftTooltip", "How often should the mod generate gift responses."),
                getValue: () => freqs[Config.GiftFrequency],
                setValue: (value) =>
                {
                    Config.GiftFrequency = freqReverseLookup.TryGetValue(value, out var k) ? k : Config.GiftFrequency;
                },
                allowedValues: freqs.Values.ToArray()
            );

            ConfigMenu.AddTextOption(
                mod: ModManifest,
                name: () => GetUIString("configFrequencyMarriage", "Frequency of marriage lines"),
                tooltip: () => GetUIString("configFrequencyMarriageTooltip",
                    "How often should the mod generate marriage lines."),
                getValue: () => freqs[Config.MarriageFrequency],
                setValue: (value) =>
                {
                    Config.MarriageFrequency =
                        freqReverseLookup.TryGetValue(value, out var k) ? k : Config.MarriageFrequency;
                },
                allowedValues: freqs.Values.ToArray()
            );

            ConfigMenu.AddTextOption(
                mod: ModManifest,
                name: () => GetUIString("configDiableForCharacters", "Disable for specific NPCs"),
                tooltip: () => GetUIString("configDiableForCharactersTooltip",
                    "Comma-separated list of villagers to disable the mod for, e.g. (\"Abigail,Leah,Sam\")"),
                getValue: () => Config.DisableCharacters,
                setValue: (value) => { Config.DisableCharacters = value; }
            );
            
            // ── ★ 快捷键与控制设置 Section ──────────────────────
            ConfigMenu.AddSectionTitle(
                mod: ModManifest,
                text: () => GetUIString("configSectionKeybinds", "Keybinds & Controls")
            );

            // 1. 打字对话按键
            // 🌟 修复：从 AddTextOption 改为 AddKeybind，提供可视化按键绑定体验
            ConfigMenu.AddKeybind(
                mod: ModManifest,
                name: () => GetUIString("configInitiateKey", "Initiate Typed Dialogue Key"),
                getValue: () => ModEntry.Config.InitiateTypedDialogueKey,
                setValue: (value) => ModEntry.Config.InitiateTypedDialogueKey = value,
                tooltip: () => GetUIString("configInitiateKeyTooltip",
                    "Key to hold while clicking on an NPC to initiate typed dialogue.")
            );

            // 2. 追问快捷键
            ConfigMenu.AddKeybind(
                mod: ModManifest,
                name: () => GetUIString("configQuickReplyKey", "Quick Reply Key"),
                getValue: () => ModEntry.Config.QuickReplyKey,
                setValue: (value) => ModEntry.Config.QuickReplyKey = value,
                tooltip: () => GetUIString("configQuickReplyKeyTooltip", 
                    "Key to quickly reply to the last spoken NPC within 5 seconds.")
            );
        }

        /// <summary>
        /// 获取缓存的模型名称，若缓存失效则触发后台异步刷新
        /// </summary>
        private static string[] GetCachedModelNames()
        {
            if (_cachedModelNames == null || _cachedProvider != ModEntry.Config.Provider)
            {
                // 🌟 仅触发后台任务，不阻塞当前 UI 线程
                RefreshModelNamesCacheAsync();
            }
            return _cachedModelNames ?? Array.Empty<string>();
        }

        /// <summary>
        /// 🌟 核心修复：后台异步刷新模型缓存，防止 GMCM 界面卡死
        /// </summary>
        private static void RefreshModelNamesCacheAsync()
        {
            _cachedProvider = ModEntry.Config.Provider;

            Task.Run(async () =>
            {
                try
                {
                    var fetchedNames = await GetModelNamesAsync();
                    if (fetchedNames != null && fetchedNames.Length > 0)
                    {
                        var namesList = fetchedNames.ToList();
                        namesList.Sort();
                        _cachedModelNames = namesList.ToArray();

                        // 🌟 刷新成功后，通过 SMAPI 事件切回主线程重新注册 GMCM
                        if (_modEntry != null)
                        {
                            _modEntry.Helper.Events.GameLoop.UpdateTicked += OnUpdateTickedToRefreshUi;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _modEntry?.Monitor.Log($"Error fetching model names: {ex.Message}", LogLevel.Warn);
                    _cachedModelNames = Array.Empty<string>();
                }
            });
        }

        /// <summary>
        /// 主线程回调：确保 GMCM UI 操作在正确的线程执行
        /// </summary>
        private static void OnUpdateTickedToRefreshUi(object sender, StardewModdingAPI.Events.UpdateTickedEventArgs e)
        {
            if (_modEntry != null)
            {
                Register(_modEntry);
                // 立即取消订阅，确保只执行一次
                _modEntry.Helper.Events.GameLoop.UpdateTicked -= OnUpdateTickedToRefreshUi;
            }
        }

        /// <summary>
        /// 🌟 核心修复：异步获取模型列表，替代原有的同步 GetModelNames
        /// </summary>
        private static async Task<string[]> GetModelNamesAsync()
        {
            if (string.IsNullOrWhiteSpace(ModEntry.Config.ApiKey))
                return Array.Empty<string>();

            if (!ModEntry.LlmMap.TryGetValue(ModEntry.Config.Provider, out var provider))
                return Array.Empty<string>();

            if (typeof(IGetModelNames).IsAssignableFrom(provider))
            {
                string currentModel = string.IsNullOrWhiteSpace(ModEntry.Config.ModelName)
                    ? "placeholder-for-fetching"
                    : ModEntry.Config.ModelName;

                var paramsDict = new Dictionary<string, string>()
                {
                    { "apiKey", ModEntry.Config.ApiKey },
                    { "modelName", currentModel },
                    { "url", ModEntry.Config.ServerAddress },
                    { "promptFormat", ModEntry.Config.PromptFormat }
                };

                try
                {
                    var instance = Llm.CreateInstance(provider, paramsDict);
                    // 🌟 调用全新的异步接口方法
                    return await ((IGetModelNames)instance).GetModelNamesAsync();
                }
                catch (Exception ex)
                {
                    _modEntry?.Monitor.Log($"Failed to get model names: {ex.Message}", LogLevel.Trace);
                    return Array.Empty<string>();
                }
            }

            return Array.Empty<string>();
        }

        private static IGenericModConfigMenuApi GetConfigMenu(ModEntry modEntry)
        {
            return modEntry.Helper.ModRegistry.GetApi<IGenericModConfigMenuApi>("spacechase0.GenericModConfigMenu");
        }

        private static void SetLlm()
        {
            if (!ModEntry.LlmMap.TryGetValue(ModEntry.Config.Provider, out var llmType))
            {
                ModEntry.SMonitor.Log($"Invalid LLM provider: {ModEntry.Config.Provider}", LogLevel.Error);
                return;
            }

            Llm.SetLlm(llmType, apiKey: ModEntry.Config.ApiKey, modelName: ModEntry.Config.ModelName, url: ModEntry.Config.ServerAddress, promptFormat: ModEntry.Config.PromptFormat);
        }
    }
}