// SocialGraphService.cs
// VT-SOCIAL-01-Phase1-ShadowGraph — 高维寄生影子层（Shadow Observer）的服务入口。
// ModData 单一数据源（ValleytalkReborn.Social.{npcName}），零 friendshipData 写入、
// 零 Harmony、零静态可变缓存（仅持有不可变 JsonSerializerOptions）。
// 正向锚定（Positive Focus）：lens 只描述 NPC 能做什么/如何回应，不做禁止式空话。

using System;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using StardewValley;

namespace ValleytalkReborn.Social;

public sealed class SocialGraphService
{
    public static SocialGraphService Instance { get; } = new();

    public const string ModDataPrefix = "ValleytalkReborn.Social.";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Converters = { new JsonStringEnumConverter() }
    };

    private SocialGraphService()
    {
    }

    /// <summary>
    /// 读取 NPC 心理档案；键缺失/空白时按原版心级冷启动播种并写回 ModData。
    /// </summary>
    public SocialProfile GetProfile(Farmer farmer, string npcName, int vanillaHearts = 0, bool isMarried = false)
    {
        if (farmer == null)
        {
            Log.Error("[SocialGraphService] GetProfile called with null farmer (SaveLoaded 未完成?) — 返回安全回退档案。");
            return SocialProfile.CreateDefault(vanillaHearts, isMarried);
        }
        if (string.IsNullOrWhiteSpace(npcName))
        {
            Log.Debug("[SocialGraphService] GetProfile called with empty npcName — 返回未绑定默认档案。");
            return SocialProfile.CreateDefault();
        }

        string key = ModDataPrefix + npcName;
        if (!farmer.modData.TryGetValue(key, out string raw) || string.IsNullOrWhiteSpace(raw))
        {
            var seeded = SocialProfile.CreateDefault(vanillaHearts, isMarried);
            farmer.modData[key] = JsonSerializer.Serialize(seeded, JsonOptions);
            return seeded;
        }

        try
        {
            var profile = JsonSerializer.Deserialize<SocialProfile>(raw, JsonOptions);
            if (profile != null)
            {
                return profile;
            }
            Log.Warning($"[SocialGraphService] Null social profile payload for '{npcName}' — 视为损坏，重置为默认档案并写回修复。");
        }
        catch (JsonException ex)
        {
            Log.Warning($"[SocialGraphService] Corrupt social profile for '{npcName}' ({ex.Message}) — 重置为默认档案并写回修复。");
        }

        var repaired = SocialProfile.CreateDefault(vanillaHearts, isMarried);
        farmer.modData[key] = JsonSerializer.Serialize(repaired, JsonOptions);
        return repaired;
    }

    /// <summary>
    /// 将档案序列化为紧凑 JSON 写入 farmer.modData[ModDataPrefix + npcName]。
    /// </summary>
    public void SaveProfile(Farmer farmer, string npcName, SocialProfile profile)
    {
        if (farmer == null) throw new ArgumentNullException(nameof(farmer));
        if (string.IsNullOrWhiteSpace(npcName)) throw new ArgumentException("npcName is required", nameof(npcName));
        if (profile == null) throw new ArgumentNullException(nameof(profile));
        farmer.modData[ModDataPrefix + npcName] = JsonSerializer.Serialize(profile, JsonOptions);
    }

    /// <summary>
    /// 九宫格判定：婚后 UnresolvedFriction > DomesticDistance > 默认和谐；
    /// 未婚按 Affection × Trust 阈值交叉判定。
    /// </summary>
    public SocialArchetype EvaluateArchetype(SocialProfile profile, bool isMarried)
    {
        if (isMarried)
        {
            if (profile.UnresolvedFriction >= 40) return SocialArchetype.DomesticColdSpell;
            if (profile.DomesticDistance >= 50) return SocialArchetype.DomesticRoommate;
            return SocialArchetype.DomesticHarmonious;
        }
        if (profile.Affection >= 60 && profile.Trust >= 60) return SocialArchetype.CloseConfidant;
        if (profile.Affection >= 50 && profile.Trust < 35) return SocialArchetype.GuardedAcquaintance;
        if (profile.Affection < 40 && profile.Trust >= 55) return SocialArchetype.ReluctantConfidant;
        return SocialArchetype.Stranger;
    }

    /// <summary>
    /// 按 Positive Focus 原则产出 &lt;relationship_lens&gt; 提示契约块（zh/en 双语）。
    /// </summary>
    public string CompileAttitudeLens(string npcName, SocialProfile profile, bool isZh, bool isMarried)
    {
        SocialArchetype archetype = EvaluateArchetype(profile, isMarried);
        var sb = new StringBuilder();
        sb.AppendLine("<relationship_lens>");
        sb.AppendLine(isZh
            ? $"对象：{npcName}｜好感 {profile.Affection}/100｜信任 {profile.Trust}/100｜张力 {profile.Tension}"
            : $"Target: {npcName} | Affection {profile.Affection}/100 | Trust {profile.Trust}/100 | Tension {profile.Tension}");
        if (isMarried)
        {
            sb.AppendLine(isZh
                ? $"婚后指标｜同居疏离 {profile.DomesticDistance}/100｜未解摩擦 {profile.UnresolvedFriction}/100"
                : $"Domestic metrics | Distance {profile.DomesticDistance}/100 | Unresolved friction {profile.UnresolvedFriction}/100");
        }

        var (anchor, domain, register, contract) = LensBody(archetype, isZh);
        sb.AppendLine(isZh
            ? $"- 视角锚点（你对农夫此人的心理认知与信任防线，独立于即时情绪）：{anchor}"
            : $"- Perspective anchor (your psychological read of the farmer and trust line, independent of the current mood): {anchor}");
        sb.AppendLine(isZh
            ? $"- 信息域（允许交流的话题深浅度）：{domain}"
            : $"- Information domain (how deep topics may go): {domain}");
        sb.AppendLine(isZh
            ? $"- 语气温度：{register}"
            : $"- Register: {register}");
        sb.AppendLine(isZh
            ? $"- 行为契约：{contract}"
            : $"- Behavior contract: {contract}");
        sb.Append("</relationship_lens>");
        return sb.ToString();
    }

    private static (string Anchor, string Domain, string Register, string Contract) LensBody(
        SocialArchetype archetype, bool isZh)
    {
        return archetype switch
        {
            SocialArchetype.Stranger => isZh
                ? ("对农夫保持礼貌而疏离的初识印象",
                   "仅限问候、天气与农忙等表面寒暄",
                   "客套",
                   "以礼貌回应自然接住话头，待信任升温再逐步深入")
                : ("a polite but distant first-acquaintance impression of the farmer",
                   "greetings, weather and farm-busy small talk only",
                   "courteous",
                   "reciprocate politely and let trust warm up before going deeper"),
            SocialArchetype.GuardedAcquaintance => isZh
                ? ("谈吐随和、礼貌熟络，但交情止于表面",
                   "轻松社交话题（镇上见闻、日常琐事）",
                   "随和",
                   "涉及隐私话题时自然转移话题，既不冷场也不交底")
                : ("easygoing and politely familiar, but the bond stays surface-level",
                   "light social topics (town news, daily trifles)",
                   "easygoing",
                   "when privacy comes up, steer the topic away gracefully — neither awkward silence nor confession"),
            SocialArchetype.ReluctantConfidant => isZh
                ? ("言语直接带刺、嘴硬心软，内心已把农夫当作可托付之人",
                   "知晓农夫一贯守信，私事与心事均可言说",
                   "直接带刺但可信",
                   "主动作出真实诉求，把真正的难处托付出去")
                : ("blunt and prickly in words, soft underneath — already trusting the farmer",
                   "knows the farmer keeps promises; personal matters and worries may surface",
                   "blunt but trustworthy",
                   "volunteer a real request and hand over a genuine burden"),
            SocialArchetype.CloseConfidant => isZh
                ? ("莫逆知己，彼此深知、无需客套",
                   "隐私痛点与真心话均可交流",
                   "温存",
                   "主动提起共同回忆并直陈担忧")
                : ("a trusted confidant — deeply known, no pleasantries needed",
                   "private pains and honest words are all welcome",
                   "warm",
                   "bring up shared memories and voice concerns directly"),
            SocialArchetype.DomesticHarmonious => isZh
                ? ("深层默契与家庭温情，婚后依旧彼此在意",
                   "家庭农务与共同生活安排",
                   "温存",
                   "以体贴的家务协作巩固亲密")
                : ("deep tacit understanding and family warmth that endures after marriage",
                   "household farming and shared life arrangements",
                   "warm",
                   "deepen intimacy through considerate cooperation on chores"),
            SocialArchetype.DomesticColdSpell => isZh
                ? ("同居夫妻间的单日冷战与心寒委屈",
                   "农务家务等必要事务",
                   "冷淡",
                   "保持冷淡防备，真诚道歉后可能出现解冻")
                : ("a one-day cold spell between cohabiting spouses with lingering hurt",
                   "necessary farm and household matters only",
                   "cold",
                   "stay cold and guarded; a sincere apology may begin to thaw"),
            _ => isZh
                ? ("同住一个屋檐下，却已室友化的疏离",
                   "事务性交流（家务分工、日常安排）",
                   "冷淡偏事务",
                   "维持井然有序的共同生活，回避亲密话题")
                : ("living under one roof, but grown roommate-distant",
                   "transactional exchange (chore split, daily schedules)",
                   "cool and matter-of-fact",
                   "keep the shared life orderly and steer off intimate topics"),
        };
    }
}
