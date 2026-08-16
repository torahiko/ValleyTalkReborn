using System;
using System.Collections.Generic;
using StardewModdingAPI;
using StardewValley;

namespace ValleytalkReborn
{
    /// <summary>
    /// Clean & Lean PlayerProfileManager (v3.1 - Nuanced Romance Edition):
    /// - Pure positive framing to eliminate the pink elephant effect.
    /// - Standardized semantic XML prompt tagging with strict EN-Fallback dual-track localization.
    /// - De-centering character anchors for NPC personality independence.
    /// - Dynamic romance nuance detection (e.g., flattered surprise when dating against prior orientation label).
    /// - Language: Chinese (zh*) → Chinese prompt; ALL other languages → English fallback.
    /// </summary>
    internal static class PlayerProfileManager
    {
        /// <summary>
        /// 语言检测：仅当游戏语言代码以 "zh" 开头时返回 true。
        /// 所有其他语言（日语、韩语、法语、德语等小语种）均返回 false，自动回落英语。
        /// </summary>
        private static bool IsChineseLanguage =>
            LocalizedContentManager.CurrentLanguageCode.ToString()
                .StartsWith("zh", StringComparison.OrdinalIgnoreCase);

        public static string BuildProfileText(NPC targetNpc, string playerInput = "", ContextFlags flags = null)
        {
            var config = ModEntry.Config;
            if (!config.EnablePlayerProfile)
                return string.Empty;

            var lines = new List<string>();
            bool isZh = IsChineseLanguage;

            // 0. 路由评估
            flags ??= ContextRouter.Evaluate(targetNpc, playerInput, config.RomanceSafetyMode);

            // 1. 动态社交边界 (系统层约束) — 受安全模式档位控制 (纯正向引导)
            if (flags.IncludeSafetyRules)
            {
                string safetyInstruction = GetSafetyInstruction(targetNpc, config.RomanceSafetyMode, isZh);
                if (!string.IsNullOrEmpty(safetyInstruction))
                {
                    lines.Add(safetyInstruction);
                    lines.Add("");
                }
            }

            // 2. 角色独立性锚点 — 与安全模式无关，始终生效 (纯正向引导，杜绝献媚)
            lines.Add(GetCharacterAnchor(targetNpc, isZh));
            lines.Add("");

            // 3. 玩家自定义规则与基础身份
            lines.Add(isZh ? "### 玩家规则与交互设定" : "### PLAYER RULES & CONTEXT");

            // 显式弱化与去中心化引导语
            lines.Add(isZh
                ? "以下为玩家的背景偏好设定，仅在相关话题自然出现时参考，保持对话从容自如："
                : "The following are player background preferences. Reference only when naturally relevant; maintain organic dialogue flow:");

            string saveBio = GetSaveCustomBio();
            if (!string.IsNullOrWhiteSpace(saveBio))
            {
                lines.Add($"<custom_rules>\n{saveBio.Trim()}\n</custom_rules>");
            }

            // farmer_context：内联显式降权
            string genderStr = Game1.player.IsMale
                ? (isZh ? "男性" : "Male")
                : (isZh ? "女性" : "Female");

            lines.Add(isZh
                ? $"<farmer_context>玩家为{genderStr}，倾向{config.PlayerSexualOrientation}。此信息仅作为背景参考，无需刻意聚焦或过度回应。</farmer_context>"
                : $"<farmer_context>Player is {genderStr}, orientation {config.PlayerSexualOrientation}. This is background context only; treat as subtle background knowledge rather than a focal point.</farmer_context>");

            // 4. [新增] 恋爱反差/惊喜微反应 (如果玩家建立了跨越初始取向偏好的亲密关系)
            string romanceNuance = GetRomanceNuance(targetNpc, config.PlayerSexualOrientation, isZh);
            if (!string.IsNullOrEmpty(romanceNuance))
            {
                lines.Add(romanceNuance);
            }

            return string.Join("\n", lines);
        }

        /// <summary>
        /// 角色独立性锚点：纯正向聚焦，确保 NPC 拥有独立生活与人格，消除过度献媚。
        /// </summary>
        private static string GetCharacterAnchor(NPC targetNpc, bool isZh)
        {
            return isZh
                ? "<character_anchor>NPC拥有独立的生活节奏与内心世界。请将对话重心自然落在小镇日常、个人经历及当下情境上。保持符合自身性格的表达距离，自由分享个人见闻与思绪。</character_anchor>"
                : "<character_anchor>The NPC maintains an independent daily life and personal perspective. Focus dialogue naturally on town events, personal experiences, and the immediate surroundings. Keep a casual, authentic conversational boundary and share thoughts freely.</character_anchor>";
        }

        /// <summary>
        /// 恋爱反差细腻心理：当玩家表白/交往的对象与初始偏好冲突时，提供带有惊喜与好奇的细腻情感引导。
        /// </summary>
        private static string GetRomanceNuance(NPC targetNpc, string orientationConfig, bool isZh)
        {
            if (targetNpc == null || string.IsNullOrWhiteSpace(orientationConfig))
                return string.Empty;

            // 仅在双方处于交往、订婚或结婚状态时触发该心理暗示
            if (!Game1.player.friendshipData.TryGetValue(targetNpc.Name, out var friendship))
                return string.Empty;

            if (!friendship.IsDating() && !friendship.IsEngaged() && !friendship.IsMarried())
                return string.Empty;

            // 修复：使用 Gender.Male
            bool isNpcMale = targetNpc.Gender == Gender.Male;
            bool isSameGender = (Game1.player.IsMale == isNpcMale);
            string raw = orientationConfig.Trim().ToLowerInvariant();

            bool isHomosexualPreference = raw.Contains("gay") || raw.Contains("lesbian") || raw.Contains("homo") || raw.Contains("同性");
            bool isHeterosexualPreference = raw.Contains("straight") || raw.Contains("hetero") || raw.Contains("异性");

            bool isMismatch = false;

            // 玩家设定为同性偏好，但正在与异性 NPC 恋爱
            if (isHomosexualPreference && !isSameGender)
            {
                isMismatch = true;
            }
            // 玩家设定为异性偏好，但正在与同性 NPC 恋爱
            else if (isHeterosexualPreference && isSameGender)
            {
                isMismatch = true;
            }

            if (isMismatch)
            {
                return isZh
                    ? "<romance_nuance>NPC潜意识知晓玩家最初的取向偏好与当下选择有所不同。在涉及深层亲密情感时，可依NPC自身性格自然带有一丝受宠若惊、隐秘的惊喜或温柔的好奇（例如将自己视为特别的例外），无需过度质疑，重在展现真诚的心意流动。</romance_nuance>"
                    : "<romance_nuance>The NPC is subtly aware that dating them contrasts with the player's previously stated orientation. When touching upon romance, naturally allow for a touch of flattered surprise, tender curiosity (seeing themselves as a cherished exception), and authentic warmth suited to their personality.</romance_nuance>";
            }

            return string.Empty;
        }
        private static string GetSaveCustomBio()
        {
            if (string.IsNullOrWhiteSpace(Constants.SaveFolderName) || ModEntry.SHelper == null)
                return string.Empty;

            string path = $"data/{Constants.SaveFolderName}/PlayerProfile.json";
            try
            {
                var saveData = ModEntry.SHelper.Data.ReadJsonFile<Dictionary<string, string>>(path);
                if (saveData != null && saveData.TryGetValue("PlayerCustomBio", out string bio))
                {
                    return bio;
                }
            }
            catch
            {
                // 静默忽略读取异常
            }
            return string.Empty;
        }

        /// <summary>
        /// 安全指令：纯正向目标框架 (Positive Guidance)。
        /// </summary>
        private static string GetSafetyInstruction(NPC npc, SafetyModeLevel level, bool isZh)
        {
            if (level == SafetyModeLevel.Off || npc == null)
                return string.Empty;

            int hearts = 0;
            bool isRomanceActive = false;

            if (Game1.player.friendshipData.TryGetValue(npc.Name, out var friendship))
            {
                hearts = friendship.Points / 250;
                isRomanceActive = friendship.IsDating() || friendship.IsEngaged() || friendship.IsMarried();
            }

            return level switch
            {
                // Strict: 邻里友善 + 专注自身生活
                SafetyModeLevel.Strict => (!isRomanceActive)
                    ? (isZh
                        ? "<interaction_boundary>请保持温和尊重的邻里社交风格，专注于日常关怀、小镇生活与自身近况交流。保持从容自洽的交往距离。</interaction_boundary>"
                        : "<interaction_boundary>Maintain a warm, respectful, and neighborly tone, focusing on community life, daily greetings, and your own activities. Keep a grounded, self-contained interpersonal boundary.</interaction_boundary>")
                    : string.Empty,

                // Moderate: 熟人日常 + 轻松自如
                SafetyModeLevel.Moderate => (!isRomanceActive && hearts <= 7)
                    ? (isZh
                        ? "<interaction_boundary>保持得体舒适的社交距离，以惬意自然的方式进行熟人间的日常对话，自然交流彼此生活与眼前事物。</interaction_boundary>"
                        : "<interaction_boundary>Adopt a casual, friendly, and polite tone suited for comfortable acquaintance dialogue, naturally sharing thoughts on everyday life and current surroundings.</interaction_boundary>")
                    : string.Empty,

                // Loose: 基础礼貌
                SafetyModeLevel.Loose => (hearts <= 4)
                    ? (isZh
                        ? "<interaction_boundary>保持随和有礼的日常交流风格。</interaction_boundary>"
                        : "<interaction_boundary>Keep interactions polite, casual, and pleasant.</interaction_boundary>")
                    : string.Empty,

                _ => string.Empty
            };
        }
    }
}