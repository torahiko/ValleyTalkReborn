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
        private static bool _isInputHookRegistered = false;
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

        /// <summary>
        /// 双向兼容映射：优先读取 configProvider_Key，若未命中自动尝试别名
        /// </summary>
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

        private static string GetUIString(string key, string fallback, object tokens = null)
        {
            string result = null;
            if (_modEntry?.Helper?.Translation != null)
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

            // 注册文本框光标导航拦截钩子
            RegisterGlobalTextBoxNavigationHook(modEntry.Helper);

            if (ConfigMenu == null)
            {
                modEntry.Monitor.Log(GetUIString("configGmcmNotInstalled", "Generic Mod Config Menu not installed."),
                    LogLevel.Warn);
                return;
            }

            if (!ModEntry.LlmMap.ContainsKey(Config.Provider))
            {
                Config.Provider = "OpenAiCompatible";
            }

            string editingProvider = Config.Provider;

            ConfigMenu.Unregister(ModManifest);
            ConfigMenu.Register(
                mod: ModManifest,
                reset: () => ModEntry.Config = new ModConfig(),
                save: () =>
                {
                    modEntry.Helper.WriteConfig(ModEntry.Config);

                    ModEntry.CleanupOnConfigToggle();

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
                getValue: () => Config.EnableMod,
                setValue: value => Config.EnableMod = value
            );

            ConfigMenu.AddBoolOption(
                mod: ModManifest,
                name: () => GetUIString("configRespectAuthorConsent", "Respect Modder AI Consent"),
                tooltip: () => GetUIString("configRespectAuthorConsentTooltip",
                    "Enabled by default. Respects third-party mod authors' permitAiUse declarations. When disabled, AI dialogue is enabled for all custom NPCs."),
                getValue: () => Config.RespectAuthorAiConsent,
                setValue: value => Config.RespectAuthorAiConsent = value
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

            // ★ 修复 1：下拉选项列表去重，剔除别名冗余（排除带 Llm 前缀的别名，防止出现 2 个自定义）
            var distinctLlmTypes = ModEntry.LlmMap.Keys
                .Where(k => !k.StartsWith("Llm", StringComparison.OrdinalIgnoreCase) || k.Equals("LlamaCpp", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            ConfigMenu.AddTextOption(
                mod: ModManifest,
                name: () => GetUIString("configProvider", "AI Model Provider"),
                getValue: () => Config.Provider,
                setValue: value =>
                {
                    Config.Provider = value;
                    _cachedModelNames = null;
                    RefreshModelNamesCacheAsync();
                },
                allowedValues: distinctLlmTypes,
                formatAllowedValue: FormatProvider,
                fieldId: "Provider"
            );

            // GMCM 机制提示
            ConfigMenu.AddParagraph(
                mod: ModManifest,
                text: () => GetUIString("configFetchHintFull",
                    "[Tip] Enter your API Key (and Server Address if needed), click 'Save', then exit and re-open this menu. The mod will automatically fetch available models. Switching providers also requires saving and reopening.")
            );

            if (!ModEntry.LlmMap.TryGetValue(Config.Provider, out var llmType))
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
                    getValue: () => Config.ApiKey,
                    setValue: value =>
                    {
                        if (Config.Provider == editingProvider)
                        {
                            Config.ApiKey = value;
                        }
                    },
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
                    getValue: () => Config.ModelName,
                    setValue: value =>
                    {
                        if (Config.Provider == editingProvider)
                        {
                            Config.ModelName = value;
                        }
                    },
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
                            if (value != placeholder && Config.Provider == editingProvider)
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
                    // ★ 修复 2：完整多语言支持，绝不出现硬编码未翻译的英文
                    string errorHint = string.IsNullOrEmpty(_lastFetchErrorMessage)
                        ? GetUIString("configFetchHint", "Enter your API Key and click 'Save' to fetch available models.")
                        : GetUIString("configFetchError", "⚠️ Failed to fetch models: ") + _lastFetchErrorMessage
                          + " " + GetUIString("configFetchErrorRetry", "(Click 'Save' to retry)");
                    ConfigMenu.AddParagraph(
                        mod: ModManifest,
                        text: () => errorHint
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
                    setValue: value =>
                    {
                        if (Config.Provider == editingProvider)
                        {
                            Config.ServerAddress = value;
                        }
                    },
                    fieldId: "ServerAddress"
                );
            }

            ConfigMenu.AddPageLink(
                mod: ModManifest,
                pageId: "advanced",
                text: () => GetUIString("configAdvancedTitle", "⚙️ Advanced Model Parameters") + " →",
                tooltip: () => GetUIString("configAdvancedWarning",
                    "⚠️ Warning: If you are unsure what these settings do, please leave them at default!")
            );

            // ── 对话与输出选项（主页面） ──
            ConfigMenu.AddBoolOption(
                mod: ModManifest,
                name: () => GetUIString("configTranslation", "Translate Outputs"),
                tooltip: () => GetUIString("configTranslationTooltip",
                    "Translate the AI model outputs to the game language (without i18n pack)."),
                getValue: () => Config.ApplyTranslation,
                setValue: value => Config.ApplyTranslation = value
            );

            ConfigMenu.AddTextOption(
                mod: ModManifest,
                name: () => GetUIString("configFrequencyGeneral", "Frequency of general lines"),
                tooltip: () => GetUIString("configFrequencyGeneralTooltip",
                    "How often should the mod generate general lines."),
                getValue: () => Config.GeneralFrequency.ToString(),
                setValue: value =>
                {
                    if (int.TryParse(value, out int val))
                        Config.GeneralFrequency = Math.Clamp(val, 0, 4);
                },
                allowedValues: FrequencyValues,
                formatAllowedValue: FormatFrequency
            );

            ConfigMenu.AddTextOption(
                mod: ModManifest,
                name: () => GetUIString("configFrequencyGift", "Frequency of gift lines"),
                tooltip: () => GetUIString("configFrequencyGiftTooltip",
                    "How often should the mod generate gift lines."),
                getValue: () => Config.GiftFrequency.ToString(),
                setValue: value =>
                {
                    if (int.TryParse(value, out int val))
                        Config.GiftFrequency = Math.Clamp(val, 0, 4);
                },
                allowedValues: FrequencyValues,
                formatAllowedValue: FormatFrequency
            );

            ConfigMenu.AddTextOption(
                mod: ModManifest,
                name: () => GetUIString("configFrequencyMarriage", "Frequency of marriage lines"),
                tooltip: () => GetUIString("configFrequencyMarriageTooltip",
                    "How often should the mod generate marriage lines."),
                getValue: () => Config.MarriageFrequency.ToString(),
                setValue: value =>
                {
                    if (int.TryParse(value, out int val))
                        Config.MarriageFrequency = Math.Clamp(val, 0, 4);
                },
                allowedValues: FrequencyValues,
                formatAllowedValue: FormatFrequency
            );

            ConfigMenu.AddTextOption(
                mod: ModManifest,
                name: () => GetUIString("configDisableForCharacters", GetUIString("configDiableForCharacters", "Disable for specific NPCs")),
                tooltip: () => GetUIString("configDisableForCharactersTooltip", GetUIString("configDiableForCharactersTooltip",
                    "Comma-separated list of villagers to disable the mod for, e.g. (\"Abigail,Leah,Sam\")")),
                getValue: () => Config.DisableCharacters,
                setValue: value => Config.DisableCharacters = value
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
                getValue: () => Config.EnableAmbientBarks,
                setValue: value => Config.EnableAmbientBarks = value
            );

            ConfigMenu.AddBoolOption(
                mod: ModManifest,
                name: () => GetUIString("configEnableA2A", "Enable NPC-to-NPC Conversations (A2A)"),
                tooltip: () => GetUIString("configEnableA2ATooltip",
                    "Allows NPCs who meet each other to engage in emergent dynamic conversations."),
                getValue: () => Config.EnableA2A,
                setValue: value => Config.EnableA2A = value
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
                getValue: () => Config.EnableSpouseSchedule,
                setValue: value => Config.EnableSpouseSchedule = value
            );

            ConfigMenu.AddBoolOption(
                mod: ModManifest,
                name: () => GetUIString("configEnableDateSystem", "Enable Date System (WIP)"),
                tooltip: () => GetUIString("configEnableDateSystemTooltip",
                    "Allows scheduling dates and romantic outings with eligible NPCs. Currently experimental and disabled by default."),
                getValue: () => Config.EnableDateSystem,
                setValue: value => Config.EnableDateSystem = value
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
                getValue: () => Config.InitiateTypedDialogueKey,
                setValue: value => Config.InitiateTypedDialogueKey = value
            );

            ConfigMenu.AddKeybind(
                mod: ModManifest,
                name: () => GetUIString("configQuickReplyKey", "Quick Reply Key"),
                tooltip: () => GetUIString("configQuickReplyKeyTooltip", "Press this key within 5 seconds after an NPC speaks to send a quick follow-up reply."),
                getValue: () => Config.QuickReplyKey,
                setValue: value => Config.QuickReplyKey = value
            );

            ConfigMenu.AddKeybind(
                mod: ModManifest,
                name: () => GetUIString("configDismissFollowerKey", "Dismiss Follower Key"),
                tooltip: () => GetUIString("configDismissFollowerKeyTooltip", "Key to dismiss the currently following NPC (regular or date)."),
                getValue: () => Config.DismissFollowerKey,
                setValue: value => Config.DismissFollowerKey = value
            );

            // =========================================================================
            // ── ★ 二级子页面：高级参数（Page: "advanced"）────────────────────────────
            // =========================================================================
            ConfigMenu.AddPage(
                mod: ModManifest,
                pageId: "advanced",
                pageTitle: () => GetUIString("configAdvancedTitle", "⚙️ Advanced Model Parameters")
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
                getValue: () => Config.Temperature,
                setValue: value => Config.Temperature = value,
                min: 0.0f,
                max: 2.0f,
                interval: 0.05f
            );

            ConfigMenu.AddNumberOption(
                mod: ModManifest,
                name: () => GetUIString("configTopP", "Top_P (Nucleus Sampling)"),
                tooltip: () => GetUIString("configTopPTooltip",
                    "Nucleus sampling threshold for output diversity. Range: 0.0 ~ 1.0, default 0.9."),
                getValue: () => Config.TopP,
                setValue: value => Config.TopP = value,
                min: 0.0f,
                max: 1.0f,
                interval: 0.05f
            );

            ConfigMenu.AddNumberOption(
                mod: ModManifest,
                name: () => GetUIString("configMaxTokens", "Max Tokens (Max Length)"),
                tooltip: () => GetUIString("configMaxTokensTooltip",
                    "Maximum number of tokens per generation. Range: 100 ~ 8192, default 1024."),
                getValue: () => Config.MaxTokens,
                setValue: value => Config.MaxTokens = value,
                min: 100,
                max: 8192,
                interval: 50
            );

            ConfigMenu.AddTextOption(
                mod: ModManifest,
                name: () => GetUIString("configCustomBodyJson", "Custom Body JSON (Geek Mode)"),
                tooltip: () => GetUIString("configCustomBodyJsonTooltip",
                    "For advanced users: Enter valid JSON object to deep-merge into the request payload. Only applies to Main dialogue."),
                getValue: () => Config.CustomBodyJson,
                setValue: value => Config.CustomBodyJson = value
            );
        }

        private static string GetConnectionStatusText()
        {
            if (string.IsNullOrWhiteSpace(ModEntry.Config.ApiKey))
            {
                return GetUIString("configStatusNotConfigured", "⚪ Not Configured: Enter API Key and save");
            }

            bool llmDisabled = DialogueBuilder.Instance?.LlmDisabled ?? true;

            if (llmDisabled)
            {
                return GetUIString("configStatusFailed", "❌ Connection Failed: Check API Key, network, or console logs");
            }

            string modelName = ModEntry.Config.ModelName;
            if (string.IsNullOrWhiteSpace(modelName))
            {
                modelName = GetUIString("configStatusNoModel", "(No model selected)");
            }

            return GetUIString("configStatusReady", "✅ Connected / Ready: {{modelName}}", new { modelName });
        }

        // =========================================================================
        // ── ★ 修复 3：全局文本框键盘导航与光标拦截实现 ─────────────────────────
        // =========================================================================

        private static readonly FieldInfo CursorPositionField = typeof(TextBox).GetField("_cursorPosition", BindingFlags.NonPublic | BindingFlags.Instance)
                                                              ?? typeof(TextBox).GetField("cursorPosition", BindingFlags.NonPublic | BindingFlags.Instance);

        private static void RegisterGlobalTextBoxNavigationHook(IModHelper helper)
        {
            if (_isInputHookRegistered || helper == null) return;

            helper.Events.Input.ButtonPressed += OnButtonPressedHandleTextBoxCursor;
            _isInputHookRegistered = true;
        }

        private static void OnButtonPressedHandleTextBoxCursor(object sender, ButtonPressedEventArgs e)
        {
            // 通过游戏全局键盘调度器直接获取当前处于焦点选中的输入框
            var subscriber = Game1.keyboardDispatcher?.Subscriber;
            if (subscriber == null) return;

            // 1. 若使用的是模组自建打字输入框
            if (subscriber is DialogueTextInputBox customBox && customBox.Selected)
            {
                if (IsControlKeyDown()) return;

                if (e.Button == SButton.Left || e.Button == SButton.Right ||
                    e.Button == SButton.Home || e.Button == SButton.End ||
                    e.Button == SButton.Delete || e.Button == SButton.Back)
                {
                    if (e.Button.TryGetKeyboard(out Keys k))
                    {
                        customBox.RecieveSpecialInput(k);
                        _modEntry?.Helper?.Input.Suppress(e.Button);
                    }
                }
                return;
            }

            // 2. 原版 TextBox 或 GMCM 内部的文本框
            if (subscriber is TextBox vanillaTextBox && vanillaTextBox.Selected)
            {
                // 支持快捷键导航：左、右、Home、End、Delete
                if (e.Button == SButton.Left || e.Button == SButton.Right ||
                    e.Button == SButton.Home || e.Button == SButton.End ||
                    e.Button == SButton.Delete)
                {
                    if (e.Button.TryGetKeyboard(out Keys k))
                    {
                        if (HandleVanillaTextBoxNavigation(vanillaTextBox, k))
                        {
                            _modEntry?.Helper?.Input.Suppress(e.Button);
                        }
                    }
                }
            }
        }

        private static bool HandleVanillaTextBoxNavigation(TextBox textBox, Keys key)
        {
            if (textBox == null) return false;
            string currentText = textBox.Text ?? string.Empty;

            int cursor = currentText.Length;
            if (CursorPositionField != null)
            {
                try
                {
                    cursor = (int)CursorPositionField.GetValue(textBox);
                }
                catch
                {
                    cursor = currentText.Length;
                }
            }

            cursor = Math.Clamp(cursor, 0, currentText.Length);
            bool ctrl = IsControlKeyDown();

            switch (key)
            {
                case Keys.Left:
                    if (ctrl)
                    {
                        // Ctrl + 左箭头：按单词左移
                        int newPos = cursor - 1;
                        while (newPos > 0 && char.IsWhiteSpace(currentText[newPos])) newPos--;
                        while (newPos > 0 && !char.IsWhiteSpace(currentText[newPos - 1])) newPos--;
                        SetTextBoxCursor(textBox, Math.Max(0, newPos));
                    }
                    else if (cursor > 0)
                    {
                        SetTextBoxCursor(textBox, cursor - 1);
                    }
                    return true;

                case Keys.Right:
                    if (ctrl)
                    {
                        // Ctrl + 右箭头：按单词右移
                        int newPos = cursor;
                        while (newPos < currentText.Length && !char.IsWhiteSpace(currentText[newPos])) newPos++;
                        while (newPos < currentText.Length && char.IsWhiteSpace(currentText[newPos])) newPos++;
                        SetTextBoxCursor(textBox, Math.Min(currentText.Length, newPos));
                    }
                    else if (cursor < currentText.Length)
                    {
                        SetTextBoxCursor(textBox, cursor + 1);
                    }
                    return true;

                case Keys.Home:
                    SetTextBoxCursor(textBox, 0);
                    return true;

                case Keys.End:
                    SetTextBoxCursor(textBox, currentText.Length);
                    return true;

                case Keys.Delete:
                    if (cursor < currentText.Length)
                    {
                        textBox.Text = currentText.Remove(cursor, 1);
                        SetTextBoxCursor(textBox, cursor);
                    }
                    return true;
            }

            return false;
        }

        private static void SetTextBoxCursor(TextBox textBox, int newCursor)
        {
            if (CursorPositionField != null && textBox != null)
            {
                try
                {
                    CursorPositionField.SetValue(textBox, Math.Clamp(newCursor, 0, (textBox.Text ?? string.Empty).Length));
                }
                catch { }
            }
        }

        private static bool IsControlKeyDown()
        {
            var state = Game1.input.GetKeyboardState();
            return state.IsKeyDown(Keys.LeftControl) || state.IsKeyDown(Keys.RightControl);
        }

        private static void RefreshModelNamesCacheAsync()
        {
            _lastFetchErrorMessage = null;
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
                        _lastFetchErrorMessage = null;

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
                    _lastFetchErrorMessage = ex.Message;
                }
            });
        }

        private static void OnUpdateTickedToRefreshUi(object sender, StardewModdingAPI.Events.UpdateTickedEventArgs e)
        {
            if (_modEntry != null)
            {
                Register(_modEntry);
                _modEntry.Helper.Events.GameLoop.UpdateTicked -= OnUpdateTickedToRefreshUi;
            }
        }

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

            Llm.SetLlm(llmType, apiKey: ModEntry.Config.ApiKey, modelName: ModEntry.Config.ModelName,
                url: ModEntry.Config.ServerAddress, promptFormat: ModEntry.Config.PromptFormat);
        }
    }
}