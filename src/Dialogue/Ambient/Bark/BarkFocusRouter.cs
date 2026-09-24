using System;
using System.Collections.Generic;
using System.Linq;
using StardewModdingAPI;
using StardewValley;

namespace ValleytalkReborn;

internal enum BarkFocusType { Sensory, Interactive, Introspective, Companion, FreeDrift }

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
    private const double HeldItemGateProbability = 0.30;
    private const float  HeldItemPerceptionWeight = 1.0f;
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

        // ── 池 0：Companion（跟随/约会注意力聚焦） ──
        var focus = CompanionFocusResolver.Resolve(npc);
        if (focus != CompanionFocusMode.None)
        {
            // 权重 2.2f：高于 Sensory(1.8f)/Preoccupation(0.7f)，低于瞬态感知(2.5f)；
            // 疲劳阻尼：lastType == Companion 时 weight *= 0.4f（允许继续聚焦但不霸屏）
            float companionWeight = 2.2f;
            if (lastType == BarkFocusType.Companion)
                companionWeight *= 0.4f;

            var loc = npc?.currentLocation;
            string locName = loc != null ? EnvironmentScanner.GetLocationFriendlyName(loc.Name) : null;

            string contextLine;
            if (loc == null)
            {
                // loc 为 null 兜底文案，不 return null 文本
                contextLine = isZh ? "你和农夫正待在一起。" : "You are spending time with the farmer.";
            }
            else
            {
                switch (focus)
                {
                    case CompanionFocusMode.DateWalking:
                        contextLine = isZh
                            ? $"你和农夫正在{locName}边走边约会，注意力留在你们的同行相处上。"
                            : $"You are walking through {locName} together with the farmer, your attention on the companionship between you.";
                        break;
                    case CompanionFocusMode.DateSettled:
                        contextLine = isZh
                            ? $"你正和农夫在{locName}约会，注意力留在两人之间的相处上。"
                            : $"You and the farmer are on a date at {locName}, your attention on the two of you together.";
                        break;
                    case CompanionFocusMode.RegularFollow:
                        contextLine = isZh
                            ? $"你正陪着农夫在{locName}散步同行。"
                            : $"You are walking alongside the farmer through {locName}.";
                        break;
                    default:
                        contextLine = isZh ? "你和农夫正待在一起。" : "You are spending time with the farmer.";
                        break;
                }
            }

            list.Add(new Candidate(companionWeight, new BarkFocusDecision
            {
                FocusType           = BarkFocusType.Companion,
                InjectedContextLine = contextLine,
                AllowThinkingLens   = false,
                SensoryItemKey      = null,
                MatchedPerception   = null
            }));
            // 不 return：保留与其他池的竞争语义，与单槽位路由器设计一致
            ModEntry.SMonitor?.Log($"[BarkFocusRouter] Companion focus candidate for {npc?.Name}: mode={focus}, weight={companionWeight:F2}", LogLevel.Trace);
        }

        // ── 池 1：Introspective（内心世界） ──
        // 1a. 心事常态（解除硬编码 0.2，基准权重 0.7f）
        TryAddPreoccupation(npc, bio, lastType, list);
        // 1b. 记忆闪回（低频彩蛋 0.25f，受时空心境门槛限制）
        TryAddLongIntervalEcho(npc, lastType, isZh, list);

        // ── 池 2：Interactive（外部互动与在场感知） ──
        TryAddInteractive(npc, lastType, isZh, list);

        // ── 池 3：Sensory（体感/天气/室内物件，基准 1.8f） ──
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
    //  池 1a：心事常态 (Preoccupation) - 一放
    // ──────────────────────────────────────────────────────────

    private static void TryAddPreoccupation(
        NPC npc,
        BioData bio,
        BarkFocusType lastType,
        List<Candidate> list)
    {
        var activeEntry = ProgressStateResolver.ResolveActiveEntry(npc, bio?.ProgressStates);
        var pool = (activeEntry?.Preoccupations != null && activeEntry.Preoccupations.Count > 0)
            ? activeEntry.Preoccupations
            : bio?.Preoccupations;

        ModEntry.SMonitor?.Log($"[BarkFocusRouter] Preoccupation pool for {npc?.Name}: {(activeEntry?.Preoccupations?.Count > 0 ? "stage" : "global")} ({pool?.Count ?? 0})", LogLevel.Trace);

        if (pool != null && pool.Count > 0)
        {
            var picked = pool[_rng.Next(pool.Count)];

            // 基准权重 0.7f：低于感官(1.8f)，但在平静时刻能自然浮现
            float weight = 0.7f;
            if (lastType == BarkFocusType.Introspective)
                weight *= 0.3f; // 疲劳阻尼：防连续自我内耗

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
    //  池 1b：记忆闪回 (LongIntervalEcho) - 一缓
    // ──────────────────────────────────────────────────────────

    private static void TryAddLongIntervalEcho(
        NPC npc,
        BarkFocusType lastType,
        bool isZh,
        List<Candidate> list)
    {
        var loc = npc?.currentLocation;
        if (loc == null) return;

        // 门槛闸门：仅在情绪易泛滥的情境下才允许闪回
        bool isQuietTime    = Game1.timeOfDay >= 2000;
        bool isGloomyWeather = Game1.isRaining || Game1.isSnowing || Game1.isLightning;
        bool isIndoorQuiet  = !loc.IsOutdoors && loc.characters.Count <= 3;

        if (!isQuietTime && !isGloomyWeather && !isIndoorQuiet)
            return;

        var memoryMgr = MemoryManager.Instance;
        if (memoryMgr == null) return;

        string echoText = memoryMgr.GetRandomMemoryFragment(npc.Name);
        if (string.IsNullOrWhiteSpace(echoText)) return;

        // 极低频彩蛋权重（0.25f）
        float fatigueMult = (lastType == BarkFocusType.Introspective) ? 0.3f : 1.0f;
        float weight = 0.25f * fatigueMult;

        if (weight > 0.01f)
        {
            // 碎片化包装：引导模型只捕捉情绪余波，不要复述日记
            string cleanedMemory = echoText.Trim().TrimEnd('。', '.', '！', '!');
            string contextLine = isZh
                ? $"脑子里忽然掠过以前的一点事（{cleanedMemory}）... 只是闪了一下念头。"
                : $"A fleeting trace of the past crossed your mind ({cleanedMemory})... just a passing thought.";

            list.Add(new Candidate(weight, new BarkFocusDecision
            {
                FocusType           = BarkFocusType.Introspective,
                InjectedContextLine = contextLine,
                AllowThinkingLens   = false,
                SensoryItemKey      = null
            }));
        }
    }

    // ──────────────────────────────────────────────────────────
    //  池 2：Interactive（含关系网分层） - 一限
    // ──────────────────────────────────────────────────────────

    private static void TryAddInteractive(
        NPC npc,
        BarkFocusType lastType,
        bool isZh,
        List<Candidate> list)
    {
        float fatigueMult = (lastType == BarkFocusType.Interactive) ? 0.3f : 1.0f;

        // 2a. 瞬态感知（最高优先级交互）
        var perceptions = PerceptionManager.Instance?.GetFilteredBucketFor(npc.Name, 1);
        if (perceptions != null && perceptions.Count > 0)
        {
            var entry = perceptions[0];
            if (!string.IsNullOrWhiteSpace(entry?.Template))
            {
                bool isHeldItem = string.Equals(entry?.Key, "PlayerActiveItem", StringComparison.Ordinal);
                if (isHeldItem && _rng.NextDouble() >= HeldItemGateProbability)
                {
                    ModEntry.SMonitor?.Log("[BarkFocusRouter] 手持物品感知未过频率闸门，本轮跳过", LogLevel.Trace);
                    // 不 return，让代码继续向下执行 2b 和 2c
                }
                else
                {
                    float weight = isHeldItem ? HeldItemPerceptionWeight : 2.5f;
                    string cleaned = FormatPerceptionForBark(entry.Template, isZh);
                    if (!string.IsNullOrWhiteSpace(cleaned))
                    {
                        list.Add(new Candidate(weight * fatigueMult, new BarkFocusDecision
                        {
                            FocusType           = BarkFocusType.Interactive,
                            InjectedContextLine = cleaned,
                            AllowThinkingLens   = false,
                            SensoryItemKey      = null,
                            MatchedPerception   = entry
                        }));
                    }
                }
            }
        }

        // 2b. 约会目击
        var witnessDecision = TryGetWitnessDateFocus(npc, isZh);
        if (witnessDecision != null)
        {
            list.Add(new Candidate(3.0f * fatigueMult, witnessDecision));
        }

        // 2c. 附近人物感知（分层竞争：T1-T4 关系网阶梯）
        var nearbyCandidates = EvaluateNearbyPresence(npc, isZh);
        foreach (var person in nearbyCandidates)
        {
            list.Add(new Candidate(person.Weight * fatigueMult, new BarkFocusDecision
            {
                FocusType           = BarkFocusType.Interactive,
                InjectedContextLine = person.Description,
                AllowThinkingLens   = false,
                SensoryItemKey      = null,
                MatchedPerception   = null
            }));
        }
    }

    /// <summary>
    /// 检查该路人 NPC 是否正在目击玩家与约会对象的约会。
    /// 若是，返回 Interactive 焦点决策（纯事实描述，情绪由模型依据人设自由发挥）；
    /// 若否，返回 null。
    /// 命中后立即调用 DateManager.RecordNpcWitnessDate 记录 60 分钟冷却。
    /// </summary>
    private static BarkFocusDecision TryGetWitnessDateFocus(
        NPC npc,
        bool isZh)
    {
        var dateManager = DateManager.Instance;
        if (dateManager == null) return null;
        if (!dateManager.CanNpcWitnessDate(npc.Name)) return null;

        // 空间感知：路人 NPC 与玩家必须在同一地图，且处于视线范围（7 格）
        var player = Game1.player;
        if (player == null || npc.currentLocation != player.currentLocation) return null;

        const int WitnessRangeSquared = 49; // 7×7 格
        if (!DialogueUtilities.IsInRangeSquared(npc, player, WitnessRangeSquared))
            return null;

        string datePartner = dateManager.ActiveDateNpcName;
        if (string.IsNullOrEmpty(datePartner)) return null;

        string partnerName = isZh
            ? NpcNameLocalizer.GetZhName(datePartner)
            : (Game1.getCharacterFromName(datePartner)?.displayName ?? datePartner);

        // 成功消费门票：记录该 NPC 的 60 游戏分钟冷却
        dateManager.RecordNpcWitnessDate(npc.Name);

        // 纯事实描述，绝不预设情绪（让模型依据人设自由发挥好奇、祝福、吃味或调侃）
        string context = isZh
            ? $"你注意到农夫正和 {partnerName} 在一起，从两人的举止神态和氛围来看，明显是在约会。"
            : $"You notice the farmer and {partnerName} together nearby. From their closeness and the atmosphere, they are clearly on a date.";

        return new BarkFocusDecision
        {
            FocusType           = BarkFocusType.Interactive,
            InjectedContextLine = context,
            AllowThinkingLens   = false,  // 观察外界生动事件时，关闭内耗心事
            SensoryItemKey      = null,
            MatchedPerception   = null
        };
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

        // 室内：最多 1 件突出物件（直取实体列表，修复原 BuildSceneBlock 解析取到地点行的缺陷）
        if (isIndoor)
        {
            string item = null;

            var exclude = new List<string>();
            if (Game1.player != null)
            {
                if (!string.IsNullOrEmpty(Game1.player.displayName))
                    exclude.Add(Game1.player.displayName);
                if (!string.IsNullOrEmpty(Game1.player.Name))
                    exclude.Add(Game1.player.Name);
            }

            try
            {
                var names = SceneContextBuilder.BuildNearbyList(npc, radiusTiles: 4, maxItems: 1, excludeNames: exclude);
                item = names?.FirstOrDefault(l => !string.IsNullOrWhiteSpace(l))?.Trim();
            }
            catch (Exception ex)
            {
                ModEntry.SMonitor?.Log($"[BarkFocusRouter] NearbyList 读取失败，跳过室内物件触点: {ex.Message}", LogLevel.Trace);
                item = null;
            }

            if (!string.IsNullOrEmpty(item))
            {
                string objectKey = "object_" + item.ToLowerInvariant().Replace(" ", "_");
                if (objectKey.Length > 40) objectKey = objectKey.Substring(0, 40);
                string description = isZh ? $"你的目光落在{item}上。" : $"Your gaze lands on the {item} nearby.";
                items.Add(new SensoryItem(objectKey, description));
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
    //  辅助：Interactive 附近人物感知（关系网分层 T1-T4）
    // ══════════════════════════════════════════════════════════

    private readonly struct PersonCandidate
    {
        public readonly float Weight;
        public readonly string Description;
        public PersonCandidate(float weight, string description) { Weight = weight; Description = description; }
    }

    /// <summary>
/// 基于关系网、好感度与人群密度，动态评估在场人物与氛围
/// 彻底适配星落酒吧（高密度）与节日广场等拥挤场景
/// </summary>
private static List<PersonCandidate> EvaluateNearbyPresence(NPC npc, bool isZh)
{
    var result = new List<PersonCandidate>();
    bool isFestival = Game1.isFestival() || Game1.CurrentEvent?.isFestival == true;

    // ── 1. 节日专属氛围拦截（节日临时 Actor 的 currentLocation 可能为 null，
    //       故分支必须置于 loc 解引用与 null 早退之前以保证可达）──
    if (isFestival)
    {
        // 节日时全员聚拢，注意力首选节日整体氛围，不再逐个挑路人
        string festivalName = Game1.CurrentEvent?.FestivalName ?? (isZh ? "节日" : "festival");
        string festDesc = isZh
            ? $"今天是{festivalName}，广场上到处都是镇民，气氛格外喧闹。"
            : $"It's the {festivalName} today — the whole town is gathered around, bustling and loud.";

        result.Add(new PersonCandidate(1.2f, festDesc));

        // 节日里如果有配偶在身边，依然保留极高亲密注目
        int festRangeSquared = Game1.currentLocation?.IsOutdoors == true ? 100 : 49;
        string spouseN = npc.getSpouse()?.Name;
        if (!string.IsNullOrEmpty(spouseN) && Game1.currentLocation?.getCharacterFromName(spouseN) is NPC sp &&
            IsFestivalActorNear(npc, sp, festRangeSquared))
        {
            string sName = isZh ? NpcNameLocalizer.GetZhName(sp.Name) : (sp.displayName ?? sp.Name);
            result.Add(new PersonCandidate(1.6f, isZh ? $"{sName}就在你身边。" : $"{sName} is right by your side."));
        }

        return result; // 节日直接收口，不再处理日常人际扫描
    }

    var loc = npc?.currentLocation;
    if (loc == null) return result;

    int rangeSquared = loc.IsOutdoors ? 100 : 49;
    var player = Game1.player;

    // ── 2. 玩家在场判定 ──
    if (player != null && player.currentLocation == loc && DialogueUtilities.IsInRangeSquared(npc, player, rangeSquared))
    {
        int hearts = 0;
        if (player.friendshipData != null && player.friendshipData.TryGetValue(npc.Name, out var fs) && fs != null)
        {
            hearts = fs.Points / 250;
        }

        string pName = player.displayName ?? player.Name ?? "农夫";

        if (hearts >= 8)
        {
            result.Add(new PersonCandidate(1.6f, isZh ? $"{pName}就在身边。" : $"{pName} is right nearby."));
        }
        else if (hearts >= 2)
        {
            result.Add(new PersonCandidate(0.5f, isZh ? $"注意到农夫就在不远处。" : "Noticed the farmer nearby."));
        }
        else
        {
            // 低好感玩家做虚焦处理
            result.Add(new PersonCandidate(0.35f, isZh ? "有人从不远处走过去了。" : "Caught sight of someone walking past."));
        }
    }

    // ── 3. 其他村民与人群密度计算 ──
    string spouseName = npc.getSpouse()?.Name;
    var inRangeVillagers = loc.characters
        .Where(c => c != null && c != npc && c.IsVillager && DialogueUtilities.IsInRangeSquared(npc, c, rangeSquared))
        .ToList();

    int crowdCount = inRangeVillagers.Count;
    if (crowdCount == 0)
        return result;

    // 优先 3.1: 配偶在场 (最高优先级)
    var spouse = inRangeVillagers.FirstOrDefault(c =>
        !string.IsNullOrEmpty(spouseName) && string.Equals(c.Name, spouseName, StringComparison.OrdinalIgnoreCase));

    if (spouse != null)
    {
        string sName = isZh ? NpcNameLocalizer.GetZhName(spouse.Name) : (spouse.displayName ?? spouse.Name);
        result.Add(new PersonCandidate(1.6f, isZh ? $"{sName}就在不远处。" : $"{sName} is nearby."));
        return result;
    }

    // 优先 3.2: 关系网命中（亲属、死党、乐队同伴）
    var relatedVillagers = new List<NPC>();
    foreach (var other in inRangeVillagers)
    {
        if (NpcPersonaRelationScanner.HasRelationInAnyDirection(npc.Name, other.Name))
        {
            relatedVillagers.Add(other);
        }
    }

    if (relatedVillagers.Count > 0)
    {
        var chosen = relatedVillagers[_rng.Next(relatedVillagers.Count)];
        string oName = isZh ? NpcNameLocalizer.GetZhName(chosen.Name) : (chosen.displayName ?? chosen.Name);

        // 如果在拥挤的室内（如酒吧），描述加上空间距离感
        string noticeLine = (crowdCount >= 4 && !loc.IsOutdoors)
            ? (isZh ? $"隔着人群看到{oName}也在那边。" : $"Spotted {oName} over through the crowd.")
            : (isZh ? $"看见{oName}也在附近。" : $"Noticed {oName} around here too.");

        result.Add(new PersonCandidate(0.8f, noticeLine));
        return result;
    }

    // ── 3.3 降级处理：无特殊关系时的环境反馈 ── 
    if (crowdCount >= 4)
    {
        // ★ 高密度人群特异化（酒吧/聚会）：将注意力转向整体环境氛围，权重提升至 0.7f
        if (!loc.IsOutdoors)
        {
            // 典型场景：星落酒吧周五晚
            string barAtmosphere = isZh
                ? "周围聚了不少人，满是断断续续的说笑和碰杯声。"
                : "The place is quite packed, filled with intermittent chatter and clinking glasses.";
            result.Add(new PersonCandidate(0.7f, barAtmosphere));
        }
        else
        {
            // 典型场景：户外集会
            string outdoorCrowd = isZh
                ? "附近聚着好些镇民，周围显得有些嘈杂。"
                : "A good number of townsfolk have gathered around, pretty bustling.";
            result.Add(new PersonCandidate(0.6f, outdoorCrowd));
        }
    }
    else
    {
        // 普通低密度场景：1~3 个无关系路人，依然采用淡化虚焦
        result.Add(new PersonCandidate(0.35f, isZh ? "周围有镇民在走动散步。" : "A few townsfolk are milling about nearby."));
    }

    return result;
}

    /// <summary>
    /// 节日语境下两个 Actor 的距离判定。
    /// 节日 actors 与玩家同处活动地图，npc.currentLocation 在临时 Actor 上可能为 null，
    /// 故不做 currentLocation 相等性检查，仅做 null 检查后与 DialogueUtilities.IsInRangeSquared
    /// 相同的像素平方距离计算（long 防溢出、除以 64*64=4096、&lt;= rangeSquared）。
    /// </summary>
    private static bool IsFestivalActorNear(NPC a, NPC b, int rangeSquared)
    {
        if (a == null || b == null) return false;

        long dx = (long)a.Position.X - (long)b.Position.X;
        long dy = (long)a.Position.Y - (long)b.Position.Y;
        long distSq = (dx * dx + dy * dy) / (64 * 64);

        return distSq <= rangeSquared;
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
