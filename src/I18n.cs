using System;
using System.Collections.Generic;
using StardewValley;

namespace ValleyTalk
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
    }
}