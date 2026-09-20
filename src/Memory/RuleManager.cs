using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using StardewModdingAPI;
using StardewModdingAPI.Events;
using StardewValley;

namespace ValleytalkReborn;

/// <summary>
/// Unified rule store (RULE-MERGE). Merges the former WorldMemoryManager entries and
/// Manual-sourced NPC memories into a single per-save list under "valleytalk.rules".
/// Provides the prompt segment that used to be emitted by both MemoryManager (hard rules)
/// and WorldMemoryManager (global lore).
/// </summary>
internal sealed class RuleManager
{
    public static readonly RuleManager Instance = new RuleManager();

    private const string SaveDataKey = "valleytalk.rules";
    private const string MigrationFlagKey = "valleytalk.rules-migrated";
    private const string WorldMemoriesKey = "valleytalk.world-memories";
    private const string NpcMemoriesKey = "valleytalk.npc-memories";

    public const int MaxRulesPerScope = 10;
    public const int MaxRuleLength = 120;

    private List<MemoryEntry> _rules = new List<MemoryEntry>();
    private bool _isLoaded = false;
    private bool _loadFailed = false;

    private static bool IsChineseLanguage =>
        LocalizedContentManager.CurrentLanguageCode == LocalizedContentManager.LanguageCode.zh;

    private static int CurrentGameDay() =>
        Context.IsWorldReady ? (int)Game1.Date.TotalDays : 0;

    private RuleManager() { }

    public void Initialize(IModHelper helper)
    {
        // 先取消防止重复注册（Cleanup → Entry 的重入场景，对齐 MemoryManager L109-112 先例）
        helper.Events.GameLoop.SaveLoaded -= OnSaveLoaded;
        helper.Events.GameLoop.DayStarted -= OnDayStarted;

        helper.Events.GameLoop.SaveLoaded += OnSaveLoaded;
        helper.Events.GameLoop.DayStarted += OnDayStarted;
    }

    public void Cleanup()
    {
        try
        {
            _rules?.Clear();
            _rules = new List<MemoryEntry>();
            _isLoaded = false;
            _loadFailed = false;
            ModEntry.SMonitor?.Log("[RuleManager] Cleaned up successfully.", LogLevel.Debug);
        }
        catch (Exception ex)
        {
            ModEntry.SMonitor?.Log($"[RuleManager] Error during cleanup: {ex.Message}", LogLevel.Warn);
        }
    }

    public bool IsLoaded => _isLoaded;

    public void EnsureLoaded()
    {
        if (!_isLoaded) Load();
    }

    private void OnSaveLoaded(object sender, SaveLoadedEventArgs e) => Load();

    private void OnDayStarted(object sender, DayStartedEventArgs e)
    {
        if (!_isLoaded) Load();
        if (_isLoaded && !_loadFailed)
        {
            int removed = CheckRulesExpiration();
            if (removed > 0)
                ModEntry.SMonitor?.Log($"[RuleManager] DayStarted: expired {removed} rule(s).", LogLevel.Info);
        }
    }

    public void Load()
    {
        _rules.Clear();
        _isLoaded = false;
        _loadFailed = false;

        if (!Context.IsWorldReady || ModEntry.SHelper == null)
        {
            // 对齐 WorldMemoryManager L53-57 先例：世界未就绪时空转
            _isLoaded = true;
            return;
        }

        try
        {
            var loaded = ModEntry.SHelper.Data.ReadSaveData<List<MemoryEntry>>(SaveDataKey);
            if (loaded != null)
            {
                _rules = loaded;
                _isLoaded = true;
                return;
            }

            // 首次加载且无 rules key → 尝试迁移
            string migrated = ModEntry.SHelper.Data.ReadSaveData<string>(MigrationFlagKey);
            if (migrated != "true")
            {
                int count = RunMigration();
                if (!_loadFailed)
                {
                    _isLoaded = true;
                    ModEntry.SMonitor?.Log($"[RuleManager] Migrated {count} rules.", LogLevel.Info);
                }
            }
            else
            {
                // 标记已迁移但 rules key 缺失（异常态）：保留空列表，不失败
                _isLoaded = true;
            }
        }
        catch (Exception ex)
        {
            _loadFailed = true;
            _isLoaded = true;
            ModEntry.SMonitor?.Log($"[RuleManager] Load failed: {ex.Message}", LogLevel.Warn);
        }
    }

    /// <summary>
    /// 迁移步骤（implementation_flow 第 1 步）。任何异常 → _loadFailed=true，不写任何 key。
    /// </summary>
    private int RunMigration()
    {
        var migrated = new List<MemoryEntry>();
        try
        {
            // 1a) 全局记忆 → scope=WORLD, ExpireDay=-1, AutoArchive=true
            var worldList = ModEntry.SHelper.Data.ReadSaveData<List<MemoryEntry>>(WorldMemoriesKey);
            if (worldList != null)
            {
                foreach (var e in worldList)
                {
                    if (e == null || string.IsNullOrWhiteSpace(e.Content)) continue;
                    if (string.IsNullOrWhiteSpace(e.Id)) e.Id = Guid.NewGuid().ToString();
                    e.NpcName = "WORLD";
                    e.ExpireDay = -1;
                    e.AutoArchive = true;
                    if (e.CreatedAt == default) e.CreatedAt = DateTime.Now;
                    migrated.Add(e);
                }
            }

            // 1b) NPC Manual 条目 → scope=NpcName, ExpireDay=-1, AutoArchive=true；从原 dict 移除
            var npcDict = ModEntry.SHelper.Data.ReadSaveData<Dictionary<string, List<MemoryEntry>>>(NpcMemoriesKey);
            if (npcDict != null)
            {
                foreach (var npcName in npcDict.Keys.ToList())
                {
                    var list = npcDict[npcName];
                    if (list == null) continue;

                    var manualItems = list
                        .Where(e => e != null && string.Equals(e.Source, "Manual", StringComparison.Ordinal))
                        .ToList();

                    foreach (var e in manualItems)
                    {
                        if (string.IsNullOrWhiteSpace(e.Id)) e.Id = Guid.NewGuid().ToString();
                        e.ExpireDay = -1;
                        e.AutoArchive = true;
                        if (e.CreatedAt == default) e.CreatedAt = DateTime.Now;
                        migrated.Add(e);
                        list.Remove(e);
                    }

                    if (list.Count == 0) npcDict.Remove(npcName);
                }
            }

            // 1c) 写 rules key + 迁移标记 + 回写 npc-memories（移除后）
            _rules = migrated;
            Save();
            ModEntry.SHelper.Data.WriteSaveData(MigrationFlagKey, "true");
            if (npcDict != null)
                ModEntry.SHelper.Data.WriteSaveData(NpcMemoriesKey, npcDict);

            return migrated.Count;
        }
        catch (Exception ex)
        {
            _loadFailed = true;
            _rules.Clear();
            ModEntry.SMonitor?.Log($"[RuleManager] Migration failed: {ex.Message}", LogLevel.Warn);
            return 0;
        }
    }

    public void Save()
    {
        if (_loadFailed)
        {
            ModEntry.SMonitor?.Log(
                "[RuleManager] Write refused: last load failed, refusing to overwrite SaveData.",
                LogLevel.Error);
            return;
        }

        try
        {
            if (!Context.IsWorldReady || ModEntry.SHelper == null) return;
            ModEntry.SHelper.Data.WriteSaveData(SaveDataKey, _rules);
        }
        catch (Exception ex)
        {
            ModEntry.SMonitor?.Log($"[RuleManager] Save failed: {ex.Message}", LogLevel.Warn);
        }
    }

    // ──────────────────────────────────────────────────────────────
    // 公开 API
    // ──────────────────────────────────────────────────────────────

    /// <summary>
    /// scopeNpcName: "WORLD" 或空白 = 全局；其他 = NPC 内部名。
    /// durationDays: 0 = 仅今日(ExpireDay=today+1)；-1 = 永久；n>0 = ExpireDay=today+n。
    /// </summary>
    public MemoryOperationResult AddRule(string scopeNpcName, string content, int durationDays, MemoryCategory category)
    {
        if (string.IsNullOrWhiteSpace(scopeNpcName) || string.IsNullOrWhiteSpace(content))
            return MemoryOperationResult.NotFound;

        EnsureLoaded();
        if (_loadFailed) return MemoryOperationResult.CapacityFull;

        scopeNpcName = scopeNpcName.Trim();
        if (string.Equals(scopeNpcName, "WORLD", StringComparison.OrdinalIgnoreCase))
            scopeNpcName = "WORLD";

        if (GetRuleCount(scopeNpcName) >= MaxRulesPerScope)
            return MemoryOperationResult.CapacityFull;

        string trimmed = SmartTruncate(content.Trim(), MaxRuleLength);

        if (_rules.Any(e => string.Equals(e.NpcName, scopeNpcName, StringComparison.Ordinal) &&
                            string.Equals(e.Content, trimmed, StringComparison.OrdinalIgnoreCase)))
            return MemoryOperationResult.Duplicate;

        int today = CurrentGameDay();
        int expire = durationDays == 0 ? today + 1
                   : durationDays < 0 ? -1
                   : today + durationDays;

        _rules.Insert(0, new MemoryEntry
        {
            NpcName = scopeNpcName,
            Content = trimmed,
            Category = category,
            Source = "Manual",
            CreatedAt = DateTime.Now,
            CreatedDay = today,
            ExpireDay = expire,
            AutoArchive = true
        });

        Save();
        return MemoryOperationResult.Success;
    }

    public MemoryOperationResult EditRule(string ruleId, string newContent)
    {
        if (string.IsNullOrWhiteSpace(ruleId) || string.IsNullOrWhiteSpace(newContent))
            return MemoryOperationResult.NotFound;

        EnsureLoaded();
        if (_loadFailed) return MemoryOperationResult.CapacityFull;

        var entry = _rules.FirstOrDefault(e => e.Id == ruleId);
        if (entry == null) return MemoryOperationResult.NotFound;

        string trimmed = SmartTruncate(newContent.Trim(), MaxRuleLength);

        if (_rules.Any(e => e.Id != ruleId &&
                            string.Equals(e.NpcName, entry.NpcName, StringComparison.Ordinal) &&
                            string.Equals(e.Content, trimmed, StringComparison.OrdinalIgnoreCase)))
            return MemoryOperationResult.Duplicate;

        entry.Content = trimmed;
        Save();
        return MemoryOperationResult.Success;
    }

    /// <summary>直接删，不入箱。</summary>
    public bool RemoveRule(string ruleId)
    {
        EnsureLoaded();
        var entry = _rules.FirstOrDefault(e => e.Id == ruleId);
        if (entry == null) return false;
        _rules.Remove(entry);
        Save();
        return true;
    }

    /// <summary>手动入箱（调用方负责后续 RemoveRule）。</summary>
    public MemoryOperationResult ArchiveRule(string ruleId, string archiveReason)
    {
        EnsureLoaded();
        if (_loadFailed) return MemoryOperationResult.CapacityFull;

        var entry = _rules.FirstOrDefault(e => e.Id == ruleId);
        if (entry == null) return MemoryOperationResult.NotFound;

        MemoryManager.Instance.ArchiveMemory(entry.NpcName, entry, archiveReason ?? "Manual");
        return MemoryOperationResult.Success;
    }

    /// <summary>参数 null/空白 = 返回全部；按 CreatedDay desc, CreatedAt desc。</summary>
    public List<MemoryEntry> GetRules(string scopeNpcName)
    {
        EnsureLoaded();
        IEnumerable<MemoryEntry> query = _rules;
        if (!string.IsNullOrWhiteSpace(scopeNpcName))
        {
            string scope = scopeNpcName.Trim();
            query = query.Where(e => string.Equals(e.NpcName, scope, StringComparison.Ordinal));
        }
        return query
            .OrderByDescending(e => e.CreatedDay)
            .ThenByDescending(e => e.CreatedAt)
            .ToList();
    }

    public int GetRuleCount(string scopeNpcName)
    {
        EnsureLoaded();
        if (string.IsNullOrWhiteSpace(scopeNpcName)) return _rules.Count;
        string scope = scopeNpcName.Trim();
        return _rules.Count(e => string.Equals(e.NpcName, scope, StringComparison.Ordinal));
    }

    /// <summary>返回移除条数，DayStarted 调用。</summary>
    public int CheckRulesExpiration()
    {
        int today = CurrentGameDay();
        if (today <= 0) return 0;

        var snapshot = _rules.ToList();
        var toRemove = new List<MemoryEntry>();
        foreach (var e in snapshot)
        {
            if (e.ExpireDay > 0 && today >= e.ExpireDay)
                toRemove.Add(e);
        }

        if (toRemove.Count == 0) return 0;

        bool changed = false;
        foreach (var e in toRemove)
        {
            if (e.AutoArchive)
            {
                try
                {
                    MemoryManager.Instance.ArchiveMemory(e.NpcName, e, "Expired");
                }
                catch (Exception ex)
                {
                    ModEntry.SMonitor?.Log($"[RuleManager] ArchiveMemory failed for rule {e.Id}: {ex.Message}", LogLevel.Warn);
                }
            }
            if (_rules.Remove(e)) changed = true;
        }

        if (changed) Save();
        return toRemove.Count;
    }

    /// <summary>
    /// 三段拼装（implementation_flow 第 7 步）。全空返回 ""。
    /// </summary>
    public string GetPromptSegment(string npcName)
    {
        if (_loadFailed) return "";
        EnsureLoaded();
        if (_loadFailed) return "";

        bool isZh = IsChineseLanguage;
        var sb = new StringBuilder();

        // a) 严格规则段：scope==npcName 且 Category==Behavior
        var strictRules = _rules
            .Where(e => string.Equals(e.NpcName, npcName, StringComparison.Ordinal) &&
                        e.Category == MemoryCategory.Behavior)
            .OrderByDescending(e => e.CreatedAt)
            .ThenByDescending(e => e.CreatedDay)
            .ToList();

        if (strictRules.Count > 0)
        {
            sb.AppendLine(isZh
                ? "=== 必须严格遵守的互动禁忌与防线 ==="
                : "=== INTERACTION BOUNDARIES (STRICT) ===");
            sb.AppendLine(isZh
                ? "以下是不可违背的红线，在交谈中必须始终保持遵守："
                : "Strict rules and boundaries you must never cross:");
            foreach (var r in strictRules)
                sb.AppendLine($"- {r.Content}");
            sb.AppendLine("=========================================");
            sb.AppendLine();
        }

        // b) NPC 事实段：scope==npcName 且 Category!=Behavior
        var facts = _rules
            .Where(e => string.Equals(e.NpcName, npcName, StringComparison.Ordinal) &&
                        e.Category != MemoryCategory.Behavior)
            .OrderByDescending(e => e.CreatedDay)
            .ThenByDescending(e => e.CreatedAt)
            .ToList();

        if (facts.Count > 0)
        {
            sb.AppendLine("<shared_lore>");
            foreach (var f in facts)
                sb.AppendLine($"- {f.Content}");
            sb.AppendLine("</shared_lore>");
            sb.AppendLine();
        }

        // c) 全局段：scope=="WORLD" 的全部规则
        var worldRules = _rules
            .Where(e => string.Equals(e.NpcName, "WORLD", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(e => e.CreatedAt)
            .ToList();

        if (worldRules.Count > 0)
        {
            sb.AppendLine(isZh
                ? "=== 全局世界观与小镇既定事实 ==="
                : "=== GLOBAL WORLD LORE & FACTS ===");
            sb.AppendLine(isZh
                ? "以下是鹈鹕镇当前公认的既定事实。请将这些事实融入你的日常认知中："
                : "These are established facts about the valley. Treat them as natural, universal knowledge:");
            sb.AppendLine();
            foreach (var e in worldRules)
                sb.AppendLine($"- [WORLD] {e.Content}");
            sb.AppendLine("=========================================");
        }

        return sb.ToString().TrimEnd();
    }

    // ──────────────────────────────────────────────────────────────
    // 工具方法（SmartTruncate 复制为私有实现，不跨类调用 internal 之外的东西）
    // ──────────────────────────────────────────────────────────────

    private static string SmartTruncate(string content, int maxLen)
    {
        if (string.IsNullOrEmpty(content) || content.Length <= maxLen) return content;

        int minLen = (int)(maxLen * 0.6);
        char[] endPunctuation = { '。', '！', '？', '!', '?', '.', '…' };

        for (int i = maxLen - 1; i >= minLen; i--)
        {
            if (content.Length <= i) continue;
            char c = content[i];
            if (endPunctuation.Contains(c))
                return content.Substring(0, i + 1);
        }

        return content.Substring(0, maxLen);
    }
}
