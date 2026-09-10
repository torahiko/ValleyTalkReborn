using System;
using System.Collections.Generic;
using System.Linq;
using StardewModdingAPI;

namespace ValleytalkReborn
{
    public enum SafetyModeLevel { Off, Loose, Moderate, Strict }

    public class ModConfig
    {
        private string disableCharacters = string.Empty;

        public bool EnableMod { get; set; } = true;
        public bool Debug { get; set; } = false;
        public string Provider { get; set; } = "Mistral";
        public string ModelName { get; set; } = "";
        public string ServerAddress { get; set; } = "https://openrouter.ai/api";
        public string PromptFormat { get; set; } = "[INST] {system}\n{prompt}[/INST]\n{response_start}";
        public int QueryTimeout { get; set; } = 60;
        public string ApiKey { get; set; } = string.Empty;
        public bool ApplyTranslation { get; set; } = false;
        public int GeneralFrequency { get; set; } = 4;
        public int MarriageFrequency { get; set; } = 4;
        public int GiftFrequency { get; set; } = 4;
        public string TypedResponses { get; set; } = "With Generated";
        public SButton InitiateTypedDialogueKey { get; set; } = SButton.LeftAlt;
        public SButton QuickReplyKey { get; set; } = SButton.Enter;
        /// <summary>主动取消 NPC 跟随（普通跟随 / 约会跟随）的热键。</summary>
        public SButton DismissFollowerKey { get; set; } = SButton.G;
        public bool SuppressConnectionCheck { get; set; } = false;
        public bool EnableMemoryCompression { get; set; } = true;
        public int MemoryRecentCount { get; set; } = 10;

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
        /// 启用原生 Function Calling（云端 LLM）。
        /// 设为 false 时回退至 Legacy [ACTION:TAG] 文本解析模式（适合本地 Llama 模型）。
        /// </summary>
        public bool UseNativeToolCalling { get; set; } = true;
        public bool EnableVanillaFirst { get; set; } = true;
        public bool EnableNightlyConsolidation { get; set; } = true;

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
        /// 校验并修正对话相关配置值到合法范围。
        /// </summary>
        public void ValidateDialogueConfig(IMonitor monitor)
        {
            LlmTimeoutSeconds = Clamp(LlmTimeoutSeconds, 5, 120);
            BarkApiCooldownTicks = Clamp(BarkApiCooldownTicks, 30, 3600);
            BarkQueueSize = Clamp(BarkQueueSize, 1, 20);
            A2AMaxParticipants = Clamp(A2AMaxParticipants, 2, 4);
            BarkDwellScans = Clamp(BarkDwellScans, 1, 10);

            monitor?.Log("[ModConfig] 对话配置已校验。", LogLevel.Debug);
        }

        private static int Clamp(int value, int min, int max)
        {
            if (value < min) return min;
            if (value > max) return max;
            return value;
        }
    }
}