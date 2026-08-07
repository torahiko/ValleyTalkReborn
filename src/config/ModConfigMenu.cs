using System;
using System.Collections.Generic;
using System.Linq;
using GenericModConfigMenu;
using StardewModdingAPI;

namespace ValleyTalk
{
    internal static class ModConfigMenu
    {
        private static IGenericModConfigMenuApi ConfigMenu;
        private static IManifest ModManifest;
        private static ModEntry _modEntry;

        private static Dictionary<int,string> freqs = new Dictionary<int, string>()
        {
            { 0, "Never (0%)" },
            { 1, "Rarely (25%)" },
            { 2, "Occasionally (50%)" },
            { 3, "Mostly (75%)" },
            { 4, "Always (100%)" }
        };

        // 【新增核心修复】专属的 UI 翻译拦截器
        private static string GetUIString(string key, string fallback, object tokens = null)
        {
            string result = null;

            // 1. 强制最高优先级：读取 SMAPI 标准的 i18n 翻译文件夹
            if (_modEntry != null && _modEntry.Helper != null && _modEntry.Helper.Translation != null)
            {
                var smapiTranslation = _modEntry.Helper.Translation.Get(key);
                if (smapiTranslation.HasValue())
                {
                    result = smapiTranslation.ToString();
                }
            }

            // 2. 如果标准翻译没找到，退回到原作者的 PromptCache 缓存系统
            if (string.IsNullOrEmpty(result))
            {
                string cacheResult = Util.GetString(key, returnNull: true);
                if (!string.IsNullOrEmpty(cacheResult))
                {
                    result = cacheResult;
                }
            }

            // 3. 都没找到，使用代码里的英文硬编码保底
            if (string.IsNullOrEmpty(result))
            {
                result = fallback;
            }

            // 4. 替换文本变量 (tokens)
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
                modEntry.Monitor.Log(GetUIString("configGmcmNotInstalled", "Generic Mod Config Menu not installed."), LogLevel.Warn);
                return;
            }

            // register mod
            ConfigMenu.Register(
                mod: ModManifest,
                reset: () => ModEntry.Config = new ModConfig(),
                save: () => modEntry.Helper.WriteConfig(ModEntry.Config)
            );

            // add some config options
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
            // Create a string array of the options in the LlmType enum
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
                    ConfigMenu.Unregister(ModManifest);
                    Register(_modEntry);
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
                    setValue: (value) =>{ Config.ApiKey = value; SetLlm(); },
                    fieldId: "ApiKey"
                );
            }

            if (constructorParameters.Contains("modelName", StringComparer.OrdinalIgnoreCase))
            {
                ConfigMenu.AddTextOption(
                    mod: ModManifest,
                    name: () => GetUIString("configModelName", "Model Name"),
                    tooltip: () => GetUIString("configModelNameTooltip", "Name of the AI model to use."),
                    getValue: () => Config.ModelName,
                    setValue: (value) =>
                    { 
                        Config.ModelName = value; SetLlm(); 
                    },
                    fieldId: "ModelName"
                );
            }
            if (constructorParameters.Contains("url", StringComparer.OrdinalIgnoreCase))
            {
                ConfigMenu.AddTextOption(
                    mod: ModManifest,
                    name: () => GetUIString("configServerAddress", "Server Address"),
                    tooltip: () => GetUIString("configServerAddressTooltip", "URL of the server for local and Open AI compatible models."),
                    getValue: () => Config.ServerAddress,
                    setValue: (value) =>{ Config.ServerAddress = value; SetLlm(); },
                    fieldId: "ServerAddress"
                );
            }
            ConfigMenu.AddTextOption(
                mod: ModManifest,
                name: () => GetUIString("configInitiateKey", "Key to initiate typed dialogue"),
                tooltip: () => GetUIString("configInitiateKeyTooltip", "Key to hold while clicking on an NPC to initiate typed dialogue."),
                getValue: () => ModEntry.Config.InitiateTypedDialogueKey.ToString(),
                setValue: (value) => { SButton result; if (Enum.TryParse<SButton>(value, out result)) ModEntry.Config.InitiateTypedDialogueKey = result; }
            );
            ConfigMenu.AddBoolOption(
                mod: ModManifest,
                name: () => GetUIString("configTranslation", "Translate Outputs"),
                tooltip: () => GetUIString("configTranslationTooltip", "Translate the AI model outputs to the game language (without i18n pack)."),
                getValue: () => Config.ApplyTranslation,
                setValue: (value) =>{ Config.ApplyTranslation = value; }
            );
            ConfigMenu.AddTextOption(
                mod: ModManifest,
                name: () => GetUIString("configFrequencyGeneral", "Frequency of general lines"),
                tooltip: () => GetUIString("configFrequencyGeneralTooltip", "How often should the mod generate general lines."),
                getValue: () => freqs[Config.GeneralFrequency],
                setValue: (value) =>{ Config.GeneralFrequency = freqs.First(x => x.Value == value).Key; },
                allowedValues: freqs.Values.ToArray()
            );
            ConfigMenu.AddTextOption(
                mod: ModManifest,
                name: () => GetUIString("configFrequencyGift", "Frequency of gift responses"),
                tooltip: () => GetUIString("configFrequencyGiftTooltip", "How often should the mod generate gift responses."),
                getValue: () => freqs[Config.GiftFrequency],
                setValue: (value) =>{ Config.GiftFrequency = freqs.First(x => x.Value == value).Key; },
                allowedValues: freqs.Values.ToArray()
            );
            ConfigMenu.AddTextOption(
                mod: ModManifest,
                name: () => GetUIString("configFrequencyMarriage", "Frequency of marriage lines"),
                tooltip: () => GetUIString("configFrequencyMarriageTooltip", "How often should the mod generate marriage lines."),
                getValue: () => freqs[Config.MarriageFrequency],
                setValue: (value) =>{ Config.MarriageFrequency = freqs.First(x => x.Value == value).Key; },
                allowedValues: freqs.Values.ToArray()
            );
            ConfigMenu.AddBoolOption(
                mod: ModManifest,
                name: () => GetUIString("configEnableCancelButton", "Enable Cancel Button"),
                tooltip: () => GetUIString("configEnableCancelButtonTooltip", "Show a red X button during AI response wait, allowing you to interrupt the request."),
                getValue: () => Config.EnableCancelButton,
                setValue: value => Config.EnableCancelButton = value
            );

            ConfigMenu.AddTextOption(
                mod: ModManifest,
                name: () => GetUIString("configDiableForCharacters", "Disable for characters"),
                tooltip: () => GetUIString("configDiableForCharactersTooltip", "Comma-separated list of villagers to disable the mod for, e.g. (\"Abigail,Leah,Sam\")"),
                getValue: () => Config.DisableCharacters,
                setValue: (value) =>{ Config.DisableCharacters = value; }
            );

            // ========== Action Awareness System ==========
            ConfigMenu.AddSectionTitle(
                mod: ModManifest, 
                text: () => GetUIString("Perception.SectionTitle", "Action Awareness System"), 
                tooltip: () => GetUIString("Perception.SectionTooltip", "Let NPCs perceive player actions and mention them in dialogue")
            );

            ConfigMenu.AddBoolOption(
                mod: ModManifest,
                name: () => GetUIString("Perception.EnableMaster", "Enable Action Awareness (Master Switch)"),
                tooltip: () => GetUIString("Perception.EnableMasterTooltip", "Master switch for all action awareness features"),
                getValue: () => Config.EnablePerceptionSystem,
                setValue: value => Config.EnablePerceptionSystem = value
            );

            ConfigMenu.AddSectionTitle(mod: ModManifest, text: () => GetUIString("Perception.LayerTitle", "Layer Switches"));

            ConfigMenu.AddBoolOption(
                mod: ModManifest,
                name: () => GetUIString("Perception.EnableNearby", "Enable Nearby Perception (Talk)"),
                tooltip: () => GetUIString("Perception.EnableNearbyTooltip", "NPCs within 8 tiles can overhear conversations"),
                getValue: () => Config.EnableNearbyPerception,
                setValue: value => Config.EnableNearbyPerception = value
            );

            ConfigMenu.AddBoolOption(
                mod: ModManifest,
                name: () => GetUIString("Perception.EnableSameMap", "Enable Same-Map Perception"),
                tooltip: () => GetUIString("Perception.EnableSameMapTooltip", "NPCs on the same map see your actions"),
                getValue: () => Config.EnableSameMapPerception,
                setValue: value => Config.EnableSameMapPerception = value
            );

            ConfigMenu.AddBoolOption(
                mod: ModManifest,
                name: () => GetUIString("Perception.EnableGlobal", "Enable Town-Wide Broadcast (Harvest)"),
                tooltip: () => GetUIString("Perception.EnableGlobalTooltip", "Major events like harvests are known town-wide"),
                getValue: () => Config.EnableGlobalPerception,
                setValue: value => Config.EnableGlobalPerception = value
            );

            ConfigMenu.AddSectionTitle(mod: ModManifest, text: () => GetUIString("Perception.IndividualTitle", "Individual Action Switches"));

            ConfigMenu.AddBoolOption(
                mod: ModManifest,
                name: () => GetUIString("Perception.EnableEat", "Eating"),
                tooltip: () => GetUIString("Perception.EnableEatTooltip", "NPCs can see you eating"),
                getValue: () => Config.EnablePerceptionEat,
                setValue: value => Config.EnablePerceptionEat = value
            );

            ConfigMenu.AddBoolOption(
                mod: ModManifest,
                name: () => GetUIString("Perception.EnableFish", "Fishing"),
                tooltip: () => GetUIString("Perception.EnableFishTooltip", "NPCs can see you catching fish"),
                getValue: () => Config.EnablePerceptionFish,
                setValue: value => Config.EnablePerceptionFish = value
            );

            ConfigMenu.AddBoolOption(
                mod: ModManifest,
                name: () => GetUIString("Perception.EnableChop", "Chopping Trees"),
                tooltip: () => GetUIString("Perception.EnableChopTooltip", "NPCs can see you chopping trees"),
                getValue: () => Config.EnablePerceptionChop,
                setValue: value => Config.EnablePerceptionChop = value
            );

            ConfigMenu.AddBoolOption(
                mod: ModManifest,
                name: () => GetUIString("Perception.EnablePlace", "Placing Items"),
                tooltip: () => GetUIString("Perception.EnablePlaceTooltip", "NPCs can see you placing furniture/flooring"),
                getValue: () => Config.EnablePerceptionPlace,
                setValue: value => Config.EnablePerceptionPlace = value
            );

            ConfigMenu.AddBoolOption(
                mod: ModManifest,
                name: () => GetUIString("Perception.EnableTalk", "Talking (Nearby NPCs overhear)"),
                tooltip: () => GetUIString("Perception.EnableTalkTooltip", "NPCs nearby can hear your conversations"),
                getValue: () => Config.EnablePerceptionTalk,
                setValue: value => Config.EnablePerceptionTalk = value
            );

            ConfigMenu.AddBoolOption(
                mod: ModManifest,
                name: () => GetUIString("Perception.EnableHarvest", "Harvesting (Town-Wide)"),
                tooltip: () => GetUIString("Perception.EnableHarvestTooltip", "Harvests are broadcast town-wide"),
                getValue: () => Config.EnablePerceptionHarvest,
                setValue: value => Config.EnablePerceptionHarvest = value
            );

            ConfigMenu.AddSectionTitle(mod: ModManifest, text: () => GetUIString("Perception.LifetimeTitle", "Lifetime Settings"));

            ConfigMenu.AddNumberOption(
                mod: ModManifest,
                name: () => GetUIString("Perception.TalkLifetime", "Talk Perception Lifetime (minutes)"),
                tooltip: () => GetUIString("Perception.TalkLifetimeTooltip", "How long nearby NPCs remember conversations"),
                getValue: () => Config.PerceptionTalkLifetime,
                setValue: value => Config.PerceptionTalkLifetime = (int)value,
                min: 1,
                max: 10,
                interval: 1
            );

            ConfigMenu.AddNumberOption(
                mod: ModManifest,
                name: () => GetUIString("Perception.ActionLifetime", "Action Perception Lifetime (minutes)"),
                tooltip: () => GetUIString("Perception.ActionLifetimeTooltip", "How long NPCs remember actions"),
                getValue: () => Config.PerceptionActionLifetime,
                setValue: value => Config.PerceptionActionLifetime = (int)value,
                min: 1,
                max: 30,
                interval: 1
            );

            ConfigMenu.AddNumberOption(
                mod: ModManifest,
                name: () => GetUIString("Perception.HarvestLifetime", "Harvest Broadcast Lifetime (minutes)"),
                tooltip: () => GetUIString("Perception.HarvestLifetimeTooltip", "How long harvest news is remembered (default 1440 = 24h)"),
                getValue: () => Config.PerceptionHarvestLifetime,
                setValue: value => Config.PerceptionHarvestLifetime = (int)value,
                min: 60,
                max: 2880,
                interval: 60
            );

            ConfigMenu.AddParagraph(
                mod: ModManifest,
                text: () => {
                    var names = GetModelNames().ToList();
                    names.Sort();
                    if (names.Count() == 0) return GetUIString("configNoModels", $"Unable to get model names for {Config.Provider} (maybe the API key wasn't set when this menu was opened?)", new { Provider = Config.Provider });
                    
                    var modelString = string.Join(", \n", names);
                    return GetUIString("configModels", $"The models available on provider {Config.Provider} are:\n{modelString}", new { Provider = Config.Provider, Models = modelString });
                }
            );
        }

        private static string[] GetModelNames()
        {
            var provider = ModEntry.LlmMap[ModEntry.Config.Provider];
            if (provider.GetInterfaces().Any(x => x.Name == "IGetModelNames"))
            {
                var paramsDict = new Dictionary<string, string>()
                {
                    { "apiKey", ModEntry.Config.ApiKey },
                    { "modelName", ModEntry.Config.ModelName },
                    { "url", ModEntry.Config.ServerAddress },
                    { "promptFormat", ModEntry.Config.PromptFormat }
                };
                var instance = Llm.CreateInstance(provider, paramsDict);
                return ((IGetModelNames)instance).GetModelNames();
            }
            else
            {
                return new string[] { };
            }
        }

        private static IGenericModConfigMenuApi GetConfigMenu(ModEntry modEntry)
        {
            // get Generic Mod Config Menu's API (if it's installed)
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