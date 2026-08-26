using System;
using System.Collections.Generic;
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
            // 优先使用 SMAPI 翻译机制（如果可用）
            try
            {
                if (ModEntry.SHelper?.Translation != null)
                {
                    var translation = ModEntry.SHelper.Translation.Get(key);
                    if (translation.HasValue()) return translation.ToString();
                }
            }
            catch { }

            // 本地字典回退
            EnsureLocale();
            if (_locale != null && _locale.TryGetValue(key, out var v) && !string.IsNullOrEmpty(v)) return v;
            if (_english != null && _english.TryGetValue(key, out v) && !string.IsNullOrEmpty(v)) return v;
            
            return key;
        }

        private static Dictionary<string, string> Load(string locale)
        {
            if (ModEntry.SHelper?.Data == null) return new Dictionary<string, string>();

            List<string> candidates = new List<string>();
            if (locale == "default" || locale == "en")
            {
                candidates.Add("i18n/default.json");
            }
            else if (locale == "zh")
            {
                candidates.Add("i18n/zh.json");
                candidates.Add("i18n/zh-CN.json");
            }
            else
            {
                candidates.Add($"i18n/{locale}.json");
            }

            foreach (var path in candidates)
            {
                try
                {
                    var result = ModEntry.SHelper.Data.ReadJsonFile<Dictionary<string, string>>(path);
                    if (result != null && result.Count > 0) return result;
                }
                catch { }
            }

            // 小语种找不到文件时回退到英文
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
            public static string AddHint() => Lookup("Memory.AddHint");
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
    }
}