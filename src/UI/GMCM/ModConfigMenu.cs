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

            // 注册文本框左右键及光标移动拦截器
            RegisterGlobalTextBoxNavigationHook(modEntry.Helper);

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

                    // ★ 即时响应开关关闭：秒杀正在进行的会话与残余台词
                    ModEntry.CleanupOnConfigToggle();

                    // ── 配偶日程开关关闭：安全送回所有在外配偶 ──
                    if (!ModEntry.Config.EnableSpouseSchedule)
                    {
                        CompanionScheduleManager.Instance.SafeDismissAllSpousesToHome();
                    }

                    // ── 约会系统开关关闭：静默终止进行中约会（不注销 SMAPI 事件）──
                    if (!ModEntry.Config.EnableDateSystem
                        && DateManager.Instance.Phase != DatePhase.None)
                    {
                        DateManager.Instance.AbortActiveDateSilently();
                    }

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

            // 🌟 尊重第三方作者 AI 授权选项
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
                tooltip: () =>
                    GetUIString("configLoggingTooltip", "Enable or disable logging of prompts and responses."),
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
                    string placeholder = GetUIString("configQuickSelectPlaceholder", "--- Select to auto-fill ---");
                    var quickSelectOptions = new List<string> { placeholder };
                    quickSelectOptions.AddRange(_cachedModelNames);

                    ConfigMenu.AddTextOption(
                        mod: ModManifest,
                        name: () => GetUIString("configQuickSelect", "Quick Select Model"),
                        tooltip: () => GetUIString("configQuickSelectTooltip",
                            "Select a model and click Save to fill into Model Name."),
                        getValue: () => placeholder,
                        setValue: (value) =>
                        {
                            if (value != placeholder)
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
                        text: () => GetUIString("configFetchHint",
                            "Enter API Key and click 'Save' to fetch available models.")
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
                getValue: () => Config.GeneralFrequency.ToString(),
                setValue: (value) =>
                {
                    if (int.TryParse(value, out int val))
                        Config.GeneralFrequency = Math.Clamp(val, 0, 4);
                },
                allowedValues: FrequencyValues,
                formatAllowedValue: FormatFrequency
            );

            ConfigMenu.AddTextOption(
                mod: ModManifest,
                name: () => GetUIString("configFrequencyGift", "Frequency of gift responses"),
                tooltip: () =>
                    GetUIString("configFrequencyGiftTooltip", "How often should the mod generate gift responses."),
                getValue: () => Config.GiftFrequency.ToString(),
                setValue: (value) =>
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
                setValue: (value) =>
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
                setValue: (value) => { Config.DisableCharacters = value; }
            );

            // ── ★ 环境气泡与 NPC 互动 (Bark & A2A) ─────────────────
            ConfigMenu.AddSectionTitle(
                mod: ModManifest,
                text: () => GetUIString("configSectionAmbientDialogue", "Ambient & NPC Interactions")
            );

            // 1. Bark 随地气泡开关
            ConfigMenu.AddBoolOption(
                mod: ModManifest,
                name: () => GetUIString("configEnableBark", "Enable NPC Self-Talk (Barks)"),
                tooltip: () => GetUIString("configEnableBarkTooltip",
                    "Allows nearby NPCs to display spontaneous overhead thought bubbles."),
                getValue: () => Config.EnableAmbientBarks,
                setValue: value => Config.EnableAmbientBarks = value
            );

            // 2. A2A NPC间对话开关
            ConfigMenu.AddBoolOption(
                mod: ModManifest,
                name: () => GetUIString("configEnableA2A", "Enable NPC-to-NPC Conversations (A2A)"),
                tooltip: () => GetUIString("configEnableA2ATooltip",
                    "Allows NPCs who meet each other to engage in emergent dynamic conversations."),
                getValue: () => Config.EnableA2A,
                setValue: value => Config.EnableA2A = value
            );

            // ── 伴侣日程与出游系统 ─────────────────────────────────
            ConfigMenu.AddSectionTitle(
                mod: ModManifest,
                text: () => GetUIString("configSectionCompanionFeatures", "Companion & Romance Features")
            );

            // 1. 配偶日程开关 (Default: true)
            ConfigMenu.AddBoolOption(
                mod: ModManifest,
                name: () => GetUIString("configEnableSpouseSchedule", "Enable Spouse Schedules"),
                tooltip: () => GetUIString("configEnableSpouseScheduleTooltip",
                    "Allows married spouses to have dynamic daily schedules, wander the farm, and visit locations around town."),
                getValue: () => Config.EnableSpouseSchedule,
                setValue: value => Config.EnableSpouseSchedule = value
            );

            // 2. 约会系统开关 (Default: false)
            ConfigMenu.AddBoolOption(
                mod: ModManifest,
                name: () => GetUIString("configEnableDateSystem", "Enable Date System (WIP)"),
                tooltip: () => GetUIString("configEnableDateSystemTooltip",
                    "Allows scheduling dates and romantic outings with eligible NPCs. Currently experimental and disabled by default."),
                getValue: () => Config.EnableDateSystem,
                setValue: value => Config.EnableDateSystem = value
            );

            // ── ★ 快捷键与控制设置 Section ──────────────────────
            ConfigMenu.AddSectionTitle(
                mod: ModManifest,
                text: () => GetUIString("configSectionKeybinds", "Keybinds & Controls")
            );

            // 1. 打字对话按键
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

            // 3. 取消跟随快捷键
            ConfigMenu.AddKeybind(
                mod: ModManifest,
                name: () => GetUIString("configDismissFollowerKey", "Dismiss Follower Key"),
                getValue: () => ModEntry.Config.DismissFollowerKey,
                setValue: (value) => ModEntry.Config.DismissFollowerKey = value,
                tooltip: () => GetUIString("configDismissFollowerKeyTooltip",
                    "Key to dismiss the currently following NPC (regular or date).")
            );
        }

        /// <summary>
        /// 注册全局按键拦截，适配 GMCM 中文本输入框的左右方向键、Home/End、Delete 移动光标功能
        /// </summary>
        private static void RegisterGlobalTextBoxNavigationHook(IModHelper helper)
        {
            if (_isInputHookRegistered || helper == null) return;

            helper.Events.Input.ButtonPressed += OnButtonPressedHandleTextBoxCursor;
            _isInputHookRegistered = true;
        }

        /// <summary>
        /// 监听全局按键：若当前玩家正在 GMCM 等界面的文本框中编辑，拦截并接管左右导航与特殊编辑按键
        /// </summary>
        private static void OnButtonPressedHandleTextBoxCursor(object sender, ButtonPressedEventArgs e)
        {
            var subscriber = Game1.keyboardDispatcher?.Subscriber;
            if (subscriber == null) return;

            // 1. 如果使用了模组自建的 DialogueTextInputBox，直接转发 RecieveSpecialInput
            if (subscriber is DialogueTextInputBox customBox && customBox.Selected)
            {
                if (IsControlKeyDown()) return; // 避免与系统的复制/粘贴冲突

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

            // 2. 如果是 GMCM 或原版的 TextBox/TextBoxEventSubscriber
            if (subscriber is TextBox vanillaTextBox && vanillaTextBox.Selected)
            {
                if (IsControlKeyDown()) return;

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

        private static readonly FieldInfo CursorPositionField = typeof(TextBox).GetField("_cursorPosition", BindingFlags.NonPublic | BindingFlags.Instance)
                                                              ?? typeof(TextBox).GetField("cursorPosition", BindingFlags.NonPublic | BindingFlags.Instance);

        /// <summary>
        /// 针对原版或 GMCM 内部 TextBox 的光标控制逻辑
        /// </summary>
        private static bool HandleVanillaTextBoxNavigation(TextBox textBox, Keys key)
        {
            if (textBox == null) return false;
            string currentText = textBox.Text ?? string.Empty;

            // 获取当前光标位置（通过反射读取内部 _cursorPosition，读取失败则降级以末尾计）
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

            switch (key)
            {
                case Keys.Left:
                    if (cursor > 0)
                    {
                        cursor--;
                        SetTextBoxCursor(textBox, cursor);
                        return true;
                    }
                    break;

                case Keys.Right:
                    if (cursor < currentText.Length)
                    {
                        cursor++;
                        SetTextBoxCursor(textBox, cursor);
                        return true;
                    }
                    break;

                case Keys.Home:
                    cursor = 0;
                    SetTextBoxCursor(textBox, cursor);
                    return true;

                case Keys.End:
                    cursor = currentText.Length;
                    SetTextBoxCursor(textBox, cursor);
                    return true;

                case Keys.Delete:
                    if (cursor < currentText.Length)
                    {
                        textBox.Text = currentText.Remove(cursor, 1);
                        SetTextBoxCursor(textBox, cursor);
                        return true;
                    }
                    break;
            }

            return false;
        }

        private static void SetTextBoxCursor(TextBox textBox, int newCursor)
        {
            if (CursorPositionField != null)
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

            Llm.SetLlm(llmType, apiKey: ModEntry.Config.ApiKey, modelName: ModEntry.Config.ModelName,
                url: ModEntry.Config.ServerAddress, promptFormat: ModEntry.Config.PromptFormat);
        }
    }
}