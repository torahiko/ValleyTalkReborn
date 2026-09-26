using System;
using System.Collections.Generic;
using System.Linq;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

namespace ValleytalkReborn
{
    /// <summary>
    /// 社区家务账目管理器：从 JSON 资产加载家务条目，按当前季节/星期构建内存调度索引，
    /// 并为对话管线提供确定性的双面话题查询。
    ///
    /// 纯内存态：所有状态成员（条目、索引、标志、服务引用）均不进 SaveData/ModData，
    /// 返回标题或 Cleanup 时全部清除。单机模式专用，多人模式下 TryGetTopic 直接返回 false。
    /// </summary>
    internal static class CommunityChoreLedger
    {
        /// <summary>社区家务账目资产键（低优先级，允许外部资产提供者覆写）。</summary>
        public const string ASSET_KEY = "ValleytalkReborn/ChoreLedgerData";

        private static IModHelper _helper;
        private static IMonitor _monitor;

        /// <summary>已通过结构校验的账目条目集合（内存态）。</summary>
        private static List<ChoreLedgerEntry> _entries = new();

        /// <summary>当前季节/星期下，NPC 内部名 → 胜出条目的调度索引（内存态）。</summary>
        private static Dictionary<string, ChoreLedgerEntry> _entriesByNpcAndSchedule = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>是否已通过 SaveLoaded 生命周期（内存态）。</summary>
        private static bool _isSaveLoaded;

        /// <summary>
        /// 初始化：注销旧处理器、保存服务引用、订阅所需生命周期事件。
        /// 幂等——重复调用会先 Cleanup 再重新订阅。
        /// </summary>
        internal static void Initialize(IModHelper helper, IMonitor monitor)
        {
            Cleanup();

            _helper = helper;
            _monitor = monitor;

            helper.Events.GameLoop.SaveLoaded += HandleSaveLoaded;
            helper.Events.GameLoop.DayStarted += HandleDayStarted;
            helper.Events.GameLoop.ReturnedToTitle += HandleReturnedToTitle;
        }

        /// <summary>
        /// 清理全部内存态：条目、调度索引、标志、服务引用，并注销生命周期处理器。
        /// </summary>
        internal static void Cleanup()
        {
            if (_helper != null)
            {
                _helper.Events.GameLoop.SaveLoaded -= HandleSaveLoaded;
                _helper.Events.GameLoop.DayStarted -= HandleDayStarted;
                _helper.Events.GameLoop.ReturnedToTitle -= HandleReturnedToTitle;
                _helper = null;
            }

            _monitor = null;
            _entries = new();
            _entriesByNpcAndSchedule = new(StringComparer.OrdinalIgnoreCase);
            _isSaveLoaded = false;
        }

        /// <summary>
        /// 热路径：按 NPC 内部名查找当天生效的家务话题。
        /// 不做 JSON 解析、不做全配置遍历、不做 LLM/文件 IO、不分配 Task。
        /// </summary>
        internal static bool TryGetTopic(string npcName, out string topicText)
        {
            topicText = null;

            if (string.IsNullOrWhiteSpace(npcName))
                return false;

            if (Context.IsMultiplayer)
                return false;

            if (!_isSaveLoaded || !Context.IsWorldReady)
                return false;

            if (!_entriesByNpcAndSchedule.TryGetValue(npcName, out var entry))
                return false;

            bool isSource = string.Equals(entry.SourceNpc, npcName, StringComparison.Ordinal);
            bool isTarget = string.Equals(entry.TargetNpc, npcName, StringComparison.Ordinal);

            ChoreTopic topic;
            if (isSource)
                topic = entry.SourceTopic;
            else if (isTarget)
                topic = entry.TargetTopic;
            else
            {
                _monitor?.Log($"[CommunityChoreLedger] Invariant violation: NPC '{npcName}' matched entry ChoreId='{entry.ChoreId}' but is neither Source nor Target.", LogLevel.Error);
                return false;
            }

            if (topic == null || string.IsNullOrEmpty(topic.InnerMotivation) || string.IsNullOrEmpty(topic.PublicOpinion))
            {
                _monitor?.Log($"[CommunityChoreLedger] Invariant violation: ChoreId='{entry.ChoreId}' NPC '{npcName}' selected topic is missing or empty.", LogLevel.Error);
                return false;
            }

            topicText = $"[Community Chore] Motivation: {topic.InnerMotivation} Public opinion: {topic.PublicOpinion}";
            return true;
        }

        /// <summary>
        /// 生命周期刷新点：根据当前季节与星期重建内存调度索引。
        /// </summary>
        internal static void OnDayStarted()
        {
            _entriesByNpcAndSchedule = new(StringComparer.OrdinalIgnoreCase);

            if (Context.IsMultiplayer || !Context.IsWorldReady)
                return;

            string currentSeason = Game1.currentSeason;
            // ★ Game1.dayOfWeek 已在 1.6 移除：星露谷历法每月固定 28 天且 1 号恒为周一，
            // 故 (dayOfMonth-1)%7+1 在 [1,7] 闭区间内与星期一~周日数学恒等，零分配纯整型运算。
            int currentDayOfWeek = ((Game1.dayOfMonth - 1) % 7) + 1;

            if (string.IsNullOrEmpty(currentSeason))
                return;

            // 按 NPC 内部名收集当天候选条目；同一 NPC 多个候选时取序号最小 ChoreId。
            var candidates = new Dictionary<string, List<ChoreLedgerEntry>>(StringComparer.OrdinalIgnoreCase);

            foreach (var entry in _entries)
            {
                if (entry == null)
                    continue;

                if (!IsScheduleEligible(entry, currentSeason, currentDayOfWeek))
                    continue;

                AddCandidate(candidates, entry.SourceNpc, entry);
                AddCandidate(candidates, entry.TargetNpc, entry);
            }

            foreach (var npcName in candidates.Keys)
            {
                var list = candidates[npcName];

                if (list.Count > 1)
                {
                    list.Sort((a, b) => string.Compare(a.ChoreId, b.ChoreId, StringComparison.Ordinal));
                    string allIds = string.Join(",", list.Select(e => e.ChoreId));
                    _monitor?.Log($"[CommunityChoreLedger] NPC '{npcName}' has {list.Count} candidate ChoreIds ({allIds}); selected smallest '{list[0].ChoreId}'.", LogLevel.Warn);
                }

                _entriesByNpcAndSchedule[npcName] = list[0];
            }
        }

        /// <summary>
        /// 从 SMAPI 内容 API 重新加载并校验账目资产，原子替换内存配置。
        /// </summary>
        internal static void ReloadAsset()
        {
            _entries = new();
            _entriesByNpcAndSchedule = new(StringComparer.OrdinalIgnoreCase);

            List<ChoreLedgerEntry> loaded;
            try
            {
                loaded = _helper.GameContent.Load<List<ChoreLedgerEntry>>(ASSET_KEY);
            }
            catch (Exception ex)
            {
                _monitor?.Log($"[CommunityChoreLedger] Failed to load asset '{ASSET_KEY}': {ex.Message}", LogLevel.Error);
                return;
            }

            if (loaded == null || loaded.Count == 0)
            {
                _monitor?.Log($"[CommunityChoreLedger] Asset '{ASSET_KEY}' returned no entries.", LogLevel.Warn);
                return;
            }

            foreach (var entry in loaded)
            {
                if (entry == null)
                    continue;

                string reason;
                if (!TryValidateEntry(entry, out reason))
                {
                    _monitor?.Log($"[CommunityChoreLedger] Excluding entry ChoreId='{entry.ChoreId ?? "(null)"}': {reason}", LogLevel.Error);
                    continue;
                }

                _entries.Add(entry);
            }
        }

        /// <summary>
        /// 校验单条账目的结构合法性。返回 false 时通过 out reason 给出原因（供 Error 日志）。
        /// </summary>
        private static bool TryValidateEntry(ChoreLedgerEntry entry, out string reason)
        {
            reason = null;

            if (string.IsNullOrEmpty(entry.ChoreId))
            {
                reason = "ChoreId is empty.";
                return false;
            }

            if (entry.DayOfWeek < 1 || entry.DayOfWeek > 7)
            {
                reason = $"DayOfWeek {entry.DayOfWeek} is outside valid range 1..7.";
                return false;
            }

            if (string.IsNullOrEmpty(entry.SourceNpc) || string.IsNullOrEmpty(entry.TargetNpc))
            {
                reason = "SourceNpc or TargetNpc is empty.";
                return false;
            }

            if (string.Equals(entry.SourceNpc, entry.TargetNpc, StringComparison.Ordinal))
            {
                reason = $"SourceNpc equals TargetNpc ('{entry.SourceNpc}').";
                return false;
            }

            if (entry.SourceTopic == null
                || string.IsNullOrEmpty(entry.SourceTopic.InnerMotivation)
                || string.IsNullOrEmpty(entry.SourceTopic.PublicOpinion))
            {
                reason = "SourceTopic is missing or has empty text.";
                return false;
            }

            if (entry.TargetTopic == null
                || string.IsNullOrEmpty(entry.TargetTopic.InnerMotivation)
                || string.IsNullOrEmpty(entry.TargetTopic.PublicOpinion))
            {
                reason = "TargetTopic is missing or has empty text.";
                return false;
            }

            return true;
        }

        /// <summary>
        /// 判断某条目是否符合当天的世界条件（季节、星期、话题非空）。
        /// </summary>
        private static bool IsScheduleEligible(ChoreLedgerEntry entry, string currentSeason, int currentDayOfWeek)
        {
            if (entry.ApplicableSeasons == null)
                return false;

            bool seasonMatch = false;
            foreach (var s in entry.ApplicableSeasons)
            {
                if (!string.IsNullOrEmpty(s) && string.Equals(s, currentSeason, StringComparison.OrdinalIgnoreCase))
                {
                    seasonMatch = true;
                    break;
                }
            }

            if (!seasonMatch)
                return false;

            if (entry.DayOfWeek != currentDayOfWeek)
                return false;

            if (entry.SourceTopic == null
                || string.IsNullOrEmpty(entry.SourceTopic.InnerMotivation)
                || string.IsNullOrEmpty(entry.SourceTopic.PublicOpinion))
                return false;

            if (entry.TargetTopic == null
                || string.IsNullOrEmpty(entry.TargetTopic.InnerMotivation)
                || string.IsNullOrEmpty(entry.TargetTopic.PublicOpinion))
                return false;

            return true;
        }

        private static void AddCandidate(Dictionary<string, List<ChoreLedgerEntry>> candidates, string npcName, ChoreLedgerEntry entry)
        {
            if (!candidates.TryGetValue(npcName, out var list))
            {
                list = new();
                candidates[npcName] = list;
            }
            list.Add(entry);
        }

        private static void HandleSaveLoaded(object sender, SaveLoadedEventArgs e)
        {
            _isSaveLoaded = true;
            ReloadAsset();
        }

        private static void HandleDayStarted(object sender, DayStartedEventArgs e)
        {
            OnDayStarted();
        }

        private static void HandleReturnedToTitle(object sender, ReturnedToTitleEventArgs e)
        {
            Cleanup();
        }
    }
}
