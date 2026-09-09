using System;
using System.Collections.Generic;
using StardewModdingAPI;
using StardewValley;

namespace ValleytalkReborn
{
    /// <summary>
    /// Clean &amp; LEAN PlayerProfileManager (v3.3 - Colorless Prompt Edition):
    /// - All system-level instructions are tone-neutral structural constraints.
    /// - Tone, attitude, and interpersonal distance are 100% delegated to Hearts + Character Card.
    /// - Safety mode acts purely as content-scope boundary (no tone directives).
    /// - Standardized semantic XML prompt tagging with strict EN-Fallback dual-track localization.
    /// - Language: Chinese (zh*) → Chinese prompt; ALL other languages → English fallback.
    /// </summary>
    internal static class PlayerProfileManager
    {
        // ──────────────────────────────────────────────────────────────
        // Bio cache
        // ──────────────────────────────────────────────────────────────
        private const string SaveDataKey = "valleytalk.player-profile";

        private static string _cachedBio = string.Empty;
        private static bool _bioCacheLoaded;

        // ──────────────────────────────────────────────────────────────
        // Orientation model
        // ──────────────────────────────────────────────────────────────
        private enum SexualOrientationKind
        {
            Unknown,
            Heterosexual,
            Homosexual,
            Bisexual,
            Asexual
        }

        public static void InvalidateBioCache()
        {
            _cachedBio = string.Empty;
            _bioCacheLoaded = false;
        }

        /// <summary>
        /// 语言检测：仅当游戏语言代码以 "zh" 开头时返回 true。
        /// 所有其他语言均返回 false，自动回落英语。
        /// </summary>
        private static bool IsChineseLanguage()
        {
            return LocalizedContentManager.CurrentLanguageCode.ToString()
                .StartsWith("zh", StringComparison.OrdinalIgnoreCase);
        }

        public static string BuildProfileText(NPC targetNpc, string playerInput = "", ContextFlags flags = null)
        {
            var config = ModEntry.Config;
            if (!config.EnablePlayerProfile)
                return string.Empty;

            var lines = new List<string>();
            bool isZh = IsChineseLanguage();

            // 0. 路由评估
            flags ??= ContextRouter.Evaluate(targetNpc, playerInput, config.RomanceSafetyMode);

            // 1. 动态社交边界 (内容范围约束，不干预语气)
            if (flags.IncludeSafetyRules)
            {
                string safetyInstruction = GetSafetyInstruction(targetNpc, config.RomanceSafetyMode, isZh);
                if (!string.IsNullOrEmpty(safetyInstruction))
                {
                    lines.Add(safetyInstruction);
                    lines.Add("");
                }
            }

            // 2. 角色独立性锚点 — 始终生效，防止以玩家为中心
            lines.Add(GetCharacterAnchor(targetNpc, isZh));
            lines.Add("");

            // 3. 玩家自定义规则与基础身份
            lines.Add(isZh
                ? "### 玩家规则与交互设定"
                : "### PLAYER RULES & CONTEXT");

            lines.Add(isZh
                ? "以下为玩家的背景偏好设定，仅在话题相关时参考："
                : "The following are player background preferences; reference only when topically relevant:");

            string saveBio = GetSaveCustomBio();
            if (!string.IsNullOrWhiteSpace(saveBio))
            {
                string safeBio = SanitizePromptText(saveBio).Trim();
                lines.Add($"<custom_rules>\n{safeBio}\n</custom_rules>");
            }

            // farmer_context：纯背景声明
            string genderStr = Game1.player.IsMale
                ? (isZh ? "男性" : "Male")
                : (isZh ? "女性" : "Female");

            string orientationRaw = config.PlayerSexualOrientation?.Trim() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(orientationRaw))
            {
                lines.Add(isZh
                    ? $"<farmer_context>玩家为{genderStr}。仅作为背景信息。</farmer_context>"
                    : $"<farmer_context>Player is {genderStr}. Background information only.</farmer_context>");
            }
            else
            {
                string orientationText = GetLocalizedOrientation(orientationRaw, isZh);
                lines.Add(isZh
                    ? $"<farmer_context>玩家为{genderStr}，性取向为{orientationText}。仅作为背景信息。</farmer_context>"
                    : $"<farmer_context>Player is {genderStr}, orientation: {orientationText}. Background information only.</farmer_context>");
            }

            // 4. 恋爱反差背景
            string romanceNuance = GetRomanceNuance(targetNpc, orientationRaw, isZh);
            if (!string.IsNullOrEmpty(romanceNuance))
            {
                lines.Add(romanceNuance);
            }

            return string.Join("\n", lines);
        }

        /// <summary>
        /// 角色独立性锚点：结构约束，不注入任何语气/态度指令。
        /// </summary>
        private static string GetCharacterAnchor(NPC targetNpc, bool isZh)
        {
            return isZh
                ? "<character_anchor>以角色卡设定与当前好感度为表达基准。角色拥有独立的生活、目标与关注点，对话围绕角色自身的视角与当下情境展开。</character_anchor>"
                : "<character_anchor>Use the character card and current friendship level as the expressive baseline. The NPC has an independent life, goals, and focus; dialogue unfolds from the character's own perspective and immediate context.</character_anchor>";
            
        }

        /// <summary>
        /// 恋爱反差背景：仅陈述事实，不预设情绪反应。
        /// </summary>
        private static string GetRomanceNuance(NPC targetNpc, string orientationConfig, bool isZh)
        {
            if (targetNpc == null || string.IsNullOrWhiteSpace(orientationConfig))
                return string.Empty;

            if (!Game1.player.friendshipData.TryGetValue(targetNpc.Name, out var friendship))
                return string.Empty;

            if (!friendship.IsDating() && !friendship.IsEngaged() && !friendship.IsMarried())
                return string.Empty;

            bool isNpcMale = targetNpc.Gender == Gender.Male;
            bool isSameGender = Game1.player.IsMale == isNpcMale;

            var orientation = ParseOrientation(orientationConfig);
            bool isMismatch = false;

            if (orientation == SexualOrientationKind.Heterosexual && isSameGender)
            {
                isMismatch = true;
            }
            else if (orientation == SexualOrientationKind.Homosexual && !isSameGender)
            {
                isMismatch = true;
            }

            if (isMismatch)
            {
                return isZh
                    ? "<romance_nuance>背景设定：玩家的取向偏好与当前交往对象存在差异。按角色卡自身性格处理该背景，不预设特定情绪反应。</romance_nuance>"
                    : "<romance_nuance>Background: the player's stated orientation differs from their current partner. Handle this per the character card; no specific emotional reaction is prescribed.</romance_nuance>";
            }

            return string.Empty;
        }

        /// <summary>
        /// 安全指令：纯内容范围约束，不干预语气与态度。
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
                // Strict: 不生成浪漫/亲密内容
                SafetyModeLevel.Strict => (!isRomanceActive)
                    ? (isZh
                        ? "<interaction_boundary>双方未进入恋爱阶段。互动内容限于当前关系阶段；若遇越界言行，按角色性格予以拒绝或冷淡处理。</interaction_boundary>"
                        : "<interaction_boundary>Do not generate romantic, flirtatious, or intimate content. Keep interaction within the current relationship stage.</interaction_boundary>")
                    : string.Empty,

                // Moderate: 不主动推进浪漫关系
                SafetyModeLevel.Moderate => (!isRomanceActive && hearts <= 7)
                    ? (isZh
                        ? "<interaction_boundary>不主动推进浪漫关系。互动内容符合当前好感度阶段。</interaction_boundary>"
                        : "<interaction_boundary>Do not escalate the romantic relationship. Keep interaction consistent with the current friendship level.</interaction_boundary>")
                    : string.Empty,

                // Loose: 仅对齐当前关系阶段
                SafetyModeLevel.Loose => (!isRomanceActive && hearts <= 4)
                    ? (isZh
                        ? "<interaction_boundary>互动内容符合当前好感度阶段。</interaction_boundary>"
                        : "<interaction_boundary>Keep interaction consistent with the current friendship level.</interaction_boundary>")
                    : string.Empty,

                _ => string.Empty
            };
        }

        /// <summary>
        /// 读取当前存档专属的 PlayerCustomBio（带轻量缓存）。
        /// </summary>
        private static string GetSaveCustomBio()
        {
            if (!Context.IsWorldReady || ModEntry.SHelper == null)
                return string.Empty;

            if (_bioCacheLoaded)
                return _cachedBio;

            try
            {
                // 1. 从 SaveData 读取
                var saveData = ModEntry.SHelper.Data.ReadSaveData<Dictionary<string, string>>(SaveDataKey);

                if (saveData != null && saveData.TryGetValue("PlayerCustomBio", out string bio))
                {
                    _cachedBio = bio ?? string.Empty;
                }
                else
                {
                    // 2. 兼容迁移旧文件
                    string oldPath = $"data/{Constants.SaveFolderName}/PlayerProfile.json";
                    var oldData = ModEntry.SHelper.Data.ReadJsonFile<Dictionary<string, string>>(oldPath);
                    if (oldData != null && oldData.TryGetValue("PlayerCustomBio", out string oldBio))
                    {
                        _cachedBio = oldBio ?? string.Empty;
                        // 迁移写入 SaveData
                        ModEntry.SHelper.Data.WriteSaveData(SaveDataKey, oldData);
                    }
                    else
                    {
                        _cachedBio = string.Empty;
                    }
                }
            }
            catch
            {
                _cachedBio = string.Empty;
            }

            _bioCacheLoaded = true;
            return _cachedBio;
        }

        /// <summary>
        /// 当玩家在 UI 里修改了个人设定时调用
        /// </summary>
        public static void SaveCustomBio(string newBio)
        {
            if (!Context.IsWorldReady || ModEntry.SHelper == null) return;

            var data = new Dictionary<string, string>
            {
                ["PlayerCustomBio"] = newBio ?? string.Empty
            };

            ModEntry.SHelper.Data.WriteSaveData(SaveDataKey, data);
            _cachedBio = newBio ?? string.Empty;
            _bioCacheLoaded = true;
        }

        /// <summary>
        /// 将配置中的性取向键值转换为本地化文本。
        /// </summary>
        private static string GetLocalizedOrientation(string raw, bool isZh)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return isZh ? "未设定" : "unspecified";

            var kind = ParseOrientation(raw);
            return kind switch
            {
                SexualOrientationKind.Heterosexual => isZh ? "异性恋" : "heterosexual",
                SexualOrientationKind.Homosexual   => isZh ? "同性恋" : "homosexual",
                SexualOrientationKind.Bisexual     => isZh ? "双性恋" : "bisexual",
                SexualOrientationKind.Asexual      => isZh ? "无性恋" : "asexual",
                _                                  => raw.Trim()
            };
        }

        /// <summary>
        /// 确定性解析性取向配置。
        /// </summary>
        private static SexualOrientationKind ParseOrientation(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return SexualOrientationKind.Unknown;

            string normalized = raw.Trim().ToLowerInvariant();
            switch (normalized)
            {
                case "heterosexual":
                case "straight":
                case "异性":
                case "异性恋":
                    return SexualOrientationKind.Heterosexual;

                case "homosexual":
                case "gay":
                case "lesbian":
                case "同性":
                case "同性恋":
                    return SexualOrientationKind.Homosexual;

                case "bisexual":
                case "bi":
                case "双性":
                case "双性恋":
                    return SexualOrientationKind.Bisexual;

                case "asexual":
                case "ace":
                case "无性":
                case "无性恋":
                    return SexualOrientationKind.Asexual;
            }

            // 兜底兼容
            if (normalized.Contains("异性"))
                return SexualOrientationKind.Heterosexual;
            if (normalized.Contains("同性"))
                return SexualOrientationKind.Homosexual;
            if (normalized.Contains("双性"))
                return SexualOrientationKind.Bisexual;
            if (normalized.Contains("无性"))
                return SexualOrientationKind.Asexual;

            return SexualOrientationKind.Unknown;
        }

        /// <summary>
        /// 轻量清洗玩家自定义文本，避免尖括号干扰 Prompt 结构。
        /// </summary>
        private static string SanitizePromptText(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return string.Empty;

            return text
                .Replace('<', '＜')
                .Replace('>', '＞');
        }
    }
}