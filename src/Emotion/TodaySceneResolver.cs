using System;
using System.Collections.Generic;
using System.Linq;
using StardewModdingAPI;
using StardewValley;

namespace ValleytalkReborn;

public static class TodaySceneResolver
{
    /// <summary>
    /// 为单个角色抽选今日场景。纯逻辑，仅在主线程调用；Bio 为 null 时早退（场景保持 null）。
    /// </summary>
    public static void ResolveForCharacter(Character character, NPC rawNpc)
    {
        // 防跨日残留：先行置 null
        character.CurrentTodayScene = null;

        if (character?.Bio == null)
        {
            return;
        }

        List<TodayScene> candidateScenes = character.Bio.TodayScenes;
        if (candidateScenes == null || candidateScenes.Count == 0)
        {
            return; // 场景保持 null
        }

        // ── 历史窗口 ──
        var recent = character.RecentSceneIds ??= new List<string>();
        int maxHistory = Math.Max(1, Math.Min(3, candidateScenes.Count - 1));
        while (recent.Count > maxHistory)
        {
            recent.RemoveAt(0);
        }

        // ── 候选池（排除 recent），空则回退全量 ──
        List<TodayScene> pool = candidateScenes
            .Where(s => !recent.Contains(s.Id))
            .ToList();
        if (pool.Count == 0)
        {
            pool = candidateScenes;
        }

        // ── 权重：基于 Shock 强度与 Bias 方向 ──
        List<double> weights;
        string name = character.Name;
        var (sv, sa, so) = MoodShockStore.GetAggregatedDeltas(name);
        double shockIntensity = Math.Sqrt(sv * sv + sa * sa + so * so);

        if (shockIntensity >= 0.25f)
        {
            weights = pool.Select(scene =>
            {
                double dot = sv * scene.Bias.Valence + sa * scene.Bias.Arousal + so * scene.Bias.Openness;
                if (dot > 0) return 4.0;
                if (dot < -0.1f) return 0.2;
                return 1.0;
            }).ToList();
        }
        else
        {
            weights = Enumerable.Repeat(1.0, pool.Count).ToList();
        }

        // ── 确定性种子 + 加权轮盘抽选 ──
        int seed = (GetDeterministicHash(name) ^ (Game1.Date.TotalDays * 397)) & 0x7FFFFFFF;
        TodayScene picked = PickWeighted(pool, weights, seed);

        character.CurrentTodayScene = picked;
        recent.Add(picked.Id);

        ModEntry.SMonitor?.Log(
            $"[TodayScene] {name} 锁定场景: {picked.Id} (Tag: {picked.Tag})",
            LogLevel.Trace);
    }

    /// <summary>
    /// FNV-1a 32-bit 确定性哈希，pure，供单测与内部使用。
    /// </summary>
    public static int GetDeterministicHash(string str)
    {
        if (string.IsNullOrEmpty(str)) return 0;
        int hash = unchecked((int)0x811c9dc5);
        foreach (char c in str)
        {
            hash ^= c;
            hash = unchecked(hash * (int)0x01000193);
        }
        return hash;
    }

    /// <summary>
    /// 加权轮盘抽选（pure）。roll 超出总权重时兜底 pool[0]。
    /// </summary>
    public static TodayScene PickWeighted(IReadOnlyList<TodayScene> pool, List<double> weights, int seed)
    {
        if (pool == null || pool.Count == 0)
            throw new ArgumentException("pool 不能为空", nameof(pool));

        double total = 0;
        foreach (var w in weights) total += w;

        if (total <= 0)
            return pool[0];

        double roll = Math.Abs(seed) % total;
        double acc = 0;
        for (int i = 0; i < pool.Count; i++)
        {
            acc += weights[i];
            if (roll < acc)
                return pool[i];
        }

        return pool[0]; // 浮点残余兜底
    }
}
