using StardewModdingAPI;

namespace ValleyTalk
{
    /// <summary>
    /// 国际化辅助类，提供静态方法获取翻译文本。
    /// </summary>
    public static class I18n
    {
        /// <summary>
        /// 获取翻译文本，支持占位符参数。
        /// </summary>
        /// <param name="key">翻译键名</param>
        /// <param name="args">占位符参数</param>
        /// <returns>翻译后的文本</returns>
        public static string Memory(string key, params object[] args)
        {
            string text = ModEntry.SHelper.Translation.Get($"Memory.{key}");
            if (args != null && args.Length > 0)
            {
                return string.Format(text, args);
            }
            return text;
        }

        /// <summary>
        /// 获取当前语言的记忆最大长度提示。
        /// </summary>
        public static string MemoryCharacterLimit()
        {
            int maxLen = MemoryManager.Instance.GetMaxMemoryLength();
            return Memory("CharacterLimit", maxLen);
        }
    }
}
