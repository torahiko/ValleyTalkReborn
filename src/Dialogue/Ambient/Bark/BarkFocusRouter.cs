using System;
using System.Collections.Generic;
using System.Linq;
using StardewValley;

namespace ValleytalkReborn;

internal enum BarkFocusType { Sensory, Interactive, Introspective, FreeDrift }

internal sealed class BarkFocusDecision
{
    public BarkFocusType FocusType { get; init; }
    /// <summary>唯一注入 Prompt 的焦点描述行（已净化，适合 Bark 语境）</summary>
    public string InjectedContextLine { get; init; }
    /// <summary>是否允许 BuildThinkingPrompt 注入 Preoccupation lens</summary>
    public bool AllowThinkingLens { get; init; }
    /// <summary>Sensory 细粒度去重键，如 "weather_rain" / "object_fireplace"，非 Sensory 时为 null</summary>
    public string SensoryItemKey { get; init; }
    /// <summary>本轮若命中了 Perception 条目，记录于此以供组装后执行阅后即焚</summary>
    public PerceptionEntry MatchedPerception { get; init; }
}

/// <summary>
/// Bark 注意力单槽位竞争路由器。
/// 输入：NPC 当前状态快照；输出：唯一的 BarkFocusDecision。
/// 职责严格限定为"选哪个焦点 + 提供净化后的单行描述文本"，不持有任何状态。
/// </summary>
internal static class BarkFocusRouter
{
    private static readonly Random _rng = new Random();

    /// <summary>
    /// 主入口：在主线程调用，返回本轮唯一焦点决策。
    /// </summary>
    internal static BarkFocusDecision Decide(
        NPC npc,
        AmbientBarkStateStore.State state,
        BioData bio,
        bool isZh)
    {
        // ── 收集各池候选，计算带疲劳阻尼的权重 ──
        var candidates = BuildCandidates(npc, state, bio, isZh);

        if (candidates.Count == 0)
            return MakeFreeDrift(isZh);

        // 加权随机选取
        float totalWeight = candidates.Sum(c => c.Weight);
        float roll = (float)_rng.NextDouble() * totalWeight;
        float cumulative = 0f;

        foreach (var candidate in candidates)
        {
            cumulative += candidate.Weight;
            if (roll <= cumulative)
                return candidate.Decision;
        }

        return candidates[candidates.Count - 1].Decision;
    }

    // ══════════════════════════════════════════════════════════
    //  候选池构建
    // ══════════════════════════════════════════════════════════

    private readonly struct Candidate
    {
        public readonly float Weight;
        public readonly BarkFocusDecision Decision;
        public Candidate(float weight, BarkFocusDecision decision) { Weight = weight; Decision = decision; }
    }

    private static List<Candidate> BuildCandidates(
        NPC npc,
        AmbientBarkStateStore.State state,
        BioData bio,
        bool isZh)
    {
        var list = new List<Candidate>();
        BarkFocusType lastType;
        string lastSensoryKey;

        lock (state)
        {
            lastType       = state.LastFocusType;
            lastSensoryKey = state.LastSensoryKey;
        }

        // ── 优先检测：3 分钟内短时思维延续 (Tail 具有绝对优先权，直接锁定) ──
        var tailDecision = TryGetDirectTailContinuation(state, isZh);
        if (tailDecision != null)
        {
            list.Add(new Candidate(100.0f, tailDecision));
            return list;
        }

        // ── 池 1：Introspective（角色心事，受疲劳阻尼） ──
        TryAddPreoccupation(bio, lastType, list);

        // ── 池 2：Interactive（瞬态感知 + 在场最熟悉者） ──
        TryAddInteractive(npc, lastType, isZh, list);

        // ── 池 3：Sensory（体感/天气/室内物件） ──
        TryAddSensory(npc, lastType, lastSensoryKey, isZh, list);

        return list;
    }

    // ──────────────────────────────────────────────────────────
    //  Tail 续接（绝对优先，直接短路）
    // ──────────────────────────────────────────────────────────

    private static BarkFocusDecision TryGetDirectTailContinuation(
        AmbientBarkStateStore.State state,
        bool isZh)
    {
        if (state == null) return null;

        List<string> tail;
        DateTime? tailEndedAt;
        int tailGameTime, tailSaveDay;

        lock (state)
        {
            tail         = state.LastThreadTail.Count > 0 ? state.LastThreadTail.ToList() : null;
            tailEndedAt  = state.LastThreadEndedAt;
            tailGameTime = state.LastThreadGameTimeOfDay;
            tailSaveDay  = state.LastThreadSaveDayNumber;
        }

        if (tail != null && tailEndedAt.HasValue)
        {
            bool sameDay        = tailSaveDay == Game1.Date.TotalDays;
            bool sameTimeWindow = Math.Abs(Game1.timeOfDay - tailGameTime) <= 20;
            double minutesSince = (DateTime.UtcNow - tailEndedAt.Value).TotalMinutes;

            if (sameDay && sameTimeWindow && minutesSince <= 3)
            {
                string tailBlock = isZh
                    ? string.Join("，", tail.Select(l => $"「{l}」"))
                    : string.Join(" ", tail.Select(l => $"\"{l}\""));

                string contextLine = isZh
                    ? $"刚才最后想的是：{tailBlock}。才过了一会儿——"
                    : $"You were just thinking: {tailBlock}. Only a moment passed—";

                return new BarkFocusDecision
                {
                    FocusType           = BarkFocusType.Introspective,
                    InjectedContextLine = contextLine,
                    AllowThinkingLens   = false,
                    SensoryItemKey      = null
                };
            }
        }

        return null;
    }

    // ──────────────────────────────────────────────────────────
    //  池 1：Introspective（仅 Preoccupation 心事，受疲劳阻尼）
    // ──────────────────────────────────────────────────────────

    private static void TryAddPreoccupation(
        BioData bio,
        BarkFocusType lastType,
        List<Candidate> list)
    {
        if (bio?.Preoccupations != null && bio.Preoccupations.Count > 0)
        {
            var picked = bio.Preoccupations[_rng.Next(bio.Preoccupations.Count)];
            float weight = 1.0f;
            if (lastType == BarkFocusType.Introspective) weight *= 0.3f;

            weight *= 0.2f;

            if (weight > 0.01f)
            {
                list.Add(new Candidate(weight, new BarkFocusDecision
                {
                    FocusType           = BarkFocusType.Introspective,
                    InjectedContextLine = picked,
                    AllowThinkingLens   = true,
                    SensoryItemKey      = null
                }));
            }
        }
    }

    // ──────────────────────────────────────────────────────────
    //  池 2：Interactive
    // ──────────────────────────────────────────────────────────

    private static void TryAddInteractive(
        NPC npc,
        BarkFocusType lastType,
        bool isZh,
        List<Candidate> list)
    {
        float fatigueMult = (lastType == BarkFocusType.Interactive) ? 0.3f : 1.0f;

        // 2a. 瞬态感知（携带感知对象以供即焚）
        var perceptions = PerceptionManager.Instance?.GetFilteredBucketFor(npc.Name, 1);
        if (perceptions != null && perceptions.Count > 0)
        {
            var entry = perceptions[0];
            if (!string.IsNullOrWhiteSpace(entry?.Template))
            {
                string cleaned = FormatPerceptionForBark(entry.Template, isZh);
                if (!string.IsNullOrWhiteSpace(cleaned))
                {
                    float weight = 2.5f * fatigueMult;
                    list.Add(new Candidate(weight, new BarkFocusDecision
                    {
                        FocusType           = BarkFocusType.Interactive,
                        InjectedContextLine = cleaned,
                        AllowThinkingLens   = false,
                        SensoryItemKey      = null,
                        MatchedPerception   = entry // 绑定命中条目
                    }));
                }
            }
        }

        // 2b. 在场最熟悉者（仅限玩家或配偶）
        var notable = FindMostNotableNearby(npc, isZh);
        if (notable != null)
        {
            float weight = 1.5f * fatigueMult;
            list.Add(new Candidate(weight, new BarkFocusDecision
            {
                FocusType           = BarkFocusType.Interactive,
                InjectedContextLine = notable,
                AllowThinkingLens   = false,
                SensoryItemKey      = null,
                MatchedPerception   = null
            }));
        }
    }

    // ──────────────────────────────────────────────────────────
    //  池 3：Sensory
    // ──────────────────────────────────────────────────────────

    private static void TryAddSensory(
        NPC npc,
        BarkFocusType lastType,
        string lastSensoryKey,
        bool isZh,
        List<Candidate> list)
    {
        float fatigueMult = (lastType == BarkFocusType.Sensory) ? 0.3f : 1.0f;

        var sensoryItems = CollectSensoryItems(npc, isZh);
        if (sensoryItems.Count == 0) return;

        // 单次阻尼：过滤上一轮相同的 item key
        var filtered = sensoryItems
            .Where(s => s.Key != lastSensoryKey)
            .ToList();

        // 若全部被过滤（只有一种感官刺激），降级允许重用（避免 Sensory 池被完全清空）
        var pool = filtered.Count > 0 ? filtered : sensoryItems;

        // 随机取 1 件，模拟注意力自然落点
        var chosen = pool[_rng.Next(pool.Count)];

        float weight = 1.8f * fatigueMult;
        list.Add(new Candidate(weight, new BarkFocusDecision
        {
            FocusType           = BarkFocusType.Sensory,
            InjectedContextLine = chosen.Description,
            AllowThinkingLens   = false,
            SensoryItemKey      = chosen.Key
        }));
    }

    // ══════════════════════════════════════════════════════════
    //  FreeDrift Fallback
    // ══════════════════════════════════════════════════════════

    private static BarkFocusDecision MakeFreeDrift(bool isZh)
    {
        return new BarkFocusDecision
        {
            FocusType           = BarkFocusType.FreeDrift,
            InjectedContextLine = null, // FreeDrift 不注入焦点行，由 BuildThinkingPrompt 提供泛感官指引
            AllowThinkingLens   = false,
            SensoryItemKey      = null
        };
    }

    // ══════════════════════════════════════════════════════════
    //  辅助：感官候选收集
    // ══════════════════════════════════════════════════════════

    private readonly struct SensoryItem
    {
        public readonly string Key;         // 去重键，如 "weather_rain"
        public readonly string Description; // 注入文本
        public SensoryItem(string key, string description) { Key = key; Description = description; }
    }

    private static List<SensoryItem> CollectSensoryItems(NPC npc, bool isZh)
    {
        var items = new List<SensoryItem>();
        var loc = npc?.currentLocation;
        if (loc == null) return items;

        bool isIndoor = !loc.IsOutdoors        || loc is StardewValley.Locations.FarmHouse
                        || loc is StardewValley.Locations.IslandFarmHouse;

        // 天气（户外）
        if (!isIndoor)
        {
            string weatherKey  = "weather_" + GetWeatherRawKey();
            string weatherDesc = BuildWeatherSensoryLine(isZh);
            if (!string.IsNullOrEmpty(weatherDesc))
                items.Add(new SensoryItem(weatherKey, weatherDesc));
        }

        // 时段感（晚上/深夜）
        if (Game1.timeOfDay >= 2000)
        {
            string nightDesc = isZh ? "夜色沉了下来。" : "The night has settled in.";
            items.Add(new SensoryItem("time_night", nightDesc));
        }

        // 室内：最多 1 件突出物件（SceneContextBuilder，截断为 1 件）
        if (isIndoor)
        {
            string sceneBlock = SceneContextBuilder.BuildSceneBlock(npc, radiusTiles: 4, maxItems: 1);
            if (!string.IsNullOrWhiteSpace(sceneBlock))
            {
                var lines = sceneBlock.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
                // 跳过标题行，取第一个实际物件行
                string objectLine = lines.Skip(1).FirstOrDefault(l => !string.IsNullOrWhiteSpace(l));
                if (!string.IsNullOrEmpty(objectLine))
                {
                    string objectKey = "object_" + objectLine.Trim().ToLowerInvariant().Replace(" ", "_");
                    if (objectKey.Length > 40) objectKey = objectKey.Substring(0, 40);
                    items.Add(new SensoryItem(objectKey, objectLine.Trim()));
                }
            }
        }

        return items;
    }

    private static string GetWeatherRawKey()
    {
        if (Game1.isSnowing)      return "snow";
        if (Game1.isLightning)    return "storm";
        if (Game1.isRaining)      return "rain";
        if (Game1.isDebrisWeather) return "wind";
        return "clear";
    }

    private static string BuildWeatherSensoryLine(bool isZh)
    {
        if (isZh)
        {
            if (Game1.isSnowing)       return "雪还在下，脚底踩上去细细的咯吱声。";
            if (Game1.isLightning)     return "远处隐约滚着雷，空气里带着湿闷的电味。";
            if (Game1.isRaining)       return "雨声沙沙，落在地上溅起小小的水花。";
            if (Game1.isDebrisWeather) return "风把什么东西刮得沙沙响，不知道从哪吹来的。";
            return null; // 晴天不提供天气感官
        }
        else
        {
            if (Game1.isSnowing)       return "Snow still falling — soft crunch underfoot.";
            if (Game1.isLightning)     return "Distant thunder rolling somewhere. The air smells of rain and ozone.";
            if (Game1.isRaining)       return "Rain tapping steadily, little splashes on the ground.";
            if (Game1.isDebrisWeather) return "Wind rustling something nearby — leaves or debris, hard to tell.";
            return null;
        }
    }

    // ══════════════════════════════════════════════════════════
    //  辅助：Interactive 在场最熟悉者
    // ══════════════════════════════════════════════════════════

    private static string FindMostNotableNearby(NPC npc, bool isZh)
    {
        var loc = npc?.currentLocation;
        if (loc == null || Game1.player == null) return null;

        bool isOutdoors   = loc.IsOutdoors;
        int rangeSquared  = isOutdoors ? 100 : 49;

        int bestHearts   = 2; // >2 心保底
        string bestLabel = null;

        // 检查玩家
        var player = Game1.player;
        if (player.currentLocation == loc &&
            DialogueUtilities.IsInRangeSquared(npc, player, rangeSquared))
        {
            if (player.friendshipData != null &&
                player.friendshipData.TryGetValue(npc.Name, out var fs) && fs != null)
            {
                int hearts = fs.Points / 250;
                if (hearts > bestHearts)
                {
                    bestHearts = hearts;
                    string pName = player.displayName ?? player.Name ?? "player";
                    bestLabel = isZh
                        ? $"玩家{pName}就在附近。"
                        : $"{pName} is nearby.";
                }
            }
        }

        // 检查其他 NPC（仅当对方是配偶或有特殊羁绊时触发，严禁使用玩家的 friendshipData）
        string spouseName = npc.getSpouse()?.Name;
        foreach (var other in loc.characters)
        {
            if (other == null || other == npc || !other.IsVillager) continue;
            if (!DialogueUtilities.IsInRangeSquared(npc, other, rangeSquared)) continue;

            // 如果是已婚配偶路过，赋予最高吸引力
            if (!string.IsNullOrEmpty(spouseName) && string.Equals(other.Name, spouseName, StringComparison.OrdinalIgnoreCase))
            {
                string dn = isZh ? NpcNameLocalizer.GetZhName(other.Name) : (other.displayName ?? other.Name);
                return isZh ? $"{dn}就在不远处。" : $"{dn} is nearby.";
            }
        }

        return bestLabel;
    }

    // ══════════════════════════════════════════════════════════
    //  辅助：Perception 模板净化
    // ══════════════════════════════════════════════════════════

    /// <summary>
    /// 将主对话语境的 Perception 模板转换为 Bark 自言自语语境的客观事实描述。
    /// 剔除中括号标签前缀与"面前的/你刚刚"等面对面交互词汇。
    /// </summary>
    internal static string FormatPerceptionForBark(string template, bool isZh)
    {
        if (string.IsNullOrWhiteSpace(template)) return null;

        string s = template.Trim();

        // 1. 剔除 [中括号标签] 前缀，如 [生理状态]、[随身细节]、[即时事件]
        if (s.StartsWith("["))
        {
            int close = s.IndexOf(']');
            if (close >= 0 && close < s.Length - 1)
                s = s.Substring(close + 1).TrimStart();
        }

        // 2. 剔除面对面交互词汇
        if (isZh)
        {
            s = s.Replace("面前的玩家", "玩家").Replace("你刚刚收到了", "刚才收到了")
                 .Replace("面前的", "");
        }
        else
        {
            s = s.Replace("The player in front of you", "The player")
                 .Replace("in front of you", "")
                 .Replace("You just received", "Just received");
        }

        return string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    }
}
