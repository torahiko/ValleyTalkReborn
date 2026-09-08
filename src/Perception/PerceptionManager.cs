using System;
using System.Collections.Generic;
using System.Linq;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

namespace ValleytalkReborn;

/// <summary>
/// 管理三大感知队列：
///   Track 1 — _globalGossip (max 2): 全镇级别八卦快照与 Landmark（传说鱼、大事件）。
///   Track 2 — 玩家即时状态与交互桶：
///       - _playerStateBucket (max 6): 随身身体状态（Player*）。
///       - _activityBucket    (max 8): 玩家日常交互事件（Gift/Talk/Eat/Fish/Chop/...）。
/// </summary>
internal class PerceptionManager
{
    public static readonly PerceptionManager Instance = new PerceptionManager();

    private readonly object _lock = new object();

    // Track 1: 全局传闻快照（上限 2 条，简单 FIFO）
    private readonly Queue<PerceptionEntry> _globalGossip = new Queue<PerceptionEntry>();
    private const int MaxGossipEntries = 2;

    // Track 2: 玩家即时状态桶（上限 6 条，随身状态专用）
    private readonly Queue<PerceptionEntry> _playerStateBucket = new Queue<PerceptionEntry>();
    private const int MaxPlayerStateEntries = 6;

    // Track 2: 行为与互动事件桶（上限 8 条，保证关键行为不被过早驱逐）
    private readonly Queue<PerceptionEntry> _activityBucket = new Queue<PerceptionEntry>();
    private const int MaxActivityEntries = 8;

    // 独立维护全天已交互 NPC，防止 2 小时 TTL 或 8 条上限驱逐导致判定失效
    private readonly HashSet<string> _interactedNpcNamesToday = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    // ★ 防复读字典：记录指定 NPC 当天已经注意过的物品 ID（NPC名称 -> HashSet<ItemId>）
    private readonly Dictionary<string, HashSet<string>> _npcNoticedItemIdsToday =
        new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

    private const int BuildingResourcesCategory = -16;

    private PerceptionManager()
    {
        if (ModEntry.SHelper != null)
        {
            ModEntry.SHelper.Events.GameLoop.UpdateTicked += OnUpdateTicked;
            ModEntry.SHelper.Events.GameLoop.DayStarted   += OnDayStarted;
        }
    }

    // ─────────────────────────────────────────────
    //  Lifecycle
    // ─────────────────────────────────────────────

    public void Cleanup()
    {
        try
        {
            if (ModEntry.SHelper != null)
            {
                ModEntry.SHelper.Events.GameLoop.UpdateTicked -= OnUpdateTicked;
                ModEntry.SHelper.Events.GameLoop.DayStarted   -= OnDayStarted;
            }
            lock (_lock)
            {
                _globalGossip.Clear();
                _playerStateBucket.Clear();
                _activityBucket.Clear();
                _interactedNpcNamesToday.Clear();
                _npcNoticedItemIdsToday.Clear();
            }
        }
        catch (Exception ex)
        {
            ModEntry.SMonitor?.Log($"[PerceptionManager] Cleanup error: {ex.Message}", LogLevel.Debug);
        }
    }

    // ─────────────────────────────────────────────
    //  Public API
    // ─────────────────────────────────────────────

    public void Record(
        string key,
        string template,
        string npcName       = null,
        int    lifetimeHours = 2,
        bool   isGossip      = false,
        bool   isLandmark    = false,
        string itemId        = null,
        string locationName  = null)
    {
        if (!ShouldRecord(key, isGossip || isLandmark)) return;
        if (string.IsNullOrWhiteSpace(template)) return;

        string resolvedLocation = locationName
            ?? Game1.currentLocation?.Name
            ?? string.Empty;

        var entry = new PerceptionEntry
        {
            Key               = key,
            Template          = template,
            NpcName           = npcName ?? string.Empty,
            RecordedTimeOfDay = Game1.timeOfDay,
            LifetimeHours     = lifetimeHours,
            IsGossip          = isGossip,
            IsLandmark        = isLandmark,
            LocationName      = resolvedLocation,
            ItemId            = itemId
        };

        string track;
        lock (_lock)
        {
            if (!string.IsNullOrEmpty(npcName))
            {
                _interactedNpcNamesToday.Add(npcName);
            }

            if (isGossip || isLandmark)   // Landmark 事件自动提升至 Track 1
            {
                EnqueueGossip(entry);
                track = "Gossip";
            }
            else if (key.StartsWith("Player", StringComparison.OrdinalIgnoreCase))
            {
                EnqueueBucket(_playerStateBucket, MaxPlayerStateEntries, entry);
                track = "PlayerState";
            }
            else
            {
                EnqueueBucket(_activityBucket, MaxActivityEntries, entry);
                track = "Activity";
            }
        }

        if (ModEntry.Config?.Debug == true)
        {
            ModEntry.SMonitor?.Log(
                $"[PerceptionManager] [{track}] '{key}' @ {resolvedLocation}: {template}",
                LogLevel.Debug);
        }
    }

    public void Evict(string key, bool fromGossip = false)
    {
        if (string.IsNullOrWhiteSpace(key)) return;

        bool removed;
        lock (_lock)
        {
            if (fromGossip)
            {
                removed = EvictFromQueue(_globalGossip, key);
            }
            else
            {
                bool a = EvictFromQueue(_playerStateBucket, key);
                bool b = EvictFromQueue(_activityBucket, key);
                removed = a || b;
            }
        }

        if (removed && ModEntry.Config?.Debug == true)
        {
            string track = fromGossip ? "gossip" : "farmer bucket";
            ModEntry.SMonitor?.Log(
                $"[PerceptionManager] Evicted key '{key}' from {track}.",
                LogLevel.Debug);
        }
    }

    private static bool EvictFromQueue(Queue<PerceptionEntry> queue, string key)
    {
        if (!queue.Any(e => string.Equals(e.Key, key, StringComparison.OrdinalIgnoreCase)))
            return false;

        var remaining = queue
            .Where(e => !string.Equals(e.Key, key, StringComparison.OrdinalIgnoreCase))
            .ToList();

        queue.Clear();
        foreach (var item in remaining)
            queue.Enqueue(item);

        return true;
    }

    public void RecordGossip(string key, string template, int lifetimeHours = 20)
    {
        Record(
            key:           key,
            template:      template,
            npcName:       null,
            lifetimeHours: lifetimeHours,
            isGossip:      true,
            isLandmark:    false);
    }

    public List<PerceptionEntry> GetGossipSnapshots()
    {
        lock (_lock)
        {
            return _globalGossip
                .Where(IsPerceptionTimeValid)
                .ToList();
        }
    }

    /// <summary>
    /// 标记指定 NPC 今天已注意过某物品，后续再次计算该物品得分时触发疲劳衰减
    /// </summary>
    public void MarkItemNoticedToday(string npcName, string itemId)
    {
        if (string.IsNullOrEmpty(npcName) || string.IsNullOrEmpty(itemId)) return;
        lock (_lock)
        {
            if (!_npcNoticedItemIdsToday.TryGetValue(npcName, out var set))
            {
                set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                _npcNoticedItemIdsToday[npcName] = set;
            }
            set.Add(itemId);
        }
    }

    public bool HasNoticedItemToday(string npcName, string itemId)
    {
        if (string.IsNullOrEmpty(npcName) || string.IsNullOrEmpty(itemId)) return false;
        lock (_lock)
        {
            return _npcNoticedItemIdsToday.TryGetValue(npcName, out var set) && set.Contains(itemId);
        }
    }

    public void ConsumePerceptions(string npcName, IEnumerable<PerceptionEntry> entries)
    {
        if (string.IsNullOrEmpty(npcName) || entries == null) return;
        lock (_lock)
        {
            foreach (var entry in entries)
            {
                entry?.ConsumeFor(npcName);
            }
        }
    }

    public void ConsumePerception(string npcName, PerceptionEntry entry)
    {
        if (string.IsNullOrEmpty(npcName) || entry == null) return;
        lock (_lock)
        {
            entry.ConsumeFor(npcName);
        }
    }

    public void ConsumePerceptions(IEnumerable<PerceptionEntry> entries)
    {
        if (entries == null) return;
        lock (_lock)
        {
            foreach (var entry in entries)
            {
                if (entry != null)
                    entry.IsConsolidated = true;
            }
        }
    }

    /// <summary>
    /// 修复后的会话归档：只归档定向指派给该 NPC 的事件，绝对不误杀未注入的同场景环境事件
    /// </summary>
    public void MarkAsConsolidated(string npcName)
    {
        if (string.IsNullOrEmpty(npcName)) return;

        lock (_lock)
        {
            foreach (var e in _playerStateBucket.Concat(_activityBucket).Concat(_globalGossip))
            {
                if (!string.IsNullOrEmpty(e.NpcName) && e.NpcName.Equals(npcName, StringComparison.OrdinalIgnoreCase))
                {
                    e.ConsumeFor(npcName);
                    e.IsConsolidated = true;
                }
            }
        }
    }

    /// <summary>
    /// 获取通过目击过滤并按突出度排序的感知记录。
    /// 核心逻辑：动作优先保底槽位 + 全员个性化加权 + 疲劳惩罚 + 3.8分封顶截断。
    /// </summary>
    public List<PerceptionEntry> GetFilteredBucketFor(string npcName, int max = 3)
    {
        if (string.IsNullOrEmpty(npcName)) return new List<PerceptionEntry>();

        string npcLocation = GetNpcCurrentLocation(npcName);
        NPC    npc         = GetNpcSafe(npcName);

        lock (_lock)
        {
            var validCandidates = _playerStateBucket
                .Concat(_activityBucket)
                .Concat(_globalGossip)
                .Where(IsPerceptionTimeValid)
                .Where(e => !e.IsConsumedBy(npcName))
                .Where(e => PassesEyewitnessFilter(e, npcName, npcLocation))
                // ★ 已经对该 NPC 产生审美疲劳的随身物品直接剔除候选池，
                //   而不只是在打分阶段降权——避免"看腻了但没有别的可选，
                //   于是还是被塞进 Prompt"的情况。
                .Where(e => !(e.Key == "PlayerActiveItem"
                              && !string.IsNullOrEmpty(e.ItemId)
                              && HasNoticedItemToday(npcName, e.ItemId)))
                .OrderByDescending(e => ComputeSalience(e, npc))
                .ToList();

            if (validCandidates.Count == 0) return new List<PerceptionEntry>();

            var result = new List<PerceptionEntry>();

            // ── 动作保底槽位：若有正在发生的行为动作，至少锁定 1 个最高分名额 ──
            var topAction = validCandidates.FirstOrDefault(e =>
                e.Key is "Fish" or "LegendaryFish" or "Eat" or "Gift" or "Talk" or "Harvest");

            if (topAction != null)
            {
                result.Add(topAction);
            }

            // ── 剩余槽位由其余条目按突出度依序补齐 ──
            foreach (var candidate in validCandidates)
            {
                if (result.Count >= max) break;
                if (!result.Contains(candidate))
                {
                    result.Add(candidate);
                }
            }

            return result;
        }
    }

    public List<PerceptionEntry> GetPerceptionsFor(string npcName, int maxCount = 3)
        => GetFilteredBucketFor(npcName, maxCount);

    public IReadOnlyList<string> GetInteractedNpcNamesToday()
    {
        lock (_lock)
        {
            return _interactedNpcNamesToday.ToList();
        }
    }

    // ─────────────────────────────────────────────
    //  Private: queue management
    // ─────────────────────────────────────────────

    private void EnqueueGossip(PerceptionEntry entry)
    {
        var existing = _globalGossip.FirstOrDefault(e =>
            string.Equals(e.Key, entry.Key, StringComparison.OrdinalIgnoreCase));

        if (existing != null)
        {
            var remaining = _globalGossip.Where(e => e != existing).ToList();
            _globalGossip.Clear();
            foreach (var item in remaining)
                _globalGossip.Enqueue(item);
        }

        while (_globalGossip.Count >= MaxGossipEntries) _globalGossip.Dequeue();
        _globalGossip.Enqueue(entry);
    }

    private static void EnqueueBucket(Queue<PerceptionEntry> bucket, int maxEntries, PerceptionEntry entry)
    {
        bool isPlayerState = entry.Key.StartsWith("Player", StringComparison.OrdinalIgnoreCase);

        PerceptionEntry duplicate = bucket.FirstOrDefault(e =>
            e.Key == entry.Key &&
            (
                isPlayerState
                ||
                (!string.IsNullOrEmpty(e.NpcName) && !string.IsNullOrEmpty(entry.NpcName)
                    && string.Equals(e.NpcName, entry.NpcName, StringComparison.OrdinalIgnoreCase))
                ||
                (string.IsNullOrEmpty(e.NpcName)
                    && string.IsNullOrEmpty(entry.NpcName)
                    && !string.IsNullOrEmpty(e.LocationName)
                    && !string.IsNullOrEmpty(entry.LocationName)
                    && string.Equals(e.LocationName, entry.LocationName, StringComparison.OrdinalIgnoreCase))
            ));

        if (duplicate != null)
        {
            var remaining = bucket.Where(e => e != duplicate).ToList();
            bucket.Clear();
            foreach (var item in remaining)
                bucket.Enqueue(item);
        }

        while (bucket.Count >= maxEntries)
            bucket.Dequeue();

        bucket.Enqueue(entry);
    }

    // ─────────────────────────────────────────────
    //  Private: salience scoring
    // ─────────────────────────────────────────────

    private static float GetBasePriority(PerceptionEntry entry)
    {
        if (entry.IsLandmark) return 10f;

        return entry.Key switch
        {
            // ── Tier 1: 危机与特殊标志性装束 ──
            "LegendaryFish"              => 10f,
            "PlayerSpecialOutfit_Shorts" => 9.0f,
            "Gift"                       => 8.5f,
            "PlayerLowHealth"            => 7.5f,
            "PlayerExhausted"            => 7.0f,
            "PlayerFainted"              => 7.0f,

            // ── Tier 2: 即时行为与状态（中高基准，由人设决定是否冲顶） ──
            "Fish"                       => 5.5f,
            "Eat"                        => 5.5f,
            "Talk"                       => 5.5f,
            "Harvest"                    => 5.0f,
            "PlayerHasPendant"           => 5.0f,
            "PlayerWeddingOutfit"        => 5.0f,
            "PlayerDrunk"                => 4.5f,

            // ── Tier 3: 伴随环境与轻度状态 ──
            "PlayerTired"                => 3.5f,
            "PlayerPet"                  => 3.0f,
            "PlayerHorseNearby"          => 3.0f,
            "PlayerRidingHorse"          => 3.0f,
            "PlayerHat"                  => 3.0f,
            "Chop"                       => 2.5f,
            "Place"                      => 2.5f,

            // ── Tier 4: 底层随身物与背包（背景附带） ──
            "PlayerBagFull"              => 2.0f,
            "PlayerActiveItem"           => 1.8f,
            _                            => 1.5f
        };
    }

    private static float ComputeSalience(PerceptionEntry entry, NPC npc)
    {
        float base_ = GetBasePriority(entry);

        float decay = 1f;
        if (entry.LifetimeHours < 20)
        {
            int lifetimeMins = entry.LifetimeHours * 60;
            if (lifetimeMins <= 0) return 0f;

            int elapsedMins = GetInGameMinutes(Game1.timeOfDay)
                            - GetInGameMinutes(entry.RecordedTimeOfDay);

            decay = Math.Clamp(1f - (float)elapsedMins / lifetimeMins, 0f, 1f);
        }

        float personality = npc != null ? GetPersonalityMultiplier(entry, npc) : 1f;

        // ★ 审美疲劳机制：如果 NPC 当天已经注意过该物品，注意力衰减 90%
        if (entry.Key == "PlayerActiveItem" && npc != null && !string.IsNullOrEmpty(entry.ItemId))
        {
            if (Instance.HasNoticedItemToday(npc.Name, entry.ItemId))
            {
                personality *= 0.1f;
            }
        }

        float finalScore = base_ * decay * personality;

        // ★ 绝对天花板封顶（Hard Cap）：死物/随身物无论乘数多高，最终得分严禁超过 3.8 分
        // 彻底杜绝背包物品反超残血、力竭、醉酒和刚刚完成的钓鱼/进食动作
        if (entry.Key is "PlayerActiveItem" or "PlayerBagFull" or "PlayerHat")
        {
            finalScore = Math.Min(finalScore, 3.8f);
        }

        return finalScore;
    }

    /// <summary>
    /// 星露谷原版全村民个性化关注度矩阵
    /// </summary>
    /// <summary>
    /// 星露谷原版全村民个性化关注度矩阵
    /// </summary>
    /// <summary>
    /// 星露谷原版全村民个性化关注度矩阵
    /// </summary>
    private static float GetPersonalityMultiplier(PerceptionEntry entry, NPC npc)
    {
        if (npc == null) return 1.0f;

        string name = npc.Name;
        string key  = entry.Key;
        string qId  = entry.ItemId ?? string.Empty;

        int itemCategory = 0;
        if (!string.IsNullOrEmpty(qId))
        {
            try
            {
                var parsedItem = ItemRegistry.Create(qId, allowNull: true);
                if (parsedItem != null) itemCategory = parsedItem.Category;
            }
            catch { }
        }

        const int JunkCategoryInt = -20;
        bool isWoodOrStone   = itemCategory == BuildingResourcesCategory || qId is "(O)388" or "(O)709" or "(O)390";
        bool isMineralOrOre  = itemCategory == StardewValley.Object.GemCategory 
                            || itemCategory == StardewValley.Object.mineralsCategory 
                            || itemCategory == StardewValley.Object.metalResources;
        bool isFishItem      = itemCategory == StardewValley.Object.FishCategory;
        bool isCookingOrFood = itemCategory == StardewValley.Object.CookingCategory;

        switch (name)
        {
            // ── 1. 渔夫与海滩 ──
            case "Willy":
                if (key is "Fish" or "LegendaryFish") return 2.5f;
                if (key == "PlayerActiveItem" && isFishItem) return 2.2f;
                break;

            case "Elliott":
                if (key == "Fish") return 1.5f;
                if (key == "PlayerActiveItem" && qId is "(O)444" or "(O)637" or "(O)814") return 2.0f;
                break;

            // ── 2. 工匠与资源 ──
            case "Clint":
                if (key == "PlayerActiveItem" && (isMineralOrOre || qId.Contains("Geode") || qId is "(O)535" or "(O)536" or "(O)537")) return 2.5f;
                if (key == "Chop") return 0.3f;
                break;

            case "Robin":
                if (key == "PlayerActiveItem" && (isWoodOrStone || qId is "(O)388" or "(O)709" or "(O)390")) return 2.5f;
                if (key is "Chop" or "Place") return 1.5f;
                break;

            // ── 3. 诊所与健康 ──
            case "Harvey":
                if (key is "PlayerLowHealth" or "PlayerExhausted" or "PlayerDrunk") return 2.5f;
                if (key is "PlayerTired" or "PlayerGarlicSmell") return 2.0f;
                if (key == "Eat") return 1.8f;
                break;

            // ── 4. 酒吧与餐饮 ──
            case "Gus":
                if (key == "Eat") return 2.5f;
                if (key == "PlayerActiveItem" && (isCookingOrFood || qId is "(O)346" or "(O)303")) return 2.2f;
                if (key == "PlayerDrunk") return 2.0f;
                break;

            // ── 5. 自然与荒野 ──
            case "Linus":
                if (key == "Chop") return 0.1f;
                if (key is "Eat" or "Harvest") return 2.0f;
                if (key == "Fish") return 1.6f;
                if (key == "PlayerActiveItem" && itemCategory == -81) return 2.2f;
                break;

            case "Leah":
                if (key == "Chop") return 0.2f;
                if (key is "Harvest" or "Eat") return 1.6f;
                if (key == "PlayerActiveItem" && (isWoodOrStone || qId is "(O)169" or "(O)709")) return 2.2f;
                break;

            // ── 6. 商业与政务 ──
            case "Pierre":
                if (key == "Harvest") return 2.2f;
                if (key == "PlayerActiveItem" && (itemCategory is StardewValley.Object.VegetableCategory or StardewValley.Object.FruitsCategory)) return 2.2f;
                if (key == "PlayerBagFull") return 1.8f;
                break;

            case "Marnie":
                if (key is "PlayerPet" or "PlayerHorseNearby" or "PlayerRidingHorse") return 2.5f;
                if (key == "PlayerActiveItem" && (itemCategory is StardewValley.Object.EggCategory or StardewValley.Object.MilkCategory)) return 2.2f;
                break;

            case "Lewis":
                if (key == "PlayerSpecialOutfit_Shorts") return 5.0f;
                if (key == "PlayerHasPendant") return 2.2f;
                if (key == "Harvest") return 1.8f;
                break;

            // ── 7. 魔法与科学 ──
            case "Demetrius":
                if (key == "PlayerActiveItem" && (isFishItem || qId is "(O)107" or "(O)766" or "(O)768")) return 2.5f;
                break;

            case "Maru":
                if (key == "PlayerGlowing") return 2.2f;
                if (key == "PlayerActiveItem" && (isMineralOrOre || qId is "(O)787" or "(O)338")) return 2.2f;
                break;

            case "Wizard":
                if (key is "PlayerGlowing" or "PlayerMonsterMusk") return 2.5f;
                if (key == "PlayerActiveItem" && (qId is "(O)74" or "(O)768" or "(O)769" or "(O)126")) return 3.0f;
                break;

            case "Krobus":
                if (key == "PlayerGlowing") return 0.2f;
                if (key == "PlayerMonsterMusk") return 2.5f;
                if (key == "PlayerActiveItem" && qId is "(O)769" or "(O)305") return 2.5f;
                break;

            // ── 8. 可恋爱青年男女 ──
            case "Abigail":
                if (key == "PlayerActiveItem" && (qId == "(O)66" || qId.Contains("Sword"))) return 2.5f;
                if (key == "PlayerLowHealth") return 1.8f;
                break;

            case "Emily":
                if (key is "PlayerWeddingOutfit" or "PlayerGlowing") return 2.2f;
                if (key == "PlayerActiveItem" && (isMineralOrOre || qId is "(O)428" or "(O)440")) return 2.5f;
                break;

            case "Haley":
                if (key is "Fish" or "Chop") return 0.2f;
                if (key is "PlayerHat" or "PlayerWeddingOutfit") return 2.2f;
                if (key == "PlayerActiveItem" && qId == "(O)421") return 2.5f;
                if (key == "PlayerActiveItem" && (itemCategory == JunkCategoryInt || qId == "(O)168")) return 2.5f;
                break;

            case "Penny":
                if (key == "PlayerDrunk") return 2.5f;
                if (key is "PlayerExhausted" or "PlayerTired") return 2.0f;
                if (key == "PlayerActiveItem" && (qId is "(O)102" or "(O)797")) return 2.0f;
                break;

            case "Shane":
                if (key is "PlayerDrunk" or "PlayerExhausted") return 2.2f;
                if (key == "PlayerActiveItem" && qId is "(O)346" or "(O)302" or "(O)167") return 2.2f;
                if (key == "PlayerPet") return 1.8f;
                break;

            case "Sebastian":
                if (key == "Talk") return 0.3f;
                if (key is "PlayerLateNight" or "PlayerSpeedBuff") return 2.0f;
                if (key == "PlayerActiveItem" && qId is "(O)569" or "(O)769" or "(O)227") return 2.2f;
                break;

            case "Alex":
                if (key is "PlayerSpeedBuff" or "PlayerExhausted") return 2.2f;
                if (key == "PlayerActiveItem" && qId is "(O)195" or "(O)201") return 2.0f;
                break;

            case "Sam":
                if (key is "PlayerSpeedBuff" or "PlayerDrunk") return 1.8f;
                if (key == "PlayerActiveItem" && qId is "(O)206" or "(O)167") return 2.0f;
                break;

            // ── 9. 长辈村民 ──
            case "George":
                if (key is "PlayerSpeedBuff" or "PlayerLateNight") return 2.0f;
                if (key == "PlayerSpecialOutfit_Shorts") return 2.2f;
                break;

            case "Evelyn":
                if (key is "PlayerExhausted" or "PlayerTired") return 2.2f;
                if (key == "PlayerActiveItem" && (qId is "(O)223" or "(O)591" or "(O)593")) return 2.2f;
                break;
        }

        return 1.0f;
    }

    // ─────────────────────────────────────────────
    //  Private: filter & validity
    // ─────────────────────────────────────────────

    private static bool PassesEyewitnessFilter(
        PerceptionEntry entry, string npcName, string npcLocation)
    {
        if (entry.IsLandmark) return true;
        if (entry.Key.StartsWith("Player", StringComparison.OrdinalIgnoreCase)) return true;

        if (entry.Key == "Talk")
        {
            return !string.IsNullOrEmpty(entry.NpcName) &&
                   entry.NpcName.Equals(npcName, StringComparison.OrdinalIgnoreCase);
        }

        if (!string.IsNullOrEmpty(entry.NpcName) &&
            entry.NpcName.Equals(npcName, StringComparison.OrdinalIgnoreCase))
            return true;

        if (!string.IsNullOrEmpty(entry.LocationName) &&
            !string.IsNullOrEmpty(npcLocation) &&
            entry.LocationName.Equals(npcLocation, StringComparison.OrdinalIgnoreCase))
            return true;

        return false;
    }

    private static NPC GetNpcSafe(string npcName)
    {
        try { return Game1.getCharacterFromName(npcName); }
        catch { return null; }
    }

    private static string GetNpcCurrentLocation(string npcName)
    {
        try { return Game1.getCharacterFromName(npcName)?.currentLocation?.Name ?? string.Empty; }
        catch { return string.Empty; }
    }

    private static void PurgeExpired(Queue<PerceptionEntry> queue)
    {
        if (!queue.Any(p => !IsPerceptionTimeValid(p))) return;

        var valid = queue.Where(IsPerceptionTimeValid).ToList();
        queue.Clear();
        foreach (var item in valid) queue.Enqueue(item);
    }

    private static int GetInGameMinutes(int timeOfDay)
        => (timeOfDay / 100) * 60 + (timeOfDay % 100);

    private static bool IsPerceptionTimeValid(PerceptionEntry entry)
    {
        if (entry == null) return false;
        if (entry.LifetimeHours >= 20) return true;

        int currentMins  = GetInGameMinutes(Game1.timeOfDay);
        int recordedMins = GetInGameMinutes(entry.RecordedTimeOfDay);

        if (currentMins < recordedMins) return false;
        return (currentMins - recordedMins) <= (entry.LifetimeHours * 60);
    }

    private static bool ShouldRecord(string key, bool isGossipOrLandmark)
    {
        if (!ModEntry.Config.EnablePerceptionSystem) return false;
        if (isGossipOrLandmark) return true;

        return key switch
        {
            "Eat"           => ModEntry.Config.EnablePerceptionEat,
            "Fish"          => ModEntry.Config.EnablePerceptionFish,
            "LegendaryFish" => ModEntry.Config.EnablePerceptionFish,
            "Chop"          => ModEntry.Config.EnablePerceptionChop,
            "Place"         => ModEntry.Config.EnablePerceptionPlace,
            "Talk"          => ModEntry.Config.EnableNearbyPerception,
            "Harvest"       => ModEntry.Config.EnablePerceptionHarvest,
            "Gift"          => ModEntry.Config.EnablePerceptionGift,
            _               => true
        };
    }

    // ─────────────────────────────────────────────
    //  Utility
    // ─────────────────────────────────────────────

    internal static string PickVariant(string[] variants)
    {
        if (variants == null || variants.Length == 0) return string.Empty;
        if (variants.Length == 1) return variants[0];
        return variants[Game1.random.Next(variants.Length)];
    }

    // ─────────────────────────────────────────────
    //  Event callbacks
    // ─────────────────────────────────────────────

    private void OnDayStarted(object sender, DayStartedEventArgs e)
    {
        lock (_lock)
        {
            _playerStateBucket.Clear();
            _activityBucket.Clear();
            _globalGossip.Clear();
            _interactedNpcNamesToday.Clear();
            _npcNoticedItemIdsToday.Clear(); // 跨天重置审美疲劳
        }

        PerceptionInjector.ResetMentionedGossipKeys();

        if (ModEntry.Config?.Debug == true)
            ModEntry.SMonitor?.Log(
                "[PerceptionManager] Farmer buckets, gossip and today interactions cleared on new day.",
                LogLevel.Debug);
    }

    private void OnUpdateTicked(object sender, UpdateTickedEventArgs e)
    {
        if (!e.IsMultipleOf(60)) return;

        lock (_lock)
        {
            PurgeExpired(_playerStateBucket);
            PurgeExpired(_activityBucket);
            PurgeExpired(_globalGossip);
        }
    }
}