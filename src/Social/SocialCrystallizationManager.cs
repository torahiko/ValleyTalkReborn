// SocialCrystallizationManager.cs
// VT-SOCIAL-03-Phase3-CrystallizationAndSpouseEvolution — 日结结晶器与配偶张力演进。
// DayEnding：冷落/未交流 → Friction/Distance 累积，陪伴 → 自然解冻；全部落 Farmer.modData，
// 原版 friendshipData.Points 零触碰。DayStarted：冷战态阶跃（进入/冰释）时弹一次沉浸式
// HUDMessage（纯文学文本，零数值暴露）。ReturnedToTitle：清空内存态跨日追踪集合。

using System;
using System.Collections.Generic;
using System.Reflection;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;
using ValleytalkReborn.Dialogue.Coordination;

namespace ValleytalkReborn.Social;

public sealed class SocialCrystallizationManager
{
    public static SocialCrystallizationManager Instance { get; } = new();

    private IModHelper _helper;
    private readonly HashSet<string> _yesterdayColdSpells = new(StringComparer.OrdinalIgnoreCase);

    private SocialCrystallizationManager()
    {
    }

    public void Initialize(IModHelper helper)
    {
        _helper = helper;
        helper.Events.GameLoop.DayEnding += OnDayEnding;
        helper.Events.GameLoop.DayStarted += OnDayStarted;
        helper.Events.GameLoop.ReturnedToTitle += OnReturnedToTitle;
    }

    public void Cleanup()
    {
        if (_helper != null)
        {
            _helper.Events.GameLoop.DayEnding -= OnDayEnding;
            _helper.Events.GameLoop.DayStarted -= OnDayStarted;
            _helper.Events.GameLoop.ReturnedToTitle -= OnReturnedToTitle;
            _helper = null;
        }
        _yesterdayColdSpells.Clear();
    }

    /// <summary>
    /// 核心日结结晶（纯逻辑，无 Game1 依赖）：冷落/未交流累积张力，陪伴自然解冻；
    /// 每次结算后按九宫格重同步 Archetype（单一真理源）。
    /// </summary>
    public static void SettleSpouseDailyMetrics(SocialProfile profile, bool talkedToday, bool hasNeglectShock)
    {
        if (hasNeglectShock || !talkedToday)
        {
            profile.UnresolvedFriction = Math.Clamp(profile.UnresolvedFriction + 12, 0, 100);
            profile.DomesticDistance = Math.Clamp(profile.DomesticDistance + 6, 0, 100);
        }
        else
        {
            profile.UnresolvedFriction = Math.Clamp(profile.UnresolvedFriction - 15, 0, 100);
            profile.DomesticDistance = Math.Clamp(profile.DomesticDistance - 8, 0, 100);
        }
        profile.Archetype = SocialGraphService.Instance.EvaluateArchetype(profile, isMarried: true);
    }

    internal void OnDayEnding(object sender, DayEndingEventArgs e)
    {
        if (!Context.IsWorldReady || Game1.player == null) return;

        Farmer farmer = Game1.player;
        foreach (NPC spouse in SpouseQueryService.Instance.GetAllMarriedNpcs(farmer))
        {
            if (spouse == null) continue;
            string npcName = spouse.Name;

            // [BOUNDARY] 伴侣在 friendshipData 检索不到 → 跳过该伴侣日结并记录。
            if (!farmer.friendshipData.TryGetValue(npcName, out var friendship) || friendship == null)
            {
                Log.Warning($"[SocialCrystallization] Spouse '{npcName}' missing from friendshipData — daily settle skipped.");
                continue;
            }

            try
            {
                // [BUG] ModData 写入失败须被捕获记录，防止阻断入睡存档。
                SocialProfile profile = SocialGraphService.Instance.GetProfile(
                    farmer, npcName, farmer.getFriendshipHeartLevelForNPC(npcName), isMarried: true);
                SettleSpouseDailyMetrics(profile, friendship.TalkedToToday, HasActiveNeglectShock(npcName));
                SocialGraphService.Instance.SaveProfile(farmer, npcName, profile, isMarried: true);
            }
            catch (Exception ex)
            {
                Log.Error(ex, $"[SocialCrystallization] Daily settle failed for '{npcName}'.");
            }
        }
    }

    internal void OnDayStarted(object sender, DayStartedEventArgs e)
    {
        if (!Context.IsWorldReady || Game1.player == null) return;

        Farmer farmer = Game1.player;
        bool isZh = Prompts.ResolveIsChinese();
        var currentColdSpells = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (NPC spouse in SpouseQueryService.Instance.GetAllMarriedNpcs(farmer))
        {
            if (spouse == null) continue;
            string npcName = spouse.Name;

            SocialProfile profile = SocialGraphService.Instance.GetProfile(
                farmer, npcName, farmer.getFriendshipHeartLevelForNPC(npcName), isMarried: true);
            bool isCold = profile.Archetype == SocialArchetype.DomesticColdSpell;
            currentColdSpells.Add(npcName);

            // 阶跃门禁：仅 Normal <-> ColdSpell 转换当天提示一次，杜绝每日刷屏。
            bool wasCold = _yesterdayColdSpells.Contains(npcName);
            if (isCold == wasCold) continue;

            string displayName = isZh ? NpcNameLocalizer.GetZhName(npcName) : (spouse.displayName ?? npcName);
            ShowHudMessage((isCold, isZh) switch
            {
                (true, true) => $"{displayName} 似乎心情有些低落，默默避开了你的目光……",
                (true, false) => $"{displayName} seems downcast and quietly avoids your gaze...",
                (false, true) => $"{displayName} 神色缓和了许多，早晨的气氛轻松了起来。",
                _ => $"{displayName}'s expression has softened; the morning air feels lighter.",
            });
        }

        _yesterdayColdSpells.Clear();
        foreach (string npcName in currentColdSpells) _yesterdayColdSpells.Add(npcName);
    }

    internal void OnReturnedToTitle(object sender, ReturnedToTitleEventArgs e)
    {
        _yesterdayColdSpells.Clear();
    }

    private static void ShowHudMessage(string text)
    {
        try
        {
            Game1.addHUDMessage(new HUDMessage(text));
        }
        catch (Exception ex)
        {
            // [RECOVERABLE] 无头/异常环境：静默跳过，严禁阻断游戏循环。
            Log.Debug($"[SocialCrystallization] HUD message skipped ({ex.Message}).");
        }
    }

    /// <summary>
    /// 探测 NPC 是否带有未过期的 Neglect 冲击（工单授权的等价检查：
    /// MoodShockStore 未公开按 SourceId 的活性探测 API 且不在本工单范围，
    /// 过期判定与 store 的惰性清除规则一致）。
    /// </summary>
    private static bool HasActiveNeglectShock(string npcName)
    {
        var shocks = typeof(MoodShockStore).GetField("_shocks", BindingFlags.Static | BindingFlags.NonPublic)
            ?.GetValue(null) as Dictionary<string, List<MoodShock>>;
        if (shocks == null)
        {
            Log.Warning("[SocialCrystallization] MoodShockStore._shocks unavailable — neglect probe skipped.");
            return false;
        }

        string neglectId = EmotionShockIds.Neglect(npcName);
        int now = MoodShockStore.NowProvider();
        foreach (var kvp in shocks)
        {
            if (!string.Equals(kvp.Key, npcName, StringComparison.OrdinalIgnoreCase)) continue;
            foreach (MoodShock shock in kvp.Value)
            {
                if (string.Equals(shock.SourceId, neglectId, StringComparison.OrdinalIgnoreCase)
                    && shock.DurationMinutes > 0
                    && now - shock.StartGameMinutes < shock.DurationMinutes)
                {
                    return true;
                }
            }
        }
        return false;
    }
}
