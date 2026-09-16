using System;
using System.Collections.Generic;
using System.Linq;
using StardewModdingAPI;

namespace ValleytalkReborn
{
    public enum SafetyModeLevel { Off, Loose, Moderate, Strict }

    public class ProviderProfile
    {
        public string ApiKey { get; set; } = string.Empty;
        public string ServerAddress { get; set; } = string.Empty;
        public string ModelName { get; set; } = string.Empty;
        public string CustomBodyJson { get; set; } = string.Empty;
    }

    public class ModConfig
    {
        // ── 老版本配置迁移备份字段（仅用于迁移，不对外暴露） ──
        #pragma warning disable CS0414 // 字段经反射读取，非真正未使用
        [Obsolete("Legacy field for migration from pre-1.7 versions. Will be removed in v1.8.0.")]
        [Newtonsoft.Json.JsonProperty("_legacyApiKey")]
        private string _legacyApiKey = null;

        [Obsolete("Legacy field for migration from pre-1.7 versions. Will be removed in v1.8.0.")]
        [Newtonsoft.Json.JsonProperty("_legacyServerAddress")]
        private string _legacyServerAddress = null;

        [Obsolete("Legacy field for migration from pre-1.7 versions. Will be removed in v1.8.0.")]
        [Newtonsoft.Json.JsonProperty("_legacyModelName")]
        private string _legacyModelName = null;
        #pragma warning restore CS0414

        private string disableCharacters = string.Empty;
        private string _provider = "OpenAiCompatible";

        public bool EnableMod { get; set; } = true;
        public bool Debug { get; set; } = false;

        /// <summary>
        /// 当前活动 Provider。自动对齐 LlmMap 键名（自动容错 LlmOAICompatible <-> OpenAiCompatible）。
        /// </summary>
        public string Provider
        {
            get => _provider;
            set
            {
                if (string.Equals(value, "LlmOAICompatible", StringComparison.OrdinalIgnoreCase))
                    _provider = "OpenAiCompatible";
                else if (string.Equals(value, "LlmGemini", StringComparison.OrdinalIgnoreCase))
                    _provider = "Google";
                else if (string.Equals(value, "LlmClaude", StringComparison.OrdinalIgnoreCase))
                    _provider = "Anthropic";
                else if (string.Equals(value, "LlmOpenAi", StringComparison.OrdinalIgnoreCase))
                    _provider = "OpenAI";
                else
                    _provider = value ?? "OpenAiCompatible";
            }
        }

        /// <summary>
        /// 服务商独立配置档案库（按 Provider 类名/标识索引）。
        /// 每个 Provider 拥有独立的 ApiKey、ServerAddress、ModelName、CustomBodyJson。
        /// </summary>
        public Dictionary<string, ProviderProfile> ProviderProfiles { get; set; } = new Dictionary<string, ProviderProfile>(StringComparer.OrdinalIgnoreCase);

        /// <summary>
        /// 当前活动 Provider 的 API Key（计算属性，透明代理到 ProviderProfiles）。
        /// </summary>
        public string ApiKey
        {
            get => GetActiveProfile().ApiKey;
            set => GetActiveProfile().ApiKey = value ?? string.Empty;
        }

        /// <summary>
        /// 当前活动 Provider 的服务器地址（计算属性）。
        /// </summary>
        public string ServerAddress
        {
            get => GetActiveProfile().ServerAddress;
            set => GetActiveProfile().ServerAddress = value ?? string.Empty;
        }

        /// <summary>
        /// 当前活动 Provider 的模型名称（计算属性）。
        /// </summary>
        public string ModelName
        {
            get => GetActiveProfile().ModelName;
            set => GetActiveProfile().ModelName = value ?? string.Empty;
        }

        /// <summary>
        /// 当前活动 Provider 的自定义 Body JSON（计算属性）。
        /// 仅 OpenAiCompatible 使用，其他服务商留空。
        /// </summary>
        public string CustomBodyJson
        {
            get => GetActiveProfile().CustomBodyJson;
            set => GetActiveProfile().CustomBodyJson = value ?? string.Empty;
        }

        /// <summary>
        /// 高级模型参数：Temperature（全局共享，不按 Provider 区分）。
        /// </summary>
        public float Temperature { get; set; } = 0.9f;

        /// <summary>
        /// 高级模型参数：Top_P（全局共享）。
        /// </summary>
        public float TopP { get; set; } = 0.9f;

        /// <summary>
        /// 高级模型参数：Max Tokens（全局共享）。
        /// </summary>
        public int MaxTokens { get; set; } = 1024;

        public string PromptFormat { get; set; } = "[INST] {system}\\n{prompt}[/INST]\\n{response_start}";
        public int QueryTimeout { get; set; } = 60;
        public bool ApplyTranslation { get; set; } = false;
        public int GeneralFrequency { get; set; } = 4;
        public int MarriageFrequency { get; set; } = 4;
        public int GiftFrequency { get; set; } = 4;
        public string TypedResponses { get; set; } = "With Generated";
        public SButton InitiateTypedDialogueKey { get; set; } = SButton.LeftAlt;
        public SButton QuickReplyKey { get; set; } = SButton.Enter;
        /// <summary>面对面快捷招募 NPC / 主动取消 NPC 跟随的双向热键。</summary>
        public SButton DismissFollowerKey { get; set; } = SButton.G;
        /// <summary>唤起四合一综合管理面板（NPC记忆 / 世界记忆 / 农夫档案 / 高级设置），默认定位最近对话 NPC。</summary>
        public SButton OpenHubMenuKey { get; set; } = SButton.K;
        /// <summary>唤起时间线手账面板（按日对话记录 + 分层记忆），自动定位最近聊天的 NPC。</summary>
        public SButton OpenTimelineMenuKey { get; set; } = SButton.J;
        public bool SuppressConnectionCheck { get; set; } = false;
        public bool EnableMemoryCompression { get; set; } = true;
        public int MemoryRecentCount { get; set; } = 10;

        /// <summary>
        /// "What's Been Said So Far" 块的 Prompt 渲染条数上限（取 ChatHistory 尾部窗口）。
        /// 仅影响渲染，不影响 context.ChatHistory 数据链。
        /// 路由判定 IncludeShortTermContext == false 时（话题重置 / Turn 0 首次开场 / 简单问候），
        /// 窗口自动收窄为 1（仅保留最后一行：农夫本轮输入或礼物/递物种子行）。
        /// </summary>
        public int PromptHistoryWindow { get; set; } = 6;

        public string DisableCharacters
        {
            get => disableCharacters;
            set
            {
                disableCharacters = value;
                DisabledCharactersList = value
                    .Split(new[] { ',', ' ' })
                    .Select(s => s.Trim().ToTitleCase())
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
            }
        }

        internal HashSet<string> DisabledCharactersList { get; private set; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        public bool EnablePerceptionSystem { get; set; } = true;

        public bool EnableNearbyPerception { get; set; } = true;
        public bool EnableSameMapPerception { get; set; } = true;
        public bool EnableGlobalPerception { get; set; } = true;

        public bool EnablePerceptionEat { get; set; } = true;
        public bool EnablePerceptionFish { get; set; } = true;
        public bool EnablePerceptionChop { get; set; } = true;
        public bool EnablePerceptionPlace { get; set; } = true;
        public bool EnablePerceptionTalk { get; set; } = true;
        public bool EnablePerceptionHarvest { get; set; } = true;
        public bool EnablePerceptionGift { get; set; } = true;

        public int PerceptionTalkLifetime { get; set; } = 1;
        public int PerceptionActionLifetime { get; set; } = 5;
        public int PerceptionHarvestLifetime { get; set; } = 1440;

        // Player Profile Configuration
        public bool EnablePlayerProfile { get; set; } = true;
        public string PlayerSexualOrientation { get; set; } = ""; // Single-select
        public string PlayerCustomBio { get; set; } = ""; // Max 300 chars
        public bool EnableInfiniteChat { get; set; } = false;
        public SafetyModeLevel RomanceSafetyMode { get; set; } = SafetyModeLevel.Moderate;

        /// <summary>
        /// 【已废弃】原生工具调用已全量移除。此开关仅为旧配置文件兼容保留，不再产生任何运行时效果。
        /// </summary>
        public bool UseNativeToolCalling { get; set; } = false;
        public bool EnableVanillaFirst { get; set; } = false;
        /// <summary>
        /// 开启 = 原版台词入历史库供 AI 续聊（可能话题粘滞）；关闭（推荐/默认）= 仅优先展示给玩家，不入库、不偷听广播、不注入 Prompt。
        /// </summary>
        public bool RecordVanillaDialogue { get; set; } = false;
        public bool EnableNightlyConsolidation { get; set; } = true;

        /// <summary>
        /// 静态连胜事实注入（连聊/连礼/同款礼物）：纯硬编码判定，与夜间整理（EnableNightlyConsolidation）完全无关。
        /// </summary>
        public bool EnableStreakContext { get; set; } = true;

        /// <summary>
        /// 启用配偶自由日程与外出活动系统（CompanionScheduleManager）。
        /// 默认开启 (true)。
        /// </summary>
        public bool EnableSpouseSchedule { get; set; } = true;

        /// <summary>
        /// 启用约会与出游系统（DateManager，包含约会邀请、赴约及阶段交互）。
        /// 默认关闭 (false，尚在开发中)。
        /// </summary>
        public bool EnableDateSystem { get; set; } = false;

        /// <summary>
        /// 是否严格遵循第三方模组作者的 permitAiUse 声明。
        /// 默认开启 (true)：未声明允许 AI 的第三方 NPC 将保持游戏原生对话，不接入大模型。
        /// 关闭 (false)：玩家在本地自愿决定对所有自定义 NPC 启用 AI 对话。
        /// </summary>
        public bool RespectAuthorAiConsent { get; set; } = true;

        // ── 对话系统配置（从 DialogueConfig 合并） ──
        public bool EnableAmbientBarks { get; set; } = true;
        public bool EnableA2A { get; set; } = true;
        public int LlmTimeoutSeconds { get; set; } = 30;
        public int BarkApiCooldownTicks { get; set; } = 300;
        public int BarkQueueSize { get; set; } = 3;
        public int A2AMaxParticipants { get; set; } = 4;

        /// <summary>
        /// 停留意图判定：玩家需要连续命中雷达扫描多少次（每次约1秒）才会真正触发 Bark 请求。
        /// 用于过滤"路过"场景，避免浪费 LLM 请求。
        /// </summary>
        public int BarkDwellScans { get; set; } = 2;

        /// <summary>
        /// 获取当前 Provider 的配置档案（不存在时自动初始化）。
        /// </summary>
        public ProviderProfile GetActiveProfile()
        {
            if (ProviderProfiles == null)
            {
                ProviderProfiles = new Dictionary<string, ProviderProfile>(StringComparer.OrdinalIgnoreCase);
            }

            string currentProvider = Provider;

            if (!ProviderProfiles.ContainsKey(currentProvider))
            {
                var profile = new ProviderProfile();

                // 为兼容模式预填充默认 OpenRouter 地址
                if (string.Equals(currentProvider, "OpenAiCompatible", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(currentProvider, "LlmOAICompatible", StringComparison.OrdinalIgnoreCase))
                {
                    profile.ServerAddress = "https://openrouter.ai/api/v1";
                }

                ProviderProfiles[currentProvider] = profile;
            }

            return ProviderProfiles[currentProvider];
        }

        /// <summary>
        /// 校验并修正对话相关配置值到合法范围。
        /// </summary>
        public void ValidateDialogueConfig(IMonitor monitor)
        {
            LlmTimeoutSeconds = Clamp(LlmTimeoutSeconds, 5, 120);
            BarkApiCooldownTicks = Clamp(BarkApiCooldownTicks, 30, 3600);
            BarkQueueSize = Clamp(BarkQueueSize, 1, 20);
            A2AMaxParticipants = Clamp(A2AMaxParticipants, 2, 4);
            BarkDwellScans = Clamp(BarkDwellScans, 1, 10);
            PromptHistoryWindow = Clamp(PromptHistoryWindow, 1, 20);

            // ── 高级模型参数边界校验 ──
            Temperature = Math.Clamp(Temperature, 0.0f, 2.0f);
            TopP = Math.Clamp(TopP, 0.0f, 1.0f);
            MaxTokens = Math.Clamp(MaxTokens, 100, 8192);

            // ── CustomBodyJson JSON 校验 ──
            if (!string.IsNullOrWhiteSpace(CustomBodyJson))
            {
                try
                {
                    Newtonsoft.Json.Linq.JToken.Parse(CustomBodyJson);
                }
                catch (Newtonsoft.Json.JsonReaderException)
                {
                    monitor?.Log("[ModConfig] 畸形 CustomBodyJson detected, clearing to prevent runtime errors.", LogLevel.Warn);
                    CustomBodyJson = string.Empty;
                }
            }

            monitor?.Log("[ModConfig] Dialog and advanced parameters have been verified.", LogLevel.Debug);
        }

        private static int Clamp(int value, int min, int max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }
    }
}