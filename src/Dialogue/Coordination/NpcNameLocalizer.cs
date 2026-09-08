using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using StardewValley;

namespace ValleytalkReborn;

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
    /// 获取 NPC 当前真正生效的中文名字（完美兼容性转 Mod / 自定义更名 Mod）
    /// </summary>
    public static string GetZhName(string npcInternalName)
    {
        if (string.IsNullOrWhiteSpace(npcInternalName)) return "";

        // 1. 优先从游戏引擎中获取 NPC 实体
        var npc = Game1.getCharacterFromName(npcInternalName);
        if (npc != null)
        {
            string currentDisplayName = npc.displayName;

            // 如果被性转 Mod 改成了其他中文名字（例如“阿尔伯特”），且与内部名不同，直接采纳！
            if (!string.IsNullOrWhiteSpace(currentDisplayName) 
                && !string.Equals(currentDisplayName, npcInternalName, StringComparison.OrdinalIgnoreCase))
            {
                return currentDisplayName;
            }
        }

        // 2. 兜底策略：如果游戏里读取到的是原名或英文，则走权威汉化字典
        if (FallbackZhNames.TryGetValue(npcInternalName, out var fallbackName))
            return fallbackName;

        return npcInternalName;
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