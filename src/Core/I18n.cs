using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;
using StardewValley;

namespace ValleytalkReborn
{
    public static class I18n
    {
        private static Dictionary<string, string> _english = Load("default");
        private static Dictionary<string, string> _locale = _english;
        private static string _localeName = string.Empty;

        public static bool IsChinese => LocalizedContentManager.CurrentLanguageCode == LocalizedContentManager.LanguageCode.zh;

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
            catch { }

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
                catch { }
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
                catch { }
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

        public static string MemoryCharacterLimit()
        {
            var maxLen = MemoryManager.Instance.GetMaxMemoryLength();
            return Lookup("Memory.CharacterLimit")
                .Replace("{{value}}", maxLen.ToString())
                .Replace("{{0}}", maxLen.ToString());
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
            public static string TodayDate(string date) => Lookup("Timeline.TodayDate").Replace("{{date}}", date ?? string.Empty);
            public static string EmptyChats() => Lookup("Timeline.EmptyChats");
            public static string EmptyTier() => Lookup("Timeline.EmptyTier");
            public static string CooldownHud(int remaining) => Lookup("Timeline.CooldownHud").Replace("{{remaining}}", remaining.ToString());
            public static string CondenseMinCount(int min) => Lookup("Timeline.CondenseMinCount").Replace("{{min}}", min.ToString());
            public static string TabChats() => Lookup("Timeline.TabChats");
            public static string TabImpressions() => Lookup("Timeline.TabImpressions");
            public static string TabWeekly() => Lookup("Timeline.TabWeekly");
            public static string TabChronicle() => Lookup("Timeline.TabChronicle");
            public static string DistillThisPage() => Lookup("Timeline.DistillThisPage");
            // ── 与 Memory.* 解耦的时间线专属文案（此前与 ScrollableMemoryMenu/IntegratedHubMenu 共用）──
            public static string CloseButton() => Lookup("Timeline.CloseButton");
            public static string DeleteConfirm(string content) => Lookup("Timeline.DeleteConfirm").Replace("{{content}}", content ?? string.Empty);
            public static string DistillLlmDisabled() => Lookup("Timeline.DistillLlmDisabled");
            public static string DistillNoHistory(string npcName) => Lookup("Timeline.DistillNoHistory").Replace("{{npcName}}", npcName ?? string.Empty);
            public static string EditButtonHover() => Lookup("Timeline.EditButtonHover");
            public static string DeleteButtonHover() => Lookup("Timeline.DeleteButtonHover");
            public static string ArchiveButton(int count, int max)
                => Lookup("Timeline.ArchiveButton").Replace("{{count}}", count.ToString()).Replace("{{max}}", max.ToString());
            public static string ArchiveTitle(string npcName) => FormatNpc(Lookup("Timeline.ArchiveTitle"), npcName);
            public static string ArchiveEmpty() => Lookup("Timeline.ArchiveEmpty");
            public static string ArchiveRuleHint(int max) => Lookup("Timeline.ArchiveRuleHint").Replace("{{max}}", max.ToString());
            public static string ConsolidateToWeekly(int count) => Lookup("Timeline.ConsolidateToWeekly").Replace("{{count}}", count.ToString());
            public static string ElevateToChronicle(int count) => Lookup("Timeline.ElevateToChronicle").Replace("{{count}}", count.ToString());
            public static string PrevDay() => Lookup("Timeline.PrevDay");
            public static string NextDay() => Lookup("Timeline.NextDay");
        }

        // =========================================================================
        // 2. TIMELINE DISTILL CARD MENU (Isolated Scope)
        // =========================================================================
        public static class TimelineDistill
        {
            public static string TitleWeekly(string npcName) => FormatNpc(Lookup("TimelineDistill.TitleWeekly"), npcName);
            public static string TitleChronicle(string npcName) => FormatNpc(Lookup("TimelineDistill.TitleChronicle"), npcName);
            public static string TitleDaily(string npcName) => FormatNpc(Lookup("TimelineDistill.TitleDaily"), npcName);
            public static string Capacity(int current, int max)
                => Lookup("TimelineDistill.Capacity").Replace("{{current}}", current.ToString()).Replace("{{max}}", max.ToString());
            public static string Loading() => Lookup("TimelineDistill.Loading");
            public static string SourceHeader(int count) => Lookup("TimelineDistill.SourceHeader").Replace("{{count}}", count.ToString());
            public static string DraftTag(int index) => Lookup("TimelineDistill.DraftTag").Replace("{{index}}", index.ToString());
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
            public static string SelectNpcLabel() => Lookup("Hub.SelectNpcLabel");
            public static string TabNpcMemory() => Lookup("Hub.TabNpcMemory");
            public static string TabWorldMemory() => Lookup("Hub.TabWorldMemory");
            public static string TabProfile() => Lookup("Hub.TabProfile");
            public static string TabAdvanced() => Lookup("Hub.TabAdvanced");
        }

        // =========================================================================
        // 4. FARMER PROFILE DOMAIN (Shared between Hub Tab2 & PlayerProfileCustomMenu)
        // =========================================================================
        public static class Profile
        {
            public static string MenuTitle() => Lookup("Profile.MenuTitle");
            public static string EnableProfile() => Lookup("Profile.EnableProfile");
            public static string OrientationLabel() => Lookup("Profile.OrientationLabel");
            public static string SelectOption() => Lookup("Profile.SelectOption");
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
            public static string ButtonHover() => Lookup("Profile.ButtonHover");
        }

        // =========================================================================
        // 5. ADVANCED SETTINGS DOMAIN (Shared between Hub Tab3 & AdvancedSettingsMenu)
        // =========================================================================
        public static class AdvancedSettings
        {
            public static string MenuTitle() => Lookup("AdvancedSettings.MenuTitle");
            public static string InfiniteChat() => Lookup("AdvancedSettings.InfiniteChat");
            public static string InfiniteChatTooltip() => Lookup("AdvancedSettings.InfiniteChatTooltip");
            public static string VanillaFirst() => Lookup("AdvancedSettings.VanillaFirst");
            public static string VanillaFirstTooltip() => Lookup("AdvancedSettings.VanillaFirstTooltip");
            public static string RecordVanillaDialogue() => Lookup("AdvancedSettings.RecordVanillaDialogue");
            public static string RecordVanillaDialogueTooltip() => Lookup("AdvancedSettings.RecordVanillaDialogueTooltip");
            public static string Disclaimer() => Lookup("AdvancedSettings.Disclaimer");

            // Backward compatibility (used by DialogueTextInputMenu.cs)
            public static string ButtonHover() => Lookup("AdvancedSettings.ButtonHover");
        }

        // =========================================================================
        // 6. CORE MEMORY DOMAIN (Shared across All Memory Menus)
        // =========================================================================
        public static class Memory
        {
            // Shell & Navigation for ScrollableMemoryMenu
            public static string MenuTitle() => Lookup("Memory.MenuTitle");
            public static string TabNpc(string name) => FormatNpc(Lookup("Memory.TabNpc"), name);
            public static string TabWorld() => Lookup("Memory.TabWorld");
            public static string Empty() => Lookup("Memory.Empty");
            public static string WorldEmpty() => Lookup("Memory.WorldEmpty");

            // Buttons & Hovers
            public static string AddButton() => Lookup("Memory.AddButton");
            public static string AddWorldButton() => Lookup("Memory.AddWorldButton");
            public static string CloseButton() => Lookup("Memory.CloseButton");
            public static string EditButtonHover() => Lookup("Memory.EditButtonHover");
            public static string DeleteButtonHover() => Lookup("Memory.DeleteButtonHover");

            // Add/Edit Dialogs
            public static string AddTitle(string name) => FormatNpc(Lookup("Memory.AddTitle"), name);
            public static string EditTitle(string name) => FormatNpc(Lookup("Memory.EditTitle"), name);
            public static string AddHint(string npcName) => FormatNpc(Lookup("Memory.AddHint"), npcName);
            public static string WorldAddTitle() => Lookup("Memory.WorldAddTitle");
            public static string WorldEditTitle() => Lookup("Memory.WorldEditTitle");
            public static string WorldAddHint(int max) => Lookup("Memory.WorldAddHint").Replace("{{max}}", max.ToString());

            // Tags & Categories
            public static string RuleTag() => Lookup("Memory.RuleTag");
            public static string MemoryTag() => Lookup("Memory.MemoryTag");
            public static string AutoPrefix() => Lookup("Memory.AutoPrefix");
            public static string CategoryFactLabel() => Lookup("Memory.CategoryFactLabel");
            public static string CategoryBehaviorLabel() => Lookup("Memory.CategoryBehaviorLabel");
            public static string CategoryFactHint() => Lookup("Memory.CategoryFactHint");
            public static string CategoryBehaviorHint() => Lookup("Memory.CategoryBehaviorHint");

            // Result HUD Messages
            public static string AddFailedFull(int max) => Lookup("Memory.AddFailedFull").Replace("{{max}}", max.ToString());
            public static string AddFailedDuplicate() => Lookup("Memory.AddFailedDuplicate");
            public static string AddFailedTooLong(int max) => Lookup("Memory.AddFailedTooLong").Replace("{{max}}", max.ToString());
            public static string DeleteConfirm(string content) => Lookup("Memory.DeleteConfirm").Replace("{{content}}", content ?? string.Empty);

            // Callsigns
            public static string CallsignPrefix() => Lookup("Memory.CallsignPrefix");
            public static string CallsignUnset() => Lookup("Memory.CallsignUnset");
            public static string CallsignTitle(string npcName) => FormatNpc(Lookup("Memory.CallsignTitle"), npcName);
            public static string CallsignHint() => Lookup("Memory.CallsignHint");

            // Archive Box
            public static string ArchiveButton(int count, int max)
                => Lookup("Memory.ArchiveButton").Replace("{{count}}", count.ToString()).Replace("{{max}}", max.ToString());
            public static string ArchiveTitle(string npcName) => FormatNpc(Lookup("Memory.ArchiveTitle"), npcName);
            public static string ArchiveEmpty() => Lookup("Memory.ArchiveEmpty");
            public static string ArchiveCount(int count, int max)
                => Lookup("Memory.ArchiveCount").Replace("{{count}}", count.ToString()).Replace("{{max}}", max.ToString());
            public static string ArchiveRestoreButton() => Lookup("Memory.ArchiveRestoreButton");
            public static string ArchiveDeleteButton() => Lookup("Memory.ArchiveDeleteButton");
            public static string ArchiveDeleteConfirm(string content) => Lookup("Memory.ArchiveDeleteConfirm").Replace("{{content}}", content ?? string.Empty);
            public static string ArchiveRestoreDuplicate() => Lookup("Memory.ArchiveRestoreDuplicate");
            public static string ArchiveRestoreNotFound() => Lookup("Memory.ArchiveRestoreNotFound");
            public static string ArchiveRuleHint(int max) => Lookup("Memory.ArchiveRuleHint").Replace("{{max}}", max.ToString());
            // ── 时间线归档箱（FEAT-AUTO-T6 解耦：与 Manual/Auto 归档箱文案不共用）──
            public static string ArchiveClearButton() => Lookup("Memory.ArchiveClearButton");
            public static string ArchiveClearConfirm(int count) => Lookup("Memory.ArchiveClearConfirm").Replace("{{count}}", count.ToString());
            public static string ArchiveEndangeredTag() => Lookup("Memory.ArchiveEndangeredTag");

            // 2-Column Distill Menu (MemoryDistillMenu.cs)
            public static string DistillTitle(string npcName) => FormatNpc(Lookup("Memory.DistillTitle"), npcName);
            public static string DistillButton() => Lookup("Memory.DistillButton");
            public static string DistillLoading() => Lookup("Memory.DistillLoading");
            public static string DistillFailed() => Lookup("Memory.DistillFailed");
            public static string DistillEmpty() => Lookup("Memory.DistillEmpty");
            public static string DistillNoHistory(string npcName) => FormatNpc(Lookup("Memory.DistillNoHistory"), npcName);
            public static string DistillLeftTitle() => Lookup("Memory.DistillLeftTitle");
            public static string DistillRightTitle() => Lookup("Memory.DistillRightTitle");
            public static string DistillDuplicate() => Lookup("Memory.DistillDuplicate");
            public static string DistillLlmDisabled() => Lookup("Memory.DistillLlmDisabled");

            // Backward compatibility (used by DialogueTextInputMenu.cs)
            public static string ButtonHover(string name) => FormatNpc(Lookup("Memory.ButtonHover"), name);
        }

        public static class DialogueInput
        {
            public static string DefaultTitle() => Lookup("DialogueInput.DefaultTitle");
            public static string DefaultTitleWithNpc(string npcName) => FormatNpc(Lookup("DialogueInput.DefaultTitleWithNpc"), npcName);
            public static string Instruction() => Lookup("DialogueInput.Instruction");

            public static string ClearHistoryHover(string npcName) => FormatNpc(Lookup("DialogueInput.ClearHistoryHover"), npcName);
            public static string ViewHistoryHover(string npcName) => FormatNpc(Lookup("DialogueInput.ViewHistoryHover"), npcName);

            public static string ClearAllConfirm() => Lookup("DialogueInput.ClearAllConfirm");
            public static string ClearOneConfirm(string npcName) => FormatNpc(Lookup("DialogueInput.ClearOneConfirm"), npcName);

            public static string HistoryTitle(string npcName) => FormatNpc(Lookup("DialogueInput.HistoryTitle"), npcName);
            public static string HistoryEmpty(string npcName) => FormatNpc(Lookup("DialogueInput.HistoryEmpty"), npcName);
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
                    .Replace("{{key}}",  key  ?? string.Empty);

            public static string DismissDateConfirm(string name)
                => Lookup("Follower.DismissDateConfirm").Replace("{{name}}", name ?? string.Empty);

            public static string DismissFollowConfirm(string name)
                => Lookup("Follower.DismissFollowConfirm").Replace("{{name}}", name ?? string.Empty);

            public static string ConfirmYes() => Lookup("Follower.ConfirmYes");
            public static string ConfirmNo()  => Lookup("Follower.ConfirmNo");

            public static string DateLeaveHeadText()   => Lookup("Follower.DateLeaveHeadText");
            public static string FollowLeaveHeadText() => Lookup("Follower.FollowLeaveHeadText");

            public static string TooFarAway(string name)
                => Lookup("Follower.TooFarAway").Replace("{{name}}", name ?? string.Empty);
        }
    }
}
