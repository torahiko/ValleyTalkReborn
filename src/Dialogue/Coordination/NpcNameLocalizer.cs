using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using StardewValley;
using StardewValley.TokenizableStrings;

namespace ValleytalkReborn.Dialogue.Coordination;

internal static class NpcNameLocalizer
{
    // 官方简中标准译名兜底字典（涵盖原版村民及常用 Mod NPC）
    private static readonly Dictionary<string, string> FallbackZhNames = new(StringComparer.OrdinalIgnoreCase)
    { 
        { "Alex", "亚历克斯" },
        { "Elliott", "艾利欧特" },
        { "Harvey", "哈维" },
        { "Sam", "山姆" },
        { "Sebastian", "塞巴斯蒂安" },
        { "Shane", "谢恩" },
        { "Abigail", "阿比盖尔" },
        { "Emily", "艾米丽" },
        { "Haley", "海莉" },
        { "Leah", "莉亚" },
        { "Maru", "玛鲁" },
        { "Penny", "潘妮" },
        { "Caroline", "卡洛琳" },
        { "Clint", "克林特" },
        { "Demetrius", "德米特里厄斯" },
        { "Evelyn", "艾芙琳" },
        { "George", "乔治" },
        { "Gus", "格斯" },
        { "Jas", "贾斯" },
        { "Jodi", "乔迪" },
        { "Kent", "肯特" },
        { "Krobus", "科罗布斯" },
        { "Leo", "雷欧" },
        { "Lewis", "刘易斯" },
        { "Linus", "莱纳斯" },
        { "Marnie", "玛妮" },
        { "Pam", "潘姆" },
        { "Pierre", "皮埃尔" },
        { "Robin", "罗宾" },
        { "Sandy", "桑迪" },
        { "Vincent", "文森特" },
        { "Willy", "威利" },
        { "Wizard", "法师" },
        { "Marlon", "马龙" },
        { "Gunther", "冈瑟" },
        { "Gil", "吉尔" },
        { "Bouncer", "保镖" },
        { "Mister Qi", "齐先生" },
        { "Morris", "莫里斯" },
        { "Birdie", "贝啼" },
        { "Professor Snail", "蜗牛教授" },
        { "Lance", "兰斯" },
        { "Olivia", "奥利维亚" },
        { "Victor", "维克托" },
        { "Sophia", "索菲亚" },
        { "Andy", "安迪" },
        { "Claire", "克莱尔" },
        { "Susan", "苏珊" },
        { "Martin", "马丁" },
        { "Morgan", "摩根" },
        { "Scarlet", "斯嘉丽" },
        { "Apples", "苹果" }, 
        { "Dusty", "小灰" }, 
        { "Magnus", "马格努斯" } 
};

    /// <summary>
    /// 获取 NPC 当前语言环境下的本地化显示名（活跃实体 → 1.6 资产元数据 → 简中字典 → 内部名）。
    /// </summary>
    public static string GetLocalizedName(string npcInternalName)
    {
        if (string.IsNullOrWhiteSpace(npcInternalName)) return string.Empty;

        bool isZh = LocalizedContentManager.CurrentLanguageCode == LocalizedContentManager.LanguageCode.zh;

        // 1. 活跃实体层：性转 Mod / 自定义更名 Mod 改名后优先采纳实体 displayName
        try
        {
            var npc = Game1.getCharacterFromName(npcInternalName);
            if (npc != null && !string.IsNullOrWhiteSpace(npc.displayName))
            {
                if (isZh)
                {
                    if (!string.Equals(npc.displayName, npcInternalName, StringComparison.OrdinalIgnoreCase))
                        return npc.displayName;
                }
                else
                {
                    return npc.displayName;
                }
            }
        }
        catch (Exception)
        {
            // BOUNDARY: 单测或未加载场景 Game1.getCharacterFromName 可能引发内部 NRE，安全进入下一层
        }

        // 2. 1.6 资产元数据层：经 TokenParser 解析多语言词条
        try
        {
            if (Game1.characterData != null && Game1.characterData.TryGetValue(npcInternalName, out var charData))
            {
                if (!string.IsNullOrWhiteSpace(charData?.DisplayName))
                {
                    string parsed = TokenParser.ParseText(charData.DisplayName);
                    if (!string.IsNullOrWhiteSpace(parsed) && !parsed.StartsWith("[LocalizedText", StringComparison.Ordinal))
                    {
                        if (isZh)
                        {
                            if (!string.Equals(parsed, npcInternalName, StringComparison.OrdinalIgnoreCase))
                                return parsed;
                        }
                        else
                        {
                            return parsed;
                        }
                    }
                }
            }
        }
        catch (Exception)
        {
            // BOUNDARY: 单测或离线环境 TokenParser / Game1.content 未就绪
        }

        // 3. 简中字典兜底层
        if (isZh && FallbackZhNames.TryGetValue(npcInternalName, out var fallbackName))
            return fallbackName;

        // 4. 最终安全兜底
        return npcInternalName;
    }

    /// <summary>
    /// 获取 NPC 当前真正生效的中文名字（兼容别名，走统一 GetLocalizedName 流水线）
    /// </summary>
    public static string GetZhName(string npcInternalName)
    {
        return GetLocalizedName(npcInternalName);
    }

    /// <summary>
    /// 将文本中的英文 NPC 名字安全替换为中文（动态识别性转名）
    /// </summary>
    public static string LocalizeNamesInText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return text;

        foreach (var pair in FallbackZhNames)
        {
            // 获取当前存档中该角色真正使用的名字（性转后为“阿尔伯特”，普通为“阿比盖尔”）
            string activeName = GetZhName(pair.Key);

            // 用词边界精准替换正文中的英文名字
            text = Regex.Replace(text, $@"\b{pair.Key}\b", activeName, RegexOptions.IgnoreCase);
        }

        return text;
    }
}