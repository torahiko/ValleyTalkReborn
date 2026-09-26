using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using StardewModdingAPI;
using StardewValley;

namespace ValleytalkReborn
{
    public static class I18n
    {
        private static Dictionary<string, string> _english = Load("default");
        private static Dictionary<string, string> _locale = _english;
        private static string _localeName = string.Empty;

        public static bool IsChinese =>
            LocalizedContentManager.CurrentLanguageCode == LocalizedContentManager.LanguageCode.zh;

        private static void EnsureLocale()
        {
            var code = LocalizedContentManager.CurrentLanguageCode;
            string name = "default";

            switch (code)
            {
                case LocalizedContentManager.LanguageCode.zh:
                    name = "zh";
                    break;
                case LocalizedContentManager.LanguageCode.ja:
                    name = "ja";
                    break;
                case LocalizedContentManager.LanguageCode.ru:
                    name = "ru";
                    break;
                case LocalizedContentManager.LanguageCode.de:
                    name = "de";
                    break;
                case LocalizedContentManager.LanguageCode.es:
                    name = "es";
                    break;
                case LocalizedContentManager.LanguageCode.fr:
                    name = "fr";
                    break;
                case LocalizedContentManager.LanguageCode.it:
                    name = "it";
                    break;
                case LocalizedContentManager.LanguageCode.ko:
                    name = "ko";
                    break;
                case LocalizedContentManager.LanguageCode.pt:
                    name = "pt";
                    break;
                case LocalizedContentManager.LanguageCode.tr:
                    name = "tr";
                    break;
                case LocalizedContentManager.LanguageCode.hu:
                    name = "hu";
                    break;
                case LocalizedContentManager.LanguageCode.en:
                default:
                    name = "default";
                    break;
            }

            if (_localeName == name && _locale != null) return;

            _localeName = name;
            _locale = name == "default" ? _english : Load(name);
        }

        private static string Lookup(string key)
        {
            if (string.IsNullOrEmpty(key)) return string.Empty;

            EnsureLocale();

            if (_locale != null && _locale.TryGetValue(key, out var val) && !string.IsNullOrEmpty(val))
                return val;

            if (_english != null && _english.TryGetValue(key, out val) && !string.IsNullOrEmpty(val))
                return val;

            try
            {
                if (ModEntry.SHelper?.Translation != null)
                {
                    var translation = ModEntry.SHelper.Translation.Get(key);
                    if (translation.HasValue()) return translation.ToString();
                }
            }
            catch (Exception ex)
            {
                ModEntry.SMonitor?.Log($"[I18n] Translation lookup failed for key '{key}': {ex.GetType().Name}: {ex.Message}", LogLevel.Warn);
            }

            return key;
        }

        private static Dictionary<string, string> Load(string locale)
        {
            string baseDir = ModEntry.SHelper?.DirectoryPath ?? AppDomain.CurrentDomain.BaseDirectory;
            var candidates = new List<string>();

            void AddFileCandidates(string folderPath)
            {
                if (string.IsNullOrWhiteSpace(folderPath)) return;
                try
                {
                    string fullFolder = Path.IsPathRooted(folderPath)
                        ? folderPath
                        : Path.GetFullPath(Path.Combine(baseDir, folderPath));

                    if (locale == "default" || locale == "en")
                    {
                        candidates.Add(Path.Combine(fullFolder, "default.json"));
                    }
                    else if (locale == "zh")
                    {
                        candidates.Add(Path.Combine(fullFolder, "zh.json"));
                        candidates.Add(Path.Combine(fullFolder, "zh-CN.json"));
                    }
                    else
                    {
                        candidates.Add(Path.Combine(fullFolder, $"{locale}.json"));
                    }
                }
                catch (Exception ex)
                {
                    ModEntry.SMonitor?.Log($"[I18n] Skipped i18n candidate folder '{folderPath}': {ex.GetType().Name}: {ex.Message}", LogLevel.Warn);
                }
            }

            AddFileCandidates("../ContentPack/i18n");
            AddFileCandidates("../[CP] ValleyTalkReborn Base/i18n");
            AddFileCandidates("ContentPack/i18n");
            AddFileCandidates("[CP] ValleyTalkReborn Base/i18n");
            AddFileCandidates("i18n");

            foreach (var filePath in candidates)
            {
                try
                {
                    if (File.Exists(filePath))
                    {
                        string json = File.ReadAllText(filePath);
                        if (!string.IsNullOrWhiteSpace(json))
                        {
                            var result = JsonConvert.DeserializeObject<Dictionary<string, string>>(json);
                            if (result != null && result.Count > 0)
                            {
                                return result;
                            }
                        }
                    }
                }
                catch (Exception ex)
                {
                    ModEntry.SMonitor?.Log($"[I18n] Failed to load language file '{filePath}': {ex.GetType().Name}: {ex.Message}", LogLevel.Warn);
                }
            }

            return _english ?? new Dictionary<string, string>();
        }

        private static string FormatNpc(string text, string npcName)
        {
            string val = npcName ?? string.Empty;
            return text
                .Replace("{{npcName}}", val)
                .Replace("{{npcname}}", val)
                .Replace("{{Name}}", val)
                .Replace("{{name}}", val)
                .Replace("{{0}}", val);
        }

        public static string ResponseStart() => Lookup("responseStart");
        public static string Get(string key) => Lookup(key);

        // =========================================================================
        // 1. TIMELINE CHRONICLE MENU (Isolated Scope)
        // =========================================================================
        public static class Timeline
        {
            public static string NpcDropdownPrefix() => Lookup("Timeline.NpcDropdownPrefix");
            public static string SpeakerFarmer() => Lookup("Timeline.Speaker.Farmer");
            public static string SpeakerScene() => Lookup("Timeline.Speaker.Scene");
            public static string Title(string npcName) => FormatNpc(Lookup("Timeline.Title"), npcName);

            public static string TodayDate(string date) =>
                Lookup("Timeline.TodayDate").Replace("{{date}}", date ?? string.Empty);

            public static string EmptyChats() => Lookup("Timeline.EmptyChats");
            public static string EmptyTier() => Lookup("Timeline.EmptyTier");

            public static string CooldownHud(int remaining) =>
                Lookup("Timeline.CooldownHud").Replace("{{remaining}}", remaining.ToString());

            public static string CondenseMinCount(int min) =>
                Lookup("Timeline.CondenseMinCount").Replace("{{min}}", min.ToString());

            public static string TabChats() => Lookup("Timeline.TabChats");
            public static string TabImpressions() => Lookup("Timeline.TabImpressions");
            public static string TabWeekly() => Lookup("Timeline.TabWeekly");
            public static string TabChronicle() => Lookup("Timeline.TabChronicle");

            public static string DistillThisPage() => Lookup("Timeline.DistillThisPage");

            // ── 与 Memory.* 解耦的时间线专属文案（此前与 ScrollableMemoryMenu/IntegratedHubMenu 共用）──
            public static string CloseButton() => Lookup("Timeline.CloseButton");

            public static string DeleteConfirmTitle() => Lookup("Timeline.DeleteConfirmTitle");
            public static string DeleteConfirmSubtitle() => Lookup("Timeline.DeleteConfirmSubtitle");

            public static string DistillLlmDisabled() => Lookup("Timeline.DistillLlmDisabled");

            public static string DistillNoHistory(string npcName) => Lookup("Timeline.DistillNoHistory")
                .Replace("{{npcName}}", npcName ?? string.Empty);

            public static string EditButtonHover() => Lookup("Timeline.EditButtonHover");
            public static string DeleteButtonHover() => Lookup("Timeline.DeleteButtonHover");

            public static string ArchiveButton(int count, int max)
                => Lookup("Timeline.ArchiveButton").Replace("{{count}}", count.ToString())
                    .Replace("{{max}}", max.ToString());

            public static string ArchiveTitle(string npcName) => FormatNpc(Lookup("Timeline.ArchiveTitle"), npcName);
            public static string ArchiveEmpty() => Lookup("Timeline.ArchiveEmpty");

            public static string ArchiveRuleHint(int max) =>
                Lookup("Timeline.ArchiveRuleHint").Replace("{{max}}", max.ToString());

            public static string ConsolidateToWeekly(int count) =>
                Lookup("Timeline.ConsolidateToWeekly").Replace("{{count}}", count.ToString());

            public static string ElevateToChronicle(int count) =>
                Lookup("Timeline.ElevateToChronicle").Replace("{{count}}", count.ToString());

            public static string PrevDay() => Lookup("Timeline.PrevDay");
            public static string NextDay() => Lookup("Timeline.NextDay");
        }

        // =========================================================================
        // 2. TIMELINE DISTILL CARD MENU (Isolated Scope)
        // =========================================================================
        public static class TimelineDistill
        {
            public static string TitleWeekly(string npcName) =>
                FormatNpc(Lookup("TimelineDistill.TitleWeekly"), npcName);

            public static string TitleChronicle(string npcName) =>
                FormatNpc(Lookup("TimelineDistill.TitleChronicle"), npcName);

            public static string TitleDaily(string npcName) => FormatNpc(Lookup("TimelineDistill.TitleDaily"), npcName);

            public static string Capacity(int current, int max)
                => Lookup("TimelineDistill.Capacity").Replace("{{current}}", current.ToString())
                    .Replace("{{max}}", max.ToString());

            public static string Loading() => Lookup("TimelineDistill.Loading");

            public static string SourceHeader(int count) =>
                Lookup("TimelineDistill.SourceHeader").Replace("{{count}}", count.ToString());

            public static string DraftTag(int index) =>
                Lookup("TimelineDistill.DraftTag").Replace("{{index}}", index.ToString());

            public static string CollectButton() => Lookup("TimelineDistill.CollectButton");
            public static string EditTooltip() => Lookup("TimelineDistill.EditTooltip");
            public static string CollectedStamp() => Lookup("TimelineDistill.CollectedStamp");
            public static string CollectSuccess() => Lookup("TimelineDistill.CollectSuccess");
        }

        // =========================================================================
        // 3. INTEGRATED HUB SHELL (Isolated Scope)
        // =========================================================================
        public static class Hub
        {
            public static string Title() => Lookup("Hub.Title");
            public static string TabRules() => Lookup("Hub.TabRules");
            public static string TabProfile() => Lookup("Hub.TabProfile");
            public static string TabAdvanced() => Lookup("Hub.TabAdvanced");
            public static string TabWorldSettings() => Lookup("Hub.TabWorldSettings");
            public static string ScopeAll() => Lookup("Hub.ScopeAll");
            public static string ScopeGlobal() => Lookup("Hub.ScopeGlobal");
        }

        // =========================================================================
        // 4. FARMER PROFILE DOMAIN (Shared between Hub Tab2 & PlayerProfileCustomMenu)
        // =========================================================================
        public static class Profile
        {
            public static string EnableProfile() => Lookup("Profile.EnableProfile");
            public static string OrientationLabel() => Lookup("Profile.OrientationLabel");
            public static string OrientationNone() => Lookup("Profile.Orientation.None");
            public static string OrientationHeterosexual() => Lookup("Profile.Orientation.Heterosexual");
            public static string OrientationHomosexual() => Lookup("Profile.Orientation.Homosexual");
            public static string OrientationBisexual() => Lookup("Profile.Orientation.Bisexual");
            public static string OrientationAsexual() => Lookup("Profile.Orientation.Asexual");
            public static string RomanceSafetyLabel() => Lookup("Profile.RomanceSafetyLabel");
            public static string SafetyOff() => Lookup("Profile.Safety.Off");
            public static string SafetyLoose() => Lookup("Profile.Safety.Loose");
            public static string SafetyModerate() => Lookup("Profile.Safety.Moderate");
            public static string SafetyStrict() => Lookup("Profile.Safety.Strict");
            public static string SafetyDesc() => Lookup("Profile.Safety.Desc");
            public static string BioLabel() => Lookup("Profile.BioLabel");
            public static string BioPlaceholder() => Lookup("Profile.BioPlaceholder");
            public static string SaveButton() => Lookup("Profile.SaveButton");
            public static string SavedHud() => Lookup("Profile.SavedHud");

            // ── NPC 宫格文案（FEAT-PROFILE-I18N-001）──
            /// <summary>NPC 宫格卡片右上角徽章文本（标识有自定义人设）。</summary>
            public static string NpcCustomBadge() => Lookup("Profile.NpcCustomBadge");

            /// <summary>NPC 宫格卡片重置按钮 tooltip。含 {{npc}} 插值（NPC 显示名称）。</summary>
            public static string NpcResetTooltip(string npcDisplayName) =>
                Lookup("Profile.NpcResetTooltip").Replace("{{npc}}", npcDisplayName ?? string.Empty);

            /// <summary>NPC 宫格分页信息。含 {{current}} / {{total}} / {{count}} 三个插值。</summary>
            public static string NpcPaginationInfo(int currentPage, int totalPages, int totalCount) =>
                Lookup("Profile.NpcPaginationInfo")
                    .Replace("{{current}}", currentPage.ToString())
                    .Replace("{{total}}", totalPages.ToString())
                    .Replace("{{count}}", totalCount.ToString());

            // Backward compatibility (used by DialogueTextInputMenu.cs)
        }

        // =========================================================================
        // 5. ADVANCED SETTINGS DOMAIN (Shared between Hub Tab3 & AdvancedSettingsMenu)
        // =========================================================================
        public static class AdvancedSettings
        {
            public static string InfiniteChat() => Lookup("AdvancedSettings.InfiniteChat");
            public static string InfiniteChatTooltip() => Lookup("AdvancedSettings.InfiniteChatTooltip");
            public static string VanillaFirst() => Lookup("AdvancedSettings.VanillaFirst");
            public static string VanillaFirstTooltip() => Lookup("AdvancedSettings.VanillaFirstTooltip");
            public static string RecordVanillaDialogue() => Lookup("AdvancedSettings.RecordVanillaDialogue");

            public static string RecordVanillaDialogueTooltip() =>
                Lookup("AdvancedSettings.RecordVanillaDialogueTooltip");

            public static string RecordEventDialogue() => Lookup("AdvancedSettings.RecordEventDialogue");
            public static string RecordEventDialogueTooltip() => Lookup("AdvancedSettings.RecordEventDialogueTooltip");
            public static string Disclaimer() => Lookup("AdvancedSettings.Disclaimer");

            // Backward compatibility (used by DialogueTextInputMenu.cs)
        }

        // =========================================================================
        // 6b. WORLD SETTINGS DOMAIN (Hub Tab4 sub-pages, T4/T5/T6c/T7)
        // =========================================================================
        public static class WorldSettings
        {
            public static string DateAmbience() => Lookup("WorldSettings.DateAmbience");
            public static string LocationFestival() => Lookup("WorldSettings.LocationFestival");
            public static string PoiTuning() => Lookup("WorldSettings.PoiTuning");

            // T4 约会氛围工坊子页

            // T6c 地点与节日编撰子页

            // T7 兴趣点调谐子页

            // Sub-page descriptions
            public static string DateAmbienceDesc() => Lookup("WorldSettings.DateAmbienceDesc");
            public static string LocationFestivalDesc() => Lookup("WorldSettings.LocationFestivalDesc");
            public static string PoiTuningDesc() => Lookup("WorldSettings.PoiTuningDesc");

            // Navigation title
            public static string NavTitle() => Lookup("WorldSettings.NavTitle");

            // Bottom buttons
            public static string SaveButton() => Lookup("WorldSettings.SaveButton");
            public static string ResetButton() => Lookup("WorldSettings.ResetButton");

            // HUD messages
            public static string SaveHudSuccess(int count) =>
                Lookup("WorldSettings.SaveHudSuccess").Replace("{{count}}", count.ToString());
            public static string SaveHudPartialFail() => Lookup("WorldSettings.SaveHudPartialFail");
            public static string SaveHudAllSaved() => Lookup("WorldSettings.SaveHudAllSaved");

            // Reset dialog
            public static string ResetDialogTitle() => Lookup("WorldSettings.ResetDialogTitle");
            public static string ResetDialogSubtitle(string pageName) =>
                Lookup("WorldSettings.ResetDialogSubtitle").Replace("{{page}}", pageName ?? string.Empty);
            public static string ResetDialogBullet1() => Lookup("WorldSettings.ResetDialogBullet1");
            public static string ResetDialogBullet2() => Lookup("WorldSettings.ResetDialogBullet2");
            public static string ResetDialogConfirm() => Lookup("WorldSettings.ResetDialogConfirm");
            public static string ResetDialogCancel() => Lookup("WorldSettings.ResetDialogCancel");

            // ── Date Ambience sub-page (FEAT-WS-I18N-002) ──
            public static class DateAmbiencePage
            {
                // 字段标签
                public static string FieldLocationName() => Lookup("WorldSettings.DateAmbience.FieldLocationName");
                public static string FieldTargetMap() => Lookup("WorldSettings.DateAmbience.FieldTargetMap");
                public static string FieldHeartsWeather() => Lookup("WorldSettings.DateAmbience.FieldHeartsWeather");
                public static string FieldTimeWindow() => Lookup("WorldSettings.DateAmbience.FieldTimeWindow");
                public static string FieldAmbience() => Lookup("WorldSettings.DateAmbience.FieldAmbience");

                // 控件标签与后缀
                public static string HeartsSuffix() => Lookup("WorldSettings.DateAmbience.HeartsSuffix");
                public static string AllowRainyDays() => Lookup("WorldSettings.DateAmbience.AllowRainyDays");
                public static string TimeWindowTo() => Lookup("WorldSettings.DateAmbience.TimeWindowTo");
                public static string CustomTag() => Lookup("WorldSettings.DateAmbience.CustomTag");

                // 空态提示
                public static string EmptySelectionHint() => Lookup("WorldSettings.DateAmbience.EmptySelectionHint");

                // 按钮
                public static string SaveButton() => Lookup("WorldSettings.DateAmbience.SaveButton");
                public static string RevertButton() => Lookup("WorldSettings.DateAmbience.RevertButton");
                public static string DeleteButton() => Lookup("WorldSettings.DateAmbience.DeleteButton");
                public static string NewButton() => Lookup("WorldSettings.DateAmbience.NewButton");

                // 删除弹窗
                public static string DeleteDialogTitle() => Lookup("WorldSettings.DateAmbience.DeleteDialogTitle");
                public static string DeleteDialogSubtitle(string locationName) =>
                    Lookup("WorldSettings.DateAmbience.DeleteDialogSubtitle").Replace("{{location}}", locationName ?? string.Empty);
                public static string DeleteDialogBullet1() => Lookup("WorldSettings.DateAmbience.DeleteDialogBullet1");
                public static string DeleteDialogBullet2() => Lookup("WorldSettings.DateAmbience.DeleteDialogBullet2");
                public static string DeleteDialogConfirm() => Lookup("WorldSettings.DateAmbience.DeleteDialogConfirm");
                public static string DeleteDialogCancel() => Lookup("WorldSettings.DateAmbience.DeleteDialogCancel");

                // HUD 消息
                public static string DeleteSuccessHud() => Lookup("WorldSettings.DateAmbience.DeleteSuccessHud");

                // 新建默认值（语言专属）
                public static string NewLocationDefaultNameZh() => Lookup("WorldSettings.DateAmbience.NewLocationDefaultNameZh");
                public static string NewLocationDefaultNameEn() => Lookup("WorldSettings.DateAmbience.NewLocationDefaultNameEn");
                public static string NewLocationDefaultDescZh() => Lookup("WorldSettings.DateAmbience.NewLocationDefaultDescZh");
                public static string NewLocationDefaultDescEn() => Lookup("WorldSettings.DateAmbience.NewLocationDefaultDescEn");
            }

            // ── POI Tuning sub-page (FEAT-WS-I18N-004) ──
            public static class PoiTuningPage
            {
                // 模式切换按钮
                public static string ModeNpcWeights() => Lookup("WorldSettings.PoiTuning.ModeNpcWeights");
                public static string ModePoiCatalog() => Lookup("WorldSettings.PoiTuning.ModePoiCatalog");

                // 搜索框 placeholder
                public static string SearchSpousePlaceholder() => Lookup("WorldSettings.PoiTuning.SearchSpousePlaceholder");
                public static string SearchPoiPlaceholder() => Lookup("WorldSettings.PoiTuning.SearchPoiPlaceholder");

                // 空态提示
                public static string NoSpousesHint() => Lookup("WorldSettings.PoiTuning.NoSpousesHint");

                // POI 模式字段标签
                public static string AliasLabel() => Lookup("WorldSettings.PoiTuning.AliasLabel");
                public static string AliasPlaceholder() => Lookup("WorldSettings.PoiTuning.AliasPlaceholder");
                public static string DescLabelZh() => Lookup("WorldSettings.PoiTuning.DescLabelZh");
                public static string DescLabelEn() => Lookup("WorldSettings.PoiTuning.DescLabelEn");
                public static string AmbienceLabel(string channelTip) =>
                    Lookup("WorldSettings.PoiTuning.AmbienceLabel").Replace("{{channel}}", channelTip ?? string.Empty);

                // POI 模式按钮
                public static string TeleportButton() => Lookup("WorldSettings.PoiTuning.TeleportButton");
                public static string CaptureButton() => Lookup("WorldSettings.PoiTuning.CaptureButton");
                public static string SaveNewButton() => Lookup("WorldSettings.PoiTuning.SaveNewButton");
                public static string SaveButton() => Lookup("WorldSettings.PoiTuning.SaveButton");
                public static string RevertButton() => Lookup("WorldSettings.PoiTuning.RevertButton");
                public static string CancelNewButton() => Lookup("WorldSettings.PoiTuning.CancelNewButton");
                public static string DeleteButton() => Lookup("WorldSettings.PoiTuning.DeleteButton");
                public static string NewPoiButton() => Lookup("WorldSettings.PoiTuning.NewPoiButton");

                // 传送拦截错误
                public static string FestivalPreparationBlock(string mapName, int startHour) =>
                    Lookup("WorldSettings.PoiTuning.FestivalPreparationBlock")
                        .Replace("{{map}}", mapName ?? string.Empty)
                        .Replace("{{time}}", startHour.ToString());
                public static string FestivalActiveBlock(string mapName) =>
                    Lookup("WorldSettings.PoiTuning.FestivalActiveBlock").Replace("{{map}}", mapName ?? string.Empty);
                public static string EventInProgressBlock() => Lookup("WorldSettings.PoiTuning.EventInProgressBlock");

                // POI 删除弹窗
                public static string DeletePoiDialogTitle() => Lookup("WorldSettings.PoiTuning.DeletePoiDialogTitle");
                public static string DeletePoiDialogSubtitle(string poiId) =>
                    Lookup("WorldSettings.PoiTuning.DeletePoiDialogSubtitle").Replace("{{poi}}", poiId ?? string.Empty);
                public static string DeletePoiDialogBullet1() => Lookup("WorldSettings.PoiTuning.DeletePoiDialogBullet1");
                public static string DeletePoiDialogBullet2() => Lookup("WorldSettings.PoiTuning.DeletePoiDialogBullet2");
                public static string DeletePoiDialogConfirm() => Lookup("WorldSettings.PoiTuning.DeletePoiDialogConfirm");
                public static string DeletePoiDialogCancel() => Lookup("WorldSettings.PoiTuning.DeletePoiDialogCancel");
                public static string DeletePoiSuccessHud() => Lookup("WorldSettings.PoiTuning.DeletePoiSuccessHud");

                // NPC 权重相关 HUD/错误
                public static string SaveWeightsServiceUnavailable() => Lookup("WorldSettings.PoiTuning.SaveWeightsServiceUnavailable");
                public static string SaveWeightsSuccessHud() => Lookup("WorldSettings.PoiTuning.SaveWeightsSuccessHud");
                public static string SaveWeightsFailure() => Lookup("WorldSettings.PoiTuning.SaveWeightsFailure");

                // NPC 权重重置弹窗
                public static string ResetWeightsDialogTitle() => Lookup("WorldSettings.PoiTuning.ResetWeightsDialogTitle");
                public static string ResetWeightsDialogSubtitle(string spouseName) =>
                    Lookup("WorldSettings.PoiTuning.ResetWeightsDialogSubtitle").Replace("{{spouse}}", spouseName ?? string.Empty);
                public static string ResetWeightsDialogBullet1() => Lookup("WorldSettings.PoiTuning.ResetWeightsDialogBullet1");
                public static string ResetWeightsDialogConfirm() => Lookup("WorldSettings.PoiTuning.ResetWeightsDialogConfirm");
                public static string ResetWeightsDialogCancel() => Lookup("WorldSettings.PoiTuning.ResetWeightsDialogCancel");
                public static string ResetWeightsSuccessHud() => Lookup("WorldSettings.PoiTuning.ResetWeightsSuccessHud");

                // ResetToBaseline HUD
                public static string ResetServiceUnavailable() => Lookup("WorldSettings.PoiTuning.ResetServiceUnavailable");
                public static string ResetNothingToReset() => Lookup("WorldSettings.PoiTuning.ResetNothingToReset");
            }

            // ── Location & Festival sub-page (FEAT-WS-I18N-003) ──
            public static class LocationFestivalPage
            {
                // 模式切换按钮
                public static string ModeLocations() => Lookup("WorldSettings.LocationFestival.ModeLocations");
                public static string ModeFestivals() => Lookup("WorldSettings.LocationFestival.ModeFestivals");

                // 搜索框 placeholder
                public static string SearchLocationsPlaceholder() => Lookup("WorldSettings.LocationFestival.SearchLocationsPlaceholder");
                public static string SearchFestivalsPlaceholder() => Lookup("WorldSettings.LocationFestival.SearchFestivalsPlaceholder");

                // 季节选择器
                public static string SeasonSpring() => Lookup("WorldSettings.LocationFestival.SeasonSpring");
                public static string SeasonSummer() => Lookup("WorldSettings.LocationFestival.SeasonSummer");
                public static string SeasonFall() => Lookup("WorldSettings.LocationFestival.SeasonFall");
                public static string SeasonWinter() => Lookup("WorldSettings.LocationFestival.SeasonWinter");
                public static string SeasonSpringShort() => Lookup("WorldSettings.LocationFestival.SeasonSpringShort");
                public static string SeasonSummerShort() => Lookup("WorldSettings.LocationFestival.SeasonSummerShort");
                public static string SeasonFallShort() => Lookup("WorldSettings.LocationFestival.SeasonFallShort");
                public static string SeasonWinterShort() => Lookup("WorldSettings.LocationFestival.SeasonWinterShort");

                // 地点模式默认描述（语言专属）
                public static string DefaultLocationDescZh(string dispName) =>
                    Lookup("WorldSettings.LocationFestival.DefaultLocationDescZh").Replace("{{name}}", dispName ?? string.Empty);
                public static string DefaultLocationDescEn(string dispName) =>
                    Lookup("WorldSettings.LocationFestival.DefaultLocationDescEn").Replace("{{name}}", dispName ?? string.Empty);

                // 地点模式 HUD 消息
                public static string SaveLocationSuccessHud() => Lookup("WorldSettings.LocationFestival.SaveLocationSuccessHud");
                public static string RevertLocationSuccessHud() => Lookup("WorldSettings.LocationFestival.RevertLocationSuccessHud");

                // 地点重置弹窗
                public static string RevertLocationDialogTitle() => Lookup("WorldSettings.LocationFestival.RevertLocationDialogTitle");
                public static string RevertLocationDialogSubtitle(string locationName) =>
                    Lookup("WorldSettings.LocationFestival.RevertLocationDialogSubtitle").Replace("{{location}}", locationName ?? string.Empty);
                public static string RevertLocationDialogBullet1() => Lookup("WorldSettings.LocationFestival.RevertLocationDialogBullet1");
                public static string RevertLocationDialogBullet2() => Lookup("WorldSettings.LocationFestival.RevertLocationDialogBullet2");
                public static string RevertLocationDialogConfirm() => Lookup("WorldSettings.LocationFestival.RevertLocationDialogConfirm");
                public static string RevertLocationDialogCancel() => Lookup("WorldSettings.LocationFestival.RevertLocationDialogCancel");

                // 节日模式 HUD 消息
                public static string FestivalDateConflictHud(string existingFestivalName) =>
                    Lookup("WorldSettings.LocationFestival.FestivalDateConflictHud").Replace("{{festival}}", existingFestivalName ?? string.Empty);
                public static string SaveFestivalSuccessHud() => Lookup("WorldSettings.LocationFestival.SaveFestivalSuccessHud");
                public static string RevertFestivalSuccessHud() => Lookup("WorldSettings.LocationFestival.RevertFestivalSuccessHud");
                public static string DeleteFestivalSuccessHud() => Lookup("WorldSettings.LocationFestival.DeleteFestivalSuccessHud");
                public static string NewFestivalModeHint() => Lookup("WorldSettings.LocationFestival.NewFestivalModeHint");

                // 节日重置弹窗
                public static string RevertFestivalDialogTitle() => Lookup("WorldSettings.LocationFestival.RevertFestivalDialogTitle");
                public static string RevertFestivalDialogSubtitle(string festivalName) =>
                    Lookup("WorldSettings.LocationFestival.RevertFestivalDialogSubtitle").Replace("{{festival}}", festivalName ?? string.Empty);
                public static string RevertFestivalDialogBullet1() => Lookup("WorldSettings.LocationFestival.RevertFestivalDialogBullet1");
                public static string RevertFestivalDialogBullet2() => Lookup("WorldSettings.LocationFestival.RevertFestivalDialogBullet2");
                public static string RevertFestivalDialogBullet3() => Lookup("WorldSettings.LocationFestival.RevertFestivalDialogBullet3");
                public static string RevertFestivalDialogConfirm() => Lookup("WorldSettings.LocationFestival.RevertFestivalDialogConfirm");
                public static string RevertFestivalDialogCancel() => Lookup("WorldSettings.LocationFestival.RevertFestivalDialogCancel");

                // 节日删除弹窗
                public static string DeleteFestivalDialogTitle() => Lookup("WorldSettings.LocationFestival.DeleteFestivalDialogTitle");
                public static string DeleteFestivalDialogSubtitle(string festivalName) =>
                    Lookup("WorldSettings.LocationFestival.DeleteFestivalDialogSubtitle").Replace("{{festival}}", festivalName ?? string.Empty);
                public static string DeleteFestivalDialogBullet1() => Lookup("WorldSettings.LocationFestival.DeleteFestivalDialogBullet1");
                public static string DeleteFestivalDialogConfirm() => Lookup("WorldSettings.LocationFestival.DeleteFestivalDialogConfirm");
                public static string DeleteFestivalDialogCancel() => Lookup("WorldSettings.LocationFestival.DeleteFestivalDialogCancel");

                // 新建节日默认值
                public static string NewFestivalDefaultName() => Lookup("WorldSettings.LocationFestival.NewFestivalDefaultName");
                public static string NewFestivalDefaultDesc() => Lookup("WorldSettings.LocationFestival.NewFestivalDefaultDesc");
            }
        }

        // =========================================================================
        // 6. CORE MEMORY DOMAIN (Shared across All Memory Menus)
        // =========================================================================
        public static class Memory
        {
            // Shell & Navigation for ScrollableMemoryMenu

            // Buttons & Hovers
            public static string CloseButton() => Lookup("Memory.CloseButton");

            // Add/Edit Dialogs
            public static string AddTitle(string name) => FormatNpc(Lookup("Memory.AddTitle"), name);
            public static string EditTitle(string name) => FormatNpc(Lookup("Memory.EditTitle"), name);

            // Tags & Categories
            public static string CategoryFactLabel() => Lookup("Memory.CategoryFactLabel");
            public static string CategoryBehaviorLabel() => Lookup("Memory.CategoryBehaviorLabel");

            // Result HUD Messages
            public static string AddFailedFull(int max) =>
                Lookup("Memory.AddFailedFull").Replace("{{max}}", max.ToString());

            public static string AddFailedDuplicate() => Lookup("Memory.AddFailedDuplicate");

            // Callsigns
            public static string CallsignTitle(string npcName) => FormatNpc(Lookup("Memory.CallsignTitle"), npcName);

            // Archive Box
            public static string ArchiveTitle(string npcName) => FormatNpc(Lookup("Memory.ArchiveTitle"), npcName);

            public static string ArchiveCount(int count, int max)
                => Lookup("Memory.ArchiveCount").Replace("{{count}}", count.ToString())
                    .Replace("{{max}}", max.ToString());

            public static string ArchiveRestoreButton() => Lookup("Memory.ArchiveRestoreButton");
            public static string ArchiveDeleteButton() => Lookup("Memory.ArchiveDeleteButton");

            public static string ArchiveRuleHint(int max) =>
                Lookup("Memory.ArchiveRuleHint").Replace("{{max}}", max.ToString());

            // 2-Column Distill Menu (MemoryDistillMenu.cs)
            public static string DistillFailed() => Lookup("Memory.DistillFailed");
            public static string DistillEmpty() => Lookup("Memory.DistillEmpty");

            public static string DistillNoHistory(string npcName) =>
                FormatNpc(Lookup("Memory.DistillNoHistory"), npcName);

            public static string DistillDuplicate() => Lookup("Memory.DistillDuplicate");

            // Backward compatibility (used by DialogueTextInputMenu.cs)
        }

        // =========================================================================
        // 6c. ARCHIVED MEMORY MENU (Archive box, rules + timeline scopes)
        // =========================================================================
        public static class ArchiveMenu
        {
            public static string IconAlt() => Lookup("ArchiveMenu.IconAlt");
            public static string SubtitleRules() => Lookup("ArchiveMenu.SubtitleRules");
            public static string SubtitleTimeline() => Lookup("ArchiveMenu.SubtitleTimeline");
            public static string ClearButton() => Lookup("ArchiveMenu.ClearButton");
            public static string ClearTooltip() => Lookup("ArchiveMenu.ClearTooltip");

            public static string CapacityNearFull(int count, int max)
                => Lookup("ArchiveMenu.CapacityNearFull").Replace("{{count}}", count.ToString())
                    .Replace("{{max}}", max.ToString());

            public static string CapacityNormal(int count, int max)
                => Lookup("ArchiveMenu.CapacityNormal").Replace("{{count}}", count.ToString())
                    .Replace("{{max}}", max.ToString());

            public static string EndangeredTag() => Lookup("ArchiveMenu.EndangeredTag");
            public static string RestoreTooltip() => Lookup("ArchiveMenu.RestoreTooltip");
            public static string DeleteTooltip() => Lookup("ArchiveMenu.DeleteTooltip");

            public static string DeleteConfirmTitle() => Lookup("ArchiveMenu.DeleteConfirmTitle");
            public static string DeleteConfirmSubtitle() => Lookup("ArchiveMenu.DeleteConfirmSubtitle");

            public static string ClearConfirmTitle() => Lookup("ArchiveMenu.ClearConfirmTitle");
            public static string ClearConfirmSubtitle(int count)
                => Lookup("ArchiveMenu.ClearConfirmSubtitle").Replace("{{count}}", count.ToString());
            public static string ClearConfirmWarning() => Lookup("ArchiveMenu.ClearConfirmWarning");
            public static string ClearConfirmTip() => Lookup("ArchiveMenu.ClearConfirmTip");

            public static string TagTownConsensus() => Lookup("ArchiveMenu.TagTownConsensus");
            public static string TagBehavior() => Lookup("ArchiveMenu.TagBehavior");
            public static string TagFact() => Lookup("ArchiveMenu.TagFact");
            public static string TagJournal() => Lookup("ArchiveMenu.TagJournal");
            public static string TagWeekly() => Lookup("ArchiveMenu.TagWeekly");
            public static string TagChronicle() => Lookup("ArchiveMenu.TagChronicle");

            public static string RestoreDuplicate() => Lookup("ArchiveMenu.RestoreDuplicate");
            public static string RestoreNotFound() => Lookup("ArchiveMenu.RestoreNotFound");
            public static string RestoreCapacityFull(int max)
                => Lookup("ArchiveMenu.RestoreCapacityFull").Replace("{{max}}", max.ToString());

            public static string EmptyPrompt() => Lookup("ArchiveMenu.EmptyPrompt");
            public static string EmptyPromptTimeline() => Lookup("ArchiveMenu.EmptyPromptTimeline");
        }

        public static class DialogueInput
        {
            public static string DefaultTitle() => Lookup("DialogueInput.DefaultTitle");

            public static string DefaultTitleWithNpc(string npcName) =>
                FormatNpc(Lookup("DialogueInput.DefaultTitleWithNpc"), npcName);

            public static string ClearScopeTitle() => Lookup("DialogueInput.ClearScopeTitle");
            public static string ClearScopeHint() => Lookup("DialogueInput.ClearScopeHint");

            public static string ClearScopeToday(string npcName) =>
                FormatNpc(Lookup("DialogueInput.ClearScopeToday"), npcName);

            public static string ClearScopeCurrentNpcAll(string npcName) =>
                FormatNpc(Lookup("DialogueInput.ClearScopeCurrentNpcAll"), npcName);

            public static string ClearScopeGlobalAll() => Lookup("DialogueInput.ClearScopeGlobalAll");

            public static string ClearScopeHudToday(string npcName) =>
                FormatNpc(Lookup("DialogueInput.ClearScopeHudToday"), npcName);

            public static string ClearScopeHudCurrentNpcAll(string npcName) =>
                FormatNpc(Lookup("DialogueInput.ClearScopeHudCurrentNpcAll"), npcName);

            public static string ClearScopeHudGlobalAll() => Lookup("DialogueInput.ClearScopeHudGlobalAll");

            // ── 输入框占位符 / 副标题 / 按钮 / 气泡（VT3-i18n-002B）──
            public static string Placeholder() => Lookup("DialogInput.Placeholder");
            public static string PlaceholderWithName(string name) =>
                Lookup("DialogInput.PlaceholderWithName").Replace("{{name}}", name ?? string.Empty);

            public static string SubtitleGeneric() => Lookup("DialogInput.SubtitleGeneric");
            public static string SubtitleWithName(string name) =>
                Lookup("DialogInput.SubtitleWithName").Replace("{{name}}", name ?? string.Empty);

            public static string ButtonHistory() => Lookup("DialogInput.ButtonHistory");
            public static string ButtonClearMemory() => Lookup("DialogInput.ButtonClearMemory");
            public static string ButtonCancel() => Lookup("DialogInput.ButtonCancel");
            public static string ButtonSend() => Lookup("DialogInput.ButtonSend");

            public static string TooltipHistory(string name) =>
                Lookup("DialogInput.TooltipHistory").Replace("{{name}}", name ?? string.Empty);
            public static string TooltipHistoryGeneric() => Lookup("DialogInput.TooltipHistoryGeneric");
            public static string TooltipClearMemory(string name) =>
                Lookup("DialogInput.TooltipClearMemory").Replace("{{name}}", name ?? string.Empty);
            public static string TooltipClearMemoryGeneric() => Lookup("DialogInput.TooltipClearMemoryGeneric");
            public static string TooltipCancel() => Lookup("DialogInput.TooltipCancel");
            public static string TooltipSend() => Lookup("DialogInput.TooltipSend");

        }

        public static class Follower
        {
            public static string BusyHud(string name)
                => Lookup("Follower.BusyHud").Replace("{{name}}", name ?? string.Empty);

            public static string NotFamiliar(string name)
                => Lookup("Follower.NotFamiliar").Replace("{{name}}", name ?? string.Empty);

            public static string StartFollowingHud(string name, string key)
                => Lookup("Follower.StartFollowingHud")
                    .Replace("{{name}}", name ?? string.Empty)
                    .Replace("{{key}}", key ?? string.Empty);

            public static string DismissDateConfirm(string name)
                => Lookup("Follower.DismissDateConfirm").Replace("{{name}}", name ?? string.Empty);

            public static string DismissFollowConfirm(string name)
                => Lookup("Follower.DismissFollowConfirm").Replace("{{name}}", name ?? string.Empty);

            public static string ConfirmYes() => Lookup("Follower.ConfirmYes");
            public static string ConfirmNo() => Lookup("Follower.ConfirmNo");

            public static string DateLeaveHeadText() => Lookup("Follower.DateLeaveHeadText");
            public static string FollowLeaveHeadText() => Lookup("Follower.FollowLeaveHeadText");

            public static string TooFarAway(string name)
                => Lookup("Follower.TooFarAway").Replace("{{name}}", name ?? string.Empty);
        }

        // =========================================================================
        // 6d. POI INSPECT HUD (Survey point capture HUD)
        // =========================================================================
        public static class PoiHud
        {
            public static string TitleBadge() => Lookup("PoiHud.TitleBadge");

            public static string LocationLabel(string map, int x, int y)
                => Lookup("PoiHud.LocationLabel")
                    .Replace("{{map}}", map ?? string.Empty)
                    .Replace("{{x}}", x.ToString())
                    .Replace("{{y}}", y.ToString());

            public static string StatusPassable() => Lookup("PoiHud.StatusPassable");
            public static string StatusBlocked(string reason)
                => Lookup("PoiHud.StatusBlocked").Replace("{{reason}}", reason ?? string.Empty);

            public static string UnknownMap() => Lookup("PoiHud.UnknownMap");

            public static string ButtonCapture() => Lookup("PoiHud.ButtonCapture");
            public static string ButtonConfirm() => Lookup("PoiHud.ButtonConfirm");
            public static string ButtonUnstuck() => Lookup("PoiHud.ButtonUnstuck");
            public static string ButtonClose() => Lookup("PoiHud.ButtonClose");

            public static string CaptureFailedHud(string reason)
                => Lookup("PoiHud.CaptureFailedHud").Replace("{{reason}}", reason ?? string.Empty);
            public static string ConfirmFailedHud(string reason)
                => Lookup("PoiHud.ConfirmFailedHud").Replace("{{reason}}", reason ?? string.Empty);
            public static string ConfirmSuccessHud(string name, int x, int y)
                => Lookup("PoiHud.ConfirmSuccessHud")
                    .Replace("{{name}}", name ?? string.Empty)
                    .Replace("{{x}}", x.ToString())
                    .Replace("{{y}}", y.ToString());
            public static string UnstuckSuccessHud(int x, int y)
                => Lookup("PoiHud.UnstuckSuccessHud")
                    .Replace("{{x}}", x.ToString())
                    .Replace("{{y}}", y.ToString());
            public static string UnstuckFailedHud() => Lookup("PoiHud.UnstuckFailedHud");
        }

        // =========================================================================
        // 6e. ADD MULTI RULE INPUT MENU (Batch rule creation & dispatch)
        // =========================================================================
        public static class AddRuleMenu
        {
            public static string SubtitleGlobal() => Lookup("AddRuleMenu.SubtitleGlobal");
            public static string SubtitleNpc(string name) =>
                Lookup("AddRuleMenu.SubtitleNpc").Replace("{{name}}", name ?? string.Empty);

            public static string CategoryLabel() => Lookup("AddRuleMenu.CategoryLabel");
            public static string CategoryFact() => Lookup("AddRuleMenu.CategoryFact");
            public static string CategoryBehavior() => Lookup("AddRuleMenu.CategoryBehavior");

            public static string ScopeLabel() => Lookup("AddRuleMenu.ScopeLabel");
            public static string ScopeAll() => Lookup("AddRuleMenu.ScopeAll");
            public static string ScopeSelected(int count) =>
                Lookup("AddRuleMenu.ScopeSelected").Replace("{{count}}", count.ToString());

            public static string NpcSelectorPrompt() => Lookup("AddRuleMenu.NpcSelectorPrompt");
            public static string NpcSearchPlaceholder() => Lookup("AddRuleMenu.NpcSearchPlaceholder");
            public static string InputPlaceholder(int limit) =>
                Lookup("AddRuleMenu.InputPlaceholder").Replace("{{limit}}", limit.ToString());

            public static string CharCountPattern(int current, int limit)
                => Lookup("AddRuleMenu.CharCountPattern")
                    .Replace("{{current}}", current.ToString())
                    .Replace("{{limit}}", limit.ToString());

            public static string ButtonConfirm() => Lookup("AddRuleMenu.ButtonConfirm");
            public static string ButtonCancel() => Lookup("AddRuleMenu.ButtonCancel");

            public static string ValidationEmptyContent() => Lookup("AddRuleMenu.ValidationEmptyContent");
            public static string ValidationExceedsLimit(int limit) =>
                Lookup("AddRuleMenu.ValidationExceedsLimit").Replace("{{limit}}", limit.ToString());
            public static string ValidationNoNpcSelected() => Lookup("AddRuleMenu.ValidationNoNpcSelected");

            public static string SuccessHudSingle(int count, string name)
                => Lookup("AddRuleMenu.SuccessHudSingle")
                    .Replace("{{count}}", count.ToString())
                    .Replace("{{name}}", name ?? string.Empty);
            public static string SuccessHudMultiple(int count, int npcCount)
                => Lookup("AddRuleMenu.SuccessHudMultiple")
                    .Replace("{{count}}", count.ToString())
                    .Replace("{{npcCount}}", npcCount.ToString());
            public static string SuccessHudGlobal(int count)
                => Lookup("AddRuleMenu.SuccessHudGlobal").Replace("{{count}}", count.ToString());

            public static string TooltipCategory() => Lookup("AddRuleMenu.TooltipCategory");
            public static string TooltipScope() => Lookup("AddRuleMenu.TooltipScope");
            public static string TooltipConfirm() => Lookup("AddRuleMenu.TooltipConfirm");
            public static string TooltipCancel() => Lookup("AddRuleMenu.TooltipCancel");
        }

        /// <summary>统一警示/确认弹窗（BioValveWarningDialog）的共享按钮文案。</summary>
        public static class Dialog
        {
            public static string ConfirmDelete() => Lookup("Dialog.ConfirmDelete");
            public static string ConfirmArchive() => Lookup("Dialog.ConfirmArchive");
            public static string Keep() => Lookup("Dialog.Keep");
        }

        // =========================================================================
        // 7. BIO EDITOR / CHARACTER STUDIO DOMAIN (Isolated Scope)
        // =========================================================================
        public static class Bio
        {
            public static string TabTitle(int index) => Lookup("Bio.Tab" + (index + 1));

            public static string Title(string display, string internalName) => Lookup("Bio.Title")
                .Replace("{{display}}", display ?? string.Empty)
                .Replace("{{internal}}", internalName ?? string.Empty);

            public static string StageCount(int count, int max) => Lookup("Bio.StageCountPattern")
                .Replace("{{count}}", count.ToString())
                .Replace("{{max}}", max.ToString());

            public static string StageRow(int number, string gateSummary) => Lookup("Bio.StageRowPattern")
                .Replace("{{num}}", number.ToString())
                .Replace("{{gates}}", gateSummary ?? string.Empty);

            public static string GateSpouse(string spouseDisplay) => Lookup("Bio.GateSpousePattern")
                .Replace("{{spouse}}", spouseDisplay ?? string.Empty);

            public static string RelHeading(string npcDisplay, string targetDisplay) => Lookup("Bio.RelHeadingPattern")
                .Replace("{{npc}}", npcDisplay ?? string.Empty)
                .Replace("{{target}}", targetDisplay ?? string.Empty);

            public static string DeleteRelSubtitle(string npcDisplay, string targetDisplay) => Lookup("Bio.DeleteRelSubtitle")
                .Replace("{{npc}}", npcDisplay ?? string.Empty)
                .Replace("{{target}}", targetDisplay ?? string.Empty);

            public static string PromptTitle(string section) => Lookup("Bio.PromptTitlePattern")
                .Replace("{{section}}", section ?? string.Empty);

            public static string WizardTitle(string npcDisplay) => Lookup("Bio.WizardTitlePattern")
                .Replace("{{npc}}", npcDisplay ?? string.Empty);

            public static string StageNum(int number) => Lookup("Bio.StageNumPattern")
                .Replace("{{num}}", number.ToString());

            public static string ExportDone(string npcDisplay) => Lookup("Bio.ExportDone")
                .Replace("{{npc}}", npcDisplay ?? string.Empty);

            public static string ResetDone(string npcDisplay) => Lookup("Bio.ResetDone")
                .Replace("{{npc}}", npcDisplay ?? string.Empty);

            public static string ReviewBioTitle(string npcName) => Lookup("Bio.ReviewBioTitle")
                .Replace("{{name}}", npcName ?? string.Empty);

            public static string ReviewAmbientTitle(string npcName) => Lookup("Bio.ReviewAmbientTitle")
                .Replace("{{name}}", npcName ?? string.Empty);

            public static string ReviewLadderTitle(string npcName) => Lookup("Bio.ReviewLadderTitle")
                .Replace("{{name}}", npcName ?? string.Empty);

            public static string WithErr(string key, string err) => Lookup(key)
                .Replace("{{err}}", err ?? string.Empty);
        }
    }
}
