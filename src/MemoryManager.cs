using System;
using System.Collections.Generic;
using System.Linq;
using StardewModdingAPI;
using StardewValley;

namespace ValleytalkReborn;

/// <summary>
/// [FIX-3] 记忆分类枚举，让 Prompt 中的标签有实际数据支撑
/// </summary>
public enum MemoryCategory
{
    /// <summary>称呼习惯（如"叫我阿星"）</summary>
    Address,
    /// <summary>行为偏好（如"生气时请沉默"）</summary>
    Behavior,
    /// <summary>专属背景设定（如"我们曾在矿洞相遇"）</summary>
    Fact
}

public class MemoryEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string NpcName { get; set; } = "";
    public string Content { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public string Source { get; set; } = "Manual";

    /// <summary>
    /// [FIX-3] 记忆分类，默认为 Behavior（向后兼容旧存档）
    /// </summary>
    public MemoryCategory Category { get; set; } = MemoryCategory.Behavior;
}

/// <summary>
/// [FIX-5] 明确的操作结果枚举，替代 int? 的歧义返回值
/// </summary>
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

    private Dictionary<string, List<MemoryEntry>> _memories = new();
    private bool _isLoaded = false;

    private static bool IsChineseLanguage =>
        LocalizedContentManager.CurrentLanguageCode == LocalizedContentManager.LanguageCode.zh;

    private MemoryManager()
    {
        // 不在构造函数里注册事件，改由 ModEntry.Entry() 显式调用 Initialize
    }

    public void Initialize(IModHelper helper)
    {
        // 先取消防止重复注册（Cleanup → Entry 的重入场景）
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
            ModEntry.SMonitor?.Log("[MemoryManager] Cleaned up successfully.", LogLevel.Debug);
        }
        catch (Exception ex)
        {
            ModEntry.SMonitor?.Log($"[MemoryManager] Error during cleanup: {ex.Message}", LogLevel.Warn);
        }
    }

    public int GetMaxMemoryLength() => MaxMemoryLength;

    private void OnSaveLoaded(object sender, StardewModdingAPI.Events.SaveLoadedEventArgs e) => Load();

    /// <summary>
    /// [FIX-2] 以文件内容中的 NpcName 为准，增加空值校验
    /// </summary>
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

                // [FIX-2] 以文件内容中第一条记录的 NpcName 为准
                string actualNpcName = list.First().NpcName;
                if (string.IsNullOrWhiteSpace(actualNpcName))
                {
                    ModEntry.SMonitor?.Log(
                        $"[MemoryManager] Skipping {fileName}: NpcName is empty in file content.",
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

    /// <summary>
    /// [FIX-1] 统一使用 Data API，不再混用 File.Delete
    /// </summary>
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

    /// <summary>
    /// [FIX-5] 返回明确枚举
    /// </summary>
    public MemoryOperationResult AddMemory(string npcName, string content,
        MemoryCategory category = MemoryCategory.Behavior)
    {
        if (string.IsNullOrWhiteSpace(npcName) || string.IsNullOrWhiteSpace(content))
            return MemoryOperationResult.NotFound;

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

    /// <summary>
    /// [FIX-5] 返回明确枚举
    /// </summary>
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

    public string GetSmartMemoryContext(string npcName, int maxCount = MaxMemoriesInPrompt)
    {
        var entries = GetMemories(npcName);
        if (entries.Count == 0) return "";

        bool isZh = IsChineseLanguage;
        var selected = entries.Take(maxCount).ToList();
        var sb = new System.Text.StringBuilder();

        if (isZh)
        {
            sb.AppendLine("=== 玩家自定义规则与专属设定 ===");
            sb.AppendLine("请将以下约定自然融汇于你的角色扮演与表达习惯中：");
            sb.AppendLine();
            foreach (var e in selected)
                sb.AppendLine($"- {e.Content}");
            sb.AppendLine("=========================================");
        }
        else
        {
            sb.AppendLine("=== USER-DEFINED HIGH-PRIORITY RULES ===");
            sb.AppendLine("Seamlessly integrate these custom guidelines into your ongoing persona and speech habits:");
            sb.AppendLine();
            foreach (var e in selected)
                sb.AppendLine($"- {e.Content}");
            sb.AppendLine("=========================================");
        }

        return sb.ToString();
    }
}