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

            // 1. 优先读取已成功加载的语言包字典（包括 ContentPack 与 i18n）
            if (_locale != null && _locale.TryGetValue(key, out var val) && !string.IsNullOrEmpty(val))
                return val;

            // 2. 回退到默认英文语言包
            if (_english != null && _english.TryGetValue(key, out val) && !string.IsNullOrEmpty(val))
                return val;

            // 3. 回退到 SMAPI Translation（若有配置）
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

            // 1. 扫描同级或子级的 ContentPack 目录（物理安全穿透）
            AddFileCandidates("../ContentPack/i18n");
            AddFileCandidates("../[CP] ValleyTalkReborn Base/i18n");
            AddFileCandidates("ContentPack/i18n");
            AddFileCandidates("[CP] ValleyTalkReborn Base/i18n");

            // 2. 回退扫描当前主模组根目录下的 i18n
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

        public static class Memory
        {
            public static string Title(string name, int count)
                => FormatNpc(Lookup("Memory.Title"), name)
                    .Replace("{{1}}", count.ToString())
                    .Replace("{{Count}}", count.ToString())
                    .Replace("{{count}}", count.ToString());

            public static string Empty() => Lookup("Memory.Empty");
            public static string AddButton() => Lookup("Memory.AddButton");
            public static string AddWorldButton() => Lookup("Memory.AddWorldButton");

            public static string EditTitle(string name) => FormatNpc(Lookup("Memory.EditTitle"), name);
            public static string EditButtonHover() => Lookup("Memory.EditButtonHover");
            public static string DeleteButtonHover() => Lookup("Memory.DeleteButtonHover");
            public static string AddTitle(string name) => FormatNpc(Lookup("Memory.AddTitle"), name);
            public static string AddHint(string npcName) => FormatNpc(Lookup("Memory.AddHint"), npcName);
            public static string AddSuccess() => Lookup("Memory.AddSuccess");

            public static string AddFailedFull(int max)
                => Lookup("Memory.AddFailedFull")
                    .Replace("{{0}}", max.ToString())
                    .Replace("{{value}}", max.ToString())
                    .Replace("{{Max}}", max.ToString())
                    .Replace("{{max}}", max.ToString());

            public static string AddFailedDuplicate() => Lookup("Memory.AddFailedDuplicate");

            public static string AddFailedTooLong(int max)
                => Lookup("Memory.AddFailedTooLong")
                    .Replace("{{0}}", max.ToString())
                    .Replace("{{value}}", max.ToString())
                    .Replace("{{Max}}", max.ToString())
                    .Replace("{{max}}", max.ToString());

            public static string DeleteConfirm(string content)
                => Lookup("Memory.DeleteConfirm")
                    .Replace("{{0}}", content ?? string.Empty)
                    .Replace("{{value}}", content ?? string.Empty)
                    .Replace("{{Content}}", content ?? string.Empty)
                    .Replace("{{content}}", content ?? string.Empty);

            public static string DeleteSuccess() => Lookup("Memory.DeleteSuccess");
            public static string ButtonHover(string name) => FormatNpc(Lookup("Memory.ButtonHover"), name);
            public static string CloseButton() => Lookup("Memory.CloseButton");
            public static string CancelButton() => Lookup("Memory.CancelButton");
            public static string OKButton() => Lookup("Memory.OKButton");

            public static string CharacterLimit(int max)
                => Lookup("Memory.CharacterLimit")
                    .Replace("{{0}}", max.ToString())
                    .Replace("{{value}}", max.ToString())
                    .Replace("{{Max}}", max.ToString())
                    .Replace("{{max}}", max.ToString());

            public static string MenuTitle() => Lookup("Memory.MenuTitle");
            public static string TabNpc(string name) => FormatNpc(Lookup("Memory.TabNpc"), name);
            public static string TabWorld() => Lookup("Memory.TabWorld");
            public static string WorldEmpty() => Lookup("Memory.WorldEmpty");
            public static string WorldEditTitle() => Lookup("Memory.WorldEditTitle");
            public static string WorldAddTitle() => Lookup("Memory.WorldAddTitle");
            public static string WorldAddHint(int max)
                => Lookup("Memory.WorldAddHint")
                    .Replace("{{max}}", max.ToString())
                    .Replace("{{Max}}", max.ToString())
                    .Replace("{{value}}", max.ToString())
                    .Replace("{{0}}", max.ToString());

            public static string AutoPrefix() => Lookup("Memory.AutoPrefix");
            public static string CallsignPrefix() => Lookup("Memory.CallsignPrefix");
            public static string CallsignUnset() => Lookup("Memory.CallsignUnset");
            public static string CallsignTitle(string npcName) => FormatNpc(Lookup("Memory.CallsignTitle"), npcName);
            public static string CallsignHint() => Lookup("Memory.CallsignHint");
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

        public static class Profile
        {
            public static string ButtonHover() => Lookup("Profile.ButtonHover");
            public static string Title() => Lookup("Profile.Title");
            public static string EnableProfile() => Lookup("Profile.EnableProfile");
            public static string PersonalityTraits() => Lookup("Profile.PersonalityTraits");
            public static string FarmSpecialty() => Lookup("Profile.FarmSpecialty");
            public static string SocialStyle() => Lookup("Profile.SocialStyle");
            public static string SexualOrientation() => Lookup("Profile.SexualOrientation");
            public static string RomanceSafetyMode() => Lookup("Profile.RomanceSafetyMode");
            public static string CustomBio() => Lookup("Profile.CustomBio");
            public static string TraitsMaxReached() => Lookup("Profile.TraitsMaxReached");
            public static string SaveButton() => Lookup("Profile.SaveButton");
            public static string SaveSuccess() => Lookup("Profile.SaveSuccess");
            public static string CloseButton() => Lookup("Profile.CloseButton");
            public static string SelectOption() => Lookup("Profile.SelectOption");
            public static string SafetyStrict() => Lookup("Profile.SafetyStrict");
            public static string SafetyModerate() => Lookup("Profile.SafetyModerate");
            public static string SafetyLoose() => Lookup("Profile.SafetyLoose");
            public static string SafetyOff() => Lookup("Profile.SafetyOff");
            public static string SafetyDescription() => Lookup("Profile.SafetyDescription");
        }

        public static class AdvancedSettings
        {
            public static string ButtonHover() => Lookup("AdvancedSettings.ButtonHover");
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