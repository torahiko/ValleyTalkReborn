using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Xna.Framework;
using StardewModdingAPI;
using StardewValley;

namespace ValleytalkReborn
{
    /// <summary>
    /// ValleyTalk 通用工具类
    /// </summary>
    public static class Util
    {
        private static ITranslationHelper TranslationHelper => ModEntry.SHelper?.Translation;

        /// <summary>
        /// 获取指定 NPC 附近 4.5 格范围内的其他 NPC
        /// </summary>
        public static IEnumerable<NPC> GetNearbyNpcs(NPC npc)
        {
            if (npc == null || Game1.currentLocation?.characters == null)
                return Enumerable.Empty<NPC>();

            Vector2 speakerLocation = npc.Tile;
            string speakerName = npc.Name;

            // 使用 DistanceSquared (4.5 的平方是 20.25) 避免开方运算，大幅提升性能
            const float maxDistanceSquared = 20.25f;

            var nearbyNpcs = new List<NPC>();
            foreach (var otherNpc in Game1.currentLocation.characters)
            {
                if (otherNpc == null || otherNpc.Name == speakerName || !otherNpc.CanReceiveGifts())
                    continue;

                if (Vector2.DistanceSquared(speakerLocation, otherNpc.Tile) < maxDistanceSquared)
                {
                    nearbyNpcs.Add(otherNpc);
                }
            }

            return nearbyNpcs;
        }

        /// <summary>
        /// 拼接字符串列表（如：张三, 李四 和 王五）
        /// </summary>
        internal static string ConcatAnd(IReadOnlyList<string> strings)
        {
            if (strings == null || strings.Count == 0)
                return string.Empty;

            if (strings.Count == 1)
                return strings[0];

            var builder = new System.Text.StringBuilder();
            for (int i = 0; i < strings.Count; i++)
            {
                if (i == strings.Count - 1)
                {
                    // 格式化连接词，前后空格由语言包内部决定（中文无需空格，英文自带空格）
                    builder.Append(GetString("generalAnd")).Append(strings[i]);
                }
                else
                {
                    builder.Append(strings[i]);
                    if (i < strings.Count - 2)
                    {
                        builder.Append(", ");
                    }
                }
            }
            return builder.ToString();
        }

        /// <summary>
        /// 获取特定 NPC 的本地化或缓存字符串
        /// </summary>
        internal static string GetString(Character npc, string key, object tokens = null, bool returnNull = false)
        {
            if (npc == null || string.IsNullOrEmpty(key)) 
                return returnNull ? null : string.Empty;

            // 1. 优先读取 NPC 独特的 Prompt 覆盖
            if (npc.Bio.PromptOverrides.TryGetValue(key, out string overrideVal))
            {
                return ReplaceTokens(overrideVal, tokens);
            }

            // 2. 根据性别在 Cache 中寻找
            string result = null;
            if (npc.Bio.IsMale == true)
            {
                PromptCache.Instance.Cache.TryGetValue($"{key}.MaleNpc", out result);
            }
            else if (npc.Bio.IsMale == false)
            {
                PromptCache.Instance.Cache.TryGetValue($"{key}.FemaleNpc", out result);
            }

            // 3. 通用 Cache 查找
            if (result == null)
            {
                PromptCache.Instance.Cache.TryGetValue(key, out result);
            }

            // 4. Cache 未命中，尝试从 SMAPI 本地化 i18n 系统读取（SMAPI 原生支持 Token 替换）
            if (result == null && TranslationHelper != null)
            {
                var translation = TranslationHelper.Get(key, tokens);
                if (translation.HasValue())
                {
                    return translation.ToString();
                }
            }

            if (result == null)
            {
                return returnNull ? null : string.Empty;
            }

            return ReplaceTokens(result, tokens);
        }

        /// <summary>
        /// 获取通用的本地化或缓存字符串
        /// </summary>
        internal static string GetString(string key, object tokens = null, bool returnNull = false)
        {
            if (string.IsNullOrEmpty(key)) 
                return returnNull ? null : string.Empty;

            // 1. 优先从 PromptCache 中查找
            if (PromptCache.Instance.Cache.TryGetValue(key, out string result))
            {
                return ReplaceTokens(result, tokens);
            }

            // 2. 未命中则从 SMAPI i18n 语言包中查找
            if (TranslationHelper != null)
            {
                var translation = TranslationHelper.Get(key, tokens);
                if (translation.HasValue())
                {
                    return translation.ToString();
                }
            }

            return returnNull ? null : string.Empty;
        }

        /// <summary>
        /// 读取多语言 JSON 文件
        /// </summary>
        internal static T ReadLocalisedJson<T>(string basePath, string extension = "json") where T : class
        {
            if (ModEntry.LanguageFileSuffixes == null || ModEntry.SHelper?.Data == null)
                return default;

            foreach (var langSuffix in ModEntry.LanguageFileSuffixes)
            {
                var path = $"{basePath}{langSuffix}.{extension}";
                var result = ModEntry.SHelper.Data.ReadJsonFile<T>(path);
                if (result != null)
                {
                    return result;
                }
            }

            return default;
        }

        /// <summary>
        /// 内部 Token 替换助手（用于处理自定义 Cache 的文本，SMAPI 的文本已被 TranslationHelper 自动处理）
        /// </summary>
        private static string ReplaceTokens(string template, object tokens)
        {
            if (string.IsNullOrEmpty(template) || tokens == null) 
                return template;

            foreach (var prop in tokens.GetType().GetProperties())
            {
                var value = prop.GetValue(tokens)?.ToString();
                if (value != null)
                {
                    template = template.Replace("{{" + prop.Name + "}}", value);
                }
            }

            return template;
        }
    }
}