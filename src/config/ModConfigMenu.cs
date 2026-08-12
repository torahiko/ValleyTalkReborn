using System;
using System.Collections.Generic;
using System.Linq;
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
                    var tokenName = "{\n" + token.Name + "}}";
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
    
                    // 1. 先刷新缓存，让下拉框有最新的模型列表
                    RefreshModelNamesCache();
    
                    // 2. 如果模型名已经选择/填写，再进行 Llm 的实例化与网络连接校验
                    if (!string.IsNullOrWhiteSpace(ModEntry.Config.ModelName))
                    {
                        SetLlm();
                    }
    
                    // 3. 重新注册界面，展示更新后的下拉菜单
                    Register(modEntry);
                }
            );

            // Add config options
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
                    RefreshModelNamesCache();
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

            ConfigMenu.AddTextOption(
                mod: ModManifest,
                name: () => GetUIString("configInitiateKey", "Key to initiate typed dialogue"),
                tooltip: () => GetUIString("configInitiateKeyTooltip",
                    "Key to hold while clicking on an NPC to initiate typed dialogue."),
                getValue: () => ModEntry.Config.InitiateTypedDialogueKey.ToString(),
                setValue: (value) =>
                {
                    SButton result;
                    if (Enum.TryParse<SButton>(value, out result)) ModEntry.Config.InitiateTypedDialogueKey = result;
                }
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
                name: () => GetUIString("configDiableForCharacters", "Disable for characters"),
                tooltip: () => GetUIString("configDiableForCharactersTooltip",
                    "Comma-separated list of villagers to disable the mod for, e.g. (\"Abigail,Leah,Sam\")"),
                getValue: () => Config.DisableCharacters,
                setValue: (value) => { Config.DisableCharacters = value; }
            );

            // 注意：Perception 系统开关（EnablePerceptionSystem）在此处故意被隐藏，不向玩家暴露 UI 菜单。
            // 配置底层该字段依旧保留（默认值为 true），确保代码逻辑强耦合时不被影响。
        }

        private static string[] GetCachedModelNames()
        {
            if (_cachedModelNames == null || _cachedProvider != ModEntry.Config.Provider)
            {
                RefreshModelNamesCache();
            }
            return _cachedModelNames ?? new string[] { };
        }

        private static void RefreshModelNamesCache()
        {
            try
            {
                _cachedModelNames = GetModelNames();
                _cachedProvider = ModEntry.Config.Provider;

                var names = _cachedModelNames.ToList();
                names.Sort();
                _cachedModelNames = names.ToArray();
            }
            catch (Exception ex)
            {
                _modEntry.Monitor.Log($"Error fetching model names: {ex.Message}", LogLevel.Warn);
                _cachedModelNames = new string[] { };
            }
        }

        private static string[] GetModelNames()
        {
            if (string.IsNullOrWhiteSpace(ModEntry.Config.ApiKey))
                return new string[] { };

            if (!ModEntry.LlmMap.TryGetValue(ModEntry.Config.Provider, out var provider))
                return new string[] { };

            if (provider.GetInterfaces().Any(x => x.Name == "IGetModelNames"))
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
                    return ((IGetModelNames)instance).GetModelNames();
                }
                catch (Exception ex)
                {
                    _modEntry.Monitor.Log($"Failed to get model names: {ex.Message}", LogLevel.Trace);
                    return new string[] { };
                }
            }

            return new string[] { };
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