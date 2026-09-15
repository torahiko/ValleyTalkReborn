using System;
using System.Collections.Generic;
using System.Linq;
using StardewModdingAPI;
using StardewValley;

namespace ValleytalkReborn;

/// <summary>
/// 通用的"阶段性状态"解析器：根据当前婚姻状态、好感度、巴士修复进度与覆盖池配置，
/// 从角色卡的 BioData.ProgressStates 中选出唯一生效的一档（ProgressStateEntry）。
/// 供 Bark 路径（ResolveActiveEntry / 整档）与 Legacy 文本路径（ResolveActiveState / 仅 Text）统一调用，
/// 避免任何针对具体角色名的硬编码判断散落在各处。
///
/// 所有方法均为 Memory 只读判定，无状态、无缓存写入。
/// 调用方需保证在 SaveLoaded/DayStarted 之后调用（方能安全访问 Game1.player / Game1.MasterPlayer）。
/// </summary>
internal static class ProgressStateResolver
{
    // ── 内容谓词（决定性定义） ──
    // Entry 级（Bark 路径）：档内任一"可注入内容"非空即视为候选。
    private static bool EntryContentFilter(BioData.ProgressStateEntry entry)
        => !string.IsNullOrWhiteSpace(entry.Text)
        || !string.IsNullOrWhiteSpace(entry.BarkMindset)
        || (entry.Preoccupations != null && entry.Preoccupations.Count > 0);

    // Text 级（Legacy 路径）：仅看 Text，与改造前 ResolveActiveState 语义逐字节一致。
    private static bool TextContentFilter(BioData.ProgressStateEntry entry)
        => !string.IsNullOrWhiteSpace(entry.Text);

    // ── 特异性评分常量（约定值，勿随意改动） ──
    private const int ScoreRequirePlayerMarriedTo = 1000;   // 指定配偶 ID 的档位绝对优先
    private const int ScoreRequireMarried = 500;            // 泛婚姻档位次之
    private const int ScoreRequireBusRepaired = 200;        // 巴士修复档位
    private const int ScoreRequireJojaMartClosed = 200;     // Joja 倒闭档位（社区中心线完成）
    private const int ScoreRequireJojaMember = 200;         // Joja 会员档位
    private const int ScorePerHeart = 10;                  // 每心附加分（同分保持卡内声明顺序）

    /// <summary>
    /// 解析当前生效的完整档位（含 Text / BarkMindset / Preoccupations）。
    /// 供 Bark 路径使用。未命中任何档位时返回 null。
    /// </summary>
    /// <param name="npc">目标 NPC。</param>
    /// <param name="states">该角色卡配置的档位列表（BioData.ProgressStates）。</param>
    internal static BioData.ProgressStateEntry ResolveActiveEntry(NPC npc, List<BioData.ProgressStateEntry> states)
        => ResolveCore(npc, states, EntryContentFilter);

    /// <summary>
    /// 解析当前应注入的阶段性状态文本（Legacy 契约，行为保持与改造前逐字节一致）。
    /// 供 A2APromptBuilder / Prompts 使用。未配置或未命中任何档位时返回 null。
    /// </summary>
    /// <param name="npc">目标 NPC。</param>
    /// <param name="states">该角色卡配置的档位列表（BioData.ProgressStates）。</param>
    internal static string ResolveActiveState(NPC npc, List<BioData.ProgressStateEntry> states)
        => ResolveCore(npc, states, TextContentFilter)?.Text;

    /// <summary>
    /// 核心过滤 + 排序管线。按以下固定顺序逐步剔除不满足条件的档位，
    /// 最后按特异性评分（高→低）→ 心数门槛（高→低）取首个命中档。
    /// 同分同门槛由 LINQ 稳定排序保持卡内声明顺序，保证确定性。
    /// </summary>
    private static BioData.ProgressStateEntry ResolveCore(NPC npc, List<BioData.ProgressStateEntry> states,
                                                          Func<BioData.ProgressStateEntry, bool> contentFilter)
    {
        if (npc == null || states == null || states.Count == 0)
            return null;

        bool isSelfMarried = IsMarriedToPlayer(npc.Name);
        int hearts = GetHearts(npc);
        bool isBusRepaired = CheckBusRepaired();
        bool isJojaClosed = CheckJojaMartClosed();
        bool isJojaMember = CheckJojaMember();

        // 3. 过滤候选（固定顺序）：null 条目 → 心数门槛 → 婚姻 → 巴士 → Joja 状态 → 指定配偶 → 内容谓词
        var candidates = states
            .Where(s => s != null)
            .Where(s => hearts >= s.RequiredHearts)
            .Where(s => !s.RequireMarried || isSelfMarried)
            .Where(s => !s.RequireBusRepaired.HasValue || s.RequireBusRepaired.Value == isBusRepaired)
            .Where(s => !s.RequireJojaMartClosed.HasValue || s.RequireJojaMartClosed.Value == isJojaClosed)
            .Where(s => !s.RequireJojaMember.HasValue || s.RequireJojaMember.Value == isJojaMember)
            .Where(s => string.IsNullOrWhiteSpace(s.RequirePlayerMarriedTo) || IsMarriedToPlayer(s.RequirePlayerMarriedTo))
            .Where(contentFilter);

        // 5. OrderByDescending(特异性评分).ThenByDescending(心数) 取首个（稳定排序保证确定性）
        return candidates
            .OrderByDescending(s => CalculateSpecificityScore(s))
            .ThenByDescending(s => s.RequiredHearts)
            .FirstOrDefault();
    }

    /// <summary>
    /// 计算档位的特异性评分：条件越"窄"分数越高，窄条件自然优先宽条件。
    /// </summary>
    private static int CalculateSpecificityScore(BioData.ProgressStateEntry entry)
    {
        if (entry == null) return 0;

        int score = 0;
        if (!string.IsNullOrWhiteSpace(entry.RequirePlayerMarriedTo)) score += ScoreRequirePlayerMarriedTo;
        if (entry.RequireMarried) score += ScoreRequireMarried;
        if (entry.RequireBusRepaired.HasValue) score += ScoreRequireBusRepaired;
        if (entry.RequireJojaMartClosed.HasValue) score += ScoreRequireJojaMartClosed;
        if (entry.RequireJojaMember.HasValue) score += ScoreRequireJojaMember;
        score += entry.RequiredHearts * ScorePerHeart;
        return score;
    }

    /// <summary>
    /// 判断该 NPC 是否与玩家已婚——综合法定配偶、多角恋 mod 正式配偶、
    /// 以及原版存档关系，任一为真即视为已婚。
    /// （注意：非正式恋人 UnofficialSpouse 不计入已婚，避免跳过未婚恋爱期）
    /// </summary>
    internal static bool IsMarriedToPlayer(string npcName)
    {
        if (string.IsNullOrWhiteSpace(npcName))
            return false;

        // 法定配偶（委托 SpouseQueryService.IsMarried，语义=当前玩家配偶）
        if (CompanionScheduleManager.IsLegalSpouse(npcName))
            return true;

        // 多角恋 mod 正式配偶
        var character = Game1.getCharacterFromName(npcName);
        if (character != null && PolyamorySweetLoveBridge.IsOfficialSpouse(character))
            return true;

        // 原版存档关系检查
        var player = Game1.player;
        if (player?.friendshipData != null
            && player.friendshipData.TryGetValue(npcName, out var fs)
            && fs != null
            && fs.IsMarried())
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// 获取玩家对该 NPC 的好感度心数。无数据时返回 0。
    /// </summary>
    private static int GetHearts(NPC npc)
    {
        if (npc == null) return 0;

        var player = Game1.player;
        if (player?.friendshipData == null) return 0;

        if (!player.friendshipData.TryGetValue(npc.Name, out var fs) || fs == null)
            return 0;

        return fs.Points / 250;
    }

    /// <summary>
    /// 判定社区巴士是否已修复（Pam 的巴士恢复运营）。
    /// 判定域 = Host 世界旗标（金库献祭 ccVault / Joja 路线 jojaVault），取自 Game1.MasterPlayer.mailReceived。
    /// mailReceived.Contains 为大小写敏感，故并检 jojaVault 与 JojaVault 两种大小写（两种路线/版本写法皆有可能）。
    ///
    /// 语义边界：bus 为世界级（MasterPlayer），marriage/hearts 为本地玩家级（Game1.player）——
    /// 二者刻意不同，禁止"统一"。
    /// 任何异常 → Trace 日志 + false（Pam 落入"停运档"，安全降级）。
    /// </summary>
    internal static bool CheckBusRepaired()
    {
        try
        {
            var mail = Game1.MasterPlayer?.mailReceived;
            if (mail == null) return false;
            return mail.Contains("ccVault") || mail.Contains("jojaVault") || mail.Contains("JojaVault");
        }
        catch (Exception ex)
        {
            ModEntry.SMonitor?.Log($"[ProgressStateResolver] CheckBusRepaired 判定异常: {ex.Message}", LogLevel.Trace);
            return false;
        }
    }

    /// <summary>
    /// 判定 Joja 超市是否已倒闭（社区中心线完成，ccIsComplete 旗标）。
    /// 判定域 = Host 世界旗标，取自 Game1.MasterPlayer.mailReceived（与世界级 bus 同域）。
    /// mailReceived.Contains 为大小写敏感，故并检 "ccIsComplete"。
    /// 任何异常 → Trace 日志 + false（落入中性阶梯，安全降级）。
    /// </summary>
    internal static bool CheckJojaMartClosed()
    {
        try
        {
            var mail = Game1.MasterPlayer?.mailReceived;
            if (mail == null) return false;
            return mail.Contains("ccIsComplete");
        }
        catch (Exception ex)
        {
            ModEntry.SMonitor?.Log($"[ProgressStateResolver] CheckJojaMartClosed 异常: {ex.Message}", LogLevel.Trace);
            return false;
        }
    }

    /// <summary>
    /// 判定当前本地玩家是否已加入 Joja 会员（JojaMember 旗标）。
    /// 判定域 = 本地玩家旗标，取自 Game1.player.mailReceived（与婚姻/心数同域——RESILIENT FRIENDSHIP 档的关系主体是眼前农夫）。
    /// mailReceived.Contains 为大小写敏感。
    /// 原版 ccIsComplete 与 JojaMember 两旗标进程互斥；引擎不设特化守卫，双旗标并存时解析仍确定。
    /// 任何异常 → Trace 日志 + false。
    /// </summary>
    internal static bool CheckJojaMember()
    {
        try
        {
            var player = Game1.player;
            return player?.mailReceived?.Contains("JojaMember") == true;
        }
        catch (Exception ex)
        {
            ModEntry.SMonitor?.Log($"[ProgressStateResolver] CheckJojaMember 异常: {ex.Message}", LogLevel.Trace);
            return false;
        }
    }
}
