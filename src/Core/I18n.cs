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
            public static string ArchiveEmpty() => Lookup("Memory.ArchiveEmpty");

            public static string ArchiveCount(int count, int max)
                => Lookup("Memory.ArchiveCount").Replace("{{count}}", count.ToString())
                    .Replace("{{max}}", max.ToString());

            public static string ArchiveRestoreButton() => Lookup("Memory.ArchiveRestoreButton");
            public static string ArchiveDeleteButton() => Lookup("Memory.ArchiveDeleteButton");

            public static string ArchiveDeleteConfirmTitle() => Lookup("Memory.ArchiveDeleteConfirmTitle");
            public static string ArchiveDeleteConfirmSubtitle() => Lookup("Memory.ArchiveDeleteConfirmSubtitle");

            public static string ArchiveRestoreDuplicate() => Lookup("Memory.ArchiveRestoreDuplicate");
            public static string ArchiveRestoreNotFound() => Lookup("Memory.ArchiveRestoreNotFound");

            public static string ArchiveRuleHint(int max) =>
                Lookup("Memory.ArchiveRuleHint").Replace("{{max}}", max.ToString());

            // ── 时间线归档箱（FEAT-AUTO-T6 解耦：与 Manual/Auto 归档箱文案不共用）──
            public static string ArchiveClearButton() => Lookup("Memory.ArchiveClearButton");

            // 新增：实体按钮悬停提示文案（带回退保护）

            public static string ArchiveClearConfirmTitle() => Lookup("Memory.ArchiveClearConfirmTitle");

            public static string ArchiveClearConfirmSubtitle(int count) =>
                Lookup("Memory.ArchiveClearConfirmSubtitle").Replace("{{count}}", count.ToString());

            public static string ArchiveClearConfirmWarning() => Lookup("Memory.ArchiveClearConfirmWarning");

            public static string ArchiveEndangeredTag() => Lookup("Memory.ArchiveEndangeredTag");

            // 2-Column Distill Menu (MemoryDistillMenu.cs)
            public static string DistillFailed() => Lookup("Memory.DistillFailed");
            public static string DistillEmpty() => Lookup("Memory.DistillEmpty");

            public static string DistillNoHistory(string npcName) =>
                FormatNpc(Lookup("Memory.DistillNoHistory"), npcName);

            public static string DistillDuplicate() => Lookup("Memory.DistillDuplicate");

            // Backward compatibility (used by DialogueTextInputMenu.cs)
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

        /// <summary>统一警示/确认弹窗（BioValveWarningDialog）的共享按钮文案。</summary>
        public static class Dialog
        {
            public static string ConfirmDelete() => Lookup("Dialog.ConfirmDelete");
            public static string ConfirmArchive() => Lookup("Dialog.ConfirmArchive");
            public static string Keep() => Lookup("Dialog.Keep");
        }
    }
}
