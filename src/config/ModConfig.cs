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
    }
}