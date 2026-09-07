using System;
using System.Collections.Generic;
using StardewValley;

namespace ValleytalkReborn;

public class BioData
{
    // 更改为动态/延迟获取翻译，防止在 SMAPI 初始化未完成时触发静态构造函数崩溃
    private static string Male => ModEntry.SHelper?.Translation.Get("generalMale") ?? "Male";
    private static string Female => ModEntry.SHelper?.Translation.Get("generalFemale") ?? "Female";
    private static string He => ModEntry.SHelper?.Translation.Get("generalHe") ?? "he";
    private static string She => ModEntry.SHelper?.Translation.Get("generalShe") ?? "she";
    private static string Him => ModEntry.SHelper?.Translation.Get("generalHim") ?? "him";
    private static string Her => ModEntry.SHelper?.Translation.Get("generalHer") ?? "her";
    private static string His => ModEntry.SHelper?.Translation.Get("generalHis") ?? "his";
    private static string Hers => ModEntry.SHelper?.Translation.Get("generalHers") ?? "hers";

    private bool? isMale;
    public bool? IsMale => isMale;

    private string unique;
    private string name = string.Empty;

    public string Name 
    { 
        get => name; 
        set
        {
            name = value;
            // 【Bug 修复】只有当成功获取到 NPC 时才更新性别，避免找不到 NPC 时误判为 Female (false)
            var character = Game1.getCharacterFromName(name);
            if (character != null)
            {
                isMale = character.Gender == StardewValley.Gender.Male;
            }
        } 
    }

    public string Biography { get; set; } = string.Empty;
    public Dictionary<string, ListEntry> Relationships { get; set; } = new Dictionary<string, ListEntry>();
    public Dictionary<string, ListEntry> Traits { get; set; } = new Dictionary<string, ListEntry>();
    public string BiographyEnd { get; set; } = string.Empty;
    
    // Only update gender if the value passed is male or female
    public string Gender 
    {
        get => isMale == null ? null : (isMale.Value ? Male : Female);
        set
        {
            if (value == null)
            {
                isMale = null; 
                return;
            }
            if (value.Equals(Male, StringComparison.OrdinalIgnoreCase) || value.Equals(Female, StringComparison.OrdinalIgnoreCase))
            {
                isMale = value.Equals(Male, StringComparison.OrdinalIgnoreCase);
                return;
            }
            isMale = null;
        }
    }

    public string Unique 
    { 
        get => unique; 
        set 
        {
            unique = value; 
            if (!string.IsNullOrWhiteSpace(value) && ExtraPortraits != null && !ExtraPortraits.ContainsKey("u"))
            {
                ExtraPortraits["u"] = value;
            }
        }
    }

    public Dictionary<string, string> ExtraPortraits { get; set; } = new Dictionary<string, string>();
    public List<string> Preoccupations { get; set; } = new List<string>();
    public Dictionary<string, string> Dialogue { get; set; } = new Dictionary<string, string>();
    public bool HomeLocationBed { get; set; } = false;

    public string GenderP2 => (isMale ?? false) ? He : She;
    public string GenderPronoun => (isMale ?? false) ? Him : Her;
    public string GenderPossessive => (isMale ?? false) ? His : Hers;

    public Dictionary<string, string> PromptOverrides { get; set; } = new Dictionary<string, string>();
    public bool UsePatchedDialogue { get; set; } = false;
    public bool Missing { get; internal set; }

    // 新增：标记此 NPC 是否为大模型语料中已知的原版角色。
    // true  → Relationships 块整体跳过（大模型自己认识这些人）。
    // false → 全量输出，第三方 NPC 不设此字段时自动降级为 false。
    public bool IsKnownNpc { get; set; } = false;

    // ── 环境自言自语（Ambient Bark）系统 ──
    // 这两个字段仅供 DynamicBarkManager 使用，严禁注入到标准对话上下文（DialogueBuilder / Prompts）。
    public bool EnableAmbientBarks { get; set; } = false;
    public string AmbientBarkPrompt { get; set; } = null;

    // ── 阶段性状态（Progress States）系统 ──
    // 通用的"互斥分档"状态描述：任何角色都可以配置多档状态文本，
    // 按婚姻/好感度自动选取当前生效的那一档，供 Bark / A2A / 主对话共用同一份数据。
    // 例如 Shane 的戒酒/酗酒状态、某角色婚前婚后的语气差异，都不必再写死在代码里，
    // 直接在角色卡里配置即可。解析逻辑见 ProgressStateResolver。
    public List<ProgressStateEntry> ProgressStates { get; set; } = new List<ProgressStateEntry>();

    public class ListEntry
    {
        public string id { get; set; }
        public string Heading { get; set; }
        public string Description { get; set; }
        // 注入此条目所需的最低好感度（心数）。默认 0 = 始终注入。
        public int RequiredHearts { get; set; } = 0;
    }

    /// <summary>
    /// 单个"阶段性状态"档位。多个档位共同构成一个角色的互斥状态阶梯
    /// （例如：戒酒/酗酒、婚前/婚后语气），任意时刻只会有一档命中并被注入。
    /// </summary>
    public class ProgressStateEntry
    {
        /// <summary>
        /// 命中所需的最低好感度（心数）。解析时按此字段从高到低排序，
        /// 取第一个满足条件的档位。默认 0，可作为"兜底档"始终能命中。
        /// </summary>
        public int RequiredHearts { get; set; } = 0;

        /// <summary>
        /// 若为 true，则只要玩家与该角色已婚（法定或非官方配偶）即视为命中此档，
        /// 不再看 RequiredHearts —— 用于"结婚后自动进入某状态"的场景，
        /// 与心数门槛是两种独立判定方式，由 ProgressStateResolver 统一决定优先级。
        /// </summary>
        public bool RequireMarried { get; set; } = false;

        /// <summary>
        /// 命中该档位时注入的状态描述文本，直接用自然语言书写，
        /// 会被追加拼接到角色说话风格描述之后。
        /// </summary>
        public string Text { get; set; } = "";
    }
}