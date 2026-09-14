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
    public int LastPromptedDay { get; set; } = -1;    // 激活去重用（MEM-06 消费）
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

    public const int MaxMemoryLength    = 120;   // 60 → 120：SmartTruncate 兜底
    public const int MaxCallsignLength  = 20;
    public const int MaxMemoriesPerNpc  = 10;    // Manual 池上限（仅 AddMemory 检查）
    public const int MaxMemoriesInPrompt = 5;
    public const int MaxAutoInPrompt    = 3;
    public const int MaxAutoMemoriesPerNpc = 40; // Auto 池容量（Manual 池独立）
    public const int MaxCoreFactsInPrompt = 6;   // 核心事实段上限（MEM-06 新增）

    private const int EvictionImmuneImportance = 4; // Importance >= 此值免疫淘汰（未履约 Promise 也免疫）

    private Dictionary<string, List<MemoryEntry>> _memories = new();
    private Dictionary<string, string> _customCallsigns = new(StringComparer.OrdinalIgnoreCase);
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
        MemoryCategory category = MemoryCategory.Behavior)
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
            // 同步 autoPool 引用（list 内的同一对象）
            autoPool.Remove(victim);

            int score = victim.Importance * 10 - (today - victim.CreatedDay);
            ModEntry.SMonitor?.Log(
                $"[MemoryManager] EvictAuto [{npcName}]: \"{TrimForLog(victim.Content)}\" score={score}",
                LogLevel.Info);
        }
    }

    // ──────────────────────────────────────────────────────────────
    // 每日维护：清理过期 Promise
    // ──────────────────────────────────────────────────────────────
    private void RunDailyMaintenance()
    {
        if (!_isLoaded || _loadFailed) return;

        int today = CurrentGameDay();
        if (today <= 0) return;

        int totalPurged = 0;

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
            int removed = before - list.Count;
            totalPurged += removed;

            if (list.Count == 0) _memories.Remove(npcName);
        }

        if (totalPurged > 0)
        {
            Save();
            ModEntry.SMonitor?.Log($"[MemoryManager] DailyMaintenance: purged {totalPurged} expired promise(s).", LogLevel.Info);
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

        entry.Content = trimmed;

        // 🌟 玩家编辑过的自动记忆 → 升级为手动，不再被自动替换
        if (entry.Source == "Auto")
            entry.Source = "Manual";

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

    /// <summary>
    /// 随机抽取一条该 NPC 的自动记忆碎片文本，供 Bark 记忆闪回（一缓）使用。
    /// 优先取 Auto 条目（夜间提取的背景事实），无 Auto 时回退 Manual。
    /// 无记忆时返回 null。
    /// </summary>
    public string GetRandomMemoryFragment(string npcName)
    {
        EnsureLoaded();
        if (!_memories.TryGetValue(npcName, out var list) || list.Count == 0)
            return null;

        // 优先 Auto 条目（夜间自动提取的事实碎片，更适合走神闪回）
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

    // ──────────────────────────────────────────────────────────────
    // 🌟 Prompt 注入：手动规则（高优先级）+ 自动事实（低优先级）
    // 含情境触发判定（无色结构化标签）
    // ──────────────────────────────────────────────────────────────

    /// <summary>地点别名表（英文 key → 中文别名）。</summary>
    private static readonly Dictionary<string, string[]> LocationAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["beach"] = new[] { "海边", "沙滩", "海滩" },
        ["saloon"] = new[] { "酒吧", "星之果实餐吧", "餐吧" },
        ["mine"] = new[] { "矿洞", "矿井", "矿" },
        ["farm"] = new[] { "农场" },
        ["town"] = new[] { "小镇", "镇上", "广场" },
        ["forest"] = new[] { "森林", "树林", "秘密森林" },
        ["mountain"] = new[] { "山上", "山", "湖边", "湖" },
        ["hospital"] = new[] { "医院", "诊所" },
        ["communitycenter"] = new[] { "社区中心" },
        ["jojamart"] = new[] { "joja", "超市" },
        ["seedshop"] = new[] { "皮埃尔", "种子店", "杂货店" },
        ["trailer"] = new[] { "拖车" },
        ["sciencehouse"] = new[] { "科学屋" },
        ["adventurersguild"] = new[] { "冒险家公会" },
        ["blacksmith"] = new[] { "铁匠铺" },
        ["manorhouse"] = new[] { "庄园" },
        ["archaeologyhouse"] = new[] { "博物馆", "考古" },
        ["railroad"] = new[] { "铁路", "火车站" },
        ["desert"] = new[] { "沙漠", "卡利科" },
        ["island"] = new[] { "岛", "姜岛" },
    };
    public string GetSmartMemoryContext(string npcName, int maxCount = MaxMemoriesInPrompt)
    {
        EnsureLoaded();

        if (!_memories.TryGetValue(npcName, out var list) || list.Count == 0)
            return "";

        bool isZh = IsChineseLanguage;

        // (1) 玩家手动规则
        var manualEntries = list
            .Where(m => m.Source == "Manual")
            .OrderByDescending(m => m.CreatedAt)
            .Take(maxCount)
            .ToList();

        // (2) 长期重要事实
        var coreFacts = list
            .Where(m => m.Source == "Auto" && m.Type == MemoryType.Fact && m.Importance >= 4)
            .OrderByDescending(m => m.Importance)
            .ThenByDescending(m => m.CreatedDay)
            .Take(MaxCoreFactsInPrompt)
            .ToList();

        // (3) 今日待履约 Promise
        var activePromises = GetActivePromises(npcName);
        string currentLocation = Game1.player?.currentLocation?.Name ?? "";
        int today = CurrentGameDay();

        var todayPromises = new List<MemoryEntry>();
        foreach (var p in activePromises)
        {
            if (DayHintMatches(p.TargetDayHint, p.CreatedDay) &&
                LocationHintMatches(p.TriggerLocation, currentLocation))
            {
                todayPromises.Add(p);
                // 内存副作用：标记去重（不落盘）
                if (p.LastPromptedDay != today)
                {
                    p.LastPromptedDay = today;
                    ModEntry.SMonitor?.Log(
                        $"[MemoryManager] Promise activated for [{npcName}]: \"{TrimForLog(p.Content)}\"",
                        LogLevel.Info);
                }
            }
        }

        // (4) 近期琐事（排除核心事实 Id，Importance<=3）
        var coreFactIds = new HashSet<string>(coreFacts.Select(f => f.Id));
        var recentTrivia = list
            .Where(m => m.Source == "Auto" && m.Type == MemoryType.Fact && m.Importance <= 3)
            .Where(m => !coreFactIds.Contains(m.Id))
            .OrderByDescending(m => m.CreatedAt)
            .Take(MaxAutoInPrompt)
            .ToList();

        if (manualEntries.Count == 0 && coreFacts.Count == 0 &&
            todayPromises.Count == 0 && recentTrivia.Count == 0)
            return "";

        var sb = new System.Text.StringBuilder();

        // ── (1) 手动规则（最高优先级）── 原文案逐字保留
        if (manualEntries.Count > 0)
        {
            sb.AppendLine(isZh
                ? "=== 玩家自定义规则与专属设定（最高优先级）==="
                : "=== USER-DEFINED HIGH-PRIORITY RULES ===");
            sb.AppendLine(isZh
                ? "以下是玩家手动设置的规则，请优先遵守："
                : "These are player-defined rules. Follow them with highest priority:");
            sb.AppendLine();

            foreach (var e in manualEntries)
                sb.AppendLine($"- {e.Content}");

            sb.AppendLine("=========================================");
            sb.AppendLine();
        }

        // ── (2) 长期重要事实 ──
        if (coreFacts.Count > 0)
        {
            sb.AppendLine(isZh
                ? "=== 长期重要事实（背景知识）==="
                : "=== LONG-TERM IMPORTANT FACTS (background knowledge) ===");
            sb.AppendLine();
            foreach (var e in coreFacts)
                sb.AppendLine($"- {e.Content}");
            sb.AppendLine("=========================================");
            sb.AppendLine();
        }

        // ── (3) 今日待履约约定 ──
        if (todayPromises.Count > 0)
        {
            sb.AppendLine(isZh
                ? "=== 今日待履约约定（农场主与你之间的约定）==="
                : "=== TODAY'S PROMISES (commitments between you and the farmer) ===");
            sb.AppendLine();
            foreach (var p in todayPromises)
                sb.AppendLine($"- [TODAY_PROMISE] {p.Content}");
            sb.AppendLine("=========================================");
            sb.AppendLine();
        }

        // ── (4) 近期琐事记录 ──
        if (recentTrivia.Count > 0)
        {
            sb.AppendLine(isZh
                ? "=== 近期琐事记录（背景参考）==="
                : "=== RECENT MINOR FACTS (background) ===");
            sb.AppendLine(isZh
                ? "以下是系统自动整理的背景信息，仅作参考。若与玩家手动规则冲突，以玩家手动规则为准。标记 [CONTEXT_RELEVANT] 的条目与当前场景相关："
                : "Auto-recorded background facts for reference only. If conflicts with player rules, player rules take priority. Entries marked [CONTEXT_RELEVANT] are relevant to the current scene:");
            sb.AppendLine();

            foreach (var e in recentTrivia)
            {
                bool isRelevant = IsContextuallyRelevant(e.Content, currentLocation);
                if (isRelevant)
                    sb.AppendLine($"- [CONTEXT_RELEVANT] {e.Content}");
                else
                    sb.AppendLine($"- {e.Content}");
            }

            sb.AppendLine("=========================================");
        }

        return sb.ToString();
    }

    // ──────────────────────────────────────────────────────────────
    // 🌟 情境匹配：简单的关键词 + 地点别名匹配
    // ──────────────────────────────────────────────────────────────
    private static bool IsContextuallyRelevant(string memoryContent, string currentLocation)
    {
        if (string.IsNullOrWhiteSpace(currentLocation) || string.IsNullOrWhiteSpace(memoryContent))
            return false;

        string lowerContent = memoryContent.ToLowerInvariant();
        string lowerLocation = currentLocation.ToLowerInvariant();

        // 直接包含
        if (lowerContent.Contains(lowerLocation))
            return true;

        // 地点别名匹配（引用 LocationAliases 字段）
        foreach (var kvp in LocationAliases)
        {
            bool locationMatch = lowerLocation.Contains(kvp.Key);
            if (locationMatch)
            {
                foreach (var alias in kvp.Value)
                {
                    if (lowerContent.Contains(alias))
                        return true;
                }
            }
        }

        return false;
    }

    // ──────────────────────────────────────────────────────────────
    // 🌟 Promise 激活语法匹配（MEM-06 新增）
    // ──────────────────────────────────────────────────────────────

    private static bool ContainsAny(string source, params string[] keys)
    {
        foreach (var k in keys)
        {
            if (source.Contains(k, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static bool CrossContains(string a, string b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
            return false;
        return a.Contains(b, StringComparison.OrdinalIgnoreCase) || b.Contains(a, StringComparison.OrdinalIgnoreCase);
    }

    private static bool DayHintMatches(string hint, int createdDay)
    {
        if (string.IsNullOrWhiteSpace(hint))
            return true; // 未标注 = 始终激活

        string h = hint.Trim();
        int today = CurrentGameDay();
        int dow = (Game1.dayOfMonth - 1) % 7; // 0=Monday；dayOfMonth∈[1,28] 且 28%7==0，跨季跨年相位一致（游戏历不变量：1/8/15/22=周一）

        // ① 天气
        if (ContainsAny(h, "rain", "rainy", "雨"))
            return Game1.isRaining || Game1.isLightning;

        // ② 雷暴
        if (ContainsAny(h, "storm", "thunder", "雷", "暴风雨"))
            return Game1.isLightning;

        // ③ 雪
        if (ContainsAny(h, "snow", "雪"))
            return Game1.isSnowing;

        // ④ 节日
        if (ContainsAny(h, "festival", "节日", "庆典"))
            return Utility.isFestivalDay(Game1.dayOfMonth, Game1.season);

        // ⑤ 今天
        if (ContainsAny(h, "today", "今天", "今日"))
            return true;

        // ⑥ 明天起
        if (ContainsAny(h, "tomorrow", "明天", "明日", "次日"))
            return today >= createdDay + 1;

        // ⑦ 周末
        if (ContainsAny(h, "weekend", "周末"))
            return dow == 5 || dow == 6;

        // ⑧ 星期
        if (ContainsAny(h, "monday", "周一", "星期一", "礼拜一")) return dow == 0;
        if (ContainsAny(h, "tuesday", "周二", "星期二", "礼拜二")) return dow == 1;
        if (ContainsAny(h, "wednesday", "周三", "星期三", "礼拜三")) return dow == 2;
        if (ContainsAny(h, "thursday", "周四", "星期四", "礼拜四")) return dow == 3;
        if (ContainsAny(h, "friday", "周五", "星期五", "礼拜五")) return dow == 4;
        if (ContainsAny(h, "saturday", "周六", "星期六", "礼拜六")) return dow == 5;
        if (ContainsAny(h, "sunday", "周日", "星期日", "星期天", "礼拜日", "礼拜天")) return dow == 6;

        // ⑨ 兜底：未识别语法一律激活
        return true;
    }

    private static bool LocationHintMatches(string triggerLocation, string currentLocationName)
    {
        if (string.IsNullOrWhiteSpace(triggerLocation))
            return true; // 地点无关
        if (string.IsNullOrWhiteSpace(currentLocationName))
            return false; // 无法定位则不激活

        if (CrossContains(triggerLocation, currentLocationName))
            return true;

        foreach (var kvp in LocationAliases)
        {
            string key = kvp.Key;
            string[] aliases = kvp.Value;

            // 当前地点是否匹配 key 或任一 alias
            bool currentMatches = CrossContains(key, currentLocationName);
            if (!currentMatches)
            {
                foreach (var alias in aliases)
                {
                    if (CrossContains(alias, currentLocationName))
                    {
                        currentMatches = true;
                        break;
                    }
                }
            }

            if (!currentMatches) continue;

            // 触发地点是否匹配 key 或任一 alias（同一键条目内交叉命中）
            if (CrossContains(key, triggerLocation)) return true;
            foreach (var alias in aliases)
            {
                if (CrossContains(alias, triggerLocation)) return true;
            }
        }

        return false;
    }
}