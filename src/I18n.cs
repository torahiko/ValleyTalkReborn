using System;
using System.Collections.Generic;
using StardewValley;

namespace ValleytalkReborn
{
    public static class I18n
    {
        private static readonly Dictionary<string, string> English = Load("default");
        private static Dictionary<string, string> _locale = English;
        private static string _localeName = string.Empty;

        private static void EnsureLocale()
        {
            var code = LocalizedContentManager.CurrentLanguageCode;
            var name = code == LocalizedContentManager.LanguageCode.en ? "en" : "zh";
            if (_localeName == name && _locale != null) return;

            _localeName = name;
            _locale = name == "en" ? English : Load(name);
        }

        private static string Lookup(string key)
        {
            EnsureLocale();
            if (_locale.TryGetValue(key, out var v) && v != null) return v;
            if (English.TryGetValue(key, out v) && v != null) return v;
            return key;
        }

        private static Dictionary<string, string> Load(string locale)
        {
            if (ModEntry.SHelper?.Data == null) return new Dictionary<string, string>();
            var candidates = locale == "en"
                ? new[] { "i18n/default.json" }
                : new[] { $"i18n/{locale}.json", "i18n/zh-CN.json", "i18n/zh.json" };
            foreach (var path in candidates)
            {
                try
                {
                    var result = ModEntry.SHelper.Data.ReadJsonFile<Dictionary<string, string>>(path);
                    if (result != null && result.Count > 0) return result;
                }
                catch { }
            }
            return new Dictionary<string, string>();
        }

        public static string MemoryCharacterLimit()
        {
            var maxLen = MemoryManager.Instance.GetMaxMemoryLength();
            return Lookup("Memory.CharacterLimit").Replace("{{value}}", maxLen.ToString());
        }

        public static class Memory
        {
            public static string Title(string name, int count)
                => Lookup("Memory.Title")
                    .Replace("{{0}}", name ?? string.Empty)
                    .Replace("{{1}}", count.ToString())
                    .Replace("{{Name}}", name ?? string.Empty)
                    .Replace("{{Count}}", count.ToString());

            public static string Empty() => Lookup("Memory.Empty");

            public static string AddButton() => Lookup("Memory.AddButton");

            public static string EditTitle(string name)
                => Lookup("Memory.EditTitle")
                    .Replace("{{0}}", name ?? string.Empty)
                    .Replace("{{Name}}", name ?? string.Empty);

            public static string EditButtonHover()
                => Lookup("Memory.EditButtonHover");

            public static string DeleteButtonHover()
                => Lookup("Memory.DeleteButtonHover");

            public static string AddTitle(string name)
                => Lookup("Memory.AddTitle")
                    .Replace("{{0}}", name ?? string.Empty)
                    .Replace("{{Name}}", name ?? string.Empty);

            public static string AddHint() => Lookup("Memory.AddHint");

            public static string AddSuccess() => Lookup("Memory.AddSuccess");

            public static string AddFailedFull(int max)
                => Lookup("Memory.AddFailedFull")
                    .Replace("{{0}}", max.ToString())
                    .Replace("{{value}}", max.ToString())
                    .Replace("{{Max}}", max.ToString());

            public static string AddFailedDuplicate() => Lookup("Memory.AddFailedDuplicate");

            public static string AddFailedTooLong(int max)
                => Lookup("Memory.AddFailedTooLong")
                    .Replace("{{0}}", max.ToString())
                    .Replace("{{value}}", max.ToString())
                    .Replace("{{Max}}", max.ToString());

            public static string DeleteConfirm(string content)
                => Lookup("Memory.DeleteConfirm")
                    .Replace("{{0}}", content ?? string.Empty)
                    .Replace("{{value}}", content ?? string.Empty)
                    .Replace("{{Content}}", content ?? string.Empty);

            public static string DeleteSuccess() => Lookup("Memory.DeleteSuccess");

            public static string ButtonHover(string name)
                => Lookup("Memory.ButtonHover")
                    .Replace("{{0}}", name ?? string.Empty)
                    .Replace("{{Name}}", name ?? string.Empty);

            public static string CloseButton() => Lookup("Memory.CloseButton");

            public static string CancelButton() => Lookup("Memory.CancelButton");

            public static string OKButton() => Lookup("Memory.OKButton");

            public static string CharacterLimit(int max)
                => Lookup("Memory.CharacterLimit")
                    .Replace("{{0}}", max.ToString())
                    .Replace("{{value}}", max.ToString())
                    .Replace("{{Max}}", max.ToString());
        }

        public static class DialogueInput
        {
            public static string DefaultTitle() => Lookup("DialogueInput.DefaultTitle");

            public static string Instruction() => Lookup("DialogueInput.Instruction");

            public static string ClearHistoryHover(string npcName)
                => Lookup("DialogueInput.ClearHistoryHover")
                    .Replace("{{0}}", npcName ?? string.Empty)
                    .Replace("{{Name}}", npcName ?? string.Empty);

            public static string ViewHistoryHover(string npcName)
                => Lookup("DialogueInput.ViewHistoryHover")
                    .Replace("{{0}}", npcName ?? string.Empty)
                    .Replace("{{Name}}", npcName ?? string.Empty);

            public static string ClearAllConfirm()
                => Lookup("DialogueInput.ClearAllConfirm");

            public static string ClearOneConfirm(string npcName)
                => Lookup("DialogueInput.ClearOneConfirm")
                    .Replace("{{0}}", npcName ?? string.Empty)
                    .Replace("{{Name}}", npcName ?? string.Empty);

            public static string HistoryTitle(string npcName)
                => Lookup("DialogueInput.HistoryTitle")
                    .Replace("{{0}}", npcName ?? string.Empty)
                    .Replace("{{Name}}", npcName ?? string.Empty);

            public static string HistoryEmpty(string npcName)
                => Lookup("DialogueInput.HistoryEmpty")
                    .Replace("{{0}}", npcName ?? string.Empty)
                    .Replace("{{Name}}", npcName ?? string.Empty);
        }

        public static class Profile
        {
            public static string ButtonHover()
                => Lookup("Profile.ButtonHover");

            public static string Title()
                => Lookup("Profile.Title");

            public static string EnableProfile()
                => Lookup("Profile.EnableProfile");

            public static string PersonalityTraits()
                => Lookup("Profile.PersonalityTraits");

            public static string FarmSpecialty()
                => Lookup("Profile.FarmSpecialty");

            public static string SocialStyle()
                => Lookup("Profile.SocialStyle");

            public static string SexualOrientation()
                => Lookup("Profile.SexualOrientation");

            public static string RomanceSafetyMode()
                => Lookup("Profile.RomanceSafetyMode");

            public static string CustomBio()
                => Lookup("Profile.CustomBio");

            public static string TraitsMaxReached()
                => Lookup("Profile.TraitsMaxReached");

            public static string SaveButton()
                => Lookup("Profile.SaveButton");

            public static string SaveSuccess()
                => Lookup("Profile.SaveSuccess");

            public static string CloseButton()
                => Lookup("Profile.CloseButton");

            public static string SelectOption()
                => Lookup("Profile.SelectOption");

            public static string SafetyStrict()
                => Lookup("Profile.SafetyStrict");

            public static string SafetyModerate()
                => Lookup("Profile.SafetyModerate");

            public static string SafetyLoose()
                => Lookup("Profile.SafetyLoose");

            public static string SafetyOff()
                => Lookup("Profile.SafetyOff");

            public static string SafetyDescription()
                => Lookup("Profile.SafetyDescription");
        }
    }
}