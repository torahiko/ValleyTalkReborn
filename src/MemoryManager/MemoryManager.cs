using System;
using System.Collections.Generic;
using System.Linq;
using StardewModdingAPI;
using StardewValley;

namespace ValleytalkReborn;

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

    public const int MaxMemoryLength = 60;
    public const int MaxMemoriesPerNpc = 10;
    public const int MaxMemoriesInPrompt = 5;
    public const int MaxAutoInPrompt = 3;

    private Dictionary<string, List<MemoryEntry>> _memories = new();
    private bool _isLoaded = false;

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

        if (string.IsNullOrWhiteSpace(Constants.SaveFolderName) || ModEntry.SHelper == null) return;

        string saveDir = $"data/{Constants.SaveFolderName}";

        try
        {
            string physicalDir = System.IO.Path.Combine(ModEntry.SHelper.DirectoryPath, saveDir);

            if (!System.IO.Directory.Exists(physicalDir))
            {
                _isLoaded = true;
                return;
            }

            var files = System.IO.Directory.GetFiles(physicalDir, "memory_*.json");

            foreach (var file in files)
            {
                string fileName = System.IO.Path.GetFileName(file);
                string relativePath = $"{saveDir}/{fileName}";

                var list = ModEntry.SHelper.Data.ReadJsonFile<List<MemoryEntry>>(relativePath);
                if (list == null || list.Count == 0) continue;

                string actualNpcName = list.First().NpcName;

                if (string.IsNullOrWhiteSpace(actualNpcName))
                {
                    ModEntry.SMonitor?.Log(
                        $"[MemoryManager] Skipping {fileName}: NpcName is empty.",
                        LogLevel.Warn);
                    continue;
                }

                _memories[actualNpcName] = list;
            }

            _isLoaded = true;
        }
        catch (Exception ex)
        {
            ModEntry.SMonitor?.Log($"[MemoryManager] Load failed: {ex.Message}", LogLevel.Warn);
        }
    }

    public void Save(string npcName)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(Constants.SaveFolderName) || ModEntry.SHelper == null) return;

            string path = $"data/{Constants.SaveFolderName}/memory_{npcName}.json";

            if (!_memories.TryGetValue(npcName, out var list) || list.Count == 0)
            {
                ModEntry.SHelper.Data.WriteJsonFile(path, new List<MemoryEntry>());
            }
            else
            {
                ModEntry.SHelper.Data.WriteJsonFile(path, list);
            }
        }
        catch (Exception ex)
        {
            ModEntry.SMonitor?.Log($"[MemoryManager] Save failed for {npcName}: {ex.Message}", LogLevel.Warn);
        }
    }

    public void SaveAll()
    {
        foreach (var npcName in _memories.Keys.ToList())
            Save(npcName);
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

        if (list.Count >= MaxMemoriesPerNpc)
            return MemoryOperationResult.CapacityFull;

        var trimmedContent = content.Trim();

        if (trimmedContent.Length > MaxMemoryLength)
            return MemoryOperationResult.TooLong;

        if (list.Any(m => string.Equals(m.Content, trimmedContent, StringComparison.OrdinalIgnoreCase)))
            return MemoryOperationResult.Duplicate;

        list.Insert(0, new MemoryEntry
        {
            NpcName = npcName,
            Content = trimmedContent,
            CreatedAt = DateTime.Now,
            Source = "Manual",
            Category = category
        });

        Save(npcName);
        return MemoryOperationResult.Success;
    }

    // ──────────────────────────────────────────────────────────────
    // 🌟 自动添加（LLM 夜间提取）
    // 手动记忆不滚动，自动记忆只替换自动记忆
    // 手动满 10 条则该 NPC 禁用自动提取
    // ──────────────────────────────────────────────────────────────
    public MemoryOperationResult AddAutoMemory(string npcName, string content,
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

        int manualCount = list.Count(m => m.Source == "Manual");

        // 玩家手动记忆已满 → 该 NPC 不启用自动记忆
        if (manualCount >= MaxMemoriesPerNpc)
            return MemoryOperationResult.CapacityFull;

        var trimmed = content.Trim();

        if (trimmed.Length > MaxMemoryLength)
            return MemoryOperationResult.TooLong;

        if (list.Any(m => string.Equals(m.Content, trimmed, StringComparison.OrdinalIgnoreCase)))
            return MemoryOperationResult.Duplicate;

        int autoCapacity = MaxMemoriesPerNpc - manualCount;

        var autoEntries = list
            .Where(m => m.Source == "Auto")
            .OrderBy(m => m.CreatedAt)
            .ToList();

        // 自动记忆满了 → 只替换最旧的自动记忆
        if (autoEntries.Count >= autoCapacity)
        {
            var oldest = autoEntries.First();
            list.Remove(oldest);

            ModEntry.SMonitor?.Log(
                $"[MemoryManager] Auto-replaced oldest auto memory for [{npcName}]: \"{oldest.Content}\"",
                LogLevel.Debug);
        }

        list.Insert(0, new MemoryEntry
        {
            NpcName = npcName,
            Content = trimmed,
            CreatedAt = DateTime.Now,
            Source = "Auto",
            Category = category
        });

        Save(npcName);

        ModEntry.SMonitor?.Log(
            $"[MemoryManager] +AutoMemory [{npcName}]: \"{trimmed}\"",
            LogLevel.Info);

        return MemoryOperationResult.Success;
    }

    public MemoryOperationResult EditMemory(string npcName, string id, string newContent,
        MemoryCategory? category = null)
    {
        if (!_memories.TryGetValue(npcName, out var list))
            return MemoryOperationResult.NotFound;

        var entry = list.FirstOrDefault(m => m.Id == id);
        if (entry == null)
            return MemoryOperationResult.NotFound;

        var trimmed = newContent.Trim();

        if (trimmed.Length > MaxMemoryLength)
            return MemoryOperationResult.TooLong;

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

    public int GetMemoryCount(string npcName)
    {
        EnsureLoaded();
        return _memories.TryGetValue(npcName, out var list) ? list.Count : 0;
    }

    // ──────────────────────────────────────────────────────────────
    // 🌟 Prompt 注入：手动规则（高优先级）+ 自动事实（低优先级）
    // 含情境触发判定
    // ──────────────────────────────────────────────────────────────
    public string GetSmartMemoryContext(string npcName, int maxCount = MaxMemoriesInPrompt)
    {
        EnsureLoaded();

        if (!_memories.TryGetValue(npcName, out var list) || list.Count == 0)
            return "";

        bool isZh = IsChineseLanguage;

        var manualEntries = list
            .Where(m => m.Source == "Manual")
            .OrderByDescending(m => m.CreatedAt)
            .Take(maxCount)
            .ToList();

        var autoEntries = list
            .Where(m => m.Source == "Auto")
            .OrderByDescending(m => m.CreatedAt)
            .Take(MaxAutoInPrompt)
            .ToList();

        if (manualEntries.Count == 0 && autoEntries.Count == 0)
            return "";

        var sb = new System.Text.StringBuilder();

        // ── 手动规则（最高优先级）──
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

        // ── 自动事实（背景参考，低优先级）──
        if (autoEntries.Count > 0)
        {
            string currentLocation = Game1.player?.currentLocation?.Name ?? "";

            sb.AppendLine(isZh
                ? "=== 自动记录的近期事实与约定（背景参考）==="
                : "=== AUTO-RECORDED RECENT FACTS & PROMISES (Background) ===");
            sb.AppendLine(isZh
                ? "以下是系统自动整理的背景信息，仅作参考。若与玩家手动规则冲突，以玩家手动规则为准："
                : "Auto-recorded background facts. If conflicts with player rules, player rules take priority:");
            sb.AppendLine();

            foreach (var e in autoEntries)
            {
                bool isRelevant = IsContextuallyRelevant(e.Content, currentLocation);

                if (isRelevant)
                {
                    sb.AppendLine(isZh
                        ? $"- [!!! 当前情境触发 !!!] {e.Content} (你们现在刚好处于这个情境，请自然提及这个约定或事实！)"
                        : $"- [!!! CONTEXT TRIGGER !!!] {e.Content} (You are currently in this exact situation. Naturally bring up this promise or fact!)");
                }
                else
                {
                    sb.AppendLine($"- {e.Content}");
                }
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

        // 地点别名表（英文 key → 中文别名）
        var locationAliases = new Dictionary<string, string[]>
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

        foreach (var kvp in locationAliases)
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
}