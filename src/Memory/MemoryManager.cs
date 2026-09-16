using System;
using System.Collections.Generic;
using System.Linq;
using StardewModdingAPI;
using StardewValley;

namespace ValleytalkReborn;

public enum MemoryType
{
    Fact,
    Promise
}

public enum MemoryCategory
{
    Address,
    Behavior,
    Fact
}

public enum MemoryTier
{
    Daily = 0,
    Weekly = 1,
    Chronicle = 2
}

public class MemoryEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string NpcName { get; set; } = "";
    public string Content { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public string Source { get; set; } = "Manual";
    public MemoryCategory Category { get; set; } = MemoryCategory.Behavior;

    // ── 1.6 分级记忆扩展（旧字段全保留，反序列化兼容）──
    public MemoryType Type { get; set; } = MemoryType.Fact;
    public int Importance { get; set; } = 3;          // 写入时 clamp 到 1..5
    public int CreatedDay { get; set; }               // = Game1.Date.TotalDays；0 表示"未知（旧数据）"
    public int ExpireDay { get; set; } = -1;          // -1 永久；Promise 默认 CreatedDay+7
    public string TriggerLocation { get; set; } = "";
    public string TargetDayHint { get; set; } = "";   // Promise 专用：LLM 原文如 "Weekend"
    public bool IsFulfilled { get; set; } = false;    // Promise 专用
    public int LastPromptedDay { get; set; } = -1;    // 保留字段：旧档兼容；CORE-MEM-102 分层重写后不再读写
    public DateTime ArchivedAt { get; set; } = default; // 归档时刻；default=从未归档（CORE-MEM-101）

    // ── Timeline 分层（FEAT-MEM-300-T1）──
    public MemoryTier Tier { get; set; } = MemoryTier.Daily;
    public string DateLabel { get; set; } = "";   // 游戏内日历戳，如 "[Y1 春 7日]"，以入库时所在页面日期为准
}

public enum MemoryOperationResult
{
    Success,
    Duplicate,
    TooLong,
    CapacityFull,
    NotFound
}

internal class MemoryManager : IMemoryProvider
{
    public static readonly MemoryManager Instance = new MemoryManager();

    private const string SaveDataKey         = "valleytalk.npc-memories";
    private const string CallsignSaveDataKey = "valleytalk.npc-callsigns";
    private const string ArchiveSaveDataKey = "valleytalk.npc-archived-memories";
    private const string CategoryMigrationFlagKey = "valleytalk.memory-category-migrated";
    private const string TimelineSaveDataKey = "valleytalk.npc-timeline-memories";

    public const int MaxMemoryLength    = 120;   // 60 → 120：SmartTruncate 兜底
    public const int MaxCallsignLength  = 20;
    public const int MaxMemoriesPerNpc  = 10;    // Manual 池上限（仅 AddMemory 检查）
    public const int MaxMemoriesInPrompt = 5;
    public const int MaxAutoInPrompt    = 3;
    public const int MaxAutoMemoriesPerNpc = 40; // Auto 池容量（Manual 池独立）
    public const int MaxCoreFactsInPrompt = 6;   // 核心事实段上限（MEM-06 新增）
    public const int MaxArchivedMemoriesPerNpc = 30; // 归档箱滚动上限（CORE-MEM-101）
    public const int MaxHardRulesInPrompt = 3;    // 强锚点规则段上限（CORE-MEM-102）
    public const int MaxDailyTimelineMemories = 30;
    public const int MaxWeeklyTimelineMemories = 10;
    public const int MaxChronicleTimelineMemories = 10;

    private const int EvictionImmuneImportance = 4; // Importance >= 此值免疫淘汰（未履约 Promise 也免疫）

    private Dictionary<string, List<MemoryEntry>> _memories = new();
    private Dictionary<string, string> _customCallsigns = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, List<MemoryEntry>> _archivedMemories = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, List<MemoryEntry>> _timelineMemories = new(StringComparer.OrdinalIgnoreCase);
    private bool _isLoaded = false;
    private bool _loadFailed = false; // 加载失败时拒绝覆写 SaveData

    private static bool IsChineseLanguage =>
        LocalizedContentManager.CurrentLanguageCode == LocalizedContentManager.LanguageCode.zh;

    private MemoryManager() { }

    public void Initialize(IModHelper helper)
    {
        helper.Events.GameLoop.SaveLoaded -= OnSaveLoaded;
        helper.Events.GameLoop.DayStarted -= OnDayStarted;
        helper.Events.GameLoop.SaveLoaded += OnSaveLoaded;
        helper.Events.GameLoop.DayStarted += OnDayStarted;
    }

    private void OnDayStarted(object sender, StardewModdingAPI.Events.DayStartedEventArgs e)
    {
        if (!_isLoaded) Load();
        RunDailyMaintenance();
    }

    public void EnsureLoaded()
    {
        if (!_isLoaded) Load();
    }

    public bool IsLoaded => _isLoaded;

    public void Cleanup()
    {
        try
        {
            _memories?.Clear();
            _memories = new Dictionary<string, List<MemoryEntry>>();
            _customCallsigns?.Clear();
            _customCallsigns = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            _archivedMemories?.Clear();
            _archivedMemories = new Dictionary<string, List<MemoryEntry>>(StringComparer.OrdinalIgnoreCase);
            _isLoaded = false;
        }
        catch (Exception ex)
        {
            ModEntry.SMonitor?.Log($"[MemoryManager] Error during cleanup: {ex.Message}", LogLevel.Warn);
        }
    }

    public int GetMaxMemoryLength() => MaxMemoryLength;

    private void OnSaveLoaded(object sender, StardewModdingAPI.Events.SaveLoadedEventArgs e) => Load();

    public void Load()
    {
        _memories.Clear();
        _isLoaded = false;
        _loadFailed = false;

        if (!Context.IsWorldReady || ModEntry.SHelper == null) return;

        int migratedCount = 0;

        try
        {
            // 优先从 SMAPI 存档数据读取（玩家完全看不到此文件）
            var loaded = ModEntry.SHelper.Data.ReadSaveData<Dictionary<string, List<MemoryEntry>>>(SaveDataKey);

            if (loaded != null)
            {
                _memories = loaded;
            }
            else
            {
                // 平滑迁移：SaveData 为空时，尝试导入旧版 data/{SaveFolderName}/memory_*.json
                MigrateLegacyFiles();
            }

            // 独立读取专属称谓，key 不存在时静默回退空字典
            var loadedCallsigns = ModEntry.SHelper.Data.ReadSaveData<Dictionary<string, string>>(CallsignSaveDataKey);
            _customCallsigns = loadedCallsigns != null
                ? new Dictionary<string, string>(loadedCallsigns, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            // ── 归档箱读取（CORE-MEM-101，key 不存在时静默回退空字典）──
            var loadedArchived = ModEntry.SHelper.Data.ReadSaveData<Dictionary<string, List<MemoryEntry>>>(ArchiveSaveDataKey);
            _archivedMemories = loadedArchived != null
                ? new Dictionary<string, List<MemoryEntry>>(loadedArchived, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, List<MemoryEntry>>(StringComparer.OrdinalIgnoreCase);
            _archivedMemories = _archivedMemories
                .Where(kv => !string.IsNullOrWhiteSpace(kv.Key) && kv.Value != null)
                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);

            // ── Timeline 读取（FEAT-MEM-300-T1，key 不存在时静默回退空字典）──
            var loadedTimeline = ModEntry.SHelper.Data.ReadSaveData<Dictionary<string, List<MemoryEntry>>>(TimelineSaveDataKey);
            _timelineMemories = loadedTimeline != null
                ? new Dictionary<string, List<MemoryEntry>>(loadedTimeline, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, List<MemoryEntry>>(StringComparer.OrdinalIgnoreCase);
            _timelineMemories = _timelineMemories
                .Where(kv => !string.IsNullOrWhiteSpace(kv.Key) && kv.Value != null)
                .ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);

            // ── 字段迁移（旧 schema 兼容）──
            foreach (var npcName in _memories.Keys.ToList())
            {
                var list = _memories[npcName];
                if (list == null) continue;
                foreach (var entry in list)
                {
                    bool entryTouched = false;

                    if (entry.CreatedDay == 0 && entry.CreatedAt != default)
                    {
                        entry.CreatedDay = CurrentGameDay();
                        entryTouched = true;
                    }

                    if (entry.Importance < 1 || entry.Importance > 5)
                    {
                        entry.Importance = Math.Clamp(entry.Importance <= 0 ? 3 : entry.Importance, 1, 5);
                        entryTouched = true;
                    }

                    if (string.IsNullOrEmpty(entry.Source))
                    {
                        entry.Source = "Auto";
                        entryTouched = true;
                    }

                    if (entryTouched) migratedCount++;
                }
            }

            if (migratedCount > 0)
            {
                ModEntry.SMonitor?.Log(
                    $"[MemoryManager] Migrated {migratedCount} legacy entries (schema upgrade: CreatedDay/Importance/Source).",
                    LogLevel.Info);
            }

            _isLoaded = true;

            // ── 一次性类别迁移（CORE-MEM-101）：旧默认参数遗留的 Behavior 条目 → Fact ──
            if (ModEntry.SHelper.Data.ReadSaveData<string>(CategoryMigrationFlagKey) != "true")
            {
                int migratedCategories = 0;
                foreach (var list in _memories.Values)
                {
                    if (list == null) continue;
                    foreach (var entry in list)
                    {
                        if (entry != null && entry.Category == MemoryCategory.Behavior)
                        {
                            entry.Category = MemoryCategory.Fact;
                            migratedCategories++;
                        }
                    }
                }

                ModEntry.SHelper.Data.WriteSaveData(CategoryMigrationFlagKey, "true");

                if (migratedCategories > 0)
                {
                    ModEntry.SMonitor?.Log(
                        $"[MemoryManager] One-time category migration: {migratedCategories} Behavior → Fact (legacy manual entries).",
                        LogLevel.Info);
                    Save();
                }
            }
        }
        catch (Exception ex)
        {
            _loadFailed = true;
            ModEntry.SMonitor?.Log($"[MemoryManager] Load failed: {ex.Message}", LogLevel.Warn);
        }
    }

    public void Save(string npcName = null)
    {
        if (_loadFailed)
        {
            ModEntry.SMonitor?.Log(
                "[MemoryManager] Write refused: last load failed, refusing to overwrite SaveData.",
                LogLevel.Error);
            return;
        }

        try
        {
            if (!Context.IsWorldReady || ModEntry.SHelper == null) return;

            ModEntry.SHelper.Data.WriteSaveData(SaveDataKey, _memories);
        }
        catch (Exception ex)
        {
            ModEntry.SMonitor?.Log($"[MemoryManager] Save failed: {ex.Message}", LogLevel.Warn);
        }
    }

    public void SaveAll()
    {
        Save();
    }

    // ──────────────────────────────────────────────────────────────
    // 归档箱持久化（CORE-MEM-101）
    // ──────────────────────────────────────────────────────────────
    private void SaveArchived()
    {
        if (_loadFailed)
        {
            ModEntry.SMonitor?.Log(
                "[MemoryManager] Write refused: last load failed, refusing to overwrite SaveData.",
                LogLevel.Error);
            return;
        }

        try
        {
            if (!Context.IsWorldReady || ModEntry.SHelper == null) return;
            ModEntry.SHelper.Data.WriteSaveData(ArchiveSaveDataKey, _archivedMemories);
        }
        catch (Exception ex)
        {
            ModEntry.SMonitor?.Log($"[MemoryManager] SaveArchived failed: {ex.Message}", LogLevel.Warn);
        }
    }

    // ──────────────────────────────────────────────────────────────
    // Timeline 分层存储（FEAT-MEM-300-T1）— 独立于 Manual/Auto/归档箱
    // ──────────────────────────────────────────────────────────────

    /// <summary>获取指定 NPC 指定 tier 的时间线条目，按 CreatedDay/CreatedAt 降序。空白 npcName → 空表。</summary>
    public List<MemoryEntry> GetTimelineMemories(string npcName, MemoryTier tier)
    {
        if (string.IsNullOrWhiteSpace(npcName)) return new List<MemoryEntry>();
        EnsureLoaded();
        if (!_timelineMemories.TryGetValue(npcName, out var list)) return new List<MemoryEntry>();
        return list
            .Where(m => m.Tier == tier)
            .OrderByDescending(m => m.CreatedDay)
            .ThenByDescending(m => m.CreatedAt)
            .ToList();
    }

    /// <summary>Tier 容量上限：Daily 30 / Weekly 10 / Chronicle 10。</summary>
    public static int GetTierCapacity(MemoryTier tier) => tier switch
    {
        MemoryTier.Daily => MaxDailyTimelineMemories,
        MemoryTier.Weekly => MaxWeeklyTimelineMemories,
        MemoryTier.Chronicle => MaxChronicleTimelineMemories,
        _ => 10
    };

    /// <summary>
    /// 将游戏总天数 (Game1.Date.TotalDays) 还原为 StardewTime。
    /// 星露谷每年 112 天，每季 28 天，TotalDays 从 1 开始。
    /// </summary>
    public static StardewTime GameDayToStardewTime(int totalDays)
    {
        if (totalDays <= 0) totalDays = CurrentGameDay();
        int year = (totalDays - 1) / 112 + 1;
        int dayOfSeason = (totalDays - 1) % 28 + 1;
        Season season = (Season)(((totalDays - 1) / 28) % 4);
        return new StardewTime(year, season, dayOfSeason, 600);
    }

    /// <summary>
    /// 获取条目的动态显示日历标签（优先基于 CreatedDay 动态还原，彻底解决旧存档格式不同步）。
    /// </summary>
    public static string GetDisplayDateLabel(MemoryEntry entry)
    {
        if (entry == null) return "--";
        if (entry.CreatedDay > 0)
        {
            var time = GameDayToStardewTime(entry.CreatedDay);
            return GenerateDateLabel(entry.Tier, time);
        }
        return string.IsNullOrEmpty(entry.DateLabel) ? "--" : entry.DateLabel;
    }

    /// <summary>游戏内日历戳。禁止读 Game1 世界状态。</summary>
    public static string FormatGameDateLabel(StardewTime date)
    {
        if (I18n.IsChinese)
        {
            string seasonName = date.Season switch
            {
                Season.Spring => "春",
                Season.Summer => "夏",
                Season.Fall => "秋",
                Season.Winter => "冬",
                _ => date.Season.ToString()
            };
            return $"第 {date.Year} 年 {seasonName} {date.DayOfMonth} 日";
        }

        string enSeason = date.Season switch
        {
            Season.Spring => "Spring",
            Season.Summer => "Summer",
            Season.Fall => "Fall",
            Season.Winter => "Winter",
            _ => date.Season.ToString()
        };
        return $"{enSeason} {date.DayOfMonth}, Year {date.Year}";
    }

    /// <summary>当前游戏日戳；世界未就绪 → 空串。</summary>
    public static string FormatCurrentGameDateLabel() =>
        Context.IsWorldReady ? FormatGameDateLabel(new StardewTime(Game1.Date, Game1.timeOfDay)) : "";

    /// <summary>分层日历戳：统一中英文年份表达，与对话记录顶栏风格（FormatGameDateLabel）保持完全一致。</summary>
    public static string GenerateDateLabel(MemoryTier tier, StardewTime date)
    {
        bool isZh = I18n.IsChinese;
        string seasonName = isZh
            ? date.Season switch
            {
                Season.Spring => "春",
                Season.Summer => "夏",
                Season.Fall => "秋",
                Season.Winter => "冬",
                _ => date.Season.ToString()
            }
            : date.Season switch
            {
                Season.Spring => "Spring",
                Season.Summer => "Summer",
                Season.Fall => "Fall",
                Season.Winter => "Winter",
                _ => date.Season.ToString()
            };

        int week = (date.DayOfMonth - 1) / 7 + 1;

        return tier switch
        {
            MemoryTier.Daily => FormatGameDateLabel(date),
            MemoryTier.Weekly => isZh
                ? $"第 {date.Year} 年 {seasonName} 第 {week} 周"
                : $"Year {date.Year} {seasonName} Week {week}",
            MemoryTier.Chronicle => isZh
                ? $"第 {date.Year} 年 {seasonName}季印记"
                : $"Year {date.Year} {seasonName} Imprint",
            _ => FormatGameDateLabel(date)
        };
    }

    /// <summary>同尺度差值：daysAgo = date.DaysSince(now)，返回 CurrentGameDay - daysAgo；未来日期钳制为今日。</summary>
    public static int StardewTimeToGameDay(StardewTime date)
    {
        if (!Context.IsWorldReady) return 0;
        int daysAgo = (int)Math.Round(Math.Max(0, date.DaysSince(new StardewTime(Game1.Date, Game1.timeOfDay))));
        return Math.Max(0, CurrentGameDay() - daysAgo);
    }

    public MemoryOperationResult AddTimelineMemory(string npcName, string content, MemoryTier tier,
        string dateLabel = null, int createdDay = -1)
    {
        if (string.IsNullOrWhiteSpace(npcName) || string.IsNullOrWhiteSpace(content))
            return MemoryOperationResult.NotFound;

        EnsureLoaded();

        if (_loadFailed)
        {
            ModEntry.SMonitor?.Log("[MemoryManager] AddTimelineMemory refused: last load failed, refusing to mutate state.", LogLevel.Error);
            return MemoryOperationResult.CapacityFull;
        }

        if (!_timelineMemories.TryGetValue(npcName, out var list))
        {
            list = new List<MemoryEntry>();
            _timelineMemories[npcName] = list;
        }

        int tierCount = list.Count(m => m.Tier == tier);
        if (tierCount >= GetTierCapacity(tier))
            return MemoryOperationResult.CapacityFull;

        var trimmed = SmartTruncate(content.Trim(), MaxMemoryLength);

        if (list.Any(m => m.Tier == tier && string.Equals(m.Content, trimmed, StringComparison.OrdinalIgnoreCase)))
            return MemoryOperationResult.Duplicate;

        // T9-R1：分层日期标签规则收口——Weekly/Chronicle 由 tier 规则生成（覆盖调用方传入值）；Daily 用页面日期戳。
        // FIX-TIMELINE-DATE-01：基于 createdDay 换算 entryTime，防止跨年浓缩时年份漂移至当前系统年份。
        int day = createdDay < 0 ? CurrentGameDay() : createdDay;
        StardewTime entryTime = GameDayToStardewTime(day);

        string label;
        if (tier == MemoryTier.Weekly || tier == MemoryTier.Chronicle)
        {
            label = GenerateDateLabel(tier, entryTime);
            if (!string.IsNullOrWhiteSpace(dateLabel))
                ModEntry.SMonitor?.Log(
                    $"[MemoryManager] Timeline label overridden by tier rule ({tier}: '{label}' replaces '{dateLabel}').",
                    LogLevel.Trace);
        }
        else
        {
            label = string.IsNullOrWhiteSpace(dateLabel)
                ? GenerateDateLabel(tier, entryTime)
                : dateLabel;
        }

        var entry = new MemoryEntry
        {
            NpcName = npcName,
            Content = trimmed,
            CreatedAt = DateTime.Now,
            CreatedDay = day,
            Source = "Timeline",
            Category = MemoryCategory.Fact,
            Type = MemoryType.Fact,
            Tier = tier,
            DateLabel = label,
            Importance = tier == MemoryTier.Chronicle ? 5 : tier == MemoryTier.Weekly ? 4 : 3
        };

        list.Insert(0, entry);
        SaveTimeline();

        ModEntry.SMonitor?.Log($"[MemoryManager] +Timeline [{tier}] [{npcName}]: {TrimForLog(trimmed)}", LogLevel.Info);
        return MemoryOperationResult.Success;
    }

    public MemoryOperationResult EditTimelineMemory(string npcName, string entryId, string newContent)
    {
        if (string.IsNullOrWhiteSpace(npcName) || string.IsNullOrWhiteSpace(entryId))
            return MemoryOperationResult.NotFound;

        if (!_timelineMemories.TryGetValue(npcName, out var list))
            return MemoryOperationResult.NotFound;

        var entry = list.FirstOrDefault(m => m.Id == entryId);
        if (entry == null) return MemoryOperationResult.NotFound;

        if (string.IsNullOrWhiteSpace(newContent))
            return MemoryOperationResult.NotFound;

        var trimmed = SmartTruncate(newContent.Trim(), MaxMemoryLength);

        if (list.Any(m => m.Id != entryId && m.Tier == entry.Tier && string.Equals(m.Content, trimmed, StringComparison.OrdinalIgnoreCase)))
            return MemoryOperationResult.Duplicate;

        entry.Content = trimmed;
        SaveTimeline();
        return MemoryOperationResult.Success;
    }

    public bool RemoveTimelineMemory(string npcName, string entryId)
    {
        if (string.IsNullOrWhiteSpace(npcName) || string.IsNullOrWhiteSpace(entryId)) return false;

        if (!_timelineMemories.TryGetValue(npcName, out var list)) return false;

        var entry = list.FirstOrDefault(m => m.Id == entryId);
        if (entry == null) return false;

        list.Remove(entry);
        if (list.Count == 0) _timelineMemories.Remove(npcName);

        SaveTimeline();
        return true;
    }

    public int RemoveTimelineMemories(string npcName, IEnumerable<string> entryIds)
    {
        if (string.IsNullOrWhiteSpace(npcName) || entryIds == null) return 0;

        if (!_timelineMemories.TryGetValue(npcName, out var list) || list.Count == 0) return 0;

        var idSet = new HashSet<string>(entryIds.Where(id => !string.IsNullOrWhiteSpace(id)));
        if (idSet.Count == 0) return 0;

        int before = list.Count;
        list.RemoveAll(m => idSet.Contains(m.Id));
        int removed = before - list.Count;

        if (removed == 0) return 0;

        if (list.Count == 0) _timelineMemories.Remove(npcName);

        SaveTimeline();
        ModEntry.SMonitor?.Log($"[MemoryManager] Removed {removed} timeline item(s) for [{npcName}].", LogLevel.Info);
        return removed;
    }

    private void SaveTimeline()
    {
        if (_loadFailed)
        {
            ModEntry.SMonitor?.Log(
                "[MemoryManager] Write refused: last load failed, refusing to overwrite SaveData.",
                LogLevel.Error);
            return;
        }

        try
        {
            if (!Context.IsWorldReady || ModEntry.SHelper == null) return;
            ModEntry.SHelper.Data.WriteSaveData(TimelineSaveDataKey, _timelineMemories);
        }
        catch (Exception ex)
        {
            ModEntry.SMonitor?.Log($"[MemoryManager] SaveTimeline failed: {ex.Message}", LogLevel.Warn);
        }
    }

    // ──────────────────────────────────────────────────────────────
    // 专属称谓 API — 独立存储，不占用记忆额度
    // ──────────────────────────────────────────────────────────────

    /// <summary>获取该 NPC 对玩家的专属称谓，未设置则返回 null。</summary>
    public string GetCustomCallsign(string npcName)
    {
        if (string.IsNullOrWhiteSpace(npcName)) return null;
        EnsureLoaded();
        return _customCallsigns.TryGetValue(npcName, out var c) && !string.IsNullOrWhiteSpace(c)
            ? c.Trim()
            : null;
    }

    /// <summary>设置称谓；传 null 或空白则清除，恢复默认。</summary>
    public void SetCustomCallsign(string npcName, string callsign)
    {
        if (string.IsNullOrWhiteSpace(npcName)) return;
        EnsureLoaded();

        if (string.IsNullOrWhiteSpace(callsign))
        {
            _customCallsigns.Remove(npcName);
        }
        else
        {
            string trimmed = callsign.Trim();
            if (trimmed.Length > MaxCallsignLength)
                trimmed = trimmed.Substring(0, MaxCallsignLength);
            _customCallsigns[npcName] = trimmed;
        }

        // 仅写称谓 key，不触发记忆字典的重复序列化
        try
        {
            if (!Context.IsWorldReady || ModEntry.SHelper == null) return;
            ModEntry.SHelper.Data.WriteSaveData(CallsignSaveDataKey, _customCallsigns);
        }
        catch (Exception ex)
        {
            ModEntry.SMonitor?.Log($"[MemoryManager] SaveCallsign failed: {ex.Message}", LogLevel.Warn);
        }
    }

    /// <summary>
    /// 兼容旧版：自动读取并迁移旧 json 文件到 SaveData
    /// </summary>
    private void MigrateLegacyFiles()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(Constants.SaveFolderName)) return;

            string saveDir = $"data/{Constants.SaveFolderName}";
            string physicalDir = System.IO.Path.Combine(ModEntry.SHelper.DirectoryPath, saveDir);
            if (!System.IO.Directory.Exists(physicalDir)) return;

            var files = System.IO.Directory.GetFiles(physicalDir, "memory_*.json");
            foreach (var file in files)
            {
                string fileName = System.IO.Path.GetFileName(file);
                string relativePath = $"{saveDir}/{fileName}";

                var list = ModEntry.SHelper.Data.ReadJsonFile<List<MemoryEntry>>(relativePath);
                if (list == null || list.Count == 0) continue;

                string actualNpcName = list.First().NpcName;
                if (!string.IsNullOrWhiteSpace(actualNpcName))
                {
                    _memories[actualNpcName] = list;
                }
            }

            if (_memories.Count > 0)
            {
                Save();
                ModEntry.SMonitor?.Log($"[MemoryManager] Successfully migrated legacy JSON memories into SaveData.", LogLevel.Info);
            }
        }
        catch (Exception ex)
        {
            ModEntry.SMonitor?.Log($"[MemoryManager] Legacy migration failed: {ex.Message}", LogLevel.Warn);
        }
    }

    // ──────────────────────────────────────────────────────────────
    // 手动添加（玩家操作）
    // ──────────────────────────────────────────────────────────────
    public MemoryOperationResult AddMemory(string npcName, string content,
        MemoryCategory category = MemoryCategory.Fact)
    {
        if (string.IsNullOrWhiteSpace(npcName) || string.IsNullOrWhiteSpace(content))
            return MemoryOperationResult.NotFound;

        EnsureLoaded();

        if (!_memories.TryGetValue(npcName, out var list))
        {
            list = new List<MemoryEntry>();
            _memories[npcName] = list;
        }

        // Manual 池独立上限
        int manualCount = list.Count(m => m.Source == "Manual");
        if (manualCount >= MaxMemoriesPerNpc)
            return MemoryOperationResult.CapacityFull;

        var trimmedContent = SmartTruncate(content.Trim(), MaxMemoryLength);

        if (list.Any(m => string.Equals(m.Content, trimmedContent, StringComparison.OrdinalIgnoreCase)))
            return MemoryOperationResult.Duplicate;

        list.Insert(0, new MemoryEntry
        {
            NpcName = npcName,
            Content = trimmedContent,
            CreatedAt = DateTime.Now,
            CreatedDay = CurrentGameDay(),
            Source = "Manual",
            Category = category,
            Type = MemoryType.Fact,
            Importance = 3
        });

        Save(npcName);
        return MemoryOperationResult.Success;
    }

    // ──────────────────────────────────────────────────────────────
    // 🌟 自动添加（LLM 夜间提取）— 委托给 AddAutoFact，保留 Category 赋值
    // ──────────────────────────────────────────────────────────────
    public MemoryOperationResult AddAutoMemory(string npcName, string content,
        MemoryCategory category = MemoryCategory.Fact) =>
        AddAutoFact(npcName, content, importance: 3, category: category);

    // ──────────────────────────────────────────────────────────────
    // 事实（Fact）— 自动提取 / 手动录入通用
    // ──────────────────────────────────────────────────────────────
    public MemoryOperationResult AddAutoFact(string npcName, string content, int importance = 3,
        MemoryCategory category = MemoryCategory.Fact)
    {
        if (string.IsNullOrWhiteSpace(npcName) || string.IsNullOrWhiteSpace(content))
        {
            ModEntry.SMonitor?.Log("[MemoryManager] AddAutoFact rejected: npcName or content empty.", LogLevel.Trace);
            return MemoryOperationResult.NotFound;
        }

        // ── CORE-MEM-101 铁律：自动通道无权建立规则，强制降级为回忆 ──
        if (category != MemoryCategory.Fact)
        {
            ModEntry.SMonitor?.Log(
                "[MemoryManager] AddAutoFact forced category to Fact; auto channel cannot create rules.",
                LogLevel.Warn);
            category = MemoryCategory.Fact;
        }

        EnsureLoaded();

        if (_loadFailed)
        {
            ModEntry.SMonitor?.Log("[MemoryManager] AddAutoFact refused: last load failed, refusing to mutate state.", LogLevel.Error);
            return MemoryOperationResult.CapacityFull;
        }

        if (!_memories.TryGetValue(npcName, out var list))
        {
            list = new List<MemoryEntry>();
            _memories[npcName] = list;
        }

        var trimmed = SmartTruncate(content.Trim(), MaxMemoryLength);

        if (list.Any(m => string.Equals(m.Content, trimmed, StringComparison.OrdinalIgnoreCase)))
            return MemoryOperationResult.Duplicate;

        var entry = new MemoryEntry
        {
            NpcName = npcName,
            Content = trimmed,
            CreatedAt = DateTime.Now,
            CreatedDay = CurrentGameDay(),
            Source = "Auto",
            Category = category,
            Type = MemoryType.Fact,
            Importance = Math.Clamp(importance, 1, 5)
        };

        list.Insert(0, entry);
        EvictAutoIfNeeded(npcName, list);
        Save(npcName);

        ModEntry.SMonitor?.Log($"[MemoryManager] +AutoFact [{npcName}]: \"{TrimForLog(trimmed)}\"", LogLevel.Info);
        return MemoryOperationResult.Success;
    }

    // ──────────────────────────────────────────────────────────────
    // 约定（Promise）— 含履约追踪
    // ──────────────────────────────────────────────────────────────
    public MemoryOperationResult AddPromise(string npcName, string content, int importance,
        string targetDayHint, string triggerLocation)
    {
        if (string.IsNullOrWhiteSpace(npcName) || string.IsNullOrWhiteSpace(content))
        {
            ModEntry.SMonitor?.Log("[MemoryManager] AddPromise rejected: npcName or content empty.", LogLevel.Trace);
            return MemoryOperationResult.NotFound;
        }

        EnsureLoaded();

        if (_loadFailed)
        {
            ModEntry.SMonitor?.Log("[MemoryManager] AddPromise refused: last load failed, refusing to mutate state.", LogLevel.Error);
            return MemoryOperationResult.CapacityFull;
        }

        if (!_memories.TryGetValue(npcName, out var list))
        {
            list = new List<MemoryEntry>();
            _memories[npcName] = list;
        }

        var trimmed = SmartTruncate(content.Trim(), MaxMemoryLength);

        // 仅对"未履约"的同内容 Promise 去重
        if (list.Any(m =>
            m.Type == MemoryType.Promise &&
            !m.IsFulfilled &&
            string.Equals(m.Content, trimmed, StringComparison.OrdinalIgnoreCase)))
        {
            return MemoryOperationResult.Duplicate;
        }

        int today = CurrentGameDay();
        var entry = new MemoryEntry
        {
            NpcName = npcName,
            Content = trimmed,
            CreatedAt = DateTime.Now,
            CreatedDay = today,
            ExpireDay = today + 7,
            Source = "Auto",
            Category = MemoryCategory.Fact,
            Type = MemoryType.Promise,
            Importance = Math.Clamp(importance, 1, 5),
            TargetDayHint = targetDayHint ?? "",
            TriggerLocation = triggerLocation ?? ""
        };

        list.Insert(0, entry);
        Save(npcName);

        ModEntry.SMonitor?.Log($"[MemoryManager] +Promise [{npcName}]: \"{TrimForLog(trimmed)}\" (expire d{entry.ExpireDay})", LogLevel.Info);
        return MemoryOperationResult.Success;
    }

    // ──────────────────────────────────────────────────────────────
    // 自动池淘汰：双池解耦下仅对 Auto 池生效
    // ──────────────────────────────────────────────────────────────
    private void EvictAutoIfNeeded(string npcName, List<MemoryEntry> list)
    {
        var autoPool = list.Where(m => m.Source == "Auto").ToList();
        if (autoPool.Count <= MaxAutoMemoriesPerNpc) return;

        int today = CurrentGameDay();

        // 免疫项：Importance >= 阈值，或未履约 Promise（履约前严禁淘汰）
        bool IsImmune(MemoryEntry m) =>
            m.Importance >= EvictionImmuneImportance ||
            (m.Type == MemoryType.Promise && !m.IsFulfilled);

        var candidates = autoPool.Where(m => !IsImmune(m)).ToList();

        while (autoPool.Count > MaxAutoMemoriesPerNpc)
        {
            if (candidates.Count == 0)
            {
                ModEntry.SMonitor?.Log(
                    $"[MemoryManager] EvictAuto blocked for [{npcName}]: all auto entries are immune; " +
                    $"pool size {autoPool.Count} > soft cap {MaxAutoMemoriesPerNpc}. No eviction performed.",
                    LogLevel.Warn);

                // 绝对硬上限：超过 2 倍时连免疫项也按 Score 淘汰
                int hardCap = MaxAutoMemoriesPerNpc * 2;
                if (autoPool.Count > hardCap)
                {
                    var allCandidates = autoPool
                        .OrderBy(m => m.Importance * 10 - (today - m.CreatedDay))
                        .ThenBy(m => m.CreatedDay)
                        .ThenBy(m => m.CreatedAt)
                        .FirstOrDefault();
                    if (allCandidates != null)
                    {
                        list.Remove(allCandidates);
                        autoPool.Remove(allCandidates);
                        ModEntry.SMonitor?.Log(
                            $"[MemoryManager] EvictAuto [{npcName}] hard-cap evicted: \"{TrimForLog(allCandidates.Content)}\" " +
                            $"(Importance={allCandidates.Importance}, score=importance*10-age)",
                            LogLevel.Error);
                    }
                }
                break;
            }

            // Score = Importance*10 - 年龄（天）；低分先淘汰；平局取 CreatedDay 早，再取 CreatedAt 早
            var victim = candidates
                .OrderBy(m => m.Importance * 10 - (today - m.CreatedDay))
                .ThenBy(m => m.CreatedDay)
                .ThenBy(m => m.CreatedAt)
                .First();

            list.Remove(victim);
            candidates.Remove(victim);
            autoPool.Remove(victim);

            int score = victim.Importance * 10 - (today - victim.CreatedDay);
            ModEntry.SMonitor?.Log(
                $"[MemoryManager] EvictAuto [{npcName}]: \"{TrimForLog(victim.Content)}\" score={score}",
                LogLevel.Info);
        }
    }

    // ──────────────────────────────────────────────────────────────
    // 每日维护：清理过期 Promise + 3 天滑动窗口归档（CORE-MEM-101）
    // ──────────────────────────────────────────────────────────────
    private void RunDailyMaintenance()
    {
        if (!_isLoaded || _loadFailed) return;

        int today = CurrentGameDay();
        if (today <= 0) return;

        int totalPurged = 0;
        int totalArchived = 0;
        int touchedNpcs = 0;

        foreach (var npcName in _memories.Keys.ToList())
        {
            var list = _memories[npcName];
            if (list == null || list.Count == 0) continue;

            int before = list.Count;
            list.RemoveAll(m =>
                m.Type == MemoryType.Promise &&
                !m.IsFulfilled &&
                m.ExpireDay > 0 &&
                m.ExpireDay < today);
            totalPurged += before - list.Count;

            var toArchive = list
                .Where(m => m.Category != MemoryCategory.Behavior &&
                            m.Importance < EvictionImmuneImportance &&
                            (today - m.CreatedDay) >= 3 &&
                            !(m.Type == MemoryType.Promise && !m.IsFulfilled))
                .ToList();

            if (toArchive.Count > 0)
            {
                if (!_archivedMemories.TryGetValue(npcName, out var archiveList))
                {
                    archiveList = new List<MemoryEntry>();
                    _archivedMemories[npcName] = archiveList;
                }

                foreach (var entry in toArchive)
                {
                    entry.ArchivedAt = DateTime.Now;
                    list.Remove(entry);
                    archiveList.Insert(0, entry);
                }

                while (archiveList.Count > MaxArchivedMemoriesPerNpc)
                    archiveList.RemoveAt(archiveList.Count - 1);

                totalArchived += toArchive.Count;
                touchedNpcs++;
            }

            if (list.Count == 0) _memories.Remove(npcName);
        }

        if (totalPurged + totalArchived > 0)
        {
            Save();
            SaveArchived();
            ModEntry.SMonitor?.Log(
                $"[MemoryManager] DailyMaintenance: purged {totalPurged} expired promise(s); archived {totalArchived} item(s) across {touchedNpcs} NPC(s).",
                LogLevel.Info);
        }
    }

    // ──────────────────────────────────────────────────────────────
    // Promise 查询 / 履约标记
    // ──────────────────────────────────────────────────────────────
    public List<MemoryEntry> GetActivePromises(string npcName)
    {
        EnsureLoaded();
        if (!_memories.TryGetValue(npcName, out var list)) return new List<MemoryEntry>();

        int today = CurrentGameDay();
        return list
            .Where(m => m.Type == MemoryType.Promise && !m.IsFulfilled)
            .Where(m => m.ExpireDay <= 0 || m.ExpireDay >= today) // 永不过期 或 未到期
            .OrderBy(m => m.CreatedDay)
            .ToList();
    }

    /// <summary>高优核心事实：Importance desc, CreatedDay desc。</summary>
    public List<MemoryEntry> GetCoreFacts(string npcName, int minImportance = 4)
    {
        EnsureLoaded();
        if (!_memories.TryGetValue(npcName, out var list)) return new List<MemoryEntry>();

        return list
            .Where(m => m.Importance >= minImportance)
            .OrderByDescending(m => m.Importance)
            .ThenByDescending(m => m.CreatedDay)
            .ToList();
    }

    public bool MarkPromiseFulfilled(string npcName, string id)
    {
        EnsureLoaded();
        if (!_memories.TryGetValue(npcName, out var list)) return false;

        var entry = list.FirstOrDefault(m => m.Id == id);
        if (entry == null) return false;

        entry.IsFulfilled = true;
        Save(npcName);

        ModEntry.SMonitor?.Log($"[MemoryManager] Promise fulfilled [{npcName}]: \"{TrimForLog(entry.Content)}\"", LogLevel.Info);
        return true;
    }

    // ──────────────────────────────────────────────────────────────
    // 归档箱 API（CORE-MEM-101）：浏览 / 计数 / 恢复 / 彻底删除
    // ──────────────────────────────────────────────────────────────
    public List<MemoryEntry> GetArchivedMemories(string npcName)
    {
        if (string.IsNullOrWhiteSpace(npcName)) return new List<MemoryEntry>();
        EnsureLoaded();
        return _archivedMemories.TryGetValue(npcName, out var list) && list != null
            ? new List<MemoryEntry>(list)
            : new List<MemoryEntry>();
    }

    public int GetArchivedCount(string npcName)
    {
        if (string.IsNullOrWhiteSpace(npcName)) return 0;
        EnsureLoaded();
        return _archivedMemories.TryGetValue(npcName, out var list) && list != null
            ? list.Count
            : 0;
    }

    public MemoryOperationResult RestoreMemory(string npcName, string entryId)
    {
        if (string.IsNullOrWhiteSpace(npcName) || string.IsNullOrWhiteSpace(entryId))
            return MemoryOperationResult.NotFound;

        EnsureLoaded();

        if (_loadFailed)
        {
            ModEntry.SMonitor?.Log("[MemoryManager] RestoreMemory refused: last load failed, refusing to mutate state.", LogLevel.Error);
            return MemoryOperationResult.CapacityFull;
        }

        if (!_archivedMemories.TryGetValue(npcName, out var archiveList) || archiveList == null)
            return MemoryOperationResult.NotFound;

        var entry = archiveList.FirstOrDefault(m => m.Id == entryId);
        if (entry == null) return MemoryOperationResult.NotFound;

        if (!_memories.TryGetValue(npcName, out var activeList))
        {
            activeList = new List<MemoryEntry>();
            _memories[npcName] = activeList;
        }

        if (activeList.Any(m => string.Equals(m.Content, entry.Content, StringComparison.OrdinalIgnoreCase)))
        {
            ModEntry.SMonitor?.Log(
                $"[MemoryManager] RestoreMemory duplicate for [{npcName}]: \"{TrimForLog(entry.Content)}\" stays archived.",
                LogLevel.Debug);
            return MemoryOperationResult.Duplicate;
        }

        if (entry.Source == "Manual" &&
            activeList.Count(m => m.Source == "Manual") >= MaxMemoriesPerNpc)
            return MemoryOperationResult.CapacityFull;

        archiveList.Remove(entry);
        if (archiveList.Count == 0) _archivedMemories.Remove(npcName);

        entry.CreatedDay = CurrentGameDay();
        entry.CreatedAt = DateTime.Now;
        entry.ArchivedAt = default;
        activeList.Insert(0, entry);

        Save();
        SaveArchived();

        ModEntry.SMonitor?.Log(
            $"[MemoryManager] Restored memory [{npcName}]: \"{TrimForLog(entry.Content)}\" (fresh 3-day window).",
            LogLevel.Info);
        return MemoryOperationResult.Success;
    }

    public bool DeleteArchivedMemory(string npcName, string entryId)
    {
        if (string.IsNullOrWhiteSpace(npcName) || string.IsNullOrWhiteSpace(entryId)) return false;

        EnsureLoaded();

        if (_loadFailed)
        {
            ModEntry.SMonitor?.Log("[MemoryManager] DeleteArchivedMemory refused: last load failed, refusing to mutate state.", LogLevel.Error);
            return false;
        }

        if (!_archivedMemories.TryGetValue(npcName, out var archiveList) || archiveList == null)
            return false;

        var entry = archiveList.FirstOrDefault(m => m.Id == entryId);
        if (entry == null) return false;

        archiveList.Remove(entry);
        if (archiveList.Count == 0) _archivedMemories.Remove(npcName);

        SaveArchived();

        ModEntry.SMonitor?.Log(
            $"[MemoryManager] Deleted archived memory [{npcName}]: \"{TrimForLog(entry.Content)}\".",
            LogLevel.Info);
        return true;
    }

    // ──────────────────────────────────────────────────────────────
    // 工具方法
    // ──────────────────────────────────────────────────────────────
    private static int CurrentGameDay() =>
        Context.IsWorldReady ? (int)Game1.Date.TotalDays : 0;

    /// <summary>
    /// 在 [maxLen*0.6, maxLen] 区间内找最后一个句末标点（。！？!?.…）截断（含标点）；
    /// 找不到则硬切 maxLen。
    /// </summary>
    internal static string SmartTruncate(string content, int maxLen)
    {
        if (string.IsNullOrEmpty(content) || content.Length <= maxLen) return content;

        ModEntry.SMonitor?.Log($"[MemoryManager] SmartTruncate: truncating {content.Length} chars to {maxLen}.", LogLevel.Info);

        int minLen = (int)(maxLen * 0.6);
        char[] endPunctuation = { '。', '！', '？', '!', '?', '.', '…' };

        for (int i = maxLen - 1; i >= minLen; i--)
        {
            if (content.Length <= i) continue;
            char c = content[i];
            if (endPunctuation.Contains(c))
            {
                return content.Substring(0, i + 1);
            }
        }

        return content.Substring(0, maxLen);
    }

    private static string TrimForLog(string s, int max = 30) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s.Substring(0, max) + "…");

    // ──────────────────────────────────────────────────────────────
    // 编辑记忆（玩家操作）— SmartTruncate 兜底，不再返回 TooLong
    // ──────────────────────────────────────────────────────────────
    public MemoryOperationResult EditMemory(string npcName, string id, string newContent,
        MemoryCategory? category = null)
    {
        if (!_memories.TryGetValue(npcName, out var list))
            return MemoryOperationResult.NotFound;

        var entry = list.FirstOrDefault(m => m.Id == id);
        if (entry == null)
            return MemoryOperationResult.NotFound;

        var trimmed = SmartTruncate(newContent.Trim(), MaxMemoryLength);

        if (list.Any(m => m.Id != id && string.Equals(m.Content, trimmed, StringComparison.OrdinalIgnoreCase)))
            return MemoryOperationResult.Duplicate;

        if (entry.Source == "Auto")
        {
            int manualCount = list.Count(m => m.Source == "Manual");
            if (manualCount >= MaxMemoriesPerNpc)
                return MemoryOperationResult.CapacityFull;
            entry.Source = "Manual";
        }

        entry.Content = trimmed;

        if (category.HasValue)
            entry.Category = category.Value;

        Save(npcName);
        return MemoryOperationResult.Success;
    }

    public bool RemoveMemory(string npcName, string id)
    {
        if (!_memories.TryGetValue(npcName, out var list)) return false;

        var entry = list.FirstOrDefault(m => m.Id == id);
        if (entry == null) return false;

        list.Remove(entry);
        if (list.Count == 0) _memories.Remove(npcName);

        Save(npcName);
        return true;
    }

    public List<MemoryEntry> GetMemories(string npcName)
    {
        EnsureLoaded();
        if (!_memories.TryGetValue(npcName, out var list)) return new List<MemoryEntry>();
        return list.OrderByDescending(m => m.CreatedAt).ToList();
    }

    private static readonly Random _memoryRng = new Random();

    public string GetRandomMemoryFragment(string npcName)
    {
        EnsureLoaded();
        if (!_memories.TryGetValue(npcName, out var list) || list.Count == 0)
            return null;

        var autoPool = list.Where(m => m.Source == "Auto").ToList();
        var pool = autoPool.Count > 0 ? autoPool : list;

        if (pool.Count == 0) return null;

        var chosen = pool[_memoryRng.Next(pool.Count)];
        return string.IsNullOrWhiteSpace(chosen.Content) ? null : chosen.Content.Trim();
    }

    public int GetMemoryCount(string npcName)
    {
        EnsureLoaded();
        return _memories.TryGetValue(npcName, out var list) ? list.Count : 0;
    }

    public int GetManualMemoryCount(string npcName)
    {
        EnsureLoaded();
        if (!_memories.TryGetValue(npcName, out var list)) return 0;
        return list.Count(m => m.Source == "Manual");
    }

    // ──────────────────────────────────────────────────────────────
    // 🌟 Prompt 注入：全面契合第二人称日常交互心流
    // ──────────────────────────────────────────────────────────────

    // maxCount 参数保留以维持签名兼容；分层配额由 MaxHardRulesInPrompt / MaxCoreFactsInPrompt / MaxAutoInPrompt 决定（CORE-MEM-102）
    public string GetSmartMemoryContext(string npcName, int maxCount = MaxMemoriesInPrompt)
    {
        EnsureLoaded();

        if (string.IsNullOrWhiteSpace(npcName) ||
            !_memories.TryGetValue(npcName, out var list) || list.Count == 0)
            return "";

        // T10：inner_impressions 注入（每日固定随机种子，日内稳定）
        string impressions = BuildInnerImpressions(npcName);

        bool isZh = IsChineseLanguage;
        int today = CurrentGameDay();

        var hardRules = list
            .Where(m => m.Source == "Manual" && m.Category == MemoryCategory.Behavior)
            .OrderByDescending(m => m.CreatedAt)
            .ThenByDescending(m => m.CreatedDay)
            .Take(MaxHardRulesInPrompt)
            .ToList();

        var coreFacts = list
            .Where(m => m.Category == MemoryCategory.Fact && m.Importance >= EvictionImmuneImportance)
            .OrderByDescending(m => m.Importance)
            .ThenByDescending(m => m.CreatedDay)
            .ThenByDescending(m => m.CreatedAt)
            .Take(MaxCoreFactsInPrompt)
            .ToList();

        var recentItems = list
            .Where(m => m.Category == MemoryCategory.Fact &&
                        m.Importance < EvictionImmuneImportance &&
                        (today - m.CreatedDay) < 3)
            .OrderByDescending(m => m.CreatedDay)
            .ThenByDescending(m => m.CreatedAt)
            .Take(MaxAutoInPrompt)
            .ToList();

        // T10：短路条件修正——三段全空且无 impressions 才返回空串
        if (hardRules.Count == 0 && coreFacts.Count == 0 && recentItems.Count == 0 && string.IsNullOrEmpty(impressions))
            return "";

        var sb = new System.Text.StringBuilder();

        if (hardRules.Count > 0)
        {
            sb.AppendLine(isZh
                ? "=== 必须严格遵守的互动禁忌与防线 ==="
                : "=== INTERACTION BOUNDARIES (STRICT) ===");
            sb.AppendLine(isZh
                ? "以下是不可违背的红线，在交谈中必须始终保持遵守："
                : "Strict rules and boundaries you must never cross:");
            foreach (var r in hardRules)
                sb.AppendLine($"- {r.Content}");
            sb.AppendLine("=========================================");
            sb.AppendLine();
        }

        if (coreFacts.Count > 0 || recentItems.Count > 0)
        {
            sb.AppendLine("<shared_lore>");
            sb.AppendLine(isZh
                ? "关于农夫的一些过往事实与近况（你心里有数即可，无需刻意在每句话中复述，仅在话赶话聊到时自然流露）："
                : "Background facts and recent context about the farmer (keep in mind naturally, do not force into conversation unless relevant):");

            foreach (var f in coreFacts)
                sb.AppendLine($"- {f.Content}");

            foreach (var r in recentItems)
            {
                int diff = Math.Max(0, today - r.CreatedDay);
                string prefix = diff switch
                {
                    0 => isZh ? "[今天] " : "[Today] ",
                    1 => isZh ? "[昨天] " : "[Yesterday] ",
                    _ => isZh ? "[前天] " : "[2 days ago] "
                };
                sb.AppendLine($"- {prefix}{r.Content}");
            }

            sb.AppendLine("</shared_lore>");
        }

        // T10：尾部追加 inner_impressions 段（impressions 空则跳过）
        if (!string.IsNullOrEmpty(impressions))
        {
            sb.AppendLine();
            sb.Append(impressions);
        }

        return sb.ToString();
    }

    /// <summary>
    /// 构建 &lt;inner_impressions&gt; 段（T10）：从时间线三层（Daily/Weekly/Chronicle）各随机抽 ≤5 条，
    /// 以每日固定种子保证同一游戏日内抽取组合恒定（SystemPrompt 缓存稳定）。
    /// 无时间线条目 → 空串。
    /// </summary>
    private string BuildInnerImpressions(string npcName)
    {
        EnsureLoaded();

        if (!_timelineMemories.TryGetValue(npcName, out var list) || list.Count == 0)
            return "";

        // 每日固定随机种子：CurrentGameDay * 31 + npcName hash（日内稳定，跨天自动换组）
        int seed = CurrentGameDay() * 31 + npcName.GetHashCode(StringComparison.Ordinal);
        var rng = new Random(seed);

        bool isZh = I18n.IsChinese;

        // 三层独立抽取（顺序 Daily → Weekly → Chronicle）
        var daily = list.Where(m => m.Tier == MemoryTier.Daily).OrderBy(_ => rng.Next()).Take(5).ToList();
        var weekly = list.Where(m => m.Tier == MemoryTier.Weekly).OrderBy(_ => rng.Next()).Take(5).ToList();
        var chronicle = list.Where(m => m.Tier == MemoryTier.Chronicle).OrderBy(_ => rng.Next()).Take(5).ToList();

        if (daily.Count == 0 && weekly.Count == 0 && chronicle.Count == 0)
            return "";

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("<inner_impressions>");
        sb.AppendLine(isZh
            ? "以下是你在心里对农夫存留的主观回忆与心流印记。它们是你亲历后的日记与升华，仅供你自然代入心境，不要在对话中背诵或复述："
            : "Below are your subjective impressions and diary notes about the farmer. Let them color your mood naturally; never recite them verbatim:");

        foreach (var m in daily)
            sb.AppendLine(string.IsNullOrEmpty(m.DateLabel) ? $"- {m.Content}" : $"- {m.DateLabel} {m.Content}");
        foreach (var m in weekly)
            sb.AppendLine(string.IsNullOrEmpty(m.DateLabel) ? $"- {m.Content}" : $"- {m.DateLabel} {m.Content}");
        foreach (var m in chronicle)
            sb.AppendLine(string.IsNullOrEmpty(m.DateLabel) ? $"- {m.Content}" : $"- {m.DateLabel} {m.Content}");

        sb.AppendLine("</inner_impressions>");
        return sb.ToString();
    }
}