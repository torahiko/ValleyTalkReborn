using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using GenericModConfigMenu;
using Microsoft.Xna.Framework.Input;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using StardewValley.Menus;

namespace ValleytalkReborn
{
    internal static class ModConfigMenu
    {
        private static IGenericModConfigMenuApi ConfigMenu;
        private static IManifest ModManifest;
        private static ModEntry _modEntry;
        private static string[] _cachedModelNames = null;
        private static string _cachedProvider = null;
        private static string _lastFetchErrorMessage = null;

        private static readonly string[] FrequencyValues = { "0", "1", "2", "3", "4" };

        private static string FormatFrequency(string val)
        {
            return GetUIString($"configFrequencyLevel_{val}", val switch
            {
                "0" => "Never (0%)",
                "1" => "Rarely (25%)",
                "2" => "Occasionally (50%)",
                "3" => "Mostly (75%)",
                "4" => "Always (100%)",
                _ => val
            });
        }

        private static string FormatProvider(string providerName)
        {
            string directKey = $"configProvider_{providerName}";
            string text = GetUIString(directKey, null);
            if (text != null) return text;

            string fallbackKey = providerName switch
            {
                "OpenAiCompatible" => "configProvider_LlmOAICompatible",
                "Google" => "configProvider_LlmGemini",
                "Anthropic" => "configProvider_LlmClaude",
                "OpenAI" => "configProvider_LlmOpenAi",
                "DeepSeek" => "configProvider_LlmDeepSeek",
                "Grok" => "configProvider_LlmGrok",
                _ => directKey
            };

            return GetUIString(fallbackKey, providerName);
        }

        internal static string GetUIString(string key, string fallback, object tokens = null)
        {
            string result = null;

            try
            {
                string i18nVal = I18n.Get(key);
                if (!string.IsNullOrEmpty(i18nVal) && i18nVal != key)
                {
                    result = i18nVal;
                }
            }
            catch { }

            if (string.IsNullOrEmpty(result) && _modEntry?.Helper?.Translation != null)
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
            ModManifest = modEntry.ModManifest;
            ConfigMenu = GetConfigMenu(modEntry);

            if (ConfigMenu == null)
            {
                modEntry.Monitor.Log(GetUIString("configGmcmNotInstalled", "Generic Mod Config Menu not installed."),
                    LogLevel.Warn);
                return;
            }

            if (!ModEntry.LlmMap.ContainsKey(ModEntry.Config.Provider))
            {
                ModEntry.Config.Provider = "OpenAiCompatible";
            }

            ConfigMenu.Unregister(ModManifest);
            ConfigMenu.Register(
                mod: ModManifest,
                reset: () => ModEntry.Config = new ModConfig(),
                save: () =>
                {
                    // ★ 写盘前规范化日记蒸馏配置（未知模式/越界数值先落定为明确值）
                    ModEntry.Config.NormalizeDailyDistillationConfig(modEntry.Monitor);

                    modEntry.Helper.WriteConfig(ModEntry.Config);

                    ModEntry.CleanupOnConfigToggle();

                    Llm.RecreateHttpClient();

                    // ★ 保存后即时生效：刷新第三方授权名单与 DialogueBuilder 配置引用
                    ModEntry.CheckContentPacks();
                    DialogueBuilder.Instance.Config = ModEntry.Config;

                    if (!ModEntry.Config.EnableSpouseSchedule)
                    {
                        CompanionScheduleManager.Instance.SafeDismissAllSpousesToHome();
                    }

                    if (!ModEntry.Config.EnableDateSystem && DateManager.Instance.Phase != DatePhase.None)
                    {
                        DateManager.Instance.AbortActiveDateSilently();
                    }

                    RefreshModelNamesCacheAsync();

                    if (!string.IsNullOrWhiteSpace(ModEntry.Config.ModelName))
                    {
                        SetLlm();
                    }

                    Register(modEntry);
                }
            );

            // =========================================================================
            // ── 主页面（默认页面） ──────────────────────────────────────────────────
            // =========================================================================

            ConfigMenu.AddBoolOption(
                mod: ModManifest,
                name: () => GetUIString("configEnable", "Enable Mod"),
                tooltip: () => GetUIString("configEnableTooltip", "Enable or disable the mod."),
                getValue: () => ModEntry.Config.EnableMod,
                setValue: value => ModEntry.Config.EnableMod = value
            );

            ConfigMenu.AddBoolOption(
                mod: ModManifest,
                name: () => GetUIString("configRespectAuthorConsent", "Respect Modder AI Consent"),
                tooltip: () => GetUIString("configRespectAuthorConsentTooltip",
                    "Enabled by default. Respects third-party mod authors' permitAiUse declarations. When disabled, AI dialogue is enabled for all custom NPCs."),
                getValue: () => ModEntry.Config.RespectAuthorAiConsent,
                setValue: value => ModEntry.Config.RespectAuthorAiConsent = value
            );

#if DEBUG
            ConfigMenu.AddBoolOption(
                mod: ModManifest,
                name: () => GetUIString("configLogging", "Enable Logging"),
                tooltip: () => GetUIString("configLoggingTooltip", "Enable or disable logging of prompts and responses."),
                getValue: () => ModEntry.Config.Debug,
                setValue: value => ModEntry.Config.Debug = value
            );
#endif

            var distinctLlmTypes = ModEntry.LlmMap.Keys
                .Where(k => !k.Equals("Dummy", StringComparison.OrdinalIgnoreCase)
                            && !k.Equals("LlmDummy", StringComparison.OrdinalIgnoreCase))
                .Where(k => !k.StartsWith("Llm", StringComparison.OrdinalIgnoreCase) || k.Equals("LlamaCpp", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            ConfigMenu.AddTextOption(
                mod: ModManifest,
                name: () => GetUIString("configProvider", "AI Model Provider"),
                getValue: () => ModEntry.Config.Provider,
                setValue: value =>
                {
                    ModEntry.Config.Provider = value;
                    _cachedModelNames = null;
                    RefreshModelNamesCacheAsync();
                },
                allowedValues: distinctLlmTypes,
                formatAllowedValue: FormatProvider,
                fieldId: "Provider"
            );

            if (!ModEntry.LlmMap.TryGetValue(ModEntry.Config.Provider, out var llmType))
            {
                llmType = typeof(LlmOAICompatible);
            }

            var constructorParameters = llmType.GetConstructors().First().GetParameters().Select(x => x.Name).ToArray();

            if (constructorParameters.Contains("apiKey", StringComparer.OrdinalIgnoreCase))
            {
                ConfigMenu.AddTextOption(
                    mod: ModManifest,
                    name: () => GetUIString("configApiKey", "API Key"),
                    tooltip: () => GetUIString("configApiKeyTooltip", "API Key for the AI model provider."),
                    getValue: () => ModEntry.Config.ApiKey,
                    setValue: value => ModEntry.Config.ApiKey = value?.Trim() ?? string.Empty,
                    fieldId: "ApiKey"
                );
            }

            // 状态指示灯
            ConfigMenu.AddParagraph(
                mod: ModManifest,
                text: () => GetConnectionStatusText()
            );

            if (constructorParameters.Contains("modelName", StringComparer.OrdinalIgnoreCase))
            {
                ConfigMenu.AddTextOption(
                    mod: ModManifest,
                    name: () => GetUIString("configModelName", "Model Name"),
                    tooltip: () => GetUIString("configModelNameTooltip", "Name of the AI model to use."),
                    getValue: () => ModEntry.Config.ModelName,
                    setValue: value => ModEntry.Config.ModelName = value?.Trim() ?? string.Empty,
                    fieldId: "ModelName"
                );

                if (_cachedModelNames != null && _cachedModelNames.Length > 0)
                {
                    string placeholder = GetUIString("configQuickSelectPlaceholder", "--- Select to auto-fill ---");
                    var quickSelectOptions = new List<string> { placeholder };
                    quickSelectOptions.AddRange(_cachedModelNames);

                    ConfigMenu.AddTextOption(
                        mod: ModManifest,
                        name: () => GetUIString("configQuickSelect", "Quick Select Model"),
                        tooltip: () => GetUIString("configQuickSelectTooltip",
                            "Select a model and click Save to fill into Model Name."),
                        getValue: () => placeholder,
                        setValue: value =>
                        {
                            if (value != placeholder)
                            {
                                ModEntry.Config.ModelName = value;
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
                        text: () => string.IsNullOrEmpty(_lastFetchErrorMessage)
                            ? GetUIString("configFetchHint", "Enter your API Key and click 'Save' to fetch available models.")
                            : GetUIString("configFetchError", "Failed to fetch models: ") + _lastFetchErrorMessage
                              + " " + GetUIString("configFetchErrorRetry", "(Click 'Save' to retry)")
                    );
                }
            }

            if (constructorParameters.Contains("url", StringComparer.OrdinalIgnoreCase))
            {
                ConfigMenu.AddTextOption(
                    mod: ModManifest,
                    name: () => GetUIString("configServerAddress", "Server Address"),
                    tooltip: () => GetUIString("configServerAddressTooltip",
                        "For Custom (OpenAI-Compatible) / VolcEngine: base URL, e.g. https://api.deepseek.com — missing https:// or /v1, trailing slashes, or pasted /chat/completions endings are auto-corrected. For Local (Llama.cpp/Ollama): the FULL endpoint, e.g. http://localhost:8080/completion or http://localhost:11434/api/generate."),
                    getValue: () => ModEntry.Config.ServerAddress,
                    setValue: value => ModEntry.Config.ServerAddress = value?.Trim() ?? string.Empty,
                    fieldId: "ServerAddress"
                );
            }

            ConfigMenu.AddTextOption(
                mod: ModManifest,
                name: () => GetUIString("configProxyMode", "Proxy Mode"),
                tooltip: () => GetUIString("configProxyModeTooltip", "Choose how the mod connects to LLM APIs. System uses OS settings, Direct bypasses any proxy, Custom lets you specify a proxy URL."),
                getValue: () => ModEntry.Config.ProxyMode.ToString(),
                setValue: value =>
                {
                    if (Enum.TryParse<ProxyMode>(value, out var parsed))
                        ModEntry.Config.ProxyMode = parsed;
                },
                allowedValues: new[] { "System", "Direct", "Custom" },
                formatAllowedValue: val => GetUIString($"configProxyMode_{val}", val)
            );

            ConfigMenu.AddTextOption(
                mod: ModManifest,
                name: () => GetUIString("configCustomProxyUrl", "Custom Proxy URL"),
                tooltip: () => GetUIString("configCustomProxyUrlTooltip", "Used when Proxy Mode is set to Custom. Enter a full URL including scheme, e.g. http://127.0.0.1:7890 or socks5://127.0.0.1:1080."),
                getValue: () => ModEntry.Config.CustomProxyUrl,
                setValue: value => ModEntry.Config.CustomProxyUrl = value?.Trim() ?? string.Empty
            );

            ConfigMenu.AddPageLink(
                mod: ModManifest,
                pageId: "advanced",
                text: () => GetUIString("configAdvancedTitle", "Advanced Model Parameters") + " →",
                tooltip: () => GetUIString("configAdvancedWarning",
                    "⚠️ Warning: If you are unsure what these settings do, please leave them at default!")
            );

            // ── 对话与输出选项（主页面） ──
            ConfigMenu.AddBoolOption(
                mod: ModManifest,
                name: () => GetUIString("configTranslation", "Translate Outputs"),
                tooltip: () => GetUIString("configTranslationTooltip",
                    "Translate the AI model outputs to the game language (without i18n pack)."),
                getValue: () => ModEntry.Config.ApplyTranslation,
                setValue: value => ModEntry.Config.ApplyTranslation = value
            );

            ConfigMenu.AddBoolOption(
                mod: ModManifest,
                name: () => GetUIString("configEnableSuggestedResponses", "Enable Suggested Responses"),
                tooltip: () => GetUIString("configEnableSuggestedResponsesTooltip",
                    "When enabled, AI generates 2-3 dialogue reply choices. Disable to save tokens and use pure typing."),
                getValue: () => ModEntry.Config.EnableSuggestedResponses,
                setValue: value => ModEntry.Config.EnableSuggestedResponses = value
            );

            ConfigMenu.AddBoolOption(
                mod: ModManifest,
                name: () => GetUIString("configEnableRhythmicTyping", "Rhythmic Dialogue Typing"),
                tooltip: () => GetUIString("configEnableRhythmicTypingTooltip",
                    "When enabled, dialogues pause naturally at punctuation and line breaks for realistic speech rhythm. Disable for uniform vanilla-speed typing."),
                getValue: () => ModEntry.Config.EnableRhythmicTyping,
                setValue: value => ModEntry.Config.EnableRhythmicTyping = value
            );

            ConfigMenu.AddBoolOption(
                mod: ModManifest,
                name: () => GetUIString("configChoiceBoxStyle", "Custom Choice Box"),
                tooltip: () => GetUIString("configChoiceBoxStyleTooltip",
                    "ON = custom floating choice panel (Stage 2). OFF = vanilla in-dialogue choice buttons (best compatibility)."),
                getValue: () => ModEntry.Config.ChoiceBoxStyle == ChoiceBoxStyle.Custom,
                setValue: value => ModEntry.Config.ChoiceBoxStyle = value ? ChoiceBoxStyle.Custom : ChoiceBoxStyle.Vanilla
            );

            ConfigMenu.AddBoolOption(
                mod: ModManifest,
                name: () => GetUIString("configRecordEventDialogue", "Record Event & Cutscene Dialogue"),
                tooltip: () => GetUIString("configRecordEventDialogueTooltip",
                    "Record NPC dialogue and farmer choices during events and cutscenes into the history."),
                getValue: () => ModEntry.Config.RecordEventDialogue,
                setValue: value => ModEntry.Config.RecordEventDialogue = value
            );

            ConfigMenu.AddBoolOption(
                mod: ModManifest,
                name: () => GetUIString("configDirectorEnableThinking", "Director Deep Thinking"),
                tooltip: () => GetUIString("configDirectorEnableThinkingTooltip",
                    "When enabled, AI cutscene director uses deep reasoning for richer drama (slower). When disabled, uses fast mode with thinking suppressed."),
                getValue: () => ModEntry.Config.DirectorEnableThinking,
                setValue: value => ModEntry.Config.DirectorEnableThinking = value
            );

            ConfigMenu.AddTextOption(
                mod: ModManifest,
                name: () => GetUIString("configFrequencyGeneral", "Frequency of general lines"),
                tooltip: () => GetUIString("configFrequencyGeneralTooltip",
                    "How often should the mod generate general lines."),
                getValue: () => ModEntry.Config.GeneralFrequency.ToString(),
                setValue: value =>
                {
                    if (int.TryParse(value, out int val))
                        ModEntry.Config.GeneralFrequency = Math.Clamp(val, 0, 4);
                },
                allowedValues: FrequencyValues,
                formatAllowedValue: FormatFrequency
            );

            ConfigMenu.AddTextOption(
                mod: ModManifest,
                name: () => GetUIString("configFrequencyGift", "Frequency of gift lines"),
                tooltip: () => GetUIString("configFrequencyGiftTooltip",
                    "How often should the mod generate gift lines."),
                getValue: () => ModEntry.Config.GiftFrequency.ToString(),
                setValue: value =>
                {
                    if (int.TryParse(value, out int val))
                        ModEntry.Config.GiftFrequency = Math.Clamp(val, 0, 4);
                },
                allowedValues: FrequencyValues,
                formatAllowedValue: FormatFrequency
            );

            ConfigMenu.AddTextOption(
                mod: ModManifest,
                name: () => GetUIString("configFrequencyMarriage", "Frequency of marriage lines"),
                tooltip: () => GetUIString("configFrequencyMarriageTooltip",
                    "How often should the mod generate marriage lines."),
                getValue: () => ModEntry.Config.MarriageFrequency.ToString(),
                setValue: value =>
                {
                    if (int.TryParse(value, out int val))
                        ModEntry.Config.MarriageFrequency = Math.Clamp(val, 0, 4);
                },
                allowedValues: FrequencyValues,
                formatAllowedValue: FormatFrequency
            );

            ConfigMenu.AddTextOption(
                mod: ModManifest,
                name: () => GetUIString("configDisableForCharacters", GetUIString("configDiableForCharacters", "Disable for specific NPCs")),
                tooltip: () => GetUIString("configDisableForCharactersTooltip", GetUIString("configDiableForCharactersTooltip",
                    "Comma-separated list of villagers to disable the mod for, e.g. (\"Abigail,Leah,Sam\")")),
                getValue: () => ModEntry.Config.DisableCharacters,
                setValue: value => ModEntry.Config.DisableCharacters = value
            );

            // ── 环境气泡与 NPC 互动 (Bark & A2A) ──
            ConfigMenu.AddSectionTitle(
                mod: ModManifest,
                text: () => GetUIString("configSectionAmbientDialogue", "Ambient & NPC Interactions")
            );

            ConfigMenu.AddBoolOption(
                mod: ModManifest,
                name: () => GetUIString("configEnableBark", "Enable NPC Self-Talk (Barks)"),
                tooltip: () => GetUIString("configEnableBarkTooltip",
                    "Allows nearby NPCs to display spontaneous overhead thought bubbles."),
                getValue: () => ModEntry.Config.EnableAmbientBarks,
                setValue: value => ModEntry.Config.EnableAmbientBarks = value
            );

            ConfigMenu.AddBoolOption(
                mod: ModManifest,
                name: () => GetUIString("configEnableA2A", "Enable NPC-to-NPC Conversations (A2A)"),
                tooltip: () => GetUIString("configEnableA2ATooltip",
                    "Allows NPCs who meet each other to engage in emergent dynamic conversations."),
                getValue: () => ModEntry.Config.EnableA2A,
                setValue: value => ModEntry.Config.EnableA2A = value
            );

            ConfigMenu.AddBoolOption(
                mod: ModManifest,
                name: () => GetUIString("configEnableProactiveMicroSocial", "Enable Proactive Micro-Social"),
                tooltip: () => GetUIString("configEnableProactiveMicroSocialTooltip",
                    "When disabled, all Barks fall back to Soliloquy (proactive micro-social interactions are disabled)."),
                getValue: () => ModEntry.Config.EnableProactiveMicroSocial,
                setValue: value => ModEntry.Config.EnableProactiveMicroSocial = value
            );

            ConfigMenu.AddNumberOption(
                mod: ModManifest,
                getValue: () => (int)(ModEntry.Config.MicroSocialMidFriendshipChance * 100),
                setValue: value => ModEntry.Config.MicroSocialMidFriendshipChance = value / 100f,
                name: () => GetUIString("configMicroSocialMidFriendshipChance", "Mid-Friendship Micro-Social Chance"),
                tooltip: () => GetUIString("configMicroSocialMidFriendshipChanceTooltip",
                    "Probability of triggering a micro-social interaction at 3-6 hearts; 7+ hearts always triggers, 0-2 hearts never triggers."),
                min: 0,
                max: 100,
                interval: 5
            );

            // ── 伴侣日程与出游系统 ──
            ConfigMenu.AddSectionTitle(
                mod: ModManifest,
                text: () => GetUIString("configSectionCompanionFeatures", "Companion & Romance Features")
            );

            ConfigMenu.AddBoolOption(
                mod: ModManifest,
                name: () => GetUIString("configEnableSpouseSchedule", "Enable Spouse Schedules"),
                tooltip: () => GetUIString("configEnableSpouseScheduleTooltip",
                    "Allows married spouses to have dynamic daily schedules, wander the farm, and visit locations around town."),
                getValue: () => ModEntry.Config.EnableSpouseSchedule,
                setValue: value => ModEntry.Config.EnableSpouseSchedule = value
            );

            ConfigMenu.AddBoolOption(
                mod: ModManifest,
                name: () => GetUIString("configEnableDateSystem", "Enable Date System (WIP)"),
                tooltip: () => GetUIString("configEnableDateSystemTooltip",
                    "Allows scheduling dates and romantic outings with eligible NPCs. Currently experimental and disabled by default."),
                getValue: () => ModEntry.Config.EnableDateSystem,
                setValue: value => ModEntry.Config.EnableDateSystem = value
            );

            // ── 时间线自动总结 ──
            ConfigMenu.AddSectionTitle(
                mod: ModManifest,
                text: () => GetUIString("configSectionAutoSummarize", "Timeline Auto-Summary")
            );

            // ── 日记蒸馏（DD401）：三选一模式 + 触发阈值 + 单 NPC 每日请求预算 ──
            ConfigMenu.AddTextOption(
                mod: ModManifest,
                name: () => GetUIString("configDailyDistillMode", "Daily Diary Distillation"),
                tooltip: () => GetUIString("configDailyDistillModeTooltip",
                    "How daily diary entries are produced: Intraday generates during the day, Overnight consolidates overnight, Disabled turns automatic daily diaries off."),
                getValue: () => ModEntry.Config.DailyDistillMode,
                setValue: value => ModEntry.Config.DailyDistillMode = value,
                allowedValues: new[] { "Disabled", "Overnight", "Intraday" },
                formatAllowedValue: value => value switch
                {
                    "Disabled" => GetUIString("configDailyDistillModeDisabled", "Disabled"),
                    "Overnight" => GetUIString("configDailyDistillModeOvernight", "Overnight"),
                    "Intraday" => GetUIString("configDailyDistillModeIntraday", "Intraday"),
                    _ => value
                },
                fieldId: "DailyDistillMode"
            );

            ConfigMenu.AddNumberOption(
                mod: ModManifest,
                name: () => GetUIString("configDailyDistillThreshold", "Daily Diary Trigger Threshold"),
                tooltip: () => GetUIString("configDailyDistillThresholdTooltip",
                    "Minimum interactions with an NPC yesterday required before a daily diary is distilled (2-10)."),
                getValue: () => ModEntry.Config.DailyDistillThreshold,
                setValue: value => ModEntry.Config.DailyDistillThreshold = value,
                min: 2,
                max: 10,
                interval: 1,
                fieldId: "DailyDistillThreshold"
            );

            ConfigMenu.AddNumberOption(
                mod: ModManifest,
                name: () => GetUIString("configDailyMaxRequestsPerNpc", "Daily Request Budget per NPC"),
                tooltip: () => GetUIString("configDailyMaxRequestsPerNpcTooltip",
                    "Maximum LLM requests per NPC per day for daily diary generation, including the first generation and failed attempts; up to two final consolidation passes may run the next day. The 35-second window is a request limit, not a completion promise."),
                getValue: () => ModEntry.Config.DailyMaxRequestsPerNpc,
                setValue: value => ModEntry.Config.DailyMaxRequestsPerNpc = value,
                min: 1,
                max: 5,
                interval: 1,
                fieldId: "DailyMaxRequestsPerNpc"
            );

            ConfigMenu.AddBoolOption(
                mod: ModManifest,
                name: () => GetUIString("configAutoSummarizeWeekly", "Auto-summarize Weekly Report"),
                tooltip: () => GetUIString("configAutoSummarizeWeeklyTooltip",
                    "Every Monday, if the past 7 days have >= 3 diary entries, automatically condense into a weekly report."),
                getValue: () => ModEntry.Config.AutoSummarizeWeekly,
                setValue: value => ModEntry.Config.AutoSummarizeWeekly = value
            );

            ConfigMenu.AddBoolOption(
                mod: ModManifest,
                name: () => GetUIString("configAutoSummarizeSeason", "Auto-summarize Season Report"),
                tooltip: () => GetUIString("configAutoSummarizeSeasonTooltip",
                    "On the 1st of each season, if last season has >= 2 weekly reports, automatically condense into a season report."),
                getValue: () => ModEntry.Config.AutoSummarizeSeason,
                setValue: value => ModEntry.Config.AutoSummarizeSeason = value
            );

            ConfigMenu.AddBoolOption(
                mod: ModManifest,
                name: () => GetUIString("configAutoSummarizeYearly", "Auto-summarize Yearly Report"),
                tooltip: () => GetUIString("configAutoSummarizeYearlyTooltip",
                    "On Spring 1 each year, if last year has >= 2 season reports, automatically condense into a yearly report."),
                getValue: () => ModEntry.Config.AutoSummarizeYearly,
                setValue: value => ModEntry.Config.AutoSummarizeYearly = value
            );

            // ── 快捷键设置 ──
            ConfigMenu.AddSectionTitle(
                mod: ModManifest,
                text: () => GetUIString("configSectionKeybinds", "Keybinds & Controls")
            );

            ConfigMenu.AddKeybind(
                mod: ModManifest,
                name: () => GetUIString("configInitiateKey", "Initiate Conversation Key"),
                tooltip: () => GetUIString("configInitiateKeyTooltip", "Hold this key and click an NPC to open the custom chat box."),
                getValue: () => ModEntry.Config.InitiateTypedDialogueKey,
                setValue: value => ModEntry.Config.InitiateTypedDialogueKey = value
            );

            ConfigMenu.AddKeybind(
                mod: ModManifest,
                name: () => GetUIString("configQuickChatKey", "Quick Chat Key"),
                tooltip: () => GetUIString("configQuickChatKeyTooltip", "Press this key when facing or near an NPC to open the custom chat box directly."),
                getValue: () => ModEntry.Config.QuickChatKey,
                setValue: value => ModEntry.Config.QuickChatKey = value
            );

            ConfigMenu.AddKeybind(
                mod: ModManifest,
                name: () => GetUIString("configDismissFollowerKey", "Dismiss Follower Key"),
                tooltip: () => GetUIString("configDismissFollowerKeyTooltip",
                    "Key used to stop the NPC currently following you, whether they're just tagging along or accompanying you on a date."),
                getValue: () => ModEntry.Config.DismissFollowerKey,
                setValue: value => ModEntry.Config.DismissFollowerKey = value
            );

            ConfigMenu.AddKeybind(
                mod: ModManifest,
                name: () => GetUIString("configOpenHubMenuKey", "Open Management Hub"),
                tooltip: () => GetUIString("configOpenHubMenuKeyTooltip",
                    "Press to open the combined management hub (NPC memories / world memories / farmer profile / advanced settings). Opens on your most recently spoken NPC. Avoid keys already used by the game or other mods."),
                getValue: () => ModEntry.Config.OpenHubMenuKey,
                setValue: value => ModEntry.Config.OpenHubMenuKey = value
            );

            ConfigMenu.AddKeybind(
                mod: ModManifest,
                name: () => GetUIString("configOpenTimelineKey", "Open Timeline Chronicle"),
                tooltip: () => GetUIString("configOpenTimelineKeyTooltip",
                    "Press to open the timeline chronicle (day-by-day chat history / layered memories). It automatically locks onto the NPC you chatted with most recently. Avoid keys already used by the game or other mods."),
                getValue: () => ModEntry.Config.OpenTimelineMenuKey,
                setValue: value => ModEntry.Config.OpenTimelineMenuKey = value
            );

            // =========================================================================
            // ── 二级子页面：高级参数（Page: "advanced"）────────────────────────────
            // =========================================================================
            ConfigMenu.AddPage(
                mod: ModManifest,
                pageId: "advanced",
                pageTitle: () => GetUIString("configAdvancedTitle", "Advanced Model Parameters")
            );

            ConfigMenu.AddParagraph(
                mod: ModManifest,
                text: () => GetUIString("configAdvancedWarning",
                    "⚠️ Warning: If you are unsure what these settings do, please leave them at default! Incorrect values can cause API errors or distorted NPC dialogue.")
            );

            ConfigMenu.AddNumberOption(
                mod: ModManifest,
                name: () => GetUIString("configTemperature", "Temperature (Creativity)"),
                tooltip: () => GetUIString("configTemperatureTooltip",
                    "Controls randomness and creativity. Range: 0.0 ~ 2.0, default 0.9."),
                getValue: () => ModEntry.Config.Temperature,
                setValue: value => ModEntry.Config.Temperature = value,
                min: 0.0f,
                max: 2.0f,
                interval: 0.05f
            );

            ConfigMenu.AddNumberOption(
                mod: ModManifest,
                name: () => GetUIString("configTopP", "Top_P (Nucleus Sampling)"),
                tooltip: () => GetUIString("configTopPTooltip",
                    "Nucleus sampling threshold for output diversity. Range: 0.0 ~ 1.0, default 0.9."),
                getValue: () => ModEntry.Config.TopP,
                setValue: value => ModEntry.Config.TopP = value,
                min: 0.0f,
                max: 1.0f,
                interval: 0.05f
            );

            ConfigMenu.AddNumberOption(
                mod: ModManifest,
                name: () => GetUIString("configMaxTokens", "Max Tokens (Max Length)"),
                tooltip: () => GetUIString("configMaxTokensTooltip",
                    "Maximum number of tokens per generation. Range: 100 ~ 8192, default 1024."),
                getValue: () => ModEntry.Config.MaxTokens,
                setValue: value => ModEntry.Config.MaxTokens = value,
                min: 100,
                max: 8192,
                interval: 50
            );

            ConfigMenu.AddNumberOption(
                mod: ModManifest,
                name: () => GetUIString("configLocalMaxConcurrentRequests", "Local Max Concurrent Requests"),
                tooltip: () => GetUIString("configLocalMaxConcurrentRequestsTooltip",
                    "How many requests may be in flight against a local (loopback / private-network) LLM endpoint at the same time. Range: 1 ~ 4, default 1. Cloud providers are never limited by this value."),
                getValue: () => ModEntry.Config.LocalMaxConcurrentRequests,
                setValue: value => ModEntry.Config.LocalMaxConcurrentRequests = value,
                min: 1,
                max: 4,
                interval: 1
            );

            ConfigMenu.AddTextOption(
                mod: ModManifest,
                name: () => GetUIString("configCustomBodyJson", "Custom Body JSON (Geek Mode)"),
                tooltip: () => GetUIString("configCustomBodyJsonTooltip",
                    "For advanced users: Enter valid JSON object to deep-merge into the request payload. Only applies to Main dialogue."),
                getValue: () => ModEntry.Config.CustomBodyJson,
                setValue: value => ModEntry.Config.CustomBodyJson = value
            );
        }

        private static string GetConnectionStatusText()
        {
            if (ProviderDefaults.IsMissingApiKeyBlocking(
                    ModEntry.Config.Provider, ModEntry.Config.ServerAddress, ModEntry.Config.ApiKey))
            {
                return GetUIString("configStatusNotConfigured", "Not Configured: Enter API Key and save");
            }

            bool llmDisabled = DialogueBuilder.Instance?.LlmDisabled ?? true;

            if (llmDisabled)
            {
                return GetUIString("configStatusFailed", "Connection Failed: Check API Key, network, or console logs");
            }

            string modelName = ModEntry.Config.ModelName;
            if (string.IsNullOrWhiteSpace(modelName))
            {
                modelName = GetUIString("configStatusNoModel", "(No model selected)");
            }

            return GetUIString("configStatusReady", "Connected / Ready: {{modelName}}", new { modelName });
        }

        private static void RefreshModelNamesCacheAsync()
        {
            _lastFetchErrorMessage = null;
            _cachedProvider = ModEntry.Config.Provider;

            Task.Run(async () =>
            {
                try
                {
                    ModelDiscoveryResult result = await GetModelNamesAsync();
                    string[] fetchedNames = result.ModelNames;

                    if (fetchedNames != null && fetchedNames.Length > 0)
                    {
                        var namesList = fetchedNames.ToList();
                        namesList.Sort();
                        _cachedModelNames = namesList.ToArray();
                        _lastFetchErrorMessage = null;
                    }
                    else
                    {
                        _cachedModelNames = Array.Empty<string>();
                        _lastFetchErrorMessage = BuildModelFetchErrorMessage(result);
                    }
                }
                catch (Exception ex)
                {
                    _modEntry?.Monitor.Log($"[ModConfigMenu] Error fetching model names: {ex.Message}", LogLevel.Warn);
                    _cachedModelNames = Array.Empty<string>();
                    _lastFetchErrorMessage = ex.Message;
                }
                finally
                {
                    if (_modEntry != null)
                    {
                        _modEntry.Helper.Events.GameLoop.UpdateTicked += OnUpdateTickedToRefreshUi;
                    }
                }
            });
        }

        private static void OnUpdateTickedToRefreshUi(object sender, StardewModdingAPI.Events.UpdateTickedEventArgs e)
        {
            if (_modEntry != null)
            {
                _modEntry.Helper.Events.GameLoop.UpdateTicked -= OnUpdateTickedToRefreshUi;
                Register(_modEntry);
            }
        }

        private static string BuildModelFetchErrorMessage(ModelDiscoveryResult result)
        {
            (string key, string fallback) = result.Failure switch
            {
                ModelDiscoveryFailure.MissingApiKey => ("configModelFetchMissingApiKey",
                    "No API key: fill in the API key and save, or type the model name manually."),
                ModelDiscoveryFailure.InvalidUrl => ("configModelFetchInvalidUrl",
                    "Invalid server address: check the Server Address field (scheme, host and port), or type the model name manually."),
                ModelDiscoveryFailure.Transport => ("configModelFetchTransport",
                    "Cannot reach the server: start your local model service or check the address, then save to retry."),
                ModelDiscoveryFailure.Http => ("configModelFetchHttp",
                    $"Server returned HTTP {result.StatusCode}: check the address and API key. {result.Detail}"),
                ModelDiscoveryFailure.InvalidJson => ("configModelFetchInvalidJson",
                    $"Unreadable response: the server did not return valid JSON. {result.Detail}"),
                ModelDiscoveryFailure.UnsupportedSchema => ("configModelFetchUnsupportedSchema",
                    $"Unexpected response format: no model list found. {result.Detail}"),
                _ => ("configModelFetchEmptyResponse",
                    "Server returned an empty model list: you can type the model name manually.")
            };

            return GetUIString(key, fallback, new { statusCode = result.StatusCode, detail = result.Detail });
        }

        private static async Task<ModelDiscoveryResult> GetModelNamesAsync()
        {
            // 公共 Provider 空 Key：不发无效请求，前置返回 MissingApiKey 诊断。
            // 本地服务商（Ollama / LMStudio）与回环地址放行无 Key 模型列表拉取。
            if (ProviderDefaults.IsMissingApiKeyBlocking(
                    ModEntry.Config.Provider, ModEntry.Config.ServerAddress, ModEntry.Config.ApiKey))
                return ModelDiscovery.Failed(ModelDiscoveryFailure.MissingApiKey, "API key is empty for a public provider.");

            if (!ModEntry.LlmMap.TryGetValue(ModEntry.Config.Provider, out var provider))
                return ModelDiscovery.Failed(ModelDiscoveryFailure.EmptyResponse, "Provider is not registered.");

            if (typeof(IGetModelNames).IsAssignableFrom(provider))
            {
                string currentModel = string.IsNullOrWhiteSpace(ModEntry.Config.ModelName)
                    ? "placeholder-for-fetching"
                    : ModEntry.Config.ModelName;

                var paramsDict = new Dictionary<string, string>()
                {
                    { "apiKey", ModEntry.Config.ApiKey },
                    { "modelName", currentModel },
                    { "url", ProviderDefaults.ResolveServerAddress(ModEntry.Config.Provider, ModEntry.Config.ServerAddress) },
                    { "promptFormat", ModEntry.Config.PromptFormat }
                };

                try
                {
                    var instance = Llm.CreateInstance(provider, paramsDict);

                    if (instance is IModelDiscoveryDiagnostics diagnostics)
                    {
                        return await diagnostics.GetModelNamesWithDiagnosticsAsync();
                    }

                    // 非 OpenAI 族 Provider（Claude / Gemini）保持 legacy 路径。
                    string[] legacyNames = await ((IGetModelNames)instance).GetModelNamesAsync();

                    return legacyNames != null && legacyNames.Length > 0
                        ? ModelDiscovery.Success(legacyNames)
                        : ModelDiscovery.Failed(ModelDiscoveryFailure.EmptyResponse, "Provider returned no models.");
                }
                catch (Exception ex)
                {
                    _modEntry?.Monitor.Log($"[ModConfigMenu] Failed to get model names: {ex.Message}", LogLevel.Warn);
                    throw;
                }
            }

            return ModelDiscovery.Failed(ModelDiscoveryFailure.EmptyResponse, "Provider does not support model discovery.");
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

            Llm.SetLlm(llmType, apiKey: ModEntry.Config.ApiKey, modelName: ModEntry.Config.ModelName,
                url: ProviderDefaults.ResolveServerAddress(ModEntry.Config.Provider, ModEntry.Config.ServerAddress),
                promptFormat: ModEntry.Config.PromptFormat);
        }
    }
}