using System;
using System.Collections.Generic;
using System.Linq;
using StardewModdingAPI;
using StardewValley;

namespace ValleytalkReborn
{
    /// <summary>
    /// 配偶日程规划算法引擎。
    /// 负责偏好权重解析、加权随机选点、疲劳度衰减计算以及错峰出发时间分配。
    /// </summary>
    public class SchedulePlanner
    {
        public const string PREF_ASSET_KEY = "ValleytalkReborn/NpcPreferences";

        private readonly IModHelper _helper;
        private Dictionary<string, NpcPreference> _npcPreferences = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Queue<string>> _recentPoiHistory = new(StringComparer.OrdinalIgnoreCase);
        private bool _prefsLoaded = false;

        public SchedulePlanner(IModHelper helper)
        {
            _helper = helper ?? throw new ArgumentNullException(nameof(helper));
        }

        public bool IsLoaded => _prefsLoaded;
        public int TotalPrefCount => _npcPreferences.Count;

        /// <summary>加载偏好资产。</summary>
        public void LoadPreferences()
        {
            try
            {
                _npcPreferences = _helper.GameContent.Load<Dictionary<string, NpcPreference>>(PREF_ASSET_KEY)
                    ?? new(StringComparer.OrdinalIgnoreCase);

                _prefsLoaded = true;
                ModEntry.SMonitor?.Log(
                    $"[Planner] Loaded {_npcPreferences.Count} NPC preferences.",
                    LogLevel.Info);
            }
            catch (Exception ex)
            {
                ModEntry.SMonitor?.Log($"[Planner] Preference load failed: {ex.Message}", LogLevel.Error);
                _npcPreferences = new(StringComparer.OrdinalIgnoreCase);
                _prefsLoaded = false;
            }
        }

        /// <summary>热重载偏好资产。</summary>
        public void ReloadPreferences()
        {
            _prefsLoaded = false;
            LoadPreferences();
        }

        /// <summary>
        /// 为指定 NPC 规划日程条目列表（加权选点 + 疲劳度衰减 + 错峰时间分配）。
        /// 算法与原 CSM.PickPoisWeighted 完全一致。
        /// </summary>
        public List<ScheduledPoiEntry> BuildSchedule(
            Dictionary<string, PoiAsset> legalPois,
            string npcName,
            int baseSlot,
            HashSet<int> usedDepartureTimes)
        {
            if (!_prefsLoaded)
                LoadPreferences();

            if (legalPois == null || legalPois.Count == 0)
                return new List<ScheduledPoiEntry>();

            _npcPreferences.TryGetValue(npcName, out var prefs);
            var baseWeights = prefs?.PreferredPois
                .ToDictionary(p => p.PoiId, p => p.Weight, StringComparer.OrdinalIgnoreCase)
                ?? new(StringComparer.OrdinalIgnoreCase);

            _recentPoiHistory.TryGetValue(npcName, out var historyQueue);
            var recentList = historyQueue?.ToList() ?? new List<string>();

            int currentSlot = baseSlot;
            int desired    = Game1.random.Next(2, 4);            // 偏好 2–3 个节点
            int poolFloor  = Math.Min(2, legalPois.Count);       // 池级保底：单 POI 池自动降为 1
            int pickCount  = Math.Max(poolFloor, Math.Min(desired, legalPois.Count));
            var entries = new List<ScheduledPoiEntry>();
            var remainingPois = new Dictionary<string, PoiAsset>(legalPois, StringComparer.OrdinalIgnoreCase);

            for (int i = 0; i < pickCount; i++)
            {
                if (currentSlot >= 1800 || remainingPois.Count == 0) break;

                // 仅保留当前时刻后至少拥有 60 分钟窗口的 POI
                var validCandidates = remainingPois.Where(kv =>
                {
                    var cond = kv.Value.Conditions ?? new PoiConditions();
                    int latest = cond.TimeRange?.Count == 2 ? cond.TimeRange[1] : 1900;
                    return latest >= currentSlot + 60;
                }).ToList();

                if (validCandidates.Count == 0) break;

                // 动态疲劳度衰减计算
                var weightedPool = validCandidates.Select(kv =>
                {
                    int w = baseWeights.TryGetValue(kv.Key, out int customWeight) ? customWeight : 50;

                    int recentIndex = recentList.LastIndexOf(kv.Key);
                    if (recentIndex != -1)
                    {
                        int stepsBack = recentList.Count - 1 - recentIndex;
                        float penalty = stepsBack switch
                        {
                            0 => 0.25f, // 昨天刚去过
                            1 => 0.55f, // 前天去过
                            _ => 0.80f
                        };
                        w = Math.Max(5, (int)(w * penalty));
                    }
                    return (PoiId: kv.Key, Asset: kv.Value, Weight: w);
                }).ToList();

                int totalWeight = weightedPool.Sum(c => c.Weight);
                if (totalWeight <= 0) break;

                int roll = Game1.random.Next(totalWeight);
                int acc = 0;
                (string PoiId, PoiAsset Asset) selected = default;
                foreach (var candidate in weightedPool)
                {
                    acc += candidate.Weight;
                    if (roll < acc)
                    {
                        selected = (candidate.PoiId, candidate.Asset);
                        break;
                    }
                }

                if (selected.PoiId == null) break;

                var cond = selected.Asset.Conditions ?? new PoiConditions();
                int earliest = cond.TimeRange?.Count == 2 ? cond.TimeRange[0] : 700;
                int latest   = cond.TimeRange?.Count == 2 ? cond.TimeRange[1] : 1900;

                int depart = AllocateUniqueDepartureTime(currentSlot, earliest, latest, usedDepartureTimes);
                int stay = selected.Asset.StayMinutes > 0 ? selected.Asset.StayMinutes : 90;

                entries.Add(new ScheduledPoiEntry
                {
                    PoiId         = selected.PoiId,
                    Asset         = selected.Asset,
                    DepartureTime = depart,
                    StayMinutes   = stay
                });

                remainingPois.Remove(selected.PoiId);
                currentSlot = MovementPathfinding.SafeAddGameTime(depart, stay + Game1.random.Next(2, 5) * 10);
            }

            ModEntry.SMonitor?.Log(
                $"[Planner] {npcName}: planned {entries.Count}/{pickCount} entries (pool={legalPois.Count}, baseSlot={currentSlot})",
                LogLevel.Debug);
            entries.Sort((a, b) => a.DepartureTime.CompareTo(b.DepartureTime));
            return entries;
        }

        /// <summary>全局出发时间错峰算法。</summary>
        public static int AllocateUniqueDepartureTime(
            int desired,
            int earliest,
            int latest,
            HashSet<int> usedTimes)
        {
            int time = Math.Max(earliest, Math.Min(latest, desired));

            for (int i = 0; i < 120; i++)
            {
                int candidate = time + i * 10;
                if (candidate > latest || candidate >= 2000)
                    break;

                if (!usedTimes.Contains(candidate))
                {
                    usedTimes.Add(candidate);
                    return candidate;
                }
            }

            for (int i = 1; i < 120; i++)
            {
                int candidate = time - i * 10;
                if (candidate < earliest)
                    break;

                if (!usedTimes.Contains(candidate))
                {
                    usedTimes.Add(candidate);
                    return candidate;
                }
            }

            return time;
        }

        // ══════════════════════════════════════════════════════════════
        //  疲劳度历史管理
        // ══════════════════════════════════════════════════════════════

        /// <summary>在换日时记录当天已执行的 POI 历史（每个 NPC 保留最近 6 个）。</summary>
        public void RecordExecutedHistory(string npcName, IEnumerable<ScheduledPoiEntry> executedEntries)
        {
            if (string.IsNullOrWhiteSpace(npcName) || executedEntries == null) return;

            if (!_recentPoiHistory.TryGetValue(npcName, out var q))
            {
                q = new Queue<string>();
                _recentPoiHistory[npcName] = q;
            }

            foreach (var entry in executedEntries.Where(en => en.Executed && en.PoiId != null))
            {
                q.Enqueue(entry.PoiId);
            }

            while (q.Count > 6) q.Dequeue();
        }

        /// <summary>清理不再处于合法婚姻关系的 NPC 历史。</summary>
        public void CleanupStaleHistory(Func<string, bool> isLegalSpousePredicate)
        {
            if (isLegalSpousePredicate == null) return;

            var staleKeys = _recentPoiHistory.Keys
                .Where(name => !isLegalSpousePredicate(name))
                .ToList();

            foreach (var key in staleKeys)
                _recentPoiHistory.Remove(key);
        }

        public void ClearHistory()
        {
            _recentPoiHistory.Clear();
        }
    }
}
